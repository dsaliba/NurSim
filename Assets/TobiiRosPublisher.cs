using UnityEngine;
using RosMessageTypes.Std;
using RosMessageTypes.Geometry;
using Unity.Robotics.ROSTCPConnector;
using Tobii.Research.Unity;

public class TobiiRosPublisher : MonoBehaviour
{
    private ROSConnection ros;

    // Publish rate
    public float publishHz = 60f;
    private float timer = 0f;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();

        // -------------------------------  
        // REGISTER ALL PUBLISHERS
        // -------------------------------

        RegisterEyePublishers("left_eye");
        RegisterEyePublishers("right_eye");

        // Combined
        ros.RegisterPublisher<BoolMsg>("/tobii/combined/valid");
        ros.RegisterPublisher<PointMsg>("/tobii/combined/origin");
        ros.RegisterPublisher<Vector3Msg>("/tobii/combined/direction");
        ros.RegisterPublisher<Float32Msg>("/tobii/combined/timestamp");
    }

    // Register all topics for one eye
    private void RegisterEyePublishers(string baseTopic)
    {
        ros.RegisterPublisher<PointMsg> ($"/tobii/{baseTopic}/gaze_origin_user");
        ros.RegisterPublisher<PointMsg> ($"/tobii/{baseTopic}/gaze_origin_trackbox");
        ros.RegisterPublisher<BoolMsg>  ($"/tobii/{baseTopic}/gaze_origin_valid");

        ros.RegisterPublisher<PointMsg>   ($"/tobii/{baseTopic}/gaze_point_user");
        ros.RegisterPublisher<Point32Msg> ($"/tobii/{baseTopic}/gaze_point_screen");
        ros.RegisterPublisher<BoolMsg>    ($"/tobii/{baseTopic}/gaze_point_valid");

        ros.RegisterPublisher<Float32Msg> ($"/tobii/{baseTopic}/pupil_diameter");
        ros.RegisterPublisher<BoolMsg>    ($"/tobii/{baseTopic}/pupil_valid");

        ros.RegisterPublisher<Vector3Msg> ($"/tobii/{baseTopic}/gaze_ray_direction");
    }

    // ---------------------------------------------------------------------

    void Update()
    {
        timer += Time.deltaTime;
        if (timer < (1f / publishHz))
            return;

        timer = 0f;

        var gaze = EyeTracker.Instance?.LatestGazeData;
        if (gaze == null)
            return;

        PublishEye("left_eye", gaze.Left);
        PublishEye("right_eye", gaze.Right);

        PublishCombined(gaze);
    }

    // ---------------------------------------------------------------------
    // PER-EYE PUBLISHING
    // ---------------------------------------------------------------------
    private void PublishEye(string baseTopic, IGazeDataEye eye)
    {
        ros.Publish($"/tobii/{baseTopic}/gaze_origin_user",
            new PointMsg(
                eye.GazeOriginInUserCoordinates.x,
                eye.GazeOriginInUserCoordinates.y,
                eye.GazeOriginInUserCoordinates.z));

        ros.Publish($"/tobii/{baseTopic}/gaze_origin_trackbox",
            new PointMsg(
                eye.GazeOriginInTrackBoxCoordinates.x,
                eye.GazeOriginInTrackBoxCoordinates.y,
                eye.GazeOriginInTrackBoxCoordinates.z));

        ros.Publish($"/tobii/{baseTopic}/gaze_origin_valid",
            new BoolMsg(eye.GazeOriginValid));

        ros.Publish($"/tobii/{baseTopic}/gaze_point_user",
            new PointMsg(
                eye.GazePointInUserCoordinates.x,
                eye.GazePointInUserCoordinates.y,
                eye.GazePointInUserCoordinates.z));

        ros.Publish($"/tobii/{baseTopic}/gaze_point_screen",
            new Point32Msg(
                eye.GazePointOnDisplayArea.x,
                eye.GazePointOnDisplayArea.y,
                0f));

        ros.Publish($"/tobii/{baseTopic}/gaze_point_valid",
            new BoolMsg(eye.GazePointValid));

        ros.Publish($"/tobii/{baseTopic}/pupil_diameter",
            new Float32Msg(eye.PupilDiameter));

        ros.Publish($"/tobii/{baseTopic}/pupil_valid",
            new BoolMsg(eye.PupilDiameterValid));

        Vector3 dir = eye.GazeRayScreen.direction;
        ros.Publish($"/tobii/{baseTopic}/gaze_ray_direction",
            new Vector3Msg(dir.x, dir.y, dir.z));
    }

    // ---------------------------------------------------------------------
    // COMBINED GAZE
    // ---------------------------------------------------------------------
    private void PublishCombined(IGazeData gaze)
    {
        bool valid = gaze.CombinedGazeRayScreenValid;
        ros.Publish("/tobii/combined/valid", new BoolMsg(valid));

        if (valid)
        {
            Ray r = gaze.CombinedGazeRayScreen;

            ros.Publish("/tobii/combined/origin",
                new PointMsg(r.origin.x, r.origin.y, r.origin.z));

            ros.Publish("/tobii/combined/direction",
                new Vector3Msg(r.direction.x, r.direction.y, r.direction.z));
        }

        ros.Publish("/tobii/combined/timestamp",
            new Float32Msg(gaze.TimeStamp));
    }
}
