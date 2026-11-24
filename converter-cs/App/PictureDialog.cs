// PictureDialog.cs - "Change picture": the choices for the picture in the game's costume menu, side by side with a preview
// (the mod's own picture, each pose drawn from the outfit, an image of the user's). Dark window, like ConfirmDialog.
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace CustomOutfits
{
    internal sealed class PictureChoiceItem
    {
        public string Key;          // returned when chosen
        public string Title;        // e.g. "Pose 1"
        public string Detail;       // e.g. "Arms down"
        public ImageSource Image;   // null = no preview (the "Upload image" tile shows a symbol)
        public bool Current;        // the picture used now
    }

    internal static class PictureDialog
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(System.IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Shows the choices; returns the Key of the one clicked, or null (closed).</summary>
        public static string Show(Window owner, string title, IList<PictureChoiceItem> items)
        {
            string result = null;
            var res = Application.Current;
            var w = new Window
            {
                Title = title,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)res.FindResource("Panel"),
                Foreground = (Brush)res.FindResource("Text"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
            };
            w.SourceInitialized += (s, e) =>
            {
                int on = 1;
                var h = new WindowInteropHelper(w).Handle;
                if (DwmSetWindowAttribute(h, 20, ref on, 4) != 0) DwmSetWindowAttribute(h, 19, ref on, 4);
            };
            w.KeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };

            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            root.Children.Add(new TextBlock { Text = "Picture in the game's costume menu", FontSize = 16, FontWeight = FontWeights.SemiBold });
            root.Children.Add(new TextBlock
            {
                Text = "Click the one to use.", Margin = new Thickness(0, 6, 0, 16), FontSize = 13,
                Foreground = (Brush)res.FindResource("Muted"),
            });
            var row = new WrapPanel { MaxWidth = 4 * 212 };
            foreach (var it in items)
            {
                var tile = new StackPanel { Width = 168 };
                var frame = new Border
                {
                    Width = 168, Height = 168, CornerRadius = new CornerRadius(6), ClipToBounds = true,
                    Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
                };
                if (it.Image != null)
                    frame.Child = new Image { Source = it.Image, Stretch = Stretch.Uniform, Margin = new Thickness(4) };
                else
                    frame.Child = new TextBlock
                    {
                        Text = "+", FontSize = 54, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)res.FindResource("Muted"),
                    };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(frame, BitmapScalingMode.HighQuality);
                tile.Children.Add(frame);
                tile.Children.Add(new TextBlock { Text = it.Title, Margin = new Thickness(2, 10, 0, 0), FontWeight = FontWeights.SemiBold });
                tile.Children.Add(new TextBlock
                {
                    Text = (it.Current ? "In use · " : "") + (it.Detail ?? ""), Margin = new Thickness(2, 2, 0, 0), FontSize = 12.5,
                    Foreground = it.Current ? (Brush)res.FindResource("Ok") : (Brush)res.FindResource("Muted"), TextWrapping = TextWrapping.Wrap,
                });
                var normal = it.Current ? (Brush)res.FindResource("Accent") : (Brush)res.FindResource("Line");
                var card = new Border
                {
                    Child = tile, Padding = new Thickness(10), Margin = new Thickness(0, 0, 14, 14), CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(2), BorderBrush = normal, Background = (Brush)res.FindResource("Panel2"),
                    Cursor = Cursors.Hand, ToolTip = it.Title + (string.IsNullOrEmpty(it.Detail) ? "" : ": " + it.Detail),
                };
                card.MouseEnter += (s, e) => card.BorderBrush = (Brush)res.FindResource("Accent2");
                card.MouseLeave += (s, e) => card.BorderBrush = normal;
                string key = it.Key;
                card.MouseLeftButtonUp += (s, e) => { result = key; w.Close(); };
                row.Children.Add(card);
            }
            root.Children.Add(row);
            var cancel = new Button
            {
                Content = "Cancel", Style = (Style)res.FindResource("Btn"), HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 14, 0), IsCancel = true,
            };
            cancel.Click += (s, e) => w.Close();
            root.Children.Add(cancel);
            w.Content = root;
            string snap = System.Environment.GetEnvironmentVariable("CO_SNAPSHOT_DIALOG");
            if (snap != null)
            {
                // (development: an image of the choices instead of showing them)
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = 0; w.Top = 0; w.ShowActivated = false; w.Opacity = 0.01;
                w.Show();
                w.UpdateLayout();
                var fe = (FrameworkElement)w.Content;
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)fe.ActualWidth, (int)fe.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var bg = new DrawingVisual();
                using (var dc = bg.RenderOpen()) dc.DrawRectangle((Brush)res.FindResource("Panel"), null, new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                bmp.Render(bg);
                bmp.Render(fe);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = System.IO.File.Create(snap)) enc.Save(fs);
                w.Close();
                return null;
            }
            w.ShowDialog();
            return result;
        }
    }
}
