---
name: bridge-capture-impl
description: >-
  Full implementation plan and step-by-step guide for the Bridge-Capture
  console application. Covers Kestrel WebSocket server, ZKTeco SDK fingerprint
  capture, Windows Service + System Tray hosting, wss:// TLS setup with
  dotnet dev-certs, in-memory fingerprint state management, and the REST API
  for clearing capture state. Use this skill whenever implementing, extending,
  or debugging the Bridge-Capture project.
---

# Bridge-Capture Implementation Plan

## Project Overview

**Bridge-Capture** is a .NET 10 console application that acts as a local bridge
between a ZKTeco USB fingerprint scanner and a remote .NET Core web application.

- It runs **silently as a Windows Service** and shows a **System Tray icon**.
- It exposes a **Kestrel WebSocket server** over `wss://localhost:5050`.
- The browser page (hosted on the remote .NET Core app) connects via JavaScript
  WebSocket directly to `wss://localhost:5050/ws/fingerprint`.
- When a finger is scanned, the app pushes a JSON payload containing:
  - `base64Image` — the fingerprint image as a base64 string (for display)
  - `templateBase64` — the raw fingerprint template (for saving to DB)
- The .NET Core web app receives this payload and saves the template to SQL Server.

---

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                  CLIENT MACHINE                             │
│                                                             │
│  ┌───────────────────────────────────────────────────────┐  │
│  │  Bridge-Capture (.NET 10 Console / Windows Service)   │  │
│  │                                                       │  │
│  │  ┌─────────────┐   ┌──────────────────────────────┐  │  │
│  │  │ Kestrel     │   │ FingerprintCaptureService     │  │  │
│  │  │ wss://:5050 │◄──│ (BackgroundService)           │  │  │
│  │  │             │   │ ZKTeco SDK listener thread    │  │  │
│  │  │ POST        │   │ Stores template in-memory     │  │  │
│  │  │ /api/capture│   └──────────────────────────────┘  │  │
│  │  │ /start      │                                      │  │
│  │  │             │   ┌──────────────────────────────┐  │  │
│  │  │ WS          │   │ WebSocketBroadcaster          │  │  │
│  │  │ /ws/        │◄──│ Manages connected clients     │  │  │
│  │  │ fingerprint │   │ Broadcasts JSON payloads      │  │  │
│  │  └─────────────┘   └──────────────────────────────┘  │  │
│  │                                                       │  │
│  │  ┌─────────────────────────────────────────────────┐  │  │
│  │  │ TrayApplicationContext (STA Thread)             │  │  │
│  │  │ NotifyIcon + ContextMenuStrip                   │  │  │
│  │  └─────────────────────────────────────────────────┘  │  │
│  └───────────────────────────────────────────────────────┘  │
│                                                             │
│  ZKTeco USB Scanner ──────────────────────────────────────► │
└─────────────────────────────────────────────────────────────┘
                 │ wss://localhost:5050/ws/fingerprint
                 │ (TLS via dotnet dev-certs)
                 ▼
┌────────────────────────────────────────────────────────────┐
│  Browser (any machine on the network)                      │
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
│     → POST to .NET Core API → EF Core → SQL Server        │
└────────────────────────────────────────────────────────────┘
```

---

## Phase 0: Prerequisites

### 0.1 Trust the localhost TLS certificate (run once per client machine)

```powershell
dotnet dev-certs https --trust
```

This generates a self-signed certificate trusted by the Windows Certificate
Store and accepted by all major browsers for `localhost`.

> **Automate in installer**: Add this command to your Inno Setup or NSIS
> installer script so end-users never need to run it manually.

### 0.2 ZKTeco SDK Reference

The ZKTeco SDK is already installed. Add the reference to the `.csproj`:

```xml
<ItemGroup>
  <!-- Replace path with your actual SDK DLL location -->
  <Reference Include="zkemkeeper">
    <HintPath>C:\Program Files\ZKTeco\SDK\zkemkeeper.dll</HintPath>
    <EmbedInteropTypes>false</EmbedInteropTypes>
  </Reference>
