using System;
using System.Collections;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

public class DialogController : MonoBehaviour
{
    private const string FunctionsBaseUrl = "https://us-central1-museumai-2a2e6.cloudfunctions.net";
    private const string ElevenLabsUrl = "https://api.elevenlabs.io/v1/text-to-speech";
    private const string ElevenLabsVoiceId = "TfOkTMvLYzgpJ01mn1zA";
    private const int RequestTimeout = 120;

    [SerializeField] private AudioSource[] audioSources;
    [SerializeField] private Animator animator;
    [SerializeField] private AudioClip errorClip;
    [SerializeField] private CharecterEffectsHelper charecterEffectsHelper;
    [SerializeField] private TMP_Text answerText;

    [Header("ElevenLabs")]
    [SerializeField] private string elevenLabsApiKey;

    [Header("Testing")]
    [SerializeField] private bool useTestAnswerAudio;
    [SerializeField] private AudioClip testAnswerAudioClip;
    [SerializeField] private bool useDirectElevenLabs; // Новый флаг для прямого обращения

    public void AskQuestion(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        SetThinking(true);
        StartCoroutine(AskQuestionCoroutine(message));
    }

    public void AskQuestionWithAudio(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        SetThinking(true);
        StopAllAudio();

        if (useTestAnswerAudio)
        {
            PlayAudio(testAnswerAudioClip);
            return;
        }

        if (useDirectElevenLabs)
        {
            StartCoroutine(AskQuestionWithAudioDirectCoroutine(message));
        }
        else
        {
            StartCoroutine(AskQuestionWithAudioCoroutine(message));
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // СТАРЫЕ МЕТОДЫ (остаются неизменными)
    // ════════════════════════════════════════════════════════════════════════

    private IEnumerator AskQuestionCoroutine(string message)
    {
        using var request = CreatePostRequest("museumGuide", message);
        yield return request.SendWebRequest();

        var result = ParseResponse(request);

        if (result == null)
        {
            SetAnswer("Error. Please try again later.");
            SetThinking(false);
            yield break;
        }

        SetAnswer(!string.IsNullOrEmpty(result.answer) ? result.answer : "Answer not found.");
        SetThinking(false);
    }

    private IEnumerator AskQuestionWithAudioCoroutine(string message)
    {
        using var request = CreatePostRequest("museumGuideWithAudio", message);
        yield return request.SendWebRequest();

        var result = ParseResponse(request);

        if (result == null)
        {
            SetAnswer("Error. Please try again later.");
            PlayAudio(errorClip);
            yield break;
        }

        if (!string.IsNullOrEmpty(result.answer))
            SetAnswer(result.answer);

        if (!string.IsNullOrEmpty(result.audioBase64))
            yield return PlayMp3FromBase64(result.audioBase64);
        else
            SetThinking(false);
    }

    // ════════════════════════════════════════════════════════════════════════
    // НОВЫЙ МЕТОД: Text → ElevenLabs HTTP (прямой)
    // ════════════════════════════════════════════════════════════════════════

    private IEnumerator AskQuestionWithAudioDirectCoroutine(string message)
    {
        // 1. Получить текстовый ответ от Firebase
        using var textRequest = CreatePostRequest("museumGuide", message);
        yield return textRequest.SendWebRequest();

        var result = ParseResponse(textRequest);

        if (result == null || string.IsNullOrEmpty(result.answer))
        {
            SetAnswer("Error. Please try again later.");
            PlayAudio(errorClip);
            yield break;
        }

        // Показать текст сразу
        SetAnswer(result.answer);

        // 2. Отправить текст напрямую в ElevenLabs API
        yield return StartCoroutine(PlayAudioFromElevenLabsDirect(result.answer));
    }

    private IEnumerator PlayAudioFromElevenLabsDirect(string text)
    {
        if (string.IsNullOrEmpty(elevenLabsApiKey))
        {
            Debug.LogError("ElevenLabs API Key not set in Inspector!");
            PlayAudio(errorClip);
            yield break;
        }

        string url = $"{ElevenLabsUrl}/{ElevenLabsVoiceId}";

        // Создать TTS request с правильной сериализацией
        var ttsRequest = new ElevenLabsTTSRequest
        {
            text = text,
            model_id = "eleven_multilingual_v2",
            voice_settings = new VoiceSettings
            {
                stability = 0.5f,
                similarity_boost = 0.8f
            }
        };

        string jsonBody = JsonUtility.ToJson(ttsRequest);
        Debug.Log($"ElevenLabs Request: {jsonBody}");

        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);

        using var request = new UnityWebRequest(url, "POST")
        {
            uploadHandler = new UploadHandlerRaw(bodyRaw),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = RequestTimeout
        };

        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("xi-api-key", elevenLabsApiKey);
        request.SetRequestHeader("Accept", "audio/mpeg");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            var mp3Bytes = request.downloadHandler.data;

            if (mp3Bytes != null && mp3Bytes.Length > 0)
            {
                var path = Path.Combine(Application.temporaryCachePath, $"guide_{System.Guid.NewGuid()}.mp3");
                File.WriteAllBytes(path, mp3Bytes);

                using var audioRequest = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.MPEG);
                yield return audioRequest.SendWebRequest();

                if (audioRequest.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(audioRequest);
                    PlayAudio(clip);

                    yield return new WaitForSeconds(clip.length + 1f);
                    try { File.Delete(path); } catch { }
                }
                else
                {
                    Debug.LogError($"Audio decode error: {audioRequest.error}");
                    PlayAudio(errorClip);
                }
            }
            else
            {
                Debug.LogError("No audio data received");
                PlayAudio(errorClip);
            }
        }
        else
        {
            Debug.LogError($"ElevenLabs error: {request.responseCode}\nResponse: {request.downloadHandler.text}");
            PlayAudio(errorClip);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ (неизменные)
    // ════════════════════════════════════════════════════════════════════════

    private UnityWebRequest CreatePostRequest(string functionName, string message)
    {
        var body = JsonUtility.ToJson(new RequestWrapper { data = new RequestData { question = message } });
        var request = new UnityWebRequest($"{FunctionsBaseUrl}/{functionName}", "POST")
        {
            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = RequestTimeout
        };
        request.SetRequestHeader("Content-Type", "application/json");
        return request;
    }

    private ResponseResult ParseResponse(UnityWebRequest request)
    {
        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"Request error: {request.error}\n{request.downloadHandler.text}");
            return null;
        }

