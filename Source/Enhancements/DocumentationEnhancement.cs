using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Linq;
using HelixGodotProxy.Utils;

namespace HelixGodotProxy.Enhancements;

public class DocumentationEnhancement : ILspEnhancement
{
    public string Name => "Documentation Enhancement";
    public int Priority => 200;

    // Precompiled regexes
    private static readonly Regex CodeblocksWrap = new Regex(@"`codeblocks`\s*(.*?)\s*`/codeblocks`", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CodeblockLang = new Regex(@"`codeblock\s+lang=([^`]+)`\s*(.*?)\s*`/codeblock`", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CodeblockNoLang = new Regex(@"`codeblock`\s*(.*?)\s*`/codeblock`", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GdscriptLang = new Regex(@"`gdscript\s+([^`]*)`\s*(.*?)\s*`/gdscript`", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GdscriptNoLang = new Regex(@"`gdscript`\s*(.*?)\s*`/gdscript`", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CsharpBlock = new Regex(@"`csharp(?:[^`]*)`.*?`/csharp`", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InlineBackticks = new Regex(@"(?<!`)`([^`]+)`(?!`)", RegexOptions.Compiled);

    private static bool ContainsBBCode(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("`codeblocks`") || text.Contains("`/codeblocks`") ||
               text.Contains("`codeblock") || text.Contains("`/codeblock`") ||
               text.Contains("`gdscript") || text.Contains("`/gdscript`") ||
               text.Contains("`csharp") || text.Contains("`/csharp`") ||
               text.Contains("`code`") || text.Contains("`/code`") ||
               text.Contains("`b`") || text.Contains("`/b`") ||
               text.Contains("`i`") || text.Contains("`/i`") ||
               text.Contains("`u`") || text.Contains("`/u`");
    }

    public bool ShouldProcess(LspMessage message) => message.Direction == MessageDirection.ServerToClient;

    public async Task<LspMessage> ProcessAsync(LspMessage message)
    {
        try
        {
            var jsonContent = LspMessageParser.ExtractJsonContent(message.Content);
            if (string.IsNullOrEmpty(jsonContent)) return message;
            using var jsonDocument = JsonDocument.Parse(jsonContent);
            var root = jsonDocument.RootElement;
            if (!ContainsDocumentation(root)) return message;

            var modifiedJson = await ProcessDocumentation(root);
            if (modifiedJson is not null)
            {
                return new LspMessage
                {
                    Content = LspMessageParser.FormatMessage(modifiedJson),
                    Direction = message.Direction,
                    Timestamp = message.Timestamp,
                    IsModified = true
                };
            }
        }
        catch (Exception ex)
        {
            await Logger.LogAsync(LogLevel.ERROR, $"DocumentationEnhancement error: {ex.Message}");
        }
        return message;
    }

    private bool ContainsDocumentation(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result))
        {
            _ = Logger.LogAsync(LogLevel.DEBUG, "ContainsDocumentation: no 'result' property");
            return false;
        }

        if (result.ValueKind == JsonValueKind.Array)
        {
            _ = Logger.LogAsync(LogLevel.DEBUG, "ContainsDocumentation: result is Array - TRUE");
            return true;
        }

        if (result.ValueKind != JsonValueKind.Object)
        {
            _ = Logger.LogAsync(LogLevel.DEBUG, $"ContainsDocumentation: result is not Object, it's {result.ValueKind} - FALSE");
            return false;
        }

        if (result.TryGetProperty("items", out _))
        {
            _ = Logger.LogAsync(LogLevel.DEBUG, "ContainsDocumentation: found 'items' - TRUE (CompletionList)");
            return true; // CompletionList
        }

        if (result.TryGetProperty("signatures", out _))
        {
            _ = Logger.LogAsync(LogLevel.DEBUG, "ContainsDocumentation: found 'signatures' - TRUE (SignatureHelp)");
            return true; // SignatureHelp
        }

        if (result.TryGetProperty("contents", out _))
        {
            _ = Logger.LogAsync(LogLevel.DEBUG, "ContainsDocumentation: found 'contents' - TRUE (Hover)");
            return true; // Hover
        }

        if (result.TryGetProperty("label", out _) &&
            (result.TryGetProperty("kind", out _) ||
             result.TryGetProperty("documentation", out _) ||
             result.TryGetProperty("detail", out _)))
        {
            _ = Logger.LogAsync(LogLevel.DEBUG, "ContainsDocumentation: found 'label' with kind/doc/detail - TRUE (single CompletionItem)");
            return true; // single CompletionItem
        }

        _ = Logger.LogAsync(LogLevel.DEBUG, "ContainsDocumentation: no documentation fields found - FALSE");
        return false;
    }

    private async Task<string?> ProcessDocumentation(JsonElement root)
    {
        var rootNode = JsonNode.Parse(root.GetRawText());
        if (rootNode is null) return null;

        bool modified = false;
        var resultNode = rootNode["result"];
        if (resultNode is null) return null;

        if (resultNode is JsonArray directArray)
        {
            for (int i = 0; i < directArray.Count; i++)
            {
                if (directArray[i] is JsonObject item && await ProcessCompletionItemDocumentation(item))
                    modified = true;
            }
        }
        else if (resultNode is JsonObject resultObj)
        {
            if (resultObj["items"] is JsonArray itemsArray)
            {
                for (int i = 0; i < itemsArray.Count; i++)
                {
                    if (itemsArray[i] is JsonObject item && await ProcessCompletionItemDocumentation(item))
                        modified = true;
                }
            }
            else if (resultObj.TryGetPropertyValue("signatures", out var _) && await ProcessSignatureHelp(resultObj))
            {
                modified = true;
            }
            else if (resultObj.TryGetPropertyValue("contents", out var _) && await ProcessHoverContents(resultObj))
            {
                modified = true;
            }
            else if (resultObj.TryGetPropertyValue("label", out _) &&
                     (resultObj.TryGetPropertyValue("kind", out _) ||
                      resultObj.TryGetPropertyValue("documentation", out _) ||
                      resultObj.TryGetPropertyValue("detail", out _)))
            {
                if (await ProcessCompletionItemDocumentation(resultObj))
                    modified = true;
            }
        }

        return modified ? rootNode.ToJsonString() : null;
    }

    private async Task<bool> ProcessSignatureHelp(JsonObject resultObj)
    {
        bool modified = false;
        if (resultObj["signatures"] is not JsonArray sigs) return false;

        for (int i = 0; i < sigs.Count; i++)
        {
            if (sigs[i] is not JsonObject sig) continue;

            // Get the signature label if available
            var sigLabel = sig.TryGetPropertyValue("label", out var labelNode) && labelNode is JsonValue lv
                ? lv.GetValue<string>() : $"sig[{i}]";

            if (sig.TryGetPropertyValue("documentation", out var docNode))
            {
                if (docNode is JsonValue dv)
                {
                    var s = dv.GetValue<string>();
                    if (!string.IsNullOrEmpty(s))
                    {
                        await Logger.LogAsync(LogLevel.DEBUG, $"[SIG] {sigLabel} - original doc (string): '{s.Replace("\n", "\\n").Substring(0, Math.Min(150, s.Length))}'");

                        // Extract and remove signature if present at the start
                        var (extractedSig, remainingDoc) = ExtractSignatureFromDoc(s);
                        if (extractedSig != null)
                        {
                            s = remainingDoc;
                            await Logger.LogAsync(LogLevel.DEBUG, $"[SIG] {sigLabel} - removed embedded signature: '{extractedSig}'");
                        }

                        var updated = NormalizeDoc(s);

                        sig["documentation"] = new JsonObject { ["kind"] = JsonValue.Create("markdown"), ["value"] = JsonValue.Create(updated) };
                        modified = true;
                    }
                }
                else if (docNode is JsonObject dObj && dObj.TryGetPropertyValue("value", out var v) && v is JsonValue vv)
                {
                    var s = vv.GetValue<string>();
                    if (!string.IsNullOrEmpty(s))
                    {
                        await Logger.LogAsync(LogLevel.DEBUG, $"[SIG] {sigLabel} - original doc (object.value): '{s.Replace("\n", "\\n").Substring(0, Math.Min(150, s.Length))}'");

                        // Extract and remove signature if present at the start
                        var (extractedSig, remainingDoc) = ExtractSignatureFromDoc(s);
                        if (extractedSig != null)
                        {
                            s = remainingDoc;
                            await Logger.LogAsync(LogLevel.DEBUG, $"[SIG] {sigLabel} - removed embedded signature: '{extractedSig}'");
                        }

                        var updated = NormalizeDoc(s);

                        dObj["value"] = JsonValue.Create(updated);
                        dObj["kind"] = JsonValue.Create("markdown");
                        modified = true;
                    }
                }
            }

            if (sig["parameters"] is JsonArray @params)
            {
                for (int p = 0; p < @params.Count; p++)
                {
                    if (@params[p] is not JsonObject param) continue;
                    if (param.TryGetPropertyValue("documentation", out var pDoc))
                    {
                        if (pDoc is JsonValue pv)
                        {
                            var s = pv.GetValue<string>();
                            if (!string.IsNullOrEmpty(s))
                            {
                                var updated = NormalizeDoc(s);
                                param["documentation"] = new JsonObject { ["kind"] = JsonValue.Create("markdown"), ["value"] = JsonValue.Create(updated) };
                                modified = true;
                            }
                        }
                        else if (pDoc is JsonObject pObj && pObj.TryGetPropertyValue("value", out var pvNode) && pvNode is JsonValue pvv)
                        {
                            var s = pvv.GetValue<string>();
                            if (!string.IsNullOrEmpty(s))
                            {
                                var updated = NormalizeDoc(s);
                                pObj["value"] = JsonValue.Create(updated);
                                pObj["kind"] = JsonValue.Create("markdown");
                                modified = true;
                            }
                        }
                    }
                }
            }
        }

        return modified;
    }

    private async Task<bool> ProcessCompletionItemDocumentation(JsonObject item)
    {
        bool modified = false;

        // Get label for logging
        var label = item.TryGetPropertyValue("label", out var labelNode) && labelNode is JsonValue lv
            ? lv.GetValue<string>() : "unknown";

        await Logger.LogAsync(LogLevel.DEBUG, $"[DOC] Processing completion item: {label}");

        // Process documentation field
        if (item.TryGetPropertyValue("documentation", out var docNode))
        {
            if (docNode is JsonValue docValue)
            {
                var s = docValue.GetValue<string>();
                if (!string.IsNullOrEmpty(s))
                {
                    await Logger.LogAsync(LogLevel.DEBUG, $"[{label}] Original doc (string): '{s.Replace("\n", "\\n").Substring(0, Math.Min(200, s.Length))}'");

                    // Extract signature from documentation if present
                    var (extractedSig, remainingDoc) = ExtractSignatureFromDoc(s);
                    if (extractedSig != null)
                    {
                        // Use extracted signature as detail (overrides whatever was there)
                        item["detail"] = JsonValue.Create(extractedSig);
                        s = remainingDoc;
                        modified = true;
                        await Logger.LogAsync(LogLevel.DEBUG, $"[{label}] Set detail to: '{extractedSig}'");
                    }

                    var updated = NormalizeDoc(s);
                    await Logger.LogAsync(LogLevel.DEBUG, $"[{label}] Final doc: '{updated.Replace("\n", "\\n").Substring(0, Math.Min(200, updated.Length))}'");
                    item["documentation"] = new JsonObject { ["kind"] = JsonValue.Create("markdown"), ["value"] = JsonValue.Create(updated) };
                    modified = true;
                }
            }
            else if (docNode is JsonObject docObj && docObj.TryGetPropertyValue("value", out var v) && v is JsonValue vv)
            {
                var s = vv.GetValue<string>();
                if (!string.IsNullOrEmpty(s))
                {
                    await Logger.LogAsync(LogLevel.DEBUG, $"[{label}] Original doc (object.value): '{s.Replace("\n", "\\n").Substring(0, Math.Min(200, s.Length))}'");

                    // Extract signature from documentation if present
                    var (extractedSig, remainingDoc) = ExtractSignatureFromDoc(s);
                    if (extractedSig != null)
                    {
                        // Use extracted signature as detail (overrides partial detail like "class")
                        item["detail"] = JsonValue.Create(extractedSig);
                        s = remainingDoc;
                        await Logger.LogAsync(LogLevel.DEBUG, $"[{label}] Extracted signature: '{extractedSig}'");
                    }

                    var updated = NormalizeDoc(s);
                    await Logger.LogAsync(LogLevel.DEBUG, $"[{label}] After NormalizeDoc: '{updated.Replace("\n", "\\n").Substring(0, Math.Min(200, updated.Length))}'");
                    docObj["value"] = JsonValue.Create(updated);
                    docObj["kind"] = JsonValue.Create("markdown");
                    modified = true;
                }
            }
        }
        return modified;
    }

    private async Task<bool> ProcessHoverContents(JsonObject hoverResult)
    {
        bool modified = false;
        if (!hoverResult.TryGetPropertyValue("contents", out var contentsNode)) return false;

        if (contentsNode is JsonValue cv)
        {
            var s = cv.GetValue<string>();
            if (!string.IsNullOrEmpty(s))
            {
                await Logger.LogAsync(LogLevel.DEBUG, $"[HOVER] Processing string contents: '{s.Replace("\n", "\\n").Substring(0, Math.Min(150, s.Length))}'");

                // Extract and remove signature if present
                var (extractedSig, remainingDoc) = ExtractSignatureFromDoc(s);
                if (extractedSig != null)
                {
                    s = remainingDoc;
                    await Logger.LogAsync(LogLevel.DEBUG, $"[HOVER] Extracted signature: '{extractedSig}'");
                    // Prepend syntax-highlighted signature
                    s = $"```gdscript\n{extractedSig}\n```\n\n{s}";
                }

                var updated = NormalizeDoc(s);
                hoverResult["contents"] = new JsonObject { ["kind"] = JsonValue.Create("markdown"), ["value"] = JsonValue.Create(updated) };
                modified = true;
            }
        }
        else if (contentsNode is JsonObject co && co.TryGetPropertyValue("value", out var v) && v is JsonValue vv)
        {
            var s = vv.GetValue<string>();
            if (!string.IsNullOrEmpty(s))
            {
                await Logger.LogAsync(LogLevel.DEBUG, $"[HOVER] Processing object.value contents: '{s.Replace("\n", "\\n").Substring(0, Math.Min(150, s.Length))}'");

                // Extract and remove signature if present
                var (extractedSig, remainingDoc) = ExtractSignatureFromDoc(s);
                if (extractedSig != null)
                {
                    s = remainingDoc;
                    await Logger.LogAsync(LogLevel.DEBUG, $"[HOVER] Extracted signature: '{extractedSig}'");
                    // Prepend syntax-highlighted signature
                    s = $"```gdscript\n{extractedSig}\n```\n\n{s}";
                }

                var updated = NormalizeDoc(s);
                co["value"] = JsonValue.Create(updated);
                co["kind"] = JsonValue.Create("markdown");
                modified = true;
            }
        }
        else if (contentsNode is JsonArray ca)
        {
            var parts = new List<string>(ca.Count);
            foreach (var node in ca)
            {
                switch (node)
                {
                    case JsonValue jv:
                        {
                            var s = jv.GetValue<string>();
                            if (!string.IsNullOrEmpty(s)) parts.Add(NormalizeDoc(s));
                            break;
                        }
                    case JsonObject jo:
                        {
                            string? val = null;
                            if (jo.TryGetPropertyValue("value", out var vn) && vn is JsonValue vvv) val = vvv.GetValue<string>();
                            if (!string.IsNullOrEmpty(val))
                            {
                                if (jo.TryGetPropertyValue("language", out var ln) && ln is JsonValue lvv)
                                {
                                    var lang = lvv.GetValue<string>()?.Trim() ?? string.Empty;
                                    parts.Add(CleanAndFenceCode(val!, string.IsNullOrEmpty(lang) ? string.Empty : lang));
                                }
                                else
                                {
                                    parts.Add(NormalizeDoc(val!));
                                }
                            }
                            break;
                        }
                }
            }
            var combined = string.Join("\n\n", parts);
            combined = CollapseBlanksOutsideFences(combined);
            combined = SanitizeAllFences(combined);
            combined = combined.Trim();
            hoverResult["contents"] = new JsonObject { ["kind"] = JsonValue.Create("markdown"), ["value"] = JsonValue.Create(combined) };
            modified = true;
        }
        return modified;
    }

    private static bool IsSignature(string detail)
    {
        // Remove common prefixes that Godot adds
        var cleaned = detail;
        if (cleaned.StartsWith("<Native> "))
            cleaned = cleaned.Substring(9);

        // Check for function signatures
        if (cleaned.StartsWith("func ") ||
            cleaned.StartsWith("static func ") ||
            (cleaned.Contains("(") && cleaned.Contains(")") && cleaned.Contains("->")) ||
            (cleaned.StartsWith("@") && cleaned.Contains("func ")))
        {
            return true;
        }

        // Check for constant signatures
        if (cleaned.StartsWith("const ") ||
            cleaned.StartsWith("static const ") ||
            (cleaned.StartsWith("@") && cleaned.Contains(":")) ||
            (cleaned.Contains(":") && cleaned.Contains("=") && !cleaned.StartsWith("func")))
        {
            return true;
        }

        // Check for class, enum, and other type definitions
        if (cleaned.StartsWith("class ") ||
            cleaned.StartsWith("enum ") ||
            cleaned.StartsWith("signal ") ||
            cleaned.StartsWith("var ") ||
            cleaned.StartsWith("extends "))
        {
            return true;
        }

        return false;
    }

    private (string?, string) ExtractSignatureFromDoc(string documentation)
    {
        if (string.IsNullOrWhiteSpace(documentation))
            return (null, documentation);

        var lines = documentation.Trim().Split('\n');

        // Check first non-empty line for a signature
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (IsSignature(trimmed))
            {
                // Found signature - get index and extract remaining
                int sigIndex = Array.IndexOf(lines, line);
                var remaining = string.Join("\n", lines.Skip(sigIndex + 1)).Trim();
                return (trimmed, remaining);
            }

            // First non-empty line is not a signature
            break;
        }

        return (null, documentation);
    }

    private string NormalizeDoc(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = ContainsBBCode(text) ? ConvertBBCodeToMarkdown(text) : text;
        // Outside-fence whitespace and then fence sanitation
        s = CollapseBlanksOutsideFences(s);
        s = SanitizeAllFences(s);
        return s.Trim();
    }

    private string ConvertBBCodeToMarkdown(string bbcode)
    {
        var s = bbcode;
        // Convert explicit blocks first
        s = CodeblocksWrap.Replace(s, m => ProcessCodeBlockContent(m.Groups[1].Value));
        s = CodeblockLang.Replace(s, m => CleanAndFenceCode(m.Groups[2].Value, m.Groups[1].Value));
        s = CodeblockNoLang.Replace(s, m => CleanAndFenceCode(m.Groups[1].Value, string.Empty));
        s = GdscriptLang.Replace(s, m => CleanAndFenceCode(m.Groups[2].Value, "gdscript"));
        s = GdscriptNoLang.Replace(s, m => CleanAndFenceCode(m.Groups[1].Value, "gdscript"));
        s = CsharpBlock.Replace(s, "");

        // Apply basic inline formatting only outside fences
        s = ReplaceOutsideFences(s, segment =>
        {
            segment = Regex.Replace(segment, @"`b(?:\s+[^`]*)?`(.*?)`/b`", "**$1**", RegexOptions.Singleline);
            segment = Regex.Replace(segment, @"`i(?:\s+[^`]*)?`(.*?)`/i`", "*$1*", RegexOptions.Singleline);
            segment = Regex.Replace(segment, @"`u(?:\s+[^`]*)?`(.*?)`/u`", "__$1__", RegexOptions.Singleline);
            segment = Regex.Replace(segment, @"`s(?:\s+[^`]*)?`(.*?)`/s`", "~~$1~~", RegexOptions.Singleline);
            segment = Regex.Replace(segment, @"`url=([^`]+)`(.*?)`/url`", "[$2]($1)", RegexOptions.Singleline);
            segment = Regex.Replace(segment, @"`url(?:\s+[^`]*)?`(.*?)`/url`", "$1", RegexOptions.Singleline);
            segment = Regex.Replace(segment, @"`code(?:\s+[^`]*)?`(.*?)`/code`", "`$1`", RegexOptions.Singleline);
            // drop unknown open/close tags
            segment = Regex.Replace(segment, @"`/[a-zA-Z0-9_]+`", "", RegexOptions.Singleline);
            return segment;
        });

        return s;
    }

    private string ProcessCodeBlockContent(string content)
    {
        var s = content;
        s = GdscriptLang.Replace(s, m => CleanAndFenceCode(m.Groups[2].Value, "gdscript"));
        s = GdscriptNoLang.Replace(s, m => CleanAndFenceCode(m.Groups[1].Value, "gdscript"));
        s = CsharpBlock.Replace(s, "");
        return s;
    }

    private string CleanAndFenceCode(string code, string language)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        code = code.Replace("\r", "");
        var lines = code.Split('\n');
        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (nonEmpty.Any())
        {
            var minIndent = nonEmpty.Min(l => l.TakeWhile(char.IsWhiteSpace).Count());
            lines = lines.Select(l => string.IsNullOrWhiteSpace(l) ? "" : (l.Length >= minIndent ? l.Substring(minIndent) : l)).ToArray();
        }
        var body = string.Join("\n", lines).Trim();
        if (language.Equals("gdscript", StringComparison.OrdinalIgnoreCase))
        {
            body = InlineBackticks.Replace(body, "$1");
        }
        return string.IsNullOrEmpty(language) ? $"```\n{body}\n```" : $"```{language}\n{body}\n```";
    }

    private string CollapseBlanksOutsideFences(string input)
    {
        var text = (input ?? string.Empty).Replace("\r", "");
        var lines = text.Split('\n');
        var outLines = new List<string>(lines.Length);
        bool inFence = false;
        bool lastWasBlank = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.StartsWith("```"))
            {
                // Don't remove blank line before opening fence
                outLines.Add(l);
                inFence = !inFence;
                lastWasBlank = false;
                continue;
            }
            if (!inFence)
            {
                if (string.IsNullOrWhiteSpace(l))
                {
                    if (lastWasBlank) continue;
                    outLines.Add("");
                    lastWasBlank = true;
                }
                else
                {
                    outLines.Add(l);
                    lastWasBlank = false;
                }
            }
            else
            {
                outLines.Add(l);
            }
        }
        // Remove trailing blank line
        if (outLines.Count > 0 && outLines[^1].Length == 0)
            outLines.RemoveAt(outLines.Count - 1);
        return string.Join('\n', outLines).TrimEnd();
    }

