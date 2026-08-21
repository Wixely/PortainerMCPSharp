using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using PortainerMCPSharp.Services;

namespace PortainerMCPSharp.Tools;

/// <summary>
/// Reading stacks. Nothing here changes anything, so none of it is gated beyond the
/// EnableStacks toggle and the allow/block lists.
/// </summary>
[McpServerToolType]
public static class StackTools
{
    [McpServerTool(Name = "portainer_list_stacks"),
     Description("List the stacks this Portainer manages, with their id, name, environment, type (compose/swarm/kubernetes), status (active/inactive/deploying/error) and whether they are backed by git. Start here when you need a stack id.")]
    public static async Task<string> ListStacks(
        PortainerRegistry registry,
        SafetyGate gate,
        StackResolver resolver,
        [Description("Only stacks on this environment (endpoint) id. Omit for all environments.")] int? environmentId = null,
        [Description("Only stacks whose name contains this text, case-insensitive.")] string? nameContains = null,
        [Description("Only stacks in this state: active, inactive, deploying, error.")] string? status = null,
        [Description("Maximum stacks to return.")] int? limit = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableStacks, "portainer_list_stacks", "EnableStacks");
        if (environmentId is not null) gate.EnsureEnvironmentAllowed(environmentId.Value);

        var client = registry.Get(alias);
        var stacks = await resolver.ListAsync(client, environmentId, "portainer_list_stacks", ct);

        IEnumerable<StackRef> filtered = stacks.Where(s => IsVisible(gate, s));

        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            filtered = filtered.Where(s => s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            filtered = filtered.Where(s => string.Equals(s.StatusName, status, StringComparison.OrdinalIgnoreCase));
        }

