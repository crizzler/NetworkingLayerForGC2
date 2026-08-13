using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

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

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            root.style.paddingLeft = 2f;
            root.style.paddingRight = 2f;

            root.Add(new HelpBox(
                "A Network Action Endpoint is the receiving address and allow-list for one " +
                "replicated object. Put it on or beneath an admitted Fusion/PurrNet transport " +
                "identity, then bind every Action Definition this object accepts.",
                HelpBoxMessageType.Info));

            // This must remain a UI Toolkit PropertyField. NetworkActionBindingDrawer creates
            // GC2's ConditionListTool/InstructionListTool controls for the nested authored
            // lists; an IMGUI PropertyField cannot render those UI Toolkit-only drawers and
            // produces Unity's "No GUI Implemented" placeholder.
            root.Add(new PropertyField(m_Actions, "Actions"));
            root.Add(CreateSpacer());
            root.Add(CreateHeader("Identity"));
            root.Add(new HelpBox(
                "Endpoint ID is an automatically generated, stable sub-address. It is not the " +
                "Action ID or runtime Network ID. Normally leave it unchanged. Sibling " +
                "endpoints beneath one transport identity need different IDs; prefab instances " +
                "with different Network IDs may reuse the same ID.",
                HelpBoxMessageType.None));

            var endpointId = new TextField("Endpoint ID") { isReadOnly = true };
            endpointId.BindProperty(m_EndpointId);
            root.Add(endpointId);

            var wireHash = new TextField("Wire Hash") { isReadOnly = true };
            root.Add(wireHash);
            RefreshWireHash(wireHash);
            wireHash.TrackPropertyValue(m_EndpointId, _ => RefreshWireHash(wireHash));

            var identityButtons = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    marginTop = 2f,
                    marginBottom = 4f
                }
            };
            var copyId = new Button(CopyEndpointId) { text = "Copy ID" };
            copyId.style.flexGrow = 1f;
            var regenerateId = new Button(RegenerateEndpointIds)
            {
                text = "Regenerate Endpoint ID…"
            };
            regenerateId.style.flexGrow = 1f;
            regenerateId.SetEnabled(!Application.isPlaying);
            identityButtons.Add(copyId);
            identityButtons.Add(regenerateId);
            root.Add(identityButtons);

            root.Add(new PropertyField(
                m_NetworkCharacter,
                "Network Character (Optional)"));
            root.Add(new PropertyField(m_AutoFindNetworkCharacter));

            root.Add(CreateSpacer());
            root.Add(CreateHeader("Debug"));
            root.Add(new PropertyField(m_LogNetworkMessages));

            var warnings = new VisualElement();
            root.Add(warnings);
            RefreshConfigurationWarnings(warnings);
            root.TrackSerializedObjectValue(
                serializedObject,
                _ => RefreshConfigurationWarnings(warnings));

            if (Application.isPlaying && targets.Length == 1)
            {
                VisualElement runtime = CreateRuntimeStatus();
                root.Add(runtime);
                runtime.schedule.Execute(() => RefreshRuntimeStatus(runtime)).Every(250);
            }

            return root;
        }

        private static VisualElement CreateHeader(string text)
        {
            var label = new Label(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginTop = 2f;
            label.style.marginBottom = 2f;
            return label;
        }

        private static VisualElement CreateSpacer()
        {
            var spacer = new VisualElement();
            spacer.style.height = 6f;
            return spacer;
        }

        private void RefreshWireHash(TextField field)
        {
            serializedObject.UpdateIfRequiredOrScript();
            if (m_EndpointId.hasMultipleDifferentValues)
            {
                field.SetValueWithoutNotify("—");
                return;
            }

            int hash = StableHashUtility.GetStableHash(m_EndpointId.stringValue);
            field.SetValueWithoutNotify(
                hash == 0 ? "Invalid" : $"0x{unchecked((uint)hash):X8}");
        }

        private void CopyEndpointId()
        {
            serializedObject.UpdateIfRequiredOrScript();
            if (m_EndpointId.hasMultipleDifferentValues ||
                string.IsNullOrWhiteSpace(m_EndpointId.stringValue)) return;
            GUIUtility.systemCopyBuffer = m_EndpointId.stringValue;
        }

        private void RegenerateEndpointIds()
        {
            if (Application.isPlaying) return;
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

        private void RefreshConfigurationWarnings(VisualElement container)
        {
            container.Clear();
            if (targets.Length != 1) return;
            var endpoint = (NetworkActionEndpoint)target;
            if (endpoint == null) return;

            if (string.IsNullOrWhiteSpace(endpoint.EndpointId) || endpoint.EndpointHash == 0)
            {
                container.Add(new HelpBox(
                    "This endpoint has no valid stable Endpoint ID and cannot register. " +
                    "Regenerate the ID and save the scene or prefab.",
                    HelpBoxMessageType.Error));
            }

            IReadOnlyList<NetworkActionBinding> actions = endpoint.Actions;
            if (actions.Count == 0)
            {
                container.Add(new HelpBox(
                    "Actions is empty. This endpoint can register, but it accepts no Network " +
                    "Action until a Definition binding is added.",
                    HelpBoxMessageType.Warning));
                return;
            }

            var contracts = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < actions.Count; i++)
            {
                NetworkActionDefinition definition = actions[i]?.Definition;
                if (definition == null)
                {
                    container.Add(new HelpBox(
                        $"Action binding {i} has no Definition and cannot accept a request.",
                        HelpBoxMessageType.Error));
                    continue;
                }

                string key = $"{definition.ActionHash}:{definition.ActionId}";
                if (!contracts.Add(key))
                {
                    container.Add(new HelpBox(
                        $"Action '{definition.ActionId}' is bound more than once. Keep exactly " +
                        "one binding for each Action ID on an endpoint.",
                        HelpBoxMessageType.Error));
                }
            }
        }

        private VisualElement CreateRuntimeStatus()
        {
            var runtime = new VisualElement { name = "network-action-runtime-status" };
            runtime.Add(CreateSpacer());
            runtime.Add(CreateHeader("Runtime"));
            runtime.Add(new Label { name = "network-id" });
            runtime.Add(new Label { name = "registered" });
            RefreshRuntimeStatus(runtime);
            return runtime;
        }

        private void RefreshRuntimeStatus(VisualElement runtime)
        {
            if (!Application.isPlaying || targets.Length != 1) return;
            var endpoint = (NetworkActionEndpoint)target;
            if (endpoint == null) return;
            Label networkId = runtime.Q<Label>("network-id");
            Label registered = runtime.Q<Label>("registered");
            if (networkId != null) networkId.text = $"Network ID: {endpoint.NetworkId}";
            if (registered != null)
            {
                bool value = NetworkActionManager.Instance != null &&
                             NetworkActionManager.Instance.IsRegistered(
                                 endpoint.NetworkId, endpoint);
                registered.text = $"Registered: {value}";
            }
        }
    }
}
