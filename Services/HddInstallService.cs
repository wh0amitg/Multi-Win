using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using WinMultiInstaller.Models;

namespace WinMultiInstaller.Services;

/// <summary>
/// "No USB stick" mode: copies Windows Setup files to a folder on an internal
/// drive and registers a ONE-SHOT boot entry (bcdedit /bootsequence) that loads
/// sources\boot.wim via ramdisk. The PC boots into Setup once; the default boot
/// entry is untouched. After Setup finishes, <see cref="RemoveHddSetup"/> (or the
/// generated remove-hdd-setup.cmd) deletes the folder and the BCD entry.
/// Only Windows images are supported; Linux ISOs still need a USB stick.
/// </summary>
public class HddInstallService
{
    public const string SetupDirName = "MULTIWIN-SETUP";
    private const string BcdFileName = "multiwin-bcd.txt";

    public record HddTarget(string Root, string SetupDir, long FreeBytes);

    public record HddState(bool Installed, string? SetupDir, string? BcdId);

    public HddState GetState()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                string dir = Path.Combine(d.RootDirectory.FullName.TrimEnd('\\'), SetupDirName);
                if (!Directory.Exists(dir)) continue;
                string? guid = null;
                string bf = Path.Combine(dir, BcdFileName);
                if (File.Exists(bf))
                {
                    guid = File.ReadAllText(bf).Trim();
                    if (!Regex.IsMatch(guid, @"^\{[0-9a-fA-F-]{36}\}$")) guid = null;
                }
                return new HddState(true, dir, guid);
            }
            catch { }
        }
        return new HddState(false, null, null);
    }

    public List<HddTarget> GetTargets(long needBytes)
    {
        var list = new List<HddTarget>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                string root = d.RootDirectory.FullName.TrimEnd('\\');
                if (root.Length != 2 || root[1] != ':') continue; // drive-letter volumes only
                string dir = Path.Combine(root, SetupDirName);
                list.Add(new HddTarget(root, dir, d.AvailableFreeSpace));
            }
            catch { }
        }
        return list.OrderByDescending(t => t.FreeBytes).ToList();
    }

    public void Install(string isoPath, string arch, WindowsCustom custom,
        string driveRoot, bool uefi,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        void L(string m) => log?.Report(m);
        if (string.IsNullOrWhiteSpace(driveRoot) || driveRoot.Length != 2 || driveRoot[1] != ':')
            throw new ArgumentException("Pick an internal drive (e.g. C:).");
        if (!File.Exists(isoPath))
            throw new FileNotFoundException("ISO file not found.", isoPath);
        long isoLen = new FileInfo(isoPath).Length;
        long need = isoLen + 512L * 1024 * 1024;
        var drive = new DriveInfo(driveRoot);
        if (drive.AvailableFreeSpace < need)
            throw new IOException(
                $"Not enough free space on {driveRoot} (need {UsbService.Fmt(need)}, free {UsbService.Fmt(drive.AvailableFreeSpace)}).");

        string setupDir = Path.Combine(driveRoot, SetupDirName);
        if (Directory.Exists(setupDir))
            throw new IOException($"{setupDir} already exists — remove the previous HDD setup first.");

        L("Mounting ISO...");
        string isoLetter = UsbService.MountIsoImage(isoPath);
        try
        {
            ct.ThrowIfCancellationRequested();
            string bootWim = isoLetter + @"\sources\boot.wim";
            string bootSdi = isoLetter + @"\sources\boot.sdi";
            if (!File.Exists(bootWim))
                throw new IOException("This image has no sources\\boot.wim — HDD install supports Windows Setup images only.");
            if (!File.Exists(bootSdi))
                throw new IOException("This image has no sources\\boot.sdi — cannot create the ramdisk boot entry.");

            L($"Copying Setup files to {setupDir}...");
            Directory.CreateDirectory(setupDir);
            var lines = new List<string>();
            int rc = Run("robocopy", $"{isoLetter}\\ {setupDir} /E /R:2 /W:5 /MT:8",
                l => { lock (lines) lines.Add(l); L("  " + l.Trim()); }, ct);
            L($"robocopy exit code: {rc} (0-7 = success)");
            if (rc > 7) throw new IOException($"robocopy failed, exit code {rc}.");

            BypassService.Inject(setupDir + "\\", arch, custom, L);

            L("Registering one-shot boot entry...");
            string guid = BcdCopyDefault("Multi-Win Setup (auto-removable)", L, ct);
            File.WriteAllText(Path.Combine(setupDir, BcdFileName), guid);
            string wimPath = $"[{driveRoot}]\\{SetupDirName}\\sources\\boot.wim";
            string sdiPath = $"\\{SetupDirName}\\sources\\boot.sdi";
            BcdSetRamdiskOptions(driveRoot, sdiPath, L, ct);
            Bcd($"set {guid} device ramdisk={wimPath},{{ramdiskoptions}}", L, ct);
            Bcd($"set {guid} osdevice ramdisk={wimPath},{{ramdiskoptions}}", L, ct);
            Bcd($"set {guid} path \\windows\\system32\\{(uefi ? "winload.efi" : "winload.exe")}", L, ct);
            Bcd($"set {guid} winpe yes", L, ct);
            Bcd($"set {guid} detecthal yes", L, ct);
            Bcd($"bootsequence {guid}", L, ct);

            WriteCleanupScript(setupDir, guid);
            L("Done. Reboot to start Windows Setup. Default boot entry unchanged — " +
              "the PC boots into Setup ONCE, then back to your system.");
            L($"After Setup finishes, delete {setupDir} and run RemoveHddSetup (or remove-hdd-setup.cmd).");
        }
        catch
        {
            try { UsbService.DismountIsoImage(isoPath); } catch { }
            throw;
        }
        try { UsbService.DismountIsoImage(isoPath); } catch (Exception ex) { L("Warning: could not dismount ISO: " + ex.Message); }
    }

    public void RemoveHddSetup(Action<string>? log = null)
    {
        void L(string m) => log?.Invoke(m);
        var st = GetState();
        if (!st.Installed || st.SetupDir == null)
        {
            L("No HDD setup found, nothing to remove.");
            return;
        }
        if (st.BcdId != null)
        {
            try { Bcd($"delete {st.BcdId} /f", L, CancellationToken.None); }
            catch (Exception ex) { L("Could not delete boot entry (already gone?): " + ex.Message); }
        }
        for (int i = 0; i < 3; i++)
        {
            try
            {
                Directory.Delete(st.SetupDir, recursive: true);
                L($"Removed {st.SetupDir}.");
                return;
            }
            catch (IOException) { Thread.Sleep(1000); }
        }
        try
        {
            // Last resort: schedule for next reboot.
            string cmd = Path.Combine(st.SetupDir, "remove-hdd-setup.cmd");
            if (File.Exists(cmd))
                Run("schtasks", $"/create /tn \"MultiWinCleanup\" /tr \"\\\"{cmd}\\\"\" /sc onstart /ru SYSTEM /f", L, CancellationToken.None);
            L($"Folder is locked, cleanup scheduled. Run {cmd} as admin after reboot.");
        }
        catch (Exception ex) { L("Remove failed: " + ex.Message); }
    }

    private static void WriteCleanupScript(string setupDir, string guid)
    {
        string cmd = Path.Combine(setupDir, "remove-hdd-setup.cmd");
        File.WriteAllText(cmd,
            "@echo off\r\n" +
            "REM Removes the Multi-Win one-shot Setup entry and this folder. Run as admin.\r\n" +
            $"bcdedit /delete {guid} /f\r\n" +
            "schtasks /delete /tn \"MultiWinCleanup\" /f >nul 2>&1\r\n" +
            "cd /d \"%~dp0..\"\r\n" +
            $"rmdir /s /q \"{setupDir}\"\r\n",
            new UTF8Encoding(false));
    }

    private static string BcdCopyDefault(string description, Action<string> L, CancellationToken ct)
    {
        var lines = new List<string>();
        int rc = Run("bcdedit", $"/copy {{default}} /d \"{description}\"",
            l => { lock (lines) lines.Add(l); L("  " + l.Trim()); }, ct);
        string all = string.Join("\n", lines);
        var m = Regex.Match(all, @"\{[0-9a-fA-F-]{36}\}");
        if (rc != 0 || !m.Success)
            throw new IOException("bcdedit /copy failed:\n" + string.Join("\n", lines.TakeLast(6)));
        L($"Boot entry {m.Value} created.");
        return m.Value;
    }

    private static void BcdSetRamdiskOptions(string driveRoot, string sdiPath, Action<string> L, CancellationToken ct)
    {
        // {ramdiskoptions} is a well-known alias; create is a no-op if it exists.
        try { Bcd("create {ramdiskoptions} /d \"Multi-Win ramdisk\"", L, ct); } catch { }
        Bcd($"set {{ramdiskoptions}} ramdisksdidevice partition={driveRoot}", L, ct);
        Bcd($"set {{ramdiskoptions}} ramdisksdipath {sdiPath}", L, ct);
    }

    private static void Bcd(string args, Action<string> L, CancellationToken ct)
    {
        var lines = new List<string>();
        int rc = Run("bcdedit", "/" + args.TrimStart('/', ' '),
            l => { lock (lines) lines.Add(l); }, ct);
        if (rc != 0)
            throw new IOException("bcdedit " + args + " failed:\n" + string.Join("\n", lines.TakeLast(6)));
        foreach (var l in lines.TakeLast(3))
            if (!string.IsNullOrWhiteSpace(l)) L("  " + l.Trim());
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
        p.Start();
        var ro = Task.Run(() => Pump(p.StandardOutput, onLine));
        var re = Task.Run(() => Pump(p.StandardError, onLine));
        try
        {
            while (!p.WaitForExit(250))
                ct.ThrowIfCancellationRequested();
            Task.WaitAll(ro, re);
            ct.ThrowIfCancellationRequested();
            return p.ExitCode;
        }
        catch
        {
            try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } } catch { }
            throw;
        }
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
}
