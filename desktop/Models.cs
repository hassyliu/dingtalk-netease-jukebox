using System.Text.Json.Serialization;

namespace DianGeJi;

public static class Fmt
{
    public static string Time(double sec)
    {
        var s = Math.Max(0, (int)sec);
        return $"{s / 60}:{s % 60:00}";
    }

    public static string Song(Song s) => $"《{s.Name}》- {s.Artists}";
}

public class Song
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Artists { get; set; } = "";
    public string? Album { get; set; }
    public string? Cover { get; set; }
    public int Duration { get; set; }
    public int Fee { get; set; }
    public string? Uid { get; set; }
    public string? Requester { get; set; }
    public string? RequesterId { get; set; }
    public string? Source { get; set; }

    [JsonIgnore] public int Index { get; set; }
    [JsonIgnore] public string? Thumb => string.IsNullOrEmpty(Cover) ? null : Cover + "?param=96y96";
    [JsonIgnore] public string DurationText => Fmt.Time(Duration);
    [JsonIgnore] public string Subtitle => Artists + (string.IsNullOrEmpty(Album) ? "" : " · " + Album);
    [JsonIgnore] public string RequesterText => Requester ?? "";
    [JsonIgnore] public bool IsVip => Fee == 1;

    public Song Clone() => (Song)MemberwiseClone();
}

public class IdleInfo
{
    public long? Id { get; set; }
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

public class EventItem
{
    public string T { get; set; } = "";
    public string Text { get; set; } = "";
}

public class Account
{
    public long Uid { get; set; }
    public string Nickname { get; set; } = "";
    public string? Avatar { get; set; }
    public bool Vip { get; set; }
}

public class AppState
{
    public Song? Current { get; set; }
    public bool Loading { get; set; }
    public bool Paused { get; set; }
    public double Position { get; set; }
    public int Volume { get; set; }
    public string? Device { get; set; }
    public string? PlayerError { get; set; }
    public string Quality { get; set; } = "exhigh";
    public List<Song> Queue { get; set; } = new();
    public List<Song> History { get; set; } = new();
    public IdleInfo Idle { get; set; } = new();
    public List<EventItem> Events { get; set; } = new();
}

public class Playlist
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public string? Cover { get; set; }
    public string? Thumb => string.IsNullOrEmpty(Cover) ? null : Cover + "?param=96y96";
    public string CountText => $"{Count} 首";
}
