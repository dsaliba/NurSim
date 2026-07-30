using System.Collections;
using System.Collections.Generic;
using System.Linq;
using RosMessageTypes.Std;
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

    public bool loadOnStart = true;

    string activeEnvironmentName;
    string activeInterfaceName;

    private ROSConnection ros;
    private GameObject activeInterface;
    private bool firstUpdate = true;
    bool lastLatch = false;

    private const string SessionKeyHasPending = "IONA_PendingSceneRestart";
    private const string SessionKeyEnv = "IONA_PendingEnv";
    private const string SessionKeyIface = "IONA_PendingIface";

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<StringMsg>("unity/ip", latch: true);
        ros.RegisterPublisher<StringMsg>("trial/dash");
        ros.RegisterPublisher<BoolMsg>("/task/end");
        ros.RegisterPublisher<StringMsg>("/unity/camera_selection");
        ros.RegisterPublisher<StringMsg>("/mapping_player/file_path");
        ros.RegisterPublisher<StringMsg>("/preset_pose_command");

        if (loadOnStart)
        {
            int envIndex = 0;
            int ifaceIndex = 0;

#if UNITY_EDITOR
            if (SessionState.GetBool(SessionKeyHasPending, false))
            {
                SessionState.SetBool(SessionKeyHasPending, false);
                string pendingEnv = SessionState.GetString(SessionKeyEnv, null);
                string pendingIface = SessionState.GetString(SessionKeyIface, null);

                int foundEnv = System.Array.IndexOf(environmentSceneNames, pendingEnv);
                int foundIface = System.Array.IndexOf(interfaces.Select(i => i.name).ToArray(), pendingIface);

                if (foundEnv >= 0) envIndex = foundEnv;
                if (foundIface >= 0) ifaceIndex = foundIface;
            }
#endif

            StartCoroutine(LoadEnvironmentSceneAsync(envIndex, onDone: () =>
            {
                LoadInterfaceScene(ifaceIndex);
            }));
        }

        ros.Subscribe<BoolMsg>("/haptic/latched", msg =>
        {
            if (msg.data != lastLatch)
                HTTPDash.Instance.SendNotification("Haptics",
                    $"Device {(msg.data ? "latched" : "unlatched")}",
                    msg.data ? "blue" : "red");
            lastLatch = msg.data;
        });

        HTTPDash.Instance.RegisterButton("End Task", "End",
            s => ros.Publish("/task/end", new BoolMsg(true)));

        RegisterSceneAndInterfaceDashboardControl();
        RegisterCameraDashboardControls();
        RegisterMappingFileDashboardControls();
        RegisterPresetPoseDashboardControls();
    }

    // ── Combined environment + interface load card ─────────────────────
    private void RegisterSceneAndInterfaceDashboardControl()
    {
        if (environmentSceneNames == null || environmentSceneNames.Length == 0 ||
            interfaces == null || interfaces.Length == 0)
        {
            Debug.LogWarning("SimulationManager: Need at least one environment and one interface configured for dashboard.");
            return;
        }

        string[] interfaceNames = interfaces.Select(i => i.name).ToArray();

        var fields = new List<HTTPDash.MultiFieldCard.MultiField>
        {
            HTTPDash.MultiFieldCard.MultiField.Dropdown("environment", "Environment", environmentSceneNames),
            HTTPDash.MultiFieldCard.MultiField.Dropdown("interface", "Interface", interfaceNames)
        };

        HTTPDash.Instance.RegisterMultiField(
            "Scene Setup",
            "Load",
            fields,
            values =>
            {
                string envName = values.TryGetValue("environment", out var e) ? e : null;
                string ifaceName = values.TryGetValue("interface", out var i) ? i : null;

                if (string.IsNullOrEmpty(envName) || string.IsNullOrEmpty(ifaceName))
                {
                    Debug.LogWarning("SimulationManager: Missing environment/interface selection from dashboard.");
                    return;
                }

#if UNITY_EDITOR
                HTTPDash.Instance.SendNotification("Restarting",
                    $"Restarting Play Mode with {envName} / {ifaceName}", "blue");
                RequestEditorRestartWithSelection(envName, ifaceName);
#else
                HTTPDash.Instance.SendNotification("Reloading",
                    $"Reloading in place (no editor restart in builds): {envName} / {ifaceName}", "blue");
                int envIdx = System.Array.IndexOf(environmentSceneNames, envName);
                int ifaceIdx = System.Array.IndexOf(interfaceNames, ifaceName);
                if (envIdx >= 0) StartCoroutine(ReloadEnvironmentSceneAsync(envIdx));
                if (ifaceIdx >= 0) ReloadInterfaceScene(ifaceIdx);
#endif
            });
    }

