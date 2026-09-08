namespace WinMultiInstaller.Models;

public record UsbDrive(
    string DeviceId,
    string Model,
    ulong SizeBytes,
    string Letter
)
{
    public string PartitionStyle { get; init; } = "?";
    public string Display { get; init; } = "";
}
