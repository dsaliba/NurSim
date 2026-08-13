using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
//Generated with Chat GPT
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

    // All orchestrator-* sub-channels are merged into one "orchestrator" DataChannel
    // so the browser only needs 1 persistent connection instead of 5+ (Chrome allows
    // only 6 simultaneous connections per origin — every extra pending long-poll
    // connection steals a slot that POST requests need).
    private readonly object orcStateLock = new object();
    private readonly Dictionary<string, string> orcState = new Dictionary<string, string>();

    // ── Pending card initialisation store ────────────────────────────────────
    // Persists values to PlayerPrefs so they survive play-mode restarts.
    // Keyed by card title.  Values are applied automatically when a card with the
    // matching title registers, and to existing cards if StorePendingInit is called
    // while they are already in the list.
    private const string PendingInitPrefix = "IONA_PI_";

    /// <summary>
    /// Cache a raw value for the card titled <paramref name="cardTitle"/>.
    /// Survives play-mode restarts via PlayerPrefs.
    /// If a card with that title is already registered, the value is applied to it
    /// immediately (and the cards channel is republished so the browser pre-fills).
    /// </summary>
    public void StorePendingInit(string cardTitle, string rawValue)
    {
        if (string.IsNullOrEmpty(cardTitle)) return;
        PlayerPrefs.SetString(PendingInitPrefix + cardTitle, rawValue ?? "");
        PlayerPrefs.Save();

        // Apply immediately to any already-registered matching card (main-thread safe).
        HTMLDashCard existing = null;
        lock (cardsLock)
            existing = cards.FirstOrDefault(c =>
                string.Equals(GetCardTitle(c), cardTitle, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            ApplyPendingToCard(existing, rawValue ?? "");
            BumpCardsVersionAndFlush();
        }
    }

    /// <summary>Remove a stored pending init without applying it.</summary>
    public void ClearPendingInit(string cardTitle)
    {
        PlayerPrefs.DeleteKey(PendingInitPrefix + cardTitle);
        PlayerPrefs.Save();
    }

    /// <summary>
    /// Read and clear a stored pending init value. Returns null if none stored.
    /// </summary>
    private string ConsumePendingInit(string cardTitle)
    {
        string pkey = PendingInitPrefix + cardTitle;
        string val  = PlayerPrefs.GetString(pkey, "");
        if (string.IsNullOrEmpty(val)) return null;
        PlayerPrefs.DeleteKey(pkey);
        PlayerPrefs.Save();
        return val;
    }

    private static string GetCardTitle(HTMLDashCard c)
    {
        if (c is DropdownCard  dc) return dc.title;
        if (c is MultiFieldCard mf) return mf.title;
        if (c is DragOrderCard  dg) return dg.title;
        if (c is SliderCard     sc) return sc.title;
        if (c is ButtonCard     bc) return bc.title;
        return null;
    }

    // Apply a pending-init value to a card that is already registered.
    // DropdownCard:   sets currentValue and invokes callback (e.g. camera ROS publish).
    // DragOrderCard:  reorders the items array to match the stored order — does NOT
    //                 invoke the callback (geometry is already applied to the scene by
    //                 SimulationManager; the drag list just needs to show the right order).
    // Other types:    no-op for now (extend as needed).
    private static void ApplyPendingToCard(HTMLDashCard card, string raw)
    {
        if (card is DropdownCard dc)
        {
            dc.currentValue = raw;
            dc.Invoke(raw);
        }
        else if (card is DragOrderCard dg)
        {
            string[] order = raw.Split(new[]{ ';' }, StringSplitOptions.RemoveEmptyEntries);
            dg.items = ReorderItems(dg.items, order);
        }
    }

    private static string[] ReorderItems(string[] current, string[] order)
    {
        var result    = new List<string>();
        var remaining = new List<string>(current);
        foreach (string name in order)
        {
            int idx = remaining.FindIndex(s =>
                string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) { result.Add(remaining[idx]); remaining.RemoveAt(idx); }
        }
        result.AddRange(remaining);   // items not in the order stay at the end
        return result.ToArray();
    }

    public string localIP = "localhost";

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        // Reset card ID counter so IDs are stable even when domain-reload is disabled
        // (without this, IDs increment across play-mode restarts and the browser's
        // cached card IDs no longer match what the server registered).
        HTMLDashCard.nextID = 0;
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
                        // ── 1. Read body FIRST (Content-Length-bounded — non-blocking) ───────────
                        // fetch() with JSON.stringify() always sets Content-Length, so reading
                        // exactly that many bytes returns immediately without waiting for EOF.
                        // We MUST read before closing the response because response.Close()
                        // tears down the connection context, zeroing the input stream.
                        string content = "";
                        try
                        {
                            long len = request.ContentLength64;
                            if (len > 0 && len <= 1_048_576)           // known length ≤ 1 MiB
                            {
                                byte[] bodyBuf = new byte[(int)len];
                                int total = 0;
                                while (total < bodyBuf.Length)
                                {
                                    int n = request.InputStream.Read(bodyBuf, total, bodyBuf.Length - total);
                                    if (n <= 0) break;
                                    total += n;
                                }
                                content = Encoding.UTF8.GetString(bodyBuf, 0, total);
                            }
                            else if (len < 0)                          // chunked / unknown length
                            {
                                // Rare: fetch() almost always provides Content-Length.
                                // Read up to 64 KiB in one non-blocking pass.
                                byte[] tmp = new byte[65536];
                                int n = request.InputStream.Read(tmp, 0, tmp.Length);
                                content = Encoding.UTF8.GetString(tmp, 0, n);
                            }
                            // len == 0 → intentionally empty body; content stays ""
                        }
                        catch (Exception bodyEx)
                        {
                            Debug.LogWarning($"[HTTPDash] POST body-read error: {bodyEx.GetType().Name}: {bodyEx.Message}");
                        }

                        // ── 2. Send acknowledgement ───────────────────────────────────────────────
                        byte[] okBytes = Encoding.UTF8.GetBytes("OK");
                        response.StatusCode      = 200;
                        response.ContentType     = "text/plain";
                        response.ContentLength64 = okBytes.Length;
                        response.OutputStream.Write(okBytes, 0, okBytes.Length);
                        response.Close();

                        // ── 3. Parse card ID and dispatch ────────────────────────────────────────
                        string actionKey = path.Substring("/action/".Length);
                        if (!int.TryParse(actionKey, out int acID))
                        {
                            Debug.LogWarning($"[HTTPDash] POST /action/ — unparseable card ID '{actionKey}'");
                            continue;
                        }

                        HTMLDashCard matched;
                        lock (cardsLock)
                            matched = cards.FirstOrDefault(c => c.id == acID);

                        if (matched != null)
                        {
                            Debug.Log($"[HTTPDash] POST /action/{acID} → {matched.GetType().Name}, body='{content}'");
                            UnityMainThreadDispatcher.Enqueue(matched.Invoke, content);
                        }
                        else
                        {
                            string registeredIds;
                            lock (cardsLock)
                                registeredIds = string.Join(", ", cards.Select(c => $"{c.id}({c.GetType().Name})"));
                            Debug.LogWarning($"[HTTPDash] POST /action/{acID} — NO CARD FOUND. Registered: [{registeredIds}]");
                        }
                        continue; // response already closed above
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
                catch (ThreadAbortException) { }  // expected when Unity stops play-mode; don't log, don't Error Pause
                catch (HttpListenerException hle)
                {
                    // Suppress the flood of "connection reset" noise on graceful shutdown,
                    // but surface genuine errors while running so nothing is invisible.
                    if (running)
                        Debug.LogWarning($"[HTTPDash] HttpListenerException: {hle.Message} (code {hle.ErrorCode})");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[HTTPDash] Server error: {ex}");
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
                // Serve immediately when:
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
    /// All "orchestrator-*" names are automatically merged into a single composite
    /// "orchestrator" channel so the browser only holds one long-poll connection
    /// for all orchestrator data (staying under Chrome's 6-connection-per-origin limit).
    /// </summary>
    public void PublishChannel(string name, string json)
    {
        const string ORC_PREFIX = "orchestrator-";
        if (name.StartsWith(ORC_PREFIX))
        {
            string key = name.Substring(ORC_PREFIX.Length);
            string composite;
            lock (orcStateLock)
            {
                orcState[key] = json;                       // update this sub-channel
                var sb = new StringBuilder("{");
                bool first = true;
                foreach (var kv in orcState)
                {
                    if (!first) sb.Append(",");
                    sb.Append($"\"{Esc(kv.Key)}\":{kv.Value}");
                    first = false;
                }
                sb.Append("}");
                composite = sb.ToString();
            }
            GetOrCreateChannel("orchestrator").Publish(composite);
        }
        else
        {
            GetOrCreateChannel(name).Publish(json);
        }
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
        public string currentValue;   // set by StorePendingInit; serialized to "value" in JSON for browser pre-fill
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
            string valPart = !string.IsNullOrEmpty(currentValue) ? $",\"value\":\"{Esc(currentValue)}\"" : "";
            return $"{{\"type\":\"dropdown\",\"id\":{id},\"title\":\"{Esc(title)}\",\"buttonText\":\"{Esc(buttonText)}\",\"options\":[{opts}]{valPart}}}";
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
                    ? $",\"options\":[{string.Join(",", f.options.Select(o => $"\"{Esc(o)}\""  ))}]"
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

    // ── Slider card ──────────────────────────────────────────────────────

    /// <summary>
    /// A card that displays a range slider snapping between evenly-spaced tick marks.
    /// The current value is shown live; pressing the primary button POSTs the value to Unity.
    /// Optionally a secondary "save" button can be shown in the same card — it POSTs the
    /// current slider value prefixed with "save:" so Invoke() can dispatch to saveCallback.
    /// </summary>
    [System.Serializable]
    public class SliderCard : HTMLDashCard
    {
        public string title;
        public string buttonText;
        public string saveButtonText; // null = no secondary save button
        public float min;
        public float max;
        public float step;
        public float defaultValue;
        public Action<float> callback;
        public Action<float> saveCallback; // optional; fired by the secondary button

        public SliderCard(
            string title, string buttonText,
            float min, float max, float step, float defaultValue,
            Action<float> callback,
            string saveButtonText = null, Action<float> saveCallback = null)
        {
            this.id = HTMLDashCard.nextID++;
            this.title = title;
            this.buttonText = buttonText;
            this.saveButtonText = saveButtonText;
            this.min = min;
            this.max = max;
            this.step = step;
            this.defaultValue = defaultValue;
            this.callback = callback;
            this.saveCallback = saveCallback;
        }

        // Use InvariantCulture so floats always serialise with '.' not ','
        private static string F(float f) =>
            f.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public override string AsJson()
        {
            string json =
                $"{{\"type\":\"slider\",\"id\":{id},\"title\":\"{Esc(title)}\",\"buttonText\":\"{Esc(buttonText)}\"" +
                $",\"min\":{F(min)},\"max\":{F(max)},\"step\":{F(step)},\"defaultValue\":{F(defaultValue)}";
            if (!string.IsNullOrEmpty(saveButtonText))
                json += $",\"saveButtonText\":\"{Esc(saveButtonText)}\"";
            json += "}";
            return json;
        }

        public override void Invoke(string rawBody)
        {
            if (rawBody != null && rawBody.StartsWith("save:"))
            {
                string valStr = rawBody.Substring(5);
                if (float.TryParse(valStr,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float val))
                    saveCallback?.Invoke(val);
                else
                    Debug.LogWarning($"SliderCard '{title}': could not parse save value '{valStr}'");
            }
            else
            {
                if (float.TryParse(rawBody,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float val))
                    callback?.Invoke(val);
                else
                    Debug.LogWarning($"SliderCard '{title}': could not parse value '{rawBody}'");
            }
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

        /// <summary>
        /// Last known participant ID — pre-fills the input after re-renders.
        /// Set by RecordingManager when a recording starts.
        /// </summary>
        public string participantValue = "";

        /// <summary>
        /// Currently selected value per condition key, set by
        /// <see cref="RecordingManager.SetConditionValue"/> and synced in
        /// <see cref="RecordingManager.RebuildDashConditions"/>.
        /// Serialised as "value" in each condition's JSON so the browser
        /// can pre-select the correct option after a card re-render.
        /// </summary>
        public Dictionary<string, string> conditionValues = new Dictionary<string, string>();

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
                string valPart = conditionValues.TryGetValue(c.key, out var v) && !string.IsNullOrEmpty(v)
                                 ? $",\"value\":\"{Esc(v)}\"" : "";
                return $"{{\"key\":\"{Esc(c.key)}\",\"label\":\"{Esc(c.label)}\",\"options\":[{opts}]{valPart}}}";
            }));
            string participantPart = !string.IsNullOrEmpty(participantValue)
                                     ? $",\"participant\":\"{Esc(participantValue)}\"" : "";
            return $"{{\"type\":\"recording\",\"id\":{id},\"title\":\"{Esc(title)}\",\"conditions\":[{condJson}]{participantPart}}}";
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


