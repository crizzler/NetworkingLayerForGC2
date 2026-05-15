#if GC2_MELEE
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Melee;
using System;
using System.Reflection;

namespace Arawn.GameCreator2.Networking.Melee
{
    /// <summary>
    /// Runtime installer for Melee patch delegates. Enables patched-mode validation on server.
    /// </summary>
    public class NetworkMeleePatchHooks : NetworkSingleton<NetworkMeleePatchHooks>
    {
        private bool m_IsServer;
        private bool m_Installed;
        private float m_NextPatchDiagnosticTime;

        public bool IsPatchActive => m_Installed && IsMeleePatched();

        public void Initialize(bool isServer)
        {
            m_IsServer = isServer;
            if (m_IsServer) InstallHooks();
            else UninstallHooks();
            LogPatchDiagnostic($"initialize server={isServer} installed={m_Installed} patched={IsMeleePatched()}");
        }

        protected override void OnSingletonCleanup()
        {
            UninstallHooks();
        }

        public static bool IsMeleePatched()
        {
            return
                HasPublicStaticField(
                    typeof(MeleeStance),
                    "NetworkInputChargeValidator",
                    typeof(Func<MeleeStance, MeleeKey, bool>)) &&
                HasPublicStaticField(
                    typeof(MeleeStance),
                    "NetworkInputExecuteValidator",
                    typeof(Func<MeleeStance, MeleeKey, bool>)) &&
                HasPublicStaticField(
                    typeof(MeleeStance),
                    "NetworkPlaySkillValidator",
                    typeof(Func<MeleeStance, MeleeWeapon, Skill, GameObject, bool>)) &&
                HasPublicStaticField(
                    typeof(MeleeStance),
                    "NetworkPlayReactionValidator",
                    typeof(Func<MeleeStance, GameObject, ReactionInput, IReaction, bool>)) &&
                HasPublicStaticField(
                    typeof(Skill),
                    "NetworkOnHitValidator",
                    typeof(Func<Skill, Args, Vector3, Vector3, bool>)) &&
                HasPublicStaticProperty(typeof(MeleeStance), "IsNetworkingActive", typeof(bool)) &&
                HasPublicStaticProperty(typeof(Skill), "IsNetworkingActive", typeof(bool)) &&
                HasInstanceMethod(typeof(MeleeStance), "InputChargeDirect", typeof(MeleeKey)) &&
                HasInstanceMethod(typeof(MeleeStance), "InputExecuteDirect", typeof(MeleeKey)) &&
                HasInstanceMethod(typeof(MeleeStance), "PlaySkillDirect", typeof(MeleeWeapon), typeof(Skill), typeof(GameObject)) &&
                HasInstanceMethod(typeof(MeleeStance), "PlayReactionDirect", typeof(GameObject), typeof(ReactionInput), typeof(IReaction), typeof(bool)) &&
                HasInstanceMethod(typeof(Skill), "OnHit", typeof(Args), typeof(Vector3), typeof(Vector3));
        }

        private void InstallHooks()
        {
            if (m_Installed) return;
            if (!IsMeleePatched())
            {
                LogPatchDiagnostic("install skipped: GC2 melee package is not patched with network delegates");
                return;
            }

            SetStaticProperty(typeof(MeleeStance), "IsNetworkingActive", true);
            SetStaticProperty(typeof(Skill), "IsNetworkingActive", true);

            SetStaticField(typeof(MeleeStance), "NetworkInputChargeValidator", new Func<MeleeStance, MeleeKey, bool>(ValidateInputCharge));
            SetStaticField(typeof(MeleeStance), "NetworkInputExecuteValidator", new Func<MeleeStance, MeleeKey, bool>(ValidateInputExecute));
            SetStaticField(typeof(MeleeStance), "NetworkPlaySkillValidator", new Func<MeleeStance, MeleeWeapon, Skill, GameObject, bool>(ValidatePlaySkill));
            SetStaticField(typeof(MeleeStance), "NetworkPlayReactionValidator", new Func<MeleeStance, GameObject, ReactionInput, IReaction, bool>(ValidatePlayReaction));
            SetStaticField(typeof(Skill), "NetworkOnHitValidator", new Func<Skill, Args, Vector3, Vector3, bool>(ValidateOnHit));

            m_Installed = true;
            LogPatchDiagnostic("installed GC2 melee patch hooks");
        }

