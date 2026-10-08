using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ESDP.Models;

namespace ESDP
{
    public partial class CableRegisterWindow : Window
    {
        private readonly Cable? _editing;
        private string _lineColorHex = "#000000";
        private readonly List<string> _customCategoryNames;
        public IReadOnlyList<string> CustomCategoryNames => _customCategoryNames;
        public Cable? ResultCable { get; private set; }

        public CableRegisterWindow(
            Cable? existing = null,
            CableCategory? preferredCategory = null,
            IEnumerable<string>? customCategoryNames = null,
            string? preferredCustomCategory = null)
        {
            InitializeComponent();

            _customCategoryNames = customCategoryNames?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList()
                ?? new List<string>();
            foreach (CableCategory cat in System.Enum.GetValues(typeof(CableCategory)))
                CategoryCombo.Items.Add(cat.ToString());
            foreach (string name in _customCategoryNames) CategoryCombo.Items.Add(name);

            _editing = existing;

            if (existing == null)
            {
                Title = "케이블 추가";
                CategoryCombo.SelectedItem = !string.IsNullOrWhiteSpace(preferredCustomCategory)
                    ? preferredCustomCategory
                    : (preferredCategory ?? CableCategory.탄내케이블).ToString();
            }
            else
            {
                Title = $"케이블 수정 - {existing.Name}";
                NameBox.Text = existing.Name;
                CategoryCombo.SelectedItem = string.IsNullOrWhiteSpace(existing.CustomCategoryName)
                    ? existing.Category.ToString() : existing.CustomCategoryName;
                _lineColorHex = ColorOption.NormalizeHex(existing.LineColorHex) ?? "#000000";
            }
            UpdateLineColorControls();
        }

        private void CategoryCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
            => UpdateLineColorControls();

        private void UpdateLineColorControls()
        {
            if (LineColorButton == null || LineColorSwatch == null) return;
            bool custom = !Enum.TryParse(CategoryCombo.SelectedItem?.ToString(), out CableCategory selected) ||
                          selected == CableCategory.기타케이블;
            LineColorButton.IsEnabled = custom;
            string shown = selected == CableCategory.점검케이블
                ? "#0070C0"
                : custom ? _lineColorHex : "#000000";
            LineColorSwatch.Fill = ColorOption.BrushFromHex(shown);
        }

        private void LineColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ColorPickerWindow.TryPick(
                    this, _lineColorHex, out string hex, "기타 케이블 선 색"))
                return;
            _lineColorHex = hex;
            UpdateLineColorControls();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("케이블 이름을 입력하세요.", "확인",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var cable = _editing ?? new Cable();
            cable.Name = name;
            string selectedCategory = CategoryCombo.SelectedItem?.ToString() ?? CableCategory.탄내케이블.ToString();
            if (Enum.TryParse(selectedCategory, out CableCategory category))
            {
                cable.Category = category;
                cable.CustomCategoryName = "";
            }
            else
            {
                cable.Category = CableCategory.기타케이블;
                cable.CustomCategoryName = selectedCategory;
            }
            cable.LineColorHex = _lineColorHex;

            // 신규 등록이면 1:1 케이블의 기본 포트 정의 두 개(P1,P2)를 만들어둔다.
            // (기존 편집일 땐 이미 있는 Connectors 그대로 둔다 — 이름/부품정보만 여기서 못 건드림,
            //  포트 이름은 캔버스에서 포트 더블클릭으로 바꾼다)
            if (IsNewCable(_editing))
            {
                cable.Connectors.Add(new CableConnector { PortName = "P1" });
                cable.Connectors.Add(new CableConnector { PortName = "P2" });
            }

            ResultCable = cable;
            DialogResult = true;
        }

        private static bool IsNewCable(Cable? editing) => editing == null;

        private void AddCustomCategoryButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CategoryNameWindow("케이블") { Owner = this };
            if (dialog.ShowDialog() != true) return;
            string name = dialog.ResultName;
            if (Enum.TryParse<CableCategory>(name, out _) || _customCategoryNames.Any(item =>
                    item.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
            {
                MessageBox.Show("같은 이름의 범주가 이미 있습니다.", "범주 추가",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _customCategoryNames.Add(name);
            CategoryCombo.Items.Add(name);
            CategoryCombo.SelectedItem = name;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
