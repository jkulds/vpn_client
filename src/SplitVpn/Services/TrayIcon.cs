using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace SplitVpn.Services;

/// <summary>
/// Значок в области уведомлений.
/// </summary>
/// <remarks>
/// Берётся из WinForms: в WPF своего NotifyIcon нет, а WPF-UI убрал его в 4.x.
/// Обе подсистемы уживаются в одном процессе, отдельного окна WinForms не создаётся.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _connect;
    private readonly ToolStripMenuItem _disconnect;

    public event Action? ShowRequested;
    public event Action? ConnectRequested;
    public event Action? DisconnectRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();

        var show = new ToolStripMenuItem("Открыть", null, (_, _) => ShowRequested?.Invoke());
        show.Font = new Font(show.Font, System.Drawing.FontStyle.Bold);

        _connect = new ToolStripMenuItem("Подключить", null, (_, _) => ConnectRequested?.Invoke());
        _disconnect = new ToolStripMenuItem("Отключить", null, (_, _) => DisconnectRequested?.Invoke());

        menu.Items.Add(show);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_connect);
        menu.Items.Add(_disconnect);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => ExitRequested?.Invoke()));

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = "SplitVpn",
            ContextMenuStrip = menu
        };

        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public void Update(bool running, string status)
    {
        _connect.Enabled = !running;
        _disconnect.Enabled = running;

        // Windows обрезает подсказку значка на 63 символах, дальше молча отбрасывает.
        var text = $"SplitVpn - {status}";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Notify(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(5000);
    }

    private static Icon LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/SplitVpn;component/Assets/app.ico");
            var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is not null) return new Icon(stream);
        }
        catch (Exception)
        {
            // ресурс не найден - лучше системный значок, чем отсутствие трея
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
