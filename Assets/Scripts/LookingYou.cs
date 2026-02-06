using UnityEngine;
using UniVRM10;

public class LookingYou : MonoBehaviour
{
    [SerializeField] private Vrm10Instance vrmInstance;
    [SerializeField] private Transform target;

    [Header("Head Control")]
    [SerializeField] private bool enableHeadRotation = true;
    [SerializeField, Range(0f, 1f)] private float headRotationWeight = 0.3f; // 頭の回転の強さ
    [SerializeField] private float rotationSpeed = 5f; // 回転のスムーズさ

    private Transform headBone;
    private Quaternion originalHeadRotation;

    void Start()
    {
        // Humanoid の Head ボーンを取得
        if (vrmInstance != null)
        {
            var animator = vrmInstance.GetComponent<Animator>();
            if (animator != null)
            {
                headBone = animator.GetBoneTransform(HumanBodyBones.Head);
                if (headBone != null)
                {
                    originalHeadRotation = headBone.localRotation;
                }
            }
        }
    }

    void Update()
    {
        if (vrmInstance != null && vrmInstance.Runtime != null && target != null)
        {
            // ワールド座標から Yaw/Pitch を計算して設定（目の制御）
            // Y座標を反転して正しい方向を向くようにする
            Vector3 correctedPosition = target.position;
            Vector3 headPosition = vrmInstance.Runtime.LookAt.LookAtOriginTransform.position;
            correctedPosition.y = headPosition.y - (target.position.y - headPosition.y);

            vrmInstance.Runtime.LookAt.LookAtInput = new LookAtInput
            {
                WorldPosition = correctedPosition
            };
        }
    }

    void LateUpdate()
    {
        // 頭の回転を制御（VRM の処理後に実行）
        if (enableHeadRotation && headBone != null && target != null)
        {
            // ターゲット方向を計算
            Vector3 direction = target.position - headBone.position;
            if (direction.sqrMagnitude > 0.001f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(direction);
                Quaternion localTargetRotation = Quaternion.Inverse(headBone.parent.rotation) * targetRotation;

                // 元の回転とブレンド
                Quaternion blendedRotation = Quaternion.Slerp(originalHeadRotation, localTargetRotation, headRotationWeight);

                // スムーズに回転
                headBone.localRotation = Quaternion.Slerp(headBone.localRotation, blendedRotation, Time.deltaTime * rotationSpeed);
            }
        }
    }
}
