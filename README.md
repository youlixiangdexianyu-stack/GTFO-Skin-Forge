# ForgeSkin

GTFO 的武器皮肤工具，由两部分组成：

- **ForgeSkin 编辑器**：Windows 桌面程序，给作者制作皮肤用。
- **ForgeSkin 插件**：BepInEx 插件，在游戏里把皮肤套到你自己的装备上。

玩家只需要装插件和皮肤 mod，不用运行编辑器。

能做的东西包括枪、近战武器、工具、资源包、消耗品和大件搬运物品。

## 功能

**贴图和材质**
- 替换每个部件的四个通道：基础色（Albedo）、法线（Normal）、金属/光滑（MetallicSmoothness）、自发光（Emissive）。
- 可以调材质参数：自发光颜色和强度、金属度、光滑度、色调。
- 选一个贴图文件夹或者把文件拖进来，按文件名自动识别通道和部件。识别把握不大的会标出来，要你手动确认。
- 输出尺寸可以保持原图、跟随原版，或者固定 1K/2K/4K，始终保持长宽比。

**涂装**
- 在 3D 视图里直接画：底漆、表面贴花、笔刷和遮罩。打包时这些会烘焙进普通贴图。

**挂件**
- 导入 GLB 模型挂到枪上。
- 也可以把原版某个部件的模型换掉，换上去的模型保留原部件的位置、对齐和材质。

**屏幕**
- 挂在枪上，实时显示数据。数据源：
  - 弹匣、备弹、总弹药
  - 击杀数
  - 本局这把枪造成的伤害、本局全部武器造成的伤害、本局受到的伤害
  - 这把枪的准确率、生命值、感染值
  - 固定文字
- 显示样式可以是数字，也可以是进度条。进度条只用于有上限的数据源：弹药、准确率、生命和感染。
- 低弹药时可以变色，还可以随剩余弹匣逐渐过渡。
- 字体可以用自带字体、电脑上装的字体，或者一个字体文件。

**动态图层和触发效果**
- 图层可以是图片、图片序列（GIF 按帧拆开）、MP4（抽帧）或文字，叠加在部件贴图上。
- 图层可以一直循环，也可以在游戏事件发生时播放。可用的事件：
  - 击杀、命中、没打中、开火、空扣扳机
  - 换弹开始、换弹结束
  - 拿出、收起
  - 开镜、收镜
  - 低弹药、打空、捡到弹药
  - 开灯、关灯
- 挂件可以在事件发生时闪一下自发光，或者显示几秒。
- 编辑器里每个事件都有「触发预览」，可以直接在 3D 视图里看效果。

**音效**
- 在任意事件上播放自己的音效。任意音频文件都可以，本地会转成 Ogg。可以调音量，低弹药事件还可以设阈值。

**部件布局**
- 每个部件、子部件和挂件都能移动、旋转、缩放和隐藏，游戏里显示一样的布局。

**3D 编辑**
- 操作手柄。
- G/R/S 快捷键，可以按 X/Y/Z 锁定轴。
- 固定视角（小键盘 1/3/7、5 切换透视和正交）。
- 「吸附到表面」。

**多套皮肤**
- 每件物品可以有多套皮肤：新建、复制、改名、删除，并选择游戏里用哪一套。
- 所有修改自动保存，支持撤销（Ctrl+Z）。
- **版本快照**：手动存一个带名字的版本，以后可以恢复。恢复前会自动备份当前状态。

**导出前体检**
- 一次检查一件物品下的全部皮肤：未确认的匹配、非法数值、缺失的通道、找不到的文件。

**发布**
- 应用到游戏：作者测试用。会在选定的测试档里安装或更新插件、打开热重载、装上皮肤，再从插件日志里读出游戏实际做了什么。
- 导出皮肤包（`.forgeskin`）。
- 导出 Thunderstore 皮肤 mod：依赖 ForgeSkin 插件，玩家用 r2modman 安装。
- 「替换游戏原版资源」：写进游戏自己的资源文件，带备份和还原，给不装 BepInEx 的玩家用。也可以导出成打过补丁的资源文件压缩包。
- OBJ 导出：按部件分别导出或合并导出，带原始材质名、MTL 和清单。

皮肤只在本机生效：别人看到的是他自己装的皮肤。

## 给玩家：安装皮肤

