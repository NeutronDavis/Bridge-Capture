using BridgeCapture.Services;
using BridgeCapture.Tray;

// ════════════════════════════════════════════════════════════════════════════
//  Bridge-Capture — Entry Point
//  Kestrel WebSocket server + ZKTeco fingerprint bridge + Windows Service
// ════════════════════════════════════════════════════════════════════════════

// ── 0. Single-instance check ──────────────────────────────────────────────────
// Prevent launching a second instance if the Windows Service or background process is already running.
using var mutex = new Mutex(true, "Global\\BridgeCapture_SingleInstance_Mutex", out bool isNewInstance);
if (!isNewInstance)
{
    if (Environment.UserInteractive)
    {
        MessageBox.Show(
            "Bridge Capture is already running in the background or as a Windows Service.",
            "Bridge Capture",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
    return;
}

var builder = WebApplication.CreateBuilder(args);

// ── 1. Windows Service lifecycle ─────────────────────────────────────────────
// UseWindowsService() is a no-op when running as a normal console app,
// so this is safe to leave enabled during development.
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "BridgeCaptureService";
});

// ── 2. Kestrel: wss://localhost:5050 ─────────────────────────────────────────
// Configuration comes from appsettings.json ("Kestrel" section)
// which loads localhost.pfx on target machines.
builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    kestrel.Configure(context.Configuration.GetSection("Kestrel"));
});

// ── 3. Dependency Injection ──────────────────────────────────────────────────
builder.Services.AddSingleton<FingerprintState>();          // in-memory capture state
builder.Services.AddSingleton<WebSocketBroadcaster>();      // WebSocket client manager
builder.Services.AddHostedService<FingerprintCaptureService>(); // ZKTeco SDK listener

// ── 4. CORS ──────────────────────────────────────────────────────────────────
// Allow local HTML test files (file://), local dev web servers, and production domains.
builder.Services.AddCors(cors =>
{
    cors.AddDefaultPolicy(policy =>
    {
        policy
            .SetIsOriginAllowed(_ => true) // Allow local HTML files (file://) and any origin
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// ── 5. Middleware pipeline ────────────────────────────────────────────────────
app.UseCors();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

// ── 6. REST endpoint: POST /api/capture/start ────────────────────────────────
// The browser page calls this before opening the WebSocket to wipe any
// previously captured fingerprint from memory.
app.MapPost("/api/capture/start", (FingerprintState state) =>
{
    state.Clear();
    app.Logger.LogInformation("Capture state cleared via API.");
    return Results.Ok(new { message = "Ready. Previous fingerprint data cleared." });
});

// ── 7. WebSocket endpoint: GET /ws/fingerprint ───────────────────────────────
// The browser JS opens a WebSocket here. When a finger is placed on the
// scanner, a JSON payload is pushed to every connected client.
app.Map("/ws/fingerprint", async (HttpContext ctx, WebSocketBroadcaster broadcaster) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("Expected a WebSocket upgrade request.");
        return;
    }

    using var socket   = await ctx.WebSockets.AcceptWebSocketAsync();
    var       clientId = broadcaster.AddClient(socket);

    // Block until the client disconnects (receive loop inside)
    await broadcaster.ListenUntilClosedAsync(clientId, socket, ctx.RequestAborted);
});

// ── 8. System Tray icon (interactive sessions only) ───────────────────────────
// When running as a pure headless Windows Service there is no desktop session,
// so we skip the WinForms tray entirely.
if (Environment.UserInteractive)
{
    var trayThread = new Thread(() =>
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApplicationContext(app.Lifetime));
    });
    trayThread.SetApartmentState(ApartmentState.STA); // WinForms requires STA
    trayThread.IsBackground = true;
    trayThread.Name         = "SysTray-Thread";
    trayThread.Start();
}

app.Logger.LogInformation("Bridge-Capture listening on wss://localhost:5050");

await app.RunAsync();
