using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PortainerMCPSharp.Services;

namespace PortainerMCPSharp.Tools;

/// <summary>
/// Containers, through Portainer's Docker proxy. These are Docker Engine operations rather
/// than Portainer ones, hence the <c>docker_</c> prefix.
///
/// Three endpoints here stream forever unless bounded — logs, stats and events — so the bounds
/// are applied by this layer rather than trusted to the caller.
/// </summary>
[McpServerToolType]
public static class DockerContainerTools
{
    private const string Feature = "EnableContainers";

    [McpServerTool(Name = "docker_list_containers"),
     Description("List containers on an environment, with their name, image, state, status and the stack they belong to. By default only running containers are returned.")]
    public static async Task<string> ListContainers(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Environment (endpoint) id. Falls back to Portainer:DefaultEnvironmentId.")] int? environmentId = null,
        [Description("Include stopped and exited containers, not just running ones.")] bool all = false,
        [Description("Only containers belonging to this compose stack / project name.")] string? stackName = null,
        [Description("Only containers whose name contains this text.")] string? nameContains = null,
        [Description("Only containers in this state: created, restarting, running, removing, paused, exited, dead.")] string? state = null,
        [Description("Raw Docker filters JSON, for anything the simple arguments do not cover. Overrides stackName and state when given.")] string? filters = null,
        [Description("Maximum containers to return.")] int? limit = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_list_containers";
        gate.EnsureFeature(gate.Options.EnableContainers, Tool, Feature);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        var combined = ToolHelpers.CombineFilters(
            filters,
            ("label", string.IsNullOrWhiteSpace(stackName) ? null : $"com.docker.compose.project={stackName}"),
            ("status", state));

        var path = ToolHelpers.WithQuery(
            "containers/json",
            ("all", ToolHelpers.Bool(all || !string.IsNullOrWhiteSpace(state))),
            ("filters", combined));

        var node = await client.DockerGetJsonAsync(envId, path, Tool, ct);
        var items = (node as JsonArray)?.OfType<JsonNode>().ToList() ?? new List<JsonNode>();

        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            items = items
                .Where(c => (ToolHelpers.PrimaryName(c) ?? string.Empty)
                    .Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var max = gate.ClampLimit(limit);
        var page = items.Take(max).Select(Summarize).ToList();

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            containers = page,
            count = page.Count,
            total = items.Count,
            truncated = items.Count > page.Count,
        });
    }

    [McpServerTool(Name = "docker_inspect_container"),
     Description("Get a container's full Docker inspect payload: config, mounts, networks, restart count and exit code. Large — use docker_list_containers when you only need state.")]
    public static async Task<string> InspectContainer(
        PortainerRegistry registry,
        SafetyGate gate,
        Redaction redaction,
        [Description("Container id or name.")] string container,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_inspect_container";
        gate.EnsureFeature(gate.Options.EnableContainers, Tool, Feature);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        var node = await client.DockerGetJsonAsync(
            envId, $"containers/{Uri.EscapeDataString(container)}/json", Tool, ct) as JsonObject;

        // Container env is cleartext and often holds real credentials; pull it out and redact it
        // rather than letting the raw blob carry it into the agent's context.
        var env = redaction.RedactDockerEnv(node?["Config"]?["Env"] as JsonArray);
        if (redaction.Enabled && node?["Config"] is JsonObject config)
        {
            config.Remove("Env");
        }

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            env,
            secretsRedacted = redaction.Enabled,
            inspect = gate.CapJson(node),
        });
    }

    [McpServerTool(Name = "docker_get_container_logs"),
     Description("Fetch a container's recent log output. Returns the last 200 lines by default and is capped at Portainer:MaxLogBytes. This is the first thing to check when a deploy succeeded but the service is not working.")]
    public static async Task<string> GetLogs(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Container id or name.")] string container,
        [Description("Number of lines from the end of the log. Use a smaller number to save context.")] int tail = 200,
        [Description("Only lines since this Unix timestamp (seconds). 0 for no lower bound.")] long since = 0,
        [Description("Include stdout.")] bool stdout = true,
        [Description("Include stderr.")] bool stderr = true,
        [Description("Prefix each line with its timestamp.")] bool timestamps = false,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_get_container_logs";
        gate.EnsureFeature(gate.Options.EnableContainers, Tool, Feature);

        if (!stdout && !stderr)
        {
            throw new McpException(
                $"MCP tool '{Tool}' needs at least one of stdout or stderr. Docker returns nothing otherwise.");
        }

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        // follow is deliberately never set: it would stream until the request timed out.
        var path = ToolHelpers.WithQuery(
            $"containers/{Uri.EscapeDataString(container)}/logs",
            ("stdout", ToolHelpers.Bool(stdout)),
            ("stderr", ToolHelpers.Bool(stderr)),
            ("timestamps", ToolHelpers.Bool(timestamps)),
            ("tail", tail > 0 ? tail.ToString() : "all"),
            ("since", since > 0 ? since.ToString() : null));

        var (bytes, truncated) = await client.DockerGetRawAsync(envId, path, gate.Options.MaxLogBytes, Tool, ct);
        var text = PortainerClient.DecodeDockerLog(bytes);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            container,
            tail,
            bytes = bytes.Length,
            truncated,
            logs = text,
        });
    }

    [McpServerTool(Name = "docker_get_container_stats"),
     Description("Get a one-shot CPU, memory, network and block IO snapshot for a container. Always a single sample, never a stream.")]
    public static async Task<string> GetStats(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Container id or name.")] string container,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_get_container_stats";
        gate.EnsureFeature(gate.Options.EnableContainers, Tool, Feature);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        // stream=false is mandatory: the default streams samples until the connection drops.
        var node = await client.DockerGetJsonAsync(
            envId, $"containers/{Uri.EscapeDataString(container)}/stats?stream=false&one-shot=false", Tool, ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            container,
            stats = gate.CapJson(node),
        });
    }

    [McpServerTool(Name = "docker_top_container"),
     Description("List the processes running inside a container, like 'docker top'.")]
    public static async Task<string> TopContainer(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Container id or name.")] string container,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_top_container";
        gate.EnsureFeature(gate.Options.EnableContainers, Tool, Feature);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        var node = await client.DockerGetJsonAsync(
            envId, $"containers/{Uri.EscapeDataString(container)}/top", Tool, ct);

        return ToolHelpers.Json(gate, new { instance = client.Alias, environmentId = envId, container, processes = node });
    }

    // ---------------------------------------------------------------- lifecycle

    [McpServerTool(Name = "docker_start_container"),
     Description("Start a stopped container. Requires Portainer:ReadOnly=false and Portainer:AllowContainerLifecycle=true.")]
    public static Task<string> StartContainer(
        PortainerRegistry registry, SafetyGate gate,
        [Description("Container id or name.")] string container,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
        => LifecycleAsync(registry, gate, container, environmentId, alias, "start", null, "docker_start_container", ct);

    [McpServerTool(Name = "docker_stop_container"),
     Description("Stop a running container. Requires Portainer:ReadOnly=false and Portainer:AllowContainerLifecycle=true.")]
    public static Task<string> StopContainer(
        PortainerRegistry registry, SafetyGate gate,
        [Description("Container id or name.")] string container,
        [Description("Seconds to wait for a graceful shutdown before killing it.")] int timeoutSeconds = 10,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
        => LifecycleAsync(registry, gate, container, environmentId, alias, "stop", ("t", timeoutSeconds.ToString()), "docker_stop_container", ct);

    [McpServerTool(Name = "docker_restart_container"),
     Description("Restart a container. Requires Portainer:ReadOnly=false and Portainer:AllowContainerLifecycle=true.")]
    public static Task<string> RestartContainer(
        PortainerRegistry registry, SafetyGate gate,
        [Description("Container id or name.")] string container,
        [Description("Seconds to wait for a graceful shutdown before killing it.")] int timeoutSeconds = 10,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
        => LifecycleAsync(registry, gate, container, environmentId, alias, "restart", ("t", timeoutSeconds.ToString()), "docker_restart_container", ct);

    [McpServerTool(Name = "docker_kill_container"),
     Description("Send a signal to a container, SIGKILL by default. Skips graceful shutdown. Requires Portainer:ReadOnly=false and Portainer:AllowContainerLifecycle=true.")]
    public static Task<string> KillContainer(
        PortainerRegistry registry, SafetyGate gate,
        [Description("Container id or name.")] string container,
        [Description("Signal to send, e.g. SIGKILL, SIGTERM, SIGHUP.")] string signal = "SIGKILL",
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
        => LifecycleAsync(registry, gate, container, environmentId, alias, "kill", ("signal", signal), "docker_kill_container", ct);

    [McpServerTool(Name = "docker_rename_container"),
     Description("Rename a container. Requires Portainer:ReadOnly=false and Portainer:AllowContainerLifecycle=true.")]
    public static async Task<string> RenameContainer(
        PortainerRegistry registry, SafetyGate gate,
        [Description("Container id or current name.")] string container,
        [Description("New name for the container.")] string newName,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_rename_container";
        gate.EnsureFeature(gate.Options.EnableContainers, Tool, Feature);
        gate.EnsureContainerLifecycle(Tool);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        await gate.EnsureContainerMutableAsync(client, envId, container, Tool, ct);

        await client.DockerSendJsonAsync(
            HttpMethod.Post, envId,
            $"containers/{Uri.EscapeDataString(container)}/rename?name={Uri.EscapeDataString(newName)}",
            null, Tool, ct);

        return ToolHelpers.Json(gate, new { instance = client.Alias, environmentId = envId, container, renamedTo = newName });
    }

    private static async Task<string> LifecycleAsync(
        PortainerRegistry registry, SafetyGate gate, string container, int? environmentId, string? alias,
        string action, (string Key, string Value)? parameter, string tool, CancellationToken ct)
    {
        gate.EnsureFeature(gate.Options.EnableContainers, tool, Feature);
        gate.EnsureContainerLifecycle(tool);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, tool);

        // Inspect before acting so Portainer's own container can be recognised and refused.
        await gate.EnsureContainerMutableAsync(client, envId, container, tool, ct);

        var path = ToolHelpers.WithQuery(
            $"containers/{Uri.EscapeDataString(container)}/{action}",
            parameter is null ? default : (parameter.Value.Key, parameter.Value.Value));

        await client.DockerSendJsonAsync(HttpMethod.Post, envId, path, null, tool, ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            container,
            action,
            accepted = true,
            note = "Docker accepted the request. Confirm with docker_list_containers if the outcome matters.",
        });
    }

    [McpServerTool(Name = "docker_remove_container"),
     Description("Remove a container, discarding its writable layer. Requires Portainer:ReadOnly=false and Portainer:AllowContainerRemoval=true. Removing its anonymous volumes additionally requires Portainer:AllowVolumeManagement=true.")]
    public static async Task<string> RemoveContainer(
        PortainerRegistry registry, SafetyGate gate,
        [Description("Container id or name.")] string container,
        [Description("Remove even if the container is running.")] bool force = false,
        [Description("Also remove anonymous volumes attached to the container. Destroys data.")] bool removeVolumes = false,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_remove_container";
        gate.EnsureFeature(gate.Options.EnableContainers, Tool, Feature);
        gate.EnsureContainerRemoval(Tool);
        if (removeVolumes) gate.EnsureVolumeManagement(Tool);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        await gate.EnsureContainerMutableAsync(client, envId, container, Tool, ct);

        var path = ToolHelpers.WithQuery(
            $"containers/{Uri.EscapeDataString(container)}",
            ("force", ToolHelpers.Bool(force)),
            ("v", ToolHelpers.Bool(removeVolumes)));

        await client.DockerSendJsonAsync(HttpMethod.Delete, envId, path, null, Tool, ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            container,
            removed = true,
            volumesRemoved = removeVolumes,
        });
    }

    [McpServerTool(Name = "docker_create_container"),
     Description("Create a container from a raw Docker container-create JSON body. Prefer deploying a stack instead — containers made this way are unmanaged and show as 'external' in Portainer. Requires Portainer:ReadOnly=false and Portainer:AllowContainerCreate=true.")]
    public static async Task<string> CreateContainer(
        PortainerRegistry registry, SafetyGate gate,
        [Description("Name for the new container.")] string name,
        [Description("Docker container-create body as JSON, e.g. {\"Image\":\"nginx:alpine\",\"HostConfig\":{\"PortBindings\":{\"80/tcp\":[{\"HostPort\":\"8080\"}]}}}.")] string configJson,
        [Description("Start the container immediately after creating it.")] bool start = true,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_create_container";
        gate.EnsureFeature(gate.Options.EnableContainers, Tool, Feature);
        gate.EnsureContainerCreate(Tool);

        JsonNode? config;
        try
        {
            config = JsonNode.Parse(configJson);
        }
        catch (JsonException ex)
        {
            throw new McpException(
                $"MCP tool '{Tool}' could not parse configJson: {ex.Message} " +
                "It must be a Docker container-create body, e.g. {\"Image\":\"nginx:alpine\"}.");
        }

        if (config is not JsonObject configObject)
        {
            throw new McpException($"MCP tool '{Tool}' expected configJson to be a JSON object.");
        }

        if (string.IsNullOrWhiteSpace(configObject["Image"]?.GetValue<string>()))
        {
            throw new McpException($"MCP tool '{Tool}' requires an \"Image\" property in configJson.");
        }

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        var created = await client.DockerSendJsonAsync(
            HttpMethod.Post, envId,
            $"containers/create?name={Uri.EscapeDataString(name)}",
            configObject, Tool, ct);

        var id = created?["Id"]?.GetValue<string>();

        if (start && !string.IsNullOrWhiteSpace(id))
        {
            await client.DockerSendJsonAsync(
                HttpMethod.Post, envId, $"containers/{id}/start", null, Tool, ct);
        }

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            created = true,
            containerId = id,
            name,
            started = start,
            warnings = created?["Warnings"],
        });
    }

    // ---------------------------------------------------------------- shaping

    private static object Summarize(JsonNode? container) => new
    {
        id = container?["Id"]?.GetValue<string>()?[..Math.Min(12, container["Id"]!.GetValue<string>().Length)],
        fullId = container?["Id"]?.GetValue<string>(),
        name = ToolHelpers.PrimaryName(container),
        image = container?["Image"]?.GetValue<string>(),
        state = container?["State"]?.GetValue<string>(),
        status = container?["Status"]?.GetValue<string>(),
        stack = ToolHelpers.ComposeProject(container),
        service = ToolHelpers.ComposeService(container),
        created = container?["Created"]?.GetValue<long>(),
        ports = (container?["Ports"] as JsonArray)?.Select(p => new
        {
            @private = p?["PrivatePort"]?.GetValue<int>(),
            @public = p?["PublicPort"]?.GetValue<int>(),
            type = p?["Type"]?.GetValue<string>(),
        }).ToList(),
    };
}
