using System;
using System.Collections.Generic;
using System.Reflection;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using NUnit.Framework;
using UnityEngine;
using Gc2CategoryAttribute = GameCreator.Runtime.Common.CategoryAttribute;

namespace Arawn.GameCreator2.Networking.Tests
{
    public sealed class CoreVisualScriptingTests
    {
        private static readonly (Type Type, string Category)[] ExpectedEntries =
        {
            (typeof(InstructionNetworkCoreStartRagdoll),
                "Network/Core/Ragdoll/Start Ragdoll"),
            (typeof(InstructionNetworkCoreRecoverRagdoll),
                "Network/Core/Ragdoll/Recover Ragdoll"),
            (typeof(InstructionNetworkCoreAttachProp),
                "Network/Core/Props/Attach Prop"),
            (typeof(InstructionNetworkCoreDetachProp),
                "Network/Core/Props/Detach Prop"),
            (typeof(InstructionNetworkCoreDetachPropInstance),
                "Network/Core/Props/Detach Prop Instance"),
            (typeof(InstructionNetworkCoreDetachAllProps),
                "Network/Core/Props/Detach All Props"),
            (typeof(InstructionNetworkCoreSetInvincibility),
                "Network/Core/Combat/Set Invincibility"),
            (typeof(InstructionNetworkCoreDamagePoise),
                "Network/Core/Combat/Damage Poise"),
            (typeof(InstructionNetworkCoreResetPoise),
                "Network/Core/Combat/Reset Poise"),
            (typeof(InstructionNetworkCoreSetBusy),
                "Network/Core/Character/Set Busy Limbs"),
            (typeof(EventNetworkCoreRequestCompleted),
                "Network/Core/On Request Completed"),
            (typeof(ConditionNetworkCoreLastRequestApproved),
                "Network/Core/Last Request Approved"),
            (typeof(GetBoolNetworkCoreRequestHasResult),
                "Network/Core/Request/Has Result"),
            (typeof(GetBoolNetworkCoreRequestApproved),
                "Network/Core/Request/Approved"),
            (typeof(GetBoolNetworkCorePoiseIsBroken),
                "Network/Core/Request/Poise Is Broken"),
            (typeof(GetStringNetworkCoreRequestOperation),
                "Network/Core/Request/Operation"),
            (typeof(GetStringNetworkCoreRequestRejectReason),
                "Network/Core/Request/Reject Reason"),
            (typeof(GetDecimalNetworkCoreRequestCharacterId),
                "Network/Core/Request/Character ID"),
            (typeof(GetDecimalNetworkCorePropInstanceId),
                "Network/Core/Request/Prop Instance ID"),
            (typeof(GetDecimalNetworkCoreCurrentPoise),
                "Network/Core/Request/Current Poise"),
            (typeof(GetDecimalNetworkCoreApprovedInvincibilityDuration),
                "Network/Core/Request/Approved Invincibility Duration")
        };

        [SetUp]
        public void SetUp()
        {
            ResetContext();
        }

        [TearDown]
        public void TearDown()
        {
            ResetContext();
        }

        [Test]
        public void Entries_HaveStableGc2DiscoveryMetadata()
        {
            foreach ((Type type, string expectedCategory) in ExpectedEntries)
            {
                TitleAttribute title = type.GetCustomAttribute<TitleAttribute>(false);
                Gc2CategoryAttribute category =
                    type.GetCustomAttribute<Gc2CategoryAttribute>(false);

                Assert.That(type.IsSerializable, Is.True, type.FullName);
                Assert.That(title, Is.Not.Null, $"{type.FullName} needs a Title attribute.");
                Assert.That(title.Title, Is.Not.Empty, type.FullName);
                Assert.That(category, Is.Not.Null,
                    $"{type.FullName} needs a Category attribute.");
                Assert.That(category.ToString(), Is.EqualTo(expectedCategory), type.FullName);
            }
        }

