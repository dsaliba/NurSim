using System.Collections.Generic;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

public class VirtualChest : MonoBehaviour
{
    public string topicName = "/chest_logger/logger_info";
    public string controlTopicName = "/chest_control/velocity_fraction";

    // ✅ NEW TOPICS
    public string homedTopic = "/chest_logger/is_homed";
    public string positionTopic = "/chest_logger/current_position";

    public float maxVelocity = 1.0f;
    public ArticulationBody chest;

    private ROSConnection ros;

    private float originalLowerLimit;
    private float originalUpperLimit;
    private bool isFixed = false;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();

        ros.RegisterPublisher<StringMsg>(topicName);

        // ✅ NEW publishers
        ros.RegisterPublisher<BoolMsg>(homedTopic);
        ros.RegisterPublisher<Float32Msg>(positionTopic);

        var drive = chest.xDrive;
        originalLowerLimit = drive.lowerLimit;
        originalUpperLimit = drive.upperLimit;

        ros.Subscribe<Float32Msg>(controlTopicName, msg =>
        {
            var targetVelocity = maxVelocity * msg.data;

            if (Mathf.Abs(targetVelocity) > 0.0001f)
            {
                if (isFixed) UnlockChest();

                var drive = chest.xDrive;
                drive.targetVelocity = -targetVelocity;
                chest.xDrive = drive;
            }
            else
            {
                if (!isFixed) LockChest();

                var drive = chest.xDrive;
                drive.targetVelocity = 0f;
                chest.xDrive = drive;

                chest.velocity = Vector3.zero;
                chest.angularVelocity = Vector3.zero;
            }
        });
    }

    void LockChest()
    {
        float currentPos = chest.jointPosition[0];

        var drive = chest.xDrive;
        drive.lowerLimit = currentPos;
        drive.upperLimit = currentPos;
        chest.xDrive = drive;

        isFixed = true;
    }

    void UnlockChest()
    {
        var drive = chest.xDrive;
        drive.lowerLimit = originalLowerLimit;
        drive.upperLimit = originalUpperLimit;
        chest.xDrive = drive;

        isFixed = false;
    }

    void Update()
    {
        float currentPosition = chest.jointPosition[0];
        float currentVelocity = chest.jointVelocity[0];

        bool upperLimitReached = currentPosition >= chest.xDrive.upperLimit;
        bool lowerLimitReached = currentPosition <= chest.xDrive.lowerLimit;

        // ✅ Publish homing shim (ALWAYS TRUE in sim)
        ros.Publish(homedTopic, new BoolMsg(true));

        // ✅ Publish position for initialization logic
        ros.Publish(positionTopic, new Float32Msg(currentPosition));

        // Existing JSON logger (unchanged)
        string message = $"{{\"Brake\":{{\"Active\":1,\"ABS\":true}}," +
                         $"\"Motor\":{{\"Homed\":true,\"CurrentPosition\":{currentPosition * 1000}," +
                         $"\"CurrentVelocity\":{currentVelocity / 1000}," +
                         $"\"FailedState\":false,\"Enabled\":true}}," +
                         $"\"Limits\":{{\"UpperLimitReached\":{upperLimitReached.ToString().ToLower()}," +
                         $"\"LowerLimitReached\":{lowerLimitReached.ToString().ToLower()}}}}}";

        ros.Publish(topicName, new StringMsg(message));
    }
}
