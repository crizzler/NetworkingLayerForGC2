#if GC2_QUESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Quests;

namespace Arawn.GameCreator2.Networking.Quests
{
    internal readonly struct NetworkQuestRepositoryRegistrationResult
    {
        public readonly bool Success;
        public readonly int ProfileQuestCount;
        public readonly int RepositoryCountBefore;
        public readonly int AddedCount;
        public readonly int ReplacedCount;
        public readonly int RepositoryCountAfter;
        public readonly string Error;

        public NetworkQuestRepositoryRegistrationResult(
            bool success,
            int profileQuestCount,
            int repositoryCountBefore,
            int addedCount,
            int replacedCount,
            int repositoryCountAfter,
            string error)
        {
            Success = success;
            ProfileQuestCount = profileQuestCount;
            RepositoryCountBefore = repositoryCountBefore;
            AddedCount = addedCount;
            ReplacedCount = replacedCount;
            RepositoryCountAfter = repositoryCountAfter;
            Error = error ?? string.Empty;
        }

        public override string ToString()
        {
            string summary =
                $"success={Success} profileQuests={ProfileQuestCount} " +
                $"before={RepositoryCountBefore} added={AddedCount} " +
                $"replaced={ReplacedCount} total={RepositoryCountAfter}";

            return string.IsNullOrEmpty(Error)
                ? summary
                : $"{summary} error=\"{Error}\"";
        }
    }

    /// <summary>
    /// Makes quests referenced by the network profile available to Game Creator's
    /// runtime quest UI. Game Creator discovers all Quest assets with AssetDatabase
    /// in the Editor, but that discovery path is unavailable in a player build.
    /// </summary>
    internal static class NetworkQuestRepositoryRegistration
    {
        private const BindingFlags INSTANCE_PRIVATE =
            BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly FieldInfo QuestsField =
            typeof(QuestsList).GetField("m_Quests", INSTANCE_PRIVATE);

        private static readonly FieldInfo QuestsLookupField =
            typeof(QuestsList).GetField("m_QuestsLut", INSTANCE_PRIVATE);

        private static readonly FieldInfo TasksLookupField =
            typeof(QuestsList).GetField("m_TasksLut", INSTANCE_PRIVATE);

        public static NetworkQuestRepositoryRegistrationResult EnsureRegistered(
            NetworkQuestProfile profile)
        {
            int profileQuestCount = CountProfileQuests(profile);

            try
            {
                if (QuestsField == null ||
                    QuestsLookupField == null ||
                    TasksLookupField == null)
                {
                    return Failure(
                        profileQuestCount,
                        "Game Creator QuestsList internals could not be resolved. " +
                        "The installed Quests version may be incompatible.");
                }

                QuestsRepository repository = Settings.From<QuestsRepository>();
                QuestsList questList = repository?.Quests;
                if (questList == null)
                {
                    return Failure(
                        profileQuestCount,
                        "Game Creator QuestsRepository is unavailable.");
                }

                Quest[] existing = QuestsField.GetValue(questList) as Quest[]
                    ?? Array.Empty<Quest>();
                var merged = new List<Quest>(existing.Length + profileQuestCount);

                for (int i = 0; i < existing.Length; i++)
                {
                    if (existing[i] != null) merged.Add(existing[i]);
                }

                int addedCount = 0;
                int replacedCount = 0;
                NetworkQuestBinding[] bindings =
                    profile != null ? profile.Quests : Array.Empty<NetworkQuestBinding>();

                for (int bindingIndex = 0; bindingIndex < bindings.Length; bindingIndex++)
                {
                    Quest profileQuest = bindings[bindingIndex].Quest;
                    if (profileQuest == null) continue;

                    string profileQuestId = profileQuest.Id.String;
                    bool found = false;
                    bool replaced = false;

                    for (int repositoryIndex = 0;
                         repositoryIndex < merged.Count;
                         repositoryIndex++)
                    {
                        Quest repositoryQuest = merged[repositoryIndex];
                        if (repositoryQuest == null) continue;
                        if (!string.Equals(
                                repositoryQuest.Id.String,
                                profileQuestId,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        found = true;
                        if (!ReferenceEquals(repositoryQuest, profileQuest))
                        {
                            merged[repositoryIndex] = profileQuest;
                            replaced = true;
                        }
                    }

                    if (!found)
                    {
                        merged.Add(profileQuest);
                        addedCount++;
                    }
                    else if (replaced)
                    {
                        replacedCount++;
                    }
                }

                bool removedNullEntries = merged.Count != existing.Length;
                if (addedCount > 0 || replacedCount > 0 || removedNullEntries)
                {
                    QuestsField.SetValue(questList, merged.ToArray());
                }

                // QuestsList builds these dictionaries lazily. Clear both even when
                // the array was already correct, in case an earlier lookup cached an
                // incomplete player-build repository.
                if (profileQuestCount > 0)
                {
                    QuestsLookupField.SetValue(questList, null);
                    TasksLookupField.SetValue(questList, null);
                }

                return new NetworkQuestRepositoryRegistrationResult(
                    true,
                    profileQuestCount,
                    existing.Length,
                    addedCount,
                    replacedCount,
                    merged.Count,
                    string.Empty);
            }
            catch (Exception exception)
            {
                return Failure(
                    profileQuestCount,
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        private static int CountProfileQuests(NetworkQuestProfile profile)
        {
            if (profile == null) return 0;

            int count = 0;
            NetworkQuestBinding[] bindings = profile.Quests;
            for (int i = 0; i < bindings.Length; i++)
            {
                if (bindings[i].Quest != null) count++;
            }

            return count;
        }

        private static NetworkQuestRepositoryRegistrationResult Failure(
            int profileQuestCount,
            string error)
        {
            return new NetworkQuestRepositoryRegistrationResult(
                false,
                profileQuestCount,
                0,
                0,
                0,
                0,
                error);
        }
    }
}
#endif
