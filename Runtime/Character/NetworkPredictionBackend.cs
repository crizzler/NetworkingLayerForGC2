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
