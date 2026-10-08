using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ESDP.Models;

using Point = System.Windows.Point;
using ModelPoint = ESDP.Models.Point;

namespace ESDP
{
    public class SnapAnchor
    {
        public string Key { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public PortSide Facing { get; set; }
    }

    public class CableRenderCallbacks
    {
        public Func<string, SnapAnchor?>? ResolveAnchor;
        public Func<string, IReadOnlyList<Rect>>? GetObstacles;
        public Action<CablePortLayout, MouseButtonEventArgs>? OnPortDown;
        public Action<CablePortLayout, MouseButtonEventArgs>? OnPortDoubleClick;
        public Func<CablePortLayout, PortSide>? ResolvePortFacing;
        public Action<CablePortLayout>? OnPortAutoFacing;
        public Action<MouseButtonEventArgs>? OnLineDoubleClick;                    // 케이블 설정창 열기
        public Action<MouseButtonEventArgs>? OnLineLeftButtonDown;                 // Ctrl+드래그 전체 이동
        public Action<string, MouseButtonEventArgs>? OnLineRightClick;              // (legId, e) - 메뉴(꺾인점 초기화/분기삭제)
        public Action<string, int, MouseButtonEventArgs>? OnSegmentDown;            // (legId, segmentIndex, e) - 변(세그먼트) 평행이동 시작
        public Action<string, int, MouseButtonEventArgs>? OnCornerRemove;           // (legId, routePtsIndex, e) - 꺾인점 하나만 삭제
        public Action<CableBranch, MouseButtonEventArgs>? OnBranchDotDown;
        public Action? OnSelect; // 이 케이블을 선택 상태로 만듦 (선택된 케이블만 꺾인점/핸들을 보여줌)
        public Brush LineBrush = Brushes.Black;
        public Func<CablePortLayout, bool>? IsPortSelected;
    }

    /// <summary>
    /// 케이블을 "루트 직선(PortA-PortB) + 분기 트리" 형태로 그린다.
    /// 모든 포트는 항상 고정된 방향(FixedFacing)으로 나가며, 각 다리(leg)는
    /// 사용자가 자유롭게 추가한 꺾인점(Bends, 개수 제한 없음)을 순서대로 지나간다.
    /// </summary>
    public static class CableRenderer
    {
        public const double PortW = 3;
        public const double PortH = 11;
        public const double PortLabelFontSize = 9;
        public const double LineThickness = 2;
        // 결합된 장치 포트와 케이블 포트 사이의 시각적 간격.
        public const double ConnectGap = 0.75;
        public const double StubLength = 4;
        public const double DefaultSpan = 160;
        public const double CrossingRadius = 6; // 교차 반원 점프의 반지름
        private const double RoutePointTolerance = 1.0;
        private const double MinEditableSegmentLength = 14.0;

        private sealed class SegmentHandleInfo
        {
            public Point A { get; init; }
            public Point B { get; init; }
            public bool IsHorizontal { get; init; }
        }

        private sealed class CornerMarkerInfo
        {
            public Point Position { get; init; }
        }

        public static CableConnector? FindConnector(Cable cable, string connectorId)
            => cable.Connectors.FirstOrDefault(c => c.Id == connectorId);

        private static double Clamp01(double v) => Math.Max(0, Math.Min(1, v));

        private static Point ToPoint(ModelPoint p) => new Point(p.X, p.Y);

        public static (double X, double Y, PortSide? DeviceFacing) ResolveConnection(
            CablePortLayout lay, Func<string, SnapAnchor?>? resolveAnchor)
        {
            if (!string.IsNullOrEmpty(lay.ConnectedToPortKey) && resolveAnchor != null)
            {
                var a = resolveAnchor(lay.ConnectedToPortKey!);
                if (a != null)
                {
                    double gap = PortW + ConnectGap;
                    double x = a.X, y = a.Y;
                    switch (a.Facing)
                    {
                        case PortSide.Left: x = a.X - gap; break;
                        case PortSide.Right: x = a.X + gap; break;
                        case PortSide.Top: y = a.Y - gap; break;
                        case PortSide.Bottom: y = a.Y + gap; break;
                    }
                    lay.PortX = x; lay.PortY = y;
                    return (x, y, a.Facing);
                }
            }
            return (lay.PortX, lay.PortY, null);
        }

        /// <summary>
        /// start(항상 실제 지점)와 end(항상 실제 지점) 사이를, 각자의 고정방향(스텁)과
        /// 중간 꺾인점들을 거쳐 잇는 전체 경로를 계산한다. startFacing 이 null 이면
        /// (분기의 시작점=부모선 위 앵커) 스텁 없이 그 지점에서 바로 시작한다.
        /// </summary>
        public static List<Point> BuildFullRoute(Point start, PortSide? startFacing,
                                                   Point end, PortSide? endFacing, List<Point> bends)
        {
            Point stubStart = startFacing.HasValue ? Stub(start, startFacing.Value) : start;
            Point stubEnd = endFacing.HasValue ? Stub(end, endFacing.Value) : end;
            var middle = ChainThroughBends(stubStart, bends, stubEnd);
            Point visibleStart = startFacing.HasValue
                ? PortEdge(start, startFacing.Value)
                : start;
            Point visibleEnd = endFacing.HasValue
                ? PortEdge(end, endFacing.Value)
                : end;
            var route = new List<Point> { visibleStart };
            route.AddRange(middle);
            route.Add(visibleEnd);
            CollapseEndpointBacktracks(route, startFacing.HasValue, endFacing.HasValue);
            return route;
        }

        /// <summary>
        /// 통로 중심이 포트 캡 외곽과 일치하는 좁은 장치 사이에서는 자동
        /// 스텁이 캡 밖으로 나갔다가 같은 선을 되짚어 오는 180도 왕복을
        /// 만들 수 있다. 그 경우에만 스텁을 0길이 접속점으로 바꿔 선이
        /// 포트 외곽에서 곧바로 통로 방향으로 진행하게 한다.
        /// </summary>
        private static void CollapseEndpointBacktracks(
            List<Point> route, bool hasStartStub, bool hasEndStub)
        {
            static bool Same(Point first, Point second)
                => Len(first, second) < 0.01;

            static bool Reverses(Point first, Point middle, Point last)
            {
                double firstX = middle.X - first.X;
                double firstY = middle.Y - first.Y;
                double secondX = last.X - middle.X;
                double secondY = last.Y - middle.Y;
                double cross = firstX * secondY - firstY * secondX;
                double dot = firstX * secondX + firstY * secondY;
                return Math.Abs(cross) < 0.01 && dot < -0.0001;
            }

            if (hasStartStub && route.Count >= 3 &&
                Reverses(route[0], route[1], route[2]))
            {
                route[1] = route[0];
                if (route.Count >= 3 && Same(route[2], route[0]))
                    route.RemoveAt(2);
            }

            if (hasEndStub && route.Count >= 3)
            {
                int stubIndex = route.Count - 2;
                if (Reverses(
                        route[stubIndex - 1],
                        route[stubIndex],
                        route[stubIndex + 1]))
                {
                    route[stubIndex] = route[stubIndex + 1];
                    if (stubIndex - 1 >= 0 &&
                        Same(route[stubIndex - 1], route[stubIndex + 1]))
                        route.RemoveAt(stubIndex - 1);
                }
            }
        }
        /// <summary>
        /// 꺾인 경로(polyline)를 실제로 따라가며, 전체 길이의 t(0~1) 지점 좌표를 구한다.
        /// 양 끝을 잇는 직선이 아니라 "실제 그려진 선"을 따라가므로, 분기점이 항상 선 위에 놓인다.
        /// </summary>
        public static Point PointAlongPolyline(List<Point> pts, double t)
        {
            if (pts == null || pts.Count == 0) return new Point();
            if (pts.Count == 1) return pts[0];

            double total = 0;
            for (int i = 0; i < pts.Count - 1; i++) total += Len(pts[i], pts[i + 1]);
            if (total < 0.0001) return pts[0];

            double target = Clamp01(t) * total, acc = 0;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                double seg = Len(pts[i], pts[i + 1]);
                if (acc + seg >= target)
                {
                    double f = seg < 0.0001 ? 0 : (target - acc) / seg;
                    return new Point(pts[i].X + (pts[i + 1].X - pts[i].X) * f,
                                     pts[i].Y + (pts[i + 1].Y - pts[i].Y) * f);
                }
                acc += seg;
            }
            return pts[^1];
        }

