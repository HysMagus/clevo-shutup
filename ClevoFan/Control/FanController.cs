using ClevoFan.Ec;

namespace ClevoFan.Control;

/// <summary>
/// Reusable, thread-safe fan-control core shared by the CLI daemon and the tray UI. Encapsulates the
/// quiet CPU curve, the GPU policy, the thermal auto-revert latch, and bulletproof restore.
/// Pure logic — no UI, no service management. Mirrors the design proven by the CLI.
///
/// SAFETY invariants:
///  - The CPU fan runs the quiet curve; the GPU fan is only LOWERED while the dGPU is asleep AND the
///    CPU is cool (shared heatpipe), otherwise it is handed back to the EC firmware.
///  - At/above ThermalRevertC, BOTH fans are handed to the EC firmware until cooled below ThermalResumeC.
///  - On any EC read failure, we fail safe by handing both fans back to the EC.
///  - RestoreAll never throws; if EC handback fails it forces fans to 100%.
/// </summary>
public sealed class FanController
{
    public const int ThermalForceMaxC = 86; // >= this: force CPU fan 100%
    public const int ThermalRevertC   = 90; // >= this: hand BOTH fans to EC defaults (latched)
    public const int ThermalResumeC   = 80; // resume quiet control once cooled below this
    public const int GpuQuietDuty      = 25; // % applied to the GPU fan while quieted
    public const int GpuQuietMaxCpuC   = 70; // above this CPU temp, the GPU fan helps cool — hand to EC

    private readonly object _gate = new();
    private readonly ClevoEc _ec;
    private readonly FanCurve _curve;
    private bool _gpuControlled;
    private bool _reverted;

    public bool TookControl { get; private set; }

    public FanController(ClevoEc ec, FanCurve? curve = null)
    {
        _ec = ec;
        _curve = curve ?? FanCurve.Quiet();
    }

    public readonly record struct Status(
        int CpuTempC, int CpuRpm, int CpuDuty,
        int GpuTempC, int GpuRpm,
        bool GpuQuiet, bool Reverted, string Note);

    /// <summary>Apply one control decision (quiet curve + GPU policy + thermal revert). Fails safe.</summary>
    public Status Tick()
    {
        lock (_gate)
        {
            int t;
            try { t = _ec.ReadCpuTempC(); }
            catch
            {
                RestoreAllNoLock();
                return new Status(0, 0, 0, 0, 0, false, false, "EC read failed — handed back to EC auto");
            }

            if (_reverted)
            {
                if (t >= ThermalResumeC)
                    return Snapshot(t, $"EC defaults (cooling, waiting < {ThermalResumeC}°C)");
                _reverted = false; // cooled enough — resume quiet control this tick
            }
            else if (t >= ThermalRevertC)
            {
                _reverted = true;
                _gpuControlled = false;
                _ec.RestoreAllFansAuto();
                return Snapshot(t, $"THERMAL REVERT ≥ {ThermalRevertC}°C → EC defaults");
            }

            int duty = t >= ThermalForceMaxC ? 100 : _curve.DutyFor(t);
            _ec.SetCpuDutyPercent(duty);
            ApplyGpuPolicy(t);
            TookControl = true;
            return Snapshot(t, t >= ThermalForceMaxC ? "thermal: CPU fan 100%" : "quiet curve");
        }
    }

    /// <summary>Read-only status snapshot (no writes).</summary>
    public Status ReadStatus()
    {
        lock (_gate)
        {
            int t;
            try { t = _ec.ReadCpuTempC(); }
            catch { return new Status(0, 0, 0, 0, 0, false, _reverted, "EC read failed"); }
            return Snapshot(t, _reverted ? "EC defaults" : "monitoring");
        }
    }

    /// <summary>Force BOTH fans to 100% (cooldown). Marks that we've taken control.</summary>
    public void ForceMax()
    {
        lock (_gate)
        {
            _ec.ForceMaxBothFans();
            TookControl = true;
        }
    }

    /// <summary>Hand both fans back to the EC; if that fails, force 100%. Never throws.</summary>
    public void RestoreAll()
    {
        lock (_gate) { RestoreAllNoLock(); }
    }

    private void RestoreAllNoLock()
    {
        _gpuControlled = false;
        _reverted = false;
        try { _ec.RestoreAllFansAuto(); }
        catch { try { _ec.ForceMaxBothFans(); } catch { /* nothing left */ } }
    }

    private void ApplyGpuPolicy(int cpuTempC)
    {
        int gpuT = _ec.ReadGpuTempC();
        if (gpuT == 0 && cpuTempC < GpuQuietMaxCpuC)
        {
            _ec.SetGpuDutyPercent(GpuQuietDuty);
            _gpuControlled = true;
        }
        else if (_gpuControlled)
        {
            _ec.RestoreGpuFanAuto();
            _gpuControlled = false;
        }
    }

    private Status Snapshot(int cpuTemp, string note)
        => new(cpuTemp, _ec.ReadCpuRpm(), _ec.ReadCpuDutyPercent(),
               _ec.ReadGpuTempC(), _ec.ReadGpuRpm(), _gpuControlled, _reverted, note);
}
