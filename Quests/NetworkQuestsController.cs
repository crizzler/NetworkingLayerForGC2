#if GC2_QUESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Quests;

namespace Arawn.GameCreator2.Networking.Quests
{
    [RequireComponent(typeof(Journal))]
    [AddComponentMenu("Game Creator/Network/Quests/Network Quests Controller")]
    [DefaultExecutionOrder(ApplicationManager.EXECUTION_ORDER_DEFAULT + 5)]
    public class NetworkQuestsController : MonoBehaviour
    {
        private const int TASK_INVALID = -1;

        private static readonly FieldInfo TaskKeyQuestHashField =
            typeof(TaskKey).GetField("m_QuestHash", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly MethodInfo JournalOnRememberMethod =
            typeof(Journal).GetMethod("OnRemember", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo JournalEventQuestChangeField =
            typeof(Journal).GetField("EventQuestChange", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo JournalEventTaskChangeField =
            typeof(Journal).GetField("EventTaskChange", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo JournalEventTaskValueChangeField =
            typeof(Journal).GetField("EventTaskValueChange", BindingFlags.Instance | BindingFlags.NonPublic);

        [Serializable]
        private struct PendingQuestRequest
        {
            public NetworkQuestRequest Request;
            public float SentTime;
        }

        [Header("Network Settings")]
        [SerializeField] private bool m_OptimisticUpdates;

        [Header("Sync Settings")]
        [SerializeField] private float m_FullSyncInterval = 5f;

        [Header("Validation")]
        [SerializeField] private bool m_LogRejections;

        [Header("Debug")]
        [SerializeField] private bool m_LogAllChanges;

        public event Action<NetworkQuestRequest> OnQuestRequested;
        public event Action<NetworkQuestBroadcast> OnQuestApplied;
        public event Action<QuestRejectionReason, string> OnQuestRejected;

        private Journal m_Journal;
        private NetworkCharacter m_NetworkCharacter;

        private bool m_IsServer;
        private bool m_IsLocalClient;
        private bool m_IsRemoteClient;

        private ushort m_NextRequestId = 1;
        private ushort m_LastIssuedRequestId = 1;

        private bool m_IsRegistered;
        private uint m_RegisteredNetworkId;

        private float m_LastFullSync;

        private readonly Dictionary<ulong, PendingQuestRequest> m_PendingRequests = new(16);
        private readonly Dictionary<uint, float> m_RecentlyAppliedCorrelations = new(16);
        private readonly Queue<NetworkQuestBroadcast> m_BroadcastQueue = new();
        private bool m_ProcessingBroadcastQueue;
        private bool m_SuppressInterception;

        public uint NetworkId => m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;

        public bool IsServer => m_IsServer;
        public bool IsLocalClient => m_IsLocalClient;
        public bool IsRemoteClient => m_IsRemoteClient;
        internal bool IsApplyingAuthoritativeChange => m_SuppressInterception;

        private void Awake()
        {
            m_Journal = GetComponent<Journal>();
            m_NetworkCharacter = GetComponent<NetworkCharacter>();
        }

        private void OnEnable()
        {
            if (m_Journal == null) return;

            m_Journal.EventQuestActivate += OnLocalQuestActivate;
            m_Journal.EventQuestDeactivate += OnLocalQuestDeactivate;
            m_Journal.EventTaskActivate += OnLocalTaskActivate;
            m_Journal.EventTaskDeactivate += OnLocalTaskDeactivate;
            m_Journal.EventTaskComplete += OnLocalTaskComplete;
            m_Journal.EventTaskAbandon += OnLocalTaskAbandon;
            m_Journal.EventTaskFail += OnLocalTaskFail;
            m_Journal.EventTaskValueChange += OnLocalTaskValueChange;
            m_Journal.EventQuestTrack += OnLocalQuestTrack;
            m_Journal.EventQuestUntrack += OnLocalQuestUntrack;
        }

        private void OnDisable()
        {
            if (m_Journal != null)
            {
                m_Journal.EventQuestActivate -= OnLocalQuestActivate;
                m_Journal.EventQuestDeactivate -= OnLocalQuestDeactivate;
                m_Journal.EventTaskActivate -= OnLocalTaskActivate;
                m_Journal.EventTaskDeactivate -= OnLocalTaskDeactivate;
                m_Journal.EventTaskComplete -= OnLocalTaskComplete;
                m_Journal.EventTaskAbandon -= OnLocalTaskAbandon;
                m_Journal.EventTaskFail -= OnLocalTaskFail;
                m_Journal.EventTaskValueChange -= OnLocalTaskValueChange;
                m_Journal.EventQuestTrack -= OnLocalQuestTrack;
                m_Journal.EventQuestUntrack -= OnLocalQuestUntrack;
            }

            UnregisterFromManager();
        }

        private void Update()
        {
            EnsureRegisteredWithManager();
            CleanupPendingRequests();

            if (!m_IsServer) return;

            float currentTime = Time.time;
            if (m_FullSyncInterval > 0f && currentTime - m_LastFullSync >= m_FullSyncInterval)
            {
                NetworkQuestsManager.Instance?.BroadcastFullSnapshot(CaptureFullSnapshot());
                m_LastFullSync = currentTime;
            }
        }

        public void Initialize(bool isServer, bool isLocalClient)
        {
            m_IsServer = isServer;
            m_IsLocalClient = isLocalClient;
            m_IsRemoteClient = !isServer && !isLocalClient;

            EnsureRegisteredWithManager();

            if (m_LogAllChanges)
            {
                string role = m_IsServer ? "Server" : (m_IsLocalClient ? "LocalClient" : "RemoteClient");
                Debug.Log($"[NetworkQuestsController] {gameObject.name} initialized as {role}");
            }
        }

        public void RequestActivateQuest(Quest quest) => RequestQuestAction(QuestActionType.ActivateQuest, quest);
        public void RequestDeactivateQuest(Quest quest) => RequestQuestAction(QuestActionType.DeactivateQuest, quest);
        public void RequestActivateTask(Quest quest, int taskId) => RequestQuestAction(QuestActionType.ActivateTask, quest, taskId);
        public void RequestDeactivateTask(Quest quest, int taskId) => RequestQuestAction(QuestActionType.DeactivateTask, quest, taskId);
        public void RequestCompleteTask(Quest quest, int taskId) => RequestQuestAction(QuestActionType.CompleteTask, quest, taskId);
        public void RequestAbandonTask(Quest quest, int taskId) => RequestQuestAction(QuestActionType.AbandonTask, quest, taskId);
        public void RequestFailTask(Quest quest, int taskId) => RequestQuestAction(QuestActionType.FailTask, quest, taskId);
        public void RequestSetTaskValue(Quest quest, int taskId, double value) => RequestQuestAction(QuestActionType.SetTaskValue, quest, taskId, value);
        public void RequestTrackQuest(Quest quest) => RequestQuestAction(QuestActionType.TrackQuest, quest);
        public void RequestUntrackQuest(Quest quest) => RequestQuestAction(QuestActionType.UntrackQuest, quest);
        public void RequestUntrackAll() => RequestQuestAction(QuestActionType.UntrackAll, null, TASK_INVALID, 0d);

        public void RequestQuestAction(QuestActionType action, Quest quest, int taskId = TASK_INVALID, double value = 0d)
        {
            if (m_IsRemoteClient)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning("[NetworkQuestsController] Cannot request quest changes from a remote proxy");
                }

                return;
            }

            uint networkId = NetworkId;
            if (networkId == 0)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning("[NetworkQuestsController] Missing NetworkId; cannot send quest request");
                }

                OnQuestRejected?.Invoke(QuestRejectionReason.TargetNotFound, "Missing NetworkId");
                return;
            }

            if (RequiresQuest(action) && quest == null)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning($"[NetworkQuestsController] Action {action} requires a quest reference");
                }

