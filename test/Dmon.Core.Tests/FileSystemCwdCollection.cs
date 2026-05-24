namespace Dmon.Core.Tests;

/// <summary>
/// Serialises all test classes that mutate Directory.GetCurrentDirectory().
/// Tests in this collection run sequentially, preventing CWD corruption.
/// </summary>
[CollectionDefinition("FileSystemCwd", DisableParallelization = true)]
public sealed class FileSystemCwdCollection { }
