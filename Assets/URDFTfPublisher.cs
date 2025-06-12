using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Geometry;
using RosMessageTypes.Tf2;
using RosMessageTypes.Rosgraph;

public class URDFTfPublisher : MonoBehaviour
{
    [Tooltip("Root link of the robot, e.g., base_link")]
    public GameObject baseLink;

    [Tooltip("Original robot name from URDF (e.g., robot_name)")]
    public string originalRobotName = "robot_name";

    [Tooltip("Robot name to use in TF frames (e.g., new_robot_name)")]
    public string targetRobotName = "new_robot_name";

    [Tooltip("TF publish rate in Hz")]
    public float publishRateHz = 10.0f;

    private float publishInterval;
    private float timeSinceLastPublish = 0f;

    private ROSConnection ros;
    private const string tfTopic = "/tf";

    private List<Transform> linkTransforms = new List<Transform>();

    private TimeMsg currentRosTime = new TimeMsg();
    void ClockCallback(ClockMsg clockMsg)
    {
        currentRosTime = clockMsg.clock;
    }

    void Start()
    {
        if (baseLink == null)
        {
            Debug.LogError("Base link GameObject is not assigned!");
            enabled = false;
            return;
        }

        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<TFMessageMsg>(tfTopic);

        publishInterval = 1.0f / publishRateHz;

        FindLinkTransforms(baseLink.transform);
        ros.Subscribe<ClockMsg>("/clock", ClockCallback);
    }

    void Update()
    {
        timeSinceLastPublish += Time.deltaTime;
        if (timeSinceLastPublish >= publishInterval)
        {
            PublishTF();
            timeSinceLastPublish = 0f;
        }
    }



    void FindLinkTransforms(Transform current)
    {
        // Only add links with names like "robot_name/..."
        if (current.name.StartsWith(originalRobotName + "/"))
        {
            linkTransforms.Add(current);
        }

        foreach (Transform child in current)
        {
            FindLinkTransforms(child);
        }
    }

    void PublishTF()
    {
        var tfMessage = new TFMessageMsg();
        var transforms = new List<TransformStampedMsg>();

        foreach (Transform link in linkTransforms)
        {
            // Get the part after robot_name/
            string relativeName = link.name.Substring(originalRobotName.Length + 1); // skip "robot_name/"

            string frameId = $"{relativeName}";

            string parentFrameId;

            if (link == baseLink.transform)
            {
                parentFrameId = "world";
            }
            else
            {
                Transform parent = link.parent;
                if (parent == null || !parent.name.StartsWith(originalRobotName + "/"))
                    continue;

                string parentRelative = parent.name.Substring(originalRobotName.Length + 1);
                parentFrameId = $"{parentRelative}";
            }

            DateTime now = DateTime.UtcNow;
            TimeSpan sinceEpoch = now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var tf = new TransformStampedMsg
            {
                header = new RosMessageTypes.Std.HeaderMsg
                {
                    stamp = new TimeMsg
                    {
                        sec = (uint)sinceEpoch.TotalSeconds,
                        nanosec = (uint)((sinceEpoch.TotalSeconds - Math.Floor(sinceEpoch.TotalSeconds)) * 1e9)
                    },
                    frame_id = parentFrameId
                },
                child_frame_id = frameId,
                transform = new TransformMsg
                {
                    translation = link.localPosition.To<FLU>(),
                    rotation = link.localRotation.To<FLU>()
                }
            };

            transforms.Add(tf);
        }

        tfMessage.transforms = transforms.ToArray();
        ros.Publish(tfTopic, tfMessage);
    }
}
