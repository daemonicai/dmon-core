namespace Dmon.Core.Session;

public interface ISessionDirectoryResolver
{
    string Resolve(string workingDirectory);
}

public sealed class SessionDirectoryResolver : ISessionDirectoryResolver
{
    private readonly IConfiguration _configuration;

    private const string DmonDir = ".dmon";
    private const string ConfigFile = "config.yaml";
    private const string SessionsSubdir = "sessions";
    private const string GlobalRoot = "~/.dmon/sessions";

    public SessionDirectoryResolver(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string Resolve(string workingDirectory)
    {
        string? dmonRoot = FindDmonRoot(workingDirectory);

        if (dmonRoot is null)
        {
            return ExpandPath(GlobalRoot);
        }

        string sessionStore = _configuration.GetValue("sessionStore", "local")!;

        return sessionStore switch
        {
            "global" => ExpandPath(GlobalRoot),
            "local" or "" => Path.Combine(dmonRoot, DmonDir, SessionsSubdir),
            _ => ExpandPath(sessionStore)
        };
    }

    private static string? FindDmonRoot(string start)
    {
        string? current = Path.GetFullPath(start);

        while (current is not null)
        {
            string candidate = Path.Combine(current, DmonDir, ConfigFile);

            if (File.Exists(candidate))
            {
                return current;
            }

            string? parent = Path.GetDirectoryName(current);

            if (parent == current)
            {
                break;
            }

            current = parent;
        }

        return null;
    }

    private static string ExpandPath(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path[2..]);
        }

        return path;
    }
}
