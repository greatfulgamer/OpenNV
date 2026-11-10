using Godot;

namespace OpenNV.Runtime.Presentation.Rendering;

internal partial class RuntimeNativeUnderwater : CanvasLayer
{
    private ColorRect? _tint;
    private ColorRect? _light;
    private ShaderMaterial? _material;
    private ShaderMaterial? _lightMaterial;
    private Func<Vector3>? _camera;
    private Func<Vector3>? _sun;
    private Func<Vector3, float?>? _heightAt;
    private Action<bool>? _underwaterChanged;
    private Action<bool, float>? _underwaterState;
    private float _units = 1.0f;
    private bool _wasUnderwater;

    internal void Configure(Func<Vector3> camera, Func<Vector3, float?> heightAt, float units,
        Action<bool>? underwaterChanged = null, Action<bool, float>? underwaterState = null,
        Func<Vector3>? sun = null)
    {
        _camera = camera;
        _heightAt = heightAt;
        _underwaterChanged = underwaterChanged;
        _underwaterState = underwaterState;
        _sun = sun;
        _units = units;
    }

    public override void _Ready()
    {
        Layer = 50;
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/underwater.gdshader") };
        _tint = new ColorRect
        {
            Name = "UnderwaterTint",
            Color = Colors.White,
            Material = _material,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _tint.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_tint);
        _lightMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/underwater_light.gdshader") };
        _light = new ColorRect
        {
            Name = "UnderwaterLight",
            Color = Colors.White,
            Material = _lightMaterial,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _light.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_light);
    }

    public override void _Process(double delta)
    {
        if (_tint is null || _camera is null || _heightAt is null) return;
        var camera = _camera.Invoke();
        var surface = _heightAt.Invoke(camera);
        var underwater = false;
        var below = 0.0f;
        if (surface is float height)
        {
            below = (height * _units - camera.Y) / _units;
            underwater = below > 0.15f;
        }
        if (underwater != _wasUnderwater)
        {
            _wasUnderwater = underwater;
            GD.Print($"OPENNV_UNDERWATER visible={underwater} belowGameUnits={below:F1}");
            _underwaterChanged?.Invoke(underwater);
        }
        _underwaterState?.Invoke(underwater, below * _units);
        _tint.Visible = underwater;
        if (underwater)
        {
            var alpha = Mathf.Clamp(0.22f + below * _units * 0.015f, 0.22f, 0.60f);
            _material?.SetShaderParameter("depth_alpha", alpha);
        }
        UpdateLight(underwater, below * _units);
    }

    private void UpdateLight(bool underwater, float depthMeters)
    {
        if (_light is null || _lightMaterial is null) return;
        _light.Visible = underwater;
        var intensity = 0.0f;
        var sunUv = new Vector2(0.5f, 0.15f);
        if (underwater && _sun is not null)
        {
            var viewport = GetViewport();
            var camera = viewport.GetCamera3D();
            var direction = _sun.Invoke();
            if (camera is not null && direction.Y > 0.0f)
            {
                var world = camera.GlobalPosition + direction.Normalized() * 1000.0f;
                var size = viewport.GetVisibleRect().Size;
                if (!camera.IsPositionBehind(world) && size.X > 1 && size.Y > 1)
                {
                    var point = camera.UnprojectPosition(world);
                    sunUv = new Vector2(point.X / size.X, point.Y / size.Y);
                    var depthFade = Mathf.Clamp(1.0f - depthMeters * 0.02f, 0.25f, 1.0f);
                    intensity = Mathf.Clamp(direction.Y, 0.0f, 1.0f) * depthFade * 0.40f;
                }
            }
        }
        _lightMaterial.SetShaderParameter("sun_uv", sunUv);
        _lightMaterial.SetShaderParameter("intensity", intensity);
    }
}
