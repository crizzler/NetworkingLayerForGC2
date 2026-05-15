#if GC2_SHOOTER
using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Shooter;
using Arawn.NetworkingCore.LagCompensation;
using Arawn.GameCreator2.Networking.Combat;

namespace Arawn.GameCreator2.Networking.Shooter
{
    public partial class NetworkShooterController
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // RELOAD NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to start reloading the current weapon.
        /// </summary>
        /// <returns>True if request was sent, false if invalid state.</returns>
        public bool RequestReload()
        {
            LogDiagnostics($"[ShooterAmmoDebug] local reload requested {BuildAmmoDebug()}");

            if (m_IsServer && !m_IsLocalClient)
            {
                LogDiagnostics("reload request ignored on server instance");
                return false;
            }

            if (m_CurrentWeapon == null)
            {
                LogDiagnosticsWarning("reload request ignored: no current shooter weapon");
                return false;
            }

            if (m_ShooterStance == null)
            {
                LogDiagnosticsWarning("reload request ignored: no ShooterStance");
                return false;
            }
            
            // Don't request if already reloading
            if (m_ShooterStance.Reloading.IsReloading)
            {
                LogDiagnostics("reload request ignored: already reloading");
                return false;
            }
            
            // Don't request if jammed
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.IsJammed)
            {
                LogDiagnostics("reload request ignored: weapon is jammed");
                return false;
            }
            
