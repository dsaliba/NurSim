using System.Collections;
using System.Collections.Generic;
using System.Linq;
using RosMessageTypes.Std;
// NOTE: If the auto-generated namespace for gopher_ros_clearcore differs in your
// project, adjust the line below to match (check Assets/RosMessages/ for the folder name).
using RosMessageTypes.GopherRosClearcore;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class SimulationManager : MonoBehaviour
{
    [System.Serializable]
    public class NamedPrefab
    {
        public string name;
        public GameObject prefab;
    }

    [System.Serializable]
    public class MappingFileOption
    {
        public string displayName;
        public string filePath;
    }

    [System.Serializable]
    public class PresetPoseOption
    {
        public string displayName;
        // ROS service name suffix, e.g. "home_pose" or "front_xy_grasp_pose"
        // Full topic: /{armName}/preset_poses/{serviceName}
        public string serviceName;
    }

    [SerializeField]
    public string unitySystemIP = "127.0.0.1";
    public string[] environmentSceneNames;
    public NamedPrefab[] interfaces;

    [Header("Camera Selection")]
    public string[] cameraOptions = new string[] { "Front", "Back", "Side" };

    [Header("Mapping Files")]
    public List<MappingFileOption> mappingFileOptions = new List<MappingFileOption>();

    [Header("Preset Poses")]
    public List<PresetPoseOption> presetPoseOptions = new List<PresetPoseOption>();
    public string rightArmName = "right_arm";
    public string leftArmName  = "left_arm";

    [Header("Chest Height Control")]
    [Tooltip("Minimum chest height in metres.")]
    public float chestHeightMin = 0.0f;
    [Tooltip("Maximum chest height in metres.")]
    public float chestHeightMax = 0.5f;
    [Tooltip("Step between slider ticks in metres (e.g. 0.05 = 5 cm steps).")]
    public float chestHeightStep = 0.05f;
    [Tooltip("Speed fraction sent with each position command (0–1). Matches the speed_fraction field the ClearCore 'am_' command expects.")]
    public float chestHeightSpeedFraction = 0.5f;

    public bool loadOnStart = true;

    string activeEnvironmentName;
    string activeInterfaceName;

    private ROSConnection ros;
    private GameObject activeInterface;
    private bool firstUpdate = true;
    bool lastLatch = false;

    // ── Chest height state ──────────────────────────────────────────────
    // Tracks the last value sent via the slider so Save Default always
    // has something meaningful to store even before the first Move command.
    private float lastChestHeightSent;
    private const string PrefKeyChestDefault = "IONA_ChestHeightDefault";

    private const string SessionKeyHasPending  = "IONA_PendingSceneRestart";
    private const string SessionKeyEnvIndex   = "IONA_PendingEnvIndex";
    private const string SessionKeyIfaceIndex = "IONA_PendingIfaceIndex";

    // True only when we ourselves triggered the stop via RequestEditorRestartWithSelection.
#if UNITY_EDITOR
    private static bool s_RestartInProgress = false;
#endif

    // ── ROS helpers ─────────────────────────────────────────────────────

    private bool IsRosReady()
    {
        if (ros == null)
        {
            Debug.LogWarning("SimulationManager: ROSConnection is null — skipping publish.");
            return false;
        }
        return true;
    }

    private void SafePublish<T>(string topic, T message) where T : Unity.Robotics.ROSTCPConnector.MessageGeneration.Message
    {
        if (!IsRosReady()) return;
        try
        {
            ros.Publish(topic, message);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"SimulationManager: Could not publish on '{topic}': {e.Message}");
        }
    }

    // ── Validation helpers ──────────────────────────────────────────────

    private bool IsValidEnvIndex(int index) =>
        environmentSceneNames != null &&
        index >= 0 &&
        index < environmentSceneNames.Length;

    private bool IsValidIfaceIndex(int index) =>
        interfaces != null &&
        index >= 0 &&
        index < interfaces.Length;

    private bool CanLoadEnvironment(int index)
    {
        if (!IsValidEnvIndex(index))
        {
            Debug.LogWarning($"SimulationManager: Environment index {index} is out of range " +
                             $"(count: {environmentSceneNames?.Length ?? 0}). Assign scenes in the inspector.");
            return false;
        }

        if (string.IsNullOrEmpty(environmentSceneNames[index]))
        {
            Debug.LogWarning($"SimulationManager: Environment scene name at index {index} is empty. " +
                             "Fill in the scene name in the SimulationManager inspector.");
            return false;
        }

        return true;
    }

    private bool CanLoadInterface(int index)
    {
        if (!IsValidIfaceIndex(index))
        {
            Debug.LogWarning($"SimulationManager: Interface index {index} is out of range " +
                             $"(count: {interfaces?.Length ?? 0}). Assign interfaces in the inspector.");
            return false;
        }

        if (interfaces[index] == null)
        {
            Debug.LogWarning($"SimulationManager: Interface entry at index {index} is null.");
            return false;
        }

        if (interfaces[index].prefab == null)
        {
            Debug.LogWarning($"SimulationManager: Prefab for interface '{interfaces[index].name}' " +
                             "is not assigned in the inspector.");
            return false;
        }

        return true;
    }

    // ── Start ───────────────────────────────────────────────────────────

    void Start()
    {
        StartCoroutine(Initialize());
    }

    IEnumerator Initialize()
    {
        int envIndex   = 0;
        int ifaceIndex = 0;

#if UNITY_EDITOR
        if (SessionState.GetBool(SessionKeyHasPending, false))
        {
            SessionState.SetBool(SessionKeyHasPending, false);

            int pendingEnv   = SessionState.GetInt(SessionKeyEnvIndex,   -1);
            int pendingIface = SessionState.GetInt(SessionKeyIfaceIndex, -1);

            if (IsValidEnvIndex(pendingEnv))     envIndex   = pendingEnv;
            if (IsValidIfaceIndex(pendingIface)) ifaceIndex = pendingIface;

            Debug.Log($"SimulationManager: Resuming with env index {envIndex}, iface index {ifaceIndex}.");
        }
#endif

        yield return null; // wait one frame for ROSConnection's ConnectionThread to start

        try
        {
            ros = ROSConnection.GetOrCreateInstance();
            if (ros == null)
            {
                Debug.LogWarning("SimulationManager: ROSConnection.GetOrCreateInstance() returned null. " +
                                 "ROS features will be unavailable this session.");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"SimulationManager: Failed to acquire ROSConnection: {e.Message}. " +
                             "ROS features will be unavailable this session.");
            ros = null;
        }

        if (ros != null)
        {
            ros.RegisterPublisher<StringMsg>("unity/ip", latch: true);
            ros.RegisterPublisher<StringMsg>("trial/dash");
            ros.RegisterPublisher<BoolMsg>("/task/end");
            ros.RegisterPublisher<StringMsg>("/unity/camera_selection");
            ros.RegisterPublisher<StringMsg>("/mapping_player/file_path");
            ros.RegisterPublisher<StringMsg>("/preset_pose_command");
            // Chest absolute-position topic consumed by chest_control.py __position_callback.
            // The Position message has fields: float position (metres) and float speed_fraction (0–1).
            ros.RegisterPublisher<PositionMsg>("/chest_control/position");
        }

        if (loadOnStart)
        {
            bool envOk   = CanLoadEnvironment(envIndex);
            bool ifaceOk = CanLoadInterface(ifaceIndex);

            Debug.Log($"SimulationManager: loadOnStart — envIndex={envIndex} envOk={envOk}, ifaceIndex={ifaceIndex} ifaceOk={ifaceOk}");

            if (envOk)
            {
                System.Action onDone = ifaceOk ? () => LoadInterfaceScene(ifaceIndex) : (System.Action)null;
                StartCoroutine(LoadEnvironmentSceneAsync(envIndex, onDone));
            }
            else
            {
                Debug.LogWarning("SimulationManager: Environment load skipped due to inspector misconfiguration (see warnings above).");
            }

            if (!ifaceOk)
            {
                Debug.LogWarning($"SimulationManager: Interface load skipped — prefab at index {ifaceIndex} is not assigned.");
            }
        }
        else
        {
            Debug.Log("SimulationManager: loadOnStart is false — skipping initial load.");
        }

        if (ros != null)
        {
            ros.Subscribe<BoolMsg>("/haptic/latched", msg =>
            {
                if (msg.data != lastLatch)
                    HTTPDash.Instance.SendNotification("Haptics",
                        $"Device {(msg.data ? "latched" : "unlatched")}",
                        msg.data ? "blue" : "red");
                lastLatch = msg.data;
            });
        }

        HTTPDash.Instance.RegisterButton("End Task", "End",
            s => SafePublish("/task/end", new BoolMsg(true)));

        RegisterSceneAndInterfaceDashboardControl();
        RegisterCameraDashboardControls();
        RegisterMappingFileDashboardControls();
        RegisterPresetPoseDashboardControls();
        RegisterChestHeightControl();
    }

    // ── Combined environment + interface load card ──────────────────────

    private void RegisterSceneAndInterfaceDashboardControl()
    {
        if (environmentSceneNames == null || environmentSceneNames.Length == 0 ||
            interfaces == null || interfaces.Length == 0)
        {
            Debug.LogWarning("SimulationManager: Need at least one environment and one interface configured for the Scene Setup dashboard card.");
            return;
        }

        string[] interfaceNames = interfaces.Select(i => i.name).ToArray();

        var fields = new List<HTTPDash.MultiFieldCard.MultiField>
        {
            HTTPDash.MultiFieldCard.MultiField.Dropdown("environment", "Environment", environmentSceneNames),
            HTTPDash.MultiFieldCard.MultiField.Dropdown("interface",   "Interface",   interfaceNames)
        };

        HTTPDash.Instance.RegisterMultiField(
            "Scene Setup",
            "Load",
            fields,
            values =>
            {
                string envName   = values.TryGetValue("environment", out var e) ? e : null;
                string ifaceName = values.TryGetValue("interface",   out var i) ? i : null;

                if (string.IsNullOrEmpty(envName) || string.IsNullOrEmpty(ifaceName))
                {
                    Debug.LogWarning("SimulationManager: Missing environment/interface selection from dashboard.");
                    return;
                }

#if UNITY_EDITOR
                int envIdx   = System.Array.IndexOf(environmentSceneNames, envName);
                int ifaceIdx = System.Array.IndexOf(interfaceNames,        ifaceName);

                if (envIdx < 0 || ifaceIdx < 0)
                {
                    Debug.LogWarning($"SimulationManager: Could not resolve selection to indices " +
                                     $"(env='{envName}'→{envIdx}, iface='{ifaceName}'→{ifaceIdx}). Aborting restart.");
                    return;
                }

                HTTPDash.Instance.SendNotification("Restarting",
                    $"Restarting Play Mode with {envName} / {ifaceName}", "blue");
                RequestEditorRestartWithSelection(envIdx, ifaceIdx);
#else
                int envIdx   = System.Array.IndexOf(environmentSceneNames, envName);
                int ifaceIdx = System.Array.IndexOf(interfaceNames,        ifaceName);

                if (envIdx < 0)
                {
                    Debug.LogWarning($"SimulationManager: Environment '{envName}' not found in environmentSceneNames.");
                    return;
                }
                if (ifaceIdx < 0)
                {
                    Debug.LogWarning($"SimulationManager: Interface '{ifaceName}' not found in interfaces.");
                    return;
                }

                bool envOk   = CanLoadEnvironment(envIdx);
                bool ifaceOk = CanLoadInterface(ifaceIdx);

                if (envOk)
                    StartCoroutine(ReloadEnvironmentSceneAsync(envIdx,
                        onDone: ifaceOk ? () => ReloadInterfaceScene(ifaceIdx) : (System.Action)null));
                else if (ifaceOk)
                    ReloadInterfaceScene(ifaceIdx);

                HTTPDash.Instance.SendNotification("Reloading",
                    $"{envName} / {ifaceName}", "blue");
#endif
            });
    }

