using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.ROSTCPEndpoint;

public class InputDetectorLeft : MonoBehaviour
{
    private InputDevice leftTargetDevice;

    private bool posStatus = false;
    private bool rotStatus = false;

    ROSConnection ros;
    public string leftControllerTopic = "leftControllerInfo";
    public float publishMessageFrequency = 0.05f;
    private float timeElapsed;
    private bool controllerFound = false;

    // Haptic Feedback
    [Range(0, 1)]
    public float intensity;
    public float duration;

    // Start is called before the first frame update
    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<ControllerInputMsg>(leftControllerTopic);

        this.registerController();
    }

    private void registerController()
    {
        List<InputDevice> devices = new List<InputDevice>();
        InputDeviceCharacteristics leftControllerCharacteristics = InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller;
        InputDevices.GetDevicesWithCharacteristics(leftControllerCharacteristics, devices);

        foreach (var item in devices)
        {
            Debug.Log(item.name + item.characteristics);
        }

        if (devices.Count > 0)
        {
            leftTargetDevice = devices[0];
            controllerFound = true;
            Debug.Log(leftTargetDevice);
        }
    }

    public void TriggerHaptic(InputDevice targetDevice)
    {
        if (intensity > 0)
        {
            targetDevice.SendHapticImpulse(0, intensity, duration);
        }
    }

    // Update is called once per frame
    void Update()
    {
        if (!controllerFound)
        {
            this.registerController();
        }
        // Left Controller Primary Button
        if (leftTargetDevice.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryButtonValue) && primaryButtonValue)
        {
            Debug.Log("Pressing 'X'");
        }

        // Left Controller Secondary Button
        if (leftTargetDevice.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondaryButtonValue) && secondaryButtonValue)
        {
            Debug.Log("Pressing 'Y'");
        }

        // Left Controller Trigger Button
        if (leftTargetDevice.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerButtonValue) && triggerButtonValue)
        {
            // Debug.Log("Pressing Left Trigger");
        }

        // Left Controller Trigger Value
        if (leftTargetDevice.TryGetFeatureValue(CommonUsages.trigger, out float triggerValue) && triggerValue > 0.001)
        {
            Debug.Log("Left Trigger " + triggerValue);
        }

        // Left Controller Grip Button
        if (leftTargetDevice.TryGetFeatureValue(CommonUsages.gripButton, out bool gripButtonValue) && gripButtonValue)
        {
            Debug.Log("Pressing Left Grip");
        }

        // Left Controller Joystick Button
        if (leftTargetDevice.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool primary2DAxisClickValue) && primary2DAxisClickValue)
        {
            Debug.Log("Pressing Left Controller Joystick Button");
        }

        // Left Controller Joystick
        if (leftTargetDevice.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 primary2DAxisValue) && primary2DAxisValue != Vector2.zero)
        {
            Debug.Log("Left Controller Joystick: " + primary2DAxisValue);
        }

        // Left Controller Position
        if (leftTargetDevice.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 devicePositionValue) && posStatus)
        {
            Debug.Log("Left Controller Position: " + devicePositionValue);
        }

        // Left Controller Rotation
        if (leftTargetDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion deviceRotationValue) && rotStatus)
        {
            Debug.Log("Left Controller Rotation: " + deviceRotationValue);
        }

        timeElapsed += Time.deltaTime;

        if (timeElapsed > publishMessageFrequency)
        {
            ControllerInputMsg leftControllerInfo = new ControllerInputMsg(
                primaryButtonValue,
                secondaryButtonValue,
                triggerButtonValue,
                triggerValue,
                gripButtonValue,
                primary2DAxisClickValue,
                primary2DAxisValue.x,
                primary2DAxisValue.y,
                devicePositionValue.x,
                devicePositionValue.y,
                devicePositionValue.z,
                deviceRotationValue.x,
                deviceRotationValue.y,
                deviceRotationValue.z,
                deviceRotationValue.w
                );

            ros.Publish(leftControllerTopic, leftControllerInfo);

            timeElapsed = 0;
        }
    }
}
