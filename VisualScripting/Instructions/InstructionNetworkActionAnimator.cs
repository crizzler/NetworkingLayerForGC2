using System;
using System.Reflection;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    public enum NetworkActionAnimatorParameterType
    {
        Boolean = 0,
        Float = 1,
        Integer = 2
    }

    [Serializable]
    public sealed class NetworkActionAnimatorStateVariant
    {
        [Header("Match Confirmed Payload")]
        [SerializeField] private NetworkActionVisualVariantKeyType m_KeyType =
            NetworkActionVisualVariantKeyType.Boolean;
        [SerializeField] private bool m_Boolean;
        [SerializeField] private int m_Number;
        [SerializeField] private string m_String = string.Empty;

        [Header("Authored Animator State")]
        [Tooltip("Use the full Animator state path, for example 'Base Layer.Open'.")]
        [SerializeField] private string m_FullStatePath = string.Empty;
        [Min(0)] [SerializeField] private int m_Layer;
        [Tooltip("Live cross-fade duration in seconds. Zero plays the state immediately.")]
        [Min(0f)] [SerializeField] private float m_LiveCrossFadeDuration;
        [Tooltip("Normalized pose selected directly when applying a late-join snapshot.")]
        [Range(0f, 1f)] [SerializeField] private float m_SnapshotNormalizedTime;
        [Tooltip("Only restarts an already active state for a live transient action. " +
                 "Persistent state and snapshots remain idempotent.")]
        [SerializeField] private bool m_RestartIfAlreadyActive;

        public NetworkActionVisualVariantKeyType KeyType => m_KeyType;
        public bool BooleanKey => m_Boolean;
        public int NumberKey => m_Number;
        public string StringKey => m_String ?? string.Empty;
        public string FullStatePath => m_FullStatePath ?? string.Empty;
        public int Layer => m_Layer;
        public float LiveCrossFadeDuration => Mathf.Max(0f, m_LiveCrossFadeDuration);
        public float SnapshotNormalizedTime => Mathf.Clamp01(m_SnapshotNormalizedTime);
        public bool RestartIfAlreadyActive => m_RestartIfAlreadyActive;

        internal bool Matches(in NetworkActionPayload payload)
        {
            return NetworkActionStateApplicationSupport.MatchesVariantKey(
                m_KeyType,
                m_Boolean,
                m_Number,
                m_String,
                in payload);
        }
    }

    internal static class NetworkActionAnimatorApplicationSupport
    {
        public static Animator ResolveAnimator(PropertyGetGameObject target, Args args)
        {
            GameObject gameObject = target?.Get(args);
            return gameObject != null ? gameObject.Get<Animator>() : null;
        }

        public static bool CanApply(Animator animator)
        {
            if (animator == null || animator.runtimeAnimatorController == null) return false;
            if (animator.GetComponentInParent<Character>() != null ||
                animator.GetComponentInParent<NetworkCharacter>() != null ||
                animator.GetComponentInParent<UnitAnimimNetworkController>() != null)
            {
                WarnCharacterAnimator(animator);
                return false;
            }

            if (animator.GetComponent<NetworkActionEndpoint>() != null ||
                animator.GetComponent<NetworkActionManager>() != null ||
                animator.GetComponent<NetworkTransportBridge>() != null ||
                !NetworkActionStateApplicationSupport.CanSetTransform(animator.gameObject))
            {
                Debug.LogWarning(
                    $"[Network Actions] Refused to drive Animator '{animator.name}' on a " +
                    "network identity, endpoint, movement, or transport root. Put the " +
                    "Animator on a presentation child instead.",
                    animator);
                return false;
            }

            Component[] components = animator.GetComponentsInParent<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null) continue;
                for (Type type = component.GetType(); type != null; type = type.BaseType)
                {
                    string name = type.Name;
                    if (name.IndexOf(
                            "NetworkMecanimAnimator",
                            StringComparison.OrdinalIgnoreCase) >= 0 ||
                        string.Equals(name, "NetworkAnimator", StringComparison.OrdinalIgnoreCase))
                    {
                        WarnCharacterAnimator(animator);
                        return false;
                    }
                }
            }

            if (HasExternalNetworkAnimatorDriver(animator))
            {
                WarnCharacterAnimator(animator);
                return false;
            }

            return true;
        }

        public static bool TryGetParameter(
            Animator animator,
            string parameterName,
            AnimatorControllerParameterType expectedType,
            out int parameterHash)
        {
            parameterHash = 0;
            if (animator == null || string.IsNullOrWhiteSpace(parameterName)) return false;
            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                AnimatorControllerParameter parameter = parameters[i];
                if (!string.Equals(parameter.name, parameterName, StringComparison.Ordinal))
                    continue;
                if (parameter.type != expectedType)
                {
                    Debug.LogWarning(
                        $"[Network Actions] Animator parameter '{parameterName}' on " +
                        $"'{animator.name}' is {parameter.type}, not {expectedType}.",
                        animator);
                    return false;
                }

                parameterHash = parameter.nameHash;
                return true;
            }

            Debug.LogWarning(
                $"[Network Actions] Animator parameter '{parameterName}' was not found on " +
                $"'{animator.name}'.",
                animator);
            return false;
        }

        public static int RoundAndClampToInt(float value)
        {
            if (value <= int.MinValue) return int.MinValue;
            if (value >= int.MaxValue) return int.MaxValue;
            return Mathf.RoundToInt(value);
        }

        private static void WarnCharacterAnimator(Animator animator)
        {
            Debug.LogWarning(
                $"[Network Actions] Refused to drive Animator '{animator?.name ?? "(missing)"}' " +
                "because it belongs to a GC2 Character or another network Animator. Use the " +
                "dedicated Network Character State/Gesture instructions instead.",
                animator);
        }

        private static bool HasExternalNetworkAnimatorDriver(Animator animator)
        {
            Transform searchRoot = FindTransportRoot(animator.transform) ??
                                   animator.transform.root;
            Component[] components = searchRoot.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null) continue;
                Type type = component.GetType();
                string typeName = type.Name;
                if (typeName.IndexOf(
                        "NetworkMecanimAnimator",
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    !string.Equals(
                        typeName,
                        "NetworkAnimator",
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                Animator driven = ReadAnimatorReference(component, type);
                if (driven == animator ||
                    (driven == null && component.gameObject == animator.gameObject))
                    return true;
            }

            return false;
        }

        private static Transform FindTransportRoot(Transform start)
        {
            for (Transform current = start; current != null; current = current.parent)
            {
                Component[] components = current.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null) continue;
                    for (Type type = component.GetType(); type != null; type = type.BaseType)
                    {
                        string name = type.FullName ?? type.Name;
                        if (name == "Fusion.NetworkObject" ||
                            name == "PurrNet.NetworkIdentity" ||
                            name.EndsWith("NetworkIdentity", StringComparison.Ordinal) ||
                            name.EndsWith("NetworkObject", StringComparison.Ordinal))
                            return current;
                    }
                }
            }

            return null;
        }

        private static Animator ReadAnimatorReference(Component component, Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                       BindingFlags.NonPublic;
            string[] memberNames = { "Animator", "animator", "_animator", "m_Animator" };
            for (int i = 0; i < memberNames.Length; i++)
            {
                FieldInfo field = type.GetField(memberNames[i], flags);
                if (field != null && typeof(Animator).IsAssignableFrom(field.FieldType))
                {
                    try { return field.GetValue(component) as Animator; }
                    catch (Exception) { return null; }
                }

                PropertyInfo property = type.GetProperty(memberNames[i], flags);
                if (property == null || !property.CanRead ||
                    !typeof(Animator).IsAssignableFrom(property.PropertyType) ||
                    property.GetIndexParameters().Length != 0)
                    continue;
                try { return property.GetValue(component) as Animator; }
                catch (Exception) { return null; }
            }

            return null;
        }
    }

    [Version(1, 0, 0)]
    [Title("Apply Network Animator Parameter")]
    [Description("Applies a confirmed Boolean or Number payload to an authored world-object Animator parameter")]
    [Category("Network/Actions/Apply/Animator Parameter")]
    [Parameter("Target", "World or prop Animator; Character and network Animator targets are refused")]
    [Parameter("Parameter Name", "Locally authored Animator parameter shared by every build")]
    [Keywords("Network", "Action", "State", "Animator", "Parameter", "Boolean", "Float", "Integer")]
    [Image(typeof(IconAnimator), ColorTheme.Type.Teal, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionNetworkApplyAnimatorParameter : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectTarget.Create();
        [SerializeField] private string m_ParameterName = string.Empty;
        [SerializeField] private NetworkActionAnimatorParameterType m_ParameterType =
            NetworkActionAnimatorParameterType.Boolean;
        [SerializeField] private bool m_FalseBoolean;
        [SerializeField] private bool m_TrueBoolean = true;

        public override string Title =>
            $"Apply Network Animator {m_ParameterName} to {m_Target}";

        protected override Task Run(Args args)
        {
            if (!NetworkActionStateApplicationSupport.TryGetPresentationPayload(
                    out NetworkActionPayload payload))
                return DefaultResult;

            Animator animator = NetworkActionAnimatorApplicationSupport.ResolveAnimator(
                m_Target, args);
            if (!NetworkActionAnimatorApplicationSupport.CanApply(animator))
                return DefaultResult;

            switch (m_ParameterType)
            {
                case NetworkActionAnimatorParameterType.Boolean:
                    if (payload.Type != NetworkActionPayloadType.Boolean ||
                        !NetworkActionAnimatorApplicationSupport.TryGetParameter(
                            animator,
                            m_ParameterName,
                            AnimatorControllerParameterType.Bool,
                            out int booleanHash))
                        return DefaultResult;
                    animator.SetBool(
                        booleanHash,
                        payload.BooleanValue ? m_TrueBoolean : m_FalseBoolean);
                    break;

                case NetworkActionAnimatorParameterType.Float:
                    if (payload.Type != NetworkActionPayloadType.Number ||
                        !float.IsFinite(payload.NumberValue) ||
                        !NetworkActionAnimatorApplicationSupport.TryGetParameter(
                            animator,
                            m_ParameterName,
                            AnimatorControllerParameterType.Float,
                            out int floatHash))
                        return DefaultResult;
                    animator.SetFloat(floatHash, payload.NumberValue);
                    break;

                case NetworkActionAnimatorParameterType.Integer:
                    if (payload.Type != NetworkActionPayloadType.Number ||
                        !float.IsFinite(payload.NumberValue) ||
                        !NetworkActionAnimatorApplicationSupport.TryGetParameter(
                            animator,
                            m_ParameterName,
                            AnimatorControllerParameterType.Int,
                            out int integerHash))
                        return DefaultResult;
                    animator.SetInteger(
                        integerHash,
                        NetworkActionAnimatorApplicationSupport.RoundAndClampToInt(
                            payload.NumberValue));
                    break;
            }

            return DefaultResult;
        }
    }

    [Version(1, 0, 0)]
    [Title("Apply Network Animator State")]
    [Description("Maps a confirmed Boolean, integral Number, or String payload to an allow-listed world-object Animator state")]
    [Category("Network/Actions/Apply/Animator State")]
    [Parameter("Target", "World or prop Animator; Character and network Animator targets are refused")]
    [Parameter("Variants", "Locally authored payload keys and full Animator state paths")]
    [Keywords("Network", "Action", "State", "Animator", "Play", "Cross Fade", "Snapshot")]
    [Image(typeof(IconAnimator), ColorTheme.Type.Teal, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionNetworkApplyAnimatorState : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectTarget.Create();
        [SerializeField] private NetworkActionAnimatorStateVariant[] m_Variants =
            Array.Empty<NetworkActionAnimatorStateVariant>();

        public override string Title => $"Apply Network Animator State to {m_Target}";

        protected override Task Run(Args args)
        {
            if (!NetworkActionStateApplicationSupport.TryGetPresentationPayload(
                    out NetworkActionPayload payload))
                return DefaultResult;
            if (payload.Type != NetworkActionPayloadType.Boolean &&
                payload.Type != NetworkActionPayloadType.Number &&
                payload.Type != NetworkActionPayloadType.String)
                return DefaultResult;

            NetworkActionAnimatorStateVariant variant = ResolveVariant(in payload);
            if (variant == null || string.IsNullOrWhiteSpace(variant.FullStatePath))
                return DefaultResult;

            Animator animator = NetworkActionAnimatorApplicationSupport.ResolveAnimator(
                m_Target, args);
            if (!NetworkActionAnimatorApplicationSupport.CanApply(animator))
                return DefaultResult;
            if (variant.Layer < 0 || variant.Layer >= animator.layerCount)
            {
                Debug.LogWarning(
                    $"[Network Actions] Animator layer {variant.Layer} is invalid for " +
                    $"'{animator.name}'.",
                    animator);
                return DefaultResult;
            }

            int stateHash = Animator.StringToHash(variant.FullStatePath);
            if (!animator.HasState(variant.Layer, stateHash))
            {
                Debug.LogWarning(
                    $"[Network Actions] Animator state '{variant.FullStatePath}' was not " +
                    $"found on layer {variant.Layer} of '{animator.name}'.",
                    animator);
                return DefaultResult;
            }

            NetworkActionExecutionContext context = NetworkActionContext.Current;
            if (context.IsSnapshot)
            {
                if (IsSnapshotStateConverged(
                        animator,
                        variant.Layer,
                        stateHash,
                        variant.SnapshotNormalizedTime))
                    return DefaultResult;

                animator.Play(
                    stateHash,
                    variant.Layer,
                    variant.SnapshotNormalizedTime);
                return DefaultResult;
            }

            bool stateIsActive = IsStateActive(animator, variant.Layer, stateHash);
            bool mayRestart = variant.RestartIfAlreadyActive &&
                              context.Broadcast.EffectKind ==
                              NetworkActionEffectKind.TransientEvent;
            if (stateIsActive && !mayRestart) return DefaultResult;

            if (variant.LiveCrossFadeDuration > 0f)
            {
                animator.CrossFadeInFixedTime(
                    stateHash,
                    variant.LiveCrossFadeDuration,
                    variant.Layer,
                    0f);
            }
            else
            {
                animator.Play(stateHash, variant.Layer, 0f);
            }

            return DefaultResult;
        }

        private NetworkActionAnimatorStateVariant ResolveVariant(
            in NetworkActionPayload payload)
        {
            NetworkActionAnimatorStateVariant[] variants = m_Variants ??
                                                            Array.Empty<NetworkActionAnimatorStateVariant>();
            for (int i = 0; i < variants.Length; i++)
            {
                NetworkActionAnimatorStateVariant variant = variants[i];
                if (variant != null && variant.Matches(in payload)) return variant;
            }

            return null;
        }

        private static bool IsStateActive(Animator animator, int layer, int stateHash)
        {
            if (animator.IsInTransition(layer))
                return animator.GetNextAnimatorStateInfo(layer).fullPathHash == stateHash;
            return animator.GetCurrentAnimatorStateInfo(layer).fullPathHash == stateHash;
        }

        private static bool IsSnapshotStateConverged(
            Animator animator,
            int layer,
            int stateHash,
            float snapshotNormalizedTime)
        {
            if (animator.IsInTransition(layer)) return false;
            AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(layer);
            if (current.fullPathHash != stateHash) return false;
            if (snapshotNormalizedTime <= 0.0001f) return true;
            return current.normalizedTime + 0.001f >= snapshotNormalizedTime;
        }
    }
}
