using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Arawn.GameCreator2.Networking.Tests
{
    public sealed class NetworkActionPresentationInstructionTests
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
        public async Task BooleanRotation_IsAbsoluteAndIdempotentForLiveAndSnapshotState()
        {
            GameObject endpoint = Track(new GameObject("Presentation Endpoint"));
            GameObject visual = Track(new GameObject("Door Visual"));
            visual.transform.SetParent(endpoint.transform, false);

            var instruction = new InstructionNetworkApplyBooleanRotation();
            WriteField(instruction, "m_Target", GetGameObjectInstance.Create(visual));
            WriteField(instruction, "m_FalseRotation", new PropertyGetRotation(
                Quaternion.Euler(0f, 5f, 0f)));
            WriteField(instruction, "m_TrueRotation", new PropertyGetRotation(
                Quaternion.Euler(0f, 95f, 0f)));

            await Run(instruction, endpoint, NetworkActionPayload.FromBoolean(true), false);
            AssertRotation(visual.transform.localRotation, Quaternion.Euler(0f, 95f, 0f));

            // A presentation state is assigned, rather than accumulated. Reapplying the same
            // live revision or a late-join snapshot must therefore produce the exact same pose.
            visual.transform.localRotation = Quaternion.Euler(12f, 37f, 4f);
            await Run(instruction, endpoint, NetworkActionPayload.FromBoolean(true), true);
            AssertRotation(visual.transform.localRotation, Quaternion.Euler(0f, 95f, 0f));

            await Run(instruction, endpoint, NetworkActionPayload.FromBoolean(true), true);
            AssertRotation(visual.transform.localRotation, Quaternion.Euler(0f, 95f, 0f));

            await Run(instruction, endpoint, NetworkActionPayload.FromBoolean(false), false);
            AssertRotation(visual.transform.localRotation, Quaternion.Euler(0f, 5f, 0f));
        }

        [Test]
        public void PresentationFacade_ExposesCuratedStateApplicationInstructions()
        {
            Type[] instructionTypes =
            {
                typeof(InstructionNetworkApplyBooleanTransformState),
                typeof(InstructionNetworkApplyVectorPosition),
                typeof(InstructionNetworkApplyBooleanActiveState),
                typeof(InstructionNetworkApplyBooleanRendererState),
                typeof(InstructionNetworkApplyBooleanBehaviourState),
                typeof(InstructionNetworkApplyBooleanColliderState),
                typeof(InstructionNetworkApplyBooleanLayerState),
                typeof(InstructionNetworkApplyBooleanTagState),
                typeof(InstructionNetworkApplyVisualVariant),
                typeof(InstructionNetworkApplyAnimatorParameter),
                typeof(InstructionNetworkApplyAnimatorState),
                typeof(InstructionNetworkApplyBooleanCharacterControllable)
            };

            foreach (Type instructionType in instructionTypes)
            {
                Assert.That(typeof(Instruction).IsAssignableFrom(instructionType), Is.True,
                    $"{instructionType.Name} must remain a GC2 Instruction.");
                Assert.That(instructionType.IsAbstract, Is.False);
            }
        }

        [Test]
        public async Task OneConfirmedSnapshot_DerivesSeveralPresentationEffects()
        {
            GameObject endpoint = Track(new GameObject("Composite Endpoint"));
            endpoint.AddComponent<NetworkActionEndpoint>();
            GameObject presentation = Track(new GameObject("Composite Presentation"));
            presentation.transform.SetParent(endpoint.transform, false);
            MeshRenderer renderer = presentation.AddComponent<MeshRenderer>();
            BoxCollider collider = presentation.AddComponent<BoxCollider>();

            var transform = new InstructionNetworkApplyBooleanTransformState();
            WriteField(transform, "m_Target", GetGameObjectInstance.Create(presentation));
            WriteField(transform, "m_ApplyPosition", true);
            WriteField(transform, "m_TruePosition", new PropertyGetPosition(
                new Vector3(7f, 8f, 9f)));

            var render = new InstructionNetworkApplyBooleanRendererState();
            WriteField(render, "m_Target", GetGameObjectInstance.Create(presentation));

            var collision = new InstructionNetworkApplyBooleanColliderState();
            WriteField(collision, "m_Target", GetGameObjectInstance.Create(presentation));
            WriteField(collision, "m_ApplyIsTrigger", true);

            NetworkActionPayload payload = NetworkActionPayload.FromBoolean(true);

            // EditMode has no GC2 AsyncManager, so InstructionList intentionally stops after its
            // first item. Exercise the same endpoint context through one-item lists here; live
            // endpoint lists compose these instructions normally once the runtime manager exists.
            await Run(transform, endpoint, payload, true);
            await Run(render, endpoint, payload, true);
            await Run(collision, endpoint, payload, true);
            await Run(transform, endpoint, payload, true);
            await Run(render, endpoint, payload, true);
            await Run(collision, endpoint, payload, true);

            Assert.That(presentation.transform.localPosition,
                Is.EqualTo(new Vector3(7f, 8f, 9f)));
            Assert.That(renderer.enabled, Is.True);
            Assert.That(collider.enabled, Is.True);
            Assert.That(collider.isTrigger, Is.True);
        }

        [Test]
        public void UncontrollableDirectionalUnit_ClearsMovementAndPendingJump()
        {
            var unit = new UnitPlayerDirectionalNetwork();
            unit.InjectInput(new Vector2(0.6f, 0.8f), true);
            Assert.That(unit.RawInput, Is.Not.EqualTo(Vector2.zero));
            Assert.That(unit.IsJumpPressed, Is.True);

            unit.IsControllable = false;
            unit.InjectInput(Vector2.one, true);

            Assert.That(unit.RawInput, Is.EqualTo(Vector2.zero));
            Assert.That(unit.IsJumpPressed, Is.False,
                "Disabling control must not replay a queued jump after control returns.");
        }

        [Test]
        public async Task BooleanRotation_RequiresConfirmedBooleanActionContext()
        {
            GameObject visual = Track(new GameObject("Unchanged Visual"));
            Quaternion original = Quaternion.Euler(10f, 20f, 30f);
            visual.transform.localRotation = original;

            var instruction = new InstructionNetworkApplyBooleanRotation();
            WriteField(instruction, "m_Target", GetGameObjectInstance.Create(visual));
            WriteField(instruction, "m_TrueRotation", new PropertyGetRotation(
                Quaternion.Euler(0f, 90f, 0f)));

            await new InstructionList(instruction).Run(new Args(visual, visual));
            AssertRotation(visual.transform.localRotation, original);

            await Run(instruction, visual, NetworkActionPayload.FromNumber(1f), false);
            AssertRotation(visual.transform.localRotation, original);

            await RunResponse(
                instruction, visual,
                NetworkActionPayload.FromBoolean(true),
                authorized: true);
            AssertRotation(visual.transform.localRotation, original,
                "An approval response is not an applied-state context.");

            await RunResponse(
                instruction, visual,
                NetworkActionPayload.FromBoolean(true),
                authorized: false);
            AssertRotation(visual.transform.localRotation, original,
                "A rejected request must never mutate local presentation.");
        }

        [Test]
        public async Task BooleanTransform_AssignsAllEnabledChannelsAndReappliesSnapshot()
        {
            GameObject parent = Track(new GameObject("Transform Parent"));
            parent.transform.position = new Vector3(20f, 3f, -4f);
            GameObject visual = Track(new GameObject("Transform Visual"));
            visual.transform.SetParent(parent.transform, false);

            var instruction = new InstructionNetworkApplyBooleanTransformState();
            WriteField(instruction, "m_Target", GetGameObjectInstance.Create(visual));
            WriteField(instruction, "m_Space", NetworkActionTransformSpace.Local);
            WriteField(instruction, "m_ApplyPosition", true);
            WriteField(instruction, "m_FalsePosition", new PropertyGetPosition(
                new Vector3(-2f, 0f, 1f)));
            WriteField(instruction, "m_TruePosition", new PropertyGetPosition(
                new Vector3(4f, 5f, 6f)));
            WriteField(instruction, "m_ApplyRotation", true);
            WriteField(instruction, "m_FalseRotation", new PropertyGetRotation(
                Quaternion.Euler(0f, 10f, 0f)));
            WriteField(instruction, "m_TrueRotation", new PropertyGetRotation(
                Quaternion.Euler(0f, 110f, 0f)));
            WriteField(instruction, "m_ApplyScale", true);
            WriteField(instruction, "m_FalseScale", new PropertyGetScale(
                new Vector3(1f, 2f, 1f)));
            WriteField(instruction, "m_TrueScale", new PropertyGetScale(
                new Vector3(2f, 3f, 4f)));

            await Run(instruction, parent, NetworkActionPayload.FromBoolean(true), false);
            Assert.That(visual.transform.localPosition,
                Is.EqualTo(new Vector3(4f, 5f, 6f)));
            AssertRotation(visual.transform.localRotation, Quaternion.Euler(0f, 110f, 0f));
            Assert.That(visual.transform.localScale,
                Is.EqualTo(new Vector3(2f, 3f, 4f)));

            visual.transform.localPosition = Vector3.one * 99f;
            visual.transform.localRotation = Quaternion.Euler(42f, 42f, 42f);
            visual.transform.localScale = Vector3.one * 99f;
            await Run(instruction, parent, NetworkActionPayload.FromBoolean(true), true);
            await Run(instruction, parent, NetworkActionPayload.FromBoolean(true), true);

            Assert.That(visual.transform.localPosition,
                Is.EqualTo(new Vector3(4f, 5f, 6f)));
            AssertRotation(visual.transform.localRotation, Quaternion.Euler(0f, 110f, 0f));
            Assert.That(visual.transform.localScale,
                Is.EqualTo(new Vector3(2f, 3f, 4f)));

            await Run(instruction, parent, NetworkActionPayload.FromBoolean(false), false);
            Assert.That(visual.transform.localPosition,
                Is.EqualTo(new Vector3(-2f, 0f, 1f)));
            AssertRotation(visual.transform.localRotation, Quaternion.Euler(0f, 10f, 0f));
            Assert.That(visual.transform.localScale,
                Is.EqualTo(new Vector3(1f, 2f, 1f)));
        }

        [Test]
        public async Task VectorPosition_SupportsWorldSpaceAndIsAnAbsoluteSnapshot()
        {
            GameObject parent = Track(new GameObject("Vector Parent"));
            parent.transform.position = new Vector3(10f, 0f, 0f);
            GameObject visual = Track(new GameObject("Vector Visual"));
            visual.transform.SetParent(parent.transform, false);

            var instruction = new InstructionNetworkApplyVectorPosition();
            WriteField(instruction, "m_Target", GetGameObjectInstance.Create(visual));
            WriteField(instruction, "m_Space", NetworkActionTransformSpace.World);
            Vector3 expected = new(3f, 4f, 5f);

            await Run(instruction, parent, NetworkActionPayload.FromVector3(expected), false);
            Assert.That(visual.transform.position, Is.EqualTo(expected));

            visual.transform.position = Vector3.one * -20f;
            await Run(instruction, parent, NetworkActionPayload.FromVector3(expected), true);
            await Run(instruction, parent, NetworkActionPayload.FromVector3(expected), true);
            Assert.That(visual.transform.position, Is.EqualTo(expected));

            await Run(instruction, parent, NetworkActionPayload.FromBoolean(true), false);
            Assert.That(visual.transform.position, Is.EqualTo(expected),
                "A mismatched payload discriminator must be a safe no-op.");
        }

        [Test]
        public async Task TransformState_RefusesCharacterMovementRoot()
        {
            GameObject endpoint = Track(new GameObject("Transform Guard Endpoint"));
            GameObject characterRoot = Track(new GameObject("Character Movement Root"));
            characterRoot.AddComponent<Character>();
            Vector3 original = new(3f, 2f, 1f);
            characterRoot.transform.localPosition = original;

            var instruction = new InstructionNetworkApplyBooleanTransformState();
            WriteField(
                instruction, "m_Target",
                GetGameObjectInstance.Create(characterRoot));
            WriteField(instruction, "m_ApplyPosition", true);
            WriteField(
                instruction, "m_TruePosition",
                new PropertyGetPosition(new Vector3(20f, 30f, 40f)));

            LogAssert.Expect(LogType.Warning, new Regex(
                "Refused to change transform state on 'Character Movement Root'.*" +
                "presentation child",
                RegexOptions.Singleline));
            await Run(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true), false);

            Assert.That(characterRoot.transform.localPosition, Is.EqualTo(original));
        }

        [Test]
        public async Task ActiveState_CanDeactivatePresentationChildButNeverItsEndpoint()
        {
            GameObject endpointObject = Track(new GameObject("Protected Endpoint"));
            endpointObject.AddComponent<NetworkActionEndpoint>();
            GameObject visual = Track(new GameObject("Presentation Child"));
            visual.transform.SetParent(endpointObject.transform, false);

            var childInstruction = new InstructionNetworkApplyBooleanActiveState();
            WriteField(childInstruction, "m_Target", GetGameObjectInstance.Create(visual));

            await new InstructionList(childInstruction).Run(
                new Args(endpointObject, endpointObject));
            await Run(
                childInstruction, endpointObject,
                NetworkActionPayload.FromNumber(0f), false);
            Assert.That(visual.activeSelf, Is.True,
                "Missing or non-Boolean action context must be a safe no-op.");

            await Run(
                childInstruction, endpointObject,
                NetworkActionPayload.FromBoolean(false), false);
            Assert.That(visual.activeSelf, Is.False);

            await Run(
                childInstruction, endpointObject,
                NetworkActionPayload.FromBoolean(true), true);
            Assert.That(visual.activeSelf, Is.True);

            var endpointInstruction = new InstructionNetworkApplyBooleanActiveState();
            WriteField(
                endpointInstruction, "m_Target",
                GetGameObjectInstance.Create(endpointObject));
            LogAssert.Expect(LogType.Warning, new Regex(
                "Refused to deactivate 'Protected Endpoint'.*presentation state to a child",
                RegexOptions.Singleline));
            await Run(
                endpointInstruction, endpointObject,
                NetworkActionPayload.FromBoolean(false), false);
            Assert.That(endpointObject.activeSelf, Is.True);
        }

        [Test]
        public async Task RendererAndBehaviourState_ApplyAuthoredStateAndProtectNetworkBehaviour()
        {
            GameObject endpointObject = Track(new GameObject("Behaviour Endpoint"));
            NetworkActionEndpoint endpoint = endpointObject.AddComponent<NetworkActionEndpoint>();
            GameObject visual = Track(new GameObject("Renderer Visual"));
            visual.transform.SetParent(endpointObject.transform, false);
            MeshRenderer renderer = visual.AddComponent<MeshRenderer>();
            Light light = visual.AddComponent<Light>();

            var rendererInstruction = new InstructionNetworkApplyBooleanRendererState();
            WriteField(
                rendererInstruction, "m_Target", GetGameObjectInstance.Create(visual));
            await Run(
                rendererInstruction, endpointObject,
                NetworkActionPayload.FromBoolean(false), false);
            Assert.That(renderer.enabled, Is.False);
            await Run(
                rendererInstruction, endpointObject,
                NetworkActionPayload.FromBoolean(true), true);
            Assert.That(renderer.enabled, Is.True);

            var lightInstruction = new InstructionNetworkApplyBooleanBehaviourState();
            WriteField(lightInstruction, "m_Target", GetGameObjectInstance.Create(visual));
            WriteBehaviourType(lightInstruction, typeof(Light));
            await Run(
                lightInstruction, endpointObject,
                NetworkActionPayload.FromBoolean(false), false);
            Assert.That(light.enabled, Is.False);
            await Run(
                lightInstruction, endpointObject,
                NetworkActionPayload.FromBoolean(true), true);
            Assert.That(light.enabled, Is.True);

            var unsafeInstruction = new InstructionNetworkApplyBooleanBehaviourState();
            WriteField(
                unsafeInstruction, "m_Target",
                GetGameObjectInstance.Create(endpointObject));
            WriteBehaviourType(unsafeInstruction, typeof(NetworkActionEndpoint));
            LogAssert.Expect(LogType.Warning, new Regex(
                "Refused to disable 'Behaviour Endpoint'.*networking infrastructure",
                RegexOptions.Singleline));
            await Run(
                unsafeInstruction, endpointObject,
                NetworkActionPayload.FromBoolean(false), false);
            Assert.That(endpoint.enabled, Is.True);
        }

        [Test]
        public async Task ColliderState_IndependentlyAssignsEnabledAndTriggerState()
        {
            GameObject endpoint = Track(new GameObject("Collider Endpoint"));
            GameObject target = Track(new GameObject("Collider Presentation"));
            BoxCollider collider = target.AddComponent<BoxCollider>();
            GameObject target2D = Track(new GameObject("Collider 2D Presentation"));
            target2D.transform.SetParent(target.transform, false);
            BoxCollider2D collider2D = target2D.AddComponent<BoxCollider2D>();
            var instruction = new InstructionNetworkApplyBooleanColliderState();
            WriteField(instruction, "m_Target", GetGameObjectInstance.Create(target));
            WriteField(instruction, "m_IncludeChildren", true);
            WriteField(instruction, "m_ApplyEnabled", true);
            WriteField(instruction, "m_FalseEnabled", false);
            WriteField(instruction, "m_TrueEnabled", true);
            WriteField(instruction, "m_ApplyIsTrigger", true);
            WriteField(instruction, "m_FalseIsTrigger", false);
            WriteField(instruction, "m_TrueIsTrigger", true);

            await Run(instruction, endpoint, NetworkActionPayload.FromBoolean(true), false);
            Assert.That(collider.enabled, Is.True);
            Assert.That(collider.isTrigger, Is.True);
            Assert.That(collider2D.enabled, Is.True);
            Assert.That(collider2D.isTrigger, Is.True);

            await Run(instruction, endpoint, NetworkActionPayload.FromBoolean(false), true);
            await Run(instruction, endpoint, NetworkActionPayload.FromBoolean(false), true);
            Assert.That(collider.enabled, Is.False);
            Assert.That(collider.isTrigger, Is.False);
            Assert.That(collider2D.enabled, Is.False);
            Assert.That(collider2D.isTrigger, Is.False);
        }

        [Test]
        public async Task LayerAndTagState_UseOnlyAuthoredMappingsAndCanIncludeChildren()
        {
            GameObject endpoint = Track(new GameObject("Layer Tag Endpoint"));
            GameObject target = Track(new GameObject("Layer Tag Root"));
            GameObject child = Track(new GameObject("Layer Tag Child"));
            child.transform.SetParent(target.transform, false);

            var layerInstruction = new InstructionNetworkApplyBooleanLayerState();
            WriteField(layerInstruction, "m_Target", GetGameObjectInstance.Create(target));
            WriteField(layerInstruction, "m_FalseLayer", MakeLayer(0));
            WriteField(layerInstruction, "m_TrueLayer", MakeLayer(2));
            WriteField(layerInstruction, "m_IncludeChildren", true);

            var tagInstruction = new InstructionNetworkApplyBooleanTagState();
            WriteField(tagInstruction, "m_Target", GetGameObjectInstance.Create(target));
            WriteField(tagInstruction, "m_FalseTag", MakeTag("Untagged"));
            WriteField(tagInstruction, "m_TrueTag", MakeTag("Player"));
            WriteField(tagInstruction, "m_IncludeChildren", true);

            await Run(layerInstruction, endpoint, NetworkActionPayload.FromBoolean(true), false);
            await Run(tagInstruction, endpoint, NetworkActionPayload.FromBoolean(true), false);
            Assert.That(target.layer, Is.EqualTo(2));
            Assert.That(child.layer, Is.EqualTo(2));
            Assert.That(target.CompareTag("Player"), Is.True);
            Assert.That(child.CompareTag("Player"), Is.True);

            await Run(layerInstruction, endpoint, NetworkActionPayload.FromBoolean(false), true);
            await Run(tagInstruction, endpoint, NetworkActionPayload.FromBoolean(false), true);
            Assert.That(target.layer, Is.Zero);
            Assert.That(child.layer, Is.Zero);
            Assert.That(target.CompareTag("Untagged"), Is.True);
            Assert.That(child.CompareTag("Untagged"), Is.True);
        }

        [Test]
        public async Task VisualVariant_MapsConfirmedKeysToSharedMeshAndMaterials()
        {
            GameObject endpoint = Track(new GameObject("Visual Variant Endpoint"));
            GameObject target = Track(new GameObject("Visual Variant Target"));
            MeshFilter filter = target.AddComponent<MeshFilter>();
            MeshRenderer renderer = target.AddComponent<MeshRenderer>();
            Mesh initialMesh = Track(new Mesh { name = "Initial Mesh" });
            Mesh booleanMesh = Track(new Mesh { name = "Boolean Mesh" });
            Mesh numberMesh = Track(new Mesh { name = "Number Mesh" });
            Mesh stringMesh = Track(new Mesh { name = "String Mesh" });
            filter.sharedMesh = initialMesh;

            Shader shader = Shader.Find("Sprites/Default") ??
                            Shader.Find("Universal Render Pipeline/Lit") ??
                            Shader.Find("Standard") ??
                            Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null, "A built-in shader is required for this test.");
            Material material = Track(new Material(shader) { name = "Variant Material" });

            NetworkActionVisualVariant booleanVariant = MakeVariant(
                NetworkActionPayloadType.Boolean, true, 0, string.Empty,
                booleanMesh, new[] { material });
            NetworkActionVisualVariant numberVariant = MakeVariant(
                NetworkActionPayloadType.Number, false, 2, string.Empty,
                numberMesh, null);
            NetworkActionVisualVariant stringVariant = MakeVariant(
                NetworkActionPayloadType.String, false, 0, "damaged",
                stringMesh, null);

            var instruction = new InstructionNetworkApplyVisualVariant();
            WriteField(instruction, "m_Target", GetGameObjectInstance.Create(target));
            WriteField(instruction, "m_Variants", new[]
            {
                booleanVariant, numberVariant, stringVariant
            });

            await RunResponse(
                instruction, endpoint,
                NetworkActionPayload.FromBoolean(true),
                authorized: false);
            Assert.That(filter.sharedMesh, Is.SameAs(initialMesh),
                "A rejected request must not select local visual assets.");

            await Run(instruction, endpoint, NetworkActionPayload.FromBoolean(true), false);
            Assert.That(filter.sharedMesh, Is.SameAs(booleanMesh));
            Assert.That(renderer.sharedMaterials, Has.Length.EqualTo(1));
            Assert.That(renderer.sharedMaterials[0], Is.SameAs(material));

            filter.sharedMesh = initialMesh;
            await Run(instruction, endpoint, NetworkActionPayload.FromNumber(2.5f), false);
            Assert.That(filter.sharedMesh, Is.SameAs(initialMesh),
                "Non-integral or unmatched numeric values must not choose an authored variant.");

            await Run(instruction, endpoint, NetworkActionPayload.FromNumber(2f), true);
            await Run(instruction, endpoint, NetworkActionPayload.FromNumber(2f), true);
            Assert.That(filter.sharedMesh, Is.SameAs(numberMesh));

            await Run(
                instruction, endpoint,
                NetworkActionPayload.FromString("damaged"), false);
            Assert.That(filter.sharedMesh, Is.SameAs(stringMesh));
        }

        [Test]
        public async Task CharacterControllable_AppliesAbsoluteBooleanStateAndSnapshot()
        {
            GameObject endpoint = Track(new GameObject("Character State Endpoint"));
            GameObject characterObject = Track(new GameObject("Character Target"));
            Character character = characterObject.AddComponent<Character>();
            Assert.That(character.Player, Is.Not.Null);

            var instruction = new InstructionNetworkApplyBooleanCharacterControllable();
            WriteField(
                instruction, "m_Character",
                GetGameObjectInstance.Create(characterObject));

            await Run(instruction, endpoint, NetworkActionPayload.FromBoolean(false), false);
            Assert.That(character.Player.IsControllable, Is.False);

            await Run(instruction, endpoint, NetworkActionPayload.FromBoolean(true), true);
            await Run(instruction, endpoint, NetworkActionPayload.FromBoolean(true), true);
            Assert.That(character.Player.IsControllable, Is.True);

            await Run(instruction, endpoint, NetworkActionPayload.FromString("false"), false);
            Assert.That(character.Player.IsControllable, Is.True,
                "An untyped string must never change Character controllability.");
        }

        private static async Task Run(
            Instruction instruction,
            GameObject endpoint,
            NetworkActionPayload payload,
            bool snapshot)
        {
            await Run(new InstructionList(instruction), endpoint, payload, snapshot);
        }

        private static async Task Run(
            InstructionList instructions,
            GameObject endpoint,
            NetworkActionPayload payload,
            bool snapshot)
        {
            var broadcast = new NetworkActionBroadcast
            {
                ActionId = "tests.presentation",
                ActionHash = StableHashUtility.GetStableHash("tests.presentation"),
                Payload = payload,
                Revision = 4,
                IsSnapshot = snapshot
            };
            NetworkActionExecutionContext context =
                NetworkActionExecutionContext.FromBroadcast(
                    in broadcast, endpoint, endpoint);

            using (NetworkActionContext.Push(in context))
            {
                await instructions.Run(new Args(endpoint, endpoint));
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
                ActionId = "tests.presentation",
                ActionHash = StableHashUtility.GetStableHash("tests.presentation"),
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

        private static void AssertRotation(
            Quaternion actual,
            Quaternion expected,
            string message = null)
        {
            Assert.That(
                Quaternion.Angle(actual, expected),
                Is.LessThan(0.001f),
                message);
        }

        private static void WriteBehaviourType(
            InstructionNetworkApplyBooleanBehaviourState instruction,
            Type behaviourType)
        {
            TypeReferenceBehaviour reference = ReadField<TypeReferenceBehaviour>(
                instruction, "m_Behaviour");
            WriteField(reference, "m_TypeName", behaviourType.AssemblyQualifiedName);
        }

        private static LayerMaskValue MakeLayer(int value)
        {
            var layer = new LayerMaskValue();
            WriteField(layer, "m_Value", value);
            return layer;
        }

        private static TagValue MakeTag(string value)
        {
            var tag = new TagValue();
            WriteField(tag, "m_Value", value);
            return tag;
        }

        private static NetworkActionVisualVariant MakeVariant(
            NetworkActionPayloadType payloadType,
            bool booleanKey,
            int numberKey,
            string stringKey,
            Mesh mesh,
            Material[] materials)
        {
            var variant = new NetworkActionVisualVariant();
            WriteField(
                variant,
                "m_KeyType",
                payloadType switch
                {
                    NetworkActionPayloadType.Boolean =>
                        NetworkActionVisualVariantKeyType.Boolean,
                    NetworkActionPayloadType.Number =>
                        NetworkActionVisualVariantKeyType.Number,
                    NetworkActionPayloadType.String =>
                        NetworkActionVisualVariantKeyType.String,
                    _ => throw new ArgumentOutOfRangeException(nameof(payloadType))
                });
            WriteField(variant, "m_Boolean", booleanKey);
            WriteField(variant, "m_Number", numberKey);
            WriteField(variant, "m_String", stringKey);
            WriteField(variant, "m_ApplyMesh", mesh != null);
            WriteField(variant, "m_Mesh", mesh);
            WriteField(variant, "m_ApplyMaterials", materials != null);
            WriteField(variant, "m_Materials", materials ?? Array.Empty<Material>());
            return variant;
        }

        private static T ReadField<T>(object target, string fieldName)
        {
            Type type = target.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(fieldName, InstanceFields);
                if (field != null) return (T)field.GetValue(target);
                type = type.BaseType;
            }

            Assert.Fail($"{target.GetType().Name} does not declare field '{fieldName}'.");
            return default;
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
    }
}
