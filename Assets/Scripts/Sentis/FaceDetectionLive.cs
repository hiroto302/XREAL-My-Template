using System.Collections.Generic;
using Unity.Sentis;
using UnityEngine;
using UnityEngine.UI;
using Unity.XR.XREAL;

/// <summary>
/// XREAL RGBカメラのリアルタイム映像に BlazeFace 顔検出を適用するスクリプト。
/// FaceDetectionTest.cs（静止画版）の検出ロジックをそのまま流用し、
/// 入力をカメラの YUV テクスチャに差し替えている。
///
/// [Scene セットアップ]
/// Canvas
///   ├─ RawImage-Preview  (previewImage に割り当て)
///   │    └─ OverlayContainer (overlayContainer に割り当て)
///   └─ Text-Status       (statusText に割り当て)
///
/// [Inspector 設定]
///   - Model Asset  : Assets/Models/blaze_face_short_range.onnx
///   - YUV Material : YUVTransRGB シェーダーの Material
///
/// 腕一本分 前に伸ばした距離 の人物を2人まで同時検出できた
/// </summary>
public class FaceDetectionLive : MonoBehaviour
{
    [Header("Sentis")]
    [SerializeField] private ModelAsset modelAsset;

    [Header("UI")]
    [SerializeField] private RawImage      previewImage;       // カメラ映像プレビュー
    [SerializeField] private Material      yuvMaterial;        // YUVTransRGB シェーダーの Material
    [SerializeField] private RectTransform overlayContainer;   // BBox 描画先
    [SerializeField] private Text          statusText;

    [Header("Detection Settings")]
    [SerializeField] private float scoreThreshold      = 0.5f;
    [SerializeField] private float iouThreshold        = 0.3f;
    [SerializeField] private int   detectIntervalFrames = 5;   // N フレームごとに推論

    private IWorker                   _worker;
    private float[,]                  _anchors;
    private XREALRGBCameraTexture     _cam;
    private RenderTexture             _rgbRt;   // YUV→RGB 変換結果（lazy 生成）
    private int                       _frameCount;

    struct Detection
    {
        public float xMin, yMin, xMax, yMax;
        public float score;
    }

    private readonly List<GameObject> _activeBoxes = new List<GameObject>();

    // -----------------------------------------------------------------------

    void Start()
    {
        var model = ModelLoader.Load(modelAsset);
        _worker  = WorkerFactory.CreateWorker(BackendType.GPUCompute, model);
        _anchors = GenerateAnchors();

        // RGBCameraExample.cs が同シーンで先に StartCapture() を呼んでいる場合はスキップ
        _cam = XREALRGBCameraTexture.CreateSingleton();
        if (!_cam.IsCapturing)
            _cam.StartCapture();

        if (statusText != null) statusText.text = "Camera starting...";
    }

    void OnDestroy()
    {
        _worker?.Dispose();
        if (_rgbRt != null) { _rgbRt.Release(); Destroy(_rgbRt); }
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

        // YUV テクスチャをシェーダー経由でプレビュー表示
        previewImage.texture = yuv[0];
        yuvMaterial.SetTexture("_UTex", yuv[1]);
        yuvMaterial.SetTexture("_VTex", yuv[2]);
        previewImage.material = yuvMaterial;

        // N フレームごとに推論
        if (++_frameCount % detectIntervalFrames != 0) return;

        // YUV → RGB RenderTexture（カメラ解像度が判明した初回に作成）
        if (_rgbRt == null || _rgbRt.width != yuv[0].width || _rgbRt.height != yuv[0].height)
        {
            _rgbRt?.Release();
            _rgbRt = new RenderTexture(yuv[0].width, yuv[0].height, 0);
        }
        Graphics.Blit(yuv[0], _rgbRt, yuvMaterial);

        Detect(_rgbRt);
    }

    // -----------------------------------------------------------------------
    // 検出フロー（FaceDetectionTest と同じ、引数だけ RenderTexture に変更）
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

        ClearBoundingBoxes();
        DrawBoundingBoxes(filtered);

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
    // アンカー生成（FaceDetectionTest と同一）
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
    // 検出デコード（FaceDetectionTest と同一）
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
    // Greedy NMS（FaceDetectionTest と同一）
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
    // Canvas バウンディングボックス描画（FaceDetectionTest と同一）
    // -----------------------------------------------------------------------

    void ClearBoundingBoxes()
    {
        foreach (var go in _activeBoxes) Destroy(go);
        _activeBoxes.Clear();
    }

    void DrawBoundingBoxes(List<Detection> detections)
    {
        foreach (var d in detections)
        {
            var go    = new GameObject("BBox");
            go.transform.SetParent(overlayContainer, false);

            var rect  = go.AddComponent<RectTransform>();
            var image = go.AddComponent<Image>();

            image.color = new Color(0f, 1f, 0f, 0.35f);

            rect.anchorMin = new Vector2(d.xMin, 1f - d.yMax);
            rect.anchorMax = new Vector2(d.xMax, 1f - d.yMin);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            _activeBoxes.Add(go);
        }
    }
}
