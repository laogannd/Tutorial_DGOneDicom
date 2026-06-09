# DICOM 点云系统用户手册

版本: 260609.2  
适用模块: `Assets/!!Workspace/_Workspace/Script/Dicom`  
英文版: [README_EN.md](README_EN.md)  
适用读者: Unity/VR 场景搭建人员、训练内容集成者、测试人员

---

## 10 秒内先看这里

**这是什么？**  
一个 Unity DICOM 点云可视化系统：从本地 DICOM 切片目录加载 CT/MRI 序列，将体素转换为 GPU 点云，并在 Built-in 或 URP 管线中渲染。

**为什么我需要它？**  
它让 VR 培训场景能快速查看三维医学影像体数据，支持抓取、双手缩放、裁切、窗宽窗位、阈值过滤、强度增益、色调调整和 CT 组织分类着色，而不需要为每个体素创建 GameObject。

**怎么开始用？**  
把同一序列的 `.dcm` 文件放到 `Application.persistentDataPath/dicom`，创建使用 `Dicom/PointCloud` Shader 的材质，在场景空物体上挂 `DicomDemoBootstrap` 并指定材质，进入 Play Mode。

---

## 1. 文档范围与标准符合性

本手册按 ISO/IEC/IEEE 26514:2022 的用户文档原则组织，覆盖用户需要完成任务所需的信息：产品目的、目标用户、前置条件、快速开始、常见任务、配置参考、系统反馈、故障恢复、安全注意事项、限制、验收检查和版本记录。

本手册描述当前代码实现，不包含医学诊断流程、影像标注规范、医院 PACS 接入流程或临床合规说明。点云显示仅用于培训、演示和可视化验证，不应作为医学诊断依据。

## 2. 系统概览

| 模块 | 主要文件 | 作用 |
|---|---|---|
| DICOM 解析 | `Core/DicomParser.cs`, `Core/DicomSeriesLoader.cs`, `Core/DicomDataset.cs` | 后台扫描目录，解析 Part 10 DICOM，读取尺寸、像素间距、层厚、窗宽窗位、重标定参数和 16bit 像素数据，并组装三维体数据 |
| 加载诊断 | `Core/DicomLoadStatus.cs`, `PointCloud/PointCloudController.cs` | 维护加载阶段、文件进度、耗时、体数据尺寸、点数和错误信息 |
| 点云生成 | `PointCloud/VoxelToPointJob.cs`, `PointCloud/PointCloudController.cs` | 使用 Burst Job 两遍式统计和写入点云，按阈值筛选体素，按归一化范围生成强度 |
| 点云渲染 | `PointCloud/DicomPointCloud.cs`, `Shaders/DicomPointCloud.shader` | 使用 `ComputeBuffer` 和 `Graphics.DrawProcedural` 渲染 billboard 点或点图元，支持 URP 与 Built-in SubShader |
| 分类着色 | `Core/DicomClassificationProfile.cs`, `UI/Editor/DicomClassificationProfileCreator.cs` | 用 HU 区间映射组织类别，最多 16 类；可一键创建 CT 默认分类配置 |
| VR 交互 | `Interaction/DicomGrabbableSetup.cs`, `Interaction/TwoHandScaler.cs` | 自动补齐 AutoHand `Grabbable`、`Rigidbody`、`BoxCollider`，支持双手等比缩放 |
| 显示控制 | `Interaction/WindowLevelController.cs`, `Interaction/ClippingPlaneController.cs` | 实时控制窗宽窗位、裁切平面；阈值和归一化变更会触发点云重建 |
| 调试与 UI | `Demo/DicomDebugPanel.cs`, `UI/DicomPanelUI.cs`, `UI/Editor/DicomPanelFactory*.cs`, `UI/PokeSlider.cs` | 提供 IMGUI 调试面板和世界空间 VR 操作面板工厂，支持触碰滑块与分类着色开关 |

## 3. 使用前准备

### 3.1 Unity 与插件要求

