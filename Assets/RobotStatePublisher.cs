using System;
using System.Collections;
using System.Collections.Generic;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

public class RobotStatePublisher : MonoBehaviour
{
    public int robotIndex = 0;
    public bool publishSteering = true;
    public bool publishSpeed = true;

    public bool connected = false;
    protected ROSConnection ros;
    // Start is called before the first frame update
    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        if (publishSteering)
        {
            ros.RegisterPublisher<Float64Msg>("robot/angular_velocity");
        }

        if (publishSpeed)
        {
            ros.RegisterPublisher<Float64Msg>("robot/linear_velocity");
        }
    }

    public bool ConnectToTask()
    {
        if (TaskEnvironment.instances.Count > TaskEnvironment.currentIndex)
        {
            Trial trial = TaskEnvironment.instances[TaskEnvironment.currentIndex].trial;
            

            return true;
        }
        return false;
    }

    // Update is called once per frame
    void Update()
    {
        
        GameObject robot =
            TaskEnvironment.instances[TaskEnvironment.currentIndex].getObjectListByKey("robots")[robotIndex];
        Rigidbody body = robot.GetComponent<Rigidbody>();
        if (publishSteering)
        {
                
            if (body != null)
            {
                ros.Publish("robot/angular_velocity", new Float64Msg(body.angularVelocity.y));
            }
        }

        if (publishSpeed)
        {
            if (body != null)
            {
                ros.Publish("robot/linear_velocity", new Float64Msg(body.velocity.magnitude));
            }
        }
    }
}
