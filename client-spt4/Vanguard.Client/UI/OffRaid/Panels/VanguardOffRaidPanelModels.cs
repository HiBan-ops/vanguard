using System;
using System.Collections.Generic;

// Responsibility: Defines data/state contracts used by the Off-Raid Operator UI, centered on Off Raid Panel Models.
// Flow: Producer code populates these shapes, policy/service code consumes them, and no orchestration is performed by the model itself.
// Authority boundary: Data contract only; authority remains with the service, store, or runtime reader that produces the values.
// Invariant: Model construction is side-effect free and preserves serialized/runtime compatibility expected by its consumers.
namespace Vanguard.Client.UI.OffRaid.Panels;

internal sealed class VanguardOffRaidPanelModel
{
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public List<VanguardInfoSectionModel> InfoSections { get; init; } = new();
    public List<VanguardOffRaidPanelAction> Actions { get; init; } = new();
}

internal sealed class VanguardInfoSectionModel
{
    public string Title { get; init; } = string.Empty;
    public List<VanguardInfoRowModel> Rows { get; init; } = new();
}

internal sealed class VanguardInfoRowModel
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public bool? Checked { get; init; }
    public Action<bool>? SetChecked { get; init; }
    public bool Enabled { get; init; } = true;

    // Presentation metadata is deliberately generic rather than Raid-History-specific. It lets
    // current raid facts form readable parent/detail blocks and gives future structured events
    // (VisitAPI/relationship projections, rescue events, medical causes, notable encounters, etc.)
    // enough room to render without changing the table contract again.
    public float Height { get; init; } = 22f;
    public int IndentLevel { get; init; }
    public bool Emphasized { get; init; }
    public bool WrapValue { get; init; }
    public bool FullWidthValue { get; init; }
}

internal sealed class VanguardOffRaidPanelAction
{
    public string Label { get; init; } = string.Empty;
    public string? Hint { get; init; }
    public Action Execute { get; init; } = static () => { };
    public bool Enabled { get; init; } = true;
}
