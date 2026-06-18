using Dmon.Abstractions.Providers;

namespace Dmon.Core.Providers;

public static class ProviderConfigComposer
{
    /// <summary>
    /// Merges config-derived entries with synthesized defaults from wired factories.
    /// Config entries come first (order preserved); a synthesized default is appended
    /// for each factory whose AdapterName is not already represented by a config entry.
    /// Two factories sharing the same AdapterName produce only one synthesized entry (first wins).
    /// </summary>
    public static IReadOnlyList<ProviderConfig> Compose(
        IReadOnlyList<ProviderConfig> fromConfig,
        IEnumerable<IProviderFactory> factories)
    {
        // Build the set of adapters already represented by config entries.
        HashSet<string> coveredAdapters = new(StringComparer.OrdinalIgnoreCase);

        foreach (ProviderConfig config in fromConfig)
        {
            coveredAdapters.Add(config.Adapter);
        }

        List<ProviderConfig> result = new(fromConfig.Count);
        result.AddRange(fromConfig);

        // Track adapters already synthesized to guard against duplicate factories.
        HashSet<string> synthesized = new(StringComparer.OrdinalIgnoreCase);

        foreach (IProviderFactory factory in factories)
        {
            if (coveredAdapters.Contains(factory.AdapterName))
            {
                continue;
            }

            if (!synthesized.Add(factory.AdapterName))
            {
                continue;
            }

            ProviderAuthConfig auth = !string.IsNullOrWhiteSpace(factory.DefaultEnvVar)
                ? new ProviderAuthConfig { Type = "envVar", EnvVar = factory.DefaultEnvVar }
                : new ProviderAuthConfig { Type = "none" };

            result.Add(new ProviderConfig
            {
                Name = factory.AdapterName,
                Adapter = factory.AdapterName,
                DefaultModelId = factory.DefaultModelId,
                Auth = auth,
                BaseUrl = null
            });
        }

        return result;
    }
}
