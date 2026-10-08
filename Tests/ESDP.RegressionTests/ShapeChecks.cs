using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ESDP;
using ESDP.Models;
using P = System.Windows.Point;
using Path = System.IO.Path;

internal static class ShapeChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type Catalog = typeof(MainWindow).Assembly.GetType("ESDP.ShapeCatalog")!;
    private static object? C(string name, params object?[] args) => Catalog.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args);
    private static object? W(MainWindow w, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Flags)!.Invoke(w, args);
    private static T F<T>(object w, string name) => (T)w.GetType().GetField(name, Flags)!.GetValue(w)!;
    private static void Check(bool pass, string message) { if (!pass) throw new Exception("Shape regression: " + message); }
    private static Shape Outline(CanvasShape s) => (Shape)C("Outline", s, Brushes.White, Brushes.Black, null)!;
    private static CanvasShape Create(string kind, double w = 120, double h = 80) => (CanvasShape)C("Create", kind, w, h)!;
    private static void Render(FrameworkElement content, string path, double width, double height)
    {
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen()) dc.DrawRectangle((Brush)Application.Current.FindResource("AppPanelBrush"), null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    public static void Run(MainWindow window, bool visualQa)
    {
        var definitions = ((IEnumerable)Catalog.GetField("All")!.GetValue(null)!).Cast<object>().ToList();
        Check(definitions.Count == 32, "gallery item count");
        var kinds = definitions.Select(d => (string)d.GetType().GetProperty("Kind")!.GetValue(d)!).ToList();
        Check(kinds.Distinct().Count() == kinds.Count, "duplicate shape kind");
        foreach (string kind in kinds)
        foreach (var size in new[] { (12d, 10d), (120d, 80d), (200d, 15d), (15d, 200d) })
        {
            var s = Create(kind, size.Item1, size.Item2);
            var outline = Outline(s);
            var area = (Rect)C("TextArea", s)!;
            Check(!area.IsEmpty && area.Width > 0 && area.Height > 0, kind + " invalid text area");
            Check(area.Left >= -.001 && area.Top >= -.001 && area.Right <= s.Width + .001 && area.Bottom <= s.Height + .001, kind + " text area outside geometry");
            if (outline is Polygon polygon)
                Check(polygon.Points.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)), kind + " invalid polygon");
            if (!(bool)C("IsLine", kind)! && kind != "Image")
            {
                var preview = (Shape)typeof(MainWindow).GetMethod("CreateShapeDrawPreviewElement", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object[] { kind, size.Item1, size.Item2 })!;
                Check(preview.GetType() == outline.GetType(), kind + " preview/canvas geometry type mismatch");
                if (outline is Polygon canvasPolygon) Check(canvasPolygon.Points.SequenceEqual(((Polygon)preview).Points), kind + " preview points mismatch");
            }
        }
        var legacy = new CanvasShape { Kind = "RoundedRectangle", Width = 100, Height = 80 };
        Check(Math.Abs(((Rectangle)Outline(legacy)).RadiusX - 14) < .001, "legacy corner radius changed");
        Check((Rect)C("TextArea", legacy)! == new Rect(4, 0, 92, 80), "legacy label layout changed");
        var rounded = Create("RoundedRectangle", 100, 80);
        C("Adjust", rounded, 0, new P(40, 0)); Check(((Rectangle)Outline(rounded)).RadiusX == 40, "roundness handle");
        var arrow = Create("RightArrow"); var oldArrow = ((Polygon)Outline(arrow)).Points.ToArray();
        C("Adjust", arrow, 1, new P(60, 8));
        Check(!oldArrow.SequenceEqual(((Polygon)Outline(arrow)).Points), "arrow thickness handle");
        arrow.FlipHorizontal = true;
        C("Adjust", arrow, 0, new P(30, 0));
        Check(Math.Abs(arrow.Adjustment1!.Value - .25) < .001, "flipped handle mapping");
        var dialogShape = Create("Callout");
        dialogShape.GroupId = "kept-group"; dialogShape.Text = "설명"; dialogShape.TextRtfBase64 = "";
        string dialogBefore = JsonSerializer.Serialize(dialogShape);
        var dialog = new ShapePropertiesWindow(dialogShape);
        var built = (CanvasShape)typeof(ShapePropertiesWindow).GetMethod("Build", Flags)!.Invoke(dialog, new object[] { false })!;
        Check(built != null && built.Kind == dialogShape.Kind && built.GroupId == dialogShape.GroupId, "property roundtrip");
        Check(dialogBefore == JsonSerializer.Serialize(dialogShape), "property window changed model before Apply");
        var boxes = F<Dictionary<string, TextBox>>(dialog, "_numbers");
        boxes["W"].Text = "NaN";
        Check(typeof(ShapePropertiesWindow).GetMethod("Build", Flags)!.Invoke(dialog, new object[] { false }) == null, "invalid size accepted");
        boxes["W"].Text = "200"; boxes["Angle"].Text = "450"; boxes["Margin"].Text = "10";
        built = (CanvasShape)typeof(ShapePropertiesWindow).GetMethod("Build", Flags)!.Invoke(dialog, new object[] { false })!;
        Check(built.Width == 200 && built.RotationAngle == 90 && built.TextMargin == 10, "numeric size/rotation/margin");
        var list = F<List<PlacedShape>>(window, "_placedShapes");
        var canvas = F<Canvas>(window, "MainCanvas");
        string cablesBefore = JsonSerializer.Serialize(F<List<PlacedCable>>(window, "_placedCables").Select(p => p.Instance));
        var first = Create("Triangle"); first.X = 300; first.Y = 300; first.GroupId = "original-group";
        var second = Create("Callout"); second.X = 460; second.Y = 300; second.GroupId = first.GroupId;
        first.ZOrder = 0; second.ZOrder = 1;
        foreach (var s in new[] { first, second })
        {
            var placed = (PlacedShape)W(window, "CreatePlacedShape", s)!;
            list.Add(placed); canvas.Children.Add(placed.Container); W(window, "RenderShape", placed);
        }
        W(window, "SelectShape", list[0]);
        Check(F<HashSet<PlacedShape>>(window, "_selectedShapes").Count == 2, "group selection not expanded");
        W(window, "RotateShapeSelection", 90d);
        Check(first.RotationAngle == 90 && second.RotationAngle == 90, "group rotation");
        W(window, "FlipShapeSelection", true);
        Check(first.FlipHorizontal && second.FlipHorizontal, "group flip");
        W(window, "ScaleSelectedShapes", 1.25);
        Check(first.Width == 150 && second.Height == 100, "group scale");
        W(window, "Menu_Copy", window, new RoutedEventArgs());
        var clipboard = F<object>(window, "_clipboard");
        Check(clipboard is List<CanvasShape> { Count: 2 }, "group clipboard");
        W(window, "Menu_Paste", window, new RoutedEventArgs());
        Check(list.Count == 4, "group paste");
        Check(list[2].Shape.GroupId == list[3].Shape.GroupId && list[2].Shape.GroupId != first.GroupId, "pasted group IDs");
        Check(list[2].Shape.FlipHorizontal && list[2].Shape.Width == first.Width, "pasted format missing");
        W(window, "DuplicateShape", list[2]);
        Check(list.Count == 6 && list[4].Shape.GroupId == list[5].Shape.GroupId && list[4].Shape.GroupId != list[2].Shape.GroupId, "duplicate group");
        first.CustomVertices = new() { new() { X = .2, Y = 0 }, new() { X = 1, Y = 1 }, new() { X = 0, Y = 1 } };
        var cloned = (CanvasShape)C("Clone", first)!; cloned.CustomVertices[0].X = .4;
        Check(first.CustomVertices[0].X == .2, "custom vertex shallow clone");
        string dragBefore = JsonSerializer.Serialize(first), otherBefore = JsonSerializer.Serialize(second);
        W(window, "StartShapeAdjustment", list[0], 0, 0);
        W(window, "MoveShapeAdjustment", new P(first.X + first.Width / 2, first.Y + first.Height / 2));
        Check(first.CustomVertices[0].X != .2, "vertex did not move");
        Check(otherBefore == JsonSerializer.Serialize(second), "vertex editing moved another shape");
        W(window, "CancelShapeAdjustment");
        Check(dragBefore == JsonSerializer.Serialize(first) && F<PlacedShape?>(window, "_adjustingShape") == null, "cancel did not restore geometry");
        var shrink = Create("Rectangle", 100, 40); shrink.FontSize = 28; shrink.TextFit = "Shrink";
        shrink.Text = "긴 글자가 영역을 넘어가는 경우 글자 크기만 축소합니다.";
        var shrinkCanvas = new Canvas();
        typeof(MainWindow).GetMethod("AddShapeTextViewer", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { shrinkCanvas, shrink });
        var shrinkViewer = ((Grid)shrinkCanvas.Children[0]).Children.OfType<RichTextBox>().Single();
        Check(shrinkViewer.LayoutTransform is ScaleTransform { ScaleX: < 1 }, "text overflow was not shrunk");
        Check(shrink.FontSize == 28, "text fitting rewrote authored size");
        var project = new Project { Sheets = new() { new Sheet { Shapes = list.Select(p => p.Shape).ToList() } } };
        string qaPath = Path.GetFullPath(".verification/shape-editor/qa"); Directory.CreateDirectory(qaPath);
        string save = Path.Combine(qaPath, "shape-regression.esdp");
        ProjectFileService.SaveAtomic(save, project);
        var loaded = ProjectFileService.LoadPrepared(save).Project.Sheets[0].Shapes;
        Check(loaded[0].CustomVertices[0].X == .2 && loaded[0].FlipHorizontal && loaded[0].GroupId == first.GroupId, "project lost editing properties");
        Check(cablesBefore == JsonSerializer.Serialize(F<List<PlacedCable>>(window, "_placedCables").Select(p => p.Instance)), "shape tools changed cables");
        if (visualQa)
        {
            W(window, "PopulateShapeGallery");
            var gallery = F<StackPanel>(window, "ShapeGalleryContent");
            Render(gallery, Path.Combine(qaPath, "gallery.png"), 380, 780);
            var props = new ShapePropertiesWindow(Create("RightArrow"));
            Render((FrameworkElement)props.Content, Path.Combine(qaPath, "properties.png"), 824, 624);
            F<TabControl>(props, "OptionsTabs").SelectedIndex = 1;
            Render((FrameworkElement)props.Content, Path.Combine(qaPath, "properties-text.png"), 744, 524);
            var samples = new Canvas { Width = 900, Height = 850, Background = Brushes.White };
            int index = 0;
            foreach (var kind in kinds.Where(k => !(bool)C("IsLine", k)! && k is not "Image" and not "Text"))
            {
                var s = Create(kind, 130, 90); s.Text = (string)C("Name", kind)!;
                s.X = 30 + index % 5 * 175; s.Y = 30 + index / 5 * 140;
                var placed = (PlacedShape)W(window, "CreatePlacedShape", s)!;
                W(window, "RenderShape", placed); samples.Children.Add(placed.Container); index++;
            }
            Render(samples, Path.Combine(qaPath, "canvas-shapes.png"), 900, 850);
        }
        dialog.Close();
        W(window, "SelectShape", list[0]);
        W(window, "ResetUndoHistory");
        string beforeUndo = JsonSerializer.Serialize(list.Select(p => p.Shape).ToList());
        W(window, "RotateShapeSelection", 90d); W(window, "CommitHistorySnapshot");
        string afterRotate = JsonSerializer.Serialize(list.Select(p => p.Shape).ToList());
        W(window, "Menu_Undo", window, new RoutedEventArgs());
        Check(beforeUndo == JsonSerializer.Serialize(list.Select(p => p.Shape).ToList()), "Undo changed group geometry/vertices");
        W(window, "Menu_Redo", window, new RoutedEventArgs());
        Check(afterRotate == JsonSerializer.Serialize(list.Select(p => p.Shape).ToList()), "Redo lost group transform");
        W(window, "SelectShape", new object?[] { null });
        foreach (var placed in list.ToArray()) canvas.Children.Remove(placed.Container);
        list.Clear();
        Console.WriteLine("PASS: 32 shape entries, shared gallery/preview geometry at 4 sizes; legacy layout; adjustable/flipped handles; property validation; group transforms/copy; saved vertices; cables unchanged");
    }
}
