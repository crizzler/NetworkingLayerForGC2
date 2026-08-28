using System;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;


namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Server-authoritative facing unit for networked characters.
    /// The server validates and broadcasts facing direction changes to all clients.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This facing unit should be used instead of UnitFacingPivot when you need
    /// server-authoritative control over character facing direction. This is important
    /// for mechanics like backstab damage, cone attacks, or any gameplay where
    /// facing direction affects outcomes.
    /// </para>
    /// <para>
    /// The server calculates and validates the facing direction, then broadcasts it
    /// to all clients. Clients interpolate smoothly between received values.
    /// </para>
    /// </remarks>
    [Title("Network Pivot (Server-Authoritative)")]
    [Image(typeof(IconRotationYaw), ColorTheme.Type.Blue)]

    [Category("Network/Network Pivot")]
    [Description("Server-authoritative facing that syncs across the network. " +
                 "Use for characters where facing direction affects gameplay (backstabs, cone attacks, etc.)")]

    [Serializable]
    public class UnitFacingNetworkPivot : TUnitFacing, INetworkFacingUnit
    {
        private enum DirectionFrom
        {
            MotionDirection,
            DriverDirection
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // EXPOSED MEMBERS
        // ════════════════════════════════════════════════════════════════════════════════════════

        [SerializeField] private DirectionFrom m_DirectionFrom = DirectionFrom.MotionDirection;
        [SerializeField] private Axonometry m_Axonometry = new Axonometry();

        [Header("Network Settings")]
        [Tooltip("How quickly clients interpolate to the server's facing direction")]
        [SerializeField] private float m_InterpolationSpeed = 15f;

        [Tooltip("Minimum angle change (degrees) before sending an update")]
        [SerializeField] private float m_MinAngleChange = 1f;

        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE MEMBERS
        // ════════════════════════════════════════════════════════════════════════════════════════

        [NonSerialized] private NetworkCharacter m_NetworkCharacter;
        [NonSerialized] private float m_ServerYaw;
        [NonSerialized] private float m_ClientYaw;
        [NonSerialized] private float m_LastSentYaw;
        [NonSerialized] private bool m_IsNetworkInitialized;

        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════

        public override Axonometry Axonometry
        {
            get => m_Axonometry;
            set => m_Axonometry = value;
        }

        /// <summary>
        /// The server-authoritative yaw angle in degrees.
        /// </summary>
        public float ServerYaw => m_ServerYaw;

        /// <summary>
        /// The current interpolated yaw angle on this client.
        /// </summary>
        public float ClientYaw => m_ClientYaw;

        /// <summary>
        /// Whether this facing unit is network-initialized and ready.
        /// </summary>
        public bool IsNetworkInitialized => m_IsNetworkInitialized;

        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);

            // Initialize yaw from current rotation
            m_ServerYaw = character.transform.eulerAngles.y;
            m_ClientYaw = m_ServerYaw;
            m_LastSentYaw = m_ServerYaw;

            // Try to find NetworkCharacter
            m_NetworkCharacter = character.GetComponent<NetworkCharacter>();

            if (m_NetworkCharacter != null)
            {
                m_NetworkCharacter.OnFacingUnitRegistered(this);
                m_IsNetworkInitialized = true;
            }
            else
            {
                Debug.LogWarning($"[UnitFacingNetworkPivot] No NetworkCharacter found on {character.name}. " +
                                 "Falling back to local-only facing.");
            }
        }

        public override void OnDispose(Character character)
        {
            if (m_NetworkCharacter != null)
            {
                m_NetworkCharacter.OnFacingUnitUnregistered();
            }

            base.OnDispose(character);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // UPDATE
        // ════════════════════════════════════════════════════════════════════════════════════════

        public override void OnUpdate()
        {
            if (Character.IsDead) return;

            if (IsExternalMotionOwningFacing())
            {
                SyncNetworkYawToCurrentRotation();
                return;
            }

            if (m_IsNetworkInitialized && m_NetworkCharacter != null)
            {
                UpdateNetworked();
            }
            else
            {
                UpdateLocal();
            }
        }

        private void UpdateLocal()
        {
            // Fallback: behave like regular UnitFacingPivot
            Vector3 direction = GetLocalDirection();
            m_ServerYaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            m_ClientYaw = m_ServerYaw;

            base.OnUpdate();
        }

        private void UpdateNetworked()
        {
            var role = m_NetworkCharacter.CurrentRole;

            switch (role)
            {
                case NetworkCharacter.NetworkRole.Server:
                    UpdateAsServer();
                    break;

                case NetworkCharacter.NetworkRole.LocalClient:
                    UpdateAsLocalClient();
                    break;

                case NetworkCharacter.NetworkRole.RemoteClient:
                    UpdateAsRemoteClient();
                    break;

                default:
                    UpdateLocal();
                    break;
            }
        }

        private void UpdateAsServer()
        {
            // Let GC2 resolve its authored Facing layers before publishing authority yaw. Free
            // Flow, Traversal, Dash, and ordinary visual-scripting Instructions all use
            // SetLayerDirection/SetLayerTarget; deriving yaw only from Motion.MoveDirection
            // silently discarded those requests on server-owned characters.
            base.OnUpdate();

            Quaternion resolvedRotation = Transform.rotation;
            m_ServerYaw = resolvedRotation.eulerAngles.y;
            m_ClientYaw = m_ServerYaw;

            // Check if we need to broadcast update
            float angleDelta = Mathf.Abs(Mathf.DeltaAngle(m_LastSentYaw, m_ServerYaw));
            if (angleDelta >= m_MinAngleChange)
            {
                m_LastSentYaw = m_ServerYaw;
                // NetworkCharacter transport integration handles the broadcast path.
            }

            // TUnitFacing writes the Transform directly. Route the same resolved rotation through
            // the active driver as well so its physics representation is synchronized before
            // server-side combat overlap tests execute.
            Character.Driver.SetRotation(resolvedRotation);
        }

        private void UpdateAsLocalClient()
        {
            // Resolve the same GC2 Facing layer queue as the ordinary Pivot unit. Free Flow,
            // Traversal, and visual-scripting actions use SetLayerDirection/SetLayerTarget; using
            // only Motion.MoveDirection here discarded those authored requests on connected
            // owners. Restore the source rotation before applying the validated network yaw so
            // base.OnUpdate is used as a resolver rather than an unvalidated transform write.
            Quaternion sourceRotation = Transform.rotation;
            base.OnUpdate();
            float desiredYaw = GetResolvedTargetYaw(sourceRotation.eulerAngles.y);
            Transform.rotation = sourceRotation;

            // Send a new target when input changes, then keep requesting while the
            // validated server yaw is still catching up to that target.
            float requestedDelta = Mathf.Abs(Mathf.DeltaAngle(m_LastSentYaw, desiredYaw));
            float serverDelta = Mathf.Abs(Mathf.DeltaAngle(m_ServerYaw, desiredYaw));
            if (requestedDelta >= m_MinAngleChange || serverDelta >= m_MinAngleChange)
            {
                m_LastSentYaw = desiredYaw;
                m_NetworkCharacter.RequestFacingUpdate(desiredYaw);
            }

            // Interpolate toward server yaw for smooth visuals
            m_ClientYaw = Mathf.LerpAngle(m_ClientYaw, m_ServerYaw,
                m_InterpolationSpeed * Character.Time.DeltaTime);

            // Apply rotation
            ApplyRotation(m_ClientYaw);
        }

        private void UpdateAsRemoteClient()
        {
            // Remote client: interpolate toward received server yaw
            m_ClientYaw = Mathf.LerpAngle(m_ClientYaw, m_ServerYaw,
                m_InterpolationSpeed * Character.Time.DeltaTime);

            // Apply rotation
            ApplyRotation(m_ClientYaw);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // NETWORK CALLBACKS
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Called by NetworkCharacter when receiving a facing update from the server.
        /// </summary>
        /// <param name="yaw">The new server-authoritative yaw angle in degrees.</param>
        public void OnServerYawReceived(float yaw)
        {
            m_ServerYaw = yaw;
        }

        /// <summary>
        /// Called by NetworkCharacter on the server when a client requests a facing change.
        /// </summary>
        /// <param name="requestedYaw">The yaw angle the client wants to face.</param>
        /// <returns>The validated yaw angle (may be same as requested or adjusted).</returns>
        public float ValidateFacingRequest(float requestedYaw)
        {
            // Server can validate/modify the requested yaw here
            // For example: clamp rotation speed, check for cheating, etc.

            // GC2 uses a negative angular speed to mean immediate rotation. Passing that value
            // into Mathf.Clamp reverses its min/max bounds and can rotate connected owners in the
            // wrong direction instead of honoring an authored facing layer.
            if (Character.Motion.AngularSpeed < 0f)
            {
                m_ServerYaw = requestedYaw;
                return m_ServerYaw;
            }

            // Calculate max rotation delta based on angular speed
            float maxDelta = Character.Motion.AngularSpeed * Character.Time.DeltaTime;
            float currentYaw = m_ServerYaw;
            float delta = Mathf.DeltaAngle(currentYaw, requestedYaw);

            // Clamp to max rotation speed
            delta = Mathf.Clamp(delta, -maxDelta, maxDelta);

            // Apply validated rotation
            m_ServerYaw = currentYaw + delta;
            return m_ServerYaw;
        }

        /// <summary>
        /// Forces the facing to a specific yaw angle. Server-only.
        /// </summary>
        /// <param name="yaw">The yaw angle in degrees.</param>
        public void ForceServerYaw(float yaw)
        {
            m_ServerYaw = yaw;
            m_ClientYaw = yaw;
            m_LastSentYaw = yaw;
            ApplyRotation(yaw);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROTECTED METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════

        protected override Vector3 GetDefaultDirection()
        {
            // TUnitFacing calls this only when no authored facing layer is active. Preserve the
            // ordinary Pivot behavior in that case and let a queued layer override it.
            return GetLocalDirection();
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════

        private Vector3 GetLocalDirection()
        {
            Vector3 driverDirection = Vector3.Scale(
                m_DirectionFrom switch
                {
                    DirectionFrom.MotionDirection => Character.Motion.MoveDirection,
                    DirectionFrom.DriverDirection => Character.Driver.WorldMoveDirection,
                    _ => throw new ArgumentOutOfRangeException()
                },
                Vector3Plane.NormalUp
            );

            Vector3 direction = DecideDirection(driverDirection);
            return m_Axonometry?.ProcessRotation(this, direction) ?? direction;
        }

        private float GetResolvedTargetYaw(float fallbackYaw)
        {
            // TUnitFacing stores the unsmoothed, layer-resolved direction in m_FaceDirection.
            // Reading Transform.eulerAngles after base.OnUpdate instead would read GC2's already
            // smoothed intermediate yaw, which then gets clamped by authority and interpolated a
            // second time on the owner. That made finite-speed Free Flow facing visibly sluggish.
            Vector3 direction = Vector3.Scale(m_FaceDirection, Vector3Plane.NormalUp);
            if (direction.sqrMagnitude <= float.Epsilon) return fallbackYaw;

            return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        }

        private void ApplyRotation(float yaw)
        {
            Quaternion targetRotation = Quaternion.Euler(0f, yaw, 0f);
            Quaternion sourceRotation = Transform.rotation;

            m_FaceDirection = targetRotation * Vector3.forward;
            m_PivotSpeed = Vector3.SignedAngle(
                sourceRotation * Vector3.forward,
                m_FaceDirection,
                Vector3.up
            );

            // Route root rotation through the active driver. Network CharacterControllers can
            // be left dirty in PhysX when auto-sync transforms is disabled and Facing writes the
            // Transform directly after Driver.OnUpdate. The server driver flushes this write
            // before GC2 performs its LateUpdate melee overlap queries.
            Quaternion rotation = Quaternion.Lerp(
                targetRotation,
                sourceRotation * Character.Animim.RootMotionDeltaRotation,
                Character.RootMotionRotation
            );

            Character.Driver.SetRotation(rotation);
        }

        private bool IsExternalMotionOwningFacing()
        {
            if (Character?.Busy == null) return false;
            return Character.Busy.IsBusy || Character.Busy.AreLegsBusy;
        }

        private void SyncNetworkYawToCurrentRotation()
        {
            float currentYaw = Transform.eulerAngles.y;
            m_ServerYaw = currentYaw;
            m_ClientYaw = currentYaw;
            m_LastSentYaw = currentYaw;
            m_FaceDirection = Quaternion.Euler(0f, currentYaw, 0f) * Vector3.forward;
            m_PivotSpeed = 0f;
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // STRING
        // ════════════════════════════════════════════════════════════════════════════════════════

        public override string ToString() => "Network Pivot";
    }
}
