using System.ComponentModel;
using ModelContextProtocol.Server;
using PortainerMCPSharp.Services;

namespace PortainerMCPSharp.Tools;

/// <summary>Portainer's own status and version, useful for confirming what you are talking to.</summary>
[McpServerToolType]
public static class SystemTools
{
    [McpServerTool(Name = "portainer_system_version"),
     Description("Get the Portainer server version and edition (CE or EE), plus whether an update is available. Worth calling first: several API routes are EE-only and return 404 on CE.")]
    public static async Task<string> Version(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableSystem, "portainer_system_version", "EnableSystem");
        var client = registry.Get(alias);
        var node = await client.GetJsonAsync("api/system/version", "portainer_system_version", ct);
        return ToolHelpers.Json(gate, node);
    }

    [McpServerTool(Name = "portainer_system_status"),
     Description("Get the Portainer instance's status: instance id and API version.")]
    public static async Task<string> Status(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableSystem, "portainer_system_status", "EnableSystem");
        var client = registry.Get(alias);
        var node = await client.GetJsonAsync("api/system/status", "portainer_system_status", ct);
        return ToolHelpers.Json(gate, node);
    }

    [McpServerTool(Name = "portainer_system_info"),
     Description("Get Portainer's system information: platform, agent and edge counts, and how the instance is deployed.")]
    public static async Task<string> Info(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableSystem, "portainer_system_info", "EnableSystem");
        var client = registry.Get(alias);
        var node = await client.GetJsonAsync("api/system/info", "portainer_system_info", ct);
        return ToolHelpers.Json(gate, node);
    }
}
