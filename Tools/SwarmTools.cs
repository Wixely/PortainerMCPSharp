using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PortainerMCPSharp.Services;

namespace PortainerMCPSharp.Tools;

/// <summary>
/// Swarm services, tasks and nodes. Only relevant on environments that are part of a Swarm
/// cluster; on a plain Docker host these calls return an error from Docker itself.
/// </summary>
[McpServerToolType]
public static class SwarmTools
{
    private const string Feature = "EnableSwarm";

    [McpServerTool(Name = "docker_get_swarm_info"),
     Description("Check whether an environment is part of a Swarm cluster and get the cluster id, node count and manager status.")]
    public static async Task<string> GetSwarmInfo(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_get_swarm_info";
        gate.EnsureFeature(gate.Options.EnableSwarm, Tool, Feature);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        var info = await client.DockerGetJsonAsync(envId, "info", Tool, ct);
        var swarm = info?["Swarm"];

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            isSwarm = !string.IsNullOrWhiteSpace(swarm?["Cluster"]?["ID"]?.GetValue<string>()),
            clusterId = swarm?["Cluster"]?["ID"]?.GetValue<string>(),
            nodeId = swarm?["NodeID"]?.GetValue<string>(),
            isManager = swarm?["ControlAvailable"]?.GetValue<bool>(),
            nodes = swarm?["Nodes"]?.GetValue<int>(),
            managers = swarm?["Managers"]?.GetValue<int>(),
            localState = swarm?["LocalNodeState"]?.GetValue<string>(),
        });
    }

    [McpServerTool(Name = "docker_list_services"),
     Description("List Swarm services with their image, replica counts and the stack they belong to. Replicas show as running/desired so a service that cannot schedule is obvious.")]
    public static async Task<string> ListServices(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Only services belonging to this Swarm stack name.")] string? stackName = null,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Maximum services to return.")] int? limit = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_list_services";
        gate.EnsureFeature(gate.Options.EnableSwarm, Tool, Feature);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        var filters = ToolHelpers.CombineFilters(
            null,
            ("label", string.IsNullOrWhiteSpace(stackName) ? null : $"com.docker.stack.namespace={stackName}"));

        var node = await client.DockerGetJsonAsync(
            envId, ToolHelpers.WithQuery("services", ("status", "true"), ("filters", filters)), Tool, ct);

        var items = (node as JsonArray)?.OfType<JsonNode>().ToList() ?? new List<JsonNode>();
        var max = gate.ClampLimit(limit);

        var page = items.Take(max).Select(s => new
        {
            id = s["ID"]?.GetValue<string>(),
            name = s["Spec"]?["Name"]?.GetValue<string>(),
            image = s["Spec"]?["TaskTemplate"]?["ContainerSpec"]?["Image"]?.GetValue<string>(),
            mode = s["Spec"]?["Mode"]?["Replicated"] is not null ? "replicated" : "global",
            desiredReplicas = s["Spec"]?["Mode"]?["Replicated"]?["Replicas"]?.GetValue<long>(),
            runningReplicas = s["ServiceStatus"]?["RunningTasks"]?.GetValue<long>(),
            stack = s["Spec"]?["Labels"]?["com.docker.stack.namespace"]?.GetValue<string>(),
            version = s["Version"]?["Index"]?.GetValue<long>(),
            updatedAt = s["UpdatedAt"]?.GetValue<string>(),
        }).ToList();

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            services = page,
            count = page.Count,
            total = items.Count,
            truncated = items.Count > page.Count,
        });
    }

    [McpServerTool(Name = "docker_inspect_service"),
     Description("Get a Swarm service's full specification, including its update status and rollback config.")]
    public static async Task<string> InspectService(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Service id or name.")] string service,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_inspect_service";
        gate.EnsureFeature(gate.Options.EnableSwarm, Tool, Feature);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        var node = await client.DockerGetJsonAsync(envId, $"services/{Uri.EscapeDataString(service)}", Tool, ct);

        return ToolHelpers.Json(gate, new { instance = client.Alias, environmentId = envId, service = gate.CapJson(node) });
    }

    [McpServerTool(Name = "docker_get_service_logs"),
     Description("Fetch recent log output from a Swarm service, aggregated across its tasks. Capped at Portainer:MaxLogBytes.")]
    public static async Task<string> GetServiceLogs(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Service id or name.")] string service,
        [Description("Number of lines from the end of the log.")] int tail = 200,
        [Description("Only lines since this Unix timestamp (seconds). 0 for no lower bound.")] long since = 0,
        [Description("Include stdout.")] bool stdout = true,
        [Description("Include stderr.")] bool stderr = true,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_get_service_logs";
        gate.EnsureFeature(gate.Options.EnableSwarm, Tool, Feature);

        if (!stdout && !stderr)
        {
            throw new McpException($"MCP tool '{Tool}' needs at least one of stdout or stderr.");
        }

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        var path = ToolHelpers.WithQuery(
            $"services/{Uri.EscapeDataString(service)}/logs",
            ("stdout", ToolHelpers.Bool(stdout)),
            ("stderr", ToolHelpers.Bool(stderr)),
            ("tail", tail > 0 ? tail.ToString() : "all"),
            ("since", since > 0 ? since.ToString() : null));

        var (bytes, truncated) = await client.DockerGetRawAsync(envId, path, gate.Options.MaxLogBytes, Tool, ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            service,
            bytes = bytes.Length,
            truncated,
            logs = PortainerClient.DecodeDockerLog(bytes),
        });
    }

    [McpServerTool(Name = "docker_update_service"),
     Description("Scale a Swarm service or force it to redeploy its tasks. Requires Portainer:ReadOnly=false and Portainer:AllowSwarmMutation=true.")]
    public static async Task<string> UpdateService(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Service id or name.")] string service,
        [Description("New replica count. Omit to leave the count unchanged.")] int? replicas = null,
        [Description("Force the service to recreate its tasks even when nothing else changed. Use this to pick up a re-pushed image tag.")] bool forceUpdate = false,
        [Description("New image for the service, e.g. ghcr.io/acme/api:1.4.0. Omit to keep the current image.")] string? image = null,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_update_service";
        gate.EnsureFeature(gate.Options.EnableSwarm, Tool, Feature);
        gate.EnsureSwarmMutation(Tool);

        if (replicas is null && !forceUpdate && string.IsNullOrWhiteSpace(image))
        {
            throw new McpException(
                $"MCP tool '{Tool}' was called with nothing to change. Pass replicas, image, or forceUpdate=true.");
        }

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        // Docker requires the service's current version index on an update, so read it first
        // rather than making the agent track it.
        var current = await client.DockerGetJsonAsync(
            envId, $"services/{Uri.EscapeDataString(service)}", Tool, ct);

        var version = current?["Version"]?["Index"]?.GetValue<long>()
            ?? throw new McpException(
                $"Could not read the current version of service '{service}'; Docker needs it to accept an update.");

        if (current?["Spec"] is not JsonObject spec)
        {
            throw new McpException($"Service '{service}' returned no Spec to update.");
        }

        var updated = spec.DeepClone()!.AsObject();

        if (replicas is not null)
        {
            if (updated["Mode"]?["Replicated"] is null)
            {
                throw new McpException(
                    $"Service '{service}' runs in global mode, which has no replica count to set. " +
                    "Only replicated services can be scaled.");
            }
            updated["Mode"]!["Replicated"]!["Replicas"] = replicas.Value;
        }

        if (!string.IsNullOrWhiteSpace(image))
        {
            if (updated["TaskTemplate"]?["ContainerSpec"] is not JsonObject containerSpec)
            {
                throw new McpException($"Service '{service}' has no ContainerSpec to set an image on.");
            }
            containerSpec["Image"] = image;
        }

        if (forceUpdate)
        {
            if (updated["TaskTemplate"] is JsonObject taskTemplate)
            {
                var current_force = taskTemplate["ForceUpdate"]?.GetValue<long>() ?? 0;
                taskTemplate["ForceUpdate"] = current_force + 1;
            }
        }

        await client.DockerSendJsonAsync(
            HttpMethod.Post, envId,
            $"services/{Uri.EscapeDataString(service)}/update?version={version}",
            updated, Tool, ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            service,
            updated = true,
            replicas,
            image,
            forceUpdate,
            note = "Swarm accepted the update. Tasks reschedule asynchronously — call docker_list_services to see "
                 + "running vs desired replicas settle.",
        });
    }

    [McpServerTool(Name = "docker_list_tasks"),
     Description("List Swarm tasks (the individual container instances behind services), with their state and any error message. This is where a failing service shows why it will not start.")]
    public static async Task<string> ListTasks(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Only tasks for this service id or name.")] string? service = null,
        [Description("Only tasks in this state, e.g. running, failed, rejected, shutdown.")] string? state = null,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Maximum tasks to return.")] int? limit = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_list_tasks";
        gate.EnsureFeature(gate.Options.EnableSwarm, Tool, Feature);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        var filters = ToolHelpers.CombineFilters(null, ("service", service), ("desired-state", state));
        var node = await client.DockerGetJsonAsync(
            envId, ToolHelpers.WithQuery("tasks", ("filters", filters)), Tool, ct);

        var items = (node as JsonArray)?.OfType<JsonNode>().ToList() ?? new List<JsonNode>();
        var max = gate.ClampLimit(limit);

        var page = items.Take(max).Select(t => new
        {
            id = t["ID"]?.GetValue<string>(),
            serviceId = t["ServiceID"]?.GetValue<string>(),
            nodeId = t["NodeID"]?.GetValue<string>(),
            slot = t["Slot"]?.GetValue<int>(),
            desiredState = t["DesiredState"]?.GetValue<string>(),
            state = t["Status"]?["State"]?.GetValue<string>(),
            message = t["Status"]?["Message"]?.GetValue<string>(),
            error = t["Status"]?["Err"]?.GetValue<string>(),
            image = t["Spec"]?["ContainerSpec"]?["Image"]?.GetValue<string>(),
            timestamp = t["Status"]?["Timestamp"]?.GetValue<string>(),
        }).ToList();

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            tasks = page,
            count = page.Count,
            total = items.Count,
            truncated = items.Count > page.Count,
        });
    }

    [McpServerTool(Name = "docker_list_nodes"),
     Description("List the nodes in a Swarm cluster with their role, availability and status.")]
    public static async Task<string> ListNodes(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_list_nodes";
        gate.EnsureFeature(gate.Options.EnableSwarm, Tool, Feature);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        var node = await client.DockerGetJsonAsync(envId, "nodes", Tool, ct);

        var nodes = (node as JsonArray)?.Select(n => new
        {
            id = n?["ID"]?.GetValue<string>(),
            hostname = n?["Description"]?["Hostname"]?.GetValue<string>(),
            role = n?["Spec"]?["Role"]?.GetValue<string>(),
            availability = n?["Spec"]?["Availability"]?.GetValue<string>(),
            state = n?["Status"]?["State"]?.GetValue<string>(),
            address = n?["Status"]?["Addr"]?.GetValue<string>(),
            leader = n?["ManagerStatus"]?["Leader"]?.GetValue<bool>(),
            engineVersion = n?["Description"]?["Engine"]?["EngineVersion"]?.GetValue<string>(),
            version = n?["Version"]?["Index"]?.GetValue<long>(),
        }).ToList();

        return ToolHelpers.Json(gate, new { instance = client.Alias, environmentId = envId, nodes });
    }

    [McpServerTool(Name = "docker_update_node"),
     Description("Change a Swarm node's availability: active, pause (no new tasks), or drain (move tasks off it). Requires Portainer:ReadOnly=false and Portainer:AllowSwarmMutation=true.")]
    public static async Task<string> UpdateNode(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Node id, from docker_list_nodes.")] string nodeId,
        [Description("New availability: active, pause, or drain.")] string availability,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_update_node";
        gate.EnsureFeature(gate.Options.EnableSwarm, Tool, Feature);
        gate.EnsureSwarmMutation(Tool);

        var normalized = availability?.ToLowerInvariant();
        if (normalized is not ("active" or "pause" or "drain"))
        {
            throw new McpException(
                $"MCP tool '{Tool}' does not understand availability '{availability}'. Use active, pause, or drain.");
        }

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        var current = await client.DockerGetJsonAsync(envId, $"nodes/{Uri.EscapeDataString(nodeId)}", Tool, ct);
        var version = current?["Version"]?["Index"]?.GetValue<long>()
            ?? throw new McpException($"Could not read the current version of node '{nodeId}'.");

        if (current?["Spec"] is not JsonObject spec)
        {
            throw new McpException($"Node '{nodeId}' returned no Spec to update.");
        }

        var updated = spec.DeepClone()!.AsObject();
        updated["Availability"] = normalized;

        await client.DockerSendJsonAsync(
            HttpMethod.Post, envId, $"nodes/{Uri.EscapeDataString(nodeId)}/update?version={version}", updated, Tool, ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            nodeId,
            availability = normalized,
            updated = true,
        });
    }
}
