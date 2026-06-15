using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
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
/// Port assignment (5000–5500) is managed statically so multiple instances
/// never collide. Port assignments are published to ROS topic /unity/camera_ports
/// so ROS nodes know which port to target.
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

    [Tooltip("Fixed width override (0 = use camera width × resolutionScale).")]
    public int overrideWidth = 0;

    [Tooltip("Fixed height override (0 = use camera height × resolutionScale).")]
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
    private static readonly HashSet<int> _assignedPorts = new HashSet<int>();
    private const int PORT_MIN = 5000;
    private const int PORT_MAX = 5500;

    /// <summary>All live streamers: key → streamer. Used by CameraReceiver for direct lookup.</summary>
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

    // UDP send
    private UdpClient _udpSender;
    private string _unityReceiverIP = null;
    private bool _ipResolved = false;

    // Direct-mode subscribers
    private readonly List<CameraReceiver> _directReceivers = new List<CameraReceiver>();

    // URP frame capture gate — set in Update, consumed in endCameraRendering callback
    private bool _captureThisFrame = false;

    private CancellationTokenSource _cts;

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

        RegisterStreamer();
        AllocateRenderTexture();
        AssignPort();
        SetupROS();

        // Subscribe to /unity/ip so we learn the Unity receiver's IP
        _ros.Subscribe<StringMsg>("/unity/ip", OnUnityIPReceived);

        _frameInterval = 1f / Mathf.Max(1, targetFPS);
        _nextFrameTime = Time.time;
    }

    void OnEnable()
    {
        RegisterStreamer();
        // URP: hook into the render pipeline's post-camera event
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        UnregisterStreamer();
    }

    void OnDestroy()
    {
        _cts.Cancel();
        UnregisterStreamer();
        ReleasePort(_assignedPort);
        _udpSender?.Close();
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

    private void AssignPort()
    {
        lock (_portLock)
        {
            for (int p = PORT_MIN; p <= PORT_MAX; p++)
            {
                if (_assignedPorts.Contains(p)) continue;
                if (TryBindPort(p))
                {
                    _assignedPort = p;
                    _assignedPorts.Add(p);
                    Debug.Log($"[CameraStreamer:{cameraKey}] Assigned UDP port {p}");
                    return;
                }
            }
            Debug.LogError($"[CameraStreamer:{cameraKey}] No free UDP port found in {PORT_MIN}-{PORT_MAX}!");
        }
    }

    private bool TryBindPort(int port)
    {
        try
        {
            var test = new UdpClient(port);
            test.Close();
            return true;
        }
        catch { return false; }
    }

    private static void ReleasePort(int port)
    {
        if (port < 0) return;
        lock (_portLock) _assignedPorts.Remove(port);
    }

    private void SetupROS()
    {
        if (unityToUnityMode) return;

        if (useCompressedImage)
            _ros.RegisterPublisher<CompressedImageMsg>(rosTopic);
        else
            _ros.RegisterPublisher<ImageMsg>(rosTopic);

        // Publish port assignment for ROS nodes
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
    private void OnUnityIPReceived(StringMsg msg)
    {
        _unityReceiverIP = msg.data?.Trim();
        _ipResolved = !string.IsNullOrEmpty(_unityReceiverIP);
        if (_ipResolved)
        {
            Debug.Log($"[CameraStreamer:{cameraKey}] Unity receiver IP: {_unityReceiverIP}, port: {_assignedPort}");
            _udpSender?.Close();
            _udpSender = new UdpClient();
        }
    }

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
    // Frame throttle — gate whether this frame should be captured.
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
    // URP post-render callback — fires after the GPU finishes rendering to _rt.
    // This is the CORRECT place to ReadPixels; the RT is fully populated here.
    // -------------------------------------------------------------------------
    private void OnEndCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        // Only process our own camera, and only when throttle says it's time
        if (cam != _cam || !_captureThisFrame) return;
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

        bool hasUDPTarget = _ipResolved && _udpSender != null;

        // Fire-and-forget async UDP send so main thread isn't blocked
        if (hasUDPTarget)
        {
            var ip   = _unityReceiverIP;
            var port = _assignedPort;
            var data = jpeg;
            System.Threading.Tasks.Task.Run(() => SendUDPChunked(data, ip, port), _cts.Token);
        }

        // ROS publish
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
            // Raw RGB publish — read raw bytes from already-readback texture
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
    // UDP chunked send
    // -------------------------------------------------------------------------
    // Packet layout (bytes):
    //   [0..3]  frameId    uint32 — monotonically increasing frame counter
    //   [4..5]  chunkIdx   uint16 — index of this chunk (0-based)
    //   [6..7]  chunkTotal uint16 — total chunks for this frame
    //   [8..]   payload         — JPEG bytes slice
    // -------------------------------------------------------------------------
    private const int UDP_PAYLOAD_SIZE = 60000; // stay well under typical MTU
    private int _frameCounter = 0;

    private void SendUDPChunked(byte[] jpeg, string ip, int port)
    {
        try
        {
            uint frameId = (uint)Interlocked.Increment(ref _frameCounter);
            int total    = (jpeg.Length + UDP_PAYLOAD_SIZE - 1) / UDP_PAYLOAD_SIZE;
            var ep       = new IPEndPoint(IPAddress.Parse(ip), port);

            for (int i = 0; i < total; i++)
            {
                int offset = i * UDP_PAYLOAD_SIZE;
                int len    = Mathf.Min(UDP_PAYLOAD_SIZE, jpeg.Length - offset);
                byte[] pkt = new byte[8 + len];

                // Header
                Buffer.BlockCopy(BitConverter.GetBytes(frameId),      0, pkt, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)i),    0, pkt, 4, 2);
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)total), 0, pkt, 6, 2);
                // Payload
                Buffer.BlockCopy(jpeg, offset, pkt, 8, len);

                _udpSender.Send(pkt, pkt.Length, ep);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[CameraStreamer:{cameraKey}] UDP send error: {ex.Message}");
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
