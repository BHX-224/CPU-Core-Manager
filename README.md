# CPU Core Manager

A small Windows utility for seeing exactly what your CPU has and deciding which cores each application runs on — including making an app *believe* the machine has a different number of processors.

C# / .NET 8 WinForms. Single project, zero external dependencies.

## Features

- **CPU overview** — vendor, logical core count, and hybrid P/E-core breakdown (Intel 12th gen+) detected straight from the Windows registry (`HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor`). No WMI, no CPUID FFI.
- **Visual core grid** — every logical processor as a checkbox. On hybrid Intel CPUs, P-cores are highlighted blue and E-cores amber (Windows assigns P-core logical IDs first on Alder Lake+, so "First N" lands on P-cores). Presets: *All cores*, *First 8*, *First 16*, *Clear all*.
- **Per-app affinity profiles** — pin any executable to the selected core mask. Profiles persist across reboots and are auto-applied whenever that app starts, even if you launch it normally from Explorer or a shortcut.
- **"Processor count lie"** — launch an app so it reports N processors: sets `NUMBER_OF_PROCESSORS` for the child process (catches early readers) *and* pins its affinity to match, so the perceived core count is physically consistent with what it can actually run on. No DLL injection, no shims, no hooks.
- **Hybrid-aware lie value** — a separate reported-count setting used automatically when the machine is detected as hybrid Intel, so you can claim e.g. 16 fast cores instead of 16 mixed ones.
- **Start with Windows** — optional `HKCU` Run entry keeps auto-apply active after reboot; minimize to system tray (double-click the icon to restore).

## How the count-lie works

Two mechanisms, combined:

1. **Environment variable.** The child process is spawned with `NUMBER_OF_PROCESSORS=N`. Anything that reads that variable — many C/C++ runtimes, game engines, and thread-pool heuristics — sees N processors from its very first instruction.
2. **Affinity pinning.** The process's affinity mask is set to exactly the selected cores (with a short retry loop, since some processes take a moment after spawn before the mask can be written). Apps that query the OS directly (`GetSystemInfo`) still see the real hardware count, but their threads are confined to N cores anyway — so behavior matches an N-core machine.

The auto-apply watcher polls running processes every 300 ms and re-applies saved affinity masks by executable name, so profiled apps get pinned no matter how they're started. (The env-var part only applies at launch time through this tool; the watcher can pin cores on already-running or externally-launched processes.)

## Build from source

Requirements (that's all — no `requirements.txt` equivalent needed):

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`winget install Microsoft.DotNet.SDK.8`)

```bat
git clone https://github.com/<you>/CpuCoreManager.git
cd CpuCoreManager
dotnet build -c Release
bin\Release\net8.0-windows\CpuCoreManager.exe
```

The exe needs the **.NET 8 Desktop Runtime** on the target machine, or publish self-contained for a standalone binary (`dotnet publish -c Release -r win-x64 --self-contained`).

## Signing & SmartScreen warnings

Running an unsigned `CpuCoreManager.exe` downloaded from somewhere makes Windows show "Windows protected your PC" / untrusted-publisher warnings — that's normal for any unsigned exe, not a defect. Three ways to handle it:

1. **Build from source** (zero cost). A locally compiled exe has no download provenance, so SmartScreen doesn't fire. This is the main reason this repo ships with build instructions.
2. **Self-signed certificate** (free; trusted per machine). One-time setup in PowerShell:
   ```powershell
   $cert = New-SelfSignedCertificate -Type CodeSigningCert `
     -Subject "CN=CpuCoreManager" -KeyUsage DigitalSignature `
     -FriendlyName "CpuCoreManager Signing"
   signtool sign /sha1 $cert.Thumbprint /fd SHA256 bin\Release\net8.0-windows\CpuCoreManager.exe
   Export-Certificate -Cert $cert -FilePath .\CpuCoreManagerRoot.cer
   ```
   Then on each target machine, double-click `CpuCoreManagerRoot.cer` → *Install Certificate* → Current User → place in **Trusted Root Certification Authorities**. (`signtool` ships with the Windows SDK.) This removes the warning only where you installed the root.
3. **CA-issued code-signing certificate** (OV/EV, ~$100–200/yr) — the real fix for public distribution: trusted out of the box on every machine, and SmartScreen reputation builds with download volume.

## Self-test

```bat
bin\Release\net8.0-windows\CpuCoreManager.exe --selftest
```

Runs headless checks and prints `SELFTEST PASS` / `SELFTEST FAIL: ...` (exit code 0/1):

- mask math (`ToMask` / `FromMask` / range formatting)
- CPU detection sanity — vendor string, logical count vs `Environment.ProcessorCount`, hybrid P/E plausibility
- JSON profile round-trip in a temp directory
- real process affinity set/read-back on a spawned `cmd`
- env-var lie path — child must echo back the faked processor count

## Data files

| File | Purpose |
|---|---|
| `%APPDATA%\CpuCoreManager\profiles.json` | Saved profiles (exe path + core mask) |
| `%APPDATA%\CpuCoreManager\settings.json` | Reported-processor-count values (1–16), regular and hybrid |
| `crash.log` (next to the exe) | Unhandled exceptions, also shown as a dialog |

Corrupt config files are ignored rather than fatal — the app starts fresh with defaults.

## Project layout

```
CpuCoreManager.csproj   single-project build (net8.0-windows, no external packages)
Program.cs              the entire app: UI, CPU detection, profiles, watcher, self-test
app.ico                 application icon
.gitignore
README.md
```

## Limitations

- Affinity masks are 64-bit (`ulong`) → up to **64 logical processors**. Any current PC is far below that.
- E-core count is a heuristic: on Intel, every logical processor whose base clock (`~MHz` in the registry) is below the max observed is counted as an E-core. Correct for current SKUs; it does not call the per-LP topology API.
- The watcher matches by executable **filename**, so two different apps with the same name share a profile.
- Setting affinity on elevated processes requires this app to run elevated (the retry loop just gives up quietly otherwise).
