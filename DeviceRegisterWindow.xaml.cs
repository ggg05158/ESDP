using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ESDP.Models;

namespace ESDP
{
    public partial class DeviceRegisterWindow : Window
    {
        private readonly ObservableCollection<PortEditRow> _ports = new();
        private bool _portSlotsReady;
        private bool _updatingPortSlots;
        private readonly Dictionary<string, (string Side, double Position)> _portSlotState = new();
        private readonly Device? _editing;
        public Device? ResultDevice { get; private set; }
        public string ResultPurpose { get; private set; } = "";
        public string? ResultZoneId { get; private set; }
        private readonly bool _editInstancePurpose;

        // 미리보기 캔버스 안에서 장치 박스가 그려지는 위치/크기 (드래그 좌표 변환용)
        private double _previewShiftX = 30;
        private double _previewShiftY = 20;
        private double _previewW = 120, _previewH = 80;
        private PortEditRow? _previewDragRow;
        private string _backgroundColorHex = "#FFFFFF";
        private string _backgroundImageBase64 = "";
        private string _backgroundImageFileName = "";
        private bool _backgroundImageStretchExplicit;
        private bool _hasCustomNamePosition;
        private double _namePositionX = 0.5;
        private double _namePositionY = 0.5;
        private bool _previewDraggingName;
        private double _previewNameDragOffsetX;
        private double _previewNameDragOffsetY;
        private readonly List<string> _customKindNames;
        public IReadOnlyList<string> CustomKindNames => _customKindNames;

        // 편집 대상 개체의 실제 현재 크기 (더블클릭으로 열었을 때 미리보기에 그대로 반영하기 위함).
        // null 이면 포트 개수 기반 기본 크기를 쓴다 (신규 등록/템플릿 편집 시).
        private readonly double? _hintW;
        private readonly double? _hintH;
        private readonly IReadOnlyDictionary<string, string>? _categoryColors;

