using UnityEngine;
using UnityEngine.UI;

public class DebugTextPosition : MonoBehaviour
{
    [Header("監視するオブジェクト")]
    [SerializeField] private Transform targetTransform;
    
    [Header("表示先のテキスト")]
    [SerializeField] private Text debugText;
    
    [Header("表示設定")]
    [SerializeField] private bool showPosition = true;
    [SerializeField] private bool showRotation = false;
    [SerializeField] private bool showScale = false;
    [SerializeField] private int decimalPlaces = 2;

    private void Update()
    {
        if (targetTransform == null || debugText == null)
        {
            return;
        }

        UpdateDebugText();
    }

    private void UpdateDebugText()
    {
        string debugInfo = "";

        if (showPosition)
        {
            Vector3 pos = targetTransform.position;
            debugInfo += $"Position: ({pos.x.ToString($"F{decimalPlaces}")}, " +
                        $"{pos.y.ToString($"F{decimalPlaces}")}, " +
                        $"{pos.z.ToString($"F{decimalPlaces}")})\n";
        }

        if (showRotation)
        {
            Vector3 rot = targetTransform.eulerAngles;
            debugInfo += $"Rotation: ({rot.x.ToString($"F{decimalPlaces}")}, " +
                        $"{rot.y.ToString($"F{decimalPlaces}")}, " +
                        $"{rot.z.ToString($"F{decimalPlaces}")})\n";
        }

        if (showScale)
        {
            Vector3 scale = targetTransform.localScale;
            debugInfo += $"Scale: ({scale.x.ToString($"F{decimalPlaces}")}, " +
                        $"{scale.y.ToString($"F{decimalPlaces}")}, " +
                        $"{scale.z.ToString($"F{decimalPlaces}")})\n";
        }

        debugText.text = debugInfo.TrimEnd('\n');
    }
}
