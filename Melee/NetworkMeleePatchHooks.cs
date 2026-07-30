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
    /// Runtime installer for Melee patch delegates. Suppresses client gameplay, routes strikes,
    /// and emits authoritative server reaction transitions.
    /// </summary>
    public class NetworkMeleePatchHooks : NetworkSingleton<NetworkMeleePatchHooks>
    {
        private bool m_IsServer;
        private bool m_Installed;
        private float m_NextPatchDiagnosticTime;
        private float m_NextInvariantWarningTime;

        public bool IsPatchActive => m_Installed && IsMeleePatched();

        public void Initialize(bool isServer)
        {
            m_IsServer = isServer;
            InstallHooks();
            LogPatchDiagnostic($"initialize server={isServer} installed={m_Installed} patched={IsMeleePatched()}");
        }

        /// <summary>Remove all static GC2 delegates when the owning manager shuts down.</summary>
        public void Shutdown()
        {
            UninstallHooks();
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
                    typeof(MeleeStance),
                    "NetworkReactionStarted",
                    typeof(Action<MeleeStance, GameObject, ReactionInput, IReaction>)) &&
                HasPublicStaticField(
                    typeof(Skill),
                    "NetworkOnHitValidator",
                    typeof(Func<Skill, Args, Vector3, Vector3, bool>)) &&
                HasPublicStaticField(
                    typeof(AttackSkill),
                    "NetworkStrikeValidator",
                    typeof(Func<MeleeStance, StrikeOutput, Skill, bool>)) &&
                HasPublicStaticField(
                    typeof(AttackSkill),
                    "NetworkStrikeProbe",
                    typeof(Action<MeleeStance, Skill, int, int>)) &&
                HasPublicStaticField(
                    typeof(AttackSkill),
                    "NetworkSkillStarted",
                    typeof(Action<MeleeStance, MeleeWeapon, Skill, int>)) &&
                HasPublicStaticField(
                    typeof(TShieldResponse),
                    "NetworkReactionResolved",
                    typeof(Action<
                        TShieldResponse,
                        Args,
                        ShieldOutput,
                        ReactionInput,
                        Reaction,
                        ReactionOutput>)) &&
                HasPublicStaticProperty(typeof(MeleeStance), "IsNetworkingActive", typeof(bool)) &&
                HasPublicStaticProperty(typeof(Skill), "IsNetworkingActive", typeof(bool)) &&
                HasInstanceMethod(typeof(MeleeStance), "InputChargeDirect", typeof(MeleeKey)) &&
                HasInstanceMethod(typeof(MeleeStance), "InputExecuteDirect", typeof(MeleeKey)) &&
                HasInstanceMethod(typeof(MeleeStance), "PlaySkillDirect", typeof(MeleeWeapon), typeof(Skill), typeof(GameObject)) &&
                HasInstanceMethod(typeof(MeleeStance), "PlayReactionDirect", typeof(GameObject), typeof(ReactionInput), typeof(IReaction), typeof(bool)) &&
                HasInstanceMethod(typeof(MeleeStance), "HitDirect", typeof(Character), typeof(ReactionInput), typeof(Skill)) &&
                HasInstanceMethodReturning(
                    typeof(Skill),
                    "NetworkResolveDefenseDirect",
                    typeof(BlockType),
                    typeof(Character),
                    typeof(Character),
                    typeof(Vector3),
                    typeof(Vector3),
                    typeof(float)) &&
                HasInstanceMethod(typeof(Skill), "OnHit", typeof(Args), typeof(Vector3), typeof(Vector3));
        }

        private void InstallHooks()
        {
            if (m_Installed) return;
            if (!IsMeleePatched())
            {
                WarnInvariant(
                    "The required GC2 Melee source patch is missing or stale. Networked hits " +
                    "cannot be intercepted safely. Apply it from Game Creator > Networking " +
                    "Layer > Patches > Melee > Patch (Server Authority).");
                return;
            }

            SetStaticField(typeof(MeleeStance), "NetworkInputChargeValidator", new Func<MeleeStance, MeleeKey, bool>(ValidateInputCharge));
            SetStaticField(typeof(MeleeStance), "NetworkInputExecuteValidator", new Func<MeleeStance, MeleeKey, bool>(ValidateInputExecute));
            SetStaticField(typeof(MeleeStance), "NetworkPlaySkillValidator", new Func<MeleeStance, MeleeWeapon, Skill, GameObject, bool>(ValidatePlaySkill));
            SetStaticField(typeof(MeleeStance), "NetworkPlayReactionValidator", new Func<MeleeStance, GameObject, ReactionInput, IReaction, bool>(ValidatePlayReaction));
            SetStaticField(typeof(MeleeStance), "NetworkReactionStarted", new Action<MeleeStance, GameObject, ReactionInput, IReaction>(OnReactionStarted));
            SetStaticField(typeof(Skill), "NetworkOnHitValidator", new Func<Skill, Args, Vector3, Vector3, bool>(ValidateOnHit));
            SetStaticField(typeof(AttackSkill), "NetworkStrikeValidator", new Func<MeleeStance, StrikeOutput, Skill, bool>(ValidateStrike));
            SetStaticField(typeof(AttackSkill), "NetworkStrikeProbe", new Action<MeleeStance, Skill, int, int>(ObserveStrikeProbe));
            SetStaticField(
                typeof(AttackSkill),
                "NetworkSkillStarted",
                new Action<MeleeStance, MeleeWeapon, Skill, int>(OnSkillStarted));
            SetStaticField(
                typeof(TShieldResponse),
                "NetworkReactionResolved",
                new Action<
                    TShieldResponse,
                    Args,
                    ShieldOutput,
                    ReactionInput,
                    Reaction,
                    ReactionOutput>(OnShieldReactionResolved));

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
            SetStaticField(typeof(MeleeStance), "NetworkReactionStarted", null);
            SetStaticField(typeof(Skill), "NetworkOnHitValidator", null);
            SetStaticField(typeof(AttackSkill), "NetworkStrikeValidator", null);
            SetStaticField(typeof(AttackSkill), "NetworkStrikeProbe", null);
            SetStaticField(typeof(AttackSkill), "NetworkSkillStarted", null);
            SetStaticField(typeof(TShieldResponse), "NetworkReactionResolved", null);

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

        private bool ValidatePlayReaction(MeleeStance stance, GameObject from, ReactionInput input, IReaction reaction)
        {
            if (!TryGetNetworkContext(
                    stance,
                    out NetworkCharacter networkCharacter,
                    out NetworkMeleeController controller))
            {
                return true;
            }

            if (controller == null)
            {
                WarnInvariant(
                    $"Blocked a melee reaction for network character '{networkCharacter.name}' " +
                    "because NetworkMeleeController is missing or not ready.");
                return false;
            }

            return m_IsServer;
        }

        private bool ValidateOnHit(Skill _, Args args, Vector3 __, Vector3 ___)
        {
            GameObject attacker = args?.Self;
            NetworkCharacter networkCharacter = attacker != null
                ? attacker.GetComponentInParent<NetworkCharacter>()
                : null;
            if (networkCharacter == null)
            {
                return true;
            }

            if (attacker.GetComponentInParent<NetworkMeleeController>() == null)
            {
                WarnInvariant(
                    $"Blocked legacy Skill.OnHit gameplay for network character " +
                    $"'{networkCharacter.name}' because NetworkMeleeController is missing or not ready.");
                return false;
            }

            // Compatibility for the previous Skill.OnHit-only patch. The new AttackSkill hook
            // suppresses the entire native branch before this callback is reached.
            return m_IsServer;
        }

        private void OnReactionStarted(
            MeleeStance stance,
            GameObject from,
            ReactionInput input,
            IReaction reaction)
        {
            if (!m_IsServer) return;

            NetworkCharacter sourceNetworkCharacter =
                from != null ? from.GetComponentInParent<NetworkCharacter>() : null;

            if (!TryGetNetworkContext(
                    stance,
                    out NetworkCharacter networkCharacter,
                    out NetworkMeleeController controller))
            {
                if (sourceNetworkCharacter != null)
                {
                    WarnInvariant(
                        $"Could not broadcast an authoritative reaction from network character " +
                        $"'{sourceNetworkCharacter.name}' because the target network context is missing.");
                }
                return;
            }

            if (controller == null)
            {
                WarnInvariant(
                    $"Could not broadcast an authoritative reaction for network character " +
                    $"'{networkCharacter.name}' because NetworkMeleeController is missing or not ready.");
                return;
            }

            controller.OnAuthoritativeReactionStarted(from, input, reaction);
        }

        private void OnShieldReactionResolved(
            TShieldResponse _,
            Args args,
            ShieldOutput __,
            ReactionInput input,
            Reaction reaction,
            ReactionOutput output)
        {
            if (!m_IsServer || args?.Self == null) return;

            Character target = args.Self.Get<Character>();
            if (target == null) return;

            NetworkCharacter networkCharacter = target.GetComponent<NetworkCharacter>();
            if (networkCharacter == null) return;

            NetworkMeleeController controller = target.GetComponent<NetworkMeleeController>();
            if (controller == null)
            {
                WarnInvariant(
                    $"Could not broadcast authored shield reaction for network character " +
                    $"'{networkCharacter.name}' because NetworkMeleeController is missing or not ready.");
                return;
            }

            controller.OnAuthoritativeShieldReactionResolved(
                args.Target,
                input,
                reaction,
                output);
        }

        private void ObserveStrikeProbe(
            MeleeStance stance,
            Skill _,
            int __,
            int candidateCount)
        {
            // This source hook remains functional: it performs no logging or probe tracking.
            // When collection is empty, it only restores a missing native target proxy so GC2
            // can run its normal volume and CanHit filtering on the following strike frame.
            if (candidateCount == 0)
            {
                EnsureIntendedTargetPhysicsProxy(stance);
            }
        }

        private bool ValidateStrike(MeleeStance stance, StrikeOutput output, Skill skill)
        {
            NetworkCharacter sourceNetworkCharacter =
                stance?.Character != null
                    ? stance.Character.GetComponent<NetworkCharacter>()
                    : null;

            if (!TryGetNetworkContext(stance, out NetworkCharacter networkCharacter, out NetworkMeleeController controller))
            {
                if (sourceNetworkCharacter != null)
                {
                    WarnInvariant(
                        $"Could not route a melee strike from network character " +
                        $"'{sourceNetworkCharacter.name}' because its network context is missing.");
                }
                return true;
            }

            if (controller == null)
            {
                WarnInvariant(
                    $"Suppressed a melee strike for network character '{networkCharacter.name}' " +
                    "because NetworkMeleeController is missing or not ready.");
                return false;
            }

            return controller.InterceptStrikeOutput(output, skill);
        }

        private void OnSkillStarted(
            MeleeStance stance,
            MeleeWeapon weapon,
            Skill skill,
            int comboNodeId)
        {
            if (!TryGetNetworkContext(
                    stance,
                    out NetworkCharacter networkCharacter,
                    out NetworkMeleeController controller))
            {
                return;
            }

            // Anticipation is the earliest exact skill boundary at which the intended target is
            // known. Flush and, only if needed, rebuild its native query proxy before Strike.
            EnsureIntendedTargetPhysicsProxy(stance);

            if (controller == null)
            {
                WarnInvariant(
                    $"Could not route the started melee skill for network character " +
                    $"'{networkCharacter.name}' because NetworkMeleeController is missing or not ready.");
                return;
            }

            try
            {
                controller.OnPatchedAttackSkillStarted(stance, weapon, skill, comboNodeId);
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"[NetworkMeleeController] Failed to route started Skill " +
                    $"'{(skill != null ? skill.name : "null")}' for '{networkCharacter.name}': " +
                    $"{exception.GetBaseException().Message}\n{exception.StackTrace}",
                    controller);
            }
        }

        private bool EnsureIntendedTargetPhysicsProxy(MeleeStance stance)
        {
            GameObject target = stance?.Args?.Target;
            Character targetCharacter = target != null
                ? target.GetComponentInParent<Character>()
                : null;

            if (targetCharacter?.Driver is not UnitDriverNetworkServer serverDriver)
            {
                return true;
            }

            bool queryable = serverDriver.EnsureControllerPhysicsQueryable();
            if (!queryable)
            {
                WarnInvariant(
                    $"Server target '{targetCharacter.name}' remained absent from physics " +
                    "queries after its CharacterController repair. The current strike remains " +
                    "subject to GC2's normal candidate collection.");
            }

            return queryable;
        }

        private bool CanRunLocalOrServerMelee(MeleeStance stance)
        {
            if (!TryGetNetworkContext(stance, out NetworkCharacter networkCharacter, out NetworkMeleeController controller))
            {
                return true;
            }

            if (controller == null)
            {
                WarnInvariant(
                    $"Blocked melee input for network character '{networkCharacter.name}' " +
                    "because NetworkMeleeController is missing or not ready.");
                return false;
            }

            return m_IsServer || controller.IsLocalClient;
        }

        private static bool TryGetNetworkContext(
            MeleeStance stance,
            out NetworkCharacter networkCharacter,
            out NetworkMeleeController controller)
        {
            networkCharacter = null;
            controller = null;

            Character character = stance?.Character;
            if (character == null) return false;

            networkCharacter = character.GetComponent<NetworkCharacter>();
            if (networkCharacter == null) return false;

            controller = character.GetComponent<NetworkMeleeController>();
            return true;
        }

        private void WarnInvariant(string message)
        {
            const float WarningInterval = 10f;
            if (Time.unscaledTime < m_NextInvariantWarningTime) return;
            m_NextInvariantWarningTime = Time.unscaledTime + WarningInterval;
            Debug.LogWarning($"[NetworkMeleePatchHooks] {message}", this);
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

        private static bool HasInstanceMethodReturning(
            Type type,
            string methodName,
            Type returnType,
            params Type[] parameterTypes)
        {
            MethodInfo method = type.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                parameterTypes,
                null);

            return method != null && method.ReturnType == returnType;
        }
    }
}
#endif
