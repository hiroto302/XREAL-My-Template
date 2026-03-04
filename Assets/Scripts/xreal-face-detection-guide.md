# XREAL ONE + Eye 顔検出 AR 開発ガイド

Web版 MediaPipe Face Detector デモの技術を XREAL ONE + Eye グラス上で動作する AR アプリケーションに移植するための開発ガイド。

---

## 1. 処理フローの全体図

### 1.1 エンドツーエンド パイプライン

```
┌─────────────────────────────────────────────────────────────────────┐
│                      XREAL ONE + Eye グラス                         │
│                                                                     │
│  ┌──────────┐    ┌──────────────┐    ┌─────────────────────────┐   │
│  │ Eye カメラ │───▶│ カメラフレーム │───▶│ MediaPipe Face Detector │   │
│  │ (RGB)     │    │ (Texture2D)  │    │ (BlazeFace Short Range) │   │
│  └──────────┘    └──────────────┘    └────────┬────────────────┘   │
│                                                │                    │
│                                    検出結果     │                    │
│                                  (BBox + KP)   ▼                    │
│                                      ┌─────────────────┐           │
│                                      │  座標変換        │           │
│                                      │  画像座標 → AR空間 │          │
│                                      └────────┬────────┘           │
│                                               │                    │
│                                               ▼                    │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │                    Unity AR 描画                            │    │
│  │  ┌──────────┐  ┌──────────────┐  ┌────────────────────┐   │    │
│  │  │ BBox描画  │  │ キーポイント  │  │ 信頼度ラベル       │   │    │
│  │  │ (LineLoop)│  │ (Sphere)     │  │ (TextMeshPro)      │   │    │
│  │  └──────────┘  └──────────────┘  └────────────────────┘   │    │
│  └────────────────────────────────────────────────────────────┘    │
│                           │                                        │
│                           ▼                                        │
│                  ┌─────────────────┐                               │
│                  │  AR オーバーレイ  │                               │
│                  │  (透過ディスプレイ)│                               │
│                  └─────────────────┘                               │
└─────────────────────────────────────────────────────────────────────┘
```

### 1.2 フレーム単位の処理シーケンス

```
  Eye カメラ         MediaPipe            Unity
     │                  │                   │
     │  フレーム取得     │                   │
     │─────────────────▶│                   │
     │                  │                   │
     │                  │ 推論 (2-5ms GPU)  │
     │                  │──────┐            │
     │                  │      │            │
     │                  │◀─────┘            │
     │                  │                   │
     │                  │  検出結果          │
     │                  │──────────────────▶│
     │                  │                   │
     │                  │                   │ 座標変換 + 描画
     │                  │                   │──────┐
     │                  │                   │      │
     │                  │                   │◀─────┘
     │                  │                   │
     │                  │                   │ AR表示
     │                  │                   │──▶ ユーザー
```

### 1.3 Eye カメラの仕様

| 項目 | 値 |
|------|-----|
| センサー | RGB カメラ（グラスフレーム前面） |
| 解像度 | 640x480 (VGA) ※SDK設定による |
| FPS | 30fps (デフォルト) |
| FOV | 約 52° |
| 用途 | 6DoF トラッキング / シーン認識 |

> **注意**: Eye カメラは装着者の視点方向を撮影するカメラであり、アイトラッキング用のカメラとは異なる。XREAL ONE + Eye の「Eye」はアイトラッキング機能を指すが、顔検出にはフロントカメラ（RGBカメラ）を使用する。

---

## 2. ステップバイステップ 開発手順

### Step 0: 前提条件の確認

- **ハードウェア**: XREAL ONE + Eye グラス、対応 Android スマートフォン (USB-C)
- **開発環境**: Unity 2022.3 LTS 以上 (Android Build Support)
- **SDK**: NRSDK 2.x (XREAL 公式 Unity SDK)
- **ライブラリ**: MediaPipe Unity Plugin または NuGet パッケージ

### Step 1: Unity プロジェクトのセットアップ

```
1. Unity Hub で新規3Dプロジェクトを作成
2. Build Settings → Android に切り替え
3. Player Settings で以下を設定:
   - Minimum API Level: Android 10 (API 29)
   - Scripting Backend: IL2CPP
   - Target Architectures: ARM64
   - Graphics APIs: OpenGLES3 (Vulkan は NRSDK 非対応の場合あり)
```

### Step 2: NRSDK のインポートと設定

```
1. NRSDK .unitypackage をインポート
2. NRCameraRig プレハブをシーンに配置
3. Main Camera を削除 (NRCameraRig が代替)
4. NRSessionConfig:
   - Tracking Type: Tracking6Dof
   - Plane Finding Mode: DISABLE (顔検出のみなので不要)
```

### Step 3: RGB カメラフレームの取得

