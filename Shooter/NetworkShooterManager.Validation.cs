#if GC2_SHOOTER
using System;
using System.Collections.Generic;
using UnityEngine;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking.Shooter
{
    public partial class NetworkShooterManager
    {
        private static NetworkRequestContext BuildContext(uint actorNetworkId, uint correlationId)
        {
            return NetworkRequestContext.Create(actorNetworkId, correlationId);
        }

        private bool ValidateShooterRequest(uint senderClientId, uint actorNetworkId, uint correlationId, string requestType)
        {
            return SecurityIntegration.ValidateModuleRequest(
                senderClientId,
                BuildContext(actorNetworkId, correlationId),
                "Shooter",
                requestType);
        }

        private bool ValidateActorBinding(
            uint senderClientId,
            uint actorNetworkId,
            uint claimedNetworkId,
            string requestType,
            string claimedFieldName)
        {
            if (actorNetworkId == 0 || claimedNetworkId == 0)
            {
                SecurityIntegration.RecordViolation(
                    senderClientId,
                    actorNetworkId,
                    SecurityViolationType.InvalidRequest,
                    "Shooter",
                    $"{requestType} missing actor binding values actor={actorNetworkId}, {claimedFieldName}={claimedNetworkId}");
                return false;
            }

            if (actorNetworkId == claimedNetworkId)
            {
                return true;
            }

            SecurityIntegration.RecordViolation(
                senderClientId,
                actorNetworkId,
                SecurityViolationType.ProtocolMismatch,
                "Shooter",
                $"{requestType} actor mismatch actor={actorNetworkId}, {claimedFieldName}={claimedNetworkId}");
            return false;
        }

        private bool TryGetActorController(
            uint senderClientId,
            uint actorNetworkId,
            string requestType,
            out NetworkShooterController controller)
        {
            if (m_Controllers.TryGetValue(actorNetworkId, out controller))
            {
                return true;
            }

            SecurityIntegration.RecordViolation(
                senderClientId,
                actorNetworkId,
                SecurityViolationType.InvalidTarget,
                "Shooter",
                $"{requestType} rejected: no registered controller for actor {actorNetworkId}");

            if (m_LogShotRequests || m_LogHitRequests || m_LogBroadcasts)
            {
                Debug.LogWarning(
                    $"[NetworkShooterManager] {requestType} rejected: missing controller for actor {actorNetworkId}");
            }

            return false;
        }

        private static ulong ComposeShotKey(uint shooterNetworkId, ushort shotRequestId)
        {
            return ((ulong)shooterNetworkId << 16) | shotRequestId;
        }

        private bool IsQueueAtCapacity<T>(Queue<T> queue, int maxQueueLength, uint senderClientId, uint actorNetworkId, string requestType)
        {
            int safeLimit = Mathf.Max(1, maxQueueLength);
            if (queue.Count < safeLimit) return false;

            SecurityIntegration.RecordViolation(
                senderClientId,
                actorNetworkId,
                SecurityViolationType.RateLimitExceeded,
                "Shooter",
                $"{requestType} queue capacity reached ({queue.Count}/{safeLimit})");

            if (m_LogShotRequests || m_LogHitRequests || m_LogBroadcasts)
            {
                Debug.LogWarning(
                    $"[NetworkShooterManager] Dropped {requestType}: queue full ({queue.Count}/{safeLimit})");
            }

            return true;
        }

        private static int DropStaleRequests<T>(Queue<T> queue, float maxAgeSeconds, Func<T, float> getReceivedTime)
        {
            if (queue.Count == 0) return 0;

            float now = Time.time;
            int dropped = 0;
            while (queue.Count > 0)
            {
                T queued = queue.Peek();
                if (now - getReceivedTime(queued) <= maxAgeSeconds) break;

                queue.Dequeue();
                dropped++;
            }

            return dropped;
        }

        private static int ResolveMaxAcceptedHits(NetworkShotRequest request, int maxHitsPerProjectile)
        {
            int projectileCount = Mathf.Max(1, request.TotalProjectiles);
            int safePerProjectile = Mathf.Clamp(maxHitsPerProjectile, 1, 32);
            int maxHits = projectileCount * safePerProjectile;
            return Mathf.Clamp(maxHits, projectileCount, 256);
        }

        private bool ValidateHitSourceShot(
            NetworkShooterHitRequest request,
            out ulong shotKey,
            out HitRejectionReason rejectionReason)
        {
            shotKey = 0;
            rejectionReason = HitRejectionReason.ShotNotValidated;
            if (request.SourceShotRequestId == 0) return false;

            uint shooterNetworkId = request.ActorNetworkId != 0 ? request.ActorNetworkId : request.ShooterNetworkId;
            shotKey = ComposeShotKey(shooterNetworkId, request.SourceShotRequestId);
            if (!m_ValidatedShotReferences.TryGetValue(shotKey, out var shotReference))
            {
                return false;
            }

            if (shotReference.WeaponHash != 0 && shotReference.WeaponHash != request.WeaponHash)
            {
                return false;
            }

            if (Time.time - shotReference.ValidatedTime > m_ValidatedShotLifetime)
            {
                m_ValidatedShotReferences.Remove(shotKey);
                return false;
            }

            if (shotReference.AcceptedHitCount >= Mathf.Max(1, shotReference.MaxAcceptedHits))
            {
                rejectionReason = HitRejectionReason.AlreadyHit;
                return false;
            }

            if (request.IsCharacterHit && request.TargetNetworkId != 0)
            {
                if (shotReference.AcceptedCharacterTargets != null &&
                    shotReference.AcceptedCharacterTargets.Contains(request.TargetNetworkId))
                {
                    rejectionReason = HitRejectionReason.AlreadyHit;
                    return false;
                }
            }

            return true;
        }

        private void RecordValidatedHitClaim(ulong shotKey, NetworkShooterHitRequest request)
        {
            if (shotKey == 0) return;
            if (!m_ValidatedShotReferences.TryGetValue(shotKey, out var shotReference)) return;

            shotReference.AcceptedHitCount++;

            if (request.IsCharacterHit && request.TargetNetworkId != 0)
            {
                shotReference.AcceptedCharacterTargets ??= new HashSet<uint>();
                shotReference.AcceptedCharacterTargets.Add(request.TargetNetworkId);
            }

            m_ValidatedShotReferences[shotKey] = shotReference;
        }

        private void RecordValidatedShot(NetworkShotRequest request)
        {
            uint shooterNetworkId = request.ActorNetworkId != 0 ? request.ActorNetworkId : request.ShooterNetworkId;
            ulong shotKey = ComposeShotKey(shooterNetworkId, request.RequestId);
            m_ValidatedShotReferences[shotKey] = new ValidatedShotReference
            {
                ValidatedTime = Time.time,
                WeaponHash = request.WeaponHash,
                MaxAcceptedHits = ResolveMaxAcceptedHits(request, m_MaxValidatedHitsPerProjectile),
                AcceptedHitCount = 0,
                AcceptedCharacterTargets = null
            };
        }

        private void CleanupStaleValidatedShotReferences()
        {
            if (m_ValidatedShotReferences.Count == 0) return;

            float now = Time.time;
            s_SharedValidatedShotKeyBuffer.Clear();
            foreach (var entry in m_ValidatedShotReferences)
            {
                if (now - entry.Value.ValidatedTime > m_ValidatedShotLifetime)
                {
                    s_SharedValidatedShotKeyBuffer.Add(entry.Key);
                }
            }

            foreach (ulong key in s_SharedValidatedShotKeyBuffer)
            {
                if (m_ValidatedShotReferences.TryGetValue(key, out var shotReference) &&
                    shotReference.AcceptedCharacterTargets != null)
                {
                    shotReference.AcceptedCharacterTargets.Clear();
                }

                m_ValidatedShotReferences.Remove(key);
            }
        }
    }
}
#endif
