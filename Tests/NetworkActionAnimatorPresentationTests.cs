using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;

namespace Arawn.GameCreator2.Networking.Tests
{
    public sealed class NetworkActionAnimatorPresentationTests
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly List<UnityEngine.Object> m_Cleanup = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = m_Cleanup.Count - 1; i >= 0; i--)
            {
                if (m_Cleanup[i] != null) UnityEngine.Object.DestroyImmediate(m_Cleanup[i]);
            }

            m_Cleanup.Clear();
        }

        [Test]
        public async Task AnimatorParameter_AppliesBooleanFloatAndRoundedIntegerAbsolutely()
        {
            AnimatorRig rig = CreateAnimatorRig("Parameter World Object");
            GameObject endpoint = Track(new GameObject("Parameter Endpoint"));

            var instruction = new InstructionNetworkApplyAnimatorParameter();
            WriteField(instruction, "m_Target", GetGameObjectInstance.Create(rig.GameObject));

            WriteField(instruction, "m_ParameterName", "Open");
            WriteField(
                instruction,
                "m_ParameterType",
                NetworkActionAnimatorParameterType.Boolean);
            WriteField(instruction, "m_FalseBoolean", true);
            WriteField(instruction, "m_TrueBoolean", false);

            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: false);
            Assert.That(rig.Animator.GetBool("Open"), Is.False,
                "Boolean payloads use the two authored absolute values, not a toggle.");

            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(false), snapshot: true);
            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(false), snapshot: true);
            Assert.That(rig.Animator.GetBool("Open"), Is.True,
                "Reapplying a snapshot must retain the same authored Boolean value.");

            WriteField(instruction, "m_ParameterName", "Amount");
            WriteField(
                instruction,
                "m_ParameterType",
                NetworkActionAnimatorParameterType.Float);
            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromNumber(2.75f), snapshot: false);
            Assert.That(rig.Animator.GetFloat("Amount"), Is.EqualTo(2.75f).Within(0.0001f));

            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromNumber(-1.25f), snapshot: true);
            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromNumber(-1.25f), snapshot: true);
            Assert.That(rig.Animator.GetFloat("Amount"), Is.EqualTo(-1.25f).Within(0.0001f));

            WriteField(instruction, "m_ParameterName", "Mode");
            WriteField(
                instruction,
                "m_ParameterType",
                NetworkActionAnimatorParameterType.Integer);
            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromNumber(2.6f), snapshot: false);
            Assert.That(rig.Animator.GetInteger("Mode"), Is.EqualTo(3));

            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromNumber(float.MaxValue), snapshot: true);
            Assert.That(rig.Animator.GetInteger("Mode"), Is.EqualTo(int.MaxValue),
                "Finite values outside the Int32 range must clamp before conversion.");

            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromNumber(-float.MaxValue), snapshot: true,
                revision: 5);
            Assert.That(rig.Animator.GetInteger("Mode"), Is.EqualTo(int.MinValue));
        }

        [Test]
        public async Task AnimatorParameter_RequiresConfirmedPresentationContextAndExactContract()
        {
            AnimatorRig rig = CreateAnimatorRig("Guarded Parameter Object");
            GameObject endpoint = Track(new GameObject("Guard Endpoint"));
            rig.Animator.SetFloat("Amount", 7f);

            var instruction = new InstructionNetworkApplyAnimatorParameter();
            WriteField(instruction, "m_Target", GetGameObjectInstance.Create(rig.GameObject));
            WriteField(instruction, "m_ParameterName", "Amount");
            WriteField(
                instruction,
                "m_ParameterType",
                NetworkActionAnimatorParameterType.Float);

            await new InstructionList(instruction).Run(new Args(endpoint, endpoint));
            await RunResponse(
                instruction, endpoint,
                NetworkActionPayload.FromNumber(12f), authorized: true);
            await RunResponse(
                instruction, endpoint,
                NetworkActionPayload.FromNumber(12f), authorized: false);
            await RunAuthorityCommit(
                instruction, endpoint,
                NetworkActionPayload.FromNumber(12f));
            Assert.That(rig.Animator.GetFloat("Amount"), Is.EqualTo(7f),
                "Approved, rejected, missing, and AuthorityCommit contexts are not replica application.");

            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: false);
            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromNumber(float.NaN), snapshot: false);
            Assert.That(rig.Animator.GetFloat("Amount"), Is.EqualTo(7f),
                "The payload discriminator and finite-number checks must fail closed.");

            WriteField(instruction, "m_ParameterName", "Missing");
            LogAssert.Expect(LogType.Warning, new Regex(
                "Animator parameter 'Missing' was not found",
                RegexOptions.Singleline));
            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromNumber(3f), snapshot: false);

            WriteField(instruction, "m_ParameterName", "Open");
            LogAssert.Expect(LogType.Warning, new Regex(
                "Animator parameter 'Open'.*is Bool, not Float",
                RegexOptions.Singleline));
            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromNumber(3f), snapshot: false);
            Assert.That(rig.Animator.GetBool("Open"), Is.False);
            Assert.That(rig.Animator.GetFloat("Amount"), Is.EqualTo(7f));
        }

        [Test]
        public async Task AnimatorPresenters_RefuseCharacterAndNetworkAnimatorTargets()
        {
            GameObject endpoint = Track(new GameObject("World Endpoint"));

            AnimatorRig characterRig = CreateAnimatorRig("Character Animator");
            characterRig.GameObject.AddComponent<Character>();
            var parameter = new InstructionNetworkApplyAnimatorParameter();
            WriteField(
                parameter, "m_Target",
                GetGameObjectInstance.Create(characterRig.GameObject));
            WriteField(parameter, "m_ParameterName", "Open");
            LogAssert.Expect(LogType.Warning, new Regex(
                "Refused to drive Animator 'Character Animator'.*GC2 Character",
                RegexOptions.Singleline));
            await RunApplied(
                parameter, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: false);
            Assert.That(characterRig.Animator.GetBool("Open"), Is.False);

            AnimatorRig networkRig = CreateAnimatorRig("Network Animator");
            networkRig.GameObject.AddComponent<UnitAnimimNetworkController>();
            var state = new InstructionNetworkApplyAnimatorState();
            WriteField(state, "m_Target", GetGameObjectInstance.Create(networkRig.GameObject));
            WriteField(state, "m_Variants", new[]
            {
                MakeStateVariant(
                    NetworkActionVisualVariantKeyType.Boolean,
                    booleanKey: true,
                    fullStatePath: "Base Layer.Open")
            });
            LogAssert.Expect(LogType.Warning, new Regex(
                "Refused to drive Animator 'Network Animator'.*network Animator",
                RegexOptions.Singleline));
            await RunApplied(
                state, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: false);
            AssertCurrentState(networkRig.Animator, networkRig.ClosedHash);

            AnimatorRig endpointRootRig = CreateAnimatorRig("Endpoint Root Animator");
            endpointRootRig.GameObject.AddComponent<NetworkActionEndpoint>();
            WriteField(
                parameter, "m_Target",
                GetGameObjectInstance.Create(endpointRootRig.GameObject));
            LogAssert.Expect(LogType.Warning, new Regex(
                "Refused to drive Animator 'Endpoint Root Animator'.*endpoint",
                RegexOptions.Singleline));
            await RunApplied(
                parameter, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: false);
            Assert.That(endpointRootRig.Animator.GetBool("Open"), Is.False,
                "Animators must live on a presentation child, not on the endpoint root.");
        }

        [Test]
        public async Task AnimatorState_MapsBooleanIntegralNumberAndOrdinalStringKeys()
        {
            AnimatorRig rig = CreateAnimatorRig("State World Object");
            GameObject endpoint = Track(new GameObject("State Endpoint"));
            var instruction = new InstructionNetworkApplyAnimatorState();
            WriteField(instruction, "m_Target", GetGameObjectInstance.Create(rig.GameObject));
            WriteField(instruction, "m_Variants", new[]
            {
                MakeStateVariant(
                    NetworkActionVisualVariantKeyType.Boolean,
                    booleanKey: true,
                    fullStatePath: "Base Layer.Open"),
                MakeStateVariant(
                    NetworkActionVisualVariantKeyType.Number,
                    numberKey: 2,
                    fullStatePath: "Base Layer.Number"),
                MakeStateVariant(
                    NetworkActionVisualVariantKeyType.String,
                    stringKey: "damaged",
                    fullStatePath: "Base Layer.Damaged")
            });

            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: false);
            FlushAnimator(rig.Animator);
            AssertCurrentState(rig.Animator, rig.OpenHash);

            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromNumber(2f), snapshot: false,
                revision: 5);
            FlushAnimator(rig.Animator);
            AssertCurrentState(rig.Animator, rig.NumberHash);

            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromString("damaged"), snapshot: false,
                revision: 6);
            FlushAnimator(rig.Animator);
            AssertCurrentState(rig.Animator, rig.DamagedHash);

            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromNumber(2.5f), snapshot: false,
                revision: 7);
            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromString("Damaged"), snapshot: false,
                revision: 8);
            FlushAnimator(rig.Animator);
            AssertCurrentState(rig.Animator, rig.DamagedHash,
                "Numeric state keys require integral values and string keys are ordinal/case-sensitive.");
        }

        [Test]
        public async Task AnimatorState_SnapshotConvergesWithoutRewindingOrBlockingRecovery()
        {
            AnimatorRig rig = CreateAnimatorRig("Snapshot Animator Object");
            GameObject endpoint = Track(new GameObject("Snapshot Endpoint"));
            var instruction = new InstructionNetworkApplyAnimatorState();
            WriteField(instruction, "m_Target", GetGameObjectInstance.Create(rig.GameObject));
            WriteField(instruction, "m_Variants", new[]
            {
                MakeStateVariant(
                    NetworkActionVisualVariantKeyType.Boolean,
                    booleanKey: true,
                    fullStatePath: "Base Layer.Open",
                    snapshotNormalizedTime: 0.35f,
                    restartIfAlreadyActive: true)
            });

            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: true,
                revision: 10, authorityEpoch: 3);
            FlushAnimator(rig.Animator);
            AnimatorStateInfo first = rig.Animator.GetCurrentAnimatorStateInfo(0);
            Assert.That(first.fullPathHash, Is.EqualTo(rig.OpenHash));
            Assert.That(NormalizedCycle(first), Is.EqualTo(0.35f).Within(0.02f));

            rig.Animator.Update(0.2f);
            float advancedTime = NormalizedCycle(rig.Animator.GetCurrentAnimatorStateInfo(0));
            Assert.That(advancedTime, Is.GreaterThan(0.35f));

            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: true,
                revision: 10, authorityEpoch: 3);
            FlushAnimator(rig.Animator);
            Assert.That(
                NormalizedCycle(rig.Animator.GetCurrentAnimatorStateInfo(0)),
                Is.EqualTo(advancedTime).Within(0.02f),
                "A duplicate snapshot must not restart an already applied animation.");

            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: true,
                revision: 11, authorityEpoch: 3);
            FlushAnimator(rig.Animator);
            Assert.That(
                NormalizedCycle(rig.Animator.GetCurrentAnimatorStateInfo(0)),
                Is.EqualTo(advancedTime).Within(0.02f),
                "A full snapshot with the same selected state must not rewind an already " +
                "converged animation merely because its revision is newer.");

            SetState(rig.Animator, rig.ClosedHash, 0f);
            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: true,
                revision: 11, authorityEpoch: 3);
            FlushAnimator(rig.Animator);
            AssertCurrentState(rig.Animator, rig.OpenHash);
            Assert.That(
                NormalizedCycle(rig.Animator.GetCurrentAnimatorStateInfo(0)),
                Is.EqualTo(0.35f).Within(0.02f),
                "The same snapshot revision must repair external drift or a pooled reset.");

            rig.Animator.Rebind();
            rig.Animator.Update(0f);
            AssertCurrentState(rig.Animator, rig.ClosedHash);
            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: true,
                revision: 11, authorityEpoch: 3);
            FlushAnimator(rig.Animator);
            AssertCurrentState(rig.Animator, rig.OpenHash,
                "Rebind must not leave a persistent state stuck at its controller default.");
        }

        [Test]
        public async Task AnimatorState_LiveRestartIsExplicitAndTransientOnly()
        {
            AnimatorRig rig = CreateAnimatorRig("Restart Animator Object");
            GameObject endpoint = Track(new GameObject("Restart Endpoint"));
            var instruction = new InstructionNetworkApplyAnimatorState();
            WriteField(instruction, "m_Target", GetGameObjectInstance.Create(rig.GameObject));

            NetworkActionAnimatorStateVariant noRestart = MakeStateVariant(
                NetworkActionVisualVariantKeyType.Boolean,
                booleanKey: true,
                fullStatePath: "Base Layer.Open",
                restartIfAlreadyActive: false);
            WriteField(instruction, "m_Variants", new[] { noRestart });
            SetState(rig.Animator, rig.OpenHash, 0.6f);
            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: false,
                effectKind: NetworkActionEffectKind.TransientEvent);
            FlushAnimator(rig.Animator);
            Assert.That(
                NormalizedCycle(rig.Animator.GetCurrentAnimatorStateInfo(0)),
                Is.EqualTo(0.6f).Within(0.02f));

            NetworkActionAnimatorStateVariant restart = MakeStateVariant(
                NetworkActionVisualVariantKeyType.Boolean,
                booleanKey: true,
                fullStatePath: "Base Layer.Open",
                restartIfAlreadyActive: true);
            WriteField(instruction, "m_Variants", new[] { restart });
            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: false,
                revision: 5,
                effectKind: NetworkActionEffectKind.TransientEvent);
            FlushAnimator(rig.Animator);
            Assert.That(
                NormalizedCycle(rig.Animator.GetCurrentAnimatorStateInfo(0)),
                Is.LessThan(0.02f),
                "Restart is opt-in and only meaningful for live transient effects.");

            SetState(rig.Animator, rig.OpenHash, 0.6f);
            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: false,
                revision: 6,
                effectKind: NetworkActionEffectKind.PersistentState);
            FlushAnimator(rig.Animator);
            Assert.That(
                NormalizedCycle(rig.Animator.GetCurrentAnimatorStateInfo(0)),
                Is.EqualTo(0.6f).Within(0.02f),
                "Persistent live state must not replay even when Restart is authored.");
        }

        [Test]
        public async Task AnimatorState_UsesCrossFadeAndRejectsInvalidAuthoredDestinations()
        {
            AnimatorRig rig = CreateAnimatorRig("Cross Fade Animator Object");
            GameObject endpoint = Track(new GameObject("Cross Fade Endpoint"));
            var instruction = new InstructionNetworkApplyAnimatorState();
            WriteField(instruction, "m_Target", GetGameObjectInstance.Create(rig.GameObject));
            WriteField(instruction, "m_Variants", new[]
            {
                MakeStateVariant(
                    NetworkActionVisualVariantKeyType.Boolean,
                    booleanKey: true,
                    fullStatePath: "Base Layer.Open",
                    liveCrossFadeDuration: 0.25f)
            });

            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: false,
                effectKind: NetworkActionEffectKind.TransientEvent);
            rig.Animator.Update(0.01f);
            Assert.That(rig.Animator.IsInTransition(0), Is.True);
            Assert.That(
                rig.Animator.GetNextAnimatorStateInfo(0).fullPathHash,
                Is.EqualTo(rig.OpenHash),
                "A positive live duration uses a cross-fade to the authored full state path.");

            SetState(rig.Animator, rig.OpenHash, 0.2f);
            rig.Animator.CrossFadeInFixedTime(rig.ClosedHash, 0.5f, 0, 0f);
            rig.Animator.Update(0.01f);
            Assert.That(rig.Animator.IsInTransition(0), Is.True);
            Assert.That(rig.Animator.GetCurrentAnimatorStateInfo(0).fullPathHash,
                Is.EqualTo(rig.OpenHash));
            Assert.That(rig.Animator.GetNextAnimatorStateInfo(0).fullPathHash,
                Is.EqualTo(rig.ClosedHash));
            WriteField(instruction, "m_Variants", new[]
            {
                MakeStateVariant(
                    NetworkActionVisualVariantKeyType.Boolean,
                    booleanKey: true,
                    fullStatePath: "Base Layer.Open")
            });
            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: false,
                revision: 4);
            FlushAnimator(rig.Animator);
            AssertCurrentState(rig.Animator, rig.OpenHash,
                "A transition away from the desired state is not convergence and must be " +
                "corrected back to the confirmed state.");

            SetState(rig.Animator, rig.ClosedHash, 0.4f);
            WriteField(instruction, "m_Variants", new[]
            {
                MakeStateVariant(
                    NetworkActionVisualVariantKeyType.Boolean,
                    booleanKey: true,
                    fullStatePath: "Base Layer.Does Not Exist")
            });
            LogAssert.Expect(LogType.Warning, new Regex(
                "Animator state 'Base Layer.Does Not Exist' was not found",
                RegexOptions.Singleline));
            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: false,
                revision: 5);
            FlushAnimator(rig.Animator);
            AssertCurrentState(rig.Animator, rig.ClosedHash);

            WriteField(instruction, "m_Variants", new[]
            {
                MakeStateVariant(
                    NetworkActionVisualVariantKeyType.Boolean,
                    booleanKey: true,
                    fullStatePath: "Base Layer.Open",
                    layer: 5)
            });
            LogAssert.Expect(LogType.Warning, new Regex(
                "Animator layer 5 is invalid",
                RegexOptions.Singleline));
            await RunApplied(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true), snapshot: false,
                revision: 6);
            FlushAnimator(rig.Animator);
            AssertCurrentState(rig.Animator, rig.ClosedHash);
        }

        private AnimatorRig CreateAnimatorRig(string name)
        {
            GameObject gameObject = Track(new GameObject(name));
            Animator animator = gameObject.AddComponent<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            AnimatorController controller = Track(new AnimatorController());
            controller.name = $"{name} Controller";
            controller.parameters = new[]
            {
                new AnimatorControllerParameter
                {
                    name = "Open",
                    type = AnimatorControllerParameterType.Bool
                },
                new AnimatorControllerParameter
                {
                    name = "Amount",
                    type = AnimatorControllerParameterType.Float
                },
                new AnimatorControllerParameter
                {
                    name = "Mode",
                    type = AnimatorControllerParameterType.Int
                }
            };

            AnimatorStateMachine stateMachine = Track(new AnimatorStateMachine());
            stateMachine.name = "Base Layer";
            AnimatorState closed = Track(stateMachine.AddState("Closed"));
            AnimatorState open = Track(stateMachine.AddState("Open"));
            AnimatorState number = Track(stateMachine.AddState("Number"));
            AnimatorState damaged = Track(stateMachine.AddState("Damaged"));
            closed.motion = CreateOneSecondClip("Closed Clip");
            open.motion = CreateOneSecondClip("Open Clip");
            number.motion = CreateOneSecondClip("Number Clip");
            damaged.motion = CreateOneSecondClip("Damaged Clip");
            stateMachine.defaultState = closed;

            controller.layers = new[]
            {
                new AnimatorControllerLayer
                {
                    name = "Base Layer",
                    defaultWeight = 1f,
                    stateMachine = stateMachine
                }
            };

            animator.runtimeAnimatorController = controller;
            animator.Rebind();
            animator.Update(0f);

            var rig = new AnimatorRig(
                gameObject,
                animator,
                Animator.StringToHash("Base Layer.Closed"),
                Animator.StringToHash("Base Layer.Open"),
                Animator.StringToHash("Base Layer.Number"),
                Animator.StringToHash("Base Layer.Damaged"));
            Assert.That(animator.HasState(0, rig.ClosedHash), Is.True,
                "The in-memory AnimatorController test fixture failed to initialize.");
            AssertCurrentState(animator, rig.ClosedHash);
            return rig;
        }

        private AnimationClip CreateOneSecondClip(string name)
        {
            AnimationClip clip = Track(new AnimationClip { name = name });
            clip.wrapMode = WrapMode.Loop;
            clip.SetCurve(
                string.Empty,
                typeof(Transform),
                "localPosition.x",
                AnimationCurve.Linear(0f, 0f, 1f, 0f));
            return clip;
        }

        private static NetworkActionAnimatorStateVariant MakeStateVariant(
            NetworkActionVisualVariantKeyType keyType,
            bool booleanKey = false,
            int numberKey = 0,
            string stringKey = "",
            string fullStatePath = "",
            int layer = 0,
            float liveCrossFadeDuration = 0f,
            float snapshotNormalizedTime = 0f,
            bool restartIfAlreadyActive = false)
        {
            var variant = new NetworkActionAnimatorStateVariant();
            WriteField(variant, "m_KeyType", keyType);
            WriteField(variant, "m_Boolean", booleanKey);
            WriteField(variant, "m_Number", numberKey);
            WriteField(variant, "m_String", stringKey);
            WriteField(variant, "m_FullStatePath", fullStatePath);
            WriteField(variant, "m_Layer", layer);
            WriteField(variant, "m_LiveCrossFadeDuration", liveCrossFadeDuration);
            WriteField(variant, "m_SnapshotNormalizedTime", snapshotNormalizedTime);
            WriteField(variant, "m_RestartIfAlreadyActive", restartIfAlreadyActive);
            return variant;
        }

        private static async Task RunApplied(
            Instruction instruction,
            GameObject endpoint,
            NetworkActionPayload payload,
            bool snapshot,
            uint revision = 4,
            uint authorityEpoch = 2,
            NetworkActionEffectKind effectKind = NetworkActionEffectKind.PersistentState)
        {
            var broadcast = new NetworkActionBroadcast
            {
                ActionId = "tests.animator.presentation",
                ActionHash = StableHashUtility.GetStableHash("tests.animator.presentation"),
                Payload = payload,
                EffectKind = effectKind,
                Revision = revision,
                AuthorityEpoch = authorityEpoch,
                IsSnapshot = snapshot
            };
            NetworkActionExecutionContext context =
                NetworkActionExecutionContext.FromBroadcast(
                    in broadcast, endpoint, endpoint);
            using (NetworkActionContext.Push(in context))
            {
                await new InstructionList(instruction).Run(new Args(endpoint, endpoint));
            }
        }

        private static async Task RunAuthorityCommit(
            Instruction instruction,
            GameObject endpoint,
            NetworkActionPayload payload)
        {
            var broadcast = new NetworkActionBroadcast
            {
                ActionId = "tests.animator.presentation",
                ActionHash = StableHashUtility.GetStableHash("tests.animator.presentation"),
                Payload = payload,
                EffectKind = NetworkActionEffectKind.PersistentState,
                Revision = 4
            };
            NetworkActionExecutionContext context =
                NetworkActionExecutionContext.FromBroadcast(
                    in broadcast, endpoint, endpoint, authorityCommit: true);
            using (NetworkActionContext.Push(in context))
            {
                await new InstructionList(instruction).Run(new Args(endpoint, endpoint));
            }
        }

        private static async Task RunResponse(
            Instruction instruction,
            GameObject endpoint,
            NetworkActionPayload payload,
            bool authorized)
        {
            var response = new NetworkActionResponse
            {
                ActionId = "tests.animator.presentation",
                ActionHash = StableHashUtility.GetStableHash("tests.animator.presentation"),
                Authorized = authorized,
                CanonicalPayload = payload,
                RejectReason = authorized
                    ? NetworkActionRejectReason.None
                    : NetworkActionRejectReason.HandlerRejected
            };
            NetworkActionExecutionContext context =
                NetworkActionExecutionContext.FromResponse(
                    in response, endpoint, endpoint);
            using (NetworkActionContext.Push(in context))
            {
                await new InstructionList(instruction).Run(new Args(endpoint, endpoint));
            }
        }

        private static void SetState(Animator animator, int stateHash, float normalizedTime)
        {
            animator.Play(stateHash, 0, normalizedTime);
            animator.Update(0f);
        }

        private static void FlushAnimator(Animator animator) => animator.Update(0f);

        private static float NormalizedCycle(AnimatorStateInfo state) =>
            state.normalizedTime - Mathf.Floor(state.normalizedTime);

        private static void AssertCurrentState(
            Animator animator,
            int expectedHash,
            string message = null)
        {
            Assert.That(
                animator.GetCurrentAnimatorStateInfo(0).fullPathHash,
                Is.EqualTo(expectedHash),
                message);
        }

        private static void WriteField(object target, string fieldName, object value)
        {
            Type type = target.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(fieldName, InstanceFields);
                if (field != null)
                {
                    field.SetValue(target, value);
                    return;
                }

                type = type.BaseType;
            }

            Assert.Fail($"{target.GetType().Name} does not declare field '{fieldName}'.");
        }

        private T Track<T>(T value) where T : UnityEngine.Object
        {
            m_Cleanup.Add(value);
            return value;
        }

        private readonly struct AnimatorRig
        {
            public readonly GameObject GameObject;
            public readonly Animator Animator;
            public readonly int ClosedHash;
            public readonly int OpenHash;
            public readonly int NumberHash;
            public readonly int DamagedHash;

            public AnimatorRig(
                GameObject gameObject,
                Animator animator,
                int closedHash,
                int openHash,
                int numberHash,
                int damagedHash)
            {
                GameObject = gameObject;
                Animator = animator;
                ClosedHash = closedHash;
                OpenHash = openHash;
                NumberHash = numberHash;
                DamagedHash = damagedHash;
            }
        }
    }
}
