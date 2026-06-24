# DICOM 点云系统用户手册

版本: 260611.0  
适用模块: `Assets/!!Workspace/_Workspace/Script/Dicom`  
英文版: [README_EN.md](README_EN.md)  
适用读者: Unity/VR 场景搭建人员、训练内容集成者、测试人员

---

## 10 秒内先看这里

**这是什么？**  
一个 Unity DICOM 点云可视化插件：从本地 DICOM 切片目录加载 CT/MRI 序列，解析体数据，转换为 GPU 点云，并在 Built-in 或 URP 管线中显示。

**为什么我需要它？**  
它让 VR 培训或演示场景能快速查看三维医学影像体数据，支持抓取、双手缩放、自动适配尺寸、裁切、窗宽窗位、阈值过滤、伪彩、断点色带、HU 分类和 HU 区间分析，而不需要为每个体素创建 GameObject。

**怎么开始用？**  
把同一序列的 `.dcm` 文件放到 `Application.persistentDataPath/dicom`，创建使用 `Dicom/PointCloud` Shader 的材质，在场景空物体上挂 `DicomDemoBootstrap` 并指定材质，进入 Play Mode。

---

## 1. 文档范围与标准符合性

本手册按 ISO/IEC/IEEE 26514:2022 的用户文档原则组织，提供完成任务所需的信息：产品目的、目标用户、前置条件、快速开始、任务步骤、配置参考、系统反馈、故障恢复、安全注意事项、限制、验收检查和版本记录。

本手册描述当前代码实现，不包含医学诊断流程、影像标注规范、医院 PACS 接入流程或临床合规说明。点云显示仅用于培训、演示和可视化验证，不应作为医学诊断依据。

## 2. 系统概览

| 模块 | 主要文件 | 作用 |
|---|---|---|
| DICOM 解析 | `Core/DicomParser.cs`, `Core/DicomSeriesLoader.cs`, `Core/DicomDataset.cs` | 后台扫描目录，解析 Part 10 DICOM，读取尺寸、像素间距、层厚、窗宽窗位、重标定参数、方向信息和 16bit 像素数据，并组装三维体数据 |
| 堆叠轴检测 | `Core/DicomSeriesLoader.cs`, `PointCloud/PointCloudController.cs` | 根据 `ImageOrientationPatient` 与切片位置检测 X/Y/Z 堆叠轴，减少冠状、矢状序列按默认 Z 轴重建导致的方向错误 |
| 加载诊断 | `Core/DicomLoadStatus.cs`, `PointCloud/PointCloudController.cs` | 维护加载阶段、文件进度、耗时、体数据尺寸、点数和错误信息 |
| 点云生成 | `PointCloud/VoxelToPointJob.cs`, `PointCloud/PointCloudController.cs` | 使用 Burst Job 两遍式统计和写入点云，按阈值筛选体素，按归一化范围生成强度 |
| 点云渲染 | `PointCloud/DicomPointCloud.cs`, `Shaders/DicomPointCloud.shader` | 使用 `ComputeBuffer` 和 `Graphics.DrawProcedural` 渲染 billboard 点或点图元，支持 URP 与 Built-in SubShader |
| 显色配置 | `Core/DicomClassificationProfile.cs`, `Core/DicomLutProfile.cs`, `Core/DicomBreakpointProfile.cs` | 支持灰度强度、HU 分类调色板、离散 LUT 伪彩、真实值断点插值色带四种显色模式 |
| HU 分析 | `Analysis/HuRangeAnalyzer.cs`, `Analysis/HuRangeReport.cs` | 加载完成后统计 HU 直方图，自动识别占用区间，并可写入分类 Profile |
| VR 交互 | `Interaction/DicomGrabbableSetup.cs`, `Interaction/TwoHandScaler.cs`, `Interaction/DicomModelTransform.cs` | 自动补齐 AutoHand 抓取组件，支持双手缩放、米级尺寸适配、模型缩放滑条和一键复位 |
| 裁切与调窗 | `Interaction/WindowLevelController.cs`, `Interaction/ClippingPlaneController.cs`, `Interaction/Editor/ClippingPlaneFactory.cs` | 实时控制窗宽窗位和裁切平面；裁切手柄可由菜单或运行时按钮生成/移除 |
| 调试与 UI | `Demo/DicomDebugPanel.cs`, `UI/DicomPanelUI.cs`, `UI/Editor/DicomPanelFactory*.cs`, `UI/PokeSlider.cs`, `UI/PokeScrollbar.cs` | 提供 IMGUI 调试面板和世界空间 VR 操作面板，支持触碰滑块、粗滚动条、显色切换、重建轴切换和 HU 区间应用 |

