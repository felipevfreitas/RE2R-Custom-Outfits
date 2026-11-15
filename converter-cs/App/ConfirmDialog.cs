// ConfirmDialog.cs - confirmation box in the program's dark theme (instead of the light MessageBox).
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CustomOutfits
{
    internal static class ConfirmDialog
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(System.IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Returns true if the user clicked the first button. cancelText null = single button.</summary>
        public static bool Show(Window owner, string title, string message, string detail, string okText, string cancelText)
        {
            bool result = false;
            var w = new Window
            {
                Title = title,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)Application.Current.FindResource("Panel"),
                Foreground = (Brush)Application.Current.FindResource("Text"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
            };
            w.SourceInitialized += (s, e) =>
            {
                int on = 1;
                var h = new WindowInteropHelper(w).Handle;
                if (DwmSetWindowAttribute(h, 20, ref on, 4) != 0) DwmSetWindowAttribute(h, 19, ref on, 4);
            };
            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20), MaxWidth = 560 };
            root.Children.Add(new TextBlock { Text = message, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(detail))
                root.Children.Add(new TextBlock
                {
                    Text = detail, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 13,
                    Foreground = (Brush)Application.Current.FindResource("Muted"),
                });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            if (cancelText != null)
            {
                var cancel = new Button { Content = cancelText, Style = (Style)Application.Current.FindResource("Btn"), Margin = new Thickness(0, 0, 10, 0), IsCancel = true, IsDefault = true };
                cancel.Click += (s, e) => w.Close();
                buttons.Children.Add(cancel);
            }
            var ok = new Button
            {
                Content = okText,
                Style = (Style)Application.Current.FindResource(cancelText != null ? "BtnDanger" : "BtnPrimary"),
                IsDefault = cancelText == null,
            };
            ok.Click += (s, e) => { result = true; w.Close(); };
            buttons.Children.Add(ok);
            root.Children.Add(buttons);
            w.Content = root;
            w.ShowDialog();
            return result;
        }
    }
}
