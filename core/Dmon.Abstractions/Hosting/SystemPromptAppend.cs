namespace Dmon.Hosting;

/// <summary>
/// Ordered marker registered into DI by <see cref="DmonRegistrationExtensions.AppendToSystemPrompt{T}"/>.
/// <see cref="Dmon.Core.SystemPrompt.SystemPromptBuilder"/> enumerates all registrations
/// in call order to assemble <c>final = base + ordered appends</c>.
/// </summary>
/// <param name="Text">The text to append after the resolved base.</param>
public sealed record SystemPromptAppend(string Text);
