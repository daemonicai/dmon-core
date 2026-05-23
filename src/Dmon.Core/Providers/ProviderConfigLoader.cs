namespace Dmon.Core.Providers;

public sealed class ProviderConfigLoader
{
    private readonly IConfiguration _configuration;

    public ProviderConfigLoader(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public IReadOnlyList<ProviderConfig> Load()
    {
        IConfigurationSection providersSection = _configuration.GetSection("providers");
        List<ProviderConfig> result = [];

        foreach (IConfigurationSection providerSection in providersSection.GetChildren())
        {
            string name = providerSection.Key;
            string? adapter = providerSection["adapter"];

            if (string.IsNullOrWhiteSpace(adapter))
            {
                throw new InvalidOperationException($"Provider '{name}' is missing required 'adapter' field.");
            }

            IConfigurationSection authSection = providerSection.GetSection("auth");
            ProviderAuthConfig auth = new()
            {
                Type = authSection["type"] ?? "none",
                EnvVar = authSection["envVar"]
            };

            result.Add(new ProviderConfig
            {
                Name = name,
                Adapter = adapter,
                BaseUrl = providerSection["baseUrl"],
                DefaultModelId = providerSection["defaultModelId"],
                Auth = auth
            });
        }

        return result;
    }
}
