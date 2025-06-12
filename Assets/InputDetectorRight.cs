using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.ROSTCPEndpoint;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;

public class InputDetectorRight : MonoBehaviour
{
    private InputDevice rightTargetDevice;

    private bool posStatus = false;
    private bool rotStatus = false;

    ROSConnection ros;
    public string rightControllerTopic = "rightControllerInfo";
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
        ros.RegisterPublisher<ControllerInputMsg>(rightControllerTopic);

        this.registerController();
    }

    private void registerController()
    {
        List<InputDevice> devices = new List<InputDevice>();
        InputDeviceCharacteristics rightControllerCharacteristics = InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller;
        InputDevices.GetDevicesWithCharacteristics(rightControllerCharacteristics, devices);

        foreach (var item in devices)
        {
            Debug.Log(item.name + item.characteristics);
        }

        if (devices.Count > 0)
        {
            rightTargetDevice = devices[0];
            controllerFound = true;
            Debug.Log(rightTargetDevice);
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
        // Right Controller Primary Button
        if (rightTargetDevice.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryButtonValue) && primaryButtonValue)
        {
            Debug.Log("Pressing 'A'");
        }

        // Right Controller Secondary Button
        if (rightTargetDevice.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondaryButtonValue) && secondaryButtonValue)
        {
            Debug.Log("Pressing 'B'");
        }

        // Right Controller Trigger Button
        if (rightTargetDevice.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerButtonValue) && triggerButtonValue)
        {
            // Debug.Log("Pressing Right Trigger");
        }

        // Right Controller Trigger Value
        if (rightTargetDevice.TryGetFeatureValue(CommonUsages.trigger, out float triggerValue) && triggerValue > 0.001)
        {
            Debug.Log("Right Trigger " + triggerValue);
        }

        // Right Controller Grip Button
        if (rightTargetDevice.TryGetFeatureValue(CommonUsages.gripButton, out bool gripButtonValue) && gripButtonValue)
        {
            Debug.Log("Pressing Right Grip");
        }

        // Right Controller Joystick Button
        if (rightTargetDevice.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool primary2DAxisClickValue) && primary2DAxisClickValue)
        {
            Debug.Log("Pressing Right Controller Joystick Button");
        }

        // Right Controller Joystick
        if (rightTargetDevice.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 primary2DAxisValue) && primary2DAxisValue != Vector2.zero)
        {
            Debug.Log("Right Controller Joystick: " + primary2DAxisValue);
        }

        // Right Controller Position
        if (rightTargetDevice.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 devicePositionValue) && posStatus)
        {
            Debug.Log("Right Controller Position: " + devicePositionValue);
            //devicePositionValue = FRD.ConvertToRUF(devicePositionValue);
        }

        // Right Controller Rotation
        if (rightTargetDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion deviceRotationValue) && rotStatus)
        {
            Debug.Log("Right Controller Rotation: " + deviceRotationValue);
            //deviceRotationValue = FRD.ConvertToRUF(deviceRotationValue);
        }

        timeElapsed += Time.deltaTime;

        if (timeElapsed > publishMessageFrequency)
        {
            ControllerInputMsg rightControllerInfo = new ControllerInputMsg(
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

            ros.Publish(rightControllerTopic, rightControllerInfo);

            timeElapsed = 0;
        }
    }
}