        var all = filtered.ToList();
        var max = gate.ClampLimit(limit);
        var page = all.Take(max).Select(Summarize).ToList();

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            stacks = page,
            count = page.Count,
            total = all.Count,
            truncated = all.Count > page.Count,
        });
    }

    [McpServerTool(Name = "portainer_find_stack"),
     Description("Resolve a stack name (or id) to the facts every other stack tool needs: stack id, environment id, type, status and whether it is git-backed. Use this once, then pass the ids to the deploy tools.")]
    public static async Task<string> FindStack(
        PortainerRegistry registry,
        SafetyGate gate,
        StackResolver resolver,
        [Description("Stack name or numeric stack id.")] string stack,
        [Description("Narrow the search to one environment id, when two environments have a stack of the same name.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableStacks, "portainer_find_stack", "EnableStacks");

        var client = registry.Get(alias);
        var resolved = await resolver.ResolveAsync(client, stack, environmentId, "portainer_find_stack", ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            stack = Summarize(resolved),
            updateTool = resolved.IsGitStack ? "portainer_redeploy_stack_from_git" : "portainer_update_stack",
        });
    }

    [McpServerTool(Name = "portainer_get_stack"),
     Description("Get one stack's full detail: environment, type, status, git configuration, auto-update settings and environment variables. Secret-looking env values are masked unless Portainer:RedactSecrets=false.")]
    public static async Task<string> GetStack(
        PortainerRegistry registry,
        SafetyGate gate,
        StackResolver resolver,
        Redaction redaction,
        [Description("Stack name or numeric stack id.")] string stack,
        [Description("Narrow the search to one environment id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableStacks, "portainer_get_stack", "EnableStacks");

        var client = registry.Get(alias);
        var resolved = await resolver.ResolveAsync(client, stack, environmentId, "portainer_get_stack", ct);
        var node = await client.GetJsonAsync($"api/stacks/{resolved.Id}", "portainer_get_stack", ct) as JsonObject
                   ?? resolved.Raw;

        var detailed = StackResolver.FromJson(node);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            stack = Summarize(detailed),
            env = redaction.RedactPairs(node["Env"] as JsonArray),
            autoUpdate = node["AutoUpdate"],
            createdBy = node["CreatedBy"]?.GetValue<string>(),
            creationDate = node["CreationDate"]?.GetValue<long>(),
            entryPoint = node["EntryPoint"]?.GetValue<string>(),
            additionalFiles = (node["AdditionalFiles"] as JsonArray)?.Select(f => f?.GetValue<string>()).ToList(),
            secretsRedacted = redaction.Enabled,
        });
    }

    [McpServerTool(Name = "portainer_get_stack_file"),
     Description("Get a stack's compose file or Kubernetes manifest as text. Inline secret-looking values are masked unless Portainer:RedactSecrets=false. Output is capped at Portainer:MaxStackFileChars.")]
    public static async Task<string> GetStackFile(
        PortainerRegistry registry,
        SafetyGate gate,
        StackResolver resolver,
        Redaction redaction,
        [Description("Stack name or numeric stack id.")] string stack,
        [Description("Narrow the search to one environment id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableStacks, "portainer_get_stack_file", "EnableStacks");

        var client = registry.Get(alias);
        var resolved = await resolver.ResolveAsync(client, stack, environmentId, "portainer_get_stack_file", ct);
        var node = await client.GetJsonAsync($"api/stacks/{resolved.Id}/file", "portainer_get_stack_file", ct);

        var content = node?["StackFileContent"]?.GetValue<string>() ?? string.Empty;
        var redacted = redaction.RedactComposeFile(content);

        var max = gate.Options.MaxStackFileChars;
        var truncated = redacted.Length > max;
        if (truncated) redacted = redacted[..max];

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            stackId = resolved.Id,
            stackName = resolved.Name,
            environmentId = resolved.EndpointId,
            type = resolved.TypeName,
            isGitStack = resolved.IsGitStack,
            content = redacted,
            chars = redacted.Length,
            truncated,
            secretsRedacted = redaction.Enabled,
        });
    }

    [McpServerTool(Name = "portainer_get_stack_images_status"),
     Description("Check whether a stack's images are outdated compared with their registry. This is the 'should I redeploy?' check — call it before pulling and redeploying.")]
    public static async Task<string> GetImagesStatus(
        PortainerRegistry registry,
        SafetyGate gate,
        StackResolver resolver,
        [Description("Stack name or numeric stack id.")] string stack,
        [Description("Narrow the search to one environment id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        gate.EnsureFeature(gate.Options.EnableStacks, "portainer_get_stack_images_status", "EnableStacks");

        var client = registry.Get(alias);
        var resolved = await resolver.ResolveAsync(client, stack, environmentId, "portainer_get_stack_images_status", ct);
        var node = await client.GetJsonAsync(
            $"api/stacks/{resolved.Id}/images_status", "portainer_get_stack_images_status", ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            stackId = resolved.Id,
            stackName = resolved.Name,
            imagesStatus = node,
        });
    }

    // ---------------------------------------------------------------- shaping

    internal static bool IsVisible(SafetyGate gate, StackRef stack)
    {
        var options = gate.Options;

        if (options.AllowedStacks.Count > 0 &&
            !options.AllowedStacks.Contains(stack.Name, StringComparer.OrdinalIgnoreCase)) return false;
        if (options.BlockedStacks.Contains(stack.Name, StringComparer.OrdinalIgnoreCase)) return false;

        if (options.AllowedEnvironmentIds.Count > 0 &&
            !options.AllowedEnvironmentIds.Contains(stack.EndpointId)) return false;
        return !options.BlockedEnvironmentIds.Contains(stack.EndpointId);
    }

    internal static object Summarize(StackRef stack) => new
    {
        id = stack.Id,
        name = stack.Name,
        environmentId = stack.EndpointId,
        type = stack.TypeName,
        typeCode = stack.Type,
        status = stack.StatusName,
        statusCode = stack.Status,
        isGitStack = stack.IsGitStack,
        gitUrl = stack.GitUrl,
        gitReference = stack.GitReference,
        composeFilePath = stack.ComposeFilePath,
        webhookId = stack.WebhookId,
    };
}
