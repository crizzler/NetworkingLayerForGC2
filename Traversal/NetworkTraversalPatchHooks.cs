#if GC2_TRAVERSAL
using System;
using System.Collections.Generic;
using System.Reflection;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Traversal;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Traversal
{
    /// <summary>
    /// Runtime installer for Traversal patch delegates.
    /// In patched mode, direct traversal calls are rerouted through NetworkTraversalController requests.
    /// </summary>
    public class NetworkTraversalPatchHooks : NetworkSingleton<NetworkTraversalPatchHooks>
    {
        private const BindingFlags STATIC_PUBLIC = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags INSTANCE_ALL = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const float LEDGE_EDGE_INPUT_THRESHOLD = 0.25f;
        private const float LEDGE_EDGE_POSITION_TOLERANCE = 0.06f;
        private const float LEDGE_EDGE_INTENT_MEMORY_SECONDS = 0.45f;
        private const float LEDGE_EDGE_OVERRIDE_LOG_INTERVAL = 0.35f;

        private static readonly PropertyInfo s_TraversalStanceRelativePositionProperty =
            typeof(TraversalStance).GetProperty("RelativePosition", INSTANCE_ALL);

        private bool m_IsServer;
        private bool m_Installed;
        private bool m_AnimationOverrideInstalled;
        private float m_LastLedgeEdgeOverrideLogTime;
        private readonly Dictionary<int, LedgeEdgeIntentMemory> m_LedgeEdgeIntentMemory = new();

        private struct LedgeEdgeIntentMemory
        {
            public int TraverseInstanceId;
            public float Direction;
            public float Timestamp;
        }

        public bool IsPatchActive => m_Installed && IsTraversalPatched();

        public void Initialize(bool isServer, bool isActive = true)
        {
            m_IsServer = isServer;
            if (isActive) InstallHooks();
            else UninstallHooks();
        }

        protected override void OnSingletonCleanup()
        {
            UninstallHooks();
        }

        public static bool IsTraversalPatched()
        {
            Type traverseLinkType = typeof(TraverseLink);
            Type traverseInteractiveType = typeof(TraverseInteractive);
            Type motionInteractiveType = typeof(MotionInteractive);
            Type traversalStanceType = typeof(TraversalStance);

            return
                HasPublicStaticField(traverseLinkType, "NetworkRunValidator", typeof(Func<TraverseLink, Character, bool>)) &&
                HasPublicStaticField(traverseInteractiveType, "NetworkEnterValidator", typeof(Func<TraverseInteractive, Character, InteractiveTransitionData, bool>)) &&
                HasPublicStaticField(motionInteractiveType, "NetworkEdgeConnectionResolver", typeof(Func<MotionInteractive, TraverseInteractive, Character, Vector3, Vector3, bool, Traverse>)) &&
                HasPublicStaticField(motionInteractiveType, "NetworkConnectionSkipTransitionResolver", typeof(Func<Traverse, Traverse, Character, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkTryCancelValidator", typeof(Func<TraversalStance, Args, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkForceCancelValidator", typeof(Func<TraversalStance, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkTryJumpValidator", typeof(Func<TraversalStance, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkTryActionValidator", typeof(Func<TraversalStance, IdString, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkTryStateEnterValidator", typeof(Func<TraversalStance, IdString, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkTryStateExitValidator", typeof(Func<TraversalStance, bool>));
        }

        private void InstallHooks()
        {
            InstallAnimationInputOverride();

            if (m_Installed)
            {
                Debug.Log($"[NetworkTraversalPatchHooks] Traversal patch hooks refreshed. server={m_IsServer}");
                return;
            }

            if (!IsTraversalPatched())
            {
                Debug.LogWarning("[NetworkTraversalPatchHooks] Traversal runtime patch markers were not detected. Using interception mode.");
                return;
            }

            SetStaticField(typeof(TraverseLink), "NetworkRunValidator", new Func<TraverseLink, Character, bool>(ValidateRunTraverseLink));
            SetStaticField(typeof(TraverseInteractive), "NetworkEnterValidator", new Func<TraverseInteractive, Character, InteractiveTransitionData, bool>(ValidateEnterTraverseInteractive));
            SetStaticField(typeof(MotionInteractive), "NetworkEdgeConnectionResolver", new Func<MotionInteractive, TraverseInteractive, Character, Vector3, Vector3, bool, Traverse>(ResolveInteractiveEdgeConnection));
            SetStaticField(typeof(MotionInteractive), "NetworkConnectionSkipTransitionResolver", new Func<Traverse, Traverse, Character, bool>(ShouldSkipConnectionTransition));

            SetStaticField(typeof(TraversalStance), "NetworkTryCancelValidator", new Func<TraversalStance, Args, bool>(ValidateTryCancel));
            SetStaticField(typeof(TraversalStance), "NetworkForceCancelValidator", new Func<TraversalStance, bool>(ValidateForceCancel));
            SetStaticField(typeof(TraversalStance), "NetworkTryJumpValidator", new Func<TraversalStance, bool>(ValidateTryJump));
            SetStaticField(typeof(TraversalStance), "NetworkTryActionValidator", new Func<TraversalStance, IdString, bool>(ValidateTryAction));
            SetStaticField(typeof(TraversalStance), "NetworkTryStateEnterValidator", new Func<TraversalStance, IdString, bool>(ValidateTryStateEnter));
            SetStaticField(typeof(TraversalStance), "NetworkTryStateExitValidator", new Func<TraversalStance, bool>(ValidateTryStateExit));

            m_Installed = true;
            Debug.Log($"[NetworkTraversalPatchHooks] Traversal patch hooks installed. server={m_IsServer}");
        }

        private void UninstallHooks()
        {
            UninstallAnimationInputOverride();

            if (!m_Installed) return;

            SetStaticField(typeof(TraverseLink), "NetworkRunValidator", null);
            SetStaticField(typeof(TraverseInteractive), "NetworkEnterValidator", null);
            SetStaticField(typeof(MotionInteractive), "NetworkEdgeConnectionResolver", null);
            SetStaticField(typeof(MotionInteractive), "NetworkConnectionSkipTransitionResolver", null);

            SetStaticField(typeof(TraversalStance), "NetworkTryCancelValidator", null);
            SetStaticField(typeof(TraversalStance), "NetworkForceCancelValidator", null);
            SetStaticField(typeof(TraversalStance), "NetworkTryJumpValidator", null);
            SetStaticField(typeof(TraversalStance), "NetworkTryActionValidator", null);
            SetStaticField(typeof(TraversalStance), "NetworkTryStateEnterValidator", null);
            SetStaticField(typeof(TraversalStance), "NetworkTryStateExitValidator", null);

            m_Installed = false;
        }

        private void InstallAnimationInputOverride()
        {
            UnitAnimimNetworkKinematic.TraversalAnimationInputOverride = ApplyTraversalAnimationInputOverride;
            m_AnimationOverrideInstalled = true;
        }

        private void UninstallAnimationInputOverride()
        {
            if (m_AnimationOverrideInstalled &&
                UnitAnimimNetworkKinematic.TraversalAnimationInputOverride == ApplyTraversalAnimationInputOverride)
            {
                UnitAnimimNetworkKinematic.TraversalAnimationInputOverride = null;
            }

            m_AnimationOverrideInstalled = false;
        }

        private bool ValidateRunTraverseLink(TraverseLink traverseLink, Character character)
        {
            NetworkTraversalController controller = ResolveController(character);
            return RouteClientAction(controller, () => controller.RequestRunTraverseLinkFromPatch(traverseLink, character));
        }

        private bool ValidateEnterTraverseInteractive(TraverseInteractive traverseInteractive, Character character, InteractiveTransitionData transition)
        {
            NetworkTraversalController controller = ResolveController(character);
            return RouteClientAction(controller, () => controller.RequestEnterTraverseInteractiveFromPatch(traverseInteractive, character, transition));
        }

        private bool ValidateTryCancel(TraversalStance stance, Args args)
        {
            NetworkTraversalController controller = ResolveController(stance);
            return RouteClientAction(controller, () => controller.RequestTryCancelFromPatch(stance, args));
        }

        private bool ValidateForceCancel(TraversalStance stance)
        {
            NetworkTraversalController controller = ResolveController(stance);
            return RouteClientAction(controller, () => controller.RequestForceCancelFromPatch(stance));
        }

        private bool ValidateTryJump(TraversalStance stance)
        {
            NetworkTraversalController controller = ResolveController(stance);
            return RouteClientAction(controller, () => controller.RequestTryJumpFromPatch(stance));
        }

        private bool ValidateTryAction(TraversalStance stance, IdString actionId)
        {
            NetworkTraversalController controller = ResolveController(stance);
            return RouteClientAction(controller, () => controller.RequestTryActionFromPatch(stance, actionId));
        }

        private bool ValidateTryStateEnter(TraversalStance stance, IdString stateId)
        {
            NetworkTraversalController controller = ResolveController(stance);
            return RouteClientAction(controller, () => controller.RequestTryStateEnterFromPatch(stance, stateId));
        }

        private bool ValidateTryStateExit(TraversalStance stance)
        {
            NetworkTraversalController controller = ResolveController(stance);
            return RouteClientAction(controller, () => controller.RequestTryStateExitFromPatch(stance));
        }

        private bool RouteClientAction(NetworkTraversalController controller, Action requestAction)
        {
            if (controller == null || !controller.enabled)
            {
                return true;
            }

            if (controller.IsApplyingAuthoritativeChange)
            {
                return true;
            }

            if (controller.IsRemoteClient)
            {
                return false;
            }

            requestAction?.Invoke();
            return false;
        }

        private Traverse ResolveInteractiveEdgeConnection(
            MotionInteractive motion,
            TraverseInteractive interactive,
            Character character,
            Vector3 currentLocalPosition,
            Vector3 localDirection,
            bool edgeB)
        {
            NetworkTraversalController controller = ResolveController(character);
            return controller != null && controller.enabled
                ? controller.ResolveInteractiveEdgeConnectionFromPatch(
                    motion,
                    interactive,
                    character,
                    currentLocalPosition,
                    localDirection,
                    edgeB)
                : null;
        }

        private bool ShouldSkipConnectionTransition(Traverse current, Traverse next, Character character)
        {
            NetworkTraversalController controller = ResolveController(character);
            return controller == null || !controller.enabled ||
                   controller.ShouldSkipConnectionTransitionFromPatch(current, next, character);
        }

        private bool ApplyTraversalAnimationInputOverride(
            Character character,
            ref Vector3 targetIntent,
            ref Vector3 targetSpeed,
            Vector3 currentSpeed)
        {
            if (character == null || character.Combat == null) return false;
            NetworkCharacter networkCharacter = character.GetComponent<NetworkCharacter>();

            TraversalStance stance = character.Combat.RequestStance<TraversalStance>();
            if (stance?.Traverse is not TraverseInteractive interactive) return false;
            if (interactive.MotionInteractive == null) return false;
            if (!string.Equals(interactive.MotionInteractive.name, "Motion_Ledge_Climb", StringComparison.Ordinal))
            {
                return false;
            }

            float horizontalInput = GetHorizontalTraversalInput(
                character,
                targetIntent,
                targetSpeed,
                currentSpeed);
            if (Mathf.Abs(horizontalInput) >= LEDGE_EDGE_INPUT_THRESHOLD)
            {
                RememberLedgeEdgeIntent(character, interactive, horizontalInput);
            }
            else if (TryGetRememberedLedgeEdgeIntent(character, interactive, out float rememberedInput))
            {
                horizontalInput = rememberedInput;
            }

            if (Mathf.Abs(horizontalInput) < LEDGE_EDGE_INPUT_THRESHOLD) return false;

            Vector3 localPosition = GetTraversalLocalPosition(stance, interactive, character);
            bool pushingA = localPosition.z <= interactive.PositionA + LEDGE_EDGE_POSITION_TOLERANCE &&
                            horizontalInput < -LEDGE_EDGE_INPUT_THRESHOLD;
            bool pushingB = localPosition.z >= interactive.PositionB - LEDGE_EDGE_POSITION_TOLERANCE &&
                            horizontalInput > LEDGE_EDGE_INPUT_THRESHOLD;

            if (!pushingA && !pushingB) return false;

            targetSpeed = Vector3.zero;
            targetIntent = new Vector3(Mathf.Sign(horizontalInput), 0f, 0f);

            float now = Time.time;
            if (now - m_LastLedgeEdgeOverrideLogTime >= LEDGE_EDGE_OVERRIDE_LOG_INTERVAL)
            {
                m_LastLedgeEdgeOverrideLogTime = now;
                Debug.Log(
                    $"[TraversalAnimDebug][PatchHooks] ledge edge intent override " +
                    $"character='{character.name}' traverse='{interactive.name}' " +
                    $"role={(networkCharacter != null ? networkCharacter.CurrentRole.ToString() : "none")} " +
                    $"inputX={horizontalInput:F3} localZ={localPosition.z:F3} " +
                    $"boundsA={interactive.PositionA:F3} boundsB={interactive.PositionB:F3} " +
                    $"edge={(pushingA ? "A" : "B")} intent={FormatVector(targetIntent)} speed={FormatVector(targetSpeed)}",
                    character);
            }

            return true;
        }

        private static float GetHorizontalTraversalInput(
            Character character,
            Vector3 targetIntent,
            Vector3 targetSpeed,
            Vector3 currentSpeed)
        {
            if (character?.Player is UnitPlayerDirectionalNetwork networkPlayer)
            {
                Vector2 rawInput = networkPlayer.RawInput;
                if (Mathf.Abs(rawInput.x) > 0.0001f)
                {
                    return rawInput.x;
                }
            }

            float localInput = character?.Player?.LocalInputDirection.x ?? 0f;
            if (Mathf.Abs(localInput) > 0.0001f)
            {
                return localInput;
            }

            if (Mathf.Abs(targetSpeed.x) > 0.0001f)
            {
                return targetSpeed.x;
            }

            if (Mathf.Abs(currentSpeed.x) > 0.0001f)
            {
                return currentSpeed.x;
            }

            return Mathf.Abs(targetIntent.x) > 0.0001f ? targetIntent.x : 0f;
        }

        private void RememberLedgeEdgeIntent(
            Character character,
            TraverseInteractive interactive,
            float horizontalInput)
        {
            if (character == null || interactive == null) return;

            m_LedgeEdgeIntentMemory[character.GetInstanceID()] = new LedgeEdgeIntentMemory
            {
                TraverseInstanceId = interactive.GetInstanceID(),
                Direction = Mathf.Sign(horizontalInput),
                Timestamp = Time.time
            };
        }

        private bool TryGetRememberedLedgeEdgeIntent(
            Character character,
            TraverseInteractive interactive,
            out float horizontalInput)
        {
            horizontalInput = 0f;
            if (character == null || interactive == null) return false;

            int key = character.GetInstanceID();
            if (!m_LedgeEdgeIntentMemory.TryGetValue(key, out LedgeEdgeIntentMemory memory))
            {
                return false;
            }

            if (memory.TraverseInstanceId != interactive.GetInstanceID() ||
                Time.time - memory.Timestamp > LEDGE_EDGE_INTENT_MEMORY_SECONDS)
            {
                m_LedgeEdgeIntentMemory.Remove(key);
                return false;
            }

            horizontalInput = memory.Direction;
            return Mathf.Abs(horizontalInput) >= LEDGE_EDGE_INPUT_THRESHOLD;
        }

        private static Vector3 GetTraversalLocalPosition(
            TraversalStance stance,
            TraverseInteractive interactive,
            Character character)
        {
            if (s_TraversalStanceRelativePositionProperty?.GetValue(stance) is Vector3 relativePosition)
            {
                return relativePosition;
            }

            return interactive != null && character != null
                ? interactive.Transform.InverseTransformPoint(character.transform.position)
                : Vector3.zero;
        }

        private static NetworkTraversalController ResolveController(Character character)
        {
            if (character == null) return null;
            return character.GetComponent<NetworkTraversalController>();
        }

        private static NetworkTraversalController ResolveController(TraversalStance stance)
        {
            return stance != null ? ResolveController(stance.Character) : null;
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3},{value.y:F3},{value.z:F3})";
        }

        private static void SetStaticField(Type type, string fieldName, object value)
        {
            FieldInfo field = type.GetField(fieldName, STATIC_PUBLIC);
            if (field == null)
            {
                Debug.LogWarning($"[NetworkTraversalPatchHooks] Missing patched field {type.Name}.{fieldName}. GC2 update likely changed signatures.");
                return;
            }

            field.SetValue(null, value);
        }

        private static bool HasPublicStaticField(Type type, string fieldName, Type expectedFieldType)
        {
            FieldInfo field = type.GetField(fieldName, STATIC_PUBLIC);
            return field != null && expectedFieldType.IsAssignableFrom(field.FieldType);
        }
    }
}
#endif
