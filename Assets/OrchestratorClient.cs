using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Fetches sessions and schedules from the Fitts Orchestrator REST API and
/// distributes the resulting data to RecordingManager, SimulationManager,
/// and the HTTPDash frontend.
///
/// SETUP
/// -----
///   1. Add this component to any persistent GameObject (e.g. the same one
///      that hosts SimulationManager).  DontDestroyOnLoad keeps it alive.
///   2. Fill the inspector fields for scene/interface/viewpoint mappings.
///      Two mapping slots (gamepadRobot, motionController) still need the
///      exact string values used in your SimulationManager.interfaces[].name
///      array — see the TODO comments.
///   3. The researcher enters the machine API key in the dashboard
///      "Orchestrator" card.  The key is stored in a browser cookie; it is
///      NOT committed to source control.
///
/// DATA FLOW
/// ---------
///   Browser "Fetch" button
///     → POST /action/{id} {action:"fetch",...}
///     → FetchSessions() hits GET /sessions?schedule_available=true
///     → publishes "orchestrator-sessions" channel → JS populates dropdown
///
///   Browser "Apply Session" button
///     → POST /action/{id} {action:"apply","sessionId":"..."}
///     → ApplySession() hits GET /sessions/{id}/schedule
///     → ApplyScheduleLocally(): sets RecordingManager conditions,
///       saves full schedule to PlayerPrefs,
///       publishes "orchestrator-applied" (JS pre-fills dropdowns),
///       publishes "orchestrator-schedule" (JS renders timeline)
///
///   Browser timeline entry click
///     → POST /action/{id} {action:"apply_condition","conditionId":"..."}
///     → ApplyConditionById(): updates RecordingManager camera condition
///
///   Unity Play-mode restart (Editor):
///     Start() reads PlayerPrefs, re-runs ApplyScheduleLocally() so
///     RecordingManager conditions are restored before any recording starts.
///     The browser JS restores its own dropdown selections from cookies.
///
/// RACE CONDITION ANALYSIS
/// -----------------------
///   In-Editor reload (play-mode stop→start):
///     The HTTP server (HTTPDash) is DontDestroyOnLoad but stops and
///     restarts with the play session.  The browser's long-poll retry
///     (2 s back-off in JS) reconnects after Unity starts listening again.
///     We persist session state to PlayerPrefs BEFORE triggering any
///     scene reload.  After restart, Start() restores C# state and
///     republishes all channels so the reconnecting browser gets a fresh
///     snapshot.
///
///   In-Build runtime (LoadSceneAsync/Additive):
///     HTTPDash stays alive (DontDestroyOnLoad); only the environment scene
///     reloads.  No restart needed.  The OrchestratorClient simply re-applies
///     condition values after the new scene finishes loading.
/// </summary>
public class OrchestratorClient : MonoBehaviour
{
    public static OrchestratorClient Instance { get; private set; }

    // ── API endpoints ────────────────────────────────────────────────────────

    [Header("API Endpoints")]
    public string productionBaseUrl  = "https://fittsteleopstudy.org/api/v1";
    public string developmentBaseUrl = "http://127.0.0.1:8000/api/v1";

    // ── Scene name mappings ──────────────────────────────────────────────────

    [Header("environment_type → Unity Scene Name")]
    [Tooltip("Scene when environment_type == 'physical_robot'.")]
    public string scenePhysicalRobot   = "Unity200A Fitts Twin";
    [Tooltip("Scene when unity_simulation + human_hand.")]
    public string sceneSimulatedHand   = "Unity200A Fitts";
    [Tooltip("Scene when unity_simulation + gamepad_robot or motion_controller_robot.")]
    public string sceneSimulatedRobot  = "Unity200A Fitts Robot";

    // ── SimulationManager interface prefab name mappings ─────────────────────

    [Header("interface_condition → SimulationManager Interface Name\n(must match interfaces[].name exactly; leave blank = load no interface)")]
    [Tooltip("Prefab name for human_hand (simulated). Leave blank = load no interface prefab.")]
    public string interfaceNameHumanHand         = "";
    [Tooltip("Prefab name for gamepad_robot (simulated only; embodied uses no interface prefab).")]
    public string interfaceNameGamepadRobot      = "Fitts";
    [Tooltip("Prefab name for motion_controller_robot (simulated only; embodied uses no interface prefab).")]
    public string interfaceNameMotionController  = "Fitts";

    // ── Embodied mapping file paths ──────────────────────────────────────────

    [Header("physical_robot + interface_condition → Mapping File Path\n(full path on the robot; published directly to /mapping_player/file_path)")]
    [Tooltip("Full mapping file path for gamepad_robot interface (e.g. /home/fetch/catkin_ws/joystick_mapping.json).")]
    public string mappingFileGamepadRobotPath     = "";
    [Tooltip("Full mapping file path for motion_controller_robot interface (e.g. /home/fetch/catkin_ws/pose_mapping.json).")]
    public string mappingFileMotionControllerPath = "";

