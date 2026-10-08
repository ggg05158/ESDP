using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ESDP.Models;
using Microsoft.Win32;

using Point = System.Windows.Point;
using Path = System.IO.Path;

namespace ESDP
{
    public partial class MainWindow
    {
        private sealed class PdfExportOptions
        {
            public List<int> SheetIndexes { get; init; } = new();
            public int Dpi { get; init; } = 300;
            public string PaperName { get; init; } = "A3";
            public bool Landscape { get; init; } = true;
        }

        private sealed class PdfExportProgressWindow : Window
        {
            private readonly TextBlock _statusText;
            private readonly TextBlock _detailText;
            private readonly ProgressBar _progressBar;
            private readonly System.Diagnostics.Stopwatch _stopwatch = new();
            private bool _allowClose;

            public PdfExportProgressWindow(Window owner)
            {
                Title = "PDF 내보내기 진행 상황";
                Owner = owner;
                Width = 520;
                Height = 180;
                ResizeMode = ResizeMode.NoResize;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                ShowInTaskbar = false;

                var root = new Grid { Margin = new Thickness(22, 18, 22, 18) };
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                _statusText = new TextBlock
                {
                    Text = "내보내기를 준비하고 있습니다.",
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                root.Children.Add(_statusText);

                _progressBar = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 1,
                    Value = 0,
                    Height = 18,
                    IsIndeterminate = true,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                Grid.SetRow(_progressBar, 1);
                root.Children.Add(_progressBar);

                _detailText = new TextBlock
                {
                    Text = "페이지 수를 계산하는 중...",
                    FontSize = 11,
                    Foreground = AppThemeManager.GetBrush("AppMutedTextBrush")
                };
                Grid.SetRow(_detailText, 2);
                root.Children.Add(_detailText);

                Content = root;
                Closing += (_, args) => args.Cancel = !_allowClose;
            }

            public void SetPreparing(string detail)
            {
                _statusText.Text = "PDF 내보내기를 준비하고 있습니다.";
                _detailText.Text = detail;
                _progressBar.IsIndeterminate = true;
                RefreshDisplay();
            }

            public void BeginExport(int totalPages)
            {
                _stopwatch.Restart();
                _progressBar.IsIndeterminate = false;
                _progressBar.Minimum = 0;
                _progressBar.Maximum = Math.Max(1, totalPages);
                _progressBar.Value = 0;
                _statusText.Text = $"0 / {totalPages} 페이지 (0%)";
                _detailText.Text = "첫 페이지를 내보내는 중...";
                RefreshDisplay();
            }

            public void UpdateProgress(
                int completedPages,
                int totalPages,
                string sheetName,
                int sheetPage,
                int sheetPageCount)
            {
                _progressBar.Value = completedPages;
                int percent = totalPages <= 0
                    ? 0
                    : (int)Math.Round(completedPages * 100.0 / totalPages);
                _statusText.Text = $"{completedPages} / {totalPages} 페이지 ({percent}%)";

                string elapsed = FormatDuration(_stopwatch.Elapsed);
                string remaining = "계산 중";
                if (completedPages > 0 && completedPages < totalPages)
                {
                    double remainingSeconds =
                        _stopwatch.Elapsed.TotalSeconds / completedPages *
                        (totalPages - completedPages);
                    remaining = FormatDuration(TimeSpan.FromSeconds(remainingSeconds));
                }
                else if (completedPages >= totalPages)
                {
                    remaining = "0초";
                }

                _detailText.Text =
                    $"{sheetName} - {sheetPage}/{sheetPageCount} 페이지  |  " +
                    $"경과 {elapsed}  |  예상 남은 시간 {remaining}";
                RefreshDisplay();
            }

            public void SetSaving()
            {
                _statusText.Text = "PDF 파일을 저장하고 있습니다.";
                _detailText.Text = $"경과 시간 {FormatDuration(_stopwatch.Elapsed)}";
                RefreshDisplay();
            }

            public void CloseAfterExport()
            {
                _allowClose = true;
                Close();
            }

            private void RefreshDisplay()
            {
                Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            }

            private static string FormatDuration(TimeSpan duration)
            {
                if (duration.TotalMinutes >= 1)
                    return $"{(int)duration.TotalMinutes}분 {duration.Seconds}초";
                return $"{Math.Max(0, (int)Math.Ceiling(duration.TotalSeconds))}초";
            }
        }

