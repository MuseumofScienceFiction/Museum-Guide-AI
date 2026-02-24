using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Whisper;
using Whisper.Utils;

public class QuestionDetector : MonoBehaviour
{
    [SerializeField] private WhisperManager whisper;
    [SerializeField] private MicrophoneRecord microphoneRecord;
    [SerializeField] private Button askButton;
    [SerializeField] private DialogController dialogController;

    private TMP_Text buttonLabel;

    private void Awake()
    {
        buttonLabel = askButton.GetComponentInChildren<TMP_Text>();
        askButton.onClick.AddListener(OnAskButtonClicked);
        microphoneRecord.OnRecordStop += OnRecordStopAsync;
    }

    private void OnAskButtonClicked()
    {
        if (!microphoneRecord.IsRecording)
        {
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

        var res = await whisper.GetTextAsync(recordedAudio.Data, recordedAudio.Frequency, recordedAudio.Channels);

        if (res == null)
            return;

        Debug.Log($"Transcription: {res.Result}");
        dialogController.AskQuestionWithAudio(res.Result);
    }
}
