namespace HelixGodotProxy.Enhancements;

/// <summary>
/// Represents the direction of LSP communication
/// </summary>
public enum MessageDirection
{
    ClientToServer, // Helix to Godot
    ServerToClient  // Godot to Helix
}

/// <summary>
/// Contains the LSP message data and metadata
/// </summary>
public class LspMessage
{
    public string Content { get; set; } = string.Empty;
    public MessageDirection Direction { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool IsModified { get; set; } = false;
}

/// <summary>
/// Base interface for all LSP message enhancements
/// </summary>
public interface ILspEnhancement
{
    /// <summary>
    /// Process the LSP message and optionally modify it
    /// </summary>
    /// <param name="message">The LSP message to process</param>
    /// <returns>The processed message (may be the same instance if not modified)</returns>
    Task<LspMessage> ProcessAsync(LspMessage message);

    /// <summary>
    /// Determines if this enhancement should process the given message
    /// </summary>
    /// <param name="message">The message to check</param>
    /// <returns>True if this enhancement should process the message</returns>
    bool ShouldProcess(LspMessage message);

    /// <summary>
    /// Gets the priority of this enhancement (higher number = higher priority)
    /// </summary>
    int Priority { get; }
}
