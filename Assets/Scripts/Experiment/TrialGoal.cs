using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public abstract class TrialGoal : MonoBehaviour
{
    public bool completed = false;
    public string contextMessage;
    public event Action onComplete;

    public void Complete()
    {
        completed = true;
        onComplete?.Invoke();
    }

    public abstract void Activate();
}
