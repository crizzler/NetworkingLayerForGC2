using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;

namespace Arawn.GameCreator2.Networking
{
    [Title("On Network Core Request Completed")]
    [Description("Executed when a Core visual-scripting request receives authoritative approval, rejection, or timeout")]
    [Category("Network/Core/On Request Completed")]
    [Keywords("Network", "Core", "Request", "Response", "Approved", "Rejected", "Timeout")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class EventNetworkCoreRequestCompleted : Event
    {
        [UnityEngine.SerializeField]
        private NetworkCoreVisualScriptingOperation m_Operation =
            NetworkCoreVisualScriptingOperation.None;

        [UnityEngine.SerializeField]
        private PropertyGetGameObject m_Character = GetGameObjectLocalNetworkPlayer.Create();

        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            NetworkCoreVisualScriptingContext.RequestCompleted += OnRequestCompleted;
        }

        protected override void OnDisable(Trigger trigger)
        {
            NetworkCoreVisualScriptingContext.RequestCompleted -= OnRequestCompleted;
            base.OnDisable(trigger);
        }

        private void OnRequestCompleted(NetworkCoreVisualScriptingResult result)
        {
            if (m_Operation == NetworkCoreVisualScriptingOperation.None ||
                result.Operation != m_Operation || !MatchesCharacter(result)) return;
            Execute(result);
        }

        private bool MatchesCharacter(NetworkCoreVisualScriptingResult result)
        {
            UnityEngine.GameObject gameObject = m_Character?.Get(Self);
            NetworkCharacter character = gameObject != null
                ? gameObject.GetComponent<NetworkCharacter>() ??
                  gameObject.GetComponentInParent<NetworkCharacter>()
                : null;
            return character != null && character.NetworkId != 0 &&
                   character.NetworkId == result.CharacterNetworkId;
        }

        private async void Execute(NetworkCoreVisualScriptingResult result)
        {
            try
            {
                await Task.Yield();
                using (NetworkCoreVisualScriptingContext.Push(result))
                {
                    await m_Trigger.Execute(Self);
                }
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogException(exception, Self);
            }
        }
    }
}
