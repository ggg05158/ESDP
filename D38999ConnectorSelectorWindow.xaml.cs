using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace ESDP
{
    public partial class D38999ConnectorSelectorWindow : Window
    {
        private sealed class GaugeOption
        {
            public int? Value { get; init; }
            public string Label { get; init; } = "";
        }

        private sealed class ResultRow
        {
            public required D38999SearchResult Result { get; init; }
            public string ArrangementId => Result.Arrangement.ArrangementId;
            public int ShellSize => Result.Arrangement.ShellSize;
            public int TotalContacts => Result.Arrangement.TotalContacts;
            public int PrimaryCompatibleContacts => Result.PrimaryCompatibleContacts;
            public int SpareContacts => Result.SpareContacts;
            public string ServiceRating => Result.Arrangement.ServiceRating;
            public string Category => Result.Arrangement.Category;
            public string CompatibleSummary => Result.CompatibleSummary;
        }

        private sealed class SeriesRow
        {
            public string Name { get; init; } = "";
            public bool IsBuiltIn { get; init; }
            public UserConnectorSeries? UserSeries { get; init; }
        }

        private sealed class CustomConnectorRow
        {
            public required UserConnectorItem Connector { get; init; }
            public string Name => Connector.Name;
            public int PinCount => Connector.PinCount;
            public string ImageStatus => string.IsNullOrWhiteSpace(Connector.ImageFileName)
                ? "없음" : "등록됨";
        }

        private readonly D38999CatalogService _catalog;
        private readonly ConnectorCatalogService _userCatalog;
        private readonly ObservableCollection<ResultRow> _results = new();
        private readonly ObservableCollection<SeriesRow> _seriesRows = new();
        private readonly ObservableCollection<CustomConnectorRow> _customConnectorRows = new();
        private readonly string _initialConnectorName;
        private readonly string _initialCatalogKey;
        private readonly D38999ParsedPartNumber? _parsedInitialPartNumber;
        private D38999SearchResult? _selectedResult;
        private UserConnectorItem? _selectedCustomConnector;
        private string _selectedCatalogKey = "";
        private bool _updatingPartNumberOptions;

        public string ResultConnectorName { get; private set; } = "";
        public string ResultArrangementId { get; private set; } = "";
        public string ResultConnectorCatalogKey { get; private set; } = "";

        public D38999ConnectorSelectorWindow(
            string? initialConnectorName = null, string? initialCatalogKey = null)
        {
            InitializeComponent();
            _initialConnectorName = initialConnectorName?.Trim() ?? "";
            _initialCatalogKey = initialCatalogKey?.Trim() ?? "";
            _catalog = D38999CatalogService.Instance;
            _userCatalog = ConnectorCatalogService.Instance;
            _catalog.TryParsePartNumber(_initialConnectorName, out var parsedInitialPartNumber);
            _parsedInitialPartNumber = parsedInitialPartNumber;

            var gaugeOptions = new List<GaugeOption>
            {
                new() { Value = null, Label = "선택 안 함" }
            };
            gaugeOptions.AddRange(_catalog.AvailableGauges.Select(value =>
                new GaugeOption { Value = value, Label = $"AWG {value}" }));
            foreach (var combo in new[] { Gauge1Combo, Gauge2Combo, Gauge3Combo })
                combo.ItemsSource = gaugeOptions;
            Gauge1Combo.SelectedItem = gaugeOptions.FirstOrDefault(item => item.Value == 22)
                ?? gaugeOptions[0];
            Gauge2Combo.SelectedIndex = 0;
            Gauge3Combo.SelectedIndex = 0;

            SetOptionalItems(ConnectorTypeCombo, _catalog.ConnectorTypes);
            ResultsGrid.ItemsSource = _results;
            SeriesList.ItemsSource = _seriesRows;
            CustomConnectorsGrid.ItemsSource = _customConnectorRows;
            RunSearch();
            RefreshSeriesList();
        }

        private SeriesRow? CurrentSeries => SeriesList.SelectedItem as SeriesRow;

        private void RefreshSeriesList(string? preferredSeriesId = null)
        {
            _userCatalog.Load();
            _seriesRows.Clear();
            _seriesRows.Add(new SeriesRow { Name = "D38999", IsBuiltIn = true });
            foreach (var series in _userCatalog.Catalog.Series
                         .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _seriesRows.Add(new SeriesRow
                {
                    Name = series.Name,
                    UserSeries = series
                });
            }

            SeriesRow? selected = null;
            if (!string.IsNullOrWhiteSpace(preferredSeriesId))
                selected = _seriesRows.FirstOrDefault(item =>
                    item.UserSeries?.Id == preferredSeriesId);
            if (selected == null && _initialCatalogKey.StartsWith(
                    "USER:", StringComparison.OrdinalIgnoreCase))
            {
                string[] tokens = _initialCatalogKey.Split(':');
                if (tokens.Length == 3)
                    selected = _seriesRows.FirstOrDefault(item => item.UserSeries?.Id == tokens[1]);
            }
            if (selected == null && !string.IsNullOrWhiteSpace(_initialConnectorName))
                selected = _seriesRows.FirstOrDefault(item => item.UserSeries?.Connectors.Any(
                    connector => connector.Name.Equals(_initialConnectorName,
                        StringComparison.OrdinalIgnoreCase)) == true);
            SeriesList.SelectedItem = selected ?? _seriesRows[0];
        }

        private void SeriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = CurrentSeries;
            bool builtIn = row?.IsBuiltIn == true;
            D38999Panel.Visibility = builtIn ? Visibility.Visible : Visibility.Collapsed;
            CustomSeriesPanel.Visibility = builtIn ? Visibility.Collapsed : Visibility.Visible;
            DeleteSeriesButton.IsEnabled = row?.UserSeries != null;
            D38999PartNumberPanel.Visibility = builtIn ? Visibility.Visible : Visibility.Collapsed;
            CustomConnectorInfoPanel.Visibility = builtIn ? Visibility.Collapsed : Visibility.Visible;

            if (builtIn)
            {
                if (ResultsGrid.SelectedItem is ResultRow resultRow) ShowDetails(resultRow.Result);
                else ClearDetails();
                return;
            }

            RefreshCustomConnectors(row?.UserSeries);
        }

        private void RefreshCustomConnectors(
            UserConnectorSeries? series, string? preferredConnectorId = null)
        {
            _customConnectorRows.Clear();
            CustomSeriesTitleText.Text = series?.Name ?? "사용자 시리즈";
            if (series != null)
            {
                foreach (var connector in series.Connectors
                             .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
                    _customConnectorRows.Add(new CustomConnectorRow { Connector = connector });
            }

            string? initialConnectorId = null;
            if (_initialCatalogKey.StartsWith("USER:", StringComparison.OrdinalIgnoreCase))
            {
                string[] tokens = _initialCatalogKey.Split(':');
                if (tokens.Length == 3) initialConnectorId = tokens[2];
            }
            CustomConnectorRow? selected = _customConnectorRows.FirstOrDefault(item =>
                item.Connector.Id == (preferredConnectorId ?? initialConnectorId));
            selected ??= _customConnectorRows.FirstOrDefault(item => item.Name.Equals(
                _initialConnectorName, StringComparison.OrdinalIgnoreCase));
            selected ??= _customConnectorRows.FirstOrDefault();
            CustomConnectorsGrid.SelectedItem = selected;
            if (selected == null) ClearCustomDetails();
        }

        private void AddSeriesButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ConnectorSeriesNameWindow { Owner = this };
            if (dialog.ShowDialog() != true) return;
            try
            {
                var series = _userCatalog.AddSeries(dialog.SeriesName);
                RefreshSeriesList(series.Id);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "커넥터 시리즈",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void DeleteSeriesButton_Click(object sender, RoutedEventArgs e)
        {
            var series = CurrentSeries?.UserSeries;
            if (series == null) return;
            if (MessageBox.Show($"'{series.Name}' 시리즈와 등록된 커넥터를 모두 삭제할까요?",
                    "커넥터 시리즈 삭제", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { _userCatalog.DeleteSeries(series); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "커넥터 시리즈 삭제 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            RefreshSeriesList();
        }

        private void AddConnectorButton_Click(object sender, RoutedEventArgs e)
        {
            var series = CurrentSeries?.UserSeries;
            if (series == null) return;
            var dialog = new CustomConnectorEditorWindow(series.Name) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            try
            {
                var connector = _userCatalog.AddConnector(series, dialog.ConnectorName,
                    dialog.PinCount, dialog.ImagePath);
                RefreshCustomConnectors(series, connector.Id);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "커넥터 추가",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void DeleteConnectorButton_Click(object sender, RoutedEventArgs e)
        {
            var series = CurrentSeries?.UserSeries;
            if (series == null || CustomConnectorsGrid.SelectedItem is not CustomConnectorRow row)
                return;
            if (MessageBox.Show($"'{row.Name}' 커넥터를 삭제할까요?", "커넥터 삭제",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            try { _userCatalog.DeleteConnector(series, row.Connector); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "커넥터 삭제 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            RefreshSeriesList(series.Id);
        }

        private void CustomConnectorsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DeleteConnectorButton.IsEnabled = CustomConnectorsGrid.SelectedItem is CustomConnectorRow;
            if (CustomConnectorsGrid.SelectedItem is CustomConnectorRow row)
                ShowCustomDetails(row.Connector);
            else
                ClearCustomDetails();
        }

        private void CustomConnectorsGrid_MouseDoubleClick(
            object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (CustomConnectorsGrid.SelectedItem is CustomConnectorRow) ApplySelection();
        }

        private void ShowCustomDetails(UserConnectorItem connector)
        {
            _selectedCustomConnector = connector;
            _selectedCatalogKey = CurrentSeries?.UserSeries is { } series
                ? ConnectorCatalogService.CustomCatalogKey(series, connector)
                : "";
            ArrangementTitleText.Text = connector.Name;
            ArrangementMetaText.Text = $"시리즈: {CurrentSeries?.Name}";
            ContactBreakdownText.Text = $"핀 수: {connector.PinCount}";
            CompatibilityText.Text = "사용자가 등록한 커넥터입니다.";
            CustomConnectorSeriesText.Text = $"커넥터 시리즈: {CurrentSeries?.Name}";
            CustomConnectorPinCountText.Text = $"핀 수: {connector.PinCount}";
            SetPreviewImage(_userCatalog.ResolveImagePath(connector));
            ApplyButton.IsEnabled = true;
            PinPositionButton.IsEnabled = InsertImage.Source != null;
        }

        private void ClearCustomDetails()
        {
            _selectedCustomConnector = null;
            _selectedCatalogKey = "";
            ArrangementTitleText.Text = "등록된 커넥터가 없습니다.";
            ArrangementMetaText.Text = "+ 커넥터 추가 버튼으로 이름, 핀 수, 핀 배치 사진을 등록하세요.";
            ContactBreakdownText.Text = "";
            CompatibilityText.Text = "";
            CustomConnectorSeriesText.Text = "";
            CustomConnectorPinCountText.Text = "";
            SetPreviewImage(null);
            ApplyButton.IsEnabled = false;
            PinPositionButton.IsEnabled = false;
        }

        private void SetPreviewImage(string? imagePath)
        {
            InsertImage.Source = null;
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(imagePath, UriKind.Absolute);
                    image.EndInit();
                    image.Freeze();
                    InsertImage.Source = image;
                }
                catch
                {
                    InsertImage.Source = null;
                }
            }
            bool hasImage = InsertImage.Source != null;
            InsertImage.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
            NoImageText.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
        }

        private static void SetOptionalItems(ComboBox combo, IEnumerable<string> values)
        {
            combo.Items.Add("선택 안 함");
            foreach (string value in values) combo.Items.Add(value);
            combo.SelectedIndex = 0;
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e) => RunSearch();

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            Gauge1Combo.SelectedItem = Gauge1Combo.Items.Cast<GaugeOption>()
                .FirstOrDefault(item => item.Value == 22) ?? Gauge1Combo.Items[0];
            Gauge2Combo.SelectedIndex = 0;
            Gauge3Combo.SelectedIndex = 0;
            Gauge1MinBox.Clear();
            Gauge1MaxBox.Clear();
            ConnectorTypeCombo.SelectedIndex = 0;
            RunSearch();
        }

        private void RunSearch()
        {
            if (!TryBuildCriteria(out var criteria)) return;
            var found = _catalog.Search(criteria);
            _results.Clear();
            foreach (var result in found) _results.Add(new ResultRow { Result = result });
            ResultCountText.Text = $"{_results.Count}개";

            ResultRow? select = null;
            string? initialArrangement = _parsedInitialPartNumber?.Configuration.ArrangementId
                ?? D38999CatalogService.ExtractArrangementId(_initialConnectorName);
            if (!string.IsNullOrWhiteSpace(initialArrangement))
                select = _results.FirstOrDefault(item => item.ArrangementId.Equals(
                    initialArrangement, StringComparison.OrdinalIgnoreCase));
            select ??= _results.FirstOrDefault();
            ResultsGrid.SelectedItem = select;
            if (select != null) ResultsGrid.ScrollIntoView(select);
            else ClearDetails();
        }

        private bool TryBuildCriteria(out D38999SearchCriteria criteria)
        {
            bool hasMinimum = !string.IsNullOrWhiteSpace(Gauge1MinBox.Text);
            bool hasMaximum = !string.IsNullOrWhiteSpace(Gauge1MaxBox.Text);
            int? minimum = ParseCircuitCount(Gauge1MinBox, "최소 회선 수");
            if (hasMinimum && !minimum.HasValue)
            {
                criteria = new D38999SearchCriteria();
                return false;
            }
            int? maximum = ParseCircuitCount(Gauge1MaxBox, "최대 회선 수");
            if (hasMaximum && !maximum.HasValue)
            {
                criteria = new D38999SearchCriteria();
                return false;
            }
            if (minimum.HasValue && maximum.HasValue && minimum > maximum)
            {
                MessageBox.Show("최소 회선 수는 최대 회선 수보다 클 수 없습니다.",
                    "검색 조건", MessageBoxButton.OK, MessageBoxImage.Information);
                Gauge1MinBox.Focus();
                criteria = new D38999SearchCriteria();
                return false;
            }

            criteria = new D38999SearchCriteria
            {
                ConnectorType = OptionalValue(ConnectorTypeCombo),
                MinimumCircuits = minimum,
                MaximumCircuits = maximum
            };
            foreach (var combo in new[] { Gauge1Combo, Gauge2Combo, Gauge3Combo })
            {
                int? gauge = (combo.SelectedItem as GaugeOption)?.Value;
                if (gauge.HasValue && !criteria.AcceptableGauges.Contains(gauge.Value))
                    criteria.AcceptableGauges.Add(gauge.Value);
            }
            if ((minimum.HasValue || maximum.HasValue) && criteria.AcceptableGauges.Count == 0)
            {
                MessageBox.Show("회선 수 범위를 사용하려면 게이지를 하나 이상 선택하세요.",
                    "검색 조건", MessageBoxButton.OK, MessageBoxImage.Information);
                Gauge1Combo.Focus();
                return false;
            }
            return true;
        }

        private static int? ParseCircuitCount(TextBox box, string label)
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return null;
            if (int.TryParse(box.Text.Trim(), out int value) && value >= 0) return value;
            MessageBox.Show($"{label}에는 0 이상의 정수를 입력하세요.",
                "검색 조건", MessageBoxButton.OK, MessageBoxImage.Information);
            box.Focus();
            return null;
        }

        private static string OptionalValue(ComboBox combo)
            => combo.SelectedIndex <= 0 ? "" : combo.SelectedItem?.ToString() ?? "";

        private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultsGrid.SelectedItem is ResultRow row) ShowDetails(row.Result);
            else ClearDetails();
        }

        private void ResultsGrid_MouseDoubleClick(
            object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ResultsGrid.SelectedItem is ResultRow) ApplySelection();
        }

        private void ShowDetails(D38999SearchResult result)
        {
            _selectedResult = result;
            var item = result.Arrangement;
            _selectedCatalogKey = $"D38999:{item.ArrangementId}";
            ArrangementTitleText.Text = $"D38999 Insert {item.ArrangementId}";
            ArrangementMetaText.Text =
                $"셸 {item.ShellSize} · 전체 {item.TotalContacts}접점" +
                (string.IsNullOrWhiteSpace(item.SpecialContactNote)
                    ? ""
                    : $" · {item.SpecialContactNote}");
            ContactBreakdownText.Text = string.Join("  ·  ", item.ContactCounts
                .Where(pair => pair.Value > 0)
                .Select(pair => $"Size {pair.Key}: {pair.Value}"));
            CompatibilityText.Text = result.CompatibleSummary;
            InsertImage.Source = _catalog.LoadInsertImage(item);
            bool hasImage = InsertImage.Source != null;
            NoImageText.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
            InsertImage.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;

            PopulatePartNumberOptions(result);
            ApplyButton.IsEnabled = true;
            PinPositionButton.IsEnabled = InsertImage.Source != null;
        }

        private void PopulatePartNumberOptions(D38999SearchResult result)
        {
            _updatingPartNumberOptions = true;
            try
            {
                var types = result.Configurations.Select(item => item.ConnectorType)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                FinalConnectorTypeCombo.ItemsSource = types;

                string? preferredType = null;
                if (_parsedInitialPartNumber?.Configuration.ArrangementId.Equals(
                        result.Arrangement.ArrangementId,
                        StringComparison.OrdinalIgnoreCase) == true)
                    preferredType = _parsedInitialPartNumber.Configuration.ConnectorType;
                if (string.IsNullOrWhiteSpace(preferredType))
                    preferredType = OptionalValue(ConnectorTypeCombo);
                if (string.IsNullOrWhiteSpace(preferredType))
                    preferredType = types.FirstOrDefault(value => value.Equals(
                        "Straight plug", StringComparison.OrdinalIgnoreCase));
                FinalConnectorTypeCombo.SelectedItem = types.FirstOrDefault(value =>
                    value.Equals(preferredType, StringComparison.OrdinalIgnoreCase))
                    ?? types.FirstOrDefault();
                PopulateCodeOptions();
            }
            finally
            {
                _updatingPartNumberOptions = false;
            }
            UpdateConnectorName();
        }

        private void PopulateCodeOptions()
        {
            var configuration = CurrentConfiguration();
            if (configuration == null)
            {
                ClassCombo.ItemsSource = null;
                ContactStyleCombo.ItemsSource = null;
                KeyingCombo.ItemsSource = null;
                PatternBox.Text = "";
                return;
            }

            PatternBox.Text = configuration.PartNumberPattern;
            bool matchesInitial = _parsedInitialPartNumber?.Configuration.PartNumberPattern.Equals(
                configuration.PartNumberPattern, StringComparison.OrdinalIgnoreCase) == true;
            SetCodeOptions(ClassCombo, configuration.AllowedClasses,
                matchesInitial ? _parsedInitialPartNumber!.ClassCode : "W", "W");
            SetCodeOptions(ContactStyleCombo, configuration.AllowedContactStyles,
                matchesInitial ? _parsedInitialPartNumber!.ContactStyleCode : "P", "P");
            SetCodeOptions(KeyingCombo, configuration.AllowedKeying,
                matchesInitial ? _parsedInitialPartNumber!.KeyingCode : "N", "N");
        }

        private static void SetCodeOptions(
            ComboBox combo, string encodedOptions, string? preferred, string fallback)
        {
            var values = D38999CatalogService.SplitCodes(encodedOptions);
            combo.ItemsSource = values;
            combo.SelectedItem = values.FirstOrDefault(value => value.Equals(
                    preferred, StringComparison.OrdinalIgnoreCase))
                ?? values.FirstOrDefault(value => value.Equals(
                    fallback, StringComparison.OrdinalIgnoreCase))
                ?? values.FirstOrDefault();
        }

        private D38999Configuration? CurrentConfiguration()
        {
            string type = FinalConnectorTypeCombo.SelectedItem?.ToString() ?? "";
            return _selectedResult?.Configurations.FirstOrDefault(item =>
                item.ConnectorType.Equals(type, StringComparison.OrdinalIgnoreCase))
                ?? _selectedResult?.Configurations.FirstOrDefault();
        }

        private void FinalConnectorTypeCombo_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            if (_updatingPartNumberOptions) return;
            _updatingPartNumberOptions = true;
            try
            {
                PopulateCodeOptions();
            }
            finally
            {
                _updatingPartNumberOptions = false;
            }
            UpdateConnectorName();
        }

        private void PartCodeCombo_SelectionChanged(
            object sender, SelectionChangedEventArgs e)
        {
            if (!_updatingPartNumberOptions) UpdateConnectorName();
        }

        private void UpdateConnectorName()
        {
            var configuration = CurrentConfiguration();
            if (configuration == null) return;
            string generated = D38999CatalogService.BuildPartNumber(
                configuration,
                ClassCombo.SelectedItem?.ToString() ?? "",
                ContactStyleCombo.SelectedItem?.ToString() ?? "",
                KeyingCombo.SelectedItem?.ToString() ?? "");
            ConnectorNameBox.Text = generated;
        }

        private static string BlankAsDash(string value)
            => string.IsNullOrWhiteSpace(value) ? "-" : value;

        private void ClearDetails()
        {
            ArrangementTitleText.Text = "검색 결과가 없습니다.";
            ArrangementMetaText.Text = "조건을 완화하거나 회선 수 범위를 다시 확인하세요.";
            ContactBreakdownText.Text = "";
            CompatibilityText.Text = "";
            PatternBox.Text = "";
            ConnectorNameBox.Text = "";
            FinalConnectorTypeCombo.ItemsSource = null;
            ClassCombo.ItemsSource = null;
            ContactStyleCombo.ItemsSource = null;
            KeyingCombo.ItemsSource = null;
            _selectedResult = null;
            _selectedCatalogKey = "";
            InsertImage.Source = null;
            InsertImage.Visibility = Visibility.Collapsed;
            NoImageText.Visibility = Visibility.Visible;
            ApplyButton.IsEnabled = false;
            PinPositionButton.IsEnabled = false;
        }

        private void PinPositionButton_Click(object sender, RoutedEventArgs e)
        {
            if (InsertImage.Source == null || string.IsNullOrWhiteSpace(_selectedCatalogKey)) return;
            string connectorName = CurrentSeries?.UserSeries != null
                ? _selectedCustomConnector?.Name ?? "커넥터"
                : ConnectorNameBox.Text.Trim();
            var editor = new ConnectorPinPositionEditorWindow(
                _selectedCatalogKey, connectorName, InsertImage.Source,
                _userCatalog.GetPinPositions(_selectedCatalogKey))
            { Owner = this };
            editor.ShowDialog();
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e) => ApplySelection();

        private void ApplySelection()
        {
            if (CurrentSeries?.UserSeries != null)
            {
                if (_selectedCustomConnector == null) return;
                ResultConnectorName = _selectedCustomConnector.Name;
                ResultArrangementId = "";
                ResultConnectorCatalogKey = _selectedCatalogKey;
                DialogResult = true;
                return;
            }
            if (ResultsGrid.SelectedItem is not ResultRow row) return;
            string connectorName = ConnectorNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(connectorName))
                connectorName = D38999CatalogService.SuggestedConnectorName(row.Result);
            ResultConnectorName = connectorName;
            ResultArrangementId = row.ArrangementId;
            ResultConnectorCatalogKey = _selectedCatalogKey;
            DialogResult = true;
        }
    }
}
