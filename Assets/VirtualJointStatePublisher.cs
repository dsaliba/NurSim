using System;
using System.Collections.Generic;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Rosgraph;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Sensor;
using RosMessageTypes.Std;
using RosMessageTypes.KortexDriver;

public class VirtualJointStatePublisher : MonoBehaviour
{
    [Tooltip("ROS topic to publish JointState messages to (e.g., /joint_states)")]
    public string jointStateTopic = "/joint_states";

    public string baseFeedbackTopic = "/base_feedback";

    public string robotName;

    [Tooltip("Rate at which to publish JointState messages (Hz)")]
    public float publishRateHz = 30f;

    [Tooltip("List of ArticulationBodies representing the joints. Names should match joint names in URDF.")]
    public List<ArticulationBody> jointArticulations;

    private float publishInterval;
    private float timeSinceLastPublish;

    private ROSConnection ros;
    
    private TimeMsg currentRosTime;

    


    void ClockCallback(ClockMsg clockMsg)
    {
        currentRosTime = clockMsg.clock;
    }

    void Start()
    {
        if (jointArticulations == null || jointArticulations.Count == 0)
        {
            Debug.LogError("No articulation joints assigned.");
            enabled = false;
            return;
        }

        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<JointStateMsg>(jointStateTopic);
        ros.RegisterPublisher<BaseCyclic_FeedbackMsg>(baseFeedbackTopic);

        publishInterval = 1f / publishRateHz;
        timeSinceLastPublish = 0f;
        ros.Subscribe<ClockMsg>("/clock", ClockCallback);
    }

    void Update()
    {
        timeSinceLastPublish += Time.deltaTime;
        if (timeSinceLastPublish >= publishInterval)
        {
            PublishJointState();
            timeSinceLastPublish = 0f;
        }
    }

    void PublishJointState()
    {
        BaseCyclic_FeedbackMsg basemsg = new BaseCyclic_FeedbackMsg();
        basemsg.interconnect = new InterconnectCyclic_FeedbackMsg();
        basemsg.interconnect.oneof_tool_feedback = new InterconnectCyclic_Feedback_tool_feedbackMsg();
        basemsg.interconnect.oneof_tool_feedback.gripper_feedback = new GripperCyclic_FeedbackMsg[] { new GripperCyclic_FeedbackMsg()};
        basemsg.interconnect.oneof_tool_feedback.gripper_feedback[0].motor = new MotorFeedbackMsg[] {new MotorFeedbackMsg()};
        //basemsg.interconnect.oneof_tool_feedback.gripper_feedback[0].motor[0].

        ros.Publish(baseFeedbackTopic, basemsg);
        if (Time.fixedTimeAsDouble > 10)
        {
            int count = jointArticulations.Count;
            DateTime now = DateTime.UtcNow;
            TimeSpan sinceEpoch = now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var msg = new JointStateMsg
            {
                header = new HeaderMsg
                {
                    stamp = new TimeMsg
                    {
                        sec = (uint)sinceEpoch.TotalSeconds,
                        nanosec = (uint)((sinceEpoch.TotalSeconds - Math.Floor(sinceEpoch.TotalSeconds)) * 1e9)
                    },
                    frame_id = "" // Typically empty for JointState
                },
                name = new string[count],
                position = new double[count],
                velocity = new double[count],
                effort = new double[count]
            };

            for (int i = 0; i < count; i++)
            {
                var joint = jointArticulations[i];
                string jointName = $"{robotName}/joint_{i+1}";

                // Get joint state (position, velocity, effort)
                float jointPosition = joint.jointPosition[0]; // assumes 1-DOF
                float jointVelocity = joint.jointVelocity[0];
                float jointEffort = joint.jointForce[0];

                msg.name[i] = jointName;
                msg.position[i] = jointPosition;
                msg.velocity[i] = jointVelocity;
                msg.effort[i] = jointEffort;
            }

            ros.Publish(jointStateTopic, msg);
        }
        
    }
}
