using System.Text.Json;
using System.Text.Json.Nodes;
using HelixGodotProxy.Utils;

namespace HelixGodotProxy.Enhancements;

/// <summary>
/// Enhancement that converts function-like completions to snippets for better cursor placement.
/// </summary>
public class FunctionCompletionEnhancement : ILspEnhancement
{
    public string Name => "Function Completion Enhancement";
    public int Priority => 100;

    public bool ShouldProcess(LspMessage message)
        => message.Direction == MessageDirection.ServerToClient;

    public async Task<LspMessage> ProcessAsync(LspMessage message)
    {
        try
        {
            var jsonContent = LspMessageParser.ExtractJsonContent(message.Content);
            if (string.IsNullOrEmpty(jsonContent)) return message;

            using var jsonDocument = JsonDocument.Parse(jsonContent);
            var root = jsonDocument.RootElement;
            if (!IsCompletionResponse(root)) return message;

            var modifiedJson = ProcessCompletionItems(root);
            if (modifiedJson != null)
            {
                var newLspMessage = LspMessageParser.FormatMessage(modifiedJson);
                return new LspMessage
                {
                    Content = newLspMessage,
                    Direction = message.Direction,
                    Timestamp = message.Timestamp,
                    IsModified = true
                };
            }
        }
        catch (Exception ex)
        {
            await Logger.LogAsync(LogLevel.ERROR, $"Error processing function completions: {ex.Message}");
        }

        return message;
    }

    private static bool IsCompletionResponse(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result)) return false;
        if (result.ValueKind == JsonValueKind.Array) return true;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out _)) return true;
        return false;
    }

    private static string? ProcessCompletionItems(JsonElement root)
    {
        var rootNode = JsonNode.Parse(root.GetRawText());
        if (rootNode == null) return null;

        bool modified = false;
        JsonArray? items = null;
        var result = rootNode["result"];
        if (result is JsonArray directArray)
        {
            items = directArray;
        }
        else if (result is JsonObject resultObj && resultObj["items"] is JsonArray itemsArray)
        {
            items = itemsArray;
        }

        if (items == null) return null;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not JsonObject item) continue;
            if (ProcessCompletionItem(item)) modified = true;
        }

        return modified ? rootNode.ToJsonString() : null;
    }

    private static bool ProcessCompletionItem(JsonObject item)
    {
        if (!IsFunctionCompletion(item)) return false;

        string insertText = GetInsertText(item);
        string label = item.TryGetPropertyValue("label", out var labelNode) && labelNode != null ? labelNode.GetValue<string>() ?? string.Empty : string.Empty;

        var originalFormat = item.TryGetPropertyValue("insertTextFormat", out var formatNode) ? formatNode?.GetValue<int>() : null;
        var hasTextEdit = item.TryGetPropertyValue("textEdit", out var textEditNode) && textEditNode != null;
        Logger.LogDebug($"Completion item: label='{label}', insertText='{insertText}', origFmt={originalFormat}, hasEdit={hasTextEdit}");

        if (!label.Contains("(…)"))
        {
            Logger.LogDebug($"Skip '{label}' - no parameter marker (…)");
            return false;
        }

        if (insertText.EndsWith("()"))
        {
            string snippetText = insertText.Substring(0, insertText.Length - 2) + "($1)";
            item["insertText"] = JsonValue.Create(snippetText);
            if (item.TryGetPropertyValue("textEdit", out var editNode) && editNode is JsonObject textEdit)
                textEdit["newText"] = JsonValue.Create(snippetText);
            item["insertTextFormat"] = JsonValue.Create(2); // Snippet
            item["kind"] = JsonValue.Create(15); // Snippet icon
            Logger.LogDebug($"Converted '{insertText}' -> '{snippetText}'");
            return true;
        }
        else if (insertText.EndsWith("(") && !insertText.EndsWith("(("))
        {
            string snippetText = insertText + "$1)";
            item["insertText"] = JsonValue.Create(snippetText);
            if (item.TryGetPropertyValue("textEdit", out var editNode2) && editNode2 is JsonObject textEdit2)
                textEdit2["newText"] = JsonValue.Create(snippetText);
            item["insertTextFormat"] = JsonValue.Create(2);
            item["kind"] = JsonValue.Create(15);
            Logger.LogDebug($"Converted '{insertText}' -> '{snippetText}'");
            return true;
        }

        return false;
    }

    private static bool IsFunctionCompletion(JsonObject item)
    {
        if (item.TryGetPropertyValue("kind", out var kindNode))
        {
            var kind = kindNode?.GetValue<int>();
            if (kind is 6 or 2 or 3) return true; // Variable/Method/Function
        }
        if (item.TryGetPropertyValue("detail", out var detailNode))
        {
            var detail = detailNode?.GetValue<string>();
            if (detail?.Contains("(") == true) return true;
        }
        return false;
    }

    private static string GetInsertText(JsonObject item)
    {
        if (item.TryGetPropertyValue("insertText", out var insertTextNode))
            return insertTextNode?.GetValue<string>() ?? string.Empty;
        if (item.TryGetPropertyValue("label", out var labelNode))
            return labelNode?.GetValue<string>() ?? string.Empty;
        return string.Empty;
    }
}