        [Test]
        public void Instructions_DeriveFromSharedAuthoritativeRequestBase()
        {
            foreach ((Type type, _) in ExpectedEntries)
            {
                if (!typeof(Instruction).IsAssignableFrom(type)) continue;

                Assert.That(
                    typeof(TInstructionNetworkCoreRequest).IsAssignableFrom(type),
                    Is.True,
                    type.FullName);
            }

            FieldInfo character = FindInstanceField(
                typeof(TInstructionNetworkCoreRequest),
                "m_Character");
            FieldInfo waitForResponse = FindInstanceField(
                typeof(TInstructionNetworkCoreRequest),
                "m_WaitForResponse");

            Assert.That(character, Is.Not.Null);
            Assert.That(character.FieldType, Is.EqualTo(typeof(PropertyGetGameObject)));
            Assert.That(character.GetCustomAttribute<SerializeField>(), Is.Not.Null);
            Assert.That(waitForResponse, Is.Not.Null);
            Assert.That(waitForResponse.FieldType, Is.EqualTo(typeof(bool)));
            Assert.That(waitForResponse.GetCustomAttribute<SerializeField>(), Is.Not.Null);
        }

        [Test]
        public void DynamicInputs_UseGc2PropertyFields()
        {
            AssertFieldType<InstructionNetworkCoreStartRagdoll>(
                "m_Force", typeof(PropertyGetDirection));
            AssertFieldType<InstructionNetworkCoreRecoverRagdoll>(
                "m_Instant", typeof(PropertyGetBool));
            AssertFieldType<InstructionNetworkCoreAttachProp>(
                "m_PropId", typeof(PropertyGetString));
            AssertFieldType<InstructionNetworkCoreAttachProp>(
                "m_LocalPosition", typeof(PropertyGetPosition));
            AssertFieldType<InstructionNetworkCoreAttachProp>(
                "m_LocalRotation", typeof(PropertyGetRotation));
            AssertFieldType<InstructionNetworkCoreDetachPropInstance>(
                "m_PropInstanceId", typeof(PropertyGetInteger));
            AssertFieldType<InstructionNetworkCoreSetInvincibility>(
                "m_Duration", typeof(PropertyGetDecimal));
            AssertFieldType<InstructionNetworkCoreDamagePoise>(
                "m_Damage", typeof(PropertyGetDecimal));
            AssertFieldType<InstructionNetworkCoreSetBusy>(
                "m_SetBusy", typeof(PropertyGetBool));
            AssertFieldType<InstructionNetworkCoreSetBusy>(
                "m_Timeout", typeof(PropertyGetDecimal));
        }

        [Test]
        public void PropResponse_PublishesReusableResultContext()
        {
            const uint CharacterNetworkId = 42;
            var notifications = new List<NetworkCoreVisualScriptingResult>();
            Action<NetworkCoreVisualScriptingResult> listener = notifications.Add;
            NetworkCoreVisualScriptingContext.RequestCompleted += listener;

            try
            {
                NetworkCoreVisualScriptingContext.BeginRequest(
                    NetworkCoreVisualScriptingOperation.AttachProp,
                    CharacterNetworkId);

                Assert.That(NetworkCoreVisualScriptingContext.HasResult, Is.False);
                Assert.That(
                    NetworkCoreVisualScriptingContext.LastResult.CharacterNetworkId,
                    Is.EqualTo(CharacterNetworkId));

                NetworkCoreVisualScriptingContext.CompleteRequest(
                    NetworkCoreVisualScriptingOperation.AttachProp,
                    CharacterNetworkId,
                    new NetworkPropResponse
                    {
                        RequestId = 7,
                        CorrelationId = 700,
                        Approved = true,
                        RejectReason = PropRejectReason.None,
                        PropInstanceId = 19
                    });
            }
            finally
            {
                NetworkCoreVisualScriptingContext.RequestCompleted -= listener;
            }

            Assert.That(notifications, Has.Count.EqualTo(1));
            NetworkCoreVisualScriptingResult result = notifications[0];
            Assert.That(result.Operation,
                Is.EqualTo(NetworkCoreVisualScriptingOperation.AttachProp));
            Assert.That(result.CharacterNetworkId, Is.EqualTo(CharacterNetworkId));
            Assert.That(result.RequestId, Is.EqualTo(7));
            Assert.That(result.CorrelationId, Is.EqualTo(700));
            Assert.That(result.Approved, Is.True);
            Assert.That(result.RejectReason, Is.EqualTo(nameof(PropRejectReason.None)));
            Assert.That(result.PropInstanceId, Is.EqualTo(19));
        }

