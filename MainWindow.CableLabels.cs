using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ESDP.Models;
using Microsoft.Win32;

using Point = System.Windows.Point;
using Path = System.IO.Path;

namespace ESDP
{
    public partial class MainWindow
    {
        private static List<Point> GetPortLabelCandidates(
            PortSide side, double anchorX, double anchorY,
            double width, double height)
        {
            const double nearGap = 4;
            const double sideGap = 6;
            Point aboveLeft = new(
                anchorX - width - nearGap, anchorY - height - nearGap);
            Point aboveRight = new(
                anchorX + nearGap, anchorY - height - nearGap);
            Point belowLeft = new(
                anchorX - width - nearGap, anchorY + nearGap);
            Point belowRight = new(
                anchorX + nearGap, anchorY + nearGap);
            Point left = new(
                anchorX - width - sideGap, anchorY - height / 2);
            Point right = new(
                anchorX + sideGap, anchorY - height / 2);
            Point above = new(
                anchorX - width / 2, anchorY - height - sideGap);
            Point below = new(
                anchorX - width / 2, anchorY + sideGap);

            return side switch
            {
                PortSide.Left => new()
                {
                    right, aboveRight, belowRight, above, below,
                    aboveLeft, belowLeft, left
                },
                PortSide.Right => new()
                {
                    left, aboveLeft, belowLeft, above, below,
                    aboveRight, belowRight, right
                },
                PortSide.Top => new()
                {
                    below, belowRight, belowLeft, right, left,
                    aboveRight, aboveLeft, above
                },
                _ => new()
                {
                    above, aboveRight, aboveLeft, right, left,
                    belowRight, belowLeft, below
                }
            };
        }


