using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using PortainerMCPSharp.Configuration;

namespace PortainerMCPSharp.Services;

/// <summary>
/// Maps an alias to a <see cref="PortainerClient"/>. One Portainer already federates many
/// Docker environments, so most deployments configure a single instance and never pass an
/// alias; the registry exists for estates that run separate Portainers (prod vs dev, or one
/// per site).
/// </summary>
public sealed class PortainerRegistry : IDisposable
{
    private readonly PortainerOptions _options;
    private readonly Dictionary<string, PortainerInstanceEntry> _byAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<PortainerClient>> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _defaultAlias;
    private bool _disposed;

    public PortainerRegistry(IOptions<PortainerOptions> options)
    {
        _options = options.Value;

        var index = 0;
        foreach (var entry in _options.Instances)
        {
            index++;
            var alias = string.IsNullOrWhiteSpace(entry.Alias) ? $"portainer-{index}" : entry.Alias.Trim();
            while (_byAlias.ContainsKey(alias)) alias = $"portainer-{++index}";
            entry.Alias = alias;
            _byAlias[alias] = entry;
        }

        var preferred = _options.DefaultAlias;
        _defaultAlias = !string.IsNullOrWhiteSpace(preferred) && _byAlias.ContainsKey(preferred)
            ? _byAlias.Keys.First(k => string.Equals(k, preferred, StringComparison.OrdinalIgnoreCase))
            : _byAlias.Keys.FirstOrDefault();
    }

    public IReadOnlyDictionary<string, PortainerInstanceEntry> Instances => _byAlias;
    public string? DefaultAlias => _defaultAlias;
    public bool HasInstances => _byAlias.Count > 0;

    public PortainerInstanceEntry ResolveAlias(string? alias)
    {
        if (_byAlias.Count == 0)
        {
            throw new McpException(
                "No Portainer instances are configured. Add at least one entry under " +
                "Portainer:Instances with a BaseUrl and an ApiKey.");
        }

        if (!string.IsNullOrWhiteSpace(alias))
        {
            if (_byAlias.TryGetValue(alias, out var direct)) return direct;
            throw new McpException(
                $"Unknown Portainer alias '{alias}'. Available: {string.Join(", ", _byAlias.Keys)}.");
        }

        return _byAlias[_defaultAlias!];
    }

    /// <summary>Get (creating on first use) the client for an alias.</summary>
    public PortainerClient Get(string? alias)
    {
        var entry = ResolveAlias(alias);
        var lazy = _clients.GetOrAdd(
            entry.Alias,
            _ => new Lazy<PortainerClient>(() => new PortainerClient(entry, _options), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var lazy in _clients.Values)
        {
            if (lazy.IsValueCreated) lazy.Value.Dispose();
        }
    }
}
