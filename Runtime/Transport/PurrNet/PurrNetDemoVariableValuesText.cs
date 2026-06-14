using System;
using System.Globalization;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("PurrNet Demo Variable Values")]
    [Category("Network/PurrNet/Demo Variable Values")]
    [Description("Formats the Core + Variables demo Local Name Variables as display text.")]
    [Serializable]
    public sealed class PurrNetDemoVariableValuesText : PropertyTypeGetString
    {
        [SerializeField] private string m_Title = "Variables";
        [SerializeField] private PropertyGetBool m_Checked = new PropertyGetBool(false);
        [SerializeField] private PropertyGetDecimal m_Counter = new PropertyGetDecimal(0d);

        public override string Get(Args args)
        {
            bool isChecked = m_Checked.Get(args);
            double counter = m_Counter.Get(args);

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}\nChecked: {1}\nCounter: {2:0.###}",
                m_Title,
                isChecked,
                counter);
        }

        public override string String => m_Title;
    }
}
