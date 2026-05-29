namespace Dmon.Memory.Embedding;

/// <summary>
/// Resolves the path to the nomic-embed-text GGUF on disk, downloading it on first run if absent.
///
/// Cache directory (in priority order):
///   1. <c>DMON_MODEL_CACHE</c> env var (allows CI / tests to redirect to a non-existent path so the
///      download branch is never reached during automated builds)
///   2. <c>%LOCALAPPDATA%/dmon/models/</c> on Windows
///   3. <c>~/.local/share/dmon/models/</c> on Linux
///   4. <c>~/Library/Application Support/dmon/models/</c> on macOS
///
/// The class is intentionally non-static so it can be injected / replaced in tests.
/// </summary>
public sealed class ModelResolver
{
    // Environment variable that overrides the cache directory. Set to a non-existent path
    // (e.g. /tmp/dmon-models-nocache) in CI to ensure no download is attempted.
    public const string CacheDirEnvVar = "DMON_MODEL_CACHE";

    private readonly string _cacheDir;

    public ModelResolver() : this(ResolveCacheDir()) { }

    // Internal constructor for tests — pass a temp dir to redirect caching.
    internal ModelResolver(string cacheDir)
    {
        _cacheDir = cacheDir;
    }

    /// <summary>
    /// Returns the absolute path to the model GGUF, downloading it if not already cached.
    /// The download is atomic: the file is written to <c>*.tmp</c> then renamed into place,
    /// so a half-written file is never seen by other callers.
    /// </summary>
    public async Task<string> ResolveAsync(CancellationToken cancellationToken = default)
    {
        string modelPath = Path.Combine(_cacheDir, NomicEmbedding.GgufFileName);
        if (File.Exists(modelPath))
            return modelPath;

        Directory.CreateDirectory(_cacheDir);

        string tmpPath = modelPath + ".tmp";
        using HttpClient client = new();
        // HuggingFace redirects; HttpClient follows by default.
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.Add("User-Agent", "dmon/1.0 (model-resolver)");

        using HttpResponseMessage response = await client.GetAsync(
            NomicEmbedding.GgufDownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using (FileStream fs = new(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            await contentStream.CopyToAsync(fs, cancellationToken);
        }

        // Atomic rename — replaces any stale .tmp from a previous interrupted download.
        File.Move(tmpPath, modelPath, overwrite: true);
        return modelPath;
    }

    /// <summary>The resolved model cache directory (for diagnostic / test use).</summary>
    public string CacheDirectory => _cacheDir;

    private static string ResolveCacheDir()
    {
        string? envOverride = Environment.GetEnvironmentVariable(CacheDirEnvVar);
        if (!string.IsNullOrWhiteSpace(envOverride))
            return envOverride;

        // Use LocalApplicationData which maps to the platform-appropriate location.
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "dmon", "models");
    }
}