## 3. 使用前准备

### 3.1 Unity 与插件要求

- Unity 项目中已包含 Jobs、Burst、Collections、Mathematics。
- 目标设备和渲染管线支持 Shader Model 4.5、`StructuredBuffer` 和程序化绘制。
- Shader 提供 URP 与 Built-in 两个 SubShader；HDRP 未在当前代码中声明专用 SubShader。
- VR 抓取、触碰滑块、触碰滚动条和面板手交互依赖 AutoHand。未安装 AutoHand 时，`Interaction` 和 `UI/PokeSlider.cs` 相关脚本无法正常编译或使用。
- 世界空间 UI 使用 UGUI、TextMeshPro、`GraphicRaycaster`、`ScrollRect` 和 AutoHand 触碰组件。

### 3.2 DICOM 数据要求

当前支持：

- 标准 DICOM Part 10 文件，128 字节前导后包含 `DICM` 标识。
- 文件扩展名为 `.dcm` 或无扩展名。
- 非压缩 Little Endian 16bit 像素数据。
- 显式 VR Little Endian 和隐式 VR Little Endian。
- 同一目录只包含同一序列，且每张切片 Rows 和 Columns 一致。
- 优先按 `ImageOrientationPatient` 与 `ImagePositionPatient` 排序；缺少方向信息时使用 `ImagePositionPatient.z`，再退回 `InstanceNumber`。

当前不支持：

- JPEG、JPEG2000、RLE 等压缩或封装像素数据。
- 8bit、12bit 打包、32bit 等非 16bit 像素格式。
- 递归扫描子目录。
- 多序列混放后自动按 Study/Series UID 分组。
- 直接从 PACS、DICOMweb 或网络地址加载。

### 3.3 数据安全

DICOM 文件可能包含患者姓名、ID、检查日期等敏感信息。用于培训、演示或测试前，应先完成脱敏处理，并确认数据存放路径、设备拷贝方式和日志输出符合项目的数据管理要求。

## 4. 快速开始

### 4.1 准备 DICOM 文件

将同一序列的 DICOM 切片放入一个目录。Demo 默认读取：

```text
Application.persistentDataPath/dicom
```

常见位置示例：

| 平台 | 典型路径 |
|---|---|
| Windows Editor | `%USERPROFILE%\AppData\LocalLow\<CompanyName>\<ProductName>\dicom` |
| Android/Pico | `/sdcard/Android/data/<package-name>/files/dicom` |

Pico/Android 测试时可用：

```bash
adb push <本地DICOM目录> /sdcard/Android/data/<package-name>/files/dicom
```

### 4.2 创建点云材质

1. 在 Unity Project 面板中新建 Material。
2. Shader 选择 `Dicom/PointCloud`。
3. 将材质命名为 `DicomPointCloudMaterial`。
4. 按设备性能调整 `_PointSize`，默认点大小为 `0.002`。

### 4.3 使用 Demo 启动

