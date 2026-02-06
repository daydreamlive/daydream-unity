using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public class DaydreamApi
{
    private string baseUrl;
    private string apiKey;

    public DaydreamApi(string baseUrl, string apiKey)
    {
        this.baseUrl = baseUrl;
        this.apiKey = apiKey;
    }

    /// <summary>
    /// Creates a new stream. paramsJson is the inner params object built by DaydreamJsonWriter.
    /// </summary>
    public async Task<StreamResponse> CreateStream(string paramsJson)
    {
        string json = "{\"pipeline\":\"streamdiffusion\",\"params\":" + paramsJson + "}";

        Debug.Log($"[Daydream API] Creating stream: {json}");

        using var req = new UnityWebRequest($"{baseUrl}/v1/streams", "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Authorization", $"Bearer {apiKey}");

        var op = req.SendWebRequest();
        while (!op.isDone) await Task.Yield();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[Daydream API] Create stream failed: {req.error}\nResponse: {req.downloadHandler?.text}");
            return null;
        }

        Debug.Log($"[Daydream API] Stream created: {req.downloadHandler.text}");
        return JsonUtility.FromJson<StreamResponse>(req.downloadHandler.text);
    }

    /// <summary>
    /// Updates stream parameters. paramsJson is the inner params object built by DaydreamJsonWriter.
    /// </summary>
    public async Task<bool> UpdateStream(string streamId, string paramsJson)
    {
        string json = "{\"pipeline\":\"streamdiffusion\",\"params\":" + paramsJson + "}";

        using var req = new UnityWebRequest($"{baseUrl}/v1/streams/{streamId}", "PATCH");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Authorization", $"Bearer {apiKey}");

        var op = req.SendWebRequest();
        while (!op.isDone) await Task.Yield();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[Daydream API] Update failed: {req.error}\nResponse: {req.downloadHandler?.text}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Exchanges SDP with a WHIP or WHEP endpoint.
    /// Returns SdpExchangeResult with answer SDP and extracted headers.
    /// </summary>
    public async Task<SdpExchangeResult> ExchangeSdp(string url, string sdpOffer)
    {
        using var req = new UnityWebRequest(url, "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(sdpOffer);
        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/sdp");
        req.SetRequestHeader("Authorization", $"Bearer {apiKey}");

        var op = req.SendWebRequest();
        while (!op.isDone) await Task.Yield();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[Daydream API] SDP exchange failed: {req.error} (HTTP {req.responseCode})\nURL: {url}\nResponse: {req.downloadHandler?.text}");
            return null;
        }

        var result = new SdpExchangeResult
        {
            AnswerSdp = req.downloadHandler.text,
            WhepUrl = req.GetResponseHeader("livepeer-playback-url"),
            ResourceUrl = req.GetResponseHeader("location"),
        };

        if (!string.IsNullOrEmpty(result.WhepUrl))
        {
            Debug.Log($"[Daydream API] Got WHEP URL: {result.WhepUrl}");
        }
        if (!string.IsNullOrEmpty(result.ResourceUrl))
        {
            Debug.Log($"[Daydream API] Resource URL: {result.ResourceUrl}");
        }

        return result;
    }

    public async Task<bool> DeleteStream(string streamId)
    {
        using var req = UnityWebRequest.Delete($"{baseUrl}/v1/streams/{streamId}");
        req.SetRequestHeader("Authorization", $"Bearer {apiKey}");

        var op = req.SendWebRequest();
        while (!op.isDone) await Task.Yield();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[Daydream API] Delete stream failed: {req.error}");
            return false;
        }

        Debug.Log($"[Daydream API] Stream deleted: {streamId}");
        return true;
    }
}