#if UNITY_EDITOR
    private static void RequestEditorRestartWithSelection(string envName, string ifaceName)
    {
        SessionState.SetString(SessionKeyEnv, envName);
        SessionState.SetString(SessionKeyIface, ifaceName);
        SessionState.SetBool(SessionKeyHasPending, true);

        EditorApplication.playModeStateChanged -= OnPlayModeStateChangedForRestart;
        EditorApplication.playModeStateChanged += OnPlayModeStateChangedForRestart;
        EditorApplication.isPlaying = false;
    }

    private static void OnPlayModeStateChangedForRestart(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChangedForRestart;
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
                ros.Publish("/unity/camera_selection", new StringMsg(selectedCamera));
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
                MappingFileOption selected = mappingFileOptions
                    .FirstOrDefault(m => m.displayName == selectedDisplayName);

                if (selected == null)
                {
                    Debug.LogWarning($"SimulationManager: Unknown mapping file option '{selectedDisplayName}' from dashboard.");
                    return;
                }

                ros.Publish("/mapping_player/file_path", new StringMsg(selected.filePath));
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
        HTTPDash.Instance.RegisterDropdown(
            cardTitle,
            "Execute",
            displayNames,
            (string selectedDisplayName) =>
            {
                PresetPoseOption selected = presetPoseOptions
                    .FirstOrDefault(p => p.displayName == selectedDisplayName);

                if (selected == null)
                {
                    Debug.LogWarning($"SimulationManager: Unknown preset pose '{selectedDisplayName}'.");
                    return;
                }

                ros.Publish("/preset_pose_command",
                    new StringMsg($"{armName}/{selected.serviceName}"));

                HTTPDash.Instance.SendNotification("Preset Pose",
                    $"{armName}: {selected.displayName}", "blue");
            });
    }

    // ── Scene / environment helpers ─────────────────────────────────────
    public void ResetCurrentEnvironment() => StartCoroutine(ResetEnvironmentCoroutine());

    private IEnumerator ResetEnvironmentCoroutine()
    {
        TaskEnvironment.instances.Remove(
            TaskEnvironment.instances[TaskEnvironment.currentIndex]);

        AsyncOperation unloadOp = SceneManager.UnloadSceneAsync(activeEnvironmentName);
        if (unloadOp != null)
            while (!unloadOp.isDone)
                yield return null;

        yield return StartCoroutine(LoadEnvironmentSceneAsync(0));
    }

    private IEnumerator ReloadEnvironmentSceneAsync(int sceneIndex)
    {
        if (!string.IsNullOrEmpty(activeEnvironmentName))
        {
            if (TaskEnvironment.instances.Count > TaskEnvironment.currentIndex &&
                TaskEnvironment.currentIndex >= 0)
            {
                TaskEnvironment.instances.Remove(
                    TaskEnvironment.instances[TaskEnvironment.currentIndex]);
            }

            AsyncOperation unloadOp = SceneManager.UnloadSceneAsync(activeEnvironmentName);
            if (unloadOp != null)
                while (!unloadOp.isDone)
                    yield return null;
        }

        yield return StartCoroutine(LoadEnvironmentSceneAsync(sceneIndex));
    }

    private void ReloadInterfaceScene(int sceneIndex)
    {
        if (activeInterface != null)
            Destroy(activeInterface);

        LoadInterfaceScene(sceneIndex);
    }

    public void LoadInterfaceScene(int sceneIndex)
    {
        activeInterfaceName = interfaces[sceneIndex].name;
        activeInterface = Instantiate(interfaces[sceneIndex].prefab,
                                      new Vector3(0, 100, 0), Quaternion.identity);
        DontDestroyOnLoad(activeInterface);
    }

    public void LoadEnvironmentScene(int sceneIndex) => StartCoroutine(LoadEnvironmentSceneAsync(sceneIndex));

    private IEnumerator LoadEnvironmentSceneAsync(int sceneIndex, System.Action onDone = null)
    {
        activeEnvironmentName = environmentSceneNames[sceneIndex];

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
        loadOp.allowSceneActivation = true;

        while (!loadOp.isDone)
            yield return null;

        Scene labScene = SceneManager.GetSceneByName(activeEnvironmentName);
        if (labScene.IsValid())
            SceneManager.SetActiveScene(labScene);

        HTTPDash.Instance.SendNotification("Scene Loaded",
            "Loaded scene: " + activeEnvironmentName, "blue");

        onDone?.Invoke();
    }

    void Update()
    {
        if (firstUpdate)
        {
            ros.Publish("unity/ip", new StringMsg(unitySystemIP));
            firstUpdate = false;
        }
    }
}
