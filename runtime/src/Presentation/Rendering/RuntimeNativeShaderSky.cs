using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Formats.Gamebryo;

namespace OpenNV.Runtime.Presentation.Rendering;

internal partial class RuntimeNativeShaderSky : Node3D
{
    private readonly record struct Palette(
        Color Top, Color Bottom, Color Haze, Color Cloud, Color Shadow,
        float Coverage, float Brightness, float Softness, float Speed, float Glow, float Scale);

    private static readonly Color NightTop = new(0.025f, 0.035f, 0.080f);
    private static readonly Color NightBottom = new(0.065f, 0.085f, 0.150f);
    private static readonly Color NightHaze = new(0.050f, 0.060f, 0.120f);
    private static readonly Color NightCloud = new(0.060f, 0.070f, 0.100f);
    private static readonly Color NightShadow = new(0.020f, 0.025f, 0.040f);
    private static readonly Color DuskTop = new(0.180f, 0.160f, 0.320f);
    private static readonly Color DuskBottom = new(0.850f, 0.420f, 0.220f);
    private static readonly Color DuskHaze = new(0.900f, 0.550f, 0.350f);
    private static readonly Color DuskCloud = new(1.000f, 0.720f, 0.550f);
    private static readonly Color DuskShadow = new(0.220f, 0.160f, 0.160f);

    private Func<Vector3>? _anchor;
    private Func<string>? _weather;
    private Func<Vector3>? _sun;
    private ShaderMaterial? _material;
    private MeshInstance3D? _sphere;
    private float _sphereRadius;
    private string _lastWeather = "";
    private Palette _palette = DefaultPalette();
    private float _cirrusCoverage = 0.25f;
    private float _cirrusOpacity = 0.18f;
    private float _coverageCap = 0.55f;
    private bool _denseWeather;
    private int _debugFrames;

    private static Palette DefaultPalette() => new(
        new Color(0.10f, 0.28f, 0.62f), new Color(0.52f, 0.68f, 0.84f), new Color(0.72f, 0.78f, 0.86f),
        new Color(0.92f, 0.94f, 0.98f), new Color(0.45f, 0.50f, 0.60f),
        0.52f, 1.0f, 0.35f, 0.25f, 0.6f, 4.5f);

