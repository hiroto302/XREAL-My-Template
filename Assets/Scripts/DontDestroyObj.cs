using UnityEngine;

public class DontDestroyObj : MonoBehaviour
{
    private static DontDestroyObj instance;
    
    public static DontDestroyObj Instance
    {
        get { return instance; }
    }
    
    void Awake()
    {
        // 既にインスタンスが存在する場合は破棄
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(gameObject);
    }
}
