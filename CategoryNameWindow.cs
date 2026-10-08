using System.Windows;
using System.Windows.Controls;

namespace ESDP
{
    internal sealed class CategoryNameWindow : Window
    {
        private readonly TextBox _nameBox = new();
        public string ResultName { get; private set; } = "";

        public CategoryNameWindow(
            string targetName, string initialName = "", bool rename = false)
        {
            Title = rename ? $"{targetName} 그룹 이름 변경" : $"새 {targetName} 범주";
            Width = 360;
            Height = 195;
            MinHeight = 185;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(new TextBlock
            {
                Text = rename
                    ? $"변경할 {targetName} 그룹 이름"
                    : $"추가할 {targetName} 범주 이름"
            });
            _nameBox.Text = initialName;
            _nameBox.Margin = new Thickness(0, 7, 0, 12);
            _nameBox.Height = 28;
            Grid.SetRow(_nameBox, 1);
            root.Children.Add(_nameBox);
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var ok = new Button
            {
                Content = rename ? "변경" : "추가",
                Width = 76, Height = 28, IsDefault = true,
                Style = AppThemeManager.GetStyle("PrimaryButtonStyle")
            };
            ok.Click += (_, _) => Accept();
            buttons.Children.Add(ok);
            buttons.Children.Add(new Button
            {
                Content = "취소", Width = 76, Height = 28, Margin = new Thickness(7, 0, 0, 0),
                IsCancel = true
            });
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);
            Content = root;
            Loaded += (_, _) =>
            {
                _nameBox.Focus();
                _nameBox.SelectAll();
            };
        }

        private void Accept()
        {
            ResultName = _nameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(ResultName))
            {
                MessageBox.Show("범주 이름을 입력하세요.", "범주 추가",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        }
    }
}
