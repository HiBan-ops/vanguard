# Vanguard

> **Vanguard is not a faction. It is a network.**

**Persistent Operators. Independent behavior. Shared survival.**

Vanguard turns allied PMC companions into **persistent Operators** whose identity, equipment, condition, progression and history continue beyond a single raid.

You build a roster, prepare Operators Off-Raid, deploy with them, survive together and deal with the consequences when things go sideways.

> **The player is part of the squad, not a puppeteer above it.**

Vanguard is designed around Operators as independent combat entities rather than disposable followers waiting for constant player input.

**Current release:** Vanguard **0.8.0** for **SPT 4.0.13**.

## What Vanguard adds

### Persistent Operator identity and career

Operators are durable world entities rather than temporary raid spawns. Vanguard keeps one continuing person across recruitment, release, re-engagement, menus and raids.

Persistent truth includes, where applicable:

- identity, side, role/archetype and roster membership;
- equipment and loadout;
- medical state and recovery;
- career level/XP, Common Skills and weapon Masteries;
- behavioral/persona foundations;
- raid history and deployment state;
- concrete EFT visual identity (Head, Body, Feet, Hands and Voice).

A released Operator is not deleted. They remain part of the Vanguard world and may return through the contract market with the same identity and persistent profile.

### Career genesis and cumulative migration

New Operators receive a deterministic career background based on EFT/SPT progression authority, archetype and stable Operator identity. That background is applied once; later raid progression remains the persistent truth.

Legacy Operator data is upgraded through **schema/marker-driven cumulative migration**. Migration is not keyed to a marketing version such as `0.7.8` or `0.7.9`: supported historical states are classified from their persisted schema/markers, converged in the required order, backed up, read back and kept idempotent. Unknown/newer/conflicting shapes fail closed rather than being guessed.

### Off-Raid management

Vanguard includes an Off-Raid management layer for:

- recruitment and contracts;
- Operator dossiers;
- equipment preparation;
- medical treatment and recovery;
- career, Skills/Masteries and raid-history views;
- deployment preparation;
- raid salary/economy foundations.

The broader relationship, loyalty and contract-variety systems remain future work.

### Persistent Off-Raid Operator session

Opening an Operator inventory creates a persistent Off-Raid authority context rather than a one-screen transaction. Compatible navigation such as Traders, Flea Market, Equipment Builds, Handbook, Messenger, Settings and contextual item actions can keep the same Operator session active. Returning to Character restores the current Operator inventory context, while Main Menu is the normal explicit completion boundary that commits once, exits Operator inventory mode and reconciles the player profile. Raid launch remains protected and Hideout remains fail-closed because its broader player graph is not qualified under temporary Operator authority.

### Main Menu squad preview

Vanguard 0.8.0 can render the selected squad directly in the main-menu presentation using EFT's native character rendering path.

The preview is a **read-only projection** of persistent Operator truth:

- 1 to 4 selected Operators;
- persistent VisualIdentity and equipment;
- dynamic selection and equipment refresh;
- unavailable/KIA Operators remain visible as an independent darkened **Shadow** state while selected;
- no preview read creates, repairs, rerolls or mutates persistent Operator data.

Both the Vanilla main menu and **Menu Overhaul** are supported. Menu Overhaul retains ownership of its PMC, camera, lights and viewport; Vanguard owns only its Operator presentation and never takes ownership of the Menu Overhaul PMC.

During raid loading the selected squad can remain visible until the effective BattleUI boundary, where Vanguard-owned preview bodies/cameras/lights are cleaned up.

### Main Menu placement profiles

Vanguard keeps a standalone button layout when no configured external menu topology is loaded. When a declared menu-plugin topology is present, Vanguard moves **only its own button** to an absolute normalized X/Y position and leaves SPT and third-party rows untouched.

`MainMenuPlacementProfiles.jsonc` contains documented profiles for Menu Overhaul, Career Log and their combined topology. Users can add GUID-qualified profiles; the most specific matching profile wins, and the F12 X/Y controls edit only the currently active profile.

### Squad behavior, not replacement combat AI

Vanguard coordinates the squad-level context required to keep persistent Operators functioning together, including:

- following and regrouping;
- spacing and cohesion;
- tactical movement and positioning;
- survival priorities;
- medical arbitration;
- persistent Operator context.

