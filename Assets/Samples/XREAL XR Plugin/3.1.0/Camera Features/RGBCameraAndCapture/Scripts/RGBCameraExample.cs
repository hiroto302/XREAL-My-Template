using UnityEngine;
using UnityEngine.UI;

namespace Unity.XR.XREAL.Samples
{
    /// <summary>
    /// XREAL Eye Camera の RGB カメラ映像をキャプチャし、UI に表示するサンプルクラス。
    ///
    /// 【概要】
    /// カメラ映像は YUV_420_888 フォーマットで 3 枚のテクスチャ (Y, U, V) として取得される。
    /// それらを RawImage にアタッチされた YUVTransRGB シェーダーに渡すことで、
    /// GPU 上で RGB に変換してリアルタイムに画面へ表示する。
    ///
    /// 【YUV テクスチャ パイプライン】
    ///   XREAL Eye Camera
    ///     → (ネイティブプラグイン) XREALPlugin.StartRGBCameraDataCapture()
    ///     → YUV_420_888 フレームデータ
    ///     → XREALRGBCameraTexture が Y / U / V を Alpha8 テクスチャに展開
    ///     → RawImage マテリアル (_MainTex / _UTex / _VTex) にセット
    ///     → YUVTransRGB シェーダー (BT.601 係数) で RGB 変換 + GammaToLinearSpace
    ///     → 画面表示
    ///
    /// 【自分の開発での活用ポイント】
    /// - Update() 内で GetYUVFormatTextures() の後に画像処理を挟むことで、
    ///   フレーム単位でカメラ画像を加工できる。
    /// - XREALRGBCameraTexture は DontDestroyOnLoad なシングルトンのため、
    ///   シーン遷移後も同インスタンスを CreateSingleton() で再取得可能。
    /// - ポーリング (Update) の代わりに OnRGBCameraUpdate イベントを購読すると
    ///   コールバック方式でフレーム更新を受け取れる。
    /// </summary>
    public class RGBCameraExample : MonoBehaviour
    {
        // -----------------------------------------------------------------------
        // SerializeField (Inspector で設定する UI 要素)
        // -----------------------------------------------------------------------

        /// <summary>現在の画像フォーマット名を表示するテキスト ("YUV_420_888")</summary>
        [SerializeField]
        private Text m_ImageFormatText;

        /// <summary>
        /// カメラ映像を表示する RawImage。
        /// YUVTransRGB シェーダーを持つマテリアルが Inspector でアサインされている必要がある。
        /// シェーダープロパティ: _MainTex (Y), _UTex (U), _VTex (V)
        /// </summary>
        [SerializeField]
        private RawImage m_YUVImage;

        /// <summary>キャプチャ開始ボタン。クリックで Play() を呼ぶ</summary>
        [SerializeField]
        private Button m_PlayButton;

        /// <summary>キャプチャ停止ボタン。クリックで Stop() を呼ぶ</summary>
        [SerializeField]
        private Button m_StopButton;

        // -----------------------------------------------------------------------
        // Private フィールド
        // -----------------------------------------------------------------------

        /// <summary>
        /// YUV テクスチャの管理シングルトン。
        /// ネイティブプラグインとの橋渡しを担い、DontDestroyOnLoad で維持される。
        /// </summary>
        private XREALRGBCameraTexture m_RGBCameraTexture;

        // -----------------------------------------------------------------------
        // Unity ライフサイクル
        // -----------------------------------------------------------------------

