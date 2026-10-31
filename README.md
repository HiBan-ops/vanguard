# Vanguard

> **Vanguard is not a faction. It is a network.**

**Persistent Operators. Independent behavior. Shared survival.**

Vanguard turns allied PMC companions into **persistent Operators** whose identity, equipment, condition, progression and history continue beyond a single raid.

You build a roster, prepare Operators Off-Raid, deploy with them, survive together and deal with the consequences when things go sideways.

> **The player is part of the squad, not a puppeteer above it.**

Vanguard is designed around Operators as independent combat entities rather than disposable followers waiting for constant player input.

**Vanguard 0.8.7** targets **SPT 4.0.13**. See the compatibility matrix below before installing or updating dependencies.

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

The selected squad can be rendered directly in the main-menu presentation using EFT's native character rendering path.

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

Looting is opportunistic and requires an admissible safety window. Operators can temporarily leave a Tactical Authoring HOLD or interrupt normal Cohesion movement to loot, then return to the applicable HOLD while the player remains inside the authored zone, or resume Cohesion outside it. Combat authority, dangerous grenades and urgent medical needs can interrupt looting.

### Tactical Editor

Vanguard includes an in-raid **Tactical Editor** for creating tactical zones and Operator positioning slots directly inside EFT.

Basic workflow:

- **Ctrl+F6** — open/close the Tactical Editor;
- **Ctrl+Home** — create a zone at the player's position, type its name, then press **Enter**;
- **Ctrl+Insert** — place a tactical slot at the player's position; current look direction becomes the watch direction;
- **Ctrl+S** — save the authored layout.

Saved layouts are reused across raids. With automatic authored-zone occupancy enabled, entering one of your saved zones activates its available HOLD slots automatically, **without opening the Tactical Editor**. While the player remains in that zone, HOLD slots take precedence over ordinary Travel/Cohesion positioning, subject to safety, medical, loot and combat arbitration. Leaving the zone releases its HOLD occupancy and returns movement to Cohesion when no higher authority is active. Persisted layouts are consumed by the active raid authority in standalone/local play, Fika direct-host play and Fika Headless. Author identity remains part of the live Tactical state, so another player's authored zone does not take control of your Operators.

Authored slots are tactical positioning assignments, not RTS-style direct orders. Operators may temporarily interrupt a HOLD for a dangerous grenade, admissible medical care or safe looting. During active combat SAIN retains combat movement authority; ordinary HOLD, looting and Cohesion movement do not override it. When combat ends, movement is reconsidered against the player's current authored-zone occupancy.

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

Vanguard 0.8.7 targets **SPT 4.0.13**. The following is the reference compatibility stack inherited from the validated development baseline; it is not a guarantee of support for untested later dependency versions:

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

These are the **reference versions used for compatibility work**. The final packaged build and release integrity must be checked before publication. Newer versions of these dependencies are not automatically supported; keep rollback copies when changing your mod stack.

Vanguard currently reserves MoreBotsAPI role IDs:

- `867100` — USEC Operator;
- `867101` — BEAR Operator.

## Compatibility philosophy

Vanguard owns the persistent player-allied Operator squad domain. Mods that also command, persist, reposition or otherwise own the same allied PMC entities can conflict even when both plugins load successfully.

Compatibility is classified by authority and runtime evidence rather than by “it starts without crashing” alone.

Menu Overhaul coexistence is supported, including shared main-menu presentation without Vanguard taking ownership of Menu Overhaul's PMC/camera/lights. Career Log button coexistence continues to use placement profiles.

The release page may carry additional compatibility notes; the exact core shipping-reference versions are frozen in this README.

## Release status

**Vanguard 0.8.7 is currently a pre-release source candidate, not an approved public distribution.** The installation layout below describes the intended official archive; it does not certify that a publishable archive exists. Final release requires source-to-binary provenance, independent package integrity checks, a consistent installed client/server version, runtime qualification and resolution of the embedded-artwork redistribution gate recorded in `MANIFEST.txt`.

The stabilization goal is to expose the persistent-Operator architecture to broader field validation only after those release requirements have been met, while keeping the core behavior stable enough to produce useful reports.

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

