using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ESDP
{
    internal static class RichTextSupport
    {
        public static FlowDocument CreateDocument(
            string? rtfBase64, string plainText, string fontFamily,
            double fontSize, string colorHex, bool bold, bool italic,
            bool underline, string alignment)
        {
            var document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = AppFonts.Resolve(fontFamily),
                FontSize = fontSize,
                Foreground = ToBrush(colorHex),
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
                TextAlignment = ParseAlignment(alignment)
            };
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            var run = new Run(plainText);
            if (underline) run.TextDecorations = TextDecorations.Underline;
            paragraph.Inlines.Add(run);
            document.Blocks.Add(paragraph);

            if (string.IsNullOrWhiteSpace(rtfBase64)) return document;
            try
            {
                using var stream = new MemoryStream(
                    Convert.FromBase64String(rtfBase64));
                new TextRange(document.ContentStart, document.ContentEnd)
                    .Load(stream, DataFormats.Rtf);
                foreach (Block block in document.Blocks)
                    if (block is Paragraph p) p.Margin = new Thickness(0);
            }
            catch
            {
                // 예전 파일이나 손상된 서식은 일반 텍스트로 안전하게 표시한다.
            }
            document.PagePadding = new Thickness(0);
            return document;
        }

        public static FrameworkElement CreateViewer(
            string? rtfBase64, string plainText, string fontFamily,
            double fontSize, string colorHex, bool bold, bool italic,
            bool underline, string alignment,
            double horizontalPadding = 4)
        {
            var viewer = new RichTextBox(CreateDocument(
                rtfBase64, plainText, fontFamily, fontSize, colorHex,
                bold, italic, underline, alignment))
            {
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                IsDocumentEnabled = false,
                IsHitTestVisible = false,
                Focusable = false,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(
                    horizontalPadding, 0, horizontalPadding, 0),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            // RichTextBox 자체를 전체 높이로 늘인 뒤 PagePadding을 나중에
            // 보정하면 첫 프레임이 위쪽에 표시된다. 표시 전용 뷰어는 높이를
            // 내용만큼만 사용하고, 바깥 컨테이너가 처음부터 가운데 배치한다.
            var host = new Grid
            {
                Background = Brushes.Transparent,
                ClipToBounds = true,
                IsHitTestVisible = false
            };
            host.Children.Add(viewer);
            return host;
        }

        public static void KeepVerticallyCentered(RichTextBox box)
        {
            bool applying = false;

            void ApplyImmediately()
            {
                if (applying || box.ActualHeight <= 0) return;
                applying = true;
                try
                {
                    box.UpdateLayout();
                    Rect first = box.Document.ContentStart
                        .GetCharacterRect(LogicalDirection.Forward);
                    Rect last = box.Document.ContentEnd
                        .GetCharacterRect(LogicalDirection.Backward);
                    double contentHeight = Math.Max(
                        box.FontSize, last.Bottom - first.Top);
                    if (!double.IsFinite(contentHeight))
                        contentHeight = box.FontSize;
                    double top = Math.Max(
                        0, (box.ActualHeight - contentHeight -
                            box.Padding.Top - box.Padding.Bottom) / 2);

                    if (Math.Abs(box.Document.PagePadding.Top - top) > 0.5)
                        box.Document.PagePadding =
                            new Thickness(0, top, 0, 0);
                }
                catch
                {
                    box.Document.PagePadding = new Thickness(0);
                }
                finally
                {
                    applying = false;
                }
            }

            box.Loaded += (_, __) => ApplyImmediately();
            box.SizeChanged += (_, __) => ApplyImmediately();
            box.TextChanged += (_, __) => ApplyImmediately();
        }

        /// <summary>
        /// 표시용 RichTextBox의 실제 글자 영역만 판정한다. 호스트 전체가
        /// 아니라 글자 바로 위를 더블클릭했을 때만 인라인 편집에 들어간다.
        /// </summary>
        public static bool IsPointOverText(
            FrameworkElement viewerHost, Point pointInHost)
        {
            var box = FindVisualChild<RichTextBox>(viewerHost);
            if (box == null || box.Document == null) return false;

            Point point = viewerHost.TranslatePoint(pointInHost, box);
            TextPointer? pointer = box.Document.ContentStart;
            TextPointer end = box.Document.ContentEnd;
            int guard = 0;

            while (pointer != null && pointer.CompareTo(end) < 0 &&
                   guard++ < 4096)
            {
                TextPointer? next = pointer.GetNextInsertionPosition(
                    LogicalDirection.Forward);
                if (next == null) break;

                string text = new TextRange(pointer, next).Text;
                Rect start = pointer.GetCharacterRect(LogicalDirection.Forward);
                Rect finish = next.GetCharacterRect(LogicalDirection.Backward);
                if (!string.IsNullOrEmpty(text) && !start.IsEmpty &&
                    double.IsFinite(start.Left) && double.IsFinite(start.Top))
                {
                    double width = 1;
                    if (!finish.IsEmpty &&
                        Math.Abs(finish.Top - start.Top) < Math.Max(2, start.Height))
                        width = Math.Max(1, finish.Right - start.Left);

                    var characterBounds = new Rect(
                        start.Left, start.Top, width,
                        Math.Max(1, start.Height));
                    characterBounds.Inflate(2.5, 2);
                    if (characterBounds.Contains(point)) return true;
                }
                pointer = next;
            }
            return false;
        }

        private static T? FindVisualChild<T>(DependencyObject parent)
            where T : DependencyObject
        {
            for (int index = 0;
                 index < VisualTreeHelper.GetChildrenCount(parent);
                 index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match) return match;
                var nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        public static string Save(FlowDocument document)
        {
            using var stream = new MemoryStream();
            new TextRange(document.ContentStart, document.ContentEnd)
                .Save(stream, DataFormats.Rtf);
            return Convert.ToBase64String(stream.ToArray());
        }

        public static string GetPlainText(FlowDocument document)
            => new TextRange(document.ContentStart, document.ContentEnd)
                .Text.TrimEnd('\r', '\n');

        public static Brush ToBrush(string hex)
        {
            try
            {
                return new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(hex));
            }
            catch { return Brushes.Black; }
        }

        public static string BrushToHex(object value, string fallback)
        {
            if (value is SolidColorBrush brush)
                return $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
            return fallback;
        }

        private static TextAlignment ParseAlignment(string value) => value switch
        {
            "Left" => TextAlignment.Left,
            "Right" => TextAlignment.Right,
            "Justify" => TextAlignment.Justify,
            _ => TextAlignment.Center
        };
    }
}
