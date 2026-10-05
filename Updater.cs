using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace BiliGet;

public sealed class UpdateInfo
{
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Notes { get; set; } = "";
    public string[] Changelog { get; set; } = Array.Empty<string>();
}

public static class Updater
{
    public const string CurrentVersion = "1.1.14";
    public const string DownloadPage = "https://otakusdremingworld.xyz/changelog.html";

    // 依次尝试：域名 HTTPS → 服务器 IP HTTPS → 服务器 IP HTTP
    // （某些网络会按域名 SNI 重置，用 IP 可绕过）
    private static readonly string[] Bases =
    {
        "https://otakusdremingworld.xyz",
        "https://101.200.208.212",
        "http://101.200.208.212",
    };

    private static HttpClient MakeClient(string? proxy, int timeoutSec)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        if (!string.IsNullOrWhiteSpace(proxy)) handler.Proxy = new WebProxy(proxy!);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
    }

    public static async Task<UpdateInfo?> CheckAsync(string? proxy = null)
    {
        Exception? last = null;
        foreach (var b in Bases)
        {
            try
            {
                using var h = MakeClient(proxy, 10);
                var json = await h.GetStringAsync(b + "/download/vget-latest.json?t=" + DateTime.UtcNow.Ticks);
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                var info = new UpdateInfo
                {
                    Version = r.TryGetProperty("version", out var v) ? (v.GetString() ?? "") : "",
                    Url = r.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "",
                    Sha256 = r.TryGetProperty("sha256", out var s) ? (s.GetString() ?? "") : "",
                    Notes = r.TryGetProperty("notes", out var n) ? (n.GetString() ?? "") : "",
                };
                if (r.TryGetProperty("changelog", out var cl) && cl.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var it in cl.EnumerateArray())
                        if (it.GetString() is string line && line.Length > 0) list.Add(line);
                    info.Changelog = list.ToArray();
                }
                return info;
            }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new Exception("无法连接更新服务器");
    }

    public static bool IsNewer(string remote, string local)
    {
        static int[] Parts(string s) => s.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var a = Parts(remote); var b = Parts(local);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    public static async Task<string> DownloadAsync(string url, string sha256, string? proxy, Action<double> progress)
    {
        var candidates = new List<string> { url };
        try
        {
            var u = new Uri(url);
            candidates.Add("https://101.200.208.212" + u.AbsolutePath);
            candidates.Add("http://101.200.208.212" + u.AbsolutePath);
        }
        catch { }

        Exception? last = null;
        foreach (var cand in candidates)
        {
            try { return await DownloadOneAsync(cand, sha256, proxy, progress); }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new Exception("下载更新失败");
    }

    private static async Task<string> DownloadOneAsync(string url, string sha256, string? proxy, Action<double> progress)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "OTAKUsVget-update.exe");
        if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { } }

        using var h = MakeClient(proxy, 600);
        using var resp = await h.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;

        await using (var fs = File.Create(tmp))
        await using (var s = await resp.Content.ReadAsStreamAsync())
        {
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await s.ReadAsync(buf)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n));
                read += n;
                if (total > 0) progress((double)read / total * 100.0);
            }
        }

        if (!string.IsNullOrWhiteSpace(sha256))
        {
            using var fs = File.OpenRead(tmp);
            var hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            if (!hash.Equals(sha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                throw new Exception("安装包校验失败（可能下载不完整）");
        }
        return tmp;
    }

    public static void RunInstaller(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
        });
    }
}
