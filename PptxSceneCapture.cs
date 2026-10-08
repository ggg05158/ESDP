using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Shape = System.Windows.Shapes.Shape;
using Point = System.Windows.Point;
using ESDP.Models;

namespace ESDP;

internal sealed class PptxScene
{
    public List<PptxVisual> Items { get; } = new();
    public List<Point> Junctions { get; } = new();
}

internal sealed class PptxVisual
{
    public string Name = "";
    public Rect Bounds;
    public PathGeometry? Geometry;
    public List<Point>? Route;
    public bool Cable;
    public Brush? Fill, Stroke;
    public double Thickness, Opacity = 1;
    public DoubleCollection? Dash;
    public PenLineCap Cap;
    public List<PptxParagraph>? Paragraphs;
    public Thickness Padding;
    public double Rotation;
    public bool FlipH = false, FlipV;
    public bool Wrap;
    public Color? Glow;
    public double GlowRadius;
    public byte[]? Image;
    public bool RasterFallback;
    public bool CenterConnection;
    public List<PptxVisual>? Children;
    public Rect? TextBounds;
    public Color? Highlight;
    public byte[]? ImageFill;
}

internal sealed record PptxRun(string Text, string Font, double Size, Color Color, bool Bold, bool Italic, bool Underline);
internal sealed record PptxParagraph(List<PptxRun> Runs, TextAlignment Alignment);