// ── OrchestratorAction ────────────────────────────────────────────────────────

/// <summary>
/// One entry from the API geometry_sequence — describes a single task sheet
/// in the order the participant will encounter it.
/// </summary>
[System.Serializable]
public class OrchestratorGeometry
{
    public int    sheetNumber;
    public string label;
    public string code;
}

/// <summary>
/// Deserialised body from the Orchestrator card's POST /action/{id}.
/// All fields map 1:1 to the JSON the dashboard JS sends.
/// </summary>
[System.Serializable]
public class OrchestratorAction
{
    /// <summary>"fetch" | "apply" | "apply_condition" | "clear"</summary>
    public string action;
    /// <summary>Machine API key — stored only in browser cookie, never logged.</summary>
    public string apiKey;
    /// <summary>True when the researcher has ticked the "Dev server" checkbox.</summary>
    public bool   useDevServer;
    /// <summary>Session ID selected in the dropdown (for action "apply").</summary>
    public string sessionId;
    /// <summary>Condition ID clicked in the timeline (for action "apply_condition").</summary>
    public string conditionId;
    /// <summary>API interface_condition code, e.g. "gamepad_robot" (for action "apply_condition").</summary>
    public string interfaceCondition;
    /// <summary>API viewpoint label, e.g. "side" (for action "apply_condition" — used for camera selection).</summary>
    public string viewpoint;
    /// <summary>Position of this condition within its block: 1 = first trial (Training Sheet on), 2/3 = subsequent.</summary>
    public int    viewpointPosition;
    /// <summary>Ordered geometry/sheet sequence for this condition (for action "apply_condition").</summary>
    public OrchestratorGeometry[] geometries;
    /// <summary>API environment_type, e.g. "physical_robot" (for action "apply_condition" — used for scene mapping).</summary>
    public string environmentType;
}

// ── OrchestratorCard ─────────────────────────────────────────────────────────

/// <summary>
/// Dashboard card that exposes the Orchestrator session-fetch / apply flow.
/// Renders as type "orchestrator" in JSON; the JS in GenerateDashboardHtml
/// handles it separately from the generic card types.
/// </summary>
[System.Serializable]
public class OrchestratorCard : HTMLDashCard
{
    public string title          = "Orchestrator";
    public string productionUrl  = "https://fittsteleopstudy.org/api/v1";
    public string developmentUrl = "http://127.0.0.1:8000/api/v1";
    public System.Action<OrchestratorAction> callback;

    public OrchestratorCard(System.Action<OrchestratorAction> callback,
                             string prodUrl = null, string devUrl = null)
    {
        this.id       = HTMLDashCard.nextID++;
        this.callback = callback;
        if (prodUrl != null) productionUrl  = prodUrl;
        if (devUrl  != null) developmentUrl = devUrl;
    }

    public override string AsJson() =>
        $"{{\"type\":\"orchestrator\",\"id\":{id},\"title\":\"{Esc(title)}\"," +
        $"\"prodUrl\":\"{Esc(productionUrl)}\",\"devUrl\":\"{Esc(developmentUrl)}\"}}";

    public override void Invoke(string rawBody)
    {
        Debug.Log($"[HTTPDash] OrchestratorCard.Invoke called, body length={rawBody?.Length ?? -1}");

        if (string.IsNullOrEmpty(rawBody))
        {
            Debug.LogWarning("[HTTPDash] OrchestratorCard.Invoke: empty body, ignoring.");
            return;
        }

        OrchestratorAction action;
        try { action = JsonUtility.FromJson<OrchestratorAction>(rawBody); }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[HTTPDash] OrchestratorCard: failed to parse body: {e.Message}\nBody: {rawBody}");
            return;
        }

        if (action == null)
        {
            Debug.LogWarning("[HTTPDash] OrchestratorCard: JsonUtility returned null.");
            return;
        }

        // IMPORTANT: never log action.apiKey — it must not appear in any log file.
        Debug.Log($"[HTTPDash] OrchestratorCard dispatching: action='{action.action}'" +
                  $" sessionId='{action.sessionId}'" +
                  $" conditionId='{action.conditionId}'" +
                  $" interface='{action.interfaceCondition}'" +
                  $" viewpoint='{action.viewpoint}'" +
                  $" viewpointPosition={action.viewpointPosition}");

