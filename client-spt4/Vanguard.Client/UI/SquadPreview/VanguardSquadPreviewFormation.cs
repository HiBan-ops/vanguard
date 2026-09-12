#if SPT_CLIENT
using UnityEngine;
#endif

// Responsibility: provides deterministic local-space positions for the selected 1..4 Operator squad.
// Flow: the reconciler supplies stable index/count and receives a centered formation position; camera/framing remain owned by the active host adapter.
// Authority boundary: presentation geometry only; no gameplay, selection or persistent state is read or changed here.
// Invariant: the same ordered selected squad yields the same centered formation and never requires a full scene rebuild.
namespace Vanguard.Client.UI.SquadPreview;

internal static class VanguardSquadPreviewFormation
{
#if SPT_CLIENT
    private const float Spacing = 0.78f;
    private const float RearOffsetPerStep = 0.035f;

    public static Vector3 ResolveLocalPosition(int index, int count, string hostKind)
    {
        int safeCount = Mathf.Clamp(count, 1, 4);
        int safeIndex = Mathf.Clamp(index, 0, safeCount - 1);
        if (string.Equals(hostKind, "menu_overhaul", System.StringComparison.OrdinalIgnoreCase))
        {
            return ResolveMenuOverhaulPosition(safeIndex, safeCount);
        }

        float center = (safeCount - 1) * 0.5f;
        float centeredIndex = safeIndex - center;
        float x = centeredIndex * Spacing;
        float z = Mathf.Abs(centeredIndex) * RearOffsetPerStep;
        return new Vector3(x, 0f, z);
    }

    private static Vector3 ResolveMenuOverhaulPosition(int index, int count)
    {
        // Menu Overhaul owns a PMC at local X=0. Reserve the center lane for that PMC and
        // place Vanguard Operators slightly behind it in the same PlayerMVObject scene.
        float x = count switch
        {
            1 => -0.95f,
            2 => index == 0 ? -0.95f : 0.95f,
            3 => index switch
            {
                0 => -1.42f,
                1 => -0.70f,
                _ => 0.96f
            },
            _ => index switch
            {
                0 => -1.48f,
                1 => -0.76f,
                2 => 0.76f,
                _ => 1.48f
            }
        };

        float z = 0.22f + Mathf.Abs(x) * 0.035f;

        // The MO Vanguard anchor is rotated 180 degrees to make bodies face the user.
        // Pre-rotate positions by the inverse transform so world-space formation geometry remains
        // identical to the qualified Menu Overhaul layout instead of mirroring/swapping depth around the PMC.
        return new Vector3(-x, 0f, -z);
    }
#endif
}
