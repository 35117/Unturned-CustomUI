# CustomUI —— 把 Unturned 的箱子界面换成可编程界面

> 一个 **Unturned（3.26.x）+ BepInEx 5** 插件框架。
> 玩家按 F 打开箱子/路障时，**右侧存储区**由插件决定显示什么、怎么排、点一下做什么。
>
> **当前版本 `26.10.12.3`** · GUID `com.35117.customui` · 产物 `CustomUIMod.dll`

---

## 它能干什么

| | |
|---|---|
| **三套内置视图** | 大格子（一行 N 个）· 列表（一行一物品）· 蓝图（配方表，能从箱内扣材料合成） |
| **区块栈** | 右列可挂多块面板，顺序/显隐由插件和玩家分别控制；加自己的区块有基类可继承 |
| **公开接入面** | **398 项 / 42 个公开类型** —— 图标格、进度条、下拉、表单、搜索框、右键菜单、悬浮提示、彩绘面板… 都是可复用的组件 |
| **可配到底** | 格子尺寸 / 字号 / 圆角 / 边框 / 颜色 / 列数 / 行高… 默认值与内置视图一致，不设就等于原样 |

## 安装

1. 装 **BepInEx 5.4.23.5 (x64)** 到 Unturned 根目录（[下载](https://github.com/BepInEx/BepInEx/releases)）；
2. 把 **`CustomUIMod.dll`** 放进 `<Unturned>/BepInEx/Plugins/`；
3. 启动一次游戏 → 生成 `<Unturned>/BepInEx/config/com.35117.customui.cfg`。

> **联机**：客户端与服务端**各装各的**（客户端不会读服务端的 `Plugins`）。
> 界面功能全在客户端跑；服务端装了不会报错，只是不做事。

## 常用配置（`com.35117.customui.cfg`，**改完立即生效**）

| 项 | 默认 | 说明 |
|---|---|---|
| `[General] Enabled` | `true` | 总开关 |
| `[General] DefaultView` | `grid` | `grid` / `list` / `blueprint` |
| `[General] VanillaBoxes` | 空 | 填箱型名（逗号分隔）→ 这些箱子**保持原版界面** |
| `[General] KeepVanillaGrid` | `false` | `true` = 上面原版箱子网格、下面本插件界面 |
| `[Blueprint] Enabled` | `false` | 蓝图视图总开关 |

## 想接自己的界面？

看 **[第三方接入指南.md](第三方接入指南.md)** —— 30 秒接入 / 三种形态 / 常见任务 / 排查清单 / 硬规矩。

```csharp
public class MyPanel : StorageUiRegionBase   // 继承基类，实现 CanHandle / RegionPanel
{
    public override string Id { get { return "myplugin.mypanel"; } }
    ...
}
```

## 想改它自己？

⚠ **`build.bat` 用 `%~dp0..` 定位游戏根** ⟹ 项目要放在**游戏根目录的直接子目录**里才能编译
（它引用 `Unturned_Data/Managed/Assembly-CSharp.dll`、`SDG.Glazier.Runtime.dll`、`BepInEx/core/*.dll`）。

```bat
python _mkbuild.py         :: ★ 新增/删除 CustomUI*.cs 后**先跑这个**（生成 build.bat 的源文件清单）
build.bat                  :: 编译 → ..\BepInEx\Plugins\CustomUIMod.dll
python _check_customui.py  :: 自检（998 条 / 27 节，必须 FAIL=0）
python _mkrelease.py       :: 打发布包 → 发布包\CustomUIMod-<版本>.zip
```

**三条最容易栽的**（完整踩坑清单见 [开发文档.md](开发文档.md) §8）：

1. 新增 `.cs` 忘了跑 `_mkbuild.py` → 编译照样 `[OK]`，**产物里却没有那个类型**；
2. **判据字符串本身也是锚点** —— 改字段/属性/签名前先 `grep _check_customui.py`；
3. **新判据先做阳性对照**（破坏实现必须变红、还原必须恢复全绿），否则不知道它在判什么。

## 文档

| 想看什么 | 去哪 |
|---|---|
| 接自己的界面 | [第三方接入指南.md](第三方接入指南.md) |
| 完整实现记录（§7 验收清单 224 条 / §8 坑清单 100 条 / 决策与源码核对） | [开发文档.md](开发文档.md) |
| 怎么打发布包 / 版本号规则 | [发布说明.md](发布说明.md) |

## 版本号规则

`年份两位.月份.日期.当天第几个版本` —— 例如 **`26.10.12.3`** = 2026-10-12 当天第 3 次发布。

## 许可与免责

**未声明许可证**（作者保留所有权利）：可自由下载使用，但未经许可请勿二次分发或用于商业用途。

这是**非官方**项目，与 Smartly Dressed Games 无关。装之前请备份存档。

## 作者

**35117** · **Deepseek-V4.1-Flash（主）** · **Hy4 preview** · **Space Bunny**
