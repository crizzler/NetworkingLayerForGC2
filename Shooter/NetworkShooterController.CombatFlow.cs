#if GC2_SHOOTER
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Common.Audio;
using GameCreator.Runtime.Shooter;
using Arawn.NetworkingCore.LagCompensation;
using Arawn.GameCreator2.Networking.Combat;

namespace Arawn.GameCreator2.Networking.Shooter
{
    public partial class NetworkShooterController
    {
        private static readonly RaycastHit[] REMOTE_SHOT_HITS = new RaycastHit[64];
        private static readonly Collider[] REMOTE_IMPACT_COLLIDERS = new Collider[16];
        private const float SERVER_NATIVE_IMPACT_MARKER_LIFETIME_SECONDS = 2f;
        private const float SERVER_NATIVE_IMPACT_POSITION_TOLERANCE = 0.35f;
        private static readonly IComparer<RaycastHit> RAYCAST_HIT_DISTANCE_COMPARER =
            Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance));

        private static readonly FieldInfo PROJECTILE_SHOT_FIELD = typeof(Projectile).GetField(
            "m_Shot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PROJECTILE_IMPACT_EFFECT_FIELD = typeof(Projectile).GetField(
            "m_ImpactEffect",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PROJECTILE_IMPACT_SOUND_FIELD = typeof(Projectile).GetField(
            "m_ImpactSound",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo SHOT_PROJECTILE_PREFAB_FIELD = typeof(TShotProjectile).GetField(
            "m_Prefab",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_PROJECTILE_DELAY_FIELD = typeof(TShotProjectile).GetField(
            "m_Delay",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo SHOT_RAYCAST_LAYER_MASK_FIELD = typeof(ShotRaycast).GetField(
            "m_LayerMask",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_RAYCAST_MAX_DISTANCE_FIELD = typeof(ShotRaycast).GetField(
            "m_MaxDistance",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_RAYCAST_PIERCES_FIELD = typeof(ShotRaycast).GetField(
            "m_Pierces",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_RAYCAST_USE_LINE_FIELD = typeof(ShotRaycast).GetField(
            "m_UseLineRenderer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_RAYCAST_DURATION_FIELD = typeof(ShotRaycast).GetField(
            "m_Duration",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_RAYCAST_MATERIAL_FIELD = typeof(ShotRaycast).GetField(
            "m_LineMaterial",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_RAYCAST_COLOR_FIELD = typeof(ShotRaycast).GetField(
            "m_Color",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_RAYCAST_WIDTH_FIELD = typeof(ShotRaycast).GetField(
            "m_Width",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_RAYCAST_TEXTURE_MODE_FIELD = typeof(ShotRaycast).GetField(
            "m_TextureMode",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_RAYCAST_TEXTURE_ALIGN_FIELD = typeof(ShotRaycast).GetField(
            "m_TextureAlign",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo SHOT_RIGIDBODY_IMPULSE_FIELD = typeof(ShotRigidbody).GetField(
            "m_Impulse",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_RIGIDBODY_FORCE_FIELD = typeof(ShotRigidbody).GetField(
            "m_ImpulseForce",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_RIGIDBODY_MASS_FIELD = typeof(ShotRigidbody).GetField(
            "m_Mass",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_RIGIDBODY_AIR_RESISTANCE_FIELD = typeof(ShotRigidbody).GetField(
            "m_AirResistance",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_RIGIDBODY_WIND_FIELD = typeof(ShotRigidbody).GetField(
            "m_WindInfluence",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_RIGIDBODY_MAX_DISTANCE_FIELD = typeof(ShotRigidbody).GetField(
            "m_MaxDistance",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_RIGIDBODY_TIMEOUT_FIELD = typeof(ShotRigidbody).GetField(
            "m_Timeout",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo SHOT_KINEMATIC_FORCE_FIELD = typeof(ShotKinematic).GetField(
            "m_Force",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_KINEMATIC_GRAVITY_FIELD = typeof(ShotKinematic).GetField(
            "m_Gravity",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_KINEMATIC_AIR_RESISTANCE_FIELD = typeof(ShotKinematic).GetField(
            "m_AirResistance",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_KINEMATIC_WIND_FIELD = typeof(ShotKinematic).GetField(
            "m_WindInfluence",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_KINEMATIC_LAYER_MASK_FIELD = typeof(ShotKinematic).GetField(
            "m_LayerMask",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_KINEMATIC_MAX_DISTANCE_FIELD = typeof(ShotKinematic).GetField(
            "m_MaxDistance",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_KINEMATIC_TIMEOUT_FIELD = typeof(ShotKinematic).GetField(
            "m_Timeout",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo SHOT_TRACER_SPEED_FIELD = typeof(ShotTracer).GetField(
            "m_Speed",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SHOT_TRACER_LAYER_MASK_FIELD = typeof(ShotTracer).GetField(
            "m_LayerMask",
            BindingFlags.Instance | BindingFlags.NonPublic);

        // ════════════════════════════════════════════════════════════════════════════════════════
        // SHOT INTERCEPTION
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Intercept a shot before it's fired.
        /// Call this from your network-aware shot implementation.
        /// </summary>
        /// <param name="muzzlePosition">Muzzle position.</param>
        /// <param name="shotDirection">Shot direction with spread.</param>
        /// <param name="weapon">The weapon being used.</param>
        /// <param name="chargeRatio">Charge ratio for charged weapons.</param>
        /// <param name="projectileIndex">Index for multi-projectile weapons.</param>
        /// <param name="totalProjectiles">Total projectiles in shot.</param>
        /// <returns>True if shot should proceed locally (server or optimistic).</returns>
        public bool InterceptShot(
            Vector3 muzzlePosition,
            Vector3 shotDirection,
            ShooterWeapon weapon,
            float chargeRatio,
            byte projectileIndex,
            byte totalProjectiles)
        {
            LogDiagnostics(
                $"intercept shot entered weapon={(weapon != null ? weapon.name : "null")} " +
                $"hash={(weapon != null ? weapon.Id.Hash : 0)} local={m_IsLocalClient} server={m_IsServer} " +
                $"remote={m_IsRemoteClient} listener={(OnShotRequestSent != null)} muzzle={muzzlePosition} " +
                $"dir={shotDirection} charge={chargeRatio:F2}");

            // Remote clients don't fire - they receive broadcasts
            if (m_IsRemoteClient && !m_IsServer)
            {
                LogDiagnostics(
                    $"intercept shot blocked on remote client weapon={(weapon != null ? weapon.name : "null")}");
                return false;
            }

            if (!CanSendLocalShotRequest(weapon, logReason: true))
            {
                return false;
            }

            // A server-owned actor must continue into GC2 so the authoritative projectile/hits
            // exist. Client-only owners may choose whether to present/fire optimistically.
            bool playOptimistically = m_IsServer || m_OptimisticShotEffects;
            bool requestSent = SendShotRequest(
                muzzlePosition,
                shotDirection,
                weapon,
                chargeRatio,
                projectileIndex,
                totalProjectiles,
                playOptimistically,
                nativeNotificationObserved: false);
            return requestSent && playOptimistically;
        }

        internal void NotifyShotFired(
            Vector3 muzzlePosition,
            Vector3 shotDirection,
            ShooterWeapon weapon,
            float chargeRatio)
        {
            LogDiagnostics(
                $"GC2 shot fired hook received weapon={(weapon != null ? weapon.name : "null")} " +
                $"hash={(weapon != null ? weapon.Id.Hash : 0)} muzzle={muzzlePosition} dir={shotDirection} " +
                $"charge={chargeRatio:F2}");
            if (!CanSendLocalShotRequest(weapon, logReason: true))
            {
                NetworkShooterDebug.LogPhysics(
                    "ShotGate",
                    $"rejected actor={NetworkId} role={DebugRole} " +
                    $"weapon={(weapon != null ? weapon.name : "null")} " +
                    $"hash={(weapon != null ? weapon.Id.Hash : 0)} muzzle={muzzlePosition} " +
                    $"exactDirection={shotDirection}",
                    this);
                return;
            }

            NetworkShooterDebug.LogPhysics(
                "ShotGate",
                $"accepted actor={NetworkId} role={DebugRole} weapon={weapon.name} " +
                $"hash={weapon.Id.Hash} muzzle={muzzlePosition} exactDirection={shotDirection}",
                this);

            ClearProcessedHits();

            if (TryMarkMatchingPendingShotAsNative(
                    muzzlePosition,
                    shotDirection,
                    weapon))
            {
                LogDiagnostics(
                    $"reused pending shot for GC2 native notification weaponHash={(weapon != null ? weapon.Id.Hash : 0)} " +
                    $"muzzle={muzzlePosition}");
                return;
            }

            if (m_IsServer)
            {
                NetworkShotRequest trustedRequest = BuildShotRequest(
                    muzzlePosition,
                    shotDirection,
                    weapon,
                    chargeRatio,
                    0,
                    1);
                NetworkShooterDebug.LogPhysics(
                    "TrustedShot",
                    $"actor={trustedRequest.ActorNetworkId} req={trustedRequest.RequestId} " +
                    $"corr={trustedRequest.CorrelationId} weaponHash={trustedRequest.WeaponHash} " +
                    $"muzzle={trustedRequest.MuzzlePosition} exactDirection={trustedRequest.ShotDirection} " +
                    $"manager={(NetworkShooterManager.Instance != null)} role={DebugRole}",
                    this);
                NetworkShooterManager manager = NetworkShooterManager.Instance;
                if (manager != null && manager.TryServerQueueTrustedShot(trustedRequest))
                {
                    if (m_IsLocalClient)
                    {
                        m_RecentOptimisticShotPresentations.Add(new RecentOptimisticShotPresentation
                        {
                            Request = trustedRequest,
                            ExpiresAt = Time.time + 2f
                        });
                    }

                    return;
                }

                LogDiagnosticsWarning(
                    $"trusted server shot could not be queued req={trustedRequest.RequestId} " +
                    $"weaponHash={trustedRequest.WeaponHash}");
                return;
            }

            SendShotRequest(
                muzzlePosition,
                shotDirection,
                weapon,
                chargeRatio,
                0,
                1,
                optimisticPresentationPlayed: true,
                nativeNotificationObserved: true);
        }

        private bool CanSendLocalShotRequest(ShooterWeapon weapon, bool logReason)
        {
            if ((m_IsRemoteClient && !m_IsServer) || (!m_IsLocalClient && !m_IsServer))
            {
                if (logReason)
                {
                    LogDiagnosticsWarning(
                        $"shot request suppressed: controller is not a local/server shooter " +
                        $"server={m_IsServer} local={m_IsLocalClient} remote={m_IsRemoteClient}");
                }

                return false;
            }

            if (m_IsServer && !m_IsLocalClient) return true;

            if (logReason && m_LogShots)
            {
                LogDiagnostics($"[ShooterAmmoDebug] local shot gate check {BuildAmmoDebug(weapon)}");
            }

            if (weapon == null)
            {
                if (logReason) LogDiagnosticsWarning("shot request suppressed: weapon is null");
                return false;
            }

            if (m_CurrentWeapon == null || m_CurrentWeapon.Id.Hash != weapon.Id.Hash)
            {
                TryAdoptShooterWeapon(weapon, requireEquipped: true);
            }

            if (m_CurrentWeapon == null || m_CurrentWeapon.Id.Hash != weapon.Id.Hash)
            {
                if (logReason)
                {
                    LogDiagnosticsWarning(
                        $"shot request suppressed: current shooter weapon mismatch currentHash={(m_CurrentWeapon != null ? m_CurrentWeapon.Id.Hash : 0)} " +
                        $"requestedHash={weapon.Id.Hash}");
                }

                return false;
            }

            TryRefreshCurrentWeaponData();
            if (m_CurrentWeaponData == null)
            {
                if (logReason) LogDiagnosticsWarning($"shot request suppressed: no WeaponData for {weapon.name}");
                return false;
            }

            if (m_ShooterStance != null && m_ShooterStance.Reloading.WeaponReloading == weapon)
            {
                if (logReason)
                {
                    LogDiagnostics(
                        $"[ShooterAmmoDebug] shot request suppressed: {weapon.name} is reloading " +
                        $"{BuildAmmoDebug(weapon)}");
                }
                return false;
            }

            if (m_CurrentWeaponData.IsJammed)
            {
                if (logReason) LogDiagnostics($"shot request suppressed: {weapon.name} is jammed");
                return false;
            }

            if (!HasLocalAmmoAvailableForShot(weapon))
            {
                if (logReason)
                {
                    LogDiagnostics(
                        $"[ShooterAmmoDebug] shot request suppressed: {weapon.name} has no local magazine ammo " +
                        $"{BuildAmmoDebug(weapon)}");
                }
                return false;
            }

            return true;
        }

        private bool HasLocalAmmoAvailableForShot(ShooterWeapon weapon)
        {
            if (weapon == null || m_Character == null) return false;

            Args args = GetShooterWeaponArgs(weapon);
            if (!weapon.Magazine.GetHasMagazine(args)) return true;

            ShooterMunition munition = m_Character.Combat.RequestMunition(weapon) as ShooterMunition;
            return munition != null && munition.InMagazine > 0;
        }

        private bool SendShotRequest(
            Vector3 muzzlePosition,
            Vector3 shotDirection,
            ShooterWeapon weapon,
            float chargeRatio,
            byte projectileIndex,
            byte totalProjectiles,
            bool optimisticPresentationPlayed,
            bool nativeNotificationObserved)
        {
            EnsureShooterManagerRegistration();

            NetworkShotRequest request = BuildShotRequest(
                muzzlePosition,
                shotDirection,
                weapon,
                chargeRatio,
                projectileIndex,
                totalProjectiles);

            ulong pendingKey = GetPendingKey(request.ActorNetworkId, request.CorrelationId);
            if (pendingKey == 0)
            {
                Debug.LogWarning("[NetworkShooterController] Ignoring shot request with invalid actor/correlation context.");
                return false;
            }

            int maxPendingShots = Mathf.Max(1, m_MaxPendingShots);
            if (m_PendingShots.Count >= maxPendingShots)
            {
                if (m_LogShots)
                {
                    Debug.LogWarning($"[NetworkShooterController] Shot request dropped: pending queue is full ({maxPendingShots}).");
                }

                return false;
            }

            m_PendingShots[pendingKey] = new PendingShotRequest
            {
                Request = request,
                SentTime = Time.time,
                OptimisticPlayed = optimisticPresentationPlayed,
                NativeNotificationObserved = nativeNotificationObserved
            };

            NetworkShooterDebug.LogPhysics(
                "ShotRequest",
                $"actor={request.ActorNetworkId} req={request.RequestId} corr={request.CorrelationId} " +
                $"weaponHash={request.WeaponHash} muzzle={request.MuzzlePosition} " +
                $"exactDirection={request.ShotDirection} projectile={request.ProjectileIndex + 1}/" +
                $"{Mathf.Max(1, request.TotalProjectiles)} listener={(OnShotRequestSent != null)} " +
                $"role={DebugRole}",
                this);

            if (OnShotRequestSent == null)
            {
                LogDiagnosticsWarning(
                    $"shot request has no listeners req={request.RequestId} actor={request.ActorNetworkId} " +
                    $"weaponHash={request.WeaponHash}. NetworkShooterManager/transport bridge is not routing shots.");
            }
            else
            {
                LogDiagnostics(
                    $"shot request queued req={request.RequestId} actor={request.ActorNetworkId} " +
                    $"weaponHash={request.WeaponHash} pending={m_PendingShots.Count}");
            }

            OnShotRequestSent?.Invoke(request);

            if (m_LogShots)
            {
                Debug.Log($"[NetworkShooterController] Shot request sent: {request.RequestId}");
            }

            return true;
        }

        private NetworkShotRequest BuildShotRequest(
            Vector3 muzzlePosition,
            Vector3 shotDirection,
            ShooterWeapon weapon,
            float chargeRatio,
            byte projectileIndex,
            byte totalProjectiles)
        {
            uint shooterNetworkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            int sightHash = m_CurrentWeaponData?.SightId.Hash ?? 0;
            ushort requestId = GetNextRequestId();
            m_LastSentShotRequestId = requestId;

            return new NetworkShotRequest
            {
                RequestId = requestId,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, requestId),
                ClientTimestamp = GetNetworkTime(),
                ShooterNetworkId = shooterNetworkId,
                MuzzlePosition = muzzlePosition,
                ShotDirection = shotDirection,
                WeaponHash = weapon?.Id.Hash ?? 0,
                SightHash = sightHash,
                ChargeRatio = chargeRatio,
                ProjectileIndex = projectileIndex,
                TotalProjectiles = totalProjectiles
            };
        }

        private bool TryMarkMatchingPendingShotAsNative(
            Vector3 muzzlePosition,
            Vector3 shotDirection,
            ShooterWeapon weapon)
        {
            int weaponHash = weapon?.Id.Hash ?? 0;
            float now = Time.time;
            ulong matchedKey = 0;
            float latestSentTime = float.MinValue;

            foreach (KeyValuePair<ulong, PendingShotRequest> pair in m_PendingShots)
            {
                PendingShotRequest pending = pair.Value;
                if (pending.NativeNotificationObserved) continue;
                if (now - pending.SentTime > 0.5f) continue;
                if (pending.Request.WeaponHash != weaponHash) continue;
                if ((pending.Request.MuzzlePosition - muzzlePosition).sqrMagnitude > 0.25f) continue;

                Vector3 pendingDirection = pending.Request.ShotDirection.sqrMagnitude > 0.0001f
                    ? pending.Request.ShotDirection.normalized
                    : Vector3.zero;
                Vector3 nativeDirection = shotDirection.sqrMagnitude > 0.0001f
                    ? shotDirection.normalized
                    : Vector3.zero;
                if (Vector3.Dot(pendingDirection, nativeDirection) < 0.995f) continue;
                if (pending.SentTime <= latestSentTime) continue;

                latestSentTime = pending.SentTime;
                matchedKey = pair.Key;
            }

            if (matchedKey == 0 ||
                !m_PendingShots.TryGetValue(matchedKey, out PendingShotRequest matched))
            {
                return false;
            }

            matched.OptimisticPlayed = true;
            matched.NativeNotificationObserved = true;
            m_PendingShots[matchedKey] = matched;
            m_LastSentShotRequestId = matched.Request.RequestId;
            return true;
        }

        private void EnsureShooterManagerRegistration()
        {
            if (OnShotRequestSent != null && OnHitDetected != null) return;
            if (m_NetworkCharacter == null || m_NetworkCharacter.NetworkId == 0) return;

            NetworkShooterManager manager = NetworkShooterManager.Instance;
            if (manager == null) return;

            manager.RegisterController(m_NetworkCharacter.NetworkId, this);
            LogDiagnostics(
                $"requested late Shooter manager registration netId={m_NetworkCharacter.NetworkId} " +
                $"shotListener={(OnShotRequestSent != null)} hitListener={(OnHitDetected != null)}");
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // HIT INTERCEPTION
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Intercept a hit detected by a shot.
        /// </summary>
        /// <param name="target">The hit target GameObject.</param>
        /// <param name="hitPoint">Hit point in world space.</param>
        /// <param name="hitNormal">Hit normal.</param>
        /// <param name="distance">Distance from muzzle.</param>
        /// <param name="weapon">The weapon used.</param>
        /// <param name="pierceIndex">Pierce index (0 = first hit).</param>
        /// <returns>True if hit should be processed locally.</returns>
        public bool InterceptHit(
            GameObject target,
            Vector3 hitPoint,
            Vector3 hitNormal,
            float distance,
            ShooterWeapon weapon,
            byte pierceIndex)
        {
            return InterceptHit(
                target,
                hitPoint,
                hitNormal,
                distance,
                weapon,
                pierceIndex,
                nativeHitWillContinue: false,
                allowOptimisticPresentation: true);
        }

        private bool InterceptHit(
            GameObject target,
            Vector3 hitPoint,
            Vector3 hitNormal,
            float distance,
            ShooterWeapon weapon,
            byte pierceIndex,
            bool nativeHitWillContinue,
            bool allowOptimisticPresentation)
        {
            if (target == null)
            {
                LogDiagnosticsWarning(
                    $"intercept hit skipped because target is null weapon={(weapon != null ? weapon.name : "null")}");
                return false;
            }

            LogDiagnostics(
                $"intercept hit entered target={target.name} weapon={(weapon != null ? weapon.name : "null")} " +
                $"hash={(weapon != null ? weapon.Id.Hash : 0)} local={m_IsLocalClient} server={m_IsServer} " +
                $"remote={m_IsRemoteClient} listener={(OnHitDetected != null)} point={hitPoint} " +
                $"distance={distance:F2}");

            EnsureShooterManagerRegistration();

            int targetId = target.GetInstanceID();

            Collider hitCollider = target.GetComponent<Collider>();
            Rigidbody hitRigidbody = hitCollider != null && hitCollider.attachedRigidbody != null
                ? hitCollider.attachedRigidbody
                : target.GetComponentInParent<Rigidbody>();
            NetworkShooterDebug.LogPhysics(
                "HitIntercept",
                $"actor={NetworkId} role={DebugRole} target={target.name} targetInstance={targetId} " +
                $"weapon={(weapon != null ? weapon.name : "null")} hash={(weapon != null ? weapon.Id.Hash : 0)} " +
                $"forceEnabled={(weapon != null && weapon.Fire.ForceEnabled)} " +
                $"force={(weapon != null ? weapon.Fire.Force : 0f):F2} point={hitPoint} normal={hitNormal} " +
                $"distance={distance:F2} nativeRequested={nativeHitWillContinue} " +
                $"processedCount={m_ProcessedHits.Count} collider={(hitCollider != null ? hitCollider.name : "null")} " +
                $"{DescribeRigidbodyState(hitRigidbody)}",
                this);

            // Don't process same target twice
            if (m_ProcessedHits.Contains(targetId))
            {
                NetworkShooterDebug.LogPhysics(
                    "HitIntercept",
                    $"suppressed duplicate actor={NetworkId} target={target.name} targetInstance={targetId} " +
                    $"processedCount={m_ProcessedHits.Count}",
                    this);
                return false;
            }
            m_ProcessedHits.Add(targetId);

            // Remote clients don't process hits
            if (m_IsRemoteClient && !m_IsServer)
            {
                LogDiagnostics($"intercept hit blocked on remote client target={target.name}");
                return false;
            }

            bool optimisticPresentationPlayed =
                allowOptimisticPresentation &&
                PlayOptimisticHitPresentation(target, hitPoint, hitNormal, weapon);

            // Get target network ID
            var targetNetworkChar = target.GetComponentInParent<NetworkCharacter>();
            uint targetNetworkId = targetNetworkChar != null ? targetNetworkChar.NetworkId : 0;
            uint shooterNetworkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            ushort requestId = GetNextRequestId();
            ushort sourceShotRequestId = ResolveSourceShotRequestId();

            Character targetCharacter = target.GetComponentInParent<Character>();
            bool isCharacterHit = targetCharacter != null;
            uint impactPropNetworkId = 0;
            if (!isCharacterHit &&
                weapon != null &&
                weapon.Fire.ForceEnabled &&
                NetworkShooterImpactProp.TryGetExisting(target, out NetworkShooterImpactProp impactProp))
            {
                impactPropNetworkId = impactProp.NetworkId;
            }

            var request = new NetworkShooterHitRequest
            {
                RequestId = requestId,
                SourceShotRequestId = sourceShotRequestId,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, requestId),
                ClientTimestamp = GetNetworkTime(),
                ShooterNetworkId = shooterNetworkId,
                TargetNetworkId = targetNetworkId,
                HitPoint = hitPoint,
                HitNormal = hitNormal,
                Distance = distance,
                WeaponHash = weapon?.Id.Hash ?? 0,
                PierceIndex = pierceIndex,
                IsCharacterHit = isCharacterHit,
                ImpactPropNetworkId = impactPropNetworkId
            };

            // A server replica owned by a remote client can observe the same collision as that
            // owner. Suppress this native path and wait for the authenticated owner claim so the
            // hit is neither trusted nor queued twice. Host-local and true server-owned actors do
            // not enter this branch.
            if (m_IsServer &&
                !CanTrustServerObservedHit(
                    out bool clientOwnedServerReplica,
                    out string serverTrustReason))
            {
                NetworkShooterDebug.LogPhysics(
                    "ServerTrust",
                    $"rejected actor={request.ActorNetworkId} req={request.RequestId} corr={request.CorrelationId} " +
                    $"target={request.TargetNetworkId} clientOwnedReplica={clientOwnedServerReplica} " +
                    $"reason={serverTrustReason}",
                    this);
                if (clientOwnedServerReplica)
                {
                    LogDiagnostics(
                        $"suppressed server-replica hit pending owner claim actor={request.ActorNetworkId} " +
                        $"target={request.TargetNetworkId} req={request.RequestId}");
                }
                else
                {
                    LogHitInvariantWarning(
                        $"suppressed server-observed hit because ownership is not ready actor={request.ActorNetworkId} " +
                        $"target={request.TargetNetworkId} req={request.RequestId}. " +
                        "The active transport must register character ownership before Shooter gameplay starts.");
                }

                return false;
            }

            // A dedicated-server actor has no owning client request. The cancellable patched GC2
            // callback queues server-observed collisions directly and then lets the authoritative
            // native hit continue. The legacy Condition-only path remains manager-owned.
            if (m_IsServer && (nativeHitWillContinue || !m_IsLocalClient))
            {
                NetworkShooterManager manager = NetworkShooterManager.Instance;
                if (manager != null &&
                    manager.TryServerQueueTrustedHit(request, nativeHitWillContinue))
                {
                    NetworkShooterDebug.LogPhysics(
                        "TrustedHitQueue",
                        $"accepted actor={request.ActorNetworkId} req={request.RequestId} corr={request.CorrelationId} " +
                        $"sourceShot={request.SourceShotRequestId} target={request.TargetNetworkId} " +
                        $"nativeContinues={nativeHitWillContinue} {DescribeRigidbodyState(hitRigidbody)}",
                        this);
                    if (nativeHitWillContinue)
                    {
                        // The owner will replay the confirmed authored reaction and submit its
                        // animation-driven pose. Open the matching server gate before native GC2
                        // enters the reaction so those authenticated Y/root-motion samples are
                        // accepted instead of being classified as ordinary owner movement.
                        (targetCharacter?.Driver as INetworkServerOwnerMotionAuthority)
                            ?.OpenServerOwnerMotionWindow(
                                OWNER_REACTION_MOTION_WINDOW_SECONDS,
                                request.CorrelationId);

                        m_ServerNativeHitContinuations.Add(targetId);
                        m_ServerNativeHitCorrelations[targetId] = request.CorrelationId;

                        // GC2 is about to instantiate the impact locally. Retain that fact until
                        // the trusted server broadcast returns so a host does not present it twice.
                        m_RecentOptimisticHitPresentations.Add(new RecentOptimisticHitPresentation
                        {
                            Request = request,
                            ExpiresAt = Time.time + 2f
                        });
                    }

                    LogDiagnostics(
                        $"queued trusted server hit req={request.RequestId} target={request.TargetNetworkId} " +
                        $"weaponHash={request.WeaponHash} nativeContinues={nativeHitWillContinue}");
                    return nativeHitWillContinue;
                }

                LogHitInvariantWarning(
                    $"trusted server hit could not be queued req={request.RequestId}; " +
                    $"nativeRequested={nativeHitWillContinue}. GC2 remains suppressed because " +
                    "unconfirmed gameplay cannot be reconciled or broadcast safely.");
                return false;
            }

            if (OnHitDetected == null)
            {
                LogHitInvariantWarning(
                    $"suppressed Shooter hit because no request route is registered actor={request.ActorNetworkId} " +
                    $"target={request.TargetNetworkId} req={request.RequestId}. " +
                    "NetworkShooterManager and the active transport must be ready before Shooter gameplay starts.");
                return false;
            }

            ulong pendingKey = GetPendingKey(request.ActorNetworkId, request.CorrelationId);
            if (pendingKey == 0)
            {
                Debug.LogWarning("[NetworkShooterController] Ignoring hit request with invalid actor/correlation context.");
                return false;
            }

            m_PendingHits[pendingKey] = new PendingHitRequest
            {
                Request = request,
                SentTime = Time.time,
                OptimisticPlayed = optimisticPresentationPlayed
            };

            NetworkShooterDebug.LogPhysics(
                "ClientHitQueue",
                $"actor={request.ActorNetworkId} req={request.RequestId} corr={request.CorrelationId} " +
                $"sourceShot={request.SourceShotRequestId} target={request.TargetNetworkId} " +
                $"weaponHash={request.WeaponHash} point={request.HitPoint} listener={(OnHitDetected != null)} " +
                $"{DescribeRigidbodyState(hitRigidbody)}",
                this);

            LogDiagnostics(
                $"hit request queued req={request.RequestId} actor={request.ActorNetworkId} " +
                $"target={request.TargetNetworkId} sourceShot={request.SourceShotRequestId}");

            OnHitDetected.Invoke(request);

            if (m_LogHits)
            {
                Debug.Log($"[NetworkShooterController] Hit request sent: {target.name} at {hitPoint}");
            }

            // The client may already have played a cosmetic, but native Shooter.OnHit must stay
            // suppressed. The validated broadcast owns all confirmed gameplay and presentation.
            return false;
        }

        /// <summary>
        /// Cancellable hook injected at the beginning of ShooterWeapon.OnHit.
        /// A server queues one trusted confirmation and lets GC2 continue once; clients queue one
        /// request and suppress all native gameplay while retaining presentation-only optimism.
        /// </summary>
        internal bool ValidatePatchedHit(ShotData data)
        {
            if (data.Target == null)
            {
                LogHitInvariantWarning(
                    $"suppressed GC2 Shooter hit because the patched callback received no target " +
                    $"weapon={(data.Weapon != null ? data.Weapon.name : "null")}. Native side effects cannot be authorized.");
                return false;
            }

            LogDiagnostics(
                $"GC2 hit hook received target={data.Target.name} weapon={(data.Weapon != null ? data.Weapon.name : "null")} " +
                $"hash={(data.Weapon != null ? data.Weapon.Id.Hash : 0)} point={data.HitPoint} distance={data.Distance:F2} " +
                $"sourceShot={m_LastSentShotRequestId}");

            Vector3 hitNormal = data.ShootDirection.sqrMagnitude > 0.0001f
                ? -data.ShootDirection.normalized
                : Vector3.up;

            bool continueNative = InterceptHit(
                data.Target,
                data.HitPoint,
                hitNormal,
                data.Distance,
                data.Weapon,
                (byte)Mathf.Clamp(data.Pierces, 0, byte.MaxValue),
                nativeHitWillContinue: m_IsServer,
                allowOptimisticPresentation: true
            );

            if (continueNative && m_IsServer)
            {
                ApplySupplementalPhysicalImpact(data);
            }

            return continueNative;
        }

        /// <summary>
        /// Compatibility entry point for the previous notification-only patch. New installations
        /// use <see cref="ValidatePatchedHit"/> so clients can cancel native side effects.
        /// </summary>
        [Obsolete("Use the cancellable ShooterWeapon.NetworkOnHitValidator patch hook instead.")]
        internal void NotifyHitDetected(ShotData data)
        {
            _ = ValidatePatchedHit(data);
        }

        internal bool TryConsumeServerNativeHitContinuation(GameObject target)
        {
            if (!m_IsServer || target == null) return false;
            return m_ServerNativeHitContinuations.Remove(target.GetInstanceID());
        }

        internal void NotifyPatchedHitResolved(
            ShotData data,
            BlockType blockType,
            ReactionOutput reactionOutput,
            float reactionPower)
        {
            if (!m_IsServer || data.Target == null) return;

            int targetId = data.Target.GetInstanceID();
            // A legacy Condition may already have consumed this token. Removing it here also
            // guarantees condition-free weapons cannot leak it into a later hit sequence.
            m_ServerNativeHitContinuations.Remove(targetId);
            if (!m_ServerNativeHitCorrelations.TryGetValue(targetId, out uint correlationId))
            {
                LogHitInvariantWarning(
                    $"native Shooter hit outcome had no matching continuation token target={data.Target.name} " +
                    $"block={blockType}. Reapply the current Shooter source patch.");
                return;
            }

            m_ServerNativeHitCorrelations.Remove(targetId);
            NetworkBlockResult blockResult = NetworkShooterManager.MapBlockType(blockType);

            Character targetCharacter = data.Target.Get<Character>();
            if (targetCharacter != null &&
                (blockResult == NetworkBlockResult.None ||
                 blockResult == NetworkBlockResult.BlockBroken))
            {
                (targetCharacter.Driver as INetworkServerOwnerMotionAuthority)
                    ?.OpenServerOwnerMotionWindow(
                        NetworkShooterReactionContext.CalculateOwnerMotionWindow(reactionOutput),
                        correlationId);
            }

            NetworkShooterManager manager = NetworkShooterManager.Instance;
            if (float.IsNaN(reactionPower) || float.IsInfinity(reactionPower))
            {
                reactionPower = float.NaN;
            }

            bool nativeOutcomeRecorded = manager != null &&
                manager.TryServerRecordNativeHitOutcome(
                    NetworkId,
                    correlationId,
                    blockResult,
                    reactionPower);
            if (!nativeOutcomeRecorded)
            {
                LogHitInvariantWarning(
                    $"native Shooter hit outcome could not be matched to its queued request actor={NetworkId} " +
                    $"corr={correlationId} block={blockResult}.");
                return;
            }

            // NetworkOnHitResolved runs after GC2's direct Rigidbody impulse and after the
            // supplemental parent-Rigidbody path. Record only validated native continuations;
            // the host consumes this marker when its confirmed broadcast loops back.
            RecordServerNativeEnvironmentImpactMarker(data);
        }

        private bool CanTrustServerObservedHit(
            out bool clientOwnedServerReplica,
            out string reason)
        {
            clientOwnedServerReplica = false;
            reason = "trusted";
            if (!m_IsServer)
            {
                reason = "controller-not-server";
                return false;
            }
            if (NetworkId == 0)
            {
                reason = "network-id-zero";
                return false;
            }

            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            if (bridge == null)
            {
                reason = "active-transport-missing";
                return false;
            }
            if (!bridge.IsServer)
            {
                reason = $"active-transport-not-server host={bridge.IsHost}";
                return false;
            }

            // A locally-owned host actor is already positively identified by the transport-backed
            // NetworkCharacter role. The base character registry can trail spawn/scene activation
            // briefly; requiring that secondary lookup suppresses the native GC2 hit (including
            // Rigidbody force) with no client request fallback.
            if (m_IsLocalClient)
            {
                bool trustedHost =
                    bridge.IsHost &&
                    m_NetworkCharacter != null &&
                    m_NetworkCharacter.IsHostInstance &&
                    m_NetworkCharacter.IsOwnerInstance;
                reason = trustedHost
                    ? "host-owner"
                    : $"local-owner-role-invalid bridgeHost={bridge.IsHost} " +
                      $"characterHost={(m_NetworkCharacter != null && m_NetworkCharacter.IsHostInstance)} " +
                      $"characterOwner={(m_NetworkCharacter != null && m_NetworkCharacter.IsOwnerInstance)}";
                return trustedHost;
            }

            Character registeredCharacter = bridge.ResolveCharacter(NetworkId);
            if (registeredCharacter == null)
            {
                reason = "character-not-registered-in-base-transport";
                return false;
            }
            if (registeredCharacter != m_Character)
            {
                reason = $"registered-character-mismatch registered={registeredCharacter.name}";
                return false;
            }

            if (bridge.TryGetCharacterOwner(NetworkId, out uint ownerClientId) &&
                NetworkTransportBridge.IsValidClientId(ownerClientId))
            {
                clientOwnedServerReplica = true;
                reason = $"remote-client-owned owner={ownerClientId}";
                return false;
            }

            reason = "server-owned";
            return true;
        }

        private void LogHitInvariantWarning(string message)
        {
            float now = Time.unscaledTime;
            if (now < m_NextHitInvariantWarningTime) return;

            m_NextHitInvariantWarningTime = now + 2f;
            Debug.LogWarning($"[NetworkShooterController] {message}", this);
        }

        private bool PlayOptimisticHitPresentation(
            GameObject target,
            Vector3 hitPoint,
            Vector3 hitNormal,
            ShooterWeapon weapon)
        {
            if (!m_IsLocalClient || m_IsServer || !m_OptimisticHitEffects) return false;

            if (weapon != null &&
                PlayConfiguredImpactEffect(weapon, target, hitPoint, hitNormal))
            {
                return true;
            }

            if (!m_UseGeneratedFallbackPresentation) return false;
            DrawRemoteImpact(hitPoint, hitNormal);
            return true;
        }

        private void ApplySupplementalPhysicalImpact(ShotData data)
        {
            if (data.Target == null || data.Weapon == null) return;
            if (!data.Weapon.Fire.ForceEnabled) return;
            if (data.Target.GetComponentInParent<Character>() != null) return;
            if (data.Target.GetComponentInParent<NetworkShooterImpactProp>() != null) return;
            if (data.Target.Get<Rigidbody>() != null) return;

            Rigidbody rigidbody = null;
            Collider collider = data.Target.GetComponent<Collider>();
            if (collider != null && collider.attachedRigidbody != null)
            {
                rigidbody = collider.attachedRigidbody;
            }

            rigidbody ??= data.Target.GetComponentInParent<Rigidbody>();
            if (rigidbody == null) return;

            Vector3 direction = data.ShootDirection.sqrMagnitude > 0.0001f
                ? data.ShootDirection.normalized
                : m_Character != null ? m_Character.transform.forward : Vector3.forward;

            rigidbody.AddForceAtPosition(direction * data.Weapon.Fire.Force, data.HitPoint, ForceMode.Impulse);
            NetworkShooterDebug.LogPhysics(
                "NativeSupplementalImpulse",
                $"actor={NetworkId} weapon={data.Weapon.name} hash={data.Weapon.Id.Hash} " +
                $"force={data.Weapon.Fire.Force:F2} point={data.HitPoint} direction={direction} " +
                $"{DescribeRigidbodyState(rigidbody)}",
                this);
            LogDiagnostics(
                $"applied supplemental shooter impact rigidbody={rigidbody.name} " +
                $"force={data.Weapon.Fire.Force:F2} point={data.HitPoint}");
        }

        /// <summary>
        /// Clear the processed hits set. Call when starting a new shot sequence.
        /// </summary>
        public void ClearProcessedHits()
        {
            m_ProcessedHits.Clear();
            m_ServerNativeHitContinuations.Clear();
            m_ServerNativeHitCorrelations.Clear();
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER VALIDATION
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// [Server] Process a shot request from a client.
        /// </summary>
        public NetworkShotResponse ProcessShotRequest(NetworkShotRequest request, uint clientNetworkId)
        {
            NetworkShooterDebug.LogPhysics(
                "ServerShotValidation",
                $"begin actor={request.ActorNetworkId} req={request.RequestId} corr={request.CorrelationId} " +
                $"client={clientNetworkId} weaponHash={request.WeaponHash} muzzle={request.MuzzlePosition} " +
                $"exactDirection={request.ShotDirection} role={DebugRole} " +
                $"currentWeapon={(m_CurrentWeapon != null ? m_CurrentWeapon.name : "null")} " +
                $"currentHash={(m_CurrentWeapon != null ? m_CurrentWeapon.Id.Hash : 0)}",
                this);
            LogDiagnostics(
                $"server processing shot req={request.RequestId} corr={request.CorrelationId} client={clientNetworkId} " +
                $"actor={request.ActorNetworkId} shooter={request.ShooterNetworkId} weaponHash={request.WeaponHash} " +
                $"currentWeapon={(m_CurrentWeapon != null ? m_CurrentWeapon.name : "null")} " +
                $"currentHash={(m_CurrentWeapon != null ? m_CurrentWeapon.Id.Hash : 0)} muzzle={request.MuzzlePosition} " +
                $"dir={request.ShotDirection}");

            if (!m_IsServer)
            {
                NetworkShooterDebug.LogPhysics(
                    "ServerShotValidation",
                    $"rejected actor={request.ActorNetworkId} req={request.RequestId} reason=controller-not-server",
                    this);
                LogDiagnosticsWarning($"shot rejected req={request.RequestId}: controller is not server");
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.CheatSuspected
                };
            }

            // Validate weapon is equipped
            if (m_CurrentWeapon == null || m_CurrentWeapon.Id.Hash != request.WeaponHash)
            {
                NetworkShooterDebug.LogPhysics(
                    "ServerShotValidation",
                    $"rejected actor={request.ActorNetworkId} req={request.RequestId} reason=weapon-not-equipped " +
                    $"currentHash={(m_CurrentWeapon != null ? m_CurrentWeapon.Id.Hash : 0)} " +
                    $"requestedHash={request.WeaponHash}",
                    this);
                LogDiagnosticsWarning(
                    $"shot rejected req={request.RequestId}: weapon not equipped " +
                    $"current={(m_CurrentWeapon != null ? m_CurrentWeapon.name : "null")} " +
                    $"currentHash={(m_CurrentWeapon != null ? m_CurrentWeapon.Id.Hash : 0)} requestedHash={request.WeaponHash}");
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.WeaponNotEquipped
                };
            }

            // Validate ammo
            var munition = m_Character.Combat.RequestMunition(m_CurrentWeapon) as ShooterMunition;
            int ammoBeforeValidation = munition != null ? munition.InMagazine : -1;
            if (!HasAmmoAvailableForShot(m_CurrentWeapon, munition))
            {
                NetworkShooterDebug.LogPhysics(
                    "ServerShotValidation",
                    $"rejected actor={request.ActorNetworkId} req={request.RequestId} reason=no-ammo " +
                    $"munition={(munition != null)} inMagazine={(munition != null ? munition.InMagazine : -1)}",
                    this);
                LogDiagnosticsWarning(
                    $"[ShooterAmmoDebug] shot rejected req={request.RequestId}: no ammo " +
                    $"munition={(munition != null)} inMagazine={(munition != null ? munition.InMagazine : -1)} " +
                    $"{BuildAmmoDebug(m_CurrentWeapon)}");
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.NoAmmo
                };
            }

            // Validate not jammed
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.IsJammed)
            {
                NetworkShooterDebug.LogPhysics(
                    "ServerShotValidation",
                    $"rejected actor={request.ActorNetworkId} req={request.RequestId} reason=weapon-jammed",
                    this);
                LogDiagnosticsWarning($"shot rejected req={request.RequestId}: weapon jammed");
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.WeaponJammed
                };
            }

            // Validate muzzle position sanity against server character position
            Vector3 characterPosition = m_Character != null ? m_Character.transform.position : request.MuzzlePosition;
            if ((request.MuzzlePosition - characterPosition).sqrMagnitude > 36f) // 6m tolerance
            {
                NetworkShooterDebug.LogPhysics(
                    "ServerShotValidation",
                    $"rejected actor={request.ActorNetworkId} req={request.RequestId} reason=invalid-muzzle " +
                    $"character={characterPosition} muzzle={request.MuzzlePosition} " +
                    $"distance={Vector3.Distance(request.MuzzlePosition, characterPosition):F2}",
                    this);
                LogDiagnosticsWarning(
                    $"shot rejected req={request.RequestId}: invalid muzzle position " +
                    $"character={characterPosition} muzzle={request.MuzzlePosition} " +
                    $"distance={Vector3.Distance(request.MuzzlePosition, characterPosition):F2}");
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.InvalidPosition
                };
            }

            // Validate direction vector
            float directionSqMag = request.ShotDirection.sqrMagnitude;
            if (directionSqMag < 0.01f || directionSqMag > 1.5f)
            {
                NetworkShooterDebug.LogPhysics(
                    "ServerShotValidation",
                    $"rejected actor={request.ActorNetworkId} req={request.RequestId} reason=invalid-direction " +
                    $"sqrMagnitude={directionSqMag:F4} direction={request.ShotDirection}",
                    this);
                LogDiagnosticsWarning(
                    $"shot rejected req={request.RequestId}: invalid direction sqrMag={directionSqMag:F4} dir={request.ShotDirection}");
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.InvalidDirection
                };
            }

            // Server-side fire-rate validation derived from weapon fire-rate.
            float now = Time.time;
            float minShotInterval = GetServerMinShotInterval();
            if (minShotInterval > 0f && now - m_LastServerValidatedShotTime < minShotInterval)
            {
                NetworkShooterDebug.LogPhysics(
                    "ServerShotValidation",
                    $"rejected actor={request.ActorNetworkId} req={request.RequestId} reason=rate-limit " +
                    $"elapsed={(now - m_LastServerValidatedShotTime):F3} min={minShotInterval:F3}",
                    this);
                LogDiagnosticsWarning(
                    $"shot rejected req={request.RequestId}: rate limit elapsed={(now - m_LastServerValidatedShotTime):F3} " +
                    $"min={minShotInterval:F3}");
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.RateLimitExceeded
                };
            }

            // Ensure lag-comp validator records this shot so subsequent hit requests can be
            // cryptographically/structurally bound to an authoritative validated shot.
            if (m_Validator == null)
            {
                m_Validator = new ShooterLagCompensationValidator(m_ValidationConfig);
            }

            ShotValidationResult shotValidation = m_Validator.ValidateShot(
                request,
                m_Character,
                m_CurrentWeapon
            );
            if (!shotValidation.IsValid)
            {
                NetworkShooterDebug.LogPhysics(
                    "ServerShotValidation",
                    $"rejected actor={request.ActorNetworkId} req={request.RequestId} corr={request.CorrelationId} " +
                    $"reason={shotValidation.RejectionReason} details={shotValidation.RejectionDetails ?? "none"}",
                    this);
                if (!LagCompensationManager.IsInitialized)
                {
                    LogHitInvariantWarning(
                        $"Authoritative shot {request.RequestId} cannot be validated because " +
                        "lag compensation is not initialized. Ensure the server bootstrap is active.");
                }
                LogDiagnosticsWarning(
                    $"shot rejected req={request.RequestId}: validator reason={shotValidation.RejectionReason} " +
                    $"details={shotValidation.RejectionDetails}");
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = shotValidation.RejectionReason
                };
            }

            // Store validated shot for hit validation
            m_ValidatedShots[request.RequestId] = new ValidatedShot
            {
                Request = request,
                ValidatedTime = Time.time,
                HitsProcessed = 0
            };
            m_LastServerValidatedShotTime = now;

            ushort ammoRemaining = ConsumeServerAmmoForShot(m_CurrentWeapon, munition);

            NetworkShooterDebug.LogPhysics(
                "ServerShotValidation",
                $"accepted actor={request.ActorNetworkId} req={request.RequestId} corr={request.CorrelationId} " +
                $"weaponHash={request.WeaponHash} ammoBefore={ammoBeforeValidation} " +
                $"ammoRemaining={ammoRemaining} validatedShots={m_ValidatedShots.Count}",
                this);

            LogDiagnostics(
                $"[ShooterAmmoDebug] shot validated req={request.RequestId} corr={request.CorrelationId} " +
                $"ammoBefore={ammoBeforeValidation} ammoRemaining={ammoRemaining} " +
                $"validatedShots={m_ValidatedShots.Count} {BuildAmmoDebug(m_CurrentWeapon)}");

            return new NetworkShotResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = ShotRejectionReason.None,
                AmmoRemaining = ammoRemaining
            };
        }

        private bool HasAmmoAvailableForShot(ShooterWeapon weapon, ShooterMunition munition)
        {
            if (weapon == null) return false;

            Args args = GetShooterWeaponArgs(weapon);
            if (!weapon.Magazine.GetHasMagazine(args)) return true;

            if (munition == null) return false;
            if (munition.InMagazine > 0) return true;

            return false;
        }

        private ushort ConsumeServerAmmoForShot(ShooterWeapon weapon, ShooterMunition munition)
        {
            if (!m_IsServer || weapon == null || munition == null) return 0;

            Args args = GetShooterWeaponArgs(weapon);
            if (!weapon.Magazine.GetHasMagazine(args)) return 0;
            if (munition.InMagazine <= 0) return 0;

            int ammoRemaining = Mathf.Max(0, munition.InMagazine - 1);
            if (!m_IsLocalClient)
            {
                munition.InMagazine = ammoRemaining;
            }

            return (ushort)Mathf.Clamp(ammoRemaining, 0, ushort.MaxValue);
        }

        private float GetServerMinShotInterval()
        {
            if (m_CurrentWeapon == null || m_CurrentWeaponData == null)
            {
                return 0.02f;
            }

            float fireRate = m_CurrentWeapon.Fire.FireRate(m_CurrentWeaponData.WeaponArgs);
            if (float.IsNaN(fireRate) || float.IsInfinity(fireRate) || fireRate <= float.Epsilon)
            {
                return 0.02f;
            }

            // Allow slight tolerance for transport jitter while still blocking impossible cadence.
            float interval = 1f / fireRate;
            return Mathf.Clamp(interval * 0.9f, 0.01f, 2f);
        }

        /// <summary>
        /// [Server] Process a hit request from a client.
        /// </summary>
        public NetworkShooterHitResponse ProcessHitRequest(NetworkShooterHitRequest request, uint clientNetworkId)
        {
            LogDiagnostics(
                $"server processing hit req={request.RequestId} sourceShot={request.SourceShotRequestId} " +
                $"corr={request.CorrelationId} client={clientNetworkId} actor={request.ActorNetworkId} " +
                $"target={request.TargetNetworkId} weaponHash={request.WeaponHash} characterHit={request.IsCharacterHit} " +
                $"point={request.HitPoint} distance={request.Distance:F2}");

            if (!m_IsServer)
            {
                LogDiagnosticsWarning($"hit rejected req={request.RequestId}: controller is not server");
                return new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.CheatSuspected
                };
            }

            if (request.SourceShotRequestId == 0 ||
                !m_ValidatedShots.TryGetValue(request.SourceShotRequestId, out var validatedShot))
            {
                LogDiagnosticsWarning(
                    $"hit rejected req={request.RequestId}: source shot not validated " +
                    $"sourceShot={request.SourceShotRequestId} validatedShots={m_ValidatedShots.Count}");
                return new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.ShotNotValidated
                };
            }

            if (validatedShot.Request.ShooterNetworkId != request.ShooterNetworkId ||
                validatedShot.Request.WeaponHash != request.WeaponHash)
            {
                LogDiagnosticsWarning(
                    $"hit rejected req={request.RequestId}: source shot binding mismatch " +
                    $"shotShooter={validatedShot.Request.ShooterNetworkId} hitShooter={request.ShooterNetworkId} " +
                    $"shotWeapon={validatedShot.Request.WeaponHash} hitWeapon={request.WeaponHash}");
                return new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.ShotNotValidated
                };
            }

            // For character hits, validate target
            if (request.IsCharacterHit && request.TargetNetworkId != 0)
            {
                var targetNetworkChar = NetworkShooterManager.Instance?.GetCharacterByNetworkId(request.TargetNetworkId);
                if (targetNetworkChar == null)
                {
                    LogDiagnosticsWarning(
                        $"hit rejected req={request.RequestId}: target NetworkCharacter not found target={request.TargetNetworkId}");
                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = HitRejectionReason.TargetNotFound
                    };
                }

                var targetCharacter = targetNetworkChar.GetComponent<Character>();
                if (targetCharacter == null)
                {
                    LogDiagnosticsWarning(
                        $"hit rejected req={request.RequestId}: target Character not found target={request.TargetNetworkId}");
                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = HitRejectionReason.TargetNotFound
                    };
                }

                if (targetCharacter.IsDead)
                {
                    LogDiagnosticsWarning($"hit rejected req={request.RequestId}: target dead target={targetCharacter.name}");
                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = HitRejectionReason.TargetDead
                    };
                }

                // Check invincibility
                if (targetCharacter.Combat.Invincibility.IsInvincible)
                {
                    LogDiagnosticsWarning($"hit rejected req={request.RequestId}: target invincible target={targetCharacter.name}");
                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = HitRejectionReason.TargetInvincible
                    };
                }

                // Check dodge
                if (targetCharacter.Dash != null && targetCharacter.Dash.IsDodge)
                {
                    LogDiagnosticsWarning($"hit rejected req={request.RequestId}: target dodged target={targetCharacter.name}");
                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = HitRejectionReason.TargetDodged
                    };
                }

                // ═══════════════════════════════════════════════════════════════════════════════════
                // LAG COMPENSATION VALIDATION
                // ═══════════════════════════════════════════════════════════════════════════════════

                // Ensure validator is initialized
                if (m_Validator == null)
                {
                    m_Validator = new ShooterLagCompensationValidator(m_ValidationConfig);
                }

                // Perform lag-compensated validation
                var validationResult = m_Validator.ValidateShotHit(
                    request,
                    m_Character,
                    m_CurrentWeapon
                );

                if (!validationResult.IsValid)
                {
                    if (validationResult.RejectionReason == CombatValidationRejectionReason.InternalError ||
                        validationResult.RejectionReason == CombatValidationRejectionReason.TargetNotRegistered ||
                        validationResult.RejectionReason == CombatValidationRejectionReason.NoHistoryAvailable)
                    {
                        bool managerReady = LagCompensationManager.IsInitialized;
                        LagCompensationManager lagManager = managerReady
                            ? LagCompensationManager.Instance
                            : null;
                        bool targetRegistered = managerReady &&
                                                lagManager.IsRegistered(request.TargetNetworkId);
                        LogHitInvariantWarning(
                            $"Authoritative shooter hit {request.RequestId} cannot be validated: " +
                            $"reason={validationResult.RejectionReason}, managerReady={managerReady}, " +
                            $"targetRegistered={targetRegistered}, target={request.TargetNetworkId}, " +
                            $"details={validationResult.RejectionDetails ?? "none"}.");
                    }
                    LogDiagnosticsWarning(
                        $"hit rejected req={request.RequestId}: validator reason={validationResult.RejectionReason}");
                    if (m_LogHits)
                    {
                        Debug.Log($"[NetworkShooterController] Hit rejected: {validationResult}");
                    }

                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = MapValidationRejection(validationResult.RejectionReason),
                        Damage = 0f,
                        BlockResult = NetworkBlockResult.None
                    };
                }

                // Hit validated with lag compensation!
                LogDiagnostics(
                    $"hit validated req={request.RequestId} target={request.TargetNetworkId} damage={validationResult.FinalDamage:F2}");
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkShooterController] Hit validated: {validationResult}");
                }

                MarkValidatedShotHitProcessed(request.SourceShotRequestId);
                return new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = true,
                    RejectionReason = HitRejectionReason.None,
                    Damage = validationResult.FinalDamage,
                    BlockResult = NetworkBlockResult.None
                };
            }

            // Environment hit - still validate against the authoritative shot trajectory.
            if (m_Validator == null)
            {
                m_Validator = new ShooterLagCompensationValidator(m_ValidationConfig);
            }

            var environmentValidation = m_Validator.ValidateShotHit(
                request,
                m_Character,
                m_CurrentWeapon
            );

            if (!environmentValidation.IsValid)
            {
                NetworkShooterDebug.LogPhysics(
                    "EnvironmentValidation",
                    $"rejected actor={request.ActorNetworkId} req={request.RequestId} corr={request.CorrelationId} " +
                    $"sourceShot={request.SourceShotRequestId} weaponHash={request.WeaponHash} " +
                    $"point={request.HitPoint} distance={request.Distance:F2} " +
                    $"reason={environmentValidation.RejectionReason} " +
                    $"details={environmentValidation.RejectionDetails ?? "none"}",
                    this);
                LogDiagnosticsWarning(
                    $"environment hit rejected req={request.RequestId}: validator reason={environmentValidation.RejectionReason}");
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkShooterController] Environment hit rejected: {environmentValidation}");
                }

                return new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = MapValidationRejection(environmentValidation.RejectionReason),
                    Damage = 0f,
                    BlockResult = NetworkBlockResult.None
                };
            }

            MarkValidatedShotHitProcessed(request.SourceShotRequestId);
            NetworkShooterDebug.LogPhysics(
                "EnvironmentValidation",
                $"accepted actor={request.ActorNetworkId} req={request.RequestId} corr={request.CorrelationId} " +
                $"sourceShot={request.SourceShotRequestId} weaponHash={request.WeaponHash} " +
                $"point={request.HitPoint} distance={request.Distance:F2}",
                this);
            LogDiagnostics($"environment hit validated req={request.RequestId} point={request.HitPoint}");
            return new NetworkShooterHitResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = HitRejectionReason.None,
                Damage = 0f,
                BlockResult = NetworkBlockResult.None
            };
        }

        private ushort ResolveSourceShotRequestId()
        {
            if (m_LastSentShotRequestId != 0)
            {
                return m_LastSentShotRequestId;
            }

            float latestSentTime = float.MinValue;
            ushort latestRequestId = 0;
            foreach (var pending in m_PendingShots.Values)
            {
                if (pending.SentTime > latestSentTime)
                {
                    latestSentTime = pending.SentTime;
                    latestRequestId = pending.Request.RequestId;
                }
            }

            return latestRequestId;
        }

        private void MarkValidatedShotHitProcessed(ushort sourceShotRequestId)
        {
            if (sourceShotRequestId == 0) return;
            if (!m_ValidatedShots.TryGetValue(sourceShotRequestId, out var validatedShot)) return;

            validatedShot.HitsProcessed++;
            m_ValidatedShots[sourceShotRequestId] = validatedShot;
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // RECEIVING RESPONSES & BROADCASTS
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// [Client] Called when server responds to a shot request.
        /// </summary>
        public void ReceiveShotResponse(NetworkShotResponse response)
        {
            if (!TryTakePending(
                    m_PendingShots,
                    response.ActorNetworkId,
                    response.CorrelationId,
                    out PendingShotRequest pending))
            {
                LogDiagnosticsWarning(
                    $"shot response dropped stale/unknown req={response.RequestId} corr={response.CorrelationId} " +
                    $"actor={response.ActorNetworkId} validated={response.Validated} reason={response.RejectionReason}");
                if (m_LogShots)
                {
                    Debug.LogWarning($"[NetworkShooterController] Shot response dropped (stale/unknown): req={response.RequestId}, corr={response.CorrelationId}");
                }
                return;
            }

            LogDiagnostics(
                $"shot response received req={response.RequestId} corr={response.CorrelationId} " +
                $"validated={response.Validated} reason={response.RejectionReason} ammo={response.AmmoRemaining}");

            ApplyShotResponseAmmo(response);

            if (response.Validated && pending.OptimisticPlayed)
            {
                m_RecentOptimisticShotPresentations.Add(new RecentOptimisticShotPresentation
                {
                    Request = pending.Request,
                    ExpiresAt = Time.time + 2f
                });
            }

            if (!response.Validated)
            {
                if (m_LogShots)
                {
                    Debug.Log($"[NetworkShooterController] Shot rejected: {response.RejectionReason}");
                }
                // Optimistic shot effects (muzzle flash, tracer, sound) are fire-and-forget VFX
                // that have already completed by the time the server response arrives.
                // Rolling them back would be visually jarring and offer no gameplay benefit.
            }
        }

        private void ApplyShotResponseAmmo(NetworkShotResponse response)
        {
            if (m_IsServer && m_IsLocalClient)
            {
                LogDiagnostics(
                    $"[ShooterAmmoDebug] shot response ammo ignored on host-local req={response.RequestId} " +
                    $"ammo={response.AmmoRemaining} {BuildAmmoDebug()}");
                return;
            }
            if (m_CurrentWeapon == null || m_Character == null) return;
            if (!response.Validated && response.RejectionReason != ShotRejectionReason.NoAmmo) return;

            Args args = GetShooterWeaponArgs(m_CurrentWeapon);
            if (!m_CurrentWeapon.Magazine.GetHasMagazine(args)) return;

            ShooterMunition munition = m_Character.Combat.RequestMunition(m_CurrentWeapon) as ShooterMunition;
            if (munition == null) return;

            int previousAmmo = munition.InMagazine;
            munition.InMagazine = response.AmmoRemaining;
            LogDiagnostics(
                $"[ShooterAmmoDebug] applied shot response ammo req={response.RequestId} " +
                $"validated={response.Validated} reason={response.RejectionReason} previous={previousAmmo} " +
                $"new={munition.InMagazine} {BuildAmmoDebug(m_CurrentWeapon)}");
        }

        /// <summary>
        /// [Client] Called when server responds to a hit request.
        /// </summary>
        public void ReceiveHitResponse(NetworkShooterHitResponse response)
        {
            if (!TryTakePending(
                    m_PendingHits,
                    response.ActorNetworkId,
                    response.CorrelationId,
                    out PendingHitRequest pending))
            {
                LogDiagnosticsWarning(
                    $"hit response dropped stale/unknown req={response.RequestId} corr={response.CorrelationId} " +
                    $"actor={response.ActorNetworkId} validated={response.Validated} reason={response.RejectionReason}");
                if (m_LogHits)
                {
                    Debug.LogWarning($"[NetworkShooterController] Hit response dropped (stale/unknown): req={response.RequestId}, corr={response.CorrelationId}");
                }
                return;
            }

            LogDiagnostics(
                $"hit response received req={response.RequestId} corr={response.CorrelationId} " +
                $"validated={response.Validated} reason={response.RejectionReason} damage={response.Damage:F2}");

            if (response.Validated && pending.OptimisticPlayed)
            {
                m_RecentOptimisticHitPresentations.Add(new RecentOptimisticHitPresentation
                {
                    Request = pending.Request,
                    ExpiresAt = Time.time + 2f
                });
            }
            else if (!response.Validated)
            {
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkShooterController] Hit rejected: {response.RejectionReason}");
                }
            }
        }

        /// <summary>
        /// [All] Called when server broadcasts a confirmed shot.
        /// </summary>
        public void ReceiveShotBroadcast(NetworkShotBroadcast broadcast)
        {
            bool isLocalShooter = m_IsLocalClient && broadcast.ShooterNetworkId == NetworkId;
            bool matchedOptimisticPresentation =
                isLocalShooter && HasMatchingOptimisticShotPresentation(broadcast);
            LogDiagnostics(
                $"shot broadcast received shooter={broadcast.ShooterNetworkId} weaponHash={broadcast.WeaponHash} " +
                $"willPlayEffects={!matchedOptimisticPresentation} muzzle={broadcast.MuzzlePosition} hitPoint={broadcast.HitPoint}");
            OnShotConfirmed?.Invoke(broadcast);

            // Suppress confirmation only when this exact local shot actually entered the native
            // optimistic presentation path. A local owner with optimism disabled (or no matching
            // request) still needs the confirmed tracer/muzzle presentation.
            if (!matchedOptimisticPresentation)
            {
                PlayShotEffects(broadcast);
            }
        }

        private bool HasMatchingOptimisticShotPresentation(NetworkShotBroadcast broadcast)
        {
            ulong matchedPendingKey = 0;
            foreach (KeyValuePair<ulong, PendingShotRequest> pair in m_PendingShots)
            {
                PendingShotRequest pending = pair.Value;
                if (!pending.OptimisticPlayed) continue;
                if (!MatchesConfirmedShotBroadcast(pending.Request, broadcast)) continue;
                matchedPendingKey = pair.Key;
                break;
            }

            if (matchedPendingKey != 0 &&
                m_PendingShots.TryGetValue(matchedPendingKey, out PendingShotRequest matchedPending))
            {
                matchedPending.OptimisticPlayed = false;
                m_PendingShots[matchedPendingKey] = matchedPending;
                return true;
            }

            float now = Time.time;
            for (int i = m_RecentOptimisticShotPresentations.Count - 1; i >= 0; i--)
            {
                RecentOptimisticShotPresentation recent = m_RecentOptimisticShotPresentations[i];
                if (recent.ExpiresAt < now)
                {
                    m_RecentOptimisticShotPresentations.RemoveAt(i);
                    continue;
                }

                if (!MatchesConfirmedShotBroadcast(recent.Request, broadcast)) continue;
                m_RecentOptimisticShotPresentations.RemoveAt(i);
                return true;
            }

            return false;
        }

        private static bool MatchesConfirmedShotBroadcast(
            NetworkShotRequest request,
            NetworkShotBroadcast broadcast)
        {
            Vector3 requestDirection = request.ShotDirection.sqrMagnitude > 0.0001f
                ? request.ShotDirection.normalized
                : Vector3.zero;
            Vector3 broadcastDirection = broadcast.ShotDirection.sqrMagnitude > 0.0001f
                ? broadcast.ShotDirection.normalized
                : Vector3.zero;

            return request.ShooterNetworkId == broadcast.ShooterNetworkId &&
                   request.WeaponHash == broadcast.WeaponHash &&
                   (request.MuzzlePosition - broadcast.MuzzlePosition).sqrMagnitude <= 0.25f &&
                   Vector3.Dot(requestDirection, broadcastDirection) >= 0.995f;
        }

        /// <summary>
        /// [All] Called when server broadcasts a confirmed hit.
        /// </summary>
        public void ReceiveHitBroadcast(NetworkShooterHitBroadcast broadcast)
        {
            bool isLocalShooter = m_IsLocalClient && broadcast.ShooterNetworkId == NetworkId;
            bool matchedOptimisticPresentation =
                isLocalShooter && HasMatchingOptimisticHitPresentation(broadcast);
            bool willPlayEffects = !matchedOptimisticPresentation;
            LogDiagnostics(
                $"hit broadcast received shooter={broadcast.ShooterNetworkId} target={broadcast.TargetNetworkId} " +
                $"weaponHash={broadcast.WeaponHash} willPlayEffects={willPlayEffects} point={broadcast.HitPoint}");
            NetworkShooterDebug.LogPhysics(
                "ControllerBroadcast",
                $"receiverActor={NetworkId} receiverRole={DebugRole} shooter={broadcast.ShooterNetworkId} " +
                $"target={broadcast.TargetNetworkId} weaponHash={broadcast.WeaponHash} " +
                $"point={broadcast.HitPoint} normal={broadcast.HitNormal} " +
                $"impactMotion={broadcast.HasImpactMotion} matchedOptimistic={matchedOptimisticPresentation}",
                this);

            // Confirmed environment physics is gameplay-independent local presentation. It must
            // run even when the owner's optimistic VFX marker suppresses duplicate particles.
            ApplyConfirmedEnvironmentImpact(broadcast);
            OnHitConfirmed?.Invoke(broadcast);

            // Play effects on observer instances or non-optimistic locals.
            if (willPlayEffects)
            {
                PlayHitEffects(broadcast);
            }
        }

        private bool HasMatchingOptimisticHitPresentation(NetworkShooterHitBroadcast broadcast)
        {
            ulong matchedPendingKey = 0;
            foreach (KeyValuePair<ulong, PendingHitRequest> pair in m_PendingHits)
            {
                PendingHitRequest pending = pair.Value;
                if (!pending.OptimisticPlayed) continue;
                if (!MatchesConfirmedHitBroadcast(pending.Request, broadcast)) continue;
                matchedPendingKey = pair.Key;
                break;
            }

            if (matchedPendingKey != 0 &&
                m_PendingHits.TryGetValue(matchedPendingKey, out PendingHitRequest matchedPending))
            {
                // Consume the marker without removing the request; its response still owns
                // request completion and rejection notification.
                matchedPending.OptimisticPlayed = false;
                m_PendingHits[matchedPendingKey] = matchedPending;
                return true;
            }

            float now = Time.time;
            for (int i = m_RecentOptimisticHitPresentations.Count - 1; i >= 0; i--)
            {
                RecentOptimisticHitPresentation recent = m_RecentOptimisticHitPresentations[i];
                if (recent.ExpiresAt < now)
                {
                    m_RecentOptimisticHitPresentations.RemoveAt(i);
                    continue;
                }

                if (!MatchesConfirmedHitBroadcast(recent.Request, broadcast)) continue;
                m_RecentOptimisticHitPresentations.RemoveAt(i);
                return true;
            }

            return false;
        }

        private static bool MatchesConfirmedHitBroadcast(
            NetworkShooterHitRequest request,
            NetworkShooterHitBroadcast broadcast)
        {
            return request.ShooterNetworkId == broadcast.ShooterNetworkId &&
                   request.TargetNetworkId == broadcast.TargetNetworkId &&
                   request.WeaponHash == broadcast.WeaponHash &&
                   (request.HitPoint - broadcast.HitPoint).sqrMagnitude <= 0.25f;
        }

        /// <summary>
        /// [All] Target-side delivery for a confirmed hit. This deliberately does not
        /// instantiate shared impact presentation, which is owned by the attacker path.
        /// </summary>
        public void ReceiveTargetHitBroadcast(NetworkShooterHitBroadcast broadcast)
        {
            OnHitConfirmed?.Invoke(broadcast);

            ShooterWeapon weapon = NetworkShooterManager.GetShooterWeaponByHash(broadcast.WeaponHash);
            if (weapon == null)
            {
                LogMissingHitAssetOnce(broadcast.WeaponHash, "target reaction");
                return;
            }

            PlayRemoteHitReaction(broadcast, weapon);
        }

        /// <summary>
        /// [All] Combined exact-once path for a character that hit itself.
        /// </summary>
        public void ReceiveSelfHitBroadcast(NetworkShooterHitBroadcast broadcast)
        {
            bool isLocalShooter = m_IsLocalClient && broadcast.ShooterNetworkId == NetworkId;
            OnHitConfirmed?.Invoke(broadcast);

            ShooterWeapon weapon = NetworkShooterManager.GetShooterWeaponByHash(broadcast.WeaponHash);
            if (weapon != null)
            {
                PlayRemoteHitReaction(broadcast, weapon);
            }
            else
            {
                LogMissingHitAssetOnce(broadcast.WeaponHash, "self-hit reaction");
            }

            if (!isLocalShooter || !HasMatchingOptimisticHitPresentation(broadcast))
            {
                PlayHitEffects(broadcast);
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // EFFECTS
        // ════════════════════════════════════════════════════════════════════════════════════════

        private void PlayShotEffects(NetworkShotBroadcast broadcast)
        {
            // Resolve weapon from hash to play muzzle flash, tracer, and fire sound.
            // On the local client these are handled optimistically by ShooterStance.
            // This path runs for remote clients observing another player's shots.
            ShooterWeapon weapon = NetworkShooterManager.GetShooterWeaponByHash(broadcast.WeaponHash);
            if (weapon == null)
            {
                LogMissingShotAssetOnce(broadcast.WeaponHash);
                if (m_UseGeneratedFallbackPresentation)
                {
                    DrawRemoteTracer(broadcast.MuzzlePosition, broadcast.HitPoint);
                }
                return;
            }

            LogDiagnostics(
                $"playing remote shot effects weapon={weapon.name} hash={broadcast.WeaponHash} " +
                $"muzzle={broadcast.MuzzlePosition} end={broadcast.HitPoint}");

            ApplyRemoteSightHash(weapon, broadcast.SightHash);
            PlayRemoteFireEffects(weapon, broadcast.MuzzlePosition, broadcast.ShotDirection);
            if (!TryPlayRemoteProjectileEffects(weapon, broadcast))
            {
                if (m_UseGeneratedFallbackPresentation)
                {
                    DrawRemoteTracer(broadcast.MuzzlePosition, broadcast.HitPoint);
                }
            }
        }

        private void PlayHitEffects(NetworkShooterHitBroadcast broadcast)
        {
            // Resolve weapon from hash to determine impact effect style.
            // MaterialHash (resolved server-side) can drive surface-specific particles.
            ShooterWeapon weapon = NetworkShooterManager.GetShooterWeaponByHash(broadcast.WeaponHash);
            if (weapon == null)
            {
                LogMissingHitAssetOnce(
                    broadcast.WeaponHash,
                    "impact presentation",
                    broadcast.MaterialHash);
                if (m_UseGeneratedFallbackPresentation)
                {
                    DrawRemoteImpact(broadcast.HitPoint, broadcast.HitNormal);
                }
                return;
            }

            LogDiagnostics(
                $"playing remote hit effects weapon={weapon.name} target={broadcast.TargetNetworkId} " +
                $"point={broadcast.HitPoint} materialHash={broadcast.MaterialHash}");

            GameObject impactTarget = ResolveImpactPresentationTarget(broadcast);
            if (!PlayConfiguredImpactEffect(
                    weapon,
                    impactTarget,
                    broadcast.HitPoint,
                    broadcast.HitNormal,
                    broadcast.MaterialHash))
            {
                if (m_UseGeneratedFallbackPresentation)
                {
                    DrawRemoteImpact(broadcast.HitPoint, broadcast.HitNormal);
                }
            }
        }

        private void LogMissingShotAssetOnce(int weaponHash)
        {
            if (!m_MissingShotEffectWeaponHashes.Add(weaponHash)) return;
            LogDiagnosticsWarning(
                $"cannot resolve Shooter weapon hash {weaponHash} for remote shot presentation. " +
                "Register the weapon and optional model/handle on PurrNetShooterTransportBridge. " +
                (m_UseGeneratedFallbackPresentation
                    ? "The configured generated fallback tracer will be shown."
                    : "Non-visual event routing continues; no fallback is configured."));
        }

        private void LogMissingHitAssetOnce(int weaponHash, string purpose, int materialHash = 0)
        {
            if (!m_MissingHitEffectWeaponHashes.Add(weaponHash)) return;
            LogDiagnosticsWarning(
                $"cannot resolve Shooter weapon hash {weaponHash} for {purpose} " +
                $"(materialHash={materialHash}). " +
                "Register it on PurrNetShooterTransportBridge. Gameplay routing continues. " +
                (m_UseGeneratedFallbackPresentation
                    ? "The configured generated fallback impact will be used."
                    : "No fallback presentation is configured."));
        }

        private void PlayRemoteHitReaction(NetworkShooterHitBroadcast broadcast, ShooterWeapon weapon)
        {
            if (broadcast.TargetNetworkId == 0 || broadcast.TargetNetworkId != NetworkId) return;
            if (m_Character == null || weapon == null) return;
            NetworkBlockResult blockResult = (NetworkBlockResult)broadcast.BlockResult;
            if (blockResult != NetworkBlockResult.None &&
                blockResult != NetworkBlockResult.BlockBroken)
            {
                return;
            }

            // The server instance already executed the authoritative native/fallback reaction.
            // Host transport loopback still raises OnHitConfirmed, but must not play it again.
            if (m_IsServer)
            {
                LogDiagnostics(
                    $"skipped duplicate host/server shooter reaction target={broadcast.TargetNetworkId} " +
                    $"weaponHash={broadcast.WeaponHash}");
                return;
            }

            NetworkCharacter attackerNetworkCharacter =
                NetworkShooterManager.Instance?.GetCharacterByNetworkId(broadcast.ShooterNetworkId);
            Character attackerCharacter = attackerNetworkCharacter != null
                ? attackerNetworkCharacter.GetComponent<Character>()
                : null;

            Vector3 shotDirection = broadcast.HitNormal.sqrMagnitude > 0.0001f
                ? -broadcast.HitNormal.normalized
                : attackerCharacter != null
                    ? (m_Character.transform.position - attackerCharacter.transform.position).normalized
                    : (m_Character.transform.position - broadcast.HitPoint).normalized;

            if (shotDirection.sqrMagnitude <= 0.0001f)
            {
                shotDirection = -m_Character.transform.forward;
            }

            Args args = new Args(
                attackerCharacter != null ? attackerCharacter.gameObject : null,
                m_Character.gameObject);
            var reactionInput = new ReactionInput(
                m_Character.transform.InverseTransformDirection(shotDirection).normalized,
                ResolveBroadcastReactionPower(broadcast)
            );

            INetworkOwnerMotionAuthority motionAuthority = null;
            if (m_IsLocalClient)
            {
                motionAuthority = m_NetworkCharacter?.OwnerMotionAuthority;
                if (motionAuthority != null)
                {
                    motionAuthority.OpenOwnerMotionWindow(OWNER_REACTION_MOTION_WINDOW_SECONDS);
                }
                else
                {
                    LogHitInvariantWarning(
                        $"local Shooter target '{name}' cannot synchronize reaction root motion because " +
                        "its active movement backend does not implement INetworkOwnerMotionAuthority. " +
                        "Use the built-in network movement backend for Shooter reactions.");
                }
            }

            ReactionOutput reactionOutput = m_Character.Combat.GetHitReaction(
                reactionInput,
                args,
                weapon.HitReaction);
            motionAuthority?.OpenOwnerMotionWindow(
                NetworkShooterReactionContext.CalculateOwnerMotionWindow(reactionOutput));
            LogDiagnostics(
                $"played remote shooter hit reaction target={broadcast.TargetNetworkId} " +
                $"weapon={weapon.name} direction={reactionInput.Direction} power={reactionInput.Power:F2}");
        }

        private static float ResolveBroadcastReactionPower(NetworkShooterHitBroadcast broadcast)
        {
            return float.IsNaN(broadcast.ReactionPower) ||
                   float.IsInfinity(broadcast.ReactionPower)
                ? 0f
                : broadcast.ReactionPower;
        }

        private void PlayRemoteFireEffects(ShooterWeapon weapon, Vector3 muzzlePosition, Vector3 shotDirection)
        {
            if (m_Character == null || weapon == null)
            {
                LogDiagnosticsWarning(
                    $"remote fire effects skipped character={(m_Character != null)} weapon={(weapon != null)}");
                return;
            }

            TryGetShooterStance();
            GameObject prop = m_Character.Combat.GetProp(weapon);
            var args = new Args(m_Character.gameObject, prop);

            Animator propAnimator = prop != null ? prop.Get<Animator>() : null;
            if (propAnimator != null) propAnimator.SetTrigger(SHOOT_TRIGGER);

            AnimationClip fireAnimationClip = weapon.Fire.FireAnimation(args);
            float duration = 0f;

            if (fireAnimationClip != null)
            {
                ConfigGesture config = weapon.Fire.FireConfig(fireAnimationClip);
                _ = m_Character.Gestures.CrossFade(
                    fireAnimationClip,
                    weapon.Fire.FireAvatarMask,
                    BlendMode.Blend,
                    config,
                    true
                );

                duration = fireAnimationClip.length - config.TransitionOut;
            }

            AudioClip fireAudio = weapon.Fire.FireAudio(args);
            if (fireAudio != null)
            {
                _ = AudioManager.Instance.SoundEffect.Play(
                    fireAudio,
                    AudioConfigSoundEffect.Create(
                        1f,
                        new Vector2(0.95f, 1.05f),
                        0f,
                        m_Character.Time.UpdateTime,
                        prop != null ? SpatialBlending.Spatial : SpatialBlending.None,
                        prop
                    ),
                    args
                );
            }

            GameObject muzzleEffect = weapon.Fire.MuzzleEffect(args);
            if (muzzleEffect != null)
            {
                Quaternion rotation = shotDirection.sqrMagnitude > 0.0001f
                    ? Quaternion.LookRotation(shotDirection.normalized)
                    : Quaternion.identity;
                muzzleEffect.transform.SetPositionAndRotation(muzzlePosition, rotation);
            }

            TryPlayRemoteShellEjection(weapon, args, prop);
            NotifyRemoteShotState(weapon, args, duration);

            LogDiagnostics(
                $"remote fire effects result prop={(prop != null ? prop.name : "null")} " +
                $"propAnimator={(propAnimator != null)} fireClip={(fireAnimationClip != null ? fireAnimationClip.name : "null")} " +
                $"fireAudio={(fireAudio != null ? fireAudio.name : "null")} muzzleEffect={(muzzleEffect != null ? muzzleEffect.name : "null")} " +
                $"duration={duration:F2}");

            _ = duration;
        }

        private void TryPlayRemoteShellEjection(ShooterWeapon weapon, Args args, GameObject prop)
        {
            if (weapon == null || prop == null) return;

            try
            {
                weapon.Shell.Eject(args);
                LogDiagnostics($"played remote shell ejection weapon={weapon.name} prop={prop.name}");
            }
            catch (Exception exception)
            {
                LogDiagnosticsWarning(
                    $"remote shell ejection failed weapon={weapon.name}: {exception.GetType().Name}");
            }
        }

        private bool TryPlayRemoteProjectileEffects(ShooterWeapon weapon, NetworkShotBroadcast broadcast)
        {
            if (weapon == null || m_Character == null) return false;
            if (!TryGetRemoteShotType(weapon, out TShot shotType))
            {
                LogDiagnosticsWarning($"remote projectile playback skipped: could not resolve shot type for {weapon.name}");
                return false;
            }

            Args args = GetShooterWeaponArgs(weapon);
            Vector3 direction = broadcast.ShotDirection.sqrMagnitude > 0.0001f
                ? broadcast.ShotDirection.normalized
                : m_Character.transform.forward;

            return shotType switch
            {
                ShotRaycast raycast => PlayRemoteRaycastShot(raycast, weapon, args, broadcast.MuzzlePosition, direction),
                TShotProjectile projectile => PlayRemoteProjectileShot(projectile, weapon, args, broadcast.MuzzlePosition, direction),
                _ => false
            };
        }

        private static bool TryGetRemoteShotType(ShooterWeapon weapon, out TShot shotType)
        {
            shotType = null;
            if (weapon == null || PROJECTILE_SHOT_FIELD == null) return false;

            if (PROJECTILE_SHOT_FIELD.GetValue(weapon.Projectile) is Shot shot)
            {
                shotType = shot.Value;
            }

            return shotType != null;
        }

        private bool PlayRemoteRaycastShot(
            ShotRaycast shot,
            ShooterWeapon weapon,
            Args args,
            Vector3 muzzlePosition,
            Vector3 direction)
        {
            LayerMask layerMask = GetLayerMaskField(SHOT_RAYCAST_LAYER_MASK_FIELD, shot, Physics.DefaultRaycastLayers);
            float maxDistance = GetDecimalField(SHOT_RAYCAST_MAX_DISTANCE_FIELD, shot, args, 100f);
            int pierces = Mathf.Max(0, GetIntegerField(SHOT_RAYCAST_PIERCES_FIELD, shot, args, 0));
            bool useLineRenderer = GetBoolField(SHOT_RAYCAST_USE_LINE_FIELD, shot, true);

            int hitCount = Physics.RaycastNonAlloc(
                muzzlePosition,
                direction,
                REMOTE_SHOT_HITS,
                maxDistance,
                layerMask,
                QueryTriggerInteraction.Ignore
            );

            if (hitCount > 1)
            {
                Array.Sort(REMOTE_SHOT_HITS, 0, hitCount, RAYCAST_HIT_DISTANCE_COMPARER);
            }

            int maxIterations = Mathf.Min(pierces + 1, hitCount);
            Vector3 tracerEnd = muzzlePosition + direction * maxDistance;
            int processed = 0;

            for (int i = 0; i < hitCount && processed < maxIterations; i++)
            {
                RaycastHit hit = REMOTE_SHOT_HITS[i];
                if (hit.collider == null) continue;
                if (m_Character != null && hit.collider.GetComponentInParent<Character>() == m_Character) continue;

                tracerEnd = hit.point;
                processed++;
            }

            if (useLineRenderer)
            {
                float duration = GetDecimalField(SHOT_RAYCAST_DURATION_FIELD, shot, args, 0.5f);
                Material material = GetMaterialField(SHOT_RAYCAST_MATERIAL_FIELD, shot, args);
                Color color = GetColorField(SHOT_RAYCAST_COLOR_FIELD, shot, args, Color.white);
                float width = GetDecimalField(SHOT_RAYCAST_WIDTH_FIELD, shot, args, 0.1f);
                var textureMode = GetEnumField(SHOT_RAYCAST_TEXTURE_MODE_FIELD, shot, LineTextureMode.Stretch);
                var textureAlign = GetEnumField(SHOT_RAYCAST_TEXTURE_ALIGN_FIELD, shot, LineAlignment.View);

                DrawConfiguredRemoteTracer(
                    muzzlePosition,
                    tracerEnd,
                    duration,
                    maxDistance,
                    material,
                    color,
                    width,
                    textureMode,
                    textureAlign
                );
            }

            LogDiagnostics(
                $"remote raycast VFX played weapon={weapon.name} hits={processed}/{hitCount} " +
                $"muzzle={muzzlePosition} end={tracerEnd}");
            return true;
        }

        private bool PlayRemoteProjectileShot(
            TShotProjectile shot,
            ShooterWeapon weapon,
            Args args,
            Vector3 muzzlePosition,
            Vector3 direction)
        {
            GameObject prefab = GetProjectilePrefab(shot, args);
            if (prefab == null)
            {
                LogDiagnosticsWarning($"remote projectile playback skipped: projectile prefab missing for {weapon.name}");
                return false;
            }

            float delay = GetDecimalField(SHOT_PROJECTILE_DELAY_FIELD, shot, args, 0f);
            float speed = 50f;
            float airResistance = 0f;
            float windInfluence = 0f;
            float gravity = 0f;
            float maxDistance = 100f;
            float lifetime = Mathf.Max(5f, delay + 5f);
            bool useRigidbody = shot is ShotRigidbody;

            if (shot is ShotRigidbody)
            {
                speed = GetDecimalField(SHOT_RIGIDBODY_FORCE_FIELD, shot, args, 50f);
                airResistance = GetDecimalField(SHOT_RIGIDBODY_AIR_RESISTANCE_FIELD, shot, args, 0f);
                windInfluence = GetDecimalField(SHOT_RIGIDBODY_WIND_FIELD, shot, args, 0f);
                maxDistance = GetDecimalField(SHOT_RIGIDBODY_MAX_DISTANCE_FIELD, shot, args, 100f);
                lifetime = Mathf.Max(delay + GetDecimalField(SHOT_RIGIDBODY_TIMEOUT_FIELD, shot, args, 5f), 0.25f);
            }
            else if (shot is ShotKinematic)
            {
                speed = GetDecimalField(SHOT_KINEMATIC_FORCE_FIELD, shot, args, 50f);
                gravity = GetDecimalField(SHOT_KINEMATIC_GRAVITY_FIELD, shot, args, Physics.gravity.magnitude);
                airResistance = GetDecimalField(SHOT_KINEMATIC_AIR_RESISTANCE_FIELD, shot, args, 0f);
                windInfluence = GetDecimalField(SHOT_KINEMATIC_WIND_FIELD, shot, args, 0f);
                maxDistance = GetDecimalField(SHOT_KINEMATIC_MAX_DISTANCE_FIELD, shot, args, 100f);
                lifetime = Mathf.Max(delay + GetDecimalField(SHOT_KINEMATIC_TIMEOUT_FIELD, shot, args, 5f), 0.25f);
            }
            else if (shot is ShotTracer)
            {
                speed = GetDecimalField(SHOT_TRACER_SPEED_FIELD, shot, args, 50f);
                maxDistance = 100f;
                lifetime = Mathf.Max(maxDistance / Mathf.Max(0.1f, speed), 0.25f);
            }

            GameObject projectile = PoolManager.Instance.Pick(
                prefab,
                muzzlePosition,
                Quaternion.LookRotation(direction),
                1,
                lifetime
            );

            if (projectile == null) return false;

            if (useRigidbody && projectile.TryGetComponent(out Rigidbody rigidbody))
            {
                float mass = GetDecimalField(SHOT_RIGIDBODY_MASS_FIELD, shot, args, 1f);
                ForceMode impulse = GetForceModeField(SHOT_RIGIDBODY_IMPULSE_FIELD, shot, ForceMode.VelocityChange);

                rigidbody.mass = Mathf.Max(0.001f, mass);
                rigidbody.linearDamping = Mathf.Max(0f, airResistance);
                rigidbody.linearVelocity = Vector3.zero;
                rigidbody.angularVelocity = Vector3.zero;
                rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                rigidbody.AddForce(direction * speed, impulse);
            }

            var vfx = projectile.GetComponent<NetworkShooterProjectileVfx>() ??
                      projectile.AddComponent<NetworkShooterProjectileVfx>();

            LayerMask layerMask = shot is ShotKinematic
                ? GetLayerMaskField(SHOT_KINEMATIC_LAYER_MASK_FIELD, shot, Physics.DefaultRaycastLayers)
                : shot is ShotTracer
                    ? GetLayerMaskField(SHOT_TRACER_LAYER_MASK_FIELD, shot, Physics.DefaultRaycastLayers)
                    : Physics.DefaultRaycastLayers;

            vfx.Configure(
                m_Character,
                weapon,
                direction * speed,
                layerMask,
                maxDistance,
                lifetime,
                airResistance,
                windInfluence,
                gravity,
                useRigidbody
            );

            LogDiagnostics(
                $"remote projectile VFX played weapon={weapon.name} shot={shot.GetType().Name} " +
                $"prefab={prefab.name} speed={speed:F2} rigidbody={useRigidbody}");
            return true;
        }

        private static GameObject GetProjectilePrefab(TShotProjectile shot, Args args)
        {
            return SHOT_PROJECTILE_PREFAB_FIELD?.GetValue(shot) is PropertyGetGameObject prefab
                ? prefab.Get(args)
                : null;
        }

        private bool ApplyConfirmedEnvironmentImpact(NetworkShooterHitBroadcast broadcast)
        {
            if (broadcast.TargetNetworkId != 0 || broadcast.HasImpactMotion)
            {
                NetworkShooterDebug.LogPhysics(
                    "ConfirmedImpulse",
                    $"skipped shooter={broadcast.ShooterNetworkId} target={broadcast.TargetNetworkId} " +
                    $"impactMotion={broadcast.HasImpactMotion} weaponHash={broadcast.WeaponHash}",
                    this);
                return false;
            }

            ShooterWeapon weapon = NetworkShooterManager.GetShooterWeaponByHash(broadcast.WeaponHash);
            if (weapon == null)
            {
                NetworkShooterDebug.LogPhysics(
                    "ConfirmedImpulse",
                    $"failed missing-weapon shooter={broadcast.ShooterNetworkId} " +
                    $"weaponHash={broadcast.WeaponHash} point={broadcast.HitPoint}",
                    this);
                LogMissingHitAssetOnce(
                    broadcast.WeaponHash,
                    "confirmed environment physics",
                    broadcast.MaterialHash);
                return false;
            }

            if (!weapon.Fire.ForceEnabled)
            {
                NetworkShooterDebug.LogPhysics(
                    "ConfirmedImpulse",
                    $"skipped force-disabled shooter={broadcast.ShooterNetworkId} " +
                    $"weapon={weapon.name} weaponHash={broadcast.WeaponHash}",
                    this);
                return false;
            }

            Vector3 direction = broadcast.HitNormal.sqrMagnitude > 0.0001f
                ? -broadcast.HitNormal.normalized
                : m_Character != null ? m_Character.transform.forward : Vector3.forward;

            if (!TryResolveEnvironmentImpactCollider(broadcast.HitPoint, direction, out Collider collider) ||
                !TryResolveOrdinaryEnvironmentRigidbody(collider, out Rigidbody rigidbody))
            {
                int nearbyCount = Physics.OverlapSphereNonAlloc(
                    broadcast.HitPoint,
                    0.75f,
                    REMOTE_IMPACT_COLLIDERS,
                    ~0,
                    QueryTriggerInteraction.Ignore);
                string nearby = BuildNearbyColliderDiagnostic(nearbyCount);
                ClearImpactColliderBuffer(nearbyCount);
                NetworkShooterDebug.LogPhysics(
                    "ConfirmedImpulse",
                    $"failed unresolved shooter={broadcast.ShooterNetworkId} weapon={weapon.name} " +
                    $"weaponHash={broadcast.WeaponHash} point={broadcast.HitPoint} direction={direction} " +
                    $"nearbyCount={nearbyCount} nearby={nearby}",
                    this);
                LogDiagnostics(
                    $"confirmed environment physics unresolved shooter={broadcast.ShooterNetworkId} " +
                    $"weaponHash={broadcast.WeaponHash} point={broadcast.HitPoint}");
                return false;
            }

            bool consumedNativeMarker = TryConsumeServerNativeEnvironmentImpactMarker(
                rigidbody,
                broadcast.WeaponHash,
                broadcast.HitPoint);
            Vector3 velocityBefore = rigidbody.linearVelocity;
            Vector3 angularVelocityBefore = rigidbody.angularVelocity;
            if (Physics.simulationMode == SimulationMode.Script)
            {
                LogHitInvariantWarning(
                    "A confirmed Shooter Rigidbody impulse was received while Unity 3D Physics " +
                    "Simulation Mode is Script. The impulse is queued, but the Rigidbody cannot " +
                    "move unless an active prediction/physics owner calls PhysicsScene.Simulate. " +
                    "For the built-in PurrNet demos, enable Project Settings > Physics > Auto Simulation.");
            }

            if (!consumedNativeMarker)
            {
                rigidbody.WakeUp();
                rigidbody.AddForceAtPosition(
                    direction.normalized * weapon.Fire.Force,
                    broadcast.HitPoint,
                    ForceMode.Impulse);
            }

            NetworkShooterDebug.LogPhysics(
                "ConfirmedImpulse",
                $"applied shooter={broadcast.ShooterNetworkId} receiverActor={NetworkId} role={DebugRole} " +
                $"weapon={weapon.name} weaponHash={broadcast.WeaponHash} collider={collider.name} " +
                $"{DescribeRigidbodyState(rigidbody)} force={weapon.Fire.Force:F2} point={broadcast.HitPoint} " +
                $"direction={direction.normalized} nativeMarkerConsumed={consumedNativeMarker} " +
                $"velocityBefore={velocityBefore} velocityAfter={rigidbody.linearVelocity} " +
                $"angularBefore={angularVelocityBefore} angularAfter={rigidbody.angularVelocity}",
                this);

            LogDiagnostics(
                $"confirmed environment physics shooter={broadcast.ShooterNetworkId} " +
                $"weapon={weapon.name} weaponHash={broadcast.WeaponHash} collider={collider.name} " +
                $"rigidbody={rigidbody.name} force={weapon.Fire.Force:F2} point={broadcast.HitPoint} " +
                $"direction={direction.normalized} nativeMarkerConsumed={consumedNativeMarker}");
            return !consumedNativeMarker;
        }

        private void RecordServerNativeEnvironmentImpactMarker(ShotData data)
        {
            if (!m_IsServer || data.Target == null || data.Weapon == null) return;
            if (!data.Weapon.Fire.ForceEnabled) return;
            if (!TryResolveOrdinaryEnvironmentRigidbody(data.Target, out Rigidbody rigidbody)) return;

            float now = Time.time;
            CleanupServerNativeEnvironmentImpactMarkers(now);
            m_RecentServerNativeEnvironmentImpacts.Add(new RecentServerNativeEnvironmentImpact
            {
                RigidbodyInstanceId = rigidbody.GetInstanceID(),
                WeaponHash = data.Weapon.Id.Hash,
                HitPoint = data.HitPoint,
                ExpiresAt = now + SERVER_NATIVE_IMPACT_MARKER_LIFETIME_SECONDS
            });

            NetworkShooterDebug.LogPhysics(
                "NativeImpulseMarker",
                $"actor={NetworkId} weapon={data.Weapon.name} hash={data.Weapon.Id.Hash} " +
                $"point={data.HitPoint} markers={m_RecentServerNativeEnvironmentImpacts.Count} " +
                $"{DescribeRigidbodyState(rigidbody)}",
                this);

            LogDiagnostics(
                $"recorded server-native environment physics marker weapon={data.Weapon.name} " +
                $"weaponHash={data.Weapon.Id.Hash} rigidbody={rigidbody.name} point={data.HitPoint} " +
                $"expires={SERVER_NATIVE_IMPACT_MARKER_LIFETIME_SECONDS:F1}s");
        }

        private bool TryConsumeServerNativeEnvironmentImpactMarker(
            Rigidbody rigidbody,
            int weaponHash,
            Vector3 hitPoint)
        {
            if (!m_IsServer || rigidbody == null) return false;

            float now = Time.time;
            float toleranceSqr =
                SERVER_NATIVE_IMPACT_POSITION_TOLERANCE * SERVER_NATIVE_IMPACT_POSITION_TOLERANCE;
            int rigidbodyInstanceId = rigidbody.GetInstanceID();

            for (int i = m_RecentServerNativeEnvironmentImpacts.Count - 1; i >= 0; i--)
            {
                RecentServerNativeEnvironmentImpact marker = m_RecentServerNativeEnvironmentImpacts[i];
                if (marker.ExpiresAt < now)
                {
                    m_RecentServerNativeEnvironmentImpacts.RemoveAt(i);
                    continue;
                }

                if (marker.RigidbodyInstanceId != rigidbodyInstanceId ||
                    marker.WeaponHash != weaponHash ||
                    (marker.HitPoint - hitPoint).sqrMagnitude > toleranceSqr)
                {
                    continue;
                }

                m_RecentServerNativeEnvironmentImpacts.RemoveAt(i);
                return true;
            }

            return false;
        }

        private void CleanupServerNativeEnvironmentImpactMarkers(float now)
        {
            for (int i = m_RecentServerNativeEnvironmentImpacts.Count - 1; i >= 0; i--)
            {
                if (m_RecentServerNativeEnvironmentImpacts[i].ExpiresAt < now)
                {
                    m_RecentServerNativeEnvironmentImpacts.RemoveAt(i);
                }
            }
        }

        private void ClearServerNativeEnvironmentImpactMarkers()
        {
            m_RecentServerNativeEnvironmentImpacts.Clear();
        }

        private static bool TryResolveOrdinaryEnvironmentRigidbody(
            GameObject target,
            out Rigidbody rigidbody)
        {
            rigidbody = null;
            if (target == null) return false;
            if (target.GetComponentInParent<Character>() != null) return false;
            if (target.GetComponentInParent<NetworkShooterImpactProp>() != null) return false;

            Collider collider = target.GetComponent<Collider>();
            if (collider != null && collider.attachedRigidbody != null)
            {
                rigidbody = collider.attachedRigidbody;
            }

            rigidbody ??= target.GetComponent<Rigidbody>();
            rigidbody ??= target.GetComponentInParent<Rigidbody>();
            return rigidbody != null;
        }

        private static string DescribeRigidbodyState(Rigidbody rigidbody)
        {
            if (rigidbody == null) return "rigidbody=null";

            return $"rigidbody={rigidbody.name} rbInstance={rigidbody.GetInstanceID()} " +
                   $"kinematic={rigidbody.isKinematic} mass={rigidbody.mass:F2} " +
                   $"simulationMode={Physics.simulationMode} " +
                   $"sleeping={rigidbody.IsSleeping()} position={rigidbody.position} " +
                   $"velocity={rigidbody.linearVelocity}";
        }

        private static string BuildNearbyColliderDiagnostic(int count)
        {
            if (count <= 0) return "none";

            int safeCount = Mathf.Min(count, REMOTE_IMPACT_COLLIDERS.Length);
            var descriptions = new string[safeCount];
            for (int i = 0; i < safeCount; i++)
            {
                Collider candidate = REMOTE_IMPACT_COLLIDERS[i];
                if (candidate == null)
                {
                    descriptions[i] = "null";
                    continue;
                }

                Rigidbody rigidbody = candidate.attachedRigidbody != null
                    ? candidate.attachedRigidbody
                    : candidate.GetComponentInParent<Rigidbody>();
                descriptions[i] =
                    $"{candidate.name}(trigger={candidate.isTrigger},layer={candidate.gameObject.layer}," +
                    $"character={(candidate.GetComponentInParent<Character>() != null)}," +
                    $"impactProp={(candidate.GetComponentInParent<NetworkShooterImpactProp>() != null)}," +
                    $"rb={(rigidbody != null ? rigidbody.name : "null")})";
            }

            return string.Join("|", descriptions);
        }

        private static void ClearImpactColliderBuffer(int count)
        {
            int safeCount = Mathf.Min(count, REMOTE_IMPACT_COLLIDERS.Length);
            for (int i = 0; i < safeCount; i++)
            {
                REMOTE_IMPACT_COLLIDERS[i] = null;
            }
        }

        private static bool TryResolveOrdinaryEnvironmentRigidbody(
            Collider collider,
            out Rigidbody rigidbody)
        {
            rigidbody = null;
            if (!IsValidEnvironmentImpactCollider(collider)) return false;

            rigidbody = collider.attachedRigidbody != null
                ? collider.attachedRigidbody
                : collider.GetComponentInParent<Rigidbody>();
            return rigidbody != null;
        }

        private bool TryResolveEnvironmentImpactCollider(Vector3 point, Vector3 direction, out Collider collider)
        {
            collider = null;
            float bestDistanceSqr = float.MaxValue;

            int count = Physics.OverlapSphereNonAlloc(
                point,
                0.35f,
                REMOTE_IMPACT_COLLIDERS,
                ~0,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                Collider candidate = REMOTE_IMPACT_COLLIDERS[i];
                REMOTE_IMPACT_COLLIDERS[i] = null;
                if (!IsValidEnvironmentImpactCollider(candidate)) continue;

                Vector3 closest = candidate.ClosestPoint(point);
                float distanceSqr = (closest - point).sqrMagnitude;
                if (distanceSqr >= bestDistanceSqr) continue;

                bestDistanceSqr = distanceSqr;
                collider = candidate;
            }

            if (collider != null) return true;

            Vector3 rayDirection = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
            Vector3 rayOrigin = point - rayDirection * 0.25f;
            if (Physics.Raycast(
                    rayOrigin,
                    rayDirection,
                    out RaycastHit hit,
                    0.75f,
                    ~0,
                    QueryTriggerInteraction.Ignore) &&
                IsValidEnvironmentImpactCollider(hit.collider))
            {
                collider = hit.collider;
                return true;
            }

            return false;
        }

        private GameObject ResolveImpactPresentationTarget(NetworkShooterHitBroadcast broadcast)
        {
            GameObject fallback = null;

            if (broadcast.TargetNetworkId != 0)
            {
                NetworkCharacter target = NetworkShooterManager.Instance != null
                    ? NetworkShooterManager.Instance.GetCharacterByNetworkId(broadcast.TargetNetworkId)
                    : null;
                fallback = target != null ? target.gameObject : null;
            }
            else if (TryResolveImpactPresentationCollider(
                         broadcast.HitPoint,
                         broadcast.HitNormal.sqrMagnitude > 0.0001f
                             ? -broadcast.HitNormal.normalized
                             : Vector3.forward,
                         out Collider collider))
            {
                fallback = collider.gameObject;
            }

            var manager = NetworkShooterManager.Instance;
            if (manager?.ResolveMaterialTargetFunc != null)
            {
                try
                {
                    fallback = manager.ResolveMaterialTargetFunc.Invoke(broadcast.MaterialHash, fallback) ?? fallback;
                }
                catch (Exception exception)
                {
                    LogDiagnosticsWarning(
                        $"material target resolver failed materialHash={broadcast.MaterialHash}: " +
                        $"{exception.GetType().Name}: {exception.Message}");
                }
            }

            if (broadcast.MaterialHash != 0 && fallback == null)
            {
                LogDiagnosticsWarning(
                    $"impact material hash {broadcast.MaterialHash} had no local collider/target at {broadcast.HitPoint}");
            }

            return fallback;
        }

        private static bool TryResolveImpactPresentationCollider(
            Vector3 point,
            Vector3 direction,
            out Collider collider)
        {
            collider = null;
            float bestDistanceSqr = float.MaxValue;

            int count = Physics.OverlapSphereNonAlloc(
                point,
                0.35f,
                REMOTE_IMPACT_COLLIDERS,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                Collider candidate = REMOTE_IMPACT_COLLIDERS[i];
                REMOTE_IMPACT_COLLIDERS[i] = null;
                if (candidate == null || candidate.isTrigger) continue;
                if (candidate.GetComponentInParent<Character>() != null) continue;

                float distanceSqr = (candidate.ClosestPoint(point) - point).sqrMagnitude;
                if (distanceSqr >= bestDistanceSqr) continue;
                bestDistanceSqr = distanceSqr;
                collider = candidate;
            }

            if (collider != null) return true;

            Vector3 rayDirection = direction.sqrMagnitude > 0.0001f
                ? direction.normalized
                : Vector3.forward;
            return Physics.Raycast(
                       point - rayDirection * 0.25f,
                       rayDirection,
                       out RaycastHit hit,
                       0.75f,
                       Physics.DefaultRaycastLayers,
                       QueryTriggerInteraction.Ignore) &&
                   (collider = hit.collider) != null &&
                   !collider.isTrigger &&
                   collider.GetComponentInParent<Character>() == null;
        }

        private static bool IsValidEnvironmentImpactCollider(Collider collider)
        {
            if (collider == null || collider.isTrigger) return false;
            if (collider.GetComponentInParent<Character>() != null) return false;
            if (collider.GetComponentInParent<NetworkShooterImpactProp>() != null) return false;

            return collider.attachedRigidbody != null ||
                   collider.GetComponentInParent<Rigidbody>() != null;
        }

        private bool PlayConfiguredImpactEffect(
            ShooterWeapon weapon,
            GameObject target,
            Vector3 point,
            Vector3 normal,
            int materialHash = 0)
        {
            if (weapon == null || m_Character == null) return false;

            bool played = false;
            Quaternion rotation = normal.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(normal.normalized)
                : Quaternion.identity;

            Args args = target != null
                ? new Args(m_Character.gameObject, target)
                : new Args(m_Character.gameObject);

            if (PROJECTILE_IMPACT_EFFECT_FIELD?.GetValue(weapon.Projectile) is PropertyGetInstantiate effect)
            {
                GameObject instance = effect.Get(args, point, rotation);
                played = instance != null;
            }

            if (target != null &&
                PROJECTILE_IMPACT_SOUND_FIELD?.GetValue(weapon.Projectile) is MaterialSoundsAsset impactSound)
            {
                MaterialSounds.Play(args, point, normal, target, impactSound, UnityEngine.Random.Range(-180f, 180f));
                played = true;
                LogDiagnostics(
                    $"played configured material impact sound materialHash={materialHash} target={target.name}");
            }

            return played;
        }

        private static void DrawConfiguredRemoteTracer(
            Vector3 start,
            Vector3 end,
            float duration,
            float maxDistance,
            Material material,
            Color color,
            float width,
            LineTextureMode textureMode,
            LineAlignment textureAlign)
        {
            var lineObject = new GameObject("Network Shooter Raycast Tracer");
            lineObject.AddComponent<LineRenderer>();
            var tracer = lineObject.AddComponent<Tracer>();
            material ??= new Material(Shader.Find("Sprites/Default"));
            tracer.OnShoot(
                start,
                end,
                Mathf.Max(0.01f, duration),
                Mathf.Max(0.1f, maxDistance),
                material,
                color,
                Mathf.Max(0.001f, width),
                textureMode,
                textureAlign
            );
            UnityEngine.Object.Destroy(lineObject, Mathf.Max(0.05f, duration) + 0.1f);
        }

        private static LayerMask GetLayerMaskField(FieldInfo field, object target, LayerMask fallback)
        {
            return field != null && field.GetValue(target) is LayerMask value ? value : fallback;
        }

        private static bool GetBoolField(FieldInfo field, object target, bool fallback)
        {
            return field != null && field.GetValue(target) is bool value ? value : fallback;
        }

        private static int GetIntegerField(FieldInfo field, object target, Args args, int fallback)
        {
            return field != null && field.GetValue(target) is PropertyGetInteger value
                ? (int)value.Get(args)
                : fallback;
        }

        private static float GetDecimalField(FieldInfo field, object target, Args args, float fallback)
        {
            return field != null && field.GetValue(target) is PropertyGetDecimal value
                ? (float)value.Get(args)
                : fallback;
        }

        private static Material GetMaterialField(FieldInfo field, object target, Args args)
        {
            return field != null && field.GetValue(target) is PropertyGetMaterial value
                ? value.Get(args)
                : null;
        }

        private static Color GetColorField(FieldInfo field, object target, Args args, Color fallback)
        {
            return field != null && field.GetValue(target) is PropertyGetColor value
                ? value.Get(args)
                : fallback;
        }

        private static T GetEnumField<T>(FieldInfo field, object target, T fallback) where T : struct, Enum
        {
            object value = field?.GetValue(target);
            if (value == null) return fallback;

            try
            {
                return (T)Enum.ToObject(typeof(T), Convert.ToInt32(value));
            }
            catch
            {
                return fallback;
            }
        }

        private static ForceMode GetForceModeField(FieldInfo field, object target, ForceMode fallback)
        {
            object value = field?.GetValue(target);
            if (value == null) return fallback;

            try
            {
                return (ForceMode)Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static void DrawRemoteTracer(Vector3 start, Vector3 end)
        {
            var lineObject = new GameObject("Network Shooter Tracer");
            var line = lineObject.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPosition(0, start);
            line.SetPosition(1, end);
            line.startWidth = 0.035f;
            line.endWidth = 0.01f;
            line.startColor = Color.yellow;
            line.endColor = new Color(1f, 0.65f, 0.1f, 0f);
            line.material = new Material(Shader.Find("Sprites/Default"));
            UnityEngine.Object.Destroy(lineObject, 0.18f);
        }

        private static void DrawRemoteImpact(Vector3 point, Vector3 normal)
        {
            var impact = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            impact.name = "Network Shooter Impact";
            impact.transform.position = point;
            impact.transform.localScale = Vector3.one * 0.08f;

            if (impact.TryGetComponent<Collider>(out var collider))
            {
                UnityEngine.Object.Destroy(collider);
            }

            var renderer = impact.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material = new Material(Shader.Find("Standard"))
                {
                    color = Color.yellow
                };
            }

            if (normal.sqrMagnitude > 0.0001f)
            {
                impact.transform.rotation = Quaternion.LookRotation(normal.normalized);
            }

            UnityEngine.Object.Destroy(impact, 0.12f);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // VALIDATION HELPERS
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Maps CombatValidationRejectionReason to HitRejectionReason.
        /// </summary>
        private static HitRejectionReason MapValidationRejection(CombatValidationRejectionReason reason)
        {
            return reason switch
            {
                CombatValidationRejectionReason.None => HitRejectionReason.None,
                CombatValidationRejectionReason.AttackerNotFound => HitRejectionReason.ShooterNotFound,
                CombatValidationRejectionReason.TargetNotFound => HitRejectionReason.TargetNotFound,
                CombatValidationRejectionReason.AttackerNotRegistered => HitRejectionReason.ShooterNotFound,
                CombatValidationRejectionReason.TargetNotRegistered => HitRejectionReason.TargetNotFound,
                CombatValidationRejectionReason.TargetNotActive => HitRejectionReason.TargetNotFound,
                CombatValidationRejectionReason.TargetInvincible => HitRejectionReason.TargetInvincible,
                CombatValidationRejectionReason.TargetDodging => HitRejectionReason.TargetDodged,
                CombatValidationRejectionReason.TargetDead => HitRejectionReason.TargetDead,
                CombatValidationRejectionReason.TimestampTooOld => HitRejectionReason.TimestampTooOld,
                CombatValidationRejectionReason.TimestampInFuture => HitRejectionReason.CheatSuspected,
                CombatValidationRejectionReason.NoHistoryAvailable => HitRejectionReason.TimestampTooOld,
                CombatValidationRejectionReason.RaycastMissed => HitRejectionReason.RaycastMissed,
                CombatValidationRejectionReason.OutOfShooterRange => HitRejectionReason.OutOfRange,
                CombatValidationRejectionReason.InvalidTrajectory => HitRejectionReason.InvalidTrajectory,
                CombatValidationRejectionReason.ShotNotValidated => HitRejectionReason.ShotNotValidated,
                CombatValidationRejectionReason.InvalidMuzzlePosition => HitRejectionReason.InvalidPosition,
                CombatValidationRejectionReason.CheatSuspected => HitRejectionReason.CheatSuspected,
                _ => HitRejectionReason.CheatSuspected
            };
        }

    }
}
#endif
