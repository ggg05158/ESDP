using System.Collections.ObjectModel;
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ESDP.Models;
using WpfPoint = System.Windows.Point;

namespace ESDP
{
    public sealed class CableSignalEditRow : INotifyPropertyChanged
    {
        private int _order;
        private string _displayOrder = "";
        private string _pinName = "";
        private string _wireType = "";

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ParentSignalId { get; set; } = "";
        public int Order { get => _order; set => SetField(ref _order, value); }
        public string DisplayOrder { get => _displayOrder; set => SetField(ref _displayOrder, value); }
        public bool IsContinuation => !string.IsNullOrWhiteSpace(ParentSignalId);
        private bool _isSelectedAcrossTables;
        public bool IsSelectedAcrossTables
        {
            get => _isSelectedAcrossTables;
            set => SetField(ref _isSelectedAcrossTables, value);
        }
        public string PinName { get => _pinName; set => SetField(ref _pinName, value); }
        public string WireType { get => _wireType; set => SetField(ref _wireType, value); }
        public ObservableCollection<CablePin> Endpoints { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;
        private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class PinMarkBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string text = value?.ToString() ?? "";
            return string.IsNullOrWhiteSpace(text)
                ? Brushes.Transparent
                : ColorOption.BrushFromHex(text);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    public sealed class ShieldCellVisual : FrameworkElement
    {
        public static readonly DependencyProperty OwnerProperty = DependencyProperty.Register(
            nameof(Owner), typeof(CableSignalEditorWindow), typeof(ShieldCellVisual),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnOwnerChanged));
        public static readonly DependencyProperty PortIndexProperty = DependencyProperty.Register(
            nameof(PortIndex), typeof(int), typeof(ShieldCellVisual),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

        public CableSignalEditorWindow? Owner
        {
            get => (CableSignalEditorWindow?)GetValue(OwnerProperty);
            set => SetValue(OwnerProperty, value);
        }

        public int PortIndex
        {
            get => (int)GetValue(PortIndexProperty);
            set => SetValue(PortIndexProperty, value);
        }

        public ShieldCellVisual()
        {
            Focusable = true;
            Loaded += (_, _) =>
            {
                Owner?.RegisterShieldVisual(this);
                InvalidateVisual();
            };
            Unloaded += (_, _) => Owner?.UnregisterShieldVisual(this);
            DataContextChanged += (_, _) => InvalidateVisual();
        }

        private static void OnOwnerChanged(
            DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            var visual = (ShieldCellVisual)dependencyObject;
            if (e.OldValue is CableSignalEditorWindow oldOwner)
                oldOwner.UnregisterShieldVisual(visual);
            if (visual.IsLoaded && e.NewValue is CableSignalEditorWindow newOwner)
                newOwner.RegisterShieldVisual(visual);
            visual.InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            drawingContext.DrawRectangle(Brushes.Transparent, null,
                new Rect(0, 0, ActualWidth, ActualHeight));
            if (DataContext is CableSignalEditRow row)
                Owner?.RenderShieldCell(drawingContext, row, PortIndex, ActualWidth, ActualHeight);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            if (DataContext is CableSignalEditRow row)
                Owner?.ShieldCellMouseDown(row, PortIndex);
            e.Handled = true;
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            if (e.LeftButton == MouseButtonState.Pressed && DataContext is CableSignalEditRow row)
                Owner?.ShieldCellMouseEnter(row, PortIndex);
        }
    }

    public partial class CableSignalEditorWindow : Window
    {
        private static readonly Regex ShieldTokenPattern =
            new("^[sScC]\\d+$", RegexOptions.Compiled);
        private const string SignalCellsClipboardFormat = "ESDP.SignalCells.v1";
        private static string _recentPinMarkColorHex = "#FFD966";

        private readonly List<CablePortPropertyRow> _ports;
        private readonly List<(string PortName, string ConnType, string ConnectorCatalogKey)> _initialConnectorText;
        private readonly ObservableCollection<CableSignalEditRow> _rows = new();
        private readonly Dictionary<DataGridColumn, int> _inConnColumnPorts = new();
        private readonly Dictionary<DataGridColumn, (int PortIndex, string Property)> _endpointColumns = new();
        private readonly Dictionary<DataGridColumn, int> _shieldColumns = new();
        private readonly List<DataGrid> _signalGrids = new();
        private readonly List<Border> _shieldSortOverlays = new();
        private readonly Dictionary<string, List<Border>> _commonGroupCells = new();
        private readonly List<ShieldCellVisual> _shieldVisuals = new();
        private readonly HashSet<(int PortIndex, string RowId)> _selectedShieldCells = new();
        private readonly HashSet<string> _selectedCommonSignalIds = new(StringComparer.Ordinal);
        private List<string>? _shieldClipboardTokens;
        private StackPanel? _commonRowsPanel;
        private CableSignalEditRow? _selectedSignalRow;
        private bool _shieldDragActive;
        private int _shieldDragPortIndex = -1;
        private int _shieldDragStartRow = -1;
        private bool _shieldBondingMode;
        private List<string>? _orderBeforeEndpointSort;
        private DataGrid? _cellDragGrid;
        private int _cellDragStartRow = -1;
        private int _cellDragStartColumn = -1;
        private bool _commonNameDragActive;
        private string _commonNameDragStartId = "";
        private bool _isClearingCellSelections;
        private CableSignalEditRow? _editingRow;
        private int _editingPortIndex = -1;
        private string _originalInConn = "";
        private readonly string _initialStateSnapshot;
        private readonly List<string> _undoHistory = new();
        private readonly List<string> _redoHistory = new();
        private bool _isRestoringState;
        private bool _allowClose;

        private sealed class EditorState
        {
            public List<EditorRowState> Rows { get; set; } = new();
            public List<ConnectorTextState> Connectors { get; set; } = new();
            public bool OverallBraid { get; set; }
            public bool Tube { get; set; }
        }

        private sealed class EditorRowState
        {
            public string Id { get; set; } = "";
            public string ParentSignalId { get; set; } = "";
            public string PinName { get; set; } = "";
            public string WireType { get; set; } = "";
            public List<CablePin> Endpoints { get; set; } = new();
        }

        private sealed class ConnectorTextState
        {
            public string PortName { get; set; } = "";
            public string ConnType { get; set; } = "";
            public string ConnectorCatalogKey { get; set; } = "";
        }

        private sealed class SignalCellsClipboardPayload
        {
            public List<List<SignalClipboardCell>> Rows { get; set; } = new();
        }

        private sealed class SignalClipboardCell
        {
            public bool IsShield { get; set; }
            public string Value { get; set; } = "";
            public string MarkColorHex { get; set; } = "";
        }

        public List<CableSignalRow> ResultSignalRows { get; private set; } = new();
        public bool ResultOverallBraid { get; private set; } = true;
        public bool ResultTube { get; private set; } = true;

        public CableSignalEditorWindow(
            string cableName,
            IEnumerable<CablePortPropertyRow> ports,
            IEnumerable<CableSignalRow> signalRows,
            bool overallBraid,
            bool tube)
        {
            InitializeComponent();
            UpdateRecentPinMarkSwatch();
            _ports = ports.ToList();
            _initialConnectorText = _ports
                .Select(port => (port.PortName, port.ConnType, port.ConnectorCatalogKey)).ToList();

            int order = 0;
            foreach (var source in signalRows)
            {
                var row = new CableSignalEditRow
                {
                    Id = string.IsNullOrWhiteSpace(source.Id) ? Guid.NewGuid().ToString() : source.Id,
                    ParentSignalId = source.ParentSignalId,
                    Order = ++order,
                    PinName = source.PinName,
                    WireType = source.WireType
                };
                foreach (var port in _ports)
                    row.Endpoints.Add(source.PortPins.TryGetValue(port.ConnectorId, out var pin)
                        ? ClonePin(pin)
                        : new CablePin());
                _rows.Add(row);
            }
            if (_rows.Count == 0) AddEmptyRow();
            RenumberRows();

            string displayName = string.IsNullOrWhiteSpace(cableName) ? "이름 없는 케이블" : cableName;
            Title = $"케이블 신호 상세 설정 - {displayName}";
            CableTitleText.Text = $"{displayName} 신호 상세";
            OverallBraidCheck.IsChecked = overallBraid;
            TubeCheck.IsChecked = tube;
            PreviewMouseLeftButtonUp += (_, _) =>
            {
                _shieldDragActive = false;
                _commonNameDragActive = false;
                if (ReferenceEquals(Mouse.Captured, _commonRowsPanel))
                    Mouse.Capture(null);
            };
            PreviewMouseMove += Window_PreviewMouseMove;
            BuildTables();

            var workArea = SystemParameters.WorkArea;
            Width = Math.Max(MinWidth, workArea.Width * 0.96);
            Height = Math.Max(MinHeight, workArea.Height * 0.93);
            MaxWidth = workArea.Width;
            MaxHeight = workArea.Height;
            UpdateSummary();
            _initialStateSnapshot = CaptureStateSnapshot();
            _undoHistory.Add(_initialStateSnapshot);
            OverallBraidCheck.PreviewMouseLeftButtonDown += (_, _) => PushUndoCheckpoint();
            TubeCheck.PreviewMouseLeftButtonDown += (_, _) => PushUndoCheckpoint();
            Closing += Window_Closing;
        }

        private void BuildTables()
        {
            TablesPanel.Children.Clear();
            _signalGrids.Clear();
            _inConnColumnPorts.Clear();
            _endpointColumns.Clear();
            _shieldColumns.Clear();
            _shieldSortOverlays.Clear();

            TablesPanel.Children.Add(CreateTableCard(
                "회선 정보", "신호명 및 회선 종류", CreateCommonTable(), 340));

            for (int portIndex = 0; portIndex < _ports.Count; portIndex++)
            {
                var port = _ports[portIndex];
                var grid = CreateGrid();
                grid.Tag = portIndex;
                grid.Columns.Add(PinMarkColumn(portIndex));
                var shieldColumn = ShieldColumn(portIndex);
                _shieldColumns[shieldColumn] = portIndex;
                grid.Columns.Add(shieldColumn);
                var inConn = EndpointColumn("내부 연결", portIndex, nameof(CablePin.InConn), 56);
                _inConnColumnPorts[inConn] = portIndex;
                grid.Columns.Add(inConn);
                grid.Columns.Add(EndpointColumn("게이지", portIndex, nameof(CablePin.PinType), 50));

                string title = string.IsNullOrWhiteSpace(port.PortName)
                    ? $"포트 {portIndex + 1}"
                    : port.PortName;
                string subtitle = string.IsNullOrWhiteSpace(port.ConnType)
                    ? "커넥터 타입 미지정"
                    : port.ConnType;
                TablesPanel.Children.Add(CreateTableCard(
                    title, subtitle, CreateEndpointTableHost(grid), 182, port));
            }
            UpdateGridHeights();
        }

        private UIElement CreateEndpointTableHost(DataGrid dataGrid)
        {
            var host = new Grid();
            host.Children.Add(dataGrid);

            var message = new TextBlock
            {
                Text = "핀\n정렬\n중",
                FontSize = 8,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppThemeManager.GetBrush("AppMutedTextBrush"),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                LineHeight = 8
            };
            var overlay = new Border
            {
                Width = 26,
                Margin = new Thickness(48, 34, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = AppThemeManager.GetBrush("AppSubtlePanelBrush"),
                BorderBrush = AppThemeManager.GetBrush("AppStrongBorderBrush"),
                BorderThickness = new Thickness(0.5, 0, 0.5, 0),
                Child = message,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = true
            };
            Panel.SetZIndex(overlay, 10);
            _shieldSortOverlays.Add(overlay);
            host.Children.Add(overlay);
            return host;
        }

        private DataGrid CreateGrid()
        {
            var grid = new DataGrid
            {
                ItemsSource = _rows,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserReorderColumns = false,
                CanUserSortColumns = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                BorderThickness = new Thickness(0),
                AlternatingRowBackground = AppThemeManager.GetBrush("AppAlternateRowBrush"),
                RowHeaderWidth = 0,
                RowHeight = 30,
                ColumnHeaderHeight = 34,
                FontSize = 11,
                SelectionMode = DataGridSelectionMode.Extended,
                SelectionUnit = DataGridSelectionUnit.Cell,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                ColumnHeaderStyle = FindResource("SignalColumnHeader") as Style,
                RowStyle = FindResource("SignalRow") as Style,
                CellStyle = FindResource("SignalCell") as Style
            };
            grid.SelectionChanged += SignalGrid_SelectionChanged;
            grid.BeginningEdit += SignalsGrid_BeginningEdit;
            grid.CellEditEnding += SignalsGrid_CellEditEnding;
            grid.Sorting += SignalsGrid_Sorting;
            grid.PreviewMouseWheel += SignalGrid_PreviewMouseWheel;
            grid.PreviewMouseLeftButtonDown += SignalGrid_PreviewMouseLeftButtonDown;
            grid.MouseDoubleClick += SignalGrid_MouseDoubleClick;
            grid.PreviewTextInput += SignalGrid_PreviewTextInput;
            grid.PreviewMouseMove += SignalGrid_PreviewMouseMove;
            grid.PreviewMouseLeftButtonUp += SignalGrid_PreviewMouseLeftButtonUp;
            _signalGrids.Add(grid);
            return grid;
        }

        private Border CreateTableCard(
            string title, string subtitle, UIElement content, double width,
            CablePortPropertyRow? editablePort = null)
        {
            var headerStack = new StackPanel();
            if (editablePort == null)
            {
                headerStack.Children.Add(new TextBlock
                {
                    Text = title,
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold
                });
                headerStack.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    FontSize = 10.5,
                    Foreground = AppThemeManager.GetBrush("AppMutedTextBrush"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }
            else
            {
                var nameBox = new TextBox
                {
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Padding = new Thickness(0),
                    ToolTip = "포트 이름"
                };
                nameBox.SetBinding(TextBox.TextProperty, new Binding(nameof(CablePortPropertyRow.PortName))
                {
                    Source = editablePort,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                });
                nameBox.GotKeyboardFocus += (_, _) => PushUndoCheckpoint();
                var connectorBox = new TextBox
                {
                    FontSize = 10.5,
                    Foreground = AppThemeManager.GetBrush("AppMutedTextBrush"),
                    BorderBrush = AppThemeManager.GetBrush("AppStrongBorderBrush"),
                    BorderThickness = new Thickness(1),
                    Background = AppThemeManager.GetBrush("AppControlBrush"),
                    Padding = new Thickness(3, 0, 3, 0),
                    Margin = new Thickness(0, 2, 0, 0),
                    ToolTip = "커넥터 이름(타입)"
                };
                connectorBox.SetBinding(TextBox.TextProperty, new Binding(nameof(CablePortPropertyRow.ConnType))
                {
                    Source = editablePort,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                });
                connectorBox.GotKeyboardFocus += (_, _) => PushUndoCheckpoint();
                var connectorPlaceholder = new TextBlock
                {
                    Text = "커넥터 번호 입력",
                    FontSize = 10.5,
                    Foreground = AppThemeManager.GetBrush("AppMutedTextBrush"),
                    Margin = new Thickness(4, 2, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                };
                void UpdateConnectorPlaceholder() => connectorPlaceholder.Visibility =
                    string.IsNullOrWhiteSpace(connectorBox.Text) && !connectorBox.IsKeyboardFocused
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                connectorBox.TextChanged += (_, _) => UpdateConnectorPlaceholder();
                connectorBox.TextChanged += (_, _) =>
                {
                    if (connectorBox.IsKeyboardFocused)
                        editablePort.ConnectorCatalogKey = "";
                };
                connectorBox.GotKeyboardFocus += (_, _) => UpdateConnectorPlaceholder();
                connectorBox.LostKeyboardFocus += (_, _) => UpdateConnectorPlaceholder();
                var connectorHost = new Grid();
                connectorHost.ColumnDefinitions.Add(new ColumnDefinition());
                connectorHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                connectorHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                connectorHost.Children.Add(connectorBox);
                connectorHost.Children.Add(connectorPlaceholder);
                var connectorSearchButton = new Button
                {
                    Content = "🔍",
                    Style = AppThemeManager.GetStyle("CompactIconButtonStyle"),
                    Padding = new Thickness(0),
                    Margin = new Thickness(3, 2, 0, 0),
                    ToolTip = "커넥터 검색"
                };
                Grid.SetColumn(connectorSearchButton, 1);
                connectorSearchButton.Click += (_, _) =>
                {
                    try
                    {
                        var selector = new D38999ConnectorSelectorWindow(
                            editablePort.ConnType, editablePort.ConnectorCatalogKey)
                        {
                            Owner = this
                        };
                        if (selector.ShowDialog() != true) return;
                        PushUndoCheckpoint();
                        editablePort.ConnType = selector.ResultConnectorName;
                        editablePort.ConnectorCatalogKey = selector.ResultConnectorCatalogKey;
                        connectorBox.Text = selector.ResultConnectorName;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"D38999 커넥터 정보를 불러오지 못했습니다.\n\n{ex.Message}",
                            "커넥터 검색", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                };
                connectorHost.Children.Add(connectorSearchButton);
                var pinLayoutButton = new Button
                {
                    Content = "◉",
                    Style = AppThemeManager.GetStyle("CompactIconButtonStyle"),
                    Padding = new Thickness(0),
                    Margin = new Thickness(3, 2, 0, 0),
                    ToolTip = "핀 배열 보기"
                };
                Grid.SetColumn(pinLayoutButton, 2);
                pinLayoutButton.Click += (_, _) =>
                {
                    int portIndex = _ports.IndexOf(editablePort);
                    if (portIndex < 0) return;
                    var pins = _rows.Select(row =>
                    {
                        var pin = ClonePin(row.Endpoints[portIndex]);
                        pin.PinName = row.PinName;
                        return pin;
                    }).ToList();
                    var viewer = new ConnectorPinLayoutWindow(
                        editablePort.PortName, editablePort.ConnType,
                        editablePort.ConnectorCatalogKey, pins)
                    { Owner = this };
                    viewer.ShowDialog();
                };
                connectorHost.Children.Add(pinLayoutButton);
                UpdateConnectorPlaceholder();
                headerStack.Children.Add(nameBox);
                headerStack.Children.Add(connectorHost);
            }
            var header = new Border
            {
                Height = 58,
                Background = AppThemeManager.GetBrush("AppPanelHeaderBrush"),
                Padding = new Thickness(10, 6, 10, 5),
                Child = headerStack
            };
            DockPanel.SetDock(header, Dock.Top);
            var panel = new DockPanel();
            panel.Children.Add(header);
            panel.Children.Add(content);
            return new Border
            {
                Width = width,
                Margin = new Thickness(0, 0, 8, 0),
                Background = AppThemeManager.GetBrush("AppPanelBrush"),
                BorderBrush = AppThemeManager.GetBrush("AppStrongBorderBrush"),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top,
                Child = panel
            };
        }

        private UIElement CreateCommonTable()
        {
            var panel = new StackPanel();
            var header = new Grid { Height = 34, Background = AppThemeManager.GetBrush("AppPanelHeaderBrush") };
            AddCommonColumns(header);
            AddHeaderCell(header, 0, "번호");
            AddHeaderCell(header, 1, "신호명");
            AddHeaderCell(header, 2, "회선 종류");
            AddHeaderCell(header, 3, "");
            panel.Children.Add(header);

            _commonRowsPanel = new StackPanel();
            panel.Children.Add(_commonRowsPanel);
            RebuildCommonRows();
            return panel;
        }

        private static void AddCommonColumns(Grid grid)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        }

        private static void AddHeaderCell(Grid grid, int column, string text)
        {
            var border = new Border
            {
                BorderBrush = AppThemeManager.GetBrush("AppPanelHeaderBorderBrush"),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(border, column);
            grid.Children.Add(border);
        }

        private void RebuildCommonRows()
        {
            if (_commonRowsPanel == null) return;
            _commonRowsPanel.Children.Clear();
            _commonGroupCells.Clear();

            var knownIds = _rows.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var parent in _rows.Where(row =>
                         !row.IsContinuation || !knownIds.Contains(row.ParentSignalId)))
            {
                var physicalRows = new List<CableSignalEditRow> { parent };
                physicalRows.AddRange(_rows.Where(row => row.ParentSignalId == parent.Id));
                _commonRowsPanel.Children.Add(CreateMergedSignalGroup(parent, physicalRows));
            }
            UpdateCommonSelection(_selectedSignalRow);
        }

        private Grid CreateMergedSignalGroup(
            CableSignalEditRow parent, IReadOnlyList<CableSignalEditRow> physicalRows)
        {
            var grid = new Grid { Height = Math.Max(1, physicalRows.Count) * 30 };
            AddCommonColumns(grid);
            var mergedCells = new List<Border>();

            var numberBorder = MergedCell(new TextBlock
            {
                Text = parent.DisplayOrder,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            numberBorder.ToolTip = "마우스 오른쪽 버튼: 같은 신호의 핀 행 추가";
            numberBorder.MouseLeftButtonDown += (_, _) => SelectRowAcrossTables(parent);
            numberBorder.MouseRightButtonDown += (_, e) =>
            {
                OpenAddRowMenu(parent);
                e.Handled = true;
            };
            AddMergedCell(grid, numberBorder, 0, mergedCells);

            var nameBox = new TextBox
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(3, 0, 3, 0)
            };
            nameBox.Tag = parent;
            nameBox.SetBinding(TextBox.TextProperty, new Binding(nameof(CableSignalEditRow.PinName))
            {
                Source = parent,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            nameBox.GotKeyboardFocus += (_, _) =>
            {
                PushUndoCheckpoint();
                SelectRowAcrossTables(parent);
            };
            var nameBorder = MergedCell(nameBox);
            nameBorder.Tag = parent;
            nameBorder.PreviewMouseLeftButtonDown += (_, _) => BeginCommonNameDrag(parent);
            AddMergedCell(grid, nameBorder, 1, mergedCells);

            var typeBox = new TextBox
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalContentAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Padding = new Thickness(2, 0, 2, 0),
                ToolTip = "직접 입력할 수 있습니다. 오른쪽 클릭 추천: TWP / SGL / 기타"
            };
            typeBox.SetBinding(TextBox.TextProperty,
                new Binding(nameof(CableSignalEditRow.WireType))
                {
                    Source = parent,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                });
            typeBox.GotKeyboardFocus += (_, _) =>
            {
                PushUndoCheckpoint();
                SelectRowAcrossTables(parent);
            };
            var recommendationMenu = new ContextMenu();
            foreach (string recommendation in new[] { "TWP", "SGL", "기타" })
            {
                var item = new MenuItem { Header = recommendation };
                item.Click += (_, _) =>
                {
                    PushUndoCheckpoint();
                    parent.WireType = recommendation;
                    typeBox.Focus();
                    typeBox.CaretIndex = typeBox.Text.Length;
                };
                recommendationMenu.Items.Add(item);
            }
            typeBox.ContextMenu = recommendationMenu;
            var typeBorder = MergedCell(typeBox);
            typeBorder.Tag = parent;
            typeBorder.PreviewMouseLeftButtonDown += (_, _) => BeginCommonNameDrag(parent);
            AddMergedCell(grid, typeBorder, 2, mergedCells);

            var deleteGrid = new Grid();
            for (int index = 0; index < physicalRows.Count; index++)
            {
                deleteGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
                var button = new Button
                {
                    Content = "×",
                    Style = AppThemeManager.GetStyle("DangerIconButtonStyle"),
                    Width = 27,
                    Height = 24,
                    DataContext = physicalRows[index]
                };
                button.Click += RemoveSignalRowButton_Click;
                var cell = new Border
                {
                    BorderBrush = AppThemeManager.GetBrush("AppPanelHeaderBorderBrush"),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Child = button
                };
                Grid.SetRow(cell, index);
                deleteGrid.Children.Add(cell);
                mergedCells.Add(cell);
            }
            Grid.SetColumn(deleteGrid, 3);
            grid.Children.Add(deleteGrid);
            _commonGroupCells[parent.Id] = mergedCells;
            return grid;
        }

        private static Border MergedCell(UIElement content)
            => new()
            {
                BorderBrush = AppThemeManager.GetBrush("AppPanelHeaderBorderBrush"),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Background = AppThemeManager.GetBrush("AppPanelBrush"),
                Child = content
            };

        private static void AddMergedCell(
            Grid grid, Border cell, int column, ICollection<Border> collection)
        {
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
            collection.Add(cell);
        }

        private void OpenAddRowMenu(CableSignalEditRow row)
        {
            var menu = new ContextMenu();
            var add = new MenuItem { Header = "아래에 행 추가" };
            add.Click += (_, _) => AddContinuationRow(row);
            menu.Items.Add(add);
            menu.IsOpen = true;
        }

        private void SelectRowAcrossTables(CableSignalEditRow row)
        {
            _selectedSignalRow = row;
            foreach (var candidate in _rows)
                candidate.IsSelectedAcrossTables = ReferenceEquals(candidate, row);
            foreach (var grid in _signalGrids) grid.ScrollIntoView(row);
            UpdateCommonSelection(row);
        }

        private void BeginCommonNameDrag(CableSignalEditRow parent)
        {
            ClearGridCellSelections();
            _selectedShieldCells.Clear();
            _commonNameDragActive = true;
            _commonNameDragStartId = parent.Id;
            _selectedCommonSignalIds.Clear();
            _selectedCommonSignalIds.Add(parent.Id);
            SelectRowAcrossTables(parent);
            InvalidateShieldVisuals();
        }

        private void ContinueCommonNameDrag(CableSignalEditRow parent, MouseEventArgs e)
        {
            if (!_commonNameDragActive || e.LeftButton != MouseButtonState.Pressed) return;
            var parents = _rows.Where(row => !row.IsContinuation).ToList();
            int start = parents.FindIndex(row => row.Id == _commonNameDragStartId);
            int end = parents.IndexOf(parent);
            if (start < 0 || end < 0) return;
            if (start != end && _commonRowsPanel != null &&
                !ReferenceEquals(Mouse.Captured, _commonRowsPanel))
                Mouse.Capture(_commonRowsPanel, CaptureMode.SubTree);
            _selectedCommonSignalIds.Clear();
            for (int index = Math.Min(start, end); index <= Math.Max(start, end); index++)
                _selectedCommonSignalIds.Add(parents[index].Id);
            SelectRowAcrossTables(parent);
        }

        private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_commonNameDragActive || e.LeftButton != MouseButtonState.Pressed ||
                _commonRowsPanel == null)
                return;
            var hit = _commonRowsPanel.InputHitTest(e.GetPosition(_commonRowsPanel)) as DependencyObject;
            var nameCell = FindParent<Border>(hit);
            while (nameCell != null && nameCell.Tag is not CableSignalEditRow)
                nameCell = FindParent<Border>(VisualTreeHelper.GetParent(nameCell));
            if (nameCell?.Tag is CableSignalEditRow parent)
            {
                ContinueCommonNameDrag(parent, e);
                e.Handled = true;
            }
        }

        private void UpdateCommonSelection(CableSignalEditRow? row)
        {
            string selectedParentId = row == null
                ? ""
                : row.IsContinuation ? row.ParentSignalId : row.Id;
            var rowBrush = AppThemeManager.GetBrush("AppSubtlePanelBrush");
            var selectedBrush = AppThemeManager.GetBrush("AppAccentLightBrush");
            foreach (var pair in _commonGroupCells)
            foreach (var cell in pair.Value)
            {
                bool explicitSelection = _selectedCommonSignalIds.Contains(pair.Key) &&
                                         Grid.GetColumn(cell) is 1 or 2;
                cell.Background = explicitSelection
                    ? selectedBrush
                    : pair.Key == selectedParentId ? rowBrush : Brushes.White;
            }
        }

        private DataGridTextColumn TextColumn(
            object header, string path, double width, bool readOnly = false, bool centered = false)
            => new()
            {
                Header = header,
                Width = width,
                IsReadOnly = readOnly,
                ElementStyle = FindResource(centered ? "SignalCenteredCellText" : "SignalCellText") as Style,
                EditingElementStyle = FindResource(centered ? "SignalCenteredCellEditor" : "SignalCellEditor") as Style,
                Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
            };

        private DataGridTextColumn EndpointColumn(
            object header, int portIndex, string property, double width)
        {
            var column = new DataGridTextColumn
            {
                Header = header,
                Width = width,
                ElementStyle = FindResource("SignalCenteredCellText") as Style,
                EditingElementStyle = FindResource("SignalCenteredCellEditor") as Style,
                CanUserSort = property == nameof(CablePin.PinNum),
                Binding = new Binding($"Endpoints[{portIndex}].{property}")
                {
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                }
            };
            _endpointColumns[column] = (portIndex, property);
            return column;
        }

        private DataGridTemplateColumn PinMarkColumn(int portIndex)
        {
            var converter = new PinMarkBrushConverter();
            string pinPath = $"Endpoints[{portIndex}].{nameof(CablePin.PinNum)}";
            string colorPath = $"Endpoints[{portIndex}].{nameof(CablePin.MarkColorHex)}";

            FrameworkElementFactory BuildCell(bool editing)
            {
                var grid = new FrameworkElementFactory(typeof(Grid));
                var marker = new FrameworkElementFactory(typeof(Border));
                marker.SetValue(FrameworkElement.WidthProperty, 7d);
                marker.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
                marker.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 3, 0, 3));
                marker.SetValue(Border.CornerRadiusProperty, new CornerRadius(1));
                marker.SetBinding(Border.BackgroundProperty,
                    new Binding(colorPath) { Converter = converter });
                grid.AppendChild(marker);

                var text = new FrameworkElementFactory(editing ? typeof(TextBox) : typeof(TextBlock));
                text.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 1, 0));
                text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                if (editing)
                {
                    text.SetValue(TextBox.BorderThicknessProperty, new Thickness(0));
                    text.SetValue(TextBox.PaddingProperty, new Thickness(0));
                    text.SetValue(TextBox.TextAlignmentProperty, TextAlignment.Center);
                    text.SetBinding(TextBox.TextProperty, new Binding(pinPath)
                    {
                        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                    });
                }
                else
                {
                    text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
                    text.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
                    text.SetBinding(TextBlock.TextProperty, new Binding(pinPath));
                }
                grid.AppendChild(text);
                return grid;
            }

            var column = new DataGridTemplateColumn
            {
                Header = "핀",
                Width = 48,
                CanUserSort = true,
                CellTemplate = new DataTemplate { VisualTree = BuildCell(false) },
                CellEditingTemplate = new DataTemplate { VisualTree = BuildCell(true) }
            };
            _endpointColumns[column] = (portIndex, nameof(CablePin.PinNum));
            return column;
        }

        private List<CablePin> SelectedPinMarkTargets()
        {
            var result = new List<CablePin>();
            foreach (var grid in _signalGrids)
            foreach (var cell in grid.SelectedCells)
            {
                if (cell.Item is not CableSignalEditRow row ||
                    !_endpointColumns.TryGetValue(cell.Column, out var endpoint) ||
                    endpoint.Property != nameof(CablePin.PinNum) ||
                    endpoint.PortIndex < 0 || endpoint.PortIndex >= row.Endpoints.Count)
                    continue;
                var pin = row.Endpoints[endpoint.PortIndex];
                if (!result.Contains(pin)) result.Add(pin);
            }
            return result;
        }

        private void PinMarkColorButton_Click(object sender, RoutedEventArgs e)
        {
            var targets = SelectedPinMarkTargets();
            if (targets.Count == 0)
            {
                MessageBox.Show("색상 마크를 지정할 핀 셀을 먼저 선택하세요.", "핀 마크",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string initial = targets.Select(pin => pin.MarkColorHex)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? _recentPinMarkColorHex;
            if (!ColorPickerWindow.TryPick(this, initial, out string hex, "핀 마크 색")) return;
            _recentPinMarkColorHex = hex;
            UpdateRecentPinMarkSwatch();
            ApplyPinMarkColor(targets, hex);
        }

        private void RecentPinMarkButton_Click(object sender, RoutedEventArgs e)
        {
            var targets = SelectedPinMarkTargets();
            if (targets.Count == 0)
            {
                MessageBox.Show("최근 색을 적용할 핀 셀을 먼저 선택하세요.", "핀 마크",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ApplyPinMarkColor(targets, _recentPinMarkColorHex);
        }

        private void ApplyPinMarkColor(IEnumerable<CablePin> targets, string colorHex)
        {
            PushUndoCheckpoint();
            foreach (var pin in targets) pin.MarkColorHex = colorHex;
        }

        private void UpdateRecentPinMarkSwatch()
        {
            RecentPinMarkSwatch.Background = ColorOption.BrushFromHex(_recentPinMarkColorHex);
            RecentPinMarkButton.ToolTip =
                $"최근 핀 마크 색({_recentPinMarkColorHex})을 선택한 핀에 바로 적용합니다.";
        }

        private void ClearPinMarkButton_Click(object sender, RoutedEventArgs e)
        {
            var targets = SelectedPinMarkTargets();
            if (targets.Count == 0) return;
            PushUndoCheckpoint();
            foreach (var pin in targets) pin.MarkColorHex = "";
        }

        private void SignalsGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            CommitPendingEdits();

            if (_shieldColumns.ContainsKey(e.Column)) return;
            if (!_endpointColumns.TryGetValue(e.Column, out var endpoint)) return;

            // 핀 열은 원래 순서 -> 오름차순 -> 내림차순 -> 원래 순서로 순환한다.
            if (endpoint.Property == nameof(CablePin.PinNum) &&
                _orderBeforeEndpointSort is { Count: > 0 } &&
                e.Column.SortDirection == ListSortDirection.Descending)
            {
                RestoreOrderBeforeEndpointSort();
                return;
            }

            PushUndoCheckpoint();

            _orderBeforeEndpointSort ??= _rows.Select(row => row.Id).ToList();

            var direction = e.Column.SortDirection != ListSortDirection.Ascending
                ? ListSortDirection.Ascending
                : ListSortDirection.Descending;
            foreach (var grid in _signalGrids)
            foreach (var column in grid.Columns)
                column.SortDirection = null;
            e.Column.SortDirection = direction;

            var knownIds = _rows.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
            var groups = _rows
                .Where(row => !row.IsContinuation || !knownIds.Contains(row.ParentSignalId))
                .Select(parent =>
                {
                    var rows = new[] { parent }
                        .Concat(_rows.Where(row => row.ParentSignalId == parent.Id)).ToList();
                    string key = rows.Select(row =>
                            GetEndpointText(row, endpoint.PortIndex, endpoint.Property))
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
                    return new { Rows = rows, Key = key };
                });
            groups = direction == ListSortDirection.Ascending
                ? groups.OrderBy(group => group.Key, NaturalTextComparer.Instance)
                : groups.OrderByDescending(group => group.Key, NaturalTextComparer.Instance);

            var ordered = groups.SelectMany(group => group.Rows).ToList();
            for (int target = 0; target < ordered.Count; target++)
            {
                int current = _rows.IndexOf(ordered[target]);
                if (current != target) _rows.Move(current, target);
            }
            RenumberRows();
            RebuildCommonRows();
            UpdateGridHeights();
            SetShieldSortOverlayVisible(true);
            InvalidateShieldVisuals();
        }

        private void SetShieldSortOverlayVisible(bool visible)
        {
            foreach (var overlay in _shieldSortOverlays)
            {
                overlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (overlay.Child is TextBlock message)
                    message.Visibility = visible && _rows.Count >= 2
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
            if (visible)
            {
                _selectedShieldCells.Clear();
                _shieldDragActive = false;
                _shieldBondingMode = false;
            }
        }

        private void RestoreOrderBeforeEndpointSort()
        {
            if (_orderBeforeEndpointSort is not { Count: > 0 }) return;
            var order = _orderBeforeEndpointSort
                .Select((id, index) => (id, index))
                .ToDictionary(pair => pair.id, pair => pair.index, StringComparer.Ordinal);
            var restored = _rows
                .OrderBy(row => order.GetValueOrDefault(row.Id, int.MaxValue))
                .ToList();
            for (int target = 0; target < restored.Count; target++)
            {
                int current = _rows.IndexOf(restored[target]);
                if (current != target) _rows.Move(current, target);
            }
            foreach (var grid in _signalGrids)
            foreach (var column in grid.Columns)
                column.SortDirection = null;
            _orderBeforeEndpointSort = null;
            RenumberRows();
            RebuildCommonRows();
            UpdateGridHeights();
            SetShieldSortOverlayVisible(false);
            InvalidateShieldVisuals();
            UpdateSummary();
        }

        private static string GetEndpointText(
            CableSignalEditRow row, int portIndex, string property)
        {
            if (portIndex < 0 || portIndex >= row.Endpoints.Count) return "";
            var pin = row.Endpoints[portIndex];
            return property switch
            {
                nameof(CablePin.PinNum) => pin.PinNum,
                nameof(CablePin.InConn) => pin.InConn,
                nameof(CablePin.PinType) => pin.PinType,
                _ => ""
            };
        }

        private sealed class NaturalTextComparer : IComparer<string>
        {
            public static NaturalTextComparer Instance { get; } = new();
            public int Compare(string? left, string? right)
            {
                left ??= "";
                right ??= "";
                bool leftNumber = decimal.TryParse(left, out decimal leftValue);
                bool rightNumber = decimal.TryParse(right, out decimal rightValue);
                if (leftNumber && rightNumber) return leftValue.CompareTo(rightValue);
                if (leftNumber != rightNumber) return leftNumber ? -1 : 1;
                return StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
            }
        }

        private DataGridTemplateColumn ShieldColumn(int portIndex)
        {
            var factory = new FrameworkElementFactory(typeof(ShieldCellVisual));
            factory.SetBinding(ShieldCellVisual.OwnerProperty, new Binding
            {
                RelativeSource = new RelativeSource(
                    RelativeSourceMode.FindAncestor,
                    typeof(CableSignalEditorWindow),
                    1)
            });
            factory.SetValue(ShieldCellVisual.PortIndexProperty, portIndex);
            factory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            return new DataGridTemplateColumn
            {
                Header = "쉴드",
                Width = 26,
                CellTemplate = new DataTemplate { VisualTree = factory },
                IsReadOnly = true,
                CanUserSort = false
            };
        }

        private DataGridComboBoxColumn WireTypeColumn()
            => new()
            {
                Header = "회선 종류",
                Width = 72,
                ItemsSource = new[] { "TWP", "SGL", "기타" },
                SelectedItemBinding = new Binding(nameof(CableSignalEditRow.WireType))
                {
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                },
                ElementStyle = new Style(typeof(ComboBox))
                {
                    Setters =
                    {
                        new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
                        new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                        new Setter(Control.PaddingProperty, new Thickness(1, 0, 1, 0))
                    }
                },
                EditingElementStyle = new Style(typeof(ComboBox))
                {
                    Setters =
                    {
                        new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
                        new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                        new Setter(Control.PaddingProperty, new Thickness(1, 0, 1, 0))
                    }
                }
            };

        private DataGridTemplateColumn DeleteColumn()
        {
            var factory = new FrameworkElementFactory(typeof(Button));
            factory.SetValue(ContentControl.ContentProperty, "×");
            factory.SetValue(FrameworkElement.WidthProperty, 27d);
            factory.SetValue(FrameworkElement.HeightProperty, 24d);
            factory.AddHandler(Button.ClickEvent, new RoutedEventHandler(RemoveSignalRowButton_Click));
            return new DataGridTemplateColumn
            {
                Header = "",
                Width = 36,
                CellTemplate = new DataTemplate { VisualTree = factory }
            };
        }

        private void SignalGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isClearingCellSelections || sender is not DataGrid source) return;
            var row = source.CurrentItem as CableSignalEditRow
                      ?? source.SelectedCells.LastOrDefault().Item as CableSignalEditRow;
            if (row != null) SelectRowAcrossTables(row);
        }

        private void SignalGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            TablesScrollViewer.ScrollToVerticalOffset(
                Math.Max(0, TablesScrollViewer.VerticalOffset - e.Delta));
            e.Handled = true;
        }

        private void SignalGrid_PreviewMouseLeftButtonDown(
            object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGrid grid ||
                FindParent<DataGridCell>(e.OriginalSource as DependencyObject) is not { } cell ||
                FindParent<DataGridRow>(cell) is not { Item: CableSignalEditRow row })
                return;
            ClearGridCellSelections();
            _selectedShieldCells.Clear();
            _selectedCommonSignalIds.Clear();
            InvalidateShieldVisuals();
            if (_shieldColumns.ContainsKey(cell.Column))
            {
                _cellDragGrid = null;
                _cellDragStartRow = -1;
                _cellDragStartColumn = -1;
                return;
            }
            _cellDragGrid = grid;
            _cellDragStartRow = _rows.IndexOf(row);
            _cellDragStartColumn = cell.Column.DisplayIndex;
        }

        private void SignalGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGrid grid ||
                FindParent<DataGridCell>(e.OriginalSource as DependencyObject) is not { } cell ||
                !_endpointColumns.TryGetValue(cell.Column, out var endpoint) ||
                endpoint.Property != nameof(CablePin.PinNum))
                return;

