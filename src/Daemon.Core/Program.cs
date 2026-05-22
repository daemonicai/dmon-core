using Daemon.Core;

// IChatClient pipeline (assembled in Group 9 startup):
//   1. PermissionGateChatClient   ← evaluate policy, prompt/deny
//   2. FunctionInvokingChatClient ← M.E.AI dispatch loop
//   3. actual provider client

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
