using System.Numerics;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;

namespace BiliGet;

public sealed partial class MainWindow : Window
{
    private static readonly string CfgDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliGet");
    private static readonly string CfgPath = Path.Combine(CfgDir, "settings.json");
    private Dictionary<string, string> _cfg = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<DownloadItem> _downloads = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<BatchItem> _batch = new();
    private DispatcherQueueTimer? _loginTimer;
    private bool _loadingHistory;
    private bool _historyLoaded;
    private DateTime _lastHistoryLoad = DateTime.MinValue;
    private WebView2? _loginView;
    private DispatcherQueueTimer? _qrTimer;
    private string? _qrKey;
    private DispatcherQueueTimer? _toastTimer;
    private DispatcherQueueTimer? _cacheTimer;
    private readonly DispatcherQueueTimer _prefetchTimer;
    private int _activeDownloads;
    private readonly List<DownloadItem> _queue = new();
    private int _running;
    private string _resCoverUrl = "";
    private string _seriesCoverUrl = "";
    private double _resDuration;
    private long _resAudioBytes;

    // 解析缓存：BV/av → 基础信息 + 分P(含已解析清晰度) + 单视频清晰度，短期复用
    private sealed class BiliCache
    {
        public BiliLogin.BiliMeta Meta = new();
        public readonly List<PartItem> Parts = new();
        public List<FormatItem>? SingleFormats;
        public bool Pgc;
        public DateTime At;
    }
    private readonly Dictionary<string, BiliCache> _biliCache = new();
    private int _parseSeq;
    private static readonly long BigSizeThreshold = 2L * 1024 * 1024 * 1024; // 约 2GB
    private readonly List<PartItem> _parts = new();
    private readonly List<HistoryItem> _historyAll = new();
    private string _historyFilter = "all";
    private bool _histHasMore;
    private bool _isPlaylist;
    private bool _isSeries;
    private bool _isPgcSeason;
    private bool _suppressParts;
    private DownloadItem? _ctxItem;
    private Action? _toastAction;
    private string _curNav = "search";
    private Windows.UI.Color _accentColor = Windows.UI.Color.FromArgb(255, 0x00, 0xAE, 0xEC);
    private SolidColorBrush _accentBrush = new(Windows.UI.Color.FromArgb(255, 0x00, 0xAE, 0xEC));
    private static readonly string DlStatePath = Path.Combine(CfgDir, "downloads.json");