```csharp
// NRRGBCamTexture を使ってカメラフレームを取得
using NRKernal;

public class FaceCameraProvider : MonoBehaviour
{
    private NRRGBCamTexture rgbCamTexture;

    void Start()
    {
        rgbCamTexture = new NRRGBCamTexture();
        rgbCamTexture.Play();
    }

    // 毎フレーム最新のテクスチャを取得
    public Texture2D GetCurrentFrame()
    {
        return rgbCamTexture.GetTexture();
    }

    void OnDestroy()
    {
        rgbCamTexture?.Stop();
    }
}
```

### Step 4: MediaPipe Face Detector の統合

**方法 A: MediaPipe Unity Plugin (推奨)**

```csharp
using Mediapipe;
using Mediapipe.Tasks.Vision;

public class FaceDetectorManager : MonoBehaviour
{
    private FaceDetector detector;

    async void Start()
    {
        var options = new FaceDetectorOptions
        {
            baseOptions = new BaseOptions
            {
                modelAssetPath = "blaze_face_short_range.tflite",
                // GPU Delegate で高速化
                delegateCase = BaseOptions.Delegate.GPU
            },
            runningMode = RunningMode.VIDEO,
            minDetectionConfidence = 0.5f,
            numFaces = 5
        };

        detector = await FaceDetector.CreateAsync(options);
    }

    public FaceDetectorResult Detect(Texture2D frame, long timestampMs)
    {
        var image = new Image(frame);
        return detector.DetectForVideo(image, timestampMs);
    }
}
```

**方法 B: TensorFlow Lite 直接利用**

MediaPipe Unity Plugin が使えない場合、BlazeFace モデルを TFLite で直接推論する。

```
1. blaze_face_short_range.tflite をStreamingAssetsに配置
2. Unity Barracuda または TFLite C# バインディングで推論
3. 出力テンソルを手動でパース (BBox + keypoints)
```

### Step 5: 座標変換 (画像座標 → AR 空間)

```csharp
public class CoordinateConverter : MonoBehaviour
{
    [SerializeField] private Camera arCamera;

    /// <summary>
    /// MediaPipe の正規化座標 [0,1] を AR 空間のワールド座標に変換
    /// </summary>
    public Vector3 ImageToWorldPosition(
        float normalizedX,
        float normalizedY,
        float estimatedDepth = 1.5f)  // カメラからの推定距離 (m)
    {
        // MediaPipe は左上原点 → Unity スクリーン座標に変換
        float screenX = normalizedX * Screen.width;
        float screenY = (1f - normalizedY) * Screen.height;

        // スクリーン座標 → ワールド座標
        Vector3 screenPoint = new Vector3(screenX, screenY, estimatedDepth);
        return arCamera.ScreenToWorldPoint(screenPoint);
    }

    /// <summary>
    /// バウンディングボックスのサイズからおおよその距離を推定
    /// </summary>
    public float EstimateDepth(float bboxHeightNormalized)
    {
        // 平均的な顔の高さ ≈ 0.23m
        // カメラFOV = 52°, 解像度 = 480px
        float faceHeightMeters = 0.23f;
        float fovRad = 52f * Mathf.Deg2Rad;
        float focalLength = 0.5f / Mathf.Tan(fovRad / 2f);

        return (faceHeightMeters * focalLength) / bboxHeightNormalized;
    }
}
```

### Step 6: AR オーバーレイ描画

```csharp
public class ARFaceOverlay : MonoBehaviour
{
    [SerializeField] private GameObject bboxPrefab;      // LineRenderer付き
    [SerializeField] private GameObject keypointPrefab;   // 小球体
    [SerializeField] private GameObject labelPrefab;      // TextMeshPro

    private CoordinateConverter converter;
    private List<GameObject> activeOverlays = new();

    public void UpdateOverlays(FaceDetectorResult result)
    {
        ClearOverlays();

        foreach (var detection in result.detections)
        {
            float score = detection.categories[0].score;
            if (score < 0.5f) continue;

            var bbox = detection.boundingBox;
            float depth = converter.EstimateDepth(bbox.height);

            // バウンディングボックス
            DrawBoundingBox(bbox, depth);

            // キーポイント (6点: 右目, 左目, 鼻先, 口, 右耳, 左耳)
            foreach (var kp in detection.keypoints)
            {
                DrawKeypoint(kp.x, kp.y, depth);
            }

            // 信頼度ラベル
            DrawLabel(bbox, depth, score);
        }
    }
}
```

### Step 7: パフォーマンスチューニング

```
1. カメラ解像度の調整:
   - 640x480 → 推論速度優先
   - 1280x720 → 検出精度優先

2. 推論頻度の制御:
   - 毎フレーム推論ではなく、2-3フレームおきに推論
   - 中間フレームは前回結果を補間表示

3. GPU Delegate 有効化:
   - Snapdragon の Adreno GPU で高速化
   - 推論時間: CPU ~15ms → GPU ~3ms

4. オブジェクトプール:
   - 描画オブジェクトの生成/破棄を避ける
   - Web版と同様のプール方式を採用 (MAX_DETECTIONS=5)
```

