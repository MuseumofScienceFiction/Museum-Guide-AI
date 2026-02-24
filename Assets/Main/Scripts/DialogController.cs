using Firebase.Extensions;
using Firebase.Functions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

public class DialogController : MonoBehaviour
{
    private FirebaseFunctions functions;
    [SerializeField] private AudioSource[] audioSources;
    [SerializeField] private Animator animator;
    [SerializeField] private AudioClip errorClip;

    [Header("Testing")]
    [SerializeField] private bool useTestAnswerAudio;
    [SerializeField] private AudioClip testAnswerAudioClip;

    private void Start()
    {
        functions = FirebaseFunctions.DefaultInstance;
    }

    public void AskQuestion(string message)
    {
        if (string.IsNullOrEmpty(message)) return;

        var data = new Dictionary<string, object>
        {
            { "question", message }
        };

        functions
            .GetHttpsCallable("museumGuide")
            .CallAsync(data)
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                {
                    Debug.Log("Error. Please try again later.");
                    Debug.LogError(task.Exception);
                    return;
                }

                var result = task.Result.Data as IDictionary<string, object>;

                if (result != null && result.TryGetValue("answer", out var ans))
                    Debug.Log(ans);
                else
                    Debug.Log("Answer not found.");
            });
    }

    public void AskQuestionWithAudio(string message)
    {
        if (string.IsNullOrEmpty(message)) return;

        if (useTestAnswerAudio)
        {
            PlayAudio(testAnswerAudioClip);
            return;
        }

        var data = new Dictionary<string, object>
        {
            { "question", message }
        };

        functions
            .GetHttpsCallable("museumGuideWithAudio")
            .CallAsync(data)
            .ContinueWithOnMainThread(task =>
            {
                if (task.IsFaulted)
                {
                    Debug.Log("Error. Please try again later.");
                    PlayAudio(errorClip);
                    Debug.LogError(task.Exception);
                    return;
                }

                var result = task.Result.Data as IDictionary<string, object>;
                if (result == null) return;

                if (result.TryGetValue("answer", out var ans))
                    Debug.Log(ans.ToString());

                if (result.TryGetValue("audioBase64", out var audio))
                {
                    var base64 = audio.ToString();
                    if (!string.IsNullOrEmpty(base64))
                        StartCoroutine(PlayMp3FromBase64(base64));
                }
            });
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
}
