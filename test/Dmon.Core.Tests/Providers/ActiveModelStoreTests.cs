using Dmon.Core.Providers;

namespace Dmon.Core.Tests.Providers;

public sealed class ActiveModelStoreTests
{
    // --- helpers ---

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    // --- round-trip ---

    [Fact]
    public async Task SaveAsync_ThenLoad_ReturnsOriginalSelection()
    {
        string tempDir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, ".dmon"));
            ActiveModelStore store = ActiveModelStore.LoadProject(tempDir);
            ActiveSelection original = new("gemini", "gemini-2.0-flash-lite");

            await store.SaveAsync(original);

            ActiveSelection? loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.Equal("gemini", loaded.Provider);
            Assert.Equal("gemini-2.0-flash-lite", loaded.Model);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // --- project scope ---

    [Fact]
    public async Task SaveAsync_ProjectDmonExists_WritesToProjectStateYaml()
    {
        string tempDir = CreateTempDir();
        try
        {
            string dmonDir = Path.Combine(tempDir, ".dmon");
            Directory.CreateDirectory(dmonDir);

            ActiveModelStore store = ActiveModelStore.LoadProject(tempDir);
            await store.SaveAsync(new ActiveSelection("anthropic", "claude-3-5-sonnet"));

            string expectedPath = Path.Combine(dmonDir, "state.yaml");
            Assert.Equal(expectedPath, store.FilePath);
            Assert.True(File.Exists(expectedPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // --- global fallback resolution (no real write to ~/. dmon) ---

    [Fact]
    public void LoadProject_NoDmonDir_ResolvesToGlobalPath()
    {
        string tempDir = CreateTempDir();
        try
        {
            // No .dmon directory under tempDir — must fall back to global.
            ActiveModelStore store = ActiveModelStore.LoadProject(tempDir);

            string expectedGlobalPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dmon",
                "state.yaml");

            Assert.Equal(expectedGlobalPath, store.FilePath);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadGlobal_AlwaysResolvesToHomeDirectory()
    {
        ActiveModelStore store = ActiveModelStore.LoadGlobal();

        string expectedGlobalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dmon",
            "state.yaml");

        Assert.Equal(expectedGlobalPath, store.FilePath);
    }

    // --- absent file ---

    [Fact]
    public void Load_AbsentFile_ReturnsNull()
    {
        string tempDir = CreateTempDir();
        try
        {
            string dmonDir = Path.Combine(tempDir, ".dmon");
            Directory.CreateDirectory(dmonDir);

            ActiveModelStore store = ActiveModelStore.LoadProject(tempDir);
            // No state.yaml written yet.
            ActiveSelection? result = store.Load();

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // --- garbage file ---

    [Fact]
    public void Load_GarbageFile_ReturnsNullWithoutThrowing()
    {
        string tempDir = CreateTempDir();
        try
        {
            string dmonDir = Path.Combine(tempDir, ".dmon");
            Directory.CreateDirectory(dmonDir);
            File.WriteAllText(Path.Combine(dmonDir, "state.yaml"), "this is not yaml: [[[{{{}}}");

            ActiveModelStore store = ActiveModelStore.LoadProject(tempDir);

            // Must not throw; garbage values don't supply a valid provider.
            ActiveSelection? result = store.Load();

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // --- activeModel absent ---

    [Fact]
    public async Task Load_NoModelKey_ReturnsSelectionWithNullModel()
    {
        string tempDir = CreateTempDir();
        try
        {
            string dmonDir = Path.Combine(tempDir, ".dmon");
            Directory.CreateDirectory(dmonDir);

            // Write a state.yaml with only activeProvider (no activeModel key).
            File.WriteAllText(Path.Combine(dmonDir, "state.yaml"), "activeProvider: openai\n");

            ActiveModelStore store = ActiveModelStore.LoadProject(tempDir);
            ActiveSelection? result = store.Load();

            Assert.NotNull(result);
            Assert.Equal("openai", result.Provider);
            Assert.Null(result.Model);

            // Also confirm SaveAsync omits the model key when Model is null.
            await store.SaveAsync(new ActiveSelection("openai", null));
            string content = File.ReadAllText(store.FilePath);
            Assert.DoesNotContain("activeModel", content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // --- fresh reload from disk ---

    [Fact]
    public async Task SaveAsync_FreshLoadFromDisk_PreservesValues()
    {
        string tempDir = CreateTempDir();
        try
        {
            string dmonDir = Path.Combine(tempDir, ".dmon");
            Directory.CreateDirectory(dmonDir);

            ActiveModelStore store = ActiveModelStore.LoadProject(tempDir);
            await store.SaveAsync(new ActiveSelection("gemini", "gemini-2.0-flash-lite"));

            // Load with a new store instance (simulates restart).
            ActiveModelStore reloaded = ActiveModelStore.LoadProject(tempDir);
            ActiveSelection? result = reloaded.Load();

            Assert.NotNull(result);
            Assert.Equal("gemini", result.Provider);
            Assert.Equal("gemini-2.0-flash-lite", result.Model);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
