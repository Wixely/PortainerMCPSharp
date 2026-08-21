using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using PortainerMCPSharp.Services;

namespace PortainerMCPSharp.Tools;

/// <summary>Networks. Mutating tools sit behind <c>AllowNetworkManagement</c>.</summary>
[McpServerToolType]
public static class DockerNetworkTools
{
    private const string Feature = "EnableNetworks";

    [McpServerTool(Name = "docker_list_networks"),
     Description("List networks on an environment, with driver, scope and the stack that owns them.")]
    public static async Task<string> ListNetworks(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Only networks belonging to this compose stack / project name.")] string? stackName = null,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Maximum networks to return.")] int? limit = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_list_networks";
        gate.EnsureFeature(gate.Options.EnableNetworks, Tool, Feature);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        var filters = ToolHelpers.CombineFilters(
            null,
            ("label", string.IsNullOrWhiteSpace(stackName) ? null : $"com.docker.compose.project={stackName}"));

        var node = await client.DockerGetJsonAsync(
            envId, ToolHelpers.WithQuery("networks", ("filters", filters)), Tool, ct);

        var items = (node as JsonArray)?.OfType<JsonNode>().ToList() ?? new List<JsonNode>();
        var max = gate.ClampLimit(limit);

        var page = items.Take(max).Select(n => new
        {
            id = n["Id"]?.GetValue<string>()?[..Math.Min(12, n["Id"]!.GetValue<string>().Length)],
            name = n["Name"]?.GetValue<string>(),
            driver = n["Driver"]?.GetValue<string>(),
            scope = n["Scope"]?.GetValue<string>(),
            @internal = n["Internal"]?.GetValue<bool>(),
            attachable = n["Attachable"]?.GetValue<bool>(),
            stack = n["Labels"]?["com.docker.compose.project"]?.GetValue<string>(),
        }).ToList();

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            networks = page,
            count = page.Count,
            total = items.Count,
            truncated = items.Count > page.Count,
        });
    }

    [McpServerTool(Name = "docker_inspect_network"),
     Description("Get a network's full detail, including which containers are attached and their addresses.")]
    public static async Task<string> InspectNetwork(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Network id or name.")] string network,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_inspect_network";
        gate.EnsureFeature(gate.Options.EnableNetworks, Tool, Feature);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        var node = await client.DockerGetJsonAsync(envId, $"networks/{Uri.EscapeDataString(network)}", Tool, ct);

        return ToolHelpers.Json(gate, new { instance = client.Alias, environmentId = envId, network = gate.CapJson(node) });
    }

    [McpServerTool(Name = "docker_create_network"),
     Description("Create a network. Requires Portainer:ReadOnly=false and Portainer:AllowNetworkManagement=true.")]
    public static async Task<string> CreateNetwork(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Name for the new network.")] string name,
        [Description("Network driver: bridge (default), overlay for Swarm, macvlan, host, none.")] string driver = "bridge",
        [Description("Make the network internal, with no outbound external access.")] bool @internal = false,
        [Description("Allow standalone containers to attach. Needed for overlay networks used outside Swarm services.")] bool attachable = false,
        [Description("Labels as a JSON object.")] string? labels = null,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_create_network";
        gate.EnsureFeature(gate.Options.EnableNetworks, Tool, Feature);
        gate.EnsureNetworkManagement(Tool);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        var body = new JsonObject
        {
            ["Name"] = name,
            ["Driver"] = driver,
            ["Internal"] = @internal,
            ["Attachable"] = attachable,
            ["Labels"] = DockerVolumeTools.ParseObject(labels, nameof(labels), Tool),
        };

        var node = await client.DockerSendJsonAsync(HttpMethod.Post, envId, "networks/create", body, Tool, ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            created = true,
            networkId = node?["Id"]?.GetValue<string>(),
            warning = node?["Warning"]?.GetValue<string>(),
        });
    }

    [McpServerTool(Name = "docker_remove_network"),
     Description("Remove a network. Requires Portainer:ReadOnly=false and Portainer:AllowNetworkManagement=true.")]
    public static async Task<string> RemoveNetwork(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Network id or name.")] string network,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_remove_network";
        gate.EnsureFeature(gate.Options.EnableNetworks, Tool, Feature);
        gate.EnsureNetworkManagement(Tool);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        await client.DockerSendJsonAsync(
            HttpMethod.Delete, envId, $"networks/{Uri.EscapeDataString(network)}", null, Tool, ct);

        return ToolHelpers.Json(gate, new { instance = client.Alias, environmentId = envId, network, removed = true });
    }

    [McpServerTool(Name = "docker_connect_container"),
     Description("Attach a container to a network. Requires Portainer:ReadOnly=false and Portainer:AllowNetworkManagement=true.")]
    public static async Task<string> ConnectContainer(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Network id or name.")] string network,
        [Description("Container id or name.")] string container,
        [Description("Extra network aliases for the container, comma-separated.")] string? aliases = null,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_connect_container";
        gate.EnsureFeature(gate.Options.EnableNetworks, Tool, Feature);
        gate.EnsureNetworkManagement(Tool);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        await gate.EnsureContainerMutableAsync(client, envId, container, Tool, ct);

        var body = new JsonObject { ["Container"] = container };
        if (!string.IsNullOrWhiteSpace(aliases))
        {
            var array = new JsonArray();
            foreach (var a in aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                array.Add(a);
            }
            body["EndpointConfig"] = new JsonObject { ["Aliases"] = array };
        }

        await client.DockerSendJsonAsync(
            HttpMethod.Post, envId, $"networks/{Uri.EscapeDataString(network)}/connect", body, Tool, ct);

        return ToolHelpers.Json(gate, new { instance = client.Alias, environmentId = envId, network, container, connected = true });
    }

    [McpServerTool(Name = "docker_disconnect_container"),
     Description("Detach a container from a network. Requires Portainer:ReadOnly=false and Portainer:AllowNetworkManagement=true.")]
    public static async Task<string> DisconnectContainer(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Network id or name.")] string network,
        [Description("Container id or name.")] string container,
        [Description("Force the disconnect.")] bool force = false,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_disconnect_container";
        gate.EnsureFeature(gate.Options.EnableNetworks, Tool, Feature);
        gate.EnsureNetworkManagement(Tool);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        await gate.EnsureContainerMutableAsync(client, envId, container, Tool, ct);

        var body = new JsonObject { ["Container"] = container, ["Force"] = force };
        await client.DockerSendJsonAsync(
            HttpMethod.Post, envId, $"networks/{Uri.EscapeDataString(network)}/disconnect", body, Tool, ct);

        return ToolHelpers.Json(gate, new { instance = client.Alias, environmentId = envId, network, container, disconnected = true });
    }

    [McpServerTool(Name = "docker_prune_networks"),
     Description("Delete every network not used by at least one container. Requires Portainer:ReadOnly=false and Portainer:AllowPrune=true.")]
    public static async Task<string> PruneNetworks(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_prune_networks";
        gate.EnsureFeature(gate.Options.EnableNetworks, Tool, Feature);
        gate.EnsurePrune(Tool);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        var node = await client.DockerSendJsonAsync(HttpMethod.Post, envId, "networks/prune", null, Tool, ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            pruned = true,
            deleted = (node?["NetworksDeleted"] as JsonArray)?.Select(n => n?.GetValue<string>()).ToList(),
        });
    }
}
