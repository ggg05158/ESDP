using System.Windows;
using System.Windows.Input;

namespace ESDP
{
    public partial class RenamePortWindow : Window
    {
        public string? ResultName { get; private set; }

        public RenamePortWindow(string currentName)
        {
            InitializeComponent();
            NameBox.Text = currentName;
            Loaded += (s, e) => { NameBox.Focus(); NameBox.SelectAll(); };
        }

        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) OkButton_Click(sender, new RoutedEventArgs());
            else if (e.Key == Key.Escape) CancelButton_Click(sender, new RoutedEventArgs());
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            ResultName = NameBox.Text;
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
