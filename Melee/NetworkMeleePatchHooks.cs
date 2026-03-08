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

        public bool IsPatchActive => m_Installed && IsMeleePatched();

        public void Initialize(bool isServer)
        {
            m_IsServer = isServer;
            if (m_IsServer) InstallHooks();
            else UninstallHooks();
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
            if (!IsMeleePatched()) return;

            SetStaticField(typeof(MeleeStance), "NetworkInputChargeValidator", new Func<MeleeStance, MeleeKey, bool>(ValidateInputCharge));
            SetStaticField(typeof(MeleeStance), "NetworkInputExecuteValidator", new Func<MeleeStance, MeleeKey, bool>(ValidateInputExecute));
            SetStaticField(typeof(MeleeStance), "NetworkPlaySkillValidator", new Func<MeleeStance, MeleeWeapon, Skill, GameObject, bool>(ValidatePlaySkill));
            SetStaticField(typeof(MeleeStance), "NetworkPlayReactionValidator", new Func<MeleeStance, GameObject, ReactionInput, IReaction, bool>(ValidatePlayReaction));
            SetStaticField(typeof(Skill), "NetworkOnHitValidator", new Func<Skill, Args, Vector3, Vector3, bool>(ValidateOnHit));

            m_Installed = true;
        }

        private void UninstallHooks()
        {
            if (!m_Installed) return;

            SetStaticField(typeof(MeleeStance), "NetworkInputChargeValidator", null);
            SetStaticField(typeof(MeleeStance), "NetworkInputExecuteValidator", null);
            SetStaticField(typeof(MeleeStance), "NetworkPlaySkillValidator", null);
            SetStaticField(typeof(MeleeStance), "NetworkPlayReactionValidator", null);
            SetStaticField(typeof(Skill), "NetworkOnHitValidator", null);

            m_Installed = false;
        }

        private bool ValidateInputCharge(MeleeStance _, MeleeKey __) => m_IsServer;
        private bool ValidateInputExecute(MeleeStance _, MeleeKey __) => m_IsServer;
        private bool ValidatePlaySkill(MeleeStance _, MeleeWeapon __, Skill ___, GameObject ____) => m_IsServer;
        private bool ValidatePlayReaction(MeleeStance _, GameObject __, ReactionInput ___, IReaction ____) => m_IsServer;
        private bool ValidateOnHit(Skill _, Args __, Vector3 ___, Vector3 ____) => m_IsServer;

        private static void SetStaticField(Type type, string fieldName, object value)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            field?.SetValue(null, value);
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
