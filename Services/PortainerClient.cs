using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using PortainerMCPSharp.Configuration;

namespace PortainerMCPSharp.Services;

/// <summary>
/// Thin client over one Portainer instance. Speaks three surfaces:
/// the typed API (<c>/api/stacks</c>, <c>/api/endpoints</c>, …), the Docker proxy
/// (<c>/api/endpoints/{id}/docker/&lt;docker path&gt;</c>), and <c>/api/auth</c>.
///
/// Every outbound call funnels through <see cref="SendAsync"/> so that authentication,
/// 401-refresh, and error shaping happen in exactly one place.
/// </summary>
public sealed class PortainerClient : IDisposable
{
    private readonly PortainerOptions _options;
    private readonly HttpClient _http;
    private bool _disposed;

    public PortainerClient(PortainerInstanceEntry entry, PortainerOptions options)
    {
        _options = options;
        Entry = entry;

        if (string.IsNullOrWhiteSpace(entry.BaseUrl))
        {
            throw new McpException(
                $"Portainer instance '{entry.Alias}' has no BaseUrl. Set Portainer:Instances:<n>:BaseUrl.");
        }

        var baseUrl = entry.BaseUrl.EndsWith('/') ? entry.BaseUrl : entry.BaseUrl + "/";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new McpException(
                $"Portainer instance '{entry.Alias}' has an invalid BaseUrl: '{entry.BaseUrl}'.");
        }
        BaseUri = baseUri;

