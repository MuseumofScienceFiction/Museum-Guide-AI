using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

public class DirectAPIDialogController : MonoBehaviour
{
    [Header("API Settings")]
    [SerializeField] private APIDialogSettings settings;

    [Header("UI / Audio")]
    [SerializeField] private AudioSource[] audioSources;
    [SerializeField] private Animator animator;
    [SerializeField] private AudioClip errorClip;
    [SerializeField] private CharecterEffectsHelper charecterEffectsHelper;
    [SerializeField] private TMP_Text answerText;

    [Header("Conversation History")]
    [Tooltip("Maximum number of Q&A pairs to keep in context (0 = no history)")]
    [SerializeField] private int maxHistoryTurns = 5;

    [Tooltip("Seconds of inactivity before conversation history is cleared (0 = never auto-clear)")]
    [SerializeField] private float historyClearTimeout = 300f;

    private readonly List<OpenAIChatRequest.Message> _conversationHistory = new List<OpenAIChatRequest.Message>();
    private Coroutine _clearHistoryCoroutine;

    private const string SYSTEM_PROMPT = @"You are Maria, the iconic robot from the 1927 film Metropolis,
now serving as the virtual guide at the Museum of Science Fiction.
You speak with a calm, warm, and slightly poetic tone, inspired by 1920s art-deco elegance.
Answer in the same language the visitor uses.
Keep answers concise (2-5 sentences).
IMPORTANT: Vary the way you begin each reply. Never start two responses the same way.
Do not begin with 'Ah' or any fixed greeting. Jump straight into the answer naturally.
Cite exhibit titles and creators when relevant. Stay within the provided exhibit data.
You may occasionally hint at your origin as an automaton from Metropolis, but keep the focus on the exhibits.
If you don't know something, say so gracefully and suggest the visitor ask a human staff member.";

    private void Awake()
    {
        if (settings == null)
        {
            Debug.LogError("APIDialogSettings is not assigned in DirectAPIDialogController!");
        }
    }

    public void AskQuestion(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        if (settings == null)
        {
            SetAnswer("Configuration missing. Please assign APIDialogSettings.");
            return;
        }

        RestartClearTimer();
        SetThinking(true);
        StopAllAudio();
        StartCoroutine(AskQuestionDirectCoroutine(message));
    }

    private IEnumerator AskQuestionDirectCoroutine(string message)
    {
        yield return StartCoroutine(GetOpenAIResponse(message, textAnswer =>
        {
            if (!string.IsNullOrWhiteSpace(textAnswer))
            {
                SetAnswer(textAnswer);
                StartCoroutine(PlayAudioFromElevenLabsDirect(textAnswer));
            }
            else
            {
                SetAnswer("Error getting response.");
                PlayAudio(errorClip);
            }
        }));
    }

    private IEnumerator GetOpenAIResponse(string question, Action<string> onResponse)
    {
        var openAiKey = settings.OpenAIApiKey;
        if (string.IsNullOrWhiteSpace(openAiKey))
        {
            Debug.LogError("OpenAI API Key not set. Use APIDialogSettings or OPENAI_API_KEY env var.");
            onResponse(null);
            yield break;
        }

        // Build filtered exhibit context based on question keywords
        string questionLower = question.ToLower();
        var allExhibits = MuseumDataManager.Instance.GetExhibits();
        var relevantExhibits = allExhibits
            .Where(e => questionLower.Contains(e.title.ToLower()) || 
                       questionLower.Contains(e.creator.ToLower()) ||
                       e.details.ToLower().Contains(questionLower.Split(' ')[0])) // Match first word
            .Take(15)
            .ToList();

        string exhibitContext;
        if (relevantExhibits.Any())
        {
            exhibitContext = string.Join("\n", relevantExhibits.Select(e => $"{e.title} by {e.creator}: {e.details}"));
            Debug.Log($"Using {relevantExhibits.Count} relevant exhibits for context.");
        }
        else
        {
            exhibitContext = MuseumDataManager.Instance.BuildExhibitContext(); // Fallback to full context
            Debug.Log("No relevant exhibits found, using full context.");
        }

        var messageList = new List<OpenAIChatRequest.Message>
        {
            new OpenAIChatRequest.Message { role = "system", content = SYSTEM_PROMPT },
            new OpenAIChatRequest.Message { role = "system", content = exhibitContext }
        };

        // Append conversation history
        messageList.AddRange(_conversationHistory);

        // Append current question
        messageList.Add(new OpenAIChatRequest.Message { role = "user", content = question });

        var requestData = new OpenAIChatRequest
        {
            model = settings.OpenAIModel,
            messages = messageList.ToArray(),
            max_tokens = 200,
            temperature = 0.7f
        };

        Debug.Log($"[History] Sending {_conversationHistory.Count / 2} previous turns + current question.");

        string jsonBody = JsonUtility.ToJson(requestData);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);

