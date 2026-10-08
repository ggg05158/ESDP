using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ESDP
{
    public sealed partial class ConnectorCatalogService
    {
        private const long MaxLibraryBytes = 256L * 1024 * 1024;
        private const int MaxCatalogBytes = 16 * 1024 * 1024;
        private string _persistedJson = "";
        private bool _recoveredFromBackup;
        public bool IsReadOnly { get; private set; }
        public string? LastLoadWarning { get; private set; }

        private void EnsureWritable()
        {
            if (IsReadOnly) throw new InvalidOperationException(LastLoadWarning ?? "카탈로그 변경이 차단되었습니다.");
        }

        private static void ValidateImageFileName(string name)
        {
            if (name.Length == 0) return;
            if (name.Length > 200 || name != Path.GetFileName(name) || name is "." or ".." ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Path.GetExtension(name).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif"))
                throw new InvalidDataException("카탈로그 이미지 파일 이름이 올바르지 않습니다.");
        }

        private static UserConnectorCatalog ParseCatalog(string json)
        {
            var catalog = JsonSerializer.Deserialize<UserConnectorCatalog>(json, JsonOptions)
                ?? throw new InvalidDataException("카탈로그 데이터가 없습니다.");
            ValidateCatalog(catalog);
            return catalog;
        }

        private static UserConnectorCatalog ReadCatalog(string path)
        {
            if (new FileInfo(path).Length > MaxCatalogBytes) throw new InvalidDataException("카탈로그가 너무 큽니다.");
            return ParseCatalog(File.ReadAllText(path));
        }

        private static void ValidateCatalog(UserConnectorCatalog catalog)
        {
            catalog.Series ??= new(); catalog.PinLayouts ??= new();
            if (catalog.Series.Count > 10000 || catalog.PinLayouts.Count > 100000) throw new InvalidDataException("카탈로그 항목이 너무 많습니다.");
            static void CheckId(string? id)
            {
                if (string.IsNullOrWhiteSpace(id) || id.Contains(':')) throw new InvalidDataException("커넥터 ID가 올바르지 않습니다.");
            }
            var seriesIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var series in catalog.Series)
            {
                if (series == null) throw new InvalidDataException("빈 시리즈 항목입니다.");
                CheckId(series.Id);
                if (!seriesIds.Add(series.Id) || string.IsNullOrWhiteSpace(series.Name)) throw new InvalidDataException("시리즈 ID 또는 이름이 올바르지 않습니다.");
                series.Connectors ??= new();
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var connector in series.Connectors)
                {
                    if (connector == null) throw new InvalidDataException("빈 커넥터 항목입니다.");
                    CheckId(connector.Id);
                    if (!ids.Add(connector.Id) || string.IsNullOrWhiteSpace(connector.Name) || connector.PinCount < 0)
                        throw new InvalidDataException("커넥터 정보가 올바르지 않습니다.");
                    connector.ImageFileName ??= "";
                    ValidateImageFileName(connector.ImageFileName);
                }
            }
            var layoutKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var layout in catalog.PinLayouts)
            {
                if (layout == null || string.IsNullOrWhiteSpace(layout.CatalogKey) || !layoutKeys.Add(layout.CatalogKey) ||
                    !double.IsFinite(layout.MarkerDiameterPoints) || layout.MarkerDiameterPoints < 0)
                    throw new InvalidDataException("핀 배열 정보가 올바르지 않습니다.");
                layout.Positions ??= new();
                foreach (var p in layout.Positions)
                    if (p == null || string.IsNullOrWhiteSpace(p.PinNumber) || !double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
                        p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1)
                        throw new InvalidDataException("핀 위치는 이미지 안의 좌표여야 합니다.");
            }
        }

        private void Save()
        {
            EnsureWritable();
            string temp = _catalogFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                ValidateCatalog(Catalog);
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Catalog, JsonOptions));
                if (bytes.Length > MaxCatalogBytes) throw new InvalidDataException("카탈로그가 너무 큽니다.");
                Directory.CreateDirectory(_imageDirectory);
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes); file.Flush(true); }
                ReadCatalog(temp);
                if (File.Exists(_catalogFilePath))
                {
                    if (_recoveredFromBackup)
                        File.Copy(_catalogFilePath, _catalogFilePath + ".corrupt." + DateTime.Now.ToString("yyyyMMddHHmmss") + "." + Guid.NewGuid().ToString("N"), false);
                    File.Replace(temp, _catalogFilePath, _recoveredFromBackup ? null : _catalogFilePath + ".bak");
                }
                else File.Move(temp, _catalogFilePath);
                _persistedJson = Encoding.UTF8.GetString(bytes);
                _recoveredFromBackup = false;
            }
            catch
            {
                if (_persistedJson.Length > 0) Catalog = ParseCatalog(_persistedJson);
                throw;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        // Removed connectors retain their stored images so the previous .bak can recover them.
        public void ExportLibrary(string path)
        {
            if (IsReadOnly) throw new InvalidOperationException("손상된 카탈로그 대신 정상 백업을 복구한 뒤 내보내 주세요.");
            ValidateCatalog(Catalog);
            var images = Catalog.Series.SelectMany(s => s.Connectors).Select(c => c.ImageFileName)
                .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string temp = Path.GetFullPath(path) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                {
                    using (var writer = new StreamWriter(zip.CreateEntry("catalog.json").Open(), new UTF8Encoding(false)))
                        writer.Write(JsonSerializer.Serialize(Catalog, JsonOptions));
                    long size = 0;
                    foreach (var name in images)
                    {
                        string image = Path.Combine(_imageDirectory, name);
                        var info = new FileInfo(image);
                        if (!info.Exists) throw new FileNotFoundException("공유할 커넥터 이미지가 없습니다.", image);
                        if ((size += info.Length) > MaxLibraryBytes || info.Length > 32L * 1024 * 1024)
                            throw new InvalidDataException("커넥터 이미지 용량이 공유 한도를 넘습니다.");
                        zip.CreateEntryFromFile(image, "images/" + name, CompressionLevel.Optimal);
                    }
                }
                File.Move(temp, Path.GetFullPath(path), true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public void ImportLibrary(string path)
        {
            EnsureWritable();
            var addedImages = new List<string>();
            var original = Catalog;
            try
            {
                using var zip = ZipFile.OpenRead(path);
                if (zip.Entries.Count > 20000) throw new InvalidDataException("공유 파일 항목이 너무 많습니다.");
                long size = 0;
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    if (!entries.TryAdd(entry.FullName, entry) || (size += entry.Length) > MaxLibraryBytes || entry.Length > 32L * 1024 * 1024)
                        throw new InvalidDataException("공유 파일 용량이나 중복 항목이 올바르지 않습니다.");
                    if (entry.FullName != "catalog.json")
                    {
                        if (!entry.FullName.StartsWith("images/", StringComparison.Ordinal)) throw new InvalidDataException("허용되지 않은 공유 파일 경로입니다.");
                        ValidateImageFileName(entry.FullName[7..]);
                        if (entry.FullName.Length == 7) throw new InvalidDataException("빈 이미지 경로입니다.");
                    }
                }
                if (!entries.TryGetValue("catalog.json", out var catalogEntry) || catalogEntry.Length > MaxCatalogBytes)
                    throw new InvalidDataException("공유 카탈로그가 없습니다.");
                // Bound actual decompressed bytes as well as ZIP metadata.
                using var jsonBytes = new MemoryStream();
                using (var catalogStream = catalogEntry.Open()) CopyBounded(catalogStream, jsonBytes, MaxCatalogBytes);
                var incoming = ParseCatalog(Encoding.UTF8.GetString(jsonBytes.ToArray()));
                var merged = ParseCatalog(JsonSerializer.Serialize(Catalog, JsonOptions));
                Directory.CreateDirectory(_imageDirectory);
                var imageMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var connector in incoming.Series.SelectMany(s => s.Connectors))
                {
                    string name = connector.ImageFileName;
                    if (name.Length == 0) continue;
                    if (!imageMap.TryGetValue(name, out var newName))
                    {
                        if (!entries.TryGetValue("images/" + name, out var entry)) throw new InvalidDataException("공유 파일의 이미지가 누락되었습니다: " + name);
                        newName = Guid.NewGuid().ToString("N") + Path.GetExtension(name);
                        string destination = Path.Combine(_imageDirectory, newName);
                        addedImages.Add(destination);
                        using var source = entry.Open();
                        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        CopyBounded(source, output, 32L * 1024 * 1024);
                        if (output.Length != entry.Length) throw new InvalidDataException("이미지 길이가 올바르지 않습니다.");
                        output.Flush(true);
                        imageMap[name] = newName;
                    }
                    connector.ImageFileName = newName;
                }
                foreach (var series in incoming.Series)
                {
                    var existing = merged.Series.FirstOrDefault(s => s.Id.Equals(series.Id, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                    {
                        string baseName = series.Name;
                        int suffix = 1;
                        while (merged.Series.Any(s => s.Name.Equals(series.Name, StringComparison.OrdinalIgnoreCase)))
                            series.Name = baseName + " (가져오기 " + suffix++ + ")";
                        merged.Series.Add(series);
                    }
                    else foreach (var connector in series.Connectors)
                    {
                        existing.Connectors.RemoveAll(c => c.Id.Equals(connector.Id, StringComparison.OrdinalIgnoreCase));
                        existing.Connectors.Add(connector);
                    }
                }
                foreach (var layout in incoming.PinLayouts)
                {
                    merged.PinLayouts.RemoveAll(p => p.CatalogKey.Equals(layout.CatalogKey, StringComparison.OrdinalIgnoreCase));
                    merged.PinLayouts.Add(layout);
                }
                Catalog = merged;
                Save();
            }
            catch
            {
                Catalog = original;
                foreach (var image in addedImages)
                    try { File.Delete(image); } catch (IOException) { }
                throw;
            }
        }

        private static void CopyBounded(Stream source, Stream destination, long limit)
        {
            byte[] buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if ((total += count) > limit) throw new InvalidDataException("압축 해제 데이터가 안전 한도를 넘습니다.");
                destination.Write(buffer, 0, count);
            }
        }
    }
}
