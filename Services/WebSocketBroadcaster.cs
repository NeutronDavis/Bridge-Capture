using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BridgeCapture.Models;

namespace BridgeCapture.Services;

/// <summary>
/// Singleton that manages all connected WebSocket clients and broadcasts
/// fingerprint payloads to every open connection.
/// </summary>
public class WebSocketBroadcaster
{
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private readonly ILogger<WebSocketBroadcaster> _logger;

    public WebSocketBroadcaster(ILogger<WebSocketBroadcaster> logger)
    {
        _logger = logger;
    }

    // ── Client registration ──────────────────────────────────────────────────

    public string AddClient(WebSocket socket)
    {
        var id = Guid.NewGuid().ToString();
        _clients[id] = socket;
        _logger.LogInformation("WebSocket client connected: {Id}  (total: {Count})", id, _clients.Count);
        return id;
    }

    public void RemoveClient(string id)
    {
        _clients.TryRemove(id, out _);
        _logger.LogInformation("WebSocket client disconnected: {Id}  (total: {Count})", id, _clients.Count);
    }

    // ── Broadcast ────────────────────────────────────────────────────────────

    /// <summary>
    /// Serialises <paramref name="payload"/> as JSON and sends it to every connected client.
    /// Stale / closed connections are removed automatically.
    /// </summary>
    public async Task BroadcastAsync(FingerprintPayload payload, CancellationToken ct)
    {
        var json    = JsonSerializer.Serialize(payload);
        var bytes   = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        var dead = new List<string>();

        foreach (var (id, socket) in _clients)
        {
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.SendAsync(segment, WebSocketMessageType.Text, endOfMessage: true, ct);
                    _logger.LogInformation("Fingerprint payload sent → client {Id}", id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Send failed for client {Id} — marking for removal", id);
                    dead.Add(id);
                }
            }
            else
            {
                dead.Add(id);
            }
        }

        foreach (var id in dead)
            RemoveClient(id);
    }

    // ── Keep-alive loop ──────────────────────────────────────────────────────

    /// <summary>
    /// Blocks the request pipeline until the browser closes the connection.
    /// This keeps the WebSocket endpoint handler alive without polling.
    /// </summary>
    public async Task ListenUntilClosedAsync(string clientId, WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket error for client {Id}", clientId);
        }
        finally
        {
            RemoveClient(clientId);
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Connection closed",
                    CancellationToken.None);
            }
        }
    }
}
