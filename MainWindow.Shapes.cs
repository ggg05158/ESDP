using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ESDP.Models;
using Point = System.Windows.Point;

namespace ESDP;

public partial class MainWindow
{
    private PlacedShape? _adjustingShape;
    private PlacedShape? _editingShapeVertices;
    private int _shapeAdjustmentIndex;
    private int _shapeVertexDragIndex = -1;
    private CanvasShape? _copiedShapeFormat;
    private CanvasShape? _shapeAdjustmentStart;

    private void PopulateShapeGallery()
    {
        ShapeGalleryContent.Children.Clear();
        foreach (var category in ShapeCatalog.All.GroupBy(d => d.Category))
        {
            ShapeGalleryContent.Children.Add(new TextBlock { Text = category.Key,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 10, 0, 5) });
            var panel = new WrapPanel();
            foreach (var definition in category)
            {
                var icon = new Canvas { Width = 42, Height = 28, IsHitTestVisible = false };
                var stroke = AppThemeManager.GetBrush("AppTextBrush");
                if (ShapeCatalog.IsLine(definition.Kind))
                {
                    var points = definition.Kind == "ElbowLine"
                        ? new[] { new Point(2, 24), new Point(20, 24), new Point(20, 4), new Point(40, 4) }
                        : new[] { new Point(2, 23), new Point(40, 5) };
                    icon.Children.Add(new Polyline { Points = new PointCollection(points), Stroke = stroke, StrokeThickness = 1.6 });
                    if (definition.Kind is "ArrowLine" or "DoubleArrowLine")
                        icon.Children.Add(CreateLineArrowHead(2, 23, 40, 5, 8, 1, stroke));
                    if (definition.Kind == "DoubleArrowLine")
                        icon.Children.Add(CreateLineArrowHead(40, 5, 2, 23, 8, 1, stroke));
                }
                else
                {
                    var shape = ShapeCatalog.Outline(ShapeCatalog.Create(definition.Kind, 38, 24),
                        AppThemeManager.WithAlpha("AppAccentBrush", 30), stroke, 1.4);
                    Canvas.SetLeft(shape, 2); Canvas.SetTop(shape, 2); icon.Children.Add(shape);
                    if (definition.Kind is "Text" or "Image")
                        icon.Children.Add(new TextBlock { Text = definition.Kind == "Text" ? "Ab" : "사진", FontSize = 11,
                            Foreground = stroke, Margin = new Thickness(8, 5, 0, 0) });
                }
                var button = new Button { Content = icon, Tag = definition.Kind, Width = 58, Height = 44,
                    Margin = new Thickness(2), Padding = new Thickness(4), ToolTip = definition.Name,
                    Style = AppThemeManager.GetStyle("TileButtonStyle") };
                if (definition.Kind == "Image") button.Click += Menu_AddImage;
                else button.Click += ShapeGalleryItem_Click;
                panel.Children.Add(button);
            }
            ShapeGalleryContent.Children.Add(panel);
        }
    }

    internal static void AddShapeTextViewer(Canvas canvas, CanvasShape s)
    {
        var area = ShapeCatalog.TextArea(s);
        var host = RichTextSupport.CreateViewer(s.TextRtfBase64, s.Text, s.FontFamily, s.FontSize,
            s.TextColorHex, s.TextBold, s.TextItalic, s.TextUnderline, s.TextAlignment, 0);
        host.Width = area.Width; host.Height = area.Height;
        if (host is Grid grid && grid.Children.OfType<RichTextBox>().FirstOrDefault() is { } viewer)
        {
            viewer.VerticalAlignment = s.TextVerticalAlignment switch
            { "Top" => VerticalAlignment.Top, "Bottom" => VerticalAlignment.Bottom, _ => VerticalAlignment.Center };
            if (s.TextFit == "Shrink")
            {
                double scale = 1, minimum = Math.Min(1, 6 / Math.Max(6, s.FontSize));
                for (int i = 0; i < 8; i++)
                {
                    viewer.Width = area.Width / scale;
                    viewer.LayoutTransform = new ScaleTransform(scale, scale);
                    viewer.Measure(new Size(area.Width, double.PositiveInfinity));
                    if (viewer.DesiredSize.Height <= area.Height + .5 || scale <= minimum) break;
                    scale = Math.Max(minimum, scale * Math.Min(.9, area.Height / Math.Max(1, viewer.DesiredSize.Height)));
                }
            }
        }
        Canvas.SetLeft(host, area.X); Canvas.SetTop(host, area.Y); canvas.Children.Add(host);
    }

    private void AddShapeAdjustmentHandles(PlacedShape placed)
    {
        var s = placed.Shape;
        if (_editingShapeVertices == placed && s.CustomVertices.Count >= 3)
        {
            for (int i = 0; i < s.CustomVertices.Count; i++)
            {
                int vertex = i;
                var p = s.CustomVertices[i];
                var position = new Point((s.FlipHorizontal ? 1 - p.X : p.X) * s.Width,
                    (s.FlipVertical ? 1 - p.Y : p.Y) * s.Height);
                AddEditingHandle(placed, position, Brushes.Black, "꼭짓점 이동 / Ctrl+클릭: 삭제", () =>
                {
                    if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && s.CustomVertices.Count > 3)
                    { s.CustomVertices.RemoveAt(vertex); ShapeEditCompleted(); return; }
                    StartShapeAdjustment(placed, 0, vertex);
                });
                var next = s.CustomVertices[(i + 1) % s.CustomVertices.Count];
                var middle = new Point((p.X + next.X) / 2, (p.Y + next.Y) / 2);
                var shown = new Point((s.FlipHorizontal ? 1 - middle.X : middle.X) * s.Width,
                    (s.FlipVertical ? 1 - middle.Y : middle.Y) * s.Height);
                AddEditingHandle(placed, shown, Brushes.White, "클릭: 꼭짓점 추가", () =>
                {
                    s.CustomVertices.Insert(vertex + 1, new Models.Point { X = middle.X, Y = middle.Y });
                    ShapeEditCompleted();
                }, 7);
            }
            return;
        }
        if (!ShapeCatalog.Adjustable(s)) return;
        int count = ShapeCatalog.IsArrow(s.Kind) ? 2 : 1;
        for (int i = 0; i < count; i++)
        {
            int index = i;
            AddEditingHandle(placed, ShapeCatalog.AdjustmentHandle(s, i), Brushes.Gold,
                i == 1 ? "화살표 몸통 두께 조절" : "도형 모양 조절", () =>
                {
                    StartShapeAdjustment(placed, index);
                });
        }
    }
    private static void AddEditingHandle(PlacedShape placed, Point p, Brush fill, string tip, Action begin, double size = 10)
    {
        var handle = new Rectangle { Width = size, Height = size, Fill = fill, Stroke = Brushes.DimGray,
            StrokeThickness = 1, Cursor = Cursors.Hand, ToolTip = tip };
        if (fill == Brushes.Gold) handle.RenderTransform = new RotateTransform(45, size / 2, size / 2);
        Canvas.SetLeft(handle, p.X - size / 2); Canvas.SetTop(handle, p.Y - size / 2);
        handle.MouseLeftButtonDown += (_, e) => { begin(); e.Handled = true; };
        placed.Container.Children.Add(handle);
    }
    private void MoveShapeAdjustment(Point mouse)
    {
        if (_adjustingShape is not { } placed) return;
        var s = placed.Shape;
        Point local = ShapeGeometry.ToUnrotated(s, mouse) - new Vector(s.X, s.Y);
        if (_shapeVertexDragIndex >= 0 && _shapeVertexDragIndex < s.CustomVertices.Count)
        {
            double x = Math.Clamp(local.X / Math.Max(1, s.Width), 0, 1), y = Math.Clamp(local.Y / Math.Max(1, s.Height), 0, 1);
            var vertex = s.CustomVertices[_shapeVertexDragIndex];
            vertex.X = s.FlipHorizontal ? 1 - x : x; vertex.Y = s.FlipVertical ? 1 - y : y;
        }
        else ShapeCatalog.Adjust(s, _shapeAdjustmentIndex, local);
        RenderShape(placed);
    }
    private void StartShapeAdjustment(PlacedShape placed, int index, int vertex = -1)
    {
        CommitActiveTextEdit(); CommitHistorySnapshot();
        _shapeAdjustmentStart = ShapeCatalog.Clone(placed.Shape); _shapeVertexDragIndex = vertex;
        _shapeAdjustmentIndex = index; _adjustingShape = placed; placed.Container.CaptureMouse();
    }
    private void CancelShapeAdjustment()
    {
        if (_adjustingShape is not { } placed) return;
        if (_shapeAdjustmentStart is { } saved) ShapeCatalog.CopyTo(saved, placed.Shape);
        _adjustingShape = null; _shapeAdjustmentStart = null; placed.Container.ReleaseMouseCapture(); RenderShape(placed);
    }
    private void FinishShapeAdjustmentIfCaptureLost(PlacedShape placed)
    {
        if (_adjustingShape != placed) return;
        _adjustingShape = null; _shapeAdjustmentStart = null;
        RenderShape(placed); UpdateCanvasExtent(); _isDirty = true;
        CommitHistoryIfActionComplete();
    }
    private void ShapeEditingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedShape is not { } placed) return;
        var menu = new ContextMenu();
        menu.Items.Add(MakeMenuItem("도형 세부 설정...", () => ShowShapeProperties(placed)));
        AddShapeEditingMenu(menu, placed);
        menu.PlacementTarget = ShapeEditingButton; menu.IsOpen = true;
    }
    private void AddShapeEditingMenu(ContextMenu menu, PlacedShape placed)
    {
        var change = new MenuItem { Header = "도형 변경", IsEnabled = !ShapeCatalog.IsLine(placed.Shape.Kind) && placed.Shape.Kind != "Image" };
        foreach (var category in ShapeCatalog.All.Where(d => !ShapeCatalog.IsLine(d.Kind) && d.Kind != "Image").GroupBy(d => d.Category))
        {
            var sub = new MenuItem { Header = category.Key };
            foreach (var definition in category)
                sub.Items.Add(MakeMenuItem(definition.Name, () =>
                {
                    foreach (var item in _selectedShapes)
                    {
                        item.Shape.Kind = definition.Kind; item.Shape.CustomVertices.Clear();
                        item.Shape.Adjustment1 = null; item.Shape.Adjustment2 = null; item.Shape.UseShapeTextArea = true;
                    }
                    _editingShapeVertices = null; ShapeEditCompleted();
                }));
            change.Items.Add(sub);
        }
        menu.Items.Add(change);
        var rotate = new MenuItem { Header = "회전 및 반전" };
        rotate.Items.Add(MakeMenuItem("오른쪽으로 90° 회전", () => RotateShapeSelection(90)));
        rotate.Items.Add(MakeMenuItem("왼쪽으로 90° 회전", () => RotateShapeSelection(-90)));
        rotate.Items.Add(MakeMenuItem("좌우 반전", () => FlipShapeSelection(true)));
        rotate.Items.Add(MakeMenuItem("상하 반전", () => FlipShapeSelection(false)));
        menu.Items.Add(rotate);
        var edit = MakeMenuItem(_editingShapeVertices == placed ? "점 편집 종료" : "점 편집", () =>
        {
            if (_editingShapeVertices == placed) _editingShapeVertices = null;
            else if (placed.Shape.CustomVertices.Count >= 3) _editingShapeVertices = placed;
            else if (ShapeCatalog.Outline(placed.Shape, null, Brushes.Black) is Polygon polygon)
            {
                placed.Shape.CustomVertices = polygon.Points.Select(p => new Models.Point { X = p.X / placed.Shape.Width, Y = p.Y / placed.Shape.Height }).ToList();
                _editingShapeVertices = placed;
            }
            ShapeEditCompleted();
        });
        edit.IsEnabled = placed.Shape.CustomVertices.Count >= 3 || ShapeCatalog.Outline(placed.Shape, null, Brushes.Black) is Polygon;
        menu.Items.Add(edit);
        menu.Items.Add(new Separator());
        var group = MakeMenuItem("그룹으로 묶기", () =>
        {
            string id = Guid.NewGuid().ToString(); foreach (var item in _selectedShapes) item.Shape.GroupId = id;
            _editingShapeVertices = null; ShapeEditCompleted();
        });
        group.IsEnabled = _selectedShapes.Count >= 2 && _selectedSet.Count == 0 && _selectedCablePorts.Count == 0;
        menu.Items.Add(group);
        var ungroup = MakeMenuItem("그룹 해제", () =>
        {
            foreach (var item in _selectedShapes) item.Shape.GroupId = "";
            ShapeEditCompleted();
        });
        ungroup.IsEnabled = _selectedShapes.Any(p => p.Shape.GroupId.Length > 0); menu.Items.Add(ungroup);
        var scale = MakeMenuItem("선택 도형 크기 배율...", ScaleShapeSelection); menu.Items.Add(scale);
        menu.Items.Add(new Separator());
        var styles = new MenuItem { Header = "빠른 서식" };
        foreach (string style in new[] { "기본", "강조", "윤곽선만", "설명" })
            styles.Items.Add(MakeMenuItem(style, () =>
            {
                foreach (var item in _selectedShapes)
                {
                    item.Shape.FillHex = style == "윤곽선만" ? "#00FFFFFF" : style == "강조" ? "#FFF2D5" : style == "설명" ? "#FFF8E8" : "#FFFFFF";
                    item.Shape.StrokeHex = style == "강조" ? "#A95515" : "#333333";
                    item.Shape.TextColorHex = "#222222"; item.Shape.TextRtfBase64 = "";
                    item.Shape.StrokeThickness = style == "강조" ? 2.5 : 1.5; item.Shape.LineStyle = "Solid";
                }
                ShapeEditCompleted();
            }));
        menu.Items.Add(styles);
        menu.Items.Add(MakeMenuItem("서식 복사", () => _copiedShapeFormat = ShapeCatalog.Clone(placed.Shape)));
        var paste = MakeMenuItem("서식 붙여넣기", () =>
        {
            if (_copiedShapeFormat is not { } source) return;
            foreach (var item in _selectedShapes)
            {
                var s = item.Shape;
                s.FillHex = source.FillHex; s.StrokeHex = source.StrokeHex; s.StrokeThickness = source.StrokeThickness;
                s.LineStyle = source.LineStyle; s.StartLineCap = source.StartLineCap; s.EndLineCap = source.EndLineCap;
                s.ArrowHeadSize = source.ArrowHeadSize; s.FontFamily = source.FontFamily; s.FontSize = source.FontSize;
                s.TextColorHex = source.TextColorHex; s.TextBold = source.TextBold; s.TextItalic = source.TextItalic;
                s.TextUnderline = source.TextUnderline; s.TextAlignment = source.TextAlignment; s.TextRtfBase64 = "";
                s.TextVerticalAlignment = source.TextVerticalAlignment; s.TextMargin = source.TextMargin;
                s.TextFit = source.TextFit;
            }
            ShapeEditCompleted();
        });
        paste.IsEnabled = _copiedShapeFormat != null; menu.Items.Add(paste);
    }
    private void ShapeEditCompleted()
    {
        foreach (var item in _selectedShapes) RenderShape(item);
        UpdateTextFormatPalette(); UpdateCanvasExtent(); _isDirty = true;
    }
    private IEnumerable<PlacedShape> ShapeGroup(PlacedShape placed) => string.IsNullOrEmpty(placed.Shape.GroupId)
        ? new[] { placed } : _placedShapes.Where(p => p.Shape.GroupId == placed.Shape.GroupId);
    private Rect ShapeSelectionBounds()
    {
        var bounds = Rect.Empty; foreach (var item in _selectedShapes) bounds.Union(ShapeGeometry.Bounds(item.Shape));
        return bounds;
    }
    private void RotateShapeSelection(double angle)
    {
        var bounds = ShapeSelectionBounds(); if (bounds.IsEmpty) return;
        Point pivot = new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        foreach (var item in _selectedShapes)
        {
            var s = item.Shape; var center = ShapeGeometry.Center(s);
            var moved = pivot + ShapeGeometry.Rotate(center - pivot, angle);
            s.X += moved.X - center.X; s.Y += moved.Y - center.Y;
            s.RotationAngle = ShapeGeometry.NormalizeAngle(s.RotationAngle + angle);
        }
        ShapeEditCompleted();
    }
    private void FlipShapeSelection(bool horizontal)
    {
        var bounds = ShapeSelectionBounds(); if (bounds.IsEmpty) return;
        foreach (var item in _selectedShapes)
        {
            var s = item.Shape; var center = ShapeGeometry.Center(s);
            if (horizontal) { s.X += bounds.Left + bounds.Right - 2 * center.X; s.FlipHorizontal = !s.FlipHorizontal; }
            else { s.Y += bounds.Top + bounds.Bottom - 2 * center.Y; s.FlipVertical = !s.FlipVertical; }
            s.RotationAngle = ShapeGeometry.NormalizeAngle(-s.RotationAngle);
        }
        ShapeEditCompleted();
    }
    private void ScaleShapeSelection()
    {
        var dialog = new TextEditWindow("100") { Owner = this, Title = "선택 도형 크기 배율 (%)" };
        if (dialog.ShowDialog() != true) return;
        if (!double.TryParse(dialog.ResultText.Trim(), out var percent) || !double.IsFinite(percent) || percent < 1 || percent > 1000)
        { MessageBox.Show(this, "배율은 1~1000%로 입력해 주세요.", "도형 크기", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        ScaleSelectedShapes(percent / 100);
    }
    private void ScaleSelectedShapes(double scale)
    {
        var bounds = ShapeSelectionBounds(); if (bounds.IsEmpty) return;
        Point pivot = new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        foreach (var item in _selectedShapes)
        {
            var s = item.Shape; var center = ShapeGeometry.Center(s); var desired = pivot + (center - pivot) * scale;
            s.Width *= scale; s.Height *= scale;
            foreach (var p in s.LinePoints) { p.X *= scale; p.Y *= scale; }
            var now = ShapeGeometry.Center(s); s.X += desired.X - now.X; s.Y += desired.Y - now.Y;
        }
        ShapeEditCompleted();
    }
    private void AddShapeCopies(IEnumerable<CanvasShape> sources)
    {
        var copies = new List<PlacedShape>(); var groupIds = new Dictionary<string, string>();
        foreach (var source in sources.ToArray())
        {
            var copy = ShapeCatalog.Clone(source); copy.Id = Guid.NewGuid().ToString(); copy.X += 20; copy.Y += 20;
            if (!string.IsNullOrEmpty(copy.GroupId))
            {
                if (!groupIds.TryGetValue(copy.GroupId, out var id)) groupIds[copy.GroupId] = id = Guid.NewGuid().ToString();
                copy.GroupId = id;
            }
            copy.ZOrder = GetNextShapeZOrder(); var placed = CreatePlacedShape(copy);
            _placedShapes.Add(placed); MainCanvas.Children.Add(placed.Container); ApplyShapeZOrder(placed); copies.Add(placed);
        }
        SelectShape(null); SelectAddShapes(copies); ShapeEditCompleted();
    }
}
