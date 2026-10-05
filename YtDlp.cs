using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiliGet;

public sealed class BatchItem
{
    public string Title { get; set; } = "";
    public string Meta { get; set; } = "";
    public string Url { get; set; } = "";
    public string FmtExpr { get; set; } = "";
    public string NameTag { get; set; } = "";   // 清晰度/编码后缀，避免不同选择互相覆盖
    public bool AudioOnly { get; set; }
    public string AudioFmt { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public Microsoft.UI.Xaml.Media.ImageSource? Cover { get; set; }
}

public sealed class FormatItem
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public int Height { get; set; }
    public int Fps { get; set; }
    public Dictionary<string, string> CodecIds { get; set; } = new();
    public Dictionary<string, long> CodecSizes { get; set; } = new();
    public override string ToString() => Label;
}

public sealed class PartItem
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public long Cid { get; set; }
    public long EpId { get; set; }
    public string CoverUrl { get; set; } = "";
    public System.Collections.Generic.List<FormatItem> Formats { get; set; } = new();
    public override string ToString() => Title;
}

public sealed class HistoryItem
{
    public string Title { get; set; } = "";
    public string Sub { get; set; } = "";
    public string Bvid { get; set; } = "";
    public string Url { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public bool IsPgc { get; set; }

    private Microsoft.UI.Xaml.Media.ImageSource? _cover;
    private bool _tried;
    // 懒加载：只有真正显示到列表里的项才会解码封面（列表虚拟化 → 每次只解码可见的几张）
    public Microsoft.UI.Xaml.Media.ImageSource? Cover
    {
        get
        {
            if (!_tried)
            {
                _tried = true;
                if (CoverUrl.Length > 0)
                    _cover = CoverCache.Get(CoverUrl, 160);
            }
            return _cover;
        }
    }
    // 离开历史页时释放封面位图，降低常驻内存（下次显示会重新懒加载）
    public void ReleaseCover() { _cover = null; _tried = false; }
}

public sealed class DownloadItem : INotifyPropertyChanged
{
    private string _status = "";
    private double _progress;
    private bool _indeterminate = true;
    private bool _progressVisible = true;
    private bool _buttonVisible = true;
    private string _buttonText = "暂停";
    public string Title { get; set; } = "";
    public string Status
    {
        get => _status;
        set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
    }
    public double Progress
    {
        get => _progress;
        set { _progress = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress))); }
    }
    public bool Indeterminate
    {
        get => _indeterminate;
        set { _indeterminate = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Indeterminate))); }
    }
    public bool ProgressVisible
    {
        get => _progressVisible;
        set { _progressVisible = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressVisible))); }
    }
    public bool ButtonVisible
    {
        get => _buttonVisible;
        set { _buttonVisible = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ButtonVisible))); }
    }
    public string ButtonText
    {
        get => _buttonText;
        set { _buttonText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ButtonText))); }
    }
    public string FilePath { get; set; } = "";
    public Microsoft.UI.Xaml.Media.ImageSource? Cover { get; set; }
    public string CoverUrl { get; set; } = "";
    public bool Paused { get; set; }
    public bool Done { get; set; }
    public string Url { get; set; } = "";
    public string FmtExpr { get; set; } = "";
    public bool AudioOnly { get; set; }
    public string AudioFmt { get; set; } = "";
    public string Dir { get; set; } = "";
    public string NameTpl { get; set; } = "%(title)s.%(ext)s";
    public CancellationTokenSource? Cts { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class YtDlp
{
    static YtDlp()
    {
        try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
    }

    public static string Dir => AppContext.BaseDirectory;

    // 运行时工具目录：某些路径（如含空格/位于文档下）会让 PyInstaller 打包的 yt-dlp 无法创建临时目录，
    // 因此把工具复制到 %LOCALAPPDATA%\BiliGet\tools 再从那里运行
    public static string RuntimeDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliGet", "tools");

    private static readonly object _copyLock = new();
    private static bool _copied;

    private static string BundledTool(string name)
    {
        var root = Path.Combine(Dir, name);
        if (File.Exists(root)) return root;
        var sub = Path.Combine(Dir, "tools", name);
        if (File.Exists(sub)) return sub;
        return root;
    }

    private static void EnsureToolsCopied()
    {
        if (_copied) return;
        lock (_copyLock)
        {
            if (_copied) return;
            try
            {
                Directory.CreateDirectory(RuntimeDir);
                foreach (var name in new[] { "yt-dlp.exe", "ffmpeg.exe", "aria2c.exe" })
                {
                    var src = BundledTool(name);
                    if (!File.Exists(src)) continue;
                    var dst = Path.Combine(RuntimeDir, name);
                    try
                    {
                        // 用字节重写（而非 File.Copy）以丢弃“来自 Internet”标记(MOTW)，
                        // 否则便携版解压后 yt-dlp 可能被系统限制、无法创建临时目录
                        var need = !File.Exists(dst)
                                   || new FileInfo(dst).Length != new FileInfo(src).Length
                                   || File.Exists(dst + ":Zone.Identifier");
                        if (need) File.WriteAllBytes(dst, File.ReadAllBytes(src));
                    }
                    catch { }
                }
            }
            catch { }
            _copied = true;
        }
    }

    private static string Tool(string name)
    {
        var bundled = BundledTool(name);
        try
        {
            if (File.Exists(bundled))
            {
                EnsureToolsCopied();
                var dst = Path.Combine(RuntimeDir, name);
                if (File.Exists(dst)) return dst;
            }
        }
        catch { }
        return bundled;
    }

    public static string ExePath => Tool("yt-dlp.exe");
    public static string FfmpegPath => Tool("ffmpeg.exe");
    public static string AriaPath => Tool("aria2c.exe");

    /// <summary>确保内置工具存在（随程序打包，无需联网下载）。</summary>
    public static async Task<bool> EnsureAsync(Action<string>? log = null)
    {
        try { await Task.Run(EnsureToolsCopied); } catch { }
        try { Diag("ready base=" + Dir + " ytdlp=" + ExePath + " ffmpeg=" + FfmpegPath + " aria=" + AriaPath); } catch { }
        if (File.Exists(ExePath)) return true;
        log?.Invoke("未找到内置 yt-dlp.exe，请确认安装完整。");
        return false;
    }

    private static List<string> BaseArgs(string? cookie, string? proxy)
    {
        var a = new List<string> { "--no-warnings" };
        if (!string.IsNullOrWhiteSpace(cookie)) { a.Add("--cookies"); a.Add(cookie!); }
        if (!string.IsNullOrWhiteSpace(proxy)) { a.Add("--proxy"); a.Add(proxy!); }
        if (File.Exists(FfmpegPath)) { a.Add("--ffmpeg-location"); a.Add(Path.GetDirectoryName(FfmpegPath)!); }
        return a;
    }

    public static async Task<(JsonDocument? doc, string error)> ParseAsync(string url, string? cookie, string? proxy, bool flatPlaylist = false)
    {
        var args = BaseArgs(cookie, proxy);
        if (flatPlaylist) args.Add("--flat-playlist");   // 合集/多P 先只拉列表，避免逐条完整解析
        args.Add("-J");
        args.Add(url);
        var (code, stdout, stderr) = await RunCaptureAsync(args);
        if (code != 0)
            return (null, string.IsNullOrWhiteSpace(stderr) ? ("yt-dlp 退出码 " + code) : stderr.Trim());
        try { return (JsonDocument.Parse(stdout), ""); }
        catch (Exception ex) { return (null, "解析输出异常：" + ex.Message); }
    }

    public static async Task<string?> DownloadAsync(string url, string formatExpr, string outDir,
        string? cookie, string? proxy, string? nameTemplate,
        Action<double, string> progress, Action<string> line, CancellationToken ct = default,
        string? audioFormat = null, bool subtitles = false)
    {
        Directory.CreateDirectory(outDir);
        var f = string.IsNullOrWhiteSpace(formatExpr) ? "bv*+ba/b" : formatExpr;

        // 输出名由调用方给出（已包含清晰的清晰度/编码后缀，避免同名覆盖）
        var tpl = string.IsNullOrWhiteSpace(nameTemplate) ? "%(title)s.%(ext)s" : nameTemplate!;

        var common = BaseArgs(cookie, proxy);
        common.Add("--no-playlist");   // 每个任务都是一个具体视频/分P，避免一次拉整套
        common.Add("-f"); common.Add(f);
        if (!string.IsNullOrEmpty(audioFormat))
        {
            common.Add("-x");
            common.Add("--audio-format"); common.Add(audioFormat);
            common.Add("--audio-quality"); common.Add("0");
        }
        else
        {
            common.Add("--merge-output-format"); common.Add("mp4");
        }
        if (subtitles)
        {
            common.Add("--write-subs");
            common.Add("--sub-langs"); common.Add("zh-CN,zh-Hans,zh-Hant,en");
            common.Add("--convert-subs"); common.Add("srt");
        }
        common.Add("--newline");
        common.Add("--no-quiet");   // --print 会隐含 --quiet，进而吞掉 aria2c 的进度readout
        common.Add("--print"); common.Add("after_move:filepath");
        common.Add("-o"); common.Add(Path.Combine(outDir, tpl));

        async Task<(bool ok, string? file)> RunAsync(bool useAria)
        {
            var args = new List<string>(common);
            if (useAria)
            {
                args.Add("--downloader"); args.Add("aria2c");
                args.Add("--downloader-args");
                args.Add("aria2c:-x16 -s16 -k1M --file-allocation=none --console-log-level=warn " +
                         "--disable-ipv6=true --check-certificate=false --continue=true --auto-file-renaming=false " +
                         "--max-tries=5 --retry-wait=2 --connect-timeout=15 --timeout=15");
            }
            args.Add(url);

            string? finalFile = null;
            var ok = await RunStreamAsync(args, s =>
            {
                line(s);

                // yt-dlp 原生：[download]  45.3% of ~120MiB at 3.20MiB/s ETA 00:12
                var m = Regex.Match(s, @"\[download\]\s+([\d.]+)%");
                if (m.Success && double.TryParse(m.Groups[1].Value, out var p))
                {
                    var at = s.IndexOf(" at ", StringComparison.Ordinal);
                    progress(p, at >= 0 ? s.Substring(at + 4).Trim() : "");
                }
                else
                {
                    // aria2c：[#15d304 22MiB/30MiB(73%) CN:14 DL:8.6MiB ETA:2s]
                    var m2 = Regex.Match(s, @"\((\d+)%\)");
                    if (m2.Success && double.TryParse(m2.Groups[1].Value, out var p2))
                    {
                        var size = Regex.Match(s, @"\s(\S+/\S+?)\(");
                        var dl = Regex.Match(s, @"DL:(\S+)");
                        var eta = Regex.Match(s, @"ETA:([^\]\s]+)");
                        var parts = new List<string>();
                        if (size.Success) parts.Add(size.Groups[1].Value);
                        if (dl.Success) parts.Add(dl.Groups[1].Value + "/s");
                        if (eta.Success) parts.Add("ETA " + eta.Groups[1].Value);
                        progress(p2, string.Join("  ·  ", parts));
                    }
                }

                // 同名同清晰度已存在（重复下载）：视为成功，指向已有文件
                if (finalFile == null)
                {
                    var mm = Regex.Match(s, @"\[download\]\s+(.+?)\s+has already been downloaded");
                    if (mm.Success) finalFile = mm.Groups[1].Value;
                }

                var t = s.Trim();
                if (finalFile == null && t.Length > 0 && t[0] != '[' &&
                    Regex.IsMatch(t, @"\.(mp4|mkv|webm|flv|mp3|m4a)$", RegexOptions.IgnoreCase))
                    finalFile = t;
            }, ct);
            return (ok, finalFile);
        }

        var useAria = File.Exists(AriaPath);
        var (ok, file) = await RunAsync(useAria);
        if (ct.IsCancellationRequested) return null;
        // 只要拿到了最终文件就算成功（即使 yt-dlp 退出码非 0，例如下载完成后合并/post-process 的次要报错）
        if (file != null) return file;
        if (ok) return null;   // 成功但没解析到文件名 → 交给上层按目录兜底查找

        if (useAria)
        {
            line("aria2c 下载失败，改用内置下载器重试…");
            progress(0, "");
            var (ok2, file2) = await RunAsync(false);
            if (ct.IsCancellationRequested) return null;
            return ok2 ? file2 : null;
        }
        return null;
    }

    private static ProcessStartInfo MakePsi(List<string> args)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = AnsiEncoding(),
            StandardErrorEncoding = AnsiEncoding(),
            WorkingDirectory = Dir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            var dirs = new[] { Dir, Path.GetDirectoryName(ExePath), Path.GetDirectoryName(AriaPath), Path.GetDirectoryName(FfmpegPath) }
                .Where(d => !string.IsNullOrEmpty(d)).Distinct();
            psi.EnvironmentVariables["PATH"] = string.Join(";", dirs) + ";" + Environment.GetEnvironmentVariable("PATH");
        }
        catch { }
        return psi;
    }

    private static void Diag(string msg)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliGet");
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, "ytdlp.log");
            try { if (new FileInfo(p).Exists && new FileInfo(p).Length > 200_000) File.Delete(p); } catch { }
            File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss") + "  " + msg + "\r\n");
        }
        catch { }
    }

    private static async Task<(int code, string stdout, string stderr)> RunCaptureAsync(List<string> args)
    {
        try { Diag("exe=" + ExePath + " cwd=" + Dir + " args=" + string.Join(' ', args)); } catch { }
        using var p = Process.Start(MakePsi(args))!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var outS = await so; var errS = await se;
        try { Diag("exit=" + p.ExitCode + " err=" + (errS.Length > 300 ? errS.Substring(0, 300) : errS)); } catch { }
        return (p.ExitCode, outS, errS);
    }

    private static async Task<bool> RunStreamAsync(List<string> args, Action<string> line, CancellationToken ct = default)
    {
        using var p = Process.Start(MakePsi(args))!;

        // 暂停：杀掉 yt-dlp 及其 aria2c 子进程（保留 .part/.aria2 供续传）
        using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });

        // aria2c 用 \r 原地刷新进度；这里按 \r / \n 同时切分，保证实时拿到进度。
        static async Task Pump(StreamReader r, Action<string> line)
        {
            try
            {
                var buf = new char[4096];
                var sb = new StringBuilder();
                int n;
                while ((n = await r.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    for (var i = 0; i < n; i++)
                    {
                        var c = buf[i];
                        if (c == '\r' || c == '\n')
                        {
                            if (sb.Length > 0) { line(sb.ToString()); sb.Clear(); }
                        }
                        else sb.Append(c);
                    }
                }
                if (sb.Length > 0) line(sb.ToString());
            }
            catch { }
        }

        try
        {
            var outPump = Pump(p.StandardOutput, line);
            var errPump = Pump(p.StandardError, line);

            // 先等 yt-dlp 主进程退出即视为流程结束。
            // 不要用 WhenAll(Pump) 作为主等待条件：子进程(aria2c/ffmpeg)可能继承管道句柄，
            // 导致一直读不到 EOF 而永久卡住（并发下载时更容易复现）。
            await p.WaitForExitAsync();

            // 再给残留输出最多 1.2s 读完（例如 after_move 打印的最终文件路径）
            await Task.WhenAny(Task.WhenAll(outPump, errPump), Task.Delay(1200));
        }
        catch { }
        try { return p.ExitCode == 0; } catch { return false; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int GetACP();

    private static Encoding AnsiEncoding()
    {
        try { return Encoding.GetEncoding(GetACP()); } catch { return Encoding.UTF8; }
    }

    /// <summary>把 yt-dlp 的英文报错翻成人话。</summary>
    public static string FriendlyError(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return "未知错误";
        var l = s.ToLowerInvariant();
        if (l.Contains("sign in") || l.Contains("login") || l.Contains("cookies"))
            return "需要登录：请点左上角头像扫码登录后重试";
        if (l.Contains("unavailable") || l.Contains("has been removed") || l.Contains("does not exist"))
            return "视频不可用或已被删除";
        if (l.Contains("region") || l.Contains("area") || l.Contains("geo"))
            return "该视频在当前地区不可用";
        if (l.Contains("412")) return "被 B 站风控(412)，请稍后重试或先登录";
        if (l.Contains("403")) return "被服务器拒绝(403)，Cookie 可能失效，请重新登录";
        if (l.Contains("404")) return "资源不存在(404)";
        if (l.Contains("timed out") || l.Contains("timeout")) return "网络超时，请检查网络或代理";
        if (l.Contains("requested format is not available")) return "该清晰度/编码不可用，请换一个";
        if (l.Contains("unable to download") || l.Contains("failed to"))
            return "下载失败：" + (s.Length > 120 ? s.Substring(0, 120) : s);
        return s.Length > 200 ? s.Substring(0, 200) : s;
    }
}

/// <summary>封面磁盘缓存：URL → 本地文件，二次打开秒出、省流量。</summary>
public static class CoverCache
{
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliGet", "covercache");

    private static readonly System.Net.Http.HttpClient Http = CreateHttp();
    private static System.Net.Http.HttpClient CreateHttp()
    {
        var h = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            h.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        }
        catch { }
        return h;
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> InFlight = new();

    // M3：内存封面 LRU（按 宽度|URL 去重，弱引用，超上限淘汰最旧）
    private static readonly object MemLock = new();
    private static readonly Dictionary<string, WeakReference<Microsoft.UI.Xaml.Media.Imaging.BitmapImage>> MemCache = new();
    private static readonly LinkedList<string> MemOrder = new();
    private const int MemCap = 200;

    private static string PathFor(string url)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(Dir, hash + ".img");
    }

    /// <summary>有本地缓存就读本地；否则先返回网络图，同时后台落盘。（带内存 LRU）</summary>
    public static Microsoft.UI.Xaml.Media.ImageSource? Get(string url, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var key = decodeWidth + "|" + url;
        lock (MemLock)
        {
            if (MemCache.TryGetValue(key, out var wr) && wr.TryGetTarget(out var cached))
            {
                MemOrder.Remove(key); MemOrder.AddLast(key);
                return cached;
            }
        }
        try
        {
            var path = PathFor(url);
            Microsoft.UI.Xaml.Media.Imaging.BitmapImage bi;
            if (File.Exists(path))
            {
                bi = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = decodeWidth };
                bi.UriSource = new Uri("file:///" + path.Replace('\\', '/'));
            }
            else
            {
                _ = DownloadAsync(url, path);
                bi = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = decodeWidth };
                bi.UriSource = new Uri(url);
            }
            lock (MemLock)
            {
                MemCache[key] = new WeakReference<Microsoft.UI.Xaml.Media.Imaging.BitmapImage>(bi);
                MemOrder.Remove(key); MemOrder.AddLast(key);
                while (MemOrder.Count > MemCap) { var old = MemOrder.First!.Value; MemOrder.RemoveFirst(); MemCache.Remove(old); }
            }
            return bi;
        }
        catch { return null; }
    }

    /// <summary>直接下载图片原始字节（用于「下载封面」）。</summary>
    public static async Task<byte[]?> FetchBytesAsync(string url)
    {
        try
        {
            using var r = await Http.GetAsync(url);
            r.EnsureSuccessStatusCode();
            return await r.Content.ReadAsByteArrayAsync();
        }
        catch { return null; }
    }

    private static async Task DownloadAsync(string url, string path)
    {
        if (!InFlight.TryAdd(url, true)) return;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            Directory.CreateDirectory(Dir);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, path, true);
            Trim();   // 新增即触发，超过 100 个文件淘汰最旧
        }
        catch { }
        finally { InFlight.TryRemove(url, out _); }
    }

    public static (long bytes, int files) Size()
    {
        long total = 0; int count = 0;
        try
        {
            if (!Directory.Exists(Dir)) return (0, 0);
            foreach (var f in new DirectoryInfo(Dir).EnumerateFiles()) { total += f.Length; count++; }
        }
        catch { }
        return (total, count);
    }

    public static long Clean()
    {
        long freed = 0;
        try
        {
            if (!Directory.Exists(Dir)) return 0;
            foreach (var f in new DirectoryInfo(Dir).EnumerateFiles())
            { try { var len = f.Length; f.Delete(); freed += len; } catch { } }
        }
        catch { }
        return freed;
    }

    /// <summary>智能清理：超过上限时按最后访问时间淘汰最旧的封面。</summary>
    public static void Trim(long maxBytes = 512L * 1024 * 1024, int maxFiles = 100)
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var files = new DirectoryInfo(Dir).EnumerateFiles()
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();   // 新→旧
            long total = 0; foreach (var f in files) total += f.Length;
            var count = files.Count;
            for (var i = files.Count - 1; i >= 0 && (total > maxBytes || count > maxFiles); i--)
            {
                try { total -= files[i].Length; count--; files[i].Delete(); } catch { }
            }
        }
        catch { }
    }
}
