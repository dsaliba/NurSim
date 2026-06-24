using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;


/// <summary>
/// Bridges the HTTPDash "Recording" tab and the Python recording_manager_node
/// over ROS. Other scripts use this to register experiment metadata and to
/// trigger recordings without knowing anything about the dashboard or ROS.
/// </summary>
public class RecordingManager : MonoBehaviour
{
    public static RecordingManager Instance { get; private set; }

    [Header("ROS Topics")]
    public string cmdTopic = "/recording/cmd";
    public string topicsTopic = "/recording/topics";
    public string statusTopic = "/recording/status";

    private string experimentName = "experiment";

    // Ordered: filename composition (and the dashboard's dropdown order) follows
    // registration order, so re-registering the same key updates in place.
    private List<(string key, string label, string[] options)> conditionDefs =
        new List<(string key, string label, string[] options)>();

    private Dictionary<string, string> conditionOverrides = new Dictionary<string, string>();

    private HTTPDash.RecordingCard dashCard;
    private bool isRecording = false;


    private ROSConnection ros;


    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Start()
    {

        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<StringMsg>(cmdTopic);
        ros.Subscribe<StringMsg>(topicsTopic, OnTopicsReceived);
        ros.Subscribe<StringMsg>(statusTopic, OnStatusReceived);

        EnsureDashCard();
    }

    public bool IsRecording => isRecording;

    // ── Public API for other scripts ────────────────────────────────────

    public void SetExperimentName(string name) => experimentName = name;

    public void RegisterCondition(string key, string label, string[] options)
    {
        conditionDefs.RemoveAll(c => c.key == key);
        conditionDefs.Add((key, label, options));
        RebuildDashConditions();
    }

    public void RegisterCondition(string key, string[] options) => RegisterCondition(key, key, options);

    /// <summary>
    /// Pin a specific value for a condition for script-driven recordings
    /// (i.e. when StartRecording() is called directly, not via the dashboard).
    /// Has no effect on dashboard-driven starts, which always use whatever
    /// the dropdown is currently set to.
    /// </summary>
    public void SetConditionValue(string key, string value) => conditionOverrides[key] = value;

    /// <summary>
    /// Starts a recording using the currently registered conditions/overrides.
    /// Participant and topic selection are NOT specified here — the Python
    /// node falls back to its cached values (normally whatever was last
    /// entered on the dashboard) for anything not provided.
    /// </summary>
    public void StartRecording() => SendCommand("start", null, null, null);

    public void StopRecording() => SendCommand("stop", null, null, null);

    // ── Dashboard wiring ────────────────────────────────────────────────

    private void EnsureDashCard()
    {
        if (dashCard != null || HTTPDash.Instance == null) return;
        dashCard = HTTPDash.Instance.RegisterRecordingCard(OnDashSubmit);
        RebuildDashConditions();
    }

    private void RebuildDashConditions()
    {
        if (dashCard == null)
        {
            EnsureDashCard();
            if (dashCard == null) return; // HTTPDash not up yet; next caller will retry
        }

        dashCard.conditions = conditionDefs.Select(c => new HTTPDash.RecordingCard.ConditionDef
        {
            key = c.key,
            label = c.label,
            options = c.options.ToList()
        }).ToList();

        HTTPDash.Instance.NotifyCardsChanged();
    }

    private void OnDashSubmit(HTTPDash.RecordingSubmission sub)
    {
        if (sub.command == "start")
            SendCommand("start", sub.participant, sub.conditions, sub.topics);
        else if (sub.command == "stop")
            SendCommand("stop", null, null, null);
    }

    // ── ROS command building ────────────────────────────────────────────

    private void SendCommand(string action, string participant,
        List<HTTPDash.ConditionValuePair> conditionsFromCaller, List<string> topics)
    {
        List<HTTPDash.ConditionValuePair> conditions = conditionsFromCaller;

        if (action == "start" && conditions == null)
        {
            // Script-driven start: build from registered defs + any pinned overrides.
            conditions = conditionDefs.Select(c => new HTTPDash.ConditionValuePair
            {
                key = c.key,
                value = conditionOverrides.TryGetValue(c.key, out var v) ? v
                      : (c.options.Length > 0 ? c.options[0] : "")
            }).ToList();
        }

        string json;
        if (action == "start")
        {
            string condJson = conditions != null
                ? string.Join(",", conditions.Select(c => $"{{\"key\":\"{Esc(c.key)}\",\"value\":\"{Esc(c.value)}\"}}"))
                : "";
            string topicsJson = topics != null
                ? string.Join(",", topics.Select(t => $"\"{Esc(t)}\""))
                : "";

            json = $"{{\"action\":\"start\",\"experiment\":\"{Esc(experimentName)}\",\"participant\":\"{Esc(participant)}\",\"conditions\":[{condJson}],\"topics\":[{topicsJson}]}}";
        }
        else
        {
            json = "{\"action\":\"stop\"}";
        }


        ros.Publish(cmdTopic, new StringMsg { data = json });

    }

    private static string Esc(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");


    private void OnTopicsReceived(StringMsg msg)
    {
        HTTPDash.Instance?.PublishChannel("recording-topics", msg.data);
    }

    private void OnStatusReceived(StringMsg msg)
    {
        bool recording = msg.data.Contains("\"recording\":true");
        bool hasError = msg.data.Contains("\"error\"");
        isRecording = recording;

        string title = hasError ? "Recording Error" : (recording ? "Recording Started" : "Recording Stopped");
        string color = hasError ? "red" : (recording ? "green" : "blue");

        HTTPDash.Instance?.SendNotification(title, msg.data, color);
    }
}
