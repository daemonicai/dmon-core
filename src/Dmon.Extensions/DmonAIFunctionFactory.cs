using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Dmon.Extensions;

/// <summary>
/// Provides convenience methods for creating <see cref="AIFunction"/> instances
/// from delegates and methods. Wraps <see cref="Microsoft.Extensions.AI.AIFunctionFactory"/>
/// with simplified overloads for common extension patterns.
/// </summary>
/// <remarks>
/// <para>
/// This class is a thin helper over <see cref="Microsoft.Extensions.AI.AIFunctionFactory.Create(System.Delegate, string?, string?, JsonSerializerOptions?)"/>.
/// It exists so extension authors can create functions with fewer parameters and
/// reasonable defaults, without needing to configure <see cref="AIFunctionFactoryOptions"/>
/// for every call.
/// </para>
/// <para>
/// For advanced scenarios (custom schemas, parameter binding control, result marshalling),
/// use <see cref="Microsoft.Extensions.AI.AIFunctionFactory"/> directly. This helper covers
/// the 90% case: a named, described function with automatic JSON schema derivation.
/// </para>
/// <para>Example:</para>
/// <code>
/// var fn = DmonAIFunctionFactory.Create(
///     (string name, int age) => $"Hello {name}, you are {age}",
///     "GreetPerson",
///     "Returns a personalised greeting for a person by name and age.");
/// </code>
/// </remarks>
public static class DmonAIFunctionFactory
{
    /// <summary>
    /// Creates an <see cref="AIFunction"/> from a delegate with automatic JSON schema
    /// derivation for all parameters.
    /// </summary>
    /// <param name="method">The delegate to wrap as an AI-callable function.</param>
    /// <param name="name">
    /// The name the LLM will use to invoke this function. Must be unique within the
    /// session's tool registry. Use PascalCase (e.g. <c>"GetWeather"</c>).
    /// </param>
    /// <param name="description">
    /// A description of what the function does. This is included in the LLM's system
    /// prompt and influences when the model chooses to call the function.
    /// Keep it clear and actionable.
    /// </param>
    /// <returns>An <see cref="AIFunction"/> ready for registration in an extension.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="method"/> is <see langword="null"/>.
    /// </exception>
    public static AIFunction Create(Delegate method, string name, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(method);

        return Microsoft.Extensions.AI.AIFunctionFactory.Create(
            method,
            name,
            description);
    }

    /// <summary>
    /// Creates an <see cref="AIFunction"/> from a delegate with custom
    /// <see cref="JsonSerializerOptions"/> for JSON schema derivation and argument
    /// deserialization.
    /// </summary>
    /// <param name="method">The delegate to wrap.</param>
    /// <param name="name">
    /// The function name exposed to the LLM. If <see langword="null"/>, the underlying
    /// <see cref="Microsoft.Extensions.AI.AIFunctionFactory"/> will infer a name from the delegate.
    /// </param>
    /// <param name="description">The function description for the LLM.</param>
    /// <param name="serializerOptions">
    /// <see cref="JsonSerializerOptions"/> used for parameter deserialization and
    /// JSON schema generation. If <see langword="null"/>, defaults are used.
    /// </param>
    /// <returns>An <see cref="AIFunction"/> ready for registration.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="method"/> is <see langword="null"/>.
    /// </exception>
    public static AIFunction Create(
        Delegate method,
        string name,
        string? description,
        JsonSerializerOptions? serializerOptions)
    {
        ArgumentNullException.ThrowIfNull(method);

        return Microsoft.Extensions.AI.AIFunctionFactory.Create(
            method,
            name,
            description,
            serializerOptions);
    }

    /// <summary>
    /// Creates an <see cref="AIFunction"/> from a <see cref="MethodInfo"/> and an
    /// optional target instance for instance methods. For static methods, pass
    /// <see langword="null"/> for <paramref name="target"/>.
    /// </summary>
    /// <param name="method">The <see cref="MethodInfo"/> representing the function implementation.</param>
    /// <param name="target">
    /// The object instance for instance methods, or <see langword="null"/> for static methods.
    /// </param>
    /// <param name="name">
    /// The function name exposed to the LLM. If <see langword="null"/>, the underlying
    /// <see cref="Microsoft.Extensions.AI.AIFunctionFactory"/> will infer a name from the method.
    /// </param>
    /// <param name="description">The function description for the LLM.</param>
    /// <param name="serializerOptions">
    /// Optional <see cref="JsonSerializerOptions"/> for schema derivation and argument binding.
    /// </param>
    /// <returns>An <see cref="AIFunction"/> ready for registration.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="method"/> is <see langword="null"/>.
    /// </exception>
    public static AIFunction Create(
        MethodInfo method,
        object? target,
        string name,
        string? description = null,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(method);

        return Microsoft.Extensions.AI.AIFunctionFactory.Create(
            method,
            target,
            name,
            description,
            serializerOptions);
    }

    /// <summary>
    /// Creates an <see cref="AIFunction"/> from a delegate with full
    /// <see cref="AIFunctionFactoryOptions"/> for advanced scenarios such as
    /// custom parameter binding, result marshalling, or excluding the result schema.
    /// </summary>
    /// <param name="method">The delegate to wrap.</param>
    /// <param name="options">Full options controlling schema generation, parameter binding, and naming.</param>
    /// <returns>An <see cref="AIFunction"/> ready for registration.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="method"/> is <see langword="null"/>.
    /// </exception>
    public static AIFunction Create(Delegate method, AIFunctionFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(method);

        return Microsoft.Extensions.AI.AIFunctionFactory.Create(method, options);
    }

    /// <summary>
    /// Creates an <see cref="AIFunction"/> from a <see cref="MethodInfo"/> with full
    /// <see cref="AIFunctionFactoryOptions"/>.
    /// </summary>
    /// <param name="method">The <see cref="MethodInfo"/> representing the function.</param>
    /// <param name="target">The target instance, or <see langword="null"/> for static methods.</param>
    /// <param name="options">Full options for schema, binding, and naming.</param>
    /// <returns>An <see cref="AIFunction"/> ready for registration.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="method"/> is <see langword="null"/>.
    /// </exception>
    public static AIFunction Create(
        MethodInfo method,
        object? target,
        AIFunctionFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(method);

        return Microsoft.Extensions.AI.AIFunctionFactory.Create(method, target, options);
    }
}