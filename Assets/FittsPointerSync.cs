using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;

/// <summary>
/// FittsPointerSync  —  attach to the Plane that has FittsLawTask.
/// ============================================================================
///
/// HOW TO CALIBRATE (do this in order)
/// =====================================
///
/// STEP 0 — Prerequisites
///   • physicalSheetWidthM  must equal transform.localScale.x * 10
///   • physicalSheetHeightM must equal transform.localScale.z * 10
///   • A MISMATCH warning in the Console tells you what to fix.
///
/// STEP 1 — Find your axis mapping  (tick "Calibration Mode" in Inspector)
///   The HUD shows three live values: ROS X  |  ROS Y  |  ROS Z
///   and which one changed most since you pressed "Save Reference".
///
///   a) Press "Save Reference" (button appears in Inspector during Play).
///   b) Slowly move the physical pointer to the RIGHT of the sheet.
///      The value that increases the most → set axisSheetRight to that axis.
///      If it DECREASES, use NegRos* instead.
///   c) Press "Save Reference" again.
///   d) Move pointer toward the TOP of the sheet.
///      Set axisSheetUp from whichever changes.
///   e) Press "Save Reference" again.
///   f) LIFT pointer away from the sheet surface.
///      Set axisSheetNorm from whichever changes.
///
/// STEP 2 — Fix the centre offset
///   Place the physical pointer at the exact centre of the printed layout.
///   If the gizmo is NOT at the Unity plane centre:
///     • If the tag offsets in the launch file are wrong (likely), fix them:
///         roslaunch ... tag_offset_x_mm:=VALUE tag_offset_y_mm:=VALUE
///     • Or, for quick iteration, adjust "Sheet Offset (local units)" live
///       in the Inspector until the gizmo snaps to the plane centre.
///
/// STEP 3 — Verify rotation
///   Tilt the pointer FORWARD (toward you).  The gizmo axis arrows should
///   reflect the tilt.  If rotation is mirrored or spinning wrong, adjust
///   "Rotation Offset Euler" in the Inspector (try (0,0,0), (180,0,0), (0,180,0)).
///   The rotation conversion is derived from your axis mapping automatically —
///   you should rarely need to change rotationOffsetEuler once position is right.
///
/// Coordinate system notes
/// -----------------------
///   ROS "fitts_sheet" frame (right-handed):
///     X = right on sheet,  Y = up on sheet,  Z = toward viewer (sheet normal)
///   Unity Plane local (left-handed, 10×10 units):
///     X = sheet X,  Y = sheet Z (normal/height),  -Z = sheet Y (up)
///   This is because FittsLawTask.WorldToPlane returns (local.x/10, -local.z/10).
/// </summary>
[AddComponentMenu("Fitts Law / Pointer Sync (ROS)")]
public class FittsPointerSync : MonoBehaviour
{
    // ── Physical sheet ─────────────────────────────────────────────────────────
    [Header("Physical sheet dimensions (metres)")]
    [Tooltip("Must equal transform.localScale.x * 10.  See Console warning on Play.")]
    public float physicalSheetWidthM  = 0.30f;
    [Tooltip("Must equal transform.localScale.z * 10.")]
    public float physicalSheetHeightM = 0.30f;

    // ── Pointer ────────────────────────────────────────────────────────────────
    [Header("Pointer target")]
    [Tooltip("Empty child of this Plane; also referenced by FittsLawTask.pointer.")]
    public Transform pointer;

    // ── ROS ───────────────────────────────────────────────────────────────────
    [Header("ROS")]
    public string rosTopic = "/fitts/pointer_pose_sheet";

    // ── Axis mapping (STEP 1) ──────────────────────────────────────────────────
    [Header("Axis mapping  —  set after STEP 1 calibration")]
    [Tooltip("Which ROS axis corresponds to moving the pointer RIGHT on the sheet.")]
    public RosAxis axisSheetRight = RosAxis.RosX;
    [Tooltip("Which ROS axis corresponds to moving the pointer UP on the sheet face.")]
    public RosAxis axisSheetUp    = RosAxis.RosY;
    [Tooltip("Which ROS axis corresponds to LIFTING the pointer off the sheet surface.")]
    public RosAxis axisSheetNorm  = RosAxis.RosZ;

    // ── Centre offset (STEP 2) ─────────────────────────────────────────────────
    [Header("Sheet origin offset  (local units)  —  STEP 2 fine-tuning")]
    [Tooltip("Shift the mapped position in Unity local X.  Physical sheet centre should land at 0.")]
    public float sheetOffsetLocalX = 0f;
    [Tooltip("Shift the mapped position in Unity local Z.  Physical sheet centre should land at 0.")]
    public float sheetOffsetLocalZ = 0f;

