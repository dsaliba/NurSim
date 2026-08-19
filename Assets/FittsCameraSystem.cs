// FittsCameraSystem.cs
// ============================================================
// Emulates the fitts_cameras.launch ROS pipeline in Unity using
// Unity cameras instead of physical /dev/videoN devices.
//
// Requires: Unity Robotics Hub — ROS TCP Connector
//   com.unity.robotics.ros-tcp-connector
//   https://github.com/Unity-Technologies/ROS-TCP-Connector.git
//     ?path=/com.unity.robotics.ros-tcp-connector
//
// ROS node                     Unity equivalent
// ────────────────────────────────────────────────────────────
// realsense_opencv_publisher → Renders Unity cameras to
//                              RenderTextures at target FPS
// camera_view_selector       → SetActiveView() driven by
//                              /unity/camera_selection subscriber
// camera_streamer_node       → Handled externally by the real
//                              ROS node; this script publishes
//                              to /box_camera/… for it to pick up
// box_camera_port_announcer  → Not needed; Unity's CameraStreamer
//                              component still owns port assignment
//
// DATA FLOW
//   1. Unity subscribes to /unity/camera_selection (std_msgs/String)
//      → "Front" | "Back" | "Side" (case-insensitive)
//   2. Selected Unity camera is rendered each frame at captureFps.
//   3. Frame is rotated / flipped and published as sensor_msgs/Image
//      to /box_camera/camera/color/image_raw  (encoding: rgb8)
//      and sensor_msgs/CameraInfo to
//            /box_camera/camera/color/camera_info
//      These topics are recorded by a ROS bag AND consumed by
//      the existing camera_streamer_node, which forwards them to
//      Unity's CameraReceiver via UDP (fragmented protocol
//      [4B frame_id | 2B total_frags | 2B frag_idx | payload]).
//
// SETUP
//   1. Attach this component to any GameObject.
//   2. In the Inspector set Cameras[0..2]:
//        label       → "Front" / "Back" / "Side"
//        unityCamera → drag in your 3 Unity cameras
//        rotationDeg → 0 / 90 / 180 / 270  (clockwise)
//        fx/fy/cx/cy → intrinsics (written into camera_info)
//   3. Configure ROSConnection (separate component / menu) to
//      point at your ROS master / ROS TCP endpoint.
//   4. Optionally assign a UI RawImage to Preview Image.
//
// VIEW SWITCHING (runtime)
//   ROS:     rostopic pub /unity/camera_selection std_msgs/String \
//                "data: 'Front'"
//   Code:    GetComponent<FittsCameraSystem>().SetActiveView("Back");
//   UI:      Wire SelectFront() / SelectBack() / SelectSide() buttons.
//   Editor:  Right-click the component → Select Front / Back / Side.
// ============================================================

using System;
using System.Net;
using System.Net.Sockets;
using UnityEngine;
using UnityEngine.UI;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;
using RosMessageTypes.Sensor;
using RosMessageTypes.BuiltinInterfaces;

[AddComponentMenu("Fitts/Camera System")]
public class FittsCameraSystem : MonoBehaviour
{
    // ──────────────────────────────────────────────────────────
    // Inspector types
    // ──────────────────────────────────────────────────────────

    [Serializable]
    public class CameraEntry
    {
        [Tooltip("Logical view name used for selection: Front | Back | Side\n" +
                 "(case-insensitive match against /unity/camera_selection values)")]
        public string label = "Front";

        [Tooltip("Unity Camera to capture — equivalent to a /dev/videoN source.\n" +
                 "Its targetTexture is overwritten at Start.")]
        public Camera unityCamera;

        [Tooltip("Clockwise rotation applied before publishing (0 / 90 / 180 / 270).\n" +
                 "Mirrors rotation_deg in the launch file.")]
        [Range(0, 270)]
        public int rotationDeg = 0;

        [Header("Intrinsics — written to sensor_msgs/CameraInfo")]
        [Tooltip("Focal length X in pixels  (mirrors fx in launch file)")]
        public float fx = 613.8f;
        [Tooltip("Focal length Y in pixels  (mirrors fy in launch file)")]
        public float fy = 613.8f;
        [Tooltip("Principal point X in pixels  (mirrors cx in launch file)")]
        public float cx = 318.9f;
        [Tooltip("Principal point Y in pixels  (mirrors cy in launch file)")]
        public float cy = 239.2f;

