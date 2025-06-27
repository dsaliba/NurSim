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
    
    // Start is called before the first frame update
    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<StringMsg>("unity/ip", latch: true);
        ros.RegisterPublisher<StringMsg>("trial/dash");
        ros.RegisterPublisher<BoolMsg>("/haptic/eject");
        
        if (loadOnStart)
        {
            LoadEnvironmentScene(0);
            LoadInterfaceScene(0);
        }
        // HTTPDash.Instance.RegisterButton("Reset", () =>
        // {
        //     Debug.Log("Reset button clicked from HTTP!");
        //     ResetCurrentEnvironment();
        //     Destroy(activeInterface);
        //     LoadInterfaceScene(0);
        // });
        HTTPDash.Instance.RegisterButton("Task Reset", "Reset", s =>
        {
            Debug.Log("Reset button clicked from HTTP!");
            ResetCurrentEnvironment();
            Destroy(activeInterface);
            LoadInterfaceScene(0);
        });
        HTTPDash.Instance.RegisterButton("Haptics", "Eject", s =>
        {
            ros.Publish("/haptic/eject", new BoolMsg(true));
        });
        ros.Subscribe<BoolMsg>("/haptic/latched", msg =>
        {
            if (msg.data != lastLatch)
            {
                HTTPDash.Instance.SendNotification("Haptics", $"Device {(msg.data ? "latched":"unlatched")}", msg.data ? "blue" : "red");
            }
            lastLatch = msg.data;
        });
        ros.RegisterPublisher<BoolMsg>("/ui/show_hints");
        HTTPDash.Instance.RegisterDropdown("Show Hints", "Update", new string[] { "On", "Off"}, s => ros.Publish("/ui/show_hints", new BoolMsg(s.Equals("On"))));
        HTTPDash.Instance.RegisterDropdown("Test Dropdown", "Press Me Too", new string[]{"Dimitri", "Nikita", "Lorena"}, s => Debug.LogWarning(s));
        HTTPDash.Instance.RegisterInput("Test Input", "Press Me Last", "Write here", s => Debug.LogWarning(s));
        HTTPDash.Instance.RegisterInput("Dash Message", "Send", "Write here", s => ros.Publish("trial/dash", new StringMsg(s)));
        HTTPDash.Instance.RegisterDropdown("Medicines", "Press Me Too", new string[]{"Advil", "peroxide", "D3"}, s =>
        {

            Debug.LogWarning("Medicine selected" + s);
        });
        
        HTTPDash.Instance.RegisterButton("Count", "Increment", s =>
        {
            Debug.LogWarning("Count " +  count++);
            
        });
        
    }
    
    private int count = 0;
    
    public void ResetCurrentEnvironment()
    {
        StartCoroutine(ResetEnvironmentCoroutine());
    }

    private IEnumerator ResetEnvironmentCoroutine()
    {
        TaskEnvironment.instances.Remove(TaskEnvironment.instances[TaskEnvironment.currentIndex]);
        AsyncOperation unloadOp = SceneManager.UnloadSceneAsync(activeEnvironmentName);
        if (unloadOp != null)
        {
            while (!unloadOp.isDone)
                yield return null;
        }

        LoadEnvironmentScene(0);
    }

    public void LoadInterfaceScene(int sceneIndex)
    {
        this.activeInterfaceName = interfaces[sceneIndex].name;
        this.activeInterface = Instantiate(interfaces[sceneIndex].prefab, new Vector3(0, 100, 0), Quaternion.identity);
    }

    public void LoadEnvironmentScene(int sceneIndex)
    {
        this.activeEnvironmentName = environmentSceneNames[sceneIndex];
        for (int i = 0; i < TaskEnvironment.instances.Count; i++)
        {
            if (this.activeEnvironmentName.Equals(TaskEnvironment.instances[i]))
            {
                TaskEnvironment.currentIndex = i;
                break;
            }
        }
        HTTPDash.Instance.SendNotification("Scene Loaded", "Loaded scene: " + activeEnvironmentName, "blue");
        SceneManager.LoadScene(activeEnvironmentName, LoadSceneMode.Additive);
    } 
    

    // Update is called once per frame
    void Update()
    {
        if (firstUpdate)
        {
            ros.Publish("unity/ip", new StringMsg(unitySystemIP));
            firstUpdate = false;
        }
    }
}
