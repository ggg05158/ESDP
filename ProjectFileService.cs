using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ESDP.Models;

namespace ESDP
{
    internal sealed class ProjectLoadResult
    {
        public required Project Project { get; init; }
        public bool WasMigrated { get; init; }
    }

    /// <summary>
    /// 프로젝트 파일의 직렬화, 형식 마이그레이션, 기본 검증과 원자적 저장을
    /// 담당한다. UI 상태를 변경하지 않으므로 파일을 완전히 준비한 뒤 화면에
    /// 적용할 수 있다.
    /// </summary>
    internal static class ProjectFileService
    {
        private static readonly UTF8Encoding Utf8WithoutBom = new(false);

        internal static JsonSerializerOptions JsonOptions { get; } = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static ProjectLoadResult LoadPrepared(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("프로젝트 파일 경로가 비어 있습니다.", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException("프로젝트 파일을 찾을 수 없습니다.", path);

            Project project;
            using (var stream = new FileStream(
                       path, FileMode.Open, FileAccess.Read, FileShare.Read,
                       bufferSize: 64 * 1024, FileOptions.SequentialScan))
            {
                project = JsonSerializer.Deserialize<Project>(stream, JsonOptions)
                          ?? throw new InvalidDataException("프로젝트 파일 내용이 비어 있습니다.");
            }

            bool migrated = PrepareObjectGraph(project);
            Validate(project);
            return new ProjectLoadResult { Project = project, WasMigrated = migrated };
        }

        public static void SaveAtomic(string path, Project project)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("프로젝트 파일 경로가 비어 있습니다.", nameof(path));

            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath)
                               ?? throw new InvalidOperationException("프로젝트 저장 폴더를 확인할 수 없습니다.");
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"프로젝트 저장 폴더가 없습니다: {directory}");

            project.SchemaVersion = Project.CurrentSchemaVersion;
            Validate(project);

            string temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            string backupPath = fullPath + ".bak";

