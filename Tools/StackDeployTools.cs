using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PortainerMCPSharp.Services;

namespace PortainerMCPSharp.Tools;

/// <summary>
/// Changing stacks. This is the point of the server, and it is where Portainer's API is most
/// full of implicit rules — which are encoded here rather than left for the agent to discover
/// through opaque 400s:
///
/// <list type="bullet">
///   <item>Nearly every mutation needs <c>?endpointId=</c>; it is inferred from the stack.</item>
///   <item><c>PUT /api/stacks/{id}</c> works only for file-based stacks; git stacks redeploy.</item>
///   <item><c>/stacks/{id}/git</c> is POST, not PUT, whatever the prose docs say.</item>
///   <item>Swarm stacks need a SwarmID, fetched from the Docker side.</item>
///   <item>A 2xx means "accepted", not "running" — hence the optional wait.</item>
/// </list>
/// </summary>
[McpServerToolType]
public static class StackDeployTools
{
    // ---------------------------------------------------------------- create

    [McpServerTool(Name = "portainer_create_stack_from_content"),
     Description("Deploy a new stack from compose file content you supply inline. Requires Portainer:ReadOnly=false and Portainer:AllowStackDeploy=true. Set waitSeconds to have the call verify the containers actually started rather than just returning 'accepted'.")]
    public static async Task<string> CreateFromContent(
        PortainerRegistry registry,
        SafetyGate gate,
        StackResolver resolver,
        [Description("Name for the new stack. Must be unique on the target environment.")] string name,
        [Description("The full docker-compose YAML content.")] string composeContent,
        [Description("Environment (endpoint) id to deploy to. Falls back to Portainer:DefaultEnvironmentId.")] int? environmentId = null,
        [Description("Deployment platform: \"standalone\" for plain compose (default), or \"swarm\" for a Swarm stack.")] string platform = "standalone",
        [Description("Environment variables as a JSON object, e.g. {\"TZ\":\"UTC\",\"PORT\":\"8080\"}.")] string? env = null,
        [Description("Seconds to wait for the stack to settle and report container states. 0 returns as soon as Portainer accepts the deploy.")] int waitSeconds = 0,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "portainer_create_stack_from_content";
        gate.EnsureFeature(gate.Options.EnableStacks, Tool, "EnableStacks");
        gate.EnsureStackDeploy(Tool);
        gate.EnsureStackAllowed(name);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        var kind = ParsePlatform(platform, Tool);

        var body = new JsonObject
        {
            ["Name"] = name,
            ["StackFileContent"] = composeContent,
            ["Env"] = ParseEnv(env, Tool),
        };

        if (kind == "swarm")
        {
            body["SwarmID"] = await StackResolver.GetSwarmIdAsync(client, envId, Tool, ct);
        }

        var created = await client.SendJsonAsync(
            HttpMethod.Post,
            $"api/stacks/create/{kind}/string?endpointId={envId}",
            body, Tool, ct) as JsonObject;

        return await FinishAsync(client, gate, resolver, created, envId, waitSeconds, Tool, ct);
    }

