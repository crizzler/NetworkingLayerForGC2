#if UNITY_EDITOR && GC2_STATS && GC2_MELEE
using System;
using System.Reflection;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Melee;
using GameCreator.Runtime.Stats;
using GameCreator.Runtime.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Stats.Melee
{
    // Only authored demo data is saved. Stock animation, shield and sword visuals remain
    // references to Melee.Sword, which the Install Manager installs separately.
    public static class NetworkMeleeFormulaDemoAssets
    {
        private const string SwordRoot = "Assets/Plugins/GameCreator/Installs/Melee.Sword@1.1.3";

        public static UnityEngine.Object Build(string root, string transport)
        {
            const string folderName = "Parry Formula";
            string folder = root + "/" + folderName;
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(root, folderName);

            Formula formula = LoadOrCreate<Formula>(folder + "/Training Damage.asset");
            var formulaData = new SerializedObject(formula);
            // A stock Skill can evaluate Power against scenery before checking for a Character.
            // Use only the attacking Character's HP so scenery needs no Traits component.
            formulaData.FindProperty("m_Formula").stringValue = NetworkMeleeHealthDemoMonitor.TrainingFormula;
            formulaData.ApplyModifiedPropertiesWithoutUndo();

            Skill slash = BuildSkill(folder, transport + " Training Slash", "LightAttack1", formula, false);
            Skill riposte = BuildSkill(folder, transport + " Training Riposte", "Riposte", formula, true);
            MeleeWeapon stock = AssetDatabase.LoadAssetAtPath<MeleeWeapon>(SwordRoot + "/Sword_Weapon.asset");
            if (stock == null) throw new InvalidOperationException("Install the GC2 Sword examples first.");
            MeleeWeapon weapon = LoadOrCreate<MeleeWeapon>(folder + "/Training Sword.asset");
            var data = new SerializedObject(weapon);
            var stockData = new SerializedObject(stock);
            foreach (string field in new[] { "m_Shield", "m_HitReaction", "m_ParriedReaction", "m_State" })
                data.CopyFromSerializedProperty(stockData.FindProperty(field));
            data.FindProperty("m_Id.m_SerializedID.m_String").stringValue = "arawn-training-sword-" + transport.ToLowerInvariant();
            data.FindProperty("m_Title.m_Property").managedReferenceValue = new GetStringString("Training Sword");
            SerializedProperty selector = data.FindProperty("m_Combos");
            selector.FindPropertyRelative("m_CombosFrom").enumValueIndex = 1; // Embedded
            data.ApplyModifiedPropertiesWithoutUndo();

            // Change the live tree after applying SerializedObject changes. Reassigning the
            // same managed reference through SerializedProperty can discard its dictionary.
            ComboTree tree = weapon.Combo ?? new ComboTree();
            if (tree.RootIds.Length == 0)
            {
                tree.AddToRoot(new ComboItem()); // Parry takes priority over the normal tap.
                tree.AddToRoot(new ComboItem());
            }
            var target = new ConditionGameObjectExists();
            SetAuthoringField(target, "m_GameObject", GetGameObjectTarget.Create());
            int[] roots = tree.RootIds;
            if (roots.Length != 2) throw new InvalidOperationException("Expected exactly two training combo roots.");
            ConfigureCombo(tree.Get(roots[0]), riposte, new RunConditionsList(new ConditionMeleeCompareTimeLastParry(), target));
            ConfigureCombo(tree.Get(roots[1]), slash, new RunConditionsList());
            SetAuthoringField(GetAuthoringField(weapon, "m_Combos"), "m_CombosEmbed", tree);
            var stockAttach = stockData.FindProperty("m_OnEquip.m_Instructions.m_Instructions").GetArrayElementAtIndex(0).managedReferenceValue;
            var attach = new InstructionCharacterAttachProp();
            var detach = new InstructionCharacterRemoveProp();
            object prop = GetAuthoringField(stockAttach, "m_Prop");
            SetAuthoringField(attach, "m_Character", GetGameObjectSelf.Create());
            SetAuthoringField(attach, "m_Prop", prop);
            SetAuthoringField(attach, "m_Handle", GetAuthoringField(stockAttach, "m_Handle"));
            SetAuthoringField(detach, "m_Character", GetGameObjectSelf.Create());
            SetAuthoringField(detach, "m_Prop", prop);
            SetAuthoringField(weapon, "m_OnEquip", new RunInstructionsList(attach));
            SetAuthoringField(weapon, "m_OnUnequip", new RunInstructionsList(detach));
            EditorUtility.SetDirty(weapon);
            return weapon;
        }

        private static void ConfigureCombo(ComboItem item, Skill skill, RunConditionsList conditions)
        {
            SetAuthoringField(item, "m_Skill", skill);
            SetAuthoringField(item, "m_When", MeleeExecute.AnyTime);
            SetAuthoringField(item, "m_Conditions", conditions);
        }

        private static void SetAuthoringField(object target, string name, object value) =>
            AuthoringField(target, name).SetValue(target, value);

        private static object GetAuthoringField(object target, string name) =>
            AuthoringField(target, name).GetValue(target);

        private static FieldInfo AuthoringField(object target, string name)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new MissingFieldException(target.GetType().Name, name);
        }

        private static Skill BuildSkill(string folder, string name, string stockName, Formula formula, bool riposte)
        {
            Skill source = AssetDatabase.LoadAssetAtPath<Skill>(SwordRoot + "/Skills/" + stockName + ".asset");
            if (source == null || source.Animation == null) throw new InvalidOperationException("Missing stock animation for " + stockName);
            Skill skill = LoadOrCreate<Skill>(folder + "/" + name + ".asset");
            var data = new SerializedObject(skill);
            data.FindProperty("m_Title.m_Property").managedReferenceValue = new GetStringString(riposte ? "Training Riposte" : "Training Slash");
            data.FindProperty("m_Animation").objectReferenceValue = source.Animation;
            data.FindProperty("m_Mask").objectReferenceValue = source.Mask;
            var clip = data.FindProperty("m_MeleeSequence.m_Sequence.m_Tracks").GetArrayElementAtIndex(MeleeSequence.TRACK_PHASES)
                .FindPropertyRelative("m_Clips");
            clip.arraySize = 1;
            // Match the stock clips' contact timing; don't copy the stock Skill or its logic.
            var stockPhases = new SerializedObject(source).FindProperty("m_MeleeSequence.m_Sequence.m_Tracks")
                .GetArrayElementAtIndex(MeleeSequence.TRACK_PHASES).FindPropertyRelative("m_Clips").GetArrayElementAtIndex(0);
            clip.GetArrayElementAtIndex(0).managedReferenceValue = new ClipMeleePhases(
                stockPhases.FindPropertyRelative("m_Time").floatValue,
                stockPhases.FindPropertyRelative("m_Duration").floatValue);
            var power = data.FindProperty("m_Power.m_Property");
            power.managedReferenceValue = new GetDecimalFormula();
            var instance = power.FindPropertyRelative("m_Formula.m_Property");
            instance.managedReferenceValue = new GetFormulaInstance();
            instance.FindPropertyRelative("m_Formula").objectReferenceValue = formula;
            if (riposte)
            {
                data.FindProperty("m_Motion").enumValueIndex = (int)MeleeMotion.MotionWarp;
                var warpClips = data.FindProperty("m_MeleeSequence.m_Sequence.m_Tracks")
                    .GetArrayElementAtIndex(MeleeSequence.TRACK_MOTION_WARPING).FindPropertyRelative("m_Clips");
                var toward = new GetLocationMeleeSelfToTarget();
                SetAuthoringField(toward, "m_Distance", GetDecimalDecimal.Create(0.70f));
                SetAuthoringField(toward, "m_TrackTarget", true);
                FieldInfo rotation = AuthoringField(toward, "m_Rotation");
                rotation.SetValue(toward, Enum.Parse(rotation.FieldType, "TowardsTarget"));
                var warp = new ClipMeleeMotionWarping(0f, 0.69f);
                SetAuthoringField(warp, "m_Self", new PropertyGetLocation(toward));
                warpClips.arraySize = 1;
                warpClips.GetArrayElementAtIndex(0).managedReferenceValue = warp;
                var instructions = data.FindProperty("m_OnStart.m_Instructions.m_Instructions");
                instructions.arraySize = 1;
                var reset = instructions.GetArrayElementAtIndex(0);
                reset.managedReferenceValue = new InstructionMeleeResetParryTime();
                reset.FindPropertyRelative("m_Character.m_Property").managedReferenceValue = new GetGameObjectSelf();
            }
            data.ApplyModifiedPropertiesWithoutUndo();
            return skill;
        }

        private static T LoadOrCreate<T>(string path) where T : ScriptableObject
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null) return asset;
            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }
    }
}
#endif
