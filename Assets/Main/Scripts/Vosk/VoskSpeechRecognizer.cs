using System;
using System.IO;
using UnityEngine;
using Vosk;

public class VoskSpeechRecognizer : MonoBehaviour
{
    [Header("Model")]
    [Tooltip("Folder name inside StreamingAssets that contains the Vosk model")]
    [SerializeField] private string modelPath = "vosk-model";

    [Header("Microphone")]
    [SerializeField] private int sampleRate = 16000;

    public event Action<string> OnPartialResult;

    public bool IsModelLoaded { get; private set; }

    private Model _model;
    private VoskRecognizer _recognizer;
    private AudioClip _micClip;
    private int _lastSamplePos;
    private bool _isRecording;

    private void Start()
    {
        LoadModel();
    }

    private void LoadModel()
    {
        var fullPath = Path.Combine(Application.streamingAssetsPath, modelPath);

        if (!Directory.Exists(fullPath))
        {
            Debug.LogError($"[Vosk] Model not found at: {fullPath}\n" +
                           "Download a model from https://alphacephei.com/vosk/models and extract it into StreamingAssets/vosk-model.");
            return;
        }

        try
        {
            Vosk.Vosk.SetLogLevel(0);
            _model = new Model(fullPath);
            _recognizer = new VoskRecognizer(_model, sampleRate);
            _recognizer.SetMaxAlternatives(0);
            _recognizer.SetWords(true);
            IsModelLoaded = true;
            Debug.Log("[Vosk] Model loaded successfully.");

            StartListening();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Vosk] Failed to load model: {ex.Message}");
        }
    }

    public void StartListening()
    {
        if (_isRecording || !IsModelLoaded) return;

        _micClip = Microphone.Start(null, true, 300, sampleRate);
        _lastSamplePos = 0;
        _isRecording = true;
        Debug.Log("[Vosk] Microphone started — listening continuously.");
    }

    public void StopListening()
    {
        if (!_isRecording) return;

        Microphone.End(null);
        _isRecording = false;
        _recognizer.Reset();
        Debug.Log("[Vosk] Microphone stopped.");
    }

    private void Update()
    {
        if (!_isRecording) return;
        ProcessMicrophoneData();
    }

    private void ProcessMicrophoneData()
    {
        var currentPos = Microphone.GetPosition(null);
        if (currentPos == _lastSamplePos) return;

        int sampleCount = currentPos > _lastSamplePos
            ? currentPos - _lastSamplePos
            : _micClip.samples - _lastSamplePos + currentPos;

        if (sampleCount <= 0) return;

        var samples = new float[sampleCount];
        _micClip.GetData(samples, _lastSamplePos);
        _lastSamplePos = currentPos;

        var pcmBytes = FloatToPcm16(samples);

        if (_recognizer.AcceptWaveform(pcmBytes, pcmBytes.Length))
        {
            var json = _recognizer.Result();
            var parsed = JsonUtility.FromJson<VoskResultData>(json);
            if (!string.IsNullOrWhiteSpace(parsed?.text))
                OnPartialResult?.Invoke(parsed.text);
        }
        else
        {
            var json = _recognizer.PartialResult();
            var parsed = JsonUtility.FromJson<VoskPartialData>(json);
            if (!string.IsNullOrWhiteSpace(parsed?.partial))
                OnPartialResult?.Invoke(parsed.partial);
        }
    }

    private static byte[] FloatToPcm16(float[] floatSamples)
    {
        var bytes = new byte[floatSamples.Length * 2];
        for (var i = 0; i < floatSamples.Length; i++)
        {
            var clamped = Mathf.Clamp(floatSamples[i], -1f, 1f);
            var value = (short)(clamped * short.MaxValue);
            bytes[i * 2] = (byte)(value & 0xFF);
            bytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return bytes;
    }

    private void OnDestroy()
    {
        if (_isRecording)
        {
            Microphone.End(null);
            _isRecording = false;
        }

        _recognizer?.Dispose();
        _model?.Dispose();
    }

    [Serializable]
    private class VoskResultData { public string text; }

    [Serializable]
    private class VoskPartialData { public string partial; }
}
