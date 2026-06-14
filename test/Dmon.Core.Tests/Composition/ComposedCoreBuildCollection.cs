namespace Dmon.Core.Tests.Composition;

/// <summary>
/// Serialises every test class that shells out to the .NET SDK (pack/build/restore/run)
/// so their nested MSBuild invocations do not run concurrently — concurrent SDK
/// invocations from inside <c>dotnet test</c> crash MSBuild worker nodes (MSB4166).
/// The shared <see cref="ComposedCoreFeedFixture"/> packs the local feed once for all
/// feed-consuming classes in the collection instead of once per class.
/// </summary>
[CollectionDefinition("ComposedCoreBuild", DisableParallelization = true)]
public sealed class ComposedCoreBuildCollection : ICollectionFixture<ComposedCoreFeedFixture> { }
