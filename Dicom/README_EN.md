# DICOM Point Cloud System User Manual

Version: 260611.0  
Module: `Assets/!!Workspace/_Workspace/Script/Dicom`  
Chinese version: [README.md](README.md)  
Audience: Unity/VR scene builders, training-content integrators, and testers

---

## Read This First: 10-Second Rule

**What is it?**  
A Unity DICOM point-cloud visualization plugin. It loads a local directory of CT/MRI DICOM slices, parses the volume data, converts it into a GPU point cloud, and renders it in the Built-in or URP render pipeline.

**Why do I need it?**  
It lets a VR training or demo scene inspect 3D medical image volume data with grabbing, two-hand scaling, automatic size fitting, clipping, window/level, threshold filtering, pseudo color, breakpoint color ramps, HU classification, and HU range analysis without creating one GameObject per voxel.

**How do I start?**  
Put one DICOM series of `.dcm` files under `Application.persistentDataPath/dicom`, create a material that uses the `Dicom/PointCloud` shader, add `DicomDemoBootstrap` to an empty scene object, assign the material, and enter Play Mode.

---

## 1. Scope And Standards Alignment

This manual follows the user-documentation principles of ISO/IEC/IEEE 26514:2022. It provides the information needed to complete user tasks: product purpose, target users, prerequisites, quick start, task procedures, configuration reference, system feedback, error recovery, safety notes, limitations, acceptance checks, and version history.

This manual documents the current code implementation only. It does not define medical diagnosis workflows, image annotation standards, hospital PACS integration, or clinical compliance procedures. The point-cloud view is for training, demonstration, and visualization validation, not for medical diagnosis.

## 2. System Overview

| Area | Main files | Purpose |
|---|---|---|
| DICOM parsing | `Core/DicomParser.cs`, `Core/DicomSeriesLoader.cs`, `Core/DicomDataset.cs` | Scans a directory in the background, parses Part 10 DICOM files, reads dimensions, spacing, thickness, window/level, rescale parameters, orientation, and 16-bit pixels, then assembles a 3D volume |
| Stack-axis detection | `Core/DicomSeriesLoader.cs`, `PointCloud/PointCloudController.cs` | Uses `ImageOrientationPatient` and slice positions to detect the X/Y/Z stack axis, reducing reconstruction errors for coronal and sagittal series |
| Load diagnostics | `Core/DicomLoadStatus.cs`, `PointCloud/PointCloudController.cs` | Tracks phase, file progress, timings, volume size, point count, and errors |
| Point generation | `PointCloud/VoxelToPointJob.cs`, `PointCloud/PointCloudController.cs` | Uses Burst jobs to count and write filtered voxels into a point array, with normalized intensity |
| Rendering | `PointCloud/DicomPointCloud.cs`, `Shaders/DicomPointCloud.shader` | Uses `ComputeBuffer` and `Graphics.DrawProcedural` to render billboard points or point primitives, with URP and Built-in SubShaders |
| Color profiles | `Core/DicomClassificationProfile.cs`, `Core/DicomLutProfile.cs`, `Core/DicomBreakpointProfile.cs` | Supports four color modes: grayscale intensity, HU classification palette, discrete LUT pseudo color, and real-value breakpoint interpolation |
| HU analysis | `Analysis/HuRangeAnalyzer.cs`, `Analysis/HuRangeReport.cs` | Builds an HU histogram after loading, detects occupied ranges, and can write those ranges into a classification profile |
| VR interaction | `Interaction/DicomGrabbableSetup.cs`, `Interaction/TwoHandScaler.cs`, `Interaction/DicomModelTransform.cs` | Adds AutoHand grab components, supports two-hand scaling, meter-scale size fitting, a model-scale slider, and reset-to-home |
| Clipping and windowing | `Interaction/WindowLevelController.cs`, `Interaction/ClippingPlaneController.cs`, `Interaction/Editor/ClippingPlaneFactory.cs` | Controls window/level and clipping in real time; clipping handles can be created or removed from menu commands or runtime UI |
| Debug and UI | `Demo/DicomDebugPanel.cs`, `UI/DicomPanelUI.cs`, `UI/Editor/DicomPanelFactory*.cs`, `UI/PokeSlider.cs`, `UI/PokeScrollbar.cs` | Provides an IMGUI debug panel and a world-space VR operation panel with poke sliders, a wide scrollbar, color-mode switches, stack-axis switching, and HU range application |

