using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using ClosedXML.Excel;
using ESDP.Models;

namespace ESDP
{
    internal static class ExcelInterchangeService
    {
        private const int CableFirstDataRow = 10;
        private const int CableTemplateLastRow = 159;
        private const int CableFirstPortColumn = 4;
        private const int CablePortStride = 5;
        private const double TableGapColumnWidth = 2.0;

        private interface IImportSheet
        {
            string Name { get; }
            int LastRow { get; }
            int LastColumn { get; }
            string Text(int row, int column);
        }

        private sealed class ClosedXmlImportSheet(IXLWorksheet sheet) : IImportSheet
        {
            public string Name => sheet.Name;
            public int LastRow => sheet.LastRowUsed()?.RowNumber() ?? 0;
            public int LastColumn => sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
            public string Text(int row, int column)
                => sheet.Cell(row, column).GetFormattedString().Trim();
        }

        private sealed class BrowserImportSheet(BrowserExcelWorksheet sheet) : IImportSheet
        {
            public string Name => sheet.Name;
            public int LastRow => sheet.LastRow;
            public int LastColumn => sheet.LastColumn;
            public string Text(int row, int column) => sheet.CellText(row, column);
        }

        public static void ExportCables(string path, IEnumerable<Cable> source)
        {
            using var workbook = LoadEmbedded("ESDP.Resources.CableListTemplate.xlsx");
            var template = workbook.Worksheet("template");
            var cables = source.ToList();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "template" };
            var sheetNames = cables
                .Select(cable => MakeSheetName(cable.Name, usedNames))
                .ToList();

            if (cables.Count == 0)
            {
                template.Name = "cable_list";
                template.Visibility = XLWorksheetVisibility.Visible;
                template.Cell(2, 2).Value = "등록된 케이블이 없습니다.";
            }
            else
            {
                // 템플릿 시트를 복사한 뒤 삭제하면 Excel이 워크시트 XML을
                // 복구하는 파일이 만들어질 수 있다. 빈 템플릿 상태에서 필요한
                // 복제본만 먼저 만든 후 원본 템플릿을 첫 결과 시트로 사용한다.
                var outputSheets = new List<IXLWorksheet> { template };
                for (int index = 1; index < cables.Count; index++)
                    outputSheets.Add(template.CopyTo(sheetNames[index]));

                template.Name = sheetNames[0];
                for (int index = 0; index < cables.Count; index++)
                {
                    outputSheets[index].Visibility = XLWorksheetVisibility.Visible;
                    WriteCableSheet(outputSheets[index], cables[index]);
                }
            }

            workbook.SaveAs(path);
        }

        public static List<Cable> ImportCables(string path)
        {
            using var workbook = new XLWorkbook(path);
            return ImportCables(workbook.Worksheets
                .Select(sheet => (IImportSheet)new ClosedXmlImportSheet(sheet)));
        }

        public static List<Cable> ImportCables(BrowserExcelWorkbook workbook)
            => ImportCables(workbook.Sheets
                .Select(sheet => (IImportSheet)new BrowserImportSheet(sheet)));

