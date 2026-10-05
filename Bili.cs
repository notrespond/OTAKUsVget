using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiliGet;

public static class BiliLogin
{
    private static readonly System.Net.CookieContainer Cookies = new();
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // 关键：启用 CookieContainer，使 发短信 / 登录 处于同一会话（验证码与 buvid 绑定）
        var h = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = Cookies,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        c.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
        return c;
    }

    public static async Task<(string url, string key)> GenerateQrAsync()
    {
        var json = await Http.GetStringAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var d)) throw new Exception("获取二维码失败");
        return (d.GetProperty("url").GetString() ?? "", d.GetProperty("qrcode_key").GetString() ?? "");
    }

    /// <summary>status: 0=成功, 86101=未扫码, 86090=已扫码待确认, 86038=已过期</summary>
    public static async Task<(int code, Dictionary<string, string> cookies)> PollQrAsync(string key)
    {
        using var resp = await Http.GetAsync(
            "https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + Uri.EscapeDataString(key));
        var json = await resp.Content.ReadAsStringAsync();

        var cookies = new Dictionary<string, string>();
        if (resp.Headers.TryGetValues("Set-Cookie", out var vals))
        {
            foreach (var v in vals)
            {
                var m = Regex.Match(v, @"^\s*([^=;]+)=([^;]*)");
                if (m.Success)
                {
                    var name = m.Groups[1].Value.Trim();
                    if (!name.StartsWith("#") && !cookies.ContainsKey(name)) cookies[name] = m.Groups[2].Value;
                }
            }
        }

        var code = -1;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var dd) && dd.TryGetProperty("code", out var cc))
                code = cc.GetInt32();
        }
        catch { }
        return (code, cookies);
    }

    public static string SaveCookiesFile(Dictionary<string, string> cookies, string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cookies.txt");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Netscape HTTP Cookie File");
        foreach (var kv in cookies)
            sb.AppendLine($".bilibili.com\tTRUE\t/\tFALSE\t0\t{kv.Key}\t{kv.Value}");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static string ReadCookieHeader(string path)
    {
        var pairs = new List<string>();
        try
        {
            foreach (var ln in File.ReadAllLines(path))
            {
                if (ln.Length == 0 || (ln[0] == '#' && !ln.StartsWith("#HttpOnly_"))) continue;
                var line = ln.StartsWith("#HttpOnly_") ? ln.Substring(10) : ln;
                var p = line.Split('\t');
                if (p.Length >= 7) pairs.Add(p[5] + "=" + p[6]);
            }
        }
        catch { }
        return string.Join("; ", pairs);
    }

    /// <summary>用本地 Cookie 获取账号昵称与头像。</summary>
    public static async Task<(string uname, string face)> GetAccountAsync(string cookieFile)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
        req.Headers.TryAddWithoutValidation("Cookie", ReadCookieHeader(cookieFile));
        using var resp = await Http.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var d)) return ("", "");
        var uname = d.TryGetProperty("uname", out var u) ? (u.GetString() ?? "") : "";
        var face = d.TryGetProperty("face", out var f) ? (f.GetString() ?? "") : "";
        if (face.StartsWith("http://")) face = "https://" + face.Substring(7);
        return (uname, face);
    }

    // ---------- 复用的 B站 API HttpClient（HTTP/2 + gzip/br + 连接池），按代理缓存 ----------
    private static readonly Dictionary<string, HttpClient> _apiClients = new();
    private static HttpClient ApiClient(string? proxy)
    {
        var key = proxy ?? "";
        lock (_apiClients)
        {
            if (_apiClients.TryGetValue(key, out var c)) return c;
            var h = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip
                                       | System.Net.DecompressionMethods.Deflate
                                       | System.Net.DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(6),
            };
            if (!string.IsNullOrWhiteSpace(proxy)) { try { h.Proxy = new System.Net.WebProxy(proxy!); h.UseProxy = true; } catch { } }
            var cl = new HttpClient(h)
            {
                Timeout = TimeSpan.FromSeconds(10),
                DefaultRequestVersion = System.Net.HttpVersion.Version20,
                DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrHigher,
            };
            _apiClients[key] = cl;
            return cl;
        }
    }
    private static HttpRequestMessage ApiReq(HttpMethod method, string url, string? cookieFile)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        if (!string.IsNullOrEmpty(cookieFile) && File.Exists(cookieFile))
        {
            var ck = ReadCookieHeader(cookieFile);
            if (ck.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", ck);
        }
        return req;
    }
    private static async Task<string> ApiGetStringAsync(string url, string? cookieFile, string? proxy)
    {
        using var resp = await ApiClient(proxy).SendAsync(ApiReq(HttpMethod.Get, url, cookieFile)).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    /// <summary>B站视频基础信息（用于「先出卡片、后台补清晰度」）。</summary>
    public sealed class BiliMeta
    {
        public string Title { get; set; } = "";
        public string Thumb { get; set; } = "";
        public string Bvid { get; set; } = "";
        public string Uploader { get; set; } = "";
        public double Duration { get; set; }
        public long Cid { get; set; }
        public bool IsSeries { get; set; }
        public List<(string title, string url, long cid, string cover)> Parts { get; set; } = new();
    }

    // ---------- wbi 签名（参考 bilibili-API-collect）----------
    private static readonly int[] MixinKeyEncTab =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40,
        61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11,
        36, 20, 34, 44, 52
    };
    private static string _mixinKey = "";
    private static DateTime _mixinKeyAt = DateTime.MinValue;

    private static string Md5Hex(string s)
    {
        var b = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(b.Length * 2);
        foreach (var x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    private static async Task<string> GetMixinKeyAsync(string? cookieFile, string? proxy)
    {
        if (_mixinKey.Length > 0 && (DateTime.UtcNow - _mixinKeyAt) < TimeSpan.FromHours(6)) return _mixinKey;
        var json = await ApiGetStringAsync("https://api.bilibili.com/x/web-interface/nav", cookieFile, proxy).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("wbi_img", out var wbi)) return "";
        static string Fn(JsonElement j, string k)
        {
            var u = j.TryGetProperty(k, out var v) ? (v.GetString() ?? "") : "";
            var name = u.Substring(u.LastIndexOf('/') + 1);
            var dot = name.IndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }
        var img = Fn(wbi, "img_url");
        var sub = Fn(wbi, "sub_url");
        var orig = img + sub;
        var chars = new char[32];
        for (int i = 0; i < 32; i++) chars[i] = i < orig.Length ? orig[MixinKeyEncTab[i]] : '0';
        _mixinKey = new string(chars);
        _mixinKeyAt = DateTime.UtcNow;
        return _mixinKey;
    }

    /// <summary>对参数做 wbi 签名，返回带 wts/w_rid 的查询串。</summary>
    private static async Task<string> SignQueryAsync(IDictionary<string, string> p, string? cookieFile, string? proxy)
    {
        var key = await GetMixinKeyAsync(cookieFile, proxy);
        p["wts"] = ((long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds).ToString();
        var filtered = p.Where(kv => kv.Value.Length > 0 && !kv.Value.Any(ch => "!'()*".IndexOf(ch) >= 0))
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value));
        var query = string.Join("&", filtered);
        var wrid = Md5Hex(query + key);
        return query + "&w_rid=" + wrid;
    }

    /// <summary>直接调 wbi playurl 取 DASH 视频轨（清晰度/编码/码率），用于秒出清晰度列表。</summary>
    public static Task<List<(int width, int height, string codecs, double fps, long bandwidth)>>
        GetPlayTracksAsync(string bvid, long cid, string? cookieFile, string? proxy)
        => GetPlayTracksInternalAsync(new Dictionary<string, string> { ["bvid"] = bvid, ["cid"] = cid.ToString() }, cookieFile, proxy);

    public static Task<List<(int width, int height, string codecs, double fps, long bandwidth)>>
        GetPlayTracksEpAsync(long epId, long cid, string? cookieFile, string? proxy)
        => GetPlayTracksInternalAsync(new Dictionary<string, string> { ["ep_id"] = epId.ToString(), ["cid"] = cid.ToString() }, cookieFile, proxy);

    private static async Task<List<(int width, int height, string codecs, double fps, long bandwidth)>>
        GetPlayTracksInternalAsync(Dictionary<string, string> idParams, string? cookieFile, string? proxy)
    {
        var res = new List<(int, int, string, double, long)>();
        try
        {
            var p = new Dictionary<string, string>(idParams)
            {
                ["qn"] = "127", ["fnver"] = "0", ["fnval"] = "4048", ["fourk"] = "1",
            };
            var q = await SignQueryAsync(p, cookieFile, proxy).ConfigureAwait(false);
            var json = await ApiGetStringAsync("https://api.bilibili.com/x/player/wbi/playurl?" + q, cookieFile, proxy).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return res;
            if (!data.TryGetProperty("dash", out var dash) || !dash.TryGetProperty("video", out var vids) || vids.ValueKind != JsonValueKind.Array)
                return res;
            foreach (var v in vids.EnumerateArray())
            {
                var w = v.TryGetProperty("width", out var wj) && wj.ValueKind == JsonValueKind.Number ? wj.GetInt32() : 0;
                var h = v.TryGetProperty("height", out var hj) && hj.ValueKind == JsonValueKind.Number ? hj.GetInt32() : 0;
                var codecs = v.TryGetProperty("codecs", out var cj) ? (cj.GetString() ?? "") : "";
                var bw = v.TryGetProperty("bandwidth", out var bj) && bj.ValueKind == JsonValueKind.Number ? bj.GetInt64() : 0;
                double fps = 0;
                if (v.TryGetProperty("frameRate", out var fr))
                {
                    if (fr.ValueKind == JsonValueKind.Number) fps = fr.GetDouble();
                    else if (fr.ValueKind == JsonValueKind.String)
                    {
                        var parts = (fr.GetString() ?? "").Split('/');
                        if (parts.Length == 2 && double.TryParse(parts[0], out var a) && double.TryParse(parts[1], out var b) && b > 0) fps = a / b;
                        else double.TryParse(fr.GetString(), out fps);
                    }
                }
                if (h > 0 || w > 0) res.Add((w, h, codecs, fps, bw));
            }
        }
        catch { }
        return res;
    }

    /// <summary>番剧 season 信息（标题/封面/UP/选集），用于番剧免 yt-dlp 出卡片。</summary>
    public sealed class PgcMeta
    {
        public string Title { get; set; } = "";
        public string Cover { get; set; } = "";
        public string Uploader { get; set; } = "";
        public List<(string title, long epid, long cid, string cover)> Eps { get; set; } = new();
    }

    public static async Task<PgcMeta> GetSeasonMetaAsync(long epId, string? cookieFile, string? proxy)
    {
        var m = new PgcMeta();
        try
        {
            var json = await ApiGetStringAsync($"https://api.bilibili.com/pgc/view/web/season?ep_id={epId}", cookieFile, proxy).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var data)) return m;
            m.Title = data.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
            m.Cover = data.TryGetProperty("cover", out var c) ? (c.GetString() ?? "") : "";
            if (data.TryGetProperty("up_info", out var up) && up.ValueKind == JsonValueKind.Object && up.TryGetProperty("uname", out var un))
                m.Uploader = un.GetString() ?? "";
            if (data.TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Array)
            {
                foreach (var ep in eps.EnumerateArray())
                {
                    var id = ep.TryGetProperty("id", out var idj) && idj.ValueKind == JsonValueKind.Number ? idj.GetInt64() : 0;
                    var cid = ep.TryGetProperty("cid", out var cj) && cj.ValueKind == JsonValueKind.Number ? cj.GetInt64() : 0;
                    var title = ep.TryGetProperty("long_title", out var lt) && lt.GetString() is string lts && lts.Length > 0
                        ? lts : (ep.TryGetProperty("title", out var tt) ? (tt.GetString() ?? "") : "");
                    var cov = ep.TryGetProperty("cover", out var cv) ? (cv.GetString() ?? "") : "";
                    if (id > 0) m.Eps.Add((title, id, cid, cov));
                }
            }
        }
        catch { }
        return m;
    }

    /// <summary>启动预热：把 wbi 签名密钥提前取好，首解析省一次 nav 请求。</summary>
    public static async Task WarmAsync(string? cookieFile, string? proxy)
    {
        try { await GetMixinKeyAsync(cookieFile, proxy).ConfigureAwait(false); } catch { }
    }

    /// <summary>一次 /x/web-interface/view 请求拿全基础信息：标题/封面/UP/时长/分P/合集。</summary>
    public static async Task<BiliMeta> GetVideoMetaAsync(string bvid, string aid, string? cookieFile, string? proxy)
    {
        var m = new BiliMeta { Bvid = bvid };
        var q = bvid.Length > 0 ? "bvid=" + Uri.EscapeDataString(bvid) : "aid=" + Uri.EscapeDataString(aid);
        var json = await ApiGetStringAsync("https://api.bilibili.com/x/web-interface/view?" + q, cookieFile, proxy).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var code) && code.GetInt32() != 0) return m;
        if (!root.TryGetProperty("data", out var data)) return m;

        m.Title = data.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
        m.Thumb = data.TryGetProperty("pic", out var pic) ? (pic.GetString() ?? "") : "";
        m.Bvid = data.TryGetProperty("bvid", out var bv) ? (bv.GetString() ?? bvid) : bvid;
        if (data.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("name", out var on))
            m.Uploader = on.GetString() ?? "";
        if (data.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number)
            m.Duration = du.GetDouble();
        if (data.TryGetProperty("cid", out var cidj) && cidj.ValueKind == JsonValueKind.Number)
            m.Cid = cidj.GetInt64();

        if (data.TryGetProperty("ugc_season", out var season) && season.ValueKind == JsonValueKind.Object)
        {
            m.IsSeries = true;
            if (season.TryGetProperty("sections", out var secs) && secs.ValueKind == JsonValueKind.Array)
            {
                foreach (var sec in secs.EnumerateArray())
                {
                    if (!sec.TryGetProperty("episodes", out var eps) || eps.ValueKind != JsonValueKind.Array) continue;
                    foreach (var ep in eps.EnumerateArray())
                    {
                        var ebv = ep.TryGetProperty("bvid", out var b) ? (b.GetString() ?? "") : "";
                        var et = ep.TryGetProperty("title", out var etj) ? (etj.GetString() ?? "") : "";
                        var ecid = ep.TryGetProperty("cid", out var ecj) && ecj.ValueKind == JsonValueKind.Number ? ecj.GetInt64() : 0;
                        var ecov = "";
                        if (ep.TryGetProperty("arc", out var arc0))
                        {
                            if (arc0.TryGetProperty("title", out var at) && et.Length == 0) et = at.GetString() ?? "";
                            if (arc0.TryGetProperty("pic", out var apic)) ecov = apic.GetString() ?? "";
                        }
                        if (ecov.Length == 0 && ep.TryGetProperty("pic", out var epic)) ecov = epic.GetString() ?? "";
                        if (et.Length == 0 && ep.TryGetProperty("arc", out var arc) && arc.TryGetProperty("title", out var at2))
                            et = at2.GetString() ?? "";
                        if (ebv.Length > 0) m.Parts.Add((et.Length > 0 ? et : ebv, "https://www.bilibili.com/video/" + ebv, ecid, ecov));
                    }
                }
            }
        }
        else if (data.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array && pages.GetArrayLength() > 1)
        {
            foreach (var pg in pages.EnumerateArray())
            {
                var num = pg.TryGetProperty("page", out var pn) && pn.ValueKind == JsonValueKind.Number ? pn.GetInt32() : 0;
                var pt = pg.TryGetProperty("part", out var ptt) ? (ptt.GetString() ?? "") : "";
                var pcid = pg.TryGetProperty("cid", out var pcj) && pcj.ValueKind == JsonValueKind.Number ? pcj.GetInt64() : 0;
                var label = pt.Length > 0 && !Regex.IsMatch(pt, @"^P\d+$") ? $"P{num} · {pt}" : $"P{num}";
                m.Parts.Add((label, $"https://www.bilibili.com/video/{m.Bvid}?p={num}", pcid, ""));
            }
        }
        return m;
    }

    /* ---------------- 手机号 + 短信验证码 ---------------- */

    public static async Task<(string token, string gt, string challenge)> GetCaptchaAsync()
    {
        await EnsureBuvidAsync();
        var json = await Http.GetStringAsync("https://passport.bilibili.com/x/passport-login/captcha?ad_type=geetest&source=main-fe-header");
        using var doc = JsonDocument.Parse(json);
        var d = doc.RootElement.GetProperty("data");
        var token = d.TryGetProperty("token", out var t) ? (t.GetString() ?? "") : "";
        var gt = ""; var challenge = "";
        if (d.TryGetProperty("geetest", out var g))
        {
            gt = g.TryGetProperty("gt", out var gv) ? (gv.GetString() ?? "") : "";
            challenge = g.TryGetProperty("challenge", out var cv) ? (cv.GetString() ?? "") : "";
        }
        return (token, gt, challenge);
    }

    private static async Task EnsureBuvidAsync()
    {
        try
        {
            var c = await Http.GetStringAsync("https://api.bilibili.com/x/frontend/finger/spi");
            using var d = JsonDocument.Parse(c);
            var dd = d.RootElement.GetProperty("data");
            var b3 = dd.TryGetProperty("b_3", out var p3) ? (p3.GetString() ?? "") : "";
            var b4 = dd.TryGetProperty("b_4", out var p4) ? (p4.GetString() ?? "") : "";
            if (b3.Length > 0) Cookies.Add(new System.Net.Cookie("buvid3", b3, "/", ".bilibili.com"));
            if (b4.Length > 0) Cookies.Add(new System.Net.Cookie("buvid4", b4, "/", ".bilibili.com"));
        }
        catch { }
    }

    public static async Task<(int code, string msg, string captchaKey)> SendSmsAsync(string tel, string token, string challenge, string validate, string seccode)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["cid"] = "86", ["tel"] = tel, ["source"] = "main-fe-header",
            ["token"] = token, ["challenge"] = challenge,
            ["validate"] = validate, ["seccode"] = seccode,
        });
        using var resp = await Http.PostAsync("https://passport.bilibili.com/x/passport-login/web/sms/send", form);
        var json = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            var msg = doc.RootElement.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
            var key = "";
            if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                && d.TryGetProperty("captcha_key", out var k)) key = k.GetString() ?? "";
            return (code, msg, key);
        }
        catch { return (-1, "接口返回异常", ""); }
    }

    public static async Task<(int code, string msg, Dictionary<string, string> cookies)> LoginSmsAsync(string tel, string code, string captchaKey)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["cid"] = "86", ["tel"] = tel, ["code"] = code, ["source"] = "main-fe-header",
            ["captcha_key"] = captchaKey, ["go_url"] = "https://www.bilibili.com/",
        });
        using var resp = await Http.PostAsync("https://passport.bilibili.com/x/passport-login/web/login/sms", form);
        var json = await resp.Content.ReadAsStringAsync();
        var cookies = ParseCookies(resp);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var c = doc.RootElement.TryGetProperty("code", out var cc) ? cc.GetInt32() : -1;
            var msg = doc.RootElement.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
            return (c, msg, cookies);
        }
        catch { return (-1, "接口返回异常", cookies); }
    }

    private static Dictionary<string, string> ParseCookies(HttpResponseMessage resp)
    {
        var cookies = new Dictionary<string, string>();
        if (resp.Headers.TryGetValues("Set-Cookie", out var vals))
        {
            foreach (var v in vals)
            {
                var m = Regex.Match(v, @"^\s*([^=;]+)=([^;]*)");
                if (m.Success)
                {
                    var name = m.Groups[1].Value.Trim();
                    if (!name.StartsWith("#") && !cookies.ContainsKey(name)) cookies[name] = m.Groups[2].Value;
                }
            }
        }
        return cookies;
    }

    /// <summary>账号密码登录（RSA 加密密码 + 极验参数）。</summary>
    public static async Task<(int code, string msg, Dictionary<string, string> cookies)> PwdLoginAsync(
        string username, string password, string token, string challenge, string validate, string seccode)
    {
        var keyJson = await Http.GetStringAsync("https://passport.bilibili.com/x/passport-login/web/key");
        using var kd = JsonDocument.Parse(keyJson);
        var kdata = kd.RootElement.GetProperty("data");
        var hash = kdata.GetProperty("hash").GetString() ?? "";
        var keyB64 = kdata.GetProperty("key").GetString() ?? "";
        string encPwd;
        using (var rsa = System.Security.Cryptography.RSA.Create())
        {
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(keyB64), out _);
            var bytes = System.Text.Encoding.UTF8.GetBytes(hash + password);
            encPwd = Convert.ToBase64String(rsa.Encrypt(bytes, System.Security.Cryptography.RSAEncryptionPadding.Pkcs1));
        }
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username, ["password"] = encPwd, ["keep"] = "0", ["source"] = "main-fe-header", ["go_url"] = "https://www.bilibili.com/",
            ["token"] = token, ["challenge"] = challenge, ["validate"] = validate, ["seccode"] = seccode,
        });
        using var resp = await Http.PostAsync("https://passport.bilibili.com/x/passport-login/web/login", form);
        var json = await resp.Content.ReadAsStringAsync();
        var cookies = ParseCookies(resp);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var c = doc.RootElement.TryGetProperty("code", out var cc) ? cc.GetInt32() : -1;
            var m = doc.RootElement.TryGetProperty("message", out var mm) ? (mm.GetString() ?? "") : "";
            return (c, m, cookies);
        }
        catch { return (-1, "接口返回异常", cookies); }
    }

    /// <summary>番剧（PGC）：用 season 接口列出整季选集。</summary>
    public static async Task<(string title, string thumb, List<(string title, string url)> parts)>
        GetSeasonStructureAsync(string url, string? cookieFile, string? proxy)
    {
        var parts = new List<(string title, string url)>();
        var mEp = Regex.Match(url, @"bangumi/play/ep(\d+)", RegexOptions.IgnoreCase);
        var mSs = Regex.Match(url, @"bangumi/play/ss(\d+)", RegexOptions.IgnoreCase);
        var q = mEp.Success ? "ep_id=" + mEp.Groups[1].Value : mSs.Success ? "season_id=" + mSs.Groups[1].Value : "";
        if (q.Length == 0) return ("", "", parts);

        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(proxy)) handler.Proxy = new System.Net.WebProxy(proxy!);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        if (!string.IsNullOrEmpty(cookieFile) && File.Exists(cookieFile))
        {
            var ck = ReadCookieHeader(cookieFile);
            if (ck.Length > 0) http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", ck);
        }
        var json = await http.GetStringAsync("https://api.bilibili.com/pgc/view/web/season?" + q);
        string title = "", thumb = "";
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var c) && c.GetInt32() != 0) return (title, thumb, parts);
        if (!root.TryGetProperty("result", out var data)) return (title, thumb, parts);
        title = data.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
        thumb = data.TryGetProperty("cover", out var cv) ? (cv.GetString() ?? "") : "";
        if (data.TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var ep in eps.EnumerateArray())
            {
                i++;
                var id = ep.TryGetProperty("id", out var ide) && ide.ValueKind == JsonValueKind.Number ? ide.GetInt64() : 0;
                if (id <= 0) { i--; continue; }
                var lt = ep.TryGetProperty("long_title", out var l) ? (l.GetString() ?? "") : "";
                var label = lt.Length > 0 ? $"第{i}话  {lt}" : $"第{i}话";
                parts.Add((label, "https://www.bilibili.com/bangumi/play/ep" + id));
            }
        }
        return (title, thumb, parts);
    }
}