### Step 8: ビルドとデバイス テスト

```
1. Build Settings → Android → Build And Run
2. XREAL グラスを Android 端末に接続
3. Nebula アプリから開発者モードを有効化
4. adb logcat でログ確認:
   adb logcat -s Unity MediaPipe

5. 動作確認チェックリスト:
   □ カメラフレームが取得できている
   □ 顔が検出されている (ログ出力で確認)
   □ BBox が正しい位置に表示されている
   □ キーポイントが顔のパーツに対応している
   □ フレームレートが 25fps 以上を維持
   □ AR オーバーレイが現実の顔位置に重なっている
```

---

## 3. 技術的な詳細

### 3.1 BlazeFace モデル仕様

| 項目 | 値 |
|------|-----|
| モデル名 | blaze_face_short_range |
| 入力サイズ | 128x128 RGB |
| 出力 | バウンディングボックス + 6キーポイント |
| キーポイント | 右目、左目、鼻先、口中央、右耳珠、左耳珠 |
| 検出距離 | ~2m (Short Range) |
| モデルサイズ | float16: ~200KB |
| 量子化 | float16 (精度とサイズのバランス) |

### 3.2 キーポイント定義

```
    右耳珠 ──── 右目     左目 ──── 左耳珠
                    \   /
                     鼻先
                      |
                    口中央

インデックス:
  0: 右目 (Right Eye)
  1: 左目 (Left Eye)
  2: 鼻先 (Nose Tip)
  3: 口中央 (Mouth Center)
  4: 右耳珠 (Right Ear Tragion)
  5: 左耳珠 (Left Ear Tragion)
```

### 3.3 座標系の違い

```
MediaPipe 出力座標          Unity ワールド座標
 (正規化 [0,1])

  (0,0)───────▶ X(1,0)       Y ▲
    │                          │
    │                          │
    │                          │
    ▼                          └──────▶ X
  Y(0,1)                    Z は奥方向

  ※ 左上原点、Y下向き         ※ 左下原点、Y上向き
```

**変換式:**

```
Unity_X = MediaPipe_X           (ミラーしない場合)
Unity_X = 1.0 - MediaPipe_X    (ミラーする場合 / セルフィービュー)
Unity_Y = 1.0 - MediaPipe_Y    (Y軸反転)
Unity_Z = EstimateDepth(bbox)  (BBoxサイズから推定)
```

### 3.4 レイテンシ見積もり

| 処理段階 | 所要時間 | 備考 |
|----------|---------|------|
| カメラフレーム取得 | ~3ms | NRRGBCamTexture |
| テクスチャ変換 (GPU→CPU) | ~2ms | ReadPixels / AsyncGPUReadback |
| MediaPipe 推論 (GPU) | 2-5ms | Snapdragon GPU Delegate |
| MediaPipe 推論 (CPU) | 10-20ms | ARM NEON 最適化 |
| 座標変換 | <1ms | 単純な算術演算 |
| Unity 描画更新 | ~2ms | オブジェクトプール使用時 |
| **合計 (GPU)** | **~10ms** | **~100fps 相当** |
| **合計 (CPU)** | **~25ms** | **~40fps 相当** |

> **注**: 実測値はデバイスの Snapdragon チップセットにより異なる。XREAL ONE は Snapdragon XR2 Gen 2 相当のプロセッサを搭載。

### 3.5 API リファレンス (主要クラス)

**NRSDK (カメラ関連)**

| クラス/メソッド | 用途 |
|---------------|------|
| `NRRGBCamTexture` | RGBカメラからテクスチャ取得 |
| `NRRGBCamTexture.Play()` | カメラストリーム開始 |
| `NRRGBCamTexture.GetTexture()` | 現在フレームのTexture2D取得 |
| `NRFrame.GetPose()` | 現在のヘッド姿勢(6DoF)取得 |

**MediaPipe (顔検出関連)**

| クラス/メソッド | 用途 |
|---------------|------|
| `FaceDetector.CreateAsync()` | 検出器の非同期初期化 |
| `FaceDetector.DetectForVideo()` | ビデオフレームに対する検出実行 |
| `FaceDetectorResult.detections` | 検出結果リスト |
| `Detection.boundingBox` | バウンディングボックス (正規化座標) |
| `Detection.keypoints` | 6キーポイント (正規化座標) |
| `Detection.categories[0].score` | 検出信頼度 [0, 1] |

---

## 4. Web版との比較

