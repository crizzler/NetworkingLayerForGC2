using System;
using UnityEngine;

namespace Arawn.NetworkingCore
{
    /// <summary>
    /// Wrapper for network timestamps that handles server time synchronization.
    /// Network-agnostic - works with any networking solution.
    /// </summary>
    [Serializable]
    public struct NetworkTimestamp : IEquatable<NetworkTimestamp>, IComparable<NetworkTimestamp>
    {
        /// <summary>
        /// Server time in seconds since server start.
        /// </summary>
        public double serverTime;
        
        /// <summary>
        /// Local time when this timestamp was recorded (for RTT calculation).
        /// </summary>
        public float localTime;
        
        /// <summary>
        /// Tick number if using tick-based networking (0 if not applicable).
        /// </summary>
        public uint tick;
        
        /// <summary>
        /// Creates a timestamp from server time.
        /// </summary>
        public static NetworkTimestamp FromServerTime(double serverTime, uint tick = 0)
        {
            return new NetworkTimestamp
            {
                serverTime = serverTime,
                localTime = Time.time,
                tick = tick
            };
        }
        
        /// <summary>
        /// Creates a timestamp from current local time (for single-player or host).
        /// </summary>
        public static NetworkTimestamp Now()
        {
            return new NetworkTimestamp
            {
                serverTime = Time.timeAsDouble,
                localTime = Time.time,
                tick = 0
            };
        }
        
        /// <summary>
        /// Creates a timestamp from tick number (for tick-based networking like Fusion).
        /// </summary>
        public static NetworkTimestamp FromTick(uint tick, double tickDuration)
        {
            return new NetworkTimestamp
            {
                serverTime = tick * tickDuration,
                localTime = Time.time,
                tick = tick
            };
        }
        
        /// <summary>
        /// Calculates the difference between this timestamp and another.
        /// </summary>
        public double DifferenceFrom(NetworkTimestamp other)
        {
            return serverTime - other.serverTime;
        }
        
        /// <summary>
        /// Returns a timestamp offset by the given seconds.
        /// </summary>
        public NetworkTimestamp Offset(double seconds)
        {
            return new NetworkTimestamp
            {
                serverTime = serverTime + seconds,
                localTime = localTime,
                tick = tick
            };
        }
        
        // Operators
        public static bool operator ==(NetworkTimestamp a, NetworkTimestamp b) => 
            Math.Abs(a.serverTime - b.serverTime) < 0.0001;
        
        public static bool operator !=(NetworkTimestamp a, NetworkTimestamp b) => 
            !(a == b);
        
        public static bool operator <(NetworkTimestamp a, NetworkTimestamp b) => 
            a.serverTime < b.serverTime;
        
        public static bool operator >(NetworkTimestamp a, NetworkTimestamp b) => 
            a.serverTime > b.serverTime;
        
        public static bool operator <=(NetworkTimestamp a, NetworkTimestamp b) => 
            a.serverTime <= b.serverTime;
        
        public static bool operator >=(NetworkTimestamp a, NetworkTimestamp b) => 
            a.serverTime >= b.serverTime;
        
        public static double operator -(NetworkTimestamp a, NetworkTimestamp b) => 
            a.serverTime - b.serverTime;
        
        // IEquatable
        public bool Equals(NetworkTimestamp other) => this == other;
        public override bool Equals(object obj) => obj is NetworkTimestamp ts && this == ts;
        public override int GetHashCode() => serverTime.GetHashCode();
        
        // IComparable
        public int CompareTo(NetworkTimestamp other) => serverTime.CompareTo(other.serverTime);
        
        public override string ToString() => $"[T:{serverTime:F3}s, Tick:{tick}]";
    }
    
    /// <summary>
    /// Tracks Round-Trip Time (RTT) / ping for a network connection.
    /// </summary>
    public class RTTTracker
    {
        private readonly float[] m_Samples;
        private int m_SampleIndex;
        private int m_SampleCount;
        
        /// <summary>
        /// Average RTT in seconds.
        /// </summary>
        public float AverageRTT { get; private set; }
        
        /// <summary>
        /// Smoothed RTT (exponential moving average).
        /// </summary>
        public float SmoothedRTT { get; private set; }
        
        /// <summary>
        /// RTT variance for jitter calculation.
        /// </summary>
        public float RTTVariance { get; private set; }
        
        /// <summary>
        /// One-way latency estimate (RTT / 2).
        /// </summary>
        public float OneWayLatency => SmoothedRTT * 0.5f;
        
        public RTTTracker(int sampleCount = 10)
        {
            m_Samples = new float[sampleCount];
            m_SampleIndex = 0;
            m_SampleCount = 0;
            AverageRTT = 0.1f; // Default 100ms
            SmoothedRTT = 0.1f;
            RTTVariance = 0.01f;
        }
        
        /// <summary>
        /// Add a new RTT sample.
        /// </summary>
        public void AddSample(float rtt)
        {
            m_Samples[m_SampleIndex] = rtt;
            m_SampleIndex = (m_SampleIndex + 1) % m_Samples.Length;
            m_SampleCount = Mathf.Min(m_SampleCount + 1, m_Samples.Length);
            
            // Calculate average
            float sum = 0f;
            for (int i = 0; i < m_SampleCount; i++)
                sum += m_Samples[i];
            AverageRTT = sum / m_SampleCount;
            
            // Smoothed RTT (exponential moving average)
            const float alpha = 0.125f; // RFC 6298 recommendation
            SmoothedRTT = (1f - alpha) * SmoothedRTT + alpha * rtt;
            
            // Variance
            float diff = Mathf.Abs(rtt - SmoothedRTT);
            const float beta = 0.25f;
            RTTVariance = (1f - beta) * RTTVariance + beta * diff;
        }
        
        /// <summary>
        /// Get the recommended rewind time for lag compensation.
        /// Accounts for RTT + some jitter buffer.
        /// </summary>
        public float GetRewindTime()
        {
            // RTT + 2 * variance for jitter compensation
            return SmoothedRTT + 2f * RTTVariance;
        }
    }
}
