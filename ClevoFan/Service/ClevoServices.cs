using System.Diagnostics;

namespace ClevoFan.Service;

/// <summary>
/// Manages the Clevo factory fan stack so the user can FLIP between Clevo-default and our quiet
/// control on demand — nothing is permanently disabled.
///
/// "Hand the EC to us"  = stop the Control Center Hotkey Service (CCDCHUService) and the FnKey app;
///                        together they own the factory fan profile, so stopping them frees the EC.
/// "Back to Clevo"      = restart the service and relaunch FnKey (a UWP app, launched by AUMID);
///                        the factory controller re-asserts its own fan curve.
///
/// AUMIDs/service names were confirmed on this unit via Get-StartApps / Get-CimInstance Win32_Service.
/// </summary>
public static class ClevoServices
{
    private const string HotkeyService       = "CCDCHUService";
    private const string FnKeyProcess        = "FnKey";
    private const string FnKeyAumid          = @"CLEVOCO.FnhotkeysandOSD_6h6z29zh29qx0!App";
    private const string ControlCenterAumid  = @"CLEVOCO.ControlCenter3.0_6h6z29zh29qx0!App";

    /// <summary>True if the Clevo factory fan stack appears to be running.</summary>
    public static bool ClevoRunning()
        => Process.GetProcessesByName(FnKeyProcess).Length > 0 || ServiceRunning(HotkeyService);

    /// <summary>Stop the Clevo factory fan stack so we can own the EC.</summary>
    public static void StopClevo()
    {
        RunHidden("sc.exe", $"stop {HotkeyService}");
        RunHidden("taskkill.exe", $"/F /IM {FnKeyProcess}.exe");
    }

    /// <summary>Restart the Clevo factory fan stack (return to default behavior).</summary>
    public static void StartClevo()
    {
        RunHidden("sc.exe", $"start {HotkeyService}");
        LaunchAumid(FnKeyAumid);
    }

    /// <summary>Open the Clevo Control Center 3.0 GUI.</summary>
    public static void OpenControlCenter() => LaunchAumid(ControlCenterAumid);

    private static bool ServiceRunning(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"query {name}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return outp.Contains("RUNNING", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    // Launch a UWP app by AppUserModelID via the shell.
    private static void LaunchAumid(string aumid)
        => RunHidden("explorer.exe", $"shell:AppsFolder\\{aumid}");

    private static void RunHidden(string file, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch { /* best effort — never let service plumbing crash the app */ }
    }
}
