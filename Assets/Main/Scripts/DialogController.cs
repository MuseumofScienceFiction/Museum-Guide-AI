using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class DialogController : MonoBehaviour
{
    private const string FunctionsBaseUrl = "https://us-central1-museumai-2a2e6.cloudfunctions.net";

    [SerializeField] private AudioSource[] audioSources;
    [SerializeField] private Animator animator;
    [SerializeField] private AudioClip errorClip;

    [Header("Testing")]
    [SerializeField] private bool useTestAnswerAudio;
    [SerializeField] private AudioClip testAnswerAudioClip;

    public void AskQuestion(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        StartCoroutine(AskQuestionCoroutine(message));
    }

    private IEnumerator AskQuestionCoroutine(string message)
    {
        var body = JsonUtility.ToJson(new RequestWrapper { data = new RequestData { question = message } });

        using (var request = new UnityWebRequest($"{FunctionsBaseUrl}/museumGuide", "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 120;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.Log("Error. Please try again later.");
                Debug.LogError($"Request error: {request.error}\n{request.downloadHandler.text}");
                yield break;
            }

            var response = JsonUtility.FromJson<ResponseWrapper>(request.downloadHandler.text);

            if (response.result != null && !string.IsNullOrEmpty(response.result.answer))
                Debug.Log(response.result.answer);
            else
                Debug.Log("Answer not found.");
        }
    }

    public void AskQuestionWithAudio(string message)
    {
        if (string.IsNullOrEmpty(message)) return;

        if (useTestAnswerAudio)
        {
            PlayAudio(testAnswerAudioClip);
            return;
        }

        StartCoroutine(AskQuestionWithAudioCoroutine(message));
    }

    private IEnumerator AskQuestionWithAudioCoroutine(string message)
    {
        var body = JsonUtility.ToJson(new RequestWrapper { data = new RequestData { question = message } });

        using (var request = new UnityWebRequest($"{FunctionsBaseUrl}/museumGuideWithAudio", "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 120;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.Log("Error. Please try again later.");
                PlayAudio(errorClip);
                Debug.LogError($"Request error: {request.error}\n{request.downloadHandler.text}");
                yield break;
            }

            var response = JsonUtility.FromJson<ResponseWrapper>(request.downloadHandler.text);
            if (response.result == null) yield break;

            if (!string.IsNullOrEmpty(response.result.answer))
                Debug.Log(response.result.answer);

            if (!string.IsNullOrEmpty(response.result.audioBase64))
                yield return PlayMp3FromBase64(response.result.audioBase64);
        }
    }

    private IEnumerator PlayMp3FromBase64(string base64)
    {
        var mp3Bytes = Convert.FromBase64String(base64);
        var path = Path.Combine(Application.temporaryCachePath, "guide_answer.mp3");
        File.WriteAllBytes(path, mp3Bytes);

        using (var www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.MPEG))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                var clip = DownloadHandlerAudioClip.GetContent(www);
                PlayAudio(clip);
            }
            else
            {
                Debug.LogError($"Playback error: {www.error}");
            }
        }
    }

    private void PlayAudio(AudioClip audioClip)
    {
        foreach (var audioSource in audioSources)
        {
            audioSource.clip = audioClip;
            audioSource.Play();
        }

        StartTalkAnimation();
    }

    private void StartTalkAnimation()
    {
        var trigger = UnityEngine.Random.Range(0, 2) == 0 ? "talk1" : "talk2";
        animator.SetTrigger(trigger);
    }

    [Serializable]
    private class RequestWrapper
    {
        public RequestData data;
    }

    [Serializable]
    private class RequestData
    {
        public string question;
    }

    [Serializable]
    private class ResponseWrapper
    {
        public ResponseResult result;
    }

    [Serializable]
    private class ResponseResult
    {
        public string answer;
        public string audioBase64;
    }
}
