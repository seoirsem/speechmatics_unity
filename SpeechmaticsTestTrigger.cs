using UnityEngine;
using System;
using System.IO;
using System.Collections.Generic;

public class SpeechmaticsTestTrigger : MonoBehaviour
{
    [Header("Verbosity: 0 = only transcripts, 1 = debug logs")] 
    [Range(0, 1)]
    public int verbosity = 0;
    public SpeechmaticsClient speechmaticsClient;
    private AudioClip recordingClip;
    private bool isRecording = false;
    private float recordingStartTime;
    private const int RECORDING_SECONDS = 10; // length of buffer in seconds (long enough for looping)
    private const int SAMPLE_RATE = 16000;
    private bool apiSessionStarted = false;
    private bool connectionEstablished = false;
    private Queue<byte[]> pendingAudioData = new Queue<byte[]>();

    public static event Action<string, int> BroadcastTranscript; // new

    // Allows other classes to broadcast a transcript
    public static void Broadcast(string transcript, int speaker)
    {
        BroadcastTranscript?.Invoke(transcript, speaker);
    }

    void Start()
    {
        speechmaticsClient = GetComponent<SpeechmaticsClient>();
        if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.Start] Method called");
        if (speechmaticsClient != null)
        {
            if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.Start] Registering event handlers");
            speechmaticsClient.OnTranscriptionReceived += OnTranscriptionReceived;
            speechmaticsClient.OnPartialTranscriptionReceived += OnPartialTranscriptionReceived;
            speechmaticsClient.OnConnectionEstablished += OnConnectionEstablished;
            speechmaticsClient.OnError += OnError;
        }
        else
        {
            Debug.LogError("[SpeechmaticsTestTrigger.Start] SpeechmaticsClient reference not set! Please set it in the Unity Inspector.");
            return;
        }

        // Start the real-time transcription session once at the beginning
        if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.Start] Starting Speechmatics session and waiting for it to be ready...");
        speechmaticsClient.StartRealTimeTranscription();
        apiSessionStarted = true;
        if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.Start] API session started flag set to true");
        
        // We'll start recording when the recognition session is fully established
        // This happens when we receive the OnConnectionEstablished event
    }

    void OnDestroy()
    {
        if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.OnDestroy] Method called");
        if (speechmaticsClient != null)
        {
            if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.OnDestroy] Unregistering event handlers");
            speechmaticsClient.OnTranscriptionReceived -= OnTranscriptionReceived;
            speechmaticsClient.OnPartialTranscriptionReceived -= OnPartialTranscriptionReceived;
            speechmaticsClient.OnConnectionEstablished -= OnConnectionEstablished;
            speechmaticsClient.OnError -= OnError;
            
            // Only end the transcription if we started it
            if (apiSessionStarted)
            {
                if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.OnDestroy] Ending Speechmatics session...");
                speechmaticsClient.EndTranscription();
                apiSessionStarted = false;
                if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.OnDestroy] API session started flag set to false");
            }
        }
    }

    void OnTranscriptionReceived(string transcript)
    {
        if (verbosity > 0) Debug.Log($"[SpeechmaticsTestTrigger] Transcript: {transcript}");
    }
    
    void OnPartialTranscriptionReceived(string transcript)
    {
        if (verbosity > 0) Debug.Log($"[SpeechmaticsTestTrigger] Partial transcript: {transcript}");
    }
    
    void OnConnectionEstablished()
    {
        if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.OnConnectionEstablished] Method called");
        connectionEstablished = true;
        if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.OnConnectionEstablished] connectionEstablished set to true");
        if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.OnConnectionEstablished] Starting microphone recording now that connection is established");
        StartRecording();
    }
    
    void OnError(string errorMessage)
    {
        if (verbosity > 0) Debug.LogError($"[SpeechmaticsTestTrigger.OnError] Method called with error: {errorMessage}");
    }
    
    void SendAudioDataToSpeechmatics(byte[] audioData)
    {
        if (speechmaticsClient != null)
        {
            speechmaticsClient.SendAudioData(audioData);
        }
        else
        {
            Debug.LogError("[SpeechmaticsTestTrigger.SendAudioDataToSpeechmatics] Cannot transcribe: SpeechmaticsClient reference not set!");
        }
    }

    void Update()
    {
        // Only process pending audio data if connection is now established
        if (connectionEstablished && pendingAudioData.Count > 0)
        {
            if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.Update] Connection established and pending audio data available");
            // Process one chunk per frame to avoid overwhelming the connection
            byte[] audioData = pendingAudioData.Dequeue();
            if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.Update] Calling SendAudioDataToSpeechmatics with queued audio data");
            SendAudioDataToSpeechmatics(audioData);
            
            if (pendingAudioData.Count > 0)
            {
                if (verbosity > 0) Debug.Log($"[SpeechmaticsTestTrigger.Update] Still have {pendingAudioData.Count} pending audio chunks...");
            }
        }
    }

    private int chunkSize = 4800; // 300ms at 16kHz
    private int lastSamplePosition = 0;
    private float[] chunkBuffer;

    void StartRecording()
    {
        // if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.StartRecording] Method called");
        if (verbosity > 0) Debug.Log($"[SpeechmaticsTestTrigger.StartRecording] Microphone.devices = [{string.Join(", ", Microphone.devices)}]");

        if (Microphone.devices.Length == 0)
        {
            if (verbosity > 0) Debug.LogError("[SpeechmaticsTestTrigger.StartRecording] No microphone devices found! Check permissions and hardware.");
            return;
        }
        string deviceName = Microphone.devices[0];
        if (verbosity > 0) Debug.Log($"[SpeechmaticsTestTrigger.StartRecording] Using device: {deviceName}");

        try
        {
            // Start mic in looping mode for continuous buffer
            recordingClip = Microphone.Start(deviceName, true, RECORDING_SECONDS, SAMPLE_RATE);
            if (recordingClip == null)
            {
                if (verbosity > 0) Debug.LogError("[SpeechmaticsTestTrigger.StartRecording] Microphone.Start returned null. Permission denied or device error?");
                return;
            }
            if (verbosity > 0) Debug.Log($"[SpeechmaticsTestTrigger.StartRecording] Microphone.Start succeeded. IsRecording = {Microphone.IsRecording(deviceName)}");
            if (!Microphone.IsRecording(deviceName))
            {
                if (verbosity > 0) Debug.LogError("[SpeechmaticsTestTrigger.StartRecording] Microphone is NOT recording. Permission denied or device error?");
                return;
            }
            isRecording = true;
            chunkBuffer = new float[chunkSize];
            lastSamplePosition = 0;
            if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.StartRecording] Recording started successfully, starting chunk sender coroutine");
            StartCoroutine(SendAudioChunks());
        }
        catch (Exception ex)
        {
            if (verbosity > 0) Debug.LogError($"[SpeechmaticsTestTrigger.StartRecording] Exception starting microphone: {ex.Message}");
        }
    }

    private System.Collections.IEnumerator SendAudioChunks()
    {
        if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.SendAudioChunks] Coroutine started");
        while (isRecording)
        {
            int micPos = Microphone.GetPosition(null);
            int samplesAvailable = (micPos >= lastSamplePosition)
                ? micPos - lastSamplePosition
                : (RECORDING_SECONDS * SAMPLE_RATE) - lastSamplePosition + micPos;

            while (samplesAvailable >= chunkSize)
            {
                // Read chunk
                recordingClip.GetData(chunkBuffer, lastSamplePosition);
                lastSamplePosition = (lastSamplePosition + chunkSize) % (RECORDING_SECONDS * SAMPLE_RATE);
                samplesAvailable -= chunkSize;

                // Convert to PCM16
                byte[] audioBytes = new byte[chunkSize * 2];
                for (int i = 0; i < chunkSize; i++)
                {
                    short value = (short)(Mathf.Clamp(chunkBuffer[i], -1f, 1f) * short.MaxValue);
                    audioBytes[i * 2] = (byte)(value & 0xFF);
                    audioBytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
                }
                // Send
                SendAudioDataToSpeechmatics(audioBytes);
            }
            yield return null; // check every frame
        }
        if (verbosity > 0) Debug.Log("[SpeechmaticsTestTrigger.SendAudioChunks] Coroutine ended");
    }

    // ProcessRecording is no longer used; recording is continuous and chunked.

}
