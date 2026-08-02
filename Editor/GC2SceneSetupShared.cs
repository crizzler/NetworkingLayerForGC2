using System;
using Arawn.GameCreator2.Networking.Security;
using GameCreator.Runtime.Characters;
using UnityEditor;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Editor
{
    /// <summary>
    /// Transport-neutral scene and prefab preparation shared by transport-specific wizards.
    /// Callers retain ownership of object lookup and Undo policy through the supplied callback.
    /// </summary>
    public static class GC2SceneSetupShared
    {
        public static void EnsureCoreManagers(
            GameObject root,
            Func<Type, string, GameObject, Component> ensureComponent)
        {
            if (ensureComponent == null)
            {
                throw new ArgumentNullException(nameof(ensureComponent));
            }

            ensureComponent(
                typeof(NetworkSecurityManager),
                "Network Security Manager",
                root);
            ensureComponent(
                typeof(NetworkCoreManager),
                "Network Core Manager",
                root);
            ensureComponent(
                typeof(NetworkAnimationManager),
                "Network Animation Manager",
                root);
            ensureComponent(
                typeof(NetworkMotionManager),
                "Network Motion Manager",
                root);
            ensureComponent(
                typeof(NetworkVariableManager),
                "Network Variable Manager",
                root);
        }

        public static bool ConfigureNetworkReadyCharacterKernel(
            Character character,
            bool applyWithUndo)
        {
            if (character == null) return false;

            Transform mannequin = character.Animim?.Mannequin;
            Animator animator = character.Animim?.Animator;
            var serialized = new SerializedObject(character);
            SerializedProperty kernel = serialized.FindProperty("m_Kernel");
            if (kernel == null) return false;

            bool changed = false;
            SerializedProperty player = kernel.FindPropertyRelative("m_Player");
            SerializedProperty driver = kernel.FindPropertyRelative("m_Driver");
            IUnitPlayer current = character.Player;

            if (current is UnitPlayerPointClick || current is UnitPlayerPointClickNetwork)
            {
                changed |= SetManagedReferenceIfDifferent<UnitPlayerPointClickNetwork>(player);
                changed |= SetManagedReferenceIfDifferent<UnitDriverNavmeshNetworkClient>(driver);
            }
            else if (current is UnitPlayerTank || current is UnitPlayerTankNetwork)
            {
                changed |= SetManagedReferenceIfDifferent<UnitPlayerTankNetwork>(player);
                changed |= SetManagedReferenceIfDifferent<UnitDriverNetworkClient>(driver);
            }
            else if (current is UnitPlayerFollowPointer ||
                     current is UnitPlayerFollowPointerNetwork)
            {
                changed |= SetManagedReferenceIfDifferent<UnitPlayerFollowPointerNetwork>(player);
                changed |= SetManagedReferenceIfDifferent<UnitDriverNetworkClient>(driver);
            }
            else
            {
                changed |= SetManagedReferenceIfDifferent<UnitPlayerDirectionalNetwork>(player);
                changed |= SetManagedReferenceIfDifferent<UnitDriverNetworkClient>(driver);
            }

            changed |= SetManagedReferenceIfDifferent<UnitMotionNetworkController>(
                kernel.FindPropertyRelative("m_Motion"));
            changed |= SetManagedReferenceIfDifferent<UnitFacingNetworkPivot>(
                kernel.FindPropertyRelative("m_Facing"));
            changed |= SetManagedReferenceIfDifferent<UnitAnimimNetworkKinematic>(
                kernel.FindPropertyRelative("m_Animim"));

            if (!changed) return false;

            if (applyWithUndo)
            {
                serialized.ApplyModifiedProperties();
            }
            else
            {
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            animator ??= character.GetComponentInChildren<Animator>(true);
            mannequin ??= animator != null ? animator.transform.parent : null;
            if (character.Animim != null)
            {
                if (character.Animim.Mannequin == null)
                {
                    character.Animim.Mannequin = mannequin;
                }

                if (character.Animim.Animator == null)
                {
                    character.Animim.Animator = animator;
                }
            }

            EditorUtility.SetDirty(character);
            return true;
        }

        private static bool SetManagedReferenceIfDifferent<T>(SerializedProperty property)
            where T : class, new()
        {
            if (property == null ||
                property.propertyType != SerializedPropertyType.ManagedReference ||
                property.managedReferenceValue is T)
            {
                return false;
            }

            property.managedReferenceValue = new T();
            return true;
        }
    }
}
