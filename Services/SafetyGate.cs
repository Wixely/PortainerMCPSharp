using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using PortainerMCPSharp.Configuration;

namespace PortainerMCPSharp.Services;

/// <summary>
/// Every safety decision in one place, so a tool class stays declarative and no mutating path
/// can quietly skip a check.
///
/// The model is layered: <see cref="PortainerOptions.ReadOnly"/> is the master switch, each
/// destructive category has its own gate on top, and allow/block lists scope which resources
/// are reachable at all. Error text always names the exact config key needed.
/// </summary>
public sealed class SafetyGate
{
    private readonly PortainerOptions _options;

    public SafetyGate(IOptions<PortainerOptions> options) => _options = options.Value;

    public PortainerOptions Options => _options;
    public bool IsReadOnly => _options.ReadOnly;

    // ---------------------------------------------------------------- master + category gates

    public void EnsureWriteAllowed(string tool)
    {
        if (_options.ReadOnly)
        {
            throw new McpException(
                $"MCP tool '{tool}' is blocked by server configuration. " +
                "Set Portainer:ReadOnly=false to let this server change anything.");
        }
    }

    /// <summary>Second gate on top of <see cref="EnsureWriteAllowed"/>.</summary>
    public void EnsureAllowed(bool gate, string tool, string configKey, string why)
    {
        EnsureWriteAllowed(tool);
        if (!gate)
        {
            throw new McpException(
                $"MCP tool '{tool}' requires Portainer:{configKey}=true (in addition to Portainer:ReadOnly=false). {why}");
        }
    }

    public void EnsureStackDeploy(string tool) => EnsureAllowed(
        _options.AllowStackDeploy, tool, "AllowStackDeploy",
        "It creates or changes a deployed stack.");

    public void EnsureStackLifecycle(string tool) => EnsureAllowed(
        _options.AllowStackLifecycle, tool, "AllowStackLifecycle",
        "It starts or stops a running stack.");

    public void EnsureStackDelete(string tool) => EnsureAllowed(
        _options.AllowStackDelete, tool, "AllowStackDelete",
        "Deleting a stack removes its containers.");

    public void EnsureContainerLifecycle(string tool) => EnsureAllowed(
        _options.AllowContainerLifecycle, tool, "AllowContainerLifecycle",
        "It starts, stops or restarts a running container.");

    public void EnsureContainerCreate(string tool) => EnsureAllowed(
        _options.AllowContainerCreate, tool, "AllowContainerCreate",
        "It creates a container outside any stack, which Portainer will show as external.");

    public void EnsureContainerRemoval(string tool) => EnsureAllowed(
        _options.AllowContainerRemoval, tool, "AllowContainerRemoval",
        "Removing a container discards its writable layer.");

    public void EnsureImagePull(string tool) => EnsureAllowed(
        _options.AllowImagePull, tool, "AllowImagePull",
        "It downloads an image onto the target host.");

    public void EnsureImageRemoval(string tool) => EnsureAllowed(
        _options.AllowImageRemoval, tool, "AllowImageRemoval",
        "Removing an image can break containers that are not currently running.");

    public void EnsureVolumeManagement(string tool) => EnsureAllowed(
        _options.AllowVolumeManagement, tool, "AllowVolumeManagement",
        "Volume removal destroys data that nothing else can restore.");

    public void EnsureNetworkManagement(string tool) => EnsureAllowed(
        _options.AllowNetworkManagement, tool, "AllowNetworkManagement",
        "It changes container connectivity.");

    public void EnsurePrune(string tool) => EnsureAllowed(
        _options.AllowPrune, tool, "AllowPrune",
        "Prune removes everything unused in one sweep and cannot be undone.");

    public void EnsureSwarmMutation(string tool) => EnsureAllowed(
        _options.AllowSwarmMutation, tool, "AllowSwarmMutation",
        "It rescales or reschedules workloads across the cluster.");

    public void EnsureWebhookTrigger(string tool) => EnsureAllowed(
        _options.AllowWebhookTrigger, tool, "AllowWebhookTrigger",
        "It forces a redeploy of the stack behind the webhook.");

    /// <summary>Feature toggle for a whole tool category.</summary>
    public void EnsureFeature(bool enabled, string tool, string configKey)
    {
        if (!enabled)
        {
            throw new McpException($"MCP tool '{tool}' is disabled. Set Portainer:{configKey}=true to enable it.");
        }
    }

    // ---------------------------------------------------------------- scoping

    public int EnsureEnvironmentAllowed(int environmentId)
    {
        if (_options.AllowedEnvironmentIds.Count > 0 && !_options.AllowedEnvironmentIds.Contains(environmentId))
        {
            throw new McpException(
                $"Environment {environmentId} is not in Portainer:AllowedEnvironmentIds " +
                $"({string.Join(", ", _options.AllowedEnvironmentIds)}).");
        }

        if (_options.BlockedEnvironmentIds.Contains(environmentId))
        {
            throw new McpException($"Environment {environmentId} is in Portainer:BlockedEnvironmentIds.");
        }

        return environmentId;
    }