    // Sanitize all fenced blocks: CR->LF, trim inner edges, remove alternating blank lines for any language,
    // and collapse multiple blanks; also strip gdscript inline backticks.
    private string SanitizeAllFences(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("```")) return text ?? string.Empty;
        var src = text.Replace("\r", "");
        var sb = new System.Text.StringBuilder(src.Length);
        int idx = 0;
        while (idx < src.Length)
        {
            int open = src.IndexOf("```", idx, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(src, idx, src.Length - idx);
                break;
            }
            sb.Append(src, idx, open - idx);
            int eol = src.IndexOf('\n', open);
            if (eol < 0)
            {
                sb.Append(src, open, src.Length - open);
                break;
            }
            var fenceLine = src.Substring(open, eol - open);
            var lang = fenceLine.Length > 3 ? fenceLine.Substring(3).Trim().ToLowerInvariant() : string.Empty;
            sb.Append(src, open, eol - open + 1);
            int contentStart = eol + 1;
            int close = src.IndexOf("```", contentStart, StringComparison.Ordinal);
            if (close < 0)
            {
                sb.Append(src, contentStart, src.Length - contentStart);
                break;
            }
            var inner = src.Substring(contentStart, close - contentStart).Replace("\r", "");
            var lines = inner.Split('\n');
            int start = 0; while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start])) start++;
            int end = lines.Length - 1; while (end >= start && string.IsNullOrWhiteSpace(lines[end])) end--;

            // Detect alternating blank pattern
            bool hasConsecutiveBlanks = false; int blankCount = 0, nonBlankCount = 0;
            for (int i = start; i <= end; i++)
            {
                bool blank = string.IsNullOrWhiteSpace(lines[i]);
                if (blank) { blankCount++; if (i > start && string.IsNullOrWhiteSpace(lines[i - 1])) hasConsecutiveBlanks = true; }
                else nonBlankCount++;
            }
            bool looksAlternating = blankCount > 0 && !hasConsecutiveBlanks && blankCount >= nonBlankCount - 1 && blankCount <= nonBlankCount + 1;

            if (looksAlternating)
            {
                for (int i = start; i <= end; i++)
                    if (!string.IsNullOrWhiteSpace(lines[i])) { sb.Append(lines[i].TrimEnd()); sb.Append('\n'); }
            }
            else
            {
                bool lastBlank = false;
                for (int i = start; i <= end; i++)
                {
                    bool blank = string.IsNullOrWhiteSpace(lines[i]);
                    if (blank)
                    {
                        if (lastBlank) continue; // collapse
                        sb.Append('\n');
                        lastBlank = true;
                    }
                    else
                    {
                        var line = lines[i].TrimEnd();
                        if (lang == "gdscript") line = InlineBackticks.Replace(line, "$1");
                        sb.Append(line);
                        sb.Append('\n');
                        lastBlank = false;
                    }
                }
            }

            int closeEol = src.IndexOf('\n', close);
            if (closeEol >= 0)
            {
                sb.Append(src, close, closeEol - close + 1);
                idx = closeEol + 1;
            }
            else
            {
                sb.Append(src, close, src.Length - close);
                idx = src.Length;
            }
        }
        return sb.ToString();
    }

    private string ReplaceOutsideFences(string input, Func<string, string> transform)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var lines = input.Replace("\r", "").Split('\n');
        var sb = new System.Text.StringBuilder(input.Length);
        bool inFence = false;
        var chunk = new System.Text.StringBuilder();
        void FlushChunk()
        {
            if (chunk.Length > 0)
            {
                sb.Append(transform(chunk.ToString()));
                chunk.Clear();
            }
        }
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("```"))
            {
                FlushChunk();
                inFence = !inFence;
                sb.Append(line);
                if (i < lines.Length - 1) sb.Append('\n');
                continue;
            }
            if (inFence)
            {
                sb.Append(line);
                if (i < lines.Length - 1) sb.Append('\n');
            }
            else
            {
                chunk.Append(line);
                if (i < lines.Length - 1) chunk.Append('\n');
            }
        }
        FlushChunk();
        return sb.ToString();
    }
}