    [McpServerTool(Name = "portainer_create_stack_from_git"),
     Description("Deploy a new stack from a git repository. Portainer clones the repo and deploys the compose file from it. Requires Portainer:ReadOnly=false and Portainer:AllowStackDeploy=true.")]
    public static async Task<string> CreateFromGit(
        PortainerRegistry registry,
        SafetyGate gate,
        StackResolver resolver,
        [Description("Name for the new stack.")] string name,
        [Description("Git repository URL, e.g. https://github.com/acme/deploy.")] string repositoryUrl,
        [Description("Path to the compose file inside the repository. Defaults to docker-compose.yml.")] string composeFile = "docker-compose.yml",
        [Description("Git reference to deploy, e.g. refs/heads/main. Defaults to the repository default branch.")] string? reference = null,
        [Description("Environment (endpoint) id to deploy to. Falls back to Portainer:DefaultEnvironmentId.")] int? environmentId = null,
        [Description("Deployment platform: \"standalone\" (default) or \"swarm\".")] string platform = "standalone",
        [Description("Environment variables as a JSON object.")] string? env = null,
        [Description("Username for a private repository. Leave blank for public repos.")] string? gitUsername = null,
        [Description("Password or personal access token for a private repository.")] string? gitPassword = null,
        [Description("Poll the repository for changes and redeploy automatically, e.g. \"5m\" or \"1h\". Leave blank for manual redeploys only.")] string? autoUpdateInterval = null,
        [Description("Skip TLS verification when cloning. For internal git servers with self-signed certificates.")] bool tlsSkipVerify = false,
        [Description("Seconds to wait for the stack to settle and report container states.")] int waitSeconds = 0,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "portainer_create_stack_from_git";
        gate.EnsureFeature(gate.Options.EnableStacks, Tool, "EnableStacks");
        gate.EnsureStackDeploy(Tool);
        gate.EnsureStackAllowed(name);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        var kind = ParsePlatform(platform, Tool);

        var body = new JsonObject
        {
            ["Name"] = name,
            ["RepositoryURL"] = repositoryUrl,
            ["ComposeFile"] = composeFile,
            ["Env"] = ParseEnv(env, Tool),
            ["TLSSkipVerify"] = tlsSkipVerify,
        };

        if (!string.IsNullOrWhiteSpace(reference)) body["RepositoryReferenceName"] = reference;

        if (!string.IsNullOrWhiteSpace(gitUsername) || !string.IsNullOrWhiteSpace(gitPassword))
        {
            body["RepositoryAuthentication"] = true;
            body["RepositoryUsername"] = gitUsername ?? string.Empty;
            body["RepositoryPassword"] = gitPassword ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(autoUpdateInterval))
        {
            body["AutoUpdate"] = new JsonObject { ["Interval"] = autoUpdateInterval };
        }

        if (kind == "swarm")
        {
            body["SwarmID"] = await StackResolver.GetSwarmIdAsync(client, envId, Tool, ct);
        }

        var created = await client.SendJsonAsync(
            HttpMethod.Post,
            $"api/stacks/create/{kind}/repository?endpointId={envId}",
            body, Tool, ct) as JsonObject;

        return await FinishAsync(client, gate, resolver, created, envId, waitSeconds, Tool, ct);
    }

    // ---------------------------------------------------------------- update / redeploy

    [McpServerTool(Name = "portainer_update_stack"),
     Description("Replace a file-based stack's compose content and redeploy it. Only works for stacks created from content or upload — for a git-backed stack use portainer_redeploy_stack_from_git instead. Requires Portainer:ReadOnly=false and Portainer:AllowStackDeploy=true.")]
    public static async Task<string> UpdateStack(
        PortainerRegistry registry,
        SafetyGate gate,
        StackResolver resolver,
        [Description("Stack name or numeric stack id.")] string stack,
        [Description("The new full docker-compose YAML content. Omit to keep the current content and only change env or pull behaviour.")] string? composeContent = null,
        [Description("Environment variables as a JSON object. Omit to keep the current ones.")] string? env = null,
        [Description("Pull the latest image for each service before redeploying.")] bool pullImage = false,
        [Description("Remove services that are no longer in the compose file. Swarm stacks only.")] bool prune = false,
        [Description("Narrow the stack search to one environment id.")] int? environmentId = null,
        [Description("Seconds to wait for the stack to settle and report container states.")] int waitSeconds = 0,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "portainer_update_stack";
        gate.EnsureFeature(gate.Options.EnableStacks, Tool, "EnableStacks");
        gate.EnsureStackDeploy(Tool);

        var client = registry.Get(alias);
        var resolved = await resolver.ResolveAsync(client, stack, environmentId, Tool, ct);
        StackResolver.EnsureFileBased(resolved, Tool);

        // Keep the existing content when the caller only wants to change env or pull behaviour.
        var content = composeContent;
        if (string.IsNullOrWhiteSpace(content))
        {
            var file = await client.GetJsonAsync($"api/stacks/{resolved.Id}/file", Tool, ct);
            content = file?["StackFileContent"]?.GetValue<string>() ?? string.Empty;
        }

        var body = new JsonObject
        {
            ["StackFileContent"] = content,
            ["PullImage"] = pullImage,
            ["Prune"] = prune,
            ["Env"] = env is null ? (resolved.Raw["Env"]?.DeepClone() ?? new JsonArray()) : ParseEnv(env, Tool),
        };

        var updated = await client.SendJsonAsync(
            HttpMethod.Put,
            $"api/stacks/{resolved.Id}?endpointId={resolved.EndpointId}",
            body, Tool, ct) as JsonObject;

        return await FinishAsync(client, gate, resolver, updated ?? resolved.Raw, resolved.EndpointId, waitSeconds, Tool, ct);
    }

