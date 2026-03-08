using System.Collections.Generic;
using System.Globalization;
using Unity.Sentis;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 静止画で手の21キーポイントを検出してCanvas上に表示するテストスクリプト。
///
/// 2ステップパイプライン:
///   Step1: hand_detector.onnx (192×192) → 手のBBox + 回転角を取得
///   Step2: hand_landmarks_detector.onnx (224×224) → 21キーポイントを取得
///   → 2D Canvas上にドット(keypoint)と線(skeleton)を描画
///
/// HandDetectionLive3D.cs の3D版を、エディタで確認しやすい2D Canvas版に変換したもの。
///
/// [必要なファイル]
///   Assets/Models/hand_detector.onnx
///   Assets/Models/hand_landmarks_detector.onnx
///   Assets/Data/anchors.csv  (2016行, 4列: cx,cy,w,h 正規化値)
///
/// [Scene セットアップ]
///   Canvas
///     └─ RawImage-Display      → displayImage
///          └─ OverlayContainer → overlayContainer (ドット・線の描画先)
///   Text-Status                → statusText
/// </summary>
public class HandLandmarkTest : MonoBehaviour
{
    [Header("Sentis")]
    [SerializeField] private ModelAsset detectorModelAsset;
    [SerializeField] private ModelAsset landmarkerModelAsset;
    [SerializeField] private TextAsset  anchorsCSV;

    [Header("UI")]
    [SerializeField] private Texture2D     testImage;
    [SerializeField] private RawImage      displayImage;
    [SerializeField] private RectTransform overlayContainer;
    [SerializeField] private Text          statusText;

    [Header("Detection Settings")]
    [SerializeField] private float scoreThreshold = 0.5f;

    [Header("Visualization")]
    [SerializeField] private Color keypointColor = new Color(0.2f, 0.6f, 1f, 1f);
    [SerializeField] private Color lineColor     = new Color(1f, 1f, 1f, 0.9f);
    [SerializeField] private float dotSize       = 12f; // ドットのピクセルサイズ
    [SerializeField] private float lineThickness = 4f;  // 線の太さ（ピクセル）

    // --- 定数 ---
    private const int DetectorSize = 192;
    private const int LandmarkSize = 224;
    private const int NumAnchors   = 2016;
    private const int BoxStride    = 18;
    private const int NumKeypoints = 21;

    // BlazeHand スケルトン接続 (21キーポイント)
    // 0: Wrist, 1-4: Thumb, 5-8: Index, 9-12: Middle, 13-16: Ring, 17-20: Pinky
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

    // --- 内部状態 ---
    private IWorker  _detectorWorker;
    private IWorker  _landmarkerWorker;
    private float[,] _anchors;
    private string   _detScoreOutput;
    private string   _detBoxOutput;
    private string   _lmOutput;

    private readonly List<GameObject> _uiObjects = new List<GameObject>();

    // -----------------------------------------------------------------------

    void Start()
    {
        // モデルロード
        var detModel    = ModelLoader.Load(detectorModelAsset);
        var lmModel     = ModelLoader.Load(landmarkerModelAsset);

        // ★ デバッグ: 出力テンソル名確認
        for (int i = 0; i < detModel.outputs.Count; i++)
            Debug.Log($"[HandLM] detector output[{i}] name={detModel.outputs[i].name}");
        for (int i = 0; i < lmModel.outputs.Count; i++)
            Debug.Log($"[HandLM] landmarker output[{i}] name={lmModel.outputs[i].name}");

        _detScoreOutput   = detModel.outputs[0].name; // index 0 = scores
        _detBoxOutput     = detModel.outputs[1].name; // index 1 = boxes
        _lmOutput         = lmModel.outputs[0].name;  // 最初の出力 = landmarks

        _detectorWorker   = WorkerFactory.CreateWorker(BackendType.GPUCompute, detModel);
        _landmarkerWorker = WorkerFactory.CreateWorker(BackendType.GPUCompute, lmModel);
        _anchors          = LoadAnchors(anchorsCSV.text, NumAnchors);

        displayImage.texture = testImage;
        if (statusText != null) statusText.text = "Running...";

        Detect(testImage);
    }

    void OnDestroy()
    {
        _detectorWorker?.Dispose();
        _landmarkerWorker?.Dispose();
        ClearUI();
    }

    // -----------------------------------------------------------------------
    // メイン検出フロー
    // -----------------------------------------------------------------------