// Capture the already rendered export view. Never rebuild routes or edit source models.
internal static class PptxSceneCapture
{
    public static PptxScene Capture(Canvas canvas, IReadOnlyList<PlacedCable> cables, string pageGuideTag,
        IReadOnlyList<PlacedDevice>? devices = null, IReadOnlyList<PlacedShape>? shapes = null)
    {
        var scene = new PptxScene();
        var cableLines = new Dictionary<Polyline, string>();
        foreach (var cable in cables)
        {
            // DrawLegLine emits one visible polyline per root/branch in depth-first
            // order. Associate the name with its visual, independent of canvas Z-order.
            var lines = cable.Container.Children.OfType<Polyline>()
                .Where(p => Visible(p.Stroke) && !Visible(p.Fill) && p.Opacity > 0).ToArray();
            for (int i = 0; i < lines.Length; i++)
                cableLines[lines[i]] = "케이블 " + cable.EffectiveCable.Name + (i == 0 ? " 메인 가닥" : " 분기 가닥 " + i);
            foreach (var leg in cable.RenderedSegments.GroupBy(s => s.LegKey).Skip(1))
                scene.Junctions.Add(leg.First().A);
        }
        var deviceContainers = devices?.ToDictionary(d => d.Container) ?? new();
        var shapeContainers = shapes?.ToDictionary(s => s.Container) ?? new();
        var cableContainers = cables.ToDictionary(c => c.Container);
        // Labels are drawn on a shared top layer, not inside the cable container.
        // Use their explicit owner metadata, never the displayed name or proximity.
        var cableLabels = canvas.Children.OfType<Panel>().SelectMany(p => p.Children.OfType<TextBlock>())
            .Where(t => t.Tag is CableLabelVisualInfo info && cableContainers.ContainsKey(info.Placed.Container))
            .ToLookup(t => ((CableLabelVisualInfo)t.Tag).Placed);
        var groupedLabels = cableLabels.SelectMany(g => g).Cast<FrameworkElement>().ToHashSet();
        foreach (FrameworkElement child in canvas.Children.OfType<FrameworkElement>().OrderBy(Panel.GetZIndex))
        {
            if (Equals(child.Tag, pageGuideTag)) continue;
            if (child is Canvas container && deviceContainers.TryGetValue(container, out var device))
                CaptureDevice(device, canvas, scene, cableLines);
            else if (child is Canvas cableContainer && cableContainers.TryGetValue(cableContainer, out var cable))
                CaptureCable(cable, canvas, scene, cableLines, cableLabels[cable]);
            else if (child is Canvas shapeContainer && shapeContainers.TryGetValue(shapeContainer, out var shape))
            {
                var local = new PptxScene();
                string name = "도형 " + ShapeCatalog.Name(shape.Shape.Kind);
                Walk(child, canvas, local, cableLines, name, 1);
                if (local.Items.Count > 1) scene.Items.Add(Group(name, local.Items));
                else scene.Items.AddRange(local.Items);
            }
            else Walk(child, canvas, scene, cableLines, "개체", 1, groupedLabels);
        }
        // The file writer runs off the UI thread. Freeze copies, never live UI resources.
        foreach (var item in Descendants(scene.Items))
        {
            item.Fill = Frozen(item.Fill); item.Stroke = Frozen(item.Stroke);
            if (item.Geometry != null) { item.Geometry = item.Geometry.CloneCurrentValue(); item.Geometry.Freeze(); }
            if (item.Dash != null) { item.Dash = item.Dash.CloneCurrentValue(); item.Dash.Freeze(); }
        }
        return scene;
    }
    internal static IEnumerable<PptxVisual> Descendants(IEnumerable<PptxVisual> items)
    {
        foreach (var item in items)
        {
            yield return item;
            if (item.Children != null)
                foreach (var child in Descendants(item.Children)) yield return child;
        }
    }
    static PptxVisual Group(string name, List<PptxVisual> children)
    {
        var bounds = Rect.Empty;
        foreach (var child in children)
        {
            Rect box = child.Bounds;
            if (child.Rotation != 0 && !box.IsEmpty)
            {
                var matrix = Matrix.Identity;
                matrix.RotateAt(child.Rotation, box.X + box.Width / 2, box.Y + box.Height / 2);
                box = Rect.Transform(box, matrix);
            }
            bounds.Union(box);
            if (child.TextBounds.HasValue) bounds.Union(child.TextBounds.Value);
        }
        return new PptxVisual { Name = name, Bounds = bounds, Children = children };
    }
    static void CaptureCable(PlacedCable cable, Canvas root, PptxScene scene,
        Dictionary<Polyline, string> cableLines, IEnumerable<TextBlock> labels)
    {
        var container = cable.Container;
        if (container.Visibility != Visibility.Visible || container.Opacity <= 0) return;
        string name = "케이블 " + cable.EffectiveCable.Name;
        var local = new PptxScene();
        Walk(container, root, local, cableLines, name, 1);
        var pending = new List<(CableLabelVisualInfo Info, PptxVisual Visual)>();
        foreach (var text in labels)
        {
            var info = (CableLabelVisualInfo)text.Tag;
            var labelScene = new PptxScene();
            string labelName = name + (info.Label.IsCableName ? " 이름" : " 커넥터 " + info.Label.Text.Replace('\n', ' '));
            double opacity = 1;
            for (DependencyObject parent = VisualTreeHelper.GetParent(text); parent != root && parent is FrameworkElement e;
                parent = VisualTreeHelper.GetParent(parent))
                opacity *= e.Opacity;
            Walk(text, root, labelScene, cableLines, labelName, opacity);
            foreach (var visual in labelScene.Items) pending.Add((info, visual));
        }
        var layouts = new[] { cable.Instance.PortA, cable.Instance.PortB }
            .Concat(cable.Instance.Branches.Select(b => b.Port)).ToArray();
        Matrix matrix = MatrixTo(container, root);
        foreach (var marker in local.Items.Where(v => v.CenterConnection).ToArray())
        {
            Point center = new(marker.Bounds.X + marker.Bounds.Width / 2, marker.Bounds.Y + marker.Bounds.Height / 2);
            var layout = layouts.FirstOrDefault(l => (matrix.Transform(new Point(l.PortX, l.PortY)) - center).Length < .05);
            if (layout == null) continue;
            var connector = CableRenderer.FindConnector(cable.EffectiveCable, layout.ConnectorId);
            string connectorName = name + " 커넥터 " + (connector?.PortName ?? "?");
            marker.Name = connectorName;
            var members = new List<PptxVisual> { marker };
            foreach (var label in pending.Where(p => !p.Info.Label.IsCableName && p.Info.Label.ConnectorId == layout.ConnectorId &&
                (matrix.Transform(new Point(p.Info.Label.AnchorX, p.Info.Label.AnchorY)) - center).Length < .05).ToArray())
            {
                members.Add(label.Visual); pending.Remove(label);
            }
            local.Items[local.Items.IndexOf(marker)] = Group(connectorName, members);
        }
        local.Items.AddRange(pending.Select(p => p.Visual));
        if (local.Items.Count != 0) scene.Items.Add(Group(name, local.Items));
    }
    static void CaptureDevice(PlacedDevice device, Canvas root, PptxScene scene, Dictionary<Polyline, string> cableLines)
    {
        var container = device.Container;
        if (container.Visibility != Visibility.Visible || container.Opacity <= 0) return;
        string name = "장치 " + device.EffectiveDevice.Name;
        var children = container.Children.OfType<FrameworkElement>().ToArray();
        var body = children.OfType<Border>().FirstOrDefault(b => b.Tag is not DevicePreviewRenderer.DeviceNameVisualInfo);
        var caption = children.OfType<Border>().FirstOrDefault(b => b.Tag is DevicePreviewRenderer.DeviceNameVisualInfo);
        var local = new PptxScene();
        if (body != null)
        {
            var geometry = new RectangleGeometry(new Rect(0, 0, body.ActualWidth, body.ActualHeight),
                body.CornerRadius.TopLeft, body.CornerRadius.TopLeft) { Transform = new MatrixTransform(MatrixTo(body, root)) };
            var bodyVisual = new PptxVisual { Name = name + " 본체", Bounds = geometry.Bounds,
                Geometry = PathGeometry.CreateFromGeometry(geometry), Fill = body.Background, Stroke = body.BorderBrush,
                Thickness = body.BorderThickness.Left, Opacity = container.Opacity * body.Opacity };
            local.Items.Add(bodyVisual);
            if (bodyVisual.Geometry != null)
            {
                // Photo + device colour become the shape's picture fill, not a
                // separate picture which could be detached from its native text.
                if (body.Child is Panel content && content.Children.OfType<Image>().Any(i => i.Source != null))
                {
                    bodyVisual.ImageFill = DeviceBackground(body);
                }
                if (caption?.Child is FrameworkElement text)
                {
                    var label = new PptxScene();
                    Walk(text, root, label, cableLines, name, container.Opacity * caption.Opacity);
                    var visual = label.Items.SingleOrDefault(v => v.Paragraphs != null);
                    if (visual != null)
                    {
                        bodyVisual.Paragraphs = visual.Paragraphs;
                        bodyVisual.TextBounds = visual.Bounds;
                        bodyVisual.Padding = visual.Padding;
                        bodyVisual.Highlight = bodyVisual.ImageFill != null ? Colors.White : null;
                    }
                }
            }
        }
        // Group each device port with its own label first. Cable connector glyphs
        // live in cable containers and are deliberately never included here.
        foreach (var marker in children.Where(c => c.Tag is Port))
        {
            var port = (Port)marker.Tag;
            var members = new PptxScene();
            string portName = name + " 포트 " + port.Designator;
            Walk(marker, root, members, cableLines, portName, container.Opacity);
            foreach (var label in children.Where(c => c.Tag is DevicePreviewRenderer.PortLabelVisualInfo info && info.Port.Id == port.Id))
                Walk(label, root, members, cableLines, portName, container.Opacity);
            if (members.Items.Count != 0) local.Items.Add(Group(portName, members.Items));
        }
        if (local.Items.Count != 0) scene.Items.Add(Group(name, local.Items));
    }
    static byte[] DeviceBackground(Border body)
    {
        double width = body.ActualWidth, height = body.ActualHeight;
        double scale = Math.Min(2, 4096 / Math.Max(width, height));
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(body.Background, null, new Rect(0, 0, width, height));
            if (body.Child is FrameworkElement content && content.ActualWidth > 0 && content.ActualHeight > 0)
            {
                var position = content.TransformToAncestor(body).Transform(new Point());
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
                dc.DrawRectangle(new VisualBrush(content) { AutoLayoutContent = false, Stretch = Stretch.Fill,
                    ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, content.ActualWidth, content.ActualHeight) },
                    null, new Rect(position, new Size(content.ActualWidth, content.ActualHeight)));
                dc.Pop();
            }
        }
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(width * scale)),
            Math.Max(1, (int)Math.Ceiling(height * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    static Brush? Frozen(Brush? brush)
    {
        if (brush == null) return null;
        var copy = brush.CloneCurrentValue(); copy.Freeze(); return copy;
    }

    static bool Visible(Brush? brush) => brush != null && brush.Opacity > 0 &&
        (brush is not SolidColorBrush solid || solid.Color.A != 0);
    static Color ColorOf(Brush? brush) => brush is SolidColorBrush s ? s.Color : Colors.Black;
    static Matrix MatrixTo(FrameworkElement e, Canvas root)
    {
        var transform = e.TransformToAncestor(root);
        Point origin = transform.Transform(new Point());
        Point x = transform.Transform(new Point(1, 0)), y = transform.Transform(new Point(0, 1));
        return new Matrix(x.X - origin.X, x.Y - origin.Y, y.X - origin.X, y.Y - origin.Y, origin.X, origin.Y);
    }

    static void Walk(FrameworkElement e, Canvas root, PptxScene scene, Dictionary<Polyline, string> cableLines, string name, double opacity,
        ISet<FrameworkElement>? excluded = null)
    {
        if (excluded?.Contains(e) == true) return;
        if (e.Visibility != Visibility.Visible || e.Opacity <= 0) return;
        opacity *= e.Opacity;
        if (e.Tag is PlacedDevice device) name = "장치 " + device.EffectiveDevice.Name;
        else if (e.Tag is PlacedCable cable) name = "케이블 " + cable.EffectiveCable.Name;
        else if (e.Tag is PlacedShape shape) name = "도형 " + ShapeCatalog.Name(shape.Shape.Kind);
        Matrix matrix = MatrixTo(e, root);
        if (e is Shape vector)
        {
            if (!Visible(vector.Fill) && !Visible(vector.Stroke)) return; // invisible hit areas
            var visual = new PptxVisual { Name = name, Fill = vector.Fill, Stroke = vector.Stroke,
                Thickness = vector.StrokeThickness * Math.Sqrt(Math.Abs(matrix.Determinant)), Opacity = opacity,
                Dash = vector.StrokeDashArray, Cap = vector.StrokeStartLineCap };
            if (vector is Polyline poly && !Visible(poly.Fill))
            {
                visual.Route = poly.Points.Select(matrix.Transform).ToList();
                visual.Cable = cableLines.TryGetValue(poly, out var legName);
                if (visual.Cable) visual.Name = legName!;
                visual.Bounds = PointsBounds(visual.Route);
            }
            else
            {
                var geometry = PathGeometry.CreateFromGeometry(vector.RenderedGeometry);
                Matrix full = geometry.Transform?.Value ?? Matrix.Identity;
                full.Append(matrix);
                geometry.Transform = new MatrixTransform(full);
                visual.Geometry = geometry;
                visual.Bounds = geometry.Bounds;
                // Port markers have their own center connection site, not a device-edge approximation.
                visual.CenterConnection = vector is Rectangle && e.ActualWidth <= 16 && e.ActualHeight <= 16;
            }
            scene.Items.Add(visual);
            return;
        }
        if (e is TextBlock text)
        {
            if (string.IsNullOrEmpty(text.Text) && text.Inlines.Count == 0) return;
            var visual = TextVisual(e, matrix, name, opacity);
            visual.Fill = text.Background;
            visual.Padding = text.Padding;
            visual.Wrap = text.TextWrapping != TextWrapping.NoWrap;
            visual.Paragraphs = new() { new(ReadInlines(text.Inlines), text.TextAlignment) };
            if (visual.Paragraphs[0].Runs.Count == 0)
                visual.Paragraphs[0].Runs.Add(Run(text.Text, text.FontFamily, text.FontSize, text.Foreground,
                    text.FontWeight, text.FontStyle, text.TextDecorations));
            if (text.Effect is System.Windows.Media.Effects.DropShadowEffect shadow && shadow.ShadowDepth == 0)
            { visual.Glow = shadow.Color; visual.GlowRadius = shadow.BlurRadius; }
            scene.Items.Add(visual);
            return;
        }
        if (e is RichTextBox rich)
        {
            var visual = TextVisual(e, matrix, name + " 글자", opacity);
            visual.Fill = rich.Background;
            visual.Padding = rich.Padding;
            visual.Wrap = false;
            visual.Paragraphs = new();
            foreach (Block block in rich.Document.Blocks)
                if (block is Paragraph paragraph) visual.Paragraphs.Add(new(ReadLaidOutParagraph(paragraph), paragraph.TextAlignment));
                else { RasterRichText(rich, root, scene, name, opacity); return; }
            // Office can clip whole wrapped lines where WPF clips only the glyph edge.
            // Preserve difficult local text exactly, without flattening devices/cables.
            if (visual.Paragraphs.Count > 1 || visual.Paragraphs.SelectMany(p => p.Runs).Any(r => r.Text.Contains('\n')))
            { RasterRichText(rich, root, scene, name + " 서식 글자", opacity); return; }
            if (visual.Paragraphs.Count != 0) scene.Items.Add(visual);
            return;
        }
        if (e is Image image)
        {
            Raster(image, matrix, scene, name + " 이미지", opacity, false);
            return;
        }
        if (e is Border border)
        {
            if (Visible(border.Background) || Visible(border.BorderBrush))
            {
                if (border.Background is ImageBrush)
                    Raster(border, matrix, scene, name, opacity, true);
                else
                {
                    var geometry = new RectangleGeometry(new Rect(0, 0, e.ActualWidth, e.ActualHeight),
                        border.CornerRadius.TopLeft, border.CornerRadius.TopLeft) { Transform = new MatrixTransform(matrix) };
                    scene.Items.Add(new PptxVisual { Name = name, Bounds = geometry.Bounds,
                        Geometry = PathGeometry.CreateFromGeometry(geometry), Fill = border.Background,
                        Stroke = border.BorderBrush, Thickness = border.BorderThickness.Left, Opacity = opacity });
                }
            }
            if (border.Child is FrameworkElement child && border.Background is not ImageBrush)
                Walk(child, root, scene, cableLines, name, opacity, excluded);
            return;
        }
        if (e is Panel panel)
        {
            // Canvas backgrounds include the editor-only grid, so deliberately do not export them.
            foreach (FrameworkElement child in panel.Children.OfType<FrameworkElement>().OrderBy(Panel.GetZIndex))
                Walk(child, root, scene, cableLines, name, opacity, excluded);
            return;
        }
        // Hosts for rich text may have a ScrollViewer template. Walk the content, not its chrome.
        if (e is ContentControl content && content.Content is FrameworkElement nested)
        { Walk(nested, root, scene, cableLines, name, opacity, excluded); return; }
        if (VisualTreeHelper.GetChildrenCount(e) > 0)
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(e); i++)
                if (VisualTreeHelper.GetChild(e, i) is FrameworkElement child) Walk(child, root, scene, cableLines, name, opacity, excluded);
    }

    static PptxVisual TextVisual(FrameworkElement e, Matrix matrix, string name, double opacity)
    {
        double width = Math.Max(1, e.ActualWidth), height = Math.Max(1, e.ActualHeight);
        Point center = matrix.Transform(new Point(width / 2, height / 2));
        double sx = Math.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12);
        double sy = Math.Sqrt(matrix.M21 * matrix.M21 + matrix.M22 * matrix.M22);
        return new PptxVisual { Name = name, Bounds = new Rect(center.X - width * sx / 2, center.Y - height * sy / 2, width * sx, height * sy),
            Rotation = Math.Atan2(matrix.M12, matrix.M11) * 180 / Math.PI,
            FlipV = matrix.Determinant < 0, Opacity = opacity };
    }

    static PptxRun Run(string text, FontFamily font, double size, Brush foreground, FontWeight weight,
        FontStyle style, TextDecorationCollection? decorations) => new(text, font.Source, size, ColorOf(foreground),
            weight.ToOpenTypeWeight() >= 600, style != FontStyles.Normal, decorations?.Count > 0);
    static List<PptxRun> ReadInlines(InlineCollection inlines)
    {
        var result = new List<PptxRun>();
        foreach (Inline inline in inlines)
            switch (inline)
            {
                case System.Windows.Documents.Run run:
                    result.Add(Run(run.Text, run.FontFamily, run.FontSize, run.Foreground, run.FontWeight, run.FontStyle, run.TextDecorations)); break;
                case Span span: result.AddRange(ReadInlines(span.Inlines)); break;
                case LineBreak: result.Add(Run("\n", inline.FontFamily, inline.FontSize, inline.Foreground, inline.FontWeight, inline.FontStyle, null)); break;
            }
        return result;
    }

    // WPF and Office have different word-wrapping metrics. Retain the line breaks
    // chosen by the already laid-out document instead of wrapping it again in PPT.
    static List<PptxRun> ReadLaidOutParagraph(Paragraph paragraph)
    {
        var result = new List<PptxRun>();
        TextPointer? position = paragraph.ContentStart;
        var line = position.GetLineStartPosition(0);
        if (line == null) return ReadInlines(paragraph.Inlines);
        TextPointer? nextLine = line.GetLineStartPosition(1);
        while (position != null && position.CompareTo(paragraph.ContentEnd) < 0)
        {
            if (nextLine != null && position.CompareTo(nextLine) >= 0)
            {
                if (result.Count > 0) result.Add(result[^1] with { Text = "\n" });
                nextLine = nextLine.GetLineStartPosition(1);
            }
            if (position.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                string text = position.GetTextInRun(LogicalDirection.Forward);
                int count = Math.Min(text.Length, position.GetOffsetToPosition(paragraph.ContentEnd));
                if (nextLine != null && position.CompareTo(nextLine) < 0) count = Math.Min(count, position.GetOffsetToPosition(nextLine));
                if (count > 0)
                {
                    var style = position.Parent as TextElement ?? paragraph;
                    result.Add(Run(text[..count], style.FontFamily, style.FontSize, style.Foreground,
                        style.FontWeight, style.FontStyle, (style as Inline)?.TextDecorations));
                    position = position.GetPositionAtOffset(count, LogicalDirection.Forward); continue;
                }
            }
            if (position.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart && position.GetAdjacentElement(LogicalDirection.Forward) is LineBreak)
                if (result.Count > 0) result.Add(result[^1] with { Text = "\n" });
            position = position.GetNextContextPosition(LogicalDirection.Forward);
        }
        return result.Count == 0 ? ReadInlines(paragraph.Inlines) : result;
    }

    static void RasterRichText(RichTextBox rich, Canvas root, PptxScene scene, string name, double opacity)
    {
        // A RichTextBox VisualBrush alone can omit wrapped lines in its internal
        // ScrollViewer. Its fixed-size display host preserves the existing layout,
        // clipping and shrink transform without reflowing the document.
        if (VisualTreeHelper.GetParent(rich) is Grid host && host.Children.Count == 1)
            Raster(host, MatrixTo(host, root), scene, name, opacity / rich.Opacity, true);
        else Raster(rich, MatrixTo(rich, root), scene, name, opacity, true);
    }

    static void Raster(FrameworkElement e, Matrix matrix, PptxScene scene, string name, double opacity, bool fallback)
    {
        if (e.ActualWidth <= 0 || e.ActualHeight <= 0) return;
        FrameworkElement source = e;
        if (e is Image image)
        {
            // A VisualBrush includes the image's own flip/opacity. The PPT transform
            // already carries that flip, so render an untransformed local image copy.
            source = new Image { Source = image.Source, Stretch = image.Stretch, StretchDirection = image.StretchDirection,
                Width = e.ActualWidth, Height = e.ActualHeight };
            source.Measure(new Size(e.ActualWidth, e.ActualHeight));
            source.Arrange(new Rect(0, 0, e.ActualWidth, e.ActualHeight)); source.UpdateLayout();
        }
        else opacity /= e.Opacity; // own opacity is baked into the local raster; parents are not
        // Only images/unsupported local visuals are rasterized, never the diagram or cable layer.
        double scale = Math.Min(2, Math.Min(4096 / e.ActualWidth, 4096 / e.ActualHeight));
        int width = Math.Max(1, (int)Math.Ceiling(e.ActualWidth * scale));
        int height = Math.Max(1, (int)Math.Ceiling(e.ActualHeight * scale));
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
            dc.DrawRectangle(new VisualBrush(source) { AutoLayoutContent = false, Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, e.ActualWidth, e.ActualHeight) }, null, new Rect(0, 0, e.ActualWidth, e.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream);
        var visual = TextVisual(e, matrix, name, opacity);
        visual.Image = stream.ToArray(); visual.RasterFallback = fallback;
        scene.Items.Add(visual);
    }

    internal static Rect PointsBounds(IEnumerable<Point> points)
    {
        var bounds = Rect.Empty;
        foreach (Point point in points) bounds.Union(point);
        return bounds;
    }
}
