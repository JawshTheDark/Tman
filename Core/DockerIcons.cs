using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tman.Core;

/// <param name="Image">The icon, ready to draw on any thread.</param>
/// <param name="NeedsLightTile">The artwork is mostly dark, and would vanish on Tman's dark background.</param>
public sealed record DockerIcon(ImageSource Image, bool NeedsLightTile);

/// <summary>
/// Fetches app icons named by template lists, and keeps them on disk. The addresses come from strangers' files, so only
/// plain images from public https sites are accepted, nothing is shown until it decodes, and a broken one costs nothing.
/// WPF can't draw SVG, so those apps keep their lettered tile.
/// </summary>
public static class DockerIcons
{
    const int ByteLimit = 1024 * 1024, PixelLimit = 4096, DrawnSize = 96;
    const int Simultaneous = 8;

    static readonly TimeSpan RetryAfter = TimeSpan.FromDays(14);
    static readonly HttpClient Http = CreateClient();
    static readonly SemaphoreSlim Gate = new(Simultaneous);
    static readonly ConcurrentDictionary<string, Lazy<Task<DockerIcon?>>> Known = new();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Tman");
        return client;
    }

    static string Folder => Path.Combine(DockerTemplates.DataFolder, "docker-icons");

    /// <summary>The first of these addresses that gives a usable picture, or null. Each app is only fetched once.</summary>
    public static Task<DockerIcon?> GetAsync(IReadOnlyList<string> urls)
    {
        if (urls.Count == 0) return Task.FromResult<DockerIcon?>(null);
        return Known.GetOrAdd(string.Join('\n', urls), _ => new Lazy<Task<DockerIcon?>>(() => Task.Run(() => FirstUsable(urls)))).Value;
    }

    static async Task<DockerIcon?> FirstUsable(IReadOnlyList<string> urls)
    {
        foreach (var url in urls)
        {
            if (await LoadOne(url).ConfigureAwait(false) is { } icon) return icon;
        }
        return null;
    }

    /// <summary>Https only, and only named public sites, so a list can't use Tman to poke at this PC or the local network.</summary>
    public static bool IsAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.HostNameType != UriHostNameType.Dns || !uri.IsDefaultPort || !uri.Host.Contains('.')) return false;
        var host = uri.Host.ToLowerInvariant();
        if (host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".lan") || host.EndsWith(".internal")) return false;
        return !uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
    }

    static async Task<DockerIcon?> LoadOne(string url)
    {
        if (!IsAllowed(url)) return null;
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32];
        string picture = Path.Combine(Folder, name + ".img"), failed = Path.Combine(Folder, name + ".none");
        try
        {
            if (File.Exists(picture) && Decode(await File.ReadAllBytesAsync(picture).ConfigureAwait(false)) is { } saved) return saved;
            if (File.Exists(failed) && DateTime.Now - File.GetLastWriteTime(failed) < RetryAfter) return null;

            var bytes = await Download(url).ConfigureAwait(false);
            var icon = bytes == null ? null : Decode(bytes);
            Directory.CreateDirectory(Folder);
            if (icon != null) await File.WriteAllBytesAsync(picture, bytes!).ConfigureAwait(false);
            else await File.WriteAllTextAsync(failed, "").ConfigureAwait(false);
            return icon;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            return null; // Probably the network, not the icon, so it gets another chance next time.
        }
    }

    /// <summary>The file, or null if the site says it's gone or it's too large. Connection trouble throws instead.</summary>
    static async Task<byte[]?> Download(string url)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError)
                throw new HttpRequestException("Try later.");
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > ByteLimit) return null;
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                bytes.Write(buffer, 0, read);
                if (bytes.Length > ByteLimit) return null;
            }
            return bytes.ToArray();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Only formats named by their first bytes reach the decoder, whatever the address or the site claims.</summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> b)
    {
        if (b.Length < 12) return false;
        return b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
            || (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            || b[..3].SequenceEqual("GIF"u8)
            || (b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WEBP"u8))
            || (b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0);
    }

    /// <summary>A small frozen copy of the picture, or null if it isn't one. Reads the size first, so a huge image is never unpacked.</summary>
    public static DockerIcon? Decode(byte[] bytes)
    {
        if (!LooksLikeImage(bytes)) return null;
        try
        {
            using (var header = new MemoryStream(bytes))
            {
                var frame = BitmapFrame.Create(header, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                if (frame.PixelWidth is < 1 or > PixelLimit || frame.PixelHeight is < 1 or > PixelLimit) return null;
            }
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = DrawnSize;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return new DockerIcon(image, IsMostlyDark(image));
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or IOException or System.Runtime.InteropServices.COMException or OverflowException)
        {
            return null;
        }
    }

    static bool IsMostlyDark(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        double light = 0, weight = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            double alpha = pixels[i + 3] / 255.0;
            light += alpha * (0.11 * pixels[i] + 0.59 * pixels[i + 1] + 0.30 * pixels[i + 2]) / 255.0;
            weight += alpha;
        }
        return weight > 0 && light / weight < 0.38;
    }
}
