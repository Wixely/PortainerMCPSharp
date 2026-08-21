using System.ComponentModel;
using ModelContextProtocol.Server;
using PortainerMCPSharp.Services;

namespace PortainerMCPSharp.Tools;

/// <summary>
/// Discovery of the configured Portainer instances. Every other tool takes an optional
/// <c>alias</c>; with a single instance configured it is never needed.
/// </summary>
[McpServerToolType]
public static class InstanceTools
{
    [McpServerTool(Name = "portainer_list_instances"),
     Description("List the Portainer instances this server is configured to talk to, with their aliases, base URLs and which one is the default. Pass an alias to any other tool to target a specific instance. Credentials are never returned.")]
    public static async Task<string> ListInstances(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Also probe each instance's /api/system/version to report whether it is reachable and what version it runs. Costs one HTTP call per instance.")] bool probe = true,
        CancellationToken ct = default)
    {
        var results = new List<object>();

        foreach (var entry in registry.Instances.Values)
        {
            string? version = null;
            string? edition = null;
            bool? reachable = null;
            string? error = null;

            if (probe)
            {
                try
                {
                    var client = registry.Get(entry.Alias);
                    var node = await client.GetJsonAsync("api/system/version", "portainer_list_instances", ct);
                    version = node?["ServerVersion"]?.GetValue<string>() ?? node?["Version"]?.GetValue<string>();
                    edition = node?["ServerEdition"]?.GetValue<string>();
                    reachable = true;
                }
                catch (Exception ex)
                {
                    // One dead instance must not hide the healthy ones.
                    reachable = false;
                    error = ex.Message;
                }
            }

            results.Add(new
            {
                alias = entry.Alias,
                baseUrl = entry.BaseUrl,
                isDefault = string.Equals(entry.Alias, registry.DefaultAlias, StringComparison.OrdinalIgnoreCase),
                credential = entry.CredentialKind,
                description = string.IsNullOrWhiteSpace(entry.Description) ? null : entry.Description,
                allowInvalidCertificate = entry.AllowInvalidCertificate,
                reachable,
                version,
                edition,
                error,
            });
        }

        return ToolHelpers.Json(gate, new
        {
            instances = results,
            defaultAlias = registry.DefaultAlias,
            readOnly = gate.IsReadOnly,
        });
    }
}
