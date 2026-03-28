using System;
using System.Collections.Generic;
using FluentValidation;

namespace ReClaw.App.Validation;

public sealed class ActionValidatorRegistry
{
    private readonly Dictionary<Type, IValidator> validators = new();

    public void Register<T>(IValidator<T> validator) where T : class
    {
        if (validator is null) throw new ArgumentNullException(nameof(validator));
        validators[typeof(T)] = validator;
    }

    public IValidator? Get(Type inputType)
    {
        if (inputType is null) throw new ArgumentNullException(nameof(inputType));
        return validators.TryGetValue(inputType, out var validator) ? validator : null;
    }
}