1. Back up your SPT installation and existing Vanguard data before upgrading. Do not delete persistent Operator data during an ordinary update.
2. Install the required compatible SPT and AI dependencies listed above. Fika components are required only for the corresponding Fika topology.
3. Once a qualified public release has been issued, extract the official `Vanguard-0.8.7-SPT-4.0.13.zip` archive at the installation root containing the **`BepInEx`** and **`SPT`** folders (for example, `E:\Games\SPT\`). The ZIP contains those two folders directly at its root. Merge both folders into the existing installation; do not extract into a new version-named subdirectory. The `SPT` folder is intentional and must be preserved.
4. Verify `BepInEx/plugins/Vanguard.Client/Vanguard.Client.dll`, `BepInEx/patchers/Vanguard.MoreBotsPrePatch/Vanguard.MoreBotsPrePatch.dll` and `SPT/user/mods/Vanguard.Server/Vanguard.Server.dll` relative to that outer installation root. Launch the SPT server and game and review Vanguard startup health diagnostics.
5. For updates and removal, preserve your persistent Operator data and configuration backups; removing the three Vanguard assemblies is not equivalent to deleting saved Operator records.

The actual shipping archive layout, file hashes and final upgrade/removal instructions must be verified against the frozen public release package. Do not install development-only handoffs or `SCOPED_SOURCE` patch archives as public releases.

## Roadmap

Longer-term work is intended to deepen the identity of persistent Operators rather than turn Vanguard into a conventional follower-control mod.

Planned directions include richer relationships and loyalty/respect, contract types and durations, payment consequences, dynamic Operator events, emergent encounters, deeper careers, expanded Off-Raid management and additional tactical knowledge systems.

Future contract concepts include one-shot companions that do not remain in the roster, time-based contracts driven by server time, and Operators who may later propose work, extend/decline contracts or react to survival history and treatment. These are future directions, not current shipping behavior.

### Persistent Operators beyond the raid

Vanguard's longer-term goal is to let real Operator history and raid events feed into conversations, personal stories, relationships, recruitment and assignments from the wider Vanguard network.

The project plans to explore a provider-independent **Vanguard Narrative API**. Narrative technologies such as **VisitAPI** are being evaluated as optional presentation providers; Vanguard Core would remain authoritative for identity, persistence and gameplay state.

Two official expansion concepts are being considered:

- **Vanguard — Operator Stories** — personal narratives/interactions driven by persistent Operators and raid history;
- **Vanguard — Network Assignments** — optional contracts, objectives and mission chains from the wider Vanguard network.

These are post-release roadmap goals with no committed delivery date.

## Building from source

The repository contains three independently buildable C# projects. Build Vanguard **0.8.7** with the **.NET 9 SDK**, using an **SPT 4.0.13** installation and the compatible dependencies listed above. The client and pre-patch projects reference local SPT/EFT, BepInEx, BigBrain and MoreBotsAPI assemblies; those binaries are **not included in this source repository**. The server project restores its declared NuGet dependencies.

On Windows, start in the repository root:

```powershell
Copy-Item client-spt4/Directory.Build.props.example client-spt4/Directory.Build.props
```

Edit `client-spt4/Directory.Build.props` and point `SptRoot` to your own SPT installation. Check that the referenced game, BepInEx, BigBrain and MoreBotsAPI DLL paths exist. This file is machine-specific and excluded by `.gitignore`; **do not commit it**.

Build the shipping targets separately (there is no requirement to build the client's alternative `net9.0` target):

```powershell
dotnet build client-spt4/Vanguard.Client/Vanguard.Client.csproj -c Release -f netstandard2.1 --nologo
dotnet build server-spt4/Vanguard.Server/Vanguard.Server.csproj -c Release -f net9.0 --nologo
dotnet build client-spt4/Vanguard.MoreBotsPrePatch/Vanguard.MoreBotsPrePatch.csproj -c Release -f netstandard2.1 --nologo
```

The three projects import `build/Vanguard.Version.props` for their shared semantic version. The build outputs are `Vanguard.Client.dll`, `Vanguard.Server.dll` and `Vanguard.MoreBotsPrePatch.dll`; see the installation section for their eventual locations in SPT. A successful local compilation **does not establish** Fika/SAIN/runtime compatibility or certify a release archive. Public release packages require a frozen source snapshot, matching DLL SHA-256 values and separate validation of the installation layout.

## AI-assisted development

AI tools have been used throughout Vanguard development, especially to ensure proper workflow organization and documentation, but also for extensive technical research and synthesis, review, debugging, code generation and corrections, and visual assets. Product direction, architecture, base code integration and decisions, testing, validation, and release authority remain under the author's control.

## License and third-party software

Vanguard is published under the **Mozilla Public License 2.0 (MPL-2.0)**.

SPT, EFT and Vanguard's external dependencies/integrations remain the property of their respective authors and are governed by their own licenses and terms. Required third-party notices will be included with the public source/release where applicable.

The client project embeds 14 Operator portrait JPEGs and 2 interface PNGs. `MANIFEST.txt` records the exact source paths and SHA-256 values for all 16. This is a file-integrity inventory, **not an attribution record or a license grant**. Asset authorship, applicable licenses, notices and redistribution permissions must be independently established before public source or binary distribution; the MPL-2.0 declaration for Vanguard code does not prove rights to these assets.