1. 在场景中新建空物体，命名为 `DicomDemo`。
2. 添加组件 `DicomDemoBootstrap`。
3. 保持 `Relative Dir` 为 `dicom`，或改成你的相对目录名。
4. 将 `DicomPointCloudMaterial` 拖到 `Point Material`。
5. 可选：将 `DicomClassificationProfile`、`DicomLutProfile`、`DicomBreakpointProfile` 拖到对应字段。
6. 勾选 `Auto Load On Start`，需要屏幕调试时保留 `Attach Debug Panel`。
7. 进入 Play Mode。

加载成功后，Demo 会创建 `DicomPointCloud` 子物体，并挂载 `DicomPointCloud`、`PointCloudController`、`DicomGrabbableSetup`、`DicomModelTransform`、`TwoHandScaler`、`WindowLevelController` 和 `ClippingPlaneController`。Console 会输出加载进度，调试面板会显示阶段、文件进度、尺寸、点数和耗时。

## 5. 常见任务

### 5.1 在代码中加载指定目录

```csharp
using UnityEngine;
using Dicom.PointCloud;

public class DicomLoaderExample : MonoBehaviour
{
    [SerializeField] PointCloudController _controller;
    [SerializeField] string _directory;

    public void Load()
    {
        _controller.Load(_directory);
    }
}
```

常用事件：

```csharp
_controller.OnProgress += ratio => Debug.Log($"进度: {ratio:P0}");
_controller.OnLoaded += dataset => Debug.Log($"{dataset.Width}x{dataset.Height}x{dataset.Depth}");
_controller.OnError += error => Debug.LogError(error.Message);
_controller.OnReportChanged += report => Debug.Log(report.PhaseText);
_controller.OnHuAnalyzed += report => Debug.Log($"HU 区间数: {report.Segments.Count}");
```

### 5.2 手动搭建正式场景对象

适合正式场景，避免依赖 Demo 中为了便利而使用的反射绑定。

```text
DicomPointCloudRoot
├─ DicomPointCloud         指定点云材质
├─ PointCloudController    控制加载、阈值、归一化、显色配置和点云生成
├─ DicomGrabbableSetup     可选，加载完成后创建抓取碰撞盒
├─ DicomModelTransform     可选，加载后自动适配尺寸并支持复位
├─ TwoHandScaler           可选，双手抓取时缩放
├─ WindowLevelController   可选，实时调窗
└─ ClippingPlaneController 可选，裁切平面控制
```

调用 `PointCloudController.Load(directory)` 开始加载。

### 5.3 调整阈值、归一化和重建轴

阈值决定哪些真实像素值进入点云；归一化决定进入点云后的强度映射；重建轴决定切片堆叠方向映射到 X/Y/Z 哪个轴。

```csharp
_controller.SetThreshold(200f, 1200f);
_controller.SetNormalize(200f, 1500f);
_controller.SetReconstructAxis(DicomReconstructAxis.Z);
_controller.Rebuild();
```

这些操作会重新过滤体素并重建点云，开销较大。UI 滑条应在用户点击 `Apply` 或明确切换轴后调用，不应每帧调用。加载完成时系统会根据 DICOM 元数据自动设置一次重建轴，必要时可在面板中手动循环切换 X/Y/Z。

### 5.4 实时调整显示外观

窗宽窗位、点大小、强度增益、色调和显色模式切换不会重建点云，适合实时拖动。

```csharp
_windowLevel.SetWindow(0.45f, 0.35f);
_pointCloud.SetPointSize(0.003f);
Shader.SetGlobalVector(Shader.PropertyToID("_DicomTint"), new Vector4(1f, 0.9f, 0.85f, 1.2f));
_controller.SetColorMode(DicomColorMode.Lut);
```

### 5.5 使用分类、LUT 和断点显色

支持四种显色模式：

| 模式 | 用途 | 准备 |
|---|---|---|
| `Intensity` | 灰度强度显示 | 无需 Profile |
| `Classification` | 按 HU 区间显示组织类别 | 绑定 `DicomClassificationProfile` |
| `Lut` | 对窗宽窗位后的强度做离散伪彩 | 绑定 `DicomLutProfile` |
| `Breakpoint` | 按真实值断点线性插值色带 | 绑定 `DicomBreakpointProfile` |

