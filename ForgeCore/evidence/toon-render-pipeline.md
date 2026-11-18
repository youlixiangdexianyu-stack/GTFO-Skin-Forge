# 游戏渲染管线与卡通着色器的接入取证

证据等级：离线读游戏文件与 interop 程序集（静态取证）。没有进游戏，下面的数据流都没有运行时读回。

## 来源

- 游戏：Steam 构建 20403457，`E:\SteamLibrary\steamapps\common\GTFO`，只读。
- Unity 版本：`globalgamemanagers` 头为 2019.4.21f1，`UnityPlayer.dll` 文件版本 2019.4.21.12021164。
- 着色器：UnityPy 1.25.4 读 `GTFO_Data/globalgamemanagers.assets`（`globalgamemanagers.assets` SHA-256 `3BBBDC31…490CE6`）里的 Shader 对象：通道名、标签、关键字、属性；解压后的程序块按 `DXBC` 头切开，用 Windows SDK 10.0.26100 的 `fxc.exe /dumpbin` 反汇编；Unity 2019 把常量与贴图绑定表写在每个程序之后（名字、cbuffer 偏移、贴图槽），据此把反汇编里的 `cbN[i]` 对回名字。
- C# 侧：`ilspycmd` 读 Forge-Multiplayer 档 `BepInEx/interop/Modules-ASM.dll` 的 `ClusteredRendering`、`RenderPipe`、`RenderPipeConfig`、`PreLitVolume`、`ShadingTypeExtractor`、`FPS_Render`、`Player.PlayerBackpackManager` 的成员表（interop 只有签名，没有方法体）。

## 管线

- 内置管线，相机走延迟路径，延迟光照换成游戏自己的：`RenderPipe` 有 `usesCustomDeferred`、`s_deferredLightingMaterial`、`s_deferredMRT`、`s_shadingTypeCMD`；`ClusteredRendering.CollectCommands(beforeGBuffer, beforeLighting)`、`m_deferredMaterial`。
- 光照两段：`Hidden/GTFO-GameDeferredShading`（Unity 灯的延迟着色替身，读 `_CameraGBufferTexture0..2`、`_ShadingTypeTexture`）和 `Clustering/ClusterDeferred`（分簇灯、阴影图集、间接光体积、雾体积、SSR，读同一组 G 缓冲和 `_ShadingTypeTexture`）。
- 着色类型：`Hidden/ExtractShadingType` 从 G 缓冲取出，`_ShadingType` 取值 Normal 0、Translucent 1、Skin 2、Cloth 3、NormalWarm 4、SkinCold 5（`Cell/Player/CustomGearShader` 的属性枚举）。
- 灯光数据是全局量，前向物体能读：分簇表 `_LightCluster`（StructuredBuffer，步长 16 字节，每格 uint4：x 点光、y 效果灯、z 聚光灯的 32 位掩码）、`_ClusterSize`、`_ClusterFarZ`；cbuffer `LightData`（14336 字节）：`_PointPosInvRangeSq[32]` @0、`_PointColorPhysical[32]` @512、`_EffectPosInvRangeSq[32]` @1536、`_EffectColorPhysical[32]` @2048、`_SpotVP[32]` @5120、`_SpotPosInvRangeSq[32]` @7168、`_SpotColor[32]` @7680、`_SpotParams[32]` @8192；阴影图集 `_ShadowAtlas`（8 × 5 格，指数阴影图）、`VSM_ShadowResInv`、`_NearPeak`。
- 雾与环境光：`PreLitVolume` 的视锥体素 `_FogVolume`（rgb 内散射、a 光学深度，透射率 `exp(-a × _FogColor.rgb)`）与间接光 `_IndirectVolume`，坐标为屏幕 uv 与 `sqrt(距离 / 远裁面 × _FogConversionFrom)`；开关 `_VolumesEnabled`。
- 第一人称：物品用 `_FPS_VP` 投影、按 `_FPSWeight` 混合（装备着色器顶点程序里 `_FPSWeight > 0` 时 `lerp(VP·p, _FPS_VP·p, _FPSWeight)`），关键字 `FPS_RENDERING_ALLOWED`；`PlayerBackpackManager.ENABLE_FPS_RENDERING` 是运行时字符串字段，值未读回，所以卡通着色器同时认 `FPS_RENDERING_ALLOWED` 与 `ENABLE_FPS_RENDERING`。

## 玩家装备着色器 `Cell/Player/CustomGearShader`

- 通道：`DEFERRED`（LightMode Deferred）与 `FORWARD`（ForwardBase）；没有 ShadowCaster 通道。
- `DEFERRED` 关键字：`ENABLE_EMISSIVE`、`FPS_RENDERING_ALLOWED`、`SHADOWS_DEPTH`/`SHADOWS_CUBE`、`UNITY_HDR_ON`。`FORWARD` 关键字带 `LOBBY_RENDERING_ENABLED`、`MENU_RENDERING`、`ICON_GENERATION`、`EDITOR_RENDERING_ENABLED`、`EXTERIOR_RENDERING_ENABLED`、`CONTACT_SHADOWS_ENABLED`：关内不透明装备走延迟，前向用于大厅、菜单与图标。
- G 缓冲布局（反汇编 `DEFERRED` 像素程序）：RT0 = 底色 ×(1 − 金属)，a = 1；RT1 = lerp(0.04, 底色, 金属)，a = 光滑度（带镜面抗锯齿：min(1 − (max |ddx n|², |ddy n|²)^0.333, 光滑度)）；RT2 = 法线 × 0.5 + 0.5，a = 1；RT3 = 4 × `_EmissiveColor.a` × `_EmissiveColor.rgb` × 自发光图（HDR 变体），a = (`_ShadingType` + 1) / 255。
- 法线图按 x = r × a、y = g 解（DXT5nm 与 RG 两种排布都读得对）。

