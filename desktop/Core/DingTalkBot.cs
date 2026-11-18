using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DianGeJi.Core;

/// <summary>
/// 钉钉机器人（Stream 模式：本机主动连钉钉服务器，不需要公网 IP）。
/// 协议参考官方 dingtalk-stream-sdk-python。
/// </summary>
public sealed class DingTalkBot
{
    private const string OpenConnectionApi = "https://api.dingtalk.com/v1.0/gateway/connections/open";
    private const string BotTopic = "/v1.0/im/bot/messages/get";
    private static readonly TimeSpan SearchTtl = TimeSpan.FromMinutes(10);

    public const string Help = """
        #### 🎵 点歌机器人
        - **点歌 歌名** 直接点第一首搜索结果（也可以直接发歌名）
        - **搜 歌名** 列出搜索结果，再发 **选 N** 点第 N 首
        - **点歌 网易云链接** 精确点歌
        - **列表** 查看播放列表
        - **当前** 查看正在播放
        - **切歌** 跳过当前歌曲
        - **暂停 / 继续**
        - **删除 N** 删除列表第 N 首（自己点的）
        - **置顶 N** / **清空** / **音量 0-100**（管理员）
        """;

    private readonly AppSettings _cfg;
    private readonly Jukebox _jb;
    private readonly NcmClient _ncm;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<string, (DateTime At, List<Song> Results)> _searches = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public string Status { get; private set; } = "未配置";
    public string? LastMessage { get; private set; }

    public DingTalkBot(AppSettings cfg, Jukebox jb, NcmClient ncm)
    {
        _cfg = cfg;
        _jb = jb;
        _ncm = ncm;
    }

