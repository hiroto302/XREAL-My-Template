using System.Collections.Generic;
using Unity.Sentis;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Sentis 1.4.0-pre.3 (旧API) で BlazeFace Short Range を静止画テストするスクリプト。
/// モデル出力をデコードして Canvas 上にバウンディングボックスを描画する。
/// </summary>
public class FaceDetectionTest : MonoBehaviour
{
    [Header("Sentis")]
    [SerializeField] private ModelAsset modelAsset;

    [Header("UI")]
    [SerializeField] private Texture2D testImage;
    [SerializeField] private RawImage displayImage;
    [SerializeField] private RectTransform overlayContainer;
    [SerializeField] private Text statusText;

    [Header("Detection Settings")]
    [SerializeField] private float scoreThreshold = 0.5f;
    [SerializeField] private float iouThreshold   = 0.3f;

    private IWorker _worker;
    private float[,] _anchors; // [896, 2] : (x_center, y_center) 正規化済み

    // 検出結果1件
    struct Detection
    {
        public float xMin, yMin, xMax, yMax; // [0,1] 正規化座標
        public float score;
    }

    // 現在表示中の BBox UI オブジェクト
    private readonly List<GameObject> _activeBoxes = new List<GameObject>();

    // -----------------------------------------------------------------------

    void Start()
    {
        var model = ModelLoader.Load(modelAsset);
        _worker  = WorkerFactory.CreateWorker(BackendType.GPUCompute, model);
        _anchors = GenerateAnchors();

        displayImage.texture = testImage;
        statusText.text = "Model loaded. Running...";

        Detect(testImage);
    }

    void OnDestroy()
    {
        _worker?.Dispose();
    }

    // -----------------------------------------------------------------------
    // メイン検出フロー
    // -----------------------------------------------------------------------

    void Detect(Texture2D tex)
    {
        using var input = PrepareInputNHWC(tex);
        _worker.Execute(input);

        var rawScores = _worker.PeekOutput("classificators") as TensorFloat;
        var rawBoxes  = _worker.PeekOutput("regressors")     as TensorFloat;

        // GPU → CPU へダウンロード（必須）
        rawScores.CompleteOperationsAndDownload();
        rawBoxes.CompleteOperationsAndDownload();
        float[] scoresData = rawScores.ToReadOnlyArray(); // 896
        float[] boxesData  = rawBoxes.ToReadOnlyArray();  // 896 * 16

        var detections = DecodeDetections(scoresData, boxesData);
        var filtered   = SimpleNMS(detections);

        ClearBoundingBoxes();
        DrawBoundingBoxes(filtered);

        statusText.text = $"{filtered.Count} 件検出";
        Debug.Log($"[FaceDetect] {filtered.Count} detections");
    }

    // -----------------------------------------------------------------------
    // 入力前処理: 128x128 NHWC TensorFloat
    // -----------------------------------------------------------------------

    TensorFloat PrepareInputNHWC(Texture2D tex)
    {
        var rt = RenderTexture.GetTemporary(128, 128, 0);
        Graphics.Blit(tex, rt);

        RenderTexture.active = rt;
        var resized = new Texture2D(128, 128, TextureFormat.RGB24, false);
        resized.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
        resized.Apply();
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);

        var pixels = resized.GetPixels32();
        Destroy(resized);

        var data = new float[128 * 128 * 3];
        // GetPixels32() は Y=0 が下（Unity座標）。BlazeFace は Y=0 が上なので反転する。
        for (int y = 0; y < 128; y++)
        for (int x = 0; x < 128; x++)
        {
            int srcIdx = (127 - y) * 128 + x; // Y軸反転
            int dstIdx = y * 128 + x;
            data[dstIdx * 3 + 0] = pixels[srcIdx].r / 255f;
            data[dstIdx * 3 + 1] = pixels[srcIdx].g / 255f;
            data[dstIdx * 3 + 2] = pixels[srcIdx].b / 255f;
        }

