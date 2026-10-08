using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ESDP
{
    public partial class StartWindow : Window
    {
        public StartWindow()
        {
            InitializeComponent();
            RefreshRecentProjects();
        }

        private void NewProjectButton_Click(object sender, RoutedEventArgs e)
        {
            var main = new MainWindow();
            Application.Current.MainWindow = main;
            main.Show();
            Close();
        }

        private void OpenProjectButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "ESDP 프로젝트 (*.esdp)|*.esdp|이전 프로젝트 (*.esmp)|*.esmp|모든 파일 (*.*)|*.*",
                Title = "프로젝트 불러오기"
            };
            if (dlg.ShowDialog() != true) return;

            OpenProject(dlg.FileName);
        }

        private void RecentProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string path })
                OpenProject(path);
        }

        private void RefreshRecentProjects()
        {
            var projects = RecentProjects.Load();
            RecentProjectsList.ItemsSource = projects;
            NoRecentProjectsText.Visibility =
                projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OpenProject(string path)
        {
            if (!File.Exists(path))
            {
                RecentProjects.Remove(path);
                RefreshRecentProjects();
                MessageBox.Show("프로젝트 파일을 찾을 수 없어 최근 목록에서 제거했습니다.",
                                "ESDP", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var main = new MainWindow();
            if (!main.LoadProjectFromFile(path))
            {
                main.Close();
                return;
            }

            Application.Current.MainWindow = main;
            main.Show();
            Close();
        }
    }
}
