using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
///     A simple collision avoidance module
///
///     Slow down robot if obstacles are close in front
///     The speed limit would be set to distance * slowMultiplier
///
///     Stop the robot if the obstacle is really close
/// </summary>
public class CollisionAvoidance : MonoBehaviour
{
    // Robot components
    [SerializeField] private Laser laser;
    [SerializeField] private ArticulationBaseController baseController;
    
    // Detection parameters
    // laser
    [SerializeField] private float detectionAngleMin = -10f * Mathf.Deg2Rad;
    [SerializeField] private float detectionAngleMax =  10f * Mathf.Deg2Rad;
    private int minIndex = -1;
    private int maxIndex = -1;
    // base controller
    [SerializeField] private float slowThreshold = 1.0f;
    [SerializeField] private float slowMultiplier = 0.5f;
    [SerializeField] private float stopDistance = 0.3f;
    [SerializeField] private string wheelSpeedLimitID = "collision avoidance limit";

    void Start()
    {
        // Get the index of the given range
        // to search for obstacles in laser scan
        var (updateRate, samples, angleMin, angleMax, rangeMin, rangeMax) = 
            laser.GetLaserScanParameters();
        float angleIncrement = (angleMax - angleMin) / (samples - 1);
        for (int i = 0; i < samples; ++i)
        {
            float angle = angleMin + i * angleIncrement;
            if (minIndex == -1 && angle > detectionAngleMin)
                minIndex = i;
            if (maxIndex == -1 && angle > detectionAngleMax)
                maxIndex = i;
        }
        if (minIndex == -1 || maxIndex == -1)
        {
            Debug.LogWarning(
                "The provided detection range does not" +
                "fall into the laser detection range."
            );
        }

        // Start updating speed limit based on the laser scan result
        InvokeRepeating("UpdateSpeedLimit", 1.0f, 1f / updateRate);
    }

    private void UpdateSpeedLimit()
    {
        // Get the minimal distance to the obstacles forward
        float minDistance = GetMinDistanceToObstacle();
        
        // Only forward linear speed limit
        float speedLimit;
        if (minDistance < stopDistance)
        {   
            speedLimit = 0f;
        }
        else if (minDistance < slowThreshold)
        {
            speedLimit = minDistance * slowMultiplier;
        }
        else  // minDistance >= slowThreshold
        {
            speedLimit = 100f;
        }
        baseController.AddSpeedLimit(
            new float[] {speedLimit, 100f, 100f, 100f},
            wheelSpeedLimitID
        );
    }

    private float GetMinDistanceToObstacle()
    {
        // Get the minimal distance to the obstacles forward
        float minDistance = 100f;
        for (int i = minIndex; i < maxIndex; ++i)
        {
            if (laser.ObstacleRanges[i] != 0 &&
                laser.ObstacleRanges[i] < minDistance)
            {
                minDistance = laser.ObstacleRanges[i];
            }
        }
        return minDistance;
    }
}
