using System;
using System.IO;
using UnityEngine;

namespace GazeData.EyeTracking
{
    /// <summary>
    /// Minimal standalone gaze-recording session: no controllers, no Fitts task, just
    /// start/stop logging raw gaze data to CSV. Press R to start, R again to stop.
    /// Use this to collect pilot data (e.g. for fixation/saccade feature extraction) without
    /// needing the controller interaction pipeline set up.
    /// </summary>
    public class SimpleGazeSession : MonoBehaviour
    {
        [SerializeField] private ViveOpenXrGazeRecorder gazeRecorder;
        [SerializeField] private string participantId = "P01";
        [Tooltip("Root folder (relative to Application.persistentDataPath) where CSVs are written.")]
        [SerializeField] private string outputSubfolder = "GazeData";

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.R))
            {
                if (gazeRecorder.IsRecording)
                {
                    gazeRecorder.StopRecording();
                    Debug.Log("[SimpleGazeSession] Stopped recording.");
                }
                else
                {
                    string sessionId = $"{participantId}_{DateTime.Now:yyyyMMdd_HHmmss}";
                    string sessionDir = Path.Combine(Application.persistentDataPath, outputSubfolder);
                    Directory.CreateDirectory(sessionDir);
                    gazeRecorder.StartRecording(sessionDir, sessionId);
                    Debug.Log($"[SimpleGazeSession] Started recording '{sessionId}'. Writing to: {sessionDir}");
                }
            }
        }
    }
}
