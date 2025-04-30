using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Text;
using NativeWebSocket;

public class SpeechmaticsClient : MonoBehaviour
{
    public int verbosity = 0;
    [Header("Set your Speechmatics API Key here")]
    public string apiKey = "YOUR_SPEECHMATICS_API_KEY";

    // Example: "en" for English
    public string language = "en";
    
    // Real-time API endpoint
    private const string WEBSOCKET_URL = "wss://eu2.rt.speechmatics.com/v2/";
    
    // WebSocket client
    private WebSocket websocket;
    
    // Flag to track if recognition session is ready to receive audio
    private bool recognitionSessionReady = false;
    
    // Audio settings
    [Header("Audio Settings")]
    public int sampleRate = 16000;
    public bool enablePartials = false;
    
    // Events
    public event Action<string> OnTranscriptionReceived;
    public event Action<string> OnPartialTranscriptionReceived;
    public event Action OnConnectionEstablished;
    public event Action<string> OnError;

    void Start()
    {
        // Load API key from file
        string apiKeyPath = Path.Combine(Application.dataPath, "api_key.txt");
        if (File.Exists(apiKeyPath))
        {
            apiKey = File.ReadAllText(apiKeyPath).Trim();
        }
        else
        {
            // we assume it has been set in the inspector
        }
    }
    // Call this to start transcription
    public void TranscribeAudio(string audioFilePath)
    {
        // Initialize WebSocket on start
        Application.runInBackground = true;
    }
    
    private void Update()
    {
        // Update WebSocket in Unity's main thread
        if (websocket != null)
        {
            #if !UNITY_WEBGL || UNITY_EDITOR
            websocket.DispatchMessageQueue();
            #endif
        }
    }
    
    private void OnDestroy()
    {
        CloseConnection();
    }
    
    private void OnApplicationQuit()
    {
        CloseConnection();
    }
    
