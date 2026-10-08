using System.Windows;
using System.Windows.Input;

namespace ESDP
{
    public partial class TextEditWindow : Window
    {
        public string ResultText { get; private set; }

        public TextEditWindow(string currentText)
        {
            InitializeComponent();
            ResultText = currentText;
            ContentBox.Text = currentText;
            Loaded += (_, __) =>
            {
                ContentBox.Focus();
                ContentBox.SelectAll();
            };
        }

        private void ContentBox_PreviewKeyDown(
            object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter &&
                Keyboard.Modifiers == ModifierKeys.Control)
            {
                Accept();
                e.Handled = true;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
            => Accept();

        private void Accept()
        {
            ResultText = ContentBox.Text;
            DialogResult = true;
        }
    }
}