        private PdfExportOptions? ShowPdfExportSettingsDialog()
        {
            PdfExportOptions? result = null;
            var dialog = new Window
            {
                Title = "PDF 내보내기 설정",
                Owner = this,
                Width = 650,
                Height = 500,
                MinWidth = 580,
                MinHeight = 440,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                SizeToContent = SizeToContent.Manual
            };

            var root = new Grid { Margin = new Thickness(22, 18, 22, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            root.Children.Add(new TextBlock
            {
                Text = "PDF 내보내기 설정",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });

            var content = new Grid { Margin = new Thickness(0, 18, 0, 18) };
            Grid.SetRow(content, 1);
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var sheetPanel = new Grid();
            sheetPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            sheetPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            sheetPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            sheetPanel.Children.Add(new TextBlock
            {
                Text = "내보낼 시트",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var scopeButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(scopeButtons, 1);
            var currentSheetButton = new Button
            {
                Content = "현재 시트만",
                Style = AppThemeManager.GetStyle("SubtleButtonStyle"),
                MinWidth = 90,
                Height = 28,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(0, 0, 6, 0)
            };
            var allSheetsButton = new Button
            {
                Content = "전체 시트",
                Style = AppThemeManager.GetStyle("SubtleButtonStyle"),
                MinWidth = 82,
                Height = 28,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(0, 0, 6, 0)
            };
            var clearSheetsButton = new Button
            {
                Content = "선택 해제",
                Style = AppThemeManager.GetStyle("SubtleButtonStyle"),
                MinWidth = 82,
                Height = 28,
                Padding = new Thickness(10, 2, 10, 2)
            };

            scopeButtons.Children.Add(currentSheetButton);
            scopeButtons.Children.Add(allSheetsButton);
            scopeButtons.Children.Add(clearSheetsButton);
            sheetPanel.Children.Add(scopeButtons);

            var sheetCheckPanel = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
            var sheetChecks = new List<CheckBox>();
            for (int index = 0; index < _sheets.Count; index++)
            {
                var check = new CheckBox
                {
                    Content = $"{index + 1}. {_sheets[index].Name}",
                    IsChecked = index == _currentSheetIndex,
                    FontSize = 13,
                    Padding = new Thickness(4, 3, 4, 3),
                    Margin = new Thickness(0, 1, 0, 1)
                };
                sheetChecks.Add(check);
                sheetCheckPanel.Children.Add(check);
            }

            var sheetListBorder = new Border
            {
                BorderBrush = AppThemeManager.GetBrush("AppInputBorderBrush"),
                BorderThickness = new Thickness(1),
                Background = AppThemeManager.GetBrush("AppPanelBrush"),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = sheetCheckPanel
                }
            };
            Grid.SetRow(sheetListBorder, 2);
            sheetPanel.Children.Add(sheetListBorder);
            content.Children.Add(sheetPanel);

            var qualityPanel = new StackPanel();
            Grid.SetColumn(qualityPanel, 2);
            qualityPanel.Children.Add(new TextBlock
            {
                Text = "출력 품질",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });
            qualityPanel.Children.Add(new TextBlock
            {
                Text = "용지 크기",
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 5)
            });
            var paperSizeCombo = new ComboBox
            {
                Width = 130,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Left,
                ItemsSource = new[] { "A3", "A4" },
                SelectedItem = _currentPaper is PaperSize.A4_Landscape or PaperSize.A4_Portrait
                    ? "A4"
                    : "A3",
                Margin = new Thickness(0, 0, 0, 10)
            };
            qualityPanel.Children.Add(paperSizeCombo);
            qualityPanel.Children.Add(new TextBlock
            {
                Text = "방향",
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 5)
            });
            var orientationCombo = new ComboBox
            {
                Width = 130,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Left,
                ItemsSource = new[] { "가로", "세로" },
                SelectedItem = _currentPaper is PaperSize.A3_Portrait or PaperSize.A4_Portrait
                    ? "세로"
                    : "가로",
                Margin = new Thickness(0, 0, 0, 14)
            };
            qualityPanel.Children.Add(orientationCombo);
            qualityPanel.Children.Add(new TextBlock
            {
                Text = "DPI",
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 5)
            });
            var dpiCombo = new ComboBox
            {
                IsEditable = true,
                Width = 130,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Left,
                ItemsSource = new[] { "150", "300", "600" },
                Text = "300"
            };
            qualityPanel.Children.Add(dpiCombo);
            qualityPanel.Children.Add(new TextBlock
            {
                Text = "150 DPI  빠른 확인용\n300 DPI  일반 출력 권장\n600 DPI  고품질 출력",
                Foreground = AppThemeManager.GetBrush("AppMutedTextBrush"),
                FontSize = 11,
                LineHeight = 20,
                Margin = new Thickness(0, 10, 0, 0)
            });
            qualityPanel.Children.Add(new TextBlock
            {
                Text = "직접 값을 입력할 수도 있습니다.\n허용 범위: 72~600 DPI",
                Foreground = AppThemeManager.GetBrush("AppMutedTextBrush"),
                FontSize = 10,
                Margin = new Thickness(0, 14, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            qualityPanel.Children.Add(new TextBlock
            {
                Text = "600 DPI는 페이지 수에 따라\n메모리와 시간이 많이 필요합니다.",
                Foreground = AppThemeManager.GetBrush("AppWarningTextBrush"),
                FontSize = 10,
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(qualityPanel);
            root.Children.Add(content);

            var actionButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(actionButtons, 2);
            var exportButton = new Button
            {
                Content = "내보내기",
                Style = AppThemeManager.GetStyle("PrimaryButtonStyle"),
                Width = 104,
                Height = 34,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            var cancelButton = new Button
            {
                Content = "취소",
                Width = 92,
                Height = 34,
                IsCancel = true
            };

            currentSheetButton.Click += (_, _) =>
            {
                for (int index = 0; index < sheetChecks.Count; index++)
                    sheetChecks[index].IsChecked = index == _currentSheetIndex;
            };
            allSheetsButton.Click += (_, _) =>
            {
                foreach (var check in sheetChecks)
                    check.IsChecked = true;
            };
            clearSheetsButton.Click += (_, _) =>
            {
                foreach (var check in sheetChecks)
                    check.IsChecked = false;
            };
            exportButton.Click += (_, _) =>
            {
                var selectedIndexes = sheetChecks
                    .Select((check, index) => new { check, index })
                    .Where(item => item.check.IsChecked == true)
                    .Select(item => item.index)
                    .ToList();
                if (selectedIndexes.Count == 0)
                {
                    MessageBox.Show(dialog, "내보낼 시트를 하나 이상 선택하세요.",
                        "PDF 내보내기 설정", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (!int.TryParse(dpiCombo.Text?.Trim(), out int dpi) || dpi < 72 || dpi > 600)
                {
                    MessageBox.Show(dialog, "DPI는 72에서 600 사이의 숫자로 입력하세요.",
                        "PDF 내보내기 설정", MessageBoxButton.OK, MessageBoxImage.Information);
                    dpiCombo.Focus();
                    return;
                }

                result = new PdfExportOptions
                {
                    SheetIndexes = selectedIndexes,
                    Dpi = dpi,
                    PaperName = paperSizeCombo.SelectedItem?.ToString() ?? "A3",
                    Landscape = string.Equals(
                        orientationCombo.SelectedItem?.ToString(),
                        "가로",
                        StringComparison.Ordinal)
                };
                dialog.DialogResult = true;
            };
            cancelButton.Click += (_, _) => dialog.DialogResult = false;

            actionButtons.Children.Add(exportButton);
            actionButtons.Children.Add(cancelButton);
            root.Children.Add(actionButtons);
            dialog.Content = root;
            dialog.ShowDialog();
            return result;
        }

        private void Menu_ExportPdf(object sender, RoutedEventArgs e)
        {
            if (_sheets.Count == 0) return;

            PdfExportOptions? options = ShowPdfExportSettingsDialog();
            if (options == null) return;

            string defaultName = _currentFilePath == null
                ? "ESDP_내보내기.pdf"
                : $"{Path.GetFileNameWithoutExtension(_currentFilePath)}.pdf";
            var dialog = new SaveFileDialog
            {
                Title = "PDF로 내보내기",
                Filter = "PDF 문서 (*.pdf)|*.pdf",
                DefaultExt = ".pdf",
                AddExtension = true,
                FileName = defaultName
            };
            if (dialog.ShowDialog(this) != true) return;

            CaptureCurrentSheet();
            int[] sheetIndexes = options.SheetIndexes.ToArray();
            var (outputWidthMm, outputHeightMm) = GetPdfOutputSize(options);
            var progressWindow = new PdfExportProgressWindow(this);
            bool previousEnabled = IsEnabled;
            var previousCursor = Cursor;
            bool exportSucceeded = false;
            Exception? exportError = null;
            MainWindow? exportView = null;

            progressWindow.Show();
            IsEnabled = false;
            Cursor = Cursors.Wait;
            try
            {
                PdfRasterLimits.GetSize(outputWidthMm, outputHeightMm, options.Dpi);
                // A detached, unshown window owns all export visuals and cloned models.
                // Do not ApplySheet on the editor: that repairs routes and clears selection.
                exportView = CreatePdfExportView();
                var pageCounts = new Dictionary<int, int>();
                int totalPages = 0;
                for (int position = 0; position < sheetIndexes.Length; position++)
                {
                    int sheetIndex = sheetIndexes[position];
                    progressWindow.SetPreparing(
                        $"시트 분석 중 ({position + 1}/{sheetIndexes.Length}) - {_sheets[sheetIndex].Name}");
                    exportView.ApplySheet(sheetIndex);
                    exportView.PrepareCurrentCanvasForPdfExport();
                    int pageCount = exportView.GetCurrentSheetPdfPageCount();
                    pageCounts[sheetIndex] = pageCount;
                    totalPages += pageCount;
                }

                using var document = new PdfSharp.Pdf.PdfDocument();
                document.Info.Title = Path.GetFileNameWithoutExtension(dialog.FileName);
                document.Info.Creator = "ESDP";

                int completedPages = 0;
                progressWindow.BeginExport(totalPages);
                foreach (int sheetIndex in sheetIndexes)
                {
                    exportView.ApplySheet(sheetIndex);
                    exportView.PrepareCurrentCanvasForPdfExport();
                    int sheetCompletedPages = 0;
                    int sheetPageCount = pageCounts[sheetIndex];
                    exportView.ExportCurrentSheetPages(
                        document,
                        options.Dpi,
                        outputWidthMm,
                        outputHeightMm,
                        () =>
                    {
                        completedPages++;
                        sheetCompletedPages++;
                        progressWindow.UpdateProgress(
                            completedPages,
                            totalPages,
                            _sheets[sheetIndex].Name,
                            sheetCompletedPages,
                            sheetPageCount);
                    });
                }

                progressWindow.SetSaving();
                string pdfTemp = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    document.Save(pdfTemp);
                    File.Move(pdfTemp, dialog.FileName, true);
                }
                finally { if (File.Exists(pdfTemp)) File.Delete(pdfTemp); }
                exportSucceeded = true;
            }
            catch (Exception ex)
            {
                exportError = ex;
            }
            finally
            {
                if (exportView != null)
                {
                    exportView._isDirtyValue = false;
                    exportView.Close();
                }
                Cursor = previousCursor;
                IsEnabled = previousEnabled;
                progressWindow.CloseAfterExport();
                Activate();
            }

            if (exportSucceeded)
            {
                StatusText.Text = $"PDF로 내보냄: {Path.GetFileName(dialog.FileName)}";
                MessageBox.Show(
                    $"PDF 내보내기가 완료되었습니다.\n{dialog.FileName}",
                    "ESDP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else if (exportError != null)
            {
                MessageBox.Show(
                    $"PDF로 내보내는 중 오류가 발생했습니다.\n{exportError.Message}",
                    "ESDP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private MainWindow CreatePdfExportView()
        {
            var view = new MainWindow { _isPdfExportRendering = true };
            view._deviceCategoryNames.Clear();
            view._deviceCategoryNames.AddRange(_deviceCategoryNames);
            view._deviceCategoryColors.Clear();
            foreach (var pair in _deviceCategoryColors) view._deviceCategoryColors[pair.Key] = pair.Value;
            view._historyReady = false;
            view._sheets.Clear();
            foreach (var sheet in _sheets) view._sheets.Add(DeepClone(sheet));
            foreach (var item in _registeredDevices)
                view._registeredDevices.Add(new DeviceListItem(DeepClone(item.Device)));
            foreach (var item in _registeredCables)
                view._registeredCables.Add(new CableListItem(DeepClone(item.Cable)));
            return view;
        }

        private void PrepareCurrentCanvasForPdfExport()
        {
            MainCanvas.Measure(new Size(MainCanvas.Width, MainCanvas.Height));
            MainCanvas.Arrange(new Rect(0, 0, MainCanvas.Width, MainCanvas.Height));
            MainCanvas.UpdateLayout();
        }

        private int GetCurrentSheetPdfPageCount()
        {
            var (pageWidth, pageHeight) = GetBasePaperPx();
            int columns = Math.Max(1, (int)Math.Round(MainCanvas.Width / pageWidth));
            int rows = Math.Max(1, (int)Math.Round(MainCanvas.Height / pageHeight));
            return columns * rows;
        }

        private static (double WidthMm, double HeightMm) GetPdfOutputSize(
            PdfExportOptions options)
        {
            double longSide = string.Equals(
                options.PaperName, "A4", StringComparison.OrdinalIgnoreCase)
                ? 297
                : 420;
            double shortSide = string.Equals(
                options.PaperName, "A4", StringComparison.OrdinalIgnoreCase)
                ? 210
                : 297;
            return options.Landscape
                ? (longSide, shortSide)
                : (shortSide, longSide);
        }

        private void ExportCurrentSheetPages(
            PdfSharp.Pdf.PdfDocument document,
            double exportDpi,
            double outputWidthMm,
            double outputHeightMm,
            Action pageCompleted)
        {
            PdfRasterLimits.GetSize(outputWidthMm, outputHeightMm, exportDpi);
            var (pageWidth, pageHeight) = GetBasePaperPx();
            int columns = Math.Max(1, (int)Math.Round(MainCanvas.Width / pageWidth));
            int rows = Math.Max(1, (int)Math.Round(MainCanvas.Height / pageHeight));

            var guides = MainCanvas.Children
                .OfType<FrameworkElement>()
                .Where(item => Equals(item.Tag, PageGuideTag))
                .ToList();

            var previousBackground = MainCanvas.Background;
            var previousPageBackground = PageBorder.Background;
            var gridBrush = FindResource("DiagramGridBrush") as DrawingBrush;
            double previousGridOpacity = gridBrush?.Opacity ?? 1;
            var guideVisibility = guides.Select(guide => guide.Visibility).ToArray();
            // A transparent canvas is composited onto the explicit white bitmap in
            // RenderCanvasRegionToPng. This prevents the tiled DrawingBrush from
            // being retained by WPF's VisualBrush cache during export.
            MainCanvas.Background = Brushes.Transparent;
            PageBorder.Background = Brushes.Transparent;
            if (gridBrush != null) gridBrush.Opacity = 0;
            foreach (var guide in guides)
                guide.Visibility = Visibility.Collapsed;
            MainCanvas.InvalidateVisual();
            PageBorder.InvalidateVisual();
            MainCanvas.UpdateLayout();

            try
            {
                for (int row = 0; row < rows; row++)
                for (int column = 0; column < columns; column++)
                {
                    var sourceRect = new Rect(
                        column * pageWidth,
                        row * pageHeight,
                        pageWidth,
                        pageHeight);
                    using (var imageStream = RenderCanvasRegionToPng(
                               sourceRect,
                               exportDpi,
                               outputWidthMm,
                               outputHeightMm))
                    using (var image = PdfSharp.Drawing.XImage.FromStream(imageStream))
                    {
                        var pdfPage = document.AddPage();
                        pdfPage.Width = PdfSharp.Drawing.XUnit.FromMillimeter(outputWidthMm);
                        pdfPage.Height = PdfSharp.Drawing.XUnit.FromMillimeter(outputHeightMm);

                        using var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(pdfPage);
                        graphics.DrawImage(
                            image,
                            0,
                            0,
                            pdfPage.Width.Point,
                            pdfPage.Height.Point);
                    }

                    pageCompleted();
                }
            }
            finally
            {
                MainCanvas.Background = previousBackground;
                PageBorder.Background = previousPageBackground;
                if (gridBrush != null) gridBrush.Opacity = previousGridOpacity;
                for (int i = 0; i < guides.Count; i++)
                    guides[i].Visibility = guideVisibility[i];
                MainCanvas.InvalidateVisual();
                PageBorder.InvalidateVisual();
            }
        }

        private MemoryStream RenderCanvasRegionToPng(
            Rect sourceRect,
            double dpi,
            double outputWidthMm,
            double outputHeightMm)
        {
            var (pixelWidth, pixelHeight) = PdfRasterLimits.GetSize(outputWidthMm, outputHeightMm, dpi);
            var targetRect = new Rect(0, 0, pixelWidth, pixelHeight);

            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, targetRect);
                var canvasBrush = new VisualBrush(MainCanvas)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = sourceRect,
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = targetRect,
                    Stretch = Stretch.Uniform,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                };
                drawing.DrawRectangle(canvasBrush, null, targetRect);
            }

            var bitmap = new RenderTargetBitmap(
                pixelWidth,
                pixelHeight,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var stream = new MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;
            return stream;
        }

    }
}
