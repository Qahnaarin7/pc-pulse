# PC Pulse

PC Pulse monitors a Windows PC from an Android phone and from a Windows desktop window. Both apps offer System, Light, and Dark appearance modes. The app interface is available in Ukrainian.

## Installation

Download the MSI for your Windows architecture and the APK for your phone from [GitHub Releases](https://github.com/Qahnaarin7/pc-pulse/releases). The MSI bundles the .NET runtime. The phone requires Android 8.0 or later. [Supported Windows versions](https://learn.microsoft.com/dotnet/core/install/windows#supported-versions)

1. If a previous PC Pulse agent is running, exit it to free TCP port 8787.
2. Install the MSI. Open **PC Pulse** from the Start menu, select **Settings**, and copy the pairing code.
3. Install the APK, open PC Pulse, and paste the code. Treat the pairing code like a password. It authorizes monitoring and supported hardware controls.
4. To start PC Pulse in the Windows tray after signing in, enable the startup option in Windows settings inside the app.

Closing or minimizing the Windows window moves it to the tray. Double-click the icon to reopen the window, or right-click it and choose **Exit** to stop the agent. Use **Exit** before upgrading the MSI.

## Access from another network

### Cloudflare Workers

Run the managed relay in your Cloudflare account once. After the PC is registered and the phone receives the updated pairing code, Windows reconnects on startup and Android can reach the PC on mobile data without a VPN client or inbound router ports. The PC and agent must be online.

1. From the repository's `relay` folder, run `npm ci` and `npx wrangler login`.
2. Generate an enrollment key with `node -e "console.log(require('node:crypto').randomBytes(32).toString('base64url'))"`. Put it in a private JSON file with the property `RELAY_ENROLLMENT_KEY`.
3. Deploy with `npx wrangler deploy --secrets-file path-to-private.json`. Copy the HTTPS URL printed by Wrangler. A personal deployment can use `workers.dev`.
4. In Windows PC Pulse, open **Settings → Internet access → Relay settings**, enable Cloudflare, and enter the HTTPS URL and enrollment key. Once connected, select **Copy Cloudflare code**. Paste it under **Settings → Pairing code** on Android, then save the connection. The PC saves its individual host credentials after registering. The Android status shows when the active route uses Cloudflare.

Phone requests and PC responses use end-to-end AES-GCM encryption. The Worker validates separate random routing credentials and forwards encrypted messages. The encryption key stays with the paired apps. The relay processes routing identifiers, timing, and ciphertext length. Keep the enrollment key private. It controls registration of new PCs and belongs outside the repository.

This repository includes the relay and its integration checks. To run the checks against a local Worker, start `npx wrangler dev --local --port 8788` with a disposable enrollment key configured in an ignored `.dev.vars` file, then run `npm test` with that key in `PCPULSE_TEST_ENROLLMENT`. [Cloudflare WebSocket Hibernation](https://developers.cloudflare.com/durable-objects/best-practices/websockets/) · [workers.dev setup](https://developers.cloudflare.com/workers/configuration/routing/workers-dev/)

### LAN and private VPN

PC Pulse works over a local network or a private VPN. For Tailscale, install the client on both devices, join the same tailnet, and allow the phone to reach the PC in the tailnet policy. When the PC's VPN adapter is active, copy a fresh pairing code. A single code can include both Wi-Fi and VPN addresses. Android tries the remaining addresses if the current route stops working.

The agent authenticates requests and accepts local or private VPN addresses. The phone verifies the PC's TLS certificate against the fingerprint in the pairing code. The Windows installer creates TCP 8787 rules for private LAN clients and Tailscale addresses. Other private VPNs can require a separate firewall rule. Generate a new code if the PC's address changes and the old code has no alternate route. [Tailscale connectivity guide](https://tailscale.com/docs/how-to/connect-to-devices)

## Sampling and notifications

| Data or activity | Interval |
| --- | ---: |
| CPU, GPU, and memory dashboard | About 2 seconds |
| Process list | 5 seconds |
| Disk and memory-module sensors with the dashboard open | 60 seconds |
| Windows thermal protection, when enabled | About 2 seconds |
| Android background temperature alerts, when enabled | 5 seconds |

The first per-process CPU measurement takes about a second. CPU values represent each process's share of total CPU time over the displayed measurement window. Memory sorting uses the working set. The Windows dashboard pauses updates while the window is in the tray. A shared sensor cache combines closely spaced requests from the desktop and phone.

Enable **Temperature alerts** in Android settings, save the thresholds, and grant notifications. The defaults are 90 °C warning and 95 °C critical for the CPU, and 80 °C warning and 85 °C critical for the GPU. After setup, Android restarts monitoring after boot when the system and device manufacturer allow it. The background service polls temperatures only and shows a quiet notification with a **Stop** button. Android Doze can delay network checks. A force-stopped app needs to be opened again. [Android Doze behavior](https://developer.android.com/training/monitoring-device-state/doze-standby)

## Automatic full-speed cooling

In **Settings → Thermal protection** on Windows or Android, enable **Full speed at critical temperature** and save the CPU and GPU thresholds. The default limits are **95 °C for the CPU** and **85 °C for the GPU**. Thermal protection starts disabled. Settings are stored on the PC and apply to both apps.

The Windows agent samples locally, including when it is in the tray and the phone is disconnected. The first valid reading at or above either limit requests the vendor's Full Speed mode. When PC Pulse selected Full Speed, it returns to Auto after the temperatures stay at least 5 °C below their limits for 30 seconds. If a sensor that triggered Full Speed stops reporting, PC Pulse holds Full Speed until it can verify recovery. Automatic recovery applies only to Full Speed selected by PC Pulse.

Fan control currently supports the GIGABYTE G6X 9KG with the Control Center driver and an available Auto or Full Speed profile. The thermal feature supplements the hardware's built-in protection. The hardware can react independently of Windows sampling or sensor availability. [GIGABYTE G6X manual](https://download.gigabyte.com/FileList/Manual/GIGABYTE_G6X_E-Manual_V1.0.pdf) · [Intel Digital Thermal Sensor](https://edc.intel.com/content/www/us/en/design/products/platforms/details/raptor-lake-s/13th-generation-core-processors-datasheet-volume-1-of-2/005/digital-thermal-sensor/)

## Hardware compatibility

CPU load, memory usage, and process data come from Windows. NVMe temperatures use the Windows storage interface when available. LibreHardwareMonitor provides compatible CPU, GPU, SMART, and DIMM readings. Some sensors require PawnIO and an elevated Windows agent. An unavailable reading is shown as a dash or a message in its tile. [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/tag/v0.9.6) · [Windows NVMe temperature queries](https://learn.microsoft.com/windows/win32/fileio/working-with-nvme-devices#temperature-queries)

## Build from source

Windows builds use the .NET 10 SDK, Python 3.10 or later, and WiX Toolset 3.14.1.

```powershell
dotnet publish .\desktop\PcPulse.Desktop.csproj -c Release -r win-x64 --self-contained true -o .\dist\win-x64
python .\installer\build.py --wix C:\path\to\wix --payload .\dist\win-x64 --version 0.5.1 --out .\dist\PC-Pulse-Windows-x64.msi
```

For Arm64, use `-r win-arm64` with .NET and `--architecture arm64` with the installer script. Use `--port` to run a Windows test instance on a different TCP port. Pairing credentials and thermal settings are kept in the local Windows user profile.

Android builds use JDK 17 or 21, Android SDK Platform 35, and Android SDK Build Tools 35.0.0.

```powershell
.\android\build.ps1 -JavaHome 'C:\path\to\jdk' `
    -AndroidJar 'C:\path\to\sdk\platforms\android-35\android.jar' `
    -BuildTools 'C:\path\to\sdk\build-tools\35.0.0'
```

The Android build script uses a local key at `android\build\development.p12`. Keep your key to sign updates to a build you distribute.

Run the thermal policy checks with `dotnet run --project .\tests\PcPulse.ThermalChecks.csproj`. These checks use synthetic readings and issue no hardware commands.