## 3. Prerequisites

### 3.1 Unity And Plugin Requirements

- Unity Jobs, Burst, Collections, and Mathematics are available in the project.
- The target device and render pipeline support Shader Model 4.5, `StructuredBuffer`, and procedural drawing.
- The shader contains URP and Built-in SubShaders. HDRP does not have a dedicated SubShader in the current implementation.
- VR grabbing, poke sliders, poke scrollbars, and hand interaction depend on AutoHand. Without AutoHand, the `Interaction` scripts and `UI/PokeSlider.cs` cannot be compiled or used correctly.
- The world-space UI uses UGUI, TextMeshPro, `GraphicRaycaster`, `ScrollRect`, and AutoHand touch components.

### 3.2 DICOM Data Requirements

Supported:

- Standard DICOM Part 10 files with a `DICM` marker after the 128-byte preamble.
- `.dcm` files and files without an extension.
- Uncompressed Little Endian 16-bit pixel data.
- Explicit VR Little Endian and Implicit VR Little Endian.
- One series per directory, with matching Rows and Columns on all slices.
- Slice sorting by `ImageOrientationPatient` and `ImagePositionPatient` first; when orientation is missing, it falls back to `ImagePositionPatient.z`, then `InstanceNumber`.

Not supported:

- JPEG, JPEG2000, RLE, or other compressed or encapsulated pixel data.
- 8-bit, packed 12-bit, 32-bit, or other non-16-bit pixel formats.
- Recursive subdirectory scanning.
- Automatic Study/Series UID grouping when multiple series are mixed in one directory.
- Direct loading from PACS, DICOMweb, or network URLs.

### 3.3 Data Safety

DICOM files can contain sensitive patient information such as name, ID, and study date. Before using data for training, demos, or tests, de-identify it and verify that storage paths, device transfer methods, and logs meet your project data-management requirements.

## 4. Quick Start

### 4.1 Prepare DICOM Files

Put all slices from one series into one directory. The demo reads this path by default:

```text
Application.persistentDataPath/dicom
```

Common path examples:

| Platform | Typical path |
|---|---|
| Windows Editor | `%USERPROFILE%\AppData\LocalLow\<CompanyName>\<ProductName>\dicom` |
| Android/Pico | `/sdcard/Android/data/<package-name>/files/dicom` |

For Pico/Android testing:

```bash
adb push <local-dicom-directory> /sdcard/Android/data/<package-name>/files/dicom
```

### 4.2 Create A Point-Cloud Material

1. Create a Material in the Unity Project panel.
2. Select the `Dicom/PointCloud` shader.
3. Name the material `DicomPointCloudMaterial`.
4. Tune `_PointSize` for the device if needed. The default is `0.002`.

### 4.3 Start With The Demo

1. Create an empty scene object named `DicomDemo`.
2. Add `DicomDemoBootstrap`.
3. Keep `Relative Dir` as `dicom`, or set your own relative directory name.
4. Assign `DicomPointCloudMaterial` to `Point Material`.
5. Optional: assign `DicomClassificationProfile`, `DicomLutProfile`, and `DicomBreakpointProfile` to their matching fields.
6. Enable `Auto Load On Start`; keep `Attach Debug Panel` enabled if screen debugging is needed.
7. Enter Play Mode.

When loading succeeds, the demo creates a `DicomPointCloud` child object and adds `DicomPointCloud`, `PointCloudController`, `DicomGrabbableSetup`, `DicomModelTransform`, `TwoHandScaler`, `WindowLevelController`, and `ClippingPlaneController`. The Console reports progress, and the debug panel shows phase, file progress, dimensions, point count, and timings.

## 5. Common Tasks

### 5.1 Load A Specific Directory From Code

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

Useful events:

```csharp
_controller.OnProgress += ratio => Debug.Log($"Progress: {ratio:P0}");
_controller.OnLoaded += dataset => Debug.Log($"{dataset.Width}x{dataset.Height}x{dataset.Depth}");
_controller.OnError += error => Debug.LogError(error.Message);
_controller.OnReportChanged += report => Debug.Log(report.PhaseText);
_controller.OnHuAnalyzed += report => Debug.Log($"HU ranges: {report.Segments.Count}");
```

