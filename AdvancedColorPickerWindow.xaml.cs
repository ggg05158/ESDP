using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ESDP
{
    public partial class AdvancedColorPickerWindow : Window
    {
        private const int SpectrumWidth = 270;
        private const int SpectrumHeight = 165;

        private bool _updating = true;
        private bool _draggingSpectrum;
        private bool _draggingBrightness;
        private double _hue;
        private double _saturation;
        private double _lightness;
        private double _barHue = double.NaN;
        private double _barSaturation = double.NaN;
        // Independent of the selected lightness: build once and share a frozen bitmap.
        private static readonly Lazy<BitmapSource> SpectrumBitmap = new(CreateSpectrumBitmap);
        private byte _red;
        private byte _green;
        private byte _blue;
        private byte _alpha;

        public string SelectedHex { get; private set; } = "#000000";

        public AdvancedColorPickerWindow(string currentHex)
        {
            InitializeComponent();
            BasicColors.ItemsSource = BuildBasicColors();

            ParseHex(currentHex, out _alpha, out _red, out _green, out _blue);
            _saturation = 1;
            ReadRgbAsHsl();
            CurrentColorPreview.Fill = CreateBrush(
                _alpha, _red, _green, _blue);
            SpectrumImage.Source = SpectrumBitmap.Value;
            UpdateAllControls();
        }

        private static List<ColorOption> BuildBasicColors()
        {
            var colors = new List<ColorOption>();
            foreach (double brightness in new[]
                     { 1.0, 0.88, 0.72, 0.56, 0.40, 0.25 })
            {
                for (int column = 0; column < 12; column++)
                {
                    double hue = column * 30.0;
                    double saturation = column == 0 ? 0 : 0.85;
                    HsvToRgb(hue, saturation, brightness,
                             out byte r, out byte g, out byte b);
                    colors.Add(new ColorOption
                    {
                        Name = ToRgbHex(r, g, b),
                        Hex = ToRgbHex(r, g, b)
                    });
                }
            }

            foreach (byte gray in new byte[]
                     { 255, 224, 192, 160, 128, 96, 64, 32, 0 })
            {
                colors.Add(new ColorOption
                {
                    Name = ToRgbHex(gray, gray, gray),
                    Hex = ToRgbHex(gray, gray, gray)
                });
            }
            return colors;
        }

        private void BasicColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string hex }) return;
            ParseHex(hex, out _, out _red, out _green, out _blue);
            // "채우기 없음"에서 색을 다시 고르면 불투명한 일반 채우기로
            // 돌아가야 한다. 기존 알파 0을 유지하면 색을 골라도 계속 투명하다.
            _alpha = 255;
            ReadRgbAsHsl();
            UpdateAllControls();
        }

        private void SpectrumCanvas_MouseLeftButtonDown(
            object sender, MouseButtonEventArgs e)
        {
            _draggingSpectrum = true;
            SpectrumCanvas.Focus();
            SpectrumCanvas.CaptureMouse();
            SetSpectrumPosition(e.GetPosition(SpectrumCanvas));
            e.Handled = true;
        }

        private void SpectrumCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingSpectrum && e.LeftButton == MouseButtonState.Pressed)
                SetSpectrumPosition(e.GetPosition(SpectrumCanvas));
        }

        private void SpectrumCanvas_MouseLeftButtonUp(
            object sender, MouseButtonEventArgs e)
        {
            if (_draggingSpectrum) SetSpectrumPosition(e.GetPosition(SpectrumCanvas));
            _draggingSpectrum = false;
            SpectrumCanvas.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void SetSpectrumPosition(Point point)
        {
            if (_alpha == 0) _alpha = 255;
            double x = Math.Max(0, Math.Min(SpectrumWidth - 1, point.X));
            double y = Math.Max(0, Math.Min(SpectrumHeight - 1, point.Y));
            _hue = x / (SpectrumWidth - 1) * 360.0;
            _saturation = 1.0 - y / (SpectrumHeight - 1);
            HslToRgb(_hue, _saturation, _lightness,
                     out _red, out _green, out _blue);
            UpdateAllControls();
        }

        private void BrightnessCanvas_MouseLeftButtonDown(
            object sender, MouseButtonEventArgs e)
        {
            _draggingBrightness = true;
            BrightnessCanvas.Focus();
            BrightnessCanvas.CaptureMouse();
            SetBrightnessPosition(e.GetPosition(BrightnessCanvas));
            e.Handled = true;
        }

        private void BrightnessCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingBrightness && e.LeftButton == MouseButtonState.Pressed)
                SetBrightnessPosition(e.GetPosition(BrightnessCanvas));
        }

        private void BrightnessCanvas_MouseLeftButtonUp(
            object sender, MouseButtonEventArgs e)
        {
            if (_draggingBrightness) SetBrightnessPosition(e.GetPosition(BrightnessCanvas));
            _draggingBrightness = false;
            BrightnessCanvas.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void SetBrightnessPosition(Point point)
        {
            if (_alpha == 0) _alpha = 255;
            double y = Math.Max(0, Math.Min(SpectrumHeight - 1, point.Y));
            _lightness = 1.0 - y / (SpectrumHeight - 1);
            HslToRgb(_hue, _saturation, _lightness,
                     out _red, out _green, out _blue);
            UpdateAllControls();
        }

        private void RgbBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updating ||
                !byte.TryParse(RedBox.Text, out byte r) ||
                !byte.TryParse(GreenBox.Text, out byte g) ||
                !byte.TryParse(BlueBox.Text, out byte b)) return;
            _red = r;
            _green = g;
            _blue = b;
            ReadRgbAsHsl();
            UpdateAllControls(sender as TextBox);
        }

        private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updating) return;
            string? normalized = ColorOption.NormalizeHex(HexBox.Text);
            if (normalized == null) return;
            ParseHex(normalized, out _alpha, out _red, out _green, out _blue);
            ReadRgbAsHsl();
            UpdateAllControls(sender as TextBox);
        }

        private void ReadRgbAsHsl()
        {
            RgbToHsl(_red, _green, _blue, out double hue,
                out double saturation, out _lightness);
            // Hue is undefined for gray; both H and S are undefined at black/white.
            // Keep the user's last chromatic choice rather than jumping the pointer.
            if (_red != _green || _green != _blue) _hue = hue;
            if (_lightness > 0 && _lightness < 1) _saturation = saturation;
        }

        private void HslBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updating || !byte.TryParse(HueBox.Text, out byte h) ||
                !byte.TryParse(SaturationBox.Text, out byte s) ||
                !byte.TryParse(LightnessBox.Text, out byte l)) return;
            _hue = h / 255.0 * 360;
            _saturation = s / 255.0;
            _lightness = l / 255.0;
            HslToRgb(_hue, _saturation, _lightness, out _red, out _green, out _blue);
            UpdateAllControls(sender as TextBox);
        }

        private void ColorModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RgbInputs == null || HslInputs == null) return;
            bool hsl = ColorModel.SelectedIndex == 1;
            RgbInputs.Visibility = hsl ? Visibility.Collapsed : Visibility.Visible;
            HslInputs.Visibility = hsl ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ColorInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Normalize incomplete/out-of-range edits without interrupting valid typing.
            UpdateAllControls();
        }

        private void Picker_LostMouseCapture(object sender, MouseEventArgs e)
        {
            _draggingSpectrum = false;
            _draggingBrightness = false;
        }

        private void Picker_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            if (ReferenceEquals(sender, SpectrumCanvas))
            {
                if (e.Key == Key.Left || e.Key == Key.Right)
                    _hue = Math.Clamp(_hue + (e.Key == Key.Right ? step : -step) * 360 / (SpectrumWidth - 1), 0, 360);
                else
                    _saturation = Math.Clamp(_saturation + (e.Key == Key.Up ? step : -step) / (SpectrumHeight - 1), 0, 1);
            }
            else
            {
                if (e.Key is Key.Left or Key.Right) return;
                _lightness = Math.Clamp(_lightness + (e.Key == Key.Up ? step : -step) / (SpectrumHeight - 1), 0, 1);
            }
            HslToRgb(_hue, _saturation, _lightness, out _red, out _green, out _blue);
            UpdateAllControls();
            e.Handled = true;
        }

        private void TransparencySlider_ValueChanged(
            object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updating) return;
            _alpha = (byte)Math.Round(255 * (1 - e.NewValue / 100.0));
            UpdateAllControls();
        }

        private void TransparencyBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updating) return;
            string text = TransparencyBox.Text.Trim().TrimEnd('%').Trim();
            if (!double.TryParse(text, out double transparency) || !double.IsFinite(transparency)) return;
            transparency = Math.Max(0, Math.Min(100, transparency));
            _alpha = (byte)Math.Round(255 * (1 - transparency / 100.0));
            UpdateAllControls(sender as TextBox);
        }

        private void EyedropperButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            var picker = new ScreenEyedropperWindow();
            bool picked = picker.ShowDialog() == true;
            Show();
            Activate();
            if (!picked || picker.SelectedHex == null) return;
            ParseHex(picker.SelectedHex,
                     out _, out _red, out _green, out _blue);
            ReadRgbAsHsl();
            UpdateAllControls();
        }

        private void UpdateAllControls(TextBox? editing = null)
        {
            _updating = true;
            SetInput(RedBox, _red.ToString(), editing);
            SetInput(GreenBox, _green.ToString(), editing);
            SetInput(BlueBox, _blue.ToString(), editing);
            SetInput(HueBox, Math.Round(_hue / 360 * 255).ToString(), editing);
            SetInput(SaturationBox, Math.Round(_saturation * 255).ToString(), editing);
            SetInput(LightnessBox, Math.Round(_lightness * 255).ToString(), editing);
            SelectedHex = ToHex(_alpha, _red, _green, _blue);
            SetInput(HexBox, SelectedHex, editing);
            double transparency = Math.Round((1 - _alpha / 255.0) * 100);
            TransparencySlider.Value = transparency;
            SetInput(TransparencyBox, $"{transparency:0} %", editing);
            NewColorPreview.Fill = CreateBrush(_alpha, _red, _green, _blue);
            _updating = false;

            UpdateLightnessBar();
            UpdateIndicators();
        }

        private static void SetInput(TextBox box, string text, TextBox? editing)
        {
            if (!ReferenceEquals(box, editing) && box.Text != text) box.Text = text;
        }

        private static BitmapSource CreateSpectrumBitmap()
        {
            var pixels = new byte[SpectrumWidth * SpectrumHeight * 4];
            int index = 0;
            for (int y = 0; y < SpectrumHeight; y++)
            {
                double saturation = 1.0 - y / (double)(SpectrumHeight - 1);
                for (int x = 0; x < SpectrumWidth; x++)
                {
                    double hue = x / (double)(SpectrumWidth - 1) * 360.0;
                    HslToRgb(hue, saturation, 0.5,
                             out byte r, out byte g, out byte b);
                    pixels[index++] = b;
                    pixels[index++] = g;
                    pixels[index++] = r;
                    pixels[index++] = 255;
                }
            }

            var bitmap = new WriteableBitmap(
                SpectrumWidth, SpectrumHeight, 96, 96,
                PixelFormats.Bgra32, null);
            bitmap.WritePixels(
                new Int32Rect(0, 0, SpectrumWidth, SpectrumHeight),
                pixels, SpectrumWidth * 4, 0);
            bitmap.Freeze();
            return bitmap;
        }

        private void UpdateLightnessBar()
        {
            if (_barHue == _hue && _barSaturation == _saturation) return;
            HslToRgb(_hue, _saturation, 0.5, out byte r, out byte g, out byte b);
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(0, 1)
            };
            brush.GradientStops.Add(new GradientStop(Colors.White, 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(r, g, b), 0.5));
            brush.GradientStops.Add(new GradientStop(Colors.Black, 1));
            brush.Freeze();
            BrightnessBar.Fill = brush;
            _barHue = _hue;
            _barSaturation = _saturation;
        }

        private void UpdateIndicators()
        {
            Canvas.SetLeft(SpectrumIndicator,
                _hue / 360.0 * (SpectrumWidth - 1) - 5.5);
            Canvas.SetTop(SpectrumIndicator,
                (1 - _saturation) * (SpectrumHeight - 1) - 5.5);
            Canvas.SetLeft(BrightnessIndicator, 23);
            Canvas.SetTop(BrightnessIndicator,
                (1 - _lightness) * (SpectrumHeight - 1));
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedHex = ToHex(_alpha, _red, _green, _blue);
            DialogResult = true;
        }

        private static Brush CreateBrush(byte a, byte r, byte g, byte b)
            => new SolidColorBrush(Color.FromArgb(a, r, g, b));

        private static string ToRgbHex(byte r, byte g, byte b)
            => $"#{r:X2}{g:X2}{b:X2}";

        private static string ToHex(byte a, byte r, byte g, byte b)
            => a == 255
                ? ToRgbHex(r, g, b)
                : $"#{a:X2}{r:X2}{g:X2}{b:X2}";

        private static void ParseHex(
            string? value, out byte a, out byte r, out byte g, out byte b)
        {
            string normalized = ColorOption.NormalizeHex(value) ?? "#000000";
            if (normalized.Length == 9)
            {
                a = Convert.ToByte(normalized.Substring(1, 2), 16);
                r = Convert.ToByte(normalized.Substring(3, 2), 16);
                g = Convert.ToByte(normalized.Substring(5, 2), 16);
                b = Convert.ToByte(normalized.Substring(7, 2), 16);
            }
            else
            {
                a = 255;
                r = Convert.ToByte(normalized.Substring(1, 2), 16);
                g = Convert.ToByte(normalized.Substring(3, 2), 16);
                b = Convert.ToByte(normalized.Substring(5, 2), 16);
            }
        }

        private static void RgbToHsl(
            byte r, byte g, byte b,
            out double hue, out double saturation, out double lightness)
        {
            double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double delta = max - min;
            hue = 0;
            if (delta > 0)
            {
                if (max == rd) hue = 60 * (((gd - bd) / delta) % 6);
                else if (max == gd) hue = 60 * (((bd - rd) / delta) + 2);
                else hue = 60 * (((rd - gd) / delta) + 4);
            }
            if (hue < 0) hue += 360;
            lightness = (max + min) / 2;
            saturation = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * lightness - 1));
        }

        private static void HslToRgb(double hue, double saturation, double lightness,
            out byte r, out byte g, out byte b)
        {
            // HSL, not HSV: L=1 must be white for every hue/saturation.
            hue = (hue % 360 + 360) % 360;
            saturation = Math.Clamp(saturation, 0, 1);
            lightness = Math.Clamp(lightness, 0, 1);
            double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            double x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
            double m = lightness - c / 2;
            (double rd, double gd, double bd) = hue switch
            {
                < 60 => (c, x, 0d), < 120 => (x, c, 0d),
                < 180 => (0d, c, x), < 240 => (0d, x, c),
                < 300 => (x, 0d, c), _ => (c, 0d, x)
            };
            r = (byte)Math.Round((rd + m) * 255);
            g = (byte)Math.Round((gd + m) * 255);
            b = (byte)Math.Round((bd + m) * 255);
        }

        private static void HsvToRgb(
            double hue, double saturation, double value,
            out byte r, out byte g, out byte b)
        {
            hue %= 360;
            if (hue < 0) hue += 360;
            double c = value * saturation;
            double x = c * (1 - Math.Abs((hue / 60.0) % 2 - 1));
            double m = value - c;
            (double rd, double gd, double bd) = hue switch
            {
                < 60 => (c, x, 0d),
                < 120 => (x, c, 0d),
                < 180 => (0d, c, x),
                < 240 => (0d, x, c),
                < 300 => (x, 0d, c),
                _ => (c, 0d, x)
            };
            r = (byte)Math.Round((rd + m) * 255);
            g = (byte)Math.Round((gd + m) * 255);
            b = (byte)Math.Round((bd + m) * 255);
        }
    }
}