</ItemGroup>
```

> **Note**: If using the newer libzkfpcsharp (fingerprint-only SDK), use:
> ```xml
> <Reference Include="libzkfpcsharp">
>   <HintPath>PATH_TO\libzkfpcsharp.dll</HintPath>
> </Reference>
> ```

---

## Phase 1: Project Setup

### 1.1 Updated `Bridge-capture.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>BridgeCapture</RootNamespace>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <!-- Required for System Tray (NotifyIcon) on Windows -->
    <UseWindowsForms>true</UseWindowsForms>
    <!-- Required for Windows Service support -->
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>

  <ItemGroup>
    <!-- Windows Service host support -->
    <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.0.0" />
    <!-- ZKTeco SDK reference -->
    <Reference Include="zkemkeeper">
      <HintPath>C:\Program Files\ZKTeco\SDK\zkemkeeper.dll</HintPath>
      <EmbedInteropTypes>false</EmbedInteropTypes>
    </Reference>
  </ItemGroup>

</Project>
```

> **Key change**: Switch `Microsoft.NET.Sdk` → `Microsoft.NET.Sdk.Web`
> to gain access to ASP.NET Core / Kestrel without extra packages.

---

## Phase 2: Project File Structure

```
Bridge-capture/
├── Bridge-capture.csproj
├── Program.cs                         ← Entry point, host builder
├── appsettings.json                   ← Port config, logging
│
├── Services/
│   ├── FingerprintCaptureService.cs   ← BackgroundService, ZKTeco SDK
│   ├── WebSocketBroadcaster.cs        ← Manages WS clients, broadcasts
│   └── FingerprintState.cs            ← In-memory state singleton
│
├── Models/
│   └── FingerprintPayload.cs          ← JSON payload model
│
└── Tray/
    └── TrayApplicationContext.cs      ← System Tray NotifyIcon
```

---

## Phase 3: Implementation — File by File

---

### `Models/FingerprintPayload.cs`

```csharp
namespace BridgeCapture.Models;

public class FingerprintPayload
{
    /// <summary>Base64-encoded PNG/BMP of the fingerprint image for display.</summary>
    public string Base64Image { get; set; } = string.Empty;

    /// <summary>Base64-encoded raw fingerprint template for database storage.</summary>
    public string TemplateBase64 { get; set; } = string.Empty;

    /// <summary>UTC timestamp of when the scan occurred.</summary>
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
}
```

---

### `Services/FingerprintState.cs`

```csharp
namespace BridgeCapture.Services;

/// <summary>
/// Thread-safe in-memory holder for the most recently captured fingerprint.
/// Cleared when the client sends a "Start Capture" request.
/// </summary>
public class FingerprintState
{
    private readonly Lock _lock = new();
    private byte[]? _template;
    private byte[]? _image;

    public void Clear()
    {
        lock (_lock)
        {
            _template = null;
            _image = null;
        }
    }

    public void Set(byte[] template, byte[] image)
    {
        lock (_lock)
        {
            _template = template;
            _image = image;
        }
    }

    public (byte[]? Template, byte[]? Image) Get()
    {
        lock (_lock) => (_template, _image);
    }

    public bool HasData()
    {
        lock (_lock) return _template != null;
    }
}
```

---

### `Services/WebSocketBroadcaster.cs`

```csharp
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BridgeCapture.Models;

namespace BridgeCapture.Services;

/// <summary>
/// Manages all active WebSocket connections and broadcasts fingerprint payloads.
/// </summary>
public class WebSocketBroadcaster
{
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private readonly ILogger<WebSocketBroadcaster> _logger;

    public WebSocketBroadcaster(ILogger<WebSocketBroadcaster> logger)
    {
        _logger = logger;
    }

