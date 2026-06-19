using Dmon.Protocol.Enums;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.Tools.Dcal.Tests;

public sealed class CalendarExtensionTests
{
    [Fact]
    public void Tools_ContainsLookupCalendar()
    {
        DcalExtension ext = new();
        Assert.Contains(ext.Tools, t => t.Name == "lookup_calendar");
    }

    [Fact]
    public void Tools_ContainsListUpcomingEvents()
    {
        DcalExtension ext = new();
        Assert.Contains(ext.Tools, t => t.Name == "list_upcoming_events");
    }

    [Fact]
    public void Evaluate_LookupCalendar_ReturnsAllow()
    {
        DcalExtension ext = new();
        FunctionCallContent call = new("call-1", "lookup_calendar", null);

        PermissionResult result = ext.Evaluate(call, new StubPermissionSettings(), null);

        Assert.Equal(PermissionResult.Allow, result);
    }

    [Fact]
    public void Evaluate_ListUpcomingEvents_ReturnsAllow()
    {
        DcalExtension ext = new();
        FunctionCallContent call = new("call-1", "list_upcoming_events", null);

        PermissionResult result = ext.Evaluate(call, new StubPermissionSettings(), null);

        Assert.Equal(PermissionResult.Allow, result);
    }

    private sealed class StubPermissionSettings : IPermissionSettings
    {
        public PermissionSettings Settings => new();
        public Task SaveAsync(PermissionSettings updated, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
