# Reflection Dependency Manifest
## GameCreator2 Networking Layer

> **Purpose:** This document catalogs every use of runtime reflection in the networking
> layer. These are **fragility hotspots** — they access private/internal members of
> Game Creator 2 (and DaimahouGames Abilities) by string name and **will break silently**
> if the target type is renamed, moved, or refactored in a GC2 update.
>
> Review this manifest after every GC2 / Abilities package update.

---

## Risk Summary

| Risk   | Count | Description |
|--------|-------|-------------|
| **High**   | 19 | Accesses GC2 private fields by string name, or invokes patched static fields/methods |
| **Medium** | 7  | Patch-detection checks, type-name hashing, own-struct field reads |
| **Low**    | 5  | Standard .NET APIs, editor-only self-dispatch, diagnostic logging |

---

## 1. NetworkCharacter.cs — Character Driver Override

| Line(s) | Target | Access | Risk |
|----------|--------|--------|------|
| 607–617 | `CharacterKernel.m_Driver` (private) | `GetField` + `SetValue` | **HIGH** |

**What it does:** Swaps the GC2 Character's internal driver unit at runtime (e.g.
switching between server/client/remote driver implementations). The kernel has no public
setter for the driver, so reflection is the only way.

**Impact if broken:** Core character movement networking fails entirely — characters
cannot be driven remotely.

**Mitigation:** Check `field != null` before use; log an error if missing.

---

## 2. NetworkCorePatchHooks.cs — Patch Detection & Hook Injection

| Line(s) | Target | Access | Risk |
|----------|--------|--------|------|
| 158–161 | `Character.NetworkIsDeadValidator` (injected static) | `GetField` | **MEDIUM** |
| 176–179 | `T.IsNetworkingActive` (injected static) | `GetField` | **MEDIUM** |
| 415–418 | Any patched type's static field (by string name) | `GetField` + `SetValue` | **HIGH** |
| 429–432 | Any GC2 type's public instance method (by string name) | `GetMethod` + `Invoke` | **HIGH** |
| 443–448 | Any GC2 type's public instance method (by string name) | `GetMethod` + `Invoke` | **HIGH** |

**What it does:** Detects whether GC2 source files have been patched by the editor
patcher (by checking for injected static fields), then sets validator delegates and
calls lifecycle methods on GC2 types.

**Impact if broken:** Networking hooks fail to install — GC2 types won't consult the
server before performing actions (death, respawn, etc.).

**Mitigation:** All sites guard with `field != null` / `method != null` checks. The
field names (`NetworkIsDeadValidator`, `IsNetworkingActive`) are defined by the editor
patcher in `GC2PatchManager.cs` — keep both sides in sync.

---

## 3. NetworkAbilitiesPatchHooks.cs — Abilities Hook Injection

| Line(s) | Target | Access | Risk |
|----------|--------|--------|------|
| 99–102 | `Caster.NetworkCastValidator` (injected static) | `GetField` | **MEDIUM** |
| 298–300 | `Caster.NetworkCastValidator` | `GetField` + `SetValue` | **HIGH** |
| 312–314 | `Caster.NetworkCastValidator` | `GetField` + `GetValue` | **HIGH** |
| 326–328 | `Caster.NetworkLearnValidator` | `GetField` + `SetValue` | **HIGH** |
| 340–342 | `Caster.NetworkLearnValidator` | `GetField` + `GetValue` | **HIGH** |
| 354–356 | `Caster.NetworkUnLearnValidator` | `GetField` + `SetValue` | **HIGH** |
| 368–370 | `Caster.NetworkUnLearnValidator` | `GetField` + `GetValue` | **HIGH** |
| 382–384 | `Caster.NetworkCastCompleted` | `GetField` + `SetValue` | **HIGH** |
| 396–399 | `Caster.LearnDirect(Ability, int)` | `GetMethod` + `Invoke` | **HIGH** |
| 415–418 | `Caster.UnLearnDirect(Ability)` | `GetMethod` + `Invoke` | **HIGH** |

**What it does:** Injects networking validators into the DaimahouGames `Caster` class
so that cast/learn/unlearn operations are routed through the server. Also calls
`LearnDirect` / `UnLearnDirect` to bypass the client-side validation on the server.

**Impact if broken:** Abilities will either not be castable in multiplayer, or will
bypass server authority entirely.

**Patched field names (defined by editor patcher):**
- `NetworkCastValidator` — `Func<Caster, Ability, bool>` delegate
- `NetworkLearnValidator` — `Func<Caster, Ability, int, bool>` delegate
- `NetworkUnLearnValidator` — `Func<Caster, int, bool>` delegate
- `NetworkCastCompleted` — `Action<Caster, Ability, bool>` delegate
- `LearnDirect` / `UnLearnDirect` — methods patched into `Caster`

---

## 4. NetworkMeleeController.cs — Melee Stance Internals

| Line(s) | Target | Access | Risk |
|----------|--------|--------|------|
| 191–192 | `MeleeStance.m_Attacks` (private) | `GetField` (cached in static ctor) | **HIGH** |
| 421 | `MeleeStance.m_Attacks` → `Attacks` | `GetValue` | **HIGH** |
| 470 | `MeleeStance.m_Attacks` → `Attacks.ComboSkill` | `GetValue` | **HIGH** |
| 1203 | `IReaction` impl type name | `GetType().FullName` | **MEDIUM** |

**What it does:** Reads the private `m_Attacks` field from GC2 Melee's `MeleeStance`
to determine the current weapon/combo state for network synchronization. The reaction
type name is used for deterministic hashing.

