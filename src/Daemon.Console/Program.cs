using Daemon.Console;

string? corePath = null;
List<string> argsList = args.ToList();
int corePathIdx = argsList.IndexOf("--core-path");
if (corePathIdx >= 0 && corePathIdx + 1 < argsList.Count)
{
    corePath = argsList[corePathIdx + 1];
}

if (args.Contains("--help") || args.Contains("-h"))
{
    System.Console.WriteLine("daemon — .NET-native coding agent (console host)");
    System.Console.WriteLine("Usage: daemon [--core-path <path>]");
    System.Console.WriteLine("  --core-path   Path to Daemon.Core.dll");
    System.Console.WriteLine();
    System.Console.WriteLine("Slash commands:");
    System.Console.WriteLine("  /new           Create a new session");
    System.Console.WriteLine("  /fork          Fork current session");
    System.Console.WriteLine("  /clone         Clone current session");
    System.Console.WriteLine("  /model         Cycle active model");
    System.Console.WriteLine("  /model <p> <m> Set active model");
    System.Console.WriteLine("  /login <p>     Log in to a provider");
    System.Console.WriteLine("  /logout <p>    Log out of a provider");
    System.Console.WriteLine("  /load <src>    Load an extension");
    System.Console.WriteLine("  /unload <name> Unload an extension");
    System.Console.WriteLine("  /promote <name> Promote a script to NuGet extension");
    System.Console.WriteLine("  /thinking      Cycle thinking level");
    System.Console.WriteLine("  /thinking <l>  Set thinking level (off|low|medium|high)");
    System.Console.WriteLine("  /quit, /exit   Shut down");
    return;
}

var host = new ConsoleHost(corePath);
await host.RunAsync();
