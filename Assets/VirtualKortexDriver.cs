using System;
using System.Collections;
using System.Collections.Generic;
using RosMessageTypes.KortexDriver;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

public class VirtualKortexDriver : MonoBehaviour
{
    public string robotName;
    
    //[Tooltip("List of ArticulationBodies representing the joints. Names should match joint names in URDF.")]
    //public List<ArticulationBody> jointArticulations;
    
    [SerializeField] public ArticulationArmController armController;

    
    private ROSConnection ros;

    private bool connected = false;
    
    // Start is called before the first frame update
    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        
    }
    
    

    // Update is called once per frame
    void Update()
    {
        if (Time.fixedTimeAsDouble > 10 && !connected)
        {
            ros.Subscribe<JointAnglesMsg>($"/{robotName}/relaxed_ik/joint_angle_solutions", msg =>
            {
                
                    List<float> targets = new List<float>();
                    for (int i = 0; i < msg.joint_angles.Length; i++)
                    {
                        targets.Add(msg.joint_angles[i].value);
                    }
                    armController.SetJointAngles(targets.ToArray());
                    armController.MoveToTarget();
                
            
            });
            connected = true;
        }
    }
}
