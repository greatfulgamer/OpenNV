using System.Buffers.Binary;
using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Formats.Gamebryo;
using OpenNV.Runtime.Gameplay.State;

namespace OpenNV.Runtime.Presentation.Rendering;

internal partial class RuntimeNativeExteriorEnvironment : Node
{
    private FalloutSkyLightingState _sky = null!;
    private FalloutPluginStack _records = null!;
    private Func<float> _hour = null!;
    private float _units;
    private RuntimeNativeSkyLayers _layers = null!;
    private DirectionalLight3D _sun = null!;
    private Godot.Environment _environment = null!;
    private readonly HashSet<GeometryInstance3D> _meshes = [];
    private Node _scene = null!;
    private Node? _sceneRoot;
    private SceneTree? _tree;
    private float _lastHour = -1;
    private Vector3 _ambient, _fog, _range;
    private bool _lightingBound;
    private bool _underwater;
    private float _underwaterDepth;
    private bool _underwaterCaustics = true;
    private float _shadowDistance = 100;
    private float _fogScale = 1.0f;
    private float _fogFarOverrideGameUnits;
    private float _lastLoggedHour = float.NaN;
    private bool _useSkyShader;
    private bool _suppressSky;
    private string? _skyShaderPath;
    private ShaderMaterial? _skyMaterial;
    internal Compositor? Compositor { get; set; }
    internal FalloutImageSpace? ImageSpace { get; set; }
    internal int RegisteredSurfaces => _meshes.Count;
    internal object ShadowState => new
    {
        distance = _sun.DirectionalShadowMaxDistance,
        blendSplits = _sun.DirectionalShadowBlendSplits,
        fadeStart = _sun.DirectionalShadowFadeStart,
        mode = _sun.DirectionalShadowMode.ToString(),
        enabled = _sun.ShadowEnabled,
        rangeOwner = "resident-exterior-grid",
        boundary = "source-geometry-casters;retail-shadow-filtering-and-LOD-shadows-unmatched"
    };

    internal void Configure(FalloutPluginStack records, FalloutSkyLightingState sky, Func<float> hour, float units,
        Node? sceneRoot = null, float shadowDistance = 100, bool useSkyShader = false, string? skyShaderPath = null,
        bool suppressSky = false, bool underwaterCaustics = true, float fogScale = 1.0f,
        float fogFarOverrideGameUnits = 0.0f)
    {
        _useSkyShader = useSkyShader;
        _skyShaderPath = skyShaderPath;
        _suppressSky = suppressSky;
        _underwaterCaustics = underwaterCaustics;
        if (!float.IsFinite(shadowDistance) || shadowDistance <= 0)
            throw new InvalidDataException("Exterior shadow distance must cover a positive finite region.");
        if (!float.IsFinite(fogScale) || fogScale <= 0)
            throw new InvalidDataException("Exterior fog scale must be positive and finite.");
        if (!float.IsFinite(fogFarOverrideGameUnits) || fogFarOverrideGameUnits < 0)
            throw new InvalidDataException("Exterior fog far override must be non-negative and finite.");
        _fogScale = fogScale;
        _fogFarOverrideGameUnits = fogFarOverrideGameUnits;
        _records = records; _sky = sky; _hour = hour; _units = units; _sceneRoot = sceneRoot; _shadowDistance = shadowDistance;
    }

    internal void Register(Node root)
    {
        BindAdded(root);
        foreach (var mesh in root.FindChildren("*", "", true, false).OfType<GeometryInstance3D>()) BindAdded(mesh);
    }

    private void BindAdded(Node node)
    {
        if (node is not GeometryInstance3D mesh || !_scene.IsAncestorOf(mesh) || mesh.GetViewport() != _scene.GetViewport() || !_meshes.Add(mesh)) return;
        NativeNifMaterialEnvironment.BindExterior(mesh);
    }

    private void RemoveSurface(Node node) { if (node is GeometryInstance3D mesh) _meshes.Remove(mesh); }

