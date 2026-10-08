using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ESDP
{
    internal sealed class BrowserExcelWorkbook
    {
        public List<BrowserExcelWorksheet> Sheets { get; set; } = new();
    }

    internal sealed class BrowserExcelWorksheet
    {
        public string Name { get; set; } = "";
        public List<List<string>> Rows { get; set; } = new();

        public string CellText(int row, int column)
        {
            int rowIndex = row - 1;
            int columnIndex = column - 1;
            if (rowIndex < 0 || rowIndex >= Rows.Count) return "";
            var values = Rows[rowIndex];
            return columnIndex >= 0 && columnIndex < values.Count
                ? values[columnIndex]?.Trim() ?? ""
                : "";
        }

        public int LastRow => Rows.Count;
        public int LastColumn => Rows.Count == 0 ? 0 : Rows.Max(row => row.Count);
    }

    internal static class HtmlExcelImportService
    {
        private const string ResourceRoot = "ESDP.Resources.ExcelHtmlImporter.";
        internal const int MaxBodyBytes = 32 * 1024 * 1024;
        private const int MaxHeaderBytes = 16 * 1024;
        internal const long MaxWorkbookBytes = 64L * 1024 * 1024;

        public static async Task<BrowserExcelWorkbook> LoadAsync(string workbookPath)
        {
            // Bound the read even if the file grows after it is opened.
            await using var input = File.OpenRead(workbookPath);
            if (input.Length > MaxWorkbookBytes) throw new InvalidDataException("HTML Excel 가져오기는 64MB 이하 파일을 지원합니다.");
            using var memory = new MemoryStream();
            byte[] buffer = new byte[81920];
            int count;
            while ((count = await input.ReadAsync(buffer)) > 0)
            {
                if (memory.Length + count > MaxWorkbookBytes) throw new InvalidDataException("Excel 파일이 너무 큽니다.");
                memory.Write(buffer, 0, count);
            }
            byte[] workbookBytes = memory.ToArray();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(8);
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                string url = $"http://127.0.0.1:{port}/{token}/?name={Uri.EscapeDataString(Path.GetFileName(workbookPath))}";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return await RunServerAsync(listener, workbookBytes, token, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("HTML Excel 가져오기 대기 시간이 만료되었습니다.");
            }
            finally { listener.Stop(); }
        }

        private static async Task<BrowserExcelWorkbook> RunServerAsync(
            TcpListener listener, byte[] workbookBytes, string token, CancellationToken cancellationToken)
        {
            string prefix = "/" + token + "/";
            string host = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            while (true)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
                await using NetworkStream stream = client.GetStream();
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                var requestToken = requestTimeout.Token;
                try
                {
                    var request = await ReadRequestAsync(stream, requestToken);
                    string path = request.Path.Split('?', 2)[0];
                    bool validOrigin = !request.Headers.TryGetValue("Origin", out var origin) || origin == "http://" + host;
                    if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
                        !request.Headers.TryGetValue("Host", out var receivedHost) || receivedHost != host || !validOrigin)
                    {
                        await WriteResponseAsync(stream, 403, "text/plain", "Forbidden"u8.ToArray(), requestToken);
                        continue;
                    }
                    string route = path[prefix.Length..];
                    if (request.Method == "GET" && route == "")
                        await WriteResourceAsync(stream, "index.html", "text/html; charset=utf-8", requestToken);
                    else if (request.Method == "GET" && route is "app.js" or "jszip.min.js")
                        await WriteResourceAsync(stream, route, "text/javascript; charset=utf-8", requestToken);
                    else if (request.Method == "GET" && route == "workbook")
                        await WriteResponseAsync(stream, 200, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", workbookBytes, requestToken);
                    else if (request.Method == "POST" && route == "result")
                    {
                        var workbook = JsonSerializer.Deserialize<BrowserExcelWorkbook>(request.Body,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                            ?? throw new InvalidDataException("Excel 데이터가 비어 있습니다.");
                        ValidateWorkbook(workbook);
                        await WriteResponseAsync(stream, 200, "text/plain; charset=utf-8", "OK"u8.ToArray(), requestToken);
                        return workbook;
                    }
                    else await WriteResponseAsync(stream, 404, "text/plain", "Not found"u8.ToArray(), requestToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
                {
                    // One malformed/abandoned client must not abort the user's import session.
                    try { await WriteResponseAsync(stream, 400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ex.Message), requestToken); }
                    catch (Exception writeError) when (writeError is IOException or OperationCanceledException) { }
                }
            }
        }

        private static void ValidateWorkbook(BrowserExcelWorkbook workbook)
        {
            if (workbook.Sheets == null || workbook.Sheets.Count is < 1 or > 256)
                throw new InvalidDataException("Excel 시트 수가 올바르지 않습니다.");
            long cells = 0;
            foreach (var sheet in workbook.Sheets)
            {
                if (sheet == null || sheet.Rows == null || sheet.Rows.Count > 100000)
                    throw new InvalidDataException("Excel 행 데이터가 너무 많거나 올바르지 않습니다.");
                foreach (var row in sheet.Rows)
                {
                    if (row == null || row.Count > 16384 || (cells += row.Count) > 2000000)
                        throw new InvalidDataException("Excel 셀 데이터가 너무 많거나 올바르지 않습니다.");
                }
            }
        }

        private static async Task WriteResourceAsync(Stream output, string fileName, string contentType, CancellationToken cancellationToken)
        {
            await using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceRoot + fileName)
                ?? throw new InvalidOperationException($"HTML Excel 리소스가 없습니다: {fileName}");
            using var memory = new MemoryStream();
            await resource.CopyToAsync(memory, cancellationToken);
            await WriteResponseAsync(output, 200, contentType, memory.ToArray(), cancellationToken);
        }

        private static async Task<HttpRequestData> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
        {
            // Buffer header and retain any body bytes received in the same packet.
            using var received = new MemoryStream();
            byte[] buffer = new byte[4096];
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0) throw new EndOfStreamException("HTTP 요청이 중간에 종료되었습니다.");
                long previous = received.Length;
                received.Write(buffer, 0, count);
                byte[] bytes = received.GetBuffer();
                for (int i = Math.Max(0, (int)previous - 3); i <= received.Length - 4; i++)
                    if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10)
                    { headerEnd = i + 4; break; }
                if ((headerEnd < 0 ? received.Length : headerEnd) > MaxHeaderBytes)
                    throw new InvalidDataException("HTTP 헤더가 너무 큽니다.");
            }
            string[] lines = Encoding.ASCII.GetString(received.GetBuffer(), 0, headerEnd).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            string[] first = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (first.Length != 3 || first[2] != "HTTP/1.1" || first[0] is not ("GET" or "POST"))
                throw new InvalidDataException("지원하지 않는 HTTP 요청입니다.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0 || !headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim()))
                    throw new InvalidDataException("잘못되거나 중복된 HTTP 헤더입니다.");
            }
            if (headers.ContainsKey("Transfer-Encoding")) throw new InvalidDataException("청크 요청은 지원하지 않습니다.");
            int length = 0;
            if (headers.TryGetValue("Content-Length", out var text) &&
                (!int.TryParse(text, out length) || length < 0 || length > MaxBodyBytes))
                throw new InvalidDataException("HTTP 본문은 32MB 이하여야 합니다.");
            if (first[0] == "POST" && !headers.ContainsKey("Content-Length"))
                throw new InvalidDataException("Content-Length가 필요합니다.");
            byte[] body = new byte[length];
            int buffered = Math.Min(length, (int)received.Length - headerEnd);
            received.GetBuffer().AsSpan(headerEnd, buffered).CopyTo(body);
            await stream.ReadExactlyAsync(body.AsMemory(buffered), cancellationToken);
            return new HttpRequestData(first[0], first[1], headers, body);
        }

        private static async Task WriteResponseAsync(Stream stream, int statusCode, string contentType, byte[] body, CancellationToken cancellationToken)
        {
            string reason = statusCode switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", _ => "Bad Request" };
            byte[] header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n" +
                "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\n" +
                "Content-Security-Policy: default-src 'self'; style-src 'self' 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private sealed record HttpRequestData(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);
    }
}
