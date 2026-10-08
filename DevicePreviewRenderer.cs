using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Input;
using ESDP.Models;

namespace ESDP
{
    /// <summary>
    /// 장치 박스 + 포트를 Canvas에 그리는 공통 로직.
    /// MainWindow(실제 캔버스), 등록창 미리보기, 상세설정창 미리보기가
    /// 모두 이 로직을 공유해 "보이는 대로 배치된다"를 보장한다.
    /// </summary>
    public static class DevicePreviewRenderer
    {
        public sealed class PortLabelVisualInfo
        {
            public Port Port { get; init; } = null!;
            public PortSide Side { get; init; }
            public double AnchorX { get; init; }
            public double AnchorY { get; init; }
        }

        public sealed class DeviceNameVisualInfo
        {
            public double CenterX { get; init; }
            public double CenterY { get; init; }
        }

        private static readonly Brush PortVisualBrush =
            new SolidColorBrush(Color.FromRgb(0x7F, 0x7F, 0x7F));

        public const double PortWidth = 3;
        public const double PortHeight = 11;
        public const double PortLabelFontSize = 11;
        public const double PortSlotSize = 20;

        /// <summary>포트 개수 기반 최소 크기 (좁아서 라벨이 겹치는 것을 방지)</summary>
        public const double MinSpacingPerPort = 20; // 포트 하나가 필요로 하는 최소 변 길이(px)
        public const double AbsoluteMinWidth = PortSlotSize;
        public const double AbsoluteMinHeight = PortSlotSize;

        public static (double MinW, double MinH) GetMinSize(Device device)
        {
            int leftN = device.Ports.Count(p => p.Side == PortSide.Left);
            int rightN = device.Ports.Count(p => p.Side == PortSide.Right);
            int topN = device.Ports.Count(p => p.Side == PortSide.Top);
            int bottomN = device.Ports.Count(p => p.Side == PortSide.Bottom);

            // 장치의 변을 20px 셀 슬롯으로 나누고 포트 하나가
            // 셀 하나의 중앙을 차지한다. 따라서 n개 포트에는 n칸이면 충분하다.
            double minH = Math.Max(AbsoluteMinHeight, Math.Max(leftN, rightN) * MinSpacingPerPort);
            double minW = Math.Max(AbsoluteMinWidth, Math.Max(topN, bottomN) * MinSpacingPerPort);
            return (minW, minH);
        }

        /// <summary>
        /// 장치 박스 + 이름 + 포트를 canvas 에 그린다. (0,0) 이 박스 좌상단이 되도록 그린다.
        /// showLabels=false 면 상세설정 창처럼 포트 이름 텍스트를 생략한다(간섭 방지).
        /// </summary>
        public static void Draw(
            Canvas canvas, Device device, double width, double height,
            bool showLabels = true, string? purpose = null)
        {
            canvas.Children.Clear();

            var deviceContent = new Grid();
            var backgroundImage = ProjectImageService.Load(
                device.BackgroundImageBase64);
            if (backgroundImage != null)
            {
                string imageStretch = device.HasExplicitBackgroundImageStretch
                    ? device.BackgroundImageStretch
                    : "Uniform";
                deviceContent.Children.Add(new Image
                {
                    Source = backgroundImage,
                    Stretch = ProjectImageService.ParseStretch(
                        imageStretch),
                    Opacity = Math.Clamp(device.BackgroundImageOpacity, 0, 1),
                    IsHitTestVisible = false
                });
            }

            var box = new Border
            {
                Width = width,
                Height = height,
                Background = AppSettings.GetDeviceBrush(device),
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1.5),
                ClipToBounds = true,
                Child = deviceContent
            };
            Canvas.SetLeft(box, 0);
            Canvas.SetTop(box, 0);
            canvas.Children.Add(box);

            // 이름은 모든 장치에서 별도 레이어로 올린다. 따라서 일반 배경과
            // 이미지 배경 모두 같은 3×3 정렬 및 자유 드래그 좌표를 사용하며,
            // 긴 이름도 장치 본체의 ClipToBounds에 잘리지 않는다.
            var compactName = CreateDeviceCaption(
                device, purpose, compact: true);
            compactName.Measure(new Size(
                double.PositiveInfinity, double.PositiveInfinity));
            Size plateSize = compactName.DesiredSize;
            double requestedX = device.HasCustomNamePosition
                ? Math.Clamp(device.NamePositionX, 0, 1)
                : 0.5;
            double requestedY = device.HasCustomNamePosition
                ? Math.Clamp(device.NamePositionY, 0, 1)
                : 0.5;
            double centerX = GetAlignedCenter(
                requestedX, width, plateSize.Width, 5);
            double centerY = GetAlignedCenter(
                requestedY, height, plateSize.Height, 5);
            double left = centerX - plateSize.Width / 2;
            double top = centerY - plateSize.Height / 2;
            var namePlate = new Border
            {
                Background = backgroundImage != null
                    ? Brushes.White
                    : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                Child = compactName,
                Tag = new DeviceNameVisualInfo
                {
                    CenterX = centerX,
                    CenterY = centerY
                }
            };
            Canvas.SetLeft(namePlate, left);
            Canvas.SetTop(namePlate, top);
            canvas.Children.Add(namePlate);

