using UnityEngine;

namespace Dicom.Interaction
{
    // 用一个可抓取的平面手柄驱动 shader 裁剪平面，剖切点云查看内部
    // 平面方程传给全局变量 _DicomClipPlane，由 DicomPointCloud.shader 使用
    public class ClippingPlaneController : MonoBehaviour
    {
        [SerializeField] Transform _planeHandle;
        [SerializeField] bool _enabled = true;

        static readonly int _ClipPlaneId = Shader.PropertyToID("_DicomClipPlane");

        // 平面法线取手柄 up 方向，平面过手柄位置
        void LateUpdate()
        {
            if (!_enabled || _planeHandle == null)
            {
                // 关闭时给一个永远通过的平面(法线朝上，常数极大)
                Shader.SetGlobalVector(_ClipPlaneId, new Vector4(0f, 1f, 0f, 1e9f));
                return;
            }

            Vector3 n = _planeHandle.up.normalized;
            Vector3 p = _planeHandle.position;
            // 平面方程 dot(n, x) + d >= 0 保留正侧，d = -dot(n, p)
            float d = -Vector3.Dot(n, p);
            Shader.SetGlobalVector(_ClipPlaneId, new Vector4(n.x, n.y, n.z, d));
        }

        public void SetEnabled(bool on) => _enabled = on;
    }
}