        // Runtime — not serialised
        [NonSerialized] public RenderTexture rt;
        [NonSerialized] public Texture2D     cpuTex;
    }

    // ──────────────────────────────────────────────────────────
    // Inspector fields
    // ──────────────────────────────────────────────────────────

    [Header("Cameras  (cam0 = Front · cam1 = Back · cam2 = Side)")]
    public CameraEntry[] cameras = new CameraEntry[]
    {
        new CameraEntry { label = "Front" },
        new CameraEntry { label = "Back"  },
        new CameraEntry { label = "Side"  },
    };

    [Header("Capture Settings")]
    [Tooltip("Published image width in pixels  (mirrors camera_width in launch file)")]
    public int captureWidth  = 640;
    [Tooltip("Published image height in pixels  (mirrors camera_height in launch file)")]
    public int captureHeight = 480;
    [Tooltip("Target publish rate in frames per second  (mirrors camera_fps)")]
    public int captureFps    = 15;
    [Range(1, 100)]
    [Tooltip("JPEG quality for the local UDP stream sent to CameraReceiver.\n" +
             "Mirrors jpeg_quality in the launch file.")]
    public int jpegQuality   = 75;

    [Header("ROS Topics")]
    [Tooltip("Topic this node subscribes to for view-selection commands.\n" +
             "Publishes std_msgs/String: 'Front' | 'Back' | 'Side'")]
    public string selectionTopic       = "/unity/camera_selection";
    [Tooltip("Topic this node publishes sensor_msgs/Image to.\n" +
             "Matches /box_camera/camera/color/image_raw in the launch file.\n" +
             "Consumed by camera_streamer_node and recorded by rosbag.")]
    public string outputImageTopic     = "/box_camera/camera/color/image_raw";
    [Tooltip("Topic this node publishes sensor_msgs/CameraInfo to.\n" +
             "Matches /box_camera/camera/color/camera_info in the launch file.")]
    public string outputCameraInfoTopic = "/box_camera/camera/color/camera_info";
    [Tooltip("TF frame_id written into message headers")]
    public string frameId              = "box_camera";
    [Tooltip("Initial view active on Start  (mirrors mux_initial in launch file).\n" +
             "Only used if no latched message arrives within latchWaitSeconds.")]
    public string initialView          = "front";
    [Tooltip("Seconds to wait for a latched /unity/camera_selection message before\n" +
             "falling back to initialView. A latched publisher replays its last value\n" +
             "to new subscribers, so this covers session-resume cases where a view\n" +
             "was already selected before Unity started. Increase on slow connections.")]
    public float  latchWaitSeconds     = 0.5f;

    [Header("Local UDP Stream  ←→  CameraReceiver (same scene)")]
    [Tooltip("Must match cameraKey on the target CameraReceiver component.\n" +
             "Published as '<localCameraKey>:<localStreamPort>' on cameraPortsTopic\n" +
             "so CameraReceiver knows which UDP port to bind.")]
    public string localCameraKey       = "box_camera";
    [Tooltip("UDP port to send JPEG frames to on localhost.\n" +
             "CameraReceiver's SharedUdpListener will bind this port after receiving\n" +
             "the port announcement. Set to 0 to disable local UDP streaming.")]
    public int    localStreamPort      = 5415;
    [Tooltip("Topic FittsCameraSystem publishes port assignments to.\n" +
             "CameraReceiver subscribes to '/unity/camera_ports' by default;\n" +
             "this value must match that subscription.")]
    public string cameraPortsTopic     = "/unity/camera_ports";
    [Tooltip("How many times to re-publish the port announcement at startup.\n" +
             "Repeated publishing compensates for ROS TCP Connector not supporting\n" +
             "latched publishers from Unity — at least one publish must land after\n" +
             "CameraReceiver's ROS subscription is registered.")]
    public int    portAnnounceRepeats  = 5;
    [Tooltip("Seconds between each port re-announcement.")]
    public float  portAnnounceInterval = 1f;

    [Header("Optional Debug Preview")]
    [Tooltip("Assign a UI RawImage to display the selected camera feed in the scene.")]
    public RawImage previewImage;

    // ──────────────────────────────────────────────────────────
    // Public read-only state
    // ──────────────────────────────────────────────────────────

    /// <summary>Label of the currently active camera ("Front", "Back", or "Side").</summary>
    public string ActiveLabel =>
        (_activeIndex >= 0 && _activeIndex < cameras.Length)
            ? cameras[_activeIndex].label : "None";