    void Detect(Texture2D tex)
    {
        // Step1: 192×192 に縮小してTensorへ
        using var detInput = PrepareInputNHWC(tex, DetectorSize);
        _detectorWorker.Execute(detInput);

        var rawScores = _detectorWorker.PeekOutput(_detScoreOutput) as TensorFloat;
        var rawBoxes  = _detectorWorker.PeekOutput(_detBoxOutput)   as TensorFloat;
        rawScores.CompleteOperationsAndDownload();
        rawBoxes.CompleteOperationsAndDownload();

        float[] scoresData = rawScores.ToReadOnlyArray();
        float[] boxesData  = rawBoxes.ToReadOnlyArray();

        Debug.Log($"[HandLM] scoresData.Length={scoresData.Length}, boxesData.Length={boxesData.Length}");

        // ArgMax でベストアンカーを選択
        int   bestIdx   = -1;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < NumAnchors; i++)
        {
            float s = 1f / (1f + Mathf.Exp(-scoresData[i]));
            if (s > bestScore) { bestScore = s; bestIdx = i; }
        }

        ClearUI();

        if (bestScore < scoreThreshold || bestIdx < 0)
        {
            if (statusText != null) statusText.text = "Hand: not detected";
            Debug.Log($"[HandLM] Not detected. bestScore={bestScore:F3}");
            return;
        }

        Debug.Log($"[HandLM] Hand detected. bestScore={bestScore:F3}, bestIdx={bestIdx}");

        // BBox デコード（検出器ピクセル空間 0〜192）
        float anchorCx = _anchors[bestIdx, 0] * DetectorSize;
        float anchorCy = _anchors[bestIdx, 1] * DetectorSize;

        float boxCx   = boxesData[bestIdx * BoxStride + 0] + anchorCx;
        float boxCy   = boxesData[bestIdx * BoxStride + 1] + anchorCy;
        float boxW    = Mathf.Abs(boxesData[bestIdx * BoxStride + 2]);
        float boxH    = Mathf.Abs(boxesData[bestIdx * BoxStride + 3]);
        float boxSize = Mathf.Max(boxW, boxH);

        // パームキーポイントから手の向き(回転角)を推定
        // kp0=手首(index4,5), kp2=中指根元(index8,9)
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

        // BlazeHand 標準: boxCenterを手方向にシフト & 2.6倍に拡大
        boxCx   += 0.5f * boxSize * upX;
        boxCy   += 0.5f * boxSize * upY;
        boxSize *= 2.6f;

