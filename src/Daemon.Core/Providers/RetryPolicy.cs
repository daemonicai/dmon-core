using Microsoft.Extensions.Configuration;

namespace Daemon.Core.Providers;

public sealed class RetryPolicy
{
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxAttempts { get; init; } = 5;

    public static RetryPolicy FromConfiguration(IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection("Daemon:Provider:Retry");

        int baseDelayMs = section.GetValue<int>("BaseDelayMs", 1000);
        int maxDelayMs = section.GetValue<int>("MaxDelayMs", 30000);
        int maxAttempts = section.GetValue<int>("MaxAttempts", 5);

        return new RetryPolicy
        {
            BaseDelay = TimeSpan.FromMilliseconds(baseDelayMs),
            MaxDelay = TimeSpan.FromMilliseconds(maxDelayMs),
            MaxAttempts = maxAttempts
        };
    }
}
