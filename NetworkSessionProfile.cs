using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    public enum NetworkSessionPreset
    {
        Custom = 0,
        Duel = 1,
        Standard = 2,
        Massive = 3
    }

    public enum NetworkRelevanceTier
    {
        Near = 0,
        Mid = 1,
        Far = 2
    }

    [Serializable]
    public struct NetworkRelevanceSettings
    {
        [Range(1, 60)] public int stateApplyRate;
        [Range(0.02f, 0.35f)] public float interpolationDelay;
        [Range(0.05f, 1f)] public float maxExtrapolationTime;
        [Range(1f, 20f)] public float snapDistance;

        public bool syncIK;
        public bool syncAnimation;
        public bool syncCore;
        public bool syncCombat;

        [Range(1f, 30f)] public float animationStateRate;
        [Range(1f, 30f)] public float animationGestureRate;
    }

    [CreateAssetMenu(
        fileName = "NetworkSessionProfile",
        menuName = "Game Creator/Network/Session Profile",
        order = 510
    )]
    public class NetworkSessionProfile : ScriptableObject
    {
        [NonSerialized] private bool m_RuntimeTierOrderingValidated;

        [Header("Preset")]
        [SerializeField] private NetworkSessionPreset m_Preset = NetworkSessionPreset.Standard;
        [SerializeField] private bool m_AutoApplyPreset = true;

        [Header("Simulation")]
        [Range(10, 120)] public int serverSimulationRate = 30;
        [Range(5, 60)] public int serverStateBroadcastRate = 20;
        [Range(1f, 20f)] public float relevanceUpdateRate = 4f;

        [Header("Input and Reconciliation")]
        [Range(10, 60)] public int inputSendRate = 30;
        [Range(1, 5)] public int inputRedundancy = 3;
        [Range(0.01f, 1f)] public float reconciliationThreshold = 0.1f;
        [Range(1f, 10f)] public float maxReconciliationDistance = 3f;
        [Range(5f, 30f)] public float reconciliationSpeed = 15f;

        [Header("Anti-Cheat")]
        [Range(1.1f, 2f)] public float maxSpeedMultiplier = 1.2f;
        [Range(3, 20)] public int violationThreshold = 5;

        [Header("Relevance Distances")]
        [Min(1f)] public float nearDistance = 20f;
        [Min(2f)] public float midDistance = 50f;

        [Header("Fan-Out Filtering")]
        [Tooltip("If true, clients without a resolvable representative character won't receive character state updates.")]
        public bool requireObserverCharacterForRelevance = false;

        [Tooltip("If enabled, observers beyond cullDistance stop receiving regular state updates for this character.")]
        public bool enableDistanceCulling = false;

        [Min(1f)]
        [Tooltip("Distance in meters beyond which this character's state is culled for an observer (when enableDistanceCulling is true).")]
        public float cullDistance = 120f;

        [Range(0f, 10f)]
        [Tooltip("Optional low-frequency keepalive send rate (Hz) while culled. Set 0 to fully suppress updates beyond cull distance.")]
        public float culledKeepAliveRate = 0f;

        [Header("Near Tier")]
        public NetworkRelevanceSettings near = new NetworkRelevanceSettings
        {
            stateApplyRate = 30,
            interpolationDelay = 0.08f,
            maxExtrapolationTime = 0.25f,
            snapDistance = 5f,
            syncIK = true,
            syncAnimation = true,
            syncCore = true,
            syncCombat = true,
            animationStateRate = 10f,
            animationGestureRate = 20f
        };

        [Header("Mid Tier")]
        public NetworkRelevanceSettings mid = new NetworkRelevanceSettings
        {
            stateApplyRate = 20,
            interpolationDelay = 0.1f,
            maxExtrapolationTime = 0.35f,
            snapDistance = 6f,
            syncIK = false,
            syncAnimation = true,
            syncCore = true,
            syncCombat = true,
            animationStateRate = 8f,
            animationGestureRate = 12f
        };

        [Header("Far Tier")]
        public NetworkRelevanceSettings far = new NetworkRelevanceSettings
        {
            stateApplyRate = 10,
            interpolationDelay = 0.15f,
            maxExtrapolationTime = 0.5f,
            snapDistance = 8f,
            syncIK = false,
            syncAnimation = false,
            syncCore = false,
            syncCombat = false,
            animationStateRate = 4f,
            animationGestureRate = 6f
        };

        public NetworkSessionPreset Preset => m_Preset;

        public NetworkRelevanceTier GetTier(float distance)
        {
            EnsureTierOrdering(logWarnings: Application.isPlaying);
            if (distance <= nearDistance) return NetworkRelevanceTier.Near;
            if (distance <= midDistance) return NetworkRelevanceTier.Mid;
            return NetworkRelevanceTier.Far;
        }

        public NetworkRelevanceSettings GetTierSettings(NetworkRelevanceTier tier)
        {
            EnsureTierOrdering(logWarnings: Application.isPlaying);
            return tier switch
            {
                NetworkRelevanceTier.Near => near,
                NetworkRelevanceTier.Mid => mid,
                _ => far
            };
        }

        public void ApplyPreset(NetworkSessionPreset preset)
        {
            m_Preset = preset;
            m_RuntimeTierOrderingValidated = false;

            switch (preset)
            {
                case NetworkSessionPreset.Duel:
                    serverSimulationRate = 60;
                    serverStateBroadcastRate = 30;
                    relevanceUpdateRate = 8f;
                    inputSendRate = 60;
                    inputRedundancy = 3;
                    reconciliationThreshold = 0.08f;
                    maxReconciliationDistance = 3f;
                    reconciliationSpeed = 20f;
                    maxSpeedMultiplier = 1.15f;
                    violationThreshold = 4;
                    nearDistance = 25f;
                    midDistance = 60f;
                    requireObserverCharacterForRelevance = false;
                    enableDistanceCulling = false;
                    cullDistance = 140f;
                    culledKeepAliveRate = 0f;
                    near.syncIK = true;
                    near.syncAnimation = true;
                    near.syncCore = true;
                    near.syncCombat = true;
                    near.animationStateRate = 12f;
                    near.animationGestureRate = 24f;
                    mid.syncIK = true;
                    mid.syncAnimation = true;
                    mid.syncCore = true;
                    mid.syncCombat = true;
                    mid.animationStateRate = 10f;
                    mid.animationGestureRate = 18f;
                    far.syncIK = false;
                    far.syncAnimation = true;
                    far.syncCore = true;
                    far.syncCombat = true;
                    far.animationStateRate = 8f;
                    far.animationGestureRate = 10f;
                    break;

                case NetworkSessionPreset.Massive:
                    serverSimulationRate = 30;
                    serverStateBroadcastRate = 15;
                    relevanceUpdateRate = 3f;
                    inputSendRate = 20;
                    inputRedundancy = 2;
                    reconciliationThreshold = 0.12f;
                    maxReconciliationDistance = 4f;
                    reconciliationSpeed = 12f;
                    maxSpeedMultiplier = 1.25f;
                    violationThreshold = 6;
                    nearDistance = 18f;
                    midDistance = 40f;
                    requireObserverCharacterForRelevance = true;
                    enableDistanceCulling = true;
                    cullDistance = 90f;
                    culledKeepAliveRate = 1f;
                    near.stateApplyRate = 20;
                    near.syncIK = false;
                    near.syncAnimation = true;
                    near.syncCore = true;
                    near.syncCombat = true;
                    near.animationStateRate = 8f;
                    near.animationGestureRate = 10f;
                    mid.stateApplyRate = 12;
                    mid.syncIK = false;
                    mid.syncAnimation = true;
                    mid.syncCore = false;
                    mid.syncCombat = true;
                    mid.animationStateRate = 5f;
                    mid.animationGestureRate = 7f;
                    far.stateApplyRate = 6;
                    far.syncIK = false;
                    far.syncAnimation = false;
                    far.syncCore = false;
                    far.syncCombat = false;
                    far.animationStateRate = 3f;
                    far.animationGestureRate = 4f;
                    break;

                case NetworkSessionPreset.Standard:
                    serverSimulationRate = 30;
                    serverStateBroadcastRate = 20;
                    relevanceUpdateRate = 4f;
                    inputSendRate = 30;
                    inputRedundancy = 3;
                    reconciliationThreshold = 0.1f;
                    maxReconciliationDistance = 3f;
                    reconciliationSpeed = 15f;
                    maxSpeedMultiplier = 1.2f;
                    violationThreshold = 5;
                    nearDistance = 20f;
                    midDistance = 50f;
                    requireObserverCharacterForRelevance = false;
                    enableDistanceCulling = false;
                    cullDistance = 120f;
                    culledKeepAliveRate = 0f;
                    near.stateApplyRate = 30;
                    near.syncIK = true;
                    near.syncAnimation = true;
                    near.syncCore = true;
                    near.syncCombat = true;
                    near.animationStateRate = 10f;
                    near.animationGestureRate = 20f;
                    mid.stateApplyRate = 20;
                    mid.syncIK = false;
                    mid.syncAnimation = true;
                    mid.syncCore = true;
                    mid.syncCombat = true;
                    mid.animationStateRate = 8f;
                    mid.animationGestureRate = 12f;
                    far.stateApplyRate = 10;
                    far.syncIK = false;
                    far.syncAnimation = false;
                    far.syncCore = false;
                    far.syncCombat = false;
                    far.animationStateRate = 4f;
                    far.animationGestureRate = 6f;
                    break;

                case NetworkSessionPreset.Custom:
                default:
                    break;
            }

            EnsureTierOrdering(logWarnings: false);
        }

        private void OnValidate()
        {
            if (m_AutoApplyPreset && m_Preset != NetworkSessionPreset.Custom)
            {
                ApplyPreset(m_Preset);
            }

            EnsureTierOrdering(logWarnings: false);
            m_RuntimeTierOrderingValidated = false;
        }

        private void EnsureTierOrdering(bool logWarnings)
        {
            if (m_RuntimeTierOrderingValidated && Application.isPlaying) return;

            float minMidDistance = nearDistance + 1f;
            if (midDistance < minMidDistance)
            {
                if (logWarnings)
                {
                    Debug.LogWarning(
                        $"[NetworkSessionProfile] Tier distances were invalid in '{name}'. " +
                        $"Clamping midDistance from {midDistance:F2} to {minMidDistance:F2}.");
                }

                midDistance = minMidDistance;
            }

            float minCullDistance = midDistance + 1f;
            if (cullDistance < minCullDistance)
            {
                if (logWarnings)
                {
                    Debug.LogWarning(
                        $"[NetworkSessionProfile] Cull distance was invalid in '{name}'. " +
                        $"Clamping cullDistance from {cullDistance:F2} to {minCullDistance:F2}.");
                }

                cullDistance = minCullDistance;
            }

            if (Application.isPlaying)
            {
                m_RuntimeTierOrderingValidated = true;
            }
        }
    }
}