        /// <summary>
        /// 저장된 위치대로 이름표를 표시한다. 드래그 프레임에서는 변경된 케이블의 표시만 재사용한다.
        /// </summary>
        private void UpdateCableLabels(HashSet<PlacedCable>? changed = null)
        {
            var reusable = new Dictionary<(PlacedCable, bool, string?), Queue<TextBlock>>();
            if (_labelOverlay == null)
            {
                _labelOverlay = new Canvas { Background = null, IsHitTestVisible = true };
                _labelOverlay.MouseMove += LabelOverlay_MouseMove;
                _labelOverlay.MouseLeftButtonUp += LabelOverlay_MouseUp;
                MainCanvas.Children.Add(_labelOverlay);
            }
            if (changed == null) _labelOverlay.Children.Clear();
            else
                foreach (var visual in _labelOverlay.Children.OfType<TextBlock>()
                    .Where(text => text.Tag is CableLabelVisualInfo info && changed.Contains(info.Placed)).ToList())
                {
                    var info = (CableLabelVisualInfo)visual.Tag;
                    var key = (info.Placed, info.Label.IsCableName, info.Label.ConnectorId);
                    if (!reusable.TryGetValue(key, out var queue)) reusable[key] = queue = new Queue<TextBlock>();
                    queue.Enqueue(visual);
                    _labelOverlay.Children.Remove(visual);
                }
            Canvas.SetLeft(_labelOverlay, 0);
            Canvas.SetTop(_labelOverlay, 0);
            Panel.SetZIndex(_labelOverlay, 101);



            foreach (var pc in _placedCables)
            {
                if (changed != null && !changed.Contains(pc)) continue;
                foreach (var lbl in pc.RenderedLabels)
                {
                    double thisFontSize = lbl.IsCableName
                        ? (double.IsFinite(pc.EffectiveCable.NameFontSize) ? Math.Clamp(pc.EffectiveCable.NameFontSize, 6, 200) : 13)
                        : lbl.FontSize;
                    int lineCount = Math.Max(1, lbl.Text.Count(ch => ch == '\n') + 1);
                    double thisLabelHeight = (thisFontSize + 3) * lineCount;
                    double approxW = lbl.Text.Split('\n')
                        .Select(line => Math.Max(1, line.Length) * thisFontSize * 0.62)
                        .DefaultIfEmpty(thisFontSize)
                        .Max();
                    if (lbl.IsCableName)
                    {
                        // Long/Korean names must use rendered text dimensions at the selected size.
                        var probe = new TextBlock { Text = lbl.Text, FontSize = thisFontSize, FontWeight = FontWeights.Bold };
                        _labelOverlay.Children.Add(probe);
                        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        approxW = probe.DesiredSize.Width;
                        thisLabelHeight = probe.DesiredSize.Height;
                        _labelOverlay.Children.Remove(probe);
                    }
                    double x = lbl.X - approxW / 2;
                    double y = lbl.Y;
                    // 일반 렌더링과 포트 드래그에서는 저장된 위치를 그대로
                    // 따른다. 충돌 회피는 새 케이블 생성 또는 사용자가 누른
                    // '케이블/포트명 자동정리' 명령에서만 모델에 반영한다.

                    if (!lbl.IsCableName && lbl.ConnectorId != null)
                    {
                        // 포트명은 최초 글자 위치가 아니라 실제 케이블 포트를
                        // 중심으로 돈다. 따라서 포트의 어느 방향으로도 옮길 수
                        // 있고, 예전에 멀리 저장된 위치도 가까운 범위로 정리된다.
                        var labelCenter = new Point(
                            x + approxW / 2,
                            y + thisLabelHeight / 2);
                        double vx = labelCenter.X - lbl.AnchorX;
                        double vy = labelCenter.Y - lbl.AnchorY;
                        double radius = Math.Sqrt(vx * vx + vy * vy);
                        if (radius < 0.001)
                        {
                            vx = 0;
                            vy = -1;
                            radius = 1;
                        }

                        double limitedRadius = Math.Clamp(
                            radius,
                            CablePortLabelMinRadius,
                            CablePortLabelMaxRadius);
                        if (Math.Abs(limitedRadius - radius) > 0.001)
                        {
                            double ratio = limitedRadius / radius;
                            labelCenter = new Point(
                                lbl.AnchorX + vx * ratio,
                                lbl.AnchorY + vy * ratio);
                            x = labelCenter.X - approxW / 2;
                            y = labelCenter.Y - thisLabelHeight / 2;
                        }
                    }
                    else if (lbl.IsCableName)
                    {
                        // 자동 후보 및 이전 프로젝트에 저장된 오프셋까지 포함해
                        // 케이블명이 자기 선에서 멀리 떨어져 보이지 않게 한다.
                        const double maxCableNameDistance = 26;
                        var labelAnchor = new Point(x + approxW / 2, y);
                        var nearest = NearestPointOnCableLine(pc, labelAnchor);
                        double distance = Dist(
                            labelAnchor.X, labelAnchor.Y,
                            nearest.X, nearest.Y);
                        if (distance > maxCableNameDistance)
                        {
                            double ratio = maxCableNameDistance / distance;
                            labelAnchor = new Point(
                                nearest.X + (labelAnchor.X - nearest.X) * ratio,
                                nearest.Y + (labelAnchor.Y - nearest.Y) * ratio);
                            x = labelAnchor.X - approxW / 2;
                            y = labelAnchor.Y;
                        }
                    }

                    bool isNew = !reusable.TryGetValue((pc, lbl.IsCableName, lbl.ConnectorId), out var available) || available.Count == 0;
                    var text = isNew ? new TextBlock() : available!.Dequeue();
                    text.Text = lbl.Text;
                    text.FontSize = thisFontSize;
                    text.Tag = new CableLabelVisualInfo
                        {
                            Placed = pc,
                            Label = lbl
                        };

                    if (lbl.IsCableName)
                    {
                        text.FontWeight = FontWeights.Bold;
                        text.Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)); // 진한 회색
                        text.Cursor = Cursors.SizeAll;
                        text.ToolTip = "더블클릭: 케이블 정보 / 끌기: 이름표 위치 조절";
                        var pcCapture2 = pc;
                        if (isNew) text.MouseLeftButtonDown += (s, e) =>
                            CableNameLabel_MouseDown(
                                pcCapture2, (TextBlock)s, e);
                    }
                    else
                    {
                        text.FontWeight = FontWeights.Bold;
                        text.Foreground = HexToBrush(lbl.ColorHex);
                        if (lbl.ConnectorId != null)
                        {
                            text.Cursor = Cursors.SizeAll;
                            text.ToolTip = "끌어서 이름표 위치 조절";
                            var pcCapture = pc;
                            var connId = lbl.ConnectorId;
                            if (isNew) text.MouseLeftButtonDown += (s, e) =>
                                CableLabel_MouseDown(pcCapture, connId, (TextBlock)s, e);
                        }
                    }

                    // 케이블명, 케이블 포트명 및 함께 표시되는 커넥터명은
                    // 배경색과 관계없이 읽히도록 글자 획 주변에만 매우 얇은
                    // 대비 윤곽선을 둔다. 라벨의 배치 크기에는 영향을 주지 않는다.
                    text.Effect = CreateThinLabelOutline(text.Foreground);

                    Canvas.SetLeft(text, x);
                    Canvas.SetTop(text, y);
                    _labelOverlay.Children.Add(text);
                }
            }
        }

        /// <summary>
        /// 케이블/포트명 자동정리 명령에서 각 케이블에 대해 호출한다. 현재 화면의 실제 라벨 위치를
        /// 기준으로 충돌한 이름표만 가장 가까운 빈 자리로 옮기고, 충돌하지
        /// 않은 이름표와 사용자가 잡아 둔 위치는 그대로 둔다.
        /// </summary>
        private static readonly Point[] CableLabelSearchOffsets =
            (from dx in Enumerable.Range(0, 73)
             from dy in Enumerable.Range(0, 73)
             select new Point(dx * 2 - 72, dy * 2 - 72))
            .OrderBy(point => point.X * point.X + point.Y * point.Y)
            .ThenBy(point => Math.Abs(point.X) + Math.Abs(point.Y)).ToArray();

        private RectSpatialIndex BuildCableLabelIndex()
        {
            var index = new RectSpatialIndex();
            void AddLabel(TextBlock text, double x, double y)
            {
                if (!double.IsFinite(x) || !double.IsFinite(y)) return;
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                index.Put(text, new Rect(x, y, Math.Max(1, text.DesiredSize.Width), Math.Max(1, text.DesiredSize.Height)));
            }
            foreach (var device in _placed)
            {
                index.Put(device, new Rect(device.Instance.X, device.Instance.Y, device.Instance.Width, device.Instance.Height));
                foreach (var text in device.Container.Children.OfType<TextBlock>()
                    .Where(text => text.Tag is DevicePreviewRenderer.PortLabelVisualInfo))
                    AddLabel(text, device.Instance.X + Canvas.GetLeft(text), device.Instance.Y + Canvas.GetTop(text));
            }
            foreach (var cable in _placedCables)
                foreach (var (a, b, _) in cable.RenderedSegments)
                    index.Put(new object(), new Rect(Math.Min(a.X, b.X) - 1.5, Math.Min(a.Y, b.Y) - 1.5,
                        Math.Max(3, Math.Abs(a.X - b.X) + 3), Math.Max(3, Math.Abs(a.Y - b.Y) + 3)));
            if (_labelOverlay != null)
                foreach (var text in _labelOverlay.Children.OfType<TextBlock>())
                    AddLabel(text, Canvas.GetLeft(text), Canvas.GetTop(text));
            return index;
        }

        private bool TidyCableLabels(PlacedCable target, RectSpatialIndex? obstacles = null)
        {
            if (_labelOverlay == null) return false;

            var targetLabels = _labelOverlay.Children
                .OfType<TextBlock>()
                .Where(text => text.Tag is CableLabelVisualInfo info &&
                               info.Placed == target)
                .ToList();
            if (targetLabels.Count == 0) return false;

            string rootLegKey = $"{target.Instance.Id}:root";
            var renderedRootSegments = target.RenderedSegments
                .Where(segment => segment.LegKey == rootLegKey)
                .ToList();
            var rootRoute = new List<Point>();
            if (renderedRootSegments.Count > 0)
            {
                rootRoute.Add(renderedRootSegments[0].A);
                rootRoute.AddRange(renderedRootSegments.Select(segment => segment.B));
            }
            Point? cableCenter = rootRoute is { Count: >= 2 }
                ? CableRenderer.PointAlongPolyline(rootRoute, 0.5)
                : null;

            static Rect LabelRect(TextBlock text, double left, double top)
            {
                text.Measure(new Size(
                    double.PositiveInfinity, double.PositiveInfinity));
                return new Rect(
                    left,
                    top,
                    Math.Max(1, text.DesiredSize.Width),
                    Math.Max(1, text.DesiredSize.Height));
            }

            obstacles ??= BuildCableLabelIndex();
            bool Collides(TextBlock subject, Rect proposed)
            {
                proposed.Inflate(2, 2);
                return obstacles.AnyOverlap(proposed, subject);
            }
            var offsets = CableLabelSearchOffsets;

            bool changed = false;
            foreach (var text in targetLabels)
            {
                var info = (CableLabelVisualInfo)text.Tag;
                var label = info.Label;
                double currentLeft = Canvas.GetLeft(text);
                double currentTop = Canvas.GetTop(text);
                if (double.IsNaN(currentLeft) || double.IsNaN(currentTop))
                    continue;

                Rect current = LabelRect(text, currentLeft, currentTop);
                Point? chosen = null;

                // 케이블명은 개별 정리 시 실제 루트 경로 길이의 중앙을
                // 우선한다. 중앙 위가 막혀 있으면 아래·좌·우를 보고,
                // 모두 막힌 경우에만 기존 위치를 기준으로 최소 이동한다.
                if (label.IsCableName && cableCenter.HasValue)
                {
                    double gap = 5;
                    var routeAnchors = new List<Point> { cableCenter.Value };
                    if (rootRoute is { Count: >= 2 })
                    {
                        foreach (double fraction in new[] { 0.4, 0.6, 0.3, 0.7 })
                            routeAnchors.Add(
                                CableRenderer.PointAlongPolyline(rootRoute, fraction));
                    }

                    var preferred = routeAnchors.SelectMany(anchor => new[]
                    {
                        new Point(
                            anchor.X - current.Width / 2,
                            anchor.Y - current.Height - gap),
                        new Point(
                            anchor.X - current.Width / 2,
                            anchor.Y + gap),
                        new Point(
                            anchor.X - current.Width - gap,
                            anchor.Y - current.Height / 2),
                        new Point(
                            anchor.X + gap,
                            anchor.Y - current.Height / 2)
                    });
                    foreach (var point in preferred)
                    {
                        if (point.X < 0 || point.Y < 0) continue;
                        if (Collides(
                                text,
                                new Rect(point.X, point.Y, current.Width, current.Height)))
                            continue;
                        chosen = point;
                        break;
                    }
                }

                if (!chosen.HasValue && !Collides(text, current))
                    continue;

                foreach (var offset in chosen.HasValue
                             ? Enumerable.Empty<Point>()
                             : offsets)
                {
                    double left = currentLeft + offset.X;
                    double top = currentTop + offset.Y;
                    if (left < 0 || top < 0) continue;
                    var candidate = new Rect(left, top, current.Width, current.Height);
                    var center = new Point(
                        candidate.Left + candidate.Width / 2,
                        candidate.Top + candidate.Height / 2);

                    if (!label.IsCableName)
                    {
                        double radius = Dist(
                            center.X, center.Y,
                            label.AnchorX, label.AnchorY);
                        if (radius < CablePortLabelMinRadius - 0.5 ||
                            radius > CablePortLabelMaxRadius + 0.5)
                            continue;
                    }
                    else
                    {
                        Point nearest = NearestPointOnCableLine(target, center);
                        if (Dist(
                                center.X, center.Y,
                                nearest.X, nearest.Y) > 30)
                            continue;
                    }

                    if (Collides(text, candidate)) continue;
                    chosen = new Point(left, top);
                    break;
                }

                if (!chosen.HasValue) continue;

                double width = current.Width;
                if (label.IsCableName)
                {
                    target.Instance.NameLabelDX =
                        chosen.Value.X + width / 2 - label.BaseX;
                    target.Instance.NameLabelDY =
                        chosen.Value.Y - label.BaseY;
                }
                else if (label.ConnectorId != null)
                {
                    var layout = FindPortLayoutById(
                        target.Instance.Id, label.ConnectorId);
                    if (layout == null) continue;
                    layout.HasCustomLabelPosition = true;
                    layout.LabelDX =
                        chosen.Value.X + width / 2 - label.BaseX;
                    layout.LabelDY = chosen.Value.Y - label.BaseY;
                }

                Canvas.SetLeft(text, chosen.Value.X);
                Canvas.SetTop(text, chosen.Value.Y);
                obstacles.Put(text, new Rect(chosen.Value.X, chosen.Value.Y, current.Width, current.Height));
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// 자동정리 명령에서 장치 포트명을 포트 막대, 다른 포트명, 케이블 선과
        /// 겹치지 않는 가까운 자리로 옮긴다. 평소 렌더링이나 포트 이동 중에는
        /// 호출하지 않아 다른 포트명이 임의로 튀지 않게 한다.
        /// </summary>
        private bool TidyDevicePortLabels()
        {
            static Rect ExpandedLineBox(Point a, Point b)
            {
                return new Rect(
                    Math.Min(a.X, b.X) - 2,
                    Math.Min(a.Y, b.Y) - 2,
                    Math.Max(4, Math.Abs(a.X - b.X) + 4),
                    Math.Max(4, Math.Abs(a.Y - b.Y) + 4));
            }

            static double RectDistance(Rect a, Rect b)
            {
                double dx = Math.Max(0,
                    Math.Max(a.Left - b.Right, b.Left - a.Right));
                double dy = Math.Max(0,
                    Math.Max(a.Top - b.Bottom, b.Top - a.Bottom));
                return Math.Sqrt(dx * dx + dy * dy);
            }

            static Rect AbsoluteLabelRect(PlacedDevice owner, TextBlock label)
            {
                label.Measure(new Size(
                    double.PositiveInfinity, double.PositiveInfinity));
                return new Rect(
                    owner.Instance.X + Canvas.GetLeft(label),
                    owner.Instance.Y + Canvas.GetTop(label),
                    Math.Max(1, label.DesiredSize.Width),
                    Math.Max(1, label.DesiredSize.Height));
            }

            var allLineBoxes = _placedCables
                .SelectMany(cable => cable.RenderedSegments)
                .Select(segment => ExpandedLineBox(segment.A, segment.B))
                .ToList();
            var labels = _placed
                .SelectMany(owner => owner.Container.Children
                    .OfType<TextBlock>()
                    .Where(label => label.Tag is
                        DevicePreviewRenderer.PortLabelVisualInfo)
                    .Select(label => (Owner: owner, Label: label)))
                .ToList();
            var cableLabelBoxes = _labelOverlay?.Children
                .OfType<TextBlock>()
                .Select(label =>
                {
                    label.Measure(new Size(
                        double.PositiveInfinity, double.PositiveInfinity));
                    return new Rect(
                        Canvas.GetLeft(label), Canvas.GetTop(label),
                        Math.Max(1, label.DesiredSize.Width),
                        Math.Max(1, label.DesiredSize.Height));
                })
                .ToList() ?? new List<Rect>();

            var portBoxes = _placed.SelectMany(owner =>
                    DevicePreviewRenderer.GetPortAnchors(
                        owner.EffectiveDevice,
                        owner.Instance.X, owner.Instance.Y,
                        owner.Instance.Width, owner.Instance.Height)
                    .Select(anchor =>
                    {
                        bool vertical = anchor.Side is PortSide.Left or PortSide.Right;
                        double width = vertical
                            ? DevicePreviewRenderer.PortWidth
                            : DevicePreviewRenderer.PortHeight;
                        double height = vertical
                            ? DevicePreviewRenderer.PortHeight
                            : DevicePreviewRenderer.PortWidth;
                        var actual = new Rect(
                            anchor.X - width / 2,
                            anchor.Y - height / 2,
                            width, height);
                        var protectedRect = actual;
                        protectedRect.Inflate(4, 4);
                        return (Owner: owner, PortId: anchor.Port.Id,
                            Actual: actual, Protected: protectedRect);
                    }))
                .ToList();

            bool changed = false;
            foreach (var entry in labels)
            {
                var owner = entry.Owner;
                var label = entry.Label;
                var info = (DevicePreviewRenderer.PortLabelVisualInfo)label.Tag;
                Rect current = AbsoluteLabelRect(owner, label);
                var currentClearance = current;
                currentClearance.Inflate(2, 2);
                var ownPort = portBoxes.FirstOrDefault(item =>
                    item.Owner == owner && item.PortId == info.Port.Id);
                if (ownPort.Owner == null) continue;

                const double maximumPortGap = 15;
                bool tooFarFromPort =
                    RectDistance(current, ownPort.Actual) > maximumPortGap;
                bool touchesPort = portBoxes.Any(item =>
                    current.IntersectsWith(item.Protected));
                bool overlapsOtherLabel = labels.Any(other =>
                    other.Label != label && currentClearance.IntersectsWith(
                        AbsoluteLabelRect(other.Owner, other.Label)));
                bool crossesCable =
                    allLineBoxes.Any(currentClearance.IntersectsWith);
                if (!tooFarFromPort && !touchesPort &&
                    !overlapsOtherLabel && !crossesCable)
                    continue;

                var localCandidates = new List<Point>();
                localCandidates.AddRange(GetPortLabelCandidates(
                    info.Side, info.AnchorX, info.AnchorY,
                    current.Width, current.Height));
                for (int radius = 2; radius <= 30; radius += 2)
                {
                    for (int delta = -radius; delta <= radius; delta += 2)
                    {
                        localCandidates.Add(new Point(
                            Canvas.GetLeft(label) + delta,
                            Canvas.GetTop(label) - radius));
                        localCandidates.Add(new Point(
                            Canvas.GetLeft(label) + delta,
                            Canvas.GetTop(label) + radius));
                        localCandidates.Add(new Point(
                            Canvas.GetLeft(label) - radius,
                            Canvas.GetTop(label) + delta));
                        localCandidates.Add(new Point(
                            Canvas.GetLeft(label) + radius,
                            Canvas.GetTop(label) + delta));
                    }
                }

                Point? chosen = null;
                double bestScore = double.MaxValue;
                foreach (Point local in localCandidates.Distinct())
                {
                    var candidate = new Rect(
                        owner.Instance.X + local.X,
                        owner.Instance.Y + local.Y,
                        current.Width, current.Height);
                    if (candidate.Left < 0 || candidate.Top < 0) continue;
                    var clearance = candidate;
                    clearance.Inflate(2, 2);
                    if (RectDistance(candidate, ownPort.Actual) >
                        maximumPortGap)
                        continue;
                    if (allLineBoxes.Any(clearance.IntersectsWith)) continue;
                    if (portBoxes.Any(item =>
                            candidate.IntersectsWith(item.Protected)))
                        continue;

                    bool hitsOtherPortLabel = labels.Any(other =>
                        other.Label != label &&
                        clearance.IntersectsWith(
                            AbsoluteLabelRect(other.Owner, other.Label)));
                    if (hitsOtherPortLabel) continue;
                    if (cableLabelBoxes.Any(clearance.IntersectsWith)) continue;

                    bool hitsOtherDevice = _placed.Any(device =>
                        device != owner && clearance.IntersectsWith(new Rect(
                            device.Instance.X, device.Instance.Y,
                            device.Instance.Width, device.Instance.Height)));
                    if (hitsOtherDevice) continue;

                    var namePlate = owner.Container.Children
                        .OfType<Border>()
                        .FirstOrDefault(border => border.Tag is
                            DevicePreviewRenderer.DeviceNameVisualInfo);
                    if (namePlate != null)
                    {
                        namePlate.Measure(new Size(
                            double.PositiveInfinity,
                            double.PositiveInfinity));
                        var ownName = new Rect(
                            owner.Instance.X + Canvas.GetLeft(namePlate),
                            owner.Instance.Y + Canvas.GetTop(namePlate),
                            Math.Max(1, namePlate.DesiredSize.Width),
                            Math.Max(1, namePlate.DesiredSize.Height));
                        if (clearance.IntersectsWith(ownName)) continue;
                    }

                    double dx = candidate.X - current.X;
                    double dy = candidate.Y - current.Y;
                    var ownBody = new Rect(
                        owner.Instance.X, owner.Instance.Y,
                        owner.Instance.Width, owner.Instance.Height);
                    bool insideOwn = candidate.Left >= ownBody.Left &&
                                     candidate.Right <= ownBody.Right &&
                                     candidate.Top >= ownBody.Top &&
                                     candidate.Bottom <= ownBody.Bottom;
                    double score = dx * dx + dy * dy +
                                   (insideOwn ? 0 : 250);
                    if (score >= bestScore) continue;
                    bestScore = score;
                    chosen = local;
                }

                if (!chosen.HasValue) continue;
                info.Port.HasCustomLabelPosition = true;
                info.Port.LabelOffsetX = chosen.Value.X - info.AnchorX;
                info.Port.LabelOffsetY = chosen.Value.Y - info.AnchorY;
                Canvas.SetLeft(label, chosen.Value.X);
                Canvas.SetTop(label, chosen.Value.Y);
                changed = true;
            }

            return changed;
        }

        private void UpdateCableCrossings()
        {
            if (_crossingOverlay == null)
            {
                _crossingOverlay = new Canvas { Background = null, IsHitTestVisible = false };
                MainCanvas.Children.Add(_crossingOverlay);
            }
            _crossingOverlay.Children.Clear();
            Canvas.SetLeft(_crossingOverlay, 0);
            Canvas.SetTop(_crossingOverlay, 0);
            Panel.SetZIndex(_crossingOverlay, 100);

            var allSegs = _placedCables.Select(pc => pc.RenderedSegments).ToList();
            var brushes = _placedCables
                .Select(pc => GetCableLineBrush(pc.EffectiveCable))
                .ToList();
            CableRenderer.DrawCrossingJumps(_crossingOverlay, allSegs, brushes);
        }

        /// <summary>이 케이블의 모든 선분 중, 주어진 점에서 가장 가까운 지점을 찾는다.</summary>
        private static Point NearestPointOnCableLine(PlacedCable pc, Point p)
        {
            double best = double.MaxValue;
            Point bestPt = p;
            foreach (var (a, b, _) in pc.RenderedSegments)
            {
                var proj = ProjectPointToSegment(p, a, b);
                double d = Dist(p.X, p.Y, proj.X, proj.Y);
                if (d < best) { best = d; bestPt = proj; }
            }
            return bestPt;
        }

        private static Point ProjectPointToSegment(Point p, Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 0.0001) return a;
            double t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq));
            return new Point(a.X + t * dx, a.Y + t * dy);
        }

        private void CableLabel_MouseDown(
            PlacedCable pc, string connectorId, TextBlock label, MouseButtonEventArgs e)
        {
            var lay = FindPortLayoutById(pc.Instance.Id, connectorId);
            if (lay == null) return;

            var renderedLabel = pc.RenderedLabels.FirstOrDefault(item =>
                !item.IsCableName && item.ConnectorId == connectorId);
            if (renderedLabel != null)
            {
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double labelWidth = Math.Max(label.ActualWidth, label.DesiredSize.Width);
                double visibleLeft = Canvas.GetLeft(label);
                double visibleTop = Canvas.GetTop(label);
                if (!double.IsNaN(visibleLeft) && !double.IsNaN(visibleTop))
                {
                    lay.LabelDX = visibleLeft + labelWidth / 2 - renderedLabel.BaseX;
                    lay.LabelDY = visibleTop - renderedLabel.BaseY;
                }
            }

            _cableLabelDragPlaced = pc;
            _cableLabelDragConnectorId = connectorId;
            _cableLabelDragIsName = false;
            _cableLabelDragStartMouse = e.GetPosition(MainCanvas);
            _cableLabelDragOrigDX = lay.LabelDX;
            _cableLabelDragOrigDY = lay.LabelDY;
            lay.HasCustomLabelPosition = true;

            _labelOverlay?.CaptureMouse();
            e.Handled = true;
        }

        private void CableNameLabel_MouseDown(
            PlacedCable pc, TextBlock label, MouseButtonEventArgs e)
        {
            SelectCable(pc);

            if (e.ClickCount == 2)
            {
                _labelOverlay?.ReleaseMouseCapture();
                _cableLabelDragPlaced = null;
                _cableLabelDragConnectorId = null;
                _cableLabelDragIsName = false;
                OpenCableProperties(pc);
                e.Handled = true;
                return;
            }

            var renderedLabel = pc.RenderedLabels.FirstOrDefault(
                item => item.IsCableName);
            if (renderedLabel != null)
            {
                label.Measure(new Size(
                    double.PositiveInfinity, double.PositiveInfinity));
                double labelWidth = Math.Max(
                    label.ActualWidth, label.DesiredSize.Width);
                double visibleLeft = Canvas.GetLeft(label);
                double visibleTop = Canvas.GetTop(label);
                if (!double.IsNaN(visibleLeft) && !double.IsNaN(visibleTop))
                {
                    pc.Instance.NameLabelDX =
                        visibleLeft + labelWidth / 2 - renderedLabel.BaseX;
                    pc.Instance.NameLabelDY =
                        visibleTop - renderedLabel.BaseY;
                }
            }

            _cableLabelDragPlaced = pc;
            _cableLabelDragConnectorId = null;
            _cableLabelDragIsName = true;
            _cableLabelDragStartMouse = e.GetPosition(MainCanvas);
            _cableLabelDragOrigDX = pc.Instance.NameLabelDX;
            _cableLabelDragOrigDY = pc.Instance.NameLabelDY;

            _labelOverlay?.CaptureMouse();
            e.Handled = true;
        }

        private void LabelOverlay_MouseMove(object sender, MouseEventArgs e)
        {
            if (_cableLabelDragPlaced == null) return;
            var mouse = e.GetPosition(MainCanvas);

            if (_cableLabelDragIsName)
            {
                // 원 모양 반경이 아니라, "케이블 선 자체에서 너무 멀어지지 않는" 방식으로 제한한다.
                // 그래야 이름표가 선을 따라 어디로든(반경 안에서) 움직일 수 있다.
                var nameLbl = _cableLabelDragPlaced.RenderedLabels.FirstOrDefault(l => l.IsCableName);
                if (nameLbl == null) return;

                double ndx = _cableLabelDragOrigDX + (mouse.X - _cableLabelDragStartMouse.X);
                double ndy = _cableLabelDragOrigDY + (mouse.Y - _cableLabelDragStartMouse.Y);
                var candidate = new Point(nameLbl.BaseX + ndx, nameLbl.BaseY + ndy);

                var nearest = NearestPointOnCableLine(_cableLabelDragPlaced, candidate);
                double distToLine = Dist(candidate.X, candidate.Y, nearest.X, nearest.Y);
                const double maxDistFromLine = 26;
                if (distToLine > maxDistFromLine)
                {
                    double t = maxDistFromLine / distToLine;
                    candidate = new Point(
                        nearest.X + (candidate.X - nearest.X) * t,
                        nearest.Y + (candidate.Y - nearest.Y) * t);
                }

                _cableLabelDragPlaced.Instance.NameLabelDX = candidate.X - nameLbl.BaseX;
                _cableLabelDragPlaced.Instance.NameLabelDY = candidate.Y - nameLbl.BaseY;
                QueueCableDragRender(_cableLabelDragPlaced);
                return;
            }

            if (_cableLabelDragConnectorId == null) return;
            var lay = FindPortLayoutById(_cableLabelDragPlaced.Instance.Id, _cableLabelDragConnectorId);
            if (lay == null) return;

            double dx = _cableLabelDragOrigDX + (mouse.X - _cableLabelDragStartMouse.X);
            double dy = _cableLabelDragOrigDY + (mouse.Y - _cableLabelDragStartMouse.Y);

            var portLabel = _cableLabelDragPlaced.RenderedLabels.FirstOrDefault(
                item => !item.IsCableName &&
                        item.ConnectorId == _cableLabelDragConnectorId);
            if (portLabel == null) return;

            int lineCount = Math.Max(
                1, portLabel.Text.Count(ch => ch == '\n') + 1);
            double labelHeight = (portLabel.FontSize + 3) * lineCount;
            var labelCenter = new Point(
                portLabel.BaseX + dx,
                portLabel.BaseY + dy + labelHeight / 2);
            double vx = labelCenter.X - portLabel.AnchorX;
            double vy = labelCenter.Y - portLabel.AnchorY;
            double radius = Math.Sqrt(vx * vx + vy * vy);
            if (radius < 0.001)
            {
                vx = 0;
                vy = -1;
                radius = 1;
            }

            double limitedRadius = Math.Clamp(
                radius,
                CablePortLabelMinRadius,
                CablePortLabelMaxRadius);
            double ratio = limitedRadius / radius;
            labelCenter = new Point(
                portLabel.AnchorX + vx * ratio,
                portLabel.AnchorY + vy * ratio);
            dx = labelCenter.X - portLabel.BaseX;
            dy = labelCenter.Y - labelHeight / 2 - portLabel.BaseY;

            lay.LabelDX = dx;
            lay.LabelDY = dy;
            QueueCableDragRender(_cableLabelDragPlaced);
        }

        private void LabelOverlay_MouseUp(object sender, MouseButtonEventArgs e)
        {
            bool changed = _cableLabelDragPlaced != null;
            _labelOverlay?.ReleaseMouseCapture();
            _cableLabelDragPlaced = null;
            _cableLabelDragConnectorId = null;
            _cableLabelDragIsName = false;
            if (changed)
            {
                RenderAllCables();
                _isDirty = true;
            }
        }

        /// <summary>
        /// 스냅 Key("dev:instId:portId" / "cab:instId:connId")를 현재 위치·방향으로 변환.
        /// </summary>
    }
}
