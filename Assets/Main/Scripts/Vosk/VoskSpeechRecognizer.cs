using System;
using System.IO;
using UnityEngine;
using Vosk;

public class VoskSpeechRecognizer : MonoBehaviour
{
    [Serializable]
    public class LanguageModel
    {
        [Tooltip("Display name, e.g. English, Русский")]
        public string languageName;

        [Tooltip("Folder name inside StreamingAssets that contains the Vosk model")]
        public string modelFolder;
    }

    [Header("Language Models")]
    [Tooltip("List of available language models inside StreamingAssets")]
    [SerializeField] private LanguageModel[] languages = new[]
    {
        new LanguageModel { languageName = "English", modelFolder = "vosk-model-en" },
        new LanguageModel { languageName = "Русский", modelFolder = "vosk-model-ru" }
    };

    [Tooltip("Index into the languages array to load on Start")]
    [SerializeField] private int defaultLanguageIndex;

    [Header("Microphone")]
    [SerializeField] private int sampleRate = 16000;

    public event Action<string> OnPartialResult;
    public event Action<string> OnLanguageChanged;
    public event Action<byte[]> OnPcmData;

    public bool IsModelLoaded { get; private set; }
    public string CurrentLanguage => _currentLanguageIndex >= 0 && _currentLanguageIndex < languages.Length
        ? languages[_currentLanguageIndex].languageName
        : string.Empty;

    private Model _model;
    private VoskRecognizer _recognizer;
    private AudioClip _micClip;
    private int _lastSamplePos;
    private bool _isRecording;
    private int _currentLanguageIndex = -1;

    private void Start()
    {
        LoadModel(defaultLanguageIndex);
    }

    /// <summary>
    /// Switch to a different language at runtime by index into the <see cref="languages"/> array.
    /// </summary>
    public void SwitchLanguage(int languageIndex)
    {
        if (languageIndex < 0 || languageIndex >= languages.Length)
        {
            Debug.LogError($"[Vosk] Invalid language index: {languageIndex}. Available: 0–{languages.Length - 1}.");
            return;
        }

        if (languageIndex == _currentLanguageIndex)
        {
            Debug.Log($"[Vosk] Language '{languages[languageIndex].languageName}' is already active.");
            return;
        }

        var wasRecording = _isRecording;
        if (wasRecording) StopListening();

        DisposeModel();
        LoadModel(languageIndex);

        if (wasRecording && IsModelLoaded) StartListening();
    }

    /// <summary>
    /// Switch to a different language at runtime by name (case-insensitive).
    /// </summary>
    public void SwitchLanguage(string languageName)
    {
        for (var i = 0; i < languages.Length; i++)
        {
            if (string.Equals(languages[i].languageName, languageName, StringComparison.OrdinalIgnoreCase))
            {
                SwitchLanguage(i);
                return;
            }
        }

        Debug.LogError($"[Vosk] Language '{languageName}' not found in the configured list.");
    }

    /// <summary>
    /// Returns the names of all configured languages.
    /// </summary>
    public string[] GetAvailableLanguages()
    {
        var names = new string[languages.Length];
        for (var i = 0; i < languages.Length; i++)
            names[i] = languages[i].languageName;
        return names;
    }

    private void LoadModel(int languageIndex)
    {
        if (languageIndex < 0 || languageIndex >= languages.Length)
        {
            Debug.LogError($"[Vosk] Invalid language index: {languageIndex}.");
            return;
        }

        var lang = languages[languageIndex];
        var fullPath = Path.Combine(Application.streamingAssetsPath, lang.modelFolder);

        if (!Directory.Exists(fullPath))
        {
            Debug.LogError($"[Vosk] Model not found at: {fullPath}\n" +
                           "Download a model from https://alphacephei.com/vosk/models and extract it into StreamingAssets/" + lang.modelFolder + ".");
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
            _currentLanguageIndex = languageIndex;
            Debug.Log($"[Vosk] Model loaded successfully: {lang.languageName} ({lang.modelFolder})");

            OnLanguageChanged?.Invoke(lang.languageName);
            StartListening();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Vosk] Failed to load model for '{lang.languageName}': {ex.Message}");
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

        OnPcmData?.Invoke(pcmBytes);

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

    private void DisposeModel()
    {
        _recognizer?.Dispose();
        _recognizer = null;
        _model?.Dispose();
        _model = null;
        IsModelLoaded = false;
    }

    private void OnDestroy()
    {
        if (_isRecording)
        {
            Microphone.End(null);
            _isRecording = false;
        }

        DisposeModel();
    }

    [Serializable]
    private class VoskResultData { public string text; }

    [Serializable]
    private class VoskPartialData { public string partial; }
}
