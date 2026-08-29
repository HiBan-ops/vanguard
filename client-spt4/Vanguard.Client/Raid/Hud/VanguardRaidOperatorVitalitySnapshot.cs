#if SPT_CLIENT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;

// Responsibility: Defines data/state contracts used by the raid Operator HUD, centered on Raid Operator Vitality Snapshot.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.Raid.Hud;

/// <summary>
/// Read-only medical snapshot used by the HUD. It reads native body-part health without taking medical execution authority
/// without becoming an authority for medical gameplay.
/// </summary>
internal sealed class VanguardRaidOperatorVitalitySnapshot
{
    private VanguardRaidOperatorVitalitySnapshot(
        string isAlive,
        int healthPercent,
        string bodyParts,
        string physiological,
        string effects)
    {
        IsAlive = isAlive;
        HealthPercent = healthPercent;
        BodyParts = bodyParts;
        Physiological = physiological;
        Effects = effects;
    }

    public string IsAlive { get; }
    public int HealthPercent { get; }
    public string BodyParts { get; }
    public string Physiological { get; }
    public string Effects { get; }

    public static VanguardRaidOperatorVitalitySnapshot Create(IPlayer player)
    {
        var healthController = VanguardRaidHudReflection.ReadMember(player, "HealthController")
            ?? VanguardRaidHudReflection.ReadMember(player, "ActiveHealthController")
            ?? player.HealthController;
        var activeHealthController = VanguardRaidHudReflection.ReadMember(player, "ActiveHealthController")
            ?? healthController;

        string bodyParts = ReadBodyParts(healthController, out int? percentFromParts);
        int healthPercent = percentFromParts ?? ReadPercentByReflection(healthController) ?? 0;
        bool isAlive = ReadBool(activeHealthController, "IsAlive")
            ?? ReadBool(healthController, "IsAlive")
            ?? healthPercent > 0;

        return new VanguardRaidOperatorVitalitySnapshot(
            isAlive.ToString(),
            healthPercent,
            bodyParts,
            ReadPhysiological(activeHealthController, healthController),
            ReadEffects(activeHealthController, healthController));
    }

