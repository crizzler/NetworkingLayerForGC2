using System;
using System.Collections.Generic;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Melee;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Melee
{
    /// <summary>
    /// Transport-neutral authority and state adapter for the optional Free Flow Combat asset.
    /// Free Flow implements <see cref="INetworkFreeFlowCombatReceiver"/> from Assembly-CSharp;
    /// this component therefore remains safe to ship when Free Flow is not installed.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Melee/Network Free Flow Combat Adapter")]
    [DefaultExecutionOrder(-9000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkCharacter))]
    [RequireComponent(typeof(NetworkMeleeController))]
    public sealed class NetworkFreeFlowCombatAdapter : MonoBehaviour
    {
        private const float MinimumPollInterval = 0.02f;
        private const float PendingCounterLifetime = 4f;
        private const float DefaultRangeTolerance = 0.35f;
        private const int LineOfSightCapacity = 24;

        private static readonly Dictionary<uint, NetworkFreeFlowCombatAdapter> s_Adapters = new(64);

        [Min(MinimumPollInterval)]
        [SerializeField] private float m_StatePollInterval = 0.05f;
        [Min(0f)]
        [SerializeField] private float m_ServerRangeTolerance = DefaultRangeTolerance;
        [SerializeField] private bool m_LogAuthority;

        // The complete classification map cannot double as the direct request allow-list: it
        // intentionally contains later Combo nodes which are legal only with their exact node ID.
        private readonly Dictionary<int, NetworkFreeFlowSkillUse> m_SkillUses = new(24);
        private readonly HashSet<int> m_DirectSkillHashes = new();
        private readonly RaycastHit[] m_LineOfSightHits = new RaycastHit[LineOfSightCapacity];

        private NetworkCharacter m_NetworkCharacter;
        private NetworkMeleeController m_MeleeController;
        private NetworkNpcTargetSelector m_TargetSelector;
        private INetworkFreeFlowCombatReceiver m_Receiver;
        private MonoBehaviour m_ReceiverBehaviour;
        private MeleeWeapon m_RegisteredWeapon;
        private uint m_RegisteredNetworkId;
        private NetworkFreeFlowCombatState m_CurrentState;
        private NetworkFreeFlowCombatState m_PendingReplicatedState;
        private bool m_HasPendingReplicatedState;
        private float m_NextPollTime;
        private float m_LastAcceptedCounterTime = float.NegativeInfinity;

        private uint m_PendingCounterTargetId;
        private uint m_PendingCounterRevision;
        private float m_PendingCounterExpiresAt;

        public NetworkCharacter NetworkCharacter => m_NetworkCharacter;
        public NetworkFreeFlowCombatState CurrentState => m_CurrentState;
        public bool HasReceiver => ResolveReceiver() != null;
        public int RegisteredSkillCount => m_SkillUses.Count;
        public int RegisteredDirectSkillCount => m_DirectSkillHashes.Count;

        private void Awake()
        {
            m_NetworkCharacter = GetComponent<NetworkCharacter>();
            m_MeleeController = GetComponent<NetworkMeleeController>();
            m_TargetSelector = GetComponent<NetworkNpcTargetSelector>();
            m_CurrentState = NetworkFreeFlowCombatState.Create(0);
            RefreshSimulationMode();
        }

        private void OnEnable()
        {
            if (m_NetworkCharacter == null) m_NetworkCharacter = GetComponent<NetworkCharacter>();
            if (m_MeleeController == null) m_MeleeController = GetComponent<NetworkMeleeController>();
            if (m_TargetSelector == null) m_TargetSelector = GetComponent<NetworkNpcTargetSelector>();

            if (m_NetworkCharacter != null)
            {
                m_NetworkCharacter.OnRoleAssigned -= OnRoleAssigned;
                m_NetworkCharacter.OnRoleAssigned += OnRoleAssigned;
                m_NetworkCharacter.OnRoleReset -= OnRoleReset;
                m_NetworkCharacter.OnRoleReset += OnRoleReset;
            }

            RegisterIfReady();
            RefreshSimulationMode();
            m_NextPollTime = 0f;
        }

        private void OnDisable()
        {
            if (m_NetworkCharacter != null)
            {
                m_NetworkCharacter.OnRoleAssigned -= OnRoleAssigned;
                m_NetworkCharacter.OnRoleReset -= OnRoleReset;
            }

            Unregister();
            m_Receiver?.SetNetworkSimulation(false, false);
            ClearPendingCounter();
        }

        private void Update()
        {
            RegisterIfReady();
            ResolveReceiver();
            RefreshSimulationMode();
            RefreshSkillRegistrations();

            if (m_HasPendingReplicatedState && m_Receiver != null && !HasNpcAuthority())
            {
                ApplyStateToReceiver(m_PendingReplicatedState);
            }

            if (Time.unscaledTime < m_NextPollTime) return;
            m_NextPollTime = Time.unscaledTime + Mathf.Max(MinimumPollInterval, m_StatePollInterval);

            if (HasNpcAuthority())
            {
                RefreshAuthoritativeState(force: false);
            }
            else if (m_CurrentState.StateVersion != 0 && m_Receiver != null)
            {
                // Reassert authority-owned presentation periodically. Observer-side GC2 events
                // are expected to be gated, but this also corrects accidental local mutations.
                ApplyStateToReceiver(m_CurrentState);
            }
        }

        private void OnRoleAssigned(NetworkCharacter.NetworkRole role)
        {
            InvalidateSkillRegistrations();
            RegisterIfReady();
            RefreshSimulationMode();
            RefreshSkillRegistrations(force: true);
            if (HasNpcAuthority()) RefreshAuthoritativeState(force: true);
        }

        private void OnRoleReset()
        {
            InvalidateSkillRegistrations();
            RefreshSimulationMode();
            ClearPendingCounter();
        }

        /// <summary>
        /// Rebinds the optional receiver immediately after Free Flow finishes creating its
        /// runtime components during weapon equip. This prevents a newly added Runtime,
        /// Processor, or enemy Agent from waiting for the adapter's next Update before the
        /// authenticated player/NPC simulation policy is applied.
        /// </summary>
        public void NotifyFreeFlowRuntimeReady()
        {
            INetworkFreeFlowCombatReceiver receiver = ResolveReceiver();
            if (receiver == null) return;
            if (!isActiveAndEnabled)
            {
                receiver.SetNetworkSimulation(false, false);
                return;
            }

            RefreshSimulationMode();
            RefreshSkillRegistrations(force: true);

            if (m_Receiver == null || HasNpcAuthority()) return;
            if (m_HasPendingReplicatedState)
            {
                ApplyStateToReceiver(m_PendingReplicatedState);
            }
            else if (m_CurrentState.StateVersion != 0)
            {
                ApplyStateToReceiver(m_CurrentState);
            }
        }

        /// <summary>Called by the optional Free Flow runtime before dynamic enemy setup.</summary>
        public NetworkFreeFlowEnemySetupMode GetFreeFlowEnemySetupMode()
        {
            if (m_NetworkCharacter == null) return NetworkFreeFlowEnemySetupMode.Unmanaged;
            if (m_NetworkCharacter.IsPlayerOwnedActor)
            {
                return NetworkFreeFlowEnemySetupMode.PlayerOwned;
            }

            if (!m_NetworkCharacter.IsServerAuthoritativeNPC)
            {
                return NetworkFreeFlowEnemySetupMode.ObserverNpc;
            }

            return HasNpcAuthority()
                ? NetworkFreeFlowEnemySetupMode.AuthoritativeNpc
                : NetworkFreeFlowEnemySetupMode.ObserverNpc;
        }

        /// <summary>
        /// Called before Free Flow consumes a local counter window. Networked counters defer the
        /// mutation until the authenticated skill request reaches authority.
        /// </summary>
        public NetworkFreeFlowCounterPreparation PrepareFreeFlowCounter(
            GameObject target,
            bool forcedByShield)
        {
            // Prepared counter context is strictly one-shot. A new rejected attempt must not
            // leave an older target/revision available for the next unrelated direct Skill.
            ClearPendingCounter();

            // A shield-forced counter needs a validated parry lease from authority. The current
            // optional integration deliberately rejects it instead of trusting a client-side
            // OnDefend callback. Normal open-window counters are supported below.
            if (forcedByShield) return NetworkFreeFlowCounterPreparation.Rejected;

            if (!IsCounterAvailableToLocalPlayer(target, out NetworkFreeFlowCombatAdapter targetAdapter))
                return NetworkFreeFlowCounterPreparation.Rejected;

            m_PendingCounterTargetId = targetAdapter.m_NetworkCharacter.NetworkId;
            m_PendingCounterRevision = targetAdapter.m_CurrentState.StateVersion;
            m_PendingCounterExpiresAt = Time.unscaledTime + PendingCounterLifetime;
            return NetworkFreeFlowCounterPreparation.Prepared;
        }

        /// <summary>
        /// Returns whether the currently replicated counter window belongs to this authenticated
        /// local player. On a Host/Shared authority peer, refreshes the live NPC receiver first so
        /// a telegraph opened later in the frame cannot be rejected from the adapter's older poll.
        /// </summary>
        public bool IsCounterAvailableToLocalPlayer(GameObject target)
        {
            return IsCounterAvailableToLocalPlayer(target, out _);
        }

        private bool IsCounterAvailableToLocalPlayer(
            GameObject target,
            out NetworkFreeFlowCombatAdapter targetAdapter)
        {
            targetAdapter = null;
            if (m_NetworkCharacter == null || !m_NetworkCharacter.IsPlayerOwnedActor ||
                !m_NetworkCharacter.IsLocalPlayer ||
                !m_NetworkCharacter.HasAuthenticatedPlayerOwner ||
                m_NetworkCharacter.NetworkId == 0)
            {
                return false;
            }

            targetAdapter = FindForObject(target);
            if (targetAdapter == null || targetAdapter.m_NetworkCharacter == null ||
                !targetAdapter.m_NetworkCharacter.IsServerAuthoritativeNPC)
            {
                return false;
            }

            // The authority Agent opens its window during Behavior execution, after this
            // adapter's early execution-order poll can already have run. Capture/publish that
            // live state synchronously before comparing the exact assignment and revision.
            if (targetAdapter.HasNpcAuthority())
            {
                targetAdapter.RefreshAuthoritativeState(force: false);
            }

            return targetAdapter.m_CurrentState.IsCounterable &&
                   targetAdapter.m_CurrentState.StateVersion != 0 &&
                   targetAdapter.m_CurrentState.TargetNetworkId ==
                   m_NetworkCharacter.NetworkId;
        }

        /// <summary>
        /// Clears a locally prepared counter when Free Flow aborts before it starts a Skill.
        /// This only affects ephemeral owner-side request context; authority state is untouched.
        /// </summary>
        public void CancelPreparedCounter()
        {
            ClearPendingCounter();
        }

        internal static void DecorateSkillRequest(
            NetworkMeleeController controller,
            ref NetworkSkillRequest request)
        {
            NetworkFreeFlowCombatAdapter adapter = FindForController(controller);
            if (adapter == null || adapter.m_PendingCounterTargetId == 0 ||
                Time.unscaledTime > adapter.m_PendingCounterExpiresAt)
            {
                adapter?.ClearPendingCounter();
                return;
            }

            if (request.TargetNetworkId != adapter.m_PendingCounterTargetId)
            {
                adapter.ClearPendingCounter();
                return;
            }

            adapter.RefreshSkillRegistrations();
            if (!adapter.m_SkillUses.TryGetValue(
                    request.SkillHash,
                    out NetworkFreeFlowSkillUse uses) ||
                (uses & NetworkFreeFlowSkillUse.Counter) == 0)
            {
                // Counter preparation is single-use intent. If the authored counter action was
                // canceled before it emitted a Skill, the next ordinary attack against the same
                // target must not inherit the stale counter revision.
                adapter.ClearPendingCounter();
                return;
            }

            request.ActionFlags |= NetworkMeleeSkillActionFlags.FreeFlowCounter;
            request.ActionStateRevision = adapter.m_PendingCounterRevision;
            adapter.ClearPendingCounter();
        }

        /// <summary>
        /// Resolves the target carried by GC2's active direct-Skill Args when Free Flow has
        /// deliberately cleared Combat.Targets.Primary for a fallback action. This is limited to
        /// an authored fallback Skill and an explicit server-authoritative NPC; authority still
        /// validates the resulting stable ID, liveness, range, and line of sight normally.
        /// </summary>
        internal static uint ResolveFallbackSkillTargetNetworkId(
            NetworkMeleeController controller,
            int skillHash,
            GameObject skillArgsTarget)
        {
            if (controller == null || skillHash == 0 || skillArgsTarget == null) return 0;

            NetworkFreeFlowCombatAdapter actor = FindForController(controller);
            if (actor == null) return 0;

            actor.RefreshSkillRegistrations();
            if (!actor.m_SkillUses.TryGetValue(
                    skillHash,
                    out NetworkFreeFlowSkillUse uses) ||
                (uses & NetworkFreeFlowSkillUse.Fallback) == 0 ||
                !actor.m_DirectSkillHashes.Contains(skillHash))
            {
                return 0;
            }

            NetworkFreeFlowCombatAdapter target = FindForObject(skillArgsTarget);
            NetworkCharacter targetCharacter = target != null
                ? target.m_NetworkCharacter
                : null;
            if (targetCharacter == null ||
                !targetCharacter.IsServerAuthoritativeNPC ||
                targetCharacter.NetworkId == 0)
            {
                return 0;
            }

            return targetCharacter.NetworkId;
        }

        internal static bool ValidateSkillRequest(
            NetworkMeleeController controller,
            MeleeWeapon weapon,
            Skill skill,
            in NetworkSkillRequest request,
            out string details)
        {
            details = string.Empty;
            const NetworkMeleeSkillActionFlags knownFlags =
                NetworkMeleeSkillActionFlags.FreeFlowCounter;
            if ((request.ActionFlags & ~knownFlags) != 0)
            {
                details = $"unknown skill action flags {(byte)request.ActionFlags}";
                return false;
            }

            NetworkFreeFlowCombatAdapter actor = FindForController(controller);
            if (actor != null && actor.ResolveReceiver() == null)
            {
                details = "Network Free Flow adapter is present but its optional runtime receiver is not ready";
                return false;
            }

            bool isFreeFlowWeapon = actor != null && actor.IsFreeFlowWeapon(weapon);
            if (!isFreeFlowWeapon)
            {
                if (request.ActionFlags != NetworkMeleeSkillActionFlags.None)
                {
                    details = "Free Flow action context was supplied for a non-Free-Flow weapon";
                    return false;
                }

                return true;
            }

            actor.RefreshSkillRegistrations();
            bool isDirect = request.ComboNodeId == ComboTree.NODE_INVALID;
            NetworkFreeFlowSkillUse uses = NetworkFreeFlowSkillUse.TargetedAttack;
            bool hasRegisteredUse = actor.m_SkillUses.TryGetValue(
                request.SkillHash,
                out NetworkFreeFlowSkillUse registeredUses);
            if (hasRegisteredUse) uses = registeredUses;
            if (isDirect &&
                (!hasRegisteredUse || !actor.m_DirectSkillHashes.Contains(request.SkillHash)))
            {
                details = "direct Skill is not allow-listed by an authored Free Flow direct-play path";
                return false;
            }

            bool counter = (request.ActionFlags & NetworkMeleeSkillActionFlags.FreeFlowCounter) != 0;
            if (counter)
            {
                // Counter Instructions may either play a direct Skill or execute a real Combo
                // input. In both cases the concrete Skill must have been discovered from the
                // weapon's counter instruction graph; a valid Combo node alone is not proof
                // that the action was an authority-bound counter.
                if (!hasRegisteredUse ||
                    (uses & NetworkFreeFlowSkillUse.Counter) == 0)
                {
                    details = "Skill is not allow-listed as a Free Flow counter";
                    return false;
                }
            }
            else if (isDirect &&
                     (uses & (NetworkFreeFlowSkillUse.TargetedAttack |
                              NetworkFreeFlowSkillUse.Fallback)) == 0)
            {
                details = "counter-only Free Flow Skill requires an authority-bound counter context";
                return false;
            }

            if (!actor.ValidateTargetAndRange(request, uses, counter, out details)) return false;
            return true;
        }

        internal static bool CommitSkillRequest(
            NetworkMeleeController controller,
            in NetworkSkillRequest request,
            out string details)
        {
            details = string.Empty;
            if ((request.ActionFlags & NetworkMeleeSkillActionFlags.FreeFlowCounter) == 0)
            {
                return true;
            }

            NetworkFreeFlowCombatAdapter actor = FindForController(controller);
            NetworkFreeFlowCombatAdapter target = Find(request.TargetNetworkId);
            if (actor == null || target == null || !target.HasNpcAuthority())
            {
                details = "counter target is not a live server-authoritative Free Flow NPC";
                return false;
            }

            actor.GetSettings(
                out _, out _, out _, out float cooldown,
                out _, out _, out _);
            float now = Time.time;
            if (now - actor.m_LastAcceptedCounterTime < Mathf.Max(0f, cooldown))
            {
                details = "Free Flow counter cooldown is active on authority";
                return false;
            }

            NetworkFreeFlowCombatState state = target.m_CurrentState;
            if (!state.IsCounterable || state.StateVersion != request.ActionStateRevision ||
                state.TargetNetworkId != request.ActorNetworkId)
            {
                details = "counter window is closed, stale, already consumed, or assigned to another player";
                return false;
            }

            if (target.ResolveReceiver() == null ||
                !target.m_Receiver.TryConsumeNetworkCounterWindow())
            {
                details = "authority could not atomically consume the Free Flow counter window";
                return false;
            }

            actor.m_LastAcceptedCounterTime = now;
            target.RefreshAuthoritativeState(force: true);
            return true;
        }

        /// <summary>
        /// Validates a Skill that was authored and started by a server-owned NPC. Generic GC2
        /// NPC weapons remain supported; Free Flow direct Skills receive the stricter per-weapon
        /// allow-list, target, range, liveness, and optional line-of-sight checks.
        /// </summary>
        internal static bool ValidateTrustedServerNpcSkill(
            NetworkMeleeController controller,
            MeleeWeapon weapon,
            Skill skill,
            in NetworkSkillRequest request,
            out string details)
        {
            details = string.Empty;
            NetworkFreeFlowCombatAdapter actor = FindForController(controller);
            if (actor == null) return true;
            if (actor.ResolveReceiver() == null)
            {
                details = "server NPC Network Free Flow adapter has no ready runtime receiver";
                return false;
            }
            if (!actor.IsFreeFlowWeapon(weapon)) return true;
            if (!actor.HasNpcAuthority())
            {
                details = "Free Flow NPC Skill did not originate from NPC simulation authority";
                return false;
            }

            actor.RefreshSkillRegistrations();
            if (request.ComboNodeId == ComboTree.NODE_INVALID &&
                (!actor.m_SkillUses.ContainsKey(request.SkillHash) ||
                 !actor.m_DirectSkillHashes.Contains(request.SkillHash)))
            {
                details = "server NPC direct Skill is not allow-listed by an authored " +
                          "Free Flow direct-play path";
                return false;
            }

            if (request.TargetNetworkId == 0)
            {
                details = "server NPC Free Flow attack has no network player target";
                return false;
            }

            NetworkCharacter target = NetworkMeleeManager.Instance?.GetCharacterByNetworkId(
                request.TargetNetworkId);
            if (target == null || !target.IsPlayerOwnedActor ||
                !target.HasAuthenticatedPlayerOwner || target.Character == null ||
                target.Character.IsDead)
            {
                details = "server NPC Free Flow attack target is not a living authenticated player actor";
                return false;
            }

            actor.GetSettings(
                out float attackRadius,
                out float scanRadius,
                out _, out _,
                out bool requireLineOfSight,
                out _,
                out int occlusionMask);
            float maximum = Mathf.Max(attackRadius, scanRadius) +
                            Mathf.Max(0f, actor.m_ServerRangeTolerance);
            if ((target.transform.position - actor.transform.position).sqrMagnitude > maximum * maximum)
            {
                details = $"server NPC Free Flow target exceeds authority range {maximum:F2}";
                return false;
            }

            if (requireLineOfSight &&
                !actor.HasLineOfSight(target.gameObject, (LayerMask)occlusionMask))
            {
                details = "server NPC Free Flow authority line-of-sight validation failed";
                return false;
            }

            return true;
        }

        public bool ApplyReplicatedState(NetworkFreeFlowCombatState state)
        {
            if (state.CharacterNetworkId == 0 ||
                state.CharacterNetworkId != (m_NetworkCharacter != null
                    ? m_NetworkCharacter.NetworkId
                    : state.CharacterNetworkId))
            {
                return false;
            }

            if (!NetworkFreeFlowStateVersion.IsNewer(
                    state.StateVersion,
                    m_CurrentState.StateVersion))
            {
                // Equal and stale reliable/unreliable deliveries are already satisfied. Treat
                // them as applied so the manager does not retain an entry that can never advance.
                return true;
            }

            m_CurrentState = state;
            m_PendingReplicatedState = state;
            m_HasPendingReplicatedState = true;
            if (ResolveReceiver() != null && !HasNpcAuthority())
            {
                ApplyStateToReceiver(state);
            }

            return true;
        }

        /// <summary>
        /// Applies a field from a full character snapshot. Version zero is reserved as the
        /// snapshot tombstone for an omitted/default Free Flow state; live broadcasts continue
        /// through <see cref="ApplyReplicatedState"/> and can never clear state with version zero.
        /// </summary>
        internal bool ApplyReplicatedSnapshotState(NetworkFreeFlowCombatState state)
        {
            uint networkId = m_NetworkCharacter != null
                ? m_NetworkCharacter.NetworkId
                : state.CharacterNetworkId;
            if (state.CharacterNetworkId == 0 || state.CharacterNetworkId != networkId)
            {
                return false;
            }

            if (state.StateVersion != 0)
            {
                return ApplyReplicatedState(state);
            }

            NetworkFreeFlowCombatState cleared = NetworkFreeFlowCombatState.Create(networkId);
            m_CurrentState = cleared;
            m_PendingReplicatedState = cleared;
            m_HasPendingReplicatedState = true;
            if (ResolveReceiver() != null && !HasNpcAuthority())
            {
                ApplyStateToReceiver(cleared);
            }

            return true;
        }

        private void ApplyStateToReceiver(NetworkFreeFlowCombatState state)
        {
            if (m_Receiver == null) return;
            GameObject selectedPlayer = ResolveCharacterObject(state.TargetNetworkId);
            bool counterableForLocalPlayer = state.IsCounterable &&
                                             IsAuthenticatedLocalPlayer(
                                                 selectedPlayer,
                                                 state.TargetNetworkId);
            m_Receiver.ApplyReplicatedNetworkState(
                state.IsAttackable,
                counterableForLocalPlayer,
                state.HasAttackToken,
                state.ScoreBonus,
                selectedPlayer);
            // Character and combat state can arrive before the target player. Keep retrying the
            // same revision until the stable network ID resolves instead of permanently applying
            // a null target during late join.
            m_HasPendingReplicatedState = state.TargetNetworkId != 0 && selectedPlayer == null;
        }

        private static bool IsAuthenticatedLocalPlayer(GameObject player, uint expectedNetworkId)
        {
            if (player == null || expectedNetworkId == 0) return false;
            NetworkCharacter character = player.GetComponent<NetworkCharacter>();
            return character != null &&
                   character.NetworkId == expectedNetworkId &&
                   character.IsPlayerOwnedActor &&
                   character.IsLocalPlayer &&
                   character.HasAuthenticatedPlayerOwner;
        }

        private bool ValidateTargetAndRange(
            in NetworkSkillRequest request,
            NetworkFreeFlowSkillUse uses,
            bool counter,
            out string details)
        {
            details = string.Empty;
            GetSettings(
                out float attackRadius,
                out float scanRadius,
                out float counterRadius,
                out _,
                out bool requireAttackLineOfSight,
                out bool requireCounterLineOfSight,
                out int occlusionMask);

            if (request.TargetNetworkId == 0)
            {
                if (!counter && request.ComboNodeId == ComboTree.NODE_INVALID &&
                    (uses & NetworkFreeFlowSkillUse.Fallback) != 0)
                {
                    return true;
                }

                details = "Free Flow action requires a registered target";
                return false;
            }

            NetworkFreeFlowCombatAdapter target = Find(request.TargetNetworkId);
            if (target == null || target.m_NetworkCharacter == null ||
                !target.m_NetworkCharacter.IsServerAuthoritativeNPC ||
                target.m_NetworkCharacter.Character == null ||
                target.m_NetworkCharacter.Character.IsDead ||
                !target.m_CurrentState.IsAttackable)
            {
                details = "target is not an attackable, living server-authoritative Free Flow NPC";
                return false;
            }

            float allowedRadius;
            if (counter)
            {
                allowedRadius = counterRadius;
            }
            else if (request.ComboNodeId != ComboTree.NODE_INVALID)
            {
                allowedRadius = attackRadius;
            }
            else if ((uses & NetworkFreeFlowSkillUse.Fallback) != 0)
            {
                allowedRadius = Mathf.Max(attackRadius, scanRadius);
            }
            else
            {
                allowedRadius = attackRadius;
            }

            float tolerance = Mathf.Max(0f, m_ServerRangeTolerance);
            float maximum = Mathf.Max(0f, allowedRadius) + tolerance;
            if ((target.transform.position - transform.position).sqrMagnitude > maximum * maximum)
            {
                details = $"target exceeds authority Free Flow range {maximum:F2}";
                return false;
            }

            bool requireLineOfSight = counter
                ? requireCounterLineOfSight
                : requireAttackLineOfSight;
            if (requireLineOfSight &&
                !HasLineOfSight(target, (LayerMask)occlusionMask))
            {
                details = "authority line-of-sight validation failed";
                return false;
            }

            return true;
        }

        private bool HasLineOfSight(NetworkFreeFlowCombatAdapter target, LayerMask mask)
        {
            Vector3 origin = ResolveReceiver()?.GetNetworkAimPoint() ?? transform.position;
            Vector3 destination = target.ResolveReceiver()?.GetNetworkAimPoint() ??
                                  target.transform.position;
            return HasLineOfSight(origin, destination, target.gameObject, mask);
        }

        private bool HasLineOfSight(GameObject target, LayerMask mask)
        {
            Vector3 origin = ResolveReceiver()?.GetNetworkAimPoint() ?? transform.position;
            Vector3 destination = target != null ? target.transform.position : origin;
            NetworkFreeFlowCombatAdapter targetAdapter = FindForObject(target);
            if (targetAdapter?.ResolveReceiver() != null)
            {
                destination = targetAdapter.m_Receiver.GetNetworkAimPoint();
            }

            return HasLineOfSight(origin, destination, target, mask);
        }

        private bool HasLineOfSight(
            Vector3 origin,
            Vector3 destination,
            GameObject target,
            LayerMask mask)
        {
            Vector3 direction = destination - origin;
            float distance = direction.magnitude;
            if (distance <= 0.001f) return true;

            int count = Physics.RaycastNonAlloc(
                origin,
                direction / distance,
                m_LineOfSightHits,
                distance,
                mask,
                QueryTriggerInteraction.Ignore);
            // A saturated non-alloc buffer cannot prove that a later omitted collider is clear.
            // Authority therefore fails closed instead of accepting ambiguous line of sight.
            if (count >= m_LineOfSightHits.Length) return false;
            for (int i = 0; i < count; i++)
            {
                Collider collider = m_LineOfSightHits[i].collider;
                if (collider == null) continue;
                Transform hit = collider.transform;
                if (hit == transform || hit.IsChildOf(transform)) continue;
                if (target != null &&
                    (hit == target.transform || hit.IsChildOf(target.transform))) continue;
                return false;
            }

            return true;
        }

        private void RefreshAuthoritativeState(bool force)
        {
            if (!HasNpcAuthority() || ResolveReceiver() == null ||
                !m_Receiver.TryCaptureNetworkState(
                    out bool attackable,
                    out bool counterable,
                    out bool attackToken,
                    out float scoreBonus))
            {
                return;
            }

            NetworkCharacter selected = m_TargetSelector != null
                ? m_TargetSelector.SelectedTarget
                : null;
            uint targetId = selected != null ? selected.NetworkId : 0;
            m_Receiver.SetNetworkSelectedPlayer(selected != null ? selected.gameObject : null);

            byte flags = 0;
            if (attackable) flags |= NetworkFreeFlowCombatState.AttackableFlag;
            if (counterable) flags |= NetworkFreeFlowCombatState.CounterableFlag;
            if (attackToken) flags |= NetworkFreeFlowCombatState.AttackTokenFlag;

            bool changed = m_CurrentState.CharacterNetworkId != m_NetworkCharacter.NetworkId ||
                           m_CurrentState.TargetNetworkId != targetId ||
                           m_CurrentState.Flags != flags ||
                           !Mathf.Approximately(m_CurrentState.ScoreBonus, scoreBonus);
            if (!force && !changed) return;

            NetworkFreeFlowCombatState next = new NetworkFreeFlowCombatState
            {
                CharacterNetworkId = m_NetworkCharacter.NetworkId,
                StateVersion = NetworkFreeFlowStateVersion.Next(m_CurrentState.StateVersion),
                TargetNetworkId = targetId,
                Flags = flags,
                ScoreBonus = scoreBonus
            };

            NetworkMeleeManager manager = NetworkMeleeManager.Instance;
            if (manager == null || !manager.PublishAuthoritativeFreeFlowState(this, next)) return;
            m_CurrentState = next;
        }

        private void RefreshSimulationMode()
        {
            INetworkFreeFlowCombatReceiver receiver = ResolveReceiver();
            if (receiver == null || m_NetworkCharacter == null) return;

            bool localPlayerRuntime = m_NetworkCharacter.IsPlayerOwnedActor &&
                                      m_NetworkCharacter.IsLocalPlayer &&
                                      m_NetworkCharacter.HasAuthenticatedPlayerOwner;
            bool authoritativeNpc = HasNpcAuthority();
            receiver.SetNetworkSimulation(localPlayerRuntime, authoritativeNpc);

            if (m_LogAuthority)
            {
                Debug.Log(
                    $"[NetworkFreeFlowCombatAdapter] '{name}' localRuntime={localPlayerRuntime} " +
                    $"npcAuthority={authoritativeNpc} role={m_NetworkCharacter.Role}",
                    this);
                m_LogAuthority = false;
            }
        }

        private bool HasNpcAuthority()
        {
            return m_NetworkCharacter != null &&
                   m_NetworkCharacter.IsServerAuthoritativeNPC &&
                   m_NetworkCharacter.HasSimulationAuthority &&
                   m_NetworkCharacter.IsServerInstance;
        }

        private bool IsFreeFlowWeapon(MeleeWeapon weapon)
        {
            return weapon != null && ResolveReceiver() != null &&
                   m_Receiver.IsNetworkFreeFlowWeapon(weapon);
        }

        /// <summary>
        /// Identifies an accepted weapon as Free Flow while the concrete optional integration
        /// remains behind the receiver interface. The server records this decision on the attack
        /// lease so a later hit packet cannot swap a PvE target for a player actor.
        /// </summary>
        internal static bool UsesFreeFlowWeapon(
            NetworkMeleeController controller,
            MeleeWeapon weapon)
        {
            NetworkFreeFlowCombatAdapter adapter = FindForController(controller);
            return adapter != null && adapter.IsFreeFlowWeapon(weapon);
        }

        private void RefreshSkillRegistrations(bool force = false)
        {
            if (m_MeleeController == null || ResolveReceiver() == null) return;
            MeleeWeapon weapon = m_MeleeController.CurrentMeleeWeapon;
            if (!force && ReferenceEquals(weapon, m_RegisteredWeapon)) return;

            m_RegisteredWeapon = weapon;
            m_SkillUses.Clear();
            m_DirectSkillHashes.Clear();
            if (!IsFreeFlowWeapon(weapon)) return;

            m_Receiver.CollectNetworkSkills(
                weapon,
                RegisterSkillUse,
                RegisterDirectSkill);
        }

        private void InvalidateSkillRegistrations()
        {
            m_RegisteredWeapon = null;
            m_SkillUses.Clear();
            m_DirectSkillHashes.Clear();
        }

        private void RegisterSkillUse(Skill skill, NetworkFreeFlowSkillUse use)
        {
            if (skill == null || use == NetworkFreeFlowSkillUse.None) return;
            NetworkMeleeManager.RegisterSkill(skill);
            int hash = StableHashUtility.GetStableHash(skill.name);
            m_SkillUses.TryGetValue(hash, out NetworkFreeFlowSkillUse existing);
            m_SkillUses[hash] = existing | use;
        }

        private void RegisterDirectSkill(Skill skill)
        {
            if (skill == null) return;
            NetworkMeleeManager.RegisterSkill(skill);
            m_DirectSkillHashes.Add(StableHashUtility.GetStableHash(skill.name));
        }

        private void GetSettings(
            out float attackRadius,
            out float scanRadius,
            out float counterRadius,
            out float counterCooldown,
            out bool requireAttackLineOfSight,
            out bool requireCounterLineOfSight,
            out int occlusionMask)
        {
            attackRadius = 0f;
            scanRadius = 0f;
            counterRadius = 0f;
            counterCooldown = 0f;
            requireAttackLineOfSight = false;
            requireCounterLineOfSight = false;
            occlusionMask = Physics.DefaultRaycastLayers;
            if (ResolveReceiver() == null || m_MeleeController == null) return;
            m_Receiver.GetNetworkSettings(
                m_MeleeController.CurrentMeleeWeapon,
                out attackRadius,
                out scanRadius,
                out counterRadius,
                out counterCooldown,
                out requireAttackLineOfSight,
                out requireCounterLineOfSight,
                out occlusionMask);
        }

        private INetworkFreeFlowCombatReceiver ResolveReceiver()
        {
            if (m_ReceiverBehaviour != null && m_Receiver != null) return m_Receiver;
            MonoBehaviour[] behaviours = GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is not INetworkFreeFlowCombatReceiver receiver) continue;
                m_ReceiverBehaviour = behaviours[i];
                m_Receiver = receiver;
                return receiver;
            }

            m_ReceiverBehaviour = null;
            m_Receiver = null;
            return null;
        }

        private void RegisterIfReady()
        {
            uint networkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            if (networkId == 0)
            {
                Unregister();
                return;
            }
            if (m_RegisteredNetworkId == networkId &&
                s_Adapters.TryGetValue(networkId, out NetworkFreeFlowCombatAdapter current) &&
                current == this)
            {
                NetworkMeleeManager.Instance?.RegisterFreeFlowAdapter(networkId, this);
                return;
            }

            Unregister();
            if (m_CurrentState.CharacterNetworkId != 0 &&
                m_CurrentState.CharacterNetworkId != networkId)
            {
                m_CurrentState = NetworkFreeFlowCombatState.Create(networkId);
                m_HasPendingReplicatedState = false;
            }
            s_Adapters[networkId] = this;
            m_RegisteredNetworkId = networkId;
            m_CurrentState.CharacterNetworkId = networkId;
            NetworkMeleeManager.Instance?.RegisterFreeFlowAdapter(networkId, this);
        }

        private void Unregister()
        {
            if (m_RegisteredNetworkId == 0) return;
            if (s_Adapters.TryGetValue(m_RegisteredNetworkId, out NetworkFreeFlowCombatAdapter current) &&
                current == this)
            {
                s_Adapters.Remove(m_RegisteredNetworkId);
            }

            NetworkMeleeManager.Instance?.UnregisterFreeFlowAdapter(m_RegisteredNetworkId, this);
            m_RegisteredNetworkId = 0;
        }

        private void ClearPendingCounter()
        {
            m_PendingCounterTargetId = 0;
            m_PendingCounterRevision = 0;
            m_PendingCounterExpiresAt = 0f;
        }

        private GameObject ResolveCharacterObject(uint networkId)
        {
            if (networkId == 0) return null;
            NetworkCharacter character = NetworkMeleeManager.Instance?.GetCharacterByNetworkId(networkId);
            return character != null ? character.gameObject : null;
        }

        private static NetworkFreeFlowCombatAdapter Find(uint networkId)
        {
            if (networkId == 0) return null;
            return s_Adapters.TryGetValue(networkId, out NetworkFreeFlowCombatAdapter adapter) &&
                   adapter != null
                ? adapter
                : null;
        }

        private static NetworkFreeFlowCombatAdapter FindForController(NetworkMeleeController controller)
        {
            if (controller == null) return null;
            NetworkCharacter character = controller.GetComponent<NetworkCharacter>();
            NetworkFreeFlowCombatAdapter adapter = Find(character != null ? character.NetworkId : 0);
            return adapter != null ? adapter : controller.GetComponent<NetworkFreeFlowCombatAdapter>();
        }

        private static NetworkFreeFlowCombatAdapter FindForObject(GameObject target)
        {
            if (target == null) return null;
            NetworkFreeFlowCombatAdapter adapter = target.GetComponent<NetworkFreeFlowCombatAdapter>();
            if (adapter != null) return adapter;
            adapter = target.GetComponentInParent<NetworkFreeFlowCombatAdapter>();
            return adapter != null
                ? adapter
                : target.GetComponentInChildren<NetworkFreeFlowCombatAdapter>(true);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            m_StatePollInterval = Mathf.Max(MinimumPollInterval, m_StatePollInterval);
            m_ServerRangeTolerance = Mathf.Max(0f, m_ServerRangeTolerance);
        }
#endif
    }
}
