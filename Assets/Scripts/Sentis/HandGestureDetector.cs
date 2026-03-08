using System.Globalization;
using Unity.Sentis;
using UnityEngine;
using UnityEngine.UI;
using Unity.XR.XREAL;

/// <summary>
/// グー/パーのみを認識する最小構成のジェスチャー検出クラス。
///
/// HandDetectionLive2DOptimized.cs から可視化ロジックをすべて除去し、
/// 検出パイプラインとジェスチャー判別のみを残したもの。
///
/// [必要なファイル]
///   Assets/Models/hand_detector.onnx
///   Assets/Models/hand_landmarks_detector.onnx
///   Assets/Data/anchors.csv
///   Assets/Shaders/AffineHandCrop.shader (Hidden/AffineHandCrop)
///
/// [公開API]
///   CurrentGesture — GestureType.Gu / GestureType.Pa / GestureType.Unknown
/// </summary>
public class HandGestureDetector : MonoBehaviour
{
    [Header("Sentis")]
    [SerializeField] private ModelAsset detectorModelAsset;
    [SerializeField] private ModelAsset landmarkerModelAsset;
    [SerializeField] private TextAsset  anchorsCSV;

    [Header("Shader")]
    [SerializeField] private Shader cropShader;

    [Header("Camera")]
    [SerializeField] private Material yuvMaterial;

    [Header("UI")]
    [SerializeField] private Text statusText;

    [Header("Detection Settings")]
    [SerializeField] private float scoreThreshold       = 0.5f;
    [SerializeField] private int   detectIntervalFrames = 10;

    // --- 定数 ---
    private const int DetectorSize = 192;
    private const int LandmarkSize = 224;
    private const int NumAnchors   = 2016;
    private const int BoxStride    = 18;
    private const int NumKeypoints = 21;

    // --- Sentis ---
    private IWorker  _detectorWorker;
    private IWorker  _landmarkerWorker;
    private float[,] _anchors;
    private string   _detScoreOutput;
    private string   _detBoxOutput;
    private string   _lmOutput;

    // --- カメラ ---
    private XREALRGBCameraTexture _cam;
    private RenderTexture         _rgbRt;
    private int                   _frameCount;

    // --- 永続化テクスチャバッファ ---
    private RenderTexture _rt192;
    private RenderTexture _rt224;
    private Texture2D     _tex192;
    private Texture2D     _tex224;

    // --- GPU クロップシェーダー用マテリアル ---
    private Material _cropMaterial;

    // --- ジェスチャー ---
    public GestureType CurrentGesture { get; private set; }

    // -----------------------------------------------------------------------

    void Start()
    {
        _cam = XREALRGBCameraTexture.CreateSingleton();
        if (!_cam.IsCapturing) _cam.StartCapture();

        if (cropShader == null)
            Debug.LogError("[HandGestureDetector] cropShader が Inspector で未設定です。" +
                           "Assets/Shaders/AffineHandCrop.shader をアサインしてください。");
        else
            _cropMaterial = new Material(cropShader);

        var detModel = ModelLoader.Load(detectorModelAsset);
        var lmModel  = ModelLoader.Load(landmarkerModelAsset);

        _detScoreOutput   = detModel.outputs[0].name;
        _detBoxOutput     = detModel.outputs[1].name;
        _lmOutput         = lmModel.outputs[0].name;

        _detectorWorker   = WorkerFactory.CreateWorker(BackendType.GPUCompute, detModel);
        _landmarkerWorker = WorkerFactory.CreateWorker(BackendType.GPUCompute, lmModel);
        _anchors          = LoadAnchors(anchorsCSV.text, NumAnchors);

        _rt192  = new RenderTexture(DetectorSize, DetectorSize, 0, RenderTextureFormat.ARGB32);
        _rt224  = new RenderTexture(LandmarkSize, LandmarkSize, 0, RenderTextureFormat.ARGB32);
        _tex192 = new Texture2D(DetectorSize, DetectorSize, TextureFormat.RGB24, false);
        _tex224 = new Texture2D(LandmarkSize, LandmarkSize, TextureFormat.RGB24, false);

        if (statusText != null) statusText.text = "Camera starting...";
    }

