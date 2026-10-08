using System.Collections.Generic;

namespace ESDP.Models
{
    /// <summary>
    /// 하나의 물리적 신호(넷). 케이블 내부에서 여러 커넥터/핀으로
    /// 분기(멀티드롭)될 수 있으므로 endpoint를 리스트로 가진다.
    ///
    /// NetId 는 "케이블 파일 하나 안에서만" 유일한 단순 정수다.
    /// 여러 케이블을 동시에 다룰 때의 전역 유일성은 파서가
    /// (케이블이름 + NetId) 로 조합해 처리한다 — 엑셀에는 정수만 적는다.
    /// </summary>
    public class Net
    {
        /// <summary>케이블 파일 내에서 유일한 구분용 정수</summary>
        public int NetId { get; set; }

        /// <summary>이 넷에 연결된 모든 연결점</summary>
        public List<NetEndpoint> Endpoints { get; set; } = new();
    }

    /// <summary>
    /// 넷이 연결되는 지점 하나. 진짜 식별 기준은 (Connector, Pin) 조합이고,
    /// DisplayName 은 위치별로 달라질 수 있는 표시용 라벨이다.
    /// (예: 같은 넷이 한쪽에선 "Sig5", 분기 후엔 "Sig5-1")
    /// </summary>
    public class NetEndpoint
    {
        /// <summary>커넥터 지시자 (예: "J2")</summary>
        public string Connector { get; set; } = "";

        /// <summary>핀 번호</summary>
        public int Pin { get; set; }

        /// <summary>이 위치에서의 표시 라벨 (예: "Sig4", "Sig4-1")</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>확장용 속성 (feature1/feature2 자리)</summary>
        public Dictionary<string, string> Features { get; set; } = new();
    }
}