    public override void _Ready()
    {
        _scene = _sceneRoot ?? GetParent(); _tree = GetTree();
        _tree.NodeAdded += BindAdded; _tree.NodeRemoved += RemoveSurface;
        _layers = new RuntimeNativeSkyLayers { Name = "OwnedSkyLayers" };
        if (!_useSkyShader && !_suppressSky)
        {
            _layers.Build(_records, _sky, _units);
            AddChild(_layers);
        }
        _environment = new Godot.Environment
        {
            BackgroundMode = _useSkyShader ? Godot.Environment.BGMode.Sky : Godot.Environment.BGMode.Color,
            BackgroundColor = Colors.Black,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightEnergy = 1,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
            FogEnabled = true,
            FogMode = Godot.Environment.FogModeEnum.Depth,
        };
        if (_useSkyShader)
        {
            _environment.BackgroundMode = Godot.Environment.BGMode.Sky;
            // Color, not Sky: sky radiance at full contribution floods every
            // surface with a flat wash at night (the "glow"). The authored
            // weather colour plus the night scale below owns ground lighting.
            _environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            _environment.AmbientLightEnergy = 1.0f;
            _environment.GlowEnabled = true;
            _environment.TonemapMode = Godot.Environment.ToneMapper.Aces;
            _environment.TonemapExposure = 0.20f;
            _environment.TonemapWhite = 1.0f;
            _skyMaterial = new ShaderMaterial
            {
                Shader = GD.Load<Shader>(_skyShaderPath ?? "res://shaders/sky_sorta.gdshader"),
            };
            if (_skyShaderPath is not null && _skyShaderPath.Contains("sky_simple"))
            {
                _skyMaterial.SetShaderParameter("top_color", new Color(0.12f, 0.34f, 0.68f));
                _skyMaterial.SetShaderParameter("bottom_color", new Color(0.55f, 0.70f, 0.84f));
                _skyMaterial.SetShaderParameter("cloud_color", new Color(1.0f, 1.0f, 1.0f, 1.0f));
                _skyMaterial.SetShaderParameter("cloud_speed", 0.6f);
                _skyMaterial.SetShaderParameter("cloud_coverage", 0.55f);
                _skyMaterial.SetShaderParameter("cloud_scale", 2.5f);
                _skyMaterial.SetShaderParameter("cloud_brightness", 1.4f);
                _skyMaterial.SetShaderParameter("cloud_sharpness", 0.25f);
                _skyMaterial.SetShaderParameter("horizon_glow", 0.4f);
                _skyMaterial.SetShaderParameter("enable_stars", false);
            }
            else
            {
                _skyMaterial.SetShaderParameter("use_debanding", true);
                _skyMaterial.SetShaderParameter("atmosphere_sample_count", 32);
                _skyMaterial.SetShaderParameter("elevation", 0.0f);
                _skyMaterial.SetShaderParameter("rayleigh_strength", 1.0f);
                _skyMaterial.SetShaderParameter("mie_strength", 1.0f);
                _skyMaterial.SetShaderParameter("exposure", 13.0f);
                _skyMaterial.SetShaderParameter("sundisc_intensity", 30.0f);
                _skyMaterial.SetShaderParameter("use_cirrus", false);
                _skyMaterial.SetShaderParameter("use_rainbow", false);
                _skyMaterial.SetShaderParameter("hole_in_center", false);
                _skyMaterial.SetShaderParameter("cloud_base_color", new Color(0.765f, 0.789f, 0.859f));
                _skyMaterial.SetShaderParameter("cloud_overcast_color", new Color(0.765f, 0.788f, 0.859f));
                _skyMaterial.SetShaderParameter("coverage", 0.35f);
                _skyMaterial.SetShaderParameter("cloud_smoothness", 0.05f);
                _skyMaterial.SetShaderParameter("cloud_base_march_dist", 10.0f);
                _skyMaterial.SetShaderParameter("cloud_marches", 32);
                _skyMaterial.SetShaderParameter("density_coeff", 1.5f);
                _skyMaterial.SetShaderParameter("cloud_noise_factor", 0.2f);
                _skyMaterial.SetShaderParameter("cloud_shape_size", 3.0f);
                _skyMaterial.SetShaderParameter("cloud_noise_size", 12.0f);
                _skyMaterial.SetShaderParameter("clouds_anisotropy_factor", 0.15f);
                _skyMaterial.SetShaderParameter("light_marches", 4);
                _skyMaterial.SetShaderParameter("light_max_dist", 5.0f);
                _skyMaterial.SetShaderParameter("light_strength", 25.0f);
                _skyMaterial.SetShaderParameter("clouds_alpha_lower_bound", 0.0f);
                _skyMaterial.SetShaderParameter("clouds_alpha_upper_bound", 0.15f);
                _skyMaterial.SetShaderParameter("cloud_shape", SkyNoise(2, 0.0231f, true, true));
                _skyMaterial.SetShaderParameter("cloud_noise", SkyNoise(2, 0.0285f, false, false));
            }
            _environment.Sky = new Sky
            {
                SkyMaterial = _skyMaterial,
                RadianceSize = Sky.RadianceSizeEnum.Size256,
                ProcessMode = Sky.ProcessModeEnum.Realtime,
            };
        }
        AddChild(new WorldEnvironment { Name = "OwnedExteriorEnvironment", Environment = _environment, Compositor = Compositor });
        _sun = new DirectionalLight3D
        {
            Name = "OwnedExteriorSun",
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            DirectionalShadowMaxDistance = _shadowDistance,
            DirectionalShadowBlendSplits = true
        };
        AddChild(_sun);
        Register(_scene);
        SetMeta("opennv_exterior_presentation_unverified", "clouds,celestial-projection,weather-transition,region-edge-blend,LOD,matched-pixels");
        Sample();
    }

