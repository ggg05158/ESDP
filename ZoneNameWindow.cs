using System.Windows;
using System.Windows.Controls;

namespace ESDP
{
    internal sealed class ZoneNameWindow : Window
    {
        private readonly TextBox _nameBox = new();
        public string ResultName { get; private set; } = "";

        public ZoneNameWindow(string initialName = "")
        {
            Title = string.IsNullOrWhiteSpace(initialName)
                ? "구역 이름" : "구역 이름 변경";
            Width = 380;
            Height = 185;
            MinHeight = 175;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(new TextBlock
            {
                Text = "캔버스에 표시할 구역 이름",
                FontWeight = FontWeights.SemiBold
            });

            _nameBox.Text = initialName;
            _nameBox.Height = 30;
            _nameBox.Margin = new Thickness(0, 8, 0, 14);
            _nameBox.VerticalContentAlignment = VerticalAlignment.Center;
            Grid.SetRow(_nameBox, 1);
            root.Children.Add(_nameBox);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var ok = new Button
            {
                Content = "확인",
                Width = 78,
                Height = 30,
                IsDefault = true,
                Style = AppThemeManager.GetStyle("PrimaryButtonStyle")
            };
            ok.Click += (_, _) => Accept();
            buttons.Children.Add(ok);
            buttons.Children.Add(new Button
            {
                Content = "취소",
                Width = 78,
                Height = 30,
                Margin = new Thickness(8, 0, 0, 0),
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
                MessageBox.Show("구역 이름을 입력하세요.", "구역 설정",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                _nameBox.Focus();
                return;
            }
            DialogResult = true;
        }
    }
}
