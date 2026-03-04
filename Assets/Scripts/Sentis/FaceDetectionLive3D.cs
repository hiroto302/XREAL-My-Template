using System.Collections.Generic;
using Unity.Sentis;
using UnityEngine;
using UnityEngine.UI;
using Unity.XR.XREAL;

/// <summary>
/// XREAL RGBカメラのリアルタイム映像にBlazeFace顔検出を適用し、
/// 検出した顔を3D空間にビルボードマーカーとして表示するスクリプト。
///
/// FaceDetectionLive.csの2D Canvasオーバーレイを3Dワールド座標投影に差し替えた版。
/// 深度推定（B案）: bboxのピクセル幅 + 顔幅既知値（15cm）+ ピンホールカメラモデルで算出。
///   depth = (face_width_real × focal_length_px) / bbox_width_px
///   focal_length_px ≈ 130.7px  (FOV 52°, 入力128px)
///
/// [Scene セットアップ]
///   - このコンポーネントをGameObjectにアタッチ
///   - Model Asset: Assets/Models/blaze_face_short_range.onnx
///   - YUV Material: YUVTransRGB シェーダーのMaterial
///
/// [オプション]
///   - Preview Image: カメラ映像確認用RawImage（省略可）
///   - Status Text: 検出数デバッグ表示用Text（省略可）
///   - Marker Material: マーカー用Material（省略時はSprites/Defaultで自動生成）
/// </summary>
public class FaceDetectionLive3D : MonoBehaviour
{
    [Header("Sentis")]
    [SerializeField] private ModelAsset modelAsset;

    [Header("Camera")]
    [SerializeField] private Material yuvMaterial;

    [Header("UI (Optional)")]
    [SerializeField] private RawImage previewImage;
    [SerializeField] private Text     statusText;

    [Header("Detection Settings")]
    [SerializeField] private float scoreThreshold       = 0.5f;
    [SerializeField] private float iouThreshold         = 0.3f;
    [SerializeField] private int   detectIntervalFrames = 5;

    [Header("3D Marker Settings")]
    [SerializeField] private float    markerSize  = 0.2f;                           // マーカーサイズ（メートル）
    [SerializeField] private Color    markerColor = new Color(0f, 1f, 0f, 0.5f);  // 緑・半透明
    [SerializeField] private Material markerMaterial;                               // 省略時は自動生成

    // ピンホールカメラモデルによる深度推定パラメータ
    // XREAL RGB カメラ FOV=52°, モデル入力128px → focal_length = (128/2) / tan(26°) ≈ 130.7
    private const float FaceWidthReal   = 0.15f;   // 平均顔幅 15cm
    private const float FocalLengthPx   = 130.7f;

    private IWorker               _worker;
    private float[,]              _anchors;
    private XREALRGBCameraTexture _cam;
    private RenderTexture         _rgbRt;
    private int                   _frameCount;
    private Material              _markerMatInstance;

    struct Detection
    {
        public float xMin, yMin, xMax, yMax;
        public float score;
    }

    private readonly List<GameObject> _activeMarkers = new List<GameObject>();

    // -----------------------------------------------------------------------

    void Start()
    {
        var model = ModelLoader.Load(modelAsset);
        _worker  = WorkerFactory.CreateWorker(BackendType.GPUCompute, model);
        _anchors = GenerateAnchors();

        _cam = XREALRGBCameraTexture.CreateSingleton();
        if (!_cam.IsCapturing)
            _cam.StartCapture();

        // Materialが未設定の場合はSprites/Defaultで生成
        _markerMatInstance = (markerMaterial != null)
            ? new Material(markerMaterial)
            : new Material(Shader.Find("Sprites/Default"));
        _markerMatInstance.color = markerColor;

        if (statusText != null) statusText.text = "Camera starting...";
    }

