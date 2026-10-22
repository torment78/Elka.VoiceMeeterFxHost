using System.Windows;
using System.Windows.Controls;

namespace Elka.VoiceMeeterFxHost.App;

public partial class EngineStatusView : UserControl
{
    public EngineStatusView()
    {
        InitializeComponent();
        Update(HostStatusSnapshot.Empty, HostStatusSnapshot.Empty.Detail);
    }

    internal void Update(HostStatusSnapshot status, string? activity = null)
    {
        DataContext = status;
        ActivityText.Text = activity ?? string.Empty;
        ActivityText.ToolTip = activity;
        ActivityText.Visibility = string.IsNullOrWhiteSpace(activity) ? Visibility.Collapsed : Visibility.Visible;
    }
}