        /// <summary>꺾인 경로 위에서 주어진 점과 가장 가까운 지점이 전체 길이의 몇 % 지점인지 구한다.</summary>
        public static double FractionAlongPolyline(List<Point> pts, Point p)
        {
            if (pts == null || pts.Count < 2) return 0.5;

            double total = 0;
            for (int i = 0; i < pts.Count - 1; i++) total += Len(pts[i], pts[i + 1]);
            if (total < 0.0001) return 0.5;

            double best = double.MaxValue, bestLen = 0, acc = 0;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var a = pts[i]; var b = pts[i + 1];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double lenSq = dx * dx + dy * dy;
                double f = lenSq < 0.0001 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq));
                double cx = a.X + f * dx, cy = a.Y + f * dy;
                double d = Len(new Point(cx, cy), p);
                if (d < best) { best = d; bestLen = acc + Len(a, new Point(cx, cy)); }
                acc += Len(a, b);
            }
            return Clamp01(bestLen / total);
        }

        private static double Len(Point a, Point b)
            => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        /// <summary>포트에서 고정 방향으로 짧게 빠져나온 지점(스텁)의 좌표. 외부에서 동일 계산이 필요할 때 사용.</summary>
        public static Point StubPoint(Point p, PortSide facing) => Stub(p, facing);

        /// <summary>포트 캡에서 실제 케이블 선이 시작되는 외곽면 좌표.</summary>
        public static Point PortEdgePoint(Point p, PortSide facing)
            => PortEdge(p, facing);

        private static Point Stub(Point p, PortSide facing) => facing switch
        {
            PortSide.Left => new Point(p.X - StubLength, p.Y),
            PortSide.Right => new Point(p.X + StubLength, p.Y),
            PortSide.Top => new Point(p.X, p.Y - StubLength),
            _ => new Point(p.X, p.Y + StubLength)
        };

        /// <summary>
        /// 선은 포트 중심이 아니라 케이블이 나가는 쪽의 캡 외곽면에서
        /// 시작한다. 포트 아래로 선이 지나가며 반대편으로 삐져나오는
        /// 시각적 관통 현상을 막는다.
        /// </summary>
        private static Point PortEdge(Point p, PortSide facing)
        {
            double offset = PortW / 2.0;
            return facing switch
            {
                PortSide.Left => new Point(p.X - offset, p.Y),
                PortSide.Right => new Point(p.X + offset, p.Y),
                PortSide.Top => new Point(p.X, p.Y - offset),
                _ => new Point(p.X, p.Y + offset)
            };
        }

        /// <summary>p1->p2 를 직교(가로/세로만)로 잇는다. 이미 일직선이면 바로, 아니면 모서리 하나 추가.</summary>
        private static List<Point> Jog(Point p1, Point p2)
        {
            if (Math.Abs(p1.X - p2.X) < 0.01 || Math.Abs(p1.Y - p2.Y) < 0.01)
                return new List<Point> { p2 };
            // p1(더 고정적인 쪽: 스텁 시작/앵커) 근처는 세로로 짧게 붙고,
            // p2(대개 더 자유롭게 움직이는 쪽: 목적지 포트) 쪽 구간이 길이를 흡수하도록 한다.
            // 이래야 목적지 포트를 옆으로 끌었을 때, 그 포트 바로 앞 구간이 늘어난다.
            return new List<Point> { new Point(p1.X, p2.Y), p2 };
        }

        /// <summary>stubStart -> bends... -> stubEnd 를 전부 직교로 이어 하나의 경로로 만든다.</summary>
        private static List<Point> ChainThroughBends(Point stubStart, List<Point> bends, Point stubEnd)
        {
            var pts = new List<Point> { stubStart };
            var current = stubStart;
            foreach (var b in bends)
            {
                pts.AddRange(Jog(current, b));
                current = b;
            }
            pts.AddRange(Jog(current, stubEnd));

            var result = new List<Point>();
            foreach (var p in pts)
                if (result.Count == 0 || Math.Abs(result[^1].X - p.X) > 0.01 || Math.Abs(result[^1].Y - p.Y) > 0.01)
                    result.Add(p);
            return result;
        }

        /// <summary>나중에 겹침을 피해 한꺼번에 배치하기 위한 라벨 정보</summary>
        public class PendingLabel
        {
            public double X, Y;       // 실제 표시 위치 (수동 오프셋 반영됨)
            public double BaseX, BaseY; // 오프셋 적용 전 기준 위치 (드래그 계산용)
            public double AnchorX, AnchorY; // 케이블 포트 중심(포트명 이동 범위 기준)
            public string Text = "";
            public string? ConnectorId; // 포트 이름표면 그 포트의 Id, 케이블 이름표면 null
            public bool IsCableName;
            public double FontSize = PortLabelFontSize;
            public string ColorHex = "#000000";
        }

        private static void DrawPortMark(Canvas overlay, double x, double y, PortSide facing,
                                          bool isConnected, CableConnector? connector, CablePortLayout lay,
                                          CableRenderCallbacks cb, List<PendingLabel> labels)
        {
            string name = connector?.PortName ?? "?";
            string labelText = connector?.ShowConnType == true &&
                               !string.IsNullOrWhiteSpace(connector.ConnType)
                ? $"{name}\n{connector.ConnType}"
                : name;
            double labelFontSize = connector?.LabelFontSize > 0
                ? connector.LabelFontSize
                : PortLabelFontSize;
            bool horiz = facing == PortSide.Left || facing == PortSide.Right;
            double w = horiz ? PortW : PortH;
            double h = horiz ? PortH : PortW;

            var rect = new Rectangle
            {
                Width = w, Height = h,
                Fill = Brushes.Black,
                Stroke = Brushes.Transparent, StrokeThickness = 1,
                Cursor = Cursors.Hand
            };
            if (!string.IsNullOrWhiteSpace(connector?.Description))
            {
                rect.ToolTip = connector.Description.Trim();
                ToolTipService.SetInitialShowDelay(rect, 180);
                rect.ToolTipOpening += (_, e) =>
                {
                    if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0)
                        e.Handled = true;
                };
            }
            Canvas.SetLeft(rect, x - w / 2);
            Canvas.SetTop(rect, y - h / 2);
            rect.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) { cb.OnPortDoubleClick?.Invoke(lay, e); return; }
                cb.OnSelect?.Invoke();
                cb.OnPortDown?.Invoke(lay, e);
            };
            rect.MouseRightButtonDown += (s, e) =>
            {
                var menu = new ContextMenu();
                if (cb.OnPortAutoFacing != null)
                {
                    var mi = new System.Windows.Controls.MenuItem { Header = "케이블 방향 자동 맞춤" };
                    mi.Click += (_, __) => cb.OnPortAutoFacing(lay);
                    menu.Items.Add(mi);
                }
                rect.ContextMenu = menu;
                menu.IsOpen = true;
                e.Handled = true;
            };
            overlay.Children.Add(rect);

            if (cb.IsPortSelected?.Invoke(lay) == true)
            {
                var selection = new Rectangle
                {
                    Width = w + 7,
                    Height = h + 7,
                    Stroke = AppThemeManager.GetBrush("AppAccentBrush"),
                    StrokeThickness = 1.5,
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(selection, x - selection.Width / 2);
                Canvas.SetTop(selection, y - selection.Height / 2);
                overlay.Children.Add(selection);
            }

            double approxLabelWidth = labelText.Split('\n')
                .Select(line => Math.Max(1, line.Length) * labelFontSize * 0.62)
                .DefaultIfEmpty(labelFontSize)
                .Max();
            double labelX = x;
            double labelY = y - Math.Max(h, PortH) / 2 - labelFontSize - 2;
            const double labelGap = 3;

            // 포트 이름은 포트가 케이블 쪽으로 향하는 방향의 바깥에 둔다.
            // 좌·우 포트는 선 위쪽을 유지하면서 각각 왼쪽/오른쪽으로 물리고,
            // 위·아래 포트는 해당 방향으로 곧바로 물린다.
            switch (facing)
            {
                case PortSide.Left:
                    labelX = x - w / 2 - labelGap - approxLabelWidth / 2;
                    break;
                case PortSide.Right:
                    labelX = x + w / 2 + labelGap + approxLabelWidth / 2;
                    break;
                case PortSide.Top:
                    labelY = y - h / 2 - labelGap - labelFontSize - 2;
                    break;
                case PortSide.Bottom:
                    labelY = y + h / 2 + labelGap;
                    break;
            }

            if (connector?.ShowLabel != false)
            {
                labels.Add(new PendingLabel
                {
                    X = labelX + lay.LabelDX, Y = labelY + lay.LabelDY,
                    BaseX = labelX, BaseY = labelY,
                    AnchorX = x, AnchorY = y,
                    Text = labelText, ConnectorId = lay.ConnectorId,
                    FontSize = labelFontSize,
                    ColorHex = connector?.LabelColorHex ?? "#000000"
                });
            }
        }

        /// <summary>
        /// 선을 그리고, 선택된 상태면 각 꺾인 지점(자동으로 생긴 것 포함)을 점으로 보여주고
        /// 각 변(세그먼트) 중앙에 평행이동용 핸들을 놓는다. 렌더된 점 목록을 반환.
        /// </summary>
        private static List<Point> DrawLegLine(Canvas overlay, List<Point> routePts, string legId,
                                                CableRenderCallbacks cb, bool isSelected,
                                                bool hasStartStub, bool hasEndStub)
        {
            if (isSelected)
            {
                // 실제 케이블의 검은색은 유지하고, 뒤쪽에만 아주 얇은 반투명 광택을 깐다.
                // DropShadowEffect보다 가볍고 확대/축소해도 선을 정확하게 따라간다.
                var selectionGlow = new Polyline
                {
                    Stroke = AppThemeManager.GetBrush("AppAccentBrush"),
                    StrokeThickness = LineThickness + 3,
                    Opacity = 0.28,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false,
                    Points = new PointCollection(routePts)
                };
                overlay.Children.Add(selectionGlow);
            }

            var poly = new Polyline
            {
                Stroke = cb.LineBrush, StrokeThickness = LineThickness,
                Points = new PointCollection(routePts)
            };
            overlay.Children.Add(poly);

            var hit = new Polyline
            {
                Stroke = Brushes.Transparent, StrokeThickness = 10,
                Points = new PointCollection(routePts)
            };
            hit.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    cb.OnLineDoubleClick?.Invoke(e);
                    e.Handled = true;
                    return;
                }
                cb.OnLineLeftButtonDown?.Invoke(e);
                cb.OnSelect?.Invoke();
                e.Handled = true; // 안 막으면 이 클릭이 위로 전달돼 캔버스의 "빈곳 클릭=선택해제"가 곧바로 실행됨
            };
            hit.MouseRightButtonDown += (s, e) => { cb.OnSelect?.Invoke(); cb.OnLineRightClick?.Invoke(legId, e); e.Handled = true; };
            overlay.Children.Add(hit);

            if (isSelected)
            {
                // 포트 바로 옆의 "스텁" 구간(고정된 최소 돌출 길이)은 사용자가 만든 꺾임이 아니라
                // 항상 자동으로 생기는 고정 구간이므로, 그 경계점/세그먼트는 편집 표시에서 뺀다.
                // routePts[0]=시작점, (스텁 있으면) routePts[1]=스텁점, ... , routePts[^1]=끝점.
                int cornerStart = hasStartStub ? 2 : 1;
                int cornerEnd = hasEndStub ? routePts.Count - 3 : routePts.Count - 2;

                for (int i = cornerStart; i <= cornerEnd; i++)
                {
                    var dot = new Ellipse
                    {
                        Width = 5, Height = 5, Fill = Brushes.Orange,
                        Stroke = Brushes.DarkOrange, StrokeThickness = 1,
                        Cursor = Cursors.Hand,
                        ToolTip = "우클릭: 이 꺾인점만 삭제",
                        Tag = new CornerMarkerInfo { Position = routePts[i] }
                    };
                    Canvas.SetLeft(dot, routePts[i].X - 2.5);
                    Canvas.SetTop(dot, routePts[i].Y - 2.5);
                    int cornerIdx = i;
                    dot.MouseRightButtonDown += (s, e) =>
                    {
                        cb.OnCornerRemove?.Invoke(legId, cornerIdx, e);
                        e.Handled = true;
                    };
                    overlay.Children.Add(dot);
                }

                // 각 변(세그먼트) 중앙에 평행이동 핸들 — 얇고 짧게. 스텁 구간(맨 앞/맨 뒤)은 제외.
                int segStart = hasStartStub ? 1 : 0;
                int segEnd = hasEndStub ? routePts.Count - 3 : routePts.Count - 2;

                for (int i = segStart; i <= segEnd; i++)
                {
                    var p1 = routePts[i]; var p2 = routePts[i + 1];
                    bool horiz = Math.Abs(p1.Y - p2.Y) < 0.01;
                    bool vert = Math.Abs(p1.X - p2.X) < 0.01;
                    if (!horiz && !vert) continue;
                    if (Len(p1, p2) < MinEditableSegmentLength) continue;

                    double midX = (p1.X + p2.X) / 2, midY = (p1.Y + p2.Y) / 2;

                    var handle = new Rectangle
                    {
                        Width = horiz ? 10 : 4,
                        Height = horiz ? 4 : 10,
                        Fill = AppThemeManager.GetBrush("AppAccentBrush"),
                        Opacity = 0.6,
                        Cursor = horiz ? Cursors.SizeNS : Cursors.SizeWE,
                        ToolTip = "선택한 선분만 평행 이동",
                        Tag = new SegmentHandleInfo
                        {
                            A = p1,
                            B = p2,
                            IsHorizontal = horiz
                        }
                    };
                    Canvas.SetLeft(handle, midX - handle.Width / 2);
                    Canvas.SetTop(handle, midY - handle.Height / 2);
                    int segIdx = i;
                    handle.MouseLeftButtonDown += (s, e) =>
                    {
                        cb.OnSegmentDown?.Invoke(legId, segIdx, e);
                        e.Handled = true;
                    };
                    overlay.Children.Add(handle);
                }
            }

            return routePts;
        }

        /// <summary>
        /// 케이블 전체를 그리고, 이후 교차점(반원 점프) 계산에 쓸 모든 선분 목록을 반환한다.
        /// </summary>
        /// <summary>
        /// 저장된 데이터에 어떤 경위로든 거의 겹치는 점이 남아있더라도, 화면에는 절대
        /// 두 점으로 안 보이게 만드는 최종 안전장치. 그리기 직전에 항상 거친다.
        /// (저장 단계에서 막는 방식은 계속 새로운 경로로 재발했기 때문에,
        ///  마지막 관문에서 무조건 걸러내는 쪽이 훨씬 확실하다)
        ///
        /// 포트↔스텁 사이의 정상 간격(StubLength=4px)은 절대 지우면 안 되므로 그 구간은 보호하고,
        /// 그 외 구간은 저장 단계(MaterializeBends)와 같은 화면 오차 수준으로만 병합한다.
        /// 허용치가 크면 선분 핸들을 조금 움직였을 때 사용자가 만든 점까지 자동 스텁으로
        /// 오인해 지워지므로, 1px 이내의 사실상 동일한 점만 합친다.
        /// 기준이 서로 다르면 "저장할 땐 안 지워지는데 그릴 땐 다르게 보이는" 불일치가 생기므로 맞춘다.
        /// </summary>
        public static List<Point> CollapseNearDuplicates(List<Point> pts, bool hasStartStub, bool hasEndStub,
                                                           double eps = RoutePointTolerance)
        {
            if (pts.Count <= 2) return pts;

            var result = new List<Point> { pts[0] };
            for (int i = 1; i < pts.Count; i++)
            {
                var prev = result[^1];
                var cur = pts[i];
                bool isLast = i == pts.Count - 1;
                bool isProtectedStubGap =
                    (hasStartStub && i == 1) ||
                    (hasEndStub && i == pts.Count - 2);

                if (!isLast && !isProtectedStubGap
                    && Math.Abs(cur.X - prev.X) < eps && Math.Abs(cur.Y - prev.Y) < eps)
                {
                    // 이 점을 지워도 되는지: 지운 뒤 prev 가 "그 다음 점"과 직교(가로 또는 세로)로
                    // 안전하게 이어지는 경우에만 지운다. 그렇지 않으면 대각선이 생겨버리므로 유지한다.
                    var next = pts[i + 1]; // isLast 가 아니므로 i+1 은 항상 유효
                    bool staysOrthogonal = Math.Abs(prev.X - next.X) < 0.5 || Math.Abs(prev.Y - next.Y) < 0.5;
                    if (staysOrthogonal) continue;
                }

                result.Add(cur);
            }

            // Segment dragging can leave a very short orthogonal dogleg between
            // two parallel runs. It is almost invisible but produces a cluster
            // of corner markers. Collapse it at the final rendering boundary as
            // well, while preserving endpoint/stub points.
            const double shortDoglegGap = 12.0;
            bool IsProtected(int index)
            {
                if (index == 0 || index == result.Count - 1) return true;
                if (hasStartStub && index == 1) return true;
                if (hasEndStub && index == result.Count - 2) return true;
                return false;
            }

            static bool RangesOverlap(
                double firstA, double firstB,
                double secondA, double secondB)
            {
                double left = Math.Max(
                    Math.Min(firstA, firstB),
                    Math.Min(secondA, secondB));
                double right = Math.Min(
                    Math.Max(firstA, firstB),
                    Math.Max(secondA, secondB));
                return left <= right + 0.01;
            }

            bool doglegChanged = true;
            while (doglegChanged && result.Count >= 4)
            {
                doglegChanged = false;
                for (int index = 0; index <= result.Count - 4; index++)
                {
                    int bIndex = index + 1;
                    int cIndex = index + 2;
                    int dIndex = index + 3;
                    var a = result[index];
                    var b = result[bIndex];
                    var c = result[cIndex];
                    var d = result[dIndex];

                    bool horizontalDogleg =
                        Math.Abs(a.Y - b.Y) < 0.01 &&
                        Math.Abs(b.X - c.X) < 0.01 &&
                        Math.Abs(c.Y - d.Y) < 0.01 &&
                        Math.Abs(b.Y - c.Y) <= shortDoglegGap &&
                        RangesOverlap(a.X, b.X, c.X, d.X);
                    bool verticalDogleg =
                        Math.Abs(a.X - b.X) < 0.01 &&
                        Math.Abs(b.Y - c.Y) < 0.01 &&
                        Math.Abs(c.X - d.X) < 0.01 &&
                        Math.Abs(b.X - c.X) <= shortDoglegGap &&
                        RangesOverlap(a.Y, b.Y, c.Y, d.Y);

                    if (!horizontalDogleg && !verticalDogleg) continue;
                    if (IsProtected(bIndex) || IsProtected(cIndex)) continue;

                    bool aProtected = IsProtected(index);
                    bool dProtected = IsProtected(dIndex);
                    if (aProtected && dProtected) continue;

                    if (horizontalDogleg)
                    {
                        if (aProtected)
                            result[dIndex] = new Point(d.X, a.Y);
                        else
                            result[index] = new Point(a.X, d.Y);
                    }
                    else
                    {
                        if (aProtected)
                            result[dIndex] = new Point(a.X, d.Y);
                        else
                            result[index] = new Point(d.X, a.Y);
                    }

                    result.RemoveAt(cIndex);
                    result.RemoveAt(bIndex);
                    doglegChanged = true;
                    break;
                }
            }

            // 한 번 더: 일직선이 되어버려 더 이상 꺾이는 의미가 없는 가운데 점 제거
            bool changed = true;
            while (changed && result.Count > 2)
            {
                changed = false;
                for (int i = 1; i < result.Count - 1; i++)
                {
                    // 포트 바로 옆의 자동 스텁점은 선과 일직선이어도 반드시 남겨야 한다.
                    // DrawLegLine 은 이 두 점을 기준으로 실제 편집 가능한 꼭짓점과 선분의
                    // 시작/끝 인덱스를 계산한다. 여기서 없애면 양 끝쪽 면의 핸들과
                    // 첫/마지막 꺾인점이 함께 표시되지 않는다.
                    bool isStartStub = hasStartStub && i == 1;
                    bool isEndStub = hasEndStub && i == result.Count - 2;
                    if (isStartStub || isEndStub) continue;

                    var prev = result[i - 1]; var cur = result[i]; var next = result[i + 1];
                    bool sameVertical = Math.Abs(prev.X - cur.X) < 0.01 && Math.Abs(cur.X - next.X) < 0.01;
                    bool sameHorizontal = Math.Abs(prev.Y - cur.Y) < 0.01 && Math.Abs(cur.Y - next.Y) < 0.01;
                    if (sameVertical || sameHorizontal) { result.RemoveAt(i); changed = true; break; }
                }
            }
            return result;
        }

        /// <summary>
        /// 기존 직교 경로의 모양은 유지하면서, 장치 사각형 내부를 통과하는 선분만
        /// 사각형 경계 바깥으로 짧게 우회시킨다.
        /// </summary>
        public static List<Point> AvoidObstacles(
            List<Point> route,
            IReadOnlyList<Rect>? obstacles,
            bool hasStartStub,
            bool hasEndStub,
            bool? requiredFirstHorizontal = null)
        {
            if (route.Count < 2 || obstacles == null || obstacles.Count == 0)
                return route;

            bool hasCollision = false;
            for (int i = 0; i < route.Count - 1 && !hasCollision; i++)
                hasCollision = obstacles.Any(obstacle =>
                    SegmentCrossesRectInterior(route[i], route[i + 1], obstacle));
            if (!hasCollision) return route;

            int coreStartIndex = hasStartStub ? 1 : 0;
            int coreEndIndex = hasEndStub ? route.Count - 2 : route.Count - 1;
            if (coreStartIndex >= coreEndIndex)
                return AvoidObstaclesLocally(route, obstacles);

            // 분기는 부모 선과 수직으로 출발해야 한다. 일반 장애물 회피가
            // 경로 전체를 다시 만들면서 이 첫 방향을 잃으면 분기점 바로 뒤에
            // 부모와 같은 축의 꺾임점이 다시 생기므로 시작 방향을 함께 고정한다.
            var cleanCore = requiredFirstHorizontal.HasValue
                ? FindBalancedOrthogonalRoute(
                    route[coreStartIndex], route[coreEndIndex], obstacles,
                    Array.Empty<(Point A, Point B)>(),
                    requiredFirstHorizontal: requiredFirstHorizontal,
                    prioritizeBends: true)
                : FindCleanOrthogonalRoute(
                    route[coreStartIndex], route[coreEndIndex], obstacles);
            if (cleanCore != null)
            {
                var cleanRoute = route.Take(coreStartIndex).ToList();
                cleanRoute.AddRange(cleanCore);
                cleanRoute.AddRange(route.Skip(coreEndIndex + 1));
                cleanRoute = RemoveAdjacentDuplicatePoints(cleanRoute);

                // 코어 바깥의 시작·끝 접속 구간이 다른 포트 위를 통과하는
                // 경우도 남김없이 처리한다. 이때 가까운 경계로만 ㄹ자 우회한다.
                bool residualCollision = false;
                for (int i = 0;
                     i < cleanRoute.Count - 1 && !residualCollision;
                     i++)
                {
                    residualCollision = obstacles.Any(obstacle =>
                        SegmentCrossesRectInterior(
                            cleanRoute[i], cleanRoute[i + 1], obstacle));
                }
                return residualCollision
                    ? AvoidObstaclesLocally(cleanRoute, obstacles)
                    : cleanRoute;
            }

            // 아주 복잡하게 사각형이 맞물려 단순 후보로 빠져나갈 수 없는 경우에만
            // 기존의 구간별 우회를 최후 수단으로 사용한다.
            return AvoidObstaclesLocally(route, obstacles);
        }

        public static bool? GetFirstSegmentHorizontal(
            IReadOnlyList<Point>? route)
        {
            if (route == null || route.Count < 2) return null;
            for (int index = 1; index < route.Count; index++)
            {
                var start = route[0];
                var end = route[index];
                if (Len(start, end) < 0.01) continue;
                return Math.Abs(end.Y - start.Y) <
                       Math.Abs(end.X - start.X);
            }
            return null;
        }

        /// <summary>
        /// 장치는 반드시 피하되 기존 케이블은 소프트 제약으로만 고려하는 전체 정렬용 라우터.
        /// 겹침은 강하게 피하고, 짧은 교차는 지나친 외곽 우회보다 낮은 비용으로 허용한다.
        /// 기존 평행선의 양옆에는 laneGap만큼 떨어진 후보 차선을 만든다.
        /// </summary>
        public static List<Point> FindBalancedOrthogonalRoute(
            Point start,
            Point end,
            IReadOnlyList<Rect> obstacles,
            IReadOnlyList<(Point A, Point B)> occupiedSegments,
            bool? requiredFirstHorizontal = null,
            double laneGap = 8.0,
            bool prioritizeBends = false,
            PortSide? requiredStartDirection = null,
            PortSide? requiredEndDirection = null,
            double gridSize = 20.0)
        {
            var candidates = new List<List<Point>>();
            void Add(params Point[] points) => candidates.Add(points.ToList());

            double LaneCenter(double value) =>
                Math.Round((value - gridSize / 2) / gridSize,
                    MidpointRounding.AwayFromZero) * gridSize + gridSize / 2;

            IEnumerable<double> LaneCenters(
                IEnumerable<double> sourceValues)
            {
                var values = sourceValues.ToList();
                if (values.Count == 0) yield break;
                double minimum = values.Min() - gridSize * 2;
                double maximum = values.Max() + gridSize * 2;
                double current = Math.Floor(
                    (minimum - gridSize / 2) / gridSize) * gridSize +
                    gridSize / 2;
                for (; current <= maximum + 0.01; current += gridSize)
                    yield return current;
            }

            bool IsLaneCenter(double value)
                => Math.Abs(value - LaneCenter(value)) < 0.05;

            static IEnumerable<double> GapCenters(IEnumerable<double> values)
            {
                var ordered = values.Distinct().OrderBy(value => value).ToList();
                for (int index = 0; index < ordered.Count - 1; index++)
                {
                    double gap = ordered[index + 1] - ordered[index];
                    if (gap >= 2.0)
                        yield return (ordered[index] + ordered[index + 1]) / 2;
                }
            }

            static bool ReversesFacing(
                Point from, Point toward, PortSide facing)
                => facing switch
                {
                    PortSide.Left => toward.X > from.X + 0.01,
                    PortSide.Right => toward.X < from.X - 0.01,
                    PortSide.Top => toward.Y > from.Y + 0.01,
                    _ => toward.Y < from.Y - 0.01
                };

            if (Math.Abs(start.X - end.X) < 0.01 ||
                Math.Abs(start.Y - end.Y) < 0.01)
                Add(start, end);
            Add(start, new Point(start.X, end.Y), end);
            Add(start, new Point(end.X, start.Y), end);

            var obstacleYs = obstacles
                .SelectMany(rect => new[] { rect.Top, rect.Bottom })
                .ToList();
            var obstacleXs = obstacles
                .SelectMany(rect => new[] { rect.Left, rect.Right })
                .ToList();

            var allYValues = new[] { start.Y, end.Y }
                .Concat(obstacleYs)
                .Concat(occupiedSegments.SelectMany(segment =>
                    new[] { segment.A.Y, segment.B.Y }))
                .ToList();
            var allXValues = new[] { start.X, end.X }
                .Concat(obstacleXs)
                .Concat(occupiedSegments.SelectMany(segment =>
                    new[] { segment.A.X, segment.B.X }))
                .ToList();

            // 같은 길이로 갈 수 있는 열린 통로라면 경계에 붙는 선보다
            // 양쪽 경계의 정중앙 차선을 먼저 후보로 둔다.
            var corridorYs = LaneCenters(allYValues)
                .Concat(GapCenters(obstacleYs))
                .Concat(obstacleYs)
                .Concat(occupiedSegments
                    .Where(segment => Math.Abs(segment.A.Y - segment.B.Y) < 0.01)
                    .SelectMany(segment => new[]
                    {
                        segment.A.Y - laneGap,
                        segment.A.Y + laneGap
                    }))
                .Append((start.Y + end.Y) / 2)
                .Distinct()
                .ToList();

            var corridorXs = LaneCenters(allXValues)
                .Concat(GapCenters(obstacleXs))
                .Concat(obstacleXs)
                .Concat(occupiedSegments
                    .Where(segment => Math.Abs(segment.A.X - segment.B.X) < 0.01)
                    .SelectMany(segment => new[]
                    {
                        segment.A.X - laneGap,
                        segment.A.X + laneGap
                    }))
                .Append((start.X + end.X) / 2)
                .Distinct()
                .ToList();

            foreach (double y in corridorYs)
                Add(start, new Point(start.X, y), new Point(end.X, y), end);
            foreach (double x in corridorXs)
                Add(start, new Point(x, start.Y), new Point(x, end.Y), end);

            // 단순 L/ㄷ자 후보만으로는 여러 장치 사이를 통과할 때 구간별 우회가 연쇄되어
            // 짧은 왕복과 사각 루프가 생긴다. 장치 경계와 기존 케이블 레인을 직교 그래프로
            // 구성해 경로 전체를 한 번에 탐색한 후보도 함께 비교한다.
            var gridRoute = FindOrthogonalGridRoute(
                start, end, obstacles, occupiedSegments,
                requiredFirstHorizontal, laneGap, prioritizeBends,
                gridSize);
            if (gridRoute != null)
                candidates.Add(gridRoute);

            double directDistance = Math.Abs(start.X - end.X) + Math.Abs(start.Y - end.Y);
            List<Point>? best = null;
            double bestScore = double.MaxValue;

            foreach (var candidate in candidates)
            {
                var normalized = RemoveRedundantRoutePoints(candidate);
                if (normalized.Count < 2) continue;

                var first = normalized[1];
                bool firstHorizontal = Math.Abs(first.Y - start.Y) < 0.01;
                if (requiredFirstHorizontal.HasValue &&
                    firstHorizontal != requiredFirstHorizontal.Value)
                    continue;
                if (requiredStartDirection.HasValue &&
                    ReversesFacing(start, first, requiredStartDirection.Value))
                    continue;
                if (requiredEndDirection.HasValue &&
                    ReversesFacing(
                        end, normalized[^2], requiredEndDirection.Value))
                    continue;

                bool clear = true;
                for (int i = 0; i < normalized.Count - 1 && clear; i++)
                {
                    if (obstacles.Any(obstacle =>
                        SegmentCrossesRectInterior(
                            normalized[i], normalized[i + 1], obstacle)))
                        clear = false;
                }
                if (!clear) continue;

                double length = 0;
                double offGridPenalty = 0;
                for (int i = 0; i < normalized.Count - 1; i++)
                {
                    double segmentLength = Len(
                        normalized[i], normalized[i + 1]);
                    length += segmentLength;
                    bool horizontal = Math.Abs(
                        normalized[i].Y - normalized[i + 1].Y) < 0.01;
                    double axis = horizontal
                        ? normalized[i].Y
                        : normalized[i].X;
                    bool shortEndpointApproach =
                        (i == 0 || i == normalized.Count - 2) &&
                        segmentLength <= gridSize + 0.01;
                    if (!IsLaneCenter(axis) && !shortEndpointApproach)
                        offGridPenalty += 100_000_000d +
                                          segmentLength * 10_000d;
                }

                int bends = Math.Max(0, normalized.Count - 2);
                double lengthInGridCells = length / gridSize;
                // 개별 케이블 정리는 다른 선을 잠깐 교차하지 않기 위해 긴 계단형
                // 우회를 만드는 것보다 꺾임 수가 적은 경로를 절대적으로 우선한다.
                double score = prioritizeBends
                    ? bends * 1_000_000_000_000d +
                      lengthInGridCells * 20_000_000d
                    : lengthInGridCells * 20 + bends * 42;
                score += offGridPenalty;

                // 최단거리의 1.45배를 넘어가는 외곽 우회는 급격히 불리해진다.
                double freeDetour = Math.Max(24, directDistance * 0.45);
                double excessiveDetour = Math.Max(0, length - directDistance - freeDetour);
                score += excessiveDetour * 5;

                for (int i = 0; i < normalized.Count - 1; i++)
                {
                    foreach (var occupied in occupiedSegments)
                        score += ScoreCableInteraction(
                            normalized[i], normalized[i + 1],
                            occupied.A, occupied.B, laneGap);
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    best = normalized;
                }
            }

            if (best != null) return best;

            // 매우 막힌 배치의 최후 수단. 시작 방향 조건에 맞는 L 경로에서
            // 기존 장치 회피 로직을 적용하되 케이블 때문에 화면 끝까지 돌지는 않는다.
            var fallback = requiredFirstHorizontal == true
                ? new List<Point> { start, new Point(end.X, start.Y), end }
                : new List<Point> { start, new Point(start.X, end.Y), end };
            fallback = AvoidObstaclesLocally(
                RemoveRedundantRoutePoints(fallback), obstacles);

            // 극단적으로 막힌 배치에서도 포트 방향 역전만큼은 허용하지
            // 않는다. 필요한 경우 바깥 방향 보호점을 한 칸 더 두고 그 뒤를
            // 장애물 회피 경로에 맡긴다.
            if (requiredStartDirection.HasValue && fallback.Count >= 2 &&
                ReversesFacing(
                    fallback[0], fallback[1], requiredStartDirection.Value))
            {
                fallback.Insert(1, MoveInFacingDirection(
                    fallback[0], requiredStartDirection.Value,
                    Math.Max(20, laneGap * 2)));
            }
            if (requiredEndDirection.HasValue && fallback.Count >= 2 &&
                ReversesFacing(
                    fallback[^1], fallback[^2], requiredEndDirection.Value))
            {
                fallback.Insert(fallback.Count - 1, MoveInFacingDirection(
                    fallback[^1], requiredEndDirection.Value,
                    Math.Max(20, laneGap * 2)));
            }
            return RemoveRedundantRoutePoints(fallback);
        }

        private static Point MoveInFacingDirection(
            Point point, PortSide facing, double distance)
            => facing switch
            {
                PortSide.Left => new Point(point.X - distance, point.Y),
                PortSide.Right => new Point(point.X + distance, point.Y),
                PortSide.Top => new Point(point.X, point.Y - distance),
                _ => new Point(point.X, point.Y + distance)
            };

        private static List<Point>? FindOrthogonalGridRoute(
            Point start,
            Point end,
            IReadOnlyList<Rect> obstacles,
            IReadOnlyList<(Point A, Point B)> occupiedSegments,
            bool? requiredFirstHorizontal,
            double laneGap,
            bool prioritizeBends,
            double gridSize)
        {
            static IEnumerable<double> UniqueSorted(IEnumerable<double> values) =>
                values.Select(value => Math.Round(value, 3)).Distinct().OrderBy(value => value);

            static IEnumerable<double> GridCenters(
                double minimum, double maximum, double size)
            {
                double current = Math.Floor(
                    (minimum - size / 2) / size) * size + size / 2;
                for (; current <= maximum + 0.01; current += size)
                    yield return current;
            }

            var allXs = new[] { start.X, end.X }
                .Concat(obstacles.SelectMany(rect =>
                    new[] { rect.Left, rect.Right }))
                .Concat(occupiedSegments.SelectMany(segment =>
                    new[] { segment.A.X, segment.B.X }))
                .ToList();
            var allYs = new[] { start.Y, end.Y }
                .Concat(obstacles.SelectMany(rect =>
                    new[] { rect.Top, rect.Bottom }))
                .Concat(occupiedSegments.SelectMany(segment =>
                    new[] { segment.A.Y, segment.B.Y }))
                .ToList();
            double minX = allXs.Min() - gridSize * 2;
            double maxX = allXs.Max() + gridSize * 2;
            double minY = allYs.Min() - gridSize * 2;
            double maxY = allYs.Max() + gridSize * 2;

            // 정상 그래프 축은 격자 칸의 중앙선이다. start/end 축은 포트에서
            // 가장 가까운 중앙선으로 진입하는 짧은 접속 구간에만 사용된다.
            var xs = UniqueSorted(GridCenters(minX, maxX, gridSize)
                    .Append(start.X).Append(end.X))
                .ToList();
            var ys = UniqueSorted(GridCenters(minY, maxY, gridSize)
                    .Append(start.Y).Append(end.Y))
                .ToList();

            // 지나치게 큰 도면에서도 그래프가 폭증하지 않도록 실제 경로 후보 축만 제한한다.
            if (xs.Count * ys.Count > 16000)
                return null;

            bool PointIsUsable(Point point) => !obstacles.Any(rect =>
                point.X > rect.Left + 0.01 && point.X < rect.Right - 0.01 &&
                point.Y > rect.Top + 0.01 && point.Y < rect.Bottom - 0.01);
            bool EdgeIsClear(Point a, Point b) => !obstacles.Any(rect =>
                SegmentCrossesRectInterior(a, b, rect));

            var points = new List<Point>();
            var pointIndex = new Dictionary<(double X, double Y), int>();
            foreach (double y in ys)
            foreach (double x in xs)
            {
                var point = new Point(x, y);
                if (!PointIsUsable(point)) continue;
                pointIndex[(x, y)] = points.Count;
                points.Add(point);
            }

            if (!pointIndex.TryGetValue((Math.Round(start.X, 3), Math.Round(start.Y, 3)), out int startIndex) ||
                !pointIndex.TryGetValue((Math.Round(end.X, 3), Math.Round(end.Y, 3)), out int endIndex))
                return null;

            var neighbours = Enumerable.Range(0, points.Count)
                .Select(_ => new List<int>()).ToArray();
            void ConnectAxisGroups(IEnumerable<IGrouping<double, int>> groups, bool horizontal)
            {
                foreach (var group in groups)
                {
                    var ordered = group
                        .OrderBy(index => horizontal ? points[index].X : points[index].Y)
                        .ToList();
                    for (int i = 0; i < ordered.Count - 1; i++)
                    {
                        int a = ordered[i];
                        int b = ordered[i + 1];
                        if (!EdgeIsClear(points[a], points[b])) continue;
                        neighbours[a].Add(b);
                        neighbours[b].Add(a);
                    }
                }
            }
            ConnectAxisGroups(
                Enumerable.Range(0, points.Count).GroupBy(index => points[index].Y),
                horizontal: true);
            ConnectAxisGroups(
                Enumerable.Range(0, points.Count).GroupBy(index => points[index].X),
                horizontal: false);

            // 방향 0=시작, 1=가로, 2=세로. 방향을 상태에 포함해야 꺾임 수를 정확히 평가한다.
            var queue = new PriorityQueue<(int Node, int Direction), double>();
            var distance = new Dictionary<(int Node, int Direction), double>();
            var previous = new Dictionary<(int Node, int Direction), (int Node, int Direction)>();
            var startState = (Node: startIndex, Direction: 0);
            distance[startState] = 0;
            queue.Enqueue(startState, 0);
            (int Node, int Direction)? bestEnd = null;

            while (queue.TryDequeue(out var state, out double queuedCost))
            {
                if (!distance.TryGetValue(state, out double knownCost) ||
                    queuedCost > knownCost + 0.001)
                    continue;
                if (state.Node == endIndex)
                {
                    bestEnd = state;
                    break;
                }

                foreach (int nextNode in neighbours[state.Node])
                {
                    Point a = points[state.Node];
                    Point b = points[nextNode];
                    int nextDirection = Math.Abs(a.Y - b.Y) < 0.01 ? 1 : 2;
                    if (state.Direction == 0 && requiredFirstHorizontal.HasValue &&
                        (nextDirection == 1) != requiredFirstHorizontal.Value)
                        continue;

                    double edgeLength = Len(a, b);
                    if (edgeLength < 0.01) continue;
                    double axis = nextDirection == 1 ? a.Y : a.X;
                    double nearestLane = Math.Round(
                        (axis - gridSize / 2) / gridSize,
                        MidpointRounding.AwayFromZero) * gridSize +
                        gridSize / 2;
                    bool onGridLane = Math.Abs(axis - nearestLane) < 0.05;
                    bool touchesEndpoint = state.Node == startIndex ||
                                           nextNode == endIndex;
                    bool shortEndpointApproach = touchesEndpoint &&
                                                 edgeLength <= gridSize + 0.01;
                    double cost = knownCost +
                        (prioritizeBends ? edgeLength * 1_000_000d : edgeLength);
                    if (!onGridLane && !shortEndpointApproach)
                        cost += 100_000_000d + edgeLength * 10_000d;
                    if (state.Direction != 0 && state.Direction != nextDirection)
                        cost += prioritizeBends ? 1_000_000_000_000d : 42;
                    foreach (var occupied in occupiedSegments)
                        cost += ScoreCableInteraction(
                            a, b, occupied.A, occupied.B, laneGap);

                    var nextState = (Node: nextNode, Direction: nextDirection);
                    if (distance.TryGetValue(nextState, out double oldCost) &&
                        oldCost <= cost + 0.001)
                        continue;
                    distance[nextState] = cost;
                    previous[nextState] = state;
                    queue.Enqueue(nextState, cost);
                }
            }

            if (!bestEnd.HasValue) return null;
            var states = new List<(int Node, int Direction)>();
            var cursor = bestEnd.Value;
            states.Add(cursor);
            while (previous.TryGetValue(cursor, out var prior))
            {
                cursor = prior;
                states.Add(cursor);
            }
            states.Reverse();
            return RemoveRedundantRoutePoints(
                states.Select(state => points[state.Node]).ToList());
        }

        private static double ScoreCableInteraction(
            Point a,
            Point b,
            Point c,
            Point d,
            double laneGap)
        {
            bool abHorizontal = Math.Abs(a.Y - b.Y) < 0.01;
            bool cdHorizontal = Math.Abs(c.Y - d.Y) < 0.01;

            if (abHorizontal == cdHorizontal)
            {
                double axisDistance = abHorizontal
                    ? Math.Abs(a.Y - c.Y)
                    : Math.Abs(a.X - c.X);
                if (axisDistance >= laneGap) return 0;

                double overlap = abHorizontal
                    ? IntervalOverlap(a.X, b.X, c.X, d.X)
                    : IntervalOverlap(a.Y, b.Y, c.Y, d.Y);
                if (overlap <= 0.01) return 0;

                double closeness = (laneGap - axisDistance) / laneGap;
                // 서로 다른 케이블이 같은 격자 선로를 공유하는 것은 정상
                // 후보에서는 금지에 가깝게 취급한다. 좁은 통로에 정상 차선이
                // 전혀 없을 때만 비격자 균등 분산 후보가 이 비용보다 싸진다.
                double exactOverlapPenalty = axisDistance < 0.5
                    ? 1_000_000_000d + overlap * 1_000_000d
                    : 0;
                return exactOverlapPenalty + overlap * closeness * 3.5;
            }

            Point h1 = abHorizontal ? a : c;
            Point h2 = abHorizontal ? b : d;
            Point v1 = abHorizontal ? c : a;
            Point v2 = abHorizontal ? d : b;

            double minHX = Math.Min(h1.X, h2.X);
            double maxHX = Math.Max(h1.X, h2.X);
            double minVY = Math.Min(v1.Y, v2.Y);
            double maxVY = Math.Max(v1.Y, v2.Y);
            bool crosses = v1.X > minHX + 0.5 && v1.X < maxHX - 0.5 &&
                           h1.Y > minVY + 0.5 && h1.Y < maxVY - 0.5;

            // 교차는 보기 좋지는 않지만 큰 외곽 우회보다는 훨씬 싸게 둔다.
            return crosses ? 28 : 0;
        }

        private static double IntervalOverlap(
            double a1, double a2, double b1, double b2)
        {
            double left = Math.Max(Math.Min(a1, a2), Math.Min(b1, b2));
            double right = Math.Min(Math.Max(a1, a2), Math.Max(b1, b2));
            return Math.Max(0, right - left);
        }

        private static List<Point>? FindCleanOrthogonalRoute(
            Point start,
            Point end,
            IReadOnlyList<Rect> obstacles)
        {
            var candidates = new List<List<Point>>();

            if (Math.Abs(start.X - end.X) < 0.01 ||
                Math.Abs(start.Y - end.Y) < 0.01)
                candidates.Add(new List<Point> { start, end });

            candidates.Add(new List<Point>
            {
                start,
                new Point(start.X, end.Y),
                end
            });
            candidates.Add(new List<Point>
            {
                start,
                new Point(end.X, start.Y),
                end
            });

            var corridorYs = obstacles
                .SelectMany(rect => new[] { rect.Top, rect.Bottom })
                .Distinct()
                .ToList();
            var corridorXs = obstacles
                .SelectMany(rect => new[] { rect.Left, rect.Right })
                .Distinct()
                .ToList();

            foreach (double y in corridorYs)
            {
                candidates.Add(new List<Point>
                {
                    start,
                    new Point(start.X, y),
                    new Point(end.X, y),
                    end
                });
            }
            foreach (double x in corridorXs)
            {
                candidates.Add(new List<Point>
                {
                    start,
                    new Point(x, start.Y),
                    new Point(x, end.Y),
                    end
                });
            }

            List<Point>? best = null;
            double bestScore = double.MaxValue;
            foreach (var candidate in candidates)
            {
                var normalized = RemoveRedundantRoutePoints(candidate);
                bool clear = true;
                for (int i = 0; i < normalized.Count - 1 && clear; i++)
                {
                    foreach (var obstacle in obstacles)
                    {
                        if (SegmentCrossesRectInterior(
                            normalized[i], normalized[i + 1], obstacle))
                        {
                            clear = false;
                            break;
                        }
                    }
                }
                if (!clear) continue;

                double length = 0;
                for (int i = 0; i < normalized.Count - 1; i++)
                    length += Len(normalized[i], normalized[i + 1]);

                // 길이가 조금 짧더라도 불필요하게 한 번 더 꺾이는 경로는 선택하지 않는다.
                double score = length + Math.Max(0, normalized.Count - 2) * 30;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = normalized;
                }
            }

            return best;
        }

        private static List<Point> AvoidObstaclesLocally(
            List<Point> route,
            IReadOnlyList<Rect> obstacles)
        {
            // 원래 경로의 포트 스텁점은 일직선이어도 편집 인덱스 계산에 필요하므로 보존한다.
            var result = new List<Point>(route);
            for (int guard = 0; guard < 48; guard++)
            {
                bool replaced = false;
                for (int segmentIndex = 0; segmentIndex < result.Count - 1; segmentIndex++)
                {
                    var a = result[segmentIndex];
                    var b = result[segmentIndex + 1];
                    var obstacle = FindFirstBlockingObstacle(a, b, obstacles);
                    if (!obstacle.HasValue) continue;

                    var detour = MakeObstacleDetour(a, b, obstacle.Value, obstacles);
                    if (detour.Count <= 2) continue;

                    result.RemoveAt(segmentIndex + 1);
                    result.InsertRange(segmentIndex + 1, detour.Skip(1));
                    result = RemoveAdjacentDuplicatePoints(result);
                    replaced = true;
                    break;
                }

                if (!replaced) break;
            }

            return RemoveAdjacentDuplicatePoints(result);
        }

        private static Rect? FindFirstBlockingObstacle(
            Point a, Point b, IReadOnlyList<Rect> obstacles)
        {
            bool horizontal = Math.Abs(a.Y - b.Y) < 0.01;
            bool vertical = Math.Abs(a.X - b.X) < 0.01;
            if (!horizontal && !vertical) return null;

            Rect? best = null;
            double bestDistance = double.MaxValue;
            foreach (var obstacle in obstacles)
            {
                if (!SegmentCrossesRectInterior(a, b, obstacle)) continue;

                double distance;
                if (horizontal)
                    distance = Math.Min(Math.Abs(obstacle.Left - a.X), Math.Abs(obstacle.Right - a.X));
                else
                    distance = Math.Min(Math.Abs(obstacle.Top - a.Y), Math.Abs(obstacle.Bottom - a.Y));

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = obstacle;
                }
            }
            return best;
        }

        private static bool SegmentCrossesRectInterior(Point a, Point b, Rect rect)
        {
            if (Math.Abs(a.Y - b.Y) < 0.01)
            {
                if (a.Y <= rect.Top || a.Y >= rect.Bottom) return false;
                double minX = Math.Min(a.X, b.X);
                double maxX = Math.Max(a.X, b.X);
                return maxX > rect.Left && minX < rect.Right;
            }

            if (Math.Abs(a.X - b.X) < 0.01)
            {
                if (a.X <= rect.Left || a.X >= rect.Right) return false;
                double minY = Math.Min(a.Y, b.Y);
                double maxY = Math.Max(a.Y, b.Y);
                return maxY > rect.Top && minY < rect.Bottom;
            }

            return false;
        }

        private static List<Point> MakeObstacleDetour(
            Point a, Point b, Rect obstacle, IReadOnlyList<Rect> obstacles)
        {
            List<Point> first;
            List<Point> second;

            if (Math.Abs(a.Y - b.Y) < 0.01)
            {
                first = new List<Point>
                {
                    a,
                    new Point(a.X, obstacle.Top),
                    new Point(b.X, obstacle.Top),
                    b
                };
                second = new List<Point>
                {
                    a,
                    new Point(a.X, obstacle.Bottom),
                    new Point(b.X, obstacle.Bottom),
                    b
                };
            }
            else
            {
                first = new List<Point>
                {
                    a,
                    new Point(obstacle.Left, a.Y),
                    new Point(obstacle.Left, b.Y),
                    b
                };
                second = new List<Point>
                {
                    a,
                    new Point(obstacle.Right, a.Y),
                    new Point(obstacle.Right, b.Y),
                    b
                };
            }

            first = RemoveRedundantRoutePoints(first);
            second = RemoveRedundantRoutePoints(second);
            return ScoreDetour(first, obstacles) <= ScoreDetour(second, obstacles)
                ? first
                : second;
        }

        private static double ScoreDetour(List<Point> route, IReadOnlyList<Rect> obstacles)
        {
            double score = 0;
            for (int i = 0; i < route.Count - 1; i++)
            {
                score += Len(route[i], route[i + 1]);
                foreach (var obstacle in obstacles)
                {
                    if (SegmentCrossesRectInterior(route[i], route[i + 1], obstacle))
                        score += 100000;
                }
            }
            return score;
        }

        private static List<Point> RemoveRedundantRoutePoints(IEnumerable<Point> points)
        {
            var result = new List<Point>();
            foreach (var point in points)
            {
                if (result.Count == 0 || Len(result[^1], point) > 0.01)
                    result.Add(point);
            }

            bool changed = true;
            while (changed && result.Count > 2)
            {
                changed = false;
                for (int i = 1; i < result.Count - 1; i++)
                {
                    var prev = result[i - 1];
                    var current = result[i];
                    var next = result[i + 1];
                    bool vertical = Math.Abs(prev.X - current.X) < 0.01 &&
                                    Math.Abs(current.X - next.X) < 0.01;
                    bool horizontal = Math.Abs(prev.Y - current.Y) < 0.01 &&
                                      Math.Abs(current.Y - next.Y) < 0.01;
                    if (!vertical && !horizontal) continue;
                    result.RemoveAt(i);
                    changed = true;
                    break;
                }
            }
            return result;
        }

        private static List<Point> RemoveAdjacentDuplicatePoints(IEnumerable<Point> points)
        {
            var result = new List<Point>();
            foreach (var point in points)
            {
                if (result.Count == 0 || Len(result[^1], point) > 0.01)
                    result.Add(point);
            }
            return result;
        }

        public static (List<(Point A, Point B, string LegKey)> Segments, List<PendingLabel> Labels) Draw(
            Canvas overlay, CableInstance inst, Cable cable, CableRenderCallbacks cb, bool isSelected)
        {
            var segments = new List<(Point, Point, string)>();
            var labels = new List<PendingLabel>();
            void AddSegments(List<Point> pts, string legKey)
            {
                for (int i = 0; i < pts.Count - 1; i++) segments.Add((pts[i], pts[i + 1], legKey));
            }

            var (ax, ay, aDevFacing) = ResolveConnection(inst.PortA, cb.ResolveAnchor);
            var (bx, by, bDevFacing) = ResolveConnection(inst.PortB, cb.ResolveAnchor);
            PortSide aFacing = aDevFacing ?? cb.ResolvePortFacing?.Invoke(inst.PortA) ?? inst.PortA.FixedFacing;
            PortSide bFacing = bDevFacing ?? cb.ResolvePortFacing?.Invoke(inst.PortB) ?? inst.PortB.FixedFacing;

            var aPos = new Point(ax, ay); var bPos = new Point(bx, by);
            var rootBends = inst.RootBends.Select(ToPoint).ToList();
            var rootRoute = CollapseNearDuplicates(
                BuildFullRoute(aPos, aFacing, bPos, bFacing, rootBends),
                hasStartStub: true, hasEndStub: true);
            rootRoute = AvoidObstacles(
                rootRoute, cb.GetObstacles?.Invoke("root"),
                hasStartStub: true, hasEndStub: true);

            string rootLegKey = $"{inst.Id}:root";
            DrawLegLine(overlay, rootRoute, "root", cb, isSelected, hasStartStub: true, hasEndStub: true);
            AddSegments(rootRoute, rootLegKey);

            var connA = FindConnector(cable, inst.PortA.ConnectorId);
            var connB = FindConnector(cable, inst.PortB.ConnectorId);
            DrawPortMark(overlay, ax, ay, aFacing, aDevFacing.HasValue, connA, inst.PortA, cb, labels);
            DrawPortMark(overlay, bx, by, bFacing, bDevFacing.HasValue, connB, inst.PortB, cb, labels);

            // 케이블 이름표 (기본은 루트 중앙, 사용자가 끌어서 옮긴 만큼 반영)
            double nameBaseX = (aPos.X + bPos.X) / 2;
            double nameBaseY = Math.Min(aPos.Y, bPos.Y) - 20;
            if (inst.ShowName)
            {
                labels.Add(new PendingLabel
                {
                    X = nameBaseX + inst.NameLabelDX,
                    Y = nameBaseY + inst.NameLabelDY,
                    BaseX = nameBaseX,
                    BaseY = nameBaseY,
                    Text = cable.Name,
                    FontSize = double.IsFinite(cable.NameFontSize) ? Math.Clamp(cable.NameFontSize, 6, 200) : 13,
                    IsCableName = true
                });
            }

            foreach (var branch in inst.Branches.Where(b => b.ParentId == "root"))
                DrawBranch(overlay, inst, cable, branch, rootRoute, cb, segments, AddSegments, labels, isSelected);

            return (segments, labels);
        }

        private static void DrawBranch(Canvas overlay, CableInstance inst, Cable cable, CableBranch branch,
                                        List<Point> parentRoute, CableRenderCallbacks cb,
                                        List<(Point, Point, string)> allSegments, Action<List<Point>, string> addSegments,
                                        List<PendingLabel> labels, bool isSelected)
        {
            // 분기점은 부모의 "실제 꺾인 경로" 위 t 지점에 놓는다.
            // (양 끝을 잇는 직선으로 계산하면 부모가 꺾여있을 때 선 밖으로 벗어나 버린다)
            var anchor = PointAlongPolyline(parentRoute, branch.T);

            var (px, py, devFacing) = ResolveConnection(branch.Port, cb.ResolveAnchor);
            PortSide portFacing = devFacing ?? cb.ResolvePortFacing?.Invoke(branch.Port) ?? branch.Port.FixedFacing;
            var portPos = new Point(px, py);

            var bends = branch.Bends.Select(ToPoint).ToList();
            var route = CollapseNearDuplicates(
                BuildFullRoute(anchor, null, portPos, portFacing, bends),
                hasStartStub: false, hasEndStub: true);
            bool? requiredFirstHorizontal =
                GetFirstSegmentHorizontal(route);
            route = AvoidObstacles(
                route, cb.GetObstacles?.Invoke(branch.Id),
                hasStartStub: false, hasEndStub: true,
                requiredFirstHorizontal: requiredFirstHorizontal);

            string legKey = $"{inst.Id}:{branch.Id}";
            DrawLegLine(overlay, route, branch.Id, cb, isSelected, hasStartStub: false, hasEndStub: true);
            addSegments(route, legKey);

            if (isSelected)
            {
                RemoveCornerMarkerAtBranch(overlay, anchor);
                MoveOverlappingSegmentHandle(overlay, anchor);
                var dotHitArea = new Canvas
                {
                    Width = 14,
                    Height = 14,
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    ToolTip = "분기점 — 끌어서 이 케이블의 다른 선으로 이동"
                };
                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = Brushes.MediumSeaGreen,
                    Stroke = Brushes.DarkGreen,
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(dot, 4);
                Canvas.SetTop(dot, 4);
                dotHitArea.Children.Add(dot);
                Canvas.SetLeft(dotHitArea, anchor.X - 7);
                Canvas.SetTop(dotHitArea, anchor.Y - 7);
                dotHitArea.MouseLeftButtonDown += (s, e) =>
                {
                    cb.OnSelect?.Invoke();
                    cb.OnBranchDotDown?.Invoke(branch, e);
                    e.Handled = true;
                };
                overlay.Children.Add(dotHitArea);
            }

            var conn = FindConnector(cable, branch.Port.ConnectorId);
            DrawPortMark(overlay, px, py, portFacing, devFacing.HasValue, conn, branch.Port, cb, labels);

            foreach (var child in inst.Branches.Where(b => b.ParentId == branch.Id))
                DrawBranch(overlay, inst, cable, child, route, cb, allSegments, addSegments, labels, isSelected);
        }

        /// <summary>
        /// 분기점과 선분 이동 핸들이 정확히 겹치면 분기점이 마우스 입력을
        /// 가로챈다. 핸들은 선분 위의 다른 위치로 옮겨 두 기능을 모두 쓸 수
        /// 있게 한다. 짧은 선분이라 옆으로 옮길 공간이 없을 때만 선에서
        /// 약간 떨어뜨려 표시한다.
        /// </summary>
        private static void MoveOverlappingSegmentHandle(
            Canvas overlay, Point branchAnchor)
        {
            const double overlapRadius = 8;
            const double alongOffset = 14;

            foreach (var handle in overlay.Children
                         .OfType<Rectangle>()
                         .Where(item => item.Tag is SegmentHandleInfo))
            {
                if (handle.Tag is not SegmentHandleInfo info) continue;
                double left = Canvas.GetLeft(handle);
                double top = Canvas.GetTop(handle);
                double centerX = left + handle.Width / 2;
                double centerY = top + handle.Height / 2;
                if (Len(new Point(centerX, centerY), branchAnchor) >=
                    overlapRadius) continue;

                if (info.IsHorizontal)
                {
                    double min = Math.Min(info.A.X, info.B.X) +
                                 handle.Width / 2 + 1;
                    double max = Math.Max(info.A.X, info.B.X) -
                                 handle.Width / 2 - 1;
                    double candidate = centerX + alongOffset;
                    if (candidate <= max)
                        Canvas.SetLeft(handle, candidate - handle.Width / 2);
                    else if (centerX - alongOffset >= min)
                        Canvas.SetLeft(
                            handle, centerX - alongOffset - handle.Width / 2);
                    else
                        handle.Visibility = Visibility.Collapsed;
                }
                else
                {
                    double min = Math.Min(info.A.Y, info.B.Y) +
                                 handle.Height / 2 + 1;
                    double max = Math.Max(info.A.Y, info.B.Y) -
                                 handle.Height / 2 - 1;
                    double candidate = centerY + alongOffset;
                    if (candidate <= max)
                        Canvas.SetTop(handle, candidate - handle.Height / 2);
                    else if (centerY - alongOffset >= min)
                        Canvas.SetTop(
                            handle, centerY - alongOffset - handle.Height / 2);
                    else
                        handle.Visibility = Visibility.Collapsed;
                }
            }
        }

        private static void RemoveCornerMarkerAtBranch(
            Canvas overlay, Point branchAnchor)
        {
            var markers = overlay.Children
                .OfType<Ellipse>()
                .Where(item => item.Tag is CornerMarkerInfo info &&
                    Len(info.Position, branchAnchor) < 1.0)
                .ToList();
            foreach (var marker in markers)
                overlay.Children.Remove(marker);
        }
        /// <summary>
        /// 서로 다른 다리(leg)끼리 "연결 아닌 교차"가 생기는 지점에 살짝 비켜가는 표시를 overlay 에 그린다.
        /// 같은 다리가 스스로 꺾이며 겹치는 경우는 진짜 교차가 아니므로 제외한다.
        /// 곡선(베지어/원호) 대신, 지금까지 계속 잘 동작한 것과 똑같은 단순 Polyline(직선 여러 개 잇기)만
        /// 쓰고 모서리만 둥글게 처리한다 — 기하 계산이 훨씬 단순해서 어긋날 여지가 적다.
        /// </summary>
        public static void DrawCrossingJumps(
            Canvas overlay,
            List<List<(Point A, Point B, string LegKey)>> allCableSegments,
            IReadOnlyList<Brush>? cableBrushes = null)
        {
            foreach (var crossing in CableCrossingIndex.Find(allCableSegments))
            {
                int cableI = crossing.HorizontalCable, cableJ = crossing.VerticalCable;
                Brush horizontalBrush = cableBrushes != null && cableI < cableBrushes.Count
                    ? cableBrushes[cableI]
                    : Brushes.Black;
                Brush verticalBrush = cableBrushes != null && cableJ < cableBrushes.Count
                    ? cableBrushes[cableJ]
                    : Brushes.Black;
                DrawNotch(overlay, crossing.X, crossing.Y, horizontalBrush, verticalBrush);
            }
        }

        /// <summary>
        /// (x,y) 지점에서 가로선이 살짝 위로 비켜가는 작은 굴곡을 그린다.
        /// 흰색으로 덮어 지우지 않는다 — 그렇게 하면 그 자리를 지나가는 세로선까지 같이 지워져서
        /// 세로선이 끊긴 것처럼 보이기 때문. 노치의 양 끝이 원래 선 좌표에 정확히 붙어있으므로
        /// 그냥 덧그려도 자연스럽게 이어져 보인다.
        /// </summary>
        private static void DrawNotch(
            Canvas overlay, double x, double y,
            Brush horizontalBrush, Brush verticalBrush)
        {
            double halfW = 4.5;      // 실제 보이는 곡선의 폭
            double eraseHalfW = 3.0; // 지우는 폭은 곡선보다 좁게 잡는다.
            double depth = 3.5;      // 위로 얼마나 비켜가는지

            // ① 원래 가로선의 그 구간을 흰색으로 덮어 지운다.
            //    곡선(③)보다 살짝 좁게 지워서, 곡선이 원래 선 위로 여유 있게 겹쳐 덮이게 한다.
            //    (지우는 폭과 곡선 폭을 똑같이 맞추면 그 경계에서 안티앨리어싱 때문에
            //     아주 얇은 흰 틈이 생길 수 있어서, 일부러 곡선을 더 넓게 잡아 여유를 둔다.)
            var eraser = new Polyline
            {
                Stroke = Brushes.White, StrokeThickness = LineThickness + 2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Points = new PointCollection { new Point(x - eraseHalfW, y), new Point(x + eraseHalfW, y) }
            };
            overlay.Children.Add(eraser);

            // ② 방금 지운 자리에 세로선이 지나가고 있었다면 같이 지워져서 끊겨 보인다.
            //    그 자리에 세로선을 다시 짧게 덧그려서 항상 이어져 보이게 한다.
            var vertRestore = new Line
            {
                X1 = x, Y1 = y - (LineThickness + 4), X2 = x, Y2 = y + (LineThickness + 4),
                Stroke = verticalBrush, StrokeThickness = LineThickness
            };
            overlay.Children.Add(vertRestore);

            // ③ 실제로 보이는, 가로선이 살짝 비켜가는 굴곡. 2차 베지어 곡선 하나로 매끄러운
            //    반달 모양을 만든다. ①보다 넓게 그려서 원래 직선 위로 여유 있게 겹친다.
            var figure = new PathFigure { StartPoint = new Point(x - halfW, y), IsClosed = false };
            figure.Segments.Add(new QuadraticBezierSegment(new Point(x, y - depth * 2), new Point(x + halfW, y), true));
            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            overlay.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geo, Stroke = horizontalBrush, StrokeThickness = LineThickness, Fill = null,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
        }
    }
}
