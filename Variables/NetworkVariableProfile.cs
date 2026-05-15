using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Variables;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [CreateAssetMenu(
        fileName = "NetworkVariableProfile",
        menuName = "Game Creator/Network/Variable Profile",
        order = 520)]
    public sealed class NetworkVariableProfile : ScriptableObject
    {
        [Serializable]
        public struct NameBinding
        {
            public string Name;
            public bool AllowClientWrites;
        }

        [Serializable]
        public struct LocalListBinding
        {
            public string BindingId;
            public bool SyncList;
            public bool AllowClientWrites;
        }

        [Serializable]
        public struct GlobalNameBinding
        {
            public GlobalNameVariables Variables;
            public string Name;
            public bool AllowClientWrites;
        }

        [Serializable]
        public struct GlobalListBinding
        {
            public GlobalListVariables Variables;
            public string BindingId;
            public bool AllowClientWrites;
        }

        [Header("Local Variables")]
        [SerializeField] private NameBinding[] m_LocalNameVariables = Array.Empty<NameBinding>();
        [SerializeField] private LocalListBinding m_LocalListVariables = new LocalListBinding
        {
            BindingId = "Local List",
            SyncList = false,
            AllowClientWrites = true
        };

        [Header("Global Variables")]
        [SerializeField] private GlobalNameBinding[] m_GlobalNameVariables = Array.Empty<GlobalNameBinding>();
        [SerializeField] private GlobalListBinding[] m_GlobalListVariables = Array.Empty<GlobalListBinding>();

        [Header("Runtime")]
        [Tooltip("If true, managers send full snapshots when a client finishes loading a scene.")]
        [SerializeField] private bool m_SnapshotOnLateJoin = true;

        [Tooltip("If true, normal GC2 local variable changes on an owner controller are forwarded as network requests.")]
        [SerializeField] private bool m_AutoSendOwnerLocalChanges = false;

        public bool SnapshotOnLateJoin => m_SnapshotOnLateJoin;
        public bool AutoSendOwnerLocalChanges => m_AutoSendOwnerLocalChanges;

        public int ProfileHash => StableHashUtility.GetStableHash(GetProfileId());

        public NameBinding[] LocalNameVariables => m_LocalNameVariables ?? Array.Empty<NameBinding>();
        public GlobalNameBinding[] GlobalNameVariables => m_GlobalNameVariables ?? Array.Empty<GlobalNameBinding>();
        public GlobalListBinding[] GlobalListVariables => m_GlobalListVariables ?? Array.Empty<GlobalListBinding>();

        public bool AllowsLocalList => m_LocalListVariables.SyncList;
        public int LocalListHash => StableHashUtility.GetStableHash(GetLocalListBindingId());

        public bool IsLocalNameAllowed(string variableName, bool requireClientWrite)
        {
            if (string.IsNullOrWhiteSpace(variableName)) return false;

            var bindings = LocalNameVariables;
            for (int i = 0; i < bindings.Length; i++)
            {
                if (!string.Equals(bindings[i].Name, variableName, StringComparison.Ordinal)) continue;
                return !requireClientWrite || bindings[i].AllowClientWrites;
            }

            return false;
        }

        public bool IsLocalListAllowed(bool requireClientWrite)
        {
            if (!m_LocalListVariables.SyncList) return false;
            return !requireClientWrite || m_LocalListVariables.AllowClientWrites;
        }

        public bool IsGlobalNameAllowed(GlobalNameVariables asset, string variableName, bool requireClientWrite)
        {
            if (asset == null || string.IsNullOrWhiteSpace(variableName)) return false;

            var bindings = GlobalNameVariables;
            for (int i = 0; i < bindings.Length; i++)
            {
                if (bindings[i].Variables != asset) continue;
                if (!string.Equals(bindings[i].Name, variableName, StringComparison.Ordinal)) continue;
                return !requireClientWrite || bindings[i].AllowClientWrites;
            }

            return false;
        }

        public bool IsGlobalListAllowed(GlobalListVariables asset, bool requireClientWrite)
        {
            if (asset == null) return false;

            var bindings = GlobalListVariables;
            for (int i = 0; i < bindings.Length; i++)
            {
                if (bindings[i].Variables != asset) continue;
                return !requireClientWrite || bindings[i].AllowClientWrites;
            }

            return false;
        }

        public bool TryResolveGlobalNameAsset(int profileHash, int variableHash, out GlobalNameVariables asset, out string variableName)
        {
            asset = null;
            variableName = null;
            if (profileHash != ProfileHash) return false;

            var bindings = GlobalNameVariables;
            for (int i = 0; i < bindings.Length; i++)
            {
                string name = bindings[i].Name;
                if (GetGlobalNameBindingHash(bindings[i].Variables, name) != variableHash) continue;

                asset = bindings[i].Variables;
                variableName = name;
                return asset != null;
            }

            return false;
        }

        public bool TryResolveGlobalListAsset(int profileHash, int variableHash, out GlobalListVariables asset)
        {
            asset = null;
            if (profileHash != ProfileHash) return false;

            var bindings = GlobalListVariables;
            for (int i = 0; i < bindings.Length; i++)
            {
                GlobalListVariables candidate = bindings[i].Variables;
                if (candidate == null) continue;
                if (GetGlobalAssetHash(candidate) != variableHash) continue;

                asset = candidate;
                return true;
            }

            return false;
        }

        public string GetProfileId()
        {
            return !string.IsNullOrWhiteSpace(name) ? name : GetInstanceID().ToString();
        }

        public string GetLocalListBindingId()
        {
            return string.IsNullOrWhiteSpace(m_LocalListVariables.BindingId)
                ? "Local List"
                : m_LocalListVariables.BindingId;
        }

        public static int GetGlobalAssetHash(TGlobalVariables asset)
        {
            if (asset == null) return 0;
            IdString uniqueId = asset.UniqueID;
            string identity = string.IsNullOrEmpty(uniqueId.String) ? asset.name : uniqueId.String;
            return StableHashUtility.GetStableHash(identity);
        }

        public static int GetVariableHash(string variableName)
        {
            return StableHashUtility.GetStableHash(variableName ?? string.Empty);
        }

        public static int GetGlobalNameBindingHash(GlobalNameVariables asset, string variableName)
        {
            int assetHash = GetGlobalAssetHash(asset);
            return StableHashUtility.GetStableHash($"{assetHash}:{variableName ?? string.Empty}");
        }
    }
}
