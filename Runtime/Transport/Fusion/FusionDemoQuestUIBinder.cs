using System;
using System.Collections;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Rebinds the optional GC2 Quest demo UI after the local Fusion-owned character and
    /// its Journal have completed the transport readiness flow.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Demo Quest UI Binder")]
    public sealed class FusionDemoQuestUIBinder : MonoBehaviour
    {
        private const string JournalType =
            "GameCreator.Runtime.Quests.Journal, GameCreator.Runtime.Quests";
        private const string QuestUiNamespace = "GameCreator.Runtime.Quests.UnityUI.";

        [SerializeField] private float m_CheckInterval = 0.25f;
        [SerializeField] private float m_Timeout = 30f;

        private IEnumerator Start()
        {
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start <= m_Timeout)
            {
                if (HasLocalPlayerJournal())
                {
                    RebindQuestUI();
                    yield break;
                }

                yield return new WaitForSecondsRealtime(
                    Mathf.Max(0.05f, m_CheckInterval));
            }
        }

        private static bool HasLocalPlayerJournal()
        {
            Type journalType = Type.GetType(JournalType);
            if (journalType == null) return false;

            Character[] characters = FindObjectsByType<Character>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            for (int i = 0; i < characters.Length; i++)
            {
                Character character = characters[i];
                if (character == null || !character.IsPlayer) continue;
                if (character.GetComponent(journalType) != null) return true;
            }

            return false;
        }

        private void RebindQuestUI()
        {
            MonoBehaviour[] behaviours = GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null) continue;

                Type type = behaviour.GetType();
                if (type.FullName == null ||
                    !type.FullName.StartsWith(QuestUiNamespace, StringComparison.Ordinal))
                {
                    continue;
                }

                bool wasEnabled = behaviour.enabled;
                behaviour.enabled = false;
                behaviour.enabled = wasEnabled;
            }
        }
    }
}
