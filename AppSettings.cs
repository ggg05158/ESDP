using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using ESDP.Models;

namespace ESDP
{
    /// <summary>
    /// 프로그램 전역 설정. (나중에 프로젝트 저장 기능을 붙일 때
    /// Project 모델 안으로 옮겨서 파일에 함께 저장할 예정)
    /// </summary>
    public static class AppSettings
    {
        private sealed class StoredSettings
        {
            public string ThemeName { get; set; } = AppThemeManager.SoftOrange;
            public string GridStrength { get; set; } = GridNormal;
            public string UpdateSharePath { get; set; } = DefaultUpdateSharePath;
            public string LastNotifiedUpdateFolder { get; set; } = "";
            public Dictionary<string, string> KindColors { get; set; } = new();
        }

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ESDP", "settings.json");

        public static string ThemeName { get; set; } = AppThemeManager.SoftOrange;
        public const string DefaultUpdateSharePath = @"\\192.168.0.1\ESDP_Update";
        public static string UpdateSharePath { get; set; } = DefaultUpdateSharePath;
        public static string LastNotifiedUpdateFolder { get; set; } = "";
        public const string GridLight = "Light";
        public const string GridNormal = "Normal";
        public const string GridStrong = "Strong";
        public static string GridStrength { get; set; } = GridNormal;
        /// <summary>장치 종류별 배경색 (Hex)</summary>
        public static readonly Dictionary<DeviceKind, string> KindColors = new()
        {
            [DeviceKind.주장치]   = "#ADD8E6",
            [DeviceKind.착화류]   = "#FFB3B3",
            [DeviceKind.시험장치] = "#C8E6C9",
            [DeviceKind.점검장비] = "#FFE0B2",
        };

        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var stored = JsonSerializer.Deserialize<StoredSettings>(
                    File.ReadAllText(SettingsPath));
                if (stored == null) return;
                ThemeName = stored.ThemeName is AppThemeManager.SkyBlue or
                    AppThemeManager.SoftOrange or AppThemeManager.Dark
                    ? stored.ThemeName
                    : AppThemeManager.SoftOrange;
                GridStrength = stored.GridStrength is GridLight or GridStrong
                    ? stored.GridStrength
                    : GridNormal;
                UpdateSharePath = stored.UpdateSharePath?.Trim() ?? "";
                LastNotifiedUpdateFolder =
                    stored.LastNotifiedUpdateFolder?.Trim() ?? "";
                foreach (var pair in stored.KindColors)
                    if (Enum.TryParse(pair.Key, out DeviceKind kind) &&
                        ColorOption.NormalizeHex(pair.Value) is { } hex)
                        KindColors[kind] = hex;
            }
            catch
            {
                ThemeName = AppThemeManager.SoftOrange;
            }
        }

        public static void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var stored = new StoredSettings
            {
                ThemeName = ThemeName,
                GridStrength = GridStrength,
                UpdateSharePath = UpdateSharePath,
                LastNotifiedUpdateFolder = LastNotifiedUpdateFolder,
                KindColors = KindColors.ToDictionary(
                    pair => pair.Key.ToString(), pair => pair.Value)
            };
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
        }

        public static Color GetColor(DeviceKind kind)
        {
            if (!KindColors.TryGetValue(kind, out var hex) || string.IsNullOrWhiteSpace(hex))
                hex = "#FFFFFF";
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return Colors.White; }
        }

        public static Brush GetBrush(DeviceKind kind) => new SolidColorBrush(GetColor(kind));

        public static Brush GetDeviceBrush(Device device)
        {
            if (ColorOption.NormalizeHex(device.CategoryColorHex) is { } categoryColor)
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(categoryColor));
            if (device.Kind != DeviceKind.기타)
                return GetBrush(device.Kind);

            string hex = ColorOption.NormalizeHex(device.BackgroundColorHex) ?? "#FFFFFF";
            try
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            catch
            {
                return Brushes.White;
            }
        }
    }
}
