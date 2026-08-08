#if ARAWN_GC2_FUSION_KCC
using Fusion.Addons.KCC;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.KCC
{
    /// <summary>
    /// Applies GC2 movement values inside KCC's processor pipeline. Continuous intent is also
    /// evaluated during render prediction; tick-addressed one-shots are consumed only during a
    /// fixed simulation pass and are recreated naturally during Fusion resimulation.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FusionGc2KccProcessor : KCCProcessor,
        IPrepareData
    {
        // KCC sorts processors from highest to lowest priority. Run before the default
        // EnvironmentProcessor (1000), which suppresses the remaining PrepareData processors.
        private const float PreparePriority = 2000f;

        [SerializeField] private EnvironmentProcessor m_EnvironmentProcessor;

        private FusionKccCharacterDriver m_Driver;
        private Vector3 m_InputDirection;
        private float m_Yaw;
        private float m_Speed;
        private Vector3 m_Gravity;
        private bool m_UpdateKinematics = true;
        private bool m_ForceGrounded;
        private float m_TerminalVelocity = -53f;
        private float m_LocomotionRootMotionWeight;
        private Vector3 m_RenderRootMotionVelocity;

        private bool m_HasTickCommands;
        private bool m_HasTeleport;
        private Vector3 m_TeleportFootPosition;
        private bool m_Teleport;
        private Vector3 m_RootMotionDelta;
        private float m_RootMotionWeight;
        private Vector3 m_JumpImpulse;
        private bool m_ResetVerticalVelocity;
        private bool m_HasCollisionChange;
        private bool m_CollisionEnabled = true;

        public override float GetPriority(global::Fusion.Addons.KCC.KCC kcc) =>
            PreparePriority;

        internal void Bind(
            FusionKccCharacterDriver driver,
            EnvironmentProcessor environmentProcessor = null)
        {
            m_Driver = driver;
            if (environmentProcessor != null)
            {
                m_EnvironmentProcessor = environmentProcessor;
            }
        }

        internal void SetContinuousIntent(
            Vector3 inputDirection,
            float yaw,
            float speed,
            Vector3 gravity,
            bool updateKinematics,
            bool forceGrounded,
            float terminalVelocity,
            float locomotionRootMotionWeight,
            Vector3 renderRootMotionVelocity)
        {
            m_InputDirection = IsFinite(inputDirection)
                ? Vector3.ClampMagnitude(inputDirection, 1f)
                : Vector3.zero;
            m_Yaw = IsFinite(yaw) ? Mathf.Repeat(yaw, 360f) : 0f;
            m_Speed = IsFinite(speed) ? Mathf.Max(0f, speed) : 0f;
            m_Gravity = IsFinite(gravity) ? gravity : Physics.gravity;
            m_UpdateKinematics = updateKinematics;
            m_ForceGrounded = forceGrounded;
            m_TerminalVelocity = IsFinite(terminalVelocity)
                ? Mathf.Min(0f, terminalVelocity)
                : -53f;
            m_LocomotionRootMotionWeight = IsFinite(locomotionRootMotionWeight)
                ? Mathf.Clamp01(locomotionRootMotionWeight)
                : 0f;
            m_RenderRootMotionVelocity = IsFinite(renderRootMotionVelocity)
                ? renderRootMotionVelocity
                : Vector3.zero;
        }

        internal void QueueTickCommands(
            bool hasTeleport,
            Vector3 teleportFootPosition,
            bool teleport,
            Vector3 rootMotionDelta,
            float rootMotionWeight,
            Vector3 jumpImpulse,
            bool resetVerticalVelocity,
            bool hasCollisionChange,
            bool collisionEnabled)
        {
            m_HasTickCommands = true;
            m_HasTeleport = hasTeleport && IsFinite(teleportFootPosition);
            m_TeleportFootPosition = teleportFootPosition;
            m_Teleport = teleport;
            m_RootMotionDelta = IsFinite(rootMotionDelta)
                ? rootMotionDelta
                : Vector3.zero;
            m_RootMotionWeight = IsFinite(rootMotionWeight)
                ? Mathf.Clamp01(rootMotionWeight)
                : 0f;
            m_JumpImpulse = IsFinite(jumpImpulse)
                ? jumpImpulse
                : Vector3.zero;
            m_ResetVerticalVelocity = resetVerticalVelocity;
            m_HasCollisionChange = hasCollisionChange;
            m_CollisionEnabled = collisionEnabled;
        }

        public void Execute(
            PrepareData stage,
            global::Fusion.Addons.KCC.KCC kcc,
            KCCData data)
        {
            // EnvironmentProcessor owns and suppresses the final gravity/speed stages. Feed
            // GC2's values into it before its PrepareData pass instead of competing with those
            // stages, so Photon's acceleration, slope, step and platform code stays intact.
            if (m_EnvironmentProcessor != null)
            {
                m_EnvironmentProcessor.KinematicSpeed = m_Speed;
                m_EnvironmentProcessor.Gravity = m_Gravity;
            }

            // Let the official environment processor calculate its normal velocity first.
            // The post-process below then performs GC2's exact displacement blend without
            // changing Photon's acceleration, slope, step, snap or platform calculations.
            data.InputDirection = m_UpdateKinematics
                ? m_InputDirection
                : Vector3.zero;
            data.LookYaw = m_Yaw;

            // EnvironmentProcessor suppresses lower-priority processors in its nested velocity
            // stages. A PrepareData post-process is the supported way to adjust the completed
            // result while retaining that processor and any customer extensions.
            kcc.EnqueuePostProcess(PostProcessPreparedData);

            if (!kcc.IsInFixedUpdate || !m_HasTickCommands) return;

            if (m_HasCollisionChange)
            {
                kcc.SetShape(
                    m_CollisionEnabled ? EKCCShape.Capsule : EKCCShape.None);
            }

            if (m_HasTeleport && m_Teleport)
            {
                kcc.SetPosition(
                    m_TeleportFootPosition,
                    teleport: true,
                    allowAntiJitter: false,
                    moveRigidbody: true);
            }

            if (m_JumpImpulse.sqrMagnitude > 0.0000001f)
            {
                // GC2 jump force is a requested vertical velocity. EnvironmentProcessor treats
                // JumpImpulse as a physical impulse and divides it by Rigidbody.mass, so convert
                // the velocity request to impulse here to keep jump height mass-independent.
                float mass = kcc.Rigidbody != null
                    ? Mathf.Max(0.0001f, kcc.Rigidbody.mass)
                    : 1f;
                data.JumpImpulse += m_JumpImpulse * mass;
            }
        }

        private void PostProcessPreparedData(
            global::Fusion.Addons.KCC.KCC kcc,
            KCCData data)
        {
            bool fixedTick = kcc.IsInFixedUpdate;
            bool hasFixedCommands = fixedTick && m_HasTickCommands;

            if (hasFixedCommands && m_HasTeleport && m_Teleport)
            {
                // A hard teleport is the complete movement result for this tick. The backend
                // also sequences a vertical reset, but clearing both velocity channels here
                // prevents locomotion or another processor delta from leaking past the warp.
                data.DynamicVelocity = Vector3.zero;
                data.KinematicVelocity = Vector3.zero;
                data.ExternalDelta = Vector3.zero;
            }
            else if (hasFixedCommands && m_HasTeleport)
            {
                // A non-teleport owner pose is an absolute animation-authored target. Convert it
                // to a KCC external delta so the complete old-to-new path is collision swept.
                // It is the sole motion writer for this tick, matching GC2 traversal semantics.
                data.DynamicVelocity = Vector3.zero;
                data.KinematicVelocity = Vector3.zero;
                data.ExternalDelta = m_TeleportFootPosition - data.BasePosition;
            }
            else
            {
                Vector3 dynamicVelocity = data.DynamicVelocity;
                if ((hasFixedCommands && m_ResetVerticalVelocity) || m_ForceGrounded)
                {
                    // ForceGrounded suppresses both falling and a previously accumulated upward
                    // velocity. Keeping the positive half would make the character rise while
                    // GC2 reports it as grounded.
                    dynamicVelocity.y = 0f;
                }
                else if (dynamicVelocity.y < m_TerminalVelocity)
                {
                    dynamicVelocity.y = m_TerminalVelocity;
                }
                data.DynamicVelocity = dynamicVelocity;

                Vector3 kinematicVelocity = data.KinematicVelocity;
                if (hasFixedCommands && m_ResetVerticalVelocity)
                {
                    kinematicVelocity.y = 0f;
                }

                float deltaTime = Mathf.Max(0f, data.DeltaTime);
                Vector3 kineticDelta = m_UpdateKinematics
                    ? kinematicVelocity * deltaTime
                    : Vector3.zero;
                Vector3 rootMotionDelta = hasFixedCommands
                    ? m_RootMotionDelta
                    : !fixedTick
                        ? m_RenderRootMotionVelocity * deltaTime
                        : Vector3.zero;
                float rootMotionWeight = hasFixedCommands
                    ? m_RootMotionWeight
                    : !fixedTick
                        ? m_LocomotionRootMotionWeight
                        : 0f;

                Vector3 blendedDelta = Vector3.Lerp(
                    kineticDelta,
                    rootMotionDelta,
                    rootMotionWeight);
                if (m_Driver != null)
                {
                    blendedDelta = m_Driver.ProcessAxonometryTranslation(blendedDelta);
                }

                // Preserve EnvironmentProcessor's calculated velocity as the acceleration
                // baseline. ExternalDelta contributes only the difference needed to produce the
                // exact GC2/axonometry displacement in this move and remains collision swept.
                data.KinematicVelocity = m_UpdateKinematics
                    ? kinematicVelocity
                    : Vector3.zero;
                data.ExternalDelta += blendedDelta -
                                      data.KinematicVelocity * deltaTime;
            }

            if (hasFixedCommands)
            {
                ClearTickCommands();
            }
        }

        private void ClearTickCommands()
        {
            m_HasTickCommands = false;
            m_HasTeleport = false;
            m_TeleportFootPosition = Vector3.zero;
            m_Teleport = false;
            m_RootMotionDelta = Vector3.zero;
            m_RootMotionWeight = 0f;
            m_JumpImpulse = Vector3.zero;
            m_ResetVerticalVelocity = false;
            m_HasCollisionChange = false;
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }
}
#endif