            try
            {
                using (var stream = new FileStream(
                           temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           bufferSize: 64 * 1024,
                           FileOptions.SequentialScan | FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, Utf8WithoutBom, 64 * 1024, leaveOpen: true))
                {
                    string json = JsonSerializer.Serialize(project, JsonOptions);
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                // 최종 파일을 교체하기 전에 방금 기록한 임시 파일을 실제 로더로
                // 다시 읽는다. 잘못된 JSON이나 호환되지 않는 모델은 원본을 건드리지 않는다.
                ProjectLoadResult verification = LoadPrepared(temporaryPath);
                if (verification.Project.SchemaVersion != Project.CurrentSchemaVersion)
                    throw new InvalidDataException("저장 파일 형식 버전을 검증하지 못했습니다.");

                if (File.Exists(fullPath))
                    File.Replace(temporaryPath, fullPath, backupPath, ignoreMetadataErrors: true);
                else
                    File.Move(temporaryPath, fullPath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static bool PrepareObjectGraph(Project project)
        {
            if (project.SchemaVersion < 0)
                throw new InvalidDataException("프로젝트 형식 버전이 올바르지 않습니다.");
            if (project.SchemaVersion > Project.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"이 프로젝트는 더 새로운 ESDP 형식({project.SchemaVersion})입니다. " +
                    $"현재 프로그램이 지원하는 형식은 {Project.CurrentSchemaVersion}까지입니다.");
            }

            bool changed = false;
            if (project.SchemaVersion == 0)
            {
                project.SchemaVersion = 1;
                changed = true;
            }

            changed |= EnsureTopLevelCollections(project);

            // 시트 개념이 없던 형식은 기존 배치 데이터를 첫 시트로 옮긴다.
            if (project.Sheets.Count == 0)
            {
                project.Sheets.Add(new Sheet
                {
                    Name = "Sheet1",
                    PaperSize = project.PaperSize,
                    Zones = project.Zones,
                    DeviceInstances = project.DeviceInstances,
                    CableInstances = project.CableInstances,
                    Connections = project.Connections,
                    Shapes = project.Shapes
                });
                changed = true;
            }

            foreach (Sheet sheet in project.Sheets)
                changed |= EnsureSheetCollections(sheet);
            foreach (Device device in project.Devices)
                device.Ports ??= new List<Port>();
            foreach (Cable cable in project.Cables)
            {
                cable.Connectors ??= new List<CableConnector>();
                cable.SignalRows ??= new List<CableSignalRow>();
                foreach (CableConnector connector in cable.Connectors)
                    connector.Pins ??= new List<CablePin>();
            }

            return changed;
        }

        private static bool EnsureTopLevelCollections(Project project)
        {
            bool changed = false;
            if (project.Devices == null) { project.Devices = new(); changed = true; }
            if (project.Cables == null) { project.Cables = new(); changed = true; }
            if (project.CustomDeviceKinds == null) { project.CustomDeviceKinds = new(); changed = true; }
            if (project.DeviceCategoryColors == null) { project.DeviceCategoryColors = new(); changed = true; }
            if (project.CustomCableCategories == null) { project.CustomCableCategories = new(); changed = true; }
            if (project.Sheets == null) { project.Sheets = new(); changed = true; }
            if (project.CableTypeColors == null) { project.CableTypeColors = new(); changed = true; }
            if (project.Zones == null) { project.Zones = new(); changed = true; }
            if (project.DeviceInstances == null) { project.DeviceInstances = new(); changed = true; }
            if (project.CableInstances == null) { project.CableInstances = new(); changed = true; }
            if (project.Connections == null) { project.Connections = new(); changed = true; }
            if (project.Shapes == null) { project.Shapes = new(); changed = true; }
            project.Name ??= "새 프로젝트";
            return changed;
        }

        private static bool EnsureSheetCollections(Sheet sheet)
        {
            if (sheet == null)
                throw new InvalidDataException("프로젝트에 비어 있는 시트 정보가 있습니다.");

            bool changed = false;
            if (sheet.Zones == null) { sheet.Zones = new(); changed = true; }
            if (sheet.DeviceInstances == null) { sheet.DeviceInstances = new(); changed = true; }
            if (sheet.CableInstances == null) { sheet.CableInstances = new(); changed = true; }
            if (sheet.Connections == null) { sheet.Connections = new(); changed = true; }
            if (sheet.Shapes == null) { sheet.Shapes = new(); changed = true; }
            sheet.Name ??= "Sheet";

            foreach (Zone zone in sheet.Zones)
                zone.MemberDeviceInstanceIds ??= new List<string>();
            foreach (CableInstance cable in sheet.CableInstances)
            {
                cable.PortA ??= new CablePortLayout();
                cable.PortB ??= new CablePortLayout();
                cable.RootBends ??= new List<Models.Point>();
                cable.Branches ??= new List<CableBranch>();
                foreach (CableBranch branch in cable.Branches)
                {
                    branch.Port ??= new CablePortLayout();
                    branch.Bends ??= new List<Models.Point>();
                }
            }
            return changed;
        }

        private static void Validate(Project project)
        {
            if (project.SchemaVersion != Project.CurrentSchemaVersion)
                throw new InvalidDataException(
                    $"지원되지 않는 프로젝트 형식 버전입니다: {project.SchemaVersion}");
            if (project.Sheets.Count == 0)
                throw new InvalidDataException("프로젝트에 시트가 없습니다.");

            EnsureUniqueIds(project.Devices.Select(item => item.Id), "장치");
            EnsureUniqueIds(project.Cables.Select(item => item.Id), "케이블");
            EnsureUniqueIds(project.Sheets.Select(item => item.Id), "시트");

            foreach (Sheet sheet in project.Sheets)
            {
                EnsureUniqueIds(sheet.DeviceInstances.Select(item => item.Id),
                    $"'{sheet.Name}' 시트의 장치 개체");
                EnsureUniqueIds(sheet.CableInstances.Select(item => item.Id),
                    $"'{sheet.Name}' 시트의 케이블 개체");

                foreach (DeviceInstance device in sheet.DeviceInstances)
                {
                    EnsureFinite(device.X, "장치 X 좌표");
                    EnsureFinite(device.Y, "장치 Y 좌표");
                    EnsureFinite(device.Width, "장치 너비");
                    EnsureFinite(device.Height, "장치 높이");
                }
                foreach (Zone zone in sheet.Zones)
                {
                    EnsureFinite(zone.X, "구역 X 좌표");
                    EnsureFinite(zone.Y, "구역 Y 좌표");
                    EnsureFinite(zone.Width, "구역 너비");
                    EnsureFinite(zone.Height, "구역 높이");
                }
                foreach (CanvasShape shape in sheet.Shapes)
                {
                    EnsureFinite(shape.X, "도형 X 좌표");
                    EnsureFinite(shape.Y, "도형 Y 좌표");
                    EnsureFinite(shape.Width, "도형 너비");
                    EnsureFinite(shape.Height, "도형 높이");
                    EnsureFinite(shape.RotationAngle, "도형 회전");
                    EnsureFinite(shape.TextMargin, "도형 글자 여백");
                    EnsureFinite(shape.FontSize, "도형 글자 크기");
                    if (shape.Adjustment1 is { } first) EnsureFinite(first, "도형 모양 조절 1");
                    if (shape.Adjustment2 is { } second) EnsureFinite(second, "도형 모양 조절 2");
                    shape.CustomVertices ??= new();
                    shape.GroupId ??= "";
                    if (shape.CustomVertices.Count > 1000) throw new InvalidDataException("도형의 꼭짓점이 너무 많습니다.");
                    foreach (var vertex in shape.CustomVertices)
                    {
                        EnsureFinite(vertex.X, "도형 꼭짓점 X"); EnsureFinite(vertex.Y, "도형 꼭짓점 Y");
                        if (vertex.X < 0 || vertex.X > 1 || vertex.Y < 0 || vertex.Y > 1)
                            throw new InvalidDataException("도형 꼭짓점은 도형 영역 안에 있어야 합니다.");
                    }
                }
                foreach (CableInstance cable in sheet.CableInstances)
                    ValidateCableGeometry(cable);
            }
        }

        private static void ValidateCableGeometry(CableInstance cable)
        {
            ValidatePort(cable.PortA);
            ValidatePort(cable.PortB);
            foreach (Models.Point point in cable.RootBends)
            {
                EnsureFinite(point.X, "케이블 꺾임점 X 좌표");
                EnsureFinite(point.Y, "케이블 꺾임점 Y 좌표");
            }
            foreach (CableBranch branch in cable.Branches)
            {
                EnsureFinite(branch.T, "케이블 분기 위치");
                ValidatePort(branch.Port);
                foreach (Models.Point point in branch.Bends)
                {
                    EnsureFinite(point.X, "케이블 분기 꺾임점 X 좌표");
                    EnsureFinite(point.Y, "케이블 분기 꺾임점 Y 좌표");
                }
            }
        }

        private static void ValidatePort(CablePortLayout port)
        {
            EnsureFinite(port.PortX, "케이블 포트 X 좌표");
            EnsureFinite(port.PortY, "케이블 포트 Y 좌표");
            EnsureFinite(port.LabelDX, "케이블 포트명 X 위치");
            EnsureFinite(port.LabelDY, "케이블 포트명 Y 위치");
        }

        private static void EnsureUniqueIds(IEnumerable<string> ids, string itemName)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string? id in ids)
            {
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidDataException($"{itemName}에 비어 있는 ID가 있습니다.");
                if (!seen.Add(id))
                    throw new InvalidDataException($"{itemName} ID가 중복되었습니다: {id}");
            }
        }

        private static void EnsureFinite(double value, string name)
        {
            if (!double.IsFinite(value))
                throw new InvalidDataException($"{name}가 올바른 숫자가 아닙니다.");
        }
    }
}
