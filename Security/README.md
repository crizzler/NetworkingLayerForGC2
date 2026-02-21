# GC2 Networking Security System

## Overview

The GC2 Networking Security System provides comprehensive server-authoritative protection for all Game Creator 2 networking modules. It includes:

- **Rate Limiting**: Prevents request spam from clients
- **Sequence Validation**: Detects replay attacks
- **State Validation**: Detects state desynchronization and cheating
- **Violation Tracking**: Logs, warns, blocks, or kicks malicious clients
- **Optional Source Patches**: Deep integration for maximum security

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    NetworkSecurityManager                        │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐             │
│  │ Rate Limiter │ │  Violation   │ │   Sequence   │             │
│  │  (per module)│ │   Tracker    │ │   Tracker    │             │
│  └──────────────┘ └──────────────┘ └──────────────┘             │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                   SecurityIntegration                            │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐             │
│  │ValidateCore()│ │ValidateStats │ │ValidateMelee │             │
│  │ValidateShooter│ │ValidateInventory│ │ValidateAbilities │       │
│  └──────────────┘ └──────────────┘ └──────────────┘             │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                   Network Controllers                            │
│  NetworkStatsController | NetworkInventoryController | etc.     │
└─────────────────────────────────────────────────────────────────┘
```

## Quick Start

### 1. Add NetworkSecurityManager to Scene

```csharp
// Add to your NetworkManager or a persistent object
[RequireComponent(typeof(NetworkSecurityManager))]
public class MyNetworkManager : MonoBehaviour
{
    private void Start()
    {
        var security = GetComponent<NetworkSecurityManager>();
        
        // Initialize on server
        security.Initialize(
            isServer: true, 
            getServerTime: () => Time.time
        );
        
        // Set up kick handler
        security.KickClient = (clientId, reason) =>
        {
            // Your network layer's kick implementation
            MyNetworkTransport.KickClient(clientId, reason);
        };
    }
}
```

### 2. Configure Security Settings

In the Inspector on NetworkSecurityManager:

| Setting | Default | Description |
|---------|---------|-------------|
| Enable Rate Limiting | true | Limit requests per second |
| Max Requests Per Second | 20 | Per-module request limit |
| Enable Anomaly Detection | true | Track violation patterns |
| Violation Threshold | 5 | Violations before action |
| Violation Action | TempBlock | LogOnly/LogAndWarn/TempBlock/Kick |
| Temp Block Duration | 30s | Block duration for TempBlock |
| Enable State Validation | true | Periodic state checks |

### 3. Integrate with Controllers

```csharp
// In your server-side request processor:
public NetworkStatModifyResponse ProcessStatModifyRequest(
    NetworkStatModifyRequest request, 
    uint clientNetworkId)
{
    // Validate using SecurityIntegration
    if (!SecurityIntegration.ValidateStatsRequest(
        clientNetworkId, 
        request.TargetNetworkId,
        request.RequestId, 
        "StatModify",
        request.StatHash, 
        request.Value))
    {
        return new NetworkStatModifyResponse
        {
            RequestId = request.RequestId,
            Authorized = false,
            RejectionReason = StatRejectionReason.SecurityViolation
        };
    }
    
    // Continue with normal processing...
}
```

## Optional Source Patches

For maximum security, you can patch GC2 source files to add deep network validation hooks. This prevents clients from bypassing network validation by calling GC2 methods directly.

### Patch Menu

Access via: `Tools > Game Creator 2 Networking > Patches`

| Module | What Gets Patched |
|--------|-------------------|
| **Abilities** | Caster.Cast(), Learn(), UnLearn() |
| **Stats** | RuntimeStatData.Base, AddModifier(), RemoveModifier() |
| | RuntimeAttributeData.Value |
| **Inventory** | TBagContent.Use(), Drop() |
| | BagWealth.Set(), Add() |
| **Melee** | MeleeStance.InputCharge/Execute(), PlaySkill(), Hit() |
| | Skill.OnHit() |
| **Shooter** | ShooterStance.PullTrigger(), ReleaseTrigger(), Reload() |
| | WeaponData.Shoot() |

### How to Apply Patches

1. Go to `Tools > Game Creator 2 Networking > Patches > [Module] > Patch`
2. Review the dialog explaining changes
3. Click "Apply Patch"
4. Backups are created automatically

### How to Remove Patches

1. Go to `Tools > Game Creator 2 Networking > Patches > [Module] > Unpatch`
2. Original files are restored from backup

### Check Patch Status

- Single module: `Tools > Game Creator 2 Networking > Patches > [Module] > Check Status`
- All modules: `Tools > Game Creator 2 Networking > Patches > Status Overview...`

## Security Violation Types

| Type | Description |
|------|-------------|
| `RateLimitExceeded` | Client sending too many requests |
| `InvalidSequence` | Out-of-order or duplicate request IDs (replay attack) |
| `ReplayAttack` | Request ID already processed |
| `OutOfBoundsValue` | Value outside acceptable range |
| `StateDesync` | Client state doesn't match server |
| `UnauthorizedAction` | Action not permitted for client |
| `InvalidTarget` | Targeting entity client shouldn't access |

## Violation Actions

Configure in NetworkSecurityConfig:

| Action | Behavior |
|--------|----------|
| `LogOnly` | Log to console only |
| `LogAndWarn` | Log + send warning to client |
| `TempBlock` | Temporarily block client requests |
| `Kick` | Disconnect client from server |
| `Custom` | Call your custom handler |

## Module-Specific Configuration

You can configure different security settings per module:

```csharp
var security = NetworkSecurityManager.Instance;

