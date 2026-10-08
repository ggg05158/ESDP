using System;
using System.Collections.Generic;
using System.Windows;

namespace ESDP
{
    // Broad phase only: the caller retains the exact overlap/clearance rule.
    internal sealed class RectSpatialIndex
    {
        private const double CellSize = 64;
        private readonly Dictionary<(long X, long Y), HashSet<object>> _cells = new();
        private readonly Dictionary<object, Rect> _boxes = new();
        private readonly HashSet<object> _large = new();

        private static List<(long X, long Y)>? Cells(Rect box)
        {
            if (box.IsEmpty || !double.IsFinite(box.Left) || !double.IsFinite(box.Top) ||
                !double.IsFinite(box.Right) || !double.IsFinite(box.Bottom)) return null;
            double left = Math.Floor(box.Left / CellSize), top = Math.Floor(box.Top / CellSize);
            double right = Math.Floor(box.Right / CellSize), bottom = Math.Floor(box.Bottom / CellSize);
            if (Math.Abs(left) > 1e12 || Math.Abs(top) > 1e12 || Math.Abs(right) > 1e12 || Math.Abs(bottom) > 1e12 ||
                (right - left + 1) * (bottom - top + 1) > 4096) return null;
            var result = new List<(long, long)>();
            for (long x = (long)left; x <= (long)right; x++)
                for (long y = (long)top; y <= (long)bottom; y++) result.Add((x, y));
            return result;
        }

        internal void Put(object key, Rect box)
        {
            Remove(key);
            _boxes[key] = box;
            var cells = Cells(box);
            if (cells == null) { _large.Add(key); return; }
            foreach (var cell in cells)
            {
                if (!_cells.TryGetValue(cell, out var members)) _cells[cell] = members = new HashSet<object>();
                members.Add(key);
            }
        }

        internal void Remove(object key)
        {
            if (!_boxes.Remove(key, out var old)) return;
            _large.Remove(key);
            foreach (var cell in Cells(old) ?? new())
                if (_cells.TryGetValue(cell, out var members))
                {
                    members.Remove(key);
                    if (members.Count == 0) _cells.Remove(cell);
                }
        }

        internal bool AnyOverlap(Rect box, object? except = null, Func<Rect, Rect, bool>? exact = null)
        {
            var cells = Cells(box);
            var candidates = new HashSet<object>(_large);
            if (cells == null) candidates.UnionWith(_boxes.Keys);
            else foreach (var cell in cells)
                if (_cells.TryGetValue(cell, out var members)) candidates.UnionWith(members);
            foreach (var key in candidates)
                if (!ReferenceEquals(key, except) && (exact?.Invoke(box, _boxes[key]) ?? box.IntersectsWith(_boxes[key]))) return true;
            return false;
        }
    }
}
