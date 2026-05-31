using Dmon.Abstractions.Memory;
using Dmon.Core.Session;
using Dmon.Memory.Embedding;
using Dmon.Memory.Index;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Dmon.Memory;

/// <summary>
/// Extension methods for registering Dmon memory services.
/// </summary>
public static class DmonMemoryServiceExtensions
{
    /// <summary>
    /// Registers the local embedder, short-term store, and <see cref="IMemory"/> facade.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call after <c>AddDmonCore()</c>:
    /// <code>
    /// services.AddDmonCore().AddDmonMemory();
    /// </code>
    /// </para>
    /// <para>
    /// <strong>Session identity:</strong> the host must also register <see cref="MemoryContext"/>
    /// (a session-scoped record) before resolving <see cref="IShortTermMemory"/>. Additionally,
    /// <see cref="IShortTermMemory.InitializeAsync"/> must be awaited before calling
    /// <see cref="IShortTermMemory.SearchAsync"/> or <see cref="IShortTermMemory.RecordAsync"/>;
    /// DI cannot perform async initialisation — this is a host responsibility.
    /// </para>
    /// <para>
    /// <strong>Durable (long-term) memory is opt-in.</strong> To enable it, call the
    /// <c>Dmon.Memory.Meko</c> package's <c>AddMekoLongTermMemory(...)</c> before or after
    /// <c>AddDmonMemory()</c> — registration order does not matter because the facade resolves
    /// <see cref="ILongTermMemory"/> at construction via <c>GetService</c> (nullable). With no
    /// long-term store registered, <see cref="IMemory.LongTerm"/> is <see langword="null"/> and
    /// dmon operates in short-term-only mode.
    /// </para>
    /// <para>
    /// <strong>Isolation:</strong> <c>Dmon.Memory</c> has no dependency on <c>Dmon.Memory.Meko</c>.
    /// </para>
    /// <para>
    /// All registrations use <c>TryAddSingleton</c>, so a host or test can pre-register
    /// substitutes (e.g. a stub embedder) without this method clobbering them.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddDmonMemory(this IServiceCollection services)
    {
        services.TryAddSingleton(new ModelResolver());

        services.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new LocalEmbeddingGenerator(
                sp.GetRequiredService<ModelResolver>(),
                sp.GetService<ILogger<LocalEmbeddingGenerator>>()));

        services.TryAddSingleton<IMessageAppender, MessageAppender>();

        services.TryAddSingleton<IShortTermMemory>(sp =>
            new ShortTermMemory(
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                sp.GetRequiredService<IMessageAppender>(),
                sp.GetRequiredService<ISessionStore>(),
                sp.GetRequiredService<ISessionDirectoryResolver>(),
                sp.GetRequiredService<MemoryContext>(),
                sp.GetService<ILogger<ShortTermMemory>>()));

        services.TryAddSingleton<IMemory>(sp =>
            new Memory(
                sp.GetRequiredService<IShortTermMemory>(),
                sp.GetService<ILongTermMemory>(),
                sp.GetService<ILogger<Memory>>()));

        return services;
    }
}
