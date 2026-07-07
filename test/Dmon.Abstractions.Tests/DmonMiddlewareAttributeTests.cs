using Dmon.Abstractions.Extensions;

namespace Dmon.Abstractions.Tests;

/// <summary>
/// Verifies the default and custom-priority behaviour of <see cref="DmonMiddlewareAttribute"/>.
/// Spec: "DmonMiddlewareAttribute marks and configures middleware" — Priority defaults to 0
/// and can be set to any integer at construction time.
/// Task 6.1.
/// </summary>
public sealed class DmonMiddlewareAttributeTests
{
    [Fact]
    public void DefaultPriority_IsZero()
    {
        DmonMiddlewareAttribute attr = new();

        Assert.Equal(0, attr.Priority);
    }

    [Fact]
    public void CustomPriority_IsPreserved()
    {
        DmonMiddlewareAttribute attr = new() { Priority = 100 };

        Assert.Equal(100, attr.Priority);
    }
}
