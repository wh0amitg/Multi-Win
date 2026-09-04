namespace WinMultiInstaller.Models;

public record UsbDrive(
    string DeviceId,
    string Model,
    ulong SizeBytes,
    string Letter
);
