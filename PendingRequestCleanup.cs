using System;
using System.Collections.Generic;

namespace Arawn.GameCreator2.Networking
{
    public interface ITimedPendingRequest
    {
        float PendingSentTime { get; }
    }

    public interface ITimeoutAwarePendingRequest : ITimedPendingRequest
    {
        bool TryInvokeTimeout();
    }

    public static class PendingRequestCleanup
    {
        public static int RemoveTimedOut<TKey, TValue>(
            Dictionary<TKey, TValue> pending,
            List<TKey> removalBuffer,
            float now,
            float timeout,
            Action<TValue> onRemoved = null,
            Action<Exception> onRemoveException = null)
            where TValue : struct, ITimedPendingRequest
        {
            return RemoveTimedOut(
                pending,
                removalBuffer,
                now,
                timeout,
                request => request.PendingSentTime,
                onRemoved,
                onRemoveException);
        }

        public static int RemoveTimedOut<TKey, TValue>(
            Dictionary<TKey, TValue> pending,
            List<TKey> removalBuffer,
            float now,
            float timeout,
            Func<TValue, float> sentTimeSelector,
            Action<TValue> onRemoved = null,
            Action<Exception> onRemoveException = null)
        {
            if (pending == null || pending.Count == 0) return 0;
            if (removalBuffer == null) throw new ArgumentNullException(nameof(removalBuffer));
            if (sentTimeSelector == null) throw new ArgumentNullException(nameof(sentTimeSelector));

            removalBuffer.Clear();
            foreach (var kvp in pending)
            {
                if (now - sentTimeSelector(kvp.Value) > timeout)
                {
                    removalBuffer.Add(kvp.Key);
                }
            }

            int removedCount = 0;
            for (int i = 0; i < removalBuffer.Count; i++)
            {
                TKey key = removalBuffer[i];
                if (!pending.TryGetValue(key, out TValue value))
                {
                    continue;
                }

                if (!pending.Remove(key))
                {
                    continue;
                }

                removedCount++;
                if (onRemoved == null)
                {
                    continue;
                }

                try
                {
                    onRemoved(value);
                }
                catch (Exception exception)
                {
                    onRemoveException?.Invoke(exception);
                }
            }

            return removedCount;
        }
    }
}
