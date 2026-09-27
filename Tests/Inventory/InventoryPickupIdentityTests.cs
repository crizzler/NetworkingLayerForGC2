#if GC2_INVENTORY
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Arawn.GameCreator2.Networking.Editor;
using GameCreator.Runtime.Inventory;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace Arawn.GameCreator2.Networking.Inventory.Tests
{
    public sealed class InventoryPickupIdentityTests
    {
        const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        readonly List<Object> cleanup=new();
        readonly List<Scene> scenes=new();
        readonly List<string> sceneAssets=new();
        [TearDown] public void Cleanup()
        {
            for(int i=cleanup.Count-1;i>=0;i--) if(cleanup[i]!=null) Object.DestroyImmediate(cleanup[i]);
            cleanup.Clear();
            foreach(Scene scene in scenes) if(scene.IsValid() && scene.isLoaded)
            {
                if(SceneManager.sceneCount==1) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                else EditorSceneManager.CloseScene(scene,true);
            }
            scenes.Clear();
            foreach(string path in sceneAssets) AssetDatabase.DeleteAsset(path);
            sceneAssets.Clear();
        }
        T Keep<T>(T value) where T:Object {cleanup.Add(value);return value;}
        static void Set(object value,string name,object data) => value.GetType().GetField(name,Private).SetValue(value,data);
        NetworkInventoryPickupSource Source(string name,uint id=0,Transform parent=null)
        {
            var go=Keep(new GameObject(name));go.SetActive(false);go.transform.SetParent(parent,false);
            var source=go.AddComponent<NetworkInventoryPickupSource>();Set(source,"m_PickupId",id);return source;
        }
        [TestCase("delete")][TestCase("reorder")][TestCase("reparent")]
        public void AutomaticId_RemainsStableAfterHierarchyMutation(string mutation)
        {
            var root=Keep(new GameObject("Identity test parent"));
            var sibling=Keep(new GameObject("Earlier sibling"));sibling.transform.SetParent(root.transform,false);
            var source=Source("Pickup",parent:root.transform);uint before=source.PickupId;
            Assert.That(before,Is.Not.Zero);
            if(mutation=="delete") Object.DestroyImmediate(sibling);
            else if(mutation=="reorder") source.transform.SetSiblingIndex(0);
            else source.transform.SetParent(null,false);
            Assert.That(source.PickupId,Is.EqualTo(before));
        }
        [Test]
        public void AutomaticId_UnregisterAfterReparentDoesNotLeaveStaleEntry()
        {
            var manager=Keep(new GameObject("Identity manager")).AddComponent<NetworkInventoryManager>();
            var root=Keep(new GameObject("Original parent"));var source=Source("Pickup",parent:root.transform);
            manager.RegisterPickupSource(source);source.transform.SetParent(null,false);
            manager.UnregisterPickupSource(source);
            var registry=(IDictionary)typeof(NetworkInventoryManager).GetField("m_PickupSources",Private).GetValue(manager);
            Assert.That(registry.Count,Is.Zero);
        }
        [TestCase(false)][TestCase(true)]
        public void DuplicateSerializedIds_FailClosedRegardlessOfRegistrationOrder(bool reverse)
        {
            var manager=Keep(new GameObject("Collision manager")).AddComponent<NetworkInventoryManager>();
            var first=Source("A",900u);var second=Source("B",900u);
            manager.RegisterPickupSource(reverse?second:first);manager.RegisterPickupSource(reverse?first:second);
            object[] args={new NetworkPickupRequest {PropNetworkId=900u,PickerBagNetworkId=123u},1u,null};
            typeof(NetworkInventoryManager).GetMethod("TryProcessRegisteredPickup",Private).Invoke(manager,args);
            Assert.That(((NetworkPickupResponse)args[2]).Authorized,Is.False);
            Assert.That(((NetworkPickupResponse)args[2]).RejectionReason,Is.EqualTo(InventoryRejectionReason.IdentityMismatch));
        }
        [Test]
        public void AdditiveScenes_AutomaticIdsIncludeAuthoredSceneIdentity()
        {
            Scene a=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);scenes.Add(a);
            string pathA="Assets/Arawn/InventoryRegression/Identity-"+Guid.NewGuid().ToString("N")+".unity";
            sceneAssets.Add(pathA);Assert.That(EditorSceneManager.SaveScene(a,pathA),Is.True);
            Scene b=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);scenes.Add(b);
            string pathB="Assets/Arawn/InventoryRegression/Identity-"+Guid.NewGuid().ToString("N")+".unity";
            sceneAssets.Add(pathB);Assert.That(EditorSceneManager.SaveScene(b,pathB),Is.True);
            var first=Source("Identical pickup");var second=Source("Identical pickup");
            SceneManager.MoveGameObjectToScene(first.gameObject,a);SceneManager.MoveGameObjectToScene(second.gameObject,b);
            Assert.That(first.PickupId,Is.Not.EqualTo(second.PickupId));
            var manager=Keep(new GameObject("Additive identity manager")).AddComponent<NetworkInventoryManager>();
            Set(first,"m_PickupId",123u);Set(second,"m_PickupId",123u);
            manager.RegisterPickupSource(first);manager.RegisterPickupSource(second);
            object[] args={new NetworkPickupRequest {PropNetworkId=123u},1u,null};
            typeof(NetworkInventoryManager).GetMethod("TryProcessRegisteredPickup",Private).Invoke(manager,args);
            Assert.That(((NetworkPickupResponse)args[2]).RejectionReason,Is.EqualTo(InventoryRejectionReason.IdentityMismatch));
            manager.UnregisterPickupSource(first);
            var registry=(IDictionary)typeof(NetworkInventoryManager).GetField("m_PickupSources",Private).GetValue(manager);
            Assert.That(registry[123u],Is.SameAs(second));
            manager.UnregisterPickupSource(second);Assert.That(registry.Count,Is.Zero);
        }

        [Test]
        public void Wizard_ConvertsLoadedInactiveStockPrefabSelfItemAndRemainsIdempotent()
        {
            Scene scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);scenes.Add(scene);
            var item=AssetDatabase.LoadAssetAtPath<Item>("Assets/Plugins/GameCreator/Installs/Inventory.Items@1.3.13/Potion_Health.asset");
            Assert.That(item,Is.Not.Null,"Declared installed Inventory examples must be provisioned");
            var prefab=(GameObject)new SerializedObject(item).FindProperty("m_Prefab").objectReferenceValue;
            var instance=Keep((GameObject)PrefabUtility.InstantiatePrefab(prefab,scene));instance.SetActive(false);
            Assert.That(instance.GetComponentsInChildren<NetworkInventoryPickupSource>(true),Is.Empty);
            Assert.That(InventorySceneSetupTools.ConvertStockScenePickups(scene,false),Is.EqualTo(1));
            var sources=instance.GetComponentsInChildren<NetworkInventoryPickupSource>(true);
            Assert.That(sources.Length,Is.EqualTo(1));Assert.That(sources[0].Item,Is.SameAs(item));
            uint id=sources[0].PickupId;Assert.That(new SerializedObject(sources[0]).FindProperty("m_PickupId").uintValue,Is.Not.Zero);
            Assert.That(InventorySceneSetupTools.ConvertStockScenePickups(scene,false),Is.EqualTo(1));
            Assert.That(instance.GetComponentsInChildren<NetworkInventoryPickupSource>(true).Length,Is.EqualTo(1));
            Assert.That(sources[0].PickupId,Is.EqualTo(id));
            Assert.That(prefab.GetComponentsInChildren<NetworkInventoryPickupSource>(true),Is.Empty);
        }
    }
}
#endif
