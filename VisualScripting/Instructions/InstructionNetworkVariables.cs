using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Variables;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Version(0, 1, 0)]
    [Title("Network Set Local Name Variable")]
    [Description("Requests a server-authoritative change to a profiled Local Name Variable")]
    [Category("Network/Variables/Set Local Name Variable")]
    [Parameter("Actor", "NetworkCharacter that owns the request")]
    [Parameter("Target", "GameObject with a NetworkVariableController and LocalNameVariables component")]
    [Parameter("Name", "Name of the GC2 Local Name Variable")]
    [Parameter("Value", "Supported network value to assign")]
    [Keywords("Network", "Variables", "Local", "Name", "Set")]
    [Image(typeof(IconNameVariable), ColorTheme.Type.Teal)]
    [Serializable]
    public sealed class InstructionNetworkSetLocalNameVariable : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();
        [SerializeField] private PropertyGetString m_Name = new PropertyGetString("my-variable");
        [SerializeField] private NetworkVariableInstructionValue m_Value = new();

        public override string Title => $"Network Set Local Name {m_Name}";

        protected override Task Run(Args args)
        {
            if (!NetworkVariableInstructionUtility.TryGetActorNetworkId(m_Actor, args, nameof(InstructionNetworkSetLocalNameVariable), out uint actorNetworkId) ||
                !NetworkVariableInstructionUtility.TryGetController(m_Target, args, nameof(InstructionNetworkSetLocalNameVariable), out var controller))
            {
                return Task.CompletedTask;
            }

            controller.RequestSetLocalName(m_Name.Get(args), m_Value.Get(args), actorNetworkId);
            return Task.CompletedTask;
        }
    }

    [Version(0, 1, 0)]
    [Title("Network Set Global Name Variable")]
    [Description("Requests a server-authoritative change to a profiled Global Name Variable")]
    [Category("Network/Variables/Set Global Name Variable")]
    [Parameter("Actor", "NetworkCharacter that owns the request")]
    [Parameter("Variables", "GC2 Global Name Variables asset")]
    [Parameter("Name", "Name of the GC2 Global Name Variable")]
    [Parameter("Value", "Supported network value to assign")]
    [Keywords("Network", "Variables", "Global", "Name", "Set")]
    [Image(typeof(IconNameVariable), ColorTheme.Type.Teal, typeof(OverlayDot))]
    [Serializable]
    public sealed class InstructionNetworkSetGlobalNameVariable : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] private GlobalNameVariables m_Variables;
        [SerializeField] private PropertyGetString m_Name = new PropertyGetString("my-variable");
        [SerializeField] private NetworkVariableInstructionValue m_Value = new();

        public override string Title => $"Network Set Global Name {m_Name}";

        protected override Task Run(Args args)
        {
            if (!NetworkVariableInstructionUtility.TryGetActorNetworkId(m_Actor, args, nameof(InstructionNetworkSetGlobalNameVariable), out uint actorNetworkId) ||
                !NetworkVariableInstructionUtility.TryGetManager(nameof(InstructionNetworkSetGlobalNameVariable), out var manager))
            {
                return Task.CompletedTask;
            }

            manager.RequestSetGlobalName(actorNetworkId, m_Variables, m_Name.Get(args), m_Value.Get(args));
            return Task.CompletedTask;
        }
    }

    [Version(0, 1, 0)]
    [Title("Network Set Local List Variable")]
    [Description("Requests a server-authoritative set operation on a profiled Local List Variable")]
    [Category("Network/Variables/Set Local List Variable")]
    [Parameter("Actor", "NetworkCharacter that owns the request")]
    [Parameter("Target", "GameObject with a NetworkVariableController and LocalListVariables component")]
    [Parameter("Index", "List index to set")]
    [Parameter("Value", "Supported network value to assign")]
    [Keywords("Network", "Variables", "Local", "List", "Set")]
    [Image(typeof(IconListVariable), ColorTheme.Type.Teal)]
    [Serializable]
    public sealed class InstructionNetworkSetLocalListVariable : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();
        [SerializeField] private PropertyGetInteger m_Index = new PropertyGetInteger(0);
        [SerializeField] private NetworkVariableInstructionValue m_Value = new();

        public override string Title => $"Network Set Local List [{m_Index}]";

        protected override Task Run(Args args)
        {
            if (!NetworkVariableInstructionUtility.TryGetActorNetworkId(m_Actor, args, nameof(InstructionNetworkSetLocalListVariable), out uint actorNetworkId) ||
                !NetworkVariableInstructionUtility.TryGetController(m_Target, args, nameof(InstructionNetworkSetLocalListVariable), out var controller))
            {
                return Task.CompletedTask;
            }

            controller.RequestSetLocalList(NetworkVariableInstructionUtility.GetIndex(m_Index, args), m_Value.Get(args), actorNetworkId);
            return Task.CompletedTask;
        }
    }

    [Version(0, 1, 0)]
    [Title("Network Push Local List Variable")]
    [Description("Requests a server-authoritative push operation on a profiled Local List Variable")]
    [Category("Network/Variables/Push Local List Variable")]
    [Parameter("Actor", "NetworkCharacter that owns the request")]
    [Parameter("Target", "GameObject with a NetworkVariableController and LocalListVariables component")]
    [Parameter("Value", "Supported network value to push")]
    [Keywords("Network", "Variables", "Local", "List", "Push")]
    [Image(typeof(IconListVariable), ColorTheme.Type.Teal, typeof(OverlayPlus))]
    [Serializable]
    public sealed class InstructionNetworkPushLocalListVariable : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();
        [SerializeField] private NetworkVariableInstructionValue m_Value = new();

        public override string Title => "Network Push Local List";

        protected override Task Run(Args args)
        {
            if (!NetworkVariableInstructionUtility.TryGetActorNetworkId(m_Actor, args, nameof(InstructionNetworkPushLocalListVariable), out uint actorNetworkId) ||
                !NetworkVariableInstructionUtility.TryGetController(m_Target, args, nameof(InstructionNetworkPushLocalListVariable), out var controller))
            {
                return Task.CompletedTask;
            }

            controller.RequestPushLocalList(m_Value.Get(args), actorNetworkId);
            return Task.CompletedTask;
        }
    }

    [Version(0, 1, 0)]
    [Title("Network Remove Local List Variable")]
    [Description("Requests a server-authoritative remove operation on a profiled Local List Variable")]
    [Category("Network/Variables/Remove Local List Variable")]
    [Parameter("Actor", "NetworkCharacter that owns the request")]
    [Parameter("Target", "GameObject with a NetworkVariableController and LocalListVariables component")]
    [Parameter("Index", "List index to remove")]
    [Keywords("Network", "Variables", "Local", "List", "Remove")]
    [Image(typeof(IconListVariable), ColorTheme.Type.Teal, typeof(OverlayMinus))]
    [Serializable]
    public sealed class InstructionNetworkRemoveLocalListVariable : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();
        [SerializeField] private PropertyGetInteger m_Index = new PropertyGetInteger(0);

        public override string Title => $"Network Remove Local List [{m_Index}]";

        protected override Task Run(Args args)
        {
            if (!NetworkVariableInstructionUtility.TryGetActorNetworkId(m_Actor, args, nameof(InstructionNetworkRemoveLocalListVariable), out uint actorNetworkId) ||
                !NetworkVariableInstructionUtility.TryGetController(m_Target, args, nameof(InstructionNetworkRemoveLocalListVariable), out var controller))
            {
                return Task.CompletedTask;
            }

            controller.RequestRemoveLocalList(NetworkVariableInstructionUtility.GetIndex(m_Index, args), actorNetworkId);
            return Task.CompletedTask;
        }
    }

    [Version(0, 1, 0)]
    [Title("Network Set Global List Variable")]
    [Description("Requests a server-authoritative set operation on a profiled Global List Variable")]
    [Category("Network/Variables/Set Global List Variable")]
    [Parameter("Actor", "NetworkCharacter that owns the request")]
    [Parameter("Variables", "GC2 Global List Variables asset")]
    [Parameter("Index", "List index to set")]
    [Parameter("Value", "Supported network value to assign")]
    [Keywords("Network", "Variables", "Global", "List", "Set")]
    [Image(typeof(IconListVariable), ColorTheme.Type.Teal, typeof(OverlayDot))]
    [Serializable]
    public sealed class InstructionNetworkSetGlobalListVariable : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] private GlobalListVariables m_Variables;
        [SerializeField] private PropertyGetInteger m_Index = new PropertyGetInteger(0);
        [SerializeField] private NetworkVariableInstructionValue m_Value = new();

        public override string Title => $"Network Set Global List [{m_Index}]";

        protected override Task Run(Args args)
        {
            if (!NetworkVariableInstructionUtility.TryGetActorNetworkId(m_Actor, args, nameof(InstructionNetworkSetGlobalListVariable), out uint actorNetworkId) ||
                !NetworkVariableInstructionUtility.TryGetManager(nameof(InstructionNetworkSetGlobalListVariable), out var manager))
            {
                return Task.CompletedTask;
            }

            manager.RequestSetGlobalList(actorNetworkId, m_Variables, NetworkVariableInstructionUtility.GetIndex(m_Index, args), m_Value.Get(args));
            return Task.CompletedTask;
        }
    }

    [Version(0, 1, 0)]
    [Title("Network Push Global List Variable")]
    [Description("Requests a server-authoritative push operation on a profiled Global List Variable")]
    [Category("Network/Variables/Push Global List Variable")]
    [Parameter("Actor", "NetworkCharacter that owns the request")]
    [Parameter("Variables", "GC2 Global List Variables asset")]
    [Parameter("Value", "Supported network value to push")]
    [Keywords("Network", "Variables", "Global", "List", "Push")]
    [Image(typeof(IconListVariable), ColorTheme.Type.Teal, typeof(OverlayPlus))]
    [Serializable]
    public sealed class InstructionNetworkPushGlobalListVariable : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] private GlobalListVariables m_Variables;
        [SerializeField] private NetworkVariableInstructionValue m_Value = new();

        public override string Title => "Network Push Global List";

        protected override Task Run(Args args)
        {
            if (!NetworkVariableInstructionUtility.TryGetActorNetworkId(m_Actor, args, nameof(InstructionNetworkPushGlobalListVariable), out uint actorNetworkId) ||
                !NetworkVariableInstructionUtility.TryGetManager(nameof(InstructionNetworkPushGlobalListVariable), out var manager))
            {
                return Task.CompletedTask;
            }

            manager.RequestPushGlobalList(actorNetworkId, m_Variables, m_Value.Get(args));
            return Task.CompletedTask;
        }
    }

    [Version(0, 1, 0)]
    [Title("Network Remove Global List Variable")]
    [Description("Requests a server-authoritative remove operation on a profiled Global List Variable")]
    [Category("Network/Variables/Remove Global List Variable")]
    [Parameter("Actor", "NetworkCharacter that owns the request")]
    [Parameter("Variables", "GC2 Global List Variables asset")]
    [Parameter("Index", "List index to remove")]
    [Keywords("Network", "Variables", "Global", "List", "Remove")]
    [Image(typeof(IconListVariable), ColorTheme.Type.Teal, typeof(OverlayMinus))]
    [Serializable]
    public sealed class InstructionNetworkRemoveGlobalListVariable : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] private GlobalListVariables m_Variables;
        [SerializeField] private PropertyGetInteger m_Index = new PropertyGetInteger(0);

        public override string Title => $"Network Remove Global List [{m_Index}]";

        protected override Task Run(Args args)
        {
            if (!NetworkVariableInstructionUtility.TryGetActorNetworkId(m_Actor, args, nameof(InstructionNetworkRemoveGlobalListVariable), out uint actorNetworkId) ||
                !NetworkVariableInstructionUtility.TryGetManager(nameof(InstructionNetworkRemoveGlobalListVariable), out var manager))
            {
                return Task.CompletedTask;
            }

            manager.RequestRemoveGlobalList(actorNetworkId, m_Variables, NetworkVariableInstructionUtility.GetIndex(m_Index, args));
            return Task.CompletedTask;
        }
    }
}
