using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;

[System.Serializable]
public class GameObjectFloatPair
{
    public GameObject gameObject;
    public float value;
}

public class RangeBranchManager : MonoBehaviour
{
    [Header("ROS Settings")]
    [Tooltip("Name of the ROS Float32 topic to subscribe to.")]
    public string rosTopicName = "";

    [Header("GameObject-Value Pairs")]
    [Tooltip("List of GameObjects with their associated float values.")]
    public List<GameObjectFloatPair> gameObjectPairs = new List<GameObjectFloatPair>();

    ROSConnection ros;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.Subscribe<Float32Msg>(rosTopicName, OnFloatReceived);
    }

    void OnFloatReceived(Float32Msg msg)
    {
        float receivedValue = msg.data;

        // Find the GameObject with the maximum value <= receivedValue
        GameObjectFloatPair selectedPair = null;
        float maxValidValue = float.MinValue;

        foreach (var pair in gameObjectPairs)
        {
            if (pair.value <= receivedValue && pair.value > maxValidValue)
            {
                maxValidValue = pair.value;
                selectedPair = pair;
            }
        }

        // Enable only the selected GameObject, disable the rest
        foreach (var pair in gameObjectPairs)
        {
            bool shouldEnable = (pair == selectedPair);
            if (pair.gameObject != null)
                pair.gameObject.SetActive(shouldEnable);
        }
    }
}
