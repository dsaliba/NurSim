using System;
using System.Collections;
using System.Collections.Generic;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

public class Dial : MonoBehaviour
{
    public string topicName;
    public float topicMin = -1;
    public float topicMax = 1;
    public float rotationMin = -90;
    public float rotationMax = 90;
    public bool flipRotation = false;
    private bool connected;

    public double value;
    private RectTransform rectTransform;
    private int searchAttemptsLeft = 100;
    protected ROSConnection ros;
    
    // Start is called before the first frame update
    void Start()
    {
        rectTransform = GetComponent<RectTransform>();
        ros = ROSConnection.GetOrCreateInstance();
        ros.Subscribe<Float64Msg>(topicName, msg => value = msg.data);
    }

    // Update is called once per frame
    void Update()
    {
        float normalized = (float)(2 * (value - topicMin) / (topicMax - topicMin) - 1) * (flipRotation?-1:1);
            
        rectTransform.rotation = Quaternion.Euler(rectTransform.rotation.eulerAngles.x, rectTransform.rotation.eulerAngles.y, (normalized+1)/2f*(rotationMax-rotationMin) + rotationMin);
    }
}