### 5.2 Build A Production Scene Object

Use this setup for production scenes so you do not depend on the demo's reflection-based convenience binding.

```text
DicomPointCloudRoot
├─ DicomPointCloud         Assigns the point-cloud material
├─ PointCloudController    Controls loading, threshold, normalization, color profiles, and point generation
├─ DicomGrabbableSetup     Optional; creates the grab collider after loading
├─ DicomModelTransform     Optional; fits size after loading and supports reset
├─ TwoHandScaler           Optional; scales while two hands grab the same object
├─ WindowLevelController   Optional; real-time window/level control
└─ ClippingPlaneController Optional; clipping-plane control
```

Call `PointCloudController.Load(directory)` to start loading.

### 5.3 Adjust Threshold, Normalization, And Stack Axis

Threshold controls which real pixel values become points. Normalization controls intensity mapping for generated points. The stack axis controls which X/Y/Z axis receives the slice stack.

```csharp
_controller.SetThreshold(200f, 1200f);
_controller.SetNormalize(200f, 1500f);
_controller.SetReconstructAxis(DicomReconstructAxis.Z);
_controller.Rebuild();
```

These operations refilter voxels and rebuild the point cloud. They can be expensive. UI sliders should call them only after the user presses `Apply` or intentionally changes the axis, not every frame. After loading, the system automatically sets the axis from DICOM metadata; users can still cycle X/Y/Z manually from the panels.

### 5.4 Adjust Appearance In Real Time

Window/level, point size, gain, tint, and color-mode switching do not rebuild the point cloud and are suitable for real-time dragging.

```csharp
_windowLevel.SetWindow(0.45f, 0.35f);
_pointCloud.SetPointSize(0.003f);
Shader.SetGlobalVector(Shader.PropertyToID("_DicomTint"), new Vector4(1f, 0.9f, 0.85f, 1.2f));
_controller.SetColorMode(DicomColorMode.Lut);
```

### 5.5 Use Classification, LUT, And Breakpoint Coloring

Supported color modes:

| Mode | Purpose | Setup |
|---|---|---|
| `Intensity` | Grayscale intensity display | No profile required |
| `Classification` | Displays tissue classes by HU ranges | Assign `DicomClassificationProfile` |
| `Lut` | Applies discrete pseudo color to windowed intensity | Assign `DicomLutProfile` |
| `Breakpoint` | Interpolates a color ramp by real value stops | Assign `DicomBreakpointProfile` |

Classification profile:

1. In the Project panel, run `Assets > Dicom > 创建 CT 默认分类配置`.
2. Assign the generated `CtClassificationProfile.asset` to `DicomDemoBootstrap.Classification Profile` or `PointCloudController`.
3. Enable classification coloring in `DicomDebugPanel` or the VR operation panel.

LUT profile:

1. Use `Create > Dicom > LUT Profile`, or use `Dicom/Data/DicomLutProfile.asset`.
2. Select `HotIron`, `Rainbow`, `Bone`, `GrayInverse`, or `Custom`.
3. At runtime, the panel button can cycle through built-in LUT presets.

Breakpoint profile:

1. Run `Assets > Dicom > 创建示例断点配置`.
2. Assign the generated `DicomBreakpointProfile.asset` to the demo or controller.
3. Edit the Stop list in the Inspector by real value. The system sorts stops by Value and bakes a 1D color ramp.

### 5.6 Use HU Range Analysis

After loading, `PointCloudController` automatically runs HU histogram analysis and fires `OnHuAnalyzed`. The debug panel and VR operation panel show detected occupied ranges, voxel counts, and percentages.

If a `DicomClassificationProfile` is assigned, call:

```csharp
bool ok = _controller.ApplyDetectedRangesToProfile();
```

On success, detected ranges overwrite the current classification profile, colors are generated across HSV, and the point cloud is rebuilt. Fine-tune category names, ranges, and colors in the Inspector afterward.

### 5.7 Use The VR Operation Panel

Editor menu commands:

