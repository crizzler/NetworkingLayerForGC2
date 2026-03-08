using System;
using System.Text;
using UnityEngine;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Runtime test infrastructure for validating security, sequence tracking, and
    /// relevance-tier logic. Add to a scene via the GC2 Networking Setup Wizard
    /// (Scene step → "Create Test Infrastructure") or manually.
    ///
    /// Runs a diagnostic suite on Start and logs a summary to the console.
    /// Safe to ship disabled; enable in development/QA builds only.
    /// </summary>
    [AddComponentMenu("GC2 Networking/Network Test Runner")]
    public sealed class NetworkTestRunner : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Session profile to validate relevance-tier logic. If null, tier tests are skipped.")]
        public NetworkSessionProfile sessionProfile;

        [Header("Options")]
        [Tooltip("Run the diagnostic suite automatically on Start.")]
        [SerializeField] private bool m_RunOnStart = true;

        [Tooltip("Destroy this GameObject after tests complete (keep scene clean at runtime).")]
        [SerializeField] private bool m_DestroyAfterRun = false;

        private int m_Passed;
        private int m_Failed;
        private readonly StringBuilder m_Log = new StringBuilder(2048);

        // ════════════════════════════════════════════════════════════════════════════════
        // LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════

        private void Start()
        {
            if (m_RunOnStart) RunAllTests();
        }

        // ════════════════════════════════════════════════════════════════════════════════
        // PUBLIC API
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>Run the full diagnostic suite and log results.</summary>
        [ContextMenu("Run All Tests")]
        public void RunAllTests()
        {
            m_Passed = 0;
            m_Failed = 0;
            m_Log.Clear();
            m_Log.AppendLine("[NetworkTestRunner] Starting runtime diagnostics…");

            TestSequenceTracker();
            TestRateLimiter();
            TestViolationTracker();
            TestOwnershipResolver();
            TestRequestContext();
            TestCorrelationPipeline();
            TestPositionStateRoundTrip();
            TestInputStateRoundTrip();
            TestSecurityPassThrough();

            if (sessionProfile != null)
            {
                TestRelevanceTiers();
            }
            else
            {
                m_Log.AppendLine("  [SKIP] Relevance-tier tests (no session profile assigned).");
            }

            m_Log.AppendLine($"\n  Results: {m_Passed} passed, {m_Failed} failed, {m_Passed + m_Failed} total.");

            if (m_Failed > 0)
                Debug.LogWarning(m_Log.ToString());
            else
                Debug.Log(m_Log.ToString());

            if (m_DestroyAfterRun) Destroy(gameObject);
        }

        // ════════════════════════════════════════════════════════════════════════════════
        // TEST SUITES
        // ════════════════════════════════════════════════════════════════════════════════

        private void TestSequenceTracker()
        {
            var tracker = new SequenceTracker();
            Assert("SeqTracker: accepts new", tracker.ValidateSequence(1, 1));
            Assert("SeqTracker: accepts next", tracker.ValidateSequence(1, 2));
            Assert("SeqTracker: rejects replay", !tracker.ValidateSequence(1, 2));
            Assert("SeqTracker: rejects older", !tracker.ValidateSequence(1, 1));
            tracker.ClearClient(1);
            Assert("SeqTracker: reuse after clear", tracker.ValidateSequence(1, 1));
        }

        private void TestRateLimiter()
        {
            var limiter = new RateLimiter(3, 1.0f);
            Assert("RateLimiter: allows first", limiter.TryRequest(1, 0f));
            Assert("RateLimiter: allows second", limiter.TryRequest(1, 0.1f));
            Assert("RateLimiter: allows third", limiter.TryRequest(1, 0.2f));
            Assert("RateLimiter: blocks fourth", !limiter.TryRequest(1, 0.3f));
            Assert("RateLimiter: expires oldest", limiter.TryRequest(1, 1.1f));
        }

        private void TestViolationTracker()
        {
            var tracker = new ViolationTracker(3, 10f);
            Assert("ViolTracker: below threshold",
                !tracker.RecordViolation(1, SecurityViolationType.InvalidRequest, "t", 0f));
            tracker.RecordViolation(1, SecurityViolationType.InvalidRequest, "t", 0.1f);
            Assert("ViolTracker: at threshold",
                tracker.RecordViolation(1, SecurityViolationType.InvalidRequest, "t", 0.2f));
            Assert("ViolTracker: not blocked until explicit",
                !tracker.IsBlocked(1, 0.3f));
            tracker.BlockClient(1, 5f, 0.3f);
            Assert("ViolTracker: blocked after call", tracker.IsBlocked(1, 1f));
            Assert("ViolTracker: block expires", !tracker.IsBlocked(1, 5.4f));
        }

        private void TestOwnershipResolver()
        {
            var resolver = new NetworkOwnershipResolver();

            resolver.RegisterEntityOwner(1001, 7);
            Assert("Ownership: resolve owner",
                resolver.TryResolveOwnerClientId(1001, out uint owner) && owner == 7);
            Assert("Ownership: valid sender",
                resolver.ValidateOwnership(7, 1001, out _));
            Assert("Ownership: invalid sender",
                !resolver.ValidateOwnership(8, 1001, out _));

            resolver.RegisterEntityActor(2001, 1001);
            Assert("Ownership: entity→actor chain",
                resolver.TryResolveOwnerClientIdForEntity(2001, out uint chainOwner) && chainOwner == 7);

            resolver.UnregisterEntity(1001);
            Assert("Ownership: unregister removes",
                !resolver.TryResolveOwnerClientId(1001, out _));

            resolver.Clear();
        }

        private void TestRequestContext()
        {
            var valid = NetworkRequestContext.Create(42, 100);
            Assert("ReqCtx: valid when both non-zero", valid.IsValid);

            var noActor = NetworkRequestContext.Create(0, 100);
            Assert("ReqCtx: invalid when actor=0", !noActor.IsValid);

            var noCorr = NetworkRequestContext.Create(42, 0);
            Assert("ReqCtx: invalid when correlation=0", !noCorr.IsValid);
        }

        private void TestCorrelationPipeline()
        {
            uint actorId = 42;
            ushort counter = 0;

            uint corrA = NetworkCorrelation.Next(actorId, ref counter);
            uint corrB = NetworkCorrelation.Next(actorId, ref counter);

            var ctxA = NetworkRequestContext.Create(actorId, corrA);
            var ctxB = NetworkRequestContext.Create(actorId, corrB);

            Assert("Correlation: ctx A valid", ctxA.IsValid);
            Assert("Correlation: ctx B valid", ctxB.IsValid);

            ushort seqA = NetworkCorrelation.ExtractRequestId(ctxA.CorrelationId);
            ushort seqB = NetworkCorrelation.ExtractRequestId(ctxB.CorrelationId);
            Assert("Correlation: incrementing sequences", seqA != seqB);

            var tracker = new SequenceTracker();
            Assert("Correlation: tracker accepts A", tracker.ValidateSequence(1, seqA));
            Assert("Correlation: tracker accepts B", tracker.ValidateSequence(1, seqB));
            Assert("Correlation: tracker rejects A replay", !tracker.ValidateSequence(1, seqA));
        }

        private void TestPositionStateRoundTrip()
        {
            var state = NetworkPositionState.Create(
                new Vector3(10.5f, -2.0f, 3.25f),
                rotationY: 180f,
                verticalVel: -9.81f,
                lastInput: 42,
                isGrounded: true,
                isJumping: false);

            var pos = state.GetPosition();
            Assert("PosState: X round-trip", Mathf.Abs(pos.x - 10.5f) < 0.02f);
            Assert("PosState: Y round-trip", Mathf.Abs(pos.y - (-2.0f)) < 0.02f);
            Assert("PosState: Z round-trip", Mathf.Abs(pos.z - 3.25f) < 0.02f);
            Assert("PosState: rotation round-trip", Mathf.Abs(state.GetRotationY() - 180f) < 0.1f);
            Assert("PosState: grounded flag", state.IsGrounded);
            Assert("PosState: not jumping", !state.IsJumping);
            Assert("PosState: lastInput", state.lastProcessedInput == 42);
        }

        private void TestInputStateRoundTrip()
        {
            var state = NetworkInputState.Create(
                new Vector2(0.5f, -1f), sequence: 10, deltaTime: 0.016f,
                flags: NetworkInputState.FLAG_JUMP | NetworkInputState.FLAG_SPRINT);

            var dir = state.GetInputDirection();
            Assert("InputState: X round-trip", Mathf.Abs(dir.x - 0.5f) < 0.002f);
            Assert("InputState: Y round-trip", Mathf.Abs(dir.y - (-1f)) < 0.002f);
            Assert("InputState: sequence", state.sequenceNumber == 10);
            Assert("InputState: deltaTime", Mathf.Abs(state.GetDeltaTime() - 0.016f) < 0.002f);
            Assert("InputState: jump flag", state.HasFlag(NetworkInputState.FLAG_JUMP));
            Assert("InputState: sprint flag", state.HasFlag(NetworkInputState.FLAG_SPRINT));
            Assert("InputState: no dash flag", !state.HasFlag(NetworkInputState.FLAG_DASH));
        }

        private void TestSecurityPassThrough()
        {
            // SecurityIntegration only fails closed in authoritative server context.
            // This runtime helper verifies the non-authoritative pass-through path.
            var ctx = NetworkRequestContext.Create(42, NetworkCorrelation.Compose(42, (ushort)1));
            Assert("Security: non-authoritative pass-through",
                SecurityIntegration.ValidateModuleRequest(1, in ctx, "Core", "Move"));
            Assert("Security: ownership pass-through (non-authoritative)",
                SecurityIntegration.ValidateOwnership(1, 1001, "Core"));
        }

        private void TestRelevanceTiers()
        {
            Assert("Tier: near at 0", sessionProfile.GetTier(0f) == NetworkRelevanceTier.Near);
            Assert("Tier: near at boundary", sessionProfile.GetTier(sessionProfile.nearDistance) == NetworkRelevanceTier.Near);
            Assert("Tier: mid above near", sessionProfile.GetTier(sessionProfile.nearDistance + 0.1f) == NetworkRelevanceTier.Mid);
            Assert("Tier: mid at boundary", sessionProfile.GetTier(sessionProfile.midDistance) == NetworkRelevanceTier.Mid);
            Assert("Tier: far above mid", sessionProfile.GetTier(sessionProfile.midDistance + 0.1f) == NetworkRelevanceTier.Far);

            var nearSettings = sessionProfile.GetTierSettings(NetworkRelevanceTier.Near);
            var farSettings = sessionProfile.GetTierSettings(NetworkRelevanceTier.Far);
            Assert("Tier: near rate >= far rate", nearSettings.stateApplyRate >= farSettings.stateApplyRate);
        }

        // ════════════════════════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════════════════════════

        private void Assert(string label, bool condition)
        {
            if (condition)
            {
                m_Passed++;
                m_Log.AppendLine($"  [PASS] {label}");
            }
            else
            {
                m_Failed++;
                m_Log.AppendLine($"  [FAIL] {label}");
            }
        }
    }
}
