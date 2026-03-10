using UnityEngine;

/// <summary>
/// N体重力シミュレーション（銀河合体）- パーティクルシステム版
///
/// 見た目: 発光する粒子が螺旋腕を形成しながら引き合い、銀河合体のアニメーションを生成。
/// 銀河A = 暖色（橙→金）、銀河B = 寒色（青→シアン）。速い星ほど白熱して輝く。
///
/// レンダリング: 単一 ParticleSystem + Additive ブレンド + プロシージャル Gaussian グロウテクスチャ。
/// XREAL の透過ディスプレイでは Additive ブレンドが光の加算になり、部屋に星雲が浮かんで見える。
///
/// [シーンセットアップ]
///   1. 空の GameObject に本スクリプトをアタッチするだけ
///   2. Play → カメラ前方 1.8m に星雲が自動出現
///
/// [手連動] SetExtraGravitySources(positions, masses) を毎フレーム呼ぶだけ
/// </summary>
public class GravitySimulation : MonoBehaviour
{
    // -----------------------------------------------------------------------
    // Inspector
    // -----------------------------------------------------------------------

    [Header("Simulation")]
    [SerializeField, Range(50, 500)] int   starCount     = 200;
    [SerializeField]                 float G             = 0.3f;
    [SerializeField]                 float dt            = 0.001f;
    [SerializeField, Range(1, 10)]   int   stepsPerFrame = 5;
    [SerializeField]                 float softening     = 0.03f;

    [Header("Initial Conditions")]
    [SerializeField] float galaxyRadius     = 0.35f;
    [SerializeField] float galaxySeparation = 1.0f;
    [SerializeField] float orbitSpeed       = 1.0f;
    [SerializeField] float mergeSpeed       = 0.05f;
    [SerializeField] float massMin          = 0.05f;
    [SerializeField] float massMax          = 1.0f;
    [SerializeField] float blackHoleMass    = 5.0f;

    [Header("Visual")]
    [SerializeField] float particleSizeBase = 0.05f;  // 質量1 の星のグロウ半径 (m)
    [SerializeField] float blackHoleSizeMul = 3.5f;   // ブラックホールのサイズ倍率

    [Header("XREAL / Camera")]
    [SerializeField] bool  autoOrientToCamera = true;
    [SerializeField] float cameraDistance     = 1.8f;
    [SerializeField] float cameraOffsetY      = -0.2f;

    // -----------------------------------------------------------------------
    // 内部状態
    // -----------------------------------------------------------------------

    Vector3[] _pos, _vel, _forces;
    float[]   _mass;
    Vector3   _origin;

    ParticleSystem          _ps;
    ParticleSystem.Particle[] _particles;
    Texture2D               _glowTex;
    Material                _particleMat;

    struct GravitySource { public Vector3 pos; public float mass; }
    GravitySource[] _extraSources = new GravitySource[0];

    // -----------------------------------------------------------------------
    // MonoBehaviour
    // -----------------------------------------------------------------------

    void Start()
    {
        _origin = transform.position;
        InitPhysics();
        InitParticleSystem();
    }

    void Update()
    {
        UpdateOrigin();
        for (int s = 0; s < stepsPerFrame; s++)
            StepPhysics();
        UpdateParticles();
    }

    void OnDestroy()
    {
        if (_glowTex    != null) Destroy(_glowTex);
        if (_particleMat != null) Destroy(_particleMat);
    }

    // -----------------------------------------------------------------------
    // 物理初期化（螺旋銀河 × 2）
    // -----------------------------------------------------------------------