Individual combat intelligence remains primarily handled by **SAIN**. Vanguard builds squad context and persistence around the existing AI ecosystem rather than attempting to replace it wholesale.

Each Operator currently starts from a private clone of the loaded SAIN `Normal` personality. Vanguard layers Operator Persona/Specialty adjustments and bounded safety invariants over that private baseline while leaving SAIN's global `Normal` profile untouched.

**Player-issued squad orders are not currently implemented.**

### Looting and survival

Operators can perform configurable opportunistic looting for useful equipment, weapons, compatible ammunition, medical supplies and selected valuables.

Combat, medical urgency and squad cohesion remain higher priorities than opportunistic loot.

### Tactical Editor

Vanguard includes an in-raid **Tactical Editor** for creating tactical zones and Operator positioning slots directly inside EFT.

Basic workflow:

- **Ctrl+F6** — open/close the Tactical Editor;
- **Ctrl+Home** — create a zone at the player's position, type its name, then press **Enter**;
- **Ctrl+Insert** — place a tactical slot at the player's position; current look direction becomes the watch direction;
- **Ctrl+S** — save the authored layout.

Saved layouts are reused across raids. With automatic authored-zone occupancy enabled, Vanguard reloads the saved map and activates the relevant authored zone when its owner enters it. Persisted layouts are consumed by the active raid authority in standalone/local play, Fika direct-host play and Fika Headless. Author identity remains part of the live Tactical state, so another player's authored zone does not take control of your Operators.

Authored slots are tactical assignments, not RTS-style direct orders. Combat, grenade safety, medical needs and higher-priority authorities can interrupt an assignment; Vanguard can return the Operator when it becomes valid again.

Useful editing controls:

- **Ctrl+R** — reload the last saved layout;
- **Ctrl+PageDown** — select the next authored zone;
- **Ctrl+Shift+N** — rename the selected zone;
- **Ctrl+P** — move the nearest slot to the player's current position and recapture watch direction;
- **Ctrl+Delete** — disable/re-enable the nearest slot;
- **Ctrl+Shift+Delete** — delete the nearest slot;
- **Ctrl+Shift+Backspace** — delete the selected zone and its contained authored data;
- **Ctrl+V** — revalidate the selected zone.

## Supported execution topologies

Vanguard has been exercised in:

- **Standalone SPT / `EFT.LocalGame`**;
- **Fika Host**;
- **Fika dedicated Headless**.

Fika is required only for Fika topologies. The exact versions qualified for the shipping build are listed below.

## Requirements

Vanguard 0.8.0 targets **SPT 4.0.13**. The shipping release was qualified against this exact reference stack:

| Component | Qualified version | Status |
|---|---:|---|
| SPT | `4.0.13` | Required |
| MoreBotsAPI | `2.0.1` | Required |
| BigBrain | `1.4.0` | Required |
| Waypoints | `1.8.2` | Required |
| SAIN | `4.4.3` | Required |
| Fika Core | `2.3.9` | Required only for Fika topologies |
| Fika Server | `2.3.5` | Required only for Fika topologies |
| Fika Headless | `1.4.15` | Required only for dedicated-Headless topology |
| Looting Bots | `1.7.0` | Optional / recommended and tested |
| Menu Overhaul | `1.2.2` | Optional / supported and tested |

These are the **qualified shipping-reference versions**, not a promise that every newer upstream release is automatically compatible. When updating a dependency beyond this matrix, preserve a rollback copy and re-check Vanguard behavior.

Vanguard currently reserves MoreBotsAPI role IDs:

- `867100` — USEC Operator;
- `867101` — BEAR Operator.

## Compatibility philosophy

Vanguard owns the persistent player-allied Operator squad domain. Mods that also command, persist, reposition or otherwise own the same allied PMC entities can conflict even when both plugins load successfully.

Compatibility is classified by authority and runtime evidence rather than by “it starts without crashing” alone.

Menu Overhaul coexistence is supported in 0.8.0, including shared main-menu presentation without Vanguard taking ownership of Menu Overhaul's PMC/camera/lights. Career Log button coexistence continues to use placement profiles.

The release page may carry additional compatibility notes; the exact core shipping-reference versions are frozen in this README.

## Release status

Vanguard remains an **Alpha/Beta field-validation release**. The goal is to expose the persistent-Operator architecture to more machines, mod stacks and edge cases while keeping the core behavior stable enough to produce useful reports.

Priority during stabilization:

