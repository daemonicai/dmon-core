using System.Text.RegularExpressions;
using Dmon.Abstractions.Providers;

namespace Dmon.Core.Providers;

public sealed class ProviderConfigLoader
{
    // Shown in every schema-validation error so the fix is obvious from the message alone.
    private const string SchemaHint =
        """
        Expected map form:

          providers:
            anthropic:
              adapter: anthropic
              defaultModelId: claude-sonnet-4-6
              auth:
                type: envVar
                envVar: ANTHROPIC_API_KEY
        """;

    private static readonly Regex NumericKey = new(@"^\d+$", RegexOptions.Compiled);

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

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    $"A provider entry has an empty or whitespace name. Each provider must be keyed by a non-empty name. {SchemaHint}");
            }

            if (NumericKey.IsMatch(name))
            {
                throw new InvalidOperationException(
                    $"Provider key '{name}' is numeric. The 'providers' block must be a YAML map " +
                    $"keyed by provider name, not a sequence. {SchemaHint}");
            }

            string? adapter = providerSection["adapter"];

            if (string.IsNullOrWhiteSpace(adapter))
            {
                throw new InvalidOperationException(
                    $"Provider '{name}' is missing required 'adapter' field. {SchemaHint}");
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
