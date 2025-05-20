using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Object = System.Object;

public class TaskEnvironment : MonoBehaviour
{
    
    

    [System.Serializable]
    public class ObjectListMapItem
    {
        public string key;
        public GameObject[] objects;

        public ObjectListMapItem(string key)
        {
            this.key = key;
        }
    }
    
    


    public Trial trial;

    // Default object groups, NOTE: more specific object groups should be added via the inspector
    [SerializeField] public ObjectListMapItem[] objectMap = new []{
        new ObjectListMapItem("goals"), 
        new ObjectListMapItem("robots"), 
        new ObjectListMapItem("cameras")
    };
    
    
    
    public static List<TaskEnvironment> instances = new List<TaskEnvironment>();
    public static int currentIndex = 0;


    public String sceneName;
    public void Awake()
    {
        instances.Add(this);
        sceneName = gameObject.scene.name;
        Debug.Log("Added " + sceneName);
    }

    public GameObject[] getObjectListByKey(string key)
    {
        for (int i = 0; i < objectMap.Length; i++)
        {
            if (objectMap[i].key.Equals(key))
            {
                return objectMap[i].objects;
            }
        }

        return new GameObject[] { };
    }

    
    

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