    void OnDestroy()
    {
        _detectorWorker?.Dispose();
        _landmarkerWorker?.Dispose();
        if (_rgbRt  != null) { _rgbRt.Release();  Destroy(_rgbRt); }
        if (_rt192  != null) { _rt192.Release();   Destroy(_rt192); }
        if (_rt224  != null) { _rt224.Release();   Destroy(_rt224); }
        if (_tex192 != null) Destroy(_tex192);
        if (_tex224 != null) Destroy(_tex224);
        if (_cropMaterial != null) Destroy(_cropMaterial);
    }

    // -----------------------------------------------------------------------
    // メインループ
    // -----------------------------------------------------------------------

    void Update()
    {
        var yuv = _cam.GetYUVFormatTextures();
        if (yuv[0] == null)
        {
            if (statusText != null) statusText.text = "Camera: waiting...";
            return;
        }

        if (++_frameCount % detectIntervalFrames != 0) return;

        if (_rgbRt == null || _rgbRt.width != yuv[0].width || _rgbRt.height != yuv[0].height)
        {
            _rgbRt?.Release();
            _rgbRt = new RenderTexture(yuv[0].width, yuv[0].height, 0);
        }

        yuvMaterial.SetTexture("_UTex", yuv[1]);
        yuvMaterial.SetTexture("_VTex", yuv[2]);
        Graphics.Blit(yuv[0], _rgbRt, yuvMaterial);

        Detect(_rgbRt);
    }

    // -----------------------------------------------------------------------
    // 検出フロー
    // -----------------------------------------------------------------------

    void Detect(RenderTexture rt)
    {
        Graphics.Blit(rt, _rt192);
        RenderTexture.active = _rt192;
        _tex192.ReadPixels(new Rect(0, 0, DetectorSize, DetectorSize), 0, 0);
        _tex192.Apply();
        RenderTexture.active = null;

        RunDetectionAndLandmark();
    }

    void RunDetectionAndLandmark()
    {
        // ---- Step 1: 手の検出 ----
        using var detInput = TextureToNHWC(_tex192, DetectorSize);
        _detectorWorker.Execute(detInput);

        var rawScores = _detectorWorker.PeekOutput(_detScoreOutput) as TensorFloat;
        var rawBoxes  = _detectorWorker.PeekOutput(_detBoxOutput)   as TensorFloat;
        rawScores.CompleteOperationsAndDownload();
        rawBoxes.CompleteOperationsAndDownload();

        float[] scoresData = rawScores.ToReadOnlyArray();
        float[] boxesData  = rawBoxes.ToReadOnlyArray();

        int   bestIdx   = -1;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < NumAnchors; i++)
        {
            float s = 1f / (1f + Mathf.Exp(-scoresData[i]));
            if (s > bestScore) { bestScore = s; bestIdx = i; }
        }

        if (bestScore < scoreThreshold || bestIdx < 0)
        {
            CurrentGesture = GestureType.Unknown;
            if (statusText != null) statusText.text = "Hand: not detected";
            return;
        }

        float anchorCx = _anchors[bestIdx, 0] * DetectorSize;
        float anchorCy = _anchors[bestIdx, 1] * DetectorSize;

        float boxCx   = boxesData[bestIdx * BoxStride + 0] + anchorCx;
        float boxCy   = boxesData[bestIdx * BoxStride + 1] + anchorCy;
        float boxW    = Mathf.Abs(boxesData[bestIdx * BoxStride + 2]);
        float boxH    = Mathf.Abs(boxesData[bestIdx * BoxStride + 3]);
        float boxSize = Mathf.Max(boxW, boxH);

        float kp0x = boxesData[bestIdx * BoxStride + 4] + anchorCx;
        float kp0y = boxesData[bestIdx * BoxStride + 5] + anchorCy;
        float kp2x = boxesData[bestIdx * BoxStride + 8] + anchorCx;
        float kp2y = boxesData[bestIdx * BoxStride + 9] + anchorCy;

        float dx  = kp2x - kp0x;
        float dy  = kp2y - kp0y;
        float len = Mathf.Sqrt(dx * dx + dy * dy);
        float upX = len > 1e-6f ? dx / len : 0f;
        float upY = len > 1e-6f ? dy / len : -1f;

        float rotation = Mathf.PI * 0.5f - Mathf.Atan2(dy, dx);