    [McpServerTool(Name = "portainer_redeploy_stack_from_git"),
     Description("Pull the latest commit from a git-backed stack's repository and redeploy it. This is the update path for git stacks. Requires Portainer:ReadOnly=false and Portainer:AllowStackDeploy=true.")]
    public static async Task<string> RedeployFromGit(
        PortainerRegistry registry,
        SafetyGate gate,
        StackResolver resolver,
        [Description("Stack name or numeric stack id.")] string stack,
        [Description("Pull the latest image for each service as well as the latest commit.")] bool pullImage = true,
        [Description("Remove services no longer present in the compose file. Swarm stacks only.")] bool prune = false,
        [Description("Force a redeploy even when the repository has no new commits.")] bool force = false,
        [Description("Git reference to deploy, e.g. refs/heads/main. Omit to keep the stack's current reference.")] string? reference = null,
        [Description("Environment variables as a JSON object. Omit to keep the current ones.")] string? env = null,
        [Description("Narrow the stack search to one environment id.")] int? environmentId = null,
        [Description("Seconds to wait for the stack to settle and report container states.")] int waitSeconds = 0,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "portainer_redeploy_stack_from_git";
        gate.EnsureFeature(gate.Options.EnableStacks, Tool, "EnableStacks");
        gate.EnsureStackDeploy(Tool);

        var client = registry.Get(alias);
        var resolved = await resolver.ResolveAsync(client, stack, environmentId, Tool, ct);
        StackResolver.EnsureGitBased(resolved, Tool);

        var body = new JsonObject
        {
            ["PullImage"] = pullImage,
            ["Prune"] = prune,
            ["RepullImageAndRedeploy"] = force,
        };

        if (!string.IsNullOrWhiteSpace(reference)) body["RepositoryReferenceName"] = reference;
        if (env is not null) body["Env"] = ParseEnv(env, Tool);

        var updated = await client.SendJsonAsync(
            HttpMethod.Put,
            $"api/stacks/{resolved.Id}/git/redeploy?endpointId={resolved.EndpointId}",
            body, Tool, ct) as JsonObject;

        return await FinishAsync(client, gate, resolver, updated ?? resolved.Raw, resolved.EndpointId, waitSeconds, Tool, ct);
    }

    [McpServerTool(Name = "portainer_update_stack_git_config"),
     Description("Change a git-backed stack's repository settings: branch/reference, credentials, or the automatic update polling interval. Does not redeploy on its own. Requires Portainer:ReadOnly=false and Portainer:AllowStackDeploy=true.")]
    public static async Task<string> UpdateGitConfig(
        PortainerRegistry registry,
        SafetyGate gate,
        StackResolver resolver,
        [Description("Stack name or numeric stack id.")] string stack,
        [Description("New git reference, e.g. refs/heads/release. Omit to keep the current one.")] string? reference = null,
        [Description("Polling interval for automatic updates, e.g. \"5m\". Pass an empty string to turn polling off.")] string? autoUpdateInterval = null,
        [Description("Pull the latest image on each automatic update.")] bool autoUpdateForcePull = false,
        [Description("Username for a private repository.")] string? gitUsername = null,
        [Description("Password or personal access token for a private repository.")] string? gitPassword = null,
        [Description("Narrow the stack search to one environment id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "portainer_update_stack_git_config";
        gate.EnsureFeature(gate.Options.EnableStacks, Tool, "EnableStacks");
        gate.EnsureStackDeploy(Tool);

        var client = registry.Get(alias);
        var resolved = await resolver.ResolveAsync(client, stack, environmentId, Tool, ct);
        StackResolver.EnsureGitBased(resolved, Tool);

        var body = new JsonObject();
        if (!string.IsNullOrWhiteSpace(reference)) body["RepositoryReferenceName"] = reference;

        if (autoUpdateInterval is not null)
        {
            body["AutoUpdate"] = string.IsNullOrWhiteSpace(autoUpdateInterval)
                ? null
                : new JsonObject
                {
                    ["Interval"] = autoUpdateInterval,
                    ["ForcePullImage"] = autoUpdateForcePull,
                };
        }

        if (!string.IsNullOrWhiteSpace(gitUsername) || !string.IsNullOrWhiteSpace(gitPassword))
        {
            body["RepositoryAuthentication"] = true;
            body["RepositoryUsername"] = gitUsername ?? string.Empty;
            body["RepositoryPassword"] = gitPassword ?? string.Empty;
        }

        if (body.Count == 0)
        {
            throw new McpException(
                $"MCP tool '{Tool}' was called with nothing to change. Pass reference, autoUpdateInterval, " +
                "or git credentials.");
        }

        // POST, not PUT. Portainer's prose documentation says PUT here and is wrong.
        var updated = await client.SendJsonAsync(
            HttpMethod.Post,
            $"api/stacks/{resolved.Id}/git?endpointId={resolved.EndpointId}",
            body, Tool, ct) as JsonObject;

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            stack = StackTools.Summarize(updated is null ? resolved : StackResolver.FromJson(updated)),
            gitConfigUpdated = true,
            note = "Settings changed. Call portainer_redeploy_stack_from_git to apply them now.",
        });
    }

