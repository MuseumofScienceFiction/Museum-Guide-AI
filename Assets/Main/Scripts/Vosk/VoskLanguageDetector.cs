using System;
using System.IO;
using UnityEngine;
using Vosk;

/// <summary>
/// Automatically detects the spoken language by running lightweight Vosk recognizers
/// for each configured language in parallel. When a phrase is completed, the recognizer
/// that produces the highest-confidence / longest result "wins", and the main
/// <see cref="VoskSpeechRecognizer"/> is switched to that language.
/// </summary>
public class VoskLanguageDetector : MonoBehaviour
{
    [Serializable]
    public class DetectionLanguage
    {
        [Tooltip("Must match a languageName in VoskSpeechRecognizer.languages")]
        public string languageName;

        [Tooltip("Folder inside StreamingAssets with a SMALL Vosk model for this language")]
        public string smallModelFolder;
    }

    [Header("References")]
    [SerializeField] private VoskSpeechRecognizer speechRecognizer;

    [Header("Detection Models")]
    [Tooltip("Small/lightweight models used only for language detection")]
    [SerializeField] private DetectionLanguage[] detectionLanguages = new[]
    {
        new DetectionLanguage { languageName = "English",  smallModelFolder = "vosk-model-small-en-us-0.15" },
        new DetectionLanguage { languageName = "Русский",  smallModelFolder = "vosk-model-small-ru-0.22" }
    };

    [Header("Settings")]
    [Tooltip("Sample rate must match VoskSpeechRecognizer")]
    [SerializeField] private int sampleRate = 16000;

    [Tooltip("Minimum word count in the winning result to trigger a language switch")]
    [SerializeField] private int minWordsToSwitch = 2;

    [Tooltip("Enable to see detection scores in the console")]
    [SerializeField] private bool debugLog;

    public event Action<string> OnLanguageDetected;

    private Model[] _models;
    private VoskRecognizer[] _recognizers;
    private bool _initialized;

    private void Start()
    {
        Initialize();
    }

    private void OnEnable()
    {
        if (speechRecognizer != null)
            speechRecognizer.OnPcmData += FeedPcmData;
    }

    private void OnDisable()
    {
        if (speechRecognizer != null)
            speechRecognizer.OnPcmData -= FeedPcmData;
    }

    private void Initialize()
    {
        _models = new Model[detectionLanguages.Length];
        _recognizers = new VoskRecognizer[detectionLanguages.Length];

        for (var i = 0; i < detectionLanguages.Length; i++)
        {
            var lang = detectionLanguages[i];
            var fullPath = Path.Combine(Application.streamingAssetsPath, lang.smallModelFolder);

            if (!Directory.Exists(fullPath))
            {
                Debug.LogWarning($"[VoskLangDetect] Model not found for '{lang.languageName}' at: {fullPath}. " +
                                 "Download a small model from https://alphacephei.com/vosk/models");
                continue;
            }

            try
            {
                _models[i] = new Model(fullPath);
                _recognizers[i] = new VoskRecognizer(_models[i], sampleRate);
                _recognizers[i].SetMaxAlternatives(0);
                _recognizers[i].SetWords(true);

                if (debugLog)
                    Debug.Log($"[VoskLangDetect] Loaded detection model: {lang.languageName} ({lang.smallModelFolder})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VoskLangDetect] Failed to load '{lang.languageName}': {ex.Message}");
            }
        }

        _initialized = true;
    }

    private void FeedPcmData(byte[] pcmBytes)
    {
        if (!_initialized) return;

        var bestIndex = -1;
        var bestScore = 0f;
        var anyFinalized = false;

        for (var i = 0; i < _recognizers.Length; i++)
        {
            if (_recognizers[i] == null) continue;

            if (_recognizers[i].AcceptWaveform(pcmBytes, pcmBytes.Length))
            {
                anyFinalized = true;
                var json = _recognizers[i].Result();
                var score = ScoreResult(json);

                if (debugLog)
                    Debug.Log($"[VoskLangDetect] {detectionLanguages[i].languageName}: score={score:F2} | {json}");

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }
            else
            {
                // Consume data to keep recognizer in sync; ignore partial results.
            }
        }

        if (!anyFinalized || bestIndex < 0) return;

        // Reset all recognizers for the next phrase
        for (var i = 0; i < _recognizers.Length; i++)
            _recognizers[i]?.Reset();

        var detectedName = detectionLanguages[bestIndex].languageName;

        if (debugLog)
            Debug.Log($"[VoskLangDetect] Detected language: {detectedName} (score {bestScore:F2})");

        if (!string.Equals(speechRecognizer.CurrentLanguage, detectedName, StringComparison.OrdinalIgnoreCase))
        {
            Debug.Log($"[VoskLangDetect] Switching to {detectedName}");
            OnLanguageDetected?.Invoke(detectedName);
            speechRecognizer.SwitchLanguage(detectedName);
        }
    }

    /// <summary>
    /// Scores a Vosk final result JSON. Uses word count weighted by average confidence.
    /// Returns 0 when the result is empty or has fewer words than <see cref="minWordsToSwitch"/>.
    /// </summary>
    private float ScoreResult(string json)
    {
        var result = JsonUtility.FromJson<VoskWordResult>(json);

        if (result?.result == null || result.result.Length == 0)
        {
            // Fall back to plain text word count
            var textResult = JsonUtility.FromJson<VoskTextResult>(json);
            if (textResult == null || string.IsNullOrWhiteSpace(textResult.text))
                return 0f;

            var words = textResult.text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return words.Length >= minWordsToSwitch ? words.Length : 0f;
        }

        if (result.result.Length < minWordsToSwitch)
            return 0f;

        var totalConf = 0f;
        for (var i = 0; i < result.result.Length; i++)
            totalConf += result.result[i].conf;

        return totalConf; // sum of per-word confidences — higher = better
    }

    private void OnDestroy()
    {
        if (_recognizers != null)
        {
            for (var i = 0; i < _recognizers.Length; i++)
            {
                _recognizers[i]?.Dispose();
                _recognizers[i] = null;
            }
        }

        if (_models != null)
        {
            for (var i = 0; i < _models.Length; i++)
            {
                _models[i]?.Dispose();
                _models[i] = null;
            }
        }
    }

    [Serializable]
    private class VoskTextResult { public string text; }

    [Serializable]
    private class VoskWordResult { public VoskWordInfo[] result; }

    [Serializable]
    private class VoskWordInfo
    {
        public float conf;
        public float end;
        public float start;
        public string word;
    }
}
