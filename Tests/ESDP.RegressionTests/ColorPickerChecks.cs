using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ESDP;

static class ColorPickerChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception("Color picker: " + message); }
    static object? Call(AdvancedColorPickerWindow w, string name, params object?[] args)
        => typeof(AdvancedColorPickerWindow).GetMethod(name, Private)!.Invoke(w, args);
    static T Field<T>(AdvancedColorPickerWindow w, string name)
        => (T)typeof(AdvancedColorPickerWindow).GetField(name, Private)!.GetValue(w)!;
    static T Control<T>(AdvancedColorPickerWindow w, string name) => (T)w.FindName(name);

    public static void Run(bool render)
    {
        var toHsl = typeof(AdvancedColorPickerWindow).GetMethod("RgbToHsl", BindingFlags.Static | BindingFlags.NonPublic)!;
        var toRgb = typeof(AdvancedColorPickerWindow).GetMethod("HslToRgb", BindingFlags.Static | BindingFlags.NonPublic)!;
        var random = new Random(7421);
        for (int i = 0; i < 4000; i++)
        {
            byte r = (byte)random.Next(256), g = (byte)random.Next(256), b = (byte)random.Next(256);
            object?[] hs = { r, g, b, 0d, 0d, 0d }; toHsl.Invoke(null, hs);
            object?[] rgb = { hs[3], hs[4], hs[5], (byte)0, (byte)0, (byte)0 }; toRgb.Invoke(null, rgb);
            Check((byte)rgb[3]! == r && (byte)rgb[4]! == g && (byte)rgb[5]! == b, "RGB/HSL round-trip changed RGB");
        }

        var w = new AdvancedColorPickerWindow("#FF0000");
        var spectrum = Control<Image>(w, "SpectrumImage").Source;
        Check(spectrum is BitmapSource { IsFrozen: true }, "spectrum is not frozen");
        var pixels = new byte[270 * 165 * 4]; ((BitmapSource)spectrum).CopyPixels(pixels, 270 * 4, 0);
        Check(pixels[2] == 255 && pixels[1] == 0 && pixels[0] == 0, "spectrum red endpoint wrong");
        int gray = (164 * 270) * 4;
        Check(pixels[gray] == 128 && pixels[gray + 1] == 128 && pixels[gray + 2] == 128, "spectrum gray endpoint wrong");
        var bar = Control<System.Windows.Shapes.Rectangle>(w, "BrightnessBar");
        var gradient = (LinearGradientBrush)bar.Fill;
        Check(gradient.GradientStops.Count == 3 && gradient.GradientStops[0].Color == Colors.White &&
              gradient.GradientStops[1].Color == Colors.Red && gradient.GradientStops[2].Color == Colors.Black,
              "lightness scale must be white / chosen midpoint / black");
        Call(w, "SetBrightnessPosition", new Point(0, 0));
        Check(w.SelectedHex == "#FFFFFF", "top of scale not white");
        Call(w, "SetBrightnessPosition", new Point(0, 164));
        Check(w.SelectedHex == "#000000", "bottom of scale not black");
        Call(w, "SetBrightnessPosition", new Point(0, 82));
        Check(w.SelectedHex == "#FF0000", "midpoint did not retain hue/saturation");
        Call(w, "SetBrightnessPosition", new Point(0, 41));
        Check(w.SelectedHex == "#FF8080", "lightened red not pastel HSL red");
        Check(ReferenceEquals(gradient, bar.Fill), "lightness-only changes rebuilt bar");

        // Picking hue at black/white still records the pointer, even though final RGB is achromatic.
        Call(w, "SetBrightnessPosition", new Point(0, 164));
        Call(w, "SetSpectrumPosition", new Point(269d / 3, 0));
        Check(w.SelectedHex == "#000000", "spectrum altered requested black lightness");
        Call(w, "SetBrightnessPosition", new Point(0, 82));
        Check(w.SelectedHex == "#00FF00", "black erased chosen green hue");
        Control<TextBox>(w, "HexBox").Text = "#FFFFFF";
        Check(Math.Abs(Field<double>(w, "_hue") - 120) < .001 && Field<double>(w, "_saturation") == 1,
              "white RGB input erased H/S");
        Call(w, "SetBrightnessPosition", new Point(0, 82));
        Check(w.SelectedHex == "#00FF00", "white erased chromatic selection");
        Control<TextBox>(w, "HexBox").Text = "#808080";
        Check(Math.Abs(Field<double>(w, "_hue") - 120) < .001 && Field<double>(w, "_saturation") == 0, "gray hue handling wrong");
        Control<TextBox>(w, "HexBox").Text = "#804472C4";
        Check(w.SelectedHex == "#804472C4", "HEX/RGB alpha round-trip changed original");
        Control<TextBox>(w, "RedBox").Text = "128";
        Check(Field<byte>(w, "_red") == 128 && Field<byte>(w, "_alpha") == 128, "RGB edit did not preserve alpha");

        Control<ComboBox>(w, "ColorModel").SelectedIndex = 1;
        Check(Control<Grid>(w, "RgbInputs").Visibility == Visibility.Collapsed &&
              Control<Grid>(w, "HslInputs").Visibility == Visibility.Visible, "HSL inputs not switched");
        Control<TextBox>(w, "HueBox").Text = "85";
        Control<TextBox>(w, "SaturationBox").Text = "255";
        Control<TextBox>(w, "LightnessBox").Text = "128";
        Check(w.SelectedHex == "#8001FF01", "HSL 0-255 input does not produce correct RGB");
        var hueBox = Control<TextBox>(w, "HueBox");
        hueBox.Text = "999";
        Check(w.SelectedHex == "#8001FF01", "invalid HSL edit changed selected color");
        Call(w, "ColorInput_LostKeyboardFocus", hueBox, null);
        Check(hueBox.Text == "85", "invalid edit was not normalized");
        Call(w, "SetSpectrumPosition", new Point(269, 0));
        Check(Field<double>(w, "_hue") == 360 && Control<TextBox>(w, "HueBox").Text == "255", "hue seam jumped");

        var time = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) Call(w, "SetBrightnessPosition", new Point(0, i % 165));
        time.Stop();
        Check(ReferenceEquals(spectrum, Control<Image>(w, "SpectrumImage").Source), "drag rebuilt spectrum bitmap");
        var second = new AdvancedColorPickerWindow("#000000");
        Check(ReferenceEquals(spectrum, Control<Image>(second, "SpectrumImage").Source), "windows did not share immutable spectrum");
        Call(second, "SetSpectrumPosition", new Point(269d * 2 / 3, 0));
        Call(second, "SetBrightnessPosition", new Point(0, 82));
        Check(second.SelectedHex == "#0000FF", "initial black selection cannot recover blue");
        var transparent = new AdvancedColorPickerWindow("#00FFFFFF");
        Call(transparent, "SetSpectrumPosition", new Point(0, 0));
        Check(Field<byte>(transparent, "_alpha") == 255, "no-fill did not become opaque on picking color");
        Control<TextBox>(transparent, "TransparencyBox").Text = "NaN";
        Check(Field<byte>(transparent, "_alpha") == 255, "invalid transparency changed alpha");

        if (render)
        {
            foreach (string theme in new[] { AppThemeManager.SoftOrange, AppThemeManager.Dark })
            {
                AppThemeManager.Apply(theme);
                foreach (var item in new[] { ("custom-rgb", 0), ("custom-hsl", 1) })
                {
                    var picker = new AdvancedColorPickerWindow("#ED7D31");
                    Control<TabControl>(picker, "ColorTabs").SelectedIndex = 1;
                    Control<ComboBox>(picker, "ColorModel").SelectedIndex = item.Item2;
                    Render(picker, theme + "-" + item.Item1);
                    picker.Close();
                }
            }
            AppThemeManager.Apply(AppSettings.ThemeName);
        }
        w.Close(); second.Close(); transparent.Close();
        Console.WriteLine($"PASS: HSL 4000 RGB round-trips; white/color/black scale; achromatic H/S retention; input/alpha synchronization; shared spectrum; 1000 lightness updates {time.Elapsed.TotalMilliseconds:F1}ms");
    }

    static void Render(AdvancedColorPickerWindow w, string name)
    {
        // Arrange detached content at minimum client size; no visible application launch.
        var root = (FrameworkElement)w.Content;
        root.Measure(new Size(550, 500)); root.Arrange(new Rect(0, 0, 550, 500)); root.UpdateLayout();
        foreach (string nameToCheck in new[] { "SpectrumCanvas", "BrightnessCanvas", "HexBox", "TransparencyBox", "NewColorPreview", "CurrentColorPreview" })
        {
            var element = Control<FrameworkElement>(w, nameToCheck);
            var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
            Check(bounds.Left >= 0 && bounds.Right <= 550 && bounds.Top >= 0 && bounds.Bottom <= 500, "minimum window clips " + nameToCheck);
        }
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(w.Background, null, new Rect(0, 0, 550, 500));
        var bitmap = new RenderTargetBitmap(550, 500, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(background); bitmap.Render(root);
        string path = Path.Combine(AppContext.BaseDirectory, "qa"); Directory.CreateDirectory(path);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(path, name + ".png")); encoder.Save(stream);
    }
}
