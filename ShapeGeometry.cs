using System.Windows;
using System.Windows.Media;
using ESDP.Models;
using Point = System.Windows.Point;

namespace ESDP;

internal static class ShapeGeometry
{
    public static double NormalizeAngle(double angle) => double.IsFinite(angle) ? (angle % 360 + 360) % 360 : 0;
    public static Rect LocalBounds(CanvasShape shape)
    {
        if (shape.Kind == "ElbowLine" && shape.LinePoints.Count > 0)
        {
            double x = shape.LinePoints.Min(p => p.X), y = shape.LinePoints.Min(p => p.Y);
            return new Rect(x, y, shape.LinePoints.Max(p => p.X) - x, shape.LinePoints.Max(p => p.Y) - y);
        }
        return new Rect(Math.Min(0, shape.Width), Math.Min(0, shape.Height), Math.Abs(shape.Width), Math.Abs(shape.Height));
    }
    public static Point Center(CanvasShape shape)
    {
        var b = LocalBounds(shape);
        return new Point(shape.X + b.X + b.Width / 2, shape.Y + b.Y + b.Height / 2);
    }
    public static Rect Bounds(CanvasShape shape)
    {
        var b = LocalBounds(shape); b.Offset(shape.X, shape.Y);
        var center = Center(shape);
        return new RotateTransform(NormalizeAngle(shape.RotationAngle), center.X, center.Y).TransformBounds(b);
    }
    public static Point ToUnrotated(CanvasShape shape, Point point)
    {
        var center = Center(shape);
        return new RotateTransform(-NormalizeAngle(shape.RotationAngle), center.X, center.Y).Transform(point);
    }
    public static Vector Rotate(Vector vector, double angle)
    {
        var p = new RotateTransform(angle).Transform(new Point(vector.X, vector.Y));
        return new Vector(p.X, p.Y);
    }
    public static Point ToEditingWorld(CanvasShape shape, Point point)
    {
        var p = ToUnrotated(shape, point); var center = Center(shape);
        if (shape.FlipHorizontal) p.X = 2 * center.X - p.X;
        if (shape.FlipVertical) p.Y = 2 * center.Y - p.Y;
        return p;
    }
    // When geometry changes its bounding center, retain the unchanged world-space points.
    public static void PreserveCenterFrame(CanvasShape shape, Point previousCenter)
    {
        var delta = Center(shape) - previousCenter;
        var shift = Rotate(delta, shape.RotationAngle) - delta;
        shape.X += shift.X; shape.Y += shift.Y;
    }
}
