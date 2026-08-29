#if SPT_CLIENT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Vanguard.Client.Api;
using Vanguard.Client.Api.Dtos;
using Vanguard.Client.Compatibility;
using Vanguard.Client.Diagnostics;
using Vanguard.Client.Raid.Interop;
using Vanguard.Client.Raid.Patches;
using Vanguard.Client.Raid.Runtime;
using Vanguard.Client.Runtime.Alliance;
using Vanguard.Client.Runtime.Integrations.MoreBots;
using Vanguard.Client.Runtime.Integrations.Sain;

// Responsibility: materializes the server-selected persistent Operators into raid bots, validates spawn placement and binds runtime identity/progression to the spawned instances.
// Flow: For each pending server snapshot, the service resolves the owning player, constructs and sanitizes the EFT profile, preloads required bundles, finds a safe reachable NavMesh spawn, creates the bot through the raid controller, then applies friendliness/group policy and registers the persistent Operator identity.
// Authority boundary: only the current Fika/local raid authority may spawn; persistent Operator data remains server truth and downstream AI systems own behavior after registration.
// Invariant: each selected Operator is spawned at most once from a validated nearby NavMesh position and receives its exact persisted identity/progression snapshot before runtime registration.

namespace Vanguard.Client.Raid.Services;

internal sealed class VanguardRaidOperatorSpawnService
{
    private const float SpawnStrictSampleRadiusMeters = 1.65f;
    private const float SpawnBalancedSampleRadiusMeters = 3.25f;
    private const float SpawnCapsuleRadiusMeters = 0.32f;
    private const float SpawnCapsuleHeightMeters = 1.72f;
    private const float SpawnStrictMaxProjectionDistanceMeters = 1.25f;
    private const float SpawnBalancedMaxProjectionDistanceMeters = 2.65f;
    private const float SpawnMinOwnerDistanceMeters = 1.35f;
    private const float SpawnStrictMaxOwnerDistanceMeters = 8.0f;
    private const float SpawnBalancedMaxOwnerDistanceMeters = 14.0f;
    private const float SpawnStrictMaxVerticalDeltaMeters = 0.75f;
    private const float SpawnBalancedMaxVerticalDeltaMeters = 1.25f;
    private const float SpawnLocalEgressProbeMeters = 1.25f;
    private const int SpawnStrictLocalEgressMinimumValidDirections = 2;
    private const int SpawnBalancedLocalEgressMinimumValidDirections = 1;
    private static readonly TimeSpan NavSafeRetryDelay = TimeSpan.FromSeconds(2.75);
    private static DateTimeOffset nextNavSafeRetryAt = DateTimeOffset.MinValue;
    private static readonly Dictionary<string, GeneratedOperatorProfile> GeneratedProfileCache = new(StringComparer.Ordinal);
    // ActivateBot can return before EFT runs the group/finalize callback on busy headless raids.
    // Keep the retry window long enough to avoid false pending warnings while staying bounded.
    private const int LateBindRetryCount = 24;
    private static readonly TimeSpan LateBindRetryDelay = TimeSpan.FromMilliseconds(250);

    private static readonly FieldInfo BotPresetsField = AccessTools.Field(typeof(BotCreatorClass), "Ginterface21_0");
    private static readonly FieldInfo SessionField = AccessTools.Field(typeof(BotsPresets), "ISession");
    private static readonly MethodInfo FinalizeSpawnMethod = AccessTools.Method(typeof(BotSpawner), "method_11");
    private static readonly MethodInfo ExistingBotsMethod = AccessTools.Method(typeof(BotSpawner), "method_5");


