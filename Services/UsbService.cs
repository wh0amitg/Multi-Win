using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using WinMultiInstaller.Models;

namespace WinMultiInstaller.Services;

public class UsbService
{
    private const long Fat32MaxFile = 4294967295L;
    private const int SplitChunkMb = 3800;

    public List<UsbDrive> GetUsbDrives()
    {
        var list = new List<UsbDrive>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Model, Size FROM Win32_DiskDrive WHERE InterfaceType='USB'");
        foreach (ManagementObject d in searcher.Get())
        {
            var deviceId = d["DeviceID"]?.ToString() ?? "";
            var model = d["Model"]?.ToString() ?? "USB Drive";
            ulong.TryParse(d["Size"]?.ToString(), out var size);
            list.Add(new UsbDrive(deviceId, model.Trim(), size, ""));
        }
        return list;
    }

    public void WriteImage(string isoPath, UsbDrive target, bool bypassTpm,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        void L(string m) => log?.Report(m);
        long lastEmit = 0;
        void Emit(string line)
        {
            var now = Environment.TickCount64;
            if (now - lastEmit < 400 && !IsImportant(line)) return;
            lastEmit = now;
            L("  " + line.Trim());
        }

        int disk = ParseDiskNumber(target.DeviceId);
        AssertUsbDisk(disk, target.DeviceId);
        long isoLen = new FileInfo(isoPath).Length;
        if (target.SizeBytes > 0 && target.SizeBytes < (ulong)(isoLen + 200L * 1024 * 1024))
            throw new IOException(
                $"USB drive too small: {Fmt((long)target.SizeBytes)}, image needs {Fmt(isoLen)}.");

        L("Mounting ISO...");
        string isoLetter = MountIso(isoPath, Emit, ct);
        L($"ISO mounted as {isoLetter}");
        try
        {
            ct.ThrowIfCancellationRequested();
            L("Preparing USB (diskpart: clean, MBR, FAT32, active)...");
            string usbLetter = PrepareUsb(disk, Emit, ct);
            L($"USB ready as {usbLetter}");

            ct.ThrowIfCancellationRequested();
            CopyFiles(isoLetter, usbLetter, L, Emit, ct);

            if (bypassTpm)
            {
                BypassService.Inject(usbLetter + "\\");
                L("TPM/SecureBoot bypass written (autounattend.xml).");
            }
        }
        finally
        {
            try { DismountIso(isoPath, L); }
            catch (Exception ex) { L("Warning: could not dismount ISO: " + ex.Message); }
        }
    }