        private void UninstallHooks()
        {
            if (!m_Installed) return;

            SetStaticField(typeof(MeleeStance), "NetworkInputChargeValidator", null);
            SetStaticField(typeof(MeleeStance), "NetworkInputExecuteValidator", null);
            SetStaticField(typeof(MeleeStance), "NetworkPlaySkillValidator", null);
            SetStaticField(typeof(MeleeStance), "NetworkPlayReactionValidator", null);
            SetStaticField(typeof(Skill), "NetworkOnHitValidator", null);
            SetStaticProperty(typeof(MeleeStance), "IsNetworkingActive", false);
            SetStaticProperty(typeof(Skill), "IsNetworkingActive", false);

            m_Installed = false;
            LogPatchDiagnostic("uninstalled GC2 melee patch hooks");
        }

        private bool ValidateInputCharge(MeleeStance stance, MeleeKey _) => CanRunLocalOrServerMelee(stance);
        private bool ValidateInputExecute(MeleeStance stance, MeleeKey key)
        {
            bool allowed = CanRunLocalOrServerMelee(stance);
            if (!allowed)
            {
                LogPatchDiagnostic(
                    $"blocked patched InputExecute key={key} character={(stance?.Character != null ? stance.Character.name : "null")} " +
                    $"server={m_IsServer}");
            }

            return allowed;
        }

        private bool ValidatePlaySkill(MeleeStance stance, MeleeWeapon _, Skill __, GameObject ___) => CanRunLocalOrServerMelee(stance);
        private bool ValidatePlayReaction(MeleeStance _, GameObject __, ReactionInput ___, IReaction ____) => m_IsServer;
        private bool ValidateOnHit(Skill _, Args __, Vector3 ___, Vector3 ____) => m_IsServer;

        private bool CanRunLocalOrServerMelee(MeleeStance stance)
        {
            if (m_IsServer) return true;

            var character = stance?.Character;
            var controller = character != null
                ? character.GetComponent<NetworkMeleeController>()
                : null;

            return controller != null && controller.IsLocalClient;
        }

        private void LogPatchDiagnostic(string message)
        {
            if (!NetworkMeleeDebug.ForceInputLockDiagnostics &&
                !NetworkMeleeDebug.ForceSkillDiagnostics)
            {
                return;
            }

            if (Time.unscaledTime < m_NextPatchDiagnosticTime && !message.StartsWith("blocked", StringComparison.Ordinal))
            {
                return;
            }

            m_NextPatchDiagnosticTime = Time.unscaledTime + 0.5f;
            Debug.Log($"[NetworkMeleeSkillDebug][PatchHooks] {message}", this);
        }

        private static void SetStaticField(Type type, string fieldName, object value)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            field?.SetValue(null, value);
        }

        private static void SetStaticProperty(Type type, string propertyName, object value)
        {
            PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
            MethodInfo setter = property?.GetSetMethod(true);
            setter?.Invoke(null, new[] { value });
        }

        private static bool HasPublicStaticField(Type type, string fieldName, Type expectedFieldType)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            return field != null && expectedFieldType.IsAssignableFrom(field.FieldType);
        }

        private static bool HasPublicStaticProperty(Type type, string propertyName, Type expectedPropertyType)
        {
            PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
            return property != null && expectedPropertyType.IsAssignableFrom(property.PropertyType);
        }

        private static bool HasInstanceMethod(Type type, string methodName, params Type[] parameterTypes)
        {
            MethodInfo method = type.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                parameterTypes,
                null);

            return method != null;
        }
    }
}
#endif
