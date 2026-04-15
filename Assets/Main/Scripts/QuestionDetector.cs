using System;
using System.Collections;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Whisper;
using Whisper.Utils;

public enum WhisperMode
{
    Local,
    OpenAI
}

public class QuestionDetector : MonoBehaviour
{
    [Header("Whisper Mode")]
    [Tooltip("Local = whisper.unity on device, OpenAI = Whisper API via network")]
    [SerializeField] private WhisperMode whisperMode = WhisperMode.Local;

    [Header("Local Whisper")]
    [SerializeField] private WhisperManager whisper;

    [Header("OpenAI Whisper API")]
    [SerializeField] private APIDialogSettings apiSettings;
    [Tooltip("Optional language hint for OpenAI Whisper (ISO-639-1, e.g. 'en', 'ru'). Leave empty for auto-detect.")]
    [SerializeField] private string openAIWhisperLanguage = "";

    [Header("Recording")]
    [SerializeField] private MicrophoneRecord microphoneRecord;

    [Header("UI")]
    [SerializeField] private Button askButton;
    [SerializeField] private TMP_Text questionText;
    [SerializeField] private TMP_Text answerText;
    [SerializeField] private Button exitButton;

    [Header("Controllers")]
    [SerializeField] private DirectAPIDialogController directDialogController;
    [SerializeField] private VoskSpeechRecognizer voskRecognizer;

    private TMP_Text buttonLabel;

    private void Awake()
    {
        buttonLabel = askButton.GetComponentInChildren<TMP_Text>();
        askButton.onClick.AddListener(OnAskButtonClicked);
        microphoneRecord.OnRecordStop += OnRecordStopAsync;
        exitButton.onClick.AddListener(OnExitButtonClicked);
    }

    private void OnAskButtonClicked()
    {
        if (!microphoneRecord.IsRecording)
        {
            if (voskRecognizer != null)
                voskRecognizer.StopListening();

            microphoneRecord.StartRecord();
            buttonLabel.text = "Stop";
        }
        else
        {
            microphoneRecord.StopRecord();
            buttonLabel.text = "Ask";
        }
    }

    private async void OnRecordStopAsync(AudioChunk recordedAudio)
    {
        buttonLabel.text = "Ask";

        string transcription = null;

        switch (whisperMode)
        {
            case WhisperMode.Local:
                var res = await whisper.GetTextAsync(recordedAudio.Data, recordedAudio.Frequency, recordedAudio.Channels);
                if (res != null)
                    transcription = res.Result;
                break;

            case WhisperMode.OpenAI:
                transcription = await TranscribeViaOpenAIAsync(recordedAudio);
                break;
        }

        if (string.IsNullOrWhiteSpace(transcription))
            return;

        questionText.text = transcription;
        answerText.text = "";
        Debug.Log($"Transcription ({whisperMode}): {transcription}");
        directDialogController.AskQuestion(transcription);

        if (voskRecognizer != null)
            voskRecognizer.StartListening();
    }

    private async Awaitable<string> TranscribeViaOpenAIAsync(AudioChunk audio)
    {
        var apiKey = apiSettings != null ? apiSettings.OpenAIApiKey : null;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Debug.LogError("[QuestionDetector] OpenAI API Key not set. Cannot use OpenAI Whisper mode.");
            return null;
        }

        var wavBytes = AudioChunkToWav(audio);

        var form = new WWWForm();
        form.AddBinaryData("file", wavBytes, "audio.wav", "audio/wav");
        form.AddField("model", apiSettings.OpenAIWhisperModel);
        if (!string.IsNullOrWhiteSpace(openAIWhisperLanguage))
            form.AddField("language", openAIWhisperLanguage);

        using var request = UnityWebRequest.Post(apiSettings.OpenAIWhisperUrl, form);
        request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
        request.timeout = apiSettings.RequestTimeout;

        await request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[QuestionDetector] OpenAI Whisper API error: {request.error}\n{request.downloadHandler.text}");
            return null;
        }

        var response = JsonUtility.FromJson<OpenAIWhisperResponse>(request.downloadHandler.text);
        return response?.text;
    }

    private static byte[] AudioChunkToWav(AudioChunk audio)
    {
        var samples = audio.Data;
        int sampleCount = samples.Length;
        int channelCount = audio.Channels;
        int sampleRate = audio.Frequency;
        int bitsPerSample = 16;
        int byteRate = sampleRate * channelCount * bitsPerSample / 8;
        int blockAlign = channelCount * bitsPerSample / 8;
        int dataSize = sampleCount * blockAlign / channelCount;

        using var stream = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(stream);

        // RIFF header
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)channelCount);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);

        // data chunk
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);

        for (int i = 0; i < sampleCount; i++)
        {
            var clamped = Mathf.Clamp(samples[i], -1f, 1f);
            writer.Write((short)(clamped * short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }

    private void OnExitButtonClicked()
    {
        Application.Quit();
    }

    [Serializable]
    private class OpenAIWhisperResponse
    {
        public string text;
    }
}