- `GameObject > Dicom > 创建 VR 操作面板`: creates a world-space panel in the current scene.
- `GameObject > Dicom > 创建 VR 操作面板并存为预制体`: creates the panel and saves a prefab under `Assets/!!Workspace/_Workspace/Script/Dicom/Prefabs`.

The panel includes load status, progress bar, point size, window/level, gain, RGB tint, threshold, normalization, stack axis, rebuild, model scale, reset, clipping spawn/clear, color-mode toggles for classification/LUT/breakpoint, LUT preset cycling, and HU range analysis.

Panel behavior:

- `PokeSlider` automatically configures a `BoxCollider` and AutoHand `HandTouchEvent`, so finger poking can push the slider while ray dragging still works.
- `PokeScrollbar` lets the wide right-side scrollbar be pushed by a VR finger.
- `DicomPanelGrabHandle` makes the fixed title bar a grab area for moving the panel.
- `Auto Bind Data Source` is enabled by default. If no source is explicitly assigned, the panel searches the scene for `PointCloudController`, then retries by `Auto Bind Retry Interval` until `Auto Bind Timeout`.
- `Global Font` can replace all TextMeshPro fonts in the panel at runtime. For Chinese text, assign a TMP font asset that contains Chinese glyphs.

### 5.8 Grab, Scale, Reset, And Clip

`DicomGrabbableSetup` sizes the `BoxCollider` from the loaded volume, adds or reuses `Rigidbody` and AutoHand `Grabbable`, and allows two hands to grab the object.

`DicomModelTransform` fits the volume's maximum dimension to a meter-scale target size, records the loaded Home pose, and provides `ResetTransform()` to restore position, rotation, scale, and rigidbody velocity.

`TwoHandScaler` uniformly scales the same `Grabbable` while two hands hold it. Its default scale range is constrained relative to the fit scale from `DicomModelTransform`.

Clipping creation options:

- `GameObject > Dicom > 为点云创建裁切平面`: select a point cloud with `PointCloudController`, then create and bind a clipping handle.
- `GameObject > Dicom > 创建裁切平面(独立)`: create an independent clipping plane before a point cloud exists.
- VR panel buttons: spawn a clipping plane at the point-cloud center, or clear the existing handle.

Code example:

```csharp
_clippingPlane.SpawnPlaneAt(_controller.transform.position, Vector3.up);
_clippingPlane.SetEnabled(false);
_clippingPlane.RemovePlane();
```

## 6. Configuration Reference

| Component | Field/API | Default | Description |
|---|---|---:|---|
| `DicomDemoBootstrap` | `Relative Dir` | `dicom` | DICOM directory relative to `Application.persistentDataPath` |
| `DicomDemoBootstrap` | `Point Material` | Empty | Material using the `Dicom/PointCloud` shader |
| `DicomDemoBootstrap` | `Classification Profile` | Empty | Optional CT/HU classification profile |
| `DicomDemoBootstrap` | `Lut Profile` | Empty | Optional discrete pseudo-color LUT profile |
| `DicomDemoBootstrap` | `Breakpoint Profile` | Empty | Optional real-value breakpoint color ramp profile |
| `DicomDemoBootstrap` | `Auto Load On Start` | `true` | Loads the default directory in `Start()` |
| `DicomDemoBootstrap` | `Attach Debug Panel` | `true` | Adds and binds the IMGUI debug panel |
| `DicomPointCloud` | `Material` | Empty | Point-cloud rendering material |
| `DicomPointCloud` | `Point Size` | `0.002` | Billboard point size |
| `DicomPointCloud` | `Use Billboard Quads` | `true` | `true` uses camera-facing quads; `false` uses point primitives |
| `PointCloudController` | `Threshold Min/Max` | `200/3000` | Real pixel-value range included in the point cloud |
| `PointCloudController` | `Normalize Min/Max` | `200/1500` | Intensity normalization range |
| `PointCloudController` | `Reconstruct Axis` | `Z` | Updated automatically after load when DICOM orientation is available |
| `PointCloudController` | `Classification/Lut/Breakpoint Profile` | Empty | Optional color profiles |
| `WindowLevelController` | `Window Center/Width` | `0.5/1` | Normalized display window center and width |
| `DicomModelTransform` | `Target World Size` | `0.5` | Target maximum model dimension after loading, in meters |
| `DicomModelTransform` | `Min Scale/Max Scale` | `0.0002/0.05` | Model scale limits |
| `DicomGrabbableSetup` | `Kinematic When Idle` | `true` | Whether the idle rigidbody is kinematic |
| `DicomGrabbableSetup` | `Mass` | `1` | Mass for the created Rigidbody |
| `TwoHandScaler` | `Min Scale/Max Scale` | `0.1/10` | Two-hand scaling multiplier limits relative to fit scale |
| `ClippingPlaneController` | `Plane Handle` | Empty | Transform used as the clipping plane |
| `ClippingPlaneController` | `Default Extent` | `0.3` | Default generated clipping handle size in meters |
| `DicomDebugPanel` | `Visible` | `true` | Whether the screen debug panel is visible |
| `DicomDebugPanel` | `Toggle Key` | `F1` | Shows or hides the debug panel |
| `DicomPanelUI` | `Auto Bind Data Source` | `true` | Automatically finds the point-cloud controller when unassigned |
| `DicomPanelUI` | `Global Font` | Empty | Runtime font override for panel TMP text |
| `PokeSlider` | `Collider Depth` | `0.02` | Slider touch-collider thickness in meters |

