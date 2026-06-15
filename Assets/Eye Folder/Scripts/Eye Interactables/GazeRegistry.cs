using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;

/// <summary>
/// Static registry that owns the single "/unity/gazed_object" ROS topic.
/// All IKeyedGazeContributor components register here.
/// Call SetGazed(key) when a component is hit/selected, ClearGazed(key) when unselected.
/// Publishes the currently gazed key, or "none" when nothing is gazed.
/// </summary>
public static class GazeRegistry
{
    private const string Topic = "/unity/gazed_object";

    private static ROSConnection _ros;
    private static bool _registered = false;
    private static string _currentKey = null;

    private static void EnsureRegistered()
    {
        if (_registered) return;
        _ros = ROSConnection.GetOrCreateInstance();
        _ros.RegisterPublisher<StringMsg>(Topic);
        _registered = true;
    }

    /// <summary>
    /// Called by a component when the gaze ray is on it.
    /// Only the first caller wins until it clears.
    /// </summary>
    public static void SetGazed(string key)
    {
        EnsureRegistered();
        if (string.IsNullOrEmpty(key)) return;

        // Only update (and publish) if the active key is actually changing
        //if (_currentKey == key) return;

        _currentKey = key;
        _ros.Publish(Topic, new StringMsg(_currentKey));
    }

    /// <summary>
    /// Called by a component when the gaze ray leaves it.
    /// Only clears if this component is the one currently registered as gazed.
    /// </summary>
    public static void ClearGazed(string key)
    {
        EnsureRegistered();
        if (_currentKey != key) return;

        _currentKey = null;
        _ros.Publish(Topic, new StringMsg("none"));
    }
}
