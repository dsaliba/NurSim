using System.Collections;
using System.Collections.Generic;
using RosMessageTypes.Power;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;
using UnityEngine.UI;

public class Battery : MonoBehaviour
{
    // Start is called before the first frame update
    public string batteryTopicName = "/battery_state";
    float maxHeight;
    float minHeight;
    public Slider slider;
    public Color defaultColor = Color.green;
    public Color chargingColor = Color.blue;
    public float min = 0.55f;
    public Image fillImage;

    private ROSConnection ros;
    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.Subscribe<BatteryStateMsg>(batteryTopicName, msg =>
        {
            fillImage.color = msg.is_charging ? chargingColor : defaultColor;
            slider.value = ((msg.charge_level-min)/(1-min));
        });
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
