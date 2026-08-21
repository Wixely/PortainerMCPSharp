using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PortainerMCPSharp.Configuration;

namespace PortainerMCPSharp.Services;

/// <summary>
/// Masks secret-looking values on the way out.
///
/// Portainer returns stack environments, container environments, and registry config in
/// cleartext. Without this those values land in the agent's context, and from there in logs
/// and transcripts — a much wider audience than the Portainer UI has. The key is always kept
/// visible so an agent can still see which variables exist and set new values for them.
/// </summary>
public sealed class Redaction
{
    public const string Mask = "***redacted***";

    private readonly PortainerOptions _options;
    private readonly Regex? _pattern;

    public Redaction(IOptions<PortainerOptions> options)
    {
        _options = options.Value;

        if (!string.IsNullOrWhiteSpace(_options.SecretKeyPattern))
        {
            try
            {
                _pattern = new Regex(
                    _options.SecretKeyPattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException)
            {
                // A bad pattern must not take the server down, and it must not silently disable
                // redaction either — fall back to a conservative built-in.
                _pattern = new Regex(
                    "PASSWORD|SECRET|TOKEN|KEY|CREDENTIAL",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(200));
            }
        }
    }

    public bool Enabled => _options.RedactSecrets;

    /// <summary>True when a variable with this name should have its value masked.</summary>
    public bool IsSecretKey(string? key)
    {
        if (!Enabled || _pattern is null || string.IsNullOrWhiteSpace(key)) return false;
        try { return _pattern.IsMatch(key); }
        catch (RegexMatchTimeoutException) { return true; }
    }

    public string? MaskValue(string? key, string? value) =>
        IsSecretKey(key) && !string.IsNullOrEmpty(value) ? Mask : value;

    /// <summary>
    /// Project Portainer's <c>[{Name, Value}]</c> env pair array into a redacted list.
    /// </summary>
    public List<object> RedactPairs(JsonArray? pairs)
    {
        var result = new List<object>();
        if (pairs is null) return result;

        foreach (var pair in pairs)
        {
            var name = pair?["Name"]?.GetValue<string>() ?? pair?["name"]?.GetValue<string>();
            var value = pair?["Value"]?.ToString() ?? pair?["value"]?.ToString();
            result.Add(new { name, value = MaskValue(name, value), redacted = IsSecretKey(name) });
        }

        return result;
    }

    /// <summary>
    /// Project Docker's <c>["KEY=value"]</c> env array into a redacted list.
    /// </summary>
    public List<object> RedactDockerEnv(JsonArray? env)
    {
        var result = new List<object>();
        if (env is null) return result;

        foreach (var item in env)
        {
            var raw = item?.GetValue<string>() ?? string.Empty;
            var split = raw.IndexOf('=');
            var name = split < 0 ? raw : raw[..split];
            var value = split < 0 ? null : raw[(split + 1)..];
            result.Add(new { name, value = MaskValue(name, value), redacted = IsSecretKey(name) });
        }

        return result;
    }

    /// <summary>
    /// Mask inline secrets in a compose file's <c>environment:</c> entries.
    ///
    /// This is line-based on purpose. Parsing YAML properly would need a dependency, and a
    /// miss here is a leaked secret, so the rule is deliberately broad: any line that looks
    /// like an assignment whose key matches the secret pattern gets its value replaced.
    /// </summary>
    public string RedactComposeFile(string compose)
    {
        if (!Enabled || string.IsNullOrEmpty(compose)) return compose;

        var lines = compose.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            // "  - KEY=value" and "  KEY: value" are the two shapes compose allows.
            var body = trimmed.StartsWith("- ", StringComparison.Ordinal) ? trimmed[2..] : trimmed;

            var eq = body.IndexOf('=');
            var colon = body.IndexOf(':');
            var split = eq >= 0 && (colon < 0 || eq < colon) ? eq : colon;
            if (split <= 0) continue;

            var key = body[..split].Trim().Trim('"', '\'');
            if (!IsSecretKey(key)) continue;

            var value = body[(split + 1)..].Trim();
            // Leave "${VAR}" interpolation alone: it names a variable, it is not the secret.
            if (value.Length == 0 || value.StartsWith("${", StringComparison.Ordinal)) continue;

            var indent = line[..(line.Length - trimmed.Length)];
            var prefix = trimmed.StartsWith("- ", StringComparison.Ordinal) ? "- " : string.Empty;
            var separator = split == eq ? "=" : ": ";
            lines[i] = $"{indent}{prefix}{key}{separator}{Mask}";
        }

        return string.Join('\n', lines);
    }
}
