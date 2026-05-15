# Reflection Dependency Manifest
## GameCreator2 Networking Layer

> Purpose: track all runtime/editor reflection dependencies in this package so GC2/Abilities updates can be validated quickly.
> 
> Scope: `Assets/Arawn/NetworkingLayerForGC2/` (current codebase).

---

## Risk Summary (Current)

| Risk | Hotspots | Description |
|---|---:|---|
| High | 13 | Private/internal GC2 member access or patched-field injection points that can break on upstream rename/signature drift |
| Medium | 3 | Dynamic shape-based member lookups and reflective invocation by string names |
| Low | 1 | Editor-only dynamic dispatch |

---

## Scope Note

This manifest includes two reflection categories:

- Always-on runtime dependencies (active in interception/fallback mode).
- Optional patched-mode dependencies (only relevant when GC2 patchers are applied and patch hooks are enabled).

## Why Optional Patched Mode Uses Reflection

Patched mode intentionally uses reflective binding instead of hard compile-time references to patched members for these reasons:

- Keep GC2 source edits minimal: patchers inject a narrow hook surface instead of large structural rewrites.
- Preserve compatibility: projects still compile and run in interception/fallback mode when patches are not applied.
- Reduce upgrade friction: GC2 updates are more likely to need patch-rule updates than full networking-layer recompilation against changed patched signatures.
- Enable graceful degradation: hook installers can probe for patched members at runtime and disable patched mode safely when signatures drift.

---

## High-Risk Reflection Hotspots

### 1) Core patched hook contract (optional patched mode)
- File: `Assets/Arawn/NetworkingLayerForGC2/Core/NetworkCorePatchHooks.cs`
- Access: `GetField`, `GetProperty`, `GetMethod`, `Invoke`, `SetValue`
- Exact targets:
  - `Invincibility.NetworkSetValidator`, `Invincibility.NetworkInvincibilitySet`, `Invincibility.IsNetworkingActive`, `Invincibility.SetDirect(...)`
  - `Poise.NetworkDamageValidator`, `Poise.NetworkSetValidator`, `Poise.NetworkResetValidator`, `Poise.NetworkPoiseDamaged`, `Poise.IsNetworkingActive`, `Poise.DamageDirect(...)`, `Poise.SetDirect(...)`, `Poise.ResetDirect(...)`
  - `Jump.NetworkJumpValidator`, `Jump.NetworkJumpForceValidator`, `Jump.NetworkJumpExecuted`, `Jump.IsNetworkingActive`, `Jump.DoDirect(...)`
  - `Dash.NetworkDashValidator`, `Dash.NetworkDashStarted`, `Dash.NetworkDashFinished`, `Dash.IsNetworkingActive`, `Dash.ExecuteDirect(...)`
  - `Character.NetworkIsDeadValidator`, `Character.NetworkDeathStateChanged`, `Character.IsNetworkingActive`, `Character.SetIsDeadDirect(...)`

### 2) Abilities patched hook contract (optional patched mode)
- File: `Assets/Arawn/NetworkingLayerForGC2/Abilities/NetworkAbilitiesPatchHooks.cs`
- Access: `GetField`, `GetProperty`, `GetMethod`, `Invoke`, `SetValue`, `GetValue`
- Exact targets:
  - `Caster.NetworkCastValidator`
  - `Caster.NetworkLearnValidator`
  - `Caster.NetworkUnLearnValidator`
  - `Caster.NetworkCastCompleted`
  - `Caster.IsNetworkingActive`
  - `Caster.LearnDirect(...)`
  - `Caster.UnLearnDirect(...)`

### 3) Inventory patched hook contract (optional patched mode)
- File: `Assets/Arawn/NetworkingLayerForGC2/Inventory/NetworkInventoryPatchHooks.cs`
- Access: `GetField`, `GetProperty`, `GetMethod`, `SetValue`
- Exact targets:
  - `TBagContent.NetworkAddValidator`
  - `TBagContent.NetworkRemoveValidator`
  - `TBagContent.NetworkMoveValidator`
  - `TBagContent.NetworkDropValidator`
  - `TBagContent.NetworkUseValidator`
  - `TBagContent.IsNetworkingActive`
  - `TBagContent.UseDirect(...)`
  - `TBagContent.DropDirect(...)`
  - `BagWealth.NetworkAddValidator`
  - `BagWealth.NetworkSetValidator`
  - `BagWealth.IsNetworkingActive`
  - `BagWealth.SetDirect(...)`
  - `BagWealth.AddDirect(...)`

