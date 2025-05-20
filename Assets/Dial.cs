using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Dial : MonoBehaviour
{
    public string valueName;
    public float minimumRation = -90;
    public float maximumRation = 90;
    public bool flipRotation = false;
    private bool connected;

    private Trial.LiveNumber value;
    private RectTransform rectTransform;
    private int searchAttemptsLeft = 100;
    
    // Start is called before the first frame update
    void Start()
    {
        rectTransform = GetComponent<RectTransform>();
    }

    public bool ConnectToTask()
    {
        if (TaskEnvironment.instances.Count > TaskEnvironment.currentIndex)
        {
            Trial trial= TaskEnvironment.instances[TaskEnvironment.currentIndex].trial;
            if (trial != null)
            {
                value = Array.Find(trial.liveNumbers, number => number.name.Equals(valueName));
                if (value == null)
                {
                    
                    searchAttemptsLeft--;
                    if (searchAttemptsLeft == 0)
                    {
                        Debug.LogWarning("Dial failed to find live value named " + valueName + " in time.");
                    }
                    else
                    {
                        return false;
                    }

                }else if(!value.clamped)
                {   
                    Debug.LogWarning("Dial assigned to unclamped value " + valueName);
                }

                return true;
            }
            else
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        return false;
    }

    // Update is called once per frame
    void Update()
    {
        if (!connected) connected = ConnectToTask();
        
        if (value != null && value.clamped)
        {
            float normalized = (float)(2 * (value.value - value.min) / (value.max - value.min) - 1) * (flipRotation?-1:1);
            
            rectTransform.rotation = Quaternion.Euler(rectTransform.rotation.eulerAngles.x, rectTransform.rotation.eulerAngles.y, (normalized+1)/2f*(maximumRation-minimumRation) + minimumRation);
        }
    }
}
