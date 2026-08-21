using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PortainerMCPSharp.Services;

namespace PortainerMCPSharp.Tools;

/// <summary>Docker daemon level information for an environment.</summary>
[McpServerToolType]
public static class DockerSystemTools
{
    [McpServerTool(Name = "docker_info"),
     Description("Get the Docker daemon's own view of an environment: container and image counts, storage driver, kernel, OS, memory and Swarm state.")]
    public static async Task<string> Info(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_info";
        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        var node = await client.DockerGetJsonAsync(envId, "info", Tool, ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            name = node?["Name"]?.GetValue<string>(),
            serverVersion = node?["ServerVersion"]?.GetValue<string>(),
            containers = node?["Containers"]?.GetValue<int>(),
            running = node?["ContainersRunning"]?.GetValue<int>(),
            paused = node?["ContainersPaused"]?.GetValue<int>(),
            stopped = node?["ContainersStopped"]?.GetValue<int>(),
            images = node?["Images"]?.GetValue<int>(),
            storageDriver = node?["Driver"]?.GetValue<string>(),
            kernelVersion = node?["KernelVersion"]?.GetValue<string>(),
            operatingSystem = node?["OperatingSystem"]?.GetValue<string>(),
            architecture = node?["Architecture"]?.GetValue<string>(),
            cpus = node?["NCPU"]?.GetValue<int>(),
            memoryBytes = node?["MemTotal"]?.GetValue<long>(),
            swarmState = node?["Swarm"]?["LocalNodeState"]?.GetValue<string>(),
            warnings = (node?["Warnings"] as JsonArray)?.Select(w => w?.GetValue<string>()).ToList(),
        });
    }

    [McpServerTool(Name = "docker_version"),
     Description("Get the Docker Engine and API version for an environment.")]
    public static async Task<string> Version(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_version";
        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        var node = await client.DockerGetJsonAsync(envId, "version", Tool, ct);

        return ToolHelpers.Json(gate, new { instance = client.Alias, environmentId = envId, version = node });
    }

    [McpServerTool(Name = "docker_disk_usage"),
     Description("Get disk usage for an environment: how much space images, containers, volumes and the build cache are consuming, and how much is reclaimable.")]
    public static async Task<string> DiskUsage(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_disk_usage";
        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        // type= filters keep this from returning the full object inventory, which is huge.
        var node = await client.DockerGetJsonAsync(envId, "system/df", Tool, ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            layersSizeBytes = node?["LayersSize"]?.GetValue<long>(),
            images = Summarize(node?["Images"] as JsonArray),
            containers = Summarize(node?["Containers"] as JsonArray),
            volumes = Summarize(node?["Volumes"] as JsonArray),
            buildCacheBytes = (node?["BuildCache"] as JsonArray)?
                .Sum(b => b?["Size"]?.GetValue<long>() ?? 0),
        });
    }

    [McpServerTool(Name = "docker_events"),
     Description("Get Docker daemon events within an explicit time window — container starts, stops, deaths, image pulls. Useful for working out what happened to a container that is no longer running. A time window is required; this never streams.")]
    public static async Task<string> Events(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Start of the window as a Unix timestamp (seconds).")] long since,
        [Description("End of the window as a Unix timestamp (seconds). Omit for 'now'.")] long? until = null,
        [Description("Only events for this object type: container, image, volume, network, daemon, service, node.")] string? type = null,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_events";
        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        if (since <= 0)
        {
            throw new McpException(
                $"MCP tool '{Tool}' needs a 'since' Unix timestamp. Without one Docker streams events forever " +
                "and the call would simply time out.");
        }

        var end = until ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var filters = ToolHelpers.CombineFilters(null, ("type", type));

        var path = ToolHelpers.WithQuery(
            "events", ("since", since.ToString()), ("until", end.ToString()), ("filters", filters));

        // The events endpoint returns newline-delimited JSON rather than a single document.
        var (bytes, truncated) = await client.DockerGetRawAsync(envId, path, gate.Options.MaxLogBytes, Tool, ct);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        var events = new List<JsonNode?>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try { events.Add(JsonNode.Parse(line)); }
            catch (System.Text.Json.JsonException) { /* a truncated final line is expected */ }
        }

        var max = gate.ClampLimit(null);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            since,
            until = end,
            count = Math.Min(events.Count, max),
            truncated = truncated || events.Count > max,
            events = events.Take(max).Select(e => new
            {
                type = e?["Type"]?.GetValue<string>(),
                action = e?["Action"]?.GetValue<string>(),
                name = e?["Actor"]?["Attributes"]?["name"]?.GetValue<string>(),
                image = e?["Actor"]?["Attributes"]?["image"]?.GetValue<string>(),
                time = e?["time"]?.GetValue<long>(),
            }).ToList(),
        });
    }

    private static object Summarize(JsonArray? array) => new
    {
        count = array?.Count ?? 0,
        sizeBytes = array?.Sum(i => i?["Size"]?.GetValue<long>() ?? 0) ?? 0,
    };
}
