using System;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

public class Guage : MonoBehaviour
{
    [Header("ROS Settings")]
    public string topicName;
    public float topicMin = -1;
    public float topicMax = 1;
    public bool flipDirection = false;
    public bool float64 = false;

    [Header("Slider Settings")]
    public float positionMin = -100f; // In local Y space
    public float positionMax = 100f;

    public double value;
    private RectTransform rectTransform;
    protected ROSConnection ros;

    void Start()
    {
        rectTransform = GetComponent<RectTransform>();
        ros = ROSConnection.GetOrCreateInstance();
        if (float64)
        {
            ros.Subscribe<Float64Msg>(topicName, msg => value = msg.data);
        }else
        {
            ros.Subscribe<Float32Msg>(topicName, msg => value = msg.data);
        }
        
    }

    void Update()
    {
        // Normalize value to range 0 - 1
        float normalized = Mathf.InverseLerp(topicMin, topicMax, (float)value);
        if (flipDirection)
            normalized = 1f - normalized;

        // Interpolate vertical position
        float yPos = Mathf.Lerp(positionMin, positionMax, normalized);
        rectTransform.anchoredPosition = new Vector2(rectTransform.anchoredPosition.x, yPos);
    }
}
