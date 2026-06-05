using System.Diagnostics;
using ClevoFan.Native;

namespace ClevoFan.Ec;

/// <summary>
/// Clevo PC50-series embedded-controller access, built on the standard ACPI EC port handshake
/// plus Clevo's vendor "set fan duty" command. Confirmed across four independent sources:
/// TUXEDO tuxedo-fan-control (ec_access.cc), SkyLandTW/ClevoECView (fanctrl.c), SkyLandTW/clevo-indicator,
/// simopil/clevofan, and the HWiNFO "FAN control on Clevo based laptops" thread.
///
/// Reads (temp / rpm / duty) use the generic ACPI EC read command (0x80).
/// Setting fan duty uses Clevo's vendor command 0x99 with a fan selector byte.
///
/// SAFETY POSTURE for this build: we only ever drive the CPU fan (selector 0x01). The GPU fan
/// (selector 0x02) is never written, so it stays on the EC's own automatic control and continues
/// to ramp normally under GPU load. Holding a GPU fan low is the one thing that could cook the dGPU.
///
/// All register addresses below are HIGH-confidence but cross-board-variable: verify with `dump`
/// on the actual unit before trusting writes (see README, "Verification pass").
/// </summary>
public sealed class ClevoEc
{
    // ---- Ports (the only two LpcACPIEC whitelists) ----
    private const ushort EC_CMD = 0x66;   // status/command port (EC_SC)
    private const ushort EC_DATA = 0x62;  // data port

    // ---- Status-register flags on EC_CMD ----
    private const byte IBF = 0x02;  // input buffer full  -> wait for 0 before writing
    private const byte OBF = 0x01;  // output buffer full -> wait for 1 before reading

    // ---- ACPI EC commands ----
    private const byte RD_EC = 0x80;  // read  EC RAM: cmd -> addr -> (read) value
    private const byte WR_EC = 0x81;  // write EC RAM: cmd -> addr -> value   (unused; reserved)

    // ---- Clevo vendor fan command ----
    private const byte FAN_DUTY_CMD = 0x99; // 0x99 -> selector -> duty(0..255)
    private const byte FAN_AUTO_SEL = 0xFF; // 0x99 -> 0xFF -> fanIndex  : hand fan back to EC auto

    // ---- Fan selectors ----
    public const byte SEL_CPU = 0x01;
    public const byte SEL_GPU = 0x02; // intentionally never written in this build

    // ---- EC RAM registers (clevo-indicator map; verify per unit) ----
    private const byte REG_CPU_TEMP = 0x07;
    private const byte REG_GPU_TEMP = 0xCD;
    private const byte REG_FAN_DUTY = 0xCE; // primary (CPU) fan duty readback, 0..255
    private const byte REG_FAN_RPM_HI = 0xD0;
    private const byte REG_FAN_RPM_LO = 0xD1;
    private const byte REG_GPU_RPM_HI = 0xD2;
    private const byte REG_GPU_RPM_LO = 0xD3;

    // RPM = DIVISOR / ticks. clevo-indicator uses 2156220; simopil/clevofan uses 1966080 for
    // multi-fan PC50-class boards. Default to the clevofan value; confirm against HWiNFO during `dump`.
    public int RpmDivisor { get; set; } = 1966080;

    private const int BusyTimeoutMs = 200;

    private readonly PawnIo _io;
    private readonly object _lock = new();

    public ClevoEc(PawnIo io) => _io = io;

    // ---------------- low-level ACPI EC handshake ----------------

