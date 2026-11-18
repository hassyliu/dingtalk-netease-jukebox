using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DianGeJi.Core;
using QRCoder;

namespace DianGeJi;

public enum BotState
{
    None,
    Ok,
    Warn,
    Error,
}

public partial class MainViewModel : ObservableObject
{
    public const string DefaultDevice = "系统默认设备";
    private const string Me = "桌面端";

    private readonly Services _svc;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _tick;
    private bool _applying; // 正在把后台状态同步到界面，此时不要把改动再写回后台
    private DateTime _volumeTouched = DateTime.MinValue;
    private DateTime _lastStateAt = DateTime.Now;
    private string _queueKey = "", _historyKey = "", _eventsKey = "";
    private double _serverPosition;
    private Account? _account;

    /// <summary>界面提示：(消息, 是否错误)</summary>
    public event Action<string, bool>? Toast;

    public MainViewModel(Services svc)
    {
        _svc = svc;
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _poll.Tick += (_, _) => Refresh();
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => AdvancePosition();
    }

    private Jukebox Jb => _svc.Jukebox;
    private NcmClient Ncm => _svc.Ncm;
    private AppSettings Cfg => _svc.Settings;

    public void Start()
    {
        foreach (var kv in Quality.Levels)
            Qualities.Add(new KeyValuePair<string, string>(kv.Key, kv.Value.Name));
        LoadDevices(false);
        LoadSettings();
        Refresh();
        _ = RefreshAccountAsync();
        _poll.Start();
        _tick.Start();
    }

    // ================= 页面 =================
    [ObservableProperty] private int _pageIndex;

    partial void OnPageIndexChanged(int value)
    {
        if (value == 2 && IsLoggedIn && MyPlaylists.Count == 0)
            _ = LoadMyPlaylistsAsync();
        if (value == 4)
            LoadSettings();
    }

    // ================= 正在播放 =================
    [ObservableProperty] private bool _hasCurrent;
    [ObservableProperty] private string _title = "没有在播放";
    [ObservableProperty] private string _subtitle = "在钉钉群里 @机器人 点歌，或者在「搜索点歌」里点歌";
    [ObservableProperty] private string _requesterText = "";
    [ObservableProperty] private string? _coverUrl;
    [ObservableProperty] private bool _isPaused = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration = 1;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private string? _playerError;
    [ObservableProperty] private int _volume = 70;
    [ObservableProperty] private string _trayText = "点歌机";

    /// <summary>用户正在拖动进度条</summary>
    public bool IsSeeking { get; set; }

    partial void OnPositionChanged(double value)
    {
        if (IsSeeking)
            PositionText = Fmt.Time(value);
    }

    partial void OnVolumeChanged(int value)
    {
        if (_applying)
            return;
        _volumeTouched = DateTime.Now;
        _ = DebouncedVolumeAsync(value);
    }

    private int _volumeVersion;

    private async Task DebouncedVolumeAsync(int value)
    {
        var v = ++_volumeVersion;
        await Task.Delay(200);
        if (v == _volumeVersion)
            await RunAsync(() => Task.Run(() => Jb.SetVolume(value)));
    }

    [RelayCommand]
    private async Task TogglePlayAsync()
    {
        var resume = IsPaused || !HasCurrent;
        IsPaused = !resume;
        await RunAsync(() => Task.Run(() =>
        {
            if (resume)
                Jb.Resume();
            else
                Jb.Pause();
        }));
        Refresh();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        await RunAsync(() => Task.Run(Jb.Next));
        Refresh();
    }

    public async Task SeekAsync(double sec)
    {
        Position = sec;
        _serverPosition = sec;
        _lastStateAt = DateTime.Now;
        await RunAsync(() => Task.Run(() => Jb.Seek(sec)));
    }

    private void AdvancePosition()
    {
        if (!HasCurrent || IsSeeking)
            return;
        var pos = _serverPosition;
        if (!IsPaused && !IsLoading)
            pos += (DateTime.Now - _lastStateAt).TotalSeconds;
        pos = Math.Min(pos, Duration);
        Position = pos;
        PositionText = Fmt.Time(pos);
    }

    // ================= 输出设备 / 音质 =================
    public ObservableCollection<string> Devices { get; } = new() { DefaultDevice };
    [ObservableProperty] private string? _selectedDevice = DefaultDevice;

