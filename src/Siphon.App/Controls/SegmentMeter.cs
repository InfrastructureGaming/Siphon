using System.Windows;
using System.Windows.Media;

namespace Siphon.App.Controls;

/// <summary>
/// A horizontal segmented peak meter. Segments are spaced evenly in dB from <see cref="FloorDb"/>
/// up to -1 dBFS; the top segment (at or above -1 dBFS) lights red.
/// </summary>
public sealed class SegmentMeter : FrameworkElement
{
    public const double FloorDb = -48;
    private const double HotDb = -1;
    private const double Gap = 2;

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(SegmentMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentCountProperty = DependencyProperty.Register(
        nameof(SegmentCount), typeof(int), typeof(SegmentMeter),
        new FrameworkPropertyMetadata(24, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LitBrushProperty = DependencyProperty.Register(
        nameof(LitBrush), typeof(Brush), typeof(SegmentMeter),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HotBrushProperty = DependencyProperty.Register(
        nameof(HotBrush), typeof(Brush), typeof(SegmentMeter),
        new FrameworkPropertyMetadata(Brushes.Red, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IdleBrushProperty = DependencyProperty.Register(
        nameof(IdleBrush), typeof(Brush), typeof(SegmentMeter),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Linear peak level, 1.0 = full scale.</summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public int SegmentCount
    {
        get => (int)GetValue(SegmentCountProperty);
        set => SetValue(SegmentCountProperty, value);
    }

    public Brush LitBrush
    {
        get => (Brush)GetValue(LitBrushProperty);
        set => SetValue(LitBrushProperty, value);
    }

    public Brush HotBrush
    {
        get => (Brush)GetValue(HotBrushProperty);
        set => SetValue(HotBrushProperty, value);
    }

    public Brush IdleBrush
    {
        get => (Brush)GetValue(IdleBrushProperty);
        set => SetValue(IdleBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        int n = Math.Max(2, SegmentCount);
        double width = (ActualWidth - Gap * (n - 1)) / n;
        if (width <= 0)
            return;

        double levelDb = Level > 0 ? 20 * Math.Log10(Level) : double.NegativeInfinity;
        double step = (HotDb - FloorDb) / (n - 1);
        for (int i = 0; i < n; i++)
        {
            double threshold = FloorDb + i * step;
            Brush brush = levelDb < threshold ? IdleBrush : threshold >= HotDb ? HotBrush : LitBrush;
            var rect = new Rect(Math.Round(i * (width + Gap)), 0, Math.Max(1, Math.Round(width)), ActualHeight);
            dc.DrawRoundedRectangle(brush, null, rect, 1, 1);
        }
    }
}
