namespace HelixGodotProxy.Enhancements;

/// <summary>
/// Manages and orchestrates all LSP message enhancements
/// </summary>
public class EnhancementPipeline
{
    private readonly List<ILspEnhancement> _enhancements;

    public EnhancementPipeline()
    {
        _enhancements = new List<ILspEnhancement>();
    }

    /// <summary>
    /// Registers an enhancement with the pipeline
    /// </summary>
    /// <param name="enhancement">The enhancement to register</param>
    public void RegisterEnhancement(ILspEnhancement enhancement)
    {
        _enhancements.Add(enhancement);
        _enhancements.Sort((a, b) => b.Priority.CompareTo(a.Priority)); // Sort by priority descending
    }

    /// <summary>
    /// Processes a message through all applicable enhancements
    /// </summary>
    /// <param name="message">The message to process</param>
    /// <returns>The processed message</returns>
    public async Task<LspMessage> ProcessAsync(LspMessage message)
    {
        var currentMessage = message;

        foreach (var enhancement in _enhancements)
        {
            if (enhancement.ShouldProcess(currentMessage))
            {
                try
                {
                    currentMessage = await enhancement.ProcessAsync(currentMessage);
                }
                catch (Exception ex)
                {
                    await Logger.LogAsync(LogLevel.ERROR,
                        $"Enhancement {enhancement.GetType().Name} failed: {ex.Message}");
                    // Continue with other enhancements even if one fails
                }
            }
        }

        return currentMessage;
    }

    /// <summary>
    /// Gets the count of registered enhancements
    /// </summary>
    public int EnhancementCount => _enhancements.Count;
}
