using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ESDP;
using ESDP.Models;
using P = System.Windows.Point;

class Program
{
    static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static object? Call(MainWindow w, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Flags)!.Invoke(w, args);
    static T Field<T>(MainWindow w, string name) => (T)typeof(MainWindow).GetField(name, Flags)!.GetValue(w)!;
    static List<CableCrossingIndex.Crossing> Brute(List<List<(P A, P B, string LegKey)>> cables)
    {
        var flat = cables.SelectMany((list, c) => list.Select(s => (s.A, s.B, s.LegKey, C: c))).ToList();
        var result = new List<CableCrossingIndex.Crossing>();
        var seen = new HashSet<(double, double)>();
        for (int i = 0; i < flat.Count; i++)
            for (int j = 0; j < flat.Count; j++)
            {
                var h = flat[i]; var v = flat[j];
                if (i == j || h.LegKey == v.LegKey || Math.Abs(h.A.Y - h.B.Y) >= .01 || Math.Abs(v.A.X - v.B.X) >= .01) continue;
                if (v.A.X > Math.Min(h.A.X, h.B.X) + 3 && v.A.X < Math.Max(h.A.X, h.B.X) - 3 &&
                    h.A.Y > Math.Min(v.A.Y, v.B.Y) + 3 && h.A.Y < Math.Max(v.A.Y, v.B.Y) - 3 &&
                    seen.Add((Math.Round(v.A.X, 1), Math.Round(h.A.Y, 1))))
                    result.Add(new(v.A.X, h.A.Y, h.C, v.C));
            }
        return result;
    }
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--pptx-only"))
        {
            var pptApp = new App(); pptApp.InitializeComponent();
            try { PptxChecks.Run(args[Array.IndexOf(args, "--project") + 1], Path.GetFullPath(".verification/pptx-export/qa")); }
            catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; }
            pptApp.Shutdown(); return;
        }
        if (args.Contains("--color-only"))
        {
            var colorApp = new App(); colorApp.InitializeComponent();
            ColorPickerChecks.Run(args.Contains("--color-qa"));
            return;
        }
        bool layoutOnly = args.Contains("--layout");
        if (!layoutOnly) { InfrastructureChecks.Data(); StorageChecks.Run(); }
        var random = new Random(190);
        for (int trial = 0; trial < 300; trial++)
        {
            var cables = new List<List<(P, P, string)>>();
            for (int c = 0; c < 8; c++)
            {
                var lines = new List<(P, P, string)>();
                for (int i = 0; i < 25; i++)
                {
                    double x = random.Next(-30, 31), y = random.Next(-30, 31), length = random.Next(-25, 26);
                    lines.Add((new P(x, y), i % 2 == 0 ? new P(x + length, y) : new P(x, y + length), $"leg{random.Next(12)}"));
                }
                cables.Add(lines);
            }
            Check(Brute(cables).SequenceEqual(CableCrossingIndex.Find(cables)), $"Crossing mismatch {trial}");
        }
        Console.WriteLine("PASS: 300 randomized crossing comparisons, including end clearances, same legs and duplicate points");
        var sparse = new List<List<(P, P, string)>> { new() };
        for (int i = 0; i < 6000; i++)
            sparse[0].Add((new P(i * 30, i * 30), i % 2 == 0 ? new P(i * 30 + 20, i * 30) : new P(i * 30, i * 30 + 20), i.ToString()));
        var timer = Stopwatch.StartNew(); var old = Brute(sparse); timer.Stop(); double before = timer.Elapsed.TotalMilliseconds;
        timer.Restart(); var current = CableCrossingIndex.Find(sparse); timer.Stop();
        Check(old.SequenceEqual(current), "Sparse mismatch");
        Console.WriteLine($"6000 sparse segments: brute {before:F1}ms, sweep {timer.Elapsed.TotalMilliseconds:F1}ms");

        var app = new App(); app.InitializeComponent();
        ColorPickerChecks.Run(args.Contains("--color-qa"));
        var window = new MainWindow();
        ShapeChecks.Run(window, args.Contains("--shape-qa"));
        CheckPortDrags(window);
        CheckFreeTailAndFrames(window);
        var pcs = Field<List<PlacedCable>>(window, "_placedCables");
        var canvas = Field<Canvas>(window, "MainCanvas");
        pcs.Clear();
        for (int c = 0; c < 3; c++)
        {
            var template = new Cable { Name = "TEST " + c };
            template.Connectors.Add(new CableConnector { Id = "a", PortName = "P1" });
            template.Connectors.Add(new CableConnector { Id = "b", PortName = "P2" });
            var instance = new CableInstance { CableId = template.Id,
                PortA = new CablePortLayout { ConnectorId = "a", PortX = 100, PortY = 100 + c * 100, FixedFacing = PortSide.Left },
                PortB = new CablePortLayout { ConnectorId = "b", PortX = 400, PortY = 100 + c * 100, FixedFacing = PortSide.Right } };
            var pc = (PlacedCable)Call(window, "CreatePlacedCable", template, instance)!;
            pcs.Add(pc); canvas.Children.Add(pc.Container);
        }
        Call(window, "RenderAllCables");
        var unrelatedSegments = pcs[2].RenderedSegments;
        var initialLabels = Field<Canvas>(window, "_labelOverlay").Children.OfType<TextBlock>().ToArray();
        string beforeModel = JsonSerializer.Serialize(pcs.Select(pc => pc.Instance));
        for (int i = 0; i < 100; i++) Call(window, "QueueCableDragRender", pcs[0]);
        Check(Field<HashSet<PlacedCable>>(window, "_pendingCableDragRenders").Count == 1, "Input was not coalesced");
        Call(window, "FlushCableDragRender");
        Check(ReferenceEquals(unrelatedSegments, pcs[2].RenderedSegments), "Unrelated cable rebuilt");
        Check(initialLabels.All(label => Field<Canvas>(window, "_labelOverlay").Children.Contains(label)), "Labels not reused");
        Check(beforeModel == JsonSerializer.Serialize(pcs.Select(pc => pc.Instance)), "Rendering changed authored model");
        pcs[1].Instance.PortA.ConnectedToPortKey = $"cab:{pcs[0].Instance.Id}:b";
        pcs[0].Instance.PortB.PortX = 470;
        Call(window, "QueueCableDragRender", pcs[0]);
        Check(Field<HashSet<PlacedCable>>(window, "_pendingCableDragRenders").Count == 1, "Dependent graph was expanded before frame flush");
        Call(window, "FlushCableDragRender");
        var connectedSegments = pcs[1].RenderedSegments.ToArray();
        Check(pcs[1].Instance.PortA.PortX > 450, "Connected endpoint stale");
        Call(window, "RenderAllCables");
        Check(!Field<bool>(window, "_cableRenderFramePending"), "Pending input survived final render");
        Check(Field<Canvas>(window, "_crossingOverlay").Visibility == Visibility.Visible, "Crossings hidden after drop");
        Check(connectedSegments.SequenceEqual(pcs[1].RenderedSegments), "Partial and full connected render differ");
        Call(window, "QueueCableDragRender", pcs[0]);
        Call(window, "CancelCableDragRender");
        Check(!Field<bool>(window, "_cableRenderFramePending"), "Frame callback leak");
        Console.WriteLine("PASS: 100 events coalesced; unrelated geometry retained; labels reused; model unchanged; connected endpoints and final flush correct");
        Call(window, "RenderAllCables");
        var options = new JsonSerializerOptions();
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var projectArg = Array.FindIndex(args, arg => arg == "--project");
        if (projectArg >= 0 && projectArg + 1 >= args.Length)
            throw new ArgumentException("--project requires a project file path.");
        var saved = projectArg >= 0
            ? ProjectFileService.LoadPrepared(args[projectArg + 1]).Project
            : CreateFixture();
        var sheet = saved.Sheets.First();
        var normalized = JsonSerializer.Deserialize<Project>(JsonSerializer.Serialize(saved, options), options)!;
        typeof(MainWindow).GetMethod("NormalizeProjectDevicesToGrid", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { normalized });
        foreach (var pc in pcs) canvas.Children.Remove(pc.Container);
        pcs.Clear();
        var devices = Field<List<PlacedDevice>>(window, "_placed");
        devices.Clear();
        foreach (var instance in sheet.DeviceInstances)
        {
            var device = new PlacedDevice { Instance = instance, TemplateDevice = saved.Devices.First(d => d.Id == instance.DeviceId), Container = new Canvas() };
            devices.Add(device); canvas.Children.Add(device.Container); Call(window, "RenderDevice", device);
        }
        foreach (var instance in sheet.CableInstances)
        {
            var pc = (PlacedCable)Call(window, "CreatePlacedCable", saved.Cables.First(c => c.Id == instance.CableId), instance)!;
            pcs.Add(pc); canvas.Children.Add(pc.Container);
        }
        Call(window, "RenderAllCables");
        string savedModel = JsonSerializer.Serialize(sheet.CableInstances, options);
        foreach (var pc in pcs)
        {
            Call(window, "QueueCableDragRender", pc);
            Call(window, "FlushCableDragRender");
            var partial = pcs.Select(c => c.RenderedSegments.ToArray()).ToArray();
            Call(window, "RenderAllCables");
            Check(pcs.Select((c, i) => c.RenderedSegments.SequenceEqual(partial[i])).All(match => match), "grid_test partial/full geometry mismatch");
        }
        Check(savedModel == JsonSerializer.Serialize(sheet.CableInstances, options), "grid_test model changed by refresh");
        if (devices.Count > 0)
        {
            devices[0].Instance.X += 30;
            Call(window, "QueueDeviceCableDragRender", devices[0]);
            Call(window, "FlushCableDragRender");
            var partial = pcs.Select(c => c.RenderedSegments.ToArray()).ToArray();
            Call(window, "RenderAllCables");
            Check(pcs.Select((c, i) => c.RenderedSegments.SequenceEqual(partial[i])).All(match => match), "Device move partial/full mismatch");
        }
        Console.WriteLine($"PASS: project fixture ({pcs.Count} cables), partial/full geometry identical; routes and branch model retained; device-dependent refresh identical");
        string Routes() => JsonSerializer.Serialize(sheet.CableInstances.Select(c => new
        {
            c.RootBends,
            Branches = c.Branches.Select(b => new { b.Id, b.ParentId, b.T, b.Bends, b.Port.PortX, b.Port.PortY, b.Port.ConnectedToPortKey }),
            c.PortA.PortX, c.PortA.PortY, Bx = c.PortB.PortX, By = c.PortB.PortY
        }), options);
        var routesBefore = Routes();
        Call(window, "Menu_AutoArrangeAllCables", window, new RoutedEventArgs());
        Check(routesBefore == Routes(), "Label arrangement modified cable routes or branch anchors");
        string layoutJson = JsonSerializer.Serialize(new { normalized, cables = sheet.CableInstances, devices = saved.Devices }, options);
        Console.WriteLine("LAYOUT:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(layoutJson))));
        if (!layoutOnly) Console.WriteLine("Fixture output: " + InfrastructureChecks.Temp);
        typeof(MainWindow).GetProperty("_isDirty", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, false);
        window.Close(); app.Shutdown();
    }

    static void CheckPortDrags(MainWindow window)
    {
        var pcs = Field<List<PlacedCable>>(window, "_placedCables");
        var canvas = Field<Canvas>(window, "MainCanvas");
        foreach (bool straight in new[] { false, true })
        {
            var template = new Cable { Name = "Port-drag fixture" };
            foreach (string id in new[] { "a", "b", "c", "d" })
                template.Connectors.Add(new CableConnector { Id = id, PortName = id });
            var inst = new CableInstance
            {
                CableId = template.Id,
                PortA = new CablePortLayout { ConnectorId = "a", PortX = 100, PortY = 300, FixedFacing = PortSide.Right },
                PortB = new CablePortLayout { ConnectorId = "b", PortX = 700, PortY = straight ? 300 : 500, FixedFacing = PortSide.Left },
                RootBends = straight ? new() : new() { new ESDP.Models.Point { X = 500, Y = 300 }, new ESDP.Models.Point { X = 500, Y = 500 } }
            };
            var parent = new CableBranch { Id = "parent", ParentId = "root", Port = new CablePortLayout { ConnectorId = "c", PortX = 500, PortY = 100, FixedFacing = PortSide.Bottom } };
            var child = new CableBranch { Id = "child", ParentId = "parent", Port = new CablePortLayout { ConnectorId = "d", PortX = 800, PortY = 200, FixedFacing = PortSide.Left } };
            inst.Branches.AddRange(new[] { parent, child });
            var pc = (PlacedCable)Call(window, "CreatePlacedCable", template, inst)!;
            pcs.Add(pc); canvas.Children.Add(pc.Container);
            List<P> Route(string id) => (List<P>)Call(window, "ComputeLegRoute", inst, id, false)!;
            P Anchor(CableBranch branch) => CableRenderer.PointAlongPolyline(Route(branch.ParentId), branch.T);
            parent.T = CableRenderer.FractionAlongPolyline(Route("root"), new P(500, straight ? 300 : 400));
            child.T = CableRenderer.FractionAlongPolyline(Route("parent"), new P(500, 200));
            var before = inst.Branches.ToDictionary(branch => branch, Anchor);
            var branchRoutes = inst.Branches.ToDictionary(branch => branch, branch => Route(branch.Id).ToArray());
            var protection = Call(window, "CaptureCablePortDragProtection", pc, inst.PortB);
            foreach (var position in new[] { new P(900, 500), new P(400, 600), new P(750, 200), new P(700, straight ? 300 : 500) })
            {
                inst.PortB.PortX = position.X; inst.PortB.PortY = position.Y;
                Call(window, "PreserveCablePortDragBranches", inst, protection);
                foreach (var branch in inst.Branches)
                {
                    Check((Anchor(branch) - before[branch]).Length < .01, $"Port drag moved attachment: straight={straight}, port={position}, branch={branch.Id}, before={before[branch]}, after={Anchor(branch)}, root={string.Join(';', Route("root"))}");
                    Check(Route(branch.Id).Count == branchRoutes[branch].Length && Route(branch.Id).Zip(branchRoutes[branch]).All(pair => (pair.First - pair.Second).Length < .01), $"Port drag changed unrelated branch route: straight={straight}, branch={branch.Id}, port={position}, before={string.Join(';', branchRoutes[branch])}, after={string.Join(';', Route(branch.Id))}");
                }
            }
            protection = Call(window, "CaptureCablePortDragProtection", pc, inst.PortA);
            inst.PortA.PortX = 50; inst.PortA.PortY = 650;
            Call(window, "PreserveCablePortDragBranches", inst, protection);
            foreach (var branch in inst.Branches)
                Check((Anchor(branch) - before[branch]).Length < .01, $"Start-port drag moved attachment: straight={straight}, branch={branch.Id}, before={before[branch]}, after={Anchor(branch)}, protection={protection}, bends={string.Join(';', inst.RootBends.Select(b => $"{b.X},{b.Y}"))}, route={string.Join(';', Route("root"))}");
            var childBefore = Anchor(child);
            var parentBefore = Anchor(parent);
            var childRoute = Route(child.Id).ToArray();
            protection = Call(window, "CaptureCablePortDragProtection", pc, parent.Port);
            parent.Port.PortY = 50;
            Call(window, "PreserveCablePortDragBranches", inst, protection);
            Check((Anchor(child) - childBefore).Length < .01 && (Anchor(parent) - parentBefore).Length < .01, "Branch-port drag moved nested attachment");
            Check(Route(child.Id).Count == childRoute.Length && Route(child.Id).Zip(childRoute).All(pair => (pair.First - pair.Second).Length < .01), $"Branch-port drag changed descendant route: before={string.Join(';', childRoute)}, after={string.Join(';', Route(child.Id))}");
            pcs.Remove(pc); canvas.Children.Remove(pc.Container);
        }
        Check(DevicePreviewRenderer.PortWidth == 3, "Device port thickness not halved");
        Console.WriteLine("PASS: root/branch port drags preserve branch anchors and descendant routes, including shortened and redirected endpoints; device port thickness 3px");
    }

    static void CheckFreeTailAndFrames(MainWindow window)
    {
        var template = new Cable { Name = "Fixed-junction/free-tail" };
        foreach (string id in new[] { "a", "b", "c" }) template.Connectors.Add(new CableConnector { Id = id, PortName = id });
        var inst = new CableInstance
        {
            CableId = template.Id,
            PortA = new CablePortLayout { ConnectorId = "a", PortX = 500, PortY = 100, FixedFacing = PortSide.Right },
            PortB = new CablePortLayout { ConnectorId = "b", PortX = 700, PortY = 400, FixedFacing = PortSide.Bottom },
            RootBends = new() { new ESDP.Models.Point { X = 600, Y = 100 }, new ESDP.Models.Point { X = 600, Y = 600 }, new ESDP.Models.Point { X = 700, Y = 600 } }
        };
        var branch = new CableBranch { Id = "fixed-junction", ParentId = "root", Port = new CablePortLayout { ConnectorId = "c", PortX = 100, PortY = 500, FixedFacing = PortSide.Right } };
        inst.Branches.Add(branch);
        var pc = (PlacedCable)Call(window, "CreatePlacedCable", template, inst)!;
        var pcs = Field<List<PlacedCable>>(window, "_placedCables"); pcs.Add(pc);
        var canvas = Field<Canvas>(window, "MainCanvas"); canvas.Children.Add(pc.Container);
        List<P> Route(string id) => (List<P>)Call(window, "ComputeLegRoute", inst, id, false)!;
        branch.T = CableRenderer.FractionAlongPolyline(Route("root"), new P(600, 500));
        P Anchor() => CableRenderer.PointAlongPolyline(Route("root"), branch.T);
        P junction = Anchor();
        var branchBefore = Route(branch.Id).ToArray();
        var protection = Call(window, "CaptureCablePortDragProtection", pc, inst.PortB);
        inst.PortB.PortX = 900; inst.PortB.PortY = 450;
        Call(window, "PreserveCablePortDragBranches", inst, protection);
        Check((Anchor() - junction).Length < .01, "Screenshot junction 1 moved");
        Check(inst.RootBends.Any(point => Math.Abs(point.X - 900) < .01 && Math.Abs(point.Y - 500) < .01), "Tail did not rebuild the direct L-shaped connection");
        Check(inst.RootBends.All(point => point.Y <= 500), "Old U-shaped tail retained translated corners");
        Check(inst.RootBends.Any(point => point.X == 600 && point.Y == 100), "Protected body bend moved");
        Check(Route(branch.Id).Count == branchBefore.Length && Route(branch.Id).Zip(branchBefore).All(pair => (pair.First - pair.Second).Length < .01), "Other branch changed when moving free tail");
        inst.PortB.PortY = 650;
        Call(window, "PreserveCablePortDragBranches", inst, protection);
        Check(inst.PortB.FixedFacing == PortSide.Top, "Free P3 kept the old U-shaped approach direction");
        Check(inst.RootBends.Any(point => point.X == 900 && point.Y == 500), "Reported red horizontal/vertical route was not built");
        Check(inst.RootBends.All(point => point.Y <= 500), "P3 below the junction retained a lower U detour");
        Check((Anchor() - junction).Length < .01, "Red L-path moved fixed junction");
        foreach (var position in new[] { new P(850, 650), new P(550, 650), new P(850, 350), new P(550, 350), new P(600, 350), new P(600, 650) })
        {
            inst.PortB.PortX = position.X; inst.PortB.PortY = position.Y;
            Call(window, "PreserveCablePortDragBranches", inst, protection);
            Check((Anchor() - junction).Length < .01, $"Local tail moved junction for port {position}");
            Check(inst.RootBends.Count <= 5, "Local tail kept unnecessary inherited bends");
        }
        inst.PortB.PortX = 700; inst.PortB.PortY = 400;
        Call(window, "PreserveCablePortDragBranches", inst, protection);
        Check(inst.RootBends.All(point => point.Y <= 500), "Returning port restored the old U-shaped detour");
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, Flags)!.SetValue(window, value);
        Set("_cablePortDragPlaced", pc); Set("_cablePortDragLayout", inst.PortB); Set("_cablePortDragProtection", protection);
        string before = JsonSerializer.Serialize(inst);
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) Call(window, "QueueCablePortDragInput", pc, new P(750 + i % 150, 450));
        timer.Stop();
        Check(before == JsonSerializer.Serialize(inst), "Queued mouse input performed per-event model edits");
        Check(Field<HashSet<PlacedCable>>(window, "_pendingCableDragRenders").Count == 1, "Port input not coalesced");
        var last = Field<P?>(window, "_pendingCablePortMouse");
        Check(last.HasValue && last.Value == new P(849, 450), "Latest mouse input lost");
        Call(window, "FlushCableDragRender");
        Check(Field<P?>(window, "_pendingCablePortMouse") == null, "Port input survived frame flush");
        Check(Math.Abs(inst.PortB.PortX - last!.Value.X) <= 30 && Math.Abs(inst.PortB.PortY - last.Value.Y) <= 30, "Last input was not applied/snapped");
        Check((Anchor() - junction).Length < .01, "Queued frame moved fixed junction");
        Check(inst.RootBends.All(point => point.Y <= Math.Max(junction.Y, inst.PortB.PortY)), "Queued frame retained an old tail detour");
        var pendingPosition = new P(900, 500);
        Call(window, "QueueCablePortDragInput", pc, pendingPosition);
        Call(window, "RenderAllCables");
        Check(Math.Abs(inst.PortB.PortX - pendingPosition.X) <= 30 && Math.Abs(inst.PortB.PortY - pendingPosition.Y) <= 30, "Full render discarded final pending input");
        Check((Anchor() - junction).Length < .01, "Final render moved protected junction");
        Call(window, "QueueCablePortDragInput", pc, new P(1000, 550));
        Call(window, "CancelCableDragRender");
        Check(Field<P?>(window, "_pendingCablePortMouse") == null, "Canceled drag retained input");
        Set("_cablePortDragPlaced", null); Set("_cablePortDragLayout", null); Set("_cablePortDragProtection", null);
        var device = new Device { Name = "Connection target" };
        foreach (var side in new[] { PortSide.Left, PortSide.Right, PortSide.Top, PortSide.Bottom })
            device.Ports.Add(new Port { Id = side.ToString(), Designator = side.ToString(), Side = side });
        var targetDevice = new PlacedDevice { TemplateDevice = device, Instance = new DeviceInstance { DeviceId = device.Id, X = 1000, Y = 300, Width = 120, Height = 180 }, Container = new Canvas() };
        var devices = Field<List<PlacedDevice>>(window, "_placed"); devices.Add(targetDevice);
        foreach (var side in new[] { PortSide.Left, PortSide.Right, PortSide.Top, PortSide.Bottom })
        {
            inst.PortB.ConnectedToPortKey = $"dev:{targetDevice.Instance.Id}:{side}";
            Call(window, "PreserveCablePortDragBranches", inst, protection);
            Call(window, "RenderAllCables");
            var route = Route("root");
            var outward = side switch { PortSide.Left => new Vector(-1, 0), PortSide.Right => new Vector(1, 0), PortSide.Top => new Vector(0, -1), _ => new Vector(0, 1) };
            var lastSegment = route[^2] - route[^1];
            Check(Vector.Multiply(lastSegment, outward) > .01, $"Connected {side} port approached backwards");
            Check((Anchor() - junction).Length < .01, $"Connected {side} port moved junction");
            Check(inst.RootBends.All(point => point.X != 600 || point.Y != 600), "Connected port kept original U-tail corner");
        }
        devices.Remove(targetDevice);
        var targetTemplate = new Cable { Name = "Cable connection target" };
        targetTemplate.Connectors.Add(new CableConnector { Id = "target", PortName = "Target" });
        targetTemplate.Connectors.Add(new CableConnector { Id = "other", PortName = "Other" });
        var targetInst = new CableInstance { CableId = targetTemplate.Id, PortA = new CablePortLayout { ConnectorId = "target", PortX = 1000, PortY = 650, FixedFacing = PortSide.Right }, PortB = new CablePortLayout { ConnectorId = "other", PortX = 1200, PortY = 650, FixedFacing = PortSide.Left } };
        var targetCable = (PlacedCable)Call(window, "CreatePlacedCable", targetTemplate, targetInst)!;
        pcs.Add(targetCable); canvas.Children.Add(targetCable.Container);
        inst.PortB.ConnectedToPortKey = $"cab:{targetInst.Id}:target";
        Call(window, "PreserveCablePortDragBranches", inst, protection);
        Check((Anchor() - junction).Length < .01, "Cable-to-cable connection moved junction");
        Check(inst.RootBends.All(point => point.X != 600 || point.Y != 600), "Cable-to-cable connection retained old U tail");
        pcs.Remove(targetCable); canvas.Children.Remove(targetCable.Container);
        inst.PortB.ConnectedToPortKey = null;
        pcs.Remove(pc); canvas.Children.Remove(pc.Container);
        Console.WriteLine($"PASS: red L-tail, fixed junction/body, all four device port directions, cable-to-cable connection, no old U-tail; 1000 inputs queued in {timer.Elapsed.TotalMilliseconds:F1}ms; final frame/cancel correct");
    }

    static Project CreateFixture()
    {
        var project = new Project();
        var template = new Cable { Id = "fixture-cable", Name = "Cable" };
        foreach (string id in new[] { "a", "b", "c" }) template.Connectors.Add(new CableConnector { Id = id, PortName = id });
        project.Cables.Add(template);
        var sheet = new Sheet { Name = "Fixture" };
        project.Sheets.Add(sheet);
        sheet.CableInstances.Add(new CableInstance
        {
            Id = "fixture-instance", CableId = template.Id,
            PortA = new CablePortLayout { ConnectorId = "a", PortX = 100, PortY = 100, FixedFacing = PortSide.Left },
            PortB = new CablePortLayout { ConnectorId = "b", PortX = 400, PortY = 300, FixedFacing = PortSide.Right },
            RootBends = new() { new ESDP.Models.Point { X = 250, Y = 100 }, new ESDP.Models.Point { X = 250, Y = 300 } }
        });
        sheet.CableInstances[0].Branches.Add(new CableBranch
        {
            Id = "fixture-branch", ParentId = "root", T = .5,
            Port = new CablePortLayout { ConnectorId = "c", PortX = 550, PortY = 200, FixedFacing = PortSide.Right },
            Bends = new() { new ESDP.Models.Point { X = 350, Y = 200 } }
        });
        return project;
    }
}
