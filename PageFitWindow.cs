using System.Windows;
using System.Windows.Controls;

namespace ESDP
{
    internal sealed class PageFitWindow : Window
    {
        private readonly ComboBox _rowBox = new();
        private readonly ComboBox _columnBox = new();
        private readonly RadioButton _keepLayoutButton = new();
        private readonly RadioButton _repositionButton = new();
        private readonly TextBlock _fitNotice = new();
        private readonly Func<int, int, bool> _canKeepLayout;

        public int PageRows => (int)(_rowBox.SelectedItem ?? 1);
        public int PageColumns => (int)(_columnBox.SelectedItem ?? 1);
        public bool RepositionLayout => _repositionButton.IsChecked == true;

        public PageFitWindow(
            int currentRows, int currentColumns,
            Func<int, int, bool> canKeepLayout)
        {
            _canKeepLayout = canKeepLayout;
            Title = "지정 페이지로 맞추기";
            Width = 470;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new Grid { Margin = new Thickness(20) };
            for (int i = 0; i < 6; i++)
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            root.Children.Add(new TextBlock
            {
                Text = "사용할 페이지 배열을 지정하세요.",
                FontWeight = FontWeights.SemiBold,
                FontSize = 15,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var pageGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            pageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            pageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            pageGrid.Children.Add(new TextBlock
            {
                Text = "행", VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            Grid.SetColumn(_rowBox, 1);
            _rowBox.Margin = new Thickness(0, 0, 20, 0);
            pageGrid.Children.Add(_rowBox);
            var columnLabel = new TextBlock
            {
                Text = "열", VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(columnLabel, 2);
            pageGrid.Children.Add(columnLabel);
            Grid.SetColumn(_columnBox, 3);
            pageGrid.Children.Add(_columnBox);
            foreach (int value in Enumerable.Range(1, 10))
            {
                _rowBox.Items.Add(value);
                _columnBox.Items.Add(value);
            }
            _rowBox.SelectedItem = Math.Clamp(currentRows, 1, 10);
            _columnBox.SelectedItem = Math.Clamp(currentColumns, 1, 10);
            _rowBox.SelectionChanged += (_, _) => UpdateFitAvailability();
            _columnBox.SelectionChanged += (_, _) => UpdateFitAvailability();
            Grid.SetRow(pageGrid, 1);
            root.Children.Add(pageGrid);

            var example = new TextBlock
            {
                Text = "예: 1행 × 2열은 현재 페이지 오른쪽에 한 페이지를 더 사용합니다.",
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            };
            Grid.SetRow(example, 2);
            root.Children.Add(example);

            var modes = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            _keepLayoutButton.Content = "페이지 영역만 변경 — 현재 배치와 좌표 유지";
            _keepLayoutButton.IsChecked = true;
            _keepLayoutButton.Margin = new Thickness(0, 0, 0, 8);
            modes.Children.Add(_keepLayoutButton);
            _repositionButton.Content = "배치 위치도 지정한 전체 페이지 비율에 맞춤";
            modes.Children.Add(_repositionButton);
            Grid.SetRow(modes, 3);
            root.Children.Add(modes);

            _fitNotice.TextWrapping = TextWrapping.Wrap;
            _fitNotice.Margin = new Thickness(0, 0, 0, 14);
            _fitNotice.Opacity = 0.78;
            Grid.SetRow(_fitNotice, 4);
            root.Children.Add(_fitNotice);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var apply = new Button
            {
                Content = "적용", Width = 82, Height = 30, IsDefault = true,
                Style = AppThemeManager.GetStyle("PrimaryButtonStyle")
            };
            apply.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(apply);
            buttons.Children.Add(new Button
            {
                Content = "취소", Width = 82, Height = 30,
                Margin = new Thickness(8, 0, 0, 0), IsCancel = true
            });
            Grid.SetRow(buttons, 5);
            root.Children.Add(buttons);

            Content = root;
            Loaded += (_, _) => UpdateFitAvailability();
        }

        private void UpdateFitAvailability()
        {
            bool canKeep = _canKeepLayout(PageRows, PageColumns);
            if (!canKeep)
            {
                _fitNotice.Text =
                    "현재 배치가 목표 영역을 넘습니다. 적용하면 선택한 페이지 수를 지키도록 배치도 함께 자동 조정됩니다.";
                return;
            }

            _fitNotice.Text =
                "현재 배치가 지정 영역 안에 있으므로 위치를 그대로 유지할 수 있습니다.";
        }
    }
}
