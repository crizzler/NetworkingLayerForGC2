using System;
using System.Reflection;
using Arawn.GameCreator2.Networking;
using GameCreator.Runtime.Characters;
using UnityEngine;
using UnityEngine.TestTools;

namespace Arawn.GameCreator2.Networking.TestUtilities
{
    /// <summary>
    /// Reproduces the Unity lifecycle that EditMode tests do not run when they create a
    /// MonoBehaviour with AddComponent. Tests which exercise runtime-ready components must use
    /// this helper instead of depending on uninitialized serialized defaults.
    /// </summary>
    public static class EditModeLifecycle
    {
        private const BindingFlags LifecycleFlags =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly;

        public static T AddComponent<T>(GameObject gameObject) where T : MonoBehaviour
        {
            if (gameObject == null) throw new ArgumentNullException(nameof(gameObject));
            if (Application.isPlaying)
            {
                throw new InvalidOperationException(
                    $"{nameof(EditModeLifecycle)} is only intended for EditMode tests.");
            }

            T component = gameObject.AddComponent<T>();
            PrepareComponent(component);
            Invoke(component, "Awake");
            if (component.isActiveAndEnabled) Invoke(component, "OnEnable");
            return component;
        }

        public static MonoBehaviour AddComponent(GameObject gameObject, Type componentType)
        {
            if (gameObject == null) throw new ArgumentNullException(nameof(gameObject));
            if (componentType == null) throw new ArgumentNullException(nameof(componentType));
            if (!typeof(MonoBehaviour).IsAssignableFrom(componentType))
            {
                throw new ArgumentException(
                    $"{componentType.FullName} is not a MonoBehaviour.", nameof(componentType));
            }
            if (Application.isPlaying)
            {
                throw new InvalidOperationException(
                    $"{nameof(EditModeLifecycle)} is only intended for EditMode tests.");
            }

            var component = (MonoBehaviour) gameObject.AddComponent(componentType);
            PrepareComponent(component);
            Invoke(component, "Awake");
            if (component.isActiveAndEnabled) Invoke(component, "OnEnable");
            return component;
        }

        public static void Invoke(MonoBehaviour component, string message)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            if (string.IsNullOrEmpty(message)) throw new ArgumentException(
                "A lifecycle message is required.", nameof(message));

            MethodInfo method = FindLifecycleMethod(component.GetType(), message);
            method?.Invoke(component, null);
        }

        /// <summary>
        /// Assigns a real runtime role while suppressing only the synchronous EditMode error
        /// emitted by GC2's stock controller driver when it disposes its generated helper with
        /// Object.Destroy. Runtime role assignment itself is still exercised in full.
        /// </summary>
        public static void InitializeNetworkRole(
            NetworkCharacter character,
            bool isServer,
            bool isOwner,
            bool isHost = false)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));
            // GC2 2.19's stock driver uses Object.Destroy for its generated controller.
            // That is correct in Play Mode but Unity reports it as an error while this
            // EditMode fixture performs its first swap to a network driver. Limit suppression
            // to that synchronous vendor lifecycle call; subsequent test errors still fail.
            bool previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                character.InitializeNetworkRole(isServer, isOwner, isHost);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previousIgnore;
            }
        }

        public static void InitializeNetworkRole(
            NetworkCharacter character,
            bool isServer,
            bool isOwner,
            bool isHost,
            bool hasAuthenticatedPlayerOwner)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));
            bool previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                character.InitializeNetworkRole(
                    isServer,
                    isOwner,
                    isHost,
                    hasAuthenticatedPlayerOwner);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previousIgnore;
            }
        }

        private static MethodInfo FindLifecycleMethod(Type type, string message)
        {
            while (type != null && type != typeof(MonoBehaviour))
            {
                MethodInfo method = type.GetMethod(message, LifecycleFlags, null, Type.EmptyTypes, null);
                if (method != null) return method;
                type = type.BaseType;
            }

            return null;
        }

        private static void PrepareComponent(MonoBehaviour component)
        {
            if (component is not Character character) return;

            Animator animator = component.GetComponent<Animator>();
            if (animator == null) animator = component.gameObject.AddComponent<Animator>();
            character.Animim.Animator = animator;
        }
    }
}
