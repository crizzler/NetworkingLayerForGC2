using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    public enum NetworkPredictionBackend
    {
        BuiltIn = 0,
        PurrDiction = 1
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

    public interface INetworkDirectionalInputSink
    {
        void ProcessDirectionalInput(Vector2 inputDirection, Transform cameraTransform, bool jump);
    }

    public interface INetworkNavMeshCommandSink
    {
        void RequestMoveToPosition(Vector3 target);
        void RequestMoveToDirection(Vector3 direction);
        void RequestStop(bool immediate = false);
        void RequestWarp(Vector3 position);
    }
}