                OnQuestRejected?.Invoke(QuestRejectionReason.QuestNotFound, "Action requires quest reference");
                return;
            }

            var request = new NetworkQuestRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = networkId,
                CorrelationId = NetworkCorrelation.Compose(networkId, m_LastIssuedRequestId),
                TargetNetworkId = networkId,
                Action = action,
                QuestHash = quest != null ? quest.Id.Hash : 0,
                QuestIdString = quest != null ? quest.Id.String : string.Empty,
                TaskId = taskId,
                Value = value
            };

            NetworkQuestsManager manager = NetworkQuestsManager.Instance;
            if (!m_IsServer && manager == null)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning("[NetworkQuestsController] NetworkQuestsManager instance not found");
                }

                OnQuestRejected?.Invoke(QuestRejectionReason.TargetNotFound, "NetworkQuestsManager missing");
                return;
            }

            m_PendingRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingQuestRequest
            {
                Request = request,
                SentTime = Time.time
            };

            OnQuestRequested?.Invoke(request);

            if (m_IsServer)
            {
                _ = ProcessLocalServerRequestAsync(request);
            }
            else
            {
                if (m_OptimisticUpdates)
                {
                    _ = ApplyAuthoritativeActionAsync(action, quest, taskId, value);
                }

                manager.SendQuestRequest(request);
            }
        }

        private async System.Threading.Tasks.Task ProcessLocalServerRequestAsync(NetworkQuestRequest request)
        {
            NetworkQuestResponse response = await ProcessQuestRequestAsync(request, request.ActorNetworkId);
            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            ReceiveQuestResponse(response);
        }

        public async System.Threading.Tasks.Task<NetworkQuestResponse> ProcessQuestRequestAsync(NetworkQuestRequest request, uint senderClientId)
        {
            if (!Enum.IsDefined(typeof(QuestActionType), request.Action))
            {
                return CreateRejectedResponse(request, QuestRejectionReason.InvalidAction, "Unknown quest action");
            }

            QuestRejectionReason rejection = QuestRejectionReason.None;
            Quest quest = null;

            if (!TryResolveQuestRequest(request, out quest, out rejection))
            {
                return CreateRejectedResponse(request, rejection, "Quest resolution failed");
            }

            if (!ValidateTaskRequest(request, quest, out rejection))
            {
                return CreateRejectedResponse(request, rejection, "Task resolution failed");
            }

            bool applied;
            try
            {
                applied = await ApplyAuthoritativeActionAsync(request.Action, quest, request.TaskId, request.Value);
            }
            catch (Exception exception)
            {
                return CreateRejectedResponse(request, QuestRejectionReason.Exception, exception.Message);
            }

            if (!applied)
            {
                return CreateRejectedResponse(request, QuestRejectionReason.InvalidState, "Quest action rejected by runtime state");
            }

            NetworkQuestResponse response = BuildSuccessResponse(request, quest, request.TaskId);

            if (m_IsServer)
            {
                NetworkQuestBroadcast broadcast = BuildBroadcast(request, quest, request.TaskId, request.ActorNetworkId);
                NetworkQuestsManager.Instance?.BroadcastQuestChange(broadcast);
            }

            if (m_LogAllChanges)
            {
                Debug.Log($"[NetworkQuestsController] Applied {request.Action} for quest='{request.QuestIdString}' task={request.TaskId} sender={senderClientId}");
            }

            return response;
        }

        public void ReceiveQuestResponse(NetworkQuestResponse response)
        {
            ulong key = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (!m_PendingRequests.Remove(key))
            {
                return;
            }

            if (!response.Authorized || !response.Applied)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning($"[NetworkQuestsController] Quest request rejected: {response.RejectionReason} ({response.Error})");
                }

                OnQuestRejected?.Invoke(response.RejectionReason, response.Error);
                return;
            }

            if (!m_IsServer)
            {
                m_RecentlyAppliedCorrelations[response.CorrelationId] = Time.time;
                if (!m_OptimisticUpdates)
                {
                    _ = ApplyResponseStateAsync(response);
                }
            }
        }

        public void ReceiveQuestChangeBroadcast(NetworkQuestBroadcast broadcast)
        {
            if (broadcast.NetworkId != NetworkId) return;

            if (broadcast.CorrelationId != 0 && m_RecentlyAppliedCorrelations.Remove(broadcast.CorrelationId))
            {
                return;
            }

            m_BroadcastQueue.Enqueue(broadcast);
            if (!m_ProcessingBroadcastQueue)
            {
                _ = ProcessBroadcastQueueAsync();
            }
        }

        public NetworkQuestsSnapshot CaptureFullSnapshot()
        {
            List<NetworkQuestSnapshotEntry> questEntries = new List<NetworkQuestSnapshotEntry>(m_Journal.QuestEntries.Count);
            foreach (KeyValuePair<IdString, QuestEntry> pair in m_Journal.QuestEntries)
            {
                questEntries.Add(new NetworkQuestSnapshotEntry
                {
                    QuestHash = pair.Key.Hash,
                    QuestIdString = pair.Key.String,
                    State = (byte)pair.Value.State,
                    IsTracking = pair.Value.IsTracking
                });
            }

            Dictionary<int, string> questIdLookup = BuildQuestIdLookup();
            List<NetworkTaskSnapshotEntry> taskEntries = new List<NetworkTaskSnapshotEntry>(m_Journal.TaskEntries.Count);
            foreach (KeyValuePair<TaskKey, TaskEntry> pair in m_Journal.TaskEntries)
            {
                int questHash = GetTaskKeyQuestHash(pair.Key);
                if (questHash == 0) continue;

                taskEntries.Add(new NetworkTaskSnapshotEntry
                {
                    QuestHash = questHash,
                    QuestIdString = questIdLookup.TryGetValue(questHash, out string questIdString) ? questIdString : string.Empty,
                    TaskId = pair.Key.TaskId,
                    State = (byte)pair.Value.State,
                    Value = pair.Value.Value
                });
            }

            return new NetworkQuestsSnapshot
            {
                NetworkId = NetworkId,
                ServerTime = Time.time,
                QuestEntries = questEntries.ToArray(),
                TaskEntries = taskEntries.ToArray()
            };
        }

        public void ReceiveFullSnapshot(NetworkQuestsSnapshot snapshot)
        {
            if (snapshot.NetworkId != NetworkId) return;

            ApplySnapshot(snapshot);
        }

        private async System.Threading.Tasks.Task ProcessBroadcastQueueAsync()
        {
            m_ProcessingBroadcastQueue = true;

            try
            {
                while (m_BroadcastQueue.Count > 0)
                {
                    NetworkQuestBroadcast broadcast = m_BroadcastQueue.Dequeue();
                    await ApplyBroadcastAsync(broadcast);
                    OnQuestApplied?.Invoke(broadcast);
                }
            }
            finally
            {
                m_ProcessingBroadcastQueue = false;
            }
        }

        private async System.Threading.Tasks.Task ApplyBroadcastAsync(NetworkQuestBroadcast broadcast)
        {
            Quest quest = ResolveQuestIdentity(broadcast.QuestIdString, broadcast.QuestHash);
            if (RequiresQuest(broadcast.Action) && quest == null)
            {
                return;
            }

            await ApplyAuthoritativeActionAsync(broadcast.Action, quest, broadcast.TaskId, broadcast.TaskValue);
        }

        private async System.Threading.Tasks.Task ApplyResponseStateAsync(NetworkQuestResponse response)
        {
            Quest quest = ResolveQuestIdentity(response.QuestIdString, response.QuestHash);
            if (RequiresQuest(response.Action) && quest == null)
            {
                return;
            }

            await ApplyAuthoritativeActionAsync(response.Action, quest, response.TaskId, response.TaskValue);
        }

        private async System.Threading.Tasks.Task<bool> ApplyAuthoritativeActionAsync(QuestActionType action, Quest quest, int taskId, double value)
        {
            bool previousSuppressInterception = m_SuppressInterception;
            m_SuppressInterception = true;

            try
            {
                switch (action)
                {
                    case QuestActionType.ActivateQuest:
                        return await m_Journal.ActivateQuest(quest);

                    case QuestActionType.DeactivateQuest:
                        return await m_Journal.DeactivateQuest(quest);

                    case QuestActionType.ActivateTask:
                        return await m_Journal.ActivateTask(quest, taskId);

                    case QuestActionType.DeactivateTask:
                        return await m_Journal.DeactivateTask(quest, taskId);

                    case QuestActionType.CompleteTask:
                        return await m_Journal.CompleteTask(quest, taskId);

                    case QuestActionType.AbandonTask:
                        return await m_Journal.AbandonTask(quest, taskId);

                    case QuestActionType.FailTask:
                        return await m_Journal.FailTask(quest, taskId);

                    case QuestActionType.SetTaskValue:
                        return await m_Journal.SetTaskValue(quest, taskId, value);

                    case QuestActionType.TrackQuest:
                        return m_Journal.TrackQuest(quest);

                    case QuestActionType.UntrackQuest:
                        return m_Journal.UntrackQuest(quest);

                    case QuestActionType.UntrackAll:
                        m_Journal.UntrackQuests();
                        return true;

                    default:
                        return false;
                }
            }
            finally
            {
                m_SuppressInterception = previousSuppressInterception;
            }
        }

        private bool TryResolveQuestRequest(in NetworkQuestRequest request, out Quest quest, out QuestRejectionReason rejection)
        {
            quest = null;
            rejection = QuestRejectionReason.None;

            if (!RequiresQuest(request.Action))
            {
                return true;
            }

            if (string.IsNullOrEmpty(request.QuestIdString))
            {
                rejection = QuestRejectionReason.QuestNotFound;
                return false;
            }

            if (request.QuestHash != 0)
            {
                IdString requestedId = new IdString(request.QuestIdString);
                if (requestedId.Hash != request.QuestHash)
                {
                    rejection = QuestRejectionReason.IdentityMismatch;
                    return false;
                }
            }

            quest = ResolveQuestIdentity(request.QuestIdString, request.QuestHash);
            if (quest == null)
            {
                rejection = QuestRejectionReason.QuestNotFound;
                return false;
            }

            if (request.QuestHash != 0 && quest.Id.Hash != request.QuestHash)
            {
                rejection = QuestRejectionReason.IdentityMismatch;
                return false;
            }

            return true;
        }

        private bool ValidateTaskRequest(in NetworkQuestRequest request, Quest quest, out QuestRejectionReason rejection)
        {
            rejection = QuestRejectionReason.None;

            if (!RequiresTask(request.Action))
            {
                return true;
            }

            if (quest == null)
            {
                rejection = QuestRejectionReason.QuestNotFound;
                return false;
            }

            if (request.TaskId == TASK_INVALID || !quest.Contains(request.TaskId))
            {
                rejection = QuestRejectionReason.TaskNotFound;
                return false;
            }

            return true;
        }

        private static bool RequiresQuest(QuestActionType action)
        {
            return action != QuestActionType.UntrackAll;
        }

        private static bool RequiresTask(QuestActionType action)
        {
            return action == QuestActionType.ActivateTask ||
                   action == QuestActionType.DeactivateTask ||
                   action == QuestActionType.CompleteTask ||
                   action == QuestActionType.AbandonTask ||
                   action == QuestActionType.FailTask ||
                   action == QuestActionType.SetTaskValue;
        }

        private Quest ResolveQuestIdentity(string questIdString, int questHash)
        {
            QuestsRepository repository = Settings.From<QuestsRepository>();
            Quest[] quests = repository?.Quests?.Quests;
            if (quests == null || quests.Length == 0)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(questIdString))
            {
                IdString questId = new IdString(questIdString);
                if (questHash != 0 && questId.Hash != questHash)
                {
                    return null;
                }

                Quest byId = repository.Quests.Get(questId);
                if (byId != null)
                {
                    return byId;
                }
            }

            if (questHash != 0)
            {
                for (int i = 0; i < quests.Length; i++)
                {
                    Quest candidate = quests[i];
                    if (candidate != null && candidate.Id.Hash == questHash)
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private NetworkQuestResponse BuildSuccessResponse(in NetworkQuestRequest request, Quest quest, int taskId)
        {
            byte questState = 0;
            byte taskState = 0;
            bool isTracking = false;
            double taskValue = 0d;

            if (quest != null)
            {
                questState = (byte)m_Journal.GetQuestState(quest);
                isTracking = m_Journal.IsQuestTracking(quest);
            }

            if (quest != null && taskId != TASK_INVALID && quest.Contains(taskId))
            {
                taskState = (byte)m_Journal.GetTaskState(quest, taskId);
                taskValue = m_Journal.GetTaskValue(quest, taskId);
            }

            return new NetworkQuestResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Action = request.Action,
                Authorized = true,
                Applied = true,
                RejectionReason = QuestRejectionReason.None,
                QuestHash = quest != null ? quest.Id.Hash : request.QuestHash,
                QuestIdString = quest != null ? quest.Id.String : request.QuestIdString,
                TaskId = taskId,
                QuestState = questState,
                TaskState = taskState,
                IsTracking = isTracking,
                TaskValue = taskValue,
                Error = string.Empty
            };
        }

        private NetworkQuestBroadcast BuildBroadcast(in NetworkQuestRequest request, Quest quest, int taskId, uint actorNetworkId)
        {
            NetworkQuestResponse response = BuildSuccessResponse(request, quest, taskId);
            return new NetworkQuestBroadcast
            {
                NetworkId = NetworkId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = request.CorrelationId,
                Action = request.Action,
                QuestHash = response.QuestHash,
                QuestIdString = response.QuestIdString,
                TaskId = response.TaskId,
                QuestState = response.QuestState,
                TaskState = response.TaskState,
                IsTracking = response.IsTracking,
                TaskValue = response.TaskValue,
                ServerTime = Time.time
            };
        }

        private static NetworkQuestResponse CreateRejectedResponse(in NetworkQuestRequest request, QuestRejectionReason reason, string error)
        {
            return new NetworkQuestResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Action = request.Action,
                Authorized = false,
                Applied = false,
                RejectionReason = reason,
                QuestHash = request.QuestHash,
                QuestIdString = request.QuestIdString,
                TaskId = request.TaskId,
                Error = error
            };
        }

        private static ulong GetPendingKey(uint actorNetworkId, uint correlationId, ushort requestId)
        {
            uint pendingCorrelation = correlationId != 0 ? correlationId : requestId;
            return ((ulong)actorNetworkId << 32) | pendingCorrelation;
        }

        private void CleanupPendingRequests()
        {
            float now = Time.time;
            const float timeout = 8f;

            if (m_PendingRequests.Count > 0)
            {
                List<ulong> expired = null;
                foreach (KeyValuePair<ulong, PendingQuestRequest> pair in m_PendingRequests)
                {
                    if (now - pair.Value.SentTime <= timeout) continue;

                    expired ??= new List<ulong>(4);
                    expired.Add(pair.Key);
                }

                if (expired != null)
                {
                    for (int i = 0; i < expired.Count; i++)
                    {
                        m_PendingRequests.Remove(expired[i]);
                    }
                }
            }

            if (m_RecentlyAppliedCorrelations.Count > 0)
            {
                List<uint> stale = null;
                foreach (KeyValuePair<uint, float> pair in m_RecentlyAppliedCorrelations)
                {
                    if (now - pair.Value <= timeout) continue;

                    stale ??= new List<uint>(4);
                    stale.Add(pair.Key);
                }

                if (stale != null)
                {
                    for (int i = 0; i < stale.Count; i++)
                    {
                        m_RecentlyAppliedCorrelations.Remove(stale[i]);
                    }
                }
            }
        }

        private void EnsureRegisteredWithManager()
        {
            NetworkQuestsManager manager = NetworkQuestsManager.Instance;
            if (manager == null) return;

            uint networkId = NetworkId;
            if (networkId == 0) return;

            if (m_IsRegistered && m_RegisteredNetworkId == networkId)
            {
                return;
            }

            if (m_IsRegistered)
            {
                manager.UnregisterController(m_RegisteredNetworkId);
            }

            manager.RegisterController(networkId, this);
            m_IsRegistered = true;
            m_RegisteredNetworkId = networkId;
        }

        private void UnregisterFromManager()
        {
            if (!m_IsRegistered) return;

            NetworkQuestsManager.Instance?.UnregisterController(m_RegisteredNetworkId);
            m_IsRegistered = false;
            m_RegisteredNetworkId = 0;
        }

        private Dictionary<int, string> BuildQuestIdLookup()
        {
            Dictionary<int, string> lookup = new Dictionary<int, string>(16);

            QuestsRepository repository = Settings.From<QuestsRepository>();
            Quest[] quests = repository?.Quests?.Quests;
            if (quests == null) return lookup;

            for (int i = 0; i < quests.Length; i++)
            {
                Quest quest = quests[i];
                if (quest == null) continue;
                lookup[quest.Id.Hash] = quest.Id.String;
            }

            return lookup;
        }

        private static int GetTaskKeyQuestHash(TaskKey key)
        {
            if (TaskKeyQuestHashField == null) return 0;

            object boxed = key;
            if (boxed == null) return 0;

            try
            {
                object value = TaskKeyQuestHashField.GetValue(boxed);
                return value is int hash ? hash : 0;
            }
            catch
            {
                return 0;
            }
        }

        private void ApplySnapshot(NetworkQuestsSnapshot snapshot)
        {
            HashSet<Quest> changedQuests = new HashSet<Quest>();
            HashSet<(Quest, int)> changedTasks = new HashSet<(Quest, int)>();

            m_Journal.QuestEntries.Clear();
            m_Journal.TaskEntries.Clear();

            if (snapshot.QuestEntries != null)
            {
                for (int i = 0; i < snapshot.QuestEntries.Length; i++)
                {
                    NetworkQuestSnapshotEntry entry = snapshot.QuestEntries[i];
                    Quest quest = ResolveQuestIdentity(entry.QuestIdString, entry.QuestHash);
                    if (quest == null) continue;

                    IdString questId = new IdString(entry.QuestIdString);
                    m_Journal.QuestEntries[questId] = new QuestEntry
                    {
                        State = (State)entry.State,
                        IsTracking = entry.IsTracking
                    };

                    changedQuests.Add(quest);
                }
            }

            if (snapshot.TaskEntries != null)
            {
                for (int i = 0; i < snapshot.TaskEntries.Length; i++)
                {
                    NetworkTaskSnapshotEntry entry = snapshot.TaskEntries[i];
                    Quest quest = ResolveQuestIdentity(entry.QuestIdString, entry.QuestHash);
                    if (quest == null || !quest.Contains(entry.TaskId)) continue;

                    TaskKey key = new TaskKey(quest.Id.Hash, entry.TaskId);
                    m_Journal.TaskEntries[key] = new TaskEntry
                    {
                        State = (State)entry.State,
                        Value = entry.Value
                    };

                    changedQuests.Add(quest);
                    changedTasks.Add((quest, entry.TaskId));
                }
            }

            try
            {
                JournalOnRememberMethod?.Invoke(m_Journal, null);
            }
            catch
            {
                // Non-fatal: snapshot data is still applied.
            }

            foreach (Quest quest in changedQuests)
            {
                InvokeQuestChangeEvent(quest);
            }

            foreach ((Quest quest, int taskId) in changedTasks)
            {
                InvokeTaskChangeEvent(quest);
                InvokeTaskValueChangeEvent(quest, taskId);
            }
        }

        private void InvokeQuestChangeEvent(Quest quest)
        {
            if (JournalEventQuestChangeField == null) return;
            if (JournalEventQuestChangeField.GetValue(m_Journal) is Action<Quest> callback)
            {
                callback.Invoke(quest);
            }
        }

        private void InvokeTaskChangeEvent(Quest quest)
        {
            if (JournalEventTaskChangeField == null) return;
            if (JournalEventTaskChangeField.GetValue(m_Journal) is Action<Quest> callback)
            {
                callback.Invoke(quest);
            }
        }

        private void InvokeTaskValueChangeEvent(Quest quest, int taskId)
        {
            if (JournalEventTaskValueChangeField == null) return;
            if (JournalEventTaskValueChangeField.GetValue(m_Journal) is Action<Quest, int> callback)
            {
                callback.Invoke(quest, taskId);
            }
        }

        private ushort GetNextRequestId()
        {
            if (m_NextRequestId == 0)
            {
                m_NextRequestId = 1;
            }

            ushort requestId = m_NextRequestId;
            m_NextRequestId++;
            if (m_NextRequestId == 0)
            {
                m_NextRequestId = 1;
            }

            m_LastIssuedRequestId = requestId;
            return requestId;
        }

        private void ForwardLocalInterceptedAction(QuestActionType action, Quest quest, int taskId = TASK_INVALID, double value = 0d)
        {
            if (m_SuppressInterception) return;
            if (!m_IsLocalClient || m_IsServer) return;

            RequestQuestAction(action, quest, taskId, value);
        }

        private void OnLocalQuestActivate(Quest quest)
        {
            ForwardLocalInterceptedAction(QuestActionType.ActivateQuest, quest);
        }

        private void OnLocalQuestDeactivate(Quest quest)
        {
            ForwardLocalInterceptedAction(QuestActionType.DeactivateQuest, quest);
        }

        private void OnLocalTaskActivate(Quest quest, int taskId)
        {
            ForwardLocalInterceptedAction(QuestActionType.ActivateTask, quest, taskId);
        }

        private void OnLocalTaskDeactivate(Quest quest, int taskId)
        {
            ForwardLocalInterceptedAction(QuestActionType.DeactivateTask, quest, taskId);
        }

        private void OnLocalTaskComplete(Quest quest, int taskId)
        {
            ForwardLocalInterceptedAction(QuestActionType.CompleteTask, quest, taskId);
        }

        private void OnLocalTaskAbandon(Quest quest, int taskId)
        {
            ForwardLocalInterceptedAction(QuestActionType.AbandonTask, quest, taskId);
        }

        private void OnLocalTaskFail(Quest quest, int taskId)
        {
            ForwardLocalInterceptedAction(QuestActionType.FailTask, quest, taskId);
        }

        private void OnLocalTaskValueChange(Quest quest, int taskId)
        {
            if (quest == null || taskId == TASK_INVALID) return;

            double value = m_Journal.GetTaskValue(quest, taskId);
            ForwardLocalInterceptedAction(QuestActionType.SetTaskValue, quest, taskId, value);
        }

        private void OnLocalQuestTrack(Quest quest)
        {
            ForwardLocalInterceptedAction(QuestActionType.TrackQuest, quest);
        }

        private void OnLocalQuestUntrack(Quest quest)
        {
            ForwardLocalInterceptedAction(QuestActionType.UntrackQuest, quest);
        }
    }
}
#endif