    [McpServerTool(Name = "portainer_set_stack_env"),
     Description("Set or remove environment variables on a stack and redeploy it. Reads the current variables, merges your changes, and writes them back — so you can change one value without restating the rest. Requires Portainer:ReadOnly=false and Portainer:AllowStackDeploy=true.")]
    public static async Task<string> SetStackEnv(
        PortainerRegistry registry,
        SafetyGate gate,
        StackResolver resolver,
        Redaction redaction,
        [Description("Stack name or numeric stack id.")] string stack,
        [Description("Variables to set or add, as a JSON object, e.g. {\"LOG_LEVEL\":\"debug\"}.")] string? set = null,
        [Description("Variable names to remove, comma-separated.")] string? remove = null,
        [Description("Pull the latest image for each service while redeploying.")] bool pullImage = false,
        [Description("Narrow the stack search to one environment id.")] int? environmentId = null,
        [Description("Seconds to wait for the stack to settle and report container states.")] int waitSeconds = 0,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "portainer_set_stack_env";
        gate.EnsureFeature(gate.Options.EnableStacks, Tool, "EnableStacks");
        gate.EnsureStackDeploy(Tool);

        if (string.IsNullOrWhiteSpace(set) && string.IsNullOrWhiteSpace(remove))
        {
            throw new McpException($"MCP tool '{Tool}' needs something to do: pass set, remove, or both.");
        }

        var client = registry.Get(alias);
        var resolved = await resolver.ResolveAsync(client, stack, environmentId, Tool, ct);

        // Start from the live values so untouched variables survive the round-trip.
        var current = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in (resolved.Raw["Env"] as JsonArray) ?? new JsonArray())
        {
            var key = pair?["Name"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(key)) current[key] = pair?["Value"]?.ToString();
        }

        var added = new List<string>();
        var changed = new List<string>();
        foreach (var (key, value) in ParseEnvDictionary(set, Tool))
        {
            if (current.ContainsKey(key)) changed.Add(key); else added.Add(key);
            current[key] = value;
        }

        var removed = new List<string>();
        foreach (var key in (remove ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.Remove(key)) removed.Add(key);
        }

        var envArray = new JsonArray();
        foreach (var (key, value) in current)
        {
            envArray.Add(new JsonObject { ["Name"] = key, ["Value"] = value });
        }

        // A git stack keeps its content in git; sending StackFileContent would be meaningless,
        // so route env-only changes for those through the redeploy endpoint.
        JsonObject? updated;
        if (resolved.IsGitStack)
        {
            updated = await client.SendJsonAsync(
                HttpMethod.Put,
                $"api/stacks/{resolved.Id}/git/redeploy?endpointId={resolved.EndpointId}",
                new JsonObject { ["Env"] = envArray, ["PullImage"] = pullImage, ["Prune"] = false },
                Tool, ct) as JsonObject;
        }
        else
        {
            var file = await client.GetJsonAsync($"api/stacks/{resolved.Id}/file", Tool, ct);
            updated = await client.SendJsonAsync(
                HttpMethod.Put,
                $"api/stacks/{resolved.Id}?endpointId={resolved.EndpointId}",
                new JsonObject
                {
                    ["StackFileContent"] = file?["StackFileContent"]?.GetValue<string>() ?? string.Empty,
                    ["Env"] = envArray,
                    ["PullImage"] = pullImage,
                    ["Prune"] = false,
                },
                Tool, ct) as JsonObject;
        }

