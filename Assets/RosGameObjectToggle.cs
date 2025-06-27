using System.Collections;
using System.Collections.Generic;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

public class RosGameObjectToggle : MonoBehaviour
{

    public string topicName = "";
    public GameObject gameObject;
    public bool invert = false;
    private ROSConnection ros;
    // Start is called before the first frame update
    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.Subscribe<BoolMsg>(topicName, msg =>
        {
            gameObject.SetActive(invert?!msg.data:msg.data);
        });
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
