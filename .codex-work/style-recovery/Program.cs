using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;

if (args.Length != 3)
    throw new ArgumentException("Usage: StyleRecovery <source> <destination> <sheet1|sheet2|...>");

var source = Path.GetFullPath(args[0]);
var destination = Path.GetFullPath(args[1]);
var keepNames = args[2]
    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToHashSet(StringComparer.Ordinal);

Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
File.Copy(source, destination, overwrite: true);

using var document = SpreadsheetDocument.Open(destination, true);
var workbookPart = document.WorkbookPart
    ?? throw new InvalidDataException("Workbook part is missing.");
var workbook = workbookPart.Workbook;
var sheets = workbook.Sheets?.Elements<Sheet>().ToList()
    ?? throw new InvalidDataException("Sheets collection is missing.");

if (!keepNames.IsSubsetOf(sheets.Select(s => s.Name?.Value ?? string.Empty)))
    throw new InvalidDataException("One or more requested sheets do not exist in the source workbook.");

foreach (var sheet in sheets)
{
    var name = sheet.Name?.Value ?? string.Empty;
    if (keepNames.Contains(name))
    {
        sheet.State = SheetStateValues.Visible;
        continue;
    }

    if (sheet.Id?.Value is string relationshipId)
    {
        var worksheetPart = workbookPart.GetPartById(relationshipId);
        sheet.Remove();
        workbookPart.DeletePart(worksheetPart);
    }
}

foreach (var view in workbook.BookViews?.Elements<WorkbookView>() ?? Enumerable.Empty<WorkbookView>())
    view.ActiveTab = 0U;

workbook.Save();

var errors = new OpenXmlValidator().Validate(document).ToList();
if (errors.Count > 0)
    throw new InvalidDataException(string.Join(Environment.NewLine, errors.Select(error => error.Description)));

Console.WriteLine($"Recovered {Path.GetFileName(destination)}: {string.Join(", ", keepNames)}; Open XML errors: 0");
