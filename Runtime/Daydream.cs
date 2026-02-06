using System.Collections;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Main Daydream component. Attach to any Camera to apply real-time AI transformation.
///
/// Usage:
///   1. Add this component to a Camera
///   2. Set API Key and Prompt in Inspector
///   3. Press Play
///
/// Uses ScreenSpaceOverlay Canvas (works with URP/HDRP/Built-in).
/// Camera renders to captureRT for WHIP streaming.
/// Overlay shows captureRT (passthrough) or AI output.
/// </summary>
[RequireComponent(typeof(Camera))]
public class Daydream : MonoBehaviour
{
    [Header("API")]
    public string apiUrl = "https://api.daydream.live";
    [HideInInspector] public string apiKey = "";

    [Header("Model")]
    [Tooltip("stabilityai/sdxl-turbo, stabilityai/sd-turbo, Lykon/dreamshaper-8, prompthero/openjourney-v4")]
    public string modelId = "stabilityai/sdxl-turbo";
    [Range(384, 1024)] public int resolution = 512;

    [Header("Prompt")]
    public string prompt = "minecraft screenshot, blocky voxel terrain, grass blocks, dirt, stone, oak trees, blue sky, sunlight";
    public string negativePrompt = "blurry, low quality, flat, 2d";

    [Header("Generation")]
    [Range(0.1f, 20f)] public float guidanceScale = 1.0f;
    [Range(0f, 1f)] public float delta = 0.7f;
    [Tooltip("-1 for random")] public int seed = 42;
    [Range(1, 100)] public int numInferenceSteps = 50;
    public int[] tIndexList = new int[] { 11 };
    public bool doAddNoise = true;
    public bool skipDiffusion;

    [Header("Prompt Schedule")]
    [Tooltip("Override prompt with weighted schedule. Leave empty for simple prompt.")]
    public PromptEntry[] promptSchedule = new PromptEntry[0];
    [Tooltip("linear or slerp")]
    public string promptInterpolationMethod = "slerp";
    public bool normalizePromptWeights = true;

    [Header("Seed Schedule")]
    [Tooltip("Override seed with weighted schedule. Leave empty for simple seed.")]
    public SeedEntry[] seedSchedule = new SeedEntry[0];
    [Tooltip("linear or slerp")]
    public string seedInterpolationMethod = "slerp";
    public bool normalizeSeedWeights = true;

    [Header("Similar Image Filter")]
    public bool enableSimilarImageFilter;
    [Range(0f, 1f)] public float similarImageFilterThreshold = 0.98f;
    public int similarImageFilterMaxSkipFrame = 10;

    [Header("ControlNet")]
    public ControlNetConfig[] controlnets = new ControlNetConfig[]
    {
        new ControlNetConfig { modelId = "xinsir/controlnet-depth-sdxl-1.0", conditioningScale = 0.45f, preprocessor = "depth" },
        new ControlNetConfig { modelId = "xinsir/controlnet-canny-sdxl-1.0", conditioningScale = 0f, preprocessor = "canny" },
        new ControlNetConfig { modelId = "xinsir/controlnet-tile-sdxl-1.0", conditioningScale = 0.21f, preprocessor = "passthrough" },
    };

    [Header("IP Adapter")]
    public IPAdapterConfig ipAdapter = new IPAdapterConfig { enabled = true, scale = 0.5f };
    public string ipAdapterStyleImageUrl = "";

    [Header("Image Processing")]
    public ImageProcessorConfig imagePreprocessing = new ImageProcessorConfig();
    public ImageProcessorConfig imagePostprocessing = new ImageProcessorConfig();

    [Header("Display")]
    public bool showOverlay = true;
    public bool showOriginalPIP = true;
    [Range(0.15f, 0.4f)] public float pipSize = 0.25f;

    [Header("Status (Read Only)")]
    [SerializeField] private DaydreamState state = DaydreamState.Idle;

    // Public API
    public DaydreamState State => state;
    public Texture OutputTexture => whepClient?.ReceivedTexture;

    // Internal
    private DaydreamAuth auth;
    private DaydreamApi api;
    private DaydreamWhipClient whipClient;
    private DaydreamWhepClient whepClient;
    private Camera cam;
    private RenderTexture captureRT;
    private string streamId;
    private bool receivingAI;

    // Display (ScreenSpaceOverlay — works in all render pipelines)
    private GameObject canvasObj;
    private GameObject bgCamObj;
    private RawImage displayImage;
    private RawImage pipImage;

    // Parameter sync
    private string lastSentJson;
    private float lastCheckTime;
    private const float PARAM_SYNC_INTERVAL = 0.1f;

