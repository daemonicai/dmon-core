using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Extensions;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, List<AIFunction>> _extensions = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string extensionName, IEnumerable<AIFunction> tools)
    {
        List<AIFunction> list = tools.ToList();

        _extensions.AddOrUpdate(
            extensionName,
            _ => list,
            (_, _) => list);
    }

    public void Unregister(string extensionName)
    {
        _extensions.TryRemove(extensionName, out _);
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
    }
}
