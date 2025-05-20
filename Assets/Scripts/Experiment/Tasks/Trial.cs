using System;
using System.Collections;
using System.Collections.Generic;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

public abstract class Trial : MonoBehaviour
{
    private double startTime = -1;
    private double stopTime = -1;
    public TaskEnvironment environment;
    protected ROSConnection ros;
    
    public void UpdateElapsedTime()
    {
        ros.Publish("trial/elapsed_time", new Float64Msg(startTime < 0 ? 0 : stopTime < 0 ? Time.timeAsDouble - startTime : stopTime - startTime));
    }

    public void StartTrial()
    {
        startTime = Time.timeAsDouble;
    }

    public void StopTrial()
    {
        stopTime = Time.timeAsDouble;
    }
    
    
    // Start is called before the first frame update
    public void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<Float64Msg>("trial/elapsed_time");
    }

    // Update is called once per frame
    public void Update()
    {
        UpdateElapsedTime();
    }
}
