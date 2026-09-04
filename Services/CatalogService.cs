using System.IO;
using System.Text.Json;
using WinMultiInstaller.Models;

namespace WinMultiInstaller.Services;

public class CatalogService
{
    private readonly string _path = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Data", "catalog.json");

    public List<WindowsImage> Load()
    {
        var devPath = Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Data", "catalog.json"));
        var file = File.Exists(_path) ? _path : devPath;
        if (!File.Exists(file)) return new();
        var json = File.ReadAllText(file);
        return JsonSerializer.Deserialize<List<WindowsImage>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    }
}
