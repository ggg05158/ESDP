using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ESDP;
using ESDP.Models;

static class InfrastructureChecks
{
    internal static readonly string Temp = Path.Combine(Path.GetTempPath(), "ESDP.RegressionTests", Guid.NewGuid().ToString("N"));
    static readonly Assembly ESDPAssembly = typeof(MainWindow).Assembly;
    static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    static void Reject(Action action, string label)
    {
        try { action(); } catch (Exception ex) when (ex is not NullReferenceException) { return; }
        throw new Exception("Not rejected: " + label);
    }
    static ConnectorCatalogService Store(string name) => (ConnectorCatalogService)Activator.CreateInstance(typeof(ConnectorCatalogService), PrivateInstance, null, new object[] { Path.Combine(Temp, name) }, null)!;
    public static void Data()
    {
        Directory.CreateDirectory(Temp);
        var random = new Random(441);
        var index = new RectSpatialIndex();
        var boxes = new Dictionary<object, Rect>();
        for (int i = 0; i < 500; i++)
        {
            object obstacleKey = new object();
            var box = new Rect(random.Next(-5000, 5000), random.Next(-5000, 5000), random.Next(1, 200), random.Next(1, 200));
            boxes[obstacleKey] = box; index.Put(obstacleKey, box);
        }
        object large = new object(); boxes[large] = new Rect(-10000, -10000, 20000, 20000); index.Put(large, boxes[large]);
        for (int i = 0; i < 2000; i++)
        {
            var query = new Rect(random.Next(-11000, 11000), random.Next(-11000, 11000), random.Next(1, 100), random.Next(1, 100));
            object? except = i % 2 == 0 ? large : null;
            Check(index.AnyOverlap(query, except) == boxes.Any(pair => !ReferenceEquals(pair.Key, except) && pair.Value.IntersectsWith(query)), "Spatial lookup differs");
        }
        index.Remove(large); boxes.Remove(large);
        Check(!index.AnyOverlap(new Rect(-9999, -9999, 1, 1)), "Large obstacle removal failed");
        Console.WriteLine("PASS: 2000 indexed rectangle queries vs brute, exclusion and large-obstacle removal");

        string image = Path.Combine(Temp, "source.png");
        File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII="));
        var source = Store("source");
        var series = source.AddSeries("UserSeries");
        var connector = source.AddConnector(series, "TestConnector", 4, image);
        string key = ConnectorCatalogService.CustomCatalogKey(series, connector);
        source.SavePinPositions(key, new[] { new ConnectorPinPosition { PinNumber = "1", X = .2, Y = .4 } }, 3);
        source.AddSeries("SecondSeries");
        string package = Path.Combine(Temp, "sample.esdplib");
        source.ExportLibrary(package);
        var destination = Store("destination");
        destination.AddSeries("Existing");
        destination.ImportLibrary(package);
        destination.ImportLibrary(package);
        Check(destination.Catalog.Series.Count == 3, "Import removed existing/duplicated series");
        var imported = destination.Catalog.Series.Single(s => s.Id == series.Id).Connectors.Single();
        Check(File.ReadAllBytes(destination.ResolveImagePath(imported)!).SequenceEqual(File.ReadAllBytes(image)), "Image bytes changed");
        Check(destination.GetPinPositions(key).Single().X == .2 && destination.GetPinMarkerDiameterPoints(key) == 3, "Pin layout lost");
        string catalogFile = Path.Combine(Temp, "source", "catalog.json");
        File.WriteAllText(catalogFile, "corrupted original");
        var recovered = Store("source");
        Check(!recovered.IsReadOnly && recovered.LastLoadWarning != null && recovered.GetPinPositions(key).Count == 1, "Backup recovery failed");
        recovered.AddSeries("AfterRecovery");
        Check(Directory.GetFiles(Path.GetDirectoryName(catalogFile)!, "catalog.json.corrupt.*").Length == 1, "Corrupt original not preserved");
        var blocked = Store("blocked");
        string blockedFile = Path.Combine(Temp, "blocked", "catalog.json");
        File.WriteAllText(blockedFile, "broken"); blocked.Load();
        Check(blocked.IsReadOnly && blocked.LastLoadWarning != null, "Corrupt data silently emptied");
        Reject(() => blocked.AddSeries("must not write"), "Read-only library");
        Check(File.ReadAllText(blockedFile) == "broken", "Corrupt original overwritten");
        string malicious = Path.Combine(Temp, "malicious.esdplib");
        using (var zip = ZipFile.Open(malicious, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("../escape.png").Open())) writer.Write("bad");
        string before = JsonSerializer.Serialize(destination.Catalog);
        Reject(() => destination.ImportLibrary(malicious), "ZIP traversal");
        Check(before == JsonSerializer.Serialize(destination.Catalog), "Rejected import changed library");
        destination.DeleteConnector(destination.Catalog.Series.Single(s => s.Id == series.Id), imported);
        Check(File.Exists(destination.ResolveImagePath(imported)), "Backup image deleted too early");
        Console.WriteLine("PASS: library image/pin merge, repeated import, backup recovery, corrupt-file preservation, read-only guard, ZIP traversal rejection, backup images retained");

        var size = PdfRasterLimits.GetSize(420, 297, 300);
        Check(size.Width > 4900 && size.Height > 3400, "PDF size wrong");
        Reject(() => PdfRasterLimits.GetSize(420, 297, 600), "Oversized PDF");
        Reject(() => PdfRasterLimits.GetSize(double.NaN, 297, 300), "NaN PDF");
        Reject(() => PdfRasterLimits.GetSize(1e10, 297, 300), "Overflow PDF");
        Console.WriteLine("PASS: PDF pre-allocation memory and invalid/overflow dimensions rejected");

        string updates = Path.Combine(Temp, "updates"); Directory.CreateDirectory(updates);
        Directory.CreateDirectory(Path.Combine(updates, "261001_ESDP"));
        Directory.CreateDirectory(Path.Combine(updates, "261002_ESDP"));
        Directory.CreateDirectory(Path.Combine(updates, "261399_ESDP"));
        var updateType = typeof(UpdateNotificationService);
        object latest = updateType.GetMethod("FindLatest", PrivateStatic)!.Invoke(null, new object[] { updates, CancellationToken.None })!;
        Check(latest.GetType().GetProperty("Name")!.GetValue(latest)?.ToString() == "261002_ESDP", "Wrong update folder");
        string metadata = ESDPAssembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ESDP.BuildDate").Value!;
        Check(((DateTime)updateType.GetMethod("GetCurrentProgramDate", PrivateStatic)!.Invoke(null, null)!).ToString("yyyy-MM-dd") == metadata, "Update date not embedded");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Reject(() => updateType.GetMethod("FindLatest", PrivateStatic)!.Invoke(null, new object[] { updates, canceled.Token }), "Canceled update");
        Console.WriteLine("PASS: update date uses embedded metadata, invalid dates skipped, cancellation honored");
        Http().GetAwaiter().GetResult();
    }
    static async Task Http()
    {
        var serverType = ESDPAssembly.GetType("ESDP.HtmlExcelImportService")!;
        var read = serverType.GetMethod("ReadRequestAsync", PrivateStatic)!;
        async Task<object> Read(string request)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request));
            var task = (Task)read.Invoke(null, new object[] { stream, CancellationToken.None })!;
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task)!;
        }
        var parsed = await Read("POST /result HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 2\r\n\r\n{}");
        Check(Encoding.UTF8.GetString((byte[])parsed.GetType().GetProperty("Body")!.GetValue(parsed)!) == "{}", "Buffered HTTP body lost");
        foreach (string headers in new[] { "Content-Length: -1", "Content-Length: 33554433", "Content-Length: invalid", "Content-Length: 0\r\nContent-Length: 0", "Transfer-Encoding: chunked" })
        {
            bool rejected = false;
            try { await Read("POST /result HTTP/1.1\r\n" + headers + "\r\n\r\n"); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Unsafe HTTP headers accepted");
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = (Task)serverType.GetMethod("RunServerAsync", PrivateStatic)!.Invoke(null, new object[] { listener, new byte[] { 1, 2 }, "test-token", stop.Token })!;
        try
        {
            using var http = new System.Net.Http.HttpClient();
            string url = $"http://127.0.0.1:{port}/";
            Check((await http.GetAsync(url + "wrong/workbook")).StatusCode == HttpStatusCode.Forbidden, "Token not enforced");
            var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url + "test-token/workbook");
            request.Headers.Add("Origin", "https://untrusted.example");
            Check((await http.SendAsync(request)).StatusCode == HttpStatusCode.Forbidden, "Foreign origin accepted");
            var response = await http.GetAsync(url + "test-token/workbook");
            Check(response.IsSuccessStatusCode && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(new byte[] { 1, 2 }), "Authorized workbook failed");
            Check(response.Headers.Contains("Content-Security-Policy"), "Missing CSP");
            var result = await http.PostAsync(url + "test-token/result", new System.Net.Http.StringContent("{\"sheets\":[{\"name\":\"S\",\"rows\":[[\"x\"]]}]}", Encoding.UTF8, "application/json"));
            Check(result.IsSuccessStatusCode, "Valid browser result failed");
            await server;
        }
        finally { stop.Cancel(); listener.Stop(); }
        Console.WriteLine("PASS: buffered HTTP parsing, bounded/duplicate headers, token and origin checks, successful import after invalid clients");
    }
}
