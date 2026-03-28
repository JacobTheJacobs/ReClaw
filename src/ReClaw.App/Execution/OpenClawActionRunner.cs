using System;
using System.Threading;
using System.Threading.Tasks;
using ReClaw.App.Actions;

namespace ReClaw.App.Execution;

internal interface IOpenClawActionRunner
{
    Task<ActionResult> RunAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        string[] args,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken);
}

internal sealed class OpenClawActionRunner : IOpenClawActionRunner
{
    private readonly ProcessRunner runner;

    public OpenClawActionRunner(ProcessRunner runner)
    {
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public Task<ActionResult> RunAsync(
        string actionId,
        Guid correlationId,
        ActionContext context,
        string[] args,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        var openClawRunner = new OpenClawRunner(runner);
        return openClawRunner.RunAsync(actionId, correlationId, context, args, events, cancellationToken);
    }
}
