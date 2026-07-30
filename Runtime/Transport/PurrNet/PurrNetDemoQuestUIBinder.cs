using System;
using System.Collections;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Demo Quest UI Binder")]
    public sealed class PurrNetDemoQuestUIBinder : MonoBehaviour
    {
        private const string JOURNAL_TYPE = "GameCreator.Runtime.Quests.Journal, GameCreator.Runtime.Quests";
        private const string QUEST_UI_NAMESPACE = "GameCreator.Runtime.Quests.UnityUI.";

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

                yield return new WaitForSecondsRealtime(Mathf.Max(0.05f, m_CheckInterval));
            }
        }

        private static bool HasLocalPlayerJournal()
        {
            Type journalType = Type.GetType(JOURNAL_TYPE);
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
                if (type.FullName == null || !type.FullName.StartsWith(QUEST_UI_NAMESPACE, StringComparison.Ordinal))
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
