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
    private string[] tokens;
    private TextMeshProUGUI textMeshPro;
    private bool hooked = false;
    protected ROSConnection ros;
    private List<string> segments;
    
    
    // Start is called before the first frame update
    void Start()
    {
        textMeshPro = GetComponent<TextMeshProUGUI>();
        tokens = textFormatString.Split(' ');
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
                // Add static text before the match
                if (match.Index > lastIndex)
                {
                    segments.Add(input.Substring(lastIndex, match.Index - lastIndex));
                    indexInArray++;
                }

                // Add empty slot for dynamic function result
                segments.Add(""); // placeholder
                int currentIndex = indexInArray;

                string funcName = match.Groups[1].Value;
                string[] args = match.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.RemoveEmptyEntries);
                Debug.LogWarning($"Function: {funcName}, Index: {currentIndex}, Args: [{string.Join(", ", args)}]");
                switch (funcName)
                {
                    case "string":
                        ros.Subscribe<StringMsg>(args[0], message =>
                    {
                        segments[currentIndex] = message.data;
                    });
                        break;
                    case "num":
                        string format = args.Length > 1 ? args[1] : "0.##";
                    ros.Subscribe<Float64Msg>(args[0], message =>
                    {
                        segments[currentIndex] = message.data.ToString(format);
                    });
                        break;
                        case "boolText":
                        ros.Subscribe<BoolMsg>(args[0], message =>
                        {
                            bool b = message.data;
                            segments[currentIndex] = $"<color={(b?args[2] : args[4])}>{(b?args[1] : args[3])}</color>";
                        });
                        break;
                        case "time":
                        ros.Subscribe<Float64Msg>(args[0], message =>
                        {
                            double elapsed = message.data;
                            int msTotal = (int)(elapsed * 1000);
                            int min = msTotal / 60000;
                            int sec = (msTotal / 1000) % 60;
                            int ms = (msTotal % 1000)/100;
                            segments[currentIndex] = $"{min:D2}:{sec:D2}:{ms:D1}";
                        });
                    break;
                }

                lastIndex = match.Index + match.Length;
                indexInArray++;
            }

            // Add any trailing text
            if (lastIndex < input.Length)
            {
                segments.Add(input.Substring(lastIndex));
            }
            // for (int i = 0; i < tokens.Length; i++)
            // {
            //     if (tokens[i].Length > 0)
            //     {
            //         if (tokens[i][0] == '^')
            //         {
            //             int capturedIndex = i;
            //             string[] subTokens = tokens[i].Substring(1).Split('@');
            //             switch (subTokens[0])
            //             {
            //                 case "s":
            //                     ros.Subscribe<StringMsg>(subTokens[1], message =>
            //                     {
            //                         tokens[capturedIndex] = message.data;
            //                     });
            //                     break;
            //                 case "t":
            //                     
            //                     ros.Subscribe<Float64Msg>(subTokens[1], message =>
            //                     {
            //                         double elapsed = message.data;
            //                         int msTotal = (int)(elapsed * 1000);
            //                         int min = msTotal / 60000;
            //                         int sec = (msTotal / 1000) % 60;
            //                         int ms = (msTotal % 1000)/100;
            //                         tokens[capturedIndex] = $"{min:D2}:{sec:D2}:{ms:D1}";
            //                     });
            //                     break;
            //                 case "n":
            //                     string format = subTokens.Length > 2 ? subTokens[2] : "0.##";
            //                     ros.Subscribe<Float64Msg>(subTokens[1], message =>
            //                     {
            //                         tokens[capturedIndex] = message.data.ToString(format);
            //                     });
            //                     break;
            //             }
            //
            //         }
            //     }
            // }

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
            liveString+=s;
        }
        textMeshPro.text = liveString;
    }

    // Update is called once per frame
    void Update()
    {
        if (!hooked) hooked = RegisterHooks();
    }
    
    public void StartTimer(float intervalSeconds)
    {
        StartCoroutine(TimerCoroutine(intervalSeconds, () =>
        {
            Refresh();
        }));
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
