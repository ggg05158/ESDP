using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace ESDP
{
    public static class AppThemeManager
    {
        public const string SkyBlue = "SkyBlue";
        public const string SoftOrange = "SoftOrange";
        public const string Dark = "Dark";

        private static readonly IReadOnlyDictionary<string, string> BluePalette =
            new Dictionary<string, string>
            {
                ["AppSurfaceBrush"] = "#F4F7FA", ["AppPanelBrush"] = "#FFFFFF",
                ["AppRibbonBrush"] = "#F3F8FC", ["AppSidebarBrush"] = "#F7FAFD",
                ["AppBorderBrush"] = "#C9D7E4", ["AppAccentBrush"] = "#1676C4",
                ["AppAccentLightBrush"] = "#DCEEFF", ["AppTextBrush"] = "#25364A",
                ["AppMutedTextBrush"] = "#66788A", ["AppGroupHeaderBrush"] = "#E7EFF6",
                ["AppGroupHeaderTextBrush"] = "#334E68", ["AppDividerBrush"] = "#CBD9E5",
                ["AppStatusBrush"] = "#EDF4F9", ["AppCardBorderBrush"] = "#C7D6E2",
                ["AppGridLineBrush"] = "#E9EEF2", ["AppWorkspaceBrush"] = "#DDE6EE",
                ["AppControlHoverBrush"] = "#E8F3FC",
                ["AppControlHoverBorderBrush"] = "#65A9DF",
                ["AppControlPressedBrush"] = "#D3E9FA",
                ["AppControlBrush"] = "#FFFFFF", ["AppOnAccentBrush"] = "#FFFFFF",
                ["AppDangerBrush"] = "#C42B1C", ["AppDangerLightBrush"] = "#FDE7E9",
                ["AppWarningTextBrush"] = "#8A5200",
                ["AppInputBorderBrush"] = "#B8CAD9", ["AppAccentTextBrush"] = "#0E5F9F",
                ["AppSubtlePanelBrush"] = "#F4F6F8", ["AppAlternateRowBrush"] = "#F7FAFD",
                ["AppInfoPanelBrush"] = "#EEF5FC", ["AppInfoBorderBrush"] = "#B9D3EA",
                ["AppInfoTextBrush"] = "#355A78", ["AppPanelHeaderBrush"] = "#EEF3F7",
                ["AppPanelHeaderBorderBrush"] = "#C5D0DA", ["AppStrongBorderBrush"] = "#9FB4C5"
            };

        private static readonly IReadOnlyDictionary<string, string> OrangePalette =
            new Dictionary<string, string>
            {
                ["AppSurfaceBrush"] = "#FAF7F3", ["AppPanelBrush"] = "#FFFFFF",
                ["AppRibbonBrush"] = "#FFF7EE", ["AppSidebarBrush"] = "#FFFAF5",
                ["AppBorderBrush"] = "#E4D3C2", ["AppAccentBrush"] = "#C97724",
                ["AppAccentLightBrush"] = "#FFE8CF", ["AppTextBrush"] = "#4A3828",
                ["AppMutedTextBrush"] = "#806A56", ["AppGroupHeaderBrush"] = "#F8EBDD",
                ["AppGroupHeaderTextBrush"] = "#6B4930", ["AppDividerBrush"] = "#E7D5C3",
                ["AppStatusBrush"] = "#FCF3E8", ["AppCardBorderBrush"] = "#E1CFBD",
                ["AppGridLineBrush"] = "#F1EAE3", ["AppWorkspaceBrush"] = "#EDE3D8",
                ["AppControlHoverBrush"] = "#FFF0DF",
                ["AppControlHoverBorderBrush"] = "#D9A05F",
                ["AppControlPressedBrush"] = "#FFE1C2",
                ["AppControlBrush"] = "#FFFFFF", ["AppOnAccentBrush"] = "#FFFFFF",
                ["AppDangerBrush"] = "#C42B1C", ["AppDangerLightBrush"] = "#FDE7E9",
                ["AppWarningTextBrush"] = "#8A4C12",
                ["AppInputBorderBrush"] = "#DEC7B0", ["AppAccentTextBrush"] = "#A95D18",
                ["AppSubtlePanelBrush"] = "#FAF6F1", ["AppAlternateRowBrush"] = "#FFF9F3",
                ["AppInfoPanelBrush"] = "#FFF3E6", ["AppInfoBorderBrush"] = "#E6C49E",
                ["AppInfoTextBrush"] = "#754923", ["AppPanelHeaderBrush"] = "#FAEFE4",
                ["AppPanelHeaderBorderBrush"] = "#E3CFBB", ["AppStrongBorderBrush"] = "#D3B89B"
            };

        private static readonly IReadOnlyDictionary<string, string> DarkPalette =
            new Dictionary<string, string>
            {
                ["AppSurfaceBrush"] = "#1E1E1E", ["AppPanelBrush"] = "#252526",
                ["AppRibbonBrush"] = "#292725", ["AppSidebarBrush"] = "#232323",
                ["AppBorderBrush"] = "#403D3A", ["AppAccentBrush"] = "#D58A3A",
                ["AppAccentLightBrush"] = "#4A3421", ["AppTextBrush"] = "#F2F0ED",
                ["AppMutedTextBrush"] = "#B9B1A9", ["AppGroupHeaderBrush"] = "#352D26",
                ["AppGroupHeaderTextBrush"] = "#F0C89E", ["AppDividerBrush"] = "#48433E",
                ["AppStatusBrush"] = "#29231E", ["AppCardBorderBrush"] = "#4B4641",
                ["AppGridLineBrush"] = "#E9EEF2", ["AppWorkspaceBrush"] = "#171717",
                ["AppControlHoverBrush"] = "#3A3027",
                ["AppControlHoverBorderBrush"] = "#D58A3A",
                ["AppControlPressedBrush"] = "#513721",
                ["AppControlBrush"] = "#2B2B2B", ["AppOnAccentBrush"] = "#21150A",
                ["AppDangerBrush"] = "#FF99A4", ["AppDangerLightBrush"] = "#442326",
                ["AppWarningTextBrush"] = "#F4C06A",
                ["AppInputBorderBrush"] = "#5A544E", ["AppAccentTextBrush"] = "#F2B979",
                ["AppSubtlePanelBrush"] = "#2B2B2B", ["AppAlternateRowBrush"] = "#2D2D2D",
                ["AppInfoPanelBrush"] = "#352B22", ["AppInfoBorderBrush"] = "#725537",
                ["AppInfoTextBrush"] = "#F0C89E", ["AppPanelHeaderBrush"] = "#302B27",
                ["AppPanelHeaderBorderBrush"] = "#4E4842", ["AppStrongBorderBrush"] = "#655E57"
            };

        public static void Apply(string? themeName)
        {
            var palette = themeName?.ToUpperInvariant() switch
            {
                "SKYBLUE" => BluePalette,
                "DARK" => DarkPalette,
                _ => OrangePalette
            };
            if (Application.Current == null) return;
            foreach (var pair in palette)
            {
                if (Application.Current.Resources[pair.Key] is SolidColorBrush brush)
                {
                    Color color = (Color)ColorConverter.ConvertFromString(pair.Value);
                    if (brush.IsFrozen)
                        Application.Current.Resources[pair.Key] = new SolidColorBrush(color);
                    else
                        brush.Color = color;
                }
            }

            ApplyGridStrength(themeName);

            foreach (Window window in Application.Current.Windows)
                ApplyWindowChrome(window);
        }

        private static void ApplyGridStrength(string? themeName)
        {
            bool orange = !string.Equals(themeName, SkyBlue,
                StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(themeName, Dark,
                    StringComparison.OrdinalIgnoreCase);
            string color = AppSettings.GridStrength switch
            {
                AppSettings.GridLight => orange ? "#F1EAE3" : "#E9EEF2",
                AppSettings.GridStrong => orange ? "#C3AE9A" : "#B5C3CE",
                _ => orange ? "#DDCFC2" : "#D3DCE4"
            };
            Application.Current.Resources["AppGridLineBrush"] =
                new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }

        public static void ApplyWindowChrome(Window window)
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;
                int enabled = string.Equals(AppSettings.ThemeName, Dark,
                    StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            }
            catch
            {
                // 지원하지 않는 Windows 버전에서는 기본 제목 표시줄을 유지한다.
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int valueSize);

        public static SolidColorBrush GetBrush(string resourceKey)
        {
            if (Application.Current?.TryFindResource(resourceKey) is SolidColorBrush brush)
                return brush;
            return Brushes.Transparent;
        }

        public static SolidColorBrush WithAlpha(string resourceKey, byte alpha)
        {
            Color color = GetBrush(resourceKey).Color;
            return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        }

        public static Style? GetStyle(string resourceKey)
            => Application.Current?.TryFindResource(resourceKey) as Style;
    }
}
