namespace BridgeCapture.Services;

/// <summary>
/// Thread-safe singleton that holds the most recently captured fingerprint data in memory.
/// Cleared via POST /api/capture/start before each new capture session.
/// </summary>
public class FingerprintState
{
    private readonly object _lock = new();
    private byte[]? _template;
    private byte[]? _image;

    /// <summary>Wipes the stored template and image ready for a new capture.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _template = null;
            _image    = null;
        }
    }

    /// <summary>Stores the template and image bytes from a completed scan.</summary>
    public void Set(byte[] template, byte[] image)
    {
        lock (_lock)
        {
            _template = template;
            _image    = image;
        }
    }

    /// <summary>Returns a copy of the stored template and image (both may be null if not yet captured).</summary>
    public (byte[]? Template, byte[]? Image) Get()
    {
        lock (_lock)
        {
            return (_template, _image);
        }
    }

    /// <summary>Returns true if a fingerprint has been captured and is waiting to be consumed.</summary>
    public bool HasData()
    {
        lock (_lock)
        {
            return _template != null;
        }
    }
}
