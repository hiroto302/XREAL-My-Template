using System.Collections.Generic;
using System.Globalization;
using Unity.Sentis;
using UnityEngine;
using UnityEngine.UI;
using Unity.XR.XREAL;

/// <summary>
/// XREAL RGBカメラのリアルタイム映像にBlazeHandを適用し、
/// 手の21キーポイントを3D空間にスケルトン表示するスクリプト。
///
/// 2ステップパイプライン:
///   Step1: hand_detector.onnx で手のBBoxを検出 (192×192入力, 2016アンカー)
///   Step2: hand_landmarks_detector.onnx で21キーポイントを推定 (224×224入力)
///
/// 注意: BlazeHandのキーポイント数は21 (33はBlazePose)。
///       モデル出力テンソル名はONNXファイルによって異なる場合があります。
///
/// [必要なファイル]
///   Assets/Models/hand_detector.onnx
///   Assets/Models/hand_landmarks_detector.onnx
///   Assets/Data/anchors.csv  (2016行, 4列: cx,cy,w,h 正規化値)
///   ↑ 全て https://github.com/Unity-Technologies/sentis-samples/tree/main/BlazeDetectionSample/Hand からDL
///
/// [Scene セットアップ]
///   - このコンポーネントをGameObjectにアタッチ
///   - Detector Model / Landmarker Model / Anchors CSV / YUV Material をアサイン
/// </summary>
public class HandDetectionLive3D : MonoBehaviour
{
    [Header("Sentis")]
    [SerializeField] private ModelAsset detectorModelAsset;
    [SerializeField] private ModelAsset landmarkerModelAsset;
    [SerializeField] private TextAsset  anchorsCSV;

    [Header("Camera")]
    [SerializeField] private Material yuvMaterial;

    [Header("UI (Optional)")]
    [SerializeField] private RawImage previewImage;
    [SerializeField] private Text     statusText;

    [Header("Detection Settings")]
    [SerializeField] private float scoreThreshold       = 0.5f;
    [SerializeField] private int   detectIntervalFrames = 5;

    [Header("Keypoint Settings")]
    [SerializeField] private float keypointSize  = 0.02f;
    [SerializeField] private Color keypointColor = new Color(0.2f, 0.6f, 1f, 0.9f);
    [SerializeField] private float lineWidth     = 0.005f;
    [SerializeField] private Color lineColor     = Color.white;

    // --- 定数 ---
    private const int   DetectorSize  = 192;
    private const int   LandmarkSize  = 224;
    private const int   NumAnchors    = 2016;
    private const int   NumKeypoints  = 21;
    // 深度推定: depth = handWidthReal * focalLen / bboxPx
    // XREAL RGB FOV≈52°, 入力192px → focal = (192/2)/tan(26°) ≈ 196px
    private const float HandWidthReal = 0.08f;   // 手のひら平均幅 8cm
    private const float FocalLengthPx = 196.0f;

    // --- BlazeHand スケルトン接続 (21キーポイント) ---
    // 0: Wrist, 1-4: Thumb, 5-8: Index, 9-12: Middle, 13-16: Ring, 17-20: Pinky
    private static readonly int[][] SkeletonConnections = new int[][]
    {
        new int[]{0,1},  new int[]{0,5},  new int[]{0,9},  new int[]{0,13}, new int[]{0,17}, // 手首→各指
        new int[]{1,2},  new int[]{2,3},  new int[]{3,4},                                    // 親指
        new int[]{5,6},  new int[]{6,7},  new int[]{7,8},                                    // 人差し指
        new int[]{9,10}, new int[]{10,11}, new int[]{11,12},                                 // 中指
        new int[]{13,14}, new int[]{14,15}, new int[]{15,16},                                // 薬指
        new int[]{17,18}, new int[]{18,19}, new int[]{19,20},                                // 小指
        new int[]{5,9},  new int[]{9,13}, new int[]{13,17}                                   // 手のひら
    };