        // Scene / interface / sheet-order setup is handled entirely by
        // OrchestratorClient.ApplyConditionById(), which has direct access to the
        // full C# schedule (including environment_type and correct sheet_label values)
        // and calls SimulationManager.ApplyCondition() with a resolved target scene name.
        // Nothing to do here except dispatch to the registered callback.
        callback?.Invoke(action);
    }

    private static string Esc(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
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
        var card = new DropdownCard(title, buttonText, options, callback);
        string pending = ConsumePendingInit(title);
        if (!string.IsNullOrEmpty(pending))
            card.currentValue = pending;
        lock (cardsLock) { cards.Add(card); }
        BumpCardsVersionAndFlush();
        // Invoke callback AFTER the card is registered and the channel is flushed so the
        // browser pre-fills the correct value before any side-effect fires.
        if (!string.IsNullOrEmpty(pending))
            card.Invoke(pending);
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
        // If a pending condition order is cached, pre-sort items to match it.
        // The callback is NOT invoked — SimulationManager already applied the geometry
        // sequence to the scene; we just want the drag list to reflect the same order.
        string pending = ConsumePendingInit(title);
        if (!string.IsNullOrEmpty(pending))
        {
            string[] order = pending.Split(new[]{ ';' }, StringSplitOptions.RemoveEmptyEntries);
            items = ReorderItems(items, order);
        }
        var card = new DragOrderCard(title, buttonText, items, callback);
        lock (cardsLock) { cards.Add(card); }
        BumpCardsVersionAndFlush();
        return card;
    }

    /// <summary>
    /// Register a tick-snapping slider card.
    /// </summary>
    public SliderCard RegisterSlider(
        string title, string buttonText,
        float min, float max, float step, float defaultValue,
        Action<float> callback,
        string saveButtonText = null, Action<float> saveCallback = null)
    {
        var card = new SliderCard(title, buttonText, min, max, step, defaultValue,
                                  callback, saveButtonText, saveCallback);
        lock (cardsLock) { cards.Add(card); }
        BumpCardsVersionAndFlush();
        return card;
    }

    /// <summary>
    /// Call after mutating a card object in place to re-publish the cards channel.
    /// </summary>

    public OrchestratorCard RegisterOrchestratorCard(System.Action<OrchestratorAction> callback,
                                                      string prodUrl = null, string devUrl = null)
{
    var card = new OrchestratorCard(callback, prodUrl, devUrl);
    lock (cardsLock) { cards.Add(card); }
    BumpCardsVersionAndFlush();
    return card;
}

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

    // ── Page shell ───────────────────────────────────────────────────────────────

    private string GenerateDashboardHtml()
{
    return @"<!DOCTYPE html>
<html data-theme='light'>
<head>
  <meta charset='UTF-8' />
  <title>NurSim Unity Dashboard</title>
  <style>
    :root {
      --bg:            #E8E8EC;
      --surface:       #FFFFFF;
      --raised:        #F2F2F5;
      --border:        rgba(0,0,0,0.09);
      --border-mid:    rgba(0,0,0,0.14);
      --text:          #18181B;
      --text-2:        #52525B;
      --accent:        #B91C3A;
      --accent-h:      #941529;
      --accent-glow:   rgba(185,28,58,0.10);
      --success:       #166534;
      --header-bg:     #FFFFFF;
      --tl-bg:         #F5F5F8;
      --tl-border:     rgba(0,0,0,0.10);
    }
    [data-theme='dark'] {
      --bg:            #141416;
      --surface:       #1E1E22;
      --raised:        #28282E;
      --border:        rgba(255,255,255,0.06);
      --border-mid:    rgba(255,255,255,0.11);
      --text:          #E2E2E6;
      --text-2:        #9494A2;
      --accent:        #C4364F;
      --accent-h:      #A82B41;
      --accent-glow:   rgba(196,54,79,0.13);
      --success:       #2D6A46;
      --header-bg:     #1E1E22;
      --tl-bg:         #1A1A1F;
      --tl-border:     rgba(255,255,255,0.07);
    }

    *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }

    body {
      font-family: -apple-system, 'Segoe UI', system-ui, sans-serif;
      font-size: 14px;
      background: var(--bg);
      color: var(--text);
      height: 100vh;
      display: flex;
      flex-direction: column;
      overflow: hidden;
      transition: background 0.22s, color 0.22s;
    }

    /* ── Header ─────────────────────────────── */
    header {
      display: flex;
      align-items: center;
      height: 50px;
      padding: 0 1.1rem;
      gap: 0.75rem;
      background: var(--header-bg);
      border-bottom: 1px solid var(--border-mid);
      flex-shrink: 0;
      transition: background 0.22s, border-color 0.22s;
    }
    .logo-wrap {
      display: flex; align-items: center;
      background: #fff; border-radius: 5px; padding: 2px 7px; flex-shrink: 0;
    }
    [data-theme='light'] .logo-wrap { background: transparent; padding: 0; }
    header img { height: 25px; display: block; }
    .h-title {
      flex: 1; font-size: 0.88em; font-weight: 600;
      color: var(--text); letter-spacing: -0.01em;
    }
    .h-tag {
      font-size: 0.67em; font-weight: 500; color: var(--text-2);
      background: var(--raised); border: 1px solid var(--border-mid);
      border-radius: 4px; padding: 0.18em 0.52em;
    }
    .theme-btn {
      width: 30px; height: 30px; border-radius: 5px;
      border: 1px solid var(--border-mid); background: transparent;
      color: var(--text-2); cursor: pointer;
      display: flex; align-items: center; justify-content: center;
      font-size: 0.85em; transition: background 0.12s, color 0.12s;
    }
    .theme-btn:hover { background: var(--raised); color: var(--text); }

    /* ── Outer layout ────────────────────────── */
    .outer {
      display: flex; flex-direction: column; flex: 1; overflow: hidden;
    }
    .shell-cols { display: flex; flex: 1; overflow: hidden; }

    /* ── Columns ─────────────────────────────── */
    .col {
      display: flex; flex-direction: column; overflow: hidden;
      border-right: 1px solid var(--border-mid); transition: border-color 0.22s;
    }
    .col:last-child { border-right: none; }
    .col-controls      { flex: 1 1 0; min-width: 240px; }
    .col-recording     { flex: 0 0 295px; }
    .col-notifications { flex: 0 0 245px; }

    /* ── Column header ───────────────────────── */
    .col-head {
      height: 36px; padding: 0 0.55rem 0 0.9rem;
      display: flex; align-items: center; gap: 0.35rem;
      background: var(--raised); border-bottom: 1px solid var(--border-mid);
      flex-shrink: 0; transition: background 0.22s;
    }
    .col-label {
      font-size: 0.66em; font-weight: 700;
      text-transform: uppercase; letter-spacing: 0.1em; color: var(--text-2);
    }
    .badge {
      margin-left: auto; font-size: 0.62em; font-weight: 700;
      background: var(--accent); color: #fff; border-radius: 99px;
      padding: 0.12em 0.48em; display: none;
    }

    /* ── Grid column-count picker ────────────── */
    .gcol-picker { display: flex; gap: 2px; margin-left: auto; }
    .gcol-btn {
      width: 21px; height: 21px;
      font-size: 0.7em; font-weight: 600; font-family: inherit;
      border: 1px solid var(--border-mid); border-radius: 3px;
      background: transparent; color: var(--text-2); cursor: pointer;
      display: flex; align-items: center; justify-content: center;
      transition: background 0.1s, color 0.1s, border-color 0.1s;
    }
    .gcol-btn:hover { background: var(--raised); color: var(--text); }
    .gcol-btn.active { background: var(--accent); color: #fff; border-color: var(--accent); }

    /* ── Grid resize ruler ───────────────────── */
    .grid-ruler {
      height: 9px; flex-shrink: 0;
      background: var(--raised);
      border-bottom: 1px solid var(--border-mid);
      position: relative; overflow: visible; transition: background 0.22s;
    }
    .ruler-handle {
      position: absolute; top: -2px;
      height: calc(100% + 4px); width: 11px; margin-left: -5.5px;
      cursor: col-resize; z-index: 10;
      display: flex; align-items: center; justify-content: center;
    }
    .ruler-handle::after {
      content: ''; width: 2px; height: 100%;
      background: var(--border-mid); border-radius: 1px;
      transition: width 0.1s, background 0.1s;
    }
    .ruler-handle:hover::after, .ruler-handle.resizing::after {
      width: 3px; background: var(--accent);
    }

    /* ── Controls grid ───────────────────────── */
    .ctrl-list {
      flex: 1; overflow-y: auto;
      display: grid; align-content: start;
      gap: 1px; background: var(--border-mid); transition: background 0.22s;
    }
    .ctrl-list::-webkit-scrollbar { width: 4px; }
    .ctrl-list::-webkit-scrollbar-thumb { background: var(--border-mid); border-radius: 2px; }

    .ctrl-row {
      background: var(--surface); padding: 0.68rem 0.78rem 0.72rem;
      position: relative; transition: background 0.1s; min-width: 0;
    }
    .ctrl-row.row-dragging { opacity: 0.28; }
    .ctrl-row.row-over { outline: 2px solid var(--accent); outline-offset: -2px; z-index: 1; }

    .row-drag {
      position: absolute; top: 6px; right: 7px;
      cursor: grab; color: var(--text-2); opacity: 0;
      transition: opacity 0.12s; font-size: 0.82em; line-height: 1;
      padding: 2px 3px; border-radius: 3px; user-select: none;
    }
    .ctrl-row:hover .row-drag { opacity: 0.45; }
    .row-drag:active { cursor: grabbing; opacity: 0.9 !important; }

    .row-title {
      font-size: 0.7em; font-weight: 700;
      text-transform: uppercase; letter-spacing: 0.05em; color: var(--text-2);
      margin-bottom: 0.4rem; line-height: 1.3;
      padding-right: 1.4rem;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }

    /* ── Shared form controls ─────────────────── */
    input[type='text'], input[type='password'], select {
      width: 100%; padding: 0.44em 0.6em; font-size: 0.875em;
      font-family: inherit; color: var(--text);
      background: var(--raised); border: 1px solid var(--border-mid);
      border-radius: 5px; outline: none;
      -webkit-appearance: none; appearance: none;
      transition: border-color 0.12s, box-shadow 0.12s, background 0.22s, color 0.22s;
    }
    input[type='text']:focus, input[type='password']:focus, select:focus {
      border-color: var(--accent); box-shadow: 0 0 0 2px var(--accent-glow);
    }
    select {
      background-image: url(""data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='9' height='5'%3E%3Cpath d='M0 0l4.5 5L9 0z' fill='%23888'/%3E%3C/svg%3E"");
      background-repeat: no-repeat; background-position: right 0.62em center;
      padding-right: 1.75em; cursor: pointer;
    }

    .btn {
      display: block; width: 100%; margin-top: 0.48rem;
      padding: 0.44em 0.85em; font-size: 0.82em; font-weight: 600;
      font-family: inherit; border: none; border-radius: 5px; cursor: pointer;
      transition: background 0.12s, filter 0.12s, transform 0.07s;
    }
    .btn:active { transform: scale(0.975); }
    .btn-primary { background: var(--accent); color: #fff; }
    .btn-primary:hover { background: var(--accent-h); }
    .btn-ghost {
      background: transparent; color: var(--text-2);
      border: 1px solid var(--border-mid); margin-top: 0.28rem;
    }
    .btn-ghost:hover { background: var(--raised); color: var(--text); }

    .row-lbl {
      display: block; font-size: 0.78em; font-weight: 500; color: var(--text-2);
      margin-top: 0.48rem; margin-bottom: 0.18rem;
    }
    .row-lbl:first-of-type { margin-top: 0; }

    /* ── Slider ──────────────────────────────── */
    .slider-val {
      font-size: 1.28em; font-weight: 700; color: var(--accent);
      letter-spacing: -0.02em; line-height: 1; display: block; margin-bottom: 0.05rem;
    }
    input[type='range'] {
      -webkit-appearance: none; appearance: none;
      width: 100%; height: 3px; border-radius: 2px;
      background: var(--border-mid); border: none; padding: 0;
      outline: none; cursor: pointer; margin: 0.38rem 0 0.12rem;
      transition: background 0.22s;
    }
    input[type='range']::-webkit-slider-thumb {
      -webkit-appearance: none; width: 15px; height: 15px; border-radius: 50%;
      background: var(--accent); cursor: pointer;
      border: 2px solid var(--surface); box-shadow: 0 1px 3px rgba(0,0,0,0.22);
    }
    input[type='range']::-moz-range-thumb {
      width: 15px; height: 15px; border-radius: 50%;
      background: var(--accent); cursor: pointer; border: 2px solid var(--surface);
    }
    .slider-ends { display: flex; justify-content: space-between; font-size: 0.69em; color: var(--text-2); }

    /* ── DragOrderCard ───────────────────────── */
    .drag-list { list-style: none; display: flex; flex-direction: column; gap: 0.2rem; }
    .drag-item {
      display: flex; align-items: center; gap: 0.35rem;
      padding: 0.32rem 0.42rem;
      background: var(--raised); border: 1px solid var(--border);
      border-radius: 4px; user-select: none; transition: background 0.1s, border-color 0.1s, opacity 0.12s;
    }
    .drag-item.dragging  { opacity: 0.28; }
    .drag-item.drag-over { border-color: var(--accent); background: var(--accent-glow); }
    .drag-handle { cursor: grab; color: var(--text-2); opacity: 0.4; font-size: 0.95em; flex-shrink: 0; }
    .drag-handle:active { cursor: grabbing; }
    .drag-check  { flex-shrink: 0; accent-color: var(--accent); cursor: pointer; }
    .drag-lbl    { flex: 1; font-size: 0.8em; color: var(--text); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .drag-idx    { font-size: 0.67em; color: var(--text-2); flex-shrink: 0; min-width: 1.25em; text-align: right; }

    /* ── Orchestrator card ───────────────────── */
    .orc-row {
      display: flex; gap: 0.3rem; align-items: center; margin-bottom: 0.3rem;
    }
    .orc-row input[type='password'] { flex: 1; margin-bottom: 0; }
    .orc-dev-label {
      display: flex; align-items: center; gap: 0.22rem; flex-shrink: 0;
      font-size: 0.75em; color: var(--text-2); white-space: nowrap; cursor: pointer;
    }
    .orc-dev-label input { width: auto; accent-color: var(--accent); }
    .orc-status {
      font-size: 0.73em; margin-top: 0.25rem; line-height: 1.35;
      min-height: 1.3em; color: var(--text-2); word-break: break-word;
    }
    .orc-status.ok      { color: var(--success); }
    .orc-status.error   { color: var(--accent); }
    .orc-status.loading { color: var(--text-2); font-style: italic; }
    .orc-session-label {
      font-size: 0.68em; color: var(--text-2); margin-top: 0.25rem;
      font-weight: 600; letter-spacing: 0.02em; text-transform: uppercase;
    }
    .orc-btn-row { display: flex; gap: 0.3rem; margin-top: 0.3rem; }
    .orc-btn-row .btn { flex: 1; margin-top: 0; }
    .orc-connect-row { display: flex; gap: 0.3rem; align-items: center; margin-top: 0.3rem; }
    .orc-connect-row .btn { flex: 1; margin-top: 0; }
    .orc-dot {
      width: 10px; height: 10px; border-radius: 50%; flex-shrink: 0;
      background: var(--border-mid); transition: background 0.3s;
    }
    .orc-dot.connecting  { background: #ca8a04; }
    .orc-dot.connected   { background: #16a34a; }
    .orc-dot.failed      { background: var(--accent); }

    /* ── Recording column ────────────────────── */
    .rec-scroll { flex: 1; overflow-y: auto; padding: 0.75rem; }
    .rec-scroll::-webkit-scrollbar { width: 4px; }
    .rec-scroll::-webkit-scrollbar-thumb { background: var(--border-mid); border-radius: 2px; }
    .rec-form {
      background: var(--surface); border: 1px solid var(--border-mid);
      border-radius: 7px; overflow: hidden;
      transition: background 0.22s, border-color 0.22s;
    }
    .rec-form-head {
      padding: 0.55rem 0.8rem; border-bottom: 1px solid var(--border);
      display: flex; align-items: center; gap: 0.42rem;
    }
    .rec-form-head h3 { font-size: 0.8em; font-weight: 600; color: var(--text); }
    .rec-dot { width: 7px; height: 7px; border-radius: 50%; background: var(--success); flex-shrink: 0; }
    .rec-form-body { padding: 0.7rem 0.8rem; }
    .topic-row-tools { display: flex; gap: 0.25rem; flex-wrap: wrap; margin-top: 0.25rem; }
    .chip {
      flex: 1 1 auto; padding: 0.26em 0.42em;
      font-size: 0.7em; font-weight: 500; font-family: inherit;
      border-radius: 4px; border: 1px solid var(--border-mid);
      background: var(--raised); color: var(--text-2); cursor: pointer;
      white-space: nowrap; transition: background 0.1s, color 0.1s;
    }
    .chip:hover { background: var(--border-mid); color: var(--text); }
    .chip-accent { color: var(--accent); border-color: var(--accent); }
    .chip-accent:hover { background: var(--accent-glow); }
    .topic-box {
      margin-top: 0.3rem; max-height: 160px; overflow-y: auto;
      background: var(--raised); border: 1px solid var(--border-mid);
      border-radius: 5px; transition: background 0.22s;
    }
    .topic-box::-webkit-scrollbar { width: 3px; }
    .topic-box::-webkit-scrollbar-thumb { background: var(--border-mid); }
    .t-row {
      display: flex; align-items: center; gap: 0.5rem;
      padding: 0.28rem 0.55rem; border-bottom: 1px solid var(--border);
      cursor: pointer; transition: background 0.1s;
    }
    .t-row:last-child { border-bottom: none; }
    .t-row:hover { background: var(--surface); }
    .t-row input { flex-shrink: 0; accent-color: var(--accent); }
    .t-name { flex: 1; font-size: 0.79em; font-family: 'SF Mono','Consolas',monospace; color: var(--text); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .t-type { font-size: 0.72em; color: var(--text-2); white-space: nowrap; }
    .t-empty { padding: 0.7rem; color: var(--text-2); font-size: 0.77em; font-style: italic; text-align: center; }
    .rec-btns { display: flex; gap: 0.35rem; margin-top: 0.55rem; }
    .rec-btns .btn { flex: 1; margin-top: 0; }
    .btn-start { background: var(--success); color: #fff; }
    .btn-start:hover { filter: brightness(1.12); }
    .btn-stop  { background: var(--accent); color: #fff; }
    .btn-stop:hover { background: var(--accent-h); }
    .row-lbl-rec {
      display: block; font-size: 0.78em; font-weight: 500; color: var(--text-2);
      margin-top: 0.5rem; margin-bottom: 0.2rem;
    }
    .row-lbl-rec:first-child { margin-top: 0; }

    /* ── Notifications ───────────────────────── */
    .notif-scroll {
      flex: 1; overflow-y: auto; padding: 0.65rem;
      display: flex; flex-direction: column; gap: 0.38rem;
    }
    .notif-scroll::-webkit-scrollbar { width: 4px; }
    .notif-scroll::-webkit-scrollbar-thumb { background: var(--border-mid); border-radius: 2px; }
    .notif-empty {
      flex: 1; display: flex; flex-direction: column;
      align-items: center; justify-content: center;
      color: var(--text-2); font-size: 0.75em; gap: 0.3rem; opacity: 0.45; text-align: center;
    }
    .notif-empty-ico { font-size: 1.5em; }
    .notif-item {
      border-radius: 6px; padding: 0.55rem 0.65rem; color: #fff;
      animation: fadeIn 0.18s ease;
    }
    @keyframes fadeIn { from { opacity: 0; transform: translateY(-4px); } to { opacity: 1; transform: translateY(0); } }
    .notif-item h3 { font-size: 0.78em; font-weight: 600; margin-bottom: 0.18em; }
    .notif-item p  { font-size: 0.73em; opacity: 0.83; line-height: 1.4; }

    /* ── Schedule timeline ───────────────────── */
    .timeline-panel {
      flex-shrink: 0;
      background: var(--tl-bg);
      border-top: 1px solid var(--tl-border);
      display: flex; flex-direction: column;
      transition: background 0.22s, border-color 0.22s;
      max-height: 170px;
    }
    .timeline-panel.collapsed { max-height: 33px; }
    .timeline-head {
      height: 33px; padding: 0 0.7rem;
      display: flex; align-items: center; gap: 0.6rem;
      border-bottom: 1px solid var(--tl-border); flex-shrink: 0;
      background: var(--raised); transition: background 0.22s;
    }
    .tl-session-label {
      font-size: 0.68em; color: var(--text-2); font-weight: 500;
      overflow: hidden; text-overflow: ellipsis; white-space: nowrap; flex: 1;
    }
    .tl-session-label strong { color: var(--text); }
    .tl-collapse-btn {
      flex-shrink: 0; background: transparent; border: none; cursor: pointer;
      color: var(--text-2); font-size: 0.7em; padding: 2px 5px;
      border-radius: 3px; transition: background 0.1s;
    }
    .tl-collapse-btn:hover { background: var(--border-mid); color: var(--text); }
    .timeline-scroll {
      flex: 1; overflow-x: auto; overflow-y: hidden;
      display: flex; gap: 1px;
      background: var(--border-mid);
      padding: 0;
    }
    .timeline-scroll::-webkit-scrollbar { height: 4px; }
    .timeline-scroll::-webkit-scrollbar-thumb { background: var(--border-mid); border-radius: 2px; }
    .tl-block {
      background: var(--surface);
      min-width: 210px; flex: 1 1 0;
      display: flex; flex-direction: column;
    }
    .tl-block-head {
      padding: 0.22rem 0.6rem; background: var(--raised);
      border-bottom: 1px solid var(--tl-border);
      font-size: 0.63em; font-weight: 700; text-transform: uppercase;
      letter-spacing: 0.06em; color: var(--text-2);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .tl-condition {
      display: flex; align-items: center; gap: 0.4rem;
      padding: 0.3rem 0.6rem; cursor: pointer;
      border-bottom: 1px solid var(--border);
      transition: background 0.1s;
    }
    .tl-condition:last-child { border-bottom: none; }
    .tl-condition:hover { background: var(--raised); }
    .tl-condition.active { background: var(--accent-glow); }
    .tl-vp {
      font-size: 0.67em; font-weight: 700; min-width: 36px; text-align: center;
      background: var(--raised); border: 1px solid var(--border-mid);
      border-radius: 3px; padding: 0.1em 0.32em; flex-shrink: 0;
      text-transform: uppercase; color: var(--text-2);
    }
    .tl-condition.active .tl-vp {
      background: var(--accent); color: #fff; border-color: var(--accent);
    }
    .tl-geoms { display: flex; gap: 2px; flex-wrap: nowrap; overflow: hidden; }
    .tl-geom {
      font-size: 0.62em; font-weight: 600;
      background: var(--raised); border: 1px solid var(--border-mid);
      border-radius: 3px; padding: 0.08em 0.3em; color: var(--text);
      white-space: nowrap; flex-shrink: 0;
    }
    .tl-arrow {
      font-size: 0.55em; color: var(--text-2); flex-shrink: 0; line-height: 1;
    }
    .timeline-empty {
      padding: 0.9rem 1rem; color: var(--text-2); font-size: 0.75em;
      font-style: italic; text-align: center;
    }

    /* ── Save toast ──────────────────────────── */
    .save-toast {
      position: fixed; bottom: 1.1rem; left: 50%;
      transform: translateX(-50%) translateY(6px);
      background: var(--text); color: var(--surface);
      font-size: 0.75em; padding: 0.38em 0.9em;
      border-radius: 99px; opacity: 0;
      transition: opacity 0.18s, transform 0.18s;
      pointer-events: none; z-index: 200;
    }
    .save-toast.show { opacity: 1; transform: translateX(-50%) translateY(0); }
  </style>
</head>
<body>
  <header>
    <div class='logo-wrap'>
      <img src='https://labs.wpi.edu/hiro/wp-content/uploads/sites/45/2016/03/Hiro_Logo_WPITheme-300x108.png' alt='HIRO Logo' />
    </div>
    <span class='h-title'>NurSim Dashboard</span>
    <span class='h-tag'>Unity</span>
    <button class='theme-btn' id='themeToggle' title='Toggle dark mode'>&#9790;</button>
  </header>

  <div class='outer'>
    <div class='shell-cols'>
      <!-- Controls column -->
      <div class='col col-controls'>
        <div class='col-head'>
          <span class='col-label'>Controls</span>
          <div class='gcol-picker' id='gcol-picker'></div>
        </div>
        <div class='grid-ruler' id='grid-ruler'></div>
        <div class='ctrl-list' id='card-container'></div>
      </div>

      <!-- Recording column -->
      <div class='col col-recording'>
        <div class='col-head'><span class='col-label'>Recording</span></div>
        <div class='rec-scroll' id='recording-container'></div>
      </div>

      <!-- Notifications column -->
      <div class='col col-notifications'>
        <div class='col-head'>
          <span class='col-label'>Notifications</span>
          <span class='badge' id='notif-count'>0</span>
        </div>
        <div class='notif-scroll' id='notif-list'>
          <div class='notif-empty' id='notif-empty'>
            <span class='notif-empty-ico'>&#128276;</span>
            <span>No notifications yet</span>
          </div>
        </div>
      </div>
    </div>

    <!-- Schedule timeline — appears at bottom, collapsed by default -->
    <div class='timeline-panel collapsed' id='timeline-panel'>
      <div class='timeline-head'>
        <span class='col-label'>Schedule Timeline</span>
        <span class='tl-session-label' id='tl-session-label'>No session loaded</span>
        <button class='tl-collapse-btn' id='tl-collapse-btn'>&#9650;</button>
      </div>
      <div id='timeline-body' style='flex:1;display:flex;overflow:hidden;'>
        <div class='timeline-empty' id='timeline-empty'>Apply a session in the Orchestrator card to see the schedule.</div>
        <div class='timeline-scroll' id='timeline-scroll' style='display:none'></div>
      </div>
    </div>
  </div>

  <div class='save-toast' id='save-toast'>Layout saved</div>

  <script>
    // ── Theme ─────────────────────────────────────────────────────────────────
    const html = document.documentElement;
    const themeBtn = document.getElementById('themeToggle');
    let isDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
    function applyTheme() {
      html.setAttribute('data-theme', isDark ? 'dark' : 'light');
      themeBtn.innerHTML = isDark ? '&#9728;' : '&#9790;';
    }
    applyTheme();
    themeBtn.addEventListener('click', () => { isDark = !isDark; applyTheme(); });

    let recordingTopics = [];
    let recordingSelected = {};
    let notifCount = 0;
    let activeConditionId = null;

    // ── Cookie helpers ────────────────────────────────────────────────────────
    function gc(name) {
      const m = document.cookie.match(new RegExp('(?:^|; )' + name + '=([^;]*)'));
      return m ? decodeURIComponent(m[1]) : null;
    }
    function sc(name, val) {
      document.cookie = `${name}=${encodeURIComponent(val)}; max-age=31536000; path=/`;
    }

    // ── Toast ─────────────────────────────────────────────────────────────────
    let toastTimer = null;
    function showToast(msg) {
      const t = document.getElementById('save-toast');
      t.textContent = msg; t.classList.add('show');
      clearTimeout(toastTimer);
      toastTimer = setTimeout(() => t.classList.remove('show'), 1600);
    }

    // ── Form-state cookie (non-recording fields only) ─────────────────────────
    const FORM_KEY = 'httpdash_form_state';

    function loadFormState() {
      try { const r = gc(FORM_KEY); return r ? JSON.parse(r) : {}; } catch(e) { return {}; }
    }
    function saveFormState() {
      const c = document.getElementById('card-container');
      if (!c) return;
      const state = loadFormState();
      c.querySelectorAll('select, input[type=""text""], input[type=""range""]').forEach(el => {
        if (el.id && !el.closest('.rec-form')) state[el.id] = el.value;
      });
      sc(FORM_KEY, JSON.stringify(state));
    }
    function restoreFormState() {
      const state = loadFormState();
      Object.keys(state).forEach(id => {
        const el = document.getElementById(id);
        if (el && !el.closest('.rec-form')) {
          el.value = state[id];
          // Sync slider display
          if (el.type === 'range') {
            const slv = document.getElementById('slv-' + id.replace('sl-', ''));
            if (slv) slv.textContent = parseFloat(state[id]).toFixed(stepDec(parseFloat(el.step)));
          }
        }
      });
    }
    // Auto-save on any change inside card-container
    document.addEventListener('change', e => {
      if (e.target.closest('#card-container')) saveFormState();
    });
    document.addEventListener('input', e => {
      if (e.target.type === 'range' && e.target.closest('#card-container')) saveFormState();
    });

    // ── Grid settings ─────────────────────────────────────────────────────────
    const GRID_KEY = 'httpdash_grid';
    let gridCols = 3;
    let gridWidths = [1, 1, 1];

    function loadGrid() {
      try {
        const raw = gc(GRID_KEY);
        if (raw) {
          const d = JSON.parse(raw);
          if (d.cols >= 1 && d.cols <= 6) gridCols = d.cols;
          gridWidths = (Array.isArray(d.widths) && d.widths.length === gridCols)
            ? d.widths : Array(gridCols).fill(1);
        }
      } catch(e) { gridWidths = Array(gridCols).fill(1); }
    }
    function saveGrid() { sc(GRID_KEY, JSON.stringify({ cols: gridCols, widths: gridWidths })); showToast('Layout saved'); }
    function applyGrid() {
      document.getElementById('card-container').style.gridTemplateColumns = gridWidths.map(w => w + 'fr').join(' ');
      buildRuler();
      document.querySelectorAll('.gcol-btn').forEach(b => b.classList.toggle('active', +b.dataset.n === gridCols));
    }
    function setColCount(n) {
      gridCols = n;
      const old = gridWidths.slice();
      gridWidths = Array.from({ length: n }, (_, i) => old[i] !== undefined ? old[i] : 1);
      const sum = gridWidths.reduce((a, b) => a + b, 0);
      gridWidths = gridWidths.map(w => (w / sum) * n);
      saveGrid(); applyGrid();
    }
    function buildColPicker() {
      const picker = document.getElementById('gcol-picker');
      picker.innerHTML = '';
      for (let n = 1; n <= 6; n++) {
        const btn = document.createElement('button');
        btn.className = 'gcol-btn' + (n === gridCols ? ' active' : '');
        btn.dataset.n = n; btn.textContent = n;
        btn.title = `${n} column${n > 1 ? 's' : ''}`;
        btn.onclick = () => setColCount(n);
        picker.appendChild(btn);
      }
    }
    function buildRuler() {
      const ruler = document.getElementById('grid-ruler');
      ruler.innerHTML = '';
      if (gridCols <= 1) return;
      const total = gridWidths.reduce((a, b) => a + b, 0);
      let cumPct = 0;
      gridWidths.slice(0, -1).forEach((w, i) => {
        cumPct += w / total;
        const handle = document.createElement('div');
        handle.className = 'ruler-handle';
        handle.style.left = (cumPct * 100) + '%';
        ruler.appendChild(handle);
        handle.addEventListener('mousedown', e => {
          e.preventDefault();
          const startX = e.clientX, rw = ruler.offsetWidth;
          const startWidths = [...gridWidths], frPerPx = total / rw, minFr = 0.15;
          handle.classList.add('resizing');
          document.body.style.cursor = 'col-resize';
          document.body.style.userSelect = 'none';
          function onMove(ev) {
            const dFr = (ev.clientX - startX) * frPerPx;
            const newA = startWidths[i] + dFr, newB = startWidths[i + 1] - dFr;
            if (newA < minFr || newB < minFr) return;
            gridWidths[i] = newA; gridWidths[i + 1] = newB;
            document.getElementById('card-container').style.gridTemplateColumns = gridWidths.map(v => v + 'fr').join(' ');
            const tot2 = gridWidths.reduce((a, b) => a + b, 0);
            let cum2 = 0;
            ruler.querySelectorAll('.ruler-handle').forEach((h, hi) => {
              cum2 += gridWidths[hi] / tot2; h.style.left = (cum2 * 100) + '%';
            });
          }
          function onUp() {
            handle.classList.remove('resizing');
            document.body.style.cursor = ''; document.body.style.userSelect = '';
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp); saveGrid();
          }
          document.addEventListener('mousemove', onMove);
          document.addEventListener('mouseup', onUp);
        });
      });
    }

    // ── Card order cookie ─────────────────────────────────────────────────────
    const ORDER_KEY = 'httpdash_ctrl_order';
    function getSavedOrder() { try { const r = gc(ORDER_KEY); return r ? JSON.parse(r) : []; } catch(e) { return []; } }
    function saveCurrentOrder() {
      const ids = Array.from(document.getElementById('card-container').querySelectorAll('.ctrl-row[data-cid]')).map(el => +el.dataset.cid);
      sc(ORDER_KEY, JSON.stringify(ids));
    }
    function applyOrder(cards) {
      const saved = getSavedOrder();
      if (!saved.length) return cards;
      const byId = {};
      cards.forEach(c => { byId[c.id] = c; });
      const ordered = [];
      saved.forEach(id => { if (byId[id] !== undefined) { ordered.push(byId[id]); delete byId[id]; } });
      cards.forEach(c => { if (byId[c.id] !== undefined) ordered.push(c); });
      return ordered;
    }

    // ── Row drag-to-reorder ───────────────────────────────────────────────────
    function initCtrlReorder(container) {
      let dragged = null, fromHandle = false;
      container.addEventListener('mousedown', e => { fromHandle = !!e.target.closest('.row-drag'); });
      container.addEventListener('dragstart', e => {
        if (!fromHandle) { e.preventDefault(); return; }
        dragged = e.target.closest('.ctrl-row[draggable]');
        if (!dragged) { e.preventDefault(); return; }
        dragged.classList.add('row-dragging'); e.dataTransfer.effectAllowed = 'move';
      });
      container.addEventListener('dragend', () => {
        if (dragged) { dragged.classList.remove('row-dragging'); saveCurrentOrder(); }
        container.querySelectorAll('.ctrl-row').forEach(el => el.classList.remove('row-over'));
        dragged = null; fromHandle = false;
      });
      container.addEventListener('dragover', e => {
        e.preventDefault(); if (!dragged) return;
        const target = e.target.closest('.ctrl-row[draggable]');
        if (!target || target === dragged) return;
        container.querySelectorAll('.ctrl-row').forEach(el => el.classList.remove('row-over'));
        target.classList.add('row-over');
        const r = target.getBoundingClientRect();
        const nx = (e.clientX - r.left) / r.width, ny = (e.clientY - r.top) / r.height;
        if (nx + ny < 1) container.insertBefore(dragged, target);
        else container.insertBefore(dragged, target.nextSibling);
      });
      container.addEventListener('dragleave', e => {
        const t = e.target.closest('.ctrl-row');
        if (t && t !== dragged) t.classList.remove('row-over');
      });
    }

    // ── DragOrderCard internal drag ───────────────────────────────────────────
    function initDragList(listId) {
      const list = document.getElementById(listId);
      if (!list) return;
      let dragged = null;
      list.addEventListener('dragstart', e => {
        e.stopPropagation();
        dragged = e.target.closest('.drag-item');
        if (!dragged) return;
        setTimeout(() => dragged && dragged.classList.add('dragging'), 0);
      });
      list.addEventListener('dragend', () => {
        if (dragged) dragged.classList.remove('dragging');
        list.querySelectorAll('.drag-item').forEach(el => el.classList.remove('drag-over'));
        dragged = null; refreshIdx(list);
      });
      list.addEventListener('dragover', e => {
        e.preventDefault(); if (!dragged) return;
        const t = e.target.closest('.drag-item');
        if (!t || t === dragged) return;
        list.querySelectorAll('.drag-item').forEach(el => el.classList.remove('drag-over'));
        t.classList.add('drag-over');
        const r = t.getBoundingClientRect();
        if (e.clientY < r.top + r.height / 2) list.insertBefore(dragged, t);
        else list.insertBefore(dragged, t.nextSibling);
      });
      list.addEventListener('dragleave', e => {
        const t = e.target.closest('.drag-item');
        if (t && t !== dragged) t.classList.remove('drag-over');
      });
    }
    function refreshIdx(list) {
      list.querySelectorAll('.drag-item').forEach((el, i) => {
        const ix = el.querySelector('.drag-idx');
        if (ix) ix.textContent = (i + 1) + '.';
      });
    }
    function stepDec(step) {
      const s = step.toString(), d = s.indexOf('.');
      return d < 0 ? 0 : s.length - d - 1;
    }
    function snap(v, min, max, step) {
      const s = Math.round((v - min) / step) * step + min;
      return Math.min(max, Math.max(min, +s.toFixed(10)));
    }

    const HANDLE = '<span class=""row-drag"" title=""Drag to reorder"">&#x2807;</span>';

    // ── Orchestrator card state ───────────────────────────────────────────────
    // API key stored separately so it is never merged into the generic form-state
    const ORC_KEY_COOKIE = 'iona_apikey';
    let orcCardIds         = [];  // tracks which card IDs are orchestrator type
    let orcCardUrls        = {};  // { cardId: { prod, dev } } — populated from card JSON
    let orcFetchedSessions = {};  // { session_id: raw API session object } — set by orcFetch
    let orcConditionData   = {};  // { condition_id: { labels, config, session, isFirstInBlock } } — set by parseScheduleFromApi

    function orcGetKey()       { return gc(ORC_KEY_COOKIE) || ''; }
    function orcSaveKey(k)     { sc(ORC_KEY_COOKIE, k); }

    function orcPost(cardId, payload) {
      fetch('/action/' + cardId, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      })
      .then(r => { if (!r.ok) console.error('[OrchestratorCard] POST failed:', r.status, r.statusText); })
      .catch(err => console.error('[OrchestratorCard] POST error:', err));
    }

    // Runs entirely in the browser — no Unity dispatch needed.
    // Requires the API server to have CORS configured for the Authorization header.
    function orcConnect(cardId) {
      const keyEl = document.getElementById('orc-key-' + cardId);
      const devEl = document.getElementById('orc-dev-' + cardId);
      const key   = (keyEl ? keyEl.value : '').trim();
      const dev   = devEl ? devEl.checked : false;
      if (!key) {
        setOrcConnectDot(cardId, 'failed');
        setOrcStatus('error', 'Enter an API key first.');
        return;
      }
      orcSaveKey(key);
      setOrcConnectDot(cardId, 'connecting');
      setOrcStatus('loading', 'Testing connection…');

      const urls = orcCardUrls[cardId] || {};
      const base = (dev ? urls.dev : urls.prod) || (dev
        ? 'http://127.0.0.1:8000/api/v1'
        : 'https://fittsteleopstudy.org/api/v1');
      const url  = base + '/sessions?schedule_available=true&page_size=1';

      console.log('[OrchestratorCard] Connect → GET', url);

      fetch(url, { headers: { 'Authorization': 'Api-Key ' + key } })
        .then(r => {
          console.log('[OrchestratorCard] Connect response: HTTP', r.status);
          if (r.ok) {
            setOrcConnectDot(cardId, 'connected');
            setOrcStatus('ok', 'Connected — API reachable at ' + base + '.');
          } else if (r.status === 401 || r.status === 403) {
            setOrcConnectDot(cardId, 'failed');
            setOrcStatus('error', 'Auth failed (HTTP ' + r.status + '). Check your API key.');
          } else {
            setOrcConnectDot(cardId, 'failed');
            setOrcStatus('error', 'Server error: HTTP ' + r.status + '.');
          }
        })
        .catch(err => {
          console.error('[OrchestratorCard] Connect error:', err);
          setOrcConnectDot(cardId, 'failed');
          setOrcStatus('error', 'Connection failed: ' + err.message);
        });
    }

    // Fetch sessions directly from the API in the browser (no Unity dispatch needed).
    // Populates the session dropdown and caches session metadata for Apply.
    function orcFetch(cardId) {
      const keyEl = document.getElementById('orc-key-' + cardId);
      const devEl = document.getElementById('orc-dev-' + cardId);
      const key   = (keyEl ? keyEl.value : '').trim();
      const dev   = devEl ? devEl.checked : false;
      if (!key) { setOrcStatus('error', 'Enter an API key first.'); return; }
      orcSaveKey(key);
      setOrcStatus('loading', 'Fetching sessions…');

      const urls = orcCardUrls[cardId] || {};
      const base = (dev ? urls.dev : urls.prod) || (dev
        ? 'http://127.0.0.1:8000/api/v1'
        : 'https://fittsteleopstudy.org/api/v1');
      const url  = base + '/sessions?schedule_available=true&page_size=100';

      console.log('[OrchestratorCard] Fetch → GET', url);

      fetch(url, { headers: { 'Authorization': 'Api-Key ' + key } })
        .then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
        .then(data => {
          const results = data.results || [];
          // Cache raw objects so Apply can use participant_id, study_name etc.
          orcFetchedSessions = {};
          results.forEach(s => { orcFetchedSessions[s.session_id] = s; });

          setOrcSessions(results.map(s => ({
            id:    s.session_id,
            label: `${s.participant_id} — ${s.study_name} (${s.workflow_status})`
          })));

          const total = data.count != null ? data.count : results.length;
          const msg = results.length < total
            ? `Loaded ${results.length} of ${total} sessions.`
            : `Loaded ${results.length} session${results.length !== 1 ? 's' : ''}.`;
          setOrcStatus('ok', msg);
          console.log('[OrchestratorCard] Sessions:', results.length);
        })
        .catch(err => {
          console.error('[OrchestratorCard] Fetch error:', err);
          setOrcStatus('error', 'Fetch failed: ' + err.message);
        });
    }

    // Maps the API schedule response (schema_version 1.0.0) to the shape
    // renderTimeline() expects, and simultaneously populates orcConditionData
    // so applyConditionLocally() can fill the recording card without a Unity round-trip.
    function parseScheduleFromApi(apiData, sessionMeta) {
      const sess   = apiData.session || {};
      const blocks = apiData.blocks  || [];
      orcConditionData = {};  // reset on every apply

      function ifaceLabel(code) {
        return (code || '').replace(/_/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
      }

      return {
        participantId: sess.participant_id || (sessionMeta && sessionMeta.participant_id) || '',
        studyName:     sess.study_name     || (sessionMeta && sessionMeta.study_name)     || '',
        sessionId:     sess.session_id     || (sessionMeta && sessionMeta.session_id)     || '',
        blocks: blocks.map(b => ({
          ordinal:       b.assigned_ordinal,
          interfaceCode: ifaceLabel(b.interface_condition),
          conditions:   (b.conditions || []).map(c => {
            const lbl = c.labels || {};
            const cfg = c.condition_configuration || {};
            // viewpoint_position 1 = first trial of this interface block → Training Sheet on
            orcConditionData[c.condition_id] = {
              labels:         lbl,
              config:         cfg,
              session:        sess,
              sessionMeta:    sessionMeta,
              isFirstInBlock: (cfg.viewpoint_position || 1) === 1
            };
            return {
              conditionId:    c.condition_id,
              interfaceLabel: lbl.interface_condition || '',
              viewpointLabel: lbl.viewpoint           || '',
              viewpoint:      lbl.viewpoint           || '',
              geometries:    (lbl.geometry_sequence || []).map(g => ({
                sheetNumber: g.sheet_number,
                label:       g.sheet_label || g.label || '',
                code:        g.code        || ''
              }))
            };
          })
        }))
      };
    }

    // Apply a condition's configuration directly in the browser:
    //   • pre-fills the recording card participant + condition dropdowns
    //     off for subsequent trials in the same block)
    // Unity is still notified via orcPost so it can load the game-side scene.
    function applyConditionLocally(conditionId) {
      const d = orcConditionData[conditionId];
      if (!d) { console.warn('[OrchestratorCard] No cached data for condition', conditionId); return; }

      const cfg  = d.config  || {};
      const lbl  = d.labels  || {};
      const sess = d.session || d.sessionMeta || {};

      // ── Participant ──────────────────────────────────────────────────────
      const participant = sess.participant_id || cfg.participant_id || '';
      document.querySelectorAll('[id^=""rp-""]').forEach(el => { el.value = participant; });

      // ── Recording condition dropdowns ────────────────────────────────────
      // Try to set each dropdown by matching its options against both the raw
      // API code (e.g. ""gamepad_robot"") and the human label (e.g. ""Gamepad Robot Control"").
      function trySetSelect(el, ...candidates) {
        for (const v of candidates) {
          if (!v) continue;
          // exact value match
          if (el.querySelector(`option[value=""${CSS.escape(v)}""]`)) { el.value = v; return; }
          // text-content match
          const byText = Array.from(el.options).find(o => o.text.trim() === v);
          if (byText) { el.value = byText.value; return; }
        }
      }

      // Interface condition  (key ending in ""-interface"")
      document.querySelectorAll('[id^=""rc-""][id$=""-interface""]').forEach(el =>
        trySetSelect(el, cfg.interface_condition, lbl.interface_condition));

      // Viewpoint / camera  (key ending in ""-viewpoint"" or ""-camera"")
      document.querySelectorAll('[id^=""rc-""][id$=""-viewpoint""], [id^=""rc-""][id$=""-camera""]').forEach(el =>
        trySetSelect(el, cfg.viewpoint, lbl.viewpoint));

      // ── Training Sheet topic ─────────────────────────────────────────────
      // Enabled on the first trial of each interface block (viewpoint_position=1),
      // disabled on subsequent trials so the sheet isn't recorded twice.
      const TRAINING = 'Training Sheet';
      Object.keys(recordingSelected).forEach(cardId => {
        if (d.isFirstInBlock) {
          // Add if the topic exists in the current list
          const exists = recordingTopics.some(t => t.name === TRAINING);
          if (exists) recordingSelected[cardId].add(TRAINING);
        } else {
          recordingSelected[cardId].delete(TRAINING);
        }
        renderTopicList(cardId);
      });

      saveFormState();
      console.log('[OrchestratorCard] Applied condition locally:', conditionId,
                  'interface:', cfg.interface_condition, 'viewpoint:', cfg.viewpoint,
                  'isFirstInBlock:', d.isFirstInBlock);
    }

    // ── Render cards ──────────────────────────────────────────────────────────
    function renderCards(list) {
      renderNormal(list.filter(c => c.type !== 'recording'));
      renderRecording(list.filter(c => c.type === 'recording'));
      restoreFormState();  // restore dropdowns/inputs from cookie after each render
      applyCardValues(list); // override with condition-driven values (higher priority than cookie)
      // Re-wire orchestrator API key from cookie after re-render
      orcCardIds.forEach(id => {
        const el = document.getElementById('orc-key-' + id);
        if (el && !el.value) el.value = orcGetKey();
      });
    }

    // Apply server-provided current values to card controls.
    // Dropdown 'value' is set by StorePendingInit (C#).
    // Recording card condition 'value' is set by RecordingManager.SetConditionValue (C#).
    // Both take priority over cookie-saved state (called after restoreFormState).
    function applyCardValues(list) {
      list.forEach(card => {
        if (card.type === 'dropdown' && card.value) {
          const sel = document.getElementById('sel-' + card.id);
          if (sel && Array.from(sel.options).some(o => o.value === card.value))
            sel.value = card.value;
        }
        if (card.type === 'recording') {
          // Restore participant text input
          if (card.participant) {
            const inp = document.getElementById(`rp-${card.id}`);
            if (inp && !inp.value) inp.value = card.participant;
          }
          // Restore condition dropdowns
          if (card.conditions) {
            card.conditions.forEach(cond => {
              if (!cond.value) return;
              const sel = document.getElementById(`rc-${card.id}-${cond.key}`);
              if (sel && Array.from(sel.options).some(o => o.value === cond.value))
                sel.value = cond.value;
            });
          }
        }
      });
    }

    function renderNormal(rawList) {
      const list = applyOrder(rawList);
      const c = document.getElementById('card-container');
      c.innerHTML = '';
      orcCardIds = [];

      list.forEach(card => {
        const row = document.createElement('div');
        row.className = 'ctrl-row';
        row.setAttribute('draggable', 'true');
        row.dataset.cid = card.id;

        let h = '';

        if (card.type === 'button') {
          h = `${HANDLE}
               <div class=""row-title"">${card.title}</div>
               <button class=""btn btn-primary"" id=""btn-${card.id}"">${card.buttonText}</button>`;

        } else if (card.type === 'input') {
          h = `${HANDLE}
               <div class=""row-title"">${card.title}</div>
               <input id=""inp-${card.id}"" type=""text"" placeholder=""${card.placeHolder}"">
               <button class=""btn btn-primary"" id=""sub-inp-${card.id}"">${card.buttonText}</button>`;

        } else if (card.type === 'dropdown') {
          const opts = card.options.map(o => `<option value=""${o}"">${o}</option>`).join('');
          h = `${HANDLE}
               <div class=""row-title"">${card.title}</div>
               <select id=""sel-${card.id}"">${opts}</select>
               <button class=""btn btn-primary"" id=""sub-sel-${card.id}"">${card.buttonText}</button>`;

        } else if (card.type === 'multifield') {
          let fields = '';
          card.fields.forEach(f => {
            if (f.fieldType === 'dropdown') {
              const opts = f.options.map(o => `<option value=""${o}"">${o}</option>`).join('');
              fields += `<span class=""row-lbl"">${f.label}</span>
                         <select id=""mf-${card.id}-${f.key}"">${opts}</select>`;
            } else {
              fields += `<span class=""row-lbl"">${f.label}</span>
                         <input id=""mf-${card.id}-${f.key}"" type=""text"" placeholder=""${f.placeholder||''}"">`;
            }
          });
          h = `${HANDLE}
               <div class=""row-title"">${card.title}</div>
               ${fields}
               <button class=""btn btn-primary"" id=""sub-mf-${card.id}"">${card.buttonText}</button>`;

        } else if (card.type === 'dragorder') {
          const rows = card.items.map((name, i) => `
            <li class=""drag-item"" draggable=""true"" data-name=""${name}"">
              <span class=""drag-handle"">&#x2807;</span>
              <input type=""checkbox"" class=""drag-check"" checked>
              <span class=""drag-lbl"">${name}</span>
              <span class=""drag-idx"">${i + 1}.</span>
            </li>`).join('');
          h = `${HANDLE}
               <div class=""row-title"">${card.title}</div>
               <ul id=""dl-${card.id}"" class=""drag-list"">${rows}</ul>
               <button class=""btn btn-primary"" id=""sub-dl-${card.id}"">${card.buttonText}</button>`;

        } else if (card.type === 'slider') {
          const dec = stepDec(card.step);
          const n = Math.round((card.max - card.min) / card.step) + 1;
          let ticks = '';
          for (let i = 0; i < n; i++) {
            const v = snap(card.min + i * card.step, card.min, card.max, card.step);
            ticks += `<option value=""${v}""></option>`;
          }
          const init = snap(card.defaultValue, card.min, card.max, card.step).toFixed(dec);
          const save = card.saveButtonText
            ? `<button class=""btn btn-ghost"" id=""sav-sl-${card.id}"">${card.saveButtonText}</button>` : '';
          h = `${HANDLE}
               <div class=""row-title"">${card.title}</div>
               <span class=""slider-val"" id=""slv-${card.id}"">${init}</span>
               <datalist id=""tk-${card.id}"">${ticks}</datalist>
               <input id=""sl-${card.id}"" type=""range"" min=""${card.min}"" max=""${card.max}""
                      step=""${card.step}"" value=""${card.defaultValue}"" list=""tk-${card.id}"">
               <div class=""slider-ends"">
                 <span>${card.min.toFixed(dec)}</span><span>${card.max.toFixed(dec)}</span>
               </div>
               <button class=""btn btn-primary"" id=""sub-sl-${card.id}"">${card.buttonText}</button>${save}`;

        } else if (card.type === 'orchestrator') {
          orcCardIds.push(card.id);
          orcCardUrls[card.id] = { prod: card.prodUrl, dev: card.devUrl };
          h = `${HANDLE}
               <div class=""row-title"">${card.title}</div>
               <div class=""orc-row"">
                 <input id=""orc-key-${card.id}"" type=""password"" placeholder=""Api-Key"" autocomplete=""off"" value=""${orcGetKey()}"">
                 <label class=""orc-dev-label"">
                   <input type=""checkbox"" id=""orc-dev-${card.id}""> Dev
                 </label>
               </div>
               <select id=""orc-sel-${card.id}"" style=""margin-top:0.35rem"">
                 <option value="""">— fetch sessions first —</option>
               </select>
               <div class=""orc-status"" id=""orc-status-${card.id}""></div>
               <div class=""orc-connect-row"">
                 <span class=""orc-dot"" id=""orc-dot-${card.id}"" title=""Connection status""></span>
                 <button class=""btn btn-ghost"" id=""orc-connect-${card.id}"">Connect</button>
                 <button class=""btn btn-ghost"" id=""orc-fetch-${card.id}"">&#8635; Fetch</button>
               </div>
               <div class=""orc-btn-row"">
                 <button class=""btn btn-primary"" id=""orc-apply-${card.id}"">Apply Session</button>
                 <button class=""btn btn-ghost"" id=""orc-clear-${card.id}"">Clear</button>
               </div>`;
        }

        row.innerHTML = h;
        c.appendChild(row);

        // ── Wire interactions ────────────────────────────────────────────────
        if (card.type === 'button') {
          document.getElementById(`btn-${card.id}`).onclick = () =>
            fetch(`/action/${card.id}`, { method: 'POST', body: card.title });

        } else if (card.type === 'input') {
          document.getElementById(`sub-inp-${card.id}`).onclick = () =>
            fetch(`/action/${card.id}`, { method: 'POST',
              body: document.getElementById(`inp-${card.id}`).value });

        } else if (card.type === 'dropdown') {
          document.getElementById(`sub-sel-${card.id}`).onclick = () =>
            fetch(`/action/${card.id}`, { method: 'POST',
              body: document.getElementById(`sel-${card.id}`).value });

        } else if (card.type === 'multifield') {
          document.getElementById(`sub-mf-${card.id}`).onclick = () => {
            const parts = card.fields.map(f =>
              `${encodeURIComponent(f.key)}=${encodeURIComponent(
                document.getElementById(`mf-${card.id}-${f.key}`).value)}`);
            fetch(`/action/${card.id}`, { method: 'POST', body: parts.join('&') });
          };

        } else if (card.type === 'dragorder') {
          initDragList(`dl-${card.id}`);
          document.getElementById(`sub-dl-${card.id}`).onclick = () => {
            const items = Array.from(document.getElementById(`dl-${card.id}`)
              .querySelectorAll('.drag-item')).map(li => ({
                name: li.dataset.name,
                enabled: li.querySelector('.drag-check').checked }));
            fetch(`/action/${card.id}`, { method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(items) });
          };

        } else if (card.type === 'slider') {
          const sl = document.getElementById(`sl-${card.id}`);
          const vl = document.getElementById(`slv-${card.id}`);
          const dec = stepDec(card.step);
          sl.addEventListener('input', () => {
            const s = snap(parseFloat(sl.value), card.min, card.max, card.step);
            sl.value = s; vl.textContent = s.toFixed(dec);
          });
          document.getElementById(`sub-sl-${card.id}`).onclick = () => {
            const s = snap(parseFloat(sl.value), card.min, card.max, card.step);
            fetch(`/action/${card.id}`, { method: 'POST', body: s.toString() });
          };
          if (card.saveButtonText) {
            document.getElementById(`sav-sl-${card.id}`).onclick = () => {
              const s = snap(parseFloat(sl.value), card.min, card.max, card.step);
              fetch(`/action/${card.id}`, { method: 'POST', body: 'save:' + s.toString() });
            };
          }

        } else if (card.type === 'orchestrator') {
          // Save API key to cookie whenever it changes
          const keyEl = document.getElementById(`orc-key-${card.id}`);
          keyEl.addEventListener('change', () => orcSaveKey(keyEl.value));

          document.getElementById(`orc-connect-${card.id}`).onclick = () =>
            orcConnect(card.id);
          document.getElementById(`orc-fetch-${card.id}`).onclick = () => orcFetch(card.id);

          document.getElementById(`orc-apply-${card.id}`).onclick = () => {
            const sessionId = document.getElementById(`orc-sel-${card.id}`).value;
            const dev       = document.getElementById(`orc-dev-${card.id}`).checked;
            const key       = keyEl.value.trim();
            if (!sessionId) { setOrcStatus('error', 'Select a session first.'); return; }
            orcSaveKey(key);

            // Notify Unity so it can load the scene / set up game state
            orcPost(card.id, {
              action:        'apply',
              apiKey:        key,
              useDevServer:  dev,
              sessionId:     sessionId
            });

            // Independently fetch the schedule in-browser to populate the timeline
            const urls = orcCardUrls[card.id] || {};
            const base = (dev ? urls.dev : urls.prod) || (dev
              ? 'http://127.0.0.1:8000/api/v1'
              : 'https://fittsteleopstudy.org/api/v1');
            const scheduleUrl = base + `/sessions/${sessionId}/schedule`;

            setOrcStatus('loading', 'Loading schedule…');
            fetch(scheduleUrl, { headers: { 'Authorization': 'Api-Key ' + key } })
              .then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
              .then(scheduleData => {
                console.log('[OrchestratorCard] Schedule raw:', scheduleData);
                const sessionMeta = orcFetchedSessions[sessionId] || { session_id: sessionId };
                renderTimeline(parseScheduleFromApi(scheduleData, sessionMeta));
                setOrcStatus('ok', 'Session applied — schedule loaded.');
              })
              .catch(err => {
                console.error('[OrchestratorCard] Schedule fetch error:', err);
                // Unity may still publish the schedule via orchestrator-schedule channel
                setOrcStatus('ok', 'Applied — waiting for schedule from Unity.');
              });
          };
          document.getElementById(`orc-clear-${card.id}`).onclick = () => {
            orcPost(card.id, { action: 'clear', apiKey: keyEl.value });
          };
        }
      });
    }

    // ── Orchestrator status + sessions channels ───────────────────────────────
    function setOrcStatus(status, message) {
      orcCardIds.forEach(id => {
        const el = document.getElementById('orc-status-' + id);
        if (!el) return;
        el.textContent = message;
        el.className = 'orc-status ' + (status || '');
      });
    }
    function setOrcConnectDot(cardId, state) {
      // state: '' (idle) | 'connecting' | 'connected' | 'failed'
      const dot = document.getElementById('orc-dot-' + cardId);
      if (dot) dot.className = 'orc-dot' + (state ? ' ' + state : '');
    }
    function setAllOrcConnectDots(state) {
      orcCardIds.forEach(id => setOrcConnectDot(id, state));
    }
    function setOrcSessions(sessions) {
      orcCardIds.forEach(id => {
        const sel = document.getElementById('orc-sel-' + id);
        if (!sel) return;
        const prev = sel.value;
        sel.innerHTML = '<option value="""">— select session —</option>';
        sessions.forEach(s => {
          const opt = document.createElement('option');
          opt.value = s.id; opt.textContent = s.label;
          sel.appendChild(opt);
        });
        if (prev && sel.querySelector(`option[value=""${prev}""]`)) sel.value = prev;
      });
    }

    // ── Orchestrator applied: pre-fill Scene Setup, Camera, Recording fields ──
    function applyOrchestratorData(data) {
      if (!data || !Object.keys(data).length) return;

      // Find the Scene Setup multifield card and pre-fill it.
      // It was registered with field keys ""environment"" and ""interface"".
      if (data.environmentScene) {
        const envEls = document.querySelectorAll('[id^=""mf-""][id$=""-environment""]');
        envEls.forEach(el => { el.value = data.environmentScene; });
      }
      if (data.interfacePrefab !== undefined) {
        const ifEls = document.querySelectorAll('[id^=""mf-""][id$=""-interface""]');
        ifEls.forEach(el => { el.value = data.interfacePrefab || ''; });
      }
      // Camera Selection dropdown (DropdownCard)
      if (data.camera) {
        document.querySelectorAll('[id^=""sel-""]').forEach(el => {
          // The camera dropdown's options are Front/Back/Side; match by checking options
          const hasCamera = Array.from(el.options).some(o =>
            ['Front', 'Back', 'Side'].includes(o.value));
          if (hasCamera) el.value = data.camera;
        });
      }
      // Recording card participant
      if (data.participant) {
        document.querySelectorAll('[id^=""rp-""]').forEach(el => { el.value = data.participant; });
      }
      // Recording card interface condition
      if (data.recInterface) {
        document.querySelectorAll('[id^=""rc-""][id$=""-interface""]').forEach(el => {
          if (el.querySelector(`option[value=""${data.recInterface}""]`))
            el.value = data.recInterface;
        });
      }
      // Mapping file (embodied runs only; field ID ends in '-mapping').
      // 'mappingFile' is an empty string for simulated runs so we skip setting
      // it in that case (leave whatever the researcher last chose in place).
      if (data.mappingFile) {
        document.querySelectorAll('[id^=""mf-""][id$=""-mapping""]').forEach(el => {
          el.value = data.mappingFile;
        });
      }
      // Save everything to form-state cookie so it survives a restart
      saveFormState();
    }

    // ── Timeline ──────────────────────────────────────────────────────────────
    let tlCollapsed = true;

    document.getElementById('tl-collapse-btn').addEventListener('click', () => {
      tlCollapsed = !tlCollapsed;
      const panel = document.getElementById('timeline-panel');
      panel.classList.toggle('collapsed', tlCollapsed);
      document.getElementById('tl-collapse-btn').textContent = tlCollapsed ? '▲' : '▼';
    });

    function renderTimeline(data) {
      const panel   = document.getElementById('timeline-panel');
      const label   = document.getElementById('tl-session-label');
      const empty   = document.getElementById('timeline-empty');
      const scroll  = document.getElementById('timeline-scroll');

      if (!data || !data.blocks || !data.blocks.length) {
        label.innerHTML = 'No session loaded';
        empty.style.display = '';
        scroll.style.display = 'none';
        return;
      }

      label.innerHTML = `<strong>${escHtml(data.participantId || '')}</strong>` +
                        (data.studyName ? ` &mdash; ${escHtml(data.studyName)}` : '') +
                        (data.sessionId ? ` <span style=""opacity:0.55"">(${escHtml(data.sessionId)})</span>` : '');
      empty.style.display = 'none';
      scroll.style.display = '';
      scroll.innerHTML = '';

      data.blocks.forEach(block => {
        const col = document.createElement('div');
        col.className = 'tl-block';

        const head = document.createElement('div');
        head.className = 'tl-block-head';
        head.textContent = `B${block.ordinal}: ${block.interfaceCode || ''}`;
        col.appendChild(head);

        (block.conditions || []).forEach(cond => {
          const row = document.createElement('div');
          row.className = 'tl-condition' + (cond.conditionId === activeConditionId ? ' active' : '');
          row.dataset.cid = cond.conditionId;
          row.title = `${cond.interfaceLabel} · ${cond.viewpointLabel}\nClick to set camera = ${cond.viewpointLabel}`;

          const vp = document.createElement('span');
          vp.className = 'tl-vp';
          vp.textContent = (cond.viewpointLabel || cond.viewpoint || '?').slice(0, 5);
          row.appendChild(vp);

          const geomsEl = document.createElement('div');
          geomsEl.className = 'tl-geoms';
          (cond.geometries || []).forEach((g, gi) => {
            if (gi > 0) {
              const arrow = document.createElement('span');
              arrow.className = 'tl-arrow'; arrow.textContent = '→';
              geomsEl.appendChild(arrow);
            }
            const chip = document.createElement('span');
            chip.className = 'tl-geom';
            chip.textContent = 'S' + g.sheetNumber;
            chip.title = g.label || g.code;
            geomsEl.appendChild(chip);
          });
          row.appendChild(geomsEl);

          // Find the orchestrator card id for posting
          row.addEventListener('click', () => {
            if (!orcCardIds.length) return;
            activeConditionId = cond.conditionId;
            document.querySelectorAll('.tl-condition').forEach(el =>
              el.classList.toggle('active', el.dataset.cid === cond.conditionId));
            applyConditionLocally(cond.conditionId);
            const _d   = orcConditionData[cond.conditionId] || {};
            const _cfg = _d.config || {};
            const _lbl = _d.labels || {};
            orcPost(orcCardIds[0], {
              action:             'apply_condition',
              apiKey:             orcGetKey(),
              conditionId:        cond.conditionId,
              interfaceCondition: _cfg.interface_condition || '',
              viewpoint:          _cfg.viewpoint || _lbl.viewpoint || '',
              viewpointPosition:  _cfg.viewpoint_position  || 1,
              environmentType:    (_d.session || _d.sessionMeta || {}).environment_type || '',
              geometries:         (_lbl.geometry_sequence || []).map(g => ({
                sheetNumber: g.sheet_number || 0,
                label:       g.sheet_label || g.label || '',
                code:        g.code  || ''
              }))
            });
          });

          col.appendChild(row);
        });

        scroll.appendChild(col);
      });

      // Auto-expand timeline when schedule loads
      if (tlCollapsed) {
        tlCollapsed = false;
        panel.classList.remove('collapsed');
        document.getElementById('tl-collapse-btn').textContent = '▼';
      }
    }

    function handleConditionApplied(data) {
      if (!data || !data.conditionId) return;
      activeConditionId = data.conditionId;
      document.querySelectorAll('.tl-condition').forEach(el =>
        el.classList.toggle('active', el.dataset.cid === data.conditionId));
      // Optionally update Camera dropdown to reflect the applied viewpoint
      if (data.camera) {
        document.querySelectorAll('[id^=""sel-""]').forEach(el => {
          const hasCamera = Array.from(el.options).some(o => ['Front', 'Back', 'Side'].includes(o.value));
          if (hasCamera && el.querySelector(`option[value=""${data.camera}""]`))
            el.value = data.camera;
        });
        saveFormState();
      }
    }

    function escHtml(s) {
      return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/'/g, '&#39;');
    }

    // ── Recording card ────────────────────────────────────────────────────────
    function renderRecording(list) {
      const c = document.getElementById('recording-container');
      c.innerHTML = '';
      list.forEach(card => {
        if (!recordingSelected[card.id]) recordingSelected[card.id] = new Set();
        const wrap = document.createElement('div');
        wrap.className = 'rec-form';

        let h = `<div class=""rec-form-head"">
          <div class=""rec-dot""></div><h3>${card.title}</h3>
        </div>
        <div class=""rec-form-body"">
          <span class=""row-lbl-rec"">Participant</span>
          <input id=""rp-${card.id}"" type=""text"" placeholder=""Participant ID"">`;

        card.conditions.forEach(cond => {
          const opts = cond.options.map(o => `<option value=""${o}"">${o}</option>`).join('');
          h += `<span class=""row-lbl-rec"">${cond.label}</span>
                <select id=""rc-${card.id}-${cond.key}"">${opts}</select>`;
        });

        h += `<span class=""row-lbl-rec"">Topics</span>
          <div class=""topic-row-tools"">
            <button class=""chip chip-accent"" id=""rr-${card.id}"">&#8635; Refresh</button>
            <button class=""chip"" id=""ra-${card.id}"">All</button>
            <button class=""chip"" id=""rd-${card.id}"">None</button>
            <button class=""chip"" id=""rs-${card.id}"">Save</button>
            <button class=""chip"" id=""rl-${card.id}"">Load</button>
          </div>
          <div class=""topic-box"" id=""rt-${card.id}""></div>
          <div class=""rec-btns"">
            <button class=""btn btn-start"" id=""rb-start-${card.id}"">&#9654; Start</button>
            <button class=""btn btn-stop""  id=""rb-stop-${card.id}"">&#9632; Stop</button>
          </div>
        </div>`;

        wrap.innerHTML = h;
        c.appendChild(wrap);

        document.getElementById(`rr-${card.id}`).onclick = fetchTopicsNow;
        document.getElementById(`ra-${card.id}`).onclick = () => {
          recordingTopics.forEach(t => recordingSelected[card.id].add(t.name));
          renderTopicList(card.id);
        };
        document.getElementById(`rd-${card.id}`).onclick = () => {
          recordingSelected[card.id].clear(); renderTopicList(card.id);
        };
        document.getElementById(`rs-${card.id}`).onclick = () => saveSel(card.id);
        document.getElementById(`rl-${card.id}`).onclick = () => loadSel(card.id, false);
        document.getElementById(`rb-start-${card.id}`).onclick = () => submitRec(card, 'start');
        document.getElementById(`rb-stop-${card.id}`).onclick  = () => submitRec(card, 'stop');

        loadSel(card.id, true);
        renderTopicList(card.id);
      });
      if (list.length > 0) fetchTopicsNow();
    }

    function renderTopicList(cardId) {
      const el = document.getElementById(`rt-${cardId}`);
      if (!el) return;
      el.innerHTML = '';
      if (!recordingTopics.length) { el.innerHTML = '<div class=""t-empty"">No topics — click Refresh</div>'; return; }
      recordingTopics.forEach(t => {
        const row = document.createElement('label');
        row.className = 't-row';
        const chk = recordingSelected[cardId].has(t.name) ? 'checked' : '';
        row.innerHTML = `<input type=""checkbox"" ${chk}><span class=""t-name"">${t.name}</span><span class=""t-type"">${t.type}</span>`;
        row.querySelector('input').onchange = e => {
          if (e.target.checked) recordingSelected[cardId].add(t.name);
          else recordingSelected[cardId].delete(t.name);
        };
        el.appendChild(row);
      });
    }
    function fetchTopicsNow() {
      fetch('/data/recording-topics?since=-1')
        .then(r => r.json())
        .then(resp => {
          recordingTopics = (resp.data && resp.data.topics) || [];
          Object.keys(recordingSelected).forEach(id => renderTopicList(id));
        })
        .catch(err => console.error('topic refresh:', err));
    }
    function saveSel(id) {
      const p = document.getElementById(`rp-${id}`).value;
      const conds = {};
      document.querySelectorAll(`select[id^=""rc-${id}-""]`).forEach(s => {
        conds[s.id.replace(`rc-${id}-`, '')] = s.value;
      });
      sc(`iona_rec_${id}`, JSON.stringify({
        participant: p, conditions: conds,
        topics: Array.from(recordingSelected[id] || []) }));
    }
    function loadSel(id, silent) {
      const raw = gc(`iona_rec_${id}`);
      if (!raw) return;
      try {
        const d = JSON.parse(raw);
        const pi = document.getElementById(`rp-${id}`);
        if (pi && d.participant) pi.value = d.participant;
        Object.keys(d.conditions || {}).forEach(k => {
          const s = document.getElementById(`rc-${id}-${k}`);
          if (s) s.value = d.conditions[k];
        });
        recordingSelected[id] = new Set(d.topics || []);
        renderTopicList(id);
      } catch(e) { if (!silent) console.error('load sel:', e); }
    }
    function submitRec(card, command) {
      const participant = document.getElementById(`rp-${card.id}`).value;
      const conditions = card.conditions.map(c => ({
        key: c.key, value: document.getElementById(`rc-${card.id}-${c.key}`).value }));
      const topics = Array.from(recordingSelected[card.id] || []);
      fetch(`/action/${card.id}`, {
        method: 'POST',
        body: JSON.stringify({ command, participant, conditions, topics }) });
    }

    // ── Long-poll loops ───────────────────────────────────────────────────────
    function cardsLoop(since) {
      fetch(`/data/cards?since=${since}`)
        .then(r => r.json())
        .then(resp => { renderCards(resp.data.cards); cardsLoop(resp.version); })
        .catch(err => { console.error('cards poll:', err); setTimeout(() => cardsLoop(since), 2000); });
    }
    function topicsLoop(since) {
      fetch(`/data/recording-topics?since=${since}`)
        .then(r => r.json())
        .then(resp => {
          recordingTopics = (resp.data && resp.data.topics) || [];
          Object.keys(recordingSelected).forEach(id => renderTopicList(id));
          topicsLoop(resp.version);
        })
        .catch(err => { console.error('topics poll:', err); setTimeout(() => topicsLoop(since), 2000); });
    }
    function addNotif(title, body, color) {
      const list = document.getElementById('notif-list');
      const empty = document.getElementById('notif-empty');
      if (empty) empty.remove();
      notifCount++;
      const ct = document.getElementById('notif-count');
      ct.style.display = 'inline'; ct.textContent = notifCount;
      const div = document.createElement('div');
      div.className = 'notif-item';
      div.style.background = color || '#444';
      div.innerHTML = `<h3>${title}</h3><p>${body}</p>`;
      list.appendChild(div); list.scrollTop = list.scrollHeight;
    }
    function notifLoop() {
      fetch('/wait-for-message')
        .then(r => r.json())
        .then(d => { if (d.title && d.body) addNotif(d.title, d.body, d.color); notifLoop(); })
        .catch(err => { console.error('notif poll:', err); setTimeout(notifLoop, 2000); });
    }

    // Single merged orchestrator channel loop — replaces 5+ individual loops.
    // The server merges all orchestrator-* sub-channels into one composite payload,
    // keeping the browser under Chrome's 6-connection-per-origin limit so POST
    // requests can always get through.
    function orcDataLoop(since) {
      fetch(`/data/orchestrator?since=${since}`)
        .then(r => r.json())
        .then(resp => {
          const d = resp.data || {};
          if (d.status)    setOrcStatus(d.status.status, d.status.message || '');
          if (d.sessions)  setOrcSessions(d.sessions.sessions || []);
          if (d.applied)   applyOrchestratorData(d.applied);
          if (d.schedule)  renderTimeline(d.schedule);
          if (d.condition) handleConditionApplied(d.condition);
          if (d.ping) {
            if (d.ping.connecting) {
              setAllOrcConnectDots('connecting');
            } else {
              const state = d.ping.connected ? 'connected' : 'failed';
              setAllOrcConnectDots(state);
              if (!d.ping.connected && d.ping.error)
                setOrcStatus('error', 'Connect failed: ' + d.ping.error);
              else if (d.ping.connected)
                setOrcStatus('ok', 'Connected — API reachable.');
            }
          }
          orcDataLoop(resp.version);
        })
        .catch(() => setTimeout(() => orcDataLoop(since), 2000));
    }

    // ── Startup ───────────────────────────────────────────────────────────────
    loadGrid();
    buildColPicker();
    applyGrid();
    initCtrlReorder(document.getElementById('card-container'));

    cardsLoop(0);        // 1 connection
    topicsLoop(0);       // 2 connections
    notifLoop();         // 3 connections  (/wait-for-message)
    orcDataLoop(0);      // 4 connections  (all orchestrator-* merged)
  </script>
</body>
</html>
";
}

}
