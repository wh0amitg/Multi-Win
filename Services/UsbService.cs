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

    public enum FileSystemMode { Fat32Split, NtfsDirect }
    public enum PartitionStyle { Mbr, Gpt }

    public List<UsbDrive> GetUsbDrives()
    {
        var list = new List<UsbDrive>();
        var styles = DiskStyles();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Model, Size, Index FROM Win32_DiskDrive WHERE InterfaceType='USB'");
        foreach (ManagementObject d in searcher.Get())
        {
            var deviceId = d["DeviceID"]?.ToString() ?? "";
            var model = (d["Model"]?.ToString() ?? "USB Drive").Trim();
            ulong.TryParse(d["Size"]?.ToString(), out var size);
            int index = -1;
            try { index = Convert.ToInt32(d["Index"]); } catch { }
            string style = styles.TryGetValue(index, out var s) ? s : "?";
            list.Add(new UsbDrive(deviceId, model, size, "")
            {
                PartitionStyle = style,
                Display = $"{model} · {Fmt((long)size)} · {style}"
            });
        }
        return list;
    }

    private static Dictionary<int, string> DiskStyles()
    {
        var map = new Dictionary<int, string>();
        try
        {
            var opts = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(8), ReturnImmediately = true };
            using var s = new ManagementObjectSearcher(
                new ManagementScope(@"root\Microsoft\Windows\Storage"),
                new ObjectQuery("SELECT Number, PartitionStyle FROM MSFT_Disk"), opts);
            foreach (ManagementObject o in s.Get())
            {
                int num = Convert.ToInt32(o["Number"]);
                map[num] = Convert.ToUInt16(o["PartitionStyle"]) switch { 1 => "MBR", 2 => "GPT", _ => "?" };
            }
        }
        catch { }
        return map;
    }

    public void WriteImage(string isoPath, UsbDrive target, string arch, WindowsCustom custom,
        IProgress<string>? log = null, CancellationToken ct = default,
        FileSystemMode fsMode = FileSystemMode.Fat32Split, bool verify = false,
        string? volumeLabel = null, PartitionStyle partStyle = PartitionStyle.Mbr)
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

        L("Mounting ISO...");
        string isoLetter = MountIso(isoPath, Emit, ct);
        L($"ISO mounted as {isoLetter}");
        try
        {
            ct.ThrowIfCancellationRequested();
            if (fsMode == FileSystemMode.NtfsDirect)
            {
                long needUsb = isoLen + 200L * 1024 * 1024;
                if (target.SizeBytes > 0 && target.SizeBytes < (ulong)needUsb)
                    throw new IOException(
                        $"USB drive too small: {Fmt((long)target.SizeBytes)}, need {Fmt(needUsb)}.");
                CheckUefiNtfsAsset(L);
                L($"Preparing USB (diskpart: clean, {partStyle}, NTFS, active)...");
                string usbLetterNtfs = PrepareUsb(disk, Emit, ct, "ntfs", volumeLabel ?? "MULTIWIN", partStyle);
                L($"USB ready as {usbLetterNtfs}");
                ct.ThrowIfCancellationRequested();
                int rcNtfs = Run("robocopy",
                    $"{isoLetter}\\ {usbLetterNtfs}\\ /E /R:2 /W:5 /MT:8",
                    Emit, ct);
                L($"robocopy exit code: {rcNtfs} (0-7 = success)");
                if (rcNtfs > 7) throw new IOException($"robocopy failed, exit code {rcNtfs}.");
                FinishTweaks(usbLetterNtfs, arch, custom, L);
                if (verify)
                {
                    L("Verifying files...");
                    UsbCheckService.VerifyFileCopy(isoLetter, usbLetterNtfs, L, ct);
                }
                return;
            }

            CopyPlan plan = DeterminePlan(isoLetter, L);
            long needUsbFat = isoLen + (plan == CopyPlan.ConvertEsd ? 3L * 1024 * 1024 * 1024 : 200L * 1024 * 1024);
            if (target.SizeBytes > 0 && target.SizeBytes < (ulong)needUsbFat)
                throw new IOException(
                    $"USB drive too small: {Fmt((long)target.SizeBytes)}, need {Fmt(needUsbFat)}.");
            if (plan == CopyPlan.ConvertEsd)
                RequireTempSpace(isoLetter, L);

            L($"Preparing USB (diskpart: clean, {partStyle}, FAT32, active)...");
            string usbLetter = PrepareUsb(disk, Emit, ct, "fat32", volumeLabel ?? "MULTIWIN", partStyle);
            L($"USB ready as {usbLetter}");

            ct.ThrowIfCancellationRequested();
            CopyFiles(isoLetter, usbLetter, plan,
                SafeName(Path.GetFileNameWithoutExtension(isoPath)), L, Emit, ct);

            FinishTweaks(usbLetter, arch, custom, L);
            if (verify)
            {
                L("Verifying files...");
                UsbCheckService.VerifyFileCopy(isoLetter, usbLetter, L, ct);
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
        CancellationToken ct = default, bool verify = false)
    {
        void L(string m) => log?.Report(m);
        int disk = ParseDiskNumber(target.DeviceId);
        AssertUsbDisk(disk, target.DeviceId);
        long total = new FileInfo(isoPath).Length;
        if (target.SizeBytes > 0 && (ulong)total > target.SizeBytes)
            throw new IOException(
                $"USB drive too small: {Fmt((long)target.SizeBytes)}, image is {Fmt(total)}.");

        string script = TempScriptPath("multiwin-clean");
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
        if (verify)
            UsbCheckService.VerifyRaw(isoPath, target.DeviceId, L, progress, ct);
    }

    private enum CopyPlan { Direct, SplitWim, ConvertEsd }

    private static CopyPlan DeterminePlan(string isoLetter, Action<string> L)
    {
        string wim = isoLetter + @"\sources\install.wim";
        string esd = isoLetter + @"\sources\install.esd";
        if (File.Exists(wim) && new FileInfo(wim).Length > Fat32MaxFile)
        {
            L($"install.wim is {Fmt(new FileInfo(wim).Length)} > 4GB, will copy without it and split.");
            return CopyPlan.SplitWim;
        }
        if (File.Exists(esd) && new FileInfo(esd).Length > Fat32MaxFile)
        {
            L($"install.esd is {Fmt(new FileInfo(esd).Length)} > 4GB, will convert to split WIM.");
            return CopyPlan.ConvertEsd;
        }
        return CopyPlan.Direct;
    }

    private static void RequireTempSpace(string isoLetter, Action<string> L)
    {
        long need = new FileInfo(isoLetter + @"\sources\install.esd").Length + 1L * 1024 * 1024 * 1024;
        string? root = Path.GetPathRoot(Path.GetTempPath());
        if (root != null)
        {
            long free = new DriveInfo(root).AvailableFreeSpace;
            if (free < need)
                throw new IOException(
                    $"Not enough temp space for ESD conversion: need {Fmt(need)}, free {Fmt(free)} on {root}.");
        }
        L($"Temp space check passed ({Fmt(need)} needed for conversion).");
    }

    private static void CopyFiles(string isoLetter, string usbLetter, CopyPlan plan, string tempTag,
        Action<string> L, Action<string> emit, CancellationToken ct)
    {
        string srcWim = isoLetter + @"\sources\install.wim";

        if (plan == CopyPlan.SplitWim)
        {
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
            return;
        }

        if (plan == CopyPlan.ConvertEsd)
        {
            ConvertEsd(isoLetter, usbLetter, tempTag, L, emit, ct);
            return;
        }

        int rc2 = Run("robocopy",
            $"{isoLetter}\\ {usbLetter}\\ /E /R:2 /W:5 /MT:8",
            emit, ct);
        L($"robocopy exit code: {rc2} (0-7 = success)");
        if (rc2 > 7) throw new IOException($"robocopy failed, exit code {rc2}.");
    }

    private static void ConvertEsd(string isoLetter, string usbLetter, string tempTag,
        Action<string> L, Action<string> emit, CancellationToken ct)
    {
        string srcEsd = isoLetter + @"\sources\install.esd";
        int rc = Run("robocopy",
            $"{isoLetter}\\ {usbLetter}\\ /E /R:2 /W:5 /MT:8 /XF install.esd",
            emit, ct);
        L($"robocopy exit code: {rc}");
        if (rc > 7) throw new IOException($"robocopy failed, exit code {rc}.");

        L("Reading ESD editions...");
        var infoLines = new List<string>();
        int gi = Run("dism", $"/Get-ImageInfo /ImageFile:\"{srcEsd}\"", infoLines.Add, ct);
        if (gi != 0) throw new IOException($"dism Get-ImageInfo failed, exit code {gi}.");
        var indexes = infoLines
            .Select(l => Regex.Match(l, @":\s*(\d+)\s*$"))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .Distinct().OrderBy(i => i).ToList();
        if (indexes.Count == 0) throw new IOException("Could not read ESD edition list.");
        L($"Found {indexes.Count} edition(s), exporting to WIM (slowest part, be patient)...");

        string tmpDir = Path.Combine(Path.GetTempPath(), "Multi-Win");
        Directory.CreateDirectory(tmpDir);
        string tmpWim = Path.Combine(tmpDir, tempTag + "-" + Path.GetRandomFileName() + ".conv.wim");
        try { if (File.Exists(tmpWim)) File.Delete(tmpWim); } catch { }
        try
        {
            foreach (int i in indexes)
            {
                ct.ThrowIfCancellationRequested();
                L($"Exporting edition {i}/{indexes.Max()}...");
                int ex = Run("dism",
                    $"/Export-Image /SourceImageFile:\"{srcEsd}\" /SourceIndex:{i} /DestinationImageFile:\"{tmpWim}\" /Compress:fast /CheckIntegrity",
                    emit, ct);
                if (ex != 0) throw new IOException($"dism Export-Image (index {i}) failed, exit code {ex}.");
            }
            L($"Splitting into {SplitChunkMb}MB chunks...");
            int d = Run("dism",
                $"/Split-Image /ImageFile:\"{tmpWim}\" /SWMFile:\"{usbLetter}\\sources\\install.swm\" /FileSize:{SplitChunkMb}",
                emit, ct);
            L($"dism exit code: {d}");
            if (d != 0) throw new IOException($"dism Split-Image failed, exit code {d}.");
        }
        finally { try { if (File.Exists(tmpWim)) File.Delete(tmpWim); } catch { } }
    }

    private static string SafeName(string n)
    {
        string t = Regex.Replace(n ?? "", @"[^A-Za-z0-9_-]+", "_").Trim('_');
        if (t.Length == 0) return "image";
        return t.Length > 40 ? t[..40] : t;
    }

    internal static string SanitizeLabel(string label)
    {
        string t = Regex.Replace(label ?? "", @"[^A-Za-z0-9 _-]+", "").Trim();
        if (t.Length == 0) return "MULTIWIN";
        t = t.ToUpperInvariant();
        return t.Length > 11 ? t[..11] : t;
    }

    private static void FinishTweaks(string usbLetter, string arch, WindowsCustom custom, Action<string> L)
    {
        if (custom.HasTweaks())
            BypassService.Inject(usbLetter + "\\", arch, custom, L);

        if (custom.EmptyAppraiser)
        {
            string ap = usbLetter + @"\sources\appraiserres.dll";
            if (File.Exists(ap))
            {
                string bak = ap + ".bak";
                try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                File.Move(ap, bak);
                File.WriteAllBytes(ap, Array.Empty<byte>());
                L("appraiserres.dll emptied (helps in-place upgrade without TPM).");
            }
            else L("appraiserres.dll not found, skipping.");
        }
    }

    private static void CheckUefiNtfsAsset(Action<string> L)
    {
        try
        {
            string img = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "uefi-ntfs.img");
            if (!File.Exists(img))
                L("Note: NTFS mode without Assets/uefi-ntfs.img boots on BIOS/CSM and NTFS-capable UEFI only. " +
                  "For max UEFI compat use FAT32 (split) mode.");
            else
                L("uefi-ntfs.img found (future dual-partition chainload can use it).");
        }
        catch { }
    }

    private static string PrepareUsb(int disk, Action<string> emit, CancellationToken ct, string fs = "fat32", string label = "MULTIWIN", PartitionStyle style = PartitionStyle.Mbr)
    {
        string fsLower = (fs ?? "fat32").ToLowerInvariant() == "ntfs" ? "ntfs" : "fat32";
        string vol = SanitizeLabel(label);
        string script = TempScriptPath("multiwin-diskpart");
        string convert = style == PartitionStyle.Gpt ? "convert gpt" : "convert mbr";
        // GPT: no "active" flag (MBR-only concept); single data partition is enough
        // for UEFI boot from FAT32. MBR keeps "active" for BIOS boot.
        string active = style == PartitionStyle.Gpt ? "" : "active\n";
        File.WriteAllText(script,
            $"select disk {disk}\nclean\n{convert}\ncreate partition primary\n" +
            $"format fs={fsLower} quick label=\"{vol}\"\n{active}assign\nexit\n");
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
        // Disk number is validated int; script itself is static. EncodedCommand
        // avoids any quoting/injection issues with inline -Command strings.
        string ps = "(Get-Partition -DiskNumber " + disk +
            " -ErrorAction SilentlyContinue | Where-Object DriveLetter | Select-Object -First 1).DriveLetter";
        RunEncodedPowerShell(ps,
            l => { var t = l.Trim(); if (t.Length == 1 && char.IsLetter(t[0])) found = t.ToUpperInvariant() + ":"; },
            ct);
        return found;
    }

    public string? TryGetVolumeLetter(UsbDrive target)
    {
        try
        {
            int disk = ParseDiskNumber(target.DeviceId);
            return QueryUsbLetter(disk, CancellationToken.None);
        }
        catch { return null; }
    }

    private static string MountIso(string isoPath, Action<string> emit, CancellationToken ct)
    {
        string? found = null;
        // Script travels base64-encoded (-EncodedCommand), so cmd.exe quoting is a
        // non-issue. The path itself is a single-quoted PowerShell literal where
        // only ' needs escaping (doubled) — no interpolation, no injection.
        string q = ToPsSingleQuoted(Path.GetFullPath(isoPath));
        string b64 = EncodePowerShell(
            $"Mount-DiskImage -ImagePath {q} | Out-Null; " +
            $"(Get-DiskImage -ImagePath {q} | Get-Volume).DriveLetter");
        using var p = StartProcess("powershell",
            "-NoProfile -NonInteractive -EncodedCommand " + b64, out var readOut, out var readErr,
            l => { var t = l.Trim(); if (t.Length == 1 && char.IsLetter(t[0])) found = t.ToUpperInvariant() + ":"; else emit(l); });
        WaitForExitCancellable(p, readOut, readErr, ct);
        return found ?? throw new IOException("Could not mount ISO / get its drive letter.");
    }

    private static void DismountIso(string isoPath, Action<string> L)
    {
        try
        {
            string q = ToPsSingleQuoted(Path.GetFullPath(isoPath));
            string b64 = EncodePowerShell($"Dismount-DiskImage -ImagePath {q}");
            using var p = StartProcess("powershell",
                "-NoProfile -NonInteractive -EncodedCommand " + b64, out var ro, out var re, null);
            WaitForExitCancellable(p, ro, re, CancellationToken.None);
        }
        catch (Exception ex) { L("Warning: could not dismount ISO: " + ex.Message); return; }
        L("ISO dismounted.");
    }

    private static string ToPsSingleQuoted(string s) => "'" + s.Replace("'", "''") + "'";

    private static string EncodePowerShell(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

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
        // Never concatenate deviceId into WQL: validate the number first, then
        // query by integer Index. Anything that doesn't parse as PHYSICALDRIVEn
        // (or whose Index doesn't match) aborts.
        int parsed = ParseDiskNumber(deviceId);
        if (parsed != disk)
            throw new IOException($"Disk number mismatch ({disk} vs {parsed}), aborting.");
        using var s = new ManagementObjectSearcher(
            $"SELECT InterfaceType, Model, Index FROM Win32_DiskDrive WHERE Index={disk}");
        foreach (ManagementObject o in s.Get())
        {
            int idx;
            try { idx = Convert.ToInt32(o["Index"]); } catch { continue; }
            if (idx != disk) continue;
            string iface = o["InterfaceType"]?.ToString() ?? "";
            if (!iface.Contains("USB", StringComparison.OrdinalIgnoreCase))
                throw new IOException(
                    $"Refusing: disk {disk} ({o["Model"]}) is '{iface}', not USB. Aborting.");
            return;
        }
        throw new IOException($"Disk {disk} not found, aborting.");
    }

    private static string TempScriptPath(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + "-" + Path.GetRandomFileName() + ".txt");

    private static Process StartProcess(string exe, string args,
        out Task readOut, out Task readErr, Action<string>? onLine)
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
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Start();
        readOut = Task.Run(() => Pump(p.StandardOutput, onLine));
        readErr = Task.Run(() => Pump(p.StandardError, onLine));
        return p;
    }

    private static int WaitForExitCancellable(Process p, Task readOut, Task readErr, CancellationToken ct)
    {
        try
        {
            while (!p.WaitForExit(250))
                ct.ThrowIfCancellationRequested();
            Task.WaitAll(readOut, readErr);
            ct.ThrowIfCancellationRequested();
            return p.ExitCode;
        }
        catch
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
            }
            catch { }
            try { Task.WaitAll(readOut, readErr, 2000); } catch { }
            throw;
        }
    }

    internal static void RunEncodedPowerShell(string script, Action<string>? onLine, CancellationToken ct)
    {
        string b64 = EncodePowerShell(script);
        using var p = StartProcess("powershell",
            "-NoProfile -NonInteractive -EncodedCommand " + b64, out var ro, out var re, onLine);
        int rc = WaitForExitCancellable(p, ro, re, ct);
        if (rc != 0) onLine?.Invoke($"powershell exit code: {rc}");
    }

    private static int Run(string exe, string args, Action<string>? onLine, CancellationToken ct)
    {
        using var p = StartProcess(exe, args, out var readOut, out var readErr, onLine);
        return WaitForExitCancellable(p, readOut, readErr, ct);
    }

    private static void Pump(StreamReader r, Action<string>? onLine)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        int n;
        while ((n = r.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                char c = buf[i];
                if (c == '\r' || c == '\n')
                {
                    if (sb.Length > 0) { onLine?.Invoke(sb.ToString()); sb.Clear(); }
                }
                else sb.Append(c);
            }
        }
        if (sb.Length > 0) onLine?.Invoke(sb.ToString());
    }

    private static bool IsImportant(string l) =>
        l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
        || l.StartsWith("Files :", StringComparison.OrdinalIgnoreCase)
        || l.StartsWith("Bytes :", StringComparison.OrdinalIgnoreCase)
        || l.StartsWith("Times :", StringComparison.OrdinalIgnoreCase)
        || l.StartsWith("Ended :", StringComparison.OrdinalIgnoreCase)
        || l.StartsWith("Speed :", StringComparison.OrdinalIgnoreCase);

    internal static string Fmt(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024L * 1024 => $"{b / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:F1} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:F2} GB",
    };
}