    // --- 内部状態 ---
    private IWorker               _detectorWorker;
    private IWorker               _landmarkerWorker;
    private float[,]              _anchors;         // [2016, 4]: cx, cy, w, h (正規化値)
    private string                _detBoxOutput;    // boxes テンソル名
    private string                _detScoreOutput;  // scores テンソル名
    private string                _lmOutput;        // landmarks テンソル名

    private XREALRGBCameraTexture _cam;
    private RenderTexture         _rgbRt;
    private int                   _frameCount;

    private readonly List<GameObject> _keypointObjects = new List<GameObject>();
    private readonly List<GameObject> _lineObjects     = new List<GameObject>();
    private Material                  _keypointMat;
    private Material                  _lineMat;

    // -----------------------------------------------------------------------

    void Start()
    {
        // アンカーCSV読み込み
        _anchors = LoadAnchors(anchorsCSV.text, NumAnchors);

        // Detector モデル (outputs は List<Model.Output>、.name で名前取得)
        var detModel    = ModelLoader.Load(detectorModelAsset);
        _detBoxOutput   = detModel.outputs[0].name;  // 通常 index 0 = boxes (regressors)
        _detScoreOutput = detModel.outputs[1].name;  // 通常 index 1 = scores (classificators)
        _detectorWorker = WorkerFactory.CreateWorker(BackendType.GPUCompute, detModel);

        // Landmarker モデル
        var lmModel       = ModelLoader.Load(landmarkerModelAsset);
        _lmOutput         = lmModel.outputs[0].name; // 通常 "Identity" or 最初の出力
        _landmarkerWorker = WorkerFactory.CreateWorker(BackendType.GPUCompute, lmModel);

        // カメラ
        _cam = XREALRGBCameraTexture.CreateSingleton();
        if (!_cam.IsCapturing) _cam.StartCapture();

        // 可視化マテリアル
        _keypointMat       = new Material(Shader.Find("Sprites/Default")) { color = keypointColor };
        _lineMat           = new Material(Shader.Find("Sprites/Default")) { color = lineColor };

        if (statusText != null) statusText.text = "Camera starting...";
    }

    void OnDestroy()
    {
        _detectorWorker?.Dispose();
        _landmarkerWorker?.Dispose();
        if (_rgbRt != null) { _rgbRt.Release(); Destroy(_rgbRt); }
        if (_keypointMat != null) Destroy(_keypointMat);
        if (_lineMat     != null) Destroy(_lineMat);
        ClearVisuals();
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

        if (previewImage != null)
        {
            previewImage.texture  = yuv[0];
            yuvMaterial.SetTexture("_UTex", yuv[1]);
            yuvMaterial.SetTexture("_VTex", yuv[2]);
            previewImage.material = yuvMaterial;
        }

        if (++_frameCount % detectIntervalFrames != 0) return;

        // YUV → RGB RenderTexture
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
        // ---- 192×192 に縮小して Texture2D に読み込み ----
        var rt192 = RenderTexture.GetTemporary(DetectorSize, DetectorSize, 0);
        Graphics.Blit(rt, rt192);
        RenderTexture.active = rt192;
        var tex192 = new Texture2D(DetectorSize, DetectorSize, TextureFormat.RGB24, false);
        tex192.ReadPixels(new Rect(0, 0, DetectorSize, DetectorSize), 0, 0);
        tex192.Apply();
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt192);

