using PurrNet.Packing;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.PurrDiction
{
    /// <summary>
    /// A one-tick GC2 pose operation captured in PurrDiction input history. Keeping these
    /// channels distinct prevents an Update-time rotation or scale write from implicitly
    /// admitting a render/interpolated position into prediction state.
    /// </summary>
    public struct PurrDictionExternalPoseCommand : IPackedAuto
    {
        public const ushort FLAG_POSITION = 1 << 0;
        public const ushort FLAG_POSITION_ABSOLUTE = 1 << 1;
        public const ushort FLAG_ROTATION = 1 << 2;
        public const ushort FLAG_ROTATION_ABSOLUTE = 1 << 3;
        public const ushort FLAG_SCALE = 1 << 4;
        public const ushort FLAG_SCALE_ABSOLUTE = 1 << 5;
        public const ushort FLAG_TELEPORT = 1 << 6;

        public ushort sequence;
        public uint sourceTick;
        public ushort flags;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;

        public bool HasCommand => (flags & (FLAG_POSITION | FLAG_ROTATION | FLAG_SCALE)) != 0;
        public bool HasPosition => (flags & FLAG_POSITION) != 0;
        public bool PositionIsAbsolute => (flags & FLAG_POSITION_ABSOLUTE) != 0;
        public bool HasRotation => (flags & FLAG_ROTATION) != 0;
        public bool RotationIsAbsolute => (flags & FLAG_ROTATION_ABSOLUTE) != 0;
        public bool HasScale => (flags & FLAG_SCALE) != 0;
        public bool ScaleIsAbsolute => (flags & FLAG_SCALE_ABSOLUTE) != 0;
        public bool IsTeleport => (flags & FLAG_TELEPORT) != 0;

        public void ClearOneShot()
        {
            this = default;
        }
    }

    /// <summary>
    /// Common hand-off used by both directional and NavMesh GC2 drivers. Driver methods only
    /// sample or queue intent; the PredictedIdentity consumes it from Simulate.
    /// </summary>
    public interface IPurrDictionNativeMovementBackend
    {
        bool CanAuthorLocalIntent { get; }
        bool CanAuthorTrustedServerPose { get; }
        bool IsOwnerMotionWindowActive { get; }

        void OpenOwnerMotionWindow(float durationSeconds);
        void OpenServerOwnerMotionWindow(float durationSeconds, uint operationId = 0);
        void CloseServerOwnerMotionWindow(float graceSeconds = 0f);
        void QueueExternalPosition(Vector3 value, bool absolute, bool teleport);
        void QueueExternalRotation(Quaternion value, bool absolute);
        void QueueExternalScale(Vector3 value, bool absolute);
    }

    public struct PurrDictionResolvedExternalPose
    {
        public bool hasPosition;
        public bool hasRotation;
        public bool hasScale;
        public bool teleport;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
    }

    /// <summary>
    /// Coalesces all GC2 transform calls received between prediction ticks. Absolute values are
    /// updated by later additive calls so the resulting command remains idempotent when replayed.
    /// </summary>
    internal sealed class PurrDictionPendingExternalPose
    {
        private ushort m_NextSequence;
        private ushort m_Flags;
        private Vector3 m_Position;
        private Quaternion m_Rotation = Quaternion.identity;
        private Vector3 m_Scale;

        public bool HasCommand =>
            (m_Flags & (PurrDictionExternalPoseCommand.FLAG_POSITION |
                        PurrDictionExternalPoseCommand.FLAG_ROTATION |
                        PurrDictionExternalPoseCommand.FLAG_SCALE)) != 0;

        public void QueuePosition(Vector3 value, bool absolute, bool teleport)
        {
            if ((m_Flags & PurrDictionExternalPoseCommand.FLAG_POSITION) == 0 || absolute)
            {
                m_Position = value;
                m_Flags |= PurrDictionExternalPoseCommand.FLAG_POSITION;
                if (absolute)
                {
                    m_Flags |= PurrDictionExternalPoseCommand.FLAG_POSITION_ABSOLUTE;
                }
                else
                {
                    m_Flags &= unchecked((ushort)~PurrDictionExternalPoseCommand.FLAG_POSITION_ABSOLUTE);
                }
            }
            else
            {
                m_Position += value;
            }

            if (teleport) m_Flags |= PurrDictionExternalPoseCommand.FLAG_TELEPORT;
        }

        public void QueueRotation(Quaternion value, bool absolute)
        {
            if ((m_Flags & PurrDictionExternalPoseCommand.FLAG_ROTATION) == 0 || absolute)
            {
                m_Rotation = value;
                m_Flags |= PurrDictionExternalPoseCommand.FLAG_ROTATION;
                if (absolute)
                {
                    m_Flags |= PurrDictionExternalPoseCommand.FLAG_ROTATION_ABSOLUTE;
                }
                else
                {
                    m_Flags &= unchecked((ushort)~PurrDictionExternalPoseCommand.FLAG_ROTATION_ABSOLUTE);
                }
            }
            else
            {
                m_Rotation *= value;
            }
        }

        public void QueueScale(Vector3 value, bool absolute)
        {
            if ((m_Flags & PurrDictionExternalPoseCommand.FLAG_SCALE) == 0 || absolute)
            {
                m_Scale = value;
                m_Flags |= PurrDictionExternalPoseCommand.FLAG_SCALE;
                if (absolute)
                {
                    m_Flags |= PurrDictionExternalPoseCommand.FLAG_SCALE_ABSOLUTE;
                }
                else
                {
                    m_Flags &= unchecked((ushort)~PurrDictionExternalPoseCommand.FLAG_SCALE_ABSOLUTE);
                }
            }
            else
            {
                // GC2's AddScale contract is component-wise addition.
                m_Scale += value;
            }
        }

        public bool TryConsume(ulong sourceTick, out PurrDictionExternalPoseCommand command)
        {
            if (!HasCommand)
            {
                command = default;
                return false;
            }

            unchecked
            {
                m_NextSequence++;
                if (m_NextSequence == 0) m_NextSequence = 1;
            }

            command = new PurrDictionExternalPoseCommand
            {
                sequence = m_NextSequence,
                sourceTick = (uint)sourceTick,
                flags = m_Flags,
                position = m_Position,
                rotation = m_Rotation,
                scale = m_Scale
            };

            m_Flags = 0;
            m_Position = Vector3.zero;
            m_Rotation = Quaternion.identity;
            m_Scale = Vector3.zero;
            return true;
        }

        public void Clear()
        {
            m_NextSequence = 0;
            m_Flags = 0;
            m_Position = Vector3.zero;
            m_Rotation = Quaternion.identity;
            m_Scale = Vector3.zero;
        }
    }
}
