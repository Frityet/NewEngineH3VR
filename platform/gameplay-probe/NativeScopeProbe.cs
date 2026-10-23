#if NEWENGINE_UNITY_2021
// Test-only optical diagnostics. Invoke Run explicitly after the normal scene/camera handoff.
// This fixture never calls Camera.Render, UpdateAllScopes, ApplyProjection or an input API.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using FistVR;
using NativeH3VR;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public static class NativeScopeProbe {
    const BindingFlags Fields =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [Serializable]
    sealed class ImageEvidence {
        public string path, unavailable;
        public int width, height, pixels, nonBlackPixels, redPixels, greenPixels, bluePixels;
        public float minimum, maximum, mean, contrast;
        public bool available;
    }
    [Serializable]
    sealed class Observation {
        public string phase, scene, sourceName, mainName, shader, mainTextureName, scopeTargetName;
        public int frame, sourceId, mainId, scopeCameraId, sourcePreCull, sourcePreRender,
            sourcePostRender, scopePreCull, scopePostRender;
        public bool instanceMatches, sourceEnabled, scopeEnabled, scopeViewVisible, cameraRelative,
            targetCreated, shaderSupported;
        public bool useStereo, singlePassStereo, projectionPresent, mainTextureMatchesScopeTarget;
        public int targetWidth, targetHeight, targetMSAA, stereoState, activeEyeIndex;
        public string targetFormat;
        public Vector3 sourcePosition, scopePosition, scopeForward, scopeViewUVS;
        public Vector4 mainTexUVS, vignette, projectionVignette;
        public float vignetteFlat, eyeIndex, projectionVignetteFlat;
    }
    [Serializable]
    sealed class Report {
        public string schema = "newengineh3vr-scope-probe-1", startedUtc, finishedUtc, scene, mode,
                      item, itemGuid, scopeName, error;
        public string scope = "Diagnostic overhead; original scope lifecycle and native eye " +
                              "rendering only. Callback phases are observations, not a guarantee " +
                              "of ordering against component callbacks. Pixel evidence is " +
                              "diagnostic, not full optical equivalence or headset acceptance.";
        public string sourceReferences =
            "PIPScope_MainCamera.OnPreCull; " +
            "PIPScope.UpdateAllScopes/UpdateScope/CreateRT/ApplyProjection/ResetProjection; " +
            "PIPScope_Camera.PreRender/PostRender (original game methods).";
        public string lensRegionDefinition =
            "Projected physical ocular-circle bounding box, reconstructed through the original " +
            "main view matrix when camera-relative. Includes surrounding pixels; not a lens " +
            "segmentation mask. Full eye PNG remains authoritative for visual review.";
        public string selectedEye =
            "left native eye (scope fixture aligns to this eye, not the head centre)";
        public bool synthetic, finalEyeCopied, sourceMainBindingObserved, sourceCallbacksObserved,
            scopeCameraRendered;
        public bool automaticAlignmentRequiresReview, cleanupComplete;
        public int pid, sourceRenders, scopeRenders;
        public ImageEvidence scopeImage, finalEye, lensRegion;
        public Observation[] observations;
    }
    // New diagnostic types keep all previously shipped type field layouts intact.
    [Serializable]
    sealed class OpticalParameters {
        public float baseFOV, baseMagnification, currentMagnification, maxFOV, projectionDistance,
            projectionOffsetReticle;
        public float ocularLensDiameter, objectiveLensDiameter, frontLensDiameter, exitPupilScale,
            exitPupilOffset;
        public float cameraOffsetRearLens, cameraClipRearLens, cameraRelativeDepthScale,
            chromaticAberration, distortion;
        public float eyeReliefOcular, exitPupilDiameterOcular, ocularLensScale, magnificationOcular,
            fovOcular, scopePlaneSizeOcular;
        public float reticlePlaneSizeOcular, magnificationScopeViewScaleOcular,
            fovLimitImageScaleOcular;
        public float eyeReliefObjective, exitPupilDiameterObjective, objectiveLensScale,
            magnificationObjective, fovObjective, scopePlaneSizeObjective;
        public bool disableExitPupil, disableProjection, usingFrontLens, enableFrontLens,
            isDecorative, firstFocalPlane;
        public float reticleFOV, scopeCameraFOV, scopeCameraNear, scopeCameraAspect;
        public Vector3 scopeLossyScale, lensLossyScale, scopeCameraPosition;
    }
    [Serializable]
    sealed class ProjectionEvidence {
        public bool present, enabled, isBiocular;
        public int cameraId, stereoState, activeEyeIndex;
        public float eyePosLerp, vignetteFlat, vignetteFlat_R, rVignetteFlat, rVignetteFlat_R,
            scopeViewScale;
        public Vector3 cameraPosition, cameraPositionR, cameraPositionC, scopeViewUVS,
            scopeViewUVS_R, internalTubeUVS, internalTubeUVS_R;
        public Vector3 rScopeViewUVS, rScopeViewUVS_R;
        public Vector4 reticleUVS, reticleUVS_R, vignetteUVS, vignetteUVS_R, rReticleUVS,
            rReticleUVS_R, rVignetteUVS, rVignetteUVS_R;
        public Vector2 scopeViewOffset;
    }
    [Serializable]
    sealed class OpticalObservation {
        public string phase;
        public int frame;
        public Vector3 sourcePosition, sourcePositionInScope, sourceOffsetFromOcularReliefPlane;
        public OpticalParameters parameters;
        public ProjectionEvidence cameraProjection, lastMainProjection;
        public Vector4 lensReticleUVS, lensInternalTube, lensScopeViewOffsetScale;
    }
    [Serializable]
    sealed class ExtraReport {
        public string reliefOffsetContract =
            "Owned fixture axial offset only; no original optical parameter changed. Positive " +
            "offset places the ocular farther in front of the selected eye. Zero preserves the " +
            "original exact-relief fixture.";
        public bool automaticPlacement;
        public float requestedReliefOffsetMM, derivedAlignmentEyeRelief, actualPlacementDistance;
        public OpticalObservation[] opticalObservations;
    }
    sealed class ExtraState {
        public readonly ExtraReport report = new ExtraReport();
        public readonly List<OpticalObservation> observations = new List<OpticalObservation>();
    }
    static class Diagnostics {
        public static readonly Dictionary<Session, ExtraState> sessions =
            new Dictionary<Session, ExtraState>();
    }
    [Serializable]
    sealed class MatrixEvidence {
        public bool finite, zero, matchesProjectionView;
        public string hash, determinant, m00, m11;
        public string[] elements;
    }
    [Serializable]
    sealed class TargetEvidence {
        public string name, shader;
        public int layer;
        public bool layerInCullingMask, frustumTestAvailable, visibleByCullingMatrix;
        public Vector3 position, boundsCenter, boundsSize;
    }
    [Serializable]
    sealed class CameraObservation {
        public string phase, cameraName, targetName, targetFormat, targetEye, sourceProjectionM00,
            sourceProjectionM11;
        public string lensMaterialName, lensShaderName;
        public string[] lensShaderKeywords;
        public int frame, cameraId, targetInstanceId, targetWidth, targetHeight,
            scopeTargetInstanceId, cullingMask;
        public bool targetCreated, stereoEnabled, nativeEyeCamera, nativeEyeRenderActive,
            nativePackedPolicy, packedPolicyCorrectionEnabled;
        public float sourceFOV, sourceAspect, cameraFOV, cameraAspect, nearClip, farClip;
        public MatrixEvidence projection, culling;
        public TargetEvidence[] ownedTargets;
    }
    [Serializable]
    sealed class CameraReport {
        public string matrixContract =
            "Float elements are invariant round-trip strings (including explicit nonfinite " +
            "values); hash is FNV-1a over little-endian IEEE754 bytes in row-major order. " +
            "Culling flags/target frustum checks observe current original state, never change it.";
        public CameraObservation[] cameraObservations;
        public int[] originalScopeTargetInstanceIDs, originalSourceTargetInstanceIDs;
    }
    sealed class CameraState {
        public readonly List<CameraObservation> observations = new List<CameraObservation>();
        public readonly HashSet<int> scopeTargets = new HashSet<int>(),
                                     sourceTargets = new HashSet<int>();
    }
    static class CameraDiagnostics {
        public static readonly Dictionary<Session, CameraState> sessions =
            new Dictionary<Session, CameraState>();
    }
    static class EyeSelection {
        public static readonly Dictionary<Session, bool> rightEye = new Dictionary<Session, bool>();
    }
    [Serializable]
    sealed class MeshExperimentReport {
        public string ownMeshExperiment =
            "Diagnostic experiment only: forceRenderingOff on owned original-prefab renderers " +
            "during its scope-camera preCull-to-postRender; restore before source-eye rendering. " +
            "No original shader/camera/optical parameter is changed.";
        public bool ownMeshExperimentRequested, ownMeshExperimentApplied,
            ownMeshExperimentRestored = true;
        public int ownMeshRendererCount, ownMeshScopePasses;
        public string ownMeshRestoreError;
    }
    sealed class MeshExperimentState {
        public readonly MeshExperimentReport report = new MeshExperimentReport();
        public Renderer[] renderers;
        public bool[] previous;
        public bool active;
    }
    static class MeshExperiment {
        public static readonly Dictionary<Session, MeshExperimentState> sessions =
            new Dictionary<Session, MeshExperimentState>();
    }
    [Serializable]
    sealed class MountedObservation {
        public string phase, mountName, mountPath, mountType, attachmentType, attachmentParent,
            interfaceType, controllerName;
        public int frame, mountAttachmentCount, gunAttachmentCount, gunRigidbodyCount,
            attachmentRigidbodyCount;
        public bool curMountMatches, registeredOnMount, registeredOnGun, rootObjectMatchesGun,
            controllerMatchesOptic;
        public bool gunHeld, attachmentHeld, gunHandAssigned, attachmentHandAssigned,
            allOwnedBodiesFrozen;
        public bool canScaleToMount, mountCanRescale, interfaceActive, mountActive;
        public int magnificationIndex, zeroDistanceIndex, reticleIndex, reticleColorIndex, flipped;
        public int scopeElevationMagnitude, scopeWindageMagnitude, reticleElevationMagnitude,
            reticleWindageMagnitude;
        public float rootScaleModifier, railFraction, railDistance, magnificationOverride;
        public Vector3 railFront, railRear, attachmentPosition, attachmentLocalPosition,
            attachmentLocalScale;
        public Vector3 opticLossyScale, assemblyPosition;
        public Quaternion attachmentRotation, assemblyRotation;
    }
    [Serializable]
    sealed class MountedFixtureReport {
        public string contract =
            "API-mounted synthetic graphics fixture only; no hand input/interaction. Exact owned " +
            "C7A2 plus EC79A2, original unique _RailMount_Top, ScaleToMount when permitted and " +
            "AttachToMount(false). Rail midpoint placement precedes the original snap/clamp API. " +
            "Complete gun/optic assembly is aligned to the selected eye; authored " +
            "optic/camera/material parameters are not overridden.";
        public string mountItem, mountItemGuid, opticItem, opticItemGuid, error, cleanupError;
        public bool requested, apiAttached, scaleApplied, verified, detachedForCleanup,
            deregisteredForCleanup, ownedGunDestroyed;
        public bool ownMeshDiagnosticOpticOnly = true;
        public int frozenOwnedGunBodies, frozenOwnedOpticBodies;
        public MountedObservation[] observations;
    }
    [Serializable]
    sealed class MountedReportEnvelope {
        public MountedFixtureReport mountedFixture;
    }
    sealed class MountedFixtureState {
        public readonly MountedFixtureReport report = new MountedFixtureReport();
        public readonly List<MountedObservation> observations = new List<MountedObservation>();
        public GameObject gunRoot;
        public FVRFireArm gun;
        public FVRFireArmAttachment attachment;
        public FVRFireArmAttachmentMount mount;
        public PIPScopeController controller;
    }
    static class MountedFixtures {
        public static readonly Dictionary<Session, MountedFixtureState> sessions =
            new Dictionary<Session, MountedFixtureState>();
    }

    // Opt-in requires NEWENGINEH3VR_SCOPE_PROBE=1 plus synthetic gameplay acceptance mode.
    // NEWENGINEH3VR_SCOPE_ITEM selects an exact original IM item ID to load; unset observes
    // an existing active scope, optionally filtered by NEWENGINEH3VR_SCOPE_MATCH (name substring).
    public static IEnumerator Run(NativeStereoRenderer renderer, string reportDirectory) {
        if (Environment.GetEnvironmentVariable("NEWENGINEH3VR_SCOPE_PROBE") != "1")
            yield break;
        if (Environment.GetEnvironmentVariable("NATIVEH3VR_GAMEPLAY_PROBE") != "1" ||
            !NativeSteamVRInput.Synthetic)
            throw new InvalidOperationException(
                "Scope probe requires synthetic NATIVEH3VR_GAMEPLAY_PROBE=1; never enable during " +
                "player profiling");
        if (!Path.IsPathRooted(reportDirectory))
            throw new ArgumentException("Scope report directory must be absolute");
        var session = new Session(renderer, reportDirectory);
        IEnumerator work = session.Execute();
        try {
            // Catch coroutine failures while retaining the report and owned-resource cleanup.
            while (true) {
                bool next;
                try {
                    next = work.MoveNext();
                } catch (Exception error) {
                    session.report.error = error.ToString();
                    break;
                }
                if (!next)
                    break;
                yield return work.Current;
            }
        } finally {
            session.Dispose();
        }
        if (session.report.error != null)
            throw new InvalidOperationException("Scope diagnostic failed: " + session.report.error);
    }

    sealed class Session : IDisposable {
        readonly NativeStereoRenderer renderer;
        readonly string directory;
        readonly List<Object> owned = new List<Object>();
        readonly List<Observation> rows = new List<Observation>();
        readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        public readonly Report report = new Report();
        Camera source;
        PIPScope optic;
        GameObject spawned;
        RenderTexture finalCopy, scopeCopy;
        Rect lensRect;
        Vector3 alignmentPosition;
        Quaternion alignmentRotation;
        bool lensRectAvailable, callbacks, armed, copied;
        int preCull, preRender, postRender, scopePre, scopePost;

        public Session(NativeStereoRenderer value, string path) {
            renderer = value;
            directory = path;
            Diagnostics.sessions.Add(this, new ExtraState());
            CameraDiagnostics.sessions.Add(this, new CameraState());
            EyeSelection.rightEye.Add(this, false);
            MeshExperiment.sessions.Add(this, new MeshExperimentState());
            MountedFixtures.sessions.Add(this, new MountedFixtureState());
            report.startedUtc = DateTime.UtcNow.ToString("o");
            report.synthetic = NativeSteamVRInput.Synthetic;
            using (var process = System.Diagnostics.Process.GetCurrentProcess()) report.pid =
                process.Id;
        }
        public IEnumerator Execute() {
            Directory.CreateDirectory(directory);
            if (renderer == null || renderer.sourceCamera == null || renderer.suspendSceneRendering)
                throw new InvalidOperationException(
                    "Normal native scene/camera handoff is not ready");
            source = renderer.sourceCamera;
            report.scene = SceneManager.GetActiveScene().name;
            string eye = Environment.GetEnvironmentVariable("NEWENGINEH3VR_SCOPE_EYE") ?? "left";
            if (eye != "left" && eye != "right")
                throw new ArgumentException("NEWENGINEH3VR_SCOPE_EYE must be left or right");
            bool rightEye = eye == "right";
            EyeSelection.rightEye[this] = rightEye;
            report.selectedEye =
                eye + " native eye (scope fixture aligns to this eye, not the head centre)";
            ExtraReport extra = Diagnostics.sessions[this].report;
            string offsetText =
                Environment.GetEnvironmentVariable("NEWENGINEH3VR_SCOPE_RELIEF_OFFSET_MM");
            float offset = 0;
            if (offsetText != null &&
                (!Single.TryParse(offsetText, NumberStyles.Float, CultureInfo.InvariantCulture,
                                  out offset) ||
                 Single.IsNaN(offset) || Single.IsInfinity(offset) || offset < -30 || offset > 30))
                throw new ArgumentException(
                    "NEWENGINEH3VR_SCOPE_RELIEF_OFFSET_MM must be finite invariant -30..30");
            extra.requestedReliefOffsetMM = offset;
            if (renderer.trackingOrigin == null || renderer.completedFrames == 0)
                throw new InvalidOperationException(
                    "A completed native frame and tracking origin are required to align the " +
                    "selected eye");
            var selectedView = rightEye ? renderer.latestFrame.right : renderer.latestFrame.left;
            alignmentPosition = renderer.trackingOrigin.TransformPoint(selectedView.pose.position);
            alignmentRotation = renderer.trackingOrigin.rotation * selectedView.pose.rotation;
            report.item = Environment.GetEnvironmentVariable("NEWENGINEH3VR_SCOPE_ITEM");
            MountedFixtureReport mountedReport = MountedFixtures.sessions[this].report;
            mountedReport.mountItem =
                Environment.GetEnvironmentVariable("NEWENGINEH3VR_SCOPE_MOUNT_ITEM");
            mountedReport.requested = !String.IsNullOrEmpty(mountedReport.mountItem);
            if (mountedReport.requested &&
                (mountedReport.mountItem != "C7A2" || report.item != "EC79A2"))
                throw new ArgumentException(
                    "Mounted scope fixture supports only NEWENGINEH3VR_SCOPE_MOUNT_ITEM=C7A2 " +
                    "with NEWENGINEH3VR_SCOPE_ITEM=EC79A2");
            string hideMeshes =
                Environment.GetEnvironmentVariable("NEWENGINEH3VR_SCOPE_HIDE_OWN_MESHES");
            if (hideMeshes != null && hideMeshes != "0" && hideMeshes != "1")
                throw new ArgumentException("NEWENGINEH3VR_SCOPE_HIDE_OWN_MESHES must be 0 or 1");
            MeshExperiment.sessions[this].report.ownMeshExperimentRequested = hideMeshes == "1";
            if (hideMeshes == "1" && String.IsNullOrEmpty(report.item))
                throw new InvalidOperationException(
                    "Own-mesh experiment requires an owned NEWENGINEH3VR_SCOPE_ITEM; observing " +
                    "an existing optic is prohibited");
            if (!String.IsNullOrEmpty(report.item)) {
                report.mode = "owned-original-prefab";
                FVRObject definition;
                if (IM.OD == null || !IM.OD.TryGetValue(report.item, out definition))
                    throw new InvalidOperationException(
                        "Exact original scope item absent from IM.OD: " + report.item);
                report.itemGuid = definition.Guid;
                var request = definition.GetGameObjectAsync();
                float loadDeadline = Time.realtimeSinceStartup + 60;
                while (!request.Pump()) {
                    if (Time.realtimeSinceStartup > loadDeadline)
                        throw new TimeoutException("Original scope Anvil load timed out");
                    yield return null;
                }
                if (request.Result == null)
                    throw new InvalidOperationException("Original scope Anvil load returned null");
                spawned = Object.Instantiate(request.Result);
                owned.Add(spawned);
                optic = spawned.GetComponentInChildren<PIPScope>(true);
                if (optic == null)
                    throw new InvalidOperationException(
                        "Requested original prefab has no PIPScope; legacy ScopeCam needs a " +
                        "separate fixture");
                mountedReport.frozenOwnedOpticBodies = FreezeOwned(spawned);
                // Only owned fixtures are positioned. Original cameras, materials and settings
                // remain untouched; the normal callbacks still decide whether the optic renders.
                yield return null;
                if (mountedReport.requested) {
                    IEnumerator mounting = MountOwnedScope();
                    while (mounting.MoveNext())
                        yield return mounting.Current;
                    report.mode = "api-mounted-synthetic-original-prefabs";
                }
                GameObject alignmentRoot =
                    mountedReport.requested ? MountedFixtures.sessions[this].gunRoot : spawned;
                Quaternion relative =
                    Quaternion.Inverse(alignmentRoot.transform.rotation) * optic.transform.rotation;
                alignmentRoot.transform.rotation = alignmentRotation * Quaternion.Inverse(relative);
                float relief = Read<float>(optic, "eyeReliefOcular");
                if (Single.IsNaN(relief) || Single.IsInfinity(relief) || relief < .02f ||
                    relief > .5f)
                    relief = .1f;
                extra.automaticPlacement = true;
                extra.derivedAlignmentEyeRelief = Read<float>(optic, "eyeReliefOcular");
                extra.actualPlacementDistance = relief + offset * .001f;
                alignmentRoot.transform.position +=
                    alignmentPosition +
                    alignmentRotation * Vector3.forward * extra.actualPlacementDistance -
                    optic.transform.position;
                Physics.SyncTransforms();
                if (mountedReport.requested)
                    ObserveMounted("assembly-aligned");
                AddTargets();
            } else {
                if (offset != 0)
                    throw new ArgumentException(
                        "Relief offset only applies to an owned NEWENGINEH3VR_SCOPE_ITEM fixture");
                report.mode = "observe-existing-no-reposition";
                string match = Environment.GetEnvironmentVariable("NEWENGINEH3VR_SCOPE_MATCH");
                PIPScope[] scopes = Object.FindObjectsOfType<PIPScope>();
                foreach (PIPScope candidate in scopes)
                    if (candidate.gameObject.activeInHierarchy &&
                        (String.IsNullOrEmpty(match) ||
                         candidate.name.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)) {
                        if (optic != null)
                            throw new InvalidOperationException(
                                "Multiple existing scopes match; set NEWENGINEH3VR_SCOPE_MATCH");
                        optic = candidate;
                    }
                if (optic == null)
                    throw new InvalidOperationException(
                        "No existing active PIPScope matches; supply an exact original " +
                        "NEWENGINEH3VR_SCOPE_ITEM");
            }
            report.scopeName = optic.name;
            Camera.onPreCull += PreCull;
            Camera.onPreRender += PreRender;
            Camera.onPostRender += PostRender;
            callbacks = true;
            armed = true;
            Debug.Log("NEWENGINEH3VR_SCOPE_PROBE_START mode=" + report.mode +
                      " scope=" + optic.name);
            float deadline = Time.realtimeSinceStartup + 12;
            while (!copied && Time.realtimeSinceStartup < deadline) {
                if (renderer == null || renderer.sourceCamera != source ||
                    SceneManager.GetActiveScene().name != report.scene)
                    throw new InvalidOperationException(
                        "Scene/source camera changed during scope fixture");
                yield return null;
            }
            armed = false;
            if (report.error != null)
                yield break;
            if (!copied)
                throw new TimeoutException("No completed native source-eye image after 12 " +
                                           "seconds; see callback observations");
            report.scopeImage = SaveImage(scopeCopy, "scope-rt.png", new Rect(0, 0, 1, 1));
            report.finalEye = SaveImage(finalCopy, "final-eye.png", new Rect(0, 0, 1, 1));
            report.lensRegion =
                lensRectAvailable
                    ? SaveImage(finalCopy, "final-lens-region.png", lensRect)
                    : new ImageEvidence { unavailable =
                                              "Lens viewport bounds unavailable/invisible; no " +
                                              "final-lens pixel claim" };
            report.automaticAlignmentRequiresReview =
                spawned != null && (optic.cameraRelativeRenderer || !lensRectAvailable);
        }
        static int FreezeOwned(GameObject root) {
            Rigidbody[] bodies = root.GetComponentsInChildren<Rigidbody>(true);
            foreach (Rigidbody body in bodies) {
                body.useGravity = false;
                body.isKinematic = true;
            }
            return bodies.Length;
        }
        IEnumerator MountOwnedScope() {
            MountedFixtureState state = MountedFixtures.sessions[this];
            state.report.opticItem = report.item;
            state.report.opticItemGuid = report.itemGuid;
            FVRObject definition;
            if (IM.OD == null || !IM.OD.TryGetValue("C7A2", out definition))
                throw new InvalidOperationException("Exact original C7A2 item absent from IM.OD");
            state.report.mountItemGuid = definition.Guid;
            if (!String.Equals(definition.Guid, "995587646fa94f443aab67ca2e5c0a8f",
                               StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(report.itemGuid, "4691ee3e16bb8fa4ab64ab4b8f43a260",
                               StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("C7A2/EC79A2 original prefab GUID mapping " +
                                                    "changed; refusing substituted assets");
            var request = definition.GetGameObjectAsync();
            float deadline = Time.realtimeSinceStartup + 60;
            while (!request.Pump()) {
                if (Time.realtimeSinceStartup > deadline)
                    throw new TimeoutException("Original C7A2 Anvil load timed out");
                yield return null;
            }
            if (request.Result == null)
                throw new InvalidOperationException("Original C7A2 Anvil load returned null");
            state.gunRoot = Object.Instantiate(request.Result);
            owned.Add(state.gunRoot);
            state.report.frozenOwnedGunBodies = FreezeOwned(state.gunRoot);
            yield return null;
            FVRFireArm[] guns = state.gunRoot.GetComponentsInChildren<FVRFireArm>(true);
            FVRFireArmAttachment[] attachments =
                spawned.GetComponentsInChildren<FVRFireArmAttachment>(true);
            if (guns.Length != 1 || attachments.Length != 1)
                throw new InvalidOperationException(
                    "C7A2/EC79A2 fixture requires one original firearm and one attachment");
            state.gun = guns[0];
            state.attachment = attachments[0];
            if (state.attachment.gameObject != spawned)
                throw new InvalidOperationException(
                    "Unexpected EC79A2 attachment root; refusing ambiguous owned hierarchy");
            if (state.attachment.curMount != null || !state.attachment.CanAttach() ||
                state.gun.IsHeld || state.attachment.IsHeld)
                throw new InvalidOperationException(
                    "Owned C7A2/EC79A2 must be unheld and initially unattached");
            foreach (FVRFireArmAttachmentMount candidate in state.gunRoot
                         .GetComponentsInChildren<FVRFireArmAttachmentMount>(true))
                if (candidate.name == "_RailMount_Top") {
                    if (state.mount != null)
                        throw new InvalidOperationException(
                            "Ambiguous original C7A2 _RailMount_Top");
                    state.mount = candidate;
                }
            FVRFireArmAttachmentMount mount = state.mount;
            if (mount == null || !mount.gameObject.activeInHierarchy || mount.Parent != state.gun ||
                mount.MyObject != state.gun || state.gun.AttachmentMounts == null ||
                !state.gun.AttachmentMounts.Contains(mount) ||
                mount.Type != FVRFireArmAttachementMountType.Picatinny ||
                mount.Type != state.attachment.Type || mount.Point_Front == null ||
                mount.Point_Rear == null || mount.GetRootMount() != mount ||
                !mount.Point_Front.IsChildOf(state.gunRoot.transform) ||
                !mount.Point_Rear.IsChildOf(state.gunRoot.transform) ||
                !mount.isMountableOn(state.attachment))
                throw new InvalidOperationException("Unexpected original C7A2 top Picatinny rail " +
                                                    "mapping; refusing substitute mount");
            if ((mount.Point_Front.position - mount.Point_Rear.position).sqrMagnitude < .000001f ||
                Single.IsNaN(mount.ScaleModifier) || Single.IsInfinity(mount.ScaleModifier) ||
                mount.ScaleModifier <= 0)
                throw new InvalidOperationException("Original C7A2 rail geometry/scale is invalid");
            state.controller = state.attachment.AttachmentInterface as PIPScopeController;
            if (state.controller == null || state.controller.PScope != optic)
                throw new InvalidOperationException("Original EC79A2 attachment interface does " +
                                                    "not reference the selected PIPScope");
            ObserveMounted("before-attach");
            if (state.attachment.CanScaleToMount && mount.CanThisRescale()) {
                state.attachment.ScaleToMount(mount);
                state.report.scaleApplied = true;
            }
            state.attachment.transform.rotation = mount.transform.rotation;
            state.attachment.transform.position =
                Vector3.Lerp(mount.Point_Rear.position, mount.Point_Front.position, .5f);
            state.attachment.AttachToMount(mount, false);
            state.report.apiAttached = true;
            // Let the original deferred Rigidbody destruction and attachment controller settle.
            yield return null;
            state.report.frozenOwnedGunBodies = FreezeOwned(state.gunRoot);
            if (state.attachment.curMount != mount ||
                !mount.AttachmentsList.Contains(state.attachment) ||
                !state.gun.Attachments.Contains(state.attachment) ||
                state.attachment.GetRootObject() != state.gun ||
                !state.attachment.transform.IsChildOf(state.gunRoot.transform))
                throw new InvalidOperationException("Original attachment API did not establish " +
                                                    "the owned C7A2 rail hierarchy/registrations");
            state.report.verified = true;
            ObserveMounted("after-attach");
        }
        void ObserveMounted(string phase) {
            MountedFixtureState state = MountedFixtures.sessions[this];
            if (!state.report.requested || state.gunRoot == null || state.gun == null ||
                state.attachment == null || state.mount == null)
                return;
            if (state.report.verified &&
                (state.attachment.curMount != state.mount ||
                 !state.mount.AttachmentsList.Contains(state.attachment) ||
                 !state.gun.Attachments.Contains(state.attachment) || state.gun.IsHeld ||
                 state.attachment.IsHeld || state.gun.m_hand != null ||
                 state.attachment.m_hand != null))
                throw new InvalidOperationException(
                    "API-mounted fixture ownership/registration changed; no hand-interaction " +
                    "claim is permitted");
            if (state.observations.Count >= 48)
                return;
            Vector3 rear = state.mount.Point_Rear.position,
                    front = state.mount.Point_Front.position, rail = front - rear;
            float fraction =
                Vector3.Dot(state.attachment.transform.position - rear, rail) / rail.sqrMagnitude;
            Rigidbody[] gunBodies = state.gunRoot.GetComponentsInChildren<Rigidbody>(true),
                        opticBodies = spawned.GetComponentsInChildren<Rigidbody>(true);
            bool frozen = true;
            foreach (Rigidbody body in gunBodies)
                frozen &= body.isKinematic && !body.useGravity;
            foreach (Rigidbody body in opticBodies)
                frozen &= body.isKinematic && !body.useGravity;
            string path = state.mount.name;
            for (Transform parent = state.mount.transform.parent;
                 parent != null && parent != state.gunRoot.transform; parent = parent.parent)
                path = parent.name + "/" + path;
            path = state.gunRoot.name + "/" + path;
            state.observations.Add(new MountedObservation {
                phase = phase,
                frame = Time.frameCount,
                mountName = state.mount.name,
                mountPath = path,
                mountType = state.mount.Type.ToString(),
                attachmentType = state.attachment.Type.ToString(),
                attachmentParent = state.attachment.transform.parent != null
                                       ? state.attachment.transform.parent.name
                                       : null,
                interfaceType = state.attachment.AttachmentInterface != null
                                    ? state.attachment.AttachmentInterface.GetType().FullName
                                    : null,
                controllerName = state.controller != null ? state.controller.name : null,
                controllerMatchesOptic =
                    state.controller != null && state.controller.PScope == optic,
                curMountMatches = state.attachment.curMount == state.mount,
                registeredOnMount = state.mount.AttachmentsList.Contains(state.attachment),
                registeredOnGun = state.gun.Attachments.Contains(state.attachment),
                rootObjectMatchesGun = state.attachment.GetRootObject() == state.gun,
                mountAttachmentCount = state.mount.AttachmentsList.Count,
                gunAttachmentCount = state.gun.Attachments.Count,
                gunHeld = state.gun.IsHeld,
                attachmentHeld = state.attachment.IsHeld,
                gunHandAssigned = state.gun.m_hand != null,
                attachmentHandAssigned = state.attachment.m_hand != null,
                allOwnedBodiesFrozen = frozen,
                gunRigidbodyCount = gunBodies.Length,
                attachmentRigidbodyCount = opticBodies.Length,
                canScaleToMount = state.attachment.CanScaleToMount,
                mountCanRescale = state.mount.CanThisRescale(),
                rootScaleModifier = state.mount.ScaleModifier,
                interfaceActive = state.attachment.AttachmentInterface != null &&
                                  state.attachment.AttachmentInterface.gameObject.activeInHierarchy,
                mountActive = state.mount.gameObject.activeInHierarchy,
                magnificationIndex = state.controller.MagnificationIndex,
                zeroDistanceIndex = state.controller.ZeroDistanceIndex,
                reticleIndex = state.controller.ReticleIndex,
                reticleColorIndex = state.controller.ReticleColorIndex,
                railFraction = fraction,
                flipped = state.controller.Flipped,
                scopeElevationMagnitude = state.controller.ScopeElevationMagnitude,
                scopeWindageMagnitude = state.controller.ScopeWindageMagnitude,
                reticleElevationMagnitude = state.controller.ReticleElevationMagnitude,
                reticleWindageMagnitude = state.controller.ReticleWindageMagnitude,
                magnificationOverride = state.controller.MagnificationOverride,
                railDistance = Vector3.Distance(state.attachment.transform.position,
                                                rear + rail * Mathf.Clamp01(fraction)),
                railFront = front,
                railRear = rear,
                attachmentPosition = state.attachment.transform.position,
                attachmentRotation = state.attachment.transform.rotation,
                attachmentLocalPosition = state.attachment.transform.localPosition,
                attachmentLocalScale = state.attachment.transform.localScale,
                opticLossyScale = optic.transform.lossyScale,
                assemblyPosition = state.gunRoot.transform.position,
                assemblyRotation = state.gunRoot.transform.rotation
            });
        }
        void AddTargets() {
            Shader shader = Shader.Find("Unlit/Color");
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException(
                    "Supported Unlit/Color shader required for diagnostic target");
            int mask = optic.scopeCam != null ? optic.scopeCam.cullingMask & source.cullingMask
                                              : source.cullingMask;
            int layer = -1;
            for (int i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0) {
                    layer = i;
                    break;
                }
            if (layer < 0)
                throw new InvalidOperationException(
                    "Scope and source camera share no target layer");
            Color[] colors = { Color.red, Color.green, Color.blue, Color.white };
            for (int i = 0; i < colors.Length; i++) {
                var material = new Material(shader);
                material.color = colors[i];
                owned.Add(material);
                GameObject panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
                owned.Add(panel);
                panel.name = "Owned scope diagnostic color " + i;
                panel.layer = layer;
                panel.transform.rotation = alignmentRotation;
                // Keep the same angular size while placing the target ahead of the
                // range's authored paper targets, which can occlude a 3 m fixture.
                panel.transform.position =
                    alignmentPosition + alignmentRotation * Vector3.forward * .65f +
                    alignmentRotation * Vector3.right * ((i % 2 == 0 ? -.04875f : .04875f)) +
                    alignmentRotation * Vector3.up * ((i < 2 ? .04875f : -.04875f));
                panel.transform.localScale = new Vector3(.0975f, .0975f, .002f);
                panel.GetComponent<Renderer>().sharedMaterial = material;
                Collider collider = panel.GetComponent<Collider>();
                if (collider != null)
                    collider.enabled = false;
            }
        }
        void PreCull(Camera camera) {
            if (camera == source)
                RestoreOwnMeshes();
            if (!armed)
                return;
            if (optic != null && camera == optic.scopeCam) {
                scopePre++;
                try {
                    HideOwnMeshes();
                } catch (Exception error) {
                    RestoreOwnMeshes();
                    CallbackFailed(error);
                }
                if (armed)
                    ObserveCameraSafely(camera, "scope.onPreCull");
            }
            if (camera == source) {
                preCull++;
                ObserveSafely("source.onPreCull");
                ObserveCameraSafely(camera, "source.onPreCull");
            }
        }
        void PreRender(Camera camera) {
            // Safety backstop for an interrupted nested scope render: the source
            // eye must always see the original optic geometry.
            if (camera == source)
                RestoreOwnMeshes();
            if (!armed)
                return;
            if (optic != null && camera == optic.scopeCam)
                ObserveCameraSafely(camera, "scope.onPreRender");
            if (camera != source)
                return;
            preRender++;
            ObserveSafely("source.onPreRender");
            ObserveCameraSafely(camera, "source.onPreRender");
            // Once the selected eye has been copied, later callbacks must not
            // replace its crop with a different eye's viewport rectangle.
            try {
                if (!copied)
                    UpdateLensRect();
            } catch (Exception error) {
                CallbackFailed(error);
            }
        }
        void PostRender(Camera camera) {
            if (optic != null && camera == optic.scopeCam) {
                try {
                    if (armed) {
                        scopePost++;
                        ObserveCameraSafely(camera, "scope.onPostRender");
                    }
                } finally {
                    RestoreOwnMeshes();
                }
                return;
            }
            if (!armed)
                return;
            if (camera != source)
                return;
            postRender++;
            ObserveSafely("source.onPostRender");
            ObserveCameraSafely(camera, "source.onPostRender");
            if (!armed)
                return;
            if (postRender < 12 || copied)
                return;
            // The opposite eye is intentionally outside the tightly aligned ocular.
            // Copy the selected eye while its original Render callback still owns the target.
            Vector3 left =
                renderer.trackingOrigin.TransformPoint(renderer.latestFrame.left.pose.position);
            Vector3 right =
                renderer.trackingOrigin.TransformPoint(renderer.latestFrame.right.pose.position);
            Vector3 selected = EyeSelection.rightEye[this] ? right : left;
            Vector3 opposite = EyeSelection.rightEye[this] ? left : right;
            if ((source.transform.position - selected).sqrMagnitude >=
                (source.transform.position - opposite).sqrMagnitude)
                return;
            try {
                if (source.targetTexture == null || !source.targetTexture.IsCreated())
                    throw new InvalidOperationException(
                        "Completed source eye has no created target texture");
                finalCopy = Copy(source.targetTexture, "Owned scope final eye copy");
                RenderTexture rt = Read<RenderTexture>(optic, "rt");
                if (rt != null && rt.IsCreated())
                    scopeCopy = Copy(rt, "Owned scope RT copy");
                copied = true;
                report.finalEyeCopied = true;
            } catch (Exception error) {
                report.error = error.ToString();
                armed = false;
                copied = true;
            }
        }
        void CallbackFailed(Exception error) {
            if (report.error == null)
                report.error = "Observer callback failed: " + error;
            armed = false;
            copied = true;
            RestoreOwnMeshes();
        }
        void HideOwnMeshes() {
            MeshExperimentState state = MeshExperiment.sessions[this];
            if (!state.report.ownMeshExperimentRequested)
                return;
            if (spawned == null)
                throw new InvalidOperationException("Own-mesh experiment lost its owned prefab");
            if (state.active)
                throw new InvalidOperationException("Nested own-mesh experiment scope pass");
            state.renderers = spawned.GetComponentsInChildren<Renderer>(true);
            if (state.renderers.Length == 0 || state.renderers.Length > 2048)
                throw new InvalidOperationException(
                    "Owned prefab renderer count outside diagnostic limit");
            state.previous = new bool[state.renderers.Length];
            for (int i = 0; i < state.renderers.Length; i++)
                state.previous[i] = state.renderers[i].forceRenderingOff;
            state.active = true;
            state.report.ownMeshExperimentRestored = false;
            state.report.ownMeshRendererCount = state.renderers.Length;
            state.report.ownMeshScopePasses++;
            state.report.ownMeshExperimentApplied = true;
            for (int i = 0; i < state.renderers.Length; i++)
                state.renderers[i].forceRenderingOff = true;
        }
        void RestoreOwnMeshes() {
            MeshExperimentState state;
            if (!MeshExperiment.sessions.TryGetValue(this, out state) || !state.active)
                return;
            bool restored = true;
            for (int i = 0; i < state.renderers.Length; i++)
                try {
                    if (state.renderers[i] != null)
                        state.renderers[i].forceRenderingOff = state.previous[i];
                } catch (Exception error) {
                    restored = false;
                    state.report.ownMeshRestoreError =
                        (state.report.ownMeshRestoreError ?? "") + "\n" + error;
                    report.error = (report.error ?? "") + "\nOwn-mesh restoration failed: " + error;
                    armed = false;
                    copied = true;
                }
            state.active = !restored;
            state.report.ownMeshExperimentRestored = restored;
        }
        void ObserveSafely(string phase) {
            try {
                Observe(phase);
                ObserveMounted(phase);
            } catch (Exception error) {
                CallbackFailed(error);
            }
        }
        void ObserveCameraSafely(Camera camera, string phase) {
            try {
                ObserveCamera(camera, phase);
            } catch (Exception error) {
                CallbackFailed(error);
            }
        }
        void ObserveCamera(Camera camera, string phase) {
            CameraState state = CameraDiagnostics.sessions[this];
            if (state.observations.Count >= 96 || camera == null || optic == null || source == null)
                return;
            RenderTexture target = camera.targetTexture;
            RenderTexture scopeTarget =
                optic.scopeCam != null ? optic.scopeCam.targetTexture : null;
            if (scopeTarget != null)
                state.scopeTargets.Add(scopeTarget.GetInstanceID());
            if (camera == source && target != null)
                state.sourceTargets.Add(target.GetInstanceID());
            Material lens = optic.lensRenderer != null ? optic.lensRenderer.sharedMaterial : null;
            Matrix4x4 projection = camera.projectionMatrix, culling = camera.cullingMatrix;
            MatrixEvidence cullingEvidence = Matrix(culling);
            cullingEvidence.matchesProjectionView =
                culling.Equals(projection * camera.worldToCameraMatrix);
            var targets = new List<TargetEvidence>();
            Plane[] planes =
                cullingEvidence.finite ? GeometryUtility.CalculateFrustumPlanes(culling) : null;
            foreach (Object resource in owned) {
                GameObject panel = resource as GameObject;
                if (panel == null || !panel.name.StartsWith("Owned scope diagnostic color ",
                                                            StringComparison.Ordinal))
                    continue;
                Renderer panelRenderer = panel.GetComponent<Renderer>();
                if (panelRenderer == null)
                    continue;
                Material material = panelRenderer.sharedMaterial;
                targets.Add(new TargetEvidence {
                    name = panel.name, layer = panel.layer,
                    shader =
                        material != null && material.shader != null ? material.shader.name : null,
                    layerInCullingMask = (camera.cullingMask & (1 << panel.layer)) != 0,
                    frustumTestAvailable = planes != null,
                    visibleByCullingMatrix = planes != null && GeometryUtility.TestPlanesAABB(
                                                                   planes, panelRenderer.bounds),
                    position = panel.transform.position, boundsCenter = panelRenderer.bounds.center,
                    boundsSize = panelRenderer.bounds.size
                });
            }
            state.observations.Add(new CameraObservation {
                phase = phase,
                frame = Time.frameCount,
                cameraName = camera.name,
                cameraId = camera.GetInstanceID(),
                targetInstanceId = target != null ? target.GetInstanceID() : 0,
                targetName = target != null ? target.name : null,
                targetCreated = target != null && target.IsCreated(),
                targetWidth = target != null ? target.width : 0,
                targetHeight = target != null ? target.height : 0,
                targetFormat = target != null ? target.format.ToString() : null,
                scopeTargetInstanceId = scopeTarget != null ? scopeTarget.GetInstanceID() : 0,
                cullingMask = camera.cullingMask,
                stereoEnabled = camera.stereoEnabled,
                targetEye = camera.stereoTargetEye.ToString(),
                nativeEyeCamera = NativeEyeProjection.IsEyeCamera(camera),
                nativeEyeRenderActive = NativeEyeProjection.IsRenderingEye,
                nativePackedPolicy = NewEnginePostProcessing.IsPackedStereo,
                packedPolicyCorrectionEnabled = NewEnginePostProcessing.Enabled,
                sourceFOV = source.fieldOfView,
                sourceAspect = source.aspect,
                sourceProjectionM00 =
                    source.projectionMatrix.m00.ToString("R", CultureInfo.InvariantCulture),
                sourceProjectionM11 =
                    source.projectionMatrix.m11.ToString("R", CultureInfo.InvariantCulture),
                cameraFOV = camera.fieldOfView,
                cameraAspect = camera.aspect,
                nearClip = camera.nearClipPlane,
                farClip = camera.farClipPlane,
                projection = Matrix(projection),
                culling = cullingEvidence,
                ownedTargets = targets.ToArray(),
                lensMaterialName = lens != null ? lens.name : null,
                lensShaderName = lens != null && lens.shader != null ? lens.shader.name : null,
                lensShaderKeywords = lens != null ? lens.shaderKeywords : new string[0]
            });
        }
        static MatrixEvidence Matrix(Matrix4x4 value) {
            var evidence = new MatrixEvidence {
                finite = true,
                zero = true,
                elements = new string[16],
                determinant = value.determinant.ToString("R", CultureInfo.InvariantCulture),
                m00 = value.m00.ToString("R", CultureInfo.InvariantCulture),
                m11 = value.m11.ToString("R", CultureInfo.InvariantCulture)
            };
            uint hash = 2166136261;
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++) {
                    float element = value[row, column];
                    evidence.elements[row * 4 + column] =
                        element.ToString("R", CultureInfo.InvariantCulture);
                    evidence.finite &= !Single.IsNaN(element) && !Single.IsInfinity(element);
                    evidence.zero &= element == 0;
                    byte[] bytes = BitConverter.GetBytes(element);
                    if (!BitConverter.IsLittleEndian)
                        Array.Reverse(bytes);
                    foreach (byte part in bytes)
                        hash = unchecked((hash ^ part) * 16777619);
                }
            evidence.hash = hash.ToString("x8", CultureInfo.InvariantCulture);
            return evidence;
        }
        void Observe(string phase) {
            if (rows.Count >= 48 || optic == null || source == null)
                return;
            Camera main =
                PIPScope_MainCamera.instance != null ? PIPScope_MainCamera.instance.cam : null;
            Camera scopeCamera = optic.scopeCam;
            RenderTexture target = scopeCamera != null ? scopeCamera.targetTexture : null;
            Material material =
                optic.lensRenderer != null ? optic.lensRenderer.sharedMaterial : null;
            block.Clear();
            if (optic.lensRenderer != null)
                optic.lensRenderer.GetPropertyBlock(block);
            Texture texture = block.GetTexture("_MainTex");
            PIPScope.ProjectionData projection = null;
            PIPScope_Camera mainComponent = source.GetComponent<PIPScope_Camera>();
            if (mainComponent != null)
                mainComponent.projectionDataSet.TryGetValue(optic, out projection);
            RecordOptics(phase, projection);
            report.sourceMainBindingObserved |= main == source;
            rows.Add(new Observation {
                phase = phase,
                scene = SceneManager.GetActiveScene().name,
                frame = Time.frameCount,
                sourceName = source.name,
                sourceId = source.GetInstanceID(),
                mainName = main != null ? main.name : null,
                mainId = main != null ? main.GetInstanceID() : 0,
                scopeCameraId = scopeCamera != null ? scopeCamera.GetInstanceID() : 0,
                instanceMatches = main == source,
                sourcePreCull = preCull,
                sourcePreRender = preRender,
                sourcePostRender = postRender,
                scopePreCull = scopePre,
                scopePostRender = scopePost,
                sourceEnabled = source.enabled,
                scopeEnabled = optic.enabled,
                scopeViewVisible = Read<bool>(optic, "scopeViewVisible"),
                cameraRelative = optic.cameraRelativeRenderer,
                sourcePosition = source.transform.position,
                scopePosition = optic.transform.position,
                scopeForward = optic.transform.forward,
                shader = material != null && material.shader != null ? material.shader.name : null,
                shaderSupported =
                    material != null && material.shader != null && material.shader.isSupported,
                useStereo = material != null && material.IsKeywordEnabled("USE_STEREO"),
                singlePassStereo = Shader.IsKeywordEnabled("UNITY_SINGLE_PASS_STEREO"),
                mainTextureName = texture != null ? texture.name : null,
                mainTextureMatchesScopeTarget = texture != null && texture == target,
                scopeTargetName = target != null ? target.name : null,
                targetCreated = target != null && target.IsCreated(),
                targetWidth = target != null ? target.width : 0,
                targetHeight = target != null ? target.height : 0,
                targetMSAA = target != null ? target.antiAliasing : 0,
                targetFormat = target != null ? target.format.ToString() : null,
                mainTexUVS = block.GetVector("_MainTexUVS"),
                vignette = block.GetVector("_Vignette"),
                vignetteFlat = block.GetFloat("_VignetteFlat"),
                eyeIndex = block.GetFloat("_EyeIndex"),
                projectionPresent = projection != null,
                stereoState = projection != null ? projection.stereoState : -1,
                activeEyeIndex = projection != null ? projection.activeEyeIndex : -1,
                scopeViewUVS = projection != null ? projection.scopeViewUVS : Vector3.zero,
                projectionVignette = projection != null ? projection.vignetteUVS : Vector4.zero,
                projectionVignetteFlat = projection != null ? projection.vignetteFlat : -1
            });
        }
        void RecordOptics(string phase, PIPScope.ProjectionData projection) {
            var parameters = new OpticalParameters {
                baseFOV = optic.baseFOV,
                baseMagnification = optic.baseMagnification,
                currentMagnification = optic.currentMagnification,
                maxFOV = optic.maxFOV,
                projectionDistance = optic.projectionDistance,
                projectionOffsetReticle = optic.projectionOffsetReticle,
                ocularLensDiameter = optic.ocularLensDiameter,
                objectiveLensDiameter = optic.objectiveLensDiameter,
                frontLensDiameter = optic.frontLensDiameter,
                exitPupilScale = optic.exitPupilScale,
                exitPupilOffset = optic.exitPupilOffset,
                cameraOffsetRearLens = optic.cameraOffsetRearLens,
                cameraClipRearLens = optic.cameraClipRearLens,
                cameraRelativeDepthScale = optic.cameraRelativeDepthScale,
                chromaticAberration = optic.chromaticAberration,
                distortion = optic.distortion,
                eyeReliefOcular = Read<float>(optic, "eyeReliefOcular"),
                exitPupilDiameterOcular = Read<float>(optic, "exitPupilDiameterOcular"),
                ocularLensScale = Read<float>(optic, "ocularLensScale"),
                magnificationOcular = Read<float>(optic, "magnificationOcular"),
                fovOcular = Read<float>(optic, "fovOcular"),
                scopePlaneSizeOcular = Read<float>(optic, "scopePlaneSizeOcular"),
                reticlePlaneSizeOcular = Read<float>(optic, "reticlePlaneSizeOcular"),
                magnificationScopeViewScaleOcular =
                    Read<float>(optic, "magnificationScopeViewScaleOcular"),
                fovLimitImageScaleOcular = Read<float>(optic, "fovLimitImageScaleOcular"),
                eyeReliefObjective = Read<float>(optic, "eyeReliefObjective"),
                exitPupilDiameterObjective = Read<float>(optic, "exitPupilDiameterObjective"),
                objectiveLensScale = Read<float>(optic, "objectiveLensScale"),
                magnificationObjective = Read<float>(optic, "magnificationObjective"),
                fovObjective = Read<float>(optic, "fovObjective"),
                scopePlaneSizeObjective = Read<float>(optic, "scopePlaneSizeObjective"),
                disableExitPupil = optic.disableExitPupil,
                disableProjection = optic.disableProjection,
                usingFrontLens = Read<bool>(optic, "usingFrontLens"),
                enableFrontLens = optic.enableFrontLens,
                isDecorative = optic.isDecorative,
                firstFocalPlane = optic.reticle != null && optic.reticle.isFirstFocalPlane,
                reticleFOV = optic.reticle != null ? optic.reticle.reticleFOV : -1,
                scopeCameraFOV = optic.scopeCam != null ? optic.scopeCam.fieldOfView : -1,
                scopeCameraNear = optic.scopeCam != null ? optic.scopeCam.nearClipPlane : -1,
                scopeCameraAspect = optic.scopeCam != null ? optic.scopeCam.aspect : -1,
                scopeLossyScale = optic.transform.lossyScale,
                lensLossyScale = optic.lensRenderer != null
                                     ? optic.lensRenderer.transform.lossyScale
                                     : Vector3.zero,
                scopeCameraPosition =
                    optic.scopeCam != null ? optic.scopeCam.transform.position : Vector3.zero
            };
            Vector3 cameraPosition =
                projection != null ? projection.cameraPosition : source.transform.position;
            Diagnostics.sessions[this].observations.Add(new OpticalObservation {
                phase = phase, frame = Time.frameCount, sourcePosition = source.transform.position,
                sourcePositionInScope = optic.transform.InverseTransformPoint(cameraPosition),
                sourceOffsetFromOcularReliefPlane = optic.transform.InverseTransformDirection(
                    cameraPosition - (optic.transform.position -
                                      optic.transform.forward * parameters.eyeReliefOcular)),
                parameters = parameters, cameraProjection = Projection(projection),
                lastMainProjection = Projection(optic.mainProjection),
                lensReticleUVS = block.GetVector("_ReticleUVS"),
                lensInternalTube = block.GetVector("_InternalTube"),
                lensScopeViewOffsetScale = block.GetVector("_ScopeViewOffsetScale")
            });
        }
        static ProjectionEvidence Projection(PIPScope.ProjectionData value) {
            if (value == null)
                return new ProjectionEvidence { present = false };
            return new ProjectionEvidence { present = true,
                                            enabled = value.enabled,
                                            isBiocular = value.isBiocular,
                                            cameraId = value.camera != null
                                                           ? value.camera.GetInstanceID()
                                                           : 0,
                                            stereoState = value.stereoState,
                                            activeEyeIndex = value.activeEyeIndex,
                                            eyePosLerp = value.eyePosLerp,
                                            cameraPosition = value.cameraPosition,
                                            cameraPositionR = value.cameraPositionR,
                                            cameraPositionC = value.cameraPositionC,
                                            scopeViewUVS = value.scopeViewUVS,
                                            scopeViewUVS_R = value.scopeViewUVS_R,
                                            reticleUVS = value.reticleUVS,
                                            reticleUVS_R = value.reticleUVS_R,
                                            vignetteUVS = value.vignetteUVS,
                                            vignetteUVS_R = value.vignetteUVS_R,
                                            vignetteFlat = value.vignetteFlat,
                                            vignetteFlat_R = value.vignetteFlat_R,
                                            internalTubeUVS = value.internalTubeUVS,
                                            internalTubeUVS_R = value.internalTubeUVS_R,
                                            rScopeViewUVS = value.rScopeViewUVS,
                                            rScopeViewUVS_R = value.rScopeViewUVS_R,
                                            rReticleUVS = value.rReticleUVS,
                                            rReticleUVS_R = value.rReticleUVS_R,
                                            rVignetteUVS = value.rVignetteUVS,
                                            rVignetteUVS_R = value.rVignetteUVS_R,
                                            rVignetteFlat = value.rVignetteFlat,
                                            rVignetteFlat_R = value.rVignetteFlat_R,
                                            scopeViewScale = value.scopeViewScale,
                                            scopeViewOffset = value.scopeViewOffset };
        }
        void UpdateLensRect() {
            if (optic == null || optic.lensRenderer == null)
                return;
            float radius = optic.ocularLensDiameter * Read<float>(optic, "ocularLensScale") * .5f;
            if (radius <= 0 || Single.IsNaN(radius) || Single.IsInfinity(radius))
                return;
            float minX = 1, minY = 1, maxX = 0, maxY = 0;
            for (int i = 0; i < 4; i++) {
                Vector3 point = optic.transform.position +
                                optic.transform.right * ((i & 1) == 0 ? -radius : radius) +
                                optic.transform.up * ((i & 2) == 0 ? -radius : radius);
                if (optic.cameraRelativeRenderer)
                    point = PIPScope_MainCamera.viewMatrix.MultiplyPoint3x4(point);
                Vector3 viewport = source.WorldToViewportPoint(point);
                if (viewport.z <= 0)
                    return;
                minX = Mathf.Min(minX, viewport.x);
                maxX = Mathf.Max(maxX, viewport.x);
                minY = Mathf.Min(minY, viewport.y);
                maxY = Mathf.Max(maxY, viewport.y);
            }
            minX = Mathf.Clamp01(minX);
            minY = Mathf.Clamp01(minY);
            maxX = Mathf.Clamp01(maxX);
            maxY = Mathf.Clamp01(maxY);
            if (maxX <= minX || maxY <= minY)
                return;
            lensRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            lensRectAvailable = true;
        }
        RenderTexture Copy(RenderTexture input, string name) {
            float scale = Mathf.Min(1, 2048f / Mathf.Max(input.width, input.height));
            var output = new RenderTexture(Mathf.Max(1, Mathf.RoundToInt(input.width * scale)),
                                           Mathf.Max(1, Mathf.RoundToInt(input.height * scale)), 0,
                                           RenderTextureFormat.ARGB32);
            output.name = name;
            owned.Add(output);
            if (!output.Create())
                throw new InvalidOperationException("Diagnostic copy texture creation failed");
            RenderTexture previous = RenderTexture.active;
            try {
                Graphics.Blit(input, output);
            } finally {
                RenderTexture.active = previous;
            }
            return output;
        }
        ImageEvidence SaveImage(RenderTexture input, string filename, Rect rect) {
            if (input == null || !input.IsCreated())
                return new ImageEvidence {
                    unavailable = "Original scope did not produce a created RT to copy"
                };
            int x = Mathf.FloorToInt(rect.xMin * input.width),
                y = Mathf.FloorToInt(rect.yMin * input.height);
            int width = Mathf.Clamp(Mathf.CeilToInt(rect.width * input.width), 1, input.width - x),
                height =
                    Mathf.Clamp(Mathf.CeilToInt(rect.height * input.height), 1, input.height - y);
            var pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
            owned.Add(pixels);
            RenderTexture previous = RenderTexture.active;
            try {
                RenderTexture.active = input;
                pixels.ReadPixels(new Rect(x, y, width, height), 0, 0);
                pixels.Apply();
            } finally {
                RenderTexture.active = previous;
            }
            File.WriteAllBytes(Path.Combine(directory, filename), pixels.EncodeToPNG());
            var image = new ImageEvidence { available = true, path = filename, width = width,
                                            height = height, minimum = 1 };
            Color32[] data = pixels.GetPixels32();
            image.pixels = data.Length;
            foreach (Color32 pixel in data) {
                float peak = Mathf.Max(pixel.r, Mathf.Max(pixel.g, pixel.b)) / 255f;
                image.minimum = Mathf.Min(image.minimum, peak);
                image.maximum = Mathf.Max(image.maximum, peak);
                image.mean += peak;
                if (peak > .04f)
                    image.nonBlackPixels++;
                if (pixel.r > 70 && pixel.r > pixel.g * 1.5f && pixel.r > pixel.b * 1.5f)
                    image.redPixels++;
                if (pixel.g > 70 && pixel.g > pixel.r * 1.5f && pixel.g > pixel.b * 1.5f)
                    image.greenPixels++;
                if (pixel.b > 70 && pixel.b > pixel.r * 1.5f && pixel.b > pixel.g * 1.5f)
                    image.bluePixels++;
            }
            image.mean /= data.Length;
            image.contrast = image.maximum - image.minimum;
            return image;
        }
        public void Dispose() {
            armed = false;
            RestoreOwnMeshes();
            if (callbacks) {
                Camera.onPreCull -= PreCull;
                Camera.onPreRender -= PreRender;
                Camera.onPostRender -= PostRender;
                callbacks = false;
            }
            report.sourceCallbacksObserved = preCull > 0 && preRender > 0 && postRender > 0;
            report.scopeCameraRendered = scopePre > 0 && scopePost > 0;
            report.sourceRenders = postRender;
            report.scopeRenders = scopePost;
            report.observations = rows.ToArray();
            report.finishedUtc = DateTime.UtcNow.ToString("o");
            // Scope cameras may be detached by the original camera-relative renderer.
            // Destroy the owned prefab first so original OnDisable reparents/releases them.
            bool cleaned = CleanupMountedFixture();
            try {
                if (spawned != null)
                    Object.DestroyImmediate(spawned);
            } catch (Exception error) {
                cleaned = false;
                report.error = (report.error ?? "") + "\nOwned prefab cleanup: " + error;
            }
            for (int i = owned.Count - 1; i >= 0; i--)
                try {
                    if (owned[i] != null)
                        Object.DestroyImmediate(owned[i]);
                } catch (Exception error) {
                    cleaned = false;
                    report.error = (report.error ?? "") + "\nOwned resource cleanup: " + error;
                }
            owned.Clear();
            report.cleanupComplete = cleaned;
            MountedFixtureState mountedState = MountedFixtures.sessions[this];
            mountedState.report.ownedGunDestroyed = mountedState.gunRoot == null;
            mountedState.report.error = report.error;
            mountedState.report.observations = mountedState.observations.ToArray();
            ExtraState extra = Diagnostics.sessions[this];
            extra.report.opticalObservations = extra.observations.ToArray();
            string json = JsonUtility.ToJson(report, true).TrimEnd();
            json = json.Substring(0, json.Length - 1) + ",\n" +
                   JsonUtility.ToJson(extra.report, true).Substring(1);
            Diagnostics.sessions.Remove(this);
            CameraState cameraState = CameraDiagnostics.sessions[this];
            var cameraReport = new CameraReport {
                cameraObservations = cameraState.observations.ToArray(),
                originalScopeTargetInstanceIDs = new List<int>(cameraState.scopeTargets).ToArray(),
                originalSourceTargetInstanceIDs = new List<int>(cameraState.sourceTargets).ToArray()
            };
            json = json.TrimEnd();
            json = json.Substring(0, json.Length - 1) + ",\n" +
                   JsonUtility.ToJson(cameraReport, true).Substring(1);
            CameraDiagnostics.sessions.Remove(this);
            EyeSelection.rightEye.Remove(this);
            MeshExperimentState meshExperiment = MeshExperiment.sessions[this];
            json = json.TrimEnd();
            json = json.Substring(0, json.Length - 1) + ",\n" +
                   JsonUtility.ToJson(meshExperiment.report, true).Substring(1);
            MeshExperiment.sessions.Remove(this);
            json = json.TrimEnd();
            json = json.Substring(0, json.Length - 1) + ",\n" +
                   JsonUtility
                       .ToJson(new MountedReportEnvelope { mountedFixture = mountedState.report },
                               true)
                       .Substring(1);
            MountedFixtures.sessions.Remove(this);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "scope-probe.json"), json + "\n");
            if (report.error == null)
                Debug.Log("NEWENGINEH3VR_SCOPE_PROBE_STOP status=complete report=" + directory);
            else
                Debug.LogError("NEWENGINEH3VR_SCOPE_PROBE_STOP status=failed error=" +
                               report.error + " report=" + directory);
        }
        bool CleanupMountedFixture() {
            MountedFixtureState state = MountedFixtures.sessions[this];
            if (!state.report.requested)
                return true;
            try {
                ObserveMounted("before-cleanup");
                if (state.attachment != null && state.attachment.curMount != null) {
                    if (state.gunRoot == null || state.gun == null || state.mount == null ||
                        state.attachment.curMount != state.mount ||
                        state.mount.Parent != state.gun || state.mount.MyObject != state.gun)
                        throw new InvalidOperationException(
                            "Mounted cleanup refuses an unexpected non-owned mount");
                    state.attachment.DetachFromMount();
                }
                state.report.detachedForCleanup =
                    state.attachment == null || state.attachment.curMount == null;
                state.report.deregisteredForCleanup =
                    (state.mount == null || state.attachment == null ||
                     !state.mount.AttachmentsList.Contains(state.attachment)) &&
                    (state.gun == null || state.attachment == null ||
                     !state.gun.Attachments.Contains(state.attachment));
                if (!state.report.detachedForCleanup || !state.report.deregisteredForCleanup)
                    throw new InvalidOperationException(
                        "Original attachment detach did not clear owned registrations");
                return true;
            } catch (Exception error) {
                state.report.cleanupError = error.ToString();
                report.error = (report.error ?? "") + "\nOwned mount cleanup: " + error;
                return false;
            }
        }
    }
    static T Read<T>(object instance, string name) {
        FieldInfo field = instance.GetType().GetField(name, Fields);
        if (field == null || field.FieldType != typeof(T))
            throw new MissingFieldException(instance.GetType().FullName, name);
        return (T)field.GetValue(instance);
    }
}
#endif