    public void WriteRaw(string isoPath, UsbDrive target,
        IProgress<string>? log = null, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        void L(string m) => log?.Report(m);
        int disk = ParseDiskNumber(target.DeviceId);
        AssertUsbDisk(disk, target.DeviceId);
        long total = new FileInfo(isoPath).Length;
        if (target.SizeBytes > 0 && (ulong)total > target.SizeBytes)
            throw new IOException(
                $"USB drive too small: {Fmt((long)target.SizeBytes)}, image is {Fmt(total)}.");

        string script = Path.Combine(Path.GetTempPath(), $"multiwin-clean-{disk}.txt");
        File.WriteAllText(script,
            $"select disk {disk}\nonline disk noerr\nattributes disk clear readonly noerr\nclean\nexit\n");
        try
        {
            var lines = new List<string>();
            int rc = Run("diskpart", $"/s \"{script}\"", lines.Add, ct);
            string all = string.Join("\n", lines);
            if (rc != 0 || all.Contains("has encountered an error", StringComparison.OrdinalIgnoreCase))
                throw new IOException("diskpart clean failed:\n" + string.Join("\n", lines.TakeLast(6)));
        }
        finally { try { File.Delete(script); } catch { } }

        L($"Writing {Fmt(total)} raw to disk {disk} (do not unplug)...");
        using var src = File.OpenRead(isoPath);
        using var dst = new FileStream(target.DeviceId, FileMode.Open,
            FileAccess.Write, FileShare.ReadWrite, 1 << 20, FileOptions.None);
        var buf = new byte[4 << 20];
        long done = 0, lastRep = 0;
        var sw = Stopwatch.StartNew();
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            dst.Write(buf, 0, n);
            done += n;
            if (done - lastRep >= 64L << 20 || done == total)
            {
                lastRep = done;
                double pct = (double)done / total * 100;
                progress?.Report(pct);
                double bps = sw.Elapsed.TotalSeconds > 0 ? done / sw.Elapsed.TotalSeconds : 0;
                L($"  {pct:F1}% · {Fmt(done)} / {Fmt(total)} · {Fmt((long)bps)}/s");
            }
        }
        dst.Flush();
        L("Raw write finished.");
    }

    private static void CopyFiles(string isoLetter, string usbLetter,
        Action<string> L, Action<string> emit, CancellationToken ct)
    {
        string srcWim = isoLetter + @"\sources\install.wim";
        string srcEsd = isoLetter + @"\sources\install.esd";

        if (File.Exists(srcWim) && new FileInfo(srcWim).Length > Fat32MaxFile)
        {
            L($"install.wim is {Fmt(new FileInfo(srcWim).Length)} > 4GB, copying without it, then splitting...");
            int rc = Run("robocopy",
                $"{isoLetter}\\ {usbLetter}\\ /E /R:2 /W:5 /MT:8 /XF install.wim",
                emit, ct);
            L($"robocopy exit code: {rc}");
            if (rc > 7) throw new IOException($"robocopy failed, exit code {rc}.");

            L($"Splitting install.wim into {SplitChunkMb}MB chunks (dism, takes minutes)...");
            int d = Run("dism",
                $"/Split-Image /ImageFile:\"{srcWim}\" /SWMFile:\"{usbLetter}\\sources\\install.swm\" /FileSize:{SplitChunkMb}",
                emit, ct);
            L($"dism exit code: {d}");
            if (d != 0) throw new IOException($"dism Split-Image failed, exit code {d}.");
        }
        else
        {
            if (File.Exists(srcEsd) && new FileInfo(srcEsd).Length > Fat32MaxFile)
                throw new IOException("install.esd exceeds the FAT32 4GB limit, this image is not supported.");
            L("Copying files (robocopy, takes a while, watch the % lines)...");
            int rc = Run("robocopy",
                $"{isoLetter}\\ {usbLetter}\\ /E /R:2 /W:5 /MT:8",
                emit, ct);
            L($"robocopy exit code: {rc} (0-7 = success)");
            if (rc > 7) throw new IOException($"robocopy failed, exit code {rc}.");
        }
    }

    private static string PrepareUsb(int disk, Action<string> emit, CancellationToken ct)
    {
        string script = Path.Combine(Path.GetTempPath(), $"multiwin-diskpart-{disk}.txt");
        File.WriteAllText(script,
            $"select disk {disk}\nclean\nconvert mbr\ncreate partition primary\n" +
            "format fs=fat32 quick label=\"MULTIWIN\"\nactive\nassign\nexit\n");
        try
        {
            var outLines = new List<string>();
            int rc = Run("diskpart", $"/s \"{script}\"", l => { outLines.Add(l); emit(l); }, ct);
            string all = string.Join("\n", outLines);
            if (rc != 0 || all.Contains("has encountered an error", StringComparison.OrdinalIgnoreCase)
                || all.Contains("There is no disk selected", StringComparison.OrdinalIgnoreCase))
                throw new IOException("diskpart failed:\n" + string.Join("\n", outLines.TakeLast(8)));
        }
        finally { try { File.Delete(script); } catch { } }

        for (int i = 0; i < 20; i++)
        {
            ct.ThrowIfCancellationRequested();
            string? letter = QueryUsbLetter(disk, ct);
            if (letter != null) return letter;
            Thread.Sleep(1000);
        }
        throw new IOException("USB formatted but no drive letter appeared.");
    }

    private static string? QueryUsbLetter(int disk, CancellationToken ct)
    {
        string? found = null;
        Run("powershell",
            $"-NoProfile -NonInteractive -Command \"(Get-Partition -DiskNumber {disk} -ErrorAction SilentlyContinue | Where-Object DriveLetter | Select-Object -First 1).DriveLetter\"",
            l => { var t = l.Trim(); if (t.Length == 1 && char.IsLetter(t[0])) found = t.ToUpperInvariant() + ":"; },
            ct);
        return found;
    }

    private static string MountIso(string isoPath, Action<string> emit, CancellationToken ct)
    {
        string? found = null;
        string ps = $"Mount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}' | Out-Null; " +
                    $"(Get-DiskImage -ImagePath '{isoPath.Replace("'", "''")}' | Get-Volume).DriveLetter";
        Run("powershell", $"-NoProfile -NonInteractive -Command \"{ps}\"",
            l => { var t = l.Trim(); if (t.Length == 1 && char.IsLetter(t[0])) found = t.ToUpperInvariant() + ":"; else emit(l); },
            ct);
        return found ?? throw new IOException("Could not mount ISO / get its drive letter.");
    }

    private static void DismountIso(string isoPath, Action<string> L)
    {
        Run("powershell",
            $"-NoProfile -NonInteractive -Command \"Dismount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}'\"",
            null, CancellationToken.None);
        L("ISO dismounted.");
    }

    internal static string MountIsoImage(string isoPath) =>
        MountIso(isoPath, _ => { }, CancellationToken.None);

    internal static void DismountIsoImage(string isoPath) =>
        DismountIso(isoPath, _ => { });

    private static int ParseDiskNumber(string deviceId)
    {
        var m = Regex.Match(deviceId ?? "", @"PHYSICALDRIVE(\d+)\s*$", RegexOptions.IgnoreCase);
        if (!m.Success) throw new ArgumentException($"Bad device id: {deviceId}");
        return int.Parse(m.Groups[1].Value);
    }

    private static void AssertUsbDisk(int disk, string deviceId)
    {
        string wmiId = deviceId.Replace("\\", "\\\\");
        using var s = new ManagementObjectSearcher(
            $"SELECT InterfaceType, Model FROM Win32_DiskDrive WHERE DeviceID='{wmiId}'");
        foreach (ManagementObject o in s.Get())
        {
            string iface = o["InterfaceType"]?.ToString() ?? "";
            if (!iface.Contains("USB", StringComparison.OrdinalIgnoreCase))
                throw new IOException(
                    $"Refusing: disk {disk} ({o["Model"]}) is '{iface}', not USB. Aborting.");
            return;
        }
        throw new IOException($"Disk {disk} not found, aborting.");
    }

    private static int Run(string exe, string args, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        ct.ThrowIfCancellationRequested();
        return p.ExitCode;
    }

    private static bool IsImportant(string l) =>
        l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
        || l.StartsWith("Files :", StringComparison.OrdinalIgnoreCase)
        || l.StartsWith("Bytes :", StringComparison.OrdinalIgnoreCase)
        || l.StartsWith("Times :", StringComparison.OrdinalIgnoreCase)
        || l.StartsWith("Ended :", StringComparison.OrdinalIgnoreCase)
        || l.StartsWith("Speed :", StringComparison.OrdinalIgnoreCase);

    private static string Fmt(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024L * 1024 => $"{b / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:F1} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:F2} GB",
    };
}
