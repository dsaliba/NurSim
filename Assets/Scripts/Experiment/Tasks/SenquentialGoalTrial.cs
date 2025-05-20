using System;
using System.Collections;
using System.Collections.Generic;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

public class SenquentialGoalTrial : Trial
{
    public int currentGoalIndex = -1;

    public new void Start()
    {
        base.Start();
        ros.RegisterPublisher<Float64Msg>("trial/distance_to_goal");
        ros.RegisterPublisher<Int32Msg>("trial/goal_index", latch:true);
        ros.RegisterPublisher<StringMsg>("trial/progress_description", latch:true);
        ros.RegisterPublisher<StringMsg>("trial/current_hint", latch:true);
        OnGoalCompleted();
        StartTrial();
        
    }

    public void OnGoalCompleted()
    {
        GameObject[] goals = base.environment.getObjectListByKey("goals");
        GoalStepping:
        currentGoalIndex++;
        if (currentGoalIndex >= goals.Length)
        {
            ros.Publish("trial/goal_index", new Int32Msg(currentGoalIndex));
            ros.Publish("trial/progress_description", new StringMsg((currentGoalIndex) + "/" +goals.Length));
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
            ros.Publish("trial/current_hint", new StringMsg(nextGoal.contextMessage));
            ros.Publish("trial/progress_description", new StringMsg((currentGoalIndex) + "/" +goals.Length));
            if (currentGoalIndex > 0)
            {
                ros.Publish("trial/goal_index", new Int32Msg(currentGoalIndex));
                
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
        ros.Publish("trial/distance_to_goal", new Float64Msg(distance));
    }

    
    public new void Update()
    {
        base.Update();
        UpdateDistanceToNextGoal();
    }
}
