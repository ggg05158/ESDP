using System;

namespace ESDP
{
    internal static class PdfRasterLimits
    {
        // Reserve space for WPF pixels, encoder and PDF image copies (approx. 4 x BGRA).
        internal static (int Width, int Height) GetSize(double widthMm, double heightMm, double dpi)
        {
            if (!double.IsFinite(widthMm) || !double.IsFinite(heightMm) || !double.IsFinite(dpi) ||
                widthMm <= 0 || heightMm <= 0 || dpi <= 0)
                throw new ArgumentException("PDF 용지 크기와 해상도는 양수여야 합니다.");
            double width = Math.Ceiling(widthMm / 25.4 * dpi), height = Math.Ceiling(heightMm / 25.4 * dpi);
            long budget = Environment.Is64BitProcess ? 384L * 1024 * 1024 : 96L * 1024 * 1024;
            if (width > 32767 || height > 32767 || width * height * 16 > budget)
                throw new InvalidOperationException("PDF 한 페이지의 예상 메모리가 안전 한도를 넘습니다. DPI를 낮춰 다시 내보내 주세요. (A3는 300 DPI 권장)");
            return ((int)Math.Max(1, width), (int)Math.Max(1, height));
        }
    }
}
