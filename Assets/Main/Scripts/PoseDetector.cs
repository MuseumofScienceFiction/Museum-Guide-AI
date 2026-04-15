using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using Mediapipe;
using Mediapipe.Tasks.Vision.PoseLandmarker;
using Mediapipe.Unity;
using Mediapipe.Unity.Experimental;
using Color = UnityEngine.Color;
using Debug = UnityEngine.Debug;
using Rect = UnityEngine.Rect;

/// <summary>
/// Captures webcam frames, runs MediaPipe Pose Landmarker, and draws skeleton overlay.
///
/// Setup:
///   1. Install MediaPipe Unity Plugin from GitHub Releases (.tgz).
///   2. Download the pose model and place it at Assets/StreamingAssets/pose_landmarker_full.bytes
///      https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_full/float16/latest/pose_landmarker_full.task
///   3. Create a Canvas → RawImage and assign it to webcamDisplay.
/// </summary>
public class PoseDetector : MonoBehaviour
{
    [Header("Webcam Settings")]
    [SerializeField] private RawImage webcamDisplay;
    [SerializeField] private int requestedWidth = 1280;
    [SerializeField] private int requestedHeight = 720;
    [SerializeField] private int requestedFps = 30;

    [Header("Pose Settings")]
    [Tooltip("Model file name inside Assets/StreamingAssets")]
    [SerializeField] private string modelFileName = "pose_landmarker_full.bytes";
    [SerializeField] private int numPoses = 1;
    [SerializeField] private float minDetectionConfidence = 0.5f;
    [SerializeField] private float minPresenceConfidence = 0.5f;
    [SerializeField] private float minTrackingConfidence = 0.5f;

    [Header("Visualization")]
    [SerializeField] private Color landmarkColor = Color.green;
    [SerializeField] private Color connectionColor = Color.cyan;
    [SerializeField] private float landmarkRadius = 6f;

    private WebCamTexture _webCamTexture;
    private PoseLandmarker _poseLandmarker;
    private TextureFramePool _textureFramePool;
    private PoseLandmarkerResult _result;
    private readonly Stopwatch _stopwatch = new Stopwatch();
    private bool _isInitialized;
    private bool _isGlogInitialized;

    // Detected landmarks in normalized [0,1] coordinates (x, y, visibility).
    private readonly List<Vector3> _currentLandmarks = new List<Vector3>();

    /// <summary>
    /// Current frame pose landmarks. Each Vector3: x, y — normalized position; z — visibility.
    /// Empty when no pose is detected. Contains 33 entries for a full pose.
    /// </summary>
    public IReadOnlyList<Vector3> CurrentLandmarks => _currentLandmarks;

    // MediaPipe Pose connections (pairs of landmark indices).
    private static readonly int[,] Connections = new int[,]
    {
        { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 7 },
        { 0, 4 }, { 4, 5 }, { 5, 6 }, { 6, 8 },
        { 9, 10 },
        { 11, 12 },
        { 11, 13 }, { 13, 15 }, { 15, 17 }, { 15, 19 }, { 15, 21 }, { 17, 19 },
        { 12, 14 }, { 14, 16 }, { 16, 18 }, { 16, 20 }, { 16, 22 }, { 18, 20 },
        { 11, 23 }, { 12, 24 }, { 23, 24 },
        { 23, 25 }, { 25, 27 }, { 27, 29 }, { 27, 31 }, { 29, 31 },
        { 24, 26 }, { 26, 28 }, { 28, 30 }, { 28, 32 }, { 30, 32 },
    };

    #region Lifecycle

    private IEnumerator Start()
    {
        yield return InitializeMediaPipe();
        if (_poseLandmarker == null)
            yield break;

        yield return InitializeWebcam();
        if (_webCamTexture == null)
            yield break;

        _textureFramePool = new TextureFramePool(
            _webCamTexture.width, _webCamTexture.height, TextureFormat.RGBA32, 10);

        _stopwatch.Start();
        _isInitialized = true;

        yield return RunDetectionLoop();
    }