        try
        {
            RunDetectionAndLandmark(tex192);
        }
        finally
        {
            Destroy(tex192);
        }
    }

    void RunDetectionAndLandmark(Texture2D tex192)
    {
        // ---- Step 1: 手の検出 (192×192) ----
        using var detInput = TextureToNHWC(tex192, DetectorSize);
        _detectorWorker.Execute(detInput);

        var rawBoxes  = _detectorWorker.PeekOutput(_detBoxOutput)   as TensorFloat;
        var rawScores = _detectorWorker.PeekOutput(_detScoreOutput) as TensorFloat;
        rawBoxes.CompleteOperationsAndDownload();
        rawScores.CompleteOperationsAndDownload();

        float[] boxesData  = rawBoxes.ToReadOnlyArray();
        float[] scoresData = rawScores.ToReadOnlyArray();

        // ArgMax: シグモイド後の最大スコアアンカーを選択
        int   bestIdx   = -1;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < NumAnchors; i++)
        {
            float s = 1f / (1f + Mathf.Exp(-scoresData[i]));
            if (s > bestScore) { bestScore = s; bestIdx = i; }
        }

        ClearVisuals();

        if (bestScore < scoreThreshold || bestIdx < 0)
        {
            if (statusText != null) statusText.text = "Hand: not detected";
            return;
        }

        // ---- Bbox デコード (detector tensor空間 0〜192) ----
        float anchorCx = _anchors[bestIdx, 0] * DetectorSize;
        float anchorCy = _anchors[bestIdx, 1] * DetectorSize;

        float boxCx = boxesData[bestIdx * 18 + 0] + anchorCx;
        float boxCy = boxesData[bestIdx * 18 + 1] + anchorCy;
        float boxW  = Mathf.Abs(boxesData[bestIdx * 18 + 2]);
        float boxH  = Mathf.Abs(boxesData[bestIdx * 18 + 3]);
        float boxSize = Mathf.Max(boxW, boxH);

        // パームキーポイント: kp0(手首), kp2(中指根元) で手の向き推定
        // ボックス内オフセット: box[4+0,4+1] = kp0, box[4+4,4+5] = kp2
        float kp0x = boxesData[bestIdx * 18 + 4] + anchorCx;
        float kp0y = boxesData[bestIdx * 18 + 5] + anchorCy;
        float kp2x = boxesData[bestIdx * 18 + 8] + anchorCx;
        float kp2y = boxesData[bestIdx * 18 + 9] + anchorCy;

        float dx  = kp2x - kp0x;
        float dy  = kp2y - kp0y;
        float len = Mathf.Sqrt(dx * dx + dy * dy);
        float upX = len > 1e-6f ? dx / len : 0f;
        float upY = len > 1e-6f ? dy / len : -1f;

        // BlazeHand 標準: 回転 = π/2 - atan2(delta.y, delta.x)
        float rotation = Mathf.PI * 0.5f - Mathf.Atan2(dy, dx);

        // ボックス中心を手方向にシフト & 2.6倍に拡大（手全体を含むよう）
        boxCx   += 0.5f * boxSize * upX;
        boxCy   += 0.5f * boxSize * upY;
        boxSize *= 2.6f;

        // ---- Step 2: ランドマーク推定 (224×224 アフィンクロップ) ----
        using var lmInput = AffineCropNHWC(tex192, new Vector2(boxCx, boxCy), boxSize, rotation);
        _landmarkerWorker.Execute(lmInput);

        var rawLm = _landmarkerWorker.PeekOutput(_lmOutput) as TensorFloat;
        rawLm.CompleteOperationsAndDownload();
        float[] lmData = rawLm.ToReadOnlyArray();

        // キーポイントを3Dワールド座標に変換
        float depth       = EstimateDepth(boxW / DetectorSize);
        var   worldPoints = new Vector3[NumKeypoints];

        for (int i = 0; i < NumKeypoints; i++)
        {
            float lx = lmData[i * 3 + 0]; // 0〜224 クロップ空間
            float ly = lmData[i * 3 + 1];

            // クロップ空間 → 192×192 検出器空間
            Vector2 detPt = CropToDetectorSpace(new Vector2(lx, ly),
                                                 new Vector2(boxCx, boxCy), boxSize, rotation);
            // 検出器空間(0〜192) → Viewport 正規化(0〜1)
            float nx = detPt.x / DetectorSize;
            float ny = 1f - detPt.y / DetectorSize; // Y反転（UnityのViewport座標）

            worldPoints[i] = Camera.main.ViewportToWorldPoint(new Vector3(nx, ny, depth));
        }

        DrawVisuals(worldPoints);
        if (statusText != null) statusText.text = $"Hand detected  score={bestScore:F2}";
    }

    // -----------------------------------------------------------------------
    // 前処理ユーティリティ
    // -----------------------------------------------------------------------

    /// <summary>Texture2D → NHWC TensorFloat。GetPixels32()はY=0が下なので反転。</summary>
    static TensorFloat TextureToNHWC(Texture2D tex, int size)
    {
        Color32[] pixels = tex.GetPixels32();
        float[]   data   = new float[size * size * 3];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int srcIdx = (size - 1 - y) * size + x; // Unity: Y=0が下
            int dstIdx = y * size + x;
            data[dstIdx * 3 + 0] = pixels[srcIdx].r / 255f;
            data[dstIdx * 3 + 1] = pixels[srcIdx].g / 255f;
            data[dstIdx * 3 + 2] = pixels[srcIdx].b / 255f;
        }
        return new TensorFloat(new TensorShape(1, size, size, 3), data);
    }

    /// <summary>
    /// 192×192 検出器空間から回転補正アフィンクロップして 224×224 NHWC テンソルを生成。
    ///
    /// アフィン変換 (crop → detector):
    ///   T(centre) * S(scale, -scale) * R(rotation) * T(-112, -112)
    ///   = S(scale, -scale) * R * (cropPx - 112) + centre
    ///   srcX = scale*(cos*(cx) - sin*(cy)) + centre.x
    ///   srcY = -scale*(sin*(cx) + cos*(cy)) + centre.y   ← Yフリップ
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

            // アフィン: detector空間のサンプル座標
            float srcX =  scale * (cos * cx - sin * cy) + centre.x;
            float srcY = -scale * (sin * cx + cos * cy) + centre.y; // Yフリップ

            // GetPixelBilinear の UV: X は左→右、Y は下→上（Unity座標）
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
        // Yフリップを元に戻す: srcY = -scale*ry + centre.y → ry に -1
        return new Vector2(scale * rx + centre.x, -scale * ry + centre.y);
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

    // -----------------------------------------------------------------------
    // 深度推定 (ピンホールカメラモデル)
    // -----------------------------------------------------------------------

    static float EstimateDepth(float bboxWidthNorm)
    {
        float bboxPx = bboxWidthNorm * DetectorSize;
        if (bboxPx < 1f) return 1.0f;
        return HandWidthReal * FocalLengthPx / bboxPx;
    }

    // -----------------------------------------------------------------------
    // 3D可視化: スケルトンマーカー
    // -----------------------------------------------------------------------

    void ClearVisuals()
    {
        foreach (var go in _keypointObjects) Destroy(go);
        _keypointObjects.Clear();
        foreach (var go in _lineObjects) Destroy(go);
        _lineObjects.Clear();
    }

    void DrawVisuals(Vector3[] points)
    {
        // キーポイント球体
        foreach (var pt in points)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "HandKeypoint";
            Destroy(go.GetComponent<SphereCollider>());
            go.transform.position   = pt;
            go.transform.localScale = Vector3.one * keypointSize;
            go.GetComponent<MeshRenderer>().material = _keypointMat;
            _keypointObjects.Add(go);
        }

        // スケルトン線
        foreach (int[] conn in SkeletonConnections)
        {
            var go = new GameObject("HandBone");
            var lr = go.AddComponent<LineRenderer>();
            lr.material      = _lineMat;
            lr.startWidth    = lineWidth;
            lr.endWidth      = lineWidth;
            lr.positionCount = 2;
            lr.useWorldSpace = true;
            lr.SetPosition(0, points[conn[0]]);
            lr.SetPosition(1, points[conn[1]]);
            _lineObjects.Add(go);
        }
    }
}
