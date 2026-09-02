---
name: bridge-capture-impl
description: >-
  Full implementation plan and step-by-step guide for the Bridge-Capture
  console application. Covers Kestrel WebSocket server, ZKTeco ZKFPEngX SDK
  fingerprint capture, Windows Service + System Tray hosting, wss:// TLS setup with
  localhost.pfx, in-memory fingerprint state management, test.html verification,
  and Inno Setup installer creation.
---

# Bridge-Capture Implementation Guide

## Project Overview

**Bridge-Capture** is a .NET 10 (win-x86) console application that acts as a local bridge
between a ZKTeco USB fingerprint scanner (`biokey.ocx`) and a remote .NET Core web application.

- GitHub Repository: [https://github.com/NeutronDavis/Bridge-Capture.git](https://github.com/NeutronDavis/Bridge-Capture.git)
- Architecture: **x86** (required because `biokey.ocx` is a 32-bit COM control).
- Service: Runs **silently as a Windows Service** (`BridgeCaptureService`) and shows a **System Tray icon**.
- Server: Exposes a **Kestrel WebSocket server** over `wss://localhost:5050`.
- Client Flow:
  - The browser page connects via JavaScript WebSocket to `wss://localhost:5050/ws/fingerprint`.
  - Calling `POST https://localhost:5050/api/capture/start` clears any in-memory state.
  - When a finger is placed on the scanner, the app pushes a JSON payload containing:
    - `base64Image` — base64 BMP image string (for `<img src="data:image/bmp;base64,..." />`).
    - `templateBase64` — base64-encoded raw fingerprint template (for saving to DB).
    - `capturedAt` — UTC timestamp.
  - The remote .NET Core web app receives `templateBase64`, decodes it via `Convert.FromBase64String()`, and saves the raw bytes to SQL Server as `VARBINARY(MAX)`.

---

## System Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                  CLIENT MACHINE                             │
│                                                             │
│  ┌───────────────────────────────────────────────────────┐  │
│  │  Bridge-Capture (.NET 10 Console / Windows Service)   │  │
│  │  Platform: win-x86                                    │  │
│  │                                                       │  │
│  │  ┌─────────────┐   ┌──────────────────────────────┐  │  │
│  │  │ Kestrel     │   │ FingerprintCaptureService     │  │  │
│  │  │ wss://:5050 │◄──│ (BackgroundService / STA)     │  │  │
│  │  │             │   │ ZKFPEngXClass (biokey.ocx)   │  │  │
│  │  │ POST        │   │ SetDllDirectory FPSensor     │  │  │
│  │  │ /api/capture│   │ Stores template in-memory    │  │  │
│  │  │ /start      │   └──────────────────────────────┘  │  │
│  │  │             │                                      │  │
│  │  │ WS          │   ┌──────────────────────────────┐  │  │
│  │  │ /ws/        │◄──│ WebSocketBroadcaster          │  │  │
│  │  │ fingerprint │   │ Manages connected clients    │  │  │
│  │  └─────────────┘   └──────────────────────────────┘  │  │
│  │                                                       │  │
│  │  ┌─────────────────────────────────────────────────┐  │  │
│  │  │ TrayApplicationContext (STA Thread)             │  │  │
│  │  │ NotifyIcon + ContextMenuStrip                   │  │  │
│  │  └─────────────────────────────────────────────────┘  │  │
│  └───────────────────────────────────────────────────────┘  │
│                                                             │
│  ZKTeco USB Scanner (biokey.ocx) ─────────────────────────► │
└─────────────────────────────────────────────────────────────┘
                 │ wss://localhost:5050/ws/fingerprint
                 │ (TLS via localhost.pfx / certutil Root)
                 ▼
┌────────────────────────────────────────────────────────────┐
│  Browser (on client machine)                               │
│  .NET Core Web App page (hosted on production server)      │
│                                                            │
│  1. User clicks "Start Capture"                            │
│     → fetch("https://localhost:5050/api/capture/start")    │
│                                                            │
│  2. JS WebSocket connects:                                 │
│     const ws = new WebSocket("wss://localhost:5050/ws/fingerprint") │
│                                                            │
│  3. Receives JSON push:                                    │
│     { base64Image: "...", templateBase64: "..." }          │
│                                                            │
│  4. User clicks "Save"                                     │
│     → POST to .NET Core API → Convert.FromBase64String()   │
│     → EF Core → SQL Server VARBINARY(MAX)                  │
└────────────────────────────────────────────────────────────┘
```

---

## Key Technical Decisions & Fixes

1. **32-Bit (x86) Process Target**: `biokey.ocx` is a 32-bit COM control (located in `C:\Program Files (x86)\FPSensor\Biokey\biokey.ocx`). The .NET application MUST target `<PlatformTarget>x86</PlatformTarget>`.
2. **Native DLL Search Path (`SetDllDirectory`)**: Before instantiating `ZKFPEngXClass`, the app calls `SetDllDirectory(@"C:\Program Files (x86)\FPSensor\Biokey")` so Windows can locate native helpers like `zkfputil.dll` and `ZKFPCap_ASYNC.dll`.
3. **STA Threading & Message Pump**: `ZKFPEngXClass` requires an STA thread. `FingerprintCaptureService` spawns a dedicated STA thread and calls `Application.Run()` to drive the WinForms message loop required for COM events (`OnCapture`).
4. **Graceful Handling for Code 2**: If `InitEngine()` returns code `2`, the scanner is not plugged into USB. The app logs a clean warning instead of throwing an unhandled exception.
5. **Certificate Trust (`localhost.pfx`)**: The installer bundles `localhost.pfx` and imports it into the machine's Trusted Root Certification Authorities using `certutil.exe`. Kestrel loads it via `appsettings.json`.

---

## File Structure

```
Bridge-capture/
├── Bridge-capture.csproj              ← Sdk="Microsoft.NET.Sdk.Web", x86, net10.0-windows
├── Program.cs                         ← Host, Kestrel, CORS, WS & REST routes, Tray
├── appsettings.json                   ← Port 5050, Kestrel PFX cert config
├── localhost.pfx                      ← Self-signed SSL certificate for localhost
├── installer.iss                      ← Inno Setup installer script
├── test.html                          ← Web test interface
│
├── lib/
│   └── Interop.ZKFPEngXControl.dll    ← Generated COM Interop assembly
│
├── Models/
│   └── FingerprintPayload.cs          ← JSON payload DTO
│
├── Services/
│   ├── FingerprintCaptureService.cs   ← ZKFPEngX SDK listener (STA thread)
│   ├── FingerprintState.cs            ← Thread-safe in-memory state
│   └── WebSocketBroadcaster.cs        ← WebSocket client connection manager
│
└── Tray/
    └── TrayApplicationContext.cs      ← WinForms NotifyIcon system tray context
```

---

## Source Code Reference

### `Bridge-capture.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>BridgeCapture</RootNamespace>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseWindowsForms>true</UseWindowsForms>
    <PlatformTarget>x86</PlatformTarget>
  </PropertyGroup>

  <ItemGroup>
    <None Update="localhost.pfx">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="9.0.0" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include="Interop.ZKFPEngXControl">
      <HintPath>lib\Interop.ZKFPEngXControl.dll</HintPath>
      <EmbedInteropTypes>false</EmbedInteropTypes>
    </Reference>
  </ItemGroup>

</Project>
```

---

### `Services/FingerprintCaptureService.cs`
```csharp
using BridgeCapture.Models;
using ZKFPEngXControl;

namespace BridgeCapture.Services;

public class FingerprintCaptureService : BackgroundService
{
    private readonly ILogger<FingerprintCaptureService> _logger;
    private readonly FingerprintState _state;
    private readonly WebSocketBroadcaster _broadcaster;

    private ZKFPEngXClass? _fp;
    private readonly ManualResetEventSlim _sdkReady = new(false);

    public FingerprintCaptureService(
        ILogger<FingerprintCaptureService> logger,
        FingerprintState state,
        WebSocketBroadcaster broadcaster)
    {
        _logger      = logger;
        _state       = state;
        _broadcaster = broadcaster;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FingerprintCaptureService starting...");

        var sdkThread = new Thread(RunSdkSta)
        {
            Name         = "ZKFPEngX-STA",
            IsBackground = true
        };
        sdkThread.SetApartmentState(ApartmentState.STA);
        sdkThread.Start();

        await Task.Run(() => _sdkReady.Wait(stoppingToken), stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            Application.Exit();
        }
    }

    private void RunSdkSta()
    {
        try
        {
            string sdkDir = @"C:\Program Files (x86)\FPSensor\Biokey";
            if (Directory.Exists(sdkDir))
            {
                SetDllDirectory(sdkDir);
            }

            _fp = new ZKFPEngXClass();

            int initResult = _fp.InitEngine();
            if (initResult != 0)
            {
                if (initResult == 2)
                {
                    _logger.LogWarning("ZKTeco SDK InitEngine returned code 2: Scanner is not plugged in. Please connect your ZKTeco USB scanner and restart.");
                }
                else
                {
                    _logger.LogError("ZKTeco.InitEngine() failed with code {Code}.", initResult);
                }
                _sdkReady.Set();
                return;
            }

            _logger.LogInformation(
                "ZKFPEngX engine initialised. SDK version: {Ver}, Sensor count: {Count}",
                _fp.FPEngineVersion, _fp.SensorCount);

            if (_fp.SensorCount == 0)
            {
                _logger.LogWarning("No fingerprint sensor detected. Plug in the ZKTeco USB reader and restart.");
                _sdkReady.Set();
                return;
            }

            _fp.OnCapture        += OnCapture;
            _fp.OnFingerTouching += OnFingerTouching;
            _fp.OnFingerLeaving  += OnFingerLeaving;

            _fp.BeginCapture();
            _logger.LogInformation("ZKFPEngX capture loop started. Place finger on scanner...");

            _sdkReady.Set();

            Application.Run();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "ZKFPEngX SDK initialisation failed");
            _sdkReady.Set();
        }
        finally
        {
            CleanupSdk();
        }
    }

    private void OnFingerTouching() => _logger.LogDebug("ZKFPEngX: finger touching sensor...");
    private void OnFingerLeaving()  => _logger.LogDebug("ZKFPEngX: finger left sensor.");

    private async void OnCapture(bool ActionResult, object ATemplate)
    {
        if (!ActionResult)
        {
            _logger.LogWarning("ZKFPEngX: capture failed or quality too low.");
            return;
        }

        try
        {
            byte[] templateBytes = ATemplate is byte[] b ? b : Array.Empty<byte>();
            if (templateBytes.Length == 0) return;

            string imageBase64 = _fp!.GetFPImageBase64() ?? string.Empty;
            byte[] imageBytes = imageBase64.Length > 0
                ? Convert.FromBase64String(imageBase64)
                : Array.Empty<byte>();

            _logger.LogInformation("Fingerprint captured ✓ template: {TmplLen} bytes", templateBytes.Length);

            _state.Set(templateBytes, imageBytes);

            var payload = new FingerprintPayload
            {
                Base64Image    = imageBase64,
                TemplateBase64 = Convert.ToBase64String(templateBytes),
                CapturedAt     = DateTime.UtcNow
            };

            await _broadcaster.BroadcastAsync(payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing ZKFPEngX capture event");
        }
    }

    private void CleanupSdk()
    {
        if (_fp is null) return;
        try
        {
            _fp.OnCapture        -= OnCapture;
            _fp.OnFingerTouching -= OnFingerTouching;
            _fp.OnFingerLeaving  -= OnFingerLeaving;
            _fp.CancelCapture();
            _fp.EndEngine();
            System.Runtime.InteropServices.Marshal.ReleaseComObject(_fp);
            _fp = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during ZKFPEngX cleanup");
        }
    }
}
```

---

## Build & Publishing Commands

### 1. Local Run for Testing
```powershell
dotnet run --project "Bridge-capture.csproj"
```

### 2. Publish Self-Contained Win-x86 Application
```powershell
dotnet publish "Bridge-capture.csproj" -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```

### 3. Build Standalone Installer Package (`BridgeCaptureSetup.exe`)
```powershell
# Requires Inno Setup 6
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer.iss
```
Output executable: `Output/BridgeCaptureSetup.exe`.

---

## Git Workflow

To commit and push updates:
```powershell
git add .
git commit -m "Update implementation"
git push origin main
```
