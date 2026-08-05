using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEditor;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Editor
{
    /// <summary>
    /// Offline editor catalog for Photon Fusion's public Gaming Cloud regions.
    /// At runtime Photon still supplies the App Id's authoritative, allowlisted region set.
    /// </summary>
    public static class FusionRegionCatalog
    {
        public const string BestRegionCode = "";

        private static readonly ReadOnlyCollection<string> s_Codes =
            Array.AsReadOnly(new[]
            {
                BestRegionCode,
                "asia",
                "au",
                "cae",
                "cn",
                "eu",
                "hk",
                "in",
                "jp",
                "za",
                "sa",
                "kr",
                "tr",
                "uae",
                "us",
                "usw",
                "ussc"
            });

        private static readonly ReadOnlyCollection<GUIContent> s_Options =
            Array.AsReadOnly(new[]
            {
                new GUIContent(
                    "Best Region (automatic)",
                    "Photon pings the regions enabled for this App Id and selects one automatically."),
                new GUIContent("Asia - Singapore (asia)"),
                new GUIContent("Australia - Sydney (au)"),
                new GUIContent("Canada East - Montreal (cae)"),
                new GUIContent(
                    "China Mainland - Shanghai (cn)",
                    "The Photon China region requires separate App Id access and configuration."),
                new GUIContent("Europe - Amsterdam (eu)"),
                new GUIContent("Hong Kong (hk)"),
                new GUIContent("India - Chennai (in)"),
                new GUIContent("Japan - Tokyo (jp)"),
                new GUIContent("South Africa - Johannesburg (za)"),
                new GUIContent("South America - Sao Paulo (sa)"),
                new GUIContent("South Korea - Seoul (kr)"),
                new GUIContent("Turkey - Istanbul (tr)"),
                new GUIContent("United Arab Emirates - Dubai (uae)"),
                new GUIContent("USA East - Washington D.C. (us)"),
                new GUIContent("USA West - San Jose (usw)"),
                new GUIContent("USA South Central - Dallas (ussc)")
            });

        private static readonly GUIContent DefaultLabel = new GUIContent(
            "Region",
            "Best Region asks Photon to choose automatically. For direct session-name joins, " +
            "all peers must resolve or select the same region.");

        public static IReadOnlyList<string> Codes => s_Codes;

        public static string Normalize(string region)
        {
            return string.IsNullOrWhiteSpace(region)
                ? BestRegionCode
                : region.Trim().ToLowerInvariant();
        }

        public static bool IsKnown(string region)
        {
            return IndexOf(region) >= 0;
        }

        public static int IndexOf(string region)
        {
            string normalized = Normalize(region);
            for (int i = 0; i < s_Codes.Count; i++)
            {
                if (string.Equals(s_Codes[i], normalized, StringComparison.Ordinal)) return i;
            }

            return -1;
        }

        public static string DrawPopup(string region)
        {
            return DrawPopup(DefaultLabel, region, out _);
        }

        public static string DrawPopup(GUIContent label, string region)
        {
            return DrawPopup(label, region, out _);
        }

        public static string DrawPopup(
            GUIContent label,
            string region,
            out bool changed)
        {
            string current = region ?? BestRegionCode;
            int currentIndex = IndexOf(current);
            GUIContent[] options;

            if (currentIndex >= 0)
            {
                options = CopyOptions();
            }
            else
            {
                options = new GUIContent[s_Options.Count + 1];
                for (int i = 0; i < s_Options.Count; i++) options[i] = s_Options[i];
                options[s_Options.Count] = new GUIContent(
                    $"Legacy or custom region ({current.Trim()})",
                    "This serialized value is not in the current public catalog. It is preserved " +
                    "until another option is selected.");
                currentIndex = s_Options.Count;
            }

            EditorGUI.BeginChangeCheck();
            int selectedIndex = EditorGUILayout.Popup(label ?? DefaultLabel, currentIndex, options);
            changed = EditorGUI.EndChangeCheck();
            if (!changed) return current;

            return selectedIndex >= 0 && selectedIndex < s_Codes.Count
                ? s_Codes[selectedIndex]
                : current;
        }

        public static void DrawSerializedProperty(
            SerializedProperty property,
            GUIContent label = null)
        {
            if (property == null || property.propertyType != SerializedPropertyType.String)
            {
                throw new ArgumentException(
                    "A string SerializedProperty is required for a Fusion region popup.",
                    nameof(property));
            }

            GUIContent effectiveLabel = label ?? DefaultLabel;
            Rect position = EditorGUILayout.GetControlRect();
            effectiveLabel = EditorGUI.BeginProperty(position, effectiveLabel, property);
            bool previousMixedValue = EditorGUI.showMixedValue;
            bool hasMixedValues = property.hasMultipleDifferentValues;
            EditorGUI.showMixedValue = hasMixedValues;
            try
            {
                string current = property.stringValue ?? BestRegionCode;
                int currentIndex = hasMixedValues ? -1 : IndexOf(current);
                GUIContent[] options;

                if (hasMixedValues || currentIndex >= 0)
                {
                    options = CopyOptions();
                }
                else
                {
                    options = new GUIContent[s_Options.Count + 1];
                    for (int i = 0; i < s_Options.Count; i++) options[i] = s_Options[i];
                    options[s_Options.Count] = new GUIContent(
                        $"Legacy or custom region ({current.Trim()})",
                        "This serialized value is not in the current public catalog. It is " +
                        "preserved until another option is selected.");
                    currentIndex = s_Options.Count;
                }

                EditorGUI.BeginChangeCheck();
                int selectedIndex = EditorGUI.Popup(
                    position,
                    effectiveLabel,
                    currentIndex,
                    options);
                if (EditorGUI.EndChangeCheck() &&
                    selectedIndex >= 0 &&
                    selectedIndex < s_Codes.Count)
                {
                    property.stringValue = s_Codes[selectedIndex];
                }
            }
            finally
            {
                EditorGUI.showMixedValue = previousMixedValue;
                EditorGUI.EndProperty();
            }
        }

        private static GUIContent[] CopyOptions()
        {
            var options = new GUIContent[s_Options.Count];
            for (int i = 0; i < s_Options.Count; i++) options[i] = s_Options[i];
            return options;
        }
    }
}