            grid.CurrentCell = new DataGridCellInfo(cell);
            if (!grid.BeginEdit()) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (FindVisualChild<TextBox>(cell) is { } editor)
                {
                    editor.Focus();
                    editor.SelectAll();
                }
            }));
            e.Handled = true;
        }

        private void SignalGrid_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is not DataGrid grid || grid.CurrentCell.Column == null ||
                !_endpointColumns.TryGetValue(grid.CurrentCell.Column, out var endpoint) ||
                endpoint.Property != nameof(CablePin.PinNum) ||
                Keyboard.FocusedElement is TextBox)
                return;

            if (!grid.BeginEdit()) return;
            string typedText = e.Text;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var row = grid.ItemContainerGenerator.ContainerFromItem(
                    grid.CurrentCell.Item) as DataGridRow;
                var cell = row == null ? null : FindVisualChild<DataGridCell>(row,
                    candidate => ReferenceEquals(candidate.Column, grid.CurrentCell.Column));
                var editor = cell == null ? null : FindVisualChild<TextBox>(cell);
                if (editor == null) return;
                editor.Focus();
                editor.Text = typedText;
                editor.CaretIndex = editor.Text.Length;
            }));
            e.Handled = true;
        }

        private void ClearGridCellSelections()
        {
            if (_isClearingCellSelections) return;
            _isClearingCellSelections = true;
            try
            {
                foreach (var signalGrid in _signalGrids)
                {
                    signalGrid.SelectedCells.Clear();
                    signalGrid.UnselectAll();
                }
            }
            finally
            {
                _isClearingCellSelections = false;
            }
        }

        private void SignalGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed ||
                sender is not DataGrid grid || !ReferenceEquals(grid, _cellDragGrid) ||
                _cellDragStartRow < 0 || _cellDragStartColumn < 0)
                return;
            var hit = grid.InputHitTest(e.GetPosition(grid)) as DependencyObject;
            var cell = FindParent<DataGridCell>(hit);
            var rowContainer = FindParent<DataGridRow>(hit);
            if (cell == null || rowContainer?.Item is not CableSignalEditRow row) return;
            int endRow = _rows.IndexOf(row);
            int endColumn = cell.Column.DisplayIndex;
            if (endRow < 0 ||
                (endRow == _cellDragStartRow && endColumn == _cellDragStartColumn))
                return;

            int firstRow = Math.Min(_cellDragStartRow, endRow);
            int lastRow = Math.Max(_cellDragStartRow, endRow);
            int firstColumn = Math.Min(_cellDragStartColumn, endColumn);
            int lastColumn = Math.Max(_cellDragStartColumn, endColumn);
            grid.SelectedCells.Clear();
            foreach (var column in grid.Columns.Where(column =>
                         column.DisplayIndex >= firstColumn && column.DisplayIndex <= lastColumn))
            for (int rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
                grid.SelectedCells.Add(new DataGridCellInfo(_rows[rowIndex], column));
            SelectRowAcrossTables(row);
            e.Handled = true;
        }

        private void SignalGrid_PreviewMouseLeftButtonUp(
            object sender, MouseButtonEventArgs e)
        {
            _cellDragGrid = null;
            _cellDragStartRow = -1;
            _cellDragStartColumn = -1;
        }

        private void CommonGrid_PreviewMouseRightButtonDown(
            object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source)
                return;
            var cell = FindParent<DataGridCell>(source);
            var rowContainer = FindParent<DataGridRow>(source);
            if (cell?.Column.DisplayIndex != 0 ||
                rowContainer?.Item is not CableSignalEditRow clickedRow)
                return;

            grid.SelectedItem = clickedRow;
            var menu = new ContextMenu();
            var add = new MenuItem { Header = "아래에 행 추가" };
            add.Click += (_, _) => AddContinuationRow(clickedRow);
            menu.Items.Add(add);
            menu.IsOpen = true;
            e.Handled = true;
        }

        private static T? FindParent<T>(DependencyObject? source)
            where T : DependencyObject
        {
            while (source != null)
            {
                if (source is T match) return match;
                source = VisualTreeHelper.GetParent(source);
            }
            return null;
        }

        private static T? FindVisualChild<T>(
            DependencyObject? source, Func<T, bool>? predicate = null)
            where T : DependencyObject
        {
            if (source == null) return null;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
            {
                var child = VisualTreeHelper.GetChild(source, index);
                if (child is T match && (predicate == null || predicate(match)))
                    return match;
                var descendant = FindVisualChild(child, predicate);
                if (descendant != null) return descendant;
            }
            return null;
        }

        private void AddContinuationRow(CableSignalEditRow clickedRow)
        {
            var parent = clickedRow.IsContinuation
                ? _rows.FirstOrDefault(row => row.Id == clickedRow.ParentSignalId)
                : clickedRow;
            if (parent == null) return;
            PushUndoCheckpoint();

            int insertIndex = _rows.IndexOf(parent) + 1;
            while (insertIndex < _rows.Count &&
                   _rows[insertIndex].ParentSignalId == parent.Id)
                insertIndex++;

            var continuation = new CableSignalEditRow
            {
                ParentSignalId = parent.Id
            };
            for (int portIndex = 0; portIndex < _ports.Count; portIndex++)
            {
                string inheritedShield = "";
                if (portIndex < parent.Endpoints.Count &&
                    TryParseShieldToken(
                        parent.Endpoints[portIndex].TwpShield, 's', out int shieldNumber))
                    inheritedShield = $"s{shieldNumber}";
                continuation.Endpoints.Add(new CablePin { TwpShield = inheritedShield });
            }
            _rows.Insert(insertIndex, continuation);
            RenumberRows();
            RebuildCommonRows();
            UpdateGridHeights();
            SelectRowAcrossTables(continuation);
            InvalidateShieldVisuals();
            UpdateSummary();
        }

        private void AddSignalRowButton_Click(object sender, RoutedEventArgs e)
        {
            PushUndoCheckpoint();
            var row = AddEmptyRow();
            RenumberRows();
            RebuildCommonRows();
            UpdateGridHeights();
            SelectRowAcrossTables(row);
            InvalidateShieldVisuals();
            UpdateSummary();
        }

        private void AddMultipleSignalRowsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(BulkSignalCountBox.Text.Trim(), out int count) ||
                count < 1 || count > 500)
            {
                MessageBox.Show("추가할 회선 수를 1~500 사이의 숫자로 입력하세요.", "회선 일괄 추가",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                BulkSignalCountBox.Focus();
                BulkSignalCountBox.SelectAll();
                return;
            }

            CableSignalEditRow? lastRow = null;
            PushUndoCheckpoint();
            for (int index = 0; index < count; index++)
                lastRow = AddEmptyRow();

            RenumberRows();
            RebuildCommonRows();
            UpdateGridHeights();
            if (lastRow != null) SelectRowAcrossTables(lastRow);
            InvalidateShieldVisuals();
            UpdateSummary();
        }

        private CableSignalEditRow AddEmptyRow()
        {
            var row = new CableSignalEditRow { Order = _rows.Count + 1 };
            foreach (var _ in _ports) row.Endpoints.Add(new CablePin());
            _rows.Add(row);
            return row;
        }

        private void RemoveSignalRowButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: CableSignalEditRow row }) return;
            PushUndoCheckpoint();
            if (!row.IsContinuation)
            {
                var children = _rows.Where(candidate => candidate.ParentSignalId == row.Id).ToList();
                if (children.Count > 0)
                {
                    var promoted = children[0];
                    promoted.ParentSignalId = "";
                    promoted.PinName = row.PinName;
                    promoted.WireType = row.WireType;
                    foreach (var remaining in children.Skip(1))
                        remaining.ParentSignalId = promoted.Id;
                }
            }
            _rows.Remove(row);
            _selectedSignalRow = null;
            RenumberRows();
            for (int portIndex = 0; portIndex < _ports.Count; portIndex++)
                RenumberShieldGroups(portIndex);
            RebuildCommonRows();
            UpdateGridHeights();
            InvalidateShieldVisuals();
            UpdateSummary();
        }

        private void RenumberRows()
        {
            int number = 0;
            foreach (var row in _rows)
            {
                if (row.IsContinuation)
                {
                    row.Order = 0;
                    row.DisplayOrder = "";
                }
                else
                {
                    row.Order = ++number;
                    row.DisplayOrder = number.ToString();
                }
            }
        }

        private void UpdateGridHeights()
        {
            double height = 34 + Math.Max(1, _rows.Count) * 30;
            foreach (var grid in _signalGrids) grid.Height = height;
        }

        private void UpdateSummary()
        {
            SummaryText.Text = _shieldBondingMode
                ? "본딩 위치 선택: 인접한 쉴드 사이의 회색 점을 누르세요."
                : $"포트 {_ports.Count}개 · 신호 {_rows.Count(row => !row.IsContinuation)}개 · 핀 행 {_rows.Count}개";
        }

        private void SignalsGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
        {
            if (sender is DataGrid { Tag: string tag } && tag == "common" &&
                e.Row.Item is CableSignalEditRow commonRow && commonRow.IsContinuation)
            {
                e.Cancel = true;
                return;
            }
            PushUndoCheckpoint();
            if (!_inConnColumnPorts.TryGetValue(e.Column, out int portIndex) ||
                e.Row.Item is not CableSignalEditRow row)
                return;
            _editingRow = row;
            _editingPortIndex = portIndex;
            _originalInConn = row.Endpoints[portIndex].InConn.Trim();
        }

        private void SignalsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit ||
                e.Row.Item is not CableSignalEditRow row)
                return;


            if (!_inConnColumnPorts.TryGetValue(e.Column, out int portIndex)) return;
            string previous = ReferenceEquals(row, _editingRow) && portIndex == _editingPortIndex
                ? _originalInConn
                : "";
            Dispatcher.BeginInvoke(new Action(() =>
                SynchronizeInternalConnection(row, portIndex, previous)));
        }

        private void SynchronizeInternalConnection(
            CableSignalEditRow sourceRow, int portIndex, string previousTargetNumber)
        {
            var source = sourceRow.Endpoints[portIndex];
            string sourceNumber = source.PinNum.Trim();
            string targetNumber = source.InConn.Trim();
            if (string.IsNullOrWhiteSpace(sourceNumber)) return;

            if (!string.IsNullOrWhiteSpace(previousTargetNumber) &&
                !string.Equals(previousTargetNumber, targetNumber, StringComparison.OrdinalIgnoreCase))
            {
                var previous = FindEndpointByPin(portIndex, previousTargetNumber, sourceRow);
                if (previous != null && string.Equals(previous.InConn.Trim(), sourceNumber,
                        StringComparison.OrdinalIgnoreCase))
                    previous.InConn = "";
            }
            if (string.IsNullOrWhiteSpace(targetNumber)) return;
            var target = FindEndpointByPin(portIndex, targetNumber, sourceRow);
            if (target == null) return;

            string displaced = target.InConn.Trim();
            if (!string.IsNullOrWhiteSpace(displaced) &&
                !string.Equals(displaced, sourceNumber, StringComparison.OrdinalIgnoreCase))
            {
                var displacedSource = FindEndpointByPin(portIndex, displaced, null);
                if (displacedSource != null && string.Equals(displacedSource.InConn.Trim(), targetNumber,
                        StringComparison.OrdinalIgnoreCase))
                    displacedSource.InConn = "";
            }
            target.InConn = sourceNumber;
            _editingRow = null;
            _editingPortIndex = -1;
            _originalInConn = "";
        }

        private CablePin? FindEndpointByPin(
            int portIndex, string pinNumber, CableSignalEditRow? excludedRow)
            => _rows.Where(row => !ReferenceEquals(row, excludedRow))
                .Select(row => row.Endpoints[portIndex])
                .FirstOrDefault(pin => string.Equals(pin.PinNum.Trim(), pinNumber,
                    StringComparison.OrdinalIgnoreCase));

        internal void RegisterShieldVisual(ShieldCellVisual visual)
        {
            if (!_shieldVisuals.Contains(visual)) _shieldVisuals.Add(visual);
        }

        internal void UnregisterShieldVisual(ShieldCellVisual visual)
            => _shieldVisuals.Remove(visual);

        private void InvalidateShieldVisuals()
        {
            foreach (var visual in _shieldVisuals.ToList()) visual.InvalidateVisual();
        }

        internal void ShieldCellMouseDown(CableSignalEditRow row, int portIndex)
        {
            int rowIndex = _rows.IndexOf(row);
            if (rowIndex < 0) return;
            if (_shieldBondingMode &&
                (TryBondAtBoundary(portIndex, rowIndex) ||
                 TryBondAtBoundary(portIndex, rowIndex - 1)))
                return;

            _shieldBondingMode = false;
            ClearGridCellSelections();
            _selectedCommonSignalIds.Clear();
            _shieldDragActive = true;
            _shieldDragPortIndex = portIndex;
            _shieldDragStartRow = rowIndex;
            _selectedShieldCells.Clear();
            _selectedShieldCells.Add((portIndex, row.Id));
            SelectRowAcrossTables(row);
            InvalidateShieldVisuals();
            UpdateSummary();
        }

        internal void ShieldCellMouseEnter(CableSignalEditRow row, int portIndex)
        {
            if (!_shieldDragActive || portIndex != _shieldDragPortIndex) return;
            int end = _rows.IndexOf(row);
            if (end < 0) return;
            int first = Math.Min(_shieldDragStartRow, end);
            int last = Math.Max(_shieldDragStartRow, end);
            _selectedShieldCells.Clear();
            for (int index = first; index <= last; index++)
                _selectedShieldCells.Add((portIndex, _rows[index].Id));
            InvalidateShieldVisuals();
        }

        internal void RenderShieldCell(
            DrawingContext dc, CableSignalEditRow row, int portIndex, double width, double height)
        {
            if (portIndex < 0 || portIndex >= row.Endpoints.Count) return;
            int rowIndex = _rows.IndexOf(row);
            if (rowIndex < 0) return;

            if (_selectedShieldCells.Contains((portIndex, row.Id)))
                dc.DrawRectangle(AppThemeManager.GetBrush("AppAccentLightBrush"), null,
                    new Rect(0, 0, width, height));

            string token = row.Endpoints[portIndex].TwpShield.Trim().ToLowerInvariant();
            var shieldPen = new Pen(new SolidColorBrush(Color.FromRgb(0x1F, 0x3B, 0x64)), 2.2)
            {
                DashStyle = new DashStyle(new[] { 2.0, 1.5 }, 0)
            };
            shieldPen.Freeze();
            const double inset = 3;

            void DrawBondMarker(double centerY, bool bonded)
            {
                if (bonded)
                {
                    dc.DrawEllipse(Brushes.Black, null,
                        new WpfPoint(width / 2, centerY), 6, 6);
                }
                else if (_shieldBondingMode)
                {
                    dc.DrawEllipse(
                        new SolidColorBrush(Color.FromRgb(0xA4, 0xAA, 0xB0)),
                        new Pen(Brushes.White, 1),
                        new WpfPoint(width / 2, centerY), 5, 5);
                }
            }

            if (TryParseShieldToken(token, 's', out int shieldNumber))
            {
                bool sameAbove = rowIndex > 0 &&
                    string.Equals(_rows[rowIndex - 1].Endpoints[portIndex].TwpShield.Trim(), token,
                                  StringComparison.OrdinalIgnoreCase);
                bool sameBelow = rowIndex + 1 < _rows.Count &&
                    string.Equals(_rows[rowIndex + 1].Endpoints[portIndex].TwpShield.Trim(), token,
                                  StringComparison.OrdinalIgnoreCase);

                if (!sameAbove && !sameBelow)
                {
                    dc.DrawRoundedRectangle(null, shieldPen,
                        new Rect(inset, 0.5, Math.Max(1, width - inset * 2), Math.Max(1, height - 1)),
                        5, 5);
                }
                else
                {
                    const double capDepth = 4;
                    double top = sameAbove ? 0 : capDepth;
                    double bottom = sameBelow ? height : Math.Max(capDepth, height - capDepth);
                    dc.DrawLine(shieldPen, new WpfPoint(inset, top), new WpfPoint(inset, bottom));
                    dc.DrawLine(shieldPen, new WpfPoint(width - inset, top), new WpfPoint(width - inset, bottom));
                    if (!sameAbove) DrawShieldCap(dc, shieldPen, inset, width - inset, height, true);
                    if (!sameBelow) DrawShieldCap(dc, shieldPen, inset, width - inset, height, false);
                }

                if (TryGetBoundaryGroups(portIndex, rowIndex, out int upper, out int lower) &&
                    upper == shieldNumber)
                {
                    DrawBondMarker(height, lower == upper + 1);
                }

                // 본딩 원은 두 셀의 경계 중앙에 놓인다. 위 셀에서 위 절반,
                // 아래 셀에서는 쉴드 캡슐을 그린 뒤 아래 절반을 다시 그려
                // 셀 클리핑에도 온전한 원으로 보이고 쉴드보다 앞에 표시된다.
                if (rowIndex > 0 &&
                    TryGetBoundaryGroups(
                        portIndex, rowIndex - 1,
                        out int upperAbove, out int lowerAbove) &&
                    lowerAbove == shieldNumber)
                {
                    DrawBondMarker(0, lowerAbove == upperAbove + 1);
                }
            }
            else if (TryParseShieldToken(token, 'c', out int groundedShield))
            {
                int nearest = FindNearestShieldRow(portIndex, rowIndex, groundedShield);
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x1F, 0x3B, 0x64)), 2.8);
                double centerX = width / 2;
                double centerY = height / 2;
                if (nearest <= rowIndex)
                    dc.DrawLine(pen, new WpfPoint(centerX, 0), new WpfPoint(centerX, centerY));
                else
                    dc.DrawLine(pen, new WpfPoint(centerX, height), new WpfPoint(centerX, centerY));
                dc.DrawLine(pen, new WpfPoint(centerX, centerY), new WpfPoint(0, centerY));
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.A &&
                TryAutoFillSelectedCells())
            {
                e.Handled = true;
                return;
            }
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

            if (e.Key == Key.Z)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) RedoEditorChange();
                else UndoEditorChange();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Y)
            {
                RedoEditorChange();
                e.Handled = true;
                return;
            }

            // 공통 신호명 선택은 표에 남아 있는 이전 선택보다 우선한다.
            if (e.Key == Key.C && TryCopySelectedCommonNames())
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.V && TryPasteSelectedCommonNames())
            {
                e.Handled = true;
                return;
            }

            // 표에서 여러 열을 함께 선택했다면 쉴드 전용 복사보다 통합 복사를 우선한다.
            if (e.Key == Key.C && TryCopySelectedDataCells())
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.V && TryPasteTabularClipboard())
            {
                e.Handled = true;
                return;
            }

            if (Keyboard.FocusedElement is ShieldCellVisual || _selectedShieldCells.Count > 0)
            {
                if (e.Key == Key.C)
                {
                    CopySelectedShieldCells();
                    e.Handled = true;
                }
                else if (e.Key == Key.V)
                {
                    PasteShieldCells();
                    e.Handled = true;
                }
                return;
            }

        }

        private bool TryCopySelectedDataCells()
        {
            var grid = GetActiveSignalGrid(requireMultipleSelection: true);
            if (grid == null || grid.SelectedCells.Count < 2) return false;

            var selected = grid.SelectedCells
                .Where(cell => cell.Item is CableSignalEditRow &&
                               (_endpointColumns.ContainsKey(cell.Column) ||
                                _shieldColumns.ContainsKey(cell.Column)))
                .ToList();
            if (selected.Count < 2) return false;

            var rowIndexes = selected.Select(cell => _rows.IndexOf((CableSignalEditRow)cell.Item))
                .Where(index => index >= 0).Distinct().OrderBy(index => index).ToList();
            var columns = selected.Select(cell => cell.Column).Distinct()
                .OrderBy(column => column.DisplayIndex).ToList();
            if (rowIndexes.Count == 0 || columns.Count == 0) return false;

            var selectedKeys = selected.Select(cell =>
                    (_rows.IndexOf((CableSignalEditRow)cell.Item), cell.Column))
                .ToHashSet();
            var lines = new List<string>();
            var payload = new SignalCellsClipboardPayload();
            foreach (int rowIndex in rowIndexes)
            {
                var payloadRow = new List<SignalClipboardCell>();
                var values = columns.Select(column =>
                {
                    bool selectedCell = selectedKeys.Contains((rowIndex, column));
                    bool isShield = _shieldColumns.ContainsKey(column);
                    string value = selectedCell
                        ? GetDataCellText(_rows[rowIndex], column)
                        : "";
                    payloadRow.Add(new SignalClipboardCell
                    {
                        IsShield = isShield,
                        Value = value,
                        MarkColorHex = !isShield && selectedCell &&
                                       _endpointColumns.TryGetValue(column, out var endpoint) &&
                                       endpoint.Property == nameof(CablePin.PinNum)
                            ? _rows[rowIndex].Endpoints[endpoint.PortIndex].MarkColorHex
                            : ""
                    });
                    // 쉴드 내부 토큰은 일반 텍스트 클립보드에 노출하지 않는다.
                    return isShield ? "" : value;
                });
                lines.Add(string.Join('\t', values));
                payload.Rows.Add(payloadRow);
            }
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, string.Join(Environment.NewLine, lines));
                data.SetData(SignalCellsClipboardFormat, JsonSerializer.Serialize(payload));
                Clipboard.SetDataObject(data, true);
                SummaryText.Text = $"선택한 셀 {selected.Count}개를 복사했습니다.";
                return true;
            }
            catch { return false; }
        }

        private string GetDataCellText(CableSignalEditRow row, DataGridColumn column)
        {
            if (_endpointColumns.TryGetValue(column, out var endpoint))
                return GetEndpointText(row, endpoint.PortIndex, endpoint.Property);
            if (_shieldColumns.TryGetValue(column, out int portIndex) &&
                portIndex >= 0 && portIndex < row.Endpoints.Count)
                return row.Endpoints[portIndex].TwpShield.Trim().ToLowerInvariant();
            return "";
        }

        private bool TryCopySelectedCommonNames()
        {
            if (_selectedCommonSignalIds.Count == 0) return false;
            // 여러 행 드래그 중 첫 TextBox에 글자 블록이 남더라도 행 복사를 우선한다.
            // 한 행만 선택한 경우에만 일반적인 글자 일부 복사를 허용한다.
            if (_selectedCommonSignalIds.Count == 1 &&
                Keyboard.FocusedElement is TextBox { SelectionLength: > 0 })
                return false;
            var values = _rows.Where(row => !row.IsContinuation &&
                                           _selectedCommonSignalIds.Contains(row.Id))
                .Select(row => $"{row.PinName}\t{row.WireType}");
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, values));
                SummaryText.Text = $"선택한 신호명과 회선 종류 {_selectedCommonSignalIds.Count}행을 복사했습니다.";
                return true;
            }
            catch { return false; }
        }

        private bool TryPasteSelectedCommonNames()
        {
            if (_selectedCommonSignalIds.Count == 0) return false;
            string text;
            try
            {
                if (!Clipboard.ContainsText()) return false;
                text = Clipboard.GetText();
            }
            catch { return false; }

            var values = text.Replace("\r\n", "\n").Replace('\r', '\n')
                .TrimEnd('\n').Split('\n')
                .Select(line => line.Split('\t'))
                .ToList();
            if (values.Count == 0) return false;
            var parents = _rows.Where(row => !row.IsContinuation).ToList();
            var targets = parents.Where(row => _selectedCommonSignalIds.Contains(row.Id)).ToList();
            if (targets.Count == 0) return false;
            PushUndoCheckpoint();

            // 시작 셀 하나만 선택한 표준 붙여넣기는 클립보드 행 수만큼 아래로 확장한다.
            if (targets.Count == 1 && values.Count > 1)
            {
                int startIndex = parents.IndexOf(targets[0]);
                int required = startIndex + values.Count;
                while (parents.Count < required)
                    parents.Add(AddEmptyRow());
                targets = parents.Skip(startIndex).Take(values.Count).ToList();
            }

            for (int index = 0; index < targets.Count; index++)
            {
                var source = values.Count == 1
                    ? values[0]
                    : index < values.Count ? values[index] : null;
                if (source == null) continue;
                targets[index].PinName = source.ElementAtOrDefault(0) ?? "";
                // 한 열짜리 외부 자료를 붙여넣을 때는 기존 회선 종류를 보존한다.
                if (source.Length > 1)
                    targets[index].WireType = source[1];
            }
            FinishTabularPaste();
            SummaryText.Text = $"신호명과 회선 종류 {Math.Min(targets.Count, values.Count == 1 ? targets.Count : values.Count)}행을 붙여넣었습니다.";
            return true;
        }

        private bool TryAutoFillSelectedCells()
        {
            if (Keyboard.FocusedElement is TextBox) return false;
            var grid = FindParent<DataGrid>(Keyboard.FocusedElement as DependencyObject);
            if (grid == null || grid.SelectedCells.Count < 2) return false;
            var cells = grid.SelectedCells
                .Where(cell => cell.Item is CableSignalEditRow &&
                               _endpointColumns.ContainsKey(cell.Column))
                .ToList();
            var columns = cells.Select(cell => cell.Column).Distinct().ToList();
            if (cells.Count < 2 || columns.Count != 1) return false;

            var column = columns[0];
            var endpoint = _endpointColumns[column];
            var targets = cells
                .Select(cell => (Row: (CableSignalEditRow)cell.Item,
                                 Index: _rows.IndexOf((CableSignalEditRow)cell.Item)))
                .Where(item => item.Index >= 0)
                .DistinctBy(item => item.Index)
                .OrderBy(item => item.Index)
                .ToList();
            if (targets.Count < 2) return false;

            var seeds = targets
                .Select((item, position) => new
                {
                    Position = position,
                    Value = GetEndpointText(item.Row, endpoint.PortIndex, endpoint.Property).Trim()
                })
                .Where(seed => !string.IsNullOrWhiteSpace(seed.Value))
                .ToList();
            if (seeds.Count == 0) return false;

            Func<int, string>? valueAt = null;
            if (seeds.Select(seed => seed.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            {
                string repeated = seeds[0].Value;
                valueAt = _ => repeated;
            }
            else if (seeds.Count >= 2 &&
                     decimal.TryParse(seeds[0].Value, out decimal firstNumber) &&
                     decimal.TryParse(seeds[1].Value, out decimal secondNumber))
            {
                decimal step = (secondNumber - firstNumber) /
                               (seeds[1].Position - seeds[0].Position);
                decimal origin = firstNumber - step * seeds[0].Position;
                valueAt = position => (origin + step * position)
                    .ToString("0.################", System.Globalization.CultureInfo.CurrentCulture);
            }
            else if (seeds.Count >= 2 &&
                     TrySplitTrailingNumber(seeds[0].Value, out string prefix1, out int number1) &&
                     TrySplitTrailingNumber(seeds[1].Value, out string prefix2, out int number2) &&
                     string.Equals(prefix1, prefix2, StringComparison.OrdinalIgnoreCase))
            {
                double step = (double)(number2 - number1) /
                              (seeds[1].Position - seeds[0].Position);
                double origin = number1 - step * seeds[0].Position;
                valueAt = position => prefix1 + Math.Round(origin + step * position);
            }
            if (valueAt == null) return false;

            PushUndoCheckpoint();
            int filled = 0;
            for (int position = 0; position < targets.Count; position++)
            {
                if (!string.IsNullOrWhiteSpace(GetEndpointText(
                        targets[position].Row, endpoint.PortIndex, endpoint.Property)))
                    continue;
                SetEndpointText(targets[position].Row, endpoint.PortIndex,
                    endpoint.Property, valueAt(position));
                filled++;
            }
            if (filled == 0) return false;
            SummaryText.Text = $"빈 셀 {filled}개를 자동으로 채웠습니다.";
            return true;
        }

        private static bool TrySplitTrailingNumber(
            string value, out string prefix, out int number)
        {
            number = 0;
            var match = Regex.Match(value, @"^(.*?)(-?\d+)$");
            if (!match.Success || !int.TryParse(match.Groups[2].Value, out number))
            {
                prefix = "";
                return false;
            }
            prefix = match.Groups[1].Value;
            return true;
        }

        private bool TryPasteTabularClipboard()
        {
            if (TryPasteStructuredSignalCells()) return true;

            string text;
            try
            {
                if (!Clipboard.ContainsText()) return false;
                text = Clipboard.GetText();
            }
            catch { return false; }

            var matrix = text.Replace("\r\n", "\n").Replace('\r', '\n')
                .TrimEnd('\n')
                .Split('\n')
                .Select(line => line.Split('\t'))
                .ToList();
            if (matrix.Count == 0) return false;

            var focused = Keyboard.FocusedElement as DependencyObject;
            var grid = FindParent<DataGrid>(focused);
            if (grid != null && grid.CurrentItem is CableSignalEditRow startRow &&
                grid.CurrentColumn != null && _endpointColumns.ContainsKey(grid.CurrentColumn))
            {
                int startRowIndex = _rows.IndexOf(startRow);
                var editableColumns = grid.Columns
                    .Where(column => _endpointColumns.ContainsKey(column))
                    .OrderBy(column => column.DisplayIndex)
                    .ToList();
                int startColumnIndex = editableColumns.IndexOf(grid.CurrentColumn);
                if (startColumnIndex < 0) return false;

                PushUndoCheckpoint();
                EnsureRowCapacity(startRowIndex + matrix.Count);
                for (int rowOffset = 0; rowOffset < matrix.Count; rowOffset++)
                for (int columnOffset = 0; columnOffset < matrix[rowOffset].Length; columnOffset++)
                {
                    int columnIndex = startColumnIndex + columnOffset;
                    if (columnIndex >= editableColumns.Count) break;
                    var endpoint = _endpointColumns[editableColumns[columnIndex]];
                    SetEndpointText(
                        _rows[startRowIndex + rowOffset], endpoint.PortIndex,
                        endpoint.Property, matrix[rowOffset][columnOffset]);
                }
                FinishTabularPaste();
                return true;
            }

            if (Keyboard.FocusedElement is TextBox { Tag: CableSignalEditRow commonStart })
            {
                var parents = _rows.Where(row => !row.IsContinuation).ToList();
                int startIndex = parents.IndexOf(commonStart);
                if (startIndex < 0) return false;
                PushUndoCheckpoint();
                int required = startIndex + matrix.Count;
                while (parents.Count < required)
                {
                    parents.Add(AddEmptyRow());
                }
                for (int offset = 0; offset < matrix.Count; offset++)
                {
                    var source = matrix[offset];
                    parents[startIndex + offset].PinName = source.ElementAtOrDefault(0) ?? "";
                    if (source.Length > 1)
                        parents[startIndex + offset].WireType = source[1];
                }
                FinishTabularPaste();
                return true;
            }
            return false;
        }

        private bool TryPasteStructuredSignalCells()
        {
            string json;
            try
            {
                if (!Clipboard.ContainsData(SignalCellsClipboardFormat) ||
                    Clipboard.GetData(SignalCellsClipboardFormat) is not string stored)
                    return false;
                json = stored;
            }
            catch { return false; }

            var payload = JsonSerializer.Deserialize<SignalCellsClipboardPayload>(json);
            if (payload?.Rows is not { Count: > 0 }) return false;
            var grid = GetActiveSignalGrid();
            if (grid?.CurrentItem is not CableSignalEditRow startRow || grid.CurrentColumn == null)
                return false;
            var columns = grid.Columns
                .Where(column => _endpointColumns.ContainsKey(column) ||
                                 _shieldColumns.ContainsKey(column))
                .OrderBy(column => column.DisplayIndex)
                .ToList();
            int startColumn = columns.IndexOf(grid.CurrentColumn);
            int startRowIndex = _rows.IndexOf(startRow);
            if (startColumn < 0 || startRowIndex < 0) return false;

            PushUndoCheckpoint();
            EnsureRowCapacity(startRowIndex + payload.Rows.Count);
            int shieldPortIndex = -1;
            var shieldTokens = Enumerable.Repeat<string?>(null, payload.Rows.Count).ToList();
            for (int rowOffset = 0; rowOffset < payload.Rows.Count; rowOffset++)
            for (int columnOffset = 0; columnOffset < payload.Rows[rowOffset].Count; columnOffset++)
            {
                int targetColumnIndex = startColumn + columnOffset;
                if (targetColumnIndex >= columns.Count) break;
                var sourceCell = payload.Rows[rowOffset][columnOffset];
                var targetColumn = columns[targetColumnIndex];
                bool targetIsShield = _shieldColumns.TryGetValue(targetColumn, out int portIndex);
                if (sourceCell.IsShield != targetIsShield) continue;
                if (targetIsShield)
                {
                    shieldPortIndex = portIndex;
                    shieldTokens[rowOffset] = sourceCell.Value;
                }
                else if (_endpointColumns.TryGetValue(targetColumn, out var endpoint))
                {
                    SetEndpointText(_rows[startRowIndex + rowOffset], endpoint.PortIndex,
                        endpoint.Property, sourceCell.Value);
                    if (endpoint.Property == nameof(CablePin.PinNum))
                        _rows[startRowIndex + rowOffset].Endpoints[endpoint.PortIndex]
                            .MarkColorHex = sourceCell.MarkColorHex;
                }
            }
            if (shieldPortIndex >= 0 && shieldTokens.Any(token => token != null))
                ApplyShieldTokens(shieldPortIndex, startRowIndex,
                    shieldTokens.Select(token => token ?? "").ToList());
            FinishTabularPaste();
            SummaryText.Text = "선택한 셀과 쉴드 모양을 함께 붙여넣었습니다.";
            return true;
        }

        private DataGrid? GetActiveSignalGrid(bool requireMultipleSelection = false)
        {
            var focusedGrid = FindParent<DataGrid>(Keyboard.FocusedElement as DependencyObject);
            if (focusedGrid != null && _signalGrids.Contains(focusedGrid) &&
                (!requireMultipleSelection || focusedGrid.SelectedCells.Count >= 2))
                return focusedGrid;

            return _signalGrids.FirstOrDefault(grid =>
                (!requireMultipleSelection || grid.SelectedCells.Count >= 2) &&
                grid.CurrentItem is CableSignalEditRow && grid.CurrentColumn != null);
        }

        private void EnsureRowCapacity(int requiredCount)
        {
            while (_rows.Count < requiredCount) AddEmptyRow();
        }

        private static void SetEndpointText(
            CableSignalEditRow row, int portIndex, string property, string value)
        {
            if (portIndex < 0 || portIndex >= row.Endpoints.Count) return;
            var pin = row.Endpoints[portIndex];
            switch (property)
            {
                case nameof(CablePin.PinNum): pin.PinNum = value; break;
                case nameof(CablePin.InConn): pin.InConn = value; break;
                case nameof(CablePin.PinType): pin.PinType = value; break;
            }
        }

        private void FinishTabularPaste()
        {
            RenumberRows();
            RebuildCommonRows();
            UpdateGridHeights();
            InvalidateShieldVisuals();
            UpdateSummary();
        }

        private void CopySelectedShieldCells()
        {
            if (!TryGetSelectedShieldRange(out int portIndex, out var selectedRows)) return;
            _shieldClipboardTokens = selectedRows
                .Select(row => row.Endpoints[portIndex].TwpShield.Trim().ToLowerInvariant())
                .ToList();
            UpdateSummary();
        }

        private void PasteShieldCells()
        {
            if (_shieldClipboardTokens is not { Count: > 0 })
            {
                MessageBox.Show("먼저 복사할 쉴드 셀을 선택하세요.", "쉴드 붙여넣기",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!TryGetSelectedShieldRange(out int portIndex, out var selectedRows)) return;
            if (selectedRows.Count != 1 && selectedRows.Count != _shieldClipboardTokens.Count)
            {
                MessageBox.Show(
                    "붙여넣을 시작 셀 하나 또는 복사한 것과 같은 개수의 셀을 선택하세요.",
                    "쉴드 붙여넣기", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int startIndex = _rows.IndexOf(selectedRows[0]);
            if (startIndex < 0 || startIndex + _shieldClipboardTokens.Count > _rows.Count)
            {
                MessageBox.Show("붙여넣을 행 공간이 부족합니다.", "쉴드 붙여넣기",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            PushUndoCheckpoint();
            var targetRows = ApplyShieldTokens(portIndex, startIndex, _shieldClipboardTokens);
            _selectedShieldCells.Clear();
            foreach (var row in targetRows)
                _selectedShieldCells.Add((portIndex, row.Id));
            _shieldBondingMode = false;
            InvalidateShieldVisuals();
            UpdateSummary();
        }

        private List<CableSignalEditRow> ApplyShieldTokens(
            int portIndex, int startIndex, IReadOnlyList<string> sourceTokens)
        {
            if (portIndex < 0 || portIndex >= _ports.Count || startIndex < 0 ||
                startIndex + sourceTokens.Count > _rows.Count)
                return new List<CableSignalEditRow>();

            var targetRows = _rows.Skip(startIndex).Take(sourceTokens.Count).ToList();
            ClearShieldRelationsAtRows(portIndex, targetRows);

            var sourceGroups = sourceTokens
                .Select(token => TryParseShieldToken(token, 's', out int number) ? number : -1)
                .Where(number => number > 0)
                .Distinct()
                .ToList();
            int nextNumber = GetShieldNumbers(portIndex).DefaultIfEmpty(0).Max() + 10;
            var mapping = new Dictionary<int, int>();
            for (int index = 0; index < sourceGroups.Count; index++)
            {
                if (index == 0)
                    mapping[sourceGroups[index]] = nextNumber;
                else
                {
                    int previousSource = sourceGroups[index - 1];
                    int increment = sourceGroups[index] == previousSource + 1 ? 1 : 2;
                    mapping[sourceGroups[index]] = mapping[previousSource] + increment;
                }
            }

            for (int index = 0; index < targetRows.Count; index++)
            {
                string source = sourceTokens[index]?.Trim().ToLowerInvariant() ?? "";
                string target = "";
                if (TryParseShieldToken(source, 's', out int shieldNumber) &&
                    mapping.TryGetValue(shieldNumber, out int mappedShield))
                    target = $"s{mappedShield}";
                else if (TryParseShieldToken(source, 'c', out shieldNumber) &&
                         mapping.TryGetValue(shieldNumber, out mappedShield))
                    target = $"c{mappedShield}";
                targetRows[index].Endpoints[portIndex].TwpShield = target;
            }

            RenumberShieldGroups(portIndex);
            return targetRows;
        }

        private void ClearShieldRelationsAtRows(
            int portIndex, IReadOnlyCollection<CableSignalEditRow> targetRows)
        {
            var replacedGroups = targetRows
                .Select(row => row.Endpoints[portIndex].TwpShield)
                .Select(token => TryParseShieldToken(token, 's', out int number) ? number : -1)
                .Where(number => number > 0)
                .ToHashSet();

            foreach (var row in _rows)
            {
                var pin = row.Endpoints[portIndex];
                if ((TryParseShieldToken(pin.TwpShield, 's', out int number) ||
                     TryParseShieldToken(pin.TwpShield, 'c', out number)) &&
                    replacedGroups.Contains(number))
                    pin.TwpShield = "";
            }
            foreach (var row in targetRows)
                row.Endpoints[portIndex].TwpShield = "";
        }

        private static void DrawShieldCap(
            DrawingContext dc, Pen pen, double left, double right, double height, bool upper)
        {
            const double capDepth = 4;
            double endY = upper ? capDepth : Math.Max(capDepth, height - capDepth);
            double controlY = upper ? 0 : height;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new WpfPoint(left, endY), false, false);
                context.BezierTo(
                    new WpfPoint(left, controlY), new WpfPoint(right, controlY),
                    new WpfPoint(right, endY), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        private void CreateShieldButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetSelectedShieldRange(out int portIndex, out var selectedRows)) return;
            PushUndoCheckpoint();
            var replacedGroups = selectedRows
                .Select(row => row.Endpoints[portIndex].TwpShield)
                .Select(token => TryParseShieldToken(token, 's', out int number) ? number : -1)
                .Where(number => number > 0)
                .ToHashSet();
            if (replacedGroups.Count > 0)
            {
                foreach (var row in _rows)
                {
                    var pin = row.Endpoints[portIndex];
                    if ((TryParseShieldToken(pin.TwpShield, 's', out int number) ||
                         TryParseShieldToken(pin.TwpShield, 'c', out number)) &&
                        replacedGroups.Contains(number))
                        pin.TwpShield = "";
                }
            }
            int nextNumber = GetShieldNumbers(portIndex).DefaultIfEmpty(-1).Max() + 2;
            if (nextNumber < 1) nextNumber = 1;
            foreach (var row in selectedRows)
                row.Endpoints[portIndex].TwpShield = $"s{nextNumber}";
            RenumberShieldGroups(portIndex);
            _shieldBondingMode = false;
            InvalidateShieldVisuals();
            UpdateSummary();
        }

        private void BondShieldButton_Click(object sender, RoutedEventArgs e)
        {
            _shieldBondingMode = true;
            _selectedShieldCells.Clear();
            InvalidateShieldVisuals();
            UpdateSummary();
        }

        private void GroundShieldButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedShieldCells.Count != 1)
            {
                MessageBox.Show("쉴드에 접지할 핀 행 하나를 선택하세요.", "쉴드 접지",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var selected = _selectedShieldCells.First();
            int rowIndex = _rows.ToList().FindIndex(row => row.Id == selected.RowId);
            if (rowIndex < 0) return;
            if (TryParseShieldToken(
                    _rows[rowIndex].Endpoints[selected.PortIndex].TwpShield, 's', out _))
            {
                MessageBox.Show("쉴드 내부 핀 행이 아닌 별도의 접지 핀 행을 선택하세요.", "쉴드 접지",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var groups = GetShieldGroups(selected.PortIndex);
            if (groups.Count == 0)
            {
                MessageBox.Show("먼저 이 포트에 쉴드를 설정하세요.", "쉴드 접지",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            PushUndoCheckpoint();
            var nearest = groups.OrderBy(group => DistanceToRange(rowIndex, group.Start, group.End)).First();
            _rows[rowIndex].Endpoints[selected.PortIndex].TwpShield = $"c{nearest.Number}";
            _shieldBondingMode = false;
            InvalidateShieldVisuals();
            UpdateSummary();
        }

        private void ClearShieldButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedShieldCells.Count == 0)
            {
                MessageBox.Show("해제할 쉴드 셀을 선택하세요.", "쉴드 해제",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            PushUndoCheckpoint();
            foreach (var portGroup in _selectedShieldCells.GroupBy(cell => cell.PortIndex))
            {
                int portIndex = portGroup.Key;
                var shieldNumbers = new HashSet<int>();
                foreach (var cell in portGroup)
                {
                    var row = _rows.FirstOrDefault(candidate => candidate.Id == cell.RowId);
                    if (row == null) continue;
                    string token = row.Endpoints[portIndex].TwpShield;
                    if (TryParseShieldToken(token, 's', out int number)) shieldNumbers.Add(number);
                    else row.Endpoints[portIndex].TwpShield = "";
                }
                foreach (var row in _rows)
                {
                    if (TryParseShieldToken(row.Endpoints[portIndex].TwpShield, 's', out int number) &&
                        shieldNumbers.Contains(number))
                        row.Endpoints[portIndex].TwpShield = "";
                    else if (TryParseShieldToken(row.Endpoints[portIndex].TwpShield, 'c', out number) &&
                             shieldNumbers.Contains(number))
                        row.Endpoints[portIndex].TwpShield = "";
                }
                RenumberShieldGroups(portIndex);
            }
            _selectedShieldCells.Clear();
            _shieldBondingMode = false;
            InvalidateShieldVisuals();
            UpdateSummary();
        }

        private bool TryGetSelectedShieldRange(
            out int portIndex, out List<CableSignalEditRow> selectedRows)
        {
            portIndex = -1;
            selectedRows = new List<CableSignalEditRow>();
            if (_selectedShieldCells.Count == 0)
            {
                MessageBox.Show("쉴드로 묶을 셀을 드래그하여 선택하세요.", "쉴드 설정",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            portIndex = _selectedShieldCells.First().PortIndex;
            int selectedPortIndex = portIndex;
            if (_selectedShieldCells.Any(cell => cell.PortIndex != selectedPortIndex)) return false;
            selectedRows = _rows.Where(row =>
                _selectedShieldCells.Contains((selectedPortIndex, row.Id))).ToList();
            return selectedRows.Count > 0;
        }

        private bool TryBondAtBoundary(int portIndex, int rowIndex)
        {
            if (!TryGetBoundaryGroups(portIndex, rowIndex, out int upper, out int lower))
                return false;
            if (lower == upper + 1)
            {
                MessageBox.Show("이미 본딩된 쉴드입니다.", "쉴드 본딩",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                _shieldBondingMode = false;
                InvalidateShieldVisuals();
                return true;
            }
            PushUndoCheckpoint();
            RenumberShieldGroups(portIndex, (upper, lower));
            _shieldBondingMode = false;
            InvalidateShieldVisuals();
            UpdateSummary();
            return true;
        }

        private bool TryGetBoundaryGroups(
            int portIndex, int rowIndex, out int upper, out int lower)
        {
            upper = lower = -1;
            if (rowIndex < 0 || rowIndex + 1 >= _rows.Count) return false;
            if (!TryParseShieldToken(_rows[rowIndex].Endpoints[portIndex].TwpShield, 's', out upper) ||
                !TryParseShieldToken(_rows[rowIndex + 1].Endpoints[portIndex].TwpShield, 's', out lower) ||
                upper == lower)
                return false;
            return true;
        }

        private void RenumberShieldGroups(int portIndex, (int First, int Second)? newBond = null)
        {
            var groups = GetShieldGroups(portIndex);
            if (groups.Count == 0) return;
            var existingBonds = new HashSet<(int, int)>();
            for (int index = 0; index + 1 < groups.Count; index++)
            {
                if (groups[index + 1].Number == groups[index].Number + 1)
                    existingBonds.Add((groups[index].Number, groups[index + 1].Number));
            }
            if (newBond.HasValue)
                existingBonds.Add((newBond.Value.First, newBond.Value.Second));

            var mapping = new Dictionary<int, int>();
            int assigned = 1;
            mapping[groups[0].Number] = assigned;
            for (int index = 1; index < groups.Count; index++)
            {
                var previous = groups[index - 1];
                var current = groups[index];
                bool bonded = existingBonds.Contains((previous.Number, current.Number)) ||
                              existingBonds.Contains((current.Number, previous.Number));
                assigned += bonded ? 1 : 2;
                mapping[current.Number] = assigned;
            }

            foreach (var row in _rows)
            {
                var pin = row.Endpoints[portIndex];
                if (TryParseShieldToken(pin.TwpShield, 's', out int number) &&
                    mapping.TryGetValue(number, out int mapped))
                    pin.TwpShield = $"s{mapped}";
                else if (TryParseShieldToken(pin.TwpShield, 'c', out number) &&
                         mapping.TryGetValue(number, out mapped))
                    pin.TwpShield = $"c{mapped}";
            }
        }

        private List<ShieldGroupInfo> GetShieldGroups(int portIndex)
        {
            var groups = new List<ShieldGroupInfo>();
            for (int rowIndex = 0; rowIndex < _rows.Count; rowIndex++)
            {
                if (!TryParseShieldToken(
                        _rows[rowIndex].Endpoints[portIndex].TwpShield, 's', out int number))
                    continue;
                var existing = groups.FirstOrDefault(group => group.Number == number);
                if (existing == null)
                    groups.Add(new ShieldGroupInfo(number, rowIndex, rowIndex));
                else
                    existing.End = rowIndex;
            }
            return groups.OrderBy(group => group.Start).ToList();
        }

        private IEnumerable<int> GetShieldNumbers(int portIndex)
            => GetShieldGroups(portIndex).Select(group => group.Number);

        private static bool TryParseShieldToken(string? value, char prefix, out int number)
        {
            number = 0;
            value = value?.Trim().ToLowerInvariant();
            return value is { Length: > 1 } && value[0] == prefix &&
                   int.TryParse(value[1..], out number) && number > 0;
        }

        private int FindNearestShieldRow(int portIndex, int rowIndex, int shieldNumber)
            => GetShieldGroups(portIndex)
                .Where(group => group.Number == shieldNumber)
                .Select(group => rowIndex < group.Start ? group.Start : group.End)
                .OrderBy(candidate => Math.Abs(candidate - rowIndex))
                .FirstOrDefault(rowIndex);

        private static int DistanceToRange(int value, int start, int end)
            => value < start ? start - value : value > end ? value - end : 0;

        private sealed class ShieldGroupInfo
        {
            public ShieldGroupInfo(int number, int start, int end)
            {
                Number = number;
                Start = start;
                End = end;
            }

            public int Number { get; }
            public int Start { get; }
            public int End { get; set; }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            CommitPendingEdits();

            for (int portIndex = 0; portIndex < _ports.Count; portIndex++)
                RenumberShieldGroups(portIndex);

            foreach (var row in _rows)
            {
                row.PinName = row.PinName.Trim();
                row.WireType = row.WireType.Trim();
                foreach (var pin in row.Endpoints)
                {
                    pin.PinNum = pin.PinNum.Trim();
                    pin.PinType = pin.PinType.Trim();
                    pin.InConn = pin.InConn.Trim();
                    pin.TwpShield = pin.TwpShield.Trim().ToLowerInvariant();
                }
            }

            for (int portIndex = 0; portIndex < _ports.Count; portIndex++)
            {
                string portName = string.IsNullOrWhiteSpace(_ports[portIndex].PortName)
                    ? $"포트 {portIndex + 1}"
                    : _ports[portIndex].PortName;
                var endpoints = _rows.Select(row => row.Endpoints[portIndex]).ToList();
                var duplicate = endpoints
                    .Select(pin => pin.PinNum.Trim())
                    .Where(pinNumber => !string.IsNullOrWhiteSpace(pinNumber) &&
                        !string.Equals(pinNumber, "TBM", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(pinNumber => pinNumber, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicate != null)
                {
                    MessageBox.Show($"{portName}의 핀 번호 '{duplicate.Key}'가 중복되었습니다.",
                        "확인", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var invalidConnection = endpoints.FirstOrDefault(pin =>
                    !string.IsNullOrWhiteSpace(pin.InConn) &&
                    !endpoints.Any(target => !ReferenceEquals(target, pin) &&
                        string.Equals(target.PinNum, pin.InConn, StringComparison.OrdinalIgnoreCase)));
                if (invalidConnection != null)
                {
                    MessageBox.Show($"{portName}의 내부 연결 대상 '{invalidConnection.InConn}'을 찾을 수 없습니다.",
                        "확인", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var invalidShield = endpoints.FirstOrDefault(pin =>
                    !string.IsNullOrWhiteSpace(pin.TwpShield) &&
                    !ShieldTokenPattern.IsMatch(pin.TwpShield));
                if (invalidShield != null)
                {
                    MessageBox.Show($"{portName}의 쉴드 값 '{invalidShield.TwpShield}' 형식이 올바르지 않습니다. s1 또는 c1처럼 입력하세요.",
                        "확인", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var shieldNumbers = endpoints.Where(pin => pin.TwpShield.StartsWith("s"))
                    .Select(pin => pin.TwpShield[1..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missingShield = endpoints.FirstOrDefault(pin =>
                    pin.TwpShield.StartsWith("c") && !shieldNumbers.Contains(pin.TwpShield[1..]));
                if (missingShield != null)
                {
                    MessageBox.Show($"{portName}의 '{missingShield.TwpShield}'에 대응하는 쉴드가 없습니다.",
                        "확인", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // 사용자가 추가한 빈 회선도 하나의 명시적인 행이다. 내용 유무로
            // 걸러내지 않고 현재 표에 보이는 모든 행을 순서대로 보존한다.
            ResultSignalRows = _rows
                .Select(ToModel)
                .ToList();
            ResultOverallBraid = OverallBraidCheck.IsChecked == true;
            ResultTube = TubeCheck.IsChecked == true;
            _allowClose = true;
            DialogResult = true;
        }

        private void CommitPendingEdits()
        {
            foreach (var grid in _signalGrids)
            {
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
            }
            if (_editingRow != null && _editingPortIndex >= 0)
                SynchronizeInternalConnection(
                    _editingRow, _editingPortIndex, _originalInConn);
        }

        private string CaptureStateSnapshot()
            => JsonSerializer.Serialize(new EditorState
            {
                Rows = _rows.Select(row => new EditorRowState
                {
                    Id = row.Id,
                    ParentSignalId = row.ParentSignalId,
                    PinName = row.PinName,
                    WireType = row.WireType,
                    Endpoints = row.Endpoints.Select(ClonePin).ToList()
                }).ToList(),
                Connectors = _ports.Select(port => new ConnectorTextState
                {
                    PortName = port.PortName,
                    ConnType = port.ConnType,
                    ConnectorCatalogKey = port.ConnectorCatalogKey
                }).ToList(),
                OverallBraid = OverallBraidCheck.IsChecked == true,
                Tube = TubeCheck.IsChecked == true
            });

        private void PushUndoCheckpoint()
        {
            if (_isRestoringState) return;
            string state = CaptureStateSnapshot();
            if (_undoHistory.Count == 0 ||
                !string.Equals(_undoHistory[^1], state, StringComparison.Ordinal))
            {
                _undoHistory.Add(state);
                if (_undoHistory.Count > 100) _undoHistory.RemoveAt(0);
            }
            _redoHistory.Clear();
        }

        private void UndoEditorChange()
        {
            CommitPendingEdits();
            string current = CaptureStateSnapshot();
            if (_undoHistory.Count == 0 ||
                !string.Equals(_undoHistory[^1], current, StringComparison.Ordinal))
                _undoHistory.Add(current);
            if (_undoHistory.Count <= 1) return;

            string removed = _undoHistory[^1];
            _undoHistory.RemoveAt(_undoHistory.Count - 1);
            _redoHistory.Add(removed);
            RestoreStateSnapshot(_undoHistory[^1]);
            SummaryText.Text = "이전 신호 상세 편집 상태로 되돌렸습니다.";
        }

        private void RedoEditorChange()
        {
            if (_redoHistory.Count == 0) return;
            string state = _redoHistory[^1];
            _redoHistory.RemoveAt(_redoHistory.Count - 1);
            if (_undoHistory.Count == 0 ||
                !string.Equals(_undoHistory[^1], state, StringComparison.Ordinal))
                _undoHistory.Add(state);
            RestoreStateSnapshot(state);
            SummaryText.Text = "되돌린 신호 상세 편집을 다시 적용했습니다.";
        }

        private void RestoreStateSnapshot(string snapshot)
        {
            var state = JsonSerializer.Deserialize<EditorState>(snapshot);
            if (state == null) return;
            _isRestoringState = true;
            try
            {
                _rows.Clear();
                foreach (var source in state.Rows)
                {
                    var row = new CableSignalEditRow
                    {
                        Id = string.IsNullOrWhiteSpace(source.Id) ? Guid.NewGuid().ToString() : source.Id,
                        ParentSignalId = source.ParentSignalId,
                        PinName = source.PinName,
                        WireType = source.WireType
                    };
                    for (int portIndex = 0; portIndex < _ports.Count; portIndex++)
                        row.Endpoints.Add(portIndex < source.Endpoints.Count
                            ? ClonePin(source.Endpoints[portIndex])
                            : new CablePin());
                    _rows.Add(row);
                }
                if (_rows.Count == 0) AddEmptyRow();
                for (int index = 0; index < _ports.Count && index < state.Connectors.Count; index++)
                {
                    _ports[index].PortName = state.Connectors[index].PortName;
                    _ports[index].ConnType = state.Connectors[index].ConnType;
                    _ports[index].ConnectorCatalogKey = state.Connectors[index].ConnectorCatalogKey;
                }
                OverallBraidCheck.IsChecked = state.OverallBraid;
                TubeCheck.IsChecked = state.Tube;
                _selectedSignalRow = null;
                _selectedShieldCells.Clear();
                _selectedCommonSignalIds.Clear();
                _orderBeforeEndpointSort = null;
                RenumberRows();
                BuildTables();
                InvalidateShieldVisuals();
                UpdateSummary();
            }
            finally
            {
                _isRestoringState = false;
            }
        }

        private bool HasUnsavedChanges()
        {
            CommitPendingEdits();
            return !string.Equals(
                _initialStateSnapshot,
                CaptureStateSnapshot(),
                StringComparison.Ordinal);
        }

        private MessageBoxResult AskToSaveChanges()
            => MessageBox.Show(
                this,
                "신호 상세설정에 변경사항이 있습니다. 저장하시겠습니까?",
                "변경사항 저장",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_allowClose) return;
            if (!HasUnsavedChanges())
            {
                _allowClose = true;
                return;
            }

            switch (AskToSaveChanges())
            {
                case MessageBoxResult.Yes:
                    e.Cancel = true;
                    Dispatcher.BeginInvoke(new Action(() =>
                        OkButton_Click(this, new RoutedEventArgs())));
                    break;
                case MessageBoxResult.No:
                    RestoreInitialConnectorText();
                    _allowClose = true;
                    break;
                default:
                    e.Cancel = true;
                    break;
            }
        }

        private CableSignalRow ToModel(CableSignalEditRow row)
        {
            var result = new CableSignalRow
            {
                Id = row.Id,
                ParentSignalId = row.ParentSignalId,
                PinName = row.PinName,
                WireType = row.WireType
            };
            for (int i = 0; i < _ports.Count; i++)
                result.PortPins[_ports[i].ConnectorId] = ClonePin(row.Endpoints[i]);
            return result;
        }

        private static CablePin ClonePin(CablePin pin) => new()
        {
            PinNum = pin.PinNum,
            PinName = pin.PinName,
            PinType = pin.PinType,
            InConn = pin.InConn,
            TwpShield = pin.TwpShield,
            MarkColorHex = pin.MarkColorHex
        };

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (!HasUnsavedChanges())
            {
                _allowClose = true;
                DialogResult = false;
                return;
            }

            switch (AskToSaveChanges())
            {
                case MessageBoxResult.Yes:
                    OkButton_Click(sender, e);
                    break;
                case MessageBoxResult.No:
                    RestoreInitialConnectorText();
                    _allowClose = true;
                    DialogResult = false;
                    break;
            }
        }

        private void RestoreInitialConnectorText()
        {
            for (int index = 0; index < _ports.Count && index < _initialConnectorText.Count; index++)
            {
                _ports[index].PortName = _initialConnectorText[index].PortName;
                _ports[index].ConnType = _initialConnectorText[index].ConnType;
                _ports[index].ConnectorCatalogKey = _initialConnectorText[index].ConnectorCatalogKey;
            }
        }
    }
}
