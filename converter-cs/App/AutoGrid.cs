// AutoGrid.cs - a wrap panel whose items stretch to fill the row: as many columns as fit at MinItemWidth, then every item
// gets the same width, so the right side never has an empty gap (a new column appears when there is room for it).
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace CustomOutfits
{
    public sealed class AutoGrid : Panel
    {
        public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(AutoGrid),
            new FrameworkPropertyMetadata(160.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(AutoGrid),
            new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
        public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

        private int Columns(double width) =>
            double.IsInfinity(width) || width <= 0 ? Math.Max(1, InternalChildren.Count) : Math.Max(1, (int)((width + Gap) / (MinItemWidth + Gap)));

        private double ItemWidth(double width, int cols) =>
            double.IsInfinity(width) ? MinItemWidth : Math.Max(0, (width - Gap * (cols - 1)) / cols);

        protected override Size MeasureOverride(Size available)
        {
            int n = InternalChildren.Count;
            if (n == 0) return new Size(0, 0);
            int cols = Columns(available.Width);
            double w = ItemWidth(available.Width, cols), height = 0;
            for (int row = 0; row * cols < n; row++)
            {
                double rowH = 0;
                for (int i = row * cols; i < Math.Min(n, (row + 1) * cols); i++)
                {
                    InternalChildren[i].Measure(new Size(w, double.PositiveInfinity));
                    rowH = Math.Max(rowH, InternalChildren[i].DesiredSize.Height);
                }
                height += rowH + (row > 0 ? Gap : 0);
            }
            double total = double.IsInfinity(available.Width) ? cols * w + (cols - 1) * Gap : available.Width;
            return new Size(total, height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            int n = InternalChildren.Count;
            int cols = Columns(final.Width);
            double w = ItemWidth(final.Width, cols), y = 0;
            for (int row = 0; row * cols < n; row++)
            {
                var items = Enumerable.Range(row * cols, Math.Min(cols, n - row * cols)).Select(i => InternalChildren[i]).ToList();
                double rowH = items.Max(c => c.DesiredSize.Height);
                for (int k = 0; k < items.Count; k++) items[k].Arrange(new Rect(k * (w + Gap), y, w, rowH));
                y += rowH + Gap;
            }
            return final;
        }
    }
}