    // ── Pointer appearance ─────────────────────────────────────────────────────
    [Header("Height offset  (local units)")]
    [Tooltip("Minimum lift above the plane.  ROS normal-axis distance is added on top.")]
    public float pointerHeightOffset = 0.02f;

    // ── Rotation (STEP 3) ──────────────────────────────────────────────────────
    [Header("Rotation offset  (Euler, local)  —  STEP 3 trim")]
    [Tooltip("Rotate the pointer model after the axis conversion.  "
           + "Try (0,0,0); if orientation is mirrored try (180,0,0) or (0,180,0).")]
    public Vector3 rotationOffsetEuler = Vector3.zero;

    // ── Smoothing ──────────────────────────────────────────────────────────────
    [Header("Smoothing")]
    [Range(0.01f, 1f)] public float positionLerpFactor  = 0.6f;
    [Range(0.01f, 1f)] public float rotationSlerpFactor = 0.5f;

    // ── Calibration mode (STEP 1 tool) ─────────────────────────────────────────
    [Header("Calibration mode  (STEP 1)")]
    [Tooltip("Shows raw ROS X/Y/Z values on screen and delta from reference point.")]
    public bool calibrationMode = false;

    // ── Debug ──────────────────────────────────────────────────────────────────
    [Header("Debug")]
    public bool  showDebugGizmo = true;
    public Color gizmoColour    = new Color(1f, 0.85f, 0f, 0.9f);
    public float gizmoRadius    = 0.015f;

    // ─────────────────────────────────────────────────────────────────────────
    //  Enum
    // ─────────────────────────────────────────────────────────────────────────

    public enum RosAxis { RosX, RosY, RosZ, NegRosX, NegRosY, NegRosZ }

    // ─────────────────────────────────────────────────────────────────────────
    //  Runtime state
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Runtime (read-only)")]
    [SerializeField] private bool    _rosConnected = false;
    [SerializeField] private float   _lastMsgAgeSec = float.PositiveInfinity;
    [SerializeField] private Vector3 _rawRos    = Vector3.zero;
    [SerializeField] private Vector3 _deltaRos  = Vector3.zero;   // change since reference
    [SerializeField] private Vector3 _localPos  = Vector3.zero;

    private Vector3    _targetWorldPos;
    private Quaternion _targetWorldRot = Quaternion.identity;
    private bool       _hasTarget      = false;
    private float      _lastMsgTime    = -1f;

    // Calibration helpers
    private Vector3 _calReference = Vector3.zero;

    // ─────────────────────────────────────────────────────────────────────────
    //  Unity lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        if (pointer == null)
        {
            Debug.LogError("[FittsPointerSync] 'Pointer' not assigned — disable script.", this);
            enabled = false;
            return;
        }
        ValidateScale();
        ROSConnection.GetOrCreateInstance()
            .Subscribe<PoseStampedMsg>(rosTopic, OnPoseReceived);
        _rosConnected = true;
        Debug.Log($"[FittsPointerSync] Subscribed to {rosTopic}. "
                + $"Sheet {physicalSheetWidthM*1000:.0f}×{physicalSheetHeightM*1000:.0f} mm.");

