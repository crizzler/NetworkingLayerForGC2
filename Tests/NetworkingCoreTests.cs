using NUnit.Framework;
using System;
using System.Reflection;
using System.Threading;
using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking.Tests
{
    /// <summary>
    /// Unit tests for networking logic.
    /// Run via Unity Test Runner (EditMode).
    /// </summary>
    public class NetworkingCoreTests
    {
        [SetUp]
        public void SetUp()
        {
            SecurityIntegration.ClearModuleServerContexts();
            NetworkCorrelation.ResetComposeState();
        }

        [TearDown]
        public void TearDown()
        {
            SecurityIntegration.ClearModuleServerContexts();
            NetworkCorrelation.ResetComposeState();
        }

        private static UnityEngine.GameObject EnsureSecurityManagerForServerTests()
        {
            if (NetworkSecurityManager.Instance != null)
            {
                SecurityIntegration.EnsureSecurityManagerInitialized(true, () => 1f);
                return null;
            }

            var go = new UnityEngine.GameObject("SecurityManager_Test_Runtime");
            go.AddComponent<NetworkSecurityManager>();
            SecurityIntegration.EnsureSecurityManagerInitialized(true, () => 1f);
            return go;
        }

        private static void DestroySecurityManagerIfCreated(UnityEngine.GameObject securityManagerGo)
        {
            if (securityManagerGo != null)
            {
                UnityEngine.Object.DestroyImmediate(securityManagerGo);
            }
        }

        private sealed class OwnershipBootstrapTestBridge : NetworkTransportBridge
        {
            public bool VerifyCalled { get; private set; }
            public bool VerifyResult { get; set; } = true;
            public uint VerifiedOwnerClientId { get; set; } = 7;

            public override bool IsServer => true;
            public override bool IsClient => false;
            public override bool IsHost => false;
            public override float ServerTime => 1f;

            public override void SendToServer(uint characterNetworkId, NetworkInputState[] inputs)
            {
            }

            public override void SendToOwner(uint ownerClientId, uint characterNetworkId, NetworkPositionState state, float serverTime)
            {
            }

            public override void Broadcast(
                uint characterNetworkId,
                NetworkPositionState state,
                float serverTime,
                uint excludeClientId = uint.MaxValue,
                NetworkRecipientFilter relevanceFilter = null)
            {
            }

            public override bool TryVerifyActorOwnership(uint senderClientId, uint actorNetworkId, out uint ownerClientId)
            {
                VerifyCalled = true;
                ownerClientId = VerifiedOwnerClientId;
                if (!VerifyResult || senderClientId != VerifiedOwnerClientId || actorNetworkId == 0)
                {
                    return false;
                }

                SetCharacterOwner(actorNetworkId, ownerClientId);
                return true;
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // STABLE HASH UTILITY
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Test]
        public void StableHash_NullOrEmpty_ReturnsZero()
        {
            Assert.AreEqual(0, StableHashUtility.GetStableHash((string)null));
            Assert.AreEqual(0, StableHashUtility.GetStableHash(""));
        }
        
        [Test]
        public void StableHash_Deterministic_SameInputSameOutput()
        {
            int hash1 = StableHashUtility.GetStableHash("TestAbility");
            int hash2 = StableHashUtility.GetStableHash("TestAbility");
            Assert.AreEqual(hash1, hash2);
        }
        
        [Test]
        public void StableHash_DifferentInputs_DifferentOutputs()
        {
            int hashA = StableHashUtility.GetStableHash("AbilityA");
            int hashB = StableHashUtility.GetStableHash("AbilityB");
            Assert.AreNotEqual(hashA, hashB);
        }
        
        [Test]
        public void StableHash_SingleChar_NonZero()
        {
            Assert.AreNotEqual(0, StableHashUtility.GetStableHash("a"));
            Assert.AreNotEqual(0, StableHashUtility.GetStableHash("Z"));
        }
        
        [Test]
        public void StableHash_KnownFnv1a_MatchesExpected()
        {
            // FNV-1a 32-bit: hash("") = 2166136261 (offset basis), but we return 0 for empty
            // For "a": unchecked((int)(((2166136261 ^ 97) * 16777619)))
            // 2166136261 ^ 97 = 2166136196
            // 2166136196 * 16777619 = (computed) → e40c292c (hex) = -469777620 (signed int)
            int hashA = StableHashUtility.GetStableHash("a");
            Assert.AreEqual(unchecked((int)0xe40c292cu), hashA);
        }
        
        [Test]
        public void StableHash_OrderMatters()
        {
            int hashAB = StableHashUtility.GetStableHash("AB");
            int hashBA = StableHashUtility.GetStableHash("BA");
            Assert.AreNotEqual(hashAB, hashBA);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // NETWORK CHARACTER STATE — BOOL PACKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Test]
        public void CharacterState_PackUnpack_AllFalse()
        {
            var state = new NetworkCharacterState { isDead = false, isPlayer = false };
            byte packed = state.ToPacked();
            Assert.AreEqual(0x00, packed);
            
            var unpacked = NetworkCharacterState.FromPacked(packed);
            Assert.IsFalse(unpacked.isDead);
            Assert.IsFalse(unpacked.isPlayer);
        }
        
        [Test]
        public void CharacterState_PackUnpack_DeadOnly()
        {
            var state = new NetworkCharacterState { isDead = true, isPlayer = false };
            byte packed = state.ToPacked();
            Assert.AreEqual(0x01, packed);
            
            var unpacked = NetworkCharacterState.FromPacked(packed);
            Assert.IsTrue(unpacked.isDead);
            Assert.IsFalse(unpacked.isPlayer);
        }
        
        [Test]
        public void CharacterState_PackUnpack_PlayerOnly()
        {
            var state = new NetworkCharacterState { isDead = false, isPlayer = true };
            byte packed = state.ToPacked();
            Assert.AreEqual(0x02, packed);
            
            var unpacked = NetworkCharacterState.FromPacked(packed);
            Assert.IsFalse(unpacked.isDead);
            Assert.IsTrue(unpacked.isPlayer);
        }
        
        [Test]
        public void CharacterState_PackUnpack_AllTrue()
        {
            var state = new NetworkCharacterState { isDead = true, isPlayer = true };
            byte packed = state.ToPacked();
            Assert.AreEqual(0x03, packed);
            
            var unpacked = NetworkCharacterState.FromPacked(packed);
            Assert.IsTrue(unpacked.isDead);
            Assert.IsTrue(unpacked.isPlayer);
        }
        
        [Test]
        public void CharacterState_BitIndependence()
        {
            // Toggling one bit shouldn't affect the other
            var dead = NetworkCharacterState.FromPacked(0x01);
            var player = NetworkCharacterState.FromPacked(0x02);
            
            Assert.IsTrue(dead.isDead);
            Assert.IsFalse(dead.isPlayer);
            Assert.IsFalse(player.isDead);
            Assert.IsTrue(player.isPlayer);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // RATE LIMITER
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Test]
        public void RateLimiter_AllowsUpToMax()
        {
            var limiter = new RateLimiter(3, 1.0f);
            
            Assert.IsTrue(limiter.TryRequest(1, 0f));   // 1st
            Assert.IsTrue(limiter.TryRequest(1, 0.1f)); // 2nd
            Assert.IsTrue(limiter.TryRequest(1, 0.2f)); // 3rd
        }
        
        [Test]
        public void RateLimiter_BlocksAtMax()
        {
            var limiter = new RateLimiter(3, 1.0f);
            
            limiter.TryRequest(1, 0f);
            limiter.TryRequest(1, 0.1f);
            limiter.TryRequest(1, 0.2f);
            
            Assert.IsFalse(limiter.TryRequest(1, 0.3f)); // 4th blocked
        }
        
        [Test]
        public void RateLimiter_SlidingWindow_ExpiresOldRequests()
        {
            var limiter = new RateLimiter(2, 1.0f);
            
            limiter.TryRequest(1, 0f);    // t=0
            limiter.TryRequest(1, 0.5f);  // t=0.5
            
            // At t=0.3, both are still in window → blocked
            Assert.IsFalse(limiter.TryRequest(1, 0.7f));
            
            // At t=1.1, the t=0 request has expired → allowed
            Assert.IsTrue(limiter.TryRequest(1, 1.1f));
        }
        
        [Test]
        public void RateLimiter_MultipleClients_Independent()
        {
            var limiter = new RateLimiter(1, 1.0f);
            
            Assert.IsTrue(limiter.TryRequest(1, 0f));   // Client 1 uses its slot
            Assert.IsFalse(limiter.TryRequest(1, 0.1f)); // Client 1 blocked
            Assert.IsTrue(limiter.TryRequest(2, 0.1f));  // Client 2 still has its own slot
        }
        
        [Test]
        public void RateLimiter_GetRequestCount_Accurate()
        {
            var limiter = new RateLimiter(5, 1.0f);
            
            Assert.AreEqual(0, limiter.GetRequestCount(1, 0f));
            
            limiter.TryRequest(1, 0f);
            limiter.TryRequest(1, 0.3f);
            limiter.TryRequest(1, 0.6f);
            
            Assert.AreEqual(3, limiter.GetRequestCount(1, 0.9f));
            
            // After window expires for the first request
            Assert.AreEqual(2, limiter.GetRequestCount(1, 1.1f));
        }
        
        [Test]
        public void RateLimiter_ClearClient_ResetsState()
        {
            var limiter = new RateLimiter(1, 1.0f);
            
            limiter.TryRequest(1, 0f);
            Assert.IsFalse(limiter.TryRequest(1, 0.1f));
            
            limiter.ClearClient(1);
            Assert.IsTrue(limiter.TryRequest(1, 0.2f));
        }
        
        [Test]
        public void RateLimiter_Clear_ResetsAllClients()
        {
            var limiter = new RateLimiter(1, 1.0f);
            
            limiter.TryRequest(1, 0f);
            limiter.TryRequest(2, 0f);
            
            limiter.Clear();
            
            Assert.IsTrue(limiter.TryRequest(1, 0.1f));
            Assert.IsTrue(limiter.TryRequest(2, 0.1f));
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // VIOLATION TRACKER
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Test]
        public void ViolationTracker_BelowThreshold_ReturnsFalse()
        {
            var tracker = new ViolationTracker(3, 10f);
            
            bool exceeded = tracker.RecordViolation(1, SecurityViolationType.InvalidRequest, "test", 0f);
            Assert.IsFalse(exceeded);
            
            exceeded = tracker.RecordViolation(1, SecurityViolationType.InvalidRequest, "test", 0.1f);
            Assert.IsFalse(exceeded);
        }
        
        [Test]
        public void ViolationTracker_AtThreshold_ReturnsTrue()
        {
            var tracker = new ViolationTracker(3, 10f);
            
            tracker.RecordViolation(1, SecurityViolationType.InvalidRequest, "1", 0f);
            tracker.RecordViolation(1, SecurityViolationType.InvalidRequest, "2", 0.1f);
            bool exceeded = tracker.RecordViolation(1, SecurityViolationType.InvalidRequest, "3", 0.2f);
            
            Assert.IsTrue(exceeded);
        }
        
        [Test]
        public void ViolationTracker_OldViolations_Expire()
        {
            var tracker = new ViolationTracker(3, 1.0f);
            
            tracker.RecordViolation(1, SecurityViolationType.InvalidRequest, "1", 0f);
            tracker.RecordViolation(1, SecurityViolationType.InvalidRequest, "2", 0.5f);
            
            // At t=1.5, the first violation (t=0) has expired
            Assert.AreEqual(1, tracker.GetViolationCount(1, 1.5f));
        }
        
        [Test]
        public void ViolationTracker_GetViolationCount_RespectsWindow()
        {
            var tracker = new ViolationTracker(10, 2.0f);
            
            tracker.RecordViolation(1, SecurityViolationType.RateLimitExceeded, "a", 0f);
            tracker.RecordViolation(1, SecurityViolationType.RateLimitExceeded, "b", 1f);
            tracker.RecordViolation(1, SecurityViolationType.RateLimitExceeded, "c", 2f);
            
            Assert.AreEqual(3, tracker.GetViolationCount(1, 2f));
            Assert.AreEqual(2, tracker.GetViolationCount(1, 2.5f));  // t=0 expired
            Assert.AreEqual(1, tracker.GetViolationCount(1, 3.5f));  // t=0 and t=1 expired
        }
        
        [Test]
        public void ViolationTracker_BlockClient_IsBlocked()
        {
            var tracker = new ViolationTracker(3, 10f);
            
            Assert.IsFalse(tracker.IsBlocked(1, 0f));
            
            tracker.BlockClient(1, 5f, 0f);
            Assert.IsTrue(tracker.IsBlocked(1, 0f));
            Assert.IsTrue(tracker.IsBlocked(1, 4.9f));
        }
        
        [Test]
        public void ViolationTracker_BlockExpires()
        {
            var tracker = new ViolationTracker(3, 10f);
            
            tracker.BlockClient(1, 5f, 0f);
            
            Assert.IsTrue(tracker.IsBlocked(1, 4.9f));
            Assert.IsFalse(tracker.IsBlocked(1, 5.0f)); // Expires at exactly 5.0
        }
        
        [Test]
        public void ViolationTracker_ClearClient_RemovesViolationsAndBlock()
        {
            var tracker = new ViolationTracker(3, 10f);
            
            tracker.RecordViolation(1, SecurityViolationType.InvalidRequest, "test", 0f);
            tracker.BlockClient(1, 60f, 0f);
            
            Assert.IsTrue(tracker.IsBlocked(1, 1f));
            Assert.AreEqual(1, tracker.GetViolationCount(1, 1f));
            
            tracker.ClearClient(1);
            
            Assert.IsFalse(tracker.IsBlocked(1, 1f));
            Assert.AreEqual(0, tracker.GetViolationCount(1, 1f));
        }
        
        [Test]
        public void ViolationTracker_Clear_ResetsEverything()
        {
            var tracker = new ViolationTracker(3, 10f);
            
            tracker.RecordViolation(1, SecurityViolationType.InvalidRequest, "test", 0f);
            tracker.RecordViolation(2, SecurityViolationType.InvalidRequest, "test", 0f);
            tracker.BlockClient(1, 60f, 0f);
            
            tracker.Clear();
            
            Assert.IsFalse(tracker.IsBlocked(1, 1f));
            Assert.AreEqual(0, tracker.GetViolationCount(1, 1f));
            Assert.AreEqual(0, tracker.GetViolationCount(2, 1f));
        }
        
        [Test]
        public void ViolationTracker_UnknownClient_ZeroCount()
        {
            var tracker = new ViolationTracker(3, 10f);
            Assert.AreEqual(0, tracker.GetViolationCount(999, 0f));
            Assert.IsFalse(tracker.IsBlocked(999, 0f));
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // MOTION CONFIG — GETTER DECOMPRESSION & FLAGS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Test]
        public void MotionConfig_GetLinearSpeed_Decompresses()
        {
            var config = new NetworkMotionConfig { linearSpeed = 525 }; // 525 / 100 = 5.25
            Assert.AreEqual(5.25f, config.GetLinearSpeed(), 0.001f);
        }
        
        [Test]
        public void MotionConfig_GetAngularSpeed_Decompresses()
        {
            var config = new NetworkMotionConfig { angularSpeed = 3600 }; // 3600 / 10 = 360.0
            Assert.AreEqual(360f, config.GetAngularSpeed(), 0.01f);
        }
        
        [Test]
        public void MotionConfig_GetGravity_SignedDecompression()
        {
            var config = new NetworkMotionConfig
            {
                gravityUp = -981,    // -981 / 100 = -9.81
                gravityDown = 2000   // 2000 / 100 = 20.0
            };
            Assert.AreEqual(-9.81f, config.GetGravityUp(), 0.001f);
            Assert.AreEqual(20f, config.GetGravityDown(), 0.001f);
        }
        
        [Test]
        public void MotionConfig_GetJumpCooldown_Decompresses()
        {
            var config = new NetworkMotionConfig { jumpCooldownMs = 150 }; // 150 / 100 = 1.5
            Assert.AreEqual(1.5f, config.GetJumpCooldown(), 0.001f);
        }
        
        [Test]
        public void MotionConfig_Flags_AllCombinations()
        {
            // No flags
            var none = new NetworkMotionConfig { flags = 0 };
            Assert.IsFalse(none.CanJump);
            Assert.IsFalse(none.DashInAir);
            Assert.IsFalse(none.UseAcceleration);
            
            // All flags
            var all = new NetworkMotionConfig { flags = 0x07 };
            Assert.IsTrue(all.CanJump);
            Assert.IsTrue(all.DashInAir);
            Assert.IsTrue(all.UseAcceleration);
            
            // Individual flags
            Assert.IsTrue(new NetworkMotionConfig { flags = 1 }.CanJump);
            Assert.IsFalse(new NetworkMotionConfig { flags = 1 }.DashInAir);
            
            Assert.IsFalse(new NetworkMotionConfig { flags = 2 }.CanJump);
            Assert.IsTrue(new NetworkMotionConfig { flags = 2 }.DashInAir);
            
            Assert.IsFalse(new NetworkMotionConfig { flags = 4 }.DashInAir);
            Assert.IsTrue(new NetworkMotionConfig { flags = 4 }.UseAcceleration);
        }
        
        [Test]
        public void MotionConfig_Equals_SameValues()
        {
            var a = new NetworkMotionConfig { linearSpeed = 100, angularSpeed = 50, flags = 3 };
            var b = new NetworkMotionConfig { linearSpeed = 100, angularSpeed = 50, flags = 3 };
            Assert.IsTrue(a.Equals(b));
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }
        
        [Test]
        public void MotionConfig_Equals_DifferentValues()
        {
            var a = new NetworkMotionConfig { linearSpeed = 100 };
            var b = new NetworkMotionConfig { linearSpeed = 200 };
            Assert.IsFalse(a.Equals(b));
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // MOTION COMMAND — GETTER DECOMPRESSION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Test]
        public void MotionCommand_GetPosition_FixedPointDecompression()
        {
            var cmd = new NetworkMotionCommand
            {
                dataX = 1050,   // 1050 / 100 = 10.5
                dataY = -200,   // -200 / 100 = -2.0
                dataZ = 300     // 300 / 100 = 3.0
            };
            
            var pos = cmd.GetPosition();
            Assert.AreEqual(10.5f, pos.x, 0.001f);
            Assert.AreEqual(-2f, pos.y, 0.001f);
            Assert.AreEqual(3f, pos.z, 0.001f);
        }
        
        [Test]
        public void MotionCommand_GetDirection_HigherPrecision()
        {
            var cmd = new NetworkMotionCommand
            {
                dataX = 707,    // 707 / 1000 = 0.707
                dataY = 0,
                dataZ = 707
            };
            
            var dir = cmd.GetDirection();
            Assert.AreEqual(0.707f, dir.x, 0.001f);
            Assert.AreEqual(0f, dir.y, 0.001f);
            Assert.AreEqual(0.707f, dir.z, 0.001f);
        }
        
        [Test]
        public void MotionCommand_GetDurationAndFade_BitUnpacking()
        {
            // Duration = 1.5s → byte 150, Fade = 0.25s → byte 25
            // param2 = (150 << 8) | 25 = 38425
            var cmd = new NetworkMotionCommand { param2 = (150 << 8) | 25 };
            
            Assert.AreEqual(1.5f, cmd.GetDuration(), 0.001f);
            Assert.AreEqual(0.25f, cmd.GetFade(), 0.001f);
        }
        
        [Test]
        public void MotionCommand_GetRotationY_Wraps360()
        {
            // 0° → param1 = 0
            Assert.AreEqual(0f, new NetworkMotionCommand { param1 = 0 }.GetRotationY(), 0.01f);
            
            // 180° → param1 = 32768
            Assert.AreEqual(180f, new NetworkMotionCommand { param1 = 32768 }.GetRotationY(), 0.1f);
            
            // ~360° → param1 = 65535
            Assert.AreEqual(360f, new NetworkMotionCommand { param1 = 65535 }.GetRotationY(), 0.01f);
        }
        
        [Test]
        public void MotionCommand_GetJumpForce_Decompresses()
        {
            var cmd = new NetworkMotionCommand { param1 = 500 }; // 500 / 100 = 5.0
            Assert.AreEqual(5f, cmd.GetJumpForce(), 0.001f);
        }
        
        [Test]
        public void MotionCommand_GetSpeed_Decompresses()
        {
            var cmd = new NetworkMotionCommand { param1 = 100 }; // 100 / 10 = 10.0
            Assert.AreEqual(10f, cmd.GetSpeed(), 0.001f);
        }
        
        [Test]
        public void MotionCommand_Equals_SameValues()
        {
            var a = new NetworkMotionCommand
            {
                commandType = NetworkMotionCommandType.Dash,
                sequenceNumber = 42,
                dataX = 100, dataY = 200, dataZ = 300
            };
            var b = new NetworkMotionCommand
            {
                commandType = NetworkMotionCommandType.Dash,
                sequenceNumber = 42,
                dataX = 100, dataY = 200, dataZ = 300
            };
            Assert.IsTrue(a.Equals(b));
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INPUT STATE — DECOMPRESSION & FLAGS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Test]
        public void InputState_GetInputDirection_Decompresses()
        {
            var state = new NetworkInputState
            {
                inputX = 16383,   // ~0.5
                inputY = -32767   // ~-1.0
            };
            
            var dir = state.GetInputDirection();
            Assert.AreEqual(0.5f, dir.x, 0.001f);
            Assert.AreEqual(-1f, dir.y, 0.001f);
        }
        
        [Test]
        public void InputState_GetInputDirection_Zero()
        {
            var state = new NetworkInputState { inputX = 0, inputY = 0 };
            var dir = state.GetInputDirection();
            Assert.AreEqual(0f, dir.x, 0.001f);
            Assert.AreEqual(0f, dir.y, 0.001f);
        }
        
        [Test]
        public void InputState_GetDeltaTime_FromByte()
        {
            var state = new NetworkInputState { deltaTimeMs = 100 }; // 100ms = 0.1s
            Assert.AreEqual(0.1f, state.GetDeltaTime(), 0.001f);
        }
        
        [Test]
        public void InputState_HasFlag_IndividualFlags()
        {
            var state = new NetworkInputState { flags = NetworkInputState.FLAG_JUMP | NetworkInputState.FLAG_SPRINT };
            
            Assert.IsTrue(state.HasFlag(NetworkInputState.FLAG_JUMP));
            Assert.IsFalse(state.HasFlag(NetworkInputState.FLAG_DASH));
            Assert.IsTrue(state.HasFlag(NetworkInputState.FLAG_SPRINT));
            Assert.IsFalse(state.HasFlag(NetworkInputState.FLAG_CROUCH));
        }
        
        [Test]
        public void InputState_HasFlag_NoFlags()
        {
            var state = new NetworkInputState { flags = 0 };
            Assert.IsFalse(state.HasFlag(NetworkInputState.FLAG_JUMP));
            Assert.IsFalse(state.HasFlag(NetworkInputState.FLAG_DASH));
        }
        
        [Test]
        public void InputState_HasFlag_AllFlags()
        {
            var state = new NetworkInputState { flags = 0xFF };
            Assert.IsTrue(state.HasFlag(NetworkInputState.FLAG_JUMP));
            Assert.IsTrue(state.HasFlag(NetworkInputState.FLAG_DASH));
            Assert.IsTrue(state.HasFlag(NetworkInputState.FLAG_SPRINT));
            Assert.IsTrue(state.HasFlag(NetworkInputState.FLAG_CROUCH));
            Assert.IsTrue(state.HasFlag(NetworkInputState.FLAG_CUSTOM_1));
            Assert.IsTrue(state.HasFlag(NetworkInputState.FLAG_CUSTOM_4));
        }
        
        [Test]
        public void InputState_Equals_SameValues()
        {
            var a = new NetworkInputState { inputX = 100, inputY = 200, sequenceNumber = 5, flags = 3 };
            var b = new NetworkInputState { inputX = 100, inputY = 200, sequenceNumber = 5, flags = 3 };
            Assert.IsTrue(a.Equals(b));
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROTOCOL V2 CORRELATION
        // ════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void NetworkCorrelation_Compose_ExtractsRequestId()
        {
            uint correlation = NetworkCorrelation.Compose(0xABCD1234, (ushort)42);
            Assert.AreEqual((ushort)42, NetworkCorrelation.ExtractRequestId(correlation));
        }

        [Test]
        public void NetworkCorrelation_Compose_MatchesActorAndExtractsRequestId()
        {
            uint actorId = 0xABCD1234;
            uint correlationA = NetworkCorrelation.Compose(actorId, (ushort)42);
            uint correlationB = NetworkCorrelation.Compose(actorId, (ushort)99);

            Assert.AreEqual((ushort)42, NetworkCorrelation.ExtractRequestId(correlationA));
            Assert.AreEqual((ushort)99, NetworkCorrelation.ExtractRequestId(correlationB));
            Assert.IsTrue(NetworkCorrelation.MatchesActor(correlationA, actorId));
            Assert.IsTrue(NetworkCorrelation.MatchesActor(correlationB, actorId));
        }

        [Test]
        public void NetworkCorrelation_GetSequenceMask_Uses20BitMask()
        {
            Assert.AreEqual(0x000FFFFFu, NetworkCorrelation.GetSequenceMask());
        }

        [Test]
        public void NetworkCorrelation_ComposeFromCounter_ExtractsWideGeneration()
        {
            uint actorId = 0x1234ABCD;
            uint localCounter = 0x0012DEF1u; // generation(high nibble mask)=0x2, request=0xDEF1
            uint correlation = NetworkCorrelation.Compose(actorId, localCounter);

            Assert.AreEqual((ushort)0xDEF1, NetworkCorrelation.ExtractRequestId(correlation));
            Assert.AreEqual((ushort)0x0002, NetworkCorrelation.ExtractGenerationWide(correlation));

            uint expectedSequence = ((0x0002u << 16) | 0xDEF1u) & NetworkCorrelation.GetSequenceMask();
            Assert.AreEqual(expectedSequence, NetworkCorrelation.ExtractSequenceKey(correlation));
            Assert.IsTrue(NetworkCorrelation.MatchesActor(correlation, actorId));
        }

        [Test]
        public void NetworkCorrelation_MatchesActor_ReturnsFalseForDifferentActorSegment()
        {
            uint actorId = 0x00001234;
            uint correlation = NetworkCorrelation.Compose(actorId, (ushort)9);

            Assert.IsTrue(NetworkCorrelation.MatchesActor(correlation, actorId));

            uint differentActorId = actorId + 1;
            int safety = 0;
            while (NetworkCorrelation.MatchesActor(correlation, differentActorId) && safety++ < 100000)
            {
                differentActorId++;
            }

            Assert.IsFalse(NetworkCorrelation.MatchesActor(correlation, differentActorId));
        }

        [Test]
        public void NetworkCorrelation_Next_SkipsZeroRequestId()
        {
            ushort counter = ushort.MaxValue;
            uint correlation = NetworkCorrelation.Next(0x1234, ref counter);

            Assert.AreEqual((ushort)1, counter);
            Assert.AreEqual((ushort)1, NetworkCorrelation.ExtractRequestId(correlation));
        }

        [Test]
        public void NetworkCorrelation_Compose_ReusedRequestIdIncrementsGeneration()
        {
            uint actorId = 0x12345678;
            uint correlationA = NetworkCorrelation.Compose(actorId, (ushort)7);
            uint correlationB = NetworkCorrelation.Compose(actorId, (ushort)7);

            Assert.AreNotEqual(correlationA, correlationB);
            Assert.AreEqual((ushort)7, NetworkCorrelation.ExtractRequestId(correlationA));
            Assert.AreEqual((ushort)7, NetworkCorrelation.ExtractRequestId(correlationB));
            Assert.AreNotEqual(NetworkCorrelation.ExtractGeneration(correlationA), NetworkCorrelation.ExtractGeneration(correlationB));
        }

        [Test]
        public void NetworkCorrelation_ClearComposeState_ResetsOnlySpecifiedActor()
        {
            uint actorA = 0x00112233;
            uint actorB = 0x00445566;

            uint aFirst = NetworkCorrelation.Compose(actorA, (ushort)12);
            uint bFirst = NetworkCorrelation.Compose(actorB, (ushort)12);
            uint aSecond = NetworkCorrelation.Compose(actorA, (ushort)12);
            uint bSecond = NetworkCorrelation.Compose(actorB, (ushort)12);

            Assert.AreNotEqual(
                NetworkCorrelation.ExtractGeneration(aFirst),
                NetworkCorrelation.ExtractGeneration(aSecond));
            Assert.AreNotEqual(
                NetworkCorrelation.ExtractGeneration(bFirst),
                NetworkCorrelation.ExtractGeneration(bSecond));

            NetworkCorrelation.ClearComposeState(actorA);

            uint aAfterClear = NetworkCorrelation.Compose(actorA, (ushort)12);
            uint bAfterClear = NetworkCorrelation.Compose(actorB, (ushort)12);

            Assert.AreEqual(
                NetworkCorrelation.ExtractGeneration(aFirst),
                NetworkCorrelation.ExtractGeneration(aAfterClear));
            Assert.AreNotEqual(
                NetworkCorrelation.ExtractGeneration(bFirst),
                NetworkCorrelation.ExtractGeneration(bAfterClear));
        }

        [Test]
        public void NetworkCorrelation_ExtractSequenceKey_ChangesAcrossGenerations()
        {
            uint actorId = 0x0000ABCD;
            uint correlationA = NetworkCorrelation.Compose(actorId, (ushort)1);
            uint correlationB = NetworkCorrelation.Compose(actorId, (ushort)1);

            Assert.AreNotEqual(
                NetworkCorrelation.ExtractSequenceKey(correlationA),
                NetworkCorrelation.ExtractSequenceKey(correlationB));
        }

        [Test]
        public void SecurityIntegration_IsProtocolContextMismatch_DetectsActorSegmentMismatch()
        {
            uint actorId = 0x00001234;
            uint correlation = NetworkCorrelation.Compose(actorId, (ushort)7);

            Assert.IsFalse(SecurityIntegration.IsProtocolContextMismatch(actorId, correlation));

            uint mismatchedActor = actorId + 1;
            int safety = 0;
            while (!SecurityIntegration.IsProtocolContextMismatch(mismatchedActor, correlation) && safety++ < 100000)
            {
                mismatchedActor++;
            }

            Assert.IsTrue(SecurityIntegration.IsProtocolContextMismatch(mismatchedActor, correlation));
        }

        [Test]
        public void SecurityIntegration_IsProtocolContextMismatch_DetectsZeroRequestSegment()
        {
            uint correlationWithZeroRequestSegment = (0x1234u << 16);
            Assert.IsTrue(SecurityIntegration.IsProtocolContextMismatch(0x00001234, correlationWithZeroRequestSegment));
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // OWNERSHIP RESOLUTION
        // ════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void OwnershipResolver_ActorOwner_CanResolveAndValidate()
        {
            var resolver = new NetworkOwnershipResolver();
            resolver.RegisterEntityOwner(1001, 7);

            Assert.IsTrue(resolver.TryResolveOwnerClientId(1001, out uint owner));
            Assert.AreEqual(7u, owner);
            Assert.IsTrue(resolver.ValidateOwnership(7, 1001, out uint resolvedOwner));
            Assert.AreEqual(7u, resolvedOwner);
            Assert.IsFalse(resolver.ValidateOwnership(8, 1001, out _));
        }

        [Test]
        public void OwnershipResolver_EntityActorMap_ResolvesOwnerThroughActor()
        {
            var resolver = new NetworkOwnershipResolver();
            resolver.RegisterEntityOwner(2001, 11);      // Actor owner
            resolver.RegisterEntityActor(3001, 2001);    // Bag/entity -> actor

            Assert.IsTrue(resolver.TryResolveOwnerClientIdForEntity(3001, out uint owner));
            Assert.AreEqual(11u, owner);
        }

        [Test]
        public void OwnershipResolver_UnresolvedActor_RejectsValidation()
        {
            var resolver = new NetworkOwnershipResolver();
            Assert.IsFalse(resolver.ValidateOwnership(1, 9999, out _));
        }

        [Test]
        public void SecurityIntegration_RegisterActorOwnership_ResolvesAndClearsOwner()
        {
            var originalResolver = SecurityIntegration.OwnershipResolver;
            try
            {
                SecurityIntegration.OwnershipResolver = new NetworkOwnershipResolver();

                SecurityIntegration.RegisterActorOwnership(4001, 77);
                Assert.IsTrue(SecurityIntegration.TryResolveActorOwner(4001, out uint ownerClientId));
                Assert.AreEqual(77u, ownerClientId);

                SecurityIntegration.UnregisterActorOwnership(4001);
                Assert.IsFalse(SecurityIntegration.TryResolveActorOwner(4001, out _));
            }
            finally
            {
                SecurityIntegration.OwnershipResolver = originalResolver;
            }
        }

        [Test]
        public void SecurityIntegration_UnregisterActorOwnership_ClearsCorrelationComposeState()
        {
            uint actorId = 0x00004001;
            uint correlationA = NetworkCorrelation.Compose(actorId, (ushort)21);
            uint correlationB = NetworkCorrelation.Compose(actorId, (ushort)21);
            Assert.AreNotEqual(correlationA, correlationB);

            SecurityIntegration.UnregisterActorOwnership(actorId);

            uint correlationAfterClear = NetworkCorrelation.Compose(actorId, (ushort)21);
            Assert.AreEqual(
                NetworkCorrelation.ExtractGeneration(correlationA),
                NetworkCorrelation.ExtractGeneration(correlationAfterClear));
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // SECURITY PATH — SEQUENCE TRACKER
        // ════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void SequenceTracker_AcceptsNewSequence()
        {
            var tracker = new SequenceTracker();
            Assert.IsTrue(tracker.ValidateSequence(1, 1));
            Assert.IsTrue(tracker.ValidateSequence(1, 2));
            Assert.IsTrue(tracker.ValidateSequence(1, 3));
        }

        [Test]
        public void SequenceTracker_RejectsReplay()
        {
            var tracker = new SequenceTracker();
            Assert.IsTrue(tracker.ValidateSequence(1, 5));
            Assert.IsFalse(tracker.ValidateSequence(1, 5)); // replay of same sequence
        }

        [Test]
        public void SequenceTracker_RejectsOlderSequence()
        {
            var tracker = new SequenceTracker();
            Assert.IsTrue(tracker.ValidateSequence(1, 10));
            Assert.IsFalse(tracker.ValidateSequence(1, 5)); // older than last
        }

        [Test]
        public void SequenceTracker_MultipleClients_Independent()
        {
            var tracker = new SequenceTracker();
            Assert.IsTrue(tracker.ValidateSequence(1, 5));
            Assert.IsTrue(tracker.ValidateSequence(2, 5)); // different client, same sequence is OK
            Assert.IsFalse(tracker.ValidateSequence(1, 5)); // same client replay is rejected
        }

        [Test]
        public void SequenceTracker_ClearClient_AllowsReuse()
        {
            var tracker = new SequenceTracker();
            Assert.IsTrue(tracker.ValidateSequence(1, 10));
            Assert.IsFalse(tracker.ValidateSequence(1, 10));

            tracker.ClearClient(1);
            Assert.IsTrue(tracker.ValidateSequence(1, 10)); // cleared, so allowed again
        }

        [Test]
        public void SequenceTracker_Clear_ResetsAllClients()
        {
            var tracker = new SequenceTracker();
            tracker.ValidateSequence(1, 10);
            tracker.ValidateSequence(2, 20);

            tracker.Clear();

            Assert.IsTrue(tracker.ValidateSequence(1, 10));
            Assert.IsTrue(tracker.ValidateSequence(2, 20));
        }

        [Test]
        public void SequenceTracker_ScopedByModule_AllowsSameSequenceAcrossModules()
        {
            var tracker = new SequenceTracker();
            Assert.IsTrue(tracker.ValidateSequence(1, "Inventory", 1001, 1));
            Assert.IsTrue(tracker.ValidateSequence(1, "Stats", 1001, 1));
            Assert.IsFalse(tracker.ValidateSequence(1, "Inventory", 1001, 1));
        }

        [Test]
        public void SequenceTracker_ScopedByActor_AllowsSameSequenceAcrossActors()
        {
            var tracker = new SequenceTracker();
            Assert.IsTrue(tracker.ValidateSequence(1, "Core", 1001, 1));
            Assert.IsTrue(tracker.ValidateSequence(1, "Core", 2002, 1));
            Assert.IsFalse(tracker.ValidateSequence(1, "Core", 1001, 1));
        }

        [Test]
        public void SequenceTracker_MaskedSequence_AcceptsGenerationWrapAndRejectsStaleReplay()
        {
            var tracker = new SequenceTracker();
            uint sequenceMask = NetworkCorrelation.GetSequenceMask();

            uint topMinusOne = (sequenceMask - 1u) & sequenceMask;
            uint top = sequenceMask;
            Assert.IsTrue(tracker.ValidateSequence(1, "Core", 9001, topMinusOne, sequenceMask));
            Assert.IsTrue(tracker.ValidateSequence(1, "Core", 9001, top, sequenceMask));
            Assert.IsTrue(tracker.ValidateSequence(1, "Core", 9001, 0x00000001u, sequenceMask)); // wrapped forward

            Assert.IsFalse(tracker.ValidateSequence(1, "Core", 9001, top, sequenceMask)); // stale after wrap
        }

        [Test]
        public void SequenceTracker_ConcurrentValidationAcrossClients_DoesNotThrow()
        {
            var tracker = new SequenceTracker();
            uint sequenceMask = NetworkCorrelation.GetSequenceMask();

            Exception workerAError = null;
            Exception workerBError = null;

            Thread workerA = new Thread(() =>
            {
                try
                {
                    for (uint i = 1; i < 2000; i++)
                    {
                        tracker.ValidateSequence(1, "Core", 7001, i, sequenceMask);
                    }
                }
                catch (Exception ex)
                {
                    workerAError = ex;
                }
            });

            Thread workerB = new Thread(() =>
            {
                try
                {
                    for (uint i = 1; i < 2000; i++)
                    {
                        tracker.ValidateSequence(2, "Core", 7001, i, sequenceMask);
                    }
                }
                catch (Exception ex)
                {
                    workerBError = ex;
                }
            });

            workerA.Start();
            workerB.Start();
            workerA.Join();
            workerB.Join();

            Assert.IsNull(workerAError);
            Assert.IsNull(workerBError);
        }

        [Test]
        public void SequenceTracker_ClearClient_ClearsAllScopesForClient()
        {
            var tracker = new SequenceTracker();
            Assert.IsTrue(tracker.ValidateSequence(1, "Shooter", 7001, 5));
            Assert.IsTrue(tracker.ValidateSequence(1, "Abilities", 7001, 9));
            Assert.IsTrue(tracker.ValidateSequence(2, "Shooter", 7001, 5));

            tracker.ClearClient(1);

            Assert.IsTrue(tracker.ValidateSequence(1, "Shooter", 7001, 5));
            Assert.IsTrue(tracker.ValidateSequence(1, "Abilities", 7001, 9));
            Assert.IsFalse(tracker.ValidateSequence(2, "Shooter", 7001, 5));
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // SECURITY PATH — REQUEST CONTEXT
        // ════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void RequestContext_Create_SetsFields()
        {
            var ctx = NetworkRequestContext.Create(42, 100);
            Assert.AreEqual(42u, ctx.ActorNetworkId);
            Assert.AreEqual(100u, ctx.CorrelationId);
        }

        [Test]
        public void RequestContext_IsValid_TrueWhenBothNonZero()
        {
            var ctx = NetworkRequestContext.Create(1, 1);
            Assert.IsTrue(ctx.IsValid);
        }

        [Test]
        public void RequestContext_IsValid_FalseWhenActorZero()
        {
            var ctx = NetworkRequestContext.Create(0, 100);
            Assert.IsFalse(ctx.IsValid);
        }

        [Test]
        public void RequestContext_IsValid_FalseWhenCorrelationZero()
        {
            var ctx = NetworkRequestContext.Create(42, 0);
            Assert.IsFalse(ctx.IsValid);
        }

        [Test]
        public void RequestContext_IsValid_FalseWhenBothZero()
        {
            var ctx = default(NetworkRequestContext);
            Assert.IsFalse(ctx.IsValid);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // SECURITY PATH — OWNERSHIP RESOLVER (EXTENDED)
        // ════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void OwnershipResolver_UnregisterEntity_RemovesMapping()
        {
            var resolver = new NetworkOwnershipResolver();
            resolver.RegisterEntityOwner(1001, 7);
            Assert.IsTrue(resolver.TryResolveOwnerClientId(1001, out _));

            resolver.UnregisterEntity(1001);
            Assert.IsFalse(resolver.TryResolveOwnerClientId(1001, out _));
        }

        [Test]
        public void OwnershipResolver_Clear_RemovesAllMappings()
        {
            var resolver = new NetworkOwnershipResolver();
            resolver.RegisterEntityOwner(1001, 7);
            resolver.RegisterEntityOwner(1002, 8);
            resolver.RegisterEntityActor(2001, 1001);

            resolver.Clear();

            Assert.IsFalse(resolver.TryResolveOwnerClientId(1001, out _));
            Assert.IsFalse(resolver.TryResolveOwnerClientId(1002, out _));
            Assert.IsFalse(resolver.TryResolveOwnerClientIdForEntity(2001, out _));
        }

        [Test]
        public void OwnershipResolver_OverwriteOwner_UsesLatest()
        {
            var resolver = new NetworkOwnershipResolver();
            resolver.RegisterEntityOwner(1001, 7);
            resolver.RegisterEntityOwner(1001, 99); // overwrite

            Assert.IsTrue(resolver.TryResolveOwnerClientId(1001, out uint owner));
            Assert.AreEqual(99u, owner);
        }

        [Test]
        public void OwnershipResolver_EntityActorChain_ValidatesOwnership()
        {
            var resolver = new NetworkOwnershipResolver();
            resolver.RegisterEntityOwner(1001, 7);   // actor 1001 owned by client 7
            resolver.RegisterEntityActor(2001, 1001); // entity 2001 maps to actor 1001

            // Validate ownership through the chain
            Assert.IsTrue(resolver.TryResolveActorNetworkIdForEntity(2001, out uint actor));
            Assert.AreEqual(1001u, actor);
            Assert.IsTrue(resolver.TryResolveOwnerClientIdForEntity(2001, out uint entityOwner));
            Assert.AreEqual(7u, entityOwner);
        }

        [Test]
        public void OwnershipResolver_UnmappedEntityActorLookup_ReturnsFalse()
        {
            var resolver = new NetworkOwnershipResolver();
            Assert.IsFalse(resolver.TryResolveActorNetworkIdForEntity(9999, out _));
        }

        [Test]
        public void OwnershipResolver_EntityOwnerFallback_UsesDirectEntityOwnership()
        {
            var resolver = new NetworkOwnershipResolver();
            resolver.RegisterEntityOwner(4001, 22);

            Assert.IsTrue(resolver.TryResolveOwnerClientIdForEntity(4001, out uint owner));
            Assert.AreEqual(22u, owner);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // SECURITY PATH — VALIDATION BEHAVIOR WITH/WITHOUT SECURITY MANAGER
        // ════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void SecurityIntegration_ValidateModuleRequest_NullManager_NoSenderContext_ReturnsTrue()
        {
            // Non-server-like context should pass-through when manager is absent.
            var ctx = NetworkRequestContext.Create(42, NetworkCorrelation.Compose(42, (ushort)1));
            Assert.IsTrue(SecurityIntegration.ValidateModuleRequest(NetworkTransportBridge.InvalidClientId, in ctx, "Core", "TestRequest"));
        }

        [Test]
        public void SecurityIntegration_ValidateModuleRequest_NullManager_AuthoritativeModuleContext_ReturnsFalse()
        {
            SecurityIntegration.SetModuleServerContext("Core", true);
            var ctx = NetworkRequestContext.Create(42, NetworkCorrelation.Compose(42, (ushort)1));

            Assert.IsFalse(SecurityIntegration.ValidateModuleRequest(1, in ctx, "Core", "TestRequest"));
        }

        [Test]
        public void SecurityIntegration_ValidateOwnership_NullManager_NoSenderContext_ReturnsTrue()
        {
            // Non-server-like context should pass-through when manager is absent.
            Assert.IsTrue(SecurityIntegration.ValidateOwnership(NetworkTransportBridge.InvalidClientId, 1001, "Core"));
        }

        [Test]
        public void SecurityIntegration_ValidateOwnership_NullManager_AuthoritativeModuleContext_ReturnsFalse()
        {
            SecurityIntegration.SetModuleServerContext("Core", true);
            Assert.IsFalse(SecurityIntegration.ValidateOwnership(1, 1001, "Core"));
        }

        [Test]
        public void SecurityIntegration_ValidateOwnership_UnresolvedOwner_PrimesFromTransportVerification()
        {
            var originalResolver = SecurityIntegration.OwnershipResolver;
            UnityEngine.GameObject securityGo = EnsureSecurityManagerForServerTests();
            var bridgeGo = new UnityEngine.GameObject("OwnershipBootstrapBridge");
            var bridge = bridgeGo.AddComponent<OwnershipBootstrapTestBridge>();

            try
            {
                SecurityIntegration.SetModuleServerContext("Core", true);
                SecurityIntegration.OwnershipResolver = new NetworkOwnershipResolver();

                Assert.IsTrue(SecurityIntegration.ValidateOwnership(7, 1001, "Core"));
                Assert.IsTrue(bridge.VerifyCalled, "Transport verification should be used to bootstrap unresolved ownership.");
                Assert.IsTrue(SecurityIntegration.TryResolveActorOwner(1001, out uint ownerClientId));
                Assert.AreEqual(7u, ownerClientId);
            }
            finally
            {
                SecurityIntegration.OwnershipResolver = originalResolver;
                UnityEngine.Object.DestroyImmediate(bridgeGo);
                DestroySecurityManagerIfCreated(securityGo);
            }
        }

        [Test]
        public void SecurityIntegration_ValidateOwnership_StaleMismatch_RefreshesFromTransportVerification()
        {
            var originalResolver = SecurityIntegration.OwnershipResolver;
            UnityEngine.GameObject securityGo = EnsureSecurityManagerForServerTests();
            var bridgeGo = new UnityEngine.GameObject("OwnershipMismatchBridge");
            var bridge = bridgeGo.AddComponent<OwnershipBootstrapTestBridge>();

            try
            {
                SecurityIntegration.SetModuleServerContext("Core", true);
                SecurityIntegration.OwnershipResolver = new NetworkOwnershipResolver();

                // Simulate stale ownership cache that points to an old owner.
                SecurityIntegration.RegisterActorOwnership(2002, 99);
                bridge.VerifiedOwnerClientId = 7;
                bridge.VerifyResult = true;

                Assert.IsTrue(SecurityIntegration.ValidateOwnership(7, 2002, "Core"));
                Assert.IsTrue(bridge.VerifyCalled, "Transport verification should refresh stale ownership mismatches.");
                Assert.IsTrue(SecurityIntegration.TryResolveActorOwner(2002, out uint ownerClientId));
                Assert.AreEqual(7u, ownerClientId);
            }
            finally
            {
                SecurityIntegration.OwnershipResolver = originalResolver;
                UnityEngine.Object.DestroyImmediate(bridgeGo);
                DestroySecurityManagerIfCreated(securityGo);
            }
        }

        [Test]
        public void SecurityIntegration_ValidateTargetEntityOwnership_UnresolvedEntity_PrimesFromTransportVerification()
        {
            var originalResolver = SecurityIntegration.OwnershipResolver;
            UnityEngine.GameObject securityGo = EnsureSecurityManagerForServerTests();
            var bridgeGo = new UnityEngine.GameObject("TargetOwnershipBootstrapBridge");
            var bridge = bridgeGo.AddComponent<OwnershipBootstrapTestBridge>();

            try
            {
                SecurityIntegration.SetModuleServerContext("Core", true);
                SecurityIntegration.OwnershipResolver = new NetworkOwnershipResolver();
                bridge.VerifiedOwnerClientId = 7;
                bridge.VerifyResult = true;

                const uint actorNetworkId = 3001;
                const uint targetEntityNetworkId = 7001;

                // Target entity is linked to actor, but no explicit owner is registered yet.
                SecurityIntegration.RegisterEntityActor(targetEntityNetworkId, actorNetworkId);

                Assert.IsTrue(SecurityIntegration.ValidateTargetEntityOwnership(
                    7,
                    actorNetworkId,
                    targetEntityNetworkId,
                    "Core",
                    "TargetOwnershipTest"));

                Assert.IsTrue(bridge.VerifyCalled, "Transport verification should prime unresolved target ownership.");
                Assert.IsTrue(SecurityIntegration.OwnershipResolver.TryResolveOwnerClientIdForEntity(targetEntityNetworkId, out uint ownerClientId));
                Assert.AreEqual(7u, ownerClientId);
            }
            finally
            {
                SecurityIntegration.OwnershipResolver = originalResolver;
                UnityEngine.Object.DestroyImmediate(bridgeGo);
                DestroySecurityManagerIfCreated(securityGo);
            }
        }

        [Test]
        public void SecurityIntegration_EnsureSecurityManagerInitialized_ServerWithoutManager_ReturnsFalse()
        {
            if (NetworkSecurityManager.Instance != null)
            {
                Assert.IsTrue(SecurityIntegration.EnsureSecurityManagerInitialized(true, () => 1f));
                return;
            }

            Assert.IsFalse(SecurityIntegration.EnsureSecurityManagerInitialized(true, () => 1f));
        }

        [Test]
        public void SecurityIntegration_EnsureSecurityManagerInitialized_InitializesExistingManager()
        {
            if (NetworkSecurityManager.Instance != null)
            {
                Assert.IsTrue(SecurityIntegration.EnsureSecurityManagerInitialized(true, () => 5f));
                Assert.IsTrue(NetworkSecurityManager.Instance.IsInitialized);
                Assert.IsTrue(NetworkSecurityManager.Instance.IsServer);
                return;
            }

            var go = new UnityEngine.GameObject("SecurityManager_Test");
            var manager = go.AddComponent<NetworkSecurityManager>();

            try
            {
                Assert.IsFalse(manager.IsInitialized);
                Assert.IsTrue(SecurityIntegration.EnsureSecurityManagerInitialized(true, () => 5f));
                Assert.IsTrue(manager.IsInitialized);
                Assert.IsTrue(manager.IsServer);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SecurityIntegration_ValidateCoreRequest_NullManager_ReturnsFalse()
        {
            uint correlationId = NetworkCorrelation.Compose(1001, 1);
            if (NetworkSecurityManager.Instance != null)
            {
                Assert.IsTrue(SecurityIntegration.EnsureSecurityManagerInitialized(true, () => 1f));
                Assert.IsTrue(SecurityIntegration.ValidateCoreRequest(1, 1001, correlationId, "Move"));
                return;
            }

            Assert.IsFalse(SecurityIntegration.ValidateCoreRequest(1, 1001, correlationId, "Move"));
        }

        [Test]
        public void SecurityIntegration_ValidateStatsRequest_NullManager_ReturnsFalse()
        {
            uint correlationId = NetworkCorrelation.Compose(1001, 1);
            if (NetworkSecurityManager.Instance != null)
            {
                Assert.IsTrue(SecurityIntegration.EnsureSecurityManagerInitialized(true, () => 1f));
                Assert.IsTrue(SecurityIntegration.ValidateStatsRequest(1, 1001, correlationId, "ModifyStat", 42, 10f));
                return;
            }

            Assert.IsFalse(SecurityIntegration.ValidateStatsRequest(1, 1001, correlationId, "ModifyStat", 42, 10f));
        }

        [Test]
        public void SecurityIntegration_ValidateMeleeRequest_NullManager_ReturnsFalse()
        {
            uint correlationId = NetworkCorrelation.Compose(1001, 1);
            if (NetworkSecurityManager.Instance != null)
            {
                Assert.IsTrue(SecurityIntegration.EnsureSecurityManagerInitialized(true, () => 1f));
                Assert.IsTrue(SecurityIntegration.ValidateMeleeRequest(1, 1001, correlationId, "Attack"));
                return;
            }

            Assert.IsFalse(SecurityIntegration.ValidateMeleeRequest(1, 1001, correlationId, "Attack"));
        }

        [Test]
        public void SecurityIntegration_ValidateShooterRequest_NullManager_ReturnsFalse()
        {
            uint correlationId = NetworkCorrelation.Compose(1001, 1);
            if (NetworkSecurityManager.Instance != null)
            {
                Assert.IsTrue(SecurityIntegration.EnsureSecurityManagerInitialized(true, () => 1f));
                Assert.IsTrue(SecurityIntegration.ValidateShooterRequest(1, 1001, correlationId, "Fire"));
                return;
            }

            Assert.IsFalse(SecurityIntegration.ValidateShooterRequest(1, 1001, correlationId, "Fire"));
        }

        [Test]
        public void SecurityIntegration_ValidateAbilitiesRequest_NullManager_ReturnsFalse()
        {
            uint correlationId = NetworkCorrelation.Compose(1001, 1);
            if (NetworkSecurityManager.Instance != null)
            {
                Assert.IsTrue(SecurityIntegration.EnsureSecurityManagerInitialized(true, () => 1f));
                Assert.IsTrue(SecurityIntegration.ValidateAbilitiesRequest(1, 1001, correlationId, "Cast"));
                return;
            }

            Assert.IsFalse(SecurityIntegration.ValidateAbilitiesRequest(1, 1001, correlationId, "Cast"));
        }

        [Test]
        public void SecurityIntegration_OwnershipResolver_Injectable()
        {
            // Save original and restore after test
            var original = SecurityIntegration.OwnershipResolver;
            try
            {
                var testResolver = new NetworkOwnershipResolver();
                testResolver.RegisterEntityOwner(5001, 99);

                SecurityIntegration.OwnershipResolver = testResolver;
                Assert.AreSame(testResolver, SecurityIntegration.OwnershipResolver);
            }
            finally
            {
                SecurityIntegration.OwnershipResolver = original;
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONTROLLER LOGIC — POSITION STATE
        // ════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void PositionState_Create_RoundTripsPosition()
        {
            var state = NetworkPositionState.Create(
                new UnityEngine.Vector3(10.5f, -2.0f, 3.25f),
                rotationY: 90f,
                verticalVel: -9.81f,
                lastInput: 42,
                isGrounded: true,
                isJumping: false
            );

            var pos = state.GetPosition();
            Assert.AreEqual(10.5f, pos.x, 0.02f);
            Assert.AreEqual(-2.0f, pos.y, 0.02f);
            Assert.AreEqual(3.25f, pos.z, 0.02f);
        }

        [Test]
        public void PositionState_Create_RoundTripsRotation()
        {
            var state = NetworkPositionState.Create(
                UnityEngine.Vector3.zero,
                rotationY: 180f,
                verticalVel: 0f,
                lastInput: 0,
                isGrounded: false,
                isJumping: false
            );

            Assert.AreEqual(180f, state.GetRotationY(), 0.1f);
        }

        [Test]
        public void PositionState_Create_RoundTripsVerticalVelocity()
        {
            var state = NetworkPositionState.Create(
                UnityEngine.Vector3.zero,
                rotationY: 0f,
                verticalVel: -9.81f,
                lastInput: 0,
                isGrounded: false,
                isJumping: false
            );

            Assert.AreEqual(-9.81f, state.GetVerticalVelocity(), 0.02f);
        }

        [Test]
        public void PositionState_Flags_Grounded()
        {
            var state = NetworkPositionState.Create(
                UnityEngine.Vector3.zero, 0f, 0f, 0, isGrounded: true, isJumping: false);
            Assert.IsTrue(state.IsGrounded);
            Assert.IsFalse(state.IsJumping);
        }

        [Test]
        public void PositionState_Flags_Jumping()
        {
            var state = NetworkPositionState.Create(
                UnityEngine.Vector3.zero, 0f, 0f, 0, isGrounded: false, isJumping: true);
            Assert.IsFalse(state.IsGrounded);
            Assert.IsTrue(state.IsJumping);
        }

        [Test]
        public void PositionState_Flags_BothSet()
        {
            var state = NetworkPositionState.Create(
                UnityEngine.Vector3.zero, 0f, 0f, 0, isGrounded: true, isJumping: true);
            Assert.IsTrue(state.IsGrounded);
            Assert.IsTrue(state.IsJumping);
        }

        [Test]
        public void PositionState_LastProcessedInput_PreservesValue()
        {
            var state = NetworkPositionState.Create(
                UnityEngine.Vector3.zero, 0f, 0f, lastInput: 12345, isGrounded: false, isJumping: false);
            Assert.AreEqual((ushort)12345, state.lastProcessedInput);
        }

        [Test]
        public void PositionState_Equals_SameValues()
        {
            var a = NetworkPositionState.Create(
                new UnityEngine.Vector3(1f, 2f, 3f), 90f, -5f, 10, true, false);
            var b = NetworkPositionState.Create(
                new UnityEngine.Vector3(1f, 2f, 3f), 90f, -5f, 10, true, false);

            Assert.IsTrue(a.Equals(b));
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        [Test]
        public void PositionState_Equals_DifferentPosition()
        {
            var a = NetworkPositionState.Create(
                new UnityEngine.Vector3(1f, 2f, 3f), 0f, 0f, 0, false, false);
            var b = NetworkPositionState.Create(
                new UnityEngine.Vector3(4f, 5f, 6f), 0f, 0f, 0, false, false);

            Assert.IsFalse(a.Equals(b));
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONTROLLER LOGIC — INPUT STATE CREATE & ROUND-TRIP
        // ════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void InputState_Create_RoundTripsDirection()
        {
            var state = NetworkInputState.Create(
                new UnityEngine.Vector2(0.5f, -1f), sequence: 1, deltaTime: 0.016f);

            var dir = state.GetInputDirection();
            Assert.AreEqual(0.5f, dir.x, 0.001f);
            Assert.AreEqual(-1f, dir.y, 0.001f);
        }

        [Test]
        public void InputState_Create_RoundTripsDeltaTime()
        {
            // 16ms = 0.016s → byte 16 → 0.016s
            var state = NetworkInputState.Create(
                UnityEngine.Vector2.zero, sequence: 1, deltaTime: 0.016f);

            Assert.AreEqual(0.016f, state.GetDeltaTime(), 0.002f);
        }

        [Test]
        public void InputState_Create_PreservesSequence()
        {
            var state = NetworkInputState.Create(
                UnityEngine.Vector2.zero, sequence: 42, deltaTime: 0.016f);
            Assert.AreEqual((ushort)42, state.sequenceNumber);
        }

        [Test]
        public void InputState_Create_PreservesFlags()
        {
            byte testFlags = NetworkInputState.FLAG_JUMP | NetworkInputState.FLAG_CROUCH;
            var state = NetworkInputState.Create(
                UnityEngine.Vector2.zero, sequence: 1, deltaTime: 0.016f, flags: testFlags);

            Assert.IsTrue(state.HasFlag(NetworkInputState.FLAG_JUMP));
            Assert.IsTrue(state.HasFlag(NetworkInputState.FLAG_CROUCH));
            Assert.IsFalse(state.HasFlag(NetworkInputState.FLAG_DASH));
            Assert.IsFalse(state.HasFlag(NetworkInputState.FLAG_SPRINT));
        }

        [Test]
        public void InputState_Create_ZeroInput_IsZero()
        {
            var state = NetworkInputState.Create(
                UnityEngine.Vector2.zero, sequence: 0, deltaTime: 0f);

            var dir = state.GetInputDirection();
            Assert.AreEqual(0f, dir.x, 0.001f);
            Assert.AreEqual(0f, dir.y, 0.001f);
        }

        [Test]
        public void InputState_DeltaTime_MaxCappedAt255ms()
        {
            // Passing 0.5s (500ms) should be capped at 255ms = 0.255s
            var state = NetworkInputState.Create(
                UnityEngine.Vector2.zero, sequence: 1, deltaTime: 0.5f);

            Assert.AreEqual(255, state.deltaTimeMs);
            Assert.AreEqual(0.255f, state.GetDeltaTime(), 0.001f);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONTROLLER LOGIC — RELEVANCE TIERS (NetworkSessionProfile)
        // ════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void SessionProfile_GetTier_NearDistance()
        {
            var profile = UnityEngine.ScriptableObject.CreateInstance<NetworkSessionProfile>();
            try
            {
                profile.ApplyPreset(NetworkSessionPreset.Standard);
                // Standard: nearDistance=20, midDistance=50
                Assert.AreEqual(NetworkRelevanceTier.Near, profile.GetTier(0f));
                Assert.AreEqual(NetworkRelevanceTier.Near, profile.GetTier(10f));
                Assert.AreEqual(NetworkRelevanceTier.Near, profile.GetTier(20f)); // boundary
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void SessionProfile_GetTier_MidDistance()
        {
            var profile = UnityEngine.ScriptableObject.CreateInstance<NetworkSessionProfile>();
            try
            {
                profile.ApplyPreset(NetworkSessionPreset.Standard);
                Assert.AreEqual(NetworkRelevanceTier.Mid, profile.GetTier(20.1f));
                Assert.AreEqual(NetworkRelevanceTier.Mid, profile.GetTier(35f));
                Assert.AreEqual(NetworkRelevanceTier.Mid, profile.GetTier(50f)); // boundary
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void SessionProfile_GetTier_FarDistance()
        {
            var profile = UnityEngine.ScriptableObject.CreateInstance<NetworkSessionProfile>();
            try
            {
                profile.ApplyPreset(NetworkSessionPreset.Standard);
                Assert.AreEqual(NetworkRelevanceTier.Far, profile.GetTier(50.1f));
                Assert.AreEqual(NetworkRelevanceTier.Far, profile.GetTier(100f));
                Assert.AreEqual(NetworkRelevanceTier.Far, profile.GetTier(1000f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void SessionProfile_GetTierSettings_ReturnsTierSpecificSettings()
        {
            var profile = UnityEngine.ScriptableObject.CreateInstance<NetworkSessionProfile>();
            try
            {
                profile.ApplyPreset(NetworkSessionPreset.Standard);

                var nearSettings = profile.GetTierSettings(NetworkRelevanceTier.Near);
                var midSettings = profile.GetTierSettings(NetworkRelevanceTier.Mid);
                var farSettings = profile.GetTierSettings(NetworkRelevanceTier.Far);

                // Near should have highest fidelity (highest rates, lowest delays)
                Assert.GreaterOrEqual(nearSettings.stateApplyRate, midSettings.stateApplyRate);
                Assert.GreaterOrEqual(midSettings.stateApplyRate, farSettings.stateApplyRate);

                // Far should have highest snap distance
                Assert.GreaterOrEqual(farSettings.snapDistance, nearSettings.snapDistance);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void SessionProfile_ApplyPreset_Duel_HasTighterThresholds()
        {
            var profile = UnityEngine.ScriptableObject.CreateInstance<NetworkSessionProfile>();
            try
            {
                profile.ApplyPreset(NetworkSessionPreset.Duel);
                // Duel: nearDistance=25, midDistance=60
                Assert.AreEqual(NetworkRelevanceTier.Near, profile.GetTier(25f));
                Assert.AreEqual(NetworkRelevanceTier.Mid, profile.GetTier(25.1f));
                Assert.AreEqual(NetworkRelevanceTier.Mid, profile.GetTier(60f));
                Assert.AreEqual(NetworkRelevanceTier.Far, profile.GetTier(60.1f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void SessionProfile_ApplyPreset_Massive_HasLargerFalloff()
        {
            var profile = UnityEngine.ScriptableObject.CreateInstance<NetworkSessionProfile>();
            try
            {
                profile.ApplyPreset(NetworkSessionPreset.Massive);
                // Massive: nearDistance=18, midDistance=40
                Assert.AreEqual(NetworkRelevanceTier.Near, profile.GetTier(18f));
                Assert.AreEqual(NetworkRelevanceTier.Mid, profile.GetTier(18.1f));
                Assert.AreEqual(NetworkRelevanceTier.Far, profile.GetTier(40.1f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // INTEGRATION — CORRELATION + CONTEXT PIPELINE
        // ════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void Integration_CorrelationPipeline_ComposeAndValidateContext()
        {
            uint actorId = 42;
            ushort requestId = 7;

            uint correlation = NetworkCorrelation.Compose(actorId, requestId);
            var ctx = NetworkRequestContext.Create(actorId, correlation);

            Assert.IsTrue(ctx.IsValid);
            Assert.AreEqual(requestId, NetworkCorrelation.ExtractRequestId(ctx.CorrelationId));
        }

        [Test]
        public void Integration_CorrelationNext_AutoIncrementsAndCreatesValidContext()
        {
            uint actorId = 100;
            ushort counter = 0;

            uint correlationA = NetworkCorrelation.Next(actorId, ref counter);
            uint correlationB = NetworkCorrelation.Next(actorId, ref counter);

            var ctxA = NetworkRequestContext.Create(actorId, correlationA);
            var ctxB = NetworkRequestContext.Create(actorId, correlationB);

            Assert.IsTrue(ctxA.IsValid);
            Assert.IsTrue(ctxB.IsValid);

            ushort seqA = NetworkCorrelation.ExtractRequestId(ctxA.CorrelationId);
            ushort seqB = NetworkCorrelation.ExtractRequestId(ctxB.CorrelationId);
            Assert.AreNotEqual(seqA, seqB);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // INTEGRATION — SEQUENCE TRACKER + CORRELATION
        // ════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void Integration_SequenceTracker_DetectsReplayedCorrelation()
        {
            var tracker = new SequenceTracker();
            uint actorId = 50;

            uint correlation = NetworkCorrelation.Compose(actorId, (ushort)10);
            ushort seq = NetworkCorrelation.ExtractRequestId(correlation);

            Assert.IsTrue(tracker.ValidateSequence(1, seq));
            Assert.IsFalse(tracker.ValidateSequence(1, seq)); // replayed
        }

        [Test]
        public void Integration_SequenceTracker_AcceptsIncrementingCorrelations()
        {
            var tracker = new SequenceTracker();
            ushort counter = 0;
            uint actorId = 50;

            for (int i = 0; i < 10; i++)
            {
                uint correlation = NetworkCorrelation.Next(actorId, ref counter);
                ushort seq = NetworkCorrelation.ExtractRequestId(correlation);
                Assert.IsTrue(tracker.ValidateSequence(1, seq), $"Sequence {seq} should be accepted (iteration {i})");
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // INTEGRATION — OWNERSHIP RESOLVER + SECURITY INTEGRATION
        // ════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void Integration_OwnershipResolver_SecurityIntegration_EndToEnd()
        {
            var original = SecurityIntegration.OwnershipResolver;
            UnityEngine.GameObject securityManagerGo = null;
            try
            {
                securityManagerGo = EnsureSecurityManagerForServerTests();
                var resolver = new NetworkOwnershipResolver();
                resolver.RegisterEntityOwner(1001, 7);
                SecurityIntegration.OwnershipResolver = resolver;

                // Register through SecurityIntegration's static API
                SecurityIntegration.RegisterEntityOwner(2001, 11);
                SecurityIntegration.RegisterEntityActor(3001, 2001);

                // ValidateOwnership goes through the injected resolver with a live security manager.
                Assert.IsTrue(SecurityIntegration.ValidateOwnership(7, 1001, "Test"));
                Assert.IsTrue(SecurityIntegration.ValidateOwnership(11, 2001, "Test"));
                Assert.IsFalse(SecurityIntegration.ValidateOwnership(8, 1001, "Test"));

                // Cleanup through SecurityIntegration
                SecurityIntegration.UnregisterEntity(2001);
            }
            finally
            {
                SecurityIntegration.OwnershipResolver = original;
                DestroySecurityManagerIfCreated(securityManagerGo);
            }
        }

        [Test]
        public void Integration_FullRequestValidation_NullManager_FailsClosedForServerLikeRequests()
        {
            // End-to-end: create correlation → build context → validate full request
            uint actorId = 42;
            ushort counter = 0;

            uint correlation = NetworkCorrelation.Next(actorId, ref counter);
            var ctx = NetworkRequestContext.Create(actorId, correlation);

            // With no NetworkSecurityManager, server-like requests fail closed by default.
            Assert.IsFalse(SecurityIntegration.ValidateModuleRequest(1, in ctx, "Core", "Move"));
            Assert.IsFalse(SecurityIntegration.ValidateModuleRequest(1, in ctx, "Stats", "ModifyStat"));
            Assert.IsFalse(SecurityIntegration.ValidateModuleRequest(1, in ctx, "Melee", "Attack"));
            Assert.IsFalse(SecurityIntegration.ValidateModuleRequest(1, in ctx, "Shooter", "Fire"));
        }

        [Test]
        public void Integration_FullRequestValidation_NullManager_CompatibilityToggle_PassesThrough()
        {
            uint actorId = 42;
            ushort counter = 0;
            uint correlation = NetworkCorrelation.Next(actorId, ref counter);
            var ctx = NetworkRequestContext.Create(actorId, correlation);

            SecurityIntegration.EnforceSecurityManagerForServerLikeRequests = false;
            try
            {
                Assert.IsTrue(SecurityIntegration.ValidateModuleRequest(1, in ctx, "Core", "Move"));
            }
            finally
            {
                SecurityIntegration.EnforceSecurityManagerForServerLikeRequests = true;
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // INTEGRATION — POSITION + INPUT STATE PIPELINE
        // ════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void Integration_InputToPosition_SequenceTracking()
        {
            // Verify the full input→position sequence correlation pipeline
            ushort inputSeq = 100;
            var input = NetworkInputState.Create(
                new UnityEngine.Vector2(1f, 0f), sequence: inputSeq, deltaTime: 0.016f);

            var posState = NetworkPositionState.Create(
                new UnityEngine.Vector3(5f, 0f, 0f),
                rotationY: 0f,
                verticalVel: 0f,
                lastInput: inputSeq,
                isGrounded: true,
                isJumping: false
            );

            // The position state references which input it last processed
            Assert.AreEqual(input.sequenceNumber, posState.lastProcessedInput);
        }

        [Test]
        public void Integration_PositionState_ReconciliationThresholdDecision()
        {
            // Simulate the reconciliation decision logic with pure math
            // Config defaults: threshold=0.1, maxDistance=3.0
            float reconciliationThreshold = 0.1f;
            float maxReconciliationDistance = 3.0f;

            var serverPos = new UnityEngine.Vector3(10f, 0f, 0f);
            var predictedClose = new UnityEngine.Vector3(10.05f, 0f, 0f); // within threshold
            var predictedDrift = new UnityEngine.Vector3(10.5f, 0f, 0f);  // above threshold, below max
            var predictedTeleport = new UnityEngine.Vector3(15f, 0f, 0f); // above max

            float errorClose = UnityEngine.Vector3.Distance(serverPos, predictedClose);
            float errorDrift = UnityEngine.Vector3.Distance(serverPos, predictedDrift);
            float errorTeleport = UnityEngine.Vector3.Distance(serverPos, predictedTeleport);

            // Close → no reconciliation needed
            Assert.IsFalse(errorClose > reconciliationThreshold, "Close position should not trigger reconciliation");

            // Drift → smooth reconciliation
            Assert.IsTrue(errorDrift > reconciliationThreshold, "Drifted position should trigger reconciliation");
            Assert.IsFalse(errorDrift > maxReconciliationDistance, "Drifted position should NOT trigger teleport");

            // Teleport → snap
            Assert.IsTrue(errorTeleport > reconciliationThreshold, "Teleport position should trigger reconciliation");
            Assert.IsTrue(errorTeleport > maxReconciliationDistance, "Teleport position should trigger snap");
        }

        [Test]
        public void Integration_ReconciliationVisualDecay_ExponentialShrink()
        {
            // Verify the exponential decay formula used in reconciliation visual smoothing
            float reconciliationSpeed = 15f;
            float deltaTime = 0.016f; // ~60fps

            float offset = 1.0f; // Start with 1 unit of visual offset

            // After one frame of decay
            float decayFactor = UnityEngine.Mathf.Exp(-reconciliationSpeed * deltaTime);
            offset *= decayFactor;

            Assert.Less(offset, 1.0f, "Offset should decrease after decay");
            Assert.Greater(offset, 0f, "Offset should remain positive");

            // After many frames, should approach zero
            for (int i = 0; i < 300; i++) // ~5 seconds at 60fps
            {
                offset *= decayFactor;
            }

            Assert.Less(offset, 0.001f, "Offset should approach zero after many frames");
        }
    }
}
