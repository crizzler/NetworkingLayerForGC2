using Fusion;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Selects who advances an optional Advanced KCC character in Fusion Shared Mode.
    /// This enum deliberately lives in the KCC-independent Fusion assembly so prefabs and
    /// editor tooling remain loadable when the optional addon is not installed.
    /// </summary>
    public enum FusionKccSharedAuthorityMode
    {
        /// <summary>
        /// The logical owner advances its own KCC. This follows Fusion Shared Mode's native
        /// per-object State Authority model and is the recommended KCC configuration.
        /// </summary>
        OwnerMovementAuthority = 0,

        /// <summary>
        /// The Shared master advances player movement while logical owners submit intent.
        /// Use this only when a project explicitly requires centralized movement authority.
        /// </summary>
        SharedMasterMovementAuthority = 1
    }

    /// <summary>
    /// Supplies the runner's single per-player input value without coupling
    /// <see cref="FusionTransportBridge"/> to a concrete character motor.
    /// </summary>
    public interface IFusionCharacterInputEndpoint
    {
        bool TryConsumeNetworkInput(NetworkRunner runner, NetworkInput input);

        bool TryGetNetworkInput(
            NetworkRunner runner,
            out FusionNativeCharacterInput characterInput);
    }

    /// <summary>
    /// Receives authenticated Shared-mode owner intent on the object's State Authority.
    /// Payload validation remains the responsibility of the movement implementation.
    /// </summary>
    public interface IFusionSharedCharacterEndpoint
    {
        /// <summary>
        /// Owner-clock sequence of the newest reliable one-shot that completed simulation on
        /// State Authority. Receipt is deliberately insufficient: owners retain and retry
        /// one-shots until this application acknowledgement survives in replicated state.
        /// </summary>
        int LastAppliedSharedTransientSourceTick { get; }

        void AcceptSharedCharacterInput(
            PlayerRef source,
            int trustedSourceTick,
            Vector2 move,
            float yaw,
            int sourceTick,
            int flags,
            Vector3 ownerPosition);

        void AcceptSharedCharacterTransient(
            PlayerRef source,
            int trustedSourceTick,
            Vector2 move,
            float yaw,
            int sourceTick,
            int flags,
            Vector3 ownerPosition,
            Vector3 rootMotionDelta,
            float rootMotionWeight,
            float jumpForce);
    }

    /// <summary>
    /// Optional runner-level simulation hook for Shared objects that Fusion does not place in
    /// the local peer's simulation set. Owner-authoritative KCC objects normally do not need it.
    /// </summary>
    public interface IFusionSharedCharacterRunnerPump
    {
        bool RequiresSharedLogicalOwnerProxyPump { get; }

        void SimulateSharedLogicalOwnerProxyTick(
            int tick,
            bool restorePredictedPose);

        void RenderSharedLogicalOwnerProxy();
    }

    /// <summary>
    /// Public boundary implemented by the optional, KCC-referencing sibling assembly.
    /// The main Fusion transport never references Fusion.Addons.KCC types directly.
    /// </summary>
    public interface IFusionKccRuntimeAdapter :
        IFusionCharacterInputEndpoint,
        IFusionSharedCharacterEndpoint,
        IFusionSharedCharacterRunnerPump,
        INetworkAuthoritativePoseProvider
    {
        IUnitDriver CreateDriver(
            FusionKccCharacterBackend backend,
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role);

        void Initialize(
            FusionKccCharacterBackend backend,
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role);

        void ApplySessionProfile(NetworkSessionProfile profile);
        void Shutdown();
    }

    /// <summary>
    /// Shared payload helpers used by both built-in Fusion-native movement and optional
    /// movement adapters. Keeping this logic at the transport boundary prevents routing from
    /// depending on one concrete backend.
    /// </summary>
    public static class FusionCharacterInputUtility
    {
        public static bool HasSharedTransientInput(FusionNativeCharacterInput input)
        {
            return (input.HasOwnerPose && !input.HasContinuousOwnerPose) || input.HasJump ||
                   input.HasResetVerticalVelocity || input.HasCollisionChange ||
                   input.RootMotionWeight > 0.001f ||
                   input.RootMotionDelta.sqrMagnitude > 0.000001f;
        }
    }

    /// <summary>
    /// Resolves transport character endpoints without assuming Fusion Native or Advanced KCC.
    /// </summary>
    internal static class FusionCharacterEndpointResolver
    {
        internal static bool TryGet<TEndpoint>(
            Component component,
            out TEndpoint endpoint)
            where TEndpoint : class
        {
            endpoint = null;
            if (component == null) return false;

            MonoBehaviour[] behaviours = component.GetComponents<MonoBehaviour>();
            NetworkCharacter networkCharacter = component.GetComponent<NetworkCharacter>();
            if (networkCharacter != null)
            {
                // A prefab can temporarily contain both backends while the wizard migrates it.
                // Prefer the endpoint belonging to the backend selected by NetworkCharacter so
                // component ordering cannot accidentally route KCC input to Fusion Native (or
                // vice versa).
                NetworkPredictionBackend selectedBackend = networkCharacter.PredictionBackend;
                for (int i = 0; i < behaviours.Length; i++)
                {
                    MonoBehaviour behaviour = behaviours[i];
                    if (behaviour == null || behaviour is not TEndpoint candidate ||
                        behaviour is not INetworkCharacterPredictionBackend predictionBackend ||
                        predictionBackend.Backend != selectedBackend)
                    {
                        continue;
                    }

                    endpoint = candidate;
                    return true;
                }
            }

            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                // Preserve the legacy concrete lookup semantics: Unity's GetComponent returned
                // disabled movement components too, and each endpoint remains responsible for
                // deciding whether the current call is valid for its lifecycle.
                if (behaviour == null || behaviour is not TEndpoint candidate)
                {
                    continue;
                }

                endpoint = candidate;
                return true;
            }

            return false;
        }
    }
}
