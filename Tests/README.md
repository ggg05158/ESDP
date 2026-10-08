# ESDP regression checks

Run on Windows with the .NET 8 SDK, from the ESDP project folder:

```powershell
dotnet run --project .\Tests\ESDP.RegressionTests\ESDP.RegressionTests.csproj
```

No new test-framework NuGet packages are required. Existing application packages must be available in the offline cache. The harness creates a unique temporary fixture folder and does not modify personal connector data or existing project files. Its folder is printed on success and retained for inspection.

To also check an existing project (read-only):

```powershell
dotnet run --project .\Tests\ESDP.RegressionTests\ESDP.RegressionTests.csproj -- --project C:\work\sample.esdp
```

Checks cover crossing/rectangle algorithms, drag render coalescing, label reuse, render geometry preservation, independent Undo snapshots, atomic saves/backups, connector-library merge/recovery and invalid archives, PDF memory limits, local HTML importer request validation, and update-folder parsing. PDF visual fidelity, mouse interactions, and theme appearance still require manual checks; this harness does not generate a PDF.

MainWindow is split into History (Undo/Redo), PdfExport, and CableLabels partial files. This is a source-organization refactor, not complete separation of the canvas state from UI logic. Serialized compatibility fields are intentionally retained.
