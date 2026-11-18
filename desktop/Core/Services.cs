using NAudio.MediaFoundation;

namespace DianGeJi.Core;

/// <summary>程序里的各个服务：设置、网易云、点歌机、钉钉机器人。</summary>
public sealed class Services
{
    public AppSettings Settings { get; }
    public NcmClient Ncm { get; }
    public Jukebox Jukebox { get; }
    public DingTalkBot Bot { get; }

    private Services(AppSettings settings, NcmClient ncm, Jukebox jukebox, DingTalkBot bot)
    {
        Settings = settings;
        Ncm = ncm;
        Jukebox = jukebox;
        Bot = bot;
    }

    public static Services Start()
    {
        Log.Info("点歌机启动");
        MediaFoundationApi.Startup();
        var settings = AppSettings.Load();
        var ncm = new NcmClient();
        var jukebox = new Jukebox(settings, ncm);
        var bot = new DingTalkBot(settings, jukebox, ncm);
        jukebox.Start();
        bot.Start();
        return new Services(settings, ncm, jukebox, bot);
    }

    public async Task ShutdownAsync()
    {
        await Bot.StopAsync();
        Jukebox.Shutdown();
        Log.Info("点歌机退出");
    }
}
