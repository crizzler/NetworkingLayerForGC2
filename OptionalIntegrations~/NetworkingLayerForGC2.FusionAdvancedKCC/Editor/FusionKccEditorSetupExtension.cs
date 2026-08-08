#if ARAWN_GC2_FUSION_KCC
using System;
using System.Collections.Generic;
using System.Linq;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using Arawn.GameCreator2.Networking.Transport.Fusion.Editor;
using Fusion;
using Fusion.Addons.KCC;
using GameCreator.Runtime.Characters;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.KCC.Editor
{
    /// <summary>
    /// Strongly typed editor half of the optional Advanced KCC integration. This file compiles
    /// into Assembly-CSharp-Editor because Photon ships KCC in Assembly-CSharp. The normal
    /// Fusion wizard discovers it through <see cref="IFusionKccEditorSetupExtension"/>.
    /// </summary>
    public sealed class FusionKccEditorSetupExtension : IFusionKccEditorSetupExtension
    {
        private const string MotorName = "Fusion KCC Motor";
        private const string ProcessorObjectPrefix = "Fusion KCC Processor - ";
        private const string EnvironmentProcessorObjectName =
            ProcessorObjectPrefix + "Environment";
        private const string GroundSnapProcessorObjectName =
            ProcessorObjectPrefix + "Ground Snap";
        private const string StepUpProcessorObjectName =
            ProcessorObjectPrefix + "Step Up";
        private const string Gc2ProcessorObjectName =
            ProcessorObjectPrefix + "GC2 Movement";
        private const float MinimumHeight = 0.1f;
        private const float MinimumRadius = 0.01f;

        public bool IsAvailable => true;
        public string UnavailableReason => string.Empty;

        public bool ConfigurePlayerPrefab(
            GameObject prefabRoot,
            FusionKccEditorSetupOptions options,
            IList<string> changes,
            out string error)
        {
            error = string.Empty;
            if (prefabRoot == null)
            {
                error = "Cannot configure Fusion Advanced KCC without a player prefab root.";
                return false;
            }

            try
            {
                Character character = prefabRoot.GetComponent<Character>();
                FusionKccCharacterBackend backend =
                    prefabRoot.GetComponent<FusionKccCharacterBackend>();
                NetworkObject rootNetworkObject = prefabRoot.GetComponent<NetworkObject>();
                if (character == null || backend == null || rootNetworkObject == null)
                {
                    error =
                        $"'{prefabRoot.name}' is missing the Character, NetworkObject, or " +
                        "FusionKccCharacterBackend prepared by the Fusion wizard.";
                    return false;
                }

                CharacterController controller =
                    prefabRoot.GetComponent<CharacterController>();
                bool hadRootController = controller != null;
                bool rootControllerWasEnabled = controller != null && controller.enabled;

                if (!TryFindOrCreateMotor(
                        prefabRoot.transform,
                        changes,
                        out Transform motorTransform,
                        out bool motorObjectCreated,
                        out error))
                {
                    return false;
                }
                if (!TryNormalizeDuplicateMotors(prefabRoot, motorTransform, changes, out error))
                {
                    return false;
                }

                FusionKccSetupMarker setupMarker =
                    motorTransform.GetComponent<FusionKccSetupMarker>();
                if (setupMarker == null)
                {
                    setupMarker = motorTransform.gameObject
                        .AddComponent<FusionKccSetupMarker>();
                    CaptureSetupMarker(
                        setupMarker,
                        motorTransform,
                        motorObjectCreated,
                        hadRootController,
                        rootControllerWasEnabled);
                    AddChange(
                        changes,
                        motorObjectCreated
                            ? "marked the wizard-owned KCC motor"
                            : "marked an adopted customer KCC motor");
                }
                EnsureRootControllerSnapshot(
                    setupMarker,
                    hadRootController,
                    rootControllerWasEnabled);

                if (!motorObjectCreated && !setupMarker.MotorObjectCreatedByWizard &&
                    !setupMarker.HasCustomerSnapshot)
                {
                    error =
                        $"The adopted KCC motor '{motorTransform.name}' has no reversible " +
                        "customer snapshot. Remove its stale FusionKccSetupMarker and rerun the " +
                        "wizard so the original setup can be captured safely.";
                    return false;
                }

                SetMarkerParked(setupMarker, false);

                if (!motorTransform.gameObject.activeSelf)
                {
                    motorTransform.gameObject.SetActive(true);
                    AddChange(changes, "enabled the nested Fusion KCC Motor object");
                }

                if (!string.Equals(
                        motorTransform.name,
                        MotorName,
                        StringComparison.Ordinal))
                {
                    motorTransform.name = MotorName;
                    AddChange(changes, "normalized the nested Fusion KCC Motor name");
                }

                RemoveCompetingRootTrsp(prefabRoot, changes);
                ConfigureRootNetworkObject(rootNetworkObject, changes);
                if (controller != null && controller.enabled)
                {
                    controller.enabled = false;
                    EditorUtility.SetDirty(controller);
                    AddChange(changes, "disabled the legacy CharacterController");
                }

                float height = character.Motion != null
                    ? Mathf.Max(MinimumHeight, character.Motion.Height)
                    : 2f;
                float radius = character.Motion != null
                    ? Mathf.Clamp(
                        character.Motion.Radius,
                        MinimumRadius,
                        height * 0.5f)
                    : 0.35f;
                ConfigureMotorTransform(motorTransform, height, changes);

                GameObject motorObject = motorTransform.gameObject;
                NetworkObject motorNetworkObject = EnsureComponent<NetworkObject>(
                    motorObject,
                    changes,
                    "nested KCC NetworkObject");
                ConfigureMotorNetworkObject(
                    motorNetworkObject,
                    options.SharedAuthorityMode,
                    changes);

                Rigidbody rigidbody = EnsureComponent<Rigidbody>(
                    motorObject,
                    changes,
                    "kinematic KCC Rigidbody");
                ConfigureRigidbody(rigidbody, changes);

                global::Fusion.Addons.KCC.KCC kcc =
                    EnsureComponent<global::Fusion.Addons.KCC.KCC>(
                        motorObject,
                        changes,
                        "Advanced KCC");
                FusionKccMotorBody motorBody = EnsureComponent<FusionKccMotorBody>(
                    motorObject,
                    changes,
                    "Fusion KCC motor body");

                if (!ValidateOwnedProcessorObjects(
                        motorTransform,
                        setupMarker,
                        out error))
                {
                    return false;
                }

                EnvironmentProcessor environment = ResolveOrCreateProcessor<EnvironmentProcessor>(
                    prefabRoot.transform,
                    motorTransform,
                    setupMarker,
                    kcc,
                    EnvironmentProcessorObjectName,
                    changes);
                GroundSnapProcessor groundSnap = ResolveOrCreateProcessor<GroundSnapProcessor>(
                    prefabRoot.transform,
                    motorTransform,
                    setupMarker,
                    kcc,
                    GroundSnapProcessorObjectName,
                    changes);
                StepUpProcessor stepUp = ResolveOrCreateProcessor<StepUpProcessor>(
                    prefabRoot.transform,
                    motorTransform,
                    setupMarker,
                    kcc,
                    StepUpProcessorObjectName,
                    changes);
                FusionGc2KccProcessor gc2Processor =
                    ResolveOrCreateProcessor<FusionGc2KccProcessor>(
                        prefabRoot.transform,
                        motorTransform,
                        setupMarker,
                        kcc,
                        Gc2ProcessorObjectName,
                        changes);
                NormalizeOwnedProcessorObjects(
                    motorTransform,
                    setupMarker,
                    new KCCProcessor[]
                    {
                        environment,
                        groundSnap,
                        stepUp,
                        gc2Processor
                    },
                    changes);

                EnsureEnabled(backend, changes, "Fusion KCC backend");
                EnsureEnabled(kcc, changes, "Advanced KCC");
                EnsureEnabled(environment, changes, "KCC Environment processor");
                EnsureEnabled(groundSnap, changes, "KCC Ground Snap processor");
                EnsureEnabled(stepUp, changes, "KCC Step Up processor");
                EnsureEnabled(gc2Processor, changes, "GC2 KCC processor");
                EnsureEnabled(motorBody, changes, "Fusion KCC motor body");

                ConfigureKcc(
                    kcc,
                    character,
                    height,
                    radius,
                    environment,
                    groundSnap,
                    stepUp,
                    gc2Processor,
                    changes);
                ConfigureGc2Processor(gc2Processor, environment, changes);
                ConfigureMotorBody(
                    motorBody,
                    backend,
                    kcc,
                    gc2Processor,
                    environment,
                    changes);
                ConfigureBackend(
                    backend,
                    motorBody,
                    options.SharedAuthorityMode,
                    changes);

                EditorUtility.SetDirty(prefabRoot);
                return true;
            }
            catch (Exception exception)
            {
                error =
                    $"Fusion Advanced KCC setup failed for '{prefabRoot.name}': " +
                    $"{exception.Message}\n{exception.StackTrace}";
                return false;
            }
        }

        public bool RemoveFromPlayerPrefab(
            GameObject prefabRoot,
            IList<string> changes,
            out string error)
        {
            error = string.Empty;
            if (prefabRoot == null) return true;

            try
            {
                if (!TryReadRootControllerSnapshot(
                        prefabRoot,
                        out bool hasRootControllerSnapshot,
                        out bool hadRootController,
                        out bool rootControllerWasEnabled,
                        out error))
                {
                    return false;
                }

                CharacterController controller =
                    prefabRoot.GetComponent<CharacterController>();
                if (hasRootControllerSnapshot && hadRootController && controller == null)
                {
                    error =
                        $"'{prefabRoot.name}' originally had a CharacterController, but that " +
                        "component is now missing. The wizard stopped before changing the KCC " +
                        "hierarchy.";
                    return false;
                }

                // Collect and validate every affected object before changing any of them. The
                // outer wizard also snapshots prefab bytes, but this preflight keeps direct
                // extension callers from receiving a half-restored customer hierarchy when a
                // later motor is incomplete.
                var candidates = new List<GameObject>();
                var seenCandidates = new HashSet<GameObject>();
                foreach (FusionKccMotorBody body in
                         prefabRoot.GetComponentsInChildren<FusionKccMotorBody>(true))
                {
                    AddUniqueCandidate(body != null ? body.gameObject : null);
                }

                foreach (FusionKccSetupMarker marker in
                         prefabRoot.GetComponentsInChildren<FusionKccSetupMarker>(true))
                {
                    if (marker == null) continue;
                    if (marker.MotorObjectCreatedByWizard || !marker.AdoptedSetupParked)
                    {
                        AddUniqueCandidate(marker.gameObject);
                    }
                }

                Transform namedMotor = prefabRoot.transform.Find(MotorName);
                if (namedMotor != null &&
                    (namedMotor.GetComponent<global::Fusion.Addons.KCC.KCC>() != null ||
                     namedMotor.GetComponent<FusionGc2KccProcessor>() != null ||
                     namedMotor.GetComponent<FusionKccMotorBody>() != null ||
                     namedMotor.GetComponent<FusionKccSetupMarker>() != null))
                {
                    AddUniqueCandidate(namedMotor.gameObject);
                }

                var markers = new Dictionary<GameObject, FusionKccSetupMarker>();
                foreach (GameObject candidate in candidates)
                {
                    if (candidate.transform.parent != prefabRoot.transform)
                    {
                        error =
                            $"'{prefabRoot.name}' contains a Fusion KCC setup marker or motor " +
                            $"on '{candidate.name}' outside the required direct-child hierarchy. " +
                            "The wizard stopped before changing any KCC object.";
                        return false;
                    }

                    FusionKccSetupMarker marker =
                        candidate.GetComponent<FusionKccSetupMarker>();
                    if (marker == null)
                    {
                        error =
                            $"'{prefabRoot.name}' contains a Fusion KCC motor without an " +
                            "ownership marker. The wizard cannot safely decide which customer " +
                            "content to preserve. Reapply the KCC setup before converting it.";
                        return false;
                    }

                    if (!marker.MotorObjectCreatedByWizard &&
                        !ValidateAdoptedMotorForRestore(candidate, marker, out error))
                    {
                        return false;
                    }
                    markers.Add(candidate, marker);
                }

                var destroyedObjects = new HashSet<GameObject>();
                foreach (GameObject candidate in candidates)
                {
                    FusionKccSetupMarker marker = markers[candidate];
                    if (marker.MotorObjectCreatedByWizard)
                    {
                        destroyedObjects.Add(candidate);
                    }
                    else if (!RestoreAdoptedMotor(candidate, marker, changes, out error))
                    {
                        return false;
                    }
                }

                foreach (GameObject motorObject in destroyedObjects)
                {
                    if (motorObject == null) continue;
                    Object.DestroyImmediate(motorObject, true);
                    AddChange(changes, "removed the nested Fusion KCC Motor");
                }

                if (controller != null &&
                    (hasRootControllerSnapshot ? hadRootController : true))
                {
                    bool desiredEnabled = hasRootControllerSnapshot
                        ? rootControllerWasEnabled
                        : true;
                    if (controller.enabled != desiredEnabled)
                    {
                        controller.enabled = desiredEnabled;
                        EditorUtility.SetDirty(controller);
                        AddChange(
                            changes,
                            desiredEnabled
                                ? "restored the enabled legacy CharacterController"
                                : "restored the disabled legacy CharacterController");
                    }
                }

                return true;

                void AddUniqueCandidate(GameObject candidate)
                {
                    if (candidate != null && seenCandidates.Add(candidate))
                    {
                        candidates.Add(candidate);
                    }
                }
            }
            catch (Exception exception)
            {
                error =
                    $"Could not remove Fusion Advanced KCC from '{prefabRoot.name}': " +
                    $"{exception.Message}";
                return false;
            }
        }

        public void ValidatePlayerPrefab(
            GameObject prefabRoot,
            FusionKccEditorSetupOptions options,
            IList<FusionKccEditorValidationIssue> issues)
        {
            if (prefabRoot == null || issues == null) return;

            FusionKccCharacterBackend backend =
                prefabRoot.GetComponent<FusionKccCharacterBackend>();
            FusionKccMotorBody[] bodies =
                prefabRoot.GetComponentsInChildren<FusionKccMotorBody>(true);
            FusionKccMotorBody body = bodies.Length == 1 ? bodies[0] : null;
            FusionKccEditorIssueSeverity incomplete = options.RequireAppliedSetup
                ? FusionKccEditorIssueSeverity.Error
                : FusionKccEditorIssueSeverity.Warning;

            if (bodies.Length != 1)
            {
                AddIssue(
                    issues,
                    incomplete,
                    $"Fusion Advanced KCC requires exactly one nested " +
                    $"{nameof(FusionKccMotorBody)}; found {bodies.Length}. The wizard will " +
                    "create or normalize it.",
                    prefabRoot);
                return;
            }

            Transform motor = body.transform;
            if (motor.parent != prefabRoot.transform ||
                !string.Equals(motor.name, MotorName, StringComparison.Ordinal))
            {
                AddIssue(
                    issues,
                    FusionKccEditorIssueSeverity.Error,
                    $"The Fusion KCC motor must be a direct child named '{MotorName}'.",
                    body);
            }

            FusionKccSetupMarker marker =
                motor.GetComponent<FusionKccSetupMarker>();
            if (marker == null)
            {
                AddIssue(
                    issues,
                    incomplete,
                    "The nested Fusion KCC motor has no setup ownership marker. The wizard " +
                    "cannot safely recover it after addon removal.",
                    motor.gameObject);
            }
            else if (!marker.MotorObjectCreatedByWizard &&
                     !marker.HasCustomerSnapshot)
            {
                AddIssue(
                    issues,
                    incomplete,
                    "The adopted customer KCC motor has no reversible setup snapshot.",
                    marker);
            }
            else if (marker.AdoptedSetupParked)
            {
                AddIssue(
                    issues,
                    incomplete,
                    "The adopted customer KCC motor is parked for another movement backend. " +
                    "Rerun the wizard with Fusion Advanced KCC selected.",
                    marker);
            }

            if (!motor.gameObject.activeSelf)
            {
                AddIssue(
                    issues,
                    incomplete,
                    "The nested Fusion KCC Motor GameObject is disabled.",
                    motor.gameObject);
            }

            if (backend == null || !backend.enabled ||
                backend.RuntimeAdapterComponent != body)
            {
                AddIssue(
                    issues,
                    incomplete,
                    "FusionKccCharacterBackend is missing, disabled, or not linked to the " +
                    "nested KCC motor body.",
                    prefabRoot);
            }

            if (!body.enabled)
            {
                AddIssue(
                    issues,
                    incomplete,
                    "The nested Fusion KCC motor body is disabled.",
                    body);
            }

            if (backend != null &&
                backend.SharedAuthorityMode != options.SharedAuthorityMode)
            {
                AddIssue(
                    issues,
                    FusionKccEditorIssueSeverity.Warning,
                    $"The KCC backend uses {backend.SharedAuthorityMode}, but the selected " +
                    $"Shared policy is {options.SharedAuthorityMode}.",
                    backend);
            }

            CharacterController controller =
                prefabRoot.GetComponent<CharacterController>();
            if (controller != null && controller.enabled)
            {
                AddIssue(
                    issues,
                    incomplete,
                    "The legacy CharacterController is still enabled and can fight KCC.",
                    controller);
            }

            if (prefabRoot.GetComponent<FusionNativeNetworkCharacterMotor>() != null)
            {
                AddIssue(
                    issues,
                    FusionKccEditorIssueSeverity.Error,
                    "Fusion Native and Advanced KCC movement writers are both present.",
                    prefabRoot);
            }

            NetworkObject rootNetworkObject = prefabRoot.GetComponent<NetworkObject>();
            if (rootNetworkObject != null &&
                rootNetworkObject.Flags.GetInterestMode() != NetworkObjectInterestModes.Global)
            {
                AddIssue(
                    issues,
                    FusionKccEditorIssueSeverity.Warning,
                    "The GC2 player root is not configured for global Fusion interest.",
                    rootNetworkObject);
            }

            NetworkObject motorNetworkObject = motor.GetComponent<NetworkObject>();
            if (motorNetworkObject == null)
            {
                AddIssue(
                    issues,
                    incomplete,
                    "The nested Fusion KCC motor has no NetworkObject.",
                    motor.gameObject);
            }
            else
            {
                ValidateMotorAuthority(motorNetworkObject, options, issues);
                if (motorNetworkObject.Flags.GetInterestMode() !=
                    NetworkObjectInterestModes.Global)
                {
                    AddIssue(
                        issues,
                        incomplete,
                        "The nested KCC NetworkObject is not configured for global Fusion interest.",
                        motorNetworkObject);
                }
                if (!motorNetworkObject.EnableInterpolation)
                {
                    AddIssue(
                        issues,
                        incomplete,
                        "The nested KCC NetworkObject has interpolation disabled.",
                        motorNetworkObject);
                }
                if ((motorNetworkObject.Flags & NetworkObjectFlags.HasMainNetworkTRSP) == 0)
                {
                    AddIssue(
                        issues,
                        incomplete,
                        "The nested KCC has not been baked as its NetworkObject's main TRSP. " +
                        "Rebuild the Fusion prefab table.",
                        motorNetworkObject);
                }
            }

            Rigidbody rigidbody = motor.GetComponent<Rigidbody>();
            if (rigidbody == null || !rigidbody.isKinematic || rigidbody.useGravity ||
                rigidbody.interpolation != RigidbodyInterpolation.None ||
                rigidbody.collisionDetectionMode != CollisionDetectionMode.Discrete ||
                rigidbody.constraints != RigidbodyConstraints.FreezeAll)
            {
                AddIssue(
                    issues,
                    incomplete,
                    "The nested KCC Rigidbody must use the official kinematic defaults: gravity " +
                    "off, interpolation off, discrete collision detection, and frozen constraints.",
                    motor.gameObject);
            }

            global::Fusion.Addons.KCC.KCC kcc =
                motor.GetComponent<global::Fusion.Addons.KCC.KCC>();
            EnvironmentProcessor environment = ReadSerializedObjectReference<EnvironmentProcessor>(
                body,
                "m_EnvironmentProcessor");
            FusionGc2KccProcessor gc2Processor =
                ReadSerializedObjectReference<FusionGc2KccProcessor>(
                    body,
                    "m_Gc2Processor");
            GroundSnapProcessor groundSnap = FindConfiguredProcessor<GroundSnapProcessor>(kcc);
            StepUpProcessor stepUp = FindConfiguredProcessor<StepUpProcessor>(kcc);
            if (kcc == null || environment == null || groundSnap == null ||
                stepUp == null || gc2Processor == null)
            {
                AddIssue(
                    issues,
                    incomplete,
                    "The nested motor is missing KCC or one of its required processors.",
                    motor.gameObject);
                return;
            }

            KCCProcessor[] requiredProcessors =
            {
                environment,
                groundSnap,
                stepUp,
                gc2Processor
            };
            foreach (KCCProcessor requiredProcessor in requiredProcessors)
            {
                if (requiredProcessor.GetComponent<NetworkObject>() != null &&
                    requiredProcessor.gameObject.activeSelf)
                {
                    continue;
                }
                AddIssue(
                    issues,
                    incomplete,
                    $"Required KCC processor {requiredProcessor.GetType().Name} must be on " +
                    "an active GameObject with Photon's required NetworkObject.",
                    requiredProcessor);
            }
            foreach (IGrouping<GameObject, KCCProcessor> group in
                     requiredProcessors.GroupBy(processor => processor.gameObject))
            {
                int processorCount = group.Key.GetComponents<KCCProcessor>().Length;
                if (group.Count() == 1 && processorCount == 1) continue;
                AddIssue(
                    issues,
                    incomplete,
                    $"'{group.Key.name}' contains {processorCount} Photon KCC processors. " +
                    "Photon requires exactly one KCCProcessor per GameObject.",
                    group.Key);
            }

            GameObject[] ownedProcessorObjects = marker?.WizardOwnedProcessorObjects ??
                                                 Array.Empty<GameObject>();
            var ownedSet = new HashSet<GameObject>(
                ownedProcessorObjects.Where(value => value != null));
            foreach (GameObject ownedObject in ownedSet)
            {
                if (ownedObject.transform.parent != motor ||
                    !ownedObject.name.StartsWith(
                        ProcessorObjectPrefix,
                        StringComparison.Ordinal))
                {
                    AddIssue(
                        issues,
                        incomplete,
                        "A wizard-owned KCC processor object is not a reserved-name direct " +
                        "child of the nested motor.",
                        ownedObject);
                    continue;
                }

                KCCProcessor[] localProcessors =
                    ownedObject.GetComponents<KCCProcessor>();
                NetworkObject processorNetworkObject =
                    ownedObject.GetComponent<NetworkObject>();
                if (!ownedObject.activeSelf || localProcessors.Length != 1 ||
                    !localProcessors[0].enabled || processorNetworkObject == null)
                {
                    AddIssue(
                        issues,
                        incomplete,
                        "Each wizard-owned KCC processor child must be active and contain " +
                        "exactly one enabled KCCProcessor plus its required NetworkObject.",
                        ownedObject);
                }
                else
                {
                    ValidateProcessorAuthority(
                        processorNetworkObject,
                        options,
                        issues);
                }
            }

            foreach (Transform child in motor.Cast<Transform>())
            {
                if (child == null ||
                    !child.name.StartsWith(ProcessorObjectPrefix, StringComparison.Ordinal) ||
                    ownedSet.Contains(child.gameObject))
                {
                    continue;
                }
                AddIssue(
                    issues,
                    incomplete,
                    $"Reserved KCC processor child '{child.name}' is not recorded by the " +
                    "motor ownership marker. The wizard will not delete unowned content.",
                    child.gameObject);
            }

            if (!kcc.enabled || !environment.enabled || !groundSnap.enabled ||
                !stepUp.enabled || !gc2Processor.enabled)
            {
                AddIssue(
                    issues,
                    incomplete,
                    "Advanced KCC and all required GC2/official KCC processors must be enabled.",
                    motor.gameObject);
            }

            ValidateSerializedObjectReference(
                body,
                "m_Backend",
                backend,
                "The nested KCC motor body is not linked to its root backend.",
                incomplete,
                issues);
            ValidateSerializedObjectReference(
                body,
                "m_Kcc",
                kcc,
                "The nested KCC motor body is not linked to its KCC component.",
                incomplete,
                issues);
            ValidateSerializedObjectReference(
                body,
                "m_Gc2Processor",
                gc2Processor,
                "The nested KCC motor body is not linked to the GC2 processor.",
                incomplete,
                issues);
            ValidateSerializedObjectReference(
                body,
                "m_EnvironmentProcessor",
                environment,
                "The nested KCC motor body is not linked to the Environment processor.",
                incomplete,
                issues);
            ValidateSerializedObjectReference(
                gc2Processor,
                "m_EnvironmentProcessor",
                environment,
                "The GC2 KCC processor is not linked to the Environment processor.",
                incomplete,
                issues);

            KCCSettings settings = kcc.Settings;
            Character character = prefabRoot.GetComponent<Character>();
            float height = character?.Motion != null
                ? Mathf.Max(MinimumHeight, character.Motion.Height)
                : settings.Height;
            float radius = character?.Motion != null
                ? Mathf.Clamp(character.Motion.Radius, MinimumRadius, height * 0.5f)
                : settings.Radius;
            float extent = Mathf.Clamp(radius * 0.1f, 0.01f, radius * 0.25f);

            if (settings.Shape != EKCCShape.Capsule || settings.IsTrigger ||
                settings.AllowClientTeleports ||
                settings.InputAuthorityBehavior !=
                EKCCAuthorityBehavior.PredictFixed_PredictRender ||
                settings.StateAuthorityBehavior !=
                EKCCAuthorityBehavior.PredictFixed_InterpolateRender ||
                settings.ProxyInterpolationMode != EKCCInterpolationMode.Full ||
                !settings.ForcePredictedLookRotation ||
                settings.ColliderLayer != prefabRoot.layer ||
                settings.CollisionLayerMask.value == 0 ||
                !Mathf.Approximately(settings.Extent, extent))
            {
                AddIssue(
                    issues,
                    incomplete,
                    "KCC authority, teleport, capsule, collision, or render settings do not " +
                    "match the GC2 integration requirements.",
                    kcc);
            }

            Object[] processors = settings.Processors ?? Array.Empty<Object>();
            foreach (Object required in new Object[]
                     {
                         environment,
                         groundSnap,
                         stepUp,
                         gc2Processor
                     })
            {
                var requiredProcessor = (KCCProcessor)required;
                int count = processors.Count(
                    processor => ResolvesToProcessor(processor, requiredProcessor));
                if (count == 1) continue;
                AddIssue(
                    issues,
                    incomplete,
                    $"KCC's processor list must contain {required.GetType().Name} exactly once; " +
                    $"found {count}.",
                    kcc);
            }

            if (!Mathf.Approximately(settings.Height, height) ||
                !Mathf.Approximately(settings.Radius, radius))
            {
                AddIssue(
                    issues,
                    FusionKccEditorIssueSeverity.Warning,
                    "KCC capsule dimensions do not match the active GC2 Character dimensions.",
                    kcc);
            }

            Vector3 expectedLocalPosition = GetMotorLocalFootOffset(
                motor.parent,
                height);
            if ((motor.localPosition - expectedLocalPosition).sqrMagnitude > 0.0001f)
            {
                AddIssue(
                    issues,
                    FusionKccEditorIssueSeverity.Warning,
                    "The nested KCC foot-space origin is not aligned to the GC2 capsule bottom.",
                    motor.gameObject);
            }

            if (Quaternion.Angle(motor.localRotation, Quaternion.identity) > 0.001f)
            {
                AddIssue(
                    issues,
                    FusionKccEditorIssueSeverity.Warning,
                    "The nested KCC motor rotation is not aligned with the GC2 root.",
                    motor.gameObject);
            }

            if ((motor.lossyScale - Vector3.one).sqrMagnitude > 0.0001f)
            {
                AddIssue(
                    issues,
                    FusionKccEditorIssueSeverity.Warning,
                    "The nested KCC motor is not unit scale in world space.",
                    motor.gameObject);
            }
        }

        private static void ConfigureRootNetworkObject(
            NetworkObject networkObject,
            IList<string> changes)
        {
            NetworkObjectFlags desired = networkObject.Flags.SetInterestMode(
                NetworkObjectInterestModes.Global);
            if (desired != networkObject.Flags)
            {
                networkObject.Flags = desired;
                EditorUtility.SetDirty(networkObject);
                AddChange(changes, "global player-root interest");
            }
        }

        private static void ConfigureMotorNetworkObject(
            NetworkObject networkObject,
            FusionKccSharedAuthorityMode authorityMode,
            IList<string> changes)
        {
            NetworkObjectFlags desired = networkObject.Flags;
            desired = desired.SetInterestMode(NetworkObjectInterestModes.Global);
            desired &= ~NetworkObjectFlags.DestroyWhenStateAuthorityLeaves;
            desired &= ~NetworkObjectFlags.AllowStateAuthorityOverride;
            desired |= NetworkObjectFlags.HasMainNetworkTRSP;
            if (authorityMode ==
                FusionKccSharedAuthorityMode.SharedMasterMovementAuthority)
            {
                desired |= NetworkObjectFlags.MasterClientObject;
            }
            else
            {
                desired &= ~NetworkObjectFlags.MasterClientObject;
            }

            bool changed = false;
            if (networkObject.Flags != desired)
            {
                networkObject.Flags = desired;
                changed = true;
            }
            if (!networkObject.EnableInterpolation)
            {
                networkObject.EnableInterpolation = true;
                changed = true;
            }
            if (!changed) return;

            EditorUtility.SetDirty(networkObject);
            AddChange(changes, "nested KCC authority and interpolation settings");
        }

        private static void ConfigureRigidbody(
            Rigidbody rigidbody,
            IList<string> changes)
        {
            bool changed = false;
            if (!rigidbody.isKinematic)
            {
                rigidbody.isKinematic = true;
                changed = true;
            }
            if (rigidbody.useGravity)
            {
                rigidbody.useGravity = false;
                changed = true;
            }
            if (rigidbody.interpolation != RigidbodyInterpolation.None)
            {
                rigidbody.interpolation = RigidbodyInterpolation.None;
                changed = true;
            }
            if (rigidbody.collisionDetectionMode != CollisionDetectionMode.Discrete)
            {
                rigidbody.collisionDetectionMode = CollisionDetectionMode.Discrete;
                changed = true;
            }
            if (rigidbody.constraints != RigidbodyConstraints.FreezeAll)
            {
                rigidbody.constraints = RigidbodyConstraints.FreezeAll;
                changed = true;
            }
            if (!changed) return;

            EditorUtility.SetDirty(rigidbody);
            AddChange(changes, "kinematic KCC Rigidbody settings");
        }

        private static T ResolveOrCreateProcessor<T>(
            Transform prefabRoot,
            Transform motor,
            FusionKccSetupMarker marker,
            global::Fusion.Addons.KCC.KCC kcc,
            string ownedObjectName,
            IList<string> changes)
            where T : KCCProcessor
        {
            GameObject[] ownedObjects = marker.WizardOwnedProcessorObjects ??
                                        Array.Empty<GameObject>();
            foreach (GameObject ownedObject in ownedObjects)
            {
                if (ownedObject == null || ownedObject.transform.parent != motor) continue;
                T ownedProcessor = ownedObject.GetComponent<T>();
                if (ownedProcessor == null) continue;
                ConfigureOwnedProcessorObject(
                    ownedObject,
                    ownedObjectName,
                    motor.gameObject.layer,
                    changes);
                return ownedProcessor;
            }

            T template = FindConfiguredProcessor<T>(kcc);
            bool requiresInstanceLocal =
                typeof(FusionGc2KccProcessor).IsAssignableFrom(typeof(T));
            if (CanReuseCustomerProcessor(
                    template,
                    prefabRoot,
                    requiresInstanceLocal))
            {
                return template;
            }

            var processorObject = new GameObject(ownedObjectName)
            {
                layer = motor.gameObject.layer
            };
            processorObject.transform.SetParent(motor, false);

            Type processorType = template != null &&
                                 typeof(T).IsAssignableFrom(template.GetType())
                ? template.GetType()
                : typeof(T);
            T processor = processorObject.AddComponent(processorType) as T;
            if (processor == null)
            {
                Object.DestroyImmediate(processorObject, true);
                throw new InvalidOperationException(
                    $"Could not create the required {typeof(T).Name} on its dedicated " +
                    "Fusion KCC processor object.");
            }

            if (template != null && template.GetType() == processor.GetType())
            {
                EditorUtility.CopySerialized(template, processor);
            }
            processor.enabled = true;
            RegisterOwnedProcessorObject(marker, processorObject);
            ConfigureOwnedProcessorObject(
                processorObject,
                ownedObjectName,
                motor.gameObject.layer,
                changes);
            AddChange(changes, $"created dedicated {typeof(T).Name} object");
            return processor;
        }

        private static T FindConfiguredProcessor<T>(
            global::Fusion.Addons.KCC.KCC kcc)
            where T : KCCProcessor
        {
            Object[] configured = kcc?.Settings?.Processors ?? Array.Empty<Object>();
            foreach (Object candidate in configured)
            {
                if (candidate == null) continue;
                if (candidate is T direct) return direct;
                if (KCCUtility.ResolveProcessor(candidate, out IKCCProcessor resolved) &&
                    resolved is T processor)
                {
                    return processor;
                }
            }
            return null;
        }

        private static bool CanReuseCustomerProcessor<T>(
            T processor,
            Transform prefabRoot,
            bool requiresInstanceLocal)
            where T : KCCProcessor
        {
            // FusionGc2KccProcessor contains per-character driver bindings and must never reuse
            // a customer/provider object. Owned GC2 children were handled before this method;
            // an unowned candidate is cloned so its serialized state remains untouched.
            if (requiresInstanceLocal) return false;

            if (processor == null || prefabRoot == null || !processor.enabled ||
                !processor.gameObject.activeSelf)
            {
                return false;
            }

            return processor.GetComponent<NetworkObject>() != null &&
                   processor.GetComponents<KCCProcessor>().Length == 1;
        }

        private static void ConfigureOwnedProcessorObject(
            GameObject processorObject,
            string desiredName,
            int layer,
            IList<string> changes)
        {
            bool objectChanged = false;
            if (!string.Equals(processorObject.name, desiredName, StringComparison.Ordinal))
            {
                processorObject.name = desiredName;
                objectChanged = true;
            }
            if (!processorObject.activeSelf)
            {
                processorObject.SetActive(true);
                objectChanged = true;
            }
            if (processorObject.layer != layer)
            {
                processorObject.layer = layer;
                objectChanged = true;
            }

            Transform transform = processorObject.transform;
            if (transform.localPosition != Vector3.zero)
            {
                transform.localPosition = Vector3.zero;
                objectChanged = true;
            }
            if (transform.localRotation != Quaternion.identity)
            {
                transform.localRotation = Quaternion.identity;
                objectChanged = true;
            }
            if (transform.localScale != Vector3.one)
            {
                transform.localScale = Vector3.one;
                objectChanged = true;
            }

            NetworkObject networkObject = EnsureComponent<NetworkObject>(
                processorObject,
                changes,
                $"{processorObject.name} NetworkObject");
            ConfigureProcessorNetworkObject(networkObject, changes);
            KCCProcessor processor = processorObject.GetComponent<KCCProcessor>();
            EnsureEnabled(processor, changes, processorObject.name);
            if (!objectChanged) return;

            EditorUtility.SetDirty(processorObject);
            EditorUtility.SetDirty(transform);
            AddChange(changes, $"normalized {processorObject.name}");
        }

        private static void ConfigureProcessorNetworkObject(
            NetworkObject networkObject,
            IList<string> changes)
        {
            NetworkObjectFlags desired = networkObject.Flags;
            desired = desired.SetInterestMode(NetworkObjectInterestModes.Global);
            desired &= ~NetworkObjectFlags.HasMainNetworkTRSP;
            desired &= ~NetworkObjectFlags.DestroyWhenStateAuthorityLeaves;
            desired &= ~NetworkObjectFlags.AllowStateAuthorityOverride;
            // Processor support objects carry no movement authority. Keep them on the Shared
            // master for both movement policies; only the nested motor is released/requested to
            // the logical owner in OwnerMovementAuthority mode.
            desired |= NetworkObjectFlags.MasterClientObject;

            if (networkObject.Flags == desired) return;
            networkObject.Flags = desired;
            EditorUtility.SetDirty(networkObject);
            AddChange(changes, $"{networkObject.gameObject.name} authority settings");
        }

        private static void RegisterOwnedProcessorObject(
            FusionKccSetupMarker marker,
            GameObject processorObject)
        {
            var values = (marker.WizardOwnedProcessorObjects ?? Array.Empty<GameObject>())
                .Where(value => value != null)
                .Distinct()
                .Cast<Object>()
                .ToList();
            if (!values.Contains(processorObject)) values.Add(processorObject);

            var serialized = new SerializedObject(marker);
            SetObjectArray(serialized, "m_WizardOwnedProcessorObjects", values);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(marker);
        }

        private static bool ValidateOwnedProcessorObjects(
            Transform motor,
            FusionKccSetupMarker marker,
            out string error)
        {
            error = string.Empty;
            foreach (GameObject processorObject in
                     marker.WizardOwnedProcessorObjects ?? Array.Empty<GameObject>())
            {
                if (processorObject == null) continue;
                if (processorObject.transform.parent != motor ||
                    !processorObject.name.StartsWith(
                        ProcessorObjectPrefix,
                        StringComparison.Ordinal))
                {
                    error =
                        $"The KCC setup marker on '{motor.name}' contains an unsafe owned " +
                        $"processor reference to '{processorObject.name}'. Owned processors " +
                        "must be reserved-name direct children of the KCC motor. The wizard " +
                        "stopped before changing the hierarchy.";
                    return false;
                }

                if (processorObject.GetComponents<KCCProcessor>().Length > 1)
                {
                    error =
                        $"'{processorObject.name}' contains multiple Photon KCC processors. " +
                        "Photon permits only one KCCProcessor per GameObject.";
                    return false;
                }
            }
            return true;
        }

        private static void NormalizeOwnedProcessorObjects(
            Transform motor,
            FusionKccSetupMarker marker,
            IEnumerable<KCCProcessor> retainedProcessors,
            IList<string> changes)
        {
            var retained = new HashSet<GameObject>(
                retainedProcessors
                    .Where(processor => processor != null &&
                                        processor.transform.parent == motor)
                    .Select(processor => processor.gameObject));
            var normalized = new List<Object>();
            foreach (GameObject ownedObject in
                     marker.WizardOwnedProcessorObjects ?? Array.Empty<GameObject>())
            {
                if (ownedObject == null) continue;
                if (retained.Contains(ownedObject))
                {
                    if (!normalized.Contains(ownedObject)) normalized.Add(ownedObject);
                    continue;
                }

                Object.DestroyImmediate(ownedObject, true);
                AddChange(changes, "removed an unused wizard-owned KCC processor object");
            }

            var serialized = new SerializedObject(marker);
            SetObjectArray(serialized, "m_WizardOwnedProcessorObjects", normalized);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(marker);
        }

        private static void ConfigureKcc(
            global::Fusion.Addons.KCC.KCC kcc,
            Character character,
            float height,
            float radius,
            EnvironmentProcessor environment,
            GroundSnapProcessor groundSnap,
            StepUpProcessor stepUp,
            FusionGc2KccProcessor gc2Processor,
            IList<string> changes)
        {
            KCCSettings settings = kcc.Settings;
            bool changed = false;
            changed |= Set(ref settings.Shape, EKCCShape.Capsule);
            changed |= Set(ref settings.IsTrigger, false);
            changed |= Set(ref settings.Height, height);
            changed |= Set(ref settings.Radius, radius);
            changed |= Set(ref settings.Extent, Mathf.Clamp(radius * 0.1f, 0.01f, radius * 0.25f));
            changed |= Set(ref settings.ColliderLayer, character.gameObject.layer);
            if (settings.CollisionLayerMask.value == 0)
            {
                settings.CollisionLayerMask = Physics.DefaultRaycastLayers;
                changed = true;
            }
            changed |= Set(
                ref settings.InputAuthorityBehavior,
                EKCCAuthorityBehavior.PredictFixed_PredictRender);
            changed |= Set(
                ref settings.StateAuthorityBehavior,
                EKCCAuthorityBehavior.PredictFixed_InterpolateRender);
            changed |= Set(
                ref settings.ProxyInterpolationMode,
                EKCCInterpolationMode.Full);
            changed |= Set(ref settings.ForcePredictedLookRotation, true);
            changed |= Set(ref settings.AllowClientTeleports, false);

            Object[] processors = settings.Processors ?? Array.Empty<Object>();
            // Preserve customer ordering, null slots, and unrelated custom processors. Each
            // required processor category is normalized to one active reference because Photon
            // executes every resolvable entry and multiple Environment/GC2 processors would
            // compete. An adopted motor's exact original array remains in its rollback snapshot.
            var normalized = new List<Object>(processors);
            NormalizeProcessorCategory(normalized, environment);
            NormalizeProcessorCategory(normalized, groundSnap);
            NormalizeProcessorCategory(normalized, stepUp);
            NormalizeProcessorCategory(normalized, gc2Processor);
            if (!processors.SequenceEqual(normalized))
            {
                settings.Processors = normalized.ToArray();
                changed = true;
            }

            if (!changed) return;
            EditorUtility.SetDirty(kcc);
            AddChange(changes, "GC2-aligned KCC capsule and processor settings");
        }

        private static void NormalizeProcessorCategory<T>(
            List<Object> processors,
            T required)
            where T : KCCProcessor
        {
            int retainedIndex = -1;
            for (int i = 0; i < processors.Count; ++i)
            {
                Object candidate = processors[i];
                T resolvedCategory = candidate as T;
                bool sameCategory = resolvedCategory != null;
                if (!sameCategory && candidate != null &&
                    KCCUtility.ResolveProcessor(candidate, out IKCCProcessor resolved))
                {
                    resolvedCategory = resolved as T;
                    sameCategory = resolvedCategory != null;
                }
                if (!sameCategory) continue;

                if (retainedIndex < 0)
                {
                    // Preserve the customer's exact serialized object reference (component or
                    // provider GameObject) when it resolves to the selected processor. Replace
                    // it only when the wizard had to create an instance-local substitute.
                    if (resolvedCategory != required) processors[i] = required;
                    retainedIndex = i;
                }
                else
                {
                    processors.RemoveAt(i--);
                }
            }

            if (retainedIndex < 0) processors.Add(required);
        }

        private static bool ResolvesToProcessor(
            Object candidate,
            KCCProcessor required)
        {
            if (candidate == null || required == null) return false;
            if (candidate == required) return true;
            return KCCUtility.ResolveProcessor(candidate, out IKCCProcessor resolved) &&
                   ReferenceEquals(resolved, required);
        }

        private static void ConfigureGc2Processor(
            FusionGc2KccProcessor processor,
            EnvironmentProcessor environment,
            IList<string> changes)
        {
            var serialized = new SerializedObject(processor);
            SerializedProperty property = serialized.FindProperty("m_EnvironmentProcessor");
            if (property == null || property.objectReferenceValue == environment) return;
            property.objectReferenceValue = environment;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(processor);
            AddChange(changes, "linked GC2 and Environment KCC processors");
        }

        private static void ConfigureMotorBody(
            FusionKccMotorBody body,
            FusionKccCharacterBackend backend,
            global::Fusion.Addons.KCC.KCC kcc,
            FusionGc2KccProcessor gc2Processor,
            EnvironmentProcessor environment,
            IList<string> changes)
        {
            var serialized = new SerializedObject(body);
            bool changed = SetObject(serialized, "m_Backend", backend);
            changed |= SetObject(serialized, "m_Kcc", kcc);
            changed |= SetObject(serialized, "m_Gc2Processor", gc2Processor);
            changed |= SetObject(serialized, "m_EnvironmentProcessor", environment);
            if (!changed) return;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(body);
            AddChange(changes, "linked the nested KCC motor body");
        }

        private static void ConfigureBackend(
            FusionKccCharacterBackend backend,
            FusionKccMotorBody motorBody,
            FusionKccSharedAuthorityMode authorityMode,
            IList<string> changes)
        {
            var serialized = new SerializedObject(backend);
            bool changed = SetObject(serialized, "m_RuntimeAdapter", motorBody);
            SerializedProperty authority = serialized.FindProperty("m_SharedAuthorityMode");
            if (authority != null && authority.enumValueIndex != (int)authorityMode)
            {
                authority.enumValueIndex = (int)authorityMode;
                changed = true;
            }
            if (!changed) return;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(backend);
            AddChange(changes, "linked the optional KCC runtime adapter");
        }

        private static bool TryFindOrCreateMotor(
            Transform prefabRoot,
            IList<string> changes,
            out Transform motor,
            out bool objectCreated,
            out string error)
        {
            motor = null;
            objectCreated = false;
            error = string.Empty;

            Transform[] directKccMotors = prefabRoot
                .Cast<Transform>()
                .Where(child =>
                    child != null &&
                    child.GetComponent<global::Fusion.Addons.KCC.KCC>() != null)
                .ToArray();

            FusionKccMotorBody existing = prefabRoot
                .GetComponentsInChildren<FusionKccMotorBody>(true)
                .FirstOrDefault(body => body != null && body.transform.parent == prefabRoot);
            if (existing != null)
            {
                if (directKccMotors.Any(candidate => candidate != existing.transform))
                {
                    error =
                        $"'{prefabRoot.name}' contains more than one direct-child KCC motor. " +
                        "Keep exactly one customer KCC before running the wizard.";
                    return false;
                }
                motor = existing.transform;
                return true;
            }

            if (directKccMotors.Length > 1)
            {
                error =
                    $"'{prefabRoot.name}' contains {directKccMotors.Length} direct-child KCC " +
                    "motors. The wizard cannot safely choose which customer setup to adopt.";
                return false;
            }

            if (directKccMotors.Length == 1)
            {
                motor = directKccMotors[0];
                AddChange(changes, "adopted the existing customer KCC motor");
                return true;
            }

            Transform named = prefabRoot.Find(MotorName);
            if (named != null)
            {
                motor = named;
                return true;
            }

            var createdMotor = new GameObject(MotorName);
            createdMotor.transform.SetParent(prefabRoot, false);
            motor = createdMotor.transform;
            objectCreated = true;
            AddChange(changes, "created the nested Fusion KCC Motor");
            return true;
        }

        private static bool TryNormalizeDuplicateMotors(
            GameObject prefabRoot,
            Transform retained,
            IList<string> changes,
            out string error)
        {
            error = string.Empty;
            FusionKccMotorBody[] bodies =
                prefabRoot.GetComponentsInChildren<FusionKccMotorBody>(true);
            FusionKccMotorBody[] duplicates = bodies
                .Where(body => body != null && body.transform != retained)
                .ToArray();
            foreach (FusionKccMotorBody body in duplicates)
            {
                FusionKccSetupMarker marker =
                    body.GetComponent<FusionKccSetupMarker>();
                bool safelyOwned = marker != null &&
                                   marker.MotorObjectCreatedByWizard;
                if (!safelyOwned)
                {
                    error =
                        $"'{prefabRoot.name}' contains an additional FusionKccMotorBody on " +
                        $"'{body.gameObject.name}'. Remove or convert that custom setup before " +
                        "running the wizard.";
                    return false;
                }
            }

            foreach (FusionKccMotorBody body in duplicates)
            {
                Object.DestroyImmediate(body.gameObject, true);
                AddChange(changes, "removed a duplicate nested Fusion KCC Motor");
            }
            return true;
        }

        private static void CaptureSetupMarker(
            FusionKccSetupMarker marker,
            Transform motor,
            bool objectCreatedByWizard,
            bool hadRootController,
            bool rootControllerWasEnabled)
        {
            var serialized = new SerializedObject(marker);
            SetBool(serialized, "m_MotorObjectCreatedByWizard", objectCreatedByWizard);
            SetBool(serialized, "m_AdoptedSetupParked", false);
            SetBool(serialized, "m_HasRootControllerSnapshot", true);
            SetBool(serialized, "m_HadRootCharacterController", hadRootController);
            SetBool(
                serialized,
                "m_OriginalRootCharacterControllerEnabled",
                rootControllerWasEnabled);
            SetBool(serialized, "m_HasCustomerSnapshot", !objectCreatedByWizard);
            SetObjectArray(
                serialized,
                "m_WizardOwnedProcessorObjects",
                Array.Empty<Object>());
            if (!objectCreatedByWizard)
            {
                SetString(serialized, "m_OriginalObjectName", motor.name);
                SetBool(
                    serialized,
                    "m_OriginalObjectActiveSelf",
                    motor.gameObject.activeSelf);
                SetVector3(serialized, "m_OriginalLocalPosition", motor.localPosition);
                SetQuaternion(serialized, "m_OriginalLocalRotation", motor.localRotation);
                SetVector3(serialized, "m_OriginalLocalScale", motor.localScale);

                NetworkObject networkObject = motor.GetComponent<NetworkObject>();
                SetBool(serialized, "m_HadNetworkObject", networkObject != null);
                if (networkObject != null)
                {
                    SetInt(
                        serialized,
                        "m_OriginalNetworkObjectFlags",
                        (int)networkObject.Flags);
                    SetBool(
                        serialized,
                        "m_OriginalNetworkObjectInterpolation",
                        networkObject.EnableInterpolation);
                }

                Rigidbody rigidbody = motor.GetComponent<Rigidbody>();
                SetBool(serialized, "m_HadRigidbody", rigidbody != null);
                if (rigidbody != null)
                {
                    SetBool(
                        serialized,
                        "m_OriginalRigidbodyIsKinematic",
                        rigidbody.isKinematic);
                    SetBool(
                        serialized,
                        "m_OriginalRigidbodyUseGravity",
                        rigidbody.useGravity);
                    SetInt(
                        serialized,
                        "m_OriginalRigidbodyInterpolation",
                        (int)rigidbody.interpolation);
                    SetInt(
                        serialized,
                        "m_OriginalRigidbodyCollisionDetection",
                        (int)rigidbody.collisionDetectionMode);
                    SetInt(
                        serialized,
                        "m_OriginalRigidbodyConstraints",
                        (int)rigidbody.constraints);
                }

                global::Fusion.Addons.KCC.KCC kcc =
                    motor.GetComponent<global::Fusion.Addons.KCC.KCC>();
                SetBool(serialized, "m_HadKcc", kcc != null);
                if (kcc != null)
                {
                    SetBool(serialized, "m_OriginalKccEnabled", kcc.enabled);
                    KCCSettings settings = kcc.Settings;
                    SetInt(serialized, "m_OriginalKccShape", (int)settings.Shape);
                    SetBool(serialized, "m_OriginalKccIsTrigger", settings.IsTrigger);
                    SetFloat(serialized, "m_OriginalKccHeight", settings.Height);
                    SetFloat(serialized, "m_OriginalKccRadius", settings.Radius);
                    SetFloat(serialized, "m_OriginalKccExtent", settings.Extent);
                    SetInt(
                        serialized,
                        "m_OriginalKccColliderLayer",
                        settings.ColliderLayer);
                    SetInt(
                        serialized,
                        "m_OriginalKccCollisionLayerMask",
                        settings.CollisionLayerMask.value);
                    SetInt(
                        serialized,
                        "m_OriginalKccInputAuthorityBehavior",
                        (int)settings.InputAuthorityBehavior);
                    SetInt(
                        serialized,
                        "m_OriginalKccStateAuthorityBehavior",
                        (int)settings.StateAuthorityBehavior);
                    SetInt(
                        serialized,
                        "m_OriginalKccProxyInterpolationMode",
                        (int)settings.ProxyInterpolationMode);
                    SetBool(
                        serialized,
                        "m_OriginalKccForcePredictedLookRotation",
                        settings.ForcePredictedLookRotation);
                    SetBool(
                        serialized,
                        "m_OriginalKccAllowClientTeleports",
                        settings.AllowClientTeleports);
                    SetObjectArray(
                        serialized,
                        "m_OriginalKccProcessors",
                        settings.Processors ?? Array.Empty<Object>());
                }

                EnvironmentProcessor environment =
                    motor.GetComponent<EnvironmentProcessor>();
                SetBool(serialized, "m_HadEnvironmentProcessor", environment != null);
                if (environment != null)
                {
                    SetBool(
                        serialized,
                        "m_OriginalEnvironmentProcessorEnabled",
                        environment.enabled);
                }

                GroundSnapProcessor groundSnap = motor.GetComponent<GroundSnapProcessor>();
                SetBool(serialized, "m_HadGroundSnapProcessor", groundSnap != null);
                if (groundSnap != null)
                {
                    SetBool(
                        serialized,
                        "m_OriginalGroundSnapProcessorEnabled",
                        groundSnap.enabled);
                }

                StepUpProcessor stepUp = motor.GetComponent<StepUpProcessor>();
                SetBool(serialized, "m_HadStepUpProcessor", stepUp != null);
                if (stepUp != null)
                {
                    SetBool(
                        serialized,
                        "m_OriginalStepUpProcessorEnabled",
                        stepUp.enabled);
                }

                FusionGc2KccProcessor gc2Processor =
                    motor.GetComponent<FusionGc2KccProcessor>();
                SetBool(serialized, "m_HadGc2Processor", gc2Processor != null);
                if (gc2Processor != null)
                {
                    SetBool(
                        serialized,
                        "m_OriginalGc2ProcessorEnabled",
                        gc2Processor.enabled);
                }
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(marker);
        }

        private static void EnsureRootControllerSnapshot(
            FusionKccSetupMarker marker,
            bool hadRootController,
            bool rootControllerWasEnabled)
        {
            if (marker == null || marker.HasRootControllerSnapshot) return;

            var serialized = new SerializedObject(marker);
            SetBool(serialized, "m_HasRootControllerSnapshot", true);
            SetBool(serialized, "m_HadRootCharacterController", hadRootController);
            SetBool(
                serialized,
                "m_OriginalRootCharacterControllerEnabled",
                rootControllerWasEnabled);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(marker);
        }

        private static void SetMarkerParked(
            FusionKccSetupMarker marker,
            bool parked)
        {
            if (marker == null || marker.AdoptedSetupParked == parked) return;

            var serialized = new SerializedObject(marker);
            SetBool(serialized, "m_AdoptedSetupParked", parked);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(marker);
        }

        private static bool TryReadRootControllerSnapshot(
            GameObject prefabRoot,
            out bool hasSnapshot,
            out bool hadRootController,
            out bool rootControllerWasEnabled,
            out string error)
        {
            hasSnapshot = false;
            hadRootController = false;
            rootControllerWasEnabled = false;
            error = string.Empty;

            FusionKccSetupMarker[] markers =
                prefabRoot.GetComponentsInChildren<FusionKccSetupMarker>(true);
            foreach (FusionKccSetupMarker marker in markers)
            {
                if (marker == null || !marker.HasRootControllerSnapshot) continue;
                if (!hasSnapshot)
                {
                    hasSnapshot = true;
                    hadRootController = marker.HadRootCharacterController;
                    rootControllerWasEnabled =
                        marker.OriginalRootCharacterControllerEnabled;
                    continue;
                }

                if (hadRootController == marker.HadRootCharacterController &&
                    rootControllerWasEnabled ==
                    marker.OriginalRootCharacterControllerEnabled)
                {
                    continue;
                }

                error =
                    $"'{prefabRoot.name}' contains conflicting KCC setup snapshots for its " +
                    "root CharacterController. Reapply the KCC setup after removing the stale " +
                    "duplicate marker.";
                return false;
            }

            return true;
        }

        private static bool RestoreAdoptedMotor(
            GameObject motorObject,
            FusionKccSetupMarker marker,
            IList<string> changes,
            out string error)
        {
            if (!ValidateAdoptedMotorForRestore(motorObject, marker, out error)) return false;

            var snapshot = new SerializedObject(marker);
            bool hadNetworkObject = ReadBool(snapshot, "m_HadNetworkObject");
            bool hadRigidbody = ReadBool(snapshot, "m_HadRigidbody");
            bool hadKcc = ReadBool(snapshot, "m_HadKcc");

            NetworkObject networkObject = motorObject.GetComponent<NetworkObject>();
            Rigidbody rigidbody = motorObject.GetComponent<Rigidbody>();
            global::Fusion.Addons.KCC.KCC kcc =
                motorObject.GetComponent<global::Fusion.Addons.KCC.KCC>();
            if ((hadNetworkObject && networkObject == null) ||
                (hadRigidbody && rigidbody == null) ||
                (hadKcc && kcc == null))
            {
                error =
                    $"The adopted customer KCC motor '{motorObject.name}' lost one of its " +
                    "original components. The wizard stopped rather than overwrite an incomplete " +
                    "customer setup.";
                return false;
            }

            FusionKccMotorBody body = motorObject.GetComponent<FusionKccMotorBody>();
            if (body != null) Object.DestroyImmediate(body, true);

            if (hadKcc)
            {
                KCCSettings settings = kcc.Settings;
                settings.Shape = (EKCCShape)ReadInt(snapshot, "m_OriginalKccShape");
                settings.IsTrigger = ReadBool(snapshot, "m_OriginalKccIsTrigger");
                settings.Height = ReadFloat(snapshot, "m_OriginalKccHeight");
                settings.Radius = ReadFloat(snapshot, "m_OriginalKccRadius");
                settings.Extent = ReadFloat(snapshot, "m_OriginalKccExtent");
                settings.ColliderLayer = ReadInt(snapshot, "m_OriginalKccColliderLayer");
                settings.CollisionLayerMask = ReadInt(
                    snapshot,
                    "m_OriginalKccCollisionLayerMask");
                settings.InputAuthorityBehavior = (EKCCAuthorityBehavior)ReadInt(
                    snapshot,
                    "m_OriginalKccInputAuthorityBehavior");
                settings.StateAuthorityBehavior = (EKCCAuthorityBehavior)ReadInt(
                    snapshot,
                    "m_OriginalKccStateAuthorityBehavior");
                settings.ProxyInterpolationMode = (EKCCInterpolationMode)ReadInt(
                    snapshot,
                    "m_OriginalKccProxyInterpolationMode");
                settings.ForcePredictedLookRotation = ReadBool(
                    snapshot,
                    "m_OriginalKccForcePredictedLookRotation");
                settings.AllowClientTeleports = ReadBool(
                    snapshot,
                    "m_OriginalKccAllowClientTeleports");
                settings.Processors = ReadObjectArray(
                    snapshot,
                    "m_OriginalKccProcessors");
                // Keep the restored customer motor dormant while another GC2 movement backend
                // is selected. The retained marker lets a later KCC conversion reuse this exact
                // object and its original settings without creating a duplicate hierarchy.
                kcc.enabled = false;
                EditorUtility.SetDirty(kcc);
            }
            else if (kcc != null)
            {
                Object.DestroyImmediate(kcc, true);
                kcc = null;
            }

            RestoreOrRemoveProcessor<StepUpProcessor>(
                motorObject,
                snapshot,
                "m_HadStepUpProcessor",
                "m_OriginalStepUpProcessorEnabled");
            RestoreOrRemoveProcessor<GroundSnapProcessor>(
                motorObject,
                snapshot,
                "m_HadGroundSnapProcessor",
                "m_OriginalGroundSnapProcessorEnabled");
            RestoreOrRemoveProcessor<EnvironmentProcessor>(
                motorObject,
                snapshot,
                "m_HadEnvironmentProcessor",
                "m_OriginalEnvironmentProcessorEnabled");
            RestoreOrRemoveProcessor<FusionGc2KccProcessor>(
                motorObject,
                snapshot,
                "m_HadGc2Processor",
                "m_OriginalGc2ProcessorEnabled");
            DestroyOwnedProcessorObjects(motorObject.transform, marker, changes);

            if (hadRigidbody)
            {
                rigidbody.isKinematic = ReadBool(
                    snapshot,
                    "m_OriginalRigidbodyIsKinematic");
                rigidbody.useGravity = ReadBool(
                    snapshot,
                    "m_OriginalRigidbodyUseGravity");
                rigidbody.interpolation = (RigidbodyInterpolation)ReadInt(
                    snapshot,
                    "m_OriginalRigidbodyInterpolation");
                rigidbody.collisionDetectionMode = (CollisionDetectionMode)ReadInt(
                    snapshot,
                    "m_OriginalRigidbodyCollisionDetection");
                rigidbody.constraints = (RigidbodyConstraints)ReadInt(
                    snapshot,
                    "m_OriginalRigidbodyConstraints");
                EditorUtility.SetDirty(rigidbody);
            }
            else if (rigidbody != null)
            {
                Object.DestroyImmediate(rigidbody, true);
            }

            if (hadNetworkObject)
            {
                networkObject.Flags = (NetworkObjectFlags)ReadInt(
                    snapshot,
                    "m_OriginalNetworkObjectFlags");
                networkObject.EnableInterpolation = ReadBool(
                    snapshot,
                    "m_OriginalNetworkObjectInterpolation");
                EditorUtility.SetDirty(networkObject);
            }
            else if (networkObject != null)
            {
                Object.DestroyImmediate(networkObject, true);
            }

            Transform motor = motorObject.transform;
            motor.localPosition = ReadVector3(snapshot, "m_OriginalLocalPosition");
            motor.localRotation = ReadQuaternion(snapshot, "m_OriginalLocalRotation");
            motor.localScale = ReadVector3(snapshot, "m_OriginalLocalScale");
            motorObject.name = ReadString(snapshot, "m_OriginalObjectName");
            motorObject.SetActive(ReadBool(snapshot, "m_OriginalObjectActiveSelf"));
            EditorUtility.SetDirty(motor);

            if (hadKcc)
            {
                SetMarkerParked(marker, true);
                AddChange(changes, "restored and parked the adopted customer KCC motor");
            }
            else
            {
                Object.DestroyImmediate(marker, true);
                AddChange(changes, "restored the adopted customer motor object");
            }
            return true;
        }

        private static bool ValidateAdoptedMotorForRestore(
            GameObject motorObject,
            FusionKccSetupMarker marker,
            out string error)
        {
            error = string.Empty;
            if (motorObject == null || marker == null || !marker.HasCustomerSnapshot)
            {
                error =
                    "The adopted customer KCC motor has no reversible setup snapshot. Reapply " +
                    "the KCC setup before converting it.";
                return false;
            }

            var snapshot = new SerializedObject(marker);
            var missing = new List<string>();
            AddMissing<NetworkObject>("NetworkObject", "m_HadNetworkObject");
            AddMissing<Rigidbody>("Rigidbody", "m_HadRigidbody");
            AddMissing<global::Fusion.Addons.KCC.KCC>("KCC", "m_HadKcc");
            AddMissing<EnvironmentProcessor>(
                "EnvironmentProcessor",
                "m_HadEnvironmentProcessor");
            AddMissing<GroundSnapProcessor>(
                "GroundSnapProcessor",
                "m_HadGroundSnapProcessor");
            AddMissing<StepUpProcessor>("StepUpProcessor", "m_HadStepUpProcessor");
            AddMissing<FusionGc2KccProcessor>(
                "FusionGc2KccProcessor",
                "m_HadGc2Processor");
            if (!ValidateOwnedProcessorObjects(motorObject.transform, marker, out error))
            {
                return false;
            }
            if (missing.Count == 0) return true;

            error =
                $"The adopted customer KCC motor '{motorObject.name}' lost original " +
                $"component(s): {string.Join(", ", missing)}. The wizard stopped before " +
                "changing any KCC object rather than overwrite an incomplete customer setup.";
            return false;

            void AddMissing<T>(string displayName, string presenceProperty)
                where T : Component
            {
                if (ReadBool(snapshot, presenceProperty) && motorObject.GetComponent<T>() == null)
                {
                    missing.Add(displayName);
                }
            }
        }

        private static void DestroyOwnedProcessorObjects(
            Transform motor,
            FusionKccSetupMarker marker,
            IList<string> changes)
        {
            int removed = 0;
            foreach (GameObject processorObject in
                     marker.WizardOwnedProcessorObjects ?? Array.Empty<GameObject>())
            {
                if (processorObject == null || processorObject.transform.parent != motor)
                    continue;
                Object.DestroyImmediate(processorObject, true);
                removed++;
            }

            var serialized = new SerializedObject(marker);
            SetObjectArray(
                serialized,
                "m_WizardOwnedProcessorObjects",
                Array.Empty<Object>());
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(marker);
            if (removed > 0)
            {
                AddChange(
                    changes,
                    $"removed {removed} wizard-owned KCC processor object(s)");
            }
        }

        private static void RestoreOrRemoveProcessor<T>(
            GameObject motorObject,
            SerializedObject snapshot,
            string presenceProperty,
            string enabledProperty)
            where T : UnityEngine.Behaviour
        {
            T component = motorObject.GetComponent<T>();
            if (!ReadBool(snapshot, presenceProperty))
            {
                if (component != null) Object.DestroyImmediate(component, true);
                return;
            }

            if (component == null) return;
            component.enabled = ReadBool(snapshot, enabledProperty);
            EditorUtility.SetDirty(component);
        }

        private static void ConfigureMotorTransform(
            Transform motor,
            float height,
            IList<string> changes)
        {
            Quaternion desiredRotation = Quaternion.identity;
            Vector3 parentScale = motor.parent != null
                ? motor.parent.lossyScale
                : Vector3.one;
            Vector3 desiredPosition = GetMotorLocalFootOffset(motor.parent, height);
            Vector3 desiredScale = new Vector3(
                SafeInverse(parentScale.x),
                SafeInverse(parentScale.y),
                SafeInverse(parentScale.z));
            bool changed = false;
            if ((motor.localPosition - desiredPosition).sqrMagnitude > 0.000001f)
            {
                motor.localPosition = desiredPosition;
                changed = true;
            }
            if (Quaternion.Angle(motor.localRotation, desiredRotation) > 0.001f)
            {
                motor.localRotation = desiredRotation;
                changed = true;
            }
            if ((motor.localScale - desiredScale).sqrMagnitude > 0.000001f)
            {
                motor.localScale = desiredScale;
                changed = true;
            }
            if (!changed) return;
            EditorUtility.SetDirty(motor);
            AddChange(changes, "GC2-root to KCC-foot pose mapping");
        }

        private static void RemoveCompetingRootTrsp(
            GameObject prefabRoot,
            IList<string> changes)
        {
            NetworkTRSP[] writers = prefabRoot.GetComponents<NetworkTRSP>();
            int removed = 0;
            foreach (NetworkTRSP writer in writers)
            {
                if (writer == null) continue;
                Object.DestroyImmediate(writer, true);
                removed++;
            }
            if (removed > 0)
            {
                AddChange(
                    changes,
                    $"removed {removed} competing root NetworkTRSP component(s)");
            }
        }

        private static void ValidateMotorAuthority(
            NetworkObject networkObject,
            FusionKccEditorSetupOptions options,
            IList<FusionKccEditorValidationIssue> issues)
        {
            NetworkObjectFlags flags = networkObject.Flags;
            bool master = (flags & NetworkObjectFlags.MasterClientObject) != 0;
            bool overrideAllowed =
                (flags & NetworkObjectFlags.AllowStateAuthorityOverride) != 0;
            bool destroyOnLeave =
                (flags & NetworkObjectFlags.DestroyWhenStateAuthorityLeaves) != 0;
            bool expectedMaster = options.SharedAuthorityMode ==
                                  FusionKccSharedAuthorityMode
                                      .SharedMasterMovementAuthority;
            if (master == expectedMaster && !overrideAllowed && !destroyOnLeave) return;

            AddIssue(
                issues,
                options.RequireAppliedSetup
                    ? FusionKccEditorIssueSeverity.Error
                    : FusionKccEditorIssueSeverity.Warning,
                $"The nested KCC NetworkObject authority flags do not match " +
                $"{options.SharedAuthorityMode}.",
                networkObject);
        }

        private static void ValidateProcessorAuthority(
            NetworkObject networkObject,
            FusionKccEditorSetupOptions options,
            IList<FusionKccEditorValidationIssue> issues)
        {
            NetworkObjectFlags flags = networkObject.Flags;
            bool valid = (flags & NetworkObjectFlags.MasterClientObject) != 0 &&
                         (flags & NetworkObjectFlags.AllowStateAuthorityOverride) == 0 &&
                         (flags & NetworkObjectFlags.DestroyWhenStateAuthorityLeaves) == 0 &&
                         (flags & NetworkObjectFlags.HasMainNetworkTRSP) == 0 &&
                         flags.GetInterestMode() == NetworkObjectInterestModes.Global;
            if (valid) return;

            AddIssue(
                issues,
                options.RequireAppliedSetup
                    ? FusionKccEditorIssueSeverity.Error
                    : FusionKccEditorIssueSeverity.Warning,
                "A wizard-owned KCC processor NetworkObject must remain master-owned, " +
                "globally interested, non-overridable, and contain no movement TRSP.",
                networkObject);
        }

        private static T EnsureComponent<T>(
            GameObject gameObject,
            IList<string> changes,
            string label)
            where T : Component
        {
            T component = gameObject.GetComponent<T>();
            if (component != null) return component;
            component = gameObject.AddComponent<T>();
            AddChange(changes, label);
            return component;
        }

        private static void EnsureEnabled(
            UnityEngine.Behaviour behaviour,
            IList<string> changes,
            string label)
        {
            if (behaviour == null || behaviour.enabled) return;
            behaviour.enabled = true;
            EditorUtility.SetDirty(behaviour);
            AddChange(changes, $"enabled {label}");
        }

        private static void ValidateSerializedObjectReference(
            Object target,
            string propertyName,
            Object expected,
            string message,
            FusionKccEditorIssueSeverity severity,
            IList<FusionKccEditorValidationIssue> issues)
        {
            if (target == null) return;
            var serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property != null && property.objectReferenceValue == expected) return;
            AddIssue(issues, severity, message, target);
        }

        private static T ReadSerializedObjectReference<T>(
            Object target,
            string propertyName)
            where T : Object
        {
            if (target == null) return null;
            var serialized = new SerializedObject(target);
            return serialized.FindProperty(propertyName)?.objectReferenceValue as T;
        }

        private static void SetBool(
            SerializedObject serialized,
            string propertyName,
            bool value)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property != null) property.boolValue = value;
        }

        private static void SetInt(
            SerializedObject serialized,
            string propertyName,
            int value)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property != null) property.intValue = value;
        }

        private static void SetFloat(
            SerializedObject serialized,
            string propertyName,
            float value)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property != null) property.floatValue = value;
        }

        private static void SetString(
            SerializedObject serialized,
            string propertyName,
            string value)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property != null) property.stringValue = value ?? string.Empty;
        }

        private static void SetVector3(
            SerializedObject serialized,
            string propertyName,
            Vector3 value)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property != null) property.vector3Value = value;
        }

        private static void SetQuaternion(
            SerializedObject serialized,
            string propertyName,
            Quaternion value)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property != null) property.quaternionValue = value;
        }

        private static void SetObjectArray(
            SerializedObject serialized,
            string propertyName,
            IReadOnlyList<Object> values)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null) return;
            int count = values?.Count ?? 0;
            property.arraySize = count;
            for (int i = 0; i < count; ++i)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }
        }

        private static bool ReadBool(SerializedObject serialized, string propertyName) =>
            serialized.FindProperty(propertyName)?.boolValue ?? false;

        private static int ReadInt(SerializedObject serialized, string propertyName) =>
            serialized.FindProperty(propertyName)?.intValue ?? 0;

        private static float ReadFloat(SerializedObject serialized, string propertyName) =>
            serialized.FindProperty(propertyName)?.floatValue ?? 0f;

        private static string ReadString(SerializedObject serialized, string propertyName) =>
            serialized.FindProperty(propertyName)?.stringValue ?? string.Empty;

        private static Vector3 ReadVector3(
            SerializedObject serialized,
            string propertyName) =>
            serialized.FindProperty(propertyName)?.vector3Value ?? Vector3.zero;

        private static Quaternion ReadQuaternion(
            SerializedObject serialized,
            string propertyName) =>
            serialized.FindProperty(propertyName)?.quaternionValue ?? Quaternion.identity;

        private static Object[] ReadObjectArray(
            SerializedObject serialized,
            string propertyName)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null || !property.isArray) return Array.Empty<Object>();
            var values = new Object[property.arraySize];
            for (int i = 0; i < property.arraySize; ++i)
            {
                values[i] = property.GetArrayElementAtIndex(i).objectReferenceValue;
            }
            return values;
        }

        private static bool SetObject(
            SerializedObject serialized,
            string propertyName,
            Object value)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null || property.objectReferenceValue == value) return false;
            property.objectReferenceValue = value;
            return true;
        }

        private static bool Set<T>(ref T field, T value)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            return true;
        }

        private static float SafeInverse(float value) =>
            Mathf.Abs(value) > 0.000001f ? 1f / value : 1f;

        private static Vector3 GetMotorLocalFootOffset(
            Transform parent,
            float height)
        {
            Vector3 parentScale = parent != null ? parent.lossyScale : Vector3.one;
            return new Vector3(
                0f,
                -height * 0.5f * SafeInverse(parentScale.y),
                0f);
        }

        private static void AddChange(IList<string> changes, string change)
        {
            if (changes == null || string.IsNullOrWhiteSpace(change) ||
                changes.Contains(change))
            {
                return;
            }
            changes.Add(change);
        }

        private static void AddIssue(
            IList<FusionKccEditorValidationIssue> issues,
            FusionKccEditorIssueSeverity severity,
            string message,
            Object context)
        {
            issues.Add(new FusionKccEditorValidationIssue(severity, message, context));
        }
    }
}
#endif