    public MainWindow()
    {
        InitializeComponent();
        Title = "OTAKUsVget";

        LoadSettings();
        ApplyBackdrop();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ResizeAndCenter();
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "OTAKUsVget.ico");
            AppWindow.SetIcon(File.Exists(ico) ? ico : Environment.ProcessPath!);
        }
        catch { }
        ApplyTitleBar();
        AppTitleBar.ActualThemeChanged += (_, _) => { ApplyTitleBar(); ApplyBackdrop(); RefreshSearchBorder(); };

        BatchList.ItemsSource = _batch;
        LoadDownloads();
        RefreshDownloads();
        Root.AllowDrop = true;
        Root.DragOver += (_, e) => { if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy; };
        Root.Drop += Root_Drop;
        UrlBox.Paste += UrlBox_Paste;
        ShowSearch();
        SetActiveNav("search");
        RailRoot.SizeChanged += (_, _) => RepositionNavIndicator();   // 窗口缩放/最大化后重新定位指示条
        _ = RefreshCacheInfoAsync();
        _ = Task.Run(() => CoverCache.Trim());   // 封面缓存智能清理（超限淘汰最旧）
        // 下载过程中每秒刷新缓存/临时文件占用，做到实时更新
        _cacheTimer = DispatcherQueue.CreateTimer();
        _cacheTimer.Interval = TimeSpan.FromSeconds(1);
        _cacheTimer.IsRepeating = true;
        _cacheTimer.Tick += (_, _) => { if (_activeDownloads > 0) _ = RefreshCacheInfoAsync(); };
        _cacheTimer.Start();

        // 输入即预取：输入框停顿 300ms 后，若是合法 BV/av 就提前解析进缓存
        _prefetchTimer = DispatcherQueue.CreateTimer();
        _prefetchTimer.Interval = TimeSpan.FromMilliseconds(300);
        _prefetchTimer.IsRepeating = false;
        _prefetchTimer.Tick += (_, _) => PrefetchFromInput();
        _ = InitAccountAsync();
        _ = Task.Run(() => YtDlp.EnsureAsync());   // 预热：把内置工具复制到运行时目录（避免某些安装路径下无法运行）
        _ = Task.Run(async () => { try { await BiliLogin.WarmAsync(Cfg("cookie"), Cfg("proxy")); } catch { } });   // 预热 wbi 签名，首解析省一次 nav
        UrlBox.TextChanged += (_, _) => { _prefetchTimer.Stop(); _prefetchTimer.Start(); };   // 输入即预取
        _ = CheckPostUpdateAsync();
        _ = AutoCheckUpdateAsync();
        Closed += (_, _) => SaveBounds();
        Activated += (_, _) => RefreshDownloadStatuses();
        Root.Loaded += (_, _) =>
        {
            HookComboCursors();
            // 搜索栏专属描边（用于颜色过渡）
            try { SearchBar.BorderBrush = _searchBorder; RefreshSearchBorder(); } catch { }
        };

        try { Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(CfgDir, "WebView2")); } catch { }
        var ck = Cfg("cookie");
        if (!string.IsNullOrEmpty(ck) && File.Exists(ck))
        {
            try
            {
                var d = ReadCookieFile(ck);
                if (d.ContainsKey("SESSDATA")) LoginState.Text = "已登录";
            }
            catch { }
        }
    }

    private static Dictionary<string, string> ReadCookieFile(string path)
    {
        var d = new Dictionary<string, string>();
        try
        {
            foreach (var ln in File.ReadAllLines(path))
            {
                if (ln.Length == 0 || ln[0] == '#') continue;
                var p = ln.Split('\t');
                if (p.Length >= 7) d[p[5]] = p[6];
            }
        }
        catch { }
        return d;
    }

    private string Cfg(string key, string def = "") => _cfg.TryGetValue(key, out var v) ? v : def;

    private void ApplyBackdrop()
    {
        // Mica 跟随“应用/系统”主题，不随元素 RequestedTheme 变化 → 切主题时重建即可生效
        try
        {
            SystemBackdrop = null;
            var kind = Cfg("backdrop", "micaalt");
            switch (kind)
            {
                case "mica":
                    SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                    break;
                case "acrylic":
                    SystemBackdrop = new DesktopAcrylicBackdrop();
                    break;
                case "solid":
                    SystemBackdrop = null;
                    break;
                default:
                    SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                    break;
            }
            Root.Background = kind == "solid"
                ? new SolidColorBrush(Root.ActualTheme == ElementTheme.Light
                    ? Windows.UI.Color.FromArgb(255, 0xF4, 0xF4, 0xF6)
                    : Windows.UI.Color.FromArgb(255, 0x1E, 0x1F, 0x22))
                : new SolidColorBrush(Colors.Transparent);
        }
        catch { }
    }

    private void ApplyAccent()
    {
        try
        {
            var idx = SetAccent.SelectedIndex;
            Windows.UI.Color c;
            if (idx == 0)
            {
                c = new Windows.UI.ViewManagement.UISettings()
                    .GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            }
            else
            {
                c = idx switch
                {
                    1 => Windows.UI.Color.FromArgb(255, 0x00, 0xAE, 0xEC),
                    2 => Windows.UI.Color.FromArgb(255, 0x8B, 0x5C, 0xF6),
                    3 => Windows.UI.Color.FromArgb(255, 0x10, 0xB9, 0x81),
                    4 => Windows.UI.Color.FromArgb(255, 0xF5, 0x9E, 0x0B),
                    5 => Windows.UI.Color.FromArgb(255, 0xEC, 0x48, 0x99),
                    _ => Windows.UI.Color.FromArgb(255, 0x00, 0xAE, 0xEC),
                };
            }
            _accentColor = c;
            _accentBrush = new SolidColorBrush(c);
            // 直接改主题字典里的 Accent 画刷颜色 → 所有 {ThemeResource Accent} 即时生效
            var td = Application.Current.Resources.ThemeDictionaries;
            foreach (var k in new object[] { "Dark", "Light" })
            {
                if (td.TryGetValue(k, out var o) && o is ResourceDictionary d
                    && d.TryGetValue("Accent", out var b) && b is SolidColorBrush sb)
                    sb.Color = c;
            }
            // 强调色渐变同步
            if (Application.Current.Resources["AccentGrad"] is LinearGradientBrush grad && grad.GradientStops.Count >= 2)
            {
                grad.GradientStops[0].Color = Lighten(c, 0.05);
                grad.GradientStops[1].Color = Lighten(c, 0.45);
            }
            SetActiveNav(_curNav);
            RefreshSearchBorder();
        }
        catch { }
    }

    private static void AnimateColor(DependencyObject target, Windows.UI.Color to)
    {
        try
        {
            var a = new ColorAnimation { To = to, Duration = TimeSpan.FromMilliseconds(200), EnableDependentAnimation = true };
            Storyboard.SetTarget(a, target); Storyboard.SetTargetProperty(a, "Color");
            var sb = new Storyboard(); sb.Children.Add(a); sb.Begin();
        }
        catch
        {
            if (target is SolidColorBrush b) b.Color = to;
            else if (target is GradientStop g) g.Color = to;
        }
    }

    private static Windows.UI.Color Lighten(Windows.UI.Color c, double amt)
    {
        byte L(byte v) => (byte)Math.Clamp((int)(v + (255 - v) * amt), 0, 255);
        return Windows.UI.Color.FromArgb(c.A, L(c.R), L(c.G), L(c.B));
    }

    private void ApplyTitleBar()
    {
        try
        {
            var bar = AppWindow.TitleBar;
            var dark = Root.ActualTheme != ElementTheme.Light;
            var fg = dark ? Windows.UI.Color.FromArgb(255, 240, 240, 240) : Windows.UI.Color.FromArgb(255, 20, 20, 20);
            var bg = dark ? Windows.UI.Color.FromArgb(255, 30, 31, 34) : Windows.UI.Color.FromArgb(255, 244, 244, 246);
            var transparent = ExtendsContentIntoTitleBar;
            bar.ButtonBackgroundColor = transparent ? Colors.Transparent : bg;
            bar.ButtonInactiveBackgroundColor = transparent ? Colors.Transparent : bg;
            bar.ButtonForegroundColor = fg;
            bar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x1E, 255, 255, 255);
            bar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x14, 255, 255, 255);
            if (!transparent)
            {
                bar.BackgroundColor = bg;
                bar.ForegroundColor = fg;
                bar.InactiveBackgroundColor = bg;
                bar.InactiveForegroundColor = fg;
            }
            bar.PreferredHeightOption = TitleBarHeightOption.Standard;
        }
        catch { }
    }

    private void ResizeAndCenter()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var sw = int.TryParse(Cfg("winw", ""), out var a) ? a : 0;
            var sh = int.TryParse(Cfg("winh", ""), out var b) ? b : 0;
            var sx = int.TryParse(Cfg("winx", ""), out var cc) ? cc : 0;
            var sy = int.TryParse(Cfg("winy", ""), out var dd) ? dd : 0;
            if (sw > 400 && sh > 300 && sx < area.X + area.Width - 100 && sy < area.Y + area.Height - 100)
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(sw, sh));
                AppWindow.Move(new Windows.Graphics.PointInt32(sx, sy));
                return;
            }
            var w = Math.Min(1180, area.Width - 80);
            var h = Math.Min(780, area.Height - 80);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
            AppWindow.Move(new Windows.Graphics.PointInt32(
                area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2));
        }
        catch { }
    }

    private void SaveBounds()
    {
        try
        {
            var s = AppWindow.Size; var p = AppWindow.Position;
            _cfg["winw"] = s.Width.ToString(); _cfg["winh"] = s.Height.ToString();
            _cfg["winx"] = p.X.ToString(); _cfg["winy"] = p.Y.ToString();
            SaveCfg();
        }
        catch { }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;
            var t = (await e.DataView.GetTextAsync()).Trim();
            var first = t.Split('\n')[0].Trim();
            if (first.Length == 0) return;
            UrlBox.Text = first;
            Parse_Click(this, new RoutedEventArgs());
        }
        catch { }
    }

    private void UrlBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var s = (UrlBox.Text ?? "").Trim();
            if (Regex.IsMatch(s, @"^BV[0-9A-Za-z]{10}$") || s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                Parse_Click(this, new RoutedEventArgs());
        });
    }

    /* ---------------- 设置（修改即自动保存） ---------------- */
    private bool _loadingSettings;

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(CfgPath))
                _cfg = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CfgPath)) ?? new();
        }
        catch { _cfg = new(); }

        _loadingSettings = true;
        SetDir.Text = Cfg("dir", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BiliGet"));
        SetCookie.Text = Cfg("cookie");
        SetProxy.Text = Cfg("proxy");
        SetQuality.SelectedIndex = int.TryParse(Cfg("quality", "0"), out var q) ? Math.Clamp(q, 0, 2) : 0;
        SetConcurrent.SelectedIndex = (int.TryParse(Cfg("concurrent", "2"), out var cc) ? Math.Clamp(cc, 1, 8) : 2) - 1;
        SetAutoCheck.IsChecked = Cfg("autocheck", "0") == "1";
        SetSubs.IsChecked = Cfg("subs", "0") == "1";
        SetDelFile.IsChecked = Cfg("delfile", "0") == "1";
        var theme = Cfg("theme", "dark");
        SetTheme.SelectedIndex = theme == "light" ? 1 : theme == "system" ? 2 : 0;
        SetBackdrop.SelectedIndex = Cfg("backdrop", "micaalt") switch { "mica" => 1, "acrylic" => 2, "solid" => 3, _ => 0 };
        SetAccent.SelectedIndex = int.TryParse(Cfg("accent", "0"), out var ac) ? Math.Clamp(ac, 0, 5) : 0;
        _loadingSettings = false;
        // 应用外观（主题 / 背景 / 强调色）
        Host.RequestedTheme = theme == "light" ? ElementTheme.Light
            : theme == "system" ? ElementTheme.Default : ElementTheme.Dark;
        ApplyAccent();
    }

    private void SaveCfg()
    {
        try { Directory.CreateDirectory(CfgDir); File.WriteAllText(CfgPath, JsonSerializer.Serialize(_cfg)); } catch { }
    }

    private void AutoSave_TextChanged(object sender, TextChangedEventArgs e) => AutoSave();
    private void AutoSave_SelectionChanged(object sender, SelectionChangedEventArgs e) => AutoSave();
    private void AutoSave_Check(object sender, RoutedEventArgs e) => AutoSave();

    private void AutoSave()
    {
        if (_loadingSettings) return;
        _cfg["dir"] = SetDir.Text.Trim();
        _cfg["cookie"] = SetCookie.Text.Trim();
        _cfg["proxy"] = SetProxy.Text.Trim();
        _cfg["quality"] = SetQuality.SelectedIndex.ToString();
        _cfg["concurrent"] = (SetConcurrent.SelectedIndex + 1).ToString();
        _cfg["autocheck"] = (SetAutoCheck.IsChecked == true) ? "1" : "0";
        _cfg["subs"] = (SetSubs.IsChecked == true) ? "1" : "0";
        _cfg["delfile"] = (SetDelFile.IsChecked == true) ? "1" : "0";
        _cfg["theme"] = SetTheme.SelectedIndex switch { 1 => "light", 2 => "system", _ => "dark" };
        _cfg["backdrop"] = SetBackdrop.SelectedIndex switch { 1 => "mica", 2 => "acrylic", 3 => "solid", _ => "micaalt" };
        _cfg["accent"] = SetAccent.SelectedIndex.ToString();
        SaveCfg();
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _cfg["theme"] = SetTheme.SelectedIndex switch { 1 => "light", 2 => "system", _ => "dark" };
        SaveCfg();
        var theme = SetTheme.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Default,
            _ => ElementTheme.Dark,
        };
        FadeSwapTheme(theme);
    }

    // 主题切换过渡：截图当前画面盖住 → 换主题 → 旧画面淡出（元素不消失，平滑变色）
    private bool _theming;
    private async void FadeSwapTheme(ElementTheme theme)
    {
        if (_theming)
        {
            Host.RequestedTheme = theme; ApplyTitleBar(); ApplyBackdrop();
            return;
        }
        _theming = true;
        try
        {
            // 先铺旧主题背景色（截图不含 Mica），与截图一起淡出，避免背景不同步
            ThemeSnapshotBg.Background = new SolidColorBrush(ThemeColor("AppBg", Windows.UI.Color.FromArgb(255, 0x1E, 0x1F, 0x22)));
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(Root);
            ThemeSnapshot.Source = rtb;
            ThemeSnapshotLayer.Opacity = 1;
            ThemeSnapshotLayer.Visibility = Visibility.Visible;
            await Task.Delay(45);   // 等遮罩真正画到屏幕上，再换主题，避免闪一帧
        }
        catch { }
        try
        {
            Host.RequestedTheme = theme;
            ApplyTitleBar();
            ApplyBackdrop();
        }
        catch { }
        try
        {
            var a = new DoubleAnimation
            {
                From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(a, ThemeSnapshotLayer); Storyboard.SetTargetProperty(a, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(a);
            sb.Completed += (_, _) =>
            {
                try { ThemeSnapshotLayer.Visibility = Visibility.Collapsed; ThemeSnapshot.Source = null; } catch { }
            };
            sb.Begin();
        }
        catch { try { ThemeSnapshotLayer.Visibility = Visibility.Collapsed; } catch { } }
        _theming = false;
    }

    private void Backdrop_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _cfg["backdrop"] = SetBackdrop.SelectedIndex switch { 1 => "mica", 2 => "acrylic", 3 => "solid", _ => "micaalt" };
        SaveCfg();
        ApplyBackdrop();
    }

    private void Accent_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _cfg["accent"] = SetAccent.SelectedIndex.ToString();
        SaveCfg();
        ApplyAccent();
    }

    private async void TestProxy_Click(object sender, RoutedEventArgs e)
    {
        var proxy = SetProxy.Text.Trim();
        ShowToast(proxy.Length > 0 ? "正在测试代理…" : "正在测试直连…", "info");
        try
        {
            var handler = new System.Net.Http.HttpClientHandler();
            if (proxy.Length > 0) handler.Proxy = new System.Net.WebProxy(proxy);
            using var h = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            await h.GetStringAsync("http://101.200.208.212/download/vget-latest.json");
            ShowToast(proxy.Length > 0 ? "代理可用 ✓" : "直连可用 ✓", "ok");
        }
        catch (Exception ex)
        {
            ShowToast("连接失败：" + ex.GetBaseException().Message, "err");
        }
    }

    private async Task Dialog(string msg)
    {
        var dlg = new ContentDialog
        {
            Title = "OTAKUsVget",
            Content = new TextBlock { Text = msg, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "确定",
            XamlRoot = Root.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    /* ---------------- 页面 ---------------- */
    private void ShowOnly(UIElement page)
    {
        foreach (var p in new UIElement[] { EmptyState, ResultPage, HistoryPage, DownloadsPage, LoginPage, SettingsPage, AboutPage })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        bool hideSearch = page == SettingsPage || page == AboutPage || page == HistoryPage || page == DownloadsPage;
        SearchBar.Visibility = hideSearch ? Visibility.Collapsed : Visibility.Visible;
        AnimatePageIn(page);
    }

    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Phase != 0) return;
        if (args.ItemContainer is not FrameworkElement el) return;
        try
        {
            el.Opacity = 0;
            var tf = new TranslateTransform { Y = 8 };
            el.RenderTransform = tf;
            var delay = TimeSpan.FromMilliseconds(Math.Min(args.ItemIndex, 10) * 28);
            var sb = new Storyboard();
            var op = new DoubleAnimation { From = 0, To = 1, BeginTime = delay, Duration = TimeSpan.FromMilliseconds(160) };
            Storyboard.SetTarget(op, el); Storyboard.SetTargetProperty(op, "Opacity");
            var ty = new DoubleAnimation
            {
                From = 8, To = 0, BeginTime = delay, Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(ty, tf); Storyboard.SetTargetProperty(ty, "Y");
            sb.Children.Add(op); sb.Children.Add(ty);
            sb.Begin();
        }
        catch { try { el.Opacity = 1; } catch { } }
    }

    private static void AnimatePageIn(UIElement page)
    {
        try
        {
            var tf = new TranslateTransform { Y = 10 };
            page.RenderTransform = tf;
            page.Opacity = 0;
            var sb = new Storyboard();
            var op = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(170) };
            Storyboard.SetTarget(op, page); Storyboard.SetTargetProperty(op, "Opacity");
            var ty = new DoubleAnimation
            {
                From = 10, To = 0, Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(ty, tf); Storyboard.SetTargetProperty(ty, "Y");
            sb.Children.Add(op); sb.Children.Add(ty);
            sb.Begin();
        }
        catch { try { page.Opacity = 1; } catch { } }
    }

    private void ShowSearch() => ShowOnly(EmptyState);

    /* ---------------- 搜索栏美化（S1 聚焦 / S4 悬停 / S5 阴影 / S6 渐变描边） ---------------- */
    private bool _searchFocused, _searchHover;
    private readonly SolidColorBrush _searchBorder = new(Windows.UI.Color.FromArgb(255, 0x34, 0x36, 0x3B));

    private void UrlBox_GotFocus(object sender, RoutedEventArgs e) { _searchFocused = true; RefreshSearchBorder(); }
    private void UrlBox_LostFocus(object sender, RoutedEventArgs e) { _searchFocused = false; RefreshSearchBorder(); }
    private void SearchBar_PointerEntered(object sender, PointerRoutedEventArgs e) { _searchHover = true; RefreshSearchBorder(); }
    private void SearchBar_PointerExited(object sender, PointerRoutedEventArgs e) { _searchHover = false; RefreshSearchBorder(); }

    private static Windows.UI.Color ThemeColor(string key, Windows.UI.Color fallback)
    {
        try
        {
            if (Application.Current.Resources[key] is SolidColorBrush b) return b.Color;
        }
        catch { }
        return fallback;
    }

    private void RefreshSearchBorder()
    {
        try
        {
            // 只改描边颜色；不改 BorderThickness / Translation，避免内容抖动
            var to = _searchFocused
                ? _accentColor
                : _searchHover
                    ? ThemeColor("FieldBorder", Windows.UI.Color.FromArgb(255, 0x45, 0x48, 0x4F))
                    : ThemeColor("CardBorder", Windows.UI.Color.FromArgb(255, 0x34, 0x36, 0x3B));
            AnimateColor(_searchBorder, to);
        }
        catch { }
    }

    private bool _aboutAnimated;

    private void AnimateAbout()
    {
        try
        {
            AboutLogo.Opacity = 1;
            AboutLogoTr.ScaleX = 1; AboutLogoTr.ScaleY = 1;
            var sb = new Storyboard();
            var o = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(220)) };
            Storyboard.SetTarget(o, AboutLogo); Storyboard.SetTargetProperty(o, "Opacity");
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };
            var sx = new DoubleAnimation { From = 0.6, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(380)), EasingFunction = ease };
            Storyboard.SetTarget(sx, AboutLogoTr); Storyboard.SetTargetProperty(sx, "ScaleX");
            var sy = new DoubleAnimation { From = 0.6, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(380)), EasingFunction = ease };
            Storyboard.SetTarget(sy, AboutLogoTr); Storyboard.SetTargetProperty(sy, "ScaleY");
            sb.Children.Add(o); sb.Children.Add(sx); sb.Children.Add(sy);
            sb.Begin();
        }
        catch { }
    }

    private void OpenSite_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://otakusdremingworld.xyz/") { UseShellExecute = true }); } catch { }
    }

    private async void Nav_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string ?? "search";
        // 离开登录/验证码页：释放 WebView2；离开历史页：释放封面位图
        try { if (LoginPage.Visibility == Visibility.Visible) { DisposeLoginView(); DisposeCaptchaView(); _qrTimer?.Stop(); } } catch { }
        if (_curNav == "history" && tag != "history") ReleaseHistoryCovers();
        SetActiveNav(tag);
        switch (tag)
        {
            case "search": ShowSearch(); break;
            case "history":
                ShowOnly(HistoryPage);
                {
                    var ck = Cfg("cookie");
                    if (!string.IsNullOrEmpty(ck) && File.Exists(ck) && !_loadingHistory
                        && (!_historyLoaded || (DateTime.UtcNow - _lastHistoryLoad).TotalSeconds > 15))
                    {
                        LoginState.Text = "正在载入观看历史…";
                        await LoadHistoryAsync(ReadCookieFile(ck));
                    }
                    else UpdateHistoryEmpty();
                }
                break;
            case "settings": ShowOnly(SettingsPage); _ = RefreshCacheInfoAsync(); break;
            case "about":
                ShowOnly(AboutPage);
                if (!_aboutAnimated) { _aboutAnimated = true; AnimateAbout(); }
                break;
            case "download": ShowOnly(DownloadsPage); RefreshDownloadStatuses(); break;
        }
    }

    private void SetActiveNav(string tag)
    {
        _curNav = tag;
        var accent = _accentBrush;
        var selBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, _accentColor.R, _accentColor.G, _accentColor.B));
        var dim = new SolidColorBrush(Colors.Transparent);
        var pairs = new (Button btn, string t)[]
        {
            (TabSearch, "search"), (TabHistory, "history"), (TabDownload, "download"),
            (TabSettings, "settings"), (TabAbout, "about")
        };
        foreach (var (btn, t) in pairs)
        {
            if (t == tag) { btn.Background = selBg; btn.Foreground = accent; }
            else { btn.Background = dim; btn.Foreground = (Brush)Application.Current.Resources["TextDim"]; }
        }
        if (string.IsNullOrEmpty(tag)) { NavIndicator.Visibility = Visibility.Collapsed; return; }
        MoveNavIndicator(pairs.FirstOrDefault(p => p.t == tag).btn);
    }

    // 窗口尺寸变化后按当前选中项直接重排指示条（不带动画，保证对齐）
    private void RepositionNavIndicator()
    {
        if (string.IsNullOrEmpty(_curNav)) return;
        var pairs = new (Button btn, string t)[]
        {
            (TabSearch, "search"), (TabHistory, "history"), (TabDownload, "download"),
            (TabSettings, "settings"), (TabAbout, "about")
        };
        var btn = pairs.FirstOrDefault(p => p.t == _curNav).btn;
        if (btn == null) return;
        try
        {
            if (btn.ActualHeight <= 0) return;
            var y = btn.TransformToVisual(RailRoot).TransformPoint(new Windows.Foundation.Point(0, 0)).Y
                    + btn.ActualHeight / 2 - 10;
            NavIndicatorTf.Y = y;
            NavIndicator.Visibility = Visibility.Visible;
        }
        catch { }
    }

    private void MoveNavIndicator(Button? btn)
    {
        if (btn == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (btn.ActualHeight <= 0) return;
                var y = btn.TransformToVisual(RailRoot).TransformPoint(new Windows.Foundation.Point(0, 0)).Y
                        + btn.ActualHeight / 2 - 10;
                NavIndicator.Visibility = Visibility.Visible;
                var anim = new DoubleAnimation
                {
                    To = y, Duration = TimeSpan.FromMilliseconds(220),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    EnableDependentAnimation = true,
                };
                Storyboard.SetTarget(anim, NavIndicatorTf);
                Storyboard.SetTargetProperty(anim, "Y");
                var sb = new Storyboard();
                sb.Children.Add(anim);
                sb.Begin();
            }
            catch { }
        });
    }

    private void DownloadList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DownloadItem it) return;
        if (it.FilePath.Length > 0 && File.Exists(it.FilePath))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(it.FilePath) { UseShellExecute = true }); } catch { }
        }
        else if (it.Done)
        {
            ShowToast("文件已不在，右键可「重新下载」", "info");
        }
    }

    /* ---------------- 主题：即时切换（稳定优先，避免元素消失/发虚） ---------------- */
    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        // 按“当前实际外观”亮暗互切（若原来是跟随系统，会变成显式指定）
        SetTheme.SelectedIndex = Root.ActualTheme != ElementTheme.Light ? 1 : 0;
    }

    /* ---------------- 解析 ---------------- */
    private static string BuildUrl(string input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return "";
        if (Regex.IsMatch(s, @"^BV[0-9A-Za-z]{10}$")) return "https://www.bilibili.com/video/" + s;
        if (Regex.IsMatch(s, @"^av\d+$", RegexOptions.IgnoreCase)) return "https://www.bilibili.com/video/" + s;
        if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return s;
        return "https://www.bilibili.com/video/" + s;
    }

    private void UrlBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) Parse_Click(sender, new RoutedEventArgs());
    }

    private async Task<bool> ParseBiliFastAsync(string url, string bv, string aid)
    {
        var key = bv.Length > 0 ? bv : ("av" + aid);
        _biliCache.TryGetValue(key, out var c);
        if (c != null && (DateTime.UtcNow - c.At) > TimeSpan.FromMinutes(10)) c = null;

        if (c == null)
        {
            BiliLogin.BiliMeta m;
            try { m = await BiliLogin.GetVideoMetaAsync(bv, aid, Cfg("cookie"), Cfg("proxy")); }
            catch { return false; }
            if (m == null || m.Title.Length == 0) return false;   // 风控/失败 → 回退 yt-dlp
            c = new BiliCache { Meta = m, At = DateTime.UtcNow };
            foreach (var p in m.Parts)
                c.Parts.Add(new PartItem
                {
                    Title = p.title,
                    Url = p.url,
                    Cid = p.cid,
                    CoverUrl = p.cover,
                    Formats = new List<FormatItem> { new() { Label = "最佳画质", Height = 0 } },
                });
            _biliCache[key] = c;
        }

        ShowBiliCard(url, c);
        return true;
    }

    private void ShowBiliCard(string url, BiliCache c)
    {
        var m = c.Meta;
        _parts.Clear(); _isPlaylist = false; _isSeries = false; _isPgcSeason = false;
        if (c.Parts.Count > 1 || c.Pgc)
        {
            foreach (var p in c.Parts) _parts.Add(p);
            _isPlaylist = true;
            _isPgcSeason = c.Pgc;
            _isSeries = c.Pgc || m.IsSeries;
        }
        try { PartsLabel.Text = _isPgcSeason ? "选集" : _isSeries ? "合集" : "分P"; } catch { }

        ResTitle.Text = m.Title.Length > 0 ? m.Title : url;
        _resDuration = m.Duration;
        _resAudioBytes = 0;
        var dur = m.Duration > 0 ? TimeSpan.FromSeconds(m.Duration).ToString(@"hh\:mm\:ss") : "";
        ResMeta.Text = _isPlaylist
            ? $"共 {_parts.Count} {(_isPgcSeason ? "集" : _isSeries ? "个视频" : "P")}" + (m.Uploader.Length > 0 ? "    " + m.Uploader : "")
            : (m.Uploader.Length > 0 ? m.Uploader + "    " : "") + dur;
        if (m.Thumb.Length > 0) { _resCoverUrl = m.Thumb; _seriesCoverUrl = m.Thumb; ResCover.Source = CoverCache.Get(m.Thumb, 420); }
        else _seriesCoverUrl = "";

        if (_isPlaylist)
        {
            var unit = _isPgcSeason ? "集" : _isSeries ? "个" : "P";
            var pis = new List<object> { $"全部（{_parts.Count} {unit}）" };
            foreach (var p in _parts) pis.Add(p);
            _suppressParts = true;
            PartsBox.ItemsSource = pis;
            PartsBox.SelectedIndex = 1;
            _suppressParts = false;
            PartsRow.Visibility = Visibility.Visible;
            FormatBox.ItemsSource = _parts[0].Formats;
            FormatBox.SelectedIndex = 0;
            try { AddBatchBtn.IsEnabled = _parts[0].Formats.Count > 1; } catch { }
        }
        else
        {
            PartsRow.Visibility = Visibility.Collapsed;
            PartsBox.ItemsSource = null;
            FormatBox.ItemsSource = new List<FormatItem> { new() { Label = "解析清晰度…", Height = 0 } };
            FormatBox.SelectedIndex = 0;
            try { AddBatchBtn.IsEnabled = true; } catch { }
        }

        HideSkeleton();   // —— 结果卡片立即出现 ——

        if (_isPlaylist)
        {
            if (_parts[0].Formats.Count <= 1) _ = LoadSelectedPartFormatsAsync();
        }
        else
        {
            _ = LoadSingleFormatsAsync(url, c);
        }
    }

    private async Task LoadSingleFormatsAsync(string url, BiliCache c)
    {
        if (c.SingleFormats != null) { ApplySingleFormats(c.SingleFormats); return; }
        var seq = _parseSeq;
        // 先试 wbi playurl（秒出清晰度），失败再回退 yt-dlp
        try
        {
            if (c.Meta.Cid > 0 && c.Meta.Bvid.Length > 0)
            {
                var tracks = await BiliLogin.GetPlayTracksAsync(c.Meta.Bvid, c.Meta.Cid, Cfg("cookie"), Cfg("proxy"));
                if (tracks.Count > 0 && seq == _parseSeq)
                {
                    var fits = BuildFormatItemsFromTracks(tracks, c.Meta.Duration);
                    c.SingleFormats = fits;
                    ApplySingleFormats(fits);
                    return;
                }
            }
        }
        catch { }
        try
        {
            var (doc, _) = await YtDlp.ParseAsync(url, Cfg("cookie"), Cfg("proxy"), false);
            if (doc == null) return;
            List<FormatItem> items; double dur; long audio;
            try
            {
                dur = doc.RootElement.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : c.Meta.Duration;
                audio = EstimateAudioBytes(doc.RootElement, dur);
                items = BuildFormatItems(doc.RootElement, dur);
            }
            finally { doc.Dispose(); }
            c.SingleFormats = items;          // 缓存，重复解析不再调 yt-dlp
            if (seq != _parseSeq) return;     // 已切到别的视频
            _resDuration = dur; _resAudioBytes = audio;
            ApplySingleFormats(items);
        }
        catch { }
    }

    private void ApplySingleFormats(List<FormatItem>? items)
    {
        RunOnUi(() =>
        {
            if (items == null || items.Count == 0) return;
            FormatBox.ItemsSource = items;
            FormatBox.SelectedIndex = 0;
            try { AddBatchBtn.IsEnabled = true; } catch { }
        });
    }

    // 下载封面（JPG / PNG 两选）
    private async void SaveCoverJpg_Click(object sender, RoutedEventArgs e) => await SaveCoverAsync(false);
    private async void SaveCoverPng_Click(object sender, RoutedEventArgs e) => await SaveCoverAsync(true);

    private async Task SaveCoverAsync(bool png)
    {
        if (string.IsNullOrEmpty(_resCoverUrl)) { ShowToast("没有封面可下载", "info"); return; }
        try
        {
            var bytes = await CoverCache.FetchBytesAsync(_resCoverUrl);
            if (bytes == null || bytes.Length == 0) { ShowToast("封面下载失败", "err"); return; }
            var outBytes = await EncodeImageAsync(bytes, png) ?? bytes;
            var ext = png ? ".png" : ".jpg";
            var dir = Cfg("dir", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BiliGet"));
            Directory.CreateDirectory(dir);
            var name = SanitizeTag(ResTitle.Text);
            if (name.Length == 0) name = "cover";
            var path = Path.Combine(dir, name + ext);
            var i = 1;
            while (File.Exists(path)) path = Path.Combine(dir, name + " (" + (i++) + ")" + ext);
            await File.WriteAllBytesAsync(path, outBytes);
            ShowToast("封面已保存：" + Path.GetFileName(path), "ok");
        }
        catch (Exception ex) { ShowToast("保存封面失败：" + ex.Message, "err"); }
    }

    private static async Task<byte[]?> EncodeImageAsync(byte[] src, bool png)
    {
        try
        {
            using var inStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var dw = new Windows.Storage.Streams.DataWriter(inStream);
            dw.WriteBytes(src);
            await dw.StoreAsync();
            await dw.FlushAsync();
            dw.DetachStream();
            inStream.Seek(0);
            var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(inStream);
            var bmp = await dec.GetSoftwareBitmapAsync();
            using var outStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                png ? Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId
                    : Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, outStream);
            enc.SetSoftwareBitmap(bmp);
            await enc.FlushAsync();
            outStream.Seek(0);
            var buf = new byte[(int)outStream.Size];
            var dr = new Windows.Storage.Streams.DataReader(outStream.GetInputStreamAt(0));
            await dr.LoadAsync((uint)outStream.Size);
            dr.ReadBytes(buf);
            return buf;
        }
        catch { return null; }
    }

    // 番剧 ep：season 出卡片 + playurl 出清晰度（免 yt-dlp）
    private async Task<bool> ParsePgcFastAsync(string url, long epId)
    {
        var key = "ep" + epId;
        _biliCache.TryGetValue(key, out var c);
        if (c != null && (DateTime.UtcNow - c.At) > TimeSpan.FromMinutes(10)) c = null;

        if (c == null)
        {
            BiliLogin.PgcMeta pgc;
            try { pgc = await BiliLogin.GetSeasonMetaAsync(epId, Cfg("cookie"), Cfg("proxy")); }
            catch { return false; }
            if (pgc == null || pgc.Eps.Count == 0) return false;
            c = new BiliCache { Pgc = true, At = DateTime.UtcNow };
            c.Meta.Title = pgc.Title; c.Meta.Thumb = pgc.Cover; c.Meta.Uploader = pgc.Uploader; c.Meta.IsSeries = true;
            var n = 0;
            foreach (var ep in pgc.Eps)
            {
                n++;
                c.Parts.Add(new PartItem
                {
                    Title = ep.title.Length > 0 ? $"第{n}话 · {ep.title}" : $"第{n}话",
                    Url = "https://www.bilibili.com/bangumi/play/ep" + ep.epid,
                    Cid = ep.cid,
                    EpId = ep.epid,
                    CoverUrl = ep.cover,
                    Formats = new List<FormatItem> { new() { Label = "最佳画质", Height = 0 } },
                });
            }
            _biliCache[key] = c;
        }

        ShowBiliCard(url, c);
        return true;
    }

    // 输入即预取：把 view(+playurl) 结果提前塞进缓存，回车近乎瞬时
    private void PrefetchFromInput()
    {
        var url = BuildUrl(UrlBox.Text ?? "");
        if (url.Length == 0) return;
        if (url.Contains("/bangumi/play/", StringComparison.OrdinalIgnoreCase)) return;
        var bv = ExtractBv(url);
        var aid = Regex.Match(url, @"av(\d+)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : "";
        if (bv.Length == 0 && aid.Length == 0) return;
        _ = PrefetchBiliAsync(bv, aid);
    }

    private async Task PrefetchBiliAsync(string bv, string aid)
    {
        var key = bv.Length > 0 ? bv : ("av" + aid);
        if (_biliCache.TryGetValue(key, out var ex) && (DateTime.UtcNow - ex.At) <= TimeSpan.FromMinutes(10)) return;
        try
        {
            var m = await BiliLogin.GetVideoMetaAsync(bv, aid, Cfg("cookie"), Cfg("proxy"));
            if (m == null || m.Title.Length == 0) return;
            var c = new BiliCache { Meta = m, At = DateTime.UtcNow };
            foreach (var p in m.Parts)
                c.Parts.Add(new PartItem
                {
                    Title = p.title,
                    Url = p.url,
                    Cid = p.cid,
                    CoverUrl = p.cover,
                    Formats = new List<FormatItem> { new() { Label = "最佳画质", Height = 0 } },
                });
            if (c.Parts.Count <= 1 && m.Cid > 0 && m.Bvid.Length > 0)
            {
                var tracks = await BiliLogin.GetPlayTracksAsync(m.Bvid, m.Cid, Cfg("cookie"), Cfg("proxy"));
                if (tracks.Count > 0) c.SingleFormats = BuildFormatItemsFromTracks(tracks, m.Duration);
            }
            _biliCache[key] = c;
        }
        catch { }
    }

    private async void Parse_Click(object sender, RoutedEventArgs e)
    {
        var url = BuildUrl(UrlBox.Text ?? "");
        SetActiveNav("search");
        if (url.Length == 0) { ShowSearch(); return; }

        ResTitle.Text = "解析中…";
        ResMeta.Text = "";
        ResCover.Source = null;
        _resCoverUrl = "";
        FormatBox.ItemsSource = null;
        ShowSkeleton();
        ShowOnly(ResultPage);
        _parseSeq++;

        // B站链接：先走官方轻量 API 瞬间出卡片，清晰度后台补；非B站/番剧走 yt-dlp
        var bv = ExtractBv(url);
        var aid = Regex.Match(url, @"av(\d+)", RegexOptions.IgnoreCase) is { Success: true } am ? am.Groups[1].Value : "";
        if (!url.Contains("/bangumi/play/", StringComparison.OrdinalIgnoreCase) && (bv.Length > 0 || aid.Length > 0)
            && await ParseBiliFastAsync(url, bv, aid))
            return;

        // 番剧 ep：走 season + playurl，免 yt-dlp
        if (url.Contains("/bangumi/play/", StringComparison.OrdinalIgnoreCase))
        {
            var epm = Regex.Match(url, @"ep(\d+)");
            if (epm.Success && long.TryParse(epm.Groups[1].Value, out var epId) && await ParsePgcFastAsync(url, epId))
                return;
        }

        await ParseYtDlpAsync(url);
    }

    private async Task ParseYtDlpAsync(string url)
    {
        var ok = await YtDlp.EnsureAsync(s => DispatcherQueue.TryEnqueue(() => ResMeta.Text = s));
        if (!ok)
        {
            HideSkeleton();
            ResTitle.Text = "yt-dlp 不可用"; ResMeta.Text = "请检查网络后重试（或手动放置 yt-dlp.exe）"; return;
        }

        var (doc, err) = await YtDlp.ParseAsync(url, Cfg("cookie"), Cfg("proxy"), true);
        if (doc == null)
        {
            HideSkeleton();
            ResTitle.Text = "解析失败"; ResMeta.Text = err; return;
        }

        var root = doc.RootElement;

        // ---- 检测 分P / 合集（playlist）----
        _parts.Clear();
        _isPlaylist = false;
        _isSeries = false;
        _isPgcSeason = false;
        var baseBv = ExtractBv(url);
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            // 先判断是「合集」（每个条目是不同 BV）还是「分P」（同一 BV 的不同 P）
            foreach (var en in entries.EnumerateArray())
            {
                if (en.ValueKind != JsonValueKind.Object) continue;
                var eBv = ExtractBv(EntryUrl(en));
                if (eBv.Length > 0 && (baseBv.Length == 0 || !eBv.Equals(baseBv, StringComparison.OrdinalIgnoreCase)))
                    _isSeries = true;
                break;
            }

            var idx = 0;
            foreach (var en in entries.EnumerateArray())
            {
                if (en.ValueKind != JsonValueKind.Object) continue;
                idx++;
                var ev = EntryUrl(en);
                string purl;
                if (_isSeries && ev.Length > 0) purl = ev;                       // 合集：各自视频链接
                else if (ev.Contains("p=")) purl = ev;                           // 分P 且已带 p=
                else purl = url + (url.Contains('?') ? "&" : "?") + "p=" + idx;  // 分P 兜底补 p=
                var ptitle = en.TryGetProperty("title", out var pt) ? (pt.GetString() ?? $"P{idx}") : $"P{idx}";
                var plabel = _isSeries ? ptitle
                    : (Regex.IsMatch(ptitle, @"^P\d+$") ? ptitle : $"P{idx} · {ptitle}");
                var ecov = en.TryGetProperty("thumbnail", out var eth) ? (eth.GetString() ?? "") : "";
                if (ecov.Length == 0 && en.TryGetProperty("thumbnails", out var ets) && ets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tn in ets.EnumerateArray())
                        if (tn.TryGetProperty("url", out var eu) && (eu.GetString() ?? "") is string us && us.Length > 0) { ecov = us; break; }
                }
                _parts.Add(new PartItem { Title = plabel, Url = purl, CoverUrl = ecov, Formats = BuildFormatItems(en) });
            }
            _isPlaylist = _parts.Count > 1;
        }

        // yt-dlp 不会自动展开合集 → 用 B 站官方 API 补充分P/合集结构
        if (_parts.Count <= 1)
        {
            var bv = ExtractBv(url);
            var aid = Regex.Match(url, @"av(\d+)", RegexOptions.IgnoreCase) is { Success: true } am ? am.Groups[1].Value : "";
            if (bv.Length > 0 || aid.Length > 0)
            {
                try
                {
                    var st = await BiliLogin.GetVideoMetaAsync(bv, aid, Cfg("cookie"), Cfg("proxy"));
                    if (st.Parts.Count > 1)
                    {
                        _parts.Clear();
                        _isSeries = st.IsSeries;
                        foreach (var p in st.Parts)
                            _parts.Add(new PartItem
                            {
                                Title = p.title,
                                Url = p.url,
                                Cid = p.cid,
                                CoverUrl = p.cover,
                                Formats = new List<FormatItem> { new() { Label = "最佳画质", Height = 0 } },
                            });
                        _isPlaylist = true;
                        if (!string.IsNullOrEmpty(st.Title)) ResTitle.Text = st.Title;
                        if (st.Thumb.Length > 0 && string.IsNullOrEmpty(_resCoverUrl))
                        {
                            _resCoverUrl = st.Thumb;
                            ResCover.Source = CoverCache.Get(st.Thumb, 420);
                        }
                    }
                }
                catch { }
            }
        }
        // 番剧（PGC）：用 season 接口列出整季选集
        if (url.Contains("/bangumi/play/", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var (st, sth, sparts) = await BiliLogin.GetSeasonStructureAsync(url, Cfg("cookie"), Cfg("proxy"));
                if (sparts.Count > 0)
                {
                    _parts.Clear();
                    _isSeries = true;
                    _isPgcSeason = true;
                    foreach (var p in sparts)
                        _parts.Add(new PartItem
                        {
                            Title = p.title,
                            Url = p.url,
                            Formats = new List<FormatItem> { new() { Label = "最佳画质", Height = 0 } },
                        });
                    _isPlaylist = _parts.Count > 1;
                    if (!string.IsNullOrEmpty(st)) ResTitle.Text = st;
                    if (sth.Length > 0) { _resCoverUrl = sth; ResCover.Source = CoverCache.Get(sth, 420); }
                }
            }
            catch { }
        }

        try { PartsLabel.Text = _isPgcSeason ? "选集" : _isSeries ? "合集" : "分P"; } catch { }

        ResTitle.Text = root.TryGetProperty("title", out var t) ? (t.GetString() ?? url) : url;
        var up = root.TryGetProperty("uploader", out var u) ? (u.GetString() ?? "") : "";
        var dur = "";
        if (root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
        {
            _resDuration = d.GetDouble();
            dur = TimeSpan.FromSeconds(_resDuration).ToString(@"hh\:mm\:ss");
        }
        else _resDuration = 0;
        _resAudioBytes = EstimateAudioBytes(root, _resDuration);
        ResMeta.Text = _isPlaylist
            ? $"共 {_parts.Count} {(_isPgcSeason ? "集" : _isSeries ? "个视频" : "P")}" + (up.Length > 0 ? "    " + up : "")
            : (up.Length > 0 ? up + "    " : "") + dur;
        var thumb = root.TryGetProperty("thumbnail", out var th) ? (th.GetString() ?? "") : "";
        if (thumb.Length == 0 && root.TryGetProperty("entries", out var entTh) && entTh.ValueKind == JsonValueKind.Array)
        {
            foreach (var en in entTh.EnumerateArray())
            {
                if (en.TryGetProperty("thumbnail", out var eth) && eth.GetString() is string ets && ets.Length > 0) { thumb = ets; break; }
                if (en.TryGetProperty("thumbnails", out var ths) && ths.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tn in ths.EnumerateArray())
                        if (tn.TryGetProperty("url", out var u3) && (u3.GetString() ?? "") is string s3 && s3.Length > 0) { thumb = s3; break; }
                    if (thumb.Length > 0) break;
                }
            }
        }
        if (thumb.Length > 0)
        {
            _resCoverUrl = thumb;
            ResCover.Source = CoverCache.Get(thumb, 420);
        }

        if (_isPlaylist)
        {
            var unit = _isPgcSeason ? "集" : _isSeries ? "个" : "P";
            var pis = new List<object> { $"全部（{_parts.Count} {unit}）" };
            foreach (var p in _parts) pis.Add(p);
            _suppressParts = true;
            PartsBox.ItemsSource = pis;
            PartsBox.SelectedIndex = 1;   // 默认第一个 P
            _suppressParts = false;
            PartsRow.Visibility = Visibility.Visible;
            FormatBox.ItemsSource = _parts[0].Formats;
        }
        else
        {
            PartsRow.Visibility = Visibility.Collapsed;
            PartsBox.ItemsSource = null;
            FormatBox.ItemsSource = BuildFormatItems(root, _resDuration);
            try { AddBatchBtn.IsEnabled = true; } catch { }
        }
        FormatBox.SelectedIndex = 0;
        HideSkeleton();
        if (_isPlaylist) _ = LoadSelectedPartFormatsAsync();
    }

    private static string EntryUrl(JsonElement en)
    {
        if (en.TryGetProperty("webpage_url", out var wu) && (wu.GetString() ?? "") is string s && s.Length > 0) return s;
        if (en.TryGetProperty("url", out var u2) && (u2.GetString() ?? "") is string s2 && s2.Length > 0) return s2;
        return "";
    }

    private static string ExtractBv(string s)
    {
        var m = Regex.Match(s ?? "", "BV[0-9A-Za-z]{10}");
        return m.Success ? m.Value : "";
    }

    private static List<FormatItem> BuildFormatItems(JsonElement video, double duration = 0)
    {
        var items = new List<FormatItem> { new() { Id = "", Label = "最佳画质", Height = 0 } };
        if (!video.TryGetProperty("formats", out var fmts) || fmts.ValueKind != JsonValueKind.Array) return items;
        var groups = new Dictionary<int, Dictionary<string, (string id, int fps, long size)>>();
        foreach (var f in fmts.EnumerateArray())
        {
            var vcodec = f.TryGetProperty("vcodec", out var vc) ? (vc.GetString() ?? "") : "";
            if (string.IsNullOrEmpty(vcodec) || vcodec == "none") continue;
            var id = f.TryGetProperty("format_id", out var fid) ? (fid.GetString() ?? "") : "";
            if (string.IsNullOrEmpty(id)) continue;
            var w = f.TryGetProperty("width", out var w2) && w2.ValueKind == JsonValueKind.Number ? w2.GetInt32() : 0;
            var h = f.TryGetProperty("height", out var h2) && h2.ValueKind == JsonValueKind.Number ? h2.GetInt32() : 0;
            var shortSide = (w > 0 && h > 0) ? Math.Min(w, h) : (h > 0 ? h : w);
            if (shortSide <= 0) continue;
            var fps = f.TryGetProperty("fps", out var fp) && fp.ValueKind == JsonValueKind.Number ? (int)Math.Round(fp.GetDouble()) : 0;
            var size = FmtSize(f, duration);
            if (!groups.TryGetValue(shortSide, out var map)) { map = new Dictionary<string, (string id, int fps, long size)>(); groups[shortSide] = map; }
            var ck = CodecKey(vcodec);
            if (!map.TryGetValue(ck, out var cur) || fps > cur.fps || (fps == cur.fps && size > cur.size)) map[ck] = (id, fps, size);
        }
        var ordered = groups.OrderByDescending(x => x.Key).ToList();
        foreach (var g in ordered)
        {
            var map = g.Value;
            var fps = map.Values.Max(x => x.fps);
            var stable = map.TryGetValue("avc", out var av) ? av.id : map.Values.OrderByDescending(x => x.fps).First().id;
            var codecIds = map.ToDictionary(x => x.Key, x => x.Value.id);
            var codecSizes = map.Where(x => x.Value.size > 0).ToDictionary(x => x.Key, x => x.Value.size);
            var label = QualityName(g.Key) + (fps >= 50 ? " " + fps + "fps" : "");
            items.Add(new FormatItem { Id = stable, Label = label, Height = g.Key, Fps = fps, CodecIds = codecIds, CodecSizes = codecSizes });
        }
        if (ordered.Count > 0)   // 「最佳画质」沿用最高清晰度的编码/体积，供“自动”判断
        {
            items[0].CodecIds = new Dictionary<string, string>(items[1].CodecIds);
            items[0].CodecSizes = new Dictionary<string, long>(items[1].CodecSizes);
        }
        return items;
    }

    // 从 wbi playurl 的 DASH 轨构建清晰度列表（无 yt-dlp id，下载回退按高度+编码选择）
    private static List<FormatItem> BuildFormatItemsFromTracks(
        List<(int width, int height, string codecs, double fps, long bandwidth)> tracks, double duration)
    {
        var items = new List<FormatItem> { new() { Id = "", Label = "最佳画质", Height = 0 } };
        if (tracks.Count == 0) return items;
        var groups = new Dictionary<int, Dictionary<string, (int fps, long size)>>();
        foreach (var t in tracks)
        {
            var shortSide = t.height > 0 ? t.height : t.width;
            if (shortSide <= 0) continue;
            var size = duration > 0 && t.bandwidth > 0 ? (long)(t.bandwidth / 8.0 * duration) : 0;
            var fps = (int)Math.Round(t.fps);
            var ck = CodecKey(t.codecs);
            if (!groups.TryGetValue(shortSide, out var map)) { map = new Dictionary<string, (int, long)>(); groups[shortSide] = map; }
            if (!map.TryGetValue(ck, out var cur) || fps > cur.fps || (fps == cur.fps && size > cur.size)) map[ck] = (fps, size);
        }
        foreach (var g in groups.OrderByDescending(x => x.Key))
        {
            var map = g.Value;
            var fps = map.Values.Max(x => x.fps);
            var codecIds = map.Keys.ToDictionary(k => k, _ => "");   // 无固定 id → 按高度+编码选择
            var codecSizes = map.Where(x => x.Value.size > 0).ToDictionary(x => x.Key, x => x.Value.size);
            var label = QualityName(g.Key) + (fps >= 50 ? " " + fps + "fps" : "");
            items.Add(new FormatItem { Id = "", Label = label, Height = g.Key, Fps = fps, CodecIds = codecIds, CodecSizes = codecSizes });
        }
        if (items.Count > 1)
        {
            items[0].CodecIds = new Dictionary<string, string>(items[1].CodecIds);
            items[0].CodecSizes = new Dictionary<string, long>(items[1].CodecSizes);
        }
        return items;
    }

    private static long FmtSize(JsonElement f, double duration)
    {
        if (f.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number && fs.GetInt64() > 0) return fs.GetInt64();
        if (f.TryGetProperty("filesize_approx", out var fa) && fa.ValueKind == JsonValueKind.Number && fa.GetDouble() > 0) return (long)fa.GetDouble();
        if (duration > 0 && f.TryGetProperty("tbr", out var tb) && tb.ValueKind == JsonValueKind.Number && tb.GetDouble() > 0)
            return (long)(tb.GetDouble() * 1000 / 8 * duration);
        return 0;
    }

    private static long EstimateAudioBytes(JsonElement root, double duration)
    {
        if (!root.TryGetProperty("formats", out var fmts) || fmts.ValueKind != JsonValueKind.Array) return 0;
        long best = 0; double abr = 0;
        foreach (var f in fmts.EnumerateArray())
        {
            var vcodec = f.TryGetProperty("vcodec", out var vc) ? (vc.GetString() ?? "") : "";
            var acodec = f.TryGetProperty("acodec", out var ac) ? (ac.GetString() ?? "") : "";
            if (vcodec != "none" || string.IsNullOrEmpty(acodec) || acodec == "none") continue;
            var sz = FmtSize(f, duration);
            if (sz > best) best = sz;
            if (f.TryGetProperty("abr", out var ab) && ab.ValueKind == JsonValueKind.Number) abr = Math.Max(abr, ab.GetDouble());
        }
        if (best > 0) return best;
        if (abr > 0 && duration > 0) return (long)(abr * 1000 / 8 * duration);
        return 0;
    }

    // 「自动」按体积选编码：大视频（约≥2GB）选省空间的 AV1→H.265→H.264；小视频选更清晰的 H.264→H.265→AV1
    private string[] AutoCodecOrder(FormatItem? item)
    {
        var sizes = new Dictionary<string, long>();
        if (item != null)
            foreach (var kv in item.CodecSizes)
                if (kv.Value > 0) sizes[kv.Key] = kv.Value + _resAudioBytes;
        if (sizes.Count == 0) return new[] { "av01", "hev", "avc" };
        var big = sizes.Values.Max() >= BigSizeThreshold;
        var order = sizes.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();   // 小 → 大
        if (!big) order.Reverse();                                                 // 小视频 → 清晰（体积大）
        foreach (var k in new[] { "av01", "hev", "avc" }) if (!order.Contains(k)) order.Add(k);
        return order.ToArray();
    }

    private void PartsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressParts || !_isPlaylist) return;
        _ = LoadSelectedPartFormatsAsync();
    }

    private async Task LoadSelectedPartFormatsAsync()
    {
        try
        {
            var sel = PartsBox.SelectedIndex;
            var p = sel >= 1 && sel - 1 < _parts.Count ? _parts[sel - 1] : (_parts.Count > 0 ? _parts[0] : null);
            if (p == null) return;
            // 切换分P / 合集视频时同步更新封面（下载封面也跟随）
            var cov = sel == 0 ? _seriesCoverUrl : (p.CoverUrl.Length > 0 ? p.CoverUrl : _seriesCoverUrl);
            if (cov.Length > 0) { _resCoverUrl = cov; ResCover.Source = CoverCache.Get(cov, 420); }
            FormatBox.ItemsSource = p.Formats;
            FormatBox.SelectedIndex = 0;
            if (p.Formats.Count > 1) { try { AddBatchBtn.IsEnabled = true; } catch { } return; }
            // 只有“自动” → 按需解析该 P 的真实清晰度；加载期间禁用「加入下载列表」
            try { AddBatchBtn.IsEnabled = false; AddBatchBtn.Content = "解析中…"; } catch { }
            try { FmtRing.IsActive = true; FmtRing.Visibility = Visibility.Visible; FormatBox.IsEnabled = false; } catch { }
            try { await EnsurePartFormatsAsync(p); } catch { }
            try { FmtRing.IsActive = false; FmtRing.Visibility = Visibility.Collapsed; FormatBox.IsEnabled = true; } catch { }
            try { AddBatchBtn.IsEnabled = true; AddBatchBtn.Content = "加入下载列表"; } catch { }
            var cur = PartsBox.SelectedIndex;
            var same = (cur == 0 && p == _parts[0]) || (cur >= 1 && cur - 1 < _parts.Count && _parts[cur - 1] == p);
            if (same)
            {
                FormatBox.ItemsSource = p.Formats;
                FormatBox.SelectedIndex = 0;
            }
        }
        catch { try { AddBatchBtn.IsEnabled = true; AddBatchBtn.Content = "加入下载列表"; } catch { } }
    }

    private async Task EnsurePartFormatsAsync(PartItem p)
    {
        if (p.Formats.Count > 1 || p.Url.Length == 0) return;
        // 先试 wbi playurl
        try
        {
            if (p.EpId > 0 && p.Cid > 0)
            {
                var tracks = await BiliLogin.GetPlayTracksEpAsync(p.EpId, p.Cid, Cfg("cookie"), Cfg("proxy"));
                if (tracks.Count > 0) { p.Formats = BuildFormatItemsFromTracks(tracks, 0); return; }
            }
            var bv = ExtractBv(p.Url);
            if (bv.Length > 0 && p.Cid > 0)
            {
                var tracks = await BiliLogin.GetPlayTracksAsync(bv, p.Cid, Cfg("cookie"), Cfg("proxy"));
                if (tracks.Count > 0) { p.Formats = BuildFormatItemsFromTracks(tracks, 0); return; }
            }
        }
        catch { }
        var (doc, _) = await YtDlp.ParseAsync(p.Url, Cfg("cookie"), Cfg("proxy"), false);
        if (doc != null)
        {
            try
            {
                var dur = doc.RootElement.TryGetProperty("duration", out var de) && de.ValueKind == JsonValueKind.Number ? de.GetDouble() : 0;
                p.Formats = BuildFormatItems(doc.RootElement, dur);
            }
            finally { doc.Dispose(); }
        }
    }

    // 多P：按高度+编码构表达式（不同P的 format id 不同，不能用固定 id）
    private string BuildExprByHeight(int height, string codecKey, string[]? autoOrder = null)
    {
        var h = height > 0 ? $"[height<={height}]" : "";
        if (codecKey == "auto")
        {
            var order = autoOrder ?? new[] { "av01", "hev", "avc" };
            var sel = new List<string>();
            foreach (var k in order) sel.Add($"bv*{h}[vcodec^={k}]+ba");
            sel.Add($"bv*{h}+ba");
            return string.Join("/", sel) + "/b";
        }
        return $"bv*{h}[vcodec^={codecKey}]+ba/bv*{h}[vcodec^={codecKey}]/b";
    }

    private Storyboard? _skeletonSb;
    private void ShowSkeleton()
    {
        ParseSkeleton.Visibility = Visibility.Visible;
        ResultBody.Visibility = Visibility.Collapsed;
        try
        {
            if (_skeletonSb == null)
            {
                _skeletonSb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
                var a = new DoubleAnimation { From = 0.45, To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(750)) };
                Storyboard.SetTarget(a, ParseSkeleton);
                Storyboard.SetTargetProperty(a, "Opacity");
                _skeletonSb.Children.Add(a);
            }
            _skeletonSb.Begin();
        }
        catch { }
    }

    private void HideSkeleton()
    {
        try { _skeletonSb?.Stop(); } catch { }
        ParseSkeleton.Opacity = 1;
        ParseSkeleton.Visibility = Visibility.Collapsed;
        ResultBody.Visibility = Visibility.Visible;
    }

    private static string QualityName(int shortSide)
    {
        if (shortSide <= 0) return "视频";
        if (shortSide >= 4320) return "8K";
        if (shortSide >= 2160) return "4K";
        if (shortSide >= 1440) return "2K";
        if (shortSide >= 1080) return "1080P";
        if (shortSide >= 720) return "720P";
        if (shortSide >= 480) return "480P";
        if (shortSide >= 360) return "360P";
        return shortSide + "P";
    }

    private static string CodecKey(string vcodec)
    {
        var v = vcodec.ToLowerInvariant();
        if (v.StartsWith("av01")) return "av01";
        if (v.StartsWith("hev") || v.StartsWith("hvc")) return "hev";
        if (v.StartsWith("avc") || v.StartsWith("h264")) return "avc";
        return v.Split('.')[0];
    }

    private static string CodecLabel(string key) => key switch { "hev" => "H.265", "av01" => "AV1", "avc" => "H.264", _ => key };

    private string BuildFormatExpr(FormatItem? item, string codecKey)
    {
        var h = "";
        if (item == null || item.Height == 0)
        {
            var q = SetQuality.SelectedIndex;
            h = q == 1 ? "[height<=1080]" : q == 2 ? "[height<=720]" : "";
        }
        // 自动：按预估体积决定编码偏好（大→省空间，小→更清晰）
        if (codecKey == "auto")
        {
            var order = AutoCodecOrder(item);
            if (item != null && item.Height > 0)
            {
                var opts = new List<string>();
                foreach (var k in order)
                    if (item.CodecIds.TryGetValue(k, out var id) && id.Length > 0) opts.Add(id + "+ba");
                if (opts.Count == 0) return BuildExprByHeight(item.Height, codecKey);
                return string.Join("/", opts) + "/b";
            }
            var sel = new List<string>();
            foreach (var k in order) sel.Add($"bv*{h}[vcodec^={k}]+ba");
            sel.Add($"bv*{h}+ba");
            return string.Join("/", sel) + "/b";
        }
        if (item == null || item.Height == 0)
        {
            var cf = $"[vcodec^={codecKey}]";
            return $"bv*{h}{cf}+ba/bv*{h}{cf}/b";
        }
        // 指定清晰度：优先用所选编码，没有就退回最稳定/最省空间的
        var baseId = item.Id;
        if (item.CodecIds.TryGetValue(codecKey, out var id2)) baseId = id2;
        if (baseId.Length == 0) return BuildExprByHeight(item.Height, codecKey);
        return baseId + "+bestaudio/best";
    }

    /* ---------------- 下载 ---------------- */
    private void Enqueue(DownloadItem item)
    {
        item.Paused = false;
        item.Done = false;
        item.ButtonVisible = true;
        item.ButtonText = "暂停";
        item.Indeterminate = true;
        item.ProgressVisible = true;
        item.Status = "排队中…";
        _queue.Add(item);
        PumpQueue();
    }

    private void PumpQueue()
    {
        var max = int.TryParse(Cfg("concurrent", "2"), out var m) ? Math.Clamp(m, 1, 8) : 2;
        while (_running < max && _queue.Count > 0)
        {
            var it = _queue[0];
            _queue.RemoveAt(0);
            _ = RunQueuedAsync(it);
        }
    }

    private async Task RunQueuedAsync(DownloadItem it)
    {
        _running++;
        try { await RunDownloadAsync(it); }
        finally { _running--; PumpQueue(); SaveDownloads(); }
    }

    private void DownloadItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _ctxItem = (sender as FrameworkElement)?.DataContext as DownloadItem;
    }

    private void DownloadMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout mf) return;
        var it = (mf.Target as FrameworkElement)?.DataContext as DownloadItem ?? _ctxItem;
        if (it != null) _ctxItem = it;
        var canRedownload = it != null && it.Done
                            && (it.FilePath.Length == 0 || !File.Exists(it.FilePath));
        foreach (var mi in mf.Items.OfType<MenuFlyoutItem>())
            if ((mi.Tag as string) == "redownload")
                mi.Visibility = canRedownload ? Visibility.Visible : Visibility.Collapsed;
    }

    /* ---------------- 类型：视频 / 仅音频 ---------------- */
    private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var audio = TypeBox.SelectedIndex == 1;
        if (VideoOpts != null) VideoOpts.Visibility = audio ? Visibility.Collapsed : Visibility.Visible;
        if (AudioOpts != null) AudioOpts.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
    }

    private string CurrentAudioFmt() => AudioFmtBox.SelectedIndex switch { 1 => "m4a", 2 => "flac", _ => "mp3" };

    // 生成文件名后缀用的安全文本（去掉非法字符与 %）
    private static string SanitizeTag(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var bad = Path.GetInvalidFileNameChars();
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(bad, chars[i]) >= 0 || chars[i] == '%') chars[i] = '_';
        return new string(chars).Trim();
    }

    /* ---------------- 批量列表 ---------------- */
    private void AddToBatch_Click(object sender, RoutedEventArgs e)
    {
        var url = BuildUrl(UrlBox.Text ?? "");
        if (url.Length == 0) return;
        var audioOnly = TypeBox.SelectedIndex == 1;
        var fi = FormatBox.SelectedItem as FormatItem;
        var codecKey = CodecBox.SelectedIndex switch { 1 => "avc", 2 => "hev", 3 => "av01", _ => "auto" };
        var title = string.IsNullOrWhiteSpace(ResTitle.Text) ? url : ResTitle.Text;
        var quality = fi == null || fi.Height == 0 ? "跟随设置" : fi.Label;
        var codecName = codecKey switch { "avc" => "H.264", "hev" => "H.265", "av01" => "AV1", _ => "自动" };
        var autoOrder = codecKey == "auto" ? AutoCodecOrder(fi) : null;
        // 文件名后缀：不同清晰度/编码写成不同文件，避免先下 720P 再下 4K 时被 yt-dlp 当作“已下载”复用旧文件
        var nameTag = audioOnly ? "" : SanitizeTag(((fi == null || fi.Height == 0 ? "" : fi.Label) +
                          (codecKey == "auto" ? "" : " " + codecName)).Trim());
        if (!audioOnly && nameTag.Length == 0) nameTag = "最佳";

        // 多P：根据选择加入全部 / 指定分P
        if (_isPlaylist && _parts.Count > 0)
        {
            var sel = PartsBox.SelectedIndex;   // 0 = 全部
            var targets = sel >= 1 && sel - 1 < _parts.Count
                ? new List<PartItem> { _parts[sel - 1] }
                : _parts;
            foreach (var p in targets)
            {
                _batch.Add(new BatchItem
                {
                    Title = $"{title} - {p.Title}",
                    Meta = audioOnly ? $"仅音频 · {CurrentAudioFmt()}" : $"{quality} · {codecName}",
                    Url = p.Url.Length > 0 ? p.Url : url,
                    FmtExpr = audioOnly ? "ba/b" : BuildExprByHeight(fi?.Height ?? 0, codecKey, autoOrder),
                    NameTag = nameTag,
                    AudioOnly = audioOnly,
                    AudioFmt = audioOnly ? CurrentAudioFmt() : "",
                    Cover = ResCover.Source,
                    CoverUrl = _resCoverUrl,
                });
            }
            UpdateBatchUi();
            ShowToast($"已加入下载列表（{targets.Count} 个）", "ok");
            return;
        }

        _batch.Add(new BatchItem
        {
            Title = title,
            Meta = audioOnly ? $"仅音频 · {CurrentAudioFmt()}" : $"{quality} · {codecName}",
            Url = url,
            FmtExpr = audioOnly ? "ba/b" : BuildFormatExpr(fi, codecKey),
            NameTag = nameTag,
            AudioOnly = audioOnly,
            AudioFmt = audioOnly ? CurrentAudioFmt() : "",
            Cover = ResCover.Source,
            CoverUrl = _resCoverUrl,
        });
        UpdateBatchUi();
        ShowToast("已加入下载列表", "ok");
    }

    private void UpdateBatchUi()
    {
        BatchCard.Visibility = _batch.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        BatchTitle.Text = $"下载列表（{_batch.Count}）";
    }

    private void RemoveBatch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BatchItem b)
        {
            _batch.Remove(b);
            UpdateBatchUi();
        }
    }

    private void ClearBatch_Click(object sender, RoutedEventArgs e)
    {
        _batch.Clear();
        UpdateBatchUi();
    }

    private void DownloadAll_Click(object sender, RoutedEventArgs e)
    {
        if (_batch.Count == 0) { ShowToast("下载列表为空", "info"); return; }
        var dir = Cfg("dir", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BiliGet"));
        foreach (var b in _batch)
        {
            var item = new DownloadItem
            {
                Title = b.Title,
                Status = "排队中…",
                Cover = b.Cover,
                CoverUrl = b.CoverUrl,
                Url = b.Url,
                FmtExpr = b.FmtExpr,
                NameTpl = string.IsNullOrEmpty(b.NameTag) ? "%(title)s.%(ext)s" : "%(title)s [" + b.NameTag + "].%(ext)s",
                AudioOnly = b.AudioOnly,
                AudioFmt = b.AudioFmt,
                Dir = dir,
            };
            _downloads.Insert(0, item);
            Enqueue(item);
        }
        _batch.Clear();
        UpdateBatchUi();
        DlEmpty.Visibility = Visibility.Collapsed;
        SetActiveNav("download");
        ShowOnly(DownloadsPage);
        SaveDownloads();
    }

    private void OpenDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_ctxItem is not DownloadItem it) return;
        try
        {
            if (it.FilePath.Length > 0 && File.Exists(it.FilePath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + it.FilePath + "\"") { UseShellExecute = true });
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(it.Dir) { UseShellExecute = true });
        }
        catch { }
    }

    private void Redownload_Click(object sender, RoutedEventArgs e)
    {
        if (_ctxItem is not DownloadItem it) return;
        it.FilePath = "";
        it.Progress = 0;
        Enqueue(it);
        SaveDownloads();
        ShowToast("已重新加入下载", "ok");
    }

    private void RefreshDownloadStatuses()
    {
        foreach (var it in _downloads)
        {
            if (!it.Done) continue;
            it.Status = (it.FilePath.Length == 0 || !File.Exists(it.FilePath))
                ? "文件已不在原位置"
                : "已完成 · 点击打开";
        }
    }

    private bool DelFileOnRemove => Cfg("delfile", "0") == "1";

    // 删除下载产物文件（含同名 .srt 字幕），返回释放的字节数
    private static long DeleteOutputFile(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return 0;
            long freed = 0;
            var srt = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + ".srt");
            foreach (var f in new[] { path, srt })
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    var len = new FileInfo(f).Length;
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    File.Delete(f);
                    if (!File.Exists(f)) freed += len;
                }
                catch { }
            }
            return freed;
        }
        catch { return 0; }
    }

    private void RemoveDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_ctxItem is not DownloadItem it) return;
        var delFile = DelFileOnRemove && it.Done && it.FilePath.Length > 0 ? it.FilePath : "";
        try { it.Cts?.Cancel(); } catch { }
        _queue.Remove(it);
        _downloads.Remove(it);
        if (_downloads.Count == 0) DlEmpty.Visibility = Visibility.Visible;
        SaveDownloads();
        if (delFile.Length > 0)
        {
            _ = Task.Run(async () => { await Task.Delay(800); var freed = DeleteOutputFile(delFile); if (freed > 0) RunOnUi(() => ShowToast($"已删除记录与文件（{Mb(freed)}）", "info")); });
            return;
        }
        // 若没有其它未完成下载，顺带清理未下完的临时文件
        if (!_downloads.Any(d => !d.Done))
        {
            var dir = Cfg("dir", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BiliGet"));
            _ = Task.Run(async () => { await Task.Delay(1200); CleanTemp(dir); });
        }
    }

    private sealed class DlState
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string FmtExpr { get; set; } = "";
        public bool AudioOnly { get; set; }
        public string AudioFmt { get; set; } = "";
        public string Dir { get; set; } = "";
        public string NameTpl { get; set; } = "%(title)s.%(ext)s";
        public string FilePath { get; set; } = "";
        public string CoverUrl { get; set; } = "";
        public bool Done { get; set; }
        public double Progress { get; set; }
    }

    private void LoadDownloads()
    {
        try
        {
            if (!File.Exists(DlStatePath)) return;
            var arr = JsonSerializer.Deserialize<List<DlState>>(File.ReadAllText(DlStatePath));
            if (arr == null || arr.Count == 0) return;
            foreach (var s in arr)
            {
                var it = new DownloadItem
                {
                    Title = s.Title,
                    Status = s.Done ? "已完成 · 点击打开" : "上次未完成 · 点「继续」续传",
                    Url = s.Url,
                    FmtExpr = s.FmtExpr,
                    AudioOnly = s.AudioOnly,
                    AudioFmt = s.AudioFmt ?? "",
                    Dir = s.Dir,
                    NameTpl = string.IsNullOrEmpty(s.NameTpl) ? "%(title)s.%(ext)s" : s.NameTpl,
                    FilePath = s.FilePath ?? "",
                    CoverUrl = s.CoverUrl ?? "",
                    Done = s.Done,
                    Progress = s.Done ? 100 : s.Progress,
                    Paused = !s.Done,
                    Indeterminate = false,
                    ProgressVisible = !s.Done,
                    ButtonVisible = !s.Done,
                    ButtonText = s.Done ? "打开" : "继续",
                };
                if (it.Done && (it.FilePath.Length == 0 || !File.Exists(it.FilePath)))
                    it.Status = "文件已不在原位置";
                if (it.CoverUrl.Length > 0)
                    it.Cover = CoverCache.Get(it.CoverUrl, 160);
                _downloads.Add(it);
            }
            if (_downloads.Count > 0) DlEmpty.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private void SaveDownloads()
    {
        try
        {
            var arr = _downloads.Select(d => new DlState
            {
                Title = d.Title,
                Url = d.Url,
                FmtExpr = d.FmtExpr,
                AudioOnly = d.AudioOnly,
                AudioFmt = d.AudioFmt,
                Dir = d.Dir,
                NameTpl = d.NameTpl,
                FilePath = d.FilePath,
                CoverUrl = d.CoverUrl,
                Done = d.Done,
                Progress = d.Progress,
            }).ToList();
            Directory.CreateDirectory(CfgDir);
            File.WriteAllText(DlStatePath, JsonSerializer.Serialize(arr));
        }
        catch { }
        RefreshDownloads();
    }

    private void RefreshDownloads()
    {
        try
        {
            var active = _downloads.Where(d => !d.Done).ToList();
            var done = _downloads.Where(d => d.Done).ToList();
            ActiveList.ItemsSource = active;
            DoneList.ItemsSource = done;
            ActiveExp.Header = $"进行中（{active.Count}）";
            DoneExp.Header = $"已完成（{done.Count}）";
            ActiveExp.Visibility = active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            DoneExp.Visibility = done.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            DlEmpty.Visibility = _downloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_downloads.Count > 0 && _activeDownloads == 0 && active.Count == 0 && _queue.Count == 0)
                DoneExp.IsExpanded = true;
        }
        catch { }
    }

    private void TogglePause_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadItem it) return;

        if (it.Done)
        {
            if (it.FilePath.Length > 0 && File.Exists(it.FilePath))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(it.FilePath) { UseShellExecute = true }); } catch { }
            }
            return;
        }

        if (it.ButtonText == "重试" || it.Paused)
        {
            Enqueue(it);
            SaveDownloads();
            return;
        }

        // 暂停：排队中的直接移出队列；正在下载的取消（保留 .part 以便续传）
        it.Paused = true;
        it.ButtonText = "继续";
        it.Indeterminate = false;
        it.Status = "已暂停（可点「继续」续传）";
        if (_queue.Remove(it)) { }
        else { try { it.Cts?.Cancel(); } catch { } }
        SaveDownloads();
    }

    private async Task RunDownloadAsync(DownloadItem item)
    {
        var dir = item.Dir;
        item.Done = false;
        item.Indeterminate = true;
        item.ProgressVisible = true;
        item.ButtonVisible = true;
        item.ButtonText = "暂停";
        item.Progress = 0;
        item.Status = "正在准备下载…";
        item.Cts?.Dispose();
        item.Cts = new CancellationTokenSource();
        var ct = item.Cts.Token;

        var started = DateTime.UtcNow;
        var last = "";
        var lastTick = DateTime.MinValue;
        _activeDownloads++;
        string? file;
        try
        {
            file = await YtDlp.DownloadAsync(item.Url, item.FmtExpr, dir, Cfg("cookie"), Cfg("proxy"), item.NameTpl,
            (p, info) =>
            {
                // 时间节流（约 12.5fps）：进度条平滑增长，又不会刷爆 UI
                var now = DateTime.UtcNow;
                if (p < 100 && (now - lastTick).TotalMilliseconds < 80) return;
                lastTick = now;
                DispatcherQueue.TryEnqueue(() =>
                {
                    item.Indeterminate = false;
                    item.Progress = p;
                    item.Status = info.Length > 0 ? $"下载中 {p:0.#}%  ·  {info}" : $"下载中 {p:0.#}%";
                });
            },
            s =>
            {
                if (!string.IsNullOrWhiteSpace(s)) last = s;
                var t = s.Trim();
                if (t.Contains("Destination:"))
                {
                    var isAudio = t.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) || t.Contains("bestaudio");
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        item.Indeterminate = false;
                        item.Status = isAudio ? "正在下载音频流…" : "正在下载视频流…";
                    });
                }
                else if (t.Contains("Merging formats") || t.Contains("[Merger]"))
                {
                    DispatcherQueue.TryEnqueue(() => { item.Indeterminate = false; item.Progress = 99; item.Status = "正在合并音视频…"; });
                }
                else if (t.Contains("Extracting audio") || t.Contains("[ExtractAudio]"))
                {
                    DispatcherQueue.TryEnqueue(() => { item.Indeterminate = false; item.Progress = 99; item.Status = "正在转换音频…"; });
                }
            }, ct, item.AudioOnly ? (item.AudioFmt.Length > 0 ? item.AudioFmt : "mp3") : null, SetSubs.IsChecked == true);
        }
        finally { _activeDownloads--; }

        if (ct.IsCancellationRequested)
        {
            item.Indeterminate = false;
            if (!item.Paused) item.Status = "已暂停（可点「继续」续传）";
            item.Cts?.Dispose();
            item.Cts = null;
            return;
        }

        string? saved = null;
        if (!string.IsNullOrEmpty(file) && File.Exists(file)) saved = file;
        else
        {
            // 兜底：只找“本次下载开始后”新写入的文件，避免误取旧文件
            try
            {
                saved = new DirectoryInfo(dir).GetFiles()
                    .Where(f => f.LastWriteTimeUtc >= started.AddSeconds(-3))
                    .Where(f => !IsTempFile(f.Name))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault()?.FullName;
            }
            catch { }
        }

        if (saved != null && File.Exists(saved))
        {
            item.FilePath = saved;
            item.Done = true;
            item.Indeterminate = false;
            item.ProgressVisible = false;
            item.ButtonVisible = false;
            item.Progress = 100;
            item.Status = "已完成 · 点击打开";
            ShowToast("下载完成：" + Path.GetFileName(saved), "ok");
        }
        else
        {
            item.Indeterminate = false;
            item.ButtonText = "重试";
            item.Status = "下载失败：" + YtDlp.FriendlyError(last);
            ShowToast("下载失败，详情见下载列表。", "err");
        }
        item.Cts?.Dispose();
        item.Cts = null;
    }

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowOnly(LoginPage);
            SetActiveNav("");
            LoginCard.Visibility = Visibility.Visible;
            WebPanel.Visibility = Visibility.Collapsed;
            CaptchaPanel.Visibility = Visibility.Collapsed;
            SetLoginMode(false);
            LoginMsg.Text = "登录成功后会自动获取账号并载入观看历史。";
            _ = StartQrLoginAsync();
        }
        catch (Exception ex) { QrStatus.Text = "无法开始登录：" + ex.Message; }
    }

    /* ---------------- 登录页：扫码 / 手机号（内嵌官方页） ---------------- */

    private void LoginTabPwd_Click(object sender, RoutedEventArgs e) => SetLoginMode(true);
    private void LoginTabSms_Click(object sender, RoutedEventArgs e) => SetLoginMode(false);

    private void SetLoginMode(bool pwd)
    {
        SmsForm.Visibility = pwd ? Visibility.Collapsed : Visibility.Visible;
        PwdForm.Visibility = pwd ? Visibility.Visible : Visibility.Collapsed;
        var pill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, _accentColor.R, _accentColor.G, _accentColor.B));
        var clear = new SolidColorBrush(Colors.Transparent);
        var dim = (Brush)Application.Current.Resources["TextDim"];
        TabPwd.Background = pwd ? pill : clear; TabPwd.Foreground = pwd ? (Brush)_accentBrush : dim;
        TabSms.Background = pwd ? clear : pill; TabSms.Foreground = pwd ? dim : (Brush)_accentBrush;
    }

    private void QrExpired_Tapped(object sender, TappedRoutedEventArgs e) => _ = StartQrLoginAsync();

    private void ShowQrSuccess()
    {
        try
        {
            QrExpiredOverlay.Visibility = Visibility.Collapsed;
            QrSuccessOverlay.Visibility = Visibility.Visible;
            QrCheckTf.ScaleX = 0.3; QrCheckTf.ScaleY = 0.3;
            var sb = new Storyboard();
            foreach (var p in new[] { "ScaleX", "ScaleY" })
            {
                var a = new DoubleAnimation
                {
                    To = 1, Duration = TimeSpan.FromMilliseconds(260),
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 },
                    EnableDependentAnimation = true,
                };
                Storyboard.SetTarget(a, QrCheckTf); Storyboard.SetTargetProperty(a, p);
                sb.Children.Add(a);
            }
            sb.Begin();
        }
        catch { }
    }

    /* ---------------- 短信 / 密码登录（原生表单 + 服务器极验页） ---------------- */
    private WebView2? _captchaView;
    private string _smsTel = "";
    private string _captchaToken = "";
    private string _captchaKey = "";
    private string _captchaPurpose = "sms";
    private string _pwdUser = "";
    private string _pwdPass = "";

    private async void SendSms_Click(object sender, RoutedEventArgs e)
    {
        var tel = PhoneBox.Text.Trim();
        if (!Regex.IsMatch(tel, @"^1\d{10}$")) { PhoneMsg.Text = "请输入正确的 11 位手机号"; return; }
        SendSmsBtn.IsEnabled = false;
        PhoneMsg.Text = "正在获取验证码…";
        try
        {
            var (token, gt, challenge) = await BiliLogin.GetCaptchaAsync();
            _smsTel = tel; _captchaToken = token; _captchaPurpose = "sms";
            await ShowCaptchaAsync(gt, challenge);
        }
        catch (Exception ex) { PhoneMsg.Text = "获取验证码失败：" + ex.Message; SendSmsBtn.IsEnabled = true; }
    }

    // 本地验证码服务：把内嵌页从 127.0.0.1 发出（真实 http 源，不依赖外部服务器）
    private static System.Net.Sockets.TcpListener? _capSrv;
    private static int _capPort;
    private static string _capHtml = "";

    private const string CaptchaHtml = @"<!DOCTYPE html><html><head><meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<style>
