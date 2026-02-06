using System;
using System.Collections;
using Unity.WebRTC;
using UnityEngine;

/// <summary>
/// WHEP (WebRTC-HTTP Egress Protocol) client.
/// Receives AI-transformed video frames from the Daydream API.
/// Implements OBS-style retry logic for WHEP connection.
/// </summary>
public class DaydreamWhepClient
{
    private const float ICE_GATHERING_TIMEOUT = 10f;
    private const int MAX_RETRIES = 60;
    private const float RETRY_DELAY = 0.5f;      // 500ms
    private const float RATE_LIMIT_DELAY = 2.0f;  // 2000ms for HTTP 429

    private RTCPeerConnection pc;
    private DaydreamApi api;

    public bool IsConnected { get; private set; }
    public Texture ReceivedTexture { get; private set; }

    public event Action<Texture> OnFrameReceived;
    public event Action OnConnected;
    public event Action<string> OnDisconnected;

    public DaydreamWhepClient(DaydreamApi api)
    {
        this.api = api;
    }

    /// <summary>
    /// Connects to the WHEP endpoint with retry logic.
    /// Must be called as a Coroutine from a MonoBehaviour.
    /// </summary>
    public IEnumerator Connect(string whepUrl)
    {
        IsConnected = false;

        // Retry loop (matching OBS daydream-whep.cpp pattern)
        for (int attempt = 0; attempt < MAX_RETRIES; attempt++)
        {
            if (attempt > 0)
            {
                Debug.Log($"[Daydream WHEP] Retry {attempt}/{MAX_RETRIES}");
                yield return new WaitForSeconds(RETRY_DELAY);
            }

            bool success = false;
            var connectRoutine = ConnectOnce(whepUrl, (ok) => success = ok);

            // Run the connection attempt
            var enumerator = connectRoutine;
            while (enumerator.MoveNext())
            {
                yield return enumerator.Current;
            }

            if (success)
            {
                Debug.Log("[Daydream WHEP] Connected successfully");
                yield break;
            }

            // Clean up failed attempt
            CleanupPeerConnection();
        }

        Debug.LogError("[Daydream WHEP] All retry attempts exhausted");
    }

    private IEnumerator ConnectOnce(string whepUrl, Action<bool> onResult)
    {
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
            Debug.Log($"[Daydream WHEP] ICE connection: {state}");
            switch (state)
            {
                case RTCIceConnectionState.Connected:
                case RTCIceConnectionState.Completed:
                    IsConnected = true;
                    OnConnected?.Invoke();
                    break;
                case RTCIceConnectionState.Failed:
                case RTCIceConnectionState.Closed:
                    IsConnected = false;
                    OnDisconnected?.Invoke(state.ToString());
                    break;
            }
        };

        // 2. Handle received video tracks
        pc.OnTrack = (e) =>
        {
            Debug.Log($"[Daydream WHEP] Track received: {e.Track.Kind}");
            if (e.Track is VideoStreamTrack videoTrack)
            {
                videoTrack.OnVideoReceived += (texture) =>
                {
                    ReceivedTexture = texture;
                    OnFrameReceived?.Invoke(texture);
                };
                Debug.Log("[Daydream WHEP] Video track bound, waiting for frames...");
            }
        };

        // 3. Add recvonly transceivers (video + audio)
        var videoTransceiver = pc.AddTransceiver(TrackKind.Video);
        videoTransceiver.Direction = RTCRtpTransceiverDirection.RecvOnly;

        var audioTransceiver = pc.AddTransceiver(TrackKind.Audio);
        audioTransceiver.Direction = RTCRtpTransceiverDirection.RecvOnly;

        // 4. Create SDP offer
        var offerOp = pc.CreateOffer();
        yield return offerOp;
        if (offerOp.IsError)
        {
            Debug.LogError($"[Daydream WHEP] CreateOffer failed: {offerOp.Error.message}");
            onResult(false);
            yield break;
        }

        // 5. Set local description
        var offer = offerOp.Desc;
        var localDescOp = pc.SetLocalDescription(ref offer);
        yield return localDescOp;
        if (localDescOp.IsError)
        {
            Debug.LogError($"[Daydream WHEP] SetLocalDescription failed: {localDescOp.Error.message}");
            onResult(false);
            yield break;
        }

        // 6. Skip ICE gathering (match browser default)
        yield return null;

        // 7. Send SDP to WHEP endpoint
        string sdpWithCandidates = pc.LocalDescription.sdp;
        Debug.Log($"[Daydream WHEP] Sending SDP ({sdpWithCandidates.Length} bytes)");

        var sdpTask = api.ExchangeSdp(whepUrl, sdpWithCandidates);
        while (!sdpTask.IsCompleted) yield return null;

        if (sdpTask.IsFaulted || sdpTask.Result == null)
        {
            Debug.LogWarning($"[Daydream WHEP] SDP exchange failed: {sdpTask.Exception?.Message}");
            onResult(false);
            yield break;
        }

        // 8. Set remote description
        var answer = new RTCSessionDescription
        {
            type = RTCSdpType.Answer,
            sdp = sdpTask.Result.AnswerSdp
        };
        var remoteDescOp = pc.SetRemoteDescription(ref answer);
        yield return remoteDescOp;
        if (remoteDescOp.IsError)
        {
            Debug.LogError($"[Daydream WHEP] SetRemoteDescription failed: {remoteDescOp.Error.message}");
            onResult(false);
            yield break;
        }

        Debug.Log("[Daydream WHEP] Connection established, waiting for frames...");
        onResult(true);
    }

    private void CleanupPeerConnection()
    {
        pc?.Close();
        pc?.Dispose();
        pc = null;
    }

    public void Disconnect()
    {
        IsConnected = false;
        ReceivedTexture = null;
        CleanupPeerConnection();
    }
}
