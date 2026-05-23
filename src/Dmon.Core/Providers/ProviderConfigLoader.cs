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

            IConfigurationSection capsSection = providerSection.GetSection("capabilities");
            ProviderCapabilities capabilities = new()
            {
                ToolCalling = bool.TryParse(capsSection["toolCalling"], out bool tc) && tc,
                Reasoning = bool.TryParse(capsSection["reasoning"], out bool r) && r,
                ContextWindow = int.TryParse(capsSection["contextWindow"], out int cw) ? cw : 0,
                MaxTokens = int.TryParse(capsSection["maxTokens"], out int mt) ? mt : 0
            };

            result.Add(new ProviderConfig
            {
                Name = name,
                Adapter = adapter,
                BaseUrl = providerSection["baseUrl"],
                DefaultModelId = providerSection["defaultModelId"],
                Auth = auth,
                Capabilities = capabilities
            });
        }

        return result;
    }
}
