using System;
using System.Collections;
using System.Collections.Generic;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

public class SenquentialGoalTrial : Trial
{
    public int currentGoalIndex = -1;

    // Suppresses the "Goal Reached" notification for one OnGoalCompleted call.
    // Used by RestartSequence so resetting to index -1 doesn't emit a
    // spurious "Robot reached goal #-1" notification.
    private bool suppressNextNotification = false;

    public new void Start()
    {
        base.Start();
        ros.RegisterPublisher<Float64Msg>("trial/distance_to_goal");
        ros.RegisterPublisher<Int32Msg>("trial/goal_index", latch: true);
        ros.RegisterPublisher<StringMsg>("trial/progress_description", latch: true);
        ros.RegisterPublisher<StringMsg>("trial/current_hint", latch: true);

        // Disable all goals up front, then advance to the first one.
        GameObject[] goals = base.environment.getObjectListByKey("goals");
        foreach (var g in goals)
            if (g != null) g.SetActive(false);

        OnGoalCompleted();
        StartTrial();
    }

    /// <summary>
    /// Resets the sequential goal sequence to start from goals[0] of the
    /// current (possibly reordered) goals list.
    ///
    /// <paramref name="previousActiveGoal"/> must be the GameObject that was
    /// active BEFORE TaskEnvironment updated objectMap. It is passed in
    /// explicitly because by the time this method runs, objectMap contains the
    /// new order, so looking up goals[currentGoalIndex] would return the wrong
    /// object and the real active goal's onComplete callback would be left live,
    /// corrupting the restarted sequence when that goal eventually completes.
    /// </summary>
    public void RestartSequence(GameObject previousActiveGoal)
    {
        // Unsubscribe from the goal that was active in the OLD sequence.
        if (previousActiveGoal != null)
        {
            TrialGoal current = previousActiveGoal.GetComponent<TrialGoal>();
            if (current != null)
                current.onComplete -= OnGoalCompleted;

            previousActiveGoal.SetActive(false);
        }

        suppressNextNotification = true;
        currentGoalIndex = -1;
        OnGoalCompleted();
    }

    public void OnGoalCompleted()
    {
        if (!suppressNextNotification)
        {
            HTTPDash.Instance.SendNotification(
                "Goal Reached", "Robot reached goal #" + currentGoalIndex, "green");
        }
        suppressNextNotification = false;

        GameObject[] goals = base.environment.getObjectListByKey("goals");

        // Disable the goal that just completed.
        if (currentGoalIndex >= 0 && currentGoalIndex < goals.Length)
        {
            GameObject prev = goals[currentGoalIndex];
            if (prev != null) prev.SetActive(false);
        }

        GoalStepping:
        currentGoalIndex++;

        if (currentGoalIndex >= goals.Length)
        {
            ros.Publish("trial/goal_index",
                new Int32Msg(currentGoalIndex));
            ros.Publish("trial/progress_description",
                new StringMsg(currentGoalIndex + "/" + goals.Length));
            StopTrial();
            return;
        }

        // Skip nulls or objects missing a TrialGoal component.
        TrialGoal nextGoal = goals[currentGoalIndex]?.GetComponent<TrialGoal>();
        if (nextGoal == null)
        {
            Debug.LogWarning("Object at index " + currentGoalIndex +
                " of goals list does not have component of type TrialGoal, " +
                "this may corrupt goal indexing for sequential trials.");
            goto GoalStepping;
        }

        // Enable the GameObject, wire completion, then activate.
        // Guard with -= before += so that reordering a goal back into the
        // sequence never accumulates duplicate OnGoalCompleted subscriptions,
        // which would cause the callback to fire multiple times on completion.
        goals[currentGoalIndex].SetActive(true);
        nextGoal.onComplete -= OnGoalCompleted;
        nextGoal.onComplete += OnGoalCompleted;
        nextGoal.Activate();

        ros.Publish("trial/current_hint",
            new StringMsg(nextGoal.contextMessage));
        ros.Publish("trial/progress_description",
            new StringMsg(currentGoalIndex + "/" + goals.Length));

        if (currentGoalIndex > 0)
            ros.Publish("trial/goal_index", new Int32Msg(currentGoalIndex));
    }

    public void UpdateDistanceToNextGoal()
    {
        if (currentGoalIndex < 0 || currentGoalIndex >= environment.getObjectListByKey("goals").Length)
        {
            ros.Publish("trial/distance_to_goal", new Float64Msg(0));
            return;
        }

        GameObject robot = environment.getObjectListByKey("robots")[0];
        GameObject goal  = environment.getObjectListByKey("goals")[currentGoalIndex];
        double distance  = Vector3.Distance(
            robot.transform.position, goal.transform.position);
        ros.Publish("trial/distance_to_goal", new Float64Msg(distance));
    }

    public new void Update()
    {
        base.Update();
        UpdateDistanceToNextGoal();
    }
}
