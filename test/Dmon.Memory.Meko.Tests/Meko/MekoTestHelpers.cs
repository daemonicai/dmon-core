using Dmon.Memory.Meko;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Memory.Meko.Tests.Meko;

/// <summary>
/// Factory helpers shared across test classes.
/// </summary>
internal static class MekoTestHelpers
{
    public static MekoLongTermOptions DefaultOptions(MekoCaptureMode captureMode = MekoCaptureMode.None) =>
        new()
        {
            ApiKey = "mko_tkn_test",
            DatapackId = "dp-test",
            SessionId = "sess-test",
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
