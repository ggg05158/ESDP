using System.IO;
using System.Text.Json;

namespace ESDP
{
    public sealed class RecentProjectInfo
    {
        public string FullPath { get; set; } = "";
        public DateTime LastOpenedUtc { get; set; }

        public string FileName => Path.GetFileNameWithoutExtension(FullPath);
        public string Folder => Path.GetDirectoryName(FullPath) ?? "";
    }

    public static class RecentProjects
    {
        private const int MaximumCount = 8;
        private static readonly string StoragePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ESDP",
            "recent-projects.json");

        public static IReadOnlyList<RecentProjectInfo> Load()
        {
            try
            {
                if (!File.Exists(StoragePath)) return Array.Empty<RecentProjectInfo>();

                var entries = JsonSerializer.Deserialize<List<RecentProjectInfo>>(
                                  File.ReadAllText(StoragePath))
                              ?? new List<RecentProjectInfo>();

                return entries
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.FullPath) &&
                                    File.Exists(entry.FullPath))
                    .GroupBy(entry => Path.GetFullPath(entry.FullPath), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(entry => entry.LastOpenedUtc).First())
                    .OrderByDescending(entry => entry.LastOpenedUtc)
                    .Take(MaximumCount)
                    .ToList();
            }
            catch
            {
                return Array.Empty<RecentProjectInfo>();
            }
        }

        public static void Record(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                var entries = Load()
                    .Where(entry => !string.Equals(
                        Path.GetFullPath(entry.FullPath), fullPath, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                entries.Insert(0, new RecentProjectInfo
                {
                    FullPath = fullPath,
                    LastOpenedUtc = DateTime.UtcNow
                });

                Save(entries.Take(MaximumCount));
            }
            catch
            {
                // 최근 목록 저장 실패는 프로젝트 저장/열기를 막지 않는다.
            }
        }

        public static void Remove(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                Save(Load().Where(entry => !string.Equals(
                    Path.GetFullPath(entry.FullPath), fullPath, StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                // 손상되었거나 접근할 수 없는 최근 목록은 다음 실행에서 다시 정리된다.
            }
        }

        private static void Save(IEnumerable<RecentProjectInfo> entries)
        {
            string? directory = Path.GetDirectoryName(StoragePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(
                StoragePath,
                JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
