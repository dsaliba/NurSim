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
    private Queue<HttpListenerContext> waitingClients = new Queue<HttpListenerContext>();
    private object notifyLock = new object();
    public string localIP = "localhost";

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
        listener.Prefixes.Add($"http://{localIP}:8080/");
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
                        int acID = int.Parse(actionKey);
                        foreach (HTMLDashCard card in cards)
                        {
                            if (card.id == acID)
                            {
                                using (System.IO.Stream body = request.InputStream) 
                                {
                                    using (var reader = new System.IO.StreamReader(body, request.ContentEncoding))
                                    {
                                        string content =  reader.ReadToEnd();
                                        
                                        UnityMainThreadDispatcher.Enqueue(card.callback, content);
                                    }
                                }
                                
                            }
                        }
                        byte[] buffer = Encoding.UTF8.GetBytes("OK");
                        response.ContentLength64 = buffer.Length;
                        response.OutputStream.Write(buffer, 0, buffer.Length);
                    }
                    else if (request.HttpMethod == "GET" && path == "/wait-for-message")
                    {
                        lock (notifyLock)
                        {
                            waitingClients.Enqueue(context);
                        }
                        continue;
                    }
                    
                    if (response.OutputStream.CanWrite)
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
    
    public void SendNotification(string title, string body, string color)
    {
        string json = $"{{\"title\":\"{title}\",\"body\":\"{body}\",\"color\":\"{color}\"}}";
        byte[] buffer = Encoding.UTF8.GetBytes(json);

        lock (notifyLock)
        {
            while (waitingClients.Count > 0)
            {
                try
                {
                    var client = waitingClients.Dequeue();
                    client.Response.ContentType = "application/json";
                    client.Response.ContentLength64 = buffer.Length;
                    client.Response.OutputStream.Write(buffer, 0, buffer.Length);
                    client.Response.Close();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Error sending notification: {e.Message}");
                }
            }
        }
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

    [System.Serializable]
    public abstract class HTMLDashCard
    {
        public static int nextID = 0;
        public int id;
        public Action<String> callback;
        public abstract string AsEntry();
    }

    [System.Serializable]
    public class ButtonCard : HTMLDashCard
    {
        public string title;
        public string buttonText;
        
        

        public ButtonCard(string title, string buttonText, Action<String> callback)
        {
            this.id = HTMLDashCard.nextID++;
            this.title = title;
            this.buttonText = buttonText;
            this.callback = callback;
        }

        public override string AsEntry()
        {
            return
                $"{{ type: \"button\", id: {this.id}, title: \"{this.title}\", buttonText: \"{this.buttonText}\"}},";
        }
    }
    
    [System.Serializable]
    public class InputCard : HTMLDashCard
    {
        
        public string title;
        public string buttonText;
        public string placeHolder;

        public InputCard(string title, string buttonText, string placeHolder, Action<String> callback)
        {
            this.id = HTMLDashCard.nextID++;
            this.title = title;
            this.buttonText = buttonText;
            this.callback = callback;
            this.placeHolder = placeHolder;
        }

        public override string AsEntry()
        {
            return
                $"{{ type: \"input\", id: {this.id}, title: \"{this.title}\", buttonText: \"{this.buttonText}\", placeHolder: \"{this.placeHolder}\"}},";
        }
    }
    
    [System.Serializable]
    public class DropdownCard : HTMLDashCard
    {
        public string title;
        public string buttonText;
        public string[] options;

        public DropdownCard(string title, string buttonText, string[] options, Action<String> callback)
        {
            this.id = HTMLDashCard.nextID++;
            this.title = title;
            this.buttonText = buttonText;
            this.callback = callback;
            this.options = options;

        }

        public override string AsEntry()
        {
            string[] optionsWrapped = new string[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                optionsWrapped[i] = $"\"{options[i]}\"";
            }
            string optionsEntry = $"[{string.Join(", ", optionsWrapped)}]";
            return
                $"{{ type: \"dropdown\", id: {this.id}, title: \"{this.title}\", buttonText: \"{this.buttonText}\", options: {optionsEntry}}},";
        }
    }


    
    [SerializeField ]public List<HTMLDashCard> cards = new List<HTMLDashCard>();

    public void RegisterButton(string title, string buttonText, Action<String> callback)
    {
        cards.Add(new ButtonCard(title, buttonText, callback));
    }

    public void RegisterDropdown(string title, string buttonText, string[] options, Action<String> callback)
    {
        cards.Add(new DropdownCard(title, buttonText, options, callback));
    }

    public void RegisterInput(string title, string buttonText, string placeholder, Action<String> callback)
    {
        cards.Add(new InputCard(title, buttonText, placeholder, callback));
    }
    

    private string GenerateDashboardHtml()
    {
        string entries = "";
        for (int i = 0; i < cards.Count; i++)
        {
            entries += cards[i].AsEntry() + "\n";
        }

        Debug.LogWarning(entries);
        return
            $"<!DOCTYPE html>\n<html>\n<head>\n  <meta charset=\"UTF-8\" />\n  <title>NurSim Unity Dashboard</title>\n  <style>\n    body {{\n      margin: 0;\n      font-family: \"Segoe UI\", Tahoma, Geneva, Verdana, sans-serif;\n      background: #f7f7f7;\n      color: #333;\n      height: 100vh;\n      display: flex;\n      flex-direction: column;\n    }}\n\n    header {{\n      display: flex;\n      align-items: center;\n      padding: 1em 2em;\n      background: white;\n      border-bottom: 2px solid crimson;\n    }}\n\n    header img {{\n      height: 40px;\n      margin-right: 1em;\n    }}\n\n    header h1 {{\n      font-size: 1.5em;\n      margin: 0;\n    }}\n\n    .main-content {{\n      display: flex;\n      flex: 1;\n      overflow: hidden;\n    }}\n\n    .card-container {{\n      flex: 1;\n      display: flex;\n      flex-wrap: wrap;\n      gap: 1em;\n      padding: 2em;\n      overflow-y: auto;\n      box-sizing: border-box;\n    }}\n\n    .card {{\n      background: white;\n      border: 1px solid #ccc;\n      padding: 1em;\n      border-radius: 8px;\n      box-shadow: 0 2px 6px rgba(0, 0, 0, 0.1);\n      transition: transform 0.1s ease;\n      max-width: 300px;\n      max-height: 200px;\n      flex: 1 1 auto;\n      box-sizing: border-box;\n    }}\n\n    .card:hover {{\n      transform: translateY(-2px);\n    }}\n\n    .card h2 {{\n      margin-top: 0;\n      color: crimson;\n    }}\n\n     .card input,\n    .card select,\n    .card button {{\n      width: 100%;\n      padding: 0.6em;\n      margin-top: 0.6em;\n      font-size: 1em;\n      border-radius: 6px;\n      border: 1px solid #bbb;\n      box-sizing: border-box;\n    }}\n\n    .card input:focus,\n    .card select:focus {{\n      outline: none;\n      border-color: crimson;\n      box-shadow: 0 0 0 2px rgba(220, 20, 60, 0.2);\n    }}\n\n    .card button {{\n      background: crimson;\n      color: white;\n      border: none;\n      cursor: pointer;\n      transition: background 0.3s ease;\n    }}\n\n    .card button:hover {{\n      background: #a4161a;\n    }}\n    .notifications-panel {{\n      width: 320px;\n      background: #fff;\n      border-left: 1px solid #ccc;\n      padding: 1em;\n      overflow-y: auto;\n      box-shadow: -2px 0 6px rgba(0, 0, 0, 0.05);\n      box-sizing: border-box;\n    }}\n\n    .notifications-panel h2 {{\n      color: crimson;\n      margin-top: 0;\n    }}\n\n    .notification {{\n      margin-bottom: 1em;\n      padding: 1em;\n      border-radius: 6px;\n      color: #fff;\n      box-shadow: 0 1px 3px rgba(0, 0, 0, 0.1);\n    }}\n\n    .notification h3 {{\n      margin: 0 0 0.5em;\n    }}\n  </style>\n</head>\n<body>\n  <header>\n    <img src=\"https://labs.wpi.edu/hiro/wp-content/uploads/sites/45/2016/03/Hiro_Logo_WPITheme-300x108.png\" alt=\"Company Logo\" />\n    <h1>NurSim Unity Dashboard</h1>\n  </header>\n\n  <div class=\"main-content\">\n    <div class=\"card-container\" id=\"card-container\"></div>\n    <div class=\"notifications-panel\" id=\"notifications-panel\">\n      \n    </div>\n  </div>\n\n  <script>\n    const cards = [\n      {entries}\n    ];\n\n    const container = document.getElementById(\"card-container\");\n\n    cards.forEach((card) => {{\n      const div = document.createElement(\"div\");\n      div.className = \"card\";\n      const title = `<h2>${{card.title}}</h2>`;\n      let content = \"\";\n\n      if (card.type === \"button\") {{\n        content = `<button id=\"btn-${{card.id}}\">${{card.buttonText}}</button>`;\n      }} else if (card.type === \"input\") {{\n        content = `\n          <input id=\"input-${{card.id}}\" type=\"text\" placeholder=\"${{card.placeHolder}}\">\n          <button id=\"submit-input-${{card.id}}\">${{card.buttonText}}</button>\n        `;\n      }} else if (card.type === \"dropdown\") {{\n        const options = card.options.map(opt => `<option value=\"${{opt}}\">${{opt}}</option>`).join(\"\");\n        content = `\n          <select id=\"select-${{card.id}}\">${{options}}</select>\n          <button id=\"submit-select-${{card.id}}\">${{card.buttonText}}</button>\n        `;\n      }}\n\n      div.innerHTML = title + content;\n      container.appendChild(div);\n\n      // Interactivity\n      if (card.type === \"button\") {{\n        document.getElementById(`btn-${{card.id}}`).addEventListener(\"click\", () => {{\n          fetch(`/action/${{card.id}}`, {{\n            method: \"POST\",\n            body: card.title,\n          }});\n        }});\n      }} else if (card.type === \"input\") {{\n        document.getElementById(`submit-input-${{card.id}}`).addEventListener(\"click\", () => {{\n          const value = document.getElementById(`input-${{card.id}}`).value;\n          fetch(`/action/${{card.id}}`, {{\n            method: \"POST\",\n            body: value,\n          }});\n        }});\n      }} else if (card.type === \"dropdown\") {{\n        document.getElementById(`submit-select-${{card.id}}`).addEventListener(\"click\", () => {{\n          const value = document.getElementById(`select-${{card.id}}`).value;\n          fetch(`/action/${{card.id}}`, {{\n            method: \"POST\",\n            body: value,\n          }});\n        }});\n      }}\n    }});\n\n    // Notification API – you can call this from Unity\n    function addNotification(title, body, color) {{\n      const panel = document.getElementById(\"notifications-panel\");\n      const notification = document.createElement(\"div\");\n      notification.className = \"notification\";\n      notification.style.backgroundColor = color || \"#444\";\n      notification.innerHTML = `<h3>${{title}}</h3><p>${{body}}</p>`;\n      panel.appendChild(notification);\n      panel.scrollTop = panel.scrollHeight;\n    }}\n     function listenForNotifications() {{\n    fetch(\"/wait-for-message\")\n      .then((response) => response.json())\n      .then((data) => {{\n        if (data.title && data.body) {{\n          addNotification(data.title, data.body, data.color);\n        }}\n        listenForNotifications(); // Keep listening\n      }})\n      .catch(err => {{\n        console.error(\"Notification polling error:\", err);\n        setTimeout(listenForNotifications, 2000); // Retry after failure\n      }});\n  }}\n\n  listenForNotifications();\n\n\n  </script>\n\n\n</body>\n</html>\n";
    }
}