    // ── RecordingManager condition value mappings ────────────────────────────

    [Header("interface_condition → RecordingManager 'interface' condition value")]
    public string recordingInterfaceHumanHand        = "hand";
    public string recordingInterfaceGamepadRobot     = "joystick";
    public string recordingInterfaceMotionController = "pose";

    [Header("viewpoint → RecordingManager 'camera' condition value")]
    public string recordingViewpointFront = "Front";
    public string recordingViewpointBack  = "Back";
    public string recordingViewpointSide  = "Side";

    // ── PlayerPrefs persistence keys ─────────────────────────────────────────

    private const string PrefKeyApiKey         = "IONA_OrchestratorApiKey";
    private const string PrefKeyScheduleJson   = "IONA_ScheduleJson";
    private const string PrefKeyActiveCondition = "IONA_ActiveConditionId";

    // ── Internal state ───────────────────────────────────────────────────────

    private string              _apiKey;
    private OrchestratorSchedule _activeSchedule;

    // ── Unity lifecycle ──────────────────────────────────────────────────────

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Start()
    {
        _apiKey = PlayerPrefs.GetString(PrefKeyApiKey, "");
        StartCoroutine(RegisterWithDashDelayed());
        // RestorePendingSession runs after registration so channels are ready.
    }

    // ── Dashboard card registration ──────────────────────────────────────────

    /// <summary>
    /// Waits until HTTPDash.Instance is available before registering.
    /// Handles the common case where HTTPDash and OrchestratorClient share a
    /// scene but their Start() methods run in unpredictable order.
    /// </summary>
    private IEnumerator RegisterWithDashDelayed()
    {
        // Allow up to ~5 s (300 frames at 60 fps) for HTTPDash to initialise.
        float timeout = 5f;
        while (HTTPDash.Instance == null && timeout > 0f)
        {
            yield return null;
            timeout -= Time.unscaledDeltaTime;
        }

        if (HTTPDash.Instance == null)
        {
            Debug.LogError("[OrchestratorClient] HTTPDash.Instance never became available. " +
                           "Make sure HTTPDash is on a persistent GameObject that starts before or " +
                           "alongside OrchestratorClient.");
            yield break;
        }

        HTTPDash.Instance.RegisterOrchestratorCard(OnDashAction,
                                                   prodUrl: productionBaseUrl,
                                                   devUrl:  developmentBaseUrl);
        Debug.Log("[OrchestratorClient] Registered OrchestratorCard with HTTPDash.");

        RestorePendingSession();
    }

    // ── Dashboard action handler ─────────────────────────────────────────────

    private void OnDashAction(HTTPDash.OrchestratorAction action)
    {
        Debug.Log($"[OrchestratorClient] OnDashAction received: action='{action?.action}'" +
                  $" useDevServer={action?.useDevServer}" +
                  $" apiKeyPresent={!string.IsNullOrEmpty(action?.apiKey)}");

        // Persist API key whenever it arrives (might have changed).
        if (!string.IsNullOrEmpty(action.apiKey) && action.apiKey != _apiKey)
        {
            _apiKey = action.apiKey;
            PlayerPrefs.SetString(PrefKeyApiKey, _apiKey);
            PlayerPrefs.Save();
            Debug.Log("[OrchestratorClient] API key updated and saved.");
        }

        string baseUrl = (action.useDevServer ? developmentBaseUrl : productionBaseUrl)
                         .TrimEnd('/');
        Debug.Log($"[OrchestratorClient] Base URL: {baseUrl}");

        switch (action.action)
        {
            case "fetch":
                StartCoroutine(FetchSessions(baseUrl));
                break;

            case "apply":
                if (string.IsNullOrEmpty(action.sessionId))
                    PublishStatus("error", "No session selected.");
                else
                    StartCoroutine(ApplySession(baseUrl, action.sessionId));
                break;

            case "apply_condition":
                if (string.IsNullOrEmpty(action.conditionId))
                {
                    Debug.LogWarning("[OrchestratorClient] apply_condition: conditionId is empty.");
                    PublishStatus("error", "No condition ID supplied.");
                }
                else if (_activeSchedule != null)
                {
                    // Schedule is loaded — look up full condition data from it.
                    ApplyConditionById(action.conditionId);
                }
                else
                {
                    // Schedule not yet loaded in this play session.
                    // The browser already sent all the data we need in the action body
                    // (interfaceCondition, viewpoint, viewpointPosition, environmentType,
                    // geometries), so apply directly without requiring a schedule fetch.
                    Debug.Log("[OrchestratorClient] apply_condition: _activeSchedule is null — " +
                              "applying directly from action body fields.");
                    ApplyConditionDirect(action);
                }
                break;

            case "clear":
                ClearSession();
                break;

            default:
                Debug.LogWarning($"[OrchestratorClient] Unknown action: '{action.action}'");
                break;
        }
    }

