using UnityEngine;

/// <summary>
/// Bridges FittsLawTask into the TrialGoal system.
/// SequentialGoalTrial enables this GameObject, calls Activate(), and listens
/// for onComplete — all lifetime management is done by the scene manager.
/// </summary>
[RequireComponent(typeof(FittsLawTask))]
public class FittsLawTrialGoal : TrialGoal
{
    private FittsLawTask _fittsTask;

    private void Awake()
    {
        _fittsTask = GetComponent<FittsLawTask>();
    }

    public override void Activate()
    {
        contextMessage = _fittsTask.contextMessage;

        _fittsTask.onComplete -= HandleFittsComplete;
        _fittsTask.onComplete += HandleFittsComplete;

        _fittsTask.Activate();
    }

    private void HandleFittsComplete()
    {
        _fittsTask.onComplete -= HandleFittsComplete;
        Complete();
    }
}
