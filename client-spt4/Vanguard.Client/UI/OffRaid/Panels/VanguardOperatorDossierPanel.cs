using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vanguard.Client.Api;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.UI.OffRaid.Foundation;
using Vanguard.Client.UI.OffRaid.Localization;

// Responsibility: Renders the detailed Off-Raid Operator dossier, including identity, service/career history, relationship-development data and contextual actions.
// Flow: The panel consumes a canonical Operator view, formats player-facing sections and delegates actions/navigation to the controller/API rather than reading persistence files directly.
// Authority boundary: Presentation only; canonical Operator, career, billing and medical authority remains in server projections.
// Invariant: The dossier never invents unavailable raid facts, keeps the unified Career presentation, and clearly labels incomplete relationship functionality.
namespace Vanguard.Client.UI.OffRaid.Panels;

internal sealed class VanguardOperatorDossierPanel
{
    public VanguardOffRaidPanelModel Build(VanguardOperatorStateView state, string? operatorId, Action backToActiveService, Action<string?, string?> openInventory, Action<string?, bool, bool> setLootTargets)
    {
        VanguardOperatorProfileDto? profile = state.Operators.FirstOrDefault(candidate => string.Equals(candidate.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
        VanguardOperatorMedicalProjectionDto? medical = state.MedicalProjections.FirstOrDefault(candidate => string.Equals(candidate.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
        VanguardOperatorRaidProjectionDto? raid = state.RaidProjections.FirstOrDefault(candidate => string.Equals(candidate.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
        VanguardOperatorServiceProjectionDto? service = state.ServiceProjections.FirstOrDefault(candidate => string.Equals(candidate.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
        VanguardOperatorCareerProjectionDto? verifiedCareer = state.CareerProjection.Operators?.FirstOrDefault(candidate => string.Equals(candidate.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
        VanguardOperatorCanonicalRaidHistoryDto? canonicalRaidHistory = state.CanonicalRaidHistory.Operators?.FirstOrDefault(candidate => string.Equals(candidate.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
        VanguardOperatorContactRecordDto? contact = state.Contacts.FirstOrDefault(candidate => string.Equals(candidate.OperatorId, operatorId, StringComparison.OrdinalIgnoreCase));
        VanguardOperatorCareerDto? career = profile?.Career;

        string displayName = VanguardUiText.Safe(profile?.Identity?.DisplayName, service?.DisplayName, raid?.DisplayName, medical?.DisplayName, L("general.unknown_operator"));
        string side = VanguardUiText.Faction(profile?.Identity?.Side ?? service?.Side ?? raid?.Side);
        string role = VanguardUiText.Role(profile?.Role ?? service?.Role ?? raid?.Role, profile?.Specialty ?? service?.Specialty ?? raid?.Specialty);
        int level = profile?.Progression?.Level ?? service?.Level ?? raid?.Level ?? medical?.Level ?? 0;
        int healthPercent = medical == null ? 100 : Math.Max(0, Math.Min(100, (int)Math.Round(medical.CurrentHealthRatio * 100.0)));
        (bool corpsesEnabled, bool containersEnabled) = ResolveLootPolicy(profile?.LootTargetPolicy);
        string lootPolicyLabel = LootPolicyLabel(profile?.LootTargetPolicy);
        int totalExperience = profile?.Progression?.Experience ?? service?.Experience ?? 0;
        bool xpCommitActive = career?.XpCommitState is not null;
        string totalExperienceLabel = xpCommitActive || career?.ExperienceReconciliation is null
            ? L("label.total_xp")
            : L("label.baseline_xp");
        string levelProgress = BuildLevelProgress(service);
        string trackedExperienceLabel = career?.XpCommitState?.LifetimeCoverageFromEnrollment == true
            ? L("label.xp_since_enrollment")
            : xpCommitActive
                ? L("label.xp_since_commit_activation")
                : string.Equals(career?.HistoryCompleteness, "complete_since_enrollment", StringComparison.OrdinalIgnoreCase)
                    ? L("label.xp_since_enrollment")
                    : L("label.tracked_career_xp");
        string trackedExperienceValue = $"+{Math.Max(0L, career?.ExperienceEarnedSinceEnrollment ?? 0L):N0}" +
            (!xpCommitActive && IsPartialMigratedHistory(career?.HistoryCompleteness) ? L("dossier.xp.tracked_since_activation") : string.Empty);
        VanguardCanonicalRaidHistoryEntryDto? latestRecordedRaid = GetChronologicalParticipatedRaids(canonicalRaidHistory).FirstOrDefault();

        var body = new StringBuilder();
        body.AppendLine(F("dossier.body.title", displayName));
        body.AppendLine(L("dossier.body.subtitle"));

        var sections = new List<VanguardInfoSectionModel>
        {
            new()
            {
                Title = L("dossier.section.identity"),
                Rows = new List<VanguardInfoRowModel>
                {
                    new() { Label = "OperatorId", Value = VanguardUiText.Safe(operatorId, L("general.undefined")) },
                    new() { Label = L("label.faction"), Value = side },
                    new() { Label = L("label.role"), Value = role },
                    new() { Label = L("label.level"), Value = level.ToString() },
                    new() { Label = L("label.visual_family"), Value = VanguardUiText.Value(profile?.Identity?.VisualFamily ?? service?.VisualFamily, L("general.undefined_fem")) }
                }
            },
            new()
            {
                Title = L("dossier.section.service"),
                Rows = new List<VanguardInfoRowModel>
                {
                    new() { Label = L("label.contract"), Value = VanguardUiText.Value(profile?.ContractStatus, L("general.undefined")) },
                    new() { Label = L("label.service"), Value = VanguardUiText.Value(profile?.ServiceStatus ?? service?.ServiceStatus, L("general.undefined")) },
                    new() { Label = L("label.service_state"), Value = (service?.IsSelectedForRaid ?? raid?.IsSelectedForRaid ?? false) ? L("general.active") : L("general.rest") },
                    new() { Label = L("label.raid_availability"), Value = EligibilityLabel(service?.EligibilityReason ?? raid?.EligibilityReason) },
                    new() { Label = L("label.salary_per_raid"), Value = VanguardUiText.Money(profile?.SalaryPerRaid ?? service?.SalaryPerRaid ?? 0) }
                }
            },
            new()
            {
                Title = L("dossier.section.medical"),
                Rows = new List<VanguardInfoRowModel>
                {
                    new() { Label = L("label.status"), Value = VanguardUiText.Value(medical?.MedicalStatus, L("general.undefined")) },
                    new() { Label = L("label.health"), Value = $"{healthPercent}%" },
                    new() { Label = L("label.recovery"), Value = VanguardUiText.Value(medical?.RecoveryState, L("general.none_fem")) },
                    new() { Label = L("label.injury"), Value = VanguardUiText.Value(medical?.InjurySummary, L("general.no_details")) }
                }
            },
            new()
            {
                Title = L("dossier.section.loot"),
                Rows = new List<VanguardInfoRowModel>
                {
                    new()
                    {
                        Label = L("label.corpses"),
                        Value = corpsesEnabled ? L("general.allowed") : L("general.forbidden"),
                        Checked = corpsesEnabled,
                        Enabled = !string.IsNullOrWhiteSpace(operatorId),
                        SetChecked = value => setLootTargets(operatorId, value, containersEnabled)
                    },
                    new()
                    {
                        Label = L("label.containers"),
                        Value = containersEnabled ? L("general.allowed") : L("general.forbidden"),
                        Checked = containersEnabled,
                        Enabled = !string.IsNullOrWhiteSpace(operatorId),
                        SetChecked = value => setLootTargets(operatorId, corpsesEnabled, value)
                    },
                    new() { Label = L("label.effective_mode"), Value = lootPolicyLabel }
                }
            },
            new()
            {
                Title = L("dossier.section.operational_profile"),
                Rows = new List<VanguardInfoRowModel>
                {
                    new() { Label = L("label.persona"), Value = VanguardUiText.Value(profile?.Persona?.BasePersona ?? service?.PersonaKey, profile?.Persona?.Temperament ?? service?.Temperament, L("general.undefined")) },
                    new() { Label = L("label.doctrine"), Value = VanguardUiText.Value(profile?.Persona?.Doctrine ?? service?.Doctrine, L("general.undefined_fem")) },
                    new() { Label = L("label.style"), Value = VanguardUiText.Value(profile?.Persona?.CombatStyle, L("general.undefined")) },
                    new() { Label = L("label.range"), Value = VanguardUiText.Range(profile?.Persona?.EngagementRange) },
                    new() { Label = L("label.squad_role"), Value = VanguardUiText.SquadRole(profile?.Persona?.SquadRole) },
                    new() { Label = L("label.summary"), Value = BuildPlayerBehaviorSummary(profile, service) },
                    new() { Label = L("label.traits"), Value = VanguardUiText.Traits(profile?.Persona?.Traits ?? service?.Traits) }
                }
            },
            new()
            {
                Title = L("dossier.section.career"),
                Rows = new List<VanguardInfoRowModel>
                {
                    new() { Label = L("label.scope"), Value = F("dossier.career.cumulative_scope", verifiedCareer?.VerifiedRaidCount ?? 0), Emphasized = true, WrapValue = true, Height = 26f },
                    new() { Label = L("label.latest_raid_recorded"), Value = BuildLatestRecordedRaidValue(latestRecordedRaid) },
                    new() { Label = totalExperienceLabel, Value = $"{Math.Max(totalExperience, 0):N0}" },
                    new() { Label = L("label.progression_level"), Value = levelProgress },
                    new() { Label = trackedExperienceLabel, Value = trackedExperienceValue },
                    new() { Label = L("label.raids_survivals_kia"), Value = $"{verifiedCareer?.VerifiedRaidCount ?? 0} / {verifiedCareer?.VerifiedSurvivedRaidCount ?? 0} / {verifiedCareer?.VerifiedKiaCount ?? 0}" },
                    new() { Label = L("label.kills"), Value = (verifiedCareer?.VerifiedKillCount ?? 0).ToString() },
                    new() { Label = L("label.confirmed_victims"), Value = BuildConfirmedVictims(verifiedCareer?.ConfirmedVictims), WrapValue = true, Height = 36f },
                    new() { Label = L("label.killed_by"), Value = BuildConfirmedDeathSources(verifiedCareer?.ConfirmedDeathSources), WrapValue = true, Height = 36f },
                    new() { Label = L("label.session_skill_points"), Value = BuildSkillSessionSummary(verifiedCareer), WrapValue = true, Height = 36f }
                }
            },
            new()
            {
                Title = L("dossier.section.raid_history"),
                Rows = BuildCanonicalRaidHistoryRows(canonicalRaidHistory)
            },
            new()
            {
                Title = L("dossier.section.relationship"),
                Rows = new List<VanguardInfoRowModel>
                {
                    new() { Label = L("label.trust_loyalty_respect"), Value = $"{profile?.Progression?.Trust ?? contact?.Trust ?? 0} / {profile?.Progression?.Loyalty ?? contact?.Loyalty ?? 0} / {profile?.Progression?.Respect ?? contact?.Respect ?? 0}" }
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(contact?.NarrativeSummary))
        {
            sections.Add(new VanguardInfoSectionModel
            {
                Title = L("dossier.section.narrative"),
                Rows = new List<VanguardInfoRowModel>
                {
                    new() { Label = L("label.summary"), Value = contact.NarrativeSummary! }
                }
            });
        }

        return new VanguardOffRaidPanelModel
        {
            Title = F("dossier.title", displayName),
            Subtitle = L("dossier.subtitle"),
            Body = body.ToString(),
            InfoSections = sections,
            Actions = new List<VanguardOffRaidPanelAction>
            {
                new() { Label = L("action.equipment"), Execute = () => openInventory(operatorId, displayName), Enabled = !string.IsNullOrWhiteSpace(operatorId) },
                new() { Label = L("dossier.action.back_service"), Execute = backToActiveService }
            }
        };
    }

    private static (bool CorpsesEnabled, bool ContainersEnabled) ResolveLootPolicy(string? value)
    {
        string policy = string.IsNullOrWhiteSpace(value) ? "CorpsesOnly" : value.Trim();
        if (string.Equals(policy, "ContainersOnly", StringComparison.OrdinalIgnoreCase)) return (false, true);
        if (string.Equals(policy, "CorpsesAndContainers", StringComparison.OrdinalIgnoreCase)) return (true, true);
        if (string.Equals(policy, "Disabled", StringComparison.OrdinalIgnoreCase)) return (false, false);
        return (true, false);
    }

    private static string LootPolicyLabel(string? value)
    {
        string policy = string.IsNullOrWhiteSpace(value) ? "CorpsesOnly" : value.Trim();
        if (string.Equals(policy, "ContainersOnly", StringComparison.OrdinalIgnoreCase)) return L("dossier.loot.containers_only");
        if (string.Equals(policy, "CorpsesAndContainers", StringComparison.OrdinalIgnoreCase)) return L("dossier.loot.both");
        if (string.Equals(policy, "Disabled", StringComparison.OrdinalIgnoreCase)) return L("dossier.loot.disabled");
        return L("dossier.loot.corpses_only");
    }

    private static string BuildPlayerBehaviorSummary(VanguardOperatorProfileDto? profile, VanguardOperatorServiceProjectionDto? service)
    {
        string persona = VanguardUiText.Value(profile?.Persona?.BasePersona ?? service?.PersonaKey, profile?.Persona?.Temperament ?? service?.Temperament, L("general.operator"));
        string style = VanguardUiText.Value(profile?.Persona?.CombatStyle, L("general.adaptive_style"));
        string range = VanguardUiText.Range(profile?.Persona?.EngagementRange);
        string squadRole = VanguardUiText.SquadRole(profile?.Persona?.SquadRole);
        return F("dossier.behavior", persona, style, range, squadRole);
    }

    private static string BuildLevelProgress(VanguardOperatorServiceProjectionDto? service)
    {
        if (service == null)
        {
            return L("dossier.level_curve.resolve");
        }

        if (string.Equals(service.ExperienceProgressState, "eft_curve_unresolved", StringComparison.OrdinalIgnoreCase)
            || (service.ExperienceCurveSource?.Contains("fallback", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return L("dossier.level_curve.unavailable");
        }

        if (!service.ExperienceLevelCoherent)
        {
            return L("dossier.level_curve.legacy_unreconciled");
        }

        if (service.ExperienceRequiredForNextLevel <= 0)
        {
            return string.Equals(service.ExperienceProgressState, "eft_curve_max_level", StringComparison.OrdinalIgnoreCase)
                ? L("dossier.level_curve.max")
                : L("dossier.level_curve.resolve");
        }

        int into = Math.Max(0, Math.Min(service.ExperienceIntoLevel, service.ExperienceRequiredForNextLevel));
        int required = Math.Max(1, service.ExperienceRequiredForNextLevel);
        int percent = Math.Max(0, Math.Min(100, (int)Math.Round(into * 100.0 / required)));
        return F("dossier.level_progress", into, required, percent, service.NextLevelExperience);
    }


    private static List<VanguardInfoRowModel> BuildCanonicalRaidHistoryRows(VanguardOperatorCanonicalRaidHistoryDto? history)
    {
        List<VanguardCanonicalRaidHistoryEntryDto> participatedRaids = GetChronologicalParticipatedRaids(history);
        if (participatedRaids.Count == 0)
        {
            return new List<VanguardInfoRowModel>
            {
                new() { Label = L("label.raid_records"), Value = L("dossier.history.none") }
            };
        }

        var rows = new List<VanguardInfoRowModel>();
        int shownCount = Math.Min(4, participatedRaids.Count);
        for (int index = 0; index < shownCount; index++)
        {
            VanguardCanonicalRaidHistoryEntryDto raid = participatedRaids[index];
            int killCount = raid.ConfirmedKills?.Count ?? 0;
            string outcome = raid.Died ? "KIA" : L("dossier.history.survival");
            rows.Add(new VanguardInfoRowModel
            {
                Label = BuildRaidHeaderLabel(index, raid),
                Value = F("dossier.history.raid_result", outcome, killCount),
                Emphasized = true,
                Height = 24f
            });

            string victims = BuildRaidVictims(raid.ConfirmedKills);
            if (!string.IsNullOrWhiteSpace(victims))
            {
                rows.Add(new VanguardInfoRowModel
                {
                    Label = L("dossier.history.victims"),
                    Value = victims,
                    IndentLevel = 1,
                    WrapValue = true,
                    Height = 34f
                });
            }

            if (raid.Died)
            {
                string deathSource = raid.Death == null
                    ? string.Empty
                    : raid.Death.SelfInflicted
                        ? L("dossier.history.self_inflicted_plain")
                        : F("dossier.history.killed_by_plain", CombatantDisplay(raid.Death.KillerDisplayName, raid.Death.KillerSide, raid.Death.KillerRawRole));
                string terminal = BuildTerminalDeathSummary(raid).TrimStart(' ', '·');
                string details = string.Join(" · ", new[] { deathSource, terminal }.Where(value => !string.IsNullOrWhiteSpace(value)));
                if (!string.IsNullOrWhiteSpace(details))
                {
                    rows.Add(new VanguardInfoRowModel
                    {
                        Label = L("dossier.history.death_details"),
                        Value = details,
                        IndentLevel = 1,
                        WrapValue = true,
                        Height = 34f
                    });
                }
            }

            string raidSkills = BuildRaidSkillSummary(raid.SkillSessionPoints);
            if (!string.IsNullOrWhiteSpace(raidSkills))
            {
                rows.Add(new VanguardInfoRowModel
                {
                    Label = L("dossier.history.skills_gained"),
                    Value = raidSkills,
                    IndentLevel = 1,
                    WrapValue = true,
                    Height = 34f
                });
            }

            AppendNotableEventRows(rows, raid.NotableEvents);
        }

        int remaining = participatedRaids.Count - shownCount;
        if (remaining > 0)
        {
            rows.Add(new VanguardInfoRowModel
            {
                Label = L("dossier.history.more_label"),
                Value = F("dossier.history.more_value", remaining)
            });
        }

        return rows;
    }

    // The server already emits newest-first history, but the client repeats the same deterministic ordering
    // at the presentation boundary. This protects readability if an older server response or cached payload
    // arrives out of order. Ledger commit time is the strongest persisted ordering fact; exit observation is
    // only a fallback and is never presented as an authoritative raid-start timestamp.
    private static List<VanguardCanonicalRaidHistoryEntryDto> GetChronologicalParticipatedRaids(VanguardOperatorCanonicalRaidHistoryDto? history)
    {
        return history?.Raids?
            .Where(raid => raid.Participated)
            .OrderByDescending(ResolveRaidSortTimestamp)
            .ThenByDescending(raid => raid.ExitBoundaryObservedAtUtcTelemetry)
            .ThenBy(raid => raid.SourceLedgerEntryId, StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<VanguardCanonicalRaidHistoryEntryDto>();
    }

    // Centralize timestamp preference so Career's "latest raid" and Raid History cannot disagree about
    // which raid is newest. A missing timestamp remains explicitly unknown rather than being invented.
    private static DateTimeOffset ResolveRaidSortTimestamp(VanguardCanonicalRaidHistoryEntryDto raid)
    {
        if (raid.LedgerCommittedAtUtcTelemetry != default)
        {
            return raid.LedgerCommittedAtUtcTelemetry;
        }

        return raid.ExitBoundaryObservedAtUtcTelemetry != default
            ? raid.ExitBoundaryObservedAtUtcTelemetry
            : DateTimeOffset.MinValue;
    }

    private static string BuildLatestRecordedRaidValue(VanguardCanonicalRaidHistoryEntryDto? raid)
    {
        if (raid == null)
        {
            return L("dossier.history.timestamp_unavailable");
        }

        return F("dossier.history.recorded_at", FormatRaidTimestamp(ResolveRaidSortTimestamp(raid)));
    }

    private static string BuildRaidHeaderLabel(int index, VanguardCanonicalRaidHistoryEntryDto raid)
    {
        string position = index switch
        {
            0 => L("dossier.history.last_raid"),
            1 => L("dossier.history.previous_raid"),
            _ => F("dossier.history.older_raid", index)
        };
        return F("dossier.history.header_with_time", position, FormatRaidTimestamp(ResolveRaidSortTimestamp(raid)));
    }

    private static string FormatRaidTimestamp(DateTimeOffset timestamp)
    {
        if (timestamp == default || timestamp == DateTimeOffset.MinValue)
        {
            return L("dossier.history.timestamp_unavailable");
        }

        DateTimeOffset local = timestamp.ToLocalTime();
        string format = VanguardOperatorsLocalizationService.CurrentLanguage == VanguardPresentationLanguage.French
            ? "dd/MM/yyyy HH:mm"
            : "yyyy-MM-dd HH:mm";
        return local.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string BuildRaidVictims(List<VanguardCanonicalRaidHistoryKillDto>? kills)
    {
        if (kills == null || kills.Count == 0)
        {
            return string.Empty;
        }

        var grouped = kills
            .GroupBy(kill => new
            {
                Name = kill.TargetDisplayName ?? string.Empty,
                Side = kill.TargetSide ?? string.Empty,
                Role = kill.TargetRawRole ?? string.Empty
            })
            .Select(group => new
            {
                Display = CombatantDisplay(group.Key.Name, group.Key.Side, group.Key.Role),
                Count = group.Count()
            })
            .OrderByDescending(value => value.Count)
            .ThenBy(value => value.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();
        string shown = string.Join(" · ", grouped.Take(4).Select(value => $"{value.Display}{CountSuffix(value.Count)}"));
        return grouped.Count > 4 ? F("dossier.more_others", shown, grouped.Count - 4) : shown;
    }

    private static string BuildRaidSkillSummary(List<VanguardCanonicalRaidHistorySkillPointDto>? skills)
    {
        if (skills == null || skills.Count == 0)
        {
            return string.Empty;
        }

        var positive = skills
            .Where(skill => skill.PointsEarnedDuringSession > 0.0)
            .OrderByDescending(skill => skill.PointsEarnedDuringSession)
            .ThenBy(skill => skill.SkillId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (positive.Count == 0)
        {
            return string.Empty;
        }

        double total = positive.Sum(skill => skill.PointsEarnedDuringSession);
        string top = string.Join(" · ", positive.Take(4).Select(skill => $"{VanguardUiText.Value(skill.SkillId, L("general.undefined"))}: {skill.PointsEarnedDuringSession:0.##}"));
        return F("dossier.skill.total", total) + (string.IsNullOrWhiteSpace(top) ? string.Empty : $" — {top}");
    }

    // Notable events are deliberately rendered as children of one raid, never as free-standing history rows.
    // This preserves the causal/temporal association needed by later VisitAPI and relationship projections.
    // The renderer receives structured facts; localization/narrative wording remains a presentation concern.
    private static void AppendNotableEventRows(
        List<VanguardInfoRowModel> rows,
        List<VanguardCanonicalRaidHistoryNotableEventDto>? notableEvents)
    {
        if (notableEvents == null || notableEvents.Count == 0)
        {
            return;
        }

        foreach (VanguardCanonicalRaidHistoryNotableEventDto notableEvent in notableEvents
                     .OrderBy(value => value.ObservedAtUtcTelemetry)
                     .ThenBy(value => value.EventId, StringComparer.OrdinalIgnoreCase))
        {
            string summary = BuildNotableEventSummary(notableEvent);
            if (string.IsNullOrWhiteSpace(summary))
            {
                continue;
            }

            rows.Add(new VanguardInfoRowModel
            {
                Label = L("dossier.history.notable_event"),
                Value = summary,
                IndentLevel = 1,
                WrapValue = true,
                FullWidthValue = true,
                Height = 40f
            });
        }
    }

    private static string BuildNotableEventSummary(VanguardCanonicalRaidHistoryNotableEventDto notableEvent)
    {
        string kind = VanguardUiText.Value(notableEvent.Kind, L("dossier.history.notable_event_unknown"));
        string actors = string.Join(", ", notableEvent.Actors?
            .Select(actor => VanguardUiText.Value(actor.DisplayName, actor.OperatorId, actor.ProfileId, string.Empty))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            ?? Enumerable.Empty<string>());
        return string.IsNullOrWhiteSpace(actors) ? kind : $"{kind} · {actors}";
    }

    private static string BuildTerminalDeathSummary(VanguardCanonicalRaidHistoryEntryDto raid)
    {
        VanguardCanonicalRaidHistoryTerminalDeathTruthDto? terminal = raid.TerminalDeathTruth;
        if (terminal == null)
        {
            return string.Empty;
        }

        string mechanism = TerminalDamageLabel(terminal.TerminalDamageType);
        string bodyPart = LastDamageBodyPartLabel(terminal.LastDamageBodyPart);
        string result = F("dossier.terminal.mechanism", mechanism);
        if (!string.IsNullOrWhiteSpace(bodyPart))
        {
            result += F("dossier.terminal.last_zone", bodyPart);
        }

        return result;
    }

    private static string TerminalDamageLabel(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        return normalized switch
        {
            "heavybleeding" => L("damage.heavy_bleeding"),
            "lightbleeding" => L("damage.light_bleeding"),
            "bullet" => L("damage.bullet"),
            "explosion" => L("damage.explosion"),
            "fall" => L("damage.fall"),
            "barbed" => L("damage.barbed"),
            "dehydration" => L("damage.dehydration"),
            "exhaustion" => L("damage.exhaustion"),
            "poison" => L("damage.poison"),
            "radexposure" => L("damage.rad_exposure"),
            "lethaltoxin" => L("damage.lethal_toxin"),
            "grenadefragment" => L("damage.grenade_fragment"),
            "landmine" => L("damage.landmine"),
            "artillery" => L("damage.artillery"),
            "thermobaricexplosion" => L("damage.thermobaric"),
            "environment" => L("damage.environment"),
            _ => VanguardUiText.Value(value, L("dossier.eligibility.unknown"))
        };
    }

    private static string LastDamageBodyPartLabel(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        return normalized switch
        {
            "head" => L("body.head"),
            "chest" => L("body.chest"),
            "stomach" => L("body.stomach"),
            "leftarm" => L("body.left_arm"),
            "rightarm" => L("body.right_arm"),
            "leftleg" => L("body.left_leg"),
            "rightleg" => L("body.right_leg"),
            "common" => L("body.common"),
            "" => string.Empty,
            _ => VanguardUiText.Value(value, string.Empty)
        };
    }

    private static string BuildConfirmedVictims(List<VanguardCareerNamedCombatantProjectionDto>? victims)
    {
        if (victims == null || victims.Count == 0)
        {
            return L("dossier.none_verified");
        }

        var ordered = victims
            .Where(value => value.Count > 0)
            .OrderByDescending(value => value.Count)
            .ThenBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        string shown = string.Join(" · ", ordered.Take(4)
            .Select(value => $"{CombatantDisplay(value.DisplayName, value.Side, value.RawRole)}{CountSuffix(value.Count)}"));
        return ordered.Count > 4 ? F("dossier.more_others", shown, ordered.Count - 4) : shown;
    }

    private static string BuildConfirmedDeathSources(List<VanguardCareerDeathSourceProjectionDto>? sources)
    {
        if (sources == null || sources.Count == 0)
        {
            return L("dossier.none_verified");
        }

        var ordered = sources
            .Where(value => value.Count > 0)
            .OrderByDescending(value => value.Count)
            .ThenBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        string shown = string.Join(" · ", ordered.Take(4)
            .Select(value => value.SelfInflicted
                ? F("dossier.self_inflicted_count", CountSuffix(value.Count))
                : $"{CombatantDisplay(value.DisplayName, value.Side, value.RawRole)}{CountSuffix(value.Count)}"));
        return ordered.Count > 4 ? F("dossier.more_others", shown, ordered.Count - 4) : shown;
    }

    private static string CombatantDisplay(string? displayName, string? side, string? rawRole)
    {
        string name = VanguardUiText.Value(displayName, string.Empty);
        string kind = FormatCombatantKind(side, rawRole);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "none", StringComparison.OrdinalIgnoreCase))
        {
            return kind;
        }

        return string.IsNullOrWhiteSpace(kind) ? name : $"{name} ({kind})";
    }

    private static string FormatCombatantKind(string? side, string? rawRole)
    {
        string sideValue = side?.Trim() ?? string.Empty;
        if (sideValue.Equals("Usec", StringComparison.OrdinalIgnoreCase)) return "PMC USEC";
        if (sideValue.Equals("Bear", StringComparison.OrdinalIgnoreCase)) return "PMC BEAR";

        string role = rawRole?.Trim() ?? string.Empty;
        if (role.Equals("assault", StringComparison.OrdinalIgnoreCase)) return L("combatant.scav");
        if (role.Equals("marksman", StringComparison.OrdinalIgnoreCase)) return L("combatant.scav_sniper");
        if (role.StartsWith("boss", StringComparison.OrdinalIgnoreCase)) return L("combatant.boss");
        if (role.StartsWith("follower", StringComparison.OrdinalIgnoreCase)) return L("combat.guard");
        return string.Empty;
    }

    private static string CountSuffix(int count) => count > 1 ? $" ×{count}" : string.Empty;

    private static string BuildSkillSessionSummary(VanguardOperatorCareerProjectionDto? projection)
    {
        if (projection?.SkillSessionPointsEarnedBySkill == null || projection.SkillSessionPointsEarnedBySkill.Count == 0)
        {
            return L("dossier.skill.none");
        }

        string top = string.Join(" · ", projection.SkillSessionPointsEarnedBySkill
            .Where(pair => pair.Value > 0.0)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .Select(pair => $"{pair.Key}: {pair.Value:0.##}"));
        return F("dossier.skill.total", projection.SkillSessionPointsEarnedTotal) + (string.IsNullOrWhiteSpace(top) ? string.Empty : $" — {top}");
    }

    private static string EligibilityLabel(string? reason)
    {
        return (reason ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "eligible" => L("general.available"),
            "not_in_active_service" => L("dossier.eligibility.not_active"),
            "already_deployed" => L("dossier.eligibility.already_deployed"),
            "service_unavailable" => L("general.unavailable"),
            "medical_recovery_active" => L("dossier.eligibility.medical_recovery"),
            "health_below_raid_minimum" => L("dossier.eligibility.low_health"),
            "" => L("dossier.eligibility.unknown"),
            _ => reason ?? L("dossier.eligibility.unknown"),
        };
    }


    private static bool IsPartialMigratedHistory(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return string.Equals(normalized, "partial_from_legacy_migration", StringComparison.OrdinalIgnoreCase)
            || (normalized.StartsWith("partial_from_", StringComparison.OrdinalIgnoreCase)
                && normalized.EndsWith("_migration", StringComparison.OrdinalIgnoreCase));
    }

    private static string L(string key) => VanguardOperatorsLocalizationService.Get(key);

    private static string F(string key, params object?[] args) => VanguardOperatorsLocalizationService.Format(key, args);

}
