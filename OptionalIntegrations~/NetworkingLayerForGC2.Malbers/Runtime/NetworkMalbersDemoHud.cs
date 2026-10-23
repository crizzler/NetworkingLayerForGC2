#if ARAWN_GC2_MALBERS
using System.Collections.Generic;
using Arawn.GameCreator2.Networking;
using MalbersAnimations.Controller;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Malbers
{
    /// <summary>
    /// Minimal IMGUI HUD for the optional Malbers demo. It proves which native MAnimal state
    /// each peer currently applies and lets the authority force a state so proxies can be
    /// checked without authoring a full UI prefab.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Malbers Demo HUD")]
    public sealed class NetworkMalbersDemoHud : MonoBehaviour
    {
        [SerializeField] private KeyCode m_CycleStateKey = KeyCode.Alpha1;
        [SerializeField] private bool m_ShowHud = true;

        private NetworkCharacter m_LocalCharacter;
        private MAnimal m_LocalAnimal;
        private NetworkMalbersStateBridge m_LocalStateBridge;
        private int m_StateIndex;
        private GUIStyle m_Style;

        private void Update()
        {
            if (m_LocalCharacter == null || !m_LocalCharacter.IsLocalPlayer ||
                m_LocalCharacter.Role != NetworkCharacter.NetworkRole.LocalClient)
            {
                RefreshLocalCharacter();
            }

            if (m_LocalAnimal == null || m_LocalStateBridge == null) return;
            if (!WasCycleStateKeyPressed()) return;

            List<State> states = m_LocalAnimal.states;
            if (states == null || states.Count == 0) return;

            int attempts = 0;
            State next = null;
            while (attempts < states.Count)
            {
                m_StateIndex = (m_StateIndex + 1) % states.Count;
                State candidate = states[m_StateIndex];
                if (candidate != null && candidate.ID != null && candidate.ID.ID > 0)
                {
                    next = candidate;
                    break;
                }

                attempts++;
            }

            if (next == null) return;

            m_LocalAnimal.State_Force(next.ID);
            Debug.Log(
                $"[NetworkMalbersDemoHud] local authority forced state '{next.name}' " +
                $"id={next.ID.ID} character={m_LocalCharacter.NetworkId}");
        }

        /// <summary>
        /// Reads the demo key through whichever input backend the project enables. The Unity
        /// Input class throws when a project has switched to the Input System package, which
        /// would otherwise spam the player log on every frame.
        /// </summary>
        private bool WasCycleStateKeyPressed()
        {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            UnityEngine.InputSystem.Keyboard keyboard = UnityEngine.InputSystem.Keyboard.current;
            UnityEngine.InputSystem.Controls.KeyControl key =
                keyboard != null ? keyboard[ToInputSystemKey(m_CycleStateKey)] : null;
            return key != null && key.wasPressedThisFrame;
#else
            return Input.GetKeyDown(m_CycleStateKey);
#endif
        }

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        private static UnityEngine.InputSystem.Key ToInputSystemKey(KeyCode keyCode)
        {
            switch (keyCode)
            {
                case KeyCode.Alpha0: return UnityEngine.InputSystem.Key.Digit0;
                case KeyCode.Alpha1: return UnityEngine.InputSystem.Key.Digit1;
                case KeyCode.Alpha2: return UnityEngine.InputSystem.Key.Digit2;
                case KeyCode.Alpha3: return UnityEngine.InputSystem.Key.Digit3;
                case KeyCode.Alpha4: return UnityEngine.InputSystem.Key.Digit4;
                case KeyCode.Alpha5: return UnityEngine.InputSystem.Key.Digit5;
                case KeyCode.Alpha6: return UnityEngine.InputSystem.Key.Digit6;
                case KeyCode.Alpha7: return UnityEngine.InputSystem.Key.Digit7;
                case KeyCode.Alpha8: return UnityEngine.InputSystem.Key.Digit8;
                case KeyCode.Alpha9: return UnityEngine.InputSystem.Key.Digit9;
                case KeyCode.Space: return UnityEngine.InputSystem.Key.Space;
                case KeyCode.Return: return UnityEngine.InputSystem.Key.Enter;
                default: return UnityEngine.InputSystem.Key.None;
            }
        }
#endif

        private void RefreshLocalCharacter()
        {
            m_LocalCharacter = null;
            m_LocalAnimal = null;
            m_LocalStateBridge = null;

#if UNITY_2023_1_OR_NEWER
            NetworkCharacter[] characters = UnityObjectSearch.FindAll<NetworkCharacter>(
                FindObjectsInactive.Exclude);
#else
            NetworkCharacter[] characters = FindObjectsOfType<NetworkCharacter>();
#endif
            for (int i = 0; i < characters.Length; i++)
            {
                NetworkCharacter candidate = characters[i];
                if (candidate == null || !candidate.IsLocalPlayer ||
                    candidate.Role != NetworkCharacter.NetworkRole.LocalClient)
                {
                    continue;
                }

                m_LocalCharacter = candidate;
                m_LocalAnimal = candidate.GetComponent<MAnimal>();
                m_LocalStateBridge = candidate.GetComponent<NetworkMalbersStateBridge>();
                break;
            }
        }

        private void OnGUI()
        {
            if (!m_ShowHud) return;

            m_Style ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                richText = true,
                wordWrap = true
            };

            GUILayout.BeginArea(new Rect(16f, 16f, 620f, 360f), GUI.skin.box);
            GUILayout.Label("<b>GC2 Networking Layer - Malbers Animal Controller</b>", m_Style);
            GUILayout.Space(6f);

            if (m_LocalCharacter == null)
            {
                GUILayout.Label("Waiting for a local player character...", m_Style);
            }
            else
            {
                GUILayout.Label(
                    $"Local character netId={m_LocalCharacter.NetworkId} " +
                    $"role={m_LocalCharacter.Role} owner={m_LocalCharacter.IsOwnerInstance}",
                    m_Style);

                if (m_LocalAnimal != null)
                {
                    string state = m_LocalAnimal.ActiveStateID != null
                        ? $"{m_LocalAnimal.ActiveStateID.name} ({m_LocalAnimal.ActiveStateID.ID})"
                        : "none";
                    string stance = m_LocalAnimal.Stance != null
                        ? $"{m_LocalAnimal.Stance.name} ({m_LocalAnimal.Stance.ID})"
                        : "none";
                    GUILayout.Label($"MAnimal state: {state}", m_Style);
                    GUILayout.Label(
                        $"mode={m_LocalAnimal.ActiveModeID}/{m_LocalAnimal.ModeAbility} " +
                        $"stance={stance} grounded={m_LocalAnimal.Grounded}",
                        m_Style);
                }

                GUILayout.Label(
                    "WASD move, Shift run. Press 1 to force the next MAnimal state on the " +
                    "authority; every remote peer must apply the same state.",
                    m_Style);
            }

            GUILayout.Space(6f);
            GUILayout.Label(
                "Server-authoritative NPCs broadcast; owning clients send to the server, " +
                "which validates ownership before rebroadcasting.",
                m_Style);
            GUILayout.EndArea();
        }
    }
}
#endif