### 4) Stats patched hook contract (optional patched mode)
- File: `Assets/Arawn/NetworkingLayerForGC2/Stats/NetworkStatsPatchHooks.cs`
- Access: `GetField`, `GetProperty`, `GetMethod`, `SetValue`
- Exact targets:
  - `RuntimeStatData.NetworkBaseValidator`
  - `RuntimeStatData.NetworkAddModifierValidator`
  - `RuntimeStatData.NetworkRemoveModifierValidator`
  - `RuntimeStatData.NetworkClearModifiersValidator`
  - `RuntimeStatData.IsNetworkingActive`
  - `RuntimeStatData.SetBaseDirect(...)`
  - `RuntimeStatData.AddModifierDirect(...)`
  - `RuntimeStatData.RemoveModifierDirect(...)`
  - `RuntimeStatData.ClearModifiersDirect(...)`
  - `RuntimeAttributeData.NetworkValueValidator`
  - `RuntimeAttributeData.IsNetworkingActive`
  - `RuntimeAttributeData.SetValueDirect(...)`

### 5) Shooter patched hook contract (optional patched mode)
- File: `Assets/Arawn/NetworkingLayerForGC2/Shooter/NetworkShooterPatchHooks.cs`
- Access: `GetField`, `GetProperty`, `GetMethod`, `SetValue`
- Exact targets:
  - `ShooterStance.NetworkPullTriggerValidator`
  - `ShooterStance.NetworkReleaseTriggerValidator`
  - `ShooterStance.NetworkReloadValidator`
  - `ShooterStance.IsNetworkingActive`
  - `ShooterStance.PullTriggerDirect(...)`
  - `ShooterStance.ReleaseTriggerDirect(...)`
  - `ShooterStance.ReloadDirect(...)`
  - `WeaponData.NetworkShootValidator`
  - `WeaponData.NetworkShotFired`
  - `WeaponData.IsNetworkingActive`
  - `WeaponData.ShootDirect(...)`

### 6) Melee patched hook contract (optional patched mode)
- File: `Assets/Arawn/NetworkingLayerForGC2/Melee/NetworkMeleePatchHooks.cs`
- Access: `GetField`, `GetProperty`, `GetMethod`, `SetValue`
- Exact targets:
  - `MeleeStance.NetworkInputChargeValidator`
  - `MeleeStance.NetworkInputExecuteValidator`
  - `MeleeStance.NetworkPlaySkillValidator`
  - `MeleeStance.NetworkPlayReactionValidator`
  - `MeleeStance.IsNetworkingActive`
  - `MeleeStance.InputChargeDirect(...)`
  - `MeleeStance.InputExecuteDirect(...)`
  - `MeleeStance.PlaySkillDirect(...)`
  - `MeleeStance.PlayReactionDirect(...)`
  - `Skill.NetworkOnHitValidator`
  - `Skill.IsNetworkingActive`
  - `Skill.OnHit(...)`

### 7) Quests patched hook contract (optional patched mode)
- File: `Assets/Arawn/NetworkingLayerForGC2/Quests/NetworkQuestsPatchHooks.cs`
- Access: `GetField`, `SetValue`
- Exact targets (`Journal` static validators):
  - `NetworkActivateQuestValidator`
  - `NetworkDeactivateQuestValidator`
  - `NetworkActivateTaskValidator`
  - `NetworkDeactivateTaskValidator`
  - `NetworkCompleteTaskValidator`
  - `NetworkAbandonTaskValidator`
  - `NetworkFailTaskValidator`
  - `NetworkSetTaskValueValidator`
  - `NetworkTrackQuestValidator`
  - `NetworkUntrackQuestValidator`
  - `NetworkUntrackAllValidator`

### 8) Dialogue patched hook contract (optional patched mode)
- File: `Assets/Arawn/NetworkingLayerForGC2/Dialogue/NetworkDialoguePatchHooks.cs`
- Access: `GetField`, `SetValue`
- Exact targets:
  - `Dialogue.NetworkPlayValidator`
  - `Dialogue.NetworkStopValidator`
  - `Story.NetworkContinueValidator`
  - `NodeTypeChoice.NetworkChooseValidator`

### 9) Traversal patched hook contract (optional patched mode)
- File: `Assets/Arawn/NetworkingLayerForGC2/Traversal/NetworkTraversalPatchHooks.cs`
- Access: `GetField`, `SetValue`
- Exact targets:
  - `TraverseLink.NetworkRunValidator`
  - `TraverseInteractive.NetworkEnterValidator`
  - `TraversalStance.NetworkTryCancelValidator`
  - `TraversalStance.NetworkForceCancelValidator`
  - `TraversalStance.NetworkTryJumpValidator`
  - `TraversalStance.NetworkTryActionValidator`
  - `TraversalStance.NetworkTryStateEnterValidator`
  - `TraversalStance.NetworkTryStateExitValidator`

