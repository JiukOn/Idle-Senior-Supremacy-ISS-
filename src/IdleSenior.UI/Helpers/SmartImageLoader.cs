using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdleSenior.UI.Helpers
{
    /// <summary>
    /// High-performance image loader for WPF <see cref="Image"/> controls featuring:
    /// 1. Multi-URL fallback chain (Primary -> Secondary -> Tertiary -> Vector Placeholder)
    /// 2. Two-level cache: In-Memory frozen BitmapSource + Persistent Disk Cache (%LocalAppData%)
    /// 3. Memory-optimized decoding via DecodePixelWidth
    /// 4. Throttled concurrent HTTP downloads to prevent socket exhaustion and UI stutter
    /// </summary>
    public static class SmartImageLoader
    {
        private static readonly HttpClient HttpClient = CreateHttpClient();
        private static readonly SemaphoreSlim DownloadGate = new(12, 12);

        // Cache key: "{cacheKey}_{decodeWidth}" -> frozen BitmapSource
        private static readonly ConcurrentDictionary<string, BitmapSource> MemoryCache = new(StringComparer.OrdinalIgnoreCase);

        // Negative cache for URLs that returned 404 in this session so we skip straight to the secondary URL
        private static readonly ConcurrentDictionary<string, byte> FailedUrls = new(StringComparer.OrdinalIgnoreCase);

        // In-flight deduplication so two controls requesting the same image share one download task
        private static readonly ConcurrentDictionary<string, Task<byte[]?>> InFlightDownloads = new(StringComparer.OrdinalIgnoreCase);

        private static readonly string DiskCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IdleSeniorSupremacy",
            "cache",
            "images");

        private static readonly DrawingImage FallbackPlaceholder = CreatePlaceholderImage();

        static SmartImageLoader()
        {
            try { Directory.CreateDirectory(DiskCacheDir); } catch { }
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                MaxConnectionsPerServer = 16,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(7)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            return client;
        }

        // ── Attached Properties ───────────────────────────────────────────────────

        public static async Task PreloadImageAsync(IEnumerable<string>? urls)
        {
            if (urls == null) return;
            var list = urls.ToList();
            if (list.Count == 0) return;

            string groupHash = ComputeHash(string.Join("|", list));
            string diskPath = Path.Combine(DiskCacheDir, $"{groupHash}.bin");
            if (File.Exists(diskPath)) return;

            foreach (var url in list)
            {
                if (string.IsNullOrWhiteSpace(url) || FailedUrls.ContainsKey(url)) continue;
                byte[]? bytes = await DownloadBytesDeduplicatedAsync(url).ConfigureAwait(false);
                if (bytes != null && bytes.Length > 128)
                {
                    try
                    {
                        Directory.CreateDirectory(DiskCacheDir);
                        await File.WriteAllBytesAsync(diskPath, bytes).ConfigureAwait(false);
                        return;
                    }
                    catch { }
                }
                else
                {
                    FailedUrls[url] = 1;
                }
            }
        }

        public static readonly DependencyProperty FallbackUrlsProperty =
            DependencyProperty.RegisterAttached(
                "FallbackUrls",
                typeof(IEnumerable<string>),
                typeof(SmartImageLoader),
                new PropertyMetadata(null, OnFallbackUrlsChanged));

        public static void SetFallbackUrls(DependencyObject element, IEnumerable<string>? value) =>
            element.SetValue(FallbackUrlsProperty, value);

        public static IEnumerable<string>? GetFallbackUrls(DependencyObject element) =>
            (IEnumerable<string>?)element.GetValue(FallbackUrlsProperty);

        public static readonly DependencyProperty DecodeWidthProperty =
            DependencyProperty.RegisterAttached(
                "DecodeWidth",
                typeof(int),
                typeof(SmartImageLoader),
                new PropertyMetadata(280));

        public static void SetDecodeWidth(DependencyObject element, int value) =>
            element.SetValue(DecodeWidthProperty, value);

        public static int GetDecodeWidth(DependencyObject element) =>
            (int)element.GetValue(DecodeWidthProperty);

        // Tracks the latest request version per Image control to prevent recycled virtualized containers from showing stale images
        private static readonly DependencyProperty RequestVersionProperty =
            DependencyProperty.RegisterAttached(
                "RequestVersion",
                typeof(int),
                typeof(SmartImageLoader),
                new PropertyMetadata(0));

        private static void OnFallbackUrlsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Image image) return;

            var urls = (e.NewValue as IEnumerable<string>)
                ?.Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            int decodeWidth = GetDecodeWidth(image);
            _ = LoadIntoInternalAsync(image, urls, decodeWidth);
        }

        /// <summary>
        /// Programmatically loads an image using an ordered list of fallback URLs.
        /// </summary>
        public static Task LoadIntoAsync(Image image, IEnumerable<string>? candidateUrls, int decodeWidth = 360)
        {
            var urls = candidateUrls
                ?.Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return LoadIntoInternalAsync(image, urls, decodeWidth);
        }

        /// <summary>
        /// Programmatically loads an image for a given AppId using standard Steam Banner fallback URLs.
        /// </summary>
        public static Task LoadAppBannerAsync(Image image, int appId, string? primaryUrl = null, int decodeWidth = 400)
        {
            var urls = new List<string>(5);
            if (!string.IsNullOrWhiteSpace(primaryUrl))
                urls.Add(primaryUrl!);

            if (appId > 0)
            {
                urls.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg");
                urls.Add($"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg");
                urls.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/capsule_231x87.jpg");
                urls.Add($"https://steamcdn-a.akamaihd.net/steam/apps/{appId}/header.jpg");
            }

            return LoadIntoAsync(image, urls, decodeWidth);
        }

        private static async Task LoadIntoInternalAsync(Image image, List<string>? urls, int decodeWidth)
        {
            int version = (int)image.GetValue(RequestVersionProperty) + 1;
            image.SetValue(RequestVersionProperty, version);

            if (urls == null || urls.Count == 0)
            {
                image.Source = FallbackPlaceholder;
                return;
            }

            // Use the primary URL hash as the logical group key
            string groupHash = ComputeHash(urls[0]);
            string memKey = $"{groupHash}_{decodeWidth}";

            // 1. Check In-Memory Cache (instant, 0ms)
            if (MemoryCache.TryGetValue(memKey, out var cachedBitmap))
            {
                image.Source = cachedBitmap;
                return;
            }

            // Show subtle placeholder while loading asynchronously
            image.Source = FallbackPlaceholder;

            // 2. Check Disk Cache or Download with Fallback Chain on background thread
            BitmapSource? loaded = await Task.Run(() => FetchWithFallbackAndDecodeAsync(urls, groupHash, decodeWidth));

            // Ensure the Image control hasn't been reassigned to another item while we were awaiting
            if ((int)image.GetValue(RequestVersionProperty) != version)
                return;

            if (loaded != null)
            {
                MemoryCache[memKey] = loaded;
                image.Source = loaded;
            }
            else
            {
                image.Source = FallbackPlaceholder;
            }
        }

        private static async Task<BitmapSource?> FetchWithFallbackAndDecodeAsync(
            List<string> urls,
            string groupHash,
            int decodeWidth)
        {
            string diskPath = Path.Combine(DiskCacheDir, $"{groupHash}.bin");

            // Step A: Try reading from local disk cache first
            if (File.Exists(diskPath))
            {
                try
                {
                    byte[] cachedBytes = await File.ReadAllBytesAsync(diskPath).ConfigureAwait(false);
                    var bmp = DecodeBitmap(cachedBytes, decodeWidth);
                    if (bmp != null) return bmp;
                }
                catch { }
            }

            // Step B: Iterate through the fallback URL chain (Primary -> Secondary -> Tertiary...)
            foreach (string url in urls)
            {
                if (FailedUrls.ContainsKey(url))
                    continue;

                byte[]? bytes = await DownloadBytesDeduplicatedAsync(url).ConfigureAwait(false);
                if (bytes != null && bytes.Length > 128)
                {
                    var bmp = DecodeBitmap(bytes, decodeWidth);
                    if (bmp != null)
                    {
                        // Save to disk cache asynchronously for future runs
                        try
                        {
                            Directory.CreateDirectory(DiskCacheDir);
                            await File.WriteAllBytesAsync(diskPath, bytes).ConfigureAwait(false);
                        }
                        catch { }

                        return bmp;
                    }
                }

                // Mark this URL as failed so future checks skip directly to the secondary option
                FailedUrls[url] = 1;
            }

            return null;
        }

        private static Task<byte[]?> DownloadBytesDeduplicatedAsync(string url)
        {
            return InFlightDownloads.GetOrAdd(url, async targetUrl =>
            {
                await DownloadGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var response = await HttpClient.GetAsync(targetUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return null;

                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SmartImageLoader] Fallback triggered for {targetUrl}: {ex.Message}");
                    return null;
                }
                finally
                {
                    DownloadGate.Release();
                    InFlightDownloads.TryRemove(targetUrl, out _);
                }
            });
        }

        private static BitmapSource? DecodeBitmap(byte[] bytes, int decodeWidth)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0)
                    bmp.DecodePixelWidth = decodeWidth;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static string ComputeHash(string input)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes)[..20];
        }

        private static DrawingImage CreatePlaceholderImage()
        {
            var group = new DrawingGroup();

            // Dark Steam-style gradient background
            var bgBrush = new LinearGradientBrush(
                Color.FromRgb(26, 32, 44),
                Color.FromRgb(17, 21, 30),
                new Point(0, 0),
                new Point(1, 1));

            group.Children.Add(new GeometryDrawing(
                bgBrush,
                null,
                new RectangleGeometry(new Rect(0, 0, 200, 100))));

            // Subtle controller/disc emblem in the center
            var circlePen = new Pen(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), 2.0);
            group.Children.Add(new GeometryDrawing(
                null,
                circlePen,
                new EllipseGeometry(new Point(100, 50), 16, 16)));

            group.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)),
                null,
                new EllipseGeometry(new Point(100, 50), 5, 5)));

            var img = new DrawingImage(group);
            img.Freeze();
            return img;
        }
    }
}
