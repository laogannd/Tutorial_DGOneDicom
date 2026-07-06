using UnityEngine;
using UnityEngine.Rendering;
using Autohand;

namespace Dicom.Interaction
{
    // 运行时构建可抓取裁切平面手柄,供 UI 运行时生成与编辑器工厂共用
    // 根的 up 方向即裁切法线,与 ClippingPlaneController.LateUpdate 取法一致
    // 外观:中心大面积透明(可透视点云内部),仅四边高亮镂空框示意可抓取
    public static class ClipPlaneHandleBuilder
    {
        const float HandleThickness = 0.012f;   // 抓取碰撞盒厚度
        const float PanelThickness = 0.002f;     // 中心透明面板厚度
        const float FrameThickness = 0.004f;     // 边框条厚度(略厚于面板,清晰可见)
        const float BarWidthRatio = 0.05f;        // 边框条宽度占边长比例

        // 中心面板:淡青色半透,适度可见示意平面朝向,又不过度遮挡点云内部
        static readonly Color PanelColor = new Color(0.18f, 0.78f, 0.85f, 0.2f);
        // 高亮青色边框,不透明,与 DICOM 操作面板 Accent 一致,示意可抓取
        static readonly Color FrameColor = new Color(0.18f, 0.78f, 0.85f, 1f);

        // 在 parent 下创建裁切平面手柄,extent 为平面边长(米)
        public static GameObject Build(Transform parent, float extent)
        {
            var handle = new GameObject("ClipPlaneHandle");
            handle.transform.SetParent(parent, false);
            handle.transform.localPosition = Vector3.zero;
            // 抓取层缺失时 NameToLayer 返回 -1，赋给 layer 会抛异常，守卫后跳过
            int grabLayer = LayerMask.NameToLayer(Hand.grabbableLayerNameDefault);
            if (grabLayer >= 0) handle.layer = grabLayer;
            else Debug.LogError($"未定义抓取层 '{Hand.grabbableLayerNameDefault}'，裁切平面手柄无法被抓取");

            var body = handle.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;

            // 抓取碰撞盒覆盖整个平面,厚度方向沿 up(裁切法线)
            var col = handle.AddComponent<BoxCollider>();
            col.size = new Vector3(extent, HandleThickness, extent);

            var grab = handle.AddComponent<Grabbable>();
            grab.body = body;
            grab.singleHandOnly = false;
            grab.instantGrab = false;

            // 抓取时解锁 Kinematic,脱手后复位
            handle.AddComponent<GrabKinematicLock>();

            // 材质持有者:手柄销毁时统一 Destroy 运行时创建的材质，避免 sharedMaterial 泄漏
            var owner = handle.AddComponent<ClipPlaneMaterialOwner>();

            BuildVisual(handle.transform, extent, owner);
            return handle;
        }

        // 中心透明面板 + 四根高亮条拼成的镂空框,中心透视点云,只有边框可见
        static void BuildVisual(Transform handle, float extent, ClipPlaneMaterialOwner owner)
        {
            // 中心面板:大面积透明,仅淡淡示意平面朝向(单独一份透明材质)
            var panelMat = owner.Register(CreateMaterial(PanelColor, true));
            CreateSlab("Panel", handle, new Vector3(extent, PanelThickness, extent), Vector3.zero, panelMat);

            // 四边高亮条:上下沿 X 铺满,左右沿 Z 铺满,角点重叠不影响观感
            // 四条共用同一份 Frame 材质，省 3 份材质分配
            var frameMat = owner.Register(CreateMaterial(FrameColor, false));
            float bar = extent * BarWidthRatio;
            float half = extent * 0.5f - bar * 0.5f;
            CreateSlab("Frame_Top", handle, new Vector3(extent, FrameThickness, bar), new Vector3(0f, 0f, half), frameMat);
            CreateSlab("Frame_Bottom", handle, new Vector3(extent, FrameThickness, bar), new Vector3(0f, 0f, -half), frameMat);
            CreateSlab("Frame_Left", handle, new Vector3(bar, FrameThickness, extent), new Vector3(-half, 0f, 0f), frameMat);
            CreateSlab("Frame_Right", handle, new Vector3(bar, FrameThickness, extent), new Vector3(half, 0f, 0f), frameMat);
        }

        // 用扁平 Cube 做一层薄板,移除碰撞体避免干扰抓取检测
        static GameObject CreateSlab(string name, Transform parent, Vector3 size, Vector3 localPos, Material material)
        {
            var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            slab.name = name;
            // Cube 自带 BoxCollider,薄板不需要碰撞,移除避免干扰手柄抓取检测。运行时用 Destroy
            var c = slab.GetComponent<Collider>();
            if (c != null) Object.Destroy(c);

            slab.transform.SetParent(parent, false);
            slab.transform.localPosition = localPos;
            slab.transform.localRotation = Quaternion.identity;
            slab.transform.localScale = size;

            slab.GetComponent<MeshRenderer>().sharedMaterial = material;
            return slab;
        }

        // URP 项目用 Universal Unlit,内置管线回退;transparent 时切到透明混合模式
        // 所需 shader 全部缺失(被剥离)时返回 null，调用方按无材质处理，不构造 new Material(null) 崩溃
        static Material CreateMaterial(Color color, bool transparent)
        {
            var urp = Shader.Find("Universal Render Pipeline/Unlit");
            if (urp != null)
            {
                var mat = new Material(urp);
                mat.SetColor("_BaseColor", color);
                if (transparent) SetUrpTransparent(mat);
                return mat;
            }

            // 内置管线回退:透明用支持 alpha 的 Sprites/Default(支持顶点色+alpha)
            if (transparent)
            {
                var sprite = Shader.Find("Sprites/Default");
                if (sprite != null) return new Material(sprite) { color = color };
            }
            var unlit = Shader.Find("Unlit/Color");
            if (unlit != null) return new Material(unlit) { color = color };

            Debug.LogWarning("裁切平面手柄所需 shader 均被剥离，改用无材质(建议加入 Always Included Shaders)");
            return null;
        }

        // 切换 URP Unlit 到透明表面模式:SrcAlpha/OneMinusSrcAlpha 混合,关 ZWrite,排到透明队列
        static void SetUrpTransparent(Material mat)
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_SURFACE_TYPE_OPAQUE");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)RenderQueue.Transparent;
        }
    }
}
