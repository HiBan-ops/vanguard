#if SPT_CLIENT
using UnityEngine;

// Responsibility: Represent one HUD icon badge and its optional sprite overlay information.
// Flow: Carry badge text and optional sprite references from HUD assembly into client presentation.
// Authority boundary: Icon data is presentation-only and does not change gameplay or sprite ownership.
// Invariant: The non-client conditional record retains a usable shape without referencing Unity sprite types.

namespace Vanguard.Client.Raid.Hud;

/// <summary>HUD icon badge and optional Unity sprite overlays for the SPT client renderer.</summary>
internal sealed record VanguardRaidOperatorHudIcon(string Badge, Sprite? BaseSprite, Sprite? OverlaySprite, bool ShowLabel);
#else
namespace Vanguard.Client.Raid.Hud;

/// <summary>Non-SPT compatibility shape that does not depend on Unity sprite types.</summary>
internal sealed record VanguardRaidOperatorHudIcon(string Badge, object? BaseSprite, object? OverlaySprite, bool ShowLabel);
#endif