    /// <summary>Index into <see cref="cameras"/> for the active entry.</summary>
    public int ActiveIndex => _activeIndex;

    // ──────────────────────────────────────────────────────────
    // Private state
    // ──────────────────────────────────────────────────────────

    int        _activeIndex = 0;
    float      _nextCapture;
    float      _interval;
    uint       _seqNum = 0;
    ROSConnection _ros;
    bool       _selectionReceived = false; // set true when any ROS message arrives

    // Local UDP stream → CameraReceiver
    UdpClient  _localUdp;
    uint       _udpFrameId = 0;
    const int  k_UdpMtu   = 60000; // matches camera_streamer_node MTU constant

    // Epoch for Unix timestamp calculation
    static readonly DateTime k_Epoch =
        new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ──────────────────────────────────────────────────────────
    // Lifecycle
    // ──────────────────────────────────────────────────────────

    void Start()
    {
        _interval = 1f / Mathf.Max(1, captureFps);

        // Create RenderTextures for each camera
        for (int i = 0; i < cameras.Length; i++)
        {
            var e = cameras[i];
            if (e.unityCamera == null)
            {
                Debug.LogWarning(
                    $"[FittsCameraSystem] cameras[{i}] ({e.label}) has no Camera assigned.");
                continue;
            }

            e.rt = new RenderTexture(
                captureWidth, captureHeight, 24, RenderTextureFormat.ARGB32);
            e.rt.antiAliasing = 1;
            e.rt.Create();

            e.unityCamera.targetTexture = e.rt;
            e.unityCamera.enabled       = false;   // driven on-demand

            // RGB24: 3 bytes per pixel, no alpha, avoids a conversion step
            e.cpuTex = new Texture2D(
                captureWidth, captureHeight, TextureFormat.RGB24, false);
        }

        // ── ROS wiring ──────────────────────────────────────────
        _ros = ROSConnection.GetOrCreateInstance();

        // Register publishers (idempotent if already registered)
        _ros.RegisterPublisher<ImageMsg>(outputImageTopic);
        _ros.RegisterPublisher<CameraInfoMsg>(outputCameraInfoTopic);

        // Subscribe BEFORE starting the latch-wait timer so that any latched
        // message the ROS master replays to us (because the publisher used
        // latch=True) is delivered and sets _selectionReceived = true before
        // the coroutine's WaitForSecondsRealtime expires.
        // ROSConnection dispatches subscriber callbacks on the Unity main
        // thread (via its internal Update queue), so SetActiveView() is
        // safe to call directly — no cross-thread lock needed.
        _ros.Subscribe<StringMsg>(selectionTopic, OnCameraSelection);

        Debug.Log($"[FittsCameraSystem] ROS publishers: {outputImageTopic}, " +
                  $"{outputCameraInfoTopic}");
        Debug.Log($"[FittsCameraSystem] ROS subscriber: {selectionTopic}");

        // ── Local UDP stream → in-scene CameraReceiver ──────────
        // Publishes "{localCameraKey}:{localStreamPort}" to cameraPortsTopic
        // so CameraReceiver's static OnPortAnnouncementStatic callback routes
        // the port to whichever instance has a matching cameraKey, causing its
        // SharedUdpListener to bind localStreamPort. FittsCameraSystem then
        // sends each JPEG frame to that port on loopback using the same
        // 8-byte fragmented header that camera_streamer_node uses, so
        // CameraReceiver's existing reassembly path works without modification.
        if (localStreamPort > 0)
        {
            _ros.RegisterPublisher<StringMsg>(cameraPortsTopic);
            _localUdp = new UdpClient();
            StartCoroutine(AnnounceLocalPort());
            Debug.Log($"[FittsCameraSystem] Local UDP stream → 127.0.0.1:{localStreamPort} " +
                      $"(key: {localCameraKey})");
        }

        // Wait briefly for any latched selection; fall back to initialView only
        // if nothing arrives. This handles session-resume correctly: if a view
        // was already selected before Unity started (latched on the topic),
        // we honour that instead of overriding it with the default.
        StartCoroutine(ApplyInitialViewAfterLatchWindow());
    }

    void Update()
    {
        if (Time.unscaledTime >= _nextCapture)
        {
            _nextCapture = Time.unscaledTime + _interval;
            CaptureAndPublish();
        }
    }

    void OnDestroy()
    {
        _localUdp?.Close();
        foreach (var e in cameras)
        {
            if (e.rt     != null) { e.rt.Release(); Destroy(e.rt); }
            if (e.cpuTex != null) Destroy(e.cpuTex);
        }
    }

