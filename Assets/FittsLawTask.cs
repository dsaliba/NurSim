using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.OpenXR.Input;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;
using RosMessageTypes.Geometry;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Fitts' Law Circular Task Controller  (extends Goal)
/// =====================================================
/// Attach to a default Unity Plane (10×10 units local XZ, Y-up).
///
/// • Dynamically generates and applies the Fitts' Law layout texture.
///   Updates live in the Editor on every inspector change.
///
/// • Targets are 1-indexed (T1…Tn) per ISO 9241-9 / 9241-411.
///   Visit sequence alternates opposite sides:
///     T1 → T(1+n/2) → T2 → T(2+n/2) → … (star pattern).
///
/// • Extends Goal: CheckIfObjectReachedGoal() returns true once the
///   entire sequence is complete.
///
/// • Vibrates the right-hand XR controller on each successful hit, using
///   OpenXR's action-based haptics API first (required for HTC Vive OpenXR
///   controller profiles — see the Haptics region below for why), with two
///   fallback paths for other hardware/backends.
///
/// • Optionally shows a floating dot above the active target.
///
/// • Publishes live data to ROS via ROS-TCP-Connector as hierarchical,
///   primitive-typed topics (no JSON) so rosbag/rqt_plot/CSV export work
///   without any parsing on the consumer side:
///     /fitts/layout_stats/*    — Int32 / Float32 / Int32MultiArray, latched,
///                                 published exactly once per run, at the
///                                 moment the pointer first settles on T1
///                                 (this is the task's canonical t=0 — NOT
///                                 at ResetTask/Activate time).
///     /fitts/active_target/*   — Int32, latched. Published at task start
///                                 (already describing T2) and after every
///                                 completed movement. At task end, label
///                                 and visit_step are explicitly set to -1
///                                 as a terminal sentinel so the last real
///                                 target's value doesn't appear to persist
///                                 forever when inspecting/replaying the bag.
///     /fitts/movement/*        — Int32 / Float32 / geometry_msgs/Point,
///                                 one synchronized burst per completed
///                                 movement (T1→T2, T2→T3, ...).
///     /fitts/pointer/*         — geometry_msgs/Point, published at every
///                                 trajectory sample interval throughout the
///                                 task (position_plane and position_3d).
///                                 Used to reconstruct per-movement paths in
///                                 post-processing by slicing on
///                                 [settle_time - duration, settle_time].
///     /fitts/task_complete/*   — Int32 / Float32, final run summary,
///                                 published exactly once at the end.
///   See fitts_ros_data_format.md for the full topic/field reference and
///   guidance on reconstructing per-trial tables from recorded bags.
///
/// • Scene-manager integration (managedBySceneManager = true):
///     - GameObject starts disabled at runtime.
///     - Call Activate() (matching TrialGoal API) to enable it and begin the task.
///     - onComplete fires when all targets are hit; the GameObject is then disabled again.
///     - Wire this into SequentialGoalTrial exactly like any other TrialGoal.
///
/// Dependencies:
///   • Unity ROS-TCP-Connector package          (assumed always present — no compile guard)
///   • Unity Input System package                (required by OpenXR Plugin anyway)
///   • Unity OpenXR Plugin (com.unity.xr.openxr)  (for action-based haptics)
///   • HTC Vive OpenXR feature group              (or any other OpenXR runtime/controller)
///
/// IMPORTANT PROJECT-SETTINGS CHECKLIST if haptics still don't buzz:
///   1. Project Settings → Player → Active Input Handling must include the
///      new Input System ("Input System Package (New)" or "Both"). The
///      OpenXR action-based haptics path depends on it.
///   2. Project Settings → XR Plug-in Management → OpenXR → make sure the
///      correct Vive controller interaction profile feature is enabled for
///      your actual hardware (e.g. the Vive Controller / Focus 3 / Cosmos
///      profile under the Vive OpenXR feature group). If no interaction
///      profile is enabled, the controller won't bind at all — buttons,
///      tracking, AND haptics will all silently fail to resolve.
///   3. Confirm OpenXR is the active provider for your build target under
///      XR Plug-in Management (not just installed in the package list).
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(MeshRenderer))]
public class FittsLawTask : Goal
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Layout parameters
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Layout Parameters  (ISO 9241-9 / 9241-411)")]
    [Tooltip("Number of targets placed around the ring. Odd numbers give identical amplitude for every movement.")]
    [Min(3)]
    public int numTargets = 9;

    [Tooltip("Diameter of each target circle in image pixels.")]
    [Min(4f)]
    public float targetWidthPx = 80f;

    [Tooltip("Radius of the circular layout in image pixels.")]
    [Min(10f)]
    public float radiusPx = 350f;

    [Tooltip("Image side-length in pixels (square).")]
    [Min(64)]
    public int imageSizePx = 1024;

    // ─────────────────────────────────────────────────────────────────────────
    //  Texture appearance
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Texture Appearance")]
    public Color texBackgroundColour = new Color(0.10f, 0.10f, 0.15f, 1f);
    public Color texTargetColour     = new Color(0.30f, 0.55f, 1.00f, 1f);
    public Color texActiveColour     = new Color(0.10f, 1.00f, 0.40f, 1f);
    public Color texDoneColour       = new Color(0.38f, 0.38f, 0.42f, 1f);
    public Color texRingColour       = new Color(0.22f, 0.22f, 0.30f, 1f);
    [Tooltip("Draw a faint guide ring connecting target centres.")]
    public bool drawGuideRing = true;

    // ─────────────────────────────────────────────────────────────────────────
    //  Pointer & dwell
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Pointer")]
    [Tooltip("Transform tracked as the user's pointer.")]
    public Transform pointer;

    [Tooltip("Seconds the pointer must remain inside a target to register a hit.")]
    [Min(0.01f)]
    public float dwellTime = 0.5f;

    // ─────────────────────────────────────────────────────────────────────────
    //  Dot indicator
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Active-Target Dot Indicator")]
    [Tooltip("Show a small sphere floating above the currently active target.")]
    public bool showDotIndicator = true;

    public Color dotColour = new Color(1f, 0.95f, 0.15f, 1f);

    [Tooltip("Dot size as a fraction of the target radius.")]
    [Range(0.05f, 0.5f)]
    public float dotRadiusFraction = 0.18f;

    [Tooltip("Height above the plane surface (world units).")]
    public float dotHeightOffset = 0.04f;

    // ─────────────────────────────────────────────────────────────────────────
    //  Haptics
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Haptics  (right-hand XR controller — Vive OpenXR / any OpenXR runtime)")]
    [Range(0f, 1f)]
    public float hapticAmplitude = 0.6f;

    [Min(0f)]
    public float hapticDuration = 0.12f;

    [Tooltip("Which hand's controller should vibrate on a hit.")]
    public UnityEngine.XR.InputDeviceCharacteristics hapticHandCharacteristic =
        UnityEngine.XR.InputDeviceCharacteristics.Right;

    // ─────────────────────────────────────────────────────────────────────────
    //  Audio feedback
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Audio Feedback")]
    [Tooltip("Clip played each time the pointer settles on a target zone.")]
    [SerializeField] private AudioClip _hitSound;

    [Range(0f, 1f)]
    [SerializeField] private float _hitSoundVolume = 1f;

    // ─────────────────────────────────────────────────────────────────────────
    //  ROS topics  (each is a BASE path; actual leaves are "{base}/{field}")
    // ─────────────────────────────────────────────────────────────────────────

    [Header("ROS Publishing  (hierarchical primitive topics, no JSON)")]
    public string rosTopicLayoutStats  = "/fitts/layout_stats";
    public string rosTopicActiveTarget = "/fitts/active_target";
    public string rosTopicMovement     = "/fitts/movement";
    public string rosTopicPointer      = "/fitts/pointer";
    public string rosTopicTaskComplete = "/fitts/task_complete";

    // ─────────────────────────────────────────────────────────────────────────
    //  Scene-manager integration  (SequentialGoalTrial / TrialGoal API)
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Scene Manager Integration")]
    [Tooltip("When enabled: GameObject starts disabled at runtime. " +
             "Call Activate() to show it and begin the task. " +
             "onComplete fires and the GameObject is disabled again when all targets are hit.")]
    public bool managedBySceneManager = true;

    [Tooltip("Optional message shown by the scene manager as a hint while this goal is active.")]
    public string contextMessage = "Complete the Fitts' Law circular task.";

    // ─────────────────────────────────────────────────────────────────────────
    //  Training Sheet
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Training Sheet")]
    [Tooltip("When enabled the task loops indefinitely instead of completing when all targets are visited. " +
             "A 'Finish Training' button is added to the HTTP dashboard to manually complete the goal.")]
    public bool isTrainingSheet = false;

    [Tooltip("Label shown on the Finish Training button in the HTTP dashboard.")]
    public string finishTrainingButtonLabel = "Finish Training";

    /// <summary>
    /// Fired when the entire Fitts' Law sequence is completed.
    /// Wire this into SequentialGoalTrial.OnGoalCompleted exactly like any TrialGoal.
    /// </summary>
    public event Action onComplete;

    // ─────────────────────────────────────────────────────────────────────────
    //  Gizmo visualisation
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Gizmo Visualisation")]
    public bool  showTargetGizmos        = true;
    public bool  debugTrajectories       = true;
    [Min(0.01f)]
    public float trajectorySampleInterval = 0.02f;
    public Color targetIdleColour        = new Color(0.3f, 0.6f, 1.0f, 0.35f);
    public Color targetActiveColour      = new Color(0.1f, 1.0f, 0.3f, 0.55f);
    public Color targetDoneColour        = new Color(0.6f, 0.6f, 0.6f, 0.20f);
    public Color trajectoryColour        = new Color(1.0f, 0.5f, 0.0f, 0.80f);

    // ─────────────────────────────────────────────────────────────────────────
    //  Physical sheet print command  (auto-generated, edit-mode read-only)
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Physical Sheet — Print Command")]
    [Tooltip("Run this in your terminal to generate a matching 1:1 PDF for the Vive Ultimate Tracker setup.\n\n"
           + "Sheet size is derived from the Plane's world-space lossy scale (accounts for parent transforms):\n"
           + "  sheet_mm = lossyScale.x * 10 * 1000  (Unity Plane is 10 local units)\n\n"
           + "Copy the command and run: pip install reportlab numpy pyyaml (one-time).")]
    [SerializeField]
    public string _printSheetCommand = "";

    // ─────────────────────────────────────────────────────────────────────────
    //  Read-only inspector info
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Runtime Info  (read-only)")]
    [SerializeField] private int   _currentTargetLabel = 1;   // 1-indexed display
    [SerializeField] private float _dwellProgress      = 0f;
    [SerializeField] private bool  _taskComplete       = false;
    [SerializeField] private bool  _waitingForStartDisplay = true; // mirrors _waitingForStart
    [SerializeField] private int   _movementsCompleted = 0;
    [SerializeField] private float _fittsID            = 0f;  // Shannon ID for this layout

    // ─────────────────────────────────────────────────────────────────────────
    //  Internal state
    // ─────────────────────────────────────────────────────────────────────────

    // World-space target centres
    private Vector3[] _targetPositions3D;
    // Normalised plane coordinates of each target (-0.5..0.5)
    private Vector2[] _targetPositionsPlane;
    // 1-indexed visit order (e.g. [1,5,2,6,3,7,4,8] for n=8)
    private int[]     _visitOrder;
    // Index into _visitOrder
    private int _visitStep = 0;
    private float _dwellAccum = 0f;

    // True until the user first settles on T1; timing only begins after that hit.
    private bool _waitingForStart = true;

    // Prevents registering the Finish Training button more than once.
    private bool _finishTrainingButtonRegistered = false;

    // Dot indicator GameObject
    private GameObject _dotIndicator;

    // Pre-baked textures — one per visit step, generated at task start.
    // Swapping mainTexture is free; rebuilding a 1024² texture every hit is not.
    private Texture2D[] _cachedTextures;
    private Texture2D   _editorPreviewTexture; // edit-mode only

    // Serialized pre-baked textures saved to disk by the editor button.
    // When populated, runtime skips generation entirely.
    [SerializeField] public Texture2D[] _prebakedTextures;

    // Haptic stop time — avoids coroutine race condition
    private float _hapticStopTime = -1f;

    private ROSConnection _ros;

    // ── Per-movement data ────────────────────────────────────────────────────
    private struct MovementRecord
    {
        public int    fromLabel;          // 1-indexed source target (always valid; first hit is not recorded)
        public int    toLabel;
        public float  durationSeconds;
        public float  amplitudePx;        // chord distance in image pixels
        public float  amplitude3D;        // world units
        public float  fittsID;            // Shannon ID for this movement
        public Vector3 settlePosition3D;
        public Vector2 settlePositionPlane;
        public List<Vector3> trajectory3D;
        public List<Vector2> trajectoryPlane;
        public List<float>   trajectoryTimes;
    }

    private List<MovementRecord> _records = new List<MovementRecord>();

    private float         _movementStartTime;
    private List<Vector3> _currentTraj3D    = new List<Vector3>();
    private List<Vector2> _currentTrajPlane = new List<Vector2>();
    private List<float>   _currentTrajTimes = new List<float>();
    private float         _lastSampleTime;

    // ─────────────────────────────────────────────────────────────────────────
    //  Unity lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        RebuildLayout();
        if (Application.isPlaying)
            BakeAllTextures();
        else
            RegenerateEditorPreview();
    }

    private void OnValidate()
    {
        RebuildLayout();
        RegenerateEditorPreview();
        UpdateDotIndicator();
        UpdatePrintCommand();
    }

    private void Start()
    {
        if (!Application.isPlaying) return;

        if (RecordingManager.Instance != null)
        {
            RecordingManager.Instance.SetExperimentName("fitts");
            RecordingManager.Instance.RegisterCondition("camera", "Camera", new[] { "Front", "Back", "Side" });
            RecordingManager.Instance.RegisterCondition("interface", "Interface", new[] { "hand", "joystick", "pose" });
        }

        RebuildLayout();
        BakeAllTextures();
        InitROS();
        EnsureDotIndicator();
        InitHapticAction();

        if (!managedBySceneManager)
        {
            ResetTask();
            RegisterFinishTrainingButton();
        }
    }

    private void Update()
    {
        if (!Application.isPlaying) return;

        // ── Haptic stop timer ────────────────────────────────────────────────
        if (_hapticStopTime > 0f && Time.time >= _hapticStopTime)
        {
            _hapticStopTime = -1f;
            StopHaptics();
        }

        if (_taskComplete) return;
        if (pointer == null) return;
        if (_targetPositions3D == null || _targetPositions3D.Length == 0) return;

        // ── Sample trajectory & stream pointer position to ROS ───────────────
        if (Time.time - _lastSampleTime >= trajectorySampleInterval)
        {
            _lastSampleTime = Time.time;
            Vector3 p3 = pointer.position;
            Vector2 p2 = WorldToPlane(p3);
            _currentTraj3D.Add(p3);
            _currentTrajPlane.Add(p2);
            _currentTrajTimes.Add(Time.time - _movementStartTime);

            // Publish live pointer position so bagpipe records a trajectory CSV.
            // Published from the moment the task becomes active (T1 dwell onwards)
            // so the full path of every movement is captured.
            ROSPublishPointerPosition(p3, p2);
        }

        // ── Dwell detection ──────────────────────────────────────────────────
        int     activeSlot     = _visitOrder[_visitStep];
        Vector3 targetPos      = _targetPositions3D[activeSlot];
        float   targetRadius3D = TargetRadius3D();

        Vector3 pointerLocal = transform.InverseTransformPoint(pointer.position);
        Vector3 targetLocal  = transform.InverseTransformPoint(targetPos);
        float   dist         = Vector2.Distance(
            new Vector2(pointerLocal.x, pointerLocal.z),
            new Vector2(targetLocal.x,  targetLocal.z));

        if (dist <= targetRadius3D)
        {
            _dwellAccum   += Time.deltaTime;
            _dwellProgress = Mathf.Clamp01(_dwellAccum / dwellTime);

            if (_dwellAccum >= dwellTime)
                RegisterHit(activeSlot);
        }
        else
        {
            _dwellAccum    = 0f;
            _dwellProgress = 0f;
        }

        UpdateDotIndicator();
    }

    private void OnDestroy()
    {
        StopHaptics();
        DisposeHapticAction();
        DestroyCachedTextures();
        DestroyGeneratedTexture();
        if (_dotIndicator != null)
            DestroyImmediate(_dotIndicator);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Goal override
    // ─────────────────────────────────────────────────────────────────────────

    public new bool CheckIfObjectReachedGoal(GameObject obj) => _taskComplete;

    // ─────────────────────────────────────────────────────────────────────────
    //  Task logic
    // ─────────────────────────────────────────────────────────────────────────

    private void ResetTask()
    {
        _visitStep          = 0;
        _movementsCompleted = 0;
        _currentTargetLabel = (_visitOrder != null && _visitOrder.Length > 0 && _slotLabel != null)
            ? _slotLabel[_visitOrder[0]] : 1;
        _taskComplete       = false;
        _waitingForStart    = true;
        _waitingForStartDisplay = true;
        _dwellAccum         = 0f;
        _dwellProgress      = 0f;
        _records.Clear();
        StartNewMovement();
        ApplyTextureForStep(0);
    }

    private void StartNewMovement()
    {
        _movementStartTime = Time.time;
        _lastSampleTime    = Time.time;
        _currentTraj3D.Clear();
        _currentTrajPlane.Clear();
        _currentTrajTimes.Clear();
        _dwellAccum    = 0f;
        _dwellProgress = 0f;
    }

    private void RegisterHit(int hitSlot)
    {
        // ── First hit: T1 settled — begin timing, don't record a movement ────
        if (_waitingForStart)
        {
            _waitingForStart        = false;
            _waitingForStartDisplay = false;

            ROSPublishLayoutStats();

            TriggerHaptics();
            PlayHitSound();

            _visitStep++;
            if (_visitStep >= _visitOrder.Length)
            {
                if (isTrainingSheet) { LoopTrainingSheet(); return; }
                _taskComplete = true;
                ApplyTextureForStep(_visitStep);
                UpdateDotIndicator();
                ROSPublishActiveTargetCleared();
                ROSPublishTaskComplete();
                onComplete?.Invoke();
                return;
            }

            _currentTargetLabel = _slotLabel[_visitOrder[_visitStep]];
            StartNewMovement();
            ApplyTextureForStep(_visitStep);
            UpdateDotIndicator();
            ROSPublishActiveTarget();
            return;
        }

        // ── All subsequent hits: record the completed movement ───────────────
        int   fromSlot = _visitOrder[_visitStep - 1];
        float duration = Time.time - _movementStartTime;

        Vector3 settle3D    = pointer.position;
        Vector2 settlePlane = WorldToPlane(settle3D);

        float amp3D = Vector3.Distance(_targetPositions3D[fromSlot], _targetPositions3D[hitSlot]);
        float ampPx = Vector2.Distance(ImageSpacePosition(fromSlot),  ImageSpacePosition(hitSlot));
        float movID = targetWidthPx > 0f ? Mathf.Log(ampPx / targetWidthPx + 1f, 2f) : 0f;

        var rec = new MovementRecord
        {
            fromLabel           = _slotLabel[fromSlot],
            toLabel             = _slotLabel[hitSlot],
            durationSeconds     = duration,
            amplitudePx         = ampPx,
            amplitude3D         = amp3D,
            fittsID             = movID,
            settlePosition3D    = settle3D,
            settlePositionPlane = settlePlane,
            trajectory3D        = new List<Vector3>(_currentTraj3D),
            trajectoryPlane     = new List<Vector2>(_currentTrajPlane),
            trajectoryTimes     = new List<float>(_currentTrajTimes),
        };
        _records.Add(rec);
        _movementsCompleted++;

        TriggerHaptics();
        PlayHitSound();
        ROSPublishMovement(rec);

        _visitStep++;

        if (_visitStep >= _visitOrder.Length)
        {
            if (isTrainingSheet) { LoopTrainingSheet(); return; }
            _taskComplete  = true;
            _dwellProgress = 0f;
            PrintReport();
            ApplyTextureForStep(_visitStep);
            UpdateDotIndicator();
            ROSPublishActiveTargetCleared();
            ROSPublishTaskComplete();
            onComplete?.Invoke();
            return;
        }

        _currentTargetLabel = _slotLabel[_visitOrder[_visitStep]];
        StartNewMovement();
        ApplyTextureForStep(_visitStep);
        UpdateDotIndicator();
        ROSPublishActiveTarget();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Training Sheet helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called instead of completing when isTrainingSheet is true and the full
    /// sequence has been visited. Resets back to T1 so the researcher can keep
    /// practicing without stopping the recording.
    /// </summary>
    private void LoopTrainingSheet()
    {
        _visitStep          = 0;
        _movementsCompleted = 0;
        _waitingForStart    = true;
        _waitingForStartDisplay = true;
        _currentTargetLabel = (_visitOrder != null && _visitOrder.Length > 0 && _slotLabel != null)
            ? _slotLabel[_visitOrder[0]] : 1;
        _records.Clear();
        _dwellAccum    = 0f;
        _dwellProgress = 0f;
        StartNewMovement();
        ApplyTextureForStep(0);
        UpdateDotIndicator();
    }

    /// <summary>
    /// Manually completes the training sheet goal — equivalent to what would
    /// have happened if the task ran in non-training mode. Wired to the
    /// "Finish Training" dashboard button.
    /// </summary>
    public void CompleteTraining()
    {
        if (!isTrainingSheet || _taskComplete) return;
        _taskComplete  = true;
        _dwellProgress = 0f;
        PrintReport();
        ApplyTextureForStep(_visitOrder != null ? _visitOrder.Length : 0);
        UpdateDotIndicator();
        ROSPublishActiveTargetCleared();
        ROSPublishTaskComplete();
        onComplete?.Invoke();
        if (managedBySceneManager)
            gameObject.SetActive(false);
    }

    /// <summary>
    /// Registers the "Finish Training" button with HTTPDash exactly once.
    /// Safe to call from both Start() and Activate().
    /// </summary>
    private void RegisterFinishTrainingButton()
    {
        if (!isTrainingSheet || _finishTrainingButtonRegistered) return;
        if (HTTPDash.Instance == null) return;
        _finishTrainingButtonRegistered = true;
        HTTPDash.Instance.RegisterButton("Finish Training", finishTrainingButtonLabel, _ => CompleteTraining());
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Layout calculation
    // ─────────────────────────────────────────────────────────────────────────

    private void RebuildLayout()
    {
        if (numTargets < 3) return;

        _targetPositions3D    = new Vector3[numTargets];
        _targetPositionsPlane = new Vector2[numTargets];

        for (int i = 0; i < numTargets; i++)
        {
            _targetPositionsPlane[i] = ImageToPlaneNorm(ImageSpacePosition(i));
            Vector3 worldPos = PlaneNormToWorld(_targetPositionsPlane[i]);
            Vector3 centre3D = transform.position;
            Vector3 right    = transform.right;
            float   dot      = Vector3.Dot(worldPos - centre3D, right);
            _targetPositions3D[i] = worldPos - 2f * dot * right;
        }

        _visitOrder = BuildVisitOrder(numTargets);

        _slotLabel = new int[numTargets];
        for (int step = 0; step < _visitOrder.Length; step++)
            _slotLabel[_visitOrder[step]] = step + 1;

        if (_visitOrder.Length > 0)
            _currentTargetLabel = _slotLabel[_visitOrder[0]];

        int   halfSteps = numTargets / 2;
        float theta     = 2f * Mathf.PI * halfSteps / numTargets;
        float chordPx   = 2f * radiusPx * Mathf.Sin(theta * 0.5f);
        _fittsID = (targetWidthPx > 0f)
            ? Mathf.Log(chordPx / targetWidthPx + 1f, 2f)
            : 0f;
    }

    private int[] _slotLabel;

    private Vector2 ImageSpacePosition(int zeroBasedIndex)
    {
        float angle = -Mathf.PI / 2f + 2f * Mathf.PI * zeroBasedIndex / numTargets;
        return new Vector2(
            radiusPx * Mathf.Cos(angle),
            radiusPx * Mathf.Sin(angle)
        );
    }

    private Vector2 ImageToPlaneNorm(Vector2 imgPos)
    {
        return new Vector2(
             imgPos.x / imageSizePx,
            -imgPos.y / imageSizePx
        );
    }

    private Vector3 PlaneNormToWorld(Vector2 normPos)
    {
        return transform.TransformPoint(new Vector3(normPos.x * 10f, 0f, -normPos.y * 10f));
    }

    private Vector2 WorldToPlane(Vector3 worldPos)
    {
        Vector3 local = transform.InverseTransformPoint(worldPos);
        return new Vector2(local.x / 10f, -local.z / 10f);
    }

    private float TargetRadius3D()
    {
        return (targetWidthPx * 0.5f / imageSizePx) * 10f;
    }

    private static int[] BuildVisitOrder(int n)
    {
        int half = n / 2;
        var order = new List<int>();
        for (int i = 0; i < half; i++)
        {
            order.Add(i);
            order.Add(i + half);
        }
        if (n % 2 == 1)
            order.Add(n - 1);
        return order.ToArray();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Physical sheet print command
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdatePrintCommand()
    {
        float lossyW   = Mathf.Abs(transform.lossyScale.x);
        float lossyH   = Mathf.Abs(transform.lossyScale.z);
        float sheetWmm = lossyW * 10f * 1000f;
        float sheetHmm = lossyH * 10f * 1000f;

        string sizeArg = Mathf.Abs(sheetWmm - sheetHmm) < 0.5f
            ? $"--unity-plane-scale {lossyW:G6}"
            : $"--sheet-width-mm {sheetWmm:F1} --sheet-height-mm {sheetHmm:F1}";

        _printSheetCommand =
            $"python fitts_pdf_generator.py {sizeArg} --num-targets {numTargets} --target-width-px {targetWidthPx:G6} --radius-px {radiusPx:G6} --image-size-px {imageSizePx} --output fitts_sheet.pdf";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ROS publishing
    // ─────────────────────────────────────────────────────────────────────────

    private static string T(string baseTopic, string suffix) => baseTopic.TrimEnd('/') + "/" + suffix;

    private void InitROS()
    {
        _ros = ROSConnection.GetOrCreateInstance();

        // Latched static topics
        _ros.RegisterPublisher<Int32Msg>(T(rosTopicLayoutStats, "num_targets"), latch: true);
        _ros.RegisterPublisher<Float32Msg>(T(rosTopicLayoutStats, "target_width_px"), latch: true);
        _ros.RegisterPublisher<Float32Msg>(T(rosTopicLayoutStats, "radius_px"), latch: true);
        _ros.RegisterPublisher<Float32Msg>(T(rosTopicLayoutStats, "layout_diameter_px"), latch: true);
        _ros.RegisterPublisher<Float32Msg>(T(rosTopicLayoutStats, "amplitude_px"), latch: true);
        _ros.RegisterPublisher<Float32Msg>(T(rosTopicLayoutStats, "fitts_id"), latch: true);
        _ros.RegisterPublisher<Int32MultiArrayMsg>(T(rosTopicLayoutStats, "visit_sequence"), latch: true);

        _ros.RegisterPublisher<Int32Msg>(T(rosTopicActiveTarget, "label"), latch: true);
        _ros.RegisterPublisher<Int32Msg>(T(rosTopicActiveTarget, "visit_step"), latch: true);
        _ros.RegisterPublisher<Int32Msg>(T(rosTopicActiveTarget, "total_steps"), latch: true);

        // Per-movement burst topics
        _ros.RegisterPublisher<Int32Msg>(T(rosTopicMovement, "index"));
        _ros.RegisterPublisher<Int32Msg>(T(rosTopicMovement, "from_label"));
        _ros.RegisterPublisher<Int32Msg>(T(rosTopicMovement, "to_label"));
        _ros.RegisterPublisher<Float32Msg>(T(rosTopicMovement, "duration_seconds"));
        _ros.RegisterPublisher<Float32Msg>(T(rosTopicMovement, "amplitude_px"));
        _ros.RegisterPublisher<Float32Msg>(T(rosTopicMovement, "amplitude_3d"));
        _ros.RegisterPublisher<Float32Msg>(T(rosTopicMovement, "fitts_id"));
        _ros.RegisterPublisher<PointMsg>(T(rosTopicMovement, "settle_position_3d"));
        _ros.RegisterPublisher<PointMsg>(T(rosTopicMovement, "settle_position_plane"));
        _ros.RegisterPublisher<Int32Msg>(T(rosTopicMovement, "trajectory_samples"));

        // Continuous pointer position stream — one message per trajectory sample.
        // Slice by [settle_time - duration, settle_time] in post-processing to
        // reconstruct per-movement paths.
        _ros.RegisterPublisher<PointMsg>(T(rosTopicPointer, "position_plane"));
        _ros.RegisterPublisher<PointMsg>(T(rosTopicPointer, "position_3d"));

        // Task summary
        _ros.RegisterPublisher<Int32Msg>(T(rosTopicTaskComplete, "total_movements"));
        _ros.RegisterPublisher<Float32Msg>(T(rosTopicTaskComplete, "total_time_seconds"));
        _ros.RegisterPublisher<Float32Msg>(T(rosTopicTaskComplete, "mean_movement_time_seconds"));
        _ros.RegisterPublisher<Float32Msg>(T(rosTopicTaskComplete, "mean_amplitude_px"));
        _ros.RegisterPublisher<Float32Msg>(T(rosTopicTaskComplete, "mean_fitts_id"));
        _ros.RegisterPublisher<Float32Msg>(T(rosTopicTaskComplete, "throughput_bps"));
        _ros.RegisterPublisher<Float32Msg>(T(rosTopicTaskComplete, "layout_fitts_id"));
    }

    private void ROSPublishPointerPosition(Vector3 pos3D, Vector2 posPlane)
    {
        if (_ros == null) return;
        _ros.Publish(T(rosTopicPointer, "position_plane"), new PointMsg
        {
            x = posPlane.x,
            y = posPlane.y,
            z = 0.0
        });
        _ros.Publish(T(rosTopicPointer, "position_3d"), new PointMsg
        {
            x = pos3D.x,
            y = pos3D.y,
            z = pos3D.z
        });
    }

    private void ROSPublishLayoutStats()
    {
        if (_ros == null) return;

        int   half    = numTargets / 2;
        float theta   = 2f * Mathf.PI * half / numTargets;
        float chordPx = 2f * radiusPx * Mathf.Sin(theta * 0.5f);

        int[] visitLabels = (_slotLabel != null && _visitOrder != null)
            ? Array.ConvertAll(_visitOrder, s => _slotLabel[s])
            : (_visitOrder ?? Array.Empty<int>());

        _ros.Publish(T(rosTopicLayoutStats, "num_targets"), new Int32Msg { data = numTargets });
        _ros.Publish(T(rosTopicLayoutStats, "target_width_px"), new Float32Msg { data = targetWidthPx });
        _ros.Publish(T(rosTopicLayoutStats, "radius_px"), new Float32Msg { data = radiusPx });
        _ros.Publish(T(rosTopicLayoutStats, "layout_diameter_px"), new Float32Msg { data = radiusPx * 2f });
        _ros.Publish(T(rosTopicLayoutStats, "amplitude_px"), new Float32Msg { data = chordPx });
        _ros.Publish(T(rosTopicLayoutStats, "fitts_id"), new Float32Msg { data = _fittsID });
        _ros.Publish(T(rosTopicLayoutStats, "visit_sequence"), new Int32MultiArrayMsg { data = visitLabels });

        Debug.Log($"[FittsLawTask] Layout stats published under {rosTopicLayoutStats}/*");
    }

    private void ROSPublishActiveTarget()
    {
        if (_ros == null) return;
        if (_taskComplete || _visitOrder == null || _slotLabel == null) return;

        int activeSlot = _visitOrder[_visitStep];
        int label      = _slotLabel[activeSlot];

        _ros.Publish(T(rosTopicActiveTarget, "label"), new Int32Msg { data = label });
        _ros.Publish(T(rosTopicActiveTarget, "visit_step"), new Int32Msg { data = _visitStep + 1 });
        _ros.Publish(T(rosTopicActiveTarget, "total_steps"), new Int32Msg { data = _visitOrder.Length });
    }

    private void ROSPublishActiveTargetCleared()
    {
        if (_ros == null) return;
        _ros.Publish(T(rosTopicActiveTarget, "label"), new Int32Msg { data = -1 });
        _ros.Publish(T(rosTopicActiveTarget, "visit_step"), new Int32Msg { data = -1 });
    }

    private void ROSPublishMovement(MovementRecord r)
    {
        if (_ros == null) return;

        _ros.Publish(T(rosTopicMovement, "index"), new Int32Msg { data = _movementsCompleted });
        _ros.Publish(T(rosTopicMovement, "from_label"), new Int32Msg { data = r.fromLabel });
        _ros.Publish(T(rosTopicMovement, "to_label"), new Int32Msg { data = r.toLabel });
        _ros.Publish(T(rosTopicMovement, "duration_seconds"), new Float32Msg { data = r.durationSeconds });
        _ros.Publish(T(rosTopicMovement, "amplitude_px"), new Float32Msg { data = r.amplitudePx });
        _ros.Publish(T(rosTopicMovement, "amplitude_3d"), new Float32Msg { data = r.amplitude3D });
        _ros.Publish(T(rosTopicMovement, "fitts_id"), new Float32Msg { data = r.fittsID });

        _ros.Publish(T(rosTopicMovement, "settle_position_3d"), new PointMsg
        {
            x = r.settlePosition3D.x,
            y = r.settlePosition3D.y,
            z = r.settlePosition3D.z
        });

        _ros.Publish(T(rosTopicMovement, "settle_position_plane"), new PointMsg
        {
            x = r.settlePositionPlane.x,
            y = r.settlePositionPlane.y,
            z = 0.0
        });

        _ros.Publish(T(rosTopicMovement, "trajectory_samples"), new Int32Msg { data = r.trajectory3D.Count });
    }

    private void ROSPublishTaskComplete()
    {
        if (_ros == null) return;

        double totalTime  = 0;
        double totalAmpPx = 0;
        double totalID    = 0;
        int    validMoves = 0;

        foreach (var r in _records)
        {
            totalTime  += r.durationSeconds;
            totalAmpPx += r.amplitudePx;
            totalID    += r.fittsID;
            validMoves++;
        }

        double meanMT  = validMoves > 0 ? totalTime  / validMoves : 0;
        double meanAmp = validMoves > 0 ? totalAmpPx / validMoves : 0;
        double meanID  = validMoves > 0 ? totalID    / validMoves : 0;

        double tp = 0; int tpCount = 0;
        foreach (var r in _records)
        {
            if (r.durationSeconds > 0f) { tp += r.fittsID / r.durationSeconds; tpCount++; }
        }
        double throughput = tpCount > 0 ? tp / tpCount : 0;

        _ros.Publish(T(rosTopicTaskComplete, "total_movements"), new Int32Msg { data = _records.Count });
        _ros.Publish(T(rosTopicTaskComplete, "total_time_seconds"), new Float32Msg { data = (float)totalTime });
        _ros.Publish(T(rosTopicTaskComplete, "mean_movement_time_seconds"), new Float32Msg { data = (float)meanMT });
        _ros.Publish(T(rosTopicTaskComplete, "mean_amplitude_px"), new Float32Msg { data = (float)meanAmp });
        _ros.Publish(T(rosTopicTaskComplete, "mean_fitts_id"), new Float32Msg { data = (float)meanID });
        _ros.Publish(T(rosTopicTaskComplete, "throughput_bps"), new Float32Msg { data = (float)throughput });
        _ros.Publish(T(rosTopicTaskComplete, "layout_fitts_id"), new Float32Msg { data = _fittsID });

        Debug.Log($"[FittsLawTask] Task complete published under {rosTopicTaskComplete}/*");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Haptics
    // ─────────────────────────────────────────────────────────────────────────

    private InputAction _hapticAction;
    private bool        _hapticActionInitialized = false;

    private UnityEngine.XR.InputDevice _legacyHapticDevice;
    private bool _legacyHapticDeviceCached = false;

    private void InitHapticAction()
    {
        if (_hapticActionInitialized) return;
        _hapticActionInitialized = true;

        bool isLeft = hapticHandCharacteristic.HasFlag(UnityEngine.XR.InputDeviceCharacteristics.Left);
        string hand = isLeft ? "LeftHand" : "RightHand";

        try
        {
            _hapticAction = new InputAction(
                name: "FittsLawHaptic",
                type: InputActionType.Value,
                binding: $"<XRController>{{{hand}}}/haptic");
            _hapticAction.Enable();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FittsLawTask] Could not create OpenXR haptic action: {e.Message}");
            _hapticAction = null;
        }
    }

    private void DisposeHapticAction()
    {
        if (_hapticAction == null) return;
        _hapticAction.Disable();
        _hapticAction.Dispose();
        _hapticAction = null;
    }

    private void TriggerHaptics()
    {
        bool sent = TryOpenXRHaptics();
        if (!sent) sent = TryInputSystemRumble();
        if (!sent) sent = TryLegacyXRHaptics();
        _hapticStopTime = sent ? Time.time + hapticDuration : -1f;

        if (!sent)
            Debug.LogWarning("[FittsLawTask] No haptic-capable controller found.");
    }

    private bool TryOpenXRHaptics()
    {
        if (_hapticAction == null) InitHapticAction();
        if (_hapticAction == null) return false;

        try
        {
            if (_hapticAction.controls.Count == 0) return false;
            OpenXRInput.SendHapticImpulse(_hapticAction, hapticAmplitude, hapticDuration);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FittsLawTask] OpenXR haptic impulse failed: {e.Message}");
            return false;
        }
    }

    private bool TryInputSystemRumble()
    {
        bool isLeft = hapticHandCharacteristic.HasFlag(UnityEngine.XR.InputDeviceCharacteristics.Left);
        var targetUsage = isLeft ? CommonUsages.LeftHand : CommonUsages.RightHand;

        foreach (var device in InputSystem.devices)
        {
            if (device is XRControllerWithRumble rumble && device.usages.Contains(targetUsage))
            {
                rumble.SendImpulse(hapticAmplitude, hapticDuration);
                return true;
            }
        }
        return false;
    }

    private bool TryLegacyXRHaptics()
    {
        if (!TryGetLegacyHapticDevice(out var device)) return false;
        device.SendHapticImpulse(0, hapticAmplitude, hapticDuration);
        return true;
    }

    private bool TryGetLegacyHapticDevice(out UnityEngine.XR.InputDevice device)
    {
        if (_legacyHapticDeviceCached && _legacyHapticDevice.isValid)
        {
            device = _legacyHapticDevice;
            return true;
        }

        var devices = new List<UnityEngine.XR.InputDevice>();
        UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(
            hapticHandCharacteristic | UnityEngine.XR.InputDeviceCharacteristics.Controller,
            devices);

        foreach (var dev in devices)
        {
            if (!dev.isValid) continue;
            if (dev.TryGetHapticCapabilities(out var caps) && caps.supportsImpulse)
            {
                _legacyHapticDevice       = dev;
                _legacyHapticDeviceCached = true;
                device                    = dev;
                return true;
            }
        }

        device = default;
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Audio feedback
    // ─────────────────────────────────────────────────────────────────────────

    private void PlayHitSound()
    {
        if (_hitSound == null) return;
        Vector3 pos = pointer != null ? pointer.position : transform.position;
        AudioSource.PlayClipAtPoint(_hitSound, pos, _hitSoundVolume);
    }

    private void StopHaptics()
    {
        if (_hapticAction != null)
        {
            try { OpenXRInput.StopHaptics(_hapticAction); }
            catch { }
        }

        if (_legacyHapticDeviceCached && _legacyHapticDevice.isValid)
            _legacyHapticDevice.StopHaptics();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Dot indicator
    // ─────────────────────────────────────────────────────────────────────────

    private void EnsureDotIndicator()
    {
        if (_dotIndicator != null) return;

        _dotIndicator      = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _dotIndicator.name = "FittsLaw_DotIndicator";

        var col = _dotIndicator.GetComponent<Collider>();
        if (col != null) Destroy(col);

        var mr = _dotIndicator.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            var mat   = new Material(Shader.Find("Unlit/Color") ?? Shader.Find("Standard"));
            mat.color = dotColour;
            mr.sharedMaterial = mat;
        }

        UpdateDotIndicator();
    }

    private void UpdateDotIndicator()
    {
        if (_dotIndicator == null) return;

        bool visible = showDotIndicator &&
                       Application.isPlaying &&
                       !_taskComplete &&
                       _targetPositions3D != null &&
                       _visitOrder        != null &&
                       _visitStep < _visitOrder.Length;

        _dotIndicator.SetActive(visible);
        if (!visible) return;

        float   radius     = TargetRadius3D() * Mathf.Abs(transform.lossyScale.x) * dotRadiusFraction;
        int     activeSlot = _visitOrder[_visitStep];
        Vector3 pos        = _targetPositions3D[activeSlot] + transform.up * dotHeightOffset;

        _dotIndicator.transform.position   = pos;
        _dotIndicator.transform.localScale = Vector3.one * radius * 2f;

        var mr = _dotIndicator.GetComponent<MeshRenderer>();
        if (mr?.sharedMaterial != null)
            mr.sharedMaterial.color = dotColour;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Texture system
    // ─────────────────────────────────────────────────────────────────────────

    private void BakeAllTextures()
    {
        if (numTargets < 3 || imageSizePx < 64 || _visitOrder == null) return;

        int totalSteps = _visitOrder.Length + 1;

        if (_prebakedTextures != null && _prebakedTextures.Length == totalSteps
            && _prebakedTextures[0] != null)
        {
            _cachedTextures = _prebakedTextures;
            ApplyTextureForStep(0);
            return;
        }

        DestroyCachedTextures();
        _cachedTextures = new Texture2D[totalSteps];
        for (int step = 0; step < totalSteps; step++)
            _cachedTextures[step] = BakeTexture(step);

        ApplyTextureForStep(0);
    }

    [ContextMenu("Bake Fitts Textures")]
    public void BakeAndSaveTextures()
    {
#if UNITY_EDITOR
        RebuildLayout();
        if (numTargets < 3 || imageSizePx < 64 || _visitOrder == null) return;

        string folder    = "Assets/FittsBaked";
        if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
            UnityEditor.AssetDatabase.CreateFolder("Assets", "FittsBaked");

        string assetName  = $"FittsLayout_{gameObject.name.Replace(" ", "_")}";
        int    totalSteps = _visitOrder.Length + 1;

        _prebakedTextures = new Texture2D[totalSteps];

        for (int step = 0; step < totalSteps; step++)
        {
            Texture2D tex  = BakeTexture(step);
            byte[]    png  = tex.EncodeToPNG();
            DestroyImmediate(tex);

            string path = $"{folder}/{assetName}_step{step}.png";
            System.IO.File.WriteAllBytes(path, png);
            UnityEditor.AssetDatabase.ImportAsset(path);

            var importer = UnityEditor.AssetImporter.GetAtPath(path) as UnityEditor.TextureImporter;
            if (importer != null)
            {
                importer.textureCompression = UnityEditor.TextureImporterCompression.Uncompressed;
                importer.isReadable         = false;
                importer.mipmapEnabled      = false;
                importer.npotScale          = UnityEditor.TextureImporterNPOTScale.None;
                importer.SaveAndReimport();
            }

            _prebakedTextures[step] = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        UnityEditor.EditorUtility.SetDirty(this);
        UnityEditor.AssetDatabase.SaveAssets();
        Debug.Log($"[FittsLawTask] Baked {totalSteps} textures → {folder}/{assetName}_step*.png");

        var mr = GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.sharedMaterial.mainTexture       = _prebakedTextures[0];
            mr.sharedMaterial.mainTextureScale  = new Vector2(1f, 1f);
            mr.sharedMaterial.mainTextureOffset = new Vector2(0f, 0f);
        }
#endif
    }

    private void ApplyTextureForStep(int step)
    {
        if (_cachedTextures == null) return;
        step = Mathf.Clamp(step, 0, _cachedTextures.Length - 1);

        var mr = GetComponent<MeshRenderer>();
        if (mr == null) return;

        Material mat = Application.isPlaying ? mr.material : mr.sharedMaterial;
        mat.mainTexture       = _cachedTextures[step];
        mat.mainTextureScale  = new Vector2(1f, 1f);
        mat.mainTextureOffset = new Vector2(0f, 0f);
    }

    private Texture2D BakeTexture(int step)
    {
        int size  = imageSizePx;
        var tex   = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.name  = $"FittsLayout_step{step}";

        Color32[] pixels = new Color32[size * size];
        Color32   bg     = texBackgroundColour;
        for (int i = 0; i < pixels.Length; i++) pixels[i] = bg;

        Vector2 centre = new Vector2(size * 0.5f, size * 0.5f);
        float   tRad   = targetWidthPx * 0.5f;

        if (drawGuideRing)
            DrawCircleOutline(pixels, size, centre, radiusPx + 1.5f, radiusPx - 1.5f, texRingColour);

        for (int slot = 0; slot < numTargets; slot++)
        {
            int     label  = (_slotLabel != null) ? _slotLabel[slot] : (slot + 1);
            Color32 col    = ColourForSlotAtStep(slot, step);
            Vector2 imgPos = ImageSpacePosition(slot);
            Vector2 pixPos = new Vector2(centre.x + imgPos.x, centre.y - imgPos.y);

            DrawFilledCircle(pixels, size, pixPos, tRad, col);
            DrawLabel(pixels, size, pixPos, label.ToString(), tRad);
        }

        tex.SetPixels32(pixels);
        tex.Apply();
        return tex;
    }

    private Color32 ColourForSlotAtStep(int slot, int step)
    {
        if (_visitOrder == null) return texTargetColour;
        if (step >= _visitOrder.Length) return texDoneColour;
        for (int s = 0; s < step; s++)
            if (_visitOrder[s] == slot) return texDoneColour;
        if (_visitOrder[step] == slot) return texActiveColour;
        return texTargetColour;
    }

    private void DestroyCachedTextures()
    {
        if (_cachedTextures == null) return;
        foreach (var t in _cachedTextures)
        {
            if (t == null) continue;
            if (Application.isPlaying) Destroy(t);
            else                       DestroyImmediate(t);
        }
        _cachedTextures = null;
    }

    private void RegenerateEditorPreview()
    {
        if (Application.isPlaying) return;
        if (numTargets < 3 || imageSizePx < 64) return;

        if (_editorPreviewTexture != null) DestroyImmediate(_editorPreviewTexture);
        _editorPreviewTexture = BakeTexture(0);

        var mr = GetComponent<MeshRenderer>();
        if (mr == null) return;
        mr.sharedMaterial.mainTexture       = _editorPreviewTexture;
        mr.sharedMaterial.mainTextureScale  = new Vector2(1f, 1f);
        mr.sharedMaterial.mainTextureOffset = new Vector2(0f, 0f);
    }

    private Color32 TargetColourForSlot(int slot)
    {
        if (!Application.isPlaying || _visitOrder == null) return texTargetColour;
        if (_taskComplete) return texDoneColour;
        for (int s = 0; s < _visitStep; s++)
            if (_visitOrder[s] == slot) return texDoneColour;
        if (_visitStep < _visitOrder.Length && _visitOrder[_visitStep] == slot)
            return texActiveColour;
        return texTargetColour;
    }

    private void DestroyGeneratedTexture()
    {
        if (_editorPreviewTexture == null) return;
        if (Application.isPlaying) Destroy(_editorPreviewTexture);
        else                       DestroyImmediate(_editorPreviewTexture);
        _editorPreviewTexture = null;
    }

    // ── Pixel-drawing helpers ────────────────────────────────────────────────

    private static void DrawFilledCircle(Color32[] pixels, int size,
                                          Vector2 centre, float radius, Color32 colour)
    {
        int x0 = Mathf.Max(0,    (int)(centre.x - radius - 1));
        int x1 = Mathf.Min(size, (int)(centre.x + radius + 2));
        int y0 = Mathf.Max(0,    (int)(centre.y - radius - 1));
        int y1 = Mathf.Min(size, (int)(centre.y + radius + 2));
        float r2 = radius * radius;

        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            float dx = x - centre.x, dy = y - centre.y;
            if (dx * dx + dy * dy <= r2)
                pixels[y * size + x] = colour;
        }
    }

    private static void DrawCircleOutline(Color32[] pixels, int size, Vector2 centre,
                                           float outerR, float innerR, Color32 colour)
    {
        int x0 = Mathf.Max(0,    (int)(centre.x - outerR - 1));
        int x1 = Mathf.Min(size, (int)(centre.x + outerR + 2));
        int y0 = Mathf.Max(0,    (int)(centre.y - outerR - 1));
        int y1 = Mathf.Min(size, (int)(centre.y + outerR + 2));
        float r2o = outerR * outerR, r2i = innerR * innerR;

        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            float dx = x - centre.x, dy = y - centre.y;
            float d2 = dx * dx + dy * dy;
            if (d2 <= r2o && d2 >= r2i)
                pixels[y * size + x] = colour;
        }
    }

    private static void DrawLabel(Color32[] pixels, int size,
                                   Vector2 centre, string text, float targetRadius)
    {
        if (string.IsNullOrEmpty(text)) return;

        int   numChars  = text.Length;
        float diameter  = targetRadius * 2f;

        const int GW = 5, GH = 7, GAP = 1;
        float cols = numChars * GW + (numChars - 1) * GAP;

        float scale = (diameter * 0.58f) / cols;
        if (scale < 0.5f) return;

        int SS = 4;

        float blockW = cols  * scale;
        float blockH = GH    * scale;
        float ox     = centre.x - blockW * 0.5f;
        float oy     = centre.y - blockH * 0.5f;

        int x0 = Mathf.Max(0,    (int)(centre.x - targetRadius));
        int x1 = Mathf.Min(size, (int)(centre.x + targetRadius) + 1);
        int y0 = Mathf.Max(0,    (int)(centre.y - targetRadius));
        int y1 = Mathf.Min(size, (int)(centre.y + targetRadius) + 1);

        float ssInv  = 1f / SS;
        float ssInv2 = ssInv * ssInv;
        float r2     = targetRadius * targetRadius;

        for (int py = y0; py < y1; py++)
        for (int px = x0; px < x1; px++)
        {
            float cdx = px - centre.x, cdy = py - centre.y;
            if (cdx * cdx + cdy * cdy > r2) continue;

            float coverage = 0f;
            for (int sy = 0; sy < SS; sy++)
            for (int sx = 0; sx < SS; sx++)
            {
                float fx = px + (sx + 0.5f) * ssInv;
                float fy = py + (sy + 0.5f) * ssInv;

                float bx = (fx - ox) / scale;
                float by = (fy - oy) / scale;

                if (bx < 0 || by < 0 || by >= GH) continue;

                float charSlot = bx / (GW + GAP);
                int   ci       = (int)charSlot;
                if (ci < 0 || ci >= numChars) continue;

                float colInChar = bx - ci * (GW + GAP);
                if (colInChar < 0 || colInChar >= GW) continue;

                int col = (int)colInChar;
                int row = (int)by;

                if (row < 0 || row >= GH || col < 0 || col >= GW) continue;

                int[] bmp = GetCharBitmap(text[ci]);
                if ((bmp[row] & (1 << (GW - 1 - col))) != 0)
                    coverage += ssInv2;
            }

            if (coverage <= 0f) continue;

            int     idx  = py * size + px;
            Color32 src  = pixels[idx];
            byte    a    = (byte)(coverage * 230f);
            float   af   = a / 255f;
            pixels[idx]  = new Color32(
                (byte)(src.r + (255 - src.r) * af),
                (byte)(src.g + (255 - src.g) * af),
                (byte)(src.b + (255 - src.b) * af),
                255
            );
        }
    }

    private static int[] GetCharBitmap(char c)
    {
        switch (c)
        {
            case '0': return new[]{0b01110,0b10001,0b10011,0b10101,0b11001,0b10001,0b01110};
            case '1': return new[]{0b00100,0b01100,0b00100,0b00100,0b00100,0b00100,0b01110};
            case '2': return new[]{0b01110,0b10001,0b00001,0b00010,0b00100,0b01000,0b11111};
            case '3': return new[]{0b01110,0b10001,0b00001,0b00110,0b00001,0b10001,0b01110};
            case '4': return new[]{0b00010,0b00110,0b01010,0b10010,0b11111,0b00010,0b00010};
            case '5': return new[]{0b11111,0b10000,0b11110,0b00001,0b00001,0b10001,0b01110};
            case '6': return new[]{0b00110,0b01000,0b10000,0b11110,0b10001,0b10001,0b01110};
            case '7': return new[]{0b11111,0b00001,0b00010,0b00100,0b01000,0b01000,0b01000};
            case '8': return new[]{0b01110,0b10001,0b10001,0b01110,0b10001,0b10001,0b01110};
            case '9': return new[]{0b01110,0b10001,0b10001,0b01111,0b00001,0b00010,0b01100};
            default : return new[]{0b00000,0b00100,0b00100,0b00000,0b00100,0b00100,0b00000};
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Report  (console)
    // ─────────────────────────────────────────────────────────────────────────

    private void PrintReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
        sb.AppendLine("║           FITTS' LAW CIRCULAR TASK  —  RESULTS              ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
        sb.AppendLine($"  Targets         : {numTargets}  (labelled T1 – T{numTargets})");
        sb.AppendLine($"  Target width    : {targetWidthPx} px  /  {TargetRadius3D()*2f:F4} world-units diam");
        sb.AppendLine($"  Layout radius   : {radiusPx} px");
        sb.AppendLine($"  Layout Fitts ID : {_fittsID:F3} bits  (Shannon: log₂(A/W + 1))");
        sb.AppendLine($"  Visit sequence  : [{string.Join(", ", Array.ConvertAll(_visitOrder, v => $"T{v}"))}]");
        sb.AppendLine($"  Movements       : {_records.Count}");

        double totalTime = 0, totalAmpPx = 0, totalID = 0;
        int    validMoves = 0;

        foreach (var r in _records)
        {
            totalTime  += r.durationSeconds;
            totalAmpPx += r.amplitudePx;
            totalID    += r.fittsID;
            validMoves++;
        }

        sb.AppendLine($"  Total task time : {totalTime:F3} s");
        if (validMoves > 0)
        {
            double tp = 0; int tpc = 0;
            foreach (var r in _records) if (r.durationSeconds > 0) { tp += r.fittsID / r.durationSeconds; tpc++; }
            double throughput = tpc > 0 ? tp / tpc : 0;
            sb.AppendLine($"  Mean amplitude  : {totalAmpPx/validMoves:F1} px");
            sb.AppendLine($"  Mean Fitts ID   : {totalID/validMoves:F3} bits");
            sb.AppendLine($"  Throughput (TP) : {throughput:F3} bits/s");
        }

        sb.AppendLine();
        sb.AppendLine("  ┌────┬──────┬──────┬──────────┬───────────┬──────────┐");
        sb.AppendLine("  │ #  │ From │  To  │ Time (s) │  Amp (px) │ ID (bit) │");
        sb.AppendLine("  ├────┼──────┼──────┼──────────┼───────────┼──────────┤");

        for (int i = 0; i < _records.Count; i++)
        {
            var r = _records[i];
            sb.AppendLine($"  │{i+1,3} │ T{r.fromLabel,-3} │ T{r.toLabel,-3} │{r.durationSeconds,8:F3}  │ {r.amplitudePx,7:F1}   │ {r.fittsID,6:F3}   │");
        }

        sb.AppendLine("  └────┴──────┴──────┴──────────┴───────────┴──────────┘");
        Debug.Log(sb.ToString());
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Gizmos
    // ─────────────────────────────────────────────────────────────────────────

    private void OnDrawGizmos()
    {
        if (!showTargetGizmos) return;
        if (_targetPositions3D == null || _targetPositions3D.Length != numTargets || _visitOrder == null)
            RebuildLayout();
        if (_targetPositions3D == null || _visitOrder == null) return;

        float sphereRadius = TargetRadius3D() * Mathf.Abs(transform.lossyScale.x);

        for (int slot = 0; slot < numTargets; slot++)
        {
            int  label     = (_slotLabel != null) ? _slotLabel[slot] : (slot + 1);
            int  visitStep = -1;
            for (int s = 0; s < _visitOrder.Length; s++)
                if (_visitOrder[s] == slot) { visitStep = s; break; }

            bool isActive = Application.isPlaying &&
                            !_taskComplete &&
                            _visitStep < _visitOrder.Length &&
                            _visitOrder[_visitStep] == slot;

            bool isDone = Application.isPlaying && IsSlotDone(slot);

            Gizmos.color = isActive ? targetActiveColour :
                           isDone   ? targetDoneColour   :
                                      targetIdleColour;

            Gizmos.DrawSphere(_targetPositions3D[slot], sphereRadius);
            Color oc = Gizmos.color; oc.a = Mathf.Min(1f, oc.a * 3f);
            Gizmos.color = oc;
            Gizmos.DrawWireSphere(_targetPositions3D[slot], sphereRadius);

#if UNITY_EDITOR
            string gizLabel = visitStep >= 0
                ? $"T{label}\n#{visitStep + 1}"
                : $"T{label}";

            GUIStyle style = new GUIStyle
            {
                normal    = { textColor = isActive ? Color.green : Color.white },
                fontSize  = Mathf.Max(8, (int)(sphereRadius * 120f)),
                alignment = TextAnchor.MiddleCenter
            };
            Handles.Label(_targetPositions3D[slot] + Vector3.up * sphereRadius * 0.4f, gizLabel, style);

            if (isActive && _dwellProgress > 0f)
            {
                Handles.color = new Color(0.1f, 1f, 0.3f, 0.9f);
                Handles.DrawWireArc(_targetPositions3D[slot], transform.up, transform.right,
                                    _dwellProgress * 360f, sphereRadius * 1.15f);
            }
#endif
        }

        if (debugTrajectories && Application.isPlaying)
        {
            for (int ri = 0; ri < _records.Count; ri++)
            {
                var   rec  = _records[ri];
                if (rec.trajectory3D.Count < 2) continue;
                float fade = 1f - (float)(_records.Count - 1 - ri) / Mathf.Max(1, _records.Count);
                Color tCol = trajectoryColour;
                tCol.a    *= Mathf.Lerp(0.2f, 1f, fade);
                Gizmos.color = tCol;
                for (int pi = 0; pi < rec.trajectory3D.Count - 1; pi++)
                    Gizmos.DrawLine(rec.trajectory3D[pi], rec.trajectory3D[pi + 1]);
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(rec.settlePosition3D, TargetRadius3D() * Mathf.Abs(transform.lossyScale.x) * 0.25f);
            }

            if (_currentTraj3D.Count >= 2)
            {
                Gizmos.color = new Color(1f, 1f, 0f, 0.7f);
                for (int pi = 0; pi < _currentTraj3D.Count - 1; pi++)
                    Gizmos.DrawLine(_currentTraj3D[pi], _currentTraj3D[pi + 1]);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private bool IsSlotDone(int slot)
    {
        if (_visitOrder == null) return false;
        for (int s = 0; s < _visitStep; s++)
            if (_visitOrder[s] == slot) return true;
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    public void Activate()
    {
        if (!managedBySceneManager) return;
        RebuildLayout();
        BakeAllTextures();
        InitROS();
        EnsureDotIndicator();
        InitHapticAction();
        ResetTask();
        RegisterFinishTrainingButton();
    }

    public void RestartTask()
    {
        RebuildLayout();
        ResetTask();
        EnsureDotIndicator();
    }

    public bool IsTaskComplete => _taskComplete;
    public float FittsID => _fittsID;

    public List<(int fromLabel, int toLabel, float duration, float ampPx, float fittsID,
                  Vector3 settle3D, Vector2 settlePlane,
                  List<Vector3> traj3D, List<Vector2> trajPlane)> GetRecords()
    {
        var result = new List<(int, int, float, float, float, Vector3, Vector2, List<Vector3>, List<Vector2>)>();
        foreach (var r in _records)
            result.Add((r.fromLabel, r.toLabel, r.durationSeconds, r.amplitudePx, r.fittsID,
                         r.settlePosition3D, r.settlePositionPlane,
                         new List<Vector3>(r.trajectory3D),
                         new List<Vector2>(r.trajectoryPlane)));
        return result;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Custom Inspector
// ─────────────────────────────────────────────────────────────────────────────
#if UNITY_EDITOR
[UnityEditor.CustomEditor(typeof(FittsLawTask))]
public class FittsLawTaskEditor : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        FittsLawTask task = (FittsLawTask)target;

        UnityEditor.EditorGUILayout.Space(6);
        UnityEditor.EditorGUILayout.LabelField("Sheet Print Command", UnityEditor.EditorStyles.boldLabel);

        UnityEditor.EditorGUILayout.HelpBox(
            "Sheet size is derived from the Plane's world lossy scale " +
            "(lossyScale.x × 10 000 mm).  Changes live as you move or re-parent the object.",
            UnityEditor.MessageType.None);

        if (GUILayout.Button("Copy Command to Clipboard", GUILayout.Height(26)))
        {
            GUIUtility.systemCopyBuffer = task._printSheetCommand;
            Debug.Log("[FittsLawTask] Print command copied to clipboard:\n" + task._printSheetCommand);
        }

        UnityEditor.EditorGUILayout.Space(10);
        UnityEditor.EditorGUILayout.LabelField("Texture Baking", UnityEditor.EditorStyles.boldLabel);

        bool hasPrebaked = task._prebakedTextures != null
                        && task._prebakedTextures.Length > 0
                        && task._prebakedTextures[0] != null;

        UnityEditor.EditorGUILayout.HelpBox(
            hasPrebaked
                ? $"{task._prebakedTextures.Length} pre-baked textures ready. Runtime uses these — no generation lag."
                : "No pre-baked textures found. Textures will be generated at runtime (causes lag spike). Click Bake to fix this.",
            hasPrebaked ? UnityEditor.MessageType.Info : UnityEditor.MessageType.Warning);

        if (GUILayout.Button(hasPrebaked ? "Re-Bake Textures" : "Bake Textures", GUILayout.Height(32)))
            task.BakeAndSaveTextures();

        if (hasPrebaked && GUILayout.Button("Clear Pre-Baked Textures"))
        {
            task._prebakedTextures = null;
            UnityEditor.EditorUtility.SetDirty(task);
        }
    }
}
#endif
