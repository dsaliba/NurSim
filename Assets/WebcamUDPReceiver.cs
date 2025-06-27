using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

[RequireComponent(typeof(RawImage))]
public class UDPWebcamReceiver : MonoBehaviour
{
    public string cameraName;
    public int port = -1;
    public float fragmentTimeout = 0.2f;

    private UdpClient udpClient;
    private Thread receiveThread;
    private RawImage rawImageUI;
    private Texture2D receivedTexture;

    private object frameLock = new object();
    private byte[] completeFrame;
    private static Dictionary<int, UdpClient> udpClients = new Dictionary<int, UdpClient>();
    private static Dictionary<int, UDPWebcamReceiver> socketHogs = new Dictionary<int, UDPWebcamReceiver>();

    protected ROSConnection ros;

    private Stopwatch stopwatch = Stopwatch.StartNew();
    private volatile bool isRunning = true;

    private class FrameBuffer
    {
        public int totalFragments;
        public byte[][] fragments;
        public int receivedCount;
        public float timestamp;
    }

    private Dictionary<uint, FrameBuffer> frameBuffers = new Dictionary<uint, FrameBuffer>();

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        rawImageUI = GetComponent<RawImage>();

        ros.Subscribe<StringMsg>("udpcam/ports", msg =>
        {
            if (port < 0)
            {
                string[] existing = msg.data.Split('&');
                foreach (string entry in existing)
                {
                    string[] item = entry.Split(':');
                    if (item[0] == cameraName)
                    {
                        port = int.Parse(item[1]);
                        ConnectToPort();
                        receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
                        receiveThread.Start();
                        break;
                    }
                }
            }
        });

        receivedTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
    }

    void ConnectToPort()
    {
        if (udpClients.ContainsKey(port))
        {
            socketHogs[port].StopThread();
            udpClient = udpClients[port];
            socketHogs[port] = this;
        }
        else
        {
            udpClient = new UdpClient(port);
            udpClients.Add(port, udpClient);
            socketHogs.Add(port, this);
        }
    }

    void StopThread()
    {
        isRunning = false;
        receiveThread?.Join();
    }

    void ReceiveLoop()
    {
        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, port);

        while (isRunning)
        {
            try
            {
                if (udpClient.Available == 0)
                {
                    Thread.Sleep(1);
                    continue;
                }

                byte[] data = udpClient.Receive(ref remoteEP);

                if (data.Length < 8)
                    continue;

                uint frameId = (uint)(data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]);
                ushort totalFragments = (ushort)((data[4] << 8) | data[5]);
                ushort fragmentIndex = (ushort)((data[6] << 8) | data[7]);

                byte[] payload = new byte[data.Length - 8];
                Buffer.BlockCopy(data, 8, payload, 0, payload.Length);

                lock (frameLock)
                {
                    if (!frameBuffers.TryGetValue(frameId, out var buffer))
                    {
                        buffer = new FrameBuffer
                        {
                            totalFragments = totalFragments,
                            fragments = new byte[totalFragments][],
                            receivedCount = 0,
                            timestamp = (float)stopwatch.Elapsed.TotalSeconds
                        };
                        frameBuffers[frameId] = buffer;
                    }

                    if (fragmentIndex < buffer.fragments.Length && buffer.fragments[fragmentIndex] == null)
                    {
                        buffer.fragments[fragmentIndex] = payload;
                        buffer.receivedCount++;
                    }

                    if (buffer.receivedCount == buffer.totalFragments)
                    {
                        int totalLength = 0;
                        foreach (var frag in buffer.fragments)
                            totalLength += frag.Length;

                        byte[] frameData = new byte[totalLength];
                        int offset = 0;
                        foreach (var frag in buffer.fragments)
                        {
                            Buffer.BlockCopy(frag, 0, frameData, offset, frag.Length);
                            offset += frag.Length;
                        }

                        completeFrame = frameData;
                        frameBuffers.Remove(frameId);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("Receive error: " + e.Message);
            }
        }
    }

    void Update()
    {
        float now = (float)stopwatch.Elapsed.TotalSeconds;

        lock (frameLock)
        {
            List<uint> stale = new List<uint>();
            foreach (var kvp in frameBuffers)
            {
                if (now - kvp.Value.timestamp > fragmentTimeout)
                    stale.Add(kvp.Key);
            }

            foreach (var id in stale)
            {
                frameBuffers.Remove(id);
            }
        }

        if (completeFrame != null)
        {
            byte[] frameToDisplay;
            lock (frameLock)
            {
                frameToDisplay = completeFrame;
                completeFrame = null;
            }

            if (frameToDisplay != null)
            {
                if (!receivedTexture.LoadImage(frameToDisplay, false))
                {
                    Debug.LogWarning("Failed to decode image.");
                }
                else
                {
                    rawImageUI.texture = receivedTexture;
                    rawImageUI.SetNativeSize();
                }
            }
        }
    }

    void OnApplicationQuit()
    {
        isRunning = false;
        receiveThread?.Join();
        udpClient?.Close();
    }
}
