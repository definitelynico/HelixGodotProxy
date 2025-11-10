using HelixGodotProxy.Enhancements;
using HelixGodotProxy.Utils;

namespace HelixGodotProxy;

/// <summary>
/// Main LSP proxy that forwards messages between Helix and Godot with optional enhancements
/// </summary>
public class LspProxy : IDisposable
{
    private readonly GodotLspConnection _godotConnection;
    private readonly EnhancementPipeline _enhancementPipeline;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private bool _disposed = false;
    private Task? _proxyTask;

    public LspProxy()
    {
        _godotConnection = new GodotLspConnection();
        _enhancementPipeline = new EnhancementPipeline();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    /// <summary>
    /// Starts the LSP proxy
    /// </summary>
    /// <param name="verbosity">Logging verbosity level</param>
    /// <param name="port">The port to connect to Godot LSP (default 6005)</param>
    /// <param name="host">The host to connect to (default localhost)</param>
    public async Task<bool> StartAsync(VerbosityLevel verbosity = VerbosityLevel.None, int port = 6005, string host = "localhost")
    {
        // Initialize logging (log to current directory)
        Logger.Initialize(Directory.GetCurrentDirectory(), verbosity);

        await Logger.LogAsync(LogLevel.INFO, "Starting LSP Proxy");

        // Connect to Godot LSP via TCP
        if (!await _godotConnection.ConnectAsync(port, host))
        {
            await Logger.LogAsync(LogLevel.ERROR, "Failed to connect to Godot LSP server");
            return false;
        }

        // Start the proxy loop
        _proxyTask = Task.Run(() => ProxyLoop(_cancellationTokenSource.Token));

        await Logger.LogAsync(LogLevel.INFO, "LSP Proxy started successfully");
        return true;
    }    /// <summary>
         /// Registers an enhancement with the proxy
         /// </summary>
    public void RegisterEnhancement(ILspEnhancement enhancement)
    {
        _enhancementPipeline.RegisterEnhancement(enhancement);
        Logger.Log(LogLevel.INFO, $"Registered enhancement: {enhancement.GetType().Name}");
    }

    /// <summary>
    /// Main proxy loop that handles bidirectional communication
    /// </summary>
    private async Task ProxyLoop(CancellationToken cancellationToken)
    {
        var helixToGodotTask = Task.Run(() => ForwardMessages(
            Console.OpenStandardInput(),
            _godotConnection.OutputStream!,
            MessageDirection.ClientToServer,
            cancellationToken), cancellationToken);

        var godotToHelixTask = Task.Run(() => ForwardMessages(
            _godotConnection.InputStream!,
            Console.OpenStandardOutput(),
            MessageDirection.ServerToClient,
            cancellationToken), cancellationToken);

        try
        {
            await Task.WhenAny(helixToGodotTask, godotToHelixTask);
        }
        catch (OperationCanceledException)
        {
            // Expected when shutting down
        }
        catch (Exception ex)
        {
            await Logger.LogAsync(LogLevel.ERROR, $"Proxy loop error: {ex.Message}");
        }

        await Logger.LogAsync(LogLevel.INFO, "Proxy loop ended");
    }

    /// <summary>
    /// Forwards messages from input to output stream with optional enhancements
    /// </summary>
    private async Task ForwardMessages(Stream inputStream, Stream outputStream,
        MessageDirection direction, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _godotConnection.IsConnected)
            {
                var rawMessage = await LspMessageParser.ReadMessageAsync(inputStream);
                if (rawMessage == null) break;

                // Create LSP message object
                var lspMessage = new LspMessage
                {
                    Content = rawMessage,
                    Direction = direction,
                    Timestamp = DateTime.UtcNow
                };

                // Process through enhancement pipeline
                var processedMessage = await _enhancementPipeline.ProcessAsync(lspMessage);

                // Forward the (potentially modified) message
                await LspMessageParser.WriteMessageAsync(outputStream, processedMessage.Content);

                // Log if message was modified
                if (processedMessage.IsModified)
                {
                    await Logger.LogAsync(LogLevel.INFO,
                        $"Message modified by enhancements ({direction})");
                }
            }
        }
        catch (Exception ex)
        {
            await Logger.LogAsync(LogLevel.ERROR,
                $"Error forwarding messages ({direction}): {ex.Message}");
        }
    }

    /// <summary>
    /// Stops the proxy
    /// </summary>
    public async Task StopAsync()
    {
        await Logger.LogAsync(LogLevel.INFO, "Stopping LSP Proxy");

        _cancellationTokenSource.Cancel();

        if (_proxyTask != null)
        {
            try
            {
                await _proxyTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        await _godotConnection.DisconnectAsync();
        await Logger.ShutdownAsync();
        await Logger.LogAsync(LogLevel.INFO, "LSP Proxy stopped");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            StopAsync().Wait();
            _godotConnection.Dispose();
            _cancellationTokenSource.Dispose();
            _disposed = true;
        }
    }
}
