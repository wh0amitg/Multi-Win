using System.Management;
using System.Runtime.InteropServices;
using WinMultiInstaller.Models;

namespace WinMultiInstaller.Services;

public class HardwareService
{
    public HardwareInfo GetInfo()
    {
        string cpu = "Unknown CPU";
        int ramMb = 0;
        try
        {
            using var cpuSearch = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject o in cpuSearch.Get())
            { cpu = o["Name"]?.ToString()?.Trim() ?? cpu; break; }

            using var ramSearch = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory");
            ulong total = 0;
            foreach (ManagementObject o in ramSearch.Get())
                total += Convert.ToUInt64(o["Capacity"]);
            ramMb = (int)(total / 1024 / 1024);
        }
        catch { }

        bool hasTpm = CheckTpm();
        bool hasUefi = CheckUefi();
        ulong free = 0;
        try
        {
            string? sysRoot = Path.GetPathRoot(Environment.SystemDirectory);
            if (sysRoot != null) free = (ulong)new DriveInfo(sysRoot).AvailableFreeSpace;
        }
        catch { }
        return new HardwareInfo(cpu, ramMb, hasTpm, hasUefi, free);
    }

    private static ManagementObjectSearcher WithTimeout(string scope, string query, int seconds = 10)
    {
        var options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(seconds), ReturnImmediately = true };
        var ms = string.IsNullOrEmpty(scope) ? new ManagementScope(@"\\.\root\cimv2") : new ManagementScope(scope);
        return new ManagementObjectSearcher(ms, new ObjectQuery(query), options);
    }

    private static bool CheckTpm()
    {
        try
        {
            using var s = WithTimeout(
                @"root\CIMV2\Security\MicrosoftTpm",
                "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm", 8);
            foreach (ManagementObject o in s.Get())
            {
                bool enabled = IsTruthy(o["IsEnabled_InitialValue"]);
                bool activated = IsTruthy(o["IsActivated_InitialValue"]);
                string spec = o["SpecVersion"]?.ToString() ?? "";
                // TPM 2.0 reports SpecVersion like "2.0, 0, 1.16". Accept 2.x.
                bool is2x = spec.Split(',')[0].Trim().StartsWith("2", StringComparison.Ordinal);
                if (enabled && activated && is2x) return true;
                // Fall back: any enabled TPM counts as present (caller warns about 1.2).
                if (enabled && activated) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool IsTruthy(object? v)
    {
        if (v == null) return false;
        if (v is bool b) return b;
        string s = v.ToString() ?? "";
        return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFirmwareType(ref uint firmwareType);

    private static bool CheckUefi()
    {
        try
        {
            uint t = 0;
            if (GetFirmwareType(ref t))
                return t == 2; // FirmwareTypeUefi
        }
        catch { }
        try
        {
            // Fallback: SecureBoot UEFI variable presence implies UEFI boot.
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (k?.GetValue("UEFISecureBootEnabled") != null) return true;
        }
        catch { }
        return false;
    }
}
