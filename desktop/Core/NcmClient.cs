using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DianGeJi.Core;

public sealed class NcmException(string message) : Exception(message);

/// <summary>
/// 网易云音乐接口（eapi 加密，移植自 NeteaseCloudMusicApi Enhanced）。
/// 登录 cookie 用 DPAPI 加密存在 %APPDATA%\点歌机\netease.dat。
/// </summary>
public sealed partial class NcmClient
{
    private const string EapiDomain = "https://interfacepc.music.163.com";
    private const string UserAgent = "NeteaseMusic 9.0.90/5038 (iPhone; iOS 16.2; zh_CN)";
    private static readonly byte[] EapiKey = Encoding.ASCII.GetBytes("e82ckenh8dichen8");

    private static readonly JsonSerializerOptions BodyJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly object _gate = new();
    private Dictionary<string, string> _cookies = new();
    private string _deviceId = "";

    public NcmClient()
    {
        _http = new HttpClient(new HttpClientHandler
        {
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
        })
        { Timeout = TimeSpan.FromSeconds(20) };
        LoadSession();
    }

    public HttpClient Http => _http;
    public bool HasLogin => _cookies.ContainsKey("MUSIC_U");

    // ================= 会话 =================
    private sealed class Session
    {
        public Dictionary<string, string> Cookies { get; set; } = new();
        public string DeviceId { get; set; } = "";
    }

