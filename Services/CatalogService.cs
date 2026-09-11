using System.IO;
using System.Text.Json;
using WinMultiInstaller.Models;

namespace WinMultiInstaller.Services;

public class CatalogService
{
    public const string DefaultCatalogUrl = "https://raw.githubusercontent.com/wh0amitg/Multi-Win/main/Data/catalog.json";

    public const string MicrosoftWindowsDownloadPage = "https://www.microsoft.com/software-download/windows11";
    public const string FidoUrl = "https://github.com/pbatard/Fido";

    private static readonly string[] AllowedArch = { "x64", "x86", "arm64" };
    private const long MaxCatalogBytes = 2L * 1024 * 1024;

    private readonly string _path = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Data", "catalog.json");

    public List<WindowsImage> Load()
    {
        var devPath = Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Data", "catalog.json"));
        var file = File.Exists(_path) ? _path : devPath;
        if (!File.Exists(file)) return new();
        var json = File.ReadAllText(file);
        var items = JsonSerializer.Deserialize<List<WindowsImage>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        return items.Where(IsSane).ToList();
    }

    public async Task<int> UpdateFromUrlAsync(string url, CancellationToken ct = default)
    {
        url = (url ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Catalog URL must be https.");
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if ((resp.Content.Headers.ContentLength ?? 0) > MaxCatalogBytes)
            throw new IOException("Catalog file too large, refusing.");
        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (json.Length > MaxCatalogBytes)
            throw new IOException("Catalog file too large, refusing.");
        var items = JsonSerializer.Deserialize<List<WindowsImage>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new IOException("Catalog is empty or invalid.");
        if (items.Count == 0) throw new IOException("Catalog is empty.");
        foreach (var i in items)
        {
            if (string.IsNullOrWhiteSpace(i.Id) || string.IsNullOrWhiteSpace(i.Name))
                throw new IOException("Catalog entry without Id/Name.");
            if (!IsSane(i))
                throw new IOException($"Catalog entry '{i.Id}' failed validation.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path))
            File.Copy(_path, _path + ".bak", overwrite: true);
        await File.WriteAllTextAsync(_path, json, ct).ConfigureAwait(false);
        return items.Count;
    }

    private static bool IsSane(WindowsImage i)
    {
        if (string.IsNullOrWhiteSpace(i.Id) || string.IsNullOrWhiteSpace(i.Name)) return false;
        if (i.SizeBytes < 0 || i.MinRamMb < 0) return false;
        if (!string.IsNullOrWhiteSpace(i.DownloadUrl))
        {
            if (!Uri.TryCreate(i.DownloadUrl, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        }
        if (!string.IsNullOrWhiteSpace(i.Sha256))
        {
            string h = i.Sha256.Trim();
            if (h.Length != 64 || !h.All(c => Uri.IsHexDigit(c))) return false;
        }
        if (!string.IsNullOrWhiteSpace(i.Arch) && !AllowedArch.Contains(i.Arch)) return false;
        return true;
    }

    public static string LaunchMicrosoftFlow(Action<string> log)
    {
        try
        {
            string fido = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Fido.ps1");
            if (File.Exists(fido))
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powershell",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{fido}\"")
                {
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
                return "Fido launched — pick version, copy the link into the URL box.";
            }
        }
        catch (Exception ex) { log("Could not launch Fido: " + ex.Message); }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                MicrosoftWindowsDownloadPage) { UseShellExecute = true });
        }
        catch (Exception ex) { log("Could not open browser: " + ex.Message); }
        return "Browser opened (Microsoft download page). To get a direct retail link put " +
                "Assets\\Fido.ps1 from " + FidoUrl + " next to the exe — then this button runs it.";
    }
}
