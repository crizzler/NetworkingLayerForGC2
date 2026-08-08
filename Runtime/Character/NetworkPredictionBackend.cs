using System;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    public enum NetworkPredictionBackend
    {
        BuiltIn = 0,

        /// <summary>
        /// Uses the optional PurrDiction integration for PurrNet-native tick prediction,
        /// rollback/resimulation, and render-only character presentation.
        /// </summary>
        PurrDiction = 1,

        /// <summary>
        /// Uses Fusion's tick simulation, rollback/resimulation, and NetworkTRSP render
        /// interpolation instead of the transport-neutral RPC snapshot movement path.
        /// </summary>
        FusionNative = 2,

        /// <summary>
        /// Uses Photon's optional Advanced KCC addon for Fusion-native prediction while a
        /// transport-side proxy keeps the core Networking Layer independent from KCC.
        /// </summary>
        FusionKCC = 3
    }

    public interface INetworkCharacterPredictionBackend
    {
        NetworkPredictionBackend Backend { get; }

        IUnitDriver CreateDriver(
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role);

        void Initialize(
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role,
            bool isServer,
            bool isOwner,
            bool isHost);

        void ApplySessionProfile(NetworkSessionProfile profile);
        void ResetBackend(NetworkCharacter networkCharacter);
    }

    /// <summary>
    /// Supplies the current simulation pose when a prediction backend temporarily exposes a
    /// different render pose through the Character Transform. Server-side systems such as lag
    /// compensation must sample this pose rather than recording an interpolated presentation.
    /// </summary>
    public interface INetworkAuthoritativePoseProvider
    {
        bool TryGetAuthoritativePose(
            out Vector3 position,
            out Quaternion rotation);
    }

    /// <summary>
    /// Allows an authoritative gameplay system to temporarily let the owning client
    /// submit animation-driven pose changes without depending on a concrete movement driver.
    /// </summary>
    public interface INetworkOwnerMotionAuthority
    {
        /// <summary>
        /// Suppresses ordinary reconciliation and includes the owner's animation-driven
        /// pose in outgoing movement samples for at least the requested duration.
        /// </summary>
        void OpenOwnerMotionWindow(float durationSeconds);
    }

    /// <summary>
    /// Server-side counterpart to <see cref="INetworkOwnerMotionAuthority"/>. Gameplay systems
    /// open this gate only after validating an operation that is allowed to drive the owner's
    /// root transform. Merely setting the owner-pose bit in an input packet never opens it.
    /// </summary>
    public interface INetworkServerOwnerMotionAuthority
    {
        /// <summary>
        /// Allows authenticated owner-pose samples for a short, server-approved operation.
        /// The optional operation identifier is diagnostic/correlation data only.
        /// </summary>
        void OpenServerOwnerMotionWindow(float durationSeconds, uint operationId = 0);

        /// <summary>
        /// Ends the current operation, retaining at most the requested grace period for inputs
        /// already in flight. Passing zero closes the gate immediately.
        /// </summary>
        void CloseServerOwnerMotionWindow(float graceSeconds = 0f);
    }

    /// <summary>
    /// Transport-neutral coordination hooks for temporarily owner-authored root motion.
    /// Traversal uses these hooks to keep its relative pose aligned with the collision-constrained
    /// root accepted by an authoritative movement backend. Keeping the hook outside a concrete
    /// driver lets the built-in, PurrDiction-native, and Fusion-native backends enforce the
    /// same invariants.
    /// </summary>
    public static class NetworkOwnerMotionAuthorityHooks
    {
        public static event Action<Character, Vector3> PositionAccepted;
        public static event Func<Character, Vector3, string> PositionRejectionRequested;
        public static event Func<Character, Vector3, string> ExternalRootWriteAllowanceRequested;
        public static event Func<Character, bool> ContinuousOwnerPoseRequested;

        public static void NotifyPositionAccepted(Character character, Vector3 position)
        {
            Delegate[] handlers = PositionAccepted?.GetInvocationList();
            if (handlers == null) return;

            foreach (Delegate handler in handlers)
            {
                if (handler is not Action<Character, Vector3> callback) continue;

                try
                {
                    callback.Invoke(character, position);
                }
                catch (Exception exception)
                {
                    Debug.LogError(
                        $"[NetworkOwnerMotion] Accepted-position hook failed for " +
                        $"'{character?.name ?? "Character"}': {exception.Message}\n{exception.StackTrace}",
                        character);
                }
            }
        }

        public static bool TryGetPositionRejection(
            Character character,
            Vector3 position,
            out string reason)
        {
            return TryGetReason(
                PositionRejectionRequested,
                character,
                position,
                "position-rejection",
                out reason);
        }

        public static bool TryGetExternalRootWriteAllowance(
            Character character,
            Vector3 position,
            out string reason)
        {
            return TryGetReason(
                ExternalRootWriteAllowanceRequested,
                character,
                position,
                "external-root-write-allowance",
                out reason);
        }

        /// <summary>
        /// Returns whether the active gameplay operation authors a replaceable stream of
        /// absolute owner poses. Continuous interactive traversal (climbing, ladders and
        /// ziplines) uses this classification so a prediction backend can transport the newest
        /// pose as continuous intent instead of replaying every frame as a reliable one-shot.
        /// Finite motion links such as Vault, Jump and PullUp remain reliable transients.
        /// </summary>
        public static bool IsContinuousOwnerPose(Character character)
        {
            Delegate[] handlers = ContinuousOwnerPoseRequested?.GetInvocationList();
            if (handlers == null) return false;

            foreach (Delegate handler in handlers)
            {
                if (handler is not Func<Character, bool> provider) continue;

                try
                {
                    if (provider.Invoke(character)) return true;
                }
                catch (Exception exception)
                {
                    Debug.LogError(
                        $"[NetworkOwnerMotion] continuous-owner-pose hook failed for " +
                        $"'{character?.name ?? "Character"}': {exception.Message}\n{exception.StackTrace}",
                        character);
                }
            }

            return false;
        }

        private static bool TryGetReason(
            Func<Character, Vector3, string> providers,
            Character character,
            Vector3 position,
            string hookName,
            out string reason)
        {
            reason = string.Empty;
            Delegate[] handlers = providers?.GetInvocationList();
            if (handlers == null) return false;

            foreach (Delegate handler in handlers)
            {
                if (handler is not Func<Character, Vector3, string> provider) continue;

                try
                {
                    string candidate = provider.Invoke(character, position);
                    if (string.IsNullOrEmpty(candidate)) continue;
                    reason = candidate;
                    return true;
                }
                catch (Exception exception)
                {
                    Debug.LogError(
                        $"[NetworkOwnerMotion] {hookName} hook failed for " +
                        $"'{character?.name ?? "Character"}': {exception.Message}\n{exception.StackTrace}",
                        character);
                }
            }

            return false;
        }
    }

    public interface INetworkDirectionalInputSink
    {
        void ProcessDirectionalInput(Vector2 inputDirection, Transform cameraTransform, bool jump);
    }

    /// <summary>
    /// Receives an authoritative/predicted presentation velocity from network motion systems.
    /// This keeps animation direction transport-neutral instead of hard-coding concrete drivers.
    /// </summary>
    public interface INetworkExternalMoveDirectionSink
    {
        void SetExternalMoveDirection(
            Vector3 velocity,
            bool preserveWhileTraversalLikeMotion = false);
    }

    public interface INetworkNavMeshCommandSink
    {
        void RequestMoveToPosition(Vector3 target);
        void RequestMoveToDirection(Vector3 direction);
        void RequestStop(bool immediate = false);
        void RequestWarp(Vector3 position);
    }
}
