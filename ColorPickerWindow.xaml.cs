using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ESDP
{
    public partial class ColorPickerWindow : Window
    {
        private static readonly List<string> RecentHexes = new();
        private bool _updatingHex;

        public string SelectedHex { get; private set; }

        public ColorPickerWindow(string currentHex)
        {
            InitializeComponent();
            var officePalette = ColorOption.BuildOfficePalette();
            BaseThemeColors.ItemsSource = officePalette.Take(10).ToList();
            ThemeShadeColumns.ItemsSource = Enumerable.Range(0, 10)
                .Select(column => new ThemeShadeColumn
                {
                    Shades = Enumerable.Range(1, 5)
                        .Select(row => officePalette[row * 10 + column])
                        .ToList()
                })
                .ToList();
            RecentColors.ItemsSource = BuildRecentOptions();
            SelectedHex = ColorOption.NormalizeHex(currentHex) ?? "#000000";
            SetSelectedColor(SelectedHex);
        }

        public sealed class ThemeShadeColumn
        {
            public List<ColorOption> Shades { get; set; } = new();
        }

        public static bool TryPick(
            Window owner, string currentHex, out string selectedHex,
            string title = "색 선택")
        {
            var window = new AdvancedColorPickerWindow(currentHex)
            {
                Owner = owner,
                Title = title
            };
            bool accepted = window.ShowDialog() == true;
            selectedHex = accepted ? window.SelectedHex : currentHex;
            return accepted;
        }

        private static List<ColorOption> BuildRecentOptions()
            => RecentHexes.Select(hex => new ColorOption
            {
                Name = hex,
                Hex = hex
            }).ToList();

        private void ColorSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string hex })
                SetSelectedColor(hex);
        }

        private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingHex) return;
            string? normalized = ColorOption.NormalizeHex(HexBox.Text);
            if (normalized == null) return;
            SelectedHex = normalized;
            SelectedPreview.Fill = ColorOption.BrushFromHex(normalized);
        }

        private void SetSelectedColor(string hex)
        {
            string normalized = ColorOption.NormalizeHex(hex) ?? "#000000";
            SelectedHex = normalized;
            _updatingHex = true;
            HexBox.Text = normalized;
            _updatingHex = false;
            SelectedPreview.Fill = ColorOption.BrushFromHex(normalized);
        }

        private void EyedropperButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            var picker = new ScreenEyedropperWindow();
            bool picked = picker.ShowDialog() == true;
            Show();
            Activate();
            if (picked && picker.SelectedHex != null)
                SetSelectedColor(picker.SelectedHex);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            string? normalized = ColorOption.NormalizeHex(HexBox.Text);
            if (normalized == null)
            {
                MessageBox.Show("HEX 색상은 #RRGGBB 형식으로 입력하세요.",
                                "색 선택", MessageBoxButton.OK,
                                MessageBoxImage.Information);
                return;
            }

            SelectedHex = normalized;
            RecentHexes.RemoveAll(hex =>
                hex.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            RecentHexes.Insert(0, normalized);
            if (RecentHexes.Count > 8)
                RecentHexes.RemoveRange(8, RecentHexes.Count - 8);
            DialogResult = true;
        }
    }

    internal sealed class ScreenEyedropperWindow : Window
    {
        private readonly Canvas _canvas;
        private readonly Border _preview;
        private readonly Rectangle _swatch;
        private readonly TextBlock _label;

        public string? SelectedHex { get; private set; }

        public ScreenEyedropperWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            Topmost = true;
            ShowInTaskbar = false;
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            Cursor = Cursors.Cross;

            _canvas = new Canvas();
            _swatch = new Rectangle
            {
                Width = 24, Height = 24,
                Stroke = Brushes.White, StrokeThickness = 1
            };
            _label = new TextBlock
            {
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(7, 0, 0, 0)
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(_swatch);
            panel.Children.Add(_label);
            _preview = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 25, 34, 43)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6),
                Child = panel,
                IsHitTestVisible = false
            };
            _canvas.Children.Add(_preview);
            Content = _canvas;

            MouseMove += OnMouseMove;
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) DialogResult = false;
            };
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var screen = PointToScreen(e.GetPosition(this));
            string hex = SampleScreenPixel((int)screen.X, (int)screen.Y);
            _swatch.Fill = ColorOption.BrushFromHex(hex);
            _label.Text = hex + "  클릭하여 선택";

            var local = e.GetPosition(_canvas);
            double x = Math.Min(ActualWidth - 150, local.X + 18);
            double y = Math.Min(ActualHeight - 48, local.Y + 18);
            Canvas.SetLeft(_preview, Math.Max(0, x));
            Canvas.SetTop(_preview, Math.Max(0, y));
        }

        private void OnMouseLeftButtonDown(
            object sender, MouseButtonEventArgs e)
        {
            var screen = PointToScreen(e.GetPosition(this));
            SelectedHex = SampleScreenPixel((int)screen.X, (int)screen.Y);
            DialogResult = true;
        }

        private static string SampleScreenPixel(int x, int y)
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            try
            {
                uint colorRef = GetPixel(dc, x, y);
                byte r = (byte)(colorRef & 0xFF);
                byte g = (byte)((colorRef >> 8) & 0xFF);
                byte b = (byte)((colorRef >> 16) & 0xFF);
                return $"#{r:X2}{g:X2}{b:X2}";
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern uint GetPixel(IntPtr dc, int x, int y);
    }
}
