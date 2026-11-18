using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace DianGeJi;

public partial class App : Application
{
    private const string MutexName = "DianGeJi.Desktop.SingleInstance";
    private const string ShowEventName = "DianGeJi.Desktop.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 只允许运行一个：再次打开时把已运行的窗口叫出来
        _mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            try
            {
                EventWaitHandle.OpenExisting(ShowEventName).Set();
            }
            catch (Exception)
            {
            }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray());
        }) { IsBackground = true }.Start();

        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);

        ApplicationThemeManager.ApplySystemTheme();
        var w = new MainWindow();
        MainWindow = w;
        w.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Core.Log.Error("未处理的异常", e.Exception);
        MessageBox.Show("出现了一个错误：\n" + e.Exception.Message, "点歌机", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
