using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using Vanguard.Server.Diagnostics;

// Responsibility: Coordinates Eft Experience Curve Service for the Operator domain services, delegating specialized work to its collaborators.
// Flow: Caller/route input is validated and normalized, canonical Operator/profile state is read or updated through the owning store/integration, then a response and diagnostics are produced.
// Authority boundary: Server domain orchestration only; persistent truth remains explicit in the Operator/SPT stores and client in-raid execution remains separate.
// Invariant: Operations stay profile-scoped, deterministic/idempotent where required, and partial failures do not silently corrupt canonical state.
namespace Vanguard.Server.Operators.Services;

/// <summary>
/// Resolves the cumulative EFT level experience table from the typed SPT 4.0 server model.
/// This server-side resolver intentionally uses the server DTO path instead of mirroring EFT client member names.
/// A loud fallback keeps boot safe but is never authoritative for UI coherence or promotion.
/// </summary>
[Injectable(InjectionType.Singleton)]
public sealed class VanguardEftExperienceCurveService(
    DatabaseService databaseService,
    ISptLogger<VanguardEftExperienceCurveService> logger)
{
    public const string StatusTag = "VANGUARD_EFT_XP_CURVE_STATUS";
    public const string AuthoritativeSource = "spt_globals_configuration_exp_level_exp_table";
    public const string FallbackSource = "legacy_safe_fallback_1200";

    private readonly object sync = new();
    private int[]? cachedTable;
    private bool resolutionAttempted;
    private bool fallbackLogged;

    public bool EnsureResolved() => GetTable().Length > 0;

    public int CreateExperienceForLevel(Random random, int requestedLevel)
    {
        int level = Math.Max(requestedLevel, 1);
        var table = GetTable();
        if (table.Length == 0)
        {
            LogFallbackOnce();
            return Math.Max(0, (level - 1) * 1200 + random.Next(0, 1200));
        }

        level = Math.Min(level, table.Length);
        int minimum = GetExperienceForLevel(table, level);
        // The terminal EFT level has no next-level XP window: keep XP at the authoritative cumulative floor.
        if (level >= table.Length)
        {
            return minimum;
        }

        int next = GetExperienceForLevel(table, level + 1);
        return next > minimum + 1 ? random.Next(minimum, next) : minimum;
    }

    public VanguardOperatorExperienceWindow ResolveLevelWindow(int requestedLevel)
    {
        int level = Math.Max(requestedLevel, 1);
        var table = GetTable();
        if (table.Length == 0)
        {
            LogFallbackOnce();
            return new VanguardOperatorExperienceWindow(
                level,
                Math.Max(0, (level - 1) * 1200),
                Math.Max(0, level * 1200),
                FallbackSource,
                false);
        }

        if (level > table.Length)
        {
            int cumulativeMax = GetExperienceForLevel(table, table.Length);
            return new VanguardOperatorExperienceWindow(
                table.Length,
                cumulativeMax,
                cumulativeMax,
                AuthoritativeSource,
                true);
        }

        int minimum = GetExperienceForLevel(table, level);
        int next = level < table.Length ? GetExperienceForLevel(table, level + 1) : minimum;
        return new VanguardOperatorExperienceWindow(
            level,
            minimum,
            next,
            AuthoritativeSource,
            true);
    }

    public VanguardOperatorExperienceWindow ResolveWindow(int experience)
    {
        int normalized = Math.Max(experience, 0);
        var table = GetTable();
        if (table.Length == 0)
        {
            int level = Math.Max(1, normalized / 1200 + 1);
            return new VanguardOperatorExperienceWindow(
                level,
                (level - 1) * 1200,
                level * 1200,
                FallbackSource,
                false);
        }

        int cumulative = 0;
        for (int index = 0; index < table.Length; index++)
        {
            int floor = cumulative;
            cumulative = SaturatingAdd(cumulative, Math.Max(table[index], 0));
            if (normalized < cumulative)
            {
                return new VanguardOperatorExperienceWindow(index, floor, cumulative, AuthoritativeSource, true);
            }
        }

        return new VanguardOperatorExperienceWindow(table.Length, cumulative, cumulative, AuthoritativeSource, true);
    }

    private int[] GetTable()
    {
        lock (sync)
        {
            if (resolutionAttempted)
            {
                return cachedTable ?? Array.Empty<int>();
            }

            resolutionAttempted = true;
            try
            {
                var experienceTable = databaseService.GetGlobals().Configuration.Exp.Level.ExperienceTable;
                var values = experienceTable
                    .Select(entry => Math.Max(entry.Experience, 0))
                    .ToArray();

                if (values.Length > 0 && values.Any(value => value > 0))
                {
                    cachedTable = values;
                    return cachedTable;
                }

                logger.Warning(VanguardServerDiagnosticsLog.Present(
                    $"[{StatusTag}] source=typed_path_empty; entries={values.Length}; fallback=true; authoritative=false; action=do_not_promote_xp_curve; expectedPath=GetGlobals.Configuration.Exp.Level.ExperienceTable; tag={StatusTag}"));
            }
            catch (Exception exception)
            {
                logger.Warning(VanguardServerDiagnosticsLog.Present(
                    $"[{StatusTag}] source=typed_path_exception; type={exception.GetType().Name}; fallback=true; authoritative=false; action=do_not_promote_xp_curve; expectedPath=GetGlobals.Configuration.Exp.Level.ExperienceTable; tag={StatusTag}"));
            }

            cachedTable = Array.Empty<int>();
            return cachedTable;
        }
    }

    private static int GetExperienceForLevel(IReadOnlyList<int> table, int level)
    {
        int clamped = Math.Clamp(level, 1, table.Count);
        int total = 0;
        for (int index = 0; index < clamped; index++)
        {
            total = SaturatingAdd(total, Math.Max(table[index], 0));
        }
        return total;
    }

    private static int SaturatingAdd(int left, int right) => left > int.MaxValue - right ? int.MaxValue : left + right;

    private void LogFallbackOnce()
    {
        lock (sync)
        {
            if (fallbackLogged) return;
            fallbackLogged = true;
            logger.Warning(VanguardServerDiagnosticsLog.Present(
                $"[{StatusTag}] contract=CreateExperienceForLevel; source={FallbackSource}; fallback=true; authoritative=false; note=boot_safe_only_never_ui_coherent_never_promotable; tag={StatusTag}"));
        }
    }
}

public sealed record VanguardOperatorExperienceWindow(
    int Level,
    int CurrentLevelFloorExperience,
    int NextLevelExperience,
    string Source,
    bool IsAuthoritative);
