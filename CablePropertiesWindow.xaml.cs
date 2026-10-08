using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ESDP.Models;

namespace ESDP
{
    public sealed class CablePortPropertyRow
    {
        public string ConnectorId { get; init; } = "";
        public string PortName { get; set; } = "";
        public string ConnType { get; set; } = "";
        public string ConnectorCatalogKey { get; set; } = "";
        public string Description { get; set; } = "";
        public string KeyAngle { get; set; } = "";
        public bool ShowConnType { get; set; }
        public bool ShowLabel { get; set; } = true;
        public double LabelFontSize { get; set; } = 9;
        public string LabelColorHex { get; set; } = "#000000";
        public string ConnectionStatus { get; init; } = "";
        public List<CablePin> Pins { get; set; } = new();
    }

    public partial class CablePropertiesWindow : Window
    {
        private readonly ObservableCollection<CablePortPropertyRow> _ports;
        private List<CableSignalRow> _signalRows;
        private bool _overallBraid;
        private bool _tube;

        public string ResultCableName { get; private set; } = "";
        public double ResultNameFontSize { get; private set; } = 13;
        public CableCategory ResultCategory { get; private set; }
        public string ResultCustomCategoryName { get; private set; } = "";
        public bool ResultShowCableName { get; private set; } = true;
        public string ResultCableLineColorHex { get; private set; } = "#000000";
        public IReadOnlyDictionary<string, string> ResultPortNames { get; private set; } =
            new Dictionary<string, string>();
        public IReadOnlyDictionary<string, CablePortPropertyRow> ResultPorts { get; private set; } =
            new Dictionary<string, CablePortPropertyRow>();
        public IReadOnlyList<CablePortPropertyRow> ResultPortRows { get; private set; } =
            Array.Empty<CablePortPropertyRow>();
        public List<CableSignalRow> ResultSignalRows { get; private set; } = new();
        public bool ResultOverallBraid { get; private set; } = true;
        public bool ResultTube { get; private set; } = true;
        private string _lineColorHex = "#000000";

        public CablePropertiesWindow(
            Cable cable,
            IEnumerable<CablePortPropertyRow> ports,
            bool showCableName,
            IEnumerable<string>? customCategoryNames = null)
        {
            InitializeComponent();

            CableNameBox.Text = cable.Name;
            CableNameFontSizeBox.Text = cable.NameFontSize.ToString("0.#");
            foreach (CableCategory category in Enum.GetValues(typeof(CableCategory)))
                CategoryCombo.Items.Add(category.ToString());
            foreach (string name in customCategoryNames ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(name)) CategoryCombo.Items.Add(name);
            CategoryCombo.SelectedItem = string.IsNullOrWhiteSpace(cable.CustomCategoryName)
                ? cable.Category.ToString() : cable.CustomCategoryName;
            ShowCableNameCheck.IsChecked = showCableName;
            _lineColorHex = ColorOption.NormalizeHex(cable.LineColorHex) ?? "#000000";
            UpdateCableColorControls();

            _ports = new ObservableCollection<CablePortPropertyRow>(ports);
            _overallBraid = cable.OverallBraid;
            _tube = cable.Tube;
            _signalRows = cable.SignalRows.Count > 0
                ? cable.SignalRows.Select(CloneSignalRow).ToList()
                : MigrateLegacyPins(_ports);
            PortsGrid.ItemsSource = _ports;
            Title = $"케이블 정보 - {cable.Name}";
        }

        private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => UpdateCableColorControls();

        private void UpdateCableColorControls()
        {
            if (CableColorButton == null || CableColorSwatch == null) return;
            bool custom = !Enum.TryParse(CategoryCombo.SelectedItem?.ToString(), out CableCategory selected) ||
                          selected == CableCategory.기타케이블;
            CableColorButton.IsEnabled = custom;
            CableColorSwatch.Fill = ColorOption.BrushFromHex(
                custom ? _lineColorHex :
                selected == CableCategory.점검케이블
                    ? "#0070C0"
                    : "#000000");
        }

