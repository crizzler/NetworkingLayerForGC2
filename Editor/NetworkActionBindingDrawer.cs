using Arawn.GameCreator2.Networking;
using GameCreator.Editor.VisualScripting;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Arawn.GameCreator2.Networking.Editor
{
    /// <summary>
    /// Draws endpoint bindings with GC2's native UI Toolkit list tools. The GC2 list drawers do
    /// not implement IMGUI, so rendering these nested fields through EditorGUILayout produces
    /// Unity's "No GUI Implemented" placeholder.
    /// </summary>
    [CustomPropertyDrawer(typeof(NetworkActionBinding))]
    public sealed class NetworkActionBindingDrawer : PropertyDrawer
    {
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            SerializedProperty definition = property.FindPropertyRelative("m_Definition");
            SerializedProperty authorityConditions =
                property.FindPropertyRelative("m_AuthorityConditions");
            SerializedProperty authorityCommitted =
                property.FindPropertyRelative("m_OnAuthorityCommitted");
            SerializedProperty onApplied = property.FindPropertyRelative("m_OnApplied");
            SerializedProperty onSnapshot =
                property.FindPropertyRelative("m_OnSnapshotApplied");

            var root = new VisualElement();
            root.Add(new PropertyField(definition, "Definition"));

            root.Add(CreateSection(
                "Authority Conditions",
                "All enabled Conditions are checked by logical authority before commit."));
            root.Add(new ConditionListTool(
                authorityConditions.FindPropertyRelative("m_Conditions")));

            root.Add(CreateSection(
                "On Authority Committed",
                "Runs once on authority after the canonical value has already committed."));
            root.Add(CreateInstructionTool(authorityCommitted));

            root.Add(CreateSection(
                "On Applied",
                "Runs for a live confirmed result on authority and receiving replicas."));
            root.Add(CreateInstructionTool(onApplied));

            root.Add(CreateSection(
                "On Snapshot Applied",
                "Reconstructs persistent state. Use silent, absolute, idempotent Instructions."));
            root.Add(CreateInstructionTool(onSnapshot));
            return root;
        }

        private static InstructionListTool CreateInstructionTool(SerializedProperty runner)
        {
            return new InstructionListTool(runner.FindPropertyRelative("m_Instructions"));
        }

        private static VisualElement CreateSection(string title, string description)
        {
            var section = new VisualElement();
            section.style.marginTop = 5f;
            var label = new Label(title);
            label.style.unityFontStyleAndWeight = UnityEngine.FontStyle.Bold;
            section.Add(label);
            var help = new HelpBox(description, HelpBoxMessageType.None);
            help.style.marginBottom = 2f;
            section.Add(help);
            return section;
        }
    }
}
