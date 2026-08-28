using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Command-line-only validation shared by the Fusion and PurrNet enemy demo smoke players.
    /// It is dormant unless a transport bootstrap recognizes --gc2-network-smoke-role.
    /// </summary>
    public static class NetworkEnemyShooterSmokeUtility
    {
        [Serializable]
        public sealed class Result
        {
            public bool passed;
            public string role;
            public string phase;
            public string message;
            public int humans;
            public int serverNpcs;
            public int bots;
            public int slots;
            public int shotsValidated;
            public int hitsValidated;
            public float elapsedSeconds;
        }

        private static readonly List<NetworkCharacter> s_Characters = new(64);

        public static bool TryGetArgument(string name, out string value)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length; i++)
            {
                string argument = arguments[i] ?? string.Empty;
                if (string.Equals(argument, name, StringComparison.Ordinal) &&
                    i + 1 < arguments.Length)
                {
                    value = arguments[i + 1];
                    return true;
                }

                string prefix = name + "=";
                if (argument.StartsWith(prefix, StringComparison.Ordinal))
                {
                    value = argument.Substring(prefix.Length);
                    return true;
                }
            }

            value = string.Empty;
            return false;
        }

        public static int GetIntArgument(string name, int fallback)
        {
            return TryGetArgument(name, out string value) && int.TryParse(value, out int result)
                ? result
                : fallback;
        }

        public static bool HasFlag(string name)
        {
            if (!TryGetArgument(name, out string value)) return false;
            return string.IsNullOrEmpty(value) ||
                string.Equals(value, "1", StringComparison.Ordinal) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryCapture(
            string role,
            int expectedHumans,
            bool authority,
            bool requireEnemyCombat,
            string phase,
            float elapsedSeconds,
            out Result result)
        {
            result = new Result
            {
                role = role,
                phase = phase,
                message = "Transport bridge is not running.",
                elapsedSeconds = elapsedSeconds
            };

            NetworkTransportBridge bridge = ResolveRunningBridge();
            if (bridge == null || !bridge.IsRunning)
            {
                result.message = bridge == null
                    ? "No NetworkTransportBridge exists in the smoke scene."
                    : $"Transport bridge '{bridge.name}' ({bridge.GetType().Name}) is not " +
                      $"running (active={bridge.isActiveAndEnabled}, " +
                      $"server={bridge.IsServer}, client={bridge.IsClient}).";
                return false;
            }
            if (authority && !bridge.IsServer)
            {
                result.message = "The authority process does not own server simulation.";
                return false;
            }

            bridge.CopyRegisteredCharacters(s_Characters);
            bool authorityGateEnabled = false;
            bool authorityTargetSelected = false;
            bool observerGateIncorrectlyEnabled = false;
            for (int i = 0; i < s_Characters.Count; i++)
            {
                NetworkCharacter character = s_Characters[i];
                if (character == null) continue;
                if (character.IsPlayerOwnedActor && character.HasAuthenticatedPlayerOwner)
                {
                    result.humans++;
                }

                if (!character.IsServerAuthoritativeNPC) continue;
                result.serverNpcs++;
                NetworkCharacterAuthorityGate gate =
                    character.GetComponent<NetworkCharacterAuthorityGate>();
                NetworkNpcTargetSelector selector =
                    character.GetComponent<NetworkNpcTargetSelector>();
                bool authorityAiRunning = gate != null &&
                    gate.AuthorityEnabled &&
                    HasActiveAuthorityRoot(gate);
                authorityGateEnabled |= authorityAiRunning;
                authorityTargetSelected |= selector != null && selector.SelectedTarget != null;
                observerGateIncorrectlyEnabled |=
                    !authority && gate != null &&
                    (gate.AuthorityEnabled || HasActiveAuthorityRoot(gate));
            }

            NetworkBotSlotCoordinator[] coordinators =
                UnityObjectSearch.FindAll<NetworkBotSlotCoordinator>(FindObjectsInactive.Include);
            for (int i = 0; i < coordinators.Length; i++)
            {
                IReadOnlyList<NetworkBotSlotState> states = coordinators[i].CurrentStates;
                if (states == null || states.Count <= result.slots) continue;
                result.slots = states.Count;
                result.bots = 0;
                int slotHumans = 0;
                for (int stateIndex = 0; stateIndex < states.Count; stateIndex++)
                {
                    if (states[stateIndex].OccupantType == NetworkBotSlotOccupantType.Bot)
                        result.bots++;
                    else if (states[stateIndex].OccupantType == NetworkBotSlotOccupantType.Human)
                        slotHumans++;
                }

                // A registered human that overflows configured slots is legal, but every human
                // up to the slot capacity must be represented in replicated membership.
                if (slotHumans < Math.Min(expectedHumans, states.Count))
                {
                    result.message =
                        $"Bot-slot membership has {slotHumans} humans; expected {expectedHumans}.";
                    return false;
                }
            }

            ReadShooterStatistics(out result.shotsValidated, out result.hitsValidated);

            if (result.humans != expectedHumans)
            {
                result.message =
                    $"Registered authenticated humans={result.humans}; expected={expectedHumans}.";
                return false;
            }
            if (result.serverNpcs < 1)
            {
                result.message = "No explicit server-authoritative NPC is registered.";
                return false;
            }
            if (result.slots < 3)
            {
                result.message = $"Replicated bot slots={result.slots}; expected at least 3.";
                return false;
            }
            if (result.bots < Math.Max(0, result.slots - expectedHumans))
            {
                result.message =
                    $"Replacement bots={result.bots}; expected at least {result.slots - expectedHumans}.";
                return false;
            }
            if (observerGateIncorrectlyEnabled)
            {
                result.message = "An observer is running an authority-only NPC AI root.";
                return false;
            }
            if (authority && expectedHumans > 0 && !authorityGateEnabled)
            {
                result.message = "Authority has not enabled any selected NPC AI root.";
                return false;
            }
            if (authority && expectedHumans > 0 && !authorityTargetSelected)
            {
                result.message = "Authority NPC has not selected an authenticated player target.";
                return false;
            }
            if (authority && requireEnemyCombat && expectedHumans > 0 &&
                (result.shotsValidated < 1 || result.hitsValidated < 1))
            {
                result.message =
                    $"Enemy combat has not produced a validated shot and hit " +
                    $"(shots={result.shotsValidated}, hits={result.hitsValidated}).";
                return false;
            }

            result.passed = true;
            result.message = "Enemy authority, targeting, combat, and bot membership are valid.";
            return true;
        }

        private static bool HasActiveAuthorityRoot(NetworkCharacterAuthorityGate gate)
        {
            GameObject[] roots = gate.AuthorityOnlyRoots;
            int count = roots?.Length ?? 0;
            for (int i = 0; i < count; i++)
            {
                GameObject root = roots[i];
                if (root != null && root.activeInHierarchy) return true;
            }

            return false;
        }

        public static void Finish(Result result, int exitCode)
        {
            if (result == null) result = new Result { message = "No smoke result was produced." };
            result.passed = exitCode == 0;
            if (TryGetArgument("--gc2-network-smoke-result", out string path) &&
                !string.IsNullOrWhiteSpace(path))
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
                File.WriteAllText(fullPath, JsonUtility.ToJson(result, true));
            }

            string message = $"[GC2 Network Smoke] role={result.role} phase={result.phase} " +
                $"passed={result.passed} humans={result.humans} npcs={result.serverNpcs} " +
                $"bots={result.bots}/{result.slots} shots={result.shotsValidated} " +
                $"hits={result.hitsValidated}: {result.message}";
            if (exitCode == 0) Debug.Log(message);
            else Debug.LogError(message);
            Application.Quit(exitCode);
        }

        private static void ReadShooterStatistics(out int shotsValidated, out int hitsValidated)
        {
            shotsValidated = 0;
            hitsValidated = 0;
            Type type = Type.GetType(
                "Arawn.GameCreator2.Networking.Shooter.NetworkShooterManager, " +
                "Arawn.GameCreator2.Networking.Shooter");
            if (type == null) return;

            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            object manager = type.GetProperty("Instance", flags)?.GetValue(null);
            if (manager == null) return;
            object stats = type.GetProperty("Stats", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(manager);
            if (stats == null) return;
            Type statsType = stats.GetType();
            shotsValidated = (int)(statsType.GetField("ShotsValidated")?.GetValue(stats) ?? 0);
            hitsValidated = (int)(statsType.GetField("HitsValidated")?.GetValue(stats) ?? 0);
        }

        private static NetworkTransportBridge ResolveRunningBridge()
        {
            NetworkTransportBridge active = NetworkTransportBridge.Active;
            if (active != null && active.IsRunning) return active;

            NetworkTransportBridge[] bridges =
                UnityObjectSearch.FindAll<NetworkTransportBridge>(FindObjectsInactive.Exclude);
            for (int i = 0; i < bridges.Length; i++)
            {
                if (bridges[i] != null && bridges[i].IsRunning) return bridges[i];
            }

            return active;
        }
    }
}