        if (calibrationMode)
            Debug.Log("[FittsPointerSync] CALIBRATION MODE ON — see HUD for live values. "
                    + "Press 'Save Reference' in Inspector to capture reference point.");
    }

    private void Update()
    {
        if (!_hasTarget || pointer == null) return;

        _lastMsgAgeSec = _lastMsgTime > 0f ? Time.time - _lastMsgTime : float.PositiveInfinity;
        _deltaRos      = _rawRos - _calReference;

        pointer.position = Vector3.Lerp(pointer.position,  _targetWorldPos, positionLerpFactor);
        pointer.rotation = Quaternion.Slerp(pointer.rotation, _targetWorldRot, rotationSlerpFactor);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Calibration helper  (called by custom Inspector button)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Snapshot the current raw ROS position as the calibration reference.
    /// Call this before moving the pointer in a known direction.
    /// Exposed as a button in the custom Inspector below.
    /// </summary>
    public void SaveCalibrationReference()
    {
        _calReference = _rawRos;
        Debug.Log($"[FittsPointerSync] Reference saved: raw ROS = {_rawRos}. "
                + "Now move the pointer in one known direction and note which delta grows.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ROS callback
    // ─────────────────────────────────────────────────────────────────────────

    private void OnPoseReceived(PoseStampedMsg msg)
    {
        float rx = (float)msg.pose.position.x;
        float ry = (float)msg.pose.position.y;
        float rz = (float)msg.pose.position.z;
        _rawRos = new Vector3(rx, ry, rz);

        // ── Position ──────────────────────────────────────────────────────────
        float sheetRight = Pick(axisSheetRight, rx, ry, rz);  // → Unity local  X
        float sheetUp    = Pick(axisSheetUp,    rx, ry, rz);  // → Unity local −Z
        float sheetNorm  = Pick(axisSheetNorm,  rx, ry, rz);  // → Unity local  Y

        float halfW  = physicalSheetWidthM  * 0.5f;
        float halfH  = physicalSheetHeightM * 0.5f;
        float scaleW = 5f / halfW;
        float scaleH = 5f / halfH;

        float localX =  sheetRight * scaleW + sheetOffsetLocalX;
        float localZ = -sheetUp    * scaleH + sheetOffsetLocalZ;  // ← negation
        float localY =  Mathf.Max(sheetNorm, 0f) * scaleW + pointerHeightOffset;

        localX = Mathf.Clamp(localX, -8f, 8f);
        localZ = Mathf.Clamp(localZ, -8f, 8f);

        _localPos       = new Vector3(localX, localY, localZ);
        _targetWorldPos = transform.TransformPoint(_localPos);

        // ── Rotation ──────────────────────────────────────────────────────────
        // Derived automatically from the same axis mapping.
        // The three position axes imply which ROS quaternion components go to
        // Unity local X, Y, Z (with left-hand sign adjustments).
        var ori = msg.pose.orientation;
        float qx_ros = (float)ori.x;
        float qy_ros = (float)ori.y;
        float qz_ros = (float)ori.z;
        float qw_ros = (float)ori.w;

        // Which ROS imaginary component ends up in each Unity local slot:
        //   Unity X (right)   ← the ROS component for axisSheetRight  — negated (LH convention)
        //   Unity Y (normal)  ← the ROS component for axisSheetNorm
        //   Unity Z (for −Z)  ← the ROS component for axisSheetUp     — negated (LH + dir flip)
        float uqX = -Pick(axisSheetRight, qx_ros, qy_ros, qz_ros);
        float uqY =  Pick(axisSheetNorm,  qx_ros, qy_ros, qz_ros);
        float uqZ = -Pick(axisSheetUp,    qx_ros, qy_ros, qz_ros);

        Quaternion localRot = Quaternion.Euler(rotationOffsetEuler)
                            * new Quaternion(uqX, uqY, uqZ, qw_ros);
        _targetWorldRot = transform.rotation * localRot;

        _lastMsgTime = Time.time;
        _hasTarget   = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static float Pick(RosAxis axis, float x, float y, float z) => axis switch
    {
        RosAxis.RosX    =>  x,
        RosAxis.RosY    =>  y,
        RosAxis.RosZ    =>  z,
        RosAxis.NegRosX => -x,
        RosAxis.NegRosY => -y,
        RosAxis.NegRosZ => -z,
        _               =>  x,
    };

    private void ValidateScale()
    {
        float expW = transform.localScale.x * 10f;
        float expH = transform.localScale.z * 10f;
        if (Mathf.Abs(expW - physicalSheetWidthM) > 0.002f ||
            Mathf.Abs(expH - physicalSheetHeightM) > 0.002f)
            Debug.LogWarning(
                $"[FittsPointerSync] SCALE MISMATCH: plane implies "
                + $"{expW*1000:.0f}×{expH*1000:.0f} mm, "
                + $"Inspector says {physicalSheetWidthM*1000:.0f}×{physicalSheetHeightM*1000:.0f} mm. "
                + $"Set physicalSheetWidthM={expW:.3f}, physicalSheetHeightM={expH:.3f}.", this);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Calibration HUD  (only in calibrationMode)
    // ─────────────────────────────────────────────────────────────────────────

    private void OnGUI()
    {
        if (!calibrationMode || !_hasTarget) return;

        float W = 420f, H = 180f;
        float x = 16f, y = Screen.height - H - 16f;

        GUI.color = new Color(0, 0, 0, 0.7f);
        GUI.DrawTexture(new Rect(x - 6, y - 6, W + 12, H + 12), Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUIStyle title = new GUIStyle(GUI.skin.label)
            { fontSize = 13, fontStyle = FontStyle.Bold };
        GUIStyle val = new GUIStyle(GUI.skin.label)
            { fontSize = 18, fontStyle = FontStyle.Bold };
        GUIStyle sub = new GUIStyle(GUI.skin.label)
            { fontSize = 11 };

        float col = W / 3f;
        string[] labels = { "ROS  X", "ROS  Y", "ROS  Z" };
        float[]  raw    = { _rawRos.x, _rawRos.y, _rawRos.z };
        float[]  delta  = { _deltaRos.x, _deltaRos.y, _deltaRos.z };
        float    maxAbs = Mathf.Max(Mathf.Abs(_deltaRos.x),
                                     Mathf.Abs(_deltaRos.y),
                                     Mathf.Abs(_deltaRos.z));

        GUI.Label(new Rect(x, y, W, 22), "FittsPointerSync  —  CALIBRATION MODE", title);
        y += 22;
        GUI.Label(new Rect(x, y, W, 16), "Move pointer in ONE known direction, watch which delta grows.", sub);
        y += 18;

        for (int i = 0; i < 3; i++)
        {
            float cx = x + i * col;
            bool  active = maxAbs > 0.005f && Mathf.Abs(delta[i]) == maxAbs;
            GUI.color = active ? Color.yellow : Color.white;
            GUI.Label(new Rect(cx, y,      col, 20), labels[i], title);
            GUI.Label(new Rect(cx, y + 20, col, 28), $"{raw[i]:+0.000;-0.000}", val);
            GUI.Label(new Rect(cx, y + 48, col, 20),
                      $"Δ {delta[i]:+0.000;-0.000}" + (active ? "  ← LARGEST" : ""), sub);
            GUI.color = Color.white;
        }

        y += 75;
        GUI.color = new Color(1, 1, 0.4f, 1);
        GUI.Label(new Rect(x, y, W, 16),
            "Press  [Save Reference]  in the Inspector before each direction test.", sub);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Gizmos
    // ─────────────────────────────────────────────────────────────────────────

    private void OnDrawGizmos()
    {
        if (!showDebugGizmo || !Application.isPlaying || !_hasTarget || pointer == null)
            return;

        // Sphere at pointer
        Gizmos.color = gizmoColour;
        Gizmos.DrawSphere(pointer.position, gizmoRadius);
        Gizmos.DrawWireSphere(pointer.position, gizmoRadius * 1.4f);

        // Line from plane centre to pointer
        Gizmos.color = new Color(gizmoColour.r, gizmoColour.g, gizmoColour.b, 0.25f);
        Gizmos.DrawLine(transform.position, pointer.position);

        // Pointer orientation axes (R/G/B = right/up/forward of pointer)
        float len = gizmoRadius * 4f;
        Gizmos.color = Color.red;
        Gizmos.DrawLine(pointer.position, pointer.position + pointer.right   * len);
        Gizmos.color = Color.green;
        Gizmos.DrawLine(pointer.position, pointer.position + pointer.up      * len);
        Gizmos.color = Color.blue;
        Gizmos.DrawLine(pointer.position, pointer.position + pointer.forward * len);

        // In calibration mode: also draw raw ROS axes (X=red, Y=green, Z=blue)
        // as arrows FROM the pointer position, showing physical ROS directions
        if (calibrationMode)
        {
            float rawLen = gizmoRadius * 6f;
            // Show which Unity direction the CURRENT mapping sends each ROS axis to
            // by drawing a line in that mapped direction, coloured by ROS axis
            Gizmos.color = new Color(1, 0.3f, 0.3f, 0.6f);  // ROS X (right-map)
            Gizmos.DrawLine(pointer.position,
                pointer.position + transform.right * _rawRos.x * rawLen * 10f);
            Gizmos.color = new Color(0.3f, 1, 0.3f, 0.6f);  // ROS Y (up-map)
            Gizmos.DrawLine(pointer.position,
                pointer.position + (-transform.forward) * _rawRos.y * rawLen * 10f);
            Gizmos.color = new Color(0.3f, 0.3f, 1, 0.6f);  // ROS Z (norm-map)
            Gizmos.DrawLine(pointer.position,
                pointer.position + transform.up * _rawRos.z * rawLen * 10f);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    public void SetPhysicalSheetSize(float widthM, float heightM)
    {
        physicalSheetWidthM  = widthM;
        physicalSheetHeightM = heightM;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Custom Inspector — adds the "Save Reference" button
// ─────────────────────────────────────────────────────────────────────────────
#if UNITY_EDITOR
[UnityEditor.CustomEditor(typeof(FittsPointerSync))]
public class FittsPointerSyncEditor : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var t = (FittsPointerSync)target;
        if (!t.calibrationMode) return;

        UnityEditor.EditorGUILayout.Space(8);
        UnityEditor.EditorGUILayout.HelpBox(
            "CALIBRATION ACTIVE\n" +
            "1. Click 'Save Reference' to capture current raw values.\n" +
            "2. Move pointer in one known direction.\n" +
            "3. Watch the HUD — the axis with the largest |Δ| is the one you moved on.\n" +
            "4. Set the matching axisSheet* field to that ROS axis.\n" +
            "5. Repeat for Right, Up, and Norm (lift).",
            UnityEditor.MessageType.Info);

        if (Application.isPlaying)
        {
            if (GUILayout.Button("Save Reference  (freeze Δ baseline)", GUILayout.Height(32)))
                t.SaveCalibrationReference();
        }
        else
        {
            UnityEditor.EditorGUILayout.HelpBox("Enter Play mode to use Save Reference.",
                UnityEditor.MessageType.None);
        }
    }
}
#endif
