using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace WinMultiInstaller.Services;

public class ImageService
{
    private readonly HttpClient _http = new();

    public record DownloadProgress(
        long DownloadedBytes, long TotalBytes, double BytesPerSecond, double Percent);

    public async Task DownloadAsync(string url, string destPath,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(destPath);
        var buf = new byte[81920];
        long read = 0;
        long sinceReport = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            sinceReport += n;
            if (sinceReport >= 256 * 1024 || n == 0)
            {
                var bps = sw.Elapsed.TotalSeconds > 0 ? read / sw.Elapsed.TotalSeconds : 0;
                var pct = total > 0 ? (double)read / total * 100 : -1;
                progress?.Report(new DownloadProgress(read, total, bps, pct));
                sinceReport = 0;
            }
        }
        sw.Stop();
        var finalBps = sw.Elapsed.TotalSeconds > 0 ? read / sw.Elapsed.TotalSeconds : 0;
        progress?.Report(new DownloadProgress(read, total > 0 ? total : read, finalBps, 100));
    }

    public static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
