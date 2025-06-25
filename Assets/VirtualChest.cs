using System;
using System.Collections;
using System.Collections.Generic;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

public class VirtualChest : MonoBehaviour
{
    public string topicName = "/chest_logger/logger_info";
    public string controlTopicName = "/chest_control/velocity_fraction";
    public float maxVelocity = 1.0f;
    public ArticulationBody chest;
    ROSConnection ros;
    
    
    // Start is called before the first frame update
    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<StringMsg>(topicName);
        ros.Subscribe<Float32Msg>(controlTopicName, msg => {
            //ArticulationBodyUtils.SetJointSpeedStep(chest, maxVelocity * msg.data);
            List<float> vel = new List<float>();
            chest.SetDriveTargetVelocity(ArticulationDriveAxis.X,-maxVelocity * msg.data);
        });
    }

    // Update is called once per frame
    void Update()
    {

        ros.Publish(topicName, new StringMsg($"{{\"Brake\":{{\"Active\":1,\"ABS\":true}},\"Motor\":{{\"Homed\":true,\"CurrentPosition\":0.22,\"CurrentVelocity\":{chest.jointVelocity[0]/1000},\"FailedState\":false,\"Enabled\":true}},\"Limits\":{{\"UpperLimitReached\":false,\"LowerLimitReached\":false}}}}"));
    }

  
}
