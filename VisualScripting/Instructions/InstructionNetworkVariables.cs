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

    [Version(1, 0, 0)]
    [Title("Network Insert Local List Variable")]
    [Description("Requests a server-authoritative insert operation on a profiled Local List Variable")]
    [Category("Network/Variables/Insert Local List Variable")]
    [Parameter("Actor", "NetworkCharacter that owns the request")]
    [Parameter("Target", "GameObject with a NetworkVariableController and LocalListVariables component")]
    [Parameter("Index", "List index at which the value is inserted")]
    [Parameter("Value", "Supported network value to insert")]
    [Keywords("Network", "Variables", "Local", "List", "Insert")]
    [Image(typeof(IconListVariable), ColorTheme.Type.Teal, typeof(OverlayPlus))]
    [Serializable]
    public sealed class InstructionNetworkInsertLocalListVariable : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();
        [SerializeField] private PropertyGetInteger m_Index = new PropertyGetInteger(0);
        [SerializeField] private NetworkVariableInstructionValue m_Value = new();

        public override string Title => $"Network Insert Local List [{m_Index}]";

        protected override Task Run(Args args)
        {
            if (!NetworkVariableInstructionUtility.TryGetActorNetworkId(m_Actor, args, nameof(InstructionNetworkInsertLocalListVariable), out uint actorNetworkId) ||
                !NetworkVariableInstructionUtility.TryGetController(m_Target, args, nameof(InstructionNetworkInsertLocalListVariable), out var controller))
            {
                return Task.CompletedTask;
            }

            controller.RequestInsertLocalList(
                NetworkVariableInstructionUtility.GetIndex(m_Index, args),
                m_Value.Get(args),
                actorNetworkId);
            return Task.CompletedTask;
        }
    }

    [Version(1, 0, 0)]
    [Title("Network Clear Local List Variable")]
    [Description("Requests a server-authoritative clear operation on a profiled Local List Variable")]
    [Category("Network/Variables/Clear Local List Variable")]
    [Parameter("Actor", "NetworkCharacter that owns the request")]
    [Parameter("Target", "GameObject with a NetworkVariableController and LocalListVariables component")]
    [Keywords("Network", "Variables", "Local", "List", "Clear")]
    [Image(typeof(IconListVariable), ColorTheme.Type.Teal, typeof(OverlayCross))]
    [Serializable]
    public sealed class InstructionNetworkClearLocalListVariable : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();

        public override string Title => "Network Clear Local List";

        protected override Task Run(Args args)
        {
            if (!NetworkVariableInstructionUtility.TryGetActorNetworkId(m_Actor, args, nameof(InstructionNetworkClearLocalListVariable), out uint actorNetworkId) ||
                !NetworkVariableInstructionUtility.TryGetController(m_Target, args, nameof(InstructionNetworkClearLocalListVariable), out var controller))
            {
                return Task.CompletedTask;
            }

            controller.RequestClearLocalList(actorNetworkId);
            return Task.CompletedTask;
        }
    }

    [Version(1, 0, 0)]
    [Title("Network Move Local List Variable")]
    [Description("Requests a server-authoritative move operation on a profiled Local List Variable")]
    [Category("Network/Variables/Move Local List Variable")]
    [Parameter("Actor", "NetworkCharacter that owns the request")]
    [Parameter("Target", "GameObject with a NetworkVariableController and LocalListVariables component")]
    [Parameter("From", "Source list index")]
    [Parameter("To", "Destination list index")]
    [Keywords("Network", "Variables", "Local", "List", "Move", "Reorder")]
    [Image(typeof(IconListVariable), ColorTheme.Type.Teal)]
    [Serializable]
    public sealed class InstructionNetworkMoveLocalListVariable : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();
        [SerializeField] private PropertyGetInteger m_From = new PropertyGetInteger(0);
        [SerializeField] private PropertyGetInteger m_To = new PropertyGetInteger(0);

        public override string Title => $"Network Move Local List [{m_From}] to [{m_To}]";

        protected override Task Run(Args args)
        {
            if (!NetworkVariableInstructionUtility.TryGetActorNetworkId(m_Actor, args, nameof(InstructionNetworkMoveLocalListVariable), out uint actorNetworkId) ||
                !NetworkVariableInstructionUtility.TryGetController(m_Target, args, nameof(InstructionNetworkMoveLocalListVariable), out var controller))
            {
                return Task.CompletedTask;
            }

            controller.RequestMoveLocalList(
                NetworkVariableInstructionUtility.GetIndex(m_From, args),
                NetworkVariableInstructionUtility.GetIndex(m_To, args),
                actorNetworkId);
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

    [Version(1, 0, 0)]
    [Title("Network Insert Global List Variable")]
    [Description("Requests a server-authoritative insert operation on a profiled Global List Variable")]
    [Category("Network/Variables/Insert Global List Variable")]
    [Parameter("Actor", "NetworkCharacter that owns the request")]
    [Parameter("Variables", "GC2 Global List Variables asset")]
    [Parameter("Index", "List index at which the value is inserted")]
    [Parameter("Value", "Supported network value to insert")]
    [Keywords("Network", "Variables", "Global", "List", "Insert")]
    [Image(typeof(IconListVariable), ColorTheme.Type.Teal, typeof(OverlayPlus))]
    [Serializable]
    public sealed class InstructionNetworkInsertGlobalListVariable : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] private GlobalListVariables m_Variables;
        [SerializeField] private PropertyGetInteger m_Index = new PropertyGetInteger(0);
        [SerializeField] private NetworkVariableInstructionValue m_Value = new();

        public override string Title => $"Network Insert Global List [{m_Index}]";

        protected override Task Run(Args args)
        {
            if (!NetworkVariableInstructionUtility.TryGetActorNetworkId(m_Actor, args, nameof(InstructionNetworkInsertGlobalListVariable), out uint actorNetworkId) ||
                !NetworkVariableInstructionUtility.TryGetManager(nameof(InstructionNetworkInsertGlobalListVariable), out var manager))
            {
                return Task.CompletedTask;
            }

            manager.RequestInsertGlobalList(
                actorNetworkId,
                m_Variables,
                NetworkVariableInstructionUtility.GetIndex(m_Index, args),
                m_Value.Get(args));
            return Task.CompletedTask;
        }
    }

    [Version(1, 0, 0)]
    [Title("Network Clear Global List Variable")]
    [Description("Requests a server-authoritative clear operation on a profiled Global List Variable")]
    [Category("Network/Variables/Clear Global List Variable")]
    [Parameter("Actor", "NetworkCharacter that owns the request")]
    [Parameter("Variables", "GC2 Global List Variables asset")]
    [Keywords("Network", "Variables", "Global", "List", "Clear")]
    [Image(typeof(IconListVariable), ColorTheme.Type.Teal, typeof(OverlayCross))]
    [Serializable]
    public sealed class InstructionNetworkClearGlobalListVariable : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] private GlobalListVariables m_Variables;

        public override string Title => "Network Clear Global List";

        protected override Task Run(Args args)
        {
            if (!NetworkVariableInstructionUtility.TryGetActorNetworkId(m_Actor, args, nameof(InstructionNetworkClearGlobalListVariable), out uint actorNetworkId) ||
                !NetworkVariableInstructionUtility.TryGetManager(nameof(InstructionNetworkClearGlobalListVariable), out var manager))
            {
                return Task.CompletedTask;
            }

            manager.RequestClearGlobalList(actorNetworkId, m_Variables);
            return Task.CompletedTask;
        }
    }

    [Version(1, 0, 0)]
    [Title("Network Move Global List Variable")]
    [Description("Requests a server-authoritative move operation on a profiled Global List Variable")]
    [Category("Network/Variables/Move Global List Variable")]
    [Parameter("Actor", "NetworkCharacter that owns the request")]
    [Parameter("Variables", "GC2 Global List Variables asset")]
    [Parameter("From", "Source list index")]
    [Parameter("To", "Destination list index")]
    [Keywords("Network", "Variables", "Global", "List", "Move", "Reorder")]
    [Image(typeof(IconListVariable), ColorTheme.Type.Teal)]
    [Serializable]
    public sealed class InstructionNetworkMoveGlobalListVariable : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] private GlobalListVariables m_Variables;
        [SerializeField] private PropertyGetInteger m_From = new PropertyGetInteger(0);
        [SerializeField] private PropertyGetInteger m_To = new PropertyGetInteger(0);

        public override string Title => $"Network Move Global List [{m_From}] to [{m_To}]";

        protected override Task Run(Args args)
        {
            if (!NetworkVariableInstructionUtility.TryGetActorNetworkId(m_Actor, args, nameof(InstructionNetworkMoveGlobalListVariable), out uint actorNetworkId) ||
                !NetworkVariableInstructionUtility.TryGetManager(nameof(InstructionNetworkMoveGlobalListVariable), out var manager))
            {
                return Task.CompletedTask;
            }

            manager.RequestMoveGlobalList(
                actorNetworkId,
                m_Variables,
                NetworkVariableInstructionUtility.GetIndex(m_From, args),
                NetworkVariableInstructionUtility.GetIndex(m_To, args));
            return Task.CompletedTask;
        }
    }
}
