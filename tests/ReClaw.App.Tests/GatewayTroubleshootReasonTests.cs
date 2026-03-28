using System;
using ReClaw.App.Actions;
using ReClaw.App.Execution;
using Xunit;

namespace ReClaw.App.Tests;

public sealed class GatewayTroubleshootReasonTests
{
    [Fact]
    public void ExtractStartupReason_PrefersGatewayModeUnset()
    {
        var logs = new OpenClawCommandSummary(
            "openclaw logs --follow",
            1,
            false,
            new[] { "Gateway not reachable." },
            Array.Empty<string>(),
            1,
            0,
            false);
        var doctor = new OpenClawCommandSummary(
            "openclaw doctor",
            1,
            false,
            new[] { "gateway.mode is unset; gateway start will be blocked" },
            Array.Empty<string>(),
            1,
            0,
            false);

        var reason = InternalActionDispatcher.ExtractStartupReason(logs, doctor, null, null);

        Assert.NotNull(reason);
        Assert.Contains("gateway.mode is unset", reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("openclaw config set gateway.mode local", reason!, StringComparison.OrdinalIgnoreCase);
    }
}
