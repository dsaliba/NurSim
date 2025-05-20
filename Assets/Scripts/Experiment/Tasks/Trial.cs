using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public abstract class Trial : MonoBehaviour
{
    private double startTime = -1;
    private double stopTime = -1;
    public TaskEnvironment environment;
    
    [System.Serializable]
    public class TrialEvent
    {
        public string topicName;
        public int id;
        public string data;
        public int statusCode;

        public TrialEvent(string topicName, int id, string data, int statusCode)
        {
            this.topicName = topicName;
            this.id = id;
            this.data = data;
            this.statusCode = statusCode;
        }
    }

    [System.Serializable]
    public class TrialTopic
    {
        public string topicName;
        public TrialEvent lastEvent;
        public event Action<TrialEvent> publisher;
        

        public TrialTopic(string topicName)
        {
            this.topicName = topicName;
        }

        public void Publish(TrialEvent e)
        {
            lastEvent = e;
            publisher?.Invoke(e);
        }
    }

    [SerializeField] private List<TrialTopic> topics;

    public void RegisterSubscriber(string topicName, Action<TrialEvent> handler)
    {
        TrialTopic topic = topics.Find(t => t.topicName == topicName);
        if (topic == null)
        {
            TrialTopic newTopic = new TrialTopic(topicName);
            newTopic.publisher += handler;
            topics.Add(newTopic);
        }
        else
        {
            topic.publisher += handler;
        }
        
    }

    public TrialEvent GetLastEvent(string topicName)
    {
        TrialTopic topic = topics.Find(t => t.topicName == topicName);
        return topic?.lastEvent;
    }

    public void Publish(string topicName, String data, int statusCode)
    {
        TrialTopic topic = topics.Find(t => t.topicName == topicName);
        if (topic == null)
        {
            topic = new TrialTopic(topicName);
            topics.Add(topic);
        }
        int id = topic.lastEvent==null?0:topic.lastEvent.id+1;
        TrialEvent newEvent = new TrialEvent(topic.topicName, id, data, statusCode);
        topic.Publish(newEvent);
    }
    
    [System.Serializable]
    public class LiveNumber 
    {
        public double value;
        public double min;
        public double max;
        public bool clamped;
        public string name;

        public LiveNumber(string name, double value, double min, double max)
        {
            this.name = name;
            this.value = value;
            this.min = min;
            this.max = max;
            clamped = true;
        }

        public LiveNumber(string name, double value)
        {
            this.name = name;
            this.value = value;
            this.clamped = false;
        }
    }

    [SerializeField] public LiveNumber[] liveNumbers = new[]
    {
        new LiveNumber("task_time", 0)
    };

    public void AddLiveNumber(LiveNumber newNumber)
    {
        LiveNumber[] newArray = new LiveNumber[liveNumbers.Length + 1];
        for (int i = 0; i < liveNumbers.Length; i++)
        {
            newArray[i] = liveNumbers[i];
        }
        newArray[newArray.Length-1] = newNumber;
        liveNumbers = newArray;
    }
   
    
    public void UpdateElapsedTime()
    {
        LiveNumber taskTime = Array.Find(liveNumbers, l => l.name.Equals("task_time"));
        taskTime.value = startTime < 0 ? 0 : stopTime < 0 ? Time.timeAsDouble - startTime : stopTime - startTime;
    }

    public void StartTrial()
    {
        startTime = Time.timeAsDouble;
    }

    public void StopTrial()
    {
        stopTime = Time.timeAsDouble;
    }
    
    
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    public void Update()
    {
        UpdateElapsedTime();
    }
}
