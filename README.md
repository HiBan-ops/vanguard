# Vanguard

> **Vanguard is not a faction. It is a network.**

**Persistent Operators. Independent behavior. Shared survival.**

Vanguard turns allied PMC companions into **persistent Operators** whose identity, equipment, condition and history continue beyond a single raid.

You build a roster, prepare Operators Off-Raid, deploy with them, survive together and deal with the consequences when things go sideways.

> **The player is part of the squad, not a puppeteer above it.**

Vanguard is designed around Operators as independent combat entities rather than disposable followers waiting for constant player input.

## What Vanguard adds

### Persistent Operators

Operators are persistent entities rather than temporary raid spawns. Vanguard keeps a continuing Operator context that can include:

- identity and roster membership;
- equipment and loadout;
- medical state and recovery;
- behavioral persona;
- career/progression foundations;
- raid history;
- deployment state.

Survival matters because the same Operator can return for later operations.

### Off-Raid management

Vanguard includes an Off-Raid management layer for:

- recruitment and contracts;
- Operator dossiers;
- equipment preparation;
- medical treatment and recovery;
- career and raid-history views;
- deployment preparation;
- raid salary/economy foundations.

The broader relationship system is still basic and under development.

### Squad behavior, not replacement combat AI

Vanguard coordinates the squad-level context required to keep persistent Operators functioning together, including:

- following and regrouping;
- spacing and cohesion;
- tactical movement and positioning;
- survival priorities;
- medical arbitration;
- persistent Operator context.

Individual combat intelligence remains primarily handled by **SAIN**. Vanguard builds squad context and persistence around the existing AI ecosystem rather than attempting to replace it wholesale.

In Vanguard 0.7.0, each Operator starts from a private clone of the **currently loaded SAIN `Normal` personality**. Vanguard then layers the Operator's Persona and Specialty adjustments, plus a small set of Vanguard safety invariants, over that baseline. This means changes to your SAIN `Normal` tuning also influence Vanguard Operators, while Vanguard leaves SAIN's global `Normal` profile untouched.

**Planned:** a future Vanguard architecture will use a **dedicated SAIN profile for Operators** rather than deriving from the standard `Normal` personality. This will keep Vanguard's Operator baseline separate from SAIN's preconfigured personality profiles.

**Player-issued squad orders are not currently implemented.**

### Looting and survival

Operators can perform configurable opportunistic looting for useful equipment, weapons, compatible ammunition, medical supplies and selected valuables.

Combat, medical urgency and squad cohesion remain higher priorities than opportunistic loot.

### Tactical Editor

Vanguard includes an in-raid **Tactical Editor** for creating tactical zones and Operator positioning slots directly inside EFT.

The basic workflow is intentionally simple:

- **Ctrl+F6** — open or close the Tactical Editor;
- **Ctrl+Home** — create a zone at the player's position, type its name, then press **Enter** / Zone radius can be set in F12 menu
- **Ctrl+Insert** — place a tactical slot at the player's position; the direction you are looking becomes the slot's watch direction;
- repeat **Ctrl+Insert** for the positions you want inside the zone; For each slot, you can set priorities with percentage values
- **Ctrl+S** — save the authored layout.

Saved layouts are reused across raids. With the editor closed and automatic authored-zone occupancy enabled, Vanguard reloads the saved map and activates the relevant authored zone when its owner enters it. In a Fika Headless raid, the live authoring data carries the player's profile identity and the Headless authority assigns those slots only to **Operators belonging to that owner**. Another player's authored zone therefore does not take control of your Operators.

Authored slots remain **tactical assignments, not RTS-style direct orders**. Combat, grenade safety, medical needs and other higher-priority authorities can temporarily interrupt a slot assignment; Vanguard can return the Operator to the slot when that assignment becomes valid again.

Useful editing controls:

- **Ctrl+R** — reload the last saved layout. While the editor is active, the reloaded state is automatically republished and re-evaluated by the Headless authority; there is no separate manual “reconcile” command;
- **Ctrl+PageDown** — select the next authored zone;
- **Ctrl+Shift+N** — rename the selected zone;
- **Ctrl+P** — move the nearest slot to the player's current position and recapture its watch direction;
- **Ctrl+Delete** — temporarily disable/re-enable the nearest slot;
- **Ctrl+Shift+Delete** — delete the nearest slot;
- **Ctrl+Shift+Backspace** — delete the selected zone and its contained authored data;
- **Ctrl+V** — revalidate the selected zone.

The editor also exposes optional metadata, access markers, slot types and constraints for richer tactical authoring. They are not required for the basic create-zone → place-slots → save workflow.

## Supported execution topologies

Vanguard has been exercised in three execution modes:

- **Standalone SPT / `EFT.LocalGame`**;
- **Fika Host**;
- **Fika dedicated Headless**.

Fika is required only when using a Fika topology. The distributed Headless configuration has historically received the deepest validation, but standalone SPT is also a validated Vanguard execution path.

The release page will list the exact versions qualified for the shipping build.

## Requirements

The first public release uses the following dependency model:

