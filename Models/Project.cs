using System.Collections.Generic;

namespace ESDP.Models
{
    /// <summary>
    /// 하나의 도면(시트). 엑셀의 시트처럼, 한 프로젝트 안에 여러 장을 둘 수 있다.
    /// 등록된 장치/케이블 "템플릿"은 프로젝트 전체가 공유하고, 시트는 배치 상태만 따로 갖는다.
    /// </summary>
    public class Sheet
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = "Sheet1";

        /// <summary>이 시트의 용지 크기</summary>
        public PaperSize PaperSize { get; set; } = PaperSize.A3_Landscape;

        /// <summary>
        /// 내용이 기본 용지를 넘을 때 용지를 가로/세로로 따로 늘리지 않고
        /// 선택한 용지 비율 그대로 한 장처럼 확장할지 여부.
        /// </summary>
        public bool KeepPaperAspectRatio { get; set; }

        /// <summary>
        /// '비율 한 장 맞춤'으로 확정한 한 페이지의 배율. 이후 내용이 이 경계를
        /// 넘으면 같은 크기의 페이지가 별도로 추가된다.
        /// </summary>
        public double PageScale { get; set; } = 1;

        /// <summary>
        /// 사용자가 지정한 최소 페이지 배열. 실제 내용이 이 영역을 넘으면 기존처럼
        /// 오른쪽/아래쪽으로 페이지가 더 추가될 수 있다.
        /// </summary>
        public int TargetPageColumns { get; set; } = 1;
        public int TargetPageRows { get; set; } = 1;

        /// <summary>왼쪽/위쪽에 임시로 추가된 페이지 수.</summary>
        public int LeadingPageColumns { get; set; }
        public int LeadingPageRows { get; set; }

        public List<Zone> Zones { get; set; } = new();
        public List<DeviceInstance> DeviceInstances { get; set; } = new();
        public List<CableInstance> CableInstances { get; set; } = new();
        public List<Connection> Connections { get; set; } = new();

