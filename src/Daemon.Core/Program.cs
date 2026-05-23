using Daemon.Core;
using Daemon.Core.Rpc;
using Microsoft.Extensions.Logging;

// Logs to stderr; stdout is the JSONL RPC channel.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// IChatClient pipeline (assembled by AddDaemonCore):
//   1. PermissionGateChatClient   ← evaluate policy, prompt/deny
//   2. FunctionInvokingChatClient ← M.E.AI dispatch loop
//   3. actual provider client
builder.Services
    .AddDaemonProviders()
    .AddDaemonAuth()
    .AddDaemonExtensions()
    .AddDaemonCore();

builder.Services.AddHostedService<RpcHostedService>();

IHost host = builder.Build();
host.Run();