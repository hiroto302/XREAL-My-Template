using System.Collections.Generic;
using System.Globalization;
using Unity.Sentis;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// hand_detector.onnx を静止画でテストするスクリプト。
/// FaceDetectionTest.cs と同じ構成で、手検出モデルが正しく動作するかを確認する。
///
/// [必要なファイル]
///   Assets/Models/hand_detector.onnx
///   Assets/Data/anchors.csv  (2016行, 4列: cx,cy,w,h 正規化値)
///
/// [Scene セットアップ]
///   Canvas
///     ├─ RawImage-Preview      → displayImage
///     │    └─ OverlayContainer → overlayContainer
///     └─ Text-Status           → statusText
///
///   GameObjectに本スクリプトをアタッチし Inspector で各フィールドを設定する。
/// </summary>
public class HandDetectionTest : MonoBehaviour
{
    [Header("Sentis")]
    [SerializeField] private ModelAsset modelAsset;
    [SerializeField] private TextAsset  anchorsCSV;

    [Header("UI")]
    [SerializeField] private Texture2D    testImage;
    [SerializeField] private RawImage     displayImage;
    [SerializeField] private RectTransform overlayContainer;
    [SerializeField] private Text         statusText;

    [Header("Detection Settings")]
    [SerializeField] private float scoreThreshold = 0.5f;
    [SerializeField] private float iouThreshold   = 0.3f;

    // --- 定数 ---
    private const int InputSize  = 192;
    private const int NumAnchors = 2016;
    private const int BoxStride  = 18; // 1アンカーあたりのbox値数

    // --- 内部状態 ---
    private IWorker  _worker;
    private float[,] _anchors; // [2016, 4] : cx, cy, w, h (正規化済み)

    private string _scoreOutputName;
    private string _boxOutputName;

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

        // ★ デバッグ: 出力テンソル名を確認
        for (int i = 0; i < model.outputs.Count; i++)
            Debug.Log($"[HandTest] output[{i}] name={model.outputs[i].name}");

        // HandDetectionLive2D.cs で確認済み: index 0 = scores, index 1 = boxes
        _scoreOutputName = model.outputs[0].name;
        _boxOutputName   = model.outputs[1].name;

        _worker  = WorkerFactory.CreateWorker(BackendType.GPUCompute, model);
        _anchors = LoadAnchors(anchorsCSV.text, NumAnchors);

        displayImage.texture = testImage;
        if (statusText != null) statusText.text = "Model loaded. Running...";

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

        var rawScores = _worker.PeekOutput(_scoreOutputName) as TensorFloat;
        var rawBoxes  = _worker.PeekOutput(_boxOutputName)   as TensorFloat;

        rawScores.CompleteOperationsAndDownload();
        rawBoxes.CompleteOperationsAndDownload();

        float[] scoresData = rawScores.ToReadOnlyArray();
        float[] boxesData  = rawBoxes.ToReadOnlyArray();

        // ★ デバッグ: データ長とスコア生値を確認
        Debug.Log($"[HandTest] scoresData.Length={scoresData.Length} (期待値:{NumAnchors}), boxesData.Length={boxesData.Length} (期待値:{NumAnchors * BoxStride})");
        if (scoresData.Length >= 5)
            Debug.Log($"[HandTest] scoresData[0..4] = {scoresData[0]:F3}, {scoresData[1]:F3}, {scoresData[2]:F3}, {scoresData[3]:F3}, {scoresData[4]:F3}");

        var detections = DecodeDetections(scoresData, boxesData);
        var filtered   = SimpleNMS(detections);

        ClearBoundingBoxes();
        DrawBoundingBoxes(filtered);

        string msg = $"{filtered.Count} 件検出";
        if (statusText != null) statusText.text = msg;
        Debug.Log($"[HandTest] {msg}");
    }

    // -----------------------------------------------------------------------
    // 入力前処理: 192x192 NHWC TensorFloat
    // -----------------------------------------------------------------------

    static TensorFloat PrepareInputNHWC(Texture2D tex)
    {
        var rt = RenderTexture.GetTemporary(InputSize, InputSize, 0);
        Graphics.Blit(tex, rt);
        RenderTexture.active = rt;
        var resized = new Texture2D(InputSize, InputSize, TextureFormat.RGB24, false);
        resized.ReadPixels(new Rect(0, 0, InputSize, InputSize), 0, 0);
        resized.Apply();
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);

        Color32[] pixels = resized.GetPixels32();
        Destroy(resized);

        float[] data = new float[InputSize * InputSize * 3];
        // GetPixels32() は Y=0 が下（Unity座標）。モデルは Y=0 が上なので反転。
        for (int y = 0; y < InputSize; y++)
        for (int x = 0; x < InputSize; x++)
        {
            int srcIdx = (InputSize - 1 - y) * InputSize + x;
            int dstIdx = y * InputSize + x;
            data[dstIdx * 3 + 0] = pixels[srcIdx].r / 255f;
            data[dstIdx * 3 + 1] = pixels[srcIdx].g / 255f;
            data[dstIdx * 3 + 2] = pixels[srcIdx].b / 255f;
        }

        return new TensorFloat(new TensorShape(1, InputSize, InputSize, 3), data);
    }

    // -----------------------------------------------------------------------
    // 検出デコード
    // -----------------------------------------------------------------------

    List<Detection> DecodeDetections(float[] scoresData, float[] boxesData)
    {
        var result = new List<Detection>();

        for (int i = 0; i < NumAnchors; i++)
        {
            float rawScore = scoresData[i];
            float score    = 1f / (1f + Mathf.Exp(-rawScore)); // sigmoid

            if (score < scoreThreshold) continue;

            // アンカー中心 (ピクセル空間 0〜192)
            float anchorCx = _anchors[i, 0] * InputSize;
            float anchorCy = _anchors[i, 1] * InputSize;

            // オフセットを加算して実座標を得る
            float boxCx = boxesData[i * BoxStride + 0] + anchorCx;
            float boxCy = boxesData[i * BoxStride + 1] + anchorCy;
            float boxW  = Mathf.Abs(boxesData[i * BoxStride + 2]);
            float boxH  = Mathf.Abs(boxesData[i * BoxStride + 3]);

            // [0,1] 正規化
            result.Add(new Detection
            {
                xMin  = Mathf.Clamp01((boxCx - boxW * 0.5f) / InputSize),
                yMin  = Mathf.Clamp01((boxCy - boxH * 0.5f) / InputSize),
                xMax  = Mathf.Clamp01((boxCx + boxW * 0.5f) / InputSize),
                yMax  = Mathf.Clamp01((boxCy + boxH * 0.5f) / InputSize),
                score = score
            });
        }

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
            var go    = new GameObject("HandBBox");
            go.transform.SetParent(overlayContainer, false);

            var rect  = go.AddComponent<RectTransform>();
            var image = go.AddComponent<Image>();
            image.color = new Color(0.2f, 1f, 0.4f, 0.35f); // 半透明の緑

            // モデルは Y=上が 0 → Unity UI に合わせて Y 反転
            rect.anchorMin = new Vector2(d.xMin, 1f - d.yMax);
            rect.anchorMax = new Vector2(d.xMax, 1f - d.yMin);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            _activeBoxes.Add(go);
        }
    }

    // -----------------------------------------------------------------------
    // アンカー読み込み (CSV: cx,cy,w,h 正規化値, 2016行)
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
