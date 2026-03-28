using System;
using ReClaw.App.Execution;
using ReClaw.App.Validation;
using ReClaw.Core;

namespace ReClaw.App.Actions;

public static class DefaultActionRegistry
{
    public static (ActionRegistry Registry, ActionValidatorRegistry Validators, ActionExecutor Executor) Create(
        BackupService? backupService = null,
        ProcessRunner? processRunner = null)
    {
        var registry = new ActionRegistry();
        var validators = new ActionValidatorRegistry();
        var executor = new ActionExecutor(registry, validators);

        processRunner ??= new ProcessRunner();
        var openClawRunner = new OpenClawRunner(processRunner);
        backupService ??= new BackupService();

        foreach (var descriptor in ActionCatalog.All)
        {
            registry.Register(descriptor, (actionId, correlationId, context, input, events, ct) =>
            {
                return descriptor.ExecutionMode switch
                {
                    ExecutionMode.OpenClawPassthrough =>
                        openClawRunner.RunAsync(actionId, correlationId, context, descriptor.CommandArgs ?? Array.Empty<string>(), events, ct),
                    ExecutionMode.Internal =>
                        InternalActionDispatcher.ExecuteAsync(actionId, correlationId, context, input, events, ct, backupService, processRunner),
                    _ =>
                        System.Threading.Tasks.Task.FromResult(new ActionResult(false, Error: "Unsupported execution mode"))
                };
            });
        }

        return (registry, validators, executor);
    }
}