    public string AddClient(WebSocket socket)
    {
        var id = Guid.NewGuid().ToString();
        _clients[id] = socket;
        _logger.LogInformation("WebSocket client connected: {Id}. Total: {Count}", id, _clients.Count);
        return id;
    }

    public void RemoveClient(string id)
    {
        _clients.TryRemove(id, out _);
        _logger.LogInformation("WebSocket client disconnected: {Id}. Total: {Count}", id, _clients.Count);
    }

    public async Task BroadcastAsync(FingerprintPayload payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);
        var deadClients = new List<string>();

        foreach (var (id, socket) in _clients)
        {
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.SendAsync(segment, WebSocketMessageType.Text, true, ct);
                    _logger.LogInformation("Payload sent to client {Id}", id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send to client {Id}", id);
                    deadClients.Add(id);
                }
            }
            else
            {
                deadClients.Add(id);
            }
        }

        foreach (var id in deadClients)
            RemoveClient(id);
    }

    /// <summary>
    /// Keeps the WebSocket alive, reading until the client disconnects.
    /// </summary>
    public async Task ListenUntilClosedAsync(string clientId, WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket error for client {Id}", clientId);
        }
        finally
        {
            RemoveClient(clientId);
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
        }
    }
}
```

---

### `Services/FingerprintCaptureService.cs`

```csharp
using BridgeCapture.Models;

namespace BridgeCapture.Services;

/// <summary>
/// BackgroundService that initializes the ZKTeco SDK and listens for
/// fingerprint scan events. When a scan occurs, it stores the data
/// in FingerprintState and broadcasts via WebSocketBroadcaster.
/// </summary>
public class FingerprintCaptureService : BackgroundService
{
    private readonly ILogger<FingerprintCaptureService> _logger;
    private readonly FingerprintState _state;
    private readonly WebSocketBroadcaster _broadcaster;

    // Replace with your actual SDK type:
    //   zkemkeeper: private zkemkeeper.CZKEMClass _device = new();
    //   libzkfpcsharp: private ZKFPCapture _device = new();
    private object? _device;

    public FingerprintCaptureService(
        ILogger<FingerprintCaptureService> logger,
        FingerprintState state,
        WebSocketBroadcaster broadcaster)
    {
        _logger = logger;
        _state = state;
        _broadcaster = broadcaster;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FingerprintCaptureService starting...");

        // zkemkeeper COM objects REQUIRE an STA thread
        var sdkThread = new Thread(InitializeSdk);
        sdkThread.SetApartmentState(ApartmentState.STA);
        sdkThread.IsBackground = true;
        sdkThread.Start();

        await Task.Delay(Timeout.Infinite, stoppingToken);
        CleanupSdk();
    }

    private void InitializeSdk()
    {
        try
        {
            // ── zkemkeeper (attendance device over network) ─────────────────
            // var device = new zkemkeeper.CZKEMClass();
            // bool ok = device.Connect_Net("192.168.1.201", 4370);
            // if (!ok) throw new Exception("ZKTeco: Connection failed");
            // device.RegEvent(1, 65535);
            // device.OnFingerFeature += (sEnrollNumber, iFingerIndex, iActionResult, iTmpLength) =>
            // {
            //     // Retrieve template
            //     byte[] tmpl = new byte[iTmpLength];
            //     // ... extract template from device
            //     OnFingerprintCaptured(tmpl, Array.Empty<byte>());
            // };
            // _device = device;

            // ── libzkfpcsharp (USB fingerprint-only scanner) ────────────────
            // var fp = new ZKFPCapture();
            // int ret = fp.Init();
            // if (ret != 0) throw new Exception($"ZKFPCapture Init failed: {ret}");
            // fp.OnImageReceived += (imageData, templateData) =>
            // {
            //     OnFingerprintCaptured(templateData, imageData);
            // };
            // fp.StartCapture();
            // _device = fp;

            _logger.LogInformation("ZKTeco SDK initialized. Waiting for scans...");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "ZKTeco SDK initialization failed");
        }
    }

    private async void OnFingerprintCaptured(byte[] templateBytes, byte[] imageBytes)
    {
        _logger.LogInformation("Fingerprint captured. Template: {Size} bytes", templateBytes.Length);

        _state.Set(templateBytes, imageBytes);

        var payload = new FingerprintPayload
        {
            Base64Image    = Convert.ToBase64String(imageBytes),
            TemplateBase64 = Convert.ToBase64String(templateBytes),
            CapturedAt     = DateTime.UtcNow
        };

        await _broadcaster.BroadcastAsync(payload, CancellationToken.None);
    }

    private void CleanupSdk()
    {
        // e.g. device.Disconnect(); ((IDisposable)_device)?.Dispose();
        _logger.LogInformation("ZKTeco SDK cleaned up.");
    }
}
```

---

### `Program.cs`

```csharp
using BridgeCapture.Services;
using BridgeCapture.Tray;