            uint networkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            
            var request = new NetworkReloadRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = networkId,
                WeaponHash = m_CurrentWeapon.Id.Hash,
                ClientTimestamp = GetNetworkTime()
            };

            ulong pendingKey = GetPendingKey(request.ActorNetworkId, request.CorrelationId);
            if (pendingKey == 0)
            {
                Debug.LogWarning("[NetworkShooterController] Ignoring reload request with invalid actor/correlation context.");
                return false;
            }

            m_PendingReloads[pendingKey] = new PendingReloadRequest
            {
                Request = request,
                SentTime = Time.time
            };

            if (OnReloadRequestSent == null)
            {
                LogDiagnosticsWarning(
                    $"reload request has no listeners req={request.RequestId} actor={request.ActorNetworkId} " +
                    $"weaponHash={request.WeaponHash}. NetworkShooterManager/transport bridge is not routing reloads.");
            }
            else
            {
                LogDiagnostics(
                    $"reload request queued req={request.RequestId} actor={request.ActorNetworkId} " +
                    $"weaponHash={request.WeaponHash} {BuildAmmoDebug(m_CurrentWeapon)}");
            }

            OnReloadRequestSent?.Invoke(request);
            return true;
        }
        
        /// <summary>
        /// [Client] Request quick reload (active reload mechanic).
        /// </summary>
        /// <param name="normalizedTime">Current reload progress (0-1).</param>
        /// <returns>True if request was sent.</returns>
        public bool RequestQuickReload(float normalizedTime)
        {
            if (m_IsServer) return false;
            if (m_CurrentWeapon == null) return false;
            if (!m_ShooterStance.Reloading.IsReloading) return false;
            
            uint networkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            
            var request = new NetworkQuickReloadRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = networkId,
                WeaponHash = m_CurrentWeapon.Id.Hash,
                AttemptTime = normalizedTime
            };
            
            // Quick reload is sent immediately, no pending tracking needed
            // The response will be in the reload broadcast
            return true;
        }
        
        /// <summary>
        /// [Server] Process a reload request from a client.
        /// </summary>
        public NetworkReloadResponse ProcessReloadRequest(NetworkReloadRequest request, uint clientNetworkId)
        {
            LogDiagnostics(
                $"[ShooterAmmoDebug] server reload processing client={clientNetworkId} req={request.RequestId} " +
                $"weaponHash={request.WeaponHash} {BuildAmmoDebug()}");

            if (!m_IsServer)
            {
                LogDiagnosticsWarning(
                    $"[ShooterAmmoDebug] server reload rejected: controller is not server req={request.RequestId}");
                return new NetworkReloadResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ReloadRejectionReason.InvalidState
                };
            }
            
            // Validate weapon is equipped
            if (m_CurrentWeapon == null || m_CurrentWeapon.Id.Hash != request.WeaponHash)
            {
                LogDiagnosticsWarning(
                    $"[ShooterAmmoDebug] server reload rejected: weapon mismatch req={request.RequestId} " +
                    $"requested={request.WeaponHash} {BuildAmmoDebug()}");
                return new NetworkReloadResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ReloadRejectionReason.WeaponNotEquipped
                };
            }
            
            // Host-owned players start their local reload optimistically before the
            // loopback request is validated on the same controller instance.
            bool hostLocalReloadAlreadyStarted = m_IsLocalClient &&
                m_ShooterStance.Reloading.IsReloading &&
                m_ShooterStance.Reloading.WeaponReloading == m_CurrentWeapon;

            LogDiagnostics(
                $"[ShooterAmmoDebug] server reload state req={request.RequestId} " +
                $"hostLocalAlreadyStarted={hostLocalReloadAlreadyStarted} {BuildAmmoDebug(m_CurrentWeapon)}");

            if (m_ShooterStance.Reloading.IsReloading && !hostLocalReloadAlreadyStarted)
            {
                LogDiagnosticsWarning(
                    $"[ShooterAmmoDebug] server reload rejected: already reloading req={request.RequestId} " +
                    $"{BuildAmmoDebug(m_CurrentWeapon)}");
                return new NetworkReloadResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ReloadRejectionReason.AlreadyReloading
                };
            }
            
            // Check if jammed
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.IsJammed)
            {
                LogDiagnosticsWarning(
                    $"[ShooterAmmoDebug] server reload rejected: jammed req={request.RequestId} " +
                    $"{BuildAmmoDebug(m_CurrentWeapon)}");
                return new NetworkReloadResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ReloadRejectionReason.WeaponJammed
                };
            }
            
            // Check magazine not full
            var munition = m_Character.Combat.RequestMunition(m_CurrentWeapon) as ShooterMunition;
            if (munition != null)
            {
                int magazineSize = m_CurrentWeapon.Magazine.GetMagazineSize(m_CurrentWeaponData.WeaponArgs);
                if (munition.InMagazine >= magazineSize)
                {
                    LogDiagnosticsWarning(
                        $"[ShooterAmmoDebug] server reload rejected: magazine full req={request.RequestId} " +
                        $"inMagazine={munition.InMagazine} magazineSize={magazineSize} {BuildAmmoDebug(m_CurrentWeapon)}");
                    return new NetworkReloadResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = ReloadRejectionReason.MagazineFull
                    };
                }
            }
            
            // Get quick reload window from the reload asset
            byte quickStart = 0;
            byte quickEnd = 0;
            bool isFullReload = false;
            if (munition != null)
            {
                int maxMagazine = m_CurrentWeapon.Magazine.GetMagazineSize(m_CurrentWeaponData.WeaponArgs);
                isFullReload = munition.InMagazine >= maxMagazine;
            }

            var reload = m_CurrentWeapon.GetReload(m_Character, isFullReload);
            if (reload != null)
            {
                Vector2 quickWindow = reload.GetQuickReload();
                quickStart = (byte)(quickWindow.x * 255f);
                quickEnd = (byte)(quickWindow.y * 255f);
            }

            if (!hostLocalReloadAlreadyStarted)
            {
                LogDiagnostics(
                    $"[ShooterAmmoDebug] server starting GC2 reload req={request.RequestId} " +
                    $"{BuildAmmoDebug(m_CurrentWeapon)}");
                _ = m_ShooterStance.Reloading.Reload(m_CurrentWeapon);
            }
            else
            {
                LogDiagnostics(
                    $"[ShooterAmmoDebug] server reload already started locally req={request.RequestId} " +
                    $"{BuildAmmoDebug(m_CurrentWeapon)}");
            }
            
            return new NetworkReloadResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = ReloadRejectionReason.None,
                QuickReloadWindowStart = quickStart,
                QuickReloadWindowEnd = quickEnd
            };
        }
        
        /// <summary>
        /// [Server] Process a quick reload request from a client.
        /// </summary>
        public bool ProcessQuickReloadRequest(NetworkQuickReloadRequest request, uint clientNetworkId)
        {
            if (!m_IsServer) return false;
            if (m_CurrentWeapon == null) return false;
            if (!m_ShooterStance.Reloading.IsReloading) return false;

            Vector2 quickWindow = m_ShooterStance.Reloading.QuickReloadRange;
            float normalizedAttempt = Mathf.Clamp01(request.AttemptTime);
            return normalizedAttempt >= quickWindow.x && normalizedAttempt <= quickWindow.y;
        }
        
        /// <summary>
        /// [Client] Receive reload response from server.
        /// </summary>
        public void ReceiveReloadResponse(NetworkReloadResponse response)
        {
            LogDiagnostics(
                $"[ShooterAmmoDebug] reload response received req={response.RequestId} " +
                $"validated={response.Validated} reason={response.RejectionReason} {BuildAmmoDebug()}");

            if (!TryTakePending(m_PendingReloads, response.ActorNetworkId, response.CorrelationId, out _) && m_LogShots)
            {
                Debug.LogWarning($"[NetworkShooterController] Reload response dropped (stale/unknown): req={response.RequestId}, corr={response.CorrelationId}");
            }
            
            if (!response.Validated && m_LogShots)
            {
                Debug.Log($"[NetworkShooterController] Reload rejected: {response.RejectionReason}");
            }
        }
        
        /// <summary>
        /// [All] Receive reload broadcast from server.
        /// </summary>
        public void ReceiveReloadBroadcast(NetworkReloadBroadcast broadcast)
        {
            LogDiagnostics(
                $"[ShooterAmmoDebug] reload broadcast received event={broadcast.EventType} " +
                $"character={broadcast.CharacterNetworkId} weaponHash={broadcast.WeaponHash} " +
                $"newAmmo={broadcast.NewAmmoCount} local={m_IsLocalClient} {BuildAmmoDebug()}");

            OnReloadBroadcastReceived?.Invoke(broadcast);

            if (m_IsLocalClient)
            {
                LogDiagnostics(
                    $"[ShooterAmmoDebug] reload broadcast ignored on local owner event={broadcast.EventType} " +
                    $"newAmmo={broadcast.NewAmmoCount} {BuildAmmoDebug()}");
                return;
            }

            ShooterWeapon weapon = NetworkShooterManager.GetShooterWeaponByHash(broadcast.WeaponHash);
            if (weapon == null)
            {
                LogDiagnosticsWarning($"reload broadcast ignored: weapon hash {broadcast.WeaponHash} is not registered");
                return;
            }

            TryGetShooterStance();
            if (m_ShooterStance == null)
            {
                LogDiagnosticsWarning($"reload broadcast ignored: no ShooterStance for weapon {weapon.name}");
                return;
            }

            m_CurrentWeapon = weapon;
            m_CurrentWeaponData = m_ShooterStance.Get(weapon);

            switch (broadcast.EventType)
            {
                case ReloadEventType.Started:
                    PlayRemoteReload(weapon);
                    break;

                case ReloadEventType.Cancelled:
                    StopRemoteReload(weapon, CancelReason.ForceStop);
                    break;

                case ReloadEventType.QuickReloadSuccess:
                    StopRemoteReload(weapon, CancelReason.QuickReload);
                    ApplyRemoteReloadAmmo(broadcast);
                    break;

                case ReloadEventType.QuickReloadFailed:
                    StopRemoteReload(weapon, CancelReason.ForceStop);
                    ApplyRemoteReloadAmmo(broadcast);
                    break;

                case ReloadEventType.PartialReload:
                    StopRemoteReload(weapon, CancelReason.PartialReload);
                    ApplyRemoteReloadAmmo(broadcast);
                    break;

                case ReloadEventType.Completed:
                    ApplyRemoteReloadAmmo(broadcast);
                    break;
            }
        }

        private void PlayRemoteReload(ShooterWeapon weapon)
        {
            if (weapon == null) return;

            TryGetShooterStance();
            if (m_ShooterStance == null)
            {
                LogDiagnosticsWarning($"remote reload skipped: no ShooterStance for {weapon.name}");
                return;
            }

            if (!m_Character.Combat.IsEquipped(weapon))
            {
                LogDiagnosticsWarning($"remote reload skipped: {weapon.name} is not equipped");
                return;
            }

            if (m_ShooterStance.Reloading.IsReloading &&
                m_ShooterStance.Reloading.WeaponReloading == weapon)
            {
                return;
            }

            LogDiagnostics($"playing remote reload weapon={weapon.name}");
            _ = m_ShooterStance.Reloading.Reload(weapon);
        }

        private void StopRemoteReload(ShooterWeapon weapon, CancelReason reason)
        {
            if (weapon == null) return;

            TryGetShooterStance();
            if (m_ShooterStance == null) return;
            if (!m_ShooterStance.Reloading.IsReloading) return;
            if (m_ShooterStance.Reloading.WeaponReloading != weapon) return;

            m_ShooterStance.Reloading.Stop(weapon, reason);
        }

        private void ApplyRemoteReloadAmmo(NetworkReloadBroadcast broadcast)
        {
            if (m_CurrentWeapon == null || m_Character == null) return;

            if (m_Character.Combat.RequestMunition(m_CurrentWeapon) is ShooterMunition munition)
            {
                int previousAmmo = munition.InMagazine;
                munition.InMagazine = broadcast.NewAmmoCount;
                LogDiagnostics(
                    $"[ShooterAmmoDebug] applied remote reload ammo event={broadcast.EventType} " +
                    $"previous={previousAmmo} new={munition.InMagazine} {BuildAmmoDebug(m_CurrentWeapon)}");
            }
        }

        internal bool IsReloadingWeapon(int weaponHash)
        {
            if (m_CurrentWeapon == null || m_CurrentWeapon.Id.Hash != weaponHash) return false;
            if (m_ShooterStance == null) return false;

            return m_ShooterStance.Reloading.IsReloading &&
                   m_ShooterStance.Reloading.WeaponReloading == m_CurrentWeapon;
        }

        internal bool TryGetMagazineAmmo(int weaponHash, out ushort ammo)
        {
            ammo = 0;
            if (m_CurrentWeapon == null || m_CurrentWeapon.Id.Hash != weaponHash)
            {
                LogDiagnosticsWarning(
                    $"[ShooterAmmoDebug] TryGetMagazineAmmo failed: weapon mismatch requested={weaponHash} " +
                    $"{BuildAmmoDebug()}");
                return false;
            }
            if (m_Character == null)
            {
                LogDiagnosticsWarning(
                    $"[ShooterAmmoDebug] TryGetMagazineAmmo failed: character missing requested={weaponHash}");
                return false;
            }

            TryRefreshCurrentWeaponData();
            Args args = GetShooterWeaponArgs(m_CurrentWeapon);
            if (!m_CurrentWeapon.Magazine.GetHasMagazine(args))
            {
                LogDiagnostics(
                    $"[ShooterAmmoDebug] TryGetMagazineAmmo weapon has no magazine weaponHash={weaponHash} " +
                    $"{BuildAmmoDebug(m_CurrentWeapon)}");
                return true;
            }

            if (m_Character.Combat.RequestMunition(m_CurrentWeapon) is not ShooterMunition munition)
            {
                LogDiagnosticsWarning(
                    $"[ShooterAmmoDebug] TryGetMagazineAmmo failed: no munition weaponHash={weaponHash} " +
                    $"{BuildAmmoDebug(m_CurrentWeapon)}");
                return false;
            }

            ammo = (ushort)Mathf.Clamp(munition.InMagazine, 0, ushort.MaxValue);
            LogDiagnostics(
                $"[ShooterAmmoDebug] TryGetMagazineAmmo weaponHash={weaponHash} ammo={ammo} " +
                $"{BuildAmmoDebug(m_CurrentWeapon)}");
            return true;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // JAM / FIX NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to fix a jammed weapon.
        /// </summary>
        /// <returns>True if request was sent.</returns>
        public bool RequestFixJam()
        {
            if (m_IsServer) return false;
            if (m_CurrentWeapon == null) return false;
            if (m_CurrentWeaponData == null || !m_CurrentWeaponData.IsJammed) return false;
            if (m_ShooterStance.Jamming.IsFixing) return false;
            
            uint networkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            
            var request = new NetworkFixJamRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = networkId,
                WeaponHash = m_CurrentWeapon.Id.Hash,
                ClientTimestamp = GetNetworkTime()
            };

            ulong pendingKey = GetPendingKey(request.ActorNetworkId, request.CorrelationId);
            if (pendingKey == 0)
            {
                Debug.LogWarning("[NetworkShooterController] Ignoring fix-jam request with invalid actor/correlation context.");
                return false;
            }

            m_PendingFixJams[pendingKey] = new PendingFixJamRequest
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnFixJamRequestSent?.Invoke(request);
            return true;
        }
        
        /// <summary>
        /// [Server] Process a fix jam request from a client.
        /// </summary>
        public NetworkFixJamResponse ProcessFixJamRequest(NetworkFixJamRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkFixJamResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = FixJamRejectionReason.InvalidState
                };
            }
            
            // Validate weapon is equipped
            if (m_CurrentWeapon == null || m_CurrentWeapon.Id.Hash != request.WeaponHash)
            {
                return new NetworkFixJamResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = FixJamRejectionReason.WeaponNotEquipped
                };
            }
            
            // Check weapon is actually jammed
            if (m_CurrentWeaponData == null || !m_CurrentWeaponData.IsJammed)
            {
                return new NetworkFixJamResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = FixJamRejectionReason.WeaponNotJammed
                };
            }
            
            // Check not already fixing
            if (m_ShooterStance.Jamming.IsFixing)
            {
                return new NetworkFixJamResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = FixJamRejectionReason.AlreadyFixing
                };
            }
            
            return new NetworkFixJamResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = FixJamRejectionReason.None
            };
        }
        
        /// <summary>
        /// [Client] Receive fix jam response from server.
        /// </summary>
        public void ReceiveFixJamResponse(NetworkFixJamResponse response)
        {
            if (!TryTakePending(m_PendingFixJams, response.ActorNetworkId, response.CorrelationId, out _) && m_LogShots)
            {
                Debug.LogWarning($"[NetworkShooterController] Fix-jam response dropped (stale/unknown): req={response.RequestId}, corr={response.CorrelationId}");
            }
            
            if (!response.Validated && m_LogShots)
            {
                Debug.Log($"[NetworkShooterController] Fix jam rejected: {response.RejectionReason}");
            }
        }
        
        /// <summary>
        /// [All] Receive jam broadcast from server.
        /// </summary>
        public void ReceiveJamBroadcast(NetworkJamBroadcast broadcast)
        {
            OnWeaponJammed?.Invoke(broadcast);
            
            // Apply jam state on remote clients
            if (m_IsRemoteClient && m_CurrentWeaponData != null)
            {
                m_CurrentWeaponData.IsJammed = true;
            }
        }
        
        /// <summary>
        /// [All] Receive fix jam broadcast from server.
        /// </summary>
        public void ReceiveFixJamBroadcast(NetworkFixJamBroadcast broadcast)
        {
            OnJamFixed?.Invoke(broadcast);
            
            // Apply fix state on remote clients
            if (m_IsRemoteClient && m_CurrentWeaponData != null && broadcast.Success)
            {
                m_CurrentWeaponData.IsJammed = false;
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CHARGE NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to start charging the weapon.
        /// </summary>
        /// <returns>True if request was sent.</returns>
        public bool RequestChargeStart()
        {
            if (m_IsServer) return false;
            if (m_CurrentWeapon == null) return false;
            if (m_CurrentWeapon.Fire.Mode != ShootMode.Charge) return false;
            if (m_IsCharging) return false;
            
            // Check basic requirements
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.IsJammed) return false;
            if (m_ShooterStance.Reloading.IsReloading) return false;
            
            var munition = m_Character.Combat.RequestMunition(m_CurrentWeapon) as ShooterMunition;
            if (munition != null && munition.InMagazine <= 0) return false;
            
            uint networkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            
            var request = new NetworkChargeStartRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = networkId,
                WeaponHash = m_CurrentWeapon.Id.Hash,
                ClientTimestamp = GetNetworkTime()
            };

            ulong pendingKey = GetPendingKey(request.ActorNetworkId, request.CorrelationId);
            if (pendingKey == 0)
            {
                Debug.LogWarning("[NetworkShooterController] Ignoring charge request with invalid actor/correlation context.");
                return false;
            }

            m_PendingCharges[pendingKey] = new PendingChargeRequest
            {
                Request = request,
                SentTime = Time.time
            };
            
            // Start charging optimistically
            m_IsCharging = true;
            m_ChargeStartTime = Time.time;
            
            return true;
        }
        
        /// <summary>
        /// [Client] Request to cancel charging.
        /// </summary>
        /// <returns>True if request was sent.</returns>
        public bool RequestChargeCancel()
        {
            if (m_IsServer) return false;
            if (!m_IsCharging) return false;
            
            uint networkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            
            var request = new NetworkChargeCancelRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = networkId,
                WeaponHash = m_CurrentWeapon?.Id.Hash ?? 0,
                ClientTimestamp = GetNetworkTime()
            };
            
            m_IsCharging = false;
            
            // Request sent through manager
            return true;
        }
        
        /// <summary>
        /// [Server] Process a charge start request from a client.
        /// </summary>
        public NetworkChargeStartResponse ProcessChargeStartRequest(NetworkChargeStartRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.InvalidState
                };
            }
            
            // Validate weapon is equipped
            if (m_CurrentWeapon == null || m_CurrentWeapon.Id.Hash != request.WeaponHash)
            {
                return new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.WeaponNotEquipped
                };
            }
            
            // Validate weapon is charge type
            if (m_CurrentWeapon.Fire.Mode != ShootMode.Charge)
            {
                return new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.WeaponNotChargeable
                };
            }
            
            // Check not jammed
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.IsJammed)
            {
                return new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.WeaponJammed
                };
            }
            
            // Check not reloading
            if (m_ShooterStance.Reloading.IsReloading)
            {
                return new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.Reloading
                };
            }
            
            // Check has ammo
            var munition = m_Character.Combat.RequestMunition(m_CurrentWeapon) as ShooterMunition;
            if (munition != null && munition.InMagazine <= 0)
            {
                return new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.NoAmmo
                };
            }
            
            // Mark as charging on server
            m_IsCharging = true;
            m_ChargeStartTime = Time.time;
            
            return new NetworkChargeStartResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = ChargeRejectionReason.None
            };
        }
        
        /// <summary>
        /// [Client] Receive charge start response from server.
        /// </summary>
        public void ReceiveChargeStartResponse(NetworkChargeStartResponse response)
        {
            if (!TryTakePending(m_PendingCharges, response.ActorNetworkId, response.CorrelationId, out _) && m_LogShots)
            {
                Debug.LogWarning($"[NetworkShooterController] Charge response dropped (stale/unknown): req={response.RequestId}, corr={response.CorrelationId}");
            }
            
            if (!response.Validated)
            {
                // Rollback optimistic charge
                m_IsCharging = false;
                
                if (m_LogShots)
                {
                    Debug.Log($"[NetworkShooterController] Charge rejected: {response.RejectionReason}");
                }
            }
        }
        
        /// <summary>
        /// [All] Receive charge broadcast from server.
        /// </summary>
        public void ReceiveChargeBroadcast(NetworkChargeBroadcast broadcast)
        {
            OnChargeBroadcastReceived?.Invoke(broadcast);
            
            // Update remote client charge state
            if (m_IsRemoteClient)
            {
                float chargeRatio = broadcast.ChargeRatio / 255f;
                
                switch (broadcast.EventType)
                {
                    case ChargeEventType.Started:
                        m_IsCharging = true;
                        m_ChargeStartTime = Time.time;
                        break;
                        
                    case ChargeEventType.Released:
                    case ChargeEventType.Cancelled:
                    case ChargeEventType.AutoReleased:
                        m_IsCharging = false;
                        break;
                }
            }
        }
        
        /// <summary>
        /// Get current charge ratio (0-1).
        /// </summary>
        public float GetChargeRatio()
        {
            if (!m_IsCharging || m_CurrentWeapon == null) return 0f;
            if (m_CurrentWeaponData == null) return 0f;
            
            float maxChargeTime = m_CurrentWeapon.Fire.MaxChargeTime(m_CurrentWeaponData.WeaponArgs);
            if (maxChargeTime <= 0f) return 1f;
            
            float elapsed = Time.time - m_ChargeStartTime;
            return Mathf.Clamp01(elapsed / maxChargeTime);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SIGHT SWITCH NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to switch to a different sight.
        /// </summary>
        /// <param name="sightId">The IdString of the sight to switch to.</param>
        /// <returns>True if request was sent.</returns>
        public bool RequestSightSwitch(IdString sightId)
        {
            if (m_IsServer) return false;
            if (m_CurrentWeapon == null) return false;
            
            // Don't switch if already using this sight
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.SightId == sightId) return false;
            
            // Don't switch while reloading or shooting
            if (m_ShooterStance.Reloading.IsReloading) return false;
            if (m_ShooterStance.Shooting.IsShootingAnimation) return false;
            
            // Validate sight exists on weapon
            var sightItem = m_CurrentWeapon.Sights.Get(sightId);
            if (sightItem == null) return false;
            
            uint networkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            
            var request = new NetworkSightSwitchRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = networkId,
                WeaponHash = m_CurrentWeapon.Id.Hash,
                NewSightHash = sightId.Hash,
                ClientTimestamp = GetNetworkTime()
            };

            ulong pendingKey = GetPendingKey(request.ActorNetworkId, request.CorrelationId);
            if (pendingKey == 0)
            {
                Debug.LogWarning("[NetworkShooterController] Ignoring sight-switch request with invalid actor/correlation context.");
                return false;
            }

            m_PendingSightSwitches[pendingKey] = new PendingSightSwitchRequest
            {
                Request = request,
                SentTime = Time.time
            };
            
            return true;
        }
        
        /// <summary>
        /// [Server] Process a sight switch request from a client.
        /// </summary>
        public NetworkSightSwitchResponse ProcessSightSwitchRequest(NetworkSightSwitchRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.InvalidState
                };
            }
            
            // Validate weapon is equipped
            if (m_CurrentWeapon == null || m_CurrentWeapon.Id.Hash != request.WeaponHash)
            {
                return new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.WeaponNotEquipped
                };
            }
            
            // Check not reloading
            if (m_ShooterStance.Reloading.IsReloading)
            {
                return new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.Reloading
                };
            }
            
            // Check not shooting
            if (m_ShooterStance.Shooting.IsShootingAnimation)
            {
                return new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.Shooting
                };
            }
            
            // Validate sight exists on weapon
            // We need to find the sight by hash
            bool sightFound = TryResolveSightIdByHash(m_CurrentWeapon.Sights, request.NewSightHash, out _);
            
            if (!sightFound)
            {
                return new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.SightNotAvailable
                };
            }
            
            // Check not already using this sight
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.SightId.Hash == request.NewSightHash)
            {
                return new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.AlreadyUsingSight
                };
            }
            
            return new NetworkSightSwitchResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = SightSwitchRejectionReason.None
            };
        }
        
        /// <summary>
        /// [Client] Receive sight switch response from server.
        /// </summary>
        public void ReceiveSightSwitchResponse(NetworkSightSwitchResponse response)
        {
            if (!TryTakePending(m_PendingSightSwitches, response.ActorNetworkId, response.CorrelationId, out _) && m_LogShots)
            {
                Debug.LogWarning($"[NetworkShooterController] Sight-switch response dropped (stale/unknown): req={response.RequestId}, corr={response.CorrelationId}");
            }
            
            if (!response.Validated && m_LogShots)
            {
                Debug.Log($"[NetworkShooterController] Sight switch rejected: {response.RejectionReason}");
            }
        }
        
        /// <summary>
        /// [All] Receive sight switch broadcast from server.
        /// </summary>
        public void ReceiveSightSwitchBroadcast(NetworkSightSwitchBroadcast broadcast)
        {
            OnSightSwitchBroadcastReceived?.Invoke(broadcast);
            
            // Update remote client sight state
            if (m_IsRemoteClient && m_CurrentWeaponData != null)
            {
                ApplyRemoteSightHash(m_CurrentWeapon, broadcast.NewSightHash);
            }
        }
        
    }
}
#endif
