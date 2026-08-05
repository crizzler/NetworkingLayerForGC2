using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Initializes the GC2 character role from the centralized Fusion authority model.
    /// State Authority never implies gameplay ownership in Shared mode.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Network Character Auto-Init")]
    [DefaultExecutionOrder(-200)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkCharacter))]
    [RequireComponent(typeof(FusionNetworkIdentity))]
    public sealed class FusionNetworkCharacterAuto : NetworkBehaviour
    {
        [Min(0f)]
        [SerializeField] private float m_GameplayReadyDelay = 0.35f;
        [Min(1f)]
        [SerializeField] private float m_GameplayReadyTimeout = 10f;
        [SerializeField] private bool m_LogReadinessDiagnostics = true;

        private NetworkCharacter m_Character;
        private FusionNetworkIdentity m_Identity;
        private FusionTransportBridge m_Bridge;
        private uint m_LastNetworkId;
        private PlayerRef m_LastOwner = PlayerRef.Invalid;
        private uint m_LastEpoch;
        private bool m_Initialized;
        private bool m_GameplayReadyPending;
        private float m_GameplayReadyAfter;
        private float m_GameplayReadyDeadline;
        private float m_NextReadinessCheck;
        private uint m_GameplayReadyEpoch;
        private string m_LastPendingParticipant = string.Empty;
        private float m_NextReadinessDiagnosticAt;

        private void Awake()
        {
            m_Character = GetComponent<NetworkCharacter>();
            m_Identity = GetComponent<FusionNetworkIdentity>();
        }

        public override void Spawned()
        {
            ResolveBridge();
            Subscribe();
            RefreshRole(true);
        }

        public override void Render()
        {
            ResolveBridge();
            if (m_Identity == null) return;

            uint epoch = m_Bridge != null ? m_Bridge.AuthorityEpoch : 0;
            if (!m_Initialized ||
                m_Identity.NetworkId != m_LastNetworkId ||
                m_Identity.LogicalOwner != m_LastOwner ||
                epoch != m_LastEpoch)
            {
                RefreshRole(true);
            }

            TryNotifyGameplayReady();
        }

        private void Update()
        {
            // Readiness is a control-plane timer, not visual presentation. Render normally
            // pumps it, but Fusion may skip Render for an object during startup, visibility,
            // or headless transitions. Unity Update provides a deterministic fallback.
            TryNotifyGameplayReady();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            Unsubscribe();
            if (m_Bridge != null && m_Character != null)
            {
                m_Bridge.UnregisterCharacter(m_Character);
            }

            m_Character?.ResetNetworkRole();
            m_Initialized = false;
            m_LastNetworkId = 0;
            m_LastOwner = PlayerRef.Invalid;
            m_LastEpoch = 0;
            m_GameplayReadyPending = false;
            m_GameplayReadyEpoch = 0;
            m_LastPendingParticipant = string.Empty;
            m_NextReadinessDiagnosticAt = 0f;
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        private void ResolveBridge()
        {
            if (m_Bridge != null || Runner == null) return;
            if (FusionTransportBridge.TryGetBoundBridge(Runner, out m_Bridge))
            {
                Subscribe();
            }
        }

        private void Subscribe()
        {
            if (m_Identity != null)
            {
                m_Identity.IdentityChanged -= OnIdentityChanged;
                m_Identity.IdentityChanged += OnIdentityChanged;
            }

            if (m_Bridge != null)
            {
                m_Bridge.AuthorityChanged -= OnAuthorityChanged;
                m_Bridge.AuthorityChanged += OnAuthorityChanged;
                m_Bridge.LocalSceneReady -= OnLocalSceneReady;
                m_Bridge.LocalSceneReady += OnLocalSceneReady;
            }
        }

        private void Unsubscribe()
        {
            if (m_Identity != null)
            {
                m_Identity.IdentityChanged -= OnIdentityChanged;
            }

            if (m_Bridge != null)
            {
                m_Bridge.AuthorityChanged -= OnAuthorityChanged;
                m_Bridge.LocalSceneReady -= OnLocalSceneReady;
            }
        }

        private void OnIdentityChanged(FusionNetworkIdentity identity)
        {
            if (m_Bridge != null && m_Bridge.AuthorityTransitionInProgress) return;
            RefreshRole(true);
        }

        private void OnAuthorityChanged(bool isAuthority, uint epoch)
        {
            // FusionTransportBridge refreshes every identity after all synchronous module
            // promotion and registry handlers have completed. Deferring here prevents a
            // locally-owned character from announcing GameplayReady against pre-promotion
            // module state.
            m_LastEpoch = 0;
            m_GameplayReadyPending = false;
        }

        private void OnLocalSceneReady()
        {
            // A DontDestroyOnLoad player can survive while all scene-local managers and
            // bridges are recreated. Re-arm readiness even when its ID and owner are stable.
            ArmGameplayReadiness();
        }

        private void RefreshRole(bool resetExisting)
        {
            if (m_Character == null || m_Identity == null || Runner == null || !Runner.IsRunning)
            {
                return;
            }

            ResolveBridge();
            if (m_Bridge == null) return;

            if (!m_Identity.TransportAdmitted)
            {
                m_GameplayReadyPending = false;
                if (m_Initialized)
                {
                    m_Bridge.UnregisterCharacter(m_Character);
                    m_Character.ResetNetworkRole();
                    m_Initialized = false;
                }
                return;
            }

            uint networkId = m_Identity.NetworkId;
            if (networkId == 0) return;

            bool isOwner = m_Identity.IsOwnedBy(Runner.LocalPlayer);
            bool isServer = m_Bridge.IsServer;
            // A Shared master is still a graphical client peer. Treat it as host-like only for
            // character presentation so NetworkCharacter does not apply dedicated-server
            // optimizations (which disable every remote player's Renderer). Keep the bridge's
            // public IsHost topology meaning unchanged.
            bool isHost = m_Bridge.IsHost ||
                          (Runner.GameMode == GameMode.Shared &&
                           Runner.IsSharedModeMasterClient);

            if (resetExisting && m_Initialized)
            {
                m_Bridge.UnregisterCharacter(m_Character);
                m_Character.ResetNetworkRole();
            }

            m_Character.SetManualNetworkId(networkId);
            m_Character.InitializeNetworkRole(isServer, isOwner, isHost);
            m_Bridge.RegisterCharacter(m_Character);

            if (m_Identity.TryGetLogicalOwnerClientId(out uint ownerClientId))
            {
                m_Bridge.SetCharacterOwner(networkId, ownerClientId);
            }

            m_LastNetworkId = networkId;
            m_LastOwner = m_Identity.LogicalOwner;
            m_LastEpoch = m_Bridge.AuthorityEpoch;
            m_Initialized = true;

            if (isOwner && m_Bridge.IsClient) ArmGameplayReadiness();
            else m_GameplayReadyPending = false;
        }

        private void ArmGameplayReadiness()
        {
            if (!m_Initialized ||
                m_Bridge == null ||
                m_Identity == null ||
                Runner == null ||
                !m_Identity.TransportAdmitted ||
                !m_Identity.IsOwnedBy(Runner.LocalPlayer) ||
                !m_Bridge.IsClient)
            {
                return;
            }

            // Module bridges discover their per-character controllers during their first
            // runtime scans. Defer GameplayReady briefly so a full snapshot cannot race
            // that registration and be acknowledged after being dropped.
            m_GameplayReadyPending = true;
            m_GameplayReadyAfter =
                Time.unscaledTime + Mathf.Max(0f, m_GameplayReadyDelay);
            m_GameplayReadyDeadline =
                Time.unscaledTime + Mathf.Max(1f, m_GameplayReadyTimeout);
            m_NextReadinessCheck = m_GameplayReadyAfter;
            m_GameplayReadyEpoch = m_Bridge.AuthorityEpoch;
            m_LastPendingParticipant = string.Empty;
            m_NextReadinessDiagnosticAt = m_GameplayReadyAfter;
            LogReadiness(
                $"armed; character='{name}' networkId={m_Identity.NetworkId} " +
                $"epoch={m_GameplayReadyEpoch}");
        }

        private void TryNotifyGameplayReady()
        {
            if (!m_GameplayReadyPending ||
                Time.unscaledTime < m_GameplayReadyAfter ||
                m_Bridge == null ||
                m_Identity == null)
            {
                return;
            }

            if (m_Bridge.AuthorityTransitionInProgress)
            {
                return;
            }

            if (m_GameplayReadyEpoch != m_Bridge.AuthorityEpoch ||
                !m_Identity.TransportAdmitted ||
                Runner == null ||
                !m_Identity.IsOwnedBy(Runner.LocalPlayer) ||
                !m_Bridge.IsClient)
            {
                m_GameplayReadyPending = false;
                LogReadiness(
                    $"cancelled; character='{name}' armedEpoch={m_GameplayReadyEpoch} " +
                    $"currentEpoch={m_Bridge.AuthorityEpoch}");
                return;
            }

            if (Time.unscaledTime < m_NextReadinessCheck) return;
            m_NextReadinessCheck = Time.unscaledTime + 0.1f;

            if (!AreGameplayParticipantsReady(
                    out string pendingParticipant,
                    out Exception participantFailure))
            {
                if (participantFailure != null)
                {
                    m_GameplayReadyPending = false;
                    Debug.LogException(participantFailure, this);
                    m_Bridge.ShutdownSessionForAuthorityFailure(
                        $"Gameplay readiness participant '{pendingParticipant}' failed.");
                }
                else if (Time.unscaledTime >= m_GameplayReadyDeadline)
                {
                    m_GameplayReadyPending = false;
                    m_Bridge.ShutdownSessionForAuthorityFailure(
                        $"Gameplay readiness timed out waiting for '{pendingParticipant}'.");
                }
                else if (!string.Equals(
                             m_LastPendingParticipant,
                             pendingParticipant,
                             StringComparison.Ordinal) ||
                         Time.unscaledTime >= m_NextReadinessDiagnosticAt)
                {
                    m_LastPendingParticipant = pendingParticipant;
                    m_NextReadinessDiagnosticAt = Time.unscaledTime + 2f;
                    LogReadiness(
                        $"waiting for '{pendingParticipant}'; character='{name}' " +
                        $"networkId={m_Identity.NetworkId} epoch={m_GameplayReadyEpoch}");
                }
                return;
            }

            m_GameplayReadyPending = false;
            LogReadiness(
                $"ready; character='{name}' networkId={m_Identity.NetworkId} " +
                $"epoch={m_GameplayReadyEpoch}");
            m_Bridge.NotifyLocalGameplayReady();
        }

        private void LogReadiness(string message)
        {
            if (!m_LogReadinessDiagnostics) return;
            Debug.Log($"[FusionTransport][Readiness] {message}", this);
        }

        private bool AreGameplayParticipantsReady(
            out string pendingParticipant,
            out Exception failure)
        {
            pendingParticipant = "Fusion Core bridge";
            failure = null;
            int participantCount = 0;
            bool hasCore = false;
            bool hasVariables = false;
            bool hasAnimationMotion = false;
            var moduleIds = new HashSet<ushort>();

            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null ||
                    !behaviour.isActiveAndEnabled ||
                    behaviour is not IFusionGameplayReadinessParticipant participant)
                {
                    continue;
                }

                participantCount++;
                string participantName =
                    string.IsNullOrWhiteSpace(participant.GameplayReadinessName)
                        ? behaviour.GetType().Name
                        : participant.GameplayReadinessName;

                if (participant.GameplayReadinessTransport != m_Bridge ||
                    m_Bridge.Runner != Runner)
                {
                    pendingParticipant = participantName + " bound to this runner";
                    return false;
                }

                ushort moduleId = participant.GameplayReadinessModuleId;
                if (!moduleIds.Add(moduleId))
                {
                    pendingParticipant = participantName;
                    failure = new InvalidOperationException(
                        $"More than one active Fusion readiness participant uses module ID " +
                        $"{moduleId}.");
                    return false;
                }

                hasCore |= moduleId == FusionModuleIds.Core;
                hasVariables |= moduleId == FusionModuleIds.Variables;
                hasAnimationMotion |= moduleId == FusionModuleIds.AnimationMotion;
                try
                {
                    if (participant.IsGameplayReady(m_Identity)) continue;
                    pendingParticipant = participantName;
                    return false;
                }
                catch (Exception exception)
                {
                    pendingParticipant = participantName;
                    failure = exception;
                    return false;
                }
            }

            if (participantCount == 0)
            {
                pendingParticipant = "the mandatory Fusion module bridges";
                return false;
            }
            if (!hasCore)
            {
                pendingParticipant = "Core";
                return false;
            }
            if (!hasVariables)
            {
                pendingParticipant = "Variables";
                return false;
            }
            if (!hasAnimationMotion)
            {
                pendingParticipant = "Animation/Motion";
                return false;
            }
            return true;
        }
    }
}