分类 Profile：

1. 在 Project 面板中执行 `Assets > Dicom > 创建 CT 默认分类配置`。
2. 将生成的 `CtClassificationProfile.asset` 拖到 `DicomDemoBootstrap.Classification Profile` 或 `PointCloudController`。
3. 在 `DicomDebugPanel` 或 VR 操作面板中启用分类显色。

LUT Profile：

1. 使用 `Create > Dicom > LUT Profile` 创建资产，或使用 `Dicom/Data/DicomLutProfile.asset`。
2. 选择 `HotIron`、`Rainbow`、`Bone`、`GrayInverse` 或 `Custom`。
3. 在运行时可通过面板按钮循环内置 LUT 预设。

断点 Profile：

1. 执行 `Assets > Dicom > 创建示例断点配置`。
2. 将生成的 `DicomBreakpointProfile.asset` 绑定到 Demo 或 Controller。
3. 在 Inspector 中按真实值调整 Stop 列表，系统会按 Value 排序并烘焙 1D 色带。

### 5.6 使用 HU 区间分析

加载完成后，`PointCloudController` 会自动运行 HU 直方图分析并触发 `OnHuAnalyzed`。调试面板和 VR 操作面板会显示识别到的占用区间、体素数和占比。

如已绑定 `DicomClassificationProfile`，可调用：

```csharp
bool ok = _controller.ApplyDetectedRangesToProfile();
```

成功后，自动识别出的区间会覆盖当前分类 Profile，颜色按 HSV 分布生成，并重建点云。建议之后在 Inspector 中微调分类名称、区间和颜色。

### 5.7 使用 VR 操作面板

有两种编辑器菜单：

- `GameObject > Dicom > 创建 VR 操作面板`：在当前场景生成世界空间面板。
- `GameObject > Dicom > 创建 VR 操作面板并存为预制体`：生成面板并保存到 `Assets/!!Workspace/_Workspace/Script/Dicom/Prefabs`。

面板包含加载状态、进度条、点大小、窗宽窗位、增益、RGB 色调、阈值、归一化、重建方向、重建按钮、模型缩放、复位、裁切生成/清除、显色模式开关（分类/LUT/断点）、LUT 预设切换和 HU 区间分析。

面板运行特性：

- `PokeSlider` 会自动为滑块配置 `BoxCollider` 与 AutoHand `HandTouchEvent`，手指触碰可推滑，射线拖动仍可使用。
- `PokeScrollbar` 让右侧粗滚动条可被 VR 手指推动。
- `DicomPanelGrabHandle` 把顶部标题条作为可抓取区域，可移动整个面板。
- `Auto Bind Data Source` 默认开启；面板未显式绑定时会全场景查找 `PointCloudController`，查不到则按 `Auto Bind Retry Interval` 重试，直到 `Auto Bind Timeout`。
- `Global Font` 可统一面板内 TextMeshPro 字体；中文显示需指定中文 TMP 字体。

### 5.8 抓取、双手缩放、复位与裁切

`DicomGrabbableSetup` 在数据加载完成后会按体数据物理尺寸设置 `BoxCollider`，添加或复用 `Rigidbody` 和 AutoHand `Grabbable`，并允许双手同时抓取。

`DicomModelTransform` 会把体数据最大维度自动适配到米级目标尺寸，记录加载时的 Home 位姿，并提供 `ResetTransform()` 复位位置、旋转、缩放和刚体速度。

`TwoHandScaler` 在同一个 `Grabbable` 被两只手抓住时，根据两手掌心距离变化等比缩放。默认缩放范围基于 `DicomModelTransform` 的适配缩放约束。

裁切创建方式：