        var settle = await SettleAsync(client, gate, resolver, resolved.Id, resolved.EndpointId, waitSeconds, Tool, ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            stack = StackTools.Summarize(updated is null ? resolved : StackResolver.FromJson(updated)),
            added,
            changed,
            removed,
            env = redaction.RedactPairs(envArray),
            settle,
        });
    }

    // ---------------------------------------------------------------- lifecycle

    [McpServerTool(Name = "portainer_start_stack"),
     Description("Start a stopped stack. Requires Portainer:ReadOnly=false and Portainer:AllowStackLifecycle=true.")]
    public static Task<string> StartStack(
        PortainerRegistry registry,
        SafetyGate gate,
        StackResolver resolver,
        [Description("Stack name or numeric stack id.")] string stack,
        [Description("Narrow the stack search to one environment id.")] int? environmentId = null,
        [Description("Seconds to wait for the stack to settle and report container states.")] int waitSeconds = 0,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
        => LifecycleAsync(registry, gate, resolver, stack, environmentId, waitSeconds, alias, "start", "portainer_start_stack", ct);

    [McpServerTool(Name = "portainer_stop_stack"),
     Description("Stop a running stack. Its containers are stopped but not removed, and its volumes are untouched. Requires Portainer:ReadOnly=false and Portainer:AllowStackLifecycle=true.")]
    public static Task<string> StopStack(
        PortainerRegistry registry,
        SafetyGate gate,
        StackResolver resolver,
        [Description("Stack name or numeric stack id.")] string stack,
        [Description("Narrow the stack search to one environment id.")] int? environmentId = null,
        [Description("Seconds to wait for the stack to settle and report container states.")] int waitSeconds = 0,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
        => LifecycleAsync(registry, gate, resolver, stack, environmentId, waitSeconds, alias, "stop", "portainer_stop_stack", ct);

    private static async Task<string> LifecycleAsync(
        PortainerRegistry registry, SafetyGate gate, StackResolver resolver,
        string stack, int? environmentId, int waitSeconds, string? alias,
        string action, string tool, CancellationToken ct)
    {
        gate.EnsureFeature(gate.Options.EnableStacks, tool, "EnableStacks");
        gate.EnsureStackLifecycle(tool);

        var client = registry.Get(alias);
        var resolved = await resolver.ResolveAsync(client, stack, environmentId, tool, ct);

        var result = await client.SendJsonAsync(
            HttpMethod.Post,
            $"api/stacks/{resolved.Id}/{action}?endpointId={resolved.EndpointId}",
            null, tool, ct) as JsonObject;

        return await FinishAsync(client, gate, resolver, result ?? resolved.Raw, resolved.EndpointId, waitSeconds, tool, ct);
    }

    [McpServerTool(Name = "portainer_delete_stack"),
     Description("Delete a stack and remove its containers. Requires Portainer:ReadOnly=false and Portainer:AllowStackDelete=true. Removing named volumes as well additionally requires Portainer:AllowVolumeManagement=true, because that destroys data.")]
    public static async Task<string> DeleteStack(
        PortainerRegistry registry,
        SafetyGate gate,
        StackResolver resolver,
        [Description("Stack name or numeric stack id.")] string stack,
        [Description("Also delete the stack's named volumes and its containers' anonymous volumes. Destroys data. Docker standalone stacks only.")] bool removeVolumes = false,
        [Description("Set true only when deleting an external Swarm stack that Portainer did not create.")] bool external = false,
        [Description("Narrow the stack search to one environment id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "portainer_delete_stack";
        gate.EnsureFeature(gate.Options.EnableStacks, Tool, "EnableStacks");
        gate.EnsureStackDelete(Tool);

        if (removeVolumes)
        {
            gate.EnsureVolumeManagement(Tool);
        }

        var client = registry.Get(alias);
        var resolved = await resolver.ResolveAsync(client, stack, environmentId, Tool, ct);

        var path = ToolHelpers.WithQuery(
            $"api/stacks/{resolved.Id}",
            ("endpointId", resolved.EndpointId.ToString()),
            ("removeVolumes", removeVolumes ? "true" : null),
            ("external", external ? "true" : null));

        await client.SendJsonAsync(HttpMethod.Delete, path, null, Tool, ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            deleted = true,
            stackId = resolved.Id,
            stackName = resolved.Name,
            environmentId = resolved.EndpointId,
            volumesRemoved = removeVolumes,
        });
    }

    [McpServerTool(Name = "portainer_migrate_stack"),
     Description("Move a stack to a different environment. Requires Portainer:ReadOnly=false and Portainer:AllowStackDeploy=true.")]
    public static async Task<string> MigrateStack(
        PortainerRegistry registry,
        SafetyGate gate,
        StackResolver resolver,
        [Description("Stack name or numeric stack id.")] string stack,
        [Description("Environment (endpoint) id to move the stack to.")] int targetEnvironmentId,
        [Description("Rename the stack as part of the move. Omit to keep the current name.")] string? newName = null,
        [Description("Narrow the stack search to one environment id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "portainer_migrate_stack";
        gate.EnsureFeature(gate.Options.EnableStacks, Tool, "EnableStacks");
        gate.EnsureStackDeploy(Tool);
        gate.EnsureEnvironmentAllowed(targetEnvironmentId);

        var client = registry.Get(alias);
        var resolved = await resolver.ResolveAsync(client, stack, environmentId, Tool, ct);

        var body = new JsonObject { ["EndpointID"] = targetEnvironmentId };
        if (!string.IsNullOrWhiteSpace(newName))
        {
            gate.EnsureStackAllowed(newName);
            body["Name"] = newName;
        }

        if (resolved.Type == 1)
        {
            body["SwarmID"] = await StackResolver.GetSwarmIdAsync(client, targetEnvironmentId, Tool, ct);
        }

        var migrated = await client.SendJsonAsync(
            HttpMethod.Post,
            $"api/stacks/{resolved.Id}/migrate?endpointId={resolved.EndpointId}",
            body, Tool, ct) as JsonObject;

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            migrated = true,
            from = resolved.EndpointId,
            to = targetEnvironmentId,
            stack = StackTools.Summarize(migrated is null ? resolved : StackResolver.FromJson(migrated)),
        });
    }

    [McpServerTool(Name = "portainer_trigger_stack_webhook"),
     Description("Fire a stack's redeploy webhook by its UUID. This is the same call an external CI system would make, and it needs no Portainer credential. Requires Portainer:ReadOnly=false and Portainer:AllowWebhookTrigger=true.")]
    public static async Task<string> TriggerWebhook(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("The webhook UUID, as shown by portainer_get_stack.")] string webhookId,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "portainer_trigger_stack_webhook";
        gate.EnsureFeature(gate.Options.EnableStacks, Tool, "EnableStacks");
        gate.EnsureWebhookTrigger(Tool);

        var client = registry.Get(alias);
        await client.SendJsonAsync(
            HttpMethod.Post, $"api/stacks/webhooks/{Uri.EscapeDataString(webhookId)}", null, Tool, ct);

        return ToolHelpers.Json(gate, new { instance = client.Alias, webhookId, triggered = true });
    }

    // ---------------------------------------------------------------- verification

    /// <summary>
    /// Shape the result of a mutation, optionally waiting for the stack to settle first.
    /// </summary>
    private static async Task<string> FinishAsync(
        PortainerClient client, SafetyGate gate, StackResolver resolver,
        JsonObject? stackNode, int environmentId, int waitSeconds, string tool, CancellationToken ct)
    {
        var stack = stackNode is null ? null : StackResolver.FromJson(stackNode);
        var settle = stack is null
            ? null
            : await SettleAsync(client, gate, resolver, stack.Id, environmentId, waitSeconds, tool, ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            accepted = true,
            stack = stack is null ? null : StackTools.Summarize(stack),
            settle,
            note = waitSeconds <= 0
                ? "Portainer accepted the request. That means 'queued', not 'running' — image pulls and container "
                + "starts happen afterwards. Pass waitSeconds to have this tool confirm the containers came up, or "
                + "call portainer_get_stack / docker_list_containers yourself."
                : null,
        });
    }

    /// <summary>
    /// Poll until the stack stops reporting "deploying", then report what its containers are
    /// actually doing.
    ///
    /// This exists because a 2xx from Portainer only means the deploy was accepted. The most
    /// common failure an agent hits is "it returned 200, why is the site down" — the answer is
    /// almost always a failed image pull or a container in a restart loop, both visible here.
    /// </summary>
    private static async Task<object?> SettleAsync(
        PortainerClient client, SafetyGate gate, StackResolver resolver,
        int stackId, int environmentId, int waitSeconds, string tool, CancellationToken ct)
    {
        if (waitSeconds <= 0) return null;

        var budget = gate.ClampWaitSeconds(waitSeconds);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(budget);
        var poll = Math.Max(250, gate.Options.PollIntervalMs);

        StackRef? stack = null;
        var timedOut = true;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var node = await client.GetJsonAsync($"api/stacks/{stackId}", tool, ct) as JsonObject;
            if (node is null) break;

            stack = StackResolver.FromJson(node);
            if (stack.Status != 3) // 3 = deploying
            {
                timedOut = false;
                break;
            }

            await Task.Delay(poll, ct);
        }

        var containers = stack is null
            ? new List<object>()
            : await DescribeStackContainersAsync(client, stack, environmentId, tool, ct);

        var unhealthy = containers
            .OfType<ContainerState>()
            .Where(c => !string.Equals(c.state, "running", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new
        {
            waitedSeconds = budget,
            timedOut,
            status = stack?.StatusName,
            containers,
            healthy = unhealthy.Count == 0 && containers.Count > 0,
            problems = unhealthy.Count == 0
                ? null
                : unhealthy.Select(c => $"{c.name} is {c.state} ({c.status})").ToList(),
            hint = unhealthy.Count == 0
                ? null
                : "Some containers are not running. Fetch their logs with docker_get_container_logs to see why "
                + "— a failed image pull or a crash loop are the usual causes.",
        };
    }

    private sealed record ContainerState(string? name, string? service, string? state, string? status, string? image);

    /// <summary>
    /// Find the containers belonging to a stack. Compose labels them with the project name;
    /// Swarm labels them with the stack namespace. Both are tried so the caller does not need
    /// to know which kind of stack it just deployed.
    /// </summary>
    private static async Task<List<object>> DescribeStackContainersAsync(
        PortainerClient client, StackRef stack, int environmentId, string tool, CancellationToken ct)
    {
        var label = stack.Type == 1
            ? $"com.docker.stack.namespace={stack.Name}"
            : $"com.docker.compose.project={stack.Name}";

        var filters = Uri.EscapeDataString($"{{\"label\":[\"{label}\"]}}");

        try
        {
            var node = await client.DockerGetJsonAsync(
                environmentId, $"containers/json?all=true&filters={filters}", tool, ct);

            return (node as JsonArray)?
                .Select(c => (object)new ContainerState(
                    ToolHelpers.PrimaryName(c),
                    ToolHelpers.ComposeService(c),
                    c?["State"]?.GetValue<string>(),
                    c?["Status"]?.GetValue<string>(),
                    c?["Image"]?.GetValue<string>()))
                .ToList() ?? new List<object>();
        }
        catch (McpException)
        {
            // A Kubernetes environment has no Docker proxy; the stack status alone is the answer.
            return new List<object>();
        }
    }

    // ---------------------------------------------------------------- input parsing

    private static string ParsePlatform(string platform, string tool) => platform?.ToLowerInvariant() switch
    {
        null or "" or "standalone" or "compose" or "docker" => "standalone",
        "swarm" => "swarm",
        _ => throw new McpException(
            $"MCP tool '{tool}' does not understand platform '{platform}'. Use \"standalone\" or \"swarm\". " +
            "Kubernetes stacks are not supported by this server."),
    };

    /// <summary>
    /// Accept environment variables as the shape an agent naturally writes — a JSON object —
    /// and convert to Portainer's <c>[{Name, Value}]</c>. A Portainer-shaped array is also
    /// accepted so a value round-tripped out of portainer_get_stack can be passed straight back.
    /// </summary>
    private static JsonArray ParseEnv(string? json, string tool)
    {
        var result = new JsonArray();
        foreach (var (key, value) in ParseEnvDictionary(json, tool))
        {
            result.Add(new JsonObject { ["Name"] = key, ["Value"] = value });
        }
        return result;
    }

    private static Dictionary<string, string?> ParseEnvDictionary(string? json, string tool)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return result;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new McpException(
                $"MCP tool '{tool}' could not parse the environment variables as JSON: {ex.Message} " +
                "Expected an object such as {\"TZ\":\"UTC\",\"LOG_LEVEL\":\"debug\"}.");
        }

        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj) result[key] = value?.ToString();
                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    var name = item?["Name"]?.GetValue<string>() ?? item?["name"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(name)) result[name] = item?["Value"]?.ToString() ?? item?["value"]?.ToString();
                }
                break;

            default:
                throw new McpException(
                    $"MCP tool '{tool}' expected environment variables as a JSON object or a " +
                    "[{\"Name\":…,\"Value\":…}] array.");
        }

        return result;
    }
}
