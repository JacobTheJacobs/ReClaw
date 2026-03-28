using System.Collections.Generic;
using System.Linq;
using NJsonSchema;
using ReClaw.App.Actions;

namespace ReClaw.App.Schemas;

public sealed record ActionSchema(string ActionId, JsonSchema InputSchema, JsonSchema OutputSchema);

public sealed record ActionSchemaDocument(IReadOnlyList<ActionSchema> Actions);

public static class ActionSchemaExporter
{
    public static ActionSchema Export(ActionDescriptor descriptor)
    {
        var inputSchema = JsonSchema.FromType(descriptor.InputType);
        var outputSchema = JsonSchema.FromType(descriptor.OutputType);
        return new ActionSchema(descriptor.Id, inputSchema, outputSchema);
    }

    public static ActionSchemaDocument ExportAll(IEnumerable<ActionDescriptor> descriptors)
    {
        var list = descriptors.Select(Export).ToList();
        return new ActionSchemaDocument(list);
    }
}
