using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

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




/// <summary>
/// Draws a "Complete Goal (Test)" button in the Inspector for every TrialGoal
/// subclass. Clicking it calls Complete(), which sets completed = true and
/// invokes onComplete — identical to a real completion so all downstream
/// listeners (SenquentialGoalTrial.OnGoalCompleted, etc.) fire normally.
///
/// The button is disabled outside of Play Mode (events have no subscribers)
/// and after the goal has already completed.
/// </summary>
[CustomEditor(typeof(TrialGoal), editorForChildClasses: true)]
public class TrialGoalEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Testing", EditorStyles.boldLabel);

        TrialGoal goal = (TrialGoal)target;
        bool canComplete = Application.isPlaying && !goal.completed;

        using (new EditorGUI.DisabledScope(!canComplete))
        {
            if (GUILayout.Button("Complete Goal (Test)", GUILayout.Height(28)))
                goal.Complete();
        }

        if (!Application.isPlaying)
            EditorGUILayout.HelpBox("Enter Play Mode to test goal completion.", MessageType.Info);
        else if (goal.completed)
            EditorGUILayout.HelpBox("Goal already completed.", MessageType.Warning);
    }
}

