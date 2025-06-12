using System.Linq;
using System.Collections;
using System.Collections.Generic;
using RosMessageTypes.Geometry;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;


public class BaseDriver : MonoBehaviour
{
    public string topicName;
    // Local wheel controller
    [SerializeField] private ArticulationWheelController wheelController;
    

    private ROSConnection ros;

    public void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.Subscribe<TwistMsg>(topicName, msg =>
        {
            wheelController.SetRobotSpeedStep(
                (float) msg.linear.x,
                (float) msg.angular.z);
        });
    }
    
    
}
