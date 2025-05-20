using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

[RequireComponent(typeof(TextMeshProUGUI))]
public class TaskAwareText : MonoBehaviour
{
    public string textFormatString;
    private string[] tokens;
    private TextMeshProUGUI textMeshPro;
    private bool hooked = false;
    
    
    // Start is called before the first frame update
    void Start()
    {
        textMeshPro = GetComponent<TextMeshProUGUI>();
        tokens = textFormatString.Split(' ');
        
        
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
                        Trial.TrialEvent e = TaskEnvironment.instances[TaskEnvironment.currentIndex].trial
                            .GetLastEvent(tokens[capturedIndex].Substring(1));
                        
                        TaskEnvironment.instances[TaskEnvironment.currentIndex].trial.RegisterSubscriber(tokens[capturedIndex].Substring(1), @event =>
                        {
                            tokens[capturedIndex] = @event.data;
                            Refresh();
                        });
                        if (e != null)
                        {
                            tokens[capturedIndex] = e.data;
                            Refresh();
                        }
                    }else if (tokens[i][0] == '@')
                    {
                        if (tokens[i][1] == 't')
                        {
                            int capturedIndex = i;
                            string[] subTokens = tokens[capturedIndex].Substring(2).Split('/');
                            double duration = Double.Parse(subTokens[0]);
                            string capturedName = subTokens[1];
                            double timerStart = 0;
                            StartTimer(capturedIndex, timerStart, (float)duration, () =>
                            {
                                double elapsed = Array
                                    .Find(TaskEnvironment.instances[TaskEnvironment.currentIndex].trial.liveNumbers,
                                        l => l.name.Equals(capturedName)).value;
                                int msTotal = (int)(elapsed * 1000);
                                int min = msTotal / 60000;
                                int sec = (msTotal / 1000) % 60;
                                int ms = (msTotal % 1000)/100;
                                return $"{min:D2}:{sec:D2}:{ms:D1}";
                            });
                        } else if (tokens[i][1] == 'n')
                        {
                            int capturedIndex = i;
                            string[] subTokens = tokens[capturedIndex].Substring(2).Split('/');
                            double duration = Double.Parse(subTokens[0]);
                            string capturedName = subTokens[1];
                            double timerStart = 0;
                            string format = subTokens.Length > 2 ? subTokens[2] : "0.##";
                            StartTimer(capturedIndex, timerStart, (float)duration, () =>
                            {
                                double value = Array
                                    .Find(TaskEnvironment.instances[TaskEnvironment.currentIndex].trial.liveNumbers,
                                        l => l.name.Equals(capturedName)).value;
                                return value.ToString(format);
                            });
                            
                            
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
    
    public void StartTimer(int tokenIndex, double startTime, float intervalSeconds, Func<string> tokenSource)
    {
        StartCoroutine(TimerCoroutine(intervalSeconds, () =>
        {
            tokens[tokenIndex] = tokenSource();
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
