using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Version(1, 0, 0)]

    [Title("Network Teleport")]
    [Description("Requests an authoritative teleport for a network Character")]

    [Category("Network/Characters/Navigation/Teleport")]

    [Parameter("Location", "The position and/or rotation where the Character is teleported")]
    [Parameter("Reset Vertical Velocity", "Clears falling velocity as part of the approved teleport")]

    [Keywords("Change", "Position", "Location", "Respawn", "Spawn", "Reset")]
    [Image(typeof(IconCharacter), ColorTheme.Type.Blue)]

    [Serializable]
    public sealed class InstructionNetworkCharacterTeleport : TInstructionCharacterNavigation
    {
        [SerializeField] private PropertyGetLocation m_Location = GetLocationNavigationMarker.Create;
        [SerializeField] private bool m_ResetVerticalVelocity = true;

        [NonSerialized] private float m_NextWarningTime;

        public override string Title => $"Network Teleport {this.m_Character} to {this.m_Location}";

        protected override Task Run(Args args)
        {
            Character character = this.m_Character.Get<Character>(args);
            if (character == null) return DefaultResult;

            Location location = this.m_Location.Get(args);
            GameObject target = character.gameObject;
            bool hasPosition = location.HasPosition(target);
            bool hasRotation = location.HasRotation(target);

            if (!hasPosition && !hasRotation) return DefaultResult;

            NetworkCharacter networkCharacter = character.GetComponent<NetworkCharacter>();
            if (networkCharacter == null)
            {
                ApplyNonNetworkTeleport(character, location, hasPosition, hasRotation);
                return DefaultResult;
            }

            // Only the owning player's trigger is allowed to request this operation. Remote
            // replicas receive the approved command through the motion transport instead.
            if (!networkCharacter.IsOwnerInstance) return DefaultResult;

            UnitMotionNetworkController motionController = networkCharacter.MotionController;
            if (networkCharacter.NetworkId == 0 || motionController == null)
            {
                WarnUnavailableRoute(character, networkCharacter);
                return DefaultResult;
            }

            if (!hasPosition)
            {
                // The semantic teleport command always carries a position. Character.Feet is the
                // coordinate expected by IUnitDriver.SetPosition and preserves rotation-only use.
                motionController.RequestTeleport(
                    character.Feet,
                    location.GetRotation(target).eulerAngles.y,
                    m_ResetVerticalVelocity,
                    result => WarnIfRejected(character, result));
                return DefaultResult;
            }

            Vector3 position = location.GetPosition(target);
            float rotationY = hasRotation
                ? location.GetRotation(target).eulerAngles.y
                : float.NaN;

            motionController.RequestTeleport(
                position,
                rotationY,
                m_ResetVerticalVelocity,
                result => WarnIfRejected(character, result));

            return DefaultResult;
        }

        private void ApplyNonNetworkTeleport(
            Character character,
            Location location,
            bool hasPosition,
            bool hasRotation)
        {
            GameObject target = character.gameObject;
            if (hasPosition)
            {
                character.Driver.SetPosition(location.GetPosition(target), true);
            }

            if (hasRotation)
            {
                character.Driver.SetRotation(location.GetRotation(target));
            }

            if (m_ResetVerticalVelocity)
            {
                character.Driver.ResetVerticalVelocity();
            }
        }

        private void WarnUnavailableRoute(Character character, NetworkCharacter networkCharacter)
        {
            if (UnityEngine.Time.unscaledTime < m_NextWarningTime) return;
            m_NextWarningTime = UnityEngine.Time.unscaledTime + 5f;

            Debug.LogWarning(
                $"[NetworkTeleport] Authoritative teleport for '{character.name}' was not sent " +
                $"because its network motion route is not ready " +
                $"(networkId={networkCharacter.NetworkId}, " +
                $"motionController={(networkCharacter.MotionController != null)}).",
                character);
        }

        private void WarnIfRejected(Character character, NetworkMotionResult result)
        {
            if (result.approved || UnityEngine.Time.unscaledTime < m_NextWarningTime) return;
            m_NextWarningTime = UnityEngine.Time.unscaledTime + 5f;

            Debug.LogWarning(
                $"[NetworkTeleport] The server rejected the teleport for '{character.name}' " +
                $"(reason={result.rejectionReason}, sequence={result.commandSequence}).",
                character);
        }
    }
}
