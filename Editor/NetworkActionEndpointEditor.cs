using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking;
using UnityEditor;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Editor
{
    [CustomEditor(typeof(NetworkActionEndpoint))]
    [CanEditMultipleObjects]
    public sealed class NetworkActionEndpointEditor : UnityEditor.Editor
    {
        private SerializedProperty m_Actions;
        private SerializedProperty m_EndpointId;
        private SerializedProperty m_NetworkCharacter;
        private SerializedProperty m_AutoFindNetworkCharacter;
        private SerializedProperty m_LogNetworkMessages;

        private void OnEnable()
        {
            m_Actions = serializedObject.FindProperty("m_Actions");
            m_EndpointId = serializedObject.FindProperty("m_EndpointId");
            m_NetworkCharacter = serializedObject.FindProperty("m_NetworkCharacter");
            m_AutoFindNetworkCharacter = serializedObject.FindProperty(
                "m_AutoFindNetworkCharacter");
            m_LogNetworkMessages = serializedObject.FindProperty("m_LogNetworkMessages");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.HelpBox(
                "A Network Action Endpoint is the receiving address and allow-list for one " +
                "replicated object. Put it on or beneath an admitted Fusion/PurrNet transport " +
                "identity, then bind every Action Definition this object accepts.",
                MessageType.Info);

            EditorGUILayout.PropertyField(m_Actions, true);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Identity", EditorStyles.boldLabel);
            DrawEndpointId();
            EditorGUILayout.PropertyField(
                m_NetworkCharacter,
                new GUIContent(
                    "Network Character (Optional)",
                    "Use only when this endpoint belongs to that same Network Character. " +
                    "Leave empty for ordinary world objects."));
            EditorGUILayout.PropertyField(m_AutoFindNetworkCharacter);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_LogNetworkMessages);

            serializedObject.ApplyModifiedProperties();
            DrawConfigurationWarnings();
            DrawRuntimeStatus();
        }

        private void DrawEndpointId()
        {
            EditorGUILayout.HelpBox(
                "Endpoint ID is an automatically generated, stable sub-address. It is not the " +
                "Action ID or runtime Network ID. Normally leave it unchanged. Sibling " +
                "endpoints beneath one transport identity must have different IDs; prefab " +
                "instances with different Network IDs may reuse the same ID.",
                MessageType.None);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(m_EndpointId, new GUIContent("Endpoint ID"));
            }

            if (!m_EndpointId.hasMultipleDifferentValues)
            {
                int hash = StableHashUtility.GetStableHash(m_EndpointId.stringValue);
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField(
                        "Wire Hash",
                        hash == 0 ? "Invalid" : $"0x{unchecked((uint)hash):X8}");
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(
                           m_EndpointId.hasMultipleDifferentValues ||
                           string.IsNullOrWhiteSpace(m_EndpointId.stringValue)))
                {
                    if (GUILayout.Button("Copy ID"))
                        GUIUtility.systemCopyBuffer = m_EndpointId.stringValue;
                }

                using (new EditorGUI.DisabledScope(Application.isPlaying))
                {
                    if (GUILayout.Button("Regenerate Endpoint ID…")) RegenerateEndpointIds();
                }
            }
        }

        private void RegenerateEndpointIds()
        {
            if (!EditorUtility.DisplayDialog(
                    "Regenerate Endpoint ID?",
                    "This changes the endpoint's protocol address. Only do this to resolve a " +
                    "duplicate endpoint beneath the same transport identity, or when " +
                    "intentionally creating a different logical endpoint. All peer builds must " +
                    "ship the new serialized ID.",
                    "Regenerate",
                    "Cancel"))
                return;

            Undo.RecordObjects(targets, "Regenerate Network Action Endpoint ID");
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] is not NetworkActionEndpoint endpoint) continue;
                var endpointObject = new SerializedObject(endpoint);
                SerializedProperty endpointId = endpointObject.FindProperty("m_EndpointId");
                endpointId.stringValue = Guid.NewGuid().ToString("N");
                endpointObject.ApplyModifiedProperties();
                PrefabUtility.RecordPrefabInstancePropertyModifications(endpoint);
                EditorUtility.SetDirty(endpoint);
            }

            serializedObject.Update();
        }

        private void DrawConfigurationWarnings()
        {
            if (targets.Length != 1) return;
            var endpoint = (NetworkActionEndpoint)target;
            if (string.IsNullOrWhiteSpace(endpoint.EndpointId) || endpoint.EndpointHash == 0)
            {
                EditorGUILayout.HelpBox(
                    "This endpoint has no valid stable Endpoint ID and cannot register. " +
                    "Regenerate the ID and save the scene or prefab.",
                    MessageType.Error);
            }

            IReadOnlyList<NetworkActionBinding> actions = endpoint.Actions;
            if (actions.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Actions is empty. This endpoint can register, but it accepts no Network " +
                    "Action until a Definition binding is added.",
                    MessageType.Warning);
                return;
            }

            var contracts = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < actions.Count; i++)
            {
                NetworkActionDefinition definition = actions[i]?.Definition;
                if (definition == null)
                {
                    EditorGUILayout.HelpBox(
                        $"Action binding {i} has no Definition and cannot accept a request.",
                        MessageType.Error);
                    continue;
                }

                string key = $"{definition.ActionHash}:{definition.ActionId}";
                if (!contracts.Add(key))
                {
                    EditorGUILayout.HelpBox(
                        $"Action '{definition.ActionId}' is bound more than once. Keep exactly " +
                        "one binding for each Action ID on an endpoint.",
                        MessageType.Error);
                }
            }
        }

        private void DrawRuntimeStatus()
        {
            if (!Application.isPlaying || targets.Length != 1) return;
            var endpoint = (NetworkActionEndpoint)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LongField("Network ID", endpoint.NetworkId);
                bool registered = NetworkActionManager.Instance != null &&
                                  NetworkActionManager.Instance.IsRegistered(
                                      endpoint.NetworkId, endpoint);
                EditorGUILayout.Toggle("Registered", registered);
            }
        }
    }
}
