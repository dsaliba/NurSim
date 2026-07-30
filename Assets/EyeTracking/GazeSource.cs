using UnityEngine;

namespace GazeData.EyeTracking
{
    /// <summary>
    /// Common "where is the gaze looking right now" surface, shared by the real headset
    /// recorder (<see cref="ViveOpenXrGazeRecorder"/>) and <see cref="MockGazeSource"/>
    /// (mouse-driven, for testing without a headset). Scripts that only need to read the
    /// current gaze ray/hit — e.g. the Ros publishers — should reference this base type
    /// instead of a specific concrete source, so either one can be dropped into a scene
    /// interchangeably.
    /// </summary>
    public abstract class GazeSource : MonoBehaviour
    {
        /// <summary>The object the gaze raycast is currently hitting (null if no valid gaze or
        /// nothing in range).</summary>
        public GameObject CurrentGazeHitObject { get; protected set; }

        /// <summary>World-space gaze ray for this frame. Only meaningful when
        /// <see cref="HasValidGaze"/> is true.</summary>
        public Vector3 CurrentGazeWorldOrigin { get; protected set; }
        public Vector3 CurrentGazeWorldDirection { get; protected set; }
        public bool HasValidGaze { get; protected set; }
    }
}
