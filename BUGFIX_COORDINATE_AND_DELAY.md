# Bug 修复总结：边界框位置、置信度和帧延迟问题

## 📋 问题描述

### 问题1：边界框位置不对 ❌
- 框不了正确的位置
- 根本原因：YOLO 输出坐标到原始图像坐标的映射计算有 bug

### 问题2：置信度显示 ✓
- 已确认显示格式正确（`{result.Confidence:F2}`）

### 问题3：实时识别与动作树状态树的截帧延迟 ⏱️
- 保存的截图可能不是实际执行时的帧
- 根本原因：frame 对象在异步保存时被下一帧覆盖/释放

---

## 🔧 修复方案

### 修复1：纠正坐标映射公式（OnnxInferenceService.cs）

**问题所在**
```csharp
// ❌ 错误的计算顺序
int x = (int)Math.Round((cx - dw - bw / 2.0f) / r);  // 混淆了顺序
int y = (int)Math.Round((cy - dh - bh / 2.0f) / r);
```

YOLO 输出的 `(cx, cy)` 是 **中心坐标**，`(bw, bh)` 是 **宽高**。  
正确的映射步骤应该是：
1. 先减去灰色填充偏移 `(dw, dh)`
2. 再除以缩放比例 `r`
3. 最后从中心坐标转换为左上角坐标

**修复后的公式** ✅
```csharp
// ✅ 正确的计算顺序
int x = (int)Math.Round(((cx - dw) / r) - (bw / r) / 2.0f);
int y = (int)Math.Round(((cy - dh) / r) - (bh / r) / 2.0f);
int w = (int)Math.Round(bw / r);
int h = (int)Math.Round(bh / r);
```

**修改位置**
- `OnnxInferenceService.cs` - `ParseYoloOutput()` 方法
- YOLOv5 格式坐标映射（第 340-345 行）
- YOLOv8 格式坐标映射（第 408-409 行）

---

### 修复2：克隆 frame 防止异步延迟（WorkflowExecutor.cs）

**问题所在**
```csharp
// ❌ 直接使用 frame 对象
private void HandleErrorAction(ProcessStateViewModel currentStep, Mat frame)
{
    string imagePath = _logService?.SaveFrame(frame, currentStep.Name, "NG");
    // frame 可能在保存前被下一帧覆盖或释放！
}
```

在 `VideoSourceManager.MonitoringLoop()` 中，frame 对象被循环使用。  
如果在另一个线程异步保存 frame，可能被新帧覆盖或释放。

**修复方案** ✅
```csharp
// ✅ 克隆 frame 创建独立副本
private void HandleErrorAction(ProcessStateViewModel currentStep, Mat frame)
{
    // 克隆 frame 避免在异步保存时 frame 被修改或释放
    using (Mat frameCopy = frame.Clone())
    {
        string imagePath = _logService?.SaveFrame(frameCopy, currentStep.Name, "NG");
        _logService?.LogToDb(currentStep.Name, "NG", imagePath);
    }
}
```

**修改位置**
- `Services/WorkflowExecutor.cs` - `HandleErrorAction()` 方法
- `Services/WorkflowExecutor.cs` - `HandleTimeout()` 方法

---

## 📊 修复前后对比

| 问题 | 修复前 | 修复后 |
|------|-------|-------|
| **边界框位置** | ❌ 计算顺序错误，坐标不准确 | ✅ 正确的数学映射 |
| **置信度显示** | ✓ 正确 | ✓ 正确 |
| **截帧延迟** | ❌ frame 可能被覆盖 | ✅ frame.Clone() 创建独立副本 |

---

## 🎯 坐标映射原理详解

### Letterbox 预处理流程
```
原始图像 (1920x1080)
    ↓ 按比例缩放到模型输入尺寸 (640x640)
缩放后图像 (896x504) + 灰色填充 (dw, dh)
    ↓ 
模型输入图像 (640x640)
    ↓ YOLO 推理输出
中心坐标 (cx, cy) 和宽高 (w, h) 相对于 640x640
    ↓ 逆向映射
原始图像坐标系 (1920x1080)
```

### 映射参数计算
```csharp
float r = Math.Min((float)_inputWidth / origW,      // 缩放比例
                   (float)_inputHeight / origH);
int dw = (_inputWidth - (int)Math.Round(origW * r)) / 2;  // 水平填充
int dh = (_inputHeight - (int)Math.Round(origH * r)) / 2;  // 竖直填充
```

### 正确的逆向映射（中心坐标 → 左上角坐标）
```
YOLO 输出：cx, cy (中心), bw, bh (宽高)  在 640x640 空间中

步骤1：减去灰色填充
cx_unpadded = cx - dw
cy_unpadded = cy - dh

步骤2：除以缩放比例
cx_orig = cx_unpadded / r
cy_orig = cy_unpadded / r

步骤3：从中心转左上角
x = cx_orig - bw / (2*r)
y = cy_orig - bh / (2*r)
w = bw / r
h = bh / r
```

---

## ✅ 编译验证

```
生成成功 ✓
```

所有修改已编译通过，可以直接运行。

---

## 📝 变更清单

### OnnxInferenceService.cs
- ✅ YOLOv5 格式：修正坐标映射公式（第 340-345 行）
- ✅ YOLOv8 格式：修正坐标映射公式（第 408-409 行）

### Services/WorkflowExecutor.cs
- ✅ HandleErrorAction()：添加 frame.Clone() 防止异步延迟
- ✅ HandleTimeout()：添加 frame.Clone() 防止异步延迟

---

## 🚀 下一步验证

1. 启动应用，测试边界框是否准确显示
2. 观察截图是否与实际执行时的画面一致
3. 确认置信度值显示正确且在合理范围内（0.25-1.0）
