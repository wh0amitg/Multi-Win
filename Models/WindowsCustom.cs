namespace WinMultiInstaller.Models;

public record WindowsCustom(
    bool BypassTpm,
    bool BypassNro,
    string Username,
    bool SkipPrivacy,
    bool PreventBitLocker,
    bool EmptyAppraiser,
    bool HostLocale
)
{
    public bool HasTweaks() => BypassTpm || BypassNro || !string.IsNullOrWhiteSpace(Username)
        || SkipPrivacy || PreventBitLocker || HostLocale;
}
