using Godot;

namespace OpenNV.Runtime.Presentation.Rendering;

internal partial class RuntimeNativePrecipitation : Node3D
{
    internal enum Kind { None, Rain, Snow, Dust }

    private Func<string> _weather = null!;
    private Func<Vector3> _anchor = null!;
    private GpuParticles3D? _particles;
    private Kind _kind = Kind.None;
    private string _lastWeather = "";

    internal void Configure(Func<string> weather, Func<Vector3> anchor)
    {
        _weather = weather;
        _anchor = anchor;
    }

    public override void _Ready()
    {
        _particles = new GpuParticles3D
        {
            Name = "WeatherPrecipitation",
            Amount = 1600,
            Lifetime = 1.4,
            Preprocess = 1.0,
            Explosiveness = 0.0f,
            LocalCoords = false,
            VisibilityAabb = new Aabb(new Vector3(-48, -48, -48), new Vector3(96, 96, 96)),
            Emitting = false,
        };
        AddChild(_particles);
        Apply(Kind.Rain);
        _particles.Emitting = false;
    }

    public override void _Process(double delta)
    {
        if (_particles is null) return;
        var weather = _weather?.Invoke() ?? "";
        if (!string.Equals(weather, _lastWeather, StringComparison.OrdinalIgnoreCase))
        {
            _lastWeather = weather;
            Apply(Classify(weather));
        }
        GlobalPosition = _anchor.Invoke() + new Vector3(0, 14, 0);
    }

    internal static Kind Classify(string editorId)
    {
        if (string.IsNullOrWhiteSpace(editorId)) return Kind.None;
        var id = editorId.ToLowerInvariant();
        if (id.Contains("rain")) return Kind.Rain;
        if (id.Contains("snow") || id.Contains("jacobstown")) return Kind.Snow;
        if (id.Contains("dust") || id.Contains("storm")) return Kind.Dust;
        return Kind.None;
    }

    private void Apply(Kind kind)
    {
        _kind = kind;
        if (_particles is null) return;
        _particles.Emitting = kind != Kind.None;
        GD.Print($"OPENNV_NATIVE_PRECIP kind={kind} at={GlobalPosition}");
        if (kind == Kind.None) return;
        _particles.Amount = kind switch { Kind.Rain => 3200, Kind.Snow => 1800, _ => 4000 };
        _particles.Lifetime = kind switch { Kind.Rain => 1.1, Kind.Snow => 9.0, _ => 2.4 };
        _particles.ProcessMaterial = BuildProcess(kind);
        _particles.DrawPass1 = BuildMesh(kind);
    }

    private static ParticleProcessMaterial BuildProcess(Kind kind)
    {
        var material = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            Color = Colors.White,
        };
        switch (kind)
        {
            case Kind.Rain:
                material.EmissionBoxExtents = new Vector3(30, 14, 30);
                material.Direction = new Vector3(0.10f, -1, 0);
                material.Spread = 3;
                material.InitialVelocityMin = 18;
                material.InitialVelocityMax = 26;
                material.Gravity = new Vector3(0, -20, 0);
                material.ScaleMin = 0.6f;
                material.ScaleMax = 1.4f;
                break;
            case Kind.Snow:
                material.EmissionBoxExtents = new Vector3(26, 16, 26);
                material.Direction = new Vector3(0, -1, 0);
                material.Spread = 20;
                material.InitialVelocityMin = 0.8f;
                material.InitialVelocityMax = 2.0f;
                material.Gravity = new Vector3(0, -0.6f, 0);
                material.TurbulenceEnabled = true;
                material.TurbulenceNoiseStrength = 2.5f;
                material.ScaleMin = 0.5f;
                material.ScaleMax = 1.3f;
                break;
            default:
                material.EmissionBoxExtents = new Vector3(44, 18, 44);
                material.Direction = new Vector3(1, -0.06f, 0);
                material.Spread = 18;
                material.InitialVelocityMin = 12;
                material.InitialVelocityMax = 20;
                material.Gravity = new Vector3(0, -0.3f, 0);
                material.ScaleMin = 0.7f;
                material.ScaleMax = 1.6f;
                material.TurbulenceEnabled = true;
                material.TurbulenceNoiseStrength = 0.6f;
                break;
        }
        return material;
    }

    private static Mesh BuildMesh(Kind kind)
    {
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = kind switch
            {
                Kind.Rain => new Color(0.70f, 0.80f, 0.95f, 0.45f),
                Kind.Snow => new Color(1.0f, 1.0f, 1.0f, 0.90f),
                _ => new Color(0.70f, 0.58f, 0.44f, 0.50f),
            },
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = kind == Kind.Snow
                ? BaseMaterial3D.BillboardModeEnum.Disabled
                : BaseMaterial3D.BillboardModeEnum.Enabled,
        };
        if (kind == Kind.Snow)
            return new SphereMesh { Radius = 0.03f, Height = 0.06f, RadialSegments = 4, Rings = 2, Material = material };
        return new BoxMesh
        {
            Size = kind == Kind.Rain ? new Vector3(0.012f, 0.55f, 0.012f) : new Vector3(0.045f, 0.045f, 0.45f),
            Material = material,
        };
    }
}
