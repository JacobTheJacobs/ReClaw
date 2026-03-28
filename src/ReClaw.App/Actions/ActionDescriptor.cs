using System;

namespace ReClaw.App.Actions;

public sealed record ActionDescriptor(
    string Id,
    string Label,
    string Description,
    string Group,
    string Emoji,
    ExecutionMode ExecutionMode,
    ActionCapability Capabilities,
    Type InputType,
    Type OutputType,
    string[]? CommandArgs = null,
    string? ConfirmPhrase = null,
    bool OptionalPassword = false,
    bool RequiresArchive = false,
    string[]? Tags = null
);

public sealed record EmptyInput;
public sealed record EmptyOutput;
