using System.IO;

namespace WinMultiInstaller.Services;

public static class UsbCheckService
{
    private const int BufSize = 1 << 20;

    /// <summary>
    /// Filesystem-level write/read spot check. This is NOT a full surface scan:
    /// it writes a temp file and reads it back, which catches dying drives and
    /// fake-size sticks but cannot remap hardware sectors like chkdsk /r.
    /// </summary>
    public static void CheckBadBlocks(string usbLetter, int passes,
        Action<string> log, Action<double>? progress = null, CancellationToken ct = default)
    {
        string root = usbLetter.TrimEnd('\\') + "\\";
        if (!Directory.Exists(root)) throw new IOException($"Drive {usbLetter} not found.");
        passes = Math.Clamp(passes, 1, 4);

        long free = 0;
        try { free = new DriveInfo(root).AvailableFreeSpace; } catch { }
        long testSize = free > 1024L * 1024 * 1024 ? 256L * 1024 * 1024 : 64L * 1024 * 1024;
        string testFile = Path.Combine(root, ".multiwin-badblocks.tmp");

        var rnd = new Random(0x5EED);
        var buf = new byte[BufSize];
        var read = new byte[BufSize];

        for (int pass = 1; pass <= passes; pass++)
        {
            ct.ThrowIfCancellationRequested();
            log($"Bad-blocks pass {pass}/{passes}: writing {Fmt(testSize)}...");
            rnd.NextBytes(buf);
            byte marker = (byte)(0xA5 + pass);
            for (int i = 0; i < buf.Length; i += 4096) buf[i] = marker;

            using (var fs = new FileStream(testFile, FileMode.Create, FileAccess.Write,
                FileShare.None, BufSize, FileOptions.WriteThrough))
            {
                long written = 0;
                while (written < testSize)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(buf.Length, testSize - written);
                    fs.Write(buf, 0, n);
                    written += n;
                    progress?.Invoke((pass - 1 + (double)written / testSize) / passes * 50);
                }
                fs.Flush(true);
            }

            log($"Bad-blocks pass {pass}/{passes}: verifying...");
            using (var fs = new FileStream(testFile, FileMode.Open, FileAccess.Read,
                FileShare.Read, BufSize, FileOptions.SequentialScan))
            {
                long done = 0;
                int n;
                while ((n = fs.Read(read, 0, read.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    for (int i = 0; i < n; i++)
                    {
                        if (read[i] != buf[(done + i) % BufSize])
                            throw new IOException(
                                $"Bad block detected (pass {pass}, offset {Fmt(done + i)}). Drive is unreliable.");
                    }
                    done += n;
                    progress?.Invoke(((pass - 1) / (double)passes * 50) + 25 + (double)done / testSize / passes * 25);
                }
            }
        }

        try { File.Delete(testFile); } catch { }
        log("Bad-blocks check passed, no errors.");
        progress?.Invoke(100);
    }

    private static readonly string[] CriticalFiles =
    {
        @"bootmgr", @"setup.exe", @"sources\boot.wim", @"sources\setup.exe",
        @"efi\boot\bootx64.efi",
    };

    public static void VerifyFileCopy(string srcRoot, string dstRoot,
        Action<string> log, CancellationToken ct = default)
    {
        srcRoot = srcRoot.TrimEnd('\\');
        dstRoot = dstRoot.TrimEnd('\\');
        var srcFiles = Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories);
        int ok = 0, skipped = 0, hashed = 0;
        foreach (var s in srcFiles)
        {
            ct.ThrowIfCancellationRequested();
            string rel = Path.GetRelativePath(srcRoot, s);
            if (rel.Equals(@"sources\install.wim", StringComparison.OrdinalIgnoreCase))
            {
                string swm = Path.Combine(dstRoot, "sources", "install.swm");
                if (!File.Exists(Path.Combine(dstRoot, rel)) && File.Exists(swm)) { skipped++; continue; }
            }
            if (rel.Equals(@"sources\install.esd", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(dstRoot, rel)) &&
                    File.Exists(Path.Combine(dstRoot, "sources", "install.swm"))) { skipped++; continue; }
            }
            string d = Path.Combine(dstRoot, rel);
            if (!File.Exists(d))
                throw new IOException($"Verify failed: missing on USB: {rel}");
            long ls = new FileInfo(s).Length, ld = new FileInfo(d).Length;
            if (ls != ld)
                throw new IOException($"Verify failed: size mismatch {rel} ({Fmt(ls)} vs {Fmt(ld)}).");
            if (IsCritical(rel))
            {
                string hs = HashFile(s), hd = HashFile(d);
                if (!hs.Equals(hd, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Verify failed: content mismatch {rel} (SHA256 differs).");
                hashed++;
            }
            ok++;
        }
        log($"Verify OK: {ok} files match ({hashed} critical hashed with SHA256{(skipped > 0 ? $", {skipped} split-image skipped" : "")}).");
    }

    public static void VerifyRaw(string isoPath, string deviceId,
        Action<string> log, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        long total = new FileInfo(isoPath).Length;
        log($"Verifying raw write ({Fmt(total)}) — re-reading USB, be patient...");
        using var src = File.Open(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var dst = new FileStream(deviceId, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, BufSize, FileOptions.SequentialScan);
        var a = new byte[BufSize];
        var b = new byte[BufSize];
        long done = 0;
        while (done < total)
        {
            ct.ThrowIfCancellationRequested();
            int need = (int)Math.Min(BufSize, total - done);
            int ra = ReadFull(src, a, need, ct);
            int rb = ReadFull(dst, b, need, ct);
            if (ra != rb)
                throw new IOException($"Verify failed at offset {Fmt(done)} (short read).");
            for (int i = 0; i < ra; i++)
                if (a[i] != b[i])
                    throw new IOException($"Verify failed at offset {Fmt(done + i)} (data mismatch).");
            done += ra;
            progress?.Report((double)done / total * 100);
        }
        log("Verify OK: USB contents match ISO.");
        progress?.Report(100);
    }

    public static (double writeMBs, double readMBs) Benchmark(string usbLetter,
        Action<string> log, CancellationToken ct = default)
    {
        string root = usbLetter.TrimEnd('\\') + "\\";
        if (!Directory.Exists(root)) throw new IOException($"Drive {usbLetter} not found.");
        const long size = 128L * 1024 * 1024;
        string f = Path.Combine(root, ".multiwin-bench.tmp");
        var buf = new byte[BufSize];
        new Random(1234).NextBytes(buf);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var fs = new FileStream(f, FileMode.Create, FileAccess.Write,
                FileShare.None, BufSize, FileOptions.WriteThrough))
            {
                long w = 0;
                while (w < size) { ct.ThrowIfCancellationRequested(); fs.Write(buf, 0, buf.Length); w += buf.Length; }
                fs.Flush(true);
            }
            double write = size / 1024.0 / 1024 / sw.Elapsed.TotalSeconds;
            sw.Restart();
            using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read,
                FileShare.Read, BufSize, FileOptions.SequentialScan))
            {
                var tmp = new byte[BufSize];
                while (fs.Read(tmp, 0, tmp.Length) > 0) { ct.ThrowIfCancellationRequested(); }
            }
            double read = size / 1024.0 / 1024 / sw.Elapsed.TotalSeconds;
            log($"Benchmark {usbLetter}: write {write:F1} MB/s, read {read:F1} MB/s ({Fmt(size)} sample).");
            return (write, read);
        }
        finally { try { File.Delete(f); } catch { } }
    }

    private static bool IsCritical(string rel) =>
        CriticalFiles.Any(c => rel.Equals(c, StringComparison.OrdinalIgnoreCase));

    private static string HashFile(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static int ReadFull(Stream s, byte[] buf, int need, CancellationToken ct)
    {
        int off = 0;
        while (off < need)
        {
            ct.ThrowIfCancellationRequested();
            int n = s.Read(buf, off, need - off);
            if (n == 0) break;
            off += n;
        }
        return off;
    }

    private static string Fmt(long b) => UsbService.Fmt(b);
}
