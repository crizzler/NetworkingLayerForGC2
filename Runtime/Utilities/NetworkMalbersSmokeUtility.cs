using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Reflection-only helpers for the optional Malbers multi-peer smoke bootstrap. The main
    /// package intentionally keeps no compile-time Malbers reference.
    /// </summary>
    public static class NetworkMalbersSmokeUtility
    {
        private static readonly List<NetworkCharacter> s_Characters = new(64);

        public static bool TryGetServerNpcState(
            out NetworkCharacter character,
            out int actorNetworkId,
            out int stateId)
        {            character = null;
            actorNetworkId = 0;
            stateId = 0;

            NetworkTransportBridge bridge = ResolveRunningBridge();
            if (bridge == null || !bridge.IsRunning) return false;

            bridge.CopyRegisteredCharacters(s_Characters);
            int bestStateId = 0;
            for (int i = 0; i < s_Characters.Count; i++)
            {
                NetworkCharacter candidate = s_Characters[i];
                if (candidate == null || !candidate.IsServerAuthoritativeNPC) continue;
                if (!TryGetState(candidate, out int candidateState)) continue;

                int candidateId = unchecked((int)candidate.NetworkId);
                if (character != null && candidateId >= actorNetworkId) continue;

                character = candidate;
                actorNetworkId = candidateId;
                bestStateId = candidateState;
            }

            stateId = bestStateId;
            return character != null;
        }

        /// <summary>
        /// Describes every registered character so a failed <see cref="TryGetServerNpcState"/> in
        /// a multi-process run can be diagnosed from the player log alone.
        /// </summary>
        public static string DescribeCharacters()
        {
            NetworkTransportBridge bridge = ResolveRunningBridge();
            if (bridge == null) return "bridge=none";

            NetworkTransportBridge[] all = UnityObjectSearch.FindAll<NetworkTransportBridge>(
                FindObjectsInactive.Exclude);
            int running = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].IsRunning) running++;
            }

            if (!bridge.IsRunning)
            {
                var idle = new System.Text.StringBuilder();
                idle.Append("bridge=idle active=")
                    .Append(NetworkTransportBridge.Active != null
                        ? NetworkTransportBridge.Active.name
                        : "none")
                    .Append(" bridges=").Append(all.Length)
                    .Append(" running=").Append(running);
                return idle.ToString();
            }

            bridge.CopyRegisteredCharacters(s_Characters);
            var builder = new System.Text.StringBuilder();
            builder.Append("bridge=").Append(bridge.name)
                .Append(" running=").Append(running)
                .Append(" registered=").Append(s_Characters.Count);
            for (int i = 0; i < s_Characters.Count; i++)
            {
                NetworkCharacter candidate = s_Characters[i];
                builder.Append(" | ");
                if (candidate == null)
                {
                    builder.Append("null");
                    continue;
                }

                builder.Append(candidate.name)
                    .Append(" id=").Append(candidate.NetworkId)
                    .Append(" type=").Append(candidate.EffectiveActorType)
                    .Append(" npcMode=").Append(candidate.NPCMode)
                    .Append(" authoritativeNpc=").Append(candidate.IsServerAuthoritativeNPC)
                    .Append(" animal=").Append(GetAnimal(candidate) != null)
                    .Append(" state=").Append(TryGetState(candidate, out int state) ? state : -1);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Reads the proxy-side application diagnostics from the optional Malbers state bridge.
        /// This proves a snapshot was applied to a specific actor instead of only arriving.
        /// </summary>
        /// <param name="requiredActorId">
        /// When non-zero, only the bridge that applied snapshots for this character id is
        /// considered, so several replicated NPCs cannot alternate as the reported actor.
        /// </param>
        public static bool TryGetAppliedSnapshot(
            int requiredActorId,
            out int actorNetworkId,
            out int stateId,
            out int appliedCount)
        {
            actorNetworkId = 0;
            stateId = 0;
            appliedCount = 0;

            Type bridgeType = NetworkMalbersIntegrationPolicy.FindStateBridgeType();
            if (bridgeType == null) return false;

            MonoBehaviour[] behaviours = UnityObjectSearch.FindAll<MonoBehaviour>(
                FindObjectsInactive.Include);
            PropertyInfo countProperty = bridgeType.GetProperty(
                "AppliedSnapshotCount",
                BindingFlags.Instance | BindingFlags.Public);
            PropertyInfo actorProperty = bridgeType.GetProperty(
                "LastAppliedCharacterId",
                BindingFlags.Instance | BindingFlags.Public);
            PropertyInfo stateProperty = bridgeType.GetProperty(
                "LastAppliedStateId",
                BindingFlags.Instance | BindingFlags.Public);
            if (countProperty == null || actorProperty == null || stateProperty == null)
                return false;

            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null || !bridgeType.IsInstanceOfType(behaviour)) continue;

                int count = (int)(countProperty.GetValue(behaviour) ?? 0);
                if (count <= 0) continue;

                int candidateActor = (int)(actorProperty.GetValue(behaviour) ?? 0);
                if (requiredActorId != 0 && candidateActor != requiredActorId) continue;
                if (count <= appliedCount) continue;

                appliedCount = count;
                actorNetworkId = candidateActor;
                stateId = (int)(stateProperty.GetValue(behaviour) ?? 0);
            }

            return appliedCount > 0;
        }

        public static bool TryGetSnapshotCounters(
            out int lastSentStateId,
            out int sentCount,
            out int lastReceivedStateId,
            out int receivedCount)
        {
            lastSentStateId = 0;
            sentCount = 0;
            lastReceivedStateId = 0;
            receivedCount = 0;

            NetworkMalbersStateManager manager = NetworkMalbersStateManager.Instance;
            if (manager == null) return false;

            lastSentStateId = manager.LastSentStateId;
            sentCount = manager.SentSnapshotCount;
            lastReceivedStateId = manager.LastReceivedStateId;
            receivedCount = manager.ReceivedSnapshotCount;
            return true;
        }

        public static int CountAuthenticatedHumans()
        {
            NetworkTransportBridge bridge = ResolveRunningBridge();
            if (bridge == null || !bridge.IsRunning) return 0;

            bridge.CopyRegisteredCharacters(s_Characters);
            int humans = 0;
            for (int i = 0; i < s_Characters.Count; i++)
            {
                NetworkCharacter character = s_Characters[i];
                if (character != null &&
                    character.IsPlayerOwnedActor &&
                    character.HasAuthenticatedPlayerOwner)
                {
                    humans++;
                }
            }

            return humans;
        }

        public static bool TryGetState(NetworkCharacter character, out int stateId)
        {
            stateId = 0;
            Component animal = GetAnimal(character);
            if (animal == null) return false;

            object activeStateId = animal.GetType()
                .GetProperty("ActiveStateID", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(animal);
            if (activeStateId == null) return false;

            FieldInfo idField = activeStateId.GetType().GetField(
                "ID",
                BindingFlags.Instance | BindingFlags.Public);
            if (idField == null || idField.FieldType != typeof(int)) return false;

            stateId = (int)idField.GetValue(activeStateId);
            return true;
        }

        public static bool TryForceNextState(
            NetworkCharacter character,
            int currentStateId,
            out int targetStateId)
        {
            targetStateId = 0;
            Component animal = GetAnimal(character);
            if (animal == null) return false;

            Type animalType = animal.GetType();
            object states = animalType
                .GetField("states", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(animal);
            if (states is not IEnumerable enumerable) return false;

            MethodInfo force = animalType.GetMethod(
                "State_Force",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(int) },
                null);
            if (force == null) return false;

            foreach (object state in enumerable)
            {
                if (state == null) continue;
                object stateId = state.GetType()
                    .GetField("ID", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(state);
                if (stateId == null) continue;

                FieldInfo idField = stateId.GetType().GetField(
                    "ID",
                    BindingFlags.Instance | BindingFlags.Public);
                if (idField == null || idField.FieldType != typeof(int)) continue;

                int candidate = (int)idField.GetValue(stateId);
                if (candidate <= 0 || candidate == currentStateId) continue;

                force.Invoke(animal, new object[] { candidate });
                targetStateId = candidate;
                return true;
            }

            return false;
        }

        private static Component GetAnimal(NetworkCharacter character)
        {
            if (character == null) return null;
            Type animalType = NetworkMalbersIntegrationPolicy.FindMalbersAnimalType();
            return animalType != null ? character.GetComponent(animalType) as Component : null;
        }

        /// <summary>
        /// Returns the bridge that is actually running. The cached <c>Active</c> instance can be
        /// a dormant scene bridge while another bridge owns the live session.
        /// </summary>
        private static NetworkTransportBridge ResolveRunningBridge()
        {
            NetworkTransportBridge active = NetworkTransportBridge.Active;
            if (active != null && active.IsRunning) return active;

            NetworkTransportBridge[] bridges = UnityObjectSearch.FindAll<NetworkTransportBridge>(
                FindObjectsInactive.Exclude);
            for (int i = 0; i < bridges.Length; i++)
            {
                if (bridges[i] != null && bridges[i].IsRunning) return bridges[i];
            }

            return active;
        }
    }
}
