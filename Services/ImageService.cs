using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace WinMultiInstaller.Services;

public class ImageService
{
    private static HttpClient CreateClient()
    {
        var h = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        var c = new HttpClient(h, disposeHandler: true)
        {
            // Downloads are multi-GB; per-request cancellation is driven by the token.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Multi-Win/0.6");
        return c;
    }

    public record DownloadProgress(
        long DownloadedBytes, long TotalBytes, double BytesPerSecond, double Percent);

    public async Task DownloadAsync(string url, string destPath,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Download URL must be http(s).");

        const int maxAttempts = 3;
        Exception? last = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, destPath, progress, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
        }
        throw last ?? new IOException("Download failed.");
    }

    private static async Task DownloadOnceAsync(string url, string destPath,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var http = CreateClient();
        long existing = 0;
        try { if (File.Exists(destPath)) existing = new FileInfo(destPath).Length; } catch { existing = 0; }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (existing > 0 && resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Server can't resume; restart from scratch.
            existing = 0;
            await DownloadOnceFreshAsync(url, destPath, progress, ct).ConfigureAwait(false);
            return;
        }
        if (existing > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
        {
            // Server ignored Range; restart to avoid a corrupt mix.
            existing = 0;
        }
        else resp.EnsureSuccessStatusCode();

        var total = (resp.Content.Headers.ContentLength ?? -1L);
        long fullTotal = existing + (total >= 0 ? total : 0);
        if (total < 0 && existing == 0) fullTotal = -1;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(destPath,
            existing > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);
        var buf = new byte[81920];
        long read = existing;
        long sinceReport = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            sinceReport += n;
            if (sinceReport >= 256 * 1024)
            {
                var bps = sw.Elapsed.TotalSeconds > 0 ? (read - existing) / sw.Elapsed.TotalSeconds : 0;
                var pct = fullTotal > 0 ? (double)read / fullTotal * 100 : -1;
                progress?.Report(new DownloadProgress(read, fullTotal, bps, pct));
                sinceReport = 0;
            }
        }
        sw.Stop();
        await dst.FlushAsync(ct).ConfigureAwait(false);
        var finalBps = sw.Elapsed.TotalSeconds > 0 ? (read - existing) / sw.Elapsed.TotalSeconds : 0;
        progress?.Report(new DownloadProgress(read, fullTotal > 0 ? fullTotal : read, finalBps, 100));
    }

    private static async Task DownloadOnceFreshAsync(string url, string destPath,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
        using var http = CreateClient();
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(destPath);
        var buf = new byte[81920];
        long read = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            var pct = total > 0 ? (double)read / total * 100 : -1;
            progress?.Report(new DownloadProgress(read, total, read / Math.Max(sw.Elapsed.TotalSeconds, 0.001), pct));
        }
        progress?.Report(new DownloadProgress(read, total > 0 ? total : read, read / Math.Max(sw.Elapsed.TotalSeconds, 0.001), 100));
    }

    public static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    public static string Sha256Of(Stream s)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(s)).ToLowerInvariant();
    }
}