        private static List<Cable> ImportCables(IEnumerable<IImportSheet> sourceSheets)
        {
            var result = new List<Cable>();
            foreach (var sheet in sourceSheets.Where(s =>
                         !s.Name.Equals("template", StringComparison.OrdinalIgnoreCase) &&
                         !s.Name.Equals("cable_list", StringComparison.OrdinalIgnoreCase)))
            {
                int portCount = ReadInt(sheet.Text(2, 3));
                if (portCount <= 0)
                {
                    int lastColumn = Math.Max(CableFirstPortColumn,
                        Math.Max(sheet.LastColumn, CableFirstPortColumn));
                    portCount = Enumerable.Range(0, 100)
                        .TakeWhile(index => CableFirstPortColumn + index * CablePortStride <= lastColumn)
                        .Count(index => !string.IsNullOrWhiteSpace(
                            sheet.Text(9, CableFirstPortColumn + index * CablePortStride)));
                }
                portCount = Math.Max(2, portCount);

                string name = sheet.Text(2, 2);
                if (string.IsNullOrWhiteSpace(name)) name = sheet.Name;
                var cable = new Cable
                {
                    Name = name,
                    OverallBraid = ReadBool(sheet.Text(2, 4), true),
                    Tube = ReadBool(sheet.Text(2, 5), true)
                };

                for (int index = 0; index < portCount; index++)
                {
                    int column = CableFirstPortColumn + index * CablePortStride;
                    string portName = ReadPortValue(sheet, 6, column);
                    if (string.IsNullOrWhiteSpace(portName)) portName = $"P{index + 1}";
                    cable.Connectors.Add(new CableConnector
                    {
                        PortName = portName,
                        ConnType = ReadPortValue(sheet, 8, column)
                    });
                    if (index == 0)
                        cable.Category = ParseCableCategory(ReadPortValue(sheet, 7, column));
                }

                int explicitCount = ReadInt(sheet.Text(2, 1));
                int lastDataRow = explicitCount > 0
                    ? CableFirstDataRow + explicitCount - 1
                    : FindLastCableDataRow(sheet, portCount);
                CableSignalRow? currentParent = null;
                for (int row = CableFirstDataRow; row <= lastDataRow; row++)
                {
                    string pinName = sheet.Text(row, 2);
                    string wireType = sheet.Text(row, 3);
                    bool hasEndpointData = false;
                    var signal = new CableSignalRow
                    {
                        PinName = pinName,
                        WireType = wireType
                    };
                    for (int index = 0; index < cable.Connectors.Count; index++)
                    {
                        int column = CableFirstPortColumn + index * CablePortStride;
                        var pin = new CablePin
                        {
                            PinNum = sheet.Text(row, column),
                            TwpShield = sheet.Text(row, column + 1),
                            InConn = sheet.Text(row, column + 2),
                            PinType = sheet.Text(row, column + 3)
                        };
                        hasEndpointData |= !IsPinEmpty(pin);
                        signal.PortPins[cable.Connectors[index].Id] = pin;
                    }

                    if (string.IsNullOrWhiteSpace(pinName) &&
                        string.IsNullOrWhiteSpace(wireType) && hasEndpointData && currentParent != null)
                        signal.ParentSignalId = currentParent.Id;
                    else
                        currentParent = signal;
                    cable.SignalRows.Add(signal);
                }
                result.Add(cable);
            }
            return result;
        }

        public static void ExportDevices(string path, IEnumerable<Device> source)
        {
            using var workbook = LoadEmbedded("ESDP.Resources.DeviceListTemplate.xlsx");
            var output = workbook.Worksheet("template");
            // 단일 장치 목록은 템플릿을 복사/삭제하지 않고 같은 시트를 직접
            // 결과 시트로 전환해 Excel 호환 메타데이터를 그대로 유지한다.
            output.Name = "dev_list";
            output.Visibility = XLWorksheetVisibility.Visible;
            WriteDeviceSheet(output, source.ToList());
            workbook.SaveAs(path);
        }

        public static List<Device> ImportDevices(string path)
        {
            using var workbook = new XLWorkbook(path);
            return ImportDevices(workbook.Worksheets
                .Select(sheet => (IImportSheet)new ClosedXmlImportSheet(sheet)));
        }

        public static List<Device> ImportDevices(BrowserExcelWorkbook workbook)
            => ImportDevices(workbook.Sheets
                .Select(sheet => (IImportSheet)new BrowserImportSheet(sheet)));

