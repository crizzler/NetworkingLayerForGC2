using System.Linq;
using GameCreator.Runtime.Characters;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Arawn.GameCreator2.Networking
{
    [AddComponentMenu("")]
    public sealed class NetworkTargetingDemoUI : MonoBehaviour
    {
        private NetworkCharacter[] m_Actors = System.Array.Empty<NetworkCharacter>();
        private float m_NextRefresh;
        private void Update()
        {
            if (Time.unscaledTime >= m_NextRefresh)
            {
                m_NextRefresh = Time.unscaledTime + 0.25f;
                m_Actors = UnityObjectSearch.FindAll<NetworkCharacter>(FindObjectsInactive.Exclude)
                    .Where(x => x.NetworkId != 0 && x.Role != NetworkCharacter.NetworkRole.None).ToArray();
                foreach (NetworkCharacter owner in m_Actors.Where(x => x != null && x.IsOwnerInstance))
                {
                    foreach (GameObject stale in owner.Character.Combat.Targets.List.ToArray())
                        if (!m_Actors.Any(x => x != owner && x.gameObject == stale && !x.Character.IsDead))
                            owner.Character.Combat.Targets.RemoveCandidate(stale);
                    foreach (NetworkCharacter target in m_Actors)
                        if (target != owner && !target.Character.IsDead)
                            owner.Character.Combat.Targets.AddCandidate(target.gameObject);
                }
            }
            if (Keyboard.current?.rKey.wasPressedThisFrame == true) Select(null);
        }

        private void Select(NetworkCharacter target)
        {
            NetworkCharacter owner = m_Actors.FirstOrDefault(x => x != null && x.IsOwnerInstance);
            if (owner != null) owner.Character.Combat.Targets.Primary = target != null ? target.gameObject : null;
        }

        private void OnGUI()
        {
            float width = Mathf.Min(380f, Mathf.Max(280f, Screen.width * 0.3f));
            GUILayout.BeginArea(new Rect(Screen.width - width - 12f, 12, width, Mathf.Min(Screen.height - 24, 500)), GUI.skin.box);
            GUILayout.Label("Closest Target — Multiplayer");
            GUILayout.Label("WASD: move   TAB: stock Closest Target action   R: clear");
            GUILayout.Label("Host or join using the session panel. Select an NPC or the other player below.");
            foreach (NetworkCharacter actor in m_Actors)
            {
                if (actor == null || actor.Character == null) continue;
                NetworkCombatTarget state = actor.GetComponent<NetworkCombatTarget>();
                if (state != null)
                {
                    Character target = NetworkTransportBridge.Active?.ResolveCharacter(state.TargetNetworkId);
                    float error = target != null ? Vector3.Angle(Vector3.ProjectOnPlane(actor.transform.forward, Vector3.up),
                        Vector3.ProjectOnPlane(target.transform.position - actor.transform.position, Vector3.up)) : 0;
                    GUILayout.Label($"Actor {actor.NetworkId} {actor.Role}: target {state.TargetNetworkId}, yaw error {error:F1}°");
                }
            }
            NetworkCharacter owner = m_Actors.FirstOrDefault(x => x != null && x.IsOwnerInstance);
            if (owner != null)
            {
                foreach (NetworkCharacter target in m_Actors.Where(x => x != null && x != owner))
                    if (GUILayout.Button($"Target {(target.IsPlayerOwnedActor ? "Player" : "NPC")} {target.NetworkId}")) Select(target);
                if (GUILayout.Button("Clear target")) Select(null);
            }
            GUILayout.EndArea();
        }
    }
}