        /// <summary>
        /// 初期化処理。以下の順で実行される:
        ///   1. XREALRGBCameraTexture シングルトンを生成 (または既存を取得)
        ///   2. Play / Stop ボタンにリスナーをバインド
        ///   3. UI の初期状態を設定 (フォーマットテキスト + RawImage 有効化)
        ///   4. 即座にキャプチャを開始 (シーン読み込み直後から映像取得)
        /// </summary>
        void Start()
        {
            Debug.Log($"[RGBCamera] Start");

            // (1) カメラテクスチャのシングルトンを生成。
            //     内部で "XREALRGBCameraTexture" という名前の GameObject が作られ、
            //     DontDestroyOnLoad が適用される。
            m_RGBCameraTexture = XREALRGBCameraTexture.CreateSingleton();

            // (2) ボタンに Play / Stop をバインド
            m_PlayButton.onClick.AddListener(Play);
            m_StopButton.onClick.AddListener(Stop);

            // (3) UI 初期化 + (4) 自動再生
            InitUI();
            Play();
        }

        /// <summary>
        /// 毎フレーム、YUV テクスチャを取得して RawImage に反映する。
        ///
        /// GetYUVFormatTextures() は Texture2D[3] を返す:
        ///   [0] Y テクスチャ → _MainTex (輝度、フル解像度、Alpha8)
        ///   [1] U テクスチャ → _UTex   (色差・青、半解像度、Alpha8)
        ///   [2] V テクスチャ → _VTex   (色差・赤、半解像度、Alpha8)
        ///
        /// YUV → RGB 変換は YUVTransRGB シェーダー (BT.601) が GPU で処理する。
        /// yuvTextures[0] が null の間はキャプチャ前や停止中なので何もしない。
        ///
        /// ★ 独自処理を挟む場合はこの後に追加する:
        ///   var yuvTextures = m_RGBCameraTexture.GetYUVFormatTextures();
        ///   if (yuvTextures[0] != null) { /* ここで画像処理 */ }
        /// </summary>
        void Update()
        {
            var yuvTextures = m_RGBCameraTexture.GetYUVFormatTextures();
            if (yuvTextures[0] != null)
            {
                // Y プレーン → メインテクスチャ (輝度情報)
                m_YUVImage.texture = yuvTextures[0];
                // U プレーン → _UTex (色差・青)
                m_YUVImage.material.SetTexture("_UTex", yuvTextures[1]);
                // V プレーン → _VTex (色差・赤)
                m_YUVImage.material.SetTexture("_VTex", yuvTextures[2]);
            }
        }

        /// <summary>
        /// GameObject 破棄時にキャプチャを停止する。
        /// シーン遷移やアプリ終了時のリソース解放を保証する。
        /// </summary>
        private void OnDestroy()
        {
            Debug.Log($"[RGBCamera] OnDestroy");
            Stop();
        }

        // -----------------------------------------------------------------------
        // Private メソッド
        // -----------------------------------------------------------------------

        /// <summary>
        /// UI の初期状態を設定する。
        ///   - フォーマットテキストに "YUV_420_888" を表示
        ///   - RawImage を有効化
        /// </summary>
        private void InitUI()
        {
            m_ImageFormatText.text = "YUV_420_888";
            m_YUVImage.gameObject.SetActive(true);
        }

        // -----------------------------------------------------------------------
        // Public メソッド (ボタンからも呼べる)
        // -----------------------------------------------------------------------

        /// <summary>
        /// カメラキャプチャを開始する。
        /// IsCapturing が false の場合のみ実行し、二重開始を防ぐ。
        /// 内部では XREALPlugin.StartRGBCameraDataCapture() でネイティブ層を起動する。
        /// </summary>
        public void Play()
        {
            if (!m_RGBCameraTexture.IsCapturing)
            {
                Debug.Log($"[RGBCamera] Play");
                m_RGBCameraTexture.StartCapture();
            }
        }

        /// <summary>
        /// カメラキャプチャを停止する。
        /// IsCapturing が true の場合のみ実行し、二重停止を防ぐ。
        /// 内部では XREALPlugin.StopRGBCameraDataCapture() でネイティブ層を停止する。
        /// </summary>
        public void Stop()
        {
            if (m_RGBCameraTexture.IsCapturing)
            {
                Debug.Log($"[RGBCamera] Stop");
                m_RGBCameraTexture.StopCapture();
            }
        }
    }
}