- reproducible bug fixes;
- compatibility triage;
- runtime stability;
- diagnostics and supportability;
- bounded Off-Raid quality-of-life improvements.

Large new gameplay systems are not the priority during stabilization.

## Diagnostics and startup health

Vanguard exposes four diagnostic levels through BepInEx configuration:

| Level | Intended use |
|---|---|
| **Off** | Minimal Vanguard logging. |
| **Operational** | Normal play and first-line support. |
| **Diagnostic** | Richer state/decision information for reproductions. |
| **Trace** | Short, targeted deep investigation; potentially very verbose. |

Change this setting from the BepInEx Configuration Manager (**F12 → Vanguard - Diagnostics → Audit level**).

The server also performs a bounded startup health check. Console output is intentionally readable (`PASS/WARN/FAIL`), while structured details remain in `vanguard-server.log`. Transactional checks use isolated/sandbox state and do not modify production Operator data.

When Operator data is loaded, Vanguard reports whether the schema/marker migration state is current, converged or unsupported. Marketing versions are diagnostic context only; migration authority remains persisted schema/markers.

For a useful bug report, include Vanguard/SPT/dependency versions, execution topology, reproduction steps and the relevant logs.

**Standalone SPT or Fika Host**

- `<SPT>\BepInEx\LogOutput.log`;
- current `<SPT Server>\user\logs\spt\spt*.log`;
- `<SPT Server>\user\vanguard\operators\vanguard-server.log`.

**Fika dedicated Headless**

- player client's `<SPT>\BepInEx\LogOutput.log`;
- Headless `<Headless SPT>\BepInEx\LogOutput.log`;
- current `<SPT Server>\user\logs\spt\spt*.log`;
- `<SPT Server>\user\vanguard\operators\vanguard-server.log`.

Use **Operational** for normal play, **Diagnostic** for support/reproduction, and **Trace** only for short targeted investigations.

## Installation

Use the release archive and instructions published for the selected topology. The public package is intended to be installed from the SPT root rather than by manually distributing individual Vanguard binaries.

Release-specific install/update/removal paths are finalized from the shipping package itself.

## Roadmap

Longer-term work is intended to deepen the identity of persistent Operators rather than turn Vanguard into a conventional follower-control mod.

Planned directions include richer relationships and loyalty/respect, contract types and durations, payment consequences, dynamic Operator events, emergent encounters, deeper careers, expanded Off-Raid management and additional tactical knowledge systems.

Future contract concepts include one-shot companions that do not remain in the roster, time-based contracts driven by server time, and Operators who may later propose work, extend/decline contracts or react to survival history and treatment. These are post-0.8.0 directions, not current shipping behavior.

### Persistent Operators beyond the raid

Vanguard's longer-term goal is to let real Operator history and raid events feed into conversations, personal stories, relationships, recruitment and assignments from the wider Vanguard network.

The project plans to explore a provider-independent **Vanguard Narrative API**. Narrative technologies such as **VisitAPI** are being evaluated as optional presentation providers; Vanguard Core would remain authoritative for identity, persistence and gameplay state.

Two official expansion concepts are being considered:

- **Vanguard — Operator Stories** — personal narratives/interactions driven by persistent Operators and raid history;
- **Vanguard — Network Assignments** — optional contracts, objectives and mission chains from the wider Vanguard network.

These are post-release roadmap goals with no committed delivery date.

## Building from source

Build Vanguard 0.8.0 against an **SPT 4.0.13** installation. Copy `client-spt4/Directory.Build.props.example` to `client-spt4/Directory.Build.props`, set `SptRoot`, and keep that local file out of commits. The three shipping projects share their release identity through `build/Vanguard.Version.props`.

Internal development KBs, handoffs, runtime evidence and private project artifacts are intentionally excluded from the public repository.

## AI-assisted development

AI tools have been used throughout Vanguard development, especially to ensure proper workflow organization and documentation, but also for extensive technical research and synthesis, review, debugging, code generation and corrections, and visual assets. Product direction, architecture, base code integration and decisions, testing, validation, and release authority remain under the author's control.

## License and third-party software

Vanguard is published under the **Mozilla Public License 2.0 (MPL-2.0)**.

SPT, EFT and Vanguard's external dependencies/integrations remain the property of their respective authors and are governed by their own licenses and terms. Required third-party notices will be included with the public source/release where applicable.
