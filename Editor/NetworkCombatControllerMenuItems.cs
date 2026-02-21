using UnityEngine;
using UnityEditor;
using Arawn.GameCreator2.Networking;

namespace Arawn.EnemyMasses.Editor.GameCreator2
{
    /// <summary>
    /// Adds a context menu item to create a Network Combat Controller in the scene hierarchy.
    /// </summary>
    public static class NetworkCombatControllerMenuItems
    {
        [MenuItem("GameObject/Game Creator/Networking/Network Combat Controller", false, 100)]
        public static void CreateNetworkCombatController(MenuCommand menuCommand)
        {
            var go = new GameObject("Network Combat Controller");
            go.AddComponent<NetworkCombatController>();
            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Network Combat Controller");
            Selection.activeObject = go;
        }
    }
}
