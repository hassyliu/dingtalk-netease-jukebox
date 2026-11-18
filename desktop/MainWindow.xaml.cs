using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DianGeJi.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DianGeJi;

public partial class MainWindow : FluentWindow
{
    private Services? _svc;
    private MainViewModel? _vm;
    private TrayIcon? _tray;
    private bool _quitting;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            SystemThemeWatcher.Watch(this);
            await StartBackendAsync();
        };
    }

    // ================= 启动 =================
    private async Task StartBackendAsync()
    {
        ShowOverlay("正在启动…", busy: true);
        try
        {
            _svc ??= await Task.Run(Services.Start);
        }
        catch (Exception e)
        {
            Log.Error("启动失败", e);
            ShowOverlay("启动失败：" + e.Message, busy: false);
            return;
        }
        if (_vm == null)
        {
            _vm = new MainViewModel(_svc);
            _vm.Toast += ShowToast;
            _vm.LoginRequested += ShowLogin;
            _vm.PropertyChanged += OnVmPropertyChanged;
            DataContext = _vm;
            _tray = new TrayIcon(this, _vm);
            _vm.Start();
        }
        StartupOverlay.Visibility = Visibility.Collapsed;
    }

    private void ShowOverlay(string text, bool busy)
    {
        StartupOverlay.Visibility = Visibility.Visible;
        StartupText.Text = text;
        StartupRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StartupActions.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await StartBackendAsync();

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(AppPaths.Logs) { UseShellExecute = true });

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TrayText) && _vm != null)
            _tray?.SetText(_vm.TrayText);
    }

    // ================= 窗口 / 托盘 =================
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true; // 确保跑到最前面
        Topmost = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_quitting)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        if (_svc != null && _svc.Settings.CloseToTray && _tray != null)
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowHint("点歌机还在后台运行，音乐不会停。右键托盘图标可以退出。");
            }
        }
        else
        {
            _ = QuitAsync();
        }
    }

    public async Task QuitAsync()
    {
        if (_quitting)
            return;
        _quitting = true;
        Hide();
        _tray?.Dispose();
        if (_svc != null)
            await _svc.ShutdownAsync();
        Application.Current.Shutdown();
    }

    private void BotStatus_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vm != null)
            _vm.PageIndex = 4;
    }

    // ================= 提示 =================
    private void ShowToast(string message, bool isError)
    {
        var bar = new Snackbar(SnackbarPresenter)
        {
            Title = isError ? "出错了" : "提示",
            Content = message,
            Appearance = isError ? ControlAppearance.Danger : ControlAppearance.Secondary,
            Icon = new SymbolIcon(isError ? SymbolRegular.ErrorCircle24 : SymbolRegular.CheckmarkCircle24),
            Timeout = TimeSpan.FromSeconds(isError ? 5 : 3),
        };
        bar.Show();
    }

    private async void ShowLogin()
    {
        if (_vm == null)
            return;
        var dlg = new QrLoginWindow(_vm) { Owner = IsVisible ? this : null };
        if (dlg.ShowDialog() == true)
            await _vm.OnLoggedInAsync();
    }

    // ================= 进度条拖动 =================
    private void Seek_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm != null)
            _vm.IsSeeking = true;
    }

    private void Seek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null)
            return;
        // 等 Slider 先把值更新到点击位置
        Dispatcher.BeginInvoke(async () =>
        {
            await _vm.SeekAsync(SeekSlider.Value);
            _vm.IsSeeking = false;
        });
    }

    // ================= 播放列表拖动排序 =================
    private Point _dragStart;
    private Song? _dragSong;

    private void Queue_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragSong = null;
        if (FindParent<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) != null)
            return;
        if (FindParent<System.Windows.Controls.ListViewItem>(e.OriginalSource as DependencyObject) is { DataContext: Song s })
        {
            _dragSong = s;
            _dragStart = e.GetPosition(QueueList);
        }
    }

    private void Queue_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSong == null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var d = e.GetPosition(QueueList) - _dragStart;
        if (Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance && Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance)
            return;
        var song = _dragSong;
        _dragSong = null;
        DragDrop.DoDragDrop(QueueList, song, DragDropEffects.Move);
    }

    private void Queue_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(Song)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Queue_Drop(object sender, DragEventArgs e)
    {
        if (_vm == null || e.Data.GetData(typeof(Song)) is not Song song)
            return;
        var target = FindParent<System.Windows.Controls.ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext as Song;
        var to = target == null ? _vm.Queue.Count - 1 : _vm.Queue.IndexOf(target);
        if (to < 0 || target == song)
            return;
        await _vm.MoveToAsync(song, to);
    }

    private static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T t)
                return t;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
