# XREAL 6DOF 1人称視点カメラ 実装ガイド

このガイドでは、XREAL 1 S・XREAL EYE・XREAL Beam Proを使用して、6DOF（6自由度）の1人称視点カメラを実装する手順を説明します。

## 目次

1. [概要](#概要)
2. [前提条件](#前提条件)
3. [基本構造の理解](#基本構造の理解)
4. [実装手順](#実装手順)
5. [コンポーネント詳細](#コンポーネント詳細)
6. [トラッキングモードの切り替え](#トラッキングモードの切り替え)
7. [トラブルシューティング](#トラブルシューティング)

---

## 概要

XREAL XR Pluginを使用した6DOF追跡は、ユーザーの頭部の位置と回転の両方を追跡します。これにより、ユーザーが前後左右に移動したり、上下に動いたりする動作を検知できます。

### トラッキングモードの種類

| モード | 説明 | 自由度 |
|-------|------|--------|
| **MODE_6DOF** | 位置 + 回転を追跡（前後左右上下 + ヨーピッチロール） | 6自由度 |
| **MODE_3DOF** | 回転のみ追跡（ヨーピッチロール） | 3自由度 |
| **MODE_0DOF_STAB** | 固定位置・回転（安定化あり） | 0自由度 |
| **MODE_0DOF** | 固定位置・回転 | 0自由度 |

---

## 前提条件

### 必要なパッケージ

- **XREAL XR Plugin** (v3.1.0以降)
- **XR Core Utils** (v2.5.2以降)
- **XR Legacy Input Helpers** (v2.1.12以降)
- **Input System** (新しいInput System有効化)

### サポートデバイス

- XREAL 1 S
- XREAL EYE
- XREAL Beam Pro
- その他XREAL One シリーズデバイス

### プロジェクト設定

`Assets/XR/Settings/XREALSettings.asset`で以下を確認：

```yaml
InitialTrackingType: 0  # 0 = MODE_6DOF
InitialInputSource: 1   # 1 = Controller
SupportMultiResume: 1
AddtionalPermissions:
  - CAMERA
  - VIBRATION
```

---

## 基本構造の理解

### XR Origin階層構造

```
XR Origin (GameObject)
├─ XROrigin (Component)
│  └─ Camera: Main Camera への参照
│  └─ CameraFloorOffsetObject: Camera Offset への参照
│
├─ Camera Offset (GameObject/Transform)
│  └─ LocalPosition: (0, 1.1176, 0)  ※ユーザーの目の高さ
│  └─ LocalRotation: Identity
│
└─ Main Camera (GameObject)
   ├─ Camera (Component)
   │  └─ TargetEye: BothEyes
   │  └─ Stereo Separation: 0.022
   │  └─ Field of View: 60
   │
   └─ TrackedPoseDriver (Component)
      └─ Device: Head (0)
      └─ Pose Source: Center Eye (2)
      └─ Tracking Type: Rotation And Position
      └─ Update Type: Update And Before Render
```

### 重要なコンポーネント

1. **XROrigin** - XR空間の原点を管理
2. **TrackedPoseDriver** - XRデバイスからの追跡データをカメラに適用
3. **XREALSessionManager** - セッション管理、リセンタリング、カメラキャッシュ

---

## 実装手順

### ステップ1: XR Originプレハブの配置

1. **既存のMain Cameraを削除**
   - シーン内の既存のMain Cameraを削除します

2. **XRRigプレハブを追加**

   **方法A: Package Managerから**
   ```
   Packages/XR Legacy Input Helpers/Prefabs/XRRig.prefab
   ```
   をシーンにドラッグ＆ドロップ

   **方法B: サンプルから複製**
   ```
   Assets/Samples/XREAL XR Plugin/3.1.0/Interaction Basics/HelloMR/HelloMR.unity
   ```
   からXR Interaction Hands Setupプレハブを参照

3. **名前変更（オプション）**
   - XRRig → XR Origin

### ステップ2: XROriginコンポーネントの設定

XR OriginゲームオブジェクトのInspectorで：

```
XROrigin Component:
├─ Camera: Main Camera (子オブジェクト)
├─ Camera Floor Offset Object: Camera Offset (子オブジェクト)
├─ Origin Base GameObject: XR Origin (自身)
├─ Camera Y Offset: 1.1176 (デフォルト座高)
└─ Requested Tracking Origin Mode: Device
```

### ステップ3: Main Cameraの設定

`XR Origin/Camera Offset/Main Camera`のInspectorで：

#### Cameraコンポーネント設定

```
Camera:
├─ Clear Flags: Skybox
├─ Culling Mask: Everything
├─ Projection: Perspective
├─ Field of View: 60
├─ Physical Camera: Off
├─ Clipping Planes:
│  ├─ Near: 0.3
│  └─ Far: 1000
├─ Viewport Rect: X:0 Y:0 W:1 H:1
├─ Depth: -1
├─ Rendering Path: Use Graphics Settings
├─ Target Texture: None
├─ Target Eye: Both (3)
├─ Occlusion Culling: On
├─ HDR: On
├─ MSAA: Off
├─ Allow Dynamic Resolution: Off
└─ Stereo Separation: 0.022
```

#### TrackedPoseDriverコンポーネント設定

```
TrackedPoseDriver:
├─ Device: Generic XR Device (0)
├─ Pose Source: Center Eye - HMD Reference (2)
├─ Tracking Type: Rotation And Position (0)
├─ Update Type: Update And Before Render (0)
└─ Use Relative Transform: Off
```

### ステップ4: XREALSessionManagerの追加（推奨）

1. **空のGameObjectを作成**
   ```
   Hierarchy → Right Click → Create Empty
   Name: "XREAL Session Manager"
   ```

2. **XREALSessionManagerスクリプトを追加**

   `Add Component → XREAL Session Manager`

3. **設定項目**

```csharp
XREALSessionManager:
├─ Menu Action: XRI LeftHand Interaction/Menu (InputActionReference)
├─ Recenter Action: XRI RightHand Interaction/Recenter (InputActionReference)
├─ Menu Prefab: XREALHomeMenu prefab
├─ Recenter Vibration Enabled: true
├─ Vibration Amplitude: 0.25
├─ Vibration Duration: 0.15
├─ Cache Camera X Rotation: false
├─ Cache Camera Y Rotation: true
└─ Cache Camera Position: true  ※6DOFで重要
```

**重要ポイント:**
- `Cache Camera Position: true` - 6DOFモードでアプリ一時停止/再開時にカメラ位置を保持
- `Cache Camera Y Rotation: true` - Y軸回転（水平方向の視線）をキャッシュ

### ステップ5: トラッキングタイプの設定

#### A. 起動時に6DOFを設定（推奨）

`Assets/XR/Settings/XREALSettings.asset`で：

```yaml
InitialTrackingType: 0  # 0 = MODE_6DOF
```

#### B. ランタイムで6DOFに切り替え

スクリプトから動的に切り替える場合：

```csharp
using Unity.XR.XREAL;
using UnityEngine;

public class TrackingModeController : MonoBehaviour
{
    private async void Start()
    {
        // 現在のトラッキングモードを確認
        TrackingType currentMode = XREALPlugin.GetTrackingType();
        Debug.Log($"Current Tracking Mode: {currentMode}");

        // 6DOFが利用可能か確認
        bool supports6DOF = XREALPlugin.IsHMDFeatureSupported(
            XREALSupportedFeature.XREAL_FEATURE_PERCEPTION_HEAD_TRACKING_POSITION
        );

        if (supports6DOF && currentMode != TrackingType.MODE_6DOF)
        {
            // 6DOFに切り替え
            bool success = await XREALPlugin.SwitchTrackingTypeAsync(
                TrackingType.MODE_6DOF,
                OnTrackingTypeChanged
            );

            if (success)
            {
                Debug.Log("Successfully switched to 6DOF mode");
            }
            else
            {
                Debug.LogError("Failed to switch to 6DOF mode");
            }
        }
    }

    private void OnTrackingTypeChanged(bool result, TrackingType targetTrackingType)
    {
        if (result)
        {
            Debug.Log($"Tracking type changed to: {targetTrackingType}");
        }
        else
        {
            Debug.LogWarning($"Failed to change tracking type to: {targetTrackingType}");
        }
    }
}
```

### ステップ6: ビルド設定

#### Build Settings

```
Platform: Android
Architecture: ARM64
Minimum API Level: Android 10.0 (API level 29)以降
```

#### Player Settings

```
XR Plug-in Management:
└─ Android Tab
   └─ XREAL XR: ✓ Enabled

Other Settings:
├─ Color Space: Linear
├─ Auto Graphics API: Off
├─ Graphics APIs:
│  └─ OpenGLES3
├─ Multithreaded Rendering: On
├─ Active Input Handling: Input System Package (New)
└─ Scripting Backend: IL2CPP
```

### ステップ7: テストとデバッグ

1. **Editorでの確認**
   - Play Modeで実行（エディタではシミュレーション）
   - Game Viewでカメラが正常に動作するか確認

2. **実機テスト**
   - XREAL 1 S / XREAL EYE / Beam Proに接続
   - ビルド＆実行
   - 頭を前後左右に動かして位置追跡を確認
   - 頭を回転させて回転追跡を確認

---

## コンポーネント詳細

### XREALSessionManager.cs

**役割:** セッション管理、カメラ状態のキャッシング、リセンタリング機能

**主要機能:**

1. **カメラ位置キャッシュ（6DOF専用）**
   ```csharp
   // アプリ一時停止時
   OnApplicationPause(true)
   → カメラのlocalPosition/localRotationをキャッシュ

   // アプリ再開時
   OnApplicationPause(false)
   → キャッシュした位置/回転を復元
   ```

2. **トラッキングタイプ変更時の処理**
   ```csharp
   OnTrackingTypeChangedInternal(bool result, TrackingType targetTrackingType)
   → カメラオフセットをリセット
   → XROrigin.CameraYOffsetを適用
   ```

3. **リセンタリング**
   ```csharp
   XREALPlugin.RecenterController()
   → コントローラーの原点をリセット
   ```

**ファイルパス:**
```
Library/PackageCache/com.xreal.xr@f282e2634a67/Runtime/Scripts/Android/XREALSessionManager.cs
```

### XROrigin.cs

**役割:** XR空間の原点（0, 0, 0）を管理し、カメラのオフセットを処理

**主要プロパティ:**

```csharp
public Camera Camera { get; set; }
// XRデバイスでレンダリングするカメラ

public GameObject Origin { get; set; }
// 移動操作の基準となるゲームオブジェクト

public GameObject CameraFloorOffsetObject { get; set; }
// 床からの高さオフセットを適用するオブジェクト

public float CameraYOffset { get; set; }
// デフォルト: 1.1176m (44インチ = 平均座高)

public TrackingOriginMode RequestedTrackingOriginMode { get; set; }
// Device / Floor / Unbounded
```

**ファイルパス:**
```
Library/PackageCache/com.unity.xr.core-utils@2.5.2/Runtime/XROrigin.cs
```

### TrackedPoseDriver

**役割:** XRデバイスからトラッキングデータを取得し、カメラのTransformに適用

**設定値の意味:**

| パラメータ | 値 | 説明 |
|----------|---|------|
| Device | Generic XR Device (0) | HMD（ヘッドマウントディスプレイ）を追跡 |
| Pose Source | Center Eye (2) | 両目の中心点の位置を使用 |
| Tracking Type | Rotation And Position (0) | 回転＋位置の両方を追跡（6DOF） |
| Update Type | Update And Before Render (0) | Update()とレンダリング前に更新 |

**パッケージ:**
```
com.unity.xr.legacyinputhelpers@2.1.12
```

---

## トラッキングモードの切り替え

### UIからの切り替え実装例

HelloMRサンプルを参考にした実装：

```csharp
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Unity.XR.XREAL;

public class TrackingModeSwitcher : MonoBehaviour
{
    [SerializeField] TMP_Text currentModeText;
    [SerializeField] Toggle toggle0DOF;
    [SerializeField] Toggle toggle3DOF;
    [SerializeField] Toggle toggle6DOF;

    private void Start()
    {
        // 現在のモードを表示
        UpdateCurrentModeText();

        // イベント登録
        XREALPlugin.OnTrackingTypeChanged += OnTrackingTypeChanged;
        toggle0DOF.onValueChanged.AddListener(On0DOFToggleChanged);
        toggle3DOF.onValueChanged.AddListener(On3DOFToggleChanged);
        toggle6DOF.onValueChanged.AddListener(On6DOFToggleChanged);

        // 初期UI状態を設定
        InitToggleStates();
    }

    private void OnDestroy()
    {
        XREALPlugin.OnTrackingTypeChanged -= OnTrackingTypeChanged;
        toggle0DOF.onValueChanged.RemoveListener(On0DOFToggleChanged);
        toggle3DOF.onValueChanged.RemoveListener(On3DOFToggleChanged);
        toggle6DOF.onValueChanged.RemoveListener(On6DOFToggleChanged);
    }

    private void InitToggleStates()
    {
        TrackingType currentMode = XREALPlugin.GetTrackingType();
        switch (currentMode)
        {
            case TrackingType.MODE_0DOF:
                toggle0DOF.SetIsOnWithoutNotify(true);
                break;
            case TrackingType.MODE_3DOF:
                toggle3DOF.SetIsOnWithoutNotify(true);
                break;
            case TrackingType.MODE_6DOF:
                toggle6DOF.SetIsOnWithoutNotify(true);
                break;
        }
    }

    private async void On6DOFToggleChanged(bool isOn)
    {
        if (isOn)
        {
            await XREALPlugin.SwitchTrackingTypeAsync(
                TrackingType.MODE_6DOF,
                OnTrackingTypeChanged
            );
        }
    }

    private async void On3DOFToggleChanged(bool isOn)
    {
        if (isOn)
        {
            await XREALPlugin.SwitchTrackingTypeAsync(
                TrackingType.MODE_3DOF,
                OnTrackingTypeChanged
            );
        }
    }

    private async void On0DOFToggleChanged(bool isOn)
    {
        if (isOn)
        {
            await XREALPlugin.SwitchTrackingTypeAsync(
                TrackingType.MODE_0DOF,
                OnTrackingTypeChanged
            );
        }
    }

    private void OnTrackingTypeChanged(bool result, TrackingType targetTrackingType)
    {
        if (result)
        {
            UpdateCurrentModeText();
        }
        else
        {
            Debug.LogError($"Failed to switch to {targetTrackingType}");
        }
    }

    private void UpdateCurrentModeText()
    {
        TrackingType currentMode = XREALPlugin.GetTrackingType();
        currentModeText.text = $"Current Mode: {currentMode}";
    }
}
```

### イベントシステムの理解

トラッキングモード切り替え時のイベントフロー：

```
ユーザー操作
    ↓
XREALPlugin.SwitchTrackingTypeAsync()
    ↓
OnBeginChangeTrackingType イベント発火 ← 切り替え開始
    ↓
[5フレーム待機] ← 黒画面を下層に送る
    ↓
Internal.SwitchTrackingType() ← ネイティブAPIコール
    ↓
[5フレーム待機] ← トラッキング安定化待機
    ↓
OnTrackingTypeChangedInternal イベント発火 ← 内部処理用
    ↓
callback() 実行 ← ユーザー定義コールバック
    ↓
OnTrackingTypeChanged イベント発火 ← 公開イベント
```

### トラッキング状態の監視

```csharp
using Unity.XR.XREAL;
using UnityEngine;
#if XR_ARFOUNDATION
using UnityEngine.XR.ARSubsystems;
#endif

public class TrackingStateMonitor : MonoBehaviour
{
    private void Update()
    {
        // 現在のトラッキングタイプを取得
        TrackingType currentType = XREALPlugin.GetTrackingType();

        // トラッキングが失われた理由を取得（3DOF/6DOFのみ）
        if (currentType != TrackingType.MODE_0DOF &&
            currentType != TrackingType.MODE_0DOF_STAB)
        {
#if !UNITY_EDITOR && UNITY_ANDROID && XR_ARFOUNDATION
            NotTrackingReason reason = XREALPlugin.GetTrackingReason();

            if (reason != NotTrackingReason.None)
            {
                Debug.LogWarning($"Tracking Lost: {reason}");
                // ユーザーに通知を表示
                ShowTrackingLostNotification(reason);
            }
#endif
        }
    }

    private void ShowTrackingLostNotification(NotTrackingReason reason)
    {
        string message = reason switch
        {
            NotTrackingReason.InsufficientLight => "照明が不足しています",
            NotTrackingReason.InsufficientFeatures => "特徴点が不足しています",
            NotTrackingReason.ExcessiveMotion => "動きが速すぎます",
            _ => "トラッキングが失われました"
        };

        Debug.Log($"[Tracking] {message}");
        // TODO: UI通知を表示
    }
}
```

---

## トラブルシューティング

### 問題1: カメラが動かない

**症状:** 頭を動かしてもカメラ視点が変わらない

**解決方法:**

1. **TrackedPoseDriverの確認**
   ```
   Main Camera → TrackedPoseDriver
   ├─ Tracking Type: Rotation And Position にする
   └─ Enabled: チェックが入っているか確認
   ```

2. **トラッキングモードの確認**
   ```csharp
   TrackingType mode = XREALPlugin.GetTrackingType();
   Debug.Log($"Current Mode: {mode}");
   // MODE_6DOF であることを確認
   ```

3. **XR Plug-in Managementの確認**
   ```
   Edit → Project Settings → XR Plug-in Management
   → Android タブ
   → XREAL XR: ✓
   ```

### 問題2: 位置追跡のみ動かない（回転は動く）

**症状:** 頭の回転は追跡されるが、前後左右の移動が反映されない

**解決方法:**

1. **デバイスが6DOFをサポートしているか確認**
   ```csharp
   bool supports6DOF = XREALPlugin.IsHMDFeatureSupported(
       XREALSupportedFeature.XREAL_FEATURE_PERCEPTION_HEAD_TRACKING_POSITION
   );
   Debug.Log($"6DOF Supported: {supports6DOF}");
   ```

2. **XREALSettings.assetの確認**
   ```yaml
   InitialTrackingType: 0  # 0 = MODE_6DOF
   ```

3. **トラッキング環境の改善**
   - 明るい場所で使用
   - 特徴点が多い場所（模様のある壁など）
   - ゆっくり動く（急な動きはトラッキングロストの原因）

### 問題3: アプリ再開時にカメラ位置がリセットされる

**症状:** アプリをバックグラウンド → フォアグラウンドに戻すとカメラ位置が原点に戻る

**解決方法:**

**XREALSessionManagerを追加し、設定を有効化:**

```csharp
XREALSessionManager:
└─ Cache Camera Position: true ✓
```

または、自分でキャッシュロジックを実装：

```csharp
using Unity.XR.CoreUtils;
using UnityEngine;

public class CameraPositionCache : MonoBehaviour
{
    private Transform cameraTransform;
    private Transform cameraOffset;
    private Vector3 cachedPosition;
    private Quaternion cachedRotation;

    private void Start()
    {
        cameraTransform = Camera.main.transform;
        cameraOffset = cameraTransform.parent;
    }

    private void OnApplicationPause(bool pause)
    {
        if (XREALPlugin.GetTrackingType() != TrackingType.MODE_6DOF)
            return;

        if (pause)
        {
            // アプリ一時停止時: 位置をキャッシュ
            cachedPosition = cameraOffset.localPosition +
                           cameraOffset.localRotation * cameraTransform.localPosition;
            cachedRotation = cameraOffset.localRotation * cameraTransform.localRotation;
        }
        else
        {
            // アプリ再開時: 位置を復元
            cameraOffset.localPosition = cachedPosition;
            cameraOffset.localRotation = cachedRotation;
            cameraTransform.localPosition = Vector3.zero;
            cameraTransform.localRotation = Quaternion.identity;
        }
    }
}
```

### 問題4: ステレオレンダリングが正しく表示されない

**症状:** 左右の目に同じ映像が表示される、3D感がない

**解決方法:**

1. **Cameraのターゲット設定**
   ```
   Main Camera → Camera Component
   └─ Target Eye: Both (3)
   ```

2. **Stereo Separationの調整**
   ```
   Main Camera → Camera Component
   └─ Stereo Separation: 0.022
   ```

3. **Project Settingsの確認**
   ```
   Edit → Project Settings → XR Plug-in Management → XREAL XR
   └─ Stereo Rendering Mode: Multi Pass または Single Pass Instanced
   ```

### 問題5: パフォーマンスが悪い

**症状:** フレームレートが低い、カクカクする

**解決方法:**

1. **Quality Settingsの最適化**
   ```
   Edit → Project Settings → Quality
   ├─ V Sync Count: Don't Sync
   ├─ Anti Aliasing: Disabled or 2x Multi Sampling
   └─ Shadow Resolution: Medium Shadows
   ```

2. **ターゲットフレームレートの設定**
   ```csharp
   Application.targetFrameRate = 60; // または72
   ```

3. **レンダリングの最適化**
   ```
   Camera:
   ├─ MSAA: Off
   ├─ HDR: Off (必要ない場合)
   └─ Occlusion Culling: On
   ```

4. **ポリゴン数の削減**
   - LOD（Level of Detail）を使用
   - 不要なオブジェクトを非表示
   - ライトの数を減らす

### 問題6: Unityエディタで動作確認できない

**症状:** Play Modeでカメラが動かない

**回避方法:**

Unityエディタでは実機のXRトラッキングは利用できません。以下の方法で開発：

1. **XR Device Simulator使用（推奨）**
   ```
   Window → Package Manager
   → XR Interaction Toolkit をインストール
   → Samples → XR Device Simulator をインポート
   ```

2. **モックカメラコントローラー作成**
   ```csharp
   #if UNITY_EDITOR
   using UnityEngine;

   public class EditorCameraController : MonoBehaviour
   {
       public float moveSpeed = 2f;
       public float lookSpeed = 2f;

       private void Update()
       {
           // WASDで移動
           float h = Input.GetAxis("Horizontal");
           float v = Input.GetAxis("Vertical");
           transform.Translate(new Vector3(h, 0, v) * moveSpeed * Time.deltaTime);

           // マウスで視点回転
           if (Input.GetMouseButton(1))
           {
               float mouseX = Input.GetAxis("Mouse X") * lookSpeed;
               float mouseY = Input.GetAxis("Mouse Y") * lookSpeed;
               transform.Rotate(-mouseY, mouseX, 0);
           }
       }
   }
   #endif
   ```

---

## 参考ファイル

### サンプルシーン

```
Assets/Samples/XREAL XR Plugin/3.1.0/Interaction Basics/HelloMR/HelloMR.unity
```

このシーンは完全な6DOF実装例を含んでいます：
- XR Origin セットアップ
- トラッキングモード切り替えUI
- 入力ソース切り替え（コントローラー/ハンド）
- ハプティックフィードバック

### 重要なスクリプト

| ファイル | パス | 説明 |
|---------|------|------|
| XREALPlugin.cs | `Library/PackageCache/com.xreal.xr@f282e2634a67/Runtime/Scripts/XREALPlugin.cs` | XREAL API のメインインターフェース |
| XREALSessionManager.cs | `Library/PackageCache/com.xreal.xr@f282e2634a67/Runtime/Scripts/Android/XREALSessionManager.cs` | セッション管理とカメラキャッシュ |
| XROrigin.cs | `Library/PackageCache/com.unity.xr.core-utils@2.5.2/Runtime/XROrigin.cs` | XR空間の原点管理 |
| HelloMR.cs | `Assets/Samples/XREAL XR Plugin/3.1.0/Interaction Basics/HelloMR/HelloMR.cs` | トラッキングモード切り替えのサンプル |

### XREALPlugin API リファレンス

#### トラッキング関連

```csharp
// 現在のトラッキングタイプを取得
TrackingType GetTrackingType();

// トラッキングタイプを非同期で切り替え
async Task<bool> SwitchTrackingTypeAsync(TrackingType targetType, TrackingTypeChangedCallback callback = null);

// トラッキングが失われた理由を取得
NotTrackingReason GetTrackingReason();

// デバイスが機能をサポートしているか確認
bool IsHMDFeatureSupported(XREALSupportedFeature feature);

// コントローラーをリセンタリング
void RecenterController();
```

#### イベント

```csharp
// トラッキングタイプ変更開始時
event BeginChangeTrackingTypeEvent OnBeginChangeTrackingType;

// トラッキングタイプ変更完了時
event TrackingTypeChangedCallback OnTrackingTypeChanged;
```

#### デバイス情報

```csharp
// デバイスタイプを取得
XREALDeviceType GetDeviceType();

// XREAL One シリーズか確認
bool IsOneSeriesGlasses();

// デバイスカテゴリを取得
XREALDeviceCategory GetDeviceCategory();
```

---

## まとめ

### 最小限の6DOFセットアップ手順

1. ✅ **XRRig prefabをシーンに配置**
2. ✅ **XROriginコンポーネントの設定**（Camera, Camera Offset参照）
3. ✅ **TrackedPoseDriverの設定**（Rotation And Position）
4. ✅ **XREALSettings.assetで InitialTrackingType = 0 (MODE_6DOF)**
5. ✅ **XREALSessionManagerを追加**（推奨）
6. ✅ **Androidビルド設定**（XR Plug-in Management有効化）

### 次のステップ

- ✨ **インタラクション追加**: XR Interaction Toolkitを使用してオブジェクトとのインタラクション
- ✨ **空間アンカー**: AR Foundationを使用して現実空間にオブジェクトを固定
- ✨ **ハンドトラッキング**: InputSource.Handsでハンドトラッキング対応
- ✨ **平面検出**: AR Plane Managerで床や壁を検出
- ✨ **画像トラッキング**: AR Tracked Image Managerでマーカー認識

---

## サポート

### 公式ドキュメント

- XREAL Developer Portal: https://developer.xreal.com/
- Unity XR Documentation: https://docs.unity3d.com/Manual/XR.html

### コミュニティ

- XREAL Developer Discord
- Unity Forum - XR Development

---

**作成日**: 2026-02-06
**対象バージョン**: XREAL XR Plugin v3.1.0, Unity 2022.3 LTS
**対象デバイス**: XREAL 1 S, XREAL EYE, XREAL Beam Pro