    // ──────────────────────────────────────────────────────────
    // View selector  ←→  camera_view_selector.py + mux
    //
    // Triggered by /unity/camera_selection (std_msgs/String).
    // Accepts "Front", "Back", "Side" (case-insensitive) —
    // same values used in the original launch-file pipeline.
    // ──────────────────────────────────────────────────────────

    void OnCameraSelection(StringMsg msg)
    {
        _selectionReceived = true;
        SetActiveView(msg.data);
    }

    // Waits latchWaitSeconds for a latched /unity/camera_selection message.
    // If one arrives (OnCameraSelection fires), it has already called
    // SetActiveView and we do nothing. If the window expires with no message,
    // we apply initialView as the safe default.
    System.Collections.IEnumerator ApplyInitialViewAfterLatchWindow()
    {
        yield return new WaitForSecondsRealtime(latchWaitSeconds);

        if (!_selectionReceived)
        {
            Debug.Log($"[FittsCameraSystem] No latched selection received within " +
                      $"{latchWaitSeconds}s — falling back to initialView: '{initialView}'");
            SetActiveView(initialView);
        }
        else
        {
            Debug.Log($"[FittsCameraSystem] Latched selection applied — " +
                      $"skipping initialView ('{initialView}')");
        }
    }

    /// <summary>
    /// Switch the active camera by label name (case-insensitive).
    /// Safe to call from UI buttons, other scripts, or ROS callbacks.
    /// </summary>
    public void SetActiveView(string viewName)
    {
        string lo = viewName.Trim().ToLowerInvariant();

        for (int i = 0; i < cameras.Length; i++)
        {
            if (cameras[i].label.ToLowerInvariant() != lo) continue;

            _activeIndex = i;
            Debug.Log($"[FittsCameraSystem] Active view → {cameras[i].label} (cam{i})");

            if (previewImage != null && cameras[i].rt != null)
                previewImage.texture = cameras[i].rt;

            return;
        }
        Debug.LogWarning($"[FittsCameraSystem] Unknown view '{viewName}' — no change.");
    }

    // Convenience methods — wire to UI Buttons in the Inspector
    [ContextMenu("Select Front")] public void SelectFront() => SetActiveView("Front");
    [ContextMenu("Select Back")]  public void SelectBack()  => SetActiveView("Back");
    [ContextMenu("Select Side")]  public void SelectSide()  => SetActiveView("Side");

    // ──────────────────────────────────────────────────────────
    // Capture + publish  ←→  realsense_opencv_publisher +
    //                        camera_view_selector (mux output)
    //
    // Publishes to /box_camera/camera/color/image_raw so that:
    //   a) The rosbag records it for experiment replay.
    //   b) The existing camera_streamer_node picks it up and
    //      forwards it to Unity's CameraReceiver via fragmented
    //      UDP [4B frame_id | 2B total_frags | 2B frag_idx | payload].
    // ──────────────────────────────────────────────────────────

    void CaptureAndPublish()
    {
        if (_activeIndex < 0 || _activeIndex >= cameras.Length) return;
        var e = cameras[_activeIndex];
        if (e.unityCamera == null || e.rt == null || e.cpuTex == null) return;

        // 1 ── Render the selected Unity camera
        e.unityCamera.Render();

        // 2 ── GPU → CPU readback
        var prevRT = RenderTexture.active;
        RenderTexture.active = e.rt;
        e.cpuTex.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0, false);
        e.cpuTex.Apply();
        RenderTexture.active = prevRT;

        // 3 ── Apply clockwise rotation (mirrors rotation_deg)
        Texture2D rotated = ApplyRotation(e.cpuTex, e.rotationDeg);

        // 4a ── JPEG encode for local UDP stream, while rotated is still alive.
        //       EncodeToJPG treats the texture as bottom-up (Unity/OpenGL) and
        //       produces a standard top-down JPEG. CameraReceiver's LoadImage
        //       reverses that automatically, so the displayed image is upright.
        //       This must happen BEFORE the Destroy(rotated) call below.
        byte[] udpJpeg = (_localUdp != null && localStreamPort > 0)
            ? rotated.EncodeToJPG(jpegQuality)
            : null;

