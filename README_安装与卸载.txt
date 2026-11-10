GTFO HEL 动态弹夹显示 1.5.1

功能
- 只识别 HEL 自动手枪（ArchetypeData persistentID = 66）。
- 只给本地玩家的第一人称武器添加显示。
- 显示当前弹夹子弹数，两位补零，例如 09、08、00。
- 每帧读取 BulletWeapon.GetCurrentClip()，仅在数字变化时刷新文字。
- 数字使用七段式几何体生成，不依赖 Windows 动态字体或 TMP 字体资源。
- 支持枪模空间 XYZ 偏移、XYZ 旋转，以及由枪皮工坊 DLY 工作区直接应用配置。
- 支持枪模空间绝对锚点，避免部件包围盒与实际组装变换造成位置偏差。
- 绝对锚点模式只使用 AnchorLocalX/Y/Z，不会再次叠加 OffsetX/Y/Z。
- 默认使用所选部件运行时的归一化包围盒坐标，编辑器位置会映射到游戏内同一部件表面。
- 屏幕根节点直接绑定到 AnchorObjectName 对应的枪模部件 Transform，会随部件旋转、动画、后坐力和 ADS 一起运动。
- 配置文件变化后约 0.5 秒自动刷新，不需要反复切换武器。
- 支持背板、边框独立不透明度。
- 不替换、不修改 GTFO_Data 中的任何官方文件。

安装
1. 确认测试档已经安装 BepInExPack_GTFO 3.2.4。
2. 关闭游戏。
3. 将本目录中的 BepInEx 文件夹合并到测试档根目录。
   目标是得到：
   <测试档目录>\BepInEx\plugins\Yuyu-HelAmmoDisplay\Yuyu.HelAmmoDisplay.dll
4. 重新启动游戏并装备 Raptus Steigro / HEL 自动手枪。

位置微调
首次启动后会生成：
<测试档目录>\BepInEx\config\Yuyu.HelAmmoDisplay.cfg

修改配置后，重新装备 HEL 即可重新读取位置参数。
常用项：
- AutoPlaceOnReceiverTop：自动放在 Receiver_Pistol_1 网格顶面。
- OffsetX / OffsetY / OffsetZ：在自动位置基础上微调。
- PanelScale：整体大小。
- TextCharacterSize：数字大小。
- TextColor：数字颜色，例如 #B9FBFF。
- PanelColor：黑色背板颜色。
- BorderColor：边框颜色。

如果位置不正确，建议依次只修改一个方向：
- X：屏幕左右
- Y：枪身前后
- Z：枪身上下

卸载
删除以下目录后重启游戏：
<测试档目录>\BepInEx\plugins\Yuyu-HelAmmoDisplay
可选删除：
<测试档目录>\BepInEx\config\Yuyu.HelAmmoDisplay.cfg

兼容性
- 当前插件针对 GTFO 版本 34873 和 BepInExPack_GTFO 3.2.4 编译。
- 仅本地显示，不会向队友发送状态。
- 第三人称模型和其他玩家武器不会显示数字。

源码
source\Yuyu.HelAmmoDisplay

重新编译
在能访问测试档 BepInEx 文件夹的开发机上执行：
dotnet build .\source\Yuyu.HelAmmoDisplay\Yuyu.HelAmmoDisplay.csproj -c Release