    public override void _ExitTree()
    {
        if (_tree is not null) { _tree.NodeAdded -= BindAdded; _tree.NodeRemoved -= RemoveSurface; }
        _tree = null; _meshes.Clear();
    }

    public override void _Process(double delta)
    {
        if (Math.Abs(_hour() - _lastHour) > 1f / 3600) Sample();
    }

    private static NoiseTexture3D SkyNoise(int seed, float frequency, bool invert, bool cellular) => new()
    {
        Width = 128,
        Height = 128,
        Depth = 128,
        Seamless = true,
        Invert = invert,
        Noise = new FastNoiseLite
        {
            NoiseType = cellular ? FastNoiseLite.NoiseTypeEnum.Cellular : FastNoiseLite.NoiseTypeEnum.Simplex,
            Frequency = frequency,
            Seed = seed,
            FractalOctaves = 1,
        },
    };

    internal void SetUnderwater(bool underwater, float depthMeters)
    {
        depthMeters = Mathf.Max(0.0f, depthMeters);
        if (underwater == _underwater && Mathf.Abs(depthMeters - _underwaterDepth) < 1.0f) return;
        _underwater = underwater;
        _underwaterDepth = depthMeters;
        // Force the next Sample to republish retail fog after surfacing.
        _lightingBound = false;
        RenderingServer.GlobalShaderParameterSet("opennv_underwater_light", new Vector3(0.6f, 0.95f, 1.0f));
        var causticStrength = underwater && _underwaterCaustics
            ? Mathf.Clamp(1.10f - depthMeters * 0.015f, 0.45f, 1.10f)
            : 0.0f;
        RenderingServer.GlobalShaderParameterSet("opennv_underwater_strength", causticStrength);
        GD.Print($"OPENNV_UNDERWATER_CAUSTICS strength={causticStrength:F2} depth={depthMeters:F1} enabled={_underwaterCaustics}");
        Sample();
        SetMeta("opennv_underwater_fog", underwater);
    }

