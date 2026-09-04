using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinMultiInstaller.Models;
using WinMultiInstaller.Services;

namespace WinMultiInstaller;

public partial class MainWindow : Window
{
    private static readonly Brush BarActive = new SolidColorBrush(Color.FromRgb(0, 120, 212));
    private static readonly Brush BarTodo = new SolidColorBrush(Color.FromRgb(58, 58, 58));

    private readonly CatalogService _catalog = new();
    private readonly UsbService _usb = new();
    private readonly HardwareService _hw = new();
    private readonly ImageService _img = new();
    private HardwareInfo _hwInfo = new("?", 0, false, false, 0);
    private readonly ObservableCollection<WindowsImage> _images = new();
    private int _step;

    public MainWindow()
    {
        InitializeComponent();
        TrySetAppLogo();
        Loaded += async (_, _) => await InitAsync();
        ImageBox.SelectionChanged += (_, _) => UpdateWarning();
    }

    private void TrySetAppLogo()
    {
        try
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
            string png = Path.Combine(dir, "app-logo.png");
            string ico = Path.Combine(dir, "app.ico");
            string? f = File.Exists(png) ? png : File.Exists(ico) ? ico : null;
            if (f == null) return;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(f);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            HeaderLogo.Source = bmp;
            HeaderLogoWrap.Visibility = Visibility.Visible;
            Icon = bmp;
        }
        catch { }
    }

    private async Task InitAsync()
    {
        try
        {
            HwText.Text = "Detecting hardware...";
            _hwInfo = await Task.Run(() => _hw.GetInfo());
            HwText.Text = $"{_hwInfo.CpuName} · RAM {_hwInfo.RamMb} MB · TPM: {(_hwInfo.HasTpm ? "yes" : "no")}";
            _images.Clear();
            foreach (var i in _catalog.Load()) _images.Add(i);
            ImageBox.ItemsSource = _images;
            if (ImageBox.Items.Count > 0) ImageBox.SelectedIndex = 0;
            var drives = await Task.Run(() => _usb.GetUsbDrives());
            UsbBox.ItemsSource = drives;
            UpdateWarning();
            ShowStep(0);
        }
        catch (Exception ex)
        {
            HwText.Text = "Could not read hardware: " + ex.GetBaseException().Message;
        }
    }

    private async void RefreshUsb_Click(object sender, RoutedEventArgs e)
    {
        UsbBox.IsEnabled = false;
        try { UsbBox.ItemsSource = await Task.Run(() => _usb.GetUsbDrives()); }
        finally { UsbBox.IsEnabled = true; }
    }

    private void UpdateWarning()
    {
        WarnText.Text = "";
        ImageDesc.Text = "";
        if (ImageBox.SelectedItem is WindowsImage img)
        {
            ImageDesc.Text = $"{img.Name} · {img.Arch} · min RAM {img.MinRamMb} MB";
            var warn = _hwInfo.CheckCompatibility(img);
            if (warn != null) WarnText.Text = "⚠ " + warn;
            BypassCheck.IsEnabled = img.NeedsTpm;
            if (!img.NeedsTpm) BypassCheck.IsChecked = false;
        }
    }

    private void PaintBars()
    {
        Step1Bar.Background = _step >= 0 ? BarActive : BarTodo;
        Step2Bar.Background = _step >= 1 ? BarActive : BarTodo;
        Step3Bar.Background = _step >= 2 ? BarActive : BarTodo;
        Step1Head.Opacity = _step == 0 ? 1 : 0.5;
        Step2Head.Opacity = _step == 1 ? 1 : 0.5;
        Step3Head.Opacity = _step == 2 ? 1 : 0.5;
    }

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, 2);
        Step1Panel.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        BackBtn.IsEnabled = _step > 0;
        NextBtn.Visibility = _step < 2 ? Visibility.Visible : Visibility.Collapsed;
        StartBtn.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        PaintBars();
        if (_step == 2) UpdateSummary();
    }

    private void UpdateSummary()
    {
        var img = ImageBox.SelectedItem as WindowsImage;
        var usb = UsbBox.SelectedItem as UsbDrive;
        SummaryText.Text = $"Image: {img?.Name ?? "—"}\nUSB: {usb?.Model ?? "—"}" +
            (img?.LocalPath != null ? $"\nFile: {img.LocalPath}" : "") +
            (BypassCheck.IsChecked == true ? "\nTPM bypass: ON" : "");
    }

    private async void BrowseIso_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "ISO images (*.iso)|*.iso|All files (*.*)|*.*",
            Title = "Choose ISO image"
        };
        if (dlg.ShowDialog() != true) return;

        var d0 = IsoDetect.FromFileName(dlg.FileName);
        var fi = new FileInfo(dlg.FileName);
        var item = new WindowsImage(
            Id: "local-" + Guid.NewGuid().ToString("N")[..8],
            Name: d0.Name + " (local ISO)",
            Version: d0.Name,
            Arch: IsoDetect.DetectArch(dlg.FileName),
            DownloadUrl: "",
            Sha256: "",
            SizeBytes: fi.Length,
            MinRamMb: d0.MinRamMb,
            NeedsTpm: false,
            NeedsUefi: false)
        {
            OsFamily = d0.OsFamily,
            LocalPath = dlg.FileName,
            Icon = d0.Icon
        };
        _images.Add(item);
        ImageBox.SelectedItem = item;
        UpdateWarning();
        ImageDesc.Text = $"Inspecting ISO contents...\nFile: {dlg.FileName}";

        var d = await IsoInspect.InspectAsync(dlg.FileName);
        int idx = _images.IndexOf(item);
        if (idx < 0) return;
        var confirmed = item with
        {
            Name = d.Name + " (local ISO)",
            Version = d.Name,
            MinRamMb = d.MinRamMb,
            OsFamily = d.OsFamily,
            Icon = d.Icon
        };
        _images[idx] = confirmed;
        ImageBox.SelectedItem = confirmed;
        UpdateWarning();
        ImageDesc.Text = $"Detected: {d.Name} ({(d.FromContents ? "ISO contents" : "file name")})\nFile: {dlg.FileName}";
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_step == 0 && ImageBox.SelectedItem is null)
        { ImageDesc.Text = "Pick an image first"; return; }
        if (_step == 1 && UsbBox.SelectedItem is null)
        { WarnText.Text = "Pick a USB drive first"; return; }
        ShowStep(_step + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ShowStep(_step - 1);
    }

    private bool _busy;

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        LogBox.Items.Add(line);
        LogBox.ScrollIntoView(LogBox.Items[^1]);
    }

    private static string FmtBytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024L * 1024 => $"{b / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:F1} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:F2} GB",
    };

    private static string FmtEta(double seconds) => double.IsInfinity(seconds) || double.IsNaN(seconds) || seconds < 0
        ? "--:--"
        : $"{(int)(seconds / 60):D2}:{(int)(seconds % 60):D2}";

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (ImageBox.SelectedItem is not WindowsImage img)
        { StatusText.Text = "Pick an image"; return; }
        if (UsbBox.SelectedItem is not UsbDrive usb)
        { StatusText.Text = "Pick a USB drive"; return; }

        var confirm = MessageBox.Show(
            $"Drive \"{usb.Model}\" will be FORMATTED and all data on it will be erased.\n\n" +
            $"Image: {img.Name}\nUSB: {usb.Model}\n\nContinue?",
            "Multi-Win — confirm flash",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        { StatusText.Text = "Cancelled"; Log("Cancelled by user."); return; }

        _busy = true;
        StartBtn.IsEnabled = false;
        BackBtn.IsEnabled = false;
        NextBtn.IsEnabled = false;
        try
        {
            Log($"Start: {img.Name} -> {usb.Model}" +
                (BypassCheck.IsChecked == true ? " (TPM bypass ON)" : ""));
            var dir = Path.Combine(Path.GetTempPath(), "Multi-Win");
            Directory.CreateDirectory(dir);
            bool isLocal = !string.IsNullOrEmpty(img.LocalPath);
            var isoPath = isLocal ? img.LocalPath! : Path.Combine(dir, img.Id + ".iso");

            if (isLocal)
            {
                Log($"Using local ISO: {isoPath} ({FmtBytes(new FileInfo(isoPath).Length)})");
                Progress.Value = 100;
            }
            else if (File.Exists(isoPath))
            {
                Log($"ISO already cached ({FmtBytes(new FileInfo(isoPath).Length)}), skipping download.");
                Progress.Value = 100;
            }
            else
            {
                Log($"Downloading from {img.DownloadUrl}");
                StatusText.Text = $"Downloading {img.Name}...";
                var prog = new Progress<ImageService.DownloadProgress>(p =>
                {
                    if (p.Percent >= 0) Progress.Value = p.Percent;
                    var eta = p.BytesPerSecond > 0 && p.TotalBytes > 0
                        ? (p.TotalBytes - p.DownloadedBytes) / p.BytesPerSecond : -1;
                    SpeedText.Text = p.TotalBytes > 0
                        ? $"{FmtBytes((long)p.BytesPerSecond)}/s · {FmtBytes(p.DownloadedBytes)} / {FmtBytes(p.TotalBytes)} · ETA {FmtEta(eta)}"
                        : $"{FmtBytes((long)p.BytesPerSecond)}/s · {FmtBytes(p.DownloadedBytes)} downloaded";
                    StatusText.Text = p.Percent >= 0 ? $"Downloading: {p.Percent:F1}%" : "Downloading...";
                });
                await _img.DownloadAsync(img.DownloadUrl, isoPath, prog);
                Log("Download finished.");
            }

            if (!string.IsNullOrWhiteSpace(img.Sha256))
            {
                StatusText.Text = "Verifying hash...";
                SpeedText.Text = "";
                Progress.IsIndeterminate = true;
                Log("Verifying SHA256 (may take a while on big ISOs)...");
                var hash = await Task.Run(() => ImageService.Sha256Of(isoPath));
                Progress.IsIndeterminate = false;
                Progress.Value = 100;
                if (!hash.Equals(img.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    StatusText.Text = "Hash mismatch! File is corrupted.";
                    Log("ERROR: hash mismatch, deleting corrupted file.");
                    try { File.Delete(isoPath); } catch { }
                    return;
                }
                Log("Hash OK.");
            }

            StatusText.Text = "Flashing USB...";
            SpeedText.Text = "";
            Progress.IsIndeterminate = true;
            bool bypass = BypassCheck.IsChecked == true;
            var flashLog = new Progress<string>(m => Log(m));
            if (img.OsFamily != "Windows")
            {
                Log("Non-Windows ISO — raw (dd-style) write...");
                var fp = new Progress<double>(v =>
                {
                    Progress.IsIndeterminate = false;
                    Progress.Value = v;
                    StatusText.Text = $"Flashing: {v:F1}%";
                });
                await Task.Run(() => _usb.WriteRaw(isoPath, usb, flashLog, fp));
            }
            else
            {
                Log($"Formatting {usb.DeviceId} ({usb.Model})...");
                await Task.Run(() => _usb.WriteImage(isoPath, usb, bypass, flashLog));
            }
            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            StatusText.Text = "Done ✓";
            Log("Done. USB is ready, you can close the app.");
        }
        catch (Exception ex)
        {
            Progress.IsIndeterminate = false;
            StatusText.Text = "Error: " + ex.Message;
            Log("ERROR: " + ex.GetBaseException().Message);
        }
        finally
        {
            _busy = false;
            StartBtn.IsEnabled = true;
            BackBtn.IsEnabled = _step > 0;
            NextBtn.IsEnabled = true;
        }
    }
}
