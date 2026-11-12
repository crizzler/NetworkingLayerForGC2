#if UNITY_EDITOR || DEBUG
using System;
using System.Collections;
using System.IO;
using System.Linq;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>Measures actual target delivery and rendered turning on every peer. Files coordinate probes only;
    /// no observer applies a target or rotation from a marker.</summary>
    public static class NetworkTargetingSmokeProbe
    {
        [Serializable] public sealed class Plan { public uint[] actors; public uint[] targets; }
        [Serializable] public sealed class Sample
        {
            public int step; public uint actor; public uint target; public uint revision;
            public float rootYawError; public float visualYawError; public float turnedDegrees;
        }
        [Serializable] public sealed class Result
        {
            public bool passed; public string role; public string phase; public string message;
            public Sample[] targetingSamples;
        }
        public static void Fail(string role, string message) => Finish(new Result { role = role, passed = false, phase = "targeting", message = message });
        private static void Finish(Result result)
        {
            NetworkSmokeArguments.TryGetArgument("--gc2-network-smoke-result", out string path);
            if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, JsonUtility.ToJson(result, true));
            Debug.Log($"[TargetingSmoke] role={result.role} passed={result.passed} {result.message}");
            Application.Quit(result.passed ? 0 : 1);
        }

        public static IEnumerator Run(string role, string marker)
        {
            Application.targetFrameRate = 60;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                foreach (MonoBehaviour component in UnityObjectSearch.FindAll<MonoBehaviour>(FindObjectsInactive.Include))
                    if (component != null && component.GetType().FullName == "UnityEngine.UI.GraphicRaycaster") component.enabled = false;
            NetworkSmokeArguments.TryGetArgument("--gc2-network-smoke-connection-phase", out string connectionPhase);
            bool lateJoin = connectionPhase == "latejoin";
            bool authority = role is "fusion-host" or "fusion-shared-master" or "purrnet-host" or "purrnet-server";
            string clientRole = role.StartsWith("fusion-shared", StringComparison.Ordinal) ? "fusion-shared-client" :
                role.StartsWith("fusion-", StringComparison.Ordinal) ? "fusion-client" : "purrnet-client";
            string authorityRole = role.StartsWith("fusion-shared", StringComparison.Ordinal) ? "fusion-shared-master" :
                role.StartsWith("fusion-", StringComparison.Ordinal) ? "fusion-host" : "purrnet-host";
            float deadline = Time.realtimeSinceStartup + NetworkSmokeArguments.GetIntArgument("--gc2-network-smoke-timeout", 150);
            string planPath = marker + ".targeting-plan";
            string stagePath = marker + ".targeting-stage";
            Plan plan = null;
            bool localReadyWritten = false;
            var samples = new System.Collections.Generic.List<Sample>();
            float nextDiagnostic = 0;
            int step = -1; float started = 0; float startYaw = 0; float turned = 0; int stable = 0; bool issued = false; bool captured = false;
            while (Time.realtimeSinceStartup < deadline)
            {
                // Completion must survive the first observer's disconnect.
                if (plan != null && File.Exists(stagePath) && File.ReadAllText(stagePath) == plan.actors.Length.ToString())
                {
                    if (!authority && !lateJoin)
                    {
                        File.WriteAllText(marker + ".targeting-finished." + role, "observed");
                        Finish(new Result { passed = samples.Count == plan.actors.Length, role = role, phase = "targeting", targetingSamples = samples.ToArray(), message = "NPC, PvP, clear and retarget observed on both owners." });
                        yield break;
                    }
                    if (authority && File.Exists(marker + ".targeting-finished." + clientRole) &&
                        (!NetworkSmokeArguments.HasFlag("--gc2-network-smoke-verify-latejoin") || File.Exists(marker + ".targeting-latejoin-observed")))
                    {
                        Finish(new Result { passed = samples.Count == plan.actors.Length, role = role, phase = "targeting", targetingSamples = samples.ToArray(), message = "Both owners observed; fresh observer recovered selected target." });
                        yield break;
                    }
                }
                NetworkTransportBridge bridge = NetworkTransportBridge.Active;
                NetworkCharacter[] actors = UnityObjectSearch.FindAll<NetworkCharacter>(FindObjectsInactive.Exclude)
                    .Where(x => x.NetworkId != 0 && x.Role != NetworkCharacter.NetworkRole.None).ToArray();
                NetworkCharacter[] humans = actors.Where(x => x.IsPlayerOwnedActor && x.HasAuthenticatedPlayerOwner && x.GetComponent<NetworkCombatTarget>() != null)
                    .OrderBy(x => x.NetworkId).ToArray();
                NetworkCharacter[] npcs = actors.Where(x => x.IsServerAuthoritativeNPC).OrderBy(x => x.transform.position.x).ToArray();
                if (bridge == null || !bridge.IsLocalGameplayReady || humans.Length != 2 || npcs.Length != 9)
                {
                    if (Time.realtimeSinceStartup >= nextDiagnostic)
                    {
                        nextDiagnostic = Time.realtimeSinceStartup + 3;
                        Debug.Log($"[TargetingSmoke] waiting step={step} ready={bridge != null && bridge.IsLocalGameplayReady} humans={humans.Length} npcs={npcs.Length}");
                    }
                    yield return null; continue;
                }
                if (lateJoin)
                {
                    if (!File.Exists(planPath)) { yield return null; continue; }
                    plan = JsonUtility.FromJson<Plan>(File.ReadAllText(planPath));
                    NetworkCharacter snapshotActor = actors.FirstOrDefault(x => x.NetworkId == plan.actors[0]);
                    Character snapshotTarget = bridge.ResolveCharacter(plan.targets[3]);
                    NetworkCombatTarget snapshot = snapshotActor != null ? snapshotActor.GetComponent<NetworkCombatTarget>() : null;
                    if (snapshot == null || snapshotTarget == null) { yield return null; continue; }
                    Transform snapshotVisual = snapshotActor.Character.Animim.Animator?.transform;
                    float lateRootError = Error(snapshotActor.transform.forward, snapshotActor.transform.position, snapshotTarget.transform.position);
                    float lateVisualError = snapshotVisual != null ? Error(snapshotVisual.forward, snapshotActor.transform.position, snapshotTarget.transform.position) : 180;
                    Sample previous = JsonUtility.FromJson<Sample>(File.ReadAllText(marker + ".targeting-step-3." + authorityRole));
                    bool lateMatches = !snapshotActor.IsOwnerInstance && snapshot.TargetNetworkId == previous.target && snapshot.Revision == previous.revision &&
                        snapshotActor.Character.Combat.Targets.Primary == snapshotTarget.gameObject && lateRootError <= 6 && lateVisualError <= 8;
                    stable = lateMatches ? stable + 1 : 0;
                    if (stable >= 20)
                    {
                        var sample = new Sample { step = 3, actor = snapshotActor.NetworkId, target = snapshot.TargetNetworkId, revision = snapshot.Revision,
                            rootYawError = lateRootError, visualYawError = lateVisualError };
                        File.WriteAllText(marker + ".targeting-latejoin-observed", JsonUtility.ToJson(sample));
                        Finish(new Result { passed = true, role = role, phase = "targeting-latejoin", targetingSamples = new[] { sample }, message = "Fresh process recovered authority target without issuing selection." });
                        yield break;
                    }
                    yield return null;
                    continue;
                }
                if (!localReadyWritten)
                {
                    File.WriteAllText(marker + ".targeting-local-ready." + role, "ready");
                    localReadyWritten = true;
                }
                if (authority && plan == null && File.Exists(marker + ".targeting-local-ready." + clientRole) && humans.All(x => x.Character.Facing is UnitFacingNetworkTarget))
                {
                    uint a = humans[0].NetworkId, b = humans[1].NetworkId;
                    uint[] aTargets = ChooseNpcTargets(humans[0], humans[1], npcs);
                    uint[] bTargets = ChooseNpcTargets(humans[1], humans[0], npcs);
                    plan = new Plan
                    {
                        actors = new[] { a, a, a, a, b, b, b, b },
                        targets = new[] { aTargets[0], b, 0u, aTargets[1], bTargets[0], a, 0u, bTargets[1] }
                    };
                    File.WriteAllText(planPath, JsonUtility.ToJson(plan));
                    File.WriteAllText(stagePath, "0");
                }
                if (plan == null && File.Exists(planPath)) plan = JsonUtility.FromJson<Plan>(File.ReadAllText(planPath));
                if (plan == null || !File.Exists(stagePath) || !int.TryParse(File.ReadAllText(stagePath), out int next)) { yield return null; continue; }
                if (next == plan.actors.Length) { yield return null; continue; }
                NetworkCharacter actor = actors.FirstOrDefault(x => x.NetworkId == plan.actors[next]);
                Character target = plan.targets[next] != 0 ? bridge.ResolveCharacter(plan.targets[next]) : null;
                if (actor == null || (plan.targets[next] != 0 && target == null)) { yield return null; continue; }
                if (step != next)
                {
                    step = next; started = Time.realtimeSinceStartup; startYaw = actor.transform.eulerAngles.y; turned = 0; stable = 0; issued = false; captured = false;
                    Debug.Log($"[TargetingSmoke] step={step} actor={actor.NetworkId} target={plan.targets[step]} owner={actor.IsOwnerInstance} initialYaw={startYaw:F2}");
                }
                if (actor.IsOwnerInstance && !issued && Time.realtimeSinceStartup - started > 0.4f)
                {
                    issued = true;
                    if (target == null) actor.Character.Combat.Targets.Primary = null;
                    else
                    {
                        // This is the exact GC2 operation called by the original TAB Instruction.
                        actor.Character.Combat.Targets.List.Clear();
                        actor.Character.Combat.Targets.AddCandidate(target.gameObject);
                        CycleTargets.Closest(actor.Character);
                    }
                }
                NetworkCombatTarget state = actor.GetComponent<NetworkCombatTarget>();
                float rootError = target != null ? Error(actor.transform.forward, actor.transform.position, target.transform.position) : 0;
                Transform visual = actor.Character.Animim.Animator != null ? actor.Character.Animim.Animator.transform : null;
                float visualError = target != null && visual != null ? Error(visual.forward, actor.transform.position, target.transform.position) : 0;
                turned = Mathf.Max(turned, Mathf.Abs(Mathf.DeltaAngle(startYaw, actor.transform.eulerAngles.y)));
                bool matches = state != null && state.TargetNetworkId == plan.targets[step] &&
                    actor.Character.Combat.Targets.Primary == (target != null ? target.gameObject : null) &&
                    rootError <= 6f && visual != null && visualError <= 8f && (target == null || turned > 10f);
                stable = matches ? stable + 1 : 0;
                if (Time.realtimeSinceStartup >= nextDiagnostic)
                {
                    nextDiagnostic = Time.realtimeSinceStartup + 3;
                    Debug.Log($"[TargetingSmoke] measuring step={step} requested={plan.targets[step]} confirmed={state?.TargetNetworkId} revision={state?.Revision} primary={actor.Character.Combat.Targets.Primary?.name} rootError={rootError:F2} visualError={visualError:F2} turned={turned:F2} actorPos={actor.transform.position:F3} targetPos={(target != null ? target.transform.position.ToString("F3") : "none")} yaw={actor.transform.eulerAngles.y:F2} busy={actor.Character.Busy.IsBusy} legsBusy={actor.Character.Busy.AreLegsBusy}");
                }
                if (!captured && stable >= 20 && Time.realtimeSinceStartup - started >= 1f)
                {
                    captured = true;
                    var sample = new Sample { step = step, actor = actor.NetworkId, target = state.TargetNetworkId, revision = state.Revision,
                        rootYawError = rootError, visualYawError = visualError, turnedDegrees = turned };
                    samples.Add(sample);
                    File.WriteAllText(marker + ".targeting-step-" + step + "." + role, JsonUtility.ToJson(sample));
                    Debug.Log($"[TargetingSmoke] observed step={step} rootError={rootError:F2} visualError={visualError:F2} turned={turned:F2}");
                    if (step == 1 && !Application.isBatchMode && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null &&
                        NetworkSmokeArguments.TryGetArgument("--gc2-network-smoke-screenshot", out string screenshot))
                        ScreenCapture.CaptureScreenshot(screenshot);
                }
                if (authority && captured && File.Exists(marker + ".targeting-step-" + step + "." + clientRole))
                    File.WriteAllText(stagePath, (step + 1).ToString());
                yield return null;
            }
            Fail(role, $"Timed out at step {step}; samples={samples.Count}; issued={issued}; turned={turned:F2}.");
        }
        private static uint[] ChooseNpcTargets(NetworkCharacter actor, NetworkCharacter other, NetworkCharacter[] npcs)
        {
            Vector3 otherDirection = Vector3.ProjectOnPlane(other.transform.position - actor.transform.position, Vector3.up);
            return npcs.Where(x => Error(actor.transform.forward, actor.transform.position, x.transform.position) >= 25)
                .OrderByDescending(x => Vector3.Angle(otherDirection, Vector3.ProjectOnPlane(x.transform.position - actor.transform.position, Vector3.up)))
                .ThenBy(x => x.NetworkId).Take(2).Select(x => x.NetworkId).ToArray();
        }

        private static float Error(Vector3 forward, Vector3 origin, Vector3 target) =>
            Vector3.Angle(Vector3.ProjectOnPlane(forward, Vector3.up), Vector3.ProjectOnPlane(target - origin, Vector3.up));
    }
}
#endif
