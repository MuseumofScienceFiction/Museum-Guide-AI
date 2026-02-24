/************************************************************************************
Content     :   Adds natural idle facial micro-animations to a CC5 character.
                Involuntary blinking (randomized intervals, asymmetric curves),
                subtle micro-movements of brows, eyelids, nostrils, and cheeks
                driven by layered Perlin noise. Designed to complement
                CC5LipSyncController without conflicting blendshapes.
                Supports prefixed blendshape names (e.g. "CC_Game_Body.Eye_Blink_L").
************************************************************************************/
using System;
using System.Collections.Generic;
using UnityEngine;

public class CC5FacialIdleAnimator : MonoBehaviour
{
    // ─── Inspector ──────────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("SkinnedMeshRenderer of the CC5 head/face mesh (same as lip sync controller).")]
    public SkinnedMeshRenderer skinnedMeshRenderer;

    [Header("Blinking")]
    [Tooltip("Enable involuntary blinking.")]
    public bool enableBlinking = true;

    [Range(1f, 10f)]
    [Tooltip("Minimum interval between blinks (seconds).")]
    public float blinkIntervalMin = 2.0f;

    [Range(1f, 10f)]
    [Tooltip("Maximum interval between blinks (seconds).")]
    public float blinkIntervalMax = 6.0f;

    [Range(0.01f, 0.2f)]
    [Tooltip("How fast the eyelid closes (seconds).")]
    public float blinkCloseSpeed = 0.075f;

    [Range(0.05f, 0.4f)]
    [Tooltip("How fast the eyelid opens back up (seconds).")]
    public float blinkOpenSpeed = 0.15f;

    [Range(0f, 0.1f)]
    [Tooltip("Pause at fully closed position (seconds).")]
    public float blinkClosedPause = 0.04f;

    [Range(0f, 0.4f)]
    [Tooltip("Probability of a double blink (0 = never, 1 = always).")]
    public float doubleBlikProbability = 0.15f;

    [Range(0f, 100f)]
    [Tooltip("Maximum blink blendshape weight (0–100).")]
    public float blinkWeight = 100f;

    [Header("Micro-Movements")]
    [Tooltip("Enable subtle facial micro-movements (brows, eyelids, nose, cheeks).")]
    public bool enableMicroMovements = true;

    [Range(0f, 30f)]
    [Tooltip("Global intensity of micro-movements (blendshape weight 0–100 range).")]
    public float microIntensity = 8f;

    [Range(0.05f, 1f)]
    [Tooltip("Base speed of Perlin noise animation (lower = slower, more natural).")]
    public float microSpeed = 0.2f;

    [Header("Debug")]
    [Tooltip("Log detected blendshape indices on Start.")]
    public bool logDetection = false;

    // ─── Blink State ────────────────────────────────────────────────────
    private enum BlinkPhase { Idle, Closing, Closed, Opening }
    private BlinkPhase _blinkPhase = BlinkPhase.Idle;
    private float _blinkTimer;
    private float _nextBlinkTime;
    private float _blinkValue;        // 0 = open, 1 = fully closed
    private bool _doubleBlikPending;

    // ─── Blendshape Indices ─────────────────────────────────────────────
    private int _eyeBlinkL = -1;
    private int _eyeBlinkR = -1;

    // Micro-movement channels: each has an index + Perlin offset
    private struct MicroChannel
    {
        public int index;
        public float noiseOffsetA;   // primary Perlin seed
        public float noiseOffsetB;   // secondary layer
        public float speedMul;       // per-channel speed variation
        public float intensityMul;   // per-channel intensity scale (0..1)
    }

    private readonly List<MicroChannel> _microChannels = new List<MicroChannel>();

    // Name → index dictionary with prefix stripping
    private Dictionary<string, int> _nameToIndex;

