using System.Text;
using System.Collections.Concurrent;

namespace HelixGodotProxy;

public enum LogLevel
{
    DEBUG,
    INFO,
    WARNING,
    ERROR
}

public enum VerbosityLevel
{
    None = 0,      // No logging
    Normal = 1,    // INFO and ERROR
    Verbose = 2,   // INFO, ERROR, and DEBUG
    VeryVerbose = 3 // INFO, ERROR, DEBUG, and WARNING
}

public static class Logger
{
    private static string? _logFilePath;
    private static VerbosityLevel _verbosity = VerbosityLevel.None;
    private static readonly object _initLock = new();
    private static readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private static CancellationTokenSource? _cts;
    private static Task? _writerTask;
    private static volatile bool _initialized;

    public static void Initialize(string godotProjectPath, VerbosityLevel verbosity = VerbosityLevel.None)
    {
        lock (_initLock)
        {
            _verbosity = verbosity;
            if (verbosity == VerbosityLevel.None)
            {
                _initialized = true;
                return;
            }

            if (_initialized) return;

            _logFilePath = Path.Combine(godotProjectPath, "HelixGodotProxy.log");

            var directory = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _cts = new CancellationTokenSource();
            _writerTask = Task.Run(() => WriterLoop(_cts.Token));
            _initialized = true;
        }
        LogAsync(LogLevel.INFO, "LSP Proxy Logger initialized").Wait();
    }

    public static Task LogAsync(LogLevel level, string message)
    {
        if (_verbosity == VerbosityLevel.None || string.IsNullOrEmpty(_logFilePath))
            return Task.CompletedTask;

        // Filter based on verbosity level
        bool shouldLog = level switch
        {
            LogLevel.ERROR => _verbosity >= VerbosityLevel.Normal,
            LogLevel.INFO => _verbosity >= VerbosityLevel.Normal,
            LogLevel.DEBUG => _verbosity >= VerbosityLevel.Verbose,
            LogLevel.WARNING => _verbosity >= VerbosityLevel.VeryVerbose,
            _ => false
        };

        if (!shouldLog)
            return Task.CompletedTask;

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var logEntry = $"{timestamp} [{level}] {message}{Environment.NewLine}";

        try { _queue.TryAdd(logEntry); } catch { }
        return Task.CompletedTask;
    }

    public static void Log(LogLevel level, string message)
    {
        _ = LogAsync(level, message);
    }

    public static void LogDebug(string message)
    {
        _ = LogAsync(LogLevel.DEBUG, message);
    }

    private static async Task WriterLoop(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_logFilePath)) return;

        // Open append stream with buffering
        using var fs = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sw = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = false };

        try
        {
            var batchCount = 0;
            while (!ct.IsCancellationRequested)
            {
                string? entry;
                try
                {
                    entry = _queue.Take(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await sw.WriteAsync(entry);
                batchCount++;

                // Drain quickly available items without context switches
                while (_queue.TryTake(out var more))
                {
                    await sw.WriteAsync(more);
                    batchCount++;
                    if (batchCount >= 256) break;
                }

                if (batchCount >= 16)
                {
                    await sw.FlushAsync();
                    batchCount = 0;
                }
            }

            // Flush remaining
            while (_queue.TryTake(out var tail))
            {
                await sw.WriteAsync(tail);
            }
            await sw.FlushAsync();
        }
        catch
        {
            // Swallow logging errors
        }
    }

    public static async Task ShutdownAsync()
    {
        if (!_initialized) return;
        try
        {
            _cts?.Cancel();
            _queue.CompleteAdding();
            if (_writerTask != null)
            {
                try { await _writerTask; } catch { /* ignore */ }
            }
        }
        finally
        {
            _initialized = false;
        }
    }
}