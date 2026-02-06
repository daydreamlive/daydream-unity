using System;
using System.Globalization;
using System.Text;
using UnityEngine;

public enum DaydreamState
{
    Idle,
    Creating,
    Connecting,
    Streaming,
    Reconnecting,
    Error
}

// --- Inspector-serializable parameter types ---

[Serializable]
public class PromptEntry
{
    public string text = "";
    public float weight = 1.0f;
}

[Serializable]
public class SeedEntry
{
    public int seed = 42;
    public float weight = 1.0f;
}

[Serializable]
public class ControlNetConfig
{
    public bool enabled = true;
    [Tooltip("Must match base model. SDXL: xinsir/controlnet-{depth,canny,tile}-sdxl-1.0\n" +
             "SD2.1: thibaud/controlnet-sd21-{openpose,hed,canny,depth,color}-diffusers\n" +
             "SD1.5: lllyasviel/control_v11{f1p_sd15_depth,f1e_sd15_tile,p_sd15_canny}")]
    public string modelId = "xinsir/controlnet-depth-sdxl-1.0";
    [Range(0f, 1f)] public float conditioningScale = 1.0f;
    [Tooltip("depth, canny, hed, lineart, openpose, soft_edge, blur, sharpen, etc.")]
    public string preprocessor = "depth";
    [Range(0f, 1f)] public float controlGuidanceStart = 0f;
    [Range(0f, 1f)] public float controlGuidanceEnd = 1f;
}

[Serializable]
public class IPAdapterConfig
{
    public bool enabled;
    public float scale = 1.0f;
    [Tooltip("regular or faceid (faceid for SDXL only)")]
    public string type = "regular";
    [Tooltip("linear, ease in, ease out, ease in-out, reverse in-out, weak input, weak output, " +
             "weak middle, strong middle, style transfer, composition, strong style transfer, " +
             "style and composition, style transfer precise, composition precise")]
    public string weightType = "linear";
}

[Serializable]
public class ImageProcessorEntry
{
    public bool enabled = true;
    [Tooltip("blur, canny, depth, depth_tensorrt, external, feedback, hed, lineart, " +
             "mediapipe_pose, mediapipe_segmentation, openpose, passthrough, " +
             "pose_tensorrt, realesrgan_trt, sharpen, soft_edge, standard_lineart, " +
             "temporal_net_tensorrt, upscale")]
    public string type = "depth";
}

[Serializable]
public class ImageProcessorConfig
{
    public bool enabled = true;
    public ImageProcessorEntry[] processors = new ImageProcessorEntry[0];
}

// --- API Response types (deserialized with JsonUtility) ---

[Serializable]
public class StreamResponse
{
    public string id;
    public string stream_key;
    public string whip_url;
    public string output_playback_id;
    public string gateway_host;
}

public class SdpExchangeResult
{
    public string AnswerSdp;
    public string WhepUrl;
    public string ResourceUrl;
}

// --- JSON Builder ---

/// <summary>
/// Streaming JSON writer for Daydream API requests.
/// Custom builder needed because Unity's JsonUtility cannot handle:
/// - Polymorphic types (prompt as string or array of tuples)
/// - Optional/null field omission
/// - Nested arrays of tuples
/// </summary>
public class DaydreamJsonWriter
{
    private readonly StringBuilder sb = new StringBuilder(512);
    private readonly bool[] needsSep = new bool[16];
    private int depth;

    public DaydreamJsonWriter BeginObject()
    {
        Sep();
        sb.Append('{');
        needsSep[++depth] = false;
        return this;
    }

    public DaydreamJsonWriter EndObject()
    {
        sb.Append('}');
        needsSep[--depth] = true;
        return this;
    }

    public DaydreamJsonWriter BeginArray()
    {
        Sep();
        sb.Append('[');
        needsSep[++depth] = false;
        return this;
    }

    public DaydreamJsonWriter EndArray()
    {
        sb.Append(']');
        needsSep[--depth] = true;
        return this;
    }

    public DaydreamJsonWriter Key(string key)
    {
        Sep();
        sb.Append('"');
        Escape(key);
        sb.Append("\":");
        needsSep[depth] = false;
        return this;
    }

    public DaydreamJsonWriter String(string value)
    {
        Sep();
        sb.Append('"');
        Escape(value ?? "");
        sb.Append('"');
        return this;
    }

    public DaydreamJsonWriter Number(float value)
    {
        Sep();
        sb.Append(value.ToString("G", CultureInfo.InvariantCulture));
        return this;
    }

    public DaydreamJsonWriter Number(int value)
    {
        Sep();
        sb.Append(value);
        return this;
    }

    public DaydreamJsonWriter Bool(bool value)
    {
        Sep();
        sb.Append(value ? "true" : "false");
        return this;
    }

    private void Sep()
    {
        if (needsSep[depth]) sb.Append(',');
        needsSep[depth] = true;
    }

    private void Escape(string s)
    {
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
    }

    public override string ToString() => sb.ToString();
}
