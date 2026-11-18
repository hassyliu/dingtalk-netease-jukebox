using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace DianGeJi;

public partial class QrLoginWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _key;
    private bool _checking;

    public QrLoginWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        _timer.Tick += async (_, _) => await CheckAsync();
        Loaded += async (_, _) => await LoadQrAsync();
        Closed += (_, _) => _timer.Stop();
    }

    private async Task LoadQrAsync()
    {
        _timer.Stop();
        RefreshButton.Visibility = Visibility.Collapsed;
        QrImage.Source = null;
        Ring.Visibility = Visibility.Visible;
        StatusText.Text = "正在获取二维码…";
        // 网络偶尔抖动，自动重试几次再报错
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var (key, png) = await _vm.CreateQrAsync();
                _key = key;
                QrImage.Source = ToImage(png);
                StatusText.Text = "请用网易云音乐 App 扫码";
                _timer.Start();
                break;
            }
            catch (Exception e)
            {
                Core.Log.Error($"获取登录二维码失败（第 {attempt} 次）", e);
                if (attempt < 3)
                {
                    await Task.Delay(1000);
                    continue;
                }
                var msg = string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message;
                StatusText.Text = "获取二维码失败：" + msg;
                RefreshButton.Visibility = Visibility.Visible;
            }
        }
        Ring.Visibility = Visibility.Collapsed;
    }

    private async Task CheckAsync()
    {
        if (_key == null || _checking)
            return;
        _checking = true;
        try
        {
            var code = await _vm.CheckQrAsync(_key);
            switch (code)
            {
                case 802:
                    StatusText.Text = "已扫码，请在手机上点「确认登录」";
                    break;
                case 800:
                    _timer.Stop();
                    StatusText.Text = "二维码已过期";
                    RefreshButton.Visibility = Visibility.Visible;
                    QrImage.Opacity = 0.25;
                    break;
                case 803:
                    _timer.Stop();
                    DialogResult = true;
                    break;
            }
        }
        catch (Exception)
        {
            // 网络抖动，下次再查
        }
        finally
        {
            _checking = false;
        }
    }

    private static BitmapImage ToImage(byte[] png)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = new MemoryStream(png);
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        QrImage.Opacity = 1;
        await LoadQrAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
