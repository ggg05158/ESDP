using System.Windows;

namespace ESDP
{
    public partial class ConnectorSeriesNameWindow : Window
    {
        public string SeriesName => SeriesNameBox.Text.Trim();

        public ConnectorSeriesNameWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => SeriesNameBox.Focus();
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SeriesName))
            {
                MessageBox.Show("시리즈 이름을 입력하세요.", "커넥터 시리즈",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                SeriesNameBox.Focus();
                return;
            }
            DialogResult = true;
        }
    }
}
