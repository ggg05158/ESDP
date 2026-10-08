using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ESDP
{
    public partial class CustomConnectorEditorWindow : Window
    {
        public string ConnectorName => ConnectorNameBox.Text.Trim();
        public int PinCount { get; private set; }
        public string ImagePath { get; private set; } = "";

        public CustomConnectorEditorWindow(string seriesName)
        {
            InitializeComponent();
            SeriesNameBox.Text = seriesName;
            Loaded += (_, _) => ConnectorNameBox.Focus();
        }

        private void ChooseImageButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "핀 배치 사진 선택",
                Filter = "이미지 파일|*.png;*.jpg;*.jpeg;*.bmp;*.gif|모든 파일|*.*"
            };
            if (dialog.ShowDialog(this) != true) return;

            ImagePath = dialog.FileName;
            ImageNameText.Text = Path.GetFileName(ImagePath);
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(ImagePath, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                PreviewImage.Source = image;
                NoImageText.Visibility = Visibility.Collapsed;
            }
            catch
            {
                PreviewImage.Source = null;
                NoImageText.Text = "이미지를 미리 볼 수 없습니다.";
                NoImageText.Visibility = Visibility.Visible;
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ConnectorName))
            {
                MessageBox.Show("커넥터 이름을 입력하세요.", "커넥터 추가",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                ConnectorNameBox.Focus();
                return;
            }
            if (!int.TryParse(PinCountBox.Text.Trim(), out int pinCount) || pinCount < 0)
            {
                MessageBox.Show("핀 수에는 0 이상의 정수를 입력하세요.", "커넥터 추가",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                PinCountBox.Focus();
                return;
            }
            PinCount = pinCount;
            DialogResult = true;
        }
    }
}