        /// <summary>장치/케이블과 별개로 자유롭게 그리는 기본 도형들(사각형/원/선/텍스트)</summary>
        public List<CanvasShape> Shapes { get; set; } = new();
    }

    /// <summary>
    /// 장치·케이블과 무관하게 캔버스에 자유롭게 그리는 기본 도형 하나.
    /// Visio/파워포인트의 기본 도형 개념.
    /// </summary>
    public class CanvasShape
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();

        /// <summary>"Rectangle" / "Ellipse" / "Line" / "Text" / "Image"</summary>
        public string Kind { get; set; } = "Rectangle";

        /// <summary>좌상단 좌표(Line 은 시작점)</summary>
        public double X { get; set; }
        public double Y { get; set; }

        /// <summary>크기(Line 은 끝점까지의 가로/세로 거리)</summary>
        public double Width { get; set; } = 100;
        public double Height { get; set; } = 60;
        public double RotationAngle { get; set; }
        public bool FlipHorizontal { get; set; }
        public bool FlipVertical { get; set; }
        public string GroupId { get; set; } = "";
        // Null preserves the geometry of projects created before adjustable shapes.
        public double? Adjustment1 { get; set; }
        public double? Adjustment2 { get; set; }
        public string TextVerticalAlignment { get; set; } = "Center";
        public double TextMargin { get; set; } = 4;
        public bool UseShapeTextArea { get; set; }
        public string TextFit { get; set; } = "Clip";
        public List<Point> CustomVertices { get; set; } = new();

        public string FillHex { get; set; } = "#FFFFFF";
        public string StrokeHex { get; set; } = "#000000";
        public double StrokeThickness { get; set; } = 2;
        public double ArrowHeadSize { get; set; } = 18;

        /// <summary>선 도형의 표현 방식: Solid / Dash / Dot / Double.</summary>
        public string LineStyle { get; set; } = "Solid";

        /// <summary>선의 시작과 끝 모양: Square / Round / Arrow.</summary>
        public string StartLineCap { get; set; } = "Square";
        public string EndLineCap { get; set; } = "Square";

        /// <summary>
        /// 꺾인 선의 시작점(X,Y)을 기준으로 한 상대 경로점.
        /// 직선은 Width/Height만 사용하고 꺾인 선만 이 목록을 사용한다.
        /// </summary>
        public List<Point> LinePoints { get; set; } = new();

        /// <summary>
        /// 일반 도형 레이어 안에서의 앞뒤 순서. 값이 클수록 앞에 표시된다.
        /// </summary>
        public int ZOrder { get; set; }

        /// <summary>
        /// true면 장치와 케이블보다 뒤에 있는 배경 도형으로 취급한다.
        /// 도형 메뉴의 "맨 뒤로 보내기" 결과를 저장하기 위한 값이다.
        /// </summary>
        public bool BehindDiagram { get; set; }

        /// <summary>도형 안(또는 텍스트 도형 자체)에 표시할 글자</summary>
        public string Text { get; set; } = "";
        public string TextRtfBase64 { get; set; } = "";
        public double FontSize { get; set; } = 14;
        public string TextColorHex { get; set; } = "#000000";
        public string FontFamily { get; set; } = "맑은 고딕";
        public bool TextBold { get; set; }
        public bool TextItalic { get; set; }
        public bool TextUnderline { get; set; }
        public string TextAlignment { get; set; } = "Center";

        /// <summary>이미지 도형일 때 프로젝트 안에 포함되는 이미지 데이터.</summary>
        public string ImageDataBase64 { get; set; } = "";
        public string ImageFileName { get; set; } = "";
        /// <summary>Uniform / UniformToFill / Fill</summary>
        public string ImageStretch { get; set; } = "Fill";
        public double ImageOpacity { get; set; } = 1;
    }

    /// <summary>
    /// 하나의 체계(프로젝트). 프로그램이 열고 저장하는 파일 단위.
    /// 등록된 장치/케이블 템플릿(라이브러리)과, 여러 장의 시트를 담는다.
    /// </summary>
    public class Project
    {
        /// <summary>현재 프로그램이 저장하는 프로젝트 형식 버전.</summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>
        /// 저장 파일의 형식 버전. 이 속성이 없던 이전 파일은 0으로 읽혀
        /// 단계별 마이그레이션을 거친다.
        /// </summary>
        public int SchemaVersion { get; set; }

        public string Name { get; set; } = "새 프로젝트";

        // ---- 등록된 템플릿(라이브러리) — 모든 시트가 공유 ----
        /// <summary>등록된 장치 템플릿 목록</summary>
        public List<Device> Devices { get; set; } = new();

        /// <summary>등록된 케이블 템플릿 목록</summary>
        public List<Cable> Cables { get; set; } = new();

        /// <summary>이 프로젝트에서 추가한 사용자 장치/케이블 범주.</summary>
        public List<string> CustomDeviceKinds { get; set; } = new();
        // null identifies legacy projects; an empty list means the user deleted all groups.
        public List<string>? DeviceCategoryNames { get; set; }
        public Dictionary<string, string> DeviceCategoryColors { get; set; } = new();
        public List<string> CustomCableCategories { get; set; } = new();

        /// <summary>도면 시트들 (엑셀의 시트 개념)</summary>
        public List<Sheet> Sheets { get; set; } = new();

        // ---- 케이블 종류 → 색상 매핑 ----
        /// <summary>케이블 종류별 색상 (예: "점검케이블" -> "#FF0000")</summary>
        public Dictionary<string, string> CableTypeColors { get; set; } = new();

        // ---- 이전 버전 파일 호환용 (시트 개념이 없던 시절의 저장 형식) ----
        // 불러올 때 Sheets 가 비어있고 아래 값이 있으면 시트 한 장으로 옮겨 담는다.
        public PaperSize PaperSize { get; set; } = PaperSize.A3_Landscape;
        public List<Zone> Zones { get; set; } = new();
        public List<DeviceInstance> DeviceInstances { get; set; } = new();
        public List<CableInstance> CableInstances { get; set; } = new();
        public List<Connection> Connections { get; set; } = new();
        public List<CanvasShape> Shapes { get; set; } = new();
    }
}