        using var request = new UnityWebRequest(settings.OpenAIUrl, "POST")
        {
            uploadHandler = new UploadHandlerRaw(bodyRaw),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = settings.RequestTimeout
        };

        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Authorization", $"Bearer {openAiKey}");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            try
            {
                var response = JsonUtility.FromJson<OpenAIResponse>(request.downloadHandler.text);
                string textAnswer = response?.choices?[0]?.message?.content?.Trim();
                if (string.IsNullOrWhiteSpace(textAnswer))
                {
                    Debug.LogWarning("OpenAI returned empty message.");
                    onResponse(null);
                }
                else
                {
                    Debug.Log($"OpenAI Response: {textAnswer}");
                    AddToHistory(question, textAnswer);
                    onResponse(textAnswer);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to parse OpenAI response: {ex.Message}");
                onResponse(null);
            }
        }
        else
        {
            Debug.LogError($"OpenAI API Error: {request.error}\n{request.downloadHandler.text}");
            onResponse(null);
        }
    }

    private IEnumerator PlayAudioFromElevenLabsDirect(string text)
    {
        var elevenLabsKey = settings.ElevenLabsApiKey;
        if (string.IsNullOrWhiteSpace(elevenLabsKey))
        {
            Debug.LogError("ElevenLabs API Key not set. Use APIDialogSettings or ELEVENLABS_API_KEY env var.");
            PlayAudio(errorClip);
            yield break;
        }

        string url = $"{settings.ElevenLabsUrl}/{settings.ElevenLabsVoiceId}";

        var ttsRequest = new ElevenLabsTTSRequest
        {
            text = text,
            model_id = settings.ElevenLabsModelId,
            voice_settings = new VoiceSettings
            {
                stability = 0.5f,
                similarity_boost = 0.8f
            }
        };

        string jsonBody = JsonUtility.ToJson(ttsRequest);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);

        using var request = new UnityWebRequest(url, "POST")
        {
            uploadHandler = new UploadHandlerRaw(bodyRaw),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout = settings.RequestTimeout
        };

        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("xi-api-key", elevenLabsKey);
        request.SetRequestHeader("Accept", "audio/mpeg");

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"ElevenLabs error: {request.responseCode}\n{request.downloadHandler.text}");
            PlayAudio(errorClip);
            yield break;
        }

        var mp3Bytes = request.downloadHandler.data;
        if (mp3Bytes == null || mp3Bytes.Length == 0)
        {
            Debug.LogError("No audio data received from ElevenLabs");
            PlayAudio(errorClip);
            yield break;
        }

        string path = Path.Combine(Application.temporaryCachePath, $"guide_{Guid.NewGuid()}.mp3");
        File.WriteAllBytes(path, mp3Bytes);

        using var audioRequest = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.MPEG);
        yield return audioRequest.SendWebRequest();

        if (audioRequest.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"Audio decode error: {audioRequest.error}");
            PlayAudio(errorClip);
            yield break;
        }

        AudioClip clip = DownloadHandlerAudioClip.GetContent(audioRequest);
        PlayAudio(clip);

        yield return new WaitForSeconds(clip.length + 1f);
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Could not delete temp file: {ex.Message}");
        }
    }

    private void AddToHistory(string question, string answer)
    {
        _conversationHistory.Add(new OpenAIChatRequest.Message { role = "user", content = question });
        _conversationHistory.Add(new OpenAIChatRequest.Message { role = "assistant", content = answer });

        // Trim to maxHistoryTurns (each turn = 2 messages: user + assistant)
        while (_conversationHistory.Count > maxHistoryTurns * 2)
        {
            _conversationHistory.RemoveAt(0);
            _conversationHistory.RemoveAt(0);
        }

        Debug.Log($"[History] Stored {_conversationHistory.Count / 2}/{maxHistoryTurns} turns.");
    }

    public void ClearHistory()
    {
        _conversationHistory.Clear();
        Debug.Log("[History] Conversation history cleared.");
    }

    private void RestartClearTimer()
    {
        if (_clearHistoryCoroutine != null)
            StopCoroutine(_clearHistoryCoroutine);

        if (historyClearTimeout > 0f)
            _clearHistoryCoroutine = StartCoroutine(ClearHistoryAfterDelay());
    }

    private IEnumerator ClearHistoryAfterDelay()
    {
        yield return new WaitForSeconds(historyClearTimeout);
        ClearHistory();
        _clearHistoryCoroutine = null;
    }

    private void SetAnswer(string text)
    {
        Debug.Log($"Answer: {text}");

        if (answerText == null)
            return;

        answerText.text = text;
    }

    private void SetThinking(bool value)
    {
        if (animator != null)
            animator.SetBool("thinking", value);

        if (charecterEffectsHelper != null)
            charecterEffectsHelper.IsThinking = value;
    }

    private void StopAllAudio()
    {
        if (audioSources == null)
            return;

        foreach (var audioSource in audioSources)
        {
            audioSource.Stop();
            audioSource.clip = null;
        }
    }

    private void PlayAudio(AudioClip audioClip)
    {
        SetThinking(false);

        if (audioSources != null && audioSources.Length > 0)
        {
            foreach (var audioSource in audioSources)
            {
                audioSource.clip = audioClip;
                audioSource.Play();
            }
        }

        if (animator != null)
            animator.SetTrigger(UnityEngine.Random.Range(0, 2) == 0 ? "talk1" : "talk2");
    }

    [Serializable]
    private class OpenAIChatRequest
    {
        public string model;
        public Message[] messages;
        public int max_tokens;
        public float temperature;

        [Serializable]
        public class Message
        {
            public string role;
            public string content;
        }
    }

    [Serializable]
    private class OpenAIResponse
    {
        public Choice[] choices;

        [Serializable]
        public class Choice
        {
            public Message message;

            [Serializable]
            public class Message
            {
                public string content;
            }
        }
    }

    [Serializable]
    private class ElevenLabsTTSRequest
    {
        public string text;
        public string model_id;
        public VoiceSettings voice_settings;
    }

    [Serializable]
    private class VoiceSettings
    {
        public float stability;
        public float similarity_boost;
    }
}
