using Godot;

namespace OpenNV.Runtime.Presentation.Ui;

internal partial class LoadingScreen : CanvasLayer
{
    private Label _title = null!;
    private LoadingProgressIndicator _progress = null!;

    internal void Configure(string status)
    {
        Name = "LiveRetailLoadingScreen";
        Layer = 1000;
        ProcessMode = ProcessModeEnum.Always;
        var background = new ColorRect
        {
            Color = Colors.White,
            Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/loading_screen.gdshader") },
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(background);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(680, 250) };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.022f, 0.030f, 0.034f, 0.94f),
            BorderColor = new Color(0.62f, 0.45f, 0.20f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomRight = 8,
            CornerRadiusBottomLeft = 8,
            ShadowColor = new Color(0.0f, 0.0f, 0.0f, 0.55f),
            ShadowSize = 18,
            ContentMarginLeft = 54,
            ContentMarginRight = 54,
            ContentMarginTop = 34,
            ContentMarginBottom = 30,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        center.AddChild(panel);

        var content = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        content.AddThemeConstantOverride("separation", 14);
        panel.AddChild(content);

        _title = new Label { Text = "OPENNV", HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeColorOverride("font_color", new Color(0.96f, 0.80f, 0.42f));
        _title.AddThemeFontSizeOverride("font_size", 46);
        content.AddChild(_title);

        var subtitle = new Label
        {
            Text = "MOJAVE WASTELAND   //   LIVE RETAIL FILES",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        subtitle.AddThemeColorOverride("font_color", new Color(0.54f, 0.62f, 0.55f));
        subtitle.AddThemeFontSizeOverride("font_size", 13);
        content.AddChild(subtitle);

        content.AddChild(new ColorRect
        {
            Color = new Color(0.62f, 0.45f, 0.20f, 0.85f),
            CustomMinimumSize = new Vector2(0, 1),
        });

        _progress = new LoadingProgressIndicator(status);
        content.AddChild(_progress);

        var credit = new Label
        {
            Text = "GX-9000  //  OPENNV RUNTIME",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        credit.AddThemeColorOverride("font_color", new Color(0.36f, 0.40f, 0.36f));
        credit.AddThemeFontSizeOverride("font_size", 11);
        content.AddChild(credit);
    }

    internal void SetStatus(string status) => _progress.SetStatus(status);
    internal void SetTitle(string title) => _title.Text = title;
    internal object State => _progress.State;

    internal void ShowError(string message)
    {
        _title.Text = "Unable to load";
        _title.AddThemeColorOverride("font_color", new Color(0.95f, 0.36f, 0.26f));
        _progress.ShowError(message);
    }
}
