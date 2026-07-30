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

    // Start is called before the first frame update
    void Start()
    {
        RegisterGoalsWithDash();
    }

    /// <summary>
    /// Reads the "goals" entry from objectMap and registers a drag-order card
    /// on the HTTPDash so the operator can reorder and enable/disable goals at
    /// runtime. The callback runs on the Unity main thread via
    /// UnityMainThreadDispatcher, so it is safe to call GameObject APIs.
    /// </summary>
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
    ///   - The sequential trial is then restarted so that it enables goals[0]
    ///     and wires completion events through its normal OnGoalCompleted flow.
    /// </summary>
    private void ApplyGoalOrder(List<HTTPDash.OrderedItemSubmission> orderedItems)
    {
        if (orderedItems == null || orderedItems.Count == 0) return;

        // Build a lookup from name → GameObject using the current goals list.
        GameObject[] currentGoals = getObjectListByKey("goals")
            .Where(g => g != null)
            .ToArray();

        var goalsMap = new Dictionary<string, GameObject>();
        foreach (var g in currentGoals)
            if (!goalsMap.ContainsKey(g.name))
                goalsMap[g.name] = g;

        // Disable and drop unchecked items — they are removed from the sequence.
        foreach (var item in orderedItems.Where(i => !i.enabled))
        {
            if (goalsMap.TryGetValue(item.name, out GameObject go))
                go.SetActive(false);
        }

        // Build the new ordered array from checked items only.
        // All are set inactive here; RestartSequence → OnGoalCompleted will
        // enable goals[0] through the trial's normal flow.
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

        // Persist the new ordered, filtered array into objectMap.
        for (int i = 0; i < objectMap.Length; i++)
        {
            if (objectMap[i].key == "goals")
            {
                objectMap[i].objects = newOrder.ToArray();
                break;
            }
        }

        // Restart the sequential trial from the new goals[0].
        // RestartSequence unsubscribes from the old active goal's onComplete so
        // its stale callback cannot corrupt the fresh sequence, then advances
        // to index 0 via OnGoalCompleted which enables and wires the first goal.
        SenquentialGoalTrial seqTrial = trial as SenquentialGoalTrial;
        if (seqTrial != null)
            seqTrial.RestartSequence();

        string summary = string.Join(", ", newOrder.Select((g, i) => $"{i + 1}:{g.name}"));
        Debug.Log($"TaskEnvironment ({sceneName}): Goal order applied — {summary}");
    }

    // Update is called once per frame
    void Update()
    {
    }
}
