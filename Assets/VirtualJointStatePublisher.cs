using System;
using System.Collections.Generic;
using RosMessageTypes.BuiltinInterfaces;
using RosMessageTypes.Rosgraph;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Sensor;
using RosMessageTypes.Std;
using RosMessageTypes.KortexDriver;
using RosMessageTypes.KinovaPositionalControl;

public class VirtualJointStatePublisher : MonoBehaviour
{
    [Tooltip("ROS topic to publish JointState messages to (e.g., /joint_states)")]
    public string jointStateTopic = "/joint_states";

    public string baseFeedbackTopic = "/base_feedback";
    public string gripCommmandServiceName = "/base/send_gripper_command";

    public string robotName;

    [Tooltip("Rate at which to publish JointState messages (Hz)")]
    public float publishRateHz = 30f;

    [Tooltip("List of ArticulationBodies representing the joints. Names should match joint names in URDF.")]
    public List<ArticulationBody> jointArticulations;

    public ArticulationBody leftGripperFinger;
    public ArticulationBody rightGripperFinger;

    private float publishInterval;
    private float timeSinceLastPublish;

    private ROSConnection ros;

    // ---- Minimal changes for better gripper control ----
    [Header("Gripper Control Settings")]
    [SerializeField] private float gripperClosedPosition = -0.02f;
    [SerializeField] private float gripperOpenPosition = 0.0f;
    [SerializeField] private float gripperMoveSpeed = 0.02f; // meters per second
    [SerializeField] private float gripperStiffness = 15000f;
    [SerializeField] private float gripperDamping = 500f;
    [SerializeField] private float gripperForceLimit = 1000f;

    private float desiredGripperPosition = 0f;
    private float currentGripperPosition = 0f;
    // -----------------------------------------------------

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

        ros.ImplementService<SendGripperCommandRequest, SendGripperCommandResponse>(gripCommmandServiceName, GripCommand);

        publishInterval = 1f / publishRateHz;
        timeSinceLastPublish = 0f;
    }

    public SendGripperCommandResponse GripCommand(SendGripperCommandRequest request)
    {
        float commandValue = request.input.gripper.finger[0].value;

        // Zero means open, non-zero means close
        if (commandValue == 0)
            desiredGripperPosition = gripperOpenPosition;
        else
            desiredGripperPosition = gripperClosedPosition;

        Debug.Log($"[Gripper] Command received: {commandValue} → TargetPos: {desiredGripperPosition}");

        return new SendGripperCommandResponse();
    }

    private void ApplyGripperPosition(ArticulationBody finger, float position)
    {
        ArticulationDrive drive = finger.xDrive;
        drive.stiffness = gripperStiffness;
        drive.damping = gripperDamping;
        drive.forceLimit = gripperForceLimit;
        drive.target = position;
        finger.xDrive = drive;
    }

    void FixedUpdate()
    {
        // Smoothly move toward desired position
        currentGripperPosition = Mathf.MoveTowards(
            currentGripperPosition,
            desiredGripperPosition,
            gripperMoveSpeed * Time.fixedDeltaTime
        );

        ApplyGripperPosition(leftGripperFinger, currentGripperPosition);
        ApplyGripperPosition(rightGripperFinger, -currentGripperPosition); // mirror

        // ROS publish timer
        timeSinceLastPublish += Time.fixedDeltaTime;
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
        basemsg.interconnect.oneof_tool_feedback.gripper_feedback = new GripperCyclic_FeedbackMsg[] { new GripperCyclic_FeedbackMsg() };
        basemsg.interconnect.oneof_tool_feedback.gripper_feedback[0].motor = new MotorFeedbackMsg[] { new MotorFeedbackMsg() };

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
                string jointName = $"{robotName}/joint_{i + 1}";

                msg.name[i] = jointName;
                msg.position[i] = joint.jointPosition[0];
                msg.velocity[i] = joint.jointVelocity[0];
                msg.effort[i] = joint.jointForce[0];
            }

            ros.Publish(jointStateTopic, msg);
        }
    }
}
