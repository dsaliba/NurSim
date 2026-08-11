using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    [SerializeField] public ObjectListMapItem[] objectMap = new[]{
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

    void Start()
    {
        RegisterGoalsWithDash();
    }

    private void RegisterGoalsWithDash()
    {
        if (HTTPDash.Instance == null)
        {
            Debug.LogWarning($"TaskEnvironment ({sceneName}): HTTPDash instance not found — skipping goal registration.");
            return;
        }

        GameObject[] goals = getObjectListByKey("goals");
        if (goals == null || goals.Length == 0)
        {
            Debug.Log($"TaskEnvironment ({sceneName}): No goals found in objectMap.");
            return;
        }

        goals = goals.Where(g => g != null).ToArray();
        if (goals.Length == 0) return;

        string[] goalNames = goals.Select(g => g.name).ToArray();
        string cardTitle = string.IsNullOrEmpty(sceneName) ? "Goal Order" : $"Goal Order ({sceneName})";

        HTTPDash.Instance.RegisterDragOrder(
            cardTitle,
            "Apply Order",
            goalNames,
            (orderedItems) => ApplyGoalOrder(orderedItems)
        );

        Debug.Log($"TaskEnvironment ({sceneName}): Registered {goals.Length} goals with HTTPDash.");
    }

    /// <summary>
    /// Applies a new goal order received from the dashboard.
    ///
    /// Rules:
    ///   - Unchecked items are excluded from the sequence entirely and set inactive.
    ///   - Checked items form the new ordered sequence, all set inactive upfront.
    ///   - The sequential trial is restarted so OnGoalCompleted enables goals[0]
    ///     and wires completion events through its normal flow.
    ///
    /// IMPORTANT: The currently active goal is captured from the OLD objectMap
    /// array BEFORE objectMap is updated. This reference is passed into
    /// RestartSequence so it can unsubscribe the correct onComplete callback.
    /// If we looked it up inside RestartSequence, objectMap would already be
    /// updated and goals[currentGoalIndex] would point to the wrong object,
    /// leaving the real active goal's callback live and corrupting the sequence.
    /// </summary>
    private void ApplyGoalOrder(List<HTTPDash.OrderedItemSubmission> orderedItems)
    {
        if (orderedItems == null || orderedItems.Count == 0) return;

        // Snapshot the current (old) goals array BEFORE any changes.
        // This is used both to build the lookup map and to find the active goal
        // by its old index before objectMap is overwritten.
        GameObject[] oldGoals = getObjectListByKey("goals")
            .Where(g => g != null)
            .ToArray();

        var goalsMap = new Dictionary<string, GameObject>();
        foreach (var g in oldGoals)
            if (!goalsMap.ContainsKey(g.name))
                goalsMap[g.name] = g;

        // Capture the currently active goal by reference from the OLD array now,
        // before objectMap is updated. After the update, currentGoalIndex no
        // longer maps to the same object in the new array.
        SenquentialGoalTrial seqTrial = trial as SenquentialGoalTrial;
        GameObject previousActiveGoal = null;
        if (seqTrial != null
            && seqTrial.currentGoalIndex >= 0
            && seqTrial.currentGoalIndex < oldGoals.Length)
        {
            previousActiveGoal = oldGoals[seqTrial.currentGoalIndex];
        }

        // Disable and drop unchecked items — excluded from the sequence.
        foreach (var item in orderedItems.Where(i => !i.enabled))
        {
            if (goalsMap.TryGetValue(item.name, out GameObject go))
                go.SetActive(false);
        }

        // Build the new ordered array from checked items only.
        // All are set inactive; RestartSequence → OnGoalCompleted enables goals[0].
        var newOrder = new List<GameObject>();
        foreach (var item in orderedItems.Where(i => i.enabled))
        {
            if (!goalsMap.TryGetValue(item.name, out GameObject go))
            {
                Debug.LogWarning($"TaskEnvironment ({sceneName}): Goal '{item.name}' not found — skipping.");
                continue;
            }

            go.SetActive(false);
            go.transform.SetSiblingIndex(newOrder.Count);
            newOrder.Add(go);
        }

        // Update objectMap with the new ordered, filtered array.
        for (int i = 0; i < objectMap.Length; i++)
        {
            if (objectMap[i].key == "goals")
            {
                objectMap[i].objects = newOrder.ToArray();
                break;
            }
        }

        // Restart the trial, passing the previously active goal so RestartSequence
        // can unsubscribe from its onComplete without relying on objectMap (which
        // now contains the new order and would return the wrong object at the
        // same index).
        if (seqTrial != null)
            seqTrial.RestartSequence(previousActiveGoal);

        string summary = string.Join(", ", newOrder.Select((g, i) => $"{i + 1}:{g.name}"));
        Debug.Log($"TaskEnvironment ({sceneName}): Goal order applied — {summary}");
    }

    void Update()
    {
    }
}