        // 4b ── Build raw RGB byte array in ROS row-major / top-down order.
        //      Unity ReadPixels stores rows bottom-up (OpenGL convention);
        //      sensor_msgs/Image expects rows top-down (OpenCV convention).
        //      We flip vertically here during the pixel copy.
        int     w    = rotated.width;
        int     h    = rotated.height;
        Color32[] px = rotated.GetPixels32();
        byte[]  data = new byte[w * h * 3];  // encoding: rgb8

        for (int row = 0; row < h; row++)
        {
            int srcRow = row;         // Unity: 0 = bottom
            int dstRow = h - 1 - row; // ROS:   0 = top
            for (int col = 0; col < w; col++)
            {
                Color32 c   = px[srcRow * w + col];
                int     idx = (dstRow * w + col) * 3;
                data[idx]     = c.r;
                data[idx + 1] = c.g;
                data[idx + 2] = c.b;
            }
        }

        if (rotated != e.cpuTex) Destroy(rotated);

        // 5 ── Build ROS header with Unix wall-clock timestamp
        var    now   = DateTime.UtcNow - k_Epoch;
        uint   sec   = (uint)now.TotalSeconds;
        uint   nsec  = (uint)((now.TotalSeconds - sec) * 1_000_000_000.0);
        var    stamp = new TimeMsg(sec, nsec);
        var    hdr   = new HeaderMsg(_seqNum, stamp, frameId);

        // 6 ── Publish sensor_msgs/Image
        //      step = full row length in bytes = width * 3 (rgb8)
        var imageMsg = new ImageMsg(
            hdr,
            (uint)h,
            (uint)w,
            "rgb8",
            0,            // is_bigendian = false (little-endian host)
            (uint)(w * 3),
            data
        );
        _ros.Publish(outputImageTopic, imageMsg);

        // 7 ── Publish sensor_msgs/CameraInfo  (same header / timestamp)
        _ros.Publish(outputCameraInfoTopic, BuildCameraInfo(hdr, e, w, h));

        // 8 ── Send via local fragmented UDP → CameraReceiver
        if (udpJpeg != null)
            SendFragmented(udpJpeg);