- 菜单 `GameObject > Dicom > 为点云创建裁切平面`：选中带 `PointCloudController` 的点云后创建并绑定手柄。
- 菜单 `GameObject > Dicom > 创建裁切平面(独立)`：不依赖点云，先创建独立裁切平面。
- VR 面板按钮：在点云中心生成裁切平面，或清除已有手柄。

代码示例：

```csharp
_clippingPlane.SpawnPlaneAt(_controller.transform.position, Vector3.up);
_clippingPlane.SetEnabled(false);
_clippingPlane.RemovePlane();
```

## 6. 配置参考

| 组件 | 字段/API | 默认值 | 说明 |
|---|---|---:|---|
| `DicomDemoBootstrap` | `Relative Dir` | `dicom` | 相对于 `Application.persistentDataPath` 的 DICOM 目录 |
| `DicomDemoBootstrap` | `Point Material` | 空 | 使用 `Dicom/PointCloud` Shader 的材质 |
| `DicomDemoBootstrap` | `Classification Profile` | 空 | 可选 CT/HU 分类配置 |
| `DicomDemoBootstrap` | `Lut Profile` | 空 | 可选离散伪彩 LUT 配置 |
| `DicomDemoBootstrap` | `Breakpoint Profile` | 空 | 可选真实值断点色带配置 |
| `DicomDemoBootstrap` | `Auto Load On Start` | `true` | `Start()` 时自动加载默认目录 |
| `DicomDemoBootstrap` | `Attach Debug Panel` | `true` | 自动添加并绑定 IMGUI 调试面板 |
| `DicomPointCloud` | `Material` | 空 | 点云渲染材质 |
| `DicomPointCloud` | `Point Size` | `0.002` | billboard 点大小 |
| `DicomPointCloud` | `Use Billboard Quads` | `true` | `true` 使用面向相机的 quad，`false` 使用点图元 |
| `PointCloudController` | `Threshold Min/Max` | `200/3000` | 进入点云的真实像素值范围 |
| `PointCloudController` | `Normalize Min/Max` | `200/1500` | 强度归一化范围 |
| `PointCloudController` | `Reconstruct Axis` | `Z` | 加载完成后会按 DICOM 方向信息自动更新 |
| `PointCloudController` | `Classification/Lut/Breakpoint Profile` | 空 | 三类可选显色配置 |
| `WindowLevelController` | `Window Center/Width` | `0.5/1` | 归一化显示窗位和窗宽 |
| `DicomModelTransform` | `Target World Size` | `0.5` | 加载后模型最大维度目标尺寸，单位米 |
| `DicomModelTransform` | `Min Scale/Max Scale` | `0.0002/0.05` | 模型缩放限制 |
| `DicomGrabbableSetup` | `Kinematic When Idle` | `true` | 空闲时刚体是否为 Kinematic |
| `DicomGrabbableSetup` | `Mass` | `1` | 自动创建 Rigidbody 时的质量 |
| `TwoHandScaler` | `Min Scale/Max Scale` | `0.1/10` | 相对适配缩放的双手缩放倍率限制 |
| `ClippingPlaneController` | `Plane Handle` | 空 | 作为裁切平面的 Transform |
| `ClippingPlaneController` | `Default Extent` | `0.3` | 运行时生成裁切平面手柄的默认边长，单位米 |
| `DicomDebugPanel` | `Visible` | `true` | 是否显示屏幕调试面板 |
| `DicomDebugPanel` | `Toggle Key` | `F1` | 切换调试面板显隐 |
| `DicomPanelUI` | `Auto Bind Data Source` | `true` | 未绑定时自动查找点云 Controller |
| `DicomPanelUI` | `Global Font` | 空 | 运行时统一面板 TMP 字体 |
| `PokeSlider` | `Collider Depth` | `0.02` | 触碰滑块碰撞体厚度，单位米 |

## 7. 系统反馈

