using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;

namespace ESDP
{
    /// <summary>
    /// 폐쇄망 공유 폴더의 YYMMdd_ESDP 폴더를 확인해 알림만 표시한다.
    /// 프로그램 파일을 복사하거나 업데이트를 설치하지는 않는다.
    /// </summary>
    public static class UpdateNotificationService
    {
        private static readonly Regex UpdateFolderPattern = new(
            @"^(?<date>\d{6})_ESDP$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant |
            RegexOptions.IgnoreCase);

        private sealed record UpdateFolder(string Name, DateTime Date);
        private static int _searchActive;

        public static async Task CheckAndNotifyAsync()
        {
            string sharePath = AppSettings.UpdateSharePath?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(sharePath)) return;

            // Windows UNC I/O cannot always be interrupted. Allow only one worker,
            // cancel between directory entries, and observe faults even after timeout.
            if (Interlocked.CompareExchange(ref _searchActive, 1, 0) != 0) return;
            var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            Task<UpdateFolder?> searchTask = Task.Run(() => FindLatest(sharePath, cancellation.Token));
            _ = searchTask.ContinueWith(task =>
            {
                _ = task.Exception;
                cancellation.Dispose();
                Interlocked.Exchange(ref _searchActive, 0);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            UpdateFolder? latest;
            try
            {
                latest = await searchTask.WaitAsync(TimeSpan.FromSeconds(4));
            }
            catch
            {
                // 폐쇄망 주소 미연결, 접근 권한 없음 등의 상황은 사용을 방해하지 않는다.
                return;
            }

            if (latest == null || latest.Date <= GetCurrentProgramDate()) return;
            if (string.Equals(
                    latest.Name, AppSettings.LastNotifiedUpdateFolder,
                    StringComparison.OrdinalIgnoreCase)) return;

            AppSettings.LastNotifiedUpdateFolder = latest.Name;
            AppSettings.Save();

            Window? owner = Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(window => window.IsActive)
                ?? Application.Current.MainWindow;
            const string message = "서버에 새로운 업데이트 버전이 존재합니다.";
            if (owner?.IsVisible == true)
            {
                MessageBox.Show(owner, message, "ESDP 업데이트 알림",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(message, "ESDP 업데이트 알림",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private static UpdateFolder? FindLatest(string sharePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(sharePath)) return null;

            UpdateFolder? latest = null;
            foreach (string directory in Directory.EnumerateDirectories(
                         sharePath, "*_ESDP", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(directory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
                Match match = UpdateFolderPattern.Match(name);
                if (!match.Success || !DateTime.TryParseExact(
                        match.Groups["date"].Value,
                        "yyMMdd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out DateTime date)) continue;

                if (latest == null || date > latest.Date)
                    latest = new UpdateFolder(name, date.Date);
            }
            return latest;
        }

        private static DateTime GetCurrentProgramDate()
        {
            // Embedded build date survives copying, publishing and exe timestamp changes.
            string? buildDate = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(item => item.Key == "ESDP.BuildDate")?.Value;
            if (DateTime.TryParseExact(buildDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) return date.Date;
            try
            {
                string? executablePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(executablePath) &&
                    File.Exists(executablePath))
                    return File.GetLastWriteTime(executablePath).Date;

                string assemblyPath = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrWhiteSpace(assemblyPath) &&
                    File.Exists(assemblyPath))
                    return File.GetLastWriteTime(assemblyPath).Date;
            }
            catch
            {
                // 실행 파일 날짜를 읽지 못하면 오늘 날짜를 기준으로 비교한다.
            }
            return DateTime.Today;
        }
    }
}
