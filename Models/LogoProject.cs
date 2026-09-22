using System.Text.Json.Serialization;

namespace DKEzLogoMaker.Models;

public sealed class LogoProject
{
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;
    public CanvasSettings Canvas { get; set; } = new();
    public BackgroundSettings Background { get; set; } = new();
    public TextSettings Text { get; set; } = new();
    public ImageSettings Image { get; set; } = new();

    public static LogoProject CreateDefault() => new();

    public void UpgradeIfNeeded()
    {
        if (Version < 2)
        {
            Text.CenterX = Canvas.Width / 2.0;
            Text.CenterY = Canvas.Height / 2.0;
        }

        if (Version < 3)
        {
            Image.ScaleXPercent = Image.ScalePercent;
            Image.ScaleYPercent = Image.ScalePercent;
            Image.PreserveAspectRatio = true;
        }

        Version = CurrentVersion;
    }
}

public sealed class CanvasSettings
{
    public int Width { get; set; } = 512;
    public int Height { get; set; } = 256;
}

public enum BackgroundMode
{
    Transparent,
    Solid,
    Gradient
}

public enum GradientDirection
{
    LeftToRight,
    TopToBottom,
    TopLeftToBottomRight,
    BottomLeftToTopRight
}

public sealed class BackgroundSettings
{
    public BackgroundMode Mode { get; set; } = BackgroundMode.Transparent;
    public string Color1 { get; set; } = "#352E59";
    public string Color2 { get; set; } = "#111827";
    public GradientDirection Direction { get; set; } = GradientDirection.LeftToRight;
}

public sealed class TextSettings
{
    public string Value { get; set; } = "DK Logo";
    public string FontFamily { get; set; } = "Segoe UI";
    public double Size { get; set; } = 64;
    public string Weight { get; set; } = "Bold";
    public double Spacing { get; set; } = 0;
    public string Color { get; set; } = "#20242A";
    public double CenterX { get; set; } = 256;
    public double CenterY { get; set; } = 128;
}

public sealed class ImageSettings
{
    public string? SourceFileName { get; set; }
    public string? EmbeddedAssetName { get; set; }

    // v1/v2 compatibility. v3 uses ScaleXPercent / ScaleYPercent.
    public double ScalePercent { get; set; } = 100;
    public double ScaleXPercent { get; set; } = 100;
    public double ScaleYPercent { get; set; } = 100;
    public bool PreserveAspectRatio { get; set; } = true;

    public double CenterX { get; set; } = 256;
    public double CenterY { get; set; } = 128;

    [JsonIgnore]
    public string? RuntimeSourcePath { get; set; }

    [JsonIgnore]
    public byte[]? RuntimeBytes { get; set; }
}
