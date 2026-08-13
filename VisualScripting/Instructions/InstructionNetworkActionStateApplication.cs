using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    public enum NetworkActionTransformSpace
    {
        Local = 0,
        World = 1
    }

    public enum NetworkActionVisualVariantKeyType
    {
        Boolean = 0,
        Number = 1,
        String = 2
    }

    [Serializable]
    public sealed class NetworkActionVisualVariant
    {
        [Header("Match Confirmed Payload")]
        [SerializeField] private NetworkActionVisualVariantKeyType m_KeyType =
            NetworkActionVisualVariantKeyType.Boolean;
        [SerializeField] private bool m_Boolean;
        [SerializeField] private int m_Number;
        [SerializeField] private string m_String = string.Empty;

        [Header("Local Authored Visuals")]
        [SerializeField] private bool m_ApplyMesh;
        [SerializeField] private Mesh m_Mesh;
        [SerializeField] private bool m_ApplyMaterials;
        [SerializeField] private Material[] m_Materials = Array.Empty<Material>();
        [SerializeField] private bool m_ApplySprite;
        [SerializeField] private Sprite m_Sprite;

        public NetworkActionVisualVariantKeyType KeyType => m_KeyType;
        public bool BooleanKey => m_Boolean;
        public int NumberKey => m_Number;
        public string StringKey => m_String ?? string.Empty;

        internal bool Matches(in NetworkActionPayload payload)
        {
            return NetworkActionStateApplicationSupport.MatchesVariantKey(
                m_KeyType,
                m_Boolean,
                m_Number,
                m_String,
                in payload);
        }

        internal void Apply(GameObject target)
        {
            if (target == null) return;

            if (m_ApplyMesh)
            {
                MeshFilter meshFilter = target.GetComponent<MeshFilter>();
                if (meshFilter != null) meshFilter.sharedMesh = m_Mesh;

                SkinnedMeshRenderer skinnedMesh = target.GetComponent<SkinnedMeshRenderer>();
                if (skinnedMesh != null) skinnedMesh.sharedMesh = m_Mesh;
            }

            if (m_ApplyMaterials)
            {
                Renderer renderer = target.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.sharedMaterials = m_Materials != null
                        ? (Material[])m_Materials.Clone()
                        : Array.Empty<Material>();
                }
            }

            if (m_ApplySprite)
            {
                SpriteRenderer spriteRenderer = target.GetComponent<SpriteRenderer>();
                if (spriteRenderer != null) spriteRenderer.sprite = m_Sprite;
            }
        }
    }

    internal static class NetworkActionStateApplicationSupport
    {
        public static bool TryGetPayload(
            NetworkActionPayloadType expected,
            out NetworkActionPayload payload)
        {
            return TryGetApplicationPayload(out payload) && payload.Type == expected;
        }

        public static bool TryGetApplicationPayload(out NetworkActionPayload payload)
        {
            payload = default;
            if (!NetworkActionContext.HasCurrent) return false;
            NetworkActionExecutionContext context = NetworkActionContext.Current;
            if (context.Phase != NetworkActionContextPhase.AuthorityCommit &&
                context.Phase != NetworkActionContextPhase.Applied &&
                context.Phase != NetworkActionContextPhase.SnapshotApplied)
            {
                return false;
            }

            payload = context.Payload;
            return true;
        }

        public static bool TryGetPresentationPayload(out NetworkActionPayload payload)
        {
            payload = default;
            if (!NetworkActionContext.HasCurrent) return false;
            NetworkActionExecutionContext context = NetworkActionContext.Current;
            if (context.Phase != NetworkActionContextPhase.Applied &&
                context.Phase != NetworkActionContextPhase.SnapshotApplied)
            {
                return false;
            }

            payload = context.Payload;
            return true;
        }

        public static bool MatchesVariantKey(
            NetworkActionVisualVariantKeyType keyType,
            bool booleanKey,
            int numberKey,
            string stringKey,
            in NetworkActionPayload payload)
        {
            return keyType switch
            {
                NetworkActionVisualVariantKeyType.Boolean =>
                    payload.Type == NetworkActionPayloadType.Boolean &&
                    payload.BooleanValue == booleanKey,
                NetworkActionVisualVariantKeyType.Number =>
                    payload.Type == NetworkActionPayloadType.Number &&
                    float.IsFinite(payload.NumberValue) &&
                    IsExactIntegerKey(payload.NumberValue, numberKey),
                NetworkActionVisualVariantKeyType.String =>
                    payload.Type == NetworkActionPayloadType.String && string.Equals(
                        payload.StringValue ?? string.Empty,
                        stringKey ?? string.Empty,
                        StringComparison.Ordinal),
                _ => false
            };
        }

        private static bool IsExactIntegerKey(float value, int expected)
        {
            double exact = value;
            if (exact < int.MinValue || exact > int.MaxValue ||
                exact != Math.Truncate(exact))
                return false;
            return (int)exact == expected;
        }

        public static bool CanSetInactive(GameObject target)
        {
            if (target == null) return false;

            NetworkActionEndpoint currentEndpoint = ResolveCurrentEndpoint();
            if (currentEndpoint != null &&
                currentEndpoint.transform.IsChildOf(target.transform))
            {
                return false;
            }

            Component[] components = target.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                if (IsCriticalNetworkComponent(components[i])) return false;
            }

            return true;
        }

        public static bool CanDisable(Behaviour behaviour)
        {
            return behaviour != null && !IsCriticalNetworkComponent(behaviour);
        }

        public static bool CanSetTransform(GameObject target)
        {
            if (target == null || target.GetComponent<Character>() != null ||
                target.GetComponent<NetworkCharacter>() != null)
            {
                return false;
            }

            Component[] components = target.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null) continue;
                Type type = component.GetType();
                for (Type current = type; current != null; current = current.BaseType)
                {
                    string name = current.Name;
                    string fullName = current.FullName ?? name;
                    if (name.IndexOf(
                            "NetworkTransform",
                            StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf(
                            "NetworkRigidbody",
                            StringComparison.OrdinalIgnoreCase) >= 0 ||
                        string.Equals(name, "KCC", StringComparison.OrdinalIgnoreCase) ||
                        fullName.EndsWith("NetworkIdentity", StringComparison.Ordinal) ||
                        fullName.EndsWith("NetworkObject", StringComparison.Ordinal) ||
                        fullName == "Fusion.NetworkBehaviour" ||
                        fullName == "PurrNet.NetworkBehaviour")
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static NetworkActionEndpoint ResolveCurrentEndpoint()
        {
            GameObject target = NetworkActionContext.HasCurrent
                ? NetworkActionContext.Current.Target
                : null;
            return target != null
                ? target.GetComponent<NetworkActionEndpoint>() ??
                  target.GetComponentInParent<NetworkActionEndpoint>() ??
                  target.GetComponentInChildren<NetworkActionEndpoint>(true)
                : null;
        }

        private static bool IsCriticalNetworkComponent(Component component)
        {
            if (component == null) return false;
            if (component is NetworkActionEndpoint ||
                component is NetworkActionManager ||
                component is NetworkTransportBridge ||
                component is NetworkCharacter ||
                component is NetworkVariableController ||
                component is INetworkActionAuthorityHandler)
            {
                return true;
            }

            Type type = component.GetType();
            string fullName = type.FullName ?? type.Name;
            string nameSpace = type.Namespace ?? string.Empty;
            if (nameSpace.StartsWith("Arawn.GameCreator2.Networking", StringComparison.Ordinal) ||
                nameSpace.StartsWith("Fusion", StringComparison.Ordinal) ||
                nameSpace.StartsWith("PurrNet", StringComparison.Ordinal))
            {
                return true;
            }

            for (Type current = type; current != null; current = current.BaseType)
            {
                string currentName = current.FullName ?? current.Name;
                if (currentName == "Fusion.NetworkObject" ||
                    currentName == "Fusion.NetworkBehaviour" ||
                    currentName == "PurrNet.NetworkIdentity" ||
                    currentName == "PurrNet.NetworkBehaviour")
                {
                    return true;
                }
            }

            return fullName.EndsWith("NetworkIdentity", StringComparison.Ordinal) ||
                   fullName.EndsWith("NetworkObject", StringComparison.Ordinal);
        }

        public static void WarnUnsafe(string operation, UnityEngine.Object target)
        {
            Debug.LogWarning(
                $"[Network Actions] Refused to {operation} '{target?.name ?? "(missing)"}' " +
                "because it contains the active endpoint or networking infrastructure. " +
                "Apply presentation state to a child object instead.",
                target);
        }

        public static void WarnUnsafeTransform(GameObject target)
        {
            Debug.LogWarning(
                $"[Network Actions] Refused to change transform state on " +
                $"'{target?.name ?? "(missing)"}' because it is a Character, KCC, " +
                "NetworkTransform, or transport identity root. Apply state to a presentation " +
                "child, or use the transport's movement/teleport system.",
                target);
        }
    }

    [Version(1, 0, 0)]
    [Title("Apply Network Boolean Transform State")]
    [Description("Applies absolute position, rotation, and scale values from the current confirmed boolean action payload")]
    [Category("Network/Actions/Apply/Boolean Transform State")]
    [Keywords("Network", "Action", "State", "Position", "Rotation", "Scale", "Door")]
    [Image(typeof(IconMove), ColorTheme.Type.Teal, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionNetworkApplyBooleanTransformState : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectTarget.Create();
        [SerializeField] private NetworkActionTransformSpace m_Space =
            NetworkActionTransformSpace.Local;

        [Header("Position")]
        [SerializeField] private bool m_ApplyPosition;
        [SerializeField] private PropertyGetPosition m_FalsePosition = new(Vector3.zero);
        [SerializeField] private PropertyGetPosition m_TruePosition = new(Vector3.zero);

        [Header("Rotation")]
        [SerializeField] private bool m_ApplyRotation = true;
        [SerializeField] private PropertyGetRotation m_FalseRotation =
            new(Quaternion.identity);
        [SerializeField] private PropertyGetRotation m_TrueRotation =
            new(Quaternion.Euler(0f, 90f, 0f));

        [Header("Scale")]
        [SerializeField] private bool m_ApplyScale;
        [SerializeField] private PropertyGetScale m_FalseScale = new(Vector3.one);
        [SerializeField] private PropertyGetScale m_TrueScale = new(Vector3.one);

        public override string Title => $"Apply Network Boolean Transform to {m_Target}";

        protected override Task Run(Args args)
        {
            if (!NetworkActionStateApplicationSupport.TryGetPayload(
                    NetworkActionPayloadType.Boolean, out NetworkActionPayload payload))
                return DefaultResult;

            GameObject target = m_Target.Get(args);
            if (target == null) return DefaultResult;
            if (!NetworkActionStateApplicationSupport.CanSetTransform(target))
            {
                NetworkActionStateApplicationSupport.WarnUnsafeTransform(target);
                return DefaultResult;
            }
            bool value = payload.BooleanValue;

            if (m_ApplyPosition)
            {
                Vector3 position = value
                    ? m_TruePosition.Get(args)
                    : m_FalsePosition.Get(args);
                if (m_Space == NetworkActionTransformSpace.Local)
                    target.transform.localPosition = position;
                else target.transform.position = position;
            }

            if (m_ApplyRotation)
            {
                Quaternion rotation = value
                    ? m_TrueRotation.Get(args)
                    : m_FalseRotation.Get(args);
                if (m_Space == NetworkActionTransformSpace.Local)
                    target.transform.localRotation = rotation;
                else target.transform.rotation = rotation;
            }

            if (m_ApplyScale)
            {
                target.transform.localScale = value
                    ? m_TrueScale.Get(args)
                    : m_FalseScale.Get(args);
            }

            return DefaultResult;
        }
    }

    [Version(1, 0, 0)]
    [Title("Apply Network Vector Position")]
    [Description("Applies an absolute position from the current confirmed Vector3 action payload")]
    [Category("Network/Actions/Apply/Vector Position")]
    [Keywords("Network", "Action", "State", "Position", "Vector")]
    [Image(typeof(IconVector3), ColorTheme.Type.Teal, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionNetworkApplyVectorPosition : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectTarget.Create();
        [SerializeField] private NetworkActionTransformSpace m_Space =
            NetworkActionTransformSpace.Local;

        public override string Title => $"Apply Network Vector Position to {m_Target}";

        protected override Task Run(Args args)
        {
            if (!NetworkActionStateApplicationSupport.TryGetPayload(
                    NetworkActionPayloadType.Vector3, out NetworkActionPayload payload))
                return DefaultResult;
            GameObject target = m_Target.Get(args);
            if (target == null) return DefaultResult;
            if (!NetworkActionStateApplicationSupport.CanSetTransform(target))
            {
                NetworkActionStateApplicationSupport.WarnUnsafeTransform(target);
                return DefaultResult;
            }
            if (m_Space == NetworkActionTransformSpace.Local)
                target.transform.localPosition = payload.Vector3Value;
            else target.transform.position = payload.Vector3Value;
            return DefaultResult;
        }
    }

    [Version(1, 0, 0)]
    [Title("Apply Network Boolean Active State")]
    [Description("Sets a presentation child active or inactive from the current confirmed boolean action payload")]
    [Category("Network/Actions/Apply/Boolean Active State")]
    [Keywords("Network", "Action", "State", "Active", "Enable", "Disable")]
    [Image(typeof(IconCubeSolid), ColorTheme.Type.Teal, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionNetworkApplyBooleanActiveState : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectTarget.Create();
        [SerializeField] private bool m_FalseActive;
        [SerializeField] private bool m_TrueActive = true;

        public override string Title => $"Apply Network Boolean Active State to {m_Target}";

        protected override Task Run(Args args)
        {
            if (!NetworkActionStateApplicationSupport.TryGetPayload(
                    NetworkActionPayloadType.Boolean, out NetworkActionPayload payload))
                return DefaultResult;
            GameObject target = m_Target.Get(args);
            if (target == null) return DefaultResult;
            bool active = payload.BooleanValue ? m_TrueActive : m_FalseActive;
            if (!active && !NetworkActionStateApplicationSupport.CanSetInactive(target))
            {
                NetworkActionStateApplicationSupport.WarnUnsafe("deactivate", target);
                return DefaultResult;
            }
            target.SetActive(active);
            return DefaultResult;
        }
    }

    [Version(1, 0, 0)]
    [Title("Apply Network Boolean Renderer State")]
    [Description("Sets Renderer enabled state from the current confirmed boolean action payload")]
    [Category("Network/Actions/Apply/Boolean Renderer State")]
    [Keywords("Network", "Action", "State", "Renderer", "Visible")]
    [Image(typeof(IconMaterial), ColorTheme.Type.Teal, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionNetworkApplyBooleanRendererState : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectTarget.Create();
        [SerializeField] private bool m_IncludeChildren;
        [SerializeField] private bool m_FalseEnabled;
        [SerializeField] private bool m_TrueEnabled = true;

        public override string Title => $"Apply Network Boolean Renderer State to {m_Target}";

        protected override Task Run(Args args)
        {
            if (!NetworkActionStateApplicationSupport.TryGetPayload(
                    NetworkActionPayloadType.Boolean, out NetworkActionPayload payload))
                return DefaultResult;
            GameObject target = m_Target.Get(args);
            if (target == null) return DefaultResult;
            bool enabled = payload.BooleanValue ? m_TrueEnabled : m_FalseEnabled;
            Renderer[] renderers = m_IncludeChildren
                ? target.GetComponentsInChildren<Renderer>(true)
                : target.GetComponents<Renderer>();
            for (int i = 0; i < renderers.Length; i++) renderers[i].enabled = enabled;
            return DefaultResult;
        }
    }

    [Version(1, 0, 0)]
    [Title("Apply Network Boolean Behaviour State")]
    [Description("Sets an allow-listed Behaviour type enabled state from the current confirmed boolean action payload")]
    [Category("Network/Actions/Apply/Boolean Behaviour State")]
    [Parameter("Target", "Game Object containing the authored Behaviour type")]
    [Parameter("Behaviour", "Behaviour type to update; networking infrastructure is refused")]
    [Keywords("Network", "Action", "State", "Component", "Behaviour", "Enable", "Disable")]
    [Image(typeof(IconComponent), ColorTheme.Type.Teal, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionNetworkApplyBooleanBehaviourState : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectTarget.Create();
        [SerializeField] private TypeReferenceBehaviour m_Behaviour = new();
        [SerializeField] private bool m_AllMatching;
        [SerializeField] private bool m_FalseEnabled;
        [SerializeField] private bool m_TrueEnabled = true;

        public override string Title => $"Apply Network Boolean {m_Behaviour} State";

        protected override Task Run(Args args)
        {
            if (!NetworkActionStateApplicationSupport.TryGetPayload(
                    NetworkActionPayloadType.Boolean, out NetworkActionPayload payload))
                return DefaultResult;
            GameObject target = m_Target.Get(args);
            Type type = m_Behaviour.Type;
            if (target == null || type == null || !typeof(Behaviour).IsAssignableFrom(type))
                return DefaultResult;
            bool enabled = payload.BooleanValue ? m_TrueEnabled : m_FalseEnabled;
            Behaviour[] behaviours = Array.ConvertAll(
                target.GetComponents(type), component => component as Behaviour);
            for (int i = 0; i < behaviours.Length; i++)
            {
                Behaviour behaviour = behaviours[i];
                if (behaviour == null) continue;
                if (!enabled && !NetworkActionStateApplicationSupport.CanDisable(behaviour))
                {
                    NetworkActionStateApplicationSupport.WarnUnsafe("disable", behaviour);
                    continue;
                }
                behaviour.enabled = enabled;
                if (!m_AllMatching) break;
            }
            return DefaultResult;
        }
    }

    [Version(1, 0, 0)]
    [Title("Apply Network Boolean Collider State")]
    [Description("Sets 3D and 2D Collider enabled and trigger state from the current confirmed boolean action payload")]
    [Category("Network/Actions/Apply/Boolean Collider State")]
    [Keywords("Network", "Action", "State", "Collider", "Trigger", "Physics")]
    [Image(typeof(IconPhysics), ColorTheme.Type.Teal, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionNetworkApplyBooleanColliderState : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectTarget.Create();
        [SerializeField] private bool m_IncludeChildren;
        [Header("Enabled")]
        [SerializeField] private bool m_ApplyEnabled = true;
        [SerializeField] private bool m_FalseEnabled;
        [SerializeField] private bool m_TrueEnabled = true;
        [Header("Is Trigger")]
        [SerializeField] private bool m_ApplyIsTrigger;
        [SerializeField] private bool m_FalseIsTrigger;
        [SerializeField] private bool m_TrueIsTrigger = true;

        public override string Title => $"Apply Network Boolean Collider State to {m_Target}";

        protected override Task Run(Args args)
        {
            if (!NetworkActionStateApplicationSupport.TryGetPayload(
                    NetworkActionPayloadType.Boolean, out NetworkActionPayload payload))
                return DefaultResult;
            GameObject target = m_Target.Get(args);
            if (target == null) return DefaultResult;
            bool value = payload.BooleanValue;
            Collider[] colliders = m_IncludeChildren
                ? target.GetComponentsInChildren<Collider>(true)
                : target.GetComponents<Collider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                if (m_ApplyIsTrigger)
                    colliders[i].isTrigger = value ? m_TrueIsTrigger : m_FalseIsTrigger;
                if (m_ApplyEnabled)
                    colliders[i].enabled = value ? m_TrueEnabled : m_FalseEnabled;
            }

            Collider2D[] colliders2D = m_IncludeChildren
                ? target.GetComponentsInChildren<Collider2D>(true)
                : target.GetComponents<Collider2D>();
            for (int i = 0; i < colliders2D.Length; i++)
            {
                if (m_ApplyIsTrigger)
                    colliders2D[i].isTrigger = value
                        ? m_TrueIsTrigger
                        : m_FalseIsTrigger;
                if (m_ApplyEnabled)
                    colliders2D[i].enabled = value ? m_TrueEnabled : m_FalseEnabled;
            }
            return DefaultResult;
        }
    }

    [Version(1, 0, 0)]
    [Title("Apply Network Boolean Layer State")]
    [Description("Selects one of two locally authored layers from the current confirmed boolean action payload")]
    [Category("Network/Actions/Apply/Boolean Layer State")]
    [Keywords("Network", "Action", "State", "Layer", "Collision")]
    [Image(typeof(IconLayers), ColorTheme.Type.Teal, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionNetworkApplyBooleanLayerState : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectTarget.Create();
        [SerializeField] private LayerMaskValue m_FalseLayer = new();
        [SerializeField] private LayerMaskValue m_TrueLayer = new();
        [SerializeField] private bool m_IncludeChildren;

        public override string Title => $"Apply Network Boolean Layer State to {m_Target}";

        protected override Task Run(Args args)
        {
            if (!NetworkActionStateApplicationSupport.TryGetPayload(
                    NetworkActionPayloadType.Boolean, out NetworkActionPayload payload))
                return DefaultResult;
            GameObject target = m_Target.Get(args);
            if (target == null) return DefaultResult;
            int layer = payload.BooleanValue ? m_TrueLayer.Value : m_FalseLayer.Value;
            if (layer < 0 || layer > 31) return DefaultResult;
            target.layer = layer;
            if (m_IncludeChildren)
            {
                Transform[] children = target.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < children.Length; i++) children[i].gameObject.layer = layer;
            }
            return DefaultResult;
        }
    }

    [Version(1, 0, 0)]
    [Title("Apply Network Boolean Tag State")]
    [Description("Selects one of two locally authored tags from the current confirmed boolean action payload")]
    [Category("Network/Actions/Apply/Boolean Tag State")]
    [Keywords("Network", "Action", "State", "Tag")]
    [Image(typeof(IconTag), ColorTheme.Type.Teal, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionNetworkApplyBooleanTagState : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectTarget.Create();
        [SerializeField] private TagValue m_FalseTag = new();
        [SerializeField] private TagValue m_TrueTag = new();
        [SerializeField] private bool m_IncludeChildren;

        public override string Title => $"Apply Network Boolean Tag State to {m_Target}";

        protected override Task Run(Args args)
        {
            if (!NetworkActionStateApplicationSupport.TryGetPayload(
                    NetworkActionPayloadType.Boolean, out NetworkActionPayload payload))
                return DefaultResult;
            GameObject target = m_Target.Get(args);
            if (target == null) return DefaultResult;
            string tag = payload.BooleanValue ? m_TrueTag.Value : m_FalseTag.Value;
            try
            {
                target.tag = tag;
                if (m_IncludeChildren)
                {
                    Transform[] children = target.GetComponentsInChildren<Transform>(true);
                    for (int i = 0; i < children.Length; i++) children[i].gameObject.tag = tag;
                }
            }
            catch (UnityException exception)
            {
                Debug.LogWarning(
                    $"[Network Actions] Could not apply authored tag '{tag}': " +
                    exception.Message,
                    target);
            }
            return DefaultResult;
        }
    }

    [Version(1, 0, 0)]
    [Title("Apply Network Visual Variant")]
    [Description("Maps a confirmed Boolean, integral Number, or String payload to locally authored mesh, material, and sprite assets")]
    [Category("Network/Actions/Apply/Visual Variant")]
    [Keywords("Network", "Action", "State", "Mesh", "Material", "Sprite", "Variant")]
    [Image(typeof(IconMaterial), ColorTheme.Type.Teal, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionNetworkApplyVisualVariant : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectTarget.Create();
        [SerializeField] private NetworkActionVisualVariant[] m_Variants =
            Array.Empty<NetworkActionVisualVariant>();

        public override string Title => $"Apply Network Visual Variant to {m_Target}";

        protected override Task Run(Args args)
        {
            if (!NetworkActionStateApplicationSupport.TryGetApplicationPayload(
                    out NetworkActionPayload payload))
                return DefaultResult;
            if (payload.Type != NetworkActionPayloadType.Boolean &&
                payload.Type != NetworkActionPayloadType.Number &&
                payload.Type != NetworkActionPayloadType.String)
                return DefaultResult;
            GameObject target = m_Target.Get(args);
            if (target == null) return DefaultResult;
            NetworkActionVisualVariant[] variants = m_Variants ??
                                                     Array.Empty<NetworkActionVisualVariant>();
            for (int i = 0; i < variants.Length; i++)
            {
                if (variants[i] == null || !variants[i].Matches(in payload)) continue;
                variants[i].Apply(target);
                break;
            }
            return DefaultResult;
        }
    }

    [Version(1, 0, 0)]
    [Title("Apply Network Boolean Character Controllable")]
    [Description("Applies the confirmed boolean action payload to the local GC2 Character input flag; this is not an anti-cheat authority boundary")]
    [Category("Network/Actions/Apply/Boolean Character Controllable")]
    [Keywords("Network", "Action", "Character", "Player", "Input", "Controllable", "Control")]
    [Image(typeof(IconPlayer), ColorTheme.Type.Teal, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionNetworkApplyBooleanCharacterControllable : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Character = GetGameObjectTarget.Create();
        [Tooltip("Only change the locally owned copy when a NetworkCharacter is present. Leave " +
                 "disabled for persistent state that must survive ownership transfer.")]
        [SerializeField] private bool m_LocalOwnerOnly;
        [SerializeField] private bool m_FalseControllable;
        [SerializeField] private bool m_TrueControllable = true;

        public override string Title => $"Apply Network Character Controllable to {m_Character}";

        protected override Task Run(Args args)
        {
            if (!NetworkActionStateApplicationSupport.TryGetPayload(
                    NetworkActionPayloadType.Boolean, out NetworkActionPayload payload))
                return DefaultResult;
            Character character = m_Character.Get<Character>(args);
            if (character == null || character.Player == null) return DefaultResult;
            NetworkCharacter networkCharacter = character.GetComponent<NetworkCharacter>() ??
                                                character.GetComponentInParent<NetworkCharacter>();
            if (m_LocalOwnerOnly && networkCharacter != null &&
                !networkCharacter.IsOwnerInstance)
                return DefaultResult;
            character.Player.IsControllable = payload.BooleanValue
                ? m_TrueControllable
                : m_FalseControllable;
            return DefaultResult;
        }
    }
}
