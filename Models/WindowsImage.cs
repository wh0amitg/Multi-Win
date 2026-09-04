namespace WinMultiInstaller.Models;

public record WindowsImage(
    string Id,
    string Name,
    string Version,
    string Arch,
    string DownloadUrl,
    string Sha256,
    long SizeBytes,
    int MinRamMb,
    bool NeedsTpm,
    bool NeedsUefi
)
{
    public string OsFamily { get; init; } = "Windows";
    public string? LocalPath { get; init; } = null;
    public string? Icon { get; init; } = null;
    public string? IconPath => Services.OsIconProvider.Resolve(Icon, OsFamily);
}
