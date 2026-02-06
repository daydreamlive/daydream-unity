using System;
using System.Collections;
using System.Linq;
using Unity.WebRTC;
using UnityEngine;

/// <summary>
/// WHIP (WebRTC-HTTP Ingest Protocol) client.
/// Sends video from a RenderTexture to the Daydream API via WebRTC.
/// </summary>
public class DaydreamWhipClient
{
    private const float ICE_GATHERING_TIMEOUT = 10f;
    private const uint VIDEO_BITRATE = 500_000; // 500 kbps

    private RTCPeerConnection pc;
    private VideoStreamTrack videoTrack;
    private DaydreamApi api;

    public string WhepUrl { get; private set; }
    public bool IsConnected { get; private set; }

    public event Action OnConnected;
    public event Action<string> OnDisconnected;

    public DaydreamWhipClient(DaydreamApi api)
    {
        this.api = api;
    }

    /// <summary>
    /// Connects to the WHIP endpoint and starts sending video.
    /// Must be called as a Coroutine from a MonoBehaviour.
    /// </summary>
    public IEnumerator Connect(RenderTexture captureRT, string whipUrl)
    {
        IsConnected = false;
        WhepUrl = null;

        // 1. Create PeerConnection
        var config = new RTCConfiguration
        {
            iceServers = new[]
            {
                new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } },
                new RTCIceServer { urls = new[] { "stun:stun1.l.google.com:19302" } },
            }
        };

        pc = new RTCPeerConnection(ref config);

        pc.OnIceConnectionChange = (state) =>
        {
            Debug.Log($"[Daydream WHIP] ICE connection: {state}");
            switch (state)
            {
                case RTCIceConnectionState.Connected:
                case RTCIceConnectionState.Completed:
                    if (!IsConnected)
                    {
                        IsConnected = true;
                        OnConnected?.Invoke();
                    }
                    break;
                case RTCIceConnectionState.Disconnected:
                    Debug.LogWarning("[Daydream WHIP] ICE disconnected");
                    break;
                case RTCIceConnectionState.Failed:
                case RTCIceConnectionState.Closed:
                    IsConnected = false;
                    OnDisconnected?.Invoke(state.ToString());
                    break;
            }
        };

        // Log ICE candidates for debugging
        pc.OnIceCandidate = (candidate) =>
        {
            Debug.Log($"[Daydream WHIP] ICE candidate: {candidate.Candidate}");
        };

        // 2. Add video track from RenderTexture
        videoTrack = new VideoStreamTrack(captureRT);
        var videoTransceiver = pc.AddTransceiver(videoTrack);
        videoTransceiver.Direction = RTCRtpTransceiverDirection.SendOnly;

        // Set H.264 codec preference via transceiver API
        TrySetH264Preference(videoTransceiver);

        // 3. Add dummy audio transceiver (gateway expects both video and audio)
        var audioTransceiver = pc.AddTransceiver(TrackKind.Audio);
        audioTransceiver.Direction = RTCRtpTransceiverDirection.SendOnly;

        // 4. Create SDP offer
        var offerOp = pc.CreateOffer();
        yield return offerOp;
        if (offerOp.IsError)
        {
            Debug.LogError($"[Daydream WHIP] CreateOffer failed: {offerOp.Error.message}");
            yield break;
        }

        // 5. Munge SDP to prefer H.264
        var offer = offerOp.Desc;
        offer.sdp = DaydreamSdpUtils.PreferH264(offer.sdp);

        // 6. Set local description
        var localDescOp = pc.SetLocalDescription(ref offer);
        yield return localDescOp;
        if (localDescOp.IsError)
        {
            Debug.LogError($"[Daydream WHIP] SetLocalDescription failed: {localDescOp.Error.message}");
            yield break;
        }

        // 7. Skip ICE gathering (match browser default: skipIceGathering=true)
        // Browser sends SDP immediately. Server provides candidates in answer.
        // Wait 1 frame to let any immediate candidates be added to local description.
        yield return null;

        // 8. Send SDP with ICE candidates to WHIP endpoint
        string sdpWithCandidates = pc.LocalDescription.sdp;
        Debug.Log($"[Daydream WHIP] Sending SDP ({sdpWithCandidates.Length} bytes)");

        var sdpTask = api.ExchangeSdp(whipUrl, sdpWithCandidates);
        while (!sdpTask.IsCompleted) yield return null;

        if (sdpTask.IsFaulted || sdpTask.Result == null)
        {
            Debug.LogError($"[Daydream WHIP] SDP exchange failed: {sdpTask.Exception?.Message}");
            yield break;
        }

        var result = sdpTask.Result;
        WhepUrl = result.WhepUrl;
        Debug.Log($"[Daydream WHIP] WHEP URL: {WhepUrl ?? "none"}");

        // 9. Set remote description (SDP answer)
        var answer = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = result.AnswerSdp };
        var remoteDescOp = pc.SetRemoteDescription(ref answer);
        yield return remoteDescOp;
        if (remoteDescOp.IsError)
        {
            Debug.LogError($"[Daydream WHIP] SetRemoteDescription failed: {remoteDescOp.Error.message}");
            yield break;
        }

        // 10. Apply bitrate constraints
        ApplyEncodingConstraints(videoTransceiver);

        Debug.Log("[Daydream WHIP] Connection established, waiting for ICE...");
    }

    private void TrySetH264Preference(RTCRtpTransceiver transceiver)
    {
        try
        {
            var caps = RTCRtpSender.GetCapabilities(TrackKind.Video);
            if (caps.codecs == null) return;

            var h264Codecs = caps.codecs
                .Where(c => c.mimeType.ToLower().Contains("h264"))
                .ToArray();

            if (h264Codecs.Length > 0)
            {
                transceiver.SetCodecPreferences(h264Codecs);
                Debug.Log($"[Daydream WHIP] H.264 codec preference set ({h264Codecs.Length} variants)");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Daydream WHIP] Could not set codec preferences: {e.Message}");
        }
    }

    private void ApplyEncodingConstraints(RTCRtpTransceiver transceiver)
    {
        try
        {
            var sender = transceiver.Sender;
            var parameters = sender.GetParameters();
            foreach (var encoding in parameters.encodings)
            {
                encoding.maxBitrate = VIDEO_BITRATE;
                encoding.maxFramerate = 30; // AI pipeline doesn't need 60fps
            }
            sender.SetParameters(parameters);
            Debug.Log($"[Daydream WHIP] Encoding: {VIDEO_BITRATE / 1000}kbps, 30fps max");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Daydream WHIP] Could not set encoding params: {e.Message}");
        }
    }

    public void Disconnect()
    {
        IsConnected = false;
        videoTrack?.Dispose();
        videoTrack = null;
        pc?.Close();
        pc?.Dispose();
        pc = null;
    }
}
