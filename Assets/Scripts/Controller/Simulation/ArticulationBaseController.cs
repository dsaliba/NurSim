using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
///     This script sends commands to robot base wheels
///     
///     Two speed modes are available: Slow, and Regular,
///     which correspond to 0.5, 1 of the max velocity.
///     Clipping is also applied to the input.
/// </summary>
public class ArticulationBaseController : BaseController
{
    // Emergency Stop
    [SerializeField] private bool emergencyStop = false;

    // Local wheel controller
    [SerializeField] private ArticulationWheelController wheelController;

    // Extra speed limits
    // enforced by autonomy, manipulating objects, etc.
    // [linear_forward, linear_backward, angular_left, angular_right]
    private float[] speedLimit = new[] { 100f, 100f, 100f, 100f };
    // A dictionary to store all enforced speed limits
    // ID, [linear_forward, linear_backward, angular_left, angular_right]
    private Dictionary<string, float[]> speedLimitsDict = new() {};

    // void Start() {}

    void FixedUpdate()
    {
        // Emergency stop
        if (emergencyStop)
        {
            wheelController.StopWheels();
            return;
        }

        // Extra speed limit
        linearVelocity = Utils.ClampVector3(
            linearVelocity, -speedLimit[1], speedLimit[0]
        );
        angularVelocity = Utils.ClampVector3(
            angularVelocity, -speedLimit[2], speedLimit[3]
        );

        // Velocity smoothing process is done 
        // in the BaseController parent class
        wheelController.SetRobotSpeedStep(
            linearVelocity.z, 
            angularVelocity.y
        );
    }

    // Emergency Stop
    public override void EmergencyStop()
    {
        emergencyStop = true;
    }

    public override void EmergencyStopResume()
    {
        emergencyStop = false;
    }

    // Extra speed limits for the robot
    public string AddSpeedLimit(float[] speedLimits, string identifier = "")
    {
        if (identifier == "")
        {
            identifier = speedLimitsDict.Count.ToString();
        }

        // Add or set new speed limits
        if (speedLimitsDict.ContainsKey(identifier))
        {
            speedLimitsDict[identifier] = speedLimits;
        }
        else
        {
            speedLimitsDict.Add(identifier, speedLimits);
        }
        UpdateSpeedLimits();

        return identifier;
    }

    public bool RemoveSpeedLimit(string identifier)
    {
        // Remove speed limits if exists
        if (speedLimitsDict.ContainsKey(identifier))
        {
            speedLimitsDict.Remove(identifier);
            UpdateSpeedLimits();
            
            return true;
        }
        else
        {
            return false;
        }
    }

    private void UpdateSpeedLimits()
    {
        // Convert the speed limits dict to array
        float[][] speedLimits = speedLimitsDict.Values.ToArray();
        if (speedLimits.Length == 0)
        {
            speedLimit = new[] { 100f, 100f, 100f, 100f };
        }

        // Find the minimal speed limits for each direction
        speedLimits = Utils.TransposeArray(speedLimits);
        for (int i = 0; i < speedLimits.Length; i++)
        {
            speedLimit[i] = speedLimits[i].Min();
        }
    }
}
