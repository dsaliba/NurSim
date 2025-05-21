using System.Collections;
using System.Collections.Generic;
using RosMessageTypes.Std;
using TMPro;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;


public class TextInputSender : MonoBehaviour
{
    public string topic;
    protected ROSConnection ros;
    public TMP_InputField inputField;
    // Start is called before the first frame update
    void Start()
    {
      ros = ROSConnection.GetOrCreateInstance();
      ros.RegisterPublisher<StringMsg>(topic, latch: true);
    }

    public void PublishContent()
    {
        ros.Publish(topic, new StringMsg(inputField.text));
    }
    
    

    // Update is called once per frame
    void Update()
    {
        
    }
}
