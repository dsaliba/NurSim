using UnityEngine;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
//Generated with Chat GPT 
public class HTTPDash : MonoBehaviour
{
    public static HTTPDash Instance { get; private set; }

    private HttpListener listener;
    private Thread serverThread;
    private bool running = false;

    private Dictionary<string, Action> buttonCallbacks = new Dictionary<string, Action>();
    private object lockObj = new object();

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Start()
    {
        listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:8080/");
        listener.Start();
        running = true;

        serverThread = new Thread(() =>
        {
            while (running)
            {
                try
                {
                    var context = listener.GetContext();
                    var request = context.Request;
                    var response = context.Response;

                    string path = request.Url.AbsolutePath;
                    if (request.HttpMethod == "GET" && path == "/")
                    {
                        // Serve dashboard HTML
                        string responseBody = GenerateDashboardHtml();
                        byte[] buffer = Encoding.UTF8.GetBytes(responseBody);
                        response.ContentLength64 = buffer.Length;
                        response.ContentType = "text/html";
                        response.OutputStream.Write(buffer, 0, buffer.Length);
                    }
                    else if (request.HttpMethod == "POST" && path.StartsWith("/action/"))
                    {
                        string actionKey = path.Substring("/action/".Length);
                        lock (lockObj)
                        {
                            if (buttonCallbacks.TryGetValue(actionKey, out var callback))
                            {
                                UnityMainThreadDispatcher.Enqueue(callback);
                            }
                        }

                        byte[] buffer = Encoding.UTF8.GetBytes("OK");
                        response.ContentLength64 = buffer.Length;
                        response.OutputStream.Write(buffer, 0, buffer.Length);
                    }

                    response.OutputStream.Close();
                }
                catch (HttpListenerException) { }
                catch (Exception ex)
                {
                    Debug.LogError($"HTTP Server Error: {ex}");
                }
            }
        });

        serverThread.IsBackground = true;
        serverThread.Start();
        Debug.Log("HTTPDash started at http://localhost:8080/");
    }

    void OnApplicationQuit()
    {
        running = false;
        if (listener != null && listener.IsListening)
        {
            listener.Stop();
            listener.Close();
        }
    }

    public void RegisterButton(string title, Action callback)
    {
        string key = Guid.NewGuid().ToString(); // Unique identifier
        lock (lockObj)
        {
            buttonCallbacks[key] = callback;
        }
        buttonTitles[key] = title;
    }

    private Dictionary<string, string> buttonTitles = new Dictionary<string, string>();

    private string GenerateDashboardHtml()
    {
        var sb = new StringBuilder();
        sb.Append("<html><body><h1>Unity Dashboard</h1>");

        lock (lockObj)
        {
            foreach (var pair in buttonTitles)
            {
                string id = pair.Key;
                string title = pair.Value;
                sb.AppendFormat("<button onclick=\"fetch('/action/{0}', {{ method: 'POST' }})\">{1}</button><br/>", id, title);
            }
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }
}
