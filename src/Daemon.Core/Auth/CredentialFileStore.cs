using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Daemon.Core.Auth;

public sealed class CredentialFileStore : ICredentialFileStore
{
    private readonly string _credentialsDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public CredentialFileStore(string? credentialsDirectory = null)
    {
        _credentialsDirectory = credentialsDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".daemon", "credentials");
    }

    public ValueTask<CredentialRecord?> ReadAsync(string providerName, CancellationToken cancellationToken = default)
    {
        string filePath = GetFilePath(providerName);

        if (!File.Exists(filePath))
        {
            return ValueTask.FromResult<CredentialRecord?>(null);
        }

        byte[] bytes = File.ReadAllBytes(filePath);
        CredentialRecord? record = JsonSerializer.Deserialize<CredentialRecord>(bytes, JsonOptions);

        return ValueTask.FromResult(record);
    }

    public ValueTask WriteAsync(CredentialRecord record, CancellationToken cancellationToken = default)
    {
        EnsureDirectorySecure();
        string filePath = GetFilePath(record.Provider);

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        File.WriteAllBytes(filePath, bytes);

        SecureFile(filePath);

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string providerName, CancellationToken cancellationToken = default)
    {
        string filePath = GetFilePath(providerName);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        return ValueTask.CompletedTask;
    }

    private string GetFilePath(string providerName)
    {
        return Path.Combine(_credentialsDirectory, $"{SanitiseFileName(providerName)}.json");
    }

    private static string SanitiseFileName(string providerName)
    {
        ReadOnlySpan<char> invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[providerName.Length];
        int j = 0;

        foreach (char c in providerName)
        {
            bool isValid = true;

            foreach (char iv in invalid)
            {
                if (c == iv)
                {
                    isValid = false;
                    break;
                }
            }

            if (isValid)
            {
                buffer[j++] = c;
            }
        }

        return new string(buffer[..j]);
    }

    private void EnsureDirectorySecure()
    {
        if (Directory.Exists(_credentialsDirectory))
        {
            return;
        }

        Directory.CreateDirectory(_credentialsDirectory);

        if (OperatingSystem.IsWindows())
        {
            SecureDirectoryWindows(_credentialsDirectory);
        }
        else
        {
            SecureDirectoryPosix(_credentialsDirectory);
        }
    }

    private void SecureFile(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            SecureFileWindows(filePath);
        }
        else
        {
            SecureFilePosix(filePath);
        }
    }

    private static void SecureDirectoryPosix(string path)
    {
        Chmod(path, "700");
    }

    private static void SecureFilePosix(string path)
    {
        Chmod(path, "600");
    }

    private static void Chmod(string path, string mode)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/chmod",
                Arguments = $"{mode} {EscapeArg(path)}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(2000);
        }
        catch
        {
            // Best-effort; do not throw if chmod is unavailable (e.g. inside a container).
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SecureDirectoryWindows(string path)
    {
        try
        {
            DirectoryInfo dirInfo = new(path);
            DirectorySecurity security = dirInfo.GetAccessControl();

            security.SetAccessRuleProtection(true, false);
            security.SetOwner(WindowsIdentity.GetCurrent().User!);

            AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(NTAccount));
            foreach (FileSystemAccessRule rule in rules)
            {
                security.RemoveAccessRule(rule);
            }

            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().Name,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            dirInfo.SetAccessControl(security);
        }
        catch
        {
            // Best-effort; do not throw if ACL manipulation is unsupported.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SecureFileWindows(string path)
    {
        try
        {
            FileInfo fileInfo = new(path);
            FileSecurity security = fileInfo.GetAccessControl();

            security.SetAccessRuleProtection(true, false);
            security.SetOwner(WindowsIdentity.GetCurrent().User!);

            AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(NTAccount));
            foreach (FileSystemAccessRule rule in rules)
            {
                security.RemoveAccessRule(rule);
            }

            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().Name,
                FileSystemRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));

            fileInfo.SetAccessControl(security);
        }
        catch
        {
            // Best-effort.
        }
    }

    private static string EscapeArg(string arg)
    {
        return $"\"{arg.Replace("\"", "\\\"")}\"";
    }
}
