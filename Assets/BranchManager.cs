using System.Collections;
using System.Collections.Generic;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

public class BranchManager : MonoBehaviour
{
    [System.Serializable]
    public class ObjectKeyPair
    {
        public string key;
        public GameObject gameObject;
    }

    public string topicName;
    public ObjectKeyPair[] keyPairs;
    
    private ROSConnection ros;
    // Start is called before the first frame update
    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.Subscribe<StringMsg>(topicName, msg =>
        {
            for (int i = 0; i < keyPairs.Length; i++)
            {
                if (keyPairs[i].key.Equals(msg.data))
                {
                    keyPairs[i].gameObject.SetActive(true);
                }
                else
                {
                    keyPairs[i].gameObject.SetActive(false);
                }
            }
        });
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
