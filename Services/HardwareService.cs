using System.Management;
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
        return new HardwareInfo(cpu, ramMb, hasTpm, hasUefi, 0);
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
                "root\\CIMV2\\Security\\MicrosoftTpm", "SELECT IsEnabled FROM Win32_Tpm", 8);
            foreach (ManagementObject o in s.Get()) return true;
        }
        catch { }
        return false;
    }

    private static bool CheckUefi()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\PEFirmwareType");
            return Environment.GetEnvironmentVariable("firmware_type") != "Legacy";
        }
        catch { return false; }
    }
}
