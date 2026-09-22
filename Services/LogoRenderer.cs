using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DKEzLogoMaker.Models;

namespace DKEzLogoMaker.Services;

public sealed class LogoRenderer
{
    public BitmapSource Render(LogoProject project)
        => Render(project, 100.0, BitmapScalingMode.HighQuality);

    public BitmapSource Render(LogoProject project, double scalePercent, BitmapScalingMode scalingMode)
    {
        var width = Math.Clamp(project.Canvas.Width, 1, 8192);
        var height = Math.Clamp(project.Canvas.Height, 1, 8192);
        var scale = Math.Clamp(scalePercent / 100.0, 0.01, 8.0);
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));

        if (targetWidth > 16384 || targetHeight > 16384 || (long)targetWidth * targetHeight > 100_000_000L)
            throw new InvalidOperationException("출력 이미지가 너무 큽니다. PNG 내보내기 배율을 낮춰주세요.");

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, scalingMode);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            DrawBackground(dc, project.Background, width, height);
            DrawImage(dc, project.Image);
            DrawText(dc, project.Text);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public void SavePng(string path, LogoProject project)
        => SavePng(path, project, 100.0, BitmapScalingMode.HighQuality);

    public void SavePng(string path, LogoProject project, double scalePercent, BitmapScalingMode scalingMode)
    {
        var bitmap = Render(project, scalePercent, scalingMode);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public Rect GetTextBounds(LogoProject project)
    {
        if (string.IsNullOrEmpty(project.Text.Value))
            return Rect.Empty;

        var metrics = BuildTextMetrics(project.Text);
        return new Rect(
            project.Text.CenterX - metrics.TotalWidth / 2.0,
            project.Text.CenterY - metrics.Height / 2.0,
            metrics.TotalWidth,
            metrics.Height);
    }

    public Rect GetImageBounds(LogoProject project)
    {
        var bitmap = LoadBitmap(project.Image.RuntimeBytes);
        if (bitmap is null)
            return Rect.Empty;

        var scaleX = Math.Max(0.00001, project.Image.ScaleXPercent / 100.0);
        var scaleY = Math.Max(0.00001, project.Image.ScaleYPercent / 100.0);
        var width = bitmap.PixelWidth * scaleX;
        var height = bitmap.PixelHeight * scaleY;
        return new Rect(
            project.Image.CenterX - width / 2.0,
            project.Image.CenterY - height / 2.0,
            width,
            height);
    }

    private static void DrawBackground(DrawingContext dc, BackgroundSettings background, int width, int height)
    {
        var rect = new Rect(0, 0, width, height);
        if (background.Mode == BackgroundMode.Transparent)
            return;

        if (background.Mode == BackgroundMode.Solid)
        {
            dc.DrawRectangle(new SolidColorBrush(ParseColor(background.Color1, Colors.Transparent)), null, rect);
            return;
        }

        var start = ParseColor(background.Color1, Colors.Transparent);
        var end = ParseColor(background.Color2, Colors.Transparent);
        var (startPoint, endPoint) = background.Direction switch
        {
            GradientDirection.TopToBottom => (new Point(0.5, 0), new Point(0.5, 1)),
            GradientDirection.TopLeftToBottomRight => (new Point(0, 0), new Point(1, 1)),
            GradientDirection.BottomLeftToTopRight => (new Point(0, 1), new Point(1, 0)),
            _ => (new Point(0, 0.5), new Point(1, 0.5))
        };

        dc.DrawRectangle(new LinearGradientBrush(start, end, startPoint, endPoint), null, rect);
    }

    private static void DrawImage(DrawingContext dc, ImageSettings imageSettings)
    {
        var bitmap = LoadBitmap(imageSettings.RuntimeBytes);
        if (bitmap is null)
            return;

        var scaleX = Math.Max(0.00001, imageSettings.ScaleXPercent / 100.0);
        var scaleY = Math.Max(0.00001, imageSettings.ScaleYPercent / 100.0);
        var width = bitmap.PixelWidth * scaleX;
        var height = bitmap.PixelHeight * scaleY;
        var rect = new Rect(
            imageSettings.CenterX - width / 2.0,
            imageSettings.CenterY - height / 2.0,
            width,
            height);

        dc.DrawImage(bitmap, rect);
    }

    private static void DrawText(DrawingContext dc, TextSettings text)
    {
        if (string.IsNullOrEmpty(text.Value))
            return;

        var metrics = BuildTextMetrics(text);
        var x = text.CenterX - metrics.TotalWidth / 2.0;
        var y = text.CenterY - metrics.Height / 2.0;

        foreach (var glyph in metrics.Glyphs)
        {
            dc.DrawText(glyph.Text, new Point(x, y));
            x += glyph.Width + metrics.Spacing;
        }
    }

    private static TextMetrics BuildTextMetrics(TextSettings text)
    {
        var fontFamily = new FontFamily(string.IsNullOrWhiteSpace(text.FontFamily) ? "Segoe UI" : text.FontFamily);
        var typeface = new Typeface(fontFamily, FontStyles.Normal, ParseWeight(text.Weight), FontStretches.Normal);
        var brush = new SolidColorBrush(ParseColor(text.Color, Colors.Black));
        brush.Freeze();
        var fontSize = Math.Clamp(text.Size, 0.1, 2048);
        var spacing = Math.Clamp(text.Spacing, -200, 1000);

        var glyphs = new List<TextGlyph>();
        var totalWidth = 0.0;
        var maxHeight = 0.0;

        foreach (var rune in text.Value.EnumerateRunes())
        {
            var formatted = new FormattedText(
                rune.ToString(),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                brush,
                1.0);

            var width = formatted.WidthIncludingTrailingWhitespace;
            glyphs.Add(new TextGlyph(formatted, width));
            totalWidth += width;
            maxHeight = Math.Max(maxHeight, formatted.Height);
        }

        if (glyphs.Count > 1)
            totalWidth += spacing * (glyphs.Count - 1);

        return new TextMetrics(glyphs, Math.Max(0, totalWidth), Math.Max(0, maxHeight), spacing);
    }

    private static BitmapImage? LoadBitmap(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
            return null;

        try
        {
            var bitmap = new BitmapImage();
            using var memory = new MemoryStream(bytes);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static FontWeight ParseWeight(string? value) => value?.ToLowerInvariant() switch
    {
        "medium" => FontWeights.Medium,
        "semibold" => FontWeights.SemiBold,
        "bold" => FontWeights.Bold,
        _ => FontWeights.Normal
    };

    private static System.Windows.Media.Color ParseColor(string? value, System.Windows.Media.Color fallback)
    {
        try
        {
            if (ColorConverter.ConvertFromString(value) is System.Windows.Media.Color color)
                return color;
        }
        catch
        {
        }

        return fallback;
    }

    private sealed record TextGlyph(FormattedText Text, double Width);
    private sealed record TextMetrics(IReadOnlyList<TextGlyph> Glyphs, double TotalWidth, double Height, double Spacing);
}
