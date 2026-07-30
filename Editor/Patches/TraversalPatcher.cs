using System.Collections.Generic;
using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Patcher implementation for GC2 Traversal module.
    /// Adds server-authoritative validation hooks to traversal entry points and stance actions.
    /// </summary>
    public class TraversalPatcher : GC2PatcherBase
    {
        public override string ModuleName => "Traversal";
        public override string PatchVersion => "2.6.0-traversal";
        public override string DisplayName => "Traversal (Game Creator 2)";

        public override string PatchDescription =>
            "This will modify the Game Creator 2 Traversal source code to add\n" +
            "server-authoritative networking hooks.\n\n" +
            "TraverseLink.Run, TraverseInteractive.Enter, MotionInteractive edge\n" +
            "connections, and TraversalStance action APIs will be validated\n" +
            "through network hooks before local execution. TraversalStance also\n" +
            "receives presentation-only snapshot restore/clear entry points.";

        protected override string[] FilesToPatch => new[]
        {
            "Plugins/GameCreator/Packages/Traversal/Runtime/Components/TraverseLink.cs",
            "Plugins/GameCreator/Packages/Traversal/Runtime/Components/TraverseInteractive.cs",
            "Plugins/GameCreator/Packages/Traversal/Runtime/ScriptableObjects/MotionInteractive.cs",
            "Plugins/GameCreator/Packages/Traversal/Runtime/Stance/TraversalStance.cs"
        };

        protected override VersionCompatibilityRequirement[] GetVersionCompatibilityRequirements()
        {
            return new[]
            {
                VersionRequirement("Plugins/GameCreator/Packages/Traversal/Editor/Version.txt", "2.0.*")
            };
        }

        protected override string[] GetRequiredPatchTokens(string relativePath)
        {
            if (relativePath.EndsWith("TraverseLink.cs"))
            {
                return new[] { "NetworkRunValidator", "token.IsCancelled" };
            }

            if (relativePath.EndsWith("TraverseInteractive.cs"))
            {
                return new[] { "NetworkEnterValidator", "token.IsCancelled" };
            }

            if (relativePath.EndsWith("MotionInteractive.cs"))
            {
                return new[]
                {
                    "NetworkEdgeConnectionResolver",
                    "NetworkConnectionSkipTransitionResolver",
                    "NetworkResumeInteractiveSnapshot"
                };
            }

            if (relativePath.EndsWith("TraversalStance.cs"))
            {
                return new[]
                {
                    "NetworkTryCancelValidator",
                    "NetworkForceCancelValidator",
                    "NetworkTryJumpValidator",
                    "NetworkTryActionValidator",
                    "NetworkTryStateEnterValidator",
                    "NetworkTryStateExitValidator",
                    "NetworkRestoreInteractiveSnapshot",
                    "NetworkClearSnapshot",
                    "NetworkSnapshotToken",
                    "NetworkInvalidatePendingEnter",
                    "m_NetworkEnterSequence",
                    "networkEnterSequence != this.m_NetworkEnterSequence",
                    "token != this.m_CurrentToken"
                };
            }

            return base.GetRequiredPatchTokens(relativePath);
        }

        protected override Dictionary<string, int> GetRequiredPatchTokenCounts(string relativePath)
        {
            if (relativePath.EndsWith("TraverseLink.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "NetworkRunValidator.Invoke", 1 },
                    { "token.IsCancelled", 1 }
                };
            }

            if (relativePath.EndsWith("TraverseInteractive.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "NetworkEnterValidator.Invoke", 1 },
                    { "token.IsCancelled", 1 }
                };
            }

            if (relativePath.EndsWith("MotionInteractive.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "NetworkEdgeConnectionResolver.Invoke", 2 },
                    { "NetworkEdgeConnectionResolver != null", 2 },
                    { "Traverse networkConnection = NetworkEdgeConnectionResolver.Invoke", 2 },
                    { "return networkConnection;", 2 },
                    { "NetworkConnectionSkipTransitionResolver.Invoke", 1 },
                    { "NetworkResumeInteractiveSnapshot", 1 }
                };
            }

            if (relativePath.EndsWith("TraversalStance.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "NetworkTryCancelValidator.Invoke", 1 },
                    { "NetworkForceCancelValidator.Invoke", 1 },
                    { "NetworkTryJumpValidator.Invoke", 1 },
                    { "NetworkTryActionValidator.Invoke", 1 },
                    { "NetworkTryStateEnterValidator.Invoke", 1 },
                    { "NetworkTryStateExitValidator.Invoke", 1 },
                    { "NetworkRestoreInteractiveSnapshot", 1 },
                    { "NetworkClearSnapshot", 1 },
                    { "NetworkSnapshotToken", 1 },
                    { "NetworkInvalidatePendingEnter", 1 },
                    { "networkEnterSequence != this.m_NetworkEnterSequence", 1 }
                };
            }

            return base.GetRequiredPatchTokenCounts(relativePath);
        }

        protected override Dictionary<string, int> GetRequiredPatchRegexTokenCounts(string relativePath)
        {
            if (relativePath.EndsWith("MotionInteractive.cs"))
            {
                return new Dictionary<string, int>
                {
                    {
                        @"PushingOutA\s*\(.*?Traverse\s+networkConnection\s*=\s*NetworkEdgeConnectionResolver\.Invoke\s*\(.*?false\s*\)\s*;\s*if\s*\(\s*networkConnection\s*!=\s*null\s*\)\s*\{\s*return\s+networkConnection\s*;\s*\}.*?else\s+if\s*\(\s*traverseInteractive\.ContinueA",
                        1
                    },
                    {
                        @"PushingOutB\s*\(.*?Traverse\s+networkConnection\s*=\s*NetworkEdgeConnectionResolver\.Invoke\s*\(.*?true\s*\)\s*;\s*if\s*\(\s*networkConnection\s*!=\s*null\s*\)\s*\{\s*return\s+networkConnection\s*;\s*\}.*?else\s+if\s*\(\s*traverseInteractive\.ContinueB",
                        1
                    }
                };
            }

            return base.GetRequiredPatchRegexTokenCounts(relativePath);
        }

        protected override bool PatchFile(string relativePath)
        {
            string content = ReadFile(relativePath);

            ExistingPatchState existingPatchState = PrepareContentForPatch(relativePath, ref content);
            if (existingPatchState == ExistingPatchState.SkipAlreadyPatched) return true;
            if (existingPatchState == ExistingPatchState.Failed) return false;

            if (relativePath.EndsWith("TraverseLink.cs"))
            {
                return PatchTraverseLink(relativePath, content);
            }

            if (relativePath.EndsWith("TraverseInteractive.cs"))
            {
                return PatchTraverseInteractive(relativePath, content);
            }

            if (relativePath.EndsWith("MotionInteractive.cs"))
            {
                return PatchMotionInteractive(relativePath, content);
            }

            if (relativePath.EndsWith("TraversalStance.cs"))
            {
                return PatchTraversalStance(relativePath, content);
            }

            return false;
        }

        private bool PatchTraverseLink(string relativePath, string content)
        {
            if (!TryInsertAfterTypeOpeningBrace(
                    ref content,
                    "TraverseLink",
                    "NetworkRunValidator",
                    @"
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking

        public static System.Func<TraverseLink, Character, bool> NetworkRunValidator;

        public static bool IsNetworkingActive => NetworkRunValidator != null;

        // [GC2_NETWORK_PATCH_END]
",
                    out string failureReason))
            {
                return LogPatchFailure("TraverseLink class header", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "Run",
                    "NetworkRunValidator.Invoke",
                    @"

            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkRunValidator != null && !NetworkRunValidator.Invoke(this, character))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"if\s*\(\s*character\s*==\s*null\s*\)\s*return\s*;"))
            {
                return LogPatchFailure("TraverseLink.Run", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "Run",
                    "token.IsCancelled",
                    @"

            // [GC2_NETWORK_PATCH] A newer traversal may supersede this start while OnTraverseEnter yields
            if (token == null || token.IsCancelled)
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"TraversalToken\s+token\s*=\s*await\s+traversal\.OnTraverseEnter\s*\(\s*this\s*\)\s*;"))
            {
                return LogPatchFailure("TraverseLink.Run stale-start guard", failureReason);
            }

            if (!EnsurePatchMarkerBeforeNamespace(ref content, "GameCreator.Runtime.Traversal"))
            {
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool PatchTraverseInteractive(string relativePath, string content)
        {
            if (!TryInsertAfterTypeOpeningBrace(
                    ref content,
                    "TraverseInteractive",
                    "NetworkEnterValidator",
                    @"
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking

        public static System.Func<TraverseInteractive, Character, InteractiveTransitionData, bool> NetworkEnterValidator;

        public static bool IsNetworkingActive => NetworkEnterValidator != null;

        // [GC2_NETWORK_PATCH_END]
",
                    out string failureReason))
            {
                return LogPatchFailure("TraverseInteractive class header", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "Enter",
                    "NetworkEnterValidator.Invoke",
                    @"

            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkEnterValidator != null && !NetworkEnterValidator.Invoke(this, character, transition))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"if\s*\(\s*character\s*==\s*null\s*\)\s*return\s*;"))
            {
                return LogPatchFailure("TraverseInteractive.Enter", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "Enter",
                    "token.IsCancelled",
                    @"

            // [GC2_NETWORK_PATCH] A newer traversal may supersede this start while OnTraverseEnter yields
            if (token == null || token.IsCancelled)
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"TraversalToken\s+token\s*=\s*await\s+traversal\.OnTraverseEnter\s*\(\s*this\s*\)\s*;"))
            {
                return LogPatchFailure("TraverseInteractive.Enter stale-start guard", failureReason);
            }

            if (!EnsurePatchMarkerBeforeNamespace(ref content, "GameCreator.Runtime.Traversal"))
            {
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool PatchMotionInteractive(string relativePath, string content)
        {
            if (!TryInsertAfterTypeOpeningBrace(
                    ref content,
                    "MotionInteractive",
                    "NetworkEdgeConnectionResolver",
                    @"
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking

        public static System.Func<MotionInteractive, TraverseInteractive, Character, Vector3, Vector3, bool, Traverse> NetworkEdgeConnectionResolver;
        public static System.Func<Traverse, Traverse, Character, bool> NetworkConnectionSkipTransitionResolver;

        public static bool IsNetworkingActive =>
            NetworkEdgeConnectionResolver != null ||
            NetworkConnectionSkipTransitionResolver != null;

        // Presentation-safe owner resume for a persistent interactive snapshot. This deliberately
        // skips m_OnStart/m_OnFinish and Traverse OnEnter/OnExit gameplay instruction lists.
        public async Task<bool> NetworkResumeInteractiveSnapshot(
            TraverseInteractive traverse,
            Character character,
            TraversalToken token)
        {
            if (traverse == null || character == null || token == null || token.IsCancelled)
            {
                return false;
            }

            Args args = new Args(traverse.gameObject, character.gameObject);
            character.Motion.MoveToDirection(Vector3.zero, Space.World, 0);
            character.Motion.MoveToDirection(Vector3.zero, Space.World, MOVE_DIRECTION_KEY);
            character.Driver.SetGravityInfluence(GRAVITY_KEY, this.Gravity);
            character.Driver.ResetVerticalVelocity();
            character.Driver.UpdateKinematics = false;

            Traverse nextTraverse = null;
            try
            {
                nextTraverse = await this.OnUpdate(
                    character,
                    InteractiveTransitionData.None,
                    token,
                    args);
            }
            finally
            {
                if (character != null)
                {
                    character.Motion.StopToDirection(MOVE_DIRECTION_KEY);
                    character.Driver.RemoveGravityInfluence(GRAVITY_KEY);
                    character.Driver.UpdateKinematics = true;
                }
            }

            return nextTraverse == null && !token.IsCancelled;
        }

        // [GC2_NETWORK_PATCH_END]
",
                    out string failureReason))
            {
                return LogPatchFailure("MotionInteractive class header", failureReason);
            }

            if (!TryReplaceRegexInMethod(
                    ref content,
                    "Enter",
                    "NetworkConnectionSkipTransitionResolver.Invoke",
                    @"[ \t]*_\s*=\s*Traverse\.ChangeTo\s*\(\s*traverse\s*,\s*nextTraverse\s*,\s*character\s*,\s*true\s*\)\s*;",
                    @"                // [GC2_NETWORK_PATCH] Let networking decide whether interactive-to-interactive connections should run transition clips
                bool skipTransition = NetworkConnectionSkipTransitionResolver == null ||
                    NetworkConnectionSkipTransitionResolver.Invoke(traverse, nextTraverse, character);
                _ = Traverse.ChangeTo(traverse, nextTraverse, character, skipTransition);
                // [GC2_NETWORK_PATCH_END]",
                    out failureReason))
            {
                return LogPatchFailure("MotionInteractive.Enter ChangeTo transition routing", failureReason);
            }

            if (!TryReplaceRegexInMethod(
                    ref content,
                    "OnUpdate",
                    null,
                    @"(?m)^[ \t]*if\s*\(\s*traverseInteractive\.ContinueA\s*!=\s*null\s*\)\s*\{.*?^[ \t]*return\s+traverseInteractive\.ContinueA\s*;\s*^[ \t]*\}",
                    @"                    // [GC2_NETWORK_PATCH] Route ContinueA and configured edge connections before
                    // GC2 can auto-transition. A null network result keeps a network character on
                    // the current traversal while its authoritative request is in flight.
                    if (NetworkEdgeConnectionResolver != null)
                    {
                        Traverse networkConnection = NetworkEdgeConnectionResolver.Invoke(
                            this,
                            traverseInteractive,
                            character,
                            currentLocalPosition,
                            swizzleLocalInput,
                            false);
                        if (networkConnection != null)
                        {
                            return networkConnection;
                        }
                    }
                    else if (traverseInteractive.ContinueA != null)
                    {
                        return traverseInteractive.ContinueA;
                    }
                    // [GC2_NETWORK_PATCH_END]",
                    out failureReason))
            {
                return LogPatchFailure("MotionInteractive edge A connection resolver", failureReason);
            }

            if (!TryReplaceRegexInMethod(
                    ref content,
                    "OnUpdate",
                    null,
                    @"(?m)^[ \t]*if\s*\(\s*traverseInteractive\.ContinueB\s*!=\s*null\s*\)\s*\{.*?^[ \t]*return\s+traverseInteractive\.ContinueB\s*;\s*^[ \t]*\}",
                    @"                    // [GC2_NETWORK_PATCH] Route ContinueB and configured edge connections before
                    // GC2 can auto-transition. See the edge A branch above.
                    if (NetworkEdgeConnectionResolver != null)
                    {
                        Traverse networkConnection = NetworkEdgeConnectionResolver.Invoke(
                            this,
                            traverseInteractive,
                            character,
                            currentLocalPosition,
                            swizzleLocalInput,
                            true);
                        if (networkConnection != null)
                        {
                            return networkConnection;
                        }
                    }
                    else if (traverseInteractive.ContinueB != null)
                    {
                        return traverseInteractive.ContinueB;
                    }
                    // [GC2_NETWORK_PATCH_END]",
                    out failureReason))
            {
                return LogPatchFailure("MotionInteractive edge B connection resolver", failureReason);
            }

            if (!EnsurePatchMarkerBeforeNamespace(ref content, "GameCreator.Runtime.Traversal"))
            {
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool PatchTraversalStance(string relativePath, string content)
        {
            if (!TryInsertAfterTypeOpeningBrace(
                    ref content,
                    "TraversalStance",
                    "NetworkTryCancelValidator",
                    @"
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking

        public static System.Func<TraversalStance, Args, bool> NetworkTryCancelValidator;
        public static System.Func<TraversalStance, bool> NetworkForceCancelValidator;
        public static System.Func<TraversalStance, bool> NetworkTryJumpValidator;
        public static System.Func<TraversalStance, IdString, bool> NetworkTryActionValidator;
        public static System.Func<TraversalStance, IdString, bool> NetworkTryStateEnterValidator;
        public static System.Func<TraversalStance, bool> NetworkTryStateExitValidator;

        [NonSerialized] private uint m_NetworkEnterSequence;
        public TraversalToken NetworkSnapshotToken => this.m_CurrentToken;

        public void NetworkInvalidatePendingEnter()
        {
            this.m_NetworkEnterSequence = unchecked(this.m_NetworkEnterSequence + 1u);
            if (this.m_NetworkEnterSequence == 0) this.m_NetworkEnterSequence = 1;
        }

        public static bool IsNetworkingActive =>
            NetworkTryCancelValidator != null ||
            NetworkForceCancelValidator != null ||
            NetworkTryJumpValidator != null ||
            NetworkTryActionValidator != null ||
            NetworkTryStateEnterValidator != null ||
            NetworkTryStateExitValidator != null;

        // Presentation-only late-join reconstruction. These methods deliberately do not invoke
        // Traverse.OnEnter/OnExit or MotionInteractive m_OnStart/m_OnFinish instruction lists.
        public bool NetworkRestoreInteractiveSnapshot(
            TraverseInteractive traverse,
            Vector3 relativePosition)
        {
            if (traverse == null || this.Character == null) return false;

            if (this.m_CurrentToken != null)
            {
                this.m_CurrentToken.IsCancelled = true;
            }

            this.m_NetworkEnterSequence = unchecked(this.m_NetworkEnterSequence + 1u);
            if (this.m_NetworkEnterSequence == 0) this.m_NetworkEnterSequence = 1;

            this.Traverse = traverse;
            this.RelativePosition = relativePosition;
            this.InInteractiveTransition = false;
            this.AllowMovement = false;
            this.m_CurrentToken = new TraversalToken();
            this.m_CurrentStateId = IdString.EMPTY;
            this.EventMotionEnter?.Invoke();
            return true;
        }

        public bool NetworkClearSnapshot()
        {
            if (this.Traverse == null && this.m_CurrentToken == null) return false;

            if (this.m_CurrentToken != null)
            {
                this.m_CurrentToken.IsCancelled = true;
            }

            this.m_NetworkEnterSequence = unchecked(this.m_NetworkEnterSequence + 1u);
            if (this.m_NetworkEnterSequence == 0) this.m_NetworkEnterSequence = 1;

            this.EventMotionExit?.Invoke();
            this.m_CurrentStateId = IdString.EMPTY;
            this.Traverse = null;
            this.m_CurrentToken = null;
            this.InInteractiveTransition = false;
            this.AllowMovement = true;
            return true;
        }

        // [GC2_NETWORK_PATCH_END]
",
                    out string failureReason))
            {
                return LogPatchFailure("TraversalStance class header", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "TryCancel",
                    "NetworkTryCancelValidator.Invoke",
                    @"

            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkTryCancelValidator != null && !NetworkTryCancelValidator.Invoke(this, args))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"if\s*\(\s*this\.Traverse\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.m_CurrentToken\s*==\s*null\s*\)\s*return\s*;"))
            {
                return LogPatchFailure("TraversalStance.TryCancel", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "ForceCancel",
                    "NetworkForceCancelValidator.Invoke",
                    @"

            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkForceCancelValidator != null && !NetworkForceCancelValidator.Invoke(this))
            {
                return false;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"if\s*\(\s*this\.Traverse\s*==\s*null\s*\)\s*return\s+false\s*;",
                    @"if\s*\(\s*this\.m_CurrentToken\s*==\s*null\s*\)\s*return\s+false\s*;",
                    @"if\s*\(\s*this\.m_CurrentToken\.IsCancelled\s*\)\s*return\s+false\s*;"))
            {
                return LogPatchFailure("TraversalStance.ForceCancel", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "TryJump",
                    "NetworkTryJumpValidator.Invoke",
                    @"

            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkTryJumpValidator != null && !NetworkTryJumpValidator.Invoke(this))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"if\s*\(\s*this\.Traverse\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.m_CurrentToken\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.InInteractiveTransition\s*\)\s*return\s*;"))
            {
                return LogPatchFailure("TraversalStance.TryJump", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "TryAction",
                    "NetworkTryActionValidator.Invoke",
                    @"

            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkTryActionValidator != null && !NetworkTryActionValidator.Invoke(this, actionId))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"if\s*\(\s*this\.Traverse\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.m_CurrentToken\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.InInteractiveTransition\s*\)\s*return\s*;"))
            {
                return LogPatchFailure("TraversalStance.TryAction", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "TryStateEnter",
                    "NetworkTryStateEnterValidator.Invoke",
                    @"

            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkTryStateEnterValidator != null && !NetworkTryStateEnterValidator.Invoke(this, stateId))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"if\s*\(\s*this\.Traverse\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.m_CurrentToken\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.InInteractiveTransition\s*\)\s*return\s*;"))
            {
                return LogPatchFailure("TraversalStance.TryStateEnter", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "TryStateExit",
                    "NetworkTryStateExitValidator.Invoke",
                    @"

            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkTryStateExitValidator != null && !NetworkTryStateExitValidator.Invoke(this))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"if\s*\(\s*this\.Traverse\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.m_CurrentToken\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.m_CurrentStateId\s*==\s*IdString\.EMPTY\s*\)\s*return\s*;"))
            {
                return LogPatchFailure("TraversalStance.TryStateExit", failureReason);
            }

            if (!TryInsertAtMethodStart(
                    ref content,
                    "OnTraverseEnter",
                    "uint networkEnterSequence",
                    @"
            // [GC2_NETWORK_PATCH] Invalidate a yielded start when a newer traversal starts first
            uint networkEnterSequence = unchecked(++this.m_NetworkEnterSequence);
            if (networkEnterSequence == 0)
            {
                networkEnterSequence = unchecked(++this.m_NetworkEnterSequence);
            }
            // [GC2_NETWORK_PATCH_END]
",
                    out failureReason))
            {
                return LogPatchFailure("TraversalStance.OnTraverseEnter generation", failureReason);
            }

            if (!TryInsertAfterRegexInMethod(
                    ref content,
                    "OnTraverseEnter",
                    "networkEnterSequence != this.m_NetworkEnterSequence",
                    @"(?s)if\s*\(\s*this\.ForceCancel\s*\(\s*\)\s*\)\s*\{.*?await\s+Task\.Yield\s*\(\s*\)\s*;.*?\}",
                    @"

            // [GC2_NETWORK_PATCH] Do not let an older yielded start overwrite the newer traversal
            if (networkEnterSequence != this.m_NetworkEnterSequence)
            {
                return new TraversalToken { IsCancelled = true };
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason))
            {
                return LogPatchFailure("TraversalStance.OnTraverseEnter stale-start guard", failureReason);
            }

            if (!TryInsertAtMethodStart(
                    ref content,
                    "OnTraverseExit",
                    "token != this.m_CurrentToken",
                    @"
            // [GC2_NETWORK_PATCH] Ignore stale exits from the previous interactive traverse during ChangeTo transitions
            if (traverse != this.Traverse || token != this.m_CurrentToken)
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]
",
                    out failureReason))
            {
                return LogPatchFailure("TraversalStance.OnTraverseExit stale-exit guard", failureReason);
            }

            if (!EnsurePatchMarkerBeforeNamespace(ref content, "GameCreator.Runtime.Traversal"))
            {
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private static bool LogPatchFailure(string context, string failureReason)
        {
            Debug.LogError($"[GC2 Networking] Could not patch {context}: {failureReason}");
            return false;
        }

    }
}