            foreach (var side in new[] { PortSide.Left, PortSide.Right, PortSide.Top, PortSide.Bottom })
                DrawPortsOnSide(canvas, device.Ports.Where(p => p.Side == side).ToList(), side, width, height, showLabels);
        }

        private static TextBlock CreateDeviceCaption(
            Device device, string? purpose, bool compact)
        {
            string name = string.IsNullOrWhiteSpace(device.Name)
                ? "(이름 없음)"
                : device.Name;
            string text = string.IsNullOrWhiteSpace(purpose)
                ? name
                : $"{name}\n({purpose.Trim()})";
            return new TextBlock
            {
                Text = text,
                FontFamily = AppFonts.Resolve(device.NameFontFamily),
                FontSize = Math.Max(6, device.NameFontSize),
                FontWeight = device.NameBold
                    ? FontWeights.Bold
                    : FontWeights.Normal,
                FontStyle = device.NameItalic
                    ? FontStyles.Italic
                    : FontStyles.Normal,
                Foreground = HexToBrush(device.NameColorHex),
                TextDecorations = device.NameUnderline
                    ? TextDecorations.Underline
                    : null,
                TextAlignment = ParseTextAlignment(device.NameTextAlignment),
                HorizontalAlignment = compact
                    ? HorizontalAlignment.Left
                    : HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = compact
                    ? TextWrapping.NoWrap
                    : TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
                Margin = compact ? new Thickness(0) : new Thickness(2),
                Padding = new Thickness(0)
            };
        }

        private static double GetAlignedCenter(
            double normalizedPosition, double containerLength,
            double contentLength, double edgePadding)
        {
            normalizedPosition = Math.Clamp(normalizedPosition, 0, 1);
            contentLength = Math.Max(0, contentLength);
            edgePadding = Math.Max(0, edgePadding);

            // 글자 내부 여백은 늘리지 않고 장치 경계와 글자 사이에만 여백을 둔다.
            // 긴 이름이 여백을 포함해 본체보다 넓으면 잘라내지 않고 중앙에 둔다.
            double minimumCenter = edgePadding + contentLength / 2;
            double maximumCenter = containerLength - edgePadding - contentLength / 2;
            if (minimumCenter > maximumCenter) return containerLength / 2;
            return Math.Clamp(
                normalizedPosition * containerLength,
                minimumCenter, maximumCenter);
        }

