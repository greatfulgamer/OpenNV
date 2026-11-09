using Godot;

namespace OpenNV.Runtime.Presentation.Rendering;

internal partial class RuntimeNativeUnderwater : CanvasLayer
{
    private ColorRect? _tint;
    private Func<Vector3>? _camera;
    private Func<Vector3, float?>? _heightAt;
    private Action<bool>? _underwaterChanged;
    private float _units = 1.0f;
    private bool _wasUnderwater;

    internal void Configure(Func<Vector3> camera, Func<Vector3, float?> heightAt, float units, Action<bool>? underwaterChanged = null)
    {
        _camera = camera;
        _heightAt = heightAt;
        _underwaterChanged = underwaterChanged;
        _units = units;
    }

    public override void _Ready()
    {
        Layer = 50;
        _tint = new ColorRect
        {
            Name = "UnderwaterTint",
            Color = new Color(0.03f, 0.12f, 0.16f, 0.55f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _tint.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_tint);
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
        _tint.Visible = underwater;
        if (underwater)
        {
            var alpha = Mathf.Clamp(0.25f + below * _units * 0.02f, 0.25f, 0.60f);
            _tint.Color = new Color(0.03f, 0.12f, 0.16f, alpha);
        }
    }
}
