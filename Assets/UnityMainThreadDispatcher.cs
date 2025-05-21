using System;
using System.Collections.Generic;
using UnityEngine;

public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static readonly Queue<string> paramQueue = new Queue<string>();
    private static readonly Queue<Action<string>> executionQueue = new Queue<Action<string>>();

    public static void Enqueue(Action<string> action, string param)
    {
        lock (executionQueue)
        {
            lock (paramQueue)
            {
                executionQueue.Enqueue(action);
                paramQueue.Enqueue(param);
            }
            
        }
    }

    void Update()
    {
        while (executionQueue.Count > 0)
        {
            Action<string> action = null;
            string param = "";
            lock (executionQueue)
            {

                lock (paramQueue)
                {
                    if (executionQueue.Count > 0)
                        action = executionQueue.Dequeue();
                    param = paramQueue.Count > 0 ? paramQueue.Dequeue() : "";
                }
                
            }
            action?.Invoke(param);
        }
    }
}
