using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace PortainerMCPSharp.Services;

/// <summary>A stack reduced to the facts every other stack tool needs.</summary>
public sealed record StackRef(
    int Id,
    string Name,
    int EndpointId,
    int Type,
    int Status,
    bool IsGitStack,
    string? GitUrl,
    string? GitReference,
    string? ComposeFilePath,
    string? WebhookId,
    JsonObject Raw)
{
    public string TypeName => StackResolver.DescribeType(Type);
    public string StatusName => StackResolver.DescribeStatus(Status);
}

/// <summary>
/// Turns whatever the agent said ("web", "12") into a concrete stack, and holds the routing
/// rules that Portainer's API leaves implicit.
/// </summary>
public sealed class StackResolver
{
    private readonly SafetyGate _gate;

    public StackResolver(SafetyGate gate) => _gate = gate;

    /// <summary>StackType: 1 Swarm, 2 Compose, 3 Kubernetes.</summary>
    public static string DescribeType(int type) => type switch
    {
        1 => "swarm",
        2 => "compose",
        3 => "kubernetes",
        _ => $"unknown({type})",
    };

    /// <summary>StackStatus: 1 active, 2 inactive, 3 deploying, 4 error.</summary>
    public static string DescribeStatus(int status) => status switch
    {
        1 => "active",
        2 => "inactive",
        3 => "deploying",
        4 => "error",
        _ => $"unknown({status})",
    };

    public static StackRef FromJson(JsonObject stack)
    {
        var git = stack["GitConfig"] as JsonObject;
        var hasGit = git is not null && !string.IsNullOrWhiteSpace(git["URL"]?.GetValue<string>());

        return new StackRef(
            Id: stack["Id"]?.GetValue<int>() ?? 0,
            Name: stack["Name"]?.GetValue<string>() ?? string.Empty,
            EndpointId: stack["EndpointId"]?.GetValue<int>() ?? 0,
            Type: stack["Type"]?.GetValue<int>() ?? 0,
            Status: stack["Status"]?.GetValue<int>() ?? 0,
            IsGitStack: hasGit,
            GitUrl: git?["URL"]?.GetValue<string>(),
            GitReference: git?["ReferenceName"]?.GetValue<string>(),
            ComposeFilePath: git?["ConfigFilePath"]?.GetValue<string>() ?? stack["EntryPoint"]?.GetValue<string>(),
            WebhookId: stack["AutoUpdate"]?["Webhook"]?.GetValue<string>(),
            Raw: stack);
    }

    /// <summary>List every stack the credential can see, optionally scoped to one environment.</summary>
    public async Task<List<StackRef>> ListAsync(
        PortainerClient client, int? environmentId, string tool, CancellationToken ct)
    {
        var path = "api/stacks";
        if (environmentId is not null)
        {
            // Portainer takes a JSON-encoded map here, not ordinary query parameters.
            var filters = Uri.EscapeDataString($"{{\"EndpointID\":{environmentId.Value}}}");
            path += $"?filters={filters}";
        }

        var node = await client.GetJsonAsync(path, tool, ct);
        var array = node as JsonArray ?? new JsonArray();

        return array
            .OfType<JsonObject>()
            .Select(FromJson)
            .ToList();
    }

    /// <summary>
    /// Resolve a stack by numeric id or by name. Name matching is exact and case-insensitive;
    /// an ambiguous name lists the candidates rather than guessing, because picking the wrong
    /// stack here means deploying to the wrong environment.
    /// </summary>
    public async Task<StackRef> ResolveAsync(
        PortainerClient client, string stackIdOrName, int? environmentId, string tool, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stackIdOrName))
        {
            throw new McpException($"MCP tool '{tool}' needs a stack id or name.");
        }

        var stacks = await ListAsync(client, environmentId, tool, ct);

        if (int.TryParse(stackIdOrName, out var id))
        {
            var byId = stacks.FirstOrDefault(s => s.Id == id)
                ?? throw new McpException(
                    $"Stack {id} not found on instance '{client.Alias}'. " +
                    "Use portainer_list_stacks to see available stacks and their ids.");
            Authorize(byId);
            return byId;
        }

        var matches = stacks
            .Where(s => string.Equals(s.Name, stackIdOrName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            var known = stacks.Count == 0
                ? "no stacks are visible to this credential"
                : "visible stacks: " + string.Join(", ", stacks.Select(s => $"{s.Name} (id {s.Id}, env {s.EndpointId})"));
            throw new McpException($"No stack named '{stackIdOrName}' on instance '{client.Alias}' — {known}.");
        }

        if (matches.Count > 1)
        {
            var candidates = string.Join(", ", matches.Select(s => $"id {s.Id} on environment {s.EndpointId}"));
            throw new McpException(
                $"Stack name '{stackIdOrName}' is ambiguous on instance '{client.Alias}': {candidates}. " +
                "Pass the numeric stack id, or narrow it with environmentId.");
        }

        Authorize(matches[0]);
        return matches[0];
    }

    private void Authorize(StackRef stack)
    {
        _gate.EnsureStackAllowed(stack.Name);
        if (stack.EndpointId > 0) _gate.EnsureEnvironmentAllowed(stack.EndpointId);
    }

    /// <summary>
    /// PUT /api/stacks/{id} works only for file-based stacks. Aimed at a git-backed stack it
    /// returns an opaque failure, so catch it here and name the tool that does work.
    /// </summary>
    public static void EnsureFileBased(StackRef stack, string tool)
    {
        if (!stack.IsGitStack) return;

        throw new McpException(
            $"MCP tool '{tool}' cannot update stack '{stack.Name}' (id {stack.Id}) because it is backed by git " +
            $"({stack.GitUrl}). Portainer only accepts direct file updates for file-based stacks. " +
            "Use portainer_redeploy_stack_from_git to pull and redeploy it, or " +
            "portainer_update_stack_git_config to point it at a different branch.");
    }

    public static void EnsureGitBased(StackRef stack, string tool)
    {
        if (stack.IsGitStack) return;

        throw new McpException(
            $"MCP tool '{tool}' cannot redeploy stack '{stack.Name}' (id {stack.Id}) because it is not backed by git. " +
            "Use portainer_update_stack to change its compose content directly.");
    }

    /// <summary>
    /// Swarm stack creation needs the cluster id, which lives on the Docker side rather than in
    /// Portainer's own model. Fetched here so no tool has to make the agent supply it.
    /// </summary>
    public static async Task<string> GetSwarmIdAsync(
        PortainerClient client, int environmentId, string tool, CancellationToken ct)
    {
        var info = await client.DockerGetJsonAsync(environmentId, "info", tool, ct);
        var swarmId = info?["Swarm"]?["Cluster"]?["ID"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(swarmId))
        {
            throw new McpException(
                $"Environment {environmentId} on instance '{client.Alias}' is not part of a Swarm cluster, " +
                "so a swarm stack cannot be deployed to it. Use platform=\"standalone\" for a plain compose stack.");
        }

        return swarmId;
    }
}
