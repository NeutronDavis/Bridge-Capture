namespace BridgeCapture.Tray;

/// <summary>
/// Windows System Tray icon and context menu for Bridge-Capture.
/// Must run on a dedicated STA (Single-Threaded Apartment) thread — see Program.cs.
///
/// Tray icon only appears when the app is running in an interactive user session
/// (i.e. NOT when running headless as a pure Windows Service with no desktop session).
/// </summary>
public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly IHostApplicationLifetime _lifetime;

    public TrayApplicationContext(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;

        // ── Context menu ─────────────────────────────────────────────────────
        var menu = new ContextMenuStrip();

        // Header label (disabled, acts as a title)
        var header = (ToolStripMenuItem)menu.Items.Add("Bridge Capture  ●  Running");
        header.Enabled  = false;
        header.Font     = new Font(header.Font, FontStyle.Bold);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Stop Service", null, OnStop);
        menu.Items.Add("Exit",         null, OnExit);

        // ── Tray icon ────────────────────────────────────────────────────────
        Icon appIcon = SystemIcons.Shield;
        try
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(iconPath))
            {
                appIcon = new Icon(iconPath);
            }
            else
            {
                var exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (exeIcon != null) appIcon = exeIcon;
            }
        }
        catch { }

        _trayIcon = new NotifyIcon
        {
            Text             = "NDDC-HRMS — Fingerprint Capture",
            Icon             = appIcon,
            ContextMenuStrip = menu,
            Visible          = true
        };

        // Show a toast notification on startup
        _trayIcon.ShowBalloonTip(
            timeout:  3000,
            tipTitle: "NDDC-HRMS Capture",
            tipText:  "Fingerprint bridge is running on wss://localhost:5050",
            tipIcon:  ToolTipIcon.Info);

        // ── Host shutdown hook ───────────────────────────────────────────────
        // When the .NET host stops (e.g. service stop command), hide the icon
        // and quit the WinForms message loop cleanly.
        lifetime.ApplicationStopping.Register(() =>
        {
            _trayIcon.Visible = false;
            Application.Exit();
        });
    }

    // ── Menu handlers ────────────────────────────────────────────────────────

    private void OnStop(object? sender, EventArgs e)
    {
        // Gracefully stop the .NET host (triggers ApplicationStopping above)
        _lifetime.StopApplication();
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        _lifetime.StopApplication();
        Application.Exit();
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _trayIcon.Dispose();

        base.Dispose(disposing);
    }
}
