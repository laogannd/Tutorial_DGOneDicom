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
            handle.layer = LayerMask.NameToLayer(Hand.grabbableLayerNameDefault);

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

            BuildVisual(handle.transform, extent);
            return handle;
        }

        // 中心透明面板 + 四根高亮条拼成的镂空框,中心透视点云,只有边框可见
        static void BuildVisual(Transform handle, float extent)
        {
            // 中心面板:大面积透明,仅淡淡示意平面朝向
            CreateSlab("Panel", handle, new Vector3(extent, PanelThickness, extent), Vector3.zero, PanelColor, true);

            // 四边高亮条:上下沿 X 铺满,左右沿 Z 铺满,角点重叠不影响观感
            float bar = extent * BarWidthRatio;
            float half = extent * 0.5f - bar * 0.5f;
            CreateSlab("Frame_Top", handle, new Vector3(extent, FrameThickness, bar), new Vector3(0f, 0f, half), FrameColor, false);
            CreateSlab("Frame_Bottom", handle, new Vector3(extent, FrameThickness, bar), new Vector3(0f, 0f, -half), FrameColor, false);
            CreateSlab("Frame_Left", handle, new Vector3(bar, FrameThickness, extent), new Vector3(-half, 0f, 0f), FrameColor, false);
            CreateSlab("Frame_Right", handle, new Vector3(bar, FrameThickness, extent), new Vector3(half, 0f, 0f), FrameColor, false);
        }

        // 用扁平 Cube 做一层薄板,移除碰撞体避免干扰抓取检测;transparent 控制材质混合模式
        static GameObject CreateSlab(string name, Transform parent, Vector3 size, Vector3 localPos, Color color, bool transparent)
        {
            var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            slab.name = name;
            // Cube 自带 BoxCollider,薄板不需要碰撞,移除避免干扰手柄抓取检测
            var c = slab.GetComponent<Collider>();
            if (c != null) Object.DestroyImmediate(c);

            slab.transform.SetParent(parent, false);
            slab.transform.localPosition = localPos;
            slab.transform.localRotation = Quaternion.identity;
            slab.transform.localScale = size;

            slab.GetComponent<MeshRenderer>().sharedMaterial = CreateMaterial(color, transparent);
            return slab;
        }

        // URP 项目用 Universal Unlit,内置管线回退;transparent 时切到透明混合模式
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

            // 内置管线回退:透明用支持 alpha 的 Unlit/Transparent 系不便设纯色,改用 Sprites/Default(支持顶点色+alpha)
            if (transparent)
            {
                var sprite = Shader.Find("Sprites/Default");
                if (sprite != null) return new Material(sprite) { color = color };
            }
            return new Material(Shader.Find("Unlit/Color")) { color = color };
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
