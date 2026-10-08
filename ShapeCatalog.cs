using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using ESDP.Models;
using Point = System.Windows.Point;
using Path = System.Windows.Shapes.Path;

namespace ESDP;

// One definition is shared by the gallery, insertion preview and canvas renderer.
internal static class ShapeCatalog
{
    internal sealed record Definition(string Kind, string Name, string Category);
    public static readonly Definition[] All =
    {
        new("Rectangle", "사각형", "기본 도형"), new("RoundedRectangle", "둥근 사각형", "기본 도형"),
        new("Ellipse", "타원", "기본 도형"), new("Triangle", "삼각형", "기본 도형"),
        new("RightTriangle", "직각 삼각형", "기본 도형"), new("Diamond", "마름모", "기본 도형"),
        new("Parallelogram", "평행사변형", "기본 도형"), new("Trapezoid", "사다리꼴", "기본 도형"),
        new("Pentagon", "오각형", "기본 도형"), new("Hexagon", "육각형", "기본 도형"),
        new("Octagon", "팔각형", "기본 도형"), new("Star", "별", "기본 도형"),
        new("Heart", "하트", "기본 도형"), new("Cylinder", "원통 / 데이터", "순서도"),
        new("Document", "문서", "순서도"), new("Terminator", "시작 / 종료", "순서도"),
        new("RightArrow", "오른쪽 화살표", "블록 화살표"), new("LeftArrow", "왼쪽 화살표", "블록 화살표"),
        new("UpArrow", "위쪽 화살표", "블록 화살표"), new("DownArrow", "아래쪽 화살표", "블록 화살표"),
        new("LeftRightArrow", "좌우 화살표", "블록 화살표"), new("Chevron", "갈매기형 화살표", "블록 화살표"),
        new("Callout", "사각 설명선", "설명선 및 괄호"), new("LeftBracket", "왼쪽 대괄호", "설명선 및 괄호"),
        new("RightBracket", "오른쪽 대괄호", "설명선 및 괄호"), new("BracePair", "중괄호 쌍", "설명선 및 괄호"),
        new("Line", "직선", "선"), new("ElbowLine", "꺾인 선", "선"),
        new("ArrowLine", "화살표 선", "선"), new("DoubleArrowLine", "양방향 화살표 선", "선"),
        new("Text", "텍스트 상자", "텍스트 및 이미지"), new("Image", "이미지", "텍스트 및 이미지")
    };
    public static string Name(string kind) => All.FirstOrDefault(d => d.Kind == kind)?.Name ?? kind;
    public static bool IsLine(string kind) => kind is "Line" or "ElbowLine" or "ArrowLine" or "DoubleArrowLine";
    public static bool IsArrow(string kind) => kind is "RightArrow" or "LeftArrow" or "UpArrow" or "DownArrow" or "LeftRightArrow";
    public static CanvasShape Create(string kind, double width = 120, double height = 80) => new()
    {
        Kind = kind, Width = width, Height = height, UseShapeTextArea = true,
        Adjustment1 = kind == "RoundedRectangle" ? .18 : null,
        Text = kind == "Text" ? "텍스트" : ""
    };
    private static double Value(double? value, double fallback, double min, double max)
        => Math.Clamp(value is { } v && double.IsFinite(v) ? v : fallback, min, max);
    public static double First(CanvasShape s) => s.Kind switch
    {
        "RoundedRectangle" => Value(s.Adjustment1, Math.Min(14, Math.Min(s.Width, s.Height) / 4) / Math.Max(1, Math.Min(s.Width, s.Height)), 0, .5),
        "Callout" => Value(s.Adjustment1, .22, .05, .9),
        "Star" => Value(s.Adjustment1, .45, .15, .8),
        "Parallelogram" or "Trapezoid" or "Chevron" => Value(s.Adjustment1, .2, .05, .45),
        _ when IsArrow(s.Kind) => Value(s.Adjustment1, s.ArrowHeadSize / Math.Max(1, s.Kind is "UpArrow" or "DownArrow" ? s.Height : s.Width), .08, s.Kind == "LeftRightArrow" ? .45 : .65),
        _ => .2
    };
    public static double Second(CanvasShape s) => Value(s.Adjustment2, s.Kind == "Callout" ? .25 : .27, .05, .45);
    public static Shape Outline(CanvasShape s, Brush? fill, Brush stroke, double? thickness = null)
    {
        double w = Math.Max(1, Math.Abs(s.Width)), h = Math.Max(1, Math.Abs(s.Height));
        double a = First(s), b = Second(s);
        Shape shape;
        if (s.CustomVertices.Count >= 3)
            shape = Polygon(s.CustomVertices.Select(p => new Point(p.X * w, p.Y * h)).ToArray());
        else switch (s.Kind)
        {
            case "Ellipse": shape = new Ellipse { Width = w, Height = h }; break;
            case "RoundedRectangle":
            case "Terminator":
                double radius = s.Kind == "Terminator" ? Math.Min(w, h) / 2 : Math.Min(w, h) * a;
                shape = new Rectangle { Width = w, Height = h, RadiusX = radius, RadiusY = radius }; break;
            case "Triangle": shape = Polygon(new(w / 2, 0), new(w, h), new(0, h)); break;
            case "RightTriangle": shape = Polygon(new(0, 0), new(w, h), new(0, h)); break;
            case "Diamond": shape = Polygon(new(w / 2, 0), new(w, h / 2), new(w / 2, h), new(0, h / 2)); break;
            case "Parallelogram": shape = Polygon(new(w * a, 0), new(w, 0), new(w * (1 - a), h), new(0, h)); break;
            case "Trapezoid": shape = Polygon(new(w * a, 0), new(w * (1 - a), 0), new(w, h), new(0, h)); break;
            case "Pentagon": shape = Regular(5, w, h); break;
            case "Hexagon": shape = Polygon(new(w * .25, 0), new(w * .75, 0), new(w, h / 2), new(w * .75, h), new(w * .25, h), new(0, h / 2)); break;
            case "Octagon": shape = Polygon(new(w * .3, 0), new(w * .7, 0), new(w, h * .3), new(w, h * .7), new(w * .7, h), new(w * .3, h), new(0, h * .7), new(0, h * .3)); break;
            case "Star":
                shape = Polygon(Enumerable.Range(0, 10).Select(i =>
                {
                    double r = i % 2 == 0 ? 1 : a, t = -Math.PI / 2 + i * Math.PI / 5;
                    return new Point(w * (.5 + .5 * r * Math.Cos(t)), h * (.5 + .5 * r * Math.Sin(t)));
                }).ToArray()); break;
            case "RightArrow": case "LeftArrow": case "UpArrow": case "DownArrow":
                bool vertical = s.Kind is "UpArrow" or "DownArrow";
                double length = vertical ? h : w, cross = vertical ? w : h, head = a * length, inset = b * cross;
                var arrow = new[] { new Point(0, inset), new Point(length - head, inset), new Point(length - head, 0), new Point(length, cross / 2), new Point(length - head, cross), new Point(length - head, cross - inset), new Point(0, cross - inset) };
                shape = Polygon(arrow.Select(p => s.Kind switch
                {
                    "LeftArrow" => new Point(w - p.X, p.Y), "DownArrow" => new Point(p.Y, p.X),
                    "UpArrow" => new Point(p.Y, h - p.X), _ => p
                }).ToArray()); break;
            case "LeftRightArrow":
                double x = w * a, y = h * b;
                shape = Polygon(new(0, h / 2), new(x, 0), new(x, y), new(w - x, y), new(w - x, 0), new(w, h / 2), new(w - x, h), new(w - x, h - y), new(x, h - y), new(x, h)); break;
            case "Chevron": shape = Polygon(new(0, 0), new(w * (1 - a), 0), new(w, h / 2), new(w * (1 - a), h), new(0, h), new(w * a, h / 2)); break;
            case "Callout":
                double baseY = h * .75, tip = w * a, shoulder = Math.Min(w * .12, w * b);
                shape = Polygon(new(0, 0), new(w, 0), new(w, baseY), new(Math.Min(w, tip + shoulder), baseY), new(tip, h), new(Math.Max(0, tip - shoulder), baseY), new(0, baseY)); break;
            case "Heart":
                shape = Curved(ctx =>
                {
                    ctx.BeginFigure(new(w / 2, h), true, true);
                    ctx.BezierTo(new(-w * .35, h * .38), new(w * .12, -h * .35), new(w / 2, h * .22), true, false);
                    ctx.BezierTo(new(w * .88, -h * .35), new(w * 1.35, h * .38), new(w / 2, h), true, false);
                }); break;
            case "Cylinder":
                shape = Curved(ctx =>
                {
                    double r = h * .12;
                    ctx.BeginFigure(new(0, r), true, true);
                    ctx.BezierTo(new(0, -r / 3), new(w, -r / 3), new(w, r), true, false);
                    ctx.LineTo(new(w, h - r), true, false);
                    ctx.BezierTo(new(w, h + r / 3), new(0, h + r / 3), new(0, h - r), true, false);
                    ctx.LineTo(new(0, r), true, false);
                    ctx.BeginFigure(new(0, r), false, false);
                    ctx.BezierTo(new(0, r * 2.3), new(w, r * 2.3), new(w, r), true, false);
                }); break;
            case "Document":
                shape = Curved(ctx =>
                {
                    ctx.BeginFigure(new(0, 0), true, true); ctx.LineTo(new(w, 0), true, false); ctx.LineTo(new(w, h * .85), true, false);
                    ctx.BezierTo(new(w * .65, h * .6), new(w * .35, h * 1.12), new(0, h * .85), true, false);
                }); break;
            case "LeftBracket": case "RightBracket":
                shape = Curved(ctx =>
                {
                    bool right = s.Kind == "RightBracket"; double outside = right ? w : 0, inside = right ? 0 : w;
                    ctx.BeginFigure(new(inside, 0), false, false); ctx.LineTo(new(outside, 0), true, false); ctx.LineTo(new(outside, h), true, false); ctx.LineTo(new(inside, h), true, false);
                }); fill = null; break;
            case "BracePair":
                shape = Curved(ctx =>
                {
                    foreach (bool right in new[] { false, true })
                    {
                        Point P(double px, double py) => new((right ? 1 - px : px) * w, py * h);
                        ctx.BeginFigure(P(.3, 0), false, false);
                        ctx.BezierTo(P(.12, 0), P(.2, .4), P(0, .5), true, false);
                        ctx.BezierTo(P(.2, .6), P(.12, 1), P(.3, 1), true, false);
                    }
                }); fill = null; break;
            default: shape = new Rectangle { Width = w, Height = h }; break;
        }
        shape.Fill = fill; shape.Stroke = stroke; shape.StrokeThickness = thickness ?? s.StrokeThickness;
        if (s.LineStyle == "Dash") shape.StrokeDashArray = new DoubleCollection { 4, 2 };
        if (s.LineStyle == "Dot") shape.StrokeDashArray = new DoubleCollection { 1, 2 };
        shape.RenderTransform = new ScaleTransform(s.FlipHorizontal ? -1 : 1, s.FlipVertical ? -1 : 1, w / 2, h / 2);
        return shape;
    }
    private static Polygon Polygon(params Point[] points) => new() { Points = new PointCollection(points) };
    private static Polygon Regular(int count, double w, double h) => Polygon(Enumerable.Range(0, count).Select(i =>
        new Point(w * (.5 + .5 * Math.Cos(-Math.PI / 2 + i * 2 * Math.PI / count)), h * (.5 + .5 * Math.Sin(-Math.PI / 2 + i * 2 * Math.PI / count)))).ToArray());
    private static Path Curved(Action<StreamGeometryContext> draw)
    {
        var geometry = new StreamGeometry(); using (var ctx = geometry.Open()) draw(ctx);
        geometry.Freeze(); return new Path { Data = geometry };
    }
    public static Rect TextArea(CanvasShape s)
    {
        double w = Math.Max(1, Math.Abs(s.Width)), h = Math.Max(1, Math.Abs(s.Height));
        Rect rect = new(0, 0, w, h);
        if (s.UseShapeTextArea) rect = s.Kind switch
        {
            "Ellipse" => new(w * .146, h * .146, w * .708, h * .708),
            "Diamond" => new(w * .25, h * .25, w * .5, h * .5),
            "Triangle" => new(w * .25, h * .5, w * .5, h * .48),
            "RightTriangle" => new(0, h * .6, w * .35, h * .4),
            "Hexagon" or "Octagon" => new(w * .18, h * .12, w * .64, h * .76),
            "Star" or "Pentagon" or "Heart" => new(w * .25, h * .3, w * .5, h * .4),
            "Callout" => new(0, 0, w, h * .75), "Cylinder" => new(0, h * .22, w, h * .64),
            "Document" => new(0, 0, w, h * .72),
            "RightArrow" => new(0, h * Second(s), w * (1 - First(s)), h * (1 - 2 * Second(s))),
            "LeftArrow" => new(w * First(s), h * Second(s), w * (1 - First(s)), h * (1 - 2 * Second(s))),
            "LeftRightArrow" => new(w * First(s), h * Second(s), w * (1 - 2 * First(s)), h * (1 - 2 * Second(s))),
            "UpArrow" => new(w * Second(s), h * First(s), w * (1 - 2 * Second(s)), h * (1 - First(s))),
            "DownArrow" => new(w * Second(s), 0, w * (1 - 2 * Second(s)), h * (1 - First(s))),
            "Parallelogram" or "Trapezoid" or "Chevron" => new(w * First(s), 0, w * (1 - 2 * First(s)), h),
            _ => rect
        };
        if (s.FlipHorizontal) rect.X = w - rect.Right;
        if (s.FlipVertical) rect.Y = h - rect.Bottom;
        double margin = Value(s.TextMargin, 4, 0, 100);
        double mx = Math.Min(margin, Math.Max(0, (rect.Width - 1) / 2)), my = s.UseShapeTextArea ? Math.Min(margin, Math.Max(0, (rect.Height - 1) / 2)) : 0;
        rect.Inflate(-mx, -my); return rect;
    }
    public static bool Adjustable(CanvasShape s) => s.CustomVertices.Count == 0 &&
        (IsArrow(s.Kind) || s.Kind is "RoundedRectangle" or "Parallelogram" or "Trapezoid" or "Chevron" or "Callout" or "Star");
    public static Point AdjustmentHandle(CanvasShape s, int index)
    {
        double w = Math.Abs(s.Width), h = Math.Abs(s.Height), a = First(s);
        Point p = s.Kind switch
        {
            "RoundedRectangle" => new(Math.Min(w, h) * a, 0),
            "Callout" => new(w * a, h), "Star" => new(w / 2, h * (.5 - .5 * a)),
            "LeftArrow" => new(index == 0 ? w * a : w / 2, index == 0 ? 0 : h * Second(s)),
            "UpArrow" => new(index == 0 ? 0 : w * Second(s), index == 0 ? h * a : h / 2),
            "DownArrow" => new(index == 0 ? 0 : w * Second(s), index == 0 ? h * (1 - a) : h / 2),
            _ when IsArrow(s.Kind) => new(index == 0 ? w * (1 - a) : w / 2, index == 0 ? 0 : h * Second(s)),
            _ => new(w * a, 0)
        };
        if (s.FlipHorizontal) p.X = w - p.X;
        if (s.FlipVertical) p.Y = h - p.Y;
        return p;
    }
    public static void Adjust(CanvasShape s, int index, Point local)
    {
        double w = Math.Max(1, Math.Abs(s.Width)), h = Math.Max(1, Math.Abs(s.Height));
        if (s.FlipHorizontal) local.X = w - local.X;
        if (s.FlipVertical) local.Y = h - local.Y;
        if (index == 1 && IsArrow(s.Kind))
            s.Adjustment2 = Math.Clamp(s.Kind is "UpArrow" or "DownArrow" ? local.X / w : local.Y / h, .05, .45);
        else
        {
            double a = s.Kind switch
            {
                "RoundedRectangle" => local.X / Math.Min(w, h), "Star" => 1 - 2 * local.Y / h,
                "LeftArrow" => local.X / w, "UpArrow" => local.Y / h, "DownArrow" => 1 - local.Y / h,
                _ when IsArrow(s.Kind) => 1 - local.X / w, _ => local.X / w
            };
            s.Adjustment1 = a; s.Adjustment1 = First(s);
        }
    }
    public static CanvasShape Clone(CanvasShape source)
        => System.Text.Json.JsonSerializer.Deserialize<CanvasShape>(System.Text.Json.JsonSerializer.Serialize(source))!;
    public static void CopyTo(CanvasShape source, CanvasShape target)
    {
        var copy = Clone(source);
        foreach (var property in typeof(CanvasShape).GetProperties())
            if (property.CanRead && property.CanWrite) property.SetValue(target, property.GetValue(copy));
    }
}
