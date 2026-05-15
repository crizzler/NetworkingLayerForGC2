# Patching Strategy

This page explains when GC2 source patching is necessary, and when it is not.

## Default Recommendation

Start with **no patching**.

Use transport wiring + authoritative server routing + security checks first.  
For many coop and small/mid-size games, interception/fallback mode is sufficient.

## Why Not Patch Immediately

- Lower maintenance during early development.
- Less coupling to GC2 source signatures while gameplay is still changing.
- Faster upgrade path when GC2 modules update.
- Smaller operational risk while player count is low.

## When To Move To Patching

Apply patchers when your game shows traction and abuse risk rises, for example:

- Confirmed cheating attempts in live sessions.
- Repeated authority bypass attempts in server logs.
- Competitive PvP/ranked gameplay where trust boundaries must be tighter.
- Economy abuse (item/currency/socket manipulation attempts).
- Larger concurrency where exploit automation becomes likely.

## Module Priority (Recommended)

Patch in this order for competitive hardening:

1. `Shooter` / `Melee` (combat authority)
2. `Inventory` / `Stats` / `Core` (economy + core state)
3. `Abilities` (if enabled and competitive-relevant)
4. `Quests` / `Dialogue` / `Traversal` (when strict authority is required for your design)

## Rollout Plan

1. Keep a clean backup branch before patching.
2. Apply patchers from `Game Creator > Networking Layer > Patches`.
3. Verify patch status for every enabled module.
4. Run host + dedicated server + multi-client smoke tests.
5. Run forged-request/ownership abuse tests.
6. Ship patched mode only after verification passes.

## Rollback

If a GC2 update changes signatures and patch verification fails:

1. Use `Unpatch` for the affected module.
2. Continue in interception/fallback mode.
3. Update patcher rules, then re-apply and re-test.

## Is Patching Required?

No. Patching is optional by design.

- **Coop / most flows:** start unpatched.
- **Competitive and abuse-prone environments:** patch relevant modules.

## Related Docs

- `Assets/Arawn/NetworkingLayerForGC2/Documentation/TRANSPORT_QUICKSTART.md`
- `Assets/Arawn/NetworkingLayerForGC2/Documentation/PUBLIC_API.md`
- `Documentation/REFLECTION_DEPENDENCIES.md`
