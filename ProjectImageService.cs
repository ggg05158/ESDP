using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ESDP
{
    internal sealed class ProjectImageData
    {
        public string Base64 { get; init; } = "";
        public string FileName { get; init; } = "";
        public int PixelWidth { get; init; }
        public int PixelHeight { get; init; }
    }

    internal static class ProjectImageService
    {
        private const int OptimizedMaximumDimension = 2048;
        private const long MaximumCacheBytes = 64L * 1024 * 1024;
        private sealed record ImageKey(string Hash);
        private sealed record CacheEntry(BitmapSource Image, long Bytes, LinkedListNode<string> Node);
        private static readonly ConditionalWeakTable<string, ImageKey> ImageKeys = new();
        private static readonly Dictionary<string, CacheEntry> ImageCache = new();
        private static readonly LinkedList<string> CacheOrder = new();
        private static long _cacheBytes;

        public static bool TryImport(Window owner, out ProjectImageData image)
        {
            image = new ProjectImageData();
            var dialog = new OpenFileDialog
            {
                Title = "프로젝트에 포함할 이미지 선택",
                Filter = "이미지 파일|*.png;*.jpg;*.jpeg;*.bmp;*.gif|모든 파일|*.*"
            };
            if (dialog.ShowDialog(owner) != true) return false;

            try
            {
                byte[] original = File.ReadAllBytes(dialog.FileName);
                BitmapFrame frame = Decode(original);
                string sizeText = FormatBytes(original.LongLength);
                var choice = MessageBox.Show(owner,
                    $"파일: {Path.GetFileName(dialog.FileName)}\n" +
                    $"해상도: {frame.PixelWidth:N0} × {frame.PixelHeight:N0}px\n" +
                    $"용량: {sizeText}\n\n" +
                    "예: 프로젝트 용량을 줄이도록 최적화해서 포함\n" +
                    "아니요: 원본 화질과 원본 데이터를 그대로 포함\n" +
                    "취소: 이미지 삽입 취소",
                    "이미지 품질 선택", MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);
                if (choice == MessageBoxResult.Cancel) return false;

                byte[] stored = choice == MessageBoxResult.Yes
                    ? Optimize(frame, Path.GetExtension(dialog.FileName))
                    : original;
                BitmapFrame storedFrame = Decode(stored);
                image = new ProjectImageData
                {
                    Base64 = Convert.ToBase64String(stored),
                    FileName = Path.GetFileName(dialog.FileName),
                    PixelWidth = storedFrame.PixelWidth,
                    PixelHeight = storedFrame.PixelHeight
                };
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner,
                    $"이미지를 불러오지 못했습니다.\n{ex.Message}",
                    "이미지 삽입", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        public static BitmapSource? Load(string? base64)
        {
            if (string.IsNullOrWhiteSpace(base64)) return null;
            string key = ImageKeys.GetValue(base64, value => new ImageKey(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))))).Hash;
            lock (ImageCache)
            {
                if (ImageCache.TryGetValue(key, out CacheEntry? cached))
                {
                    CacheOrder.Remove(cached.Node);
                    CacheOrder.AddLast(cached.Node);
                    return cached.Image;
                }
            }
            try
            {
                BitmapFrame frame = Decode(Convert.FromBase64String(base64));
                frame.Freeze();
                long bytes = ((long)frame.PixelWidth * Math.Max(32, frame.Format.BitsPerPixel) + 7) / 8 * frame.PixelHeight;
                lock (ImageCache)
                {
                    if (ImageCache.TryGetValue(key, out CacheEntry? concurrent))
                        return concurrent.Image;
                    if (bytes <= MaximumCacheBytes)
                    {
                        while ((_cacheBytes + bytes > MaximumCacheBytes || ImageCache.Count >= 128) &&
                               CacheOrder.First != null)
                        {
                            string oldest = CacheOrder.First.Value;
                            _cacheBytes -= ImageCache[oldest].Bytes;
                            ImageCache.Remove(oldest);
                            CacheOrder.RemoveFirst();
                        }
                        var node = CacheOrder.AddLast(key);
                        ImageCache.Add(key, new CacheEntry(frame, bytes, node));
                        _cacheBytes += bytes;
                    }
                }
                return frame;
            }
            catch
            {
                return null;
            }
        }

        public static Stretch ParseStretch(string? value) => value switch
        {
            "Fill" => Stretch.Fill,
            "UniformToFill" => Stretch.UniformToFill,
            _ => Stretch.Uniform
        };

        private static BitmapFrame Decode(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        }

        private static byte[] Optimize(BitmapFrame source, string extension)
        {
            double scale = Math.Min(1,
                (double)OptimizedMaximumDimension /
                Math.Max(source.PixelWidth, source.PixelHeight));
            BitmapSource resized = source;
            if (scale < 0.999)
            {
                resized = new TransformedBitmap(source,
                    new ScaleTransform(scale, scale));
                resized.Freeze();
            }

            BitmapEncoder encoder;
            if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                encoder = new JpegBitmapEncoder { QualityLevel = 88 };
            else
                encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(resized));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L)
                return $"{bytes / 1024d / 1024d:0.##} MB";
            if (bytes >= 1024L) return $"{bytes / 1024d:0.##} KB";
            return $"{bytes:N0} B";
        }
    }
}
