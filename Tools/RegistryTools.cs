using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using PortainerMCPSharp.Services;

namespace PortainerMCPSharp.Tools;

/// <summary>
/// Container registries known to Portainer. Read-only: registry credentials are never
/// returned, only the id a pull needs in order to reference them.
/// </summary>
[McpServerToolType]
public static class RegistryTools
{
    private const string Feature = "EnableRegistries";

    [McpServerTool(Name = "portainer_list_registries"),
     Description("List the container registries configured in Portainer, with the id docker_pull_image needs for a private image. Credentials are never returned.")]
    public static async Task<string> ListRegistries(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "portainer_list_registries";
        gate.EnsureFeature(gate.Options.EnableRegistries, Tool, Feature);

        var client = registry.Get(alias);
        var node = await client.GetJsonAsync("api/registries", Tool, ct);

        var registries = (node as JsonArray)?.Select(Describe).ToList() ?? new List<object>();

        return ToolHelpers.Json(gate, new { instance = client.Alias, registries, count = registries.Count });
    }

    [McpServerTool(Name = "portainer_get_registry"),
     Description("Get one registry's configuration by id. Credentials are never returned.")]
    public static async Task<string> GetRegistry(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Registry id.")] int registryId,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "portainer_get_registry";
        gate.EnsureFeature(gate.Options.EnableRegistries, Tool, Feature);

        var client = registry.Get(alias);
        var node = await client.GetJsonAsync($"api/registries/{registryId}", Tool, ct);

        return ToolHelpers.Json(gate, new { instance = client.Alias, registry = Describe(node) });
    }

    /// <summary>
    /// Project onto a fixed field set rather than filtering the raw object. An allow-list
    /// cannot leak a credential that a future Portainer version adds under a new name.
    /// </summary>
    private static object Describe(JsonNode? node) => new
    {
        id = node?["Id"]?.GetValue<int>(),
        name = node?["Name"]?.GetValue<string>(),
        url = node?["URL"]?.GetValue<string>(),
        type = DescribeType(node?["Type"]?.GetValue<int>()),
        typeCode = node?["Type"]?.GetValue<int>(),
        authentication = node?["Authentication"]?.GetValue<bool>(),
        username = node?["Username"]?.GetValue<string>(),
    };

    private static string DescribeType(int? type) => type switch
    {
        1 => "quay",
        2 => "azure",
        3 => "custom",
        4 => "gitlab",
        5 => "proget",
        6 => "dockerhub",
        7 => "ecr",
        8 => "github",
        _ => $"unknown({type})",
    };
}
