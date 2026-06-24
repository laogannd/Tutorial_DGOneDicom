using UnityEngine;

namespace Dicom.Interaction
{
    // 用一个可抓取的平面手柄驱动 shader 裁剪平面，剖切点云查看内部
    // 平面方程传给全局变量 _DicomClipPlane，由 DicomPointCloud.shader 使用
    public class ClippingPlaneController : MonoBehaviour
    {
        [SerializeField] Transform _planeHandle;
        [SerializeField] bool _enabled = true;
        // 运行时生成裁切平面手柄的默认边长(米),无点云尺寸参考时用此值
        [SerializeField] float _defaultExtent = 0.3f;

        static readonly int _ClipPlaneId = Shader.PropertyToID("_DicomClipPlane");

        // 是否已存在裁切平面手柄
        public bool HasPlane => _planeHandle != null;

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

        // 在指定世界位置/法线生成或重定位裁切平面(单平面共享,已存在则只移动不重建)
        // worldNormal 对齐手柄 up,即裁切法线方向
        // 手柄建为场景根物体(parent=null),不挂在点云下:裁切平面世界固定独立,与设计一致
        // 根物体无父级缩放继承,无需 lossyScale 补偿;与点云非父子、刚体不嵌套,抓点云不再自转/抖动
        public void SpawnPlaneAt(Vector3 worldPos, Vector3 worldNormal)
        {
            if (_planeHandle == null)
            {
                var handleGo = ClipPlaneHandleBuilder.Build(null, ResolveExtent());
                _planeHandle = handleGo.transform;
            }

            _planeHandle.position = worldPos;
            _planeHandle.rotation = Quaternion.FromToRotation(Vector3.up, worldNormal.normalized);
            _enabled = true;
        }

        // 平面边长(米):优先按同物体点云 BoxCollider 的世界尺寸最大维度放大,使边框露在点云外可见
        // 无 collider(独立裁切平面)时回退默认值
        float ResolveExtent()
        {
            var box = GetComponent<BoxCollider>();
            if (box == null) return _defaultExtent;

            Vector3 ls = transform.lossyScale;
            float x = box.size.x * Mathf.Abs(ls.x);
            float y = box.size.y * Mathf.Abs(ls.y);
            float z = box.size.z * Mathf.Abs(ls.z);
            float maxDim = Mathf.Max(x, Mathf.Max(y, z));
            // 略大于模型,让边框露在点云外圈可见(1.15 系数)
            return maxDim > 1e-4f ? maxDim * 1.15f : _defaultExtent;
        }

        // 销毁裁切平面手柄,LateUpdate 自动回落到"永远通过"平面,点云恢复完整
        public void RemovePlane()
        {
            if (_planeHandle == null) return;
            Destroy(_planeHandle.gameObject);
            _planeHandle = null;
        }
    }
}