        private void CableColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ColorPickerWindow.TryPick(
                    this, _lineColorHex, out string hex, "기타 케이블 선 색"))
                return;
            _lineColorHex = hex;
            UpdateCableColorControls();
        }

        private void PortLabelColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: CablePortPropertyRow row }) return;
            if (!ColorPickerWindow.TryPick(
                    this, row.LabelColorHex, out string hex, "케이블 포트 이름 글자색"))
                return;
            row.LabelColorHex = hex;
        }

        private void D38999SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: CablePortPropertyRow row }) return;
            try
            {
                var selector = new D38999ConnectorSelectorWindow(
                    row.ConnType, row.ConnectorCatalogKey) { Owner = this };
                if (selector.ShowDialog() != true) return;
                row.ConnType = selector.ResultConnectorName;
                row.ConnectorCatalogKey = selector.ResultConnectorCatalogKey;
                PortsGrid.Items.Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"D38999 커넥터 정보를 불러오지 못했습니다.\n\n{ex.Message}",
                    "커넥터 검색", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ConnectorTypeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox { IsKeyboardFocused: true, DataContext: CablePortPropertyRow row })
                row.ConnectorCatalogKey = "";
        }

        private void PinLayoutButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: CablePortPropertyRow row }) return;
            var pins = _signalRows.Select(signalRow =>
            {
                var pin = signalRow.PortPins.TryGetValue(row.ConnectorId, out var endpoint)
                    ? ClonePin(endpoint)
                    : new CablePin();
                pin.PinName = signalRow.PinName;
                return pin;
            }).ToList();
            var viewer = new ConnectorPinLayoutWindow(
                row.PortName, row.ConnType, row.ConnectorCatalogKey, pins)
            { Owner = this };
            viewer.ShowDialog();
        }

        private void AddPortButton_Click(object sender, RoutedEventArgs e)
        {
            var usedNames = _ports.Select(port => port.PortName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int number = 1;
            while (usedNames.Contains($"P{number}")) number++;

            _ports.Add(new CablePortPropertyRow
            {
                ConnectorId = Guid.NewGuid().ToString(),
                PortName = $"P{number}",
                ConnectionStatus = "새 포트 — 확인 시 기본 분기가 생성됩니다."
            });
            PortsGrid.ScrollIntoView(_ports[^1]);
        }

        private void CableSignalsButton_Click(object sender, RoutedEventArgs e)
        {
            PortsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            PortsGrid.CommitEdit(DataGridEditingUnit.Row, true);

            var editor = new CableSignalEditorWindow(
                CableNameBox.Text.Trim(), _ports, _signalRows, _overallBraid, _tube)
            {
                Owner = this
            };
            if (editor.ShowDialog() != true) return;

            _signalRows = editor.ResultSignalRows.Select(CloneSignalRow).ToList();
            _overallBraid = editor.ResultOverallBraid;
            _tube = editor.ResultTube;
            SynchronizeLegacyPins();
            PortsGrid.Items.Refresh();
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

        private static CableSignalRow CloneSignalRow(CableSignalRow source)
        {
            var row = new CableSignalRow
            {
                Id = source.Id,
                ParentSignalId = source.ParentSignalId,
                PinName = source.PinName,
                WireType = source.WireType
            };
            foreach (var pair in source.PortPins)
                row.PortPins[pair.Key] = ClonePin(pair.Value);
            return row;
        }

        private static List<CableSignalRow> MigrateLegacyPins(
            IEnumerable<CablePortPropertyRow> ports)
        {
            var list = ports.ToList();
            int count = list.Count == 0 ? 0 : list.Max(port => port.Pins.Count);
            var result = new List<CableSignalRow>();
            for (int index = 0; index < count; index++)
            {
                var row = new CableSignalRow
                {
                    PinName = list.Select(port => port.Pins.ElementAtOrDefault(index)?.PinName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? ""
                };
                foreach (var port in list)
                {
                    var pin = port.Pins.ElementAtOrDefault(index);
                    row.PortPins[port.ConnectorId] = pin == null ? new CablePin() : ClonePin(pin);
                }
                result.Add(row);
            }
            return result;
        }

        private void SynchronizeLegacyPins()
        {
            foreach (var port in _ports)
            {
                port.Pins = _signalRows.Select(row =>
                {
                    var pin = row.PortPins.TryGetValue(port.ConnectorId, out var endpoint)
                        ? ClonePin(endpoint)
                        : new CablePin();
                    pin.PinName = row.PinName;
                    return pin;
                }).ToList();
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(CableNameFontSizeBox.Text, out double nameSize) ||
                !double.IsFinite(nameSize) || nameSize < 6 || nameSize > 200)
            {
                MessageBox.Show(this, "이름 글자 크기는 6~200px 사이의 숫자로 입력해 주세요.", "글자 크기");
                CableNameFontSizeBox.Focus();
                return;
            }
            ResultNameFontSize = nameSize;
            PortsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            PortsGrid.CommitEdit(DataGridEditingUnit.Row, true);

            string cableName = CableNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(cableName))
            {
                MessageBox.Show("케이블 이름을 입력하세요.", "확인",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                CableNameBox.Focus();
                return;
            }

            foreach (var port in _ports)
            {
                port.PortName = port.PortName.Trim();
                port.ConnType = port.ConnType.Trim();
                port.Description = port.Description.Trim();
                port.KeyAngle = port.KeyAngle.Trim();
            }

            if (_ports.Any(port => string.IsNullOrWhiteSpace(port.PortName)))
            {
                MessageBox.Show("모든 포트의 이름을 입력하세요.", "확인",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var duplicate = _ports
                .GroupBy(port => port.PortName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
            {
                MessageBox.Show($"포트 이름 '{duplicate.Key}'이(가) 중복되었습니다.",
                                "확인", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultCableName = cableName;
            string selectedCategory = CategoryCombo.SelectedItem?.ToString() ?? CableCategory.기타케이블.ToString();
            if (Enum.TryParse(selectedCategory, out CableCategory category))
            {
                ResultCategory = category;
                ResultCustomCategoryName = "";
            }
            else
            {
                ResultCategory = CableCategory.기타케이블;
                ResultCustomCategoryName = selectedCategory;
            }
            ResultShowCableName = ShowCableNameCheck.IsChecked == true;
            ResultCableLineColorHex = _lineColorHex;
            ResultPortNames = _ports.ToDictionary(port => port.ConnectorId, port => port.PortName);
            ResultPorts = _ports.ToDictionary(port => port.ConnectorId);
            ResultPortRows = _ports.ToList();
            ResultSignalRows = _signalRows.Select(CloneSignalRow).ToList();
            foreach (var signal in ResultSignalRows)
            foreach (var port in _ports)
                if (!signal.PortPins.ContainsKey(port.ConnectorId))
                    signal.PortPins[port.ConnectorId] = new CablePin();
            ResultOverallBraid = _overallBraid;
            ResultTube = _tube;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