        var handler = new HttpClientHandler();
        if (entry.AllowInvalidCertificate)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = BaseUri,
            Timeout = TimeSpan.FromSeconds(Math.Max(5, options.RequestTimeoutSeconds)),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        Auth = new PortainerAuth(entry);
    }

    public PortainerInstanceEntry Entry { get; }
    public PortainerAuth Auth { get; }
    public Uri BaseUri { get; }
    public string Alias => Entry.Alias;

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    // ---------------------------------------------------------------- core send

    /// <summary>
    /// Single funnel for every outbound call. Takes a factory rather than a request because a
    /// <see cref="HttpRequestMessage"/> cannot be sent twice, and a 401 on a JWT instance has to
    /// be retried after re-authenticating.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> factory,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        var response = await SendOnceAsync(factory, completionOption, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized && Auth.CanRefresh)
        {
            response.Dispose();
            await Auth.RefreshAsync(_http, ct);
            response = await SendOnceAsync(factory, completionOption, ct);
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        Func<HttpRequestMessage> factory,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        var request = factory();
        try
        {
            await Auth.ApplyAsync(request, _http, ct);
            return await _http.SendAsync(request, completionOption, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new McpException(
                $"Could not reach Portainer at {BaseUri} for instance '{Alias}' " +
                $"({request.Method} {request.RequestUri}): {ex.Message} " +
                $"Check the instance is running and Portainer:Instances:<n>:BaseUrl points at it.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new McpException(
                $"Request to Portainer at {BaseUri} for instance '{Alias}' " +
                $"({request.Method} {request.RequestUri}) timed out after {_options.RequestTimeoutSeconds}s. " +
                "Raise Portainer:RequestTimeoutSeconds if the instance is just slow.", ex);
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>
    /// Turns a failed response into a message that says what to do about it. Portainer's status
    /// codes are unusually informative — 403 in particular means "credential fine, role wrong",
    /// which is worth stating outright so nobody goes hunting for a bad token.
    /// </summary>
    public async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await SafeReadAsync(response, ct);
        var detail = ExtractMessage(body);
        var code = (int)response.StatusCode;

        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized when Auth.Kind == PortainerCredentialKind.ApiKey =>
                $"Portainer rejected the API key for instance '{Alias}' (401). The key was sent as the X-API-KEY header. " +
                "Check it has not been revoked in Portainer under My account → Access tokens.",
            HttpStatusCode.Unauthorized =>
                $"The JWT for instance '{Alias}' was rejected and re-authentication did not help (401). " +
                "Check Portainer:Instances:<n>:Username and :Password.",
            HttpStatusCode.Forbidden =>
                $"Portainer returned 403 for {operation} on instance '{Alias}'. The credential is valid but its " +
                "RBAC role lacks permission for this operation — this is not a credential problem.",
            HttpStatusCode.NotFound =>
                $"Portainer returned 404 for {operation} on instance '{Alias}'. Either the resource id is wrong, " +
                "the environment id is wrong, or this endpoint is EE-only and the instance is CE.",
            HttpStatusCode.BadRequest =>
                $"Portainer rejected {operation} on instance '{Alias}' (400). Usually a missing required query " +
                "parameter (most stack mutations need endpointId) or a malformed body.",
            HttpStatusCode.Conflict =>
                $"Portainer reported a conflict for {operation} on instance '{Alias}' (409). For stack creation " +
                "this normally means a stack of that name already exists on the environment.",
            _ => $"Portainer returned {code} {response.ReasonPhrase} for {operation} on instance '{Alias}'.",
        };

        throw new McpException(string.IsNullOrWhiteSpace(detail) ? hint : $"{hint} Portainer said: {detail}");
    }

    // ---------------------------------------------------------------- typed API helpers

    public async Task<JsonNode?> GetJsonAsync(string path, string operation, CancellationToken ct)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, path), HttpCompletionOption.ResponseContentRead, ct);
        await EnsureSuccessAsync(response, operation, ct);
        return await ParseAsync(response, ct);
    }

    public async Task<JsonNode?> SendJsonAsync(
        HttpMethod method, string path, object? body, string operation, CancellationToken ct)
    {
        using var response = await SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(method, path);
                if (body is not null)
                {
                    request.Content = JsonContent.Create(body, options: SerializerOptions);
                }
                return request;
            },
            HttpCompletionOption.ResponseContentRead, ct);

        await EnsureSuccessAsync(response, operation, ct);
        return await ParseAsync(response, ct);
    }

    public async Task<string> GetTextAsync(string path, string operation, CancellationToken ct)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, path), HttpCompletionOption.ResponseContentRead, ct);
        await EnsureSuccessAsync(response, operation, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    // ---------------------------------------------------------------- Docker proxy

    /// <summary>
    /// Build a Docker proxy path. <paramref name="dockerPath"/> is a Docker Engine API path
    /// without a leading slash, e.g. <c>containers/json?all=true</c>.
    /// </summary>
    public static string DockerPath(int environmentId, string dockerPath) =>
        $"api/endpoints/{environmentId}/docker/{dockerPath.TrimStart('/')}";

    public Task<JsonNode?> DockerGetJsonAsync(int environmentId, string dockerPath, string operation, CancellationToken ct) =>
        GetJsonAsync(DockerPath(environmentId, dockerPath), operation, ct);

    public Task<JsonNode?> DockerSendJsonAsync(
        HttpMethod method, int environmentId, string dockerPath, object? body, string operation, CancellationToken ct) =>
        SendJsonAsync(method, DockerPath(environmentId, dockerPath), body, operation, ct);

    /// <summary>
    /// Fetch a Docker endpoint that returns a raw (non-JSON) body, such as container logs.
    /// Reads at most <paramref name="maxBytes"/> so a chatty container cannot flood the caller.
    /// </summary>
    public async Task<(byte[] Bytes, bool Truncated)> DockerGetRawAsync(
        int environmentId, string dockerPath, int maxBytes, string operation, CancellationToken ct)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, DockerPath(environmentId, dockerPath)),
            HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, operation, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var truncated = false;

        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;

            var remaining = maxBytes - (int)buffer.Length;
            if (read >= remaining)
            {
                buffer.Write(chunk, 0, Math.Max(0, remaining));
                truncated = true;
                break;
            }
            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), truncated);
    }

    /// <summary>
    /// Docker multiplexes non-TTY log streams: each chunk is prefixed with an 8-byte header of
    /// [stream type, 0, 0, 0, big-endian length]. Left in place, that header renders as binary
    /// noise interleaved with the log text. TTY containers have no framing, so the shape is
    /// detected rather than assumed.
    /// </summary>
    public static string DecodeDockerLog(byte[] raw)
    {
        if (raw.Length == 0) return string.Empty;
        if (!LooksFramed(raw)) return Encoding.UTF8.GetString(raw);

        var text = new StringBuilder();
        var offset = 0;

        while (offset + 8 <= raw.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offset + 4, 4));
            offset += 8;

            if (length <= 0) continue;

            // The final frame can be cut short by the byte cap, so take what is there.
            var available = Math.Min(length, raw.Length - offset);
            if (available <= 0) break;

            text.Append(Encoding.UTF8.GetString(raw, offset, available));
            offset += available;
        }

        return text.ToString();
    }

    private static bool LooksFramed(byte[] raw)
    {
        if (raw.Length < 8) return false;
        // Stream type is 0, 1 or 2, and the next three bytes are always padding zeros.
        if (raw[0] > 2 || raw[1] != 0 || raw[2] != 0 || raw[3] != 0) return false;

        // Guard against a TTY log that happens to start with those bytes: a real frame length
        // must not run past the buffer.
        var length = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(4, 4));
        return length <= (uint)raw.Length;
    }

    // ---------------------------------------------------------------- plumbing

    private static async Task<JsonNode?> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.NoContent) return null;

        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return null;

        try { return JsonNode.Parse(text); }
        catch (JsonException) { return JsonValue.Create(text); }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch (Exception) { return string.Empty; }
    }

    /// <summary>Portainer errors are <c>{"message": "...", "details": "..."}</c>; surface both.</summary>
    private static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        try
        {
            if (JsonNode.Parse(body) is JsonObject obj)
            {
                var message = obj["message"]?.GetValue<string>();
                var details = obj["details"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return string.IsNullOrWhiteSpace(details) || details == message
                        ? message
                        : $"{message} ({details})";
                }
            }
        }
        catch (JsonException) { /* not JSON — fall through to the raw body */ }

        return body.Length <= 500 ? body.Trim() : body[..500].Trim() + "…";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
