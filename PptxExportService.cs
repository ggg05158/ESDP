using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Point = System.Windows.Point;

namespace ESDP;

internal sealed record PptxPage(string Name, PptxScene Scene, Rect Viewport);
internal sealed record PptxExportResult(int Slides, int Connectors, int Texts, int Vectors, int Images, int RasterFallbacks);

// Export a snapshot of the rendered diagram, not the routing model. All coordinates are
// explicit; no PowerPoint automation, routing, Office installation or new dependency is used.
internal static class PptxExportService
{
    static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static XElement E(XNamespace ns, string name, params object?[] contents) => new(ns + name, contents.Where(c => c != null));
    static XAttribute At(string name, object value) => new(name, value);
    static long Emu(double value) => (long)Math.Round(value * 9525);
    static string N(double value) => Math.Round(value).ToString(CultureInfo.InvariantCulture);
    static XElement EmptyTree() => E(P, "spTree",
        E(P, "nvGrpSpPr", E(P, "cNvPr", At("id", 1), At("name", "")), E(P, "cNvGrpSpPr"), E(P, "nvPr")),
        E(P, "grpSpPr", E(A, "xfrm", E(A, "off", At("x", 0), At("y", 0)), E(A, "ext", At("cx", 0), At("cy", 0)),
            E(A, "chOff", At("x", 0), At("y", 0)), E(A, "chExt", At("cx", 0), At("cy", 0)))));
    static void Feed(OpenXmlPart part, XElement element)
    {
        using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
        new XDocument(new XDeclaration("1.0", "utf-8", "yes"), element).Save(stream);
    }

