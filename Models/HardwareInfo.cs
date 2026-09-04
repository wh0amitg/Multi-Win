namespace WinMultiInstaller.Models;

public record HardwareInfo(
    string CpuName,
    int RamMb,
    bool HasTpm,
    bool HasUefi,
    ulong DiskFreeBytes
)
{
    public string? CheckCompatibility(WindowsImage img)
    {
        if (RamMb < img.MinRamMb)
            return $"Low RAM: {RamMb} MB, {img.MinRamMb} MB required. It will be unstable.";
        if (img.NeedsTpm && !HasTpm)
            return "No TPM 2.0 — Win11 will install only with bypass, no updates, at your own risk.";
        if (img.NeedsUefi && !HasUefi)
            return "No UEFI — Win11 will install only in Legacy mode with bypass.";
        return null;
    }
}
