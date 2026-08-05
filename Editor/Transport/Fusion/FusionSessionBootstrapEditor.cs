using Arawn.GameCreator2.Networking.Transport.Fusion;
using UnityEditor;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Editor
{
    [CustomEditor(typeof(FusionSessionBootstrap))]
    [CanEditMultipleObjects]
    public sealed class FusionSessionBootstrapEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            SerializedProperty property = serializedObject.GetIterator();
            bool enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (property.propertyPath == "m_Script")
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.PropertyField(property, true);
                    }
                }
                else if (property.propertyPath == "m_Region")
                {
                    FusionRegionCatalog.DrawSerializedProperty(property);
                    if (!property.hasMultipleDifferentValues &&
                        string.IsNullOrWhiteSpace(property.stringValue))
                    {
                        EditorGUILayout.HelpBox(
                            "Photon named sessions are region-scoped, while Best Region can " +
                            "resolve differently per device. For direct Host/Join or Shared " +
                            "Create/Join, select the same explicit region on every peer. Keep " +
                            "Best Region only when a lobby or invite advertises the creator's " +
                            "resolved region.",
                            MessageType.Warning);
                    }
                }
                else
                {
                    EditorGUILayout.PropertyField(property, true);
                }
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