    void InitPhysics()
    {
        _pos    = new Vector3[starCount];
        _vel    = new Vector3[starCount];
        _mass   = new float  [starCount];
        _forces = new Vector3[starCount];

        int   halfN   = starCount / 2;
        float halfSep = galaxySeparation * 0.5f;

        for (int i = 0; i < starCount; i++)
        {
            bool  isA    = i < halfN;
            float cX     = isA ? -halfSep : +halfSep;
            float spin   = isA ? +1f : -1f;    // A = 右回転, B = 左回転（逆回転で合体が派手）
            bool  isBH   = (i == 0) || (i == halfN);

            float   m;
            Vector3 local;

            if (isBH)
            {
                m     = blackHoleMass;
                local = Vector3.zero;
            }
            else
            {
                m = Mathf.Lerp(massMin, massMax, Random.value);

                // 対数螺旋腕（2本腕）
                float diskR   = galaxyRadius * Mathf.Lerp(0.08f, 1.0f, Random.value);
                int   arm     = Random.value < 0.5f ? 0 : 1;
                float baseAng = arm * Mathf.PI;
                // 螺旋ピッチ: diskR が大きいほど angle が増える
                float spiral  = 3.2f * Mathf.Log(1f + diskR / galaxyRadius * 9f);
                float scatter = Random.Range(-0.45f, 0.45f);
                float ang     = baseAng + spiral + scatter;

                local = new Vector3(
                    diskR * Mathf.Cos(ang),
                    Random.Range(-0.04f, 0.04f) * galaxyRadius,
                    diskR * Mathf.Sin(ang));
            }

            _mass[i] = m;
            _pos [i] = new Vector3(cX, 0f, 0f) + local;

            // 円盤の接線方向に軌道速度を付与
            Vector3 tang = Vector3.zero;
            if (!isBH && local.magnitude > 0.001f)
            {
                float rXZ    = new Vector2(local.x, local.z).magnitude + 0.001f;
                float totalM = halfN * (massMin + massMax) * 0.5f + blackHoleMass;
                float vOrbit = orbitSpeed * Mathf.Sqrt(G * totalM / rXZ);
                tang = new Vector3(-local.z / rXZ, 0f, local.x / rXZ) * spin * vOrbit;
            }

            float drift = isA ? +mergeSpeed : -mergeSpeed;
            _vel[i] = tang + new Vector3(drift, 0f, 0f);
        }
    }

    // -----------------------------------------------------------------------
    // 物理演算（O(N²) Leapfrog、Newton 第3法則で半分に最適化）
    // -----------------------------------------------------------------------

    void StepPhysics()
    {
        float eps2 = softening * softening;

        for (int i = 0; i < starCount; i++)
            _forces[i] = Vector3.zero;

        // 星ペアの相互引力
        for (int i = 0; i < starCount - 1; i++)
            for (int j = i + 1; j < starCount; j++)
            {
                Vector3 diff = _pos[j] - _pos[i];
                float   d2   = diff.sqrMagnitude + eps2;
                float   c    = G / (d2 * Mathf.Sqrt(d2));
                _forces[i] += c * _mass[j] * diff;
                _forces[j] -= c * _mass[i] * diff;
            }

        // 追加重力源（手など）
        for (int i = 0; i < starCount; i++)
            foreach (var src in _extraSources)
            {
                Vector3 toSrc = src.pos - _origin - _pos[i];
                float   d2    = toSrc.sqrMagnitude + eps2;
                _forces[i] += G * src.mass * toSrc / (d2 * Mathf.Sqrt(d2));
            }

        for (int i = 0; i < starCount; i++)
        {
            _vel[i] += _forces[i] * dt;
            _pos[i] += _vel[i]    * dt;
        }
    }

    // -----------------------------------------------------------------------
    // ParticleSystem セットアップ
    // -----------------------------------------------------------------------

    void InitParticleSystem()
    {
        _glowTex    = CreateGlowTexture(64);
        _particleMat = CreateGlowMaterial(_glowTex);

        _ps = gameObject.AddComponent<ParticleSystem>();

        // 自動出力を全て無効化
        var main = _ps.main;
        main.loop           = false;
        main.playOnAwake    = false;
        main.maxParticles   = starCount;
        main.startLifetime  = 9999f;
        main.startSpeed     = 0f;
        main.startSize      = particleSizeBase;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.stopAction     = ParticleSystemStopAction.None;

        var emission = _ps.emission;
        emission.enabled = false;

        // レンダラー設定
        var psRend = GetComponent<ParticleSystemRenderer>();
        psRend.material         = _particleMat;
        psRend.renderMode       = ParticleSystemRenderMode.Billboard;
        psRend.sortMode         = ParticleSystemSortMode.None;
        psRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        psRend.receiveShadows   = false;

        // 初期パーティクルを注入
        _particles = new ParticleSystem.Particle[starCount];
        for (int i = 0; i < starCount; i++)
        {
            _particles[i].position         = _origin + _pos[i];
            _particles[i].velocity         = Vector3.zero;
            _particles[i].startLifetime    = 9999f;
            _particles[i].remainingLifetime = 9999f;
            _particles[i].startSize        = particleSizeBase * Mathf.Pow(_mass[i], 1f / 3f);
            _particles[i].startColor       = Color.white;
        }
        _ps.SetParticles(_particles, starCount);
        _ps.Play();
    }

    // -----------------------------------------------------------------------
    // パーティクル更新（毎フレーム）
    // -----------------------------------------------------------------------

