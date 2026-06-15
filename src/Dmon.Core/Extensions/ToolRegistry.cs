using System.Collections.Concurrent;
using Dmon.Abstractions.Extensions;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Extensions;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, List<AIFunction>> _extensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IToolExtension> _extensionsByTool = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string extensionName, IToolExtension extension, IEnumerable<AIFunction> tools)
    {
        List<AIFunction> list = tools.ToList();

        // Remove old tool→extension mappings for this extension name before replacing.
        if (_extensions.TryGetValue(extensionName, out List<AIFunction>? existing))
        {
            foreach (AIFunction fn in existing)
            {
                _extensionsByTool.TryRemove(fn.Name, out _);
            }
        }

        _extensions.AddOrUpdate(
            extensionName,
            _ => list,
            (_, _) => list);

        foreach (AIFunction fn in list)
        {
            _extensionsByTool[fn.Name] = extension;
        }
    }

    public IToolExtension? FindExtension(string toolName)
    {
        _extensionsByTool.TryGetValue(toolName, out IToolExtension? extension);
        return extension;
    }

    public void Unregister(string extensionName)
    {
        if (_extensions.TryRemove(extensionName, out List<AIFunction>? removed))
        {
            foreach (AIFunction fn in removed)
            {
                _extensionsByTool.TryRemove(fn.Name, out _);
            }
        }
    }

    public IReadOnlyList<AIFunction> GetAll()
    {
        List<AIFunction> result = [];

        foreach (List<AIFunction> tools in _extensions.Values)
        {
            result.AddRange(tools);
        }

        return result;
    }

    public IReadOnlyList<RegisteredExtensionSnapshot> GetSnapshot()
    {
        return _extensions
            .Select(kv => new RegisteredExtensionSnapshot(kv.Key, kv.Value.Count))
            .ToList();
    }

    public void Clear()
    {
        _extensions.Clear();
        _extensionsByTool.Clear();
    }
}