    // Call this to start a real-time transcription session
    public void StartRealTimeTranscription()
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey == "YOUR_SPEECHMATICS_API_KEY")
        {
            if (verbosity > 0) Debug.LogError("[SpeechmaticsClient.StartRealTimeTranscription] Please set your Speechmatics API key in the Unity Inspector!");
            return;
        }
                recognitionSessionReady = false;
        StartCoroutine(ConnectWebSocketCoroutine());
    }
    
    // Call this to send audio data for real-time transcription
    public void SendAudioData(byte[] audioData)
    {
        
        if (websocket == null)
        {
            Debug.LogError("[SpeechmaticsClient.SendAudioData] WebSocket is null. Call StartRealTimeTranscription first.");
            OnError?.Invoke("WebSocket is null. Call StartRealTimeTranscription first.");
            return;
        }
                
        if (websocket.State != WebSocketState.Open)
        {
            Debug.LogError("[SpeechmaticsClient.SendAudioData] WebSocket connection is not open. Call StartRealTimeTranscription first.");
            OnError?.Invoke("WebSocket connection is not open. Call StartRealTimeTranscription first.");
            return;
        }
                
        if (!recognitionSessionReady)
        {
            Debug.LogWarning("[SpeechmaticsClient.SendAudioData] Recognition session not ready yet. Cannot send audio data.");
            OnError?.Invoke("Recognition session not ready yet. Cannot send audio data.");
            return;
        }
        
        try
        {
            websocket.Send(audioData);
        }
        catch (Exception e)
        {
            Debug.LogError($"[SpeechmaticsClient.SendAudioData] Error sending audio data: {e.Message}");
            OnError?.Invoke($"Error sending audio data: {e.Message}");
        }
    }
    
    // Call this to end the real-time transcription session
    public void EndTranscription()
    {
        if (websocket == null || websocket.State != WebSocketState.Open)
        {
            return;
        }
        
        // Send EndOfStream message
        string endOfStreamMessage = "{"
            + "\"message\": \"EndOfStream\""
        + "}";
        byte[] endOfStreamBytes = Encoding.UTF8.GetBytes(endOfStreamMessage);
        websocket.Send(endOfStreamBytes);
        
        Debug.Log("Sent EndOfStream message");
    }

    private IEnumerator ConnectWebSocketCoroutine()
    {
        CloseConnection();
        
        // Create headers dictionary with Authorization
        Dictionary<string, string> headers = new Dictionary<string, string>
        {
            { "Authorization", $"Bearer {apiKey}" }
        };
        
        // Create a new WebSocket connection with headers
        websocket = new WebSocket(WEBSOCKET_URL, headers);
        
        // Set up event handlers
        websocket.OnOpen += () => {
            SendStartRecognitionMessage();
        };
        
        websocket.OnError += (e) => {
            Debug.LogError($"[SpeechmaticsClient.ConnectWebSocketCoroutine] WebSocket error: {e}");
            OnError?.Invoke($"WebSocket error: {e}");
        };
        
        websocket.OnClose += (e) => {
            Debug.Log($"[SpeechmaticsClient.ConnectWebSocketCoroutine] WebSocket closed with code: {e}");
            recognitionSessionReady = false;
        };
        
        websocket.OnMessage += (bytes) => {
            // Convert bytes to string
            string message = System.Text.Encoding.UTF8.GetString(bytes);
            ProcessWebSocketMessage(message);
        };
        
        // Connect to the WebSocket server
        websocket.Connect();
        
        // Wait for the connection to be established or fail
        float timeoutSeconds = 10.0f;
        float elapsedTime = 0.0f;
        
        while (websocket.State != WebSocketState.Open && elapsedTime < timeoutSeconds)
        {
            elapsedTime += Time.deltaTime;
            yield return null;
        }
        
        if (websocket.State != WebSocketState.Open)
        {
            Debug.LogError("[SpeechmaticsClient.ConnectWebSocketCoroutine] WebSocket connection timed out");
            OnError?.Invoke("WebSocket connection timed out");
            yield break;
        }
        
        Debug.Log("[SpeechmaticsClient.ConnectWebSocketCoroutine] WebSocket connection established successfully");
    }
    
    private void SendStartRecognitionMessage()
    {
        
        try
        {
            // Prepare the configuration for the recognition session
            // Max delay determines how long to get the transcripts back after sending the audio. Minimum is 0.7, max 10. 
            // Results will be slighly more accurate for higher delays
            // see: https://docs.speechmatics.com/rt-api-ref#transcription-config
            string config = $@"{{""message"": ""StartRecognition"",
                ""audio_format"": {{""type"": ""raw"", ""encoding"": ""pcm_s16le"", ""sample_rate"": {sampleRate}}},
                ""transcription_config"": {{""language"": ""{language}"", ""enable_partials"": {enablePartials.ToString().ToLower()}, 
                ""max_delay"": 0.7, ""diarization"": ""speaker"", ""operating_point"": ""enhanced""}}
            }}";
            
            websocket.SendText(config);
        }
        catch (Exception e)
        {
            Debug.LogError($"[SpeechmaticsClient.SendStartRecognitionMessage] Error sending StartRecognition message: {e.Message}");
            OnError?.Invoke($"Error starting recognition: {e.Message}");
        }
    }
    
    [Serializable]
private class SpeechmaticsSimpleMessage
{
    public string message;
}

