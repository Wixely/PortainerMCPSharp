using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortainerMCPSharp.Services;

namespace PortainerMCPSharp.Tools;

/// <summary>Shared result shaping for the tool layer.</summary>
internal static class ToolHelpers
{
    public static string Json(SafetyGate gate, object value) =>
        Cap(gate, JsonSerializer.Serialize(value, JsonOpts.Default));

    public static string Json(SafetyGate gate, JsonNode? node) =>
        Cap(gate, node?.ToJsonString(PortainerClient.SerializerOptions) ?? "null");

    /// <summary>
    /// Apply the size ceiling without handing back malformed JSON. Cutting a serialized object
    /// mid-token would leave the client with something it cannot parse at all, so an oversized
    /// payload is wrapped in a small valid envelope carrying the prefix as a string.
    /// </summary>
    private static string Cap(SafetyGate gate, string json)
    {
        var limit = gate.Options.MaxJsonChars;
        if (limit <= 0 || json.Length <= limit) return json;

        var room = Math.Max(0, limit - 400);

        return JsonSerializer.Serialize(new
        {
            truncated = true,
            originalChars = json.Length,
            maxChars = limit,
            hint = "Payload exceeded Portainer:MaxJsonChars. `partial` holds the leading fragment as text, "
                 + "not parseable JSON. Narrow the request (filter the listing, ask for fewer items) or "
                 + "raise Portainer:MaxJsonChars.",
            partial = json[..room],
        }, JsonOpts.Default);
    }

    /// <summary>
    /// Docker's <c>filters</c> query parameter takes a URL-encoded JSON map of
    /// <c>{"key": ["value", …]}</c>. Built here so tools can offer plain arguments.
    /// </summary>
    public static string BuildDockerFilters(params (string Key, string? Value)[] pairs)
    {
        var filters = new JsonObject();
        foreach (var (key, value) in pairs)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            filters[key] = new JsonArray(value);
        }
        return filters.Count == 0 ? string.Empty : Uri.EscapeDataString(filters.ToJsonString());
    }

    /// <summary>Merge a raw JSON filters string (caller-supplied) with generated ones.</summary>
    public static string? CombineFilters(string? rawFilters, params (string Key, string? Value)[] pairs)
    {
        if (!string.IsNullOrWhiteSpace(rawFilters))
        {
            // The caller gave us a complete filter document; trust it and pass it through.
            return Uri.EscapeDataString(rawFilters);
        }

        var built = BuildDockerFilters(pairs);
        return string.IsNullOrEmpty(built) ? null : built;
    }

    /// <summary>Append query parameters to a path, handling the first '?' vs later '&amp;'.</summary>
    public static string WithQuery(string path, params (string Key, string? Value)[] parameters)
    {
        var builder = new StringBuilder(path);
        var first = !path.Contains('?');

        foreach (var (key, value) in parameters)
        {
            if (value is null) continue;
            builder.Append(first ? '?' : '&').Append(key).Append('=').Append(value);
            first = false;
        }

        return builder.ToString();
    }

    public static string Bool(bool value) => value ? "true" : "false";

    /// <summary>Take at most <paramref name="limit"/> items and report whether anything was dropped.</summary>
    public static (List<JsonNode> Items, bool Truncated, int Total) Limit(JsonArray? array, int limit)
    {
        var all = array?.OfType<JsonNode>().ToList() ?? new List<JsonNode>();
        return all.Count <= limit
            ? (all, false, all.Count)
            : (all.Take(limit).ToList(), true, all.Count);
    }

    /// <summary>Docker returns container names as a list with a leading slash; make it readable.</summary>
    public static string? PrimaryName(JsonNode? container)
    {
        var names = container?["Names"] as JsonArray;
        var first = names?.FirstOrDefault()?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(first)) return first.TrimStart('/');
        return container?["Name"]?.GetValue<string>()?.TrimStart('/');
    }

    /// <summary>The compose project a container belongs to, if any.</summary>
    public static string? ComposeProject(JsonNode? container) =>
        container?["Labels"]?["com.docker.compose.project"]?.GetValue<string>();

    public static string? ComposeService(JsonNode? container) =>
        container?["Labels"]?["com.docker.compose.service"]?.GetValue<string>();
}
