# Clevo Quiet Fan

Tame the absurd fan noise on a **Lambda Tensorbook / Clevo PC50-series** laptop running Windows.

The factory Clevo/Lambda Control Center runs **both** fans hard even at idle, and all three of its
modes (Automatic / Max / Custom) are far too aggressive. This project gives you a genuinely quiet
CPU-fan curve (and quiets the idle GPU fan) that you can **flip on whenever the noise annoys you and
flip back to factory in one click** — nothing is permanently disabled.

Two pieces, sharing one validated Embedded-Controller engine:

| | |
|---|---|
| **ClevoFanTray** | System-tray app — the thing you'll actually use. Toggle Quiet / Max / Back-to-Clevo. |
| **clevofan** | Command-line tool — diagnostics, scripting, and the same control engine. |

---

## ⚠️ Read first — safety

This writes **directly to the laptop's Embedded Controller (EC)** to control the fans. Use at your
own risk. The design is safety-first:

- Only the **CPU fan** runs the quiet curve. The **GPU fan** is only quieted while the dGPU is
  *asleep* **and** the CPU is cool (the two fans share a heatpipe), otherwise it stays on the EC.
- **Thermal auto-revert:** at **90 °C** both fans are handed back to the factory EC curve until things
  cool below 80 °C; the CPU fan is forced to 100 % at 86 °C before that.
- **Never stranded:** every exit path — normal, Ctrl+C, crash, EC read failure — restores factory
  fan control, and if that ever fails it forces the fans to **100 %** rather than leave them low.
- **Back to Clevo default** (or a reboot) fully restores the stock experience.

---

## Compatibility

Developed and confirmed on:

- **Lambda Tensorbook** = Clevo **PC50HS** (Windows model `PC5x_7xHP_HR_HS`)
- Intel **i7-11800H**, NVIDIA **RTX 3080 Laptop GPU** (Optimus/hybrid), ITE IT5570E EC
- Insyde BIOS `1.07.04LAM`, **Windows 10 Pro 22H2**

It very likely works on other Clevo **PC50** (HP/HR/HS/DN) rebrands — System76 Oryx Pro 8, Sager
NP8753, XMG/Schenker, Tuxedo, Tongfang — but **verify the EC registers first** with `clevofan dump`
(compare against HWiNFO). The fan protocol is the standard Clevo `0x99` command on EC ports
`0x66`/`0x62`.

---

## Prerequisites (one-time)

1. **Windows 10/11 x64**, and you must run the tools **as Administrator**.
2. **.NET 8 SDK** — <https://dotnet.microsoft.com/download/dotnet/8.0>
   (or `winget install Microsoft.DotNet.SDK.8`).
3. **PawnIO** signed kernel driver — <https://pawnio.eu>. This is how we do EC port I/O safely on
   modern Windows (no WinRing0, no disabling driver-signature enforcement / Memory Integrity).
