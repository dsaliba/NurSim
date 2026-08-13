using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

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

        // If a geometry sequence was pending from a session restart, apply it now
        // that goals have been registered and are ready.
#if UNITY_EDITOR
        string packed = UnityEditor.SessionState.GetString("IONA_PendingGeometries", "");
        if (!string.IsNullOrEmpty(packed))
        {
            UnityEditor.SessionState.SetString("IONA_PendingGeometries", "");
            string[] labels = packed.Split(';');
            Debug.Log($"[TaskEnvironment] Applying deferred geometry order on scene start: " +
                      string.Join(" → ", labels));
            ApplyGeometrySequence(labels);
        }
#endif
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

    // ── Geometry sequence ordering (called by SimulationManager) ────────

    /// <summary>
    /// Reorders goals to match the API geometry_sequence.
    /// <paramref name="orderedLabels"/> should be goal names/labels in the
    /// order the participant will encounter them.  Goals not present in the
    /// array are moved to the end and disabled.
    /// </summary>
    public void ApplyGeometrySequence(string[] orderedLabels)
    {
        if (orderedLabels == null || orderedLabels.Length == 0) return;

        GameObject[] goals = getObjectListByKey("goals")
            .Where(g => g != null).ToArray();

        if (goals.Length == 0)
        {
            Debug.LogWarning($"[TaskEnvironment ({sceneName})] ApplyGeometrySequence: no goals found.");
            return;
        }

        // Build lookup (case-insensitive)
        var goalsByName = new Dictionary<string, GameObject>(
            System.StringComparer.OrdinalIgnoreCase);
        foreach (var g in goals)
            if (!goalsByName.ContainsKey(g.name))
                goalsByName[g.name] = g;

        var submissions = new List<HTTPDash.OrderedItemSubmission>();

        // Add goals in geometry order (enabled)
        foreach (var label in orderedLabels)
        {
            if (goalsByName.TryGetValue(label, out GameObject go))
                submissions.Add(new HTTPDash.OrderedItemSubmission { name = go.name, enabled = true });
            else
                Debug.LogWarning($"[TaskEnvironment ({sceneName})] No goal named '{label}' — skipping.");
        }

        // Append remaining goals (not in sequence) as disabled
        var sequencedNames = new HashSet<string>(
            submissions.Select(s => s.name), System.StringComparer.OrdinalIgnoreCase);
        foreach (var g in goals)
            if (!sequencedNames.Contains(g.name))
                submissions.Add(new HTTPDash.OrderedItemSubmission { name = g.name, enabled = false });

        ApplyGoalOrder(submissions);
    }

    // ── Goal ordering ────────────────────────────────────────────────────

    /// <summary>
    /// Applies a new goal order received from the dashboard or from
    /// <see cref="ApplyGeometrySequence"/>.
    ///
    /// Rules:
    ///   - Unchecked items are excluded from the sequence entirely and set inactive.
    ///   - Checked items form the new ordered sequence, all set inactive upfront.
    ///   - The sequential trial is restarted so OnGoalCompleted enables goals[0]
    ///     and wires completion events through its normal flow.
    ///
    /// IMPORTANT: The currently active goal is captured from the OLD objectMap
    /// array BEFORE objectMap is updated.
    /// </summary>
    public void ApplyGoalOrder(List<HTTPDash.OrderedItemSubmission> orderedItems)
    {
        if (orderedItems == null || orderedItems.Count == 0) return;

        GameObject[] oldGoals = getObjectListByKey("goals")
            .Where(g => g != null)
            .ToArray();

        var goalsMap = new Dictionary<string, GameObject>();
        foreach (var g in oldGoals)
            if (!goalsMap.ContainsKey(g.name))
                goalsMap[g.name] = g;

        SenquentialGoalTrial seqTrial = trial as SenquentialGoalTrial;
        GameObject previousActiveGoal = null;
        if (seqTrial != null
            && seqTrial.currentGoalIndex >= 0
            && seqTrial.currentGoalIndex < oldGoals.Length)
        {
            previousActiveGoal = oldGoals[seqTrial.currentGoalIndex];
        }

        // Disable unchecked items
        foreach (var item in orderedItems.Where(i => !i.enabled))
        {
            if (goalsMap.TryGetValue(item.name, out GameObject go))
                go.SetActive(false);
        }

        // Build new ordered array from checked items
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

        for (int i = 0; i < objectMap.Length; i++)
        {
            if (objectMap[i].key == "goals")
            {
                objectMap[i].objects = newOrder.ToArray();
                break;
            }
        }

        if (seqTrial != null)
            seqTrial.RestartSequence(previousActiveGoal);

        string summary = string.Join(", ", newOrder.Select((g, i) => $"{i + 1}:{g.name}"));
        Debug.Log($"TaskEnvironment ({sceneName}): Goal order applied — {summary}");
    }

    void Update()
    {
    }
}
