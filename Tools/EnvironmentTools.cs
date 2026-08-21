using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using PortainerMCPSharp.Services;

namespace PortainerMCPSharp.Tools;

/// <summary>
/// Portainer environments ("endpoints") — the Docker or Kubernetes hosts a single Portainer
/// federates. Almost every stack and Docker call needs an environment id, so this is usually
/// the first tool an agent reaches for.
/// </summary>
[McpServerToolType]
public static class EnvironmentTools
{
    [McpServerTool(Name = "portainer_list_environments"),
     Description("List the Docker/Kubernetes environments this Portainer manages, with their ids, type and health. The id returned here is the environmentId every other tool needs. Snapshots (full container inventories, megabytes each) are excluded unless you ask for them.")]
    public static async Task<string> ListEnvironments(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Filter by name substring, matched server-side.")] string? search = null,
        [Description("Comma-separated type filter: 1 local Docker, 2 agent on Docker, 3 Azure ACI, 4 edge agent on Docker, 5 local Kubernetes, 6 agent on Kubernetes, 7 edge agent on Kubernetes.")] string? types = null,
        [Description("Include the Snapshots array (container/image/volume inventory). Very large — off by default.")] bool includeSnapshots = false,
        [Description("Maximum environments to return.")] int? limit = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableEnvironments, "portainer_list_environments", "EnableEnvironments");
        var client = registry.Get(alias);

        var path = ToolHelpers.WithQuery(
            "api/endpoints",
            ("excludeSnapshots", includeSnapshots ? "false" : "true"),
            ("search", string.IsNullOrWhiteSpace(search) ? null : Uri.EscapeDataString(search)),
            ("types", string.IsNullOrWhiteSpace(types) ? null : Uri.EscapeDataString(types)));

        var node = await client.GetJsonAsync(path, "portainer_list_environments", ct);
        var (items, truncated, total) = ToolHelpers.Limit(node as JsonArray, gate.ClampLimit(limit));

        var environments = items
            .Select(Describe)
            .Where(e => IsVisible(gate, e.id))
            .ToList();

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environments,
            count = environments.Count,
            totalReturnedByPortainer = total,
            truncated,
        });
    }

    [McpServerTool(Name = "portainer_get_environment"),
     Description("Get one environment by id, including its URL, type, health and group. Use portainer_list_environments first if you do not know the id.")]
    public static async Task<string> GetEnvironment(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Environment (endpoint) id.")] int environmentId,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableEnvironments, "portainer_get_environment", "EnableEnvironments");
        gate.EnsureEnvironmentAllowed(environmentId);

        var client = registry.Get(alias);
        var node = await client.GetJsonAsync($"api/endpoints/{environmentId}", "portainer_get_environment", ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environment = Describe(node),
        });
    }

    [McpServerTool(Name = "portainer_get_environment_dashboard"),
     Description("Get the counts Portainer shows on an environment's dashboard: running/stopped containers, images, volumes, networks, stacks and services. One call instead of listing everything.")]
    public static async Task<string> GetDashboard(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Environment (endpoint) id.")] int environmentId,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableEnvironments, "portainer_get_environment_dashboard", "EnableEnvironments");
        gate.EnsureEnvironmentAllowed(environmentId);

        var client = registry.Get(alias);
        var node = await client.GetJsonAsync($"api/docker/{environmentId}/dashboard", "portainer_get_environment_dashboard", ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId,
            dashboard = node,
        });
    }

    [McpServerTool(Name = "portainer_refresh_environment_snapshot"),
     Description("Force Portainer to re-snapshot an environment so its cached container/image inventory is current. Useful after deploying outside Portainer. Requires Portainer:ReadOnly=false.")]
    public static async Task<string> RefreshSnapshot(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Environment (endpoint) id.")] int environmentId,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableEnvironments, "portainer_refresh_environment_snapshot", "EnableEnvironments");
        gate.EnsureWriteAllowed("portainer_refresh_environment_snapshot");
        gate.EnsureEnvironmentAllowed(environmentId);

        var client = registry.Get(alias);
        await client.SendJsonAsync(
            HttpMethod.Post, $"api/endpoints/{environmentId}/snapshot", null,
            "portainer_refresh_environment_snapshot", ct);

        return ToolHelpers.Json(gate, new { instance = client.Alias, environmentId, snapshotRefreshed = true });
    }

    // ---------------------------------------------------------------- shaping

    private static bool IsVisible(SafetyGate gate, int id)
    {
        if (gate.Options.AllowedEnvironmentIds.Count > 0 && !gate.Options.AllowedEnvironmentIds.Contains(id)) return false;
        return !gate.Options.BlockedEnvironmentIds.Contains(id);
    }

    /// <summary>
    /// Project an environment, collapsing Portainer's two different health signals into one
    /// field. <c>Status</c> only means anything for directly connected environments; an edge
    /// agent reports Status 0 forever and its real health is the server-computed
    /// <c>Heartbeat</c> boolean. Both raw fields are kept so nothing is hidden.
    /// </summary>
    private static EnvironmentSummary Describe(JsonNode? node)
    {
        var id = node?["Id"]?.GetValue<int>() ?? 0;
        var type = node?["Type"]?.GetValue<int>() ?? 0;
        var status = node?["Status"]?.GetValue<int>() ?? 0;
        var heartbeat = node?["Heartbeat"] is JsonValue hb && hb.TryGetValue<bool>(out var beat) ? beat : (bool?)null;
        var isEdge = type is 4 or 7;

        var health = isEdge
            ? heartbeat switch { true => "up", false => "down", _ => "unknown" }
            : status switch { 1 => "up", 2 => "down", _ => "unknown" };

        return new EnvironmentSummary(
            id,
            node?["Name"]?.GetValue<string>() ?? string.Empty,
            type,
            DescribeType(type),
            health,
            isEdge,
            status,
            heartbeat,
            node?["URL"]?.GetValue<string>(),
            node?["GroupId"]?.GetValue<int>(),
            (node?["TagIds"] as JsonArray)?.Select(t => t?.GetValue<int>() ?? 0).ToList());
    }

    private static string DescribeType(int type) => type switch
    {
        1 => "docker-local",
        2 => "docker-agent",
        3 => "azure-aci",
        4 => "docker-edge-agent",
        5 => "kubernetes-local",
        6 => "kubernetes-agent",
        7 => "kubernetes-edge-agent",
        _ => $"unknown({type})",
    };

    private sealed record EnvironmentSummary(
        int id,
        string name,
        int type,
        string typeName,
        string health,
        bool isEdge,
        int rawStatus,
        bool? heartbeat,
        string? url,
        int? groupId,
        List<int>? tagIds);
}
