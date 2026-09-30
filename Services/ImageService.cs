using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace WinMultiInstaller.Services;

public class ImageService
{
    private static HttpClient CreateClient()
    {
        var h = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        var c = new HttpClient(h, disposeHandler: true)
        {

            Timeout = Timeout.InfiniteTimeSpan,
        };


        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return c;
    }

    private static bool IsMicrosoftHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        u.Host.EndsWith("microsoft.com", StringComparison.OrdinalIgnoreCase);

    private static void ApplyMicrosoftHeaders(HttpRequestMessage req) =>
        req.Headers.Referrer = new Uri("https://www.microsoft.com/software-download/windows11");

    private static void ThrowIfHtmlPage(HttpResponseMessage resp, string url)
    {
        string media = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (media.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            throw new IOException(
                "Server returned an HTML page instead of the ISO file. " +
                "Microsoft download links are session-bound and expire quickly — " +
                "generate a fresh link and paste it again.");
    }

    private static bool TryGetDriveId(string url, out string id)
    {
        id = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        string host = u.Host.ToLowerInvariant();
        if (host != "drive.google.com" && host != "docs.google.com" &&
            !host.EndsWith(".drive.google.com") && !host.EndsWith("drive.usercontent.google.com"))
            return false;
        var m = Regex.Match(u.Query, @"[?&]id=([^&]+)");
        if (m.Success) { id = Uri.UnescapeDataString(m.Groups[1].Value); return true; }
        m = Regex.Match(u.AbsolutePath, @"/file/d/([^/]+)");
        if (m.Success) { id = m.Groups[1].Value; return true; }
        return false;
    }

    public static bool IsGoogleDriveUrl(string url) => TryGetDriveId(url, out _);

    public static bool TryGetDriveFileId(string url, out string id) => TryGetDriveId(url, out id);



    private static async Task<string> ResolveDriveUrlAsync(HttpClient http, string url, CancellationToken ct)
    {
        if (!TryGetDriveId(url, out string id)) return url;
        string direct = $"https://drive.usercontent.google.com/download?id={Uri.EscapeDataString(id)}&export=download&confirm=t";
        using (var probe = new HttpRequestMessage(HttpMethod.Get, direct))
        {
            probe.Headers.Range = new RangeHeaderValue(0, 0);
            using var pr = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            string media = pr.Content.Headers.ContentType?.MediaType ?? "";
            if ((pr.StatusCode == HttpStatusCode.OK || pr.StatusCode == HttpStatusCode.PartialContent) &&
                !media.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                return direct;
        }
        string pageUrl = $"https://drive.google.com/uc?export=download&id={Uri.EscapeDataString(id)}";
        using var pageReq = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        using var pageResp = await http.SendAsync(pageReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        pageResp.EnsureSuccessStatusCode();
        string html = await pageResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var m = Regex.Match(html, @"(/uc\?export=download[^""'\s<>]*confirm[^""'\s<>]*)");
        string confirmUrl;
        if (m.Success)
            confirmUrl = "https://drive.google.com" + WebUtility.HtmlDecode(m.Groups[1].Value);
        else if ((m = Regex.Match(html, @"confirm=([0-9A-Za-z_\-]+)")).Success)
            confirmUrl = $"https://drive.google.com/uc?export=download&confirm={m.Groups[1].Value}&id={Uri.EscapeDataString(id)}";
        else
            throw new IOException(
                "Google Drive asked for a download confirmation this tool can't pass. " +
                "Open the link in a browser once, or check the file sharing settings.");
        return confirmUrl;
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
        url = await ResolveDriveUrlAsync(http, url, ct).ConfigureAwait(false);
        long existing = 0;
        try { if (File.Exists(destPath)) existing = new FileInfo(destPath).Length; } catch { existing = 0; }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (IsMicrosoftHost(url)) ApplyMicrosoftHeaders(req);
        if (existing > 0)
            req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (existing > 0 && resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {

            existing = 0;
            await DownloadOnceFreshAsync(url, destPath, progress, ct).ConfigureAwait(false);
            return;
        }
        if (existing > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
        {

            existing = 0;
        }
        else resp.EnsureSuccessStatusCode();
        ThrowIfHtmlPage(resp, url);

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
        url = await ResolveDriveUrlAsync(http, url, ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (IsMicrosoftHost(url)) ApplyMicrosoftHeaders(req);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        ThrowIfHtmlPage(resp, url);
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