    public void EnsureStackAllowed(string stackName)
    {
        if (string.IsNullOrWhiteSpace(stackName)) return;

        if (_options.AllowedStacks.Count > 0 &&
            !_options.AllowedStacks.Contains(stackName, StringComparer.OrdinalIgnoreCase))
        {
            throw new McpException(
                $"Stack '{stackName}' is not in Portainer:AllowedStacks " +
                $"({string.Join(", ", _options.AllowedStacks)}).");
        }

        if (_options.BlockedStacks.Contains(stackName, StringComparer.OrdinalIgnoreCase))
        {
            throw new McpException($"Stack '{stackName}' is in Portainer:BlockedStacks.");
        }
    }

    // ---------------------------------------------------------------- self-protection

    /// <summary>
    /// Refuse to mutate a container that is Portainer itself, its agent, or an explicitly
    /// blocked name. Called after inspecting the container so both name and image are known.
    /// </summary>
    public void EnsureContainerMutable(string tool, string? containerName, string? image)
    {
        var name = (containerName ?? string.Empty).TrimStart('/');

        if (_options.BlockedContainers.Count > 0 &&
            _options.BlockedContainers.Any(b =>
                string.Equals(b.TrimStart('/'), name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new McpException(
                $"MCP tool '{tool}' refused: container '{name}' is in Portainer:BlockedContainers.");
        }

        if (!_options.ProtectPortainerItself) return;

        if (IsPortainerItself(name, image))
        {
            throw new McpException(
                $"MCP tool '{tool}' refused: container '{name}' (image '{image}') is Portainer itself or its agent. " +
                "Stopping it would sever the connection this server uses and could not be undone from here. " +
                "Set Portainer:ProtectPortainerItself=false only if you have another route back in.");
        }
    }

    /// <summary>Pure predicate, kept separate so it can be reused for reporting and for tests.</summary>
    public static bool IsPortainerItself(string? containerName, string? image)
    {
        var img = image ?? string.Empty;
        if (img.Contains("portainer/portainer", StringComparison.OrdinalIgnoreCase) ||
            img.Contains("portainer/agent", StringComparison.OrdinalIgnoreCase) ||
            img.Contains("portainer-ce", StringComparison.OrdinalIgnoreCase) ||
            img.Contains("portainer-ee", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = (containerName ?? string.Empty).TrimStart('/');
        return string.Equals(name, "portainer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "portainer_agent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "portainer-agent", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Inspect the container, then apply <see cref="EnsureContainerMutable"/>.</summary>
    public async Task EnsureContainerMutableAsync(
        PortainerClient client, int environmentId, string containerId, string tool, CancellationToken ct)
    {
        if (!_options.ProtectPortainerItself && _options.BlockedContainers.Count == 0) return;

        var node = await client.DockerGetJsonAsync(
            environmentId, $"containers/{Uri.EscapeDataString(containerId)}/json", tool, ct);

        var name = node?["Name"]?.GetValue<string>();
        var image = node?["Config"]?["Image"]?.GetValue<string>() ?? node?["Image"]?.GetValue<string>();
        EnsureContainerMutable(tool, name, image);
    }

    /// <summary>Clamp a caller-supplied wait to the configured ceiling.</summary>
    public int ClampWaitSeconds(int? requested)
    {
        var seconds = requested ?? _options.DefaultWaitSeconds;
        if (seconds <= 0) seconds = _options.DefaultWaitSeconds;
        return Math.Min(seconds, Math.Max(1, _options.MaxWaitSeconds));
    }

    /// <summary>Clamp a caller-supplied item count to <see cref="PortainerOptions.MaxItems"/>.</summary>
    public int ClampLimit(int? requested)
    {
        var limit = requested ?? _options.MaxItems;
        if (limit <= 0) limit = _options.MaxItems;
        return Math.Min(limit, _options.MaxItems);
    }

    /// <summary>Resolve an environment id from the caller, the default, or fail with a named key.</summary>
    public int ResolveEnvironmentId(int? environmentId, string tool)
    {
        var id = environmentId ?? _options.DefaultEnvironmentId;
        if (id is null)
        {
            throw new McpException(
                $"MCP tool '{tool}' needs an environment id. Pass environmentId, or set " +
                "Portainer:DefaultEnvironmentId. Use portainer_list_environments to find the id.");
        }
        return EnsureEnvironmentAllowed(id.Value);
    }

    /// <summary>Cap a JSON blob so one inspect payload cannot swamp the client's context.</summary>
    public JsonNode? CapJson(JsonNode? node)
    {
        if (node is null) return null;
        var text = node.ToJsonString();
        if (text.Length <= _options.MaxJsonChars) return node;

        return new JsonObject
        {
            ["truncated"] = true,
            ["maxJsonChars"] = _options.MaxJsonChars,
            ["note"] = "Payload exceeded Portainer:MaxJsonChars and was truncated to a string preview.",
            ["preview"] = text[.._options.MaxJsonChars],
        };
    }
}
