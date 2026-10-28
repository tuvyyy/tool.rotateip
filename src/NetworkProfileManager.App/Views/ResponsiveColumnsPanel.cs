using System.Windows;
using System.Windows.Controls;

namespace NetworkProfileManager.App.Views;

public sealed class ResponsiveColumnsPanel : Panel
{
    public static readonly DependencyProperty BreakpointProperty =
        DependencyProperty.Register(nameof(Breakpoint), typeof(double), typeof(ResponsiveColumnsPanel),
            new FrameworkPropertyMetadata(760.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty ColumnGapProperty =
        DependencyProperty.Register(nameof(ColumnGap), typeof(double), typeof(ResponsiveColumnsPanel),
            new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty RowGapProperty =
        DependencyProperty.Register(nameof(RowGap), typeof(double), typeof(ResponsiveColumnsPanel),
            new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty FirstColumnRatioProperty =
        DependencyProperty.Register(nameof(FirstColumnRatio), typeof(double), typeof(ResponsiveColumnsPanel),
            new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty EqualRowHeightProperty =
        DependencyProperty.Register(nameof(EqualRowHeight), typeof(bool), typeof(ResponsiveColumnsPanel),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double Breakpoint
    {
        get => (double)GetValue(BreakpointProperty);
        set => SetValue(BreakpointProperty, value);
    }

    public double ColumnGap
    {
        get => (double)GetValue(ColumnGapProperty);
        set => SetValue(ColumnGapProperty, value);
    }

    public double RowGap
    {
        get => (double)GetValue(RowGapProperty);
        set => SetValue(RowGapProperty, value);
    }

    public double FirstColumnRatio
    {
        get => (double)GetValue(FirstColumnRatioProperty);
        set => SetValue(FirstColumnRatioProperty, value);
    }

    public bool EqualRowHeight
    {
        get => (bool)GetValue(EqualRowHeightProperty);
        set => SetValue(EqualRowHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        if (visible.Count == 0) return new Size(0, 0);

        var width = double.IsInfinity(availableSize.Width) ? 1000.0 : availableSize.Width;
        var twoCols = width >= Breakpoint && visible.Count > 1;
        var gap = ColumnGap;
        var rowGap = RowGap;
        var ratio = Math.Clamp(FirstColumnRatio, 0.25, 0.75);

        if (!twoCols)
        {
            double totalHeight = 0;
            for (var i = 0; i < visible.Count; i++)
            {
                visible[i].Measure(new Size(width, double.PositiveInfinity));
                totalHeight += visible[i].DesiredSize.Height;
                if (i > 0) totalHeight += rowGap;
            }
            return new Size(width, totalHeight);
        }

        var usable = Math.Max(0, width - gap);
        var col0Width = usable * ratio;
        var col1Width = usable - col0Width;
        double totalRowsHeight = 0;

        for (var i = 0; i < visible.Count; i += 2)
        {
            var left = visible[i];
            left.Measure(new Size(col0Width, double.PositiveInfinity));
            var rowHeight = left.DesiredSize.Height;

            if (i + 1 < visible.Count)
            {
                var right = visible[i + 1];
                right.Measure(new Size(col1Width, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, right.DesiredSize.Height);
            }

            totalRowsHeight += rowHeight;
            if (i > 0) totalRowsHeight += rowGap;
        }

        return new Size(width, totalRowsHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        if (visible.Count == 0) return finalSize;

        var width = finalSize.Width;
        var twoCols = width >= Breakpoint && visible.Count > 1;
        var gap = ColumnGap;
        var rowGap = RowGap;
        var ratio = Math.Clamp(FirstColumnRatio, 0.25, 0.75);

        if (!twoCols)
        {
            double y = 0;
            foreach (var child in visible)
            {
                var h = child.DesiredSize.Height;
                child.Arrange(new Rect(0, y, width, h));
                y += h + rowGap;
            }
            return new Size(width, Math.Max(0, y - rowGap));
        }

        var usable = Math.Max(0, width - gap);
        var col0Width = usable * ratio;
        var col1Width = usable - col0Width;
        double currentY = 0;

        for (var i = 0; i < visible.Count; i += 2)
        {
            var left = visible[i];
            var leftH = left.DesiredSize.Height;
            var right = i + 1 < visible.Count ? visible[i + 1] : null;
            var rightH = right?.DesiredSize.Height ?? 0;
            var rowH = Math.Max(leftH, rightH);

            left.Arrange(new Rect(0, currentY, col0Width, EqualRowHeight ? rowH : leftH));
            right?.Arrange(new Rect(col0Width + gap, currentY, col1Width, EqualRowHeight ? rowH : rightH));

            currentY += rowH + rowGap;
        }

        return new Size(width, Math.Max(0, currentY - rowGap));
    }
}

