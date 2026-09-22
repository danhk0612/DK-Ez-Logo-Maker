namespace DKEzLogoMaker.Models;

public sealed class AppSettings
{
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 760;
    public double PreviewZoom { get; set; } = 1.0;
    public string? LastProjectDirectory { get; set; }
    public string? LastExportDirectory { get; set; }
    public string? LastImageDirectory { get; set; }
    public string? LastImagePath { get; set; }
    public double LastExportScalePercent { get; set; } = 100;
    public string LastExportScalingMode { get; set; } = "HighQuality";
    public LogoProject EditorState { get; set; } = LogoProject.CreateDefault();
}
