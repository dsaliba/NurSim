using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class SingleCamera : MonoBehaviour
{
    public string cameraName;

    private Camera cameraObject;
    
    private RawImage rawImage;
    private RenderTexture renderTexture;

    private bool connected = false;
    
    // Start is called before the first frame update
    void Start()
    {
        
        
    }

    private bool ConnectToCamera()
    {
        if (TaskEnvironment.instances.Count == 0)
        {
            return false;
        } 
        foreach (GameObject camera in TaskEnvironment.instances[TaskEnvironment.currentIndex].getObjectListByKey("cameras"))
        {
            if (camera.name.Equals(cameraName))
            {
                cameraObject = camera.GetComponent<Camera>();
            }
        }

        if (cameraObject == null) return false;

        rawImage = GetComponent<RawImage>();

        // Use the camera's pixel dimensions if available
        int width = cameraObject.pixelWidth;
        int height = cameraObject.pixelHeight;

        // Fallback to screen size if camera size is zero (can happen on first frame)
        if (width == 0 || height == 0)
        {
            width = Screen.width;
            height = Screen.height;
        }

        // Create and assign the RenderTexture
        renderTexture = new RenderTexture(width, height, 16);
        renderTexture.Create();

        cameraObject.targetTexture = renderTexture;
        rawImage.texture = renderTexture;

        // Resize the RawImage to match the aspect ratio of the camera
        RectTransform rt = rawImage.rectTransform;
        return true;
    }

    // Update is called once per frame
    void Update()
    {
        if (!connected) connected = ConnectToCamera();
    }
}
