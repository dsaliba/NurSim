using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using Unity.Robotics.Core;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;
using RosMessageTypes.Std;
using RosMessageTypes.Geometry;

/// <summary>
///     This script publishes robot stamped pose
/// </summary>
public class PoseStampedPublisher : MonoBehaviour
{
    // ROS Connector
    private ROSConnection ros;
    // Variables required for ROS communication
    [SerializeField] private string poseStampedTopicName = "model_pose";
    [SerializeField] private string frameID = "model_pose";

    // Transform
    [SerializeField] private Transform publishedTransform;

    // Message
    private PoseStampedMsg poseStamped;
    // rate
    [SerializeField] private int publishRate = 10;
    private Timer timer;

    void Start()
    {
        // Get ROS connection static instance
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<PoseStampedMsg>(poseStampedTopicName);

        // Initialize message
        poseStamped = new PoseStampedMsg
        {
            header = new HeaderMsg(
                0, new TimeStamp(Clock.time), frameID
            )
        };

        // Rate
        timer = new Timer(publishRate);
    }

    void FixedUpdate()
    {
        timer.UpdateTimer(Time.fixedDeltaTime);

        if (timer.ShouldProcess)
        {
            PublishPoseStamped();
            timer.ShouldProcess = false;
        }
    }

    private void PublishPoseStamped()
    {
        poseStamped.header.Update();

        poseStamped.pose.position = publishedTransform.position.To<FLU>();
        poseStamped.pose.orientation = publishedTransform.rotation.To<FLU>();

        ros.Publish(poseStampedTopicName, poseStamped);
    }
}