### 10) Quests runtime private member access
- File: `Assets/Arawn/NetworkingLayerForGC2/Quests/NetworkQuestsController.cs`
- Access: `GetField`, `GetMethod`, `GetValue`, `Invoke`
- Exact targets:
  - `TaskKey.m_QuestHash` (private field)
  - `Journal.OnRemember(...)` (non-public method)
  - `Journal.EventQuestChange` (non-public delegate field)
  - `Journal.EventTaskChange` (non-public delegate field)
  - `Journal.EventTaskValueChange` (non-public delegate field)

### 11) Inventory runtime private member access
- Files:
  - `Assets/Arawn/NetworkingLayerForGC2/Inventory/NetworkInventoryController.Server.cs`
  - `Assets/Arawn/NetworkingLayerForGC2/Inventory/NetworkInventoryController.Server.SyncAndHelpers.cs`
- Access: `GetField`, `SetValue`
- Exact targets:
  - `RuntimeSocket.m_AttachmentRuntimeItem` (private field, written during socket broadcast apply and runtime reconstruction)
  - `RuntimeItem.m_RuntimeID` (private field, written during runtime reconstruction and stack identity restore)

### 12) Melee runtime private member access
- Files:
  - `Assets/Arawn/NetworkingLayerForGC2/Melee/NetworkMeleeController.cs`
  - `Assets/Arawn/NetworkingLayerForGC2/Melee/VisualScripting/ConditionNetworkMeleeHit.cs`
- Access: `GetField`, `GetValue`
- Exact target:
  - `MeleeStance.m_Attacks` (private field)

### 13) Variables runtime private member access
- File: `Assets/Arawn/NetworkingLayerForGC2/Variables/NetworkVariableSync.cs`
- Access: `GetField`, `GetValue`
- Exact targets:
  - `LocalNameVariables.m_Runtime` (private)
  - Name entries are enumerated via public `NameVariableRuntime` iterator (`foreach (NameVariable ...)`)
- Risk:
  - If `LocalNameVariables.m_Runtime` is renamed/removed, name snapshot enumeration will fail.

---

## Medium-Risk Reflection Hotspots

### 1) Dynamic combat member probing
- File: `Assets/Arawn/NetworkingLayerForGC2/Combat/NetworkCombatInterceptor.cs`
- Access: dynamic `GetProperty`/`GetField` by string + fallback conversion
- Exact probed member names:
  - `Id`, `Hash`, `Weapon`, `Target`, `Point`, `Direction`
- Risk:
  - Upstream shape changes in hit payload objects can degrade interception without compile errors.

### 2) Core reflective direct invocation path
- File: `Assets/Arawn/NetworkingLayerForGC2/Core/NetworkCorePatchHooks.cs`
- Access: `GetMethod` + `Invoke` (`TryCallMethod`, `TryCallMethod<TResult>`) by method name strings
- Exact invoked names:
  - `SetDirect`, `DamageDirect`, `SetIsDeadDirect`
- Risk:
  - Method rename/signature drift degrades patched-mode direct execution path.

### 3) Abilities reflective direct invocation path
- File: `Assets/Arawn/NetworkingLayerForGC2/Abilities/NetworkAbilitiesPatchHooks.cs`
- Access: `GetMethod` + `Invoke`
- Exact invoked names:
  - `LearnDirect`, `UnLearnDirect`
- Risk:
  - Rename/signature drift causes fallback-only behavior.

---

## Low-Risk Reflection Hotspots

### 1) Editor-only patch UI dispatch
- File: `Assets/Arawn/NetworkingLayerForGC2/Editor/Patches/GC2PatchManager.cs`
- Access: `typeof(GC2PatchManager).GetMethod(methodName)?.Invoke(...)`
- Exact target pattern:
  - `Patch{Module}` / `Unpatch{Module}`
- Risk:
  - Affects patch window UX only; runtime networking unaffected.

---

## Quick Validation Checklist After GC2 / Module Updates

1. Re-run patch status checks in the patch manager for all installed modules.
2. Verify all `Network*PatchHooks.Is*Patched()` checks still pass.
3. Smoke-test runtime flows that depend on private members:
   - Melee (`m_Attacks`)
   - Quests snapshot/apply (`TaskKey`, `Journal` internals)
   - Variables runtime map (`m_Runtime`)
   - Inventory socket attachment reconstruction (`m_AttachmentRuntimeItem`)
4. If a hook fails, update the corresponding patcher and this manifest together.
