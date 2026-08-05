#if GC2_TRAVERSAL
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Traversal;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Traversal
{
    /// <summary>
    /// Runtime installer for Traversal patch delegates.
    /// In patched mode, direct traversal calls are rerouted through NetworkTraversalController requests.
    /// </summary>
    public class NetworkTraversalPatchHooks : NetworkSingleton<NetworkTraversalPatchHooks>
    {
        private const BindingFlags STATIC_PUBLIC = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags INSTANCE_ALL = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const float LEDGE_EDGE_INPUT_THRESHOLD = 0.25f;
        private const float LEDGE_EDGE_POSITION_TOLERANCE = 0.06f;
        private const float LEDGE_EDGE_INTENT_MEMORY_SECONDS = 0.45f;
        private const float LEDGE_EDGE_OVERRIDE_LOG_INTERVAL = 0.35f;

        private static readonly PropertyInfo s_TraversalStanceRelativePositionProperty =
            typeof(TraversalStance).GetProperty("RelativePosition", INSTANCE_ALL);

        private bool m_IsServer;
        private bool m_Installed;
        private bool m_AnimationOverrideInstalled;
        private bool m_LogDiagnostics;
        private float m_LastLedgeEdgeOverrideLogTime;
        private static bool s_LoggedMissingRequiredPatch;
        private readonly Dictionary<int, LedgeEdgeIntentMemory> m_LedgeEdgeIntentMemory = new();
        private readonly Dictionary<string, float> m_RouteDiagnosticTimes = new(StringComparer.Ordinal);

        private struct LedgeEdgeIntentMemory
        {
            public int TraverseInstanceId;
            public float Direction;
            public float Timestamp;
        }

        public bool IsPatchActive => m_Installed && IsTraversalPatched();

        public void Initialize(bool isServer, bool isActive = true, bool logDiagnostics = false)
        {
            m_IsServer = isServer;
            m_LogDiagnostics = logDiagnostics;
            if (isActive) InstallHooks();
            else UninstallHooks();
        }

        protected override void OnSingletonCleanup()
        {
            UninstallHooks();
        }

        public static bool IsTraversalPatched()
        {
            Type traverseLinkType = typeof(TraverseLink);
            Type traverseInteractiveType = typeof(TraverseInteractive);
            Type motionInteractiveType = typeof(MotionInteractive);
            Type traversalStanceType = typeof(TraversalStance);

            return
                HasPublicStaticField(traverseLinkType, "NetworkRunValidator", typeof(Func<TraverseLink, Character, bool>)) &&
                HasPublicStaticField(traverseInteractiveType, "NetworkEnterValidator", typeof(Func<TraverseInteractive, Character, InteractiveTransitionData, bool>)) &&
                HasPublicStaticField(motionInteractiveType, "NetworkEdgeConnectionResolver", typeof(Func<MotionInteractive, TraverseInteractive, Character, Vector3, Vector3, bool, Traverse>)) &&
                HasPublicStaticField(motionInteractiveType, "NetworkConnectionSkipTransitionResolver", typeof(Func<Traverse, Traverse, Character, bool>)) &&
                HasPublicInstanceMethod(
                    motionInteractiveType,
                    "NetworkResumeInteractiveSnapshot",
                    typeof(Task<bool>),
                    typeof(TraverseInteractive),
                    typeof(Character),
                    typeof(TraversalToken)) &&
                HasPublicStaticField(traversalStanceType, "NetworkTryCancelValidator", typeof(Func<TraversalStance, Args, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkForceCancelValidator", typeof(Func<TraversalStance, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkTryJumpValidator", typeof(Func<TraversalStance, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkTryActionValidator", typeof(Func<TraversalStance, IdString, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkTryStateEnterValidator", typeof(Func<TraversalStance, IdString, bool>)) &&
                HasPublicStaticField(traversalStanceType, "NetworkTryStateExitValidator", typeof(Func<TraversalStance, bool>)) &&
                HasPublicInstanceMethod(
                    traversalStanceType,
                    "NetworkRestoreInteractiveSnapshot",
                    typeof(bool),
                    typeof(TraverseInteractive),
                    typeof(Vector3)) &&
                HasPublicInstanceMethod(
                    traversalStanceType,
                    "NetworkClearSnapshot",
                    typeof(bool)) &&
                HasPublicInstanceMethod(
                    traversalStanceType,
                    "NetworkInvalidatePendingEnter",
                    typeof(void)) &&
                HasPublicInstanceProperty(
                    traversalStanceType,
                    "NetworkSnapshotToken",
                    typeof(TraversalToken));
        }

        private void InstallHooks()
        {
            InstallAnimationInputOverride();

            if (m_Installed)
            {
                if (DiagnosticsEnabled)
                {
                    Debug.Log($"[NetworkTraversalPatchHooks] Traversal patch hooks refreshed. server={m_IsServer}");
                }

                return;
            }

            if (!IsTraversalPatched())
            {
                if (!s_LoggedMissingRequiredPatch)
                {
                    Debug.LogError(
                        "[NetworkTraversalPatchHooks] Required Game Creator Traversal patch markers were not " +
                        "detected. Network traversal requests are disabled until the Networking Layer setup " +
                        "wizard applies and validates the compatible patch.");
                    s_LoggedMissingRequiredPatch = true;
                }

                return;
            }

            s_LoggedMissingRequiredPatch = false;

            SetStaticField(typeof(TraverseLink), "NetworkRunValidator", new Func<TraverseLink, Character, bool>(ValidateRunTraverseLink));
            SetStaticField(typeof(TraverseInteractive), "NetworkEnterValidator", new Func<TraverseInteractive, Character, InteractiveTransitionData, bool>(ValidateEnterTraverseInteractive));
            SetStaticField(typeof(MotionInteractive), "NetworkEdgeConnectionResolver", new Func<MotionInteractive, TraverseInteractive, Character, Vector3, Vector3, bool, Traverse>(ResolveInteractiveEdgeConnection));
            SetStaticField(typeof(MotionInteractive), "NetworkConnectionSkipTransitionResolver", new Func<Traverse, Traverse, Character, bool>(ShouldSkipConnectionTransition));

            SetStaticField(typeof(TraversalStance), "NetworkTryCancelValidator", new Func<TraversalStance, Args, bool>(ValidateTryCancel));
            SetStaticField(typeof(TraversalStance), "NetworkForceCancelValidator", new Func<TraversalStance, bool>(ValidateForceCancel));
            SetStaticField(typeof(TraversalStance), "NetworkTryJumpValidator", new Func<TraversalStance, bool>(ValidateTryJump));
            SetStaticField(typeof(TraversalStance), "NetworkTryActionValidator", new Func<TraversalStance, IdString, bool>(ValidateTryAction));
            SetStaticField(typeof(TraversalStance), "NetworkTryStateEnterValidator", new Func<TraversalStance, IdString, bool>(ValidateTryStateEnter));
            SetStaticField(typeof(TraversalStance), "NetworkTryStateExitValidator", new Func<TraversalStance, bool>(ValidateTryStateExit));

            m_Installed = true;
            if (DiagnosticsEnabled)
            {
                Debug.Log($"[NetworkTraversalPatchHooks] Traversal patch hooks installed. server={m_IsServer}");
            }
        }

        private void UninstallHooks()
        {
            UninstallAnimationInputOverride();
            ClearAllLedgeEdgeIntents();

            if (!m_Installed) return;

            SetStaticField(typeof(TraverseLink), "NetworkRunValidator", null);
            SetStaticField(typeof(TraverseInteractive), "NetworkEnterValidator", null);
            SetStaticField(typeof(MotionInteractive), "NetworkEdgeConnectionResolver", null);
            SetStaticField(typeof(MotionInteractive), "NetworkConnectionSkipTransitionResolver", null);

            SetStaticField(typeof(TraversalStance), "NetworkTryCancelValidator", null);
            SetStaticField(typeof(TraversalStance), "NetworkForceCancelValidator", null);
            SetStaticField(typeof(TraversalStance), "NetworkTryJumpValidator", null);
            SetStaticField(typeof(TraversalStance), "NetworkTryActionValidator", null);
            SetStaticField(typeof(TraversalStance), "NetworkTryStateEnterValidator", null);
            SetStaticField(typeof(TraversalStance), "NetworkTryStateExitValidator", null);

            m_Installed = false;
        }

        private void InstallAnimationInputOverride()
        {
            UnitAnimimNetworkKinematic.TraversalAnimationInputOverride = ApplyTraversalAnimationInputOverride;
            m_AnimationOverrideInstalled = true;
        }

        private void UninstallAnimationInputOverride()
        {
            if (m_AnimationOverrideInstalled &&
                UnitAnimimNetworkKinematic.TraversalAnimationInputOverride == ApplyTraversalAnimationInputOverride)
            {
                UnitAnimimNetworkKinematic.TraversalAnimationInputOverride = null;
            }

            m_AnimationOverrideInstalled = false;
        }

        private bool ValidateRunTraverseLink(TraverseLink traverseLink, Character character)
        {
            NetworkTraversalController controller = ResolveController(character);
            return RouteClientAction(character, controller, () => controller.RequestRunTraverseLinkFromPatch(traverseLink, character));
        }

        private bool ValidateEnterTraverseInteractive(TraverseInteractive traverseInteractive, Character character, InteractiveTransitionData transition)
        {
            NetworkTraversalController controller = ResolveController(character);
            return RouteClientAction(character, controller, () => controller.RequestEnterTraverseInteractiveFromPatch(traverseInteractive, character, transition));
        }

        private bool ValidateTryCancel(TraversalStance stance, Args args)
        {
            NetworkTraversalController controller = ResolveController(stance);
            return RouteClientAction(stance?.Character, controller, () => controller.RequestTryCancelFromPatch(stance, args));
        }

        private bool ValidateForceCancel(TraversalStance stance)
        {
            NetworkTraversalController controller = ResolveController(stance);
            return RouteClientAction(stance?.Character, controller, () => controller.RequestForceCancelFromPatch(stance));
        }

        private bool ValidateTryJump(TraversalStance stance)
        {
            NetworkTraversalController controller = ResolveController(stance);
            return RouteClientAction(stance?.Character, controller, () => controller.RequestTryJumpFromPatch(stance));
        }

        private bool ValidateTryAction(TraversalStance stance, IdString actionId)
        {
            NetworkTraversalController controller = ResolveController(stance);
            Character character = stance?.Character;
            if (character != null &&
                !string.IsNullOrEmpty(actionId.String) &&
                actionId.String.IndexOf("PullUp", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                NetworkCharacter networkCharacter = character.GetComponent<NetworkCharacter>();
                NetworkTraversalClimbDiagnostics.SetCharacterFocus(
                    character.gameObject,
                    networkCharacter != null ? networkCharacter.NetworkId : 0,
                    true);
                NetworkTraversalClimbDiagnostics.Log(
                    "PatchInput",
                    $"actor={networkCharacter?.NetworkId ?? 0} role={networkCharacter?.CurrentRole.ToString() ?? "none"} " +
                    $"action=TryAction actionId='{actionId.String}' controllerReady={controller?.IsReadyForNetworkRouting ?? false} " +
                    $"traverse='{stance.Traverse?.name ?? "none"}' pos={NetworkTraversalClimbDiagnostics.Vector(character.transform.position)}",
                    character);
            }
            return RouteClientAction(stance?.Character, controller, () => controller.RequestTryActionFromPatch(stance, actionId));
        }

        private bool ValidateTryStateEnter(TraversalStance stance, IdString stateId)
        {
            NetworkTraversalController controller = ResolveController(stance);
            return RouteClientAction(stance?.Character, controller, () => controller.RequestTryStateEnterFromPatch(stance, stateId));
        }

        private bool ValidateTryStateExit(TraversalStance stance)
        {
            NetworkTraversalController controller = ResolveController(stance);
            return RouteClientAction(stance?.Character, controller, () => controller.RequestTryStateExitFromPatch(stance));
        }

        private bool RouteClientAction(
            Character character,
            NetworkTraversalController controller,
            Action requestAction)
        {
            NetworkCharacter networkCharacter = character != null
                ? character.GetComponent<NetworkCharacter>()
                : null;

            // Characters outside the networking layer retain normal GC2 local traversal.
            if (networkCharacter == null)
            {
                return true;
            }

            // A configured network character must never fall through to native local gameplay.
            if (controller == null || !controller.isActiveAndEnabled)
            {
                WarnRouteInvariant(
                    character,
                    "controller-missing",
                    "NetworkTraversalController is missing or disabled");
                return false;
            }

            if (controller.IsApplyingAuthoritativeChange)
            {
                return true;
            }

            // Ownership can be assigned in the same frame as GC2 invokes the patch. Refresh
            // the cached controller role before deciding that this is a remote proxy.
            controller.RefreshRoutingRoleFromNetworkCharacter();
            if (!controller.CanAcceptPatchedRequest(out TraversalRouteStatus routeStatus))
            {
                WarnRouteInvariant(
                    character,
                    $"route:{routeStatus}",
                    $"the authoritative traversal route is {routeStatus}");
                return false;
            }

            if (NetworkTraversalClimbDiagnostics.IsFocused(character.gameObject))
            {
                NetworkTraversalClimbDiagnostics.Log(
                    "PatchRoute",
                    $"actor={controller.NetworkId} role={networkCharacter.CurrentRole} route={routeStatus} " +
                    $"server={controller.IsServer} local={controller.IsLocalClient} remote={controller.IsRemoteClient} " +
                    $"nativeSuppressed=true",
                    character);
            }

            if (DiagnosticsEnabled)
            {
                Debug.Log(
                    $"[TraversalTrace][PatchHooks] routed character='{character.name}' " +
                    $"actor={controller.NetworkId} server={controller.IsServer} " +
                    $"local={controller.IsLocalClient} remote={controller.IsRemoteClient}",
                    character);
            }

            requestAction?.Invoke();
            return false;
        }

        private bool DiagnosticsEnabled =>
            m_LogDiagnostics ||
            (NetworkTraversalManager.Instance != null && NetworkTraversalManager.Instance.DiagnosticsEnabled);

        private void WarnRouteInvariant(Character character, string key, string reason)
        {
            float now = Time.realtimeSinceStartup;
            string diagnosticKey = $"{(character != null ? character.GetInstanceID() : 0)}:{key}";
            if (m_RouteDiagnosticTimes.TryGetValue(diagnosticKey, out float previous) && now - previous < 5f)
            {
                return;
            }

            m_RouteDiagnosticTimes[diagnosticKey] = now;
            Debug.LogWarning(
                $"[NetworkTraversalPatchHooks] Suppressed native local traversal for network character " +
                $"'{(character != null ? character.name : "unknown")}': {reason}. Run the setup wizard " +
                "for the active transport and verify the required Traversal patch, controller role, " +
                "and prediction backend.",
                character);
        }

        private Traverse ResolveInteractiveEdgeConnection(
            MotionInteractive motion,
            TraverseInteractive interactive,
            Character character,
            Vector3 currentLocalPosition,
            Vector3 localDirection,
            bool edgeB)
        {
            NetworkTraversalController controller = ResolveController(character);
            if (controller != null && controller.enabled)
            {
                return controller.ResolveInteractiveEdgeConnectionFromPatch(
                    motion,
                    interactive,
                    character,
                    currentLocalPosition,
                    localDirection,
                    edgeB);
            }

            NetworkCharacter networkCharacter = character != null
                ? character.GetComponent<NetworkCharacter>()
                : null;
            if (networkCharacter != null)
            {
                WarnRouteInvariant(
                    character,
                    "edge-controller-missing",
                    "NetworkTraversalController is missing or disabled while resolving an authored edge continuation");
                return null;
            }

            // Hooks are installed process-wide. Only a genuinely non-networked character
            // retains GC2's authored ContinueA/ContinueB behavior.
            return interactive != null
                ? edgeB ? interactive.ContinueB : interactive.ContinueA
                : null;
        }

        private bool ShouldSkipConnectionTransition(Traverse current, Traverse next, Character character)
        {
            NetworkTraversalController controller = ResolveController(character);
            return controller == null || !controller.enabled ||
                   controller.ShouldSkipConnectionTransitionFromPatch(current, next, character);
        }

        private bool ApplyTraversalAnimationInputOverride(
            Character character,
            ref Vector3 targetIntent,
            ref Vector3 targetSpeed,
            Vector3 currentSpeed)
        {
            if (character == null || character.Combat == null) return false;
            NetworkCharacter networkCharacter = character.GetComponent<NetworkCharacter>();

            TraversalStance stance = character.Combat.RequestStance<TraversalStance>();
            if (stance?.Traverse is not TraverseInteractive interactive)
            {
                ClearLedgeEdgeIntent(character);
                return false;
            }

            if (interactive.MotionInteractive == null)
            {
                ClearLedgeEdgeIntent(character);
                return false;
            }

            if (string.Equals(interactive.MotionInteractive.name, "Motion_Free_Climb", StringComparison.Ordinal))
            {
                ClearLedgeEdgeIntent(character);
                return ApplyFreeClimbEdgeAnimationOverride(
                    character,
                    networkCharacter,
                    stance,
                    interactive,
                    ref targetIntent,
                    ref targetSpeed,
                    currentSpeed);
            }

            if (!string.Equals(interactive.MotionInteractive.name, "Motion_Ledge_Climb", StringComparison.Ordinal))
            {
                ClearLedgeEdgeIntent(character);
                return false;
            }

            Vector3 targetIntentBefore = targetIntent;
            Vector3 targetSpeedBefore = targetSpeed;

            bool hasHorizontalInput = TryGetLiveHorizontalTraversalInput(
                character,
                out float horizontalInput,
                out string horizontalInputSource);
            bool hasVerticalInput = TryGetLiveVerticalTraversalInput(
                character,
                out float verticalInput,
                out string verticalInputSource);

            // Remote representations have no owner input unit. They do receive the exact
            // authored Motion.MoveDirection through the priority-9 motion broadcast, however.
            // Convert Animim's character-local intent back into the Traverse local plane so
            // every observer can make the same blocked-edge decision as the owner.
            if (TryGetObservedTraversalLocalIntent(
                    character,
                    networkCharacter,
                    interactive,
                    targetIntentBefore,
                    targetSpeedBefore,
                    out Vector3 observedTraversalIntent))
            {
                if (!hasHorizontalInput &&
                    Mathf.Abs(observedTraversalIntent.z) >= LEDGE_EDGE_INPUT_THRESHOLD)
                {
                    hasHorizontalInput = true;
                    horizontalInput = observedTraversalIntent.z;
                    horizontalInputSource = "observed-motion-z";
                }

                if (!hasVerticalInput &&
                    Mathf.Abs(observedTraversalIntent.y) >= LEDGE_EDGE_INPUT_THRESHOLD)
                {
                    hasVerticalInput = true;
                    verticalInput = observedTraversalIntent.y;
                    verticalInputSource = "observed-motion-y";
                }
            }

            // A ledge traverse is one-dimensional: ClampInBounds always fixes its local
            // Y coordinate, so forward/back input cannot produce locomotion. GC2's climb
            // controller represents that blocked direction through Intent-Y (Edge Forward /
            // Edge Backward), while a non-zero Speed-Y selects Move Forward / Move Backward.
            // Resolve the live input directly because the mapped intent remains on Z and the
            // attempted driver velocity remains on Y even though the stance cannot move.
            bool hasDominantVerticalInput = hasVerticalInput &&
                                            Mathf.Abs(verticalInput) > Mathf.Abs(horizontalInput);
            if (hasDominantVerticalInput)
            {
                ClearLedgeEdgeIntent(character);
                targetSpeed = Vector3.zero;
                targetIntent = new Vector3(0f, Mathf.Sign(verticalInput), 0f);

                LogFocusedVerticalLedgeInput(
                    character,
                    networkCharacter,
                    interactive,
                    verticalInput,
                    verticalInputSource,
                    GetTraversalLocalPosition(stance, interactive, character),
                    targetIntentBefore,
                    targetSpeedBefore,
                    targetIntent,
                    targetSpeed,
                    currentSpeed);
                return true;
            }

            bool remembered = false;
            if (!hasHorizontalInput &&
                TryGetRememberedLedgeEdgeIntent(character, interactive, out float rememberedInput))
            {
                horizontalInput = rememberedInput;
                horizontalInputSource = "remembered-edge-intent";
                remembered = true;
            }

            Vector3 localPosition = GetTraversalLocalPosition(stance, interactive, character);
            float edgePositionTolerance = GetTraversalEdgePositionTolerance(character);
            if (Mathf.Abs(horizontalInput) < LEDGE_EDGE_INPUT_THRESHOLD)
            {
                ClearLedgeEdgeIntent(character);
                LogFocusedLedgeInput(
                    character,
                    networkCharacter,
                    interactive,
                    horizontalInput,
                    horizontalInputSource,
                    remembered,
                    localPosition,
                    false,
                    false,
                    targetIntentBefore,
                    targetSpeedBefore,
                    targetIntent,
                    targetSpeed,
                    currentSpeed,
                    false);
                return false;
            }

            bool pushingA = localPosition.z <= interactive.PositionA + edgePositionTolerance &&
                            horizontalInput < -LEDGE_EDGE_INPUT_THRESHOLD;
            bool pushingB = localPosition.z >= interactive.PositionB - edgePositionTolerance &&
                            horizontalInput > LEDGE_EDGE_INPUT_THRESHOLD;

            if (!pushingA && !pushingB)
            {
                // Edge intent is only useful while actually holding against an authored
                // ledge boundary. Carrying it through the middle of the ledge makes a
                // later animation frame look like fresh input and can restart the blend.
                ClearLedgeEdgeIntent(character);
                LogFocusedLedgeInput(
                    character,
                    networkCharacter,
                    interactive,
                    horizontalInput,
                    horizontalInputSource,
                    remembered,
                    localPosition,
                    false,
                    false,
                    targetIntentBefore,
                    targetSpeedBefore,
                    targetIntent,
                    targetSpeed,
                    currentSpeed,
                    false);
                return false;
            }

            if (hasHorizontalInput)
            {
                RememberLedgeEdgeIntent(character, interactive, horizontalInput);

                // The authored Traverse blend tree uses Speed-XY to choose locomotion and
                // Intent-X/Y to choose the blocked-edge pose. At an exact rail boundary GC2
                // alternates its attempted movement velocity between full speed and zero as
                // the position clamp runs. Passing that velocity through makes the tree swap
                // MoveL/MoveR and IntentL/IntentR every few frames. A held outward input at a
                // confirmed boundary is not locomotion: hold zero speed and the exact intent.
                targetSpeed = Vector3.zero;
                targetIntent = new Vector3(Mathf.Sign(horizontalInput), 0f, 0f);
            }
            else
            {
                // Once input is released, a very short memory selects GC2's authored
                // zero-speed edge pose without pretending that locomotion is still held.
                targetSpeed = Vector3.zero;
                targetIntent = new Vector3(Mathf.Sign(horizontalInput), 0f, 0f);
            }

            LogFocusedLedgeInput(
                character,
                networkCharacter,
                interactive,
                horizontalInput,
                horizontalInputSource,
                remembered,
                localPosition,
                pushingA,
                pushingB,
                targetIntentBefore,
                targetSpeedBefore,
                targetIntent,
                targetSpeed,
                currentSpeed,
                true);

            float now = Time.time;
            if (DiagnosticsEnabled &&
                now - m_LastLedgeEdgeOverrideLogTime >= LEDGE_EDGE_OVERRIDE_LOG_INTERVAL)
            {
                m_LastLedgeEdgeOverrideLogTime = now;
                Debug.Log(
                    $"[TraversalAnimDebug][PatchHooks] ledge edge intent override " +
                    $"character='{character.name}' traverse='{interactive.name}' " +
                    $"role={(networkCharacter != null ? networkCharacter.CurrentRole.ToString() : "none")} " +
                    $"inputX={horizontalInput:F3} localZ={localPosition.z:F3} " +
                    $"boundsA={interactive.PositionA:F3} boundsB={interactive.PositionB:F3} " +
                    $"edgeTolerance={edgePositionTolerance:F3} " +
                    $"edge={(pushingA ? "A" : "B")} intent={FormatVector(targetIntent)} speed={FormatVector(targetSpeed)}",
                    character);
            }

            return true;
        }

        private static bool ApplyFreeClimbEdgeAnimationOverride(
            Character character,
            NetworkCharacter networkCharacter,
            TraversalStance stance,
            TraverseInteractive interactive,
            ref Vector3 targetIntent,
            ref Vector3 targetSpeed,
            Vector3 currentSpeed)
        {
            Vector3 intentBefore = targetIntent;
            Vector3 speedBefore = targetSpeed;
            Vector3 localPosition = GetTraversalLocalPosition(stance, interactive, character);
            float halfWidth = interactive.Width * 0.5f;
            float edgePositionTolerance = GetTraversalEdgePositionTolerance(character);

            Vector2 climbPlaneIntent = new Vector2(intentBefore.x, intentBefore.z);
            string inputSource = "local-animation-intent";
            if (character.Player is UnitPlayerDirectionalNetwork networkPlayer &&
                networkPlayer.RawInput.sqrMagnitude >=
                LEDGE_EDGE_INPUT_THRESHOLD * LEDGE_EDGE_INPUT_THRESHOLD)
            {
                climbPlaneIntent = networkPlayer.RawInput;
                inputSource = "owner-raw-input";
            }
            else if (TryGetObservedTraversalLocalIntent(
                         character,
                         networkCharacter,
                         interactive,
                         intentBefore,
                         speedBefore,
                         out Vector3 observedTraversalIntent))
            {
                climbPlaneIntent = new Vector2(
                    observedTraversalIntent.x,
                    observedTraversalIntent.z);
                inputSource = "observed-motion";
            }

            bool pushingLeft =
                halfWidth > float.Epsilon &&
                localPosition.x <= -halfWidth + edgePositionTolerance &&
                climbPlaneIntent.x < -LEDGE_EDGE_INPUT_THRESHOLD;
            bool pushingRight =
                halfWidth > float.Epsilon &&
                localPosition.x >= halfWidth - edgePositionTolerance &&
                climbPlaneIntent.x > LEDGE_EDGE_INPUT_THRESHOLD;
            bool pushingDown =
                localPosition.z <= interactive.PositionA + edgePositionTolerance &&
                climbPlaneIntent.y < -LEDGE_EDGE_INPUT_THRESHOLD;
            bool pushingUp =
                localPosition.z >= interactive.PositionB - edgePositionTolerance &&
                climbPlaneIntent.y > LEDGE_EDGE_INPUT_THRESHOLD;

            string edge = pushingLeft ? "left" :
                pushingRight ? "right" :
                pushingDown ? "down" :
                pushingUp ? "up" :
                "none";
            if (edge == "none") return false;

            // Motion_Free_Climb maps player X/Z into the climb plane X/Y. The Traverse
            // controller's blocked-edge subtree reads Intent-X/Y, while Speed-XY selects
            // movement. Remap the held input and force zero speed only at a clamped edge.
            if (climbPlaneIntent.sqrMagnitude > 1f) climbPlaneIntent.Normalize();
            targetIntent = new Vector3(climbPlaneIntent.x, climbPlaneIntent.y, 0f);
            targetSpeed = Vector3.zero;

            if (NetworkTraversalClimbDiagnostics.IsFocused(character.gameObject))
            {
                string signature =
                    $"{edge}:{AxisSign(targetIntent.x)},{AxisSign(targetIntent.y)}";
                bool changed = NetworkTraversalClimbDiagnostics.HasChanged(
                    $"free-edge-override:{character.GetInstanceID()}",
                    signature);
                NetworkTraversalClimbDiagnostics.Log(
                    changed ? "FreeEdgeOverrideChange" : "FreeEdgeOverride",
                    $"actor={networkCharacter?.NetworkId ?? 0} " +
                    $"role={networkCharacter?.CurrentRole.ToString() ?? "none"} " +
                    $"traverse='{interactive.name}' edge={edge} source={inputSource} " +
                    $"local={NetworkTraversalClimbDiagnostics.Vector(localPosition)} " +
                    $"boundsX={-halfWidth:F3}/{halfWidth:F3} " +
                    $"boundsZ={interactive.PositionA:F3}/{interactive.PositionB:F3} " +
                    $"edgeTolerance={edgePositionTolerance:F3} " +
                    $"intentPre={NetworkTraversalClimbDiagnostics.Vector(intentBefore)} " +
                    $"intentPost={NetworkTraversalClimbDiagnostics.Vector(targetIntent)} " +
                    $"speedPre={NetworkTraversalClimbDiagnostics.Vector(speedBefore)} " +
                    $"speedPost={NetworkTraversalClimbDiagnostics.Vector(targetSpeed)} " +
                    $"currentSpeed={NetworkTraversalClimbDiagnostics.Vector(currentSpeed)}",
                    character,
                    changed ? null : $"free-edge-override:{character.GetInstanceID()}");
            }

            return true;
        }

        private static bool TryGetLiveHorizontalTraversalInput(
            Character character,
            out float horizontalInput,
            out string source)
        {
            horizontalInput = 0f;
            if (character?.Player is UnitPlayerDirectionalNetwork networkPlayer)
            {
                Vector2 rawInput = networkPlayer.RawInput;
                if (Mathf.Abs(rawInput.x) >= LEDGE_EDGE_INPUT_THRESHOLD)
                {
                    source = "raw-input";
                    horizontalInput = rawInput.x;
                    return true;
                }
            }

            float localInput = character?.Player?.LocalInputDirection.x ?? 0f;
            if (Mathf.Abs(localInput) >= LEDGE_EDGE_INPUT_THRESHOLD)
            {
                source = "local-input";
                horizontalInput = localInput;
                return true;
            }

            source = "none";
            return false;
        }

        private static bool TryGetLiveVerticalTraversalInput(
            Character character,
            out float verticalInput,
            out string source)
        {
            verticalInput = 0f;
            if (character?.Player is UnitPlayerDirectionalNetwork networkPlayer)
            {
                Vector2 rawInput = networkPlayer.RawInput;
                if (Mathf.Abs(rawInput.y) >= LEDGE_EDGE_INPUT_THRESHOLD)
                {
                    source = "raw-input-y";
                    verticalInput = rawInput.y;
                    return true;
                }
            }

            // Player local forward is Z. This fallback keeps custom/local player units
            // compatible without using the climb-plane velocity that caused the regression.
            float localInput = character?.Player?.LocalInputDirection.z ?? 0f;
            if (Mathf.Abs(localInput) >= LEDGE_EDGE_INPUT_THRESHOLD)
            {
                source = "local-input-z";
                verticalInput = localInput;
                return true;
            }

            source = "none";
            return false;
        }

        private static bool TryGetObservedTraversalLocalIntent(
            Character character,
            NetworkCharacter networkCharacter,
            TraverseInteractive interactive,
            Vector3 characterLocalIntent,
            Vector3 characterLocalSpeed,
            out Vector3 traversalLocalIntent)
        {
            traversalLocalIntent = Vector3.zero;
            if (character == null ||
                networkCharacter == null ||
                networkCharacter.IsOwnerInstance ||
                interactive == null)
            {
                return false;
            }

            Vector3 worldIntent;
            if (character.Motion is UnitMotionNetworkController networkMotion &&
                networkMotion.TryGetTraversalPresentationDirection(out Vector3 presentationDirection))
            {
                worldIntent = presentationDirection;
            }
            else if (characterLocalIntent.sqrMagnitude >=
                     LEDGE_EDGE_INPUT_THRESHOLD * LEDGE_EDGE_INPUT_THRESHOLD)
            {
                worldIntent = character.transform.TransformDirection(characterLocalIntent);
            }
            else if (characterLocalSpeed.sqrMagnitude >=
                     LEDGE_EDGE_INPUT_THRESHOLD * LEDGE_EDGE_INPUT_THRESHOLD)
            {
                worldIntent = character.transform.TransformDirection(characterLocalSpeed);
            }
            else
            {
                return false;
            }

            traversalLocalIntent = interactive.Transform.InverseTransformDirection(worldIntent);
            if (traversalLocalIntent.sqrMagnitude > 1f)
            {
                traversalLocalIntent.Normalize();
            }

            return traversalLocalIntent.sqrMagnitude >=
                   LEDGE_EDGE_INPUT_THRESHOLD * LEDGE_EDGE_INPUT_THRESHOLD;
        }

        private void RememberLedgeEdgeIntent(
            Character character,
            TraverseInteractive interactive,
            float horizontalInput)
        {
            if (character == null || interactive == null) return;

            int key = character.GetInstanceID();
            float direction = Mathf.Sign(horizontalInput);
            bool changed = !m_LedgeEdgeIntentMemory.TryGetValue(key, out LedgeEdgeIntentMemory previous) ||
                           previous.TraverseInstanceId != interactive.GetInstanceID() ||
                           !Mathf.Approximately(previous.Direction, direction);

            if (changed && m_LedgeEdgeIntentMemory.ContainsKey(key))
            {
                // A reversal starts a fresh lease; it must not inherit the prior edge's
                // release window.
                ClearLedgeEdgeIntent(character);
            }

            m_LedgeEdgeIntentMemory[key] = new LedgeEdgeIntentMemory
            {
                TraverseInstanceId = interactive.GetInstanceID(),
                Direction = direction,
                Timestamp = Time.time
            };

            if (changed && NetworkTraversalClimbDiagnostics.IsFocused(character.gameObject))
            {
                NetworkCharacter networkCharacter = character.GetComponent<NetworkCharacter>();
                NetworkTraversalClimbDiagnostics.Log(
                    "EdgeIntent",
                    $"actor={networkCharacter?.NetworkId ?? 0} role={networkCharacter?.CurrentRole.ToString() ?? "none"} " +
                    $"operation=remember traverse='{interactive.name}' direction={direction:F0}",
                    character);
            }
        }

        private bool TryGetRememberedLedgeEdgeIntent(
            Character character,
            TraverseInteractive interactive,
            out float horizontalInput)
        {
            horizontalInput = 0f;
            if (character == null || interactive == null) return false;

            int key = character.GetInstanceID();
            if (!m_LedgeEdgeIntentMemory.TryGetValue(key, out LedgeEdgeIntentMemory memory))
            {
                return false;
            }

            if (memory.TraverseInstanceId != interactive.GetInstanceID() ||
                Time.time - memory.Timestamp > LEDGE_EDGE_INTENT_MEMORY_SECONDS)
            {
                m_LedgeEdgeIntentMemory.Remove(key);
                if (NetworkTraversalClimbDiagnostics.IsFocused(character.gameObject))
                {
                    NetworkCharacter networkCharacter = character.GetComponent<NetworkCharacter>();
                    NetworkTraversalClimbDiagnostics.Log(
                        "EdgeIntent",
                        $"actor={networkCharacter?.NetworkId ?? 0} role={networkCharacter?.CurrentRole.ToString() ?? "none"} " +
                        $"operation=expire traverse='{interactive.name}' age={Time.time - memory.Timestamp:F3}",
                        character);
                }
                return false;
            }

            horizontalInput = memory.Direction;
            return Mathf.Abs(horizontalInput) >= LEDGE_EDGE_INPUT_THRESHOLD;
        }

        /// <summary>
        /// Clears short-lived ledge animation intent for a character. This is deliberately
        /// presentation-only and does not alter traversal state or send a request.
        /// </summary>
        internal void ClearLedgeEdgeIntent(Character character)
        {
            if (character == null) return;
            m_LedgeEdgeIntentMemory.Remove(character.GetInstanceID());
        }

        private void ClearAllLedgeEdgeIntents()
        {
            m_LedgeEdgeIntentMemory.Clear();
        }

        private static void LogFocusedLedgeInput(
            Character character,
            NetworkCharacter networkCharacter,
            TraverseInteractive interactive,
            float horizontalInput,
            string source,
            bool remembered,
            Vector3 localPosition,
            bool pushingA,
            bool pushingB,
            Vector3 intentBefore,
            Vector3 speedBefore,
            Vector3 intentAfter,
            Vector3 speedAfter,
            Vector3 currentSpeed,
            bool overridden)
        {
            if (!NetworkTraversalClimbDiagnostics.IsFocused(character.gameObject)) return;

            string signature =
                $"{source}:{Mathf.Sign(horizontalInput)}:{pushingA}:{pushingB}:{remembered}:{overridden}";
            bool changed = NetworkTraversalClimbDiagnostics.HasChanged(
                $"ledge-override:{character.GetInstanceID()}",
                signature);
            NetworkTraversalClimbDiagnostics.Log(
                changed ? "LedgeOverrideChange" : "LedgeOverride",
                $"actor={networkCharacter?.NetworkId ?? 0} role={networkCharacter?.CurrentRole.ToString() ?? "none"} " +
                $"traverse='{interactive.name}' input={horizontalInput:F3} source={source} remembered={remembered} " +
                $"local={NetworkTraversalClimbDiagnostics.Vector(localPosition)} " +
                $"bounds={interactive.PositionA:F3}/{interactive.PositionB:F3} edge={(pushingA ? "A" : pushingB ? "B" : "none")} " +
                $"intentPre={NetworkTraversalClimbDiagnostics.Vector(intentBefore)} intentPost={NetworkTraversalClimbDiagnostics.Vector(intentAfter)} " +
                $"speedPre={NetworkTraversalClimbDiagnostics.Vector(speedBefore)} speedPost={NetworkTraversalClimbDiagnostics.Vector(speedAfter)} " +
                $"currentSpeed={NetworkTraversalClimbDiagnostics.Vector(currentSpeed)} overridden={overridden}",
                character,
                changed ? null : $"ledge-override:{character.GetInstanceID()}");
        }

        private static void LogFocusedVerticalLedgeInput(
            Character character,
            NetworkCharacter networkCharacter,
            TraverseInteractive interactive,
            float verticalInput,
            string source,
            Vector3 localPosition,
            Vector3 intentBefore,
            Vector3 speedBefore,
            Vector3 intentAfter,
            Vector3 speedAfter,
            Vector3 currentSpeed)
        {
            if (!NetworkTraversalClimbDiagnostics.IsFocused(character.gameObject)) return;

            string signature = $"{source}:{Mathf.Sign(verticalInput)}";
            bool changed = NetworkTraversalClimbDiagnostics.HasChanged(
                $"ledge-vertical-override:{character.GetInstanceID()}",
                signature);
            NetworkTraversalClimbDiagnostics.Log(
                changed ? "LedgeVerticalOverrideChange" : "LedgeVerticalOverride",
                $"actor={networkCharacter?.NetworkId ?? 0} role={networkCharacter?.CurrentRole.ToString() ?? "none"} " +
                $"traverse='{interactive.name}' input={verticalInput:F3} source={source} " +
                $"local={NetworkTraversalClimbDiagnostics.Vector(localPosition)} " +
                $"intentPre={NetworkTraversalClimbDiagnostics.Vector(intentBefore)} intentPost={NetworkTraversalClimbDiagnostics.Vector(intentAfter)} " +
                $"speedPre={NetworkTraversalClimbDiagnostics.Vector(speedBefore)} speedPost={NetworkTraversalClimbDiagnostics.Vector(speedAfter)} " +
                $"currentSpeed={NetworkTraversalClimbDiagnostics.Vector(currentSpeed)} overridden=True",
                character,
                changed ? null : $"ledge-vertical-override:{character.GetInstanceID()}");
        }

        private static Vector3 GetTraversalLocalPosition(
            TraversalStance stance,
            TraverseInteractive interactive,
            Character character)
        {
            NetworkCharacter networkCharacter = character != null
                ? character.GetComponent<NetworkCharacter>()
                : null;
            NetworkTraversalController traversalController = ResolveController(character);
            bool isNonOwnerReplica = networkCharacter != null
                ? !networkCharacter.IsOwnerInstance
                : traversalController?.IsRemoteClient == true;

            if (isNonOwnerReplica &&
                interactive?.MotionInteractive != null &&
                character != null)
            {
                // A non-owner's stance-relative pose can lag its current root because owner-pose
                // acceptance, Traversal snapshots, and remote interpolation update on different
                // paths. This applies both to a RemoteClient observing the host and to the host's
                // Server representation of a connected owner. Use the current anchor-corrected
                // pose so the retained direction selects Edge as soon as that representation
                // actually reaches the boundary.
                Vector3 anchorPosition = interactive.MotionInteractive.CharacterPosition(character);
                return interactive.Transform.InverseTransformPoint(anchorPosition);
            }

            if (s_TraversalStanceRelativePositionProperty?.GetValue(stance) is Vector3 relativePosition)
            {
                return relativePosition;
            }

            return interactive != null && character != null
                ? interactive.Transform.InverseTransformPoint(character.transform.position)
                : Vector3.zero;
        }

        private static float GetTraversalEdgePositionTolerance(Character character)
        {
            // CharacterController stops a root approximately one skin width before a contact
            // boundary. The original fixed tolerance was smaller than GC2's default 0.08 skin,
            // so a network owner could be visibly clamped while still missing the authored edge
            // animation test. This tolerance is only used together with explicit outward input.
            float skinWidth = character?.Driver != null
                ? Mathf.Max(0f, character.Driver.SkinWidth)
                : 0f;
            return Mathf.Max(LEDGE_EDGE_POSITION_TOLERANCE, skinWidth + 0.01f);
        }

        private static NetworkTraversalController ResolveController(Character character)
        {
            if (character == null) return null;
            return character.GetComponent<NetworkTraversalController>();
        }

        private static NetworkTraversalController ResolveController(TraversalStance stance)
        {
            return stance != null ? ResolveController(stance.Character) : null;
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3},{value.y:F3},{value.z:F3})";
        }

        private static int AxisSign(float value)
        {
            return value > 0.05f ? 1 : value < -0.05f ? -1 : 0;
        }

        private static void SetStaticField(Type type, string fieldName, object value)
        {
            FieldInfo field = type.GetField(fieldName, STATIC_PUBLIC);
            if (field == null)
            {
                Debug.LogWarning($"[NetworkTraversalPatchHooks] Missing patched field {type.Name}.{fieldName}. GC2 update likely changed signatures.");
                return;
            }

            field.SetValue(null, value);
        }

        private static bool HasPublicStaticField(Type type, string fieldName, Type expectedFieldType)
        {
            FieldInfo field = type.GetField(fieldName, STATIC_PUBLIC);
            return field != null && expectedFieldType.IsAssignableFrom(field.FieldType);
        }

        private static bool HasPublicInstanceMethod(
            Type type,
            string methodName,
            Type returnType,
            params Type[] parameterTypes)
        {
            MethodInfo method = type.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public,
                null,
                parameterTypes,
                null);
            return method != null && method.ReturnType == returnType;
        }

        private static bool HasPublicInstanceProperty(
            Type type,
            string propertyName,
            Type expectedPropertyType)
        {
            PropertyInfo property = type.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public);
            return property != null && property.PropertyType == expectedPropertyType;
        }
    }
}
#endif
