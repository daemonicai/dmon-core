using System.Collections.Concurrent;
using System.Threading.Channels;
using Daemonic.Dmail.Data;
using Daemonic.Dmail.Models;

namespace Daemonic.Dmail.Services;

public sealed class ImapWatcherManager : BackgroundService
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly AccountService _accounts;
    private readonly OAuth2Service _oauth;
    private readonly Channel<Email> _channel;
    private readonly IConfiguration _config;
    private readonly IServiceProvider _services;
    private readonly ILogger<ImapWatcherManager> _logger;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _watchers = new();
    private readonly ConcurrentDictionary<string, Task> _watcherTasks = new();

    /// <summary>Number of currently active IMAP watchers (task 10.1).</summary>
    public int ActiveWatcherCount => _watchers.Count;

    public ImapWatcherManager(
        ISqliteConnectionFactory connectionFactory,
        AccountService accounts,
        OAuth2Service oauth,
        Channel<Email> channel,
        IConfiguration config,
        IServiceProvider services,
        ILogger<ImapWatcherManager> logger)
    {
        _connectionFactory = connectionFactory;
        _accounts = accounts;
        _oauth = oauth;
        _channel = channel;
        _config = config;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IMAP watcher manager started");

        // Start watchers for all connected accounts
        await RefreshWatchersAsync(stoppingToken);

        // Poll every 30 seconds for new/removed accounts
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            await RefreshWatchersAsync(stoppingToken);
        }

        // Stop all watchers on shutdown
        foreach (var (email, cts) in _watchers)
        {
            cts.Cancel();
        }
    }

    public async Task StartWatcherForAccountAsync(string email)
    {
        if (_watchers.ContainsKey(email))
        {
            _logger.LogInformation("Watcher already running for {Email}", email);
            return;
        }

        var cts = new CancellationTokenSource();
        _watchers[email] = cts;

        var backfillMonths = _config.GetValue<int>("DMAIL_BACKFILL_MONTHS", 1);
        var logger = _services.GetRequiredService<ILogger<ImapIdleWatcher>>();

        var watcher = new ImapIdleWatcher(
            email, _connectionFactory, _accounts, _oauth, _channel, backfillMonths, logger);

        var task = watcher.StartAsync(cts.Token);
        _watcherTasks[email] = task;

        _logger.LogInformation("Started IMAP watcher for {Email}", email);
    }

    public async Task StopWatcherForAccountAsync(string email)
    {
        if (_watchers.TryRemove(email, out var cts))
        {
            await cts.CancelAsync();
            _watcherTasks.TryRemove(email, out _);
            _logger.LogInformation("Stopped IMAP watcher for {Email}", email);
        }
    }

    private async Task RefreshWatchersAsync(CancellationToken ct)
    {
        var accounts = await GetConnectedAccountsAsync();
        var accountEmails = accounts.ToHashSet();

        // Stop watchers for removed accounts
        foreach (var email in _watchers.Keys.ToList())
        {
            if (!accountEmails.Contains(email))
            {
                await StopWatcherForAccountAsync(email);
            }
        }

        // Start watchers for new accounts
        foreach (var email in accountEmails)
        {
            if (!_watchers.ContainsKey(email))
            {
                await StartWatcherForAccountAsync(email);
            }
        }
    }

    private async Task<List<string>> GetConnectedAccountsAsync()
    {
        var emails = new List<string>();
        await using var connection = await _connectionFactory.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT email FROM accounts WHERE account_state = 'connected'";

        using var reader = await cmd.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync())
        {
            emails.Add(reader.GetString(0));
        }
        return emails;
    }
}