## 前向透明着色器 `GTFO/Glass`（游戏自己的前向受光样本）

关内变体（绑定 `_LightCluster`、`_IndirectVolume`、`_FogVolume`）的像素程序：

- 分簇格：`uv = SV_Position.xy / _ScreenParams.xy`，`z = floor(sqrt(saturate(视深 / _ClusterFarZ)) × _ClusterSize.z − 0.5)`，格号 `(z × Y + y) × X + x`。
- 点光与效果灯：`window = saturate(1 − d² × invRangeSq)`，强度 `color.rgb × window × (1 / (d² + (1 − color.a) × _NearPeak + 0.001) + window)`；聚光灯同样的窗口但没有 `_NearPeak` 项，乘 `_SpotVP` 投影的方框内判定（边距 4 × `VSM_ShadowResInv`）与阴影图集采样 `min(exp2((|s.x| − (1 − z × _SpotParams.x)) × 144.27), 1) × s.y`，图集格偏移 `(floor(i / 5) × 0.125, (i mod 5) × 0.2)`，i = `_SpotParams.y`。
- 漫反射是 Disney 漫反射 × N·L / π，高光是 GGX；环境光取 `_IndirectVolume`（沿法线外移 `min(距离 × 0.05, 0.5) × (1 − e^−距离)`），环境反射取 `_ReflectionVolume`。
- 最后 `颜色 × exp(−雾.a × _FogColor.rgb) + 雾.rgb`。

## 热成像、雾、体积光对自定义着色器的要求

- 热成像镜（`Unlit/HolographicSight_Thermal`，合同记的原版热成像）按屏幕坐标读主相机 G 缓冲算热度：不写 G 缓冲的前向物体在热成像里看到的是它后面的东西。
- 雾与体积光：延迟物体由 `ClusterDeferred` 统一加雾；前向物体必须自己采样 `_FogVolume`（`GTFO/Glass` 如此），否则在雾里不变暗、不泛光。
- 曝光：游戏的后处理曝光把暗场景提亮很多倍（`ForgeVisualOutline.shader` 开头：画进场景的颜色全变白）。卡通着色器的输出必须是和游戏灯同单位的线性 HDR 光照结果；边缘光、描边颜色按当地光照缩放，不能是固定亮度。
- 本库现有着色器怎么过这些关：粒子、标记、程序化画法都在透明队列、不受光、不写深度；描边是相机命令缓冲在后处理之后合成；受光模型一律借游戏装备着色器（`LitMaterial`）。在此之前本库没有自己的受光着色器。

## 两种接法的可行性

- 前向（`ForgeVisual/ToonForward`）：可行。灯表、阴影图集、雾与环境光体积都是按名字设置的全局量，卡通着色器按上面的公式读同一组数据，分阶明暗、高光、边缘光都能按每盏灯算。风险：公式是从一个构建的反汇编里抄的，游戏更新改格式就失效（需要重新取证）；不写 G 缓冲（热成像、SSR、延迟贴花看不到）；体积雾 z 方向只做三线性采样，没有游戏的三次样条滤波与蓝噪声抖动；不接 `_ReflectionVolume` 反射与外景大气、级联阴影（关内室外场景会偏暗）；`EarlyTransparentRenderer`、接触阴影没有接。
- 延迟（`ForgeVisual/ToonDeferred` + `ForgeVisual/ToonOverlay`）：可行。按装备着色器的 G 缓冲布局写，游戏照常算光、阴影、雾，热成像与贴花都认。卡通感来自之后叠的一层：抓一次屏，去掉雾和自身自发光，除以底色得到到达表面的光，以游戏环境光为基准判明暗、按半档分带，再加边缘光与描边。风险：明暗只能近似（灯的方向在这一层已经没有了），高光仍是游戏的 GGX；每帧多一次整屏抓取；叠加层与第一人称的深度若不完全相等会闪（用了 Offset −1）；多出一个渲染器（蒙皮的要再蒙皮一次）。

## 性能（每个材质，静态计数；没有实测）

| 接法 | 绘制 | 说明 |
| --- | --- | --- |
| 前向 | 2 遍：FORWARD + OUTLINE（描边多一遍网格绘制） | 每像素按所在格的灯掩码逐盏算；变体 3 个（第一人称关键字） |
| 延迟 | 3 遍：DEFERRED（G 缓冲）+ 叠加层 STEP + OUTLINE（描边多一遍） | 叠加层是另一个渲染器；有叠加层可见的帧多一次整屏抓取（具名 GrabPass，一帧一次）；变体 DEFERRED 6 个、叠加层每通道 3 个 |

帧时间实测：未运行（需要进游戏，同机同场景前后对比）。
