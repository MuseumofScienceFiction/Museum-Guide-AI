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
    private const int RequestTimeout = 120;

    [SerializeField] private AudioSource[] audioSources;
    [SerializeField] private Animator animator;
    [SerializeField] private AudioClip errorClip;
    [SerializeField] private GameObject thinkingIndicator;
    [SerializeField] private TMP_Text answerText;

    [Header("Testing")]
    [SerializeField] private bool useTestAnswerAudio;
    [SerializeField] private AudioClip testAnswerAudioClip;

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

        StartCoroutine(AskQuestionWithAudioCoroutine(message));
    }

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
        thinkingIndicator.SetActive(value);
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

    [Serializable] private class RequestWrapper { public RequestData data; }
    [Serializable] private class RequestData { public string question; }
    [Serializable] private class ResponseWrapper { public ResponseResult result; }
    [Serializable] private class ResponseResult { public string answer; public string audioBase64; }
}
