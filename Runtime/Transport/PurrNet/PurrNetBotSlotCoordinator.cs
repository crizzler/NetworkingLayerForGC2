using System;
using System.Collections.Generic;
using PurrNet;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Transports;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    public struct PurrNetBotSlotStatePacket : IPackedAuto
    {
        public uint slotIdHash;
        public byte occupantType;
        public uint humanClientId;
        public uint actorNetworkId;
        public Vector3 position;
        public Quaternion rotation;
        public uint revision;
    }

    public struct PurrNetBotSlotSnapshotPacket : IPackedAuto
    {
        public uint snapshotRevision;
        public PurrNetBotSlotStatePacket[] states;
    }

    public struct PurrNetBotSlotSnapshotRequestPacket : IPackedAuto
    {
        public uint knownSnapshotRevision;
    }

    /// <summary>
    /// PurrNet server/host bot-slot adapter with reliable, server-authenticated membership
    /// snapshots for observers and late joiners.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Bot Slot Coordinator")]
    [DisallowMultipleComponent]
    public sealed class PurrNetBotSlotCoordinator : NetworkBotSlotCoordinator
    {
        [SerializeField] private NetworkManager m_NetworkManager;

        private NetworkManager m_HookedManager;
        private bool m_ServerSubscribed;
        private bool m_ClientSubscribed;
        private uint m_SnapshotRevision;
        private readonly Dictionary<PlayerID, float> m_LastSnapshotRequestTimes = new();

        private NetworkManager ActiveManager =>
            m_NetworkManager != null ? m_NetworkManager : NetworkManager.main;

        protected override bool IsTransportAuthority =>
            ActiveManager != null && ActiveManager.isServer;

        protected override void OnEnable()
        {
            base.OnEnable();
            HookManager();
        }

        protected override void Update()
        {
            HookManager();
            base.Update();
        }

        private void OnDisable()
        {
            UnhookManager();
        }

        protected override GameObject SpawnBotActor(
            GameObject prefab,
            Vector3 position,
            Quaternion rotation,
            uint slotIdHash)
        {
            NetworkManager manager = ActiveManager;
            if (manager == null || !manager.isServer || prefab == null) return null;

            GameObject actor = UnityProxy.Instantiate(
                prefab,
                position,
                rotation,
                gameObject.scene);
            if (actor == null) return null;

            NetworkIdentity identity = actor.GetComponent<NetworkIdentity>();
            NetworkCharacter character = actor.GetComponent<NetworkCharacter>();
            if (identity == null || character == null ||
                character.ActorType != NetworkCharacterActorType.NPC)
            {
                Debug.LogError(
                    $"[PurrNetBotSlotCoordinator] Bot prefab '{prefab.name}' requires a root " +
                    "NetworkIdentity and an explicitly classified NPC NetworkCharacter.",
                    prefab);
                UnityProxy.Destroy(actor);
                return null;
            }

            identity.RemoveOwnership();
            return actor;
        }

        protected override bool DespawnBotActor(GameObject actor)
        {
            if (actor == null || !IsTransportAuthority) return false;
            UnityProxy.Destroy(actor);
            return true;
        }

        protected override uint ResolveActorNetworkId(GameObject actor)
        {
            if (actor == null) return 0;
            NetworkCharacter networkCharacter = actor.GetComponent<NetworkCharacter>();
            if (networkCharacter != null && networkCharacter.NetworkId != 0)
            {
                return networkCharacter.NetworkId;
            }

            NetworkIdentity identity = actor.GetComponent<NetworkIdentity>();
            return identity != null && identity.isSpawned && identity.objectId < uint.MaxValue
                ? (uint)(identity.objectId + 1UL)
                : 0;
        }

        protected override GameObject ResolveActor(uint actorNetworkId)
        {
            Character character = NetworkTransportBridge.Active?.ResolveCharacter(actorNetworkId);
            return character != null ? character.gameObject : null;
        }

        protected override void PublishAuthoritativeSnapshot(
            IReadOnlyList<NetworkBotSlotState> states)
        {
            NetworkManager manager = ActiveManager;
            if (manager == null || !manager.isServer || states == null) return;

            var packets = new PurrNetBotSlotStatePacket[states.Count];
            for (int i = 0; i < states.Count; i++)
            {
                NetworkBotSlotState state = states[i];
                packets[i] = new PurrNetBotSlotStatePacket
                {
                    slotIdHash = state.SlotIdHash,
                    occupantType = (byte)state.OccupantType,
                    humanClientId = state.HumanClientId,
                    actorNetworkId = state.ActorNetworkId,
                    position = state.Position,
                    rotation = state.Rotation,
                    revision = state.Revision
                };
            }

            var snapshot = new PurrNetBotSlotSnapshotPacket
            {
                snapshotRevision = ++m_SnapshotRevision,
                states = packets
            };

            try
            {
                manager.SendToAll(snapshot, Channel.ReliableOrdered);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        private void HookManager()
        {
            NetworkManager manager = ActiveManager;
            if (ReferenceEquals(manager, m_HookedManager))
            {
                EnsureRunningSubscriptions(manager);
                return;
            }

            UnhookManager();
            m_HookedManager = manager;
            if (manager == null) return;

            manager.onNetworkStarted += OnNetworkStarted;
            manager.onNetworkShutdown += OnNetworkShutdown;
            manager.onPlayerLoadedScene += OnPlayerLoadedScene;
            EnsureRunningSubscriptions(manager);
        }

        private void UnhookManager()
        {
            if (m_HookedManager == null) return;
            UnsubscribeServer();
            UnsubscribeClient();
            m_HookedManager.onNetworkStarted -= OnNetworkStarted;
            m_HookedManager.onNetworkShutdown -= OnNetworkShutdown;
            m_HookedManager.onPlayerLoadedScene -= OnPlayerLoadedScene;
            m_HookedManager = null;
            m_LastSnapshotRequestTimes.Clear();
        }

        private void OnNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer) SubscribeServer(manager);
            else SubscribeClient(manager);
        }

        private void OnNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (asServer) UnsubscribeServer();
            else UnsubscribeClient();
        }

        private void OnPlayerLoadedScene(PlayerID player, SceneID scene, bool asServer)
        {
            if (asServer && gameObject.scene.IsValid())
            {
                PublishAuthoritativeSnapshot(CurrentStates);
            }
        }

        private void SubscribeClient(NetworkManager manager)
        {
            if (m_ClientSubscribed || manager == null || !manager.isClient) return;
            manager.Subscribe<PurrNetBotSlotSnapshotPacket>(HandleSnapshotClient, false);
            m_ClientSubscribed = true;

            // The coordinator can become client-active after the server's scene-loaded callback
            // already published its one-shot late-join snapshot. Request the current durable
            // membership explicitly so reconnects and PurrNet lifecycle ordering both converge.
            if (!manager.isServer)
            {
                manager.SendToServer(
                    new PurrNetBotSlotSnapshotRequestPacket
                    {
                        knownSnapshotRevision = m_SnapshotRevision
                    },
                    Channel.ReliableOrdered);
            }
        }

        private void SubscribeServer(NetworkManager manager)
        {
            if (m_ServerSubscribed || manager == null || !manager.isServer) return;
            manager.Subscribe<PurrNetBotSlotSnapshotRequestPacket>(
                HandleSnapshotRequestServer,
                true);
            m_ServerSubscribed = true;
        }

        private void UnsubscribeServer()
        {
            if (!m_ServerSubscribed || m_HookedManager == null) return;
            try
            {
                m_HookedManager.Unsubscribe<PurrNetBotSlotSnapshotRequestPacket>(
                    HandleSnapshotRequestServer,
                    true);
            }
            catch (Exception)
            {
                // PurrNet can tear down the broadcaster before MonoBehaviour disable.
            }
            m_ServerSubscribed = false;
        }

        private void UnsubscribeClient()
        {
            if (!m_ClientSubscribed || m_HookedManager == null) return;
            try
            {
                m_HookedManager.Unsubscribe<PurrNetBotSlotSnapshotPacket>(
                    HandleSnapshotClient,
                    false);
            }
            catch (Exception)
            {
                // PurrNet can tear down the broadcaster before MonoBehaviour disable.
            }
            m_ClientSubscribed = false;
        }

        private void EnsureRunningSubscriptions(NetworkManager manager)
        {
            if (manager == null) return;
            if (manager.isServer) SubscribeServer(manager);
            if (manager.isClient) SubscribeClient(manager);
        }

        private void HandleSnapshotRequestServer(
            PlayerID sender,
            PurrNetBotSlotSnapshotRequestPacket request,
            bool asServer)
        {
            if (!asServer || sender.isServer || !IsTransportAuthority) return;

            float now = Time.realtimeSinceStartup;
            if (m_LastSnapshotRequestTimes.TryGetValue(sender, out float lastRequest) &&
                now - lastRequest < 1f)
            {
                return;
            }

            m_LastSnapshotRequestTimes[sender] = now;
            if (request.knownSnapshotRevision < m_SnapshotRevision ||
                request.knownSnapshotRevision == 0)
            {
                PublishAuthoritativeSnapshot(CurrentStates);
            }
        }

        private void HandleSnapshotClient(
            PlayerID sender,
            PurrNetBotSlotSnapshotPacket snapshot,
            bool asServer)
        {
            if (asServer || !sender.isServer || snapshot.states == null) return;

            var states = new NetworkBotSlotState[snapshot.states.Length];
            for (int i = 0; i < snapshot.states.Length; i++)
            {
                PurrNetBotSlotStatePacket state = snapshot.states[i];
                states[i] = new NetworkBotSlotState
                {
                    SlotIdHash = state.slotIdHash,
                    OccupantType = (NetworkBotSlotOccupantType)state.occupantType,
                    HumanClientId = state.humanClientId,
                    ActorNetworkId = state.actorNetworkId,
                    Position = state.position,
                    Rotation = state.rotation,
                    Revision = state.revision
                };
            }

            ApplyReplicatedSnapshot(states);
            m_SnapshotRevision = Math.Max(m_SnapshotRevision, snapshot.snapshotRevision);
        }
    }
}
