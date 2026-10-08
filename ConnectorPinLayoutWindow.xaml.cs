using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ESDP
{
    public partial class ConnectorPinLayoutWindow : Window
    {
        private readonly ConnectorPinVisualInfo? _visual;
        private readonly List<ESDP.Models.CablePin> _pins;
        private double _imageWidth;
        private double _imageHeight;

        public ConnectorPinLayoutWindow(
            string portName, string connectorName, string? catalogKey,
            IEnumerable<ESDP.Models.CablePin> pins)
        {
            InitializeComponent();
            _pins = pins.ToList();
            _visual = ConnectorCatalogService.Instance.ResolveVisual(connectorName, catalogKey);
            TitleText.Text = $"{portName} · {connectorName}";
            ShowVisual();
        }

        private void ShowVisual()
        {
            LayoutImage.Source = _visual?.Image;
            bool hasImage = _visual?.Image != null;
            LayoutViewbox.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
            NoImageText.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
            EditPositionsButton.IsEnabled = hasImage && !string.IsNullOrWhiteSpace(_visual?.CatalogKey);
            if (!hasImage)
            {
                GuideText.Text = "커넥터 선택창에서 핀 배열 사진을 먼저 등록하세요.";
                return;
            }

            (_imageWidth, _imageHeight) = ImageDimensions(_visual!.Image!);
            CoordinateHost.Width = _imageWidth;
            CoordinateHost.Height = _imageHeight;
            LayoutImage.Width = MarksCanvas.Width = _imageWidth;
            LayoutImage.Height = MarksCanvas.Height = _imageHeight;
            DrawMarks();
        }

        private void DrawMarks()
        {
            MarksCanvas.Children.Clear();
            var positions = ConnectorCatalogService.Instance.GetPinPositions(_visual!.CatalogKey);
            var positionLookup = positions
                .GroupBy(item => item.PinNumber, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            int displayed = 0;
            int colored = 0;
            int unmapped = 0;
            double storedDiameterPoints = ConnectorCatalogService.Instance
                .GetPinMarkerDiameterPoints(_visual.CatalogKey);
            double radius = storedDiameterPoints > 0
                ? Math.Clamp(storedDiameterPoints * 96.0 / 72.0 / 2.0,
                    2.0, Math.Min(_imageWidth, _imageHeight) * 0.08)
                : Math.Max(5, Math.Min(_imageWidth, _imageHeight) * 0.012);
            foreach (var pin in _pins.Where(item =>
                         !string.IsNullOrWhiteSpace(item.PinNum)))
            {
                if (!positionLookup.TryGetValue(pin.PinNum.Trim(), out var position))
                {
                    unmapped++;
                    continue;
                }
                bool hasColor = !string.IsNullOrWhiteSpace(pin.MarkColorHex);
                string markerColor = hasColor
                    ? ColorOption.NormalizeHex(pin.MarkColorHex) ?? "#000000"
                    : "#000000";
                string pinNumberText = pin.PinNum.Trim();
                double markerRadius = radius + Math.Min(2.5,
                    Math.Max(0, pinNumberText.Length - 1) * 1.1);
                string signalName = string.IsNullOrWhiteSpace(pin.PinName)
                    ? "신호명 없음" : pin.PinName.Trim();
                var marker = new Border
                {
                    Width = markerRadius * 2,
                    Height = markerRadius * 2,
                    CornerRadius = new CornerRadius(markerRadius),
                    Padding = new Thickness(0),
                    Background = ColorOption.BrushFromHex(markerColor),
                    BorderBrush = hasColor ? Brushes.Black : Brushes.White,
                    BorderThickness = new Thickness(Math.Max(1, radius * 0.1)),
                    ToolTip = $"핀 {pin.PinNum.Trim()}\n신호: {signalName}",
                    Child = new TextBlock
                    {
                        Text = pinNumberText,
                        Foreground = PinNumberForeground(markerColor),
                        FontSize = Math.Max(9, radius * 0.72 + 2),
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Center
                    }
                };
                Canvas.SetLeft(marker, position.X * _imageWidth - markerRadius);
                Canvas.SetTop(marker, position.Y * _imageHeight - markerRadius);
                MarksCanvas.Children.Add(marker);
                displayed++;
                if (hasColor) colored++;
            }
            string summary = $"핀 {displayed}개 표시 · 색 지정 {colored}개 · 기본 검정 {displayed - colored}개";
            GuideText.Text = unmapped > 0
                ? $"{summary} · 위치 미설정 {unmapped}개"
                : summary;
        }

        private static Brush PinNumberForeground(string colorHex)
        {
            var color = (Color)ColorConverter.ConvertFromString(colorHex);
            double luminance = color.R * 0.2126 + color.G * 0.7152 + color.B * 0.0722;
            return luminance < 118 ? Brushes.White : Brushes.Black;
        }

        private void EditPositionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_visual?.Image == null) return;
            var editor = new ConnectorPinPositionEditorWindow(
                _visual.CatalogKey, _visual.ConnectorName, _visual.Image,
                ConnectorCatalogService.Instance.GetPinPositions(_visual.CatalogKey))
            { Owner = this };
            if (editor.ShowDialog() == true) DrawMarks();
        }

        private static (double Width, double Height) ImageDimensions(ImageSource image)
        {
            if (image is BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
                return (bitmap.PixelWidth, bitmap.PixelHeight);
            return (900, 650);
        }
    }
}
