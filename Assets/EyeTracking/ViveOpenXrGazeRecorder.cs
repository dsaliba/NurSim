using System;
using UnityEngine;
using GazeData.Utils;
using VIVE.OpenXR;
using VIVE.OpenXR.EyeTracker;

namespace GazeData.EyeTracking
{
    /// <summary>
    /// Polls per-eye gaze via the VIVE OpenXR "Eye Tracker" feature (XR_HTC_eye_tracker
    /// extension) every frame and writes one CSV row per sample. (No SRanipal needed if used for ProEye).
    ///
    /// Requires: VIVE OpenXR package installed, and the "Eye Tracker" feature enabled under
    /// Project Settings > XR Plug-in Management > OpenXR.
    ///
    /// NOTE: the exact struct/field names below (XrSingleEyeGazeDataHTC.isValid/gazePose,
    /// XrSingleEyePupilDataHTC.pupilDiameter, etc.) are based on VIVE's published API
    /// description, not a compiled sample. If Unity reports a member name mismatch, open the
    /// struct definition (right-click the type > Go to Definition) and adjust the field names
    /// used in RecordSample() below — the rest of the script does not need to change.
    /// </summary>
    public class ViveOpenXrGazeRecorder : GazeSource
    {
        [Tooltip("Camera representing the HMD, used to convert local gaze poses to world space for the raycast column.")]
        [SerializeField] private Camera hmdCamera;

        [Tooltip("Layers that count as valid 'gaze target' hits (e.g. the Fitts targets).")]
        [SerializeField] private LayerMask gazeRaycastMask = ~0;

        [Tooltip("Max raycast distance in meters.")]
        [SerializeField] private float raycastDistance = 20f;

        [Header("Gaze ray visualization")]
        [Tooltip("Draw a visible line along the gaze direction while recording. Renders in the headset too, not just the Scene view.")]
        [SerializeField] private bool showGazeRay = true;
        [SerializeField] private Color gazeRayColor = Color.cyan;
        [Tooltip("How long to draw the line when the gaze isn't hitting anything.")]
        [SerializeField] private float noHitRayLength = 2f;

        private CsvLogger _logger;
        private bool _recording;
        private LineRenderer _gazeRayRenderer;

        public bool IsRecording => _recording;
        public string CurrentFilePath => _logger?.FilePath;

        // CurrentGazeHitObject / CurrentGazeWorldOrigin / CurrentGazeWorldDirection /
        // HasValidGaze are inherited from GazeSource, set below in RecordSample(). Updated
        // every recorded frame (null/false while not recording, no valid gaze, or nothing in
        // range) — read them from other scripts for real-time "what is being looked at"
        // feedback. Eye-tracker noise (~0.5-1.5 deg on Vive Pro Eye) can make small colliders
        // unreliable to hit precisely even while genuinely fixating on them; prefer the raw
        // ray over CurrentGazeHitObject if you need a custom angular-tolerance check instead.

        private void Awake()
        {
            if (hmdCamera == null) hmdCamera = Camera.main;
            if (showGazeRay) SetupGazeRayRenderer();
        }

        private void SetupGazeRayRenderer()
        {
            var rayObj = new GameObject("GazeRayVisual");
            rayObj.transform.SetParent(transform, false);
            _gazeRayRenderer = rayObj.AddComponent<LineRenderer>();
            _gazeRayRenderer.positionCount = 2;
            _gazeRayRenderer.startWidth = 0.004f;
            _gazeRayRenderer.endWidth = 0.004f;
            _gazeRayRenderer.material = new Material(Shader.Find("Sprites/Default"));
            _gazeRayRenderer.startColor = gazeRayColor;
            _gazeRayRenderer.endColor = gazeRayColor;
            _gazeRayRenderer.useWorldSpace = true;
            _gazeRayRenderer.enabled = false;
        }

        public void StartRecording(string sessionDirectory, string sessionId)
        {
            if (_recording) StopRecording();

            string header = string.Join(",",
                "unity_time_s", "wall_clock_iso",
                "left_valid", "left_pos_x", "left_pos_y", "left_pos_z",
                "left_rot_x", "left_rot_y", "left_rot_z", "left_rot_w",
                "right_valid", "right_pos_x", "right_pos_y", "right_pos_z",
                "right_rot_x", "right_rot_y", "right_rot_z", "right_rot_w",
                "left_pupil_diameter_mm", "right_pupil_diameter_mm",
                "gaze_hit_object", "gaze_hit_distance_m");

            _logger = new CsvLogger(sessionDirectory, $"gaze_{sessionId}.csv", header);
            _recording = true;
        }

        public void StopRecording()
        {
            if (!_recording) return;
            _logger?.Dispose();
            _logger = null;
            _recording = false;
            if (_gazeRayRenderer != null) _gazeRayRenderer.enabled = false;
        }

        private void Update()
        {
            if (!_recording) return;
            RecordSample();
        }