    // ================= 连接 =================
    public void Start()
    {
        if (string.IsNullOrWhiteSpace(_cfg.ClientId) || string.IsNullOrWhiteSpace(_cfg.ClientSecret))
        {
            Status = "未配置（在设置里填写 Client ID 和 Client Secret）";
            return;
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Status = "连接中…";
        _loop = Task.Run(() => RunAsync(_cfg.ClientId.Trim(), _cfg.ClientSecret.Trim(), ct));
    }

    public async Task StopAsync()
    {
        if (_cts == null)
            return;
        _cts.Cancel();
        try
        {
            if (_loop != null)
                await _loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }
        _cts = null;
        _loop = null;
        Status = "已停止";
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        Start();
    }

    private async Task RunAsync(string clientId, string secret, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            (string Endpoint, string Ticket)? conn;
            try
            {
                conn = await OpenConnectionAsync(clientId, secret, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (conn == null)
            {
                Status = "连接失败（检查 Client ID / Secret，以及机器人是否为 Stream 模式并已发布）";
                await Delay(15, ct);
                continue;
            }
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                await ws.ConnectAsync(new Uri($"{conn.Value.Endpoint}?ticket={Uri.EscapeDataString(conn.Value.Ticket)}"), ct);
                Status = "已连接";
                Log.Info("钉钉已连接");
                await ReceiveLoopAsync(ws, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Warn("钉钉连接断开：" + e.Message);
            }
            if (!ct.IsCancellationRequested)
            {
                Status = "连接断开，重连中…";
                await Delay(3, ct);
            }
        }
    }

    private static async Task Delay(int sec, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(sec), ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<(string, string)?> OpenConnectionAsync(string clientId, string secret, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            clientId,
            clientSecret = secret,
            subscriptions = new[] { new { type = "CALLBACK", topic = BotTopic } },
            ua = "dingtalk-stream-dotnet/diangeji-1.0",
            localIp = "127.0.0.1",
        });
        try
        {
            using var resp = await _http.PostAsync(OpenConnectionApi, new StringContent(body, Encoding.UTF8, "application/json"), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"钉钉连接失败：HTTP {(int)resp.StatusCode} {text}");
                return null;
            }
            var j = JsonNode.Parse(text);
            var endpoint = j?["endpoint"]?.GetValue<string>();
            var ticket = j?["ticket"]?.GetValue<string>();
            return endpoint == null || ticket == null ? null : (endpoint, ticket);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.Warn("钉钉连接失败：" + e.Message);
            return null;
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var msg = new MemoryStreamLite();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close)
                return;
            msg.Write(buffer, r.Count);
            if (!r.EndOfMessage)
                continue;
            var text = msg.ToUtf8();
            msg.Reset();

            JsonNode? frame;
            try
            {
                frame = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                continue;
            }
            if (frame == null)
                continue;
            var type = frame["type"]?.GetValue<string>();
            var headers = frame["headers"];
            var topic = headers?["topic"]?.GetValue<string>();
            var messageId = headers?["messageId"]?.GetValue<string>() ?? "";
            var data = frame["data"]?.GetValue<string>() ?? "";

            if (type == "SYSTEM")
            {
                await SendAckAsync(ws, messageId, data, ct);
                if (topic == "disconnect")
                {
                    Log.Info("钉钉要求重连");
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                    return;
                }
            }
            else if (type == "CALLBACK")
            {
                await SendAckAsync(ws, messageId, "{\"response\":null}", ct);
                if (topic == BotTopic)
                    _ = Task.Run(() => OnBotMessageAsync(data));
            }
            else if (type == "EVENT")
            {
                await SendAckAsync(ws, messageId, data, ct);
            }
        }
    }

    private async Task SendAckAsync(ClientWebSocket ws, string messageId, string data, CancellationToken ct)
    {
        var ack = JsonSerializer.Serialize(new
        {
            code = 200,
            headers = new { contentType = "application/json", messageId },
            message = "OK",
            data,
        });
        await _sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(ack), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ================= 消息处理 =================
    private sealed record Sender(string Nick, string Id, string Webhook, long WebhookExpire);

    private abstract record Reply;
    private sealed record TextReply(string Text) : Reply;
    private sealed record MarkdownReply(string Title, string Text) : Reply;

    private async Task OnBotMessageAsync(string data)
    {
        try
        {
            var m = JsonNode.Parse(data);
            if (m == null)
                return;
            var msgType = m["msgtype"]?.GetValue<string>();
            var text = "";
            if (msgType == "text")
                text = m["text"]?["content"]?.GetValue<string>() ?? "";
            else if (msgType == "richText")
                text = string.Join(" ", m["content"]?["richText"]?.AsArray()
                    .Select(x => x?["text"]?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)) ?? []);
            var staffId = m["senderStaffId"]?.GetValue<string>() ?? m["senderId"]?.GetValue<string>() ?? "";
            var sender = new Sender(
                m["senderNick"]?.GetValue<string>() ?? "某人",
                staffId,
                m["sessionWebhook"]?.GetValue<string>() ?? "",
                m["sessionWebhookExpiredTime"]?.GetValue<long>() ?? 0);
            LastMessage = DateTime.Now.ToString("HH:mm:ss");
            Log.Info($"{sender.Nick}: {text}");

            Reply reply;
            try
            {
                reply = await HandleAsync(text, sender);
            }
            catch (Exception e) when (e is JukeboxException or NcmException)
            {
                reply = new TextReply(e.Message);
            }
            catch (Exception e)
            {
                Log.Error("处理钉钉消息出错", e);
                reply = new TextReply("出错了：" + e.Message);
            }
            await SendReplyAsync(sender, reply);
        }
        catch (Exception e)
        {
            Log.Error("处理钉钉消息出错", e);
        }
    }

    private async Task SendReplyAsync(Sender s, Reply reply)
    {
        if (s.Webhook == "")
            return;
        object body = reply switch
        {
            MarkdownReply md => new { msgtype = "markdown", markdown = new { title = md.Title, text = md.Text }, at = new { atUserIds = new[] { s.Id } } },
            TextReply t => new { msgtype = "text", text = new { content = t.Text }, at = new { atUserIds = new[] { s.Id } } },
            _ => throw new InvalidOperationException(),
        };
        try
        {
            using var resp = await _http.PostAsync(s.Webhook, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode)
                Log.Warn("钉钉回复失败：" + await resp.Content.ReadAsStringAsync());
        }
        catch (Exception e)
        {
            Log.Warn("钉钉回复失败：" + e.Message);
        }
    }

    /// <summary>模拟收到一条钉钉消息，返回回复内容（测试用）。</summary>
    internal async Task<string> SimulateAsync(string text, string nick, string id)
    {
        try
        {
            return await HandleAsync(text, new Sender(nick, id, "", 0)) switch
            {
                MarkdownReply md => md.Text,
                TextReply t => t.Text,
                _ => "",
            };
        }
        catch (Exception e) when (e is JukeboxException or NcmException)
        {
            return e.Message;
        }
    }

    private static readonly string[] Prefixes = { "点歌", "搜歌", "搜索", "删除", "置顶", "音量", "选" };

    private async Task<Reply> HandleAsync(string raw, Sender s)
    {
        var text = Regex.Replace(raw ?? "", @"\s+", " ").Trim();
        var isAdmin = _cfg.IsAdmin(s.Nick, s.Id);
        var notify = new Notify(s.Webhook, s.WebhookExpire);

        var m = Regex.Match(text, @"^(\S+)\s*(.*)$");
        var cmd = m.Success ? m.Groups[1].Value : "";
        var arg = m.Success ? m.Groups[2].Value.Trim() : "";
        // 兼容「点歌晴天」「选2」这种不带空格的写法
        foreach (var p in Prefixes)
        {
            if (!cmd.StartsWith(p) || cmd == p)
                continue;
            var rest = cmd[p.Length..];
            if (p is "点歌" or "搜歌" or "搜索" || rest.All(char.IsDigit))
            {
                cmd = p;
                arg = (rest + " " + arg).Trim();
                break;
            }
        }

        switch (cmd)
        {
            case "" or "帮助" or "help" or "?" or "？" or "菜单":
                return new MarkdownReply("点歌帮助", Help);
            case "点歌" or "点":
                return await RequestAsync(arg, s, notify);
            case "搜" or "搜歌" or "搜索":
                return await SearchAsync(arg, s);
            case "选":
                return await PickAsync(arg, s, notify);
            case "切歌" or "下一首" or "跳过" or "skip" or "next":
            {
                var cur = _jb.Next();
                return new TextReply(cur != null ? $"已切掉 {Fmt.Song(cur)}" : "现在没有在放歌");
            }
            case "暂停" or "pause":
                _jb.Pause();
                return new TextReply("已暂停");
            case "继续" or "播放" or "play" or "resume":
                _jb.Resume();
                return new TextReply("继续播放");
            case "列表" or "队列" or "歌单" or "播放列表" or "list":
                return ListReply();
            case "当前" or "正在播放" or "现在" or "np":
                return NowReply();
            case "删除" or "删":
                return RemoveReply(arg, s, isAdmin);
            case "置顶":
                return isAdmin ? TopReply(arg) : new TextReply("只有管理员可以置顶");
            case "清空":
                if (!isAdmin)
                    return new TextReply("只有管理员可以清空列表");
                _jb.Clear();
                return new TextReply("播放列表已清空");
            case "音量":
                if (arg == "")
                    return new TextReply($"当前音量 {_jb.Player.Volume}");
                if (!isAdmin)
                    return new TextReply("只有管理员可以调音量");
                if (!int.TryParse(arg, out var v))
                    return new TextReply("用法：音量 0-100");
                _jb.SetVolume(v);
                return new TextReply($"音量已调到 {_jb.Player.Volume}");
            default:
                // 其他文字都当作歌名
                return await RequestAsync(text, s, notify);
        }
    }

    private async Task<Reply> RequestAsync(string arg, Sender s, Notify notify)
    {
        if (arg == "")
            return new TextReply("用法：点歌 歌名（或网易云分享链接）");
        Song? song = null;
        if (NcmClient.LooksLikeLink(arg) || (arg.Length >= 5 && arg.All(char.IsDigit)))
        {
            var id = await _ncm.ParseIdAsync(arg, "song");
            if (id != null)
                song = await _ncm.SongDetailAsync(id.Value);
        }
        if (song == null)
        {
            var results = await _ncm.SearchAsync(arg, 5);
            if (results.Count == 0)
                return new TextReply($"没搜到「{arg}」");
            song = results[0];
            lock (_searches)
                _searches[s.Id] = (DateTime.Now, results);
        }
        await _jb.AddAsync(song, s.Nick, s.Id, "dingtalk", notify: notify);
        return new TextReply($"已点 {Fmt.Song(song)}");
    }

    private async Task<Reply> SearchAsync(string arg, Sender s)
    {
        if (arg == "")
            return new TextReply("用法：搜 歌名");
        var results = await _ncm.SearchAsync(arg, 10);
        if (results.Count == 0)
            return new TextReply($"没搜到「{arg}」");
        lock (_searches)
            _searches[s.Id] = (DateTime.Now, results);
        var lines = results.Select((x, i) => $"{i + 1}. {x.Name} - {x.Artists}{(x.Fee == 1 ? "（VIP）" : "")}");
        return new MarkdownReply("搜索结果", $"#### 搜索「{arg}」\n" + string.Join("\n", lines) + "\n\n发送 **选 N** 点歌");
    }

    private async Task<Reply> PickAsync(string arg, Sender s, Notify notify)
    {
        (DateTime At, List<Song> Results) entry;
        lock (_searches)
            _searches.TryGetValue(s.Id, out entry);
        if (entry.Results == null || DateTime.Now - entry.At > SearchTtl)
            return new TextReply("先发「搜 歌名」再选");
        if (!int.TryParse(arg, out var n) || n < 1 || n > entry.Results.Count)
            return new TextReply($"请发 选 1 ~ 选 {entry.Results.Count}");
        var song = entry.Results[n - 1];
        await _jb.AddAsync(song, s.Nick, s.Id, "dingtalk", notify: notify);
        return new TextReply($"已点 {Fmt.Song(song)}");
    }

    private Reply NowReply()
    {
        var st = _jb.Snapshot();
        var cur = st.Current;
        if (cur == null)
            return new TextReply("现在没有在放歌");
        var state = st.Loading ? "加载中" : st.Paused ? "已暂停" : "播放中";
        return new TextReply($"{state}：{Fmt.Song(cur)}\n{Fmt.Time(st.Position)} / {Fmt.Time(cur.Duration)}，{cur.Requester} 点播");
    }

    private Reply ListReply()
    {
        var st = _jb.Snapshot();
        var sb = new StringBuilder("#### 播放列表\n");
        if (st.Current is { } cur)
            sb.Append($"**▶ {cur.Name} - {cur.Artists}**（{cur.Requester}）\n\n");
        if (st.Queue.Count == 0)
        {
            sb.Append("列表是空的，快来点歌吧");
            if (st.Idle.Id != null)
                sb.Append($"\n\n（空闲时随机播放歌单「{st.Idle.Name}」）");
        }
        else
        {
            foreach (var (x, i) in st.Queue.Take(30).Select((x, i) => (x, i)))
                sb.Append($"{i + 1}. {x.Name} - {x.Artists}（{x.Requester}）\n");
            if (st.Queue.Count > 30)
                sb.Append($"\n…还有 {st.Queue.Count - 30} 首");
        }
        return new MarkdownReply("播放列表", sb.ToString());
    }

    private Song ByIndex(string arg)
    {
        var q = _jb.Snapshot().Queue;
        if (!int.TryParse(arg, out var n) || n < 1 || n > q.Count)
            throw new JukeboxException($"序号不对，列表共 {q.Count} 首");
        return q[n - 1];
    }

    private Reply RemoveReply(string arg, Sender s, bool isAdmin)
    {
        var song = ByIndex(arg);
        if (!isAdmin && song.RequesterId != s.Id)
            return new TextReply("只能删除自己点的歌");
        _jb.Remove(song.Uid!);
        _jb.Event($"{s.Nick} 删除了 {Fmt.Song(song)}");
        return new TextReply($"已删除 {Fmt.Song(song)}");
    }

    private Reply TopReply(string arg)
    {
        var song = ByIndex(arg);
        _jb.Move(song.Uid!, 0);
        return new TextReply($"已置顶 {Fmt.Song(song)}");
    }

    /// <summary>拼接 WebSocket 分片消息。</summary>
    private sealed class MemoryStreamLite
    {
        private byte[] _buf = new byte[64 * 1024];
        private int _len;

        public void Write(byte[] src, int count)
        {
            if (_len + count > _buf.Length)
                Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + count));
            Buffer.BlockCopy(src, 0, _buf, _len, count);
            _len += count;
        }

        public string ToUtf8() => Encoding.UTF8.GetString(_buf, 0, _len);

        public void Reset() => _len = 0;
    }
}
