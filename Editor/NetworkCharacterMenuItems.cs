using UnityEngine;
using UnityEditor;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Editor.Characters;
using Arawn.GameCreator2.Networking;

namespace Arawn.EnemyMasses.Editor.GameCreator2
{
    /// <summary>
    /// Creates hierarchy context menu items for networked GC2 characters.
    /// Adds network character variants under "Game Creator/Characters/Network/"
    /// </summary>
    public static class NetworkCharacterMenuItems
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // MENU ITEMS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [MenuItem("GameObject/Game Creator/Characters/Network/Network Player", false, 10)]
        public static void CreateNetworkPlayer(MenuCommand menuCommand)
        {
            MakeNetworkCharacter(menuCommand, NetworkCharacterType.Player);
        }
        
        [MenuItem("GameObject/Game Creator/Characters/Network/Network Character (Server)", false, 11)]
        public static void CreateNetworkCharacterServer(MenuCommand menuCommand)
        {
            MakeNetworkCharacter(menuCommand, NetworkCharacterType.NPCServerAuthoritative);
        }
        
        [MenuItem("GameObject/Game Creator/Characters/Network/Network Character (Client-Side)", false, 12)]
        public static void CreateNetworkCharacterClientSide(MenuCommand menuCommand)
        {
            MakeNetworkCharacter(menuCommand, NetworkCharacterType.NPCClientSide);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CHARACTER TYPES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private enum NetworkCharacterType
        {
            Player,                  // Human-controlled with client prediction
            NPCServerAuthoritative,  // Server runs AI, broadcasts to clients
            NPCClientSide            // Lightweight: clients run AI locally
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CREATION METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private static void MakeNetworkCharacter(MenuCommand menuCommand, NetworkCharacterType type)
        {
            string objectName = type switch
            {
                NetworkCharacterType.Player => "Network Player",
                NetworkCharacterType.NPCServerAuthoritative => "Network Character (Server)",
                NetworkCharacterType.NPCClientSide => "Network Character (Client)",
                _ => "Network Character"
            };
            
            GameObject instance = new GameObject(objectName);
            
            // Add GC2 Character component
            Character character = instance.AddComponent<Character>();
            
            // Position at ground level + half height
            float height = character.Motion.Height;
            character.transform.position += Vector3.up * (height * 0.5f);
            
            // Load default GC2 assets
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterEditor.MODEL_PATH);
            MaterialSoundsAsset footsteps = AssetDatabase.LoadAssetAtPath<MaterialSoundsAsset>(FOOTSTEPS_PATH);
            RuntimeAnimatorController controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(RTC_PATH);
            
            // Apply model with default assets
            if (prefab != null)
            {
                character.ChangeModel(prefab, new Character.ChangeOptions 
                {
                    controller = controller,
                    materials = footsteps,
                    offset = Vector3.zero
                });
            }
            
            // Set player designation
            character.IsPlayer = type == NetworkCharacterType.Player;
            
            // Configure network-ready GC2 units via serialization
            ConfigureNetworkUnits(character, type);
            
            // Add NetworkCharacter component
            NetworkCharacter networkCharacter = instance.AddComponent<NetworkCharacter>();
            
            // Configure NetworkCharacter based on type
            ConfigureNetworkCharacterComponent(networkCharacter, type);
            
            // Parent to selected object if any
            GameObjectUtility.SetParentAndAlign(instance, menuCommand?.context as GameObject);
            
            // Register for undo
            Undo.RegisterCreatedObjectUndo(instance, $"Create {objectName}");
            
            // Select the new object
            Selection.activeObject = instance;
        }
        
        private static void ConfigureNetworkUnits(Character character, NetworkCharacterType type)
        {
            // Use SerializedObject to set the kernel units to network versions
            SerializedObject serializedCharacter = new SerializedObject(character);
            SerializedProperty kernelProperty = serializedCharacter.FindProperty("m_Kernel");
            
            if (kernelProperty == null) return;
            
            // Set Player unit for human-controlled characters
            if (type == NetworkCharacterType.Player)
            {
                SerializedProperty playerProperty = kernelProperty.FindPropertyRelative("m_Player");
                if (playerProperty != null)
                {
                    playerProperty.managedReferenceValue = new UnitPlayerDirectionalNetwork();
                }
            }
            
            // Set Motion unit to UnitMotionNetworkController (for all types)
            SerializedProperty motionProperty = kernelProperty.FindPropertyRelative("m_Motion");
            if (motionProperty != null)
            {
                motionProperty.managedReferenceValue = new UnitMotionNetworkController();
            }
            
            // Set Driver based on character type
            SerializedProperty driverProperty = kernelProperty.FindPropertyRelative("m_Driver");
            if (driverProperty != null)
            {
                switch (type)
                {
                    case NetworkCharacterType.Player:
                        // Network Player: Client-side prediction driver for responsive player input
                        driverProperty.managedReferenceValue = new UnitDriverNetworkClient();
                        break;
                        
                    case NetworkCharacterType.NPCServerAuthoritative:
                        // Server-Authoritative NPC: Server runs NavMesh pathfinding, broadcasts to clients
                        driverProperty.managedReferenceValue = new UnitDriverNavmeshNetworkServer();
                        break;
                        
                    case NetworkCharacterType.NPCClientSide:
                        // Client-Side NPC: Uses standard NavMesh driver, syncs only critical events
                        // Each client runs the AI locally with deterministic behavior
                        driverProperty.managedReferenceValue = new UnitDriverNavmesh();
                        break;
                }
            }
            
            serializedCharacter.ApplyModifiedPropertiesWithoutUndo();
        }
        
        private static void ConfigureNetworkCharacterComponent(NetworkCharacter networkCharacter, NetworkCharacterType type)
        {
            SerializedObject serializedNetChar = new SerializedObject(networkCharacter);
            
            // Set NPC mode for client-side characters
            SerializedProperty npcModeProperty = serializedNetChar.FindProperty("m_NPCMode");
            if (npcModeProperty != null)
            {
                npcModeProperty.enumValueIndex = type switch
                {
                    NetworkCharacterType.NPCServerAuthoritative => (int)NetworkCharacter.NPCSyncMode.ServerAuthoritative,
                    NetworkCharacterType.NPCClientSide => (int)NetworkCharacter.NPCSyncMode.ClientSideDeterministic,
                    _ => (int)NetworkCharacter.NPCSyncMode.ServerAuthoritative
                };
            }
            
            serializedNetChar.ApplyModifiedPropertiesWithoutUndo();
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PATHS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private const string FOOTSTEPS_PATH = RuntimePaths.CHARACTERS + "Assets/3D/Footsteps.asset";
        private const string RTC_PATH = RuntimePaths.CHARACTERS + "Assets/Controllers/CompleteLocomotion.controller";
    }
}
