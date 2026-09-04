using System.IO;

namespace WinMultiInstaller.Services;

public static class IsoInspect
{
    public static Task<IsoDetect.DetectedOs> InspectAsync(string isoPath, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var byName = IsoDetect.FromFileName(isoPath);
            string letter;
            try { letter = UsbService.MountIsoImage(isoPath); }
            catch { return byName; }
            try
            {
                return InspectMounted(letter, byName);
            }
            finally { try { UsbService.DismountIsoImage(isoPath); } catch { } }
        }, ct);

    private static IsoDetect.DetectedOs InspectMounted(string root, IsoDetect.DetectedOs fallback)
    {
        bool Exists(string p) => Directory.Exists(root + p) || File.Exists(root + p);

        if (Exists(@"\sources\install.wim") || Exists(@"\sources\install.esd"))
            return fallback.OsFamily == "Windows"
                ? fallback with { FromContents = true }
                : new IsoDetect.DetectedOs("Windows (custom ISO)", "Windows", "windows", 4096, true);

        string info = root + @"\.disk\info";
        if (File.Exists(info))
        {
            string first = File.ReadLines(info).FirstOrDefault()?.Trim() ?? "";
            string s = first.ToLowerInvariant();
            if (s.Contains("kali")) return new("Kali Linux", "Linux", "kali", 2048, true);
            if (s.Contains("linux mint")) return new("Linux Mint", "Linux", "linux", 2048, true);
            if (s.Contains("ubuntu")) return new(Trim(first, "Ubuntu"), "Linux", "linux", 2048, true);
            if (s.Contains("debian")) return new(Trim(first, "Debian"), "Linux", "linux", 1024, true);
            if (s.Contains("parrot")) return new("Parrot OS", "Linux", "linux", 2048, true);
            if (s.Contains("tails")) return new("Tails", "Linux", "linux", 2048, true);
            if (first.Length > 0) return new(Trim(first, "Linux"), "Linux", "linux", 2048, true);
        }

        if (Exists(@"\arch")) return new("Arch Linux", "Linux", "linux", 1024, true);
        if (File.Exists(root + @"\.discinfo"))
            return new(DiscInfoName(root) ?? "Fedora/RHEL family", "Linux", "linux", 2048, true);
        if (Exists(@"\LiveOS")) return new("Linux Live (RHEL family)", "Linux", "linux", 2048, true);
        if (Exists(@"\casper"))
            return fallback.OsFamily == "Linux"
                ? fallback with { FromContents = true }
                : new("Ubuntu-based Linux", "Linux", "linux", 2048, true);
        if (Exists(@"\isolinux") || Exists(@"\syslinux") || Exists(@"\boot\grub"))
            return fallback.OsFamily == "Linux"
                ? fallback with { FromContents = true }
                : new("Linux (bootloader detected)", "Linux", "linux", 2048, true);

        return fallback;
    }

    private static string Trim(string raw, string fallback)
    {
        string t = raw.Trim().Trim('"');
        if (t.Length > 64) t = t[..64] + "…";
        return t.Length == 0 ? fallback : t;
    }

    private static string? DiscInfoName(string root)
    {
        try
        {
            var lines = File.ReadAllLines(root + @"\.discinfo")
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            var cand = lines.FirstOrDefault(l => l.Contains("fedora", StringComparison.OrdinalIgnoreCase)
                || l.Contains("rhel", StringComparison.OrdinalIgnoreCase)
                || l.Contains("centos", StringComparison.OrdinalIgnoreCase)
                || l.Contains("rocky", StringComparison.OrdinalIgnoreCase)
                || l.Contains("alma", StringComparison.OrdinalIgnoreCase));
            return cand is null ? null : Trim(cand, "Fedora/RHEL family");
        }
        catch { return null; }
    }
}
