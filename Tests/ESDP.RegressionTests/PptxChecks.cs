using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using ESDP;
using ESDP.Models;
using P = System.Windows.Point;

internal static class PptxChecks
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main", Ns = "http://schemas.openxmlformats.org/presentationml/2006/main";
    static object? W(MainWindow w, string name, params object[] args) => typeof(MainWindow).GetMethod(name, Flags)!.Invoke(w, args);
    static T F<T>(object w, string name) => (T)w.GetType().GetField(name, Flags)!.GetValue(w)!;
    static void Check(bool valid, string message) { if (!valid) throw new Exception("PPT regression: " + message); }
    static object Export(string path, object pages, bool portrait) => typeof(MainWindow).Assembly.GetType("ESDP.PptxExportService")!
        .GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new[] { path, pages, portrait })!;
    static long Number(XElement e, string attr) => long.Parse(e.Attribute(attr)!.Value);
    static IEnumerable<XElement> CableLegs(XDocument xml) => xml.Descendants().Where(e =>
        e.Name == Ns + "cxnSp" || e.Name == Ns + "sp" && e.Element(Ns + "nvSpPr")?.Element(Ns + "cNvPr")?.Attribute("name")?.Value.Contains("가닥") == true);

    public static void Run(string fixture, string output)
    {
        Directory.CreateDirectory(output);
        var project = ProjectFileService.LoadPrepared(fixture).Project;
        var window = new MainWindow();
        typeof(MainWindow).GetField("_isPdfExportRendering", Flags)!.SetValue(window, true);
        W(window, "ApplyProjectToEditor", project, 0); W(window, "PrepareCurrentCanvasForPdfExport");
        string before = JsonSerializer.Serialize(project);
        var canvas = F<Canvas>(window, "MainCanvas");
        var segments = F<List<PlacedCable>>(window, "_placedCables").SelectMany(c => c.RenderedSegments).ToList();
        var pages = W(window, "CapturePptxPages", new[] { 0 })!;
        string file = Path.Combine(output, "grid-test.pptx");
        if (File.Exists(file)) { VerifyRoutes(file, pages, segments); Console.WriteLine("PASS: existing PPT (including PowerPoint save round-trip) still has exact source connector coordinates/attachments"); }
        var result = Export(file, pages, false);
        Check(before == JsonSerializer.Serialize(project), "source project modified during capture/write");
        using (var doc = PresentationDocument.Open(file, false))
        {
            Check(!new OpenXmlValidator().Validate(doc).Any(), "schema invalid");
            var size = doc.PresentationPart!.Presentation!.SlideSize!;
            Check(size!.Cx!.Value == 15120000 && size.Cy!.Value == 10692000, "not exact A3 landscape");
            int connectorCount = 0, textCount = 0;
            foreach (var slide in doc.PresentationPart.SlideParts)
            {
                var xml = XDocument.Load(slide.GetStream());
                connectorCount += CableLegs(xml).Count();
                textCount += xml.Descendants(Ns + "txBody").Count();
                var ids = xml.Descendants(Ns + "cNvPr").Select(e => Number(e, "id")).ToArray();
                Check(ids.Distinct().Count() == ids.Length, "duplicate shape ID");
                foreach (var conn in xml.Descendants(Ns + "cxnSp"))
                    foreach (var link in conn.Descendants().Where(e => e.Name == A + "stCxn" || e.Name == A + "endCxn"))
                        Check(ids.Contains(Number(link, "id")), "dangling connection reference");
            }
            Check(connectorCount > 0 && textCount > 10, "diagram flattened or content missing");
            Check(doc.PresentationPart.SlideParts.Any(s => XDocument.Load(s.GetStream()).Descendants(A + "t").Any(t => t.Value.Contains("CX-"))), "cable names missing");
        }
        // Compare every emitted native connector segment to the exact rendered cable routes.
        VerifyRoutes(file, pages, segments);
        VerifyCableGroups(file, F<List<PlacedCable>>(window, "_placedCables"));
        using (var whole = PresentationDocument.Open(file, false))
        {
            var first = whole.PresentationPart!.Presentation!.SlideIdList!.Elements<DocumentFormat.OpenXml.Presentation.SlideId>().First();
            var xml = XDocument.Load(whole.PresentationPart.GetPartById(first.RelationshipId!).GetStream());
            var legs = CableLegs(xml).ToArray();
            Check(legs.Length == segments.Select(s => s.LegKey).Distinct().Count(), "one authored leg split into multiple PPT objects");
            Check(legs.Any(c => c.Descendants(A + "custGeom").Any() && c.Descendants(A + "lnTo").Count() >= 4), "multi-bend whole leg missing");
            Check(legs.All(c => c.Descendants(Ns + "cNvPr").First().Attribute("name")!.Value.Contains("가닥")), "leg selection names missing");
        }
        SaveCanvas(canvas, Path.Combine(output, "grid-test-source.png"));
        Export(Path.Combine(output, "grid-test-portrait.pptx"), pages, true);
        using (var portrait = PresentationDocument.Open(Path.Combine(output, "grid-test-portrait.pptx"), false))
            Check(portrait.PresentationPart!.Presentation!.SlideSize!.Cx!.Value == 10692000, "portrait A3 size");
        // Invalid requests cannot overwrite an existing file.
        string guard = Path.Combine(output, "guard.pptx"); File.WriteAllBytes(guard, new byte[] { 1, 2, 3 });
        var empty = Activator.CreateInstance(pages.GetType())!;
        try { Export(guard, empty, false); throw new Exception("Empty export accepted"); } catch (TargetInvocationException) { }
        Check(File.ReadAllBytes(guard).SequenceEqual(new byte[] { 1, 2, 3 }), "failed export replaced destination");
        Console.WriteLine("PASS: PPT grid_test: " + result + "; source unchanged; A3 sizes; one editable object per leg; exact route geometry");
        TestGallery(window, output);
        TestWholeLegs(output);
        TestDevices(window, output);
        typeof(MainWindow).GetField("_isDirtyValue", Flags)!.SetValue(window, false); window.Close();
    }

    static void VerifyRoutes(string file, object pages, List<(P A, P B, string LegKey)> original)
    {
        double total = 0;
        var viewport = (Rect)((IList)pages)[0]!.GetType().GetProperty("Viewport")!.GetValue(((IList)pages)[0])!;
        double scale = Math.Min(420 * 96 / 25.4 / viewport.Width, 297 * 96 / 25.4 / viewport.Height);
        double ox = (420 * 96 / 25.4 - viewport.Width * scale) / 2 - viewport.X * scale;
        double oy = (297 * 96 / 25.4 - viewport.Height * scale) / 2 - viewport.Y * scale;
        using var doc = PresentationDocument.Open(file, false);
        var presentation = doc.PresentationPart!;
        var first = presentation.Presentation!.SlideIdList!.Elements<DocumentFormat.OpenXml.Presentation.SlideId>().First();
        var xml = XDocument.Load(presentation.GetPartById(first.RelationshipId!).GetStream());
        foreach (var connector in CableLegs(xml))
        {
            var xf = connector.Descendants(A + "xfrm").First();
            double x = Number(xf.Element(A + "off")!, "x") / 9525.0, y = Number(xf.Element(A + "off")!, "y") / 9525.0;
            double w = Number(xf.Element(A + "ext")!, "cx") / 9525.0, h = Number(xf.Element(A + "ext")!, "cy") / 9525.0;
            bool fh = xf.Attribute("flipH")?.Value == "1", fv = xf.Attribute("flipV")?.Value == "1";
            double rotation = (double?)xf.Attribute("rot") / 60000.0 ?? 0;
            P Convert(P p)
            {
                if (fh) p.X = w - p.X; if (fv) p.Y = h - p.Y;
                var matrix = Matrix.Identity; matrix.RotateAt(rotation, w / 2, h / 2); p = matrix.Transform(p);
                return new((p.X + x - ox) / scale, (p.Y + y - oy) / scale);
            }
            var subpaths = new List<List<P>>();
            if (connector.Descendants(A + "custGeom").FirstOrDefault() is { } custom)
            {
                foreach (var path in custom.Descendants(A + "path"))
                {
                    double pw = Number(path, "w"), ph = Number(path, "h");
                    foreach (var command in path.Elements())
                    {
                        if (command.Name == A + "moveTo") subpaths.Add(new());
                        if (command.Name == A + "moveTo" || command.Name == A + "lnTo")
                        {
                            var p = command.Element(A + "pt")!;
                            subpaths[^1].Add(Convert(new P(Number(p, "x") / pw * w, Number(p, "y") / ph * h)));
                        }
                    }
                }
            }
            else subpaths.Add(connector.Descendants(A + "prstGeom").First().Attribute("prst")!.Value == "bentConnector2"
                ? new() { Convert(new P()), Convert(new P(w, 0)), Convert(new P(w, h)) }
                : new() { Convert(new P()), Convert(new P(w, h)) });
            foreach (var points in subpaths)
                for (int i = 1; i < points.Count; i++)
                {
                    P a = points[i - 1], b = points[i], midpoint = new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                    total += (b - a).Length;
                    Check(original.Any(s => Distance(midpoint, s.A, s.B) < .005), $"connector departed from source route: {a} -> {b}");
                }
            if (connector.Name != Ns + "cxnSp") continue; // freeforms intentionally have no automatic attachments
            foreach (var pair in new[] { ("stCxn", subpaths[0][0]), ("endCxn", subpaths[^1][^1]) })
            {
                long id = Number(connector.Descendants(A + pair.Item1).First(), "id");
                var shape = xml.Descendants(Ns + "sp").First(s => Number(s.Element(Ns + "nvSpPr")!.Element(Ns + "cNvPr")!, "id") == id);
                var sx = shape.Descendants(A + "xfrm").First();
                var off = sx.Element(A + "off")!; var ext = sx.Element(A + "ext")!;
                P center = new((Number(off, "x") + Number(ext, "cx") / 2.0) / 9525, (Number(off, "y") + Number(ext, "cy") / 2.0) / 9525);
                center = new((center.X - ox) / scale, (center.Y - oy) / scale);
                Check((center - pair.Item2).Length < .005, "connector and fixed attachment do not coincide");
            }
        }
        double expected = original.Sum(s => (s.B - s.A).Length);
        Check(Math.Abs(total - expected) < .1, $"route length changed: {expected} -> {total}");
    }
    static double Distance(P p, P a, P b)
    {
        var d = b - a; if (d.LengthSquared < .00001) return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, d) / d.LengthSquared, 0, 1); return (p - (a + d * t)).Length;
    }
    static void VerifyCableGroups(string file, List<PlacedCable> original)
    {
        using var doc = PresentationDocument.Open(file, false);
        var first = doc.PresentationPart!.Presentation!.SlideIdList!.Elements<DocumentFormat.OpenXml.Presentation.SlideId>().First();
        var xml = XDocument.Load(doc.PresentationPart.GetPartById(first.RelationshipId!).GetStream());
        string Name(XElement e) => e.Elements().First().Element(Ns + "cNvPr")!.Attribute("name")!.Value;
        var groups = xml.Descendants(Ns + "spTree").Single().Elements(Ns + "grpSp")
            .Where(g => Name(g).StartsWith("케이블 ")).ToArray();
        Check(groups.Length == original.Count, "cable instances must have distinct native groups, including repeated names");
        Check(groups.Sum(g => g.Elements(Ns + "grpSp").Count()) == original.Sum(c => 2 + c.Instance.Branches.Count),
            "connector glyphs/names are not nested inside their cable groups");
        Check(groups.Sum(g => g.Descendants(Ns + "txBody").Count()) == original.Sum(c => c.RenderedLabels.Count),
            "shared-layer cable/port labels were lost or duplicated");
        foreach (var group in groups)
        {
            Check(CableLegs(new XDocument(new XElement(group))).All(leg => Name(leg).StartsWith(Name(group) + " ")),
                "leg grouped with a different cable");
            Check(!group.Descendants(Ns + "cNvPr").Any(n => n.Attribute("name")?.Value.StartsWith("장치 ") == true),
                "device ports incorrectly included in cable group");
            foreach (var conn in group.Descendants(Ns + "cxnSp"))
                foreach (var link in conn.Descendants().Where(e => e.Name == A + "stCxn" || e.Name == A + "endCxn"))
                    Check(group.Descendants(Ns + "cNvPr").Any(n => Number(n, "id") == Number(link, "id")),
                        "connector attachment is outside its moving cable group");
        }
        Check(!xml.Descendants(Ns + "spTree").Single().Elements(Ns + "sp").Any(s => Name(s).StartsWith("케이블 ") && s.Element(Ns + "txBody") != null),
            "cable labels still exported loose on shared layer");
        Console.WriteLine("PASS: cable instance groups; nested connector/port-label groups; all shared-layer labels included once; device ports excluded; owned connector anchors");
    }
    static void SaveCanvas(Canvas canvas, string path)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, canvas.Width, canvas.Height)); dc.DrawRectangle(new VisualBrush(canvas), null, new Rect(0, 0, canvas.Width, canvas.Height)); }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(canvas.Width), (int)Math.Ceiling(canvas.Height), 96, 96, PixelFormats.Pbgra32); bmp.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp)); using var stream = File.Create(path); encoder.Save(stream);
    }
    static void TestWholeLegs(string output)
    {
        var assembly = typeof(MainWindow).Assembly;
        var sceneType = assembly.GetType("ESDP.PptxScene")!;
        var visualType = assembly.GetType("ESDP.PptxVisual")!;
        var pageType = assembly.GetType("ESDP.PptxPage")!;
        object Pages(Rect viewport, params List<P>[] routes)
        {
            var scene = Activator.CreateInstance(sceneType, true)!;
            var items = (IList)sceneType.GetProperty("Items")!.GetValue(scene)!;
            var junctions = (IList)sceneType.GetProperty("Junctions")!.GetValue(scene)!;
            for (int i = 0; i < routes.Length; i++)
            {
                var visual = Activator.CreateInstance(visualType, true)!;
                void Set(string key, object value) => visualType.GetField(key)!.SetValue(visual, value);
                Set("Cable", true); Set("Route", routes[i]); Set("Name", "테스트 " + (i == 0 ? "메인 가닥" : "분기 가닥 " + i));
                Set("Stroke", Brushes.Black); Set("Thickness", 2d);
                var box = Rect.Empty; foreach (var p in routes[i]) box.Union(p); Set("Bounds", box);
                items.Add(visual); if (i != 0) junctions.Add(routes[i][0]);
            }
            var page = Activator.CreateInstance(pageType, new object[] { "가닥 테스트", scene, viewport })!;
            var pages = Array.CreateInstance(pageType, 1); pages.SetValue(page, 0); return pages;
        }
        var root = new List<P> { new(10, 10), new(90, 10), new(90, 20), new(20, 20), new(20, 30), new(90, 30),
            new(90, 40), new(20, 40), new(20, 50), new(90, 50), new(90, 60), new(20, 60) };
        var branch = new List<P> { new(50, 30), new(50, 65), new(60, 65), new(60, 80), new(80, 80) };
        var nested = new List<P> { new(60, 70), new(30, 70), new(30, 85), new(50, 85) };
        var loop = new List<P> { new(10, 10), new(60, 10), new(60, 60), new(10, 60), new(10, 10) };
        var horizontal = new List<P> { new(10, 90), new(10, 90), new(25, 90), new(80, 90) };
        var vertical = new List<P> { new(95, 10), new(95, 10), new(95, 25), new(95, 80) };
        var viewport = new Rect(0, 0, 100, 100);
        var paths = new[] { root, branch, nested, loop, horizontal, vertical };
        var pages = Pages(viewport, paths);
        string file = Path.Combine(output, "whole-legs.pptx"); Export(file, pages, false);
        var segments = paths.SelectMany((path, leg) => path.Zip(path.Skip(1), (a, b) => (a, b, leg.ToString()))).ToList();
        VerifyRoutes(file, pages, segments);
        using (var doc = PresentationDocument.Open(file, false))
        {
            Check(!new OpenXmlValidator().Validate(doc).Any(), "whole-leg schema invalid");
            var xml = XDocument.Load(doc.PresentationPart!.SlideParts.First().GetStream());
            var legs = CableLegs(xml).ToArray();
            Check(legs.Length == paths.Length, "junctions, nested branches or repeated endpoints split a leg");
            Check(legs[0].Descendants(A + "lnTo").Count() == root.Count - 1, "high-bend path reduced or lost authored corners");
        }
        var clipped = new List<P> { new(10, 20), new(80, 20), new(80, 140), new(160, 140), new(160, 80), new(40, 80), new(40, 40) };
        var clippedPages = Pages(viewport, clipped);
        string clippedFile = Path.Combine(output, "whole-leg-clipped.pptx"); Export(clippedFile, clippedPages, false);
        var expected = new List<(P A, P B, string LegKey)> { (new(10, 20), new(80, 20), "root"), (new(80, 20), new(80, 100), "root"),
            (new(100, 80), new(40, 80), "root"), (new(40, 80), new(40, 40), "root") };
        VerifyRoutes(clippedFile, clippedPages, expected);
        using (var doc = PresentationDocument.Open(clippedFile, false))
        {
            Check(!new OpenXmlValidator().Validate(doc).Any(), "clipped whole-leg schema invalid");
            var xml = XDocument.Load(doc.PresentationPart!.SlideParts.First().GetStream());
            Check(CableLegs(xml).Count() == 1 && CableLegs(xml).Single().Descendants(A + "moveTo").Count() == 2,
                "leaving/re-entering page must retain one object and two separate subpaths, no artificial joining line");
        }
        Console.WriteLine("PASS: unlimited bends, parent/nested branches, closed endpoints, duplicate/flat points; one leg per object; page re-entry without joining gaps");
        // Straight/L native connectors need their invisible anchors inside their
        // moving cable group, even when two groups start at the same coordinate.
        var groupedPages = Pages(viewport, new List<P> { new(10, 10), new(80, 10) },
            new List<P> { new(10, 10), new(10, 40), new(80, 40) });
        var groupedScene = pageType.GetProperty("Scene")!.GetValue(((IList)groupedPages)[0])!;
        var groupedItems = (IList)sceneType.GetProperty("Items")!.GetValue(groupedScene)!;
        foreach (var leg in groupedItems.Cast<object>().ToArray())
        {
            int index = groupedItems.IndexOf(leg);
            var group = Activator.CreateInstance(visualType, true)!;
            var members = (IList)Activator.CreateInstance(groupedItems.GetType())!; members.Add(leg);
            visualType.GetField("Name")!.SetValue(group, "케이블 그룹 " + index);
            visualType.GetField("Bounds")!.SetValue(group, visualType.GetField("Bounds")!.GetValue(leg));
            visualType.GetField("Children")!.SetValue(group, members);
            groupedItems[index] = group;
        }
        string groupedFile = Path.Combine(output, "grouped-native-connectors.pptx"); Export(groupedFile, groupedPages, false);
        VerifyRoutes(groupedFile, groupedPages, new() { (new(10, 10), new(80, 10), "a"),
            (new(10, 10), new(10, 40), "b"), (new(10, 40), new(80, 40), "b") });
        using (var doc = PresentationDocument.Open(groupedFile, false))
        {
            Check(!new OpenXmlValidator().Validate(doc).Any(), "grouped native connectors schema invalid");
            var xml = XDocument.Load(doc.PresentationPart!.SlideParts.First().GetStream());
            var groups = xml.Descendants(Ns + "spTree").Single().Elements(Ns + "grpSp").ToArray();
            Check(groups.Length == 2, "native connectors not grouped separately");
            foreach (var group in groups)
                foreach (var link in group.Descendants().Where(e => e.Name == A + "stCxn" || e.Name == A + "endCxn"))
                    Check(group.Descendants(Ns + "cNvPr").Any(n => Number(n, "id") == Number(link, "id")),
                        "coincident native connectors shared anchors across cable groups");
        }
        Console.WriteLine("PASS: grouped straight/L native connectors own all anchors; coincident cables remain independent; original coordinates retained");
    }

    static void TestGallery(MainWindow window, string output)
    {
        var catalog = typeof(MainWindow).Assembly.GetType("ESDP.ShapeCatalog")!;
        var definitions = ((IEnumerable)catalog.GetField("All")!.GetValue(null)!).Cast<object>().ToArray();
        var sheet = new Sheet();
        for (int i = 0; i < definitions.Length; i++)
        {
            string kind = (string)definitions[i].GetType().GetProperty("Kind")!.GetValue(definitions[i])!;
            if (kind == "Image") continue;
            var shape = (CanvasShape)catalog.GetMethod("Create")!.Invoke(null, new object[] { kind, 145d, 90d })!;
            shape.X = 25 + i % 8 * 190; shape.Y = 35 + i / 8 * 220; shape.Text = "한글 " + kind; shape.FontSize = 13;
            shape.FillHex = i % 2 == 0 ? "#FFE9D9" : "#C8E2EA"; shape.RotationAngle = i % 5 == 0 ? 18 : 0;
            sheet.Shapes.Add(shape);
        }
        // Multipage clipping and mixed sheet sizes/orientation.
        sheet.TargetPageColumns = 2;
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        { dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 60, 40)); dc.DrawRectangle(Brushes.Blue, null, new Rect(60, 0, 60, 40)); }
        var image = new RenderTargetBitmap(120, 40, 96, 96, PixelFormats.Pbgra32); image.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var png = new MemoryStream(); encoder.Save(png);
        sheet.Shapes.Add(new CanvasShape { Kind = "Image", X = 1600, Y = 540, Width = 200, Height = 60,
            ImageDataBase64 = Convert.ToBase64String(png.ToArray()), ImageStretch = "Uniform", ImageOpacity = .45, RotationAngle = 18, FlipHorizontal = true });
        var second = new Sheet { Name = "세로 시트", PaperSize = PaperSize.A3_Portrait };
        second.Shapes.Add(new CanvasShape { Kind = "Ellipse", X = 150, Y = 150, Width = 200, Height = 100, Text = "두 번째 시트", FontSize = 24 });
        var project = new Project { Sheets = new() { sheet, second } };
        W(window, "ApplyProjectToEditor", project, 0); W(window, "PrepareCurrentCanvasForPdfExport");
        string before = JsonSerializer.Serialize(project);
        var pages = W(window, "CapturePptxPages", new[] { 0, 1 })!;
        Check(((IList)pages).Count == 3, "multipage/multisheet count");
        Export(Path.Combine(output, "gallery.pptx"), pages, false);
        SaveCanvas(F<Canvas>(window, "MainCanvas"), Path.Combine(output, "gallery-source.png"));
        Check(before == JsonSerializer.Serialize(project), "gallery source changed");
        using var doc = PresentationDocument.Open(Path.Combine(output, "gallery.pptx"), false);
        Check(!new OpenXmlValidator().Validate(doc).Any(), "gallery schema invalid");
        Check(doc.PresentationPart!.SlideParts.Count() == 3, "gallery slides missing");
        var slideXml = doc.PresentationPart.SlideParts.Select(s => XDocument.Load(s.GetStream())).ToArray();
        Check(slideXml.SelectMany(x => x.Descendants(A + "alphaModFix")).Any(x => Number(x, "amt") == 45000), "image opacity missing");
        Check(slideXml.SelectMany(x => x.Descendants(Ns + "pic")).Any(x => x.Descendants(A + "xfrm").Any(t => t.Attribute("flipV")?.Value == "1")), "flipped image transform missing");
        // The first wrapped label must retain both visible lines, not just its first line.
        var firstPage = ((IList)pages)[0]!;
        var scene = firstPage.GetType().GetProperty("Scene")!.GetValue(firstPage)!;
        var visuals = (IEnumerable)scene.GetType().GetProperty("Items")!.GetValue(scene)!;
        var fallback = FlattenVisuals(visuals).First(v => (bool)v.GetType().GetField("RasterFallback")!.GetValue(v)!);
        using var localPng = new MemoryStream((byte[])fallback.GetType().GetField("Image")!.GetValue(fallback)!);
        var localImage = new FormatConvertedBitmap(BitmapDecoder.Create(localPng, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
        int stride = localImage.PixelWidth * 4;
        var pixels = new byte[stride * localImage.PixelHeight]; localImage.CopyPixels(pixels, stride, 0);
        var inkRows = Enumerable.Range(0, localImage.PixelHeight).Where(y => Enumerable.Range(0, localImage.PixelWidth)
            .Any(x => pixels[y * stride + x * 4 + 3] > 100 && pixels[y * stride + x * 4] < 100)).ToArray();
        Check(inkRows.Length > 0 && inkRows[^1] - inkRows[0] >= 28, "wrapped local text lost its second line");
        Console.WriteLine("PASS: shape gallery, rotated/flipped/transparent images, native single-line text and exact wrapped text; multipage/mixed orientation; atomic failure protection");
    }
    static IEnumerable<object> FlattenVisuals(IEnumerable items)
    {
        foreach (object visual in items)
        {
            yield return visual;
            if (visual.GetType().GetField("Children")!.GetValue(visual) is IEnumerable children)
                foreach (var child in FlattenVisuals(children)) yield return child;
        }
    }
    static void TestDevices(MainWindow window, string output)
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        { dc.DrawRectangle(Brushes.DarkSlateBlue, null, new Rect(0, 0, 240, 100)); dc.DrawEllipse(Brushes.Orange, null, new P(160, 50), 32, 32); }
        var bitmap = new RenderTargetBitmap(240, 100, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream);
        var photo = new Device { Name = "사진 장치", BackgroundImageBase64 = Convert.ToBase64String(stream.ToArray()),
            HasExplicitBackgroundImageStretch = true, BackgroundImageStretch = "Uniform", BackgroundImageOpacity = .6,
            NameFontSize = 18, HasCustomNamePosition = true, NamePositionX = 1, NamePositionY = 1,
            Ports = new() { new() { Designator = "장치P1", Side = PortSide.Left },
                new() { Designator = "장치P2", Side = PortSide.Top, ShowConnType = true, ConnType = "D38999" },
                new() { Designator = "숨긴포트", Side = PortSide.Right, ShowLabel = false } } };
        var longName = new Device { Name = "본체보다 훨씬 긴 장치 이름도 잘리지 않아야 합니다", NameFontSize = 16 };
        var sheet = new Sheet { DeviceInstances = new() {
            new() { DeviceId = photo.Id, X = 80, Y = 80, Width = 280, Height = 180, Purpose = "A형" },
            new() { DeviceId = photo.Id, X = 500, Y = 80, Width = 280, Height = 180, Purpose = "B형" },
            new() { DeviceId = longName.Id, X = 500, Y = 400, Width = 100, Height = 80 } } };
        var project = new Project { Devices = new() { photo, longName }, Sheets = new() { sheet } };
        W(window, "ApplyProjectToEditor", project, 0); W(window, "PrepareCurrentCanvasForPdfExport");
        string before = JsonSerializer.Serialize(project);
        var pages = W(window, "CapturePptxPages", new[] { 0 })!;
        string file = Path.Combine(output, "semantic-devices.pptx"); Export(file, pages, false);
        SaveCanvas(F<Canvas>(window, "MainCanvas"), Path.Combine(output, "semantic-devices-source.png"));
        Check(before == JsonSerializer.Serialize(project), "semantic capture changed source devices");
        using var doc = PresentationDocument.Open(file, false);
        Check(!new OpenXmlValidator().Validate(doc).Any(), "native body text/groups/highlight schema invalid");
        var xml = XDocument.Load(doc.PresentationPart!.SlideParts.First().GetStream());
        var groups = xml.Descendants(Ns + "spTree").Single().Elements(Ns + "grpSp").ToArray();
        Check(groups.Length == 3, "device instances must be three distinct native groups");
        foreach (var group in groups)
        {
            var body = group.Elements(Ns + "sp").Single();
            Check(body.Element(Ns + "txBody") != null, "device name is not native body text");
            Check(body.Element(Ns + "nvSpPr")!.Element(Ns + "cNvSpPr")!.Attribute("txBox") == null, "body incorrectly exported as a textbox");
            Check(body.Descendants(A + "gd").Count() == 4, "custom name placement guides missing");
            var bodyTexts = body.Descendants(A + "t").Select(t => t.Value).ToArray();
            if (bodyTexts.Contains(photo.Name))
            {
                Check(body.Element(Ns + "spPr")!.Element(A + "blipFill") != null, "photo should be body picture fill");
                Check(!group.Descendants(Ns + "pic").Any(), "photo detached from body");
                Check(body.Descendants(A + "highlight").All(h => h.Element(A + "srgbClr")?.Attribute("val")?.Value == "FFFFFF") &&
                    body.Descendants(A + "highlight").Any(), "white glyph background is not text highlight");
                Check(bodyTexts.Any(t => t is "(A형)" or "(B형)"), "instance purpose missing from body");
                var ports = group.Elements(Ns + "grpSp").ToArray();
                Check(ports.Length == 3, "device ports must be individually grouped inside their device");
                foreach (var port in ports)
                {
                    var parts = port.Elements(Ns + "sp").ToArray();
                    Check(parts.Count(s => s.Element(Ns + "txBody") == null) == 1, "port marker lost/duplicated");
                    Check(parts.Any(s => s.Descendants(A + "cxn").Any()), "port attachment site lost by grouping");
                }
                Check(!group.Descendants(A + "t").Any(t => t.Value == "숨긴포트"), "hidden port name restored unexpectedly");
                Check(group.Descendants(A + "t").Any(t => t.Value == "D38999"), "connector specification missing from port label");
            }
            else
                Check(bodyTexts.Single() == longName.Name && body.Descendants(A + "bodyPr").Single().Attribute("horzOverflow")!.Value == "overflow", "long device name clipped");
        }
        Check(groups.Count(g => g.Descendants(A + "t").Any(t => t.Value == "(A형)")) == 1 &&
            groups.Count(g => g.Descendants(A + "t").Any(t => t.Value == "(B형)")) == 1, "shared device template mixed instance purposes");
        Console.WriteLine("PASS: native device body text/photo fill/white text highlight; exact name placement; instance purposes; long names; nested device-port groups; hidden labels");
    }
}
