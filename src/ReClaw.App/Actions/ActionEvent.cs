using System;

namespace ReClaw.App.Actions;

public abstract record ActionEvent(string ActionId, Guid CorrelationId, DateTimeOffset Timestamp);

public record ActionStarted(string ActionId, Guid CorrelationId, DateTimeOffset Timestamp, string? Message = null)
    : ActionEvent(ActionId, CorrelationId, Timestamp);

public record LogReceived(string ActionId, Guid CorrelationId, DateTimeOffset Timestamp, string Line, bool IsError = false)
    : ActionEvent(ActionId, CorrelationId, Timestamp);

public record ProgressChanged(string ActionId, Guid CorrelationId, DateTimeOffset Timestamp, double? Progress, string? Message = null)
    : ActionEvent(ActionId, CorrelationId, Timestamp);

public record StatusChanged(string ActionId, Guid CorrelationId, DateTimeOffset Timestamp, string Status, string? Detail = null)
    : ActionEvent(ActionId, CorrelationId, Timestamp);

public record ActionCompleted(string ActionId, Guid CorrelationId, DateTimeOffset Timestamp, ActionResult Result)
    : ActionEvent(ActionId, CorrelationId, Timestamp);

public record ActionFailed(string ActionId, Guid CorrelationId, DateTimeOffset Timestamp, string Error)
    : ActionEvent(ActionId, CorrelationId, Timestamp);

public record ActionCancelled(string ActionId, Guid CorrelationId, DateTimeOffset Timestamp, string? Reason = null)
    : ActionEvent(ActionId, CorrelationId, Timestamp);
