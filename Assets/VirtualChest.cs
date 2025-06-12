using System;
using System.Collections;
using System.Collections.Generic;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

public class VirtualChest : MonoBehaviour
{
    public string topicName = "/chest_logger/logger_info";
    public ArticulationBody chest;
    ROSConnection ros;
    
    
    // Start is called before the first frame update
    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<StringMsg>(topicName);
    }

    // Update is called once per frame
    void Update()
    {
        ros.Publish(topicName, new StringMsg($"{{\"Brake\":{{\"Active\":1,\"ABS\":true}},\"Motor\":{{\"Homed\":true,\"CurrentPosition\":{chest.jointPosition[0]/1000},\"CurrentVelocity\":{chest.jointVelocity[0]/1000},\"FailedState\":false,\"Enabled\":true}},\"Limits\":{{\"UpperLimitReached\":false,\"LowerLimitReached\":false}}}}"));
    }
}
