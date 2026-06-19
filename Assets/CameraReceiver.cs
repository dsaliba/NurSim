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
///   (b) UDP chunked JPEG stream sent by a ROS node (camera_streamer_node).
///
/// Port assignment is learned from the /unity/camera_ports ROS topic, which
/// CameraStreamer publishes (as a label only -- CameraStreamer itself never
/// binds a socket). The actual UDP socket for a given port is owned by a
/// static, SHARED listener (SharedUdpListener) keyed by port number, not by
/// any individual CameraReceiver instance. This lets multiple CameraReceiver
/// components -- e.g. several UI panels all showing "front_camera" -- attach
/// to the same incoming UDP stream without each trying to exclusively bind
/// the port (which previously caused "address already in use" errors the
/// moment a second receiver, or a re-initializing streamer, touched the same
/// port number).
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
    // Static: shared ROS subscription bookkeeping
    // -------------------------------------------------------------------------
    private static ROSConnection _ros;
    private static bool _portTopicSubscribed = false;

    // All live CameraReceiver instances, so the single shared
    // /unity/camera_ports subscription can fan a message out to every
    // receiver interested in that cameraKey (there can be more than one).
    private static readonly List<CameraReceiver> _allReceivers = new List<CameraReceiver>();

    // -------------------------------------------------------------------------
    // Static: shared UDP socket manager
    // -------------------------------------------------------------------------
    /// <summary>
    /// Owns exactly one bound UdpClient per port, shared by however many
    /// CameraReceiver instances are currently listening on that port.
    /// Reference-counted: the socket is opened when the first listener
    /// attaches and closed when the last one detaches. This is what allows
    /// multiple receivers (e.g. two UI panels both showing "front_camera")
    /// to coexist without fighting over an exclusive bind.
    /// </summary>
    private static class SharedUdpListener
    {
        private class Entry
        {
            public UdpClient Client;
            public Thread ReceiveThread;
            public CancellationTokenSource Cts;
            public int RefCount;

            // Per-port frame reassembly state. Shared across all receivers
            // attached to this port, since they're all receiving the same
            // physical stream -- there is exactly one reassembly pipeline
            // per port, not one per receiver.
            public readonly Dictionary<uint, byte[][]> FrameChunks = new Dictionary<uint, byte[][]>();
            public readonly Dictionary<uint, int> FrameReceived = new Dictionary<uint, int>();
            public uint LastCompleteFrame = 0;

            // Subscribers currently attached to this port's stream.
            public readonly List<CameraReceiver> Subscribers = new List<CameraReceiver>();
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();

        /// <summary>
        /// Attach a receiver to the shared listener for this port, opening
        /// the socket if this is the first attachment. Safe to call even if
        /// already attached (no-op double add is avoided).
        /// </summary>
        public static void Attach(int port, CameraReceiver receiver)
        {
            lock (_lock)
            {
                if (!_entries.TryGetValue(port, out Entry entry))
                {
                    entry = new Entry();

                    try
                    {
                        entry.Client = new UdpClient(port);
                        entry.Client.Client.ReceiveBufferSize = 1 * 1024 * 1024; // 1 MB
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[SharedUdpListener] Could not bind port {port}: {ex.Message}");
                        return;
                    }

                    entry.Cts = new CancellationTokenSource();
                    entry.ReceiveThread = new Thread(() => ReceiveLoop(port, entry))
                    {
                        IsBackground = true,
                        Name = $"UDPReceive_shared_{port}"
                    };
                    entry.ReceiveThread.Start();

                    _entries[port] = entry;
                    Debug.Log($"[SharedUdpListener] Opened shared listener on UDP port {port}.");
                }

                if (!entry.Subscribers.Contains(receiver))
                {
                    entry.Subscribers.Add(receiver);
                    entry.RefCount++;
                }
            }
        }

        /// <summary>
        /// Detach a receiver from the shared listener for this port. Closes
        /// the socket once the last subscriber detaches.
        /// </summary>
        public static void Detach(int port, CameraReceiver receiver)
        {
            lock (_lock)
            {
                if (!_entries.TryGetValue(port, out Entry entry)) return;

                if (entry.Subscribers.Remove(receiver))
                    entry.RefCount--;

                if (entry.RefCount <= 0)
                {
                    entry.Cts.Cancel();
                    entry.ReceiveThread?.Join(200);
                    entry.Client?.Close();
                    _entries.Remove(port);
                    Debug.Log($"[SharedUdpListener] Closed shared listener on UDP port {port} (no subscribers left).");
                }
            }
        }

        private static void ReceiveLoop(int port, Entry entry)
        {
            var remoteEP = new IPEndPoint(IPAddress.Any, 0);

            while (!entry.Cts.IsCancellationRequested)
            {
                try
                {
                    byte[] packet = entry.Client.Receive(ref remoteEP);
                    if (packet.Length < 8) continue;

                    // IMPORTANT: camera_streamer_node.cpp writes this header
                    // using htonl()/htons() -- i.e. NETWORK byte order
                    // (big-endian). BitConverter.ToUInt32/ToUInt16 read using
                    // the HOST's native byte order, which is little-endian
                    // on essentially every desktop/Unity target. Reading
                    // with plain BitConverter here silently byte-swaps every
                    // field: frame_id, chunk_idx, and especially chunkTotal
                    // (which then drives an incorrectly-sized reassembly
                    // array), so frames never actually complete even though
                    // packets are visibly arriving on the wire. Convert
                    // explicitly via IPAddress.NetworkToHostOrder to match
                    // what the sender actually wrote.
                    //
                    // FIELD ORDER: camera_streamer_node.cpp's packet layout
                    // is frame_id (bytes 0-3), then TOTAL_FRAGMENTS (bytes
                    // 4-5), then FRAG_IDX (bytes 6-7) -- total comes before
                    // index. These were previously read swapped (chunkIdx
                    // from 4-5, chunkTotal from 6-7), which is why a
                    // single-fragment frame (total_fragments=1, frag_idx=0)
                    // showed up here as chunkIdx=1, chunkTotal=0 on every
                    // packet -- a consistent, readable-but-wrong value
                    // rather than random noise, which is exactly what
                    // tipped this off as a field-order bug rather than
                    // packet corruption.
                    uint   frameId    = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(packet, 0));
                    ushort chunkTotal = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(packet, 4));
                    ushort chunkIdx   = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(packet, 6));

                    // Defensive validation: chunkIdx/chunkTotal come straight
                    // from the network and must never be trusted blindly
                    // before indexing an array sized by them. A malformed,
                    // truncated, reordered, or cross-stream packet (e.g. a
                    // stray packet from a different camera_streamer_node
                    // instance, or one captured mid-transition before this
                    // port's stream settled) could otherwise throw an
                    // IndexOutOfRangeException and silently kill this
                    // receive thread, which is exactly what surfaced here.
                    // Drop anything that doesn't make sense rather than
                    // crash.
                    if (chunkTotal == 0 || chunkIdx >= chunkTotal)
                    {
                        Debug.LogWarning(
                            $"[SharedUdpListener:{port}] Dropping malformed packet " +
                            $"(frameId={frameId}, chunkIdx={chunkIdx}, chunkTotal={chunkTotal}).");
                        continue;
                    }

                    if (frameId <= entry.LastCompleteFrame && entry.LastCompleteFrame != 0)
                        continue;

                    lock (entry.FrameChunks)
                    {
                        if (!entry.FrameChunks.ContainsKey(frameId))
                        {
                            entry.FrameChunks[frameId] = new byte[chunkTotal][];
                            entry.FrameReceived[frameId] = 0;
                            PruneOldFrames(entry, frameId);
                        }

                        // A frame's chunkTotal must stay consistent across
                        // all of its packets (it's fixed at send time for a
                        // given frameId). If a later packet for this same
                        // frameId reports a DIFFERENT chunkTotal than the
                        // array we already allocated, indexing with the new
                        // chunkIdx against the old array could go out of
                        // bounds. Treat that as corrupt/inconsistent data
                        // for this frame and discard the whole frame rather
                        // than risk an out-of-bounds write.
                        byte[][] frameSlots = entry.FrameChunks[frameId];
                        if (chunkIdx >= frameSlots.Length)
                        {
                            Debug.LogWarning(
                                $"[SharedUdpListener:{port}] Inconsistent chunkTotal for " +
                                $"frameId={frameId} (chunkIdx={chunkIdx}, allocated size={frameSlots.Length}); discarding frame.");
                            entry.FrameChunks.Remove(frameId);
                            entry.FrameReceived.Remove(frameId);
                            continue;
                        }

                        if (frameSlots[chunkIdx] == null)
                        {
                            int payloadLen = packet.Length - 8;
                            var chunk = new byte[payloadLen];
                            Buffer.BlockCopy(packet, 8, chunk, 0, payloadLen);
                            frameSlots[chunkIdx] = chunk;
                            entry.FrameReceived[frameId]++;
                        }

                        if (entry.FrameReceived[frameId] == chunkTotal)
                        {
                            byte[] jpeg = AssembleFrame(entry, frameId, chunkTotal);
                            entry.LastCompleteFrame = frameId;
                            entry.FrameChunks.Remove(frameId);
                            entry.FrameReceived.Remove(frameId);

                            // Fan the completed frame out to every receiver
                            // currently subscribed to this port.
                            List<CameraReceiver> subsCopy;
                            lock (_lock) subsCopy = new List<CameraReceiver>(entry.Subscribers);

                            foreach (var sub in subsCopy)
                                sub.OnSharedFrameReceived(jpeg);
                        }
                    }
                }
                catch (SocketException ex)
                {
                    if (!entry.Cts.IsCancellationRequested)
                        Debug.LogWarning($"[SharedUdpListener:{port}] Socket error: {ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    if (!entry.Cts.IsCancellationRequested)
                        Debug.LogWarning($"[SharedUdpListener:{port}] Receive error: {ex.Message}");
                }
            }
        }

        private static byte[] AssembleFrame(Entry entry, uint frameId, int chunkTotal)
        {
            int totalLen = 0;
            var chunks = entry.FrameChunks[frameId];
            for (int i = 0; i < chunkTotal; i++)
                totalLen += chunks[i].Length;

            byte[] frame = new byte[totalLen];
            int offset = 0;
            for (int i = 0; i < chunkTotal; i++)
            {
                Buffer.BlockCopy(chunks[i], 0, frame, offset, chunks[i].Length);
                offset += chunks[i].Length;
            }
            return frame;
        }

        private static void PruneOldFrames(Entry entry, uint newestFrameId)
        {
            var toRemove = new List<uint>();
            foreach (var id in entry.FrameChunks.Keys)
                if (newestFrameId - id > 5)
                    toRemove.Add(id);
            foreach (var id in toRemove)
            {
                entry.FrameChunks.Remove(id);
                entry.FrameReceived.Remove(id);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Instance state
    // -------------------------------------------------------------------------
    private int _currentListenPort = -1;

    // Double buffer: background thread (via SharedUdpListener) writes
    // _pendingJpeg, main thread reads it in Update.
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
    }

    void OnEnable()
    {
        lock (_allReceivers)
            if (!_allReceivers.Contains(this))
                _allReceivers.Add(this);

        if (_ros == null)
            _ros = ROSConnection.GetOrCreateInstance();

        // Only one actual ROS subscription for the whole class -- it fans
        // out to every CameraReceiver via _allReceivers. Subscribing once
        // per instance would still work correctness-wise (ROSConnection
        // supports multiple subscribers), but a single shared subscription
        // keeps this consistent with the "one shared resource" pattern used
        // for the UDP sockets below.
        if (!_portTopicSubscribed)
        {
            _ros.Subscribe<StringMsg>("/unity/camera_ports", OnPortAnnouncementStatic);
            _portTopicSubscribed = true;
        }

        // Try to connect directly if a local CameraStreamer already exists
        TryConnectDirect();

        // If manual port override is set, start immediately
        if (manualPort > 0)
            StartListening(manualPort);

        ShowNoSignal();
    }

    void OnDisable()
    {
        if (_currentListenPort >= 0)
        {
            SharedUdpListener.Detach(_currentListenPort, this);
            _currentListenPort = -1;
        }

        lock (CameraStreamer.ActiveStreamers)
        {
            if (CameraStreamer.ActiveStreamers.TryGetValue(cameraKey, out var streamer))
                streamer.UnregisterDirectReceiver(this);
        }

        lock (_allReceivers)
            _allReceivers.Remove(this);
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

        // Check for decoded JPEG fanned out from the shared UDP listener
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
        if (_decodeTex != null) Destroy(_decodeTex);
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

    /// <summary>
    /// Single shared ROS callback for the whole class. Fans the
    /// announcement out to every live CameraReceiver so each one can decide
    /// (by matching cameraKey) whether it applies to them.
    /// </summary>
    private static void OnPortAnnouncementStatic(StringMsg msg)
    {
        var parts = msg.data?.Split(':');
        if (parts == null || parts.Length != 2) return;
        if (!int.TryParse(parts[1], out int port)) return;
        string key = parts[0];

        List<CameraReceiver> targets;
        lock (_allReceivers)
            targets = new List<CameraReceiver>(_allReceivers);

        foreach (var receiver in targets)
        {
            if (string.Equals(receiver.cameraKey, key, StringComparison.Ordinal))
                receiver.OnPortAnnouncement(port);
        }
    }

    private void OnPortAnnouncement(int port)
    {
        if (port == _currentListenPort) return; // no change

        Debug.Log($"[CameraReceiver:{cameraKey}] Port announcement -> {port}");

        // Check for direct mode first (streamer might have restarted)
        TryConnectDirect();
        if (_isDirectMode) return;

        StartListening(port);
    }

    private void StartListening(int port)
    {
        // Detach from whatever port we were previously on (if any). This is
        // a no-op for the shared socket unless we were the last subscriber,
        // in which case SharedUdpListener closes it -- otherwise other
        // receivers still attached to that old port keep working
        // uninterrupted.
        if (_currentListenPort >= 0 && _currentListenPort != port)
            SharedUdpListener.Detach(_currentListenPort, this);

        _currentListenPort = port;

        // Attach to (or join) the shared listener for the new port. If
        // another CameraReceiver is already listening on this exact port
        // (e.g. two UI panels both showing "front_camera"), this just adds
        // us as a second subscriber to the existing socket rather than
        // attempting a second exclusive bind -- which is what used to fail
        // with "Only one usage of each socket address is normally
        // permitted."
        SharedUdpListener.Attach(port, this);

        Debug.Log($"[CameraReceiver:{cameraKey}] Listening on UDP port {port} (shared).");

        // Reset this receiver's own reassembly view. (Reassembly itself now
        // happens once per port inside SharedUdpListener; this is just
        // clearing this instance's display-side buffer so a stale pending
        // frame from the previous port doesn't get shown under the new
        // port's label.)
        lock (_bufferLock)
        {
            _pendingJpeg = null;
            _newFrameReady = false;
        }
    }

    /// <summary>
    /// Called by SharedUdpListener on its background thread whenever a full
    /// frame has been reassembled for the port this receiver is attached
    /// to. Just stores it for Update() to pick up on the main thread.
    /// </summary>
    internal void OnSharedFrameReceived(byte[] jpeg)
    {
        lock (_bufferLock)
        {
            _pendingJpeg = jpeg;
            _newFrameReady = true;
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
