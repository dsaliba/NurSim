using System.Collections;
using System.Collections.Generic;
using RosMessageTypes.ROSTCPEndpoint;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

public class VirtualActiveNeck : MonoBehaviour
{
    public string topicName;
    public Transform neckTransform;
    private ROSConnection ros;
    private Quaternion lastRot;
    
    // Start is called before the first frame update
    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.Subscribe<ControllerInputMsg>(topicName, msg =>
        {
            lastRot = new Quaternion(msg.controller_rot_x, msg.controller_rot_y, 0, msg.controller_rot_w);
        });
    }

    // Update is called once per frame
    void Update()
    {
        neckTransform.localRotation = lastRot;
    }
}
