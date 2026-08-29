#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using Vanguard.Client.Runtime.Audit;

// Responsibility: Provides Snapshot Value Helpers support for the decision snapshot pipeline.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Runtime.Decision;

internal sealed partial class VanguardOperatorDecisionSnapshotBuilder
{
    private static string Text(object? value)
    {
        return VanguardOperatorRuntimeAuditReflection.Text(value);
    }

    private static bool? Bool(object? value)
    {
        if (value is bool boolValue)
        {
            return boolValue;
        }

        return null;
    }

    private static float? Float(object? value)
    {
        try
        {
            if (value == null)
            {
                return null;
            }

            float number = Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
            if (float.IsNaN(number) || float.IsInfinity(number))
            {
                return null;
            }

            return number;
        }
        catch
        {
            return null;
        }
    }

    private static Vector3? Vector(object? value)
    {
        if (value is Vector3 vector)
        {
            return vector;
        }

        object? hasValue = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(value, "HasValue");
        if (hasValue is bool trueValue && trueValue)
        {
            object? nested = VanguardOperatorRuntimeAuditReflection.GetPropertyOrField(value, "Value");
            if (nested is Vector3 nestedVector)
            {
                return nestedVector;
            }
        }

        return null;
    }

    private static Vector3? VectorFromComponents(object? x, object? y, object? z)
    {
        float? xf = Float(x);
        float? yf = Float(y);
        float? zf = Float(z);
        if (xf.HasValue && yf.HasValue && zf.HasValue)
        {
            return new Vector3(xf.Value, yf.Value, zf.Value);
        }

        return null;
    }

    private static string SanitizeKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        var chars = value.Trim().ToLowerInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]))
            {
                chars[i] = '_';
            }
        }

        return new string(chars).Trim('_');
    }
}
#endif
