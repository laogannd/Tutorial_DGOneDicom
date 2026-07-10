using UnityEngine;

namespace Dicom.Interaction
{
    // 点云模型远程射线操控的公共契约:DicomModelTransform 与 GeneModelTransform 均实现
    // 射线指针/操控器只依赖此接口,故一套挂在手上的指针能同时驱动 DICOM 与基因两种点云
    public interface IPointCloudManipulable
    {
        // 当前等比缩放(localScale.x),双手缩放以此为基准
        float CurrentScale { get; }

        // 世界空间平移(单手射线拖动)
        void TranslateWorld(Vector3 delta);

        // 绕世界轴点旋转(双手射线旋转)
        void RotateAroundWorld(Vector3 worldPivot, Quaternion delta);

        // 绕世界轴点等比缩放(双手射线缩放),内部 clamp 到各自 Min/Max
        void ScaleAroundWorld(Vector3 worldPivot, float targetScale);
    }
}