    internal void Configure(Func<Vector3> anchor, float units, Func<string> weather, Func<Vector3> sun,
        int cloudOctaves = 5, float starDensity = 1.5f)
    {
        _anchor = anchor;
        _weather = weather;
        _sun = sun;
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/sky_dome.gdshader") };
        _material.SetShaderParameter("star_intensity", 1.0f);
        _material.SetShaderParameter("star_density", starDensity);
        _material.SetShaderParameter("cloud_octaves", Math.Clamp(cloudOctaves, 1, 5));
        _material.SetShaderParameter("wind_direction", new Vector2(0.85f, 0.28f));
        // Equirect star panorama first. The old night_sky.png is a 512x512
        // tangent tile; mapped as a 360x180 panorama its texels are ~0.7 deg
        // wide, so every star smears into a multi-pixel blob.
        var starBase = "/home/bazzite/opennv/runtime/assets/env";
        var starPng = System.IO.File.Exists($"{starBase}/night_sky_equirect.png")
            ? $"{starBase}/night_sky_equirect.png"
            : $"{starBase}/night_sky.png";
        if (System.IO.File.Exists(starPng))
        {
            var starImage = Image.LoadFromFile(starPng);
            if (starImage is not null)
            {
                _material.SetShaderParameter("star_field", ImageTexture.CreateFromImage(starImage));
                _material.SetShaderParameter("use_star_field", true);
                GD.Print($"OPENNV_NATIVE_STAR_FIELD source={starPng} size={starImage.GetWidth()}x{starImage.GetHeight()}");
            }
        }
        _sphere = new MeshInstance3D
        {
            Name = "ShaderSkySphere",
            Mesh = new SphereMesh
            {
                Radius = 1.0f,
                Height = 2.0f,
                RadialSegments = 128,
                Rings = 64,
            },
            Scale = new Vector3(190.0f, 190.0f, 190.0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = _material,
        };
        _sphereRadius = 190.0f;
        AddChild(_sphere);
        ApplyWeather("");
    }

    public override void _Process(double delta)
    {
        if (_anchor is not null) GlobalPosition = _anchor.Invoke();
        if (_sphere is not null)
        {
            // A finite dome inside the camera's near field paints over any
            // geometry farther away (the old 190 m radius clipped the world to
            // the foreground). Park the shell just inside the far plane so it
            // can never be nearer than visible terrain; the shader is
            // direction-driven, so the visual result is unchanged.
            var camera = GetViewport()?.GetCamera3D();
            var radius = camera is not null ? Mathf.Max(1.0f, camera.Far * 0.95f) : 190.0f;
            if (Mathf.Abs(radius - _sphereRadius) > 1.0f)
            {
                _sphereRadius = radius;
                _sphere.Scale = new Vector3(radius, radius, radius);
                GD.Print($"OPENNV_SKY_DOME_RADIUS radius={radius:F0} cameraFar={camera?.Far:F0}");
            }
        }
        if (_material is null) return;
        if (_weather is not null)
        {
            var id = _weather.Invoke() ?? "";
            if (!string.Equals(id, _lastWeather, StringComparison.OrdinalIgnoreCase))
            {
                _lastWeather = id;
                ApplyWeather(id);
            }
        }

        var sun = (_sun?.Invoke() ?? Vector3.Up).Normalized();
        var sunY = sun.Y;
        var day = Mathf.Clamp((sunY + 0.05f) / 0.30f, 0.0f, 1.0f);
        var night = Mathf.Clamp((-sunY - 0.02f) / 0.20f, 0.0f, 1.0f);
        var dusk = Mathf.Exp(-Mathf.Abs(sunY) * 9.0f);

        var top = _palette.Top.Lerp(NightTop, night).Lerp(DuskTop, dusk * 0.7f);
        var bottom = _palette.Bottom.Lerp(NightBottom, night).Lerp(DuskBottom, dusk * 0.7f);
        var haze = _palette.Haze.Lerp(NightHaze, night).Lerp(DuskHaze, dusk * 0.7f);
        var cloud = _palette.Cloud.Lerp(NightCloud, night).Lerp(DuskCloud, dusk * 0.5f);
        var shadow = _palette.Shadow.Lerp(NightShadow, night).Lerp(DuskShadow, dusk * 0.4f);

        _material.SetShaderParameter("top_color", top);
        _material.SetShaderParameter("bottom_color", bottom);
        _material.SetShaderParameter("haze_color", haze);
        _material.SetShaderParameter("cloud_color", new Color(cloud.R, cloud.G, cloud.B, 1.0f));
        _material.SetShaderParameter("cloud_shadow_color", shadow);
        _material.SetShaderParameter("sun_direction", sun);
        _material.SetShaderParameter("sun_glow", _palette.Glow * (0.05f + 0.95f * day) + dusk * 0.5f);
        // Midday clear/cloudy skies read thin and feathery (cirrus), not puffy;
        // dawn/dusk keeps the sparse authored shape. Rain and storm weather is
        // the exception: it keeps a dense sheet at every hour and the cap is
        // lifted so it can actually look like weather.
        var wispy = _denseWeather ? 0.0f : Mathf.Clamp(day * (1.0f - dusk), 0.0f, 1.0f);
        var coverage = Mathf.Min(Mathf.Lerp(_palette.Coverage, 0.12f, wispy), _coverageCap);
        _material.SetShaderParameter("cloud_coverage", coverage);
        // Extra large-scale domain warp only when the sheet is dense; sparse
        // feathery clouds keep their exact silhouette, dense sheets lose the
        // repeating FBM tile.
        var dense = Mathf.Clamp((coverage - 0.50f) / 0.35f, 0.0f, 1.0f);
        _material.SetShaderParameter("cloud_warp", 1.0f + 1.6f * dense);
        _material.SetShaderParameter("cloud_brightness", _palette.Brightness * (0.18f + 0.82f * day + 0.45f * dusk));
        _material.SetShaderParameter("cloud_softness", Mathf.Lerp(_palette.Softness, 0.92f, wispy));
        _material.SetShaderParameter("cloud_sharpness", Mathf.Lerp(0.16f, 0.03f, wispy));
        _material.SetShaderParameter("cloud_speed", _palette.Speed);
        _material.SetShaderParameter("cloud_scale", _palette.Scale);
        _material.SetShaderParameter("cirrus_coverage", Mathf.Lerp(_cirrusCoverage, Mathf.Max(_cirrusCoverage, 0.70f), wispy));
        _material.SetShaderParameter("cirrus_opacity", Mathf.Lerp(_cirrusOpacity, Mathf.Max(_cirrusOpacity, 1.0f), wispy));
        var star = night * (1.0f - Mathf.Clamp(_palette.Coverage, 0.0f, 0.85f));
        _material.SetShaderParameter("star_intensity", star * 1.2f);
    }

    private void ApplyWeather(string id)
    {
        if (_material is null) return;
        var key = id.ToLowerInvariant();
        _palette = key switch
        {
            var k when k.Contains("rain") || k.Contains("storm") => new Palette(
                new Color(0.16f, 0.19f, 0.25f), new Color(0.40f, 0.45f, 0.52f), new Color(0.52f, 0.57f, 0.62f),
                new Color(0.60f, 0.63f, 0.69f), new Color(0.28f, 0.31f, 0.37f),
                0.80f, 0.72f, 0.65f, 0.30f, 0.15f, 3.8f),
            var k when k.Contains("snow") || k.Contains("jacobstown") => new Palette(
                new Color(0.38f, 0.47f, 0.62f), new Color(0.72f, 0.78f, 0.86f), new Color(0.82f, 0.86f, 0.92f),
                new Color(0.90f, 0.93f, 0.97f), new Color(0.58f, 0.62f, 0.70f),
                0.70f, 0.85f, 0.55f, 0.20f, 0.25f, 4.2f),
            var k when k.Contains("dust") || k.Contains("hazy") || k.Contains("searchlight") => new Palette(
                new Color(0.34f, 0.29f, 0.22f), new Color(0.72f, 0.64f, 0.52f), new Color(0.80f, 0.70f, 0.55f),
                new Color(0.82f, 0.74f, 0.62f), new Color(0.45f, 0.40f, 0.33f),
                0.50f, 0.85f, 0.60f, 0.20f, 0.35f, 4.5f),
            var k when k.Contains("clear") => new Palette(
                new Color(0.10f, 0.28f, 0.62f), new Color(0.52f, 0.68f, 0.84f), new Color(0.72f, 0.78f, 0.86f),
                new Color(0.92f, 0.94f, 0.98f), new Color(0.45f, 0.50f, 0.60f),
                0.60f, 1.0f, 0.60f, 0.22f, 0.6f, 3.8f),
            _ => DefaultPalette(),
        };
        var (cirrusCoverage, cirrusOpacity) = key switch
        {
            var k when k.Contains("rain") || k.Contains("storm") => (0.55f, 0.30f),
            var k when k.Contains("snow") || k.Contains("jacobstown") => (0.35f, 0.25f),
            var k when k.Contains("dust") || k.Contains("hazy") || k.Contains("searchlight") => (0.45f, 0.30f),
            var k when k.Contains("clear") => (0.18f, 0.14f),
            _ => (0.25f, 0.18f),
        };
        _material.SetShaderParameter("cirrus_coverage", cirrusCoverage);
        _material.SetShaderParameter("cirrus_opacity", cirrusOpacity);
        _cirrusCoverage = cirrusCoverage;
        _cirrusOpacity = cirrusOpacity;
        // Sparse weather never exceeds the cap, so a clear day cannot saturate
        // into the tiled FBM pattern. Dense sheets are reserved for weather
        // that is supposed to be heavy.
        _denseWeather = key.Contains("rain") || key.Contains("storm");
        _coverageCap = _denseWeather ? 0.92f
            : key.Contains("snow") || key.Contains("jacobstown") ? 0.75f
            : key.Contains("dust") || key.Contains("hazy") || key.Contains("searchlight") ? 0.60f
            : key.Contains("clear") ? 0.45f
            : 0.55f;
        GD.Print($"OPENNV_NATIVE_SHADER_SKY weather={id} coverage={_palette.Coverage:F2} cap={_coverageCap:F2} dense={_denseWeather}");
    }
}
