using System.Net.Sockets;

namespace HelixGodotProxy.Utils;

/// <summary>
/// Manages the TCP connection to the Godot LSP server
/// </summary>
public class GodotLspConnection : IDisposable
{
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private bool _disposed = false;

    public Stream? InputStream => _networkStream;
    public Stream? OutputStream => _networkStream;
    public bool IsConnected => _tcpClient?.Connected == true;

    /// <summary>
    /// Connects to the Godot LSP server via TCP
    /// </summary>
    /// <param name="port">The port to connect to (default 6005)</param>
    /// <param name="host">The host to connect to (default localhost)</param>
    public async Task<bool> ConnectAsync(int port = 6005, string host = "localhost")
    {
        try
        {
            _tcpClient = new TcpClient
            {
                NoDelay = true, // reduce latency for small messages
                ReceiveBufferSize = 128 * 1024,
                SendBufferSize = 128 * 1024
            };
            await _tcpClient.ConnectAsync(host, port);
            _networkStream = _tcpClient.GetStream();
            // Use infinite timeouts for async operations; we rely on CancellationTokens for control
            _networkStream.ReadTimeout = System.Threading.Timeout.Infinite;
            _networkStream.WriteTimeout = System.Threading.Timeout.Infinite;

            await Logger.LogAsync(LogLevel.INFO, $"Connected to Godot LSP server at {host}:{port}");
            return true;
        }
        catch (Exception ex)
        {
            await Logger.LogAsync(LogLevel.ERROR, $"Failed to connect to Godot LSP server at {host}:{port}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Disconnects from the Godot LSP server
    /// </summary>
    public async Task DisconnectAsync()
    {
        try
        {
            _networkStream?.Close();
            _tcpClient?.Close();
            await Logger.LogAsync(LogLevel.INFO, "Disconnected from Godot LSP server");
        }
        catch (Exception ex)
        {
            await Logger.LogAsync(LogLevel.WARNING, $"Error during disconnect: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            DisconnectAsync().Wait();
            _networkStream?.Dispose();
            _tcpClient?.Dispose();
            _disposed = true;
        }
    }
}