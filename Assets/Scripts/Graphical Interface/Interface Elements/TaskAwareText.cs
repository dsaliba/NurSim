using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using RosMessageTypes.Std;
using TMPro;
using Unity.Robotics.ROSTCPConnector;
using UnityEngine;

[RequireComponent(typeof(TextMeshProUGUI))]
public class TaskAwareText : MonoBehaviour
{
    public string textFormatString;
    public float refreshRate = 0.1f;

    private TextMeshProUGUI textMeshPro;
    private bool hooked = false;
    protected ROSConnection ros;
    private List<string> segments;

    // Hold references to the callbacks to prevent garbage collection
    private List<Delegate> activeCallbacks = new();

    void Start()
    {
        textMeshPro = GetComponent<TextMeshProUGUI>();
        ros = ROSConnection.GetOrCreateInstance();
        StartTimer(refreshRate);
    }

    private bool RegisterHooks()
    {
        if (TaskEnvironment.instances.Count > TaskEnvironment.currentIndex)
        {
            string input = textFormatString;
            segments = new();

            var regex = new Regex(@"\{(\w+):\s*([^}]+)\}");
            int lastIndex = 0;
            int indexInArray = 0;

            foreach (Match match in regex.Matches(input))
            {
                if (match.Index > lastIndex)
                {
                    segments.Add(input.Substring(lastIndex, match.Index - lastIndex));
                    indexInArray++;
                }

                segments.Add(""); // Placeholder
                int currentIndex = indexInArray;

                string funcName = match.Groups[1].Value;
                string[] args = match.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.RemoveEmptyEntries);
                Debug.LogWarning($"Function: {funcName}, Index: {currentIndex}, Args: [{string.Join(", ", args)}]");

                switch (funcName)
                {
                    case "string":
                    {
                        Action<StringMsg> callback = message =>
                        {
                            segments[currentIndex] = message.data;
                        };
                        activeCallbacks.Add(callback);
                        ros.Subscribe<StringMsg>(args[0], callback);
                        break;
                    }
                    case "num":
                    {
                        string format = args.Length > 1 ? args[1] : "0.##";
                        Action<Float64Msg> callback = message =>
                        {
                            segments[currentIndex] = message.data.ToString(format);
                        };
                        activeCallbacks.Add(callback);
                        ros.Subscribe<Float64Msg>(args[0], callback);
                        break;
                    }
                    case "boolText":
                    {
                        Action<BoolMsg> callback = message =>
                        {
                            bool b = message.data;
                            segments[currentIndex] = $"<color={(b ? args[2] : args[4])}>{(b ? args[1] : args[3])}</color>";
                        };
                        activeCallbacks.Add(callback);
                        ros.Subscribe<BoolMsg>(args[0], callback);
                        break;
                    }
                    case "time":
                    {
                        Action<Float64Msg> callback = message =>
                        {
                            double elapsed = message.data;
                            int msTotal = (int)(elapsed * 1000);
                            int min = msTotal / 60000;
                            int sec = (msTotal / 1000) % 60;
                            int ms = (msTotal % 1000) / 100;
                            segments[currentIndex] = $"{min:D2}:{sec:D2}:{ms:D1}";
                        };
                        activeCallbacks.Add(callback);
                        ros.Subscribe<Float64Msg>(args[0], callback);
                        break;
                    }
                }

                lastIndex = match.Index + match.Length;
                indexInArray++;
            }

            if (lastIndex < input.Length)
            {
                segments.Add(input.Substring(lastIndex));
            }

            return true;
        }
        else
        {
            return false;
        }
    }

    void Refresh()
    {
        if (segments == null) return;

        string liveString = "";
        foreach (var s in segments)
        {
            liveString += s;
        }

        textMeshPro.text = liveString;
    }

    void Update()
    {
        if (!hooked)
            hooked = RegisterHooks();
    }

    public void StartTimer(float intervalSeconds)
    {
        StartCoroutine(TimerCoroutine(intervalSeconds, Refresh));
    }

    private IEnumerator TimerCoroutine(float interval, Action onTick)
    {
        while (true)
        {
            yield return new WaitForSeconds(interval);
            onTick?.Invoke();
        }
    }
}
