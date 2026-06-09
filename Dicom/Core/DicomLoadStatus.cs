namespace Dicom.Core
{
    // 加载管线阶段，调试系统据此显示当前进行到哪一步、失败在哪
    public enum DicomLoadPhase
    {
        Idle,           // 未开始
        Scanning,       // 扫描目录收集文件
        Parsing,        // 逐文件解析切片
        Sorting,        // 按位置排序切片
        Assembling,     // 组装三维体素
        BuildingPoints, // 体素转点云
        Completed,      // 全部完成
        Failed          // 任意阶段失败
    }

    // 主线程持有的加载诊断快照，供调试面板直接读取
    // 后台线程只回填 Progress 标志位，主线程在 Update 内合并到此对象
    public class DicomLoadReport
    {
        public DicomLoadPhase Phase = DicomLoadPhase.Idle;

        // 文件解析进度
        public int FilesDone;
        public int FilesTotal;
        public string CurrentFile = "";

        // 加载成功后的体数据信息
        public int Width;
        public int Height;
        public int Depth;
        public int PointCount;

        // 耗时统计(秒)，解析+组装为 Load，体素转点为 Build
        public float LoadSeconds;
        public float BuildSeconds;

        // 失败时的错误信息，成功时为空
        public string ErrorMessage = "";
        public string ErrorStack = "";

        // 文件进度比例 0..1，Total 为 0 时返回 0
        public float FileRatio => FilesTotal > 0 ? (float)FilesDone / FilesTotal : 0f;

        public bool HasError => Phase == DicomLoadPhase.Failed;

        public string PhaseText
        {
            get
            {
                switch (Phase)
                {
                    case DicomLoadPhase.Idle: return "空闲";
                    case DicomLoadPhase.Scanning: return "扫描目录";
                    case DicomLoadPhase.Parsing: return "解析切片";
                    case DicomLoadPhase.Sorting: return "排序切片";
                    case DicomLoadPhase.Assembling: return "组装体素";
                    case DicomLoadPhase.BuildingPoints: return "生成点云";
                    case DicomLoadPhase.Completed: return "完成";
                    case DicomLoadPhase.Failed: return "失败";
                    default: return "未知";
                }
            }
        }

        public void Reset()
        {
            Phase = DicomLoadPhase.Idle;
            FilesDone = 0;
            FilesTotal = 0;
            CurrentFile = "";
            Width = Height = Depth = 0;
            PointCount = 0;
            LoadSeconds = 0f;
            BuildSeconds = 0f;
            ErrorMessage = "";
            ErrorStack = "";
        }
    }
}
