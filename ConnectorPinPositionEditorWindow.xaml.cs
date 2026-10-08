using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ESDP
{
    public partial class ConnectorPinPositionEditorWindow : Window
    {
        private const string LowercasePinSequence = "abcdefghijklmnopqrstuvwxyz";
        private const string UppercasePinSequence = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        private sealed record DetectedPinCircle(double X, double Y, double Diameter);
        private sealed record PinPositionUndoState(
            List<ConnectorPinPosition> Positions, string PinNumber);
        private readonly string _catalogKey;
        private readonly ObservableCollection<ConnectorPinPosition> _positions;
        private readonly double _imageWidth;
        private readonly double _imageHeight;
        private readonly Popup _pinPreviewPopup;
        private readonly TextBlock _pinPreviewText;
        private byte[]? _snapPixels;
        private int _snapPixelWidth;
        private int _snapPixelHeight;
        private int _snapStride;
        private readonly List<DetectedPinCircle> _detectedPinCircles = new();
        private readonly Stack<PinPositionUndoState> _undoHistory = new();
        private Border? _hoverSnapGuide;
        private double _markerDiameterPoints;
        private bool _updatingMarkerSize;

        public ConnectorPinPositionEditorWindow(
            string catalogKey, string connectorName, ImageSource image,
            IEnumerable<ConnectorPinPosition> positions)
        {
            InitializeComponent();
            _catalogKey = catalogKey;
            _positions = new ObservableCollection<ConnectorPinPosition>(positions.Select(Clone));
            PositionsList.ItemsSource = _positions;
            TitleText.Text = $"{connectorName} 핀 위치 설정";
            LayoutImage.Source = image;

            (_imageWidth, _imageHeight) = ImageDimensions(image);
            CoordinateHost.Width = _imageWidth;
            CoordinateHost.Height = _imageHeight;
            LayoutImage.Width = PinsCanvas.Width = _imageWidth;
            LayoutImage.Height = PinsCanvas.Height = _imageHeight;
            PrepareSnapPixels(image);
            double storedMarkerSize = ConnectorCatalogService.Instance
                .GetPinMarkerDiameterPoints(catalogKey);
            double detectedMarkerSize = DetectDefaultMarkerDiameterPoints();
            _markerDiameterPoints = storedMarkerSize > 0
                ? storedMarkerSize
                : detectedMarkerSize;
            _updatingMarkerSize = true;
            MarkerSizeBox.Text = _markerDiameterPoints.ToString("0.#");
            DetectedSizeText.Text = _detectedPinCircles.Count > 0
                ? $"(인식 기본값 {detectedMarkerSize:0.#}pt)"
                : "(기본값)";
            _updatingMarkerSize = false;
            _pinPreviewText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            };
            _pinPreviewPopup = new Popup
            {
                Placement = PlacementMode.RelativePoint,
                PlacementTarget = PinsCanvas,
                AllowsTransparency = true,
                IsHitTestVisible = false,
                Child = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(215, 0x25, 0x2A, 0x30)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 3, 6, 3),
                    Child = _pinPreviewText
                }
            };
            PinNumberBox.TextChanged += (_, _) => UpdatePinPreviewText();
            RedrawPositions();
            Loaded += (_, _) => PinNumberBox.Focus();
        }

        private static (double Width, double Height) ImageDimensions(ImageSource image)
        {
            if (image is BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
                return (bitmap.PixelWidth, bitmap.PixelHeight);
            return (900, 650);
        }

        private void PinsCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            string pinNumber = PinNumberBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(pinNumber))
            {
                MessageBox.Show("먼저 핀 번호를 입력하세요.", "핀 위치",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                PinNumberBox.Focus();
                return;
            }

            Point point = e.GetPosition(PinsCanvas);
            if (SnapToPinCheck.IsChecked == true)
                point = SnapToNearestPinCenter(point);
            PushPositionUndoCheckpoint();
            bool sparePin = pinNumber.Equals("EX", StringComparison.OrdinalIgnoreCase);
            var sameNamePosition = sparePin ? null : _positions.FirstOrDefault(item => item.PinNumber.Equals(
                pinNumber, StringComparison.Ordinal));
            double overwriteRadius = Math.Max(5, Math.Min(_imageWidth, _imageHeight) * 0.012);
            var occupiedPosition = _positions
                .Select(item => new
                {
                    Position = item,
                    Distance = Math.Sqrt(
                        Math.Pow(item.X * _imageWidth - point.X, 2) +
                        Math.Pow(item.Y * _imageHeight - point.Y, 2))
                })
                .Where(item => item.Distance <= overwriteRadius)
                .OrderBy(item => item.Distance)
                .Select(item => item.Position)
                .FirstOrDefault();

            ConnectorPinPosition position;
            if (sameNamePosition != null)
            {
                position = sameNamePosition;
                if (occupiedPosition != null && !ReferenceEquals(occupiedPosition, position))
                    _positions.Remove(occupiedPosition);
            }
            else if (occupiedPosition != null)
            {
                position = occupiedPosition;
                position.PinNumber = pinNumber;
            }
            else
            {
                position = new ConnectorPinPosition { PinNumber = pinNumber };
                _positions.Add(position);
            }
            position.X = Math.Clamp(point.X / _imageWidth, 0, 1);
            position.Y = Math.Clamp(point.Y / _imageHeight, 0, 1);
            PositionsList.Items.Refresh();
            PositionsList.SelectedItem = position;
            RedrawPositions();

            MovePinSequence(1);
        }

        private void PreviousPinButton_Click(object sender, RoutedEventArgs e)
            => MovePinSequence(-1);

        private void NextPinButton_Click(object sender, RoutedEventArgs e)
            => MovePinSequence(1);

        private void SparePinButton_Click(object sender, RoutedEventArgs e)
        {
            PinNumberBox.Text = "EX";
            PinNumberBox.Focus();
            PinNumberBox.SelectAll();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            if (e.Key == Key.Z)
            {
                UndoLastPositionChange();
                e.Handled = true;
            }
            else if (e.Key == Key.A)
            {
                MovePinSequence(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.S)
            {
                MovePinSequence(1);
                e.Handled = true;
            }
        }

        private void PinsCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            Point point = e.GetPosition(PinsCanvas);
            UpdatePinPreviewText();
            _pinPreviewPopup.HorizontalOffset = point.X + 12;
            _pinPreviewPopup.VerticalOffset = point.Y + 12;
            _pinPreviewPopup.IsOpen = true;
            UpdateHoverSnapGuide(point);
        }

        private void PinsCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            _pinPreviewPopup.IsOpen = false;
            if (_hoverSnapGuide != null) _hoverSnapGuide.Visibility = Visibility.Collapsed;
        }

        private void UpdateHoverSnapGuide(Point point)
        {
            if (_hoverSnapGuide == null || _detectedPinCircles.Count == 0 ||
                _snapPixelWidth <= 0 || _snapPixelHeight <= 0) return;
            double scaleX = _imageWidth / _snapPixelWidth;
            double scaleY = _imageHeight / _snapPixelHeight;
            var nearest = _detectedPinCircles
                .Select(circle => new
                {
                    Circle = circle,
                    X = circle.X * scaleX,
                    Y = circle.Y * scaleY,
                    Distance = Math.Sqrt(
                        Math.Pow(circle.X * scaleX - point.X, 2) +
                        Math.Pow(circle.Y * scaleY - point.Y, 2))
                })
                .OrderBy(item => item.Distance)
                .First();
            double diameter = Math.Max(7,
                nearest.Circle.Diameter * (scaleX + scaleY) / 2);
            double hoverRadius = Math.Max(diameter, Math.Min(_imageWidth, _imageHeight) * 0.034);
            if (nearest.Distance > hoverRadius)
            {
                _hoverSnapGuide.Visibility = Visibility.Collapsed;
                return;
            }
            _hoverSnapGuide.Width = diameter;
            _hoverSnapGuide.Height = diameter;
            _hoverSnapGuide.CornerRadius = new CornerRadius(diameter / 2);
            Canvas.SetLeft(_hoverSnapGuide, nearest.X - diameter / 2);
            Canvas.SetTop(_hoverSnapGuide, nearest.Y - diameter / 2);
            _hoverSnapGuide.Visibility = Visibility.Visible;
        }

        private void UpdatePinPreviewText()
        {
            if (_pinPreviewText == null) return;
            string pinNumber = PinNumberBox.Text.Trim();
            _pinPreviewText.Text = string.IsNullOrWhiteSpace(pinNumber)
                ? "핀 번호 입력" : $"핀 {pinNumber}";
        }

        private void PrepareSnapPixels(ImageSource image)
        {
            if (image is not BitmapSource bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
                return;
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            _snapPixelWidth = converted.PixelWidth;
            _snapPixelHeight = converted.PixelHeight;
            _snapStride = _snapPixelWidth * 4;
            _snapPixels = new byte[_snapStride * _snapPixelHeight];
            converted.CopyPixels(_snapPixels, _snapStride, 0);
            DetectPinCircles();
        }

        private void DetectPinCircles()
        {
            _detectedPinCircles.Clear();
            if (_snapPixels == null) return;
            var visited = new bool[_snapPixelWidth * _snapPixelHeight];
            int minimumDimension = Math.Min(_snapPixelWidth, _snapPixelHeight);
            int minimumDiameter = Math.Clamp((int)Math.Round(minimumDimension * 0.022), 9, 16);
            int maximumDiameter = Math.Clamp((int)Math.Round(minimumDimension * 0.085), 28, 52);
            var queue = new Queue<int>();

            for (int startY = 0; startY < _snapPixelHeight; startY++)
            for (int startX = 0; startX < _snapPixelWidth; startX++)
            {
                int startIndex = startY * _snapPixelWidth + startX;
                if (visited[startIndex] || !IsDarkPixel(startX, startY)) continue;
                visited[startIndex] = true;
                queue.Enqueue(startIndex);
                int minX = startX, maxX = startX, minY = startY, maxY = startY;

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    int x = current % _snapPixelWidth;
                    int y = current / _snapPixelWidth;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                    for (int offsetY = -1; offsetY <= 1; offsetY++)
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (offsetX == 0 && offsetY == 0) continue;
                        int neighborX = x + offsetX;
                        int neighborY = y + offsetY;
                        if (neighborX < 0 || neighborX >= _snapPixelWidth ||
                            neighborY < 0 || neighborY >= _snapPixelHeight) continue;
                        int neighborIndex = neighborY * _snapPixelWidth + neighborX;
                        if (visited[neighborIndex] || !IsDarkPixel(neighborX, neighborY)) continue;
                        visited[neighborIndex] = true;
                        queue.Enqueue(neighborIndex);
                    }
                }

                int width = maxX - minX + 1;
                int height = maxY - minY + 1;
                int diameter = Math.Max(width, height);
                double aspect = width / (double)Math.Max(1, height);
                if (diameter < minimumDiameter || diameter > maximumDiameter ||
                    aspect < 0.72 || aspect > 1.38) continue;

                double centerX = (minX + maxX) / 2.0;
                double centerY = (minY + maxY) / 2.0;
                double radius = (Math.Min(width, height) - 1) / 2.0;
                int ringMatches = 0;
                const int samples = 20;
                for (int sample = 0; sample < samples; sample++)
                {
                    double angle = sample * Math.PI * 2 / samples;
                    if (IsDarkNear(
                            (int)Math.Round(centerX + Math.Cos(angle) * radius),
                            (int)Math.Round(centerY + Math.Sin(angle) * radius), 1))
                        ringMatches++;
                }
                if (ringMatches < 13) continue;
                _detectedPinCircles.Add(new DetectedPinCircle(
                    centerX, centerY, Math.Max(width, height)));
            }
        }

        private Point SnapToNearestPinCenter(Point sourcePoint)
        {
            if (_snapPixels == null || _snapPixelWidth <= 0 || _snapPixelHeight <= 0)
                return sourcePoint;

            double scaleX = _snapPixelWidth / _imageWidth;
            double scaleY = _snapPixelHeight / _imageHeight;
            int clickX = (int)Math.Round(sourcePoint.X * scaleX);
            int clickY = (int)Math.Round(sourcePoint.Y * scaleY);
            int minimumDimension = Math.Min(_snapPixelWidth, _snapPixelHeight);
            int searchRadius = Math.Clamp((int)Math.Round(minimumDimension * 0.052), 14, 34);
            var nearestDetected = _detectedPinCircles
                .Select(circle => new
                {
                    Circle = circle,
                    Distance = Math.Sqrt(
                        (circle.X - clickX) * (circle.X - clickX) +
                        (circle.Y - clickY) * (circle.Y - clickY))
                })
                .Where(item => item.Distance <= searchRadius)
                .OrderBy(item => item.Distance)
                .FirstOrDefault();
            if (nearestDetected != null)
                return new Point(nearestDetected.Circle.X / scaleX,
                    nearestDetected.Circle.Y / scaleY);

            int minimumCircleRadius = Math.Clamp((int)Math.Round(minimumDimension * 0.009), 4, 8);
            int maximumCircleRadius = Math.Clamp((int)Math.Round(minimumDimension * 0.034), 9, 22);

            double bestScore = double.NegativeInfinity;
            int bestX = clickX;
            int bestY = clickY;
            const int samples = 20;
            for (int y = Math.Max(0, clickY - searchRadius);
                 y <= Math.Min(_snapPixelHeight - 1, clickY + searchRadius); y++)
            for (int x = Math.Max(0, clickX - searchRadius);
                 x <= Math.Min(_snapPixelWidth - 1, clickX + searchRadius); x++)
            {
                double distance = Math.Sqrt((x - clickX) * (x - clickX) + (y - clickY) * (y - clickY));
                if (distance > searchRadius) continue;

                for (int radius = minimumCircleRadius; radius <= maximumCircleRadius; radius++)
                {
                    int darkSamples = 0;
                    for (int sample = 0; sample < samples; sample++)
                    {
                        double angle = sample * Math.PI * 2 / samples;
                        int sampleX = (int)Math.Round(x + Math.Cos(angle) * radius);
                        int sampleY = (int)Math.Round(y + Math.Sin(angle) * radius);
                        if (IsDarkNear(sampleX, sampleY, 1)) darkSamples++;
                    }
                    if (darkSamples < 14) continue;

                    double score = darkSamples * 5 + radius * 1.7 - distance * 0.4;
                    if (score <= bestScore) continue;
                    bestScore = score;
                    bestX = x;
                    bestY = y;
                }
            }

            // 원 둘레의 70% 이상이 확인된 경우에만 자석 보정을 적용한다.
            if (double.IsNegativeInfinity(bestScore)) return sourcePoint;
            return new Point(bestX / scaleX, bestY / scaleY);
        }

        private bool IsDarkNear(int centerX, int centerY, int radius)
        {
            if (_snapPixels == null) return false;
            for (int y = Math.Max(0, centerY - radius);
                 y <= Math.Min(_snapPixelHeight - 1, centerY + radius); y++)
            for (int x = Math.Max(0, centerX - radius);
                 x <= Math.Min(_snapPixelWidth - 1, centerX + radius); x++)
            {
                int offset = y * _snapStride + x * 4;
                int blue = _snapPixels[offset];
                int green = _snapPixels[offset + 1];
                int red = _snapPixels[offset + 2];
                int alpha = _snapPixels[offset + 3];
                if (alpha > 40 && (red * 299 + green * 587 + blue * 114) / 1000 < 145)
                    return true;
            }
            return false;
        }

        private bool IsDarkPixel(int x, int y)
        {
            if (_snapPixels == null || x < 0 || x >= _snapPixelWidth ||
                y < 0 || y >= _snapPixelHeight) return false;
            int offset = y * _snapStride + x * 4;
            int blue = _snapPixels[offset];
            int green = _snapPixels[offset + 1];
            int red = _snapPixels[offset + 2];
            int alpha = _snapPixels[offset + 3];
            return alpha > 40 && (red * 299 + green * 587 + blue * 114) / 1000 < 145;
        }

        private void PushPositionUndoCheckpoint()
            => _undoHistory.Push(new PinPositionUndoState(
                _positions.Select(Clone).ToList(), PinNumberBox.Text));

        private void UndoLastPositionChange()
        {
            if (_undoHistory.Count == 0) return;
            var state = _undoHistory.Pop();
            _positions.Clear();
            foreach (var position in state.Positions) _positions.Add(Clone(position));
            PositionsList.SelectedItem = null;
            PinNumberBox.Text = state.PinNumber;
            PinNumberBox.Focus();
            PinNumberBox.SelectAll();
            RedrawPositions();
        }

        private void MovePinSequence(int direction)
        {
            string current = PinNumberBox.Text.Trim();
            string next = NextPinToken(current, direction);
            PinNumberBox.Text = next;
            PinNumberBox.Focus();
            PinNumberBox.SelectAll();
        }

        private static string NextPinToken(string value, int direction)
        {
            direction = direction < 0 ? -1 : 1;
            if (string.IsNullOrWhiteSpace(value))
                return direction > 0 ? "a" : "Z";
            if (int.TryParse(value, out int number))
                return Math.Max(1, number + direction).ToString();

            if (value.Length == 1 && char.IsLower(value[0]))
            {
                int index = LowercasePinSequence.IndexOf(value[0]);
                if (index >= 0)
                {
                    if (direction > 0)
                        return index == LowercasePinSequence.Length - 1
                            ? "AA" : LowercasePinSequence[index + 1].ToString();
                    return index == 0 ? "Z" : LowercasePinSequence[index - 1].ToString();
                }
            }

            if (value.Length == 2 && char.IsUpper(value[0]) && value[0] == value[1])
            {
                int index = UppercasePinSequence.IndexOf(value[0]);
                if (index >= 0)
                {
                    if (direction > 0)
                        return index == UppercasePinSequence.Length - 1
                            ? "A" : new string(UppercasePinSequence[index + 1], 2);
                    return index == 0 ? "z" : new string(UppercasePinSequence[index - 1], 2);
                }
            }

            if (value.Length == 1 && char.IsUpper(value[0]))
            {
                int index = UppercasePinSequence.IndexOf(value[0]);
                if (index >= 0)
                {
                    if (direction > 0)
                        return index == UppercasePinSequence.Length - 1
                            ? "a" : UppercasePinSequence[index + 1].ToString();
                    return index == 0 ? "ZZ" : UppercasePinSequence[index - 1].ToString();
                }
            }

            // EX나 사용자가 직접 입력한 특수 표기는 자동으로 바꾸지 않는다.
            return value;
        }

        private void PositionsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (PositionsList.SelectedItem is ConnectorPinPosition position)
                PinNumberBox.Text = position.PinNumber;
        }

        private void DeletePositionButton_Click(object sender, RoutedEventArgs e)
        {
            if (PositionsList.SelectedItem is not ConnectorPinPosition position) return;
            PushPositionUndoCheckpoint();
            _positions.Remove(position);
            RedrawPositions();
        }

        private void RedrawPositions()
        {
            PinsCanvas.Children.Clear();
            _hoverSnapGuide = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(62, 0, 0, 0)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(185, 0, 0, 0)),
                BorderThickness = new Thickness(1.2),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            PinsCanvas.Children.Add(_hoverSnapGuide);
            double radius = Math.Clamp(_markerDiameterPoints * 96.0 / 72.0 / 2.0,
                2.0, Math.Min(_imageWidth, _imageHeight) * 0.08);
            foreach (var position in _positions)
            {
                var marker = new Border
                {
                    Width = radius * 2,
                    Height = radius * 2,
                    CornerRadius = new CornerRadius(radius),
                    Background = AppThemeManager.WithAlpha("AppAccentBrush", 205),
                    BorderBrush = Brushes.White,
                    BorderThickness = new Thickness(Math.Max(1, radius * 0.12)),
                    ToolTip = $"핀 {position.PinNumber}",
                    Child = new TextBlock
                    {
                        Text = position.PinNumber,
                        Foreground = Brushes.White,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = Math.Max(8, radius * 0.72 + 1),
                        Margin = new Thickness(0),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Center
                    }
                };
                Canvas.SetLeft(marker, position.X * _imageWidth - radius);
                Canvas.SetTop(marker, position.Y * _imageHeight - radius);
                PinsCanvas.Children.Add(marker);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ConnectorCatalogService.Instance.SavePinPositions(
                    _catalogKey, _positions, _markerDiameterPoints);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "핀 위치 저장 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private double DetectDefaultMarkerDiameterPoints()
        {
            if (_detectedPinCircles.Count == 0)
                return Math.Clamp(Math.Min(_imageWidth, _imageHeight) * 0.014 * 72.0 / 96.0,
                    4, 16);
            var ordered = _detectedPinCircles.Select(item => item.Diameter)
                .OrderBy(value => value).ToList();
            double medianPixels = ordered[ordered.Count / 2];
            double scaleX = _imageWidth / Math.Max(1, _snapPixelWidth);
            double scaleY = _imageHeight / Math.Max(1, _snapPixelHeight);
            return Math.Clamp(medianPixels * (scaleX + scaleY) / 2 * 72.0 / 96.0,
                3, 24);
        }

        private void MarkerSizeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingMarkerSize || !IsInitialized) return;
            if (!double.TryParse(MarkerSizeBox.Text, out double points) ||
                points < 2 || points > 40) return;
            _markerDiameterPoints = points;
            RedrawPositions();
        }

        private static ConnectorPinPosition Clone(ConnectorPinPosition source)
            => new() { PinNumber = source.PinNumber, X = source.X, Y = source.Y };
    }
}