| 状态 | 反馈 |
|---|---|
| 扫描目录 | `DicomLoadReport.Phase = Scanning`，当前文件为目录路径 |
| 解析切片 | `OnProgress` 返回 `0..1`，`OnReportChanged` 提供 `FilesDone/FilesTotal/CurrentFile` |
| 排序与组装 | `PhaseText` 显示排序切片或组装体素 |
| 生成点云 | 报告体数据尺寸、点数和建点耗时 |
| HU 分析完成 | `OnHuAnalyzed` 返回 `HuRangeReport`，面板列出区间、体素数和占比 |
| 加载成功 | `OnLoaded` 返回 `DicomDataset` |
| 加载失败 | `OnError` 返回异常，调试面板红框显示阶段和错误信息 |
| 阈值后无点 | Console 输出警告，调试面板提示放宽阈值 |
| 面板未绑定数据源 | 自动重试，超时后 Console 输出警告 |

## 8. 故障排除

**Q: Play 后没有看到点云。**  
A: 检查 `Point Material` 是否设置、Shader 是否为 `Dicom/PointCloud`、相机是否能看到点云、Console 是否提示目录不存在或无 `.dcm` 文件。也可以调大 `Point Size` 或放宽阈值。

**Q: 报错“DICOM 目录不存在”。**  
A: 确认数据是否放在 `Application.persistentDataPath` 下的相对目录中。Demo 默认是 `dicom`。

**Q: 报错“目录内无 .dcm 文件”。**  
A: 当前只扫描目录第一层，只接受 `.dcm` 或无扩展名文件。不要把切片放在子目录里。

**Q: 报错“缺少 DICM 魔数”。**  
A: 文件不是标准 DICOM Part 10 文件，或导出工具移除了文件头。请重新导出标准 DICOM 文件。

**Q: 报错“仅支持 16bit 像素”。**  
A: 当前解析器只支持 `BitsAllocated = 16`。需要先转换数据格式，或扩展解析器。

**Q: 报错“像素数据为压缩/封装格式”。**  
A: 当前不支持压缩 DICOM。请导出未压缩 Little Endian 序列。

**Q: 点云方向不对。**  
A: 系统会优先按 DICOM 方向信息检测堆叠轴。若原始数据方向标签缺失或异常，可在调试面板或 VR 面板中切换 `Reconstruct Axis` 后重建。

**Q: 点云太稀或完全为空。**  
A: 阈值范围可能不匹配数据。先设置较宽范围确认数据能显示，再逐步收窄。

**Q: 明暗不合适。**  
A: 调整窗宽窗位；如果强度分布本身不合适，再调整 `Normalize Min/Max` 并 Apply 重建。

**Q: 分类、LUT 或断点显色没有变化。**  
A: 确认已绑定对应 Profile，并启用了对应显色开关。分类模式还要求阈值范围内的 HU 值命中分类区间。

**Q: HU 区间无法写入 Profile。**  
A: 需要先完成加载和 HU 分析，并且 `PointCloudController` 已绑定 `DicomClassificationProfile`。

**Q: VR 中无法抓取点云。**  
A: 检查 AutoHand 是否安装，点云是否加载完成，`DicomGrabbableSetup` 是否已创建碰撞盒，手部 Layer 和 AutoHand 抓取设置是否正确。

**Q: VR 操作面板滑块或滚动条不能触碰拖动。**  
A: 确认滑块物体上存在 `PokeSlider`/`PokeScrollbar`、`BoxCollider` 和 AutoHand `HandTouchEvent`，手部碰撞层能触发触碰事件。

**Q: 世界空间面板不显示数据。**  
A: 确认场景中存在 `PointCloudController`。如果点云由 Demo 运行时创建，保持 `Auto Bind Data Source` 开启，并确认未超过自动绑定超时时间。

## 9. 操作限制与风险

