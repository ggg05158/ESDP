using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace ESDP;

public partial class MainWindow
{
    private async void Menu_ExportPptx(object sender, RoutedEventArgs e)
    {
        if (_sheets.Count == 0 || _currentSheetIndex < 0 || _currentSheetIndex >= _sheets.Count) return;
        var all = new CheckBox { Content = "모든 시트 내보내기", Margin = new Thickness(0, 12, 0, 12) };
        var paper = new ComboBox { ItemsSource = new[] { "A3 가로 (420 × 297 mm)", "A3 세로 (297 × 420 mm)" },
            SelectedIndex = GetBasePaperPx().W >= GetBasePaperPx().H ? 0 : 1, MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Left };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "PPT 슬라이드 크기", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(paper); panel.Children.Add(all);
        panel.Children.Add(new TextBlock { Text = "현재 도면의 각 페이지를 슬라이드 한 장으로 옮깁니다.\n격자·선택 핸들은 제외하고, 원본 케이블 경로는 유지합니다.\n장치 이름·용도는 본체 도형의 텍스트이며, 장치 포트·포트명은 장치와 함께 그룹화합니다.\n사진 장치 이름의 흰 배경은 글자 강조색으로 옮깁니다.\n메인 가닥과 각 분기 가닥은 꺾임 수와 관계없이 각각 하나의 편집 가능한 선 개체입니다.\n복잡한 경로는 자유형 선이며, PPT에서 장치 이동 시 자동으로 따라붙지 않습니다.\n교차 점프는 고정 벡터이며 PPT에서 자동 갱신되지는 않습니다.\n복잡한 여러 줄 글자·지원하지 않는 서식은 해당 부분만 이미지로 보존합니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "내보내기", IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "취소", IsCancel = true, MinWidth = 80 };
        buttons.Children.Add(apply); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        var options = new Window { Title = "PPT로 내보내기", Owner = this, Width = 530, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, ShowInTaskbar = false };
        apply.Click += (_, _) => options.DialogResult = true;
        if (options.ShowDialog() != true) return;
        var save = new SaveFileDialog { Filter = "PowerPoint 프레젠테이션 (*.pptx)|*.pptx", DefaultExt = ".pptx", AddExtension = true,
            FileName = string.IsNullOrEmpty(_currentFilePath) ? "ESDP 구성도.pptx" : Path.GetFileNameWithoutExtension(_currentFilePath) + ".pptx" };
        if (save.ShowDialog(this) != true) return;

        bool completed = false;
        var progress = new Window { Title = "PPT 내보내기", Owner = this, Width = 420, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new TextBlock { Text = "편집 가능한 PPT 문서를 만드는 중입니다...", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) } };
        progress.Closing += (_, args) => args.Cancel = !completed;
        bool wasEnabled = IsEnabled;
        var previousCursor = Cursor;
        string? message = null;
        var messageIcon = MessageBoxImage.Information;
        try
        {
            IsEnabled = false; Cursor = Cursors.Wait; progress.Show();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            CaptureCurrentSheet();
            var indexes = all.IsChecked == true ? Enumerable.Range(0, _sheets.Count).ToArray() : new[] { _currentSheetIndex };
            var pages = CapturePptxPages(indexes);
            bool portrait = paper.SelectedIndex == 1;
            var result = await Task.Run(() => PptxExportService.Write(save.FileName, pages, portrait));
            StatusText.Text = $"PPT 내보내기 완료: {result.Slides}슬라이드, 케이블 가닥 {result.Connectors}, 글자 {result.Texts}, 벡터 {result.Vectors}, 이미지 {result.Images}";
            if (result.RasterFallbacks != 0)
                message = $"PPT를 저장했습니다.\n복잡한 서식 {result.RasterFallbacks}개는 표시를 보존하기 위해 이미지로 옮겼습니다.";
        }
        catch (Exception ex)
        {
            message = "PPT 내보내기에 실패했습니다. 기존 파일과 원본 도면은 유지됩니다.\n\n" + ex.Message;
            messageIcon = MessageBoxImage.Error;
        }
        finally { completed = true; progress.Close(); IsEnabled = wasEnabled; Cursor = previousCursor; }
        if (message != null) MessageBox.Show(this, message, "PPT 내보내기", MessageBoxButton.OK, messageIcon);
    }

    private List<PptxPage> CapturePptxPages(int[] indexes)
    {
        var pages = new List<PptxPage>();
        var view = CreatePdfExportView(); // independent snapshot; disables editor-only routing/normalization
        try
        {
            foreach (int index in indexes)
            {
                view.ApplySheet(index); view.PrepareCurrentCanvasForPdfExport();
                var (width, height) = view.GetBasePaperPx();
                int cols = Math.Max(1, (int)Math.Round(view.MainCanvas.Width / width));
                int rows = Math.Max(1, (int)Math.Round(view.MainCanvas.Height / height));
                if ((long)cols * rows + pages.Count > 500) throw new InvalidOperationException("PPT 내보내기는 500페이지까지 가능합니다. 페이지 범위를 줄여주세요.");
                var scene = PptxSceneCapture.Capture(view.MainCanvas, view._placedCables, PageGuideTag,
                    view._placed, view._placedShapes);
                for (int row = 0; row < rows; row++)
                    for (int col = 0; col < cols; col++)
                        pages.Add(new($"{view._sheets[index].Name} ({row + 1},{col + 1})", scene, new Rect(col * width, row * height, width, height)));
            }
            return pages;
        }
        finally { view._isDirtyValue = false; view.Close(); }
    }
}
