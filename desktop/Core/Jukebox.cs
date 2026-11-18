using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DianGeJi.Core;

public sealed class JukeboxException(string message) : Exception(message);

/// <summary>钉钉会话的回复地址（播放失败时用来通知点歌人）。</summary>
public sealed record Notify(string Webhook, long ExpireMs);

/// <summary>点歌机核心：播放队列、空闲歌单、下载缓存、状态持久化。所有公开方法线程安全。</summary>
public sealed class Jukebox
{
    private const int CacheKeep = 40;

    private static readonly JsonSerializerOptions StateJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly AppSettings _cfg;
    private readonly NcmClient _ncm;
    private readonly HttpClient _download = new(new HttpClientHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromMinutes(5),
    };
    private readonly HttpClient _notifyHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly List<Song> _queue = new();
    private readonly List<Song> _history = new();
    private readonly List<EventItem> _events = new();
    private readonly Dictionary<string, Notify> _notify = new();
    private readonly Dictionary<string, Task<string>> _downloads = new();
    private readonly AutoResetEvent _wake = new(false);

    private Song? _current;
    private bool _loading;
    private bool _skipLoading;
    private List<Song> _idleSongs = new();
    private readonly Queue<Song> _idlePool = new();
    private volatile bool _running;

    public AudioPlayer Player { get; } = new();

    public Jukebox(AppSettings cfg, NcmClient ncm)
    {
        _cfg = cfg;
        _ncm = ncm;
        Player.Volume = cfg.Volume;
        Player.SetDeviceName(cfg.Device);
        Player.TrackEnded += OnTrackEnded;
        LoadState();
    }

