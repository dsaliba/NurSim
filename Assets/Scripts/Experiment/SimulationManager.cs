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
    // ── Singleton ────────────────────────────────────────────────────────
    public static SimulationManager Instance { get; private set; }

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

    /// <summary>
    /// Maps an API interface_condition value (e.g. "gamepad_robot") to the
    /// displayName of the mapping file that should be activated for it.
    /// Set these in the inspector so researchers can reconfigure without code changes.
    /// Example: interfaceCondition="gamepad_robot" → mappingFileDisplayName="Joystick Mapping"
    /// </summary>
    [System.Serializable]
    public class InterfaceConditionMapping
    {
        [Tooltip("API interface_condition value, e.g. 'gamepad_robot'. Case-insensitive.")]
        public string interfaceCondition;
        [Tooltip("The displayName from Mapping File Options that should be activated for this interface.")]
        public string mappingFileDisplayName;
    }

    [SerializeField]
    public string unitySystemIP = "127.0.0.1";
    public string[] environmentSceneNames;
    public NamedPrefab[] interfaces;

    [Header("Camera Selection")]
    public string[] cameraOptions = new string[] { "Front", "Back", "Side" };

    [Header("Mapping Files")]
    public List<MappingFileOption> mappingFileOptions = new List<MappingFileOption>();

    [Header("Orchestrator Condition Mappings")]
    [Tooltip("Maps API interface_condition codes to mapping file display names. " +
             "Add one entry per interface type that uses a mapping file.")]
    public List<InterfaceConditionMapping> interfaceConditionMappings = new List<InterfaceConditionMapping>();

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
    [Tooltip("Speed fraction sent with each position command (0–1).")]
    public float chestHeightSpeedFraction = 0.5f;

    public bool loadOnStart = true;

    string activeEnvironmentName;
    string activeInterfaceName;

    private ROSConnection ros;
    private GameObject activeInterface;
    bool lastLatch = false;

    // ── Chest height state ──────────────────────────────────────────────
    private float lastChestHeightSent;
    private const string PrefKeyChestDefault = "IONA_ChestHeightDefault";

    // SessionState keys for editor-restart handoff
    private const string SessionKeyHasPending  = "IONA_PendingSceneRestart";
    private const string SessionKeyEnvIndex    = "IONA_PendingEnvIndex";
    private const string SessionKeyIfaceIndex  = "IONA_PendingIfaceIndex";
    // -1 stored in SessionKeyIfaceIndex means "restart with no interface".
    // This key explicitly records whether an interface was requested so that
    // ifaceIndex=0 (first entry) and "no interface" remain distinguishable.
    private const string SessionKeyIfaceEnabled = "IONA_PendingIfaceEnabled";

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

    private void SafePublish<T>(string topic, T message)
        where T : Unity.Robotics.ROSTCPConnector.MessageGeneration.Message
    {
        if (!IsRosReady()) return;
        try   { ros.Publish(topic, message); }
        catch (System.Exception e)
        { Debug.LogWarning($"SimulationManager: Could not publish on '{topic}': {e.Message}"); }
    }

    // ── Validation helpers ──────────────────────────────────────────────

    private bool IsValidEnvIndex(int index) =>
        environmentSceneNames != null && index >= 0 && index < environmentSceneNames.Length;

    private bool IsValidIfaceIndex(int index) =>
        interfaces != null && index >= 0 && index < interfaces.Length;

    private bool CanLoadEnvironment(int index)
    {
        if (!IsValidEnvIndex(index))
        {
            Debug.LogWarning($"SimulationManager: Environment index {index} out of range " +
                             $"(count: {environmentSceneNames?.Length ?? 0}).");
            return false;
        }
        if (string.IsNullOrEmpty(environmentSceneNames[index]))
        {
            Debug.LogWarning($"SimulationManager: Environment scene name at index {index} is empty.");
            return false;
        }
        return true;
    }

    private bool CanLoadInterface(int index)
    {
        if (!IsValidIfaceIndex(index))
        {
            Debug.LogWarning($"SimulationManager: Interface index {index} out of range " +
                             $"(count: {interfaces?.Length ?? 0}).");
            return false;
        }
        if (interfaces[index] == null || interfaces[index].prefab == null)
        {
            Debug.LogWarning($"SimulationManager: Interface prefab at index {index} is not assigned.");
            return false;
        }
        return true;
    }

    // ── Awake / Start ────────────────────────────────────────────────────

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void Start()
    {
        StartCoroutine(Initialize());
    }

    IEnumerator Initialize()
    {
        int  envIndex     = 0;
        int  ifaceIndex   = 0;
        bool loadIface    = true;   // false = restart was requested with no interface
        string[] pendingGeometries = null;

#if UNITY_EDITOR
        if (SessionState.GetBool(SessionKeyHasPending, false))
        {
            SessionState.SetBool(SessionKeyHasPending, false);

            int  pendingEnv     = SessionState.GetInt(SessionKeyEnvIndex,    -1);
            int  pendingIface   = SessionState.GetInt(SessionKeyIfaceIndex,  -1);
            bool ifaceRequested = SessionState.GetBool(SessionKeyIfaceEnabled, true);

            if (IsValidEnvIndex(pendingEnv))   envIndex   = pendingEnv;
            loadIface  = ifaceRequested;
            if (loadIface && IsValidIfaceIndex(pendingIface)) ifaceIndex = pendingIface;

            // Read AND CLEAR pending geometries before the scene loads.
            // TaskEnvironment.Start() would otherwise read this from SessionState too early
            // (before SenquentialGoalTrial.Start() runs) causing a NullReferenceException.
            // SimulationManager applies them itself via ApplyGeometryOrderNextFrame after
            // the scene is loaded and all Start() methods have had a chance to run.
            string pendingGeoRaw = SessionState.GetString("IONA_PendingGeometries", "");
            SessionState.SetString("IONA_PendingGeometries", "");
            if (!string.IsNullOrEmpty(pendingGeoRaw))
                pendingGeometries = pendingGeoRaw.Split(';');

            Debug.Log($"SimulationManager: Resuming — env={envIndex}, " +
                      $"iface={ifaceIndex} (loadIface={loadIface}) " +
                      $"pendingGeos={pendingGeometries?.Length ?? 0}.");
        }
#endif

        yield return null;

        try
        {
            ros = ROSConnection.GetOrCreateInstance();
            if (ros == null)
                Debug.LogWarning("SimulationManager: ROSConnection returned null; ROS unavailable.");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"SimulationManager: ROSConnection failed: {e.Message}");
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
            ros.RegisterPublisher<PositionMsg>("/chest_control/position");

            // Publish IP immediately — the publisher is now registered so this will succeed.
            // (Update() used to do this on the first frame, but Initialize() has already
            //  yielded once by then, so the publisher wasn't registered yet and the
            //  publish silently no-oped.)
            PublishSystemIP();
        }

        if (loadOnStart)
        {
            bool envOk   = CanLoadEnvironment(envIndex);
            bool ifaceOk = loadIface && CanLoadInterface(ifaceIndex);

            Debug.Log($"SimulationManager: loadOnStart — env={envIndex} ok={envOk}, " +
                      $"iface={ifaceIndex} ok={ifaceOk} requested={loadIface}");

            if (envOk)
            {
                string[] capturedGeos = pendingGeometries;
                System.Action onDone = () =>
                {
                    if (ifaceOk) LoadInterfaceScene(ifaceIndex);
                    if (capturedGeos != null && capturedGeos.Length > 0)
                        StartCoroutine(ApplyGeometryOrderNextFrame(capturedGeos));
                };
                StartCoroutine(LoadEnvironmentSceneAsync(envIndex, onDone));
            }
            else
            {
                Debug.LogWarning("SimulationManager: Environment load skipped (see warnings above).");
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

        HTTPDash.Instance.RegisterButton("Unity System IP", "Republish IP",
            _ =>
            {
                PublishSystemIP();
                HTTPDash.Instance.SendNotification("Unity System IP",
                    $"Published: {unitySystemIP}", "blue");
            });

        RegisterSceneAndInterfaceDashboardControl();
        RegisterCameraDashboardControls();
        RegisterMappingFileDashboardControls();
        RegisterPresetPoseDashboardControls();
        RegisterChestHeightControl();
    }

    // ── IP publish helper ────────────────────────────────────────────────

    private void PublishSystemIP()
    {
        if (string.IsNullOrEmpty(unitySystemIP))
        {
            Debug.LogWarning("SimulationManager: unitySystemIP is empty — skipping publish.");
            return;
        }
        SafePublish("unity/ip", new StringMsg(unitySystemIP));
        Debug.Log($"[SimulationManager] Published unity/ip → '{unitySystemIP}'");
    }

    // ── Orchestrator-driven condition application ────────────────────────
    //
    // Called by OrchestratorCard.Invoke() for action == "apply_condition".
    //
    // Order of operations:
    //   1. Camera  — publish viewpoint to /unity/camera_selection immediately
    //   2. Mapping — publish on first trial of block (viewpointPosition == 1)
    //   3. Sheet order — apply geometry_sequence to TaskEnvironment goals
    //   4. Scene restart — reload env + interface (or env only if no matching prefab)
    //
    // Camera and mapping are published before the restart so the messages
    // are in-flight while Unity is stopping play mode (editor) or unloading
    // scenes (build).  Sheet ordering is applied after the env is loaded via
    // the onDone callback.

    /// <param name="mappingFilePath">
    /// Full file path of the mapping file to publish, configured directly in the
    /// OrchestratorClient inspector.  Published as-is to /mapping_player/file_path
    /// on the first trial of each block.  Empty or null means skip.
    /// </param>
    public void ApplyCondition(string interfaceCondition, string viewpoint,
                               int viewpointPosition, string[] orderedSheetLabels,
                               string targetSceneName = null,
                               string mappingFilePath = null)
    {
        Debug.Log($"[SimulationManager] ApplyCondition: iface='{interfaceCondition}' " +
                  $"viewpoint='{viewpoint}' pos={viewpointPosition} " +
                  $"sheets={orderedSheetLabels?.Length ?? 0}" +
                  (string.IsNullOrEmpty(mappingFilePath) ? "" : $" mapping='{mappingFilePath}'"));

        // ── 1. Camera ────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(viewpoint) && cameraOptions != null)
        {
            string cam = cameraOptions.FirstOrDefault(o =>
                string.Equals(o, viewpoint, System.StringComparison.OrdinalIgnoreCase));

            if (cam != null)
            {
                SafePublish("/unity/camera_selection", new StringMsg(cam));
                HTTPDash.Instance?.SendNotification("Camera", $"Viewpoint → {cam}", "blue");
                Debug.Log($"[SimulationManager] Camera → '{cam}'");
            }
            else
            {
                Debug.LogWarning($"[SimulationManager] No camera option matches '{viewpoint}'. " +
                                 $"Available: {string.Join(", ", cameraOptions)}");
            }
        }

        // ── 2. Mapping file (first trial of each block only) ─────────────
        // Path is set directly in the OrchestratorClient inspector and passed in.
        // No lookup required — publish straight to /mapping_player/file_path.
        if (viewpointPosition == 1 && !string.IsNullOrEmpty(mappingFilePath))
        {
            SafePublish("/mapping_player/file_path", new StringMsg(mappingFilePath));
            string fileName = System.IO.Path.GetFileName(mappingFilePath);
            HTTPDash.Instance?.SendNotification("Mapping File", $"Active: {fileName}", "blue");
            Debug.Log($"[SimulationManager] Mapping → '{mappingFilePath}'");
        }

        // ── 3. Resolve interface index ────────────────────────────────────
        // -1 means "restart environment but instantiate no interface prefab"
        // (used for physical-robot conditions where no Unity UI prefab is needed).
        int ifaceIdx = -1;
        if (!string.IsNullOrEmpty(interfaceCondition) && interfaces != null)
        {
            string norm = interfaceCondition.Replace("_", " ");
            for (int i = 0; i < interfaces.Length; i++)
            {
                if (string.Equals(interfaces[i].name, interfaceCondition,
                                  System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(interfaces[i].name, norm,
                                  System.StringComparison.OrdinalIgnoreCase))
                {
                    ifaceIdx = i;
                    break;
                }
            }
            if (ifaceIdx < 0)
                Debug.Log($"[SimulationManager] No interface prefab matches '{interfaceCondition}' — " +
                          "restarting with no interface (physical robot mode).");
        }

        // ── 4. Resolve environment index ──────────────────────────────────
        // Prefer the caller-supplied targetSceneName (set by OrchestratorClient, which
        // knows the API environment_type) so that cross-block environment changes work
        // correctly (e.g. human_hand → gamepad_robot switches scenes).
        // Falls back to the currently active scene for manual / Scene Setup dashboard use.
        int envIdx = 0;
        string resolveEnvBy = !string.IsNullOrEmpty(targetSceneName)
                               ? targetSceneName : activeEnvironmentName;
        if (!string.IsNullOrEmpty(resolveEnvBy) && environmentSceneNames != null)
        {
            int found = System.Array.IndexOf(environmentSceneNames, resolveEnvBy);
            if (found >= 0)
                envIdx = found;
            else if (!string.IsNullOrEmpty(targetSceneName))
                Debug.LogWarning($"[SimulationManager] Target scene '{targetSceneName}' not found " +
                                 $"in environmentSceneNames — falling back to index 0. " +
                                 $"Available: {string.Join(", ", environmentSceneNames)}");
        }

        // ── 5. Sheet / goal ordering (applied after scene reloads) ────────
        // Capture the already-sorted label array; the callback closes over it.
        string[] capturedGeometries = orderedSheetLabels;

        System.Action onSceneLoaded = null;
        if (capturedGeometries != null && capturedGeometries.Length > 0)
        {
            onSceneLoaded = () =>
            {
                // Slight delay so TaskEnvironment.Start() has run in the new scene.
                StartCoroutine(ApplyGeometryOrderNextFrame(capturedGeometries));
            };
        }

        // ── 6. Restart ────────────────────────────────────────────────────
        if (!CanLoadEnvironment(envIdx))
        {
            Debug.LogWarning("[SimulationManager] Cannot restart — environment index invalid.");
            return;
        }

#if UNITY_EDITOR
        HTTPDash.Instance?.SendNotification("Restarting",
            $"{environmentSceneNames[envIdx]} / " +
            (ifaceIdx >= 0 ? interfaces[ifaceIdx].name : "no interface"), "blue");
        RequestEditorRestartWithSelection(envIdx, ifaceIdx, capturedGeometries);
#else
        bool ifaceOk = ifaceIdx >= 0 && CanLoadInterface(ifaceIdx);
        StartCoroutine(ReloadEnvironmentSceneAsync(envIdx, onDone: () =>
        {
            if (ifaceOk) ReloadInterfaceScene(ifaceIdx);
            onSceneLoaded?.Invoke();
        }));
        HTTPDash.Instance?.SendNotification("Scene Reloading",
            $"{environmentSceneNames[envIdx]} / " +
            (ifaceOk ? interfaces[ifaceIdx].name : "no interface"), "blue");
#endif
    }

    // ── Geometry / goal ordering ─────────────────────────────────────────

    private IEnumerator ApplyGeometryOrderNextFrame(string[] orderedNames)
    {
        yield return null; // let TaskEnvironment.Start() run first
        ApplyGeometryOrderNow(orderedNames);
    }

    private void ApplyGeometryOrderNow(string[] orderedNames)
    {
        if (orderedNames == null || orderedNames.Length == 0) return;

        if (TaskEnvironment.instances == null || TaskEnvironment.instances.Count == 0)
        {
            Debug.LogWarning("[SimulationManager] No TaskEnvironment instances found for geometry ordering.");
            return;
        }

        int idx = Mathf.Clamp(TaskEnvironment.currentIndex, 0, TaskEnvironment.instances.Count - 1);
        var env = TaskEnvironment.instances[idx];

        // orderedNames is already sorted by sheetNumber and label-extracted by the caller.
        Debug.Log($"[SimulationManager] Applying geometry order to '{env.sceneName}': " +
                  string.Join(" → ", orderedNames));

        env.ApplyGeometrySequence(orderedNames);
    }

    // ── Combined environment + interface load card ──────────────────────

    private void RegisterSceneAndInterfaceDashboardControl()
    {
        if (environmentSceneNames == null || environmentSceneNames.Length == 0 ||
            interfaces == null || interfaces.Length == 0)
        {
            Debug.LogWarning("SimulationManager: Need at least one environment and one interface " +
                             "configured for the Scene Setup dashboard card.");
            return;
        }

        string[] interfaceNames = interfaces.Select(i => i.name).ToArray();

        var fields = new List<HTTPDash.MultiFieldCard.MultiField>
        {
            HTTPDash.MultiFieldCard.MultiField.Dropdown("environment", "Environment", environmentSceneNames),
            HTTPDash.MultiFieldCard.MultiField.Dropdown("interface",   "Interface",   interfaceNames)
        };

        HTTPDash.Instance.RegisterMultiField(
            "Scene Setup", "Load", fields,
            values =>
            {
                string envName   = values.TryGetValue("environment", out var e) ? e : null;
                string ifaceName = values.TryGetValue("interface",   out var i) ? i : null;

                Debug.Log($"[SimulationManager] Scene Setup callback: env='{envName}' iface='{ifaceName}'");

                if (string.IsNullOrEmpty(envName) || string.IsNullOrEmpty(ifaceName))
                {
                    Debug.LogWarning("[SimulationManager] Scene Setup: missing environment/interface selection.");
                    return;
                }

                int envIdx = System.Array.IndexOf(environmentSceneNames, envName);
                if (envIdx < 0)
                {
                    Debug.LogWarning($"[SimulationManager] Scene Setup: env='{envName}' not found in " +
                                     $"environmentSceneNames=[{string.Join(", ", environmentSceneNames ?? System.Array.Empty<string>())}]");
                    return;
                }

                // "None" (or any name that resolves to -1) means "no interface prefab".
                // RequestEditorRestartWithSelection treats ifaceIdx == -1 as "restart env only".
                int ifaceIdx = string.Equals(ifaceName, "None", System.StringComparison.OrdinalIgnoreCase)
                               ? -1
                               : System.Array.IndexOf(interfaceNames, ifaceName);

                if (ifaceIdx < 0 && !string.Equals(ifaceName, "None", System.StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning($"[SimulationManager] Scene Setup: iface='{ifaceName}' not found in " +
                                     $"interfaceNames=[{string.Join(", ", interfaceNames ?? System.Array.Empty<string>())}]. " +
                                     "Using -1 (no interface).");
                }

                Debug.Log($"[SimulationManager] Scene Setup: resolved envIdx={envIdx} ifaceIdx={ifaceIdx}. " +
                          "Requesting editor restart.");

#if UNITY_EDITOR
                HTTPDash.Instance.SendNotification("Restarting",
                    $"Restarting with {envName} / {ifaceName}", "blue");
                RequestEditorRestartWithSelection(envIdx, ifaceIdx, null);
#else
                bool envOk   = CanLoadEnvironment(envIdx);
                bool ifaceOk = ifaceIdx >= 0 && CanLoadInterface(ifaceIdx);

                if (envOk)
                    StartCoroutine(ReloadEnvironmentSceneAsync(envIdx,
                        onDone: ifaceOk ? () => ReloadInterfaceScene(ifaceIdx) : (System.Action)null));
                else if (ifaceOk)
                    ReloadInterfaceScene(ifaceIdx);

                HTTPDash.Instance.SendNotification("Reloading", $"{envName} / {ifaceName}", "blue");
#endif
            });
    }

#if UNITY_EDITOR
    private static void RequestEditorRestartWithSelection(int envIndex, int ifaceIndex,
                                                          string[] orderedSheetLabels)
    {
        s_RestartInProgress = true;

        SessionState.SetInt(SessionKeyEnvIndex,    envIndex);
        SessionState.SetInt(SessionKeyIfaceIndex,  ifaceIndex);  // -1 = no interface
        SessionState.SetBool(SessionKeyIfaceEnabled, ifaceIndex >= 0);
        SessionState.SetBool(SessionKeyHasPending, true);

        // Store sheet labels as semicolon-delimited so TaskEnvironment can restore
        // the geometry order after Play mode restarts (no custom serialiser needed).
        if (orderedSheetLabels != null && orderedSheetLabels.Length > 0)
            SessionState.SetString("IONA_PendingGeometries", string.Join(";", orderedSheetLabels));
        else
            SessionState.SetString("IONA_PendingGeometries", "");

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
            Debug.LogWarning("SimulationManager: No camera options configured.");
            return;
        }

        HTTPDash.Instance.RegisterDropdown(
            "Camera Selection", "Select", cameraOptions,
            (string selectedCamera) =>
            {
                if (string.IsNullOrEmpty(selectedCamera)) return;
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
            Debug.LogWarning("SimulationManager: No mapping file options configured.");
            return;
        }

        string[] displayNames = mappingFileOptions.Select(m => m.displayName).ToArray();

        HTTPDash.Instance.RegisterDropdown(
            "Mapping File", "Set Active", displayNames,
            (string selectedDisplayName) =>
            {
                if (string.IsNullOrEmpty(selectedDisplayName)) return;

                MappingFileOption selected = mappingFileOptions
                    .FirstOrDefault(m => m.displayName == selectedDisplayName);

                if (selected == null || string.IsNullOrEmpty(selected.filePath))
                {
                    Debug.LogWarning($"SimulationManager: Mapping '{selectedDisplayName}' not found / empty path.");
                    return;
                }

                SafePublish("/mapping_player/file_path", new StringMsg(selected.filePath));
                HTTPDash.Instance.SendNotification("Mapping File Set",
                    $"Active mapping: {selected.displayName}", "blue");
            });
    }

    // ── Preset poses ────────────────────────────────────────────────────

    private void RegisterPresetPoseDashboardControls()
    {
        if (presetPoseOptions == null || presetPoseOptions.Count == 0)
        {
            Debug.LogWarning("SimulationManager: No preset pose options configured.");
            return;
        }

        string[] displayNames = presetPoseOptions.Select(p => p.displayName).ToArray();
        RegisterPresetPoseCard("Right Arm Preset", rightArmName, displayNames);
        RegisterPresetPoseCard("Left Arm Preset",  leftArmName,  displayNames);
    }

    private void RegisterPresetPoseCard(string cardTitle, string armName, string[] displayNames)
    {
        if (string.IsNullOrEmpty(armName)) return;

        HTTPDash.Instance.RegisterDropdown(
            cardTitle, "Execute", displayNames,
            (string selectedDisplayName) =>
            {
                if (string.IsNullOrEmpty(selectedDisplayName)) return;

                PresetPoseOption selected = presetPoseOptions
                    .FirstOrDefault(p => p.displayName == selectedDisplayName);

                if (selected == null || string.IsNullOrEmpty(selected.serviceName))
                {
                    Debug.LogWarning($"SimulationManager: Preset pose '{selectedDisplayName}' not found / empty.");
                    return;
                }

                SafePublish("/preset_pose_command", new StringMsg($"{armName}/{selected.serviceName}"));
                HTTPDash.Instance.SendNotification("Preset Pose",
                    $"{armName}: {selected.displayName}", "blue");
            });
    }

    // ── Chest height control ────────────────────────────────────────────

    private void RegisterChestHeightControl()
    {
        float savedDefault = PlayerPrefs.GetFloat(PrefKeyChestDefault, chestHeightMin);
        savedDefault = Mathf.Clamp(savedDefault, chestHeightMin, chestHeightMax);
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
                SafePublish("/chest_control/position",
                    new PositionMsg(position: (float)height,
                                   speed_fraction: chestHeightSpeedFraction));
                HTTPDash.Instance.SendNotification("Chest Height",
                    $"Moving to {height:F3} m", "blue");
            },
            saveButtonText: "Save as Default",
            saveCallback: (float height) =>
            {
                lastChestHeightSent = height;
                PlayerPrefs.SetFloat(PrefKeyChestDefault, height);
                PlayerPrefs.Save();
                HTTPDash.Instance.SendNotification("Default Saved",
                    $"Chest default set to {height:F3} m", "#1a9c4b");
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
            TaskEnvironment.instances.Remove(TaskEnvironment.instances[TaskEnvironment.currentIndex]);

        AsyncOperation unloadOp = SceneManager.UnloadSceneAsync(activeEnvironmentName);
        if (unloadOp != null)
            while (!unloadOp.isDone) yield return null;

        if (CanLoadEnvironment(0))
            yield return StartCoroutine(LoadEnvironmentSceneAsync(0));
    }

    private IEnumerator ReloadEnvironmentSceneAsync(int sceneIndex, System.Action onDone = null)
    {
        if (!string.IsNullOrEmpty(activeEnvironmentName))
        {
            if (TaskEnvironment.currentIndex >= 0 &&
                TaskEnvironment.currentIndex < TaskEnvironment.instances.Count)
                TaskEnvironment.instances.Remove(TaskEnvironment.instances[TaskEnvironment.currentIndex]);

            AsyncOperation unloadOp = SceneManager.UnloadSceneAsync(activeEnvironmentName);
            if (unloadOp != null)
                while (!unloadOp.isDone) yield return null;
        }

        yield return StartCoroutine(LoadEnvironmentSceneAsync(sceneIndex, onDone));
    }

    private void ReloadInterfaceScene(int sceneIndex)
    {
        if (!CanLoadInterface(sceneIndex)) return;
        if (activeInterface != null) Destroy(activeInterface);
        LoadInterfaceScene(sceneIndex);
    }

    public void LoadInterfaceScene(int sceneIndex)
    {
        if (!CanLoadInterface(sceneIndex)) return;
        NamedPrefab entry   = interfaces[sceneIndex];
        activeInterfaceName = entry.name;
        activeInterface     = Instantiate(entry.prefab, new Vector3(0, 100, 0), Quaternion.identity);
        DontDestroyOnLoad(activeInterface);
    }

    public void LoadEnvironmentScene(int sceneIndex) =>
        StartCoroutine(LoadEnvironmentSceneAsync(sceneIndex));

    private IEnumerator LoadEnvironmentSceneAsync(int sceneIndex, System.Action onDone = null)
    {
        Debug.Log($"SimulationManager: LoadEnvironmentSceneAsync index={sceneIndex}.");

        if (!CanLoadEnvironment(sceneIndex)) yield break;

        string sceneName = environmentSceneNames[sceneIndex];
        activeEnvironmentName = sceneName;

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
                             "Confirm it is in File → Build Settings.");
            yield break;
        }

        loadOp.allowSceneActivation = true;
        while (!loadOp.isDone) yield return null;

        Scene labScene = SceneManager.GetSceneByName(activeEnvironmentName);
        if (labScene.IsValid())
            SceneManager.SetActiveScene(labScene);
        else
            Debug.LogWarning($"SimulationManager: '{activeEnvironmentName}' loaded but scene handle invalid.");

        HTTPDash.Instance.SendNotification("Scene Loaded",
            "Loaded: " + activeEnvironmentName, "blue");

        onDone?.Invoke();
    }

    // ── Cleanup ─────────────────────────────────────────────────────────

    void OnDestroy()
    {
        if (Instance == this) Instance = null;

#if UNITY_EDITOR
        if (!s_RestartInProgress)
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChangedForRestart;
            SessionState.SetBool(SessionKeyHasPending, false);
        }
#endif

        try { if (ros != null) ros.Disconnect(); }
        catch (System.Exception e)
        { Debug.LogWarning($"SimulationManager: ROS disconnect exception: {e.Message}"); }

        if (activeInterface != null) Destroy(activeInterface);
    }

    // ── Mapping file display-name lookup (used by OrchestratorClient) ──

    /// <summary>
    /// Returns the <see cref="MappingFileOption.displayName"/> that should be active
    /// for the given environment type and interface condition, using the same lookup
    /// logic as <see cref="ApplyCondition"/>.  Returns empty string when no match
    /// is found or when the environment doesn't use mapping files.
    /// </summary>
    public string GetMappingFileDisplayName(string envType, string interfaceCondition)
    {
        if (mappingFileOptions == null || mappingFileOptions.Count == 0) return "";
        if (envType != "physical_robot") return "";   // mapping files only for embodied runs

        MappingFileOption match = null;

        // Primary: inspector interface-condition mapping table
        if (interfaceConditionMappings != null && interfaceConditionMappings.Count > 0)
        {
            var entry = interfaceConditionMappings.FirstOrDefault(m =>
                string.Equals(m.interfaceCondition, interfaceCondition,
                              System.StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                match = mappingFileOptions.FirstOrDefault(m =>
                    string.Equals(m.displayName, entry.mappingFileDisplayName,
                                  System.StringComparison.OrdinalIgnoreCase));
            }
        }

        // Fallback: substring match on displayName
        if (match == null && !string.IsNullOrEmpty(interfaceCondition))
        {
            string norm = interfaceCondition.Replace("_", " ");
            match =
                mappingFileOptions.FirstOrDefault(m =>
                    m.displayName.IndexOf(interfaceCondition,
                                          System.StringComparison.OrdinalIgnoreCase) >= 0) ??
                mappingFileOptions.FirstOrDefault(m =>
                    m.displayName.IndexOf(norm,
                                          System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        return match?.displayName ?? "";
    }
}
