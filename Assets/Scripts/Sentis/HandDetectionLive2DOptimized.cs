using System.Globalization;
using Unity.Sentis;
using UnityEngine;
using UnityEngine.UI;
using Unity.XR.XREAL;

/// <summary>
/// HandDetectionLive2D.cs のパフォーマンス最適化版。
/// 機能は同一だが以下の3点を最適化している：
///
/// 【最適化1】GPU アフィンクロップシェーダー
///   旧: AffineCropNHWC() — 224×224 = 50,176回の CPU GetPixelBilinear()
///   新: Graphics.Blit + AffineHandCrop.shader — GPU 1パスで完結
///
/// 【最適化2】RenderTexture / Texture2D の永続化
///   旧: 毎フレーム new Texture2D() / Destroy()
///   新: Start() で一度だけ作成し毎フレーム再利用
///
/// 【最適化3】UI オブジェクト事前割り当て
///   旧: 毎Nフレームに 44個 GameObject を Destroy/Create
///   新: Start() で事前作成し SetActive で表示切替のみ (GC ゼロ)
///
/// [必要なファイル]
///   Assets/Models/hand_detector.onnx
///   Assets/Models/hand_landmarks_detector.onnx
///   Assets/Data/anchors.csv
///   Assets/Shaders/AffineHandCrop.shader (Hidden/AffineHandCrop)
///
/// [Scene セットアップ]
///   HandDetectionLive2D コンポーネントをこのコンポーネントに差し替えるだけ。
///   Inspector フィールドは同じ構成。
/// </summary>
public class HandDetectionLive2DOptimized : MonoBehaviour
{
    [Header("Sentis")]
    [SerializeField] private ModelAsset detectorModelAsset;
    [SerializeField] private ModelAsset landmarkerModelAsset;
    [SerializeField] private TextAsset  anchorsCSV;

    [Header("Shader")]
    [SerializeField] private Shader cropShader; // AffineHandCrop.shader をここにドラッグ＆ドロップ

    [Header("Camera")]
    [SerializeField] private Material yuvMaterial;

    [Header("UI")]
    [SerializeField] private RawImage      previewImage;
    [SerializeField] private RectTransform overlayContainer;
    [SerializeField] private Text          statusText;

    [Header("Detection Settings")]
    [SerializeField] private float scoreThreshold       = 0.5f;
    [SerializeField] private int   detectIntervalFrames = 10; // 旧:5 → 2段階推論に合わせて10に

    [Header("Visualization")]
    [SerializeField] private Color keypointColor = new Color(0.2f, 0.6f, 1f, 1f);
    [SerializeField] private Color lineColor     = new Color(1f, 1f, 1f, 0.9f);
    [SerializeField] private float dotSize       = 12f;
    [SerializeField] private float lineThickness = 4f;

    // --- 定数 ---
    private const int DetectorSize = 192;
    private const int LandmarkSize = 224;
    private const int NumAnchors   = 2016;
    private const int BoxStride    = 18;
    private const int NumKeypoints = 21;

    private static readonly int[][] SkeletonConnections = new int[][]
    {
        new int[]{0,1},  new int[]{0,5},  new int[]{0,9},  new int[]{0,13}, new int[]{0,17},
        new int[]{1,2},  new int[]{2,3},  new int[]{3,4},
        new int[]{5,6},  new int[]{6,7},  new int[]{7,8},
        new int[]{9,10}, new int[]{10,11}, new int[]{11,12},
        new int[]{13,14}, new int[]{14,15}, new int[]{15,16},
        new int[]{17,18}, new int[]{18,19}, new int[]{19,20},
        new int[]{5,9},  new int[]{9,13}, new int[]{13,17}
    };

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

    // --- 最適化2: 永続化テクスチャバッファ ---
    private RenderTexture _rt192; // 192x192 検出器用 RT (毎フレーム再利用)
    private RenderTexture _rt224; // 224x224 クロップ用 RT (毎フレーム再利用)
    private Texture2D     _tex192; // 192x192 CPU 読み取り用 (毎フレーム再利用)
    private Texture2D     _tex224; // 224x224 CPU 読み取り用 (毎フレーム再利用)

    // --- 最適化1: GPU クロップシェーダー用マテリアル ---
    private Material _cropMaterial;