        [Test]
        public void PoiseResponse_IsAvailableThroughGc2Properties()
        {
            NetworkCoreVisualScriptingContext.BeginRequest(
                NetworkCoreVisualScriptingOperation.DamagePoise,
                101);
            NetworkCoreVisualScriptingContext.CompleteRequest(
                NetworkCoreVisualScriptingOperation.DamagePoise,
                101,
                new NetworkPoiseResponse
                {
                    Approved = false,
                    RejectReason = PoiseRejectReason.AlreadyBroken,
                    CurrentPoise = 2.5f,
                    IsBroken = true
                });

            Args args = Args.EMPTY;
            Assert.That(new GetBoolNetworkCoreRequestHasResult().Get(args), Is.True);
            Assert.That(new GetBoolNetworkCoreRequestApproved().Get(args), Is.False);
            Assert.That(new GetBoolNetworkCorePoiseIsBroken().Get(args), Is.True);
            Assert.That(new GetStringNetworkCoreRequestOperation().Get(args),
                Is.EqualTo(nameof(NetworkCoreVisualScriptingOperation.DamagePoise)));
            Assert.That(new GetStringNetworkCoreRequestRejectReason().Get(args),
                Is.EqualTo(nameof(PoiseRejectReason.AlreadyBroken)));
            Assert.That(new GetDecimalNetworkCoreRequestCharacterId().Get(args),
                Is.EqualTo(101d));
            Assert.That(new GetDecimalNetworkCoreCurrentPoise().Get(args),
                Is.EqualTo(2.5d));
        }

        [Test]
        public void OperationEnum_RetainsSerializedNumericValues()
        {
            Assert.That((int)NetworkCoreVisualScriptingOperation.None, Is.EqualTo(0));
            Assert.That((int)NetworkCoreVisualScriptingOperation.StartRagdoll, Is.EqualTo(1));
            Assert.That((int)NetworkCoreVisualScriptingOperation.RecoverRagdoll, Is.EqualTo(2));
            Assert.That((int)NetworkCoreVisualScriptingOperation.AttachProp, Is.EqualTo(3));
            Assert.That((int)NetworkCoreVisualScriptingOperation.DetachProp, Is.EqualTo(4));
            Assert.That((int)NetworkCoreVisualScriptingOperation.DetachPropInstance, Is.EqualTo(5));
            Assert.That((int)NetworkCoreVisualScriptingOperation.DetachAllProps, Is.EqualTo(6));
            Assert.That((int)NetworkCoreVisualScriptingOperation.SetInvincibility, Is.EqualTo(7));
            Assert.That((int)NetworkCoreVisualScriptingOperation.DamagePoise, Is.EqualTo(8));
            Assert.That((int)NetworkCoreVisualScriptingOperation.ResetPoise, Is.EqualTo(9));
            Assert.That((int)NetworkCoreVisualScriptingOperation.SetBusy, Is.EqualTo(10));
        }

        private static void AssertFieldType<T>(string name, Type expectedType)
        {
            FieldInfo field = FindInstanceField(typeof(T), name);
            Assert.That(field, Is.Not.Null, $"{typeof(T).FullName}.{name}");
            Assert.That(field.FieldType, Is.EqualTo(expectedType), field.Name);
            Assert.That(field.GetCustomAttribute<SerializeField>(), Is.Not.Null, field.Name);
        }

        private static FieldInfo FindInstanceField(Type type, string name)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public |
                    BindingFlags.DeclaredOnly);
                if (field != null) return field;
                type = type.BaseType;
            }

            return null;
        }

        private static void ResetContext()
        {
            MethodInfo reset = typeof(NetworkCoreVisualScriptingContext).GetMethod(
                "ResetStatics",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(reset, Is.Not.Null);
            reset.Invoke(null, null);
        }
    }
}
