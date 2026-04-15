using System;
using UnityEngine;

[CreateAssetMenu(fileName = "APIDialogSettings", menuName = "MuseumGuide/APIDialogSettings", order = 100)]
public class APIDialogSettings : ScriptableObject
{
    [Header("Endpoints")]
    [SerializeField] private string openAIUrl = "https://api.openai.com/v1/chat/completions";
    [SerializeField] private string openAIWhisperUrl = "https://api.openai.com/v1/audio/transcriptions";
    [SerializeField] private string elevenLabsUrl = "https://api.elevenlabs.io/v1/text-to-speech";
    [SerializeField] private string elevenLabsVoiceId = "TfOkTMvLYzgpJ01mn1zA";

    [Header("Timeout")]
    [SerializeField] private int requestTimeout = 120;

    [Header("API Keys")]
    [SerializeField, HideInInspector, Tooltip("Use environment variables or secure key vault for production. This field is here for local testing only.")]
    private string openAiApiKey;

    [SerializeField, HideInInspector, Tooltip("Use environment variables or secure key vault for production. This field is here for local testing only.")]
    private string elevenLabsApiKey;

    [Header("Model")]
    [SerializeField] private string openAIModel = "gpt-4o-mini";
    [SerializeField] private string openAIWhisperModel = "whisper-1";
    [SerializeField] private string elevenLabsModelId = "eleven_multilingual_v2";

    public string OpenAIUrl => string.IsNullOrWhiteSpace(openAIUrl) ? "https://api.openai.com/v1/chat/completions" : openAIUrl;
    public string OpenAIWhisperUrl => string.IsNullOrWhiteSpace(openAIWhisperUrl) ? "https://api.openai.com/v1/audio/transcriptions" : openAIWhisperUrl;
    public string ElevenLabsUrl => string.IsNullOrWhiteSpace(elevenLabsUrl) ? "https://api.elevenlabs.io/v1/text-to-speech" : elevenLabsUrl;
    public string ElevenLabsVoiceId => elevenLabsVoiceId;
    public int RequestTimeout => requestTimeout;

    public string OpenAIModel => openAIModel;
    public string OpenAIWhisperModel => openAIWhisperModel;
    public string ElevenLabsModelId => elevenLabsModelId;

    public string OpenAIApiKey
    {
        get
        {
            var key = !string.IsNullOrWhiteSpace(openAiApiKey) ? openAiApiKey : Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            return key;
        }
    }

    public string ElevenLabsApiKey
    {
        get
        {
            var key = !string.IsNullOrWhiteSpace(elevenLabsApiKey) ? elevenLabsApiKey : Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY");
            return key;
        }
    }

    public void SetOpenAIApiKey(string key)
    {
        openAiApiKey = key;
    }

    public void SetElevenLabsApiKey(string key)
    {
        elevenLabsApiKey = key;
    }
}