    void Start()
    {
        cam = GetComponent<Camera>();

        resolution = Mathf.Clamp(resolution, 384, 1024);
        resolution = (resolution / 64) * 64;

        // CRITICAL: WebRTC.Update() copies textures to video buffer per frame.
        StartCoroutine(WebRTC.Update());

        SetupCapture();
        SetupDisplay();

        if (!string.IsNullOrEmpty(apiKey))
        {
            // Manual API key — skip auth
            StartCoroutine(Run());
        }
        else
        {
            // Browser login
            auth = new DaydreamAuth();
            if (auth.IsLoggedIn)
            {
                apiKey = auth.ApiKey;
                StartCoroutine(Run());
            }
            else
            {
                state = DaydreamState.Connecting;
                auth.Login(apiUrl);
                StartCoroutine(WaitForLogin());
            }
        }
    }

    IEnumerator WaitForLogin()
    {
        Debug.Log("[Daydream] Waiting for browser login...");

        while (true)
        {
            string result = auth.CheckLoginResult();
            if (result == null)
            {
                yield return null;
                continue;
            }

            if (result == "")
            {
                // Success
                apiKey = auth.ApiKey;
                StartCoroutine(Run());
            }
            else
            {
                // Failed
                Debug.LogError($"[Daydream] Login failed: {result}");
                state = DaydreamState.Error;
            }
            yield break;
        }
    }

    void SetupCapture()
    {
        var gfxType = SystemInfo.graphicsDeviceType;
        var format = WebRTC.GetSupportedRenderTextureFormat(gfxType);
        captureRT = new RenderTexture(resolution, resolution, 24, format);
        captureRT.Create();

        // Camera renders to captureRT for WHIP streaming
        cam.targetTexture = captureRT;

        Debug.Log($"[Daydream] Capture RT: {resolution}x{resolution}, format={format}, graphics={gfxType}");

        // Background camera clears the screen to black (prevents "No cameras rendering")
        bgCamObj = new GameObject("Daydream Background Camera");
        var bgCam = bgCamObj.AddComponent<Camera>();
        bgCam.clearFlags = CameraClearFlags.SolidColor;
        bgCam.backgroundColor = Color.black;
        bgCam.cullingMask = 0; // Render nothing
        bgCam.depth = -100;
    }

    void SetupDisplay()
    {
        // ScreenSpaceOverlay renders independently of cameras — works in URP/HDRP/Built-in
        canvasObj = new GameObject("Daydream Overlay");
        var canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 999;

        canvasObj.AddComponent<CanvasScaler>(); // Default: ConstantPixelSize

        // Single fullscreen image — shows captureRT initially, then AI output
        var imageObj = new GameObject("Display");
        imageObj.transform.SetParent(canvasObj.transform, false);
        imageObj.layer = 5;

        displayImage = imageObj.AddComponent<RawImage>();
        displayImage.color = Color.white;

        var rect = displayImage.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        // Maintain aspect ratio — fits inside screen with letterboxing
        var fitter = imageObj.AddComponent<AspectRatioFitter>();
        fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        fitter.aspectRatio = 1f; // Square

        // Show camera output (passthrough) while connecting
        displayImage.texture = captureRT;
        displayImage.enabled = showOverlay;

        // PIP — shows original camera output in bottom-right corner
        var pipObj = new GameObject("PIP Original");
        pipObj.transform.SetParent(canvasObj.transform, false);
        pipObj.layer = 5;

        pipImage = pipObj.AddComponent<RawImage>();
        pipImage.texture = captureRT;
        pipImage.color = Color.white;

        var pipRect = pipImage.rectTransform;
        pipRect.anchorMin = new Vector2(1, 0); // Bottom-right
        pipRect.anchorMax = new Vector2(1, 0);
        pipRect.pivot = new Vector2(1, 0);
        pipRect.sizeDelta = new Vector2(200, 200);
        pipRect.anchoredPosition = new Vector2(-10, 10); // 10px margin

        pipImage.enabled = false; // Hidden until AI output starts

        Debug.Log("[Daydream] Display: ScreenSpaceOverlay Canvas + PIP");
    }