    // ─── Unity Lifecycle ────────────────────────────────────────────────
    void Start()
    {
        if (skinnedMeshRenderer == null)
        {
            Debug.LogError("[CC5FacialIdle] SkinnedMeshRenderer is not assigned!");
            enabled = false;
            return;
        }

        BuildNameIndex();
        DetectBlinkShapes();
        BuildMicroChannels();
        ScheduleNextBlink();

        if (logDetection)
            LogDetectedShapes();

        Debug.Log($"[CC5FacialIdle] Initialized — blink: {(_eyeBlinkL >= 0 ? "OK" : "NOT FOUND")}, " +
                  $"micro channels: {_microChannels.Count}");
    }

    void Update()
    {
        if (skinnedMeshRenderer == null) return;

        if (enableBlinking)
            UpdateBlink();

        if (enableMicroMovements)
            UpdateMicroMovements();
    }

    // ─── Name Index (same logic as CC5LipSyncController) ────────────────
    private void BuildNameIndex()
    {
        _nameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Mesh mesh = skinnedMeshRenderer.sharedMesh;
        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            string fullName = mesh.GetBlendShapeName(i);
            _nameToIndex[fullName] = i;

            int dotIdx = fullName.LastIndexOf('.');
            if (dotIdx >= 0 && dotIdx < fullName.Length - 1)
            {
                string shortName = fullName.Substring(dotIdx + 1);
                if (!_nameToIndex.ContainsKey(shortName))
                    _nameToIndex[shortName] = i;
            }
        }
    }

    private int FindShape(params string[] candidates)
    {
        foreach (string name in candidates)
        {
            // Exact / short-name match
            if (_nameToIndex.TryGetValue(name, out int idx))
                return idx;

            // Partial match
            foreach (var kvp in _nameToIndex)
                if (kvp.Key.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return kvp.Value;

            // Fuzzy: strip underscores
            string norm = name.Replace("_", "").ToLowerInvariant();
            foreach (var kvp in _nameToIndex)
            {
                string k = kvp.Key.Replace("_", "").ToLowerInvariant();
                int d = k.LastIndexOf('.');
                if (d >= 0) k = k.Substring(d + 1);
                if (k == norm) return kvp.Value;
            }
        }
        return -1;
    }

    // ─── Detect blink blendshapes ───────────────────────────────────────
    private void DetectBlinkShapes()
    {
        // Try multiple naming conventions: ARKit, ExPlus, Traditional
        _eyeBlinkL = FindShape("eyeBlinkLeft", "Eye_Blink_L", "Eye_Blink_Left",
                               "A07_Eye_Blink_Left", "Blink_L", "EyeBlink_L");
        _eyeBlinkR = FindShape("eyeBlinkRight", "Eye_Blink_R", "Eye_Blink_Right",
                               "A08_Eye_Blink_Right", "Blink_R", "EyeBlink_R");

        if (_eyeBlinkL < 0 || _eyeBlinkR < 0)
            Debug.LogWarning("[CC5FacialIdle] Eye blink blendshapes not found! Blinking disabled.");
    }

    // ─── Build micro-movement channels ──────────────────────────────────
    private void BuildMicroChannels()
    {
        // Each entry: (candidate names[], speedMultiplier, intensityMultiplier)
        var definitions = new (string[] names, float speed, float intensity)[]
        {
            // Brow inner raise — subtle "thinking" motion
            (new[] { "browInnerUp", "Brow_Raise_Inner_L", "A17_Brow_Raise_Inner_Left",
                     "Brow_Inner_Up" }, 0.7f, 0.6f),
            (new[] { "browInnerUp", "Brow_Raise_Inner_R", "A18_Brow_Raise_Inner_Right",
                     "Brow_Inner_Up_R" }, 0.65f, 0.6f),

            // Brow outer raise
            (new[] { "browOuterUpLeft", "Brow_Raise_Outer_L", "A19_Brow_Raise_Outer_Left",
                     "Brow_Outer_Up_L" }, 0.4f, 0.4f),
            (new[] { "browOuterUpRight", "Brow_Raise_Outer_R", "A20_Brow_Raise_Outer_Right",
                     "Brow_Outer_Up_R" }, 0.38f, 0.4f),

            // Brow drop / lowerer
            (new[] { "browDownLeft", "Brow_Drop_L", "A15_Brow_Drop_Left",
                     "Brow_Down_L" }, 0.35f, 0.35f),
            (new[] { "browDownRight", "Brow_Drop_R", "A16_Brow_Drop_Right",
                     "Brow_Down_R" }, 0.33f, 0.35f),

            // Eye squint — subtle squinting
            (new[] { "eyeSquintLeft", "Eye_Squint_L", "A11_Eye_Squint_Left",
                     "Eye_Squint_Left" }, 0.5f, 0.45f),
            (new[] { "eyeSquintRight", "Eye_Squint_R", "A12_Eye_Squint_Right",
                     "Eye_Squint_Right" }, 0.48f, 0.45f),

            // Eye wide (surprise micro-flash)
            (new[] { "eyeWideLeft", "Eye_Wide_L", "A09_Eye_Wide_Left",
                     "Eye_Wide_Left" }, 0.25f, 0.2f),
            (new[] { "eyeWideRight", "Eye_Wide_R", "A10_Eye_Wide_Right",
                     "Eye_Wide_Right" }, 0.24f, 0.2f),

            // Nose sneer
            (new[] { "noseSneerLeft", "Nose_Sneer_L", "A39_Nose_Sneer_Left",
                     "Nose_Sneer_Left" }, 0.3f, 0.25f),
            (new[] { "noseSneerRight", "Nose_Sneer_R", "A40_Nose_Sneer_Right",
                     "Nose_Sneer_Right" }, 0.28f, 0.25f),

            // Cheek puff — very subtle breathing-like motion
            (new[] { "cheekPuff", "Cheek_Puff_L", "A35_Cheek_Puff_Left",
                     "Cheek_Puff" }, 0.15f, 0.15f),
            (new[] { "cheekPuff", "Cheek_Puff_R", "A36_Cheek_Puff_Right" }, 0.14f, 0.15f),

            // Cheek squint
            (new[] { "cheekSquintLeft", "Cheek_Squint_L", "Cheek_Raise_L",
                     "A37_Cheek_Squint_Left" }, 0.2f, 0.2f),
            (new[] { "cheekSquintRight", "Cheek_Squint_R", "Cheek_Raise_R",
                     "A38_Cheek_Squint_Right" }, 0.19f, 0.2f),

            // Mouth press — very subtle lip tension
            (new[] { "mouthPressLeft", "Mouth_Press_L", "A55_Mouth_Press_Left",
                     "Mouth_Press_Left" }, 0.18f, 0.15f),
            (new[] { "mouthPressRight", "Mouth_Press_R", "A56_Mouth_Press_Right",
                     "Mouth_Press_Right" }, 0.17f, 0.15f),
        };

        float seed = UnityEngine.Random.Range(0f, 1000f);
        for (int i = 0; i < definitions.Length; i++)
        {
            var def = definitions[i];
            int idx = FindShape(def.names);
            if (idx < 0) continue;

            _microChannels.Add(new MicroChannel
            {
                index = idx,
                noiseOffsetA = seed + i * 73.7f,
                noiseOffsetB = seed + i * 137.3f + 500f,
                speedMul = def.speed,
                intensityMul = def.intensity
            });
        }
    }

    // ─── Blink Update ───────────────────────────────────────────────────
    private void ScheduleNextBlink()
    {
        _nextBlinkTime = UnityEngine.Random.Range(blinkIntervalMin, blinkIntervalMax);
        _blinkTimer = 0f;
        _blinkPhase = BlinkPhase.Idle;
    }

    private void UpdateBlink()
    {
        if (_eyeBlinkL < 0 || _eyeBlinkR < 0) return;

        float dt = Time.deltaTime;

        switch (_blinkPhase)
        {
            case BlinkPhase.Idle:
                _blinkTimer += dt;
                if (_blinkTimer >= _nextBlinkTime)
                {
                    _blinkPhase = BlinkPhase.Closing;
                    _blinkTimer = 0f;
                    _blinkValue = 0f;
                }
                break;

            case BlinkPhase.Closing:
                _blinkTimer += dt;
                _blinkValue = Mathf.Clamp01(_blinkTimer / blinkCloseSpeed);
                // Ease-in for snappy close
                _blinkValue = _blinkValue * _blinkValue;
                ApplyBlink(_blinkValue);
                if (_blinkTimer >= blinkCloseSpeed)
                {
                    _blinkPhase = BlinkPhase.Closed;
                    _blinkTimer = 0f;
                    _blinkValue = 1f;
                    ApplyBlink(1f);
                }
                break;

            case BlinkPhase.Closed:
                _blinkTimer += dt;
                if (_blinkTimer >= blinkClosedPause)
                {
                    _blinkPhase = BlinkPhase.Opening;
                    _blinkTimer = 0f;
                }
                break;

            case BlinkPhase.Opening:
                _blinkTimer += dt;
                float openT = Mathf.Clamp01(_blinkTimer / blinkOpenSpeed);
                // Ease-out for gentle open
                _blinkValue = 1f - (openT * (2f - openT));
                ApplyBlink(_blinkValue);
                if (_blinkTimer >= blinkOpenSpeed)
                {
                    _blinkValue = 0f;
                    ApplyBlink(0f);

                    // Double blink check
                    if (_doubleBlikPending)
                    {
                        _doubleBlikPending = false;
                        _blinkPhase = BlinkPhase.Closing;
                        _blinkTimer = 0f;
                    }
                    else if (UnityEngine.Random.value < doubleBlikProbability)
                    {
                        _doubleBlikPending = true;
                        // Short pause before second blink
                        _blinkPhase = BlinkPhase.Idle;
                        _nextBlinkTime = UnityEngine.Random.Range(0.1f, 0.25f);
                        _blinkTimer = 0f;
                    }
                    else
                    {
                        ScheduleNextBlink();
                    }
                }
                break;
        }
    }

    private void ApplyBlink(float t)
    {
        float w = t * blinkWeight;
        if (_eyeBlinkL >= 0)
            skinnedMeshRenderer.SetBlendShapeWeight(_eyeBlinkL, w);
        if (_eyeBlinkR >= 0)
            skinnedMeshRenderer.SetBlendShapeWeight(_eyeBlinkR, w);
    }

    // ─── Micro-Movements Update ─────────────────────────────────────────
    private void UpdateMicroMovements()
    {
        if (_microChannels.Count == 0) return;

        float time = Time.time * microSpeed;

        for (int i = 0; i < _microChannels.Count; i++)
        {
            var ch = _microChannels[i];
            // Two Perlin layers for organic, non-repeating feel
            float n1 = Mathf.PerlinNoise(time * ch.speedMul + ch.noiseOffsetA, ch.noiseOffsetA);
            float n2 = Mathf.PerlinNoise(time * ch.speedMul * 0.37f + ch.noiseOffsetB, ch.noiseOffsetB);

            // Combine layers: primary + slower secondary drift
            float combined = (n1 * 0.7f + n2 * 0.3f);

            // Remap from [0..1] to [0..1] with bias toward low values
            // (most of the time face is relaxed, occasional subtle movements)
            combined = Mathf.Max(0f, combined - 0.35f) / 0.65f;
            combined = combined * combined; // bias toward zero

            float weight = combined * microIntensity * ch.intensityMul;
            weight = Mathf.Clamp(weight, 0f, 100f);

            skinnedMeshRenderer.SetBlendShapeWeight(ch.index, weight);
        }
    }

    // ─── Debug ──────────────────────────────────────────────────────────
    private void LogDetectedShapes()
    {
        Debug.Log($"[CC5FacialIdle] Eye_Blink_L index: {_eyeBlinkL}");
        Debug.Log($"[CC5FacialIdle] Eye_Blink_R index: {_eyeBlinkR}");
        foreach (var ch in _microChannels)
        {
            string name = skinnedMeshRenderer.sharedMesh.GetBlendShapeName(ch.index);
            Debug.Log($"[CC5FacialIdle] Micro channel: [{ch.index}] {name} " +
                      $"(speed: {ch.speedMul:F2}, intensity: {ch.intensityMul:F2})");
        }
    }
}