        return new TensorFloat(new TensorShape(1, 128, 128, 3), data);
    }

    // -----------------------------------------------------------------------
    // アンカー生成 (BlazeFace Short Range 固定構成)
    // Feature map 16x16: 2 anchors/cell = 512
    // Feature map  8x8:  6 anchors/cell = 384
    // Total: 896
    // -----------------------------------------------------------------------

    static float[,] GenerateAnchors()
    {
        var anchors = new float[896, 2];
        int idx = 0;

        // Feature map 1: 16x16, 2アンカー/セル
        int fmSize = 16;
        for (int y = 0; y < fmSize; y++)
        for (int x = 0; x < fmSize; x++)
        for (int a = 0; a < 2; a++)
        {
            anchors[idx, 0] = (x + 0.5f) / fmSize;
            anchors[idx, 1] = (y + 0.5f) / fmSize;
            idx++;
        }

        // Feature map 2: 8x8, 6アンカー/セル
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
    // 検出デコード
    // -----------------------------------------------------------------------

    List<Detection> DecodeDetections(float[] scoresData, float[] boxesData)
    {
        const float inputSize = 128f;
        var result = new List<Detection>();

        for (int i = 0; i < 896; i++)
        {
            float rawScore = scoresData[i];
            float score    = 1f / (1f + Mathf.Exp(-rawScore)); // sigmoid

            if (score < scoreThreshold) continue;

            // アンカーオフセットから実座標に変換 (ピクセル空間 0~128)
            float xCenter = boxesData[i * 16 + 0] + _anchors[i, 0] * inputSize;
            float yCenter = boxesData[i * 16 + 1] + _anchors[i, 1] * inputSize;
            float wHalf   = boxesData[i * 16 + 2] * 0.5f;
            float hHalf   = boxesData[i * 16 + 3] * 0.5f;

            // [0,1] 正規化
            result.Add(new Detection
            {
                xMin  = Mathf.Clamp01((xCenter - wHalf) / inputSize),
                yMin  = Mathf.Clamp01((yCenter - hHalf) / inputSize),
                xMax  = Mathf.Clamp01((xCenter + wHalf) / inputSize),
                yMax  = Mathf.Clamp01((yCenter + hHalf) / inputSize),
                score = score
            });
        }

        // スコア降順でソート
        result.Sort((a, b) => b.score.CompareTo(a.score));
        return result;
    }

    // -----------------------------------------------------------------------
    // Greedy NMS
    // -----------------------------------------------------------------------

    List<Detection> SimpleNMS(List<Detection> detections)
    {
        var kept = new List<Detection>();

        foreach (var d in detections)
        {
            bool suppressed = false;
            foreach (var k in kept)
            {
                if (IoU(d, k) > iouThreshold) { suppressed = true; break; }
            }
            if (!suppressed) kept.Add(d);
        }

        return kept;
    }

    static float IoU(Detection a, Detection b)
    {
        float ix1 = Mathf.Max(a.xMin, b.xMin);
        float iy1 = Mathf.Max(a.yMin, b.yMin);
        float ix2 = Mathf.Min(a.xMax, b.xMax);
        float iy2 = Mathf.Min(a.yMax, b.yMax);

        float inter = Mathf.Max(0, ix2 - ix1) * Mathf.Max(0, iy2 - iy1);
        if (inter == 0) return 0;

        float aArea = (a.xMax - a.xMin) * (a.yMax - a.yMin);
        float bArea = (b.xMax - b.xMin) * (b.yMax - b.yMin);
        return inter / (aArea + bArea - inter);
    }

    // -----------------------------------------------------------------------
    // Canvas バウンディングボックス描画
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
            var go   = new GameObject("BBox");
            go.transform.SetParent(overlayContainer, false);

            var rect  = go.AddComponent<RectTransform>();
            var image = go.AddComponent<Image>();

            image.color = new Color(0f, 1f, 0f, 0.35f); // 半透明の緑

            // overlayContainer を基準に anchor で位置指定
            // モデルは Y 上向きが 0 なので、Unity UI に合わせて Y を反転
            rect.anchorMin = new Vector2(d.xMin, 1f - d.yMax);
            rect.anchorMax = new Vector2(d.xMax, 1f - d.yMin);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            _activeBoxes.Add(go);
        }
    }
}