## 7. System Feedback

| State | Feedback |
|---|---|
| Scanning directory | `DicomLoadReport.Phase = Scanning`; current file contains the directory path |
| Parsing slices | `OnProgress` returns `0..1`; `OnReportChanged` provides `FilesDone/FilesTotal/CurrentFile` |
| Sorting and assembling | `PhaseText` reports slice sorting or voxel assembly |
| Building points | The report includes volume dimensions, point count, and build time |
| HU analysis complete | `OnHuAnalyzed` returns `HuRangeReport`; panels list ranges, voxel counts, and percentages |
| Load succeeded | `OnLoaded` returns a `DicomDataset` |
| Load failed | `OnError` returns an exception; the debug panel shows the phase and error in a red box |
| No points after threshold | Console logs a warning; the debug panel suggests widening the threshold |
| Panel source not bound | The panel retries automatically, then logs a warning after timeout |

## 8. Troubleshooting

**Q: No point cloud appears after Play.**  
A: Check that `Point Material` is assigned, the shader is `Dicom/PointCloud`, the camera can see the object, and the Console does not report a missing directory or missing `.dcm` files. Try increasing `Point Size` or widening the threshold.

**Q: I see “DICOM directory does not exist”.**  
A: Confirm that data is under the relative directory inside `Application.persistentDataPath`. The demo default is `dicom`.

**Q: I see “No .dcm files in directory”.**  
A: The loader scans only the first directory level and accepts only `.dcm` files or files without extensions. Do not put slices in subdirectories.

**Q: I see “Missing DICM magic”.**  
A: The file is not a standard DICOM Part 10 file, or the export tool removed the file header. Re-export standard DICOM files.

**Q: I see “Only 16-bit pixels are supported”.**  
A: The parser supports only `BitsAllocated = 16`. Convert the data format first or extend the parser.

**Q: I see “Pixel data is compressed/encapsulated”.**  
A: Compressed DICOM is not currently supported. Export an uncompressed Little Endian series.

**Q: The point-cloud direction is wrong.**  
A: The system first detects the stack axis from DICOM orientation metadata. If the source data has missing or incorrect orientation tags, switch `Reconstruct Axis` in the debug or VR panel and rebuild.

**Q: The cloud is too sparse or empty.**  
A: The threshold range probably does not match the data. Start with a wide range, verify that data appears, then narrow it.

**Q: Brightness or contrast looks wrong.**  
A: Adjust window/level first. If the intensity distribution is still wrong, adjust `Normalize Min/Max` and apply a rebuild.

**Q: Classification, LUT, or breakpoint coloring does not change anything.**  
A: Check that the matching profile is assigned and the matching color-mode toggle is enabled. Classification mode also requires HU values inside the threshold range to hit configured class ranges.

**Q: HU ranges cannot be written to a profile.**  
A: Loading and HU analysis must be complete, and `PointCloudController` must have a `DicomClassificationProfile` assigned.

**Q: The point cloud cannot be grabbed in VR.**  
A: Check that AutoHand is installed, the point cloud finished loading, `DicomGrabbableSetup` created a collider, and hand layers plus AutoHand grab settings are correct.

