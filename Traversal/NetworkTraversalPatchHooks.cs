#if GC2_TRAVERSAL
using System;
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

        private bool m_IsServer;
        private bool m_Installed;

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
            Type traversalStanceType = typeof(TraversalStance);

            return
                HasPublicStaticField(traverseLinkType, "NetworkRunValidator", typeof(Func<TraverseLink, Character, bool>)) &&
                HasPublicStaticField(traverseInteractiveType, "NetworkEnterValidator", typeof(Func<TraverseInteractive, Character, InteractiveTransitionData, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkTryCancelValidator", typeof(Func<TraversalStance, Args, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkForceCancelValidator", typeof(Func<TraversalStance, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkTryJumpValidator", typeof(Func<TraversalStance, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkTryActionValidator", typeof(Func<TraversalStance, IdString, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkTryStateEnterValidator", typeof(Func<TraversalStance, IdString, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkTryStateExitValidator", typeof(Func<TraversalStance, bool>));
        }

        private void InstallHooks()
        {
            if (m_Installed) return;

            if (!IsTraversalPatched())
            {
                Debug.LogWarning("[NetworkTraversalPatchHooks] Traversal runtime patch markers were not detected. Using interception mode.");
                return;
            }

            SetStaticField(typeof(TraverseLink), "NetworkRunValidator", new Func<TraverseLink, Character, bool>(ValidateRunTraverseLink));
            SetStaticField(typeof(TraverseInteractive), "NetworkEnterValidator", new Func<TraverseInteractive, Character, InteractiveTransitionData, bool>(ValidateEnterTraverseInteractive));

            SetStaticField(typeof(TraversalStance), "NetworkTryCancelValidator", new Func<TraversalStance, Args, bool>(ValidateTryCancel));
            SetStaticField(typeof(TraversalStance), "NetworkForceCancelValidator", new Func<TraversalStance, bool>(ValidateForceCancel));
            SetStaticField(typeof(TraversalStance), "NetworkTryJumpValidator", new Func<TraversalStance, bool>(ValidateTryJump));
            SetStaticField(typeof(TraversalStance), "NetworkTryActionValidator", new Func<TraversalStance, IdString, bool>(ValidateTryAction));
            SetStaticField(typeof(TraversalStance), "NetworkTryStateEnterValidator", new Func<TraversalStance, IdString, bool>(ValidateTryStateEnter));
            SetStaticField(typeof(TraversalStance), "NetworkTryStateExitValidator", new Func<TraversalStance, bool>(ValidateTryStateExit));

            m_Installed = true;
        }

        private void UninstallHooks()
        {
            if (!m_Installed) return;

            SetStaticField(typeof(TraverseLink), "NetworkRunValidator", null);
            SetStaticField(typeof(TraverseInteractive), "NetworkEnterValidator", null);

            SetStaticField(typeof(TraversalStance), "NetworkTryCancelValidator", null);
            SetStaticField(typeof(TraversalStance), "NetworkForceCancelValidator", null);
            SetStaticField(typeof(TraversalStance), "NetworkTryJumpValidator", null);
            SetStaticField(typeof(TraversalStance), "NetworkTryActionValidator", null);
            SetStaticField(typeof(TraversalStance), "NetworkTryStateEnterValidator", null);
            SetStaticField(typeof(TraversalStance), "NetworkTryStateExitValidator", null);

            m_Installed = false;
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
            if (m_IsServer) return true;

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

        private static NetworkTraversalController ResolveController(Character character)
        {
            if (character == null) return null;
            return character.GetComponent<NetworkTraversalController>();
        }

        private static NetworkTraversalController ResolveController(TraversalStance stance)
        {
            return stance != null ? ResolveController(stance.Character) : null;
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
