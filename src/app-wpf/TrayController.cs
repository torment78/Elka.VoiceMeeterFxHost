using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Elka.VoiceMeeterFxHost.App;

internal sealed record TrayReloadItem(string Name, int InstanceId, Action Reload);

// The notification icon has its own message pump. Nothing on this thread may
// synchronously call the WPF dispatcher, query the engine, or read plugin state.
internal sealed class TrayController : IDisposable
{
    private const string Title = "Elka VoiceMeeter FX Host";
    private const string FailureTag = "failed-vst";
    private readonly object _gate = new();
    private readonly Queue<Action<Forms.NotifyIcon>> _pending = new();
    private readonly Thread _thread;
    private Forms.Control? _control;
    private Forms.NotifyIcon? _icon;
    private Drawing.Icon? _baseIcon;
    private Drawing.Icon? _failureIcon;
    private bool _disposed;
    private bool _stopped;

    public TrayController(Action open, Action widget, Action shutdown, bool visible = true, Action? toggle = null)
    {
        // Capture WPF resource colors on the calling UI thread, before starting
        // the independent pump. The renderer retains only System.Drawing colors.
        var applyTheme = TrayMenuTheme.Capture();
        _thread = new Thread(() => Run(open, widget, shutdown, visible, applyTheme, toggle ?? open))
        {
            IsBackground = true,
            Name = "FX Host tray"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run(Action open, Action widget, Action shutdown, bool visible, Action<Forms.ContextMenuStrip> applyTheme, Action toggle)
    {
        try
        {
            using var context = new Forms.ApplicationContext();
            using var control = new Forms.Control();
            _ = control.Handle;
            using var menu = new Forms.ContextMenuStrip();
            applyTheme(menu);
            menu.Items.Add("Open", null, (_, _) => open());
            menu.Items.Add("Widget", null, (_, _) => widget());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Shutdown", null, (_, _) => shutdown());
            _baseIcon = CreateIcon();
            using var icon = new Forms.NotifyIcon { Text = Title, Icon = _baseIcon, ContextMenuStrip = menu };
            icon.MouseUp += (_, e) => { if (e.Button == Forms.MouseButtons.Left) toggle(); };
            lock (_gate)
            {
                if (_disposed) return;
                _icon = icon;
                _control = control;
                icon.Visible = visible;
                control.BeginInvoke(new Action(Drain));
            }
            Forms.Application.Run(context);
        }
        catch (Exception ex) { RuntimeLog.Write($"Tray stopped: {ex.Message}"); }
        finally
        {
            lock (_gate)
            {
                _stopped = true;
                _control = null;
                _icon = null;
                _pending.Clear();
            }
            _failureIcon?.Dispose();
            _baseIcon?.Dispose();
        }
    }

    internal void Post(Action<Forms.NotifyIcon> action)
    {
        lock (_gate)
        {
            if (_disposed || _stopped) return;
            Enqueue(action);
        }
    }

    private void Enqueue(Action<Forms.NotifyIcon> action)
    {
        _pending.Enqueue(action);
        try { _control?.BeginInvoke(new Action(Drain)); }
        catch (InvalidOperationException)
        {
            // The message loop can finish between Run returning and its finally
            // block clearing the control. Late updates must not crash WPF.
            _pending.Clear();
            _stopped = true;
        }
    }

    private void Drain()
    {
        while (true)
        {
            Action<Forms.NotifyIcon> action;
            lock (_gate)
            {
                if (!_pending.TryDequeue(out action!)) return;
            }
            action(_icon!);
        }
    }

    public void ShowCloseHint() => Post(icon => icon.ShowBalloonTip(2500, Title,
        "Still running in the tray. Right-click the tray icon and choose Shutdown to exit.", Forms.ToolTipIcon.Info));

    public void UpdateFailures(TrayReloadItem[] failed)
    {
        Post(icon =>
        {
            var summary = failed.Length == 1 ? "1 VST FAILED" : $"{failed.Length} VSTs FAILED";
            if (failed.Length == 0)
            {
                icon.Icon = _baseIcon;
                icon.Text = Title;
            }
            else
            {
                _failureIcon ??= TrayIconBadge.CreateWarning(_baseIcon!);
                icon.Icon = _failureIcon;
                icon.Text = $"{Title} - {summary}";
            }
            var menu = icon.ContextMenuStrip!;
            foreach (var stale in menu.Items.Cast<Forms.ToolStripItem>().Where(item => Equals(item.Tag, FailureTag)).ToArray())
            {
                menu.Items.Remove(stale);
                stale.Dispose();
            }
            if (failed.Length == 0) return;
            var index = 0;
            menu.Items.Insert(index++, new Forms.ToolStripMenuItem($"{summary}:") { Enabled = false, Tag = FailureTag });
            foreach (var item in failed)
            {
                var reload = new Forms.ToolStripMenuItem($"Reload {item.Name}")
                {
                    Tag = FailureTag, ToolTipText = $"VST ID {item.InstanceId}"
                };
                reload.Click += (_, _) => item.Reload();
                menu.Items.Insert(index++, reload);
            }
            menu.Items.Insert(index, new Forms.ToolStripSeparator { Tag = FailureTag });
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed || _stopped) return;
            _disposed = true;
            Enqueue(icon => { icon.Visible = false; Forms.Application.ExitThread(); });
        }
        // Give the icon a chance to leave the notification area before a healthy
        // process exits. This wait is bounded even if Explorer stops responding.
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromMilliseconds(100));
    }

    private static Drawing.Icon CreateIcon()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && System.IO.File.Exists(processPath) &&
            Drawing.Icon.ExtractAssociatedIcon(processPath) is { } icon)
            return icon;
        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }
}
