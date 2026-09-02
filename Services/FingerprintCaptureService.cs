using BridgeCapture.Models;
using ZKFPEngXControl;

namespace BridgeCapture.Services;

/// <summary>
/// BackgroundService that initialises the ZKFPEngX SDK (biokey.ocx) on a
/// dedicated STA thread and fires a WebSocket broadcast whenever a fingerprint
/// is successfully captured.
///
/// SDK details (discovered from COM registry + interop reflection):
///   Type    : ZKFPEngXControl.ZKFPEngXClass
///   ProgID  : ZKFPEngXControl.ZKFPEngX
///   CLSID   : {CA69969C-2F27-41D3-954D-A48B941C3BA7}
///   TypeLib : {D95CB779-00CB-4B49-97B9-9F0B61CAB3C1} v4.0
///   Path    : C:\Program Files (x86)\FPSensor\Biokey\biokey.ocx
///
/// Capture flow:
///   1. InitEngine()              — initialise the fingerprint engine
///   2. SensorCount               — verify at least one device is connected
///   3. BeginCapture()            — start continuous capture loop
///   4. OnCapture(result, tmpl)   — fires when a finger is placed; template
///      is passed directly in the event args
///   5. GetFPImageBase64()        — get the image as a ready-to-use base64 string
///   6. Broadcast JSON payload    — push to all WebSocket clients
///   7. On shutdown: CancelCapture() + EndEngine()
/// </summary>
public class FingerprintCaptureService : BackgroundService
{
    private readonly ILogger<FingerprintCaptureService> _logger;
    private readonly FingerprintState _state;
    private readonly WebSocketBroadcaster _broadcaster;

    // The COM object and the STA thread it lives on.
    // NEVER access _fp from any other thread.
    private ZKFPEngXClass? _fp;

    // Signals ExecuteAsync that SDK initialisation has completed (or failed).
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

    // ── BackgroundService lifecycle ──────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FingerprintCaptureService starting...");

        // biokey.ocx is a 32-bit Apartment-threaded COM component.
        // It MUST be created, used, and cleaned up on a single STA thread.
        var sdkThread = new Thread(RunSdkSta)
        {
            Name         = "ZKFPEngX-STA",
            IsBackground = true
        };
        sdkThread.SetApartmentState(ApartmentState.STA);
        sdkThread.Start();

        // Block until SDK init finishes (success or failure)
        await Task.Run(() => _sdkReady.Wait(stoppingToken), stoppingToken);

        // Stay alive until the host requests shutdown
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        finally
        {
            // Ask the STA thread to clean up and exit its message loop
            Application.Exit();
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    // ── STA thread: SDK init + WinForms message pump ─────────────────────────

    private void RunSdkSta()
    {
        try
        {
            // Ensure Windows can find ZKTeco native DLLs (zkfputil.dll, ZKFPCap_ASYNC.dll, etc.)
            string sdkDir = @"C:\Program Files (x86)\FPSensor\Biokey";
            if (Directory.Exists(sdkDir))
            {
                SetDllDirectory(sdkDir);
            }

            // 1. Create the COM coclass (ZKFPEngXClass exposes COM events to .NET)
            _fp = new ZKFPEngXClass();

            // 2. Initialise the fingerprint engine
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
                _logger.LogWarning(
                    "No fingerprint sensor detected. Plug in the ZKTeco USB reader and restart.");
                _sdkReady.Set();
                return;
            }

            _logger.LogInformation(
                "ZKFPEngX: using sensor[{Idx}]  image size: {W}×{H}  template len: {Len}",
                _fp.SensorIndex, _fp.ImageWidth, _fp.ImageHeight, _fp.TemplateLen);

            // 3. Register event handlers BEFORE calling BeginCapture
            _fp.OnCapture        += OnCapture;
            _fp.OnFingerTouching += OnFingerTouching;
            _fp.OnFingerLeaving  += OnFingerLeaving;

            // 4. Start the continuous capture loop.
            //    The SDK will fire OnCapture each time a quality scan is completed.
            _fp.BeginCapture();
            _logger.LogInformation(
                "ZKFPEngX capture loop started. Place finger on scanner...");

            // Signal that we are ready before blocking
            _sdkReady.Set();

            // 5. Pump the STA message loop so COM events keep arriving.
            //    Application.Run() blocks until Application.Exit() is called
            //    (triggered from ExecuteAsync's finally block on shutdown).
            Application.Run();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "ZKFPEngX SDK initialisation failed");
            _sdkReady.Set(); // always unblock ExecuteAsync
        }
        finally
        {
            CleanupSdk();
        }
    }

    // ── SDK event handlers (called on the STA thread) ────────────────────────

    /// <summary>
    /// Fires when the scanner detects a finger touching the sensor plate.
    /// </summary>
    private void OnFingerTouching()
    {
        _logger.LogDebug("ZKFPEngX: finger touching sensor...");
    }

    /// <summary>
    /// Fires when the finger is lifted off the sensor.
    /// </summary>
    private void OnFingerLeaving()
    {
        _logger.LogDebug("ZKFPEngX: finger left sensor.");
    }

    /// <summary>
    /// Fires when a complete fingerprint capture is done.
    ///
    /// Parameters (from IZKFPEngXEvents_OnCaptureEventHandler):
    ///   ActionResult — true = capture succeeded, false = quality too low / failed
    ///   ATemplate   — the raw fingerprint template bytes (passed directly by the SDK)
    /// </summary>
    private async void OnCapture(bool ActionResult, object ATemplate)
    {
        if (!ActionResult)
        {
            _logger.LogWarning("ZKFPEngX: capture failed or quality too low. Ask user to try again.");
            return;
        }

        try
        {
            // ── Extract template ─────────────────────────────────────────────
            byte[] templateBytes = ATemplate is byte[] b
                ? b
                : Array.Empty<byte>();

            if (templateBytes.Length == 0)
            {
                _logger.LogWarning("ZKFPEngX: OnCapture fired but template is empty.");
                return;
            }

            // ── Extract fingerprint image ────────────────────────────────────
            // GetFPImageBase64() returns a ready-made base64 string of the BMP image.
            // This avoids any manual byte[] → base64 conversion for the image.
            string imageBase64 = _fp!.GetFPImageBase64() ?? string.Empty;

            // Also keep the raw bytes in state (from the base64 string)
            byte[] imageBytes = imageBase64.Length > 0
                ? Convert.FromBase64String(imageBase64)
                : Array.Empty<byte>();

            _logger.LogInformation(
                "Fingerprint captured ✓  template: {TmplLen} bytes  image base64: {ImgLen} chars",
                templateBytes.Length, imageBase64.Length);

            // ── Store in memory ──────────────────────────────────────────────
            _state.Set(templateBytes, imageBytes);

            // ── Build WebSocket payload ──────────────────────────────────────
            var payload = new FingerprintPayload
            {
                // The browser uses this to render <img src="data:image/bmp;base64,..." />
                Base64Image    = imageBase64,
                // The .NET Core web app saves this to SQL Server
                TemplateBase64 = Convert.ToBase64String(templateBytes),
                CapturedAt     = DateTime.UtcNow
            };

            // ── Broadcast to all connected browser WebSocket clients ──────────
            await _broadcaster.BroadcastAsync(payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing ZKFPEngX capture event");
        }
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

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

            _logger.LogInformation("ZKFPEngX SDK cleaned up.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during ZKFPEngX cleanup (non-fatal)");
        }
    }
}
