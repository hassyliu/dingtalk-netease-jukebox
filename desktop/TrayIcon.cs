using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Wpf.Ui.Controls;
using Forms = System.Windows.Forms;
using MenuItem = System.Windows.Controls.MenuItem;

namespace DianGeJi;

/// <summary>系统托盘图标：左键打开窗口，右键弹出菜单（暂停 / 切歌 / 退出）。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly MainWindow _window;
    private readonly MainViewModel _vm;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public TrayIcon(MainWindow window, MainViewModel vm)
    {
        _window = window;
        _vm = vm;
        var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(res.Stream, Forms.SystemInformation.SmallIconSize),
            Text = "点歌机",
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                _window.ShowFromTray();
            else if (e.Button == Forms.MouseButtons.Right)
                ShowMenu();
        };
    }

    public void SetText(string text)
    {
        // 托盘提示最多 127 个字符
        _icon.Text = text.Length > 120 ? text[..120] + "…" : text;
    }

    public void ShowHint(string text) => _icon.ShowBalloonTip(3000, "点歌机", text, Forms.ToolTipIcon.Info);

    private void ShowMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(Item("显示主界面", SymbolRegular.Window24, () => _window.ShowFromTray(), bold: true));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(_vm.IsPaused ? "播放" : "暂停", _vm.IsPaused ? SymbolRegular.Play24 : SymbolRegular.Pause24,
            () => _vm.TogglePlayCommand.Execute(null), enabled: _vm.HasCurrent || _vm.IsPaused));
        menu.Items.Add(Item("切歌", SymbolRegular.Next24, () => _vm.NextCommand.Execute(null), enabled: _vm.HasCurrent));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("退出点歌机", SymbolRegular.Dismiss24, () => _ = _window.QuitAsync()));

        // 让菜单在点击别处时能自动关闭
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle != IntPtr.Zero)
            SetForegroundWindow(handle);
        menu.IsOpen = true;
    }

    private static MenuItem Item(string text, SymbolRegular icon, Action onClick, bool enabled = true, bool bold = false)
    {
        var item = new MenuItem
        {
            Header = text,
            Icon = new SymbolIcon(icon),
            IsEnabled = enabled,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