        private static List<Device> ImportDevices(IEnumerable<IImportSheet> sourceSheets)
        {
            var sheets = sourceSheets.ToList();
            var sheet = sheets.FirstOrDefault(s =>
                            s.Name.Equals("dev_list", StringComparison.OrdinalIgnoreCase))
                        ?? sheets.FirstOrDefault(s =>
                            !s.Name.Equals("template", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("장치 목록 시트를 찾을 수 없습니다.");
            int lastColumn = sheet.LastColumn;
            var result = new List<Device>();
            for (int column = 2; column <= lastColumn; column += 3)
            {
                string name = sheet.Text(3, column + 1);
                int portCount = ReadInt(sheet.Text(5, column + 1));
                if (string.IsNullOrWhiteSpace(name) && portCount <= 0) continue;
                var device = new Device
                {
                    Name = string.IsNullOrWhiteSpace(name) ? $"장치 {result.Count + 1}" : name,
                    Kind = ParseDeviceKind(sheet.Text(4, column + 1))
                };
                for (int index = 0; index < portCount; index++)
                {
                    string designator = sheet.Text(7 + index, column);
                    if (string.IsNullOrWhiteSpace(designator)) continue;
                    device.Ports.Add(new Port
                    {
                        Designator = designator,
                        ConnType = sheet.Text(7 + index, column + 1),
                        Side = index % 2 == 0 ? PortSide.Left : PortSide.Right,
                        Position = -1
                    });
                }
                result.Add(device);
            }
            return result;
        }

        private static XLWorkbook LoadEmbedded(string name)
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"내장 Excel 양식을 찾을 수 없습니다: {name}");
            var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.Position = 0;
            // ClosedXML은 일부 워크시트 내용을 지연 로딩하므로 워크북 수명 동안
            // 메모리 스트림을 닫지 않는다. 워크북이 해제될 때 함께 회수된다.
            return new XLWorkbook(memory);
        }

        private static void WriteCableSheet(IXLWorksheet sheet, Cable cable)
        {
            int rowCount = Math.Max(150, cable.SignalRows.Count);
            int lastRow = CableFirstDataRow + rowCount - 1;
            int portBlockCount = Math.Max(2, cable.Connectors.Count);
            int lastColumn = CableFirstPortColumn + (portBlockCount - 1) * CablePortStride + 3;

            for (int index = 2; index < cable.Connectors.Count; index++)
            {
                int targetColumn = CableFirstPortColumn + index * CablePortStride;
                sheet.Range(5, CableFirstPortColumn, CableTemplateLastRow, CableFirstPortColumn + 3)
                    .CopyTo(sheet.Cell(5, targetColumn));
            }
            ApplyCableTableColumnWidths(sheet, portBlockCount);
            if (lastRow > CableTemplateLastRow)
                sheet.Range(CableTemplateLastRow, 1, CableTemplateLastRow, lastColumn)
                    .CopyTo(sheet.Range(CableTemplateLastRow + 1, 1, lastRow, lastColumn));

            sheet.Cell(2, 1).Value = cable.SignalRows.Count;
            sheet.Cell(2, 1).Style.NumberFormat.Format = ";;;";
            sheet.Cell(2, 2).Value = cable.Name;
            sheet.Cell(2, 3).Value = cable.Connectors.Count;
            sheet.Cell(2, 4).Value = cable.OverallBraid ? 1 : 0;
            sheet.Cell(2, 5).Value = cable.Tube ? 1 : 0;
            sheet.Cell(9, 2).Value = "pin_name";
            sheet.Cell(9, 3).Value = "wire_type";

            for (int index = 0; index < cable.Connectors.Count; index++)
            {
                int column = CableFirstPortColumn + index * CablePortStride;
                WritePortMetadata(sheet, column, 5, "port_order", (index + 1).ToString(CultureInfo.InvariantCulture));
                WritePortMetadata(sheet, column, 6, "port_name", cable.Connectors[index].PortName);
                WritePortMetadata(sheet, column, 7, "cable_type", CableCategoryText(cable.Category));
                WritePortMetadata(sheet, column, 8, "conn_type", cable.Connectors[index].ConnType);
                sheet.Cell(9, column).Value = "pin_num";
                sheet.Cell(9, column + 1).Value = "twp_shd";
                sheet.Cell(9, column + 2).Value = "in_conn";
                sheet.Cell(9, column + 3).Value = "gauge";
            }

            sheet.Range(CableFirstDataRow, 1, lastRow, lastColumn).Clear(XLClearOptions.Contents);
            for (int index = 0; index < cable.SignalRows.Count; index++)
            {
                int row = CableFirstDataRow + index;
                var signal = cable.SignalRows[index];
                sheet.Cell(row, 1).Value = index + 1;
                if (string.IsNullOrWhiteSpace(signal.ParentSignalId))
                {
                    SetText(sheet.Cell(row, 2), signal.PinName);
                    SetText(sheet.Cell(row, 3), signal.WireType);
                }
                for (int portIndex = 0; portIndex < cable.Connectors.Count; portIndex++)
                {
                    int column = CableFirstPortColumn + portIndex * CablePortStride;
                    if (!signal.PortPins.TryGetValue(cable.Connectors[portIndex].Id, out var pin))
                        continue;
                    SetText(sheet.Cell(row, column), pin.PinNum);
                    SetText(sheet.Cell(row, column + 1), pin.TwpShield);
                    SetText(sheet.Cell(row, column + 2), pin.InConn);
                    SetText(sheet.Cell(row, column + 3), pin.PinType);
                }
            }
        }