    private void LoadSession()
    {
        try
        {
            if (File.Exists(AppPaths.NeteaseSession))
            {
                var s = JsonSerializer.Deserialize<Session>(Secret.Unprotect(File.ReadAllText(AppPaths.NeteaseSession)));
                if (s != null)
                {
                    _cookies = s.Cookies;
                    _deviceId = s.DeviceId;
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn("读取网易云登录信息失败：" + e.Message);
        }
        if (_deviceId == "")
            _deviceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    }

    private void SaveSession()
    {
        string json;
        lock (_gate)
            json = JsonSerializer.Serialize(new Session { Cookies = new(_cookies), DeviceId = _deviceId });
        try
        {
            AppPaths.WriteAtomic(AppPaths.NeteaseSession, Secret.Protect(json));
        }
        catch (Exception e)
        {
            Log.Error("保存网易云登录信息失败", e);
        }
    }

    public void Logout()
    {
        lock (_gate)
            _cookies.Clear();
        SaveSession();
    }

    // ================= eapi =================
    private static string Eapi(string uri, string text)
    {
        var digest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"nobody{uri}use{text}md5forencrypt"))).ToLowerInvariant();
        var data = $"{uri}-36cd479b6b5-{text}-36cd479b6b5-{digest}";
        using var aes = Aes.Create();
        aes.Key = EapiKey;
        return Convert.ToHexString(aes.EncryptEcb(Encoding.UTF8.GetBytes(data), PaddingMode.PKCS7));
    }

    private async Task<JsonNode> CallAsync(string uri, Dictionary<string, object?> data, bool saveCookies = false)
    {
        Dictionary<string, string> header;
        lock (_gate)
        {
            header = new Dictionary<string, string>
            {
                ["osver"] = "Microsoft-Windows-10-Professional-build-19045-64bit",
                ["deviceId"] = _deviceId,
                ["os"] = "pc",
                ["appver"] = "3.1.17.204416",
                ["versioncode"] = "140",
                ["mobilename"] = "",
                ["buildver"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ["resolution"] = "1920x1080",
                ["__csrf"] = _cookies.GetValueOrDefault("__csrf", ""),
                ["channel"] = "netease",
                ["requestId"] = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Random.Shared.Next(1000):0000}",
            };
            foreach (var k in new[] { "MUSIC_U", "NMTID" })
                if (_cookies.TryGetValue(k, out var v))
                    header[k] = v;
        }
        data["header"] = header;
        var text = JsonSerializer.Serialize(data, BodyJson);

        using var req = new HttpRequestMessage(HttpMethod.Post, EapiDomain + "/eapi/" + uri[5..]);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["params"] = Eapi(uri, text) });
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ",
            header.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value))));

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new NcmException("连接网易云失败，请检查网络");
        }
        using (resp)
        {
            if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                var changed = false;
                lock (_gate)
                {
                    foreach (var c in setCookies)
                    {
                        var pair = c.Split(';', 2)[0];
                        var eq = pair.IndexOf('=');
                        if (eq <= 0)
                            continue;
                        var name = pair[..eq].Trim();
                        var value = pair[(eq + 1)..].Trim();
                        if (name is "MUSIC_U" or "__csrf" or "NMTID" or "MUSIC_A_T" or "MUSIC_R_T" || saveCookies)
                        {
                            _cookies[name] = value;
                            changed = true;
                        }
                    }
                }
                if (changed)
                    SaveSession();
            }
            var body = await resp.Content.ReadAsStringAsync();
            try
            {
                return JsonNode.Parse(body) ?? throw new NcmException("网易云返回了空数据");
            }
            catch (JsonException)
            {
                throw new NcmException($"网易云返回了无法识别的数据（HTTP {(int)resp.StatusCode}）");
            }
        }
    }

    private static void EnsureOk(JsonNode body)
    {
        var code = body["code"]?.GetValue<int>() ?? 200;
        if (code != 200)
            throw new NcmException(body["message"]?.GetValue<string>() ?? body["msg"]?.GetValue<string>() ?? $"网易云接口错误（{code}）");
    }

    // ================= 业务接口 =================
    public static Song ParseSong(JsonNode s)
    {
        var al = s["al"] ?? s["album"];
        var ar = (s["ar"] ?? s["artists"])?.AsArray();
        var artists = ar == null ? "" : string.Join(" / ", ar.Select(a => a?["name"]?.GetValue<string>() ?? "").Where(n => n != ""));
        return new Song
        {
            Id = s["id"]!.GetValue<long>(),
            Name = s["name"]?.GetValue<string>() ?? "未知歌曲",
            Artists = artists == "" ? "未知歌手" : artists,
            Album = al?["name"]?.GetValue<string>() ?? "",
            Cover = al?["picUrl"]?.GetValue<string>() ?? "",
            Duration = (int)Math.Round(((s["dt"] ?? s["duration"])?.GetValue<double>() ?? 0) / 1000),
            Fee = s["fee"]?.GetValue<int>() ?? 0,
        };
    }

    public async Task<List<Song>> SearchAsync(string keyword, int limit = 20)
    {
        var body = await CallAsync("/api/cloudsearch/pc", new() { ["s"] = keyword, ["type"] = 1, ["limit"] = limit, ["offset"] = 0, ["total"] = true });
        EnsureOk(body);
        return body["result"]?["songs"]?.AsArray().Where(x => x != null).Select(x => ParseSong(x!)).ToList() ?? new();
    }

    public async Task<List<Song>> SongDetailAsync(IReadOnlyList<long> ids)
    {
        var result = new List<Song>();
        foreach (var chunk in ids.Chunk(500))
        {
            var c = "[" + string.Join(",", chunk.Select(id => $"{{\"id\":{id}}}")) + "]";
            var body = await CallAsync("/api/v3/song/detail", new() { ["c"] = c });
            EnsureOk(body);
            result.AddRange(body["songs"]?.AsArray().Where(x => x != null).Select(x => ParseSong(x!)) ?? []);
        }
        return result;
    }

    public async Task<Song?> SongDetailAsync(long id) => (await SongDetailAsync(new[] { id })).FirstOrDefault();

    public sealed record SongUrl(string? Url, bool Trial, string Type);

    public async Task<SongUrl> SongUrlAsync(long id, string quality)
    {
        var br = Quality.Levels.TryGetValue(quality, out var q) ? q.Bitrate : 320000;
        var body = await CallAsync("/api/song/enhance/player/url", new() { ["ids"] = $"[{id}]", ["br"] = br });
        EnsureOk(body);
        var d = body["data"]?.AsArray().FirstOrDefault();
        if (d == null)
            return new SongUrl(null, false, "mp3");
        var trial = d["freeTrialInfo"] is JsonNode t && t.GetValueKind() != JsonValueKind.Null;
        return new SongUrl(d["url"]?.GetValue<string>(), trial, (d["type"]?.GetValue<string>() ?? "mp3").ToLowerInvariant());
    }

    public async Task<(string Name, List<Song> Songs)> PlaylistAsync(long id)
    {
        var body = await CallAsync("/api/v6/playlist/detail", new() { ["id"] = id, ["n"] = 100000, ["s"] = 8 });
        EnsureOk(body);
        var pl = body["playlist"] ?? throw new NcmException("歌单不存在或无权访问");
        var ids = pl["trackIds"]?.AsArray().Select(t => t!["id"]!.GetValue<long>()).ToList() ?? new();
        var songs = ids.Count > 0 ? await SongDetailAsync(ids) : new List<Song>();
        return (pl["name"]?.GetValue<string>() ?? id.ToString(), songs);
    }

    public async Task<Account?> AccountAsync()
    {
        if (!HasLogin)
            return null;
        var body = await CallAsync("/api/w/nuser/account/get", new());
        var profile = body["profile"];
        if (profile == null || profile.GetValueKind() == JsonValueKind.Null)
            return null;
        var vipType = body["account"]?["vipType"]?.GetValue<int>() ?? 0;
        return new Account
        {
            Uid = profile["userId"]!.GetValue<long>(),
            Nickname = profile["nickname"]?.GetValue<string>() ?? "",
            Avatar = profile["avatarUrl"]?.GetValue<string>(),
            Vip = vipType != 0,
        };
    }

    public async Task<List<Playlist>> UserPlaylistsAsync(long uid)
    {
        var body = await CallAsync("/api/user/playlist", new() { ["uid"] = uid, ["limit"] = 100, ["offset"] = 0, ["includeVideo"] = true });
        EnsureOk(body);
        return body["playlist"]?.AsArray().Where(p => p != null).Select(p => new Playlist
        {
            Id = p!["id"]!.GetValue<long>(),
            Name = p["name"]?.GetValue<string>() ?? "",
            Count = p["trackCount"]?.GetValue<int>() ?? 0,
            Cover = p["coverImgUrl"]?.GetValue<string>(),
        }).ToList() ?? new();
    }

    /// <summary>获取扫码登录的 key，返回 (key, 二维码内容)。</summary>
    public async Task<(string Key, string Url)> QrCreateAsync()
    {
        var body = await CallAsync("/api/login/qrcode/unikey", new() { ["type"] = 3 });
        var key = body["unikey"]?.GetValue<string>() ?? throw new NcmException("获取登录二维码失败");
        return (key, $"https://music.163.com/login?codekey={key}");
    }

    /// <summary>800 过期，801 等待扫码，802 已扫码待确认，803 登录成功。</summary>
    public async Task<int> QrCheckAsync(string key)
    {
        var body = await CallAsync("/api/login/qrcode/client/login", new() { ["key"] = key, ["type"] = 3 }, saveCookies: true);
        return body["code"]?.GetValue<int>() ?? 0;
    }

    // ================= 链接解析 =================
    /// <summary>从分享链接或纯数字解析 歌曲/歌单 id；kind = "song" | "playlist"</summary>
    public async Task<long?> ParseIdAsync(string text, string kind)
    {
        text = (text ?? "").Trim();
        if (long.TryParse(text, out var n))
            return n;
        var shortLink = ShortLinkRegex().Match(text);
        if (shortLink.Success)
        {
            try
            {
                using var handler = new HttpClientHandler { AllowAutoRedirect = true, UseProxy = false };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
                using var r = await http.GetAsync(shortLink.Value, HttpCompletionOption.ResponseHeadersRead);
                text = r.RequestMessage?.RequestUri?.ToString() ?? text;
            }
            catch (Exception)
            {
            }
        }
        var m = Regex.Match(text, kind + @"(?:\?|/\?|.*?[?&])id=(\d+)");
        if (!m.Success)
            m = Regex.Match(text, "/" + kind + @"/(\d+)");
        return m.Success ? long.Parse(m.Groups[1].Value) : null;
    }

    public static bool LooksLikeLink(string text) => text.Contains("music.163.com") || text.Contains("163cn.tv");

    [GeneratedRegex(@"https?://163cn\.tv/\S+")]
    private static partial Regex ShortLinkRegex();
}