**Impact if broken:** Melee weapon state won't sync; players won't see each other's
attacks/combos.

**Target field:** `GameCreator.Runtime.Melee.MeleeStance.m_Attacks` (private, type
`Attacks`).

---

## 5. ConditionNetworkMeleeHit.cs — Melee Condition Check

| Line(s) | Target | Access | Risk |
|----------|--------|--------|------|
| 97–102 | `MeleeStance.m_Attacks` (private) | `GetField` + `GetValue` | **HIGH** |

**What it does:** Reads the same private field as `NetworkMeleeController` to check
melee hit conditions in visual scripting nodes.

**Shares fragility with:** `NetworkMeleeController.cs` — both break together if
`m_Attacks` is renamed.

---

## 6. UnitIKNetworkController.cs — IK Rig Access

| Line(s) | Target | Access | Risk |
|----------|--------|--------|------|
| 119–123 | `IUnitAnimim` impl → `m_Rigs` (private) | `GetField` + `GetValue` | **HIGH** |

**What it does:** Accesses the private `m_Rigs` field from GC2's animation unit to
find LookAt and Aim IK rigs for network synchronization (head/weapon aiming).

**Impact if broken:** IK aiming/look direction won't sync between players.

**Target field:** Implementation of `IUnitAnimim` (likely `UnitAnimim`) → `m_Rigs`
(private, type `RigLayers`).

---

## 7. NetworkVariableSync.cs — GC2 Variable System Access

| Line(s) | Target | Access | Risk |
|----------|--------|--------|------|
| 472–473 | Any value type | `GetType().Name` | **LOW** (diagnostic) |
| 572–576 | `LocalNameVariables.m_Runtime` (private) | `GetField` + `GetValue` | **HIGH** |
| 584–586 | `NameVariableRuntime.m_Map` (private) | `GetField` + `GetValue` | **HIGH** |
| 592–594 | `NameVariableRuntime.m_Data` (private, fallback) | `GetField` + `GetValue` | **HIGH** |
| 604–607 | `Dictionary<K,V>.Keys` (.NET standard) | `GetProperty` + `GetValue` | **LOW** |

**What it does:** Drills into GC2's variable runtime to enumerate all named variables
for network synchronization. The `m_Map` / `m_Data` fallback already accounts for one
known rename.

**Impact if broken:** Named variables won't sync between clients.

**Target chain:** `LocalNameVariables` → `m_Runtime` (private) → `m_Map` or `m_Data`
(private Dictionary).

---

## 8. NetworkInventoryController.Server.cs — Pending Request Cleanup

| Line(s) | Target | Access | Risk |
|----------|--------|--------|------|
| 919–922 | Own structs `PendingAdd.SentTime`, etc. | `GetField` + `GetValue` | **MEDIUM** |

**What it does:** Generic timeout cleanup reads `SentTime` from internal pending
request structs by string name.

**Impact if broken:** Timed-out inventory requests won't be cleaned up.

**Mitigation:** Consider using an interface (`IPendingRequest { float SentTime; }`)
instead of reflection to make this compile-time safe.

---

## 9. StableHashUtility.cs — Type Name Hashing

| Line(s) | Target | Access | Risk |
|----------|--------|--------|------|
| 36 | `Component.GetType().FullName` | `GetType()` | **MEDIUM** |
| 44 | `UnityEngine.Object.GetType().FullName` | `GetType()` | **MEDIUM** |

**What it does:** Creates deterministic hash codes from fully-qualified type names for
network identification of ScriptableObject assets (abilities, projectiles, impacts).

**Impact if broken:** Hash mismatch between clients if a type is renamed or moved to a
different namespace — assets won't be found on the receiving end.

---

## 10. GC2PatchManager.cs — Editor Dynamic Dispatch (Editor-only)

| Line(s) | Target | Access | Risk |
|----------|--------|--------|------|
| 361–363 | `GC2PatchManager.Patch{Module}()` (own type) | `GetMethod` + `Invoke` | **LOW** |
| 369–371 | `GC2PatchManager.Unpatch{Module}()` (own type) | `GetMethod` + `Invoke` | **LOW** |

**What it does:** Calls `PatchCore()`, `PatchMelee()`, etc. dynamically based on UI
button selection in the editor.

**Impact if broken:** Editor patch buttons stop working; runtime unaffected.

---

## Critical Fragility Hotspots (Ordered by Impact)

1. **`CharacterKernel.m_Driver`** — Single point of failure for all character
   movement networking.
2. **`MeleeStance.m_Attacks`** — Used in 3 places across 2 files. Melee networking
   breaks entirely if renamed.
3. **`Caster.*` patched fields** (6 fields, 10 reflection sites) — Abilities
   networking depends on all of these.
4. **`LocalNameVariables.m_Runtime` → `m_Map`/`m_Data`** — Variable sync chain;
   already has one fallback baked in.
5. **`IUnitAnimim.m_Rigs`** — IK synchronization depends on animation internals.

---

## Recommended Actions After GC2 Update

1. **Search for renamed fields:** Run `grep -r "m_Attacks\|m_Driver\|m_Runtime\|m_Map\|m_Rigs"` in the GC2 package to verify field names still exist.
2. **Verify patched types:** Open `Character.cs`, `Caster.cs` in the GC2 source and
   confirm the injected static fields are present.
3. **Run in editor:** The patch detection methods
   (`CheckCorePatches`/`CheckAbilityPatches`) will log warnings if patched fields are
   missing — check the Console for `[Patch]` warnings on domain reload.
4. **Test hash stability:** If any GC2 ScriptableObject types were moved/renamed,
   `StableHashUtility` hashes will change — retest asset lookups.
