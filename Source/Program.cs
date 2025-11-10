using HelixGodotProxy.Enhancements;

namespace HelixGodotProxy;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            // Parse logging verbosity from arguments
            VerbosityLevel verbosity = VerbosityLevel.None;
            if (args.Any(a => a.ToLowerInvariant() == "logvv"))
            {
                verbosity = VerbosityLevel.VeryVerbose;
            }
            else if (args.Any(a => a.ToLowerInvariant() == "logv"))
            {
                verbosity = VerbosityLevel.Verbose;
            }
            else if (args.Any(a => a.ToLowerInvariant() == "log"))
            {
                verbosity = VerbosityLevel.Normal;
            }

            // Parse host and port
            string host = "localhost";
            int port = 6005;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i].ToLowerInvariant();
                if ((arg == "--host" || arg == "-h") && i + 1 < args.Length)
                {
                    host = args[i + 1];
                }
                else if ((arg == "--port" || arg == "-p") && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out int parsedPort))
                    {
                        port = parsedPort;
                    }
                    else
                    {
                        Console.Error.WriteLine($"Warning: Invalid port value '{args[i + 1]}'. Using default port {port}.");
                    }
                }
            }

            using var proxy = new LspProxy();

            // Register enhancements
            proxy.RegisterEnhancement(new FunctionCompletionEnhancement());
            proxy.RegisterEnhancement(new DocumentationEnhancement());

            // Handle Ctrl+C gracefully
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                proxy.StopAsync().Wait();
            };

            var success = await proxy.StartAsync(verbosity, port, host);

            if (!success)
            {
                return 1;
            }

            // Keep the proxy running
            await Task.Delay(Timeout.Infinite);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            return 1;
        }
    }
}