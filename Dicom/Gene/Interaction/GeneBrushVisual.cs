using UnityEngine;

using Dicom.PointCloud;

namespace Dicom.Gene
{
    // 套索可视化:画圈期间用 LineRenderer 沿采样轨迹画出闭合圈(纯 transform 更新,零重建)
    // + 选中 overlay 高亮点云(第二个 DicomPointCloud,挂基因点云子物体,identity 局部变换,与主点云同一 local->world)
    // overlay 仅在选中集变化时重建一次,不每帧重建 136k 点
    [RequireComponent(typeof(GeneBrushSelector))]
    public class GeneBrushVisual : MonoBehaviour
    {
        [SerializeField] Color _lineColor = new Color(0.2f, 0.85f, 1f, 0.9f);
        [SerializeField] float _lineWidth = 0.004f;
        // overlay 高亮点用 colormap 顶端强度显示(1=最亮),点比主点云略大更醒目
        [SerializeField] float _overlayPointSize = 0.004f;

        GeneBrushSelector _brush;
        GeneColorController _controller;
        DicomPointCloud _mainCloud;

        LineRenderer _line;
        Material _lineMaterial;

        DicomPointCloud _overlay;

        static readonly int _BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int _ZTestId = Shader.PropertyToID("_ZTest");

        void Awake()
        {
            _brush = GetComponent<GeneBrushSelector>();
            _controller = GetComponent<GeneColorController>();
            _mainCloud = GetComponent<DicomPointCloud>();
            _brush.OnSelectionChanged += OnSelectionChanged;
        }

        void OnDestroy()
        {
            if (_brush != null) _brush.OnSelectionChanged -= OnSelectionChanged;
            if (_lineMaterial != null) Destroy(_lineMaterial);
        }

        // 选中集变化后重建 overlay 高亮(单次触发,频率低)
        // overlay 仅作画圈期实时反馈;画笔关闭后由 Update 清空,避免恒定强度盖住区域表达显色
        void OnSelectionChanged(int count)
        {
            if (!_brush.BrushEnabled) return;
            EnsureOverlay();
            _controller.BuildOverlay(_overlay, 1f);
            // 显色态改走每实例 property block,overlay 需复制主点云显色态才能用同一 colormap 高亮
            _controller.ApplyColorState(_overlay);
        }

        void Update()
        {
            if (!_brush.BrushEnabled)
            {
                SetLineActive(false);
                // 画笔关闭:清空 overlay 让主点云区域表达显色可见
                if (_overlay != null && _overlay.PointCount > 0)
                    _overlay.SetPoints(default, 0);
                return;
            }

            UpdateLine();
        }

        // 画圈中沿采样轨迹画闭合线,非画圈时隐藏
        void UpdateLine()
        {
            var traj = _brush.TrajectoryWorld;
            bool show = _brush.Drawing && traj.Count >= 2;
            EnsureLine();
            SetLineActive(show);
            if (!show) return;

            // 闭合:末尾补起点
            _line.positionCount = traj.Count + 1;
            for (int i = 0; i < traj.Count; i++) _line.SetPosition(i, traj[i]);
            _line.SetPosition(traj.Count, traj[0]);
        }

        // overlay 点云挂在子物体,identity 局部变换 -> 与主点云同一 local->world,cell local 坐标直接对齐
        void EnsureOverlay()
        {
            if (_overlay != null) return;

            var go = new GameObject("GeneSelectionOverlay");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            _overlay = go.AddComponent<DicomPointCloud>();
            // 复用主点云材质;显色态由 ApplyColorState 复制到 overlay 实例 property block
            // overlay 恒定强度=1 显示为 colormap 顶端色
            if (_mainCloud != null && _mainCloud.Material != null)
                _overlay.SetMaterial(_mainCloud.Material);
            _overlay.SetPointSize(_overlayPointSize);
        }

        void EnsureLine()
        {
            if (_line != null) return;
            if (_lineMaterial == null) _lineMaterial = CreateMaterial(_lineColor);

            var go = new GameObject("GeneLassoLine");
            go.transform.SetParent(transform, false);
            _line = go.AddComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.widthMultiplier = _lineWidth;
            _line.numCornerVertices = 2;
            _line.sharedMaterial = _lineMaterial;
            _line.positionCount = 0;
            _line.enabled = false;
        }

        void SetLineActive(bool active)
        {
            if (_line != null && _line.enabled != active) _line.enabled = active;
        }

        // URP Unlit 材质,内置管线回退;参考 DicomBoundingBoxVisualizer
        // ZTest Always + Overlay 队列:套索圈置顶渲染,不被点云遮挡
        static Material CreateMaterial(Color color)
        {
            var urp = Shader.Find("Universal Render Pipeline/Unlit");
            if (urp != null)
            {
                var mat = new Material(urp);
                mat.SetColor(_BaseColorId, color);
                MakeOverlay(mat);
                return mat;
            }

            var sprite = Shader.Find("Sprites/Default");
            if (sprite != null)
            {
                var mat = new Material(sprite) { color = color };
                MakeOverlay(mat);
                return mat;
            }
            var unlit = Shader.Find("Unlit/Color");
            if (unlit != null)
            {
                var mat = new Material(unlit) { color = color };
                MakeOverlay(mat);
                return mat;
            }

            Debug.LogWarning("套索线所需 shader 均被剥离(建议加入 Always Included Shaders)");
            return null;
        }

        // 置顶渲染:关闭深度测试(始终通过)并放到 Overlay 队列最后绘制,使圈盖在点云之上
        static void MakeOverlay(Material mat)
        {
            if (mat.HasProperty(_ZTestId))
                mat.SetInt(_ZTestId, (int)UnityEngine.Rendering.CompareFunction.Always);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay;
        }
    }
}