    void OnDestroy()
    {
        _worker?.Dispose();
        if (_rgbRt != null) { _rgbRt.Release(); Destroy(_rgbRt); }
        if (_markerMatInstance != null) Destroy(_markerMatInstance);
        ClearMarkers();
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

        // プレビュー表示（オプション）
        if (previewImage != null)
        {
            previewImage.texture  = yuv[0];
            yuvMaterial.SetTexture("_UTex", yuv[1]);
            yuvMaterial.SetTexture("_VTex", yuv[2]);
            previewImage.material = yuvMaterial;
        }

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
        using var input = PrepareInputNHWC(rt);
        _worker.Execute(input);

        var rawScores = _worker.PeekOutput("classificators") as TensorFloat;
        var rawBoxes  = _worker.PeekOutput("regressors")     as TensorFloat;

        rawScores.CompleteOperationsAndDownload();
        rawBoxes.CompleteOperationsAndDownload();
        float[] scoresData = rawScores.ToReadOnlyArray();
        float[] boxesData  = rawBoxes.ToReadOnlyArray();

        var detections = DecodeDetections(scoresData, boxesData);
        var filtered   = SimpleNMS(detections);

        ClearMarkers();
        Draw3DMarkers(filtered);

        if (statusText != null) statusText.text = $"{filtered.Count} 件検出";
    }

    // -----------------------------------------------------------------------
    // 入力前処理: RenderTexture → 128x128 NHWC TensorFloat
    // -----------------------------------------------------------------------

    TensorFloat PrepareInputNHWC(RenderTexture srcRt)
    {
        var rt128 = RenderTexture.GetTemporary(128, 128, 0);
        Graphics.Blit(srcRt, rt128);

        RenderTexture.active = rt128;
        var resized = new Texture2D(128, 128, TextureFormat.RGB24, false);
        resized.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
        resized.Apply();
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt128);

        var pixels = resized.GetPixels32();
        Destroy(resized);

        var data = new float[128 * 128 * 3];
        // GetPixels32() は Y=0 が下（Unity座標）。BlazeFace は Y=0 が上なので反転する。
        for (int y = 0; y < 128; y++)
        for (int x = 0; x < 128; x++)
        {
            int srcIdx = (127 - y) * 128 + x;
            int dstIdx = y * 128 + x;
            data[dstIdx * 3 + 0] = pixels[srcIdx].r / 255f;
            data[dstIdx * 3 + 1] = pixels[srcIdx].g / 255f;
            data[dstIdx * 3 + 2] = pixels[srcIdx].b / 255f;
        }

