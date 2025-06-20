using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.ROSTCPEndpoint;
using System.Collections;

public class XRInputPublisher : MonoBehaviour
{
    [Header("Input Action Asset")]
    public InputActionAsset xriInputActions;

    [Header("ROS Topics")]
    public string leftControllerTopic = "/left_controller_input";
    public string rightControllerTopic = "/right_controller_input";
    public string headsetTopic = "/headset_input";

    [Header("Publishing Settings")]
    public float messagesPerSecond = 30f;

    private ROSConnection ros;

    // LEFT HAND
    private InputAction leftPrimary;
    private InputAction leftSecondary;
    private InputAction leftSelect;
    private InputAction leftActivate;
    private InputAction leftUIPress;
    private InputAction leftJoystick;
    private InputAction leftThumbstickClick;
    private InputAction leftPosition;
    private InputAction leftRotation;

    // RIGHT HAND
    private InputAction rightPrimary;
    private InputAction rightSecondary;
    private InputAction rightSelect;
    private InputAction rightActivate;
    private InputAction rightUIPress;
    private InputAction rightJoystick;
    private InputAction rightThumbstickClick;
    private InputAction rightPosition;
    private InputAction rightRotation;

    // HEADSET
    private InputAction headsetPosition;
    private InputAction headsetRotation;

    private void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<ControllerInputMsg>(leftControllerTopic);
        ros.RegisterPublisher<ControllerInputMsg>(rightControllerTopic);
        ros.RegisterPublisher<ControllerInputMsg>(headsetTopic);

        // Safety wrapper for action lookup
        InputAction TryFind(string name)
        {
            var action = xriInputActions.FindAction(name);
            if (action == null)
                Debug.LogError($"Missing input action: {name}");
            return action;
        }

        // LEFT HAND
        leftPrimary = TryFind("XRI LeftHand Interaction/Primary Button");
        leftSecondary = TryFind("XRI LeftHand Interaction/Secondary Button");
        leftSelect = TryFind("XRI LeftHand Interaction/Select");
        leftActivate = TryFind("XRI LeftHand Interaction/Activate");
        leftUIPress = TryFind("XRI LeftHand Interaction/UI Press");
        leftJoystick = TryFind("XRI LeftHand Locomotion/Move");
        leftThumbstickClick = TryFind("XRI LeftHand Locomotion/Thumbstick Click");
        leftPosition = TryFind("XRI LeftHand/Position");
        leftRotation = TryFind("XRI LeftHand/Rotation");

        // RIGHT HAND
        rightPrimary = TryFind("XRI RightHand Interaction/Primary Button");
        rightSecondary = TryFind("XRI RightHand Interaction/Secondary Button");
        rightSelect = TryFind("XRI RightHand Interaction/Select");
        rightActivate = TryFind("XRI RightHand Interaction/Activate");
        rightUIPress = TryFind("XRI RightHand Interaction/UI Press");
        rightJoystick = TryFind("XRI RightHand Locomotion/Move");
        rightThumbstickClick = TryFind("XRI RightHand Locomotion/Thumbstick Click");
        rightPosition = TryFind("XRI RightHand/Position");
        rightRotation = TryFind("XRI RightHand/Rotation");

        // HEADSET
        headsetPosition = TryFind("XRI Head/Position");
        headsetRotation = TryFind("XRI Head/Rotation");

        xriInputActions.Enable();

        StartCoroutine(PublishLoop());
    }

    private IEnumerator PublishLoop()
    {
        float waitTime = 1f / Mathf.Max(messagesPerSecond, 0.01f);

        while (true)
        {
            PublishAll();
            yield return new WaitForSeconds(waitTime);
        }
    }

    private void PublishAll()
    {
        // LEFT
        PublishController(
            leftPrimary?.IsPressed() ?? false,
            leftSecondary?.IsPressed() ?? false,
            leftActivate?.IsPressed() ?? false,
            leftActivate?.ReadValue<float>() ?? 0f,
            leftSelect?.IsPressed() ?? false,
            leftThumbstickClick?.IsPressed() ?? false,
            leftJoystick?.ReadValue<Vector2>() ?? Vector2.zero,
            leftPosition?.ReadValue<Vector3>() ?? Vector3.zero,
            leftRotation?.ReadValue<Quaternion>() ?? Quaternion.identity,
            leftControllerTopic
        );

        // RIGHT
        PublishController(
            rightPrimary?.IsPressed() ?? false,
            rightSecondary?.IsPressed() ?? false,
            rightActivate?.IsPressed() ?? false,
            rightActivate?.ReadValue<float>() ?? 0f,
            rightSelect?.IsPressed() ?? false,
            rightThumbstickClick?.IsPressed() ?? false,
            rightJoystick?.ReadValue<Vector2>() ?? Vector2.zero,
            rightPosition?.ReadValue<Vector3>() ?? Vector3.zero,
            rightRotation?.ReadValue<Quaternion>() ?? Quaternion.identity,
            rightControllerTopic
        );

        // HEADSET (pose only)
        PublishHeadsetPose(
            headsetPosition?.ReadValue<Vector3>() ?? Vector3.zero,
            headsetRotation?.ReadValue<Quaternion>() ?? Quaternion.identity
        );
    }

    private void PublishController(
        bool primary,
        bool secondary,
        bool triggerButton,
        float triggerValue,
        bool gripButton,
        bool joystickButton,
        Vector2 joystick,
        Vector3 pos,
        Quaternion rot,
        string topic
    )
    {
        ControllerInputMsg msg = new ControllerInputMsg(
            primary,
            secondary,
            triggerButton,
            triggerValue,
            gripButton,
            joystickButton,
            joystick.x,
            joystick.y,
            pos.x,
            pos.y,
            pos.z,
            rot.x,
            rot.y,
            rot.z,
            rot.w
        );

        ros.Publish(topic, msg);
    }

    private void PublishHeadsetPose(Vector3 pos, Quaternion rot)
    {
        ControllerInputMsg msg = new ControllerInputMsg(
            false, false, false, 0f, false, false,
            0f, 0f,
            pos.x, pos.y, pos.z,
            rot.x, rot.y, rot.z, rot.w
        );

        ros.Publish(headsetTopic, msg);
    }
}
