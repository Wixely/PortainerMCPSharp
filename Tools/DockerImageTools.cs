using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using PortainerMCPSharp.Services;

namespace PortainerMCPSharp.Tools;

/// <summary>Images on an environment, through the Docker proxy.</summary>
[McpServerToolType]
public static class DockerImageTools
{
    private const string Feature = "EnableImages";

    [McpServerTool(Name = "docker_list_images"),
     Description("List images present on an environment, with their tags, size and age.")]
    public static async Task<string> ListImages(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Include intermediate layers, not just tagged images.")] bool all = false,
        [Description("Only images whose repository or tag contains this text.")] string? nameContains = null,
        [Description("Maximum images to return.")] int? limit = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_list_images";
        gate.EnsureFeature(gate.Options.EnableImages, Tool, Feature);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        var node = await client.DockerGetJsonAsync(
            envId, $"images/json?all={ToolHelpers.Bool(all)}", Tool, ct);

        var items = (node as JsonArray)?.OfType<JsonNode>().ToList() ?? new List<JsonNode>();

        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            items = items.Where(i =>
                (i["RepoTags"] as JsonArray)?.Any(t =>
                    (t?.GetValue<string>() ?? string.Empty).Contains(nameContains, StringComparison.OrdinalIgnoreCase)) == true)
                .ToList();
        }

        var max = gate.ClampLimit(limit);
        var page = items.Take(max).Select(i => new
        {
            id = Short(i["Id"]?.GetValue<string>()),
            tags = (i["RepoTags"] as JsonArray)?.Select(t => t?.GetValue<string>()).ToList(),
            sizeBytes = i["Size"]?.GetValue<long>(),
            created = i["Created"]?.GetValue<long>(),
            containers = i["Containers"]?.GetValue<int>(),
        }).ToList();

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            images = page,
            count = page.Count,
            total = items.Count,
            truncated = items.Count > page.Count,
        });
    }

    [McpServerTool(Name = "docker_inspect_image"),
     Description("Get an image's full detail: layers, entrypoint, exposed ports, labels and build config.")]
    public static async Task<string> InspectImage(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Image name with tag, or image id.")] string image,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_inspect_image";
        gate.EnsureFeature(gate.Options.EnableImages, Tool, Feature);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);
        var node = await client.DockerGetJsonAsync(
            envId, $"images/{Uri.EscapeDataString(image)}/json", Tool, ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            image,
            inspect = gate.CapJson(node),
        });
    }

    [McpServerTool(Name = "docker_pull_image"),
     Description("Pull an image onto an environment. Requires Portainer:ReadOnly=false and Portainer:AllowImagePull=true. The pull progress stream is consumed and discarded; only the outcome is returned.")]
    public static async Task<string> PullImage(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Image to pull, e.g. nginx or ghcr.io/acme/api.")] string image,
        [Description("Tag to pull. Defaults to latest.")] string tag = "latest",
        [Description("Portainer registry id for a private registry, from portainer_list_registries. Omit for public images.")] int? registryId = null,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_pull_image";
        gate.EnsureFeature(gate.Options.EnableImages, Tool, Feature);
        gate.EnsureImagePull(Tool);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        var path = PortainerClient.DockerPath(
            envId,
            $"images/create?fromImage={Uri.EscapeDataString(image)}&tag={Uri.EscapeDataString(tag)}");

        using var response = await client.SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, path);
                if (registryId is not null)
                {
                    // Portainer resolves the stored credential from the registry id for us.
                    var header = Convert.ToBase64String(
                        Encoding.UTF8.GetBytes($"{{\"registryId\":{registryId.Value}}}"));
                    request.Headers.Add("X-Registry-Auth", header);
                }
                return request;
            },
            HttpCompletionOption.ResponseContentRead, ct);

        await client.EnsureSuccessAsync(response, Tool, ct);

        // The body is a newline-delimited progress stream. Streaming layer-by-layer percentages
        // into an agent's context is pure noise, so only the tail is inspected for an error.
        var body = await response.Content.ReadAsStringAsync(ct);
        var error = FindPullError(body);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            image = $"{image}:{tag}",
            pulled = error is null,
            error,
        });
    }

    [McpServerTool(Name = "docker_remove_image"),
     Description("Remove an image from an environment. Requires Portainer:ReadOnly=false and Portainer:AllowImageRemoval=true.")]
    public static async Task<string> RemoveImage(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Image name with tag, or image id.")] string image,
        [Description("Remove even if containers reference it or it has multiple tags.")] bool force = false,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_remove_image";
        gate.EnsureFeature(gate.Options.EnableImages, Tool, Feature);
        gate.EnsureImageRemoval(Tool);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        var node = await client.DockerSendJsonAsync(
            HttpMethod.Delete, envId,
            $"images/{Uri.EscapeDataString(image)}?force={ToolHelpers.Bool(force)}",
            null, Tool, ct);

        return ToolHelpers.Json(gate, new { instance = client.Alias, environmentId = envId, image, removed = true, detail = node });
    }

    [McpServerTool(Name = "docker_prune_images"),
     Description("Delete unused images on an environment. Broad and irreversible. Requires Portainer:ReadOnly=false and Portainer:AllowPrune=true.")]
    public static async Task<string> PruneImages(
        PortainerRegistry registry,
        SafetyGate gate,
        [Description("Remove all unused images, not just dangling untagged ones. Much more aggressive.")] bool all = false,
        [Description("Environment (endpoint) id.")] int? environmentId = null,
        [Description("Portainer instance alias. Omit to use the default instance.")] string? alias = null,
        CancellationToken ct = default)
    {
        const string Tool = "docker_prune_images";
        gate.EnsureFeature(gate.Options.EnableImages, Tool, Feature);
        gate.EnsurePrune(Tool);

        var client = registry.Get(alias);
        var envId = gate.ResolveEnvironmentId(environmentId, Tool);

        var filters = all ? Uri.EscapeDataString("{\"dangling\":[\"false\"]}") : null;
        var path = ToolHelpers.WithQuery("images/prune", ("filters", filters));

        var node = await client.DockerSendJsonAsync(HttpMethod.Post, envId, path, null, Tool, ct);

        return ToolHelpers.Json(gate, new
        {
            instance = client.Alias,
            environmentId = envId,
            pruned = true,
            all,
            spaceReclaimed = node?["SpaceReclaimed"]?.GetValue<long>(),
            deleted = (node?["ImagesDeleted"] as JsonArray)?.Count ?? 0,
        });
    }

    private static string? Short(string? id) =>
        string.IsNullOrEmpty(id) ? id : id.Replace("sha256:", string.Empty)[..Math.Min(12, id.Replace("sha256:", string.Empty).Length)];

    /// <summary>Docker reports pull failures as an {"error": …} object inside the progress stream.</summary>
    private static string? FindPullError(string body)
    {
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("\"error", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var node = JsonNode.Parse(line);
                var error = node?["error"]?.GetValue<string>()
                            ?? node?["errorDetail"]?["message"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(error)) return error;
            }
            catch (System.Text.Json.JsonException) { /* progress lines are not all JSON */ }
        }
        return null;
    }
}
