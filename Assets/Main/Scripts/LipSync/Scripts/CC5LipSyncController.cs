/************************************************************************************
Content     :   Maps Oculus LipSync visemes to Character Creator 5 blendshapes.
                Supports ARKit, ExPlus, and Traditional CC blendshape profiles
                with auto-detection. Handles prefixed blendshape names
                (e.g. "CC_Game_Body.jawOpen").
                Attach to the same GameObject as OVRLipSyncContext + AudioSource.
************************************************************************************/
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class CC5LipSyncController : MonoBehaviour
{
    // ─── Inspector ──────────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("SkinnedMeshRenderer of the CC5 head/face mesh that contains blendshapes.")]
    public SkinnedMeshRenderer skinnedMeshRenderer;

    [Tooltip("SkinnedMeshRenderer of the CC5 tongue mesh (optional, e.g. CC_Game_Tongue).")]
    public SkinnedMeshRenderer tongueSkinnedMeshRenderer;

    [Header("Lip Sync Settings")]
    [Range(1, 100)]
    [Tooltip("Smoothing applied to the OVR lip sync context (1 = raw, 100 = very smooth).")]
    public int smoothing = 70;

    [Range(0f, 200f)]
    [Tooltip("Global weight multiplier for all viseme blendshapes (100 = default).")]
    public float weightMultiplier = 100f;

    [Range(0f, 1f)]
    [Tooltip("Laughter probability threshold.")]
    public float laughterThreshold = 0.5f;

    [Range(0f, 3f)]
    [Tooltip("Laughter blendshape multiplier.")]
    public float laughterMultiplier = 1.5f;

    [Header("Jaw Bone (Rig)")]
    [Tooltip("Jaw bone transform. Auto-detected if left empty (searches for CC_Base_JawRoot or uses Animator.Jaw).")]
    public Transform jawBone;

    [Range(0f, 45f)]
    [Tooltip("Maximum jaw rotation angle in degrees when fully open.")]
    public float jawMaxAngle = 25f;

    [Range(1f, 30f)]
    [Tooltip("Jaw rotation smoothing speed.")]
    public float jawSmoothSpeed = 15f;

    [Tooltip("Local rotation axis used to open the jaw. CC5 characters typically use NegZ.")]
    public JawAxis jawRotationAxis = JawAxis.NegZ;

    [Header("Blendshape Profile")]
    [Tooltip("Auto-detect profile from blendshape names on the mesh.")]
    public bool autoDetectProfile = true;

    public BlendShapeProfile profile = BlendShapeProfile.ARKit;

    [Header("Debug")]
    [Tooltip("Log all blendshape names and indices to the console on Start.")]
    public bool logBlendShapes = false;

    // ─── Types ──────────────────────────────────────────────────────────
    public enum BlendShapeProfile
    {
        ARKit,
        ExPlus,
        Traditional
    }

    public enum JawAxis
    {
        X,
        Y,
        Z,
        NegX,
        NegY,
        NegZ
    }

    [Serializable]
    public struct BlendShapeMapping
    {
        public int index;
        public float weight; // 0..1
    }

    // ─── Private ────────────────────────────────────────────────────────
    private OVRLipSyncContextBase _lipsyncContext;
    private Dictionary<string, int> _nameToIndex;
    private int _tongueIndexOffset;

    // 15 visemes
    private List<BlendShapeMapping>[] _visemeMappings;

    // laughter  →  list of (blendShapeIndex, weight)
    private List<BlendShapeMapping> _laughterMappings;

    // Smoothed weights per blendshape index to avoid jitter
    private Dictionary<int, float> _currentWeights = new Dictionary<int, float>();

    // Jaw bone rotation
    private Quaternion _jawRestRotation;
    private Vector3 _jawRotationAxis = Vector3.back; // local -Z axis (CC5 default)
    private float _currentJawAngle;

    // How much each viseme opens the jaw (0 = closed, 1 = fully open)
    private float[] _visemeJawOpen;

    // ─── Unity Lifecycle ────────────────────────────────────────────────
    void Start()
    {
        if (skinnedMeshRenderer == null)
        {
            Debug.LogError("[CC5LipSync] SkinnedMeshRenderer is not assigned!");
            enabled = false;
            return;
        }

        _lipsyncContext = GetComponent<OVRLipSyncContextBase>();
        if (_lipsyncContext == null)
        {
            Debug.LogError("[CC5LipSync] No OVRLipSyncContext / OVRLipSyncContextBase on this GameObject!");
            enabled = false;
            return;
        }

        _lipsyncContext.Smoothing = smoothing;

        BuildNameIndex();

        if (logBlendShapes)
            LogAllBlendShapes();

        if (autoDetectProfile)
            DetectProfile();

        BuildMappings();
        BuildJawOpenTable();
        FindJawBone();
        _jawRotationAxis = JawAxisToVector(jawRotationAxis);

        LogMappingSummary();

        string meshInfo = $"{skinnedMeshRenderer.sharedMesh.blendShapeCount}";
        if (tongueSkinnedMeshRenderer != null)
            meshInfo += $" + {tongueSkinnedMeshRenderer.sharedMesh.blendShapeCount} tongue";
        Debug.Log($"[CC5LipSync] Initialized with profile: {profile}, " +
                  $"blendshapes: {meshInfo}, " +
                  $"jaw bone: {(jawBone != null ? jawBone.name : "NOT FOUND")}");
    }

    void Update()
    {
        if (_lipsyncContext == null || skinnedMeshRenderer == null)
            return;

        if (smoothing != _lipsyncContext.Smoothing)
            _lipsyncContext.Smoothing = smoothing;

        OVRLipSync.Frame frame = _lipsyncContext.GetCurrentPhonemeFrame();
        if (frame == null)
            return;

        ApplyVisemes(frame);
        ApplyLaughter(frame);
        ApplyJawBone(frame);
    }

    // ─── Build blendshape name → index dictionary ───────────────────────
    private void BuildNameIndex()
    {
        _nameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        RegisterMeshBlendShapes(skinnedMeshRenderer, 0);
        _tongueIndexOffset = skinnedMeshRenderer.sharedMesh.blendShapeCount;
        if (tongueSkinnedMeshRenderer != null)
            RegisterMeshBlendShapes(tongueSkinnedMeshRenderer, _tongueIndexOffset);
    }

    private void RegisterMeshBlendShapes(SkinnedMeshRenderer smr, int offset)
    {
        Mesh mesh = smr.sharedMesh;
        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            int globalIdx = offset + i;
            string fullName = mesh.GetBlendShapeName(i);
            // Main mesh (offset 0) always sets; secondary meshes only if not already present
            if (offset == 0 || !_nameToIndex.ContainsKey(fullName))
                _nameToIndex[fullName] = globalIdx;

            // Also store the part after the last '.' so that prefixed names
            // like "CC_Game_Body.jawOpen" can be found by "jawOpen"
            int dotIdx = fullName.LastIndexOf('.');
            if (dotIdx >= 0 && dotIdx < fullName.Length - 1)
            {
                string shortName = fullName.Substring(dotIdx + 1);
                if (!_nameToIndex.ContainsKey(shortName))
                    _nameToIndex[shortName] = globalIdx;
            }
        }
    }

    // ─── Auto-detect CC5 profile ────────────────────────────────────────
    private void DetectProfile()
    {
        // ARKit: jawOpen / JawOpen
        if (_nameToIndex.ContainsKey("jawOpen") || _nameToIndex.ContainsKey("JawOpen"))
        {
            profile = BlendShapeProfile.ARKit;
            Debug.Log("[CC5LipSync] Auto-detected ARKit blendshape profile.");
            return;
        }

        // ExPlus: A25_Jaw_Open
        if (_nameToIndex.ContainsKey("A25_Jaw_Open"))
        {
            profile = BlendShapeProfile.ExPlus;
            Debug.Log("[CC5LipSync] Auto-detected ExPlus blendshape profile.");
            return;
        }

        // Traditional CC3/CC4/CC5: Jaw_Open, Mouth_Open, V_Open, etc.
        if (_nameToIndex.ContainsKey("Jaw_Open") || _nameToIndex.ContainsKey("Mouth_Open")
            || _nameToIndex.ContainsKey("V_Open"))
        {
            profile = BlendShapeProfile.Traditional;
            Debug.Log("[CC5LipSync] Auto-detected Traditional CC blendshape profile.");
            return;
        }

        Debug.LogWarning("[CC5LipSync] Could not auto-detect profile. " +
                         "Falling back to ARKit. Listing all blendshapes below:");
        LogAllBlendShapes();
    }

    // ─── Build viseme → blendshape mappings ─────────────────────────────
    private void BuildMappings()
    {
        _visemeMappings = new List<BlendShapeMapping>[OVRLipSync.VisemeCount];
        for (int i = 0; i < _visemeMappings.Length; i++)
            _visemeMappings[i] = new List<BlendShapeMapping>();

        _laughterMappings = new List<BlendShapeMapping>();

        switch (profile)
        {
            case BlendShapeProfile.ARKit:       BuildARKitMappings();       break;
            case BlendShapeProfile.ExPlus:       BuildExPlusMappings();      break;
            case BlendShapeProfile.Traditional:  BuildTraditionalMappings(); break;
        }
    }

    // ──────────────────────── ARKit profile ─────────────────────────────
    // CC5 ARKit blendshape names (Apple ARKit standard, 52 shapes).
    // OVR Visemes: sil(0) PP(1) FF(2) TH(3) DD(4) kk(5) CH(6) SS(7)
    //              nn(8) RR(9) aa(10) E(11) ih(12) oh(13) ou(14)
    private void BuildARKitMappings()
    {
        // sil — silence, close mouth
        Map(0 /* sil */);

        // PP — bilabial plosive (P, B, M): lips pressed together
        Map(1 /* PP */,  ("mouthClose", 0.7f), ("mouthPucker", 0.3f), ("jawOpen", 0.05f));

        // FF — labiodental (F, V): lower lip tucked under upper teeth
        Map(2 /* FF */,  ("mouthFunnel", 0.4f), ("mouthLowerDownLeft", 0.3f),
                         ("mouthLowerDownRight", 0.3f), ("jawOpen", 0.1f));

        // TH — interdental (TH): tongue between teeth
        Map(3 /* TH */,  ("mouthFunnel", 0.2f), ("jawOpen", 0.15f),
                         ("tongueOut", 0.6f));

        // DD — alveolar (D, T, N): tongue at alveolar ridge
        Map(4 /* DD */,  ("jawOpen", 0.2f), ("mouthClose", 0.1f),
                         ("mouthUpperUpLeft", 0.15f), ("mouthUpperUpRight", 0.15f));

        // kk — velar (K, G): back tongue raised
        Map(5 /* kk */,  ("jawOpen", 0.25f), ("mouthFunnel", 0.15f));

        // CH — postalveolar (CH, J, SH): lips slightly rounded
        Map(6 /* CH */,  ("mouthFunnel", 0.5f), ("mouthPucker", 0.3f), ("jawOpen", 0.15f));

        // SS — alveolar fricative (S, Z): narrow opening
        Map(7 /* SS */,  ("mouthSmileLeft", 0.2f), ("mouthSmileRight", 0.2f),
                         ("jawOpen", 0.08f), ("mouthClose", 0.3f));

        // nn — nasal (N, L): mouth slightly open
        Map(8 /* nn */,  ("jawOpen", 0.15f), ("mouthClose", 0.2f),
                         ("mouthPressLeft", 0.1f), ("mouthPressRight", 0.1f));

        // RR — R sound: lips slightly rounded
        Map(9 /* RR */,  ("mouthFunnel", 0.35f), ("mouthPucker", 0.2f), ("jawOpen", 0.2f));

        // aa — open vowel (A, AH): wide open mouth
        Map(10 /* aa */, ("jawOpen", 0.7f), ("mouthLowerDownLeft", 0.3f),
                         ("mouthLowerDownRight", 0.3f), ("mouthUpperUpLeft", 0.15f),
                         ("mouthUpperUpRight", 0.15f));

        // E — mid vowel (E, EH): mouth open with spread lips
        Map(11 /* E */,  ("jawOpen", 0.35f), ("mouthSmileLeft", 0.3f),
                         ("mouthSmileRight", 0.3f), ("mouthLowerDownLeft", 0.15f),
                         ("mouthLowerDownRight", 0.15f));

        // ih — close-mid vowel (I, IH): small opening, spread lips
        Map(12 /* ih */, ("jawOpen", 0.2f), ("mouthSmileLeft", 0.4f),
                         ("mouthSmileRight", 0.4f));

        // oh — rounded vowel (O, AW): rounded open mouth
        Map(13 /* oh */, ("jawOpen", 0.45f), ("mouthFunnel", 0.5f),
                         ("mouthPucker", 0.2f));

        // ou — close rounded (U, OO): tight rounded lips
        Map(14 /* ou */, ("mouthFunnel", 0.6f), ("mouthPucker", 0.5f),
                         ("jawOpen", 0.15f));

        // Laughter
        MapLaughter(("jawOpen", 0.6f), ("mouthSmileLeft", 0.7f),
                    ("mouthSmileRight", 0.7f), ("mouthUpperUpLeft", 0.2f),
                    ("mouthUpperUpRight", 0.2f));
    }

    // ──────────────────────── ExPlus profile ────────────────────────────
    // CC5 ExPlus blendshape names (Reallusion naming convention).
    private void BuildExPlusMappings()
    {
        Map(0 /* sil */);

        Map(1 /* PP */,  ("Mouth_Close", 0.7f), ("Mouth_Pucker", 0.3f),
                         ("A25_Jaw_Open", 0.05f));

        Map(2 /* FF */,  ("Mouth_Funnel", 0.4f), ("Mouth_Lower_Down_Left", 0.3f),
                         ("Mouth_Lower_Down_Right", 0.3f), ("A25_Jaw_Open", 0.1f));

        Map(3 /* TH */,  ("Mouth_Funnel", 0.2f), ("A25_Jaw_Open", 0.15f),
                         ("Tongue_Out", 0.5f));

        Map(4 /* DD */,  ("A25_Jaw_Open", 0.2f), ("Mouth_Close", 0.1f),
                         ("Mouth_Upper_Up_Left", 0.15f), ("Mouth_Upper_Up_Right", 0.15f));

        Map(5 /* kk */,  ("A25_Jaw_Open", 0.25f), ("Mouth_Funnel", 0.15f));

        Map(6 /* CH */,  ("Mouth_Funnel", 0.5f), ("Mouth_Pucker", 0.3f),
                         ("A25_Jaw_Open", 0.15f));

        Map(7 /* SS */,  ("Mouth_Smile_Left", 0.2f), ("Mouth_Smile_Right", 0.2f),
                         ("A25_Jaw_Open", 0.08f), ("Mouth_Close", 0.3f));

        Map(8 /* nn */,  ("A25_Jaw_Open", 0.15f), ("Mouth_Close", 0.2f),
                         ("Mouth_Press_Left", 0.1f), ("Mouth_Press_Right", 0.1f));

        Map(9 /* RR */,  ("Mouth_Funnel", 0.35f), ("Mouth_Pucker", 0.2f),
                         ("A25_Jaw_Open", 0.2f));

        Map(10 /* aa */, ("A25_Jaw_Open", 0.7f), ("Mouth_Lower_Down_Left", 0.3f),
                         ("Mouth_Lower_Down_Right", 0.3f), ("Mouth_Upper_Up_Left", 0.15f),
                         ("Mouth_Upper_Up_Right", 0.15f));

        Map(11 /* E */,  ("A25_Jaw_Open", 0.35f), ("Mouth_Smile_Left", 0.3f),
                         ("Mouth_Smile_Right", 0.3f));

        Map(12 /* ih */, ("A25_Jaw_Open", 0.2f), ("Mouth_Smile_Left", 0.4f),
                         ("Mouth_Smile_Right", 0.4f));

        Map(13 /* oh */, ("A25_Jaw_Open", 0.45f), ("Mouth_Funnel", 0.5f),
                         ("Mouth_Pucker", 0.2f));

        Map(14 /* ou */, ("Mouth_Funnel", 0.6f), ("Mouth_Pucker", 0.5f),
                         ("A25_Jaw_Open", 0.15f));

        MapLaughter(("A25_Jaw_Open", 0.6f), ("Mouth_Smile_Left", 0.7f),
                    ("Mouth_Smile_Right", 0.7f));
    }

    // ──────────────────────── Traditional CC profile ────────────────────
    // CC3/CC4/CC5 traditional blendshape names (Reallusion game export).
    // Prefers V_* viseme blendshapes when available (direct 1:1 mapping),
    // falls back to compositing from Jaw_Open / Mouth_* shapes.
    private void BuildTraditionalMappings()
    {
        bool hasVisemes = HasAny("V_Open", "V_Explosive", "V_Tight_O");

        if (hasVisemes)
        {
            Debug.Log("[CC5LipSync] Found CC V_* viseme blendshapes — using direct mapping.");
            BuildTraditionalVisemeMappings();
        }
        else
        {
            Debug.Log("[CC5LipSync] No V_* viseme blendshapes — compositing from Jaw/Mouth shapes.");
            BuildTraditionalCompositeMappings();
        }
    }

    private bool HasAny(params string[] names)
    {
        foreach (string n in names)
            if (FindBlendShapeSilent(n) >= 0) return true;
        return false;
    }

    // Direct mapping using CC V_* viseme blendshapes
    // CC viseme shapes: V_Open, V_Tight_O, V_Tight, V_Wide,
    //   V_Explosive, V_Dental_Lip, V_Lip_Open, V_Tongue_Up, V_Tongue_Raise
    private void BuildTraditionalVisemeMappings()
    {
        // sil(0) — silence
        Map(0 /* sil */);

        // PP(1) — bilabial (P, B, M): lips pressed
        Map(1 /* PP */,  ("V_Explosive", 1.0f));

        // FF(2) — labiodental (F, V): lower lip under upper teeth
        Map(2 /* FF */,  ("V_Dental_Lip", 1.0f));

        // TH(3) — interdental: tongue out
        Map(3 /* TH */,  ("V_Open", 0.3f), ("V_Lip_Open", 0.3f),
                         ("Tongue_Out", 0.6f), ("Jaw_Open", 0.15f));

        // DD(4) — alveolar (D, T, N): tongue touches ridge
        Map(4 /* DD */,  ("V_Lip_Open", 0.4f), ("V_Tight", 0.4f),
                         ("Tongue_Up", 0.3f), ("Jaw_Open", 0.15f));

        // kk(5) — velar (K, G): back tongue
        Map(5 /* kk */,  ("V_Tight", 0.4f), ("Jaw_Open", 0.3f));

        // CH(6) — postalveolar (CH, J, SH)
        Map(6 /* CH */,  ("V_Tight_O", 0.7f), ("V_Tight", 0.3f));

        // SS(7) — alveolar fricative (S, Z)
        Map(7 /* SS */,  ("V_Wide", 0.5f), ("V_Tight", 0.5f));

        // nn(8) — nasal (N, L)
        Map(8 /* nn */,  ("V_Lip_Open", 0.5f), ("V_Tight", 0.3f),
                         ("Jaw_Open", 0.1f));

        // RR(9) — R: slightly rounded
        Map(9 /* RR */,  ("V_Tight_O", 0.6f), ("Jaw_Open", 0.2f));

        // aa(10) — open vowel (A, AH)
        Map(10 /* aa */, ("V_Open", 1.0f));

        // E(11) — mid vowel (E, EH)
        Map(11 /* E */,  ("V_Wide", 0.8f), ("V_Open", 0.3f));

        // ih(12) — close-mid (I, IH)
        Map(12 /* ih */, ("V_Wide", 1.0f));

        // oh(13) — rounded (O, AW)
        Map(13 /* oh */, ("V_Tight_O", 1.0f));

        // ou(14) — close rounded (U, OO)
        Map(14 /* ou */, ("V_Tight_O", 0.7f), ("V_Tight", 0.3f));

        MapLaughter(("V_Open", 0.5f), ("Mouth_Smile_L", 0.7f),
                    ("Mouth_Smile_R", 0.7f), ("Jaw_Open", 0.4f));
    }

    // Composite mapping from individual Jaw/Mouth shapes (no V_* available)
    private void BuildTraditionalCompositeMappings()
    {
        Map(0 /* sil */);

        Map(1 /* PP */,  ("Mouth_Press_L", 0.5f), ("Mouth_Press_R", 0.5f),
                         ("Mouth_Pucker", 0.3f), ("Jaw_Open", 0.05f));

        Map(2 /* FF */,  ("Mouth_Shrug_Lower", 0.5f), ("Jaw_Open", 0.15f),
                         ("Mouth_Frown_L", 0.15f), ("Mouth_Frown_R", 0.15f));

        Map(3 /* TH */,  ("Jaw_Open", 0.15f), ("Mouth_Shrug_Lower", 0.2f),
                         ("Tongue_Out", 0.5f));

        Map(4 /* DD */,  ("Jaw_Open", 0.2f), ("Mouth_Shrug_Upper", 0.2f),
                         ("Mouth_Press_L", 0.1f), ("Mouth_Press_R", 0.1f));

        Map(5 /* kk */,  ("Jaw_Open", 0.3f), ("Mouth_Shrug_Upper", 0.15f));

        Map(6 /* CH */,  ("Mouth_Pucker", 0.4f), ("Jaw_Open", 0.15f),
                         ("Mouth_Funnel", 0.3f));

        Map(7 /* SS */,  ("Mouth_Smile_L", 0.2f), ("Mouth_Smile_R", 0.2f),
                         ("Jaw_Open", 0.08f), ("Mouth_Close", 0.3f));

        Map(8 /* nn */,  ("Jaw_Open", 0.15f), ("Mouth_Close", 0.2f),
                         ("Mouth_Shrug_Lower", 0.15f));

        Map(9 /* RR */,  ("Mouth_Pucker", 0.3f), ("Jaw_Open", 0.2f),
                         ("Mouth_Funnel", 0.25f));

        Map(10 /* aa */, ("Jaw_Open", 0.7f),
                         ("Mouth_Shrug_Lower", 0.2f), ("Mouth_Shrug_Upper", 0.15f));

        Map(11 /* E */,  ("Jaw_Open", 0.35f), ("Mouth_Smile_L", 0.3f),
                         ("Mouth_Smile_R", 0.3f));

        Map(12 /* ih */, ("Jaw_Open", 0.2f), ("Mouth_Smile_L", 0.4f),
                         ("Mouth_Smile_R", 0.4f));

        Map(13 /* oh */, ("Jaw_Open", 0.45f), ("Mouth_Pucker", 0.3f),
                         ("Mouth_Funnel", 0.4f));

        Map(14 /* ou */, ("Mouth_Pucker", 0.5f), ("Mouth_Funnel", 0.5f),
                         ("Jaw_Open", 0.15f));

        MapLaughter(("Jaw_Open", 0.6f), ("Mouth_Smile_L", 0.7f),
                    ("Mouth_Smile_R", 0.7f));
    }

    // ─── Mapping helpers ────────────────────────────────────────────────
    private void Map(int visemeIndex, params (string name, float weight)[] entries)
    {
        foreach (var (name, weight) in entries)
        {
            int idx = FindBlendShape(name);
            if (idx >= 0)
                _visemeMappings[visemeIndex].Add(new BlendShapeMapping { index = idx, weight = weight });
        }
    }

    private void MapLaughter(params (string name, float weight)[] entries)
    {
        foreach (var (name, weight) in entries)
        {
            int idx = FindBlendShape(name);
            if (idx >= 0)
                _laughterMappings.Add(new BlendShapeMapping { index = idx, weight = weight });
        }
    }

    private int FindBlendShape(string name)
    {
        // 1. Exact match (case-insensitive, includes stripped short names)
        if (_nameToIndex.TryGetValue(name, out int idx))
            return idx;

        // 2. Partial match: blendshape name contains the search term
        foreach (var kvp in _nameToIndex)
        {
            if (kvp.Key.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                return kvp.Value;
        }

        // 3. Fuzzy: normalize underscores/case and compare
        string normalized = name.Replace("_", "").ToLowerInvariant();
        foreach (var kvp in _nameToIndex)
        {
            string keyNorm = kvp.Key.Replace("_", "").ToLowerInvariant();
            // Strip prefix before dot for comparison
            int dotIdx = keyNorm.LastIndexOf('.');
            if (dotIdx >= 0) keyNorm = keyNorm.Substring(dotIdx + 1);

            if (keyNorm == normalized)
                return kvp.Value;
        }

        if (!_missingLogged.Contains(name))
        {
            _missingLogged.Add(name);
            Debug.LogWarning($"[CC5LipSync] Blendshape '{name}' not found. Skipping.");
        }
        return -1;
    }

    private readonly HashSet<string> _missingLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // ─── Weight helper (resolves global index → correct renderer) ──────
    private void SetWeight(int globalIdx, float weight)
    {
        if (globalIdx >= _tongueIndexOffset && tongueSkinnedMeshRenderer != null)
            tongueSkinnedMeshRenderer.SetBlendShapeWeight(globalIdx - _tongueIndexOffset, weight);
        else
            skinnedMeshRenderer.SetBlendShapeWeight(globalIdx, weight);
    }

    // ─── Apply visemes ──────────────────────────────────────────────────
    private void ApplyVisemes(OVRLipSync.Frame frame)
    {
        // Collect target weights from all active visemes
        Dictionary<int, float> targetWeights = new Dictionary<int, float>();

        for (int v = 0; v < OVRLipSync.VisemeCount && v < frame.Visemes.Length; v++)
        {
            float visemeScore = frame.Visemes[v];
            if (visemeScore < 0.01f)
                continue;

            foreach (var mapping in _visemeMappings[v])
            {
                float contribution = visemeScore * mapping.weight * weightMultiplier;
                if (targetWeights.ContainsKey(mapping.index))
                    targetWeights[mapping.index] += contribution;
                else
                    targetWeights[mapping.index] = contribution;
            }
        }

        // Collect all known blendshape indices to reset unused ones
        HashSet<int> allIndices = new HashSet<int>();
        for (int v = 0; v < _visemeMappings.Length; v++)
            foreach (var m in _visemeMappings[v])
                allIndices.Add(m.index);

        // Apply with smoothing
        float dt = Time.deltaTime * 15f;
        foreach (int idx in allIndices)
        {
            float target = 0f;
            if (targetWeights.ContainsKey(idx))
                target = Mathf.Clamp(targetWeights[idx], 0f, 100f);

            if (!_currentWeights.ContainsKey(idx))
                _currentWeights[idx] = 0f;

            _currentWeights[idx] = Mathf.Lerp(_currentWeights[idx], target, dt);
            SetWeight(idx, _currentWeights[idx]);
        }
    }

    // ─── Apply laughter
    private void ApplyLaughter(OVRLipSync.Frame frame)
    {
        if (_laughterMappings.Count == 0)
            return;

        float score = frame.laughterScore;
        score = score < laughterThreshold ? 0f : score - laughterThreshold;
        score = Mathf.Min(score * laughterMultiplier, 1f);
        if (laughterThreshold < 1f)
            score *= 1f / (1f - laughterThreshold);

        float dt = Time.deltaTime * 10f;

        foreach (var mapping in _laughterMappings)
        {
            float target = Mathf.Clamp(score * mapping.weight * weightMultiplier, 0f, 100f);

            if (!_currentWeights.ContainsKey(mapping.index))
                _currentWeights[mapping.index] = 0f;

            _currentWeights[mapping.index] = Mathf.Lerp(_currentWeights[mapping.index], target, dt);
            SetWeight(mapping.index, _currentWeights[mapping.index]);
        }
    }

    private int FindBlendShapeSilent(string name)
    {
        if (_nameToIndex.TryGetValue(name, out int idx))
            return idx;
        foreach (var kvp in _nameToIndex)
        {
            if (kvp.Key.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                return kvp.Value;
        }
        return -1;
    }

    // ─── Jaw Bone ───────────────────────────────────────────────────────
    private void FindJawBone()
    {
        if (jawBone != null)
        {
            _jawRestRotation = jawBone.localRotation;
            return;
        }

        // 1. Try Animator humanoid jaw bone
        Animator animator = skinnedMeshRenderer.GetComponentInParent<Animator>();
        if (animator != null && animator.isHuman)
        {
            Transform bone = animator.GetBoneTransform(HumanBodyBones.Jaw);
            if (bone != null)
            {
                jawBone = bone;
                _jawRestRotation = jawBone.localRotation;
                Debug.Log($"[CC5LipSync] Found jaw bone via Animator: {jawBone.name}");
                return;
            }
        }

        // 2. Search hierarchy for CC_Base_JawRoot
        string[] jawNames = { "CC_Base_JawRoot", "JawRoot", "Jaw", "jaw" };
        Transform root = skinnedMeshRenderer.transform.root;
        foreach (string boneName in jawNames)
        {
            Transform found = FindChildRecursive(root, boneName);
            if (found != null)
            {
                jawBone = found;
                _jawRestRotation = jawBone.localRotation;
                Debug.Log($"[CC5LipSync] Found jaw bone by name: {jawBone.name}");
                return;
            }
        }

        Debug.LogWarning("[CC5LipSync] Jaw bone not found! Assign it manually in Inspector. " +
                         "Mouth will not open without jaw bone.");
    }

    private static Transform FindChildRecursive(Transform parent, string name)
    {
        if (parent.name.Equals(name, StringComparison.OrdinalIgnoreCase))
            return parent;
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform result = FindChildRecursive(parent.GetChild(i), name);
            if (result != null) return result;
        }
        return null;
    }

    // Per-viseme jaw opening amount (0..1)
    //  sil  PP   FF   TH   DD   kk   CH   SS   nn   RR   aa   E    ih   oh   ou
    private void BuildJawOpenTable()
    {
        _visemeJawOpen = new float[OVRLipSync.VisemeCount];
        _visemeJawOpen[0]  = 0.0f;  // sil
        _visemeJawOpen[1]  = 0.0f;  // PP  — lips closed
        _visemeJawOpen[2]  = 0.1f;  // FF  — slight
        _visemeJawOpen[3]  = 0.15f; // TH  — slight
        _visemeJawOpen[4]  = 0.2f;  // DD
        _visemeJawOpen[5]  = 0.3f;  // kk
        _visemeJawOpen[6]  = 0.15f; // CH
        _visemeJawOpen[7]  = 0.05f; // SS  — barely open
        _visemeJawOpen[8]  = 0.1f;  // nn
        _visemeJawOpen[9]  = 0.2f;  // RR
        _visemeJawOpen[10] = 1.0f;  // aa  — fully open
        _visemeJawOpen[11] = 0.5f;  // E
        _visemeJawOpen[12] = 0.25f; // ih
        _visemeJawOpen[13] = 0.6f;  // oh
        _visemeJawOpen[14] = 0.2f;  // ou
    }

    private static Vector3 JawAxisToVector(JawAxis axis)
    {
        switch (axis)
        {
            case JawAxis.X:    return Vector3.right;
            case JawAxis.Y:    return Vector3.up;
            case JawAxis.Z:    return Vector3.forward;
            case JawAxis.NegX: return Vector3.left;
            case JawAxis.NegY: return Vector3.down;
            case JawAxis.NegZ: return Vector3.back;
            default:           return Vector3.forward;
        }
    }

    private void ApplyJawBone(OVRLipSync.Frame frame)
    {
        if (jawBone == null || frame == null)
            return;

        // Calculate target jaw open from all active visemes
        float targetOpen = 0f;
        for (int v = 0; v < OVRLipSync.VisemeCount && v < frame.Visemes.Length; v++)
        {
            targetOpen += frame.Visemes[v] * _visemeJawOpen[v];
        }

        // Add laughter contribution
        float laughScore = frame.laughterScore;
        if (laughScore > laughterThreshold)
        {
            float laugh = (laughScore - laughterThreshold) * laughterMultiplier;
            targetOpen = Mathf.Max(targetOpen, Mathf.Min(laugh, 1f) * 0.7f);
        }

        targetOpen = Mathf.Clamp01(targetOpen);

        // Smooth
        _currentJawAngle = Mathf.Lerp(_currentJawAngle, targetOpen * jawMaxAngle,
                                      Time.deltaTime * jawSmoothSpeed);

        // Apply rotation around chosen local axis (opening downward)
        jawBone.localRotation = _jawRestRotation * Quaternion.AngleAxis(_currentJawAngle, _jawRotationAxis);
    }

    // ─── Debug ──────────────────────────────────────────────────────────
    private void LogMappingSummary()
    {
        int mapped = 0;
        for (int v = 0; v < _visemeMappings.Length; v++)
        {
            int count = _visemeMappings[v].Count;
            if (count > 0) mapped++;
            string visemeName = v < OVRLipSync.VisemeCount
                ? ((OVRLipSync.Viseme)v).ToString() : v.ToString();
            Debug.Log($"[CC5LipSync]   Viseme {visemeName}: {count} blendshape(s) mapped");
        }
        Debug.Log($"[CC5LipSync] Mapped {mapped}/{_visemeMappings.Length} visemes, " +
                  $"{_laughterMappings.Count} laughter blendshape(s)");

        LogRelevantBlendShapes(skinnedMeshRenderer);
        if (tongueSkinnedMeshRenderer != null)
            LogRelevantBlendShapes(tongueSkinnedMeshRenderer);
    }

    private void LogRelevantBlendShapes(SkinnedMeshRenderer smr)
    {
        Mesh mesh = smr.sharedMesh;
        Debug.Log($"[CC5LipSync] Relevant blendshapes on '{mesh.name}':");
        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            string n = mesh.GetBlendShapeName(i);
            if (n.IndexOf("Mouth", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Jaw", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Lip", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("V_", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Tongue", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Debug.Log($"  [{i}] {n}");
            }
        }
    }

    private void LogAllBlendShapes()
    {
        LogAllBlendShapesFor(skinnedMeshRenderer);
        if (tongueSkinnedMeshRenderer != null)
            LogAllBlendShapesFor(tongueSkinnedMeshRenderer);
    }

    private void LogAllBlendShapesFor(SkinnedMeshRenderer smr)
    {
        Mesh mesh = smr.sharedMesh;
        Debug.Log($"[CC5LipSync] === BlendShapes on '{mesh.name}' ({mesh.blendShapeCount} total) ===");
        for (int i = 0; i < mesh.blendShapeCount; i++)
            Debug.Log($"  [{i}] {mesh.GetBlendShapeName(i)}");
    }
}
