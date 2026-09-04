using System.IO;

namespace WinMultiInstaller.Services;

public static class OsIconProvider
{
    public static string? Resolve(string? iconKey, string? family)
    {
        var keys = new List<string>();
        if (!string.IsNullOrWhiteSpace(iconKey)) keys.Add(iconKey!);
        keys.Add(family == "Windows" ? "windows" : family == "Linux" ? "linux" : "generic");
        foreach (string dir in AssetDirs())
            foreach (string k in keys.Distinct())
            {
                string p = Path.Combine(dir, k + ".png");
                if (File.Exists(p)) return p;
            }
        return null;
    }

    private static IEnumerable<string> AssetDirs()
    {
        yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
        yield return Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets"));
    }
}
