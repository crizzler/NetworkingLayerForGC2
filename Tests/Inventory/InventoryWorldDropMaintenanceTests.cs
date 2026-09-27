#if GC2_INVENTORY
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using GameCreator.Runtime.Inventory;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Arawn.GameCreator2.Networking.Inventory.Tests
{
    public sealed class InventoryWorldDropMaintenanceTests
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<Object> m_Objects = new();
        private NetworkInventoryManager m_Manager;
        private Action<float> m_Tick;
        private static Type Controller => typeof(NetworkInventoryController);
        private static IDictionary Registry => (IDictionary)Controller.GetField("s_ServerDroppedWorldItems", Static).GetValue(null);
        private static IDictionary Instances => (IDictionary)Controller.GetField("s_DroppedItemInstances", Static).GetValue(null);
        private static long Scans => (long)Controller.GetField("s_DroppedWorldMaintenanceScans", Static).GetValue(null);

        [SetUp]
        public void SetUp()
        {
            Controller.GetMethod("ClearDroppedWorldItems", Static).Invoke(null, null);
            m_Manager = Track(new GameObject("World maintenance manager")).AddComponent<NetworkInventoryManager>();
            // Unity does not consistently call Awake in EditMode. Use the actual singleton hook.
            typeof(NetworkSingleton<NetworkInventoryManager>).GetMethod("Awake", Instance).Invoke(m_Manager, null);
            Set(m_Manager, "m_IsServer", true);
            m_Tick = (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), m_Manager,
                typeof(NetworkInventoryManager).GetMethod("MaintainWorldDrops", Instance));
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = m_Objects.Count - 1; i >= 0; --i)
                if (m_Objects[i] != null) Object.DestroyImmediate(m_Objects[i]);
            m_Objects.Clear();
            Controller.GetMethod("ClearDroppedWorldItems", Static).Invoke(null, null);
        }

        [Test]
        public void ScheduledMaintenance_ExtraControllersDoNotMultiplyGlobalScans()
        {
            Drop(1, 0f);
            var updates = new List<Action>();
            for (int i = 0; i < 32; ++i)
            {
                NetworkInventoryController c = BagController((uint)(i + 10));
                Set(c, "m_FullSyncInterval", 0f); Set(c, "m_DeltaSyncInterval", float.MaxValue);
                updates.Add((Action)Delegate.CreateDelegate(typeof(Action), c, Controller.GetMethod("Update", Instance)));
            }
            long before = Scans;
            for (int frame = 0; frame < 60; ++frame)
            {
                foreach (Action update in updates) update();
                m_Tick(frame / 60f);
            }
            Assert.That(Scans - before, Is.EqualTo(1));
            m_Tick(1f); Assert.That(Scans - before, Is.EqualTo(2));
        }

        [Test]
        public void ScheduledMaintenance_NoControllersExpiresAtTheNextUnscaledSweep()
        {
            GameObject expired = Drop(2, 0f, true);
            Assert.That(m_Manager.ControllerCount, Is.Zero);
            m_Tick(599.5f); Assert.That(expired != null && expired.activeSelf, Is.True);
            m_Tick(600.49f); Assert.That(Registry.Contains(2L), Is.True);
            m_Tick(600.5f); Assert.That(Registry.Contains(2L), Is.False);
            Assert.That(expired == null || !expired.activeSelf, Is.True);
        }

        [Test]
        public void LastControllerLeaves_ActiveManagerStillMaintainsTheWorldRegistry()
        {
            NetworkInventoryController c = BagController(77);
            m_Manager.RegisterController(c.NetworkId, c);
            GameObject drop = Drop(27, 0f, true);
            m_Manager.UnregisterController(c.NetworkId);
            Object.DestroyImmediate(c.gameObject);
            Assert.That(m_Manager.ControllerCount, Is.Zero);
            Assert.That(Registry.Contains(27L), Is.True);
            m_Tick(600f);
            Assert.That(Registry.Count + Instances.Count, Is.Zero);
            Assert.That(drop == null || !drop.activeSelf, Is.True);
        }

        [Test]
        public void ClaimTime_ExpiredTargetIsRejectedBeforeSweepWithoutScanningOtherDrops()
        {
            NetworkInventoryController c = BagController(42);
            Drop(3, Time.unscaledTime - 600.1f, true);
            Drop(4, Time.unscaledTime - 700f, true);
            Drop(5, Time.unscaledTime, true);
            Set(m_Manager, "m_NextWorldDropMaintenance", Time.unscaledTime + 1f);
            var removed = new List<long>(); m_Manager.OnBroadcastDroppedItemRemoved += b => removed.Add(b.RuntimeIdHash);
            long before = Scans;
            NetworkPickupResponse response = c.ProcessPickupRequest(new NetworkPickupRequest
                { RuntimeIdHash = 3, PickerBagNetworkId = c.NetworkId, SourceBagNetworkId = 77 }, 42);
            Assert.That(response.Authorized, Is.False);
            Assert.That(response.RejectionReason, Is.EqualTo(InventoryRejectionReason.RuntimeItemNotFound));
            Assert.That(Scans, Is.EqualTo(before));
            Assert.That(removed, Is.EqualTo(new[] {3L}));
            Assert.That(Registry.Contains(4L) && Registry.Contains(5L), Is.True);
        }

        [Test]
        public void ScheduledMaintenance_UnexpiredSurvivesAndExpiredIsRemovedExactlyOnce()
        {
            GameObject live = Drop(6, 100f, true); Drop(7, 0f, true);
            var removed = new List<long>(); m_Manager.OnBroadcastDroppedItemRemoved += b => removed.Add(b.RuntimeIdHash);
            m_Tick(600f); m_Tick(601f);
            Assert.That(removed, Is.EqualTo(new[] {7L}));
            Assert.That(Registry.Contains(6L) && Instances.Contains(6L) && live.activeSelf, Is.True);
        }

        [Test]
        public void ScheduledMaintenance_ReentrantCallbackCannotStartAnotherScan()
        {
            Drop(8, 0f); Drop(9, 0f);
            var removed = new List<long>();
            m_Manager.OnBroadcastDroppedItemRemoved += b => { removed.Add(b.RuntimeIdHash); m_Tick(900f); };
            long before = Scans; m_Tick(600f);
            Assert.That(Scans - before, Is.EqualTo(1));
            Assert.That(removed, Is.EquivalentTo(new[] {8L, 9L}));
            Assert.That(Registry.Count, Is.Zero);
        }

        [Test]
        public void ScheduledMaintenance_CallbackReplacementSurvivesCapturedExpiredEntry()
        {
            Drop(10, 0f, true); Drop(11, 0f, true);
            GameObject replacement = null; var removed = new List<long>();
            m_Manager.OnBroadcastDroppedItemRemoved += b =>
            {
                removed.Add(b.RuntimeIdHash);
                if (removed.Count == 1)
                {
                    long other = b.RuntimeIdHash == 10 ? 11 : 10;
                    replacement = Drop(other, 600f, true);
                }
            };
            m_Tick(600f);
            Assert.That(removed.Count, Is.EqualTo(1));
            Assert.That(Registry.Count, Is.EqualTo(1));
            Assert.That(replacement != null && replacement.activeSelf, Is.True);
        }

        [Test]
        public void ScheduledMaintenance_ReplacingTheCurrentKeyDoesNotDestroyNewWorldObject()
        {
            GameObject original = Drop(12, 0f, true), replacement = null;
            m_Manager.OnBroadcastDroppedItemRemoved += _ => replacement = Drop(12, 600f, true);
            m_Tick(600f);
            Assert.That(original == null || !original.activeSelf, Is.True);
            Assert.That(Registry.Contains(12L) && replacement != null && replacement.activeSelf, Is.True);
        }

        [Test]
        public void ScheduledMaintenance_CallbackSessionResetDoesNotRemoveRestartedEntries()
        {
            Drop(13, 0f, true); Drop(14, 0f, true);
            var removed = new List<long>();
            m_Manager.OnBroadcastDroppedItemRemoved += b =>
            { removed.Add(b.RuntimeIdHash); m_Manager.ClearControllers(); Drop(14, 600f, true); };
            m_Tick(600f);
            Assert.That(removed.Count, Is.EqualTo(1));
            Assert.That(Registry.Contains(14L) && Instances.Contains(14L), Is.True);
            m_Manager.ClearControllers(); m_Manager.ClearControllers();
            Assert.That(Registry.Count + Instances.Count, Is.Zero);
        }

        [Test]
        public void ScheduledMaintenance_CallbackExceptionStillCleansAllExpiredEntries()
        {
            Drop(15, 0f, true); Drop(16, 0f, true); int calls = 0;
            m_Manager.OnBroadcastDroppedItemRemoved += _ => { if (++calls == 1) throw new InvalidOperationException("maintenance callback probe"); };
            LogAssert.Expect(LogType.Exception, "InvalidOperationException: maintenance callback probe");
            m_Tick(600f); m_Tick(601f);
            Assert.That(calls, Is.EqualTo(2)); Assert.That(Registry.Count + Instances.Count, Is.Zero);
        }

        [Test]
        public void ManagerReplacement_OldManagerDisableAndClearCannotEraseNewSession()
        {
            NetworkInventoryManager old = m_Manager;
            typeof(NetworkSingleton<NetworkInventoryManager>).GetField("s_Instance", Static).SetValue(null, null);
            m_Manager = Track(new GameObject("Replacement manager")).AddComponent<NetworkInventoryManager>();
            typeof(NetworkSingleton<NetworkInventoryManager>).GetMethod("Awake", Instance).Invoke(m_Manager, null);
            Set(m_Manager, "m_IsServer", true);
            GameObject live = Drop(17, 0f, true);
            old.enabled = false; old.ClearControllers();
            typeof(NetworkInventoryManager).GetMethod("OnDisable", Instance).Invoke(old, null);
            Assert.That(Registry.Contains(17L) && live != null && live.activeSelf, Is.True);
        }

        [Test]
        public void ServerStopRestart_ClearsOldDropsAndResetsMaintenanceDeadline()
        {
            GameObject old = Drop(25, 0f, true); m_Tick(500f);
            m_Manager.IsServer = false;
            Assert.That(Registry.Count + Instances.Count, Is.Zero);
            Assert.That(old == null || !old.activeSelf, Is.True);
            m_Manager.IsServer = true;
            Drop(26, -601f, true); m_Tick(0f);
            Assert.That(Registry.Count + Instances.Count, Is.Zero);
        }

        [Test]
        public void Replay_TargetsJoiningPeerAndNeverIncludesConsumedOrExpiredEntries()
        {
            Drop(18, Time.unscaledTime); Drop(19, Time.unscaledTime - 601f, true); Drop(20, Time.unscaledTime);
            Registry.Remove(20L); // Simulate an already accepted claim's authoritative commit.
            var sent = new List<long>(); var targets = new List<ulong>(); int broadcasts = 0;
            m_Manager.OnSendWorldDropToClient += (id, b) => { targets.Add(id); sent.Add(b.Item.RuntimeIdHash); };
            m_Manager.OnBroadcastItemDropped += _ => ++broadcasts;
            m_Manager.SendInitialState(123); m_Manager.SendInitialState(123);
            Assert.That(sent, Is.EqualTo(new[] {18L, 18L}));
            Assert.That(targets, Is.EqualTo(new[] {123UL, 123UL})); Assert.That(broadcasts, Is.Zero);
            Assert.That(Registry.Contains(19L), Is.False);
        }

        [Test]
        public void Replay_CallbackMutationAndNestedSynchronizationCannotReplayOldRegistration()
        {
            Drop(21, Time.unscaledTime); Drop(22, Time.unscaledTime);
            var sent = new List<long>();
            m_Manager.OnSendWorldDropToClient += (id, b) =>
            {
                sent.Add(b.Item.RuntimeIdHash);
                long other = b.Item.RuntimeIdHash == 21 ? 22 : 21;
                Drop(other, Time.unscaledTime); // Replace captured registration even under same key.
                m_Manager.SendInitialState(id);
            };
            m_Manager.SendInitialState(123); Assert.That(sent.Count, Is.EqualTo(1));
        }

        [Test]
        public void Replay_CompatibleUntargetedTransportStillReceivesLiveDropWithoutSourceBag()
        {
            Drop(23, Time.unscaledTime); int broadcasts = 0;
            m_Manager.OnBroadcastItemDropped += b => { Assert.That(b.SourceBagNetworkId, Is.EqualTo(77)); ++broadcasts; };
            Assert.That(m_Manager.ControllerCount, Is.Zero);
            m_Manager.SendInitialState(123); Assert.That(broadcasts, Is.EqualTo(1));
        }

        [Test]
        public void Replay_DuplicateLiveCreationDoesNotReplaceWorldInstance()
        {
            NetworkInventoryController c = BagController(55); Set(c, "m_IsServer", false);
            GameObject original = Drop(24, Time.unscaledTime, true);
            var broadcast = new NetworkItemDroppedBroadcast {SourceBagNetworkId = 77, Item = new NetworkRuntimeItem {RuntimeIdHash = 24}};
            for (int i = 0; i < 5; ++i) c.ReceiveItemDroppedBroadcast(broadcast);
            Assert.That(Instances.Count, Is.EqualTo(1)); Assert.That(original != null && original.activeSelf, Is.True);
        }

        [Test]
        public void ScheduledMaintenance_WarmAllocationAndScalingMeasurement()
        {
            foreach (int size in new[] {0, 100, 1000, 10000})
            {
                m_Manager.ClearControllers();
                for (int i = 1; i <= size; ++i) Drop(i, 0f);
                m_Tick(0f); Set(m_Manager, "m_NextWorldDropMaintenance", 0f);
                long beforeScans = Scans;
                var timer = new Stopwatch();
                long beforeBytes = GC.GetAllocatedBytesForCurrentThread(); timer.Start();
                for (int frame = 0; frame < 12000; ++frame) m_Tick(frame / 60f);
                timer.Stop(); long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                long scans = Scans - beforeScans;
                TestContext.WriteLine($"WORLD_DROP_MEASUREMENT drops={size} controllers=0 ticks=12000 cadence=1s scans={scans} allocatedBytes={allocated} elapsedMs={timer.Elapsed.TotalMilliseconds:F3}");
                Assert.That(scans, Is.EqualTo(size == 0 ? 0 : 200));
                Assert.That(allocated, Is.Zero, "Warmed scheduler/registry traversal only; excludes registration, transport and destruction");
            }
        }

        [Test]
        public void ScheduledMaintenance_ExpiredBufferReusesCapacityAfterWarmup()
        {
            for (int i = 1; i <= 1000; ++i) Drop(i, 0f);
            m_Tick(600f);
            for (int i = 1; i <= 1000; ++i) Drop(i, 0f);
            long before = GC.GetAllocatedBytesForCurrentThread(); m_Tick(601f);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            TestContext.WriteLine($"WORLD_DROP_EXPIRY_MEASUREMENT expired=1000 objects=0 transportListeners=0 allocatedBytes={allocated}");
            Assert.That(Registry.Count, Is.Zero); Assert.That(allocated, Is.Zero);
        }

        private GameObject Drop(long id, float time, bool world = false)
        {
            var item = new NetworkRuntimeItem {RuntimeIdHash = id};
            Controller.GetMethod("RememberServerDroppedWorldItem", Static).Invoke(null,
                new object[] {id, 77u, item, Vector3.zero});
            object entry = Registry[id]; entry.GetType().GetField("Time").SetValue(entry, time); Registry[id] = entry;
            if (!world) return null;
            // Replacement test fixtures dispose their old object before registering another.
            if (Instances.Contains(id))
            {
                object previous = Instances[id];
                var obj = (GameObject)previous.GetType().GetField("Instance").GetValue(previous);
                Instances.Remove(id); if (obj != null) Object.DestroyImmediate(obj);
            }
            GameObject instance = Track(new GameObject("Tracked drop " + id));
            Controller.GetMethod("RememberDroppedItemInstance", Static).Invoke(null,
                new object[] {id, instance, 77u, item, Vector3.zero});
            return instance;
        }

        private NetworkInventoryController BagController(uint id)
        {
            GameObject obj = Track(new GameObject("Maintenance bag " + id));
            var bag = obj.AddComponent<Bag>();
            typeof(Bag).GetMethod("Awake", Instance)?.Invoke(bag, null);
            var c = obj.AddComponent<NetworkInventoryController>();
            Controller.GetMethod("Awake", Instance).Invoke(c, null);
            Set(c, "m_StaticNetworkIdOverride", id); Set(c, "m_IsServer", true);
            return c;
        }
        private T Track<T>(T obj) where T : Object { m_Objects.Add(obj); return obj; }
        private static void Set(object target, string name, object value)
            => target.GetType().GetField(name, Instance).SetValue(target, value);
    }
}
#endif
