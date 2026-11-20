namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private NotifyIcon? trackerTray;

    private void HideTrackerToTray()
    {
        if (trackerTray == null)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Camo Connector", null, (_, _) => RestoreTracker());
            menu.Items.Add("Exit", null, (_, _) => Close());
            trackerTray = new NotifyIcon { Icon = Icon, Text = "Camo Connector", ContextMenuStrip = menu };
            trackerTray.DoubleClick += (_, _) => RestoreTracker();
        }
        trackerTray.Visible = true;
        Hide();
    }

    private void RestoreTracker()
    {
        WindowState = FormWindowState.Normal;
        Show();
        Activate();
        if (trackerTray != null) trackerTray.Visible = false;
    }

    private void DisposeTrackerTray()
    {
        if (trackerTray == null) return;
        trackerTray.Visible = false;
        trackerTray.ContextMenuStrip?.Dispose();
        trackerTray.Dispose();
    }
}
