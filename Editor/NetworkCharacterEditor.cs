using UnityEngine;
using UnityEditor;
using Arawn.GameCreator2.Networking;

namespace Arawn.EnemyMasses.Editor.GameCreator2
{
    /// <summary>
    /// Custom editor for NetworkCharacter component.
    /// Provides a clean inspector UI for network configuration.
    /// </summary>
    [CustomEditor(typeof(NetworkCharacter))]
    public class NetworkCharacterEditor : UnityEditor.Editor
    {
        // SERIALIZED PROPERTIES: -----------------------------------------------------------------
        
        private SerializedProperty m_IKMode;
        private SerializedProperty m_FootstepsMode;
        private SerializedProperty m_InteractionMode;
        private SerializedProperty m_CombatMode;
        
        // Core Feature System modes
        private SerializedProperty m_RagdollMode;
        private SerializedProperty m_PropsMode;
        private SerializedProperty m_InvincibilityMode;
        private SerializedProperty m_PoiseMode;
        private SerializedProperty m_BusyMode;
        
        private SerializedProperty m_UseNetworkIK;
        private SerializedProperty m_UseNetworkMotion;
        private SerializedProperty m_UseLagCompensation;
        private SerializedProperty m_UseAnimationSync;
        private SerializedProperty m_UseCoreNetworking;
        
        private SerializedProperty m_DisableVisualsOnServer;
        private SerializedProperty m_DisableAudioOnServer;
        
        // STYLES: --------------------------------------------------------------------------------
        
        private GUIStyle m_HeaderStyle;
        private GUIStyle m_BoxStyle;
        
        // INITIALIZATION: ------------------------------------------------------------------------
        
        private void OnEnable()
        {
            m_IKMode = serializedObject.FindProperty("m_IKMode");
            m_FootstepsMode = serializedObject.FindProperty("m_FootstepsMode");
            m_InteractionMode = serializedObject.FindProperty("m_InteractionMode");
            m_CombatMode = serializedObject.FindProperty("m_CombatMode");
            
            // Core Feature System modes
            m_RagdollMode = serializedObject.FindProperty("m_RagdollMode");
            m_PropsMode = serializedObject.FindProperty("m_PropsMode");
            m_InvincibilityMode = serializedObject.FindProperty("m_InvincibilityMode");
            m_PoiseMode = serializedObject.FindProperty("m_PoiseMode");
            m_BusyMode = serializedObject.FindProperty("m_BusyMode");
            
            m_UseNetworkIK = serializedObject.FindProperty("m_UseNetworkIK");
            m_UseNetworkMotion = serializedObject.FindProperty("m_UseNetworkMotion");
            m_UseLagCompensation = serializedObject.FindProperty("m_UseLagCompensation");
            m_UseAnimationSync = serializedObject.FindProperty("m_UseAnimationSync");
            m_UseCoreNetworking = serializedObject.FindProperty("m_UseCoreNetworking");
            
            m_DisableVisualsOnServer = serializedObject.FindProperty("m_DisableVisualsOnServer");
            m_DisableAudioOnServer = serializedObject.FindProperty("m_DisableAudioOnServer");
        }
        
        // INSPECTOR GUI: -------------------------------------------------------------------------
        
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            
            InitStyles();
            
            NetworkCharacter networkCharacter = (NetworkCharacter)target;
            
            // Header info box
            DrawInfoBox(networkCharacter);
            
            EditorGUILayout.Space(5);
            
            // Remote Character Systems
            DrawRemoteSystemsSection();
            
            EditorGUILayout.Space(5);
            
            // Core Feature Systems
            DrawCoreFeatureSystemsSection();
            
            EditorGUILayout.Space(5);
            
            // Optional Network Components
            DrawOptionalComponentsSection();
            
            EditorGUILayout.Space(5);
            
            // Server Optimization
            DrawServerOptimizationSection();
            
            EditorGUILayout.Space(5);
            
            // Runtime Info (play mode only)
            if (Application.isPlaying)
            {
                DrawRuntimeInfo(networkCharacter);
            }
            
            serializedObject.ApplyModifiedProperties();
        }
        
