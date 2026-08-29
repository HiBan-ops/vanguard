#if SPT_CLIENT
using UnityEngine;

// Responsibility: Provides Raid Operator Hud Icon support for the raid Operator HUD.
// Flow: The file encapsulates one bounded piece of the subsystem and exchanges explicit inputs/results with neighboring services instead of relying on hidden cross-system state.
// Authority boundary: Authority remains with the owning subsystem and any external EFT/SPT/Fika/SAIN component explicitly referenced by its call path.
// Invariant: Behavior stays scoped to the current profile/raid/process contract and preserves existing safety and compatibility boundaries.
namespace Vanguard.Client.Raid.Hud;

internal sealed record VanguardRaidOperatorHudIcon(string Badge, Sprite? BaseSprite, Sprite? OverlaySprite, bool ShowLabel);
#else
namespace Vanguard.Client.Raid.Hud;

internal sealed record VanguardRaidOperatorHudIcon(string Badge, object? BaseSprite, object? OverlaySprite, bool ShowLabel);
#endif
