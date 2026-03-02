using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudisService
{
    public partial class IconPicker : Window
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern int ExtractIconEx(string f, int i, IntPtr[]? l, IntPtr[]? s, int n);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr h);

        /// <summary>
        /// Full path to the DLL the user picked from, e.g. "C:\Windows\System32\imageres.dll".
        /// Only valid when DialogResult == true and SelectedIconIndex >= 0.
        /// </summary>
        public string SelectedDllPath { get; private set; } = "";

        /// <summary>
        /// The icon index within SelectedDllPath.
        /// -1 means the user pressed "Reset to Default" — ignore SelectedDllPath in that case.
        /// Only valid when DialogResult == true.
        /// </summary>
        public int SelectedIconIndex { get; private set; } = -2; // -2 = nothing chosen yet

        public IconPicker()
        {
            InitializeComponent();
            // Auto-load shell32 on open so the grid isn't empty
            Loaded += (_, _) => BtnLoad_Click(this, new RoutedEventArgs());
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private string GetSelectedDllPath()
        {
            if (CmbDll.SelectedItem is ComboBoxItem item && item.Tag is string path)
                return path;
            return @"C:\Windows\System32\shell32.dll";
        }

        private static BitmapSource? ExtractIcon(string dllPath, int index)
        {
            var large = new IntPtr[1];
            try
            {
                if (ExtractIconEx(dllPath, index, large, null, 1) < 1 || large[0] == IntPtr.Zero)
                    return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally { if (large[0] != IntPtr.Zero) DestroyIcon(large[0]); }
        }

        // ── Load icons into grid ──────────────────────────────────────────────────

        private void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            IconPanel.Children.Clear();
            TxtFoundCount.Text = "Loading…";

            string dll = GetSelectedDllPath();
            if (!int.TryParse(TxtFrom.Text, out int from)) from = 0;
            if (!int.TryParse(TxtTo.Text,   out int to))   to   = 300;

            int found = 0;

            for (int i = from; i <= to; i++)
            {
                var src = ExtractIcon(dll, i);
                if (src == null) continue;

                found++;
                int idx = i; // capture for closure

                var img = new System.Windows.Controls.Image
                {
                    Source = src, Width = 32, Height = 32,
                    ToolTip = $"Index {idx}"
                };
                var tb = new TextBlock
                {
                    Text = idx.ToString(),
                    FontSize = 9,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Foreground = System.Windows.Media.Brushes.Gray
                };
                var sp = new StackPanel
                {
                    Width = 56, Margin = new Thickness(2),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                sp.Children.Add(img);
                sp.Children.Add(tb);
                sp.MouseLeftButtonUp += (_, _) => SelectIcon(dll, idx, src);

                IconPanel.Children.Add(sp);
            }

            TxtFoundCount.Text = found > 0 ? $"{found} icons found" : "No icons found in this range";

            if (found == 0)
                IconPanel.Children.Add(new TextBlock
                {
                    Text = "No icons found in this range.", Margin = new Thickness(8), Foreground = System.Windows.Media.Brushes.Gray
                });
        }

        private void SelectIcon(string dllPath, int index, BitmapSource src)
        {
            SelectedDllPath   = dllPath;
            SelectedIconIndex = index;
            SelImg.Source     = src;

            string dllName = System.IO.Path.GetFileName(dllPath);
            SelTxt.Text    = $"{dllName}  index {index}";
            BtnOk.IsEnabled = true;
        }

        // ── Dialog buttons ────────────────────────────────────────────────────────

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            // -1 signals "restore the default Windows file-type icon"
            SelectedIconIndex = -1;
            SelectedDllPath   = "";
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
