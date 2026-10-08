namespace ESDP.Models
{
    /// <summary>
    /// 용지 크기 정의. 내부 좌표는 전부 mm 단위로 저장하고,
    /// 화면에 그릴 때만 픽셀로 환산한다. (PPT/PDF export 시에도 mm 원본에서 파생)
    /// </summary>
    public enum PaperSize
    {
        A3_Landscape,
        A3_Portrait,
        A4_Landscape,
        A4_Portrait
    }

    public static class PaperSizeInfo
    {
        /// <summary>선택한 용지의 (가로mm, 세로mm)를 반환</summary>
        public static (double WidthMm, double HeightMm) GetSizeMm(PaperSize size)
        {
            return size switch
            {
                PaperSize.A3_Landscape => (420, 297),
                PaperSize.A3_Portrait  => (297, 420),
                PaperSize.A4_Landscape => (297, 210),
                PaperSize.A4_Portrait  => (210, 297),
                _ => (420, 297)
            };
        }

        /// <summary>표시용 이름</summary>
        public static string GetDisplayName(PaperSize size)
        {
            return size switch
            {
                PaperSize.A3_Landscape => "A3 가로",
                PaperSize.A3_Portrait  => "A3 세로",
                PaperSize.A4_Landscape => "A4 가로",
                PaperSize.A4_Portrait  => "A4 세로",
                _ => "A3 가로"
            };
        }
    }
}
