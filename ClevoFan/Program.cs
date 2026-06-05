using ClevoFan.Control;
using ClevoFan.Ec;
using ClevoFan.Native;

namespace ClevoFan;

internal static class Program
{
    // Registers we surface in `dump` so we can eyeball them against HWiNFO on the real unit.
    private static readonly byte[] DumpAddrs =
        { 0x07, 0xCD, 0xCE, 0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5 };

    // ---- Thermal safety: auto-revert to factory EC defaults when hot ----
    private const int ThermalForceMaxC = 86; // >= this: force CPU fan 100% (still our control)
    private const int ThermalRevertC   = 90; // >= this: ABANDON the quiet curve, hand BOTH fans to EC defaults
    private const int ThermalResumeC   = 80; // must cool below this before we resume quiet control (hysteresis)

    // ---- GPU fan: only quiet while the dGPU is asleep AND the CPU is cool ----
    // The two fans share a heatpipe assembly, so the GPU fan also helps cool the CPU. We therefore
    // only quiet it when the GPU is asleep (temp 0) and the CPU doesn't need that extra airflow.
    private const int GpuQuietDuty    = 25; // % applied to the GPU fan while quieted
    private const int GpuQuietMaxCpuC = 70; // if CPU is warmer than this, hand the GPU fan to EC auto

    private sealed class ControlState
    {
        public bool TookControl;   // have we written any fan? (drives restore-on-exit)
        public bool GpuControlled; // are we currently holding the GPU fan quiet?
        public bool Reverted;      // are we latched into "EC owns everything" thermal-revert mode?
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string cmd = (args.FirstOrDefault() ?? "help").ToLowerInvariant();
        string modulePath = OptionValue(args, "--module")
                            ?? Path.Combine(AppContext.BaseDirectory, "LpcACPIEC.bin");
        int? divisor = OptionValue(args, "--divisor") is { } d && int.TryParse(d, out var dv) ? dv : null;

        if (cmd is "help" or "-h" or "--help") { PrintHelp(); return 0; }

        try
        {
            using var io = new PawnIo();
            io.Open(modulePath);
            var ec = new ClevoEc(io);
            if (divisor is { } v) ec.RpmDivisor = v;

            return cmd switch
            {
                "dump"    => CmdDump(ec),
                "read"    => CmdRead(ec),
                "monitor" => CmdMonitor(ec, apply: HasFlag(args, "--apply")),
                "set"     => CmdSet(ec, args),
                "max"     => CmdMax(ec),
                "auto"    => CmdAuto(ec),
                "panic"   => CmdAuto(ec), // alias: instant "back to normal"
                "run"     => CmdRun(ec),
                _ => Unknown(cmd),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    // ---------------- commands ----------------

    private static int CmdDump(ClevoEc ec)
    {
        Console.WriteLine("Read-only EC register dump (safe). Compare against HWiNFO to confirm the map:\n");
        foreach (var (addr, value) in ec.DumpRegisters(DumpAddrs))
            Console.WriteLine($"  0x{addr:X2} = 0x{value:X2}  ({value,3})");
        Console.WriteLine();
        Console.WriteLine($"  CPU temp (0x07) : {ec.ReadCpuTempC()} C");
        Console.WriteLine($"  GPU temp (0xCD) : {ec.ReadGpuTempC()} C");
        Console.WriteLine($"  CPU fan duty    : {ec.ReadCpuDutyPercent()} %");
        Console.WriteLine($"  CPU fan RPM     : {ec.ReadCpuRpm()} (divisor {ec.RpmDivisor})");
        Console.WriteLine($"  GPU fan RPM     : {ec.ReadGpuRpm()}");
        return 0;
    }

    private static int CmdRead(ClevoEc ec)
    {
        Console.WriteLine(
            $"CPU {ec.ReadCpuTempC(),3}C  {ec.ReadCpuRpm(),5} rpm  {ec.ReadCpuDutyPercent(),3}%    " +
            $"GPU {ec.ReadGpuTempC(),3}C  {ec.ReadGpuRpm(),5} rpm");
        return 0;
    }

    private static int CmdMonitor(ClevoEc ec, bool apply)
    {
        var curve = FanCurve.Quiet();
        Console.WriteLine($"Curve: {curve}");
        Console.WriteLine(apply
            ? $"MONITOR + APPLY: quiet CPU curve. GPU quieted only while asleep & CPU<{GpuQuietMaxCpuC}C. " +
              $"Auto-revert to EC defaults at {ThermalRevertC}C. Ctrl+C restores EC auto.\n"
            : "MONITOR (read-only): showing what the curve WOULD set. No EC writes. Ctrl+C to stop.\n");

        var s = new ControlState();
        using var stop = InstallRestoreHandler(ec, () => s.TookControl);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (apply)
                {
                    string note = ControlTick(ec, curve, s);
                    Console.WriteLine($"{note}   [cpu rpm {ec.ReadCpuRpm()}  gpu {ec.ReadGpuTempC()}C rpm {ec.ReadGpuRpm()}]");
                }
                else
                {
                    int t = ec.ReadCpuTempC();
                    Console.WriteLine(
                        $"CPU {t,3}C rpm {ec.ReadCpuRpm(),5} duty {ec.ReadCpuDutyPercent(),3}% -> curve {curve.DutyFor(t),3}%" +
                        $"    GPU {ec.ReadGpuTempC(),3}C rpm {ec.ReadGpuRpm(),5}");
                }
                stop.Token.WaitHandle.WaitOne(1000);
            }
        }
        finally
        {
            if (s.TookControl) { Console.WriteLine("\nRestoring both fans to EC auto..."); SafeRestore(ec); }
        }
        return 0;
    }

