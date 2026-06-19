using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Sensor;
using RosMessageTypes.Std;

/// <summary>
/// Attaches to a Camera. Streams its feed either:
///   (a) as a ROS sensor_msgs/Image or CompressedImage topic via ROS-TCP-Connector, OR
///   (b) directly to a CameraReceiver in the same Unity process (Unity-to-Unity shortcut), OR
///   (c) over UDP (chunked JPEG) to a remote Unity CameraReceiver.
///
/// URP-compatible: uses RenderPipelineManager.endCameraRendering instead of OnPostRender.
///
/// Port assignment is now a STATIC, FIXED mapping for the entire Unity
/// session: each distinct cameraKey is deterministically assigned one port
/// out of 5000-5500 the first time ANY CameraStreamer with that key
/// initializes, and that mapping is never changed, released, or
/// reassigned -- not on disable, not on destroy, not on re-enable. The same
/// cameraKey always gets the same port for the lifetime of the session.
///
/// This replaces an earlier scheme where ports were dynamically claimed on
/// enable and released on disable. That approach kept producing collisions
/// under disable/enable churn (multiple cameras toggling, racing to
/// re-scan-and-claim from PORT_MIN), because the thing being raced was
/// reassignment itself. Removing reassignment removes the race: there is
/// now exactly one allocation event per cameraKey per session, decided once
/// and left alone.
/// </summary>
[RequireComponent(typeof(Camera))]
public class CameraStreamer : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector parameters
    // -------------------------------------------------------------------------
    [Header("Camera Identity")]
    [Tooltip("Unique key used to identify this camera (e.g. 'front_camera').")]
    public string cameraKey = "front_camera";

    [Tooltip("ROS topic name for publishing (used when not in Unity-to-Unity mode).")]
    public string rosTopic = "/camera/front/image_raw";

    [Header("Stream Settings")]
    [Range(1, 60)]
    public int targetFPS = 30;

    [Tooltip("Downscale the render texture before sending. 1 = full camera resolution.")]
    [Range(0.1f, 1f)]
    public float resolutionScale = 1f;

    [Tooltip("Fixed width override (0 = use camera width x resolutionScale).")]
    public int overrideWidth = 0;

    [Tooltip("Fixed height override (0 = use camera height x resolutionScale).")]
    public int overrideHeight = 0;

    [Header("Compression")]
    public bool useCompressedImage = true;

    [Range(1, 100)]
    public int jpegQuality = 75;

    [Header("Unity-to-Unity Optimisation")]
    [Tooltip("When enabled, frames are delivered directly to a local CameraReceiver " +
             "without any serialisation or UDP. Ideal when streamer and receiver are " +
             "in the same Unity process.")]
    public bool unityToUnityMode = false;

    // -------------------------------------------------------------------------
    // Static port manager (shared across all CameraStreamer instances)
    // -------------------------------------------------------------------------
    private static readonly object _portLock = new object();

    // Fixed for the entire session: cameraKey -> port. Populated once per
    // key (the first time a CameraStreamer with that key initializes) and
    // never removed from, never overwritten, never reassigned -- including
    // across disable/enable and across destroying and re-instantiating a
    // GameObject with the same cameraKey (e.g. a scene reload). This is the
    // single source of truth for "what port does camera X use," for the
    // entire lifetime of the Unity process.
    private static readonly Dictionary<string, int> _sessionPortAssignments =
        new Dictionary<string, int>(StringComparer.Ordinal);

    private const int PORT_MIN = 5000;
    private const int PORT_MAX = 5500;

    /// <summary>All live (enabled) streamers: key -> streamer. Used by CameraReceiver for direct lookup.</summary>
    public static readonly Dictionary<string, CameraStreamer> ActiveStreamers =
        new Dictionary<string, CameraStreamer>(StringComparer.Ordinal);

    private static ROSConnection _ros;

    // -------------------------------------------------------------------------
    // Instance state
    // -------------------------------------------------------------------------
    private Camera _cam;
    private RenderTexture _rt;
    private Texture2D _readbackTex;
    private int _assignedPort = -1;
    private float _frameInterval;
    private float _nextFrameTime;

    // Direct-mode subscribers
    private readonly List<CameraReceiver> _directReceivers = new List<CameraReceiver>();

    // URP frame capture gate -- set in Update, consumed in endCameraRendering callback
    private bool _captureThisFrame = false;

    private CancellationTokenSource _cts;

    // True once Start() has run (render texture + ROS singleton exist).
    // OnEnable can fire before Start on the very first activation, so port
    // assignment there is deferred until Start finishes; every subsequent
    // OnEnable (after a disable/re-enable cycle) can assign immediately.
    private bool _initialized = false;

    // -------------------------------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------------------------------
    void Awake()
    {
        _cam = GetComponent<Camera>();
        _cts = new CancellationTokenSource();
    }

    void Start()
    {
        // Static ROS connection (singleton)
        if (_ros == null)
            _ros = ROSConnection.GetOrCreateInstance();

        AllocateRenderTexture();

        _frameInterval = 1f / Mathf.Max(1, targetFPS);
        _nextFrameTime = Time.time;
        _initialized = true;

        // OnEnable ran before Start on first activation; finish the job now
        // that the render texture and ROS singleton actually exist.
        AssignPortAndPublish();
    }

    void OnEnable()
    {
        RegisterStreamer();
        // URP: hook into the render pipeline's post-camera event
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;

        // Re-enable after a disable: the port NUMBER for this cameraKey was
        // decided once for the whole session and never changes (see
        // AssignPort). Re-publishing here just re-announces it on
        // /unity/camera_ports for any ROS listener that (re)started while
        // we were disabled. On the very first enable, Start() hasn't run
        // yet, so this is a no-op here and happens at the end of Start()
        // instead.
        if (_initialized)
            AssignPortAndPublish();
    }

    void OnDisable()
    {
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        UnregisterStreamer();

        // Nothing to release here anymore: CameraStreamer doesn't hold a
        // socket. _sessionPortAssignments[cameraKey] stays exactly as it
        // is, forever, for the rest of the session, and _assignedPort is
        // intentionally left as-is (not reset to -1) since it still
        // correctly reflects this camera's permanent port number even
        // while disabled.
    }

    void OnDestroy()
    {
        _cts.Cancel();
        UnregisterStreamer();
        // The port number mapping for this cameraKey is permanent for the
        // session and is NOT released here. If a new CameraStreamer with
        // the same cameraKey is instantiated later (e.g. scene reload),
        // AssignPort will find the existing mapping in
        // _sessionPortAssignments and reuse the same number.
        if (_rt != null) { _rt.Release(); Destroy(_rt); }
        if (_readbackTex != null) Destroy(_readbackTex);
    }

    // -------------------------------------------------------------------------
    // Initialisation helpers
    // -------------------------------------------------------------------------
    private void RegisterStreamer()
    {
        lock (ActiveStreamers)
            ActiveStreamers[cameraKey] = this;
    }

    private void UnregisterStreamer()
    {
        lock (ActiveStreamers)
        {
            if (ActiveStreamers.TryGetValue(cameraKey, out var s) && s == this)
                ActiveStreamers.Remove(cameraKey);
        }
    }

    private void AllocateRenderTexture()
    {
        int w = overrideWidth  > 0 ? overrideWidth  : Mathf.RoundToInt(_cam.pixelWidth  * resolutionScale);
        int h = overrideHeight > 0 ? overrideHeight : Mathf.RoundToInt(_cam.pixelHeight * resolutionScale);
        w = Mathf.Max(1, w);
        h = Mathf.Max(1, h);

        if (_rt != null) { _rt.Release(); Destroy(_rt); }

        // enableRandomWrite is required for URP to write to the RT correctly
        _rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        _rt.enableRandomWrite = false;
        _rt.Create();
        _cam.targetTexture = _rt;

        if (_readbackTex != null) Destroy(_readbackTex);
        _readbackTex = new Texture2D(w, h, TextureFormat.RGB24, false);
    }

    /// <summary>
    /// Single entry point for claiming a port and telling ROS about it.
    /// Called once at the end of Start(), and again every time the
    /// component is re-enabled after being disabled. Because the port for
    /// a given cameraKey is fixed for the session (see AssignPort), this is
    /// safe to call repeatedly -- it will keep resolving to the same port
    /// number every time, it just needs to (re)open the local socket and
    /// re-publish to ROS so a newly-(re)started listener picks it up.
    /// </summary>
    private void AssignPortAndPublish()
    {
        AssignPort();
        SetupROS();
    }

    private void AssignPort()
    {
        lock (_portLock)
        {
            // If this cameraKey already has a port from earlier in the
            // session (this exact instance enabling again, OR a previous
            // instance with the same key that was destroyed and replaced,
            // e.g. on a scene reload), reuse that exact number. We NEVER
            // pick a different port for a key that's already been assigned
            // one -- that permanence is what makes this safe under
            // disable/enable churn and concurrent initialization: there is
            // only ever one allocation decision per key, made once.
            if (_sessionPortAssignments.TryGetValue(cameraKey, out int existingPort))
            {
                _assignedPort = existingPort;
                Debug.Log($"[CameraStreamer:{cameraKey}] Reusing session port {existingPort}");
                return;
            }

            // First time this cameraKey has ever been seen this session.
            // Pick a free NUMBER and record the mapping permanently. This
            // does NOT bind any socket -- CameraStreamer never sends or
            // receives UDP. The port is purely a label published to ROS
            // (/unity/camera_ports) so that:
            //   - camera_streamer_node (ROS) knows which port to send its
            //     UDP frames to, and
            //   - CameraReceiver (Unity) knows which port to listen on.
            // Both of those are real socket owners; this class is not.
            // "Free" here just means not already claimed by another
            // cameraKey in _sessionPortAssignments -- we deliberately do
            // NOT try to bind-test it, since binding from here is exactly
            // what caused CameraReceiver's bind to fail with "address
            // already in use" (CameraStreamer was squatting on the port it
            // had no actual need to hold).
            for (int p = PORT_MIN; p <= PORT_MAX; p++)
            {
                if (_sessionPortAssignments.ContainsValue(p)) continue;

                _assignedPort = p;
                _sessionPortAssignments[cameraKey] = p;
                Debug.Log($"[CameraStreamer:{cameraKey}] Permanently assigned port number {p} for this session (label only, no socket bound)");
                return;
            }
            Debug.LogError($"[CameraStreamer:{cameraKey}] No free port number found in {PORT_MIN}-{PORT_MAX}!");
        }
    }

    private void SetupROS()
    {
        if (unityToUnityMode) return;
        if (_assignedPort < 0) return; // nothing to publish if port assignment failed

        if (useCompressedImage)
            _ros.RegisterPublisher<CompressedImageMsg>(rosTopic);
        else
            _ros.RegisterPublisher<ImageMsg>(rosTopic);

        // Publish port assignment for ROS nodes. Latched so a ROS node that
        // (re)starts after this still picks up the current port immediately,
        // and re-published on every enable so toggled cameras propagate
        // their new port to the listening camera_streamer_node.
        _ros.RegisterPublisher<StringMsg>("/unity/camera_ports", latch: true);
        PublishPortAssignment();
    }

    private void PublishPortAssignment()
    {
        var msg = new StringMsg($"{cameraKey}:{_assignedPort}");
        _ros.Publish("/unity/camera_ports", msg);
    }

    // -------------------------------------------------------------------------
    // Runtime callbacks
    // -------------------------------------------------------------------------

    /// <summary>Called by CameraReceiver to register for direct (no-UDP) delivery.</summary>
    public void RegisterDirectReceiver(CameraReceiver receiver)
    {
        lock (_directReceivers)
            if (!_directReceivers.Contains(receiver))
                _directReceivers.Add(receiver);
    }

    /// <summary>Called by CameraReceiver on destroy.</summary>
    public void UnregisterDirectReceiver(CameraReceiver receiver)
    {
        lock (_directReceivers)
            _directReceivers.Remove(receiver);
    }

    // -------------------------------------------------------------------------
    // Frame throttle -- gate whether this frame should be captured.
    // The actual capture happens in OnEndCameraRendering AFTER the GPU is done.
    // -------------------------------------------------------------------------
    void LateUpdate()
    {
        if (Time.time < _nextFrameTime) return;
        _nextFrameTime += _frameInterval;

        // Unity-to-Unity direct path: RT is already fully written at this point
        // because we're past the camera render phase when LateUpdate fires for
        // cameras that rendered earlier in the frame. For safety, deliver here.
        if (unityToUnityMode)
        {
            lock (_directReceivers)
                foreach (var r in _directReceivers)
                    r.ReceiveDirectTexture(_rt);
            return;
        }

        // Signal the URP callback to capture on this frame
        _captureThisFrame = true;
    }

    // -------------------------------------------------------------------------
    // URP post-render callback -- fires after the GPU finishes rendering to _rt.
    // This is the CORRECT place to ReadPixels; the RT is fully populated here.
    // -------------------------------------------------------------------------
    private void OnEndCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        // Only process our own camera, only while we're actually enabled
        // (this callback is unsubscribed in OnDisable, but guard anyway in
        // case of any same-frame ordering edge cases), and only when the
        // throttle says it's time.
        if (cam != _cam || !enabled || !_captureThisFrame) return;
        _captureThisFrame = false;

        CaptureAndSend();
    }

    // -------------------------------------------------------------------------
    // Capture + encode + dispatch
    // -------------------------------------------------------------------------
    private void CaptureAndSend()
    {
        // _rt is fully rendered at this point (called from endCameraRendering)
        var prev = RenderTexture.active;
        RenderTexture.active = _rt;
        _readbackTex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0, false);
        _readbackTex.Apply(false);
        RenderTexture.active = prev;

        byte[] jpeg = _readbackTex.EncodeToJPG(jpegQuality);

        // Unity -> ROS is topic-only in this architecture. CameraStreamer
        // never sends UDP; ROS -> Unity is the UDP direction, handled by
        // camera_streamer_node (ROS) sending to CameraReceiver (Unity),
        // which is the only thing that should bind a socket on
        // _assignedPort. Publish to ROS and we're done.
        if (useCompressedImage)
        {
            var msg = new CompressedImageMsg
            {
                header = new HeaderMsg { frame_id = cameraKey },
                format = "jpeg",
                data   = jpeg
            };
            _ros.Publish(rosTopic, msg);
        }
        else
        {
            // Raw RGB publish -- read raw bytes from already-readback texture
            byte[] raw = _readbackTex.GetRawTextureData();
            var msg = new ImageMsg
            {
                header   = new HeaderMsg { frame_id = cameraKey },
                height   = (uint)_rt.height,
                width    = (uint)_rt.width,
                encoding = "rgb8",
                step     = (uint)(_rt.width * 3),
                data     = raw
            };
            _ros.Publish(rosTopic, msg);
        }
    }

    // -------------------------------------------------------------------------
    // Runtime reconfiguration (callable from inspector or code)
    // -------------------------------------------------------------------------
    public void ApplySettings()
    {
        _frameInterval = 1f / Mathf.Max(1, targetFPS);
        AllocateRenderTexture();
    }

    // -------------------------------------------------------------------------
    // Public accessors
    // -------------------------------------------------------------------------
    public int AssignedPort => _assignedPort;
    public RenderTexture RT => _rt;
}