    partial void OnSelectedDeviceChanged(string? value)
    {
        if (_applying || value == null)
            return;
        var name = value == DefaultDevice ? null : value;
        _ = RunAsync(async () =>
        {
            await Task.Run(() => Jb.SetDevice(name));
            Toast?.Invoke($"已切换到：{value}", false);
        });
    }

    [RelayCommand]
    private void RefreshDevices() => LoadDevices(true);

    private void LoadDevices(bool announce)
    {
        var list = AudioPlayer.ListDevices();
        _applying = true;
        try
        {
            var keep = SelectedDevice;
            Devices.Clear();
            Devices.Add(DefaultDevice);
            foreach (var d in list)
                Devices.Add(d);
            SelectedDevice = keep != null && Devices.Contains(keep) ? keep : DefaultDevice;
        }
        finally
        {
            _applying = false;
        }
        if (announce)
            Toast?.Invoke($"找到 {list.Count} 个输出设备", false);
    }

    public ObservableCollection<KeyValuePair<string, string>> Qualities { get; } = new();
    [ObservableProperty] private string? _selectedQuality;

    partial void OnSelectedQualityChanged(string? value)
    {
        if (_applying || value == null)
            return;
        _ = RunAsync(() =>
        {
            Jb.SetQuality(value);
            Toast?.Invoke("音质已切换，下一首生效", false);
            return Task.CompletedTask;
        });
    }

    // ================= 播放列表 =================
    public ObservableCollection<Song> Queue { get; } = new();
    [ObservableProperty] private string _queueCountText = "";
    [ObservableProperty] private bool _isQueueEmpty = true;
    [ObservableProperty] private string _queueEmptyHint = "";

    [RelayCommand]
    private Task RemoveAsync(Song s) => QueueOpAsync(() => Jb.Remove(s.Uid!));

    [RelayCommand]
    private Task TopAsync(Song s) => QueueOpAsync(() => Jb.Move(s.Uid!, 0));

    [RelayCommand]
    private Task MoveUpAsync(Song s) => MoveToAsync(s, Math.Max(0, Queue.IndexOf(s) - 1));

    [RelayCommand]
    private Task MoveDownAsync(Song s) => MoveToAsync(s, Queue.IndexOf(s) + 1);

    public Task MoveToAsync(Song s, int to) => QueueOpAsync(() => Jb.Move(s.Uid!, to));