- Unity 项目中已包含 Jobs、Burst、Collections、Mathematics。
- 目标设备和渲染管线支持 Shader Model 4.5、`StructuredBuffer` 和程序化绘制。
- Shader 提供 URP 与 Built-in 两个 SubShader；HDRP 未在当前代码中声明专用 SubShader。
- VR 抓取、触碰滑块和面板手交互依赖 AutoHand。未安装 AutoHand 时，`Interaction` 和 `UI/PokeSlider.cs` 相关脚本无法正常编译或使用。
- 世界空间 UI 使用 UGUI、TextMeshPro、`GraphicRaycaster`、`ScrollRect` 和 AutoHand 触碰组件。

### 3.2 DICOM 数据要求

当前支持：

- 标准 DICOM Part 10 文件，128 字节前导后包含 `DICM` 标识。
- 文件扩展名为 `.dcm` 或无扩展名。
- 非压缩 Little Endian 16bit 像素数据。
- 显式 VR Little Endian 和隐式 VR Little Endian。
- 同一目录只包含同一序列，且每张切片 Rows 和 Columns 一致。
- 切片排序优先使用 `ImagePositionPatient.z`，缺失时使用 `InstanceNumber`。

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
5. 可选：将 `DicomClassificationProfile` 拖到 `Classification Profile`。
6. 勾选 `Auto Load On Start`，需要屏幕调试时保留 `Attach Debug Panel`。
7. 进入 Play Mode。