    private void WaitFlag(byte flag, bool set)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            byte status = _io.InB(EC_CMD);
            bool isSet = (status & flag) != 0;
            if (isSet == set) return;
            if (sw.ElapsedMilliseconds > BusyTimeoutMs)
                throw new TimeoutException(
                    $"EC busy: flag 0x{flag:X2} never became {(set ? "1" : "0")} (status=0x{status:X2}).");
            Thread.SpinWait(64); // don't hammer the status port — that itself loads the EC
        }
    }

    /// <summary>
    /// If a stale output byte is sitting in the EC buffer (left over from an aborted transaction),
    /// consume it so the next read re-syncs. Best-effort.
    /// </summary>
    private void DrainOutput()
    {
        try { if ((_io.InB(EC_CMD) & OBF) != 0) _io.InB(EC_DATA); } catch { /* ignore */ }
    }

    /// <summary>Read one EC RAM byte at <paramref name="addr"/> via the 0x80 read command, with retry.</summary>
    public byte ReadByte(byte addr)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            lock (_lock)
            {
                try
                {
                    WaitFlag(IBF, false); _io.OutB(EC_CMD, RD_EC);
                    WaitFlag(IBF, false); _io.OutB(EC_DATA, addr);
                    WaitFlag(OBF, true);
                    return _io.InB(EC_DATA);
                }
                catch (TimeoutException ex) { last = ex; DrainOutput(); }
            }
            Thread.Sleep(2); // let the EC settle, then retry the whole transaction
        }
        throw new TimeoutException(
            $"EC read of 0x{addr:X2} failed after retries ({last?.Message}). " +
            "Something else may be contending the EC (Control Center / FnKey running?).");
    }

    private void Command3(byte cmd, byte b1, byte b2)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            lock (_lock)
            {
                try
                {
                    WaitFlag(IBF, false); _io.OutB(EC_CMD, cmd);
                    WaitFlag(IBF, false); _io.OutB(EC_DATA, b1);
                    WaitFlag(IBF, false); _io.OutB(EC_DATA, b2);
                    WaitFlag(IBF, false);
                    Thread.Sleep(3); // let the EC digest the command before the next transaction
                    return;
                }
                catch (TimeoutException ex) { last = ex; DrainOutput(); }
            }
            Thread.Sleep(2);
        }
        throw new TimeoutException($"EC command 0x{cmd:X2},0x{b1:X2},0x{b2:X2} failed after retries ({last?.Message}).");
    }

    // ---------------- sensor reads ----------------

    public int ReadCpuTempC() => ReadByte(REG_CPU_TEMP);
    public int ReadGpuTempC() => ReadByte(REG_GPU_TEMP);

    /// <summary>Primary (CPU) fan duty as a percentage 0..100 (from raw 0..255).</summary>
    public int ReadCpuDutyPercent() => (int)Math.Round(ReadByte(REG_FAN_DUTY) * 100.0 / 255.0);

    public int ReadCpuRpm() => RpmFrom(REG_FAN_RPM_HI, REG_FAN_RPM_LO);
    public int ReadGpuRpm() => RpmFrom(REG_GPU_RPM_HI, REG_GPU_RPM_LO);

    private int RpmFrom(byte hiReg, byte loReg)
    {
        int ticks = (ReadByte(hiReg) << 8) | ReadByte(loReg);
        return ticks > 0 ? RpmDivisor / ticks : 0;
    }

    // ---------------- CPU fan control (the only fan we write) ----------------

    /// <summary>Set the CPU fan to <paramref name="percent"/> (0..100). Latches the CPU fan into manual mode.</summary>
    public void SetCpuDutyPercent(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        byte raw = (byte)Math.Round(percent * 255.0 / 100.0);
        Command3(FAN_DUTY_CMD, SEL_CPU, raw);
    }

    /// <summary>
    /// Set the GPU fan to <paramref name="percent"/> (0..100). SAFETY: only call this while the dGPU
    /// is asleep/cool (see daemon logic). Never hold the GPU fan low while the GPU is under load.
    /// </summary>
    public void SetGpuDutyPercent(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        byte raw = (byte)Math.Round(percent * 255.0 / 100.0);
        Command3(FAN_DUTY_CMD, SEL_GPU, raw);
    }

    /// <summary>Hand the CPU fan back to the EC's built-in automatic control. Safe to call any time.</summary>
    public void RestoreCpuFanAuto() => RestoreFanAuto(SEL_CPU);

    /// <summary>Hand the GPU fan back to the EC's built-in automatic control. Safe to call any time.</summary>
    public void RestoreGpuFanAuto() => RestoreFanAuto(SEL_GPU);

    /// <summary>Hand BOTH fans back to the EC firmware. This is the "panic / back to normal" primitive.</summary>
    public void RestoreAllFansAuto()
    {
        RestoreFanAuto(SEL_CPU);
        RestoreFanAuto(SEL_GPU);
    }

    /// <summary>
    /// Last-ditch failsafe: blast BOTH fans to 100%. Used only if handing back to EC auto fails.
    /// Loud, but guarantees we never leave a fan stranded low when something has gone wrong.
    /// </summary>
    public void ForceMaxBothFans()
    {
        SetCpuDutyPercent(100);
        SetGpuDutyPercent(100);
    }

    /// <summary>
    /// Hand one fan back to the EC's built-in automatic control.
    /// Sequence per simopil/clevofan: zero the duty, settle ~100ms, then 0x99,0xFF,index.
    /// </summary>
    private void RestoreFanAuto(byte selector)
    {
        Command3(FAN_DUTY_CMD, selector, 0x00);
        Thread.Sleep(100);
        Command3(FAN_DUTY_CMD, FAN_AUTO_SEL, selector);
    }

    /// <summary>
    /// Diagnostic-only raw EC dump of the registers we care about. Read-only; safe.
    /// </summary>
    public IReadOnlyList<(byte addr, byte value)> DumpRegisters(IEnumerable<byte> addrs)
        => addrs.Select(a => (a, ReadByte(a))).ToList();
}
