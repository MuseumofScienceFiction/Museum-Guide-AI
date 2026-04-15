using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Debug = UnityEngine.Debug;

/// <summary>
/// Gesture types recognized from pose landmarks.
/// </summary>
public enum PoseGesture
{
    Wave,
    HandsUp,
    TPose,
    Squat,
    HandRaiseLeft,
    HandRaiseRight,
    LeanLeft,
    LeanRight,
}

/// <summary>
/// Analyzes pose landmarks from <see cref="PoseDetector"/> and fires UnityEvents
/// when behavioral gestures are recognized.
///
/// Attach to the same GameObject as PoseDetector (or assign the reference manually).
/// Connect responses via the Inspector events or subscribe from code.
/// </summary>
public class GestureDetector : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private PoseDetector poseDetector;

    [Header("General")]
    [Tooltip("Minimum landmark visibility to consider it reliable")]
    [SerializeField] private float minVisibility = 0.5f;
    [Tooltip("Seconds before the same gesture can fire again")]
    [SerializeField] private float cooldownSeconds = 1.5f;

    [Header("Wave Detection")]
    [Tooltip("Horizontal direction changes needed to count as a wave")]
    [SerializeField] private int waveMinOscillations = 3;
    [Tooltip("Time window (seconds) in which oscillations are accumulated")]
    [SerializeField] private float waveTimeWindow = 2f;
    [Tooltip("Min horizontal movement (normalized) for a direction change")]
    [SerializeField] private float waveMinAmplitude = 0.03f;

    [Header("T-Pose Detection")]
    [Tooltip("Max deviation (degrees) of the arm from horizontal")]
    [SerializeField] private float tposeAngleTolerance = 25f;
    [Tooltip("Min arm length relative to shoulder width")]
    [SerializeField] private float tposeMinExtension = 1.5f;

    [Header("Squat Detection")]
    [Tooltip("Knee angle (degrees) below which a squat is detected")]
    [SerializeField] private float squatKneeAngle = 120f;

    [Header("Lean Detection")]
    [Tooltip("Shoulder-line tilt (degrees) to trigger a lean")]
    [SerializeField] private float leanAngleThreshold = 15f;

    [Header("Events — Generic")]
    public UnityEvent<PoseGesture> OnGestureDetected;

    [Header("Events — Specific")]
    public UnityEvent OnWave;
    public UnityEvent OnHandsUp;
    public UnityEvent OnTPose;
    public UnityEvent OnSquat;
    public UnityEvent OnHandRaiseLeft;
    public UnityEvent OnHandRaiseRight;
    public UnityEvent OnLeanLeft;
    public UnityEvent OnLeanRight;

    // MediaPipe Pose landmark indices
    private const int Nose = 0;
    private const int LShoulder = 11;
    private const int RShoulder = 12;
    private const int LWrist = 15;
    private const int RWrist = 16;
    private const int LHip = 23;
    private const int RHip = 24;
    private const int LKnee = 25;
    private const int RKnee = 26;
    private const int LAnkle = 27;
    private const int RAnkle = 28;

    // Wave tracking — stores (time, wristX) per hand
    private readonly List<(float t, float x)> _leftWristHistory = new List<(float, float)>();
    private readonly List<(float t, float x)> _rightWristHistory = new List<(float, float)>();

    // Currently active state-based gestures (for enter/exit detection)
    private readonly HashSet<PoseGesture> _activeGestures = new HashSet<PoseGesture>();
    private readonly Dictionary<PoseGesture, float> _lastTriggerTime = new Dictionary<PoseGesture, float>();

    /// <summary>
    /// Returns true while the given state-based gesture is active
    /// (e.g. the user is currently holding a T-pose).
    /// </summary>
    public bool IsGestureActive(PoseGesture gesture) => _activeGestures.Contains(gesture);

    private void Awake()
    {
        if (poseDetector == null)
            poseDetector = GetComponent<PoseDetector>();
    }

    private void Update()
    {
        if (poseDetector == null)
            return;

        var lm = poseDetector.CurrentLandmarks;
        if (lm == null || lm.Count < 33)
            return;

        DetectWave(lm);
        DetectHandsUp(lm);
        DetectHandRaise(lm);
        DetectTPose(lm);
        DetectSquat(lm);
        DetectLean(lm);
    }

    #region Detection Methods

    // ── Wave ──────────────────────────────────────────────────────────────
    private void DetectWave(IReadOnlyList<Vector3> lm)
    {
        CheckWaveHand(lm, LWrist, LShoulder, _leftWristHistory);
        CheckWaveHand(lm, RWrist, RShoulder, _rightWristHistory);
    }

    private void CheckWaveHand(IReadOnlyList<Vector3> lm, int wristIdx, int shoulderIdx,
        List<(float t, float x)> history)
    {
        Vector3 wrist = lm[wristIdx];
        Vector3 shoulder = lm[shoulderIdx];

        if (wrist.z < minVisibility || shoulder.z < minVisibility)
        {
            history.Clear();
            return;
        }

        // Hand must be above shoulder (MediaPipe y: 0 = top, 1 = bottom)
        if (wrist.y >= shoulder.y)
        {
            history.Clear();
            return;
        }

        float now = Time.time;
        history.Add((now, wrist.x));

        // Remove entries outside the time window
        while (history.Count > 0 && now - history[0].t > waveTimeWindow)
            history.RemoveAt(0);

        if (history.Count < 4)
            return;

        if (CountOscillations(history) >= waveMinOscillations)
        {
            TriggerEvent(PoseGesture.Wave);
            history.Clear();
        }
    }

    private int CountOscillations(List<(float t, float x)> history)
    {
        int count = 0;
        int dir = 0;           // -1 = moving left, +1 = moving right
        float anchor = history[0].x;

        for (int i = 1; i < history.Count; i++)
        {
            float dx = history[i].x - anchor;

            if (dx > waveMinAmplitude)
            {
                if (dir == -1) count++;
                dir = 1;
                anchor = history[i].x;
            }
            else if (dx < -waveMinAmplitude)
            {
                if (dir == 1) count++;
                dir = -1;
                anchor = history[i].x;
            }
        }

        return count;
    }

    // ── Hands Up ─────────────────────────────────────────────────────────
    private void DetectHandsUp(IReadOnlyList<Vector3> lm)
    {
        bool active = IsVisible(lm, LWrist, RWrist, Nose)
                      && lm[LWrist].y < lm[Nose].y
                      && lm[RWrist].y < lm[Nose].y;

        SetGestureState(PoseGesture.HandsUp, active);
    }

    // ── Hand Raise (single hand) ─────────────────────────────────────────
    private void DetectHandRaise(IReadOnlyList<Vector3> lm)
    {
        bool leftUp = IsVisible(lm, LWrist, Nose) && lm[LWrist].y < lm[Nose].y;
        bool rightUp = IsVisible(lm, RWrist, Nose) && lm[RWrist].y < lm[Nose].y;

        SetGestureState(PoseGesture.HandRaiseLeft, leftUp && !rightUp);
        SetGestureState(PoseGesture.HandRaiseRight, rightUp && !leftUp);
    }

    // ── T-Pose ───────────────────────────────────────────────────────────
    private void DetectTPose(IReadOnlyList<Vector3> lm)
    {
        if (!IsVisible(lm, LShoulder, RShoulder, LWrist, RWrist))
        {
            SetGestureState(PoseGesture.TPose, false);
            return;
        }

        Vector2 ls = Pos(lm[LShoulder]);
        Vector2 rs = Pos(lm[RShoulder]);
        Vector2 lw = Pos(lm[LWrist]);
        Vector2 rw = Pos(lm[RWrist]);

        float shoulderWidth = Vector2.Distance(ls, rs);
        if (shoulderWidth < 0.01f)
        {
            SetGestureState(PoseGesture.TPose, false);
            return;
        }

        // Arms must be extended well beyond shoulder width
        float leftExt = Vector2.Distance(ls, lw) / shoulderWidth;
        float rightExt = Vector2.Distance(rs, rw) / shoulderWidth;

        // Angle from horizontal (absolute — works regardless of mirroring)
        float leftDev = Mathf.Atan2(Mathf.Abs(lw.y - ls.y), Mathf.Abs(lw.x - ls.x)) * Mathf.Rad2Deg;
        float rightDev = Mathf.Atan2(Mathf.Abs(rw.y - rs.y), Mathf.Abs(rw.x - rs.x)) * Mathf.Rad2Deg;

        bool tpose = leftExt > tposeMinExtension
                     && rightExt > tposeMinExtension
                     && leftDev < tposeAngleTolerance
                     && rightDev < tposeAngleTolerance;

        SetGestureState(PoseGesture.TPose, tpose);
    }

    // ── Squat ────────────────────────────────────────────────────────────
    private void DetectSquat(IReadOnlyList<Vector3> lm)
    {
        if (!IsVisible(lm, LHip, RHip, LKnee, RKnee, LAnkle, RAnkle))
        {
            SetGestureState(PoseGesture.Squat, false);
            return;
        }

        float leftAngle = JointAngle(lm[LHip], lm[LKnee], lm[LAnkle]);
        float rightAngle = JointAngle(lm[RHip], lm[RKnee], lm[RAnkle]);

        bool squat = leftAngle < squatKneeAngle && rightAngle < squatKneeAngle;
        SetGestureState(PoseGesture.Squat, squat);
    }

    // ── Lean ─────────────────────────────────────────────────────────────
    private void DetectLean(IReadOnlyList<Vector3> lm)
    {
        if (!IsVisible(lm, LShoulder, RShoulder))
        {
            SetGestureState(PoseGesture.LeanLeft, false);
            SetGestureState(PoseGesture.LeanRight, false);
            return;
        }

        Vector2 ls = Pos(lm[LShoulder]);
        Vector2 rs = Pos(lm[RShoulder]);
        float dist = Vector2.Distance(ls, rs);
        if (dist < 0.01f)
        {
            SetGestureState(PoseGesture.LeanLeft, false);
            SetGestureState(PoseGesture.LeanRight, false);
            return;
        }

        // dy > 0 means right shoulder is lower (higher y in image) → person leans right
        float dy = rs.y - ls.y;
        float tiltDeg = Mathf.Asin(Mathf.Clamp(dy / dist, -1f, 1f)) * Mathf.Rad2Deg;

        SetGestureState(PoseGesture.LeanRight, tiltDeg > leanAngleThreshold);
        SetGestureState(PoseGesture.LeanLeft, tiltDeg < -leanAngleThreshold);
    }

    #endregion

    #region Helpers

    private void SetGestureState(PoseGesture gesture, bool active)
    {
        bool wasActive = _activeGestures.Contains(gesture);
        if (active && !wasActive)
        {
            _activeGestures.Add(gesture);
            TriggerEvent(gesture);
        }
        else if (!active && wasActive)
        {
            _activeGestures.Remove(gesture);
        }
    }

    private void TriggerEvent(PoseGesture gesture)
    {
        float now = Time.time;
        if (_lastTriggerTime.TryGetValue(gesture, out float last) && now - last < cooldownSeconds)
            return;

        _lastTriggerTime[gesture] = now;
        OnGestureDetected?.Invoke(gesture);

        switch (gesture)
        {
            case PoseGesture.Wave:           OnWave?.Invoke(); break;
            case PoseGesture.HandsUp:        OnHandsUp?.Invoke(); break;
            case PoseGesture.TPose:          OnTPose?.Invoke(); break;
            case PoseGesture.Squat:          OnSquat?.Invoke(); break;
            case PoseGesture.HandRaiseLeft:  OnHandRaiseLeft?.Invoke(); break;
            case PoseGesture.HandRaiseRight: OnHandRaiseRight?.Invoke(); break;
            case PoseGesture.LeanLeft:       OnLeanLeft?.Invoke(); break;
            case PoseGesture.LeanRight:      OnLeanRight?.Invoke(); break;
        }

        Debug.Log($"[GestureDetector] {gesture}");
    }

    private bool IsVisible(IReadOnlyList<Vector3> lm, params int[] indices)
    {
        foreach (int i in indices)
        {
            if (i >= lm.Count || lm[i].z < minVisibility)
                return false;
        }
        return true;
    }

    private static Vector2 Pos(Vector3 landmark) => new Vector2(landmark.x, landmark.y);

    private static float JointAngle(Vector3 a, Vector3 joint, Vector3 c)
    {
        Vector2 va = Pos(a) - Pos(joint);
        Vector2 vc = Pos(c) - Pos(joint);
        return Vector2.Angle(va, vc);
    }

    #endregion
}
