using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ESDP.Models;

namespace ESDP
{
    public partial class SettingsWindow : Window
    {
        private readonly List<KindColorRow> _rows = new();
        private readonly bool _projectCategories;
        private readonly Dictionary<string, string> _initialColors = new();
        public Dictionary<string, string> ResultCategoryColors { get; } = new();

        public SettingsWindow(IReadOnlyDictionary<string, string>? categoryColors = null)
        {
            InitializeComponent();
            _projectCategories = categoryColors != null;

            string themeName = AppSettings.ThemeName;
            ThemeCombo.SelectedItem = ThemeCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(), themeName, StringComparison.OrdinalIgnoreCase))
                ?? ThemeCombo.Items[0];
            GridStrengthCombo.SelectedItem = GridStrengthCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(), AppSettings.GridStrength,
                    StringComparison.OrdinalIgnoreCase))
                ?? GridStrengthCombo.Items[1];
            UpdateSharePathBox.Text = AppSettings.UpdateSharePath;

            var options = ColorOption.BuildPalette();

            foreach (string name in categoryColors?.Keys ?? AppSettings.KindColors.Keys.Select(kind => kind.ToString()))
            {
                Enum.TryParse(name, out DeviceKind kind);
                string hex = categoryColors?.GetValueOrDefault(name) ?? AppSettings.KindColors.GetValueOrDefault(kind, "#FFFFFF");
                _initialColors[name] = hex;

                var row = new KindColorRow
                {
                    Kind = kind,
                    KindName = name,
                    Options = new ObservableCollection<ColorOption>(options)
                };
                // 현재 색과 같은 항목을 선택. 없으면 팔레트에 추가해서 선택.
                var match = options.FirstOrDefault(
                    o => string.Equals(o.Hex, hex, System.StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    match = new ColorOption { Name = hex, Hex = hex };
                    row.Options.Add(match);
                }
                row.Selected = match;

                _rows.Add(row);
            }

            RowItems.ItemsSource = _rows;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows)
            {
                if (row.Selected != null)
                {
                    if (_projectCategories)
                    {
                        if (!string.Equals(_initialColors[row.KindName], row.Selected.Hex, StringComparison.OrdinalIgnoreCase))
                            ResultCategoryColors[row.KindName] = row.Selected.Hex;
                    }
                    else AppSettings.KindColors[row.Kind] = row.Selected.Hex;
                }
            }
            AppSettings.ThemeName = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                                    ?? AppThemeManager.SoftOrange;
            AppSettings.GridStrength =
                (GridStrengthCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                ?? AppSettings.GridNormal;
            AppSettings.UpdateSharePath = UpdateSharePathBox.Text.Trim();
            AppThemeManager.Apply(AppSettings.ThemeName);
            AppSettings.Save();
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void RowColorPickerButton_Click(
            object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not KindColorRow row)
                return;

            string current = row.Selected?.Hex ?? "#FFFFFF";
            if (!ColorPickerWindow.TryPick(
                    this, current, out string hex,
                    $"{row.KindName} 장치 배경색"))
                return;

            var selected = row.Options.FirstOrDefault(option =>
                option.Hex.Equals(
                    hex, System.StringComparison.OrdinalIgnoreCase));
            if (selected == null)
            {
                selected = new ColorOption { Name = hex, Hex = hex };
                row.Options.Add(selected);
            }
            row.Selected = selected;
        }
    }

    public class KindColorRow : INotifyPropertyChanged
    {
        private ColorOption? _selected;

        public DeviceKind Kind { get; set; }
        public string KindName { get; set; } = "";
        public ObservableCollection<ColorOption> Options { get; set; } = new();
        public ColorOption? Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                PropertyChanged?.Invoke(
                    this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class ColorOption
    {
        public string Name { get; set; } = "";
        public string Hex { get; set; } = "#FFFFFF";
        public Brush Brush => BrushFromHex(Hex);

        public static Brush BrushFromHex(string hex)
            => new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(hex));

        public static string? NormalizeHex(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string text = value.Trim();
            if (!text.StartsWith("#")) text = "#" + text;
            if (text.Length != 7 && text.Length != 9) return null;
            for (int i = 1; i < text.Length; i++)
                if (!Uri.IsHexDigit(text[i])) return null;
            return text.ToUpperInvariant();
        }

        public static List<ColorOption> BuildPalette() => new()
        {
            new ColorOption { Name = "검정",     Hex = "#000000" },
            new ColorOption { Name = "흰색",     Hex = "#FFFFFF" },
            new ColorOption { Name = "연회색",   Hex = "#E0E0E0" },
            new ColorOption { Name = "연파랑",   Hex = "#ADD8E6" },
            new ColorOption { Name = "파랑",     Hex = "#7FB3D5" },
            new ColorOption { Name = "연빨강",   Hex = "#FFB3B3" },
            new ColorOption { Name = "주황",     Hex = "#FFE0B2" },
            new ColorOption { Name = "노랑",     Hex = "#FFF59D" },
            new ColorOption { Name = "연초록",   Hex = "#C8E6C9" },
            new ColorOption { Name = "초록",     Hex = "#A5D6A7" },
            new ColorOption { Name = "연보라",   Hex = "#D1C4E9" },
            new ColorOption { Name = "분홍",     Hex = "#F8BBD0" },
            new ColorOption { Name = "베이지",   Hex = "#EFEBE9" },
        };

        public static List<ColorOption> BuildOfficePalette()
        {
            string[] colors =
            {
                "#FFFFFF", "#000000", "#E7E6E6", "#44546A", "#4472C4",
                "#ED7D31", "#A5A5A5", "#FFC000", "#5B9BD5", "#70AD47",
                "#F2F2F2", "#7F7F7F", "#D0CECE", "#D6DCE4", "#D9E2F3",
                "#FCE4D6", "#E7E6E6", "#FFF2CC", "#DDEBF7", "#E2F0D9",
                "#D9D9D9", "#595959", "#AEAAAA", "#ADB9CA", "#B4C6E7",
                "#F8CBAD", "#D0CECE", "#FFE699", "#BDD7EE", "#C6E0B4",
                "#BFBFBF", "#3F3F3F", "#757171", "#8497B0", "#8EAADB",
                "#F4B183", "#AEAAAA", "#FFD966", "#9DC3E6", "#A9D18E",
                "#A6A6A6", "#262626", "#3A3838", "#323F4F", "#2F5597",
                "#C65911", "#7F7F7F", "#BF9000", "#2E75B6", "#548235",
                "#7F7F7F", "#0D0D0D", "#171717", "#222A35", "#203864",
                "#833C0C", "#525252", "#806000", "#1F4E78", "#375623"
            };

            return colors.Select((hex, index) => new ColorOption
            {
                Name = $"테마 색 {index + 1} ({hex})",
                Hex = hex
            }).ToList();
        }
    }
}
