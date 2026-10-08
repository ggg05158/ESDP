using System.Collections.Generic;

namespace ESDP.Models
{
    public enum DeviceKind { 주장치, 착화류, 시험장치, 점검장비, 기타 }

    public class Device
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public string NameRtfBase64 { get; set; } = "";
        public DeviceKind Kind { get; set; } = DeviceKind.주장치;
        /// <summary>기본 열거형에 없는 사용자가 추가한 장치 범주.</summary>
        public string CustomKindName { get; set; } = "";
        public string CategoryColorHex { get; set; } = "";
        public List<Port> Ports { get; set; } = new();
        public Dictionary<string, string> Features { get; set; } = new();

        public bool NameBold { get; set; } = true;
        public bool NameItalic { get; set; } = false;
        public bool NameUnderline { get; set; } = false;
        public double NameFontSize { get; set; } = 10;
        public string NameColorHex { get; set; } = "#000000";
        public string NameFontFamily { get; set; } = "맑은 고딕";
        public string NameTextAlignment { get; set; } = "Center";
        /// <summary>
        /// 모든 장치에서 사용하는 이름 기준 위치.
        /// 0/0.5/1은 각 변과 중앙 정렬을 뜻하고, 그 사이 값은 자유 드래그 위치다.
        /// 장치 크기가 바뀌어도 위치 비율을 유지하도록 0~1 정규화 좌표로 저장한다.
        /// </summary>
        public bool HasCustomNamePosition { get; set; }
        public double NamePositionX { get; set; } = 0.5;
        public double NamePositionY { get; set; } = 0.5;

        /// <summary>
        /// 기타 장치가 사용하는 개별 배경색. 기존 장치 종류는 AppSettings의
        /// 종류별 공통색을 사용하므로 이 값의 영향을 받지 않는다.
        /// </summary>
        public string BackgroundColorHex { get; set; } = "#FFFFFF";

        /// <summary>프로젝트 파일에 직접 포함되는 장치 배경 이미지.</summary>
        public string BackgroundImageBase64 { get; set; } = "";
        public string BackgroundImageFileName { get; set; } = "";
        /// <summary>Uniform / UniformToFill / Fill</summary>
        public string BackgroundImageStretch { get; set; } = "Uniform";
        /// <summary>
        /// 배경 이미지 표시 방식을 사용자가 직접 선택했는지 여부.
        /// 이 값이 없는 구버전 프로젝트는 당시 기본값이었던 UniformToFill 대신
        /// 사진 전체가 보이는 Uniform으로 안전하게 마이그레이션한다.
        /// </summary>
        public bool HasExplicitBackgroundImageStretch { get; set; }
        public double BackgroundImageOpacity { get; set; } = 1;
    }

    public class Port
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Designator { get; set; } = "";
        public PortSide Side { get; set; } = PortSide.Left;

        /// <summary>엑셀의 conn_type에 대응하는 커넥터 규격.</summary>
        public string ConnType { get; set; } = "";

        /// <summary>선택한 커넥터 카탈로그의 내부 키.</summary>
        public string ConnectorCatalogKey { get; set; } = "";

        /// <summary>포트 용도나 주의사항을 적는 사용자 메모.</summary>
        public string Description { get; set; } = "";

        /// <summary>도면에 커넥터 규격을 함께 표시할지 여부(기본 숨김).</summary>
        public bool ShowConnType { get; set; }

        /// <summary>도면에 포트 이름을 표시할지 여부. 구버전 프로젝트는 기본 표시.</summary>
        public bool ShowLabel { get; set; } = true;

        /// <summary>이 포트 이름표만의 글자 크기와 색상.</summary>
        public double LabelFontSize { get; set; } = 11;
        public string LabelColorHex { get; set; } = "#7F7F7F";

        /// <summary>포트 접점을 기준으로 사용자가 옮긴 이름표 위치.</summary>
        public bool HasCustomLabelPosition { get; set; }
        public double LabelOffsetX { get; set; }
        public double LabelOffsetY { get; set; }

        /// <summary>
        /// 그 변을 따라 어느 지점에 놓일지 (0.0=시작쪽 끝, 1.0=반대쪽 끝).
        /// -1 이면 "자동": 같은 변의 포트 개수만큼 고르게 나눈 위치를 쓴다.
        /// 상세설정 창에서 슬라이더로 조절하면 구체적인 값이 들어간다.
        /// </summary>
        public double Position { get; set; } = -1;
    }

    public enum PortSide { Left, Right, Top, Bottom }
}
