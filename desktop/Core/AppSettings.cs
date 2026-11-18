using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DianGeJi.Core;

/// <summary>
/// 用户设置，保存在 %APPDATA%\点歌机\settings.json。
/// Client Secret 用 DPAPI 加密后存储（ClientSecretProtected），内存里才是明文。
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();

    // ---- 钉钉 ----
    public string ClientId { get; set; } = "";
    public string ClientSecretProtected { get; set; } = "";
    [JsonIgnore] public string ClientSecret { get; set; } = "";

    // ---- 点歌规则 ----
    public List<string> Admins { get; set; } = new();
    public int MaxSongsPerUser { get; set; }
    public bool SkipTrial { get; set; } = true;
    public bool IdleInterrupt { get; set; }

    // ---- 播放 ----
    public string Quality { get; set; } = "exhigh";
    public int Volume { get; set; } = 70;
    public string? Device { get; set; }
    public long? IdlePlaylistId { get; set; }
    public string IdlePlaylistName { get; set; } = "";

    // ---- 窗口 ----
    public bool CloseToTray { get; set; } = true;

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(AppPaths.Settings)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings), Json) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception e)
        {
            Log.Error("读取设置失败，使用默认设置", e);
            s = new AppSettings();
        }
        s.ClientSecret = Secret.Unprotect(s.ClientSecretProtected);
        if (!DianGeJi.Core.Quality.Levels.ContainsKey(s.Quality))
            s.Quality = "exhigh";
        return s;
    }

    public void Save()
    {
        lock (_gate)
        {
            ClientSecretProtected = Secret.Protect(ClientSecret);
            try
            {
                AppPaths.WriteAtomic(AppPaths.Settings, JsonSerializer.Serialize(this, Json));
            }
            catch (Exception e)
            {
                Log.Error("保存设置失败", e);
            }
        }
    }

    public bool IsAdmin(string name, string id)
        => Admins.Count == 0 || Admins.Contains(name) || (id != "" && Admins.Contains(id));
}

public static class Quality
{
    /// <summary>音质代号 → (显示名, 码率)</summary>
    public static readonly Dictionary<string, (string Name, int Bitrate)> Levels = new()
    {
        ["standard"] = ("标准 128k", 128000),
        ["higher"] = ("较高 192k", 192000),
        ["exhigh"] = ("极高 320k", 320000),
        ["lossless"] = ("无损 FLAC", 999000),
    };
}
