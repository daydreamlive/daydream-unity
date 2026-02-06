using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Handles Daydream authentication via browser-based login.
/// Flow: open browser → user logs in → callback with JWT → exchange for API key → save to ~/.daydream/credentials.
/// Shared credential file with OBS and TouchDesigner plugins.
/// </summary>
public class DaydreamAuth
{
    private const int AUTH_TIMEOUT_SEC = 300;
    private const int STATE_LENGTH = 32;
    private static readonly string CredentialsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".daydream");
    private static readonly string CredentialsPath = Path.Combine(CredentialsDir, "credentials");

    private string apiKey;
    private TcpListener server;
    private Thread authThread;
    private volatile bool cancelled;
    private readonly object lockObj = new object();

    // Background thread → main thread communication
    private volatile string pendingResult; // "ok" or error message
    private string pendingApiKey;

    public bool IsLoggedIn => apiKey != null;
    public string ApiKey => apiKey;

    public DaydreamAuth()
    {
        LoadCredentials();
    }

    /// <summary>
    /// Starts browser-based login. Call CheckLoginResult() from Update() to poll for completion.
    /// </summary>
    public void Login(string apiUrl)
    {
        Cancel();
        cancelled = false;
        pendingResult = null;
        pendingApiKey = null;

        server = new TcpListener(IPAddress.Loopback, 0);
        server.Start(1);
        int port = ((IPEndPoint)server.LocalEndpoint).Port;

        string state = GenerateState();

        authThread = new Thread(() => AuthThreadFunc(state, apiUrl))
        {
            IsBackground = true,
            Name = "DaydreamAuth"
        };
        authThread.Start();

        string url = $"https://app.daydream.live/sign-in/local?port={port}&state={state}";
        Debug.Log($"[Daydream Auth] Opening browser for login");
        Application.OpenURL(url);
    }

    /// <summary>
    /// Polls for login result. Returns: null (pending), "" (success), or error message.
    /// </summary>
    public string CheckLoginResult()
    {
        string result = pendingResult;
        if (result == null) return null;

        pendingResult = null;

        if (result == "ok")
        {
            lock (lockObj)
            {
                apiKey = pendingApiKey;
            }
            Debug.Log("[Daydream Auth] Login successful");
            return "";
        }

        Debug.LogError($"[Daydream Auth] Login failed: {result}");
        return result;
    }

    public void Logout()
    {
        Cancel();
        apiKey = null;

        try
        {
            if (File.Exists(CredentialsPath))
                File.Delete(CredentialsPath);
            Debug.Log("[Daydream Auth] Logged out");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Daydream Auth] Failed to delete credentials: {e.Message}");
        }
    }

    public void Cancel()
    {
        cancelled = true;
        try { server?.Stop(); } catch { }

        if (authThread != null && authThread.IsAlive)
        {
            authThread.Join(2000);
            authThread = null;
        }
    }

    // --- Background thread ---

    private void AuthThreadFunc(string expectedState, string apiUrl)
    {
        try
        {
            var startTime = DateTime.UtcNow;

            while (!cancelled)
            {
                if ((DateTime.UtcNow - startTime).TotalSeconds > AUTH_TIMEOUT_SEC)
                {
                    pendingResult = "Login timed out";
                    return;
                }

                if (!server.Pending())
                {
                    Thread.Sleep(100);
                    continue;
                }

                using var client = server.AcceptTcpClient();
                client.ReceiveTimeout = 5000;
                var stream = client.GetStream();
                byte[] buffer = new byte[4096];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                string token = ExtractParam(request, "token");
                string state = ExtractParam(request, "state");

                if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(state))
                {
                    SendResponse(stream, 400, "<h1>Missing parameters</h1>");
                    continue;
                }

                if (state != expectedState)
                {
                    SendResponse(stream, 400, "<h1>Invalid state</h1>");
                    continue;
                }

                string key = ExchangeJwtForApiKey(token, apiUrl);

                if (key != null)
                {
                    SendRedirect(stream, "https://app.daydream.live/sign-in/local/success");
                    SaveCredentials(key);

                    lock (lockObj) { pendingApiKey = key; }
                    pendingResult = "ok";
                }
                else
                {
                    SendResponse(stream, 400, "<h1>Failed to create API key</h1>");
                    pendingResult = "Failed to exchange token for API key";
                }
                return;
            }
        }
        catch (SocketException) when (cancelled) { }
        catch (ObjectDisposedException) when (cancelled) { }
        catch (Exception e)
        {
            if (!cancelled)
                pendingResult = e.Message;
        }
        finally
        {
            try { server?.Stop(); } catch { }
        }
    }

    private string ExchangeJwtForApiKey(string jwt, string apiUrl)
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create($"{apiUrl}/v1/api-key");
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Headers["Authorization"] = $"Bearer {jwt}";
            request.Headers["x-client-source"] = "unity";
            request.Timeout = 30000;

            string body = "{\"name\":\"Unity\",\"user_type\":\"unity\"}";
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            request.ContentLength = bodyBytes.Length;

            using (var reqStream = request.GetRequestStream())
                reqStream.Write(bodyBytes, 0, bodyBytes.Length);

            using var response = (HttpWebResponse)request.GetResponse();
            using var reader = new StreamReader(response.GetResponseStream());
            string responseText = reader.ReadToEnd();

            // Parse {"apiKey":"..."} — simple extraction matching OBS approach
            int i = responseText.IndexOf("\"apiKey\"");
            if (i < 0) return null;
            i = responseText.IndexOf('"', i + 8);
            if (i < 0) return null;
            i++;
            int end = responseText.IndexOf('"', i);
            if (end < 0) return null;

            return responseText.Substring(i, end - i);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Daydream Auth] JWT exchange failed: {e.Message}");
            return null;
        }
    }

    // --- Credentials file (shared with OBS/TouchDesigner) ---

    private void LoadCredentials()
    {
        try
        {
            if (!File.Exists(CredentialsPath)) return;

            foreach (var line in File.ReadAllLines(CredentialsPath))
            {
                if (line.StartsWith("DAYDREAM_API_KEY:"))
                {
                    string key = line.Substring(17).Trim();
                    if (!string.IsNullOrEmpty(key))
                    {
                        apiKey = key;
                        Debug.Log("[Daydream Auth] Loaded credentials from ~/.daydream/credentials");
                        return;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Daydream Auth] Failed to load credentials: {e.Message}");
        }
    }

    private void SaveCredentials(string key)
    {
        try
        {
            Directory.CreateDirectory(CredentialsDir);
            File.WriteAllText(CredentialsPath, $"DAYDREAM_API_KEY: {key}\n");
            Debug.Log("[Daydream Auth] Saved credentials to ~/.daydream/credentials");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Daydream Auth] Failed to save credentials: {e.Message}");
        }
    }

    // --- Helpers ---

    private static string GenerateState()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var rng = new System.Random();
        var sb = new StringBuilder(STATE_LENGTH);
        for (int i = 0; i < STATE_LENGTH; i++)
            sb.Append(chars[rng.Next(chars.Length)]);
        return sb.ToString();
    }

    private static string ExtractParam(string request, string name)
    {
        string search = name + "=";
        int start = request.IndexOf(search);
        if (start < 0) return null;
        start += search.Length;

        int end = start;
        while (end < request.Length && request[end] != '&' && request[end] != ' '
               && request[end] != '\r' && request[end] != '\n')
            end++;

        return end > start ? request.Substring(start, end - start) : null;
    }

    private static void SendRedirect(NetworkStream stream, string location)
    {
        string response = $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        byte[] bytes = Encoding.UTF8.GetBytes(response);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void SendResponse(NetworkStream stream, int status, string body)
    {
        string statusText = status == 200 ? "OK" : status == 400 ? "Bad Request" : "Error";
        string response = $"HTTP/1.1 {status} {statusText}\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
        byte[] bytes = Encoding.UTF8.GetBytes(response);
        stream.Write(bytes, 0, bytes.Length);
    }
}