    public static void ResetStaticCachesForRaidLifecycle(string reason)
    {
        GeneratedProfileCache.Clear();
        nextNavSafeRetryAt = DateTimeOffset.MinValue;
        VanguardOperatorNativeSquadRegistry.ResetForRaidLifecycle(reason);
        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_RAID_SPAWN_STATUS",
            $"VANGUARD_HEADLESS_RAID_SESSION_CLEANUP spawn_static_cache_reset reason={reason}; generatedProfileCache=0; navSafeRetry=cleared; tag=VANGUARD_HOSTILE_INDOOR_MOVEMENT_PLAN_STATUS");
    }

    public async Task SpawnPendingOperatorsAsync(SynchronizationContext unityContext)
    {
        if (!VanguardFikaCompat.IsRaidAuthority)
        {
            VanguardClientDiagnosticsLog.Info("VANGUARD_OPERATOR_SPAWN_HEADLESS_AUTHORITY", $"spawn skipped reason=not_authority headless={VanguardFikaCompat.IsHeadless} client={VanguardFikaCompat.IsClient} host={VanguardFikaCompat.IsHost}");
            return;
        }

        var pendingOperators = VanguardRaidOperatorRuntimeRegistry.GetPendingForAuthority()
            .Where(snapshot => snapshot.IsSelectedForRaid && snapshot.IsEligibleForRaid)
            .ToArray();
        if (pendingOperators.Length == 0)
        {
            return;
        }

        if (VanguardBotsControllerStatePatch.ActiveController is not { } botsController)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_RAID_SPAWN_STATUS", "spawn deferred reason=bots_controller_unavailable");
            VanguardRaidOperatorController.QueueSpawn("bots_controller_unavailable_retry");
            return;
        }

        var botSpawner = botsController.BotSpawner;
        var botCreator = botSpawner.BotCreator ?? throw new InvalidOperationException("Bot creator is unavailable for Vanguard Operator spawn.");
        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_SPAWN_HEADLESS_AUTHORITY",
            $"spawn begin pending={pendingOperators.Length}; headless={VanguardFikaCompat.IsHeadless}; host={VanguardFikaCompat.IsHost}; ownerBinding=explicit_player_profile_id");

        var expectedOperatorsByOwner = pendingOperators
            .GroupBy(candidate => Normalize(candidate.OwnerProfileId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Math.Max(1, group.Count()), StringComparer.Ordinal);

        int spawned = 0;
        int runtimeRegistered = 0;
        for (int index = 0; index < pendingOperators.Length; index++)
        {
            var snapshot = pendingOperators[index];
            try
            {
                var ownerPlayer = ResolveOwnerPlayer(snapshot);
                if (ownerPlayer is null)
                {
                    VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_SPAWN_HEADLESS_AUTHORITY", $"operator={snapshot.OperatorId ?? "<none>"} skipped reason=owner_player_not_found owner={snapshot.OwnerProfileId ?? "<none>"}");
                    continue;
                }

                int ownerOperatorCount = expectedOperatorsByOwner.TryGetValue(Normalize(snapshot.OwnerProfileId), out var expectedForOwner)
                    ? expectedForOwner
                    : 1;
                var generatedProfile = await LoadOperatorProfileAsync(botCreator, ownerPlayer, snapshot, ownerOperatorCount);
                LogProfileEquipmentAudit(generatedProfile.Profile, snapshot, "profile_constructed_before_spawn");
                await PreloadProfileBundlesAsync(generatedProfile.Profile, snapshot);
                await VanguardUnityThread.ResumeOnAsync(unityContext);
                bool registered = await SpawnOperatorAsync(botsController, botSpawner, botCreator, ownerPlayer, snapshot, generatedProfile, ownerOperatorCount, index, unityContext);
                spawned++;
                if (registered)
                {
                    runtimeRegistered++;
                }
            }
            catch (VanguardSpawnPositionUnavailableException exception)
            {
                VanguardClientDiagnosticsLog.Warning(
                    "VANGUARD_RAID_SPAWN_SAFE_POSITION",
                    $"operator={snapshot.OperatorId ?? "<none>"} spawn_deferred reason=no_strict_navsafe_position detail={exception.Message}");
                QueueNavSafeRetry("no_strict_navsafe_position");
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(
                    "VANGUARD_RAID_SPAWN_STATUS",
                    $"operator={snapshot.OperatorId ?? "<none>"} spawn_failed type={exception.GetType().Name} message={exception.Message}");
            }
        }

        int remaining = VanguardRaidOperatorRuntimeRegistry.GetPendingForAuthority().Count;
        VanguardRuntimeBindGuardService.RecordSpawnSummary(spawned, runtimeRegistered, remaining, "spawn_pending_operators_complete");
        VanguardClientDiagnosticsLog.Info("VANGUARD_RAID_SPAWN_STATUS", $"spawn completed spawned={spawned}; runtimeRegistered={runtimeRegistered}; remaining={remaining}");
    }

    private static Player? ResolveOwnerPlayer(VanguardRaidOperatorSnapshotDto snapshot)
    {
        string ownerProfileId = Normalize(snapshot.OwnerProfileId);
        if (!string.IsNullOrWhiteSpace(ownerProfileId))
        {
            var ownerPlayer = VanguardFikaCompat.FindRaidPlayerByProfileId(ownerProfileId);
            if (ownerPlayer is not null)
            {
                return ownerPlayer;
            }
        }

        // Intentionally no silent fallback to the headless identity here. In a headless raid the
        // technical process can spawn the bot, but the owner must still be a player profile.
        return GamePlayerOwner.MyPlayer is { } localPlayer && string.Equals(localPlayer.ProfileId, ownerProfileId, StringComparison.Ordinal)
            ? localPlayer
            : null;
    }

    private static async Task<GeneratedOperatorProfile> LoadOperatorProfileAsync(
        IBotCreator botCreator,
        Player ownerPlayer,
        VanguardRaidOperatorSnapshotDto snapshot,
        int operatorCount)
    {
        string cacheKey = BuildGeneratedProfileCacheKey(snapshot);
        if (GeneratedProfileCache.TryGetValue(cacheKey, out var cachedProfile))
        {
            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_OPERATOR_PROFILE_GENERATED",
                $"profile cache hit operator={snapshot.OperatorId ?? "<none>"}; owner={snapshot.OwnerProfileId ?? "<none>"}; profile={cachedProfile.Profile.ProfileId}; action=reuse_for_spawn_retry");
            return cachedProfile;
        }

        var spawnParams = new BotSpawnParams
        {
            ShallBeGroup = new ShallBeGroupParams(true, false, Math.Max(2, operatorCount + 1)),
        };
        var generationRole = ResolveGenerationRole(ownerPlayer.Side);
        var profileData = new BotProfileDataClass(ownerPlayer.Side, generationRole, BotDifficulty.impossible, 0f, spawnParams, false);
        VanguardClientDiagnosticsLog.Info(
            VanguardOperatorBotTypes.StatusTag,
            $"VANGUARD_OPERATOR_GENERATION_ROLE operator={snapshot.OperatorId ?? "<none>"}; owner={snapshot.OwnerProfileId ?? "<none>"}; generationRole={generationRole}; reason=vanilla_generation_then_profile_retype; tag={VanguardOperatorBotTypes.StatusTag}");
        var conditions = profileData.PrepareToLoadBackend(1).ToList();
        var botPresets = BotPresetsField.GetValue(botCreator) as BotsPresets
            ?? throw new InvalidOperationException("Bot presets are unavailable for Vanguard Operator generation.");
        var profileEndpoint = SessionField.GetValue(botPresets) as ProfileEndpointFactoryAbstractClass
            ?? throw new InvalidOperationException("Profile endpoint is unavailable for Vanguard Operator generation.");

        var request = new LegacyParamsStruct
        {
            Url = profileEndpoint.Gclass1392_0.Main + VanguardApiRoutes.VanguardOperatorGenerate,
            Params = new Dictionary<string, object>
            {
                ["Info"] = new Class19<List<WaveInfoClass>>(conditions),
                ["OperatorId"] = snapshot.OperatorId ?? string.Empty,
                ["OwnerProfileId"] = snapshot.OwnerProfileId ?? string.Empty,
                ["RaidSessionId"] = snapshot.RaidSessionId ?? VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId ?? string.Empty,
            },
            Retries = LegacyParamsStruct.DefaultRetries,
        };

        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_PROFILE_GENERATED",
            $"requesting operator descriptor operator={snapshot.OperatorId ?? "<none>"}; owner={snapshot.OwnerProfileId ?? "<none>"}; route={VanguardApiRoutes.VanguardOperatorGenerate}; side={ownerPlayer.Side}");

        CompleteProfileDescriptorClass[]? generatedProfiles = await profileEndpoint.method_3<CompleteProfileDescriptorClass[]>(request);
        var descriptor = generatedProfiles?.FirstOrDefault()
            ?? throw new InvalidOperationException($"Vanguard Operator generation returned no profile for {snapshot.OperatorId ?? "<none>"}.");

        // audit subsystem safe debt cleanup: sanitize the generated profile descriptor before the
        // EFT Profile constructor instead of intentionally relying on ctor failure + retry.
        // The fallback path remains in ConstructOperatorProfileWithRepair, but normal
        // Operator spawn logs should no longer emit avoidable profile_ctor_failed noise.
        var safeDescriptor = TryBuildSanitizedDescriptor(descriptor, snapshot, out string preSanitizeReason) ?? descriptor;
        if (!ReferenceEquals(safeDescriptor, descriptor))
        {
            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_OPERATOR_PROFILE_GENERATED",
                $"profile_descriptor_pre_sanitized operator={snapshot.OperatorId ?? "<none>"}; reason={preSanitizeReason}; action=construct_profile_from_sanitized_descriptor");
        }

        var profile = ConstructOperatorProfileWithRepair(safeDescriptor, snapshot);
        var generated = new GeneratedOperatorProfile(profile, profileData);
        GeneratedProfileCache[cacheKey] = generated;
        return generated;
    }

    private static Profile ConstructOperatorProfileWithRepair(CompleteProfileDescriptorClass descriptor, VanguardRaidOperatorSnapshotDto snapshot)
    {
        try
        {
            return new Profile(descriptor);
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_PROFILE_GENERATED",
                $"profile_ctor_failed operator={snapshot.OperatorId ?? "<none>"}; type={root.GetType().Name}; message={root.Message}; action=retry_sanitized_descriptor; stack={CompactStack(root)}");

            CompleteProfileDescriptorClass? repaired = TryBuildSanitizedDescriptor(descriptor, snapshot, out string repairReason);
            if (repaired is null)
            {
                throw;
            }

            try
            {
                var repairedProfile = new Profile(repaired);
                VanguardClientDiagnosticsLog.Warning(
                    "VANGUARD_OPERATOR_PROFILE_GENERATED",
                    $"profile_ctor_repaired operator={snapshot.OperatorId ?? "<none>"}; reason={repairReason}; action=continue_spawn");
                return repairedProfile;
            }
            catch (Exception retryException)
            {
                Exception retryRoot = Unwrap(retryException);
                VanguardClientDiagnosticsLog.Warning(
                    "VANGUARD_OPERATOR_PROFILE_GENERATED",
                    $"profile_ctor_repair_failed operator={snapshot.OperatorId ?? "<none>"}; reason={repairReason}; type={retryRoot.GetType().Name}; message={retryRoot.Message}; stack={CompactStack(retryRoot)}");
                throw;
            }
        }
    }

    private static CompleteProfileDescriptorClass? TryBuildSanitizedDescriptor(CompleteProfileDescriptorClass descriptor, VanguardRaidOperatorSnapshotDto snapshot, out string reason)
    {
        reason = "unknown";
        try
        {
            string json = JsonConvert.SerializeObject(descriptor);
            JObject root = JObject.Parse(json);
            int beforeItems = ReadArray(ReadObject(root, "Inventory"), "items")?.Count ?? 0;

            StripDogTagCustomization(root);
            SanitizeInventoryReferences(EnsureObject(root, "Inventory"));

            int afterItems = ReadArray(ReadObject(root, "Inventory"), "items")?.Count ?? 0;
            reason = $"dogtag_removed_inventory_sanitized beforeItems={beforeItems}; afterItems={afterItems}";
            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_OPERATOR_PROFILE_GENERATED",
                $"profile_descriptor_sanitized operator={snapshot.OperatorId ?? "<none>"}; {reason}");
            return root.ToObject<CompleteProfileDescriptorClass>();
        }
        catch (Exception exception)
        {
            reason = exception.GetType().Name + ":" + exception.Message;
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_PROFILE_GENERATED",
                $"profile_descriptor_sanitize_failed operator={snapshot.OperatorId ?? "<none>"}; reason={reason}");
            return null;
        }
    }

    private static string BuildGeneratedProfileCacheKey(VanguardRaidOperatorSnapshotDto snapshot)
    {
        return string.Join(
            "|",
            Normalize(snapshot.RaidSessionId, VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId),
            Normalize(snapshot.OwnerProfileId),
            Normalize(snapshot.OperatorId));
    }

    private static async Task<bool> SpawnOperatorAsync(
        BotsController botsController,
        BotSpawner botSpawner,
        IBotCreator botCreator,
        Player ownerPlayer,
        VanguardRaidOperatorSnapshotDto snapshot,
        GeneratedOperatorProfile generatedProfile,
        int ownerOperatorCount,
        int index,
        SynchronizationContext unityContext)
    {
        var spawnPoint = ResolveSafeSpawnPoint(botsController, ownerPlayer, index);
        var botZone = botsController.GetClosestZone(spawnPoint.Position, out _)
            ?? throw new InvalidOperationException("Unable to find a bot zone near the player owner.");
        var corePoint = botsController.CoversData.GetClosest(spawnPoint.Position).CorePointInGame
            ?? throw new InvalidOperationException("Unable to find a valid core point near the player owner.");
        var botCreationData = BotCreationDataClass.CreateWithoutProfile(generatedProfile.ProfileData);
        string expectedProfileId = generatedProfile.Profile.ProfileId;
        VanguardRuntimeBindGuardService.BeginOperatorActivation(snapshot, expectedProfileId, botSpawner);
        bool activateBotTaskCompleted = false;
        try
        {
            VanguardRaidOperatorRuntimeRegistry.MarkExpectedBotProfile(snapshot, expectedProfileId, "spawn_operator_profile_pre_activate");
            PrepareProfileIdentity(generatedProfile.Profile, ownerPlayer);
            VanguardCoopStructuralSquadBinder.PrepareGeneratedProfile(generatedProfile.Profile, ownerPlayer, "spawn_profile_identity");
            botCreationData.AddPosition(spawnPoint.Position, corePoint.Id);
            botCreationData.AddProfile(generatedProfile.Profile);

            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_RAID_SPAWN_SAFE_POSITION",
                $"operator={snapshot.OperatorId ?? "<none>"}; owner={snapshot.OwnerProfileId ?? "<none>"}; profile={expectedProfileId}; candidate={spawnPoint.CandidateIndex}; reason={spawnPoint.Reason}; raw={spawnPoint.RawPosition}; projected={spawnPoint.Position}; distance={Vector3.Distance(ownerPlayer.Transform.position, spawnPoint.Position):0.00}; path={spawnPoint.PathStatus}; egress={spawnPoint.EgressStatus}");
            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_RAID_SPAWN_STATUS",
                $"spawn begin operator={snapshot.OperatorId ?? "<none>"}; owner={snapshot.OwnerProfileId ?? "<none>"}; profile={expectedProfileId}; zone={botZone.name}; position={spawnPoint.Position}; safePosition=true");

            await botCreator.ActivateBot(
                generatedProfile.Profile,
                botCreationData.GetPosition(),
                botZone,
                false,
                (owner, zone) =>
                {
                    VanguardRuntimeBindGuardService.RecordActivationStage(snapshot, expectedProfileId, "group_callback_entered", owner);
                    return CreateOperatorGroup(botSpawner, botsController, ownerPlayer, owner, zone, snapshot, ownerOperatorCount);
                },
                owner =>
                {
                    VanguardRuntimeBindGuardService.RecordActivationStage(snapshot, expectedProfileId, "finalize_callback_entered", owner);
                    FinalizeOperatorSpawn(botSpawner, botCreationData, ownerPlayer, owner, snapshot, ownerOperatorCount);
                },
                CancellationToken.None);

            activateBotTaskCompleted = true;
            VanguardRuntimeBindGuardService.RecordActivationStage(snapshot, expectedProfileId, "activate_bot_task_completed");
        }
        finally
        {
            VanguardRuntimeBindGuardService.EndOperatorActivation(
                snapshot,
                expectedProfileId,
                activateBotTaskCompleted,
                activateBotTaskCompleted ? "activate_bot_task_completed" : "activate_bot_scope_exited_before_completion");
        }

        return await EnsureLateRuntimeBindingAsync(botSpawner, botsController, snapshot, expectedProfileId, unityContext);
    }

    private static BotsGroup CreateOperatorGroup(
        BotSpawner botSpawner,
        BotsController botsController,
        Player ownerPlayer,
        BotOwner owner,
        BotZone zone,
        VanguardRaidOperatorSnapshotDto snapshot,
        int ownerOperatorCount)
    {
        PrepareFriendlyOwner(owner, ownerPlayer);
        var deadBodiesController = botSpawner.DeadBodiesController
            ?? throw new InvalidOperationException("Dead bodies controller is unavailable for Vanguard Operator group creation.");
        var allPlayers = botSpawner.AllPlayers ?? new List<Player>();
        if (!allPlayers.Contains(ownerPlayer))
        {
            allPlayers.Add(ownerPlayer);
        }

        var constructorPlayers = VanguardCoopStructuralSquadBinder.BuildConstructorPlayers(ownerPlayer, allPlayers, "operator_group_ctor");
        var activeEnemies = VanguardCoopStructuralSquadBinder.BuildConstructorEnemies(ownerPlayer, GetActiveEnemies(botSpawner, owner), "operator_group_ctor");

        VanguardOperatorBotsGroup CreateFreshGroup()
        {
            return new VanguardOperatorBotsGroup(
                zone,
                botsController.BotGame,
                owner,
                activeEnemies,
                deadBodiesController,
                constructorPlayers,
                ownerPlayer);
        }

        VanguardOperatorBotsGroup group;
        bool sharedGroupCreated;
        try
        {
            group = VanguardOperatorNativeSquadRegistry.GetOrCreate(
                Normalize(snapshot.RaidSessionId, VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId),
                ownerPlayer.ProfileId,
                ownerOperatorCount,
                CreateFreshGroup,
                out sharedGroupCreated);
        }
        catch (Exception exception)
        {
            // Cohesion enhancement is fail-open: a registry defect must never block an Operator spawn.
            // The individual-group fallback preserves the current stable runtime behavior for this member.
            group = CreateFreshGroup();
            sharedGroupCreated = true;
            VanguardClientDiagnosticsLog.Warning(
                VanguardOperatorNativeSquadRegistry.StatusTag,
                $"VANGUARD_NATIVE_SQUAD_FALLBACK_INDIVIDUAL operator={snapshot.OperatorId ?? "<none>"}; owner={ownerPlayer.ProfileId}; botProfile={owner.ProfileId}; reason={exception.GetType().Name}:{exception.Message}; spawnContinues=true; tag={VanguardOperatorNativeSquadRegistry.StatusTag}");
        }

        VanguardCoopStructuralSquadBinder.BindKnownFriendlies(owner, ownerPlayer, "operator_group_ctor");
        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_BRAIN_BIND_STATUS",
            $"operator_group_bound ownerPlayer={ownerPlayer.ProfileId}; botProfile={owner.ProfileId}; groupId={group.Id}; nativeMembers={group.MembersCount}; expectedOperators={ownerOperatorCount}; sharedGroupCreated={sharedGroupCreated}; enemies={activeEnemies.Count}; allPlayers={allPlayers.Count}; constructorPlayers={constructorPlayers.Count}; side={group.Side}; structuralCoopGroup=Fika; nativeSainSquad=shared_per_player_owner");
        return group;
    }

    private static void FinalizeOperatorSpawn(
        BotSpawner botSpawner,
        BotCreationDataClass botCreationData,
        Player ownerPlayer,
        BotOwner owner,
        VanguardRaidOperatorSnapshotDto snapshot,
        int ownerOperatorCount)
    {
        PrepareFriendlyOwner(owner, ownerPlayer);
        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_BRAIN_BIND_STATUS",
            $"finalize_begin operator={snapshot.OperatorId ?? "<none>"}; owner={snapshot.OwnerProfileId ?? "<none>"}; botProfile={owner.ProfileId}; state={owner.BotState}; hasGroup={owner.BotsGroup is not null}");
        var stopwatch = Stopwatch.StartNew();
        bool shallBeGroup = botCreationData.SpawnParams?.ShallBeGroup is not null;
        FinalizeSpawnMethod.Invoke(
            botSpawner,
            new object[]
            {
                owner,
                botCreationData,
                new Action<BotOwner>(spawnedOwner => BindOperator(ownerPlayer, spawnedOwner, snapshot, ownerOperatorCount)),
                shallBeGroup,
                stopwatch,
            });
    }

    private static void BindOperator(
        Player ownerPlayer,
        BotOwner spawnedOwner,
        VanguardRaidOperatorSnapshotDto snapshot,
        int ownerOperatorCount)
    {
        if (!VanguardRaidOperatorRuntimeRegistry.IsOperatorPending(snapshot.OperatorId))
        {
            return;
        }

        PrepareFriendlyOwner(spawnedOwner, ownerPlayer);
        if (spawnedOwner.Profile is not null)
        {
            LogProfileEquipmentAudit(spawnedOwner.Profile, snapshot, "spawned_owner_before_registry_bind");
        }
        try
        {
            // Some EFT group/memory APIs accept IDissonancePlayer. Calling them directly
            // forces Vanguard to reference DissonanceVoip at compile time. Reflection keeps
            // the owner binding runtime-safe while preserving the existing project references.
            VanguardEftReflection.InvokeSingleArgumentMethod(spawnedOwner.Memory, "DeleteInfoAboutEnemy", ownerPlayer);
            VanguardEftReflection.InvokeSingleArgumentMethod(spawnedOwner.BotsGroup, "RemoveEnemy", ownerPlayer);
            VanguardEftReflection.InvokeSingleArgumentMethod(spawnedOwner.BotsGroup, "AddNeutral", ownerPlayer);
            VanguardEftReflection.InvokeSingleArgumentMethod(spawnedOwner.BotsGroup, "AddAlly", ownerPlayer);
            spawnedOwner.BotsGroup?.AddMember(spawnedOwner, false);
            RebindBotToOperatorGroup(spawnedOwner);
            VanguardOperatorNativeSquadRegistry.RecordMemberBound(
                Normalize(snapshot.RaidSessionId, VanguardRaidOperatorRuntimeRegistry.ActiveRaidSessionId),
                ownerPlayer.ProfileId,
                spawnedOwner,
                ownerOperatorCount,
                "operator_bound");
            VanguardCoopStructuralSquadBinder.BindKnownFriendlies(spawnedOwner, ownerPlayer, "operator_bound");
            ClearKnownEnemies(spawnedOwner);
            if (spawnedOwner.Memory is not null)
            {
                spawnedOwner.Memory.GoalEnemy = null;
            }
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning("VANGUARD_OPERATOR_OWNER_BOUND", $"friendly bind partial failure operator={snapshot.OperatorId ?? "<none>"}; reason={exception.GetType().Name}:{exception.Message}");
        }

        LogRuntimeBrainState(spawnedOwner, snapshot, "before_registry_attach");

        bool isLocalOwner = GamePlayerOwner.MyPlayer is { } localPlayer
            && string.Equals(localPlayer.ProfileId, snapshot.OwnerProfileId, StringComparison.Ordinal);
        bool attached = VanguardRaidOperatorRuntimeRegistry.AttachSpawnedOperator(
            snapshot,
            spawnedOwner.ProfileId,
            spawnedOwner.Profile?.Info?.Nickname ?? snapshot.Callsign ?? snapshot.DisplayName ?? snapshot.OperatorId ?? "Operator",
            VanguardFikaCompat.IsHeadless,
            isLocalOwner,
            spawnedOwner);
        if (attached)
        {
            VanguardSainStaticProfileService.RegisterBoundOperator(
                snapshot.OperatorId,
                spawnedOwner.ProfileId,
                snapshot.OwnerProfileId,
                spawnedOwner,
                "operator_bound",
                snapshot);
            VanguardOperatorFriendlyTargetGuard.BindOperatorFriendlyRelations(spawnedOwner, "operator_bound");
        }

        GeneratedProfileCache.Remove(BuildGeneratedProfileCacheKey(snapshot));
    }

    private static List<BotOwner> GetActiveEnemies(BotSpawner botSpawner, BotOwner owner)
    {
        if (ExistingBotsMethod.Invoke(botSpawner, new object[] { owner }) is not IEnumerable<BotOwner> existingBots)
        {
            return new List<BotOwner>();
        }

        return existingBots
            .Where(candidate => candidate is not null && !candidate.IsDead && candidate != owner)
            .ToList();
    }

    private static void RebindBotToOperatorGroup(BotOwner owner)
    {
        var requestController = owner.BotRequestController;
        var botsGroup = owner.BotsGroup;
        if (requestController is null || botsGroup is null || owner.Memory is null)
        {
            return;
        }

        owner.Memory.BotsGroup_0 = botsGroup;
        requestController.GroupRequestController_1 = botsGroup.RequestsController;
    }

    private static void ClearKnownEnemies(BotOwner owner)
    {
        var knownEnemies = owner.EnemiesController?.EnemyInfos?.ToList();
        if (knownEnemies is null || owner.Memory is null)
        {
            return;
        }

        foreach (var enemy in knownEnemies)
        {
            owner.Memory.DeleteInfoAboutEnemy(enemy.Key);
        }
    }

    private static void LogRuntimeBrainState(BotOwner owner, VanguardRaidOperatorSnapshotDto snapshot, string stage)
    {
        try
        {
            var activeWeapon = VanguardEftReflection.ReadFirstMember(VanguardEftReflection.ReadFirstMember(owner, "WeaponManager"), "CurrentWeapon", "CurrentWeaponInfo.Weapon")
                ?? VanguardEftReflection.ReadFirstMember(owner.GetPlayer, "HandsController.Item", "WeaponManager.CurrentWeapon");
            bool hasBrain = VanguardEftReflection.ReadFirstMember(owner, "Brain", "BotBrain", "BaseBrain") is not null;
            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_OPERATOR_BRAIN_BIND_STATUS",
                $"stage={stage}; operator={snapshot.OperatorId ?? "<none>"}; botProfile={owner.ProfileId}; role={VanguardOperatorBotTypes.DescribeRole(owner)}; state={owner.BotState}; hasGroup={owner.BotsGroup is not null}; groupId={owner.BotsGroup?.Id.ToString() ?? "<none>"}; members={owner.BotsGroup?.MembersCount.ToString() ?? "<none>"}; hasBrain={hasBrain}; hasMemory={owner.Memory is not null}; hasRequestController={owner.BotRequestController is not null}; activeWeapon={(activeWeapon?.ToString() ?? "<none>")}");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_BRAIN_BIND_STATUS",
                $"stage={stage}; operator={snapshot.OperatorId ?? "<none>"}; brain_state_log_failed={exception.GetType().Name}:{exception.Message}");
        }
    }

    private static void PrepareProfileIdentity(Profile profile, Player ownerPlayer)
    {
        if (profile.Info is null || ownerPlayer.Profile?.Info is null)
        {
            return;
        }

        profile.Info.Side = ownerPlayer.Side;
        profile.Info.GroupId = ownerPlayer.GroupId;
        profile.Info.TeamId = ownerPlayer.Profile.Info.TeamId;
        ApplyOperatorProfileRole(profile, ownerPlayer.Side, "profile_identity");
    }

    private static void ApplyOperatorProfileRole(Profile profile, EPlayerSide side, string stage)
    {
        try
        {
            if (profile.Info?.Settings is null)
            {
                VanguardClientDiagnosticsLog.Warning(
                    VanguardOperatorBotTypes.StatusTag,
                    $"VANGUARD_OPERATOR_ROLE_APPLY_SKIPPED stage={stage}; profile={profile.ProfileId ?? "<none>"}; reason=settings_missing; tag={VanguardOperatorBotTypes.StatusTag}");
                return;
            }

            if (!VanguardOperatorBotTypes.TryResolveRole(side, out var role, out string diagnostic))
            {
                VanguardClientDiagnosticsLog.Warning(
                    VanguardOperatorBotTypes.StatusTag,
                    $"VANGUARD_OPERATOR_ROLE_APPLY_FALLBACK stage={stage}; profile={profile.ProfileId ?? "<none>"}; diagnostic={diagnostic}; tag={VanguardOperatorBotTypes.StatusTag}");
                return;
            }

            profile.Info.Settings.Role = role;
            VanguardClientDiagnosticsLog.Info(
                VanguardOperatorBotTypes.StatusTag,
                $"VANGUARD_OPERATOR_ROLE_APPLIED stage={stage}; profile={profile.ProfileId ?? "<none>"}; role={role}:{(int)role}; diagnostic={diagnostic}; orbitSubstring={VanguardOperatorBotTypes.RoleSubstring}; tag={VanguardOperatorBotTypes.StatusTag}");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                VanguardOperatorBotTypes.StatusTag,
                $"VANGUARD_OPERATOR_ROLE_APPLY_FAILED stage={stage}; profile={profile.ProfileId ?? "<none>"}; reason={exception.GetType().Name}:{exception.Message}; tag={VanguardOperatorBotTypes.StatusTag}");
        }
    }

    private static void PrepareFriendlyOwner(BotOwner owner, Player ownerPlayer)
    {
        if (owner.Profile?.Info is { } ownerInfo && ownerPlayer.Profile?.Info is { } playerInfo)
        {
            ownerInfo.Side = ownerPlayer.Side;
            ownerInfo.GroupId = ownerPlayer.GroupId;
            ownerInfo.TeamId = playerInfo.TeamId;
        }

        if (owner.GetPlayer?.Profile?.Info is { } spawnedInfo && ownerPlayer.Profile?.Info is { } ownerPlayerInfo)
        {
            spawnedInfo.Side = ownerPlayer.Side;
            spawnedInfo.GroupId = ownerPlayer.GroupId;
            spawnedInfo.TeamId = ownerPlayerInfo.TeamId;
        }

        if (owner.Profile is not null)
        {
            ApplyOperatorProfileRole(owner.Profile, ownerPlayer.Side, "botowner_profile");
        }

        if (owner.GetPlayer?.Profile is not null)
        {
            ApplyOperatorProfileRole(owner.GetPlayer.Profile, ownerPlayer.Side, "player_profile");
        }

        VanguardCoopStructuralSquadBinder.ApplyOperatorAffiliation(owner, ownerPlayer, "prepare_friendly_owner");
        VanguardCoopStructuralSquadBinder.ApplyMindPolicy(owner);
        ApplyOperatorMindPolicy(owner, ownerPlayer);
    }

    private static void ApplyOperatorMindPolicy(BotOwner owner, Player ownerPlayer)
    {
        var settings = owner.Settings;
        if (settings is null)
        {
            return;
        }

        settings.FileSettings.Mind.ENEMY_BY_GROUPS_PMC_PLAYERS = true;
        settings.FileSettings.Mind.ENEMY_BY_GROUPS_SAVAGE_PLAYERS = true;
        settings.FileSettings.Mind.USE_ADD_TO_ENEMY_VALIDATION = false;
        settings.FileSettings.Mind.CAN_EXECUTE_REQUESTS = true;
        settings.FileSettings.Mind.CAN_RECEIVE_PLAYER_REQUESTS_BEAR = true;
        settings.FileSettings.Mind.CAN_RECEIVE_PLAYER_REQUESTS_USEC = true;
        settings.FileSettings.Mind.CAN_RECEIVE_PLAYER_REQUESTS_SAVAGE = ownerPlayer.Side == EPlayerSide.Savage;
        settings.FileSettings.Mind.CHANCE_FUCK_YOU_ON_CONTACT_100 = 0;
        settings.FileSettings.Mind.REVENGE_TO_GROUP = true;
        settings.FileSettings.Mind.REVENGE_FOR_SAVAGE_PLAYERS = false;

        // The runtime default coop doctrine: player PMC sides are never enemies of Vanguard
        // Operators. Hostile AI PMCs/scavs remain hostile through explicit enemy bot
        // role lists below, while players and Operators are structurally bound to the
        // Fika/Vanguard global coop squad. This reduces the runtime fallback guard load.
        settings.FileSettings.Mind.DEFAULT_BEAR_BEHAVIOUR = EWarnBehaviour.AlwaysFriends;
        settings.FileSettings.Mind.DEFAULT_USEC_BEHAVIOUR = EWarnBehaviour.AlwaysFriends;
        settings.FileSettings.Mind.DEFAULT_SAVAGE_BEHAVIOUR = ownerPlayer.Side == EPlayerSide.Savage
            ? EWarnBehaviour.AlwaysFriends
            : EWarnBehaviour.AlwaysEnemies;

        var enemyTypes = settings.GetEnemyBotTypes();
        if (enemyTypes is null)
        {
            return;
        }

        AddEnemyType(enemyTypes, WildSpawnType.pmcUSEC);
        AddEnemyType(enemyTypes, WildSpawnType.pmcBEAR);
        AddEnemyType(enemyTypes, WildSpawnType.assault);
        AddBossSquadEnemyTypes(enemyTypes);
    }

    private static void AddEnemyType(ICollection<WildSpawnType> enemyTypes, WildSpawnType type)
    {
        if (!enemyTypes.Contains(type))
        {
            enemyTypes.Add(type);
        }
    }

    private static void AddBossSquadEnemyTypes(ICollection<WildSpawnType> enemyTypes)
    {
        foreach (var wildSpawnType in Enum.GetValues(typeof(WildSpawnType)).Cast<WildSpawnType>())
        {
            var name = wildSpawnType.ToString();
            if (!name.Contains("boss", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("fol" + "lower", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddEnemyType(enemyTypes, wildSpawnType);
        }
    }

    private static VanguardSpawnPoint ResolveSafeSpawnPoint(BotsController botsController, Player ownerPlayer, int index)
    {
        var ownerTransform = ownerPlayer.Transform;
        Vector3 origin = ownerTransform.position;
        Vector3 forward = Flatten(ownerTransform.forward);
        Vector3 right = Flatten(ownerTransform.right);
        var rejectCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rejectSamples = new List<string>();

        // The runtime keeps the safety rule (no partial/invalid path, no forced raw fallback), but uses
        // a two-pass selector. The strict pass prefers close squad positions. The balanced pass expands
        // the search and allows a wider NavMesh projection while still requiring a complete path back to
        // the player owner. This avoids both previous failures: bad decorative/island spawns and endless
        // deferral in dense interiors.
        if (TryResolveFromCandidates(
                botsController,
                origin,
                forward,
                right,
                index,
                BuildSpawnCandidates(origin, forward, right, index, SpawnCandidatePass.Strict),
                SpawnCandidatePass.Strict,
                rejectCounts,
                rejectSamples,
                out var strictPoint))
        {
            return strictPoint;
        }

        if (TryResolveFromCandidates(
                botsController,
                origin,
                forward,
                right,
                index,
                BuildSpawnCandidates(origin, forward, right, index, SpawnCandidatePass.Balanced),
                SpawnCandidatePass.Balanced,
                rejectCounts,
                rejectSamples,
                out var balancedPoint))
        {
            return balancedPoint;
        }

        string summary = FormatRejectSummary(rejectCounts);
        string samples = string.Join("|", rejectSamples.Take(12));
        VanguardClientDiagnosticsLog.Warning(
            "VANGUARD_RAID_SPAWN_SAFE_POSITION",
            $"no navsafe candidate index={index}; rejected={summary}; samples={samples}; action=defer_spawn_retry");
        throw new VanguardSpawnPositionUnavailableException($"rejected={summary}");
    }

    private static bool TryResolveFromCandidates(
        BotsController botsController,
        Vector3 ownerPosition,
        Vector3 forward,
        Vector3 right,
        int operatorIndex,
        IEnumerable<Vector3> candidates,
        SpawnCandidatePass pass,
        Dictionary<string, int> rejectCounts,
        List<string> rejectSamples,
        out VanguardSpawnPoint spawnPoint)
    {
        int localIndex = 0;
        foreach (Vector3 raw in candidates)
        {
            if (TryValidateSpawnCandidate(
                    botsController,
                    ownerPosition,
                    raw,
                    forward,
                    right,
                    localIndex,
                    pass,
                    out spawnPoint,
                    out string rejectReason))
            {
                VanguardClientDiagnosticsLog.Info(
                    "VANGUARD_RAID_SPAWN_SAFE_POSITION",
                    $"navsafe candidate accepted operatorIndex={operatorIndex}; pass={pass}; index={localIndex}; rejected={FormatRejectSummary(rejectCounts)}; samples={string.Join("|", rejectSamples.Take(6))}");
                return true;
            }

            AddReject(rejectCounts, rejectSamples, localIndex, $"{pass}:{rejectReason}", raw);
            localIndex++;
        }

        spawnPoint = default;
        return false;
    }

    private static bool TryValidateSpawnCandidate(
        BotsController botsController,
        Vector3 ownerPosition,
        Vector3 raw,
        Vector3 forward,
        Vector3 right,
        int candidateIndex,
        SpawnCandidatePass pass,
        out VanguardSpawnPoint spawnPoint,
        out string rejectReason)
    {
        spawnPoint = default;
        float sampleRadius = pass == SpawnCandidatePass.Strict
            ? SpawnStrictSampleRadiusMeters
            : SpawnBalancedSampleRadiusMeters;
        float maxProjectionDistance = pass == SpawnCandidatePass.Strict
            ? SpawnStrictMaxProjectionDistanceMeters
            : SpawnBalancedMaxProjectionDistanceMeters;
        float maxOwnerDistance = pass == SpawnCandidatePass.Strict
            ? SpawnStrictMaxOwnerDistanceMeters
            : SpawnBalancedMaxOwnerDistanceMeters;
        float maxVerticalDelta = pass == SpawnCandidatePass.Strict
            ? SpawnStrictMaxVerticalDeltaMeters
            : SpawnBalancedMaxVerticalDeltaMeters;
        int minimumEgressDirections = pass == SpawnCandidatePass.Strict
            ? SpawnStrictLocalEgressMinimumValidDirections
            : SpawnBalancedLocalEgressMinimumValidDirections;

        if (!TryProjectToNavMesh(raw, sampleRadius, out var projected, out var navReason, out float projectionDistance))
        {
            rejectReason = "navmesh_missing";
            return false;
        }

        if (projectionDistance > maxProjectionDistance)
        {
            rejectReason = $"navmesh_projection_too_far:{projectionDistance:0.00}";
            return false;
        }

        float verticalDelta = Math.Abs(projected.y - ownerPosition.y);
        if (verticalDelta > maxVerticalDelta)
        {
            rejectReason = $"height_delta_too_large:{verticalDelta:0.00}";
            return false;
        }

        float ownerDistance = Vector3.Distance(ownerPosition, projected);
        if (ownerDistance < SpawnMinOwnerDistanceMeters)
        {
            rejectReason = $"too_close_to_owner:{ownerDistance:0.00}";
            return false;
        }

        if (ownerDistance > maxOwnerDistance)
        {
            rejectReason = $"too_far_from_owner:{ownerDistance:0.00}";
            return false;
        }

        if (!TryValidatePath(projected, ownerPosition, sampleRadius, out string pathStatus, out float pathLength))
        {
            rejectReason = $"path_to_owner_{pathStatus}";
            return false;
        }

        if (IsBlockedByHardGeometry(projected, pass, out string clearanceStatus))
        {
            rejectReason = $"clearance_{clearanceStatus}";
            return false;
        }

        if (!HasLocalNavMeshEgress(projected, forward, right, sampleRadius, maxVerticalDelta, minimumEgressDirections, pass, out string egressStatus))
        {
            rejectReason = $"local_egress_{egressStatus}";
            return false;
        }

        if (botsController.GetClosestZone(projected, out _) is null)
        {
            rejectReason = "no_bot_zone";
            return false;
        }

        try
        {
            if (botsController.CoversData.GetClosest(projected).CorePointInGame is null)
            {
                rejectReason = "no_core_point";
                return false;
            }
        }
        catch (Exception exception)
        {
            rejectReason = $"core_point_error:{exception.GetType().Name}";
            return false;
        }

        spawnPoint = new VanguardSpawnPoint(
            projected,
            raw,
            candidateIndex,
            $"{pass}:{navReason}:projection={projectionDistance:0.00}:pathLength={pathLength:0.00}:clearance={clearanceStatus}",
            pathStatus,
            egressStatus);
        rejectReason = string.Empty;
        return true;
    }

    private static IEnumerable<Vector3> BuildSpawnCandidates(Vector3 origin, Vector3 forward, Vector3 right, int index, SpawnCandidatePass pass)
    {
        int row = Math.Max(0, index / 2);
        float rowOffset = row * 0.65f;
        float side = index % 2 == 0 ? -1f : 1f;
        Vector3[] directions =
        {
            (right * side + forward * 0.35f).normalized,
            (right * -side + forward * 0.35f).normalized,
            (forward + right * side * 0.75f).normalized,
            (forward + right * -side * 0.75f).normalized,
            (right * side - forward * 0.35f).normalized,
            (right * -side - forward * 0.35f).normalized,
            forward,
            -forward,
            (forward + right).normalized,
            (forward - right).normalized,
            (-forward + right).normalized,
            (-forward - right).normalized,
        };

        float[] distances = pass == SpawnCandidatePass.Strict
            ? new[] { 2.0f + rowOffset, 2.75f + rowOffset, 3.6f + rowOffset, 4.75f + rowOffset, 6.0f + rowOffset, 7.5f + rowOffset }
            : new[] { 1.9f + rowOffset, 2.5f + rowOffset, 3.25f + rowOffset, 4.25f + rowOffset, 5.5f + rowOffset, 7.0f + rowOffset, 9.0f + rowOffset, 11.5f + rowOffset, 13.5f + rowOffset };

        // Prefer side/diagonal squad slots, then expand into a wider accessible ring. The final
        // position is still the NavMesh projection that must path completely back to the owner.
        foreach (float distance in distances)
        {
            foreach (Vector3 direction in directions)
            {
                if (direction.sqrMagnitude <= 0.0001f)
                {
                    continue;
                }

                yield return origin + direction.normalized * distance;
            }
        }
    }

    private static bool TryProjectToNavMesh(Vector3 raw, float sampleRadius, out Vector3 projected, out string reason, out float projectionDistance)
    {
        if (NavMesh.SamplePosition(raw, out var hit, sampleRadius, NavMesh.AllAreas))
        {
            projected = hit.position;
            projectionDistance = Vector3.Distance(raw, projected);
            reason = projectionDistance <= 0.05f ? "navmesh_exact" : $"navmesh_projected:{projectionDistance:0.00}";
            return true;
        }

        projected = raw;
        projectionDistance = float.PositiveInfinity;
        reason = "navmesh_missing";
        return false;
    }

    private static bool TryValidatePath(Vector3 from, Vector3 to, float sampleRadius, out string status, out float length)
    {
        length = 0f;
        if (!NavMesh.SamplePosition(from, out var fromHit, sampleRadius, NavMesh.AllAreas))
        {
            status = "from_navmesh_missing";
            return false;
        }

        if (!NavMesh.SamplePosition(to, out var toHit, sampleRadius, NavMesh.AllAreas))
        {
            status = "to_navmesh_missing";
            return false;
        }

        var path = new NavMeshPath();
        if (!NavMesh.CalculatePath(fromHit.position, toHit.position, NavMesh.AllAreas, path))
        {
            status = "calculate_failed";
            return false;
        }

        status = path.status.ToString();
        length = ResolvePathLength(path, Vector3.Distance(fromHit.position, toHit.position));
        return path.status == NavMeshPathStatus.PathComplete;
    }

    private static float ResolvePathLength(NavMeshPath path, float fallback)
    {
        if (path.corners is null || path.corners.Length < 2)
        {
            return fallback;
        }

        float length = 0f;
        for (int i = 1; i < path.corners.Length; i++)
        {
            length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
        }

        return length <= 0.05f ? fallback : length;
    }

    private static bool HasLocalNavMeshEgress(
        Vector3 projected,
        Vector3 forward,
        Vector3 right,
        float sampleRadius,
        float maxVerticalDelta,
        int minimumValidDirections,
        SpawnCandidatePass pass,
        out string status)
    {
        var directions = new[]
        {
            forward,
            -forward,
            right,
            -right,
            (forward + right).normalized,
            (forward - right).normalized,
            (-forward + right).normalized,
            (-forward - right).normalized,
        };
        int validDirections = 0;
        int sampledDirections = 0;

        foreach (Vector3 direction in directions)
        {
            if (direction.sqrMagnitude <= 0.0001f)
            {
                continue;
            }

            Vector3 probeRaw = projected + direction.normalized * SpawnLocalEgressProbeMeters;
            if (!NavMesh.SamplePosition(probeRaw, out var probeHit, Math.Min(sampleRadius, 1.25f), NavMesh.AllAreas))
            {
                continue;
            }

            sampledDirections++;
            if (Math.Abs(probeHit.position.y - projected.y) > maxVerticalDelta)
            {
                continue;
            }

            if (IsBlockedByHardGeometry(probeHit.position, pass, out _))
            {
                continue;
            }

            if (!TryValidatePath(projected, probeHit.position, sampleRadius, out string localPathStatus, out _))
            {
                continue;
            }

            validDirections++;
            if (validDirections >= minimumValidDirections)
            {
                status = $"egress_ok:{validDirections}/{sampledDirections}";
                return true;
            }
        }

        status = $"insufficient:{validDirections}/{Math.Max(sampledDirections, 1)}";
        return false;
    }

    private static bool IsBlockedByHardGeometry(Vector3 position, SpawnCandidatePass pass, out string status)
    {
        float radius = pass == SpawnCandidatePass.Strict ? SpawnCapsuleRadiusMeters : 0.26f;
        float footOffset = radius + 0.08f;
        Vector3 bottom = position + Vector3.up * footOffset;
        Vector3 top = position + Vector3.up * Math.Max(footOffset + 0.05f, SpawnCapsuleHeightMeters - radius);
        var colliders = Physics.OverlapCapsule(bottom, top, radius, ~0, QueryTriggerInteraction.Ignore);
        int blockers = 0;
        string firstBlocker = string.Empty;

        foreach (var collider in colliders)
        {
            if (!IsBlockingSpawnCollider(collider, position))
            {
                continue;
            }

            blockers++;
            firstBlocker = firstBlocker.Length == 0 ? DescribeCollider(collider) : firstBlocker;
            if (blockers >= (pass == SpawnCandidatePass.Strict ? 1 : 2))
            {
                status = $"blocked:{blockers}:{firstBlocker}";
                return true;
            }
        }

        status = blockers == 0 ? "clear" : $"soft_clear:{blockers}:{firstBlocker}";
        return false;
    }

    private static bool IsBlockingSpawnCollider(Collider? collider, Vector3 position)
    {
        if (collider is null || !collider.enabled || collider.isTrigger)
        {
            return false;
        }

        try
        {
            if (collider.GetComponentInParent<Player>() is not null)
            {
                return false;
            }
        }
        catch
        {
            // Some EFT objects throw while resolving parents during scene load; treat them normally.
        }

        var bounds = collider.bounds;
        if (bounds.max.y < position.y + 0.22f)
        {
            return false;
        }

        // Very thin ground/trim colliders often overlap a correctly placed NavMesh point. They should
        // not prevent spawn when pathing and local egress are valid.
        if (bounds.size.y < 0.12f && bounds.max.y < position.y + 0.35f)
        {
            return false;
        }

        return true;
    }

    private static string DescribeCollider(Collider collider)
    {
        try
        {
            return $"{collider.GetType().Name}:{collider.name}".Replace(' ', '_');
        }
        catch
        {
            return collider.GetType().Name;
        }
    }

    private static void AddReject(Dictionary<string, int> rejectCounts, List<string> rejectSamples, int candidateIndex, string reason, Vector3 raw)
    {
        string key = reason.Split(':')[0];
        rejectCounts.TryGetValue(key, out int current);
        rejectCounts[key] = current + 1;
        if (rejectSamples.Count < 10)
        {
            rejectSamples.Add($"#{candidateIndex}:{reason}:raw={raw}");
        }
    }

    private static string FormatRejectSummary(Dictionary<string, int> rejectCounts)
    {
        if (rejectCounts.Count == 0)
        {
            return "none";
        }

        return string.Join(",", rejectCounts.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key}={pair.Value}"));
    }

    private static void QueueNavSafeRetry(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < nextNavSafeRetryAt)
        {
            return;
        }

        nextNavSafeRetryAt = now + NavSafeRetryDelay;
        _ = Task.Run(async () =>
        {
            await Task.Delay(NavSafeRetryDelay);
            VanguardRaidOperatorController.QueueSpawn($"navsafe_retry:{reason}");
        });
    }

    private static async Task PreloadProfileBundlesAsync(Profile profile, VanguardRaidOperatorSnapshotDto snapshot)
    {
        try
        {
            var rawPrefabPaths = profile.GetAllPrefabPaths(false).ToArray();
            var prefabPaths = rawPrefabPaths
                .Where(resource => resource is not null)
                .ToArray();
            if (prefabPaths.Length == 0)
            {
                VanguardClientDiagnosticsLog.Info(
                    "VANGUARD_OPERATOR_PROFILE_GENERATED",
                    $"bundle preload skipped operator={snapshot.OperatorId ?? "<none>"}; profile={profile.ProfileId}; reason=no_prefab_paths; rawCount={rawPrefabPaths.Length}");
                return;
            }

            await Singleton<PoolManagerClass>.Instance.LoadBundlesAndCreatePools(
                PoolManagerClass.PoolsCategory.Raid,
                PoolManagerClass.AssemblyType.Local,
                prefabPaths,
                JobPriorityClass.General,
                null,
                PoolManagerClass.DefaultCancellationToken);

            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_OPERATOR_PROFILE_GENERATED",
                $"bundle preload completed operator={snapshot.OperatorId ?? "<none>"}; profile={profile.ProfileId}; prefabPaths={prefabPaths.Length}; filtered={rawPrefabPaths.Length - prefabPaths.Length}");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_PROFILE_GENERATED",
                $"bundle preload skipped operator={snapshot.OperatorId ?? "<none>"}; profile={profile.ProfileId}; reason={exception.GetType().Name}:{exception.Message}; action=continue_spawn_without_cancel");
        }
    }

    private static void LogProfileEquipmentAudit(Profile profile, VanguardRaidOperatorSnapshotDto snapshot, string stage)
    {
        try
        {
            var items = profile.Inventory?.AllRealPlayerItems?.ToArray() ?? Array.Empty<EFT.InventoryLogic.Item>();
            var slotSummary = DescribeEquipmentSlots(items);
            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_OPERATOR_PROFILE_GENERATED",
                $"equipment audit stage={stage}; operator={snapshot.OperatorId ?? "<none>"}; profile={profile.ProfileId}; items={items.Length}; inventoryRoot={ResolveInventoryEquipmentId(profile)}; slots={slotSummary}");
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_OPERATOR_PROFILE_GENERATED",
                $"equipment audit failed stage={stage}; operator={snapshot.OperatorId ?? "<none>"}; profile={profile.ProfileId}; reason={exception.GetType().Name}:{exception.Message}");
        }
    }

    private static string ResolveInventoryEquipmentId(Profile profile)
    {
        try
        {
            object? inventory = profile.Inventory;
            object? equipment = inventory?.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(property => string.Equals(property.Name, "Equipment", StringComparison.Ordinal))
                ?.GetValue(inventory);
            if (equipment is null)
            {
                return "<none>";
            }

            object? id = equipment.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(property => string.Equals(property.Name, "Id", StringComparison.Ordinal))
                ?.GetValue(equipment);
            return id?.ToString() ?? equipment.ToString() ?? "<unknown>";
        }
        catch
        {
            return "<unavailable>";
        }
    }

    private static string DescribeEquipmentSlots(IEnumerable<EFT.InventoryLogic.Item> items)
    {
        string[] interestingSlots =
        {
            "FirstPrimaryWeapon",
            "SecondPrimaryWeapon",
            "Holster",
            "Scabbard",
            "TacticalVest",
            "ArmorVest",
            "Backpack",
            "Headwear",
            "Earpiece",
        };

        var realizedItems = items.ToArray();
        return string.Join(
            ";",
            interestingSlots.Select(slot =>
            {
                var matches = realizedItems
                    .Where(item => string.Equals(item.CurrentAddress?.Container?.ID, slot, StringComparison.OrdinalIgnoreCase))
                    .Take(3)
                    .Select(item => $"{item.Id}:{item.TemplateId}")
                    .ToArray();
                return matches.Length == 0 ? $"{slot}=0" : $"{slot}={matches.Length}[{string.Join("|", matches)}]";
            }));
    }


    private static void StripDogTagCustomization(JObject descriptor)
    {
        JObject customization = EnsureObject(descriptor, "Customization");
        foreach (string key in customization.Properties().Select(property => property.Name).ToArray())
        {
            if (string.Equals(key, "Dogtag", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "DogTag", StringComparison.OrdinalIgnoreCase))
            {
                customization.Remove(key);
            }
        }
    }

    private static void SanitizeInventoryReferences(JObject inventory)
    {
        JArray items = EnsureArray(inventory, "items");
        RemoveItemsWithMissingParents(items);
        var ids = items.OfType<JObject>()
            .Select(item => ReadString(item, "_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string panelName in new[] { "fastPanel", "hideoutAreaStashes" })
        {
            JObject panel = EnsureObject(inventory, panelName);
            foreach (string key in panel.Properties().Select(property => property.Name).ToArray())
            {
                string? value = panel[key]?.Type == JTokenType.String ? panel[key]?.Value<string>() : panel[key]?.ToString();
                if (string.IsNullOrWhiteSpace(value) || !ids.Contains(value))
                {
                    panel.Remove(key);
                }
            }
        }

        JArray favorites = EnsureArray(inventory, "favoriteItems");
        for (int i = favorites.Count - 1; i >= 0; i--)
        {
            string? value = favorites[i]?.Type == JTokenType.String ? favorites[i]?.Value<string>() : favorites[i]?.ToString();
            if (string.IsNullOrWhiteSpace(value) || !ids.Contains(value))
            {
                favorites.RemoveAt(i);
            }
        }
    }

    private static void RemoveItemsWithMissingParents(JArray items)
    {
        bool changed;
        do
        {
            changed = false;
            var ids = items.OfType<JObject>()
                .Select(item => ReadString(item, "_id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (items[i] is not JObject item)
                {
                    items.RemoveAt(i);
                    changed = true;
                    continue;
                }

                string? id = ReadString(item, "_id");
                string? parentId = ReadString(item, "parentId");
                if (string.IsNullOrWhiteSpace(id)
                    || (!string.IsNullOrWhiteSpace(parentId)
                        && !string.Equals(parentId, "hideout", StringComparison.OrdinalIgnoreCase)
                        && !ids.Contains(parentId)))
                {
                    items.RemoveAt(i);
                    changed = true;
                }
            }
        }
        while (changed);
    }

    private static JObject EnsureObject(JObject parent, string name)
    {
        JProperty? property = FindProperty(parent, name);
        if (property?.Value is JObject obj)
        {
            return obj;
        }

        obj = new JObject();
        SetToken(parent, name, obj);
        return obj;
    }

    private static JArray EnsureArray(JObject parent, string name)
    {
        JProperty? property = FindProperty(parent, name);
        if (property?.Value is JArray array)
        {
            return array;
        }

        array = new JArray();
        SetToken(parent, name, array);
        return array;
    }

    private static JObject? ReadObject(JObject parent, string name) => FindProperty(parent, name)?.Value as JObject;

    private static JArray? ReadArray(JObject? parent, string name) => parent == null ? null : FindProperty(parent, name)?.Value as JArray;

    private static string? ReadString(JObject parent, string name)
    {
        JToken? token = FindProperty(parent, name)?.Value;
        if (token == null || token.Type == JTokenType.Null)
        {
            return null;
        }

        return token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
    }

    private static JProperty? FindProperty(JObject parent, string name)
    {
        return parent.Properties().FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static void SetToken(JObject parent, string name, JToken value)
    {
        JProperty? property = FindProperty(parent, name);
        if (property == null)
        {
            parent[name] = value;
            return;
        }

        property.Value = value;
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } target)
        {
            exception = target.InnerException;
        }

        return exception;
    }

    private static string CompactStack(Exception exception)
    {
        string stack = (exception.StackTrace ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return stack.Length <= 220 ? stack : stack[..220] + "...";
    }

    private static Vector3 Flatten(Vector3 value)
    {
        value.y = 0f;
        return value.sqrMagnitude <= 0.0001f ? Vector3.forward : value.normalized;
    }

    private static async Task<bool> EnsureLateRuntimeBindingAsync(
        BotSpawner botSpawner,
        BotsController botsController,
        VanguardRaidOperatorSnapshotDto snapshot,
        string expectedProfileId,
        SynchronizationContext unityContext)
    {
        // The normal finalize callback remains authoritative. This bounded retry only handles
        // the rare case where ActivateBot returned before EFT delivered that callback.
        // No reflection, scene scan, arbitrary getter or unknown IEnumerable is used here.
        for (int attempt = 0; attempt < LateBindRetryCount; attempt++)
        {
            if (!VanguardRaidOperatorRuntimeRegistry.IsOperatorPending(snapshot.OperatorId))
            {
                return true;
            }

            if (TryAttachByProfileId(botSpawner, botsController, snapshot, expectedProfileId, $"late_bind_attempt_{attempt + 1}"))
            {
                return true;
            }

            await Task.Delay(LateBindRetryDelay);
            await VanguardUnityThread.ResumeOnAsync(unityContext);
        }

        VanguardRuntimeBindGuardService.NotifyLateBindPending(snapshot, expectedProfileId, "late_bind_retry_window_exhausted");
        VanguardClientDiagnosticsLog.Warning(
            "VANGUARD_OPERATOR_RUNTIME_REGISTERED",
            $"operator={snapshot.OperatorId ?? "<none>"}; expectedProfile={expectedProfileId}; owner={snapshot.OwnerProfileId ?? "<none>"}; state=pending_after_late_bind; action=event_assisted_typed_guard_continues");
        return false;
    }

    private static bool TryAttachByProfileId(
        BotSpawner botSpawner,
        BotsController botsController,
        VanguardRaidOperatorSnapshotDto snapshot,
        string expectedProfileId,
        string source)
    {
        if (!VanguardRuntimeBindGuardService.TryFindExpectedBotOwnerByProfileId(
                expectedProfileId,
                botsController,
                botSpawner,
                out var owner,
                out var typedSource)
            || owner is null)
        {
            return false;
        }

        string resolvedSource = source + ":" + typedSource;
        VanguardClientDiagnosticsLog.Info(
            "VANGUARD_OPERATOR_RUNTIME_REGISTERED",
            $"operator={snapshot.OperatorId ?? "<none>"}; expectedProfile={expectedProfileId}; source={resolvedSource}; state=late_bind_found_bot_owner; lookup=typed_bounded");
        return TryBindExpectedOperatorByBotOwner(owner, snapshot, expectedProfileId, resolvedSource);
    }

    internal static bool TryBindExpectedOperatorByBotOwner(BotOwner owner, VanguardRaidOperatorSnapshotDto snapshot, string expectedProfileId, string source)
    {
        if (owner is null || snapshot is null)
        {
            return false;
        }

        if (!VanguardRaidOperatorRuntimeRegistry.IsOperatorPending(snapshot.OperatorId))
        {
            return true;
        }

        if (!string.Equals(owner.ProfileId, expectedProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ownerPlayer = ResolveOwnerPlayer(snapshot);
        if (ownerPlayer is null)
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_COMBAT_BIND_COHESION_RECOVERY_STATUS",
                $"VANGUARD_RUNTIME_BIND_ADOPT_FAILED operator={snapshot.OperatorId ?? "<none>"}; expectedProfile={expectedProfileId}; source={source}; reason=owner_player_not_found; tag=VANGUARD_COMBAT_BIND_COHESION_RECOVERY_STATUS");
            return false;
        }

        try
        {
            PrepareFriendlyOwner(owner, ownerPlayer);
            if (owner.Profile is not null)
            {
                LogProfileEquipmentAudit(owner.Profile, snapshot, "runtime_bind_guard_before_registry_bind");
            }

            LogRuntimeBrainState(owner, snapshot, "runtime_bind_guard_before_registry_attach");
            BindOperator(ownerPlayer, owner, snapshot, Math.Max(1, owner.BotsGroup?.MembersCount ?? 1));
            bool attached = !VanguardRaidOperatorRuntimeRegistry.IsOperatorPending(snapshot.OperatorId);
            VanguardClientDiagnosticsLog.Info(
                "VANGUARD_COMBAT_BIND_COHESION_RECOVERY_STATUS",
                $"VANGUARD_RUNTIME_BIND_ADOPTED operator={snapshot.OperatorId ?? "<none>"}; expectedProfile={expectedProfileId}; botProfile={owner.ProfileId}; owner={snapshot.OwnerProfileId ?? "<none>"}; source={source}; attached={attached}; tag=VANGUARD_COMBAT_BIND_COHESION_RECOVERY_STATUS");
            return attached;
        }
        catch (Exception exception)
        {
            VanguardClientDiagnosticsLog.Warning(
                "VANGUARD_COMBAT_BIND_COHESION_RECOVERY_STATUS",
                $"VANGUARD_RUNTIME_BIND_ADOPT_FAILED operator={snapshot.OperatorId ?? "<none>"}; expectedProfile={expectedProfileId}; source={source}; reason={exception.GetType().Name}:{exception.Message}; tag=VANGUARD_COMBAT_BIND_COHESION_RECOVERY_STATUS");
            return false;
        }
    }

    private readonly record struct VanguardSpawnPoint(Vector3 Position, Vector3 RawPosition, int CandidateIndex, string Reason, string PathStatus, string EgressStatus);

    private enum SpawnCandidatePass
    {
        Strict,
        Balanced,
    }

    private sealed class VanguardSpawnPositionUnavailableException : Exception
    {
        public VanguardSpawnPositionUnavailableException(string message)
            : base(message)
        {
        }
    }

    private static WildSpawnType ResolveGenerationRole(EPlayerSide side)
    {
        return side switch
        {
            EPlayerSide.Bear => WildSpawnType.pmcBEAR,
            EPlayerSide.Usec => WildSpawnType.pmcUSEC,
            _ => WildSpawnType.assault,
        };
    }

    private static string Normalize(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private readonly record struct GeneratedOperatorProfile(Profile Profile, BotProfileDataClass ProfileData);
}
#endif
