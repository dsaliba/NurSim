using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using RosMessageTypes.KortexDriver;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

/// <summary>
///     Drives the virtual (in-Unity) Kortex arm from ROS messages, mirroring
///     the topics used to control the physical robot.
///
///     Also supports saving/loading named joint-angle presets:
///       - Presets are saved to a JSON file in persistentDataPath so they
///         survive editor/build restarts.
///       - At runtime, publishing a preset name (std_msgs/String) to
///         presetLoadTopic will move the arm to that preset.
///       - Presets are registered as a dropdown on the HTTPDash for manual
///         loading during testing.
///       - An optional startup preset can be specified in the inspector.
///
///     Preset loading is independent of the ROS-side teleoperation mapping
///     graph. To avoid the arm jittering and snapping back to the live
///     teleop pose immediately after a preset is applied, incoming
///     relaxed_ik joint-angle solutions are ignored for the duration of the
///     preset move (presetOverrideActive). This is purely a Unity-side
///     guard — no ROS nodes need to change.
///
///     presetOverrideActive is cleared by whichever condition fires first:
///       1. The settle coroutine detects that all joints are within
///          settleThresholdDeg of their targets (polled every 100 ms).
///       2. settleTimeoutSeconds elapses (hard safety fallback so the flag
///          can never get permanently stuck).
///       3. The SetJointAnglesWithCallback callback fires AND a short grace
///          period (callbackGracePeriodSeconds) has elapsed, giving the arm
///          time to coast to the final position before ROS teleop resumes.
/// </summary>
public class VirtualKortexDriver : MonoBehaviour
{
    public string robotName;

    [SerializeField] private ArticulationArmController armController;

    [Header("ROS")]
    [Tooltip("Seconds to wait after startup before subscribing to ROS topics.")]
    [SerializeField] private float subscribeDelaySeconds = 10f;
    [Tooltip("Topic to subscribe to for loading a saved preset by name (std_msgs/String).")]
    [SerializeField] private string presetLoadTopic = "/preset_load";
    [Tooltip("ROS service to call after a preset move so relaxed_ik re-seeds from the current pose (prevents snap-back on control hand-off).")]
    [SerializeField] private string relaxedIkResetService = "/right_arm/relaxed_ik/reset";

    [Header("Presets")]
    [Tooltip("Name of a preset to move to automatically on startup. Leave blank to skip.")]
    [SerializeField] private string startupPresetName;
    [Tooltip("Filename (within Application.persistentDataPath) used to store presets.")]
    [SerializeField] private string presetFileName = "joint_presets.json";

    [Header("Preset Settle Detection")]
    [Tooltip(
        "All joints must be within this many degrees of the preset target " +
        "before ROS teleop resumes. Tune to match your arm's motion controller."
    )]
    [SerializeField] private float settleThresholdDeg = 1.5f;

    [Tooltip(
        "Hard timeout (seconds) after which presetOverrideActive is cleared " +
        "regardless of joint proximity. Prevents the flag ever getting stuck."
    )]
    [SerializeField] private float settleTimeoutSeconds = 8f;

    [Tooltip(
        "Extra seconds to wait after SetJointAnglesWithCallback fires before " +
        "treating the arm as settled. Allows coasting to complete."
    )]
    [SerializeField] private float callbackGracePeriodSeconds = 0.5f;

    private ROSConnection ros;
    private bool hasSubscribed = false;
    private float startTime;

    private Dictionary<string, float[]> presets = new Dictionary<string, float[]>();
    private string PresetFilePath => Path.Combine(Application.persistentDataPath, presetFileName);

    // True while a preset move is in flight. While true, incoming relaxed_ik
    // joint-angle solutions from ROS are dropped so they can't race with /
    // immediately overwrite the preset's joint targets.
    private bool presetOverrideActive = false;

    // Coroutine handle so we can cancel a previous settle if a new preset is
    // loaded before the first one has finished settling.
    private Coroutine settleCoroutine = null;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        startTime = Time.time;
        ros.RegisterRosService<EmptyRequest, EmptyResponse>(relaxedIkResetService);

        LoadPresetsFromDisk();
        RegisterPresetDropdown();