        return new TensorFloat(new TensorShape(1, 128, 128, 3), data);
    }

    // -----------------------------------------------------------------------
    // アンカー生成（FaceDetectionLive と同一）
    // -----------------------------------------------------------------------

    static float[,] GenerateAnchors()
    {
        var anchors = new float[896, 2];
        int idx = 0;

        int fmSize = 16;
        for (int y = 0; y < fmSize; y++)
        for (int x = 0; x < fmSize; x++)
        for (int a = 0; a < 2; a++)
        {
            anchors[idx, 0] = (x + 0.5f) / fmSize;
            anchors[idx, 1] = (y + 0.5f) / fmSize;
            idx++;
        }

        fmSize = 8;
        for (int y = 0; y < fmSize; y++)
        for (int x = 0; x < fmSize; x++)
        for (int a = 0; a < 6; a++)
        {
            anchors[idx, 0] = (x + 0.5f) / fmSize;
            anchors[idx, 1] = (y + 0.5f) / fmSize;
            idx++;
        }

        return anchors;
    }

    // -----------------------------------------------------------------------
    // 検出デコード（FaceDetectionLive と同一）
    // -----------------------------------------------------------------------

    List<Detection> DecodeDetections(float[] scoresData, float[] boxesData)
    {
        const float inputSize = 128f;
        var result = new List<Detection>();

        for (int i = 0; i < 896; i++)
        {
            float score = 1f / (1f + Mathf.Exp(-scoresData[i]));
            if (score < scoreThreshold) continue;

            float xCenter = boxesData[i * 16 + 0] + _anchors[i, 0] * inputSize;
            float yCenter = boxesData[i * 16 + 1] + _anchors[i, 1] * inputSize;
            float wHalf   = boxesData[i * 16 + 2] * 0.5f;
            float hHalf   = boxesData[i * 16 + 3] * 0.5f;

            result.Add(new Detection
            {
                xMin  = Mathf.Clamp01((xCenter - wHalf) / inputSize),
                yMin  = Mathf.Clamp01((yCenter - hHalf) / inputSize),
                xMax  = Mathf.Clamp01((xCenter + wHalf) / inputSize),
                yMax  = Mathf.Clamp01((yCenter + hHalf) / inputSize),
                score = score
            });
        }

        result.Sort((a, b) => b.score.CompareTo(a.score));
        return result;
    }

    // -----------------------------------------------------------------------
    // Greedy NMS（FaceDetectionLive と同一）
    // -----------------------------------------------------------------------

    List<Detection> SimpleNMS(List<Detection> detections)
    {
        var kept = new List<Detection>();
        foreach (var d in detections)
        {
            bool suppressed = false;
            foreach (var k in kept)
                if (IoU(d, k) > iouThreshold) { suppressed = true; break; }
            if (!suppressed) kept.Add(d);
        }
        return kept;
    }

    static float IoU(Detection a, Detection b)
    {
        float ix1  = Mathf.Max(a.xMin, b.xMin);
        float iy1  = Mathf.Max(a.yMin, b.yMin);
        float ix2  = Mathf.Min(a.xMax, b.xMax);
        float iy2  = Mathf.Min(a.yMax, b.yMax);
        float inter = Mathf.Max(0, ix2 - ix1) * Mathf.Max(0, iy2 - iy1);
        if (inter == 0) return 0;
        float aArea = (a.xMax - a.xMin) * (a.yMax - a.yMin);
        float bArea = (b.xMax - b.xMin) * (b.yMax - b.yMin);
        return inter / (aArea + bArea - inter);
    }

    // -----------------------------------------------------------------------
    // 3Dマーカー描画（ビルボード）
    // -----------------------------------------------------------------------

    /// <summary>
    /// B案: バウンディングボックス幅から深度を推定する。
    /// depth = (face_width_real[m] × focal_length_px) / bbox_width_px
    /// </summary>
    float EstimateDepth(float bboxWidthNormalized)
    {
        float bboxWidthPx = bboxWidthNormalized * 128f;
        if (bboxWidthPx < 1f) return 1.5f; // フォールバック値
        return FaceWidthReal * FocalLengthPx / bboxWidthPx;
    }

    /// <summary>
    /// 正規化画像座標（BlazeFace出力）→ 3Dワールド座標。
    /// Camera.main.ViewportToWorldPoint を使用。
    /// </summary>
    Vector3 ToWorldPosition(Detection d, float depth)
    {
        float cx = (d.xMin + d.xMax) / 2f;
        float cy = 1f - (d.yMin + d.yMax) / 2f; // Unity UI の Y 反転
        return Camera.main.ViewportToWorldPoint(new Vector3(cx, cy, depth));
    }

    void ClearMarkers()
    {
        foreach (var go in _activeMarkers) Destroy(go);
        _activeMarkers.Clear();
    }

    void Draw3DMarkers(List<Detection> detections)
    {
        foreach (var d in detections)
        {
            float   depth = EstimateDepth(d.xMax - d.xMin);
            Vector3 pos   = ToWorldPosition(d, depth);

            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "FaceMarker3D";

            // 物理コライダーは不要
            Destroy(go.GetComponent<MeshCollider>());

            go.transform.position  = pos;
            // カメラと同じ回転にすることでビルボード効果（常にカメラ正面を向く）
            go.transform.rotation  = Camera.main.transform.rotation;
            go.transform.localScale = Vector3.one * markerSize;

            go.GetComponent<MeshRenderer>().material = _markerMatInstance;

            _activeMarkers.Add(go);
        }
    }
}
