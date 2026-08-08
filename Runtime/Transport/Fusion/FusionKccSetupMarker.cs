using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// KCC-independent ownership and rollback marker for the optional nested motor. Keeping this
    /// in the normal Fusion assembly lets cleanup distinguish wizard-owned content from an
    /// adopted customer KCC hierarchy even after the optional addon has been removed.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class FusionKccSetupMarker : MonoBehaviour
    {
        [SerializeField] private bool m_MotorObjectCreatedByWizard;
        [SerializeField] private bool m_AdoptedSetupParked;

        [SerializeField] private bool m_HasRootControllerSnapshot;
        [SerializeField] private bool m_HadRootCharacterController;
        [SerializeField] private bool m_OriginalRootCharacterControllerEnabled;

        // The marker deliberately stores only Unity/core-Fusion data. The optional editor adapter
        // interprets the KCC-specific integer values while the addon is installed. This keeps the
        // normal Fusion assembly independent from Photon KCC and still makes adopting an existing
        // customer motor reversible.
        [SerializeField] private bool m_HasCustomerSnapshot;
        [SerializeField] private string m_OriginalObjectName;
        [SerializeField] private bool m_OriginalObjectActiveSelf;
        [SerializeField] private Vector3 m_OriginalLocalPosition;
        [SerializeField] private Quaternion m_OriginalLocalRotation = Quaternion.identity;
        [SerializeField] private Vector3 m_OriginalLocalScale = Vector3.one;

        [SerializeField] private bool m_HadNetworkObject;
        [SerializeField] private int m_OriginalNetworkObjectFlags;
        [SerializeField] private bool m_OriginalNetworkObjectInterpolation;

        [SerializeField] private bool m_HadRigidbody;
        [SerializeField] private bool m_OriginalRigidbodyIsKinematic;
        [SerializeField] private bool m_OriginalRigidbodyUseGravity;
        [SerializeField] private int m_OriginalRigidbodyInterpolation;
        [SerializeField] private int m_OriginalRigidbodyCollisionDetection;
        [SerializeField] private int m_OriginalRigidbodyConstraints;

        [SerializeField] private bool m_HadKcc;
        [SerializeField] private bool m_OriginalKccEnabled;
        [SerializeField] private int m_OriginalKccShape;
        [SerializeField] private bool m_OriginalKccIsTrigger;
        [SerializeField] private float m_OriginalKccHeight;
        [SerializeField] private float m_OriginalKccRadius;
        [SerializeField] private float m_OriginalKccExtent;
        [SerializeField] private int m_OriginalKccColliderLayer;
        [SerializeField] private int m_OriginalKccCollisionLayerMask;
        [SerializeField] private int m_OriginalKccInputAuthorityBehavior;
        [SerializeField] private int m_OriginalKccStateAuthorityBehavior;
        [SerializeField] private int m_OriginalKccProxyInterpolationMode;
        [SerializeField] private bool m_OriginalKccForcePredictedLookRotation;
        [SerializeField] private bool m_OriginalKccAllowClientTeleports;
        [SerializeField] private Object[] m_OriginalKccProcessors;

        [SerializeField] private bool m_HadEnvironmentProcessor;
        [SerializeField] private bool m_OriginalEnvironmentProcessorEnabled;
        [SerializeField] private bool m_HadGroundSnapProcessor;
        [SerializeField] private bool m_OriginalGroundSnapProcessorEnabled;
        [SerializeField] private bool m_HadStepUpProcessor;
        [SerializeField] private bool m_OriginalStepUpProcessorEnabled;
        [SerializeField] private bool m_HadGc2Processor;
        [SerializeField] private bool m_OriginalGc2ProcessorEnabled;

        // Processor components cannot share the KCC motor because Photon's KCCProcessor has
        // DisallowMultipleComponent and RequireComponent(NetworkObject). The optional editor
        // integration therefore creates one child object per required processor and records
        // only those wizard-owned objects here. Keeping the ownership list KCC-independent lets
        // the normal Fusion wizard recover safely after the addon has been removed.
        [SerializeField] private GameObject[] m_WizardOwnedProcessorObjects;

        public bool MotorObjectCreatedByWizard => m_MotorObjectCreatedByWizard;
        public bool HasCustomerSnapshot => m_HasCustomerSnapshot;
        public bool AdoptedSetupParked => m_AdoptedSetupParked;
        public bool HasRootControllerSnapshot => m_HasRootControllerSnapshot;
        public bool HadRootCharacterController => m_HadRootCharacterController;
        public bool OriginalRootCharacterControllerEnabled =>
            m_OriginalRootCharacterControllerEnabled;
        public GameObject[] WizardOwnedProcessorObjects =>
            m_WizardOwnedProcessorObjects;
        public Object[] OriginalKccProcessorObjects =>
            m_OriginalKccProcessors;
    }
}
