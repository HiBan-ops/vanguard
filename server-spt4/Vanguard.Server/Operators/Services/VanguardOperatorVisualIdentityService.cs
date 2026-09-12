using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using Vanguard.Server.Diagnostics;
using Vanguard.Server.Operators.Models;

// Responsibility: Resolves the one-time concrete EFT appearance of a newly materialized Vanguard world Operator.
// Flow: World genesis supplies stable Operator facts; this service selects deterministically from the matching SPT PMC appearance pools, applies SPT's Body-to-Hands rule, validates customization ids and returns persistent evidence.
// Authority boundary: SPT bot appearance weights, customization templates and globals customization compatibility remain authoritative; Vanguard only replaces runtime randomness with stable identity-derived selection.
// Invariant: Selection depends only on stable Operator facts plus versioned policy and canonically sorted authority data; time/session/player appearance never participates in the selected ids.
namespace Vanguard.Server.Operators.Services;

[Injectable(InjectionType.Singleton)]
public sealed class VanguardOperatorVisualIdentityService(
    DatabaseService databaseService,
    ISptLogger<VanguardOperatorVisualIdentityService> logger)
{
    public const string StatusTag = "VANGUARD_OPERATOR_VISUAL_IDENTITY_STATUS";
    public const string PolicyId = "vanguard.operator-visual-identity";
    public const int PolicyVersion = 1;

    public VanguardOperatorVisualIdentity ResolveNewWorldIdentity(VanguardOperatorProfile profile, DateTimeOffset now)
    {
        if (profile.VisualIdentity is not null)
        {
            ValidatePersistentIdentity(profile.VisualIdentity, profile.Identity.Side);
            return profile.VisualIdentity;
        }

        string side = NormalizeSide(profile.Identity.Side);
        string sourceBotType = side.ToLowerInvariant();
        string seedMaterial = BuildSeedMaterial(profile.OperatorId, side, profile.Identity.VisualFamily);
        string seedFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seedMaterial)));

        try
        {
            Appearance appearance = ResolveAppearance(sourceBotType);
            string head = SelectWeighted(appearance.Head, seedMaterial, "head");
            string body = SelectWeighted(appearance.Body, seedMaterial, "body");
            string feet = SelectWeighted(appearance.Feet, seedMaterial, "feet");
            string voice = SelectWeighted(appearance.Voice, seedMaterial, "voice");

            string handsMode;
            string hands;
            if (TryResolveFixedHandsForBody(body, out string fixedHands))
            {
                hands = fixedHands;
                handsMode = "spt_body_fixed";
            }
            else
            {
                hands = SelectWeighted(appearance.Hands, seedMaterial, "hands");
                handsMode = "spt_weighted_pool";
            }

            foreach (string customizationId in new[] { head, body, feet, hands })
            {
                EnsureCustomizationEntryExists(customizationId);
            }

            var resolved = new VanguardOperatorVisualIdentity(
                head,
                body,
                feet,
                hands,
                voice,
                PolicyId,
                PolicyVersion,
                sourceBotType,
                seedFingerprint,
                now,
                VanguardOperatorVisualIdentitySchema.CurrentVersion);

            logger.Info(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=resolved; operator={profile.OperatorId}; side={side}; visualFamily={Normalize(profile.Identity.VisualFamily, "default")}; sourceBotType={sourceBotType}; head={head}; body={body}; feet={feet}; hands={hands}; voice={voice}; handsMode={handsMode}; seed={seedFingerprint}; policy={PolicyId}; policyVersion={PolicyVersion}; schema={VanguardOperatorVisualIdentitySchema.CurrentVersion}; tag={StatusTag}"));
            return resolved;
        }
        catch (Exception exception)
        {
            logger.Error(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] phase=resolve_failed; operator={profile.OperatorId}; side={side}; sourceBotType={sourceBotType}; type={exception.GetType().Name}; message={exception.Message}; action=reject_new_world_identity; tag={StatusTag}"));
            throw;
        }
    }

    public VanguardOperatorVisualIdentity AdoptLegacyPersistentIdentity(
        VanguardOperatorProfile profile,
        string head,
        string body,
        string feet,
        string hands,
        string voice,
        DateTimeOffset now)
    {
        if (profile.VisualIdentity is not null)
        {
            ValidatePersistentIdentity(profile.VisualIdentity, profile.Identity.Side);
            return profile.VisualIdentity;
        }

        ValidateCustomizationForSide(head, body, feet, hands, voice, profile.Identity.Side);
        string side = NormalizeSide(profile.Identity.Side);
        string sourceBotType = side.ToLowerInvariant();
        string seedMaterial = BuildSeedMaterial(profile.OperatorId, side, profile.Identity.VisualFamily);
        string seedFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seedMaterial)));
        var adopted = new VanguardOperatorVisualIdentity(
            head,
            body,
            feet,
            hands,
            voice,
            PolicyId,
            PolicyVersion,
            sourceBotType,
            seedFingerprint,
            now,
            VanguardOperatorVisualIdentitySchema.CurrentVersion);
        ValidatePersistentIdentity(adopted, profile.Identity.Side);
        logger.Info(VanguardServerDiagnosticsLog.Present(
            $"[{StatusTag}] phase=adopted_legacy; operator={profile.OperatorId}; side={side}; sourceBotType={sourceBotType}; seed={seedFingerprint}; policy={PolicyId}; policyVersion={PolicyVersion}; schema={VanguardOperatorVisualIdentitySchema.CurrentVersion}; tag={StatusTag}"));
        return adopted;
    }

    public void ValidatePersistentIdentity(VanguardOperatorVisualIdentity identity, string side)
    {
        if (!string.Equals(identity.ResolverPolicyId, PolicyId, StringComparison.Ordinal)
            || identity.ResolverPolicyVersion != PolicyVersion
            || identity.SchemaVersion != VanguardOperatorVisualIdentitySchema.CurrentVersion)
        {
            throw new InvalidOperationException("operator_visual_identity_policy_unsupported");
        }

        string sourceBotType = NormalizeSide(side).ToLowerInvariant();
        if (!string.Equals(identity.SourceBotType, sourceBotType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("operator_visual_identity_side_mismatch");
        }

        ValidateCustomizationForSide(identity.Head, identity.Body, identity.Feet, identity.Hands, identity.Voice, side);
    }

    // Dry migration diagnostics may assess legacy EFT customization without pretending that it is already Vanguard VisualIdentity evidence.
    public void ValidateCustomizationForSide(string head, string body, string feet, string hands, string voice, string side)
    {
        if (new[] { head, body, feet, hands, voice }.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("operator_visual_identity_incomplete");
        }

        foreach (string customizationId in new[] { head, body, feet, hands })
        {
            EnsureCustomizationEntryExists(customizationId);
        }

        string sourceBotType = NormalizeSide(side).ToLowerInvariant();
        Appearance appearance = ResolveAppearance(sourceBotType);
        EnsurePoolContains(appearance.Head, head, "head");
        EnsurePoolContains(appearance.Body, body, "body");
        EnsurePoolContains(appearance.Feet, feet, "feet");
        EnsurePoolContains(appearance.Voice, voice, "voice");

        if (TryResolveFixedHandsForBody(body, out string fixedHands))
        {
            if (!string.Equals(fixedHands, hands, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("operator_visual_identity_body_hands_mismatch");
            }
        }
        else
        {
            EnsurePoolContains(appearance.Hands, hands, "hands");
        }
    }

    private Appearance ResolveAppearance(string sourceBotType)
    {
        if (!databaseService.GetBots().Types.TryGetValue(sourceBotType, out var botType) || botType?.BotAppearance is null)
        {
            throw new InvalidOperationException($"pmc_appearance_pool_missing_{sourceBotType}");
        }

        return botType.BotAppearance;
    }

    private bool TryResolveFixedHandsForBody(string bodyId, out string handsId)
    {
        handsId = string.Empty;
        object customizationTable = databaseService.GetCustomization();
        object? bodyTemplate = ResolveDictionaryValue(customizationTable, bodyId);
        if (bodyTemplate is null)
        {
            throw new InvalidOperationException($"customization_body_missing_{bodyId}");
        }

        string bodyName = ResolveText(bodyTemplate, "Name", "_name").Trim();
        if (string.IsNullOrWhiteSpace(bodyName))
        {
            throw new InvalidOperationException($"customization_body_name_missing_{bodyId}");
        }

        object configuration = databaseService.GetGlobals().Configuration;
        object? customization = ResolveMember(configuration, "Customization");
        if (customization is null)
        {
            throw new InvalidOperationException("customization_globals_authority_missing");
        }

        object? bodyRules = ResolveMember(customization, "Body");
        if (bodyRules is null)
        {
            throw new InvalidOperationException("customization_body_authority_missing");
        }

        object? bodyRule = ResolveDictionaryValue(bodyRules, bodyName);
        if (bodyRule is null || !ResolveBool(bodyRule, "IsNotRandom", "isNotRandom"))
        {
            return false;
        }

        handsId = ResolveText(bodyRule, "Hands", "hands").Trim();
        if (string.IsNullOrWhiteSpace(handsId))
        {
            throw new InvalidOperationException($"customization_body_fixed_hands_missing_{bodyId}");
        }

        return true;
    }

    private void EnsureCustomizationEntryExists(string customizationId)
    {
        if (ResolveDictionaryValue(databaseService.GetCustomization(), customizationId) is null)
        {
            throw new InvalidOperationException($"customization_entry_missing_{customizationId}");
        }
    }

    private static void EnsurePoolContains(IReadOnlyDictionary<MongoId, double> pool, string customizationId, string part)
    {
        if (!pool.Keys.Any(key => string.Equals(key.ToString(), customizationId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"operator_visual_identity_{part}_outside_side_pool");
        }
    }

    private static string SelectWeighted(IReadOnlyDictionary<MongoId, double> pool, string seedMaterial, string part)
    {
        WeightedCandidate[] candidates = pool
            .Select(pair => new WeightedCandidate(pair.Key.ToString(), Math.Max(0d, pair.Value)))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Id) && candidate.Weight > 0d)
            .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new InvalidOperationException($"appearance_pool_empty_{part}");
        }

        double total = candidates.Sum(candidate => candidate.Weight);
        double target = DeterministicUnit(seedMaterial, part) * total;
        double cumulative = 0d;
        foreach (WeightedCandidate candidate in candidates)
        {
            cumulative += candidate.Weight;
            if (target < cumulative)
            {
                return candidate.Id;
            }
        }

        return candidates[^1].Id;
    }

    private static string BuildSeedMaterial(string operatorId, string side, string visualFamily)
        => $"vanguard|operator-visual-identity|v1|{Canonical(operatorId, "unknown")}|{Canonical(side, "unknown")}|{Canonical(visualFamily, "default")}";

    private static double DeterministicUnit(string seedMaterial, params string[] parts)
    {
        string material = seedMaterial + "|" + string.Join("|", parts.Select(part => Canonical(part, "unknown")));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        ulong value = 0UL;
        for (int index = 0; index < sizeof(ulong); index++)
        {
            value = (value << 8) | hash[index];
        }
        return value / (double)ulong.MaxValue;
    }

    private static object? ResolveDictionaryValue(object source, string expectedKey)
    {
        if (source is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (string.Equals(entry.Key?.ToString(), expectedKey, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }
            return null;
        }

        if (source is IEnumerable enumerable)
        {
            foreach (object? entry in enumerable)
            {
                if (entry is null)
                {
                    continue;
                }

                object? key = ResolveMember(entry, "Key");
                if (string.Equals(key?.ToString(), expectedKey, StringComparison.OrdinalIgnoreCase))
                {
                    return ResolveMember(entry, "Value");
                }
            }
        }

        return null;
    }

    private static object? ResolveMember(object source, string name)
    {
        Type type = source.GetType();
        PropertyInfo? property = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (property is not null)
        {
            return property.GetValue(source);
        }

        FieldInfo? field = type.GetFields(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        return field?.GetValue(source);
    }

    private static string ResolveText(object source, params string[] names)
    {
        foreach (string name in names)
        {
            object? value = ResolveMember(source, name);
            if (value is not null && !string.IsNullOrWhiteSpace(value.ToString()))
            {
                return value.ToString()!;
            }
        }
        return string.Empty;
    }

    private static bool ResolveBool(object source, params string[] names)
    {
        foreach (string name in names)
        {
            object? value = ResolveMember(source, name);
            if (value is bool typed)
            {
                return typed;
            }
            if (value is not null && bool.TryParse(value.ToString(), out bool parsed))
            {
                return parsed;
            }
        }
        return false;
    }

    private static string NormalizeSide(string? side)
    {
        if (string.Equals(side, "Bear", StringComparison.OrdinalIgnoreCase))
        {
            return "Bear";
        }
        if (string.Equals(side, "Usec", StringComparison.OrdinalIgnoreCase))
        {
            return "Usec";
        }
        throw new InvalidOperationException("operator_visual_identity_side_unsupported");
    }

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Canonical(string? value, string fallback)
        => Normalize(value, fallback).ToLowerInvariant();

    private sealed record WeightedCandidate(string Id, double Weight);
}