        if (!string.IsNullOrEmpty(startupPresetName))
        {
            // Defer to a coroutine so ArticulationBody components complete
            // their first physics initialisation before we drive them.
            // Calling SetJointAnglesWithCallback directly in Start() (before
            // any FixedUpdate) races with the physics engine's own reset and
            // can land the arm at the wrong pose.
            StartCoroutine(ApplyStartupPresetDelayed());
        }
    }

    void Update()
    {
        if (!hasSubscribed && Time.time - startTime > subscribeDelaySeconds)
        {
            SubscribeToRosTopics();
            hasSubscribed = true;
        }
    }

    private void SubscribeToRosTopics()
    {
        ros.Subscribe<JointAnglesMsg>(
            $"/{robotName}/relaxed_ik/joint_angle_solutions",
            OnJointAngleSolution
        );

        ros.Subscribe<StringMsg>(presetLoadTopic, OnPresetLoadMessage);
    }

    private void OnJointAngleSolution(JointAnglesMsg msg)
    {
        if (presetOverrideActive)
        {
            // A preset move is in progress — ignore ROS teleop input so it
            // can't yank the arm back before the preset is fully settled.
            return;
        }

        float[] targets = new float[msg.joint_angles.Length];
        for (int i = 0; i < msg.joint_angles.Length; i++)
        {
            targets[i] = msg.joint_angles[i].value;
        }
        armController.SetJointAngles(targets);
    }

    private void OnPresetLoadMessage(StringMsg msg)
    {
        LoadPreset(msg.data);
    }

    // ----- Preset save/load -----

    /// <summary>
    ///     Saves the arm's current joint targets under the given name and
    ///     persists all presets to disk.
    /// </summary>
    public void SaveCurrentPositionAsPreset(string presetName)
    {
        if (string.IsNullOrEmpty(presetName))
        {
            Debug.LogWarning("[VirtualKortexDriver] Cannot save preset with an empty name.");
            return;
        }

        float[] currentAngles = armController.GetJointAngles();
        presets[presetName] = currentAngles;
        SavePresetsToDisk();
        RegisterPresetDropdown();

        Debug.Log($"[VirtualKortexDriver] Saved preset '{presetName}' ({currentAngles.Length} joints).");
    }

    /// <summary>
    ///     Defers startup preset application until ArticulationBody components
    ///     have completed their first physics initialisation pass. Calling
    ///     SetJointAnglesWithCallback during Start() (before any FixedUpdate)
    ///     races with the physics engine's own reset and can land the arm at
    ///     the wrong pose. Two fixed-update yields are enough to clear that
    ///     window reliably.
    /// </summary>
    private IEnumerator ApplyStartupPresetDelayed()
    {
        // Let the physics engine complete at least two FixedUpdate cycles so
        // ArticulationBody drive targets are no longer overwritten by the
        // engine's initialisation reset.
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();

        if (presets.ContainsKey(startupPresetName))
        {
            LoadPreset(startupPresetName);
        }
        else
        {
            Debug.LogWarning(
                $"[VirtualKortexDriver] Startup preset '{startupPresetName}' not found in {PresetFilePath}."
            );
        }
    }

    /// <summary>
    ///     Moves the arm to a previously saved preset by name. No-ops with a
    ///     warning if the name isn't found.
    ///
    ///     Blocks ROS-driven teleop joint updates (presetOverrideActive) until
    ///     whichever condition fires first:
    ///       • All joints settle within settleThresholdDeg of the target.
    ///       • settleTimeoutSeconds elapses (hard safety fallback).
    ///       • The motion callback fires + callbackGracePeriodSeconds elapses.
    /// </summary>
    public void LoadPreset(string presetName)
    {
        if (!presets.TryGetValue(presetName, out float[] angles))
        {
            Debug.LogWarning($"[VirtualKortexDriver] No preset named '{presetName}' found.");
            return;
        }

        // Cancel any in-flight settle from a previous preset load.
        if (settleCoroutine != null)
        {
            StopCoroutine(settleCoroutine);
            settleCoroutine = null;
        }

        presetOverrideActive = true;

        // Fast path: callback fires (plus grace period) → clear flag.
        // Safety net: coroutine also polls proximity and enforces a hard timeout.
        bool callbackFired = false;

        armController.SetJointAnglesWithCallback(angles, () =>
        {
            callbackFired = true;
        });

        settleCoroutine = StartCoroutine(WaitForPresetSettle(angles, () => callbackFired));
    }

    /// <summary>
    ///     Polls joint proximity to <paramref name="targetAngles"/> every 100 ms.
    ///     Clears presetOverrideActive once the arm has settled (all joints within
    ///     settleThresholdDeg) OR the hard timeout elapses, whichever comes first.
    ///     Also respects the callback grace period so the arm can coast to rest
    ///     before ROS teleop is allowed to resume.
    /// </summary>
    private IEnumerator WaitForPresetSettle(float[] targetAngles, Func<bool> callbackFiredGetter)
    {
        float elapsed = 0f;
        float callbackFiredAt = float.MaxValue;

        while (elapsed < settleTimeoutSeconds)
        {
            yield return new WaitForSeconds(0.1f);
            elapsed += 0.1f;

            // Record the time the callback fired so we can enforce the grace period.
            if (callbackFiredGetter() && callbackFiredAt == float.MaxValue)
            {
                callbackFiredAt = elapsed;
            }

            bool gracePeriodComplete = elapsed >= callbackFiredAt + callbackGracePeriodSeconds;
            bool settled = AreJointsSettled(targetAngles);

            if (settled || gracePeriodComplete)
            {
                string reason = settled ? "joints settled" : "callback grace period elapsed";
                Debug.Log(
                    $"[VirtualKortexDriver] Preset settled ({reason} after {elapsed:F1}s). " +
                    "Calling relaxed_ik reset before resuming teleop."
                );
                yield return CallRelaxedIkResetCoroutine();
                break;
            }
        }

        if (elapsed >= settleTimeoutSeconds)
        {
            Debug.LogWarning(
                $"[VirtualKortexDriver] Preset settle timed out after {settleTimeoutSeconds}s. " +
                "Calling relaxed_ik reset and forcing resumption of ROS teleop."
            );
            yield return CallRelaxedIkResetCoroutine();
        }

        presetOverrideActive = false;
        settleCoroutine = null;
    }

    /// <summary>
    ///     Calls the relaxed_ik reset service so it re-seeds its internal joint
    ///     state from the arm's current pose. Without this, resuming teleop after
    ///     a preset move causes relaxed_ik to immediately drive the arm back to
    ///     wherever its stale internal state was pointing.
    /// </summary>
    private IEnumerator CallRelaxedIkResetCoroutine()
    {
        bool done = false;
        ros.SendServiceMessage<EmptyResponse>(
            relaxedIkResetService,
            new EmptyRequest(),
            response =>
            {
                Debug.Log("[VirtualKortexDriver] relaxed_ik reset service call succeeded.");
                done = true;
            }
        );

        // Wait up to 2 s for the service response; the arm shouldn't need longer.
        float waited = 0f;
        while (!done && waited < 2f)
        {
            yield return new WaitForSeconds(0.05f);
            waited += 0.05f;
        }

        if (!done)
        {
            Debug.LogWarning(
                "[VirtualKortexDriver] relaxed_ik reset service did not respond within 2 s. " +
                "Continuing anyway — snap-back may occur."
            );
        }
    }

    /// <summary>
    ///     Returns true when every joint is within settleThresholdDeg of its
    ///     corresponding target angle (in degrees).
    /// </summary>
    private bool AreJointsSettled(float[] targetAngles)
    {
        float[] currentAngles = armController.GetJointAngles();

        if (currentAngles == null || currentAngles.Length != targetAngles.Length)
        {
            return false;
        }

        for (int i = 0; i < targetAngles.Length; i++)
        {
            float errorDeg = Mathf.Abs(Mathf.DeltaAngle(
                currentAngles[i] * Mathf.Rad2Deg,
                targetAngles[i] * Mathf.Rad2Deg
            ));
            if (errorDeg > settleThresholdDeg)
            {
                return false;
            }
        }

        return true;
    }

    public IEnumerable<string> GetPresetNames()
    {
        return presets.Keys;
    }

    private void LoadPresetsFromDisk()
    {
        presets.Clear();

        if (!File.Exists(PresetFilePath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(PresetFilePath);
            PresetFile data = JsonUtility.FromJson<PresetFile>(json);
            if (data?.presets == null)
            {
                return;
            }

            foreach (PresetEntry entry in data.presets)
            {
                presets[entry.name] = entry.angles;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[VirtualKortexDriver] Failed to load presets from {PresetFilePath}: {e.Message}");
        }
    }

    private void SavePresetsToDisk()
    {
        try
        {
            PresetFile data = new PresetFile { presets = new List<PresetEntry>() };
            foreach (var kvp in presets)
            {
                data.presets.Add(new PresetEntry { name = kvp.Key, angles = kvp.Value });
            }

            string json = JsonUtility.ToJson(data, true);
            File.WriteAllText(PresetFilePath, json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[VirtualKortexDriver] Failed to save presets to {PresetFilePath}: {e.Message}");
        }
    }

    private void RegisterPresetDropdown()
    {
        if (HTTPDash.Instance == null)
        {
            return;
        }

        string[] names = new string[presets.Count];
        presets.Keys.CopyTo(names, 0);

        HTTPDash.Instance.RegisterDropdown(
            $"{robotName} Presets",
            "Load Preset",
            names,
            presetName => LoadPreset(presetName)
        );
    }

    [Serializable]
    private class PresetEntry
    {
        public string name;
        public float[] angles;
    }

    [Serializable]
    private class PresetFile
    {
        public List<PresetEntry> presets;
    }
}
