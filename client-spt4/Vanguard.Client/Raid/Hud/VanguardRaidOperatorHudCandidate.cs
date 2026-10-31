#if SPT_CLIENT
using EFT;
using UnityEngine;

// Responsibility: Represent Operator identity and presentation snapshots consumed by the raid HUD.
// Flow: Carry player identity, health, effects, icons, and world-position inputs to the HUD renderer.
// Authority boundary: These read models neither own nor decide authoritative raid or Operator state.
// Invariant: The display signature tracks selected presentation fields, not the complete gameplay state.

namespace Vanguard.Client.Raid.Hud;

/// <summary>Identifies the raid player associated with an Operator without assigning gameplay authority.</summary>
internal sealed record VanguardRaidOperatorHudIdentity(
    string Key,
    string OperatorId,
    string OwnerProfileId,
    string BotProfileId,
    string Nickname,
    string Source,
    IPlayer Player);

/// <summary>HUD display inputs; the signature detects presentation changes, not complete gameplay state.</summary>
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
