using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Variables;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    public enum NetworkVariableInstructionValueType
    {
        Null = 0,
        String = 1,
        Number = 2,
        Integer = 3,
        Boolean = 4,
        Position = 5,
        Color = 6
    }

    [Serializable]
    public sealed class NetworkVariableInstructionValue
    {
        [SerializeField] private NetworkVariableInstructionValueType m_Type = NetworkVariableInstructionValueType.String;
        [SerializeField] private PropertyGetString m_String = new PropertyGetString("");
        [SerializeField] private PropertyGetDecimal m_Number = new PropertyGetDecimal(0f);
        [SerializeField] private PropertyGetInteger m_Integer = new PropertyGetInteger(0);
        [SerializeField] private PropertyGetBool m_Boolean = new PropertyGetBool(false);
        [SerializeField] private PropertyGetPosition m_Position = new PropertyGetPosition(Vector3.zero);
        [SerializeField] private PropertyGetColor m_Color = new PropertyGetColor(Color.white);

        public object Get(Args args)
        {
            return m_Type switch
            {
                NetworkVariableInstructionValueType.Null => null,
                NetworkVariableInstructionValueType.String => m_String.Get(args),
                NetworkVariableInstructionValueType.Number => (float)m_Number.Get(args),
                NetworkVariableInstructionValueType.Integer => Mathf.FloorToInt((float)m_Integer.Get(args)),
                NetworkVariableInstructionValueType.Boolean => m_Boolean.Get(args),
                NetworkVariableInstructionValueType.Position => m_Position.Get(args),
                NetworkVariableInstructionValueType.Color => m_Color.Get(args),
                _ => null
            };
        }
    }

    public static class NetworkVariableInstructionUtility
    {
        public static bool TryGetController(
            PropertyGetGameObject target,
            Args args,
            string instructionName,
            out NetworkVariableController controller)
        {
            controller = null;
            GameObject gameObject = target.Get(args);
            if (gameObject == null)
            {
                Debug.LogWarning($"[{instructionName}] Target GameObject resolved to null.");
                return false;
            }

            controller = gameObject.GetComponent<NetworkVariableController>();
            if (controller == null)
            {
                Debug.LogWarning($"[{instructionName}] '{gameObject.name}' has no NetworkVariableController.");
                return false;
            }

            return true;
        }

        public static bool TryGetActorNetworkId(
            PropertyGetGameObject actor,
            Args args,
            string instructionName,
            out uint actorNetworkId)
        {
            actorNetworkId = 0;
            GameObject gameObject = actor.Get(args);
            if (gameObject == null)
            {
                Debug.LogWarning($"[{instructionName}] Actor GameObject resolved to null.");
                return false;
            }

            NetworkCharacter networkCharacter = gameObject.GetComponent<NetworkCharacter>();
            if (networkCharacter == null || networkCharacter.NetworkId == 0)
            {
                Debug.LogWarning($"[{instructionName}] '{gameObject.name}' has no initialized NetworkCharacter.");
                return false;
            }

            actorNetworkId = networkCharacter.NetworkId;
            return true;
        }

        public static bool TryGetManager(string instructionName, out NetworkVariableManager manager)
        {
            manager = NetworkVariableManager.Instance;
            if (manager != null) return true;

            Debug.LogWarning($"[{instructionName}] NetworkVariableManager is missing.");
            return false;
        }

        public static int GetIndex(PropertyGetInteger property, Args args)
        {
            return Mathf.FloorToInt((float)property.Get(args));
        }
    }
}
