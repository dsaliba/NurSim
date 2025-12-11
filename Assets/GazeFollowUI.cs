using UnityEngine;
using Tobii.Research.Unity;

public class GazeFollowUI : MonoBehaviour
{
    public float smoothSpeed = 20f;

    private RectTransform _rectTransform;
    private Canvas _canvas;

    void Awake()
    {
        _rectTransform = GetComponent<RectTransform>();
        _canvas = GetComponentInParent<Canvas>();
    }

    void Update()
    {
        var gaze = EyeTracker.Instance?.LatestGazeData;
        if (gaze == null) return;

        var left = gaze.Left;
        var right = gaze.Right;

        Vector2 displayPoint;
        if (gaze.CombinedGazeRayScreenValid)
            displayPoint = (left.GazePointOnDisplayArea + right.GazePointOnDisplayArea) * 0.5f;
        else if (left.GazePointValid)
            displayPoint = left.GazePointOnDisplayArea;
        else if (right.GazePointValid)
            displayPoint = right.GazePointOnDisplayArea;
        else
            return;

        // FIXED AXIS: flip Y
        Vector2 screenPos = new Vector2(
            displayPoint.x * Screen.width,
            (1f - displayPoint.y) * Screen.height
        );

        Vector2 canvasPos;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _canvas.transform as RectTransform,
                screenPos,
                _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera,
                out canvasPos))
        {
            Vector2 newPos = Vector2.Lerp(_rectTransform.anchoredPosition, canvasPos, Time.deltaTime * smoothSpeed);
            _rectTransform.anchoredPosition = newPos;
        }
    }
}