    IEnumerator Run()
    {
        state = DaydreamState.Creating;
        api = new DaydreamApi(apiUrl, apiKey);

        string paramsJson = BuildParamsJson();
        var createTask = api.CreateStream(paramsJson);
        while (!createTask.IsCompleted) yield return null;

        if (createTask.IsFaulted || createTask.Result == null)
        {
            Debug.LogError($"[Daydream] Stream creation failed: {createTask.Exception?.Message}");
            state = DaydreamState.Error;
            yield break;
        }

        lastSentJson = paramsJson;

        var stream = createTask.Result;
        streamId = stream.id;
        Debug.Log($"[Daydream] Stream ID: {streamId}");
        Debug.Log($"[Daydream] WHIP URL: {stream.whip_url}");

        // Connect WHIP (send video)
        state = DaydreamState.Connecting;
        whipClient = new DaydreamWhipClient(api);
        whipClient.OnConnected += () =>
        {
            Debug.Log("[Daydream] WHIP connected — streaming video");
            state = DaydreamState.Streaming;
        };
        whipClient.OnDisconnected += (reason) =>
        {
            Debug.LogWarning($"[Daydream] WHIP disconnected: {reason}");
            if (state == DaydreamState.Streaming)
            {
                state = DaydreamState.Reconnecting;
                StartCoroutine(ReconnectWhip(stream.whip_url));
            }
        };

        yield return StartCoroutine(whipClient.Connect(captureRT, stream.whip_url));

        // Wait for AI pipeline to initialize
        Debug.Log("[Daydream] Waiting for AI pipeline to initialize...");
        yield return new WaitForSeconds(2f);

        // Connect WHEP (receive AI output)
        string whepUrl = whipClient.WhepUrl;
        if (string.IsNullOrEmpty(whepUrl))
        {
            Debug.LogError("[Daydream] No WHEP URL received from WHIP exchange");
            state = DaydreamState.Error;
            yield break;
        }

        whepClient = new DaydreamWhepClient(api);
        whepClient.OnFrameReceived += OnAIFrameReceived;
        whepClient.OnConnected += () =>
        {
            Debug.Log("[Daydream] WHEP connected — receiving AI frames");
        };

        yield return StartCoroutine(whepClient.Connect(whepUrl));
    }

    void OnAIFrameReceived(Texture texture)
    {
        if (texture != null && !receivingAI)
        {
            receivingAI = true;
            Debug.Log($"[Daydream] AI output active ({texture.width}x{texture.height})");
        }
    }

    void Update()
    {
        // Switch display to AI output when available
        if (displayImage != null)
        {
            displayImage.enabled = showOverlay;

            if (showOverlay && receivingAI && whepClient?.ReceivedTexture != null)
            {
                if (displayImage.texture != whepClient.ReceivedTexture)
                {
                    displayImage.texture = whepClient.ReceivedTexture;
                    Debug.Log("[Daydream] Display switched to AI output");
                }
            }
        }

        // PIP: show original when AI output is active
        if (pipImage != null)
        {
            bool showPIP = showOverlay && showOriginalPIP && receivingAI;
            pipImage.enabled = showPIP;

            if (showPIP)
            {
                float h = Screen.height * pipSize;
                pipImage.rectTransform.sizeDelta = new Vector2(h, h);
            }
        }

        // Debounced parameter sync
        if (!string.IsNullOrEmpty(streamId) && api != null &&
            Time.time - lastCheckTime > PARAM_SYNC_INTERVAL)
        {
            lastCheckTime = Time.time;
            string json = BuildParamsJson();
            if (json != lastSentJson)
            {
                lastSentJson = json;
                _ = api.UpdateStream(streamId, json);
                Debug.Log("[Daydream] Parameters updated");
            }
        }
    }