        return JsonUtility.FromJson<ResponseWrapper>(request.downloadHandler.text)?.result;
    }

    private void SetAnswer(string text)
    {
        Debug.Log(text);
        answerText.text = text;
    }

    private void SetThinking(bool value)
    {
        animator.SetBool("thinking", value);
        charecterEffectsHelper.IsThinking = value;
    }

    private void StopAllAudio()
    {
        foreach (var audioSource in audioSources)
        {
            audioSource.Stop();
            audioSource.clip = null;
        }
    }

    private IEnumerator PlayMp3FromBase64(string base64)
    {
        var mp3Bytes = Convert.FromBase64String(base64);
        var path = Path.Combine(Application.temporaryCachePath, "guide_answer.mp3");
        File.WriteAllBytes(path, mp3Bytes);

        using var www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.MPEG);
        yield return www.SendWebRequest();

        if (www.result == UnityWebRequest.Result.Success)
            PlayAudio(DownloadHandlerAudioClip.GetContent(www));
        else
            Debug.LogError($"Playback error: {www.error}");
    }

    private void PlayAudio(AudioClip audioClip)
    {
        SetThinking(false);

        foreach (var audioSource in audioSources)
        {
            audioSource.clip = audioClip;
            audioSource.Play();
        }

        animator.SetTrigger(UnityEngine.Random.Range(0, 2) == 0 ? "talk1" : "talk2");
    }

    // ════════════════════════════════════════════════════════════════════════
    // JSON CLASSES
    // ════════════════════════════════════════════════════════════════════════

    [System.Serializable]
    private class ElevenLabsTTSRequest
    {
        public string text;
        public string model_id;
        public VoiceSettings voice_settings;
    }

    [System.Serializable]
    private class VoiceSettings
    {
        public float stability;
        public float similarity_boost;
    }

    [Serializable] private class RequestWrapper { public RequestData data; }
    [Serializable] private class RequestData { public string question; }
    [Serializable] private class ResponseWrapper { public ResponseResult result; }
    [Serializable] private class ResponseResult { public string answer; public string audioBase64; }
}