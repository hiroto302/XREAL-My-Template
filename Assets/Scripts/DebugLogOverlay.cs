using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Unity の Debug.Log をワールドスペース Canvas 上に表示するデバッグ用オーバーレイ。
///
/// 【用途】
/// Android Logcat に接続できない環境で、実機上のログを目視確認するために使用する。
/// Application.logMessageReceived を購読し、直近 m_MaxLines 行を Text に反映する。
///
/// 【セットアップ手順】
///   1. Hierarchy で右クリック → UI → Canvas を作成
///   2. Canvas の Inspector:
///        Render Mode     → World Space
///        Event Camera    → (Main Camera をアサイン)
///   3. Canvas の Transform:
///        Position        → (0, 0, 2) ※カメラ前方2mが目安
///        Scale           → (0.002, 0.002, 0.002)
///   4. Canvas 配下に UI → Text (Legacy) を作成
///        Rect Transform  → 適宜サイズ調整
///        Font Size       → 24 程度
///   5. Canvas の GameObject にこのコンポーネントをアタッチ
///   6. Inspector で m_LogText に上記 Text をアサイン
///
/// 【フィルタリング】
/// m_Filter に文字列を設定すると、その文字列を含むログだけを表示できる。
/// 例: "[SessionManager]" と設定すれば SessionManager のログのみ表示。
/// 空文字にするとすべてのログを表示する。
/// </summary>
public class DebugLogOverlay : MonoBehaviour
{
    [SerializeField]
    private Text m_LogText;

    [Tooltip("表示する最大行数")]
    [SerializeField]
    private int m_MaxLines = 12;

    [Tooltip("この文字列を含むログのみ表示。空文字ですべて表示。")]
    [SerializeField]
    private string m_Filter = "";

    private readonly Queue<string> m_LogQueue = new Queue<string>();

    void OnEnable()
    {
        Application.logMessageReceived += HandleLog;
    }

    void OnDisable()
    {
        Application.logMessageReceived -= HandleLog;
    }

    void HandleLog(string logString, string stackTrace, LogType type)
    {
        // フィルターが設定されている場合、含まれないログは無視
        if (!string.IsNullOrEmpty(m_Filter) && !logString.Contains(m_Filter))
            return;

        // ログタイプに応じてプレフィックスを付ける
        string prefix = type switch
        {
            LogType.Warning => "<color=yellow>[W]</color>",
            LogType.Error   => "<color=red>[E]</color>",
            LogType.Exception => "<color=red>[X]</color>",
            _               => "[I]",
        };

        m_LogQueue.Enqueue($"{prefix} {logString}");

        // 最大行数を超えたら古い行を削除
        while (m_LogQueue.Count > m_MaxLines)
            m_LogQueue.Dequeue();

        if (m_LogText != null)
            m_LogText.text = string.Join("\n", m_LogQueue);
    }
}
