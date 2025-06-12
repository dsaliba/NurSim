using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.ROSTCPEndpoint;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;

public class InputDetectorHeadset : MonoBehaviour
{
    private InputDevice headsetDevice;

    private bool posStatus = false;
    private bool rotStatus = false;

    ROSConnection ros;
    public string headsetTopic = "headsetInfo";
    public float publishMessageFrequency = 0.05f;
    private float timeElapsed;
    private bool headsetFound = false;

    // Start is called before the first frame update
    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<ControllerInputMsg>(headsetTopic);

        this.registerHeadset();
    }

    private void registerHeadset()
    {
        List<InputDevice> devices = new List<InputDevice>();
        InputDeviceCharacteristics headsetCharacteristics = InputDeviceCharacteristics.HeadMounted;
        InputDevices.GetDevicesWithCharacteristics(headsetCharacteristics, devices);

        foreach (var item in devices)
        {
            Debug.Log(item.name + item.characteristics);
        }

        if (devices.Count > 0)
        {
            headsetDevice = devices[0];
            headsetFound = true;
            Debug.Log(headsetDevice);
        }
    }

    // Update is called once per frame
    void Update()
    {
        if (!headsetFound)
        {
            this.registerHeadset();
        }

        // Headset Position
        if (headsetDevice.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 devicePositionValue) && posStatus)
        {
            Debug.Log("Headset Position: " + devicePositionValue);
            // devicePositionValue = FRD.ConvertToRUF(devicePositionValue);
        }

        // Headset Rotation
        if (headsetDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion deviceRotationValue) && rotStatus)
        {
            Debug.Log("Headset Rotation: " + deviceRotationValue);
            // deviceRotationValue = FRD.ConvertToRUF(deviceRotationValue);
        }

        timeElapsed += Time.deltaTime;

        if (timeElapsed > publishMessageFrequency)
        {
            ControllerInputMsg headsetInfo = new ControllerInputMsg(
                false, // No primaryButton for the headset
                false, // No secondaryButton for the headset
                false, // No triggerButton for the headset
                0.0f,  // No triggerValue for the headset
                false, // No gripButton for the headset
                false, // No joystick button for the headset
                0.0f,  // No joystick x-axis for the headset
                0.0f,  // No joystick y-axis for the headset
                devicePositionValue.x,
                devicePositionValue.y,
                devicePositionValue.z,
                deviceRotationValue.x,
                deviceRotationValue.y,
                deviceRotationValue.z,
                deviceRotationValue.w
                );

            ros.Publish(headsetTopic, headsetInfo);

            timeElapsed = 0;
        }
    }
}