#if UNITY_EDITOR
    private static void RequestEditorRestartWithSelection(int envIndex, int ifaceIndex)
    {
        s_RestartInProgress = true;

        SessionState.SetInt(SessionKeyEnvIndex,   envIndex);
        SessionState.SetInt(SessionKeyIfaceIndex, ifaceIndex);
        SessionState.SetBool(SessionKeyHasPending, true);

        EditorApplication.playModeStateChanged -= OnPlayModeStateChangedForRestart;
        EditorApplication.playModeStateChanged += OnPlayModeStateChangedForRestart;
        EditorApplication.isPlaying = false;
    }

    private static void OnPlayModeStateChangedForRestart(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChangedForRestart;
        s_RestartInProgress = false;
        EditorApplication.isPlaying = true;
    }
#endif

    // ── Camera selection dropdown ───────────────────────────────────────

    private void RegisterCameraDashboardControls()
    {
        if (cameraOptions == null || cameraOptions.Length == 0)
        {
            Debug.LogWarning("SimulationManager: No camera options configured for dashboard.");
            return;
        }

        HTTPDash.Instance.RegisterDropdown(
            "Camera Selection",
            "Select",
            cameraOptions,
            (string selectedCamera) =>
            {
                if (string.IsNullOrEmpty(selectedCamera))
                {
                    Debug.LogWarning("SimulationManager: Empty camera selection received.");
                    return;
                }
                SafePublish("/unity/camera_selection", new StringMsg(selectedCamera));
                HTTPDash.Instance.SendNotification("Camera Switched",
                    $"Active camera: {selectedCamera}", "blue");
            });
    }

    // ── Mapping file toggle dropdown ────────────────────────────────────

    private void RegisterMappingFileDashboardControls()
    {
        if (mappingFileOptions == null || mappingFileOptions.Count == 0)
        {
            Debug.LogWarning("SimulationManager: No mapping file options configured for dashboard.");
            return;
        }

        string[] displayNames = mappingFileOptions.Select(m => m.displayName).ToArray();

        HTTPDash.Instance.RegisterDropdown(
            "Mapping File",
            "Set Active",
            displayNames,
            (string selectedDisplayName) =>
            {
                if (string.IsNullOrEmpty(selectedDisplayName))
                {
                    Debug.LogWarning("SimulationManager: Empty mapping file selection received.");
                    return;
                }

                MappingFileOption selected = mappingFileOptions
                    .FirstOrDefault(m => m.displayName == selectedDisplayName);

                if (selected == null)
                {
                    Debug.LogWarning($"SimulationManager: Unknown mapping file option '{selectedDisplayName}'.");
                    return;
                }

                if (string.IsNullOrEmpty(selected.filePath))
                {
                    Debug.LogWarning($"SimulationManager: Mapping file option '{selectedDisplayName}' has an empty file path.");
                    return;
                }

                SafePublish("/mapping_player/file_path", new StringMsg(selected.filePath));
                HTTPDash.Instance.SendNotification("Mapping File Set",
                    $"Active mapping: {selected.displayName}", "blue");
            });
    }

    // ── Preset poses — one card per arm ────────────────────────────────

    private void RegisterPresetPoseDashboardControls()
    {
        if (presetPoseOptions == null || presetPoseOptions.Count == 0)
        {
            Debug.LogWarning("SimulationManager: No preset pose options configured for dashboard.");
            return;
        }

        string[] displayNames = presetPoseOptions.Select(p => p.displayName).ToArray();

        RegisterPresetPoseCard("Right Arm Preset", rightArmName, displayNames);
        RegisterPresetPoseCard("Left Arm Preset",  leftArmName,  displayNames);
    }

    private void RegisterPresetPoseCard(string cardTitle, string armName, string[] displayNames)
    {
        if (string.IsNullOrEmpty(armName))
        {
            Debug.LogWarning($"SimulationManager: Arm name for card '{cardTitle}' is empty. Skipping.");
            return;
        }

        HTTPDash.Instance.RegisterDropdown(
            cardTitle,
            "Execute",
            displayNames,
            (string selectedDisplayName) =>
            {
                if (string.IsNullOrEmpty(selectedDisplayName))
                {
                    Debug.LogWarning($"SimulationManager: Empty preset pose selection for '{cardTitle}'.");
                    return;
                }

                PresetPoseOption selected = presetPoseOptions
                    .FirstOrDefault(p => p.displayName == selectedDisplayName);

                if (selected == null)
                {
                    Debug.LogWarning($"SimulationManager: Unknown preset pose '{selectedDisplayName}'.");
                    return;
                }

                if (string.IsNullOrEmpty(selected.serviceName))
                {
                    Debug.LogWarning($"SimulationManager: Preset pose '{selectedDisplayName}' has an empty service name.");
                    return;
                }

                SafePublish("/preset_pose_command", new StringMsg($"{armName}/{selected.serviceName}"));
                HTTPDash.Instance.SendNotification("Preset Pose",
                    $"{armName}: {selected.displayName}", "blue");
            });
    }

    // ── Chest height control ────────────────────────────────────────────

    /// <summary>
    /// Registers two dashboard cards:
    ///   "Chest Height" — a tick-snapping slider that publishes an absolute
    ///     position command to /chest_control/position on "Move".
    ///   "Save Chest Default" — a button that writes the last moved-to height
    ///     into PlayerPrefs so the slider reopens at that position next session.
    /// </summary>
    private void RegisterChestHeightControl()
    {
        // Load the persisted default, falling back to chestHeightMin if nothing saved yet.
        float savedDefault = PlayerPrefs.GetFloat(PrefKeyChestDefault, chestHeightMin);
        // Clamp in case the inspector range has changed since it was saved.
        savedDefault = Mathf.Clamp(savedDefault, chestHeightMin, chestHeightMax);

        // Seed the tracker so Save Default works even before the first Move.
        lastChestHeightSent = savedDefault;

        HTTPDash.Instance.RegisterSlider(
            title:          "Chest Height",
            buttonText:     "Move",
            min:            chestHeightMin,
            max:            chestHeightMax,
            step:           chestHeightStep,
            defaultValue:   savedDefault,
            callback: (float height) =>
            {
                lastChestHeightSent = height;

                // chest_control.py __position_callback:
                //   serial_command = f'am_{message.position * 1000}_{message.speed_fraction}_'
                // Actual .msg layout: float32 position + float32 speed_fraction = 8 bytes total.
                // position is metres; the node multiplies by 1000 for the ClearCore (→ mm).
                var msg = new PositionMsg(
                    position:       (float)height,              // float32 — first field, metres
                    speed_fraction: chestHeightSpeedFraction    // float32 — second field, 0–1
                );
                SafePublish("/chest_control/position", msg);

                HTTPDash.Instance.SendNotification(
                    "Chest Height",
                    $"Moving to {height:F3} m",
                    "blue");
            },
            saveButtonText: "Save as Default",
            saveCallback: (float height) =>
            {
                lastChestHeightSent = height;
                PlayerPrefs.SetFloat(PrefKeyChestDefault, height);
                PlayerPrefs.Save();
                HTTPDash.Instance.SendNotification(
                    "Default Saved",
                    $"Chest default height set to {height:F3} m",
                    "#1a9c4b");
            });
    }

    // ── Scene / environment helpers ─────────────────────────────────────

    public void ResetCurrentEnvironment() => StartCoroutine(ResetEnvironmentCoroutine());

    private IEnumerator ResetEnvironmentCoroutine()
    {
        if (string.IsNullOrEmpty(activeEnvironmentName))
        {
            Debug.LogWarning("SimulationManager: No active environment to reset.");
            yield break;
        }

        if (TaskEnvironment.currentIndex >= 0 &&
            TaskEnvironment.currentIndex < TaskEnvironment.instances.Count)
        {
            TaskEnvironment.instances.Remove(TaskEnvironment.instances[TaskEnvironment.currentIndex]);
        }

        AsyncOperation unloadOp = SceneManager.UnloadSceneAsync(activeEnvironmentName);
        if (unloadOp != null)
            while (!unloadOp.isDone)
                yield return null;

        if (CanLoadEnvironment(0))
            yield return StartCoroutine(LoadEnvironmentSceneAsync(0));
    }

    private IEnumerator ReloadEnvironmentSceneAsync(int sceneIndex, System.Action onDone = null)
    {
        if (!string.IsNullOrEmpty(activeEnvironmentName))
        {
            if (TaskEnvironment.currentIndex >= 0 &&
                TaskEnvironment.currentIndex < TaskEnvironment.instances.Count)
            {
                TaskEnvironment.instances.Remove(TaskEnvironment.instances[TaskEnvironment.currentIndex]);
            }

            AsyncOperation unloadOp = SceneManager.UnloadSceneAsync(activeEnvironmentName);
            if (unloadOp != null)
                while (!unloadOp.isDone)
                    yield return null;
        }

        yield return StartCoroutine(LoadEnvironmentSceneAsync(sceneIndex, onDone));
    }

    private void ReloadInterfaceScene(int sceneIndex)
    {
        if (!CanLoadInterface(sceneIndex))
            return;

        if (activeInterface != null)
            Destroy(activeInterface);

        LoadInterfaceScene(sceneIndex);
    }

    public void LoadInterfaceScene(int sceneIndex)
    {
        if (!CanLoadInterface(sceneIndex))
            return;

        NamedPrefab entry    = interfaces[sceneIndex];
        activeInterfaceName  = entry.name;
        activeInterface      = Instantiate(entry.prefab, new Vector3(0, 100, 0), Quaternion.identity);
        DontDestroyOnLoad(activeInterface);
    }

    public void LoadEnvironmentScene(int sceneIndex) =>
        StartCoroutine(LoadEnvironmentSceneAsync(sceneIndex));

    private IEnumerator LoadEnvironmentSceneAsync(int sceneIndex, System.Action onDone = null)
    {
        Debug.Log($"SimulationManager: LoadEnvironmentSceneAsync called with index {sceneIndex}.");

        if (!CanLoadEnvironment(sceneIndex))
            yield break;

        string sceneName = environmentSceneNames[sceneIndex];
        activeEnvironmentName = sceneName;
        Debug.Log($"SimulationManager: Attempting to load scene '{sceneName}'.");

        for (int i = 0; i < TaskEnvironment.instances.Count; i++)
        {
            if (activeEnvironmentName.Equals(TaskEnvironment.instances[i].sceneName))
            {
                TaskEnvironment.currentIndex = i;
                break;
            }
        }

        AsyncOperation loadOp = SceneManager.LoadSceneAsync(
            activeEnvironmentName, LoadSceneMode.Additive);

        if (loadOp == null)
        {
            Debug.LogWarning($"SimulationManager: Could not load scene '{activeEnvironmentName}'. " +
                             "Make sure it is added to File → Build Settings and the name matches exactly.");
            yield break;
        }

        loadOp.allowSceneActivation = true;

        while (!loadOp.isDone)
            yield return null;

        Scene labScene = SceneManager.GetSceneByName(activeEnvironmentName);
        if (labScene.IsValid())
        {
            SceneManager.SetActiveScene(labScene);
        }
        else
        {
            Debug.LogWarning($"SimulationManager: Scene '{activeEnvironmentName}' finished loading but " +
                             "GetSceneByName returned invalid — it may not be set as the active scene.");
        }

        HTTPDash.Instance.SendNotification("Scene Loaded",
            "Loaded scene: " + activeEnvironmentName, "blue");

        onDone?.Invoke();
    }

    // ── Cleanup ─────────────────────────────────────────────────────────

    void OnDestroy()
    {
#if UNITY_EDITOR
        if (s_RestartInProgress)
        {
            // Play mode was stopped by our own restart request — leave the handler
            // and pending flag alone so OnPlayModeStateChangedForRestart can
            // re-enter play mode with the correct scene selection.
        }
        else
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChangedForRestart;
            SessionState.SetBool(SessionKeyHasPending, false);
        }
#endif

        try
        {
            if (ros != null)
                ros.Disconnect();
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"SimulationManager: Exception during ROS disconnect: {e.Message}");
        }

        if (activeInterface != null)
            Destroy(activeInterface);
    }

    // ── Update ──────────────────────────────────────────────────────────

    void Update()
    {
        if (firstUpdate)
        {
            if (ros == null)
            {
                try { ros = ROSConnection.GetOrCreateInstance(); }
                catch (System.Exception) { ros = null; }
            }

            if (!string.IsNullOrEmpty(unitySystemIP))
                SafePublish("unity/ip", new StringMsg(unitySystemIP));
            else
                Debug.LogWarning("SimulationManager: unitySystemIP is empty; skipping IP publish.");

            firstUpdate = false;
        }
    }
}