4. **`LpcACPIEC.bin`** — the signed PawnIO module that whitelists exactly EC ports `0x62`/`0x66`.
   Download from <https://github.com/namazso/PawnIO.Modules/releases> and drop it into
   `ClevoFan\bin\Release\net8.0-windows\`. The tray build copies it next to its own exe automatically.

---

## Build

From the repo root in PowerShell:

```powershell
.\build.ps1
```

…or build each project directly:

```powershell
dotnet build ClevoFan\ClevoFan.csproj         -c Release
dotnet build ClevoFan.Tray\ClevoFan.Tray.csproj -c Release
```

> `PawnIOLib.dll` is found automatically from `C:\Program Files\PawnIO`. Make sure `LpcACPIEC.bin`
> is in `ClevoFan\bin\Release\net8.0-windows\` before building the tray (it copies from there).

---

## Use — tray app (recommended)

```powershell
.\run-tray.ps1     # builds if needed, then launches elevated (UAC prompt)
```

The icon appears in the **hidden-icons overflow** — click the `^` chevron next to the clock and drag
"Clevo Fan" onto the taskbar to pin it. Left- or right-click for the menu:

- **Quiet mode** — stops the Clevo fan stack and runs the quiet CPU curve. Toggle off to revert.
- **Max cooldown (hold)** — both fans to 100 % for a quick cooldown.
- **Back to Clevo default** — restores the fans and restarts the Clevo service + FnKey.
- **Open Clevo Control Center** — launch the factory UI when you want it.
- **Exit** — restores the fans *and* brings Clevo back, so you're never left without fan control.

The status line at the top of the menu (and the tooltip) shows live CPU temp / fan % / RPM.

---

## Use — command line

From an **Administrator** terminal, in `ClevoFan\bin\Release\net8.0-windows\`:

| Command | What it does |
|---|---|
| `clevofan dump` | Read-only EC register dump + decoded temps/RPM/duty. **Start here to verify a new unit.** |
| `clevofan read` | One-line snapshot of temps / RPM / duty. |
| `clevofan monitor [--apply]` | Live view of the curve's decision. Read-only unless `--apply`. |
| `clevofan set <percent>` | Hold the CPU fan at a fixed duty; restores EC auto on exit. |
| `clevofan max` | Force BOTH fans to 100 % (quick cooldown); restores EC auto on exit. |
| `clevofan run` | Run the quiet curve continuously with thermal auto-revert. |
| `clevofan auto` / `panic` | **Back to normal** — hand both fans to the EC firmware and exit. |

Options: `--module <path>` (LpcACPIEC.bin location), `--divisor <n>` (RPM divisor; default 1966080,
try 2156220 if RPM looks wrong). `restore-normal.ps1` in `ClevoFan\` also restores fans **and**
restarts the Clevo service.

---

## How it works

- **EC access via PawnIO** — we load the signed `LpcACPIEC` module (port whitelist is literally
  `0x62`/`0x66`) and P/Invoke `pawnio_execute` to do byte-level port I/O. (`Native/PawnIo.cs`)
- **Clevo fan protocol** — read EC RAM via the ACPI `0x80` command (CPU temp `0x07`, GPU temp `0xCD`,
  duty `0xCE`, RPM `0xD0/0xD1`); set fan duty via the Clevo vendor command `0x99 → selector → duty`
  (CPU = `0x01`, GPU = `0x02`); return a fan to firmware control via `0x99,0x01,0x00 → 0x99,0xFF,0x01`.
  (`Ec/ClevoEc.cs`)
- **Control core** — quiet curve + hysteresis, GPU policy, thermal auto-revert, bulletproof restore,
  all thread-safe and shared by both apps. (`Control/FanController.cs`, `Control/FanCurve.cs`)
- **Flip Clevo on/off** — stop/start `CCDCHUService` and the `FnKey` UWP app (launched by AUMID) so
  the factory controller and ours never fight over the EC. (`Service/ClevoServices.cs`)

Tune the curve in `ClevoFan/Control/FanCurve.cs` (`FanCurve.Quiet()`), thresholds in
`FanController.cs`.

---

## Troubleshooting

- **"Unable to load DLL 'PawnIOLib'"** — PawnIO isn't installed, or you're not elevated. Install from
  pawnio.eu and run as Administrator.
- **"LpcACPIEC module not found"** — put `LpcACPIEC.bin` next to the exe (see Prerequisites).
- **Tray app "does nothing"** — its icon is in the hidden-icons overflow (`^`). Also make sure you
  ran the elevated exe, not `clevofan.exe` (the console tool, which just prints help).
- **"EC busy" / fans fighting back** — the Clevo stack is still controlling the EC. Use the tray's
  Quiet mode (it stops the Clevo stack for you), or stop it manually:
  `Stop-Service CCDCHUService; taskkill /F /IM FnKey.exe`.
- **RPM looks wrong in `dump`** — try `clevofan dump --divisor 2156220`.

---

## Repo layout

```
ClevoFan/            CLI tool + the shared core
  Native/PawnIo.cs       PawnIO driver wrapper (port I/O)
  Ec/ClevoEc.cs          Clevo EC protocol
  Control/FanCurve.cs     temp→duty curve
  Control/FanController.cs control core (shared)
  Service/ClevoServices.cs Clevo service/app toggling (shared)
  Program.cs             CLI commands
  restore-normal.ps1     full "back to factory" script
ClevoFan.Tray/       WinForms system-tray app (links the shared core)
build.ps1            build both projects
run-tray.ps1         build (if needed) + launch the tray elevated
```

---

## Credits

Clevo fan protocol confirmed across TUXEDO `tuxedo-fan-control` (`ec_access.cc`), SkyLandTW
`ClevoECView` / `clevo-indicator`, `simopil/clevofan`, and the HWiNFO "FAN control on Clevo based
laptops" thread. EC driver: **PawnIO** by namazso (`LpcACPIEC` module). Built for a Lambda Tensorbook;
this tool deliberately controls only the CPU fan on a quiet curve and treats GPU cooling conservatively.

Personal project, provided as-is with no warranty. Add a `LICENSE` if you intend to share it.