    // ── REST: fetch session list ─────────────────────────────────────────────

    private IEnumerator FetchSessions(string baseUrl)
    {
        string url = baseUrl + "/sessions?schedule_available=true&page_size=200";
        Debug.Log($"[OrchestratorClient] FetchSessions → GET {url}");
        PublishStatus("loading", $"Contacting {url} …");

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.SetRequestHeader("Authorization", "Api-Key " + _apiKey);
            yield return req.SendWebRequest();

            Debug.Log($"[OrchestratorClient] FetchSessions response: result={req.result}" +
                      $" httpCode={req.responseCode} error='{req.error}'");

            if (req.result != UnityWebRequest.Result.Success)
            {
                string msg = $"Fetch failed (HTTP {req.responseCode}): {req.error}";
                Debug.LogWarning("[OrchestratorClient] " + msg);
                PublishStatus("error", msg);
                yield break;
            }

            Debug.Log($"[OrchestratorClient] Response body ({req.downloadHandler.text.Length} chars): " +
                      req.downloadHandler.text.Substring(0, Mathf.Min(200, req.downloadHandler.text.Length)));

            SessionPage page;
            try { page = JsonUtility.FromJson<SessionPage>(req.downloadHandler.text); }
            catch (Exception e)
            {
                Debug.LogWarning("[OrchestratorClient] Parse error: " + e.Message);
                PublishStatus("error", "Parse error: " + e.Message);
                yield break;
            }

            if (page == null || page.results == null || page.results.Length == 0)
            {
                Debug.Log("[OrchestratorClient] No schedule-ready sessions in response.");
                PublishStatus("ok", "No schedule-ready sessions found.");
                HTTPDash.Instance?.PublishChannel("orchestrator-sessions", "{\"sessions\":[]}");
                yield break;
            }

            var sb = new StringBuilder();
            sb.Append("{\"sessions\":[");
            for (int i = 0; i < page.results.Length; i++)
            {
                var s = page.results[i];
                if (i > 0) sb.Append(",");
                // Label: StudyName / ParticipantID / SessionID (env type in parens)
                string label = $"{Esc(s.study_name)} / {Esc(s.participant_id)} / {Esc(s.session_id)} ({Esc(s.environment_type)})";
                sb.Append($"{{\"id\":\"{Esc(s.session_id)}\",\"label\":\"{label}\"}}");
            }
            sb.Append("]}");

            Debug.Log($"[OrchestratorClient] Publishing {page.results.Length} session(s) to dashboard.");
            HTTPDash.Instance?.PublishChannel("orchestrator-sessions", sb.ToString());
            PublishStatus("ok", $"Found {page.results.Length} session(s). Select one and click Apply.");
        }
    }

    // ── REST: fetch schedule and apply ───────────────────────────────────────

    private IEnumerator ApplySession(string baseUrl, string sessionId)
    {
        PublishStatus("loading", $"Fetching schedule for {sessionId}…");

        string url = baseUrl + "/sessions/" + Uri.EscapeUriString(sessionId) + "/schedule";

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.SetRequestHeader("Authorization", "Api-Key " + _apiKey);
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                PublishStatus("error", $"Schedule fetch failed (HTTP {req.responseCode}).");
                yield break;
            }

            string rawJson = req.downloadHandler.text;
            OrchestratorSchedule schedule;
            try { schedule = JsonUtility.FromJson<OrchestratorSchedule>(rawJson); }
            catch (Exception e)
            {
                PublishStatus("error", "Schedule parse error: " + e.Message);
                yield break;
            }

            if (schedule == null || schedule.session == null)
            {
                PublishStatus("error", "API returned an empty or invalid schedule.");
                yield break;
            }

            _activeSchedule = schedule;

            // Persist BEFORE any scene reload so post-restart restore has data.
            PlayerPrefs.SetString(PrefKeyScheduleJson, rawJson);
            PlayerPrefs.Save();

            ApplyScheduleLocally(schedule);
        }
    }

    // ── Apply schedule to all managers and publish to dashboard ──────────────

    private void ApplyScheduleLocally(OrchestratorSchedule schedule)
    {
        if (schedule == null) return;

        var sess        = schedule.session;
        string envType  = sess?.environment_type ?? "";

        // Determine interface code and first viewpoint from the first block/condition.
        string interfaceCode = "";
        string viewpointCode = "";

        if (schedule.blocks != null && schedule.blocks.Length > 0)
        {
            var firstBlock = schedule.blocks[0];
            interfaceCode = firstBlock.interface_condition ?? "";
            if (firstBlock.conditions != null && firstBlock.conditions.Length > 0)
                viewpointCode = firstBlock.conditions[0]
                                         .condition_configuration?.viewpoint ?? "";
        }

        // Map to RecordingManager values.
        string recInterface = MapRecordingInterface(interfaceCode);
        string recCamera    = MapRecordingViewpoint(viewpointCode);

        // Push to RecordingManager so script-driven starts use the API values.
        if (RecordingManager.Instance != null)
        {
            if (!string.IsNullOrEmpty(recInterface))
                RecordingManager.Instance.SetConditionValue("interface", recInterface);
            if (!string.IsNullOrEmpty(recCamera))
                RecordingManager.Instance.SetConditionValue("camera", recCamera);
        }

        // Determine target scene, interface prefab, and (for embodied) mapping file.
        string targetScene    = MapSceneName(envType, interfaceCode);
        string targetIface    = MapInterfacePrefabName(envType, interfaceCode);
        string mappingFile    = MapMappingFilePath(interfaceCode);

        // Publish applied-session packet → JS pre-fills Scene Setup and camera dropdowns.
        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append($"\"participant\":\"{Esc(sess?.participant_id ?? "")}\",");
        sb.Append($"\"sessionId\":\"{Esc(sess?.session_id ?? "")}\",");
        sb.Append($"\"studyCode\":\"{Esc(sess?.study_code ?? "")}\",");
        sb.Append($"\"studyName\":\"{Esc(sess?.study_name ?? "")}\",");
        sb.Append($"\"environmentScene\":\"{Esc(targetScene)}\",");
        sb.Append($"\"interfacePrefab\":\"{Esc(targetIface)}\",");
        sb.Append($"\"mappingFile\":\"{Esc(mappingFile)}\",");
        sb.Append($"\"camera\":\"{Esc(recCamera)}\",");
        sb.Append($"\"recInterface\":\"{Esc(recInterface)}\"");
        sb.Append("}");
        HTTPDash.Instance?.PublishChannel("orchestrator-applied", sb.ToString());

        // Publish schedule for the timeline.
        PublishScheduleTimeline(schedule);

        PublishStatus("ok",
            $"Applied: {sess?.participant_id} / {sess?.session_id}. " +
            "Scene Setup and Recording dropdowns pre-filled. " +
            "Click Load in Scene Setup to reload the scene.");

        Debug.Log($"[OrchestratorClient] Applied {sess?.session_id}: " +
                  $"scene='{targetScene}' iface='{targetIface}' " +
                  $"mappingFile='{mappingFile}' " +
                  $"recCamera='{recCamera}' recInterface='{recInterface}'");
    }

    // ── Apply a single condition from the timeline ────────────────────────────

    private void ApplyConditionById(string conditionId)
    {
        if (_activeSchedule?.blocks == null) return;

        string envType = _activeSchedule.session?.environment_type ?? "";

        foreach (var block in _activeSchedule.blocks)
        {
            if (block.conditions == null) continue;
            foreach (var cond in block.conditions)
            {
                if (cond.condition_id != conditionId) continue;

                string interfaceCondition = block.interface_condition ?? "";
                string viewpoint          = cond.condition_configuration?.viewpoint ?? "";
                // viewpoint_position 1 = first trial of the block → training sheet enabled.
                // Falls back to 1 when the field is absent (JsonUtility default for int is 0,
                // so treat ≤0 as 1 to be safe).
                int    viewpointPosition  = cond.condition_configuration?.viewpoint_position ?? 0;
                if (viewpointPosition <= 0) viewpointPosition = 1;

                // ── Sheet labels ─────────────────────────────────────────────────────
                // Read directly from the C# schedule (GeometryEntry.sheet_label = "Sheet 3")
                // rather than from the browser's JSON.  The API already provides the
                // geometry_sequence in the correct visit order — do NOT sort by sheet_number.
                string[] sheetLabels = null;
                if (cond.labels?.geometry_sequence != null && cond.labels.geometry_sequence.Length > 0)
                {
                    sheetLabels = System.Array.ConvertAll(cond.labels.geometry_sequence, g =>
                        !string.IsNullOrEmpty(g.sheet_label) ? g.sheet_label
                        : (!string.IsNullOrEmpty(g.label)    ? g.label : g.code));
                }
                // Training Sheet: first in sequence for trial 1, excluded for trials 2+.
                sheetLabels = AdjustForTrainingSheet(sheetLabels, viewpointPosition);

                // ── Target scene ─────────────────────────────────────────────────────
                // Resolved here (where we know environment_type) rather than in
                // SimulationManager (which would fall back to the currently active scene,
                // breaking cross-block environment changes).
                string targetScene = MapSceneName(envType, interfaceCondition);

                // ── RecordingManager ─────────────────────────────────────────────────
                string recCamera = MapRecordingViewpoint(viewpoint);
                string recIface  = MapRecordingInterface(interfaceCondition);
                if (RecordingManager.Instance != null)
                {
                    if (!string.IsNullOrEmpty(recCamera))
                        RecordingManager.Instance.SetConditionValue("camera", recCamera);
                    if (!string.IsNullOrEmpty(recIface))
                        RecordingManager.Instance.SetConditionValue("interface", recIface);
                }

                // ── Mapping file ─────────────────────────────────────────────────────
                // Path is configured directly in the OrchestratorClient inspector —
                // no lookup through SimulationManager's options list needed.
                string mappingFilePath = (viewpointPosition == 1)
                    ? MapMappingFilePath(interfaceCondition) : "";

                // ── SimulationManager ────────────────────────────────────────────────
                var sim = SimulationManager.Instance;
                if (sim != null)
                    sim.ApplyCondition(interfaceCondition, viewpoint, viewpointPosition,
                                       sheetLabels, targetScene, mappingFilePath);
                else
                    Debug.LogWarning("[OrchestratorClient] SimulationManager.Instance is null — " +
                                     "cannot apply scene / interface / sheet setup.");

                // ── Store pending inits for late-registering dashboard cards ─────────
                if (!string.IsNullOrEmpty(viewpoint))
                    HTTPDash.Instance?.StorePendingInit("Camera Selection", viewpoint);
                if (sheetLabels != null && sheetLabels.Length > 0)
                    HTTPDash.Instance?.StorePendingInit("Sheet Order",
                        string.Join(";", sheetLabels));

                // ── Persist active condition ─────────────────────────────────────────
                // Stored so the timeline can re-highlight the correct button after a
                // play-mode restart and browser reconnect.
                PlayerPrefs.SetString(PrefKeyActiveCondition, conditionId);
                PlayerPrefs.Save();

                // ── Notify browser ───────────────────────────────────────────────────
                // The JS handleConditionApplied() highlights the active button and
                // optionally updates the Camera dropdown.
                string json = $"{{\"conditionId\":\"{Esc(conditionId)}\"," +
                              $"\"camera\":\"{Esc(recCamera)}\"," +
                              $"\"viewpoint\":\"{Esc(viewpoint)}\"}}";
                HTTPDash.Instance?.PublishChannel("orchestrator-condition", json);

                Debug.Log($"[OrchestratorClient] Applied condition '{conditionId}': " +
                          $"iface='{interfaceCondition}' viewpoint='{viewpoint}' " +
                          $"pos={viewpointPosition} scene='{targetScene}' " +
                          $"sheets=[{string.Join(", ", sheetLabels ?? System.Array.Empty<string>())}]");
                return;
            }
        }

        PublishStatus("error", $"Condition '{conditionId}' not found in active schedule.");
    }

    // ── Apply condition directly from action fields (no schedule required) ──────
    //
    // Used when _activeSchedule is null (researcher hasn't clicked "Apply Session"
    // yet in this play session, or PlayerPrefs was cleared).  The browser sends the
    // same fields the schedule lookup would produce, so we just use them directly.

    private void ApplyConditionDirect(HTTPDash.OrchestratorAction action)
    {
        string interfaceCondition = action.interfaceCondition ?? "";
        string viewpoint          = action.viewpoint ?? "";
        int    viewpointPosition  = action.viewpointPosition > 0 ? action.viewpointPosition : 1;
        string envType            = action.environmentType ?? "";

        // Build sheet labels from the geometry array the browser sent.
        string[] sheetLabels = null;
        if (action.geometries != null && action.geometries.Length > 0)
        {
            sheetLabels = System.Array.ConvertAll(action.geometries, g =>
                !string.IsNullOrEmpty(g.label) ? g.label : g.code);
        }
        // Training Sheet: first in sequence for trial 1, excluded for trials 2+.
        sheetLabels = AdjustForTrainingSheet(sheetLabels, viewpointPosition);

        string targetScene = MapSceneName(envType, interfaceCondition);

        // Recording manager
        string recCamera = MapRecordingViewpoint(viewpoint);
        string recIface  = MapRecordingInterface(interfaceCondition);
        if (RecordingManager.Instance != null)
        {
            if (!string.IsNullOrEmpty(recCamera))
                RecordingManager.Instance.SetConditionValue("camera", recCamera);
            if (!string.IsNullOrEmpty(recIface))
                RecordingManager.Instance.SetConditionValue("interface", recIface);
        }

        string mappingFilePathDirect = (viewpointPosition == 1)
            ? MapMappingFilePath(interfaceCondition) : "";

        // SimulationManager — triggers scene restart
        var sim = SimulationManager.Instance;
        if (sim != null)
            sim.ApplyCondition(interfaceCondition, viewpoint, viewpointPosition,
                               sheetLabels, targetScene, mappingFilePathDirect);
        else
            Debug.LogWarning("[OrchestratorClient] SimulationManager.Instance is null — " +
                             "cannot apply scene / interface / sheet setup.");

        // Store pending inits for cards that register after the scene reloads.
        if (!string.IsNullOrEmpty(viewpoint))
            HTTPDash.Instance?.StorePendingInit("Camera Selection", viewpoint);
        if (sheetLabels != null && sheetLabels.Length > 0)
            HTTPDash.Instance?.StorePendingInit("Sheet Order",
                string.Join(";", sheetLabels));

        // Persist and notify browser
        PlayerPrefs.SetString(PrefKeyActiveCondition, action.conditionId);
        PlayerPrefs.Save();

        string json = $"{{\"conditionId\":\"{Esc(action.conditionId)}\"," +
                      $"\"camera\":\"{Esc(recCamera)}\"," +
                      $"\"viewpoint\":\"{Esc(viewpoint)}\"}}";
        HTTPDash.Instance?.PublishChannel("orchestrator-condition", json);

        Debug.Log($"[OrchestratorClient] ApplyConditionDirect '{action.conditionId}': " +
                  $"iface='{interfaceCondition}' viewpoint='{viewpoint}' " +
                  $"pos={viewpointPosition} env='{envType}' scene='{targetScene}' " +
                  $"sheets=[{string.Join(", ", sheetLabels ?? System.Array.Empty<string>())}]");
    }

    // ── Clear persisted session ──────────────────────────────────────────────

    public void ClearSession()
    {
        _activeSchedule = null;
        PlayerPrefs.DeleteKey(PrefKeyScheduleJson);
        PlayerPrefs.Save();
        HTTPDash.Instance?.PublishChannel("orchestrator-schedule",   "{\"blocks\":[]}");
        HTTPDash.Instance?.PublishChannel("orchestrator-sessions",   "{\"sessions\":[]}");
        HTTPDash.Instance?.PublishChannel("orchestrator-applied",    "{}");
        PublishStatus("ok", "Session cleared.");
    }

    // ── Restore persisted session after Unity restart ─────────────────────────

    /// <summary>
    /// Called from Start().  Re-applies the last session stored in PlayerPrefs
    /// so that RecordingManager condition values are correct immediately after a
    /// play-mode restart, before the researcher touches anything on the dashboard.
    /// </summary>
    private void RestorePendingSession()
    {
        string json = PlayerPrefs.GetString(PrefKeyScheduleJson, "");
        if (string.IsNullOrEmpty(json)) return;

        OrchestratorSchedule schedule;
        try { schedule = JsonUtility.FromJson<OrchestratorSchedule>(json); }
        catch
        {
            Debug.LogWarning("[OrchestratorClient] Stored schedule JSON is invalid; clearing.");
            PlayerPrefs.DeleteKey(PrefKeyScheduleJson);
            PlayerPrefs.Save();
            return;
        }

        if (schedule?.session == null) return;

        _activeSchedule = schedule;

        // Re-apply C# state (RecordingManager conditions).
        // Also republish channels so the browser gets a fresh snapshot once it
        // reconnects after the HTTP server restarts.
        ApplyScheduleLocally(schedule);

        // Re-highlight whichever timeline condition was active before the restart.
        string activeCondId = PlayerPrefs.GetString(PrefKeyActiveCondition, "");
        if (!string.IsNullOrEmpty(activeCondId))
        {
            string condJson = $"{{\"conditionId\":\"{Esc(activeCondId)}\"}}";
            HTTPDash.Instance?.PublishChannel("orchestrator-condition", condJson);
            Debug.Log($"[OrchestratorClient] Re-highlighting active condition '{activeCondId}'.");

            // Re-store pending inits so late-registering dashboard cards (Camera Selection,
            // Sheet Order drag list, etc.) get the correct values when they come online
            // after the new scene finishes loading.
            RestoreConditionPendingInits(activeCondId);
        }

        Debug.Log($"[OrchestratorClient] Restored session '{schedule.session.session_id}' " +
                  "from PlayerPrefs after restart.");
    }

    /// <summary>
    /// Looks up the given condition in _activeSchedule and calls
    /// HTTPDash.StorePendingInit for each setting that a late-registering card
    /// might need (camera selection, sheet order, mapping file).
    /// Training Sheet adjustment is applied to the sheet labels here too.
    /// </summary>
    private void RestoreConditionPendingInits(string conditionId)
    {
        if (_activeSchedule?.blocks == null || HTTPDash.Instance == null) return;
        string envType = _activeSchedule.session?.environment_type ?? "";

        foreach (var block in _activeSchedule.blocks)
        {
            if (block.conditions == null) continue;
            foreach (var cond in block.conditions)
            {
                if (cond.condition_id != conditionId) continue;

                string viewpoint = cond.condition_configuration?.viewpoint ?? "";
                if (!string.IsNullOrEmpty(viewpoint))
                    HTTPDash.Instance.StorePendingInit("Camera Selection", viewpoint);

                int viewpointPosition = cond.condition_configuration?.viewpoint_position ?? 0;
                if (viewpointPosition <= 0) viewpointPosition = 1;

                string interfaceCondition = block.interface_condition ?? "";

                // Restore RecordingManager condition overrides so the recording card
                // dropdowns are correct after a play-mode restart or scene reload.
                string recCamera = MapRecordingViewpoint(viewpoint);
                string recIface  = MapRecordingInterface(interfaceCondition);
                if (RecordingManager.Instance != null)
                {
                    if (!string.IsNullOrEmpty(recCamera))
                        RecordingManager.Instance.SetConditionValue("camera", recCamera);
                    if (!string.IsNullOrEmpty(recIface))
                        RecordingManager.Instance.SetConditionValue("interface", recIface);
                }

                // Sheet order (with Training Sheet rule applied)
                string[] labels = null;
                if (cond.labels?.geometry_sequence != null &&
                    cond.labels.geometry_sequence.Length > 0)
                {
                    labels = System.Array.ConvertAll(
                        cond.labels.geometry_sequence, g =>
                            !string.IsNullOrEmpty(g.sheet_label) ? g.sheet_label
                            : (!string.IsNullOrEmpty(g.label) ? g.label : g.code));
                }
                labels = AdjustForTrainingSheet(labels, viewpointPosition);
                if (labels.Length > 0)
                    HTTPDash.Instance.StorePendingInit("Sheet Order",
                        string.Join(";", labels));

                // Mapping File pending init (first trial of each block only)
                if (viewpointPosition == 1)
                {
                    string mappingPath = MapMappingFilePath(interfaceCondition);
                    if (!string.IsNullOrEmpty(mappingPath))
                        RecordingManager.Instance?.SetConditionValue("mappingFile", mappingPath);
                }

                return;   // found the condition — done
            }
        }
    }

    // ── Timeline publishing ──────────────────────────────────────────────────

    private void PublishScheduleTimeline(OrchestratorSchedule schedule)
    {
        if (schedule?.blocks == null)
        {
            HTTPDash.Instance?.PublishChannel("orchestrator-schedule", "{\"blocks\":[]}");
            return;
        }

        // Include session header info so the JS can display it in the timeline bar.
        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append($"\"sessionId\":\"{Esc(schedule.session?.session_id ?? "")}\",");
        sb.Append($"\"participantId\":\"{Esc(schedule.session?.participant_id ?? "")}\",");
        sb.Append($"\"studyName\":\"{Esc(schedule.session?.study_name ?? "")}\",");
        sb.Append("\"blocks\":[");

        for (int bi = 0; bi < schedule.blocks.Length; bi++)
        {
            var block = schedule.blocks[bi];
            if (bi > 0) sb.Append(",");
            sb.Append("{");
            sb.Append($"\"blockId\":\"{Esc(block.block_id)}\",");
            sb.Append($"\"ordinal\":{block.assigned_ordinal},");
            sb.Append($"\"interfaceCode\":\"{Esc(block.interface_condition)}\",");
            sb.Append("\"conditions\":[");

            if (block.conditions != null)
            {
                for (int ci = 0; ci < block.conditions.Length; ci++)
                {
                    var cond = block.conditions[ci];
                    if (ci > 0) sb.Append(",");
                    sb.Append("{");
                    sb.Append($"\"conditionId\":\"{Esc(cond.condition_id)}\",");

                    string vp    = cond.condition_configuration?.viewpoint ?? "";
                    string vpLbl = cond.labels?.viewpoint ?? vp;
                    string ifLbl = cond.labels?.interface_condition ?? block.interface_condition ?? "";
                    sb.Append($"\"viewpoint\":\"{Esc(vp)}\",");
                    sb.Append($"\"viewpointLabel\":\"{Esc(vpLbl)}\",");
                    sb.Append($"\"interfaceLabel\":\"{Esc(ifLbl)}\",");
                    sb.Append("\"geometries\":[");

                    var geoms = cond.labels?.geometry_sequence;
                    if (geoms != null)
                    {
                        for (int gi = 0; gi < geoms.Length; gi++)
                        {
                            if (gi > 0) sb.Append(",");
                            var g = geoms[gi];
                            sb.Append($"{{\"sheetNumber\":{g.sheet_number}," +
                                      $"\"code\":\"{Esc(g.code)}\"," +
                                      $"\"sheetLabel\":\"{Esc(g.sheet_label)}\"," +
                                      $"\"label\":\"{Esc(g.label)}\"}}");
                        }
                    }

                    sb.Append("]}");
                }
            }

            sb.Append("]}");
        }

        sb.Append("]}");
        HTTPDash.Instance?.PublishChannel("orchestrator-schedule", sb.ToString());
    }

    // ── Status helper ────────────────────────────────────────────────────────

    private void PublishStatus(string status, string message)
    {
        string json = $"{{\"status\":\"{Esc(status)}\",\"message\":\"{Esc(message)}\"}}";
        HTTPDash.Instance?.PublishChannel("orchestrator-status", json);
        if (status == "error")
            Debug.LogWarning("[OrchestratorClient] " + message);
    }

    // ── Mapping helpers ──────────────────────────────────────────────────────

    private string MapSceneName(string envType, string interfaceCode)
    {
        if (envType == "physical_robot") return scenePhysicalRobot;
        return interfaceCode == "human_hand" ? sceneSimulatedHand : sceneSimulatedRobot;
    }

    private string MapInterfacePrefabName(string envType, string interfaceCode)
    {
        if (envType == "physical_robot") return "";   // no prefab for embodied runs
        if (interfaceCode == "human_hand")              return interfaceNameHumanHand;
        if (interfaceCode == "gamepad_robot")           return interfaceNameGamepadRobot;
        if (interfaceCode == "motion_controller_robot") return interfaceNameMotionController;
        return "";
    }

    /// <summary>
    /// Returns the mapping file name to pre-fill on the dashboard.
    /// Only meaningful for physical_robot runs (gamepad or motion controller).
    /// Simulated runs don't use a mapping file; returns "" so the JS leaves the
    /// field alone.
    /// </summary>
    private string MapMappingFilePath(string interfaceCode)
    {
        if (interfaceCode == "gamepad_robot")           return mappingFileGamepadRobotPath;
        if (interfaceCode == "motion_controller_robot") return mappingFileMotionControllerPath;
        return "";
    }

    private string MapRecordingInterface(string interfaceCode)
    {
        if (interfaceCode == "human_hand")              return recordingInterfaceHumanHand;
        if (interfaceCode == "gamepad_robot")           return recordingInterfaceGamepadRobot;
        if (interfaceCode == "motion_controller_robot") return recordingInterfaceMotionController;
        return "";
    }

    private string MapRecordingViewpoint(string viewpoint)
    {
        if (string.IsNullOrEmpty(viewpoint)) return "";
        string vp = viewpoint.ToLowerInvariant();
        if (vp == "front") return recordingViewpointFront;
        if (vp == "back")  return recordingViewpointBack;
        if (vp == "side")  return recordingViewpointSide;
        return "";
    }

    private static string Esc(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ── Training Sheet ordering ──────────────────────────────────────────────

    /// <summary>
    /// Enforces the "Training Sheet first trial only" rule:
    ///   viewpointPosition == 1  → "Training Sheet" is inserted at index 0
    ///                             (any duplicate copy already in the array is removed first)
    ///   viewpointPosition  > 1  → "Training Sheet" is removed entirely
    /// Goals not present in the returned array will be disabled by
    /// <see cref="TaskEnvironment.ApplyGeometrySequence"/>.
    /// </summary>
    private static string[] AdjustForTrainingSheet(string[] labels, int viewpointPosition)
    {
        if (labels == null) labels = System.Array.Empty<string>();
        var list = new List<string>(labels);
        list.RemoveAll(s => string.Equals(s, "Training Sheet",
                                          System.StringComparison.OrdinalIgnoreCase));
        if (viewpointPosition == 1)
            list.Insert(0, "Training Sheet");
        return list.ToArray();
    }

    // ── Public accessor ──────────────────────────────────────────────────────

    public OrchestratorSchedule ActiveSchedule => _activeSchedule;
}