| 項目 | Web版 (現デモ) | XREAL ONE + Eye 版 |
|------|---------------|-------------------|
| **ランタイム** | ブラウザ (Chrome) | Unity (Android) |
| **カメラ** | Webカメラ (`getUserMedia`) | Eye RGBカメラ (`NRRGBCamTexture`) |
| **解像度** | 1280x720 | 640x480 (設定可変) |
| **描画エンジン** | Three.js (WebGL) | Unity (OpenGLES3) |
| **MediaPipe** | WASM + WebGL Delegate | TFLite + GPU Delegate (native) |
| **座標系** | 正規化 [0,1] → Three.js Ortho | 正規化 [0,1] → Unity World |
| **ミラー表示** | UV反転 (セルフィー) | カメラ向き次第 (通常不要) |
| **オーバーレイ** | LineLoop + CircleGeometry + Sprite | LineRenderer + Sphere + TextMeshPro |
| **FPS** | ~60fps (デスクトップ) | ~30fps ターゲット (モバイル) |
| **奥行き** | なし (2D オーバーレイ) | BBoxサイズから推定 (疑似3D配置) |

### 4.1 処理の違い (詳細)

**カメラ入力**

```
Web版:
  navigator.mediaDevices.getUserMedia() → HTMLVideoElement → VideoTexture

XREAL版:
  NRRGBCamTexture.Play() → Texture2D → MediaPipe Image
```

- Web版はブラウザAPIでストリーミング取得、XREAL版はNRSDKのネイティブAPIで取得
- XREAL版ではテクスチャのGPU→CPU転送が追加で必要になる場合がある

**推論実行**

```
Web版:
  @mediapipe/tasks-vision (WASM)
  detectForVideo(videoElement, timestamp) → JavaScript オブジェクト

XREAL版:
  MediaPipe Unity Plugin (Native TFLite)
  DetectForVideo(Image, timestamp) → C# オブジェクト
```

- Web版は WASM で実行、XREAL版は ネイティブ TFLite バイナリで実行
- ネイティブ版のほうが推論速度は一般に高速

**描画**

```
Web版 (Three.js):
  - OrthographicCamera [0,1] 座標系
  - LineLoop (BBox), CircleGeometry (KP), Sprite (ラベル)
  - オブジェクトプール: MAX_DETECTIONS=5

XREAL版 (Unity):
  - AR空間にワールド座標で配置
  - LineRenderer (BBox), Sphere (KP), TextMeshPro (ラベル)
  - BBoxサイズから距離推定 → 3D空間に配置
  - オブジェクトプール: 同方式を推奨
```

- Web版は2Dオーバーレイ（画面座標に直接配置）
- XREAL版は3D空間に配置（頭の動きに追従しない固定AR表示、または追従するビルボード表示を選択可能）

### 4.2 Web版コードからの移植ポイント

Web版 `main.js` の各関数と XREAL版の対応:

| Web版の関数 | XREAL版の対応 | 変更点 |
|------------|-------------|--------|
| `setupWebcam()` | `NRRGBCamTexture.Play()` | ブラウザAPI → NRSDK API |
| `initFaceDetector()` | `FaceDetector.CreateAsync()` | WASM → Native TFLite |
| `setupThreeJS()` | Unity Scene 初期設定 | Three.js → Unity |
| `createOverlayObjects()` | Prefab のプール生成 | Geometry → Prefab |
| `updateOverlays()` | `ARFaceOverlay.UpdateOverlays()` | 2D座標 → 3D空間配置 |
| `animate()` | `Update()` | requestAnimationFrame → MonoBehaviour |
| `onWindowResize()` | 不要 | AR表示はビューポート固定 |

---

## 5. 既知の制約と注意事項

1. **BlazeFace Short Range は ~2m まで**: 遠距離の顔検出には BlazeFace Full Range または別モデルが必要
2. **Eye カメラの FOV が狭い**: 視野外の顔は検出不可。顔検出とヘッドトラッキングを組み合わせて補完可能
3. **発熱**: 長時間のリアルタイム推論はデバイスが発熱するため、推論頻度を抑える工夫が必要
4. **照明条件**: 暗所では検出精度が著しく低下する。Eye カメラにIRライトはないため自然光に依存
5. **プライバシー**: 他者の顔を検出・記録する場合、プライバシー法規への配慮が必要



# Unity Sentis を使用してみる！
1. Unity Sentis を追加する

Packages/manifest.json に "com.unity.sentis": "1.4.0" を追加
BlazeFace ONNX モデルを用意する

StreamingAssets/ に blaze_face_short_range.onnx を配置
YUV→RGB 変換スクリプトを作る

RenderTexture に blit してから ReadPixels で Texture2D を取得
FaceDetector スクリプトを作る

Sentis で推論 → BBox 座標を取得
Canvas に BBox 描画スクリプトを作る

正規化座標 → RawImage の RectTransform 上の座標に変換して描画