var builder = WebApplication.CreateBuilder(args);

// ── Windows Service ──────────────────────────────────────────────────────────
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "BridgeCaptureService";
});

// ── Kestrel on wss://localhost:5050 ─────────────────────────────────────────
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenLocalhost(5050, listen =>
    {
        listen.UseHttps(); // auto-picks the dotnet dev-cert
    });
});

// ── DI Services ─────────────────────────────────────────────────────────────
builder.Services.AddSingleton<FingerprintState>();
builder.Services.AddSingleton<WebSocketBroadcaster>();
builder.Services.AddHostedService<FingerprintCaptureService>();

// ── CORS ─────────────────────────────────────────────────────────────────────
builder.Services.AddCors(cors =>
{
    cors.AddDefaultPolicy(policy =>
    {
        policy
            .WithOrigins("https://your-production-app.com") // ← your domain
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();

// ── WebSocket middleware ─────────────────────────────────────────────────────
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

// ── REST: POST /api/capture/start ────────────────────────────────────────────
app.MapPost("/api/capture/start", (FingerprintState state) =>
{
    state.Clear();
    return Results.Ok(new { message = "Ready. Previous fingerprint data cleared." });
});

// ── WebSocket: /ws/fingerprint ───────────────────────────────────────────────
app.Map("/ws/fingerprint", async (HttpContext ctx, WebSocketBroadcaster broadcaster) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    var clientId = broadcaster.AddClient(socket);
    await broadcaster.ListenUntilClosedAsync(clientId, socket, ctx.RequestAborted);
});

// ── System Tray (only in interactive / user session) ─────────────────────────
if (Environment.UserInteractive)
{
    var trayThread = new Thread(() =>
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApplicationContext(app.Lifetime));
    });
    trayThread.SetApartmentState(ApartmentState.STA);
    trayThread.IsBackground = true;
    trayThread.Start();
}

await app.RunAsync();
```

---

### `Tray/TrayApplicationContext.cs`

```csharp
namespace BridgeCapture.Tray;

public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly IHostApplicationLifetime _lifetime;

    public TrayApplicationContext(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Bridge Capture — Running").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Stop Service", null, (_, _) => _lifetime.StopApplication());
        menu.Items.Add("Exit", null, OnExit);

        _trayIcon = new NotifyIcon
        {
            Text             = "Bridge Capture",
            Icon             = SystemIcons.Shield,
            ContextMenuStrip = menu,
            Visible          = true,
        };

        _trayIcon.ShowBalloonTip(3000, "Bridge Capture", "Fingerprint bridge is running.", ToolTipIcon.Info);

        lifetime.ApplicationStopping.Register(() =>
        {
            _trayIcon.Visible = false;
            Application.Exit();
        });
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        _lifetime.StopApplication();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _trayIcon.Dispose();
        base.Dispose(disposing);
    }
}
```

---

### `appsettings.json`

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "BridgeCapture": {
    "Port": 5050,
    "AllowedOrigins": [ "https://your-production-app.com" ]
  }
}
```

