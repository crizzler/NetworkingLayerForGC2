using System;
using System.IO;
using System.Reflection;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Describes who owns the Malbers <c>MAnimal</c> root while a character is network-active.
    /// </summary>
    public enum NetworkMalbersMotionMode
    {
        /// <summary>
        /// No Malbers-aware bridge should run. This covers offline characters, unknown custom
        /// drivers, and objects without a resolved network role.
        /// </summary>
        Inactive = 0,

        /// <summary>
        /// The active GC2 driver is Malbers' own <c>UnitDriverMAnimal</c> (or a subclass).
        /// MAnimal remains the authoritative movement, rotation, and root-motion owner.
        /// </summary>
        NativeAuthority = 1,

        /// <summary>
        /// A Networking Layer driver owns the root transform (prediction, server simulation,
        /// or remote interpolation). MAnimal must present the movement without writing the root.
        /// </summary>
        NetworkPresentation = 2
    }

    /// <summary>
    /// Transport-neutral decisions used by the optional Malbers Animal Controller companion.
    /// This type intentionally has no compile-time dependency on Malbers assemblies so the main
    /// package still compiles and ships when the optional integration is absent.
    /// </summary>
    public static class NetworkMalbersIntegrationPolicy
    {
        public const string MalbersAnimalTypeName = "MalbersAnimations.Controller.MAnimal";
        public const string MalbersNativeDriverTypeName =
            "MalbersAnimations.Integrations.GameCreator2.UnitDriverMAnimal";
        public const string MotorBridgeTypeName =
            "Arawn.GameCreator2.Networking.Malbers.NetworkMalbersMotorBridge";
        public const string StateBridgeTypeName =
            "Arawn.GameCreator2.Networking.Malbers.NetworkMalbersStateBridge";

        private const string MainAssemblyName = "Arawn.GameCreator2.Networking";
        private const string TransportAssemblyPrefix =
            "Arawn.GameCreator2.Networking.Transport";
        private const float MinimumDirectionSqrMagnitude = 0.000001f;
        private const float MinimumReferenceSpeed = 0.01f;

        /// <summary>
        /// Finds a loaded runtime type without requiring a compile-time assembly reference.
        /// </summary>
        public static Type FindLoadedType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

#pragma warning disable UAC0005 // AppDomain reflection keeps this optional integration free of hard package references.
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
#pragma warning restore UAC0005
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                if (assembly == null || assembly.IsDynamic || assembly.ReflectionOnly) continue;

                try
                {
                    Type type = assembly.GetType(fullName, false, false);
                    if (type != null) return type;
                }
                catch (FileLoadException)
                {
                    // A partially replaced package assembly is ignored until the next reload.
                }
                catch (BadImageFormatException)
                {
                    // Native and incompatible plugin assemblies cannot contain the managed API.
                }
                catch (TypeLoadException)
                {
                    // A partially compiled assembly can expose a type that cannot be resolved.
                }
            }

            return null;
        }

        public static Type FindMalbersAnimalType() => FindLoadedType(MalbersAnimalTypeName);

        public static Type FindMotorBridgeType() => FindLoadedType(MotorBridgeTypeName);

        public static Type FindStateBridgeType() => FindLoadedType(StateBridgeTypeName);

        /// <summary>
        /// Returns true when this GameObject has a Malbers Animal Controller component.
        /// </summary>
        public static bool HasMalbersAnimal(GameObject host)
        {
            if (host == null) return false;

            Type animalType = FindMalbersAnimalType();
            return animalType != null && host.GetComponent(animalType) != null;
        }

        /// <summary>
        /// Adds the optional Assembly-CSharp bridge when it is available and not authored yet.
        /// </summary>
        public static MonoBehaviour EnsureMotorBridge(GameObject host)
        {
            if (host == null) return null;

            Type bridgeType = FindMotorBridgeType();
            if (bridgeType == null ||
                !typeof(Component).IsAssignableFrom(bridgeType) ||
                bridgeType.IsAbstract)
            {
                return null;
            }

            Component existing = host.GetComponent(bridgeType);
            if (existing != null) return existing as MonoBehaviour;

            Component created = host.AddComponent(bridgeType);
            return created as MonoBehaviour;
        }

        /// <summary>
        /// Adds the optional native-state bridge when the companion is compiled. Unlike the motor
        /// bridge it is harmless without a transport; it remains idle until a role is assigned.
        /// </summary>
        public static MonoBehaviour EnsureStateBridge(GameObject host)
        {
            if (host == null) return null;

            Type bridgeType = FindStateBridgeType();
            if (bridgeType == null ||
                !typeof(Component).IsAssignableFrom(bridgeType) ||
                bridgeType.IsAbstract)
            {
                return null;
            }

            Component existing = host.GetComponent(bridgeType);
            if (existing != null) return existing as MonoBehaviour;

            Component created = host.AddComponent(bridgeType);
            return created as MonoBehaviour;
        }

        public static bool IsNativeDriverTypeName(string fullName)
        {
            return string.Equals(fullName, MalbersNativeDriverTypeName, StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns whether the type, or any base type, is Malbers' GC2 movement driver.
        /// Subclasses of <c>UnitDriverMAnimal</c> keep native MAnimal ownership.
        /// </summary>
        public static bool IsNativeDriverHierarchy(Type driverType)
        {
            for (Type current = driverType; current != null; current = current.BaseType)
            {
                if (IsNativeDriverTypeName(current.FullName)) return true;
            }

            return false;
        }

        /// <summary>
        /// Returns whether the driver belongs to this package or one of its transport assemblies.
        /// Test and unrelated project assemblies are deliberately excluded.
        /// </summary>
        public static bool IsNetworkLayerDriverType(Type driverType)
        {
            if (driverType == null) return false;

            Assembly assembly = driverType.Assembly;
            string assemblyName = assembly != null ? assembly.GetName().Name : null;
            if (string.IsNullOrEmpty(assemblyName)) return false;

            return string.Equals(assemblyName, MainAssemblyName, StringComparison.Ordinal) ||
                   assemblyName.StartsWith(TransportAssemblyPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Recognizes custom subclasses of the built-in network drivers without requiring the
        /// subclass itself to live in a Networking Layer assembly.
        /// </summary>
        public static bool IsNetworkLayerDriverHierarchy(Type driverType)
        {
            for (Type current = driverType; current != null; current = current.BaseType)
            {
                if (IsNetworkLayerDriverType(current)) return true;
            }

            return false;
        }

        public static NetworkMalbersMotionMode ResolveMode(
            bool initialized,
            NetworkCharacter.NetworkRole role,
            IUnitDriver driver)
        {
            return ResolveMode(initialized, role, driver != null ? driver.GetType() : null);
        }

        public static NetworkMalbersMotionMode ResolveMode(
            bool initialized,
            NetworkCharacter.NetworkRole role,
            Type driverType)
        {
            if (!initialized ||
                role == NetworkCharacter.NetworkRole.None ||
                driverType == null)
            {
                return NetworkMalbersMotionMode.Inactive;
            }

            if (IsNativeDriverHierarchy(driverType))
            {
                return NetworkMalbersMotionMode.NativeAuthority;
            }

            return IsNetworkLayerDriverHierarchy(driverType)
                ? NetworkMalbersMotionMode.NetworkPresentation
                : NetworkMalbersMotionMode.Inactive;
        }

        /// <summary>
        /// Converts an authoritative or interpolated world velocity into MAnimal's local input
        /// axis. The result is clamped to <paramref name="maxAxis"/> so MAnimal's locomotion
        /// animator sees a normalized axis even when the network speed exceeds its reference.
        /// </summary>
        public static Vector3 ComputePresentationAxis(
            Vector3 worldMoveDirection,
            Transform characterTransform,
            float referenceSpeed,
            float maxAxis,
            bool suspended)
        {
            if (suspended || characterTransform == null) return Vector3.zero;
            if (worldMoveDirection.sqrMagnitude <= MinimumDirectionSqrMagnitude)
            {
                return Vector3.zero;
            }

            Vector3 localDirection =
                characterTransform.InverseTransformDirection(worldMoveDirection);
            if (localDirection.sqrMagnitude <= MinimumDirectionSqrMagnitude)
            {
                return Vector3.zero;
            }

            float speed = Mathf.Max(MinimumReferenceSpeed, referenceSpeed);
            float limit = Mathf.Max(0f, maxAxis);
            float magnitude = Mathf.Clamp(localDirection.magnitude / speed, 0f, limit);
            if (magnitude <= 0f) return Vector3.zero;

            return localDirection.normalized * magnitude;
        }
    }
}
