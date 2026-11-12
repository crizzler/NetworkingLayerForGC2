using System;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Serializable]
    [Title("Network Look at Combat Target")]
    [Category("Network/Network Look at Combat Target")]
    [Description("Faces this Character's confirmed combat target. Add Network Combat Target for validated selection and late-join replication.")]
    public sealed class UnitFacingNetworkTarget : UnitFacingNetworkPivot
    {
        public override string ToString() => "Network Look at Combat Target";

        protected override Vector3 GetDefaultDirection()
        {
            GameObject target = Character.Combat.Targets.Primary;
            return target != null
                ? DecideDirection(Vector3.ProjectOnPlane(target.transform.position - Transform.position, Vector3.up))
                : base.GetDefaultDirection();
        }
    }
}