    public static bool TryReadCommonHealth(IPlayer player, out float current, out float maximum, out bool isAlive)
    {
        current = 0f;
        maximum = 0f;
        isAlive = false;

        try
        {
            var healthController = player.HealthController;
            if (healthController is null)
            {
                return false;
            }

            var common = healthController.GetBodyPartHealth(EBodyPart.Common, true);
            current = common.Current;
            maximum = common.Maximum;
            isAlive = healthController.IsAlive;
            return maximum > 0f;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadBodyParts(object? healthController, out int? healthPercent)
    {
        var parts = new[]
        {
            EBodyPart.Head,
            EBodyPart.Chest,
            EBodyPart.Stomach,
            EBodyPart.LeftArm,
            EBodyPart.RightArm,
            EBodyPart.LeftLeg,
            EBodyPart.RightLeg,
        };

        var tokens = new List<string>();
        float currentTotal = 0f;
        float maxTotal = 0f;

        foreach (var part in parts)
        {
            if (!TryReadPartHealth(healthController, part, out float current, out float maximum))
            {
                tokens.Add($"{part}=Unknown");
                continue;
            }

            currentTotal += Math.Max(0f, current);
            maxTotal += Math.Max(0f, maximum);
            string state = maximum > 0f && current <= 0f ? ":destroyed" : string.Empty;
            tokens.Add($"{part}={current:0}/{maximum:0}{state}");
        }

        healthPercent = maxTotal > 0f
            ? ClampPercent((int)Math.Round((currentTotal / maxTotal) * 100f))
            : null;

        return string.Join(";", tokens);
    }

    private static bool TryReadPartHealth(object? healthController, EBodyPart part, out float current, out float maximum)
    {
        current = 0f;
        maximum = 0f;

        try
        {
            var method = healthController?.GetType().GetMethod(
                "GetBodyPartHealth",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method is null)
            {
                return false;
            }

            object? result;
            var parameters = method.GetParameters();
            if (parameters.Length == 2)
            {
                result = method.Invoke(healthController, new object[] { part, false });
            }
            else if (parameters.Length == 1)
            {
                result = method.Invoke(healthController, new object[] { part });
            }
            else
            {
                return false;
            }

            current = Convert.ToSingle(VanguardRaidHudReflection.ReadMember(result, "Current") ?? 0f);
            maximum = Convert.ToSingle(VanguardRaidHudReflection.ReadMember(result, "Maximum") ?? 0f);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int? ReadPercentByReflection(object? healthController)
    {
        foreach (string name in new[] { "HealthPercent", "CurrentHealthPercent", "NormalizedHealth" })
        {
            var value = VanguardRaidHudReflection.ReadMember(healthController, name);
            if (value is null)
            {
                continue;
            }

            try
            {
                float numeric = Convert.ToSingle(value);
                return numeric <= 1.01f
                    ? ClampPercent((int)Math.Round(numeric * 100f))
                    : ClampPercent((int)Math.Round(numeric));
            }
            catch
            {
            }
        }

        return null;
    }

    private static int ClampPercent(int value)
    {
        if (value < 0)
        {
            return 0;
        }

        return value > 100 ? 100 : value;
    }

    private static string ReadPhysiological(object? activeHealthController, object? healthController)
    {
        var tokens = new List<string>();
        foreach (string name in new[] { "Energy", "Hydration", "Temperature", "Radiation", "Poison", "Overweight", "Exhaustion" })
        {
            var value = VanguardRaidHudReflection.ReadMember(activeHealthController, name)
                ?? VanguardRaidHudReflection.ReadMember(healthController, name);
            if (value is not null)
            {
                tokens.Add($"{name}={SimplifyValue(value)}");
            }
        }

        return tokens.Count == 0 ? "Unknown" : string.Join(";", tokens);
    }

    private static string ReadEffects(object? activeHealthController, object? healthController)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { activeHealthController, healthController })
        {
            CollectEffects(source, names, 0);
        }

        var filtered = names
            .Where(name => name.Contains("Bleed", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Fracture", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Pain", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Contusion", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Tremor", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Dehyd", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Exhaust", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Stun", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Destroyed", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToArray();

        return filtered.Length == 0 ? "NoneOrUnknown" : string.Join(",", filtered);
    }

    private static void CollectEffects(object? source, HashSet<string> output, int depth)
    {
        if (source is null || depth > 2)
        {
            return;
        }

        if (source is string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                output.Add(text);
            }

            return;
        }

        string typeName = source.GetType().Name;
        if (typeName.Contains("Effect", StringComparison.OrdinalIgnoreCase))
        {
            output.Add(typeName);
        }

        if (source is IEnumerable enumerable)
        {
            int count = 0;
            foreach (var item in enumerable)
            {
                if (++count > 64)
                {
                    break;
                }

                CollectEffects(VanguardRaidHudReflection.UnwrapCollectionItem(item), output, depth + 1);
            }
        }

        foreach (string memberName in new[] { "Effects", "ActiveEffects", "EffectsList", "BodyPartEffects", "Dictionary_0", "List_0" })
        {
            CollectEffects(VanguardRaidHudReflection.ReadMember(source, memberName), output, depth + 1);
        }
    }

    private static bool? ReadBool(object? source, string memberName)
    {
        return VanguardRaidHudReflection.ReadMember(source, memberName) is bool value ? value : null;
    }

    private static string SimplifyValue(object value)
    {
        try
        {
            if (value is float f)
            {
                return f.ToString("0.##");
            }

            if (value is double d)
            {
                return d.ToString("0.##");
            }

            var current = VanguardRaidHudReflection.ReadMember(value, "Current")
                ?? VanguardRaidHudReflection.ReadMember(value, "Value");
            var maximum = VanguardRaidHudReflection.ReadMember(value, "Maximum")
                ?? VanguardRaidHudReflection.ReadMember(value, "Max");
            return current is not null && maximum is not null
                ? $"{current}/{maximum}"
                : value.ToString() ?? "<null>";
        }
        catch
        {
            return "Unknown";
        }
    }
}
#else
namespace Vanguard.Client.Raid.Hud;

internal sealed class VanguardRaidOperatorVitalitySnapshot
{
}
#endif
