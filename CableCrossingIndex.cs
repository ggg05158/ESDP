using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ESDP
{
    /// <summary>Read-only sweep index. Output order matches the original nested scan.</summary>
    internal static class CableCrossingIndex
    {
        internal readonly record struct Crossing(double X, double Y, int HorizontalCable, int VerticalCable);
        private readonly record struct Segment(Point A, Point B, string Leg, int Cable);
        private readonly record struct SweepEvent(double X, int Kind, int Index);

        internal static List<Crossing> Find(List<List<(Point A, Point B, string LegKey)>> cables)
        {
            const double eps = 3;
            var flat = cables.SelectMany((list, cable) => list.Select(s => new Segment(s.A, s.B, s.LegKey, cable))).ToList();
            var events = new List<SweepEvent>(flat.Count * 2);
            for (int i = 0; i < flat.Count; i++)
            {
                var s = flat[i];
                if (!double.IsFinite(s.A.X) || !double.IsFinite(s.A.Y) ||
                    !double.IsFinite(s.B.X) || !double.IsFinite(s.B.Y)) continue;
                if (Math.Abs(s.A.Y - s.B.Y) < 0.01 && Math.Abs(s.A.X - s.B.X) > eps * 2)
                {
                    events.Add(new SweepEvent(Math.Min(s.A.X, s.B.X) + eps, 2, i));
                    events.Add(new SweepEvent(Math.Max(s.A.X, s.B.X) - eps, 0, i));
                }
                if (Math.Abs(s.A.X - s.B.X) < 0.01 && Math.Abs(s.A.Y - s.B.Y) > eps * 2)
                    events.Add(new SweepEvent(s.A.X, 1, i));
            }
            events.Sort((a, b) =>
            {
                int order = a.X.CompareTo(b.X);
                if (order == 0) order = a.Kind.CompareTo(b.Kind);
                return order == 0 ? a.Index.CompareTo(b.Index) : order;
            });
            var active = new SortedSet<(double Y, int Index)>();
            var firstPairs = new Dictionary<(double X, double Y), (int H, int V)>();
            foreach (var ev in events)
            {
                var segment = flat[ev.Index];
                var key = (segment.A.Y, ev.Index);
                if (ev.Kind == 0) active.Remove(key);
                else if (ev.Kind == 2) active.Add(key);
                else
                {
                    double bottom = Math.Min(segment.A.Y, segment.B.Y) + eps;
                    double top = Math.Max(segment.A.Y, segment.B.Y) - eps;
                    // Exclude exact end-clearance boundaries, as in the old scan.
                    foreach (var h in active.GetViewBetween((bottom, int.MaxValue), (top, int.MinValue)))
                        if (h.Index != ev.Index && flat[h.Index].Leg != segment.Leg)
                        {
                            var crossingKey = (Math.Round(segment.A.X, 1), Math.Round(h.Y, 1));
                            var pair = (H: h.Index, V: ev.Index);
                            if (!firstPairs.TryGetValue(crossingKey, out var earlier) ||
                                pair.H < earlier.H || (pair.H == earlier.H && pair.V < earlier.V))
                                firstPairs[crossingKey] = pair;
                        }
                }
            }
            var pairs = firstPairs.Values.ToList();
            pairs.Sort((a, b) => a.H != b.H ? a.H.CompareTo(b.H) : a.V.CompareTo(b.V));
            var result = new List<Crossing>();
            foreach (var pair in pairs)
            {
                var h = flat[pair.H];
                var v = flat[pair.V];
                result.Add(new Crossing(v.A.X, h.A.Y, h.Cable, v.Cable));
            }
            return result;
        }
    }
}
