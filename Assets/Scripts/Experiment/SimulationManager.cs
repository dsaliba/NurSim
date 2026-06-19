using System.Collections;
using System.Collections.Generic;
using System.Linq;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        public string displayName;   // Label shown in the dashboard dropdown
        public string filePath;      // Value published to /mapping_player/file_path
    }

    [SerializeField]
    public string unitySystemIP = "127.0.0.1";
    public string[] environmentSceneNames;
    public NamedPrefab[] interfaces;

    [Header("Camera Selection")]
    public string[] cameraOptions = new string[] { "Front", "Back", "Side" };

    [Header("Mapping Files")]
    public List<MappingFileOption> mappingFileOptions = new List<MappingFileOption>();

    public bool loadOnStart = true;

    string activeEnvironmentName;
    string activeInterfaceName;

    private ROSConnection ros;
    private GameObject activeInterface;
    private bool firstUpdate = true;
    bool lastLatch = false;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<StringMsg>("unity/ip", latch: true);
        ros.RegisterPublisher<StringMsg>("trial/dash");
        ros.RegisterPublisher<BoolMsg>("/task/end");
        ros.RegisterPublisher<StringMsg>("/unity/camera_selection");
        ros.RegisterPublisher<StringMsg>("/mapping_player/file_path");

        if (loadOnStart)
        {
            // Load environment first; interface loads after so OVRCameraRig
            // is already present when the interface prefab initialises.
            StartCoroutine(LoadEnvironmentSceneAsync(0, onDone: () =>
            {
                LoadInterfaceScene(0);
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

        RegisterEnvironmentDashboardControls();
        RegisterInterfaceDashboardControls();
        RegisterCameraDashboardControls();
        RegisterMappingFileDashboardControls();
    }

    // ── Environment scene reinitialize/load dropdown ───────────────────────
    private void RegisterEnvironmentDashboardControls()
    {
        if (environmentSceneNames == null || environmentSceneNames.Length == 0)
        {
            Debug.LogWarning("SimulationManager: No environment scene names configured for dashboard.");
            return;
        }

        HTTPDash.Instance.RegisterDropdown(
            "Environment Scene",
            "Load / Reinitialize",
            environmentSceneNames,
            (string selectedSceneName) =>
            {
                int index = System.Array.IndexOf(environmentSceneNames, selectedSceneName);
                if (index < 0)
                {
                    Debug.LogWarning($"SimulationManager: Unknown environment scene '{selectedSceneName}' from dashboard.");
                    return;
                }

                HTTPDash.Instance.SendNotification("Environment Loading",
                    $"Reinitializing scene: {selectedSceneName}", "blue");

                StartCoroutine(ReloadEnvironmentSceneAsync(index));
            });
    }

    /// <summary>
    /// Unloads the currently active environment scene (if any) and loads the
    /// requested one, mirroring ResetEnvironmentCoroutine but for an
    /// arbitrary target index chosen from the dashboard.
    /// </summary>
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

    // ── Interface reinitialize/load dropdown ────────────────────────────────
    private void RegisterInterfaceDashboardControls()
    {
        if (interfaces == null || interfaces.Length == 0)
        {
            Debug.LogWarning("SimulationManager: No interfaces configured for dashboard.");
            return;
        }

        string[] interfaceNames = interfaces.Select(i => i.name).ToArray();

        HTTPDash.Instance.RegisterDropdown(
            "Interface",
            "Load / Reinitialize",
            interfaceNames,
            (string selectedInterfaceName) =>
            {
                int index = System.Array.IndexOf(interfaceNames, selectedInterfaceName);
                if (index < 0)
                {
                    Debug.LogWarning($"SimulationManager: Unknown interface '{selectedInterfaceName}' from dashboard.");
                    return;
                }

                HTTPDash.Instance.SendNotification("Interface Loading",
                    $"Reinitializing interface: {selectedInterfaceName}", "blue");

                ReloadInterfaceScene(index);
            });
    }

    /// <summary>
    /// Destroys the currently active interface prefab instance (if any) and
    /// instantiates the requested one in its place.
    /// </summary>
    private void ReloadInterfaceScene(int sceneIndex)
    {
        if (activeInterface != null)
        {
            Destroy(activeInterface);
        }

        LoadInterfaceScene(sceneIndex);
    }

    // ── Camera selection dropdown ────────────────────────────────────────
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

    // ── Mapping file toggle dropdown ────────────────────────────────────────
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

    public void ResetCurrentEnvironment()
    {
        StartCoroutine(ResetEnvironmentCoroutine());
    }

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

    public void LoadInterfaceScene(int sceneIndex)
    {
        activeInterfaceName = interfaces[sceneIndex].name;
        activeInterface = Instantiate(interfaces[sceneIndex].prefab,
                                      new Vector3(0, 100, 0), Quaternion.identity);

        // Keep the interface prefab alive across scene reloads.
        DontDestroyOnLoad(activeInterface);
    }

    /// <summary>
    /// Loads the environment scene additively, then sets it as the active scene
    /// so that OVRCameraRig / XR tracking binds to it correctly.
    /// </summary>
    public void LoadEnvironmentScene(int sceneIndex)
    {
        StartCoroutine(LoadEnvironmentSceneAsync(sceneIndex));
    }

    private IEnumerator LoadEnvironmentSceneAsync(int sceneIndex, System.Action onDone = null)
    {
        activeEnvironmentName = environmentSceneNames[sceneIndex];

        // Track which TaskEnvironment instance this maps to.
        for (int i = 0; i < TaskEnvironment.instances.Count; i++)
        {
            if (activeEnvironmentName.Equals(TaskEnvironment.instances[i].sceneName))
            {
                TaskEnvironment.currentIndex = i;
                break;
            }
        }

        // Load additively and wait for completion before setting active scene.
        AsyncOperation loadOp = SceneManager.LoadSceneAsync(
            activeEnvironmentName, LoadSceneMode.Additive);
        loadOp.allowSceneActivation = true;

        while (!loadOp.isDone)
            yield return null;

        // ── KEY FIX ──────────────────────────────────────────────────────────
        // Set the lab scene as the active scene. This ensures:
        //   1. OVRManager (on OVRCameraRig) is in the active scene, so the
        //      XR runtime binds head tracking and stereo rendering to it.
        //   2. New GameObjects spawned at runtime are created in this scene.
        Scene labScene = SceneManager.GetSceneByName(activeEnvironmentName);
        if (labScene.IsValid())
            SceneManager.SetActiveScene(labScene);
        // ─────────────────────────────────────────────────────────────────────

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
