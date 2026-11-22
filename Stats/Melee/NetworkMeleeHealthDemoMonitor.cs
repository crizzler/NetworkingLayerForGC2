#if GC2_STATS && GC2_MELEE
using System.Collections.Generic;
using GameCreator.Runtime.Stats;
using UnityEngine;
using GC2Attribute = GameCreator.Runtime.Stats.Attribute;

namespace Arawn.GameCreator2.Networking.Stats.Melee
{
    [AddComponentMenu("")]
    public sealed class NetworkMeleeHealthDemoMonitor : MonoBehaviour
    {
        [SerializeField] private GC2Attribute m_Health;

        private readonly List<NetworkCharacter> m_Characters = new();
        private Vector2 m_Scroll;
        private float m_NextRefresh;

        private void Update()
        {
            if (Time.unscaledTime < m_NextRefresh) return;
            m_NextRefresh = Time.unscaledTime + 0.25f;

            NetworkTransportBridge transport = NetworkTransportBridge.Active;
            if (transport == null || !transport.IsRunning)
            {
                m_Characters.Clear();
                return;
            }

            transport.CopyRegisteredCharacters(m_Characters);
            m_Characters.Sort((left, right) => left.NetworkId.CompareTo(right.NetworkId));
        }

        private void OnGUI()
        {
            if (m_Health == null || Screen.width < 120 || Screen.height < 180) return;

            const float margin = 12f;
            float width = Mathf.Min(330f, Screen.width - 2f * margin);
            float height = Mathf.Min(205f, Screen.height * 0.47f);
            var panel = new Rect(Screen.width - width - margin, Screen.height - height - margin, width, height);
            GUILayout.BeginArea(panel, "Replicated Health", GUI.skin.window);
            m_Scroll = GUILayout.BeginScrollView(m_Scroll);

            GUILayout.Label("Strike the practice NPC at the center of the arena, or join with a second player and duel.");
            GUILayout.Label("The session authority validates strikes and synchronizes HP.");
            GUILayout.Space(6f);
            bool displayedHealth = false;
            foreach (NetworkCharacter character in m_Characters)
            {
                if (character == null || !character.TryGetComponent(out Traits traits) || !HasHealth(traits)) continue;
                RuntimeAttributeData health = traits.RuntimeAttributes.Get(m_Health.ID);
                if (health == null) continue;

                string name = character.IsServerAuthoritativeNPC
                    ? character.name
                    : character.IsLocalPlayer ? "You" : "Player";
                GUILayout.Label($"{name} [{character.NetworkId}]  ·  {health.Value:0.##} / {health.MaxValue:0.##} HP");
                displayedHealth = true;
            }

            if (!displayedHealth) GUILayout.Label("Start or join a session to see HP.");
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private bool HasHealth(Traits traits)
        {
            if (traits.Class == null) return false;
            for (int i = 0; i < traits.Class.AttributesLength; i++)
            {
                if (traits.Class.GetAttribute(i)?.Attribute == m_Health) return true;
            }

            return false;
        }
    }
}
#endif
