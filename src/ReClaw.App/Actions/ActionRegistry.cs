using System;
using System.Collections.Generic;

namespace ReClaw.App.Actions;

public sealed class ActionRegistry
{
    private readonly Dictionary<string, ActionDescriptor> descriptors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActionHandler> handlers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ActionDescriptor> Descriptors => descriptors.Values;

    public void Register(ActionDescriptor descriptor, ActionHandler handler)
    {
        if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        descriptors[descriptor.Id] = descriptor;
        handlers[descriptor.Id] = handler;
    }

    public ActionDescriptor GetDescriptor(string actionId)
    {
        if (!descriptors.TryGetValue(actionId, out var descriptor))
        {
            throw new KeyNotFoundException($"Action '{actionId}' not found.");
        }

        return descriptor;
    }

    public ActionHandler GetHandler(string actionId)
    {
        if (!handlers.TryGetValue(actionId, out var handler))
        {
            throw new KeyNotFoundException($"Handler for action '{actionId}' not registered.");
        }

        return handler;
    }
}

public delegate System.Threading.Tasks.Task<ActionResult> ActionHandler(
    string actionId,
    Guid correlationId,
    ActionContext context,
    object? input,
    IProgress<ActionEvent> events,
    System.Threading.CancellationToken cancellationToken);
