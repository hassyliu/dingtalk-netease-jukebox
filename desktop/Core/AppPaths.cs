using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DianGeJi.Core;

/// <summary>
/// 文件位置：
///   %APPDATA%\点歌机\        设置、登录凭证（加密）、播放列表
///   %LOCALAPPDATA%\点歌机\   歌曲缓存、日志
/// </summary>
public static class AppPaths
{
    public const string AppName = "点歌机";

    // 测试时可以用环境变量 DIANGEJI_HOME 把所有数据放到别的目录
    private static readonly string? Override = Environment.GetEnvironmentVariable("DIANGEJI_HOME");

    public static string Roaming { get; } = Ensure(Override ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName));
    public static string Local { get; } = Ensure(Override ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName));
    public static string Cache { get; } = Ensure(Path.Combine(Local, "cache"));
    public static string Logs { get; } = Ensure(Path.Combine(Local, "logs"));

    public static string Settings => Path.Combine(Roaming, "settings.json");
    public static string State => Path.Combine(Roaming, "state.json");
    public static string NeteaseSession => Path.Combine(Roaming, "netease.dat");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>把文本写到临时文件再替换，避免写一半断电导致文件损坏。</summary>
    public static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>用 Windows DPAPI 加密：只有当前 Windows 用户能解开。</summary>
public static class Secret
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DianGeJi/v1");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain))
            return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher))
            return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception e)
        {
            Log.Warn("解密失败（可能是换了 Windows 账号）：" + e.Message);
            return "";
        }
    }
}

/// <summary>简单的文件日志。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(AppPaths.Logs, "点歌机.log");

    public static event Action<string>? Written;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? e = null) => Write("ERROR", e == null ? msg : $"{msg}: {e}");

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}";
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
        }
        System.Diagnostics.Debug.WriteLine(line);
        Written?.Invoke(line);
    }
}
