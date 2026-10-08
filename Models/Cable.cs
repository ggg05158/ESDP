using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ESDP.Models
{
    /// <summary>케이블 분류</summary>
    public enum CableCategory { 탄내케이블, 점검케이블, 기타케이블 }

    /// <summary>
    /// 등록된 케이블 "템플릿". 이름과 분류만 가진다.
    /// 실제 포트/분기 구조는 캔버스에 배치된 CableInstance 쪽(트리 구조)에서 관리한다.
    /// </summary>
    public class Cable
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public double NameFontSize { get; set; } = 13;
        public CableCategory Category { get; set; } = CableCategory.기타케이블;
        /// <summary>기본 열거형에 없는 사용자가 추가한 케이블 범주.</summary>
        public string CustomCategoryName { get; set; } = "";

        /// <summary>기타 케이블에서 사용하는 사용자 지정 선 색상.</summary>
        public string LineColorHex { get; set; } = "#000000";

        /// <summary>이 케이블이 가진 모든 포트(끝단)의 "정의"만 모아둔 목록. 이름/부품정보/회선.</summary>
        public List<CableConnector> Connectors { get; set; } = new();
        public bool OverallBraid { get; set; } = true;
        public bool Tube { get; set; } = true;
        public List<CableSignalRow> SignalRows { get; set; } = new();
    }

    /// <summary>
    /// 케이블 포트(끝단) 하나의 정의. 캔버스 위 실제 위치는 CableInstance 트리 쪽에 있고,
    /// 여기는 "이름이 뭐고 커넥터 타입이 뭔지" 같은 정적 정보만 갖는다.
    /// </summary>
    public class CableConnector
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();

        /// <summary>port_name (예: "P1", 나중에 J100 등으로 수정)</summary>
        public string PortName { get; set; } = "";

        /// <summary>conn_type (커넥터 부품, 나중에 입력)</summary>
        public string ConnType { get; set; } = "";

        /// <summary>핀 배열 이미지와 좌표를 찾기 위한 커넥터 카탈로그 내부 키.</summary>
        public string ConnectorCatalogKey { get; set; } = "";

        /// <summary>포트 용도나 주의사항을 적는 사용자 메모.</summary>
        public string Description { get; set; } = "";

        /// <summary>도면에 conn_type을 포트 이름 아래 함께 표시할지 여부.</summary>
        public bool ShowConnType { get; set; }

        /// <summary>도면에 포트/커넥터 이름을 표시할지 여부. 구버전 프로젝트는 기본 표시.</summary>
        public bool ShowLabel { get; set; } = true;

        public double LabelFontSize { get; set; } = 9;
        public string LabelColorHex { get; set; } = "#000000";

        /// <summary>key_angle (나중에 입력)</summary>
        public string KeyAngle { get; set; } = "";

        /// <summary>회선(핀) 목록 — 포트 상세입력 단계에서 채움</summary>
        public List<CablePin> Pins { get; set; } = new();
    }

    /// <summary>회선(핀) 하나. 엑셀 회선표의 한 행.</summary>
    public class CablePin : INotifyPropertyChanged
    {
        private string _pinNum = "";
        private string _pinName = "";
        private string _pinType = "";
        private string _inConn = "";
        private string _twpShield = "";
        private string _markColorHex = "";

        public string PinNum
        {
            get => _pinNum;
            set => SetField(ref _pinNum, value);
        }

        public string PinName
        {
            get => _pinName;
            set => SetField(ref _pinName, value);
        }

        public string PinType
        {
            get => _pinType;
            set => SetField(ref _pinType, value);
        }

        public string InConn
        {
            get => _inConn;
            set => SetField(ref _inConn, value);
        }

        public string TwpShield
        {
            get => _twpShield;
            set => SetField(ref _twpShield, value);
        }

        /// <summary>프로젝트 안에서만 사용하는 핀 식별 색상. 엑셀 내보내기 대상이 아니다.</summary>
        public string MarkColorHex
        {
            get => _markColorHex;
            set => SetField(ref _markColorHex, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField(ref string field, string? value,
                              [CallerMemberName] string? propertyName = null)
        {
            value ??= "";
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class CableSignalRow
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string ParentSignalId { get; set; } = "";
        public string PinName { get; set; } = "";
        public string WireType { get; set; } = "";
        public Dictionary<string, CablePin> PortPins { get; set; } = new();
    }
}
