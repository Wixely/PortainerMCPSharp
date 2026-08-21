using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PortainerMCPSharp.Services;

namespace PortainerMCPSharp.Tools;

/// <summary>
/// Volumes. Every mutating tool here is behind <c>AllowVolumeManagement</c>, which defaults
/// off: a removed volume is data nothing else can bring back.
/// </summary>
[McpServerToolType]
public static class DockerVolumeTools
{
    private const string Feature = "EnableVolumes";

    [McpServerTool(Name = "docker_list_volumes"),
     Description("List volumes on an environment, with their driver, mount point and the stack that owns them.")]
    public static async Task<string> ListVolumes(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Only volumes belonging to this compose stack / project name.")] string? stackName = null,
        [Description("Only volumes not used by any container.")] bool danglingOnly = false,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Maximum volumes to return.")] int? limit = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_list_volumes";
        gate.EnsureFeature(gate.Options.EnableVolumes, Tool, Feature);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        var filters = ToolHelpers.CombineFilters(
            null,
            ("label", string.IsNullOrWhiteSpace(stackName) ? null : $"com.docker.compose.project={stackName}"),
            ("dangling", danglingOnly ? "true" : null));

        var node = await client.DockerGetJsonAsync(
            envId, ToolHelpers.WithQuery("volumes", ("filters", filters)), Tool, ct);

        var items = (node?["Volumes"] as JsonArray)?.OfType<JsonNode>().ToList() ?? new List<JsonNode>();
        var max = gate.ClampLimit(limit);

        var page = items.Take(max).Select(v => new
        {
            name = v["Name"]?.GetValue<string>(),
            driver = v["Driver"]?.GetValue<string>(),
            mountpoint = v["Mountpoint"]?.GetValue<string>(),
            createdAt = v["CreatedAt"]?.GetValue<string>(),
            stack = v["Labels"]?["com.docker.compose.project"]?.GetValue<string>(),
        }).ToList();

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            volumes = page,
            count = page.Count,
            total = items.Count,
            truncated = items.Count > page.Count,
        });
    }

    [McpServerTool(Name = "docker_inspect_volume"),
     Description("Get a volume's full detail: driver options, labels, mount point and usage data.")]
    public static async Task<string> InspectVolume(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Volume name.")] string volume,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_inspect_volume";
        gate.EnsureFeature(gate.Options.EnableVolumes, Tool, Feature);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        var node = await client.DockerGetJsonAsync(envId, $"volumes/{Uri.EscapeDataString(volume)}", Tool, ct);

        return ToolHelpers.Json(gate, new { instance = client.Alias, environmentId = envId, volume = gate.CapJson(node) });
    }

    [McpServerTool(Name = "docker_create_volume"),
     Description("Create a named volume. Requires Portainer:ReadOnly=false and Portainer:AllowVolumeManagement=true.")]
    public static async Task<string> CreateVolume(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Name for the new volume.")] string name,
        [Description("Volume driver. Defaults to local.")] string driver = "local",
        [Description("Driver options as a JSON object, e.g. {\"type\":\"nfs\",\"device\":\":/path\"}.")] string? driverOptions = null,
        [Description("Labels as a JSON object.")] string? labels = null,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_create_volume";
        gate.EnsureFeature(gate.Options.EnableVolumes, Tool, Feature);
        gate.EnsureVolumeManagement(Tool);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        var body = new JsonObject
        {
            ["Name"] = name,
            ["Driver"] = driver,
            ["DriverOpts"] = ParseObject(driverOptions, nameof(driverOptions), Tool),
            ["Labels"] = ParseObject(labels, nameof(labels), Tool),
        };

        var node = await client.DockerSendJsonAsync(HttpMethod.Post, envId, "volumes/create", body, Tool, ct);

        return ToolHelpers.Json(gate, new { instance = client.Alias, environmentId = envId, created = true, volume = node });
    }

    [McpServerTool(Name = "docker_remove_volume"),
     Description("Remove a volume and everything stored in it. This destroys data permanently. Requires Portainer:ReadOnly=false and Portainer:AllowVolumeManagement=true.")]
    public static async Task<string> RemoveVolume(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Volume name.")] string volume,
        [Description("Remove even if a container still references it.")] bool force = false,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_remove_volume";
        gate.EnsureFeature(gate.Options.EnableVolumes, Tool, Feature);
        gate.EnsureVolumeManagement(Tool);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        await client.DockerSendJsonAsync(
            HttpMethod.Delete, envId,
            $"volumes/{Uri.EscapeDataString(volume)}?force={ToolHelpers.Bool(force)}", null, Tool, ct);

        return ToolHelpers.Json(gate, new { instance = client.Alias, environmentId = envId, volume, removed = true });
    }

    [McpServerTool(Name = "docker_prune_volumes"),
     Description("Delete every volume not currently used by a container. This destroys data permanently and cannot be undone. Requires Portainer:ReadOnly=false and Portainer:AllowPrune=true and Portainer:AllowVolumeManagement=true.")]
    public static async Task<string> PruneVolumes(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_prune_volumes";
        gate.EnsureFeature(gate.Options.EnableVolumes, Tool, Feature);
        // Doubly gated: this is the single most destructive operation the server can perform.
        gate.EnsurePrune(Tool);
        gate.EnsureVolumeManagement(Tool);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        var node = await client.DockerSendJsonAsync(HttpMethod.Post, envId, "volumes/prune", null, Tool, ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            pruned = true,
            spaceReclaimed = node?["SpaceReclaimed"]?.GetValue<long>(),
            deleted = (node?["VolumesDeleted"] as JsonArray)?.Select(v => v?.GetValue<string>()).ToList(),
        });
    }

    internal static JsonObject ParseObject(string? json, string parameterName, string tool)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();

        try
        {
            return JsonNode.Parse(json) as JsonObject
                   ?? throw new McpException($"MCP tool '{tool}' expected {parameterName} to be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new McpException($"MCP tool '{tool}' could not parse {parameterName} as JSON: {ex.Message}");
        }
    }
}
