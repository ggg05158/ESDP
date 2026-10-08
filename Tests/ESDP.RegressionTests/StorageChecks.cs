using System.Text.Json;
using ESDP;
using ESDP.Models;

static class StorageChecks
{
    sealed record State(int Position, string Image);
    static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
    }

    public static void Run()
    {
        var store = new HistorySnapshotStore(new JsonSerializerOptions());
        string image = new('x', 200_000);
        var snapshots = Enumerable.Range(0, 100).Select(index => store.Capture(new State(index, image))).ToList();
        store.Collect(snapshots.Where((_, index) => index % 2 == 0));
        for (int index = 0; index < 100; index += 2)
            Check(store.Restore<State>(snapshots[index]) == new State(index, image), "History snapshot changed after pruning");
        store.Clear();
        Check(store.RetainedBytes == 0, "History memory not released");

        string file = System.IO.Path.Combine(InfrastructureChecks.Temp, "project.esdp");
        var first = new Project { Sheets = new() { new Sheet { Name = "First" } } };
        ProjectFileService.SaveAtomic(file, first);
        first.Sheets[0].Name = "Second";
        ProjectFileService.SaveAtomic(file, first);
        Check(ProjectFileService.LoadPrepared(file).Project.Sheets[0].Name == "Second", "Latest save lost");
        Check(ProjectFileService.LoadPrepared(file + ".bak").Project.Sheets[0].Name == "First", "Save backup lost");
        string before = System.IO.File.ReadAllText(file);
        first.SchemaVersion = Project.CurrentSchemaVersion + 100;
        // Loading an unsupported future format must not silently accept it.
        string future = System.IO.Path.Combine(InfrastructureChecks.Temp, "future.esdp");
        System.IO.File.WriteAllText(future, JsonSerializer.Serialize(first, ProjectFileService.JsonOptions));
        bool rejected = false;
        try { ProjectFileService.LoadPrepared(future); } catch (System.IO.InvalidDataException) { rejected = true; }
        Check(rejected && before == System.IO.File.ReadAllText(file), "Future schema accepted or existing save changed");
        Console.WriteLine("PASS: independent history snapshots survive pruning; atomic save/backup and future-schema rejection");
    }
}