    private void OnDestroy()
    {
        _isInitialized = false;
        _stopwatch.Stop();

        _poseLandmarker?.Close();
        _poseLandmarker = null;

        _textureFramePool?.Dispose();
        _textureFramePool = null;

        if (_webCamTexture != null)
        {
            _webCamTexture.Stop();
            Destroy(_webCamTexture);
        }

        if (_isGlogInitialized)
        {
            Glog.Shutdown();
            _isGlogInitialized = false;
        }
        Protobuf.ResetLogHandler();
    }

    #endregion

    #region Initialization

    private IEnumerator InitializeMediaPipe()
    {
        Protobuf.SetLogHandler(Protobuf.DefaultLogHandler);
        Glog.Logtostderr = true;
        Glog.Initialize("MediaPipeUnityPlugin");
        _isGlogInitialized = true;

        ResourceUtil.EnableCustomResolver();

        var modelFullPath = Path.Combine(Application.streamingAssetsPath, modelFileName);
        if (!File.Exists(modelFullPath))
        {
            Debug.LogError(
                $"[PoseDetector] Model not found at: {modelFullPath}\n" +
                "Download from: https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_full/float16/latest/pose_landmarker_full.task\n" +
                "and place it in Assets/StreamingAssets/ (rename to .bytes).");
            yield break;
        }

        ResourceUtil.SetAssetPath(modelFileName, modelFullPath);

        var options = new PoseLandmarkerOptions(
            new Mediapipe.Tasks.Core.BaseOptions(
                Mediapipe.Tasks.Core.BaseOptions.Delegate.CPU,
                modelAssetPath: modelFileName),
            runningMode: Mediapipe.Tasks.Vision.Core.RunningMode.VIDEO,
            numPoses: numPoses,
            minPoseDetectionConfidence: minDetectionConfidence,
            minPosePresenceConfidence: minPresenceConfidence,
            minTrackingConfidence: minTrackingConfidence
        );

        _poseLandmarker = PoseLandmarker.CreateFromOptions(options);
        _result = PoseLandmarkerResult.Alloc(numPoses);

        Debug.Log("[PoseDetector] MediaPipe PoseLandmarker initialized.");
    }

