using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Sensor;
using System;
using RosMessageTypes.BuiltinInterfaces;

public class CanvasToRosCompressedImage : MonoBehaviour
{
    public string rosTopic = "/ui/canvas_image/compressed";
    public int jpegQuality = 75; // 1–100
    public float publishHz = 25f;

    private ROSConnection ros;
    private Canvas canvas;
    private float timer = 0f;
    private Texture2D tex;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();

        // Register publisher
        ros.RegisterPublisher<CompressedImageMsg>(rosTopic);

        canvas = GetComponentInParent<Canvas>();
        if (canvas == null)
        {
            Debug.LogError("CanvasToRosCompressedImage requires a Canvas in its parents.");
            enabled = false;
            return;
        }

        if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
        {
            Debug.LogWarning("Canvas is not Screen Space Overlay. Script assumes Screen Space Overlay only.");
        }

        tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
    }

    void Update()
    {
        timer += Time.deltaTime;
        if (timer < (1f / publishHz))
            return;

        timer = 0f;

        CaptureAndPublish();
    }

    private void CaptureAndPublish()
    {
        // Capture entire screen (Screen Space Overlay UI is drawn on top)
        tex.Resize(Screen.width, Screen.height);
        tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
        tex.Apply();

        // Encode to JPEG
        byte[] jpg = tex.EncodeToJPG(jpegQuality);

        // Create ROS compressed image message
        CompressedImageMsg msg = new CompressedImageMsg
        {
            header = new RosMessageTypes.Std.HeaderMsg
            {
                stamp = new TimeMsg(),
                frame_id = "canvas"
            },
            format = "jpeg",
            data = jpg
        };

        // Publish
        ros.Publish(rosTopic, msg);
    }
}
