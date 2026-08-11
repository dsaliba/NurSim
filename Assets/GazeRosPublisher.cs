// GazeROSPublisher.cs
// Requires: VIVE OpenXR Plugin 2.5.1+ (com.htc.upm.vive.openxr)
//           ROS TCP Connector          (com.unity.robotics.ros-tcp-connector)
//
// Attach to a GameObject with a BoxCollider (e.g., a flat UI canvas plane).
//
// Topics published:
//   gazeRawTopicName          — Float32MultiArrayMsg  (see RAW_DATA_LAYOUT below)
//   gazeIntersectionTopicName — PointMsg              — x/y normalised [0,1], z=0
//                               Only published when gaze ray hits this collider.
//
// COORDINATE SPACE
//   Assign your XR Origin to gazeSpaceRoot if intersections are wrong.
//   The VIVE gaze pose is in OpenXR local/stage space, which only equals Unity
//   world space when the XR Origin sits at (0,0,0) with no offset.
//
// PERFORMANCE NOTES
//   - All message objects and data arrays are pre-allocated in Start() and reused.
//   - Debug.Log is gated behind verboseLogging (default OFF). Turn it on briefly
//     to diagnose issues, then turn it off for normal operation.
//   - Search "[Gaze]" in the Console to filter this script's output.
//
// SESSION READINESS
//   ViveEyeTracker calls Debug.LogError (not throw) when the OpenXR session is not
//   yet established. To avoid those errors triggering Unity's Error Pause on
//   startup, publishing is deferred by xrSessionStartupDelay seconds (default 3).
//   If the session is lost mid-session (e.g. HMD disconnects), a backoff prevents
//   repeated error spam.

using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;
using RosMessageTypes.Geometry;
using VIVE.OpenXR;
using VIVE.OpenXR.EyeTracker;