        boxCx   += 0.5f * boxSize * upX;
        boxCy   += 0.5f * boxSize * upY;
        boxSize *= 2.6f;

        // ---- Step 2: ランドマーク推定 ----
        if (_cropMaterial == null)
        {
            if (statusText != null) statusText.text = "Error: AffineHandCrop shader not found";
            return;
        }
        _cropMaterial.SetFloat("_CentreX", boxCx);
        _cropMaterial.SetFloat("_CentreY", boxCy);
        _cropMaterial.SetFloat("_Scale",   boxSize / LandmarkSize);
        _cropMaterial.SetFloat("_Cos",     Mathf.Cos(rotation));
        _cropMaterial.SetFloat("_Sin",     Mathf.Sin(rotation));
        Graphics.Blit(_rt192, _rt224, _cropMaterial);

        RenderTexture.active = _rt224;
        _tex224.ReadPixels(new Rect(0, 0, LandmarkSize, LandmarkSize), 0, 0);
        _tex224.Apply();
        RenderTexture.active = null;

        using var lmInput = TextureToNHWC(_tex224, LandmarkSize);
        _landmarkerWorker.Execute(lmInput);

        var rawLm = _landmarkerWorker.PeekOutput(_lmOutput) as TensorFloat;
        rawLm.CompleteOperationsAndDownload();
        float[] lmData = rawLm.ToReadOnlyArray();

        // ---- Step 3: ジェスチャー判別 ----
        CurrentGesture = ClassifyGesture(lmData);
        if (statusText != null) statusText.text = $"Hand: {CurrentGesture}  score={bestScore:F2}";
    }

    // -----------------------------------------------------------------------
    // ジェスチャー判別（クロップ空間で距離比較）
    // -----------------------------------------------------------------------

    static GestureType ClassifyGesture(float[] lmData)
    {
        float wx = lmData[0], wy = lmData[1]; // wrist (landmark 0)
        int[] mcpIndices = { 5, 9, 13, 17 };
        int[] tipIndices = { 8, 12, 16, 20 };
        const float threshold = 1.5f;

        int extendedCount = 0;
        for (int i = 0; i < 4; i++)
        {
            float tx = lmData[tipIndices[i] * 3],     ty = lmData[tipIndices[i] * 3 + 1];
            float mx = lmData[mcpIndices[i] * 3],     my = lmData[mcpIndices[i] * 3 + 1];
            float tipDist = Mathf.Sqrt((tx - wx) * (tx - wx) + (ty - wy) * (ty - wy));
            float mcpDist = Mathf.Sqrt((mx - wx) * (mx - wx) + (my - wy) * (my - wy));
            if (mcpDist > 1e-5f && tipDist > mcpDist * threshold)
                extendedCount++;
        }

        if (extendedCount >= 4) return GestureType.Pa;
        if (extendedCount <= 1) return GestureType.Gu;
        return GestureType.Unknown;
    }

    // -----------------------------------------------------------------------
    // 前処理ユーティリティ
    // -----------------------------------------------------------------------

    static TensorFloat TextureToNHWC(Texture2D tex, int size)
    {
        Color32[] pixels = tex.GetPixels32();
        float[]   data   = new float[size * size * 3];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int srcIdx = (size - 1 - y) * size + x;
            int dstIdx = y * size + x;
            data[dstIdx * 3 + 0] = pixels[srcIdx].r / 255f;
            data[dstIdx * 3 + 1] = pixels[srcIdx].g / 255f;
            data[dstIdx * 3 + 2] = pixels[srcIdx].b / 255f;
        }
        return new TensorFloat(new TensorShape(1, size, size, 3), data);
    }

    // -----------------------------------------------------------------------
    // アンカー読み込み
    // -----------------------------------------------------------------------

    static float[,] LoadAnchors(string csv, int count)
    {
        float[,] result = new float[count, 4];
        string[] lines  = csv.Split('\n');
        int      idx    = 0;
        foreach (string line in lines)
        {
            if (idx >= count) break;
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            string[] parts = trimmed.Split(',');
            if (parts.Length < 4) continue;
            for (int j = 0; j < 4; j++)
                if (float.TryParse(parts[j], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    result[idx, j] = v;
            idx++;
        }
        return result;
    }
}

