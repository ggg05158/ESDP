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
    public enum HandleKind
    {
        TopLeft, Top, TopRight,
        Left, Right,
        BottomLeft, Bottom, BottomRight
    }

    public class PlacedDevice
    {
        public Device TemplateDevice { get; set; } = null!;
        public DeviceInstance Instance { get; set; } = null!;
        public Canvas Container { get; set; } = null!;

        public Device EffectiveDevice => Instance.Override ?? TemplateDevice;
    }

    public class PlacedCable
    {
        public Cable TemplateCable { get; set; } = null!;
        public CableInstance Instance { get; set; } = null!;
        public Canvas Container { get; set; } = null!;

        public Cable EffectiveCable => Instance.Override ?? TemplateCable;

        /// <summary>가장 최근에 그려진 모든 선분 (교차점/반원 점프 계산용)</summary>
        public List<(Point A, Point B, string LegKey)> RenderedSegments { get; set; } = new();

        /// <summary>가장 최근에 그려진 모든 포트 라벨 (겹침 방지 배치용)</summary>
        public List<CableRenderer.PendingLabel> RenderedLabels { get; set; } = new();
    }

    internal sealed class CableLabelVisualInfo
    {
        public PlacedCable Placed { get; init; } = null!;
        public CableRenderer.PendingLabel Label { get; init; } = null!;
    }

    internal sealed class CableArrangeExclusionListItem
    {
        public string Name { get; init; } = "";
        public string StateText { get; init; } = "";
        public Brush StateBrush { get; init; } = Brushes.Gray;
    }

    /// <summary>장치/케이블과 무관한 일반 도형(사각형/타원/선/텍스트) 하나가 캔버스에 배치된 것.</summary>
    public class PlacedShape
    {
        public CanvasShape Shape { get; set; } = null!;
        public Canvas Container { get; set; } = null!;
    }

    /// <summary>장치와 케이블보다 뒤에 표시되는 이름 있는 구역.</summary>
    public class PlacedZone
    {
        public Zone Zone { get; set; } = null!;
        public Canvas Container { get; set; } = null!;
    }

    internal sealed class ArrangeTarget
    {
        public Func<double> Left { get; init; } = null!;
        public Func<double> Top { get; init; } = null!;
        public Func<double> Width { get; init; } = null!;
        public Func<double> Height { get; init; } = null!;
        public Action<double, double> MoveTo { get; init; } = null!;
    }

    public partial class MainWindow : Window
    {
        // 메인 캔버스의 장치 배치 기준. 장치의 위치와 크기는 언제나 이
        // 20px 격자를 따른다. 일반 도형은 자유 좌표를 유지하지만 케이블
        // 선분과 분기 접점은 격자 칸의 중앙선에 자석처럼 맞춘다.
        private const double DiagramGridSize = 20;
        private const double DefaultDeviceWidth = DiagramGridSize * 6;
        private const double DefaultDeviceHeight = DiagramGridSize * 4;
        private const double PortWidth = DevicePreviewRenderer.PortWidth;
        private const double HandleSize = 6;
        private const double PxPerMm = 3.0;
        private const double ExtendMargin = 30;
        private const string PageGuideTag = "__pageGuide";
        private const int ShapeLayerBaseZIndex = 200;
        private const int ShapeBackgroundBaseZIndex = -200;
        private const int ZoneLayerZIndex = -1000;

        private const double ZoomStep = 1.1;
        private const double CablePortLabelMinRadius = 9;
        private const double CablePortLabelMaxRadius = 20;
        private const double MinZoom = 0.2;
        private const double MaxZoom = 4.0;

        private static double SnapToDiagramGrid(double value)
            => Math.Round(value / DiagramGridSize,
                MidpointRounding.AwayFromZero) * DiagramGridSize;

        private static Point SnapToDiagramGrid(Point point)
            => new(SnapToDiagramGrid(point.X), SnapToDiagramGrid(point.Y));

        private static double SnapToDiagramGridMidpoint(double value)
            => Math.Round((value - DiagramGridSize / 2) / DiagramGridSize,
                MidpointRounding.AwayFromZero) * DiagramGridSize +
               DiagramGridSize / 2;

        /// <summary>
        /// 지정된 선분 범위 안에서 가장 가까운 격자 칸 중앙 좌표를 찾는다.
        /// 매우 짧은 선분처럼 범위 안에 중앙선이 없을 때만 기존 좌표를 유지한다.
        /// </summary>
        private static double SnapToGridMidpointWithin(
            double value, double first, double second)
        {
            double min = Math.Min(first, second);
            double max = Math.Max(first, second);
            double firstMidpoint = Math.Ceiling(
                (min - DiagramGridSize / 2) / DiagramGridSize) *
                DiagramGridSize + DiagramGridSize / 2;
            double lastMidpoint = Math.Floor(
                (max - DiagramGridSize / 2) / DiagramGridSize) *
                DiagramGridSize + DiagramGridSize / 2;

            if (firstMidpoint > lastMidpoint + 0.01)
                return Math.Max(min, Math.Min(max, value));

            return Math.Max(firstMidpoint, Math.Min(
                lastMidpoint, SnapToDiagramGridMidpoint(value)));
        }

        /// <summary>
        /// 자유 케이블 포트는 격자 교차점이 아니라 격자 한 칸의 변 중앙에 둔다.
        /// 좌우 방향 포트는 세로 변 중앙, 상하 방향 포트는 가로 변 중앙이다.
        /// </summary>
        private static Point SnapCablePortToGridFace(Point point, PortSide facing)
            => facing is PortSide.Left or PortSide.Right
                ? new Point(SnapToDiagramGrid(point.X),
                    SnapToDiagramGridMidpoint(point.Y))
                : new Point(SnapToDiagramGridMidpoint(point.X),
                    SnapToDiagramGrid(point.Y));

        private static double SnapLengthToDiagramGrid(double value, double minimum)
            => Math.Max(
                Math.Ceiling(minimum / DiagramGridSize) * DiagramGridSize,
                Math.Round(value / DiagramGridSize,
                    MidpointRounding.AwayFromZero) * DiagramGridSize);

        private bool _isPanning;
        private Point _panStartMouse;
        private double _panStartH, _panStartV;

        private PaperSize _currentPaper = PaperSize.A3_Landscape;
        private readonly List<MenuItem> _paperMenuItems = new();

        private readonly ObservableCollection<DeviceListItem> _registeredDevices = new();
        private readonly ObservableCollection<CableListItem> _registeredCables = new();
        private readonly List<string> _customDeviceKinds = new();
        private readonly List<string> _deviceCategoryNames = Enum.GetNames<DeviceKind>().ToList();
        private readonly Dictionary<string, string> _deviceCategoryColors = AppSettings.KindColors
            .ToDictionary(pair => pair.Key.ToString(), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _customCableCategories = new();
        private ICollectionView? _deviceSidebarView;
        private ICollectionView? _cableSidebarView;
        private readonly List<PlacedDevice> _placed = new();
        private readonly List<PlacedCable> _placedCables = new();
        private readonly List<PlacedShape> _placedShapes = new();
        private readonly List<PlacedZone> _placedZones = new();

        private bool _isZoneSettingMode;
        private bool _isCreatingZone;
        private Point _zoneDrawStart;
        private Rectangle? _zoneDrawPreview;
        private PlacedZone? _selectedZone;
        private PlacedZone? _zoneDragPlaced;
        private HandleKind? _zoneDragHandle;
        private Point _zoneDragStartMouse;
        private double _zoneDragStartX, _zoneDragStartY;
        private double _zoneDragStartWidth, _zoneDragStartHeight;
        private bool _zoneDragChanged;
        private bool _updatingZoneListSelection;

        private PlacedShape? _selectedShape;
        private readonly HashSet<PlacedShape> _selectedShapes = new();
        private readonly HashSet<CablePortLayout> _selectedCablePorts = new();
        private PlacedShape? _movingShape;
        private PlacedShape? _resizingShape;
        private PlacedShape? _rotatingShape;
        private Point _rotationCenter;
        private double _rotationStartPointerAngle, _rotationStartAngle;
        private HandleKind _shapeResizeKind;
        private Point _shapeResizeStartMouse;
        private double _srsLeft, _srsTop, _srsWidth, _srsHeight;
        private PlacedShape? _shapeLineEndpointDragPlaced;
        private int _shapeLineEndpointDragIndex = -1;
        private PlacedShape? _shapeLineSegmentDragPlaced;
        private int _shapeLineSegmentDragIndex = -1;
        private List<ESDP.Models.Point> _shapeLineDragStartPoints = new();

        // 케이블 상호작용 상태
        private PlacedCable? _cablePortDragPlaced;
        private CablePortLayout? _cablePortDragLayout;
        private CablePortDragProtection? _cablePortDragProtection;
        private Point? _pendingCablePortMouse;

        private PlacedCable? _cableBranchDragPlaced;
        private CableBranch? _cableBranchDragBranch;
        private Point _cableBranchDragStartMouse;
        private bool _cableBranchDragHasMoved;

        private PlacedCable? _cableSegDragPlaced;
        private string? _cableSegDragLegId;
        private List<Point>? _cableSegDragRoute;
        private int _cableSegDragSegmentIndex;
        private bool _cableSegDragHorizontal;
        private Point _cableSegDragStartMouse;
        // 분기의 첫 선분도 일반 선분처럼 이동하되, 시작점(녹색 분기점)은
        // 기존 부모 경로 위에서만 함께 움직인다.
        private CableBranch? _cableSegDragOwnBranch;
        private List<Point>? _cableSegDragParentRoute;
        private double _cableSegDragOwnBranchStartT;
        private double _cableSegDragParentLength;
        private Vector _cableSegDragParentTangent;
        private bool _cableSegDragHasMoved;
        private readonly Dictionary<CableBranch, (
            Point Anchor, bool MovesWithSegment)>
            _cableSegDragBranchAnchors = new();
        private PlacedCable? _wholeCableDragAnchor;
        private Point _wholeCableDragStartMouse;
        private readonly Dictionary<CablePortLayout, Point>
            _wholeCableDragPortStarts = new();
        private readonly Dictionary<ESDP.Models.Point, Point>
            _wholeCableDragBendStarts = new();

        private PlacedCable? _selectedCable; // 꺾인점 등 편집 핸들은 선택된 케이블에서만 보인다
        private bool _isCableArrangeExclusionMode;

        private PlacedCable? _cableLabelDragPlaced;
        private string? _cableLabelDragConnectorId; // null 이면 케이블 이름표를 끄는 중
        private bool _cableLabelDragIsName;
        private Point _cableLabelDragStartMouse;
        private double _cableLabelDragOrigDX, _cableLabelDragOrigDY;

        private PlacedDevice? _devicePortLabelDragPlaced;
        private Port? _devicePortLabelDragPort;
        private TextBlock? _devicePortLabelDragVisual;
        private Point _devicePortLabelDragStartMouse;
        private double _devicePortLabelDragOrigX, _devicePortLabelDragOrigY;
        private double _devicePortLabelDragAnchorX, _devicePortLabelDragAnchorY;

        private Canvas? _crossingOverlay;
        private Canvas? _labelOverlay;

        private const double CableSnapRadius = 12; // 자석 스냅 반경(px) — 아주 가까이 왔을 때만

        // 다중 선택 지원: 여러 개체를 동시에 선택 상태로 둘 수 있다.
        private readonly HashSet<PlacedDevice> _selectedSet = new();

        // 그룹 이동용: 드래그 시작 시점의 각 개체 위치를 기록해둔다.
        private PlacedDevice? _movingAnchor;
        private Point _moveStartMouse;
        private Dictionary<PlacedDevice, Point> _moveStartPositions = new();
        private Dictionary<PlacedShape, Point> _shapeMoveStartPositions = new();
        private Dictionary<CablePortLayout, Point> _groupMovePortStarts = new();
        private Dictionary<ESDP.Models.Point, Point> _groupMoveBendStarts = new();

        private PlacedDevice? _resizingDevice;
        private HandleKind _resizeKind;
        private Point _resizeStartMouse;
        private double _rsLeft, _rsTop, _rsWidth, _rsHeight;

        private PlacedDevice? _portDragPlaced;
        private Port? _portDragPort;
        private PlacedDevice? _selectedDevicePortPlaced;
        private Port? _selectedDevicePort;

        private bool _isMarqueeSelecting;
        private Point _marqueeStart;
        private Rectangle? _marqueeRect;

        private Point _sidebarDragStart;
        private DeviceListItem? _sidebarDragCandidate;
        private bool _isSidebarDragging;

        private Rectangle? _pageGuide;
        private string? _currentFilePath;
        private bool _isDirtyValue; // 저장 안 된 변경사항이 있는지
        private bool _isDirty
        {
            get => _isDirtyValue;
            set
            {
                // 시트를 화면에 다시 펼치는 과정은 편집이 아니다. 이때 발생하는
                // 렌더링 호출이 저장 상태나 Undo 기준점을 건드리지 않게 한다.
                if (_restoringHistory || _applyingSheet) return;
                _isDirtyValue = value;
                if (!_historyReady) return;

                if (value)
                    CommitHistoryIfActionComplete();
                else
                    FlushPendingHistoryCommit();
            }
        }
        private bool _updatingTextFormatPalette;
        private sealed record LineFormatPreviewState(
            PlacedShape Placed,
            double Thickness,
            string LineStyle,
            string StartCap,
            string EndCap);
        private LineFormatPreviewState? _lineFormatPreview;
        private ComboBox? _lineFormatPreviewCombo;
        private bool _lineFormatPreviewCommitted;
        private RichTextBox? _activeTextEditor;
        private PlacedShape? _editingShape;
        private PlacedDevice? _editingDevice;

        private const int MaximumUndoSteps = 100;
        private const long MaximumHistoryBytes = 64L * 1024 * 1024;
        private readonly HistorySnapshotStore _historyStore = new(ProjectFileService.JsonOptions);
        private readonly List<HistorySnapshot> _undoHistory = new();
        private readonly List<HistorySnapshot> _redoHistory = new();
        private HistorySnapshot? _historyBaseline;
        private bool _historyReady;
        private bool _restoringHistory;
        private bool _applyingSheet;
        // Export must never run editing/repair operations on the live document.
        private bool _isPdfExportRendering;
        private bool _lastSheetApplyNormalizedBranchStructure;
        private readonly DispatcherTimer _keyboardNudgeTimer = new()
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        private bool _keyboardNudgeActive;
        private Key _keyboardNudgeDirection;
        private HistorySnapshot? _keyboardNudgeStartState;

        public MainWindow()
        {
            InitializeComponent();
            Closed += (_, __) => CancelCableDragRender();
            UpdateThemeMenuChecks();
            _keyboardNudgeTimer.Tick += (_, __) => EndKeyboardNudge();
            PreviewMouseDown += (_, __) => EndKeyboardNudge();
            BuildPaperSizeMenu();
            DeviceListBox.ItemsSource = _registeredDevices;
            CableListBox.ItemsSource = _registeredCables;
            ConfigureSidebarViews();
            FontFamilyCombo.ItemsSource = new[]
                { AppFonts.DefaultFamilyName }
                .Concat(Fonts.SystemFontFamilies
                    .Select(font => font.Source)
                    .Where(name => !name.Equals(
                        AppFonts.DefaultFamilyName,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(name => name))
                .ToList();
            FontSizeCombo.ItemsSource = new[] { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72 };
            LineThicknessCombo.ItemsSource = new[] { 0.5, 1.0, 1.5, 2.0, 3.0, 4.0, 6.0, 8.0 };

            // 시트 한 장으로 시작 (엑셀처럼)
            _sheets.Add(new Sheet { Name = "Sheet1", PaperSize = _currentPaper });
            _currentSheetIndex = 0;
            RefreshSheetTabs(0);

            ApplyPaperSize();
            _isDirty = false;
            UpdateTextFormatPalette();
            ResetUndoHistory();
        }

        // ================= 상황별 텍스트 서식 팔레트 =================
        private void UpdateTextFormatPalette()
        {
            CanvasShape? shape = _selectedShape?.Shape;
            var selectedDevices = _selectedSet.ToList();
            int arrangeCount = _selectedSet.Count + _selectedShapes.Count +
                               _selectedCablePorts.Count;
            HomeArrangeButton.IsEnabled = arrangeCount >= 2;

            if (shape == null && selectedDevices.Count == 0)
            {
                HomePalette.Visibility = Visibility.Visible;
                TextFormattingScrollViewer.Visibility = Visibility.Collapsed;
                ShapeObjectTools.Visibility = Visibility.Collapsed;
                ObjectArrangeButton.IsEnabled = false;
                return;
            }

            string fontFamily;
            double fontSize;
            bool bold;
            bool italic;
            bool underline;
            string colorHex;
            string alignment;

            if (shape != null)
            {
                UpgradeLegacyLineShape(shape);
                TextFormatTargetLabel.Text = shape.Kind == "Text"
                    ? "텍스트 도형"
                    : "도형 텍스트";
                fontFamily = shape.FontFamily;
                fontSize = shape.FontSize;
                bold = shape.TextBold;
                italic = shape.TextItalic;
                underline = shape.TextUnderline;
                colorHex = shape.TextColorHex;
                alignment = shape.TextAlignment;
            }
            else
            {
                var device = selectedDevices[0].EffectiveDevice;
                TextFormatTargetLabel.Text = selectedDevices.Count == 1
                    ? "장치 이름"
                    : $"장치 이름 {selectedDevices.Count}개";
                fontFamily = device.NameFontFamily;
                fontSize = device.NameFontSize;
                bold = device.NameBold;
                italic = device.NameItalic;
                underline = device.NameUnderline;
                colorHex = device.NameColorHex;
                alignment = device.NameTextAlignment;
            }

            if (_activeTextEditor != null)
            {
                var selection = _activeTextEditor.Selection;
                object value = selection.GetPropertyValue(
                    TextElement.FontFamilyProperty);
                if (value is FontFamily selectedFamily)
                    fontFamily = AppFonts.ToDisplayName(selectedFamily);

                value = selection.GetPropertyValue(TextElement.FontSizeProperty);
                if (value is double selectedSize)
                    fontSize = selectedSize;

                value = selection.GetPropertyValue(TextElement.FontWeightProperty);
                if (value is FontWeight selectedWeight)
                    bold = selectedWeight >= FontWeights.Bold;

                value = selection.GetPropertyValue(TextElement.FontStyleProperty);
                if (value is FontStyle selectedStyle)
                    italic = selectedStyle == FontStyles.Italic;

                value = selection.GetPropertyValue(Inline.TextDecorationsProperty);
                if (value is TextDecorationCollection decorations)
                    underline = decorations.Count > 0;

                value = selection.GetPropertyValue(TextElement.ForegroundProperty);
                colorHex = RichTextSupport.BrushToHex(value, colorHex);

                value = selection.GetPropertyValue(Paragraph.TextAlignmentProperty);
                if (value is TextAlignment selectedAlignment)
                    alignment = selectedAlignment.ToString();
            }

            _updatingTextFormatPalette = true;
            HomePalette.Visibility = Visibility.Collapsed;
            TextFormattingScrollViewer.Visibility = Visibility.Visible;
            bool isLineShape = shape != null && IsLineShapeKind(shape.Kind);
            TextOnlyTools.Visibility = isLineShape
                ? Visibility.Collapsed
                : Visibility.Visible;
            ShapeObjectTools.Visibility = shape != null
                ? Visibility.Visible
                : Visibility.Collapsed;
            LineShapeTools.Visibility = isLineShape
                ? Visibility.Visible
                : Visibility.Collapsed;
            ShapeEditTextButton.Visibility = isLineShape
                ? Visibility.Collapsed
                : Visibility.Visible;
            ShapeFillButton.Visibility = isLineShape
                ? Visibility.Collapsed
                : Visibility.Visible;
            ObjectArrangeButton.IsEnabled = arrangeCount >= 2;
            if (shape != null)
            {
                ShapeFillButton.IsEnabled = !isLineShape;
                ShapeFillSwatch.Fill = HexToBrush(shape.FillHex);
                ShapeStrokeSwatch.Fill = HexToBrush(shape.StrokeHex);
                if (IsLineShapeKind(shape.Kind))
                {
                    LineThicknessCombo.Text = shape.StrokeThickness.ToString("0.#");
                    SelectComboTag(LineStyleCombo, shape.LineStyle);
                    SelectComboTag(LineStartCapCombo, shape.StartLineCap);
                    SelectComboTag(LineEndCapCombo, shape.EndLineCap);
                }
            }
            FontFamilyCombo.Text = AppFonts.NormalizeFamilyName(fontFamily);
            FontSizeCombo.Text = fontSize.ToString("0.#");
            TextBoldButton.IsChecked = bold;
            TextItalicButton.IsChecked = italic;
            TextUnderlineButton.IsChecked = underline;
            TextColorSwatch.Fill = HexToBrush(colorHex);
            TextAlignLeftButton.IsChecked = alignment == "Left";
            TextAlignCenterButton.IsChecked = alignment != "Left" && alignment != "Right";
            TextAlignRightButton.IsChecked = alignment == "Right";
            _updatingTextFormatPalette = false;
        }

        private static void SelectComboTag(ComboBox comboBox, string? tag)
        {
            comboBox.SelectedItem = comboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        }

        private void ApplySelectedLineFormat(Action<CanvasShape> change)
        {
            if (_updatingTextFormatPalette || _selectedShape == null ||
                !IsLineShapeKind(_selectedShape.Shape.Kind)) return;
            change(_selectedShape.Shape);
            RenderShape(_selectedShape);
            _isDirty = true;
        }

        private void LineThicknessCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            MarkLineFormatPreviewCommitted(sender);
            if (LineThicknessCombo.SelectedItem is double selectedThickness)
            {
                ApplySelectedLineFormat(shape =>
                    shape.StrokeThickness = Math.Clamp(selectedThickness, 0.5, 20));
                return;
            }
            ApplyLineThicknessFromPalette();
        }

        private void LineThicknessCombo_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
            => ApplyLineThicknessFromPalette();

        private void LineThicknessCombo_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            ApplyLineThicknessFromPalette();
            Keyboard.ClearFocus();
            e.Handled = true;
        }

        private void ApplyLineThicknessFromPalette()
        {
            if (!double.TryParse(LineThicknessCombo.Text, out double thickness)) return;
            ApplySelectedLineFormat(shape =>
                shape.StrokeThickness = Math.Clamp(thickness, 0.5, 20));
        }

        private void LineStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            MarkLineFormatPreviewCommitted(sender);
            if (LineStyleCombo.SelectedItem is ComboBoxItem { Tag: string tag })
                ApplySelectedLineFormat(shape => shape.LineStyle = tag);
        }

        private void LineStartCapCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            MarkLineFormatPreviewCommitted(sender);
            if (LineStartCapCombo.SelectedItem is ComboBoxItem { Tag: string tag })
                ApplySelectedLineFormat(shape => shape.StartLineCap = tag);
        }

        private void LineEndCapCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            MarkLineFormatPreviewCommitted(sender);
            if (LineEndCapCombo.SelectedItem is ComboBoxItem { Tag: string tag })
                ApplySelectedLineFormat(shape => shape.EndLineCap = tag);
        }

        private void LineOptionCombo_DropDownOpened(object sender, EventArgs e)
        {
            if (sender is not ComboBox combo || _selectedShape == null ||
                !IsLineShapeKind(_selectedShape.Shape.Kind)) return;
            var shape = _selectedShape.Shape;
            _lineFormatPreview = new LineFormatPreviewState(
                _selectedShape,
                shape.StrokeThickness,
                shape.LineStyle,
                shape.StartLineCap,
                shape.EndLineCap);
            _lineFormatPreviewCombo = combo;
            _lineFormatPreviewCommitted = false;
        }

        private void LineOptionCombo_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not ComboBox combo || !combo.IsDropDownOpen ||
                _lineFormatPreview == null || _lineFormatPreviewCombo != combo ||
                e.OriginalSource is not DependencyObject source) return;
            if (ItemsControl.ContainerFromElement(combo, source) is not ComboBoxItem item)
                return;

            RestoreLineFormatPreviewValues(render: false);
            var shape = _lineFormatPreview.Placed.Shape;
            if (combo == LineThicknessCombo &&
                double.TryParse(item.Content?.ToString(), out double thickness))
                shape.StrokeThickness = Math.Clamp(thickness, 0.5, 20);
            else if (item.Tag is string tag)
            {
                if (combo == LineStyleCombo) shape.LineStyle = tag;
                else if (combo == LineStartCapCombo) shape.StartLineCap = tag;
                else if (combo == LineEndCapCombo) shape.EndLineCap = tag;
            }
            RenderShape(_lineFormatPreview.Placed);
        }

        private void LineOptionCombo_DropDownClosed(object sender, EventArgs e)
        {
            if (sender is not ComboBox combo || _lineFormatPreviewCombo != combo)
                return;
            if (!_lineFormatPreviewCommitted)
                RestoreLineFormatPreviewValues(render: true);
            _lineFormatPreview = null;
            _lineFormatPreviewCombo = null;
            _lineFormatPreviewCommitted = false;
        }

        private void MarkLineFormatPreviewCommitted(object sender)
        {
            if (sender is ComboBox combo && combo.IsDropDownOpen &&
                _lineFormatPreviewCombo == combo)
                _lineFormatPreviewCommitted = true;
        }

        private void RestoreLineFormatPreviewValues(bool render)
        {
            if (_lineFormatPreview == null) return;
            var shape = _lineFormatPreview.Placed.Shape;
            shape.StrokeThickness = _lineFormatPreview.Thickness;
            shape.LineStyle = _lineFormatPreview.LineStyle;
            shape.StartLineCap = _lineFormatPreview.StartCap;
            shape.EndLineCap = _lineFormatPreview.EndCap;
            if (render) RenderShape(_lineFormatPreview.Placed);
        }

        private Device EnsureEditableDevice(PlacedDevice placed)
        {
            placed.Instance.Override ??= CloneDevice(placed.TemplateDevice);
            return placed.Instance.Override;
        }

        private void ApplyTextFormat(Action<CanvasShape> shapeChange, Action<Device> deviceChange)
        {
            if (_updatingTextFormatPalette) return;

            if (_selectedShape != null)
            {
                shapeChange(_selectedShape.Shape);
                _selectedShape.Shape.TextRtfBase64 = "";
                RenderShape(_selectedShape);
            }
            else if (_selectedSet.Count > 0)
            {
                var changed = new List<(PlacedDevice Placed, string OriginalName)>();
                foreach (var placed in _selectedSet)
                {
                    var device = EnsureEditableDevice(placed);
                    string originalName = placed.TemplateDevice.Name;
                    deviceChange(device);
                    device.NameRtfBase64 = "";
                    changed.Add((placed, originalName));
                    RenderDevice(placed);
                }
                foreach (var group in changed.GroupBy(item => item.Placed.TemplateDevice.Id))
                {
                    var item = group.First();
                    SynchronizeDeviceSharedData(
                        item.Placed.TemplateDevice, item.Placed.EffectiveDevice,
                        item.OriginalName, replacePorts: false);
                }
            }
            else
            {
                return;
            }

            _isDirty = true;
            UpdateTextFormatPalette();
        }

        private void ApplyFontFamilyFromPalette()
        {
            if (_updatingTextFormatPalette) return;
            string family = AppFonts.NormalizeFamilyName(
                FontFamilyCombo.SelectedItem as string
                ?? FontFamilyCombo.Text);
            if (string.IsNullOrWhiteSpace(family)) return;
            if (_activeTextEditor != null)
            {
                _activeTextEditor.Selection.ApplyPropertyValue(
                    TextElement.FontFamilyProperty, AppFonts.Resolve(family));
                _isDirty = true;
                return;
            }
            ApplyTextFormat(
                shape => shape.FontFamily = family,
                device => device.NameFontFamily = family);
        }

        private void FontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => ApplyFontFamilyFromPalette();

        private void FontFamilyCombo_LostKeyboardFocus(
            object sender, KeyboardFocusChangedEventArgs e)
            => ApplyFontFamilyFromPalette();

        private void ApplyFontSizeFromPalette()
        {
            if (_updatingTextFormatPalette) return;
            string value = FontSizeCombo.SelectedItem?.ToString()
                           ?? FontSizeCombo.Text;
            if (!double.TryParse(value.Trim(), out double size)) return;
            size = Math.Max(6, Math.Min(200, size));
            if (_activeTextEditor != null)
            {
                _activeTextEditor.Selection.ApplyPropertyValue(
                    TextElement.FontSizeProperty, size);
                _isDirty = true;
                return;
            }
            ApplyTextFormat(
                shape => shape.FontSize = size,
                device => device.NameFontSize = size);
        }

        private void FontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => ApplyFontSizeFromPalette();

        private void FontSizeCombo_LostKeyboardFocus(
            object sender, KeyboardFocusChangedEventArgs e)
            => ApplyFontSizeFromPalette();

        private void FontSizeCombo_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            ApplyFontSizeFromPalette();
            Keyboard.ClearFocus();
            e.Handled = true;
        }

        private void FontSizeStepButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string stepText } ||
                !double.TryParse(stepText, out double step))
                return;

            string currentText = FontSizeCombo.SelectedItem?.ToString()
                                 ?? FontSizeCombo.Text;
            if (!double.TryParse(currentText.Trim(), out double current))
                current = 10;

            double next = Math.Max(6, Math.Min(200, current + step));
            _updatingTextFormatPalette = true;
            FontSizeCombo.SelectedItem = null;
            FontSizeCombo.Text = next.ToString("0.#");
            _updatingTextFormatPalette = false;
            ApplyFontSizeFromPalette();
        }

        private void TextStyleButton_Click(object sender, RoutedEventArgs e)
        {
            bool bold = TextBoldButton.IsChecked == true;
            bool italic = TextItalicButton.IsChecked == true;
            bool underline = TextUnderlineButton.IsChecked == true;
            if (_activeTextEditor != null)
            {
                var selection = _activeTextEditor.Selection;
                selection.ApplyPropertyValue(
                    TextElement.FontWeightProperty,
                    bold ? FontWeights.Bold : FontWeights.Normal);
                selection.ApplyPropertyValue(
                    TextElement.FontStyleProperty,
                    italic ? FontStyles.Italic : FontStyles.Normal);
                selection.ApplyPropertyValue(
                    Inline.TextDecorationsProperty,
                    underline ? TextDecorations.Underline : null);
                _isDirty = true;
                return;
            }
            ApplyTextFormat(
                shape =>
                {
                    shape.TextBold = bold;
                    shape.TextItalic = italic;
                    shape.TextUnderline = underline;
                },
                device =>
                {
                    device.NameBold = bold;
                    device.NameItalic = italic;
                    device.NameUnderline = underline;
                });
        }

        private void TextColorButton_Click(object sender, RoutedEventArgs e)
        {
            string currentHex = _activeTextEditor != null
                ? RichTextSupport.BrushToHex(
                    _activeTextEditor.Selection.GetPropertyValue(
                        TextElement.ForegroundProperty), "#000000")
                : _selectedShape != null
                ? _selectedShape.Shape.TextColorHex
                : _selectedSet.FirstOrDefault()?.EffectiveDevice.NameColorHex
                  ?? "#000000";
            if (!ColorPickerWindow.TryPick(
                    this, currentHex, out string hex, "글자 색"))
                return;

            if (_activeTextEditor != null)
            {
                _activeTextEditor.Selection.ApplyPropertyValue(
                    TextElement.ForegroundProperty,
                    RichTextSupport.ToBrush(hex));
                TextColorSwatch.Fill = RichTextSupport.ToBrush(hex);
                _isDirty = true;
                return;
            }
            ApplyTextFormat(
                shape => shape.TextColorHex = hex,
                device => device.NameColorHex = hex);
        }

        private void TextAlignmentButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton { Tag: string alignment }) return;
            if (_activeTextEditor != null)
            {
                _activeTextEditor.Selection.ApplyPropertyValue(
                    Paragraph.TextAlignmentProperty,
                    ParseTextAlignment(alignment));
                _activeTextEditor.Focus();
                _isDirty = true;
                return;
            }
            ApplyTextFormat(
                shape => shape.TextAlignment = alignment,
                device => device.NameTextAlignment = alignment);
        }

        private void ObjectArrangeButton_Click(object sender, RoutedEventArgs e)
        {
            int selectedCount = GetArrangeTargets().Count;
            if (selectedCount < 2) return;

            var menu = new ContextMenu();
            void Add(string label, string operation, bool enabled = true)
            {
                var item = new MenuItem { Header = label, IsEnabled = enabled };
                item.Click += (_, __) => ArrangeSelectedObjects(operation);
                menu.Items.Add(item);
            }

            Add("왼쪽 맞춤", "Left");
            Add("가로 가운데 맞춤", "Center");
            Add("오른쪽 맞춤", "Right");
            menu.Items.Add(new Separator());
            Add("위쪽 맞춤", "Top");
            Add("세로 가운데 맞춤", "Middle");
            Add("아래쪽 맞춤", "Bottom");
            menu.Items.Add(new Separator());
            Add("가로 간격 동일하게", "DistributeHorizontal", selectedCount >= 3);
            Add("세로 간격 동일하게", "DistributeVertical", selectedCount >= 3);

            var button = sender as Button ?? ObjectArrangeButton;
            button.ContextMenu = menu;
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }

        private List<ArrangeTarget> GetArrangeTargets()
        {
            var targets = new List<ArrangeTarget>();
            foreach (var placed in _selectedSet)
            {
                targets.Add(new ArrangeTarget
                {
                    Left = () => placed.Instance.X,
                    Top = () => placed.Instance.Y,
                    Width = () => placed.Instance.Width,
                    Height = () => placed.Instance.Height,
                    MoveTo = (x, y) =>
                    {
                        placed.Instance.X = SnapToDiagramGrid(x);
                        placed.Instance.Y = SnapToDiagramGrid(y);
                    }
                });
            }

            foreach (var placed in _selectedShapes)
            {
                targets.Add(new ArrangeTarget
                {
                    Left = () => ShapeGeometry.Bounds(placed.Shape).Left,
                    Top = () => ShapeGeometry.Bounds(placed.Shape).Top,
                    Width = () => ShapeGeometry.Bounds(placed.Shape).Width,
                    Height = () => ShapeGeometry.Bounds(placed.Shape).Height,
                    MoveTo = (x, y) =>
                    {
                        double dx = x - ShapeGeometry.Bounds(placed.Shape).Left;
                        double dy = y - ShapeGeometry.Bounds(placed.Shape).Top;
                        placed.Shape.X += dx;
                        placed.Shape.Y += dy;
                    }
                });
            }

            foreach (var layout in _selectedCablePorts)
            {
                targets.Add(new ArrangeTarget
                {
                    Left = () => layout.PortX,
                    Top = () => layout.PortY,
                    Width = () => 0,
                    Height = () => 0,
                    MoveTo = (x, y) =>
                    {
                        layout.ConnectedToPortKey = null;
                        layout.PortX = x;
                        layout.PortY = y;
                    }
                });
            }
            return targets;
        }

        private void ArrangeSelectedObjects(string operation)
        {
            var selected = GetArrangeTargets();
            if (selected.Count < 2) return;

            double minLeft = selected.Min(item => item.Left());
            double maxRight = selected.Max(item => item.Left() + item.Width());
            double minTop = selected.Min(item => item.Top());
            double maxBottom = selected.Max(item => item.Top() + item.Height());

            switch (operation)
            {
                case "Left":
                    foreach (var item in selected) item.MoveTo(minLeft, item.Top());
                    break;
                case "Center":
                    double centerX = (minLeft + maxRight) / 2;
                    foreach (var item in selected)
                        item.MoveTo(centerX - item.Width() / 2, item.Top());
                    break;
                case "Right":
                    foreach (var item in selected)
                        item.MoveTo(maxRight - item.Width(), item.Top());
                    break;
                case "Top":
                    foreach (var item in selected) item.MoveTo(item.Left(), minTop);
                    break;
                case "Middle":
                    double centerY = (minTop + maxBottom) / 2;
                    foreach (var item in selected)
                        item.MoveTo(item.Left(), centerY - item.Height() / 2);
                    break;
                case "Bottom":
                    foreach (var item in selected)
                        item.MoveTo(item.Left(), maxBottom - item.Height());
                    break;
                case "DistributeHorizontal":
                    if (selected.Count < 3) return;
                    var horizontal = selected.OrderBy(item => item.Left()).ToList();
                    double totalWidth = horizontal.Sum(item => item.Width());
                    double horizontalGap = (maxRight - minLeft - totalWidth) /
                                           (horizontal.Count - 1);
                    double nextX = minLeft;
                    foreach (var item in horizontal)
                    {
                        item.MoveTo(nextX, item.Top());
                        nextX += item.Width() + horizontalGap;
                    }
                    break;
                case "DistributeVertical":
                    if (selected.Count < 3) return;
                    var vertical = selected.OrderBy(item => item.Top()).ToList();
                    double totalHeight = vertical.Sum(item => item.Height());
                    double verticalGap = (maxBottom - minTop - totalHeight) /
                                         (vertical.Count - 1);
                    double nextY = minTop;
                    foreach (var item in vertical)
                    {
                        item.MoveTo(item.Left(), nextY);
                        nextY += item.Height() + verticalGap;
                    }
                    break;
                default:
                    return;
            }

            foreach (var item in _selectedSet) RenderDevice(item);
            foreach (var item in _selectedShapes) RenderShape(item);
            RenderAllCables();
            NormalizeCanvasOriginForNegativeContent();
            UpdateCanvasExtent();
            _isDirty = true;
            StatusText.Text = $"선택한 개체 {selected.Count}개 정렬됨";
        }

        // ================= 단축키 =================
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!ConfirmSaveBeforeReplacing("프로그램을 종료"))
                e.Cancel = true;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return; // 텍스트 입력 중엔 단축키 무시

            bool isArrowKey = e.Key is Key.Left or Key.Right or Key.Up or Key.Down;
            if (_keyboardNudgeActive &&
                (!isArrowKey || e.Key != _keyboardNudgeDirection))
                EndKeyboardNudge();

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
            {
                Menu_Undo(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
            {
                Menu_Redo(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
            {
                Menu_SaveProject(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            // 구역 설정 중에는 저장/실행 취소를 제외한 키 입력이 뒤쪽
            // 장치·케이블·도형의 선택이나 이동으로 전달되지 않게 한다.
            if (_isZoneSettingMode)
            {
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
            {
                SelectShape(null);
                DeselectCable();
                ClearSelection();
                ClearCablePortSelection(render: false);
                SelectAdd(_placed);
                SelectAddShapes(_placedShapes);
                foreach (var layout in _placedCables.SelectMany(pc => AllPortLayouts(pc.Instance)))
                    _selectedCablePorts.Add(layout);
                RenderAllCables();
                UpdateTextFormatPalette();
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D)
            {
                if (_selectedShape != null) DuplicateShape(_selectedShape);
                else if (_selectedSet.Count > 0) DuplicateSelected();
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
            {
                Menu_Copy(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
            {
                Menu_Paste(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (_adjustingShape != null)
                {
                    CancelShapeAdjustment(); e.Handled = true; return;
                }
                if (_armedShapeKind != null)
                {
                    CancelShapeInsertion();
                    e.Handled = true;
                    return;
                }
                SelectShape(null);
                DeselectCable();
                ClearSelection();
                ClearCablePortSelection();
                ClearSelectedDevicePort();
                e.Handled = true;
                return;
            }

            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                if (_selectedDevicePortPlaced != null &&
                    _selectedDevicePort != null &&
                    e.Key is Key.Left or Key.Right)
                {
                    MoveSelectedDevicePortAroundPerimeter(
                        clockwise: e.Key == Key.Right);
                    e.Handled = true;
                    return;
                }

                // 장치와 자유 케이블 커넥터는 키보드 이동으로도 격자선에서
                // 벗어나지 않는다. 일반 도형만 선택한 경우에는 미세 이동을 유지한다.
                double step = _selectedSet.Count > 0 ||
                              _selectedCablePorts.Count > 0
                    ? (Keyboard.Modifiers == ModifierKeys.Shift
                        ? DiagramGridSize * 5
                        : DiagramGridSize)
                    : (Keyboard.Modifiers == ModifierKeys.Shift ? 10 : 1);
                double dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
                double dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                bool moved = false;

                if (_selectedShapes.Count > 0 || _selectedSet.Count > 0 ||
                    _selectedCablePorts.Count > 0)
                {
                    BeginOrContinueKeyboardNudge(e.Key);
                    TranslateCurrentSelection(dx, dy);
                    moved = true;
                }

                if (moved) { UpdateCanvasExtent(); e.Handled = true; }
                return;
            }

            if (e.Key == Key.Delete)
            {
                if (_selectedShapes.Count > 0) { DeleteSelectedShapes(); e.Handled = true; }
                else if (_selectedCable != null) { DeleteCable(_selectedCable); e.Handled = true; }
                else if (_selectedSet.Count > 0) { DeleteSelected(); e.Handled = true; }
            }
        }

        private void DuplicateShape(PlacedShape placed)
        {
            var sources = _selectedShapes.Contains(placed) && _selectedShapes.Count > 1
                ? _selectedShapes.Select(p => p.Shape).ToList()
                : ShapeGroup(placed).Select(p => p.Shape).ToList();
            AddShapeCopies(sources);
        }

        // ================= 확대/축소 =================
        private void MainScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            double factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
            ApplyZoom(CanvasScale.ScaleX * factor);
            e.Handled = true;
        }

        private void Menu_ZoomIn(object sender, RoutedEventArgs e) => ApplyZoom(CanvasScale.ScaleX * ZoomStep);
        private void Menu_ZoomOut(object sender, RoutedEventArgs e) => ApplyZoom(CanvasScale.ScaleX / ZoomStep);
        private void Menu_ZoomReset(object sender, RoutedEventArgs e) => ApplyZoom(1.0);

        private void Menu_GridVisibility(object sender, RoutedEventArgs e)
        {
            MainCanvas.Background = MenuGridVisible.IsChecked
                ? (Brush)FindResource("DiagramGridBrush")
                : Brushes.White;
            StatusText.Text = MenuGridVisible.IsChecked
                ? $"격자 표시 켜짐 — {DiagramGridSize:0}px 간격"
                : "격자 표시 꺼짐 — 장치의 격자 배치 규칙은 계속 적용됨";
        }

        private void ApplyZoom(double value)
        {
            value = Math.Max(MinZoom, Math.Min(MaxZoom, value));
            CanvasScale.ScaleX = value;
            CanvasScale.ScaleY = value;
            StatusText.Text = $"확대/축소: {value * 100:0}%";
        }

        // ================= 휠버튼 이동 =================
        private void MainScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.MiddleButton != MouseButtonState.Pressed) return;
            _isPanning = true;
            _panStartMouse = e.GetPosition(MainScrollViewer);
            _panStartH = MainScrollViewer.HorizontalOffset;
            _panStartV = MainScrollViewer.VerticalOffset;
            MainScrollViewer.Cursor = Cursors.ScrollAll;
            MainScrollViewer.CaptureMouse();
            e.Handled = true;
        }

        private void MainScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning) return;
            if (e.MiddleButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(MainScrollViewer);
            MainScrollViewer.ScrollToHorizontalOffset(_panStartH - (pos.X - _panStartMouse.X));
            MainScrollViewer.ScrollToVerticalOffset(_panStartV - (pos.Y - _panStartMouse.Y));
        }

        private void MainScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.MiddleButton != MouseButtonState.Released) return;
            _isPanning = false;
            MainScrollViewer.Cursor = Cursors.Arrow;
            MainScrollViewer.ReleaseMouseCapture();
        }

        // ================= 용지 =================
        private void BuildPaperSizeMenu()
        {
            foreach (var (paper, name) in new[]
                     {
                         (PaperSize.A3_Landscape, "가로"),
                         (PaperSize.A3_Portrait, "세로")
                     })
            {
                var item = new MenuItem
                {
                    Header = name,
                    IsCheckable = true,
                    Tag = paper
                };
                item.Click += PaperSizeMenu_Click;
                MenuPaperSize.Items.Add(item);
                _paperMenuItems.Add(item);
            }
            UpdatePaperMenuChecks();
        }

        private void PaperSizeMenu_Click(object sender, RoutedEventArgs e)
        {
            _currentPaper = (PaperSize)((MenuItem)sender).Tag;
            if (_currentSheetIndex >= 0 && _currentSheetIndex < _sheets.Count)
            {
                var sheet = _sheets[_currentSheetIndex];
                sheet.PaperSize = _currentPaper;
                sheet.KeepPaperAspectRatio = false;
                sheet.PageScale = 1;
            }
            UpdatePaperMenuChecks();
            ApplyPaperSize();
        }

        private void UpdatePaperMenuChecks()
        {
            foreach (var item in _paperMenuItems)
                item.IsChecked = (PaperSize)item.Tag == _currentPaper;
        }

        private (double W, double H) GetBasePaperPx()
        {
            bool landscape = _currentPaper is PaperSize.A3_Landscape or
                PaperSize.A4_Landscape;
            var physical = PaperSizeInfo.GetSizeMm(
                landscape ? PaperSize.A3_Landscape : PaperSize.A3_Portrait);
            double scale = 1;
            if (_currentSheetIndex >= 0 && _currentSheetIndex < _sheets.Count)
                scale = Math.Max(0.1, _sheets[_currentSheetIndex].PageScale);
            return (AlignPaperExtentToGrid(physical.WidthMm * PxPerMm * scale),
                    AlignPaperExtentToGrid(physical.HeightMm * PxPerMm * scale));
        }

        private static double AlignPaperExtentToGrid(double value)
            => Math.Max(DiagramGridSize,
                Math.Round(value / DiagramGridSize,
                    MidpointRounding.AwayFromZero) * DiagramGridSize);

        private (double W, double H) GetPhysicalPaperPx()
        {
            bool landscape = _currentPaper is PaperSize.A3_Landscape or
                PaperSize.A4_Landscape;
            var physical = PaperSizeInfo.GetSizeMm(
                landscape ? PaperSize.A3_Landscape : PaperSize.A3_Portrait);
            return (AlignPaperExtentToGrid(physical.WidthMm * PxPerMm),
                    AlignPaperExtentToGrid(physical.HeightMm * PxPerMm));
        }

        private void ApplyPaperSize()
        {
            UpdateCanvasExtent();
        }

        private void DrawPageGuide()
        {
            foreach (var guide in MainCanvas.Children
                         .OfType<FrameworkElement>()
                         .Where(item => Equals(item.Tag, PageGuideTag))
                         .ToList())
                MainCanvas.Children.Remove(guide);

            var (baseW, baseH) = GetBasePaperPx();
            int columns = Math.Max(1,
                (int)Math.Round(MainCanvas.Width / baseW));
            int rows = Math.Max(1,
                (int)Math.Round(MainCanvas.Height / baseH));

            for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                var guide = new Rectangle
                {
                    Width = baseW,
                    Height = baseH,
                    Stroke = Brushes.Gray,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 4 },
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false,
                    Tag = PageGuideTag
                };
                Canvas.SetLeft(guide, column * baseW);
                Canvas.SetTop(guide, row * baseH);
                Panel.SetZIndex(guide, -1);
                MainCanvas.Children.Add(guide);
                _pageGuide ??= guide;
            }
        }

        private void UpdateCanvasExtent()
        {
            if (!_isPdfExportRendering) TrimUnusedLeadingPages();
            var (baseW, baseH) = GetBasePaperPx();
            int targetColumns = 1;
            int targetRows = 1;
            if (_currentSheetIndex >= 0 && _currentSheetIndex < _sheets.Count)
            {
                targetColumns = Math.Max(1,
                    _sheets[_currentSheetIndex].TargetPageColumns);
                targetRows = Math.Max(1,
                    _sheets[_currentSheetIndex].TargetPageRows);
            }
            double requiredW = baseW * targetColumns;
            double requiredH = baseH * targetRows;

            foreach (var p in _placed)
            {
                double right = p.Instance.X + p.Instance.Width + PortWidth;
                double bottom = p.Instance.Y + p.Instance.Height;
                requiredW = Math.Max(requiredW, right + ExtendMargin);
                requiredH = Math.Max(requiredH, bottom + ExtendMargin);
            }

            foreach (var pc in _placedCables)
            {
                var (minX, minY, maxX, maxY) = GetCableBounds(pc.Instance);
                requiredW = Math.Max(requiredW, maxX + ExtendMargin);
                requiredH = Math.Max(requiredH, maxY + ExtendMargin);
            }

            foreach (var ps in _placedShapes)
            {
                // Line 은 Width/Height 가 음수일 수 있으므로(대각선 방향), 항상 두 끝점의 최대값으로 계산한다.
                var bounds = ShapeGeometry.Bounds(ps.Shape);
                double right = bounds.Right;
                double bottom = bounds.Bottom;
                requiredW = Math.Max(requiredW, right + ExtendMargin);
                requiredH = Math.Max(requiredH, bottom + ExtendMargin);
            }

            foreach (var pz in _placedZones)
            {
                requiredW = Math.Max(requiredW,
                    pz.Zone.X + pz.Zone.Width + ExtendMargin);
                requiredH = Math.Max(requiredH,
                    pz.Zone.Y + pz.Zone.Height + ExtendMargin);
            }

            requiredW = Math.Ceiling(requiredW / baseW) * baseW;
            requiredH = Math.Ceiling(requiredH / baseH) * baseH;

            MainCanvas.Width = requiredW;
            MainCanvas.Height = requiredH;
            DrawPageGuide();

            bool extended = requiredW > baseW + 0.5 || requiredH > baseH + 0.5;
            int columns = Math.Max(1, (int)Math.Round(requiredW / baseW));
            int rows = Math.Max(1, (int)Math.Round(requiredH / baseH));
            StatusText.Text = extended
                ? $"용지 {rows}행 × {columns}열 — 기본 {PaperSizeInfo.GetDisplayName(_currentPaper)}"
                : $"용지: {PaperSizeInfo.GetDisplayName(_currentPaper)}   |   장치 {_placed.Count}개   |   선택 {_selectedSet.Count}개";
        }

        private (double MinX, double MinY, double MaxX, double MaxY) GetContentBounds()
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = 0, maxY = 0;

            foreach (var placed in _placed)
            {
                minX = Math.Min(minX, placed.Instance.X - PortWidth);
                minY = Math.Min(minY, placed.Instance.Y - PortWidth);
                maxX = Math.Max(maxX,
                    placed.Instance.X + placed.Instance.Width + PortWidth);
                maxY = Math.Max(maxY,
                    placed.Instance.Y + placed.Instance.Height + PortWidth);
            }
            foreach (var placed in _placedShapes)
            {
                var bounds = ShapeGeometry.Bounds(placed.Shape);
                minX = Math.Min(minX, bounds.Left);
                minY = Math.Min(minY, bounds.Top);
                maxX = Math.Max(maxX, bounds.Right);
                maxY = Math.Max(maxY, bounds.Bottom);
            }
            foreach (var placed in _placedZones)
            {
                minX = Math.Min(minX, placed.Zone.X);
                minY = Math.Min(minY, placed.Zone.Y);
                maxX = Math.Max(maxX, placed.Zone.X + placed.Zone.Width);
                maxY = Math.Max(maxY, placed.Zone.Y + placed.Zone.Height);
            }
            foreach (var placed in _placedCables)
            {
                var bounds = GetCableBounds(placed.Instance);
                minX = Math.Min(minX, bounds.MinX - PortWidth);
                minY = Math.Min(minY, bounds.MinY - PortWidth);
                maxX = Math.Max(maxX, bounds.MaxX + PortWidth);
                maxY = Math.Max(maxY, bounds.MaxY + PortWidth);
            }

            return minX == double.MaxValue
                ? (0, 0, 0, 0)
                : (minX, minY, maxX, maxY);
        }

        private void TranslateEntireDiagram(double dx, double dy)
        {
            if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01) return;
            foreach (var placed in _placed)
            {
                placed.Instance.X += dx;
                placed.Instance.Y += dy;
                RenderDevice(placed);
            }
            foreach (var placed in _placedShapes)
            {
                placed.Shape.X += dx;
                placed.Shape.Y += dy;
                RenderShape(placed);
            }
            foreach (var placed in _placedZones)
            {
                placed.Zone.X += dx;
                placed.Zone.Y += dy;
                RenderZone(placed);
            }
            foreach (var placed in _placedCables)
            {
                foreach (var layout in AllPortLayouts(placed.Instance))
                {
                    layout.PortX += dx;
                    layout.PortY += dy;
                }
                foreach (var bend in placed.Instance.RootBends)
                {
                    bend.X += dx;
                    bend.Y += dy;
                }
                foreach (var bend in placed.Instance.Branches.SelectMany(branch => branch.Bends))
                {
                    bend.X += dx;
                    bend.Y += dy;
                }
            }
            RenderAllCables();
        }

        private void TrimUnusedLeadingPages()
        {
            if (_currentSheetIndex < 0 || _currentSheetIndex >= _sheets.Count)
                return;
            var sheet = _sheets[_currentSheetIndex];
            if (sheet.LeadingPageColumns <= 0 && sheet.LeadingPageRows <= 0)
                return;

            var bounds = GetContentBounds();
            var (baseW, baseH) = GetBasePaperPx();
            int removeColumns = Math.Min(sheet.LeadingPageColumns,
                Math.Max(0, (int)Math.Floor((bounds.MinX - ExtendMargin) / baseW)));
            int removeRows = Math.Min(sheet.LeadingPageRows,
                Math.Max(0, (int)Math.Floor((bounds.MinY - ExtendMargin) / baseH)));
            if (removeColumns == 0 && removeRows == 0) return;

            double dx = -removeColumns * baseW;
            double dy = -removeRows * baseH;
            sheet.LeadingPageColumns -= removeColumns;
            sheet.LeadingPageRows -= removeRows;
            TranslateEntireDiagram(dx, dy);

            double oldHorizontal = MainScrollViewer.HorizontalOffset;
            double oldVertical = MainScrollViewer.VerticalOffset;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MainScrollViewer.ScrollToHorizontalOffset(Math.Max(0,
                    oldHorizontal + dx * CanvasScale.ScaleX));
                MainScrollViewer.ScrollToVerticalOffset(Math.Max(0,
                    oldVertical + dy * CanvasScale.ScaleY));
            }));
        }

        /// <summary>
        /// Canvas/ScrollViewer는 음수 좌표를 스크롤할 수 없으므로 왼쪽이나 위로
        /// 넘어간 내용이 있으면 도면 전체를 오른쪽/아래로 같은 거리만큼 민다.
        /// 상대 위치와 케이블 모양은 그대로이고, 결과적으로 왼쪽·위쪽에 새 용지
        /// 공간이 생긴 것처럼 동작한다.
        /// </summary>
        private bool NormalizeCanvasOriginForNegativeContent()
        {
            var bounds = GetContentBounds();
            double minX = bounds.MinX;
            double minY = bounds.MinY;
            var (baseW, baseH) = GetBasePaperPx();
            double dx = minX < 0
                ? Math.Ceiling((-minX + ExtendMargin) / baseW) * baseW
                : 0;
            double dy = minY < 0
                ? Math.Ceiling((-minY + ExtendMargin) / baseH) * baseH
                : 0;
            if (dx == 0 && dy == 0) return false;

            if (_currentSheetIndex >= 0 && _currentSheetIndex < _sheets.Count)
            {
                var sheet = _sheets[_currentSheetIndex];
                sheet.LeadingPageColumns += (int)Math.Round(dx / baseW);
                sheet.LeadingPageRows += (int)Math.Round(dy / baseH);
            }
            TranslateEntireDiagram(dx, dy);

            // 왼쪽/위에 용지를 붙인 직후 현재 화면이 튀지 않도록 새 용지 크기만큼
            // 스크롤 위치도 함께 보정한다. 개체 간 상대 위치는 전혀 바뀌지 않는다.
            double oldHorizontal = MainScrollViewer.HorizontalOffset;
            double oldVertical = MainScrollViewer.VerticalOffset;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MainScrollViewer.ScrollToHorizontalOffset(
                    oldHorizontal + dx * CanvasScale.ScaleX);
                MainScrollViewer.ScrollToVerticalOffset(
                    oldVertical + dy * CanvasScale.ScaleY);
            }));
            return true;
        }

        private void Menu_FitContentToA3Aspect(object sender, RoutedEventArgs e)
        {
            if (_currentSheetIndex < 0 || _currentSheetIndex >= _sheets.Count)
                return;

            var sheet = _sheets[_currentSheetIndex];
            var dialog = new PageFitWindow(
                Math.Max(1, sheet.TargetPageRows),
                Math.Max(1, sheet.TargetPageColumns),
                CanKeepCurrentLayoutWithinPageGrid)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true) return;

            // 사용자가 지정한 페이지 수가 최우선이다. "배치 유지"를 골랐더라도
            // 현재 내용이 목표 영역을 넘는다면 적용을 막거나 페이지를 다시 늘리지
            // 않고, 그때만 배치를 자동으로 목표 영역 안에 맞춘다.
            bool repositionLayout = dialog.RepositionLayout ||
                                    !CanKeepCurrentLayoutWithinPageGrid(
                                        dialog.PageRows, dialog.PageColumns);

            // 새 방식은 한 페이지 자체를 임의 배율로 키우지 않는다. 실제 A3 한 장을
            // 20px 격자 배수로 유지하고, 필요한 행/열 수만 늘린다.
            sheet.PaperSize = _currentPaper;
            sheet.KeepPaperAspectRatio = false;
            sheet.PageScale = 1;
            sheet.TargetPageRows = dialog.PageRows;
            sheet.TargetPageColumns = dialog.PageColumns;
            sheet.LeadingPageColumns = 0;
            sheet.LeadingPageRows = 0;

            if (repositionLayout)
                RepositionDiagramToPageGrid(dialog.PageRows, dialog.PageColumns);

            UpdatePaperMenuChecks();
            ApplyPaperSize();
            StatusText.Text = repositionLayout
                ? $"배치를 {dialog.PageRows}행 × {dialog.PageColumns}열 페이지에 맞췄습니다."
                : $"배치는 유지하고 용지를 {dialog.PageRows}행 × {dialog.PageColumns}열로 지정했습니다.";
            _isDirty = true;
        }

        private bool CanKeepCurrentLayoutWithinPageGrid(
            int pageRows, int pageColumns)
        {
            var bounds = GetContentBounds();
            if (bounds.MaxX <= bounds.MinX && bounds.MaxY <= bounds.MinY)
                return true;

            var (pageW, pageH) = GetPhysicalPaperPx();
            double targetW = pageW * Math.Max(1, pageColumns);
            double targetH = pageH * Math.Max(1, pageRows);
            return bounds.MinX >= -0.01 && bounds.MinY >= -0.01 &&
                   bounds.MaxX + ExtendMargin <= targetW + 0.01 &&
                   bounds.MaxY + ExtendMargin <= targetH + 0.01;
        }

        private void RepositionDiagramToPageGrid(int pageRows, int pageColumns)
        {
            var bounds = GetContentBounds();
            if (bounds.MaxX <= bounds.MinX && bounds.MaxY <= bounds.MinY) return;

            var (baseW, baseH) = GetBasePaperPx();
            double margin = DiagramGridSize * 2;
            double targetW = Math.Max(DiagramGridSize,
                baseW * Math.Max(1, pageColumns) - margin * 2);
            double targetH = Math.Max(DiagramGridSize,
                baseH * Math.Max(1, pageRows) - margin * 2);
            double sourceW = Math.Max(1, bounds.MaxX - bounds.MinX);
            double sourceH = Math.Max(1, bounds.MaxY - bounds.MinY);

            double MapX(double value) => margin +
                (value - bounds.MinX) / sourceW * targetW;
            double MapY(double value) => margin +
                (value - bounds.MinY) / sourceH * targetH;
            double MapCableCoordinate(double original, double mapped)
            {
                // 정상 케이블은 중앙선(offset=0)을 유지한다. 좁은 통로에서
                // 여러 케이블이 나눠 쓰던 소차선은 중앙선과의 상대 오프셋을
                // 보존해 페이지 맞춤 뒤 다시 한 선으로 합쳐지지 않게 한다.
                double oldCenter = SnapToDiagramGridMidpoint(original);
                double laneOffset = original - oldCenter;
                if (Math.Abs(laneOffset) >= DiagramGridSize * 0.45)
                    laneOffset = 0; // 구버전 격자 경계 좌표는 중앙선으로 이관
                return SnapToDiagramGridMidpoint(mapped) + laneOffset;
            }

            foreach (var placed in _placed)
            {
                double maximumX = Math.Max(margin,
                    baseW * pageColumns - margin - placed.Instance.Width);
                double maximumY = Math.Max(margin,
                    baseH * pageRows - margin - placed.Instance.Height);
                placed.Instance.X = SnapToDiagramGrid(Math.Clamp(
                    MapX(placed.Instance.X), margin, maximumX));
                placed.Instance.Y = SnapToDiagramGrid(Math.Clamp(
                    MapY(placed.Instance.Y), margin, maximumY));
                RenderDevice(placed);
            }

            foreach (var placed in _placedCables)
            {
                foreach (var layout in AllPortLayouts(placed.Instance))
                {
                    Point snappedPort = SnapCablePortToGridFace(
                        new Point(MapX(layout.PortX), MapY(layout.PortY)),
                        layout.FixedFacing);
                    layout.PortX = snappedPort.X;
                    layout.PortY = snappedPort.Y;
                }
                foreach (var bend in placed.Instance.RootBends)
                {
                    // 케이블은 격자선 교차점이 아니라 각 격자 칸의 중앙
                    // 통로를 사용한다. 페이지 맞춤 뒤에도 이 규칙을 복원한다.
                    bend.X = MapCableCoordinate(bend.X, MapX(bend.X));
                    bend.Y = MapCableCoordinate(bend.Y, MapY(bend.Y));
                }
                foreach (var bend in placed.Instance.Branches.SelectMany(branch => branch.Bends))
                {
                    bend.X = MapCableCoordinate(bend.X, MapX(bend.X));
                    bend.Y = MapCableCoordinate(bend.Y, MapY(bend.Y));
                }
            }

            foreach (var placed in _placedShapes)
            {
                placed.Shape.X = MapX(placed.Shape.X);
                placed.Shape.Y = MapY(placed.Shape.Y);
                RenderShape(placed);
            }
            foreach (var placed in _placedZones)
            {
                double left = SnapToDiagramGrid(MapX(placed.Zone.X));
                double top = SnapToDiagramGrid(MapY(placed.Zone.Y));
                double right = SnapToDiagramGrid(
                    MapX(placed.Zone.X + placed.Zone.Width));
                double bottom = SnapToDiagramGrid(
                    MapY(placed.Zone.Y + placed.Zone.Height));
                placed.Zone.X = Math.Min(left, right);
                placed.Zone.Y = Math.Min(top, bottom);
                placed.Zone.Width = Math.Max(
                    DiagramGridSize, Math.Abs(right - left));
                placed.Zone.Height = Math.Max(
                    DiagramGridSize, Math.Abs(bottom - top));
                RenderZone(placed);
            }
            RenderAllCables();
        }

        // ================= 장치 복제/복사 헬퍼 =================
        private static Device CloneDevice(Device src) => new Device
        {
            Id = src.Id,
            Name = src.Name,
            NameRtfBase64 = src.NameRtfBase64,
            Kind = src.Kind,
            CustomKindName = src.CustomKindName,
            CategoryColorHex = src.CategoryColorHex,
            Ports = src.Ports.Select(p => new Port
            {
                Id = p.Id, Designator = p.Designator, Side = p.Side, Position = p.Position,
                ConnType = p.ConnType, ConnectorCatalogKey = p.ConnectorCatalogKey,
                ShowConnType = p.ShowConnType,
                ShowLabel = p.ShowLabel,
                Description = p.Description,
                LabelFontSize = p.LabelFontSize, LabelColorHex = p.LabelColorHex,
                HasCustomLabelPosition = p.HasCustomLabelPosition,
                LabelOffsetX = p.LabelOffsetX, LabelOffsetY = p.LabelOffsetY
            }).ToList(),
            Features = new Dictionary<string, string>(src.Features),
            NameBold = src.NameBold,
            NameItalic = src.NameItalic,
            NameUnderline = src.NameUnderline,
            NameFontSize = src.NameFontSize,
            NameColorHex = src.NameColorHex,
            NameFontFamily = src.NameFontFamily,
            NameTextAlignment = src.NameTextAlignment,
            HasCustomNamePosition = src.HasCustomNamePosition,
            NamePositionX = src.NamePositionX,
            NamePositionY = src.NamePositionY,
            BackgroundColorHex = src.BackgroundColorHex,
            BackgroundImageBase64 = src.BackgroundImageBase64,
            BackgroundImageFileName = src.BackgroundImageFileName,
            BackgroundImageStretch = src.BackgroundImageStretch,
            HasExplicitBackgroundImageStretch = src.HasExplicitBackgroundImageStretch,
            BackgroundImageOpacity = src.BackgroundImageOpacity
        };

        private static void CopyDeviceInto(Device target, Device source)
        {
            target.Name = source.Name;
            target.NameRtfBase64 = source.NameRtfBase64;
            target.Kind = source.Kind;
            target.CustomKindName = source.CustomKindName;
            target.CategoryColorHex = source.CategoryColorHex;
            target.Ports = source.Ports.Select(p => new Port
            {
                Id = p.Id, Designator = p.Designator, Side = p.Side, Position = p.Position,
                ConnType = p.ConnType, ConnectorCatalogKey = p.ConnectorCatalogKey,
                ShowConnType = p.ShowConnType,
                ShowLabel = p.ShowLabel,
                Description = p.Description,
                LabelFontSize = p.LabelFontSize, LabelColorHex = p.LabelColorHex,
                HasCustomLabelPosition = p.HasCustomLabelPosition,
                LabelOffsetX = p.LabelOffsetX, LabelOffsetY = p.LabelOffsetY
            }).ToList();
            target.Features = new Dictionary<string, string>(source.Features);
            target.NameBold = source.NameBold;
            target.NameItalic = source.NameItalic;
            target.NameUnderline = source.NameUnderline;
            target.NameFontSize = source.NameFontSize;
            target.NameColorHex = source.NameColorHex;
            target.NameFontFamily = source.NameFontFamily;
            target.NameTextAlignment = source.NameTextAlignment;
            target.HasCustomNamePosition = source.HasCustomNamePosition;
            target.NamePositionX = source.NamePositionX;
            target.NamePositionY = source.NamePositionY;
            target.BackgroundColorHex = source.BackgroundColorHex;
            target.BackgroundImageBase64 = source.BackgroundImageBase64;
            target.BackgroundImageFileName = source.BackgroundImageFileName;
            target.BackgroundImageStretch = source.BackgroundImageStretch;
            target.HasExplicitBackgroundImageStretch =
                source.HasExplicitBackgroundImageStretch;
            target.BackgroundImageOpacity = source.BackgroundImageOpacity;
        }

        private static void CopyDeviceSharedData(
            Device target, Device source, bool replacePorts, bool preservePortPositions)
        {
            target.Name = source.Name;
            target.NameRtfBase64 = source.NameRtfBase64;
            target.Kind = source.Kind;
            target.CustomKindName = source.CustomKindName;
            target.CategoryColorHex = source.CategoryColorHex;
            target.Features = new Dictionary<string, string>(source.Features);
            target.NameBold = source.NameBold;
            target.NameItalic = source.NameItalic;
            target.NameUnderline = source.NameUnderline;
            target.NameFontSize = source.NameFontSize;
            target.NameColorHex = source.NameColorHex;
            target.NameFontFamily = source.NameFontFamily;
            target.NameTextAlignment = source.NameTextAlignment;
            target.HasCustomNamePosition = source.HasCustomNamePosition;
            target.NamePositionX = source.NamePositionX;
            target.NamePositionY = source.NamePositionY;
            target.BackgroundColorHex = source.BackgroundColorHex;
            target.BackgroundImageBase64 = source.BackgroundImageBase64;
            target.BackgroundImageFileName = source.BackgroundImageFileName;
            target.BackgroundImageStretch = source.BackgroundImageStretch;
            target.HasExplicitBackgroundImageStretch =
                source.HasExplicitBackgroundImageStretch;
            target.BackgroundImageOpacity = source.BackgroundImageOpacity;

            if (!replacePorts)
            {
                foreach (var sourcePort in source.Ports)
                {
                    var targetPort = target.Ports.FirstOrDefault(port => port.Id == sourcePort.Id)
                                     ?? target.Ports.FirstOrDefault(port =>
                                         port.Designator.Equals(sourcePort.Designator,
                                             StringComparison.OrdinalIgnoreCase));
                    if (targetPort == null) continue;
                    CopyPortSharedData(targetPort, sourcePort);
                }
                return;
            }

            var oldPorts = target.Ports.ToList();
            var rebuilt = new List<Port>();
            foreach (var sourcePort in source.Ports)
            {
                var old = oldPorts.FirstOrDefault(port => port.Id == sourcePort.Id)
                          ?? oldPorts.FirstOrDefault(port =>
                              port.Designator.Equals(sourcePort.Designator,
                                  StringComparison.OrdinalIgnoreCase));
                var copy = new Port
                {
                    Id = old?.Id ?? sourcePort.Id,
                    Side = preservePortPositions && old != null ? old.Side : sourcePort.Side,
                    Position = preservePortPositions && old != null ? old.Position : sourcePort.Position,
                    HasCustomLabelPosition = preservePortPositions && old != null
                        ? old.HasCustomLabelPosition : sourcePort.HasCustomLabelPosition,
                    LabelOffsetX = preservePortPositions && old != null
                        ? old.LabelOffsetX : sourcePort.LabelOffsetX,
                    LabelOffsetY = preservePortPositions && old != null
                        ? old.LabelOffsetY : sourcePort.LabelOffsetY
                };
                CopyPortSharedData(copy, sourcePort);
                rebuilt.Add(copy);
            }
            target.Ports = rebuilt;
        }

        private static void CopyPortSharedData(Port target, Port source)
        {
            target.Designator = source.Designator;
            target.ConnType = source.ConnType;
            target.ConnectorCatalogKey = source.ConnectorCatalogKey;
            target.Description = source.Description;
            target.ShowConnType = source.ShowConnType;
            target.ShowLabel = source.ShowLabel;
            target.LabelFontSize = source.LabelFontSize;
            target.LabelColorHex = source.LabelColorHex;
        }

        private void SynchronizeDeviceSharedData(
            Device canonical, Device source, string originalName, bool replacePorts)
        {
            var matchingTemplates = _registeredDevices
                .Select(item => item.Device)
                .Where(device => device.Id == canonical.Id ||
                    device.Name.Equals(originalName, StringComparison.OrdinalIgnoreCase) ||
                    device.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();
            if (!matchingTemplates.Contains(canonical)) matchingTemplates.Add(canonical);

            foreach (var template in matchingTemplates)
                CopyDeviceSharedData(template, source, replacePorts, preservePortPositions: false);

            var templateIds = matchingTemplates.Select(device => device.Id).ToHashSet();
            foreach (var instance in EnumerateAllDeviceInstances().Where(instance =>
                         templateIds.Contains(instance.DeviceId)))
            {
                if (instance.Override != null)
                    CopyDeviceSharedData(instance.Override, canonical, replacePorts,
                        preservePortPositions: true);
            }
            foreach (var item in _registeredDevices.Where(item => matchingTemplates.Contains(item.Device)))
                item.Refresh();
            RefreshSidebarViews();
        }

        private IEnumerable<DeviceInstance> EnumerateAllDeviceInstances()
            => _sheets.SelectMany(sheet => sheet.DeviceInstances)
                .Concat(_placed.Select(placed => placed.Instance))
                .Distinct();

        private IEnumerable<CableInstance> EnumerateAllCableInstances()
            => _sheets.SelectMany(sheet => sheet.CableInstances)
                .Concat(_placedCables.Select(placed => placed.Instance))
                .Distinct();

        private static Cable CloneCableDefinition(Cable source) => DeepClone(source);

        private static void CopyCableDefinition(Cable target, Cable source)
        {
            var oldConnectors = target.Connectors.ToList();
            var sourceToTargetIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var rebuilt = new List<CableConnector>();
            for (int index = 0; index < source.Connectors.Count; index++)
            {
                var sourceConnector = source.Connectors[index];
                var old = oldConnectors.FirstOrDefault(connector => connector.Id == sourceConnector.Id)
                          ?? oldConnectors.FirstOrDefault(connector =>
                              connector.PortName.Equals(sourceConnector.PortName,
                                  StringComparison.OrdinalIgnoreCase))
                          ?? (index < oldConnectors.Count ? oldConnectors[index] : null);
                string id = old?.Id ?? sourceConnector.Id;
                sourceToTargetIds[sourceConnector.Id] = id;
                rebuilt.Add(new CableConnector
                {
                    Id = id,
                    PortName = sourceConnector.PortName,
                    ConnType = sourceConnector.ConnType,
                    ConnectorCatalogKey = sourceConnector.ConnectorCatalogKey,
                    Description = sourceConnector.Description,
                    ShowConnType = sourceConnector.ShowConnType,
                    ShowLabel = sourceConnector.ShowLabel,
                    LabelFontSize = sourceConnector.LabelFontSize,
                    LabelColorHex = sourceConnector.LabelColorHex,
                    KeyAngle = sourceConnector.KeyAngle,
                    Pins = CloneCablePins(sourceConnector.Pins)
                });
            }

            target.Name = source.Name;
            target.Category = source.Category;
            target.NameFontSize = source.NameFontSize;
            target.CustomCategoryName = source.CustomCategoryName;
            target.LineColorHex = source.LineColorHex;
            target.OverallBraid = source.OverallBraid;
            target.Tube = source.Tube;
            target.Connectors = rebuilt;
            target.SignalRows = source.SignalRows.Select(row =>
            {
                var copy = new CableSignalRow
                {
                    Id = row.Id,
                    ParentSignalId = row.ParentSignalId,
                    PinName = row.PinName,
                    WireType = row.WireType
                };
                foreach (var pair in row.PortPins)
                {
                    string targetId = sourceToTargetIds.TryGetValue(pair.Key, out string? mapped)
                        ? mapped : pair.Key;
                    copy.PortPins[targetId] = CloneCablePins(new[] { pair.Value })[0];
                }
                return copy;
            }).ToList();
        }

        private void SynchronizeCableSharedData(
            Cable canonical, Cable source, string originalName, bool ensureLayouts = true)
        {
            var matchingTemplates = _registeredCables
                .Select(item => item.Cable)
                .Where(cable => cable.Id == canonical.Id ||
                    cable.Name.Equals(originalName, StringComparison.OrdinalIgnoreCase) ||
                    cable.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();
            if (!matchingTemplates.Contains(canonical)) matchingTemplates.Add(canonical);

            foreach (var template in matchingTemplates)
                CopyCableDefinition(template, source);

            var byId = matchingTemplates.ToDictionary(cable => cable.Id);
            foreach (var instance in EnumerateAllCableInstances())
            {
                if (!byId.TryGetValue(instance.CableId, out var template)) continue;
                instance.Override = null;
                if (ensureLayouts) EnsureCableInstanceLayouts(instance, template);
            }
            foreach (var item in _registeredCables.Where(item => matchingTemplates.Contains(item.Cable)))
                item.Refresh();
            RefreshSidebarViews();
        }

        private void EnsureCableInstanceLayouts(CableInstance instance, Cable template)
        {
            if (template.Connectors.Count < 2) return;
            var existing = AllPortLayouts(instance).ToDictionary(
                layout => layout.ConnectorId, StringComparer.Ordinal);
            instance.Branches.RemoveAll(branch =>
                template.Connectors.All(connector => connector.Id != branch.Port.ConnectorId));

            if (!template.Connectors.Any(connector => connector.Id == instance.PortA.ConnectorId))
                instance.PortA.ConnectorId = template.Connectors[0].Id;
            if (!template.Connectors.Any(connector => connector.Id == instance.PortB.ConnectorId))
                instance.PortB.ConnectorId = template.Connectors[1].Id;

            var missing = template.Connectors.Skip(2)
                .Where(connector => !existing.ContainsKey(connector.Id)).ToList();
            int existingBranchCount = instance.Branches.Count;
            var rootRoute = ComputeLegRoute(instance, "root", avoidObstacles: false)
                            ?? new List<Point>
                            {
                                new(instance.PortA.PortX, instance.PortA.PortY),
                                new(instance.PortB.PortX, instance.PortB.PortY)
                            };
            for (int index = 0; index < missing.Count; index++)
            {
                double t = (existingBranchCount + index + 1.0) /
                           (existingBranchCount + missing.Count + 1.0);
                Point anchor = CableRenderer.PointAlongPolyline(rootRoute, t);
                Point before = CableRenderer.PointAlongPolyline(rootRoute, Math.Max(0, t - 0.01));
                Point after = CableRenderer.PointAlongPolyline(rootRoute, Math.Min(1, t + 0.01));
                double anchorX = anchor.X;
                double anchorY = anchor.Y;
                bool positiveSide = (existingBranchCount + index) % 2 == 0;
                bool rootMostlyHorizontal = Math.Abs(after.X - before.X) >=
                                             Math.Abs(after.Y - before.Y);
                var snappedAttachment = FindGridSnappedPolylineAttachment(
                    rootRoute, anchor);
                t = snappedAttachment.T;
                anchorX = snappedAttachment.Anchor.X;
                anchorY = snappedAttachment.Anchor.Y;
                double portX = rootMostlyHorizontal
                    ? anchorX
                    : anchorX + (positiveSide ? 100 : -100);
                double portY = rootMostlyHorizontal
                    ? anchorY + (positiveSide ? 100 : -100)
                    : anchorY;
                PortSide facing = rootMostlyHorizontal
                    ? (positiveSide ? PortSide.Top : PortSide.Bottom)
                    : (positiveSide ? PortSide.Left : PortSide.Right);
                Point snappedPort = SnapCablePortToGridFace(
                    new Point(portX, portY), facing);
                instance.Branches.Add(new CableBranch
                {
                    ParentId = "root",
                    T = t,
                    Port = new CablePortLayout
                    {
                        ConnectorId = missing[index].Id,
                        PortX = snappedPort.X,
                        PortY = snappedPort.Y,
                        FixedFacing = facing
                    }
                });
            }
        }

        private void MigrateLegacySharedDefinitions(Project project)
        {
            var allCableInstances = project.Sheets.SelectMany(sheet => sheet.CableInstances)
                .Concat(project.CableInstances).Distinct().ToList();
            foreach (var group in project.Cables.GroupBy(
                         cable => cable.Name, StringComparer.OrdinalIgnoreCase))
            {
                var templates = group.ToList();
                var overrides = allCableInstances
                    .Where(instance => templates.Any(template => template.Id == instance.CableId))
                    .Select(instance => instance.Override)
                    .Where(cable => cable != null)
                    .Cast<Cable>()
                    .ToList();
                var source = templates.Concat(overrides)
                    .OrderByDescending(cable => cable.SignalRows.Count)
                    .ThenByDescending(cable => cable.Connectors.Count)
                    .First();
                foreach (var template in templates)
                    CopyCableDefinition(template, source);
                var byId = templates.ToDictionary(template => template.Id);
                foreach (var instance in allCableInstances.Where(instance =>
                             byId.ContainsKey(instance.CableId)))
                {
                    instance.Override = null;
                    EnsureCableInstanceLayouts(instance, byId[instance.CableId]);
                }
            }

            var allDeviceInstances = project.Sheets.SelectMany(sheet => sheet.DeviceInstances)
                .Concat(project.DeviceInstances).Distinct().ToList();
            foreach (var group in project.Devices.GroupBy(
                         device => device.Name, StringComparer.OrdinalIgnoreCase))
            {
                var templates = group.ToList();
                var overrides = allDeviceInstances
                    .Where(instance => templates.Any(template => template.Id == instance.DeviceId))
                    .Select(instance => instance.Override)
                    .Where(device => device != null)
                    .Cast<Device>()
                    .ToList();
                var source = templates.Concat(overrides)
                    .OrderByDescending(device => string.IsNullOrWhiteSpace(device.NameRtfBase64) ? 0 : 1)
                    .ThenByDescending(device => device.Ports.Count)
                    .First();
                foreach (var template in templates)
                    CopyDeviceSharedData(template, source, replacePorts: true,
                        preservePortPositions: true);
                var byId = templates.ToDictionary(template => template.Id);
                foreach (var instance in allDeviceInstances.Where(instance =>
                             byId.ContainsKey(instance.DeviceId) && instance.Override != null))
                    CopyDeviceSharedData(instance.Override!, byId[instance.DeviceId],
                        replacePorts: true, preservePortPositions: true);
            }
        }

        // ================= 등록 =================
        // ================= 구역 설정 =================
        private void BeginZoneSettingMode_Click(
            object sender, RoutedEventArgs e)
        {
            if (_isZoneSettingMode) return;
            if (_armedShapeKind != null) CancelShapeInsertion();
            CommitActiveTextEdit();
            SelectShape(null);
            DeselectCable();
            ClearSelection();
            ClearCablePortSelection();

            EndAutoArrangeExclusionMode();
            AutoArrangeExclusionPanel.Visibility = Visibility.Collapsed;
            _isZoneSettingMode = true;
            _isCreatingZone = false;
            BeginZoneSettingButton.Visibility = Visibility.Collapsed;
            ExitZoneSettingButton.Visibility = Visibility.Visible;
            ZoneSettingPanel.Visibility = Visibility.Visible;
            MainCanvas.ForceCursor = true;
            MainCanvas.Cursor = Cursors.Arrow;
            SelectZone(null);
            RefreshZoneSettingList();
            StatusText.Text = "구역 설정 모드 — 기존 구역을 선택해 이동·크기 조절하거나 오른쪽에서 새 구역을 만드세요.";
        }

        private void BeginZoneCreation_Click(object sender, RoutedEventArgs e)
        {
            if (!_isZoneSettingMode) return;
            _isCreatingZone = true;
            SelectZone(null);
            CreateZoneButton.Content = "캔버스에서 범위를 끄세요";
            ZoneModeHintText.Text = "캔버스를 끌어 새 구역을 만드세요. 하나를 만든 뒤에는 자동으로 편집 상태로 돌아갑니다.";
            MainCanvas.Cursor = Cursors.Cross;
            StatusText.Text = "새 구역 만들기 — 캔버스에서 원하는 범위를 끌어 지정하세요.";
        }

        private void ExitZoneSettingMode_Click(
            object sender, RoutedEventArgs e)
            => ExitZoneSettingMode();

        private void ExitZoneSettingMode()
        {
            if (_zoneDrawPreview != null)
            {
                MainCanvas.Children.Remove(_zoneDrawPreview);
                _zoneDrawPreview = null;
            }
            if (MainCanvas.IsMouseCaptured)
                MainCanvas.ReleaseMouseCapture();

            _isCreatingZone = false;
            _zoneDragPlaced = null;
            _zoneDragHandle = null;
            _isZoneSettingMode = false;
            SelectZone(null);
            BeginZoneSettingButton.Visibility = Visibility.Visible;
            ExitZoneSettingButton.Visibility = Visibility.Collapsed;
            ZoneSettingPanel.Visibility = Visibility.Collapsed;
            MainCanvas.ForceCursor = false;
            MainCanvas.Cursor = null;
            StatusText.Text = "구역 설정 모드를 종료했습니다.";
        }

        private void EndZoneCreation()
        {
            _isCreatingZone = false;
            CreateZoneButton.Content = "새 구역 만들기";
            ZoneModeHintText.Text = "기존 구역을 선택하면 이동하거나 테두리의 조절점을 끌어 크기를 바꿀 수 있습니다.";
            MainCanvas.Cursor = Cursors.Arrow;
        }

        private PlacedZone CreatePlacedZone(Zone zone)
        {
            var container = new Canvas
            {
                IsHitTestVisible = false,
                Background = null
            };
            var placed = new PlacedZone { Zone = zone, Container = container };
            Panel.SetZIndex(container, ZoneLayerZIndex);
            return placed;
        }

        private void RenderZone(PlacedZone placed)
        {
            Zone zone = placed.Zone;
            Canvas container = placed.Container;
            container.Children.Clear();
            container.Width = Math.Max(DiagramGridSize, zone.Width);
            container.Height = Math.Max(DiagramGridSize, zone.Height);
            Canvas.SetLeft(container, zone.X);
            Canvas.SetTop(container, zone.Y);
            Panel.SetZIndex(container, ZoneLayerZIndex);

            var outline = new Border
            {
                Width = container.Width,
                Height = container.Height,
                Background = AppThemeManager.WithAlpha("AppAccentBrush", 18),
                BorderBrush = AppThemeManager.WithAlpha("AppAccentBrush", 150),
                BorderThickness = new Thickness(1.5)
            };
            container.Children.Add(outline);

            var title = new TextBlock
            {
                Text = zone.Name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppThemeManager.GetBrush("AppAccentTextBrush"),
                Background = AppThemeManager.WithAlpha("AppPanelBrush", 220),
                Padding = new Thickness(5, 1, 5, 2),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(title, Math.Max(0,
                (container.Width - title.DesiredSize.Width) / 2));
            Canvas.SetTop(title, 5);
            container.Children.Add(title);

            if (_isZoneSettingMode && ReferenceEquals(_selectedZone, placed))
            {
                var selection = new Rectangle
                {
                    Width = container.Width,
                    Height = container.Height,
                    Fill = Brushes.Transparent,
                    Stroke = AppThemeManager.GetBrush("AppAccentBrush"),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 4, 2 },
                    IsHitTestVisible = false
                };
                container.Children.Add(selection);
                foreach (Point point in ZoneHandlePoints(zone.Width, zone.Height))
                {
                    var handle = new Rectangle
                    {
                        Width = 9,
                        Height = 9,
                        Fill = AppThemeManager.GetBrush("AppPanelBrush"),
                        Stroke = AppThemeManager.GetBrush("AppAccentBrush"),
                        StrokeThickness = 1.5,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(handle, point.X - 4.5);
                    Canvas.SetTop(handle, point.Y - 4.5);
                    container.Children.Add(handle);
                }
            }
        }

        private static IReadOnlyList<Point> ZoneHandlePoints(double width, double height)
            => new[]
            {
                new Point(0, 0), new Point(width / 2, 0), new Point(width, 0),
                new Point(0, height / 2), new Point(width, height / 2),
                new Point(0, height), new Point(width / 2, height), new Point(width, height)
            };

        private void RenderAllZones()
        {
            foreach (PlacedZone placed in _placedZones)
                RenderZone(placed);
        }

        private void StartZoneDraw(Point point)
        {
            _zoneDrawStart = SnapToDiagramGrid(point);
            _zoneDrawPreview = new Rectangle
            {
                Width = 0,
                Height = 0,
                Fill = AppThemeManager.WithAlpha("AppAccentBrush", 28),
                Stroke = AppThemeManager.GetBrush("AppAccentBrush"),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                IsHitTestVisible = false
            };
            Panel.SetZIndex(_zoneDrawPreview, ZoneLayerZIndex + 1);
            Canvas.SetLeft(_zoneDrawPreview, _zoneDrawStart.X);
            Canvas.SetTop(_zoneDrawPreview, _zoneDrawStart.Y);
            MainCanvas.Children.Add(_zoneDrawPreview);
            MainCanvas.CaptureMouse();
        }

        private void UpdateZoneDraw(Point point)
        {
            if (_zoneDrawPreview == null) return;
            Point current = SnapToDiagramGrid(point);
            double left = Math.Min(_zoneDrawStart.X, current.X);
            double top = Math.Min(_zoneDrawStart.Y, current.Y);
            _zoneDrawPreview.Width = Math.Abs(current.X - _zoneDrawStart.X);
            _zoneDrawPreview.Height = Math.Abs(current.Y - _zoneDrawStart.Y);
            Canvas.SetLeft(_zoneDrawPreview, left);
            Canvas.SetTop(_zoneDrawPreview, top);
        }

        private void FinishZoneDraw(Point point)
        {
            if (_zoneDrawPreview == null) return;
            Point current = SnapToDiagramGrid(point);
            double left = Math.Min(_zoneDrawStart.X, current.X);
            double top = Math.Min(_zoneDrawStart.Y, current.Y);
            double width = Math.Abs(current.X - _zoneDrawStart.X);
            double height = Math.Abs(current.Y - _zoneDrawStart.Y);

            MainCanvas.Children.Remove(_zoneDrawPreview);
            _zoneDrawPreview = null;
            MainCanvas.ReleaseMouseCapture();
            if (width < DiagramGridSize || height < DiagramGridSize)
            {
                StatusText.Text = "구역은 가로와 세로를 각각 한 격자 이상 끌어서 만드세요.";
                EndZoneCreation();
                return;
            }

            var nameWindow = new ZoneNameWindow { Owner = this };
            if (nameWindow.ShowDialog() != true)
            {
                EndZoneCreation();
                return;
            }

            var zone = new Zone
            {
                Name = nameWindow.ResultName,
                X = left,
                Y = top,
                Width = width,
                Height = height
            };
            var placed = CreatePlacedZone(zone);
            MainCanvas.Children.Add(placed.Container);
            _placedZones.Add(placed);
            SelectZone(placed);
            RefreshZoneSettingList();
            UpdateCanvasExtent();
            _isDirty = true;
            EndZoneCreation();
            StatusText.Text = $"구역 '{zone.Name}'을(를) 만들었습니다. 선택 상태에서 이동하거나 크기를 조절할 수 있습니다.";
        }

        private PlacedZone? FindZoneAt(Point point)
            => _placedZones
                .Where(placed => new Rect(placed.Zone.X, placed.Zone.Y,
                    placed.Zone.Width, placed.Zone.Height).Contains(point))
                .OrderBy(placed => placed.Zone.Width * placed.Zone.Height)
                .FirstOrDefault();

        private void SelectZone(PlacedZone? placed)
        {
            PlacedZone? previous = _selectedZone;
            _selectedZone = placed;
            if (previous != null) RenderZone(previous);
            if (placed != null && !ReferenceEquals(previous, placed)) RenderZone(placed);
            RenameZoneButton.IsEnabled = placed != null;
            DeleteZoneButton.IsEnabled = placed != null;
            if (ZoneSettingList != null)
            {
                _updatingZoneListSelection = true;
                ZoneSettingList.SelectedItem = placed;
                _updatingZoneListSelection = false;
            }
        }

        private void RefreshZoneSettingList()
        {
            if (ZoneSettingList == null) return;
            _updatingZoneListSelection = true;
            ZoneSettingList.ItemsSource = null;
            ZoneSettingList.ItemsSource = _placedZones;
            ZoneSettingList.SelectedItem = _selectedZone;
            _updatingZoneListSelection = false;
        }

        private void ZoneSettingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingZoneListSelection) return;
            SelectZone(ZoneSettingList.SelectedItem as PlacedZone);
        }

        private void RenameSelectedZone_Click(object sender, RoutedEventArgs e)
            => RenameZone(_selectedZone);

        private void RenameZone(PlacedZone? placed)
        {
            if (placed == null) return;
            var window = new ZoneNameWindow(placed.Zone.Name) { Owner = this };
            if (window.ShowDialog() != true) return;
            placed.Zone.Name = window.ResultName;
            RenderZone(placed);
            RefreshZoneSettingList();
            _isDirty = true;
        }

        private void DeleteSelectedZone_Click(object sender, RoutedEventArgs e)
            => DeleteZone(_selectedZone);

        private void DeleteZone(PlacedZone? placed)
        {
            if (placed == null) return;
            MainCanvas.Children.Remove(placed.Container);
            _placedZones.Remove(placed);
            foreach (PlacedDevice device in _placed.Where(
                         item => item.Instance.ZoneId == placed.Zone.Id))
                device.Instance.ZoneId = null;
            _selectedZone = null;
            RefreshZoneSettingList();
            RenameZoneButton.IsEnabled = false;
            DeleteZoneButton.IsEnabled = false;
            UpdateCanvasExtent();
            _isDirty = true;
        }

        private bool TryHitSelectedZoneHandle(Point point, out HandleKind handle)
        {
            handle = HandleKind.TopLeft;
            if (_selectedZone == null) return false;
            Zone zone = _selectedZone.Zone;
            var candidates = new (HandleKind Kind, Point Point)[]
            {
                (HandleKind.TopLeft, new Point(zone.X, zone.Y)),
                (HandleKind.Top, new Point(zone.X + zone.Width / 2, zone.Y)),
                (HandleKind.TopRight, new Point(zone.X + zone.Width, zone.Y)),
                (HandleKind.Left, new Point(zone.X, zone.Y + zone.Height / 2)),
                (HandleKind.Right, new Point(zone.X + zone.Width, zone.Y + zone.Height / 2)),
                (HandleKind.BottomLeft, new Point(zone.X, zone.Y + zone.Height)),
                (HandleKind.Bottom, new Point(zone.X + zone.Width / 2, zone.Y + zone.Height)),
                (HandleKind.BottomRight, new Point(zone.X + zone.Width, zone.Y + zone.Height))
            };
            const double tolerance = 9;
            foreach ((HandleKind kind, Point candidate) in candidates)
            {
                if (Math.Abs(point.X - candidate.X) <= tolerance &&
                    Math.Abs(point.Y - candidate.Y) <= tolerance)
                {
                    handle = kind;
                    return true;
                }
            }
            return false;
        }

        private static Cursor ZoneCursor(HandleKind handle) => handle switch
        {
            HandleKind.TopLeft or HandleKind.BottomRight => Cursors.SizeNWSE,
            HandleKind.TopRight or HandleKind.BottomLeft => Cursors.SizeNESW,
            HandleKind.Top or HandleKind.Bottom => Cursors.SizeNS,
            _ => Cursors.SizeWE
        };

        private void StartZoneDrag(PlacedZone placed, HandleKind? handle, Point mouse)
        {
            _zoneDragPlaced = placed;
            _zoneDragHandle = handle;
            _zoneDragStartMouse = mouse;
            _zoneDragStartX = placed.Zone.X;
            _zoneDragStartY = placed.Zone.Y;
            _zoneDragStartWidth = placed.Zone.Width;
            _zoneDragStartHeight = placed.Zone.Height;
            _zoneDragChanged = false;
            MainCanvas.CaptureMouse();
        }

        private void UpdateZoneDrag(Point mouse)
        {
            if (_zoneDragPlaced == null) return;
            Zone zone = _zoneDragPlaced.Zone;
            double dx = SnapToDiagramGrid(mouse.X - _zoneDragStartMouse.X);
            double dy = SnapToDiagramGrid(mouse.Y - _zoneDragStartMouse.Y);
            if (_zoneDragHandle == null)
            {
                zone.X = SnapToDiagramGrid(_zoneDragStartX + dx);
                zone.Y = SnapToDiagramGrid(_zoneDragStartY + dy);
            }
            else
            {
                double left = _zoneDragStartX;
                double top = _zoneDragStartY;
                double right = _zoneDragStartX + _zoneDragStartWidth;
                double bottom = _zoneDragStartY + _zoneDragStartHeight;
                HandleKind handle = _zoneDragHandle.Value;
                if (handle is HandleKind.TopLeft or HandleKind.Left or HandleKind.BottomLeft)
                    left = Math.Min(right - DiagramGridSize, SnapToDiagramGrid(_zoneDragStartX + dx));
                if (handle is HandleKind.TopRight or HandleKind.Right or HandleKind.BottomRight)
                    right = Math.Max(left + DiagramGridSize, SnapToDiagramGrid(_zoneDragStartX + _zoneDragStartWidth + dx));
                if (handle is HandleKind.TopLeft or HandleKind.Top or HandleKind.TopRight)
                    top = Math.Min(bottom - DiagramGridSize, SnapToDiagramGrid(_zoneDragStartY + dy));
                if (handle is HandleKind.BottomLeft or HandleKind.Bottom or HandleKind.BottomRight)
                    bottom = Math.Max(top + DiagramGridSize, SnapToDiagramGrid(_zoneDragStartY + _zoneDragStartHeight + dy));
                zone.X = left;
                zone.Y = top;
                zone.Width = right - left;
                zone.Height = bottom - top;
            }
            _zoneDragChanged = Math.Abs(zone.X - _zoneDragStartX) > 0.01 ||
                               Math.Abs(zone.Y - _zoneDragStartY) > 0.01 ||
                               Math.Abs(zone.Width - _zoneDragStartWidth) > 0.01 ||
                               Math.Abs(zone.Height - _zoneDragStartHeight) > 0.01;
            RenderZone(_zoneDragPlaced);
        }

        private string? FindContainingZoneId(PlacedDevice placed)
        {
            if (!string.IsNullOrWhiteSpace(placed.Instance.ZoneId))
                return placed.Instance.ZoneId;
            Point center = new(placed.Instance.X + placed.Instance.Width / 2,
                placed.Instance.Y + placed.Instance.Height / 2);
            return _placedZones
                .Where(item => new Rect(item.Zone.X, item.Zone.Y,
                    item.Zone.Width, item.Zone.Height).Contains(center))
                .OrderBy(item => item.Zone.Width * item.Zone.Height)
                .Select(item => item.Zone.Id)
                .FirstOrDefault();
        }

        private void MainCanvas_PreviewMouseRightButtonDown(
            object sender, MouseButtonEventArgs e)
        {
            if (!_isZoneSettingMode) return;
            PlacedZone? placed = FindZoneAt(e.GetPosition(MainCanvas));
            if (placed != null)
            {
                SelectZone(placed);
                var menu = new ContextMenu();
                menu.Items.Add(MakeMenuItem("구역 이름 변경...", () => RenameZone(placed)));
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem("구역 삭제", () => DeleteZone(placed)));
                menu.IsOpen = true;
            }
            e.Handled = true;
        }

        // ================= 일반 도형 (사각형/타원/선/텍스트) =================
        private void Menu_AddShapeRectangle(object sender, RoutedEventArgs e) => ArmShapeTool("Rectangle", ShapeToolRect);
        private void Menu_AddShapeEllipse(object sender, RoutedEventArgs e) => ArmShapeTool("Ellipse", ShapeToolEllipse);
        private void Menu_AddShapeLine(object sender, RoutedEventArgs e) => ArmShapeTool("Line", ShapeToolLine);
        private void Menu_AddShapeElbowLine(object sender, RoutedEventArgs e) => ArmShapeTool("ElbowLine", ShapeToolLine);
        private void Menu_AddShapeText(object sender, RoutedEventArgs e) => ArmShapeTool("Text", ShapeToolText);

        private void Menu_AddImage(object sender, RoutedEventArgs e)
        {
            ShapeGalleryPopup.IsOpen = false;
            if (!ProjectImageService.TryImport(this, out ProjectImageData image))
                return;

            double width = Math.Min(360, Math.Max(80, image.PixelWidth));
            double height = image.PixelWidth > 0
                ? width * image.PixelHeight / image.PixelWidth
                : 240;
            if (height > 320)
            {
                width *= 320 / height;
                height = 320;
            }
            var shape = new CanvasShape
            {
                Kind = "Image",
                X = Math.Max(20,
                    MainScrollViewer.HorizontalOffset / CanvasScale.ScaleX + 60),
                Y = Math.Max(20,
                    MainScrollViewer.VerticalOffset / CanvasScale.ScaleY + 60),
                Width = width,
                Height = height,
                StrokeThickness = 1,
                ZOrder = GetNextShapeZOrder(),
                ImageDataBase64 = image.Base64,
                ImageFileName = image.FileName,
                ImageStretch = "Fill",
                ImageOpacity = 1
            };
            var placed = CreatePlacedShape(shape);
            MainCanvas.Children.Add(placed.Container);
            _placedShapes.Add(placed);
            ApplyShapeZOrder(placed);
            RenderShape(placed);
            SelectShape(placed);
            UpdateCanvasExtent();
            _isDirty = true;
            StatusText.Text = $"이미지 '{image.FileName}'을(를) 프로젝트에 포함했습니다.";
        }

        private PlacedShape CreatePlacedShape(CanvasShape shape)
        {
            UpgradeLegacyLineShape(shape);
            // Background=Transparent이면 채우기가 없는 도형도 컨테이너의
            // 사각 영역 전체가 클릭을 가로챈다. null로 두면 실제 선/면만
            // 입력을 받고 투명한 내부는 뒤의 개체로 통과한다.
            var container = new Canvas { Cursor = Cursors.SizeAll, Background = null };
            var placed = new PlacedShape { Shape = shape, Container = container };

            container.MouseLeftButtonDown += ShapeContainer_MouseLeftButtonDown;
            container.MouseMove += ShapeContainer_MouseMove;
            container.MouseLeftButtonUp += ShapeContainer_MouseLeftButtonUp;
            container.MouseRightButtonDown += ShapeContainer_MouseRightButtonDown;
            container.LostMouseCapture += (_, _) => FinishShapeAdjustmentIfCaptureLost(placed);
            return placed;
        }

        private int GetNextShapeZOrder()
            => _placedShapes.Count == 0
                ? 0
                : _placedShapes.Max(item => item.Shape.ZOrder) + 1;

        private static void ApplyShapeZOrder(PlacedShape placed)
            => Panel.SetZIndex(
                placed.Container,
                (placed.Shape.BehindDiagram
                    ? ShapeBackgroundBaseZIndex
                    : ShapeLayerBaseZIndex) + placed.Shape.ZOrder);

        /// <summary>
        /// 오래된 프로젝트처럼 모든 ZOrder가 0인 경우에는 저장된 목록 순서를
        /// 그대로 앞뒤 순서로 승격한다. 이후에는 0부터 연속된 값으로 관리한다.
        /// </summary>
        private void NormalizeShapeZOrders()
        {
            var ordered = _placedShapes
                .Select((placed, index) => new
                {
                    Placed = placed,
                    OriginalIndex = index
                })
                .OrderBy(item => item.Placed.Shape.ZOrder)
                .ThenBy(item => item.OriginalIndex)
                .Select(item => item.Placed)
                .ToList();

            _placedShapes.Clear();
            _placedShapes.AddRange(ordered);
            for (int index = 0; index < _placedShapes.Count; index++)
            {
                _placedShapes[index].Shape.ZOrder = index;
                ApplyShapeZOrder(_placedShapes[index]);
            }
        }

        private enum ShapeOrderChange
        {
            BringToFront,
            BringForward,
            SendBackward,
            SendToBack
        }

        private void ChangeShapeOrder(
            PlacedShape? placed,
            ShapeOrderChange change)
        {
            if (placed == null || !_placedShapes.Contains(placed)) return;

            NormalizeShapeZOrders();
            int oldIndex = _placedShapes.IndexOf(placed);
            bool layerChanged = false;
            if (change == ShapeOrderChange.SendToBack &&
                !placed.Shape.BehindDiagram)
            {
                placed.Shape.BehindDiagram = true;
                layerChanged = true;
            }
            else if (change is ShapeOrderChange.BringToFront or
                     ShapeOrderChange.BringForward &&
                     placed.Shape.BehindDiagram)
            {
                placed.Shape.BehindDiagram = false;
                layerChanged = true;
            }
            else if (change == ShapeOrderChange.SendBackward &&
                     oldIndex == 0 && !placed.Shape.BehindDiagram)
            {
                placed.Shape.BehindDiagram = true;
                layerChanged = true;
            }

            int newIndex = change switch
            {
                ShapeOrderChange.BringToFront => _placedShapes.Count - 1,
                ShapeOrderChange.BringForward =>
                    Math.Min(_placedShapes.Count - 1, oldIndex + 1),
                ShapeOrderChange.SendBackward => Math.Max(0, oldIndex - 1),
                ShapeOrderChange.SendToBack => 0,
                _ => oldIndex
            };
            if (oldIndex == newIndex && !layerChanged) return;

            _placedShapes.RemoveAt(oldIndex);
            _placedShapes.Insert(newIndex, placed);
            for (int index = 0; index < _placedShapes.Count; index++)
            {
                _placedShapes[index].Shape.ZOrder = index;
                ApplyShapeZOrder(_placedShapes[index]);
            }

            _isDirty = true;
            StatusText.Text = change switch
            {
                ShapeOrderChange.BringToFront => "도형을 맨 앞으로 가져왔습니다.",
                ShapeOrderChange.BringForward => "도형을 한 단계 앞으로 가져왔습니다.",
                ShapeOrderChange.SendBackward => "도형을 한 단계 뒤로 보냈습니다.",
                ShapeOrderChange.SendToBack => "도형을 맨 뒤로 보냈습니다.",
                _ => StatusText.Text
            };
        }

        private void Menu_BringShapeToFront(object sender, RoutedEventArgs e)
            => ChangeShapeOrder(
                _selectedShape, ShapeOrderChange.BringToFront);

        private void Menu_BringShapeForward(object sender, RoutedEventArgs e)
            => ChangeShapeOrder(
                _selectedShape, ShapeOrderChange.BringForward);

        private void Menu_SendShapeBackward(object sender, RoutedEventArgs e)
            => ChangeShapeOrder(
                _selectedShape, ShapeOrderChange.SendBackward);

        private void Menu_SendShapeToBack(object sender, RoutedEventArgs e)
            => ChangeShapeOrder(
                _selectedShape, ShapeOrderChange.SendToBack);

        private void ShapeFillButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedShape == null ||
                IsLineShapeKind(_selectedShape.Shape.Kind)) return;
            ShowShapeColorPalette(
                ShapeFillButton, _selectedShape, isFill: true);
        }

        private void ShapeStrokeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedShape == null) return;
            ShowShapeColorPalette(
                ShapeStrokeButton, _selectedShape, isFill: false);
        }

        private void ShowShapeColorPalette(
            Button anchor, PlacedShape placed, bool isFill)
        {
            string currentHex = isFill
                ? placed.Shape.FillHex
                : placed.Shape.StrokeHex;
            var popup = new Popup
            {
                Placement = PlacementMode.Bottom,
                PlacementTarget = anchor,
                VerticalOffset = 2,
                StaysOpen = false,
                AllowsTransparency = true
            };

            var content = new StackPanel { Width = 300 };
            content.Children.Add(new TextBlock
            {
                Text = "테마 색",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(4, 2, 4, 7)
            });

            var officeColors = ColorOption.BuildOfficePalette();
            var themeGrid = new UniformGrid { Columns = 10 };
            foreach (var color in officeColors)
                themeGrid.Children.Add(CreateQuickColorButton(
                    color.Hex, color.Name, currentHex, popup,
                    hex => ApplyQuickShapeColor(placed, isFill, hex)));
            content.Children.Add(themeGrid);

            content.Children.Add(new Separator { Margin = new Thickness(0, 9, 0, 7) });
            content.Children.Add(new TextBlock
            {
                Text = "표준 색",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(4, 0, 4, 7)
            });
            var standardGrid = new UniformGrid { Columns = 10 };
            string[] standardColors =
            {
                "#C00000", "#FF0000", "#FFC000", "#FFFF00", "#92D050",
                "#00B050", "#00B0F0", "#0070C0", "#002060", "#7030A0"
            };
            foreach (string hex in standardColors)
                standardGrid.Children.Add(CreateQuickColorButton(
                    hex, hex, currentHex, popup,
                    selected => ApplyQuickShapeColor(
                        placed, isFill, selected)));
            content.Children.Add(standardGrid);

            content.Children.Add(new Separator { Margin = new Thickness(0, 9, 0, 5) });
            content.Children.Add(CreatePaletteActionButton(
                isFill ? "□  채우기 없음" : "□  윤곽선 없음",
                () =>
                {
                    popup.IsOpen = false;
                    ApplyQuickShapeColor(
                        placed, isFill, "#00FFFFFF");
                }));
            content.Children.Add(CreatePaletteActionButton(
                isFill ? "🎨  다른 채우기 색..." : "🎨  다른 윤곽선 색...",
                () =>
                {
                    popup.IsOpen = false;
                    PickShapeColor(placed, isFill);
                    UpdateTextFormatPalette();
                }));
            content.Children.Add(CreatePaletteActionButton(
                "⌾  스포이트",
                () =>
                {
                    popup.IsOpen = false;
                    var picker = new ScreenEyedropperWindow { Owner = this };
                    if (picker.ShowDialog() == true &&
                        picker.SelectedHex != null)
                        ApplyQuickShapeColor(
                            placed, isFill, picker.SelectedHex);
                }));

            if (!isFill)
            {
                content.Children.Add(new Separator
                    { Margin = new Thickness(0, 5, 0, 5) });
                content.Children.Add(new TextBlock
                {
                    Text = "윤곽선 굵기",
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(4, 2, 4, 6)
                });
                var thicknessPanel = new WrapPanel
                {
                    Margin = new Thickness(2, 0, 2, 2)
                };
                foreach (double thickness in new[]
                         { 0.5, 1.0, 1.5, 2.0, 3.0, 4.0, 6.0, 8.0 })
                {
                    var button = new Button
                    {
                        Content = thickness.ToString("0.#"),
                        Style = AppThemeManager.GetStyle("TileButtonStyle"),
                        Width = 34,
                        Height = 28,
                        Padding = new Thickness(0),
                        Margin = new Thickness(1)
                    };
                    button.Click += (_, __) =>
                    {
                        popup.IsOpen = false;
                        placed.Shape.StrokeThickness = thickness;
                        RenderShape(placed);
                        _isDirty = true;
                        UpdateTextFormatPalette();
                    };
                    thicknessPanel.Children.Add(button);
                }
                content.Children.Add(thicknessPanel);
            }

            popup.Child = new Border
            {
                Background = AppThemeManager.GetBrush("AppPanelBrush"),
                BorderBrush = AppThemeManager.GetBrush("AppStrongBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8,
                    ShadowDepth = 2,
                    Opacity = 0.22
                },
                Child = content
            };
            popup.IsOpen = true;
        }

        private static Button CreateQuickColorButton(
            string hex, string tooltip, string currentHex,
            Popup popup, Action<string> apply)
        {
            var button = new Button
            {
                Width = 27,
                Height = 23,
                Style = AppThemeManager.GetStyle("TileButtonStyle"),
                Padding = new Thickness(1),
                Margin = new Thickness(1),
                ToolTip = tooltip,
                BorderThickness = string.Equals(
                    hex, currentHex, StringComparison.OrdinalIgnoreCase)
                    ? new Thickness(2)
                    : new Thickness(1),
                BorderBrush = string.Equals(
                    hex, currentHex, StringComparison.OrdinalIgnoreCase)
                    ? Brushes.OrangeRed
                    : new SolidColorBrush(Color.FromRgb(215, 220, 225)),
                Content = new Rectangle
                {
                    Width = 21,
                    Height = 17,
                    Fill = ColorOption.BrushFromHex(hex),
                    Stretch = Stretch.Fill
                }
            };
            button.Click += (_, __) =>
            {
                popup.IsOpen = false;
                apply(hex);
            };
            return button;
        }

        private static Button CreatePaletteActionButton(
            string label, Action action)
        {
            var button = new Button
            {
                Content = label,
                Style = AppThemeManager.GetStyle("SubtleButtonStyle"),
                Height = 34,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(7, 3, 7, 3)
            };
            button.Click += (_, __) => action();
            return button;
        }

        private void ApplyQuickShapeColor(
            PlacedShape placed, bool isFill, string hex)
        {
            if (isFill) placed.Shape.FillHex = hex;
            else placed.Shape.StrokeHex = hex;
            RenderShape(placed);
            _isDirty = true;
            UpdateTextFormatPalette();
        }

        private void ShapeEditTextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedShape == null) return;
            BeginShapeTextEdit(
                _selectedShape,
                new Point(
                    Math.Abs(_selectedShape.Shape.Width) / 2,
                    Math.Max(20, Math.Abs(_selectedShape.Shape.Height)) / 2));
        }

        private void ShapeOrderButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedShape == null) return;

            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem(
                "맨 앞으로 가져오기",
                () => ChangeShapeOrder(
                    _selectedShape, ShapeOrderChange.BringToFront)));
            menu.Items.Add(MakeMenuItem(
                "한 단계 앞으로 가져오기",
                () => ChangeShapeOrder(
                    _selectedShape, ShapeOrderChange.BringForward)));
            menu.Items.Add(MakeMenuItem(
                "한 단계 뒤로 보내기",
                () => ChangeShapeOrder(
                    _selectedShape, ShapeOrderChange.SendBackward)));
            menu.Items.Add(MakeMenuItem(
                "맨 뒤로 보내기",
                () => ChangeShapeOrder(
                    _selectedShape, ShapeOrderChange.SendToBack)));
            ShapeOrderButton.ContextMenu = menu;
            menu.PlacementTarget = ShapeOrderButton;
            menu.IsOpen = true;
        }

        private void RenderShape(PlacedShape placed)
        {
            var c = placed.Container;
            var s = placed.Shape;
            UpgradeLegacyLineShape(s);
            if (s.Kind == "ElbowLine" &&
                _shapeLineEndpointDragPlaced != placed &&
                _shapeLineSegmentDragPlaced != placed)
            {
                SimplifyElbowLinePoints(s);
            }
            c.Children.Clear();

            Brush? fill = IsFullyTransparent(s.FillHex)
                ? null
                : HexToBrush(s.FillHex);
            var stroke = HexToBrush(s.StrokeHex);

            if (IsLineShapeKind(s.Kind))
            {
                var route = GetShapeLineRoute(s);
                double minX = route.Min(point => point.X);
                double minY = route.Min(point => point.Y);
                double maxX = route.Max(point => point.X);
                double maxY = route.Max(point => point.Y);
                const double linePadding = 8;
                Canvas.SetLeft(c, s.X + minX - linePadding);
                Canvas.SetTop(c, s.Y + minY - linePadding);
                c.Width = Math.Max(1, maxX - minX) + linePadding * 2;
                c.Height = Math.Max(1, maxY - minY) + linePadding * 2;
                var localRoute = route
                    .Select(point => new Point(
                        point.X - minX + linePadding,
                        point.Y - minY + linePadding))
                    .ToList();

                c.Children.Add(CreateShapePolyline(
                    localRoute, Brushes.Transparent, Math.Max(12, s.StrokeThickness + 8),
                    "Solid", "Square", "Square"));
                var visibleRoute = TrimShapePolylineForArrowCaps(
                    localRoute, s.StartLineCap, s.EndLineCap,
                    s.ArrowHeadSize, s.StrokeThickness);
                if (string.Equals(s.LineStyle, "Double", StringComparison.OrdinalIgnoreCase))
                {
                    double offset = Math.Max(1.5, s.StrokeThickness * 0.9);
                    c.Children.Add(CreateShapePolyline(
                        OffsetShapePolyline(visibleRoute, offset), stroke,
                        s.StrokeThickness, "Solid", "Square", "Square"));
                    c.Children.Add(CreateShapePolyline(
                        OffsetShapePolyline(visibleRoute, -offset), stroke,
                        s.StrokeThickness, "Solid", "Square", "Square"));
                }
                else
                {
                    c.Children.Add(CreateShapePolyline(
                        visibleRoute, stroke, s.StrokeThickness, s.LineStyle,
                        s.StartLineCap, s.EndLineCap));
                }

                if (s.StartLineCap == "Arrow" && localRoute.Count >= 2)
                    c.Children.Add(CreateLineArrowHead(
                        localRoute[1].X, localRoute[1].Y,
                        localRoute[0].X, localRoute[0].Y,
                        s.ArrowHeadSize, s.StrokeThickness, stroke));
                if (s.EndLineCap == "Arrow" && localRoute.Count >= 2)
                    c.Children.Add(CreateLineArrowHead(
                        localRoute[^2].X, localRoute[^2].Y,
                        localRoute[^1].X, localRoute[^1].Y,
                        s.ArrowHeadSize, s.StrokeThickness, stroke));

                var lineCenter = ShapeGeometry.Center(s);
                foreach (FrameworkElement visual in c.Children)
                    visual.RenderTransform = new ScaleTransform(s.FlipHorizontal ? -1 : 1, s.FlipVertical ? -1 : 1,
                        lineCenter.X - Canvas.GetLeft(c), lineCenter.Y - Canvas.GetTop(c));
                if (!string.IsNullOrWhiteSpace(s.Text))
                {
                    var textViewer = RichTextSupport.CreateViewer(
                        s.TextRtfBase64, s.Text, s.FontFamily, s.FontSize,
                        s.TextColorHex, s.TextBold, s.TextItalic,
                        s.TextUnderline, s.TextAlignment);
                    textViewer.Width = c.Width;
                    textViewer.Height = c.Height;
                    c.Children.Add(textViewer);
                }

                ApplyShapeRotation(placed);
                if (_selectedShapes.Contains(placed)) AddShapeSelectionVisuals(placed);
                return;
            }

            c.Width = Math.Max(1, s.Width);
            c.Height = Math.Max(1, s.Height);
            Canvas.SetLeft(c, s.X);
            Canvas.SetTop(c, s.Y);

            switch (s.Kind)
            {
                case "Image":
                    c.Children.Add(new Border
                    {
                        Width = s.Width,
                        Height = s.Height,
                        Background = Brushes.Transparent,
                        BorderBrush = stroke,
                        BorderThickness = new Thickness(s.StrokeThickness),
                        ClipToBounds = true,
                        Child = new Image
                        {
                            Source = ProjectImageService.Load(s.ImageDataBase64),
                            Stretch = ProjectImageService.ParseStretch(s.ImageStretch),
                            RenderTransformOrigin = new Point(.5, .5),
                            RenderTransform = new ScaleTransform(s.FlipHorizontal ? -1 : 1, s.FlipVertical ? -1 : 1),
                            Opacity = Math.Clamp(s.ImageOpacity, 0, 1)
                        }
                    });
                    break;
                default:
                    c.Children.Add(ShapeCatalog.Outline(s, fill, stroke));
                    break;
            }

            if (!string.IsNullOrWhiteSpace(s.Text))
            {
                AddShapeTextViewer(c, s);
            }

            ApplyShapeRotation(placed);
            if (_selectedShapes.Contains(placed)) AddShapeSelectionVisuals(placed);

        }

        private static bool IsLineShapeKind(string? kind)
            => kind is "Line" or "ElbowLine" or "ArrowLine" or "DoubleArrowLine";

        private static List<Point> GetShapeLineRoute(CanvasShape shape)
        {
            if (shape.Kind == "ElbowLine")
            {
                if (shape.LinePoints.Count < 2)
                {
                    double middleX = shape.Width / 2;
                    shape.LinePoints = Math.Abs(shape.Width) < 0.01 ||
                                       Math.Abs(shape.Height) < 0.01
                        ? new List<ESDP.Models.Point>
                        {
                            new() { X = 0, Y = 0 },
                            new() { X = shape.Width, Y = shape.Height }
                        }
                        : new List<ESDP.Models.Point>
                        {
                            new() { X = 0, Y = 0 },
                            new() { X = middleX, Y = 0 },
                            new() { X = middleX, Y = shape.Height },
                            new() { X = shape.Width, Y = shape.Height }
                        };
                }
                return shape.LinePoints
                    .Select(point => new Point(point.X, point.Y))
                    .ToList();
            }
            return new List<Point>
            {
                new(0, 0),
                new(shape.Width, shape.Height)
            };
        }

        private static Polyline CreateShapePolyline(
            IReadOnlyList<Point> points, Brush stroke, double thickness,
            string lineStyle, string startCap, string endCap)
        {
            var line = new Polyline
            {
                Points = new PointCollection(points),
                Stroke = stroke,
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Miter,
                StrokeStartLineCap = ToPenLineCap(startCap),
                StrokeEndLineCap = ToPenLineCap(endCap)
            };
            if (string.Equals(lineStyle, "Dash", StringComparison.OrdinalIgnoreCase))
                line.StrokeDashArray = new DoubleCollection { 5, 3 };
            else if (string.Equals(lineStyle, "Dot", StringComparison.OrdinalIgnoreCase))
            {
                line.StrokeDashArray = new DoubleCollection { 0.1, 2.4 };
                line.StrokeDashCap = PenLineCap.Round;
            }
            return line;
        }

        private static PenLineCap ToPenLineCap(string? cap)
            => string.Equals(cap, "Round", StringComparison.OrdinalIgnoreCase)
                ? PenLineCap.Round
                : PenLineCap.Square;

        private static List<Point> OffsetShapePolyline(
            IReadOnlyList<Point> points, double distance)
        {
            if (points.Count < 2) return points.ToList();
            var result = new List<Point>(points.Count);
            for (int index = 0; index < points.Count; index++)
            {
                Point previous = points[Math.Max(0, index - 1)];
                Point next = points[Math.Min(points.Count - 1, index + 1)];
                double dx = next.X - previous.X;
                double dy = next.Y - previous.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 0.001) length = 1;
                result.Add(new Point(
                    points[index].X - dy / length * distance,
                    points[index].Y + dx / length * distance));
            }
            return result;
        }

        private static List<Point> TrimShapePolylineForArrowCaps(
            IReadOnlyList<Point> points, string? startCap, string? endCap,
            double requestedSize, double thickness)
        {
            var result = points.ToList();
            if (result.Count < 2) return result;

            if (string.Equals(startCap, "Arrow", StringComparison.OrdinalIgnoreCase))
            {
                Point tip = result[0];
                Point inside = result[1];
                double dx = inside.X - tip.X;
                double dy = inside.Y - tip.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length > 0.01)
                {
                    double arrowLength = GetLineArrowHeadLength(
                        requestedSize, thickness, length);
                    result[0] = new Point(
                        tip.X + dx / length * arrowLength,
                        tip.Y + dy / length * arrowLength);
                }
            }

            if (string.Equals(endCap, "Arrow", StringComparison.OrdinalIgnoreCase))
            {
                Point tip = result[^1];
                Point inside = result[^2];
                double dx = inside.X - tip.X;
                double dy = inside.Y - tip.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length > 0.01)
                {
                    double arrowLength = GetLineArrowHeadLength(
                        requestedSize, thickness, length);
                    result[^1] = new Point(
                        tip.X + dx / length * arrowLength,
                        tip.Y + dy / length * arrowLength);
                }
            }

            return result;
        }

        private static double GetLineArrowHeadLength(
            double requestedSize, double thickness, double segmentLength)
        {
            double minimum = Math.Max(5, thickness * 2.2);
            double maximum = Math.Max(8, thickness * 4.0);
            double preferred = Math.Max(minimum, Math.Min(requestedSize, maximum));
            return Math.Min(preferred, segmentLength * 0.45);
        }

        private static void UpgradeLegacyLineShape(CanvasShape shape)
        {
            if (shape.Kind == "ArrowLine")
            {
                shape.Kind = "Line";
                shape.EndLineCap = "Arrow";
            }
            else if (shape.Kind == "DoubleArrowLine")
            {
                shape.Kind = "Line";
                shape.StartLineCap = "Arrow";
                shape.EndLineCap = "Arrow";
            }
            shape.LineStyle = string.IsNullOrWhiteSpace(shape.LineStyle)
                ? "Solid" : shape.LineStyle;
            shape.StartLineCap = string.IsNullOrWhiteSpace(shape.StartLineCap)
                ? "Square" : shape.StartLineCap;
            shape.EndLineCap = string.IsNullOrWhiteSpace(shape.EndLineCap)
                ? "Square" : shape.EndLineCap;
        }

        private static bool IsArrowShapeKind(string? kind)
            => kind is "ArrowLine" or "DoubleArrowLine" || ShapeCatalog.IsArrow(kind ?? "");

        private static Polygon CreateLineArrowHead(
            double fromX, double fromY, double tipX, double tipY,
            double requestedSize, double thickness, Brush brush)
        {
            double dx = tipX - fromX, dy = tipY - fromY;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 0.01) length = 1;
            double ux = dx / length, uy = dy / length;
            double size = GetLineArrowHeadLength(
                requestedSize, thickness, length);
            double halfWidth = Math.Max(thickness * 1.15, size * 0.42);
            double baseX = tipX - ux * size;
            double baseY = tipY - uy * size;
            double px = -uy * halfWidth, py = ux * halfWidth;

            return new Polygon
            {
                Points = new PointCollection
                {
                    new(tipX, tipY),
                    new(baseX + px, baseY + py),
                    new(baseX - px, baseY - py)
                },
                Fill = brush,
                Stroke = brush,
                StrokeThickness = 1
            };
        }

        // Preset shape geometry lives in ShapeCatalog.
        private static Brush HexToBrush(string hex)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { return Brushes.Transparent; }
        }

        private static bool IsFullyTransparent(string? hex)
        {
            string? normalized = ColorOption.NormalizeHex(hex);
            return normalized != null && normalized.Length == 9 &&
                   normalized.StartsWith("#00", StringComparison.Ordinal);
        }

        private static TextAlignment ParseTextAlignment(string value) => value switch
        {
            "Left" => TextAlignment.Left,
            "Right" => TextAlignment.Right,
            "Justify" => TextAlignment.Justify,
            _ => TextAlignment.Center
        };

        private RichTextBox CreateInlineTextEditor(
            FlowDocument document, double width, double height)
        {
            var editor = new RichTextBox(document)
            {
                Width = Math.Max(20, width),
                Height = Math.Max(20, height),
                AcceptsReturn = true,
                AcceptsTab = true,
                Background = new SolidColorBrush(Color.FromArgb(242, 255, 255, 255)),
                BorderBrush = AppThemeManager.GetBrush("AppAccentBrush"),
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(4, 1, 4, 1),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            SpellCheck.SetIsEnabled(editor, false);
            RichTextSupport.KeepVerticallyCentered(editor);
            editor.SelectionChanged += (_, __) => UpdateTextFormatPalette();
            editor.MouseLeftButtonDown += (_, e) => e.Handled = true;
            editor.MouseMove += (_, e) => e.Handled = true;
            editor.MouseLeftButtonUp += (_, e) => e.Handled = true;
            editor.PreviewKeyDown += ActiveTextEditor_PreviewKeyDown;
            return editor;
        }

        private void BeginShapeTextEdit(PlacedShape placed, Point clickPosition)
        {
            CommitActiveTextEdit();
            SelectShape(placed);

            var s = placed.Shape;
            if (string.IsNullOrWhiteSpace(s.Text) &&
                string.IsNullOrWhiteSpace(s.TextRtfBase64))
                s.TextAlignment = "Center";
            var document = RichTextSupport.CreateDocument(
                s.TextRtfBase64, s.Text, s.FontFamily, s.FontSize,
                s.TextColorHex, s.TextBold, s.TextItalic,
                s.TextUnderline, s.TextAlignment);
            var editor = CreateInlineTextEditor(
                document,
                Math.Max(20, ShapeCatalog.TextArea(s).Width),
                Math.Max(20, ShapeCatalog.TextArea(s).Height));

            _activeTextEditor = editor;
            _editingShape = placed;
            _editingDevice = null;
            var textArea = ShapeCatalog.TextArea(s);
            Canvas.SetLeft(editor, textArea.X);
            Canvas.SetTop(editor, textArea.Y);
            Panel.SetZIndex(editor, 1000);
            placed.Container.Children.Add(editor);
            FocusEditorAt(editor, new Point(clickPosition.X - textArea.X, clickPosition.Y - textArea.Y));
            StatusText.Text = "텍스트 편집 중 — 글자를 드래그해 선택한 뒤 위 팔레트에서 서식을 바꿀 수 있습니다.";
        }

        private void ShowShapeProperties(PlacedShape placed)
        {
            CommitActiveTextEdit();
            SelectShape(placed);

            var window = new ShapePropertiesWindow(placed.Shape)
            {
                Owner = this
            };
            if (window.ShowDialog() != true) return;

            var shape = placed.Shape;
            ShapeCatalog.CopyTo(window.ResultShape, shape);

            RenderShape(placed);
            _isDirty = true;
            UpdateTextFormatPalette();
            StatusText.Text = "도형 세부 설정을 적용했습니다.";
            UpdateCanvasExtent();
        }

        private void BeginDeviceTextEdit(PlacedDevice placed, Point clickPosition)
        {
            CommitActiveTextEdit();
            SelectOnly(placed);

            var device = EnsureEditableDevice(placed);
            var document = RichTextSupport.CreateDocument(
                device.NameRtfBase64, device.Name,
                device.NameFontFamily, device.NameFontSize,
                device.NameColorHex, device.NameBold, device.NameItalic,
                device.NameUnderline, device.NameTextAlignment);
            var editor = CreateInlineTextEditor(
                document,
                Math.Max(20, placed.Instance.Width - 8),
                Math.Max(20, placed.Instance.Height - 8));

            _activeTextEditor = editor;
            _editingShape = null;
            _editingDevice = placed;
            Canvas.SetLeft(editor, 4);
            Canvas.SetTop(editor, 4);
            Panel.SetZIndex(editor, 1000);
            placed.Container.Children.Add(editor);
            FocusEditorAt(editor, new Point(
                Math.Max(0, clickPosition.X - 4),
                Math.Max(0, clickPosition.Y - 4)));
            StatusText.Text = "장치 이름 편집 중 — 선택한 글자 범위에만 서식을 적용할 수 있습니다.";
        }

        private void FocusEditorAt(RichTextBox editor, Point position)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                editor.Focus();
                TextPointer? caret = editor.GetPositionFromPoint(position, true);
                if (caret != null) editor.CaretPosition = caret;
            }), DispatcherPriority.Input);
        }

        private void ActiveTextEditor_PreviewKeyDown(
            object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers == ModifierKeys.Control &&
                 e.Key == Key.Enter) || e.Key == Key.Escape)
            {
                CommitActiveTextEdit();
                e.Handled = true;
            }
        }

        private void CommitActiveTextEdit()
        {
            if (_activeTextEditor == null) return;

            var editor = _activeTextEditor;
            string plainText = RichTextSupport.GetPlainText(editor.Document);
            string rtf = RichTextSupport.Save(editor.Document);
            var shape = _editingShape;
            var devicePlaced = _editingDevice;

            _activeTextEditor = null;
            _editingShape = null;
            _editingDevice = null;

            if (shape != null)
            {
                shape.Shape.Text = plainText;
                shape.Shape.TextRtfBase64 = rtf;
                RenderShape(shape);
            }
            else if (devicePlaced != null)
            {
                var device = EnsureEditableDevice(devicePlaced);
                string originalName = devicePlaced.TemplateDevice.Name;
                device.Name = plainText;
                device.NameRtfBase64 = rtf;
                SynchronizeDeviceSharedData(
                    devicePlaced.TemplateDevice, device, originalName,
                    replacePorts: false);
                RenderDevice(devicePlaced);
            }

            _isDirty = true;
            UpdateTextFormatPalette();
        }

        private static void ApplyShapeRotation(PlacedShape placed)
        {
            var center = ShapeGeometry.Center(placed.Shape);
            placed.Container.RenderTransform = new RotateTransform(
                ShapeGeometry.NormalizeAngle(placed.Shape.RotationAngle),
                center.X - Canvas.GetLeft(placed.Container), center.Y - Canvas.GetTop(placed.Container));
        }

        private void AddShapeRotationHandle(PlacedShape placed)
        {
            var bounds = ShapeGeometry.LocalBounds(placed.Shape);
            double x = placed.Shape.X + bounds.X + bounds.Width / 2 - Canvas.GetLeft(placed.Container);
            double y = placed.Shape.Y + bounds.Y - Canvas.GetTop(placed.Container);
            var brush = AppThemeManager.GetBrush("AppAccentBrush");
            placed.Container.Children.Add(new Line { X1 = x, X2 = x, Y1 = y, Y2 = y - 24,
                Stroke = brush, StrokeThickness = 1, IsHitTestVisible = false });
            var handle = new Ellipse { Width = 12, Height = 12, Fill = Brushes.White,
                Stroke = brush, StrokeThickness = 2, Cursor = Cursors.Hand,
                ToolTip = "회전 / Shift: 15도 단위 / 더블클릭: 회전 초기화", Tag = "ShapeRotationHandle" };
            Canvas.SetLeft(handle, x - 6); Canvas.SetTop(handle, y - 30);
            handle.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    placed.Shape.RotationAngle = 0;
                    RenderShape(placed); UpdateCanvasExtent(); _isDirty = true;
                }
                else
                {
                    _rotatingShape = placed;
                    _rotationCenter = ShapeGeometry.Center(placed.Shape);
                    var pointer = e.GetPosition(MainCanvas) - _rotationCenter;
                    _rotationStartPointerAngle = Math.Atan2(pointer.Y, pointer.X) * 180 / Math.PI;
                    _rotationStartAngle = placed.Shape.RotationAngle;
                    placed.Container.CaptureMouse();
                }
                e.Handled = true;
            };
            placed.Container.Children.Add(handle);
        }

        private void AddShapeSelectionVisuals(PlacedShape placed)
        {
            var c = placed.Container;
            var s = placed.Shape;
            bool isLine = IsLineShapeKind(s.Kind);

            if (_selectedShapes.Count == 1) AddShapeRotationHandle(placed);
            if (isLine)
            {
                // 선 도형은 양 끝점과 구간 핸들만으로 선택 상태를 표시한다.
                // 외곽 점선 사각형은 실제 선보다 훨씬 크게 보여 편집을 방해한다.
                if (_selectedShapes.Count == 1) AddShapeLineHandles(placed);
                else
                {
                    var highlight = new Polyline { Points = new PointCollection(GetShapeLineRoute(s).Select(p =>
                        new Point(p.X - Canvas.GetLeft(c) + s.X, p.Y - Canvas.GetTop(c) + s.Y))),
                        Stroke = AppThemeManager.WithAlpha("AppAccentBrush", 100), StrokeThickness = 6, IsHitTestVisible = false };
                    c.Children.Add(highlight);
                }
                return;
            }

            var marker = new Rectangle
            {
                Width = Math.Max(1, Math.Abs(s.Width)),
                Height = Math.Max(1, Math.Abs(s.Height) < 1 ? 20 : Math.Abs(s.Height)),
                Stroke = AppThemeManager.GetBrush("AppAccentBrush"), StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = Brushes.Transparent, IsHitTestVisible = false
            };
            Canvas.SetLeft(marker, 0);
            Canvas.SetTop(marker, 0);
            c.Children.Add(marker);

            if (_selectedShapes.Count > 1) return;
            AddShapeHandle(placed, HandleKind.TopLeft, 0, 0);
            AddShapeHandle(placed, HandleKind.Top, s.Width / 2, 0);
            AddShapeHandle(placed, HandleKind.TopRight, s.Width, 0);
            AddShapeHandle(placed, HandleKind.Left, 0, s.Height / 2);
            AddShapeHandle(placed, HandleKind.Right, s.Width, s.Height / 2);
            AddShapeHandle(placed, HandleKind.BottomLeft, 0, s.Height);
            AddShapeHandle(placed, HandleKind.Bottom, s.Width / 2, s.Height);
            AddShapeHandle(placed, HandleKind.BottomRight, s.Width, s.Height);
            AddShapeAdjustmentHandles(placed);
        }

        private void AddShapeLineHandles(PlacedShape placed)
        {
            var shape = placed.Shape;
            var route = GetShapeLineRoute(shape);
            double minX = route.Min(point => point.X);
            double minY = route.Min(point => point.Y);
            const double padding = 8;
            var local = route.Select(point => new Point(
                point.X - minX + padding,
                point.Y - minY + padding)).ToList();
            double cx = (route.Min(p => p.X) + route.Max(p => p.X)) / 2 - minX + padding;
            double cy = (route.Min(p => p.Y) + route.Max(p => p.Y)) / 2 - minY + padding;
            for (int i = 0; i < local.Count; i++)
                local[i] = new Point(shape.FlipHorizontal ? 2 * cx - local[i].X : local[i].X,
                    shape.FlipVertical ? 2 * cy - local[i].Y : local[i].Y);

            AddShapeLineEndpointHandle(placed, 0, local[0]);
            AddShapeLineEndpointHandle(placed, local.Count - 1, local[^1]);

            if (shape.Kind != "ElbowLine") return;
            for (int index = 0; index < local.Count - 1; index++)
            {
                Point a = local[index];
                Point b = local[index + 1];
                bool horizontal = Math.Abs(a.Y - b.Y) <= Math.Abs(a.X - b.X);
                var handle = new Rectangle
                {
                    Width = 8,
                    Height = 8,
                    Fill = AppThemeManager.GetBrush("AppControlHoverBorderBrush"),
                    Stroke = Brushes.White,
                    StrokeThickness = 1,
                    Cursor = horizontal ? Cursors.SizeNS : Cursors.SizeWE,
                    ToolTip = "구간을 수직 방향으로 이동"
                };
                Canvas.SetLeft(handle, (a.X + b.X) / 2 - 4);
                Canvas.SetTop(handle, (a.Y + b.Y) / 2 - 4);
                int segmentIndex = index;
                handle.MouseLeftButtonDown += (_, e) =>
                {
                    if (shape.LinePoints.Count == 2)
                    {
                        var start = shape.LinePoints[0];
                        var end = shape.LinePoints[1];
                        shape.LinePoints.Insert(1, new ESDP.Models.Point
                        {
                            X = start.X,
                            Y = start.Y
                        });
                        shape.LinePoints.Insert(2, new ESDP.Models.Point
                        {
                            X = end.X,
                            Y = end.Y
                        });
                        segmentIndex = 1;
                    }
                    else if (segmentIndex == 0)
                    {
                        var start = shape.LinePoints[0];
                        shape.LinePoints.Insert(1, new ESDP.Models.Point
                        {
                            X = start.X,
                            Y = start.Y
                        });
                        segmentIndex = 1;
                    }
                    else if (segmentIndex == shape.LinePoints.Count - 2)
                    {
                        var end = shape.LinePoints[^1];
                        shape.LinePoints.Insert(shape.LinePoints.Count - 1,
                            new ESDP.Models.Point { X = end.X, Y = end.Y });
                    }
                    _shapeLineSegmentDragPlaced = placed;
                    _shapeLineSegmentDragIndex = segmentIndex;
                    _shapeLineDragStartPoints = shape.LinePoints
                        .Select(point => new ESDP.Models.Point { X = point.X, Y = point.Y })
                        .ToList();
                    placed.Container.CaptureMouse();
                    e.Handled = true;
                };
                placed.Container.Children.Add(handle);
            }
        }

        private void AddShapeLineEndpointHandle(
            PlacedShape placed, int pointIndex, Point position)
        {
            var handle = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = Brushes.White,
                Stroke = AppThemeManager.GetBrush("AppAccentBrush"),
                StrokeThickness = 1.4,
                Cursor = Cursors.Cross,
                ToolTip = "선 끝점 이동"
            };
            Canvas.SetLeft(handle, position.X - 4.5);
            Canvas.SetTop(handle, position.Y - 4.5);
            handle.MouseLeftButtonDown += (_, e) =>
            {
                _shapeLineEndpointDragPlaced = placed;
                _shapeLineEndpointDragIndex = pointIndex;
                _shapeLineDragStartPoints = GetShapeLineRoute(placed.Shape)
                    .Select(point => new ESDP.Models.Point { X = point.X, Y = point.Y })
                    .ToList();
                placed.Container.CaptureMouse();
                e.Handled = true;
            };
            placed.Container.Children.Add(handle);
        }

        private void AddShapeHandle(PlacedShape placed, HandleKind kind, double x, double y)
        {
            var cursor = kind switch
            {
                HandleKind.Top or HandleKind.Bottom => Cursors.SizeNS,
                HandleKind.Left or HandleKind.Right => Cursors.SizeWE,
                HandleKind.TopLeft or HandleKind.BottomRight => Cursors.SizeNWSE,
                _ => Cursors.SizeNESW
            };
            var handle = new Rectangle
            {
                Width = HandleSize, Height = HandleSize,
                Fill = Brushes.White, Stroke = AppThemeManager.GetBrush("AppAccentBrush"), StrokeThickness = 1,
                Cursor = cursor, Tag = kind,
                ToolTip = placed.Shape.Kind == "Image"
                    ? "드래그: 자유 크기 조절 / Shift+모서리: 현재 비율 유지"
                    : null
            };
            Canvas.SetLeft(handle, x - HandleSize / 2);
            Canvas.SetTop(handle, y - HandleSize / 2);
            handle.MouseLeftButtonDown += (s, e) =>
            {
                _resizingShape = placed;
                _shapeResizeKind = kind;
                _shapeResizeStartMouse = e.GetPosition(MainCanvas);
                _srsLeft = placed.Shape.X; _srsTop = placed.Shape.Y;
                _srsWidth = placed.Shape.Width; _srsHeight = placed.Shape.Height;
                placed.Container.CaptureMouse();
                e.Handled = true;
            };
            placed.Container.Children.Add(handle);
        }

        private void SelectShape(PlacedShape? placed)
        {
            var next = placed == null ? new HashSet<PlacedShape>() : ShapeGroup(placed).ToHashSet();
            var affected = _selectedShapes.Concat(next).Distinct().ToList();
            _selectedShapes.Clear(); _selectedShapes.UnionWith(next); _selectedShape = placed;
            if (_editingShapeVertices != placed) _editingShapeVertices = null;
            foreach (var item in affected) { RenderShape(item); ApplyShapeZOrder(item); }
            UpdateTextFormatPalette();
        }
        private void ToggleShapeSelect(PlacedShape placed)
        {
            var group = ShapeGroup(placed).ToList();
            bool remove = group.All(_selectedShapes.Contains);
            foreach (var item in group)
                if (remove) _selectedShapes.Remove(item); else _selectedShapes.Add(item);
            _selectedShape = remove ? _selectedShapes.LastOrDefault() : placed;
            _editingShapeVertices = null;
            foreach (var item in _placedShapes) RenderShape(item);
            UpdateTextFormatPalette();
        }
        private void SelectAddShapes(IEnumerable<PlacedShape> shapes)
        {
            foreach (var item in shapes.SelectMany(ShapeGroup).Distinct()) _selectedShapes.Add(item);
            _selectedShape ??= _selectedShapes.FirstOrDefault();
            foreach (var item in _selectedShapes) RenderShape(item);
            UpdateTextFormatPalette();
        }

        private void ShapeContainer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Canvas c) return;
            var placed = _placedShapes.FirstOrDefault(p => p.Container == c);
            if (placed == null) return;

            CommitActiveTextEdit();

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                ToggleShapeSelect(placed);
                e.Handled = true;
                return;
            }

            DeselectCable();
            if (!_selectedShapes.Contains(placed))
            {
                ClearCablePortSelection();
                ClearSelection();
                SelectShape(placed);
            }

            if (e.ClickCount == 2)
            {
                Point click = e.GetPosition(c);
                FrameworkElement? viewer = c.Children
                    .OfType<Border>()
                    .Select(border => border.Child as FrameworkElement)
                    .FirstOrDefault(child => child != null);
                viewer ??= c.Children.OfType<Grid>().FirstOrDefault();
                bool clickedText = viewer != null &&
                    RichTextSupport.IsPointOverText(
                        viewer, c.TranslatePoint(click, viewer));

                if (clickedText)
                    BeginShapeTextEdit(placed, click);
                else
                    ShowShapeProperties(placed);
                e.Handled = true;
                return;
            }

            _movingShape = placed;
            var mouse = e.GetPosition(MainCanvas);
            _moveStartMouse = mouse;
            _moveStartPositions = _selectedSet.ToDictionary(
                item => item,
                item => new Point(item.Instance.X, item.Instance.Y));
            _shapeMoveStartPositions = _selectedShapes.ToDictionary(
                item => item,
                item => new Point(item.Shape.X, item.Shape.Y));
            CaptureGroupCableGeometry();
            c.CaptureMouse();
            e.Handled = true;
        }

        private void ShapeContainer_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var mouse = e.GetPosition(MainCanvas);
            if (_adjustingShape != null)
            {
                MoveShapeAdjustment(mouse);
                return;
            }

            if (_rotatingShape != null)
            {
                var pointer = mouse - _rotationCenter;
                if (pointer.Length < 1) return;
                double angle = _rotationStartAngle + Math.Atan2(pointer.Y, pointer.X) * 180 / Math.PI - _rotationStartPointerAngle;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) angle = Math.Round(angle / 15) * 15;
                _rotatingShape.Shape.RotationAngle = ShapeGeometry.NormalizeAngle(angle);
                RenderShape(_rotatingShape);
                return;
            }
            if (_shapeLineEndpointDragPlaced != null)
            {
                var line = _shapeLineEndpointDragPlaced;
                var oldCenter = ShapeGeometry.Center(line.Shape);
                MoveShapeLineEndpoint(
                    _shapeLineEndpointDragPlaced,
                    _shapeLineEndpointDragIndex,
                    ShapeGeometry.ToEditingWorld(line.Shape, mouse));
                ShapeGeometry.PreserveCenterFrame(line.Shape, oldCenter);
                RenderShape(line);
                return;
            }

            if (_shapeLineSegmentDragPlaced != null)
            {
                var line = _shapeLineSegmentDragPlaced;
                var oldCenter = ShapeGeometry.Center(line.Shape);
                MoveShapeLineSegment(
                    _shapeLineSegmentDragPlaced,
                    _shapeLineSegmentDragIndex,
                    ShapeGeometry.ToEditingWorld(line.Shape, mouse));
                ShapeGeometry.PreserveCenterFrame(line.Shape, oldCenter);
                RenderShape(line);
                return;
            }

            if (_resizingShape != null)
            {
                var localDelta = ShapeGeometry.Rotate(mouse - _shapeResizeStartMouse, -_resizingShape.Shape.RotationAngle);
                double dx = localDelta.X, dy = localDelta.Y;
                double left = _srsLeft, top = _srsTop, w = _srsWidth, h = _srsHeight;
                switch (_shapeResizeKind)
                {
                    case HandleKind.BottomRight: w = _srsWidth + dx; h = _srsHeight + dy; break;
                    case HandleKind.BottomLeft: w = _srsWidth - dx; h = _srsHeight + dy; break;
                    case HandleKind.TopRight: w = _srsWidth + dx; h = _srsHeight - dy; break;
                    case HandleKind.TopLeft: w = _srsWidth - dx; h = _srsHeight - dy; break;
                    case HandleKind.Top: h = _srsHeight - dy; break;
                    case HandleKind.Bottom: h = _srsHeight + dy; break;
                    case HandleKind.Left: w = _srsWidth - dx; break;
                    case HandleKind.Right: w = _srsWidth + dx; break;
                }

                bool isCornerHandle = _shapeResizeKind is
                    HandleKind.TopLeft or HandleKind.TopRight or
                    HandleKind.BottomLeft or HandleKind.BottomRight;
                if (isCornerHandle &&
                    (Keyboard.Modifiers & ModifierKeys.Shift) != 0 &&
                    _srsWidth > 0.01 && _srsHeight > 0.01)
                {
                    double aspectRatio = _srsWidth / _srsHeight;
                    double rawWidth = Math.Max(1, w);
                    double rawHeight = Math.Max(1, h);
                    double heightFromWidth = rawWidth / aspectRatio;
                    double widthFromHeight = rawHeight * aspectRatio;

                    if (Math.Abs(heightFromWidth - rawHeight) <=
                        Math.Abs(widthFromHeight - rawWidth))
                    {
                        w = rawWidth;
                        h = heightFromWidth;
                    }
                    else
                    {
                        w = widthFromHeight;
                        h = rawHeight;
                    }

                    double minimumScale = Math.Max(10 / w, 10 / h);
                    if (minimumScale > 1)
                    {
                        w *= minimumScale;
                        h *= minimumScale;
                    }
                }
                else
                {
                    w = Math.Max(10, w);
                    h = Math.Max(10, h);
                    if (_resizingShape.Shape.Kind == "Image")
                        _resizingShape.Shape.ImageStretch = "Fill";
                }
                if (_shapeResizeKind is HandleKind.TopLeft or HandleKind.BottomLeft or HandleKind.Left)
                    left = _srsLeft + _srsWidth - w;
                if (_shapeResizeKind is HandleKind.TopLeft or HandleKind.TopRight or HandleKind.Top)
                    top = _srsTop + _srsHeight - h;

                _resizingShape.Shape.X = left; _resizingShape.Shape.Y = top;
                _resizingShape.Shape.Width = w; _resizingShape.Shape.Height = h;
                ShapeGeometry.PreserveCenterFrame(_resizingShape.Shape,
                    new Point(_srsLeft + _srsWidth / 2, _srsTop + _srsHeight / 2));
                RenderShape(_resizingShape);
                return;
            }

            if (_movingShape != null)
            {
                AutoScrollIfNearEdge(e);
                ApplyGroupMove(mouse);
            }
        }

        private void ShapeContainer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Canvas c) c.ReleaseMouseCapture();
            var editedLine = _shapeLineEndpointDragPlaced ?? _shapeLineSegmentDragPlaced;
            bool changed = _adjustingShape != null || _rotatingShape != null || _movingShape != null || _resizingShape != null ||
                           _shapeLineEndpointDragPlaced != null ||
                           _shapeLineSegmentDragPlaced != null;
            bool movedGroupCable = _groupMovePortStarts.Count > 0 ||
                                   _groupMoveBendStarts.Count > 0;
            _movingShape = null;
            _adjustingShape = null;
            _shapeAdjustmentStart = null;
            _rotatingShape = null;
            _resizingShape = null;
            _shapeLineEndpointDragPlaced = null;
            _shapeLineEndpointDragIndex = -1;
            _shapeLineSegmentDragPlaced = null;
            _shapeLineSegmentDragIndex = -1;
            _shapeLineDragStartPoints.Clear();
            ClearGroupMoveSnapshots();
            if (changed)
            {
                if (editedLine?.Shape.Kind == "ElbowLine")
                {
                    SimplifyElbowLinePoints(editedLine.Shape);
                    RenderShape(editedLine);
                }
                NormalizeCanvasOriginForNegativeContent();
                if (movedGroupCable) RenderAllCables();
            }
            UpdateCanvasExtent();
            if (changed) _isDirty = true;
        }

        private void MoveShapeLineEndpoint(
            PlacedShape placed, int pointIndex, Point mouse)
        {
            var shape = placed.Shape;
            if (shape.Kind == "Line")
            {
                if (pointIndex == 0)
                {
                    double endX = shape.X + shape.Width;
                    double endY = shape.Y + shape.Height;
                    var delta = SnapStraightLineDelta(endX - mouse.X, endY - mouse.Y);
                    shape.X = endX - delta.X;
                    shape.Y = endY - delta.Y;
                    shape.Width = delta.X;
                    shape.Height = delta.Y;
                }
                else
                {
                    var delta = SnapStraightLineDelta(mouse.X - shape.X, mouse.Y - shape.Y);
                    shape.Width = delta.X;
                    shape.Height = delta.Y;
                }
                RenderShape(placed);
                return;
            }

            if (shape.Kind != "ElbowLine" || shape.LinePoints.Count < 2) return;
            int index = pointIndex <= 0 ? 0 : shape.LinePoints.Count - 1;
            var point = shape.LinePoints[index];
            point.X = mouse.X - shape.X;
            point.Y = mouse.Y - shape.Y;

            int neighborIndex = index == 0 ? 1 : index - 1;
            if (_shapeLineDragStartPoints.Count == shape.LinePoints.Count)
            {
                var original = _shapeLineDragStartPoints[index];
                var originalNeighbor = _shapeLineDragStartPoints[neighborIndex];
                bool horizontal = Math.Abs(original.Y - originalNeighbor.Y) <=
                                  Math.Abs(original.X - originalNeighbor.X);
                if (horizontal)
                    shape.LinePoints[neighborIndex].Y = point.Y;
                else
                    shape.LinePoints[neighborIndex].X = point.X;
            }
            shape.Width = shape.LinePoints[^1].X;
            shape.Height = shape.LinePoints[^1].Y;
            RenderShape(placed);
        }

        private void MoveShapeLineSegment(
            PlacedShape placed, int segmentIndex, Point mouse)
        {
            var shape = placed.Shape;
            if (shape.Kind != "ElbowLine" || segmentIndex < 0 ||
                segmentIndex + 1 >= shape.LinePoints.Count) return;
            var first = shape.LinePoints[segmentIndex];
            var second = shape.LinePoints[segmentIndex + 1];
            var originalFirst = _shapeLineDragStartPoints[segmentIndex];
            var originalSecond = _shapeLineDragStartPoints[segmentIndex + 1];
            bool horizontal = Math.Abs(originalFirst.Y - originalSecond.Y) <=
                              Math.Abs(originalFirst.X - originalSecond.X);
            if (horizontal)
            {
                double y = mouse.Y - shape.Y;
                first.Y = y;
                second.Y = y;
            }
            else
            {
                double x = mouse.X - shape.X;
                first.X = x;
                second.X = x;
            }
            RenderShape(placed);
        }

        private static Point SnapStraightLineDelta(double dx, double dy)
        {
            const double snapAngleRadians = 2.0 * Math.PI / 180.0;
            double absX = Math.Abs(dx);
            double absY = Math.Abs(dy);
            if (absX < 0.001 && absY < 0.001) return new Point(dx, dy);

            double angleFromHorizontal = Math.Atan2(absY, absX);
            if (angleFromHorizontal <= snapAngleRadians)
                dy = 0;
            else if (Math.PI / 2.0 - angleFromHorizontal <= snapAngleRadians)
                dx = 0;
            return new Point(dx, dy);
        }

        private static void SimplifyElbowLinePoints(CanvasShape shape)
        {
            if (shape.Kind != "ElbowLine" || shape.LinePoints.Count < 2) return;
            var points = shape.LinePoints
                .Select(point => new Point(point.X, point.Y))
                .ToList();

            // Elbow lines are always orthogonal. Old/saved points can contain a
            // small accumulated drift, so choose the dominant axis first instead
            // of requiring every adjacent point to be within a fixed tolerance.
            for (int index = 1; index < points.Count; index++)
            {
                var previous = points[index - 1];
                var current = points[index];
                double deltaX = Math.Abs(current.X - previous.X);
                double deltaY = Math.Abs(current.Y - previous.Y);
                if (deltaX <= deltaY)
                    current.X = previous.X;
                else
                    current.Y = previous.Y;
                points[index] = current;
            }

            for (int index = points.Count - 1; index >= 1; index--)
            {
                if (Math.Abs(points[index].X - points[index - 1].X) <= 0.01 &&
                    Math.Abs(points[index].Y - points[index - 1].Y) <= 0.01)
                    points.RemoveAt(index);
            }

            // Reuse the cable-path rule for tiny orthogonal doglegs. These can be
            // almost invisible on screen while still producing several handles.
            CollapseShortOverlappingDoglegs(
                points,
                hasStartStub: false,
                hasEndStub: false);

            // A chain of collapsed doglegs can leave one short segment beside an
            // endpoint. Absorb that segment into the following/preceding axis so
            // a visually straight end does not keep an extra handle.
            const double shortEndSegment = 12.0;
            bool endChanged = true;
            while (endChanged && points.Count > 2)
            {
                endChanged = false;

                var first = points[0];
                var second = points[1];
                var third = points[2];
                double firstLength = Math.Abs(second.X - first.X) +
                                     Math.Abs(second.Y - first.Y);
                if (firstLength <= shortEndSegment)
                {
                    bool followingVertical =
                        Math.Abs(third.X - second.X) <=
                        Math.Abs(third.Y - second.Y);
                    points[2] = followingVertical
                        ? new Point(first.X, third.Y)
                        : new Point(third.X, first.Y);
                    points.RemoveAt(1);
                    endChanged = true;
                    continue;
                }

                int lastIndex = points.Count - 1;
                var beforePrevious = points[lastIndex - 2];
                var previous = points[lastIndex - 1];
                var last = points[lastIndex];
                double lastLength = Math.Abs(last.X - previous.X) +
                                    Math.Abs(last.Y - previous.Y);
                if (lastLength <= shortEndSegment)
                {
                    bool precedingVertical =
                        Math.Abs(previous.X - beforePrevious.X) <=
                        Math.Abs(previous.Y - beforePrevious.Y);
                    points[lastIndex - 2] = precedingVertical
                        ? new Point(last.X, beforePrevious.Y)
                        : new Point(beforePrevious.X, last.Y);
                    points.RemoveAt(lastIndex - 1);
                    endChanged = true;
                }
            }

            // Collapsing a dogleg can place neighboring points at the same
            // coordinate, so remove duplicates once more before collinearity.
            for (int index = points.Count - 1; index >= 1; index--)
            {
                if (Math.Abs(points[index].X - points[index - 1].X) <= 0.01 &&
                    Math.Abs(points[index].Y - points[index - 1].Y) <= 0.01)
                    points.RemoveAt(index);
            }

            bool changed = true;
            while (changed && points.Count > 2)
            {
                changed = false;
                for (int index = 1; index < points.Count - 1; index++)
                {
                    var previous = points[index - 1];
                    var current = points[index];
                    var next = points[index + 1];
                    bool vertical = Math.Abs(previous.X - current.X) < 0.01 &&
                                    Math.Abs(current.X - next.X) < 0.01;
                    bool horizontal = Math.Abs(previous.Y - current.Y) < 0.01 &&
                                      Math.Abs(current.Y - next.Y) < 0.01;
                    if (!vertical && !horizontal) continue;
                    points.RemoveAt(index);
                    changed = true;
                    break;
                }
            }

            shape.LinePoints = points
                .Select(point => new ESDP.Models.Point { X = point.X, Y = point.Y })
                .ToList();
            shape.Width = shape.LinePoints[^1].X;
            shape.Height = shape.LinePoints[^1].Y;
        }

        private void ShapeContainer_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Canvas c) return;
            var placed = _placedShapes.FirstOrDefault(p => p.Container == c);
            if (placed == null) return;
            if (!_selectedShapes.Contains(placed)) SelectShape(placed);

            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem(
                "도형 세부 설정...", () => ShowShapeProperties(placed)));
            AddShapeEditingMenu(menu, placed);
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("채우기 색상...", () => PickShapeColor(placed, isFill: true)));
            menu.Items.Add(MakeMenuItem("선 색상...", () => PickShapeColor(placed, isFill: false)));
            menu.Items.Add(MakeMenuItem("선 굵게", () => AdjustShapeStroke(placed, 0.5)));
            menu.Items.Add(MakeMenuItem("선 가늘게", () => AdjustShapeStroke(placed, -0.5)));
            menu.Items.Add(new Separator());
            var orderMenu = new MenuItem { Header = "앞뒤 순서" };
            orderMenu.Items.Add(MakeMenuItem(
                "맨 앞으로 가져오기",
                () => ChangeShapeOrder(
                    placed, ShapeOrderChange.BringToFront)));
            orderMenu.Items.Add(MakeMenuItem(
                "한 단계 앞으로 가져오기",
                () => ChangeShapeOrder(
                    placed, ShapeOrderChange.BringForward)));
            orderMenu.Items.Add(MakeMenuItem(
                "한 단계 뒤로 보내기",
                () => ChangeShapeOrder(
                    placed, ShapeOrderChange.SendBackward)));
            orderMenu.Items.Add(MakeMenuItem(
                "맨 뒤로 보내기",
                () => ChangeShapeOrder(
                    placed, ShapeOrderChange.SendToBack)));
            menu.Items.Add(orderMenu);
            if (IsArrowShapeKind(placed.Shape.Kind))
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem(
                    "화살촉 크게", () => AdjustArrowHead(placed, 3)));
                menu.Items.Add(MakeMenuItem(
                    "화살촉 작게", () => AdjustArrowHead(placed, -3)));
            }
            menu.Items.Add(MakeMenuItem("텍스트 편집...", () =>
            {
                BeginShapeTextEdit(
                    placed,
                    new Point(
                        Math.Abs(placed.Shape.Width) / 2,
                        Math.Max(20, Math.Abs(placed.Shape.Height)) / 2));
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("삭제", () => DeleteShape(placed)));

            c.ContextMenu = menu;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void AdjustShapeStroke(PlacedShape placed, double delta)
        {
            placed.Shape.StrokeThickness = Math.Max(
                0.5, Math.Min(20, placed.Shape.StrokeThickness + delta));
            RenderShape(placed);
            _isDirty = true;
        }

        private void AdjustArrowHead(PlacedShape placed, double delta)
        {
            placed.Shape.ArrowHeadSize = Math.Max(
                6, Math.Min(120, placed.Shape.ArrowHeadSize + delta));
            if (ShapeCatalog.IsArrow(placed.Shape.Kind))
            {
                double axis = placed.Shape.Kind is "UpArrow" or "DownArrow" ? placed.Shape.Height : placed.Shape.Width;
                placed.Shape.Adjustment1 = placed.Shape.ArrowHeadSize / Math.Max(1, axis);
            }
            RenderShape(placed);
            _isDirty = true;
        }

        private void PickShapeColor(PlacedShape placed, bool isFill)
        {
            string currentHex = isFill
                ? placed.Shape.FillHex
                : placed.Shape.StrokeHex;
            string title = isFill ? "도형 채우기 색" : "도형 선 색";
            if (!ColorPickerWindow.TryPick(
                    this, currentHex, out string hex, title))
                return;

            if (isFill) placed.Shape.FillHex = hex;
            else placed.Shape.StrokeHex = hex;
            RenderShape(placed);
            _isDirty = true;
        }

        private void DeleteShape(PlacedShape placed)
        {
            MainCanvas.Children.Remove(placed.Container);
            _placedShapes.Remove(placed);
            _selectedShapes.Remove(placed);
            if (_selectedShape == placed) _selectedShape = _selectedShapes.LastOrDefault();
            UpdateTextFormatPalette();
            UpdateCanvasExtent();
            _isDirty = true;
        }

        private void DeleteSelectedShapes()
        {
            foreach (var placed in _selectedShapes.ToList())
            {
                MainCanvas.Children.Remove(placed.Container);
                _placedShapes.Remove(placed);
            }
            _selectedShapes.Clear();
            _selectedShape = null;
            UpdateTextFormatPalette();
            UpdateCanvasExtent();
            _isDirty = true;
        }

        private void Menu_RegisterDevice(object sender, RoutedEventArgs e)
        {
            var win = new DeviceRegisterWindow(customKindNames: _customDeviceKinds,
                categoryNames: _deviceCategoryNames, categoryColors: _deviceCategoryColors) { Owner = this };
            if (win.ShowDialog() == true && win.ResultDevice != null)
            {
                _registeredDevices.Add(new DeviceListItem(win.ResultDevice));
                MergeCustomDeviceKinds(win.CustomKindNames);
                StatusText.Text = $"장치 '{win.ResultDevice.Name}' 추가됨";
                _isDirty = true;
            }
        }

        private void RestoreDeviceCategories(Project project)
        {
            _deviceCategoryNames.Clear();
            _deviceCategoryNames.AddRange((project.DeviceCategoryNames ?? Enum.GetNames<DeviceKind>().ToList())
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase));
            _deviceCategoryColors.Clear();
            if (project.DeviceCategoryNames == null)
                foreach (var pair in AppSettings.KindColors) _deviceCategoryColors[pair.Key.ToString()] = pair.Value;
            foreach (var pair in project.DeviceCategoryColors)
                if (ColorOption.NormalizeHex(pair.Value) is { } hex) _deviceCategoryColors[pair.Key] = hex;
        }

        private void ApplyDeviceCategoryColors()
        {
            foreach (var device in _registeredDevices.Select(item => item.Device)
                         .Concat(_sheets.SelectMany(sheet => sheet.DeviceInstances)
                             .Where(instance => instance.Override != null).Select(instance => instance.Override!))
                         .Concat(_placed.Select(item => item.EffectiveDevice)))
            {
                string name = string.IsNullOrWhiteSpace(device.CustomKindName) ? device.Kind.ToString() : device.CustomKindName;
                device.CategoryColorHex = _deviceCategoryColors.GetValueOrDefault(name, "");
            }
            foreach (var device in _placed) RenderDevice(device);
            foreach (var item in _registeredDevices) item.Refresh();
        }

        private void DeviceGroupHeader_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string name } element && _deviceCategoryNames.Contains(name))
            { OpenDeviceGroupMenu(element, name); e.Handled = true; }
        }

        private void OpenDeviceGroupMenu(FrameworkElement element, string name)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem("범주 색상 설정...", () =>
            {
                string current = _deviceCategoryColors.GetValueOrDefault(name, "#FFFFFF");
                if (!ColorPickerWindow.TryPick(this, current, out string hex, $"{name} 범주 색상")) return;
                _deviceCategoryColors[name] = hex;
                ApplyDeviceCategoryColors();
                _isDirty = true;
            }));
            menu.Items.Add(MakeMenuItem("그룹 이름 변경", () => RenameCustomDeviceGroup(name)));
            menu.Items.Add(MakeMenuItem("그룹 삭제", () => DeleteCustomDeviceGroup(name)));
            element.ContextMenu = menu;
            menu.PlacementTarget = element;
            menu.IsOpen = true;
        }

        private void ConfigureSidebarViews()
        {
            _deviceSidebarView = CollectionViewSource.GetDefaultView(
                _registeredDevices);
            _cableSidebarView = CollectionViewSource.GetDefaultView(
                _registeredCables);

            _deviceSidebarView.GroupDescriptions.Clear();
            var deviceGroups = new PropertyGroupDescription(
                nameof(DeviceListItem.GroupName));
            foreach (string name in _customDeviceKinds.Concat(_registeredDevices.Select(item => item.GroupName)))
                if (!_deviceCategoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    _deviceCategoryNames.Add(name);
            foreach (string name in _deviceCategoryNames) deviceGroups.GroupNames.Add(name);
            ApplyDeviceCategoryColors();
            _deviceSidebarView.GroupDescriptions.Add(deviceGroups);
            _cableSidebarView.GroupDescriptions.Clear();
            var cableGroups = new PropertyGroupDescription(
                nameof(CableListItem.GroupName));
            foreach (CableCategory category in Enum.GetValues(typeof(CableCategory)))
                cableGroups.GroupNames.Add(category.ToString());
            foreach (string name in _customCableCategories) cableGroups.GroupNames.Add(name);
            _cableSidebarView.GroupDescriptions.Add(cableGroups);
            ApplySidebarSorting();
        }

        private void MergeCustomDeviceKinds(IEnumerable<string> names)
        {
            bool changed = false;
            foreach (string name in names.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (_customDeviceKinds.Contains(name, StringComparer.CurrentCultureIgnoreCase)) continue;
                _customDeviceKinds.Add(name);
                changed = true;
            }
            if (changed) ConfigureSidebarViews();
        }

        private void MergeCustomCableCategories(IEnumerable<string> names)
        {
            bool changed = false;
            foreach (string name in names.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (_customCableCategories.Contains(name, StringComparer.CurrentCultureIgnoreCase)) continue;
                _customCableCategories.Add(name);
                changed = true;
            }
            if (changed) ConfigureSidebarViews();
        }

        private void AddDeviceCategoryButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CategoryNameWindow("장치") { Owner = this };
            if (dialog.ShowDialog() != true) return;
            string name = dialog.ResultName;
            if (_deviceCategoryNames.Contains(
                    name, StringComparer.CurrentCultureIgnoreCase))
            {
                MessageBox.Show("같은 이름의 장치 범주가 이미 있습니다.", "범주 추가",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _customDeviceKinds.Add(name);
            ConfigureSidebarViews();
            _isDirty = true;
            StatusText.Text = $"장치 범주 '{name}' 추가됨";
        }

        private void AddCableCategoryButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CategoryNameWindow("케이블") { Owner = this };
            if (dialog.ShowDialog() != true) return;
            string name = dialog.ResultName;
            if (Enum.TryParse<CableCategory>(name, out _) || _customCableCategories.Contains(
                    name, StringComparer.CurrentCultureIgnoreCase))
            {
                MessageBox.Show("같은 이름의 케이블 범주가 이미 있습니다.", "범주 추가",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _customCableCategories.Add(name);
            ConfigureSidebarViews();
            _isDirty = true;
            StatusText.Text = $"케이블 범주 '{name}' 추가됨";
        }

        private void DeviceGroupAddButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string groupName }) return;
            bool builtIn = Enum.TryParse(groupName, out DeviceKind kind);

            var window = new DeviceRegisterWindow(
                preferredKind: builtIn ? kind : null,
                customKindNames: _customDeviceKinds,
                categoryNames: _deviceCategoryNames, categoryColors: _deviceCategoryColors,
                preferredCustomKind: builtIn ? null : groupName) { Owner = this };
            if (window.ShowDialog() == true && window.ResultDevice != null)
            {
                _registeredDevices.Add(new DeviceListItem(window.ResultDevice));
                MergeCustomDeviceKinds(window.CustomKindNames);
                StatusText.Text = $"장치 '{window.ResultDevice.Name}' 추가됨";
                _isDirty = true;
            }
            e.Handled = true;
        }

        private void DeviceGroupManageButton_Click(
            object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string groupName } button) return;
            OpenDeviceGroupMenu(button, groupName);
            e.Handled = true;
        }

        private void RenameCustomDeviceGroup(string oldName)
        {
            var dialog = new CategoryNameWindow(
                "장치", oldName, rename: true) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            string newName = dialog.ResultName;
            if (newName.Equals(oldName, StringComparison.CurrentCulture)) return;
            if (_deviceCategoryNames.Contains(
                    newName, StringComparer.CurrentCultureIgnoreCase))
            {
                MessageBox.Show("같은 이름의 장치 그룹이 이미 있습니다.",
                    "장치 그룹", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            RenameDeviceCategory(oldName, newName);
        }

        private void RenameDeviceCategory(string oldName, string newName)
        {
            CaptureCurrentSheet();
            int index = _customDeviceKinds.FindIndex(name =>
                name.Equals(oldName, StringComparison.CurrentCultureIgnoreCase));
            if (index >= 0) _customDeviceKinds[index] = newName;
            else _customDeviceKinds.Add(newName);
            int categoryIndex = _deviceCategoryNames.FindIndex(name => name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            if (categoryIndex >= 0) _deviceCategoryNames[categoryIndex] = newName;
            if (_deviceCategoryColors.Remove(oldName, out var oldColor)) _deviceCategoryColors[newName] = oldColor;

            foreach (var item in _registeredDevices.Where(item =>
                         item.GroupName.Equals(
                             oldName, StringComparison.CurrentCultureIgnoreCase)))
            {
                item.Device.CustomKindName = newName;
                item.Refresh();
            }
            foreach (var instance in _sheets.SelectMany(
                         sheet => sheet.DeviceInstances))
            {
                if (instance.Override != null &&
                    (instance.Override.CustomKindName.Equals(
                         oldName, StringComparison.CurrentCultureIgnoreCase) ||
                     string.IsNullOrWhiteSpace(instance.Override.CustomKindName) &&
                     instance.Override.Kind.ToString().Equals(
                         oldName, StringComparison.CurrentCultureIgnoreCase)))
                    instance.Override.CustomKindName = newName;
            }

            ConfigureSidebarViews();
            _isDirty = true;
            StatusText.Text = $"장치 그룹 '{oldName}' → '{newName}' 변경됨";
        }

        private void DeleteCustomDeviceGroup(string groupName)
        {
            int memberCount = _registeredDevices.Count(item =>
                item.GroupName.Equals(
                    groupName, StringComparison.CurrentCultureIgnoreCase));
            string fallbackName = _deviceCategoryNames.FirstOrDefault(name =>
                !name.Equals(groupName, StringComparison.OrdinalIgnoreCase)) ??
                (groupName == "기타" ? "미분류" : "기타");
            bool fallbackBuiltIn = Enum.TryParse(fallbackName, out DeviceKind fallbackKind);
            if (!fallbackBuiltIn) fallbackKind = DeviceKind.기타;
            string message = memberCount > 0
                ? $"'{groupName}' 그룹을 삭제하고 소속 장치 {memberCount}개를 '{fallbackName}' 그룹으로 옮길까요?"
                : $"'{groupName}' 그룹을 삭제할까요?";
            if (MessageBox.Show(message, "장치 그룹 삭제",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) !=
                MessageBoxResult.Yes)
                return;

            DeleteDeviceCategory(groupName, fallbackName);
        }

        private void DeleteDeviceCategory(string groupName, string fallbackName)
        {
            CaptureCurrentSheet();
            bool fallbackBuiltIn = Enum.TryParse(fallbackName, out DeviceKind fallbackKind);
            if (!fallbackBuiltIn) fallbackKind = DeviceKind.기타;
            _customDeviceKinds.RemoveAll(name => name.Equals(
                groupName, StringComparison.CurrentCultureIgnoreCase));
            _deviceCategoryNames.RemoveAll(name => name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
            _deviceCategoryColors.Remove(groupName);
            foreach (var item in _registeredDevices.Where(item =>
                         item.GroupName.Equals(
                             groupName, StringComparison.CurrentCultureIgnoreCase)))
            {
                item.Device.CustomKindName = fallbackBuiltIn ? "" : fallbackName;
                item.Device.Kind = fallbackKind;
                item.Refresh();
            }
            foreach (var instance in _sheets.SelectMany(
                         sheet => sheet.DeviceInstances))
            {
                if (instance.Override == null ||
                    !(instance.Override.CustomKindName.Equals(
                          groupName, StringComparison.CurrentCultureIgnoreCase) ||
                      string.IsNullOrWhiteSpace(instance.Override.CustomKindName) &&
                      instance.Override.Kind.ToString().Equals(
                          groupName, StringComparison.CurrentCultureIgnoreCase)))
                    continue;
                instance.Override.CustomKindName = fallbackBuiltIn ? "" : fallbackName;
                instance.Override.Kind = fallbackKind;
            }

            ConfigureSidebarViews();
            _isDirty = true;
            StatusText.Text = $"장치 그룹 '{groupName}' 삭제됨";
        }

        private void ChangeRegisteredDeviceGroup(
            DeviceListItem item, string groupName)
        {
            if (Enum.TryParse(groupName, out DeviceKind kind))
            {
                item.Device.Kind = kind;
                item.Device.CustomKindName = "";
            }
            else
            {
                item.Device.Kind = DeviceKind.기타;
                item.Device.CustomKindName = groupName;
            }

            foreach (var instance in _sheets.SelectMany(
                         sheet => sheet.DeviceInstances)
                         .Where(instance => instance.DeviceId == item.Device.Id))
            {
                if (instance.Override == null) continue;
                instance.Override.Kind = item.Device.Kind;
                instance.Override.CustomKindName = item.Device.CustomKindName;
            }
            item.Refresh();
            ConfigureSidebarViews();
            _isDirty = true;
            StatusText.Text = $"장치 '{item.Name}' 소속 그룹: {groupName}";
        }

        private void CableGroupAddButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string groupName }) return;
            bool builtIn = Enum.TryParse(groupName, out CableCategory category);

            var window = new CableRegisterWindow(
                preferredCategory: builtIn ? category : null,
                customCategoryNames: _customCableCategories,
                preferredCustomCategory: builtIn ? null : groupName) { Owner = this };
            if (window.ShowDialog() == true && window.ResultCable != null)
            {
                _registeredCables.Add(new CableListItem(window.ResultCable));
                MergeCustomCableCategories(window.CustomCategoryNames);
                StatusText.Text = $"케이블 '{window.ResultCable.Name}' 추가됨";
                _isDirty = true;
            }
            e.Handled = true;
        }

        private void SidebarSortCombo_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            if (!IsInitialized) return;
            ApplySidebarSorting();
        }

        private void ApplySidebarSorting()
        {
            ApplySidebarSorting(
                _deviceSidebarView,
                DeviceSortCombo?.SelectedIndex == 1,
                nameof(DeviceListItem.GroupOrder),
                nameof(DeviceListItem.Name),
                nameof(DeviceListItem.CreationOrder));
            ApplySidebarSorting(
                _cableSidebarView,
                CableSortCombo?.SelectedIndex == 1,
                nameof(CableListItem.GroupOrder),
                nameof(CableListItem.Name),
                nameof(CableListItem.CreationOrder));
        }

        private static void ApplySidebarSorting(
            ICollectionView? view, bool byName,
            string groupProperty, string nameProperty,
            string creationProperty)
        {
            if (view == null) return;
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription(
                    groupProperty, ListSortDirection.Ascending));
                view.SortDescriptions.Add(new SortDescription(
                    byName ? nameProperty : creationProperty,
                    ListSortDirection.Ascending));
            }
        }

        private void RefreshSidebarViews()
        {
            _deviceSidebarView?.Refresh();
            _cableSidebarView?.Refresh();
        }

        private void Menu_RegisterCable(object sender, RoutedEventArgs e)
        {
            var win = new CableRegisterWindow(customCategoryNames: _customCableCategories) { Owner = this };
            if (win.ShowDialog() == true && win.ResultCable != null)
            {
                _registeredCables.Add(new CableListItem(win.ResultCable));
                MergeCustomCableCategories(win.CustomCategoryNames);
                StatusText.Text = $"케이블 '{win.ResultCable.Name}' 추가됨";
                _isDirty = true;
            }
        }

        private Point _cableSidebarDragStart;
        private CableListItem? _cableSidebarDragCandidate;
        private bool _isCableSidebarDragging;

        private void CableListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _cableSidebarDragStart = e.GetPosition(null);
            _cableSidebarDragCandidate = null;
            _isCableSidebarDragging = false;

            if (e.OriginalSource is not DependencyObject src) return;
            var lbi = FindParent<ListBoxItem>(src);
            if (lbi?.DataContext is not CableListItem item) return;

            if (e.ClickCount == 2)
            {
                OpenCableTemplateProperties(item);
                e.Handled = true;
                return;
            }

            _cableSidebarDragCandidate = item;
        }

        private void OpenCableTemplateProperties(CableListItem item)
        {
            var cable = item.Cable;
            string originalName = cable.Name;
            var rows = cable.Connectors.Select(connector =>
                new CablePortPropertyRow
                {
                    ConnectorId = connector.Id,
                    PortName = connector.PortName,
                    ConnType = connector.ConnType,
                    ConnectorCatalogKey = connector.ConnectorCatalogKey,
                    Description = connector.Description,
                    KeyAngle = connector.KeyAngle,
                    ShowConnType = connector.ShowConnType,
                    ShowLabel = connector.ShowLabel,
                    LabelFontSize = connector.LabelFontSize,
                    LabelColorHex = connector.LabelColorHex,
                    Pins = CloneCablePins(connector.Pins),
                    ConnectionStatus = "캔버스에 배치되지 않은 기본 포트"
                }).ToList();

            var window = new CablePropertiesWindow(cable, rows, true, _customCableCategories)
            {
                Owner = this
            };
            if (window.ShowDialog() != true) return;

            var editedCable = CloneCableDefinition(cable);
            editedCable.Name = window.ResultCableName;
            editedCable.NameFontSize = window.ResultNameFontSize;
            editedCable.Category = window.ResultCategory;
            editedCable.CustomCategoryName = window.ResultCustomCategoryName;
            editedCable.LineColorHex = window.ResultCableLineColorHex;
            editedCable.OverallBraid = window.ResultOverallBraid;
            editedCable.Tube = window.ResultTube;
            editedCable.SignalRows = CloneCableSignalRows(window.ResultSignalRows);
            editedCable.Connectors = window.ResultPortRows.Select(edited => new CableConnector
            {
                Id = edited.ConnectorId,
                PortName = edited.PortName.Trim(),
                ConnType = edited.ConnType.Trim(),
                ConnectorCatalogKey = edited.ConnectorCatalogKey,
                Description = edited.Description.Trim(),
                KeyAngle = edited.KeyAngle.Trim(),
                ShowConnType = edited.ShowConnType,
                ShowLabel = edited.ShowLabel,
                LabelFontSize = edited.LabelFontSize > 0 ? edited.LabelFontSize : 9,
                LabelColorHex = ColorOption.NormalizeHex(edited.LabelColorHex) ?? "#000000",
                Pins = CloneCablePins(edited.Pins)
            }).ToList();

            SynchronizeCableSharedData(cable, editedCable, originalName);

            item.Refresh();
            RefreshSidebarViews();
            RenderAllCables();
            StatusText.Text = $"케이블 '{cable.Name}' 수정됨";
            _isDirty = true;
        }

        private void CableListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isCableSidebarDragging) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (_cableSidebarDragCandidate == null) return;

            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _cableSidebarDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _cableSidebarDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _isCableSidebarDragging = true;
            var data = new DataObject(typeof(Cable), _cableSidebarDragCandidate.Cable);
            DragDrop.DoDragDrop(CableListBox, data, DragDropEffects.Copy);
            _isCableSidebarDragging = false;
            _cableSidebarDragCandidate = null;
        }

        private void CableListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _cableSidebarDragCandidate = null;
            _isCableSidebarDragging = false;
        }

        private void CableListBox_PreviewMouseRightButtonDown(
            object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source) return;
            var item = FindParent<ListBoxItem>(source)?.DataContext as CableListItem;
            if (item == null) return;

            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem("케이블 복제", () =>
                DuplicateRegisteredCable(item)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("케이블 삭제", () =>
                DeleteRegisteredCable(item)));
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void DeleteRegisteredCable(CableListItem item)
        {
            int usedCount = _sheets.Sum(sheet => sheet.CableInstances.Count(
                instance => instance.CableId == item.Cable.Id));
            string message = usedCount > 0
                ? $"'{item.Name}' 케이블과 도면에 배치된 {usedCount}개 개체를 모두 삭제할까요?"
                : $"'{item.Name}' 케이블을 목록에서 삭제할까요?";
            if (MessageBox.Show(message, "케이블 삭제",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) !=
                MessageBoxResult.Yes)
                return;

            var removedInstanceIds = _sheets
                .SelectMany(sheet => sheet.CableInstances)
                .Where(instance => instance.CableId == item.Cable.Id)
                .Select(instance => instance.Id)
                .ToHashSet(StringComparer.Ordinal);
            removedInstanceIds.UnionWith(_placedCables
                .Where(placed => placed.TemplateCable == item.Cable)
                .Select(placed => placed.Instance.Id));
            foreach (var sheet in _sheets)
            {
                sheet.CableInstances.RemoveAll(
                    instance => instance.CableId == item.Cable.Id);
                foreach (var cable in sheet.CableInstances)
                foreach (var layout in AllPortLayouts(cable))
                {
                    var parts = layout.ConnectedToPortKey?.Split(':');
                    if (parts is { Length: 3 } && parts[0] == "cab" &&
                        removedInstanceIds.Contains(parts[1]))
                        layout.ConnectedToPortKey = null;
                }
            }
            foreach (var remaining in _placedCables)
            foreach (var layout in AllPortLayouts(remaining.Instance))
            {
                var parts = layout.ConnectedToPortKey?.Split(':');
                if (parts is { Length: 3 } && parts[0] == "cab" &&
                    removedInstanceIds.Contains(parts[1]))
                    layout.ConnectedToPortKey = null;
            }
            foreach (var placed in _placedCables.Where(
                         placed => placed.TemplateCable == item.Cable).ToList())
            {
                MainCanvas.Children.Remove(placed.Container);
                _placedCables.Remove(placed);
                if (_selectedCable == placed) _selectedCable = null;
            }
            _registeredCables.Remove(item);
            RepairCableToCableConnections();
            // 격자 통로 규칙이 생기기 전에 저장된 파일은 포트 바로 바깥의
            // 꺾임점이 외곽 셀 안쪽에 남아 있을 수 있다. 선택/재렌더링 때마다
            // 선을 바꾸지는 않고, 시트를 펼치는 이 시점에만 새 통로 경계로
            // 호환 보정한다.
            foreach (var cable in _placedCables)
                NormalizeDeviceConnectedPortApproaches(cable.Instance);
            AlignDeviceApproachCorridorLanes();
            RenderAllCables();
            UpdateCanvasExtent();
            UpdateTextFormatPalette();
            _isDirty = true;
        }

        private void DuplicateRegisteredCable(CableListItem item)
        {
            var copy = DeepClone(item.Cable);
            copy.Id = Guid.NewGuid().ToString();
            copy.Name = MakeDuplicateName(
                item.Cable.Name,
                _registeredCables.Select(existing => existing.Name));
            var connectorIdMap = new Dictionary<string, string>();
            foreach (var connector in copy.Connectors)
            {
                string oldId = connector.Id;
                connector.Id = Guid.NewGuid().ToString();
                connectorIdMap[oldId] = connector.Id;
            }
            var signalIdMap = copy.SignalRows.ToDictionary(
                row => row.Id, _ => Guid.NewGuid().ToString());
            foreach (var row in copy.SignalRows)
            {
                string oldParentId = row.ParentSignalId;
                row.Id = signalIdMap[row.Id];
                row.ParentSignalId = string.IsNullOrWhiteSpace(oldParentId)
                    ? ""
                    : signalIdMap.GetValueOrDefault(oldParentId, "");
                row.PortPins = row.PortPins.ToDictionary(
                    pair => connectorIdMap.TryGetValue(pair.Key, out string? newId)
                        ? newId
                        : pair.Key,
                    pair => pair.Value);
            }

            _registeredCables.Add(new CableListItem(copy));
            RefreshSidebarViews();
            StatusText.Text = $"케이블 '{copy.Name}' 복제됨";
            _isDirty = true;
        }

        private static string MakeDuplicateName(
            string original, IEnumerable<string> existingNames)
        {
            string baseName = original.Trim();
            int open = baseName.LastIndexOf(" (", StringComparison.Ordinal);
            if (open >= 0 && baseName.EndsWith(")", StringComparison.Ordinal) &&
                int.TryParse(baseName[(open + 2)..^1], out _))
                baseName = baseName[..open];

            var used = existingNames.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            for (int number = 1; ; number++)
            {
                string candidate = $"{baseName} ({number})";
                if (!used.Contains(candidate)) return candidate;
            }
        }

        // ================= 장치 수정: 같은 템플릿을 쓰는 모든 개체에 적용 =================
        private void OpenEditForInstance(PlacedDevice placed)
        {
            var template = placed.TemplateDevice;
            var editableClone = CloneDevice(template);
            var win = new DeviceRegisterWindow(editableClone, placed.Instance.Width, placed.Instance.Height,
                customKindNames: _customDeviceKinds,
                categoryNames: _deviceCategoryNames, categoryColors: _deviceCategoryColors,
                editInstancePurpose: true,
                instancePurpose: placed.Instance.Purpose,
                availableZones: _placedZones.Select(item => item.Zone),
                instanceZoneId: FindContainingZoneId(placed))
            { Owner = this };
            if (win.ShowDialog() != true || win.ResultDevice == null) return;

            if (!ApplyDeviceEditToAllInstances(template, win.ResultDevice)) return;
            placed.Instance.Purpose = win.ResultPurpose;
            placed.Instance.ZoneId = win.ResultZoneId;
            foreach (PlacedZone zone in _placedZones)
                zone.Zone.MemberDeviceInstanceIds.RemoveAll(
                    id => id == placed.Instance.Id);
            PlacedZone? selectedZone = _placedZones.FirstOrDefault(
                zone => zone.Zone.Id == placed.Instance.ZoneId);
            if (selectedZone != null)
                selectedZone.Zone.MemberDeviceInstanceIds.Add(
                    placed.Instance.Id);
            RenderDevice(placed);
            _isDirty = true;
        }

        // ================= 수정: 사이드바의 템플릿 자체 =================
        private void OpenEditForTemplate(Device template)
        {
            var editableClone = CloneDevice(template);
            var win = new DeviceRegisterWindow(editableClone,
                customKindNames: _customDeviceKinds,
                categoryNames: _deviceCategoryNames, categoryColors: _deviceCategoryColors) { Owner = this };
            if (win.ShowDialog() != true || win.ResultDevice == null) return;

            ApplyDeviceEditToAllInstances(template, win.ResultDevice);
        }

        private bool ApplyDeviceEditToAllInstances(Device template, Device edited)
        {
            CaptureCurrentSheet();
            var pendingLayouts = new Dictionary<DeviceInstance, Device>();
            var templateIds = _registeredDevices.Where(item =>
                item.Device.Id == template.Id || item.Device.Name.Equals(template.Name,
                    StringComparison.OrdinalIgnoreCase) || item.Device.Name.Equals(edited.Name,
                    StringComparison.OrdinalIgnoreCase)).Select(item => item.Device.Id).ToHashSet();
            foreach (var instance in _sheets.SelectMany(sheet => sheet.DeviceInstances)
                         .Where(instance => templateIds.Contains(instance.DeviceId)))
            {
                var instanceTemplate = _registeredDevices.First(item => item.Device.Id == instance.DeviceId).Device;
                var original = instance.Override ?? instanceTemplate;
                var candidate = CloneDevice(original);
                CopyDeviceSharedData(candidate, edited, replacePorts: true,
                    preservePortPositions: instance.Override != null);
                var oldIds = original.Ports.Select(port => port.Id).ToHashSet();
                // Newly added ports reserve free slots independently in each placement.
                foreach (var port in candidate.Ports.Where(port => !oldIds.Contains(port.Id)))
                    port.Position = -1;
                if (!DevicePortSlots.TryAssign(candidate.Ports, instance.Width, instance.Height, out var slots))
                {
                    MessageBox.Show(this,
                        $"'{template.Name}' 장치의 포트 배치 공간이 부족하거나 포트 위치가 겹칩니다.\n배치된 장치 크기를 늘리거나 포트 위치를 조정한 뒤 다시 적용해 주세요.\n이번 변경은 적용하지 않았습니다.",
                        "포트 배치 공간 부족", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                foreach (var port in candidate.Ports) port.Position = slots[port.Id];
                pendingLayouts[instance] = candidate;
            }
            string originalName = template.Name;
            var affected = _placed.Where(p => p.TemplateDevice == template ||
                p.TemplateDevice.Name.Equals(originalName, StringComparison.OrdinalIgnoreCase)).ToList();
            CopyDeviceInto(template, edited);

            // 이름·포트 정보·글자 서식은 같은 이름의 등록 항목과 모든 배치가 공유한다.
            // 배치별 Override에는 포트의 변/위치/이름표 위치만 남겨 그대로 보존한다.
            SynchronizeDeviceSharedData(
                template, template, originalName, replacePorts: true);
            foreach (var pair in pendingLayouts) pair.Key.Override = pair.Value;

            _registeredDevices.FirstOrDefault(d => d.Device == template)?.Refresh();
            RefreshSidebarViews();
            foreach (var placed in affected)
                RenderDevice(placed);

            // 포트 ID는 유지되지만 위치/변이 바뀔 수 있으므로 연결된 케이블의
            // 앵커 좌표도 즉시 다시 계산한다.
            RenderAllCables();
            StatusText.Text = $"'{template.Name}' 및 사용 중인 모든 개체에 적용됨";
            _isDirty = true;
            UpdateTextFormatPalette();
            return true;
        }

        private void Menu_Settings(object sender, RoutedEventArgs e)
        {
            var win = new SettingsWindow(_deviceCategoryNames.ToDictionary(name => name,
                name => _deviceCategoryColors.GetValueOrDefault(name, "#FFFFFF"))) { Owner = this };
            if (win.ShowDialog() != true) return;
            foreach (var pair in win.ResultCategoryColors) _deviceCategoryColors[pair.Key] = pair.Value;
            ApplyDeviceCategoryColors();
            _isDirty = true;

            foreach (var p in _placed) RenderDevice(p);
            RenderAllZones();
            foreach (var item in _registeredDevices) item.Refresh();
            RefreshSidebarViews();
            UpdateThemeMenuChecks();
            StatusText.Text = "테마와 색상 설정이 적용되었습니다";
        }

        private void Menu_ExportConnectorLibrary(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog { Title = "커넥터 라이브러리 내보내기", Filter = "ESDP 커넥터 라이브러리|*.esdplib", DefaultExt = ".esdplib", FileName = "ConnectorLibrary.esdplib" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                ConnectorCatalogService.Instance.ExportLibrary(dialog.FileName);
                MessageBox.Show(this, "커넥터 시리즈·이미지·핀 위치를 내보냈습니다. 다른 PC에서 이 파일을 가져오세요.", "라이브러리 내보내기");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "라이브러리 내보내기 실패", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void Menu_ImportConnectorLibrary(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = "커넥터 라이브러리 가져오기", Filter = "ESDP 커넥터 라이브러리|*.esdplib" };
            if (dialog.ShowDialog(this) != true) return;
            if (MessageBox.Show(this, "기존 라이브러리에 병합합니다. 같은 ID의 커넥터와 핀 위치는 가져오는 정보로 갱신되며, 기존 카탈로그는 백업됩니다. 계속할까요?", "라이브러리 가져오기", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                ConnectorCatalogService.Instance.ImportLibrary(dialog.FileName);
                MessageBox.Show(this, "라이브러리를 가져왔습니다. 열려 있는 커넥터 설정 창은 다시 열어 주세요.", "라이브러리 가져오기");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "라이브러리 가져오기 실패", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ThemeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string themeName }) return;
            AppSettings.ThemeName = themeName;
            AppThemeManager.Apply(themeName);
            AppSettings.Save();
            RenderAllZones();
            UpdateThemeMenuChecks();
            StatusText.Text = themeName switch
            {
                AppThemeManager.SoftOrange => "주황색 라이트 테마를 적용했습니다",
                AppThemeManager.Dark => "다크 모드를 적용했습니다",
                _ => "하늘색 라이트 테마를 적용했습니다"
            };
        }

        private void UpdateThemeMenuChecks()
        {
            if (SkyBlueThemeMenuItem == null || SoftOrangeThemeMenuItem == null ||
                DarkThemeMenuItem == null) return;
            SkyBlueThemeMenuItem.IsChecked = AppSettings.ThemeName.Equals(
                AppThemeManager.SkyBlue, StringComparison.OrdinalIgnoreCase);
            SoftOrangeThemeMenuItem.IsChecked = AppSettings.ThemeName.Equals(
                AppThemeManager.SoftOrange, StringComparison.OrdinalIgnoreCase);
            DarkThemeMenuItem.IsChecked = AppSettings.ThemeName.Equals(
                AppThemeManager.Dark, StringComparison.OrdinalIgnoreCase);
        }

        private void Menu_DeleteSelected(object sender, RoutedEventArgs e) => DeleteSelected();

        private void DeleteSelected()
        {
            if (_selectedSet.Count == 0)
            {
                MessageBox.Show("삭제할 장치를 먼저 선택하세요.", "ESDP",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            foreach (var p in _selectedSet.ToList())
            {
                MainCanvas.Children.Remove(p.Container);
                _placed.Remove(p);
            }
            _selectedSet.Clear();
            UpdateTextFormatPalette();
            UpdateCanvasExtent();
            _isDirty = true;
        }

        // ================= 저장 / 불러오기 =================
        private static JsonSerializerOptions JsonOptions =>
            ProjectFileService.JsonOptions;

        // ================= 시트 (엑셀의 시트 개념) =================
        private readonly ObservableCollection<Sheet> _sheets = new();
        private int _currentSheetIndex = -1;
        private bool _switchingSheet; // 프로그램이 탭을 바꾸는 중인지 (사용자 클릭과 구분)

        private static T DeepClone<T>(T obj)
            => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(obj, JsonOptions), JsonOptions)!;

        /// <summary>지금 캔버스에 보이는 상태를 현재 시트에 담아둔다 (시트 전환/저장 직전에 호출).</summary>
        private void CaptureCurrentSheet()
        {
            if (_currentSheetIndex < 0 || _currentSheetIndex >= _sheets.Count) return;
            var sheet = _sheets[_currentSheetIndex];
            sheet.PaperSize = _currentPaper;
            sheet.DeviceInstances = _placed.Select(p => p.Instance).ToList();
            sheet.CableInstances = _placedCables.Select(p => p.Instance).ToList();
            sheet.Shapes = _placedShapes.Select(p => p.Shape).ToList();
            foreach (PlacedZone zone in _placedZones)
                zone.Zone.MemberDeviceInstanceIds = _placed
                    .Where(device => device.Instance.ZoneId == zone.Zone.Id)
                    .Select(device => device.Instance.Id)
                    .ToList();
            sheet.Zones = _placedZones.Select(p => p.Zone).ToList();
        }

        /// <summary>해당 시트의 내용을 캔버스에 펼친다.</summary>
        private void ApplySheet(int index)
        {
            if (index < 0 || index >= _sheets.Count) return;
            if (_isZoneSettingMode) ExitZoneSettingMode();
            _applyingSheet = true;
            _lastSheetApplyNormalizedBranchStructure = false;
            try
            {
                var sheet = _sheets[index];

                foreach (var p in _placed) MainCanvas.Children.Remove(p.Container);
                foreach (var pc in _placedCables) MainCanvas.Children.Remove(pc.Container);
                foreach (var ps in _placedShapes) MainCanvas.Children.Remove(ps.Container);
                foreach (var pz in _placedZones) MainCanvas.Children.Remove(pz.Container);
                _crossingOverlay?.Children.Clear();
                _labelOverlay?.Children.Clear();
                _placed.Clear();
                _placedCables.Clear();
                _placedShapes.Clear();
                _placedZones.Clear();
                _selectedSet.Clear();
                _selectedCable = null;
                _selectedShape = null;
                _selectedShapes.Clear();
                _selectedCablePorts.Clear();

            _currentSheetIndex = index;
            bool landscape = sheet.PaperSize is PaperSize.A3_Landscape or
                PaperSize.A4_Landscape;
            _currentPaper = landscape
                ? PaperSize.A3_Landscape
                : PaperSize.A3_Portrait;
            sheet.PaperSize = _currentPaper;
            sheet.KeepPaperAspectRatio = false;
            if (sheet.PageScale <= 0) sheet.PageScale = 1;
            UpdatePaperMenuChecks();
            ApplyPaperSize();

            foreach (var zone in sheet.Zones ?? new List<Zone>())
            {
                if (!_isPdfExportRendering)
                {
                zone.Width = Math.Max(DiagramGridSize,
                    SnapLengthToDiagramGrid(zone.Width, DiagramGridSize));
                zone.Height = Math.Max(DiagramGridSize,
                    SnapLengthToDiagramGrid(zone.Height, DiagramGridSize));
                zone.X = SnapToDiagramGrid(zone.X);
                zone.Y = SnapToDiagramGrid(zone.Y);
                }
                var placedZone = CreatePlacedZone(zone);
                MainCanvas.Children.Add(placedZone.Container);
                _placedZones.Add(placedZone);
                RenderZone(placedZone);
            }

            foreach (Zone zone in sheet.Zones ?? new List<Zone>())
            foreach (string instanceId in zone.MemberDeviceInstanceIds ??
                     new List<string>())
            {
                DeviceInstance? instance = sheet.DeviceInstances.FirstOrDefault(
                    item => item.Id == instanceId);
                if (instance != null && string.IsNullOrWhiteSpace(instance.ZoneId))
                    instance.ZoneId = zone.Id;
            }

            foreach (var instance in sheet.DeviceInstances)
            {
                var template = _registeredDevices.FirstOrDefault(d => d.Device.Id == instance.DeviceId)?.Device;
                if (template == null) continue;
                var placed = CreatePlaced(template, instance);
                MainCanvas.Children.Add(placed.Container);
                _placed.Add(placed);
                RenderDevice(placed);
            }

            foreach (var cinstance in sheet.CableInstances)
            {
                var template = _registeredCables.FirstOrDefault(c => c.Cable.Id == cinstance.CableId)?.Cable;
                if (template == null) continue;
                var placed = CreatePlacedCable(template, cinstance);
                MainCanvas.Children.Add(placed.Container);
                _placedCables.Add(placed);
            }

            // 불러오기와 선택에서는 저장된 경로를 유지한다. 단, 분기점에서
            // 부모 선과 같은 방향으로 한 번 더 진행하는 첫 꺾임은 구조적으로
            // 중복이므로 해당 분기 시작부만 제한적으로 복구한다.
            if (!_isPdfExportRendering)
            {
                RepairCableToCableConnections();
                foreach (var cable in _placedCables)
                    _lastSheetApplyNormalizedBranchStructure |=
                        NormalizeBranchDepartureStructure(cable.Instance);
            }
            if (_lastSheetApplyNormalizedBranchStructure) _isDirtyValue = true;
            RenderAllCables();
            RefreshAutoArrangeExclusionList();

            foreach (var shape in sheet.Shapes)
            {
                var placed = CreatePlacedShape(shape);
                MainCanvas.Children.Add(placed.Container);
                _placedShapes.Add(placed);
                RenderShape(placed);
            }
            NormalizeShapeZOrders();

                UpdateCanvasExtent();
                UpdateTextFormatPalette();
            }
            finally
            {
                _applyingSheet = false;
            }
        }

        private void RefreshSheetTabs(int selectIndex)
        {
            _switchingSheet = true;
            SheetTabs.ItemsSource = null;
            SheetTabs.ItemsSource = _sheets;
            SheetTabs.SelectedIndex = selectIndex;
            _switchingSheet = false;
        }

        private void SheetTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_switchingSheet) return;
            int newIndex = SheetTabs.SelectedIndex;
            if (newIndex < 0 || newIndex == _currentSheetIndex) return;

            CaptureCurrentSheet();
            ApplySheet(newIndex);
            StatusText.Text = $"시트: {_sheets[newIndex].Name}";
        }

        private void SheetAdd_Click(object sender, RoutedEventArgs e)
        {
            CaptureCurrentSheet();
            var sheet = new Sheet { Name = MakeUniqueSheetName("Sheet"), PaperSize = _currentPaper };
            _sheets.Add(sheet);
            RefreshSheetTabs(_sheets.Count - 1);
            ApplySheet(_sheets.Count - 1);
            _isDirty = true;
            StatusText.Text = $"시트 '{sheet.Name}' 추가됨";
        }

        private string MakeUniqueSheetName(string baseName)
        {
            for (int n = 1; ; n++)
            {
                string candidate = $"{baseName}{n}";
                if (!_sheets.Any(s => s.Name == candidate)) return candidate;
            }
        }

        // ---- 탭 상호작용: 더블클릭 이름변경 / 우클릭 메뉴 / 끌어서 순서변경 ----
        private Point _sheetDragStart;
        private Sheet? _sheetDragItem;

        private void SheetTabs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _sheetDragStart = e.GetPosition(null);
            _sheetDragItem = null;

            if (e.OriginalSource is not DependencyObject src) return;
            var lbi = FindParent<ListBoxItem>(src);
            if (lbi?.DataContext is not Sheet sheet) return;

            if (e.ClickCount == 2)
            {
                RenameSheet(sheet);
                e.Handled = true;
                return;
            }
            _sheetDragItem = sheet;
        }

        private void SheetTabs_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _sheetDragItem == null) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _sheetDragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;

            DragDrop.DoDragDrop(SheetTabs, _sheetDragItem, DragDropEffects.Move);
            _sheetDragItem = null;
        }

        private void SheetTabs_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(Sheet)) is not Sheet dragged) return;
            if (e.OriginalSource is not DependencyObject src) return;

            var lbi = FindParent<ListBoxItem>(src);
            var target = lbi?.DataContext as Sheet;
            if (target == null || target == dragged) return;

            var current = _currentSheetIndex >= 0 && _currentSheetIndex < _sheets.Count
                ? _sheets[_currentSheetIndex] : null;

            int oldIdx = _sheets.IndexOf(dragged);
            int newIdx = _sheets.IndexOf(target);
            if (oldIdx < 0 || newIdx < 0) return;

            _sheets.Move(oldIdx, newIdx);

            int selectIdx = current != null ? _sheets.IndexOf(current) : newIdx;
            _currentSheetIndex = selectIdx;
            RefreshSheetTabs(selectIdx);
            _isDirty = true;
        }

        private void SheetTabs_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject src) return;
            var lbi = FindParent<ListBoxItem>(src);
            if (lbi?.DataContext is not Sheet sheet) return;

            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem("이름 바꾸기...", () => RenameSheet(sheet)));
            menu.Items.Add(MakeMenuItem("복제", () => DuplicateSheet(sheet)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("삭제", () => DeleteSheet(sheet)));

            SheetTabs.ContextMenu = menu;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void RenameSheet(Sheet sheet)
        {
            var win = new RenamePortWindow(sheet.Name) { Owner = this, Title = "시트 이름 바꾸기" };
            if (win.ShowDialog() != true || string.IsNullOrWhiteSpace(win.ResultName)) return;

            string newName = win.ResultName!.Trim();
            if (_sheets.Any(s => s != sheet && s.Name == newName))
            {
                MessageBox.Show("같은 이름의 시트가 이미 있습니다.", "ESDP",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            sheet.Name = newName;
            RefreshSheetTabs(_currentSheetIndex);
            _isDirty = true;
        }

        private void DuplicateSheet(Sheet sheet)
        {
            CaptureCurrentSheet();

            var copy = DeepClone(sheet);
            copy.Id = Guid.NewGuid().ToString();
            copy.Name = MakeUniqueSheetName(sheet.Name + " 복사본");
            // 복제된 배치들은 새 Id 를 갖게 해서 원본과 섞이지 않도록 한다
            foreach (var di in copy.DeviceInstances) di.Id = Guid.NewGuid().ToString();
            foreach (var ci in copy.CableInstances) ci.Id = Guid.NewGuid().ToString();

            int insertAt = _sheets.IndexOf(sheet) + 1;
            _sheets.Insert(insertAt, copy);
            RefreshSheetTabs(insertAt);
            ApplySheet(insertAt);
            _isDirty = true;
            StatusText.Text = $"시트 '{copy.Name}' 로 복제됨";
        }

        private void DeleteSheet(Sheet sheet)
        {
            if (_sheets.Count <= 1)
            {
                MessageBox.Show("시트가 하나뿐이라 삭제할 수 없습니다.", "ESDP",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show($"시트 '{sheet.Name}' 을(를) 삭제할까요?", "시트 삭제",
                                 MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            int idx = _sheets.IndexOf(sheet);
            bool wasCurrent = idx == _currentSheetIndex;
            _sheets.RemoveAt(idx);

            int next = Math.Min(idx, _sheets.Count - 1);
            _currentSheetIndex = -1; // 강제로 다시 적용되게
            RefreshSheetTabs(next);
            ApplySheet(next);
            _isDirty = true;
        }

        private Project BuildProjectFromState()
        {
            CaptureCurrentSheet();
            return new Project
            {
                SchemaVersion = Project.CurrentSchemaVersion,
                Devices = _registeredDevices.Select(d => d.Device).ToList(),
                Cables = _registeredCables.Select(c => c.Cable).ToList(),
                CustomDeviceKinds = _customDeviceKinds.ToList(),
                DeviceCategoryNames = _deviceCategoryNames.ToList(),
                DeviceCategoryColors = new(_deviceCategoryColors),
                CustomCableCategories = _customCableCategories.ToList(),
                Sheets = _sheets.ToList()
            };
        }

        private void Menu_NewProject(object sender, RoutedEventArgs e)
        {
            if (!ConfirmSaveBeforeReplacing(
                    "새 프로젝트를 시작", confirmWhenClean: true))
                return;

            foreach (var p in _placed) MainCanvas.Children.Remove(p.Container);
            foreach (var pc in _placedCables) MainCanvas.Children.Remove(pc.Container);
            foreach (var ps in _placedShapes) MainCanvas.Children.Remove(ps.Container);
            foreach (var pz in _placedZones) MainCanvas.Children.Remove(pz.Container);
            _placed.Clear();
            _placedCables.Clear();
            _placedShapes.Clear();
            _placedZones.Clear();
            _crossingOverlay?.Children.Clear();
            _labelOverlay?.Children.Clear();
            _registeredDevices.Clear();
            _registeredCables.Clear();
            _customDeviceKinds.Clear();
            _customCableCategories.Clear();
            RestoreDeviceCategories(new Project());
            ConfigureSidebarViews();
            _selectedSet.Clear();
            _selectedCable = null;
            _selectedShape = null;
            _selectedZone = null;
            _selectedShapes.Clear();
            _selectedCablePorts.Clear();
            _currentFilePath = null;

            _sheets.Clear();
            _sheets.Add(new Sheet { Name = "Sheet1", PaperSize = PaperSize.A3_Landscape });
            _currentSheetIndex = -1;
            RefreshSheetTabs(0);
            ApplySheet(0);

            _isDirty = false;
            StatusText.Text = "새 프로젝트";
            ResetUndoHistory();
        }

        private void Menu_SaveProject(object sender, RoutedEventArgs e)
        {
            TrySaveCurrentProject();
        }

        private void Menu_SaveProjectAs(object sender, RoutedEventArgs e)
        {
            TrySaveProjectAs();
        }

        private bool ConfirmSaveBeforeReplacing(
            string actionDescription, bool confirmWhenClean = false)
        {
            if (!_isDirty)
            {
                if (!confirmWhenClean) return true;
                return MessageBox.Show(
                           $"{actionDescription}할까요?", "ESDP",
                           MessageBoxButton.YesNo, MessageBoxImage.Question) ==
                       MessageBoxResult.Yes;
            }

            MessageBoxResult result = MessageBox.Show(
                $"저장하지 않은 변경사항이 있습니다.\n" +
                $"{actionDescription}하기 전에 저장할까요?",
                "ESDP", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            return result switch
            {
                MessageBoxResult.Yes => TrySaveCurrentProject(),
                MessageBoxResult.No => true,
                _ => false
            };
        }

        private bool TrySaveCurrentProject()
            => _currentFilePath == null
                ? TrySaveProjectAs()
                : SaveProjectToFile(_currentFilePath);

        private bool TrySaveProjectAs()
        {
            var dialog = new SaveFileDialog
            {
                Filter = "ESDP 프로젝트 (*.esdp)|*.esdp|모든 파일 (*.*)|*.*",
                FileName = _currentFilePath == null
                    ? "project.esdp"
                    : Path.GetFileName(_currentFilePath)
            };
            return dialog.ShowDialog() == true &&
                   SaveProjectToFile(dialog.FileName);
        }

        private bool SaveProjectToFile(string path)
        {
            try
            {
                var project = BuildProjectFromState();
                ProjectFileService.SaveAtomic(path, project);
                _currentFilePath = Path.GetFullPath(path);
                _isDirty = false;
                RecentProjects.Record(_currentFilePath);
                StatusText.Text = $"저장됨: {Path.GetFileName(_currentFilePath)}";
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"저장 중 오류가 발생했습니다.\n{ex.Message}", "ESDP",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void Menu_OpenProject(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "ESDP 프로젝트 (*.esdp)|*.esdp|이전 프로젝트 (*.esmp)|*.esmp|모든 파일 (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            if (!ConfirmSaveBeforeReplacing("선택한 프로젝트를 열")) return;
            LoadProjectFromFile(dlg.FileName);
        }

        private async void Menu_ImportDevicesExcel(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "장치 Excel 불러오기",
                Filter = "Excel 통합 문서 (*.xlsx)|*.xlsx"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                StatusText.Text = "브라우저에서 장치 Excel을 확인해 주세요.";
                var workbook = await HtmlExcelImportService.LoadAsync(dialog.FileName);
                var imported = ExcelInterchangeService.ImportDevices(workbook);
                foreach (var device in imported)
                {
                    var existing = _registeredDevices.FirstOrDefault(item =>
                        item.Name.Equals(device.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                        _registeredDevices.Add(new DeviceListItem(device));
                    else
                        SynchronizeDeviceSharedData(
                            existing.Device, device, existing.Device.Name,
                            replacePorts: true);
                }
                RefreshSidebarViews();
                foreach (var placed in _placed) ApplyMinSizeAndRender(placed);
                RenderAllCables();
                _isDirty = true;
                StatusText.Text = $"장치 {imported.Count}개를 Excel에서 불러왔습니다.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"장치 Excel을 불러오지 못했습니다.\n{ex.Message}",
                    "Excel 불러오기", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Menu_ImportCablesExcel(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "케이블 Excel 불러오기",
                Filter = "Excel 통합 문서 (*.xlsx)|*.xlsx"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                StatusText.Text = "브라우저에서 케이블 Excel을 확인해 주세요.";
                var workbook = await HtmlExcelImportService.LoadAsync(dialog.FileName);
                var imported = ExcelInterchangeService.ImportCables(workbook);
                foreach (var cable in imported)
                {
                    var existing = _registeredCables.FirstOrDefault(item =>
                        item.Name.Equals(cable.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                        _registeredCables.Add(new CableListItem(cable));
                    else
                        SynchronizeCableSharedData(
                            existing.Cable, cable, existing.Cable.Name);
                }
                RefreshSidebarViews();
                RenderAllCables();
                _isDirty = true;
                StatusText.Text = $"케이블 {imported.Count}개를 Excel에서 불러왔습니다.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"케이블 Excel을 불러오지 못했습니다.\n{ex.Message}",
                    "Excel 불러오기", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Menu_ExportDevicesExcel(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!TryGetProjectExcelExportPath("subsystem", "subsystem_list.xlsx", out string path))
                    return;
                ExcelInterchangeService.ExportDevices(
                    path, _registeredDevices.Select(item => item.Device));
                StatusText.Text = $"장치 Excel 자동 저장 완료: {path}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"장치 Excel을 내보내지 못했습니다.\n{ex.Message}",
                    "Excel 내보내기", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Menu_ExportCablesExcel(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!TryGetProjectExcelExportPath("cable", "cable_list.xlsx", out string path))
                    return;
                ExcelInterchangeService.ExportCables(
                    path, _registeredCables.Select(item => item.Cable));
                StatusText.Text = $"케이블 Excel 자동 저장 완료: {path}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"케이블 Excel을 내보내지 못했습니다.\n{ex.Message}",
                    "Excel 내보내기", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool TryGetProjectExcelExportPath(string categoryFolder, string fileName, out string path)
        {
            path = "";
            if (string.IsNullOrWhiteSpace(_currentFilePath))
            {
                MessageBox.Show("Excel을 내보내려면 프로젝트를 먼저 저장해 주세요.",
                    "Excel 내보내기", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            string projectFilePath = Path.GetFullPath(_currentFilePath);
            string projectName = Path.GetFileNameWithoutExtension(projectFilePath);
            string projectDirectory = Path.GetDirectoryName(projectFilePath)
                ?? throw new InvalidOperationException("프로젝트 폴더를 확인할 수 없습니다.");
            string exportDirectory = Path.Combine(projectDirectory, projectName, categoryFolder);
            Directory.CreateDirectory(exportDirectory);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string baseName = $"{Path.GetFileNameWithoutExtension(fileName)}_{timestamp}";
            string extension = Path.GetExtension(fileName);
            path = Path.Combine(exportDirectory, baseName + extension);
            for (int suffix = 2; File.Exists(path); suffix++)
                path = Path.Combine(exportDirectory, $"{baseName}_{suffix}{extension}");
            return true;
        }

        public bool LoadProjectFromFile(string path)
        {
            Project? rollbackProject = null;
            int rollbackSheetIndex = -1;
            string? rollbackFilePath = _currentFilePath;
            bool rollbackDirty = _isDirty;
            string rollbackStatus = StatusText.Text;
            bool editorApplyStarted = false;
            try
            {
                ProjectLoadResult loadResult = ProjectFileService.LoadPrepared(path);
                Project project = loadResult.Project;

                // 모델 변환과 격자 정규화는 현재 화면을 지우기 전에 끝낸다.
                // 여기에서 실패하면 사용자가 편집 중인 화면은 전혀 바뀌지 않는다.
                MigrateLegacySharedDefinitions(project);
                bool normalizedToGrid = NormalizeProjectDevicesToGrid(project);

                if (_sheets.Count > 0)
                {
                    rollbackProject = DeepClone(BuildProjectFromState());
                    rollbackSheetIndex = _currentSheetIndex;
                }

                editorApplyStarted = true;
                ApplyProjectToEditor(project, 0);

                _currentFilePath = Path.GetFullPath(path);
                // 구버전 형식이나 비격자 구조는 원본 파일을 건드리지 않고
                // 메모리에서만 변환한다. 사용자가 저장할 때 현재 형식으로 기록된다.
                _isDirty = loadResult.WasMigrated || normalizedToGrid ||
                           _lastSheetApplyNormalizedBranchStructure;
                RecentProjects.Record(_currentFilePath);
                StatusText.Text = _isDirty
                    ? $"불러옴: {Path.GetFileName(path)} — 도면 구조를 현재 기준에 맞췄습니다. 저장하면 반영됩니다."
                    : $"불러옴: {Path.GetFileName(path)}  (시트 {_sheets.Count}장)";
                ResetUndoHistory();
                return true;
            }
            catch (Exception ex)
            {
                string recoveryMessage = "";
                if (editorApplyStarted && rollbackProject != null)
                {
                    try
                    {
                        int restoreIndex = Math.Clamp(
                            rollbackSheetIndex, 0, rollbackProject.Sheets.Count - 1);
                        ApplyProjectToEditor(rollbackProject, restoreIndex);
                        _currentFilePath = rollbackFilePath;
                        _isDirty = rollbackDirty;
                        StatusText.Text = rollbackStatus;
                    }
                    catch (Exception recoveryError)
                    {
                        recoveryMessage =
                            $"\n\n이전 화면 복구에도 실패했습니다.\n{recoveryError.Message}";
                    }
                }

                MessageBox.Show(
                    $"불러오는 중 오류가 발생했습니다.\n{ex.Message}{recoveryMessage}", "ESDP",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void ApplyProjectToEditor(Project project, int sheetIndex)
        {
            foreach (PlacedDevice placed in _placed)
                MainCanvas.Children.Remove(placed.Container);
            foreach (PlacedCable placed in _placedCables)
                MainCanvas.Children.Remove(placed.Container);
            foreach (PlacedShape placed in _placedShapes)
                MainCanvas.Children.Remove(placed.Container);
            foreach (PlacedZone placed in _placedZones)
                MainCanvas.Children.Remove(placed.Container);
            _crossingOverlay?.Children.Clear();
            _labelOverlay?.Children.Clear();

            _placed.Clear();
            _placedCables.Clear();
            _placedShapes.Clear();
            _placedZones.Clear();
            _selectedSet.Clear();
            _selectedCable = null;
            _selectedShape = null;
            _selectedZone = null;
            _selectedShapes.Clear();
            _selectedCablePorts.Clear();

            _registeredDevices.Clear();
            foreach (Device device in project.Devices)
                _registeredDevices.Add(new DeviceListItem(device));
            _registeredCables.Clear();
            foreach (Cable cable in project.Cables)
                _registeredCables.Add(new CableListItem(cable));

            _customDeviceKinds.Clear();
            _customDeviceKinds.AddRange(project.CustomDeviceKinds);
            RestoreDeviceCategories(project);
            _customCableCategories.Clear();
            _customCableCategories.AddRange(project.CustomCableCategories);
            ConfigureSidebarViews();

            _sheets.Clear();
            foreach (Sheet sheet in project.Sheets)
                _sheets.Add(sheet);

            _currentSheetIndex = -1;
            int selectedIndex = Math.Clamp(sheetIndex, 0, _sheets.Count - 1);
            RefreshSheetTabs(selectedIndex);
            ApplySheet(selectedIndex);
        }

        /// <summary>
        /// 모든 시트의 장치 위치·크기를 메인 20px 격자 규칙으로 정규화한다.
        /// 기존 중심에 가장 가까운 위치를 우선하며, 장치 본체나 실제 포트 셀이
        /// 겹치면 가까운 빈 격자를 찾는다. 일반 도형과 케이블 꺾임점은 손대지 않는다.
        /// </summary>
        private static bool NormalizeProjectDevicesToGrid(Project project)
        {
            var definitions = project.Devices.ToDictionary(
                device => device.Id, StringComparer.Ordinal);
            bool changed = false;

            foreach (var sheet in project.Sheets)
            {
                var occupied = new RectSpatialIndex();

                foreach (var instance in sheet.DeviceInstances)
                {
                    Device? device = instance.Override;
                    if (device == null)
                        definitions.TryGetValue(instance.DeviceId, out device);
                    if (device == null) continue;

                    var (minW, minH) = DevicePreviewRenderer.GetMinSize(device);
                    double width = SnapLengthToDiagramGrid(instance.Width, minW);
                    double height = SnapLengthToDiagramGrid(instance.Height, minH);
                    double centerX = instance.X + instance.Width / 2;
                    double centerY = instance.Y + instance.Height / 2;
                    double preferredX = Math.Max(0,
                        SnapToDiagramGrid(centerX - width / 2));
                    double preferredY = Math.Max(0,
                        SnapToDiagramGrid(centerY - height / 2));

                    bool IsClear(double x, double y)
                    {
                        var body = new Rect(x, y, width, height);
                        var portCells = GetOccupiedDevicePortCells(device, body);
                        return !occupied.AnyOverlap(body, exact: HasPositiveAreaOverlap) &&
                            !portCells.Any(cell => occupied.AnyOverlap(cell, exact: HasPositiveAreaOverlap));
                    }

                    double chosenX = preferredX;
                    double chosenY = preferredY;
                    if (!IsClear(chosenX, chosenY))
                    {
                        bool found = false;
                        for (int radius = 1; radius <= 200 && !found; radius++)
                        {
                            for (int gx = -radius; gx <= radius && !found; gx++)
                            {
                                int gyMagnitude = radius - Math.Abs(gx);
                                foreach (int gy in gyMagnitude == 0
                                             ? new[] { 0 }
                                             : new[] { -gyMagnitude, gyMagnitude })
                                {
                                    double candidateX = Math.Max(0,
                                        preferredX + gx * DiagramGridSize);
                                    double candidateY = Math.Max(0,
                                        preferredY + gy * DiagramGridSize);
                                    if (!IsClear(candidateX, candidateY)) continue;
                                    chosenX = candidateX;
                                    chosenY = candidateY;
                                    found = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (Math.Abs(instance.X - chosenX) > 0.01 ||
                        Math.Abs(instance.Y - chosenY) > 0.01 ||
                        Math.Abs(instance.Width - width) > 0.01 ||
                        Math.Abs(instance.Height - height) > 0.01)
                        changed = true;

                    instance.X = chosenX;
                    instance.Y = chosenY;
                    instance.Width = width;
                    instance.Height = height;
                    var finalBody = new Rect(chosenX, chosenY, width, height);
                    occupied.Put(instance, finalBody);
                    foreach (var cell in GetOccupiedDevicePortCells(device, finalBody))
                        occupied.Put(new object(), cell);
                }

                var instanceById = sheet.DeviceInstances.ToDictionary(
                    instance => instance.Id, StringComparer.Ordinal);
                var cablePortTargets = sheet.CableInstances
                    .SelectMany(instance => AllPortLayouts(instance))
                    .Select(layout => layout.ConnectedToPortKey)
                    .Where(key => key?.StartsWith(
                        "cab:", StringComparison.Ordinal) == true)
                    .Cast<string>()
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var cable in sheet.CableInstances)
                foreach (var layout in AllPortLayouts(cable))
                {
                    string ownCablePortKey =
                        $"cab:{cable.Id}:{layout.ConnectorId}";
                    bool isPassiveCableConnection =
                        cablePortTargets.Contains(ownCablePortKey);
                    if (string.IsNullOrWhiteSpace(layout.ConnectedToPortKey) &&
                        !isPassiveCableConnection)
                    {
                        Point snapped = SnapCablePortToGridFace(
                            new Point(layout.PortX, layout.PortY),
                            layout.FixedFacing);
                        double snappedX = snapped.X;
                        double snappedY = snapped.Y;
                        if (Math.Abs(layout.PortX - snappedX) > 0.01 ||
                            Math.Abs(layout.PortY - snappedY) > 0.01)
                            changed = true;
                        layout.PortX = snappedX;
                        layout.PortY = snappedY;
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(layout.ConnectedToPortKey))
                        continue;

                    string[]? parts = layout.ConnectedToPortKey?.Split(':');
                    if (parts is not { Length: 3 } || parts[0] != "dev" ||
                        !instanceById.TryGetValue(parts[1], out var instance))
                        continue;

                    Device? device = instance.Override;
                    if (device == null)
                        definitions.TryGetValue(instance.DeviceId, out device);
                    if (device == null) continue;

                    var anchor = DevicePreviewRenderer.GetPortAnchors(
                            device, instance.X, instance.Y,
                            instance.Width, instance.Height)
                        .FirstOrDefault(item => item.Port.Id == parts[2]);
                    if (anchor.Port == null) continue;

                    double gap = CableRenderer.PortW + CableRenderer.ConnectGap;
                    layout.PortX = anchor.X;
                    layout.PortY = anchor.Y;
                    switch (anchor.Side)
                    {
                        case PortSide.Left: layout.PortX -= gap; break;
                        case PortSide.Right: layout.PortX += gap; break;
                        case PortSide.Top: layout.PortY -= gap; break;
                        case PortSide.Bottom: layout.PortY += gap; break;
                    }
                    layout.FixedFacing = anchor.Side;
                }
            }

            return changed;
        }

        // ================= 사이드바 → 캔버스 =================
        private void DeviceListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _sidebarDragStart = e.GetPosition(null);
            _sidebarDragCandidate = null;
            _isSidebarDragging = false;

            if (e.OriginalSource is not DependencyObject src) return;
            var lbi = FindParent<ListBoxItem>(src);
            var item = lbi?.DataContext as DeviceListItem;
            if (item == null) return;

            if (e.ClickCount == 2)
            {
                OpenEditForTemplate(item.Device);
                e.Handled = true;
                return;
            }

            _sidebarDragCandidate = item;
        }

        private void DeviceListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _sidebarDragCandidate = null;
            _isSidebarDragging = false;
        }

        private void DeviceListBox_PreviewMouseRightButtonDown(
            object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source) return;
            var item = FindParent<ListBoxItem>(source)?.DataContext as DeviceListItem;
            if (item == null) return;

            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem("장치 복제", () =>
                DuplicateRegisteredDevice(item)));
            var groupMenu = new MenuItem { Header = "소속 그룹 변경" };
            foreach (string groupName in _deviceCategoryNames)
            {
                string targetGroup = groupName;
                var groupItem = new MenuItem
                {
                    Header = targetGroup,
                    IsCheckable = true,
                    IsChecked = item.GroupName.Equals(
                        targetGroup, StringComparison.CurrentCultureIgnoreCase)
                };
                groupItem.Click += (_, _) =>
                    ChangeRegisteredDeviceGroup(item, targetGroup);
                groupMenu.Items.Add(groupItem);
            }
            menu.Items.Add(groupMenu);
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("장치 삭제", () =>
                DeleteRegisteredDevice(item)));
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void DeleteRegisteredDevice(DeviceListItem item)
        {
            int usedCount = _sheets.Sum(sheet => sheet.DeviceInstances.Count(
                instance => instance.DeviceId == item.Device.Id));
            string message = usedCount > 0
                ? $"'{item.Name}' 장치와 도면에 배치된 {usedCount}개 개체를 모두 삭제할까요?"
                : $"'{item.Name}' 장치를 목록에서 삭제할까요?";
            if (MessageBox.Show(message, "장치 삭제",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) !=
                MessageBoxResult.Yes)
                return;

            var removedInstanceIds = _sheets
                .SelectMany(sheet => sheet.DeviceInstances)
                .Where(instance => instance.DeviceId == item.Device.Id)
                .Select(instance => instance.Id)
                .ToHashSet(StringComparer.Ordinal);
            removedInstanceIds.UnionWith(_placed
                .Where(placed => placed.TemplateDevice == item.Device)
                .Select(placed => placed.Instance.Id));
            foreach (var sheet in _sheets)
            {
                sheet.DeviceInstances.RemoveAll(
                    instance => instance.DeviceId == item.Device.Id);
                foreach (var cable in sheet.CableInstances)
                foreach (var layout in AllPortLayouts(cable))
                {
                    var parts = layout.ConnectedToPortKey?.Split(':');
                    if (parts is { Length: 3 } && parts[0] == "dev" &&
                        removedInstanceIds.Contains(parts[1]))
                        layout.ConnectedToPortKey = null;
                }
            }
            foreach (var cable in _placedCables)
            foreach (var layout in AllPortLayouts(cable.Instance))
            {
                var parts = layout.ConnectedToPortKey?.Split(':');
                if (parts is { Length: 3 } && parts[0] == "dev" &&
                    removedInstanceIds.Contains(parts[1]))
                    layout.ConnectedToPortKey = null;
            }
            foreach (var placed in _placed.Where(
                         placed => placed.TemplateDevice == item.Device).ToList())
            {
                MainCanvas.Children.Remove(placed.Container);
                _placed.Remove(placed);
                _selectedSet.Remove(placed);
            }
            _registeredDevices.Remove(item);
            RenderAllCables();
            UpdateCanvasExtent();
            UpdateTextFormatPalette();
            _isDirty = true;
        }

        private void DuplicateRegisteredDevice(DeviceListItem item)
        {
            var copy = CloneDevice(item.Device);
            copy.Id = Guid.NewGuid().ToString();
            copy.Name = MakeDuplicateName(
                item.Device.Name,
                _registeredDevices.Select(existing => existing.Name));
            foreach (var port in copy.Ports)
                port.Id = Guid.NewGuid().ToString();

            _registeredDevices.Add(new DeviceListItem(copy));
            RefreshSidebarViews();
            StatusText.Text = $"장치 '{copy.Name}' 복제됨";
            _isDirty = true;
        }

        private void DeviceListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isSidebarDragging) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (_sidebarDragCandidate == null) return;

            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _sidebarDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _sidebarDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _isSidebarDragging = true;
            var data = new DataObject(typeof(Device), _sidebarDragCandidate.Device);
            DragDrop.DoDragDrop(DeviceListBox, data, DragDropEffects.Copy);
            _isSidebarDragging = false;
            _sidebarDragCandidate = null;
        }

        private void MainCanvas_DragOver(object sender, DragEventArgs e)
        {
            if (_isZoneSettingMode)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            e.Effects = (e.Data.GetDataPresent(typeof(Device)) || e.Data.GetDataPresent(typeof(Cable)))
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void MainCanvas_Drop(object sender, DragEventArgs e)
        {
            if (_isZoneSettingMode)
            {
                e.Handled = true;
                return;
            }
            if (e.Data.GetDataPresent(typeof(Device)) &&
                e.Data.GetData(typeof(Device)) is Device device)
            {
                DropDevice(device, e.GetPosition(MainCanvas));
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(typeof(Cable)) &&
                e.Data.GetData(typeof(Cable)) is Cable cable)
            {
                DropCable(cable, e.GetPosition(MainCanvas));
                e.Handled = true;
                return;
            }
        }

        private void DropDevice(Device device, Point pos)
        {
            var (minW, minH) = DevicePreviewRenderer.GetMinSize(device);
            double w = SnapLengthToDiagramGrid(
                Math.Max(DefaultDeviceWidth, minW), minW);
            double h = SnapLengthToDiagramGrid(
                Math.Max(DefaultDeviceHeight, minH), minH);
            Point snappedCenter = SnapToDiagramGrid(pos);

            var instance = new DeviceInstance
            {
                DeviceId = device.Id,
                Width = w,
                Height = h,
                X = Math.Max(DiagramGridSize,
                    SnapToDiagramGrid(snappedCenter.X - w / 2)),
                Y = Math.Max(0,
                    SnapToDiagramGrid(snappedCenter.Y - h / 2))
            };

            // 새 장치를 기존 장치 위에 그대로 놓지 않는다. 포트와 통로는
            // 비어 있는 통로는 허용하고 실제 포트가 놓인 칸만 보호한다.
            var clearPosition = FindNearestClearDevicePosition(
                new Rect(instance.X, instance.Y, instance.Width, instance.Height),
                device);
            instance.X = clearPosition.X;
            instance.Y = clearPosition.Y;

            var placed = CreatePlaced(device, instance);
            MainCanvas.Children.Add(placed.Container);
            _placed.Add(placed);

            RenderDevice(placed);
            ClearSelection();
            SelectAdd(new[] { placed });
            UpdateCanvasExtent();
            _isDirty = true;
        }

        private static List<Rect> GetOccupiedDevicePortCells(
            Device device, Rect body)
        {
            double cell = DiagramGridSize;
            double half = cell / 2;
            return DevicePreviewRenderer.GetPortAnchors(
                    device, body.X, body.Y, body.Width, body.Height)
                .Select(anchor => anchor.Side switch
                {
                    PortSide.Left => new Rect(
                        body.Left - cell, anchor.Y - half, cell, cell),
                    PortSide.Right => new Rect(
                        body.Right, anchor.Y - half, cell, cell),
                    PortSide.Top => new Rect(
                        anchor.X - half, body.Top - cell, cell, cell),
                    _ => new Rect(
                        anchor.X - half, body.Bottom, cell, cell)
                })
                .ToList();
        }

        private static bool HasPositiveAreaOverlap(Rect first, Rect second)
        {
            const double epsilon = 0.01;
            double overlapWidth = Math.Min(first.Right, second.Right) -
                                  Math.Max(first.Left, second.Left);
            double overlapHeight = Math.Min(first.Bottom, second.Bottom) -
                                   Math.Max(first.Top, second.Top);
            return overlapWidth > epsilon && overlapHeight > epsilon;
        }

        private bool IsDevicePlacementClear(
            Rect proposed,
            Device proposedDevice,
            ISet<PlacedDevice>? movingDevices = null)
        {
            var proposedPortCells = GetOccupiedDevicePortCells(
                proposedDevice, proposed);
            foreach (var other in _placed)
            {
                if (movingDevices?.Contains(other) == true) continue;

                var otherBody = new Rect(
                    other.Instance.X,
                    other.Instance.Y,
                    other.Instance.Width,
                    other.Instance.Height);
                // 서로의 테두리가 맞닿는 배치는 허용한다. Rect.IntersectsWith는
                // 경계선만 접촉해도 true가 될 수 있어 실제 겹치는 면적을 직접 본다.
                if (HasPositiveAreaOverlap(proposed, otherBody)) return false;

                var otherPortCells = GetOccupiedDevicePortCells(
                    other.EffectiveDevice, otherBody);
                if (otherPortCells.Any(cell =>
                        HasPositiveAreaOverlap(proposed, cell)))
                    return false;
                if (proposedPortCells.Any(cell =>
                        HasPositiveAreaOverlap(cell, otherBody) ||
                        otherPortCells.Any(otherCell =>
                            HasPositiveAreaOverlap(cell, otherCell))))
                    return false;
            }
            return true;
        }

        private Point FindNearestClearDevicePosition(
            Rect requested, Device device)
        {
            double startX = SnapToDiagramGrid(requested.X);
            double startY = SnapToDiagramGrid(requested.Y);
            var initial = new Rect(
                startX, startY, requested.Width, requested.Height);
            if (IsDevicePlacementClear(initial, device))
                return new Point(startX, startY);

            // 드롭 지점에서 가까운 격자부터 동심 마름모 순서로 찾는다.
            // 캔버스가 확장형이라 충분히 멀리 가면 반드시 빈 위치가 있다.
            for (int radius = 1; radius <= 200; radius++)
            {
                for (int gx = -radius; gx <= radius; gx++)
                {
                    int gyMagnitude = radius - Math.Abs(gx);
                    foreach (int gy in gyMagnitude == 0
                                 ? new[] { 0 }
                                 : new[] { -gyMagnitude, gyMagnitude })
                    {
                        double x = Math.Max(
                            DiagramGridSize,
                            startX + gx * DiagramGridSize);
                        double y = Math.Max(
                            0,
                            startY + gy * DiagramGridSize);
                        var candidate = new Rect(
                            x, y, requested.Width, requested.Height);
                        if (IsDevicePlacementClear(candidate, device))
                            return new Point(x, y);
                    }
                }
            }

            return new Point(startX, startY);
        }

        private void DropCable(Cable cable, Point pos)
        {
            if (cable.Connectors.Count < 2)
            {
                cable.Connectors.Add(new CableConnector { PortName = "P1" });
                cable.Connectors.Add(new CableConnector { PortName = "P2" });
            }

            double half = CableRenderer.DefaultSpan / 2;
            Point snappedCenter = SnapToDiagramGrid(pos);
            Point firstPort = SnapCablePortToGridFace(
                new Point(snappedCenter.X - half, snappedCenter.Y),
                PortSide.Right);
            Point secondPort = SnapCablePortToGridFace(
                new Point(snappedCenter.X + half, snappedCenter.Y),
                PortSide.Left);
            var instance = new CableInstance
            {
                CableId = cable.Id,
                PortA = new CablePortLayout
                {
                    ConnectorId = cable.Connectors[0].Id,
                    PortX = Math.Max(0, firstPort.X),
                    PortY = firstPort.Y,
                    FixedFacing = PortSide.Right  // 처음 생성 시 서로를 향하게 해서 직선이 곧게 이어지도록
                },
                PortB = new CablePortLayout
                {
                    ConnectorId = cable.Connectors[1].Id,
                    PortX = secondPort.X,
                    PortY = secondPort.Y,
                    FixedFacing = PortSide.Left
                }
            };

            // 템플릿에 세 번째 이후 포트가 있으면 새 배치에도 기본 분기를
            // 만든다. 분기점은 루트 직선에 균등 배치하고 위/아래로 번갈아
            // 뻗게 해 처음부터 서로 포개지지 않게 한다.
            var extraConnectors = cable.Connectors.Skip(2).ToList();
            for (int index = 0; index < extraConnectors.Count; index++)
            {
                double t = (index + 1.0) / (extraConnectors.Count + 1.0);
                double anchorX = instance.PortA.PortX +
                                 (instance.PortB.PortX - instance.PortA.PortX) * t;
                double snappedAnchorX = SnapToGridMidpointWithin(
                    anchorX, instance.PortA.PortX, instance.PortB.PortX);
                double rootSpan = instance.PortB.PortX - instance.PortA.PortX;
                if (Math.Abs(rootSpan) > 0.01)
                    t = Math.Max(0, Math.Min(1,
                        (snappedAnchorX - instance.PortA.PortX) / rootSpan));
                bool below = index % 2 == 0;
                instance.Branches.Add(new CableBranch
                {
                    ParentId = "root",
                    T = t,
                    Port = new CablePortLayout
                    {
                        ConnectorId = extraConnectors[index].Id,
                        PortX = snappedAnchorX,
                        PortY = SnapToDiagramGrid(
                            firstPort.Y + (below ? 100 : -100)),
                        FixedFacing = below ? PortSide.Top : PortSide.Bottom
                    }
                });
            }

            var placed = CreatePlacedCable(cable, instance);
            MainCanvas.Children.Add(placed.Container);
            _placedCables.Add(placed);
            RenderAllCables(); // 교차점까지 한번에 갱신
            TidyCableLabels(placed); // 새 케이블의 최초 기본 위치만 충돌 정리
            UpdateCanvasExtent();
            StatusText.Text = $"케이블 '{cable.Name}' 배치됨 — 선/이름 더블클릭: 케이블 정보 / 우클릭: 분기";
            _isDirty = true;
        }

        private PlacedCable CreatePlacedCable(Cable template, CableInstance instance)
        {
            var container = new Canvas { Background = null };
            var placed = new PlacedCable { TemplateCable = template, Instance = instance, Container = container };
            container.MouseMove += (s, e) => CableOverlay_MouseMove(placed, e);
            container.MouseLeftButtonUp += (s, e) => CableOverlay_MouseUp(placed, e);
            return placed;
        }

        private void RenderCable(PlacedCable placed)
        {
            var c = placed.Container;
            c.Children.Clear();
            Canvas.SetLeft(c, 0);
            Canvas.SetTop(c, 0);

            var cb = new CableRenderCallbacks
            {
                LineBrush = _isCableArrangeExclusionMode &&
                            placed.Instance.ExcludeFromAutoArrange
                    ? Brushes.Red
                    : GetCableLineBrush(placed.EffectiveCable),
                IsPortSelected = layout => _selectedCablePorts.Contains(layout),
                ResolveAnchor = ResolveAnchor,
                ResolvePortFacing = layout => GetEffectiveCablePortFacing(placed, layout),
                // 화면 렌더링은 사용자가 저장한 굴곡을 그대로 따른다. 장애물
                // 회피는 명시적인 자동 라우팅 계산에서만 사용하며, 선택·드래그·
                // 다시 그리기만으로 케이블 모양을 바꾸지 않는다.
                GetObstacles = _ => Array.Empty<Rect>(),
                OnPortDown = (layout, e) =>
                {
                    if (_isCableArrangeExclusionMode)
                    {
                        ToggleAutoArrangeExclusion(placed);
                        e.Handled = true;
                        return;
                    }
                    CablePort_MouseDown(placed, layout, e);
                },
                OnPortDoubleClick = (layout, e) =>
                {
                    if (_isCableArrangeExclusionMode)
                    {
                        // 첫 번째 MouseDown에서 이미 제외 상태를 전환했다.
                        // 더블클릭의 두 번째 MouseDown에서는 다시 전환하지 않는다.
                        e.Handled = true;
                        return;
                    }
                    CablePort_Rename(placed, layout, e);
                },
                OnPortAutoFacing = layout => CablePort_AutoFacing(placed, layout),
                OnLineDoubleClick = e =>
                {
                    if (_isCableArrangeExclusionMode)
                    {
                        // 첫 클릭에서 이미 전환되므로 두 번째 클릭은 소비만 한다.
                        e.Handled = true;
                        return;
                    }
                    SelectCable(placed);
                    OpenCableProperties(placed);
                    e.Handled = true;
                },
                OnLineLeftButtonDown = e =>
                {
                    if (_isCableArrangeExclusionMode)
                    {
                        ToggleAutoArrangeExclusion(placed);
                        e.Handled = true;
                        return;
                    }
                    CableWhole_MouseDown(placed, e);
                },
                OnLineRightClick = (legId, e) => CableLine_RightClick(placed, legId, e),
                OnSegmentDown = (legId, segIdx, e) => CableSegment_MouseDown(placed, legId, segIdx, e),
                OnCornerRemove = (legId, cornerIdx, e) => CableCorner_Remove(placed, legId, cornerIdx, e),
                OnBranchDotDown = (branch, e) => CableBranchDot_MouseDown(placed, branch, e),
                OnSelect = () =>
                {
                    if (_isCableArrangeExclusionMode) return;
                    if (Keyboard.Modifiers != ModifierKeys.Control)
                        SelectCable(placed);
                }
            };
            var (segments, labels) = CableRenderer.Draw(
                c, placed.Instance, placed.EffectiveCable, cb,
                !_isCableArrangeExclusionMode && _selectedCable == placed);
            placed.RenderedSegments = segments;
            placed.RenderedLabels = labels;
        }

        private static Brush GetCableLineBrush(Cable cable)
        {
            string hex = cable.Category switch
            {
                CableCategory.탄내케이블 => "#000000",
                CableCategory.점검케이블 => "#0070C0",
                _ => ColorOption.NormalizeHex(cable.LineColorHex) ?? "#000000"
            };
            return HexToBrush(hex);
        }

        private static System.Windows.Media.Effects.Effect CreateThinLabelOutline(
            Brush foreground)
        {
            Color textColor = foreground is SolidColorBrush solid
                ? solid.Color
                : Colors.Black;
            double luminance =
                (0.2126 * textColor.R +
                 0.7152 * textColor.G +
                 0.0722 * textColor.B) / 255.0;
            return new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = luminance >= 0.55 ? Colors.Black : Colors.White,
                BlurRadius = 3,
                ShadowDepth = 0,
                Opacity = 0.9,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
            };
        }

        private void SelectCable(PlacedCable placed)
        {
            if (_selectedShape != null) SelectShape(null);
            if (_selectedSet.Count > 0) ClearSelection();
            ClearCablePortSelection(render: false);
            if (_selectedCable == placed) return;
            _selectedCable = placed;
            RenderAllCables();
            UpdateTextFormatPalette();
        }

        private void DeselectCable()
        {
            if (_selectedCable == null) return;
            _selectedCable = null;
            RenderAllCables();
            UpdateTextFormatPalette();
        }

        private void ClearCablePortSelection(bool render = true)
        {
            if (_selectedCablePorts.Count == 0) return;
            _selectedCablePorts.Clear();
            if (render) RenderAllCables();
            UpdateTextFormatPalette();
        }

        private void ToggleCablePortSelection(
            PlacedCable placed, CablePortLayout layout)
        {
            _selectedCable = placed;
            if (!_selectedCablePorts.Add(layout))
                _selectedCablePorts.Remove(layout);
            RenderAllCables();
            UpdateTextFormatPalette();
        }

        private void DeleteCable(PlacedCable placed)
        {
            MainCanvas.Children.Remove(placed.Container);
            _placedCables.Remove(placed);
            if (_selectedCable == placed) _selectedCable = null;
            RenderAllCables();
            RefreshAutoArrangeExclusionList();
            UpdateCanvasExtent();
            _isDirty = true;
        }

        /// <summary>모든 케이블과 교차 표시·이름표를 저장된 위치대로 즉시 갱신한다.</summary>
        private void RenderAllCables()
        {
            ApplyPendingCablePortDrag();
            CancelCableDragRender();
            foreach (var pc in _placedCables) RenderCable(pc);
            UpdateCableCrossings();
            if (_crossingOverlay != null) _crossingOverlay.Visibility = Visibility.Visible;
            UpdateCableLabels();
            UpdateCableNamesToggleButton();
        }

        private readonly HashSet<PlacedCable> _pendingCableDragRenders = new();
        private bool _cableRenderFramePending;

        private void QueueCableDragRender(PlacedCable? changed = null)
        {
            if (changed == null) _pendingCableDragRenders.UnionWith(_placedCables);
            else _pendingCableDragRenders.Add(changed);
            if (_crossingOverlay != null) _crossingOverlay.Visibility = Visibility.Hidden;
            if (_cableRenderFramePending) return;
            _cableRenderFramePending = true;
            CompositionTarget.Rendering += CableDragRenderFrame;
        }

        private void QueueCablePortDragInput(PlacedCable placed, Point mouse)
        {
            if (_cablePortDragPlaced != placed || _cablePortDragLayout == null) return;
            _pendingCablePortMouse = mouse;
            QueueCableDragRender(placed);
        }

        private void ApplyPendingCablePortDrag()
        {
            var mouse = _pendingCablePortMouse;
            _pendingCablePortMouse = null;
            var placed = _cablePortDragPlaced;
            var port = _cablePortDragLayout;
            if (!mouse.HasValue || placed == null || port == null) return;

            var snapKey = FindSnapKey(placed, port, mouse.Value);
            if (snapKey != null)
            {
                port.ConnectedToPortKey = snapKey;
                var anchor = ResolveAnchor(snapKey);
                if (anchor != null) port.FixedFacing = anchor.Facing;
            }
            else
            {
                port.ConnectedToPortKey = null;
                port.PortX = mouse.Value.X;
                port.PortY = mouse.Value.Y;
                // Facing must use the rebuilt local tail, not stale last-frame bends.
                MoveCablePortTail(placed.Instance, _cablePortDragProtection);
                SnapAndOrientFreeCablePort(placed, port, mouse.Value);
            }
            PreserveCablePortDragBranches(placed.Instance, _cablePortDragProtection);
        }

        private void ExpandCableDragDependencies(HashSet<PlacedCable> changed)
        {
            // Connection topology can change during port snapping. Build once after
            // the latest input is applied, not once for every mouse event.
            var byId = _placedCables.ToDictionary(pc => pc.Instance.Id);
            var neighbors = _placedCables.ToDictionary(pc => pc, _ => new List<PlacedCable>());
            foreach (var pc in _placedCables)
                foreach (var layout in AllPortLayouts(pc.Instance))
                {
                    var parts = layout.ConnectedToPortKey?.Split(':');
                    if (parts == null || parts.Length != 3 || parts[0] != "cab" ||
                        !byId.TryGetValue(parts[1], out var target)) continue;
                    neighbors[pc].Add(target);
                    neighbors[target].Add(pc);
                }
            var queue = new Queue<PlacedCable>(changed);
            while (queue.TryDequeue(out var pc))
                if (neighbors.TryGetValue(pc, out var linked))
                    foreach (var target in linked)
                        if (changed.Add(target)) queue.Enqueue(target);
        }

        private void CableDragRenderFrame(object? sender, EventArgs e) => FlushCableDragRender();

        private void QueueDeviceCableDragRender(PlacedDevice device)
        {
            string prefix = $"dev:{device.Instance.Id}:";
            foreach (var pc in _placedCables)
                if (AllPortLayouts(pc.Instance).Any(layout =>
                    layout.ConnectedToPortKey?.StartsWith(prefix, StringComparison.Ordinal) == true))
                    QueueCableDragRender(pc);
        }

        private void CancelCableDragRender()
        {
            if (_cableRenderFramePending) CompositionTarget.Rendering -= CableDragRenderFrame;
            _cableRenderFramePending = false;
            _pendingCablePortMouse = null;
            _pendingCableDragRenders.Clear();
        }

        private void FlushCableDragRender()
        {
            if (!_cableRenderFramePending) return;
            ApplyPendingCablePortDrag();
            var changed = new HashSet<PlacedCable>(_pendingCableDragRenders);
            CancelCableDragRender();
            ExpandCableDragDependencies(changed);
            foreach (var pc in _placedCables)
                if (changed.Contains(pc)) RenderCable(pc);
            UpdateCableLabels(changed);
        }

        /// <summary>
        /// 케이블 포트끼리의 연결은 두 끝단만 짝을 이루는 1:1 연결이다.
        /// 예전 데이터나 반복 스냅으로 A→B와 B→A가 동시에 생기면 렌더할
        /// 때마다 서로의 이동된 좌표를 다시 따라가며 선이 한쪽으로 늘어난다.
        /// 첫 번째 유효 연결만 유지하고 순환·연쇄·다중 연결은 해제한다.
        /// </summary>
        private void RepairCableToCableConnections()
        {
            var entries = _placedCables
                .SelectMany(pc => AllPortLayouts(pc.Instance).Select(layout => (
                    Key: $"cab:{pc.Instance.Id}:{layout.ConnectorId}",
                    Layout: layout)))
                .ToList();
            var byKey = entries.ToDictionary(item => item.Key, item => item.Layout);
            var occupied = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in entries)
            {
                string? targetKey = entry.Layout.ConnectedToPortKey;
                if (string.IsNullOrWhiteSpace(targetKey) ||
                    !targetKey.StartsWith("cab:", StringComparison.Ordinal))
                    continue;

                bool validTarget = byKey.TryGetValue(targetKey, out var target);
                bool targetAlreadyConnected = validTarget &&
                    !string.IsNullOrWhiteSpace(target!.ConnectedToPortKey);
                if (!validTarget || entry.Key == targetKey ||
                    targetAlreadyConnected || occupied.Contains(entry.Key) ||
                    occupied.Contains(targetKey))
                {
                    entry.Layout.ConnectedToPortKey = null;
                    continue;
                }

                occupied.Add(entry.Key);
                occupied.Add(targetKey);
            }
        }

        private PortSide GetNaturalCablePortFacing(
            PlacedCable owner, CablePortLayout layout)
        {
            var inst = owner.Instance;
            var resolvedPort = CableRenderer.ResolveConnection(layout, ResolveAnchor);
            Point port = new(resolvedPort.X, resolvedPort.Y);
            var references = new List<Point>();

            if (layout == inst.PortA)
            {
                references.AddRange(inst.RootBends.Select(
                    bend => new Point(bend.X, bend.Y)));
                var other = CableRenderer.ResolveConnection(inst.PortB, ResolveAnchor);
                references.Add(new Point(other.X, other.Y));
            }
            else if (layout == inst.PortB)
            {
                references.AddRange(inst.RootBends.AsEnumerable().Reverse().Select(
                    bend => new Point(bend.X, bend.Y)));
                var other = CableRenderer.ResolveConnection(inst.PortA, ResolveAnchor);
                references.Add(new Point(other.X, other.Y));
            }
            else
            {
                var branch = inst.Branches.FirstOrDefault(
                    item => item.Port == layout);
                if (branch != null)
                {
                    references.AddRange(branch.Bends.AsEnumerable().Reverse().Select(
                        bend => new Point(bend.X, bend.Y)));
                }
            }

            foreach (Point reference in references)
            {
                double dx = reference.X - port.X;
                double dy = reference.Y - port.Y;
                if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
                    continue;
                if (Math.Abs(dx) >= Math.Abs(dy))
                    return dx < 0 ? PortSide.Left : PortSide.Right;
                return dy < 0 ? PortSide.Top : PortSide.Bottom;
            }

            // Only resolve the parent path if no existing bend determines facing.
            // Previously every branch facing recursively recomputed it even when
            // the nearest bend had already supplied the answer.
            var ownBranch = inst.Branches.FirstOrDefault(item => ReferenceEquals(item.Port, layout));
            if (ownBranch != null)
            {
                var parentRoute = ComputeLegRoute(inst, ownBranch.ParentId, avoidObstacles: false);
                if (parentRoute is { Count: >= 2 })
                {
                    var anchor = CableRenderer.PointAlongPolyline(parentRoute, ownBranch.T);
                    double dx = anchor.X - port.X, dy = anchor.Y - port.Y;
                    if (Math.Abs(dx) >= .5 || Math.Abs(dy) >= .5)
                        return Math.Abs(dx) >= Math.Abs(dy)
                            ? (dx < 0 ? PortSide.Left : PortSide.Right)
                            : (dy < 0 ? PortSide.Top : PortSide.Bottom);
                }
            }
            return layout.FixedFacing;
        }

        private PortSide GetEffectiveCablePortFacing(
            PlacedCable owner, CablePortLayout layout)
        {
            var resolved = CableRenderer.ResolveConnection(layout, ResolveAnchor);
            return resolved.DeviceFacing ?? GetNaturalCablePortFacing(owner, layout);
        }

        /// <summary>
        /// 사용자가 포트를 실제로 옮기거나 새 연결을 만든 경우에만 방향을
        /// 갱신한다. 단순 선택/재렌더링에서는 절대로 모델을 바꾸지 않는다.
        /// </summary>
        private void UpdatePortFacingAfterUserMove(
            PlacedCable owner, CablePortLayout layout)
        {
            string? targetKey = layout.ConnectedToPortKey;
            if (!string.IsNullOrWhiteSpace(targetKey))
            {
                var parts = targetKey.Split(':');
                if (parts.Length == 3 && parts[0] == "dev")
                {
                    var anchor = ResolveAnchor(targetKey);
                    if (anchor != null) layout.FixedFacing = anchor.Facing;
                    return;
                }

                if (parts.Length == 3 && parts[0] == "cab")
                {
                    var targetOwner = _placedCables.FirstOrDefault(
                        item => item.Instance.Id == parts[1]);
                    var target = FindPortLayoutById(parts[1], parts[2]);
                    if (targetOwner != null && target != null)
                    {
                        target.FixedFacing = GetNaturalCablePortFacing(
                            targetOwner, target);
                        layout.FixedFacing = OppositePortSide(
                            target.FixedFacing);
                        return;
                    }
                }
            }

            layout.FixedFacing = GetNaturalCablePortFacing(owner, layout);
        }

        /// <summary>
        /// 떠 있는 케이블 포트는 단순히 좌표만 격자 교차점에 놓는 것이 아니라,
        /// 그 포트로 들어오는 마지막 케이블 선분과 포트 막대가 항상 직각이 되게 한다.
        /// 드래그 중에도 방향을 바로 갱신해야 마우스를 놓기 전까지 기존 방향의
        /// 포트가 선과 나란히 보이는 순간이 생기지 않는다.
        /// </summary>
        private void SnapAndOrientFreeCablePort(
            PlacedCable owner, CablePortLayout layout, Point requested)
        {
            // 먼저 실제 마우스 위치에서 케이블 본체가 있는 방향을 구한 뒤,
            // 그 방향에 대응하는 격자 변의 중앙으로 옮긴다.
            layout.PortX = requested.X;
            layout.PortY = requested.Y;
            PortSide facing = GetNaturalCablePortFacing(owner, layout);
            Point snapped = SnapCablePortToGridFace(requested, facing);
            layout.PortX = snapped.X;
            layout.PortY = snapped.Y;
            layout.FixedFacing = facing;
        }

        private SnapAnchor? ResolveAnchor(string key)
        {
            var parts = key.Split(':');
            if (parts.Length != 3) return null;

            if (parts[0] == "dev")
            {
                var pd = _placed.FirstOrDefault(p => p.Instance.Id == parts[1]);
                if (pd == null) return null;
                foreach (var (port, ax, ay, side) in DevicePreviewRenderer.GetPortAnchors(
                    pd.EffectiveDevice, pd.Instance.X, pd.Instance.Y, pd.Instance.Width, pd.Instance.Height))
                {
                    if (port.Id == parts[2])
                        return new SnapAnchor { Key = key, X = ax, Y = ay, Facing = side };
                }
                return null;
            }
            if (parts[0] == "cab")
            {
                var lay = FindPortLayoutById(parts[1], parts[2]);
                if (lay == null) return null;
                // 케이블 끝단끼리는 서로 반대 방향으로 뻗어야 두 포트 캡의
                // 바깥쪽에서 각자의 선이 시작되고 상대 케이블을 관통하지 않는다.
                return new SnapAnchor
                {
                    Key = key,
                    X = lay.PortX,
                    Y = lay.PortY,
                    Facing = OppositePortSide(lay.FixedFacing)
                };
            }
            return null;
        }

        private static PortSide OppositePortSide(PortSide side) => side switch
        {
            PortSide.Left => PortSide.Right,
            PortSide.Right => PortSide.Left,
            PortSide.Top => PortSide.Bottom,
            _ => PortSide.Top
        };

        private CablePortLayout? FindPortLayoutById(string cableInstanceId, string connectorId)
        {
            var pc = _placedCables.FirstOrDefault(p => p.Instance.Id == cableInstanceId);
            if (pc == null) return null;
            if (pc.Instance.PortA.ConnectorId == connectorId) return pc.Instance.PortA;
            if (pc.Instance.PortB.ConnectorId == connectorId) return pc.Instance.PortB;
            return pc.Instance.Branches.FirstOrDefault(b => b.Port.ConnectorId == connectorId)?.Port;
        }

        /// <summary>parentId("root" 또는 분기 Id)의 시작~끝 절대좌표(연결 반영)를 계산한다 (재귀).</summary>
        /// <summary>legId("root" 또는 분기 Id)가 가진 Bends 리스트 자체(참조)를 가져온다.</summary>
        private List<ESDP.Models.Point>? GetBendsList(CableInstance inst, string legId)
        {
            if (legId == "root") return inst.RootBends;
            return inst.Branches.FirstOrDefault(b => b.Id == legId)?.Bends;
        }

        private sealed record CablePortDragProtection(
            string LegId, CablePortLayout Port, bool AtStart,
            List<Point> FixedBends, Point? Boundary,
            bool BoundaryVertical, Dictionary<CableBranch, Point> Anchors);

        private CablePortDragProtection? CaptureCablePortDragProtection(
            PlacedCable placed, CablePortLayout port)
        {
            var previousCache = _legRouteCache;
            _legRouteCache = new();
            try
            {
                var inst = placed.Instance;
                var branch = inst.Branches.FirstOrDefault(item => ReferenceEquals(item.Port, port));
                string legId = branch?.Id ?? "root";
                bool atStart = ReferenceEquals(port, inst.PortA);
                var route = ComputeLegRoute(inst, legId, avoidObstacles: false);
                if (route is not { Count: >= 2 }) return null;
                var anchors = CaptureDescendantBranchAnchors(inst, legId, route[0], route[0])
                    .ToDictionary(pair => pair.Key, pair => pair.Value.Anchor);
                var direct = anchors.Where(pair => pair.Key.ParentId == legId)
                    .Select(pair => (Point: pair.Value, T: CableRenderer.FractionAlongPolyline(route, pair.Value)))
                    .OrderBy(pair => pair.T).ToList();
                Point? boundary = direct.Count > 0
                    ? (atStart ? direct[0].Point : direct[^1].Point)
                    : branch != null ? route[0] : null;
                double boundaryT = boundary.HasValue ? CableRenderer.FractionAlongPolyline(route, boundary.Value) : 0;
                var fixedBends = new List<(Point Point, double T)>();
                foreach (var bend in GetBendsList(inst, legId) ?? new())
                {
                    Point point = new(bend.X, bend.Y);
                    double t = CableRenderer.FractionAlongPolyline(route, point);
                    bool fixedPoint = !boundary.HasValue || (atStart ? t >= boundaryT - .000001 : t <= boundaryT + .000001);
                    if (fixedPoint) fixedBends.Add((point, t));
                }
                fixedBends.AddRange(direct);
                if (boundary.HasValue) fixedBends.Add((boundary.Value, boundaryT));
                var tangent = GetPolylineDragMetric(route, boundaryT).Tangent;
                return new(legId, port, atStart,
                    fixedBends.OrderBy(pair => pair.T).Select(pair => pair.Point).Distinct().ToList(),
                    boundary, Math.Abs(tangent.Y) > Math.Abs(tangent.X), anchors);
            }
            finally { _legRouteCache = previousCache; }
        }

        private void MoveCablePortTail(CableInstance instance, CablePortDragProtection? protection)
            => RebuildCablePortTail(instance, protection, forceDeparture: false);

        private void RebuildCablePortTail(CableInstance instance, CablePortDragProtection? protection, bool forceDeparture)
        {
            if (protection?.Boundary == null) return;
            var bends = GetBendsList(instance, protection.LegId);
            if (bends == null) return;
            var resolved = CableRenderer.ResolveConnection(protection.Port, ResolveAnchor);
            Point boundary = protection.Boundary.Value;
            Point port = new(resolved.X, resolved.Y);
            PortSide facing = resolved.DeviceFacing ?? protection.Port.FixedFacing;
            if (!resolved.DeviceFacing.HasValue)
            {
                // Keep the free connector's axis when possible, but face the new
                // junction instead of an obsolete U-shaped tail corner.
                bool vertical = facing is PortSide.Top or PortSide.Bottom;
                double dx = boundary.X - port.X, dy = boundary.Y - port.Y;
                if (vertical && Math.Abs(dy) >= CableRenderer.StubLength)
                    facing = dy < 0 ? PortSide.Top : PortSide.Bottom;
                else if (!vertical && Math.Abs(dx) >= CableRenderer.StubLength)
                    facing = dx < 0 ? PortSide.Left : PortSide.Right;
                else if (Math.Abs(dx) >= CableRenderer.StubLength)
                    facing = dx < 0 ? PortSide.Left : PortSide.Right;
                else if (Math.Abs(dy) >= CableRenderer.StubLength)
                    facing = dy < 0 ? PortSide.Top : PortSide.Bottom;
                protection.Port.FixedFacing = facing;
            }
            var tail = new List<Point>();
            if (forceDeparture)
            {
                // Do not reverse over the protected body when the port crosses
                // behind the junction. Depart perpendicular by one grid lane.
                double coordinate = protection.BoundaryVertical ? boundary.X : boundary.Y;
                double targetCoordinate = protection.BoundaryVertical ? port.X : port.Y;
                double sign = targetCoordinate < coordinate ? -1 : 1;
                double lane = SnapToDiagramGridMidpoint(coordinate + sign * DiagramGridSize);
                // Legacy/off-center anchors can round to only half a cell; that
                // tiny dogleg would be collapsed by the renderer's 12px cleanup.
                if (Math.Abs(lane - coordinate) < DiagramGridSize * .75) lane += sign * DiagramGridSize;
                Point departure = protection.BoundaryVertical
                    ? new Point(lane, boundary.Y) : new Point(boundary.X, lane);
                tail.Add(departure);
                tail.AddRange(BuildLocalPortTail(departure, port, facing, forceEscape: true));
            }
            else tail.AddRange(BuildLocalPortTail(boundary, port, facing));
            if (protection.AtStart) tail.Reverse();
            var points = protection.AtStart
                ? tail.Concat(protection.FixedBends)
                : protection.FixedBends.Concat(tail);
            bends.Clear();
            bends.AddRange(points.Select(point => new ESDP.Models.Point { X = point.X, Y = point.Y }));
        }

        private static List<Point> BuildLocalPortTail(Point boundary, Point port, PortSide facing, bool forceEscape = false)
        {
            bool vertical = facing is PortSide.Top or PortSide.Bottom;
            Vector outward = facing switch
            {
                PortSide.Left => new Vector(-1, 0), PortSide.Right => new Vector(1, 0),
                PortSide.Top => new Vector(0, -1), _ => new Vector(0, 1)
            };
            var tail = new List<Point>();
            if (!forceEscape && Vector.Multiply(boundary - port, outward) >= CableRenderer.StubLength)
            {
                // One elbow suffices when the attachment is already on the
                // connector's outward side: the red L-shaped path in the report.
                tail.Add(vertical ? new Point(port.X, boundary.Y) : new Point(boundary.X, port.Y));
            }
            else
            {
                // A connected device/cable can require the opposite approach.
                // Add only the short escape necessary for that fixed facing.
                Point escape = GetAutoRoutePortEscape(port, facing);
                tail.Add(vertical ? new Point(boundary.X, escape.Y) : new Point(escape.X, boundary.Y));
                tail.Add(escape);
            }
            return tail.Where(point => (point - boundary).Length > .01 && (point - port).Length > .01)
                .Distinct().ToList();
        }

        private void PreserveCablePortDragBranches(
            CableInstance instance, CablePortDragProtection? protection)
            => RestoreCablePortDragAnchors(instance, protection, moveTail: true);

        private void RestoreCablePortDragAnchors(
            CableInstance instance, CablePortDragProtection? protection, bool moveTail)
        {
            if (protection == null) return;
            if (moveTail) MoveCablePortTail(instance, protection);
            if (protection.Boundary.HasValue)
            {
                var route = ComputeLegRoute(instance, protection.LegId, avoidObstacles: false);
                if (route is not { Count: >= 2 }) return;
                Point boundary = protection.Boundary.Value;
                bool contains = Enumerable.Range(0, route.Count - 1).Any(index =>
                    DistancePointToSegment(boundary, route[index], route[index + 1]) < .01);
                if (!contains)
                {
                    // Only repair the movable tail if reversing/collinear cleanup
                    // would remove its fixed attachment. Never rearrange the body.
                    RebuildCablePortTail(instance, protection, forceDeparture: true);
                }
            }
            ReanchorBranchHierarchyToFinalRoutes(instance, protection.Anchors);
        }

        // ================= 포트 드래그(자석 스냅) =================
        private void CablePort_MouseDown(PlacedCable placed, CablePortLayout layout, MouseButtonEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                ToggleCablePortSelection(placed, layout);
                e.Handled = true;
                return;
            }

            ClearCablePortSelection(render: false);
            _cablePortDragPlaced = placed;
            _cablePortDragLayout = layout;
            _cablePortDragProtection = CaptureCablePortDragProtection(placed, layout);
            _pendingCablePortMouse = null;
            placed.Container.CaptureMouse();
            e.Handled = true;
        }

        private bool IsUnconnectedCablePortInsideDeviceBody(
            CablePortLayout layout)
        {
            if (!string.IsNullOrWhiteSpace(layout.ConnectedToPortKey))
                return false;

            var point = new Point(layout.PortX, layout.PortY);
            return _placed.Any(device => new Rect(
                device.Instance.X,
                device.Instance.Y,
                device.Instance.Width,
                device.Instance.Height).Contains(point));
        }

        private void CablePort_Rename(PlacedCable placed, CablePortLayout layout, MouseButtonEventArgs e)
        {
            var conn = CableRenderer.FindConnector(placed.EffectiveCable, layout.ConnectorId);
            if (conn == null) return;

            var win = new RenamePortWindow(conn.PortName) { Owner = this };
            if (win.ShowDialog() == true && !string.IsNullOrWhiteSpace(win.ResultName))
            {
                string originalName = placed.TemplateCable.Name;
                var editedCable = CloneCableDefinition(placed.EffectiveCable);
                var editedConnector = editedCable.Connectors.FirstOrDefault(item => item.Id == conn.Id);
                if (editedConnector != null)
                    editedConnector.PortName = win.ResultName!.Trim();
                SynchronizeCableSharedData(
                    placed.TemplateCable, editedCable, originalName);
                RenderAllCables();
                _isDirty = true;
            }
            e.Handled = true;
        }

        private void CablePort_AutoFacing(PlacedCable placed, CablePortLayout layout)
        {
            UpdatePortFacingAfterUserMove(placed, layout);
            RenderAllCables();
            _isDirty = true;
        }

        private string? FindSnapKey(PlacedCable self, CablePortLayout dragging, Point mouse)
        {
            double best = CableSnapRadius;
            string? foundKey = null;

            foreach (var pd in _placed)
            {
                foreach (var (port, ax, ay, side) in DevicePreviewRenderer.GetPortAnchors(
                    pd.EffectiveDevice, pd.Instance.X, pd.Instance.Y, pd.Instance.Width, pd.Instance.Height))
                {
                    double d = Dist(mouse.X, mouse.Y, ax, ay);
                    if (d < best) { best = d; foundKey = $"dev:{pd.Instance.Id}:{port.Id}"; }
                }
            }

            foreach (var pc in _placedCables)
            {
                foreach (var lay in AllPortLayouts(pc.Instance))
                {
                    if (pc == self && lay == dragging) continue;
                    string candidateKey =
                        $"cab:{pc.Instance.Id}:{lay.ConnectorId}";
                    if (!CanConnectCablePort(candidateKey, dragging))
                        continue;
                    double d = Dist(mouse.X, mouse.Y, lay.PortX, lay.PortY);
                    if (d < best) { best = d; foundKey = candidateKey; }
                }
            }
            return foundKey;
        }

        private bool CanConnectCablePort(
            string candidateKey, CablePortLayout dragging)
        {
            var parts = candidateKey.Split(':');
            if (parts.Length != 3) return false;
            var target = FindPortLayoutById(parts[1], parts[2]);
            if (target == null || target == dragging) return false;

            // 목적 포트가 이미 다른 곳을 따라가거나, 다른 포트가 목적 포트를
            // 따라가는 중이면 새 연결을 만들지 않는다. 현재 드래그 포트가
            // 이미 이 목적 포트를 따라가는 경우만 그대로 허용한다.
            if (!string.IsNullOrWhiteSpace(target.ConnectedToPortKey))
                return false;

            foreach (var pc in _placedCables)
            {
                foreach (var layout in AllPortLayouts(pc.Instance))
                {
                    if (layout == dragging) continue;
                    if (string.Equals(
                        layout.ConnectedToPortKey, candidateKey,
                        StringComparison.Ordinal))
                        return false;
                }
            }
            return true;
        }

        private static IEnumerable<CablePortLayout> AllPortLayouts(CableInstance inst)
        {
            yield return inst.PortA;
            yield return inst.PortB;
            foreach (var b in inst.Branches) yield return b.Port;
        }

        // ================= 선 우클릭: 꺾인점 초기화 / 분기 삭제 =================
        private void CableLine_RightClick(PlacedCable placed, string legId, MouseButtonEventArgs e)
        {
            var bends = GetBendsList(placed.Instance, legId);
            if (bends == null) return;
            var clickPoint = e.GetPosition(MainCanvas);

            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem("케이블 정보 / 포트 편집...", () =>
                OpenCableProperties(placed)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("여기에 분기 포트 추가", () =>
                AddBranchPort(placed, legId, clickPoint)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("이 다리의 꺾인점 전부 초기화", () =>
            {
                bends.Clear();
                RenderAllCables();
                _isDirty = true;
            }));

            if (legId != "root")
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem("이 분기 삭제", () => DeleteBranch(placed, legId)));
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("케이블 삭제 (Delete)", () => DeleteCable(placed)));

            placed.Container.ContextMenu = menu;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void OpenCableProperties(PlacedCable placed)
        {
            var cable = placed.EffectiveCable;
            string originalName = placed.TemplateCable.Name;
            var rows = new List<CablePortPropertyRow>();

            foreach (var layout in AllPortLayouts(placed.Instance))
            {
                var connector = CableRenderer.FindConnector(cable, layout.ConnectorId);
                if (connector == null) continue;

                rows.Add(new CablePortPropertyRow
                {
                    ConnectorId = connector.Id,
                    PortName = connector.PortName,
                    ConnType = connector.ConnType,
                    ConnectorCatalogKey = connector.ConnectorCatalogKey,
                    Description = connector.Description,
                    KeyAngle = connector.KeyAngle,
                    ShowConnType = connector.ShowConnType,
                    ShowLabel = connector.ShowLabel,
                    LabelFontSize = connector.LabelFontSize,
                    LabelColorHex = connector.LabelColorHex,
                    Pins = CloneCablePins(connector.Pins),
                    ConnectionStatus = DescribeCablePortConnection(placed, layout)
                });
            }

            var window = new CablePropertiesWindow(
                cable, rows, placed.Instance.ShowName, _customCableCategories) { Owner = this };
            if (window.ShowDialog() != true) return;

            var editedCable = CloneCableDefinition(cable);
            editedCable.Name = window.ResultCableName;
            editedCable.NameFontSize = window.ResultNameFontSize;
            editedCable.Category = window.ResultCategory;
            editedCable.CustomCategoryName = window.ResultCustomCategoryName;
            editedCable.LineColorHex = window.ResultCableLineColorHex;
            editedCable.OverallBraid = window.ResultOverallBraid;
            editedCable.Tube = window.ResultTube;
            editedCable.SignalRows = CloneCableSignalRows(window.ResultSignalRows);
            placed.Instance.ShowName = window.ResultShowCableName;
            editedCable.Connectors = window.ResultPortRows.Select(edited => new CableConnector
            {
                Id = edited.ConnectorId,
                PortName = edited.PortName.Trim(),
                ConnType = edited.ConnType.Trim(),
                ConnectorCatalogKey = edited.ConnectorCatalogKey,
                Description = edited.Description.Trim(),
                KeyAngle = edited.KeyAngle.Trim(),
                ShowConnType = edited.ShowConnType,
                ShowLabel = edited.ShowLabel,
                LabelFontSize = edited.LabelFontSize > 0 ? edited.LabelFontSize : 9,
                LabelColorHex = ColorOption.NormalizeHex(edited.LabelColorHex) ?? "#000000",
                Pins = CloneCablePins(edited.Pins)
            }).ToList();

            SynchronizeCableSharedData(
                placed.TemplateCable, editedCable, originalName);

            _isDirty = true;
            SelectCable(placed);
            RenderAllCables();
            StatusText.Text = $"케이블 '{editedCable.Name}' 정보가 같은 이름의 모든 개체에 적용됨";
        }

        private void ToggleAllCableNames_Click(object sender, RoutedEventArgs e)
        {
            // 일부만 보이는 혼합 상태에서는 먼저 모두 표시한다.
            bool show = _placedCables.Any(cable => !cable.Instance.ShowName);
            SetAllCableNamesVisible(show);
        }

        private sealed record ConnectorEndpoint(
            string Identity, string DisplayName, string ConnectorName, string CatalogKey);

        private void AnalyzeD38999Connections_Click(object sender, RoutedEventArgs e)
        {
            var rows = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int compatible = 0;
            int incompatible = 0;
            int uncertain = 0;

            foreach (var cable in _placedCables)
            foreach (var layout in AllPortLayouts(cable.Instance))
            {
                if (string.IsNullOrWhiteSpace(layout.ConnectedToPortKey)) continue;
                var source = GetCableConnectorEndpoint(cable, layout.ConnectorId);
                var target = GetConnectorEndpoint(layout.ConnectedToPortKey!);
                if (source == null || target == null) continue;
                string pairKey = string.Compare(source.Identity, target.Identity,
                    StringComparison.OrdinalIgnoreCase) <= 0
                    ? $"{source.Identity}|{target.Identity}"
                    : $"{target.Identity}|{source.Identity}";
                if (!visited.Add(pairKey)) continue;

                bool sourceD38999 = IsD38999Endpoint(source);
                bool targetD38999 = IsD38999Endpoint(target);
                // 사용자 커넥터나 다른 시리즈는 자료가 충분하지 않으므로 검사하지 않는다.
                // 양쪽이 모두 D38999로 확인되는 실제 D38999 결합만 판정 대상이다.
                if (!sourceD38999 || !targetD38999) continue;

                bool sourceParsed = D38999CatalogService.Instance.TryParsePartNumber(
                    source.ConnectorName, out var first);
                bool targetParsed = D38999CatalogService.Instance.TryParsePartNumber(
                    target.ConnectorName, out var second);
                string title = $"{source.DisplayName} [{source.ConnectorName}]  ↔  " +
                               $"{target.DisplayName} [{target.ConnectorName}]";
                if (!sourceParsed || !targetParsed || first == null || second == null)
                {
                    uncertain++;
                    rows.Add($"△ 판정 불가: {title}\n   양쪽 모두 완전한 D38999 품번이 필요합니다.");
                    continue;
                }

                var result = D38999CatalogService.Instance.AnalyzeMating(first, second);
                if (!result.IsCompatible)
                {
                    incompatible++;
                    rows.Add($"✕ 결합 불가: {title}\n   {string.Join("; ", result.Reasons)}");
                }
                else if (!result.IsConclusive)
                {
                    uncertain++;
                    rows.Add($"△ 확인 필요: {title}\n   인서트·접점·키잉은 일치하지만 플러그/리셉터클 형상을 해석하지 못했습니다.");
                }
                else
                {
                    compatible++;
                    rows.Add($"✓ 결합 가능: {title}");
                }
            }

            string summary = $"결합 가능 {compatible}개 · 결합 불가 {incompatible}개 · 확인 필요 {uncertain}개";
            string report = rows.Count == 0
                ? "현재 시트에서 검사할 D38999 연결을 찾지 못했습니다.\n\n" +
                  "커넥터 선택창에서 D38999 품번을 지정하고 포트끼리 연결한 뒤 다시 검사하세요."
                : summary + "\n\n" + string.Join("\n\n", rows);
            ShowConnectorCompatibilityReport(report, incompatible > 0);
        }

        private static bool IsD38999Endpoint(ConnectorEndpoint endpoint)
            => endpoint.CatalogKey.StartsWith("D38999:", StringComparison.OrdinalIgnoreCase) ||
               D38999CatalogService.Instance.TryParsePartNumber(endpoint.ConnectorName, out _);

        private ConnectorEndpoint? GetCableConnectorEndpoint(
            PlacedCable cable, string connectorId)
        {
            var connector = CableRenderer.FindConnector(cable.EffectiveCable, connectorId);
            if (connector == null) return null;
            return new ConnectorEndpoint(
                $"cab:{cable.Instance.Id}:{connectorId}",
                $"{cable.EffectiveCable.Name}/{connector.PortName}",
                connector.ConnType, connector.ConnectorCatalogKey);
        }

        private ConnectorEndpoint? GetConnectorEndpoint(string key)
        {
            string[] parts = key.Split(':');
            if (parts.Length != 3) return null;
            if (parts[0].Equals("dev", StringComparison.OrdinalIgnoreCase))
            {
                var device = _placed.FirstOrDefault(item => item.Instance.Id == parts[1]);
                var port = device?.EffectiveDevice.Ports.FirstOrDefault(item => item.Id == parts[2]);
                return device == null || port == null ? null : new ConnectorEndpoint(
                    key, $"{device.EffectiveDevice.Name}/{port.Designator}",
                    port.ConnType, port.ConnectorCatalogKey);
            }
            if (parts[0].Equals("cab", StringComparison.OrdinalIgnoreCase))
            {
                var cable = _placedCables.FirstOrDefault(item => item.Instance.Id == parts[1]);
                return cable == null ? null : GetCableConnectorEndpoint(cable, parts[2]);
            }
            return null;
        }

        private void ShowConnectorCompatibilityReport(string report, bool hasFailure)
        {
            var text = new TextBox
            {
                Text = report,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontSize = 13
            };
            var close = new Button
            {
                Content = "닫기", Width = 84, Height = 30,
                Style = AppThemeManager.GetStyle("PrimaryButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0), IsDefault = true
            };
            var root = new DockPanel { Margin = new Thickness(16) };
            DockPanel.SetDock(close, Dock.Bottom);
            root.Children.Add(close);
            root.Children.Add(text);
            var window = new Window
            {
                Owner = this,
                Title = hasFailure ? "커넥터 결합 검사 — 불일치 발견" : "커넥터 결합 검사",
                Width = 760, Height = 520, MinWidth = 560, MinHeight = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = root
            };
            close.Click += (_, _) => window.Close();
            window.ShowDialog();
        }

        private void UpdateCableNamesToggleButton()
        {
            if (CableNamesToggleButton == null) return;

            bool hasCables = _placedCables.Count > 0;
            bool allVisible = hasCables &&
                _placedCables.All(cable => cable.Instance.ShowName);
            CableNamesToggleButton.IsEnabled = hasCables;
            CableNamesToggleButton.Content = allVisible
                ? "케이블명 모두 숨김"
                : "케이블명 모두 표시";
        }

        private void SetAllCableNamesVisible(bool visible)
        {
            foreach (var cable in _placedCables)
                cable.Instance.ShowName = visible;
            RenderAllCables();
            _isDirty = true;
            StatusText.Text = visible
                ? "모든 케이블명을 표시했습니다."
                : "모든 케이블명을 숨겼습니다.";
        }

        private string DescribeCablePortConnection(PlacedCable owner, CablePortLayout layout)
        {
            if (!string.IsNullOrWhiteSpace(layout.ConnectedToPortKey))
                return DescribeConnectionTarget(layout.ConnectedToPortKey!);

            // 다른 케이블 포트가 이 포트를 대상으로 연결한 경우도 연결 상태로 표시한다.
            string ownKey = $"cab:{owner.Instance.Id}:{layout.ConnectorId}";
            foreach (var otherCable in _placedCables)
            {
                foreach (var otherLayout in AllPortLayouts(otherCable.Instance))
                {
                    if (!string.Equals(otherLayout.ConnectedToPortKey, ownKey,
                                       StringComparison.OrdinalIgnoreCase))
                        continue;

                    var connector = CableRenderer.FindConnector(
                        otherCable.EffectiveCable, otherLayout.ConnectorId);
                    string portName = connector?.PortName ?? "이름 없는";
                    return $"{otherCable.EffectiveCable.Name}의 {portName} 포트에 연결됨";
                }
            }

            return "연결되지 않음";
        }

        private string DescribeConnectionTarget(string key)
        {
            var parts = key.Split(':');
            if (parts.Length != 3) return "연결 대상 정보를 찾을 수 없음";

            if (parts[0] == "dev")
            {
                var device = _placed.FirstOrDefault(item => item.Instance.Id == parts[1]);
                if (device == null) return "연결 대상 장치를 찾을 수 없음";

                var port = device.EffectiveDevice.Ports.FirstOrDefault(item => item.Id == parts[2]);
                string portName = string.IsNullOrWhiteSpace(port?.Designator)
                    ? "이름 없는"
                    : port.Designator;
                return $"{device.EffectiveDevice.Name}의 {portName} 포트에 연결됨";
            }

            if (parts[0] == "cab")
            {
                var cable = _placedCables.FirstOrDefault(item => item.Instance.Id == parts[1]);
                if (cable == null) return "연결 대상 케이블을 찾을 수 없음";

                var connector = CableRenderer.FindConnector(cable.EffectiveCable, parts[2]);
                string portName = connector?.PortName ?? "이름 없는";
                return $"{cable.EffectiveCable.Name}의 {portName} 포트에 연결됨";
            }

            return "연결 대상 정보를 찾을 수 없음";
        }

        /// <summary>
        /// 배치된 케이블 하나만의 포트 정의를 수정할 수 있도록 템플릿을 개별 설정으로 복제한다.
        /// 분기 포트는 캔버스의 이 케이블 인스턴스에만 생기므로, 같은 템플릿의 다른 배치에는
        /// 새 포트가 섞이지 않게 한다.
        /// </summary>
        private static Cable EnsureInstanceCableOverride(PlacedCable placed)
        {
            placed.Instance.Override ??= DeepClone(placed.TemplateCable);
            return placed.Instance.Override;
        }

        private static List<CablePin> CloneCablePins(IEnumerable<CablePin>? pins)
            => pins?.Select(pin => new CablePin
            {
                PinNum = pin.PinNum,
                PinName = pin.PinName,
                PinType = pin.PinType,
                InConn = pin.InConn,
                TwpShield = pin.TwpShield,
                MarkColorHex = pin.MarkColorHex
            }).ToList() ?? new List<CablePin>();

        private static List<CableSignalRow> CloneCableSignalRows(
            IEnumerable<CableSignalRow>? rows)
            => rows?.Select(row =>
            {
                var copy = new CableSignalRow
                {
                    Id = row.Id,
                    ParentSignalId = row.ParentSignalId,
                    PinName = row.PinName,
                    WireType = row.WireType
                };
                foreach (var pair in row.PortPins)
                    copy.PortPins[pair.Key] = CloneCablePins(new[] { pair.Value })[0];
                return copy;
            }).ToList() ?? new List<CableSignalRow>();

        private static string MakeUniqueCablePortName(Cable cable)
        {
            var used = cable.Connectors
                .Select(c => c.PortName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (int number = 3; ; number++)
            {
                string candidate = $"P{number}";
                if (!used.Contains(candidate)) return candidate;
            }
        }

        private static (double T, Point Anchor, bool IsHorizontal) FindPolylineAttachment(
            List<Point> route, Point clickPoint)
        {
            double totalLength = 0;
            for (int i = 0; i < route.Count - 1; i++)
                totalLength += Dist(route[i].X, route[i].Y, route[i + 1].X, route[i + 1].Y);

            if (totalLength < 0.0001)
                return (0.5, route[0], true);

            double accumulated = 0;
            double bestDistance = double.MaxValue;
            double bestLength = totalLength / 2;
            Point bestPoint = route[0];
            bool bestIsHorizontal = true;

            for (int i = 0; i < route.Count - 1; i++)
            {
                var a = route[i];
                var b = route[i + 1];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double lengthSquared = dx * dx + dy * dy;
                double length = Math.Sqrt(lengthSquared);
                if (length < 0.0001) continue;

                double fraction = Math.Max(0, Math.Min(1,
                    ((clickPoint.X - a.X) * dx + (clickPoint.Y - a.Y) * dy) / lengthSquared));
                var projected = new Point(a.X + dx * fraction, a.Y + dy * fraction);
                double distance = Dist(clickPoint.X, clickPoint.Y, projected.X, projected.Y);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestLength = accumulated + length * fraction;
                    bestPoint = projected;
                    bestIsHorizontal = Math.Abs(dx) >= Math.Abs(dy);
                }

                accumulated += length;
            }

            return (Math.Max(0, Math.Min(1, bestLength / totalLength)), bestPoint, bestIsHorizontal);
        }

        /// <summary>
        /// 부모 케이블 위의 분기 접점을 가장 가까운 격자 중앙선에 붙인다.
        /// 가로 선분에서는 X, 세로 선분에서는 Y만 움직여 접점이 부모 선을
        /// 벗어나지 않게 한다.
        /// </summary>
        private static (double T, Point Anchor, bool IsHorizontal)
            FindGridSnappedPolylineAttachment(List<Point> route, Point point)
        {
            var raw = FindPolylineAttachment(route, point);
            Point snapped = raw.Anchor;

            double bestDistance = double.MaxValue;
            Point bestPoint = raw.Anchor;
            bool bestIsHorizontal = raw.IsHorizontal;
            for (int index = 0; index < route.Count - 1; index++)
            {
                var start = route[index];
                var end = route[index + 1];
                bool horizontal = Math.Abs(start.Y - end.Y) < 0.01;
                bool vertical = Math.Abs(start.X - end.X) < 0.01;
                if (!horizontal && !vertical) continue;

                Point candidate = horizontal
                    ? new Point(
                        SnapToGridMidpointWithin(point.X, start.X, end.X),
                        start.Y)
                    : new Point(
                        start.X,
                        SnapToGridMidpointWithin(point.Y, start.Y, end.Y));
                double distance = Dist(
                    point.X, point.Y, candidate.X, candidate.Y);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestPoint = candidate;
                bestIsHorizontal = horizontal;
            }

            if (bestDistance < double.MaxValue)
                snapped = bestPoint;
            double t = CableRenderer.FractionAlongPolyline(route, snapped);
            return (Math.Max(0, Math.Min(1, t)), snapped, bestIsHorizontal);
        }

        /// <summary>
        /// 현재 T가 속한 부모 선분을 유지한 채 그 선분 안에서만 중앙선에 붙인다.
        /// 첫 분기 선분 핸들을 끌 때 가까운 다른 선분으로 튀지 않게 하는 용도다.
        /// </summary>
        private static double SnapPolylineFractionOnCurrentSegment(
            List<Point> route, double t)
        {
            if (route.Count < 2) return Math.Max(0, Math.Min(1, t));

            double totalLength = 0;
            for (int index = 0; index < route.Count - 1; index++)
                totalLength += Dist(
                    route[index].X, route[index].Y,
                    route[index + 1].X, route[index + 1].Y);
            if (totalLength < 0.001) return 0.5;

            double targetLength = Math.Max(0, Math.Min(1, t)) * totalLength;
            double accumulated = 0;
            for (int index = 0; index < route.Count - 1; index++)
            {
                var start = route[index];
                var end = route[index + 1];
                double segmentLength = Dist(
                    start.X, start.Y, end.X, end.Y);
                if (segmentLength < 0.001) continue;
                if (accumulated + segmentLength + 0.001 < targetLength &&
                    index < route.Count - 2)
                {
                    accumulated += segmentLength;
                    continue;
                }

                double local = Math.Max(0, Math.Min(1,
                    (targetLength - accumulated) / segmentLength));
                Point point = new(
                    start.X + (end.X - start.X) * local,
                    start.Y + (end.Y - start.Y) * local);
                if (Math.Abs(start.Y - end.Y) < 0.01)
                    point.X = SnapToGridMidpointWithin(
                        point.X, start.X, end.X);
                else if (Math.Abs(start.X - end.X) < 0.01)
                    point.Y = SnapToGridMidpointWithin(
                        point.Y, start.Y, end.Y);
                return Math.Max(0, Math.Min(1,
                    CableRenderer.FractionAlongPolyline(route, point)));
            }

            return Math.Max(0, Math.Min(1, t));
        }

        private double ScoreBranchPortCandidate(Point anchor, Point port)
        {
            double score = 0;
            double canvasWidth = Math.Max(MainCanvas.ActualWidth, MainCanvas.Width);
            double canvasHeight = Math.Max(MainCanvas.ActualHeight, MainCanvas.Height);

            if (port.X < 20 || port.Y < 20 || port.X > canvasWidth - 20 || port.Y > canvasHeight - 20)
                score += 10000;

            // 분기선 안쪽을 검사해 기존 케이블과 포개지거나 장치를 가로지르는 쪽을 피한다.
            double[] samples = { 0.25, 0.5, 0.75, 1.0 };
            foreach (double sample in samples)
            {
                var p = new Point(
                    anchor.X + (port.X - anchor.X) * sample,
                    anchor.Y + (port.Y - anchor.Y) * sample);

                foreach (var segment in _placedCables.SelectMany(c => c.RenderedSegments))
                {
                    double distance = DistancePointToSegment(p, segment.A, segment.B);
                    if (distance < 12)
                        score += (12 - distance) * 12;
                }

                foreach (var device in _placed)
                {
                    var bounds = new Rect(
                        device.Instance.X - 12,
                        device.Instance.Y - 12,
                        device.Instance.Width + 24,
                        device.Instance.Height + 24);
                    if (bounds.Contains(p)) score += 1000;
                }
            }

            return score;
        }

        private static double DistancePointToSegment(Point p, Point a, Point b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 0.0001)
                return Dist(p.X, p.Y, a.X, a.Y);

            double fraction = Math.Max(0, Math.Min(1,
                ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared));
            return Dist(p.X, p.Y, a.X + dx * fraction, a.Y + dy * fraction);
        }

        /// <summary>선 위의 선택 지점에 새 커넥터 포트를 가진 분기를 만든다.</summary>
        private void AddBranchPort(PlacedCable placed, string parentLegId, Point clickPoint)
        {
            var parentRoute = ComputeLegRoute(placed.Instance, parentLegId);
            if (parentRoute == null || parentRoute.Count < 2) return;

            var attachment = FindGridSnappedPolylineAttachment(
                parentRoute, clickPoint);
            double t = attachment.T;
            var anchor = attachment.Anchor;

            var connector = new CableConnector
            {
                PortName = MakeUniqueCablePortName(placed.TemplateCable)
            };
            placed.TemplateCable.Connectors.Add(connector);

            const double branchPortDistance = 100;
            Point firstCandidate;
            Point secondCandidate;
            PortSide firstFacing;
            PortSide secondFacing;

            if (attachment.IsHorizontal)
            {
                firstCandidate = new Point(anchor.X, anchor.Y + branchPortDistance);
                secondCandidate = new Point(anchor.X, anchor.Y - branchPortDistance);
                firstFacing = PortSide.Top;
                secondFacing = PortSide.Bottom;
            }
            else
            {
                firstCandidate = new Point(anchor.X + branchPortDistance, anchor.Y);
                secondCandidate = new Point(anchor.X - branchPortDistance, anchor.Y);
                firstFacing = PortSide.Left;
                secondFacing = PortSide.Right;
            }

            bool useFirst = ScoreBranchPortCandidate(anchor, firstCandidate)
                            <= ScoreBranchPortCandidate(anchor, secondCandidate);
            Point portPosition = useFirst ? firstCandidate : secondCandidate;
            PortSide portFacing = useFirst ? firstFacing : secondFacing;
            portPosition = SnapCablePortToGridFace(portPosition, portFacing);

            placed.Instance.Branches.Add(new CableBranch
            {
                ParentId = parentLegId,
                T = t,
                Port = new CablePortLayout
                {
                    ConnectorId = connector.Id,
                    PortX = portPosition.X,
                    PortY = portPosition.Y,
                    FixedFacing = portFacing
                }
            });

            SynchronizeCableSharedData(
                placed.TemplateCable, placed.TemplateCable,
                placed.TemplateCable.Name, ensureLayouts: true);

            SelectCable(placed);
            RenderAllCables();
            UpdateCanvasExtent();
            StatusText.Text = $"분기 포트 '{connector.PortName}' 추가됨 — 초록색 분기점을 끌어 위치를 바꿀 수 있습니다";
            _registeredCables.FirstOrDefault(
                item => item.Cable == placed.TemplateCable)?.Refresh();
            _isDirty = true;
        }

        /// <summary>
        /// 꺾인점 하나만 골라서 삭제한다. routePts 기준 인덱스(1..count-2)를 먼저 실체화한 뒤,
        /// 그 인덱스에 해당하는 항목만 bends 리스트에서 제거한다.
        /// </summary>
        /// <summary>
        /// 이 다리의 경로를 살펴보고, 편집하다 보니 거의 겹치거나(두 점이 아주 가까워짐)
        /// 이미 일직선이 되어버린(꺾일 이유가 없어진) 꺾인점을 자동으로 정리한다.
        /// </summary>
        private void SimplifyBends(CableInstance inst, string legId)
        {
            var bends = GetBendsList(inst, legId);
            if (bends == null || bends.Count == 0) return;

            var fullPts = ComputeLegRoute(inst, legId, avoidObstacles: false);
            if (fullPts == null || fullPts.Count <= 2) return;

            // 두 점 사이의 "직선거리"로 보면, X는 거의 같은데 Y가 멀리 떨어진 경우를 놓친다.
            // 그래서 X축과 Y축을 따로 비교한다.
            const double eps = 1.0;
            var pts = new List<Point>(fullPts);

            // ① 축별 스냅: 앞 점과 X(또는 Y)가 거의 같으면 아예 똑같은 값으로 맞춰버린다.
            //    이러면 눈에 띄지도 않는 미세한 어긋남(=쓸데없는 지그재그)이 완전한 직선이 된다.
            for (int i = 1; i < pts.Count; i++)
            {
                double nx = pts[i].X, ny = pts[i].Y;
                if (Math.Abs(nx - pts[i - 1].X) < eps) nx = pts[i - 1].X;
                if (Math.Abs(ny - pts[i - 1].Y) < eps) ny = pts[i - 1].Y;
                pts[i] = new Point(nx, ny);
            }

            // ② 앞 점과 완전히 같아진 점 제거
            for (int i = pts.Count - 1; i >= 1; i--)
                if (Math.Abs(pts[i].X - pts[i - 1].X) < 0.01 && Math.Abs(pts[i].Y - pts[i - 1].Y) < 0.01)
                    pts.RemoveAt(i);

            // ③ 서로 겹쳐 되돌아가는 짧은 ㄷ자 구간 제거.
            //    A─B
            //      │  처럼 평행한 바깥 선분이 짧은 연결선으로 이어지고 투영 구간이 겹치면,
            //    D─C  B/C 는 더 이상 의미가 없으므로 A─D 한 줄로 합친다.
            CollapseShortOverlappingDoglegs(
                pts,
                hasStartStub: legId == "root",
                hasEndStub: true);

            // ④ 앞뒤와 한 줄로 늘어선 가운데 점 제거 (더 이상 "꺾이는" 지점이 아님)
            bool changed = true;
            while (changed && pts.Count > 2)
            {
                changed = false;
                for (int i = 1; i < pts.Count - 1; i++)
                {
                    var prev = pts[i - 1]; var cur = pts[i]; var next = pts[i + 1];
                    bool sameVertical = Math.Abs(prev.X - cur.X) < 0.01 && Math.Abs(cur.X - next.X) < 0.01;
                    bool sameHorizontal = Math.Abs(prev.Y - cur.Y) < 0.01 && Math.Abs(cur.Y - next.Y) < 0.01;
                    if (sameVertical || sameHorizontal)
                    {
                        pts.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }

            var newBends = MaterializeBends(inst, legId, pts);
            bends.Clear();
            bends.AddRange(newBends);
        }

        /// <summary>
        /// 핸들을 끌다 생긴 짧은 왕복 구간을 제거한다.
        /// 네 점 A-B-C-D 에서 AB와 CD가 서로 평행하고 투영 구간이 겹치며,
        /// 둘 사이의 BC가 짧으면 두 평행선을 같은 축으로 맞춘 뒤 B/C를 제거한다.
        /// 포트와 자동 스텁은 움직이지 않고, 반대쪽 편집점만 그 축에 맞춘다.
        /// </summary>
        private static void CollapseShortOverlappingDoglegs(
            List<Point> pts, bool hasStartStub, bool hasEndStub)
        {
            const double shortGap = 12.0;
            const double axisEps = 0.01;

            bool IsFixed(int index)
            {
                if (index == 0 || index == pts.Count - 1) return true;
                if (hasStartStub && index == 1) return true;
                if (hasEndStub && index == pts.Count - 2) return true;
                return false;
            }

            static bool Overlaps(double a1, double a2, double b1, double b2)
            {
                double left = Math.Max(Math.Min(a1, a2), Math.Min(b1, b2));
                double right = Math.Min(Math.Max(a1, a2), Math.Max(b1, b2));
                return left <= right + 0.01;
            }

            bool changed = true;
            while (changed && pts.Count >= 4)
            {
                changed = false;
                for (int aIndex = 0; aIndex <= pts.Count - 4; aIndex++)
                {
                    int bIndex = aIndex + 1;
                    int cIndex = aIndex + 2;
                    int dIndex = aIndex + 3;
                    var a = pts[aIndex];
                    var b = pts[bIndex];
                    var c = pts[cIndex];
                    var d = pts[dIndex];

                    bool abHorizontal = Math.Abs(a.Y - b.Y) < axisEps;
                    bool bcVertical = Math.Abs(b.X - c.X) < axisEps;
                    bool cdHorizontal = Math.Abs(c.Y - d.Y) < axisEps;
                    bool horizontalDogleg =
                        abHorizontal && bcVertical && cdHorizontal &&
                        Math.Abs(b.Y - c.Y) <= shortGap &&
                        Overlaps(a.X, b.X, c.X, d.X);

                    bool abVertical = Math.Abs(a.X - b.X) < axisEps;
                    bool bcHorizontal = Math.Abs(b.Y - c.Y) < axisEps;
                    bool cdVertical = Math.Abs(c.X - d.X) < axisEps;
                    bool verticalDogleg =
                        abVertical && bcHorizontal && cdVertical &&
                        Math.Abs(b.X - c.X) <= shortGap &&
                        Overlaps(a.Y, b.Y, c.Y, d.Y);

                    if (!horizontalDogleg && !verticalDogleg) continue;
                    if (IsFixed(bIndex) || IsFixed(cIndex)) continue;

                    bool aFixed = IsFixed(aIndex);
                    bool dFixed = IsFixed(dIndex);
                    if (aFixed && dFixed) continue;

                    if (horizontalDogleg)
                    {
                        if (aFixed)
                            pts[dIndex] = new Point(d.X, a.Y);
                        else
                            pts[aIndex] = new Point(a.X, d.Y);
                    }
                    else
                    {
                        if (aFixed)
                            pts[dIndex] = new Point(a.X, d.Y);
                        else
                            pts[aIndex] = new Point(d.X, a.Y);
                    }

                    pts.RemoveAt(cIndex);
                    pts.RemoveAt(bIndex);
                    changed = true;
                    break;
                }
            }
        }

        /// <summary>
        /// 현재 그려진 경로(routePts)에서 "실제로 저장해야 할 꺾인점"만 골라낸다.
        /// 경로의 양 끝(포트/앵커)은 물론이고, 포트 옆에 자동으로 생기는 스텁 지점도 제외한다.
        /// 스텁까지 저장해버리면 다음에 그릴 때 스텁이 또 자동으로 붙어서 점이 두 개로 늘어나고,
        /// 편집할수록 계속 쌓인다 (거의 붙어있는 주황 점 두 개가 생기던 원인).
        /// </summary>
        private List<ESDP.Models.Point> MaterializeBends(CableInstance inst, string legId, List<Point> routePts)
        {
            var excluded = new List<Point>();
            var owner = _placedCables.FirstOrDefault(item => item.Instance == inst);
            if (legId == "root")
            {
                var a = CableRenderer.ResolveConnection(inst.PortA, ResolveAnchor);
                var b = CableRenderer.ResolveConnection(inst.PortB, ResolveAnchor);
                var aFacing = owner != null
                    ? GetEffectiveCablePortFacing(owner, inst.PortA)
                    : inst.PortA.FixedFacing;
                var bFacing = owner != null
                    ? GetEffectiveCablePortFacing(owner, inst.PortB)
                    : inst.PortB.FixedFacing;
                excluded.Add(CableRenderer.StubPoint(new Point(a.X, a.Y), aFacing));
                excluded.Add(CableRenderer.StubPoint(new Point(b.X, b.Y), bFacing));
                // 좁은 통로에서는 렌더러가 왕복 스텁을 포트 캡 외곽의
                // 0길이 접속점으로 접는다. 이 자동 접속점도 사용자가 만든
                // 꺾임점으로 저장하지 않는다.
                excluded.Add(CableRenderer.PortEdgePoint(
                    new Point(a.X, a.Y), aFacing));
                excluded.Add(CableRenderer.PortEdgePoint(
                    new Point(b.X, b.Y), bFacing));
            }
            else
            {
                var br = inst.Branches.FirstOrDefault(x => x.Id == legId);
                if (br != null)
                {
                    var p = CableRenderer.ResolveConnection(br.Port, ResolveAnchor);
                    var facing = owner != null
                        ? GetEffectiveCablePortFacing(owner, br.Port)
                        : br.Port.FixedFacing;
                    excluded.Add(CableRenderer.StubPoint(new Point(p.X, p.Y), facing));
                    excluded.Add(CableRenderer.PortEdgePoint(
                        new Point(p.X, p.Y), facing));

                    // 분기는 시작하는 쪽(부모 선 위 앵커)에는 스텁이 없다. 그 앵커 좌표 자체가
                    // 실수로 꺾인점으로 저장되면(예: 맨 앞 변을 드래그할 때), 앵커는 항상 그 순간의
                    // 부모 선 위치에서 새로 계산되는 값이라 저장된 옛 좌표와 어긋나 두 점으로 갈라진다.
                    // 그래서 앵커 좌표도 저장 제외 목록에 넣는다.
                    var parentRoute = ComputeLegRoute(inst, br.ParentId);
                    if (parentRoute != null)
                        excluded.Add(CableRenderer.PointAlongPolyline(parentRoute, br.T));
                }
            }

            var result = new List<ESDP.Models.Point>();
            for (int i = 1; i < routePts.Count - 1; i++)
            {
                var p = routePts[i];
                // 화면상 같은 점으로 볼 수 있는 1px 이내만 제외한다.
                // 이 값이 크면 선분 핸들을 조금 옮긴 위치까지 자동 스텁으로 오인해 지워진다.
                const double excludeEps = 1.0;
                if (excluded.Any(s => Dist(s.X, s.Y, p.X, p.Y) < excludeEps)) continue;
                result.Add(new ESDP.Models.Point { X = p.X, Y = p.Y });
            }
            return result;
        }

        /// <summary>
        /// 케이블 모양이 구조적으로 바뀐 뒤에 호출한다.
        /// 모든 케이블의 쓸모없어진 꺾인점을 정리하고 다시 그린다.
        /// (드래그 "도중"에는 인덱스가 꼬일 수 있으니 쓰지 말고, 드래그가 끝났거나
        ///  장치 이동·꺾인점 삭제처럼 한 번에 끝나는 변경 뒤에 쓴다)
        /// </summary>
        private void NormalizeAllCables()
        {
            foreach (var pc in _placedCables)
                NormalizeDeviceConnectedPortApproaches(pc.Instance);
            AlignDeviceApproachCorridorLanes();
            foreach (var pc in _placedCables)
            {
                SimplifyAllLegs(pc.Instance);
            }
            RenderAllCables();
        }

        /// <summary>
        /// 장치 포트가 차지하는 외곽 한 칸 안에서는 케이블이 통로 방향으로
        /// 꺾이지 않도록, 포트 가까이에 저장된 첫/마지막 꺾임 열을 그 칸의
        /// 바깥 경계로 옮긴다. 기존 파일의 수동 경로는 나머지 부분을 그대로
        /// 유지하며, 이미 충분히 바깥에 있는 경로는 전혀 건드리지 않는다.
        /// </summary>
        private bool NormalizeDeviceConnectedPortApproaches(CableInstance inst)
        {
            bool changed = RouteTightFacingDeviceConnectionThroughPortCells(inst);

            bool TryGetEscape(CablePortLayout layout, out Point escape,
                out PortSide facing)
            {
                escape = default;
                facing = layout.FixedFacing;
                if (string.IsNullOrWhiteSpace(layout.ConnectedToPortKey) ||
                    !layout.ConnectedToPortKey.StartsWith(
                        "dev:", StringComparison.Ordinal))
                    return false;

                var resolved = CableRenderer.ResolveConnection(
                    layout, ResolveAnchor);
                if (!resolved.DeviceFacing.HasValue) return false;

                facing = resolved.DeviceFacing.Value;
                escape = GetAutoRoutePortEscape(
                    new Point(resolved.X, resolved.Y), facing);
                return true;
            }

            static double Axis(ESDP.Models.Point point, bool horizontalFacing)
                => horizontalFacing ? point.X : point.Y;

            static void SetAxis(
                ESDP.Models.Point point, bool horizontalFacing, double value)
            {
                if (horizontalFacing) point.X = value;
                else point.Y = value;
            }

            static bool IsOutside(
                double value, double escapeAxis, PortSide facing)
                => facing switch
                {
                    PortSide.Left or PortSide.Top =>
                        value <= escapeAxis + 0.01,
                    _ => value >= escapeAxis - 0.01
                };

            bool NormalizeEnd(
                CablePortLayout layout,
                List<ESDP.Models.Point> bends,
                bool atStart)
            {
                if (!TryGetEscape(layout, out var escape, out var facing))
                    return false;

                bool horizontalFacing =
                    facing is PortSide.Left or PortSide.Right;
                double escapeAxis = horizontalFacing ? escape.X : escape.Y;

                if (bends.Count == 0)
                {
                    bends.Add(new ESDP.Models.Point
                    {
                        X = escape.X,
                        Y = escape.Y
                    });
                    return true;
                }

                int index = atStart ? 0 : bends.Count - 1;
                int step = atStart ? 1 : -1;
                double originalAxis = Axis(bends[index], horizontalFacing);
                if (IsOutside(originalAxis, escapeAxis, facing)) return false;

                // 같은 X/Y 위에 연속된 포인트는 하나의 접근 차선이다.
                // 한 점만 옮기면 아주 짧은 ㄷ자 꺾임이 새로 생기므로 차선
                // 전체를 함께 외곽 경계로 맞춘다.
                while (index >= 0 && index < bends.Count &&
                       Math.Abs(Axis(bends[index], horizontalFacing) -
                                originalAxis) < 1.0)
                {
                    SetAxis(bends[index], horizontalFacing, escapeAxis);
                    index += step;
                }

                return true;
            }

            // 꺾임점이 전혀 없는 옛 경로는 양 끝 탈출점을 각각 만든다.
            // 한 점만 만든 뒤 반대 끝 보정이 같은 점을 덮어쓰지 않게 한다.
            if (inst.RootBends.Count == 0)
            {
                if (TryGetEscape(inst.PortA, out var startEscape, out _))
                {
                    inst.RootBends.Add(new ESDP.Models.Point
                    {
                        X = startEscape.X,
                        Y = startEscape.Y
                    });
                    changed = true;
                }
                if (TryGetEscape(inst.PortB, out var endEscape, out _) &&
                    (inst.RootBends.Count == 0 ||
                     Dist(inst.RootBends[^1].X, inst.RootBends[^1].Y,
                          endEscape.X, endEscape.Y) >= 0.5))
                {
                    inst.RootBends.Add(new ESDP.Models.Point
                    {
                        X = endEscape.X,
                        Y = endEscape.Y
                    });
                    changed = true;
                }
            }
            else
            {
                changed |= NormalizeEnd(inst.PortA, inst.RootBends, atStart: true);
                changed |= NormalizeEnd(inst.PortB, inst.RootBends, atStart: false);
            }

            foreach (var branch in inst.Branches)
                changed |= NormalizeEnd(
                    branch.Port, branch.Bends, atStart: false);

            return changed;
        }

        /// <summary>
        /// 서로 마주 보는 두 장치 사이에 별도의 외곽 통로 한 칸을 만들 공간이
        /// 없으면, 멀리 우회하지 않고 두 포트 셀이 맞닿은 공간을 공유 통로로
        /// 사용한다. 이 경우 예전 파일에 저장된 큰 우회점도 짧은 ㄹ자 경로로
        /// 교체한다.
        /// </summary>
        private bool RouteTightFacingDeviceConnectionThroughPortCells(
            CableInstance inst)
        {
            var start = TryGetConnectedDevicePort(inst.PortA);
            var end = TryGetConnectedDevicePort(inst.PortB);
            if (start == null || end == null || start.Value.Device == end.Value.Device)
                return false;

            var a = CableRenderer.ResolveConnection(inst.PortA, ResolveAnchor);
            var b = CableRenderer.ResolveConnection(inst.PortB, ResolveAnchor);
            var firstRect = new Rect(
                start.Value.Device.Instance.X,
                start.Value.Device.Instance.Y,
                start.Value.Device.Instance.Width,
                start.Value.Device.Instance.Height);
            var secondRect = new Rect(
                end.Value.Device.Instance.X,
                end.Value.Device.Instance.Y,
                end.Value.Device.Instance.Width,
                end.Value.Device.Instance.Height);

            double? horizontalLaneX = null;
            double? verticalLaneY = null;
            int horizontalFacingScore = 0;
            int verticalFacingScore = 0;
            // 포트 셀과 그 사이의 좁은 여유 공간만으로 곧바로 이을 수 있는
            // 거리까지는 별도 외곽 통로를 찾지 않는다. 실험 격자 기준 다섯
            // 칸이면 캡처처럼 가까운 두 장치 사이를 충분히 포함한다.
            double maximumTightGap = DiagramGridSize * 5 + 0.01;

            if (firstRect.Right <= secondRect.Left + 0.01)
            {
                double gap = secondRect.Left - firstRect.Right;
                if (gap >= DiagramGridSize - 0.01 &&
                    gap <= maximumTightGap)
                {
                    horizontalFacingScore =
                        (start.Value.Side == PortSide.Right ? 1 : 0) +
                        (end.Value.Side == PortSide.Left ? 1 : 0);
                    if (horizontalFacingScore == 2)
                        horizontalLaneX =
                            (firstRect.Right + secondRect.Left) / 2;
                }
            }
            else if (secondRect.Right <= firstRect.Left + 0.01)
            {
                double gap = firstRect.Left - secondRect.Right;
                if (gap >= DiagramGridSize - 0.01 &&
                    gap <= maximumTightGap)
                {
                    horizontalFacingScore =
                        (start.Value.Side == PortSide.Left ? 1 : 0) +
                        (end.Value.Side == PortSide.Right ? 1 : 0);
                    if (horizontalFacingScore == 2)
                        horizontalLaneX =
                            (secondRect.Right + firstRect.Left) / 2;
                }
            }

            if (firstRect.Bottom <= secondRect.Top + 0.01)
            {
                double gap = secondRect.Top - firstRect.Bottom;
                if (gap >= DiagramGridSize - 0.01 &&
                    gap <= maximumTightGap)
                {
                    verticalFacingScore =
                        (start.Value.Side == PortSide.Bottom ? 1 : 0) +
                        (end.Value.Side == PortSide.Top ? 1 : 0);
                    if (verticalFacingScore == 2)
                        verticalLaneY =
                            (firstRect.Bottom + secondRect.Top) / 2;
                }
            }
            else if (secondRect.Bottom <= firstRect.Top + 0.01)
            {
                double gap = firstRect.Top - secondRect.Bottom;
                if (gap >= DiagramGridSize - 0.01 &&
                    gap <= maximumTightGap)
                {
                    verticalFacingScore =
                        (start.Value.Side == PortSide.Top ? 1 : 0) +
                        (end.Value.Side == PortSide.Bottom ? 1 : 0);
                    if (verticalFacingScore == 2)
                        verticalLaneY =
                            (secondRect.Bottom + firstRect.Top) / 2;
                }
            }

            // 대각선으로 어긋난 장치는 가로·세로 간격이 모두 존재할 수 있다.
            // 두 후보가 같으면 가로로 마주 보는 포트의 공간을 우선해, 캡처의
            // 빨간 선처럼 두 장치 사이 정중앙에 세로 차선을 만든다.
            bool useHorizontalLane = horizontalLaneX.HasValue &&
                                     (!verticalLaneY.HasValue ||
                                      horizontalFacingScore >= verticalFacingScore);
            if (!useHorizontalLane && !verticalLaneY.HasValue) return false;

            var replacement = new List<ESDP.Models.Point>();
            void Add(double x, double y)
            {
                if (replacement.Count > 0 &&
                    Dist(replacement[^1].X, replacement[^1].Y, x, y) < 0.5)
                    return;
                replacement.Add(new ESDP.Models.Point { X = x, Y = y });
            }

            if (useHorizontalLane)
            {
                Add(horizontalLaneX!.Value, a.Y);
                Add(horizontalLaneX.Value, b.Y);
            }
            else
            {
                Add(a.X, verticalLaneY!.Value);
                Add(b.X, verticalLaneY.Value);
            }

            var proposedRoute = new List<Point> { new(a.X, a.Y) };
            proposedRoute.AddRange(replacement.Select(point =>
                new Point(point.X, point.Y)));
            proposedRoute.Add(new Point(b.X, b.Y));

            static Point TrimEndpoint(Point from, Point toward, double amount)
            {
                double dx = toward.X - from.X;
                double dy = toward.Y - from.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length <= amount + 0.01) return from;
                return new Point(
                    from.X + dx / length * amount,
                    from.Y + dy / length * amount);
            }

            bool crossesPortLabel = false;
            for (int i = 0; i < proposedRoute.Count - 1 && !crossesPortLabel; i++)
            {
                Point lineStart = proposedRoute[i];
                Point lineEnd = proposedRoute[i + 1];
                // 포트 마크 바로 옆 6px는 정상 접속 영역이다. 그 이후의
                // 선이 이름표를 가로지를 때만 중앙 직결 경로를 포기한다.
                if (i == 0)
                    lineStart = TrimEndpoint(lineStart, lineEnd, 6);
                if (i == proposedRoute.Count - 2)
                    lineEnd = TrimEndpoint(lineEnd, lineStart, 6);
                var lineBox = new Rect(
                    Math.Min(lineStart.X, lineEnd.X) - 1,
                    Math.Min(lineStart.Y, lineEnd.Y) - 1,
                    Math.Max(2, Math.Abs(lineStart.X - lineEnd.X) + 2),
                    Math.Max(2, Math.Abs(lineStart.Y - lineEnd.Y) + 2));

                foreach (var device in _placed)
                foreach (var label in device.Container.Children
                             .OfType<TextBlock>()
                             .Where(text => text.Tag is
                                 DevicePreviewRenderer.PortLabelVisualInfo))
                {
                    label.Measure(new Size(
                        double.PositiveInfinity,
                        double.PositiveInfinity));
                    var labelBox = new Rect(
                        device.Instance.X + Canvas.GetLeft(label),
                        device.Instance.Y + Canvas.GetTop(label),
                        Math.Max(1, label.DesiredSize.Width),
                        Math.Max(1, label.DesiredSize.Height));
                    if (!HasPositiveAreaOverlap(lineBox, labelBox)) continue;
                    crossesPortLabel = true;
                    break;
                }
            }

            // 중앙 직결선이 포트명을 관통한다면 포트명을 멀리 밀지 않고,
            // 앞서 계산한 장애물 회피 경로(보통 한 격자 우회)를 유지한다.
            if (crossesPortLabel) return false;

            bool same = inst.RootBends.Count == replacement.Count &&
                        inst.RootBends.Zip(replacement).All(pair =>
                            Dist(pair.First.X, pair.First.Y,
                                 pair.Second.X, pair.Second.Y) < 0.5);
            if (same) return false;

            inst.RootBends.Clear();
            inst.RootBends.AddRange(replacement);
            return true;
        }

        /// <summary>
        /// 장치의 포트 셀 바로 바깥 한 칸을 하나의 통로로 보고, 같은 통로를
        /// 사용하는 케이블 접근 차선을 통로 중앙에 동일 간격으로 배치한다.
        /// 장치 사이가 두 칸보다 좁으면 별도 외곽 통로를 억지로 만들지 않고
        /// 실제 장치 사이 공간 자체를 공유 통로로 사용한다.
        /// </summary>
        private void AlignDeviceApproachCorridorLanes(
            ISet<string>? fixedCableIds = null)
        {
            var candidates = new List<(
                string GroupKey,
                string LegKey,
                List<ESDP.Models.Point> Bends,
                bool AtStart,
                bool VerticalLane,
                double Min,
                double Max,
                double CurrentAxis)>();

            bool TryGetCorridor(
                CablePortLayout layout,
                out bool verticalLane,
                out double min,
                out double max)
            {
                verticalLane = false;
                min = max = 0;
                var parts = layout.ConnectedToPortKey?.Split(':');
                if (parts is not { Length: 3 } || parts[0] != "dev")
                    return false;

                var owner = _placed.FirstOrDefault(
                    item => item.Instance.Id == parts[1]);
                if (owner == null) return false;
                var anchor = ResolveAnchor(layout.ConnectedToPortKey!);
                if (anchor == null) return false;

                var own = new Rect(
                    owner.Instance.X,
                    owner.Instance.Y,
                    owner.Instance.Width,
                    owner.Instance.Height);
                double grid = DiagramGridSize;

                static bool Contains(double value, double first, double second)
                    => value >= Math.Min(first, second) - 0.01 &&
                       value <= Math.Max(first, second) + 0.01;

                switch (anchor.Facing)
                {
                    case PortSide.Left:
                    {
                        verticalLane = true;
                        double? neighborRight = _placed
                            .Where(item => item != owner)
                            .Select(item => new Rect(
                                item.Instance.X, item.Instance.Y,
                                item.Instance.Width, item.Instance.Height))
                            .Where(rect => rect.Right <= own.Left + 0.01 &&
                                           Contains(anchor.Y, rect.Top, rect.Bottom))
                            .Select(rect => (double?)rect.Right)
                            .OrderByDescending(value => value)
                            .FirstOrDefault();
                        double gap = neighborRight.HasValue
                            ? own.Left - neighborRight.Value
                            : double.PositiveInfinity;
                        if (neighborRight.HasValue && gap < grid * 3 - 0.01)
                        {
                            min = neighborRight.Value;
                            max = own.Left;
                        }
                        else
                        {
                            min = own.Left - grid * 3;
                            max = own.Left - grid * 2;
                        }
                        break;
                    }
                    case PortSide.Right:
                    {
                        verticalLane = true;
                        double? neighborLeft = _placed
                            .Where(item => item != owner)
                            .Select(item => new Rect(
                                item.Instance.X, item.Instance.Y,
                                item.Instance.Width, item.Instance.Height))
                            .Where(rect => rect.Left >= own.Right - 0.01 &&
                                           Contains(anchor.Y, rect.Top, rect.Bottom))
                            .Select(rect => (double?)rect.Left)
                            .OrderBy(value => value)
                            .FirstOrDefault();
                        double gap = neighborLeft.HasValue
                            ? neighborLeft.Value - own.Right
                            : double.PositiveInfinity;
                        if (neighborLeft.HasValue && gap < grid * 3 - 0.01)
                        {
                            min = own.Right;
                            max = neighborLeft.Value;
                        }
                        else
                        {
                            min = own.Right + grid * 2;
                            max = own.Right + grid * 3;
                        }
                        break;
                    }
                    case PortSide.Top:
                    {
                        verticalLane = false;
                        double? neighborBottom = _placed
                            .Where(item => item != owner)
                            .Select(item => new Rect(
                                item.Instance.X, item.Instance.Y,
                                item.Instance.Width, item.Instance.Height))
                            .Where(rect => rect.Bottom <= own.Top + 0.01 &&
                                           Contains(anchor.X, rect.Left, rect.Right))
                            .Select(rect => (double?)rect.Bottom)
                            .OrderByDescending(value => value)
                            .FirstOrDefault();
                        double gap = neighborBottom.HasValue
                            ? own.Top - neighborBottom.Value
                            : double.PositiveInfinity;
                        if (neighborBottom.HasValue && gap < grid * 3 - 0.01)
                        {
                            min = neighborBottom.Value;
                            max = own.Top;
                        }
                        else
                        {
                            min = own.Top - grid * 3;
                            max = own.Top - grid * 2;
                        }
                        break;
                    }
                    default:
                    {
                        verticalLane = false;
                        double? neighborTop = _placed
                            .Where(item => item != owner)
                            .Select(item => new Rect(
                                item.Instance.X, item.Instance.Y,
                                item.Instance.Width, item.Instance.Height))
                            .Where(rect => rect.Top >= own.Bottom - 0.01 &&
                                           Contains(anchor.X, rect.Left, rect.Right))
                            .Select(rect => (double?)rect.Top)
                            .OrderBy(value => value)
                            .FirstOrDefault();
                        double gap = neighborTop.HasValue
                            ? neighborTop.Value - own.Bottom
                            : double.PositiveInfinity;
                        if (neighborTop.HasValue && gap < grid * 3 - 0.01)
                        {
                            min = own.Bottom;
                            max = neighborTop.Value;
                        }
                        else
                        {
                            min = own.Bottom + grid * 2;
                            max = own.Bottom + grid * 3;
                        }
                        break;
                    }
                }

                return max - min >= 2.0;
            }

            void AddCandidate(
                CableInstance instance,
                string legId,
                CablePortLayout layout,
                List<ESDP.Models.Point> bends,
                bool atStart)
            {
                if (bends.Count == 0 ||
                    !TryGetCorridor(
                        layout, out bool verticalLane,
                        out double min, out double max))
                    return;

                int index = atStart ? 0 : bends.Count - 1;
                double axis = verticalLane
                    ? bends[index].X
                    : bends[index].Y;

                // 사용자가 통로와 전혀 다른 곳에 둔 수동 경로는 보존한다.
                // 이전 버전의 포트 셀 경계도 받아들일 수 있도록 반 칸의
                // 호환 여유만 둔다.
                double tolerance = DiagramGridSize / 2;
                if (axis < min - tolerance || axis > max + tolerance)
                    return;

                string groupKey = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{(verticalLane ? 'V' : 'H')}:{min:F3}:{max:F3}");
                candidates.Add((
                    groupKey,
                    $"{instance.Id}:{legId}",
                    bends,
                    atStart,
                    verticalLane,
                    min,
                    max,
                    axis));
            }

            foreach (var placed in _placedCables)
            {
                var inst = placed.Instance;
                if (fixedCableIds?.Contains(inst.Id) == true) continue;
                AddCandidate(
                    inst, "root", inst.PortA,
                    inst.RootBends, atStart: true);
                AddCandidate(
                    inst, "root", inst.PortB,
                    inst.RootBends, atStart: false);
                foreach (var branch in inst.Branches)
                    AddCandidate(
                        inst, branch.Id, branch.Port,
                        branch.Bends, atStart: false);
            }

            static void MoveTerminalRun(
                List<ESDP.Models.Point> bends,
                bool atStart,
                bool verticalLane,
                double targetAxis)
            {
                if (bends.Count == 0) return;
                int index = atStart ? 0 : bends.Count - 1;
                int step = atStart ? 1 : -1;
                double original = verticalLane
                    ? bends[index].X
                    : bends[index].Y;
                while (index >= 0 && index < bends.Count)
                {
                    double value = verticalLane
                        ? bends[index].X
                        : bends[index].Y;
                    if (Math.Abs(value - original) >= 1.0) break;
                    if (verticalLane) bends[index].X = targetAxis;
                    else bends[index].Y = targetAxis;
                    index += step;
                }
            }

            foreach (var corridor in candidates.GroupBy(item => item.GroupKey))
            {
                // 같은 케이블 다리의 양 끝이 동일한 좁은 통로를 공유하면
                // 두 후보가 아니라 하나의 차선으로 센다.
                var lanes = corridor
                    .GroupBy(item => item.LegKey)
                    .Select(group => new
                    {
                        Endpoints = group.ToList(),
                        CurrentAxis = group.Average(item => item.CurrentAxis),
                        LegKey = group.Key
                    })
                    .OrderBy(item => item.CurrentAxis)
                    .ThenBy(item => item.LegKey, StringComparer.Ordinal)
                    .ToList();
                if (lanes.Count == 0) continue;

                double min = corridor.First().Min;
                double max = corridor.First().Max;
                var gridLanes = new List<double>();
                double firstGridLane = Math.Floor(
                    (min - DiagramGridSize / 2) / DiagramGridSize) *
                    DiagramGridSize + DiagramGridSize / 2;
                for (double axis = firstGridLane;
                     axis <= max + 0.01;
                     axis += DiagramGridSize)
                {
                    if (axis > min + 0.01 && axis < max - 0.01)
                        gridLanes.Add(axis);
                }

                var targets = new List<double>();
                if (gridLanes.Count >= lanes.Count)
                {
                    // 정상 통로에서는 격자 중앙선 하나당 케이블 하나만 둔다.
                    // 현재 순서를 유지하면서 가장 가까운 비어 있는 중앙선을
                    // 골라 불필요한 차선 교환과 이동을 줄인다.
                    int previousIndex = -1;
                    for (int laneIndex = 0; laneIndex < lanes.Count; laneIndex++)
                    {
                        int remaining = lanes.Count - laneIndex - 1;
                        int maximumIndex = gridLanes.Count - remaining - 1;
                        int bestIndex = Enumerable.Range(
                                previousIndex + 1,
                                maximumIndex - previousIndex)
                            .OrderBy(index => Math.Abs(
                                gridLanes[index] - lanes[laneIndex].CurrentAxis))
                            .ThenBy(index => index)
                            .First();
                        targets.Add(gridLanes[bestIndex]);
                        previousIndex = bestIndex;
                    }
                }
                else
                {
                    // 통로 폭보다 케이블 수가 많을 때만 예외적으로 실제 폭을
                    // 균등 분할한다. 이 구간만 격자 중앙선 제약을 벗어난다.
                    double gap = (max - min) / (lanes.Count + 1);
                    for (int laneIndex = 0; laneIndex < lanes.Count; laneIndex++)
                        targets.Add(min + gap * (laneIndex + 1));
                }

                for (int laneIndex = 0; laneIndex < lanes.Count; laneIndex++)
                {
                    double target = targets[laneIndex];
                    foreach (var endpoint in lanes[laneIndex].Endpoints)
                        MoveTerminalRun(
                            endpoint.Bends,
                            endpoint.AtStart,
                            endpoint.VerticalLane,
                            target);
                }
            }

            // 일반 통로의 등간격 배치가 끝난 뒤, 가까운 두 장치 사이를 직접
            // 잇는 케이블은 반드시 두 본체 사이의 정중앙 차선으로 되돌린다.
            // 이 최종 우선순위가 없으면 한쪽 포트의 외곽 통로 계산이 중앙선을
            // 다시 장치 쪽으로 밀어 캡처의 검은 선 위치가 된다.
            foreach (var placed in _placedCables)
            {
                if (fixedCableIds?.Contains(placed.Instance.Id) == true)
                    continue;
                if (RouteTightFacingDeviceConnectionThroughPortCells(
                        placed.Instance))
                    NormalizeDeviceConnectedPortApproaches(placed.Instance);
            }
        }

        private const double AutoRouteLaneGap = 8.0;
        // 실제 포트가 놓인 바깥 격자 셀 하나만 포트 결합 전용 영역으로 쓴다.
        // 포트가 없는 장치 둘레 셀은 케이블이 통과할 수 있다.
        private const double AutoRouteDeviceClearance = DiagramGridSize;

        private void CloseAutoArrangeExclusionPanel_Click(
            object sender, RoutedEventArgs e)
        {
            EndAutoArrangeExclusionMode();
            AutoArrangeExclusionPanel.Visibility = Visibility.Collapsed;
        }

        private void BeginAutoArrangeExclusionMode_Click(
            object sender, RoutedEventArgs e)
        {
            _isCableArrangeExclusionMode = true;
            _selectedCable = null;
            BeginAutoArrangeExclusionButton.Content = "케이블을 선택하세요";
            RenderAllCables();
            StatusText.Text =
                "전체 정렬에서 제외할 케이블을 누르세요. 다시 누르면 해제됩니다.";
        }

        private void CompleteAutoArrangeExclusionMode_Click(
            object sender, RoutedEventArgs e)
        {
            EndAutoArrangeExclusionMode();
            StatusText.Text = "전체 케이블 정렬 제외 목록을 설정했습니다.";
        }

        private void EndAutoArrangeExclusionMode()
        {
            if (!_isCableArrangeExclusionMode)
            {
                RefreshAutoArrangeExclusionList();
                return;
            }
            _isCableArrangeExclusionMode = false;
            BeginAutoArrangeExclusionButton.Content = "제외 케이블 설정";
            RenderAllCables();
            RefreshAutoArrangeExclusionList();
        }

        private void ToggleAutoArrangeExclusion(PlacedCable placed)
        {
            placed.Instance.ExcludeFromAutoArrange =
                !placed.Instance.ExcludeFromAutoArrange;
            RefreshAutoArrangeExclusionList();
            RenderAllCables();
            _isDirty = true;
            StatusText.Text = placed.Instance.ExcludeFromAutoArrange
                ? $"'{placed.EffectiveCable.Name}'을(를) 전체 정렬에서 제외했습니다."
                : $"'{placed.EffectiveCable.Name}'을(를) 전체 정렬에 다시 포함했습니다.";
        }

        private void RefreshAutoArrangeExclusionList()
        {
            if (AutoArrangeExclusionList == null) return;
            AutoArrangeExclusionList.ItemsSource = _placedCables
                .OrderBy(item => item.EffectiveCable.Name,
                    StringComparer.CurrentCultureIgnoreCase)
                .Select(item => new CableArrangeExclusionListItem
                {
                    Name = item.EffectiveCable.Name,
                    StateText = item.Instance.ExcludeFromAutoArrange
                        ? "제외" : "포함",
                    StateBrush = item.Instance.ExcludeFromAutoArrange
                        ? Brushes.Red : Brushes.SlateGray
                })
                .ToList();
        }

        private void TidySelectedCable_Click(object sender, RoutedEventArgs e)
        {
            // 이전 개별 정리 진입점은 통합 명령과 같은 동작을 하게 둔다.
            Menu_AutoArrangeAllCables(sender, e);
        }

        /// <summary>
        /// 한 케이블의 모든 포트 좌표는 그대로 두고 루트와 분기를 새로 계산한다.
        /// 다른 케이블은 움직이지 않으며, 이미 그려진 선분을 소프트 장애물과 레인 기준으로 사용한다.
        /// </summary>
        private void ArrangeSingleCableRoutes(PlacedCable placed)
        {
            var occupied = _placedCables
                .Where(item => item != placed)
                .SelectMany(item => item.RenderedSegments.Select(segment =>
                    (item.Instance.Id, segment.A, segment.B)))
                .ToList();

            foreach (var layout in AllPortLayouts(placed.Instance))
                layout.FixedFacing = GetEffectiveCablePortFacing(placed, layout);

            ArrangeCableRoot(placed, occupied);
            if (CenterRootRunInEndpointDeviceCorridor(placed))
                RefreshOccupiedRootSegments(occupied, placed.Instance);
            AutoArrangeBranches(
                placed.Instance, "root", occupied,
                prioritizeBends: true);
            NormalizeDeviceConnectedPortApproaches(placed.Instance);
            SimplifyAllLegs(placed.Instance);
            // 새 케이블이 기존 통로에 합류하거나 빠져나가면 그 통로의
            // 접근 차선만 다시 등간격으로 나눈다. 다른 구간의 수동 경로는
            // 그대로 유지된다.
            AlignDeviceApproachCorridorLanes();
        }

        private void ArrangeCableRoot(
            PlacedCable placed,
            List<(string CableInstanceId, Point A, Point B)> occupied,
            bool prioritizeBends = true)
        {
            var inst = placed.Instance;
            inst.RootBends.Clear();

            var a = CableRenderer.ResolveConnection(inst.PortA, ResolveAnchor);
            var b = CableRenderer.ResolveConnection(inst.PortB, ResolveAnchor);
            var portA = new Point(a.X, a.Y);
            var portB = new Point(b.X, b.Y);
            var facingA = GetEffectiveCablePortFacing(placed, inst.PortA);
            var facingB = GetEffectiveCablePortFacing(placed, inst.PortB);
            var escapeA = GetPreferredAutoRoutePortEscape(
                inst, "root", portA, facingA);
            var escapeB = GetPreferredAutoRoutePortEscape(
                inst, "root", portB, facingB);
            var core = CableRenderer.FindBalancedOrthogonalRoute(
                escapeA,
                escapeB,
                GetCableObstacles(inst, "root", AutoRouteDeviceClearance),
                occupied.Select(segment => (segment.A, segment.B)).ToList(),
                laneGap: AutoRouteLaneGap,
                prioritizeBends: prioritizeBends,
                requiredStartDirection: facingA,
                requiredEndDirection: facingB);
            var route = new List<Point> { portA };
            route.AddRange(core);
            route.Add(portB);
            route = CableRenderer.CollapseNearDuplicates(
                route, hasStartStub: true, hasEndStub: true);

            inst.RootBends.AddRange(MaterializeBends(inst, "root", route));
            var savedRoute = ComputeLegRoute(inst, "root");
            if (savedRoute != null)
                AddOccupiedSegments(occupied, inst.Id, savedRoute);
        }

        /// <summary>
        /// 모든 케이블의 선, 꺾임점, 루트와 분기 구조는 그대로 보존하고
        /// 케이블명·케이블 포트명·장치 포트명만 가까운 빈 위치로 정리한다.
        /// </summary>
        private void Menu_AutoArrangeAllCables(object sender, RoutedEventArgs e)
        {
            bool hasDevicePortLabels = _placed.Any(device =>
                device.EffectiveDevice.Ports.Any(port => port.ShowLabel));
            if (_placedCables.Count == 0 && !hasDevicePortLabels)
            {
                StatusText.Text = "정리할 케이블명이나 포트명이 없습니다.";
                return;
            }

            EndAutoArrangeExclusionMode();
            // 현재 모델의 경로를 먼저 그대로 그려 라벨 충돌 판정의 기준으로 쓴다.
            // 이 명령에서는 RootBends, Branches, T, 포트 좌표를 변경하지 않는다.
            RenderAllCables();

            bool movedDevicePortLabel = TidyDevicePortLabels();
            if (movedDevicePortLabel)
                RenderAllCables();

            bool movedCableLabel = false;
            var labelObstacles = BuildCableLabelIndex();
            foreach (var placed in _placedCables)
                movedCableLabel |= TidyCableLabels(placed, labelObstacles);
            if (movedCableLabel)
                RenderAllCables();

            UpdateCanvasExtent();
            if (movedDevicePortLabel || movedCableLabel)
                _isDirty = true;
            StatusText.Text = movedDevicePortLabel || movedCableLabel
                ? "케이블명과 포트명을 겹치지 않는 가까운 위치로 정리했습니다."
                : "모든 케이블명과 포트명이 이미 보기 좋은 위치에 있습니다.";
        }

        private void RefreshOccupiedRootSegments(
            List<(string CableInstanceId, Point A, Point B)> occupied,
            CableInstance inst)
        {
            occupied.RemoveAll(segment =>
                segment.CableInstanceId == inst.Id);
            var route = ComputeLegRoute(inst, "root");
            if (route != null)
                AddOccupiedSegments(occupied, inst.Id, route);
        }

        private bool CenterRootRunInEndpointDeviceCorridor(
            PlacedCable placed)
        {
            var inst = placed.Instance;
            if (inst.RootBends.Count < 2) return false;

            var start = TryGetConnectedDevicePort(inst.PortA);
            var end = TryGetConnectedDevicePort(inst.PortB);
            if (start == null || end == null ||
                start.Value.Device == end.Value.Device)
                return false;

            var first = new Rect(
                start.Value.Device.Instance.X,
                start.Value.Device.Instance.Y,
                start.Value.Device.Instance.Width,
                start.Value.Device.Instance.Height);
            var second = new Rect(
                end.Value.Device.Instance.X,
                end.Value.Device.Instance.Y,
                end.Value.Device.Instance.Width,
                end.Value.Device.Instance.Height);

            var candidates = new List<(
                int Index, bool Horizontal, double Center, double Length)>();
            double? horizontalCenter = first.Bottom <= second.Top + 0.01
                ? (first.Bottom + second.Top) / 2
                : second.Bottom <= first.Top + 0.01
                    ? (second.Bottom + first.Top) / 2
                    : null;
            double? verticalCenter = first.Right <= second.Left + 0.01
                ? (first.Right + second.Left) / 2
                : second.Right <= first.Left + 0.01
                    ? (second.Right + first.Left) / 2
                    : null;

            for (int index = 0; index < inst.RootBends.Count - 1; index++)
            {
                var a = inst.RootBends[index];
                var b = inst.RootBends[index + 1];
                if (horizontalCenter.HasValue &&
                    Math.Abs(a.Y - b.Y) < 0.01)
                {
                    candidates.Add((index, true, horizontalCenter.Value,
                        Math.Abs(a.X - b.X)));
                }
                if (verticalCenter.HasValue &&
                    Math.Abs(a.X - b.X) < 0.01)
                {
                    candidates.Add((index, false, verticalCenter.Value,
                        Math.Abs(a.Y - b.Y)));
                }
            }
            if (candidates.Count == 0) return false;

            var original = inst.RootBends.Select(point =>
                new ESDP.Models.Point { X = point.X, Y = point.Y }).ToList();

            static bool CrossesInterior(Point a, Point b, Rect rect)
            {
                if (Math.Abs(a.Y - b.Y) < 0.01)
                {
                    if (a.Y <= rect.Top || a.Y >= rect.Bottom) return false;
                    return Math.Max(a.X, b.X) > rect.Left + 0.01 &&
                           Math.Min(a.X, b.X) < rect.Right - 0.01;
                }
                if (Math.Abs(a.X - b.X) < 0.01)
                {
                    if (a.X <= rect.Left || a.X >= rect.Right) return false;
                    return Math.Max(a.Y, b.Y) > rect.Top + 0.01 &&
                           Math.Min(a.Y, b.Y) < rect.Bottom - 0.01;
                }
                return false;
            }

            foreach (var candidate in candidates
                         .OrderByDescending(item => item.Length))
            {
                for (int index = 0; index < original.Count; index++)
                {
                    inst.RootBends[index].X = original[index].X;
                    inst.RootBends[index].Y = original[index].Y;
                }

                if (candidate.Horizontal)
                {
                    inst.RootBends[candidate.Index].Y = candidate.Center;
                    inst.RootBends[candidate.Index + 1].Y = candidate.Center;
                }
                else
                {
                    inst.RootBends[candidate.Index].X = candidate.Center;
                    inst.RootBends[candidate.Index + 1].X = candidate.Center;
                }

                var route = ComputeLegRoute(
                    inst, "root", avoidObstacles: false);
                var obstacles = GetCableObstacles(
                    inst, "root", AutoRouteDeviceClearance);
                bool blocked = route == null ||
                    Enumerable.Range(0, Math.Max(0, route.Count - 1))
                        .Any(index => obstacles.Any(rect =>
                            CrossesInterior(
                                route[index], route[index + 1], rect)));
                if (!blocked) return true;
            }

            for (int index = 0; index < original.Count; index++)
            {
                inst.RootBends[index].X = original[index].X;
                inst.RootBends[index].Y = original[index].Y;
            }
            return false;
        }

        private static double DistanceToOrthogonalPolyline(
            Point point, IReadOnlyList<Point> route)
        {
            double best = double.MaxValue;
            for (int index = 0; index < route.Count - 1; index++)
            {
                var a = route[index];
                var b = route[index + 1];
                double x = Math.Max(Math.Min(a.X, b.X),
                    Math.Min(Math.Max(a.X, b.X), point.X));
                double y = Math.Max(Math.Min(a.Y, b.Y),
                    Math.Min(Math.Max(a.Y, b.Y), point.Y));
                best = Math.Min(best,
                    Math.Abs(point.X - x) + Math.Abs(point.Y - y));
            }
            return best;
        }

        private bool ArrangeOneBranchRoute(
            CableInstance inst,
            CableBranch branch,
            List<(string CableInstanceId, Point A, Point B)> occupied,
            bool prioritizeBends)
        {
            branch.Bends.Clear();
            var parentRoute = ComputeLegRoute(inst, branch.ParentId);
            if (parentRoute == null || parentRoute.Count < 2) return false;

            var resolved = CableRenderer.ResolveConnection(branch.Port, ResolveAnchor);
            var port = new Point(resolved.X, resolved.Y);
            var owner = _placedCables.FirstOrDefault(item => item.Instance == inst);
            var facing = owner != null
                ? GetEffectiveCablePortFacing(owner, branch.Port)
                : branch.Port.FixedFacing;
            var escape = GetPreferredAutoRoutePortEscape(
                inst, branch.Id, port, facing);
            var arranged = FindBestBranchAttachmentAndRoute(
                inst, branch, parentRoute, escape, occupied, prioritizeBends);
            if (arranged == null) return false;

            branch.T = arranged.Value.T;
            var route = new List<Point>(arranged.Value.CoreRoute) { port };
            route = CableRenderer.CollapseNearDuplicates(
                route, hasStartStub: false, hasEndStub: true);
            branch.Bends.AddRange(MaterializeBends(inst, branch.Id, route));
            var savedRoute = ComputeLegRoute(inst, branch.Id);
            if (savedRoute != null)
                AddOccupiedSegments(occupied, inst.Id, savedRoute);
            return true;
        }

        private HashSet<PlacedCable> FindOverlappingCables()
        {
            var result = new HashSet<PlacedCable>();
            for (int i = 0; i < _placedCables.Count; i++)
            for (int j = i + 1; j < _placedCables.Count; j++)
            {
                var first = _placedCables[i];
                var second = _placedCables[j];
                bool overlaps = first.RenderedSegments.Any(a =>
                    second.RenderedSegments.Any(b =>
                        ParallelSegmentsOverlap(a.A, a.B, b.A, b.B)));
                if (!overlaps) continue;
                result.Add(first);
                result.Add(second);
            }
            return result;
        }

        private static bool ParallelSegmentsOverlap(
            Point a1, Point a2, Point b1, Point b2)
        {
            const double axisTolerance = 3.0;
            const double minimumSharedLength = 10.0;
            bool aHorizontal = Math.Abs(a1.Y - a2.Y) < 0.5;
            bool bHorizontal = Math.Abs(b1.Y - b2.Y) < 0.5;
            bool aVertical = Math.Abs(a1.X - a2.X) < 0.5;
            bool bVertical = Math.Abs(b1.X - b2.X) < 0.5;

            if (aHorizontal && bHorizontal &&
                Math.Abs(a1.Y - b1.Y) <= axisTolerance)
            {
                double overlap = Math.Min(Math.Max(a1.X, a2.X), Math.Max(b1.X, b2.X)) -
                                 Math.Max(Math.Min(a1.X, a2.X), Math.Min(b1.X, b2.X));
                return overlap >= minimumSharedLength;
            }
            if (aVertical && bVertical &&
                Math.Abs(a1.X - b1.X) <= axisTolerance)
            {
                double overlap = Math.Min(Math.Max(a1.Y, a2.Y), Math.Max(b1.Y, b2.Y)) -
                                 Math.Max(Math.Min(a1.Y, a2.Y), Math.Min(b1.Y, b2.Y));
                return overlap >= minimumSharedLength;
            }
            return false;
        }

        private HashSet<PlacedCable> SnapNearlyAlignedConnectedDevicePorts()
        {
            const double snapTolerance = 7.0;
            var changedCables = new HashSet<PlacedCable>();

            foreach (var placed in _placedCables)
            {
                var first = TryGetConnectedDevicePort(placed.Instance.PortA);
                var second = TryGetConnectedDevicePort(placed.Instance.PortB);
                if (first == null || second == null) continue;

                var anchorA = ResolveAnchor(placed.Instance.PortA.ConnectedToPortKey!);
                var anchorB = ResolveAnchor(placed.Instance.PortB.ConnectedToPortKey!);
                if (anchorA == null || anchorB == null) continue;

                bool horizontal = anchorA.Facing is PortSide.Left or PortSide.Right &&
                                  anchorB.Facing is PortSide.Left or PortSide.Right;
                bool vertical = anchorA.Facing is PortSide.Top or PortSide.Bottom &&
                                anchorB.Facing is PortSide.Top or PortSide.Bottom;

                if (horizontal)
                {
                    double difference = Math.Abs(anchorA.Y - anchorB.Y);
                    if (difference < 0.5 || difference > snapTolerance) continue;
                    double targetY = (anchorA.Y + anchorB.Y) / 2;
                    SetDevicePortAxisPosition(first.Value, targetY);
                    SetDevicePortAxisPosition(second.Value, targetY);
                    changedCables.Add(placed);
                }
                else if (vertical)
                {
                    double difference = Math.Abs(anchorA.X - anchorB.X);
                    if (difference < 0.5 || difference > snapTolerance) continue;
                    double targetX = (anchorA.X + anchorB.X) / 2;
                    SetDevicePortAxisPosition(first.Value, targetX);
                    SetDevicePortAxisPosition(second.Value, targetX);
                    changedCables.Add(placed);
                }
            }

            foreach (var device in changedCables
                         .SelectMany(cable => new[]
                         {
                             TryGetConnectedDevicePort(cable.Instance.PortA)?.Device,
                             TryGetConnectedDevicePort(cable.Instance.PortB)?.Device
                         })
                         .Where(device => device != null)
                         .Distinct()!)
                RenderDevice(device!);
            return changedCables;
        }

        private (PlacedDevice Device, string PortId, PortSide Side)?
            TryGetConnectedDevicePort(CablePortLayout layout)
        {
            string? key = layout.ConnectedToPortKey;
            if (string.IsNullOrWhiteSpace(key)) return null;
            var parts = key.Split(':');
            if (parts.Length != 3 || parts[0] != "dev") return null;
            var device = _placed.FirstOrDefault(item => item.Instance.Id == parts[1]);
            var port = device?.EffectiveDevice.Ports.FirstOrDefault(item => item.Id == parts[2]);
            return device == null || port == null
                ? null
                : (device, port.Id, port.Side);
        }

        private void SetDevicePortAxisPosition(
            (PlacedDevice Device, string PortId, PortSide Side) target,
            double absoluteAxis)
        {
            var editable = EnsureEditableDevice(target.Device);
            var port = editable.Ports.First(item => item.Id == target.PortId);
            double fraction = target.Side is PortSide.Left or PortSide.Right
                ? (absoluteAxis - target.Device.Instance.Y) / target.Device.Instance.Height
                : (absoluteAxis - target.Device.Instance.X) / target.Device.Instance.Width;
            port.Position = Math.Clamp(fraction, 0.05, 0.95);
        }

        private double EstimateRootCableSpan(PlacedCable placed)
        {
            var a = CableRenderer.ResolveConnection(placed.Instance.PortA, ResolveAnchor);
            var b = CableRenderer.ResolveConnection(placed.Instance.PortB, ResolveAnchor);
            return Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
        }

        private void AutoArrangeBranches(
            CableInstance inst,
            string parentId,
            List<(string CableInstanceId, Point A, Point B)> occupied,
            bool prioritizeBends = false)
        {
            // 부모 경로를 먼저 단순화해야 실제로 살아남은 꺾임점만
            // 분기점 스냅 후보로 사용할 수 있다.
            SimplifyBends(inst, parentId);

            var children = inst.Branches
                .Where(branch => branch.ParentId == parentId)
                .OrderBy(branch => branch.T)
                .ThenBy(branch => branch.Id, StringComparer.Ordinal)
                .ToList();

            foreach (var branch in children)
            {
                branch.Bends.Clear();
                var parentRoute = ComputeLegRoute(inst, branch.ParentId);
                if (parentRoute != null && parentRoute.Count >= 2)
                {
                    var resolvedPort =
                        CableRenderer.ResolveConnection(branch.Port, ResolveAnchor);
                    var port = new Point(resolvedPort.X, resolvedPort.Y);
                    var owner = _placedCables.FirstOrDefault(
                        item => item.Instance == inst);
                    var portFacing = owner != null
                        ? GetEffectiveCablePortFacing(owner, branch.Port)
                        : branch.Port.FixedFacing;
                    var portEscape = GetPreferredAutoRoutePortEscape(
                        inst, branch.Id, port, portFacing);
                    var arranged = FindBestBranchAttachmentAndRoute(
                        inst, branch, parentRoute, portEscape, occupied,
                        prioritizeBends);
                    if (arranged != null)
                    {
                        branch.T = arranged.Value.T;
                        var route = new List<Point>(arranged.Value.CoreRoute) { port };
                        route = CableRenderer.CollapseNearDuplicates(
                            route, hasStartStub: false, hasEndStub: true);

                        branch.Bends.Clear();
                        branch.Bends.AddRange(
                            MaterializeBends(inst, branch.Id, route));

                        var savedRoute = ComputeLegRoute(inst, branch.Id);
                        if (savedRoute != null)
                            AddOccupiedSegments(occupied, inst.Id, savedRoute);
                    }
                }

                AutoArrangeBranches(
                    inst, branch.Id, occupied, prioritizeBends);
            }
        }

        private (
            double T,
            List<Point> CoreRoute)? FindBestBranchAttachmentAndRoute(
            CableInstance inst,
            CableBranch branch,
            IReadOnlyList<Point> parentRoute,
            Point portStub,
            IReadOnlyList<(string CableInstanceId, Point A, Point B)> occupied,
            bool prioritizeBends)
        {
            const double preferredCornerDistance = 16.0;
            const double preferredFirstLegLength = 20.0;

            double totalLength = 0;
            for (int i = 0; i < parentRoute.Count - 1; i++)
                totalLength += Dist(
                    parentRoute[i].X, parentRoute[i].Y,
                    parentRoute[i + 1].X, parentRoute[i + 1].Y);
            if (totalLength < 0.01) return null;

            var obstacles = GetCableObstacles(
                inst, branch.Id, AutoRouteDeviceClearance);
            var occupiedSegments = occupied
                .Select(segment => (segment.A, segment.B))
                .ToList();
            var candidates = new List<(
                Point Anchor,
                bool ParentHorizontal,
                double T,
                double CornerDistance,
                bool SnappedToBend)>();

            bool IsSurvivingCorner(int index)
            {
                if (index <= 0 || index >= parentRoute.Count - 1)
                    return false;
                var before = parentRoute[index - 1];
                var corner = parentRoute[index];
                var after = parentRoute[index + 1];
                bool beforeHorizontal = Math.Abs(before.Y - corner.Y) < 0.01;
                bool afterHorizontal = Math.Abs(corner.Y - after.Y) < 0.01;
                bool beforeVertical = Math.Abs(before.X - corner.X) < 0.01;
                bool afterVertical = Math.Abs(corner.X - after.X) < 0.01;
                return (beforeHorizontal && afterVertical) ||
                       (beforeVertical && afterHorizontal);
            }

            double accumulated = 0;
            for (int i = 0; i < parentRoute.Count - 1; i++)
            {
                var a = parentRoute[i];
                var b = parentRoute[i + 1];
                double segmentLength = Dist(a.X, a.Y, b.X, b.Y);
                if (segmentLength < 0.5) continue;

                bool horizontal = Math.Abs(a.Y - b.Y) < 0.01;
                bool vertical = Math.Abs(a.X - b.X) < 0.01;
                if (!horizontal && !vertical)
                {
                    accumulated += segmentLength;
                    continue;
                }

                double margin = Math.Min(
                    preferredCornerDistance, segmentLength / 2);
                Point anchor;
                if (horizontal)
                {
                    double min = Math.Min(a.X, b.X) + margin;
                    double max = Math.Max(a.X, b.X) - margin;
                    double x = min <= max
                        ? Math.Max(min, Math.Min(max, portStub.X))
                        : (a.X + b.X) / 2;
                    x = SnapToGridMidpointWithin(x, min, max);
                    anchor = new Point(x, a.Y);
                }
                else
                {
                    double min = Math.Min(a.Y, b.Y) + margin;
                    double max = Math.Max(a.Y, b.Y) - margin;
                    double y = min <= max
                        ? Math.Max(min, Math.Min(max, portStub.Y))
                        : (a.Y + b.Y) / 2;
                    y = SnapToGridMidpointWithin(y, min, max);
                    anchor = new Point(a.X, y);
                }

                bool snappedToBend = false;
                if (prioritizeBends)
                {
                    // 개별 정리에서는 계산된 분기점에서 한 격자 이내에
                    // 살아남은 부모 꺾임점이 있으면 좌표만 정확히 겹친다.
                    // 꺾임점과 분기점 데이터는 계속 별개로 유지한다.
                    const double bendSnapDistance = DiagramGridSize;
                    var nearbyCorners = new List<Point>();
                    if (IsSurvivingCorner(i)) nearbyCorners.Add(a);
                    if (IsSurvivingCorner(i + 1)) nearbyCorners.Add(b);
                    var nearestCorner = nearbyCorners
                        .Select(point => new
                        {
                            Point = point,
                            Distance = Dist(
                                anchor.X, anchor.Y,
                                point.X, point.Y)
                        })
                        .Where(item => item.Distance <= bendSnapDistance + 0.01)
                        .OrderBy(item => item.Distance)
                        .FirstOrDefault();
                    if (nearestCorner != null)
                    {
                        anchor = nearestCorner.Point;
                        snappedToBend = true;
                    }
                }

                double distanceFromA = Dist(a.X, a.Y, anchor.X, anchor.Y);
                double cornerDistance = Math.Min(
                    distanceFromA, segmentLength - distanceFromA);
                double t = Math.Max(0, Math.Min(
                    1, (accumulated + distanceFromA) / totalLength));
                candidates.Add((
                    anchor, horizontal, t, cornerDistance,
                    snappedToBend));
                accumulated += segmentLength;
            }

            double bestScore = double.MaxValue;
            (double T, List<Point> CoreRoute)? best = null;
            foreach (var candidate in candidates)
            {
                var core = CableRenderer.FindBalancedOrthogonalRoute(
                    candidate.Anchor,
                    portStub,
                    obstacles,
                    occupiedSegments,
                    requiredFirstHorizontal: !candidate.ParentHorizontal,
                    laneGap: AutoRouteLaneGap,
                    prioritizeBends: prioritizeBends,
                    requiredEndDirection: branch.Port.FixedFacing);
                if (core.Count < 2) continue;

                double length = 0;
                for (int i = 0; i < core.Count - 1; i++)
                    length += Dist(
                        core[i].X, core[i].Y,
                        core[i + 1].X, core[i + 1].Y);

                double firstLegLength = Dist(
                    core[0].X, core[0].Y, core[1].X, core[1].Y);
                int bends = Math.Max(0, core.Count - 2);
                double score = prioritizeBends
                    ? bends * 1_000_000_000_000d + length * 1_000_000d
                    : length + bends * 42;

                if (!candidate.SnappedToBend &&
                    candidate.CornerDistance < preferredCornerDistance)
                    score += (preferredCornerDistance -
                              candidate.CornerDistance) * 8;
                if (prioritizeBends && candidate.SnappedToBend)
                    // 꺾임 하나를 더 만드는 것보다 우선하지는 않지만,
                    // 꺾임 수가 같은 후보끼리는 기존 모서리 합류를 선호한다.
                    score -= 50_000_000d;
                if (firstLegLength < preferredFirstLegLength)
                    score += (preferredFirstLegLength -
                              firstLegLength) * 9;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = (candidate.T, core);
                }
            }

            return best;
        }

        private Point GetPreferredAutoRoutePortEscape(
            CableInstance inst,
            string legId,
            Point port,
            PortSide facing)
        {
            var minimum = GetAutoRoutePortEscape(port, facing);
            var preferred = facing switch
            {
                PortSide.Left => new Point(
                    minimum.X - DiagramGridSize, minimum.Y),
                PortSide.Right => new Point(
                    minimum.X + DiagramGridSize, minimum.Y),
                PortSide.Top => new Point(
                    minimum.X, minimum.Y - DiagramGridSize),
                _ => new Point(
                    minimum.X, minimum.Y + DiagramGridSize)
            };

            static bool CrossesInterior(Point a, Point b, Rect rect)
            {
                if (Math.Abs(a.Y - b.Y) < 0.01)
                {
                    if (a.Y <= rect.Top || a.Y >= rect.Bottom) return false;
                    return Math.Max(a.X, b.X) > rect.Left + 0.01 &&
                           Math.Min(a.X, b.X) < rect.Right - 0.01;
                }
                if (Math.Abs(a.X - b.X) < 0.01)
                {
                    if (a.X <= rect.Left || a.X >= rect.Right) return false;
                    return Math.Max(a.Y, b.Y) > rect.Top + 0.01 &&
                           Math.Min(a.Y, b.Y) < rect.Bottom - 0.01;
                }
                return false;
            }

            // 포트 셀을 벗어난 뒤에도 한 칸 더 직진할 공간이 있으면 그
            // 다음 칸에서 꺾는다. 장치 본체나 다른 포트 칸이 막는 경우에만
            // 기존 최소 탈출점에서 바로 꺾는 것을 허용한다.
            var obstacles = GetCableObstacles(
                inst, legId, AutoRouteDeviceClearance);
            return obstacles.Any(rect =>
                    CrossesInterior(minimum, preferred, rect))
                ? minimum
                : preferred;
        }

        private static Point GetAutoRoutePortEscape(
            Point port, PortSide facing)
        {
            // 포트 칸 안에서는 바라보는 방향으로만 직진하고, 장치 면에서
            // 정확히 격자 한 칸 떨어진 바깥 경계에 도달한 뒤부터 꺾을 수 있다.
            // 케이블 포트 중심은 이미 장치 면에서 PortW + ConnectGap만큼
            // 떨어져 있으므로 그 차이만큼만 추가한다.
            double distance = Math.Max(
                CableRenderer.StubLength,
                AutoRouteDeviceClearance -
                (CableRenderer.PortW + CableRenderer.ConnectGap));

            return facing switch
            {
                PortSide.Left => new Point(port.X - distance, port.Y),
                PortSide.Right => new Point(port.X + distance, port.Y),
                PortSide.Top => new Point(port.X, port.Y - distance),
                _ => new Point(port.X, port.Y + distance)
            };
        }

        private static void AddOccupiedSegments(
            List<(string CableInstanceId, Point A, Point B)> occupied,
            string cableInstanceId,
            IReadOnlyList<Point> route)
        {
            for (int i = 0; i < route.Count - 1; i++)
            {
                var a = route[i];
                var b = route[i + 1];
                if (Dist(a.X, a.Y, b.X, b.Y) < 0.5) continue;
                occupied.Add((cableInstanceId, a, b));
            }
        }

        /// <summary>이 케이블의 모든 다리 Id ("root" + 모든 분기, 중첩 포함).</summary>
        private static IEnumerable<string> AllLegIds(CableInstance inst)
        {
            yield return "root";
            foreach (var b in inst.Branches) yield return b.Id;
        }

        /// <summary>이 케이블의 루트와 모든 분기(중첩 포함) 전부를 정리한다.</summary>
        private void SimplifyAllLegs(CableInstance inst)
        {
            SimplifyBends(inst, "root");
            foreach (var b in inst.Branches.ToList())
            {
                EnsureBranchLeavesParentPerpendicularly(inst, b);
                SimplifyBends(inst, b.Id);
            }
        }

        /// <summary>
        /// 구버전 데이터에 남은 "분기점 뒤에서 부모 선을 더 따라가는 첫 꺾임"만
        /// 교정한다. 다른 수동 꺾임과 루트 경로는 건드리지 않는다.
        /// </summary>
        private bool NormalizeBranchDepartureStructure(CableInstance inst)
        {
            bool changed = false;
            foreach (var branch in inst.Branches.ToList())
            {
                var before = branch.Bends
                    .Select(point => new Point(point.X, point.Y)).ToList();
                EnsureBranchLeavesParentPerpendicularly(inst, branch);
                SimplifyBends(inst, branch.Id);

                if (before.Count != branch.Bends.Count)
                {
                    changed = true;
                    continue;
                }
                for (int index = 0; index < before.Count; index++)
                {
                    var after = branch.Bends[index];
                    if (Dist(before[index].X, before[index].Y, after.X, after.Y) <= 0.01)
                        continue;
                    changed = true;
                    break;
                }
            }
            return changed;
        }

        /// <summary>
        /// 분기 시작부가 부모 선과 같은 방향으로 포개져 있으면, 겹치는 선두 꺾인점을 걷어내고
        /// 부모 선에서 수직으로 빠져나가는 첫 꺾임을 만든다.
        /// </summary>
        private void EnsureBranchLeavesParentPerpendicularly(CableInstance inst, CableBranch branch)
        {
            var parentRoute = ComputeLegRoute(inst, branch.ParentId, avoidObstacles: false);
            if (parentRoute == null || parentRoute.Count < 2) return;

            var anchor = CableRenderer.PointAlongPolyline(parentRoute, branch.T);
            var parentAttachment = FindPolylineAttachment(parentRoute, anchor);
            bool parentHorizontal = parentAttachment.IsHorizontal;

            var branchRoute = ComputeLegRoute(inst, branch.Id, avoidObstacles: false);
            if (branchRoute == null || branchRoute.Count < 2) return;

            Point? firstDistinct = null;
            for (int i = 1; i < branchRoute.Count; i++)
            {
                if (Dist(anchor.X, anchor.Y, branchRoute[i].X, branchRoute[i].Y) > 0.5)
                {
                    firstDistinct = branchRoute[i];
                    break;
                }
            }
            if (!firstDistinct.HasValue) return;

            bool branchStartsHorizontal =
                Math.Abs(firstDistinct.Value.Y - anchor.Y) <
                Math.Abs(firstDistinct.Value.X - anchor.X);
            if (branchStartsHorizontal != parentHorizontal) return;

            // 부모와 같은 축으로 이어지는 선두 꺾임은 겹침의 원인이므로 제거한다.
            while (branch.Bends.Count > 0)
            {
                var first = branch.Bends[0];
                bool stillOnParentAxis = parentHorizontal
                    ? Math.Abs(first.Y - anchor.Y) < 1.0
                    : Math.Abs(first.X - anchor.X) < 1.0;
                if (!stillOnParentAxis) break;
                branch.Bends.RemoveAt(0);
            }

            var resolvedPort = CableRenderer.ResolveConnection(branch.Port, ResolveAnchor);
            var owner = _placedCables.FirstOrDefault(
                item => item.Instance == inst);
            var branchFacing = owner != null
                ? GetEffectiveCablePortFacing(owner, branch.Port)
                : branch.Port.FixedFacing;
            var portStub = CableRenderer.StubPoint(
                new Point(resolvedPort.X, resolvedPort.Y), branchFacing);
            Point target = branch.Bends.Count > 0
                ? new Point(branch.Bends[0].X, branch.Bends[0].Y)
                : portStub;

            Point departureCorner;
            if (parentHorizontal)
            {
                double targetY = Math.Abs(target.Y - anchor.Y) >= 1.0
                    ? target.Y
                    : portStub.Y;
                if (Math.Abs(targetY - anchor.Y) < 1.0)
                    targetY = anchor.Y + 100;
                departureCorner = new Point(anchor.X, targetY);
            }
            else
            {
                double targetX = Math.Abs(target.X - anchor.X) >= 1.0
                    ? target.X
                    : portStub.X;
                if (Math.Abs(targetX - anchor.X) < 1.0)
                    targetX = anchor.X + 100;
                departureCorner = new Point(targetX, anchor.Y);
            }

            if (Dist(anchor.X, anchor.Y, departureCorner.X, departureCorner.Y) < 1.0)
                return;

            if (branch.Bends.Count == 0 ||
                Dist(branch.Bends[0].X, branch.Bends[0].Y,
                     departureCorner.X, departureCorner.Y) >= 1.0)
            {
                branch.Bends.Insert(0, new ESDP.Models.Point
                {
                    X = departureCorner.X,
                    Y = departureCorner.Y
                });
            }
        }

        private void CableCorner_Remove(PlacedCable placed, string legId, int routePtsIndex, MouseButtonEventArgs e)
        {
            var inst = placed.Instance;
            var fullPts = ComputeLegRoute(inst, legId);
            if (fullPts == null || routePtsIndex < 1 || routePtsIndex > fullPts.Count - 2) return;

            var bends = GetBendsList(inst, legId);
            if (bends == null) return;

            // 삭제할 지점만 빼고 경로를 다시 만든 뒤, 스텁을 제외한 진짜 꺾인점만 저장한다.
            var kept = new List<Point>();
            for (int i = 0; i < fullPts.Count; i++)
            {
                if (i == routePtsIndex) continue;
                kept.Add(fullPts[i]);
            }

            var materialized = MaterializeBends(inst, legId, kept);

            bends.Clear();
            bends.AddRange(materialized);

            // 사용자가 지정한 나머지 굴곡과 다른 케이블 경로는 그대로 둔다.
            RenderAllCables();
            e.Handled = true;
        }

        // ================= 변(세그먼트) 평행이동 =================
        /// <summary>
        /// 드래그 시작 시점의 전체 경로를 고정해두고, 선택한 변의 양 끝을
        /// 같은 만큼 평행이동시킨다. 렌더링 중 점 목록이 다시 만들어져도
        /// 인덱스가 흔들리지 않도록 저장된 bends 인덱스를 직접 잡지 않는다.
        /// </summary>
        private void CableSegment_MouseDown(PlacedCable placed, string legId, int segIndex, MouseButtonEventArgs e)
        {
            var inst = placed.Instance;
            var fullPts = ComputeLegRoute(inst, legId);
            if (fullPts == null || segIndex < 0 || segIndex >= fullPts.Count - 1) return;

            var pA = fullPts[segIndex];
            var pB = fullPts[segIndex + 1];
            bool horizontal = Math.Abs(pA.Y - pB.Y) < 0.01;
            bool vertical = Math.Abs(pA.X - pB.X) < 0.01;
            if (!horizontal && !vertical) return;

            _cableSegDragPlaced = placed;
            _cableSegDragLegId = legId;
            _cableSegDragRoute = fullPts.Select(p => new Point(p.X, p.Y)).ToList();
            _cableSegDragSegmentIndex = segIndex;
            _cableSegDragHorizontal = horizontal;
            _cableSegDragStartMouse = e.GetPosition(MainCanvas);
            _cableSegDragHasMoved = false;
            _cableSegDragOwnBranch = legId != "root" && segIndex == 0
                ? inst.Branches.FirstOrDefault(item => item.Id == legId)
                : null;
            _cableSegDragParentRoute = _cableSegDragOwnBranch != null
                ? ComputeLegRoute(
                    inst, _cableSegDragOwnBranch.ParentId,
                    avoidObstacles: false)
                : null;
            _cableSegDragOwnBranchStartT = _cableSegDragOwnBranch?.T ?? 0;
            var parentMetric = GetPolylineDragMetric(
                _cableSegDragParentRoute,
                _cableSegDragOwnBranchStartT);
            _cableSegDragParentLength = parentMetric.TotalLength;
            _cableSegDragParentTangent = parentMetric.Tangent;

            _cableSegDragBranchAnchors.Clear();
            foreach (var pair in CaptureDescendantBranchAnchors(
                         inst, legId, pA, pB))
            {
                _cableSegDragBranchAnchors[pair.Key] = pair.Value;
            }

            placed.Container.CaptureMouse();
            e.Handled = true;
        }

        private static double ParallelSegmentOverlapLength(
            Point firstA, Point firstB, Point secondA, Point secondB,
            bool horizontal)
        {
            double firstMin = horizontal
                ? Math.Min(firstA.X, firstB.X)
                : Math.Min(firstA.Y, firstB.Y);
            double firstMax = horizontal
                ? Math.Max(firstA.X, firstB.X)
                : Math.Max(firstA.Y, firstB.Y);
            double secondMin = horizontal
                ? Math.Min(secondA.X, secondB.X)
                : Math.Min(secondA.Y, secondB.Y);
            double secondMax = horizontal
                ? Math.Max(secondA.X, secondB.X)
                : Math.Max(secondA.Y, secondB.Y);
            return Math.Max(0,
                Math.Min(firstMax, secondMax) -
                Math.Max(firstMin, secondMin));
        }

        private static string GetRenderedLegId(
            PlacedCable placed, string legKey)
        {
            string prefix = placed.Instance.Id + ":";
            return legKey.StartsWith(prefix, StringComparison.Ordinal)
                ? legKey[prefix.Length..]
                : "root";
        }

        /// <summary>
        /// 한 케이블 선분을 지정한 통로 차선으로 평행이동한다. 선택한 선분의
        /// 양 끝만 옮기고 인접 선분은 길이만 달라지게 하며, 그 선분 위에 있던
        /// 자식 분기점은 같은 화면 위치를 따라오게 한다.
        /// </summary>
        private bool MoveCableSegmentToLane(
            PlacedCable placed, string legId,
            Point referenceA, Point referenceB,
            bool horizontal, double targetAxis)
        {
            var route = ComputeLegRoute(
                placed.Instance, legId, avoidObstacles: false);
            var bends = GetBendsList(placed.Instance, legId);
            if (route == null || route.Count < 2 || bends == null)
                return false;

            int bestIndex = -1;
            double bestScore = double.MaxValue;
            for (int index = 0; index < route.Count - 1; index++)
            {
                var a = route[index];
                var b = route[index + 1];
                bool sameOrientation = horizontal
                    ? Math.Abs(a.Y - b.Y) < 0.01
                    : Math.Abs(a.X - b.X) < 0.01;
                if (!sameOrientation) continue;

                double direct = Dist(a.X, a.Y, referenceA.X, referenceA.Y) +
                                Dist(b.X, b.Y, referenceB.X, referenceB.Y);
                double reverse = Dist(a.X, a.Y, referenceB.X, referenceB.Y) +
                                 Dist(b.X, b.Y, referenceA.X, referenceA.Y);
                double score = Math.Min(direct, reverse);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = index;
                }
            }
            if (bestIndex < 0) return false;

            var originalA = route[bestIndex];
            var originalB = route[bestIndex + 1];
            double currentAxis = horizontal ? originalA.Y : originalA.X;
            if (Math.Abs(currentAxis - targetAxis) < 0.01) return false;

            var childAnchors = CaptureDescendantBranchAnchors(
                placed.Instance, legId, originalA, originalB);

            route[bestIndex] = horizontal
                ? new Point(originalA.X, targetAxis)
                : new Point(targetAxis, originalA.Y);
            route[bestIndex + 1] = horizontal
                ? new Point(originalB.X, targetAxis)
                : new Point(targetAxis, originalB.Y);

            // 분기 자체의 첫 선분을 나누는 경우에는 녹색 시작점도 기존 부모
            // 경로 위에서 같은 방향으로 옮긴다.
            if (legId != "root" && bestIndex == 0)
            {
                var ownBranch = placed.Instance.Branches.FirstOrDefault(
                    branch => branch.Id == legId);
                var parentRoute = ownBranch == null
                    ? null
                    : ComputeLegRoute(
                        placed.Instance, ownBranch.ParentId,
                        avoidObstacles: false);
                if (ownBranch != null && parentRoute is { Count: >= 2 })
                {
                    var attachment = FindPolylineAttachment(
                        parentRoute, route[0]);
                    ownBranch.T = attachment.T;
                    route[0] = attachment.Anchor;
                    route[1] = horizontal
                        ? new Point(route[1].X, attachment.Anchor.Y)
                        : new Point(attachment.Anchor.X, route[1].Y);
                }
            }

            var intendedChildAnchors = new Dictionary<CableBranch, Point>();
            foreach (var pair in childAnchors)
            {
                Point intended = pair.Value.MovesWithSegment
                    ? (horizontal
                        ? new Point(pair.Value.Anchor.X, targetAxis)
                        : new Point(targetAxis, pair.Value.Anchor.Y))
                    : pair.Value.Anchor;
                intendedChildAnchors[pair.Key] = intended;

                // MaterializeBends가 분기점과 겹친 불필요한 꺾임점을 제거할 수
                // 있도록 임시 경로 기준 T도 먼저 지정한다. 실제 저장 경로의
                // 길이는 아래 단계에서 달라질 수 있으므로 이것이 최종 T는 아니다.
                if (pair.Key.ParentId == legId)
                {
                    pair.Key.T = CableRenderer.FractionAlongPolyline(
                        route, intended);
                }
            }

            bends.Clear();
            bends.AddRange(MaterializeBends(
                placed.Instance, legId, route));
            ReanchorBranchHierarchyToFinalRoutes(
                placed.Instance, intendedChildAnchors);
            return true;
        }

        /// <summary>
        /// 선택한 부모 선 아래의 모든 분기점 좌표를 수집한다. 선택 선의 직접
        /// 자식이면서 실제 선택 선분 위에 있는 점만 선분과 함께 이동하고,
        /// 손자 이하 분기점은 각자 현재 절대 좌표를 유지한다.
        /// </summary>
        private Dictionary<CableBranch, (Point Anchor, bool MovesWithSegment)>
            CaptureDescendantBranchAnchors(
                CableInstance instance, string parentLegId,
                Point selectedA, Point selectedB)
        {
            var result = new Dictionary<
                CableBranch, (Point Anchor, bool MovesWithSegment)>();
            var pendingParents = new Queue<string>();
            var visitedParents = new HashSet<string>(StringComparer.Ordinal);
            pendingParents.Enqueue(parentLegId);

            while (pendingParents.Count > 0)
            {
                string currentParentId = pendingParents.Dequeue();
                if (!visitedParents.Add(currentParentId)) continue;

                var parentRoute = ComputeLegRoute(
                    instance, currentParentId, avoidObstacles: false);
                if (parentRoute == null || parentRoute.Count < 2) continue;

                foreach (var branch in instance.Branches.Where(
                             item => item.ParentId == currentParentId))
                {
                    Point anchor = CableRenderer.PointAlongPolyline(
                        parentRoute, branch.T);
                    bool movesWithSegment =
                        currentParentId == parentLegId &&
                        DistancePointToSegment(
                            anchor, selectedA, selectedB) <= 1.0;
                    result[branch] = (anchor, movesWithSegment);
                    pendingParents.Enqueue(branch.Id);
                }
            }

            return result;
        }

        /// <summary>
        /// 부모 경로를 저장하고 난 뒤 전체 하위 분기점을 화면 절대 좌표에 다시
        /// 고정한다. 부모 분기부터 처리해야 자식의 최종 부모 경로도 확정된다.
        /// 분기점의 T는 전체 경로 길이 비율이므로, 임시 경로에서 계산한 T를 그대로
        /// 두면 스텁/중복 꺾임점 정리나 다른 선분 길이 변경 때 분기점이 밀려난다.
        /// </summary>
        private void ReanchorBranchHierarchyToFinalRoutes(
            CableInstance instance,
            IReadOnlyDictionary<CableBranch, Point> intendedAnchors)
        {
            if (intendedAnchors.Count == 0) return;
            var previousCache = _legRouteCache;
            _legRouteCache = new();
            try
            {
                var ids = intendedAnchors.Keys.Select(branch => branch.Id).ToHashSet(StringComparer.Ordinal);
                var children = intendedAnchors.Keys.GroupBy(branch => branch.ParentId)
                    .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
                var pending = new Queue<CableBranch>(intendedAnchors.Keys.Where(branch => !ids.Contains(branch.ParentId)));
                var visited = new HashSet<CableBranch>();
                while (pending.TryDequeue(out var branch))
                {
                    if (!visited.Add(branch)) continue;
                    var parentRoute = ComputeLegRoute(instance, branch.ParentId, avoidObstacles: false);
                    if (parentRoute is { Count: >= 2 })
                        branch.T = CableRenderer.FractionAlongPolyline(parentRoute, intendedAnchors[branch]);
                    if (children.TryGetValue(branch.Id, out var descendants))
                        foreach (var child in descendants) pending.Enqueue(child);
                }
                // Cyclic/malformed parent links are skipped rather than looping.
            }
            finally { _legRouteCache = previousCache; }
        }

        /// <summary>
        /// 사용자가 옮긴 선분과 다른 케이블 선분이 같은 격자 통로를 공유하면
        /// 그 통로 폭을 N+1등분한 차선에 각 케이블을 배치한다. 이것만 케이블
        /// 중앙선 규칙의 예외이며 전체 경로나 연결 트리는 재계산하지 않는다.
        /// </summary>
        private bool SeparateSharedLaneAfterSegmentDrag(
            PlacedCable dragged, string legId, int segmentIndex)
        {
            var draggedRoute = ComputeLegRoute(
                dragged.Instance, legId, avoidObstacles: false);
            if (draggedRoute == null || segmentIndex < 0 ||
                segmentIndex >= draggedRoute.Count - 1)
                return false;

            Point draggedA = draggedRoute[segmentIndex];
            Point draggedB = draggedRoute[segmentIndex + 1];
            bool horizontal = Math.Abs(draggedA.Y - draggedB.Y) < 0.01;
            bool vertical = Math.Abs(draggedA.X - draggedB.X) < 0.01;
            if (!horizontal && !vertical) return false;

            double draggedAxis = horizontal ? draggedA.Y : draggedA.X;
            double corridorCenter = SnapToDiagramGridMidpoint(draggedAxis);
            var participants = new List<(
                PlacedCable Placed, string LegId,
                Point A, Point B, double Axis)>
            {
                (dragged, legId, draggedA, draggedB, draggedAxis)
            };

            foreach (var other in _placedCables.Where(item => item != dragged))
            {
                var candidate = other.RenderedSegments
                    .Where(segment =>
                    {
                        bool sameOrientation = horizontal
                            ? Math.Abs(segment.A.Y - segment.B.Y) < 0.01
                            : Math.Abs(segment.A.X - segment.B.X) < 0.01;
                        if (!sameOrientation) return false;
                        double axis = horizontal
                            ? segment.A.Y
                            : segment.A.X;
                        return Math.Abs(
                                   SnapToDiagramGridMidpoint(axis) -
                                   corridorCenter) < 0.01 &&
                               ParallelSegmentOverlapLength(
                                   draggedA, draggedB,
                                   segment.A, segment.B,
                                   horizontal) > 1.0;
                    })
                    .Select(segment => new
                    {
                        Segment = segment,
                        Overlap = ParallelSegmentOverlapLength(
                            draggedA, draggedB,
                            segment.A, segment.B,
                            horizontal)
                    })
                    .OrderByDescending(item => item.Overlap)
                    .FirstOrDefault();
                if (candidate == null) continue;

                participants.Add((
                    other,
                    GetRenderedLegId(other, candidate.Segment.LegKey),
                    candidate.Segment.A,
                    candidate.Segment.B,
                    horizontal
                        ? candidate.Segment.A.Y
                        : candidate.Segment.A.X));
            }

            if (participants.Count < 2) return false;

            var ordered = participants
                .OrderBy(item => item.Axis)
                .ThenBy(item => item.Placed.Instance.Id,
                    StringComparer.Ordinal)
                .ToList();
            double corridorMin = corridorCenter - DiagramGridSize / 2;
            double gap = DiagramGridSize / (ordered.Count + 1);
            bool changed = false;
            for (int index = 0; index < ordered.Count; index++)
            {
                var item = ordered[index];
                changed |= MoveCableSegmentToLane(
                    item.Placed, item.LegId,
                    item.A, item.B, horizontal,
                    corridorMin + gap * (index + 1));
            }
            return changed;
        }

        private void CableWhole_MouseDown(
            PlacedCable placed, MouseButtonEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 ||
                e.ClickCount != 1)
                return;

            _wholeCableDragAnchor = placed;
            _wholeCableDragStartMouse = e.GetPosition(MainCanvas);
            _wholeCableDragPortStarts.Clear();
            _wholeCableDragBendStarts.Clear();

            // 케이블-케이블 연결로 이어진 구성요소 전체를 수집한다.
            var component = new HashSet<PlacedCable> { placed };
            var queue = new Queue<PlacedCable>();
            queue.Enqueue(placed);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var layout in AllPortLayouts(current.Instance))
                {
                    var partner = GetConnectedCablePort(layout);
                    if (partner == null) continue;
                    var owner = _placedCables.FirstOrDefault(item =>
                        AllPortLayouts(item.Instance).Any(candidate =>
                            ReferenceEquals(candidate, partner)));
                    if (owner != null && component.Add(owner))
                        queue.Enqueue(owner);
                }
            }

            foreach (var cable in component)
            {
                foreach (var layout in AllPortLayouts(cable.Instance))
                    _wholeCableDragPortStarts[layout] =
                        new Point(layout.PortX, layout.PortY);
                foreach (var bend in cable.Instance.RootBends)
                    _wholeCableDragBendStarts[bend] =
                        new Point(bend.X, bend.Y);
                foreach (var bend in cable.Instance.Branches
                             .SelectMany(branch => branch.Bends))
                    _wholeCableDragBendStarts[bend] =
                        new Point(bend.X, bend.Y);
            }

            SelectCable(placed);
            placed.Container.CaptureMouse();
            e.Handled = true;
        }

        private Rect GetDeviceRoutingObstacle(
            PlacedDevice target, double clearance)
        {
            // 장치 본체만 절대 장애물이다. 예전처럼 본체 전체를 20px
            // 부풀리면 포트가 없는 둘레 격자 칸까지 막혀 버린다.
            // 실제 포트가 차지한 셀은 GetCableObstacles에서 따로 추가한다.
            _ = clearance;
            return new Rect(
                target.Instance.X,
                target.Instance.Y,
                target.Instance.Width,
                target.Instance.Height);
        }

        /// <summary>legId 의 현재 전체 경로(시작~끝, 스텁·꺾인점 전부 포함)를 계산한다.</summary>
        private IReadOnlyList<Rect> GetCableObstacles(
            CableInstance inst, string legId, double clearance = 6)
        {
            // 장치 본체와 실제 포트가 있는 격자 칸만 하드 장애물로 사용한다.
            // clearance는 자동 정리 여부(라벨 장애물 포함)를 구분하는 용도다.
            var obstacles = _placed
                .Select(device =>
                    GetDeviceRoutingObstacle(device, clearance))
                .ToList();

            var ownPortKeys = new HashSet<string>(StringComparer.Ordinal);
            void AddOwnPort(CablePortLayout layout)
            {
                if (!string.IsNullOrWhiteSpace(layout.ConnectedToPortKey))
                    ownPortKeys.Add(layout.ConnectedToPortKey!);
            }

            if (legId == "root")
            {
                AddOwnPort(inst.PortA);
                AddOwnPort(inst.PortB);
            }
            else
            {
                var branch = inst.Branches.FirstOrDefault(item => item.Id == legId);
                if (branch != null) AddOwnPort(branch.Port);
            }

            // 장치 포트명은 경로를 짧게 꺾게 만드는 하드 장애물이 아니다.
            // 정렬 경로를 먼저 확정한 뒤 TidyDevicePortLabels에서
            // 실제로 선과 겹친 이름표만 포트 가까운 빈 자리로 이동한다.

            foreach (var device in _placed)
            {
                foreach (var (port, x, y, side) in
                         DevicePreviewRenderer.GetPortAnchors(
                             device.EffectiveDevice,
                             device.Instance.X,
                             device.Instance.Y,
                             device.Instance.Width,
                             device.Instance.Height))
                {
                    string key = $"dev:{device.Instance.Id}:{port.Id}";
                    if (ownPortKeys.Contains(key)) continue;

                    // 실제 포트가 있는 외곽 격자 칸 하나만 막는다. 자동 정리도
                    // 일반 편집도 같은 규칙을 사용하며, 현재 연결 대상 포트는
                    // 위에서 제외했으므로 자기 포트 칸은 정상 통과한다.
                    double cell = DiagramGridSize;
                    double halfCell = cell / 2;
                    obstacles.Add(side switch
                    {
                        PortSide.Left => new Rect(
                            device.Instance.X - cell,
                            y - halfCell, cell, cell),
                        PortSide.Right => new Rect(
                            device.Instance.X + device.Instance.Width,
                            y - halfCell, cell, cell),
                        PortSide.Top => new Rect(
                            x - halfCell,
                            device.Instance.Y - cell, cell, cell),
                        _ => new Rect(
                            x - halfCell,
                            device.Instance.Y + device.Instance.Height,
                            cell, cell)
                    });
                }
            }

            return obstacles;
        }

        // Cache is scoped to a single capture/reanchor operation, never across edits.
        private Dictionary<(CableInstance Instance, string LegId, bool Avoid), List<Point>?>? _legRouteCache;

        private List<Point>? ComputeLegRoute(
            CableInstance inst, string legId, bool avoidObstacles = false)
        {
            if (_legRouteCache == null) return ComputeLegRouteCore(inst, legId, avoidObstacles);
            var key = (inst, legId, avoidObstacles);
            if (_legRouteCache.TryGetValue(key, out var cached)) return cached;
            var route = ComputeLegRouteCore(inst, legId, avoidObstacles);
            _legRouteCache[key] = route;
            return route;
        }

        private List<Point>? ComputeLegRouteCore(
            CableInstance inst, string legId, bool avoidObstacles = false)
        {
            var owner = _placedCables.FirstOrDefault(item => item.Instance == inst);
            if (legId == "root")
            {
                var a = CableRenderer.ResolveConnection(inst.PortA, ResolveAnchor);
                var b = CableRenderer.ResolveConnection(inst.PortB, ResolveAnchor);
                PortSide aFacing = owner != null
                    ? GetEffectiveCablePortFacing(owner, inst.PortA)
                    : inst.PortA.FixedFacing;
                PortSide bFacing = owner != null
                    ? GetEffectiveCablePortFacing(owner, inst.PortB)
                    : inst.PortB.FixedFacing;
                var bends = inst.RootBends.Select(p => new Point(p.X, p.Y)).ToList();
                var route = CableRenderer.BuildFullRoute(new Point(a.X, a.Y), aFacing,
                                                          new Point(b.X, b.Y), bFacing, bends);
                // 화면에 실제로 그려지는 것과 반드시 똑같은 점 배열이어야 핸들 위치/방향이 맞는다.
                route = CableRenderer.CollapseNearDuplicates(
                    route, hasStartStub: true, hasEndStub: true);
                route = KeepConnectedDeviceApproachOutside(
                    route, inst.PortA, aFacing, endpointAtStart: true);
                route = KeepConnectedDeviceApproachOutside(
                    route, inst.PortB, bFacing, endpointAtStart: false);
                return avoidObstacles
                    ? CableRenderer.AvoidObstacles(
                        route, GetCableObstacles(inst, legId),
                        hasStartStub: true, hasEndStub: true)
                    : route;
            }

            var branch = inst.Branches.FirstOrDefault(b => b.Id == legId);
            if (branch == null) return null;

            // 부모의 "실제 꺾인 경로"를 구해서, 그 선을 따라간 t 지점을 분기점으로 삼는다.
            var parentRoute = ComputeLegRoute(inst, branch.ParentId, avoidObstacles);
            if (parentRoute == null) return null;
            var anchor = CableRenderer.PointAlongPolyline(parentRoute, branch.T);

            var port = CableRenderer.ResolveConnection(branch.Port, ResolveAnchor);
            PortSide portFacing = owner != null
                ? GetEffectiveCablePortFacing(owner, branch.Port)
                : branch.Port.FixedFacing;
            var branchBends = branch.Bends.Select(p => new Point(p.X, p.Y)).ToList();
            var branchRoute = CableRenderer.BuildFullRoute(anchor, null, new Point(port.X, port.Y),
                                                            portFacing, branchBends);
            branchRoute = CableRenderer.CollapseNearDuplicates(
                branchRoute, hasStartStub: false, hasEndStub: true);
            branchRoute = KeepConnectedDeviceApproachOutside(
                branchRoute, branch.Port, portFacing, endpointAtStart: false);
            bool? requiredFirstHorizontal =
                CableRenderer.GetFirstSegmentHorizontal(branchRoute);
            return avoidObstacles
                ? CableRenderer.AvoidObstacles(
                    branchRoute, GetCableObstacles(inst, legId),
                    hasStartStub: false, hasEndStub: true,
                    requiredFirstHorizontal: requiredFirstHorizontal)
                    : branchRoute;
        }

        /// <summary>
        /// 연결된 장치 포트의 진행 방향을 지키면서, 포트 직전 케이블이 장치 본체를
        /// 관통하는 경우에만 충돌 구간을 장치 바깥의 최단 직교 경로로 바꾼다.
        /// 분기점과 충돌 전의 사용자 경로는 그대로 두며 전체 루트를 재탐색하지 않는다.
        /// </summary>
        private List<Point> KeepConnectedDeviceApproachOutside(
            List<Point> route,
            CablePortLayout layout,
            PortSide facing,
            bool endpointAtStart)
        {
            if (route.Count < 3 || string.IsNullOrWhiteSpace(layout.ConnectedToPortKey))
                return route;

            string[] parts = layout.ConnectedToPortKey.Split(':');
            if (parts.Length < 3 || !string.Equals(parts[0], "dev", StringComparison.Ordinal))
                return route;

            var target = _placed.FirstOrDefault(item =>
                string.Equals(item.Instance.Id, parts[1], StringComparison.Ordinal));
            if (target == null)
                return route;

            if (endpointAtStart)
            {
                var reversed = route.AsEnumerable().Reverse().ToList();
                var repaired = KeepConnectedDeviceEndApproachOutside(reversed, target, facing);
                repaired.Reverse();
                return repaired;
            }

            return KeepConnectedDeviceEndApproachOutside(route, target, facing);
        }

        private List<Point> KeepConnectedDeviceEndApproachOutside(
            List<Point> route,
            PlacedDevice target,
            PortSide facing)
        {
            static bool SegmentCrossesInterior(Point a, Point b, Rect rect)
            {
                const double epsilon = 0.5;
                double left = rect.Left + epsilon;
                double right = rect.Right - epsilon;
                double top = rect.Top + epsilon;
                double bottom = rect.Bottom - epsilon;

                if (Math.Abs(a.Y - b.Y) < 0.01)
                    return a.Y > top && a.Y < bottom &&
                           Math.Max(a.X, b.X) > left && Math.Min(a.X, b.X) < right;
                if (Math.Abs(a.X - b.X) < 0.01)
                    return a.X > left && a.X < right &&
                           Math.Max(a.Y, b.Y) > top && Math.Min(a.Y, b.Y) < bottom;
                return true;
            }

            static void AddDistinct(List<Point> points, Point point)
            {
                if (points.Count == 0 ||
                    Math.Abs(points[^1].X - point.X) > 0.01 ||
                    Math.Abs(points[^1].Y - point.Y) > 0.01)
                    points.Add(point);
            }

            var targetRect = new Rect(
                target.Instance.X, target.Instance.Y,
                target.Instance.Width, target.Instance.Height);
            int firstCollision = -1;
            for (int i = 0; i + 1 < route.Count; i++)
            {
                if (!SegmentCrossesInterior(route[i], route[i + 1], targetRect)) continue;
                firstCollision = i;
                break;
            }
            if (firstCollision < 0)
                return route;

            // 충돌 구간의 시작점이 이미 본체 안이면, 본체 밖의 앞 점까지 한 단계씩 물러난다.
            while (firstCollision > 0 && targetRect.Contains(route[firstCollision]))
                firstCollision--;

            Point start = route[firstCollision];
            Point approach = route[^2]; // 포트 캡 바로 바깥의 보호된 스텁
            Point visibleEnd = route[^1];
            double clearance = DiagramGridSize / 2;
            double laneX = facing switch
            {
                PortSide.Left => targetRect.Left - clearance,
                PortSide.Right => targetRect.Right + clearance,
                _ => approach.X
            };
            double laneY = facing switch
            {
                PortSide.Top => targetRect.Top - clearance,
                PortSide.Bottom => targetRect.Bottom + clearance,
                _ => approach.Y
            };

            var candidates = new List<List<Point>>();
            void AddCandidate(params Point[] middle)
            {
                var candidate = new List<Point> { start };
                foreach (Point point in middle) AddDistinct(candidate, point);
                AddDistinct(candidate, approach);
                candidates.Add(candidate);
            }

            if (facing is PortSide.Left or PortSide.Right)
            {
                AddCandidate(new Point(laneX, start.Y), new Point(laneX, approach.Y));
                double above = targetRect.Top - clearance;
                double below = targetRect.Bottom + clearance;
                AddCandidate(new Point(start.X, above), new Point(laneX, above),
                             new Point(laneX, approach.Y));
                AddCandidate(new Point(start.X, below), new Point(laneX, below),
                             new Point(laneX, approach.Y));
            }
            else
            {
                AddCandidate(new Point(start.X, laneY), new Point(approach.X, laneY));
                double left = targetRect.Left - clearance;
                double right = targetRect.Right + clearance;
                AddCandidate(new Point(left, start.Y), new Point(left, laneY),
                             new Point(approach.X, laneY));
                AddCandidate(new Point(right, start.Y), new Point(right, laneY),
                             new Point(approach.X, laneY));
            }

            bool IsClear(List<Point> candidate)
            {
                foreach (var device in _placed)
                {
                    var rect = new Rect(device.Instance.X, device.Instance.Y,
                                        device.Instance.Width, device.Instance.Height);
                    for (int i = 0; i + 1 < candidate.Count; i++)
                        if (SegmentCrossesInterior(candidate[i], candidate[i + 1], rect))
                            return false;
                }
                return true;
            }

            static double Length(List<Point> points)
            {
                double result = 0;
                for (int i = 0; i + 1 < points.Count; i++)
                    result += Math.Abs(points[i + 1].X - points[i].X) +
                              Math.Abs(points[i + 1].Y - points[i].Y);
                return result;
            }

            var best = candidates
                .Where(IsClear)
                .OrderBy(Length)
                .ThenBy(candidate => candidate.Count)
                .FirstOrDefault();
            if (best == null)
                return route;

            var repaired = route.Take(firstCollision + 1).ToList();
            foreach (Point point in best.Skip(1)) AddDistinct(repaired, point);
            AddDistinct(repaired, visibleEnd);
            return CableRenderer.CollapseNearDuplicates(
                repaired, hasStartStub: false, hasEndStub: true);
        }

        // ================= 분기 삭제 (우클릭 메뉴에서 호출) =================
        private static HashSet<string> GetBranchSubtreeIds(CableInstance inst, string branchId)
        {
            var result = new HashSet<string> { branchId };
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var branch in inst.Branches)
                {
                    if (!result.Contains(branch.Id) && result.Contains(branch.ParentId))
                        changed |= result.Add(branch.Id);
                }
            }
            return result;
        }

        private void DeleteBranch(PlacedCable placed, string branchId)
        {
            var inst = placed.Instance;
            var toRemove = GetBranchSubtreeIds(inst, branchId);
            var removedConnectorIds = inst.Branches
                .Where(b => toRemove.Contains(b.Id))
                .Select(b => b.Port.ConnectorId)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet();

            inst.Branches.RemoveAll(b => toRemove.Contains(b.Id));

            // 분기 포트 정의도 함께 제거하되, 루트나 남은 분기가 같은 정의를 쓰면 보존한다.
            if (removedConnectorIds.Count > 0)
            {
                var stillUsed = new HashSet<string>
                {
                    inst.PortA.ConnectorId,
                    inst.PortB.ConnectorId
                };
                foreach (var branch in inst.Branches)
                    stillUsed.Add(branch.Port.ConnectorId);

                string originalName = placed.TemplateCable.Name;
                var cable = CloneCableDefinition(placed.EffectiveCable);
                cable.Connectors.RemoveAll(c =>
                    removedConnectorIds.Contains(c.Id) && !stillUsed.Contains(c.Id));
                foreach (var row in cable.SignalRows)
                    foreach (string connectorId in removedConnectorIds)
                        row.PortPins.Remove(connectorId);
                SynchronizeCableSharedData(
                    placed.TemplateCable, cable, originalName, ensureLayouts: true);

                // 다른 케이블이 삭제된 분기 포트에 붙어 있었다면 끊어진 참조를 남기지 않는다.
                var removedSnapKeys = removedConnectorIds
                    .Select(id => $"cab:{inst.Id}:{id}")
                    .ToHashSet();
                foreach (var other in _placedCables)
                {
                    foreach (var layout in AllPortLayouts(other.Instance))
                    {
                        if (layout.ConnectedToPortKey != null &&
                            removedSnapKeys.Contains(layout.ConnectedToPortKey))
                            layout.ConnectedToPortKey = null;
                    }
                }
            }

            // 삭제한 분기만 반영하고 남은 루트/분기 경로는 재정렬하지 않는다.
            RenderAllCables();
            UpdateCanvasExtent();
            StatusText.Text = $"분기 포트 {toRemove.Count}개 삭제됨";
            _isDirty = true;
        }

        /// <summary>
        /// 이동 중인 분기 자신과 그 하위 분기를 제외한 모든 다리 중 마우스에 가장 가까운
        /// 다리와 그 위의 위치를 찾는다. 하위 다리를 제외해야 트리 순환이 생기지 않는다.
        /// </summary>
        private (string LegId, double T)? FindNearestBranchAttachment(
            CableInstance inst, CableBranch movingBranch, Point mouse)
        {
            var blocked = GetBranchSubtreeIds(inst, movingBranch.Id);
            string? bestLegId = null;
            double bestT = 0.5;
            double bestDistance = double.MaxValue;

            foreach (var legId in AllLegIds(inst))
            {
                if (blocked.Contains(legId)) continue;
                var route = ComputeLegRoute(inst, legId);
                if (route == null || route.Count < 2) continue;

                var attachment = FindGridSnappedPolylineAttachment(route, mouse);
                double t = attachment.T;
                var point = attachment.Anchor;
                double distance = Dist(mouse.X, mouse.Y, point.X, point.Y);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestLegId = legId;
                    bestT = t;
                }
            }

            return bestLegId == null ? null : (bestLegId, bestT);
        }

        // ================= 분기점 드래그 (이 케이블의 모든 선을 따라 이동) =================
        private void CableBranchDot_MouseDown(PlacedCable placed, CableBranch branch, MouseButtonEventArgs e)
        {
            // 녹색 분기점을 직접 잡은 경우에는 기존처럼 다른 다리로 옮겨 붙일 수 있다.
            _cableBranchDragStartMouse = e.GetPosition(MainCanvas);
            _cableBranchDragHasMoved = false;
            _cableBranchDragPlaced = placed;
            _cableBranchDragBranch = branch;
            placed.Container.CaptureMouse();
            e.Handled = true;
        }

        // ================= 케이블 오버레이 공통 마우스 이동/뗌 =================
        private void CableOverlay_MouseMove(PlacedCable placed, MouseEventArgs e)
        {
            if (_wholeCableDragAnchor != null)
            {
                AutoScrollIfNearEdge(e);
                var mouse = e.GetPosition(MainCanvas);
                double dx = mouse.X - _wholeCableDragStartMouse.X;
                double dy = mouse.Y - _wholeCableDragStartMouse.Y;

                var freeAnchor = _wholeCableDragPortStarts.FirstOrDefault(
                    pair => string.IsNullOrWhiteSpace(
                                pair.Key.ConnectedToPortKey) &&
                            GetConnectedCablePort(pair.Key) == null);
                if (freeAnchor.Key != null)
                {
                    Point snappedAnchor = SnapCablePortToGridFace(
                        new Point(freeAnchor.Value.X + dx,
                            freeAnchor.Value.Y + dy),
                        freeAnchor.Key.FixedFacing);
                    dx = snappedAnchor.X - freeAnchor.Value.X;
                    dy = snappedAnchor.Y - freeAnchor.Value.Y;
                }

                foreach (var pair in _wholeCableDragPortStarts)
                {
                    // 장치에 붙은 끝은 장치 앵커에 계속 붙어 있게 둔다.
                    bool attachedToDevice = pair.Key.ConnectedToPortKey?
                        .StartsWith("dev:", StringComparison.Ordinal) == true;
                    if (attachedToDevice) continue;
                    pair.Key.PortX = pair.Value.X + dx;
                    pair.Key.PortY = pair.Value.Y + dy;
                }
                foreach (var pair in _wholeCableDragBendStarts)
                {
                    pair.Key.X = pair.Value.X + dx;
                    pair.Key.Y = pair.Value.Y + dy;
                }
                QueueCableDragRender(placed);
                return;
            }

            if (_cablePortDragPlaced == placed && _cablePortDragLayout != null)
            {
                AutoScrollIfNearEdge(e);
                QueueCablePortDragInput(placed, e.GetPosition(MainCanvas));
                return;
            }

            if (_cableBranchDragPlaced == placed && _cableBranchDragBranch != null)
            {
                AutoScrollIfNearEdge(e);
                var mouse = e.GetPosition(MainCanvas);

                // 단순 클릭이나 손떨림을 드래그로 취급하면 MouseUp에서 경로 정리가
                // 실행되어, 아무것도 움직이지 않았는데 분기점 위치가 달라질 수 있다.
                if (!_cableBranchDragHasMoved)
                {
                    double dx = mouse.X - _cableBranchDragStartMouse.X;
                    double dy = mouse.Y - _cableBranchDragStartMouse.Y;
                    if (dx * dx + dy * dy < 9.0)
                        return;
                    _cableBranchDragHasMoved = true;
                }

                var nearest = FindNearestBranchAttachment(
                    placed.Instance, _cableBranchDragBranch, mouse);
                if (nearest.HasValue)
                {
                    var (legId, t) = nearest.Value;
                    if (_cableBranchDragBranch.ParentId != legId)
                    {
                        _cableBranchDragBranch.ParentId = legId;
                        _cableBranchDragBranch.Bends.Clear();
                    }
                    _cableBranchDragBranch.T = t;
                    EnsureBranchLeavesParentPerpendicularly(
                        placed.Instance, _cableBranchDragBranch);
                }

                QueueCableDragRender(placed);
                return;
            }

            if (_cableSegDragPlaced == placed && _cableSegDragLegId != null && _cableSegDragRoute != null)
            {
                AutoScrollIfNearEdge(e);
                var mouse = e.GetPosition(MainCanvas);
                var bends = GetBendsList(placed.Instance, _cableSegDragLegId);
                int segIndex = _cableSegDragSegmentIndex;
                if (bends != null && segIndex >= 0 && segIndex < _cableSegDragRoute.Count - 1)
                {
                    var movedRoute = _cableSegDragRoute.Select(p => new Point(p.X, p.Y)).ToList();
                    var pA = movedRoute[segIndex];
                    var pB = movedRoute[segIndex + 1];

                    if (_cableSegDragHorizontal)
                    {
                        double dy = mouse.Y - _cableSegDragStartMouse.Y;
                        double snappedY = SnapToDiagramGridMidpoint(pA.Y + dy);
                        movedRoute[segIndex] = new Point(pA.X, snappedY);
                        movedRoute[segIndex + 1] = new Point(pB.X, snappedY);
                    }
                    else
                    {
                        double dx = mouse.X - _cableSegDragStartMouse.X;
                        double snappedX = SnapToDiagramGridMidpoint(pA.X + dx);
                        movedRoute[segIndex] = new Point(snappedX, pA.Y);
                        movedRoute[segIndex + 1] = new Point(snappedX, pB.Y);
                    }
                    _cableSegDragHasMoved |= _cableSegDragHorizontal
                        ? Math.Abs(movedRoute[segIndex].Y - pA.Y) > 0.01
                        : Math.Abs(movedRoute[segIndex].X - pA.X) > 0.01;

                    // 분기의 첫 선분이면 선택한 선분의 양 끝만 함께 평행이동한다.
                    // 시작점은 부모 경로를 벗어날 수 없으므로 부모의 같은 선분
                    // 위에서 격자 중앙에 붙이고, 첫 꺾임점만 그 좌표에 맞춘다.
                    // 이후 가로/세로 선분과 나머지 굴곡 좌표는 변경하지 않는다.
                    if (_cableSegDragOwnBranch != null &&
                        _cableSegDragParentRoute is { Count: >= 2 } &&
                        _cableSegDragParentLength > 0.001)
                    {
                        Vector movement = movedRoute[segIndex] - pA;
                        double distanceAlongParent =
                            movement.X * _cableSegDragParentTangent.X +
                            movement.Y * _cableSegDragParentTangent.Y;
                        double nextT = _cableSegDragOwnBranchStartT +
                                       distanceAlongParent /
                                       _cableSegDragParentLength;
                        _cableSegDragOwnBranch.T =
                            SnapPolylineFractionOnCurrentSegment(
                                _cableSegDragParentRoute, nextT);
                        Point snappedAnchor = CableRenderer.PointAlongPolyline(
                            _cableSegDragParentRoute,
                            _cableSegDragOwnBranch.T);
                        movedRoute[segIndex] = snappedAnchor;
                        movedRoute[segIndex + 1] = _cableSegDragHorizontal
                            ? new Point(pB.X, snappedAnchor.Y)
                            : new Point(snappedAnchor.X, pB.Y);
                    }

                    // 분기점과 꺾임점이 같은 위치라면 분기점이 그 위치를 소유한다.
                    // 저장 경로를 다시 계산한 뒤 T를 구하면 두 점이 갈라져 이전
                    // 위치에 주황색 꺾임점이 남을 수 있으므로, 사용자가 실제로
                    // 움직이고 있는 경로에서 분기점 위치를 먼저 확정한다.
                    Dictionary<CableBranch, Point>? intendedBranchAnchors = null;
                    if (movedRoute.Count >= 2)
                    {
                        double dx = _cableSegDragHorizontal
                            ? 0
                            : movedRoute[segIndex].X - pA.X;
                        double dy = _cableSegDragHorizontal
                            ? movedRoute[segIndex].Y - pA.Y
                            : 0;
                        intendedBranchAnchors = new();
                        foreach (var pair in _cableSegDragBranchAnchors)
                        {
                            var intended = pair.Value.MovesWithSegment
                                ? new Point(
                                    pair.Value.Anchor.X + dx,
                                    pair.Value.Anchor.Y + dy)
                                : pair.Value.Anchor;
                            intendedBranchAnchors[pair.Key] = intended;

                            // 중복 꺾임점 정리를 위한 임시 T. 저장된 최종 경로가
                            // 확정된 뒤 동일 절대 좌표로 다시 환산한다.
                            if (pair.Key.ParentId == _cableSegDragLegId)
                            {
                                pair.Key.T = CableRenderer.FractionAlongPolyline(
                                    movedRoute, intended);
                            }
                        }
                    }

                    bends.Clear();
                    bends.AddRange(MaterializeBends(
                        placed.Instance, _cableSegDragLegId, movedRoute));
                    if (intendedBranchAnchors != null)
                    {
                        ReanchorBranchHierarchyToFinalRoutes(
                            placed.Instance, intendedBranchAnchors);
                    }
                }

                QueueCableDragRender(placed);
                return;
            }
        }

        // Only a port-drop may repair its approach; drawing/exporting never calls this.
        private void RepairNewDeviceConnection(PlacedCable placed, CablePortLayout port)
        {
            var connected = TryGetConnectedDevicePort(port);
            if (connected == null) return;
            var inst = placed.Instance;
            var branch = inst.Branches.FirstOrDefault(item => ReferenceEquals(item.Port, port));
            string legId = branch?.Id ?? "root";
            bool reverse = branch != null || ReferenceEquals(inst.PortB, port);
            var route = ComputeLegRoute(inst, legId);
            if (route == null || route.Count < 2) return;
            if (reverse) route.Reverse();
            var resolved = CableRenderer.ResolveConnection(port, ResolveAnchor);
            var facing = resolved.DeviceFacing ?? port.FixedFacing;
            Vector outward = facing switch
            {
                PortSide.Left => new Vector(-1, 0),
                PortSide.Right => new Vector(1, 0),
                PortSide.Top => new Vector(0, -1),
                _ => new Vector(0, 1)
            };
            static bool Crosses(Point a, Point b, Rect box)
            {
                const double eps = 0.01;
                if (Math.Abs(a.X - b.X) < eps)
                    return a.X > box.Left + eps && a.X < box.Right - eps &&
                        Math.Max(a.Y, b.Y) > box.Top + eps && Math.Min(a.Y, b.Y) < box.Bottom - eps;
                if (Math.Abs(a.Y - b.Y) < eps)
                    return a.Y > box.Top + eps && a.Y < box.Bottom - eps &&
                        Math.Max(a.X, b.X) > box.Left + eps && Math.Min(a.X, b.X) < box.Right - eps;
                return true; // Only orthogonal routes are valid.
            }
            var device = connected.Value.Device.Instance;
            var body = new Rect(device.X, device.Y, device.Width, device.Height);
            var first = route.Skip(1).FirstOrDefault(p => (p - route[0]).Length > 0.01, route[0]);
            bool invalid = Vector.Multiply(first - route[0], outward) <= 0.01 ||
                route.Zip(route.Skip(1), (a, b) => Crosses(a, b, body)).Any(hit => hit);
            if (!invalid) return;

            var obstacles = GetCableObstacles(inst, legId);
            var escape = GetAutoRoutePortEscape(new Point(resolved.X, resolved.Y), facing);
            var anchors = CaptureDescendantBranchAnchors(inst, legId, route[0], route[0])
                .ToDictionary(pair => pair.Key, pair => pair.Value.Anchor);
            // Rejoin the earliest safe old point. All of the suffix remains untouched.
            for (int join = 1; join < route.Count; join++)
            {
                if (route.Skip(join).Zip(route.Skip(join + 1),
                        (a, b) => Crosses(a, b, body)).Any(hit => hit)) continue;
                var core = CableRenderer.FindBalancedOrthogonalRoute(
                    escape, route[join], obstacles, Array.Empty<(Point A, Point B)>(),
                    prioritizeBends: false, requiredStartDirection: facing);
                var repaired = new List<Point> { route[0] };
                repaired.AddRange(core);
                if (repaired.Count < 2 ||
                    Vector.Multiply(repaired[1] - repaired[0], outward) <= 0.01 ||
                    repaired.Zip(repaired.Skip(1), (a, b) =>
                        obstacles.Any(box => Crosses(a, b, box))).Any(hit => hit)) continue;
                repaired.AddRange(route.Skip(join + 1));
                if (reverse) repaired.Reverse();
                var bends = branch?.Bends ?? inst.RootBends;
                var previousBends = bends.ToList();
                var replacement = MaterializeBends(inst, legId, repaired);
                bends.Clear();
                bends.AddRange(replacement);
                // Validate the final renderer output, not just the router candidate:
                // automatic stubs and bend materialization can change the approach.
                var finalRoute = ComputeLegRoute(inst, legId);
                if (reverse) finalRoute?.Reverse();
                var next = finalRoute?.Skip(1).FirstOrDefault(
                    point => (point - finalRoute[0]).Length > 0.01, finalRoute[0]);
                if (finalRoute == null || finalRoute.Count < 2 || !next.HasValue ||
                    Vector.Multiply(next.Value - finalRoute[0], outward) <= 0.01 ||
                    finalRoute.Zip(finalRoute.Skip(1), (a, b) => Crosses(a, b, body)).Any(hit => hit))
                {
                    bends.Clear();
                    bends.AddRange(previousBends);
                    continue;
                }
                ReanchorBranchHierarchyToFinalRoutes(inst, anchors);
                return;
            }
            MessageBox.Show(this,
                "연결부의 안전한 우회 경로를 찾지 못했습니다. 장치 사이 공간이나 인접 꺾임점을 조정해 주세요.",
                "케이블 연결 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void CableOverlay_MouseUp(PlacedCable placed, MouseButtonEventArgs e)
        {
            // Lane separation uses the final rendered segments, including the last input event.
            FlushCableDragRender();
            placed.Container.ReleaseMouseCapture();

            if (_wholeCableDragAnchor != null)
            {
                foreach (CablePortLayout layout in
                         AllPortLayouts(_wholeCableDragAnchor.Instance))
                {
                    if (!string.IsNullOrWhiteSpace(layout.ConnectedToPortKey) ||
                        GetConnectedCablePort(layout) != null)
                        continue;
                    Point snapped = SnapCablePortToGridFace(
                        new Point(layout.PortX, layout.PortY),
                        layout.FixedFacing);
                    layout.PortX = snapped.X;
                    layout.PortY = snapped.Y;
                }
                _wholeCableDragAnchor = null;
                _wholeCableDragPortStarts.Clear();
                _wholeCableDragBendStarts.Clear();
                RenderAllCables();
                NormalizeCanvasOriginForNegativeContent();
                UpdateCanvasExtent();
                _isDirty = true;
                return;
            }

            // 일반적으로는 격자 중앙선 하나를 사용한다. 다만 사용자가 방금
            // 옮긴 선분이 다른 케이블과 같은 통로에서 겹칠 때만 해당 통로를
            // 균등 차선으로 나누며, 다른 굴곡이나 전체 루트는 건드리지 않는다.
            if (_cableSegDragHasMoved &&
                _cableSegDragPlaced == placed &&
                _cableSegDragLegId != null)
            {
                SeparateSharedLaneAfterSegmentDrag(
                    placed, _cableSegDragLegId,
                    _cableSegDragSegmentIndex);
            }

            var movedPort = _cablePortDragLayout;
            var portDragProtection = _cablePortDragProtection;
            bool movedPortInsideDevice = movedPort != null &&
                                         IsUnconnectedCablePortInsideDeviceBody(
                                             movedPort);
            bool branchWasDragging = _cableBranchDragBranch != null &&
                                     _cableBranchDragHasMoved;
            bool wasDragging = _cablePortDragPlaced != null || branchWasDragging
                               || _cableSegDragLegId != null;

            _cablePortDragPlaced = null;
            _cablePortDragLayout = null;
            _cablePortDragProtection = null;
            _cableBranchDragPlaced = null;
            _cableBranchDragBranch = null;
            _cableBranchDragHasMoved = false;
            _cableSegDragPlaced = null;
            _cableSegDragLegId = null;
            _cableSegDragRoute = null;
            _cableSegDragSegmentIndex = -1;
            _cableSegDragOwnBranch = null;
            _cableSegDragParentRoute = null;
            _cableSegDragOwnBranchStartT = 0;
            _cableSegDragParentLength = 0;
            _cableSegDragParentTangent = new Vector();
            _cableSegDragHasMoved = false;
            _cableSegDragBranchAnchors.Clear();

            // 사용자가 만든 케이블 구조가 조작을 끝내는 순간 자동 라우팅으로
            // 바뀌지 않게 한다. 이동 중 확정된 격자 중앙 좌표와 굴곡/분기 구조를
            // 그대로 저장하고, 포트를 직접 옮긴 경우에만 그 포트 방향을 갱신한다.
            if (wasDragging)
            {
                if (movedPort != null && !movedPortInsideDevice)
                {
                    UpdatePortFacingAfterUserMove(placed, movedPort);
                    RepairNewDeviceConnection(placed, movedPort);
                }
                RestoreCablePortDragAnchors(placed.Instance, portDragProtection, moveTail: false);
                RenderAllCables();
                _isDirty = true;
            }

            NormalizeCanvasOriginForNegativeContent();
            UpdateCanvasExtent();
        }

        private static double Dist(double x1, double y1, double x2, double y2)
            => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

        private static (double TotalLength, Vector Tangent) GetPolylineDragMetric(
            List<Point>? route, double t)
        {
            if (route == null || route.Count < 2)
                return (0, new Vector());

            double total = 0;
            for (int index = 0; index < route.Count - 1; index++)
                total += Dist(
                    route[index].X, route[index].Y,
                    route[index + 1].X, route[index + 1].Y);
            if (total < 0.001)
                return (0, new Vector());

            double target = Math.Max(0, Math.Min(1, t)) * total;
            double accumulated = 0;
            for (int index = 0; index < route.Count - 1; index++)
            {
                var start = route[index];
                var end = route[index + 1];
                double length = Dist(start.X, start.Y, end.X, end.Y);
                if (length < 0.001) continue;
                if (accumulated + length >= target || index == route.Count - 2)
                    return (total, new Vector(
                        (end.X - start.X) / length,
                        (end.Y - start.Y) / length));
                accumulated += length;
            }

            return (total, new Vector());
        }

        /// <summary>케이블 트리 전체(포트+꺾인점)의 바운딩 박스를 계산한다 (용지 확장 판정용).</summary>
        private (double MinX, double MinY, double MaxX, double MaxY) GetCableBounds(CableInstance inst)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            void Consider(double x, double y)
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            foreach (var lay in AllPortLayouts(inst)) Consider(lay.PortX, lay.PortY);
            foreach (var p in inst.RootBends) Consider(p.X, p.Y);
            foreach (var b in inst.Branches)
                foreach (var p in b.Bends) Consider(p.X, p.Y);

            if (minX == double.MaxValue) return (0, 0, 0, 0);
            return (minX, minY, maxX, maxY);
        }

        private PlacedDevice CreatePlaced(Device template, DeviceInstance instance)
        {
            var container = new Canvas { Cursor = Cursors.SizeAll, Background = Brushes.Transparent };
            var placed = new PlacedDevice { TemplateDevice = template, Instance = instance, Container = container };

            container.MouseLeftButtonDown += Container_MouseLeftButtonDown;
            container.MouseMove += Container_MouseMove;
            container.MouseLeftButtonUp += Container_MouseLeftButtonUp;
            container.MouseRightButtonDown += Container_MouseRightButtonDown;

            return placed;
        }

        // ================= 장치 그리기 =================
        private void DevicePortLabel_MouseLeftButtonDown(
            PlacedDevice placed, TextBlock label,
            DevicePreviewRenderer.PortLabelVisualInfo info,
            MouseButtonEventArgs e)
        {
            var editable = EnsureEditableDevice(placed);
            var port = editable.Ports.FirstOrDefault(item => item.Id == info.Port.Id);
            if (port == null) return;

            _devicePortLabelDragPlaced = placed;
            _devicePortLabelDragPort = port;
            _devicePortLabelDragVisual = label;
            _devicePortLabelDragStartMouse = e.GetPosition(MainCanvas);
            _devicePortLabelDragOrigX = Canvas.GetLeft(label) - info.AnchorX;
            _devicePortLabelDragOrigY = Canvas.GetTop(label) - info.AnchorY;
            _devicePortLabelDragAnchorX = info.AnchorX;
            _devicePortLabelDragAnchorY = info.AnchorY;
            port.HasCustomLabelPosition = true;
            port.LabelOffsetX = _devicePortLabelDragOrigX;
            port.LabelOffsetY = _devicePortLabelDragOrigY;
            placed.Container.CaptureMouse();
            e.Handled = true;
        }

        /// <summary>
        /// 포트명 이동 범위를 텍스트의 좌상단이 아니라 실제 텍스트 사각형과
        /// 포트 사각형 사이 거리로 제한한다. 따라서 글자 수가 달라도 이동 범위의
        /// 중심은 실제 포트 중심이며, 긴 이름을 포트 왼쪽에 놓아도 포트와 겹치지 않는다.
        /// </summary>
        private static Point ConstrainDevicePortLabelOffset(
            Port port, TextBlock label,
            double anchorX, double anchorY,
            double requestedOffsetX, double requestedOffsetY)
        {
            label.Measure(new Size(
                double.PositiveInfinity, double.PositiveInfinity));
            double labelWidth = Math.Max(1,
                label.ActualWidth > 0 ? label.ActualWidth : label.DesiredSize.Width);
            double labelHeight = Math.Max(1,
                label.ActualHeight > 0 ? label.ActualHeight : label.DesiredSize.Height);

            bool verticalPort = port.Side is PortSide.Left or PortSide.Right;
            double portCenterX = anchorX + (port.Side switch
            {
                PortSide.Left => -DevicePreviewRenderer.PortWidth / 2,
                PortSide.Right => DevicePreviewRenderer.PortWidth / 2,
                _ => 0
            });
            double portCenterY = anchorY + (port.Side switch
            {
                PortSide.Top => -DevicePreviewRenderer.PortWidth / 2,
                PortSide.Bottom => DevicePreviewRenderer.PortWidth / 2,
                _ => 0
            });
            double portWidth = verticalPort
                ? DevicePreviewRenderer.PortWidth
                : DevicePreviewRenderer.PortHeight;
            double portHeight = verticalPort
                ? DevicePreviewRenderer.PortHeight
                : DevicePreviewRenderer.PortWidth;

            const double maximumEdgeDistance = 15;
            const double minimumEdgeDistance = 4;

            // 텍스트 사각형의 어느 변이 포트를 향하더라도 같은 거리 범위를
            // 갖도록 글자 폭/높이까지 포함해서 좌상단 이동 범위를 계산한다.
            double minLeft = portCenterX - maximumEdgeDistance - labelWidth;
            double maxLeft = portCenterX + maximumEdgeDistance;
            double minTop = portCenterY - maximumEdgeDistance - labelHeight;
            double maxTop = portCenterY + maximumEdgeDistance;
            double requestedLeft = Math.Clamp(
                anchorX + requestedOffsetX, minLeft, maxLeft);
            double requestedTop = Math.Clamp(
                anchorY + requestedOffsetY, minTop, maxTop);

            var protectedPort = new Rect(
                portCenterX - portWidth / 2,
                portCenterY - portHeight / 2,
                portWidth, portHeight);
            protectedPort.Inflate(minimumEdgeDistance, minimumEdgeDistance);
            var requestedLabel = new Rect(
                requestedLeft, requestedTop, labelWidth, labelHeight);

            if (requestedLabel.IntersectsWith(protectedPort))
            {
                // 겹침을 해소하는 네 방향 중 사용자가 요청한 위치에서 가장
                // 적게 움직이는 방향을 선택한다. 긴 글자도 포트 바깥으로 완전히
                // 밀어내므로 글자 끝부분이 포트를 덮지 않는다.
                var candidates = new[]
                {
                    new Point(protectedPort.Left - labelWidth, requestedTop),
                    new Point(protectedPort.Right, requestedTop),
                    new Point(requestedLeft, protectedPort.Top - labelHeight),
                    new Point(requestedLeft, protectedPort.Bottom)
                };
                Point best = new(requestedLeft, requestedTop);
                double bestDistance = double.MaxValue;
                foreach (var candidate in candidates)
                {
                    double left = Math.Clamp(candidate.X, minLeft, maxLeft);
                    double top = Math.Clamp(candidate.Y, minTop, maxTop);
                    var candidateRect = new Rect(
                        left, top, labelWidth, labelHeight);
                    if (candidateRect.IntersectsWith(protectedPort)) continue;

                    double dx = left - requestedLeft;
                    double dy = top - requestedTop;
                    double distance = dx * dx + dy * dy;
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = new Point(left, top);
                }
                requestedLeft = best.X;
                requestedTop = best.Y;
            }

            return new Point(
                requestedLeft - anchorX,
                requestedTop - anchorY);
        }

        private void RenderDevice(PlacedDevice placed)
        {
            var effective = placed.EffectiveDevice;
            string category = string.IsNullOrWhiteSpace(effective.CustomKindName) ? effective.Kind.ToString() : effective.CustomKindName;
            effective.CategoryColorHex = _deviceCategoryColors.GetValueOrDefault(category, "");
            var c = placed.Container;
            var inst = placed.Instance;

            c.Width = inst.Width;
            c.Height = inst.Height;
            Canvas.SetLeft(c, inst.X);
            Canvas.SetTop(c, inst.Y);

            DevicePreviewRenderer.Draw(
                c, placed.EffectiveDevice, inst.Width, inst.Height,
                purpose: inst.Purpose);

            foreach (UIElement child in c.Children)
            {
                if (child is Rectangle rect && rect.Tag is Port port)
                {
                    bool selected = ReferenceEquals(
                                        placed, _selectedDevicePortPlaced) &&
                                    _selectedDevicePort?.Id == port.Id;
                    rect.Stroke = selected
                        ? AppThemeManager.GetBrush("AppAccentBrush")
                        : Brushes.Transparent;
                    rect.StrokeThickness = selected ? 2.2 : 0;
                    rect.MouseLeftButtonDown += (s, e) => PortHandle_MouseLeftButtonDown(placed, port, e);
                }
                else if (child is TextBlock label &&
                         label.Tag is DevicePreviewRenderer.PortLabelVisualInfo info)
                {
                    label.Cursor = Cursors.SizeAll;
                    label.ToolTip = "포트 이름 위치 이동";
                    label.MouseLeftButtonDown += (_, e) =>
                        DevicePortLabel_MouseLeftButtonDown(
                            placed, label, info, e);
                }
            }

            if (_selectedSet.Contains(placed))
                AddSelectionVisuals(placed, showHandles: _selectedSet.Count == 1);

        }

        private void ApplyMinSizeAndRender(PlacedDevice placed)
        {
            var (minW, minH) = DevicePreviewRenderer.GetMinSize(placed.EffectiveDevice);
            placed.Instance.Width = SnapLengthToDiagramGrid(
                placed.Instance.Width, minW);
            placed.Instance.Height = SnapLengthToDiagramGrid(
                placed.Instance.Height, minH);
            RenderDevice(placed);
            UpdateCanvasExtent();
        }

        private void AddSelectionVisuals(PlacedDevice placed, bool showHandles)
        {
            var c = placed.Container;
            var inst = placed.Instance;

            var marker = new Rectangle
            {
                Width = inst.Width, Height = inst.Height,
                Stroke = AppThemeManager.GetBrush("AppAccentBrush"), StrokeThickness = 2.2,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = Brushes.Transparent, IsHitTestVisible = false
            };
            Canvas.SetLeft(marker, 0);
            Canvas.SetTop(marker, 0);
            c.Children.Add(marker);

            if (!showHandles) return; // 여러 개 선택 중이면 크기조절 핸들은 숨긴다 (서로 크기가 달라 혼란스러움)

            AddHandle(placed, HandleKind.TopLeft, 0, 0, Cursors.SizeNWSE);
            AddHandle(placed, HandleKind.Top, inst.Width / 2, 0, Cursors.SizeNS);
            AddHandle(placed, HandleKind.TopRight, inst.Width, 0, Cursors.SizeNESW);
            AddHandle(placed, HandleKind.Left, 0, inst.Height / 2, Cursors.SizeWE);
            AddHandle(placed, HandleKind.Right, inst.Width, inst.Height / 2, Cursors.SizeWE);
            AddHandle(placed, HandleKind.BottomLeft, 0, inst.Height, Cursors.SizeNESW);
            AddHandle(placed, HandleKind.Bottom, inst.Width / 2, inst.Height, Cursors.SizeNS);
            AddHandle(placed, HandleKind.BottomRight, inst.Width, inst.Height, Cursors.SizeNWSE);
        }

        private void AddHandle(PlacedDevice placed, HandleKind kind, double x, double y, Cursor cursor)
        {
            var handle = new Rectangle
            {
                Width = HandleSize, Height = HandleSize,
                Fill = Brushes.White, Stroke = AppThemeManager.GetBrush("AppAccentBrush"), StrokeThickness = 1,
                Cursor = cursor, Tag = kind
            };
            Canvas.SetLeft(handle, x - HandleSize / 2);
            Canvas.SetTop(handle, y - HandleSize / 2);
            handle.MouseLeftButtonDown += Handle_MouseLeftButtonDown;
            placed.Container.Children.Add(handle);
        }

        // ================= 선택 상태 관리 (다중 선택) =================
        private void ClearSelection()
        {
            ClearSelectedDevicePort();
            if (_selectedSet.Count == 0)
            {
                UpdateTextFormatPalette();
                return;
            }
            var old = _selectedSet.ToList();
            _selectedSet.Clear();
            foreach (var p in old) RenderDevice(p);
            UpdateTextFormatPalette();
        }

        private void SelectAdd(IEnumerable<PlacedDevice> items)
        {
            bool changed = false;
            foreach (var p in items)
                if (_selectedSet.Add(p)) changed = true;
            if (changed)
                foreach (var p in _selectedSet) RenderDevice(p); // 개수 변화로 핸들 표시여부가 바뀌므로 전체 재렌더
            UpdateTextFormatPalette();
        }

        private void SelectOnly(PlacedDevice placed)
        {
            if (_selectedShape != null) SelectShape(null);
            ClearSelection();
            SelectAdd(new[] { placed });
        }

        private void ToggleSelect(PlacedDevice placed)
        {
            if (_selectedSet.Contains(placed)) _selectedSet.Remove(placed);
            else _selectedSet.Add(placed);
            foreach (var p in _placed) if (_selectedSet.Contains(p) || p == placed) RenderDevice(p);
            UpdateTextFormatPalette();
        }

        // ================= 우클릭 메뉴 =================
        private void Container_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Canvas c) return;
            var placed = _placed.FirstOrDefault(p => p.Container == c);
            if (placed == null) return;

            if (!_selectedSet.Contains(placed)) SelectOnly(placed);

            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem("수정...", () => OpenEditForInstance(placed)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(_selectedSet.Count > 1 ? $"복사 ({_selectedSet.Count}개)" : "복사",
                                        DuplicateSelected));
            menu.Items.Add(MakeMenuItem(_selectedSet.Count > 1 ? $"삭제 ({_selectedSet.Count}개)" : "삭제",
                                        DeleteSelected));

            c.ContextMenu = menu;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private static MenuItem MakeMenuItem(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, __) => action();
            return mi;
        }

        private void DuplicateSelected()
        {
            var toCopy = _selectedSet.ToList();
            if (toCopy.Count == 0) return;

            var newOnes = new List<PlacedDevice>();
            foreach (var placed in toCopy)
            {
                var newInstance = new DeviceInstance
                {
                    DeviceId = placed.Instance.DeviceId,
                    Width = placed.Instance.Width,
                    Height = placed.Instance.Height,
                    Purpose = placed.Instance.Purpose,
                    X = placed.Instance.X + 20,
                    Y = placed.Instance.Y + 20,
                    Override = placed.Instance.Override == null ? null : CloneDevice(placed.Instance.Override)
                };
                var newPlaced = CreatePlaced(placed.TemplateDevice, newInstance);
                MainCanvas.Children.Add(newPlaced.Container);
                _placed.Add(newPlaced);
                RenderDevice(newPlaced);
                newOnes.Add(newPlaced);
            }
            ClearSelection();
            SelectAdd(newOnes);
            UpdateCanvasExtent();
            _isDirty = true;
        }

        // ================= 포트 드래그 (스마트 가이드 포함) =================
        private void PortHandle_MouseLeftButtonDown(PlacedDevice placed, Port port, MouseButtonEventArgs e)
        {
            _portDragPlaced = placed;
            var editable = EnsureEditableDevice(placed);
            MaterializeDevicePortSlots(placed, editable);
            _portDragPort = editable.Ports.FirstOrDefault(candidate => candidate.Id == port.Id)
                            ?? editable.Ports.FirstOrDefault(candidate =>
                                candidate.Designator.Equals(port.Designator,
                                    StringComparison.OrdinalIgnoreCase));
            if (_portDragPort == null) return;
            _selectedDevicePortPlaced = placed;
            _selectedDevicePort = _portDragPort;
            RefreshSelectedDevicePortVisuals();
            placed.Container.CaptureMouse();
            e.Handled = true;
        }

        private void ClearSelectedDevicePort()
        {
            if (_selectedDevicePortPlaced == null && _selectedDevicePort == null)
                return;
            _selectedDevicePortPlaced = null;
            _selectedDevicePort = null;
            RefreshSelectedDevicePortVisuals();
        }

        private void RefreshSelectedDevicePortVisuals()
        {
            foreach (var placed in _placed)
            foreach (var rect in placed.Container.Children
                         .OfType<Rectangle>()
                         .Where(item => item.Tag is Port))
            {
                var port = (Port)rect.Tag;
                bool selected = ReferenceEquals(
                                    placed, _selectedDevicePortPlaced) &&
                                _selectedDevicePort?.Id == port.Id;
                rect.Stroke = selected
                    ? AppThemeManager.GetBrush("AppAccentBrush")
                    : Brushes.Transparent;
                rect.StrokeThickness = selected ? 2.2 : 0;
            }
        }

        private void MoveSelectedDevicePortAroundPerimeter(bool clockwise)
        {
            if (_selectedDevicePortPlaced == null ||
                _selectedDevicePort == null)
                return;

            var placed = _selectedDevicePortPlaced;
            var device = EnsureEditableDevice(placed);
            MaterializeDevicePortSlots(placed, device);
            var selected = device.Ports.FirstOrDefault(port =>
                port.Id == _selectedDevicePort.Id);
            if (selected == null) return;

            var slots = new List<(PortSide Side, int Index)>();
            int topCount = DevicePreviewRenderer.GetPortSlotCount(
                placed.Instance.Width);
            int sideCount = DevicePreviewRenderer.GetPortSlotCount(
                placed.Instance.Height);
            for (int index = 0; index < topCount; index++)
                slots.Add((PortSide.Top, index));
            for (int index = 0; index < sideCount; index++)
                slots.Add((PortSide.Right, index));
            for (int index = topCount - 1; index >= 0; index--)
                slots.Add((PortSide.Bottom, index));
            for (int index = sideCount - 1; index >= 0; index--)
                slots.Add((PortSide.Left, index));
            if (slots.Count == 0) return;

            int SlotIndex(Port port)
            {
                double axisLength = port.Side is PortSide.Top or PortSide.Bottom
                    ? placed.Instance.Width
                    : placed.Instance.Height;
                int localIndex = DevicePreviewRenderer.GetPortSlotIndex(
                    port.Position, axisLength);
                return slots.FindIndex(slot =>
                    slot.Side == port.Side && slot.Index == localIndex);
            }

            void ApplySlot(Port port, int perimeterIndex)
            {
                var slot = slots[perimeterIndex];
                double axisLength = slot.Side is PortSide.Top or PortSide.Bottom
                    ? placed.Instance.Width
                    : placed.Instance.Height;
                port.Side = slot.Side;
                port.Position = ((slot.Index + 0.5) *
                                 DevicePreviewRenderer.PortSlotSize) /
                                axisLength;
            }

            int current = SlotIndex(selected);
            if (current < 0) return;
            int direction = clockwise ? 1 : -1;
            int Step(int index, int amount) =>
                (index + amount + slots.Count) % slots.Count;

            var occupied = new Dictionary<int, Port>();
            foreach (var port in device.Ports)
            {
                if (port.Id == selected.Id) continue;
                int index = SlotIndex(port);
                if (index >= 0 && !occupied.ContainsKey(index))
                    occupied[index] = port;
            }

            int target = Step(current, direction);
            // 선택 포트가 다음 자리에 끼어들면 그 자리에 있던 포트는
            // 진행 방향으로 정확히 한 칸만 민다. 선택 포트의 이전 자리로
            // 되돌려 보내면 모서리에서 반대 변으로 튀어 보이므로 금지한다.
            if (occupied.TryGetValue(target, out Port? displaced))
            {
                int displacedTarget = Step(target, direction);
                if (occupied.ContainsKey(displacedTarget))
                {
                    StatusText.Text =
                        "다음 칸도 사용 중이라 선택 포트를 이동할 수 없습니다.";
                    return;
                }
                ApplySlot(displaced, displacedTarget);
            }
            ApplySlot(selected, target);

            _selectedDevicePort = selected;
            RenderDevice(placed);
            RenderAllCables();
            UpdateCanvasExtent();
            _isDirty = true;
            StatusText.Text = clockwise
                ? "선택한 장치 포트를 시계방향으로 한 칸 이동했습니다."
                : "선택한 장치 포트를 반시계방향으로 한 칸 이동했습니다.";
        }

        private static void MaterializeDevicePortSlots(
            PlacedDevice placed, Device editable)
        {
            foreach (var side in new[]
                     {
                         PortSide.Left, PortSide.Right,
                         PortSide.Top, PortSide.Bottom
                     })
            {
                var ports = editable.Ports.Where(port => port.Side == side).ToList();
                double sideLength = side is PortSide.Top or PortSide.Bottom
                    ? placed.Instance.Width
                    : placed.Instance.Height;
                for (int index = 0; index < ports.Count; index++)
                {
                    if (ports[index].Position >= 0) continue;
                    ports[index].Position = DevicePreviewRenderer.GetPortSlotFraction(
                        ports[index], index, ports.Count, sideLength);
                }
            }
        }

        /// <summary>
        /// 포트는 장치 외곽의 20px 셀 하나를 점유하며 그 셀의 중앙에 놓인다.
        /// 6x4칸 장치는 위/아래 각 6개, 좌/우 각 4개의 위치를 제공한다.
        /// </summary>
        private static double? SnapPortPosition(
            PlacedDevice placed, Port dragPort, PortSide side, double frac)
        {
            double axisLength = side is PortSide.Top or PortSide.Bottom
                ? placed.Instance.Width
                : placed.Instance.Height;
            if (axisLength <= 0.001) return 0.5;

            int slotCount = DevicePreviewRenderer.GetPortSlotCount(axisLength);
            int desiredSlot = DevicePreviewRenderer.GetPortSlotIndex(
                frac, axisLength);

            var siblings = placed.EffectiveDevice.Ports
                .Where(port => port.Side == side)
                .ToList();
            var occupied = new HashSet<int>();
            for (int index = 0; index < siblings.Count; index++)
            {
                var port = siblings[index];
                if (ReferenceEquals(port, dragPort) || port.Id == dragPort.Id)
                    continue;
                double effective = DevicePreviewRenderer.GetPortSlotFraction(
                    port, index, siblings.Count, axisLength);
                occupied.Add(DevicePreviewRenderer.GetPortSlotIndex(
                    effective, axisLength));
            }

            int? slot = Enumerable.Range(0, slotCount)
                .Where(candidate => !occupied.Contains(candidate))
                .OrderBy(candidate => Math.Abs(candidate - desiredSlot))
                .ThenBy(candidate => candidate)
                .Cast<int?>()
                .FirstOrDefault();
            if (!slot.HasValue) return null;

            return ((slot + 0.5) * DevicePreviewRenderer.PortSlotSize) /
                   axisLength;
        }

        // ================= 가장자리 자동 스크롤 =================
        private const double AutoScrollEdge = 40;   // 이 거리(px) 안으로 들어오면 스크롤 시작
        private const double AutoScrollStep = 22;   // 한 번에 스크롤할 양(px)

        /// <summary>드래그 중 마우스가 화면 가장자리에 가까우면 그 방향으로 뷰포트를 조금씩 밀어준다.</summary>
        private void AutoScrollIfNearEdge(MouseEventArgs e)
        {
            var pos = e.GetPosition(MainScrollViewer);
            double vw = MainScrollViewer.ActualWidth;
            double vh = MainScrollViewer.ActualHeight;

            if (pos.X < AutoScrollEdge)
                MainScrollViewer.ScrollToHorizontalOffset(MainScrollViewer.HorizontalOffset - AutoScrollStep);
            else if (pos.X > vw - AutoScrollEdge)
                MainScrollViewer.ScrollToHorizontalOffset(MainScrollViewer.HorizontalOffset + AutoScrollStep);

            if (pos.Y < AutoScrollEdge)
                MainScrollViewer.ScrollToVerticalOffset(MainScrollViewer.VerticalOffset - AutoScrollStep);
            else if (pos.Y > vh - AutoScrollEdge)
                MainScrollViewer.ScrollToVerticalOffset(MainScrollViewer.VerticalOffset + AutoScrollStep);
        }

        // ================= 마우스: 이동 / 크기조절 / 더블클릭 =================
        private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Rectangle handle) return;
            if (handle.Tag is not HandleKind kind) return;
            var placed = _placed.FirstOrDefault(p => p.Container == handle.Parent as Canvas);
            if (placed == null) return;

            _resizingDevice = placed;
            _resizeKind = kind;
            _resizeStartMouse = e.GetPosition(MainCanvas);
            _rsLeft = placed.Instance.X;
            _rsTop = placed.Instance.Y;
            _rsWidth = placed.Instance.Width;
            _rsHeight = placed.Instance.Height;

            placed.Container.CaptureMouse();
            e.Handled = true;
        }

        private void Container_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Canvas c) return;
            var placed = _placed.FirstOrDefault(p => p.Container == c);
            if (placed == null) return;

            CommitActiveTextEdit();
            DeselectCable();

            if (e.ClickCount == 2)
            {
                Point click = e.GetPosition(c);
                var body = c.Children.OfType<Border>().FirstOrDefault();
                var namePlate = c.Children.OfType<Border>().FirstOrDefault(
                    border => border.Tag is
                        DevicePreviewRenderer.DeviceNameVisualInfo);
                FrameworkElement? viewer = namePlate?.Child as FrameworkElement
                                           ?? body?.Child as FrameworkElement;
                bool clickedText;
                if (namePlate != null)
                {
                    Point pointInPlate = c.TranslatePoint(click, namePlate);
                    clickedText = new Rect(
                        0, 0, namePlate.ActualWidth, namePlate.ActualHeight)
                        .Contains(pointInPlate);
                }
                else
                {
                    clickedText = viewer != null &&
                        RichTextSupport.IsPointOverText(
                            viewer, c.TranslatePoint(click, viewer));
                }

                if (clickedText)
                    BeginDeviceTextEdit(placed, click);
                else
                {
                    SelectOnly(placed);
                    OpenEditForInstance(placed);
                }
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                ToggleSelect(placed);
                e.Handled = true;
                return; // Ctrl+클릭은 선택만 토글, 이동은 시작하지 않는다
            }

            // 이미 다중 선택에 포함된 걸 다시 클릭하면 포트 선택까지 유지한 채 그룹 전체를 이동
            if (!_selectedSet.Contains(placed))
            {
                ClearCablePortSelection();
                SelectOnly(placed);
            }

            _movingAnchor = placed;
            _moveStartMouse = e.GetPosition(MainCanvas);
            _moveStartPositions = _selectedSet.ToDictionary(p => p, p => new Point(p.Instance.X, p.Instance.Y));
            _shapeMoveStartPositions = _selectedShapes.ToDictionary(
                item => item, item => new Point(item.Shape.X, item.Shape.Y));
            CaptureGroupCableGeometry();

            c.CaptureMouse();
            e.Handled = true;
        }

        private void Container_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var mouse = e.GetPosition(MainCanvas);

            if (_devicePortLabelDragPlaced != null &&
                _devicePortLabelDragPort != null &&
                _devicePortLabelDragVisual != null)
            {
                AutoScrollIfNearEdge(e);
                double dx = mouse.X - _devicePortLabelDragStartMouse.X;
                double dy = mouse.Y - _devicePortLabelDragStartMouse.Y;
                Point constrainedOffset = ConstrainDevicePortLabelOffset(
                    _devicePortLabelDragPort,
                    _devicePortLabelDragVisual,
                    _devicePortLabelDragAnchorX,
                    _devicePortLabelDragAnchorY,
                    _devicePortLabelDragOrigX + dx,
                    _devicePortLabelDragOrigY + dy);
                double offsetX = constrainedOffset.X;
                double offsetY = constrainedOffset.Y;
                _devicePortLabelDragPort.HasCustomLabelPosition = true;
                _devicePortLabelDragPort.LabelOffsetX = offsetX;
                _devicePortLabelDragPort.LabelOffsetY = offsetY;
                Canvas.SetLeft(_devicePortLabelDragVisual,
                    _devicePortLabelDragAnchorX + offsetX);
                Canvas.SetTop(_devicePortLabelDragVisual,
                    _devicePortLabelDragAnchorY + offsetY);
                return;
            }

            if (_portDragPlaced != null && _portDragPort != null)
            {
                AutoScrollIfNearEdge(e);
                double localX = mouse.X - _portDragPlaced.Instance.X;
                double localY = mouse.Y - _portDragPlaced.Instance.Y;
                var (side, rawFrac) = DevicePreviewRenderer.NearestEdge(
                    localX, localY, _portDragPlaced.Instance.Width, _portDragPlaced.Instance.Height);

                double? frac = SnapPortPosition(
                    _portDragPlaced, _portDragPort, side, rawFrac);
                if (!frac.HasValue) return;

                _portDragPort.Side = side;
                _portDragPort.Position = frac.Value;
                RenderDevice(_portDragPlaced);
                // 포트 드래그 중에도 연결된 케이블이 같은 포트 ID의 새 위치를 따라온다.
                QueueDeviceCableDragRender(_portDragPlaced);
                return;
            }

            if (_resizingDevice != null) { AutoScrollIfNearEdge(e); ResizeTo(_resizingDevice, mouse); return; }

            if (_movingAnchor != null)
            {
                AutoScrollIfNearEdge(e);
                ApplyGroupMove(mouse);
            }
        }

        private bool MovesWithCurrentGroup(CablePortLayout layout)
        {
            if (_selectedCablePorts.Contains(layout)) return true;
            return ConnectionTargetMovesWithCurrentGroup(layout);
        }

        private bool ConnectionTargetMovesWithCurrentGroup(CablePortLayout layout)
        {
            string? key = layout.ConnectedToPortKey;
            if (string.IsNullOrWhiteSpace(key)) return false;
            var parts = key.Split(':');
            if (parts.Length != 3) return false;
            if (parts[0] == "dev")
                return _selectedSet.Any(item => item.Instance.Id == parts[1]);
            if (parts[0] == "cab")
            {
                var target = FindPortLayoutById(parts[1], parts[2]);
                return target != null && _selectedCablePorts.Contains(target);
            }
            return false;
        }

        private string? GetCablePortKey(CablePortLayout target)
        {
            foreach (var placed in _placedCables)
            foreach (var layout in AllPortLayouts(placed.Instance))
                if (ReferenceEquals(layout, target))
                    return $"cab:{placed.Instance.Id}:{layout.ConnectorId}";
            return null;
        }

        private CablePortLayout? GetConnectedCablePort(CablePortLayout layout)
        {
            string? targetKey = layout.ConnectedToPortKey;
            if (!string.IsNullOrWhiteSpace(targetKey))
            {
                var parts = targetKey.Split(':');
                if (parts.Length == 3 && parts[0] == "cab")
                    return FindPortLayoutById(parts[1], parts[2]);
            }

            string? ownKey = GetCablePortKey(layout);
            if (ownKey == null) return null;
            return _placedCables
                .SelectMany(item => AllPortLayouts(item.Instance))
                .FirstOrDefault(other => string.Equals(
                    other.ConnectedToPortKey, ownKey,
                    StringComparison.Ordinal));
        }

        private void CaptureGroupCableGeometry()
        {
            _groupMovePortStarts.Clear();
            _groupMoveBendStarts.Clear();

            foreach (var selectedPort in _selectedCablePorts)
                _groupMovePortStarts[selectedPort] =
                    new Point(selectedPort.PortX, selectedPort.PortY);

            // 케이블끼리 맞물린 포트는 연결 정보가 한쪽에만 저장된다. 선택된
            // 포트의 상대편도 이동 스냅샷에 포함해야 전체 이동 시 접점만 남지 않는다.
            bool addedPartner;
            do
            {
                addedPartner = false;
                foreach (var layout in _groupMovePortStarts.Keys.ToList())
                {
                    var partner = GetConnectedCablePort(layout);
                    if (partner == null || _groupMovePortStarts.ContainsKey(partner))
                        continue;
                    _groupMovePortStarts[partner] =
                        new Point(partner.PortX, partner.PortY);
                    addedPartner = true;
                }
            } while (addedPartner);

            foreach (var placed in _placedCables)
            {
                var layouts = AllPortLayouts(placed.Instance).ToList();
                if (layouts.Count == 0 || !layouts.All(MovesWithCurrentGroup))
                    continue;

                foreach (var layout in layouts)
                    _groupMovePortStarts[layout] =
                        new Point(layout.PortX, layout.PortY);
                foreach (var bend in placed.Instance.RootBends)
                    _groupMoveBendStarts[bend] = new Point(bend.X, bend.Y);
                foreach (var bend in placed.Instance.Branches.SelectMany(branch => branch.Bends))
                    _groupMoveBendStarts[bend] = new Point(bend.X, bend.Y);
            }
        }

        private void ApplyGroupMove(Point mouse, bool snapToGrid = true)
        {
            double dx = mouse.X - _moveStartMouse.X;
            double dy = mouse.Y - _moveStartMouse.Y;

            Point? gridAnchor = null;
            if (_moveStartPositions.Count > 0)
                gridAnchor = _moveStartPositions.Values.First();
            else if (_groupMovePortStarts.Count > 0)
                gridAnchor = _groupMovePortStarts.Values.First();

            if (snapToGrid && gridAnchor.HasValue)
            {
                dx = SnapToDiagramGrid(dx);
                dy = SnapToDiagramGrid(dy);
            }

            if (_moveStartPositions.Count > 0)
            {
                var movingDevices = _moveStartPositions.Keys.ToHashSet();
                bool blocked = _moveStartPositions.Any(pair =>
                    !IsDevicePlacementClear(
                        new Rect(
                            pair.Value.X + dx,
                            pair.Value.Y + dy,
                            pair.Key.Instance.Width,
                            pair.Key.Instance.Height),
                        pair.Key.EffectiveDevice,
                        movingDevices));
                if (blocked) return;
            }

            foreach (var pair in _moveStartPositions)
            {
                pair.Key.Instance.X = pair.Value.X + dx;
                pair.Key.Instance.Y = pair.Value.Y + dy;
                Canvas.SetLeft(pair.Key.Container, pair.Key.Instance.X);
                Canvas.SetTop(pair.Key.Container, pair.Key.Instance.Y);
            }

            foreach (var pair in _shapeMoveStartPositions)
            {
                pair.Key.Shape.X = pair.Value.X + dx;
                pair.Key.Shape.Y = pair.Value.Y + dy;
                RenderShape(pair.Key);
            }

            foreach (var pair in _groupMovePortStarts)
            {
                var layout = pair.Key;
                var connectedPartner = GetConnectedCablePort(layout);
                bool partnerMoves = connectedPartner != null &&
                                    _groupMovePortStarts.ContainsKey(connectedPartner);
                if (_selectedCablePorts.Contains(layout) &&
                    !ConnectionTargetMovesWithCurrentGroup(layout) &&
                    !partnerMoves)
                    layout.ConnectedToPortKey = null;
                layout.PortX = pair.Value.X + dx;
                layout.PortY = pair.Value.Y + dy;
            }
            foreach (var pair in _groupMoveBendStarts)
            {
                pair.Key.X = pair.Value.X + dx;
                pair.Key.Y = pair.Value.Y + dy;
            }

            foreach (var placed in _moveStartPositions.Keys)
                RenderDevice(placed);
            QueueCableDragRender();
        }

        private void ClearGroupMoveSnapshots()
        {
            _moveStartPositions.Clear();
            _shapeMoveStartPositions.Clear();
            _groupMovePortStarts.Clear();
            _groupMoveBendStarts.Clear();
        }

        private void TranslateCurrentSelection(double dx, double dy)
        {
            _moveStartMouse = new Point(0, 0);
            _moveStartPositions = _selectedSet.ToDictionary(
                item => item,
                item => new Point(item.Instance.X, item.Instance.Y));
            _shapeMoveStartPositions = _selectedShapes.ToDictionary(
                item => item,
                item => new Point(item.Shape.X, item.Shape.Y));
            CaptureGroupCableGeometry();
            // 장치 또는 자유 케이블 커넥터가 포함된 선택은 격자에 맞춘다.
            // 일반 도형만 선택한 경우에는 자유 좌표 이동을 유지한다.
            ApplyGroupMove(new Point(dx, dy), snapToGrid:
                _selectedSet.Count > 0 || _selectedCablePorts.Count > 0);
            ClearGroupMoveSnapshots();
            NormalizeCanvasOriginForNegativeContent();
            UpdateCanvasExtent();
            _isDirty = true;
        }

        private void ResizeTo(PlacedDevice placed, Point mouse)
        {
            double dx = mouse.X - _resizeStartMouse.X;
            double dy = mouse.Y - _resizeStartMouse.Y;
            double left = _rsLeft, top = _rsTop, w = _rsWidth, h = _rsHeight;

            switch (_resizeKind)
            {
                case HandleKind.BottomRight: w = _rsWidth + dx; h = _rsHeight + dy; break;
                case HandleKind.BottomLeft:  w = _rsWidth - dx; h = _rsHeight + dy; break;
                case HandleKind.TopRight:    w = _rsWidth + dx; h = _rsHeight - dy; break;
                case HandleKind.TopLeft:     w = _rsWidth - dx; h = _rsHeight - dy; break;
                case HandleKind.Top:         h = _rsHeight - dy; break;
                case HandleKind.Bottom:      h = _rsHeight + dy; break;
                case HandleKind.Left:        w = _rsWidth - dx; break;
                case HandleKind.Right:       w = _rsWidth + dx; break;
            }

            var (minW, minH) = DevicePreviewRenderer.GetMinSize(placed.EffectiveDevice);
            w = SnapLengthToDiagramGrid(w, minW);
            h = SnapLengthToDiagramGrid(h, minH);

            if (_resizeKind is HandleKind.TopLeft or HandleKind.BottomLeft or HandleKind.Left)
                left = _rsLeft + _rsWidth - w;
            if (_resizeKind is HandleKind.TopLeft or HandleKind.TopRight or HandleKind.Top)
                top = _rsTop + _rsHeight - h;

            left = SnapToDiagramGrid(left);
            top = SnapToDiagramGrid(top);
            if (!IsDevicePlacementClear(
                    new Rect(left, top, w, h),
                    placed.EffectiveDevice,
                    new HashSet<PlacedDevice> { placed }))
                return;

            placed.Instance.X = left;
            placed.Instance.Y = top;
            placed.Instance.Width = w;
            placed.Instance.Height = h;

            RenderDevice(placed);
            QueueDeviceCableDragRender(placed); // 포트 위치가 바뀌므로 붙은 케이블도 갱신
        }

        private void Container_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Canvas c) c.ReleaseMouseCapture();

            if (_devicePortLabelDragPlaced != null)
            {
                var finished = _devicePortLabelDragPlaced;
                _devicePortLabelDragPlaced = null;
                _devicePortLabelDragPort = null;
                _devicePortLabelDragVisual = null;
                RenderDevice(finished);
                UpdateCanvasExtent();
                _isDirty = true;
                return;
            }

            if (_portDragPlaced != null)
            {
                var finished = _portDragPlaced;
                _portDragPlaced = null;
                _portDragPort = null;
                RenderDevice(finished); // 드래그 중 표시된 안내선을 지우기 위해 마지막으로 한 번 더 그림
                if (_placedCables.Count > 0) RenderAllCables();
                UpdateCanvasExtent();
                _isDirty = true;
                return;
            }

            bool wasMovingOrResizing = _movingAnchor != null || _resizingDevice != null;
            _movingAnchor = null;
            _resizingDevice = null;
            ClearGroupMoveSnapshots();

            // 장치를 옮기거나 크기를 바꿔도 사용자가 만든 케이블 굴곡은 보존한다.
            // 연결 끝점만 새 장치 포트 위치를 따라가도록 다시 그린다.
            if (wasMovingOrResizing)
            {
                NormalizeCanvasOriginForNegativeContent();
                if (_placedCables.Count > 0)
                    RenderAllCables();
            }

            UpdateCanvasExtent();
            if (wasMovingOrResizing) _isDirty = true;
        }

        // ================= 빈 캔버스: 영역 선택(마퀴) =================
        private void MainCanvas_PreviewMouseLeftButtonDown(
            object sender, MouseButtonEventArgs e)
        {
            if (_isZoneSettingMode)
            {
                CommitActiveTextEdit();
                Point point = e.GetPosition(MainCanvas);
                if (_isCreatingZone)
                {
                    StartZoneDraw(point);
                }
                else
                {
                    if (TryHitSelectedZoneHandle(point, out HandleKind handle))
                    {
                        StartZoneDrag(_selectedZone!, handle, point);
                    }
                    else
                    {
                        PlacedZone? placed = FindZoneAt(point);
                        SelectZone(placed);
                        if (placed != null)
                        {
                            if (e.ClickCount >= 2)
                                RenameZone(placed);
                            else
                                StartZoneDrag(placed, null, point);
                        }
                    }
                }
                e.Handled = true;
                return;
            }
            if (_armedShapeKind == null) return;
            CommitActiveTextEdit();
            StartShapeDraw(e.GetPosition(MainCanvas));
            e.Handled = true;
        }

        private void MainCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isZoneSettingMode)
            {
                e.Handled = true;
                return;
            }
            CommitActiveTextEdit();
            if (_armedShapeKind != null)
            {
                StartShapeDraw(e.GetPosition(MainCanvas));
                return;
            }

            DeselectCable();
            if (Keyboard.Modifiers != ModifierKeys.Control)
            {
                SelectShape(null);
                ClearSelection();
                ClearCablePortSelection();
            }

            _isMarqueeSelecting = true;
            _marqueeStart = e.GetPosition(MainCanvas);
            _marqueeRect = new Rectangle
            {
                Stroke = AppThemeManager.GetBrush("AppAccentBrush"),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = AppThemeManager.WithAlpha("AppAccentBrush", 40),
                IsHitTestVisible = false,
                Width = 0, Height = 0
            };
            Canvas.SetLeft(_marqueeRect, _marqueeStart.X);
            Canvas.SetTop(_marqueeRect, _marqueeStart.Y);
            MainCanvas.Children.Add(_marqueeRect);
            MainCanvas.CaptureMouse();
        }

        private void MainCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_zoneDragPlaced != null)
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    AutoScrollIfNearEdge(e);
                    UpdateZoneDrag(e.GetPosition(MainCanvas));
                }
                return;
            }
            if (_zoneDrawPreview != null)
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    AutoScrollIfNearEdge(e);
                    UpdateZoneDraw(e.GetPosition(MainCanvas));
                }
                return;
            }
            if (_isZoneSettingMode)
            {
                Point point = e.GetPosition(MainCanvas);
                if (_isCreatingZone)
                    MainCanvas.Cursor = Cursors.Cross;
                else if (TryHitSelectedZoneHandle(point, out HandleKind handle))
                    MainCanvas.Cursor = ZoneCursor(handle);
                else
                    MainCanvas.Cursor = FindZoneAt(point) != null ? Cursors.SizeAll : Cursors.Arrow;
                return;
            }
            if (_shapeDrawElbowPreview != null)
            {
                AutoScrollIfNearEdge(e);
                var cur = e.GetPosition(MainCanvas);
                double middleX = (_shapeDrawStart.X + cur.X) / 2;
                _shapeDrawElbowPreview.Points = new PointCollection
                {
                    _shapeDrawStart,
                    new Point(middleX, _shapeDrawStart.Y),
                    new Point(middleX, cur.Y),
                    cur
                };
                return;
            }
            if (_shapeDrawPreview != null || _shapeDrawPreviewCanvas != null)
            {
                AutoScrollIfNearEdge(e);
                var cur = e.GetPosition(MainCanvas);
                if (IsLineShapeKind(_armedShapeKind) && _shapeDrawPreview != null)
                {
                    var delta = SnapStraightLineDelta(
                        cur.X - _shapeDrawStart.X,
                        cur.Y - _shapeDrawStart.Y);
                    _shapeDrawPreview.X1 = _shapeDrawStart.X; _shapeDrawPreview.Y1 = _shapeDrawStart.Y;
                    _shapeDrawPreview.X2 = _shapeDrawStart.X + delta.X;
                    _shapeDrawPreview.Y2 = _shapeDrawStart.Y + delta.Y;
                }
                else if (_shapeDrawPreviewCanvas != null)
                {
                    UpdateShapeDrawPreview(ConstrainShapeDrawEnd(
                        _shapeDrawStart, cur));
                }
                return;
            }

            if (!_isMarqueeSelecting || _marqueeRect == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            AutoScrollIfNearEdge(e);

            var mcur = e.GetPosition(MainCanvas);
            double mx2 = Math.Min(mcur.X, _marqueeStart.X);
            double my2 = Math.Min(mcur.Y, _marqueeStart.Y);
            double mw2 = Math.Abs(mcur.X - _marqueeStart.X);
            double mh2 = Math.Abs(mcur.Y - _marqueeStart.Y);

            Canvas.SetLeft(_marqueeRect, mx2);
            Canvas.SetTop(_marqueeRect, my2);
            _marqueeRect.Width = mw2;
            _marqueeRect.Height = mh2;
        }

        private void MainCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_zoneDragPlaced != null)
            {
                PlacedZone moved = _zoneDragPlaced;
                bool changed = _zoneDragChanged;
                _zoneDragPlaced = null;
                _zoneDragHandle = null;
                _zoneDragChanged = false;
                if (MainCanvas.IsMouseCaptured) MainCanvas.ReleaseMouseCapture();
                RenderZone(moved);
                if (changed)
                {
                    UpdateCanvasExtent();
                    _isDirty = true;
                    StatusText.Text = $"구역 '{moved.Zone.Name}'의 위치와 크기를 반영했습니다.";
                }
                e.Handled = true;
                return;
            }
            if (_zoneDrawPreview != null)
            {
                FinishZoneDraw(e.GetPosition(MainCanvas));
                e.Handled = true;
                return;
            }
            if (_shapeDrawPreview != null || _shapeDrawElbowPreview != null ||
                _shapeDrawPreviewCanvas != null)
            {
                FinishShapeDraw(e.GetPosition(MainCanvas));
                return;
            }

            if (!_isMarqueeSelecting) return;
            _isMarqueeSelecting = false;
            MainCanvas.ReleaseMouseCapture();

            if (_marqueeRect == null) return;

            double mx = Canvas.GetLeft(_marqueeRect);
            double my = Canvas.GetTop(_marqueeRect);
            double mw = _marqueeRect.Width;
            double mh = _marqueeRect.Height;

            MainCanvas.Children.Remove(_marqueeRect);
            _marqueeRect = null;

            if (mw < 3 && mh < 3) return;

            var selectionBounds = new Rect(mx, my, mw, mh);
            var hits = _placed.Where(p => selectionBounds.Contains(new Rect(
                p.Instance.X, p.Instance.Y, p.Instance.Width, p.Instance.Height))).ToList();

            var shapeHits = _placedShapes.Where(p =>
            {
                return selectionBounds.Contains(ShapeGeometry.Bounds(p.Shape));
            }).ToList();

            // 영역 안에 들어온 케이블 포트도 선택에 포함한다. 케이블끼리
            // 맞물린 포트는 이동 시작 시 상대편까지 확장되므로 접점이
            // 캔버스에 남지 않고 케이블 형상을 유지한 채 함께 움직인다.
            var portHits = _placedCables
                .SelectMany(placed => AllPortLayouts(placed.Instance).Select(layout => (placed, layout)))
                .Where(item =>
                {
                    var facing = GetEffectiveCablePortFacing(item.placed, item.layout);
                    bool horizontal = facing is PortSide.Left or PortSide.Right;
                    double halfW = (horizontal ? CableRenderer.PortW : CableRenderer.PortH) / 2;
                    double halfH = (horizontal ? CableRenderer.PortH : CableRenderer.PortW) / 2;
                    return item.layout.PortX - halfW >= mx && item.layout.PortX + halfW <= mx + mw &&
                        item.layout.PortY - halfH >= my && item.layout.PortY + halfH <= my + mh;
                })
                .Select(item => item.layout)
                .ToList();

            if (hits.Count > 0) SelectAdd(hits);
            if (shapeHits.Count > 0) SelectAddShapes(shapeHits);
            foreach (var layout in portHits)
                _selectedCablePorts.Add(layout);
            if (portHits.Count > 0)
            {
                RenderAllCables();
                UpdateTextFormatPalette();
            }
        }

        // ---- 도구 활성화 후 캔버스에서 끌어서 도형 그리기 ----
        private string? _armedShapeKind;
        private Point _shapeDrawStart;
        private Canvas? _shapeDrawPreviewCanvas;  // 실제 도형 윤곽 미리보기
        private Line? _shapeDrawPreview;          // 선(Line)용 미리보기
        private Polyline? _shapeDrawElbowPreview;

        private void SetArmedShapeKind(string? kind)
        {
            if (_isZoneSettingMode && kind != null) return;
            _armedShapeKind = kind;
            bool isDrawingMode = kind != null;
            MainCanvas.ForceCursor = isDrawingMode;
            MainCanvas.Cursor = isDrawingMode ? Cursors.Cross : null;
        }

        private void DisarmShapeTool()
        {
            SetArmedShapeKind(null);
            ShapeGalleryButton.Content = "도형 삽입 ▾";
            foreach (var button in new[]
                { ShapeToolRect, ShapeToolEllipse, ShapeToolLine, ShapeToolText })
                button.IsChecked = false;
        }

        private void CancelShapeInsertion()
        {
            if (_shapeDrawPreview != null)
                MainCanvas.Children.Remove(_shapeDrawPreview);
            if (_shapeDrawElbowPreview != null)
                MainCanvas.Children.Remove(_shapeDrawElbowPreview);
            if (_shapeDrawPreviewCanvas != null)
                MainCanvas.Children.Remove(_shapeDrawPreviewCanvas);

            _shapeDrawPreview = null;
            _shapeDrawElbowPreview = null;
            _shapeDrawPreviewCanvas = null;
            if (MainCanvas.IsMouseCaptured)
                MainCanvas.ReleaseMouseCapture();
            DisarmShapeTool();
        }

        private static Point ConstrainShapeDrawEnd(Point start, Point current)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                return current;

            double dx = current.X - start.X;
            double dy = current.Y - start.Y;
            double size = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (size < 0.01) return current;
            return new Point(
                start.X + (dx < 0 ? -size : size),
                start.Y + (dy < 0 ? -size : size));
        }

        private void UpdateShapeDrawPreview(Point current)
        {
            if (_shapeDrawPreviewCanvas == null || _armedShapeKind == null) return;
            current = ConstrainShapeDrawEnd(_shapeDrawStart, current);

            double x = Math.Min(current.X, _shapeDrawStart.X);
            double y = Math.Min(current.Y, _shapeDrawStart.Y);
            double width = Math.Max(1, Math.Abs(current.X - _shapeDrawStart.X));
            double height = Math.Max(1, Math.Abs(current.Y - _shapeDrawStart.Y));

            Canvas.SetLeft(_shapeDrawPreviewCanvas, x);
            Canvas.SetTop(_shapeDrawPreviewCanvas, y);
            _shapeDrawPreviewCanvas.Width = width;
            _shapeDrawPreviewCanvas.Height = height;
            _shapeDrawPreviewCanvas.Children.Clear();
            _shapeDrawPreviewCanvas.Children.Add(
                CreateShapeDrawPreviewElement(_armedShapeKind, width, height));
        }

        private static FrameworkElement CreateShapeDrawPreviewElement(string kind, double width, double height)
        {
            var stroke = AppThemeManager.GetBrush("AppAccentBrush");
            var fill = AppThemeManager.WithAlpha("AppAccentBrush", 35);
            var outline = ShapeCatalog.Outline(ShapeCatalog.Create(kind, width, height), fill, stroke, 1.5);
            outline.IsHitTestVisible = false;
            return outline;
        }

        private void StartShapeDraw(Point start)
        {
            _shapeDrawStart = start;
            if (_armedShapeKind == "ElbowLine")
            {
                _shapeDrawElbowPreview = new Polyline
                {
                    Stroke = Brushes.Gray,
                    StrokeThickness = 1.5,
                    StrokeDashArray = { 4, 2 },
                    Points = new PointCollection { start, start }
                };
                MainCanvas.Children.Add(_shapeDrawElbowPreview);
            }
            else if (IsLineShapeKind(_armedShapeKind))
            {
                _shapeDrawPreview = new Line
                {
                    X1 = start.X, Y1 = start.Y, X2 = start.X, Y2 = start.Y,
                    Stroke = Brushes.Gray, StrokeThickness = 1.5, StrokeDashArray = { 4, 2 }
                };
                MainCanvas.Children.Add(_shapeDrawPreview);
            }
            else
            {
                _shapeDrawPreviewCanvas = new Canvas
                {
                    Width = 1,
                    Height = 1,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(_shapeDrawPreviewCanvas, start.X);
                Canvas.SetTop(_shapeDrawPreviewCanvas, start.Y);
                MainCanvas.Children.Add(_shapeDrawPreviewCanvas);
                UpdateShapeDrawPreview(start);
            }
            MainCanvas.CaptureMouse();
        }

        private void FinishShapeDraw(Point end)
        {
            MainCanvas.ReleaseMouseCapture();
            string kind = _armedShapeKind!;

            CanvasShape shape;
            if (IsLineShapeKind(kind))
            {
                if (_shapeDrawPreview != null)
                    MainCanvas.Children.Remove(_shapeDrawPreview);
                if (_shapeDrawElbowPreview != null)
                    MainCanvas.Children.Remove(_shapeDrawElbowPreview);
                _shapeDrawPreview = null;
                _shapeDrawElbowPreview = null;

                double dx = end.X - _shapeDrawStart.X, dy = end.Y - _shapeDrawStart.Y;
                if (Math.Abs(dx) < 3 && Math.Abs(dy) < 3) { dx = 140; dy = 0; } // 그냥 클릭만 했으면 기본 길이
                if (kind == "Line")
                {
                    var snapped = SnapStraightLineDelta(dx, dy);
                    dx = snapped.X;
                    dy = snapped.Y;
                }
                shape = new CanvasShape
                {
                    Kind = kind,
                    X = _shapeDrawStart.X,
                    Y = _shapeDrawStart.Y,
                    Width = dx,
                    Height = dy,
                    ZOrder = GetNextShapeZOrder(),
                    TextAlignment = "Center"
                };
                if (kind == "ElbowLine" && Math.Abs(dx) >= 0.01 && Math.Abs(dy) >= 0.01)
                {
                    double middleX = dx / 2;
                    shape.LinePoints = new List<ESDP.Models.Point>
                    {
                        new() { X = 0, Y = 0 },
                        new() { X = middleX, Y = 0 },
                        new() { X = middleX, Y = dy },
                        new() { X = dx, Y = dy }
                    };
                }
            }
            else
            {
                MainCanvas.Children.Remove(_shapeDrawPreviewCanvas!);
                _shapeDrawPreviewCanvas = null;

                end = ConstrainShapeDrawEnd(_shapeDrawStart, end);

                double x = Math.Min(end.X, _shapeDrawStart.X), y = Math.Min(end.Y, _shapeDrawStart.Y);
                double w = Math.Abs(end.X - _shapeDrawStart.X), h = Math.Abs(end.Y - _shapeDrawStart.Y);
                if (w < 5 || h < 5) { w = 120; h = kind == "Text" ? 30 : 60; } // 그냥 클릭만 했으면 기본 크기

                shape = new CanvasShape
                {
                    Kind = kind,
                    X = x,
                    Y = y,
                    Width = w,
                    Height = h,
                    UseShapeTextArea = true,
                    Adjustment1 = kind == "RoundedRectangle" ? .18 : null,
                    ZOrder = GetNextShapeZOrder(),
                    TextAlignment = "Center"
                };
                if (kind == "Text")
                {
                    shape.Text = "텍스트";
                    shape.FillHex = "#00FFFFFF";
                }
            }

            var placed = CreatePlacedShape(shape);
            MainCanvas.Children.Add(placed.Container);
            _placedShapes.Add(placed);
            ApplyShapeZOrder(placed);
            RenderShape(placed);
            SelectShape(placed);
            UpdateCanvasExtent();

            // 한 번 그리면 도구는 해제되고 선택 모드로 돌아온다(파워포인트/비지오와 동일)
            DisarmShapeTool();
            _isDirty = true;
        }

        private void ShapeTool_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton btn) return;
            string kind = (string)btn.Tag;

            foreach (var b in new[] { ShapeToolRect, ShapeToolEllipse, ShapeToolLine, ShapeToolText })
                if (b != btn) b.IsChecked = false;

            SetArmedShapeKind(btn.IsChecked == true ? kind : null);
        }

        private void ShapeGalleryButton_Click(
            object sender, RoutedEventArgs e)
        {
            PopulateShapeGallery();
            ShapeGalleryPopup.IsOpen = !ShapeGalleryPopup.IsOpen;
        }

        private void ShapeGalleryItem_Click(
            object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string kind } button) return;
            foreach (var b in new[]
                { ShapeToolRect, ShapeToolEllipse, ShapeToolLine, ShapeToolText })
                b.IsChecked = false;
            SetArmedShapeKind(kind);
            ShapeGalleryButton.Content = $"도형: {ShapeCatalog.Name(kind)}";
            ShapeGalleryPopup.IsOpen = false;
        }

        private void ArmShapeTool(string kind, ToggleButton btn)
        {
            foreach (var b in new[] { ShapeToolRect, ShapeToolEllipse, ShapeToolLine, ShapeToolText })
                b.IsChecked = (b == btn);
            SetArmedShapeKind(kind);
        }

        // ================= 메뉴 =================
        // ================= 편집 메뉴: 복사/붙여넣기/전체선택/실행취소 =================
        private object? _clipboard; // Device(장치) 하나 또는 CanvasShape(도형) 하나

        private void Menu_SelectAll(object sender, RoutedEventArgs e)
        {
            SelectShape(null);
            DeselectCable();
            ClearSelection();
            ClearCablePortSelection(render: false);
            SelectAdd(_placed);
            SelectAddShapes(_placedShapes);
            foreach (var layout in _placedCables.SelectMany(pc => AllPortLayouts(pc.Instance)))
                _selectedCablePorts.Add(layout);
            RenderAllCables();
            UpdateTextFormatPalette();
        }

        private void Menu_Copy(object sender, RoutedEventArgs e)
        {
            if (_selectedShape != null) _clipboard = _selectedShapes.Select(p => ShapeCatalog.Clone(p.Shape)).ToList();
            else if (_selectedSet.Count == 1) _clipboard = _selectedSet.First().Instance;
            else if (_selectedSet.Count > 1)
            {
                MessageBox.Show("여러 개를 한 번에 복사하는 건 아직 지원하지 않습니다. Ctrl+D(복제)를 대신 써보세요.",
                                "ESDP", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void Menu_Paste(object sender, RoutedEventArgs e)
        {
            if (_clipboard is List<CanvasShape> shapes) AddShapeCopies(shapes);
            else if (_clipboard is CanvasShape shape) AddShapeCopies(new[] { shape });
            else if (_clipboard is DeviceInstance instTemplate)
            {
                var template = _placed.FirstOrDefault(p => p.Instance == instTemplate)?.TemplateDevice
                                ?? _registeredDevices.FirstOrDefault(d => d.Device.Id == instTemplate.DeviceId)?.Device;
                if (template == null) return;

                var newInstance = new DeviceInstance
                {
                    DeviceId = template.Id,
                    Width = instTemplate.Width, Height = instTemplate.Height,
                    Purpose = instTemplate.Purpose,
                    X = instTemplate.X + 20, Y = instTemplate.Y + 20,
                    Override = instTemplate.Override == null ? null : CloneDevice(instTemplate.Override)
                };
                var newPlaced = CreatePlaced(template, newInstance);
                MainCanvas.Children.Add(newPlaced.Container);
                _placed.Add(newPlaced);
                RenderDevice(newPlaced);
                ClearSelection();
                SelectAdd(new[] { newPlaced });
                UpdateCanvasExtent();
                _isDirty = true;
            }
        }

        private void Menu_Undo(object sender, RoutedEventArgs e)
        {
            FlushPendingHistoryCommit();
            if (_undoHistory.Count == 0)
            {
                StatusText.Text = "실행 취소할 작업이 없습니다.";
                return;
            }

            HistorySnapshot current = _historyBaseline ?? CaptureHistoryState();
            HistorySnapshot target = _undoHistory[^1];
            RestoreHistoryState(target);
            _undoHistory.RemoveAt(_undoHistory.Count - 1);
            _redoHistory.Add(current);
            _historyBaseline = target;
            TrimHistoryMemory();
            StatusText.Text = $"실행 취소됨 (남은 단계: {_undoHistory.Count})";
        }

        private void Menu_Redo(object sender, RoutedEventArgs e)
        {
            FlushPendingHistoryCommit();
            if (_redoHistory.Count == 0)
            {
                StatusText.Text = "다시 실행할 작업이 없습니다.";
                return;
            }

            HistorySnapshot current = _historyBaseline ?? CaptureHistoryState();
            HistorySnapshot target = _redoHistory[^1];
            RestoreHistoryState(target);
            _redoHistory.RemoveAt(_redoHistory.Count - 1);
            _undoHistory.Add(current);
            if (_undoHistory.Count > MaximumUndoSteps)
                _undoHistory.RemoveAt(0);
            _historyBaseline = target;
            TrimHistoryMemory();
            StatusText.Text = $"다시 실행됨 (남은 단계: {_redoHistory.Count})";
        }

        private void Menu_NotImplemented(object sender, RoutedEventArgs e)
        {
            var header = (sender as MenuItem)?.Header?.ToString() ?? (sender as Button)?.ToolTip?.ToString() ?? "이 기능";
            MessageBox.Show($"'{header}' 기능은 아직 구현되지 않았습니다.", "ESDP",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Menu_Exit(object sender, RoutedEventArgs e) => Close();

        private void Menu_About(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("ESDP - Electronic Systems Design Program",
                            "정보", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ================= 헬퍼 =================
        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject? cur = child;
            while (cur != null && cur is not T)
            {
                cur = cur is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(cur)
                    : LogicalTreeHelper.GetParent(cur);
            }
            return cur as T;
        }
    }

    public class DeviceListItem : INotifyPropertyChanged
    {
        private static long _nextCreationOrder;
        public Device Device { get; }
        public string Name => Device.Name;
        public string KindText => string.IsNullOrWhiteSpace(Device.CustomKindName)
            ? Device.Kind.ToString() : Device.CustomKindName;
        public string GroupName => KindText;
        public int GroupOrder => string.IsNullOrWhiteSpace(Device.CustomKindName)
            ? (int)Device.Kind : 1000;
        public long CreationOrder { get; }
        public Brush KindBrush => AppSettings.GetDeviceBrush(Device);

        public DeviceListItem(Device device)
        {
            Device = device;
            CreationOrder = ++_nextCreationOrder;
        }

        public void Refresh()
        {
            OnChanged(nameof(Name));
            OnChanged(nameof(KindText));
            OnChanged(nameof(GroupName));
            OnChanged(nameof(GroupOrder));
            OnChanged(nameof(KindBrush));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public class CableListItem : INotifyPropertyChanged
    {
        private static long _nextCreationOrder;
        public Cable Cable { get; }
        public string Name => Cable.Name;
        public string CategoryName => string.IsNullOrWhiteSpace(Cable.CustomCategoryName)
            ? Cable.Category.ToString() : Cable.CustomCategoryName;
        public string CategoryText => $"({CategoryName})";
        public string GroupName => CategoryName;
        public int GroupOrder => string.IsNullOrWhiteSpace(Cable.CustomCategoryName)
            ? (int)Cable.Category : 1000;
        public long CreationOrder { get; }

        public CableListItem(Cable cable)
        {
            Cable = cable;
            CreationOrder = ++_nextCreationOrder;
        }

        public void Refresh()
        {
            OnChanged(nameof(Name));
            OnChanged(nameof(CategoryName));
            OnChanged(nameof(CategoryText));
            OnChanged(nameof(GroupName));
            OnChanged(nameof(GroupOrder));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