    private void Sample()
    {
        var hour = _lastHour = _hour();
        var weather = _sky.ActiveWeather;
        var weights = FalloutWeatherTimeWeights.Sample(_sky.Climate, hour, _sky.DaytimeExtension, weather.TimeSamples);
        Color ColorAt(int kind) { var rgb = weather.Sample(weights, kind); return new(rgb[0], rgb[1], rgb[2]); }
        var ambient = ColorAt(3); var fog = ColorAt(1);
        var night = (weights.First == 3 ? weights.FirstWeight : 0) + (weights.Second == 3 ? weights.SecondWeight : 0);
        // Night keeps the authored ambient for the sky, but the terrain is
        // emissive off that same colour and reads as daytime with the lights
        // dimmed. Scale the ground ambient toward night; the authored dark
        // fog then carries the distance falloff.
        var groundScale = Mathf.Lerp(1.0f, 0.40f, Mathf.Clamp(night, 0.0f, 1.0f));
        var groundAmbient = new Color(ambient.R * groundScale, ambient.G * groundScale, ambient.B * groundScale);
        if (!_useSkyShader && !_suppressSky) _layers.Sample(_records, weather, weights, ImageSpace?.RawTraits[8] ?? 1f);
        var solarAngle = (hour - 6) * MathF.PI / 12;
        var direction = new Vector3(MathF.Cos(solarAngle), MathF.Sin(solarAngle), 0).Normalized();
        _sun.Basis = RetailLighting.DirectionalLightBasis(direction);
        _sun.LightColor = RetailLighting.GodotLightColor(ColorAt(4));
        _sun.LightEnergy = _useSkyShader
            ? Mathf.Clamp((direction.Y + 0.09f) / 0.09f, 0.0f, 1.0f)
            : direction.Y > 0 ? ImageSpace?.RawTraits[11] ?? 1 : 0;
        // Below the horizon, mirror the set sun as a faint cool moon. Without
        // it the night land is a flat ambient wash with no directional
        // shading; the moon gives slopes a sweeping light-to-dark gradient.
        var nightGlow = Mathf.Clamp(night, 0.0f, 1.0f);
        if (direction.Y <= 0 && nightGlow > 0)
        {
            var moon = new Vector3(-direction.X, -direction.Y, -direction.Z).Normalized();
            _sun.Basis = RetailLighting.DirectionalLightBasis(moon);
            _sun.LightColor = new Color(0.42f, 0.50f, 0.72f);
            _sun.LightEnergy = 0.12f * nightGlow;
        }
        _environment.AmbientLightColor = groundAmbient;
        _environment.FogLightColor = fog;
        NativeNifMaterialEnvironment.PublishGrassLight(
            Mathf.Lerp(1.0f, 0.22f, Mathf.Clamp(night, 0.0f, 1.0f)));
        var declaration = _records.GetEffective(weather.Form).ReadSubrecords().Single(field => field.Signature == "FNAM").Data;
        if (declaration.Length != 24) throw new InvalidDataException("Exterior weather fog declaration requires six floats.");
        var values = new float[6];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = BinaryPrimitives.ReadSingleLittleEndian(declaration.Span[(index * 4)..]);
            if (!float.IsFinite(values[index])) throw new InvalidDataException("Weather fog is non-finite.");
        }
        var near = Mathf.Lerp(values[0], values[2], night);
        var far = Mathf.Lerp(values[1], values[3], night);
        var power = Mathf.Lerp(values[4], values[5], night);
        // Authored fog far is much shorter than the loaded LOD radius, so a
        // clear-day valley reads as a white wall. --fog-follow-lod parks the
        // fog end just inside the LOD edge (the visible range tracks the
        // loaded world for every weather); --fog-scale is a fixed multiplier.
        var ratio = _fogFarOverrideGameUnits > 0 ? _fogFarOverrideGameUnits / far : _fogScale;
        var nightFog = Mathf.Lerp(1.0f, 0.55f, Mathf.Clamp(night, 0.0f, 1.0f));
        if (float.IsNaN(_lastLoggedHour) || Mathf.Abs(hour - _lastLoggedHour) >= 0.25f)
        {
            _lastLoggedHour = hour;
            GD.Print($"OPENNV_EXTERIOR_FOG nearGameUnits={near:F0} farGameUnits={far:F0} power={power:F3} " +
                $"ratio={ratio:F2} nightFog={nightFog:F2} farMeters={far * ratio * nightFog * _units:F0} hour={hour:F2} " +
                $"ambient=({ambient.R:F3},{ambient.G:F3},{ambient.B:F3}) ground=({groundAmbient.R:F3},{groundAmbient.G:F3},{groundAmbient.B:F3}) " +
                $"fog=({fog.R:F3},{fog.G:F3},{fog.B:F3}) sun={_sun.LightEnergy:F3}");
        }
        near *= ratio; far *= ratio;
        // Night distance gradient: the authored/LOD fog end is horizon-scale
        // (~3.4 km), which keeps the whole valley visible at 23:00. Pull the
        // interval in at night so the land sweeps to the near-black fog colour
        // instead of ending in a fully-lit diorama.
        near *= nightFog; far *= nightFog;
        if (far <= near || power <= 0) throw new InvalidDataException("Weather fog interval is invalid.");
        _environment.FogDepthBegin = Math.Max(0, near * _units);
        _environment.FogDepthEnd = far * _units;
        _environment.FogDepthCurve = power;
        if (_underwater)
        {
            var depthMeters = Mathf.Clamp(_underwaterDepth, 0.0f, 250.0f);
            _environment.FogLightColor = new Color(0.02f, 0.13f, 0.16f);
            _environment.FogDepthBegin = 0.0f;
            _environment.FogDepthEnd = Mathf.Clamp(22.0f - depthMeters * 0.25f, 5.0f, 22.0f);
            _environment.FogDepthCurve = 1.4f;
        }
        var nextAmbient = new Vector3(groundAmbient.R, groundAmbient.G, groundAmbient.B);
        var nextFog = new Vector3(fog.R, fog.G, fog.B);
        var nextRange = new Vector3(near, far, power);
        if (!_lightingBound || _ambient != nextAmbient || _fog != nextFog || _range != nextRange)
        {
            _ambient = nextAmbient; _fog = nextFog; _range = nextRange; _lightingBound = true;
            NativeNifMaterialEnvironment.PublishExterior(_ambient, _fog, _range, _units);
        }
        if (_underwater)
        {
            // Retail lighting materials read near/far/power in game units.
            var depthMeters = Mathf.Clamp(_underwaterDepth, 0.0f, 250.0f);
            var farGameUnits = Mathf.Clamp((22.0f - depthMeters * 0.25f) / _units, 300.0f, 2200.0f);
            NativeNifMaterialEnvironment.PublishExterior(_ambient,
                new Vector3(0.02f, 0.13f, 0.16f),
                new Vector3(0.0f, farGameUnits, 1.4f), _units);
        }
        SetMeta("opennv_source_weather", weather.Form.ToString());
    }
}
