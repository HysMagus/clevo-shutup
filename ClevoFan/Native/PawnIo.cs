using System.Runtime.InteropServices;

namespace ClevoFan.Native;

/// <summary>
/// Thin wrapper over the PawnIO signed kernel driver (https://pawnio.eu).
///
/// We load the stock, namazso-signed <c>LpcACPIEC</c> module, whose port whitelist is
/// literally <c>port == 0x62 || port == 0x66</c> — i.e. exactly the two ACPI EC ports we
/// need and nothing else. That is what lets this run on a stock Win10/11 box (incl. with
/// Memory Integrity/HVCI on) without disabling driver-signature enforcement, unlike WinRing0.
///
/// The module exposes two functions:
///   ioctl_pio_read  : in[0]=port            -> out[0]=value
///   ioctl_pio_write : in[0]=port, in[1]=val -> (no out)
///
/// Driver + module install (one-time, see README):
///   1. Install PawnIO (provides PawnIOLib.dll in System32 and loads the signed driver).
///   2. Place the signed LpcACPIEC.bin next to clevofan.exe (or pass --module).
///
/// NOTE: P/Invoke signatures mirror PawnIOLib/include/PawnIOLib.h. If a future PawnIOLib
/// changes them, this is the single file to fix — everything above it is pure C#.
/// </summary>
public sealed class PawnIo : IDisposable
{
    private const string Lib = "PawnIOLib"; // PawnIOLib.dll, installed under C:\Program Files\PawnIO

    // PawnIO installs PawnIOLib.dll into its own program folder (not System32/PATH), so teach the
    // runtime where to find it. Probes: $PAWNIO_LIB, next to the exe, then the default install dir.
    static PawnIo()
    {
        NativeLibrary.SetDllImportResolver(typeof(PawnIo).Assembly, (name, _, _) =>
        {
            if (!string.Equals(name, Lib, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            foreach (var candidate in new[]
                     {
                         Environment.GetEnvironmentVariable("PAWNIO_LIB"),
                         Path.Combine(AppContext.BaseDirectory, "PawnIOLib.dll"),
                         @"C:\Program Files\PawnIO\PawnIOLib.dll",
                     })
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate) &&
                    NativeLibrary.TryLoad(candidate, out var handle))
                    return handle;
            }
            return IntPtr.Zero; // fall back to default probing
        });
    }

    [DllImport(Lib)] private static extern int pawnio_open(out IntPtr handle);

    [DllImport(Lib)]
    private static extern int pawnio_load(IntPtr handle, byte[] blob, UIntPtr size);

    [DllImport(Lib, CharSet = CharSet.Ansi)]
    private static extern int pawnio_execute(
        IntPtr handle,
        string name,
        ulong[] inBuf, UIntPtr inSize,
        ulong[] outBuf, UIntPtr outSize,
        out UIntPtr returnSize);

    [DllImport(Lib)] private static extern int pawnio_close(IntPtr handle);

    private IntPtr _handle;
    private bool _loaded;

    /// <summary>Open the driver and load the signed LpcACPIEC module from <paramref name="moduleBinPath"/>.</summary>
    public void Open(string moduleBinPath)
    {
        int rc = pawnio_open(out _handle);
        if (rc != 0 || _handle == IntPtr.Zero)
            throw new PawnIoException(
                $"pawnio_open failed (0x{rc:X8}). Is PawnIO installed and the service running? " +
                "Are you running as Administrator?", rc);

        if (!File.Exists(moduleBinPath))
            throw new FileNotFoundException(
                $"LpcACPIEC module not found at '{moduleBinPath}'. Download the signed " +
                "LpcACPIEC.bin from the PawnIO.Modules releases and place it next to clevofan.exe.",
                moduleBinPath);

        byte[] blob = File.ReadAllBytes(moduleBinPath);
        rc = pawnio_load(_handle, blob, (UIntPtr)(uint)blob.Length);
        if (rc != 0)
            throw new PawnIoException(
                $"pawnio_load failed (0x{rc:X8}). The module blob may be unsigned/corrupt, or this " +
                "PawnIO edition rejects it.", rc);

        _loaded = true;
    }

    /// <summary>Read one byte from an allowed port (0x62 or 0x66).</summary>
    public byte InB(ushort port)
    {
        EnsureLoaded();
        var inBuf = new ulong[] { port };
        var outBuf = new ulong[1];
        int rc = pawnio_execute(_handle, "ioctl_pio_read",
            inBuf, (UIntPtr)1, outBuf, (UIntPtr)1, out _);
        if (rc != 0)
            throw new PawnIoException(
                $"ioctl_pio_read(0x{port:X2}) failed (0x{rc:X8}). Only 0x62/0x66 are whitelisted.", rc);
        return (byte)outBuf[0];
    }

    /// <summary>Write one byte to an allowed port (0x62 or 0x66).</summary>
    public void OutB(ushort port, byte value)
    {
        EnsureLoaded();
        var inBuf = new ulong[] { port, value };
        int rc = pawnio_execute(_handle, "ioctl_pio_write",
            inBuf, (UIntPtr)2, Array.Empty<ulong>(), UIntPtr.Zero, out _);
        if (rc != 0)
            throw new PawnIoException(
                $"ioctl_pio_write(0x{port:X2}, 0x{value:X2}) failed (0x{rc:X8}).", rc);
    }

    private void EnsureLoaded()
    {
        if (!_loaded) throw new InvalidOperationException("PawnIo.Open() must be called first.");
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            try { pawnio_close(_handle); } catch { /* best-effort */ }
            _handle = IntPtr.Zero;
            _loaded = false;
        }
    }
}

public sealed class PawnIoException : Exception
{
    public int Code { get; }
    public PawnIoException(string message, int code) : base(message) => Code = code;
}
