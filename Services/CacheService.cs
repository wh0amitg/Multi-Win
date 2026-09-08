using System.IO;

namespace WinMultiInstaller.Services;

public static class CacheService
{
    public static string CacheDir
    {
        get
        {
            string dir = Path.Combine(Path.GetTempPath(), "Multi-Win");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static List<(string Path, long Size)> ListCachedIsos()
    {
        string dir = CacheDir;
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir, "*.iso")
            .Select(f => (Path: f, Size: SafeLen(f)))
            .OrderByDescending(x => x.Size)
            .ToList();
    }

    public static long CacheSizeBytes()
    {
        long total = 0;
        foreach (var (p, s) in ListCachedIsos()) total += s;
        return total;
    }

    public static int ClearCache(Action<string>? log = null)
    {
        int n = 0;
        foreach (var (p, _) in ListCachedIsos())
        {
            try { File.Delete(p); n++; }
            catch (Exception ex) { log?.Invoke($"Could not delete {Path.GetFileName(p)}: {ex.Message}"); }
        }
        foreach (var f in Directory.GetFiles(CacheDir, "*.conv.wim"))
        {
            try { File.Delete(f); } catch { }
        }
        return n;
    }

    public static string FormatBytes(long b) => UsbService.Fmt(b);

    private static long SafeLen(string f)
    {
        try { return new FileInfo(f).Length; } catch { return 0; }
    }
}
