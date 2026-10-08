using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ESDP.Models;
using Point = System.Windows.Point;

namespace ESDP;

public partial class ShapePropertiesWindow : Window
{
    private readonly CanvasShape _original;
    private readonly Dictionary<string, TextBox> _numbers = new();
    private readonly ComboBox _kind = new(), _horizontal = new(), _vertical = new(), _textFit = new(), _lineStyle = new(), _stretch = new();
    private readonly TextBox _text = new(), _family = new();
    private readonly CheckBox _flipH = new() { Content = "좌우 반전" }, _flipV = new() { Content = "상하 반전" };
    private readonly CheckBox _bold = new() { Content = "굵게" }, _italic = new() { Content = "기울임" }, _underline = new() { Content = "밑줄" };
    private readonly CheckBox _safeText = new() { Content = "도형 안쪽의 안전한 글자 영역 사용" };
    private readonly CheckBox _noFill = new() { Content = "채우기 없음" };
    private string _fill, _stroke, _textColor, _imageData, _imageName;
    private bool _ready;
    public CanvasShape ResultShape { get; private set; }

    public ShapePropertiesWindow(CanvasShape shape)
    {
        InitializeComponent();
        _original = ShapeCatalog.Clone(shape); ResultShape = ShapeCatalog.Clone(shape);
        _fill = shape.FillHex; _stroke = shape.StrokeHex; _textColor = shape.TextColorHex;
        _imageData = shape.ImageDataBase64; _imageName = shape.ImageFileName;
        var geometry = Tab("모양 / 크기");
        var kinds = ShapeCatalog.All.Where(d => ShapeCatalog.IsLine(shape.Kind)
            ? ShapeCatalog.IsLine(d.Kind) && d.Kind is "Line" or "ElbowLine"
            : d.Kind != "Image" && !ShapeCatalog.IsLine(d.Kind)).ToList();
        if (shape.Kind == "Image") kinds = ShapeCatalog.All.Where(d => d.Kind == "Image").ToList();
        _kind.ItemsSource = kinds; _kind.DisplayMemberPath = "Name"; _kind.SelectedValuePath = "Kind";
        _kind.SelectedValue = shape.Kind;
        Row(geometry, "도형 종류", _kind);
        Number(geometry, "X", "X 좌표 (px)", shape.X); Number(geometry, "Y", "Y 좌표 (px)", shape.Y);
        Number(geometry, "W", "너비 (px)", shape.Width); Number(geometry, "H", "높이 (px)", shape.Height);
        Number(geometry, "Angle", "회전 각도 (°)", shape.RotationAngle);
        _flipH.IsChecked = shape.FlipHorizontal; _flipV.IsChecked = shape.FlipVertical;
        geometry.Children.Add(_flipH); geometry.Children.Add(_flipV);
        if (ShapeCatalog.Adjustable(shape))
        {
            Number(geometry, "A1", shape.Kind == "RoundedRectangle" ? "모서리 둥근 정도 (%)" : "모양 조절 1 (%)", ShapeCatalog.First(shape) * 100);
            if (ShapeCatalog.IsArrow(shape.Kind)) Number(geometry, "A2", "몸통 안쪽 여백 (%)", ShapeCatalog.Second(shape) * 100);
        }
        var text = Tab("텍스트");
        _text.Text = shape.Text; _text.AcceptsReturn = true; _text.TextWrapping = TextWrapping.Wrap;
        _text.MinHeight = 100; _text.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        text.Children.Add(new TextBlock { Text = "도형 안의 글자", Margin = new Thickness(0,0,0,6) }); text.Children.Add(_text);
        _family.Text = shape.FontFamily; Row(text, "글꼴", _family); Number(text, "Font", "글자 크기 (px)", shape.FontSize);
        var effects = new WrapPanel { Margin = new Thickness(0,6,0,8) };
        _bold.IsChecked = shape.TextBold; _italic.IsChecked = shape.TextItalic; _underline.IsChecked = shape.TextUnderline;
        foreach (var box in new[] { _bold, _italic, _underline }) { box.Margin = new Thickness(0,0,14,0); effects.Children.Add(box); }
        text.Children.Add(effects);
        Combo(_horizontal, new[] { ("왼쪽", "Left"), ("가운데", "Center"), ("오른쪽", "Right"), ("양쪽", "Justify") }, shape.TextAlignment);
        Combo(_vertical, new[] { ("위", "Top"), ("가운데", "Center"), ("아래", "Bottom") }, shape.TextVerticalAlignment);
        Row(text, "가로 정렬", _horizontal); Row(text, "세로 정렬", _vertical);
        Combo(_textFit, new[] { ("줄바꿈 (크기 유지)", "Clip"), ("넘치면 글자 축소", "Shrink") }, shape.TextFit);
        Row(text, "글자 맞춤", _textFit);
        Number(text, "Margin", "내부 여백 (px)", shape.TextMargin);
        _safeText.IsChecked = shape.UseShapeTextArea; text.Children.Add(_safeText);
        ColorButton(text, "글자 색", () => _textColor, value => _textColor = value);
        var style = Tab("채우기 / 선");
        ColorButton(style, "채우기 색", () => _fill, value => _fill = value);
        _noFill.IsChecked = shape.FillHex.StartsWith("#00", StringComparison.OrdinalIgnoreCase);
        style.Children.Add(_noFill);
        ColorButton(style, "윤곽선 색", () => _stroke, value => _stroke = value);
        Number(style, "Stroke", "윤곽선 굵기 (px)", shape.StrokeThickness);
        Combo(_lineStyle, new[] { ("실선", "Solid"), ("점선", "Dash"), ("도트", "Dot") }, shape.LineStyle);
        if (shape.LineStyle == "Double") _lineStyle.Items.Add(new ComboBoxItem { Content = "이중선", Tag = "Double", IsSelected = true });
        Row(style, "윤곽선 형태", _lineStyle);
        if (ShapeCatalog.IsLine(shape.Kind))
            Number(style, "Head", "화살촉 크기 (px)", shape.ArrowHeadSize);
        if (shape.Kind == "Image")
        {
            var image = Tab("이미지");
            Combo(_stretch, new[] { ("자유 변형", "Fill"), ("비율 유지", "Uniform"), ("영역 채우기", "UniformToFill") }, shape.ImageStretch);
            Row(image, "맞춤 방식", _stretch); Number(image, "Opacity", "불투명도 (%)", shape.ImageOpacity * 100);
            var replace = new Button { Content = "이미지 교체...", Height = 32, Margin = new Thickness(0,10,0,0) };
            replace.Click += (_, _) =>
            {
                if (!ProjectImageService.TryImport(this, out var imported)) return;
                _imageData = imported.Base64; _imageName = imported.FileName; Preview();
            };
            image.Children.Add(replace);
        }
        foreach (var box in _numbers.Values.Concat(new[] { _text, _family })) box.TextChanged += (_, _) => Preview();
        foreach (var combo in new[] { _kind, _horizontal, _vertical, _textFit, _lineStyle, _stretch }) combo.SelectionChanged += (_, _) => Preview();
        foreach (var box in new[] { _flipH, _flipV, _bold, _italic, _underline, _safeText, _noFill })
        { box.Checked += (_, _) => Preview(); box.Unchecked += (_, _) => Preview(); }
        _ready = true; Preview();
    }
    private StackPanel Tab(string title)
    {
        var panel = new StackPanel { Margin = new Thickness(14) };
        OptionsTabs.Items.Add(new TabItem { Header = title, Content = new ScrollViewer
        { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = panel } });
        return panel;
    }
    private static void Row(Panel parent, string title, Control value)
    {
        var row = new Grid { Margin = new Thickness(0,6,0,6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        value.MinHeight = 28; Grid.SetColumn(value, 1); row.Children.Add(value); parent.Children.Add(row);
    }
    private void Number(Panel panel, string key, string label, double value)
    {
        var box = new TextBox { Text = value.ToString("0.###"), VerticalContentAlignment = VerticalAlignment.Center };
        _numbers.Add(key, box); Row(panel, label, box);
    }
    private static void Combo(ComboBox combo, (string Label, string Value)[] items, string selected)
    {
        foreach (var item in items) combo.Items.Add(new ComboBoxItem { Content = item.Label, Tag = item.Value, IsSelected = item.Value == selected });
    }
    private static string Selected(ComboBox combo, string fallback) => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
    private void ColorButton(Panel parent, string title, Func<string> get, Action<string> set)
    {
        var button = new Button { Content = title + " 선택...", Height = 32, Margin = new Thickness(0,6,0,6) };
        button.Click += (_, _) =>
        {
            if (!ColorPickerWindow.TryPick(this, get(), out string hex, title)) return;
            set(hex); if (title == "채우기 색") _noFill.IsChecked = false; Preview();
        };
        parent.Children.Add(button);
    }
    private CanvasShape? Build(bool report)
    {
        try
        {
            double N(string key, double min, double max, double fallback = 0)
            {
                if (!_numbers.TryGetValue(key, out var box)) return fallback;
                if (!double.TryParse(box.Text, out double value) || !double.IsFinite(value) || value < min || value > max)
                    throw new ArgumentException($"{key}: {min} ~ {max} 범위의 숫자를 입력해 주세요.");
                return value;
            }
            var result = ShapeCatalog.Clone(_original);
            result.X = N("X", -1000000, 1000000); result.Y = N("Y", -1000000, 1000000);
            bool line = ShapeCatalog.IsLine(result.Kind);
            double oldWidth = result.Width, oldHeight = result.Height;
            result.Width = N("W", line ? -100000 : 1, 100000); result.Height = N("H", line ? -100000 : 1, 100000);
            if (line && Math.Abs(result.Width) + Math.Abs(result.Height) < .01) throw new ArgumentException("선의 길이는 0보다 커야 합니다.");
            if (result.Kind == "ElbowLine")
                foreach (var p in result.LinePoints)
                {
                    p.X *= Math.Abs(oldWidth) > .01 ? result.Width / oldWidth : 1;
                    p.Y *= Math.Abs(oldHeight) > .01 ? result.Height / oldHeight : 1;
                }
            result.RotationAngle = ShapeGeometry.NormalizeAngle(N("Angle", -36000, 36000));
            result.FlipHorizontal = _flipH.IsChecked == true; result.FlipVertical = _flipV.IsChecked == true;
            string kind = _kind.SelectedValue as string ?? result.Kind;
            if (kind != result.Kind) { result.Kind = kind; result.Adjustment1 = null; result.Adjustment2 = null; result.CustomVertices.Clear(); result.LinePoints.Clear(); }
            else
            {
                if (_numbers.ContainsKey("A1")) result.Adjustment1 = N("A1", 0, 100) / 100;
                if (_numbers.ContainsKey("A2")) result.Adjustment2 = N("A2", 5, 45) / 100;
            }
            result.FontFamily = string.IsNullOrWhiteSpace(_family.Text) ? "맑은 고딕" : _family.Text.Trim();
            result.FontSize = N("Font", 4, 300); result.TextMargin = N("Margin", 0, 100);
            result.Text = _text.Text; result.TextBold = _bold.IsChecked == true; result.TextItalic = _italic.IsChecked == true; result.TextUnderline = _underline.IsChecked == true;
            result.TextAlignment = Selected(_horizontal, "Center"); result.TextVerticalAlignment = Selected(_vertical, "Center"); result.UseShapeTextArea = _safeText.IsChecked == true;
            result.TextFit = Selected(_textFit, "Clip");
            result.TextColorHex = _textColor;
            if (result.Text != _original.Text || result.FontSize != _original.FontSize || result.FontFamily != _original.FontFamily
                || result.TextBold != _original.TextBold || result.TextItalic != _original.TextItalic || result.TextUnderline != _original.TextUnderline
                || result.TextColorHex != _original.TextColorHex || result.TextAlignment != _original.TextAlignment)
                result.TextRtfBase64 = "";
            result.FillHex = _noFill.IsChecked == true ? "#00FFFFFF" : _fill; result.StrokeHex = _stroke;
            result.StrokeThickness = N("Stroke", 0, 20); result.ArrowHeadSize = N("Head", 1, 120, _original.ArrowHeadSize);
            result.LineStyle = Selected(_lineStyle, _original.LineStyle);
            result.ImageStretch = Selected(_stretch, result.ImageStretch); result.ImageOpacity = N("Opacity", 0, 100, _original.ImageOpacity * 100) / 100;
            result.ImageDataBase64 = _imageData; result.ImageFileName = _imageName;
            return result;
        }
        catch (ArgumentException ex)
        {
            if (report) MessageBox.Show(this, ex.Message, "도형 설정", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }
    private void Preview()
    {
        if (!_ready || Build(false) is not { } result) return;
        PreviewCanvas.Children.Clear();
        double width = Math.Max(1, Math.Abs(result.Width)), height = Math.Max(1, Math.Abs(result.Height));
        var rotated = new RotateTransform(result.RotationAngle, width / 2, height / 2).TransformBounds(new Rect(0, 0, width, height));
        double scale = Math.Min(200 / Math.Max(1, rotated.Width), 130 / Math.Max(1, rotated.Height));
        var canvas = new Canvas { Width = width, Height = height };
        if (result.Kind == "Image") canvas.Children.Add(new Image { Width = width, Height = height,
            Source = ProjectImageService.Load(result.ImageDataBase64), Stretch = ProjectImageService.ParseStretch(result.ImageStretch),
            Opacity = result.ImageOpacity, RenderTransformOrigin = new Point(.5,.5),
            RenderTransform = new ScaleTransform(result.FlipHorizontal ? -1 : 1, result.FlipVertical ? -1 : 1) });
        else if (ShapeCatalog.IsLine(result.Kind))
        {
            var points = result.Kind == "ElbowLine" && result.LinePoints.Count >= 2
                ? result.LinePoints.Select(p => new Point(p.X, p.Y)).ToArray()
                : new[] { new Point(0,0), new Point(result.Width, result.Height) };
            canvas.Children.Add(new System.Windows.Shapes.Polyline { Points = new PointCollection(points),
                Stroke = ColorOption.BrushFromHex(result.StrokeHex), StrokeThickness = result.StrokeThickness });
        }
        else canvas.Children.Add(ShapeCatalog.Outline(result, ColorOption.BrushFromHex(result.FillHex), ColorOption.BrushFromHex(result.StrokeHex)));
        if (!string.IsNullOrWhiteSpace(result.Text)) MainWindow.AddShapeTextViewer(canvas, result);
        var transforms = new TransformGroup();
        transforms.Children.Add(new RotateTransform(result.RotationAngle, width/2, height/2));
        transforms.Children.Add(new ScaleTransform(scale, scale));
        canvas.RenderTransform = transforms;
        Canvas.SetLeft(canvas, 120 - width * scale / 2); Canvas.SetTop(canvas, 90 - height * scale / 2);
        PreviewCanvas.Children.Add(canvas);
    }
    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (Build(true) is not { } result) return;
        ResultShape = result; DialogResult = true;
    }
}