        public DeviceRegisterWindow(
            Device? existing = null,
            double? previewWidthHint = null,
            double? previewHeightHint = null,
            DeviceKind? preferredKind = null,
            IEnumerable<string>? customKindNames = null,
            string? preferredCustomKind = null,
            bool editInstancePurpose = false,
            string? instancePurpose = null,
            IEnumerable<Zone>? availableZones = null,
            string? instanceZoneId = null,
            IEnumerable<string>? categoryNames = null,
            IReadOnlyDictionary<string, string>? categoryColors = null)
        {
            InitializeComponent();
            _editInstancePurpose = editInstancePurpose;
            BackgroundImageStretchCombo.SelectedIndex = 0;
            _hintW = previewWidthHint;
            _hintH = previewHeightHint;
            _categoryColors = categoryColors;

            _customKindNames = customKindNames?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList()
                ?? new List<string>();
            foreach (string name in (categoryNames ?? Enum.GetNames<DeviceKind>().Concat(_customKindNames))
                         .Distinct(StringComparer.OrdinalIgnoreCase)) KindCombo.Items.Add(name);

            var palette = ColorOption.BuildPalette();
            TextColorCombo.ItemsSource = palette;
            TextColorCombo.DisplayMemberPath = "Name";

            _ports.CollectionChanged += (_, __) => UpdatePreview();
            PortItems.ItemsSource = _ports;

            _editing = existing;

            if (existing == null)
            {
                Title = "장치 등록";
                if (!string.IsNullOrWhiteSpace(preferredCustomKind))
                    KindCombo.SelectedItem = preferredCustomKind;
                else
                    KindCombo.SelectedItem = (preferredKind ?? DeviceKind.주장치).ToString();
                BoldCheck.IsChecked = true;
                FontSizeBox.Text = "10";
                SelectColorByHex("#000000");
            }
            else
            {
                Title = $"장치 수정 - {existing.Name}";
                NameBox.Text = existing.Name;
                KindCombo.SelectedItem = string.IsNullOrWhiteSpace(existing.CustomKindName)
                    ? existing.Kind.ToString() : existing.CustomKindName;

                BoldCheck.IsChecked = existing.NameBold;
                ItalicCheck.IsChecked = existing.NameItalic;
                UnderlineCheck.IsChecked = existing.NameUnderline;
                FontSizeBox.Text = existing.NameFontSize.ToString();
                SelectColorByHex(existing.NameColorHex);
                _backgroundColorHex =
                    ColorOption.NormalizeHex(existing.BackgroundColorHex) ?? "#FFFFFF";
                _backgroundImageBase64 = existing.BackgroundImageBase64;
                _backgroundImageFileName = existing.BackgroundImageFileName;
                _backgroundImageStretchExplicit =
                    existing.HasExplicitBackgroundImageStretch;
                _hasCustomNamePosition = existing.HasCustomNamePosition;
                _namePositionX = Math.Clamp(existing.NamePositionX, 0, 1);
                _namePositionY = Math.Clamp(existing.NamePositionY, 0, 1);
                string savedStretch = _backgroundImageStretchExplicit
                    ? existing.BackgroundImageStretch
                    : "Uniform";
                BackgroundImageStretchCombo.SelectedItem =
                    BackgroundImageStretchCombo.Items.OfType<ComboBoxItem>()
                        .FirstOrDefault(item => string.Equals(
                            item.Tag?.ToString(), savedStretch,
                            StringComparison.OrdinalIgnoreCase))
                    ?? BackgroundImageStretchCombo.Items[0];
                BackgroundImageOpacityBox.Text =
                    (Math.Clamp(existing.BackgroundImageOpacity, 0, 1) * 100)
                    .ToString("0");

                foreach (var p in existing.Ports)
                {
                    var row = new PortEditRow
                    {
                        Id = p.Id,
                        Designator = p.Designator,
                        Side = p.Side.ToString(),
                        Position = p.Position,
                        ConnType = p.ConnType,
                        ConnectorCatalogKey = p.ConnectorCatalogKey,
                        Description = p.Description,
                        ShowConnType = p.ShowConnType,
                        ShowLabel = p.ShowLabel,
                        LabelFontSize = p.LabelFontSize,
                        LabelColorHex = p.LabelColorHex,
                        HasCustomLabelPosition = p.HasCustomLabelPosition,
                        LabelOffsetX = p.LabelOffsetX,
                        LabelOffsetY = p.LabelOffsetY
                    };
                    row.PropertyChanged += PortRowChanged;
                    _ports.Add(row);
                }
            }

            if (_editInstancePurpose)
            {
                PurposeLabel.Visibility = Visibility.Visible;
                PurposeBox.Visibility = Visibility.Visible;
                NameBox.Width = 180;
                PurposeBox.Text = instancePurpose ?? "";
                ZoneLabel.Visibility = Visibility.Visible;
                ZoneCombo.Visibility = Visibility.Visible;
                ZoneCombo.Items.Add(new ComboBoxItem
                    { Content = "(구역 없음)", Tag = "" });
                foreach (Zone zone in availableZones ?? Enumerable.Empty<Zone>())
                    ZoneCombo.Items.Add(new ComboBoxItem
                        { Content = zone.Name, Tag = zone.Id });
                ZoneCombo.SelectedItem = ZoneCombo.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(
                        item.Tag?.ToString(), instanceZoneId,
                        StringComparison.Ordinal))
                    ?? ZoneCombo.Items[0];
            }

            if (KindCombo.SelectedItem == null && KindCombo.Items.Count > 0) KindCombo.SelectedIndex = 0;
            UpdateBackgroundColorControls();
            UpdateBackgroundImageControls();
            UpdatePreview();
            _portSlotsReady = true;
            CommitPortSlots();
        }

