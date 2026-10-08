using System.Collections.Generic;

namespace ESDP.Models
{
    /// <summary>
    /// 캔버스의 장치들이 어느 단/구역에 놓였는지를 나타내는 배경 영역.
    /// 장치·케이블·일반 도형보다 항상 뒤에 표시된다.
    /// </summary>
    public class Zone
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = "";

        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 400;
        public double Height { get; set; } = 300;

        /// <summary>이 구역에 소속된 DeviceInstance.Id 목록</summary>
        public List<string> MemberDeviceInstanceIds { get; set; } = new();
    }

    /// <summary>
    /// 캔버스에 실제로 배치된 장치 하나. 어떤 Device 템플릿을
    /// 참조하는지 + 위치 + 크기 + 소속 구역을 가진다.
    /// 크기는 인스턴스마다 다를 수 있다(같은 장치라도 크게/작게 배치 가능).
    /// </summary>
    public class DeviceInstance
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();

        /// <summary>참조하는 Device 템플릿의 Id</summary>
        public string DeviceId { get; set; } = "";

        public double X { get; set; }
        public double Y { get; set; }

        /// <summary>배치된 크기 (사용자가 핸들로 조절)</summary>
        public double Width { get; set; } = 120;
        public double Height { get; set; } = 80;

        /// <summary>
        /// 이 배치 개체에만 해당하는 용도. 같은 Device 템플릿을 사용해도
        /// 각 인스턴스마다 다른 값을 가지며 이름 다음 줄에 괄호로 표시된다.
        /// </summary>
        public string Purpose { get; set; } = "";

        /// <summary>소속 구역 Id (없으면 null)</summary>
        public string? ZoneId { get; set; }

        /// <summary>
        /// 이 개체만의 개별 설정. null 이면 템플릿(Device)을 그대로 따라간다.
        /// 값이 있으면 "이 개체만 수정됨" 상태이며, 템플릿이 나중에 바뀌어도
        /// 이 개체는 영향받지 않는다.
        /// </summary>
        public Device? Override { get; set; }
    }

    /// <summary>
    /// 캔버스에 배치된 케이블 하나. 처음엔 "자유 포트 2개(PortA-PortB)를 잇는 직선 하나"로 시작하고,
    /// 그 선(또는 그 아래 파생된 어떤 다리든) 위의 한 점에서 사용자가 분기를 만들면 Branches 에 추가된다.
    /// 즉 전체 구조는 트리: 루트(PortA↔PortB) + 그 위에서 자유롭게 뻗어나가는 Branches.
    /// </summary>
    public class CableInstance
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();

        /// <summary>참조하는 Cable 템플릿의 Id</summary>
        public string CableId { get; set; } = "";

        /// <summary>루트(처음 생성된 1:1 연결)의 한쪽 끝</summary>
        public CablePortLayout PortA { get; set; } = new();
        /// <summary>루트의 다른 쪽 끝</summary>
        public CablePortLayout PortB { get; set; } = new();

        /// <summary>루트 선 위에 사용자가 추가한 꺾인점들 (개수 제한 없음, 순서대로 지나간다)</summary>
        public List<Point> RootBends { get; set; } = new();

        /// <summary>이 케이블에서 뻗어나간 모든 분기(다리)들</summary>
        public List<CableBranch> Branches { get; set; } = new();

        /// <summary>케이블 이름표를 기본(중앙) 위치에서 얼마나 옮겼는지 (사용자가 끌어서 조절)</summary>
        public double NameLabelDX { get; set; }
        public double NameLabelDY { get; set; }

        /// <summary>캔버스에 케이블 이름표를 표시할지 여부.</summary>
        public bool ShowName { get; set; } = true;

        /// <summary>
        /// 전체 케이블 정렬에서 이 케이블의 현재 경로를 그대로 보존할지 여부.
        /// 개별 케이블 정리에는 영향을 주지 않는다.
        /// </summary>
        public bool ExcludeFromAutoArrange { get; set; }

        /// <summary>이 개체만의 개별 설정 (없으면 템플릿을 따름)</summary>
        public Cable? Override { get; set; }
    }

    /// <summary>
    /// 케이블 포트(끝단) 하나의 캔버스 배치 정보. 절대좌표를 갖고 자유롭게 움직이며,
    /// 다른 포트에 아주 가까이 가면 자석으로 붙는다(ConnectedToPortKey).
    /// </summary>
    public class CablePortLayout
    {
        /// <summary>대응하는 CableConnector.Id (이 포트의 "이름/부품정보" 쪽 정의)</summary>
        public string ConnectorId { get; set; } = "";

        public double PortX { get; set; }
        public double PortY { get; set; }

        /// <summary>다른 포트에 자석으로 물렸을 때, 그 상대 포트의 식별(없으면 미연결)</summary>
        public string? ConnectedToPortKey { get; set; }

        /// <summary>
        /// 이 포트에서 케이블이 나가는 방향은 항상 고정된다 — 자유 포트든 장치에 붙었든 마찬가지.
        /// 자유 포트는 우클릭으로 바꿀 수 있고, 장치에 붙는 순간 그 장치 면 방향으로 자동 갱신된다.
        /// </summary>
        public PortSide FixedFacing { get; set; } = PortSide.Bottom;

        /// <summary>포트 이름표를 기본 위치에서 얼마나 옮겼는지 (사용자가 끌어서 조절)</summary>
        public bool HasCustomLabelPosition { get; set; }
        public double LabelDX { get; set; }
        public double LabelDY { get; set; }
    }

    /// <summary>
    /// 케이블의 분기(다리) 하나. 부모(루트 또는 다른 분기)의 선 위 한 점(T)에서 시작해
    /// 자기 자신의 포트(끝단)까지 이어진다. 분기에서 또 분기가 뻗어나갈 수도 있다(중첩).
    /// </summary>
    public class CableBranch
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();

        /// <summary>부모 식별자: "root" 또는 다른 CableBranch.Id</summary>
        public string ParentId { get; set; } = "root";

        /// <summary>부모의 실제 꺾인 경로 전체를 따라간 위치 (0~1)</summary>
        public double T { get; set; } = 0.5;

        /// <summary>이 분기를 만들면서 새로 추가된 케이블 포트의 위치</summary>
        public CablePortLayout Port { get; set; } = new();

        /// <summary>이 다리(leg) 경로 위에 사용자가 추가한 꺾인점들 (개수 제한 없음)</summary>
        public List<Point> Bends { get; set; } = new();
    }

    /// <summary>
    /// 장치 포트와 케이블 커넥터면 사이의 연결.
    /// (자동 연결이 아니라, 추천 목록에서 사용자가 선택한 결과)
    /// </summary>
    public class Connection
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string DeviceInstanceId { get; set; } = "";
        public string PortId { get; set; } = "";
        public string CableInstanceId { get; set; } = "";
        public string ConnectorFaceId { get; set; } = "";
    }

    /// <summary>간단한 2D 좌표 (WPF Point 와 분리해 순수 데이터로 둠)</summary>
    public class Point
    {
        public double X { get; set; }
        public double Y { get; set; }
    }
}