// Get config for specific module
var statsConfig = security.GetConfigForModule("Stats");
statsConfig.MaxRequestsPerSecond = 30; // Stats need more requests

var combatConfig = security.GetConfigForModule("Melee");
combatConfig.ViolationThreshold = 3; // Combat is stricter
```

Enable module overrides in Inspector by checking "Use Module Overrides".

## State Validators

State validators periodically compare client-reported state with server-authoritative state:

```csharp
// Example: Stats state validation
public struct StatsState : IEquatable<StatsState>
{
    public Dictionary<int, float> StatValues;
    public Dictionary<int, float> AttributeValues;
    public HashSet<int> ActiveStatusEffects;
}
```

Configure validation interval and discrepancy threshold per module.

## Events

Subscribe to security events for custom handling:

```csharp
var security = NetworkSecurityManager.Instance;

security.OnViolationDetected += (violation) =>
{
    Debug.Log($"Violation: {violation.ViolationType} from client {violation.ClientId}");
};

security.OnThresholdExceeded += (clientId, violationType) =>
{
    Debug.LogWarning($"Client {clientId} exceeded threshold for {violationType}");
};

security.OnClientBlocked += (clientId, duration) =>
{
    Debug.LogWarning($"Client {clientId} blocked for {duration} seconds");
};
```

## Best Practices

1. **Always validate on server**: Never trust client data
2. **Use rate limiting**: Prevent request flooding
3. **Enable state validation**: Catch desync early
4. **Apply patches for competitive games**: Maximum security
5. **Test thoroughly**: Security can cause false positives
6. **Log violations**: Track potential exploits
7. **Tune thresholds**: Balance security vs. false positives

## Troubleshooting

### "Rate limited" errors for legitimate players

- Increase `MaxRequestsPerSecond` for the module
- Increase `RateLimitWindow` for burst tolerance

### False positive state desync

- Increase `ValidationInterval`
- Increase `MaxDiscrepancies` threshold
- Check for high network latency

### Patches fail to apply

- GC2 source may have been updated
- Check console for specific error
- Create GitHub issue with GC2 version

## File Locations

| File | Purpose |
|------|---------|
| `Security/NetworkSecurityTypes.cs` | Config, enums, helpers |
| `Security/NetworkSecurityManager.cs` | Central manager singleton |
| `Security/StateValidators.cs` | State validation classes |
| `Security/SecurityIntegration.cs` | Controller integration helpers |
| `Patches/GC2PatcherBase.cs` | Abstract patcher base |
| `Patches/GC2PatchManager.cs` | Menu items & status window |
| `Patches/[Module]Patcher.cs` | Module-specific patchers |
| `Patches/Backups/[Module]/` | Backup files |

## Version Compatibility

- Unity: 2021.3+
- Game Creator 2: 2.15+
- DaimahouGames Abilities: 2.0+

## Support

For issues or questions, please open a GitHub issue with:
- Unity version
- GC2 module versions
- Console error logs
- Steps to reproduce
