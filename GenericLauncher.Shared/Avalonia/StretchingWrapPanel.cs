using System;
using Avalonia;
using Avalonia.Controls;

namespace GenericLauncher.Avalonia;

public class StretchingWrapPanel : Panel
{
    public static readonly StyledProperty<Thickness> PaddingProperty =
        AvaloniaProperty.Register<StretchingWrapPanel, Thickness>(nameof(Padding));

    public Thickness Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<StretchingWrapPanel, double>(
            nameof(Spacing),
            0.0,
            coerce: CoerceSpacing);

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<StretchingWrapPanel, double>(
            nameof(MinItemWidth),
            150.0,
            coerce: CoerceMinItemWidth);

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public static readonly StyledProperty<double> MaxItemWidthProperty =
        AvaloniaProperty.Register<StretchingWrapPanel, double>(
            nameof(MaxItemWidth),
            300.0,
            coerce: CoerceMaxItemWidth);

    public double MaxItemWidth
    {
        get => GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    static StretchingWrapPanel()
    {
        AffectsMeasure<StretchingWrapPanel>(SpacingProperty,
            MinItemWidthProperty,
            MaxItemWidthProperty,
            PaddingProperty);
        AffectsArrange<StretchingWrapPanel>(SpacingProperty,
            MinItemWidthProperty,
            MaxItemWidthProperty,
            PaddingProperty);
    }

    // Caching the computations of items' width
    private double _cachedItemWidth;
    private double _cachedAvailableWidth;

    private static double CoerceSpacing(AvaloniaObject obj, double value) => Math.Max(0, value);

    private static double CoerceMinItemWidth(AvaloniaObject obj, double value)
    {
        if (obj is StretchingWrapPanel panel)
        {
            return Math.Max(1, Math.Min(value, panel.MaxItemWidth));
        }

        return Math.Max(1, value);
    }

    private static double CoerceMaxItemWidth(AvaloniaObject obj, double value)
    {
        if (obj is StretchingWrapPanel panel)
        {
            return Math.Max(panel.MinItemWidth, value);
        }

        return Math.Max(1, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var childCount = Children.Count;
        if (childCount == 0)
        {
            return new Size(Padding.Left + Padding.Right, Padding.Top + Padding.Bottom);
        }

        var contentWidth = availableSize.Width - Padding.Left - Padding.Right;
        var contentHeight = availableSize.Height - Padding.Top - Padding.Bottom;

        // Handle infinite width
        if (double.IsInfinity(contentWidth) || double.IsNaN(contentWidth))
        {
            contentWidth = MinItemWidth * childCount + Spacing * (childCount - 1);
        }

        var itemWidth = CalculateOptimalItemWidth(contentWidth);

        // Cache the item width together with content width, for which it was computed, so we can
        // re-use it when arranging the items.
        _cachedItemWidth = itemWidth;
        _cachedAvailableWidth = contentWidth;

        // Items per row i.e., number of columns, is calculated based on the MinItemWidth
        var itemsPerRow = Math.Max(1, (int)Math.Floor((contentWidth + Spacing) / (MinItemWidth + Spacing)));

        // Special case when all items fit into one row
        if (itemsPerRow >= childCount)
        {
            itemsPerRow = childCount;
        }

        var rowsCount = (int)Math.Ceiling((double)childCount / itemsPerRow);

        // We have fixed-height items only, so cache the first child's height for content height
        // calculation,
        var itemHeight = 0.0;
        if (childCount > 0)
        {
            Children[0].Measure(new Size(itemWidth, contentHeight));
            itemHeight = Children[0].DesiredSize.Height;

            // We still should measure the remaining children, otherwise they will have invalid
            // size during arranging, and they will be measured then. So we warm up them now.
            for (var i = 1; i < childCount; i++)
            {
                Children[i].Measure(new Size(itemWidth, contentHeight));
            }
        }

        var totalContentHeight = rowsCount * itemHeight + Math.Max(0, rowsCount - 1) * Spacing;
        var totalHeight = totalContentHeight + Padding.Top + Padding.Bottom;
        return new Size(availableSize.Width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var childCount = Children.Count;
        if (childCount == 0)
        {
            return finalSize;
        }

        var contentWidth = finalSize.Width - Padding.Left - Padding.Right;

        // Use cached item width if the content width matches, otherwise recalculate
        var itemWidth = Math.Abs(contentWidth - _cachedAvailableWidth) < 0.01
            ? _cachedItemWidth
            : CalculateOptimalItemWidth(contentWidth);

        // Items per row i.e., number of columns, is calculated based on the MinItemWidth
        var itemsPerRow = Math.Max(1, (int)Math.Floor((contentWidth + Spacing) / (MinItemWidth + Spacing)));

        // Special case when all items fit into one row
        if (itemsPerRow >= childCount)
        {
            itemsPerRow = childCount;
        }

        // We have fixed-height items only, so get the first item's height
        var itemHeight = childCount > 0 ? Children[0].DesiredSize.Height : 0;

        // Start arranging/positioning the items
        var x = Padding.Left;
        var y = Padding.Top;
        var itemsInCurrentRow = 0;

        var rowsCount = (int)Math.Ceiling((double)childCount / itemsPerRow);

        foreach (var child in Children)
        {
            // Start new line
            if (itemsInCurrentRow >= itemsPerRow && itemsInCurrentRow > 0)
            {
                y += itemHeight + Spacing;
                x = Padding.Left;
                itemsInCurrentRow = 0;
            }

            // TODO: Position the items on integer coordinates only and position the last column
            //  items on `finalSize.Width - Padding.Right - itemWidth` so we do not have any cropped
            //  pixels.
            child.Arrange(new Rect(x, y, itemWidth, itemHeight));
            x += itemWidth + Spacing;
            itemsInCurrentRow++;
        }

        var totalContentHeight = rowsCount * itemHeight + Math.Max(0, rowsCount - 1) * Spacing;
        var totalHeight = totalContentHeight + Padding.Top + Padding.Bottom;

        // Return the finalSize.Height, when the content is smaller than the ScrollViewer's height,
        // otherwise the content will be vertically centered. Look like ScrollViewer is ignoring the
        // `VerticalContentAlignment="Top"` :/
        return new Size(finalSize.Width, Math.Max(finalSize.Height, totalHeight));
    }

    private double CalculateOptimalItemWidth(double availableWidth)
    {
        var childCount = Children.Count;
        if (childCount == 0 || availableWidth <= 0)
        {
            return MinItemWidth;
        }

        if (availableWidth < MinItemWidth)
        {
            return Math.Max(1, availableWidth);
        }

        // We always compute the number of columns based on the min width
        var itemsPerRow = Math.Max(1, (int)Math.Floor((availableWidth + Spacing) / (MinItemWidth + Spacing)));

        // Single-row is a special case where the window can be so big, that the items cannot
        // horizontally stretch to fill all the available space. We need special handling to prevent
        // items from changing width when window expands beyond what's needed for max width. Other
        // similar component have a bug where the items' width is jumping between min -> max -> min.
        if (itemsPerRow >= childCount)
        {
            var totalSpacingForSingleRow = Spacing * Math.Max(0, childCount - 1);
            var widthNeededForSingleRow = childCount * MaxItemWidth + totalSpacingForSingleRow;

            // If we have enough space for all items at max width, lock them at max width. This
            // prevents the "jumping items' width bug" when window expands further.
            if (availableWidth >= widthNeededForSingleRow)
            {
                return MaxItemWidth;
            }

            // Otherwise, distribute available width among all children, which are still not at
            // their max width
            return (availableWidth - totalSpacingForSingleRow) / childCount;
        }

        // Multi-row case, where at least the first row can horizontally fill all the available space.
        // Calculate the width to distribute among items per row
        var totalSpacing = Spacing * Math.Max(0, itemsPerRow - 1);
        return (availableWidth - totalSpacing) / itemsPerRow;
    }
}