1. 用 r2modman 安装 [BepInExPack_GTFO](https://thunderstore.io/c/gtfo/p/BepInEx/BepInExPack_GTFO/)。
2. 安装作者发布的皮肤 mod。ForgeSkin 插件是它的依赖，会自动一起装上。
3. 启动游戏。拿起对应的装备就能看到皮肤。

插件的设置在 `BepInEx/config/ForgeSkin.cfg`：

| 设置 | 默认 | 说明 |
|---|---|---|
| `Enabled` | true | 关掉后不套任何皮肤 |
| `Development` | false | 开发模式：skins 文件夹一有变化就重新加载，「应用到游戏」能直接出现在正在运行的游戏里 |
| `Readback` | false | 把每把装备的原版部件姿势写到 `BepInEx/ForgeSkin/readback/`，排查对位问题时用 |

插件日志在 `BepInEx/LogOutput.log`，前缀是 `[ForgeSkin]`。

## 给作者：使用编辑器

1. 运行安装包 `ForgeSkin-<版本> Setup.exe`。数据（自动保存、日志、缓存）放在安装目录（`%LocalAppData%\ForgeSkin`）的 `GTFO_UserData` 文件夹里，更新不会动它。有新版本时编辑器会自己在后台下载，提示重启后生效，自带的插件也跟着更新。
2. 第一次启动会自动找 Steam 里的 GTFO。找不到就在顶栏手动选游戏目录。
3. 左边选一件物品，编辑器会从你本地的游戏里读出模型和原版贴图。
4. 编辑贴图、涂装、挂件、屏幕、图层和音效。
5. 在「发布」菜单里：
   - 先点「检查」。
   - 然后「应用到游戏」：选一个 r2modman 测试档。游戏开着也能热重载，只有替换插件 DLL 时需要先关掉游戏。
   - 测好了以后导出 Thunderstore 皮肤 mod。

## 目录结构

```
README.md
ForgeSkin.sln                 C# 解决方案（格式、后端、插件、测试）
docs/PLAN.md                  需求和设计说明：唯一的计划文档
editor/                       编辑器：Electron + React + TypeScript + three.js
  src/main/                   主进程：窗口、对话框、启动并连接后端
  src/preload/                给界面用的受限接口
  src/renderer/               界面、3D 预览、涂装、视频和文字帧渲染
  forge.config.ts             打包配置（Squirrel 安装包）
src/ForgeSkin.Format/         皮肤文档和皮肤包格式、校验（net6.0，后端和插件共用）
src/ForgeSkin.Backend/        后端（net10.0）：读游戏资源、构建皮肤包、安装、替换游戏文件；通过 stdio 和编辑器通信
src/ForgeSkin.Plugin/         游戏插件（net6.0，BepInEx 6 IL2CPP）
tests/ForgeSkin.Tests/        测试
prebuilt/plugin/              提交在仓库里的插件编译结果，给没法编译插件的机器安装用
```

依赖方向：插件 → 格式，后端 → 格式，编辑器只通过 stdio 和后端通信。编辑器自己不解析 Unity 文件和皮肤包。

## 从源码构建

需要：
- Windows
- [.NET SDK 10](https://dotnet.microsoft.com/)
- Node.js（含 npm）
- 本机安装的 GTFO

编译插件还需要一个装了 BepInExPack_GTFO 的 r2modman 档案，用来引用游戏的 interop 程序集。没有的话，后端会安装 `prebuilt/plugin/` 里的现成版本。

```bash
dotnet build ForgeSkin.sln -c Release -p:GTFOBepInExPath=<档案路径>/BepInEx
```

```bash
cd editor && npm install
```

开发运行（启动前会先按当前源码编译后端，保证编辑器和后端是同一版本）：

```bash
cd editor && npm start
```

打安装包（打包前会自动发布单文件后端并带上 `prebuilt/plugin/`），输出在 `editor/out/make/squirrel.windows/x64/`：

```bash
cd editor && npm run make
```

改了 `SkinDocument` 以后重新生成编辑器用的 TypeScript 类型（不要手改 `editor/src/renderer/generated/`）：

```bash
cd editor && npm run gen:types
```

插件的 Release 编译会自动刷新 `prebuilt/plugin/`，改了插件要把它一起提交。

## 检查和测试

```bash
dotnet test tests/ForgeSkin.Tests
```

```bash
cd editor && npm run lint
```

测试覆盖：
- 皮肤包往返，以及每类错误码各一个损坏包。
- 贴图匹配算法。
- 本地游戏上的主流程：定位、目录、预览、构建、安装。
- 替换游戏资源的往返。
- 编辑器和游戏装配一致性。

没装 GTFO 时，依赖本地游戏的测试会报告为未运行。

## 计划

需求、格式、协议和剩余工作都在 [docs/PLAN.md](docs/PLAN.md)。

ForgeSkin 是独立插件，只依赖 BepInExPack_GTFO。接下来要和 ForgeCore 及其他 Forge 模组装在同一个档案里一起跑，确认不冲突；有冲突再并入 Forge 仓库。
