using System.Collections.Generic;
using System.Diagnostics;

namespace AWS.Lambda.Powertools.JMESPath.Functions;

internal sealed class KeysFunction : BaseFunction
{
    internal KeysFunction()
        : base(1)
    {
    }

    public override bool TryEvaluate(DynamicResources resources, IList<IValue> args,
        out IValue element)
    {
        Debug.Assert(Arity.HasValue && args.Count == Arity!.Value);

        var arg0 = args[0];
        if (arg0.Type != JmesPathType.Object)
        {
            element = JsonConstants.Null;
            return false;
        }

        var values = new List<IValue>();

        foreach (var property in arg0.EnumerateObject())
        {
            values.Add(new StringValue(property.Name));
        }

        element = new ArrayValue(values);
        return true;
    }

    public override string ToString()
    {
        return "keys";
    }
}