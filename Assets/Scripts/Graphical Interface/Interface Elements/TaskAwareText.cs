using System;
using System.Collections;
using System.Collections.Generic;
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
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Length > 0)
                {
                    if (tokens[i][0] == '^')
                    {
                        int capturedIndex = i;
                        string[] subTokens = tokens[i].Substring(1).Split('@');
                        switch (subTokens[0])
                        {
                            case "s":
                                ros.Subscribe<StringMsg>(subTokens[1], message =>
                                {
                                    tokens[capturedIndex] = message.data;
                                });
                                break;
                            case "t":
                                
                                ros.Subscribe<Float64Msg>(subTokens[1], message =>
                                {
                                    double elapsed = message.data;
                                    int msTotal = (int)(elapsed * 1000);
                                    int min = msTotal / 60000;
                                    int sec = (msTotal / 1000) % 60;
                                    int ms = (msTotal % 1000)/100;
                                    tokens[capturedIndex] = $"{min:D2}:{sec:D2}:{ms:D1}";
                                });
                                break;
                            case "n":
                                string format = subTokens.Length > 2 ? subTokens[2] : "0.##";
                                ros.Subscribe<Float64Msg>(subTokens[1], message =>
                                {
                                    tokens[capturedIndex] = message.data.ToString(format);
                                });
                                break;
                        }

                    }
                }
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
        string liveString = "";
        for (int i = 0; i < tokens.Length; i++)
        {
            liveString += tokens[i] + (i < tokens.Length - 1 ? " " : "");
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
