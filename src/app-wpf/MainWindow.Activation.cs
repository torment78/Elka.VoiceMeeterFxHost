using System.Windows;

namespace Elka.VoiceMeeterFxHost.App;

public partial class MainWindow
{
    private const int WindowsMessageCommand = 0x0111;
    private const int VoicemeeterFxHostCommandId = 0x4546;

    internal void ActivateFromSecondInstance()
    {
        BringFxHostWindowToFront();
    }

    private IntPtr WindowMessageHook(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WindowsMessageCommand)
        {
            return IntPtr.Zero;
        }

        var commandId = unchecked((int)wParam.ToInt64()) & 0xffff;
        if (commandId != VoicemeeterFxHostCommandId)
        {
            return IntPtr.Zero;
        }

        handled = true;
        // VoiceMeeter sends both press and release; only the press toggles.
        if (lParam.ToInt64() != 0 && !_isShuttingDown)
        {
            Dispatcher.BeginInvoke((Action)ToggleFxHostWindowAfterVoiceMeeterClick);
        }

        return IntPtr.Zero;
    }

    private async void ToggleFxHostWindowAfterVoiceMeeterClick()
    {
        // Let VoiceMeeter finish its click handling before restoring focus.
        await Task.Delay(100);
        if (_isShuttingDown)
        {
            return;
        }

        if (IsVisible && ShowInTaskbar && WindowState != WindowState.Minimized)
        {
            HideToTray(showHint: false);
        }
        else
        {
            BringFxHostWindowToFront();
        }
    }

    private void BringFxHostWindowToFront()
    {
        if (!IsVisible || !ShowInTaskbar)
        {
            RestoreFromTray();
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = _windowStateBeforeTray == WindowState.Maximized
                ? WindowState.Maximized
                : WindowState.Normal;
        }

        Activate();
        Focus();
    }
}
