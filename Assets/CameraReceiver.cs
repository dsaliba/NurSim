using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;

/// <summary>
/// Attach to a UI Canvas (or any GameObject with a RawImage).
/// Displays the feed identified by <see cref="cameraKey"/> received via:
///   (a) Direct RenderTexture from a local CameraStreamer (Unity-to-Unity), OR
///   (b) UDP chunked JPEG stream sent by a ROS node (or remote CameraStreamer).
///
/// Port assignment is learned from the /unity/camera_ports ROS topic published
/// by CameraStreamer. Once a port is known, a local UDP listener is started.
///
/// Handles the streamer / ROS node restarting gracefully:
///   - If no packet is received for <see cref="timeoutSeconds"/>, the display
///     shows a "no signal" state and the receiver keeps listening.
///   - When the streamer restarts and publishes a new port, the receiver
///     seamlessly switches to the new port.
/// </summary>
public class CameraReceiver : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector
    // -------------------------------------------------------------------------
    [Header("Camera Identity")]
    [Tooltip("Must match the cameraKey on the corresponding CameraStreamer or ROS node.")]
    public string cameraKey = "front_camera";

    [Header("Display")]
    [Tooltip("The RawImage component that will show the camera feed. " +
             "If left null, one is searched for on this GameObject.")]
    public RawImage displayTarget;

    [Tooltip("Shown when no stream data is received.")]
    public Texture2D noSignalTexture;

    [Tooltip("Seconds without a packet before switching to no-signal state.")]
    public float timeoutSeconds = 3f;

    [Header("UDP Listen")]
    [Tooltip("Port to listen on. 0 = auto-assigned from /unity/camera_ports. " +
             "Set manually only if you want to bypass the ROS port announcement.")]
    public int manualPort = 0;

    // -------------------------------------------------------------------------
    // Private state
    // -------------------------------------------------------------------------
    private ROSConnection _ros;
    private UdpClient _udpClient;
    private int _currentListenPort = -1;
    private CancellationTokenSource _cts;
    private Thread _receiveThread;

    // Frame reassembly
    // Key: frameId, Value: chunk array (null slot = not yet received)
    private readonly Dictionary<uint, byte[][]> _frameChunks   = new Dictionary<uint, byte[][]>();
    private readonly Dictionary<uint, int>       _frameReceived = new Dictionary<uint, int>();
    private uint _lastCompleteFrame = 0;

    // Double buffer: background thread writes _pendingJpeg, main thread reads it
    private readonly object _bufferLock = new object();
    private byte[] _pendingJpeg = null;
    private bool   _newFrameReady = false;
    private bool   _isDirectMode = false;
    private RenderTexture _directRT = null;

    // Timeout tracking
    private float _lastPacketTime;
    private bool  _signalLost = true;

    // Decode texture (reused)
    private Texture2D _decodeTex;

    // -------------------------------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------------------------------
    void Awake()
    {
        if (displayTarget == null)
            displayTarget = GetComponentInChildren<RawImage>();

        _cts = new CancellationTokenSource();
    }

    void Start()
    {
        _ros = ROSConnection.GetOrCreateInstance();

        // Listen for port announcements
        _ros.Subscribe<StringMsg>("/unity/camera_ports", OnPortAnnouncement);

        // Try to connect directly if a local CameraStreamer already exists
        TryConnectDirect();

        // If manual port override is set, start immediately
        if (manualPort > 0)
            StartListening(manualPort);

        ShowNoSignal();
    }

    void Update()
    {
        // Direct mode: RenderTexture is updated automatically, just assign it
        if (_isDirectMode && _directRT != null)
        {
            if (displayTarget.texture != _directRT)
                displayTarget.texture = _directRT;
                
            _signalLost    = false;
            _lastPacketTime = Time.time;
            return;
        }

        // Check for decoded JPEG from UDP thread
        bool gotFrame = false;
        byte[] jpeg   = null;
        lock (_bufferLock)
        {
            if (_newFrameReady)
            {
                jpeg          = _pendingJpeg;
                _newFrameReady = false;
                gotFrame       = true;
            }
        }

        if (gotFrame && jpeg != null)
        {
            DisplayJpeg(jpeg);
            _lastPacketTime = Time.time;
            if (_signalLost)
            {
                _signalLost = false;
                Debug.Log($"[CameraReceiver:{cameraKey}] Signal acquired.");
            }
        }
        displayTarget.color = Color.white;

        // Timeout check
        if (!_signalLost && Time.time - _lastPacketTime > timeoutSeconds)
        {
            _signalLost = true;
            Debug.LogWarning($"[CameraReceiver:{cameraKey}] Signal lost (timeout).");
            ShowNoSignal();
        }
    }

    void OnDestroy()
    {
        _cts.Cancel();
        _receiveThread?.Abort();
        _udpClient?.Close();

        if (_decodeTex != null) Destroy(_decodeTex);

        // Unregister from direct streamer if connected
        lock (CameraStreamer.ActiveStreamers)
        {
            if (CameraStreamer.ActiveStreamers.TryGetValue(cameraKey, out var streamer))
                streamer.UnregisterDirectReceiver(this);
        }
    }

    // -------------------------------------------------------------------------
    // Direct Unity-to-Unity connection
    // -------------------------------------------------------------------------
    private void TryConnectDirect()
    {
        CameraStreamer streamer;
        lock (CameraStreamer.ActiveStreamers)
            CameraStreamer.ActiveStreamers.TryGetValue(cameraKey, out streamer);

        if (streamer != null && streamer.unityToUnityMode)
        {
            _isDirectMode = true;
            streamer.RegisterDirectReceiver(this);
            Debug.Log($"[CameraReceiver:{cameraKey}] Connected in direct Unity-to-Unity mode.");
        }
    }

    /// <summary>Called by CameraStreamer each frame in direct mode.</summary>
    public void ReceiveDirectTexture(RenderTexture rt)
    {
        _directRT = rt;
    }

    // -------------------------------------------------------------------------
    // Port management
    // -------------------------------------------------------------------------
    private void OnPortAnnouncement(StringMsg msg)
    {
        // Format: "cameraKey:port"
        var parts = msg.data?.Split(':');
        if (parts == null || parts.Length != 2) return;
        if (!string.Equals(parts[0], cameraKey, StringComparison.Ordinal)) return;
        if (!int.TryParse(parts[1], out int port)) return;

        if (port == _currentListenPort) return; // no change

        Debug.Log($"[CameraReceiver:{cameraKey}] Port announcement → {port}");

        // Check for direct mode first (streamer might have restarted)
        TryConnectDirect();
        if (_isDirectMode) return;

        StartListening(port);
    }

    private void StartListening(int port)
    {
        // Tear down existing listener
        _cts.Cancel();
        _receiveThread?.Join(200);
        _udpClient?.Close();

        _currentListenPort = port;
        _cts               = new CancellationTokenSource();

        try
        {
            _udpClient = new UdpClient(port);
            _udpClient.Client.ReceiveBufferSize = 1 * 1024 * 1024; // 1 MB
            Debug.Log($"[CameraReceiver:{cameraKey}] Listening on UDP port {port}.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[CameraReceiver:{cameraKey}] Could not bind port {port}: {ex.Message}");
            return;
        }

        // Start background receive thread
        _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = $"UDPReceive_{cameraKey}" };
        _receiveThread.Start();

        // Reset reassembly state
        lock (_frameChunks)
        {
            _frameChunks.Clear();
            _frameReceived.Clear();
        }
    }

    // -------------------------------------------------------------------------
    // UDP receive loop (background thread)
    // -------------------------------------------------------------------------
    private void ReceiveLoop()
    {
        var remoteEP = new IPEndPoint(IPAddress.Any, 0);

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                byte[] packet = _udpClient.Receive(ref remoteEP);
                if (packet.Length < 8) continue;

                uint   frameId     = BitConverter.ToUInt32(packet, 0);
                ushort chunkIdx    = BitConverter.ToUInt16(packet, 4);
                ushort chunkTotal  = BitConverter.ToUInt16(packet, 6);

                // Discard frames older than the last complete one
                if (frameId <= _lastCompleteFrame && _lastCompleteFrame != 0)
                    continue;

                lock (_frameChunks)
                {
                    if (!_frameChunks.ContainsKey(frameId))
                    {
                        _frameChunks[frameId]   = new byte[chunkTotal][];
                        _frameReceived[frameId] = 0;

                        // Prune old incomplete frames (keep only last 3 in-flight)
                        PruneOldFrames(frameId);
                    }

                    // Store chunk if not duplicate
                    if (_frameChunks[frameId][chunkIdx] == null)
                    {
                        int payloadLen = packet.Length - 8;
                        var chunk      = new byte[payloadLen];
                        Buffer.BlockCopy(packet, 8, chunk, 0, payloadLen);
                        _frameChunks[frameId][chunkIdx] = chunk;
                        _frameReceived[frameId]++;
                    }

                    // Check if frame is complete
                    if (_frameReceived[frameId] == chunkTotal)
                    {
                        byte[] jpeg = AssembleFrame(frameId, chunkTotal);
                        _lastCompleteFrame = frameId;
                        _frameChunks.Remove(frameId);
                        _frameReceived.Remove(frameId);

                        lock (_bufferLock)
                        {
                            _pendingJpeg    = jpeg;
                            _newFrameReady  = true;
                        }
                    }
                }
            }
            catch (SocketException ex)
            {
                // Socket closed gracefully on teardown
                if (!_cts.IsCancellationRequested)
                    Debug.LogWarning($"[CameraReceiver:{cameraKey}] Socket error: {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                    Debug.LogWarning($"[CameraReceiver:{cameraKey}] Receive error: {ex.Message}");
            }
        }
    }

    private byte[] AssembleFrame(uint frameId, int chunkTotal)
    {
        int totalLen = 0;
        var chunks   = _frameChunks[frameId];
        for (int i = 0; i < chunkTotal; i++)
            totalLen += chunks[i].Length;

        byte[] frame = new byte[totalLen];
        int offset   = 0;
        for (int i = 0; i < chunkTotal; i++)
        {
            Buffer.BlockCopy(chunks[i], 0, frame, offset, chunks[i].Length);
            offset += chunks[i].Length;
        }
        return frame;
    }

    private void PruneOldFrames(uint newestFrameId)
    {
        // Remove frames more than 5 frames behind
        var toRemove = new List<uint>();
        foreach (var id in _frameChunks.Keys)
            if (newestFrameId - id > 5)
                toRemove.Add(id);
        foreach (var id in toRemove)
        {
            _frameChunks.Remove(id);
            _frameReceived.Remove(id);
        }
    }

    // -------------------------------------------------------------------------
    // Display helpers (main thread only)
    // -------------------------------------------------------------------------
    private void DisplayJpeg(byte[] jpeg)
    {
        if (displayTarget == null) return;

        if (_decodeTex == null)
            _decodeTex = new Texture2D(2, 2, TextureFormat.RGB24, false);

        if (_decodeTex.LoadImage(jpeg))
        {
            displayTarget.texture = _decodeTex;
        }
        else
        {
            Debug.LogWarning($"[CameraReceiver:{cameraKey}] Failed to decode JPEG frame.");
        }
    }

    private void ShowNoSignal()
    {
        if (displayTarget == null) return;
        if (noSignalTexture != null)
            displayTarget.texture = noSignalTexture;
        else
            displayTarget.color = Color.black;
    }
}