        private static System.Windows.Media.Effects.Effect CreateThinTextOutline(
            Brush foreground)
        {
            Color textColor = foreground is SolidColorBrush solid
                ? solid.Color
                : Colors.Black;
            double luminance =
                (0.2126 * textColor.R +
                 0.7152 * textColor.G +
                 0.0722 * textColor.B) / 255.0;
            return new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = luminance >= 0.55 ? Colors.Black : Colors.White,
                BlurRadius = 3,
                ShadowDepth = 0,
                Opacity = 0.9,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
            };
        }

        private static TextAlignment ParseTextAlignment(string value) => value switch
        {
            "Left" => TextAlignment.Left,
            "Right" => TextAlignment.Right,
            "Justify" => TextAlignment.Justify,
            _ => TextAlignment.Center
        };

        private static void DrawPortsOnSide(Canvas canvas, List<Port> ports, PortSide side,
                                             double width, double height, bool showLabels)
        {
            int count = ports.Count;
            if (count == 0) return;

            bool vertical = side == PortSide.Left || side == PortSide.Right; // 세로 변

            for (int i = 0; i < count; i++)
            {
                var port = ports[i];
                double frac = GetPortSlotFraction(
                    port, i, count, vertical ? height : width);

                double rectW = vertical ? PortWidth : PortHeight;
                double rectH = vertical ? PortHeight : PortWidth;

                double x, y;
                // 포트 도형은 장치 내부가 아니라 바로 바깥쪽 인접 셀에 둔다.
                // 장치 포트와 케이블 포트가 한 20px 셀 안에서 마주 보도록,
                // 장치 포트는 본체 경계에 붙여 완전히 바깥으로 돌출시킨다.
                if (side == PortSide.Left) { x = -PortWidth; y = frac * height - rectH / 2; }
                else if (side == PortSide.Right) { x = width; y = frac * height - rectH / 2; }
                else if (side == PortSide.Top) { x = frac * width - rectW / 2; y = -PortWidth; }
                else { x = frac * width - rectW / 2; y = height; } // Bottom

                var rect = new Rectangle
                {
                    Width = rectW, Height = rectH, Fill = PortVisualBrush,
                    Tag = port
                };
                AttachAltDescriptionToolTip(rect, port.Description);
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                canvas.Children.Add(rect);

                if (!showLabels || !port.ShowLabel) continue;

                double labelFontSize = port.LabelFontSize > 0
                    ? port.LabelFontSize
                    : PortLabelFontSize;
                string labelText = port.ShowConnType &&
                                   !string.IsNullOrWhiteSpace(port.ConnType)
                    ? $"{port.Designator}\n{port.ConnType}"
                    : port.Designator;

                var label = new TextBlock
                {
                    Text = labelText,
                    FontSize = labelFontSize,
                    FontWeight = FontWeights.Bold,
                    Foreground = HexToBrush(port.LabelColorHex),
                    IsHitTestVisible = true,
                    Tag = new PortLabelVisualInfo
                    {
                        Port = port,
                        Side = side,
                        AnchorX = side == PortSide.Left ? 0 :
                                  side == PortSide.Right ? width :
                                  frac * width,
                        AnchorY = side == PortSide.Top ? 0 :
                                  side == PortSide.Bottom ? height :
                                  frac * height
                    }
                };
                label.Effect = CreateThinTextOutline(label.Foreground);
                AttachAltDescriptionToolTip(label, port.Description);

                double approxCharWidth = labelFontSize * 0.62;
                double approxLabelWidth = labelText
                    .Split('\n')
                    .Select(line => Math.Max(1, line.Length) * approxCharWidth)
                    .DefaultIfEmpty(approxCharWidth)
                    .Max();
                label.Measure(new Size(
                    double.PositiveInfinity, double.PositiveInfinity));
                double labelWidth = Math.Max(approxLabelWidth, label.DesiredSize.Width);
                double labelHeight = Math.Max(labelFontSize + 2, label.DesiredSize.Height);
                const double insideGap = 4;

                if (side == PortSide.Left)
                {
                    Canvas.SetLeft(label, insideGap);
                    Canvas.SetTop(label, frac * height - labelHeight / 2);
                }
                else if (side == PortSide.Right)
                {
                    Canvas.SetLeft(label, width - labelWidth - insideGap);
                    Canvas.SetTop(label, frac * height - labelHeight / 2);
                }
                else if (side == PortSide.Top)
                {
                    Canvas.SetLeft(label, frac * width - labelWidth / 2);
                    Canvas.SetTop(label, insideGap);
                }
                else // Bottom
                {
                    Canvas.SetLeft(label, frac * width - labelWidth / 2);
                    Canvas.SetTop(label, height - labelHeight - insideGap);
                }
                if (port.HasCustomLabelPosition)
                {
                    Canvas.SetLeft(label,
                        ((PortLabelVisualInfo)label.Tag).AnchorX + port.LabelOffsetX);
                    Canvas.SetTop(label,
                        ((PortLabelVisualInfo)label.Tag).AnchorY + port.LabelOffsetY);
                }
                canvas.Children.Add(label);
            }
        }

        // 포트는 실제 첫/마지막 격자 셀의 중앙까지 사용할 수 있어야 한다.
        // 5%~95%로 제한하면 긴 장치에서는 첫 셀 중심이 5%보다 작아져
        // 첫 번째 슬롯이 두 번째 슬롯으로 잘못 환산된다.
        private static double Clamp01(double v) => Math.Max(0, Math.Min(1, v));

        /// <summary>
        /// 장치 변을 PortSlotSize 크기의 셀로 보고 포트를 셀 중앙에 배치한다.
        /// 명시 위치도 가장 가까운 슬롯으로 양자화하며, 자동 위치는 사용 가능한
        /// 슬롯 전체에 겹치지 않도록 분산한다.
        /// </summary>
        public static int GetPortSlotCount(double sideLength)
            => Math.Max(1, (int)Math.Round(
                sideLength / PortSlotSize,
                MidpointRounding.AwayFromZero));

        public static int GetPortSlotIndex(double fraction, double sideLength)
        {
            int slotCount = GetPortSlotCount(sideLength);
            double local = Clamp01(fraction) * sideLength;
            int slot = (int)Math.Round(
                local / PortSlotSize - 0.5,
                MidpointRounding.AwayFromZero);
            return Math.Clamp(slot, 0, slotCount - 1);
        }

        public static double GetPortSlotFraction(
            Port port, int index, int count, double sideLength)
        {
            if (sideLength <= 0.001) return 0.5;
            int slotCount = GetPortSlotCount(sideLength);

            int slot;
            if (port.Position >= 0)
            {
                slot = GetPortSlotIndex(port.Position, sideLength);
            }
            else if (count <= 1)
            {
                slot = (int)Math.Round(
                    (slotCount - 1) / 2.0,
                    MidpointRounding.AwayFromZero);
            }
            else
            {
                slot = (int)Math.Round(
                    index * (slotCount - 1.0) / (count - 1.0),
                    MidpointRounding.AwayFromZero);
            }

            slot = Math.Clamp(slot, 0, slotCount - 1);
            return ((slot + 0.5) * PortSlotSize) / sideLength;
        }

        private static void AttachAltDescriptionToolTip(
            FrameworkElement element, string? description)
        {
            if (string.IsNullOrWhiteSpace(description)) return;
            element.ToolTip = description.Trim();
            ToolTipService.SetInitialShowDelay(element, 180);
            ToolTipService.SetBetweenShowDelay(element, 80);
            element.ToolTipOpening += (_, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0)
                    e.Handled = true;
            };
        }

        /// <summary>
        /// 장치의 각 포트가 캔버스에서 갖는 절대 접점 좌표를 반환한다.
        /// (instX,instY)=장치 좌상단 절대좌표, (w,h)=장치 크기.
        /// 케이블 포트를 이 지점에 자석으로 붙일 때 사용.
        /// </summary>
        public static IEnumerable<(Port Port, double X, double Y, PortSide Side)> GetPortAnchors(
            Device device, double instX, double instY, double w, double h)
        {
            foreach (var side in new[] { PortSide.Left, PortSide.Right, PortSide.Top, PortSide.Bottom })
            {
                var ports = device.Ports.Where(p => p.Side == side).ToList();
                int count = ports.Count;
                for (int i = 0; i < count; i++)
                {
                    var port = ports[i];
                    bool vertical = side is PortSide.Left or PortSide.Right;
                    double frac = GetPortSlotFraction(
                        port, i, count, vertical ? h : w);
                    double x, y;
                    // 앵커는 장치 포트 도형의 중심이다. CableRenderer가 이
                    // 중심에서 바깥으로 케이블 포트 하나만큼 떨어뜨리므로 두
                    // 포트 모두 동일한 외부 격자 셀 안에 들어간다.
                    double halfPort = PortWidth / 2.0;
                    if (side == PortSide.Left) { x = -halfPort; y = frac * h; }
                    else if (side == PortSide.Right) { x = w + halfPort; y = frac * h; }
                    else if (side == PortSide.Top) { x = frac * w; y = -halfPort; }
                    else { x = frac * w; y = h + halfPort; }
                    yield return (port, instX + x, instY + y, side);
                }
            }
        }

        /// <summary>
        /// 장치 박스(0,0)-(width,height) 기준 로컬 좌표 (mx,my)에서 가장 가까운 변과
        /// 그 변 위의 위치(0~1)를 계산한다. 포트를 마우스로 끌 때 사용.
        /// 네 변까지의 수직거리를 비교해 가장 가까운 쪽으로 판정한다.
        /// </summary>
        public static (PortSide Side, double Position) NearestEdge(double mx, double my, double width, double height)
        {
            double distLeft = Math.Abs(mx);
            double distRight = Math.Abs(mx - width);
            double distTop = Math.Abs(my);
            double distBottom = Math.Abs(my - height);

            double min = Math.Min(Math.Min(distLeft, distRight), Math.Min(distTop, distBottom));

            if (min == distLeft) return (PortSide.Left, Clamp01(SafeDiv(my, height)));
            if (min == distRight) return (PortSide.Right, Clamp01(SafeDiv(my, height)));
            if (min == distTop) return (PortSide.Top, Clamp01(SafeDiv(mx, width)));
            return (PortSide.Bottom, Clamp01(SafeDiv(mx, width)));
        }

        private static double SafeDiv(double a, double b) => b == 0 ? 0.5 : a / b;

        public static Brush HexToBrush(string hex)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { return Brushes.Black; }
        }
    }
}