html,body{margin:0;height:100%;background:transparent;color:#e8e8e8;font-family:'Microsoft YaHei UI','Segoe UI',sans-serif;overflow:hidden}
#box{width:100%;height:100%;display:flex;align-items:center;justify-content:center}
</style></head><body>
<div id=""box""></div>
<script src=""https://static.geetest.com/static/js/gt.0.4.9.js""></script>
<script>
(function(){
  function post(o){try{window.chrome.webview.postMessage(JSON.stringify(o));}catch(e){}}
  window.onerror=function(m){post({error:''+m});};
  var gt='__GT__', challenge='__CH__';
  function boot(){
    if(!window.initGeetest){setTimeout(boot,300);return;}
    try{
      initGeetest({gt:gt,challenge:challenge,offline:false,new_captcha:true,width:'380px',
        onError:function(m){post({error:''+m});}},
        function(c){
          var box = document.getElementById('box');
          box.style.opacity = '0';   // 先隐藏，别让用户看到“点击按钮”过程
          c.appendTo('#box');
          function autoClick(){
            var els=document.querySelectorAll('div,span,a,button');
            for(var i=0;i<els.length;i++){var e=els[i];
              if(e.children.length===0 && (e.textContent||'').indexOf('点击')>=0){ try{e.click();}catch(x){} return true; }
            }
            return false;
          }
          function reveal(){ try{ box.style.opacity='1'; }catch(e){} }
          function shown(){
            var im=document.querySelector('#box img, #box canvas');
            return im && ((im.clientWidth||0) > 120 || (im.clientHeight||0) > 120);
          }
          setTimeout(function(){ try { c.verify(); } catch(e){} }, 120);
          var n=0; var t2=setInterval(function(){ if(autoClick()||++n>30) clearInterval(t2); }, 300);
          var n2=0; var t3=setInterval(function(){ if(shown()||++n2>40){ reveal(); clearInterval(t3); } }, 200);
          c.onSuccess(function(){var v=c.getValidate();if(v&&v.geetest_validate)post(v);});
          c.onError(function(){post({error:'geetest error'});});
        });
    }catch(e){post({error:''+e});}
  }
  boot();
})();
</script></body></html>";

    private static int EnsureCaptchaServer()
    {
        if (_capSrv != null) return _capPort;
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        _capSrv = l;
        _capPort = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                System.Net.Sockets.TcpClient c;
                try { c = await l.AcceptTcpClientAsync(); } catch { break; }
                _ = Task.Run(() => ServeCaptcha(c));
            }
        });
        return _capPort;
    }

    private static void ServeCaptcha(System.Net.Sockets.TcpClient c)
    {
        try
        {
            using var s = c.GetStream();
            var buf = new byte[8192];
            try { s.Read(buf, 0, buf.Length); } catch { }
            var body = System.Text.Encoding.UTF8.GetBytes(_capHtml);
            var head = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length +
                "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            s.Write(head, 0, head.Length); s.Write(body, 0, body.Length); s.Flush();
        }
        catch { }
        finally { try { c.Close(); } catch { } }
    }

    private async Task ShowCaptchaAsync(string gt, string challenge)
    {
        CaptchaPanel.Visibility = Visibility.Visible;
        if (_captchaView == null)
        {
            _captchaView = new WebView2();
            CaptchaHost.Children.Add(_captchaView);
            await _captchaView.EnsureCoreWebView2Async();
            try { _captchaView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0); } catch { }
            _captchaView.CoreWebView2.WebMessageReceived += Captcha_WebMessage;
        }
        var port = EnsureCaptchaServer();
        _capHtml = CaptchaHtml.Replace("__GT__", gt).Replace("__CH__", challenge);
        _captchaView.CoreWebView2.Navigate($"http://127.0.0.1:{port}/captcha.html");
    }

    private async void Captcha_WebMessage(Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var msg = args.TryGetWebMessageAsString();
            using var doc = JsonDocument.Parse(msg);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var er))
            {
                DisposeCaptchaView();
                CaptchaPanel.Visibility = Visibility.Collapsed;
                PhoneMsg.Text = "安全验证失败：" + (er.GetString() ?? "");
                SendSmsBtn.IsEnabled = true;
                return;
            }
            var challenge = r.TryGetProperty("geetest_challenge", out var a) ? (a.GetString() ?? "") : "";
            var validate = r.TryGetProperty("geetest_validate", out var b) ? (b.GetString() ?? "") : "";
            var seccode = r.TryGetProperty("geetest_seccode", out var c) ? (c.GetString() ?? "") : "";
            if (validate.Length == 0) return;
            DisposeCaptchaView();
            CaptchaPanel.Visibility = Visibility.Collapsed;

            if (_captchaPurpose == "pwd")
            {
                PhoneMsg.Text = "正在登录…";
                var (rc, pm, pc) = await BiliLogin.PwdLoginAsync(_pwdUser, _pwdPass, _captchaToken, challenge, validate, seccode);
                PwdLoginBtn.IsEnabled = true;
                if (rc == 0 && pc.Count > 0) { PhoneMsg.Text = "登录成功"; await OnLoginSuccessAsync(pc); }
                else PhoneMsg.Text = "登录失败：" + (pm.Length > 0 ? pm : ("错误码 " + rc));
                return;
            }

            PhoneMsg.Text = "正在发送验证码…";
            var (code, m, key) = await BiliLogin.SendSmsAsync(_smsTel, _captchaToken, challenge, validate, seccode);
            if (code == 0)
            {
                _captchaKey = key;
                PhoneMsg.Foreground = _accentBrush;
                PhoneMsg.Text = "验证码已发送，请查收短信（约 60 秒内有效）";
                StartSmsCountdown();
                try { SmsBox.Focus(FocusState.Programmatic); } catch { }
            }
            else
            {
                PhoneMsg.Foreground = (Brush)Application.Current.Resources["TextDim"];
                PhoneMsg.Text = "发送失败：" + (m.Length > 0 ? m : ("错误码 " + code));
                SendSmsBtn.IsEnabled = true;
            }
        }
        catch { SendSmsBtn.IsEnabled = true; }
    }

    private DispatcherQueueTimer? _smsTimer;
    private int _smsLeft;
    private void StartSmsCountdown()
    {
        _smsLeft = 60;
        SendSmsBtn.IsEnabled = false;
        SendSmsBtn.Content = $"重新发送（{_smsLeft}s）";
        if (_smsTimer == null)
        {
            _smsTimer = DispatcherQueue.CreateTimer();
            _smsTimer.Interval = TimeSpan.FromSeconds(1);
            _smsTimer.IsRepeating = true;
            _smsTimer.Tick += (_, _) =>
            {
                _smsLeft--;
                if (_smsLeft <= 0)
                {
                    _smsTimer!.Stop();
                    SendSmsBtn.Content = "获取验证码";
                    SendSmsBtn.IsEnabled = true;
                }
                else SendSmsBtn.Content = $"重新发送（{_smsLeft}s）";
            };
        }
        _smsTimer.Stop();
        _smsTimer.Start();
    }

    private void CancelCaptcha_Click(object sender, RoutedEventArgs e)
    {
        DisposeCaptchaView();
        CaptchaPanel.Visibility = Visibility.Collapsed;
        SendSmsBtn.IsEnabled = true;
    }

    private void CaptchaOverlay_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // 只有点在遮罩空白处才关闭；点在极验控件（CaptchaHost 及其子级）不关
        var src = e.OriginalSource as DependencyObject;
        if (src != null && IsWithinCaptchaHost(src)) return;
        CancelCaptcha_Click(sender, e);
    }

    private bool IsWithinCaptchaHost(DependencyObject d)
    {
        var guard = 0;
        while (d != null && guard++ < 30)
        {
            if (ReferenceEquals(d, CaptchaHost)) return true;
            d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void DisposeCaptchaView()
    {
        try { _captchaView?.Close(); } catch { }
        try { if (_captchaView != null) CaptchaHost.Children.Remove(_captchaView); } catch { }
        _captchaView = null;
    }

    private async void SmsLogin_Click(object sender, RoutedEventArgs e)
    {
        var tel = PhoneBox.Text.Trim();
        var code = SmsBox.Text.Trim();
        if (!Regex.IsMatch(tel, @"^1\d{10}$")) { PhoneMsg.Text = "请输入正确的 11 位手机号"; return; }
        if (code.Length == 0) { PhoneMsg.Text = "请输入短信验证码"; return; }
        PhoneMsg.Text = "正在登录…";
        try
        {
            var (rc, msg, cookies) = await BiliLogin.LoginSmsAsync(tel, code, _captchaKey);
            if (rc == 0 && cookies.Count > 0) { PhoneMsg.Text = "登录成功"; await OnLoginSuccessAsync(cookies); }
            else PhoneMsg.Text = "登录失败：" + (msg.Length > 0 ? msg : ("错误码 " + rc));
        }
        catch (Exception ex) { PhoneMsg.Text = "登录失败：" + ex.Message; }
    }

    private async void PwdLogin_Click(object sender, RoutedEventArgs e)
    {
        var user = UserBox.Text.Trim();
        var pwd = PwdBox.Password;
        if (user.Length == 0 || pwd.Length == 0) { PhoneMsg.Text = "请输入账号和密码"; return; }
        PwdLoginBtn.IsEnabled = false;
        PhoneMsg.Text = "正在获取安全验证…";
        try
        {
            var (token, gt, challenge) = await BiliLogin.GetCaptchaAsync();
            _captchaPurpose = "pwd"; _pwdUser = user; _pwdPass = pwd; _captchaToken = token;
            await ShowCaptchaAsync(gt, challenge);
        }
        catch (Exception ex) { PhoneMsg.Text = "登录失败：" + ex.Message; PwdLoginBtn.IsEnabled = true; }
    }

    private async Task StartQrLoginAsync()
    {
        _qrTimer?.Stop();
        _qrDone = false;
        QrImage.Source = null;
        try { QrExpiredOverlay.Visibility = Visibility.Collapsed; QrSuccessOverlay.Visibility = Visibility.Collapsed; } catch { }
        QrStatus.Text = "正在获取二维码…";
        try
        {
            var (url, key) = await BiliLogin.GenerateQrAsync();
            _qrKey = key;
            QrImage.Source = await MakeQrAsync(url);
            QrStatus.Text = "请用哔哩哔哩 App 扫码，并在手机上确认";
            if (_qrTimer == null)
            {
                _qrTimer = DispatcherQueue.CreateTimer();
                _qrTimer.Interval = TimeSpan.FromSeconds(2);
                _qrTimer.IsRepeating = true;
                _qrTimer.Tick += async (_, _) => await PollQrAsync();
            }
            _qrTimer.Start();
        }
        catch (Exception ex)
        {
            QrStatus.Text = "获取二维码失败：" + ex.Message + "\n可点「刷新二维码」或改用网页登录。";
        }
    }

    private static async Task<BitmapImage> MakeQrAsync(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10, false);
        var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await ras.WriteAsync(png.AsBuffer());
        ras.Seek(0);
        var bi = new BitmapImage();
        await bi.SetSourceAsync(ras);
        return bi;
    }

    private bool _qrDone;
    private async Task PollQrAsync()
    {
        if (_qrKey == null || _qrDone) return;
        try
        {
            var (code, cookies) = await BiliLogin.PollQrAsync(_qrKey);
            switch (code)
            {
                case 86101: break;   // 等待扫码：不显示提示
                case 86090: QrStatus.Text = "已扫码，请在手机上点击确认"; break;
                case 86038:
                    _qrTimer?.Stop();
                    QrStatus.Text = "二维码已过期，点二维码刷新";
                    QrExpiredOverlay.Visibility = Visibility.Visible;
                    break;
                case 0:
                    _qrDone = true;
                    _qrTimer?.Stop();
                    QrStatus.Text = "登录成功！正在载入…";
                    ShowQrSuccess();
                    await Task.Delay(500);
                    await OnLoginSuccessAsync(cookies);
                    break;
            }
        }
        catch { /* 网络抖动忽略，下次再试 */ }
    }

    private async Task OnLoginSuccessAsync(Dictionary<string, string> cookies)
    {
        var path = BiliLogin.SaveCookiesFile(cookies, CfgDir);
        _cfg["cookie"] = path;
        SaveCfg();
        SetCookie.Text = path;
        try
        {
            var (uname, face) = await BiliLogin.GetAccountAsync(path);
            if (!string.IsNullOrEmpty(face)) SetAvatar(face);
            LoginState.Text = string.IsNullOrEmpty(uname) ? "已登录" : "已登录：" + uname;
        }
        catch { LoginState.Text = "已登录"; }
        ShowToast("登录成功，已载入观看历史", "ok");
        DisposeLoginView();
        SetActiveNav("history");
        ShowOnly(HistoryPage);
        await LoadHistoryAsync(cookies);
    }

    private async Task InitAccountAsync()
    {
        var ck = Cfg("cookie");
        if (string.IsNullOrEmpty(ck) || !File.Exists(ck)) { SetAvatar(null); return; }
        try
        {
            var (uname, face) = await BiliLogin.GetAccountAsync(ck);
            if (!string.IsNullOrEmpty(face)) SetAvatar(face);
            RunOnUi(() => LoginState.Text = string.IsNullOrEmpty(uname) ? "已登录" : "已登录：" + uname);
        }
        catch { }
    }

    private void SetAvatar(string? faceUrl)
    {
        RunOnUi(() =>
        {
            if (string.IsNullOrEmpty(faceUrl))
            {
                AvatarBrush.ImageSource = null;
                AvatarDefault.Visibility = Visibility.Visible;
            }
            else
            {
                try
                {
                    // 走磁盘缓存：首次下载后本地留存，之后每次打开直接读本地，不再联网重下
                    var src = CoverCache.Get(faceUrl, 80);
                    if (src != null)
                    {
                        AvatarBrush.ImageSource = src;
                        AvatarDefault.Visibility = Visibility.Collapsed;
                    }
                }
                catch { }
            }
        });
    }

    private void Avatar_Click(object sender, RoutedEventArgs e)
    {
        var ck = Cfg("cookie");
        var loggedIn = !string.IsNullOrEmpty(ck) && File.Exists(ck);
        if (!loggedIn) { Login_Click(sender, e); return; }

        var menu = new MenuFlyout();
        var logout = new MenuFlyoutItem { Text = "退出登录" };
        logout.Click += (_, _) => Logout();
        menu.Items.Add(logout);
        menu.ShowAt((FrameworkElement)sender);
    }

    private void Logout()
    {
        try
        {
            var ck = Cfg("cookie");
            if (!string.IsNullOrEmpty(ck) && File.Exists(ck)) File.Delete(ck);
        }
        catch { }
        _cfg.Remove("cookie");
        SaveCfg();
        SetCookie.Text = "";
        LoginState.Text = "未登录（点左上角头像登录）";
        SetAvatar(null);
        ShowToast("已退出登录", "info");
    }

    private void QrRefresh_Click(object sender, RoutedEventArgs e) => _ = StartQrLoginAsync();

    private async void WebLogin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LoginCard.Visibility = Visibility.Collapsed;
            WebPanel.Visibility = Visibility.Visible;
            _qrTimer?.Stop();
            LoginMsg.Text = "在下方网页中登录，成功后会自动保存。";
            if (_loginTimer == null)
            {
                _loginTimer = DispatcherQueue.CreateTimer();
                _loginTimer.Interval = TimeSpan.FromSeconds(1.5);
                _loginTimer.IsRepeating = true;
                _loginTimer.Tick += async (_, _) => await TryCaptureLoginAsync();
            }
            var lv = EnsureLoginView();
            await lv.EnsureCoreWebView2Async();
            lv.CoreWebView2.Navigate("https://passport.bilibili.com/login");
            _loginTimer.Start();
        }
        catch (Exception ex) { LoginMsg.Text = "无法打开登录页：" + ex.Message; }
    }

    private void WebBack_Click(object sender, RoutedEventArgs e)
    {
        DisposeLoginView();
        WebPanel.Visibility = Visibility.Collapsed;
        LoginCard.Visibility = Visibility.Visible;
        _ = StartQrLoginAsync();
    }

    private WebView2 EnsureLoginView()
    {
        if (_loginView == null)
        {
            _loginView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            LoginHost.Children.Add(_loginView);
        }
        return _loginView;
    }

    // WebView2 常驻会拖慢窗口拖动/缩放，离开登录页就销毁
    private void DisposeLoginView()
    {
        try { _loginView?.Close(); } catch { }
        try { if (_loginView != null) LoginHost.Children.Remove(_loginView); } catch { }
        _loginView = null;
        try { _loginTimer?.Stop(); } catch { }
    }

    private void LoginCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _loginTimer?.Stop(); } catch { }
        try { _qrTimer?.Stop(); } catch { }
        DisposeLoginView();
        SetActiveNav("history");
        ShowOnly(HistoryPage);
    }

    private bool _capturing;
    private async Task TryCaptureLoginAsync()
    {
        var lv = _loginView;
        if (_capturing || lv?.CoreWebView2 == null) return;
        try
        {
            var cookies = await lv.CoreWebView2.CookieManager.GetCookiesAsync("https://www.bilibili.com");
            var map = new Dictionary<string, string>();
            foreach (var c in cookies) map[c.Name] = c.Value;
            if (!map.ContainsKey("SESSDATA")) return;

            _capturing = true;
            _loginTimer?.Stop();
            LoginMsg.Text = "检测到登录，正在获取账号信息…";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Netscape HTTP Cookie File");
            foreach (var c in cookies)
            {
                var domain = string.IsNullOrEmpty(c.Domain) ? ".bilibili.com" : c.Domain;
                var includeSub = domain.StartsWith(".") ? "TRUE" : "FALSE";
                long exp = 0; try { exp = (long)c.Expires; } catch { }
                sb.AppendLine($"{domain}\t{includeSub}\t/\t{(c.IsSecure ? "TRUE" : "FALSE")}\t{exp}\t{c.Name}\t{c.Value}");
            }
            Directory.CreateDirectory(CfgDir);
            var cookiePath = Path.Combine(CfgDir, "cookies.txt");
            File.WriteAllText(cookiePath, sb.ToString());
            _cfg["cookie"] = cookiePath;
            SaveCfg();
            SetCookie.Text = cookiePath;
            try
            {
                var (un, fc) = await BiliLogin.GetAccountAsync(cookiePath);
                if (!string.IsNullOrEmpty(fc)) SetAvatar(fc);
                LoginState.Text = string.IsNullOrEmpty(un) ? "已登录" : "已登录：" + un;
            }
            catch { LoginState.Text = "已登录"; }
            ShowToast("登录成功，已载入观看历史", "ok");
            DisposeLoginView();
            ShowOnly(HistoryPage);
            await LoadHistoryAsync(map);
        }
        catch { }
        finally { _capturing = false; }
    }

    private static HttpClient? _histHttp;
    private static string _histProxy = "\u0000";
    private static HttpClient HistClient(string? proxy)
    {
        var p = proxy ?? "";
        if (_histHttp != null && _histProxy == p) return _histHttp;
        var h = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(p)) { try { h.Proxy = new System.Net.WebProxy(p); h.UseProxy = true; } catch { } }
        // 未配置代理时沿用系统代理（与登录等请求一致），不再强制绕过
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        _histHttp = c; _histProxy = p;
        return c;
    }

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        var ck = Cfg("cookie");
        if (string.IsNullOrEmpty(ck) || !File.Exists(ck)) { ShowToast("请先登录", "info"); return; }
        _historyLoaded = false;
        LoginState.Text = "正在载入观看历史…";
        await LoadHistoryAsync(ReadCookieFile(ck));
    }

    private long _histMax, _histViewAt;
    private string _histBusiness = "";

    private static List<HistoryItem> BuildHistoryItems(JsonElement arr)
    {
        var list = new List<HistoryItem>();
        foreach (var it in arr.EnumerateArray())
        {
            var title = it.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
            if (it.TryGetProperty("long_title", out var lt) && lt.GetString() is string lts && lts.Length > 0)
                title += "  " + lts;
            var author = it.TryGetProperty("author_name", out var a) ? (a.GetString() ?? "") : "";
            var bvid = ""; var business = ""; long oid = 0, epid = 0;
            if (it.TryGetProperty("history", out var hh))
            {
                if (hh.TryGetProperty("bvid", out var b) && b.ValueKind == JsonValueKind.String) bvid = b.GetString() ?? "";
                if (hh.TryGetProperty("business", out var bs) && bs.ValueKind == JsonValueKind.String) business = bs.GetString() ?? "";
                if (hh.TryGetProperty("oid", out var od) && od.ValueKind == JsonValueKind.Number) oid = od.GetInt64();
                if (hh.TryGetProperty("epid", out var ep) && ep.ValueKind == JsonValueKind.Number) epid = ep.GetInt64();
            }
            if (bvid.Length == 0 && it.TryGetProperty("bvid", out var b2) && b2.ValueKind == JsonValueKind.String)
                bvid = b2.GetString() ?? "";

            // 番剧（PGC）：用 epid（不是 oid，oid 是该集视频的 aid）
            string url = "", tag = "";
            if (business == "pgc" && epid > 0) { url = "https://www.bilibili.com/bangumi/play/ep" + epid; tag = "番剧"; }
            else if (bvid.Length > 0) url = bvid;
            else if (business == "pgc" && oid > 0) { url = "https://www.bilibili.com/bangumi/play/ep" + oid; tag = "番剧"; }
            if (url.Length == 0) continue;

            var u = "";
            if (it.TryGetProperty("cover", out var cv) && cv.GetString() is string cs && cs.Length > 0)
                u = cs.StartsWith("//") ? "https:" + cs
                  : cs.StartsWith("http://") ? "https://" + cs.Substring(7) : cs;
            list.Add(new HistoryItem { Title = title, Sub = (tag.Length > 0 ? "[" + tag + "] " : "") + author, Bvid = bvid, Url = url, CoverUrl = u, IsPgc = (tag == "番剧") });
        }
        return list;
    }

    private async Task FetchHistoryPageAsync(Dictionary<string, string> cookies, bool append)
    {
        var url = "https://api.bilibili.com/x/web-interface/history/cursor?ps=30";
        if (append && _histMax != 0)
            url += "&max=" + _histMax + "&view_at=" + _histViewAt + "&business=" + Uri.EscapeDataString(_histBusiness);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies.Select(kv => kv.Key + "=" + kv.Value)));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var resp = await HistClient(Cfg("proxy")).SendAsync(req, cts.Token);
        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return;
        var items = data.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? BuildHistoryItems(arr) : new List<HistoryItem>();
        if (!append)
        {
            _historyAll.Clear();
            _historyAll.AddRange(items);
        }
        else
        {
            var have = new HashSet<string>(_historyAll.Select(x => x.Url));
            foreach (var it in items) if (it.Url.Length > 0 && have.Add(it.Url)) _historyAll.Add(it);
        }
        _histHasMore = items.Count > 0;
        if (data.TryGetProperty("cursor", out var cur))
        {
            _histMax = cur.TryGetProperty("max", out var mx) && mx.ValueKind == JsonValueKind.Number ? mx.GetInt64() : 0;
            _histViewAt = cur.TryGetProperty("view_at", out var va) && va.ValueKind == JsonValueKind.Number ? va.GetInt64() : 0;
            _histBusiness = cur.TryGetProperty("business", out var bs) && bs.ValueKind == JsonValueKind.String ? (bs.GetString() ?? "") : "";
            if (_histMax == 0) _histHasMore = false;
        }
        else _histHasMore = false;
    }

    private async Task LoadHistoryAsync(Dictionary<string, string> cookies)
    {
        _loadingHistory = true;
        try
        {
            _histMax = 0; _histViewAt = 0; _histBusiness = "";
            await FetchHistoryPageAsync(cookies, false);
            // 首屏尽量凑够约 50 条（单页上限 30，必要时再取一页；失败不影响首页显示）
            try
            {
                for (int i = 0; i < 2 && _histHasMore && _historyAll.Count < 50; i++)
                    await FetchHistoryPageAsync(cookies, true);
            }
            catch { }
            ApplyHistoryFilter();
            _historyLoaded = true;
            _lastHistoryLoad = DateTime.UtcNow;
            LoginState.Text = _historyAll.Count == 0
                ? "已登录，但观看历史为空"
                : $"已登录 · 视频 {_historyAll.Count(x => !x.IsPgc)} · 番剧 {_historyAll.Count(x => x.IsPgc)}";
            UpdateHistoryEmpty();
            try { HistMoreBtn.Visibility = _histHasMore ? Visibility.Visible : Visibility.Collapsed; } catch { }
        }
        catch (Exception ex) { LoginState.Text = "读取历史失败：" + ex.GetBaseException().Message; }
        finally { _loadingHistory = false; }
    }

    private async void LoadMoreHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingHistory || !_histHasMore) return;
        var ck = Cfg("cookie");
        if (string.IsNullOrEmpty(ck) || !File.Exists(ck)) return;
        _loadingHistory = true;
        HistMoreBtn.IsEnabled = false;
        try
        {
            await FetchHistoryPageAsync(ReadCookieFile(ck), true);
            ApplyHistoryFilter();
            LoginState.Text = _historyAll.Count == 0
                ? "已登录，但观看历史为空"
                : $"已登录 · 视频 {_historyAll.Count(x => !x.IsPgc)} · 番剧 {_historyAll.Count(x => x.IsPgc)}";
            HistMoreBtn.Visibility = _histHasMore ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
        finally { _loadingHistory = false; HistMoreBtn.IsEnabled = true; }
    }

    private void UpdateHistoryEmpty()
    {
        var empty = HistoryList.ItemsSource == null || HistoryList.Items.Count == 0;
        HistEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryItem it && it.Url.Length > 0)
        {
            UrlBox.Text = it.Url;
            Parse_Click(sender, new RoutedEventArgs());
        }
    }

    private void HistFilter_Click(object sender, RoutedEventArgs e)
    {
        _historyFilter = (sender as FrameworkElement)?.Tag as string ?? "all";
        ApplyHistoryFilter();
    }

    private void ReleaseHistoryCovers()
    {
        try { foreach (var it in _historyAll) it.ReleaseCover(); } catch { }
    }

    private void ApplyHistoryFilter()
    {
        HistoryList.ItemsSource = null;
        HistoryList.ItemsSource = _historyFilter switch
        {
            "video" => _historyAll.Where(x => !x.IsPgc).ToList(),
            "pgc" => _historyAll.Where(x => x.IsPgc).ToList(),
            _ => _historyAll,
        };
        var clear = new SolidColorBrush(Colors.Transparent);
        var normal = (Brush)Application.Current.Resources["TextPrimary"];
        try
        {
            void Badge(Border bd, TextBlock tx, int n, string tag)
            {
                tx.Text = n.ToString();
                bd.Tag = n;
                if (_hoveredFilter == tag && n > 0) { bd.Visibility = Visibility.Visible; bd.Opacity = 1; }
                else { bd.Visibility = Visibility.Collapsed; bd.Opacity = 0; }
            }
            Badge(HistBadgeAll, HistBadgeAllText, _historyAll.Count, "all");
            Badge(HistBadgeVideo, HistBadgeVideoText, _historyAll.Count(x => !x.IsPgc), "video");
            Badge(HistBadgePgc, HistBadgePgcText, _historyAll.Count(x => x.IsPgc), "pgc");
        }
        catch { }
        void Mark(Button b, string tag)
        {
            var on = _historyFilter == tag;
            b.Background = clear;
            b.Foreground = on ? (Brush)_accentBrush : normal;
            b.BorderThickness = new Thickness(0);
            if (on) MoveHistUnderline(b);
        }
        try { Mark(HistTabAll, "all"); Mark(HistTabVideo, "video"); Mark(HistTabPgc, "pgc"); } catch { }
    }

    private string? _hoveredFilter;

    private void HistTab_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid g) return;
        _hoveredFilter = g.Children.OfType<Button>().FirstOrDefault()?.Tag as string;
        if (g.Children.OfType<Border>().FirstOrDefault() is Border bd && bd.Tag is int n && n > 0)
            AnimateBadge(bd, true);
    }

    private void HistTab_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid g) return;
        var tag = g.Children.OfType<Button>().FirstOrDefault()?.Tag as string;
        if (_hoveredFilter == tag) _hoveredFilter = null;
        if (g.Children.OfType<Border>().FirstOrDefault() is Border bd)
            AnimateBadge(bd, false);
    }

    private static void AnimateBadge(Border bd, bool show)
    {
        try
        {
            bd.RenderTransform = null;
            var sb = new Storyboard();
            if (show)
            {
                bd.Visibility = Visibility.Visible;
                bd.Opacity = 1;
                bd.Width = 10; bd.Height = 10;
                AddAnim(sb, bd, "Width", 16, 200, new CubicEase { EasingMode = EasingMode.EaseOut });
                AddAnim(sb, bd, "Height", 16, 200, new CubicEase { EasingMode = EasingMode.EaseOut });
            }
            else
            {
                AddAnim(sb, bd, "Width", 10, 110, new CubicEase { EasingMode = EasingMode.EaseIn });
                AddAnim(sb, bd, "Height", 10, 110, new CubicEase { EasingMode = EasingMode.EaseIn });
                sb.Completed += (_, _) => { try { bd.Visibility = Visibility.Collapsed; } catch { } };
            }
            sb.Begin();
        }
        catch { }
    }

    private static void AddAnim(Storyboard sb, DependencyObject target, string prop, double to, double ms, EasingFunctionBase ease)
    {
        var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease, EnableDependentAnimation = true };
        Storyboard.SetTarget(a, target); Storyboard.SetTargetProperty(a, prop);
        sb.Children.Add(a);
    }

    private void MoveHistUnderline(Button b)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (b.ActualWidth <= 0) return;
                var left = b.TransformToVisual(HistTabs).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
                HistUnderline.Width = Math.Max(10, b.ActualWidth - 8);   // 左右各内缩 4，避免戳出圆角
                var a = new DoubleAnimation
                {
                    To = left + 4, Duration = TimeSpan.FromMilliseconds(200),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    EnableDependentAnimation = true,
                };
                Storyboard.SetTarget(a, HistUnderlineTf); Storyboard.SetTargetProperty(a, "X");
                var sb = new Storyboard();
                sb.Children.Add(a);
                sb.Begin();
            }
            catch { }
        });
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Cfg("dir", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BiliGet"));
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { }
    }

    /* ---------------- 缓存统计 / 清理 ---------------- */
    private static bool IsTempFile(string name)
    {
        var n = name.ToLowerInvariant();
        return n.EndsWith(".part") || n.EndsWith(".aria2") || n.EndsWith(".ytdl") || n.EndsWith(".temp")
            || Regex.IsMatch(n, @"\.f\d+\.(mp4|m4a|webm|flv|mkv)$");
    }

    private static long CleanTemp(string dir)
    {
        long freed = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (!IsTempFile(f.Name)) continue;
                try { var len = f.Length; f.Delete(); freed += len; } catch { }
            }
        }
        catch { }
        return freed;
    }

    private static (long bytes, int files) TempSize(string dir)
    {
        long total = 0; int count = 0;
        try
        {
            if (!Directory.Exists(dir)) return (0, 0);
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                if (IsTempFile(f.Name)) { total += f.Length; count++; }
        }
        catch { }
        return (total, count);
    }

    private static string Human(long b)
    {
        if (b >= 1L << 30) return $"{b / 1024.0 / 1024 / 1024:0.0} GB";
        if (b >= 1L << 20) return $"{b / 1024.0 / 1024:0.0} MB";
        if (b >= 1024) return $"{b / 1024.0:0.0} KB";
        return b + " B";
    }

    private static string Mb(long b) => $"{b / 1024.0 / 1024:0.0} MB";

    private static (long bytes, int files) DirSize(string dir)
    {
        long total = 0; int count = 0;
        try
        {
            if (!Directory.Exists(dir)) return (0, 0);
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            { total += f.Length; count++; }
        }
        catch { }
        return (total, count);
    }

    private static string WebCacheDir => Path.Combine(CfgDir, "WebView2");

    private async Task RefreshCacheInfoAsync()
    {
        var dir = Cfg("dir", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BiliGet"));
        var t = await Task.Run(() => TempSize(dir));
        var w = await Task.Run(() => DirSize(WebCacheDir));
        var c = await Task.Run(() => CoverCache.Size());
        RunOnUi(() =>
        {
            TempSizeText.Text = t.files == 0 ? "无" : $"{t.files} 个，占用 {Mb(t.bytes)}";
            WebSizeText.Text = w.files == 0 ? "无" : $"{w.files} 个文件，占用 {Mb(w.bytes)}";
            CoverSizeText.Text = c.files == 0 ? "无" : $"{c.files} 张，占用 {Mb(c.bytes)}";
        });
    }

    private async void CleanCover_Click(object sender, RoutedEventArgs e)
    {
        var freed = await Task.Run(() => CoverCache.Clean());
        await RefreshCacheInfoAsync();
        ShowToast($"已清理封面缓存，释放约 {Mb(freed)}", "ok");
    }

    private async void CleanTemp_Click(object sender, RoutedEventArgs e)
    {
        if (_activeDownloads > 0) { await Dialog("还有下载正在进行，请等下载完成后再清理。"); return; }
        var dir = Cfg("dir", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BiliGet"));
        var freed = await Task.Run(() => CleanTemp(dir));
        await RefreshCacheInfoAsync();
        ShowToast($"已清理临时文件，释放约 {Mb(freed)}", "ok");
    }

    private async void CleanWeb_Click(object sender, RoutedEventArgs e)
    {
        long freed = 0;
        try
        {
            if (Directory.Exists(WebCacheDir)) { freed = await Task.Run(() => DirSize(WebCacheDir).bytes); Directory.Delete(WebCacheDir, true); }
        }
        catch { }
        await RefreshCacheInfoAsync();
        ShowToast($"已清理网页缓存，释放约 {Mb(freed)}", "ok");
    }

    /* ---------------- 应用更新 ---------------- */
    private UpdateInfo? _update;
    private static readonly SolidColorBrush DimBrush = new(Windows.UI.Color.FromArgb(255, 0x9A, 0xA0, 0xA6));

    private void SetChip(string text, Brush brush)
    {
        RunOnUi(() => { UpdChipText.Text = text; UpdChipText.Foreground = brush; });
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdate.IsEnabled = false;
        SetChip("检查中…", DimBrush);
        RunOnUi(() => UpdLine.Text = "正在检查更新…");
        try
        {
            var info = await Updater.CheckAsync(Cfg("proxy"));
            _update = info;
            var newest = (info != null && !string.IsNullOrEmpty(info.Version)) ? info.Version : Updater.CurrentVersion;
            var hasNew = info != null && !string.IsNullOrEmpty(info.Version) && Updater.IsNewer(info.Version, Updater.CurrentVersion);
            RunOnUi(() =>
            {
                if (hasNew)
                {
                    SetChip("发现新版本", _accentBrush);
                    UpdLine.Text = $"当前版本 v{Updater.CurrentVersion} · 发现新版本 v{info!.Version}"
                                 + (info.Notes.Length > 0 ? "：" + info.Notes : "");
                    BtnUpdateNow.Visibility = Visibility.Visible;
                }
                else
                {
                    SetChip("已是最新", OkBrush);
                    UpdLine.Text = $"当前版本 v{Updater.CurrentVersion}";
                    BtnUpdateNow.Visibility = Visibility.Collapsed;
                }
            });
        }
        catch (Exception)
        {
            SetChip("检查失败", ErrBrush);
            RunOnUi(() => UpdLine.Text = "检查更新失败：若无法访问官网，请在「账号与网络」填写代理后重试。");
        }
        finally { BtnCheckUpdate.IsEnabled = true; }
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_update == null || string.IsNullOrEmpty(_update.Url)) return;
        BtnUpdateNow.IsEnabled = false;
        BtnCheckUpdate.IsEnabled = false;
        RunOnUi(() => { UpdBar.Visibility = Visibility.Visible; UpdBar.Value = 0; UpdLine.Text = "正在下载更新…"; });
        try
        {
            var path = await Updater.DownloadAsync(_update.Url, _update.Sha256, Cfg("proxy"), p => RunOnUi(() => UpdBar.Value = p));
            SaveUpdateMarker(_update);
            RunOnUi(() => UpdLine.Text = "下载完成，正在安装…");
            ShowToast("更新已下载，正在安装…", "ok");
            Updater.RunInstaller(path);
            await Task.Delay(800);
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            RunOnUi(() => { UpdBar.Visibility = Visibility.Collapsed; UpdLine.Text = "下载更新失败：" + ex.Message; });
            ShowToast("下载更新失败", "err");
            BtnUpdateNow.IsEnabled = true;
            BtnCheckUpdate.IsEnabled = true;
        }
    }

    private async void ShowChangelog_Click(object sender, RoutedEventArgs e)
    {
        if (_update == null || _update.Changelog.Length == 0)
        {
            try { _update = await Updater.CheckAsync(Cfg("proxy")); } catch { }
        }
        if (_update == null || _update.Changelog.Length == 0) { OpenDownloadPage(); return; }
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = "v" + _update.Version + (_update.Notes.Length > 0 ? "：" + _update.Notes : ""), TextWrapping = TextWrapping.Wrap });
        foreach (var line in _update.Changelog)
            body.Children.Add(new TextBlock { Text = "· " + line, TextWrapping = TextWrapping.Wrap });
        var dlg = new ContentDialog
        {
            Title = "更新说明",
            Content = new ScrollViewer { Content = body, MaxHeight = 420 },
            CloseButtonText = "关闭",
            XamlRoot = Root.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    private void OpenDownloadPage()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Updater.DownloadPage) { UseShellExecute = true }); } catch { }
    }

    private void OpenDownloadPage_Click(object sender, RoutedEventArgs e) => OpenDownloadPage();

    private static string UpdateMarkerPath => Path.Combine(CfgDir, "updated.json");

    private static void SaveUpdateMarker(UpdateInfo info)
    {
        try
        {
            Directory.CreateDirectory(CfgDir);
            File.WriteAllText(UpdateMarkerPath,
                JsonSerializer.Serialize(new { info.Version, info.Notes, info.Changelog }));
        }
        catch { }
    }

    private async Task CheckPostUpdateAsync()
    {
        try
        {
            if (!File.Exists(UpdateMarkerPath)) return;
            var txt = File.ReadAllText(UpdateMarkerPath);
            File.Delete(UpdateMarkerPath);
            using var doc = JsonDocument.Parse(txt);
            var r = doc.RootElement;
            var ver = r.TryGetProperty("Version", out var v) ? (v.GetString() ?? "") : "";
            ShowToast("已更新到 v" + (ver.Length > 0 ? ver : Updater.CurrentVersion), "ok");
            var body = new StackPanel { Spacing = 6 };
            body.Children.Add(new TextBlock { Text = "更新完成，本次更新内容：", TextWrapping = TextWrapping.Wrap });
            if (r.TryGetProperty("Changelog", out var cl) && cl.ValueKind == JsonValueKind.Array)
                foreach (var it in cl.EnumerateArray())
                    if (it.GetString() is string line) body.Children.Add(new TextBlock { Text = "· " + line, TextWrapping = TextWrapping.Wrap });
            var dlg = new ContentDialog
            {
                Title = "更新完成",
                Content = new ScrollViewer { Content = body, MaxHeight = 420 },
                CloseButtonText = "好的",
                XamlRoot = Root.XamlRoot,
            };
            await dlg.ShowAsync();
        }
        catch { }
    }

    private async Task AutoCheckUpdateAsync()
    {
        if (Cfg("autocheck", "0") != "1") return;
        await Task.Delay(2500);
        try
        {
            var info = await Updater.CheckAsync(Cfg("proxy"));
            if (info != null && !string.IsNullOrEmpty(info.Version) && Updater.IsNewer(info.Version, Updater.CurrentVersion))
            {
                _update = info;
                RunOnUi(() =>
                {
                    SetChip("发现新版本", _accentBrush);
                    UpdLine.Text = $"当前版本 v{Updater.CurrentVersion} · 发现新版本 v{info.Version}，可在「应用更新」里下载。";
                    BtnUpdateNow.Visibility = Visibility.Visible;
                });
                ShowToast($"发现新版本 v{info.Version}", "info", () =>
                {
                    SetActiveNav("settings");
                    ShowOnly(SettingsPage);
                    try { UpdLine.StartBringIntoView(); } catch { }
                });
            }
        }
        catch { }
    }

    /* ---------------- Toast ---------------- */
    
    private static readonly SolidColorBrush OkBrush = new(Windows.UI.Color.FromArgb(255, 0x2E, 0xC4, 0x6B));
    private static readonly SolidColorBrush ErrBrush = new(Windows.UI.Color.FromArgb(255, 0xE2, 0x3B, 0x2E));

    private void ShowToast(string msg, string kind = "info", Action? onClick = null)
    {
        RunOnUi(() =>
        {
            _toastAction = onClick;
            ToolTipService.SetToolTip(Toast, onClick != null ? "点击前往应用更新" : "点击关闭");
            ToastText.Text = msg;
            if (kind == "ok") { ToastIcon.Glyph = "\uE73E"; ToastIcon.Foreground = OkBrush; Toast.BorderBrush = OkBrush; }
            else if (kind == "err") { ToastIcon.Glyph = "\uEA39"; ToastIcon.Foreground = ErrBrush; Toast.BorderBrush = ErrBrush; }
            else { ToastIcon.Glyph = "\uE946"; ToastIcon.Foreground = _accentBrush; Toast.BorderBrush = _accentBrush; }

            Toast.Visibility = Visibility.Visible;
            Toast.Opacity = 0;
            ToastTf.Y = 14;
            var sb = new Storyboard();
            var op = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(180)) };
            Storyboard.SetTarget(op, Toast); Storyboard.SetTargetProperty(op, "Opacity");
            var ty = new DoubleAnimation
            {
                From = 14, To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(ty, ToastTf); Storyboard.SetTargetProperty(ty, "Y");
            sb.Children.Add(op); sb.Children.Add(ty);
            sb.Begin();

            if (_toastTimer == null)
            {
                _toastTimer = DispatcherQueue.CreateTimer();
                _toastTimer.Interval = TimeSpan.FromSeconds(3.6);
                _toastTimer.IsRepeating = false;
                _toastTimer.Tick += (_, _) => { _toastTimer!.Stop(); HideToast(); };
            }
            _toastTimer.Stop();
            _toastTimer.Start();
        });
    }

    private void HideToast()
    {
        RunOnUi(() =>
        {
            if (Toast.Visibility != Visibility.Visible) return;
            var sb = new Storyboard();
            var op = new DoubleAnimation { From = Toast.Opacity, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(150)) };
            Storyboard.SetTarget(op, Toast); Storyboard.SetTargetProperty(op, "Opacity");
            var ty = new DoubleAnimation { From = ToastTf.Y, To = 10, Duration = new Duration(TimeSpan.FromMilliseconds(150)) };
            Storyboard.SetTarget(ty, ToastTf); Storyboard.SetTargetProperty(ty, "Y");
            sb.Children.Add(op); sb.Children.Add(ty);
            sb.Completed += (_, _) => { Toast.Visibility = Visibility.Collapsed; };
            sb.Begin();
        });
    }

    private void Toast_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var action = _toastAction;
        _toastAction = null;
        HideToast();
        action?.Invoke();
    }

    /* ---------------- 修复 WinUI3 ComboBox 展开时鼠标变加载/缩放光标 (#8829) ---------------- */
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetCursor(IntPtr hCursor);
    private const int IDC_ARROW = 32512;
    private static readonly IntPtr _arrowCursor = LoadCursor(IntPtr.Zero, IDC_ARROW);

    private void HookComboCursors()
    {
        foreach (var cb in FindDescendants<ComboBox>(Root))
        {
            cb.DropDownOpened += (_, _) => StartArrowHold();
            cb.DropDownClosed += (_, _) => StopArrowHold();
            cb.PointerEntered += (s, _) =>
            {
                if (s is ComboBox c && !c.IsDropDownOpen) ResetArrow();
            };
        }
    }

    // 框架会在下拉打开后把光标设成“上一次的系统光标”，单次复位会被覆盖 → 打开期间持续压住
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _arrowTimer;
    private void StartArrowHold()
    {
        if (_arrowCursor == IntPtr.Zero) return;
        if (_arrowTimer == null)
        {
            _arrowTimer = DispatcherQueue.CreateTimer();
            _arrowTimer.Interval = TimeSpan.FromMilliseconds(50);
            _arrowTimer.IsRepeating = true;
            _arrowTimer.Tick += (_, _) => ResetArrow();
        }
        ResetArrow();
        _arrowTimer.Start();
    }

    private void StopArrowHold() => _arrowTimer?.Stop();

    private static void ResetArrow()
    {
        if (_arrowCursor != IntPtr.Zero) SetCursor(_arrowCursor);
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in FindDescendants<T>(child)) yield return d;
        }
    }

    private void ClearDownloads_Click(object sender, RoutedEventArgs e)
    {
        // 取消未完成的下载，并清理其未下完的临时文件
        foreach (var it in _downloads.Where(d => !d.Done).ToList())
        {
            try { it.Cts?.Cancel(); } catch { }
        }
        var files = DelFileOnRemove
            ? _downloads.Where(d => d.Done && d.FilePath.Length > 0).Select(d => d.FilePath).ToList()
            : new List<string>();
        _queue.Clear();
        _downloads.Clear();
        DlEmpty.Visibility = Visibility.Visible;
        SaveDownloads();

        var dir = Cfg("dir", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BiliGet"));
        _ = Task.Run(async () =>
        {
            await Task.Delay(1200);   // 等 yt-dlp/aria2c 进程退出、释放文件
            var freed = CleanTemp(dir);
            foreach (var f in files) freed += DeleteOutputFile(f);
            if (freed > 0) RunOnUi(() => ShowToast($"已清除下载记录，并清理文件约 {Mb(freed)}", "info"));
        });
        ShowToast(files.Count > 0 ? $"已清除下载记录，正在删除 {files.Count} 个文件" : "已清除下载记录", "info");
    }

    private void RunOnUi(Action a)
    {
        if (DispatcherQueue.HasThreadAccess) a();
        else DispatcherQueue.TryEnqueue(() => a());
    }

    /* ---------------- 文件选择 ---------------- */
    private void PickDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var task = picker.PickSingleFolderAsync();
            task.Completed = (t, _) =>
            {
                if (t.Status == Windows.Foundation.AsyncStatus.Completed && t.GetResults() != null)
                    SetDir.Text = t.GetResults().Path;
            };
        }
        catch { }
    }

    private void PickCookie_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".txt");
            var task = picker.PickSingleFileAsync();
            task.Completed = (t, _) =>
            {
                if (t.Status == Windows.Foundation.AsyncStatus.Completed && t.GetResults() != null)
                    SetCookie.Text = t.GetResults().Path;
            };
        }
        catch { }
    }
}