- 本系统用于训练和可视化，不提供医学诊断能力。
- 大体数据可能产生大量点，造成内存和 GPU 压力。优先用阈值减少点数。
- `DicomParser` 使用静态临时像素缓存；当前加载流程按单序列后台任务设计，不要并发加载多个 DICOM 目录。
- 窗宽窗位、裁切、色调、分类色板、LUT 和断点纹理使用 Shader 全局变量；同场景多个 DICOM 点云会共享这些显示参数。
- `DicomDemoBootstrap.Load` 每次调用都会创建新的 `DicomPointCloud` 子物体；重复加载前应由业务代码管理旧对象生命周期。
- 当前加载器不递归扫描子目录，也不做多序列自动分组。
- `ApplyDetectedRangesToProfile()` 在编辑器 Play Mode 下会标记并保存分类资产；用于正式流程前应确认是否允许运行时改写 Profile。

## 10. 验收检查表

| 检查项 | 通过标准 |
|---|---|
| 数据路径 | 目标平台的 `persistentDataPath` 下存在 DICOM 目录 |
| 数据合规 | 测试数据已脱敏，且为未压缩 16bit Part 10 DICOM |
| 材质 | 点云材质使用 `Dicom/PointCloud` Shader |
| 加载反馈 | Console 或面板能显示阶段、进度、尺寸、点数和耗时 |
| 显示 | 点云出现在相机视野内，点大小和明暗可调 |
| 方向 | 横断、冠状、矢状序列的堆叠轴可自动检测或手动切换修正 |
| 性能 | 目标设备帧率可接受，无持续 GC 或频繁重建点云 |
| VR 交互 | 抓取、释放、双手缩放、复位符合预期 |
| 面板 | 世界空间面板可见，按钮、开关、滑块和滚动条可操作 |
| 显色 | 分类、LUT、断点显色可绑定、切换并显示差异 |
| HU 分析 | 加载完成后能显示占用 HU 区间，并可按需写入分类 Profile |
| 裁切 | 裁切手柄移动后能显示内部结构，清除后点云恢复完整 |

## 11. 维护信息

### 11.1 建议扩展点

- 支持压缩 DICOM 时，优先接入成熟 DICOM 解码库，不建议手写压缩解码。
- 支持多序列目录时，应按 Study/Series UID 分组后再加载。
- 多点云同屏时，应将窗宽窗位、裁切、色调、分类色板、LUT 和断点纹理改为材质实例或 `MaterialPropertyBlock`，避免全局变量互相影响。
- 正式产品中建议加入加载取消按钮、旧点云清理策略、数据选择器和设备端数据导入流程。
- 如需临床或监管用途，应另行建立质量管理、风险管理、可追溯性和验证文档；本手册仅覆盖当前 Unity 插件使用。

### 11.2 版本记录

| 版本 | 日期 | 说明 |
|---|---|---|
| 260609.0 | 2026-06-09 | 新增 DICOM 点云系统用户手册，覆盖 10 秒规则、快速开始、配置、任务、限制和故障排除 |
| 260609.1 | 2026-06-09 | 新增运行时调试面板 `DicomDebugPanel` 与加载诊断快照 `DicomLoadReport`；加载管线上报阶段、当前文件和耗时；shader 增加色调与强度增益 |
| 260609.2 | 2026-06-09 | 补充 Built-in/URP 双 SubShader、CT 分类配置、分类着色、世界空间 VR 操作面板、触碰滑块、配置参考、风险限制和验收检查 |
| 260610.0 | 2026-06-10 | VR 操作面板对齐 HU 区间分析、一键应用到 Profile、全局统一字体字段、数据源全场景查找与延迟重试自动配置 |
| 260611.0 | 2026-06-11 | 按插件当前内容统一更新中英文手册：补充 LUT、断点显色、堆叠轴检测、模型适配与复位、裁切平面工厂、PokeScrollbar、Profile 配置和 ISO/IEC/IEEE 26514:2022 信息结构 |
