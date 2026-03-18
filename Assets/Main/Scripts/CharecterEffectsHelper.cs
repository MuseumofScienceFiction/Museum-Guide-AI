using UnityEngine;

public class CharecterEffectsHelper : MonoBehaviour
{
    private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
    private const float IdleIntensity = 512f;
    private const float PulseSpeed = 30000f;
    private const float PulseMax = 10000f;

    [SerializeField] private new Renderer renderer;

    public bool IsThinking;

    private Material[] materials;
    private Color[] baseColors;
    private bool[] hasEmissive;
    private bool wasThinking = true;

    private void Start()
    {
        materials = renderer.materials;
        var count = materials.Length;
        baseColors = new Color[count];
        hasEmissive = new bool[count];

        for (var i = 0; i < count; i++)
        {
            if (!materials[i].HasProperty(EmissiveColorId)) continue;

            hasEmissive[i] = true;
            var hdr = materials[i].GetColor(EmissiveColorId);
            var max = Mathf.Max(hdr.r, hdr.g, hdr.b, 0.001f);
            baseColors[i] = new Color(hdr.r / max, hdr.g / max, hdr.b / max);
        }
    }

    private void Update()
    {
        if (IsThinking)
        {
            wasThinking = true;
            SetEmissionIntensity(Mathf.PingPong(Time.time * PulseSpeed, PulseMax));
        }
        else if (wasThinking)
        {
            wasThinking = false;
            SetEmissionIntensity(IdleIntensity);
        }
    }

    private void SetEmissionIntensity(float intensity)
    {
        for (var i = 0; i < materials.Length; i++)
        {
            if (!hasEmissive[i]) continue;
            materials[i].SetColor(EmissiveColorId, baseColors[i] * intensity);
        }
    }

    private void OnDestroy()
    {
        if (materials == null) return;

        foreach (var material in materials)
            Destroy(material);
    }
}
