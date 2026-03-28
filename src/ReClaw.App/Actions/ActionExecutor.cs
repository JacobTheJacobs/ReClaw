using System;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using ReClaw.App.Validation;
using ReClaw.App.Journal;

namespace ReClaw.App.Actions;

public sealed class ActionExecutor
{
    private readonly ActionRegistry registry;
    private readonly ActionValidatorRegistry validators;

    public ActionExecutor(ActionRegistry registry, ActionValidatorRegistry validators)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.validators = validators ?? throw new ArgumentNullException(nameof(validators));
    }

    public async Task<ActionResult> ExecuteAsync(
        string actionId,
        object? input,
        ActionContext context,
        IProgress<ActionEvent> events,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actionId)) throw new ArgumentException("ActionId required", nameof(actionId));
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (events is null) throw new ArgumentNullException(nameof(events));

        var descriptor = registry.GetDescriptor(actionId);
        var handler = registry.GetHandler(actionId);
        var correlationId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;

        events.Report(new ActionStarted(actionId, correlationId, startedAt));

        ActionResult ReturnWithJournal(ActionResult result)
        {
            OperationJournal.TryAppend(context, descriptor, input, result, startedAt, DateTimeOffset.UtcNow);
            return result;
        }

        if (input is null)
        {
            if (descriptor.InputType != typeof(EmptyInput))
            {
                return ReturnWithJournal(Fail("Missing input payload", actionId, correlationId, events));
            }
        }
        else if (!descriptor.InputType.IsInstanceOfType(input))
        {
            return ReturnWithJournal(Fail($"Invalid input type. Expected {descriptor.InputType.Name}", actionId, correlationId, events));
        }

        var validator = validators.Get(descriptor.InputType);
        if (validator != null && input != null)
        {
            var validation = validator.Validate(new ValidationContext<object>(input));
            if (!validation.IsValid)
            {
                return ReturnWithJournal(Fail(string.Join("; ", validation.Errors), actionId, correlationId, events));
            }
        }

        try
        {
            var result = await handler(actionId, correlationId, context, input, events, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                events.Report(new ActionCompleted(actionId, correlationId, DateTimeOffset.UtcNow, result));
            }
            else
            {
                events.Report(new ActionFailed(actionId, correlationId, DateTimeOffset.UtcNow, result.Error ?? "Action failed"));
            }
            return ReturnWithJournal(result);
        }
        catch (OperationCanceledException)
        {
            events.Report(new ActionCancelled(actionId, correlationId, DateTimeOffset.UtcNow));
            return ReturnWithJournal(new ActionResult(false, Error: "Cancelled"));
        }
        catch (Exception ex)
        {
            events.Report(new ActionFailed(actionId, correlationId, DateTimeOffset.UtcNow, ex.Message));
            return ReturnWithJournal(new ActionResult(false, Error: ex.Message));
        }
    }

    private static ActionResult Fail(string error, string actionId, Guid correlationId, IProgress<ActionEvent> events)
    {
        events.Report(new ActionFailed(actionId, correlationId, DateTimeOffset.UtcNow, error));
        return new ActionResult(false, Error: error);
    }
}
