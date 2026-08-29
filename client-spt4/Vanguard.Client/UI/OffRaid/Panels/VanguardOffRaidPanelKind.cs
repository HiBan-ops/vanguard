// Responsibility: Presents and coordinates Off Raid Panel Kind in the Off-Raid Operator UI.
// Flow: Canonical API/runtime state is projected into view models and Unity/TMP controls; explicit user actions are delegated back through API/service boundaries.
// Authority boundary: Presentation layer only; it does not become persistence, economy, medical, or raid-runtime authority.
// Invariant: UI refreshes are idempotent from canonical state and temporary view state must not outlive its owning screen/session.
namespace Vanguard.Client.UI.OffRaid.Panels;

internal enum VanguardOffRaidPanelKind
{
    Dashboard,
    Contracts,
    ActiveService,
    FieldHospital,
    Billing,
    OperatorDossier
}
