using System;

namespace ReClaw.App.Actions;

[Flags]
public enum ActionCapability
{
    None = 0,
    RequiresGateway = 1 << 0,
    RequiresElevation = 1 << 1,
    Destructive = 1 << 2,
    Cancellable = 1 << 3,
    RequiresPassword = 1 << 4,
    RequiresArchive = 1 << 5
}
