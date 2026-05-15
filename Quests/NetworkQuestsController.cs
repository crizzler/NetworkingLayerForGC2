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
        [SerializeField] private NetworkQuestProfile m_Profile;
        [SerializeField] private bool m_OptimisticUpdates;
        [SerializeField] private bool m_AutoForwardProfiledJournalChanges = true;

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
        public NetworkQuestProfile Profile => m_Profile;
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
                NetworkQuestsSnapshot snapshot = CaptureFullSnapshot();
                LogQuestSync($"periodic full snapshot broadcast {DescribeSnapshot(snapshot)} entries={DescribeSnapshotEntries(snapshot)}");
                NetworkQuestsManager.Instance?.BroadcastFullSnapshot(snapshot);
                m_LastFullSync = currentTime;
            }
        }

        public void Initialize(bool isServer, bool isLocalClient)
        {
            m_IsServer = isServer;
            m_IsLocalClient = isLocalClient;
            m_IsRemoteClient = !isServer && !isLocalClient;

            EnsureRegisteredWithManager();

            LogQuestSync($"initialized profile={DescribeProfile()} journalQuests={m_Journal?.QuestEntries?.Count ?? 0} journalTasks={m_Journal?.TaskEntries?.Count ?? 0}");
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

        public void RequestQuestAction(
            QuestActionType action,
            Quest quest,
            int taskId = TASK_INVALID,
            double value = 0d,
            NetworkQuestShareMode shareMode = NetworkQuestShareMode.Personal,
            string scopeId = "")
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

            if (!IsProfileAllowed(action, quest, shareMode, true, false, out QuestRejectionReason profileRejection))
            {
                string message = $"Quest action {action} is not allowed by NetworkQuestProfile for scope {shareMode}.";
                if (m_LogRejections)
                {
                    Debug.LogWarning($"[NetworkQuestsController] {message}");
                }

                OnQuestRejected?.Invoke(profileRejection, message);
                return;
            }

            var request = new NetworkQuestRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = networkId,
                CorrelationId = NetworkCorrelation.Compose(networkId, m_LastIssuedRequestId),
                TargetNetworkId = networkId,
                ProfileHash = m_Profile != null ? m_Profile.ProfileHash : 0,
                ShareMode = shareMode,
                ScopeId = scopeId ?? string.Empty,
                Action = action,
                QuestHash = quest != null ? quest.Id.Hash : 0,
                QuestIdString = quest != null ? quest.Id.String : string.Empty,
                TaskId = taskId,
                Value = value
            };

            LogQuestSync(
                $"created request {DescribeRequest(request)} " +
                $"before={DescribeJournalQuest(quest)} {DescribeJournalTask(quest, taskId)} " +
                $"optimistic={m_OptimisticUpdates}");

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
                LogQuestSync($"processing request locally on server {DescribeRequest(request)}");
                _ = ProcessLocalServerRequestAsync(request);
            }
            else
            {
                if (m_OptimisticUpdates)
                {
                    LogQuestSync($"applying optimistic local request {DescribeRequest(request)}");
                    _ = ApplyAuthoritativeActionAsync(action, quest, taskId, value);
                }

                LogQuestSync($"sending request to server {DescribeRequest(request)}");
                manager.SendQuestRequest(request);
            }
        }

        private async System.Threading.Tasks.Task ProcessLocalServerRequestAsync(NetworkQuestRequest request)
        {
            NetworkQuestResponse response = await ProcessQuestRequestAsync(request, request.ActorNetworkId);
            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            LogQuestSync($"local server produced response {DescribeResponse(response)}");
            ReceiveQuestResponse(response);
        }

        public async System.Threading.Tasks.Task<NetworkQuestResponse> ProcessQuestRequestAsync(NetworkQuestRequest request, uint senderClientId)
        {
            LogQuestSync($"server processing request sender={senderClientId} {DescribeRequest(request)}");

            if (!Enum.IsDefined(typeof(QuestActionType), request.Action))
            {
                LogQuestWarning($"rejecting request with invalid action sender={senderClientId} {DescribeRequest(request)}");
                return CreateRejectedResponse(request, QuestRejectionReason.InvalidAction, "Unknown quest action");
            }

            QuestRejectionReason rejection = QuestRejectionReason.None;
            Quest quest = null;

            if (!TryResolveQuestRequest(request, out quest, out rejection))
            {
                LogQuestWarning($"rejecting request: quest resolution failed reason={rejection} sender={senderClientId} {DescribeRequest(request)}");
                return CreateRejectedResponse(request, rejection, "Quest resolution failed");
            }

            if (!ValidateTaskRequest(request, quest, out rejection))
            {
                LogQuestWarning($"rejecting request: task resolution failed reason={rejection} sender={senderClientId} {DescribeRequest(request)}");
                return CreateRejectedResponse(request, rejection, "Task resolution failed");
            }

            bool applied;
            try
            {
                LogQuestSync(
                    $"authoritative apply begin sender={senderClientId} {DescribeRequest(request)} " +
                    $"before={DescribeJournalQuest(quest)} {DescribeJournalTask(quest, request.TaskId)}");
                applied = await ApplyAuthoritativeActionAsync(request.Action, quest, request.TaskId, request.Value);
                LogQuestSync(
                    $"authoritative apply result applied={applied} sender={senderClientId} {DescribeRequest(request)} " +
                    $"after={DescribeJournalQuest(quest)} {DescribeJournalTask(quest, request.TaskId)}");
            }
            catch (Exception exception)
            {
                LogQuestWarning($"exception while applying request sender={senderClientId} {DescribeRequest(request)} exception={exception.Message}");
                return CreateRejectedResponse(request, QuestRejectionReason.Exception, exception.Message);
            }

            if (!applied)
            {
                LogQuestWarning(
                    $"runtime rejected request as invalid state sender={senderClientId} {DescribeRequest(request)} " +
                    $"journal={DescribeJournalQuest(quest)} {DescribeJournalTask(quest, request.TaskId)}");
                return CreateRejectedResponse(request, QuestRejectionReason.InvalidState, "Quest action rejected by runtime state");
            }

            NetworkQuestResponse response = BuildSuccessResponse(request, quest, request.TaskId);
            LogQuestSync($"success response built {DescribeResponse(response)}");

            if (m_IsServer)
            {
                NetworkQuestBroadcast broadcast = BuildBroadcast(request, quest, response.TaskId, request.ActorNetworkId);
                LogQuestSync($"broadcasting authoritative change {DescribeBroadcast(broadcast)}");
                NetworkQuestsManager.Instance?.BroadcastQuestChange(broadcast);
            }

            LogQuestSync($"applied request sender={senderClientId} {DescribeRequest(request)}");

            return response;
        }

        public void ReceiveQuestResponse(NetworkQuestResponse response)
        {
            ulong key = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (!m_PendingRequests.Remove(key))
            {
                LogQuestSync($"received response without pending request key={key} {DescribeResponse(response)}");
                return;
            }

            LogQuestSync($"received response for pending request key={key} {DescribeResponse(response)}");

            if (!response.Authorized || !response.Applied)
            {
                LogQuestWarning($"quest request rejected {DescribeResponse(response)}");

                OnQuestRejected?.Invoke(response.RejectionReason, response.Error);
                return;
            }

            if (!m_IsServer)
            {
                m_RecentlyAppliedCorrelations[response.CorrelationId] = Time.time;
                LogQuestSync($"response accepted; recorded correlation={response.CorrelationId} applyResponseState={!m_OptimisticUpdates}");
                if (!m_OptimisticUpdates)
                {
                    _ = ApplyResponseStateAsync(response);
                }
            }
        }

        public void ReceiveQuestChangeBroadcast(NetworkQuestBroadcast broadcast)
        {
            LogQuestSync($"received broadcast {DescribeBroadcast(broadcast)} localNetworkId={NetworkId}");

            if (broadcast.ShareMode == NetworkQuestShareMode.Personal && broadcast.NetworkId != NetworkId)
            {
                LogQuestSync($"skipped personal broadcast for networkId={broadcast.NetworkId}");
                return;
            }

            if (broadcast.ShareMode != NetworkQuestShareMode.Personal && !ShouldReceiveSharedBroadcast(broadcast))
            {
                LogQuestSync($"skipped shared broadcast rejected by local profile {DescribeBroadcast(broadcast)}");
                return;
            }

            if (broadcast.CorrelationId != 0 && m_RecentlyAppliedCorrelations.Remove(broadcast.CorrelationId))
            {
                LogQuestSync($"skipped broadcast because correlation was already applied locally correlation={broadcast.CorrelationId}");
                return;
            }

            m_BroadcastQueue.Enqueue(broadcast);
            LogQuestSync($"queued broadcast queueCount={m_BroadcastQueue.Count} {DescribeBroadcast(broadcast)}");
            if (!m_ProcessingBroadcastQueue)
            {
                _ = ProcessBroadcastQueueAsync();
            }
        }

        public NetworkQuestsSnapshot CaptureFullSnapshot()
        {
            if (m_Profile != null && !m_Profile.SnapshotOnLateJoin)
            {
                NetworkQuestsSnapshot emptySnapshot = new NetworkQuestsSnapshot
                {
                    NetworkId = NetworkId,
                    ServerTime = Time.time,
                    QuestEntries = Array.Empty<NetworkQuestSnapshotEntry>(),
                    TaskEntries = Array.Empty<NetworkTaskSnapshotEntry>()
                };

                LogQuestSync($"captured empty full snapshot because profile disables late-join snapshots {DescribeSnapshot(emptySnapshot)}");
                return emptySnapshot;
            }

            List<NetworkQuestSnapshotEntry> questEntries = new List<NetworkQuestSnapshotEntry>(m_Journal.QuestEntries.Count);
            foreach (KeyValuePair<IdString, QuestEntry> pair in m_Journal.QuestEntries)
            {
                NetworkQuestBinding binding = ResolveSnapshotBinding(pair.Key.Hash);
                if (m_Profile != null && binding.Quest == null) continue;
                questEntries.Add(new NetworkQuestSnapshotEntry
                {
                    ProfileHash = m_Profile != null ? m_Profile.ProfileHash : 0,
                    ShareMode = binding.Quest != null ? binding.ShareMode : NetworkQuestShareMode.Personal,
                    ScopeId = string.Empty,
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

                NetworkQuestBinding binding = ResolveSnapshotBinding(questHash);
                if (m_Profile != null && binding.Quest == null) continue;
                taskEntries.Add(new NetworkTaskSnapshotEntry
                {
                    ProfileHash = m_Profile != null ? m_Profile.ProfileHash : 0,
                    ShareMode = binding.Quest != null ? binding.ShareMode : NetworkQuestShareMode.Personal,
                    ScopeId = string.Empty,
                    QuestHash = questHash,
                    QuestIdString = questIdLookup.TryGetValue(questHash, out string questIdString) ? questIdString : string.Empty,
                    TaskId = pair.Key.TaskId,
                    State = (byte)pair.Value.State,
                    Value = pair.Value.Value
                });
            }

            NetworkQuestsSnapshot snapshot = new NetworkQuestsSnapshot
            {
                NetworkId = NetworkId,
                ServerTime = Time.time,
                QuestEntries = questEntries.ToArray(),
                TaskEntries = taskEntries.ToArray()
            };

            LogQuestSync($"captured full snapshot {DescribeSnapshot(snapshot)} entries={DescribeSnapshotEntries(snapshot)}");
            return snapshot;
        }

        public void ReceiveFullSnapshot(NetworkQuestsSnapshot snapshot)
        {
            if (m_IsServer)
            {
                LogQuestSync($"skipped full snapshot on server-authoritative controller {DescribeSnapshot(snapshot)}");
                return;
            }

            if (snapshot.NetworkId != NetworkId)
            {
                LogQuestSync($"skipped full snapshot targetNetworkId={snapshot.NetworkId} localNetworkId={NetworkId} {DescribeSnapshot(snapshot)}");
                return;
            }

            LogQuestSync(
                $"receiving full snapshot {DescribeSnapshot(snapshot)} entries={DescribeSnapshotEntries(snapshot)} " +
                $"beforeQuests={m_Journal?.QuestEntries?.Count ?? 0} beforeTasks={m_Journal?.TaskEntries?.Count ?? 0}");
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
                    LogQuestSync($"dequeued broadcast queueRemaining={m_BroadcastQueue.Count} {DescribeBroadcast(broadcast)}");
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
                LogQuestWarning($"cannot apply broadcast: quest resolution failed {DescribeBroadcast(broadcast)}");
                return;
            }

            if (ShouldApplyRawState(broadcast.ProfileHash))
            {
                LogQuestSync($"applying broadcast as raw state {DescribeBroadcast(broadcast)} before={DescribeJournalQuest(quest)} {DescribeJournalTask(quest, broadcast.TaskId)}");
                ApplyBroadcastState(broadcast, quest);
                return;
            }

            LogQuestSync($"replaying broadcast through GC2 journal methods {DescribeBroadcast(broadcast)} before={DescribeJournalQuest(quest)} {DescribeJournalTask(quest, broadcast.TaskId)}");
            await ApplyAuthoritativeActionAsync(broadcast.Action, quest, broadcast.TaskId, broadcast.TaskValue);
        }

        private async System.Threading.Tasks.Task ApplyResponseStateAsync(NetworkQuestResponse response)
        {
            Quest quest = ResolveQuestIdentity(response.QuestIdString, response.QuestHash);
            if (RequiresQuest(response.Action) && quest == null)
            {
                LogQuestWarning($"cannot apply response state: quest resolution failed {DescribeResponse(response)}");
                return;
            }

            if (ShouldApplyRawState(response.ProfileHash))
            {
                LogQuestSync($"applying response as raw state {DescribeResponse(response)} before={DescribeJournalQuest(quest)} {DescribeJournalTask(quest, response.TaskId)}");
                ApplyResponseState(response, quest);
                return;
            }

            LogQuestSync($"replaying response through GC2 journal methods {DescribeResponse(response)} before={DescribeJournalQuest(quest)} {DescribeJournalTask(quest, response.TaskId)}");
            await ApplyAuthoritativeActionAsync(response.Action, quest, response.TaskId, response.TaskValue);
        }

        public bool IsNetworkRequestAllowed(
            in NetworkQuestRequest request,
            out QuestRejectionReason rejection)
        {
            return IsNetworkRequestAllowed(request, out rejection, out _);
        }

        public bool IsNetworkRequestAllowed(
            in NetworkQuestRequest request,
            out QuestRejectionReason rejection,
            out string error)
        {
            Quest quest = ResolveQuestIdentity(request.QuestIdString, request.QuestHash);
            return IsProfileAllowed(
                request.Action,
                quest,
                request.QuestHash,
                request.ShareMode,
                true,
                false,
                out rejection,
                request.ProfileHash,
                out error);
        }

        public bool ShouldReceiveSharedBroadcast(in NetworkQuestBroadcast broadcast)
        {
            if (broadcast.ShareMode == NetworkQuestShareMode.Personal) return broadcast.NetworkId == NetworkId;

            Quest quest = ResolveQuestIdentity(broadcast.QuestIdString, broadcast.QuestHash);
            return IsProfileAllowed(
                broadcast.Action,
                quest,
                broadcast.QuestHash,
                broadcast.ShareMode,
                false,
                false,
                out _,
                broadcast.ProfileHash);
        }

        private bool ShouldApplyRawState(int profileHash)
        {
            return m_Profile != null &&
                   m_Profile.ProfileHash == profileHash &&
                   !m_Profile.ReplayQuestMethodsOnRemoteClients;
        }

        private void ApplyBroadcastState(NetworkQuestBroadcast broadcast, Quest quest)
        {
            ApplyRawState(
                broadcast.Action,
                quest,
                broadcast.TaskId,
                broadcast.QuestState,
                broadcast.TaskState,
                broadcast.IsTracking,
                broadcast.TaskValue);
        }

        private void ApplyResponseState(NetworkQuestResponse response, Quest quest)
        {
            ApplyRawState(
                response.Action,
                quest,
                response.TaskId,
                response.QuestState,
                response.TaskState,
                response.IsTracking,
                response.TaskValue);
        }

        private void ApplyRawState(
            QuestActionType action,
            Quest quest,
            int taskId,
            byte questState,
            byte taskState,
            bool isTracking,
            double taskValue)
        {
            bool previousSuppressInterception = m_SuppressInterception;
            m_SuppressInterception = true;

            try
            {
                if (action == QuestActionType.UntrackAll)
                {
                    LogQuestSync("applying raw state UntrackAll");
                    m_Journal.UntrackQuests();
                    return;
                }

                if (quest == null) return;

                string beforeQuest = DescribeJournalQuest(quest);
                string beforeTask = DescribeJournalTask(quest, taskId);
                LogQuestSync(
                    $"raw state apply begin action={action} quest={DescribeQuest(quest, quest != null ? quest.Id.Hash : 0)} " +
                    $"task={taskId} questState={DescribeState(questState)} taskState={DescribeState(taskState)} " +
                    $"tracking={isTracking} taskValue={taskValue:0.###} before={beforeQuest} {beforeTask}");

                m_Journal.QuestEntries[quest.Id] = new QuestEntry
                {
                    State = (State)questState,
                    IsTracking = isTracking
                };

                if (taskId != TASK_INVALID && quest.Contains(taskId))
                {
                    TaskKey taskKey = new TaskKey(quest.Id.Hash, taskId);
                    m_Journal.TaskEntries[taskKey] = new TaskEntry
                    {
                        State = (State)taskState,
                        Value = taskValue
                    };

                    InvokeTaskChangeEvent(quest);
                    InvokeTaskValueChangeEvent(quest, taskId);
                }

                try
                {
                    JournalOnRememberMethod?.Invoke(m_Journal, null);
                }
                catch
                {
                    // Non-fatal: raw quest state was already applied.
                }

                InvokeQuestChangeEvent(quest);

                LogQuestSync(
                    $"raw state apply end action={action} quest={DescribeQuest(quest, quest.Id.Hash)} " +
                    $"after={DescribeJournalQuest(quest)} {DescribeJournalTask(quest, taskId)}");
            }
            finally
            {
                m_SuppressInterception = previousSuppressInterception;
            }
        }

        private async System.Threading.Tasks.Task<bool> ApplyAuthoritativeActionAsync(QuestActionType action, Quest quest, int taskId, double value)
        {
            bool previousSuppressInterception = m_SuppressInterception;
            m_SuppressInterception = true;
            string beforeQuest = DescribeJournalQuest(quest);
            string beforeTask = DescribeJournalTask(quest, taskId);

            try
            {
                LogQuestSync(
                    $"GC2 journal apply begin action={action} quest={DescribeQuest(quest, quest != null ? quest.Id.Hash : 0)} " +
                    $"task={taskId} value={value:0.###} before={beforeQuest} {beforeTask}");
                bool applied;

                switch (action)
                {
                    case QuestActionType.ActivateQuest:
                        applied = await m_Journal.ActivateQuest(quest);
                        break;

                    case QuestActionType.DeactivateQuest:
                        applied = await m_Journal.DeactivateQuest(quest);
                        break;

                    case QuestActionType.ActivateTask:
                        applied = await m_Journal.ActivateTask(quest, taskId);
                        break;

                    case QuestActionType.DeactivateTask:
                        applied = await m_Journal.DeactivateTask(quest, taskId);
                        break;

                    case QuestActionType.CompleteTask:
                        applied = await m_Journal.CompleteTask(quest, taskId);
                        break;

                    case QuestActionType.AbandonTask:
                        applied = await m_Journal.AbandonTask(quest, taskId);
                        break;

                    case QuestActionType.FailTask:
                        applied = await m_Journal.FailTask(quest, taskId);
                        break;

                    case QuestActionType.SetTaskValue:
                        applied = await m_Journal.SetTaskValue(quest, taskId, value);
                        break;

                    case QuestActionType.TrackQuest:
                        applied = m_Journal.TrackQuest(quest);
                        break;

                    case QuestActionType.UntrackQuest:
                        applied = m_Journal.UntrackQuest(quest);
                        break;

                    case QuestActionType.UntrackAll:
                        m_Journal.UntrackQuests();
                        applied = true;
                        break;

                    default:
                        applied = false;
                        break;
                }

                LogQuestSync(
                    $"GC2 journal apply end action={action} applied={applied} " +
                    $"after={DescribeJournalQuest(quest)} {DescribeJournalTask(quest, taskId)}");
                return applied;
            }
            finally
            {
                m_SuppressInterception = previousSuppressInterception;
            }
        }

        private bool IsProfileAllowed(
            QuestActionType action,
            Quest quest,
            NetworkQuestShareMode shareMode,
            bool requireClientWrite,
            bool requireAutoForward,
            out QuestRejectionReason rejection)
        {
            int questHash = quest != null ? quest.Id.Hash : 0;
            return IsProfileAllowed(
                action,
                quest,
                questHash,
                shareMode,
                requireClientWrite,
                requireAutoForward,
                out rejection,
                m_Profile != null ? m_Profile.ProfileHash : 0);
        }

        private bool IsProfileAllowed(
            QuestActionType action,
            Quest quest,
            int questHash,
            NetworkQuestShareMode shareMode,
            bool requireClientWrite,
            bool requireAutoForward,
            out QuestRejectionReason rejection,
            int expectedProfileHash)
        {
            return IsProfileAllowed(
                action,
                quest,
                questHash,
                shareMode,
                requireClientWrite,
                requireAutoForward,
                out rejection,
                expectedProfileHash,
                out _);
        }

        private bool IsProfileAllowed(
            QuestActionType action,
            Quest quest,
            int questHash,
            NetworkQuestShareMode shareMode,
            bool requireClientWrite,
            bool requireAutoForward,
            out QuestRejectionReason rejection,
            int expectedProfileHash,
            out string error)
        {
            rejection = QuestRejectionReason.None;
            error = string.Empty;

            if (!RequiresQuest(action))
            {
                if (shareMode == NetworkQuestShareMode.Personal) return true;
                rejection = QuestRejectionReason.InvalidAction;
                error = $"Quest action {action} does not support shared scope {shareMode}.";
                return false;
            }

            if (m_Profile == null)
            {
                if (shareMode == NetworkQuestShareMode.Personal) return true;
                rejection = QuestRejectionReason.NotAuthorized;
                error = $"NetworkQuestProfile missing on '{name}' for shared {action} quest={DescribeQuest(quest, questHash)} scope={shareMode}.";
                LogProfileRejection(error);
                return false;
            }

            if (expectedProfileHash != 0 && expectedProfileHash != m_Profile.ProfileHash)
            {
                rejection = QuestRejectionReason.IdentityMismatch;
                error = $"NetworkQuestProfile mismatch on '{name}' requestProfile={expectedProfileHash} localProfile={m_Profile.ProfileHash} profile='{m_Profile.name}'.";
                LogProfileRejection(error);
                return false;
            }

            if (!m_Profile.IsQuestAllowed(
                    quest,
                    questHash,
                    action,
                    shareMode,
                    requireClientWrite,
                    requireAutoForward,
                    out _))
            {
                rejection = QuestRejectionReason.NotAuthorized;
                error = DescribeProfileAuthorizationFailure(
                    action,
                    quest,
                    questHash,
                    shareMode,
                    requireClientWrite,
                    requireAutoForward);
                LogProfileRejection(error);
                return false;
            }

            return true;
        }

        private string DescribeProfileAuthorizationFailure(
            QuestActionType action,
            Quest quest,
            int questHash,
            NetworkQuestShareMode shareMode,
            bool requireClientWrite,
            bool requireAutoForward)
        {
            string questLabel = DescribeQuest(quest, questHash);
            if (m_Profile == null)
            {
                return $"NetworkQuestProfile missing on '{name}' for {action} quest={questLabel} scope={shareMode}.";
            }

            NetworkQuestBinding[] bindings = m_Profile.Quests;
            bool foundQuest = false;
            bool foundShareMode = false;
            bool foundClientWrite = false;
            bool foundAutoForward = false;
            bool foundAction = false;
            NetworkQuestActionMask actionMask = NetworkQuestProfile.GetActionMask(action);

            for (int i = 0; i < bindings.Length; i++)
            {
                NetworkQuestBinding binding = bindings[i];
                Quest candidate = binding.Quest;
                if (candidate == null) continue;

                int candidateHash = candidate.Id.Hash;
                bool matchesQuest = quest != null && candidateHash == quest.Id.Hash;
                bool matchesHash = questHash != 0 && candidateHash == questHash;
                if (!matchesQuest && !matchesHash) continue;

                foundQuest = true;
                if (binding.ShareMode != shareMode) continue;

                foundShareMode = true;
                if (!requireClientWrite || binding.AllowClientWrites) foundClientWrite = true;
                if (!requireAutoForward || binding.AutoForwardJournalChanges) foundAutoForward = true;
                if ((binding.AllowedActions & actionMask) != 0) foundAction = true;
            }

            string reason;
            if (!foundQuest)
            {
                reason = "quest is not bound";
            }
            else if (!foundShareMode)
            {
                reason = $"no binding for share mode {shareMode}";
            }
            else if (!foundClientWrite)
            {
                reason = "matching binding does not allow client writes";
            }
            else if (!foundAutoForward)
            {
                reason = "matching binding does not allow auto-forward";
            }
            else if (!foundAction)
            {
                reason = $"matching binding does not allow action {action}";
            }
            else
            {
                reason = "profile returned false despite a matching binding";
            }

            return $"NetworkQuestProfile '{m_Profile.name}' rejected {action} quest={questLabel} scope={shareMode}: {reason}.";
        }

        private static string DescribeQuest(Quest quest, int questHash)
        {
            if (quest != null)
            {
                return $"'{quest.name}' id='{quest.Id.String}' hash={quest.Id.Hash}";
            }

            return questHash != 0 ? $"hash={questHash}" : "none";
        }

        private string DescribeProfile()
        {
            return m_Profile != null
                ? $"'{m_Profile.name}' hash={m_Profile.ProfileHash} replay={m_Profile.ReplayQuestMethodsOnRemoteClients} lateJoin={m_Profile.SnapshotOnLateJoin}"
                : "none";
        }

        private string DescribeRole()
        {
            if (m_IsServer && m_IsLocalClient) return "HostLocal";
            if (m_IsServer) return "Server";
            if (m_IsLocalClient) return "LocalClient";
            return "RemoteClient";
        }

        private static string DescribeState(byte state)
        {
            int value = state;
            return Enum.IsDefined(typeof(State), value)
                ? $"{(State)value}({value})"
                : $"Unknown({value})";
        }

        private static string DescribeState(State state)
        {
            return $"{state}({(int)state})";
        }

        private static string DescribeQuestIdentity(string questIdString, int questHash)
        {
            return !string.IsNullOrEmpty(questIdString)
                ? $"quest='{questIdString}' hash={questHash}"
                : $"questHash={questHash}";
        }

        private string DescribeJournalQuest(Quest quest)
        {
            if (m_Journal == null || quest == null) return "journalQuest=none";

            bool present = m_Journal.QuestEntries.TryGetValue(quest.Id, out QuestEntry entry);
            State state = present ? entry.State : State.Inactive;
            bool tracking = present && entry.IsTracking;
            return $"journalQuest(present={present}, state={DescribeState(state)}, tracking={tracking})";
        }

        private string DescribeJournalTask(Quest quest, int taskId)
        {
            if (m_Journal == null || quest == null || taskId == TASK_INVALID || !quest.Contains(taskId))
            {
                return "journalTask=none";
            }

            TaskKey key = new TaskKey(quest.Id.Hash, taskId);
            bool present = m_Journal.TaskEntries.TryGetValue(key, out TaskEntry entry);
            State state = present ? entry.State : State.Inactive;
            double value = present ? entry.Value : 0d;
            return $"journalTask(id={taskId}, present={present}, state={DescribeState(state)}, value={value:0.###})";
        }

        private static string DescribeRequest(in NetworkQuestRequest request)
        {
            return
                $"requestId={request.RequestId} actor={request.ActorNetworkId} target={request.TargetNetworkId} " +
                $"correlation={request.CorrelationId} action={request.Action} share={request.ShareMode} " +
                $"profile={request.ProfileHash} {DescribeQuestIdentity(request.QuestIdString, request.QuestHash)} " +
                $"task={request.TaskId} value={request.Value:0.###} scope='{request.ScopeId}'";
        }

        private static string DescribeResponse(in NetworkQuestResponse response)
        {
            return
                $"requestId={response.RequestId} actor={response.ActorNetworkId} correlation={response.CorrelationId} " +
                $"action={response.Action} authorized={response.Authorized} applied={response.Applied} " +
                $"rejection={response.RejectionReason} share={response.ShareMode} profile={response.ProfileHash} " +
                $"{DescribeQuestIdentity(response.QuestIdString, response.QuestHash)} task={response.TaskId} " +
                $"questState={DescribeState(response.QuestState)} taskState={DescribeState(response.TaskState)} " +
                $"tracking={response.IsTracking} taskValue={response.TaskValue:0.###} error='{response.Error}'";
        }

        private static string DescribeBroadcast(in NetworkQuestBroadcast broadcast)
        {
            return
                $"networkId={broadcast.NetworkId} actor={broadcast.ActorNetworkId} correlation={broadcast.CorrelationId} " +
                $"action={broadcast.Action} share={broadcast.ShareMode} profile={broadcast.ProfileHash} " +
                $"{DescribeQuestIdentity(broadcast.QuestIdString, broadcast.QuestHash)} task={broadcast.TaskId} " +
                $"questState={DescribeState(broadcast.QuestState)} taskState={DescribeState(broadcast.TaskState)} " +
                $"tracking={broadcast.IsTracking} taskValue={broadcast.TaskValue:0.###} serverTime={broadcast.ServerTime:0.###}";
        }

        private static string DescribeSnapshot(in NetworkQuestsSnapshot snapshot)
        {
            int questCount = snapshot.QuestEntries?.Length ?? 0;
            int taskCount = snapshot.TaskEntries?.Length ?? 0;
            return $"networkId={snapshot.NetworkId} serverTime={snapshot.ServerTime:0.###} quests={questCount} tasks={taskCount}";
        }

        private static string DescribeQuestEntry(in NetworkQuestSnapshotEntry entry)
        {
            return
                $"{DescribeQuestIdentity(entry.QuestIdString, entry.QuestHash)} " +
                $"state={DescribeState(entry.State)} tracking={entry.IsTracking} share={entry.ShareMode} profile={entry.ProfileHash}";
        }

        private static string DescribeTaskEntry(in NetworkTaskSnapshotEntry entry)
        {
            return
                $"{DescribeQuestIdentity(entry.QuestIdString, entry.QuestHash)} task={entry.TaskId} " +
                $"state={DescribeState(entry.State)} value={entry.Value:0.###} share={entry.ShareMode} profile={entry.ProfileHash}";
        }

        private static string DescribeSnapshotEntries(in NetworkQuestsSnapshot snapshot)
        {
            const int maxEntries = 4;
            string summary = string.Empty;

            if (snapshot.QuestEntries != null)
            {
                int count = Mathf.Min(snapshot.QuestEntries.Length, maxEntries);
                for (int i = 0; i < count; i++)
                {
                    summary += $" q[{i}]={DescribeQuestEntry(snapshot.QuestEntries[i])};";
                }

                if (snapshot.QuestEntries.Length > maxEntries)
                {
                    summary += $" qMore={snapshot.QuestEntries.Length - maxEntries};";
                }
            }

            if (snapshot.TaskEntries != null)
            {
                int count = Mathf.Min(snapshot.TaskEntries.Length, maxEntries);
                for (int i = 0; i < count; i++)
                {
                    summary += $" t[{i}]={DescribeTaskEntry(snapshot.TaskEntries[i])};";
                }

                if (snapshot.TaskEntries.Length > maxEntries)
                {
                    summary += $" tMore={snapshot.TaskEntries.Length - maxEntries};";
                }
            }

            return string.IsNullOrEmpty(summary) ? "empty" : summary;
        }

        private void LogQuestSync(string message)
        {
            if (!m_LogAllChanges) return;
            Debug.Log($"[NetworkQuestSync][Controller] {name} netId={NetworkId} role={DescribeRole()} suppress={m_SuppressInterception} {message}", this);
        }

        private void LogQuestWarning(string message)
        {
            if (!m_LogRejections && !m_LogAllChanges) return;
            Debug.LogWarning($"[NetworkQuestSync][Controller] {name} netId={NetworkId} role={DescribeRole()} suppress={m_SuppressInterception} {message}", this);
        }

        private void LogProfileRejection(string message)
        {
            if (!m_LogRejections) return;
            Debug.LogWarning($"[NetworkQuestSync][Controller] {name} netId={NetworkId} role={DescribeRole()} {message}", this);
        }

        private NetworkQuestBinding ResolveSnapshotBinding(int questHash)
        {
            if (m_Profile != null &&
                m_Profile.TryGetBinding(null, questHash, out NetworkQuestBinding binding))
            {
                return binding;
            }

            return default;
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
            int responseTaskId = ResolveResponseTaskId(request.Action, quest, taskId);
            byte questState = 0;
            byte taskState = 0;
            bool isTracking = false;
            double taskValue = 0d;

            if (quest != null)
            {
                questState = (byte)m_Journal.GetQuestState(quest);
                isTracking = m_Journal.IsQuestTracking(quest);
            }

            if (quest != null && responseTaskId != TASK_INVALID && quest.Contains(responseTaskId))
            {
                taskState = (byte)m_Journal.GetTaskState(quest, responseTaskId);
                taskValue = m_Journal.GetTaskValue(quest, responseTaskId);
            }

            return new NetworkQuestResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                ProfileHash = request.ProfileHash,
                ShareMode = request.ShareMode,
                ScopeId = request.ScopeId,
                Action = request.Action,
                Authorized = true,
                Applied = true,
                RejectionReason = QuestRejectionReason.None,
                QuestHash = quest != null ? quest.Id.Hash : request.QuestHash,
                QuestIdString = quest != null ? quest.Id.String : request.QuestIdString,
                TaskId = responseTaskId,
                QuestState = questState,
                TaskState = taskState,
                IsTracking = isTracking,
                TaskValue = taskValue,
                Error = string.Empty
            };
        }

        private static int ResolveResponseTaskId(QuestActionType action, Quest quest, int taskId)
        {
            if (taskId != TASK_INVALID || quest == null || action != QuestActionType.ActivateQuest)
            {
                return taskId;
            }

            int firstTaskId = quest.Tasks.FirstRootId;
            return firstTaskId != TASK_INVALID && quest.Contains(firstTaskId)
                ? firstTaskId
                : TASK_INVALID;
        }

        private NetworkQuestBroadcast BuildBroadcast(in NetworkQuestRequest request, Quest quest, int taskId, uint actorNetworkId)
        {
            NetworkQuestResponse response = BuildSuccessResponse(request, quest, taskId);
            return new NetworkQuestBroadcast
            {
                NetworkId = NetworkId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = request.CorrelationId,
                ProfileHash = request.ProfileHash,
                ShareMode = request.ShareMode,
                ScopeId = request.ScopeId,
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
                ProfileHash = request.ProfileHash,
                ShareMode = request.ShareMode,
                ScopeId = request.ScopeId,
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

            LogQuestSync(
                $"ApplySnapshot begin {DescribeSnapshot(snapshot)} entries={DescribeSnapshotEntries(snapshot)} " +
                $"beforeQuests={m_Journal.QuestEntries.Count} beforeTasks={m_Journal.TaskEntries.Count}");

            bool previousSuppressInterception = m_SuppressInterception;
            m_SuppressInterception = true;

            try
            {
                m_Journal.QuestEntries.Clear();
                m_Journal.TaskEntries.Clear();

                if (snapshot.QuestEntries != null)
                {
                    for (int i = 0; i < snapshot.QuestEntries.Length; i++)
                    {
                        NetworkQuestSnapshotEntry entry = snapshot.QuestEntries[i];
                        Quest quest = ResolveQuestIdentity(entry.QuestIdString, entry.QuestHash);
                        if (quest == null)
                        {
                            LogQuestWarning($"snapshot quest entry skipped: quest not resolved {DescribeQuestEntry(entry)}");
                            continue;
                        }

                        IdString questId = new IdString(entry.QuestIdString);
                        m_Journal.QuestEntries[questId] = new QuestEntry
                        {
                            State = (State)entry.State,
                            IsTracking = entry.IsTracking
                        };

                        changedQuests.Add(quest);
                        LogQuestSync($"snapshot quest entry applied {DescribeQuestEntry(entry)} after={DescribeJournalQuest(quest)}");
                    }
                }

                if (snapshot.TaskEntries != null)
                {
                    for (int i = 0; i < snapshot.TaskEntries.Length; i++)
                    {
                        NetworkTaskSnapshotEntry entry = snapshot.TaskEntries[i];
                        Quest quest = ResolveQuestIdentity(entry.QuestIdString, entry.QuestHash);
                        if (quest == null || !quest.Contains(entry.TaskId))
                        {
                            LogQuestWarning($"snapshot task entry skipped: quest/task not resolved {DescribeTaskEntry(entry)}");
                            continue;
                        }

                        TaskKey key = new TaskKey(quest.Id.Hash, entry.TaskId);
                        m_Journal.TaskEntries[key] = new TaskEntry
                        {
                            State = (State)entry.State,
                            Value = entry.Value
                        };

                        changedQuests.Add(quest);
                        changedTasks.Add((quest, entry.TaskId));
                        LogQuestSync($"snapshot task entry applied {DescribeTaskEntry(entry)} after={DescribeJournalTask(quest, entry.TaskId)}");
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
            finally
            {
                m_SuppressInterception = previousSuppressInterception;
            }

            LogQuestSync(
                $"ApplySnapshot end changedQuests={changedQuests.Count} changedTasks={changedTasks.Count} " +
                $"afterQuests={m_Journal.QuestEntries.Count} afterTasks={m_Journal.TaskEntries.Count}");
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
            LogQuestSync(
                $"observed local GC2 journal event action={action} quest={DescribeQuest(quest, quest != null ? quest.Id.Hash : 0)} " +
                $"task={taskId} value={value:0.###} {DescribeJournalQuest(quest)} {DescribeJournalTask(quest, taskId)}");

            if (m_SuppressInterception)
            {
                LogQuestSync($"not forwarding local GC2 journal event because interception is suppressed action={action}");
                return;
            }

            if (!m_IsLocalClient || m_IsServer)
            {
                LogQuestSync($"not forwarding local GC2 journal event because controller is not a non-server local client local={m_IsLocalClient} server={m_IsServer} action={action}");
                return;
            }

            if (!m_AutoForwardProfiledJournalChanges)
            {
                LogQuestSync($"not forwarding local GC2 journal event because auto-forward is disabled action={action}");
                return;
            }

            if (m_Profile == null)
            {
                LogQuestWarning($"not forwarding local GC2 journal event because profile is missing action={action}");
                return;
            }

            int questHash = quest != null ? quest.Id.Hash : 0;
            if (!m_Profile.TryGetBinding(quest, questHash, out NetworkQuestBinding binding))
            {
                LogQuestSync($"not forwarding local GC2 journal event because quest is not bound action={action} quest={DescribeQuest(quest, questHash)}");
                return;
            }

            if (!binding.AllowClientWrites || !binding.AutoForwardJournalChanges)
            {
                LogQuestSync(
                    $"not forwarding local GC2 journal event because binding disallows it action={action} " +
                    $"allowClientWrites={binding.AllowClientWrites} autoForward={binding.AutoForwardJournalChanges}");
                return;
            }

            if ((binding.AllowedActions & NetworkQuestProfile.GetActionMask(action)) == 0)
            {
                LogQuestSync($"not forwarding local GC2 journal event because binding disallows action={action} allowed={binding.AllowedActions}");
                return;
            }

            LogQuestSync($"forwarding local GC2 journal event action={action} share={binding.ShareMode}");
            RequestQuestAction(action, quest, taskId, value, binding.ShareMode, string.Empty);
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