    private IEnumerator InitializeWebcam()
    {
        yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);

        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            Debug.LogError("[PoseDetector] Webcam permission denied.");
            yield break;
        }

        WebCamDevice[] devices = WebCamTexture.devices;
        if (devices.Length == 0)
        {
            Debug.LogError("[PoseDetector] No webcam found.");
            yield break;
        }

        Debug.Log($"[PoseDetector] Using webcam: {devices[0].name}");
        _webCamTexture = new WebCamTexture(devices[0].name, requestedWidth, requestedHeight, requestedFps);
        _webCamTexture.Play();

        while (_webCamTexture.width < 100)
            yield return null;

        if (webcamDisplay != null)
        {
            webcamDisplay.texture = _webCamTexture;
            webcamDisplay.SetNativeSize();
        }

        Debug.Log($"[PoseDetector] Webcam started ({_webCamTexture.width}x{_webCamTexture.height}).");
    }

    #endregion

    #region Detection Loop

    private IEnumerator RunDetectionLoop()
    {
        var imageProcessingOptions = new Mediapipe.Tasks.Vision.Core.ImageProcessingOptions(rotationDegrees: 0);
        var waitForEndOfFrame = new WaitForEndOfFrame();

        while (_isInitialized)
        {
            if (!_webCamTexture.didUpdateThisFrame)
            {
                yield return null;
                continue;
            }

            if (!_textureFramePool.TryGetTextureFrame(out var textureFrame))
            {
                yield return waitForEndOfFrame;
                continue;
            }

            yield return waitForEndOfFrame;

            // Flip vertically to convert from Unity's bottom-to-top texture layout
            // to MediaPipe's expected top-to-bottom orientation.
            // videoVerticallyMirrored == true means the texture is already top-to-bottom.
            bool flipV = !_webCamTexture.videoVerticallyMirrored;
            textureFrame.ReadTextureOnCPU(_webCamTexture, flipHorizontally: false, flipVertically: flipV);
            var image = textureFrame.BuildCPUImage();
            textureFrame.Release();

            long timestampMs = _stopwatch.ElapsedTicks / TimeSpan.TicksPerMillisecond;

            if (_poseLandmarker.TryDetectForVideo(image, timestampMs, imageProcessingOptions, ref _result))
            {
                ExtractLandmarks();
            }
            else
            {
                _currentLandmarks.Clear();
            }
        }
    }

    private void ExtractLandmarks()
    {
        _currentLandmarks.Clear();

        if (_result.poseLandmarks == null || _result.poseLandmarks.Count == 0)
            return;

        foreach (var lm in _result.poseLandmarks[0].landmarks)
        {
            _currentLandmarks.Add(new Vector3(lm.x, lm.y, lm.visibility ?? 0f));
        }
    }

    #endregion

    #region Visualization (OnGUI)

    private void OnGUI()
    {
        if (_currentLandmarks.Count == 0 || webcamDisplay == null)
            return;

        RectTransform rt = webcamDisplay.rectTransform;
        Vector3[] corners = new Vector3[4];
        rt.GetWorldCorners(corners);

        Vector2 screenBL = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
        Vector2 screenTR = RectTransformUtility.WorldToScreenPoint(null, corners[2]);

        float displayW = screenTR.x - screenBL.x;
        float displayH = screenTR.y - screenBL.y;

        for (int i = 0; i < Connections.GetLength(0); i++)
        {
            int idxA = Connections[i, 0];
            int idxB = Connections[i, 1];
            if (idxA >= _currentLandmarks.Count || idxB >= _currentLandmarks.Count)
                continue;

            Vector3 a = _currentLandmarks[idxA];
            Vector3 b = _currentLandmarks[idxB];
            if (a.z < 0.5f || b.z < 0.5f)
                continue;

            Vector2 pA = NormalizedToScreen(a, screenBL, displayW, displayH);
            Vector2 pB = NormalizedToScreen(b, screenBL, displayW, displayH);
            DrawLine(pA, pB, connectionColor, 2f);
        }

        for (int i = 0; i < _currentLandmarks.Count; i++)
        {
            Vector3 lm = _currentLandmarks[i];
            if (lm.z < 0.5f)
                continue;

            Vector2 p = NormalizedToScreen(lm, screenBL, displayW, displayH);
            DrawCircle(p, landmarkRadius, landmarkColor);
        }
    }

    private Vector2 NormalizedToScreen(Vector3 normalized, Vector2 screenBL, float w, float h)
    {
        float sx = screenBL.x + normalized.x * w;
        float sy = UnityEngine.Screen.height - (screenBL.y + (1f - normalized.y) * h);
        return new Vector2(sx, sy);
    }

    private static Texture2D _whiteTexture;

    private static Texture2D WhiteTexture
    {
        get
        {
            if (_whiteTexture == null)
            {
                _whiteTexture = new Texture2D(1, 1);
                _whiteTexture.SetPixel(0, 0, Color.white);
                _whiteTexture.Apply();
            }
            return _whiteTexture;
        }
    }

    private void DrawLine(Vector2 a, Vector2 b, Color color, float width)
    {
        Matrix4x4 matrixBak = GUI.matrix;
        Color colorBak = GUI.color;
        GUI.color = color;

        Vector2 d = b - a;
        float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        float length = d.magnitude;

        GUIUtility.RotateAroundPivot(angle, a);
        GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, length, width), WhiteTexture);

        GUI.matrix = matrixBak;
        GUI.color = colorBak;
    }

    private void DrawCircle(Vector2 center, float radius, Color color)
    {
        Color colorBak = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(new Rect(center.x - radius, center.y - radius, radius * 2, radius * 2), WhiteTexture);
        GUI.color = colorBak;
    }

    #endregion
}
