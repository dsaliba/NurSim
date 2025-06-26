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
            // Set velocity on the prismatic joint (X-axis)
            var targetVelocity = maxVelocity * msg.data;
            var drive = chest.xDrive;
            drive.targetVelocity = -targetVelocity;
            chest.xDrive = drive;
        });
    }

    // Update is called once per frame
    void Update()
    {
        // Get joint position (this should be on the prismatic joint, typically chest.jointPosition[0])
        float currentPosition = chest.jointPosition[0];
        float currentVelocity = chest.jointVelocity[0];

        // Get joint limits
        bool upperLimitReached = currentPosition >= chest.xDrive.upperLimit;
        bool lowerLimitReached = currentPosition <= chest.xDrive.lowerLimit;

        // Create the message
        string message = $"{{\"Brake\":{{\"Active\":1,\"ABS\":true}},\"Motor\":{{\"Homed\":true,\"CurrentPosition\":{currentPosition*1000},\"CurrentVelocity\":{currentVelocity / 1000},\"FailedState\":false,\"Enabled\":true}},\"Limits\":{{\"UpperLimitReached\":{upperLimitReached.ToString().ToLower()},\"LowerLimitReached\":{lowerLimitReached.ToString().ToLower()}}}}}";

        // Publish the message
        ros.Publish(topicName, new StringMsg(message));
    }
}