        private static void WriteDeviceSheet(IXLWorksheet sheet, IReadOnlyList<Device> devices)
        {
            int maxPorts = Math.Max(30, devices.Count == 0 ? 0 : devices.Max(d => d.Ports.Count));
            int lastRow = 6 + maxPorts;
            for (int index = 2; index < devices.Count; index++)
            {
                int targetColumn = 2 + index * 3;
                sheet.Range(2, 2, Math.Max(36, lastRow), 3).CopyTo(sheet.Cell(2, targetColumn));
            }
            ApplyDeviceTableColumnWidths(sheet, Math.Max(3, devices.Count));
            for (int index = 0; index < devices.Count; index++)
            {
                int column = 2 + index * 3;
                var device = devices[index];
                sheet.Cell(2, column).Value = "dev_id";
                sheet.Cell(2, column + 1).Value = index + 1;
                sheet.Cell(3, column).Value = "name";
                sheet.Cell(3, column + 1).Value = device.Name;
                sheet.Cell(4, column).Value = "dev_type";
                sheet.Cell(4, column + 1).Value = DeviceKindText(device.Kind);
                sheet.Cell(5, column).Value = "tot_port";
                sheet.Cell(5, column + 1).Value = device.Ports.Count;
                sheet.Cell(6, column).Value = "port";
                sheet.Cell(6, column + 1).Value = "port_info";
                sheet.Range(7, column, lastRow, column + 1).Clear(XLClearOptions.Contents);
                for (int portIndex = 0; portIndex < device.Ports.Count; portIndex++)
                {
                    sheet.Cell(7 + portIndex, 1).Value = portIndex + 1;
                    SetText(sheet.Cell(7 + portIndex, column), device.Ports[portIndex].Designator);
                    SetText(sheet.Cell(7 + portIndex, column + 1), device.Ports[portIndex].ConnType);
                }
            }
        }

        private static void ApplyCableTableColumnWidths(IXLWorksheet sheet, int blockCount)
        {
            double[] blockWidths = Enumerable.Range(0, 4)
                .Select(offset => sheet.Column(CableFirstPortColumn + offset).Width)
                .ToArray();

            for (int index = 0; index < blockCount; index++)
            {
                int column = CableFirstPortColumn + index * CablePortStride;
                for (int offset = 0; offset < blockWidths.Length; offset++)
                    sheet.Column(column + offset).Width = blockWidths[offset];
                if (index < blockCount - 1)
                    sheet.Column(column + 4).Width = TableGapColumnWidth;
            }
        }

        private static void ApplyDeviceTableColumnWidths(IXLWorksheet sheet, int blockCount)
        {
            double portWidth = sheet.Column(2).Width;
            double infoWidth = sheet.Column(3).Width;
            for (int index = 0; index < blockCount; index++)
            {
                int column = 2 + index * 3;
                sheet.Column(column).Width = portWidth;
                sheet.Column(column + 1).Width = infoWidth;
                if (index < blockCount - 1)
                    sheet.Column(column + 2).Width = TableGapColumnWidth;
            }
        }

