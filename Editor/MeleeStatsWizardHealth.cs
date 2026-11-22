using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Arawn.GameCreator2.Networking.Editor
{
    public static class MeleeStatsWizardHealth
    {
        internal const string DemoHealthPath =
            "Assets/Plugins/GameCreator/Installs/Stats.Classes@1.3.7/_Stats/Health/HP.asset";

        private const string AttributeProperty = "m_HealthAttribute";
        private const string FallbackProperty = "m_FallbackHealthAttributeId";

        public static void Draw(Component existingBridge, ref Object selected, ref bool edited)
        {
            Type attributeType = Type.GetType("GameCreator.Runtime.Stats.Attribute, GameCreator.Runtime.Stats");
            if (attributeType == null) return;

            var existing = existingBridge != null ? new SerializedObject(existingBridge) : null;
            Object attribute = selected;
            if (!edited)
            {
                attribute = existing != null
                    ? existing.FindProperty(AttributeProperty).objectReferenceValue
                    : AssetDatabase.LoadAssetAtPath(DemoHealthPath, attributeType);
            }

            EditorGUI.BeginChangeCheck();
            attribute = EditorGUILayout.ObjectField("Melee Health Attribute", attribute, attributeType, false);
            if (EditorGUI.EndChangeCheck())
            {
                selected = attribute;
                edited = true;
            }

            string fallback = existing?.FindProperty(FallbackProperty).stringValue ?? "hp";
            string target = string.IsNullOrWhiteSpace(fallback) ? "a health Attribute" : $"the '{fallback}' Attribute ID";
            if (attribute != null) target = $"'{attribute.name}'";
            EditorGUILayout.HelpBox(
                $"Give each damageable player or server-owned NPC a Traits Class containing {target} " +
                "and a NetworkStatsController. Equip and register the MeleeWeapon on the Melee transport bridge. " +
                "Validated strikes use the Skill's Power and the damage bridge. Configure any damage Formula on Skill Power.",
                MessageType.Info);
        }

        public static void Apply(Component bridge, Component existingBridge, Object selected, bool edited)
        {
            if (bridge == null || (!edited && bridge == existingBridge)) return;

            Undo.RecordObject(bridge, "Configure Melee Health Attribute");
            var serialized = new SerializedObject(bridge);
            serialized.FindProperty(AttributeProperty).objectReferenceValue = edited
                ? selected
                : AssetDatabase.LoadMainAssetAtPath(DemoHealthPath);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bridge);
        }
    }
}
