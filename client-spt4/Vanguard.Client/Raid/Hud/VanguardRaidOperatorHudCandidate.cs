#if SPT_CLIENT
using EFT;
using UnityEngine;

// Responsibility: Provides Raid Operator Hud Candidate support for the raid Operator HUD.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Raid.Hud;

internal sealed record VanguardRaidOperatorHudIdentity(
    string Key,
    string OperatorId,
    string OwnerProfileId,
    string BotProfileId,
    string Nickname,
    string Source,
    IPlayer Player);

internal sealed record VanguardRaidOperatorHudCandidate(
    string Key,
    string OperatorId,
    string OwnerProfileId,
    string BotProfileId,
    string Nickname,
    bool HealthReadable,
    int HealthPercent,
    string StatusIcons,
    string MedicalIconBadges,
    VanguardRaidOperatorHudIcon[] HudIcons,
    string BodyPartsRaw,
    string EffectsRaw,
    float DistanceMeters,
    Vector3 AnchorWorldPosition)
{
    public string ToSignature(string visibility) => $"{Key}|op={OperatorId}|owner={OwnerProfileId}|bot={BotProfileId}|name={Nickname}|hp={HealthPercent}|status={StatusIcons}|medicalIcons={MedicalIconBadges}|bodyParts={BodyPartsRaw}|{visibility}";
}
#else
namespace Vanguard.Client.Raid.Hud;

internal sealed class VanguardRaidOperatorHudIdentity
{
}

internal sealed class VanguardRaidOperatorHudCandidate
{
}
#endif
