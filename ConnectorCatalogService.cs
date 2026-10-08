using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ESDP
{
    public sealed class UserConnectorCatalog
    {
        public List<UserConnectorSeries> Series { get; set; } = new();
        public List<ConnectorPinLayout> PinLayouts { get; set; } = new();
    }

    public sealed class UserConnectorSeries
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public List<UserConnectorItem> Connectors { get; set; } = new();
    }

    public sealed class UserConnectorItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public int PinCount { get; set; }
        public string ImageFileName { get; set; } = "";
    }

    public sealed class ConnectorPinLayout
    {
        public string CatalogKey { get; set; } = "";
        public List<ConnectorPinPosition> Positions { get; set; } = new();
        /// <summary>핀 표시 원의 지름(pt). 0이면 이미지 인식값을 사용한다.</summary>
        public double MarkerDiameterPoints { get; set; }
    }

    public sealed class ConnectorPinPosition
    {
        public string PinNumber { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public string DisplayText => $"{PinNumber}  ({X:P0}, {Y:P0})";
    }

    public sealed class ConnectorPinVisualInfo
    {
        public string CatalogKey { get; init; } = "";
        public string ConnectorName { get; init; } = "";
        public ImageSource? Image { get; init; }
    }

    public sealed partial class ConnectorCatalogService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly string _catalogDirectory;
        private readonly string _catalogFilePath;
        private readonly string _imageDirectory;

        public static ConnectorCatalogService Instance { get; } = new();
        public UserConnectorCatalog Catalog { get; private set; } = new();

        private ConnectorCatalogService() : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ESDP", "ConnectorCatalog")) { }

        internal ConnectorCatalogService(string catalogDirectory)
        {
            _catalogDirectory = Path.GetFullPath(catalogDirectory);
            _catalogFilePath = Path.Combine(_catalogDirectory, "catalog.json");
            _imageDirectory = Path.Combine(_catalogDirectory, "images");
            Load();
        }

        public void Load()
        {
            LastLoadWarning = null;
            IsReadOnly = false;
            _recoveredFromBackup = false;
            try
            {
                Directory.CreateDirectory(_imageDirectory);
                if (!File.Exists(_catalogFilePath) && !File.Exists(_catalogFilePath + ".bak"))
                {
                    Catalog = new UserConnectorCatalog();
                    _persistedJson = JsonSerializer.Serialize(Catalog, JsonOptions);
                    return;
                }

                Catalog = ReadCatalog(_catalogFilePath);
            }
            catch (Exception ex)
            {
                try
                {
                    Catalog = ReadCatalog(_catalogFilePath + ".bak");
                    _recoveredFromBackup = true;
                    LastLoadWarning = "커넥터 카탈로그 원본을 읽지 못해 백업본을 사용합니다. 원본은 다음 저장 시 별도로 보존됩니다.\n" + ex.Message;
                }
                catch
                {
                    IsReadOnly = true;
                    LastLoadWarning = "커넥터 카탈로그와 백업본을 읽지 못했습니다. 기존 파일 보호를 위해 변경을 차단했습니다.\n" + _catalogFilePath + "\n" + ex.Message;
                }
            }
            _persistedJson = JsonSerializer.Serialize(Catalog, JsonOptions);
        }

        public UserConnectorSeries AddSeries(string name)
        {
            EnsureWritable();
            string normalized = name.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                throw new ArgumentException("시리즈 이름을 입력하세요.", nameof(name));
            if (normalized.Equals("D38999", StringComparison.OrdinalIgnoreCase) ||
                Catalog.Series.Any(item => item.Name.Equals(
                    normalized, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("같은 이름의 커넥터 시리즈가 이미 있습니다.");

            var series = new UserConnectorSeries { Name = normalized };
            Catalog.Series.Add(series);
            Save();
            return series;
        }

        public void DeleteSeries(UserConnectorSeries series)
        {
            EnsureWritable();
            if (!Catalog.Series.Remove(series)) return;
            foreach (var connector in series.Connectors)
            {
                RemovePinLayout(CustomCatalogKey(series, connector));
            }
            Save();
        }

        public UserConnectorItem AddConnector(
            UserConnectorSeries series, string name, int pinCount, string? sourceImagePath)
        {
            EnsureWritable();
            string normalized = name.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                throw new ArgumentException("커넥터 이름을 입력하세요.", nameof(name));
            if (pinCount < 0)
                throw new ArgumentOutOfRangeException(nameof(pinCount));
            if (series.Connectors.Any(item => item.Name.Equals(
                    normalized, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("이 시리즈에 같은 이름의 커넥터가 이미 있습니다.");

            var connector = new UserConnectorItem
            {
                Name = normalized,
                PinCount = pinCount,
                ImageFileName = ImportImage(sourceImagePath)
            };
            series.Connectors.Add(connector);
            Save();
            return connector;
        }

        public void DeleteConnector(UserConnectorSeries series, UserConnectorItem connector)
        {
            EnsureWritable();
            if (!series.Connectors.Remove(connector)) return;
            RemovePinLayout(CustomCatalogKey(series, connector));
            Save();
        }

        private void RemovePinLayout(string catalogKey)
            => Catalog.PinLayouts.RemoveAll(item => item.CatalogKey.Equals(
                catalogKey, StringComparison.OrdinalIgnoreCase));

        public string? ResolveImagePath(UserConnectorItem connector)
        {
            if (string.IsNullOrWhiteSpace(connector.ImageFileName)) return null;
            ValidateImageFileName(connector.ImageFileName);
            string path = Path.Combine(_imageDirectory, connector.ImageFileName);
            return File.Exists(path) ? path : null;
        }

        public static string CustomCatalogKey(
            UserConnectorSeries series, UserConnectorItem connector)
            => $"USER:{series.Id}:{connector.Id}";

        public IReadOnlyList<ConnectorPinPosition> GetPinPositions(string catalogKey)
        {
            var layout = Catalog.PinLayouts.FirstOrDefault(item => item.CatalogKey.Equals(
                catalogKey, StringComparison.OrdinalIgnoreCase));
            return layout?.Positions.Select(ClonePosition).ToList()
                   ?? new List<ConnectorPinPosition>();
        }

        public double GetPinMarkerDiameterPoints(string catalogKey)
            => Catalog.PinLayouts.FirstOrDefault(item => item.CatalogKey.Equals(
                   catalogKey, StringComparison.OrdinalIgnoreCase))?.MarkerDiameterPoints ?? 0;

        public void SavePinPositions(
            string catalogKey, IEnumerable<ConnectorPinPosition> positions,
            double markerDiameterPoints = 0)
        {
            EnsureWritable();
            if (string.IsNullOrWhiteSpace(catalogKey)) return;
            var layout = Catalog.PinLayouts.FirstOrDefault(item => item.CatalogKey.Equals(
                catalogKey, StringComparison.OrdinalIgnoreCase));
            if (layout == null)
            {
                layout = new ConnectorPinLayout { CatalogKey = catalogKey };
                Catalog.PinLayouts.Add(layout);
            }
            layout.Positions = positions
                .Where(item => !string.IsNullOrWhiteSpace(item.PinNumber))
                .Select(ClonePosition)
                .ToList();
            if (markerDiameterPoints > 0)
                layout.MarkerDiameterPoints = markerDiameterPoints;
            Save();
        }

        public ConnectorPinVisualInfo? ResolveVisual(
            string? connectorName, string? catalogKey = null)
        {
            string name = connectorName?.Trim() ?? "";
            string key = catalogKey?.Trim() ?? "";

            if (key.StartsWith("D38999:", StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrWhiteSpace(key) &&
                 D38999CatalogService.ExtractArrangementId(name) is not null))
            {
                string? arrangementId = key.StartsWith("D38999:", StringComparison.OrdinalIgnoreCase)
                    ? key[7..]
                    : D38999CatalogService.ExtractArrangementId(name);
                var arrangement = D38999CatalogService.Instance.Arrangements.FirstOrDefault(item =>
                    item.ArrangementId.Equals(arrangementId, StringComparison.OrdinalIgnoreCase));
                if (arrangement != null)
                {
                    return new ConnectorPinVisualInfo
                    {
                        CatalogKey = $"D38999:{arrangement.ArrangementId}",
                        ConnectorName = name,
                        Image = D38999CatalogService.Instance.LoadInsertImage(arrangement)
                    };
                }
            }

            UserConnectorSeries? foundSeries = null;
            UserConnectorItem? foundConnector = null;
            if (key.StartsWith("USER:", StringComparison.OrdinalIgnoreCase))
            {
                string[] tokens = key.Split(':');
                if (tokens.Length == 3)
                {
                    foundSeries = Catalog.Series.FirstOrDefault(item => item.Id == tokens[1]);
                    foundConnector = foundSeries?.Connectors.FirstOrDefault(item => item.Id == tokens[2]);
                }
            }
            if (foundConnector == null && !string.IsNullOrWhiteSpace(name))
            {
                foreach (var series in Catalog.Series)
                {
                    var connector = series.Connectors.FirstOrDefault(item => item.Name.Equals(
                        name, StringComparison.OrdinalIgnoreCase));
                    if (connector == null) continue;
                    foundSeries = series;
                    foundConnector = connector;
                    break;
                }
            }
            if (foundSeries == null || foundConnector == null) return null;

            return new ConnectorPinVisualInfo
            {
                CatalogKey = CustomCatalogKey(foundSeries, foundConnector),
                ConnectorName = foundConnector.Name,
                Image = LoadFileImage(ResolveImagePath(foundConnector))
            };
        }

        private static ImageSource? LoadFileImage(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        private static ConnectorPinPosition ClonePosition(ConnectorPinPosition source)
            => new() { PinNumber = source.PinNumber, X = source.X, Y = source.Y };

        private string ImportImage(string? sourceImagePath)
        {
            if (string.IsNullOrWhiteSpace(sourceImagePath) || !File.Exists(sourceImagePath))
                return "";

            Directory.CreateDirectory(_imageDirectory);
            string extension = Path.GetExtension(sourceImagePath).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif"))
                extension = ".png";
            string fileName = $"{Guid.NewGuid():N}{extension}";
            File.Copy(sourceImagePath, Path.Combine(_imageDirectory, fileName), false);
            return fileName;
        }

    }
}