加载成功后，Demo 会创建 `DicomPointCloud` 子物体，并挂载 `DicomPointCloud`、`PointCloudController`、`DicomGrabbableSetup`、`TwoHandScaler`、`WindowLevelController` 和 `ClippingPlaneController`。Console 会输出加载进度，调试面板会显示阶段、文件进度、尺寸、点数和耗时。

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
```

### 5.2 手动搭建正式场景对象

适合正式场景，避免依赖 Demo 中为了便利而使用的反射绑定。

```text
DicomPointCloudRoot
├─ DicomPointCloud        指定点云材质
├─ PointCloudController   控制加载、阈值、归一化、分类和点云生成
├─ DicomGrabbableSetup    可选，加载完成后创建抓取碰撞盒
├─ TwoHandScaler          可选，双手抓取时缩放
├─ WindowLevelController  可选，实时调窗
└─ ClippingPlaneController 可选，裁切平面控制
```

调用 `PointCloudController.Load(directory)` 开始加载。

### 5.3 调整阈值和归一化

阈值决定哪些真实像素值进入点云；归一化决定进入点云后的强度映射。

```csharp
_controller.SetThreshold(200f, 1200f);
_controller.SetNormalize(200f, 1500f);
```

这两个操作会重新过滤体素并重建点云，开销较大。UI 滑条应在用户点击 `Apply` 后调用，不应每帧调用。

### 5.4 实时调整显示外观

窗宽窗位、点大小、强度增益和色调不会重建点云，适合实时拖动。

```csharp
_windowLevel.SetWindow(0.45f, 0.35f);
_pointCloud.SetPointSize(0.003f);
Shader.SetGlobalVector(Shader.PropertyToID("_DicomTint"), new Vector4(1f, 0.9f, 0.85f, 1.2f));
```

### 5.5 使用 CT 分类着色

1. 在 Project 面板中执行 `Assets > Dicom > 创建 CT 默认分类配置`。
2. 将生成的 `CtClassificationProfile.asset` 拖到 `DicomDemoBootstrap.Classification Profile` 或 `PointCloudController`。
3. 在 `DicomDebugPanel` 或 VR 操作面板中启用“按标签分类着色”。

默认 CT 分类使用 HU 区间，将空气、脂肪、软组织、血液、松质骨、皮质骨映射到不同颜色。分类区间最多 16 类，顺序与 Shader 调色板一致。

### 5.6 使用 VR 操作面板

有两种编辑器菜单：

- `GameObject > Dicom > 创建 VR 操作面板`：在当前场景生成世界空间面板。
- `GameObject > Dicom > 创建 VR 操作面板并存为预制体`：生成面板并保存到 `Assets/!!Workspace/_Workspace/Script/Dicom/Prefabs`。

面板包含加载状态、进度条、点大小、窗宽窗位、增益、RGB 色调、阈值、归一化、裁切开关和分类着色开关。`PokeSlider` 会自动为滑块配置 `BoxCollider` 与 AutoHand `HandTouchEvent`，手指触碰可推滑，射线拖动仍可使用。

### 5.7 抓取、双手缩放与裁切

`DicomGrabbableSetup` 在数据加载完成后会按体数据物理尺寸设置 `BoxCollider`，添加或复用 `Rigidbody` 和 AutoHand `Grabbable`，并允许双手同时抓取。

`TwoHandScaler` 在同一个 `Grabbable` 被两只手抓住时，根据两手掌心距离变化等比缩放。默认缩放范围为 `0.1` 到 `10`。

裁切步骤：

1. 创建一个可移动或可抓取的裁切手柄。
2. 添加 `ClippingPlaneController`。
3. 将手柄拖到 `Plane Handle`。
4. 移动或旋转手柄，系统保留平面法线正侧的点。

关闭裁切：

```csharp
_clippingPlane.SetEnabled(false);
```

## 6. 配置参考

| 组件 | 字段/API | 默认值 | 说明 |
|---|---|---:|---|
| `DicomDemoBootstrap` | `Relative Dir` | `dicom` | 相对于 `Application.persistentDataPath` 的 DICOM 目录 |
| `DicomDemoBootstrap` | `Point Material` | 空 | 使用 `Dicom/PointCloud` Shader 的材质 |
| `DicomDemoBootstrap` | `Classification Profile` | 空 | 可选 CT/HU 分类配置 |
| `DicomDemoBootstrap` | `Auto Load On Start` | `true` | `Start()` 时自动加载默认目录 |
| `DicomDemoBootstrap` | `Attach Debug Panel` | `true` | 自动添加并绑定 IMGUI 调试面板 |
| `DicomPointCloud` | `Material` | 空 | 点云渲染材质 |
| `DicomPointCloud` | `Point Size` | `0.002` | billboard 点大小 |
| `DicomPointCloud` | `Use Billboard Quads` | `true` | `true` 使用面向相机的 quad，`false` 使用点图元 |
| `PointCloudController` | `Threshold Min/Max` | `200/3000` | 进入点云的真实像素值范围 |
| `PointCloudController` | `Normalize Min/Max` | `200/1500` | 强度归一化范围 |
| `PointCloudController` | `Classification Profile` | 空 | HU 区间分类配置 |
| `WindowLevelController` | `Window Center/Width` | `0.5/1` | 归一化显示窗位和窗宽 |
| `DicomGrabbableSetup` | `Kinematic When Idle` | `true` | 空闲时刚体是否为 Kinematic |
| `DicomGrabbableSetup` | `Mass` | `1` | 自动创建 Rigidbody 时的质量 |
| `TwoHandScaler` | `Min Scale/Max Scale` | `0.1/10` | 双手缩放限制 |
| `ClippingPlaneController` | `Plane Handle` | 空 | 作为裁切平面的 Transform |
| `DicomDebugPanel` | `Visible` | `true` | 是否显示屏幕调试面板 |
| `DicomDebugPanel` | `Toggle Key` | `F1` | 切换调试面板显隐 |
| `PokeSlider` | `Collider Depth` | `0.02` | 触碰滑块碰撞体厚度，单位米 |

## 7. 系统反馈

| 状态 | 反馈 |
|---|---|
| 扫描目录 | `DicomLoadReport.Phase = Scanning`，当前文件为目录路径 |
| 解析切片 | `OnProgress` 返回 `0..1`，`OnReportChanged` 提供 `FilesDone/FilesTotal/CurrentFile` |
| 排序与组装 | `PhaseText` 显示排序切片或组装体素 |
| 生成点云 | 报告体数据尺寸、点数和建点耗时 |
| 加载成功 | `OnLoaded` 返回 `DicomDataset` |
| 加载失败 | `OnError` 返回异常，调试面板红框显示阶段和错误信息 |
| 阈值后无点 | Console 输出警告，调试面板提示放宽阈值 |

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

**Q: 点云太稀或完全为空。**  
A: 阈值范围可能不匹配数据。先设置较宽范围确认数据能显示，再逐步收窄。

**Q: 明暗不合适。**  
A: 调整窗宽窗位；如果强度分布本身不合适，再调整 `Normalize Min/Max` 并 Apply 重建。

**Q: 分类着色没有变化。**  
A: 确认已绑定 `DicomClassificationProfile`，启用了分类着色开关，并且阈值范围内的 HU 值命中了分类区间。

**Q: VR 中无法抓取点云。**  
A: 检查 AutoHand 是否安装，点云是否加载完成，`DicomGrabbableSetup` 是否已创建碰撞盒，手部 Layer 和 AutoHand 抓取设置是否正确。

**Q: VR 操作面板滑块不能触碰拖动。**  
A: 确认滑块物体上存在 `PokeSlider`、`BoxCollider` 和 AutoHand `HandTouchEvent`，手部碰撞层能触发触碰事件。

## 9. 操作限制与风险

- 本系统用于训练和可视化，不提供医学诊断能力。
- 大体数据可能产生大量点，造成内存和 GPU 压力。优先用阈值减少点数。
- `DicomParser` 使用静态临时像素缓存；当前加载流程按单序列后台任务设计，不要并发加载多个 DICOM 目录。
- 窗宽窗位、裁切、色调、分类色板使用 Shader 全局变量；同场景多个 DICOM 点云会共享这些显示参数。
- `DicomDemoBootstrap.Load` 每次调用都会创建新的 `DicomPointCloud` 子物体；重复加载前应由业务代码管理旧对象生命周期。
- 当前加载器不递归扫描子目录，也不做多序列自动分组。

## 10. 验收检查表

| 检查项 | 通过标准 |
|---|---|
| 数据路径 | 目标平台的 `persistentDataPath` 下存在 DICOM 目录 |
| 数据合规 | 测试数据已脱敏，且为未压缩 16bit Part 10 DICOM |
| 材质 | 点云材质使用 `Dicom/PointCloud` Shader |
| 加载反馈 | Console 或面板能显示阶段、进度、尺寸、点数和耗时 |
| 显示 | 点云出现在相机视野内，点大小和明暗可调 |
| 性能 | 目标设备帧率可接受，无持续 GC 或频繁重建点云 |
| VR 交互 | 抓取、释放、双手缩放符合预期 |
| 面板 | 世界空间面板可见，按钮、开关和滑块可操作 |
| 分类 | CT 分类配置可创建、绑定并切换显示 |
| 裁切 | 裁切手柄移动后能显示内部结构 |

## 11. 维护信息

### 11.1 建议扩展点

- 支持压缩 DICOM 时，优先接入成熟 DICOM 解码库，不建议手写压缩解码。
- 支持多序列目录时，应按 Study/Series UID 分组后再加载。
- 多点云同屏时，应将窗宽窗位、裁切、色调和分类色板改为材质实例或 `MaterialPropertyBlock`，避免全局变量互相影响。
- 正式产品中建议加入加载取消按钮、旧点云清理策略、数据选择器和设备端数据导入流程。

### 11.2 版本记录

| 版本 | 日期 | 说明 |
|---|---|---|
| 260609.0 | 2026-06-09 | 新增 DICOM 点云系统用户手册，覆盖 10 秒规则、快速开始、配置、任务、限制和故障排除 |
| 260609.1 | 2026-06-09 | 新增运行时调试面板 `DicomDebugPanel` 与加载诊断快照 `DicomLoadReport`；加载管线上报阶段、当前文件和耗时；shader 增加色调与强度增益 |
| 260609.2 | 2026-06-09 | 按插件最新内容更新中英文手册：补充 Built-in/URP 双 SubShader、CT 分类配置、分类着色、世界空间 VR 操作面板、触碰滑块、配置参考、风险限制和验收检查 |