    internal static PptxExportResult Write(string destination, IReadOnlyList<PptxPage> pages, bool portrait)
    {
        if (pages.Count is < 1 or > 500) throw new InvalidOperationException("PPT 내보내기는 1~500페이지까지 가능합니다.");
        if (pages.Any(p => p.Viewport.IsEmpty || p.Viewport.Width <= 0 || p.Viewport.Height <= 0))
            throw new InvalidOperationException("유효하지 않은 페이지 크기입니다.");
        string full = System.IO.Path.GetFullPath(destination);
        string temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full)!, "." + System.IO.Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        int connectors = 0, texts = 0, vectors = 0, images = 0, fallbacks = 0;
        // Exact physical A3. Source page scale affects the fit, never the slide's paper size.
        double width = (portrait ? 297 : 420) * 96 / 25.4, height = (portrait ? 420 : 297) * 96 / 25.4;
        try
        {
            using (var doc = PresentationDocument.Create(temporary, DocumentFormat.OpenXml.PresentationDocumentType.Presentation))
            {
                var presentation = doc.AddPresentationPart();
                var master = presentation.AddNewPart<SlideMasterPart>("rIdMaster");
                var layout = master.AddNewPart<SlideLayoutPart>("rIdLayout");
                layout.AddPart(master, "rIdMaster");
                var theme = master.AddNewPart<ThemePart>("rIdTheme"); Feed(theme, Theme());
                Feed(master, E(P, "sldMaster", Ns(), E(P, "cSld", EmptyTree()), ColorMap(),
                    E(P, "sldLayoutIdLst", E(P, "sldLayoutId", At("id", 2147483649U), new XAttribute(R + "id", "rIdLayout"))),
                    E(P, "txStyles", E(P, "titleStyle"), E(P, "bodyStyle"), E(P, "otherStyle"))));
                Feed(layout, E(P, "sldLayout", Ns(), At("type", "blank"), At("preserve", "1"),
                    E(P, "cSld", At("name", "ESDP A3"), EmptyTree()), E(P, "clrMapOvr", E(A, "masterClrMapping"))));
                var ids = E(P, "sldIdLst");
                for (int i = 0; i < pages.Count; i++)
                {
                    string rel = "rIdSlide" + (i + 1);
                    var slide = presentation.AddNewPart<SlidePart>(rel); slide.AddPart(layout, "rIdLayout");
                    var writer = new SlideWriter(slide, pages[i], width, height);
                    Feed(slide, writer.Build());
                    connectors += writer.Connectors; texts += writer.Texts; vectors += writer.Vectors;
                    images += writer.Images; fallbacks += writer.Fallbacks;
                    ids.Add(E(P, "sldId", At("id", 256 + i), new XAttribute(R + "id", rel)));
                }
                Feed(presentation, E(P, "presentation", Ns(),
                    E(P, "sldMasterIdLst", E(P, "sldMasterId", At("id", 2147483648U), new XAttribute(R + "id", "rIdMaster"))),
                    ids, E(P, "sldSz", At("cx", Emu(width)), At("cy", Emu(height)), At("type", "custom")),
                    E(P, "notesSz", At("cx", 6858000), At("cy", 9144000))));
                doc.PackageProperties.Title = "ESDP 구성도";
                doc.PackageProperties.Creator = "ESDP";
            }
            // Reopen the actual ZIP package before replacing any existing user file.
            using (var check = PresentationDocument.Open(temporary, false))
            {
                var errors = new OpenXmlValidator().Validate(check).Take(8).ToArray();
                if (errors.Length != 0) throw new InvalidDataException("PPT 문서 검증 실패: " + string.Join(" / ", errors.Select(e => e.Description)));
            }
            File.Move(temporary, full, true);
            return new(pages.Count, connectors, texts, vectors, images, fallbacks);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    static object[] Ns() => new object[] { new XAttribute(XNamespace.Xmlns + "p", P), new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", R) };
    static XElement ColorMap() => E(P, "clrMap", At("bg1", "lt1"), At("tx1", "dk1"), At("bg2", "lt2"), At("tx2", "dk2"),
        At("accent1", "accent1"), At("accent2", "accent2"), At("accent3", "accent3"), At("accent4", "accent4"), At("accent5", "accent5"), At("accent6", "accent6"), At("hlink", "hlink"), At("folHlink", "folHlink"));
    static XElement Theme()
    {
        var scheme = E(A, "clrScheme", At("name", "ESDP"));
        string[] names = { "dk1", "lt1", "dk2", "lt2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" };
        string[] colors = { "000000", "FFFFFF", "333333", "F5F5F5", "C87539", "ADD8E6", "FFB6C1", "71A86B", "977AB0", "D5AB4B", "0563C1", "954F72" };
        for (int i = 0; i < names.Length; i++) scheme.Add(E(A, names[i], E(A, "srgbClr", At("val", colors[i]))));
        XElement Fonts(string name) => E(A, name, E(A, "latin", At("typeface", "맑은 고딕")), E(A, "ea", At("typeface", "맑은 고딕")), E(A, "cs", At("typeface", "Arial")));
        XElement Fill() => E(A, "solidFill", E(A, "schemeClr", At("val", "phClr")));
        return E(A, "theme", new XAttribute(XNamespace.Xmlns + "a", A), At("name", "ESDP"), E(A, "themeElements", scheme,
            E(A, "fontScheme", At("name", "ESDP"), Fonts("majorFont"), Fonts("minorFont")),
            E(A, "fmtScheme", At("name", "ESDP"), E(A, "fillStyleLst", Fill(), Fill(), Fill()),
                E(A, "lnStyleLst", Enumerable.Range(0, 3).Select(_ => E(A, "ln", At("w", 9525), Fill(), E(A, "prstDash", At("val", "solid"))))),
                E(A, "effectStyleLst", Enumerable.Range(0, 3).Select(_ => E(A, "effectStyle", E(A, "effectLst")))),
                E(A, "bgFillStyleLst", Fill(), Fill(), Fill()))));
    }

    sealed class SlideWriter
    {
        readonly SlidePart part; readonly PptxPage page; readonly double scale, ox, oy;
        readonly XElement tree = EmptyTree(); readonly Dictionary<(XElement, long, long), uint> anchors = new();
        XElement output;
        HashSet<PptxVisual>? activePorts;
        readonly Dictionary<PptxVisual, uint> ports = new(); uint nextId = 2;
        public int Connectors, Texts, Vectors, Images, Fallbacks;
        public SlideWriter(SlidePart part, PptxPage page, double width, double height)
        {
            this.part = part; this.page = page;
            output = tree;
            scale = Math.Min(width / page.Viewport.Width, height / page.Viewport.Height);
            ox = (width - page.Viewport.Width * scale) / 2 - page.Viewport.X * scale;
            oy = (height - page.Viewport.Height * scale) / 2 - page.Viewport.Y * scale;
        }
        Point Map(Point p) => new(p.X * scale + ox, p.Y * scale + oy);
        Rect Map(Rect r) => new(Map(r.TopLeft), new Size(Math.Max(.0001, r.Width * scale), Math.Max(.0001, r.Height * scale)));
        (long, long) Key(Point p) => (Emu(p.X), Emu(p.Y));
        XElement Xform(Rect r, double rotation = 0, bool flipH = false, bool flipV = false) => E(A, "xfrm",
            rotation == 0 ? null : At("rot", N((rotation % 360 + 360) % 360 * 60000)), flipH ? At("flipH", "1") : null, flipV ? At("flipV", "1") : null,
            E(A, "off", At("x", Emu(r.X)), At("y", Emu(r.Y))), E(A, "ext", At("cx", Math.Max(1, Emu(r.Width))), At("cy", Math.Max(1, Emu(r.Height)))));
        XElement Nv(uint id, string name, bool textbox = false) => E(P, "nvSpPr", E(P, "cNvPr", At("id", id), At("name", name)), E(P, "cNvSpPr", textbox ? At("txBox", "1") : null), E(P, "nvPr"));
        XElement Solid(Color color, double opacity = 1) => E(A, "solidFill", E(A, "srgbClr", At("val", $"{color.R:X2}{color.G:X2}{color.B:X2}"),
            color.A != 255 || opacity < 1 ? E(A, "alpha", At("val", N(Math.Clamp(color.A / 255.0 * opacity, 0, 1) * 100000))) : null));
        XElement Fill(Brush? brush, double opacity) => brush is SolidColorBrush b ? Solid(b.Color, opacity * b.Opacity) : E(A, "noFill");
        XElement Line(PptxVisual v) => E(A, "ln", At("w", Math.Max(0, Emu(v.Thickness * scale))), At("cap", v.Cap == PenLineCap.Round ? "rnd" : v.Cap == PenLineCap.Square ? "sq" : "flat"),
            Fill(v.Stroke, v.Opacity), v.Dash?.Count > 0 ? E(A, "custDash", Enumerable.Range(0, (v.Dash.Count + 1) / 2).Select(i =>
                E(A, "ds", At("d", N(Math.Max(.01, v.Dash[i * 2]) * 100000)), At("sp", N(Math.Max(.01, v.Dash[(i * 2 + 1) % v.Dash.Count]) * 100000))))) : E(A, "prstDash", At("val", "solid")), E(A, "round"));
        public XElement Build()
        {
            // Reserve port IDs before writing connectors so the endpoint can use the visible
            // connector glyph's center connection site, even though glyphs are drawn later.
            foreach (var item in PptxSceneCapture.Descendants(page.Scene.Items).Where(i => i.CenterConnection && Intersects(i)))
                ports[item] = nextId++;
            foreach (var item in page.Scene.Items)
                WriteItem(item);
            return E(P, "sld", Ns(), E(P, "cSld", At("name", page.Name), E(P, "bg", E(P, "bgPr", Solid(Colors.White), E(A, "effectLst"))), tree), E(P, "clrMapOvr", E(A, "masterClrMapping")));
        }
        void WriteItem(PptxVisual item)
        {
            if (!Intersects(item)) return;
            if (item.Children != null) Group(item);
            else if (item.Cable && item.Route is { Count: > 1 } route) Cable(item, route);
            else if (item.Image != null) Picture(item);
            else if (item.Paragraphs != null && item.Geometry == null) Text(item);
            else DrawVector(item);
        }
        void Group(PptxVisual item)
        {
            Rect bounds = Map(item.Bounds);
            // Identity child coordinates preserve the captured absolute positions.
            // A subsequent PowerPoint move/resize transforms the entire group.
            var xf = Xform(bounds);
            xf.Add(E(A, "chOff", At("x", Emu(bounds.X)), At("y", Emu(bounds.Y))),
                E(A, "chExt", At("cx", Math.Max(1, Emu(bounds.Width))), At("cy", Math.Max(1, Emu(bounds.Height)))));
            var group = E(P, "grpSp", E(P, "nvGrpSpPr", E(P, "cNvPr", At("id", nextId++), At("name", item.Name)),
                E(P, "cNvGrpSpPr"), E(P, "nvPr")), E(P, "grpSpPr", xf));
            var parent = output;
            var parentPorts = activePorts;
            output = group;
            activePorts = PptxSceneCapture.Descendants(item.Children!).Where(v => v.CenterConnection).ToHashSet();
            foreach (var child in item.Children!) WriteItem(child);
            output = parent;
            activePorts = parentPorts;
            if (group.Elements().Count() > 2) output.Add(group);
        }
        bool Intersects(PptxVisual v)
        {
            var box = v.Bounds;
            if (box.IsEmpty) return false;
            if (v.TextBounds.HasValue) box.Union(v.TextBounds.Value);
            // Rotated labels/images can cross a page boundary even if their unrotated box does not.
            if (v.Rotation != 0) { var m = Matrix.Identity; m.RotateAt(v.Rotation, box.X + box.Width / 2, box.Y + box.Height / 2); box = Rect.Transform(box, m); }
            box.Inflate(Math.Max(1, v.Thickness), Math.Max(1, v.Thickness));
            return box.IntersectsWith(page.Viewport);
        }
        uint Anchor(Point source)
        {
            foreach (var port in ports)
                if ((activePorts == null || activePorts.Contains(port.Key)) &&
                    (new Point(port.Key.Bounds.X + port.Key.Bounds.Width / 2, port.Key.Bounds.Y + port.Key.Bounds.Height / 2) - source).Length < .05)
                    return port.Value;
            Point p = Map(source); var position = Key(p); var key = (output, position.Item1, position.Item2);
            if (anchors.TryGetValue(key, out uint found)) return found;
            uint id = nextId++; anchors[key] = id;
            var bounds = new Rect(p.X - .0001, p.Y - .0001, .0002, .0002);
            // Native connectors must move with their anchors when the cable group
            // moves. Never share an invisible anchor with another cable group.
            output.Add(E(P, "sp", Nv(id, "접점 (경로 고정)"), E(P, "spPr", Xform(bounds), Custom(new List<XElement>(), 2, 2, true), E(A, "noFill"), E(A, "ln", E(A, "noFill")))));
            return id;
        }
        void Cable(PptxVisual item, List<Point> route)
        {
            // One authored leg is one PPT object, regardless of its number of bends
            // or junctions. Clip individual segments but retain separate subpaths
            // inside that same object when a leg leaves and re-enters a slide.
            var paths = new List<List<Point>>();
            List<Point>? current = null;
            for (int i = 1; i < route.Count; i++)
            {
                if ((route[i] - route[i - 1]).Length < .00001) continue;
                if (!Clip(route[i - 1], route[i], page.Viewport, out Point from, out Point to) || (from - to).Length < .00001)
                {
                    current = null; continue;
                }
                if (current == null || (current[^1] - from).Length > .00001)
                { current = new() { from }; paths.Add(current); }
                current.Add(to);
                if ((to - route[i]).Length > .00001) current = null;
            }
            if (paths.Count == 0) return;
            if (paths.Count == 1)
            {
                var points = paths[0];
                if (points.Count == 2) { Connector(item, points[0], null, points[1]); return; }
                if (points.Count == 3 && Math.Abs(Vector.Multiply(points[1] - points[0], points[2] - points[1])) < .001)
                { Connector(item, points[0], points[1], points[2]); return; }
            }
            WholeLegFreeform(item, paths);
        }
        void WholeLegFreeform(PptxVisual item, List<List<Point>> paths)
        {
            uint id = nextId++;
            Rect bounds = Map(PptxSceneCapture.PointsBounds(paths.SelectMany(p => p)));
            XElement Pt(Point source)
            {
                Point p = Map(source);
                return E(A, "pt", At("x", Emu(p.X - bounds.X)), At("y", Emu(p.Y - bounds.Y)));
            }
            var commands = new List<XElement>();
            foreach (var points in paths)
            {
                commands.Add(E(A, "moveTo", Pt(points[0])));
                commands.AddRange(points.Skip(1).Select(p => E(A, "lnTo", Pt(p))));
            }
            // PowerPoint rejects arbitrary custom geometry in p:cxnSp even when
            // it passes Open XML schema validation. A native p:sp freeform keeps
            // all points editable, one object per leg, without a repair prompt.
            var geometry = Custom(commands, Emu(bounds.Width), Emu(bounds.Height), false);
            geometry.Element(A + "pathLst")!.Element(A + "path")!.SetAttributeValue("fill", "none");
            output.Add(E(P, "sp", Nv(id, item.Name),
                E(P, "spPr", Xform(bounds), geometry, E(A, "noFill"), Line(item))));
            Connectors++;
        }
        void Connector(PptxVisual item, Point start, Point? bend, Point end)
        {
            uint beginId = Anchor(start), endId = Anchor(end), id = nextId++;
            Point a = Map(start), b = Map(end);
            double rotation = 0; bool flipH, flipV; Rect bounds;
            if (bend.HasValue)
            {
                Vector u = Map(bend.Value) - a; u.Normalize(); Vector v = new(-u.Y, u.X), delta = b - a;
                double w = Vector.Multiply(delta, u), h = Vector.Multiply(delta, v);
                rotation = Math.Atan2(u.Y, u.X) * 180 / Math.PI; flipH = w < 0; flipV = h < 0;
                bounds = new Rect((a.X + b.X) / 2 - Math.Abs(w) / 2, (a.Y + b.Y) / 2 - Math.Abs(h) / 2, Math.Abs(w), Math.Abs(h));
            }
            else
            {
                bounds = new Rect(a, b); flipH = b.X < a.X; flipV = b.Y < a.Y;
            }
            output.Add(E(P, "cxnSp", E(P, "nvCxnSpPr", E(P, "cNvPr", At("id", id), At("name", item.Name)),
                E(P, "cNvCxnSpPr", E(A, "stCxn", At("id", beginId), At("idx", 0)), E(A, "endCxn", At("id", endId), At("idx", 0))), E(P, "nvPr")),
                E(P, "spPr", Xform(bounds, rotation, flipH, flipV), E(A, "prstGeom", At("prst", bend.HasValue ? "bentConnector2" : "line"), E(A, "avLst")), Line(item))));
            Connectors++;
        }
        static XElement Custom(List<XElement> commands, long width, long height, bool connection) => E(A, "custGeom", E(A, "avLst"), E(A, "gdLst"), E(A, "ahLst"),
            E(A, "cxnLst", connection ? E(A, "cxn", At("ang", 0), E(A, "pos", At("x", "hc"), At("y", "vc"))) : null),
            E(A, "rect", At("l", 0), At("t", 0), At("r", "w"), At("b", "h")), E(A, "pathLst", E(A, "path", At("w", Math.Max(1, width)), At("h", Math.Max(1, height)), commands)));
        void DrawVector(PptxVisual item)
        {
            var commands = new List<XElement>(); Rect source = item.Bounds;
            XElement Pt(Point p) { p = Map(p); Rect box = Map(source); return E(A, "pt", At("x", Emu(p.X - box.X)), At("y", Emu(p.Y - box.Y))); }
            if (item.Geometry != null)
            {
                PathGeometry geometry = item.Geometry;
                if (geometry.Figures.SelectMany(f => f.Segments).Any(s => s is ArcSegment)) geometry = geometry.GetFlattenedPathGeometry(.05, ToleranceType.Absolute);
                var transform = geometry.Transform?.Value ?? Matrix.Identity;
                foreach (var figure in geometry.Figures)
                {
                    commands.Add(E(A, "moveTo", Pt(transform.Transform(figure.StartPoint))));
                    foreach (var segment in figure.Segments)
                        switch (segment)
                        {
                            case LineSegment l: commands.Add(E(A, "lnTo", Pt(transform.Transform(l.Point)))); break;
                            case PolyLineSegment l: foreach (Point p in l.Points) commands.Add(E(A, "lnTo", Pt(transform.Transform(p)))); break;
                            case BezierSegment b: commands.Add(E(A, "cubicBezTo", Pt(transform.Transform(b.Point1)), Pt(transform.Transform(b.Point2)), Pt(transform.Transform(b.Point3)))); break;
                            case PolyBezierSegment b: for (int i = 0; i + 2 < b.Points.Count; i += 3) commands.Add(E(A, "cubicBezTo", Pt(transform.Transform(b.Points[i])), Pt(transform.Transform(b.Points[i + 1])), Pt(transform.Transform(b.Points[i + 2])))); break;
                            case QuadraticBezierSegment q: commands.Add(E(A, "quadBezTo", Pt(transform.Transform(q.Point1)), Pt(transform.Transform(q.Point2)))); break;
                            case PolyQuadraticBezierSegment q: for (int i = 0; i + 1 < q.Points.Count; i += 2) commands.Add(E(A, "quadBezTo", Pt(transform.Transform(q.Points[i])), Pt(transform.Transform(q.Points[i + 1])))); break;
                        }
                    if (figure.IsClosed) commands.Add(E(A, "close"));
                }
            }
            else if (item.Route is { Count: > 0 })
            {
                commands.Add(E(A, "moveTo", Pt(item.Route[0])));
                commands.AddRange(item.Route.Skip(1).Select(p => E(A, "lnTo", Pt(p))));
            }
            else return;
            Rect bounds = Map(source); uint id = ports.TryGetValue(item, out uint portId) ? portId : nextId++;
            var custom = Custom(commands, Emu(bounds.Width), Emu(bounds.Height), item.CenterConnection);
            if (item.TextBounds is Rect textBounds)
            {
                Rect label = Map(textBounds);
                // Text is native to the body. Proportional guides retain its
                // chosen position even after resizing the shape in PowerPoint.
                var guides = custom.Element(A + "gdLst")!;
                var rect = custom.Element(A + "rect")!;
                void Edge(string attr, string guide, string axis, double value, double length)
                {
                    guides.Add(E(A, "gd", At("name", guide), At("fmla", $"*/ {axis} {Emu(value)} {Math.Max(1, Emu(length))}")));
                    rect.SetAttributeValue(attr, guide);
                }
                Edge("l", "textLeft", "w", label.Left - bounds.Left, bounds.Width);
                Edge("r", "textRight", "w", label.Right - bounds.Left, bounds.Width);
                Edge("t", "textTop", "h", label.Top - bounds.Top, bounds.Height);
                Edge("b", "textBottom", "h", label.Bottom - bounds.Top, bounds.Height);
            }
            output.Add(E(P, "sp", Nv(id, item.Name + " 벡터"), E(P, "spPr", Xform(bounds), custom,
                item.ImageFill != null ? PictureFill(item.ImageFill, item.Opacity, A) : Fill(item.Fill, item.Opacity), Line(item)),
                item.Paragraphs != null ? TextBody(item) : null));
            if (item.Paragraphs != null) Texts++;
            Vectors++;
        }
        XElement TextBody(PptxVisual item)
        {
            var paragraphs = new List<XElement>();
            foreach (var para in item.Paragraphs!)
            {
                var p = E(A, "p", E(A, "pPr", At("algn", para.Alignment == TextAlignment.Center ? "ctr" : para.Alignment == TextAlignment.Right ? "r" : para.Alignment == TextAlignment.Justify ? "just" : "l")));
                foreach (var run in para.Runs)
                {
                    var props = E(A, "rPr", At("lang", "ko-KR"), At("sz", N(Math.Clamp(run.Size * scale * 75, 100, 400000))),
                        run.Bold ? At("b", "1") : null, run.Italic ? At("i", "1") : null, run.Underline ? At("u", "sng") : null,
                        Solid(run.Color, item.Opacity), item.Glow.HasValue ? E(A, "effectLst", E(A, "glow", At("rad", Emu(item.GlowRadius * scale)),
                            E(A, "srgbClr", At("val", $"{item.Glow.Value.R:X2}{item.Glow.Value.G:X2}{item.Glow.Value.B:X2}")))) : null,
                        item.Highlight.HasValue ? E(A, "highlight", E(A, "srgbClr", At("val", $"{item.Highlight.Value.R:X2}{item.Highlight.Value.G:X2}{item.Highlight.Value.B:X2}"))) : null,
                        E(A, "latin", At("typeface", run.Font)), E(A, "ea", At("typeface", run.Font)), E(A, "cs", At("typeface", run.Font)));
                    string[] lines = run.Text.Replace("\r", "").Split('\n');
                    for (int i = 0; i < lines.Length; i++) { if (i != 0) p.Add(E(A, "br", new XElement(props))); p.Add(E(A, "r", new XElement(props), E(A, "t", lines[i]))); }
                }
                p.Add(E(A, "endParaRPr", At("lang", "ko-KR"))); paragraphs.Add(p);
            }
            return E(P, "txBody", E(A, "bodyPr", At("wrap", item.Wrap ? "square" : "none"),
                At("vertOverflow", item.TextBounds.HasValue ? "overflow" : "clip"), At("horzOverflow", item.TextBounds.HasValue ? "overflow" : "clip"),
                At("lIns", Emu(item.Padding.Left * scale)), At("rIns", Emu(item.Padding.Right * scale)),
                At("tIns", Emu(item.Padding.Top * scale)), At("bIns", Emu(item.Padding.Bottom * scale)),
                At("anchor", "t"), E(A, "noAutofit")), E(A, "lstStyle"), paragraphs);
        }
        void Text(PptxVisual item)
        {
            output.Add(E(P, "sp", Nv(nextId++, item.Name + " 글자", true), E(P, "spPr", Xform(Map(item.Bounds), item.Rotation, item.FlipH, item.FlipV),
                E(A, "prstGeom", At("prst", "rect"), E(A, "avLst")), Fill(item.Fill, item.Opacity), E(A, "ln", E(A, "noFill"))), TextBody(item)));
            Texts++;
        }
        XElement PictureFill(byte[] bytes, double opacity, XNamespace ns)
        {
            var image = part.AddImagePart(ImagePartType.Png);
            using (var data = new MemoryStream(bytes)) image.FeedData(data);
            Images++;
            return E(ns, "blipFill", E(A, "blip", new XAttribute(R + "embed", part.GetIdOfPart(image)),
                opacity < 1 ? E(A, "alphaModFix", At("amt", N(Math.Clamp(opacity, 0, 1) * 100000))) : null), E(A, "stretch", E(A, "fillRect")));
        }
        void Picture(PptxVisual item)
        {
            output.Add(E(P, "pic", E(P, "nvPicPr", E(P, "cNvPr", At("id", nextId++), At("name", item.Name)), E(P, "cNvPicPr", E(A, "picLocks", At("noChangeAspect", "1"))), E(P, "nvPr")),
                PictureFill(item.Image!, item.Opacity, P),
                E(P, "spPr", Xform(Map(item.Bounds), item.Rotation, item.FlipH, item.FlipV), E(A, "prstGeom", At("prst", "rect"), E(A, "avLst")))));
            if (item.RasterFallback) Fallbacks++;
        }
    }

    // Liang-Barsky clips only copied connector coordinates at slide edges.
    internal static bool Clip(Point a, Point b, Rect viewport, out Point from, out Point to)
    {
        double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        bool Bound(double p, double q)
        {
            if (Math.Abs(p) < 1e-12) return q >= 0;
            double r = q / p;
            if (p < 0) { if (r > t1) return false; t0 = Math.Max(t0, r); }
            else { if (r < t0) return false; t1 = Math.Min(t1, r); }
            return true;
        }
        bool ok = Bound(-dx, a.X - viewport.Left) && Bound(dx, viewport.Right - a.X) && Bound(-dy, a.Y - viewport.Top) && Bound(dy, viewport.Bottom - a.Y);
        from = new(a.X + dx * t0, a.Y + dy * t0); to = new(a.X + dx * t1, a.Y + dy * t1); return ok;
    }
}
