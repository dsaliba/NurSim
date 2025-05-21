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

        return $"<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"UTF-8\">\n  <title>NurSim Unity Dashboard</title>\n  <style>\n    body {{\n      margin: 0;\n      padding: 0;\n      font-family: \"Segoe UI\", Tahoma, Geneva, Verdana, sans-serif;\n      background: #f2f2f2;\n      color: #333;\n    }}\n\n    header {{\n      display: flex;\n      align-items: center;\n      background: white;\n      border-bottom: 2px solid #AC2B37;\n      padding: 1em 2em;\n      box-shadow: 0 2px 5px rgba(0, 0, 0, 0.1);\n    }}\n\n    header img {{\n      height: 50px;\n      margin-right: 1em;\n    }}\n\n    header h1 {{\n      margin: 0;\n      font-size: 1.8em;\n      color: #AC2B37;\n    }}\n\n    .card-container {{\n      display: flex;\n      flex-wrap: wrap;\n      gap: 1.5em;\n      justify-content: center;\n      padding: 2em;\n    }}\n\n    .card {{\n      flex: 1 1 auto;\n      max-width: 300px;\n      background: white;\n      border-radius: 10px;\n      padding: 1.2em;\n      box-shadow: 0 4px 12px rgba(0, 0, 0, 0.08);\n      transition: transform 0.1s ease;\n      border: 1px solid #ddd;\n    }}\n\n    .card:hover {{\n      transform: translateY(-2px);\n    }}\n\n    .card h2 {{\n      margin-top: 0;\n      font-size: 1.2em;\n      color: #AC2B37;\n    }}\n\n    .card input,\n    .card select,\n    .card button {{\n      width: 100%;\n      padding: 0.6em;\n      margin-top: 0.6em;\n      font-size: 1em;\n      border-radius: 6px;\n      border: 1px solid #bbb;\n      box-sizing: border-box;\n    }}\n\n    .card input:focus,\n    .card select:focus {{\n      outline: none;\n      border-color: #AC2B37;\n      box-shadow: 0 0 0 2px rgba(220, 20, 60, 0.2);\n    }}\n\n    .card button {{\n      background: #AC2B37;\n      color: white;\n      border: none;\n      cursor: pointer;\n      transition: background 0.3s ease;\n    }}\n\n    .card button:hover {{\n      background: #a4161a;\n    }}\n  </style>\n</head>\n<body>\n  <header>\n    <img src=\"https://labs.wpi.edu/hiro/wp-content/uploads/sites/45/2016/03/Hiro_Logo_WPITheme-300x108.png\" alt=\"Company Logo\">\n    <h1>NurSim Unity Dashboard</h1>\n  </header>\n\n  <div id=\"card-container\" class=\"card-container\"></div>\n\n  <script>\n    const cards = [\n      {entries}\n    ];\n\n    const container = document.getElementById('card-container');\n\n    cards.forEach((card) => {{\n      const div = document.createElement('div');\n      div.className = 'card';\n      const title = `<h2>${{card.title}}</h2>`;\n      let content = \"\";\n\n      if (card.type === \"button\") {{\n        content = `<button id=\"btn-${{card.id}}\">${{card.buttonText}}</button>`;\n      }} else if (card.type === \"input\") {{\n        content = `\n          <input id=\"input-${{card.id}}\" type=\"text\" placeholder=\"${{card.placeHolder}}\">\n          <button id=\"submit-input-${{card.id}}\">${{card.buttonText}}</button>\n        `;\n      }} else if (card.type === \"dropdown\") {{\n        const options = card.options.map(opt => `<option value=\"${{opt}}\">${{opt}}</option>`).join('');\n        content = `\n          <select id=\"select-${{card.id}}\">${{options}}</select>\n          <button id=\"submit-select-${{card.id}}\">${{card.buttonText}}</button>\n        `;\n      }}\n\n      div.innerHTML = title + content;\n      container.appendChild(div);\n\n      // Add interactivity\n      if (card.type === \"button\") {{\n        document.getElementById(`btn-${{card.id}}`).addEventListener('click', () => {{\n          fetch(`/action/${{card.id}}`, {{\n            method: 'POST',\n            body: card.title,\n          }});\n        }});\n      }} else if (card.type === \"input\") {{\n        document.getElementById(`submit-input-${{card.id}}`).addEventListener('click', () => {{\n          const value = document.getElementById(`input-${{card.id}}`).value;\n          fetch(`/action/${{card.id}}`, {{\n            method: 'POST',\n            body: value,\n          }});\n        }});\n      }} else if (card.type === \"dropdown\") {{\n        document.getElementById(`submit-select-${{card.id}}`).addEventListener('click', () => {{\n          const value = document.getElementById(`select-${{card.id}}`).value;\n          fetch(`/action/${{card.id}}`, {{\n            method: 'POST',\n            body: value,\n          }});\n        }});\n      }}\n    }});\n  </script>\n</body>\n</html>\n";
         }
}