    // --- 最適化3: 事前割り当て UI オブジェクト ---
    private RectTransform[] _dotObjects;   // キーポイントドット (21個)
    private RectTransform[] _boneObjects;  // スケルトン線 (23個)

    // -----------------------------------------------------------------------

    void Start()
    {
        // ★ カメラを最優先で起動 (以降の処理が失敗してもプレビューは映る)
        _cam = XREALRGBCameraTexture.CreateSingleton();
        if (!_cam.IsCapturing) _cam.StartCapture();

        // 最適化1: クロップシェーダーマテリアル作成 (Inspector から直接参照)
        if (cropShader == null)
            Debug.LogError("[HandLive2D-Opt] cropShader が Inspector で未設定です。" +
                           "Assets/Shaders/AffineHandCrop.shader を Crop Shader フィールドにアサインしてください。" +
                           "手検出は無効になります。");
        else
            _cropMaterial = new Material(cropShader);

        // モデルロード
        var detModel = ModelLoader.Load(detectorModelAsset);
        var lmModel  = ModelLoader.Load(landmarkerModelAsset);

        _detScoreOutput   = detModel.outputs[0].name;
        _detBoxOutput     = detModel.outputs[1].name;
        _lmOutput         = lmModel.outputs[0].name;

        _detectorWorker   = WorkerFactory.CreateWorker(BackendType.GPUCompute, detModel);
        _landmarkerWorker = WorkerFactory.CreateWorker(BackendType.GPUCompute, lmModel);
        _anchors          = LoadAnchors(anchorsCSV.text, NumAnchors);

        // 最適化2: 永続化テクスチャバッファを一度だけ作成
        _rt192  = new RenderTexture(DetectorSize, DetectorSize, 0, RenderTextureFormat.ARGB32);
        _rt224  = new RenderTexture(LandmarkSize, LandmarkSize, 0, RenderTextureFormat.ARGB32);
        _tex192 = new Texture2D(DetectorSize, DetectorSize, TextureFormat.RGB24, false);
        _tex224 = new Texture2D(LandmarkSize, LandmarkSize, TextureFormat.RGB24, false);

        // 最適化3: UI オブジェクトを事前割り当て
        PreallocateUI();

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
    // 最適化3: UI 事前割り当て
    // -----------------------------------------------------------------------

    void PreallocateUI()
    {
        // キーポイントドット (21個)
        _dotObjects = new RectTransform[NumKeypoints];
        for (int i = 0; i < NumKeypoints; i++)
        {
            var go   = new GameObject("Dot");
            go.transform.SetParent(overlayContainer, false);
            var rect = go.AddComponent<RectTransform>();
            var img  = go.AddComponent<Image>();
            img.color = keypointColor;
            rect.pivot     = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(dotSize, dotSize);
            go.SetActive(false);
            _dotObjects[i] = rect;
        }

        // スケルトン線 (23本)
        _boneObjects = new RectTransform[SkeletonConnections.Length];
        for (int i = 0; i < SkeletonConnections.Length; i++)
        {
            var go   = new GameObject("Bone");
            go.transform.SetParent(overlayContainer, false);
            var rect = go.AddComponent<RectTransform>();
            var img  = go.AddComponent<Image>();
            img.color = lineColor;
            rect.pivot     = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(0f, lineThickness);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            go.SetActive(false);
            _boneObjects[i] = rect;
        }
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

        previewImage.texture  = yuv[0];
        yuvMaterial.SetTexture("_UTex", yuv[1]);
        yuvMaterial.SetTexture("_VTex", yuv[2]);
        previewImage.material = yuvMaterial;

        if (++_frameCount % detectIntervalFrames != 0) return;

        if (_rgbRt == null || _rgbRt.width != yuv[0].width || _rgbRt.height != yuv[0].height)
        {
            _rgbRt?.Release();
            _rgbRt = new RenderTexture(yuv[0].width, yuv[0].height, 0);
        }
        Graphics.Blit(yuv[0], _rgbRt, yuvMaterial);

        Detect(_rgbRt);
    }

    // -----------------------------------------------------------------------
    // 検出フロー
    // -----------------------------------------------------------------------

    void Detect(RenderTexture rt)
    {
        // 最適化2: 永続化 _rt192 / _tex192 を再利用 (new Texture2D なし)
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

        HideAllUI();

        if (bestScore < scoreThreshold || bestIdx < 0)
        {
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

        // ---- Step 2: ランドマーク推定 (最適化1: GPU クロップ) ----
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
        Graphics.Blit(_rt192, _rt224, _cropMaterial); // GPU 1パスでアフィンクロップ

        // 最適化2: 永続化 _tex224 を再利用
        RenderTexture.active = _rt224;
        _tex224.ReadPixels(new Rect(0, 0, LandmarkSize, LandmarkSize), 0, 0);
        _tex224.Apply();
        RenderTexture.active = null;

        using var lmInput = TextureToNHWC(_tex224, LandmarkSize);
        _landmarkerWorker.Execute(lmInput);

        var rawLm = _landmarkerWorker.PeekOutput(_lmOutput) as TensorFloat;
        rawLm.CompleteOperationsAndDownload();
        float[] lmData = rawLm.ToReadOnlyArray();

        // キーポイントを Canvas 正規化座標(0~1)に変換
        var points2D = new Vector2[NumKeypoints];
        for (int i = 0; i < NumKeypoints; i++)
        {
            float lx = lmData[i * 3 + 0];
            float ly = lmData[i * 3 + 1];

            Vector2 detPt = CropToDetectorSpace(new Vector2(lx, ly),
                                                 new Vector2(boxCx, boxCy), boxSize, rotation);
            points2D[i] = new Vector2(
                detPt.x / DetectorSize,
                1f - detPt.y / DetectorSize
            );
        }

        // 最適化3: SetActive のみ (Destroy/Create なし)
        UpdateSkeleton(points2D);
        UpdateKeypoints(points2D);

        if (statusText != null) statusText.text = $"Hand detected  score={bestScore:F2}";
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

    static Vector2 CropToDetectorSpace(Vector2 cropPt, Vector2 centre, float boxSize, float rotation)
    {
        float scale = boxSize / LandmarkSize;
        float cos   = Mathf.Cos(rotation);
        float sin   = Mathf.Sin(rotation);
        float cx    = cropPt.x - LandmarkSize * 0.5f;
        float cy    = cropPt.y - LandmarkSize * 0.5f;
        float rx    = cos * cx - sin * cy;
        float ry    = sin * cx + cos * cy;
        return new Vector2(scale * rx + centre.x, -scale * ry + centre.y);
    }

    // -----------------------------------------------------------------------
    // 最適化3: Canvas 2D 描画 (SetActive のみ、Destroy/Create なし)
    // -----------------------------------------------------------------------

    void HideAllUI()
    {
        foreach (var r in _dotObjects)  r.gameObject.SetActive(false);
        foreach (var r in _boneObjects) r.gameObject.SetActive(false);
    }

    void UpdateKeypoints(Vector2[] points)
    {
        for (int i = 0; i < NumKeypoints; i++)
        {
            var rect          = _dotObjects[i];
            rect.anchorMin    = points[i];
            rect.anchorMax    = points[i];
            rect.anchoredPosition = Vector2.zero;
            rect.gameObject.SetActive(true);
        }
    }

    void UpdateSkeleton(Vector2[] points)
    {
        Vector2 containerSize = overlayContainer.rect.size;

        for (int i = 0; i < SkeletonConnections.Length; i++)
        {
            Vector2 a = points[SkeletonConnections[i][0]];
            Vector2 b = points[SkeletonConnections[i][1]];

            Vector2 aPx  = new Vector2(a.x * containerSize.x, a.y * containerSize.y);
            Vector2 bPx  = new Vector2(b.x * containerSize.x, b.y * containerSize.y);
            float   dist  = Vector2.Distance(aPx, bPx);
            float   angle = Mathf.Atan2(bPx.y - aPx.y, bPx.x - aPx.x) * Mathf.Rad2Deg;

            var rect              = _boneObjects[i];
            rect.sizeDelta        = new Vector2(dist, lineThickness);
            rect.anchoredPosition = (aPx + bPx) * 0.5f;
            rect.localRotation    = Quaternion.Euler(0f, 0f, angle);
            rect.gameObject.SetActive(true);
        }
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