        private void RecordSample()
        {
            bool haveGaze = XR_HTC_eye_tracker.Interop.GetEyeGazeData(out XrSingleEyeGazeDataHTC[] gazes);
            bool havePupil = XR_HTC_eye_tracker.Interop.GetEyePupilData(out XrSingleEyePupilDataHTC[] pupils);

            XrSingleEyeGazeDataHTC left = haveGaze ? gazes[(int)XrEyePositionHTC.XR_EYE_POSITION_LEFT_HTC] : default;
            XrSingleEyeGazeDataHTC right = haveGaze ? gazes[(int)XrEyePositionHTC.XR_EYE_POSITION_RIGHT_HTC] : default;

            // pupilDiameter comes back in meters (e.g. 0.003 for a 3mm pupil), not mm despite
            // the field name — convert here so the logged column matches its "_mm" header.
            float leftPupil = havePupil ? pupils[(int)XrEyePositionHTC.XR_EYE_POSITION_LEFT_HTC].pupilDiameter * 1000f : -1f;
            float rightPupil = havePupil ? pupils[(int)XrEyePositionHTC.XR_EYE_POSITION_RIGHT_HTC].pupilDiameter * 1000f : -1f;

            string hitName = "";
            float hitDistance = -1f;
            CurrentGazeHitObject = null;
            HasValidGaze = haveGaze && hmdCamera != null && (left.isValid || right.isValid);

            if (HasValidGaze)
            {
                // gazePose comes back in the same world/tracking space as the camera, NOT as a
                // small eye-within-head offset relative to the HMD. Evidence: gazePose.position
                // is ~1m+ in Y — roughly the participant's real eye height above the floor, not a
                // few-cm local offset. Because it's already world space:
                //  - position: don't TransformPoint() it as a camera-local offset (that double-counts
                //    the camera's own height). The camera's real tracked position is a fine stand-in
                //    for eye origin at the distances this project works at (target dots ~1-2m away).
                //  - orientation: don't TransformDirection() it through the camera either — that was
                //    the actual bug here. Doing so re-applies the head's current rotation on top of a
                //    rotation that already includes head+eye, so the ray direction compounds with head
                //    movement and the independent eye component gets swamped, making the ray look like
                //    it only follows head turns. Use the world-space rotation directly instead.
                // gazePose.orientation is a raw OpenXR quaternion: right-handed, -Z forward. Unity is
                // left-handed, +Z forward. Passing the x/y/z/w straight into a UnityEngine.Quaternion
                // without converting handedness mirrors the rotation, flipping the sign of pitch and
                // yaw (roll is unaffected) — e.g. looking down swings the ray up. Negating x and y
                // converts the OpenXR-space quaternion into the equivalent Unity-space one.
                //
                // This SDK build's XrEyePositionHTC has no combined/binocular entry, so approximate
                // one ourselves: average (slerp 50/50) the left/right rotations when both are valid,
                // instead of using either eye alone. A single eye's own convergence angle differs
                // from the true two-eye fixation point (it rotates nasally to converge on a centered
                // target), so using it alone while still originating the ray at head-center reads as
                // a systematic left/right + vertical offset - which is what using left-only produced.
                Quaternion gazeRot;
                if (left.isValid && right.isValid)
                {
                    Quaternion leftRot = new Quaternion(-left.gazePose.orientation.x, -left.gazePose.orientation.y, left.gazePose.orientation.z, left.gazePose.orientation.w);
                    Quaternion rightRot = new Quaternion(-right.gazePose.orientation.x, -right.gazePose.orientation.y, right.gazePose.orientation.z, right.gazePose.orientation.w);
                    gazeRot = Quaternion.Slerp(leftRot, rightRot, 0.5f);
                }
                else
                {
                    XrSingleEyeGazeDataHTC gaze = left.isValid ? left : right;
                    gazeRot = new Quaternion(-gaze.gazePose.orientation.x, -gaze.gazePose.orientation.y, gaze.gazePose.orientation.z, gaze.gazePose.orientation.w);
                }
                Vector3 worldOrigin = hmdCamera.transform.position;
                Vector3 worldDir = gazeRot * Vector3.forward;
                CurrentGazeWorldOrigin = worldOrigin;
                CurrentGazeWorldDirection = worldDir;
                float rayLength = noHitRayLength;
                if (Physics.Raycast(worldOrigin, worldDir, out RaycastHit hit, raycastDistance, gazeRaycastMask))
                {
                    hitName = hit.collider.name;
                    hitDistance = hit.distance;
                    CurrentGazeHitObject = hit.collider.gameObject;
                    rayLength = hit.distance;
                }

                if (_gazeRayRenderer != null)
                {
                    _gazeRayRenderer.enabled = true;
                    _gazeRayRenderer.SetPosition(0, worldOrigin);
                    _gazeRayRenderer.SetPosition(1, worldOrigin + worldDir * rayLength);
                }
            }
            else if (_gazeRayRenderer != null)
            {
                _gazeRayRenderer.enabled = false;
            }

            _logger.WriteRow(
                Time.unscaledTime, DateTime.UtcNow.ToString("O"),
                left.isValid, left.gazePose.position.x, left.gazePose.position.y, left.gazePose.position.z,
                left.gazePose.orientation.x, left.gazePose.orientation.y, left.gazePose.orientation.z, left.gazePose.orientation.w,
                right.isValid, right.gazePose.position.x, right.gazePose.position.y, right.gazePose.position.z,
                right.gazePose.orientation.x, right.gazePose.orientation.y, right.gazePose.orientation.z, right.gazePose.orientation.w,
                leftPupil, rightPupil,
                hitName, hitDistance);
        }

        private void OnDestroy() => StopRecording();
        private void OnApplicationQuit() => StopRecording();
    }
}
