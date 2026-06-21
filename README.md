# PatchCast

PatchCast streams the Windows default playback device (WASAPI loopback) and default microphone over TLS-encrypted TCP to a remote Windows client. Capture uses shared-mode WASAPI, so it does not take exclusive control of or alter either source device.

## Projects

- `PatchCast.Service`: Windows Service and TCP audio server.
- `PatchCast.Client`: WinForms remote player with host, port, connect/disconnect, independent mute, and independent volume controls.
- `PatchCast.Protocol`: framed two-channel audio protocol shared by the service and client.

## Build

Install the .NET 9 SDK, then run:

```powershell
dotnet restore PatchCast.sln
dotnet build PatchCast.sln -c Release
```

## Configure and run the server

Set `PatchCast:Port` and `PatchCast:Password` in `src/PatchCast.Service/appsettings.json`. Change the example password before deployment. The default port is TCP `4747`; permit the selected inbound TCP port in Windows Defender Firewall.

The server automatically creates a five-year self-signed TLS certificate in the service account's Windows Current User certificate store. There is no certificate file or private key to maintain manually. The client trusts the certificate on the first successful password-authenticated connection and pins its SHA-256 fingerprint. A later certificate change is rejected to expose possible interception. If an intentional account change or server reinstall changes the certificate, disconnect and use **Forget Trusted Certificate** before reconnecting. Client pins and the last host/port are stored under `%LOCALAPPDATA%\PatchCast\client-settings.json`.

For an interactive test:

```powershell
dotnet run --project src/PatchCast.Service
```

Publish the service, copy the complete publish folder to its permanent location, and edit `appsettings.json` before installation. Then open **PowerShell as Administrator** in that folder and run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
cd "C:\Program Files\PatchCast"
.\Install-Service.ps1
Get-Service PatchCast
```

The installer registers automatic startup, configures restart-on-failure, starts the service, and opens the configured TCP port in Windows Defender Firewall. By default it securely prompts for the current Windows account password (not a Windows Hello PIN). This lets the service reuse the interactive server's TLS certificate and gives it the best chance of seeing the same audio endpoints. Use `-RunAsLocalSystem` only when Local System is specifically required and can access the audio devices. `-RunAsCurrentUser` remains accepted for compatibility. Uninstall with `.\Uninstall-Service.ps1` from an elevated PowerShell prompt.

For an already-published folder that does not contain the scripts, the equivalent minimal elevated commands are:

```powershell
New-Service -Name PatchCast -DisplayName "PatchCast Audio Service" `
  -BinaryPathName '"C:\Program Files\PatchCast\PatchCast.Service.exe"' `
  -StartupType Automatic
New-NetFirewallRule -DisplayName "PatchCast TCP 4747" -Direction Inbound `
  -Action Allow -Protocol TCP -LocalPort 4747
Start-Service PatchCast
```

Windows services run in Session 0. The service account must have access to the active playback and capture endpoints. For reliable default-device capture, configure the `PatchCast` service to log on as the Windows user whose audio session is being streamed. If policy or audio drivers prohibit Session 0 capture, the production architecture must use a small per-user capture agent feeding the service.

## Run the client

```powershell
dotnet run --project src/PatchCast.Client
```

Enter the server hostname/IP, configured port, and password, then select **Connect**.

The client keeps the entered password in memory for the current run. Selecting **Save password securely for this Windows user** persists it with Windows DPAPI, encrypted for the current Windows account. When disconnected unexpectedly, the client retries after 0, 1, 2, 4, 8, 16, and then 32 seconds, continuing every 32 seconds until **Disconnect** is selected. A connection that remains healthy for 32 seconds resets the next retry delay to zero. The Status row displays `Connected`, `Disconnected`, or `Connecting (Ns)` with a live retry countdown.

The Connection quality row reports received audio bitrate, audio packet rate, TLS version, and negotiated cipher. **Show Log** opens a timestamped activity window containing connection attempts, TLS and password-authentication results, certificate pinning, retry scheduling, errors, and periodic quality samples. TCP provides reliable ordered delivery and does not expose meaningful application-level packet-loss measurements, so PatchCast does not display a fabricated loss percentage.

Server console messages include local timestamps. Routine socket errors caused by a client disconnecting are recorded as normal connection activity; unexpected audio-capture or server failures retain their warning level and exception details.

Both executable projects are configured for self-contained, single-file `win-x64` publishing. Each application's executable and runtime dependencies are bundled into one `.exe`; no separate .NET runtime installation is required. The service's editable `appsettings.json` remains beside its executable so its port and password can be changed without rebuilding. Audio remains PCM/IEEE-float inside TLS, so its contents and password are encrypted in transit.