        private void InitStyles()
        {
            if (m_HeaderStyle == null)
            {
                m_HeaderStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 12
                };
            }
            
            if (m_BoxStyle == null)
            {
                m_BoxStyle = new GUIStyle("HelpBox")
                {
                    padding = new RectOffset(10, 10, 10, 10)
                };
            }
        }
        
        private void DrawInfoBox(NetworkCharacter networkCharacter)
        {
            EditorGUILayout.BeginVertical(m_BoxStyle);
            
            EditorGUILayout.LabelField("Network Character", m_HeaderStyle);
            EditorGUILayout.Space(2);
            
            EditorGUILayout.HelpBox(
                "This component automatically assigns the correct driver based on network role:\n" +
                "• Server → UnitDriverNetworkServer (authoritative)\n" +
                "• Local Player → UnitDriverNetworkClient (prediction)\n" +
                "• Remote Player → UnitDriverNetworkRemote (interpolation)\n\n" +
                "Configure the GC2 Character's Player and Motion units in the Character component above.",
                MessageType.Info
            );
            
            EditorGUILayout.EndVertical();
        }
        
        private void DrawRemoteSystemsSection()
        {
            EditorGUILayout.BeginVertical("box");
            
            EditorGUILayout.LabelField("Remote Character Systems", m_HeaderStyle);
            EditorGUILayout.Space(2);
            
            EditorGUILayout.HelpBox(
                "Configure how expensive systems behave on remote (other players') characters.",
                MessageType.None
            );
            
            EditorGUILayout.Space(5);
            
            EditorGUILayout.PropertyField(m_IKMode, new GUIContent("IK Mode", 
                "Synchronized: Sync look/aim over network\n" +
                "LocalOnly: Run IK locally from movement\n" +
                "Disabled: No IK on remote characters"));
            
            EditorGUILayout.PropertyField(m_FootstepsMode, new GUIContent("Footsteps Mode",
                "LocalOnly: Play footsteps based on local animation\n" +
                "Disabled: No footstep sounds on remotes"));
            
            EditorGUILayout.PropertyField(m_InteractionMode, new GUIContent("Interaction Mode",
                "Usually Disabled - remotes shouldn't trigger local interactions"));
            
            EditorGUILayout.PropertyField(m_CombatMode, new GUIContent("Combat Mode",
                "Usually Disabled - combat should be server-authoritative"));
            
            EditorGUILayout.EndVertical();
        }
        
        private void DrawCoreFeatureSystemsSection()
        {
            EditorGUILayout.BeginVertical("box");
            
            EditorGUILayout.LabelField("Core Feature Systems", m_HeaderStyle);
            EditorGUILayout.Space(2);
            
            EditorGUILayout.HelpBox(
                "Configure networking modes for GC2 Core features.\n" +
                "Synchronized: Server-authoritative with network sync\n" +
                "LocalOnly: Feature works locally without networking\n" +
                "Disabled: Feature disabled on remote characters",
                MessageType.None
            );
            
            EditorGUILayout.Space(5);
            
            EditorGUILayout.PropertyField(m_RagdollMode, new GUIContent("Ragdoll Mode",
                "Synchronized: Sync ragdoll start/recover over network\n" +
                "LocalOnly: Ragdoll runs from local events\n" +
                "Disabled: No ragdoll on remote characters"));
            
            EditorGUILayout.PropertyField(m_PropsMode, new GUIContent("Props Mode",
                "Synchronized: Sync prop attach/detach/drop over network\n" +
                "LocalOnly: Props managed locally\n" +
                "Disabled: No prop system on remotes"));
            
            EditorGUILayout.PropertyField(m_InvincibilityMode, new GUIContent("Invincibility Mode",
                "Synchronized: Server validates and syncs invincibility\n" +
                "LocalOnly: Invincibility managed locally\n" +
                "Disabled: No invincibility on remotes"));
            
            EditorGUILayout.PropertyField(m_PoiseMode, new GUIContent("Poise Mode",
                "Synchronized: Server tracks poise damage and breaks\n" +
                "LocalOnly: Poise managed locally\n" +
                "Disabled: No poise on remotes"));
            
            EditorGUILayout.PropertyField(m_BusyMode, new GUIContent("Busy Mode",
                "Synchronized: Sync busy limb states over network\n" +
                "LocalOnly: Busy states managed locally\n" +
                "Disabled: No busy system on remotes"));
            
            EditorGUILayout.EndVertical();
        }
        
        private void DrawOptionalComponentsSection()
        {
            EditorGUILayout.BeginVertical("box");
            
            EditorGUILayout.LabelField("Optional Network Components", m_HeaderStyle);
            EditorGUILayout.Space(2);
            
            EditorGUILayout.PropertyField(m_UseNetworkIK, new GUIContent("Use Network IK",
                "Auto-create UnitIKNetworkController for IK synchronization"));
            
            EditorGUILayout.PropertyField(m_UseNetworkMotion, new GUIContent("Use Network Motion",
                "Use UnitMotionNetworkController for dash/teleport validation"));
            
            EditorGUILayout.PropertyField(m_UseLagCompensation, new GUIContent("Use Lag Compensation",
                "Auto-create CharacterLagCompensation for hit validation (server only)"));
            
            EditorGUILayout.PropertyField(m_UseAnimationSync, new GUIContent("Use Animation Sync",
                "Auto-create UnitAnimimNetworkController for States and Gestures sync (attack anims, emotes, etc.)"));
            
            EditorGUILayout.PropertyField(m_UseCoreNetworking, new GUIContent("Use Core Networking",
                "Auto-create NetworkCoreController for Ragdoll, Props, Invincibility, Poise, and Busy synchronization"));
            
            EditorGUILayout.EndVertical();
        }
        
        private void DrawServerOptimizationSection()
        {
            EditorGUILayout.BeginVertical("box");
            
            EditorGUILayout.LabelField("Server Optimization", m_HeaderStyle);
            EditorGUILayout.Space(2);
            
            EditorGUILayout.HelpBox(
                "These optimizations apply when running as a dedicated server.",
                MessageType.None
            );
            
            EditorGUILayout.Space(5);
            
            EditorGUILayout.PropertyField(m_DisableVisualsOnServer, new GUIContent("Disable Visuals",
                "Disable renderers and particles on server to save memory"));
            
            EditorGUILayout.PropertyField(m_DisableAudioOnServer, new GUIContent("Disable Audio",
                "Disable audio sources on server"));
            
            EditorGUILayout.EndVertical();
        }
        
        private void DrawRuntimeInfo(NetworkCharacter networkCharacter)
        {
            EditorGUILayout.BeginVertical("box");
            
            EditorGUILayout.LabelField("Runtime Status", m_HeaderStyle);
            EditorGUILayout.Space(2);
            
            // Role
            string roleText = networkCharacter.Role.ToString();
            Color roleColor = networkCharacter.Role switch
            {
                NetworkCharacter.NetworkRole.Server => Color.yellow,
                NetworkCharacter.NetworkRole.LocalClient => Color.green,
                NetworkCharacter.NetworkRole.RemoteClient => Color.cyan,
                _ => Color.gray
            };
            
            GUI.color = roleColor;
            EditorGUILayout.LabelField("Network Role", roleText);
            GUI.color = Color.white;
            
            // Active Driver
            if (networkCharacter.ActiveDriver != null)
            {
                EditorGUILayout.LabelField("Active Driver", networkCharacter.ActiveDriver.GetType().Name);
            }
            
            // Optional components
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Active Components:", EditorStyles.miniBoldLabel);
            
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("IK Controller", 
                networkCharacter.IKController != null ? "Active" : "Not Active");
            EditorGUILayout.LabelField("Motion Controller", 
                networkCharacter.MotionController != null ? "Active" : "Not Active");
            EditorGUILayout.LabelField("Lag Compensation", 
                networkCharacter.LagCompensation != null ? "Active" : "Not Active");
            EditorGUILayout.LabelField("Animation Sync", 
                networkCharacter.AnimimController != null ? "Active" : "Not Active");
            EditorGUILayout.LabelField("Core Controller", 
                networkCharacter.CoreController != null ? "Active" : "Not Active");
            EditorGUI.indentLevel--;
            
            EditorGUILayout.EndVertical();
        }
    }
}
