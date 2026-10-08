using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ESDP
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppSettings.Load();
            AppThemeManager.Apply(AppSettings.ThemeName);
            EventManager.RegisterClassHandler(
                typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(DialogWindow_Loaded));
            base.OnStartup(e);

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var splash = new SplashWindow();
            MainWindow = splash;
            splash.Show();

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(async () =>
            {
                try
                {
                    // 아주 빠른 PC에서도 스플래시가 깜빡이지 않고 로딩 상태를
                    // 알아볼 수 있도록 최소 표시 시간을 보장한다.
                    Task minimumDisplay = Task.Delay(850);
                    var startWindow = new StartWindow();
                    await minimumDisplay;

                    MainWindow = startWindow;
                    startWindow.Show();
                    splash.Close();
                    ShutdownMode = ShutdownMode.OnLastWindowClose;
                    if (ConnectorCatalogService.Instance.LastLoadWarning is { } catalogWarning)
                        MessageBox.Show(startWindow, catalogWarning, "커넥터 카탈로그 확인", MessageBoxButton.OK, MessageBoxImage.Warning);

                    _ = Dispatcher.BeginInvoke(
                        DispatcherPriority.ApplicationIdle,
                        new Action(async () =>
                            await UpdateNotificationService.CheckAndNotifyAsync()));
                }
                catch (Exception ex)
                {
                    splash.Close();
                    MessageBox.Show(
                        $"프로그램을 시작하지 못했습니다.\n\n{ex.Message}",
                        "ESDP 시작 오류",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown(-1);
                }
            }));
        }

        private static void DialogWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Window window) return;
            AppThemeManager.ApplyWindowChrome(window);
            if (window is ESDP.MainWindow or StartWindow or SplashWindow ||
                window.Content is not FrameworkElement root) return;

            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                if (!window.IsLoaded || root.ActualWidth <= 0 || root.ActualHeight <= 0) return;
                double addWidth = 0;
                double addHeight = 0;
                foreach (var button in VisualDescendants(root).OfType<Button>())
                {
                    if (button.Visibility != Visibility.Visible ||
                        button.ActualWidth <= 0 || button.ActualHeight <= 0) continue;
                    try
                    {
                        Rect bounds = button.TransformToAncestor(root).TransformBounds(
                            new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                        double overflowWidth = bounds.Right - root.ActualWidth;
                        double overflowHeight = bounds.Bottom - root.ActualHeight;
                        double clippedWidth = button.DesiredSize.Width - button.ActualWidth;
                        double clippedHeight = button.DesiredSize.Height - button.ActualHeight;
                        if (overflowWidth > 0.5)
                            addWidth = Math.Max(addWidth, overflowWidth + 10);
                        if (overflowHeight > 0.5)
                            addHeight = Math.Max(addHeight, overflowHeight + 10);
                        if (clippedWidth > 0.5)
                            addWidth = Math.Max(addWidth, clippedWidth + 10);
                        if (clippedHeight > 0.5)
                            addHeight = Math.Max(addHeight, clippedHeight + 10);
                    }
                    catch (InvalidOperationException)
                    {
                        // 다른 시각 트리에 있는 팝업 버튼은 해당 팝업이 자체 크기를 관리한다.
                    }
                }

                if (addWidth <= 0 && addHeight <= 0) return;
                Rect workArea = SystemParameters.WorkArea;
                if (addWidth > 0)
                    window.Width = Math.Min(workArea.Width, window.ActualWidth + addWidth);
                if (addHeight > 0)
                    window.Height = Math.Min(workArea.Height, window.ActualHeight + addHeight);
            });
        }

        private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                yield return child;
                foreach (DependencyObject descendant in VisualDescendants(child))
                    yield return descendant;
            }
        }
    }

}