---

## Phase 4: Browser-Side JavaScript (.NET Core Razor/Blazor page)

```javascript
let socket = null;

async function startCapture() {
    // 1. Clear previous fingerprint data on the console app
    await fetch("https://localhost:5050/api/capture/start", { method: "POST" });

    // 2. Open WebSocket
    socket = new WebSocket("wss://localhost:5050/ws/fingerprint");

    socket.onopen = () => {
        document.getElementById("status").textContent = "Waiting for scan...";
    };

    socket.onmessage = (event) => {
        const data = JSON.parse(event.data);
        document.getElementById("fp-image").src = "data:image/bmp;base64," + data.base64Image;
        window._currentTemplate = data.templateBase64;
        document.getElementById("status").textContent = "Fingerprint captured!";
        document.getElementById("btn-save").disabled = false;
    };

    socket.onerror = () => {
        document.getElementById("status").textContent =
            "Error: Is Bridge-Capture running? Did you run 'dotnet dev-certs https --trust'?";
    };
}

async function saveFingerprint() {
    if (!window._currentTemplate) return;
    const response = await fetch("/api/employees/fingerprint", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ templateBase64: window._currentTemplate })
    });
    if (response.ok)
        document.getElementById("status").textContent = "Saved successfully!";
}
```

---

## Phase 5: Install as Windows Service

```powershell
# 1. Publish
dotnet publish -c Release -r win-x64 --self-contained true -o ./publish

# 2. Install (run as Administrator)
New-Service -Name "BridgeCaptureService" `
            -BinaryPathName "C:\Path\To\publish\Bridge-capture.exe" `
            -StartupType Automatic `
            -DisplayName "Bridge Capture Fingerprint Service"

Start-Service -Name "BridgeCaptureService"
```

---

## Phase 6: Verification Checklist

- [ ] `dotnet dev-certs https --trust` run on this machine
- [ ] Console app starts, tray icon visible
- [ ] `POST https://localhost:5050/api/capture/start` returns 200
- [ ] Browser JS connects to `wss://localhost:5050/ws/fingerprint` without errors
- [ ] Finger scan triggers `OnFingerprintCaptured`
- [ ] Browser receives JSON with `base64Image` + `templateBase64`
- [ ] Save button POSTs to .NET Core API successfully
- [ ] Windows Service auto-starts after reboot

---

## Known Gotchas

| Issue | Fix |
|---|---|
| Browser blocks `wss://localhost` | Run `dotnet dev-certs https --trust` |
| Mixed Content error | Use `wss://` not `ws://` |
| zkemkeeper COM requires STA | Wrap SDK calls in `Thread` with `ApartmentState.STA` |
| Tray doesn't appear when run as Service | Tray only shown when `Environment.UserInteractive == true` |
| CORS blocks `fetch` to localhost | Add production domain to `WithOrigins(...)` |
| SDK events not firing (zkemkeeper) | Call `device.RegEvent(machineNumber, 65535)` |

---

## Implementation Order

1. `[ ]` Update `.csproj` — switch to `Microsoft.NET.Sdk.Web`, add `UseWindowsForms`, NuGet package
2. `[ ]` Create `Models/FingerprintPayload.cs`
3. `[ ]` Create `Services/FingerprintState.cs`
4. `[ ]` Create `Services/WebSocketBroadcaster.cs`
5. `[ ]` Create `Services/FingerprintCaptureService.cs` — wire your ZKTeco SDK events
6. `[ ]` Create `Tray/TrayApplicationContext.cs`
7. `[ ]` Rewrite `Program.cs`
8. `[ ]` Add/update `appsettings.json`
9. `[ ]` Add browser-side JavaScript to .NET Core web app
10. `[ ]` Run `dotnet dev-certs https --trust` on client machine
11. `[ ]` Build and test end-to-end
12. `[ ]` Publish and install as Windows Service
