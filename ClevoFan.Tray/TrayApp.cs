using System.Drawing;
using System.Windows.Forms;
using ClevoFan.Control;
using ClevoFan.Ec;
using ClevoFan.Native;
using ClevoFan.Service;

namespace ClevoFan.Tray;

/// <summary>
/// System-tray controller. Lets you FLIP between Clevo-default fans and our quiet control on demand:
///   • Quiet mode  — stops the Clevo fan stack and runs our quiet CPU curve (GPU safe).
///   • Max cooldown — stops Clevo and blasts both fans to 100%.
///   • Back to Clevo default — restores both fans and restarts the Clevo fan stack.
/// On exit (or crash) it always restores the fans and brings Clevo back, so you're never left
/// with no fan control.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private enum Mode { ClevoDefault, Quiet, Max }

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _quietItem;
    private readonly ToolStripMenuItem _maxItem;
    private readonly System.Windows.Forms.Timer _uiTimer;

    private readonly PawnIo _io = new();
    private readonly ClevoEc _ec;
    private readonly FanController _fan;

    private readonly Thread _loop;
    private volatile bool _running = true;
    private volatile Mode _mode = Mode.ClevoDefault;
    private volatile string _status = "Starting…";

    public TrayApp()
    {
        // ---- EC / driver init (throws with a clear message if PawnIO/module missing) ----
        _io.Open(Path.Combine(AppContext.BaseDirectory, "LpcACPIEC.bin"));
        _ec = new ClevoEc(_io);
        _fan = new FanController(_ec);

        // Last-ditch: if the process dies for any reason, give the fans + Clevo back.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();

        // ---- menu ----
        _statusItem = new ToolStripMenuItem("…") { Enabled = false };
        _quietItem = new ToolStripMenuItem("Quiet mode", null, (_, _) => SetMode(Mode.Quiet, _quietItem!)) { CheckOnClick = true };
        _maxItem = new ToolStripMenuItem("Max cooldown (hold)", null, (_, _) => SetMode(Mode.Max, _maxItem!)) { CheckOnClick = true };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_quietItem);
        menu.Items.Add(_maxItem);
        menu.Items.Add(new ToolStripMenuItem("Back to Clevo default", null, (_, _) => BackToDefault()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open Clevo Control Center", null, (_, _) => ClevoServices.OpenControlCenter()));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Clevo Fan",
            ContextMenuStrip = menu,
        };
        _tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) _tray.ShowContextMenu(); };

        // ---- control loop (background) + UI refresh (UI thread) ----
        _loop = new Thread(ControlLoop) { IsBackground = true, Name = "fan-control" };
        _loop.Start();

        _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();
    }

    // ---------------- mode switching ----------------

    private void SetMode(Mode mode, ToolStripMenuItem source)
    {
        // checkable items: reflect exclusive selection
        _quietItem.Checked = source == _quietItem && source.Checked;
        _maxItem.Checked = source == _maxItem && source.Checked;

        if (!source.Checked) { BackToDefault(); return; } // unchecked the active mode

        // Entering Quiet or Max: take the EC from Clevo.
        ClevoServices.StopClevo();
        _mode = mode;
        ShowBalloon(mode == Mode.Quiet ? "Quiet mode on" : "Max cooldown on",
                    mode == Mode.Quiet ? "Running the quiet CPU curve. Clevo paused." : "Both fans at 100%. Clevo paused.");
    }

    private void BackToDefault()
    {
        _mode = Mode.ClevoDefault;
        _quietItem.Checked = false;
        _maxItem.Checked = false;
        _fan.RestoreAll();          // hand fans to EC
        ClevoServices.StartClevo(); // bring the factory controller back
        _status = "Clevo default (factory fan control)";
        ShowBalloon("Back to Clevo default", "Fans returned to the factory controller.");
    }

    private void ExitApp()
    {
        _running = false;
        try { _loop.Join(2500); } catch { /* ignore */ }
        Cleanup();
        _uiTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _io.Dispose();
        ExitThread();
    }

    private void Cleanup()
    {
        try { _fan.RestoreAll(); } catch { /* ignore */ }
        try { ClevoServices.StartClevo(); } catch { /* ignore */ }
    }

    // ---------------- loops ----------------

    private void ControlLoop()
    {
        while (_running)
        {
            try
            {
                switch (_mode)
                {
                    case Mode.Max:
                        _fan.ForceMax();
                        var m = _fan.ReadStatus();
                        _status = $"MAX  CPU {m.CpuTempC}°C {m.CpuRpm}rpm   GPU {m.GpuRpm}rpm";
                        break;
                    case Mode.Quiet:
                        var q = _fan.Tick();
                        _status = q.Reverted
                            ? $"Quiet[EC: hot {q.CpuTempC}°C]  cooling…"
                            : $"Quiet  CPU {q.CpuTempC}°C  fan {q.CpuDuty}% {q.CpuRpm}rpm  GPU:{(q.GpuQuiet ? "quiet" : "EC")}";
                        break;
                    default:
                        _status = "Clevo default (factory fan control)";
                        break;
                }
            }
            catch (Exception ex) { _status = "error: " + ex.Message; }

            Thread.Sleep(_mode == Mode.ClevoDefault ? 2000 : (_mode == Mode.Max ? 1000 : 1500));
        }
    }

    private void RefreshUi()
    {
        string s = _status;
        _statusItem.Text = s;
        _tray.Text = Truncate("Clevo Fan — " + s, 63); // NotifyIcon tooltip cap
    }

    // ---------------- helpers ----------------

    private void ShowBalloon(string title, string text)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(2000);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

internal static class NotifyIconExtensions
{
    // Make a left-click open the context menu (NotifyIcon only shows it on right-click by default).
    public static void ShowContextMenu(this NotifyIcon icon)
    {
        icon.ContextMenuStrip?.Show(Cursor.Position);
    }
}