    // ================= 持久化 =================
    private sealed class StateFile
    {
        public List<Song> Queue { get; set; } = new();
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(AppPaths.State))
                return;
            var st = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(AppPaths.State), StateJson);
            foreach (var s in st?.Queue ?? new())
            {
                s.Uid = NewUid();
                _queue.Add(s);
            }
        }
        catch (Exception e)
        {
            Log.Error("读取播放列表失败", e);
        }
    }

    public void SaveState()
    {
        string json;
        lock (_gate)
        {
            var q = new List<Song>();
            if (_current != null && _current.Source != "idle")
                q.Add(_current);
            q.AddRange(_queue);
            json = JsonSerializer.Serialize(new StateFile { Queue = q }, StateJson);
        }
        try
        {
            AppPaths.WriteAtomic(AppPaths.State, json);
        }
        catch (Exception e)
        {
            Log.Error("保存播放列表失败", e);
        }
    }

    private void SaveSettings() => _cfg.Save();

    public void Event(string text)
    {
        Log.Info(text);
        lock (_gate)
        {
            _events.Insert(0, new EventItem { T = DateTime.Now.ToString("HH:mm:ss"), Text = text });
            if (_events.Count > 200)
                _events.RemoveAt(_events.Count - 1);
        }
    }

    // ================= 生命周期 =================
    public void Start()
    {
        _running = true;
        new Thread(Worker) { IsBackground = true, Name = "jukebox" }.Start();
        if (_cfg.IdlePlaylistId is long id)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await SetIdlePlaylistAsync(id, announce: false);
                }
                catch (Exception e)
                {
                    Event("空闲歌单加载失败：" + e.Message);
                }
            });
        }
    }

    public void Shutdown()
    {
        _running = false;
        SaveState();
        SaveSettings();
        Player.Stop();
        _wake.Set();
    }

    // ================= 播放调度 =================
    private void OnTrackEnded()
    {
        lock (_gate)
        {
            if (_current != null)
                PushHistory(_current);
            _current = null;
        }
        SaveState();
        _wake.Set();
    }

    private void PushHistory(Song s)
    {
        _history.Insert(0, s);
        if (_history.Count > 30)
            _history.RemoveAt(_history.Count - 1);
    }

    private Song? NextSong()
    {
        lock (_gate)
        {
            if (_queue.Count > 0)
            {
                var s = _queue[0];
                _queue.RemoveAt(0);
                return s;
            }
            if (_idlePool.Count == 0 && _idleSongs.Count > 0)
                foreach (var s in _idleSongs.OrderBy(_ => Random.Shared.Next()))
                    _idlePool.Enqueue(s);
            if (_idlePool.Count > 0)
            {
                var s = _idlePool.Dequeue().Clone();
                s.Uid = NewUid();
                s.Requester = $"空闲歌单·{_cfg.IdlePlaylistName}";
                s.RequesterId = "";
                s.Source = "idle";
                return s;
            }
        }
        return null;
    }

    private void Worker()
    {
        while (_running)
        {
            _wake.WaitOne(1000);
            if (!_running)
                break;
            lock (_gate)
                if (_loading || _current != null)
                    continue;
            if (Player.IsActive)
                continue;
            var song = NextSong();
            if (song != null)
                PlaySong(song);
        }
    }

    private void PlaySong(Song song)
    {
        lock (_gate)
        {
            _current = song;
            _loading = true;
            _skipLoading = false;
        }
        string path;
        try
        {
            path = EnsureFileAsync(song).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                if (_current == song)
                    _current = null;
                _loading = false;
            }
            var msg = $"{Fmt.Song(song)} 无法播放，已跳过：{e.Message}";
            Event(msg);
            _ = NotifyAsync(song, msg);
            SaveState();
            _wake.Set();
            return;
        }
        lock (_gate)
        {
            _loading = false;
            if (_skipLoading || _current != song)
            {
                _wake.Set();
                return;
            }
        }
        try
        {
            Player.Play(path);
        }
        catch (Exception e)
        {
            Log.Error("播放失败", e);
            lock (_gate)
                if (_current == song)
                    _current = null;
            TryDelete(path); // 文件可能损坏，下次重新下载
            Event($"{Fmt.Song(song)} 播放失败，已跳过：{e.Message}");
            _wake.Set();
            return;
        }
        Event($"开始播放 {Fmt.Song(song)}（{(string.IsNullOrEmpty(song.Requester) ? "未知" : song.Requester)} 点播）");
        SaveState();
        _ = Task.Run(PrefetchAsync);
    }

    private async Task PrefetchAsync()
    {
        Song? next;
        lock (_gate)
            next = _queue.Count > 0 ? _queue[0] : null;
        if (next == null)
            return;
        try
        {
            await EnsureFileAsync(next);
        }
        catch (Exception)
        {
            // 轮到它播放时会再试，并给出提示
        }
    }

    // ================= 下载缓存 =================
    private string? Cached(long id)
    {
        foreach (var f in Directory.EnumerateFiles(AppPaths.Cache, $"{id}_{_cfg.Quality}.*"))
            if (!f.EndsWith(".part"))
                return f;
        return null;
    }

    private Task<string> EnsureFileAsync(Song song)
    {
        var hit = Cached(song.Id);
        if (hit != null)
        {
            try { File.SetLastWriteTime(hit, DateTime.Now); } catch (IOException) { }
            return Task.FromResult(hit);
        }
        var key = $"{song.Id}_{_cfg.Quality}";
        lock (_gate)
        {
            if (_downloads.TryGetValue(key, out var running))
                return running;
            var t = DownloadAsync(song, _cfg.Quality);
            _downloads[key] = t;
            _ = t.ContinueWith(_ => { lock (_gate) _downloads.Remove(key); });
            return t;
        }
    }

    private async Task<string> DownloadAsync(Song song, string quality)
    {
        var info = await _ncm.SongUrlAsync(song.Id, quality);
        if (string.IsNullOrEmpty(info.Url))
            throw new JukeboxException("无版权或需要 VIP");
        if (info.Trial && _cfg.SkipTrial)
            throw new JukeboxException("只能试听 30 秒（需要 VIP 或单曲购买）");
        var ext = info.Type is "mp3" or "flac" or "m4a" or "aac" ? info.Type : "mp3";
        var dst = Path.Combine(AppPaths.Cache, $"{song.Id}_{quality}.{ext}");
        var part = dst + ".part";
        using (var resp = await _download.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(part);
            await resp.Content.CopyToAsync(fs);
        }
        File.Move(part, dst, overwrite: true);
        TrimCache();
        return dst;
    }

    private void TrimCache()
    {
        try
        {
            var playing = Player.Path;
            var files = new DirectoryInfo(AppPaths.Cache).GetFiles().OrderByDescending(f => f.LastWriteTime).Skip(CacheKeep);
            foreach (var f in files)
                if (!string.Equals(f.FullName, playing, StringComparison.OrdinalIgnoreCase))
                    TryDelete(f.FullName);
        }
        catch (IOException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }

    private async Task NotifyAsync(Song song, string text)
    {
        Notify? n;
        lock (_gate)
            _notify.TryGetValue(song.Uid ?? "", out n);
        if (n == null || n.ExpireMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            return;
        try
        {
            var body = JsonSerializer.Serialize(new { msgtype = "text", text = new { content = text } });
            await _notifyHttp.PostAsync(n.Webhook, new StringContent(body, Encoding.UTF8, "application/json"));
        }
        catch (Exception)
        {
        }
    }

    // ================= 对外操作 =================
    public static string NewUid() => Guid.NewGuid().ToString("N")[..8];

    public async Task CheckPlayableAsync(long id)
    {
        var info = await _ncm.SongUrlAsync(id, _cfg.Quality);
        if (string.IsNullOrEmpty(info.Url))
            throw new JukeboxException("这首歌无版权或需要 VIP，放不了");
        if (info.Trial && _cfg.SkipTrial)
            throw new JukeboxException("这首歌只能试听 30 秒（需要 VIP 或单曲购买），放不了");
    }

    /// <summary>加入队列，返回在队列中的位置（从 1 开始）。</summary>
    public async Task<int> AddAsync(Song song, string requester, string requesterId = "", string source = "app",
        bool top = false, Notify? notify = null, bool check = true)
    {
        lock (_gate)
        {
            var i = _queue.FindIndex(s => s.Id == song.Id);
            if (i >= 0)
                throw new JukeboxException($"{Fmt.Song(song)} 已经在列表第 {i + 1} 位了");
            if (_current != null && _current.Id == song.Id && source != "app")
                throw new JukeboxException($"{Fmt.Song(song)} 正在播放");
            if (_cfg.MaxSongsPerUser > 0 && source == "dingtalk" && requesterId != "")
            {
                var mine = _queue.Count(s => s.RequesterId == requesterId);
                if (mine >= _cfg.MaxSongsPerUser)
                    throw new JukeboxException($"你在列表里已经有 {mine} 首了，等播完再点吧");
            }
        }
        if (check)
            await CheckPlayableAsync(song.Id);

        var item = song.Clone();
        item.Uid = NewUid();
        item.Requester = requester;
        item.RequesterId = requesterId;
        item.Source = source;
        int pos;
        bool interrupt;
        lock (_gate)
        {
            if (notify != null)
                _notify[item.Uid] = notify;
            if (top)
            {
                _queue.Insert(0, item);
                pos = 1;
            }
            else
            {
                _queue.Add(item);
                pos = _queue.Count;
            }
            interrupt = _current?.Source == "idle" && _cfg.IdleInterrupt;
        }
        Event($"{requester} 点了 {Fmt.Song(song)}");
        SaveState();
        if (interrupt)
            Next();
        _wake.Set();
        if (pos == 1)
            _ = Task.Run(PrefetchAsync);
        return pos;
    }

    public int AddMany(IEnumerable<Song> songs, string requester, bool shuffle)
    {
        var list = shuffle ? songs.OrderBy(_ => Random.Shared.Next()).ToList() : songs.ToList();
        var n = 0;
        lock (_gate)
        {
            var exist = _queue.Select(s => s.Id).ToHashSet();
            foreach (var s in list)
            {
                if (!exist.Add(s.Id))
                    continue;
                var item = s.Clone();
                item.Uid = NewUid();
                item.Requester = requester;
                item.RequesterId = "";
                item.Source = "app";
                _queue.Add(item);
                n++;
            }
        }
        Event($"{requester} 导入了 {n} 首歌");
        SaveState();
        _wake.Set();
        return n;
    }

    /// <summary>切歌，返回被切掉的歌。</summary>
    public Song? Next()
    {
        Song? cur;
        lock (_gate)
        {
            cur = _current;
            if (_loading)
            {
                _skipLoading = true;
                _loading = false;
                _current = null;
            }
            else if (cur != null)
            {
                PushHistory(cur);
                _current = null;
            }
        }
        Player.Stop();
        SaveState();
        _wake.Set();
        return cur;
    }

    public void Pause() => Player.Pause();

    public void Resume()
    {
        Player.Resume();
        _wake.Set();
    }

    public void Seek(double sec) => Player.Seek(sec);

    public void SetVolume(int v)
    {
        Player.Volume = v;
        _cfg.Volume = Player.Volume;
        SaveSettings();
    }

    public void SetDevice(string? name)
    {
        Player.SetDevice(name);
        _cfg.Device = string.IsNullOrEmpty(name) ? null : name;
        SaveSettings();
        Event($"输出设备切换为：{(string.IsNullOrEmpty(name) ? "系统默认" : name)}");
    }

    public void SetQuality(string q)
    {
        if (!Quality.Levels.ContainsKey(q))
            throw new JukeboxException("未知音质");
        _cfg.Quality = q;
        SaveSettings();
    }

    private int Find(string uid)
    {
        var i = _queue.FindIndex(s => s.Uid == uid);
        return i >= 0 ? i : throw new JukeboxException("列表里没有这首歌（可能已经开始播放了）");
    }

    public Song Remove(string uid)
    {
        Song s;
        lock (_gate)
        {
            var i = Find(uid);
            s = _queue[i];
            _queue.RemoveAt(i);
        }
        SaveState();
        return s;
    }

    public void Move(string uid, int to)
    {
        lock (_gate)
        {
            var i = Find(uid);
            var s = _queue[i];
            _queue.RemoveAt(i);
            _queue.Insert(Math.Clamp(to, 0, _queue.Count), s);
        }
        SaveState();
    }

    public void Clear()
    {
        lock (_gate)
            _queue.Clear();
        Event("播放列表已清空");
        SaveState();
    }

    public async Task SetIdlePlaylistAsync(long? id, bool announce = true)
    {
        if (id == null)
        {
            lock (_gate)
            {
                _idleSongs = new();
                _idlePool.Clear();
            }
            _cfg.IdlePlaylistId = null;
            _cfg.IdlePlaylistName = "";
            SaveSettings();
            if (announce)
                Event("已关闭空闲歌单");
            return;
        }
        var (name, songs) = await _ncm.PlaylistAsync(id.Value);
        lock (_gate)
        {
            _idleSongs = songs;
            _idlePool.Clear();
        }
        _cfg.IdlePlaylistId = id;
        _cfg.IdlePlaylistName = name;
        SaveSettings();
        if (announce)
            Event($"空闲歌单设置为「{name}」（{songs.Count} 首）");
        _wake.Set();
    }

    // ================= 状态 =================
    public AppState Snapshot()
    {
        lock (_gate)
        {
            return new AppState
            {
                Current = _current?.Clone(),
                Loading = _loading,
                Paused = Player.IsPaused,
                Position = _current != null && !_loading ? Player.Position : 0,
                Volume = Player.Volume,
                Device = Player.DeviceName,
                PlayerError = Player.Error,
                Quality = _cfg.Quality,
                Queue = _queue.Select(s => s.Clone()).ToList(),
                History = _history.Take(15).Select(s => s.Clone()).ToList(),
                Idle = new IdleInfo { Id = _cfg.IdlePlaylistId, Name = _cfg.IdlePlaylistName, Count = _idleSongs.Count },
                Events = _events.Take(80).ToList(),
            };
        }
    }
}
