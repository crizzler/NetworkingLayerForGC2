using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    /// <summary>
    /// Manual <see cref="Packer{T}"/> registrations for the GC2 networking layer's
    /// value types (<see cref="NetworkInputState"/>, <see cref="NetworkPositionState"/>).
    ///
    /// Why this is needed:
    ///  - <see cref="GC2InputBroadcast"/> and <see cref="GC2StateBroadcast"/> are
    ///    declared <see cref="IPackedAuto"/>, so PurrNet's codegen builds a serializer
    ///    for them. While generating that serializer, however, codegen needs to know
    ///    how to write each <em>field</em> too. The struct fields themselves
    ///    (<c>NetworkInputState</c>, <c>NetworkPositionState</c>) live in the core GC2
    ///    networking asmdef, which intentionally has no PurrNet dependency, so they
    ///    aren't tagged <c>IPackedAuto</c> and codegen can't auto-emit packers for them.
    ///  - Without these extensions, codegen falls back to <c>Packer.FallbackWriter</c>,
    ///    which performs polymorphic writes via <c>Hasher.GetStableHashU32(type)</c>.
    ///    That throws <c>"Type 'Arawn...NetworkPositionState' is not registered"</c>
    ///    in builds where the IL post-processor stripped its boilerplate registration.
    ///  - Defining <c>Write(this BitPacker, T)</c> / <c>Read(this BitPacker, ref T)</c>
    ///    extension methods is the documented way (see <c>BitPackerUnityExtensions</c>)
    ///    to teach codegen how to pack a value type. They're picked up automatically
    ///    by the PurrNet ILPP when generating <c>GC2*Broadcast_Serializer</c>.
    ///
    /// The packers below write each primitive field directly, mirroring the struct
    /// layout. This is tighter than fallback polymorphic encoding and avoids any
    /// runtime type-hash lookup.
    /// </summary>
    [UsedImplicitly]
    public static class GC2NetworkValuePackers
    {
        [RegisterPackers]
        static void Register()
        {
            // Defensive: make sure Hasher knows these types so any rare polymorphic
            // path (e.g. legacy serializers cached from a prior build) doesn't crash.
            Hasher.PrepareType(typeof(NetworkInputState));
            Hasher.PrepareType(typeof(NetworkPositionState));
        }

        // -------- NetworkInputState (10 bytes base + optional authority pose) --------

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkInputState value)
        {
            packer.Write(value.inputX);
            packer.Write(value.inputY);
            packer.Write(value.sequenceNumber);
            packer.Write(value.flags);
            packer.Write(value.deltaTimeMs);
            packer.Write(value.rotationY);
            packer.Write(value.authorityFlags);

            if (value.HasOwnerAuthorityPosition)
            {
                packer.Write(value.authorityPositionX);
                packer.Write(value.authorityPositionY);
                packer.Write(value.authorityPositionZ);
            }
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkInputState value)
        {
            packer.Read(ref value.inputX);
            packer.Read(ref value.inputY);
            packer.Read(ref value.sequenceNumber);
            packer.Read(ref value.flags);
            packer.Read(ref value.deltaTimeMs);
            packer.Read(ref value.rotationY);
            packer.Read(ref value.authorityFlags);

            if (value.HasOwnerAuthorityPosition)
            {
                packer.Read(ref value.authorityPositionX);
                packer.Read(ref value.authorityPositionY);
                packer.Read(ref value.authorityPositionZ);
            }
            else
            {
                value.authorityPositionX = 0;
                value.authorityPositionY = 0;
                value.authorityPositionZ = 0;
            }
        }

        // -------- NetworkPositionState --------

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkPositionState value)
        {
            packer.Write(value.positionX);
            packer.Write(value.positionY);
            packer.Write(value.positionZ);
            packer.Write(value.rotationY);
            packer.Write(value.verticalVelocity);
            packer.Write(value.moveVelocityX);
            packer.Write(value.moveVelocityY);
            packer.Write(value.moveVelocityZ);
            packer.Write(value.flags);
            packer.Write(value.lastProcessedInput);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkPositionState value)
        {
            packer.Read(ref value.positionX);
            packer.Read(ref value.positionY);
            packer.Read(ref value.positionZ);
            packer.Read(ref value.rotationY);
            packer.Read(ref value.verticalVelocity);
            packer.Read(ref value.moveVelocityX);
            packer.Read(ref value.moveVelocityY);
            packer.Read(ref value.moveVelocityZ);
            packer.Read(ref value.flags);
            packer.Read(ref value.lastProcessedInput);
        }
    }
}