        private static void WritePortMetadata(IXLWorksheet sheet, int column, int row, string label, string value)
        {
            sheet.Cell(row, column).Value = label;
            var valueRange = sheet.Range(row, column + 1, row, column + 3);
            if (!valueRange.IsMerged()) valueRange.Merge();
            SetText(valueRange.FirstCell(), value);
        }

        private static void SetText(IXLCell cell, string? value)
        {
            if (string.IsNullOrEmpty(value))
                cell.Clear(XLClearOptions.Contents);
            else
                cell.Value = value;
        }

        private static string ReadPortValue(IImportSheet sheet, int row, int column)
        {
            for (int offset = 1; offset <= 3; offset++)
            {
                string value = sheet.Text(row, column + offset);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return "";
        }

        private static int FindLastCableDataRow(IImportSheet sheet, int portCount)
        {
            int last = Math.Max(sheet.LastRow, CableFirstDataRow - 1);
            for (int row = last; row >= CableFirstDataRow; row--)
            {
                if (!string.IsNullOrWhiteSpace(sheet.Text(row, 2)) ||
                    !string.IsNullOrWhiteSpace(sheet.Text(row, 3))) return row;
                for (int index = 0; index < portCount; index++)
                {
                    int column = CableFirstPortColumn + index * CablePortStride;
                    if (Enumerable.Range(0, 4).Any(offset =>
                        !string.IsNullOrWhiteSpace(sheet.Text(row, column + offset))))
                        return row;
                }
            }
            return CableFirstDataRow - 1;
        }

        private static int ReadInt(string text)
            => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value : 0;
        private static bool ReadBool(string value, bool fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value == "예";
        }
        private static bool IsPinEmpty(CablePin pin)
            => string.IsNullOrWhiteSpace(pin.PinNum) && string.IsNullOrWhiteSpace(pin.PinName) &&
               string.IsNullOrWhiteSpace(pin.PinType) && string.IsNullOrWhiteSpace(pin.InConn) &&
               string.IsNullOrWhiteSpace(pin.TwpShield);

        private static CableCategory ParseCableCategory(string value)
            => value.Contains("점검", StringComparison.OrdinalIgnoreCase) ? CableCategory.점검케이블
             : value.Contains("탄내", StringComparison.OrdinalIgnoreCase) ? CableCategory.탄내케이블
             : CableCategory.기타케이블;
        private static string CableCategoryText(CableCategory category) => category.ToString();

        private static DeviceKind ParseDeviceKind(string value)
        {
            if (value.Contains("착화", StringComparison.OrdinalIgnoreCase)) return DeviceKind.착화류;
            if (value.Contains("시험", StringComparison.OrdinalIgnoreCase)) return DeviceKind.시험장치;
            if (value.Contains("점검", StringComparison.OrdinalIgnoreCase)) return DeviceKind.점검장비;
            if (value.Contains("기타", StringComparison.OrdinalIgnoreCase)) return DeviceKind.기타;
            return DeviceKind.주장치;
        }
        private static string DeviceKindText(DeviceKind kind) => kind switch
        {
            DeviceKind.주장치 => "주장비",
            DeviceKind.시험장치 => "시험장비",
            _ => kind.ToString()
        };

        private static string MakeSheetName(string raw, ISet<string> used)
        {
            string name = new string((raw ?? "케이블").Select(ch =>
                "[]:*?/\\".Contains(ch) ? '_' : ch).ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "케이블";
            if (name.Length > 31) name = name[..31];
            string baseName = name;
            for (int number = 2; !used.Add(name); number++)
            {
                string suffix = $" ({number})";
                name = baseName[..Math.Min(baseName.Length, 31 - suffix.Length)] + suffix;
            }
            return name;
        }
    }
}