        private void KindCombo_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            UpdateBackgroundColorControls();
            UpdatePreview();
        }

        private void UpdateBackgroundColorControls()
        {
            if (BackgroundColorPickerButton == null || BackgroundColorSwatch == null)
                return;

            string category = KindCombo.SelectedItem?.ToString() ?? "";
            if (_categoryColors != null && _categoryColors.TryGetValue(category, out string? categoryHex))
            {
                BackgroundColorPickerButton.IsEnabled = false;
                BackgroundColorPanel.Opacity = 0.75;
                BackgroundColorSwatch.Fill = ColorOption.BrushFromHex(categoryHex);
                BackgroundColorHexText.Text = "범주 공통색 (범주 우클릭에서 변경)";
                return;
            }

            bool isOther = !Enum.TryParse(KindCombo.SelectedItem?.ToString(), out DeviceKind selectedKind) ||
                           selectedKind == DeviceKind.기타;
            BackgroundColorPickerButton.IsEnabled = isOther;
            BackgroundColorPanel.Opacity = isOther ? 1 : 0.55;

            string shownHex = isOther
                ? _backgroundColorHex
                : AppSettings.KindColors.TryGetValue(selectedKind, out string? hex)
                    ? hex
                    : "#FFFFFF";
            BackgroundColorSwatch.Fill = ColorOption.BrushFromHex(shownHex);
            BackgroundColorHexText.Text = isOther
                ? shownHex
                : "종류별 공통색";
        }

        private void BackgroundColorPickerButton_Click(
            object sender, RoutedEventArgs e)
        {
            if (Enum.TryParse(KindCombo.SelectedItem?.ToString(), out DeviceKind kind) &&
                kind != DeviceKind.기타)
                return;

            if (!ColorPickerWindow.TryPick(
                    this, _backgroundColorHex, out string hex, "기타 장치 배경색"))
                return;

            _backgroundColorHex = hex;
            UpdateBackgroundColorControls();
            UpdatePreview();
        }

        private void BackgroundImageButton_Click(
            object sender, RoutedEventArgs e)
        {
            if (!ProjectImageService.TryImport(this, out ProjectImageData image))
                return;
            _backgroundImageBase64 = image.Base64;
            _backgroundImageFileName = image.FileName;
            UpdateBackgroundImageControls();
            UpdatePreview();
        }

        private void RemoveBackgroundImageButton_Click(
            object sender, RoutedEventArgs e)
        {
            _backgroundImageBase64 = "";
            _backgroundImageFileName = "";
            _previewDraggingName = false;
            UpdateBackgroundImageControls();
            UpdatePreview();
        }

        private void ResetNamePositionButton_Click(
            object sender, RoutedEventArgs e)
        {
            _hasCustomNamePosition = false;
            _namePositionX = 0.5;
            _namePositionY = 0.5;
            UpdatePreview();
        }

        private void NamePositionButton_Click(
            object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string value }) return;
            string[] parts = value.Split(',');
            if (parts.Length != 2 ||
                !double.TryParse(parts[0],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double x) ||
                !double.TryParse(parts[1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double y))
                return;

            _hasCustomNamePosition = true;
            _namePositionX = Math.Clamp(x, 0, 1);
            _namePositionY = Math.Clamp(y, 0, 1);
            UpdatePreview();
        }

        private void BackgroundImageOption_Changed(
            object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            _backgroundImageStretchExplicit = true;
            UpdatePreview();
        }

        private void BackgroundImageOpacity_Changed(
            object sender, TextChangedEventArgs e)
        {
            if (IsLoaded) UpdatePreview();
        }

        private void UpdateBackgroundImageControls()
        {
            if (BackgroundImageNameText == null) return;
            bool hasImage = !string.IsNullOrWhiteSpace(_backgroundImageBase64);
            BackgroundImageNameText.Text = hasImage
                ? _backgroundImageFileName
                : "이미지 없음";
            RemoveBackgroundImageButton.IsEnabled = hasImage;
            ResetNamePositionButton.IsEnabled = true;
            BackgroundImageStretchCombo.IsEnabled = hasImage;
            BackgroundImageOpacityBox.IsEnabled = hasImage;
            NameDragHintText.Visibility = Visibility.Visible;
        }

        private void SelectColorByHex(string hex)
        {
            foreach (ColorOption o in TextColorCombo.Items)
            {
                if (string.Equals(o.Hex, hex, System.StringComparison.OrdinalIgnoreCase))
                {
                    TextColorCombo.SelectedItem = o;
                    return;
                }
            }
            TextColorCombo.SelectedIndex = 0;
        }

        private void TextColorPickerButton_Click(
            object sender, RoutedEventArgs e)
        {
            string current =
                (TextColorCombo.SelectedItem as ColorOption)?.Hex ?? "#000000";
            if (!ColorPickerWindow.TryPick(
                    this, current, out string hex, "장치 글자 색"))
                return;

            var options = TextColorCombo.ItemsSource as List<ColorOption>;
            var selected = options?.FirstOrDefault(option =>
                option.Hex.Equals(
                    hex, System.StringComparison.OrdinalIgnoreCase));
            if (selected == null)
            {
                selected = new ColorOption { Name = hex, Hex = hex };
                options?.Add(selected);
                TextColorCombo.Items.Refresh();
            }
            TextColorCombo.SelectedItem = selected;
            UpdatePreview();
        }

        private void AddPortButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CommitPortSlots()) { ShowPortSpaceWarning(); return; }
            var existing = _ports.Select(r => new Port
            {
                Id = r.Id,
                Side = Enum.Parse<PortSide>(r.Side),
                Position = r.Position
            }).ToList();
            if (!DevicePortSlots.TryChooseSideForNewPort(
                    existing, _previewW, _previewH,
                    _ports.Count % Enum.GetValues<PortSide>().Length,
                    out PortSide selectedSide))
            {
                ShowPortSpaceWarning();
                return;
            }

            var row = new PortEditRow
            {
                Designator = "",
                Side = selectedSide.ToString(),
                Position = -1
            };
            row.PropertyChanged += PortRowChanged;
            _ports.Add(row);
            if (!CommitPortSlots())
            {
                _ports.Remove(row);
                ShowPortSpaceWarning();
            }
            UpdatePreview();
        }

        private void ShowPortSpaceWarning() => MessageBox.Show(this,
            "해당 변에 포트를 배치할 빈 칸이 없거나 기존 포트가 겹쳐 있습니다.\n장치 크기를 늘려 공간을 확보하거나 포트 위치를 조정해 주세요.",
            "포트 배치 공간 부족", MessageBoxButton.OK, MessageBoxImage.Warning);

        private bool CommitPortSlots()
        {
            var ports = _ports.Select(r => new Port { Id = r.Id,
                Side = Enum.Parse<PortSide>(r.Side), Position = r.Position }).ToList();
            if (!DevicePortSlots.TryAssign(ports, _previewW, _previewH, out var positions)) return false;
            _updatingPortSlots = true;
            try
            {
                foreach (var row in _ports)
                {
                    row.Position = positions[row.Id];
                    _portSlotState[row.Id] = (row.Side, row.Position);
                }
            }
            finally { _updatingPortSlots = false; }
            return true;
        }

        private void PortRowChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_updatingPortSlots) return;
            if (_portSlotsReady && sender is PortEditRow row &&
                e.PropertyName is nameof(PortEditRow.Side) or nameof(PortEditRow.Position))
            {
                if (e.PropertyName == nameof(PortEditRow.Side))
                {
                    _updatingPortSlots = true;
                    row.Position = -1;
                    _updatingPortSlots = false;
                }
                if (!CommitPortSlots())
                {
                    _updatingPortSlots = true;
                    if (_portSlotState.TryGetValue(row.Id, out var old))
                    { row.Side = old.Side; row.Position = old.Position; }
                    _updatingPortSlots = false;
                    if (_previewDragRow == null) ShowPortSpaceWarning();
                }
            }
            UpdatePreview();
        }

        private void RemovePortButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is PortEditRow row)
                _ports.Remove(row);
        }

        private void AnyField_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

        /// <summary>
        /// 현재 입력 내용을 임시 Device 로 만들어 미리보기에 그리고,
        /// 각 포트 사각형에 "잡아서 끌기" 핸들러를 다시 붙인다.
        /// (Draw 는 매번 자식을 새로 만들기 때문에 그릴 때마다 다시 연결해야 한다)
        /// </summary>
        private void UpdatePreview()
        {
            if (PreviewCanvas == null) return;

            // BuildDeviceFromInputs 는 _ports 를 순서대로(빈 이름 제외) 훑어 Port 를 새로 만든다.
            // 그 순서가 아래 nonEmptyRows 와 1:1로 같으므로, 만들어진 Port 객체와 원본 행을
            // 인덱스로 짝지을 수 있다.
            var nonEmptyRows = _ports.Where(r => !string.IsNullOrWhiteSpace(r.Designator)).ToList();
            var temp = BuildDeviceFromInputs(new Device());

            var portToRow = new Dictionary<Port, PortEditRow>();
            for (int i = 0; i < temp.Ports.Count && i < nonEmptyRows.Count; i++)
                portToRow[temp.Ports[i]] = nonEmptyRows[i];

            var (minW, minH) = DevicePreviewRenderer.GetMinSize(temp);
            double w = Math.Ceiling(
                (_hintW ?? Math.Max(minW, 120)) /
                DevicePreviewRenderer.PortSlotSize) *
                DevicePreviewRenderer.PortSlotSize;
            double h = Math.Ceiling(
                (_hintH ?? Math.Max(minH, 80)) /
                DevicePreviewRenderer.PortSlotSize) *
                DevicePreviewRenderer.PortSlotSize;
            _previewW = w;
            _previewH = h;

            DevicePreviewRenderer.Draw(
                PreviewCanvas, temp, w, h,
                purpose: _editInstancePurpose ? PurposeBox.Text : null);

            Rect visualBounds = Rect.Empty;
            foreach (FrameworkElement child in PreviewCanvas.Children)
            {
                child.Measure(new Size(
                    double.PositiveInfinity, double.PositiveInfinity));
                double left = Canvas.GetLeft(child);
                double top = Canvas.GetTop(child);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;
                double childWidth = double.IsNaN(child.Width)
                    ? child.DesiredSize.Width
                    : child.Width;
                double childHeight = double.IsNaN(child.Height)
                    ? child.DesiredSize.Height
                    : child.Height;
                var childBounds = new Rect(
                    left, top, Math.Max(1, childWidth), Math.Max(1, childHeight));
                if (visualBounds.IsEmpty) visualBounds = childBounds;
                else visualBounds.Union(childBounds);
            }

            const double previewMargin = 28;
            if (visualBounds.IsEmpty) visualBounds = new Rect(0, 0, w, h);
            _previewShiftX = previewMargin - visualBounds.Left;
            _previewShiftY = previewMargin - visualBounds.Top;
            PreviewCanvas.Width = visualBounds.Width + previewMargin * 2;
            PreviewCanvas.Height = visualBounds.Height + previewMargin * 2;

            foreach (UIElement child in PreviewCanvas.Children)
            {
                // 여백만큼 전체를 밀어서 보기 좋게 표시
                Canvas.SetLeft(child, Canvas.GetLeft(child) + _previewShiftX);
                Canvas.SetTop(child, Canvas.GetTop(child) + _previewShiftY);

                // 포트 사각형이면 드래그 시작 핸들러를 붙인다.
                if (child is Rectangle rect && rect.Tag is Port p && portToRow.TryGetValue(p, out var row))
                {
                    rect.Cursor = Cursors.Hand;
                    rect.MouseLeftButtonDown += (s, e) =>
                    {
                        _previewDragRow = row;
                        PreviewCanvas.CaptureMouse();
                        e.Handled = true;
                    };
                }
            }

            var namePlate = FindDeviceNameVisual(PreviewCanvas);
            if (namePlate?.Tag is DevicePreviewRenderer.DeviceNameVisualInfo nameInfo)
            {
                namePlate.Cursor = Cursors.SizeAll;
                namePlate.ToolTip = "장치 이름 위치 이동";
                namePlate.MouseLeftButtonDown += (_, e) =>
                {
                    System.Windows.Point mouse = e.GetPosition(PreviewCanvas);
                    _previewDraggingName = true;
                    _previewNameDragOffsetX =
                        mouse.X - (_previewShiftX + nameInfo.CenterX);
                    _previewNameDragOffsetY =
                        mouse.Y - (_previewShiftY + nameInfo.CenterY);
                    PreviewCanvas.CaptureMouse();
                    e.Handled = true;
                };
            }
        }

        private static FrameworkElement? FindDeviceNameVisual(
            DependencyObject root)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < childCount; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                if (child is FrameworkElement element &&
                    element.Tag is DevicePreviewRenderer.DeviceNameVisualInfo)
                    return element;
                FrameworkElement? nested = FindDeviceNameVisual(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private void PreviewCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_previewDraggingName)
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                System.Windows.Point nameMouse = e.GetPosition(PreviewCanvas);
                double centerX = nameMouse.X - _previewShiftX - _previewNameDragOffsetX;
                double centerY = nameMouse.Y - _previewShiftY - _previewNameDragOffsetY;
                _hasCustomNamePosition = true;
                _namePositionX = Math.Clamp(centerX / Math.Max(1, _previewW), 0, 1);
                _namePositionY = Math.Clamp(centerY / Math.Max(1, _previewH), 0, 1);
                UpdatePreview();
                return;
            }

            if (_previewDragRow == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var pos = e.GetPosition(PreviewCanvas);
            double localX = pos.X - _previewShiftX;
            double localY = pos.Y - _previewShiftY;

            var (side, frac) = DevicePreviewRenderer.NearestEdge(localX, localY, _previewW, _previewH);
            double sideLength = side is PortSide.Top or PortSide.Bottom
                ? _previewW
                : _previewH;
            int slot = DevicePreviewRenderer.GetPortSlotIndex(frac, sideLength);
            frac = ((slot + 0.5) * DevicePreviewRenderer.PortSlotSize) /
                   sideLength;
            _updatingPortSlots = true;
            _previewDragRow.Side = side.ToString();
            _previewDragRow.Position = frac;
            _updatingPortSlots = false;
            PortRowChanged(_previewDragRow, new PropertyChangedEventArgs(nameof(PortEditRow.Position)));
        }

        private void PreviewCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _previewDragRow = null;
            _previewDraggingName = false;
            PreviewCanvas.ReleaseMouseCapture();
        }

        private void PortLabelColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: PortEditRow row }) return;
            if (!ColorPickerWindow.TryPick(
                    this, row.LabelColorHex, out string hex, "포트 이름 글자색"))
                return;
            row.LabelColorHex = hex;
            UpdatePreview();
        }

        private Device BuildDeviceFromInputs(Device target)
        {
            target.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "(이름 없음)" : NameBox.Text.Trim();
            target.NameRtfBase64 = "";
            string selectedCategory = KindCombo.SelectedItem?.ToString() ?? DeviceKind.주장치.ToString();
            target.CategoryColorHex = _categoryColors?.GetValueOrDefault(selectedCategory, "") ?? "";
            if (Enum.TryParse(selectedCategory, out DeviceKind selectedKind))
            {
                target.Kind = selectedKind;
                target.CustomKindName = "";
            }
            else
            {
                target.Kind = DeviceKind.기타;
                target.CustomKindName = selectedCategory;
            }
            target.NameBold = BoldCheck.IsChecked == true;
            target.NameItalic = ItalicCheck.IsChecked == true;
            target.NameUnderline = UnderlineCheck.IsChecked == true;
            target.NameFontSize = double.TryParse(FontSizeBox.Text, out var fs) ? fs : 10;
            target.NameColorHex = (TextColorCombo.SelectedItem as ColorOption)?.Hex ?? "#000000";
            target.HasCustomNamePosition = _hasCustomNamePosition;
            target.NamePositionX = Math.Clamp(_namePositionX, 0, 1);
            target.NamePositionY = Math.Clamp(_namePositionY, 0, 1);
            target.BackgroundColorHex =
                ColorOption.NormalizeHex(_backgroundColorHex) ?? "#FFFFFF";
            target.BackgroundImageBase64 = _backgroundImageBase64;
            target.BackgroundImageFileName = _backgroundImageFileName;
            target.BackgroundImageStretch =
                (BackgroundImageStretchCombo.SelectedItem as ComboBoxItem)?
                    .Tag?.ToString() ?? "Uniform";
            target.HasExplicitBackgroundImageStretch =
                _backgroundImageStretchExplicit;
            target.BackgroundImageOpacity =
                double.TryParse(BackgroundImageOpacityBox.Text,
                    out double imageOpacity)
                    ? Math.Clamp(imageOpacity / 100, 0, 1)
                    : 1;

            target.Ports.Clear();
            foreach (var row in _ports)
            {
                if (string.IsNullOrWhiteSpace(row.Designator)) continue;
                target.Ports.Add(new Port
                {
                    Id = row.Id,
                    Designator = row.Designator.Trim(),
                    Side = System.Enum.TryParse<PortSide>(row.Side, out var s) ? s : PortSide.Left,
                    Position = row.Position,
                    ConnType = row.ConnType.Trim(),
                    ConnectorCatalogKey = row.ConnectorCatalogKey,
                    Description = row.Description.Trim(),
                    ShowConnType = row.ShowConnType,
                    ShowLabel = row.ShowLabel,
                    LabelFontSize = row.LabelFontSize > 0 ? row.LabelFontSize : 11,
                    LabelColorHex = ColorOption.NormalizeHex(row.LabelColorHex) ?? "#7F7F7F",
                    HasCustomLabelPosition = row.HasCustomLabelPosition,
                    LabelOffsetX = row.LabelOffsetX,
                    LabelOffsetY = row.LabelOffsetY
                });
            }
            return target;
        }

        private void D38999SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: PortEditRow row }) return;
            try
            {
                var selector = new D38999ConnectorSelectorWindow(
                    row.ConnType, row.ConnectorCatalogKey) { Owner = this };
                if (selector.ShowDialog() != true) return;
                row.ConnType = selector.ResultConnectorName;
                row.ConnectorCatalogKey = selector.ResultConnectorCatalogKey;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"D38999 커넥터 정보를 불러오지 못했습니다.\n\n{ex.Message}",
                    "커넥터 검색", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AddCustomKindButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CategoryNameWindow("장치") { Owner = this };
            if (dialog.ShowDialog() != true) return;
            string name = dialog.ResultName;
            if (KindCombo.Items.Cast<string>().Any(item =>
                    item.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
            {
                MessageBox.Show("같은 이름의 범주가 이미 있습니다.", "범주 추가",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _customKindNames.Add(name);
            KindCombo.Items.Add(name);
            KindCombo.SelectedItem = name;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (KindCombo.SelectedItem == null)
            {
                MessageBox.Show(this, "장치 범주를 먼저 추가해 주세요.", "장치 범주", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string name = NameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("장치 이름을 입력하세요.", "확인",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!CommitPortSlots()) { ShowPortSpaceWarning(); return; }
            var device = _editing ?? new Device();
            BuildDeviceFromInputs(device);
            device.Name = name;

            ResultDevice = device;
            ResultPurpose = _editInstancePurpose
                ? PurposeBox.Text.Trim()
                : "";
            string? selectedZoneId = _editInstancePurpose
                ? (ZoneCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                : null;
            ResultZoneId = string.IsNullOrWhiteSpace(selectedZoneId)
                ? null : selectedZoneId;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }

    public class PortEditRow : INotifyPropertyChanged
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        private string _designator = "";
        private string _side = "Left";
        private double _position = -1;
        private string _connType = "";
        private string _description = "";
        private string _connectorCatalogKey = "";
        private bool _showConnType;
        private bool _showLabel = true;
        private double _labelFontSize = 11;
        private string _labelColorHex = "#7F7F7F";

        public string Designator { get => _designator; set { _designator = value; OnChanged(nameof(Designator)); } }
        public string Side { get => _side; set { _side = value; OnChanged(nameof(Side)); } }
        public double Position { get => _position; set { _position = value; OnChanged(nameof(Position)); } }
        public string ConnType { get => _connType; set { _connType = value; OnChanged(nameof(ConnType)); } }
        public string ConnectorCatalogKey { get => _connectorCatalogKey; set { _connectorCatalogKey = value; OnChanged(nameof(ConnectorCatalogKey)); } }
        public string Description { get => _description; set { _description = value; OnChanged(nameof(Description)); } }
        public bool ShowConnType { get => _showConnType; set { _showConnType = value; OnChanged(nameof(ShowConnType)); } }
        public bool ShowLabel { get => _showLabel; set { _showLabel = value; OnChanged(nameof(ShowLabel)); } }
        public double LabelFontSize { get => _labelFontSize; set { _labelFontSize = value; OnChanged(nameof(LabelFontSize)); } }
        public string LabelColorHex { get => _labelColorHex; set { _labelColorHex = value; OnChanged(nameof(LabelColorHex)); } }
        public bool HasCustomLabelPosition { get; set; }
        public double LabelOffsetX { get; set; }
        public double LabelOffsetY { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