    private static int CmdSet(ClevoEc ec, string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int pct))
        {
            Console.Error.WriteLine("Usage: clevofan set <percent 0-100>");
            return 2;
        }
        pct = Math.Clamp(pct, 0, 100);
        Console.WriteLine($"Holding CPU fan at {pct}% (GPU untouched). Thermal failsafe forces 100% at {ThermalForceMaxC}C.");
        Console.WriteLine("Press Enter (or Ctrl+C) to restore EC auto and exit.");

        var s = new ControlState { TookControl = true };
        using var stop = InstallRestoreHandler(ec, () => s.TookControl);
        try
        {
            var reader = Task.Run(Console.ReadLine);
            while (!reader.IsCompleted && !stop.IsCancellationRequested)
            {
                int t = ec.ReadCpuTempC();
                int duty = t >= ThermalForceMaxC ? 100 : pct;
                ec.SetCpuDutyPercent(duty);
                Console.WriteLine($"CPU {t,3}C rpm {ec.ReadCpuRpm(),5}  holding {duty}%");
                stop.Token.WaitHandle.WaitOne(1000);
            }
        }
        finally { Console.WriteLine("Restoring both fans to EC auto..."); SafeRestore(ec); }
        return 0;
    }

    private static int CmdMax(ClevoEc ec)
    {
        Console.WriteLine("MAX COOLDOWN: forcing BOTH fans to 100%. Press Enter (or Ctrl+C) to restore EC auto.\n");
        var s = new ControlState { TookControl = true };
        using var stop = InstallRestoreHandler(ec, () => s.TookControl);
        try
        {
            var reader = Task.Run(Console.ReadLine);
            while (!reader.IsCompleted && !stop.IsCancellationRequested)
            {
                ec.ForceMaxBothFans(); // re-assert each second in case the EC tries to take back over
                Console.WriteLine($"MAX  CPU {ec.ReadCpuTempC(),3}C rpm {ec.ReadCpuRpm(),5}    GPU {ec.ReadGpuTempC(),3}C rpm {ec.ReadGpuRpm(),5}");
                stop.Token.WaitHandle.WaitOne(1000);
            }
        }
        finally { Console.WriteLine("\nRestoring both fans to EC auto..."); SafeRestore(ec); }
        return 0;
    }

    private static int CmdAuto(ClevoEc ec)
    {
        Console.WriteLine("Handing BOTH fans back to EC automatic control...");
        ec.RestoreAllFansAuto();
        Console.WriteLine("Done. Fans are back under EC firmware control (factory behavior).");
        return 0;
    }

    private static int CmdRun(ClevoEc ec)
    {
        var curve = FanCurve.Quiet();
        Console.WriteLine($"Daemon running. CPU curve: {curve}");
        Console.WriteLine($"Thermal safety: force 100% at {ThermalForceMaxC}C; AUTO-REVERT both fans to EC defaults at {ThermalRevertC}C (resume < {ThermalResumeC}C).");
        Console.WriteLine($"GPU fan quieted to {GpuQuietDuty}% only while the dGPU is asleep AND CPU < {GpuQuietMaxCpuC}C; otherwise EC auto.");
        Console.WriteLine("Ctrl+C restores BOTH fans to EC auto.\n");

        var s = new ControlState();
        using var stop = InstallRestoreHandler(ec, () => s.TookControl);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                Console.WriteLine(ControlTick(ec, curve, s));
                stop.Token.WaitHandle.WaitOne(1500);
            }
        }
        finally { Console.WriteLine("\nRestoring both fans to EC auto..."); SafeRestore(ec); }
        return 0;
    }

    // ---------------- control core ----------------

    /// <summary>
    /// One control decision: read CPU temp, apply the quiet curve to the CPU fan, apply the GPU
    /// policy, and enforce the thermal auto-revert. Returns a human-readable status line.
    /// On a read failure it fails SAFE — hands everything back to the EC firmware.
    /// </summary>
    private static string ControlTick(ClevoEc ec, FanCurve curve, ControlState s)
    {
        int t;
        try { t = ec.ReadCpuTempC(); }
        catch { SafeRestore(ec); s.GpuControlled = false; s.Reverted = false; return "[safe] EC read failed -> handed back to EC auto"; }

        // Thermal auto-revert latch (with hysteresis): once hot, the EC firmware owns BOTH fans
        // until we've cooled well below the trip point.
        if (s.Reverted)
        {
            if (t >= ThermalResumeC) return $"reverted -> EC defaults   CPU {t}C (waiting to cool < {ThermalResumeC}C)";
            s.Reverted = false; // cooled enough — resume quiet control this tick
        }
        else if (t >= ThermalRevertC)
        {
            s.Reverted = true;
            s.GpuControlled = false;
            ec.RestoreAllFansAuto();
            return $"[THERMAL] CPU {t}C >= {ThermalRevertC}C -> reverting BOTH fans to EC defaults";
        }

        int duty = t >= ThermalForceMaxC ? 100 : curve.DutyFor(t);
        ec.SetCpuDutyPercent(duty);
        ApplyGpuPolicy(ec, t, s);
        s.TookControl = true;
        return $"CPU {t,3}C -> cpu fan {duty,3}%   gpu:{(s.GpuControlled ? "quiet" : "EC")}";
    }

    /// <summary>
    /// GPU-fan policy. Only LOWER the GPU fan while the dGPU is fully asleep (temp == 0) AND the CPU
    /// is cool enough that it doesn't need the shared-heatpipe airflow. Otherwise the GPU fan stays
    /// on EC firmware control so the 3080 (and the CPU) are always properly cooled.
    /// </summary>
    private static void ApplyGpuPolicy(ClevoEc ec, int cpuTempC, ControlState s)
    {
        int gpuT = ec.ReadGpuTempC();
        if (gpuT == 0 && cpuTempC < GpuQuietMaxCpuC)
        {
            ec.SetGpuDutyPercent(GpuQuietDuty);
            s.GpuControlled = true;
        }
        else if (s.GpuControlled)
        {
            ec.RestoreGpuFanAuto(); // conditions no longer safe to quiet — give it back to the EC
            s.GpuControlled = false;
        }
    }

    /// <summary>
    /// Bulletproof restore: hand both fans to EC auto; if that fails, blast them to 100% so we never
    /// leave a fan stranded low. Never throws.
    /// </summary>
    private static void SafeRestore(ClevoEc ec)
    {
        try { ec.RestoreAllFansAuto(); }
        catch
        {
            try { ec.ForceMaxBothFans(); Console.Error.WriteLine("WARN: EC handback failed — forced fans to 100% as a failsafe."); }
            catch { /* nothing left to do */ }
        }
    }

    // ---------------- helpers ----------------

    /// <summary>Ctrl+C + process-exit handlers that restore BOTH fans if we ever took control.</summary>
    private static CancellationTokenSource InstallRestoreHandler(ClevoEc ec, Func<bool> tookControl)
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { if (tookControl()) SafeRestore(ec); } catch { /* last-ditch */ }
        };
        return cts;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command '{cmd}'. Run `clevofan help`.");
        return 2;
    }

    private static string? OptionValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool HasFlag(string[] args, string flag) => args.Contains(flag);

    private static void PrintHelp()
    {
        Console.WriteLine("""
        clevofan — quiet fan control for Clevo PC50-series (Lambda Tensorbook). Admin required.

        Commands:
          dump                 Read-only EC register dump + decoded temps/RPM/duty. START HERE.
          read                 One-line snapshot of temps / RPM / duty.
          monitor [--apply]    Live view of the curve's decision. Read-only unless --apply.
          set <percent>        Hold the CPU fan at a fixed duty; restores EC auto on exit.
          max                  Force BOTH fans to 100% for a quick cooldown; restores EC auto on exit.
          run                  Run the quiet curve continuously with thermal auto-revert.
          auto | panic         BACK TO NORMAL: hand both fans to EC firmware control and exit.

        Options:
          --module <path>      Path to signed LpcACPIEC.bin (default: next to clevofan.exe).
          --divisor <n>        RPM divisor (default 1966080; try 2156220 if RPM looks wrong).

        Safety: only the CPU fan is run on the quiet curve. The GPU fan is only quieted while the
        dGPU is asleep and the CPU is cool. If CPU hits {0}C both fans auto-revert to EC defaults.
        On any error or exit the tool hands both fans back to the EC (or 100% if that fails).
        """.Replace("{0}", ThermalRevertC.ToString()));
    }
}
