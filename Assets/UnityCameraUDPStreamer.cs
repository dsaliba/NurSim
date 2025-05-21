using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using Random = UnityEngine.Random;

[RequireComponent(typeof(Camera))]
public class CameraStreamer : MonoBehaviour
{
    public string cameraName;
    
    private string targetIP = "";


    [Header("Stream Settings")]
    [Range(1, 60)] public int fps = 30;
    public int width = 640;
    public int height = 480;
    [Range(1, 100)] public int jpegQuality = 75;


    public int targetPort = -1;
    
    private ROSConnection ros;
    
    // Resources
    private Camera sourceCam;
    private RenderTexture renderTex;
    private Texture2D outputTex;
    private UdpClient udpClient;
    
    // State
    private uint frameId = 0;
    private const int MTU = 1400;
    private bool isStreaming = false;

    private static CameraStreamer reservation = null;
    private String buffer = null;
    private static String fakeLatch;
    private static String fakeLatchIP;

    private bool streaming = false;

    private void Awake()
    {
        
        
        sourceCam = GetComponent<Camera>();

        renderTex = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
        {
            antiAliasing = 1,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false
        };
        renderTex.Create();

        outputTex = new Texture2D(width, height, TextureFormat.RGB24, false);

        sourceCam.targetTexture = renderTex;
        sourceCam.forceIntoRenderTexture = true;

        udpClient = new UdpClient();
        Debug.LogWarning("jello waking");
    }

    public void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.Subscribe<StringMsg>("unity/ip", msg =>
        {
            
            targetIP = msg.data;
            fakeLatchIP = msg.data;
        });
        targetIP = fakeLatchIP;
        if (!ros.GetTopic("udpcam/ports").IsPublisher)
        {
     
            ros.RegisterPublisher<StringMsg>("udpcam/ports", latch: true);
            
        }
        else
        {
            
        }
        buffer = fakeLatch;
        
        Debug.LogWarning("jello and what not " + buffer);
            ros.Subscribe<StringMsg>("udpcam/ports", msg =>
            {
                
                buffer = msg.data;
                fakeLatch = buffer;
            });
        
    }

    private int cooldown = 5;
    public void Update()
    {
        if (buffer != null && reservation == null)
        {
            if (targetPort < 0)
            {
                reservation = this;
                cooldown = 5;
                string[] existing = buffer.Split('&');
                int[] taken = new int[existing.Length];
                for (int i = 0; i < taken.Length; i++)
                {
                    string[] item = existing[i].Split(":");
                    if (item[0].Equals(cameraName))
                    {
                        targetPort = int.Parse(item[1]);
                        ros.Publish("udpcam/ports", new StringMsg(buffer));
                        break;
                    }
                    taken[i] = int.Parse(item[1]);
                    
                }

                while (targetPort < 0)
                {
                    int candidate = (int)Random.Range(5000, 6000);
                    bool unique = true;
                    for (int i = 0; i < taken.Length; i++)
                    {
                        if (taken[i] == candidate)
                        {
                            unique = false;
                            break;
                        }
                    }

                    if (unique)
                    {
                        targetPort = candidate;
                        string reg = buffer + "&" + cameraName + ":" + targetPort;
                        ros.Publish("udpcam/ports", new StringMsg(reg));
                    }
                }
               
            }
        }else if (targetPort < 0 && buffer == null & reservation == null)
        {
            reservation = this;
            cooldown = 5;
            targetPort = (int)Random.Range(5000, 6000);
            string reg = cameraName + ":" + targetPort;
            ros.Publish("udpcam/ports", new StringMsg(reg));
            buffer = reg;

        }

        if (reservation == this)
        {
            cooldown--;
            if (cooldown < 0)
            {
                reservation = null;
            }
        }

        
    }


    private void OnEnable()
    {
        StartCoroutine(StreamingCoroutine());
    }

    private void OnDisable()
    {
        StopAllCoroutines();
    }

    private IEnumerator StreamingCoroutine()
    {
        var interval = new WaitForSecondsRealtime(1f / fps);
        var frameWait = new WaitForEndOfFrame();
        
        isStreaming = true;
        Debug.LogWarning("jello STARTING");
        while (isStreaming)
        {
            yield return frameWait;
            CaptureAndSendFrame();
            yield return interval;
        }
    }

    private void CaptureAndSendFrame()
    {
        

        // Ensure we have a clean state
        RenderTexture.active = renderTex;
        
        // Force camera to render
        sourceCam.Render();

        // Read pixels
        outputTex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        outputTex.Apply();
        RenderTexture.active = null;

        // Encode to JPEG
        byte[] jpegData = outputTex.EncodeToJPG(jpegQuality);

       

        // Fragment and send
        SendFragmented(jpegData);
        frameId++;
    }

    private void SendFragmented(byte[] frameData)
    {
        int maxPayload = MTU - 8; // 8 byte header
        int totalFragments = (frameData.Length + maxPayload - 1) / maxPayload;

        for (ushort i = 0; i < totalFragments; i++)
        {
            int offset = i * maxPayload;
            int size = Math.Min(maxPayload, frameData.Length - offset);
            
            byte[] packet = new byte[8 + size];
            
            // Write header (frameId, totalFragments, fragmentIndex)
            Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)frameId)), 0, packet, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)totalFragments)), 0, packet, 4, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)i)), 0, packet, 6, 2);
            
            // Copy payload
            Buffer.BlockCopy(frameData, offset, packet, 8, size);

            if (targetPort > -1 && targetIP.Length>0)
            {
                udpClient.Send(packet, packet.Length, targetIP, targetPort);
            }
        }
    }

    private void OnDestroy()
    {
        isStreaming = false;
        
        if (udpClient != null)
        {
            udpClient.Close();
            udpClient = null;
        }

        if (renderTex != null)
        {
            renderTex.Release();
            Destroy(renderTex);
        }

        if (outputTex != null)
        {
            Destroy(outputTex);
        }

        if (sourceCam != null)
        {
            sourceCam.targetTexture = null;
        }
    }
}
