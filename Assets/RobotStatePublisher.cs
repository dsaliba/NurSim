using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RobotStatePublisher : MonoBehaviour
{
    public int robotIndex = 0;
    public bool publishSteering = true;
    public bool publishSpeed = true;

    public bool connected = false;
    // Start is called before the first frame update
    void Start()
    {
        
    }

    public bool ConnectToTask()
    {
        if (TaskEnvironment.instances.Count > TaskEnvironment.currentIndex)
        {
            Trial trial = TaskEnvironment.instances[TaskEnvironment.currentIndex].trial;
            if (publishSteering)
            {
                trial.AddLiveNumber(new Trial.LiveNumber("robot/angular_velocity", 0, -1, 1));
            }

            if (publishSpeed)
            {
                trial.AddLiveNumber(new Trial.LiveNumber("robot/linear_velocity", 0, 0, 1));
            }

            return true;
        }
        return false;
    }

    // Update is called once per frame
    void Update()
    {
        if (!connected) connected = ConnectToTask();
        if (connected)
        {
            Trial trial = TaskEnvironment.instances[TaskEnvironment.currentIndex].trial;
            GameObject robot = TaskEnvironment.instances[TaskEnvironment.currentIndex].getObjectListByKey("robots")[robotIndex];
            Trial.LiveNumber angularVelocity= Array.Find(trial.liveNumbers, number => number.name.Equals("robot/angular_velocity"));
            Trial.LiveNumber linearVelocity= Array.Find(trial.liveNumbers, number => number.name.Equals("robot/linear_velocity"));
            Rigidbody body = robot.GetComponent<Rigidbody>();
            if (publishSteering)
            {
                
                if (body != null)
                {
                    angularVelocity.value = body.angularVelocity.y;
                }
            }

            if (publishSpeed)
            {
                if (body != null)
                {
                    linearVelocity.value = body.velocity.magnitude;
                }
            }
        }
    }
}
