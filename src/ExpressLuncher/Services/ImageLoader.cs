using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ExpressLuncher.Services;

public enum ImageKind
{
    /// <summary>Jaquette verticale affichée dans une carte.</summary>
    Cover,
    /// <summary>Grande image de fond.</summary>
    Hero,
    /// <summary>Logo transparent du jeu.</summary>
    Logo,
}

/// <summary>
/// Charge et décode les images hors du thread UI (bitmaps figés, réduits à la taille d'affichage),
/// avec un petit cache mémoire. Les demandes les plus récentes passent en premier : ce sont celles
/// des cartes actuellement à l'écran.
/// </summary>
public sealed class ImageLoader
{
    private const int MaxParallel = 4;
    private const int CoverCacheSize = 220;
    private const int LargeCacheSize = 10;
    private const int MaxRemoteBytes = 12 * 1024 * 1024;

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly object _gate = new();
    private readonly LinkedList<Func<Task>> _pending = new();
    private int _running;

    private readonly LruCache _covers = new(CoverCacheSize);
    private readonly LruCache _large = new(LargeCacheSize);

    /// <summary>Largeur de décodage des jaquettes, en pixels physiques.</summary>
    public int CoverDecodeWidth { get; set; } = 240;

    /// <summary>Autorise le chargement direct des images dont le chemin est une URL http(s).</summary>
    public bool AllowRemote { get; set; } = true;

    public Task<BitmapSource?> LoadAsync(string path, ImageKind kind)
    {
        var cache = kind == ImageKind.Cover ? _covers : _large;
        var key = $"{kind}|{path}";
        if (cache.TryGet(key, out var cached)) return Task.FromResult<BitmapSource?>(cached);

        return Enqueue(async () =>
        {
            var image = await LoadCoreAsync(path, kind).ConfigureAwait(false);
            if (image is not null) cache.Add(key, image);
            return image;
        });
    }

    /// <summary>Icône (128 px) de l'exécutable, ou null s'il n'en a pas.</summary>
    public Task<BitmapSource?> LoadIconAsync(string executablePath) =>
        Enqueue(() => Task.FromResult(ExtractIcon(executablePath)));

    private Task<BitmapSource?> Enqueue(Func<Task<BitmapSource?>> work)
    {
        var completion = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending.AddFirst(async () =>
            {
                try
                {
                    completion.TrySetResult(await work().ConfigureAwait(false));
                }
                catch
                {
                    completion.TrySetResult(null);
                }
            });
        }
        Pump();
        return completion.Task;
    }

    private void Pump()
    {
        while (true)
        {
            Func<Task> job;
            lock (_gate)
            {
                if (_running >= MaxParallel || _pending.First is null) return;
                job = _pending.First.Value;
                _pending.RemoveFirst();
                _running++;
            }

            Task.Run(async () =>
            {
                try
                {
                    await job().ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate) _running--;
                    Pump();
                }
            });
        }
    }

    private async Task<BitmapSource?> LoadCoreAsync(string path, ImageKind kind)
    {
        try
        {
            if (IsRemote(path))
            {
                if (!AllowRemote) return null;

                using var response = await Http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxRemoteBytes) return null;

                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length == 0 || bytes.Length > MaxRemoteBytes) return null;
                using var memory = new MemoryStream(bytes, writable: false);
                return Decode(memory, kind);
            }

            var file = ToLocalPath(path);
            if (!File.Exists(file)) return null;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Decode(stream, kind);
        }
        catch
        {
            return null;
        }
    }

    private BitmapSource Decode(Stream stream, ImageKind kind)
    {
        // Premier passage : dimensions seules, pour choisir l'axe de réduction sans jamais agrandir.
        var probe = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        var width = probe.Frames[0].PixelWidth;
        var height = probe.Frames[0].PixelHeight;
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

        switch (kind)
        {
            case ImageKind.Cover:
                var targetWidth = CoverDecodeWidth;
                var targetHeight = targetWidth * 3 / 2;
                // Image plus large que 2:3 : elle sera rognée sur les côtés, la hauteur fait donc foi.
                if ((double)width / height > 2.0 / 3.0)
                {
                    if (height > targetHeight) image.DecodePixelHeight = targetHeight;
                }
                else if (width > targetWidth)
                {
                    image.DecodePixelWidth = targetWidth;
                }
                break;

            case ImageKind.Hero:
                if (width > 1920) image.DecodePixelWidth = 1920;
                break;

            case ImageKind.Logo:
                if (width > 720) image.DecodePixelWidth = 720;
                break;
        }

        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource? ExtractIcon(string executablePath)
    {
        try
        {
            if (!File.Exists(executablePath)) return null;

            using var icon = System.Drawing.Icon.ExtractIcon(executablePath, 0, 128);
            if (icon is null) return null;

            using var bitmap = icon.ToBitmap();
            var bounds = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(bounds, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var source = BitmapSource.Create(
                    data.Width, data.Height, 96, 96, PixelFormats.Bgra32, null,
                    data.Scan0, data.Stride * data.Height, data.Stride);
                source.Freeze();
                return source;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        catch
        {
            return null;
        }
    }

    public static bool IsRemote(string? path) =>
        path is not null &&
        (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static string ToLocalPath(string path) =>
        path.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(path, UriKind.Absolute, out var uri)
            ? uri.LocalPath
            : path;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ExpressLuncher");
        return client;
    }

    /// <summary>Cache LRU minimal, thread-safe.</summary>
    private sealed class LruCache(int capacity)
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapSource>>> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<KeyValuePair<string, BitmapSource>> _order = new();

        public bool TryGet(string key, out BitmapSource? image)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    image = node.Value.Value;
                    return true;
                }
            }
            image = null;
            return false;
        }

        public void Add(string key, BitmapSource image)
        {
            lock (_lock)
            {
                if (_map.Remove(key, out var existing)) _order.Remove(existing);
                _map[key] = _order.AddFirst(new KeyValuePair<string, BitmapSource>(key, image));

                while (_map.Count > capacity && _order.Last is { } last)
                {
                    _order.RemoveLast();
                    _map.Remove(last.Value.Key);
                }
            }
        }
    }
}