private void ProcessWebSocketMessage(string message)
    {
        try
        {
            // Parse the message as JSON to robustly extract message type and content
            SpeechmaticsSimpleMessage msgObj = null;
            try {
                msgObj = JsonUtility.FromJson<SpeechmaticsSimpleMessage>(message);
            } catch (Exception jsonEx) {
                Debug.LogWarning($"[SpeechmaticsClient] Could not parse message as JSON: {jsonEx.Message}");
            }

            if (msgObj != null && !string.IsNullOrEmpty(msgObj.message))
            {
                switch (msgObj.message)
                {
                    case "RecognitionStarted":
                        recognitionSessionReady = true;
                        OnConnectionEstablished?.Invoke();
                        break;
                    case "AddTranscript":
                        // Extract the transcript content
                        string transcript = ExtractTranscriptContent(message);
                        int speakerId = ExtractSpeakerId(message);
                        SpeechmaticsTestTrigger.Broadcast(transcript, speakerId);
                        OnTranscriptionReceived?.Invoke(transcript);
                        break;
                    case "AddPartialTranscript":
                        // Extract the partial transcript content
                        string partialTranscript = ExtractTranscriptContent(message);
                        OnPartialTranscriptionReceived?.Invoke(partialTranscript);
                        break;
                    case "EndOfTranscript":
                        Debug.Log("[SpeechmaticsClient] EndOfTranscript message received");
                        break;
                    case "Error":
                        Debug.LogError($"[SpeechmaticsClient] Error message received: {message}");
                        OnError?.Invoke($"Speechmatics error: {message}");
                        break;
                    case "AudioAdded":
                        if (verbosity > 0) Debug.Log("[SpeechmaticsClient] Audio added");
                        break;
                    default:
                        break;
                }
            }
            else
            {
                Debug.Log($"[SpeechmaticsClient] Unknown or unparsable message received: {message}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[SpeechmaticsClient] Error processing WebSocket message: {e.Message}");
            OnError?.Invoke($"Error processing WebSocket message: {e.Message}");
        }
    }
    
    // Helper to extract speaker id from the JSON message
    private int ExtractSpeakerId(string jsonMessage)
    {
        try
        {
            // Minimal class for deserializing the relevant part
            var root = JsonUtility.FromJson<SpeechmaticsResultsRoot>(jsonMessage);
            if (root.results != null && root.results.Length > 0)
            {
                var alternatives = root.results[0].alternatives;
                if (alternatives != null && alternatives.Length > 0 && !string.IsNullOrEmpty(alternatives[0].speaker))
                {
                    string spk = alternatives[0].speaker;
                    if (spk.StartsWith("S"))
                    {
                        if (int.TryParse(spk.Substring(1), out int id))
                            return id;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SpeechmaticsClient] Could not extract speaker id: {e.Message}");
        }
        return 0; // Default if not found
    }

    // Classes for JSON parsing
    [Serializable]
    private class SpeechmaticsResultsRoot
    {
        public SpeechmaticsResult[] results;
    }
    [Serializable]
    private class SpeechmaticsResult
    {
        public SpeechmaticsAlternative[] alternatives;
    }
    [Serializable]
    private class SpeechmaticsAlternative
    {
        public string speaker;
    }

    private string ExtractTranscriptContent(string jsonMessage)
    {
        // Look for the transcript field in metadata
        string transcriptKey = "\"transcript\":";
        int transcriptIndex = jsonMessage.IndexOf(transcriptKey);
        if (transcriptIndex == -1)
        {
            Debug.Log("[SpeechmaticsClient.ExtractTranscriptContent] No transcript field found in message");
            return "";
        }
        // Find the start of the transcript value (after the colon and possible whitespace/quote)
        int valueStart = jsonMessage.IndexOf('"', transcriptIndex + transcriptKey.Length);
        if (valueStart == -1) {
            Debug.Log("[SpeechmaticsClient.ExtractTranscriptContent] Could not find start quote for transcript value");
            return "";
        }
        valueStart++; // move past the opening quote
        int valueEnd = jsonMessage.IndexOf('"', valueStart);
        if (valueEnd == -1) {
            Debug.Log("[SpeechmaticsClient.ExtractTranscriptContent] Could not find end quote for transcript value");
            return "";
        }
        string transcript = jsonMessage.Substring(valueStart, valueEnd - valueStart);
        
        // Clean up the transcript by removing punctuation and extra whitespace
        transcript = System.Text.RegularExpressions.Regex.Replace(transcript, @"[^\w\s]", "").Trim();
        
        return transcript;
    }
    
    private void CloseConnection()
    {
        
        if (websocket != null)
        {
            Debug.Log($"[SpeechmaticsClient.CloseConnection] Current WebSocket state: {websocket.State}");
            
            if (websocket.State == WebSocketState.Open)
            {
                // Try to send EndOfStream message before closing
                try
                {
                    Debug.Log("[SpeechmaticsClient.CloseConnection] Sending EndOfStream message before closing");
                    EndTranscription();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SpeechmaticsClient.CloseConnection] Error sending EndOfStream: {e.Message}");
                }
                
                // Close the WebSocket connection
                Debug.Log("[SpeechmaticsClient.CloseConnection] Closing WebSocket connection");
                websocket.Close();
            }
            
            websocket = null;
            recognitionSessionReady = false;
            Debug.Log("[SpeechmaticsClient.CloseConnection] WebSocket set to null and recognitionSessionReady set to false");
        }
        else
        {
            Debug.Log("[SpeechmaticsClient.CloseConnection] WebSocket was already null");
        }
    }
}
