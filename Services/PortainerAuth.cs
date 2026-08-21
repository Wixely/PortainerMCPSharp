using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using PortainerMCPSharp.Configuration;

namespace PortainerMCPSharp.Services;

/// <summary>How a given instance authenticates.</summary>
public enum PortainerCredentialKind
{
    /// <summary>API token in the <c>X-API-KEY</c> header.</summary>
    ApiKey,

    /// <summary>Username/password exchanged for a short-lived JWT bearer token.</summary>
    Password,
}

/// <summary>
/// Holds one instance's credential and knows how to put it on a request.
///
/// The header is chosen by which credential is configured, never by the caller. That is
/// deliberate: the single most common Portainer automation failure is sending an API token as
/// <c>Authorization: Bearer</c>, which returns 401 even though the token is perfectly valid.
/// Making the choice structural means this server cannot make that mistake.
/// </summary>
public sealed class PortainerAuth
{
    private const string ApiKeyHeader = "X-API-KEY";

    private readonly PortainerInstanceEntry _entry;
    private readonly string? _password;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _jwt;

    public PortainerAuth(PortainerInstanceEntry entry)
    {
        _entry = entry;

        if (!string.IsNullOrWhiteSpace(entry.ApiKey))
        {
            Kind = PortainerCredentialKind.ApiKey;
            return;
        }

        if (!string.IsNullOrWhiteSpace(entry.Username))
        {
            Kind = PortainerCredentialKind.Password;
            _password = ResolveSecret(entry.Password);
            if (string.IsNullOrWhiteSpace(_password))
            {
                throw new McpException(
                    $"Portainer instance '{entry.Alias}' has a Username but no Password. " +
                    $"Set Portainer:Instances:<n>:Password (a 'file:<path>' value is also accepted).");
            }
            return;
        }

        throw new McpException(
            $"No credential configured for Portainer instance '{entry.Alias}'. " +
            "Set Portainer:Instances:<n>:ApiKey (preferred) or Username + Password.");
    }

    public PortainerCredentialKind Kind { get; }

    /// <summary>True when a 401 is worth retrying after re-authenticating.</summary>
    public bool CanRefresh => Kind == PortainerCredentialKind.Password;

    /// <summary>Put the current credential on a request.</summary>
    public async Task ApplyAsync(HttpRequestMessage request, HttpClient http, CancellationToken ct)
    {
        if (Kind == PortainerCredentialKind.ApiKey)
        {
            request.Headers.Remove(ApiKeyHeader);
            request.Headers.Add(ApiKeyHeader, _entry.ApiKey);
            return;
        }

        var jwt = _jwt ?? await AuthenticateAsync(http, force: false, ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
    }

    /// <summary>Discard the cached JWT and get a fresh one. Called after a 401.</summary>
    public Task<string> RefreshAsync(HttpClient http, CancellationToken ct) =>
        AuthenticateAsync(http, force: true, ct);

    private async Task<string> AuthenticateAsync(HttpClient http, bool force, CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while we waited on the lock.
            if (!force && _jwt is not null) return _jwt;

            using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth")
            {
                Content = JsonContent.Create(new { username = _entry.Username, password = _password }),
            };

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new McpException(
                    $"Portainer rejected the username/password for instance '{_entry.Alias}' " +
                    $"({(int)response.StatusCode} {response.ReasonPhrase}) at POST /api/auth. " +
                    $"Check Portainer:Instances:<n>:Username and :Password. Response: {Trim(body)}");
            }

            var jwt = JsonNode.Parse(body)?["jwt"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(jwt))
            {
                throw new McpException(
                    $"Portainer accepted the credentials for instance '{_entry.Alias}' but returned no jwt. " +
                    $"Response: {Trim(body)}");
            }

            _jwt = jwt;
            return jwt;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Supports a <c>file:&lt;path&gt;</c> prefix so a password need not sit in JSON.</summary>
    private static string ResolveSecret(string value)
    {
        if (!value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return value;

        var path = value["file:".Length..].Trim();
        if (!File.Exists(path))
        {
            throw new McpException($"Password file not found: '{path}'.");
        }
        return File.ReadAllText(path).Trim();
    }

    private static string Trim(string value) =>
        value.Length <= 500 ? value : value[..500] + "…";
}
