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
    private readonly HddInstallService _hdd = new();
    private readonly HardwareService _hw = new();
    private readonly ImageService _img = new();
    private bool IsHddMode => TargetBox?.SelectedIndex == 1;
    private HardwareInfo _hwInfo = new("?", 0, false, false, 0);
    private readonly ObservableCollection<WindowsImage> _images = new();
    private int _step;

    public MainWindow()
    {
        InitializeComponent();
        TrySetAppLogo();
        Loaded += async (_, _) => await InitAsync();
        ImageBox.SelectionChanged += (_, _) => UpdateWarning();
        UsbBox.SelectionChanged += (_, _) => UpdateWarning();
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
            RefreshCacheText();
            UpdateWarning();
            ShowStep(0);
        }
        catch (Exception ex)
        {
            HwText.Text = "Could not read hardware: " + ex.GetBaseException().Message;
        }
    }

    private async void RefreshUsb_Click(object sender, RoutedEventArgs e) => await RefreshUsbAsync();

    private async Task RefreshUsbAsync()
    {
        if (UsbBox == null) return;
        UsbBox.IsEnabled = false;
        try
        {
            UsbBox.ItemsSource = await Task.Run(() => _usb.GetUsbDrives());
            if (UsbBox.SelectedItem == null && UsbBox.Items.Count > 0)
                UsbBox.SelectedIndex = 0;
            UpdateWarning();
        }
        finally { UsbBox.IsEnabled = true; }
    }

    private void UpdateWarning()
    {
        if (WarnText == null || ImageDesc == null) return;
        WarnText.Text = "";
        ImageDesc.Text = "";
        if (ImageBox.SelectedItem is WindowsImage img)
        {
            string size = img.SizeBytes > 0 ? FmtBytes(img.SizeBytes) :
                (!string.IsNullOrEmpty(img.LocalPath) && File.Exists(img.LocalPath)
                    ? FmtBytes(new FileInfo(img.LocalPath).Length) : "size unknown");
            ImageDesc.Text = $"{img.Name} · {img.Arch} · {size} · min RAM {img.MinRamMb} MB";
            var warn = _hwInfo.CheckCompatibility(img);
            if (warn != null) WarnText.Text = "⚠ " + warn;
            if (IsHddMode)
            {
                if (img.OsFamily != "Windows" && img.OsFamily != "Unknown")
                    WarnText.Text += (WarnText.Text.Length > 0 ? "\n" : "") +
                        "⚠ HDD mode supports Windows Setup images only — Linux still needs a USB stick.";
                if (HddBox?.SelectedItem is HddInstallService.HddTarget hdd && hdd.FreeBytes > 0)
                {
                    long need = img.SizeBytes > 0 ? img.SizeBytes :
                        (!string.IsNullOrEmpty(img.LocalPath) && File.Exists(img.LocalPath)
                            ? new FileInfo(img.LocalPath).Length : 0);
                    long needHdd = need + 512L * 1024 * 1024;
                    if (need > 0 && hdd.FreeBytes < needHdd)
                        WarnText.Text += (WarnText.Text.Length > 0 ? "\n" : "") +
                            $"⚠ Not enough space on {hdd.Root} (free {FmtBytes(hdd.FreeBytes)}, need {FmtBytes(needHdd)}).";
                }
                var st = _hdd.GetState();
                if (st.Installed)
                    WarnText.Text += (WarnText.Text.Length > 0 ? "\n" : "") + $"⚠ HDD setup already present at {st.SetupDir}. Remove it first.";
            }
            else if (UsbBox.SelectedItem is UsbDrive usb && usb.SizeBytes > 0)
            {
                long need = img.SizeBytes > 0 ? img.SizeBytes :
                    (!string.IsNullOrEmpty(img.LocalPath) && File.Exists(img.LocalPath)
                        ? new FileInfo(img.LocalPath).Length : 0);
                if (need > 0 && usb.SizeBytes < (ulong)need)
                    WarnText.Text += (WarnText.Text.Length > 0 ? "\n" : "") +
                        $"⚠ USB too small: {FmtBytes((long)usb.SizeBytes)} < {FmtBytes(need)} needed.";
            }
            BypassCheck.IsEnabled = img.NeedsTpm && img.OsFamily == "Windows";
            if (!BypassCheck.IsEnabled) BypassCheck.IsChecked = false;
            bool isWin = img.OsFamily == "Windows";
            WinTweaks.Visibility = isWin ? Visibility.Visible : Visibility.Collapsed;
            if (HddBox != null) RefreshHddTargetsForWarning(img);
        }
    }

    private void RefreshHddTargetsForWarning(WindowsImage img)
    {
        if (!IsHddMode) return;
        try
        {
            long need = img.SizeBytes > 0 ? img.SizeBytes :
                (!string.IsNullOrEmpty(img.LocalPath) && File.Exists(img.LocalPath)
                    ? new FileInfo(img.LocalPath).Length : 0);
            var targets = _hdd.GetTargets(need);
            HddBox.ItemsSource = targets;
            if (HddBox.SelectedItem == null && HddBox.Items.Count > 0) HddBox.SelectedIndex = 0;
        }
        catch { }
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
        StartBtn.Content = IsHddMode ? "Install to this PC" : "Flash";
        PaintBars();
        if (_step == 1 && !_busy)
        {
            _ = RefreshUsbAsync();
            if (IsHddMode) RefreshHddTargets();
        }
        if (_step == 2) UpdateSummary();
    }

    private void UpdateSummary()
    {
        var img = ImageBox.SelectedItem as WindowsImage;
        string isoSize = img == null ? "—" :
            img.SizeBytes > 0 ? FmtBytes(img.SizeBytes) :
            (!string.IsNullOrEmpty(img.LocalPath) && File.Exists(img.LocalPath)
                ? FmtBytes(new FileInfo(img.LocalPath).Length) : "size unknown");
        var opts = new List<string>();
        if (!IsHddMode && VerifyCheck.IsChecked == true) opts.Add("verify");
        if (!IsHddMode && BadBlocksCheck.IsChecked == true) opts.Add("filesystem check");
        if (BypassCheck.IsChecked == true) opts.Add("TPM bypass");
        string targetLine;
        string fsLine = "";
        if (IsHddMode)
        {
            var hdd = HddBox?.SelectedItem as HddInstallService.HddTarget;
            targetLine = $"Target: this PC ({hdd?.Root ?? "—"} \\{HddInstallService.SetupDirName}, boot once)";
            fsLine = "";
        }
        else
        {
            var usb = UsbBox.SelectedItem as UsbDrive;
            bool ntfs = FsBox.SelectedIndex == 1;
            bool gpt = PartBox.SelectedIndex == 1;
            string fs = ntfs ? "NTFS (no split)" : "FAT32 (split)";
            string part = gpt ? "GPT" : "MBR";
            targetLine = $"USB: {usb?.Display ?? "—"}";
            fsLine = (img?.OsFamily == "Windows" ? $"\n{part} · {fs} · label {UsbService.SanitizeLabel(LabelBox.Text)}" : "");
        }
        SummaryText.Text = $"Image: {img?.Name ?? "—"} ({isoSize})" +
            $"\n{targetLine}" +
            (img?.LocalPath != null ? $"\nFile: {img.LocalPath}" : "") +
            (!string.IsNullOrWhiteSpace(img?.DownloadUrl) ? $"\nURL: {img.DownloadUrl}" : "") +
            fsLine +
            (opts.Count > 0 ? $"\nOptions: {string.Join(", ", opts)}" : "");
    }

    private void RefreshCacheText()
    {
        try
        {
            var items = CacheService.ListCachedIsos();
            long total = items.Sum(x => x.Size);
            CacheText.Text = items.Count == 0
                ? "Cache: empty."
                : $"Cache: {items.Count} ISO(s), {CacheService.FormatBytes(total)} in %TEMP%\\Multi-Win.";
        }
        catch { CacheText.Text = ""; }
    }

    private void FidoBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string msg = CatalogService.LaunchMicrosoftFlow(Log);
        ImageDesc.Text = msg;
        Log(msg);
    }

    private void TargetBox_Changed(object sender, RoutedEventArgs e)
    {
        if (UsbOptions == null || HddPanel == null) return;
        bool hdd = IsHddMode;
        UsbOptions.Visibility = hdd ? Visibility.Collapsed : Visibility.Visible;
        HddPanel.Visibility = hdd ? Visibility.Visible : Visibility.Collapsed;
        if (StartBtn != null) StartBtn.Content = hdd ? "Install to this PC" : "Flash";
        if (hdd) RefreshHddTargets();
        UpdateWarning();
        if (_step == 2) UpdateSummary();
    }

    private void RefreshHddTargets(long needBytes = 0)
    {
        try
        {
            var targets = _hdd.GetTargets(needBytes);
            HddBox.ItemsSource = targets;
            if (HddBox.SelectedItem == null && HddBox.Items.Count > 0)
                HddBox.SelectedIndex = 0;
            var st = _hdd.GetState();
            HddHint.Text = st.Installed
                ? $"Previous HDD setup found at {st.SetupDir}. Remove it before creating a new one."
                : "Setup files are copied to \\MULTIWIN-SETUP and the PC boots into Setup once (default boot entry untouched). Linux images still need a USB stick.";
        }
        catch (Exception ex)
        {
            HddHint.Text = "Could not list internal drives: " + ex.GetBaseException().Message;
        }
    }

    private async void RemoveHdd_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var confirm = MessageBox.Show(
            "Delete the HDD setup folder and its boot entry?",
            "Multi-Win — remove HDD setup",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            await Task.Run(() => _hdd.RemoveHddSetup(Log));
            RefreshHddTargets();
        }
        catch (Exception ex)
        {
            Log("Remove failed: " + ex.GetBaseException().Message);
        }
    }
    private void SourceBox_Changed(object sender, RoutedEventArgs e)
    {
        if (BrowsePanel == null || UrlPanel == null) return;
        bool browse = SourceBox.SelectedIndex == 1;
        bool url = SourceBox.SelectedIndex == 2;
        BrowsePanel.Visibility = browse ? Visibility.Visible : Visibility.Collapsed;
        UrlPanel.Visibility = url ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UrlBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            AddUrl_Click(sender, e);
        }
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        int n = CacheService.ClearCache(Log);
        RefreshCacheText();
        ImageDesc.Text = n > 0 ? $"Cache cleared ({n} file(s))." : "Cache is already empty.";
        Log(n > 0 ? $"Cache cleared ({n} file(s))." : "Cache is already empty.");
    }

    private async void Bench_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (UsbBox.SelectedItem is not UsbDrive usb)
        { WarnText.Text = "Pick a USB drive first"; return; }
        string? letter = await Task.Run(() => _usb.TryGetVolumeLetter(usb));
        if (letter == null)
        { BenchText.Text = "No volume letter — format the drive first or re-plug it."; return; }
        BenchText.Text = $"Testing {letter} (128MB write+read)...";
        BenchBtn.IsEnabled = false;
        try
        {
            var (w, r) = await Task.Run(() => UsbCheckService.Benchmark(letter, Log));
            BenchText.Text = $"{letter}: write {w:F1} MB/s · read {r:F1} MB/s";
        }
        catch (Exception ex)
        {
            BenchText.Text = "Benchmark failed: " + ex.GetBaseException().Message;
            Log("Benchmark failed: " + ex.GetBaseException().Message);
        }
        finally { BenchBtn.IsEnabled = true; }
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt",
            FileName = $"multiwin-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            Title = "Save flash log"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllLines(dlg.FileName, LogBox.Items.Cast<object>().Select(x => x.ToString() ?? ""));
            Log($"Log saved to {dlg.FileName}");
        }
        catch (Exception ex)
        {
            Log("Could not save log: " + ex.GetBaseException().Message);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasIsoDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_busy) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var isos = files
            .Where(f => f.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && File.Exists(f))
            .ToList();
        ShowStep(0);
        if (isos.Count == 0) { ImageDesc.Text = "Drop an .iso file."; return; }
        foreach (var f in isos) await AddLocalIsoFileAsync(f);
    }

    private static bool HasIsoDrop(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) &&
        e.Data.GetData(DataFormats.FileDrop) is string[] files &&
        files.Any(f => f.EndsWith(".iso", StringComparison.OrdinalIgnoreCase));

    private async void BrowseIso_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "ISO images (*.iso)|*.iso|All files (*.*)|*.*",
            Title = "Choose ISO image"
        };
        if (dlg.ShowDialog() != true) return;
        await AddLocalIsoFileAsync(dlg.FileName);
    }

    private async Task AddLocalIsoFileAsync(string path)
    {
        var d0 = IsoDetect.FromFileName(path);
        var fi = new FileInfo(path);
        bool w11guess = IsoDetect.IsWindows11(d0.Name);
        var item = new WindowsImage(
            Id: "local-" + Guid.NewGuid().ToString("N")[..8],
            Name: d0.Name + " (local ISO)",
            Version: d0.Name,
            Arch: IsoDetect.DetectArch(path),
            DownloadUrl: "",
            Sha256: "",
            SizeBytes: fi.Length,
            MinRamMb: d0.MinRamMb,
            NeedsTpm: w11guess,
            NeedsUefi: w11guess)
        {
            OsFamily = d0.OsFamily,
            LocalPath = path,
            Icon = d0.Icon
        };
        _images.Add(item);
        ImageBox.SelectedItem = item;
        SourceBox.SelectedIndex = 0;
        UpdateWarning();
        ImageDesc.Text = $"Inspecting ISO contents...\nFile: {path}";

        var d = await IsoInspect.InspectAsync(path);
        int idx = _images.IndexOf(item);
        if (idx < 0) return;
        bool w11 = IsoDetect.IsWindows11(d.Name);
        var confirmed = item with
        {
            Name = d.Name + " (local ISO)",
            Version = d.Name,
            MinRamMb = d.MinRamMb,
            OsFamily = d.OsFamily,
            Icon = d.Icon,
            NeedsTpm = w11,
            NeedsUefi = w11
        };
        _images[idx] = confirmed;
        ImageBox.SelectedItem = confirmed;
        UpdateWarning();
        ImageDesc.Text = $"Detected: {d.Name} ({(d.FromContents ? "ISO contents" : "file name")})\nFile: {path}";
    }

    private async void AddUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string url = UrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        { ImageDesc.Text = "Paste a direct http(s) link to an .iso file."; return; }
        string fileName = Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath));
        if (!fileName.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        { ImageDesc.Text = "Link must point to an .iso file."; return; }
        var d = IsoDetect.FromFileName(fileName);
        bool w11url = IsoDetect.IsWindows11(d.Name);
        var item = new WindowsImage(
            Id: "url-" + Guid.NewGuid().ToString("N")[..8],
            Name: d.Name + " (URL)",
            Version: d.Name,
            Arch: IsoDetect.DetectArch(fileName),
            DownloadUrl: url,
            Sha256: "",
            SizeBytes: 0,
            MinRamMb: d.MinRamMb,
            NeedsTpm: w11url,
            NeedsUefi: w11url)
        {
            OsFamily = d.OsFamily,
            Icon = d.Icon
        };
        _images.Add(item);
        ImageBox.SelectedItem = item;
        SourceBox.SelectedIndex = 0;
        UpdateWarning();
        ImageDesc.Text = $"Checking link...\n{url}";
        long size = await TryGetContentLengthAsync(url);
        int idx = _images.IndexOf(item);
        if (idx < 0) return;
        var sized = item with { SizeBytes = Math.Max(0, size) };
        _images[idx] = sized;
        ImageBox.SelectedItem = sized;
        UpdateWarning();
        ImageDesc.Text = size > 0
            ? $"Detected: {d.Name} (file name) · {FmtBytes(size)}\n{url}"
            : $"Detected: {d.Name} (file name, size unknown)\n{url}";
    }

    private static async Task<long> TryGetContentLengthAsync(string url)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, url);
            using var resp = await http.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            return resp.Content.Headers.ContentLength ?? -1;
        }
        catch { return -1; }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("Cancelling...");
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_step == 0 && ImageBox.SelectedItem is null)
        { ImageDesc.Text = "Pick an image first"; return; }
        if (_step == 1)
        {
            if (IsHddMode)
            {
                if (ImageBox.SelectedItem is WindowsImage hi && hi.OsFamily != "Windows" && hi.OsFamily != "Unknown")
                { WarnText.Text = "HDD mode supports Windows Setup images only. Switch to USB for this image."; return; }
                if (HddBox.SelectedItem is null)
                { WarnText.Text = "Pick an internal drive"; return; }
                var st = _hdd.GetState();
                if (st.Installed)
                { WarnText.Text = $"HDD setup already present at {st.SetupDir}. Remove it first."; return; }
            }
            else if (UsbBox.SelectedItem is null)
            { WarnText.Text = "Pick a USB drive first"; return; }
        }
        ShowStep(_step + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ShowStep(_step - 1);
    }

    private bool _busy;
    private CancellationTokenSource? _cts;

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        LogBox.Items.Add(line);
        while (LogBox.Items.Count > 2000)
            LogBox.Items.RemoveAt(0);
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
        if (ImageBox.SelectedItem is not WindowsImage img0)
        { StatusText.Text = "Pick an image"; return; }
        bool hddMode = IsHddMode;
        UsbDrive? usb = UsbBox.SelectedItem as UsbDrive;
        HddInstallService.HddTarget? hddTarget = HddBox?.SelectedItem as HddInstallService.HddTarget;
        if (hddMode)
        {
            if (img0.OsFamily != "Windows" && img0.OsFamily != "Unknown")
            { StatusText.Text = "HDD mode supports Windows images only"; Log("HDD mode supports Windows Setup images only — use USB for this image."); return; }
            if (hddTarget is null)
            { StatusText.Text = "Pick an internal drive"; return; }
            var st0 = _hdd.GetState();
            if (st0.Installed)
            { StatusText.Text = $"HDD setup already present at {st0.SetupDir}"; Log($"HDD setup already present at {st0.SetupDir}. Remove it first."); return; }
        }
        else if (usb is null)
        { StatusText.Text = "Pick a USB drive"; return; }

        bool ntfsMode = FsBox.SelectedIndex == 1;
        string label = UsbService.SanitizeLabel(LabelBox.Text);
        MessageBoxResult confirm;
        if (hddMode)
        {
            confirm = MessageBox.Show(
                $"Setup files will be copied to {hddTarget!.Root}\\{HddInstallService.SetupDirName} " +
                $"and the PC will boot into Setup ONCE (default boot entry untouched).\n\n" +
                $"Image: {img0.Name}\nDrive: {hddTarget!.Root} (free {FmtBytes(hddTarget!.FreeBytes)})\n\n" +
                "After Setup finishes, the folder and its boot entry are removed.\nContinue?",
                "Multi-Win — confirm HDD install",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        }
        else
        {
            confirm = MessageBox.Show(
                $"Drive \"{usb!.Model}\" will be FORMATTED and all data on it will be erased.\n\n" +
                $"Image: {img0.Name}\nUSB: {usb!.Display}\n" +
                (img0.OsFamily == "Windows"
                    ? $"Format: {(ntfsMode ? "NTFS (no split)" : "FAT32 (split)")} · label {label}\n"
                    : "Mode: raw (dd-style) write\n") +
                "\nContinue?",
                "Multi-Win — confirm flash",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        }
        if (confirm != MessageBoxResult.Yes)
        { StatusText.Text = "Cancelled"; Log("Cancelled by user."); return; }

        _busy = true;
        StartBtn.IsEnabled = false;
        BackBtn.IsEnabled = false;
        NextBtn.IsEnabled = false;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        CancelBtn.Visibility = Visibility.Visible;
        bool downloadedNow = false;
        string isoPath = "";
        try
        {
            WindowsImage img = img0;
            Log(hddMode
                ? $"Start HDD install: {img.Name} -> {hddTarget!.Root}\\{HddInstallService.SetupDirName}" +
                  (BypassCheck.IsChecked == true ? " (TPM bypass ON)" : "")
                : $"Start: {img.Name} -> {usb!.Model}" +
                  (BypassCheck.IsChecked == true ? " (TPM bypass ON)" : ""));
            var dir = Path.Combine(Path.GetTempPath(), "Multi-Win");
            Directory.CreateDirectory(dir);
            bool isLocal = !string.IsNullOrEmpty(img.LocalPath);
            isoPath = isLocal ? img.LocalPath! : Path.Combine(dir, img.Id + ".iso");

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
                await _img.DownloadAsync(img.DownloadUrl, isoPath, prog, ct);
                downloadedNow = true;
                Log("Download finished.");
            }

            if (!string.IsNullOrWhiteSpace(img.Sha256))
            {
                StatusText.Text = "Verifying hash...";
                SpeedText.Text = "";
                Progress.IsIndeterminate = true;
                Log("Verifying SHA256 (may take a while on big ISOs)...");
                ct.ThrowIfCancellationRequested();
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

            string family = img.OsFamily;
            if (!isLocal)
            {
                Log("Checking downloaded image contents...");
                var di = await IsoInspect.InspectAsync(isoPath, ct);
                family = di.OsFamily;
                Log($"Downloaded image is: {di.Name} ({(di.FromContents ? "ISO contents" : "file name")})");
                bool dlW11 = IsoDetect.IsWindows11(di.Name);
                if (family == "Windows" && dlW11 != img.NeedsTpm)
                {
                    int diIdx = _images.IndexOf(img);
                    if (diIdx >= 0)
                    {
                        var updated = img with
                        {
                            Name = di.Name + " (URL)",
                            Version = di.Name,
                            MinRamMb = di.MinRamMb,
                            OsFamily = di.OsFamily,
                            Icon = di.Icon,
                            NeedsTpm = dlW11,
                            NeedsUefi = dlW11
                        };
                        _images[diIdx] = updated;
                        ImageBox.SelectedItem = updated;
                        img = updated;
                        UpdateWarning();
                        UpdateSummary();
                        Log(dlW11 ? "Confirmed Windows 11 — TPM bypass available."
                                  : "Not Windows 11 — TPM bypass disabled.");
                    }
                }
            }
            bool bypass = BypassCheck.IsChecked == true;
            string arch = img.Arch == "x86" ? "x86" : img.Arch == "arm64" ? "arm64" : "amd64";
            var custom = new WindowsCustom(
                bypass,
                BypassNroCheck.IsChecked == true,
                UserNameBox.Text,
                PrivacyCheck.IsChecked == true,
                BitLockerCheck.IsChecked == true,
                AppraiserCheck.IsChecked == true,
                LocaleCheck.IsChecked == true);
            var flashLog = new Progress<string>(m => Log(m));

            if (hddMode)
            {
                if (family != "Windows")
                {
                    StatusText.Text = "HDD mode supports Windows images only";
                    Log("ERROR: this image has no Windows Setup (sources\\boot.wim) — use USB mode.");
                    return;
                }
                StatusText.Text = "Copying Setup files...";
                SpeedText.Text = "";
                Progress.IsIndeterminate = true;
                string root = hddTarget!.Root;
                bool uefi = _hwInfo.HasUefi;
                await Task.Run(() => _hdd.Install(isoPath, arch, custom, root, uefi, flashLog, ct));
                Progress.IsIndeterminate = false;
                Progress.Value = 100;
                StatusText.Text = "Done — reboot to start Setup";
                Log($"Done. Reboot to start Windows Setup from {root}\\{HddInstallService.SetupDirName}.");
                Log("Default boot entry untouched — the PC boots into Setup once, then back to your system.");
                RefreshHddTargets();
                RefreshCacheText();
                return;
            }

            StatusText.Text = "Flashing USB...";
            SpeedText.Text = "";
            Progress.IsIndeterminate = true;
            if (BadBlocksCheck.IsChecked == true)
            {
                string? letter = await Task.Run(() => _usb.TryGetVolumeLetter(usb!));
                if (letter == null)
                {
                    Log("Bad-blocks check skipped: no volume letter (re-plug the drive).");
                }
                else
                {
                    Log($"Bad-blocks check on {letter} (1 pass, ~1 min)...");
                    StatusText.Text = "Checking for bad blocks...";
                    var bp = new Progress<double>(v =>
                    {
                        Progress.IsIndeterminate = false;
                        Progress.Value = v;
                        StatusText.Text = $"Bad-blocks check: {v:F0}%";
                    });
                    await Task.Run(() => UsbCheckService.CheckBadBlocks(letter, 1, Log, v => ((IProgress<double>)bp).Report(v), ct));
                    Progress.IsIndeterminate = true;
                    Log("Bad-blocks check passed.");
                }
            }
            bool verify = VerifyCheck.IsChecked == true;
            var fsMode = FsBox.SelectedIndex == 1
                ? UsbService.FileSystemMode.NtfsDirect
                : UsbService.FileSystemMode.Fat32Split;
            if (family != "Windows")
            {
                Log("Non-Windows ISO — raw (dd-style) write...");
                var fp = new Progress<double>(v =>
                {
                    Progress.IsIndeterminate = false;
                    Progress.Value = v;
                    StatusText.Text = $"Flashing: {v:F1}%";
                });
                await Task.Run(() => _usb.WriteRaw(isoPath, usb!, flashLog, fp, ct, verify));
            }
            else
            {
                Log($"Formatting {usb!.DeviceId} ({usb!.Model}, {usb!.PartitionStyle}, " +
                    (fsMode == UsbService.FileSystemMode.NtfsDirect ? "NTFS direct" : "FAT32 split") + ")...");
                await Task.Run(() => _usb.WriteImage(isoPath, usb!, arch, custom, flashLog, ct, fsMode, verify, label));
            }
            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            StatusText.Text = "Done ✓";
            Log("Done. USB is ready, you can close the app.");
            RefreshCacheText();
        }
        catch (OperationCanceledException)
        {
            Progress.IsIndeterminate = false;
            StatusText.Text = "Cancelled";
            Log("Cancelled by user.");
            if (downloadedNow)
            {
                try { if (File.Exists(isoPath)) File.Delete(isoPath); } catch { }
                Log("Partial download deleted.");
            }
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
            CancelBtn.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;
        }
    }
}
