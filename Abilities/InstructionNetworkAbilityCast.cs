using System;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking;
using DaimahouGames.Runtime.Abilities;
using DaimahouGames.Runtime.Pawns;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Abilities
{
    public enum NetworkAbilityCastTargetMode
    {
        CasterPosition,
        FacingDirection
    }

    [Version(1, 0, 0)]
    [Title("Network Cast Ability")]
    [Category("Network/Abilities/Cast")]
    [Image(typeof(IconAbility), ColorTheme.Type.Blue)]
    [Description("Requests a server-authoritative DaimahouGames Ability cast through the active network transport")]
    [Serializable]
    public sealed class InstructionNetworkAbilityCast : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Caster = GetGameObjectPlayer.Create();
        [SerializeField] private Ability m_Ability;
        [SerializeField] private NetworkAbilityCastTargetMode m_TargetMode = NetworkAbilityCastTargetMode.FacingDirection;
        [SerializeField] private PropertyGetDecimal m_Distance = new(5f);
        [SerializeField] private bool m_WaitForResponse;
        [SerializeField] private bool m_LogDiagnostics;

        public override string Title => string.Format(
            "{0} Network Cast {1}",
            m_Caster,
            m_Ability != null ? TextUtils.Humanize(m_Ability.name) : "(none)"
        );

        protected override Task Run(Args args)
        {
            GameObject casterObject = m_Caster.Get(args);
            if (casterObject == null)
            {
                LogWarning("No caster GameObject resolved.");
                return Task.CompletedTask;
            }

            if (m_Ability == null)
            {
                LogWarning($"No Ability asset assigned for caster '{casterObject.name}'.");
                return Task.CompletedTask;
            }

            Pawn pawn = casterObject.GetComponent<Pawn>();
            if (pawn == null)
            {
                LogWarning($"Caster '{casterObject.name}' has no Daimahou Pawn component.");
                return Task.CompletedTask;
            }

            EnsurePawnRegistered(casterObject, pawn);

            uint networkId = NetworkAbilitiesManager.GetNetworkIdForPawn(pawn);
            if (networkId == 0)
            {
                LogWarning(
                    $"Caster '{casterObject.name}' is not registered with NetworkAbilitiesManager yet. " +
                    "Wait until the PurrNet Abilities bridge has registered spawned pawns.");
                return Task.CompletedTask;
            }

            Vector3 targetPosition = ResolveTargetPosition(casterObject, args);
            Caster caster = pawn.GetFeature<Caster>();
            Log(
                $"request caster='{casterObject.name}' netId={networkId} ability='{m_Ability.name}' " +
                $"abilityHash={m_Ability.ID.Hash} targetMode={m_TargetMode} target={targetPosition} " +
                $"casterFeature={caster != null} {DescribeCasterSlots(caster, m_Ability)}");

            if (!m_WaitForResponse)
            {
                NetworkAbilitiesManager.RequestCastAtPosition(pawn, m_Ability, targetPosition);
                Log($"sent cast request caster='{casterObject.name}' ability='{m_Ability.name}' target={targetPosition}");
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource<bool>();
            NetworkAbilitiesManager.RequestCastAtPosition(
                pawn,
                m_Ability,
                targetPosition,
                response =>
                {
                    Log(
                        $"cast response caster='{casterObject.name}' ability='{m_Ability.name}' " +
                        $"approved={response.Approved} reject={response.RejectReason}");
                    completion.TrySetResult(response.Approved);
                });

            return completion.Task;
        }

        private Vector3 ResolveTargetPosition(GameObject casterObject, Args args)
        {
            Transform casterTransform = casterObject.transform;

            switch (m_TargetMode)
            {
                case NetworkAbilityCastTargetMode.CasterPosition:
                    return casterTransform.position;

                case NetworkAbilityCastTargetMode.FacingDirection:
                default:
                    float distance = Mathf.Max(0f, (float)m_Distance.Get(args));
                    return casterTransform.position + casterTransform.forward * distance;
            }
        }

        private static void EnsurePawnRegistered(GameObject casterObject, Pawn pawn)
        {
            if (NetworkAbilitiesManager.GetNetworkIdForPawn(pawn) != 0) return;

            NetworkCharacter networkCharacter = casterObject.GetComponent<NetworkCharacter>();
            if (networkCharacter == null || networkCharacter.NetworkId == 0) return;

            NetworkAbilitiesManager.RegisterPawn(pawn, networkCharacter.NetworkId);
        }

        private static string DescribeCasterSlots(Caster caster, Ability requestedAbility)
        {
            if (caster == null) return "slots=missing";

            const int MaxSlotsToProbe = 16;
            int populated = 0;
            int requestedSlot = -1;
            string names = string.Empty;

            for (int i = 0; i < MaxSlotsToProbe; i++)
            {
                Ability slotted = caster.GetSlottedAbility(i);
                if (slotted == null) continue;

                if (names.Length > 0) names += ",";
                names += $"{i}:{slotted.name}";
                populated++;

                if (requestedAbility != null && slotted == requestedAbility)
                {
                    requestedSlot = i;
                }
            }

            return $"populatedSlots={populated} requestedSlot={requestedSlot} slots=[{names}]";
        }

        private void Log(string message)
        {
            if (!m_LogDiagnostics) return;
            Debug.Log($"[NetworkAbilityInstruction] {message}");
        }

        private void LogWarning(string message)
        {
            if (!m_LogDiagnostics) return;
            Debug.LogWarning($"[NetworkAbilityInstruction] {message}");
        }
    }
}