    string BuildParamsJson()
    {
        var w = new DaydreamJsonWriter();
        w.BeginObject();

        // --- Required fields ---
        w.Key("model_id").String(modelId);

        // Prompt: schedule overrides simple prompt
        if (promptSchedule != null && promptSchedule.Length > 0)
        {
            w.Key("prompt").BeginArray();
            foreach (var entry in promptSchedule)
            {
                w.BeginArray().String(entry.text).Number(entry.weight).EndArray();
            }
            w.EndArray();

            if (!string.IsNullOrEmpty(promptInterpolationMethod))
                w.Key("prompt_interpolation_method").String(promptInterpolationMethod);
            w.Key("normalize_prompt_weights").Bool(normalizePromptWeights);
        }
        else
        {
            w.Key("prompt").String(prompt);
        }

        w.Key("negative_prompt").String(negativePrompt);
        w.Key("guidance_scale").Number(guidanceScale);
        w.Key("delta").Number(delta);
        w.Key("width").Number(resolution);
        w.Key("height").Number(resolution);
        w.Key("num_inference_steps").Number(numInferenceSteps);

        // t_index_list
        if (tIndexList != null && tIndexList.Length > 0)
        {
            w.Key("t_index_list").BeginArray();
            foreach (var t in tIndexList)
                w.Number(t);
            w.EndArray();
        }

        w.Key("do_add_noise").Bool(doAddNoise);

        // Seed: schedule overrides simple seed
        if (seedSchedule != null && seedSchedule.Length > 0)
        {
            w.Key("seed").BeginArray();
            foreach (var entry in seedSchedule)
            {
                w.BeginArray().Number(entry.seed).Number(entry.weight).EndArray();
            }
            w.EndArray();

            if (!string.IsNullOrEmpty(seedInterpolationMethod))
                w.Key("seed_interpolation_method").String(seedInterpolationMethod);
            w.Key("normalize_seed_weights").Bool(normalizeSeedWeights);
        }
        else if (seed >= 0)
        {
            w.Key("seed").Number(seed);
        }

        if (skipDiffusion)
            w.Key("skip_diffusion").Bool(true);

        // --- Similar Image Filter ---
        if (enableSimilarImageFilter)
        {
            w.Key("enable_similar_image_filter").Bool(true);
            w.Key("similar_image_filter_threshold").Number(similarImageFilterThreshold);
            w.Key("similar_image_filter_max_skip_frame").Number(similarImageFilterMaxSkipFrame);
        }

        // --- ControlNets ---
        if (controlnets != null && controlnets.Length > 0)
        {
            w.Key("controlnets").BeginArray();
            foreach (var cn in controlnets)
            {
                w.BeginObject();
                w.Key("model_id").String(cn.modelId);
                w.Key("conditioning_scale").Number(cn.conditioningScale);
                w.Key("preprocessor").String(cn.preprocessor);
                w.Key("enabled").Bool(cn.enabled);
                if (cn.controlGuidanceStart > 0f)
                    w.Key("control_guidance_start").Number(cn.controlGuidanceStart);
                if (cn.controlGuidanceEnd < 1f)
                    w.Key("control_guidance_end").Number(cn.controlGuidanceEnd);
                w.EndObject();
            }
            w.EndArray();
        }

        // --- IP Adapter ---
        if (ipAdapter != null && ipAdapter.enabled)
        {
            w.Key("ip_adapter").BeginObject();
            w.Key("enabled").Bool(true);
            w.Key("scale").Number(ipAdapter.scale);
            if (!string.IsNullOrEmpty(ipAdapter.type))
                w.Key("type").String(ipAdapter.type);
            if (!string.IsNullOrEmpty(ipAdapter.weightType))
                w.Key("weight_type").String(ipAdapter.weightType);
            w.EndObject();

            if (!string.IsNullOrEmpty(ipAdapterStyleImageUrl))
                w.Key("ip_adapter_style_image_url").String(ipAdapterStyleImageUrl);
        }

        // --- Image Pre/Post Processing ---
        WriteProcessorConfig(w, "image_preprocessing", imagePreprocessing);
        WriteProcessorConfig(w, "image_postprocessing", imagePostprocessing);

        w.EndObject();
        return w.ToString();
    }

    void WriteProcessorConfig(DaydreamJsonWriter w, string key, ImageProcessorConfig config)
    {
        if (config == null || config.processors == null || config.processors.Length == 0)
            return;

        w.Key(key).BeginObject();
        w.Key("enabled").Bool(config.enabled);
        w.Key("processors").BeginArray();
        foreach (var p in config.processors)
        {
            w.BeginObject();
            w.Key("type").String(p.type);
            w.Key("enabled").Bool(p.enabled);
            w.EndObject();
        }
        w.EndArray();
        w.EndObject();
    }

    public void SetPrompt(string newPrompt)
    {
        prompt = newPrompt;
    }

    IEnumerator ReconnectWhip(string whipUrl)
    {
        int maxAttempts = 5;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            float delay = Mathf.Pow(2, attempt);
            Debug.Log($"[Daydream] Reconnecting WHIP in {delay}s (attempt {attempt + 1}/{maxAttempts})");
            yield return new WaitForSeconds(delay);

            whipClient.Disconnect();
            yield return StartCoroutine(whipClient.Connect(captureRT, whipUrl));

            if (whipClient.IsConnected)
            {
                state = DaydreamState.Streaming;
                Debug.Log("[Daydream] Reconnected successfully");
                yield break;
            }
        }

        Debug.LogError("[Daydream] Reconnection failed after all attempts");
        state = DaydreamState.Error;
    }

    void OnDestroy()
    {
        auth?.Cancel();
        whipClient?.Disconnect();
        whepClient?.Disconnect();

        if (!string.IsNullOrEmpty(streamId) && api != null)
        {
            _ = api.DeleteStream(streamId);
        }

        if (captureRT != null)
        {
            cam.targetTexture = null;
            captureRT.Release();
            Destroy(captureRT);
        }

        if (canvasObj != null)
            Destroy(canvasObj);
        if (bgCamObj != null)
            Destroy(bgCamObj);
    }
}
