using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DianGeJi.Core;

/// <summary>
/// 本地播放器：Media Foundation 解码（mp3 / flac / m4a）→ WASAPI 输出到指定声卡。
/// 一次只放一个文件；自然播完时触发 TrackEnded。所有方法线程安全。
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private readonly object _gate = new();
    private MediaFoundationReader? _reader;
    private VolumeSampleProvider? _volumeProvider;
    private WasapiOut? _output;
    private int _volume = 70;

    public event Action? TrackEnded;

    public string? DeviceName { get; private set; }
    public string? Error { get; private set; }
    public string? Path { get; private set; }

    public bool IsActive
    {
        get { lock (_gate) return _reader != null; }
    }

    public bool IsPaused
    {
        get { lock (_gate) return _output?.PlaybackState == PlaybackState.Paused; }
    }

    public double Position
    {
        get
        {
            lock (_gate)
                return _reader?.CurrentTime.TotalSeconds ?? 0;
        }
    }

    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            lock (_gate)
                if (_volumeProvider != null)
                    _volumeProvider.Volume = Gain;
        }
    }

    // 人耳对音量是对数感知，用平方曲线让滑块更均匀
    private float Gain => (float)Math.Pow(_volume / 100.0, 2);

    public static List<string> ListDevices()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d => d.FriendlyName).ToList();
        }
        catch (Exception e)
        {
            Log.Error("枚举音频设备失败", e);
            return new List<string>();
        }
    }

    public void SetDeviceName(string? name) => DeviceName = string.IsNullOrEmpty(name) ? null : name;

    public void Play(string path, double startSec = 0)
    {
        lock (_gate)
        {
            StopLocked();
            Error = null;
            Path = path;
            _reader = new MediaFoundationReader(path);
            if (startSec > 0)
                _reader.CurrentTime = TimeSpan.FromSeconds(startSec);
            StartOutputLocked(playing: true);
        }
    }

    public void Pause()
    {
        lock (_gate)
            if (_output?.PlaybackState == PlaybackState.Playing)
                _output.Pause();
    }

    public void Resume()
    {
        lock (_gate)
            if (_output?.PlaybackState == PlaybackState.Paused)
                _output.Play();
    }

    public void Seek(double sec)
    {
        lock (_gate)
        {
            if (_reader == null)
                return;
            sec = Math.Clamp(sec, 0, Math.Max(0, _reader.TotalTime.TotalSeconds - 0.5));
            _reader.CurrentTime = TimeSpan.FromSeconds(sec);
        }
    }

    /// <summary>切换输出设备；正在播放时从当前位置接着放。</summary>
    public void SetDevice(string? name)
    {
        lock (_gate)
        {
            SetDeviceName(name);
            Error = null;
            if (_reader == null)
                return;
            var playing = _output?.PlaybackState != PlaybackState.Paused;
            DisposeOutputLocked();
            StartOutputLocked(playing);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
            Path = null;
        }
    }

    private void StartOutputLocked(bool playing)
    {
        var device = ResolveDevice(DeviceName);
        try
        {
            BuildOutputLocked(device);
        }
        catch (Exception e) when (DeviceName != null)
        {
            Log.Warn($"无法使用设备「{DeviceName}」：{e.Message}");
            Error = $"设备「{DeviceName}」无法使用，已改用系统默认设备";
            device?.Dispose();
            BuildOutputLocked(null);
        }
        if (playing)
            _output!.Play();
        else
            _output!.Pause();
    }

    private void BuildOutputLocked(MMDevice? device)
    {
        device ??= DefaultDevice();
        ISampleProvider sp = _reader!.ToSampleProvider();
        if (sp.WaveFormat.Channels == 1)
            sp = new MonoToStereoSampleProvider(sp);
        var mixRate = device.AudioClient.MixFormat.SampleRate;
        if (sp.WaveFormat.SampleRate != mixRate)
            sp = new WdlResamplingSampleProvider(sp, mixRate);
        _volumeProvider = new VolumeSampleProvider(sp) { Volume = Gain };
        var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 120);
        output.PlaybackStopped += OnPlaybackStopped;
        output.Init(_volumeProvider);
        _output = output;
    }

    private MMDevice? ResolveDevice(string? name)
    {
        if (name == null)
            return null;
        using var en = new MMDeviceEnumerator();
        var d = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).FirstOrDefault(x => x.FriendlyName == name);
        if (d == null)
        {
            Error = $"设备「{name}」不可用，已改用系统默认设备";
            Log.Warn(Error);
        }
        return d;
    }

    private static MMDevice DefaultDevice()
    {
        using var en = new MMDeviceEnumerator();
        return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        bool natural;
        lock (_gate)
        {
            // 只处理当前输出的事件；切歌 / 换设备时旧输出的事件忽略
            natural = sender == _output && _output != null;
            if (natural)
            {
                if (e.Exception != null)
                {
                    Error = "播放出错：" + e.Exception.Message;
                    Log.Error("播放出错", e.Exception);
                }
                StopLocked();
                Path = null;
            }
        }
        if (natural)
            Task.Run(() => TrackEnded?.Invoke());
    }

    private void DisposeOutputLocked()
    {
        var o = _output;
        _output = null;
        _volumeProvider = null;
        if (o != null)
        {
            o.PlaybackStopped -= OnPlaybackStopped;
            try
            {
                o.Stop();
            }
            catch (Exception)
            {
            }
            o.Dispose();
        }
    }

    private void StopLocked()
    {
        DisposeOutputLocked();
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose() => Stop();
}