// ─────────────────────────────────────────────────────────────────────────────
//  API data models — match the Orchestrator REST schema.
//  JsonUtility ignores any JSON fields not declared here, so we only need the
//  fields OrchestratorClient actually uses.
// ─────────────────────────────────────────────────────────────────────────────

[Serializable]
public class SessionChoice
{
    public string session_id;
    public string participant_id;
    public string study_code;
    public string study_name;
    public string environment_type;
}

[Serializable]
public class SessionPage { public SessionChoice[] results; }

[Serializable]
public class GeometryEntry
{
    public string code;
    public string label;
    public int    sheet_number;
    public string sheet_label;
}

[Serializable]
public class ConditionLabels
{
    public string        interface_condition;
    public string        viewpoint;
    public GeometryEntry[] geometry_sequence;
}

[Serializable]
public class ConditionConfiguration
{
    public string   condition_id;
    public string   interface_condition;
    public string   viewpoint;
    public int      viewpoint_position;   // 1 = first trial of block (training sheet on), >1 = subsequent
    public string[] geometry_sequence;
}

[Serializable]
public class ScheduleCondition
{
    public string               condition_id;
    public string               configuration_digest;
    public ConditionLabels      labels;
    public ConditionConfiguration condition_configuration;
}

[Serializable]
public class ScheduleBlock
{
    public string             block_id;
    public int                assigned_ordinal;
    public string             interface_condition;
    public ScheduleCondition[] conditions;
}

[Serializable]
public class ScheduleSession
{
    public string session_id;
    public string participant_id;
    public string study_code;
    public string study_name;
    public string environment_type;
}

[Serializable]
public class OrchestratorSchedule
{
    public ScheduleSession session;
    public ScheduleBlock[] blocks;
}