        // Step2: アフィンクロップして224×224 NHWC に変換
        // テスト用に192×192に縮小したTexture2Dが必要
        var tex192 = ResizeTexture(tex, DetectorSize);
        try
        {
            using var lmInput = AffineCropNHWC(tex192, new Vector2(boxCx, boxCy), boxSize, rotation);
            _landmarkerWorker.Execute(lmInput);

            var rawLm = _landmarkerWorker.PeekOutput(_lmOutput) as TensorFloat;
            rawLm.CompleteOperationsAndDownload();
            float[] lmData = rawLm.ToReadOnlyArray();

            Debug.Log($"[HandLM] lmData.Length={lmData.Length} (期待値:{NumKeypoints * 3}以上)");

            // キーポイントをCanvas正規化座標(0〜1)に変換
            var points2D = new Vector2[NumKeypoints];
            for (int i = 0; i < NumKeypoints; i++)
            {
                float lx = lmData[i * 3 + 0]; // 0〜224 クロップ空間
                float ly = lmData[i * 3 + 1];

                // クロップ空間 → 192×192 検出器空間
                Vector2 detPt = CropToDetectorSpace(new Vector2(lx, ly),
                                                     new Vector2(boxCx, boxCy), boxSize, rotation);
                // 検出器空間(0〜192) → 正規化(0〜1)
                // Canvas は Y=下が0、モデルは Y=上が0 なので Y 反転
                points2D[i] = new Vector2(
                    detPt.x / DetectorSize,
                    1f - detPt.y / DetectorSize
                );
            }

            DrawSkeleton(points2D);
            DrawKeypoints(points2D);

            if (statusText != null) statusText.text = $"Hand detected  score={bestScore:F2}";
        }
        finally
        {
            Destroy(tex192);
        }
    }

    // -----------------------------------------------------------------------
    // 前処理ユーティリティ
    // -----------------------------------------------------------------------

    static Texture2D ResizeTexture(Texture2D src, int size)
    {
        var rt = RenderTexture.GetTemporary(size, size, 0);
        Graphics.Blit(src, rt);
        RenderTexture.active = rt;
        var result = new Texture2D(size, size, TextureFormat.RGB24, false);
        result.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        result.Apply();
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);
        return result;
    }

    static TensorFloat PrepareInputNHWC(Texture2D tex, int size)
    {
        var tex2 = ResizeTexture(tex, size);
        Color32[] pixels = tex2.GetPixels32();
        Destroy(tex2);

        float[] data = new float[size * size * 3];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int srcIdx = (size - 1 - y) * size + x; // Y反転
            int dstIdx = y * size + x;
            data[dstIdx * 3 + 0] = pixels[srcIdx].r / 255f;
            data[dstIdx * 3 + 1] = pixels[srcIdx].g / 255f;
            data[dstIdx * 3 + 2] = pixels[srcIdx].b / 255f;
        }
        return new TensorFloat(new TensorShape(1, size, size, 3), data);
    }

    /// <summary>
    /// 192×192 検出器空間から手の向きに合わせて回転クロップし、224×224 NHWC テンソルを生成。
    /// HandDetectionLive3D.cs の AffineCropNHWC と同じアルゴリズム。
    /// </summary>
    static TensorFloat AffineCropNHWC(Texture2D src192, Vector2 centre, float boxSize, float rotation)
    {
        float scale = boxSize / LandmarkSize;
        float cos   = Mathf.Cos(rotation);
        float sin   = Mathf.Sin(rotation);
        float half  = LandmarkSize * 0.5f;

        float[] data = new float[LandmarkSize * LandmarkSize * 3];
        for (int y = 0; y < LandmarkSize; y++)
        for (int x = 0; x < LandmarkSize; x++)
        {
            float cx = x - half;
            float cy = y - half;

            float srcX =  scale * (cos * cx - sin * cy) + centre.x;
            float srcY = -scale * (sin * cx + cos * cy) + centre.y;

            float u =       srcX / DetectorSize;
            float v = 1f - (srcY / DetectorSize);

            Color c   = src192.GetPixelBilinear(u, v);
            int   idx = (y * LandmarkSize + x) * 3;
            data[idx + 0] = c.r;
            data[idx + 1] = c.g;
            data[idx + 2] = c.b;
        }
        return new TensorFloat(new TensorShape(1, LandmarkSize, LandmarkSize, 3), data);
    }

    /// <summary>224×224 クロップ空間の点を 192×192 検出器空間に逆変換。</summary>
    static Vector2 CropToDetectorSpace(Vector2 cropPt, Vector2 centre, float boxSize, float rotation)
    {
        float scale = boxSize / LandmarkSize;
        float cos   = Mathf.Cos(rotation);
        float sin   = Mathf.Sin(rotation);
        float cx    = cropPt.x - LandmarkSize * 0.5f;
        float cy    = cropPt.y - LandmarkSize * 0.5f;

        float rx = cos * cx - sin * cy;
        float ry = sin * cx + cos * cy;
        return new Vector2(scale * rx + centre.x, -scale * ry + centre.y);
    }

    // -----------------------------------------------------------------------
    // Canvas 2D 描画
    // -----------------------------------------------------------------------

    void ClearUI()
    {
        foreach (var go in _uiObjects) Destroy(go);
        _uiObjects.Clear();
    }

    /// <summary>キーポイントをドットで描画（正規化座標 0〜1）。</summary>
    void DrawKeypoints(Vector2[] points)
    {
        foreach (var p in points)
        {
            var go    = new GameObject("Keypoint");
            go.transform.SetParent(overlayContainer, false);
            var rect  = go.AddComponent<RectTransform>();
            var img   = go.AddComponent<Image>();
            img.color = keypointColor;

            // anchorにポイントを置いて固定サイズで描画
            rect.anchorMin        = p;
            rect.anchorMax        = p;
            rect.pivot            = new Vector2(0.5f, 0.5f);
            rect.sizeDelta        = new Vector2(dotSize, dotSize);
            rect.anchoredPosition = Vector2.zero;

            _uiObjects.Add(go);
        }
    }

    /// <summary>スケルトン接続を細い矩形（回転済み）で描画。</summary>
    void DrawSkeleton(Vector2[] points)
    {
        // overlayContainer の実サイズを取得（ピクセル変換に使用）
        Vector2 containerSize = overlayContainer.rect.size;

        foreach (var conn in SkeletonConnections)
        {
            Vector2 a = points[conn[0]];
            Vector2 b = points[conn[1]];

            // 正規化座標 → ピクセル座標 (anchorMin/Max ではなくanchoredPositionを使う)
            Vector2 aPx = new Vector2(a.x * containerSize.x, a.y * containerSize.y);
            Vector2 bPx = new Vector2(b.x * containerSize.x, b.y * containerSize.y);

            Vector2 mid    = (aPx + bPx) * 0.5f;
            float   dist   = Vector2.Distance(aPx, bPx);
            float   angle  = Mathf.Atan2(bPx.y - aPx.y, bPx.x - aPx.x) * Mathf.Rad2Deg;

            var go   = new GameObject("Bone");
            go.transform.SetParent(overlayContainer, false);
            var rect = go.AddComponent<RectTransform>();
            var img  = go.AddComponent<Image>();
            img.color = lineColor;

            // 左下基準のanchor(0,0)を使い、anchoredPositionで配置
            rect.anchorMin        = Vector2.zero;
            rect.anchorMax        = Vector2.zero;
            rect.pivot            = new Vector2(0.5f, 0.5f);
            rect.sizeDelta        = new Vector2(dist, lineThickness);
            rect.anchoredPosition = mid;
            rect.localRotation    = Quaternion.Euler(0f, 0f, angle);

            _uiObjects.Add(go);
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
