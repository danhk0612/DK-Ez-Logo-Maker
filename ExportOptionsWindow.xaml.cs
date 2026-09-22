using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DKEzLogoMaker;

public partial class ExportOptionsWindow : Window
{
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;

    public double ScalePercent { get; private set; }
    public BitmapScalingMode ScalingMode { get; private set; }
    public string ScalingModeName => ScalingMode.ToString();

    public ExportOptionsWindow(int canvasWidth, int canvasHeight, double scalePercent, string? scalingMode)
    {
        InitializeComponent();
        _canvasWidth = Math.Max(1, canvasWidth);
        _canvasHeight = Math.Max(1, canvasHeight);

        SelectByTag(ScaleCombo, NormalizeScale(scalePercent).ToString("0"));
        SelectByTag(ScalingModeCombo, NormalizeScalingModeName(scalingMode));
        UpdateOutputSize();
    }

    private void ScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateOutputSize();

    private void UpdateOutputSize()
    {
        if (OutputSizeText is null)
            return;

        var scale = GetSelectedScale();
        var width = Math.Max(1, (int)Math.Round(_canvasWidth * scale / 100.0));
        var height = Math.Max(1, (int)Math.Round(_canvasHeight * scale / 100.0));
        OutputSizeText.Text = $"출력 크기: {width:N0} × {height:N0} px";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        ScalePercent = GetSelectedScale();
        var name = (ScalingModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        ScalingMode = ParseScalingMode(name);
        DialogResult = true;
    }

    private double GetSelectedScale()
    {
        var text = (ScaleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return double.TryParse(text, out var value) ? NormalizeScale(value) : 100;
    }

    private static double NormalizeScale(double value)
    {
        var supported = new[] { 25d, 50d, 100d, 200d, 400d };
        return supported.OrderBy(x => Math.Abs(x - value)).First();
    }

    private static string NormalizeScalingModeName(string? value)
        => value switch
        {
            "Linear" => "Linear",
            "NearestNeighbor" => "NearestNeighbor",
            _ => "HighQuality"
        };

    private static BitmapScalingMode ParseScalingMode(string? value)
        => value switch
        {
            "Linear" => BitmapScalingMode.Linear,
            "NearestNeighbor" => BitmapScalingMode.NearestNeighbor,
            _ => BitmapScalingMode.HighQuality
        };

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }
}
