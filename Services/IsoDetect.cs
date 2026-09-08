using System.IO;
using System.Text.RegularExpressions;

namespace WinMultiInstaller.Services;

public static class IsoDetect
{
    public record DetectedOs(string Name, string OsFamily, string Icon, int MinRamMb, bool FromContents = false);

    public static DetectedOs FromFileName(string filePath)
    {
        string stem = Path.GetFileNameWithoutExtension(filePath).Trim();
        string s = stem.ToLowerInvariant();

        DetectedOs win(string name) => new(name, "Windows", "windows", 4096);
        DetectedOs lin(string name, string icon, int ram) => new(name, "Linux", icon, ram);

        if (s.StartsWith("kali-linux-") || s.Contains("kali")) return lin("Kali Linux", "kali", 2048);
        if (s.Contains("ubuntu")) return lin("Ubuntu", "linux", 2048);
        if (s.Contains("linuxmint") || s.Contains("mint")) return lin("Linux Mint", "linux", 2048);
        if (s.Contains("debian")) return lin("Debian", "linux", 1024);
        if (s.Contains("fedora")) return lin("Fedora", "linux", 2048);
        if (s.Contains("archlinux")) return lin("Arch Linux", "linux", 1024);
        if (s.Contains("manjaro")) return lin("Manjaro", "linux", 2048);
        if (s.Contains("opensuse") || s.Contains("tumbleweed") || s.Contains("leap")) return lin("openSUSE", "linux", 2048);
        if (s.Contains("centos")) return lin("CentOS", "linux", 2048);
        if (s.Contains("almalinux") || s.Contains("alma")) return lin("AlmaLinux", "linux", 2048);
        if (s.Contains("rocky")) return lin("Rocky Linux", "linux", 2048);
        if (s.Contains("rhel") || s.Contains("redhat")) return lin("Red Hat Enterprise Linux", "linux", 2048);
        if (s.Contains("tails")) return lin("Tails", "linux", 2048);
        if (s.Contains("parrot")) return lin("Parrot OS", "linux", 2048);
        if (s.Contains("zorin")) return lin("Zorin OS", "linux", 2048);
        if (s.Contains("endeavour")) return lin("EndeavourOS", "linux", 2048);
        if (s.Contains("garuda")) return lin("Garuda Linux", "linux", 4096);
        if (s.Contains("pop-os") || s.Contains("pop_os") || s.Contains("pop!")) return lin("Pop!_OS", "linux", 2048);
        if (s.Contains("elementary")) return lin("elementary OS", "linux", 2048);
        if (s.StartsWith("mx-") || s.Contains("mx-linux")) return lin("MX Linux", "linux", 1024);
        if (s.Contains("gentoo")) return lin("Gentoo", "linux", 1024);

        if (s.Contains("windows"))
        {
            if (s.Contains("11")) return win("Windows 11");
            if (s.Contains("10")) return win("Windows 10");
            if (s.Contains("8.1") || s.Contains("8_1") || s.Contains("81")) return win("Windows 8.1");
            if (s.Contains("8")) return win("Windows 8");
            if (s.Contains("7")) return win("Windows 7");
            if (s.Contains("vista")) return win("Windows Vista");
            if (s.Contains("xp")) return win("Windows XP");
            if (s.Contains("server")) return win("Windows Server");
            return win("Windows");
        }
        var m = Regex.Match(s, @"(^|[-_. ])win(dows)?[-_. ]?(\d+\.?\d*|xp|vista)?");
        if (m.Success)
        {
            string v = m.Groups[3].Value;
            return v switch
            {
                "11" => win("Windows 11"),
                "10" => win("Windows 10"),
                "8.1" or "81" or "8" => win("Windows 8.1"),
                "7" => win("Windows 7"),
                "xp" => win("Windows XP"),
                "vista" => win("Windows Vista"),
                _ => win("Windows"),
            };
        }

        if (s.Contains("linux")) return lin(Prettify(stem), "linux", 2048);
        return new DetectedOs(Prettify(stem), "Unknown", "generic", 2048);
    }

    public static string DetectArch(string filePath)
    {
        string s = filePath.ToLowerInvariant();
        if (s.Contains("arm64") || s.Contains("aarch64")) return "arm64";
        if (s.Contains("x86") || s.Contains("i386") || s.Contains("i686")
            || s.Contains("32-bit") || s.Contains("32bit")) return "x86";
        return "x64";
    }

    public static bool IsWindows11(string? name) =>
        (name ?? "").Contains("Windows 11", StringComparison.OrdinalIgnoreCase);

    public static DetectedOs RefineWindowsName(string mountedRoot, DetectedOs current)
    {
        if (current.OsFamily != "Windows") return current;
        try
        {
            string setup = mountedRoot + @"\setup.exe";
            if (!File.Exists(setup))
                setup = mountedRoot + @"\sources\setup.exe";
            if (!File.Exists(setup)) return current;
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(setup);
            return (vi.FileMajorPart, vi.FileMinorPart, vi.FileBuildPart) switch
            {
                (5, _, _) => current with { Name = "Windows XP", MinRamMb = 512, FromContents = true },
                (6, 0, _) => current with { Name = "Windows Vista", MinRamMb = 1024, FromContents = true },
                (6, 1, _) => current with { Name = "Windows 7", MinRamMb = 2048, FromContents = true },
                (6, 2, _) or (6, 3, _) => current with { Name = "Windows 8.1", MinRamMb = 2048, FromContents = true },
                (10, _, >= 22000) => current with { Name = "Windows 11", MinRamMb = 4096, FromContents = true },
                (10, _, _) => current with { Name = "Windows 10", MinRamMb = 4096, FromContents = true },
                (> 10, _, _) => current with { Name = "Windows 11", MinRamMb = 4096, FromContents = true },
                _ => current
            };
        }
        catch { return current; }
    }

    private static string Prettify(string stem)
    {
        string t = Regex.Replace(stem, @"[-_.]+", " ").Trim();
        t = Regex.Replace(t, @"\s+", " ");
        if (t.Length > 64) t = t[..64] + "…";
        return t.Length == 0 ? stem : char.ToUpperInvariant(t[0]) + t[1..];
    }
}
