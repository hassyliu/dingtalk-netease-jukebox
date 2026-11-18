using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DianGeJi;

/// <summary>网络图片地址 → 图片（带缓存，空地址返回 null）。</summary>
public sealed class UrlToImageConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapImage> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
            return null;
        if (Cache.TryGetValue(url, out var img))
            return img;
        try
        {
            img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(url);
            img.CacheOption = BitmapCacheOption.OnLoad;
            if (parameter is string w && int.TryParse(w, out var px))
                img.DecodePixelWidth = px;
            img.EndInit();
            if (Cache.Count > 500)
                Cache.Clear();
            Cache[url] = img;
            return img;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>bool → Visibility；参数 "!" 表示取反。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter as string == "!")
            b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>非空 → Visible；参数 "!" 表示取反。</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            string s => s.Length > 0,
            int n => n > 0,
            _ => true,
        };
        if (parameter as string == "!")
            has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>页面序号 == 参数 → Visible。</summary>
public sealed class PageToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && parameter is string p && int.TryParse(p, out var want) && i == want
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>钉钉连接状态 → 指示灯颜色。</summary>
public sealed class BotStateToBrushConverter : IValueConverter
{
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x0F, 0x9D, 0x58));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xE3, 0x9C, 0x0B));
    private static readonly Brush Error = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
    private static readonly Brush None = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        BotState.Ok => Ok,
        BotState.Warn => Warn,
        BotState.Error => Error,
        _ => None,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>暂停状态 → 播放/暂停图标。</summary>
public sealed class PlayIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Wpf.Ui.Controls.SymbolRegular.Play24 : Wpf.Ui.Controls.SymbolRegular.Pause24;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>音量 → 喇叭图标。</summary>
public sealed class VolumeIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        0 => Wpf.Ui.Controls.SymbolRegular.SpeakerMute24,
        int v when v < 35 => Wpf.Ui.Controls.SymbolRegular.Speaker024,
        int v when v < 70 => Wpf.Ui.Controls.SymbolRegular.Speaker124,
        _ => Wpf.Ui.Controls.SymbolRegular.Speaker224,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