[RequireComponent(typeof(BoxCollider))]
public class GazeROSPublisher : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector fields
    // -------------------------------------------------------------------------

    [Header("ROS Topics")]
    [Tooltip("Float32MultiArray topic for raw gaze data (see RAW_DATA_LAYOUT in source).")]
    [SerializeField] private string gazeRawTopicName = "/vive/gaze/raw";

    [Tooltip("PointMsg topic for the normalised hit position on this collider (x,y in [0,1]).")]
    [SerializeField] private string gazeIntersectionTopicName = "/vive/gaze/intersection";

    [Header("Coordinate Space")]
    [Tooltip("Assign your XR Origin here. The gaze pose from VIVE OpenXR is in tracking/stage space; " +
             "this transform converts it to Unity world space. Leave empty only if XR Origin = world origin.")]
    [SerializeField] private Transform gazeSpaceRoot;

    [Header("Publish Settings")]
    [Tooltip("Target publish rate in Hz.")]
    [SerializeField] private float publishRateHz = 75f;

    [Tooltip("Max raycast distance (metres).")]
    [SerializeField] private float maxRaycastDistance = 500f;

    [Tooltip("Layer mask for intersection raycast. Restrict to the canvas layer for best performance.")]
    [SerializeField] private LayerMask intersectionLayerMask = ~0;

    [Header("XR Session")]
    [Tooltip("Seconds to wait after play mode start before attempting to read gaze data. " +
             "ViveEyeTracker logs a LogError (not a throw) when the OpenXR session isn't ready yet, " +
             "which triggers Unity's Error Pause. This delay prevents that.")]
    [SerializeField] private float xrSessionStartupDelay = 3f;

    [Tooltip("After this many consecutive XR_ERROR_SESSION_LOST failures in a row, " +
             "publishing backs off for xrSessionBackoffSeconds before retrying.")]
    [SerializeField] private int xrSessionLostBackoffThreshold = 5;

    [Tooltip("How long (seconds) to wait before retrying after hitting the backoff threshold.")]
    [SerializeField] private float xrSessionBackoffSeconds = 5f;

    [Header("Debug")]
    [Tooltip("Draw the gaze ray in the Scene view. Cheap — leave on during development.")]
    [SerializeField] private bool drawDebugRay = true;

    [Tooltip("Enable verbose per-tick console logging. KEEP OFF during normal use — " +
             "Debug.Log captures a full stack trace on every call and will cause lag at 20 Hz.")]
    [SerializeField] private bool verboseLogging = false;

    [Tooltip("When verboseLogging is on, how often to print (Hz). Keep at 1 or lower.")]
    [SerializeField] private float verboseLogRateHz = 1f;

    // -------------------------------------------------------------------------
    // RAW_DATA_LAYOUT — indices into Float32MultiArray.data
    // -------------------------------------------------------------------------
    // -1 = field invalid/unavailable this frame.   All positions in world-space.
    //
    // [0 ]  Left  gaze origin X       [3 ]  Left  gaze direction X
    // [1 ]  Left  gaze origin Y       [4 ]  Left  gaze direction Y
    // [2 ]  Left  gaze origin Z       [5 ]  Left  gaze direction Z
    // [6 ]  Right gaze origin X       [9 ]  Right gaze direction X
    // [7 ]  Right gaze origin Y       [10]  Right gaze direction Y
    // [8 ]  Right gaze origin Z       [11]  Right gaze direction Z
    // [12]  Combined origin X         [15]  Combined direction X
    // [13]  Combined origin Y         [16]  Combined direction Y
    // [14]  Combined origin Z         [17]  Combined direction Z
    // [18]  Left  pupil diameter (mm) [19]  Right pupil diameter (mm)
    // [20]  Left  pupil position X    [21]  Left  pupil position Y
    // [22]  Right pupil position X    [23]  Right pupil position Y
    // [24]  Left  eye openness        [25]  Right eye openness
    // [26]  Left  eye squeeze         [27]  Right eye squeeze
    // [28]  Left  eye wide            [29]  Right eye wide
    // [30]  Valid-flags bitmask:
    //         bit0=left gaze   bit1=right gaze
    //         bit2=L pupil dia bit3=R pupil dia
    //         bit4=L pupil pos bit5=R pupil pos
    // -------------------------------------------------------------------------
    private const int RAW_DATA_SIZE = 31;
    private const int L = (int)XrEyePositionHTC.XR_EYE_POSITION_LEFT_HTC;
    private const int R = (int)XrEyePositionHTC.XR_EYE_POSITION_RIGHT_HTC;

    // -------------------------------------------------------------------------
    // Pre-allocated objects — never recreated after Start()
    // -------------------------------------------------------------------------
    private ROSConnection        ros;
    private BoxCollider          boxCollider;
    private float[]              rawData;          // reused every tick
    private Float32MultiArrayMsg rawMsg;           // wrapper reused; .data points to rawData
    private PointMsg             intersectionMsg;  // reused on every hit

    // -------------------------------------------------------------------------
    // Timing
    // -------------------------------------------------------------------------
    private float publishInterval;
    private float lastPublishTime;
    private float verboseLogInterval;
    private float lastVerboseLogTime = -999f;
    private float lastBoundsLogTime  = -999f;

    // -------------------------------------------------------------------------
    // Session readiness / backoff
    // -------------------------------------------------------------------------
    // ViveEyeTracker calls Debug.LogError before returning XR_ERROR_SESSION_LOST,
    // which triggers Unity's Error Pause. We gate publishing with a startup delay
    // and back off on repeated failures so we never spam errors mid-session.

    private float sessionReadyTime;       // Time.time value after which we begin publishing
    private bool  sessionEverReady;       // true once we've had at least one successful read
    private int   consecutiveFailures;    // resets to 0 on success
    private float backoffUntil = -1f;    // if > Time.time, we are in backoff

    // -------------------------------------------------------------------------
    // One-shot warning deduplication — so repeated failures don't spam the log
    // -------------------------------------------------------------------------
    private bool warnedGazeFailed;
    private bool warnedPupilFailed;
    private bool warnedGeometricFailed;

    // -------------------------------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
        boxCollider = GetComponent<BoxCollider>();
    }

    private void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<Float32MultiArrayMsg>(gazeRawTopicName);
        ros.RegisterPublisher<PointMsg>(gazeIntersectionTopicName);

        publishInterval    = 1f / Mathf.Max(publishRateHz, 1f);
        verboseLogInterval = 1f / Mathf.Max(verboseLogRateHz, 0.1f);

        // Defer publishing until after the XR session has had time to establish.
        // ViveEyeTracker.GetEyeGazeDataHTC() calls Debug.LogError (not throw) when
        // m_XrSessionCreated is false — that LogError triggers Error Pause before we
        // even see the return value. The startup delay prevents calling the API too early.
        sessionReadyTime = Time.time + xrSessionStartupDelay;

        // Pre-allocate the data array and message objects once.
        // rawMsg.data is assigned the same array reference — we mutate the array
        // in place each tick and the existing message object is reused for publish.
        rawData = new float[RAW_DATA_SIZE];
        rawMsg  = new Float32MultiArrayMsg
        {
            layout = new MultiArrayLayoutMsg
            {
                dim = new[]
                {
                    new MultiArrayDimensionMsg
                    {
                        label  = "gaze_fields",
                        size   = RAW_DATA_SIZE,
                        stride = RAW_DATA_SIZE
                    }
                },
                data_offset = 0
            },
            data = rawData  // shared reference — mutations to rawData are visible here
        };
        intersectionMsg = new PointMsg { z = 0.0 };

        Debug.Log($"[Gaze] GazeROSPublisher started on '{gameObject.name}' — " +
                  $"raw='{gazeRawTopicName}' intersection='{gazeIntersectionTopicName}' " +
                  $"rate={publishRateHz}Hz " +
                  $"spaceRoot={(gazeSpaceRoot != null ? gazeSpaceRoot.name : "none (raw OpenXR space)")} " +
                  $"colliderBounds={boxCollider.bounds} layer={LayerMask.LayerToName(gameObject.layer)} " +
                  $"startupDelay={xrSessionStartupDelay}s");
    }

    private void Update()
    {
        float now = Time.time;

        // Gate 1: startup delay — XR session not ready yet.
        if (now < sessionReadyTime)
            return;

        // Gate 2: backoff after repeated session-lost failures mid-session.
        if (now < backoffUntil)
            return;

        // Gate 3: publish rate throttle.
        if (now - lastPublishTime < publishInterval)
            return;

        lastPublishTime = now;
        PublishGazeData();
    }

    private void OnDestroy()
    {
        Debug.Log($"[Gaze] GazeROSPublisher destroyed on '{gameObject.name}'.");
    }

    // -------------------------------------------------------------------------
    // Core publish — zero heap allocations per call in steady state
    // -------------------------------------------------------------------------

    private void PublishGazeData()
    {
        // ------------------------------------------------------------------
        // 1. Fetch gaze data from VIVE OpenXR 2.5.1
        //
        // IMPORTANT: XR_HTC_eye_tracker.Interop calls ViveEyeTracker internally.
        // ViveEyeTracker calls Debug.LogError (not throw) when the XR session is
        // not ready — there is no way to check session state from outside the
        // library without triggering that error. We rely on the startup delay and
        // backoff to avoid calling the API when the session isn't established.
        // ------------------------------------------------------------------
        XrSingleEyeGazeDataHTC[]     gazes      = null;
        XrSingleEyePupilDataHTC[]    pupils     = null;
        XrSingleEyeGeometricDataHTC[] geometrics = null;

        // ViveEyeTracker calls Debug.LogError (not throw) for hardware/session errors
        // like XR_ERROR_SESSION_LOST before returning false. There is no public API to
        // check session readiness without triggering that error. Temporarily disabling
        // the Unity logger prevents those LogError calls from firing Error Pause.
        // Logging is always re-enabled in the finally block; we issue our own warnings.
        bool gazeOk = false, pupilOk = false, geometricOk = false;
        System.Exception caughtEx = null;

        bool logWasEnabled = Debug.unityLogger.logEnabled;
        Debug.unityLogger.logEnabled = false;
        try
        {
            gazeOk      = XR_HTC_eye_tracker.Interop.GetEyeGazeData(out gazes);
            pupilOk     = XR_HTC_eye_tracker.Interop.GetEyePupilData(out pupils);
            geometricOk = XR_HTC_eye_tracker.Interop.GetEyeGeometricData(out geometrics);
        }
        catch (System.Exception ex)
        {
            caughtEx = ex;
        }
        finally
        {
            Debug.unityLogger.logEnabled = logWasEnabled;
        }

        if (caughtEx != null)
        {
            if (!warnedGazeFailed)
            {
                warnedGazeFailed = true;
                Debug.LogWarning($"[Gaze] XR_HTC_eye_tracker.Interop exception: {caughtEx.Message} " +
                                 "— ensure VIVE XR Eye Tracker is enabled in XR Plug-in Management > OpenXR.");
            }
            HandleFailure();
            return;
        }

        if (!gazeOk || gazes == null || gazes.Length < 2)
        {
            // Don't warn until we've had at least one success — the XR session
            // may still be initialising slightly after our startup delay.
            if (sessionEverReady && !warnedGazeFailed)
            {
                warnedGazeFailed = true;
                Debug.LogWarning("[Gaze] GetEyeGazeData failed — eye tracking not active. " +
                                 "Check device permissions and feature toggle.");
            }
            HandleFailure();
            return;
        }

        // We have valid data — reset failure tracking.
        sessionEverReady    = true;
        consecutiveFailures = 0;
        warnedGazeFailed    = false;

        // ------------------------------------------------------------------
        // 2. Unpack gaze poses into world-space origin + direction
        // ------------------------------------------------------------------
        XrSingleEyeGazeDataHTC leftGaze  = gazes[L];
        XrSingleEyeGazeDataHTC rightGaze = gazes[R];

        bool leftValid  = (uint)leftGaze.isValid  != 0;
        bool rightValid = (uint)rightGaze.isValid != 0;

        Vector3 leftOrigin = Vector3.zero,  leftDir  = Vector3.forward;
        Vector3 rightOrigin = Vector3.zero, rightDir = Vector3.forward;

        if (leftValid)
        {
            leftOrigin = leftGaze.gazePose.position.ToUnityVector();
            leftDir    = (leftGaze.gazePose.orientation.ToUnityQuaternion() * Vector3.forward).normalized;
        }
        if (rightValid)
        {
            rightOrigin = rightGaze.gazePose.position.ToUnityVector();
            rightDir    = (rightGaze.gazePose.orientation.ToUnityQuaternion() * Vector3.forward).normalized;
        }

        // Transform from OpenXR tracking space → Unity world space if a root is assigned
        if (gazeSpaceRoot != null)
        {
            if (leftValid)
            {
                leftOrigin = gazeSpaceRoot.TransformPoint(leftOrigin);
                leftDir    = gazeSpaceRoot.TransformDirection(leftDir).normalized;
            }
            if (rightValid)
            {
                rightOrigin = gazeSpaceRoot.TransformPoint(rightOrigin);
                rightDir    = gazeSpaceRoot.TransformDirection(rightDir).normalized;
            }
        }

        // Combined gaze (averaged from whichever eyes are valid)
        Vector3 combinedOrigin, combinedDir;
        if (leftValid && rightValid)
        {
            combinedOrigin = (leftOrigin + rightOrigin) * 0.5f;
            combinedDir    = ((leftDir + rightDir) * 0.5f).normalized;
        }
        else if (leftValid)  { combinedOrigin = leftOrigin;  combinedDir = leftDir;  }
        else if (rightValid) { combinedOrigin = rightOrigin; combinedDir = rightDir; }
        else                 { combinedOrigin = Vector3.one * -1f; combinedDir = Vector3.one * -1f; }

        // ------------------------------------------------------------------
        // 3. Unpack pupil data
        // ------------------------------------------------------------------
        bool   lDiamValid = false, rDiamValid = false;
        bool   lPosValid  = false, rPosValid  = false;
        float  lDiam = -1f, rDiam = -1f;
        float  lPosX =  0f, lPosY =  0f, rPosX = 0f, rPosY = 0f;

        if (pupilOk && pupils != null && pupils.Length >= 2)
        {
            warnedPupilFailed = false;
            XrSingleEyePupilDataHTC lp = pupils[L];
            XrSingleEyePupilDataHTC rp = pupils[R];

            lDiamValid = (uint)lp.isDiameterValid != 0;
            rDiamValid = (uint)rp.isDiameterValid != 0;
            lPosValid  = (uint)lp.isPositionValid != 0;
            rPosValid  = (uint)rp.isPositionValid != 0;

            if (lDiamValid) lDiam = lp.pupilDiameter;
            if (rDiamValid) rDiam = rp.pupilDiameter;
            if (lPosValid)  { lPosX = lp.pupilPosition.x; lPosY = lp.pupilPosition.y; }
            if (rPosValid)  { rPosX = rp.pupilPosition.x; rPosY = rp.pupilPosition.y; }
        }
        else if (!pupilOk && !warnedPupilFailed)
        {
            warnedPupilFailed = true;
            Debug.LogWarning("[Gaze] GetEyePupilData failed — pupil fields will be -1.");
        }

        // ------------------------------------------------------------------
        // 4. Unpack geometric data
        // ------------------------------------------------------------------
        float lOpen = -1f, rOpen = -1f, lSq = 0f, rSq = 0f, lWide = 0f, rWide = 0f;

        if (geometricOk && geometrics != null && geometrics.Length >= 2)
        {
            warnedGeometricFailed = false;
            XrSingleEyeGeometricDataHTC lg = geometrics[L];
            XrSingleEyeGeometricDataHTC rg = geometrics[R];
            lOpen = lg.eyeOpenness; rOpen = rg.eyeOpenness;
            lSq   = lg.eyeSqueeze;  rSq   = rg.eyeSqueeze;
            lWide = lg.eyeWide;     rWide = rg.eyeWide;
        }
        else if (!geometricOk && !warnedGeometricFailed)
        {
            warnedGeometricFailed = true;
            Debug.LogWarning("[Gaze] GetEyeGeometricData failed — geometric fields will be -1.");
        }

        // ------------------------------------------------------------------
        // 5. Fill pre-allocated array in place and publish (zero allocations)
        // ------------------------------------------------------------------
        rawData[0]  = leftOrigin.x;    rawData[1]  = leftOrigin.y;    rawData[2]  = leftOrigin.z;
        rawData[3]  = leftDir.x;       rawData[4]  = leftDir.y;       rawData[5]  = leftDir.z;
        rawData[6]  = rightOrigin.x;   rawData[7]  = rightOrigin.y;   rawData[8]  = rightOrigin.z;
        rawData[9]  = rightDir.x;      rawData[10] = rightDir.y;      rawData[11] = rightDir.z;
        rawData[12] = combinedOrigin.x; rawData[13] = combinedOrigin.y; rawData[14] = combinedOrigin.z;
        rawData[15] = combinedDir.x;   rawData[16] = combinedDir.y;   rawData[17] = combinedDir.z;
        rawData[18] = lDiam;           rawData[19] = rDiam;
        rawData[20] = lPosX;           rawData[21] = lPosY;
        rawData[22] = rPosX;           rawData[23] = rPosY;
        rawData[24] = lOpen;           rawData[25] = rOpen;
        rawData[26] = lSq;             rawData[27] = rSq;
        rawData[28] = lWide;           rawData[29] = rWide;
        rawData[30] = (leftValid   ? 1  : 0) | (rightValid  ? 2  : 0)
                    | (lDiamValid  ? 4  : 0) | (rDiamValid  ? 8  : 0)
                    | (lPosValid   ? 16 : 0) | (rPosValid   ? 32 : 0);

        ros.Publish(gazeRawTopicName, rawMsg);

        // ------------------------------------------------------------------
        // 6. Raycast and publish intersection
        // ------------------------------------------------------------------
        if (!leftValid && !rightValid)
            return;

        if (drawDebugRay)
            Debug.DrawRay(combinedOrigin, combinedDir * maxRaycastDistance, Color.cyan);

        bool hit = Physics.Raycast(
            combinedOrigin, combinedDir, out RaycastHit hitInfo,
            maxRaycastDistance, intersectionLayerMask, QueryTriggerInteraction.Collide);

        if (hit && hitInfo.collider == boxCollider)
        {
            Vector2 uv = WorldHitToNormalized(hitInfo.point);
            intersectionMsg.x = uv.x;
            intersectionMsg.y = uv.y;
            ros.Publish(gazeIntersectionTopicName, intersectionMsg);
        }

        // ------------------------------------------------------------------
        // 7. Verbose logging — only when enabled, throttled to verboseLogRateHz
        //    KEEP verboseLogging = false in production. Debug.Log captures a
        //    stack trace on every call; at 20 Hz it will cause visible lag.
        // ------------------------------------------------------------------
        if (!verboseLogging)
            return;

        float now = Time.time;
        if (now - lastVerboseLogTime < verboseLogInterval)
            return;
        lastVerboseLogTime = now;

        int flags = (int)rawData[30];
        string intersection = (hit && hitInfo.collider == boxCollider)
            ? $"uv=({intersectionMsg.x:F3},{intersectionMsg.y:F3})"
            : hit ? $"hit wrong collider '{hitInfo.collider.gameObject.name}'" : "no hit";

        Debug.Log($"[Gaze] " +
                  $"L=({leftOrigin.x:F2},{leftOrigin.y:F2},{leftOrigin.z:F2})->" +
                  $"({leftDir.x:F2},{leftDir.y:F2},{leftDir.z:F2}) " +
                  $"R=({rightOrigin.x:F2},{rightOrigin.y:F2},{rightOrigin.z:F2})->" +
                  $"({rightDir.x:F2},{rightDir.y:F2},{rightDir.z:F2}) " +
                  $"Lpupil={lDiam:F2}mm Rpupil={rDiam:F2}mm " +
                  $"Lopen={lOpen:F2} Ropen={rOpen:F2} " +
                  $"Lsq={lSq:F2} Rsq={rSq:F2} Lwide={lWide:F2} Rwide={rWide:F2} " +
                  $"flags=0b{System.Convert.ToString(flags, 2).PadLeft(6, '0')} " +
                  $"intersection={intersection}");

        // Periodically log collider bounds (useful for coord-space debugging)
        if (now - lastBoundsLogTime >= 3f)
        {
            lastBoundsLogTime = now;
            Bounds b = boxCollider.bounds;
            Debug.Log($"[Gaze] BOUNDS '{gameObject.name}' center={b.center} size={b.size} " +
                      $"layer={LayerMask.LayerToName(gameObject.layer)}({gameObject.layer}) " +
                      $"enabled={boxCollider.enabled} isTrigger={boxCollider.isTrigger}");
        }
    }

    // -------------------------------------------------------------------------
    // Session failure handling
    // -------------------------------------------------------------------------

    // Called whenever a publish attempt fails (no data, exception, etc.).
    // After xrSessionLostBackoffThreshold consecutive failures, publishing backs
    // off for xrSessionBackoffSeconds. This prevents the VIVE library from being
    // hammered when the session is lost, which would spam LogError every frame.
    private void HandleFailure()
    {
        consecutiveFailures++;

        if (consecutiveFailures >= xrSessionLostBackoffThreshold)
        {
            consecutiveFailures = 0;
            backoffUntil = Time.time + xrSessionBackoffSeconds;
            Debug.LogWarning($"[Gaze] {xrSessionLostBackoffThreshold} consecutive failures — " +
                             $"backing off for {xrSessionBackoffSeconds}s before retrying. " +
                             "If this repeats, check that the HMD is connected and the OpenXR session is active.");
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Maps a world-space hit point on this BoxCollider to a normalised [0,1]×[0,1]
    /// coordinate in the collider's local XY plane.
    /// x=0 left, x=1 right, y=0 bottom, y=1 top.
    /// </summary>
    private Vector2 WorldHitToNormalized(Vector3 worldPoint)
    {
        Vector3 local  = transform.InverseTransformPoint(worldPoint);
        Vector3 center = boxCollider.center;
        Vector3 size   = boxCollider.size;

        float nx = (local.x - (center.x - size.x * 0.5f)) / (size.x > 0f ? size.x : 0.001f);
        float ny = (local.y - (center.y - size.y * 0.5f)) / (size.y > 0f ? size.y : 0.001f);

        return new Vector2(Mathf.Clamp01(nx), Mathf.Clamp01(ny));
    }
}