| Component | Status |
|---|---|
| SPT | Required |
| MoreBotsAPI | Required |
| BigBrain | Required |
| Waypoints | Required |
| SAIN | Required |
| Fika Core / Fika Server | Required only for Fika topologies |
| Fika Headless | Required only for dedicated-Headless topology |
| Looting Bots | Optional / recommended and tested |

**Exact supported versions are frozen against the final release candidate and published with the release.**

Vanguard currently reserves MoreBotsAPI role IDs:

- `867100` — USEC Operator;
- `867101` — BEAR Operator.

## Compatibility philosophy

Vanguard owns the persistent player-allied Operator squad domain. Mods that also attempt to command, persist, reposition or otherwise own the same allied PMC entities can conflict even when both plugins load successfully.

Compatibility is therefore classified by authority and runtime evidence rather than by “it starts without crashing” alone.

The SP-Mod page carries the release-specific compatibility matrix and exact tested versions.

## Alpha/Beta release status

The first public Vanguard release is intentionally an **Alpha/Beta field-validation release**.

Its purpose is not to claim universal compatibility. It is to expose Vanguard to more machines, mod stacks and edge cases while keeping the core feature set stable enough to produce useful bug reports and compatibility evidence.

During the initial public cycle, priority goes to:

- reproducible bug fixes;
- compatibility triage;
- runtime stability;
- diagnostics and supportability;
- bounded Off-Raid quality-of-life improvements.

Large new gameplay systems are not the priority during stabilization.

## Diagnostics and bug reports

Vanguard exposes four diagnostic levels through BepInEx configuration:

| Level | Intended use |
|---|---|
| **Off** | Minimal Vanguard logging. |
| **Operational** | Normal play and first-line support. |
| **Diagnostic** | Richer state/decision information for reproductions. |
| **Trace** | Short, targeted deep investigation; potentially very verbose. |

Change this setting from the BepInEx Configuration Manager (**F12 → Vanguard - Diagnostics → Audit level**).

For a useful report, include the Vanguard/SPT/dependency versions, execution topology, reproduction steps, and the actual log files below.

**Standalone SPT or Fika Host**

- `<SPT>\BepInEx\LogOutput.log` — the player EFT instance;
- the current `<SPT Server>\user\logs\spt\spt*.log` — the SPT server console log;
- `<SPT Server>\user\vanguard\operators\vanguard-server.log` — Vanguard server-side persistence, API and Off-Raid output.

**Fika dedicated Headless**

- the player client's `<SPT>\BepInEx\LogOutput.log`;
- the Headless instance's `<Headless SPT>\BepInEx\LogOutput.log`;
- the current `<SPT Server>\user\logs\spt\spt*.log` — the SPT server console log;
- `<SPT Server>\user\vanguard\operators\vanguard-server.log`.

Because the player client and Headless files have the same filename, label those two `LogOutput.log` files clearly when attaching them.

Start with **Operational**. Switch to **Diagnostic** or **Trace** only when a reproduction requires it.

## Installation

Use the release archive and instructions published for the selected topology. The intended public package is designed to be installed from the SPT root rather than by manually distributing individual Vanguard binaries.

Release-specific install/update/removal paths are finalized from the shipping package itself.

## Roadmap

The first public period is focused on stabilization and compatibility validation. Longer-term work is intended to deepen the identity of persistent Operators rather than turn Vanguard into a conventional follower-control mod.

Planned directions include richer relationships and affinity, dynamic Operator events, emergent encounters, deeper careers, expanded Off-Raid management and additional tactical knowledge systems.

### Persistent Operators beyond the raid

Vanguard's longer-term goal is to let real Operator history and raid events feed into conversations, personal stories, relationships, recruitment and assignments from the wider Vanguard network.

To support this without coupling story content to private Vanguard internals, the project plans to explore a provider-independent **Vanguard Narrative API**.

Narrative technologies such as **VisitAPI** are being evaluated as optional presentation providers for dialogue, quests and related interactions. Vanguard Core would remain authoritative for Operator identity, persistence and gameplay state.

Two official expansion concepts are currently being considered:

- **Vanguard — Operator Stories** — personal narratives and interactions driven by persistent Operators and their actual raid history;
- **Vanguard — Network Assignments** — optional contracts, objectives and mission chains originating from the wider Vanguard network.

The same API direction may later support community-created story and assignment packs.

These are **post-release roadmap goals**, not current Vanguard features. No delivery date is committed, and any provider integration must be technically qualified for the supported standalone/Fika topologies before it is advertised as supported.

## Building from source

The public repository is intended to contain the minimal source and build material required to reproduce Vanguard's released binaries.

**Release-freeze placeholder:** exact clean-checkout prerequisites, build commands and source/tag mapping are inserted only after the final normalized release source is validated.

Internal development KBs, handoffs, runtime evidence and private project artifacts are intentionally excluded from the public repository.

## AI-assisted development

AI tools have been used throughout Vanguard development, including code generation, review, debugging, documentation and visual assets. Product direction, architecture, code direction and integration decisions, testing, validation and release authority remain under the author's control.

## License and third-party software

Vanguard is intended to be published under the **MIT License**.

SPT, EFT and Vanguard's external dependencies/integrations remain the property of their respective authors and are governed by their own licenses and terms. Required third-party notices will be included with the public source/release where applicable.
