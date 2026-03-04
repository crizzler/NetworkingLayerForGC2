# GC2 Networking Protocol v2 Migration (Hard Break)

## Effective Change
This networking layer now requires **Protocol v2** request/response context in gameplay modules.

Required context fields:
- `ActorNetworkId`
- `CorrelationId`

Requests missing either field are rejected as `ProtocolMismatch`.

## Ownership Enforcement
Server request entrypoints now enforce strict sender ownership:
- sender must own `ActorNetworkId`
- mismatches are rejected and recorded as security violations
- unresolved ownership is rejected (no gameplay-path ownership learning fallback)

## Correlation Routing
Client pending request resolution is now correlation-first:
- responses route by `CorrelationId`
- fallback to legacy `RequestId` is retained only for compatibility safety

## Deployment Requirement
This is a **single-version deploy** change:
- all clients and servers must run protocol-v2-capable builds
- mixed v1/v2 gameplay request traffic is unsupported

## Integration Checklist
1. Ensure transport forwards authoritative sender client IDs for all server receives.
2. Ensure all gameplay requests include `ActorNetworkId` + `CorrelationId`.
3. Ensure ownership maps are populated for character and module-owned entities.
4. Validate security manager module limits for combat vs non-combat traffic.
