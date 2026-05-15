#if GC2_DIALOGUE
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking.Security;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Dialogue;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Dialogue
{
    [AddComponentMenu("Game Creator/Network/Dialogue/Network Dialogue Controller")]
    [DefaultExecutionOrder(ApplicationManager.EXECUTION_ORDER_DEFAULT + 5)]
    public class NetworkDialogueController : MonoBehaviour
    {
        [Serializable]
        private struct PendingDialogueRequest
        {
            public NetworkDialogueRequest Request;
            public float SentTime;
        }

        [Header("References")]
        [SerializeField] private GameCreator.Runtime.Dialogue.Dialogue m_Dialogue;
        [SerializeField] private NetworkCharacter m_NetworkCharacter;

        [Header("Network Settings")]
        [SerializeField] private NetworkDialogueAuthorityMode m_AuthorityMode = NetworkDialogueAuthorityMode.PlayerOwned;
        [SerializeField] private bool m_OptimisticUpdates;
        [SerializeField] private bool m_UseAutomaticNetworkId = true;
        [SerializeField] private uint m_ManualNetworkId;
        [SerializeField] private string m_NetworkIdSalt = string.Empty;

        [Header("Sync Settings")]
        [SerializeField] private float m_FullSyncInterval = 5f;

        [Header("Validation")]
        [SerializeField] private bool m_LogRejections;

        [Header("Debug")]
        [SerializeField] private bool m_LogAllChanges;

        public event Action<NetworkDialogueRequest> OnDialogueRequested;
        public event Action<NetworkDialogueBroadcast> OnDialogueApplied;
        public event Action<DialogueRejectionReason, string> OnDialogueRejected;

        private bool m_IsServer;
        private bool m_IsLocalClient;
        private bool m_IsRemoteClient;

        private ushort m_NextRequestId = 1;
        private ushort m_LastIssuedRequestId = 1;

        private bool m_IsRegistered;
        private uint m_RegisteredNetworkId;

        private bool m_SuppressInterception;
        private bool m_AuthoritativePlaybackActive;
        private uint m_AuthoritativePlaybackToken;
        private bool m_IsPlaying;
        private int m_CurrentNodeId = Content.NODE_INVALID;
        private Args m_LastArgs;
        private float m_LastFullSync;
        private float m_LastExplicitStopTime = -999f;
        private uint m_RuntimeStandaloneNetworkId;

        private readonly Dictionary<ulong, PendingDialogueRequest> m_PendingRequests = new(16);
        private readonly Dictionary<uint, float> m_RecentlyAppliedCorrelations = new(16);
        private readonly HashSet<uint> m_PendingAuthoritativeStartTokens = new();

        public uint NetworkId => ResolveNetworkId();
        public NetworkCharacter NetworkCharacter => m_NetworkCharacter;
        public NetworkDialogueAuthorityMode AuthorityMode => m_AuthorityMode;
        public bool RequiresTargetOwnership => m_AuthorityMode == NetworkDialogueAuthorityMode.PlayerOwned;

        public bool IsServer => m_IsServer;
        public bool IsLocalClient => m_IsLocalClient;
        public bool IsRemoteClient => m_IsRemoteClient;
        internal bool IsApplyingAuthoritativeChange => m_SuppressInterception;

        internal GameCreator.Runtime.Dialogue.Dialogue DialogueComponent => m_Dialogue;

        private void Reset()
        {
            ResolveReferences(true);
        }

        private void OnValidate()
        {
            ResolveReferences(true);
            m_RuntimeStandaloneNetworkId = ResolveStandaloneNetworkId();
        }

        private void Awake()
        {
            ResolveReferences(true);
            m_RuntimeStandaloneNetworkId = ResolveStandaloneNetworkId();
            if (m_Dialogue == null)
            {
                Debug.LogWarning("[NetworkDialogueController] Missing Dialogue reference. Assign a Dialogue component in the inspector.");
            }
        }

        private void OnEnable()
        {
            if (m_Dialogue == null) return;

            m_Dialogue.EventStart += OnLocalDialogueStart;
            m_Dialogue.EventFinish += OnLocalDialogueFinish;
            m_Dialogue.EventStartNext += OnLocalStartNext;
            m_Dialogue.EventFinishNext += OnLocalFinishNext;
        }

        private void OnDisable()
        {
            if (m_Dialogue != null)
            {
                m_Dialogue.EventStart -= OnLocalDialogueStart;
                m_Dialogue.EventFinish -= OnLocalDialogueFinish;
                m_Dialogue.EventStartNext -= OnLocalStartNext;
                m_Dialogue.EventFinishNext -= OnLocalFinishNext;
            }

            UnregisterFromManager();
        }

        private void Update()
        {
            EnsureRegisteredWithManager();
            CleanupPendingRequests();

            if (m_Dialogue == null) return;
            if (!m_IsServer) return;

            float now = Time.time;
            if (m_FullSyncInterval > 0f && now - m_LastFullSync >= m_FullSyncInterval)
            {
                NetworkDialogueManager.Instance?.BroadcastFullSnapshot(CaptureFullSnapshot());
                m_LastFullSync = now;
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
                Debug.Log($"[NetworkDialogueController] {gameObject.name} initialized as {role}");
            }
        }

        public void RequestPlay(Args args = null)
        {
            RequestDialogueAction(DialogueActionType.Play, Content.NODE_INVALID, args);
        }

        public void RequestPlay(Args args, uint actorNetworkId)
        {
            RequestDialogueAction(DialogueActionType.Play, Content.NODE_INVALID, args, actorNetworkId);
        }

        public void RequestStop()
        {
            RequestDialogueAction(DialogueActionType.Stop);
        }

        public void RequestStop(uint actorNetworkId)
        {
            RequestDialogueAction(DialogueActionType.Stop, Content.NODE_INVALID, null, actorNetworkId);
        }

        public void RequestContinue()
        {
            RequestDialogueAction(DialogueActionType.Continue);
        }

        public void RequestContinue(uint actorNetworkId)
        {
            RequestDialogueAction(DialogueActionType.Continue, Content.NODE_INVALID, null, actorNetworkId);
        }

        public void RequestChoose(int choiceNodeId)
        {
            RequestDialogueAction(DialogueActionType.Choose, choiceNodeId);
        }

        public void RequestChoose(int choiceNodeId, uint actorNetworkId)
        {
            RequestDialogueAction(DialogueActionType.Choose, choiceNodeId, null, actorNetworkId);
        }

        public void RequestChooseIndex(int oneBasedIndex)
        {
            RequestChooseIndex(oneBasedIndex, 0);
        }

        public void RequestChooseIndex(int oneBasedIndex, uint actorNetworkId)
        {
            if (!TryResolveChoiceNodeByIndex(oneBasedIndex, out int choiceNodeId))
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning($"[NetworkDialogueController] Cannot resolve dialogue choice index {oneBasedIndex}");
                }

                OnDialogueRejected?.Invoke(DialogueRejectionReason.InvalidAction, "Cannot resolve dialogue choice index");
                return;
            }

            RequestChoose(choiceNodeId, actorNetworkId);
        }

        internal void RequestPlayFromPatch(Args args)
        {
            RequestDialogueAction(DialogueActionType.Play, Content.NODE_INVALID, args);
        }

        internal void RequestStopFromPatch()
        {
            RequestDialogueAction(DialogueActionType.Stop);
        }

        internal void RequestContinueFromPatch()
        {
            RequestDialogueAction(DialogueActionType.Continue);
        }

        internal void RequestChooseFromPatch(int choiceNodeId)
        {
            RequestDialogueAction(DialogueActionType.Choose, choiceNodeId);
        }

        private void RequestDialogueAction(
            DialogueActionType action,
            int choiceNodeId = Content.NODE_INVALID,
            Args args = null,
            uint requesterActorNetworkId = 0)
        {
            if (m_IsRemoteClient)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning("[NetworkDialogueController] Cannot request dialogue changes from a remote proxy");
                }

                return;
            }

            uint targetNetworkId = NetworkId;
            if (targetNetworkId == 0)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning("[NetworkDialogueController] Missing target NetworkId; cannot send dialogue request");
                }

                OnDialogueRejected?.Invoke(DialogueRejectionReason.TargetNotFound, "Missing target NetworkId");
                return;
            }

            uint actorNetworkId = requesterActorNetworkId != 0
                ? requesterActorNetworkId
                : ResolveRequesterNetworkId(args);

            if (actorNetworkId == 0)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning("[NetworkDialogueController] Missing requester actor NetworkId; cannot send dialogue request");
                }

                OnDialogueRejected?.Invoke(DialogueRejectionReason.TargetNotFound, "Missing requester actor NetworkId");
                return;
            }

            if (action == DialogueActionType.Choose && choiceNodeId == Content.NODE_INVALID)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning("[NetworkDialogueController] Choose request is missing choice node id");
                }

                OnDialogueRejected?.Invoke(DialogueRejectionReason.InvalidAction, "Choose request requires a choice node id");
                return;
            }

            string dialogueId = GetDialogueIdString();
            IdString dialogueIdentity = new IdString(dialogueId);

            var request = new NetworkDialogueRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = actorNetworkId,
                CorrelationId = NetworkCorrelation.Compose(actorNetworkId, m_LastIssuedRequestId),
                TargetNetworkId = targetNetworkId,
                Action = action,
                DialogueHash = dialogueIdentity.Hash,
                DialogueIdString = dialogueIdentity.String,
                ChoiceNodeId = choiceNodeId,
                SelfNetworkId = ResolveSelfNetworkId(args, actorNetworkId),
                ArgsTargetNetworkId = requesterActorNetworkId != 0
                    ? targetNetworkId
                    : ResolveArgsTargetNetworkId(args, targetNetworkId)
            };

            NetworkDialogueManager manager = NetworkDialogueManager.Instance;
            if (!m_IsServer && manager == null)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning("[NetworkDialogueController] NetworkDialogueManager instance not found");
                }

                OnDialogueRejected?.Invoke(DialogueRejectionReason.TargetNotFound, "NetworkDialogueManager missing");
                return;
            }

            m_PendingRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] =
                new PendingDialogueRequest
                {
                    Request = request,
                    SentTime = Time.time
                };

            OnDialogueRequested?.Invoke(request);

            if (m_IsServer)
            {
                _ = ProcessLocalServerRequestAsync(request);
            }
            else
            {
                if (m_OptimisticUpdates)
                {
                    _ = ApplyAuthoritativeActionAsync(
                        request.Action,
                        request.ChoiceNodeId,
                        request.ActorNetworkId,
                        request.SelfNetworkId,
                        request.ArgsTargetNetworkId);
                }

                manager.SendDialogueRequest(request);
            }
        }

        private async Task ProcessLocalServerRequestAsync(NetworkDialogueRequest request)
        {
            NetworkDialogueResponse response = await ProcessDialogueRequestAsync(request, request.ActorNetworkId);
            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            ReceiveDialogueResponse(response);
        }

        public async Task<NetworkDialogueResponse> ProcessDialogueRequestAsync(NetworkDialogueRequest request, uint senderClientId)
        {
            if (!Enum.IsDefined(typeof(DialogueActionType), request.Action))
            {
                return CreateRejectedResponse(request, DialogueRejectionReason.InvalidAction, "Unknown dialogue action");
            }

            if (!MatchesDialogueIdentity(request, out string identityError))
            {
                return CreateRejectedResponse(request, DialogueRejectionReason.IdentityMismatch, identityError);
            }

            bool applied;
            try
            {
                applied = await ApplyAuthoritativeActionAsync(
                    request.Action,
                    request.ChoiceNodeId,
                    request.ActorNetworkId,
                    request.SelfNetworkId,
                    request.ArgsTargetNetworkId);
            }
            catch (Exception exception)
            {
                return CreateRejectedResponse(request, DialogueRejectionReason.Exception, exception.Message);
            }

            if (!applied)
            {
                return CreateRejectedResponse(request, DialogueRejectionReason.InvalidState, "Dialogue action rejected by runtime state");
            }

            NetworkDialogueResponse response = BuildSuccessResponse(request);

            if (m_IsServer)
            {
                NetworkDialogueBroadcast broadcast = BuildBroadcast(request);
                NetworkDialogueManager.Instance?.BroadcastDialogueChange(broadcast);
            }

            if (m_LogAllChanges)
            {
                Debug.Log($"[NetworkDialogueController] Applied {request.Action} (choiceNode={request.ChoiceNodeId}) sender={senderClientId}");
            }

            return response;
        }

        public void ReceiveDialogueResponse(NetworkDialogueResponse response)
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
                    Debug.LogWarning($"[NetworkDialogueController] Dialogue request rejected: {response.RejectionReason} ({response.Error})");
                }

                OnDialogueRejected?.Invoke(response.RejectionReason, response.Error);
                return;
            }

            if (!m_IsServer)
            {
                bool alreadyApplied = response.CorrelationId != 0 &&
                    m_RecentlyAppliedCorrelations.ContainsKey(response.CorrelationId);

                if (response.CorrelationId != 0)
                {
                    m_RecentlyAppliedCorrelations[response.CorrelationId] = Time.time;
                }

                if (!m_OptimisticUpdates && !alreadyApplied)
                {
                    _ = ApplyResponseStateAsync(response);
                }
            }
        }

        public void ReceiveDialogueChangeBroadcast(NetworkDialogueBroadcast broadcast)
        {
            if (broadcast.NetworkId != NetworkId) return;
            if (m_IsServer) return;

            if (broadcast.CorrelationId != 0 && m_RecentlyAppliedCorrelations.Remove(broadcast.CorrelationId))
            {
                return;
            }

            if (HasPendingRequestForCorrelation(broadcast.ActorNetworkId, broadcast.CorrelationId))
            {
                m_RecentlyAppliedCorrelations[broadcast.CorrelationId] = Time.time;
            }

            _ = ApplyBroadcastAsync(broadcast);
        }

        public NetworkDialogueSnapshot CaptureFullSnapshot()
        {
            List<int> visitedNodes = new List<int>(m_Dialogue.Story.Visits.Nodes.Count);
            foreach (int nodeId in m_Dialogue.Story.Visits.Nodes)
            {
                visitedNodes.Add(nodeId);
            }

            List<string> visitedTags = new List<string>(m_Dialogue.Story.Visits.Tags.Count);
            foreach (IdString tag in m_Dialogue.Story.Visits.Tags)
            {
                visitedTags.Add(tag.String);
            }

            string dialogueId = GetDialogueIdString();
            IdString dialogueIdentity = new IdString(dialogueId);

            return new NetworkDialogueSnapshot
            {
                NetworkId = NetworkId,
                ServerTime = Time.time,
                DialogueHash = dialogueIdentity.Hash,
                DialogueIdString = dialogueIdentity.String,
                IsPlaying = m_IsPlaying,
                IsVisited = m_Dialogue.Story.Visits.IsVisited,
                CurrentNodeId = m_CurrentNodeId,
                VisitedNodeIds = visitedNodes.ToArray(),
                VisitedTagIds = visitedTags.ToArray()
            };
        }

        public void ReceiveFullSnapshot(NetworkDialogueSnapshot snapshot)
        {
            if (snapshot.NetworkId != NetworkId) return;
            ApplySnapshot(snapshot);
        }

        private async Task ApplyBroadcastAsync(NetworkDialogueBroadcast broadcast)
        {
            if (!MatchesDialogueIdentity(broadcast.DialogueHash, broadcast.DialogueIdString))
            {
                return;
            }

            await ApplyAuthoritativeActionAsync(
                broadcast.Action,
                broadcast.ChoiceNodeId,
                broadcast.ActorNetworkId,
                0,
                0);

            if (broadcast.CurrentNodeId != Content.NODE_INVALID)
            {
                m_CurrentNodeId = broadcast.CurrentNodeId;
            }

            m_IsPlaying = broadcast.IsPlaying;
            OnDialogueApplied?.Invoke(broadcast);
        }

        private async Task ApplyResponseStateAsync(NetworkDialogueResponse response)
        {
            if (!MatchesDialogueIdentity(response.DialogueHash, response.DialogueIdString))
            {
                return;
            }

            await ApplyAuthoritativeActionAsync(response.Action, Content.NODE_INVALID, response.ActorNetworkId, 0, 0);
            if (response.CurrentNodeId != Content.NODE_INVALID)
            {
                m_CurrentNodeId = response.CurrentNodeId;
            }

            m_IsPlaying = response.IsPlaying;
        }

        private async Task<bool> ApplyAuthoritativeActionAsync(
            DialogueActionType action,
            int choiceNodeId,
            uint actorNetworkId,
            uint selfNetworkId,
            uint argsTargetNetworkId)
        {
            bool previousSuppress = m_SuppressInterception;
            m_SuppressInterception = true;

            try
            {
                switch (action)
                {
                    case DialogueActionType.Play:
                        return await StartDialogueAsync(actorNetworkId, selfNetworkId, argsTargetNetworkId);

                    case DialogueActionType.Stop:
                        if (m_Dialogue == null) return false;
                        m_LastExplicitStopTime = Time.time;
                        m_Dialogue.Stop();
                        return true;

                    case DialogueActionType.Continue:
                        if (m_Dialogue == null) return false;
                        m_Dialogue.Story.Continue();
                        return true;

                    case DialogueActionType.Choose:
                        return TryChooseNode(choiceNodeId);

                    default:
                        return false;
                }
            }
            finally
            {
                m_SuppressInterception = previousSuppress;
            }
        }

        private async Task<bool> StartDialogueAsync(uint actorNetworkId, uint selfNetworkId, uint argsTargetNetworkId)
        {
            if (m_Dialogue == null) return false;

            Args args = BuildArgs(actorNetworkId, selfNetworkId, argsTargetNetworkId);
            m_LastArgs = args;
            m_IsPlaying = true;

            uint playbackToken = ++m_AuthoritativePlaybackToken;
            m_AuthoritativePlaybackActive = true;
            m_PendingAuthoritativeStartTokens.Add(playbackToken);

            _ = PlayDialogueAuthoritativelyAsync(args, playbackToken);
            await Task.Yield();
            return true;
        }

        private async Task PlayDialogueAuthoritativelyAsync(Args args, uint playbackToken)
        {
            try
            {
                await m_Dialogue.Play(args);
            }
            catch (Exception exception)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning($"[NetworkDialogueController] Dialogue play failed: {exception.Message}", this);
                }
            }
            finally
            {
                if (m_AuthoritativePlaybackToken == playbackToken)
                {
                    m_AuthoritativePlaybackActive = false;
                }

                m_PendingAuthoritativeStartTokens.Remove(playbackToken);
            }
        }

        private Args BuildArgs(uint actorNetworkId, uint selfNetworkId, uint argsTargetNetworkId)
        {
            GameObject self = ResolveGameObject(selfNetworkId);
            if (self == null)
            {
                Character actorCharacter = ResolveCharacter(actorNetworkId);
                if (actorCharacter != null) self = actorCharacter.gameObject;
            }

            GameObject target = ResolveGameObject(argsTargetNetworkId);

            if (self == null) self = gameObject;
            if (target == null) target = gameObject;

            return new Args(self, target);
        }

        private static Character ResolveCharacter(uint networkId)
        {
            if (networkId == 0) return null;

            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            return bridge != null ? bridge.ResolveCharacter(networkId) : null;
        }

        private static GameObject ResolveGameObject(uint networkId)
        {
            Character character = ResolveCharacter(networkId);
            if (character != null) return character.gameObject;

            NetworkDialogueController[] controllers = FindObjectsByType<NetworkDialogueController>(FindObjectsSortMode.None);
            for (int i = 0; i < controllers.Length; i++)
            {
                NetworkDialogueController controller = controllers[i];
                if (controller == null || controller.NetworkId != networkId) continue;
                return controller.gameObject;
            }

            return null;
        }

        private bool TryChooseNode(int choiceNodeId)
        {
            if (m_Dialogue == null || choiceNodeId == Content.NODE_INVALID)
            {
                return false;
            }

            if (m_CurrentNodeId == Content.NODE_INVALID)
            {
                return false;
            }

            Node currentNode = m_Dialogue.Story.Content.Get(m_CurrentNodeId);
            if (currentNode?.NodeType is not NodeTypeChoice choiceType)
            {
                return false;
            }

            choiceType.Choose(choiceNodeId);
            return true;
        }

        private bool TryResolveChoiceNodeByIndex(int oneBasedIndex, out int choiceNodeId)
        {
            choiceNodeId = Content.NODE_INVALID;
            if (oneBasedIndex < 1) return false;
            if (m_Dialogue == null || m_CurrentNodeId == Content.NODE_INVALID) return false;

            Node currentNode = m_Dialogue.Story.Content.Get(m_CurrentNodeId);
            if (currentNode?.NodeType is not NodeTypeChoice choiceType) return false;

            Args args = m_LastArgs ?? BuildArgs(NetworkId, 0, 0);
            List<int> choices = choiceType.GetChoices(m_Dialogue.Story, m_CurrentNodeId, args, false);
            int choiceIndex = oneBasedIndex - 1;
            if (choiceIndex < 0 || choiceIndex >= choices.Count) return false;

            choiceNodeId = choices[choiceIndex];
            return choiceNodeId != Content.NODE_INVALID;
        }

        private NetworkDialogueResponse BuildSuccessResponse(in NetworkDialogueRequest request)
        {
            string dialogueId = GetDialogueIdString();
            IdString dialogueIdentity = new IdString(dialogueId);

            return new NetworkDialogueResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                TargetNetworkId = request.TargetNetworkId,
                Action = request.Action,
                Authorized = true,
                Applied = true,
                RejectionReason = DialogueRejectionReason.None,
                DialogueHash = dialogueIdentity.Hash,
                DialogueIdString = dialogueIdentity.String,
                CurrentNodeId = m_CurrentNodeId,
                IsPlaying = m_IsPlaying,
                IsVisited = m_Dialogue.Story.Visits.IsVisited,
                Error = string.Empty
            };
        }

        private NetworkDialogueBroadcast BuildBroadcast(in NetworkDialogueRequest request)
        {
            string dialogueId = GetDialogueIdString();
            IdString dialogueIdentity = new IdString(dialogueId);

            return new NetworkDialogueBroadcast
            {
                NetworkId = NetworkId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Action = request.Action,
                DialogueHash = dialogueIdentity.Hash,
                DialogueIdString = dialogueIdentity.String,
                CurrentNodeId = m_CurrentNodeId,
                ChoiceNodeId = request.ChoiceNodeId,
                IsPlaying = m_IsPlaying,
                IsVisited = m_Dialogue.Story.Visits.IsVisited,
                ServerTime = Time.time
            };
        }

        private static NetworkDialogueResponse CreateRejectedResponse(
            in NetworkDialogueRequest request,
            DialogueRejectionReason reason,
            string error)
        {
            return new NetworkDialogueResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                TargetNetworkId = request.TargetNetworkId,
                Action = request.Action,
                Authorized = false,
                Applied = false,
                RejectionReason = reason,
                DialogueHash = request.DialogueHash,
                DialogueIdString = request.DialogueIdString,
                CurrentNodeId = Content.NODE_INVALID,
                IsPlaying = false,
                IsVisited = false,
                Error = error
            };
        }

        private bool MatchesDialogueIdentity(in NetworkDialogueRequest request, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrEmpty(request.DialogueIdString))
            {
                return true;
            }

            IdString requested = new IdString(request.DialogueIdString);
            if (request.DialogueHash != 0 && requested.Hash != request.DialogueHash)
            {
                error = "Dialogue hash does not match dialogue id";
                return false;
            }

            string localDialogueId = GetDialogueIdString();
            IdString local = new IdString(localDialogueId);
            if (local.Hash != requested.Hash)
            {
                error = $"Dialogue identity mismatch expected={local.String} requested={requested.String}";
                return false;
            }

            return true;
        }

        private bool MatchesDialogueIdentity(int dialogueHash, string dialogueIdString)
        {
            if (string.IsNullOrEmpty(dialogueIdString)) return true;

            IdString requested = new IdString(dialogueIdString);
            if (dialogueHash != 0 && requested.Hash != dialogueHash)
            {
                return false;
            }

            string localDialogueId = GetDialogueIdString();
            return new IdString(localDialogueId).Hash == requested.Hash;
        }

        private string GetDialogueIdString()
        {
            if (m_Dialogue == null)
            {
                return gameObject.name;
            }

            return m_Dialogue.name;
        }

        private void ApplySnapshot(NetworkDialogueSnapshot snapshot)
        {
            bool previousSuppress = m_SuppressInterception;
            m_SuppressInterception = true;

            try
            {
                Visits visits = m_Dialogue.Story.Visits;
                visits.Clear();
                visits.IsVisited = snapshot.IsVisited;

                if (snapshot.VisitedNodeIds != null)
                {
                    for (int i = 0; i < snapshot.VisitedNodeIds.Length; i++)
                    {
                        visits.Nodes.Add(snapshot.VisitedNodeIds[i]);
                    }
                }

                if (snapshot.VisitedTagIds != null)
                {
                    for (int i = 0; i < snapshot.VisitedTagIds.Length; i++)
                    {
                        string rawTag = snapshot.VisitedTagIds[i];
                        if (string.IsNullOrEmpty(rawTag)) continue;
                        visits.Tags.Add(new IdString(rawTag));
                    }
                }

                m_CurrentNodeId = snapshot.CurrentNodeId;
                m_IsPlaying = snapshot.IsPlaying;
            }
            finally
            {
                m_SuppressInterception = previousSuppress;
            }
        }

        private static uint ExtractNetworkId(GameObject gameObject)
        {
            if (gameObject == null) return 0;

            NetworkCharacter networkCharacter = gameObject.GetComponent<NetworkCharacter>();
            if (networkCharacter == null)
            {
                networkCharacter = gameObject.GetComponentInParent<NetworkCharacter>();
            }

            if (networkCharacter != null && networkCharacter.NetworkId != 0)
            {
                return networkCharacter.NetworkId;
            }

            NetworkDialogueController dialogueController = gameObject.GetComponent<NetworkDialogueController>();
            if (dialogueController == null)
            {
                dialogueController = gameObject.GetComponentInParent<NetworkDialogueController>();
            }

            return dialogueController != null ? dialogueController.NetworkId : 0;
        }

        private uint ResolveRequesterNetworkId(Args args)
        {
            uint actorNetworkId = ExtractNetworkCharacterId(args != null ? args.Self : null);
            if (actorNetworkId != 0) return actorNetworkId;

            actorNetworkId = ExtractNetworkCharacterId(args != null ? args.Target : null);
            if (actorNetworkId != 0) return actorNetworkId;

            actorNetworkId = ExtractNetworkCharacterId(ShortcutPlayer.Instance != null
                ? ShortcutPlayer.Instance.gameObject
                : null);
            if (actorNetworkId != 0) return actorNetworkId;

            return NetworkId;
        }

        private static uint ResolveSelfNetworkId(Args args, uint actorNetworkId)
        {
            uint selfNetworkId = ExtractNetworkCharacterId(args != null ? args.Self : null);
            return selfNetworkId != 0 ? selfNetworkId : actorNetworkId;
        }

        private static uint ResolveArgsTargetNetworkId(Args args, uint targetNetworkId)
        {
            uint argsTargetNetworkId = ExtractNetworkId(args != null ? args.Target : null);
            return argsTargetNetworkId != 0 ? argsTargetNetworkId : targetNetworkId;
        }

        private static uint ExtractNetworkCharacterId(GameObject gameObject)
        {
            if (gameObject == null) return 0;

            NetworkCharacter networkCharacter = gameObject.GetComponent<NetworkCharacter>();
            if (networkCharacter == null)
            {
                networkCharacter = gameObject.GetComponentInParent<NetworkCharacter>();
            }

            return networkCharacter != null ? networkCharacter.NetworkId : 0;
        }

        private uint ResolveNetworkId()
        {
            if (m_NetworkCharacter != null && m_NetworkCharacter.NetworkId != 0)
            {
                return m_NetworkCharacter.NetworkId;
            }

            if (m_RuntimeStandaloneNetworkId == 0)
            {
                m_RuntimeStandaloneNetworkId = ResolveStandaloneNetworkId();
            }

            return m_RuntimeStandaloneNetworkId;
        }

        private uint ResolveStandaloneNetworkId()
        {
            if (!m_UseAutomaticNetworkId)
            {
                return m_ManualNetworkId == 0 ? 1u : m_ManualNetworkId;
            }

            string scenePath = gameObject.scene.path;
            string hierarchyPath = BuildHierarchyPath(transform);
            string key = $"{scenePath}|{hierarchyPath}|Dialogue|{m_NetworkIdSalt}";
            uint stableHash = unchecked((uint)StableHashUtility.GetStableHash(key));

            return stableHash == 0 ? (uint)(Mathf.Abs(transform.GetInstanceID()) + 1) : stableHash;
        }

        private static string BuildHierarchyPath(Transform current)
        {
            if (current == null) return string.Empty;

            string path = current.name;
            Transform parent = current.parent;
            while (parent != null)
            {
                path = $"{parent.name}/{path}";
                parent = parent.parent;
            }

            return path;
        }

        private static ulong GetPendingKey(uint actorNetworkId, uint correlationId, ushort requestId)
        {
            uint pendingCorrelation = correlationId != 0 ? correlationId : requestId;
            return ((ulong)actorNetworkId << 32) | pendingCorrelation;
        }

        private bool HasPendingRequestForCorrelation(uint actorNetworkId, uint correlationId)
        {
            if (correlationId == 0) return false;
            return m_PendingRequests.ContainsKey(GetPendingKey(actorNetworkId, correlationId, 0));
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

        private void CleanupPendingRequests()
        {
            float now = Time.time;
            const float timeout = 8f;

            List<ulong> expiredRequests = null;
            foreach (KeyValuePair<ulong, PendingDialogueRequest> pair in m_PendingRequests)
            {
                if (now - pair.Value.SentTime <= timeout) continue;

                expiredRequests ??= new List<ulong>(4);
                expiredRequests.Add(pair.Key);
            }

            if (expiredRequests != null)
            {
                for (int i = 0; i < expiredRequests.Count; i++)
                {
                    if (m_PendingRequests.TryGetValue(expiredRequests[i], out PendingDialogueRequest pending))
                    {
                        OnDialogueRejected?.Invoke(DialogueRejectionReason.Exception, $"Dialogue request timed out: {pending.Request.Action}");
                    }

                    m_PendingRequests.Remove(expiredRequests[i]);
                }
            }

            List<uint> staleCorrelations = null;
            foreach (KeyValuePair<uint, float> pair in m_RecentlyAppliedCorrelations)
            {
                if (now - pair.Value <= timeout) continue;

                staleCorrelations ??= new List<uint>(4);
                staleCorrelations.Add(pair.Key);
            }

            if (staleCorrelations != null)
            {
                for (int i = 0; i < staleCorrelations.Count; i++)
                {
                    m_RecentlyAppliedCorrelations.Remove(staleCorrelations[i]);
                }
            }
        }

        private void EnsureRegisteredWithManager()
        {
            NetworkDialogueManager manager = NetworkDialogueManager.Instance;
            if (manager == null) return;

            if (m_NetworkCharacter == null)
            {
                ResolveReferences(true);
            }

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

        internal static NetworkDialogueController ResolveForDialogueComponent(GameCreator.Runtime.Dialogue.Dialogue dialogue)
        {
            if (dialogue == null) return null;

            NetworkDialogueController direct = dialogue.GetComponent<NetworkDialogueController>();
            if (direct != null && direct.enabled && ReferenceEquals(direct.DialogueComponent, dialogue))
            {
                return direct;
            }

            NetworkDialogueController[] controllers = FindObjectsByType<NetworkDialogueController>(FindObjectsSortMode.None);
            for (int i = 0; i < controllers.Length; i++)
            {
                NetworkDialogueController controller = controllers[i];
                if (controller == null || !controller.enabled) continue;
                if (!ReferenceEquals(controller.DialogueComponent, dialogue)) continue;
                return controller;
            }

            return null;
        }

        private void UnregisterFromManager()
        {
            if (!m_IsRegistered) return;

            NetworkDialogueManager.Instance?.UnregisterController(m_RegisteredNetworkId);
            m_IsRegistered = false;
            m_RegisteredNetworkId = 0;
        }

        private void OnLocalDialogueStart()
        {
            m_IsPlaying = true;

            if (ConsumeAuthoritativeStartEvent()) return;
            if (m_SuppressInterception) return;
            if (m_IsServer) return;
            if (!m_IsLocalClient || m_IsRemoteClient) return;

            RequestDialogueAction(DialogueActionType.Play);
        }

        private void OnLocalDialogueFinish()
        {
            m_IsPlaying = false;

            if (m_SuppressInterception) return;

            if (m_AuthoritativePlaybackActive)
            {
                m_AuthoritativePlaybackActive = false;
                if (m_IsServer)
                {
                    BroadcastServerDialogueFinishIfNeeded();
                }

                return;
            }

            if (m_IsServer)
            {
                BroadcastServerDialogueFinishIfNeeded();
                return;
            }

            if (!m_IsLocalClient || m_IsRemoteClient) return;
            RequestDialogueAction(DialogueActionType.Stop);
        }

        private bool ConsumeAuthoritativeStartEvent()
        {
            if (m_PendingAuthoritativeStartTokens.Count == 0)
            {
                return false;
            }

            uint token = 0;
            foreach (uint pendingToken in m_PendingAuthoritativeStartTokens)
            {
                token = pendingToken;
                break;
            }

            m_PendingAuthoritativeStartTokens.Remove(token);

            if (m_LogAllChanges)
            {
                Debug.Log($"[NetworkDialogueController] Ignored authoritative Dialogue start event on {name}", this);
            }

            return true;
        }

        private void BroadcastServerDialogueFinishIfNeeded()
        {
            if (Time.time - m_LastExplicitStopTime <= 0.25f)
            {
                return;
            }

            NetworkDialogueManager manager = NetworkDialogueManager.Instance;
            if (manager == null || m_Dialogue == null) return;

            string dialogueId = GetDialogueIdString();
            IdString dialogueIdentity = new IdString(dialogueId);

            manager.BroadcastDialogueChange(new NetworkDialogueBroadcast
            {
                NetworkId = NetworkId,
                ActorNetworkId = NetworkId,
                CorrelationId = 0,
                Action = DialogueActionType.Stop,
                DialogueHash = dialogueIdentity.Hash,
                DialogueIdString = dialogueIdentity.String,
                CurrentNodeId = m_CurrentNodeId,
                ChoiceNodeId = Content.NODE_INVALID,
                IsPlaying = false,
                IsVisited = m_Dialogue.Story.Visits.IsVisited,
                ServerTime = Time.time
            });
        }

        private void OnLocalStartNext(int nodeId)
        {
            m_CurrentNodeId = nodeId;
        }

        private void OnLocalFinishNext(int nodeId)
        {
            m_CurrentNodeId = nodeId;
        }

        private void ResolveReferences(bool allowParentLookup)
        {
            if (m_Dialogue == null)
            {
                m_Dialogue = GetComponent<GameCreator.Runtime.Dialogue.Dialogue>();
            }

            if (m_NetworkCharacter != null) return;

            m_NetworkCharacter = GetComponent<NetworkCharacter>();
            if (m_NetworkCharacter != null) return;

            if (m_Dialogue != null)
            {
                m_NetworkCharacter = m_Dialogue.GetComponent<NetworkCharacter>();
                if (m_NetworkCharacter == null && allowParentLookup)
                {
                    m_NetworkCharacter = m_Dialogue.GetComponentInParent<NetworkCharacter>();
                }
            }

            if (m_NetworkCharacter == null && allowParentLookup)
            {
                m_NetworkCharacter = GetComponentInParent<NetworkCharacter>();
            }
        }
    }
}
#endif
