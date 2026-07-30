using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
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

    private object notifyLock = new object();
    private Queue<HttpListenerContext> waitingClients = new Queue<HttpListenerContext>();

    private object cardsLock = new object();

    private object channelsLock = new object();
    private Dictionary<string, DataChannel> channels = new Dictionary<string, DataChannel>();

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

                        HTMLDashCard matched;
                        lock (cardsLock)
                        {
                            matched = cards.FirstOrDefault(c => c.id == acID);
                        }

                        if (matched != null)
                        {
                            using (System.IO.Stream body = request.InputStream)
                            using (var reader = new System.IO.StreamReader(body, request.ContentEncoding))
                            {
                                string content = reader.ReadToEnd();
                                UnityMainThreadDispatcher.Enqueue(matched.Invoke, content);
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
                    else if (request.HttpMethod == "GET" && path.StartsWith("/data/"))
                    {
                        string channelName = path.Substring("/data/".Length);
                        int since = 0;
                        int.TryParse(request.QueryString["since"], out since);

                        DataChannel ch = GetOrCreateChannel(channelName);
                        ch.TryRespondOrQueue(context, since);
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
        string json = $"{{\"title\":\"{Esc(title)}\",\"body\":\"{Esc(body)}\",\"color\":\"{Esc(color)}\"}}";
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

    private static string Esc(string s)
    {
        return s == null ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // ── Generic versioned long-poll channel ─────────────────────────────
    private class DataChannel
    {
        private readonly object lockObj = new object();
        private int version = 0;
        private string cachedJson = "{}";
        private Queue<HttpListenerContext> waiting = new Queue<HttpListenerContext>();

        public void Publish(string json)
        {
            Queue<HttpListenerContext> toFlush;
            int v;
            lock (lockObj)
            {
                version++;
                cachedJson = json;
                v = version;
                toFlush = waiting;
                waiting = new Queue<HttpListenerContext>();
            }

            while (toFlush.Count > 0)
            {
                var ctx = toFlush.Dequeue();
                try { WriteVersioned(ctx, v, json); }
                catch (Exception e) { Debug.LogWarning($"DataChannel flush error: {e.Message}"); }
            }
        }

        public void TryRespondOrQueue(HttpListenerContext ctx, int since)
        {
            int v; string json; bool immediate;
            lock (lockObj)
            {
                if (since < version) { v = version; json = cachedJson; immediate = true; }
                else { waiting.Enqueue(ctx); v = 0; json = null; immediate = false; }
            }
            if (immediate) WriteVersioned(ctx, v, json);
        }

        private static void WriteVersioned(HttpListenerContext ctx, int version, string innerJson)
        {
            string payload = $"{{\"version\":{version},\"data\":{innerJson}}}";
            byte[] buffer = Encoding.UTF8.GetBytes(payload);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = buffer.Length;
            ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
            ctx.Response.Close();
        }
    }

    private DataChannel GetOrCreateChannel(string name)
    {
        lock (channelsLock)
        {
            if (!channels.TryGetValue(name, out var ch))
            {
                ch = new DataChannel();
                channels[name] = ch;
            }
            return ch;
        }
    }

    /// <summary>
    /// Publish new data on an arbitrary named channel.
    /// </summary>
    public void PublishChannel(string name, string json)
    {
        GetOrCreateChannel(name).Publish(json);
    }

    // ── Card types ───────────────────────────────────────────────────────

    [System.Serializable]
    public abstract class HTMLDashCard
    {
        public static int nextID = 0;
        public int id;
        public abstract string AsJson();
        public abstract void Invoke(string rawBody);
    }

    [System.Serializable]
    public class ButtonCard : HTMLDashCard
    {
        public string title;
        public string buttonText;
        public Action<string> callback;

        public ButtonCard(string title, string buttonText, Action<string> callback)
        {
            this.id = HTMLDashCard.nextID++;
            this.title = title;
            this.buttonText = buttonText;
            this.callback = callback;
        }

        public override string AsJson() =>
            $"{{\"type\":\"button\",\"id\":{id},\"title\":\"{Esc(title)}\",\"buttonText\":\"{Esc(buttonText)}\"}}";

        public override void Invoke(string rawBody) => callback?.Invoke(rawBody);
    }

    [System.Serializable]
    public class InputCard : HTMLDashCard
    {
        public string title;
        public string buttonText;
        public string placeHolder;
        public Action<string> callback;

        public InputCard(string title, string buttonText, string placeHolder, Action<string> callback)
        {
            this.id = HTMLDashCard.nextID++;
            this.title = title;
            this.buttonText = buttonText;
            this.callback = callback;
            this.placeHolder = placeHolder;
        }

        public override string AsJson() =>
            $"{{\"type\":\"input\",\"id\":{id},\"title\":\"{Esc(title)}\",\"buttonText\":\"{Esc(buttonText)}\",\"placeHolder\":\"{Esc(placeHolder)}\"}}";

        public override void Invoke(string rawBody) => callback?.Invoke(rawBody);
    }

    [System.Serializable]
    public class DropdownCard : HTMLDashCard
    {
        public string title;
        public string buttonText;
        public string[] options;
        public Action<string> callback;

        public DropdownCard(string title, string buttonText, string[] options, Action<string> callback)
        {
            this.id = HTMLDashCard.nextID++;
            this.title = title;
            this.buttonText = buttonText;
            this.callback = callback;
            this.options = options;
        }

        public override string AsJson()
        {
            string opts = string.Join(",", options.Select(o => $"\"{Esc(o)}\""));
            return $"{{\"type\":\"dropdown\",\"id\":{id},\"title\":\"{Esc(title)}\",\"buttonText\":\"{Esc(buttonText)}\",\"options\":[{opts}]}}";
        }

        public override void Invoke(string rawBody) => callback?.Invoke(rawBody);
    }

    [System.Serializable]
    public class MultiFieldCard : HTMLDashCard
    {
        [System.Serializable]
        public class MultiField
        {
            public string key;
            public string fieldType; // "dropdown" or "input"
            public string label;
            public string[] options;
            public string placeholder;

            public static MultiField Dropdown(string key, string label, string[] options) =>
                new MultiField { key = key, fieldType = "dropdown", label = label, options = options };

            public static MultiField Input(string key, string label, string placeholder = "") =>
                new MultiField { key = key, fieldType = "input", label = label, placeholder = placeholder };
        }

        public string title;
        public string buttonText;
        public List<MultiField> fields;
        public Action<Dictionary<string, string>> multiCallback;

        public MultiFieldCard(string title, string buttonText, List<MultiField> fields, Action<Dictionary<string, string>> callback)
        {
            this.id = HTMLDashCard.nextID++;
            this.title = title;
            this.buttonText = buttonText;
            this.fields = fields;
            this.multiCallback = callback;
        }

        public override string AsJson()
        {
            string fieldsJson = string.Join(",", fields.Select(f =>
            {
                string optsPart = f.options != null
                    ? $",\"options\":[{string.Join(",", f.options.Select(o => $"\"{Esc(o)}\""))}]"
                    : "";
                string placeholderPart = f.placeholder != null
                    ? $",\"placeholder\":\"{Esc(f.placeholder)}\""
                    : "";
                return $"{{\"key\":\"{Esc(f.key)}\",\"fieldType\":\"{Esc(f.fieldType)}\",\"label\":\"{Esc(f.label)}\"{optsPart}{placeholderPart}}}";
            }));

            return $"{{\"type\":\"multifield\",\"id\":{id},\"title\":\"{Esc(title)}\",\"buttonText\":\"{Esc(buttonText)}\",\"fields\":[{fieldsJson}]}}";
        }

        public override void Invoke(string rawBody)
        {
            var dict = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(rawBody))
            {
                foreach (var pair in rawBody.Split('&'))
                {
                    if (string.IsNullOrEmpty(pair)) continue;
                    int eq = pair.IndexOf('=');
                    string k = eq >= 0 ? pair.Substring(0, eq) : pair;
                    string v = eq >= 0 ? pair.Substring(eq + 1) : "";
                    dict[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v);
                }
            }
            multiCallback?.Invoke(dict);
        }
    }

    // ── Drag-order card ──────────────────────────────────────────────────

    /// <summary>One item in an ordered list submission from a DragOrderCard.</summary>
    [System.Serializable]
    public class OrderedItemSubmission
    {
        public string name;
        public bool enabled;
    }

    // Wrapper needed because JsonUtility cannot deserialise a root-level array.
    [System.Serializable]
    private class OrderedItemListWrapper
    {
        public List<OrderedItemSubmission> items;
    }

    /// <summary>
    /// A card that displays a drag-and-drop reorderable list with per-item
    /// enable/disable checkboxes. On submit, the callback receives the items
    /// in the new order with their enabled state.
    /// </summary>
    [System.Serializable]
    public class DragOrderCard : HTMLDashCard
    {
        public string title;
        public string buttonText;
        public string[] items;
        public Action<List<OrderedItemSubmission>> callback;

        public DragOrderCard(string title, string buttonText, string[] items, Action<List<OrderedItemSubmission>> callback)
        {
            this.id = HTMLDashCard.nextID++;
            this.title = title;
            this.buttonText = buttonText;
            this.items = items;
            this.callback = callback;
        }

        public override string AsJson()
        {
            string itemsJson = string.Join(",", items.Select(i => $"\"{Esc(i)}\""));
            return $"{{\"type\":\"dragorder\",\"id\":{id},\"title\":\"{Esc(title)}\",\"buttonText\":\"{Esc(buttonText)}\",\"items\":[{itemsJson}]}}";
        }

        public override void Invoke(string rawBody)
        {
            try
            {
                // Wrap the JSON array so JsonUtility can handle it.
                var wrapper = JsonUtility.FromJson<OrderedItemListWrapper>($"{{\"items\":{rawBody}}}");
                callback?.Invoke(wrapper?.items ?? new List<OrderedItemSubmission>());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"DragOrderCard: failed to parse submission: {e.Message}\nBody: {rawBody}");
            }
        }
    }

    /// <summary>One key/value pair, used for ordered "condition" selections.</summary>
    [System.Serializable]
    public class ConditionValuePair
    {
        public string key;
        public string value;
    }

    /// <summary>Body shape posted by the Recording card's Start/Stop buttons.</summary>
    [System.Serializable]
    public class RecordingSubmission
    {
        public string command; // "start" or "stop"
        public string participant;
        public List<ConditionValuePair> conditions;
        public List<string> topics;
    }

    [System.Serializable]
    public class RecordingCard : HTMLDashCard
    {
        [System.Serializable]
        public class ConditionDef
        {
            public string key;
            public string label;
            public List<string> options;
        }

        public string title = "Recording";
        public List<ConditionDef> conditions = new List<ConditionDef>();
        public Action<RecordingSubmission> onSubmit;

        public RecordingCard(Action<RecordingSubmission> onSubmit)
        {
            this.id = HTMLDashCard.nextID++;
            this.onSubmit = onSubmit;
        }

        public override string AsJson()
        {
            string condJson = string.Join(",", conditions.Select(c =>
            {
                string opts = string.Join(",", c.options.Select(o => $"\"{Esc(o)}\""));
                return $"{{\"key\":\"{Esc(c.key)}\",\"label\":\"{Esc(c.label)}\",\"options\":[{opts}]}}";
            }));
            return $"{{\"type\":\"recording\",\"id\":{id},\"title\":\"{Esc(title)}\",\"conditions\":[{condJson}]}}";
        }

        public override void Invoke(string rawBody)
        {
            RecordingSubmission sub;
            try { sub = JsonUtility.FromJson<RecordingSubmission>(rawBody); }
            catch (Exception e)
            {
                Debug.LogWarning($"RecordingCard: failed to parse submission: {e.Message}");
                return;
            }
            if (sub != null) onSubmit?.Invoke(sub);
        }
    }

    // ── Registration ─────────────────────────────────────────────────────

    [SerializeField] public List<HTMLDashCard> cards = new List<HTMLDashCard>();

    public void RegisterButton(string title, string buttonText, Action<string> callback)
    {
        lock (cardsLock) { cards.Add(new ButtonCard(title, buttonText, callback)); }
        BumpCardsVersionAndFlush();
    }

    public void RegisterDropdown(string title, string buttonText, string[] options, Action<string> callback)
    {
        lock (cardsLock) { cards.Add(new DropdownCard(title, buttonText, options, callback)); }
        BumpCardsVersionAndFlush();
    }

    public void RegisterInput(string title, string buttonText, string placeholder, Action<string> callback)
    {
        lock (cardsLock) { cards.Add(new InputCard(title, buttonText, placeholder, callback)); }
        BumpCardsVersionAndFlush();
    }

    public void RegisterMultiField(string title, string buttonText, List<MultiFieldCard.MultiField> fields, Action<Dictionary<string, string>> callback)
    {
        lock (cardsLock) { cards.Add(new MultiFieldCard(title, buttonText, fields, callback)); }
        BumpCardsVersionAndFlush();
    }

    public RecordingCard RegisterRecordingCard(Action<RecordingSubmission> onSubmit)
    {
        var card = new RecordingCard(onSubmit);
        lock (cardsLock) { cards.Add(card); }
        BumpCardsVersionAndFlush();
        return card;
    }

    /// <summary>
    /// Register a drag-and-drop reorder card. The callback fires on the main
    /// thread with the items in their new order and their enabled states.
    /// </summary>
    public DragOrderCard RegisterDragOrder(string title, string buttonText, string[] items, Action<List<OrderedItemSubmission>> callback)
    {
        var card = new DragOrderCard(title, buttonText, items, callback);
        lock (cardsLock) { cards.Add(card); }
        BumpCardsVersionAndFlush();
        return card;
    }

    /// <summary>
    /// Call after mutating a card object in place to re-publish the cards channel.
    /// </summary>
    public void NotifyCardsChanged() => BumpCardsVersionAndFlush();

    private void BumpCardsVersionAndFlush()
    {
        string payload;
        lock (cardsLock)
        {
            var sb = new StringBuilder();
            sb.Append("{\"cards\":[");
            for (int i = 0; i < cards.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(cards[i].AsJson());
            }
            sb.Append("]}");
            payload = sb.ToString();
        }
        PublishChannel("cards", payload);
    }

    // ── Page shell ───────────────────────────────────────────────────────

    private string GenerateDashboardHtml()
    {
        return @"<!DOCTYPE html>
<html>
<head>
  <meta charset=""UTF-8"" />
  <title>NurSim Unity Dashboard</title>
  <style>
    body {
      margin: 0;
      font-family: ""Segoe UI"", Tahoma, Geneva, Verdana, sans-serif;
      background: #f7f7f7;
      color: #333;
      height: 100vh;
      display: flex;
      flex-direction: column;
    }
    header {
      display: flex;
      align-items: center;
      padding: 1em 2em;
      background: white;
      border-bottom: 2px solid crimson;
    }
    header img { height: 40px; margin-right: 1em; }
    header h1 { font-size: 1.5em; margin: 0; }

    .main-content { display: flex; flex: 1; overflow: hidden; }

    .panel-column {
      flex: 1 1 0;
      min-width: 0;
      overflow-y: auto;
      box-sizing: border-box;
      border-right: 1px solid #ddd;
    }
    .panel-column:last-of-type { border-right: none; }
    .panel-column h2.column-title {
      margin: 0;
      padding: 0.9em 1.5em;
      background: white;
      border-bottom: 1px solid #ddd;
      color: crimson;
      font-size: 1.1em;
      position: sticky;
      top: 0;
    }

    .card-container {
      display: flex;
      flex-wrap: wrap;
      gap: 1em;
      padding: 1.5em;
      align-content: flex-start;
      box-sizing: border-box;
    }
    .card {
      background: white;
      border: 1px solid #ccc;
      padding: 1em;
      border-radius: 8px;
      box-shadow: 0 2px 6px rgba(0, 0, 0, 0.1);
      transition: transform 0.1s ease;
      max-width: 300px;
      max-height: 200px;
      flex: 1 1 auto;
      box-sizing: border-box;
    }
    .card.multifield { max-height: none; }
    .card.dragorder  { max-height: none; max-width: 360px; }
    .card:hover { transform: translateY(-2px); }
    .card h2 { margin-top: 0; color: crimson; }
    .card label { display: block; margin-top: 0.6em; font-size: 0.8em; color: #777; }
    .card input, .card select, .card button {
      width: 100%;
      padding: 0.6em;
      margin-top: 0.3em;
      font-size: 1em;
      border-radius: 6px;
      border: 1px solid #bbb;
      box-sizing: border-box;
    }
    .card input:focus, .card select:focus {
      outline: none;
      border-color: crimson;
      box-shadow: 0 0 0 2px rgba(220, 20, 60, 0.2);
    }
    .card button {
      background: crimson;
      color: white;
      border: none;
      cursor: pointer;
      transition: background 0.3s ease;
      margin-top: 0.8em;
    }
    .card button:hover { background: #a4161a; }

    /* ── Drag-order list ── */
    .drag-list {
      list-style: none;
      padding: 0;
      margin: 0.5em 0 0;
    }
    .drag-item {
      display: flex;
      align-items: center;
      gap: 0.5em;
      padding: 0.45em 0.6em;
      margin: 0.25em 0;
      background: #f9f9f9;
      border: 1px solid #ddd;
      border-radius: 5px;
      user-select: none;
      cursor: default;
      transition: background 0.1s, border-color 0.1s;
    }
    .drag-item.dragging  { opacity: 0.35; }
    .drag-item.drag-over { border-color: crimson; background: #fff0f3; }
    .drag-handle {
      cursor: grab;
      color: #bbb;
      font-size: 1.2em;
      line-height: 1;
      flex-shrink: 0;
    }
    .drag-handle:active { cursor: grabbing; }
    .drag-check {
      width: auto !important;
      margin: 0 !important;
      padding: 0 !important;
      flex-shrink: 0;
      cursor: pointer;
    }
    .drag-label {
      flex: 1;
      font-size: 0.9em;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .drag-index {
      font-size: 0.75em;
      color: #bbb;
      flex-shrink: 0;
      min-width: 1.2em;
      text-align: right;
    }

    .recording-container { padding: 1.5em; box-sizing: border-box; }
    .recording-panel {
      background: white;
      border: 1px solid #ccc;
      padding: 1.5em;
      border-radius: 8px;
      box-shadow: 0 2px 6px rgba(0, 0, 0, 0.1);
      width: 100%;
      box-sizing: border-box;
      margin-bottom: 1.5em;
    }
    .recording-panel h2 { margin-top: 0; color: crimson; }
    .recording-panel label { display: block; margin-top: 0.8em; font-size: 0.85em; color: #777; }
    .recording-panel input[type=""text""], .recording-panel select {
      width: 100%;
      padding: 0.6em;
      margin-top: 0.3em;
      font-size: 1em;
      border-radius: 6px;
      border: 1px solid #bbb;
      box-sizing: border-box;
    }
    .topic-toolbar { display: flex; gap: 0.5em; margin-top: 0.5em; flex-wrap: wrap; }
    .topic-toolbar button {
      flex: 1 1 auto;
      padding: 0.5em;
      border-radius: 6px;
      border: 1px solid #bbb;
      background: #f0f0f0;
      cursor: pointer;
      font-size: 0.85em;
    }
    .topic-toolbar button:hover { background: #e2e2e2; }
    .topic-toolbar button.refresh-btn { background: #e8f0ff; border-color: #99b8e8; }
    .topic-toolbar button.refresh-btn:hover { background: #d6e4ff; }
    .topic-list {
      margin-top: 0.5em;
      max-height: 220px;
      overflow-y: auto;
      border: 1px solid #ddd;
      border-radius: 6px;
      padding: 0.4em;
    }
    .topic-row {
      display: flex;
      align-items: center;
      gap: 0.5em;
      padding: 0.3em 0.2em;
      font-size: 0.9em;
      border-bottom: 1px solid #f0f0f0;
    }
    .topic-row:last-child { border-bottom: none; }
    .topic-row input { width: auto; margin: 0; }
    .topic-type { margin-left: auto; color: #999; font-size: 0.8em; }
    .topic-empty { padding: 0.6em; color: #999; font-size: 0.85em; font-style: italic; }
    .recording-buttons { display: flex; gap: 0.8em; margin-top: 1.2em; }
    .recording-buttons button {
      flex: 1;
      padding: 0.8em;
      border: none;
      border-radius: 6px;
      color: white;
      cursor: pointer;
      font-size: 1em;
    }
    .start-btn { background: #1a9c4b; }
    .start-btn:hover { background: #157a3b; }
    .stop-btn { background: crimson; }
    .stop-btn:hover { background: #a4161a; }

    .notifications-panel {
      width: 320px;
      flex: 0 0 320px;
      background: #fff;
      border-left: 1px solid #ccc;
      padding: 1em;
      overflow-y: auto;
      box-shadow: -2px 0 6px rgba(0, 0, 0, 0.05);
      box-sizing: border-box;
    }
    .notifications-panel h2 { color: crimson; margin-top: 0; }
    .notification {
      margin-bottom: 1em;
      padding: 1em;
      border-radius: 6px;
      color: #fff;
      box-shadow: 0 1px 3px rgba(0, 0, 0, 0.1);
    }
    .notification h3 { margin: 0 0 0.5em; }
  </style>
</head>
<body>
  <header>
    <img src=""https://labs.wpi.edu/hiro/wp-content/uploads/sites/45/2016/03/Hiro_Logo_WPITheme-300x108.png"" alt=""Company Logo"" />
    <h1>NurSim Unity Dashboard</h1>
  </header>

  <div class=""main-content"">
    <div class=""panel-column"" style=""flex: 1.2 1 0;"">
      <h2 class=""column-title"">Controls</h2>
      <div class=""card-container"" id=""card-container""></div>
    </div>
    <div class=""panel-column"" style=""flex: 1 1 0;"">
      <h2 class=""column-title"">Recording</h2>
      <div class=""recording-container"" id=""recording-container""></div>
    </div>
    <div class=""notifications-panel"" id=""notifications-panel"">
      <h2>Notifications</h2>
    </div>
  </div>

  <script>
    let recordingTopics = [];
    let recordingSelected = {}; // cardId -> Set of topic names

    // ── Drag-order helpers ────────────────────────────────────────────────

    function initDragList(listId) {
      const list = document.getElementById(listId);
      if (!list) return;
      let dragged = null;

      list.addEventListener('dragstart', e => {
        dragged = e.target.closest('.drag-item');
        if (!dragged) return;
        // Use setTimeout so the 'dragging' class is applied after the
        // browser captures the drag image.
        setTimeout(() => dragged && dragged.classList.add('dragging'), 0);
      });

      list.addEventListener('dragend', () => {
        if (dragged) dragged.classList.remove('dragging');
        list.querySelectorAll('.drag-item').forEach(el => el.classList.remove('drag-over'));
        dragged = null;
        refreshDragIndices(list);
      });

      list.addEventListener('dragover', e => {
        e.preventDefault();
        if (!dragged) return;
        const target = e.target.closest('.drag-item');
        if (!target || target === dragged) return;
        list.querySelectorAll('.drag-item').forEach(el => el.classList.remove('drag-over'));
        target.classList.add('drag-over');
        const rect = target.getBoundingClientRect();
        if (e.clientY < rect.top + rect.height / 2) {
          list.insertBefore(dragged, target);
        } else {
          list.insertBefore(dragged, target.nextSibling);
        }
      });

      list.addEventListener('dragleave', e => {
        const target = e.target.closest('.drag-item');
        if (target && target !== dragged) target.classList.remove('drag-over');
      });
    }

    function refreshDragIndices(list) {
      list.querySelectorAll('.drag-item').forEach((el, i) => {
        const idx = el.querySelector('.drag-index');
        if (idx) idx.textContent = (i + 1) + '.';
      });
    }

    // ── Card rendering ────────────────────────────────────────────────────

    function renderCards(cardList) {
      renderNormalCards(cardList.filter(c => c.type !== ""recording""));
      renderRecordingCards(cardList.filter(c => c.type === ""recording""));
    }

    function renderNormalCards(cardList) {
      const container = document.getElementById(""card-container"");
      container.innerHTML = """";

      cardList.forEach((card) => {
        const div = document.createElement(""div"");
        div.className = ""card"" +
          (card.type === ""multifield"" ? "" multifield"" : """") +
          (card.type === ""dragorder""  ? "" dragorder""  : """");
        let html = `<h2>${card.title}</h2>`;

        if (card.type === ""button"") {
          html += `<button id=""btn-${card.id}"">${card.buttonText}</button>`;

        } else if (card.type === ""input"") {
          html += `
            <input id=""input-${card.id}"" type=""text"" placeholder=""${card.placeHolder}"">
            <button id=""submit-input-${card.id}"">${card.buttonText}</button>
          `;

        } else if (card.type === ""dropdown"") {
          const options = card.options.map(opt => `<option value=""${opt}"">${opt}</option>`).join("""");
          html += `
            <select id=""select-${card.id}"">${options}</select>
            <button id=""submit-select-${card.id}"">${card.buttonText}</button>
          `;

        } else if (card.type === ""multifield"") {
          card.fields.forEach(f => {
            if (f.fieldType === ""dropdown"") {
              const options = f.options.map(opt => `<option value=""${opt}"">${opt}</option>`).join("""");
              html += `<label>${f.label}</label><select id=""mf-${card.id}-${f.key}"">${options}</select>`;
            } else {
              html += `<label>${f.label}</label><input id=""mf-${card.id}-${f.key}"" type=""text"" placeholder=""${f.placeholder || """"}"">`;
            }
          });
          html += `<button id=""submit-mf-${card.id}"">${card.buttonText}</button>`;

        } else if (card.type === ""dragorder"") {
          const itemRows = card.items.map((name, i) => `
            <li class=""drag-item"" draggable=""true"" data-name=""${name}"">
              <span class=""drag-handle"">&#x2807;</span>
              <input type=""checkbox"" class=""drag-check"" checked title=""Enable/disable this goal"">
              <span class=""drag-label"">${name}</span>
              <span class=""drag-index"">${i + 1}.</span>
            </li>`).join("""");
          html += `<ul id=""draglist-${card.id}"" class=""drag-list"">${itemRows}</ul>`;
          html += `<button id=""submit-drag-${card.id}"">${card.buttonText}</button>`;
        }

        div.innerHTML = html;
        container.appendChild(div);

        // Wire events
        if (card.type === ""button"") {
          document.getElementById(`btn-${card.id}`).addEventListener(""click"", () => {
            fetch(`/action/${card.id}`, { method: ""POST"", body: card.title });
          });

        } else if (card.type === ""input"") {
          document.getElementById(`submit-input-${card.id}`).addEventListener(""click"", () => {
            const value = document.getElementById(`input-${card.id}`).value;
            fetch(`/action/${card.id}`, { method: ""POST"", body: value });
          });

        } else if (card.type === ""dropdown"") {
          document.getElementById(`submit-select-${card.id}`).addEventListener(""click"", () => {
            const value = document.getElementById(`select-${card.id}`).value;
            fetch(`/action/${card.id}`, { method: ""POST"", body: value });
          });

        } else if (card.type === ""multifield"") {
          document.getElementById(`submit-mf-${card.id}`).addEventListener(""click"", () => {
            const parts = card.fields.map(f => {
              const el = document.getElementById(`mf-${card.id}-${f.key}`);
              return `${encodeURIComponent(f.key)}=${encodeURIComponent(el.value)}`;
            });
            fetch(`/action/${card.id}`, { method: ""POST"", body: parts.join(""&"") });
          });

        } else if (card.type === ""dragorder"") {
          initDragList(`draglist-${card.id}`);
          document.getElementById(`submit-drag-${card.id}`).addEventListener(""click"", () => {
            const list = document.getElementById(`draglist-${card.id}`);
            const items = Array.from(list.querySelectorAll('.drag-item')).map(li => ({
              name: li.dataset.name,
              enabled: li.querySelector('.drag-check').checked
            }));
            fetch(`/action/${card.id}`, {
              method: ""POST"",
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(items)
            });
          });
        }
      });
    }

    function renderRecordingCards(cardList) {
      const container = document.getElementById(""recording-container"");
      container.innerHTML = """";

      cardList.forEach((card) => {
        if (!recordingSelected[card.id]) recordingSelected[card.id] = new Set();

        const panel = document.createElement(""div"");
        panel.className = ""recording-panel"";

        let html = `<h2>${card.title}</h2>`;
        html += `<label>Participant</label><input id=""rec-participant-${card.id}"" type=""text"" placeholder=""Participant ID"">`;

        card.conditions.forEach(cond => {
          const opts = cond.options.map(o => `<option value=""${o}"">${o}</option>`).join("""");
          html += `<label>${cond.label}</label><select id=""rec-cond-${card.id}-${cond.key}"">${opts}</select>`;
        });

        html += `
          <label>Topics</label>
          <div class=""topic-toolbar"">
            <button class=""refresh-btn"" id=""rec-refresh-${card.id}"">&#x21bb; Refresh Topics</button>
            <button id=""rec-selall-${card.id}"">Select All</button>
            <button id=""rec-deselall-${card.id}"">Deselect All</button>
            <button id=""rec-save-${card.id}"">Save Selection</button>
            <button id=""rec-load-${card.id}"">Load Selection</button>
          </div>
          <div class=""topic-list"" id=""rec-topics-${card.id}""></div>
          <div class=""recording-buttons"">
            <button class=""start-btn"" id=""rec-start-${card.id}"">Start Recording</button>
            <button class=""stop-btn"" id=""rec-stop-${card.id}"">Stop Recording</button>
          </div>
        `;

        panel.innerHTML = html;
        container.appendChild(panel);

        document.getElementById(`rec-refresh-${card.id}`).addEventListener(""click"", fetchTopicsNow);
        document.getElementById(`rec-selall-${card.id}`).addEventListener(""click"", () => {
          recordingTopics.forEach(t => recordingSelected[card.id].add(t.name));
          renderTopicList(card.id);
        });
        document.getElementById(`rec-deselall-${card.id}`).addEventListener(""click"", () => {
          recordingSelected[card.id].clear();
          renderTopicList(card.id);
        });
        document.getElementById(`rec-save-${card.id}`).addEventListener(""click"", () => saveSelection(card.id));
        document.getElementById(`rec-load-${card.id}`).addEventListener(""click"", () => loadSelection(card.id, false));
        document.getElementById(`rec-start-${card.id}`).addEventListener(""click"", () => submitRecording(card, ""start""));
        document.getElementById(`rec-stop-${card.id}`).addEventListener(""click"", () => submitRecording(card, ""stop""));

        loadSelection(card.id, true);
        renderTopicList(card.id);
      });

      if (cardList.length > 0) fetchTopicsNow();
    }

    function renderTopicList(cardId) {
      const el = document.getElementById(`rec-topics-${cardId}`);
      if (!el) return;
      el.innerHTML = """";

      if (recordingTopics.length === 0) {
        const empty = document.createElement(""div"");
        empty.className = ""topic-empty"";
        empty.textContent = ""No topics received yet. Click Refresh Topics."";
        el.appendChild(empty);
        return;
      }

      recordingTopics.forEach(t => {
        const row = document.createElement(""label"");
        row.className = ""topic-row"";
        const checked = recordingSelected[cardId].has(t.name) ? ""checked"" : """";
        row.innerHTML = `<input type=""checkbox"" ${checked}> ${t.name} <span class=""topic-type"">${t.type}</span>`;
        row.querySelector(""input"").addEventListener(""change"", (e) => {
          if (e.target.checked) recordingSelected[cardId].add(t.name);
          else recordingSelected[cardId].delete(t.name);
        });
        el.appendChild(row);
      });
    }

    function fetchTopicsNow() {
      fetch(""/data/recording-topics?since=-1"")
        .then(r => r.json())
        .then(resp => {
          recordingTopics = (resp.data && resp.data.topics) || [];
          Object.keys(recordingSelected).forEach(cardId => renderTopicList(cardId));
        })
        .catch(err => console.error(""manual topic refresh failed:"", err));
    }

    function getCookie(name) {
      const m = document.cookie.match(new RegExp(""(?:^|; )"" + name + ""=([^;]*)""));
      return m ? decodeURIComponent(m[1]) : null;
    }
    function setCookie(name, value) {
      document.cookie = `${name}=${encodeURIComponent(value)}; max-age=31536000; path=/`;
    }

    function saveSelection(cardId) {
      const participant = document.getElementById(`rec-participant-${cardId}`).value;
      const conditions = {};
      document.querySelectorAll(`select[id^=""rec-cond-${cardId}-""]`).forEach(sel => {
        const key = sel.id.replace(`rec-cond-${cardId}-`, """");
        conditions[key] = sel.value;
      });
      const data = { participant, conditions, topics: Array.from(recordingSelected[cardId] || []) };
      setCookie(`iona_rec_${cardId}`, JSON.stringify(data));
    }

    function loadSelection(cardId, silent) {
      const raw = getCookie(`iona_rec_${cardId}`);
      if (!raw) return;
      try {
        const data = JSON.parse(raw);
        const pInput = document.getElementById(`rec-participant-${cardId}`);
        if (pInput && data.participant) pInput.value = data.participant;

        Object.keys(data.conditions || {}).forEach(key => {
          const sel = document.getElementById(`rec-cond-${cardId}-${key}`);
          if (sel) sel.value = data.conditions[key];
        });

        recordingSelected[cardId] = new Set(data.topics || []);
        renderTopicList(cardId);
      } catch (e) {
        if (!silent) console.error(""Failed to load selection:"", e);
      }
    }

    function submitRecording(card, command) {
      const participant = document.getElementById(`rec-participant-${card.id}`).value;
      const conditions = card.conditions.map(c => ({
        key: c.key,
        value: document.getElementById(`rec-cond-${card.id}-${c.key}`).value
      }));
      const topics = Array.from(recordingSelected[card.id] || []);
      const body = JSON.stringify({ command, participant, conditions, topics });
      fetch(`/action/${card.id}`, { method: ""POST"", body });
    }

    function cardsLoop(since) {
      fetch(`/data/cards?since=${since}`)
        .then(r => r.json())
        .then(resp => {
          renderCards(resp.data.cards);
          cardsLoop(resp.version);
        })
        .catch(err => {
          console.error(""cards poll error:"", err);
          setTimeout(() => cardsLoop(since), 2000);
        });
    }

    function recordingTopicsLoop(since) {
      fetch(`/data/recording-topics?since=${since}`)
        .then(r => r.json())
        .then(resp => {
          recordingTopics = (resp.data && resp.data.topics) || [];
          Object.keys(recordingSelected).forEach(cardId => renderTopicList(cardId));
          recordingTopicsLoop(resp.version);
        })
        .catch(err => {
          console.error(""recording-topics poll error:"", err);
          setTimeout(() => recordingTopicsLoop(since), 2000);
        });
    }

    function addNotification(title, body, color) {
      const panel = document.getElementById(""notifications-panel"");
      const notification = document.createElement(""div"");
      notification.className = ""notification"";
      notification.style.backgroundColor = color || ""#444"";
      notification.innerHTML = `<h3>${title}</h3><p>${body}</p>`;
      panel.appendChild(notification);
      panel.scrollTop = panel.scrollHeight;
    }

    function listenForNotifications() {
      fetch(""/wait-for-message"")
        .then((response) => response.json())
        .then((data) => {
          if (data.title && data.body) addNotification(data.title, data.body, data.color);
          listenForNotifications();
        })
        .catch(err => {
          console.error(""Notification polling error:"", err);
          setTimeout(listenForNotifications, 2000);
        });
    }

    cardsLoop(0);
    recordingTopicsLoop(0);
    listenForNotifications();
  </script>
</body>
</html>";
    }
}
