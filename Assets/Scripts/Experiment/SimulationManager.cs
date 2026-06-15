using System.Collections;
using System.Collections.Generic;
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

    [SerializeField]
    public string unitySystemIP = "127.0.0.1";
    public string[] environmentSceneNames;
    public NamedPrefab[] interfaces;

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
        ros.RegisterPublisher<StringMsg>("/task/error");
        ros.RegisterPublisher<BoolMsg>("/task/end");

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

        ros.RegisterPublisher<BoolMsg>("/ui/show_hints");
        HTTPDash.Instance.RegisterButton("End Task", "End",
            s => ros.Publish("/task/end", new BoolMsg(true)));
        HTTPDash.Instance.RegisterButton("Bedhead Collision", "Mark", (string s) =>
        {
            ros.Publish("/task/error", new StringMsg("bedhead_collision"));
            HTTPDash.Instance.SendNotification("Error Logged", "Bedhead Collision", "orange");
        });
        HTTPDash.Instance.RegisterButton("Unplug Fail", "Mark", (string s) =>
        {
            ros.Publish("/task/error", new StringMsg("unplug_fail"));
            HTTPDash.Instance.SendNotification("Error Logged", "Unplug Fail", "orange");
        });
        HTTPDash.Instance.RegisterButton("Plug Fail", "Mark", (string s) =>
        {
            ros.Publish("/task/error", new StringMsg("plug_fail"));
            HTTPDash.Instance.SendNotification("Error Logged", "Plug Fail", "orange");
        });
        HTTPDash.Instance.RegisterButton("Environment Collision", "Mark", (string s) =>
        {
            ros.Publish("/task/error", new StringMsg("environment_collision"));
            HTTPDash.Instance.SendNotification("Error Logged", "Environment Collision", "orange");
        });
        HTTPDash.Instance.RegisterButton("Pick Fail", "Mark", (string s) =>
        {
            ros.Publish("/task/error", new StringMsg("pick_fail"));
            HTTPDash.Instance.SendNotification("Error Logged", "Pick Fail", "orange");
        });
        HTTPDash.Instance.RegisterButton("Place Fail", "Mark", (string s) =>
        {
            ros.Publish("/task/error", new StringMsg("place_fail"));
            HTTPDash.Instance.SendNotification("Error Logged", "Place Fail", "orange");
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