**Q: VR panel sliders or scrollbars cannot be poked.**  
A: Confirm that the controls have `PokeSlider`/`PokeScrollbar`, `BoxCollider`, and AutoHand `HandTouchEvent`, and that the hand collision layer can trigger touch events.

**Q: The world-space panel does not show data.**  
A: Confirm that a `PointCloudController` exists in the scene. If the point cloud is created by the demo at runtime, keep `Auto Bind Data Source` enabled and check that auto-bind has not timed out.

## 9. Operational Limits And Risks

- This system is for training and visualization only. It does not provide medical diagnosis capability.
- Large volumes can generate many points and create memory or GPU pressure. Reduce point count with thresholds first.
- `DicomParser` uses a static temporary pixel buffer. The current load flow is designed for one series at a time; do not load multiple DICOM directories concurrently.
- Window/level, clipping, tint, classification palette, LUT, and breakpoint textures use shader globals. Multiple DICOM point clouds in one scene share those display parameters.
- `DicomDemoBootstrap.Load` creates a new `DicomPointCloud` child each time it is called. Production code should manage old objects before repeated loads.
- The loader does not recursively scan subdirectories and does not automatically group mixed series.
- `ApplyDetectedRangesToProfile()` marks and saves the classification asset in Editor Play Mode. Confirm that runtime profile modification is acceptable before using it in a production workflow.

## 10. Acceptance Checklist

| Check | Pass criteria |
|---|---|
| Data path | The target platform has a DICOM directory under `persistentDataPath` |
| Data compliance | Test data is de-identified and is uncompressed 16-bit Part 10 DICOM |
| Material | The point-cloud material uses the `Dicom/PointCloud` shader |
| Load feedback | Console or panel shows phase, progress, dimensions, point count, and timings |
| Display | The cloud appears in the camera view, and point size plus brightness can be adjusted |
| Orientation | Axial, coronal, and sagittal series can be detected automatically or corrected by manual axis switching |
| Performance | Target device frame rate is acceptable, with no continuous GC or repeated rebuilds |
| VR interaction | Grab, release, two-hand scaling, and reset match expectations |
| Panel | The world-space panel is visible, and buttons, toggles, sliders, and scrollbar operate |
| Coloring | Classification, LUT, and breakpoint profiles can be assigned, toggled, and visually distinguished |
| HU analysis | Occupied HU ranges appear after loading and can be written to a classification profile when needed |
| Clipping | Moving the clipping handle reveals internal structures; clearing it restores the full cloud |

## 11. Maintenance Information

### 11.1 Recommended Extensions

- For compressed DICOM support, integrate a mature DICOM decoder instead of hand-writing compression decoders.
- For multi-series folders, group by Study/Series UID before loading.
- For multiple point clouds in one scene, move window/level, clipping, tint, palette, LUT, and breakpoint data to material instances or `MaterialPropertyBlock` to avoid shared shader globals.
- For production use, add load cancellation, old-cloud cleanup, a data picker, and a device-side data import workflow.
- For clinical or regulated use, create separate quality-management, risk-management, traceability, and validation documentation. This manual covers only current Unity plugin usage.

### 11.2 Version History

| Version | Date | Notes |
|---|---|---|
| 260609.0 | 2026-06-09 | Added the first DICOM point-cloud user manual with the 10-second rule, quick start, configuration, tasks, limits, and troubleshooting |
| 260609.1 | 2026-06-09 | Added `DicomDebugPanel` and `DicomLoadReport`; the load pipeline reports phase, current file, and timing; shader gained tint and gain globals |
| 260609.2 | 2026-06-09 | Added Built-in/URP SubShaders, CT classification profile, classification coloring, world-space VR operation panel, poke sliders, configuration reference, risks, and acceptance checks |
| 260610.0 | 2026-06-10 | Aligned the VR operation panel with HU range analysis, one-click apply to Profile, global font, scene-wide data-source search, and delayed retry auto-binding |
| 260611.0 | 2026-06-11 | Updated Chinese and English manuals for current plugin content: LUT, breakpoint coloring, stack-axis detection, model fitting and reset, clipping-plane factory, PokeScrollbar, profile setup, and ISO/IEC/IEEE 26514:2022 information structure |
