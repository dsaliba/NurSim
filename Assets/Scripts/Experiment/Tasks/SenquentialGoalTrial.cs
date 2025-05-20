using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SenquentialGoalTrial : Trial
{
    public int currentGoalIndex = -1;

    public void Start()
    {
        OnGoalCompleted();
        StartTrial();
        AddLiveNumber(new LiveNumber("distance_to_goal", 0));
    }

    public void OnGoalCompleted()
    {
        GameObject[] goals = base.environment.getObjectListByKey("goals");
        GoalStepping:
        currentGoalIndex++;
        if (currentGoalIndex >= goals.Length)
        {
            base.Publish("goal", ""+(currentGoalIndex), 1);
            base.Publish("goal/progress", (currentGoalIndex) + "/" +goals.Length, 0);
            StopTrial();
        }
        else
        {
            TrialGoal nextGoal = goals[currentGoalIndex].gameObject.GetComponent<TrialGoal>();
            if (nextGoal == null)
            {
                Debug.LogWarning("Object at index " + currentGoalIndex + " of goals list does not have component of type TrialGoal, this may corrupt goal indexing for sequential trials.");
                goto GoalStepping;
            }
            nextGoal.onComplete += OnGoalCompleted;
            nextGoal.Activate();
            base.Publish("goal/hint", nextGoal.contextMessage, 0);
            base.Publish("goal/progress", (currentGoalIndex) + "/" +goals.Length, 0);
            if (currentGoalIndex > 0)
            {
                base.Publish("goal", ""+(currentGoalIndex), 0);
                
            }
        }
        
        
    }

    public void UpdateDistanceToNextGoal()
    {
        GameObject robot = environment.getObjectListByKey("robots")[
            0];
        GameObject goal =
            environment.getObjectListByKey("goals")[currentGoalIndex];
        double distance = Vector3.Distance(robot.transform.position, goal.transform.position);
            LiveNumber liveNumber = Array.Find(liveNumbers, number => number.name.Equals("distance_to_goal"));
            if (liveNumber != null) liveNumber.value = distance;

    }

    public void Update()
    {
        base.Update();
        UpdateDistanceToNextGoal();
    }
}
