using System.Text.Json;

namespace DataMigration.Migration.Services;

/// <summary>
/// Turns Settl's refusal into the sentence a human reads. Settl answers a rejected create with its
/// own JSON envelope, and the client hands that body on verbatim — so the operator's log used to
/// carry lines like
/// <c>HTTP 400: {"Errors":[{"Message":{"Identifier":"BioWebService.BioDomValueValidationException",
/// "Text":"PostalCode: Must not be empty\nCity: Must not be empty…</c>. Everything a person needs
/// is the <c>Text</c>; the rest is a message to a machine.
///
/// <para>Nothing is dropped when the shape is not the one described here. An answer that does not
/// parse, or parses to nothing, comes back exactly as it went in — a half-read message is worse
/// than a raw one, because a raw one can still be looked up.</para>
/// </summary>
public static class SettlErrorText
{
    /// <summary>
    /// The readable form of one Settl error string, keeping any <c>HTTP nnn:</c> prefix the client
    /// put in front. Repeated lines within the answer are said once — Settl repeats the same
    /// validation per field group, and four copies of "PostalCode: Must not be empty" is not four
    /// problems.
    /// </summary>
    public static string Readable(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw ?? "";

        var (prefix, body) = SplitPrefix(raw);
        if (body.Length == 0 || body[0] is not ('{' or '[')) return raw;

        var texts = ExtractTexts(body);
        if (texts.Count == 0) return raw;

        return prefix + string.Join("; ", texts);
    }

    /// <summary>
    /// Splits "HTTP 400: {json}" into its prefix and the JSON body. Anything that is not that
    /// shape comes back with an empty prefix and itself as the body, so the caller's own check on
    /// the body decides.
    /// </summary>
    private static (string Prefix, string Body) SplitPrefix(string raw)
    {
        var braceAt = raw.IndexOfAny(['{', '[']);
        if (braceAt <= 0) return ("", raw.Trim());
        return (raw[..braceAt], raw[braceAt..].Trim());
    }

    /// <summary>
    /// Every <c>Message.Text</c> in the answer, split on newlines, trimmed, in first-seen order and
    /// without repeats. Both shapes Settl uses are read: the <c>Errors</c> array, and a bare
    /// <c>Message</c> object.
    /// </summary>
    private static List<string> ExtractTexts(string body)
    {
        var found = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("Errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errors.EnumerateArray())
                    CollectText(error, found);
            }
            else
            {
                CollectText(root, found);
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return found;
    }

    private static void CollectText(JsonElement element, List<string> found)
    {
        if (element.ValueKind != JsonValueKind.Object) return;

        var text = element.TryGetProperty("Message", out var message) && message.ValueKind == JsonValueKind.Object
            ? message.TryGetProperty("Text", out var inner) ? inner.GetString() : null
            : element.TryGetProperty("Text", out var direct) ? direct.GetString() : null;

        if (string.IsNullOrWhiteSpace(text)) return;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !found.Contains(trimmed, StringComparer.Ordinal))
                found.Add(trimmed);
        }
    }
}
