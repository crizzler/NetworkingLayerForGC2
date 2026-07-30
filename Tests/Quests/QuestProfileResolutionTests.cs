#if GC2_QUESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Quests;
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Quests.Tests
{
    public sealed class QuestProfileResolutionTests
    {
        private const BindingFlags InstancePrivate =
            BindingFlags.Instance | BindingFlags.NonPublic;

        private readonly List<UnityEngine.Object> m_Cleanup = new();
        private Quest[] m_OriginalRepositoryQuests;

        [SetUp]
        public void SetUp()
        {
            Quest[] quests = Settings.From<QuestsRepository>().Quests.Quests;
            m_OriginalRepositoryQuests = quests != null
                ? (Quest[])quests.Clone()
                : Array.Empty<Quest>();
        }

        [TearDown]
        public void TearDown()
        {
            SetRepositoryQuests(m_OriginalRepositoryQuests);

            for (int i = m_Cleanup.Count - 1; i >= 0; i--)
            {
                if (m_Cleanup[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(m_Cleanup[i]);
                }
            }

            m_Cleanup.Clear();
        }

        [Test]
        public async System.Threading.Tasks.Task
            ProcessQuestRequest_ProfiledQuestAbsentFromGlobalRepository_ActivatesQuest()
        {
            const string questId = "74547761-7d2b-4bc8-b4ae-031518093536";

            Quest quest = CreateQuest(questId);
            NetworkQuestProfile profile = CreateProfile(quest);
            NetworkQuestsController controller = CreateController(profile, out Journal journal);
            SetRepositoryQuests(Array.Empty<Quest>());

            var request = new NetworkQuestRequest
            {
                RequestId = 1,
                ActorNetworkId = 10,
                CorrelationId = 1001,
                TargetNetworkId = 10,
                ProfileHash = profile.ProfileHash,
                ShareMode = NetworkQuestShareMode.Global,
                ScopeId = string.Empty,
                Action = QuestActionType.ActivateQuest,
                QuestHash = quest.Id.Hash,
                QuestIdString = quest.Id.String,
                TaskId = -1
            };

            Assert.That(
                controller.IsNetworkRequestAllowed(request, out QuestRejectionReason authorization),
                Is.True);
            Assert.That(authorization, Is.EqualTo(QuestRejectionReason.None));

            NetworkQuestResponse response =
                await controller.ProcessQuestRequestAsync(request, request.ActorNetworkId);

            Assert.That(
                response.Authorized,
                Is.True,
                $"{response.RejectionReason}: {response.Error}");
            Assert.That(
                response.Applied,
                Is.True,
                $"{response.RejectionReason}: {response.Error}");
            Assert.That(response.RejectionReason, Is.EqualTo(QuestRejectionReason.None));
            Assert.That(journal.IsQuestActive(quest), Is.True);
        }

        [Test]
        public void ResolveQuestIdentity_ProfileBindingRequiresMatchingIdAndHash()
        {
            const string questId = "bcad54f4-25cc-452e-922c-bfdb26817b26";

            Quest quest = CreateQuest(questId);
            NetworkQuestProfile profile = CreateProfile(quest);
            NetworkQuestsController controller = CreateController(profile, out _);

            MethodInfo resolver = typeof(NetworkQuestsController).GetMethod(
                "ResolveQuestIdentity",
                InstancePrivate);

            Assert.That(resolver, Is.Not.Null);
            Assert.That(
                resolver.Invoke(controller, new object[] { quest.Id.String, quest.Id.Hash }),
                Is.SameAs(quest));
            Assert.That(
                resolver.Invoke(controller, new object[] { "different-quest-id", quest.Id.Hash }),
                Is.Null);
            Assert.That(
                resolver.Invoke(controller, new object[] { quest.Id.String, quest.Id.Hash + 1 }),
                Is.Null);
        }

        [Test]
        public void ControllerAwake_RegistersProfileQuestAfterRepositoryWasAlreadyCached()
        {
            const string questId = "4fd0dc1e-4e44-4896-8f76-80568dd27ff3";

            Quest quest = CreateQuest(questId);
            NetworkQuestProfile profile = CreateProfile(quest);
            QuestsList questList = Settings.From<QuestsRepository>().Quests;

            SetRepositoryQuests(Array.Empty<Quest>());
            Assert.That(questList.Get(quest.Id), Is.Null);

            CreateController(profile, out _);

            Assert.That(questList.Get(quest.Id), Is.SameAs(quest));
        }

        private Quest CreateQuest(string questId)
        {
            Quest quest = ScriptableObject.CreateInstance<Quest>();
            quest.name = "Profile-only Quest";
            SetPrivate(quest, "m_UniqueId", new UniqueID(questId));
            quest.Tasks.AddToRoot(new GameCreator.Runtime.Quests.Task());
            m_Cleanup.Add(quest);
            return quest;
        }

        private NetworkQuestProfile CreateProfile(Quest quest)
        {
            NetworkQuestProfile profile = ScriptableObject.CreateInstance<NetworkQuestProfile>();
            profile.name = "Profile Resolution Regression";
            SetPrivate(profile, "m_Quests", new[] { CreateBinding(quest) });
            m_Cleanup.Add(profile);
            return profile;
        }

        private NetworkQuestsController CreateController(
            NetworkQuestProfile profile,
            out Journal journal)
        {
            var gameObject = new GameObject("Quest Profile Resolution Test");
            m_Cleanup.Add(gameObject);

            journal = gameObject.AddComponent<Journal>();
            InvokePrivate(journal, "Awake");

            NetworkQuestsController controller =
                gameObject.AddComponent<NetworkQuestsController>();
            SetPrivate(controller, "m_Profile", profile);
            InvokePrivate(controller, "Awake");
            return controller;
        }

        private static NetworkQuestBinding CreateBinding(Quest quest)
        {
            object binding = default(NetworkQuestBinding);
            SetPrivate(binding, "m_Quest", quest);
            SetPrivate(binding, "m_ShareMode", NetworkQuestShareMode.Global);
            SetPrivate(binding, "m_AllowClientWrites", true);
            SetPrivate(binding, "m_AutoForwardJournalChanges", true);
            SetPrivate(binding, "m_AllowedActions", NetworkQuestActionMask.All);
            return (NetworkQuestBinding)binding;
        }

        private static void SetPrivate(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, InstancePrivate);
            Assert.That(field, Is.Not.Null, $"Missing field {target.GetType().Name}.{fieldName}");
            field.SetValue(target, value);
        }

        private static void InvokePrivate(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, InstancePrivate);
            Assert.That(method, Is.Not.Null, $"Missing method {target.GetType().Name}.{methodName}");
            method.Invoke(target, null);
        }

        private static void SetRepositoryQuests(Quest[] quests)
        {
            QuestsList questList = Settings.From<QuestsRepository>().Quests;
            SetPrivate(questList, "m_Quests", quests ?? Array.Empty<Quest>());
            SetPrivate(questList, "m_QuestsLut", null);
            SetPrivate(questList, "m_TasksLut", null);
        }
    }
}
#endif