        _seqNum++;
    }

    // ──────────────────────────────────────────────────────────
    // CameraInfo builder  ←→  camera_info fields in opencv publisher
    //
    // Builds a plumb_bob (no distortion) CameraInfo from the
    // per-camera intrinsics configured in the Inspector.
    // ──────────────────────────────────────────────────────────

    CameraInfoMsg BuildCameraInfo(HeaderMsg header, CameraEntry e, int w, int h)
    {
        // 3×3 intrinsic matrix K (row-major)
        double[] K = new double[9]
        {
            e.fx,  0.0,  e.cx,
            0.0,   e.fy, e.cy,
            0.0,   0.0,  1.0,
        };

        // 3×3 rectification matrix R (identity — single camera)
        double[] R = new double[9]
        {
            1.0, 0.0, 0.0,
            0.0, 1.0, 0.0,
            0.0, 0.0, 1.0,
        };

        // 3×4 projection matrix P
        double[] P = new double[12]
        {
            e.fx,  0.0,  e.cx,  0.0,
            0.0,   e.fy, e.cy,  0.0,
            0.0,   0.0,  1.0,   0.0,
        };

        return new CameraInfoMsg(
            header,
            (uint)h,
            (uint)w,
            "plumb_bob",       // distortion model (no distortion)
            new double[0],     // D — empty = no distortion coefficients
            K, R, P,
            0, 0,              // binning_x, binning_y
            new RegionOfInterestMsg(0, 0, 0, 0, false)
        );
    }

    // ──────────────────────────────────────────────────────────
    // Local UDP stream helpers
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Sends JPEG bytes to <see cref="localStreamPort"/> on loopback using
    /// the exact same fragmented header layout as camera_streamer_node:
    ///
    ///   [0..3]  frame_id     uint32  big-endian  (matches htonl)
    ///   [4..5]  total_frags  uint16  big-endian  (matches htons)
    ///   [6..7]  frag_idx     uint16  big-endian  (matches htons)
    ///   [8..]   JPEG payload
    ///
    /// CameraReceiver's SharedUdpListener reads these fields with
    /// IPAddress.NetworkToHostOrder, so the byte order must be big-endian.
    /// Header bytes are written manually to avoid BitConverter endian
    /// dependence on the host platform.
    /// </summary>
    void SendFragmented(byte[] jpeg)
    {
        const int maxPayload = k_UdpMtu - 8;
        int       total      = (jpeg.Length + maxPayload - 1) / maxPayload;
        var       ep         = new IPEndPoint(IPAddress.Loopback, localStreamPort);

        for (int i = 0; i < total; i++)
        {
            int    off    = i * maxPayload;
            int    payLen = Mathf.Min(maxPayload, jpeg.Length - off);
            byte[] pkt    = new byte[8 + payLen];

            // Big-endian header — matches camera_streamer_node htonl/htons
            uint fid = _udpFrameId;
            pkt[0] = (byte)(fid   >> 24); pkt[1] = (byte)(fid   >> 16);
            pkt[2] = (byte)(fid   >>  8); pkt[3] = (byte) fid;
            pkt[4] = (byte)(total >>  8); pkt[5] = (byte) total;
            pkt[6] = (byte)(i     >>  8); pkt[7] = (byte) i;

            Buffer.BlockCopy(jpeg, off, pkt, 8, payLen);

            try   { _localUdp.Send(pkt, pkt.Length, ep); }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[FittsCameraSystem] UDP frag {i}/{total}: {ex.Message}");
            }
        }
        _udpFrameId++;
    }

    /// <summary>
    /// Re-publishes "{localCameraKey}:{localStreamPort}" to cameraPortsTopic
    /// <see cref="portAnnounceRepeats"/> times, spaced
    /// <see cref="portAnnounceInterval"/> seconds apart.
    ///
    /// Repeated publishing is necessary because Unity's ROS TCP Connector
    /// does not support latched publishers from the Unity side. At least one
    /// publish must land after CameraReceiver's static subscription to
    /// /unity/camera_ports is registered, which happens in its OnEnable.
    /// If CameraReceiver enables after FittsCameraSystem.Start() (common in
    /// scene-load order), the repeats ensure it still receives the port.
    /// </summary>
    System.Collections.IEnumerator AnnounceLocalPort()
    {
        var msg = new StringMsg($"{localCameraKey}:{localStreamPort}");
        for (int i = 0; i < portAnnounceRepeats; i++)
        {
            _ros.Publish(cameraPortsTopic, msg);
            Debug.Log($"[FittsCameraSystem] → {cameraPortsTopic}: " +
                      $"{msg.data}  ({i + 1}/{portAnnounceRepeats})");
            yield return new WaitForSecondsRealtime(portAnnounceInterval);
        }
    }

    // ──────────────────────────────────────────────────────────
    // Image rotation  ←→  rotation_deg in realsense_opencv_publisher
    //
    // Applies a clockwise rotation, matching OpenCV's convention:
    //   cv2.ROTATE_90_CLOCKWISE        → rotationDeg = 90
    //   cv2.ROTATE_180                 → rotationDeg = 180
    //   cv2.ROTATE_90_COUNTERCLOCKWISE → rotationDeg = 270
    //
    // Returns src unchanged (no copy) when deg == 0.
    // Returns a new Texture2D otherwise — caller must Destroy() it.
    // ──────────────────────────────────────────────────────────

    Texture2D ApplyRotation(Texture2D src, int deg)
    {
        deg = ((deg % 360) + 360) % 360;
        if (deg == 0) return src;

        bool   swap = (deg == 90 || deg == 270);
        int    sw   = src.width,  sh = src.height;
        int    dw   = swap ? sh : sw;
        int    dh   = swap ? sw : sh;

        Color[] sp  = src.GetPixels();
        Color[] dp  = new Color[dw * dh];

        for (int y = 0; y < sh; y++)
        for (int x = 0; x < sw; x++)
        {
            Color c = sp[y * sw + x];
            int   dx, dy;
            switch (deg)
            {
                case  90:  dx = sh - 1 - y;  dy = x;           break; // CW 90
                case 180:  dx = sw - 1 - x;  dy = sh - 1 - y;  break; // 180°
                default:   dx = y;            dy = sw - 1 - x;  break; // CW 270
            }
            dp[dy * dw + dx] = c;
        }

        var dst = new Texture2D(dw, dh, TextureFormat.RGB24, false);
        dst.SetPixels(dp);
        dst.Apply();
        return dst;
    }

    // ──────────────────────────────────────────────────────────
    // Editor helpers
    // ──────────────────────────────────────────────────────────

#if UNITY_EDITOR
    void OnValidate()
    {
        // Snap rotation to nearest valid step (0, 90, 180, 270)
        foreach (var e in cameras)
            e.rotationDeg = Mathf.Clamp((e.rotationDeg / 90) * 90, 0, 270);
    }
#endif
}