    [RelayCommand]
    private async Task ClearQueueAsync()
    {
        if (Queue.Count == 0)
            return;
        var ok = System.Windows.MessageBox.Show($"确定清空播放列表（{Queue.Count} 首）？", "点歌机",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (ok == System.Windows.MessageBoxResult.OK)
            await QueueOpAsync(Jb.Clear);
    }

    private async Task QueueOpAsync(Action op)
    {
        await RunAsync(() =>
        {
            op();
            return Task.CompletedTask;
        });
        _queueKey = "";
        Refresh();
    }

    // ================= 搜索 =================
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _searchHint = "输入歌名、歌手，或粘贴网易云分享链接";
    public ObservableCollection<Song> SearchResults { get; } = new();

    [RelayCommand]
    private async Task SearchAsync()
    {
        var q = SearchText.Trim();
        if (q == "" || IsSearching)
            return;
        IsSearching = true;
        SearchHint = "搜索中…";
        SearchResults.Clear();
        var ok = await RunAsync(async () =>
        {
            List<Song> list;
            var id = NcmClient.LooksLikeLink(q) ? await Ncm.ParseIdAsync(q, "song") : null;
            if (id != null)
            {
                var s = await Ncm.SongDetailAsync(id.Value);
                list = s == null ? new() : new() { s };
            }
            else
            {
                list = await Ncm.SearchAsync(q, 30);
            }
            foreach (var s in list)
                SearchResults.Add(s);
            SearchHint = list.Count == 0 ? "没有搜到结果" : "";
        });
        if (!ok)
            SearchHint = "搜索失败";
        IsSearching = false;
    }

    [RelayCommand]
    private Task AddAsync(Song s) => AddSongAsync(s, false);

    [RelayCommand]
    private Task InsertAsync(Song s) => AddSongAsync(s, true);

    private async Task AddSongAsync(Song s, bool top)
    {
        await RunAsync(async () =>
        {
            var pos = await Jb.AddAsync(s, Me, top: top);
            Toast?.Invoke($"已加入第 {pos} 位：{s.Name}", false);
        });
        Refresh();
    }

    // ================= 歌单 =================
    [ObservableProperty] private string _playlistInput = "";
    [ObservableProperty] private bool _shuffleImport = true;
    [ObservableProperty] private string _idleText = "未设置";
    [ObservableProperty] private bool _hasIdle;
    [ObservableProperty] private bool _isLoadingPlaylists;
    public ObservableCollection<Playlist> MyPlaylists { get; } = new();

    private async Task<long?> PlaylistIdFromInputAsync()
    {
        var raw = PlaylistInput.Trim();
        if (raw == "")
        {
            Toast?.Invoke("请先填写歌单链接或 ID", true);
            return null;
        }
        var id = await Ncm.ParseIdAsync(raw, "playlist");
        if (id == null)
            Toast?.Invoke("无法识别歌单链接或 ID", true);
        return id;
    }

    [RelayCommand]
    private async Task ImportPlaylistAsync()
    {
        if (await PlaylistIdFromInputAsync() is long id)
            await ImportAsync(id);
    }

    [RelayCommand]
    private async Task SetIdleAsync()
    {
        if (await PlaylistIdFromInputAsync() is long id)
            await SetIdlePlaylistAsync(id);
    }

    [RelayCommand]
    private Task IdleOffAsync() => RunAsync(async () =>
    {
        await Jb.SetIdlePlaylistAsync(null);
        Toast?.Invoke("已关闭空闲歌单", false);
    });

    [RelayCommand]
    private Task ImportMyAsync(Playlist p) => ImportAsync(p.Id);

    [RelayCommand]
    private Task IdleMyAsync(Playlist p) => SetIdlePlaylistAsync(p.Id);

    private async Task ImportAsync(long id)
    {
        await RunAsync(async () =>
        {
            var (name, songs) = await Ncm.PlaylistAsync(id);
            var n = Jb.AddMany(songs, $"歌单·{name}", ShuffleImport);
            Toast?.Invoke($"已从「{name}」导入 {n} 首", false);
        });
        Refresh();
    }

    private Task SetIdlePlaylistAsync(long id) => RunAsync(async () =>
    {
        await Jb.SetIdlePlaylistAsync(id);
        Toast?.Invoke($"空闲歌单已设置为「{Cfg.IdlePlaylistName}」", false);
    });

    [RelayCommand]
    private async Task LoadMyPlaylistsAsync()
    {
        if (_account == null)
            return;
        IsLoadingPlaylists = true;
        await RunAsync(async () =>
        {
            var list = await Ncm.UserPlaylistsAsync(_account.Uid);
            MyPlaylists.Clear();
            foreach (var p in list)
                MyPlaylists.Add(p);
        });
        IsLoadingPlaylists = false;
    }

    // ================= 最近播放 / 动态 =================
    public ObservableCollection<Song> History { get; } = new();
    public ObservableCollection<EventItem> Events { get; } = new();

    // ================= 网易云账号 =================
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private string _accountName = "未登录";
    [ObservableProperty] private string? _accountAvatar;
    [ObservableProperty] private string _accountDetail = "登录 VIP 账号后才能播放 VIP 歌曲";
    [ObservableProperty] private bool _isVip;

    public event Action? LoginRequested;

    [RelayCommand]
    private void Login() => LoginRequested?.Invoke();

    [RelayCommand]
    private void Logout()
    {
        var ok = System.Windows.MessageBox.Show("确定退出网易云账号？", "点歌机",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.OK)
            return;
        Ncm.Logout();
        MyPlaylists.Clear();
        ApplyAccount(null);
        Jb.Event("网易云已退出登录");
    }

    private async Task RefreshAccountAsync()
    {
        try
        {
            ApplyAccount(await Ncm.AccountAsync());
        }
        catch (Exception e)
        {
            Log.Warn("获取网易云账号信息失败：" + e.Message);
        }
    }

    private void ApplyAccount(Account? a)
    {
        _account = a;
        IsLoggedIn = a != null;
        IsVip = a?.Vip ?? false;
        AccountName = a?.Nickname ?? "未登录";
        AccountAvatar = string.IsNullOrEmpty(a?.Avatar) ? null : a!.Avatar + "?param=80y80";
        AccountDetail = a == null ? "登录 VIP 账号后才能播放 VIP 歌曲"
            : a.Vip ? "黑胶 VIP · 可以播放 VIP 歌曲" : "非 VIP · VIP 歌曲只能试听";
    }

    /// <summary>生成登录二维码，返回 (key, PNG 图片)。</summary>
    public async Task<(string Key, byte[] Png)> CreateQrAsync()
    {
        var (key, url) = await Ncm.QrCreateAsync();
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        return (key, png);
    }

    public Task<int> CheckQrAsync(string key) => Ncm.QrCheckAsync(key);

    public async Task OnLoggedInAsync()
    {
        await RefreshAccountAsync();
        Jb.Event($"网易云已登录：{AccountName}");
        Toast?.Invoke("登录成功：" + AccountName, false);
        await LoadMyPlaylistsAsync();
    }

    // ================= 钉钉 & 设置 =================
    [ObservableProperty] private string _botStatus = "";
    [ObservableProperty] private string _botStatusShort = "";
    [ObservableProperty] private BotState _botState;
    [ObservableProperty] private bool _showSetupBar;
    [ObservableProperty] private bool _showBotErrorBar;

    [ObservableProperty] private string _clientId = "";
    [ObservableProperty] private string _clientSecret = "";
    [ObservableProperty] private string _secretPlaceholder = "";
    [ObservableProperty] private string _admins = "";
    [ObservableProperty] private double? _maxSongs = 0;
    [ObservableProperty] private bool _skipTrial = true;
    [ObservableProperty] private bool _idleInterrupt;
    [ObservableProperty] private bool _closeToTray = true;

    private void LoadSettings()
    {
        ClientId = Cfg.ClientId;
        ClientSecret = "";
        SecretPlaceholder = Cfg.ClientSecret != "" ? "已保存，留空表示不修改" : "";
        Admins = string.Join("，", Cfg.Admins);
        MaxSongs = Cfg.MaxSongsPerUser;
        SkipTrial = Cfg.SkipTrial;
        IdleInterrupt = Cfg.IdleInterrupt;
        CloseToTray = Cfg.CloseToTray;
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var newId = ClientId.Trim();
        // Client ID 和 Secret 是一对：换了 ID 就必须重新填 Secret，不能沿用旧的
        if (newId != Cfg.ClientId && newId != "" && ClientSecret.Trim() == "")
        {
            Toast?.Invoke("修改了 Client ID，请同时填写对应的 Client Secret", true);
            return;
        }
        var newSecret = ClientSecret.Trim() == "" ? Cfg.ClientSecret : ClientSecret.Trim();
        var botChanged = newId != Cfg.ClientId || newSecret != Cfg.ClientSecret;
        Cfg.ClientId = newId;
        Cfg.ClientSecret = newSecret;
        Cfg.Admins = Admins.Split(new[] { ',', '，', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Cfg.MaxSongsPerUser = Math.Max(0, (int)(MaxSongs ?? 0));
        Cfg.SkipTrial = SkipTrial;
        Cfg.IdleInterrupt = IdleInterrupt;
        Cfg.CloseToTray = CloseToTray;
        Cfg.Save();
        if (botChanged)
            await _svc.Bot.RestartAsync();
        Jb.Event("设置已保存" + (botChanged ? "，钉钉机器人正在重新连接" : ""));
        Toast?.Invoke(botChanged ? "已保存，正在连接钉钉…" : "设置已保存", false);
        LoadSettings();
        Refresh();
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        // 框里改过凭证就先保存（保存时会自动重连），避免拿旧凭证去连
        if (ClientSecret.Trim() != "" || ClientId.Trim() != Cfg.ClientId)
        {
            await SaveSettingsAsync();
            return;
        }
        await _svc.Bot.RestartAsync();
        Toast?.Invoke("正在重新连接钉钉…", false);
        Refresh();
    }

    [RelayCommand]
    private void GoSettings() => PageIndex = 4;

    [RelayCommand]
    private void OpenDataFolder() => OpenShell(AppPaths.Roaming);

    [RelayCommand]
    private void OpenLogFolder() => OpenShell(AppPaths.Logs);

    [RelayCommand]
    private void OpenDevPortal() => OpenShell("https://open-dev.dingtalk.com/");

    private static void OpenShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    // ================= 状态同步 =================
    public void Refresh()
    {
        Apply(Jb.Snapshot());
        ApplyBot(_svc.Bot.Status);
    }

    private void ApplyBot(string b)
    {
        BotStatus = b;
        BotStatusShort = b.Split('（')[0];
        BotState = b == "已连接" ? BotState.Ok
            : b.StartsWith("未配置") ? BotState.None
            : b.Contains("失败") ? BotState.Error
            : BotState.Warn;
        ShowSetupBar = b.StartsWith("未配置");
        ShowBotErrorBar = b.Contains("失败");
    }

    private void Apply(AppState st)
    {
        _applying = true;
        try
        {
            var cur = st.Current;
            HasCurrent = cur != null;
            IsLoading = st.Loading;
            IsPaused = st.Paused || cur == null;
            if (cur != null)
            {
                Title = cur.Name;
                Subtitle = cur.Subtitle;
                RequesterText = (st.Loading ? "正在加载 · " : "") + cur.Requester + " 点播";
                CoverUrl = string.IsNullOrEmpty(cur.Cover) ? null : cur.Cover + "?param=300y300";
                Duration = Math.Max(1, cur.Duration);
                DurationText = Fmt.Time(cur.Duration);
                TrayText = $"{(st.Paused ? "已暂停" : "正在播放")}：{cur.Name} - {cur.Artists}";
            }
            else
            {
                Title = "没有在播放";
                Subtitle = "在钉钉群里 @机器人 点歌，或者在「搜索点歌」里点歌";
                RequesterText = "";
                CoverUrl = null;
                Duration = 1;
                DurationText = "0:00";
                TrayText = "点歌机";
            }
            _serverPosition = st.Position;
            _lastStateAt = DateTime.Now;
            if (!IsSeeking)
            {
                Position = Math.Min(st.Position, Duration);
                PositionText = Fmt.Time(Position);
            }
            PlayerError = string.IsNullOrEmpty(st.PlayerError) ? null : st.PlayerError;
            if ((DateTime.Now - _volumeTouched).TotalSeconds > 2)
                Volume = st.Volume;

            var dev = string.IsNullOrEmpty(st.Device) ? DefaultDevice : st.Device;
            if (!Devices.Contains(dev))
                Devices.Add(dev);
            SelectedDevice = dev;
            SelectedQuality = st.Quality;

            // 列表只在变化时重建，避免闪烁
            var qk = string.Join(",", st.Queue.Select(s => s.Uid));
            if (qk != _queueKey)
            {
                _queueKey = qk;
                Queue.Clear();
                var i = 1;
                foreach (var s in st.Queue)
                {
                    s.Index = i++;
                    Queue.Add(s);
                }
            }
            IsQueueEmpty = st.Queue.Count == 0;
            QueueCountText = st.Queue.Count == 0 ? "" : $"{st.Queue.Count} 首";
            QueueEmptyHint = st.Idle.Id != null
                ? $"没人点歌时会随机播放空闲歌单「{st.Idle.Name}」"
                : "在钉钉群里 @机器人 点歌，或者到「搜索点歌」里手动点歌";

            var hk = string.Join(",", st.History.Select(s => s.Uid));
            if (hk != _historyKey)
            {
                _historyKey = hk;
                History.Clear();
                foreach (var s in st.History)
                    History.Add(s);
            }
            var ek = st.Events.Count == 0 ? "" : st.Events[0].T + st.Events[0].Text + st.Events.Count;
            if (ek != _eventsKey)
            {
                _eventsKey = ek;
                Events.Clear();
                foreach (var e in st.Events)
                    Events.Add(e);
            }

            HasIdle = st.Idle.Id != null;
            IdleText = HasIdle ? $"「{st.Idle.Name}」· {st.Idle.Count} 首" : "未设置";
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>执行操作，出错时弹提示。返回是否成功。</summary>
    private async Task<bool> RunAsync(Func<Task> op)
    {
        try
        {
            await op();
            return true;
        }
        catch (Exception e) when (e is JukeboxException or NcmException)
        {
            Toast?.Invoke(e.Message, true);
        }
        catch (Exception e)
        {
            Log.Error("操作失败", e);
            Toast?.Invoke("操作失败：" + e.Message, true);
        }
        return false;
    }
}
