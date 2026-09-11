using System.IO;
using System.Text;
using WinMultiInstaller.Models;

namespace WinMultiInstaller.Services;

public static class BypassService
{
    private const string Ns =
        "xmlns:wcm=\"http://schemas.microsoft.com/WMIConfig/2002/State\" " +
        "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"";

    public static void Inject(string usbRoot, string arch, WindowsCustom c, Action<string> L)
    {
        string warch = arch == "x86" ? "x86" : arch == "arm64" ? "arm64" : "amd64";
        string user = SanitizeUser(c.Username);
        string userXml = System.Security.SecurityElement.Escape(user) ?? "";
        string tzXml;
        try { tzXml = System.Security.SecurityElement.Escape(TimeZoneInfo.Local.Id) ?? ""; }
        catch { tzXml = ""; }
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<unattend xmlns=\"urn:schemas-microsoft-com:unattend\">");

        if (c.BypassTpm)
        {
            sb.AppendLine("  <settings pass=\"windowsPE\">");
            sb.AppendLine($"    <component name=\"Microsoft-Windows-Setup\" processorArchitecture=\"{warch}\" publicKeyToken=\"31bf3856ad364e35\" language=\"neutral\" versionScope=\"nonSxS\" {Ns}>");
            sb.AppendLine("      <UserData><ProductKey><Key /></ProductKey></UserData>");
            sb.AppendLine("      <RunSynchronous>");
            int o = 1;
            foreach (var v in new[] { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck", "BypassCPUCheck" })
                sb.AppendLine($"        <RunSynchronousCommand wcm:action=\"add\"><Order>{o++}</Order><Path>reg add HKLM\\SYSTEM\\Setup\\LabConfig /v {v} /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand>");
            sb.AppendLine("      </RunSynchronous>");
            sb.AppendLine("    </component>");
            sb.AppendLine("  </settings>");
        }

        var spec = new List<string>();
        if (c.BypassNro)
            spec.Add("reg add HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE /v BypassNRO /t REG_DWORD /d 1 /f");
        if (c.PreventBitLocker)
            spec.Add("reg add HKLM\\SYSTEM\\CurrentControlSet\\Control\\BitLocker /v PreventDeviceEncryption /t REG_DWORD /d 1 /f");
        if (user.Length > 0)
        {
            spec.Add($"net user \"{user}\" /logonpasswordchg:yes");
            spec.Add("net accounts /maxpwage:unlimited");
        }
        if (spec.Count > 0)
        {
            sb.AppendLine("  <settings pass=\"specialize\">");
            sb.AppendLine($"    <component name=\"Microsoft-Windows-Deployment\" processorArchitecture=\"{warch}\" language=\"neutral\" {Ns} publicKeyToken=\"31bf3856ad364e35\" versionScope=\"nonSxS\">");
            sb.AppendLine("      <RunSynchronous>");
            int o = 1;
            foreach (var cmd in spec)
                sb.AppendLine($"        <RunSynchronousCommand wcm:action=\"add\"><Order>{o++}</Order><Path>{cmd}</Path></RunSynchronousCommand>");
            sb.AppendLine("      </RunSynchronous>");
            sb.AppendLine("    </component>");
            sb.AppendLine("  </settings>");
        }

        bool oobe = c.SkipPrivacy || user.Length > 0 || c.HostLocale;
        if (oobe)
        {
            sb.AppendLine("  <settings pass=\"oobeSystem\">");
            sb.AppendLine($"    <component name=\"Microsoft-Windows-Shell-Setup\" processorArchitecture=\"{warch}\" language=\"neutral\" {Ns} publicKeyToken=\"31bf3856ad364e35\" versionScope=\"nonSxS\">");
            if (c.SkipPrivacy)
                sb.AppendLine("      <OOBE><HideEULAPage>true</HideEULAPage><HideOEMRegistrationScreen>true</HideOEMRegistrationScreen><HideOnlineAccountScreens>true</HideOnlineAccountScreens><HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE><ProtectYourPC>3</ProtectYourPC></OOBE>");
            if (c.HostLocale && tzXml.Length > 0)
            {
                sb.AppendLine($"      <TimeZone>{tzXml}</TimeZone>");
            }
            if (user.Length > 0)
            {
                const string blank = "UABhAHMAcwB3AG8AcgBkAA==";
                sb.AppendLine($"      <UserAccounts><LocalAccounts><LocalAccount wcm:action=\"add\"><Password><Value>{blank}</Value><PlainText>false</PlainText></Password><DisplayName>{userXml}</DisplayName><Group>Administrators</Group><Name>{userXml}</Name></LocalAccount></LocalAccounts></UserAccounts>");
                sb.AppendLine($"      <AutoLogon><Password><Value>{blank}</Value><PlainText>false</PlainText></Password><Enabled>true</Enabled><LogonCount>1</LogonCount><Username>{userXml}</Username></AutoLogon>");
            }
            sb.AppendLine("    </component>");
            sb.AppendLine("  </settings>");
        }

        sb.AppendLine("</unattend>");

        string target;
        if (c.BypassTpm)
        {
            target = Path.Combine(usbRoot, "autounattend.xml");
        }
        else
        {
            string dir = Path.Combine(usbRoot, "sources", "$OEM$", "$$", "Panther");
            Directory.CreateDirectory(dir);
            target = Path.Combine(dir, "unattend.xml");
        }
        File.WriteAllText(target, sb.ToString(), new UTF8Encoding(false));

        var on = new List<string>();
        if (c.BypassTpm) on.Add("TPM/SB/RAM/CPU bypass");
        if (c.BypassNro) on.Add("online account bypass");
        if (user.Length > 0) on.Add($"local user '{user}' + autologon");
        if (c.SkipPrivacy) on.Add("OOBE cleanup");
        if (c.PreventBitLocker) on.Add("BitLocker block");
        if (c.HostLocale) on.Add("host timezone");
        L($"Setup tweaks written to {Path.GetFileName(target)}: {string.Join(", ", on)}.");
    }

    private static string SanitizeUser(string n)
    {
        if (string.IsNullOrWhiteSpace(n)) return "";
        if (n.Trim().Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return "";
        foreach (char bad in new[] { '\\', '/', '[', ']', '"', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>' })
            n = n.Replace(bad, '_');
        n = n.Trim();
        return n.Length > 20 ? n[..20] : n;
    }
}
