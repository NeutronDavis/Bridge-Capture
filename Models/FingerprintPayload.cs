namespace BridgeCapture.Models;

public class FingerprintPayload
{
    /// <summary>Base64-encoded BMP/PNG of the fingerprint image for on-screen display.</summary>
    public string Base64Image { get; set; } = string.Empty;

    /// <summary>Base64-encoded raw fingerprint template to be saved to the database.</summary>
    public string TemplateBase64 { get; set; } = string.Empty;

    /// <summary>UTC timestamp of when the scan event occurred.</summary>
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
}
