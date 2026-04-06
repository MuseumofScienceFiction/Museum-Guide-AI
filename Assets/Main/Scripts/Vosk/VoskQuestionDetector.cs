using TMPro;
using UnityEngine;

public class VoskQuestionDetector : MonoBehaviour
{
    [SerializeField] private VoskSpeechRecognizer voskRecognizer;
    [SerializeField] private TMP_Text partialText;

    private void Awake()
    {
        if (voskRecognizer != null)
            voskRecognizer.OnPartialResult += OnPartialResult;
    }

    private void OnPartialResult(string partial)
    {
        if (partialText != null)
            partialText.text = partial;
    }

    private void OnDestroy()
    {
        if (voskRecognizer != null)
            voskRecognizer.OnPartialResult -= OnPartialResult;
    }
}