    void UpdateParticles()
    {
        // 速度の最大値（正規化用）
        float maxSpeedSq = 1e-6f;
        for (int i = 0; i < starCount; i++)
        {
            float sq = _vel[i].sqrMagnitude;
            if (sq > maxSpeedSq) maxSpeedSq = sq;
        }
        float maxSpeed = Mathf.Sqrt(maxSpeedSq);
        int   halfN    = starCount / 2;

        for (int i = 0; i < starCount; i++)
        {
            _particles[i].position         = _origin + _pos[i];
            _particles[i].remainingLifetime = 9999f;  // 消えないようにリセット

            float t  = Mathf.Clamp01(_vel[i].magnitude / maxSpeed);
            float t2 = t * t;  // 二乗で急峻な変化

            Color c;
            if (i < halfN)
                // 銀河 A: 暗い赤→橙→金白（熱せられた星）
                c = Color.Lerp(new Color(0.85f, 0.12f, 0.0f), new Color(1.0f, 0.92f, 0.55f), t2);
            else
                // 銀河 B: 深青→明るいシアン白（若い青白い星）
                c = Color.Lerp(new Color(0.0f, 0.12f, 0.85f), new Color(0.45f, 0.95f, 1.0f), t2);

            // 速いほど白熱（白に近づく）
            c = Color.Lerp(c, Color.white, t2 * 0.45f);

            // alpha = Additive ブレンドでの輝度。遅い星も最低限光らせる
            c.a = 0.18f + t * 0.82f;

            bool isBH = (i == 0) || (i == halfN);
            if (isBH)
            {
                // ブラックホール: 眩しい白
                c   = new Color(1f, 1f, 1f, 1f);
            }

            _particles[i].startColor = c;
            _particles[i].startSize  = particleSizeBase
                * Mathf.Pow(_mass[i], 1f / 3f)
                * (isBH ? blackHoleSizeMul : (1f + t * 0.6f)); // 速いほど少し大きく
        }

        _ps.SetParticles(_particles, starCount);
    }

    // -----------------------------------------------------------------------
    // カメラ追従（XREAL 用）
    // -----------------------------------------------------------------------

    void UpdateOrigin()
    {
        if (!autoOrientToCamera) return;
        var cam = Camera.main;
        if (cam == null) return;
        _origin = cam.transform.position
                + cam.transform.forward * cameraDistance
                + Vector3.up            * cameraOffsetY;
    }

    // -----------------------------------------------------------------------
    // Gaussian グロウテクスチャ生成
    // -----------------------------------------------------------------------

    static Texture2D CreateGlowTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode   = TextureWrapMode.Clamp;

        float half = size * 0.5f;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = (x - half) / half;
            float dy = (y - half) / half;
            float d2 = dx * dx + dy * dy;

            // 二重ガウシアン: 鋭い輝点 + 広い柔らかいハロー
            float core  = Mathf.Exp(-d2 * 11f);   // 小さく鋭い中核
            float halo  = Mathf.Exp(-d2 *  2.0f); // 大きく柔らかいグロウ
            float spike = Mathf.Exp(-d2 * 50f) * 0.6f; // 中心の超輝点

            float alpha = Mathf.Clamp01(spike + core * 0.9f + halo * 0.35f);

            tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
        }

        tex.Apply();
        return tex;
    }

    // -----------------------------------------------------------------------
    // Additive ブレンド マテリアル生成
    // -----------------------------------------------------------------------

    static Material CreateGlowMaterial(Texture2D glowTex)
    {
        // Built-in RP 向け Additive パーティクルシェーダーを優先順に検索
        string[] candidates =
        {
            "Legacy Shaders/Particles/Additive",
            "Particles/Additive",
            "Mobile/Particles/Additive",
            "Particles/Standard Unlit",
        };

        Shader sh = null;
        foreach (var name in candidates)
        {
            sh = Shader.Find(name);
            if (sh != null) break;
        }

        if (sh == null)
        {
            Debug.LogWarning("[GravitySimulation] Additive shader not found. Falling back to Standard.");
            sh = Shader.Find("Standard");
        }

        var mat = new Material(sh);
        mat.mainTexture = glowTex;
        return mat;
    }

    // -----------------------------------------------------------------------
    // 公開 API（手連動など）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 追加重力源を設定。毎フレームワールド座標で渡す。
    /// 例: 手の3D座標を重力源として渡すと星が引き寄せられる。
    /// </summary>
    public void SetExtraGravitySources(Vector3[] worldPositions, float[] gravMasses)
    {
        _extraSources = new GravitySource[worldPositions.Length];
        for (int i = 0; i < worldPositions.Length; i++)
            _extraSources[i] = new GravitySource { pos = worldPositions[i], mass = gravMasses[i] };
    }

    public void ClearExtraGravitySources() => _extraSources = new GravitySource[0];
}
