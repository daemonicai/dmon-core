using Dmon.Memory.Meko;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Memory.Meko.Tests.Meko;

/// <summary>
/// Factory helpers shared across test classes.
/// </summary>
internal static class MekoTestHelpers
{
    // A deterministic hyphenated GUID used as the test session id.
    // MekoScopeMapping.ToRunId normalises it to "N" format (pure hex) for Meko's
    // int(run_id, 16) server-side validation.
    public const string TestSessionId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    public const string TestSessionIdNFormat = "a1b2c3d4e5f67890abcdef1234567890";

    public static MekoLongTermOptions DefaultOptions(MekoCaptureMode captureMode = MekoCaptureMode.None) =>
        new()
        {
            ApiKey = "mko_tkn_test",
            DatapackId = "dp-test",
            SessionId = TestSessionId,
            CaptureMode = captureMode,
        };

    public static MekoLongTermMemory BuildMemory(
        FakeMekoToolInvoker invoker,
        MekoCaptureMode captureMode = MekoCaptureMode.None) =>
        BuildMemory(invoker, DefaultOptions(captureMode));

    public static MekoLongTermMemory BuildMemory(
        FakeMekoToolInvoker invoker,
        MekoLongTermOptions options)
    {
        var context = new MekoMemoryContext(options);
        var logger = NullLogger<MekoLongTermMemory>.Instance;
        return new MekoLongTermMemory(invoker, context, options, logger);
    }
}
