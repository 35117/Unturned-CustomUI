# -*- coding: utf-8 -*-
"""打发布包：CustomUIMod-<版本号>.zip（只有运行时需要的东西）。

用法：
    python _mkrelease.py            # 先编译，再打包
    python _mkrelease.py --no-build # 跳过编译（dll 已编好）

⚠ 三条纪律（见 发布说明.md §一）：
  ① 打包前**必须**自检全绿（FAIL=0）—— 这里会跑一次，不绿就拒绝打包；
  ② 版本号只从 `CustomUI.cs` 的 `BepInPlugin` 读（**唯一真源**），别在脚本里写死；
  ③ 产物落在 `发布包/`（已在 .gitignore 里）—— 别把 zip 提交进仓库。
"""
import io
import os
import re
import subprocess
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
PLUGIN_DLL = os.path.join(HERE, "..", "BepInEx", "Plugins", "CustomUIMod.dll")
OUT_DIR = os.path.join(HERE, "发布包")

README_TXT = """CustomUI —— 自定义交互界面  (v{ver})
================================================================

【这是什么】
  把 Unturned 里「按 F 打开箱子/路障」时的右侧存储区，换成可编程的界面。
  想显示什么、怎么排、点一下做什么，都由注册进来的「区块提供者」决定。

【安装】
  1) 先装 BepInEx 5.4.23.5 (x64) 到 Unturned 根目录
     https://github.com/BepInEx/BepInEx/releases
  2) 把本包里的 BepInEx/Plugins/CustomUIMod.dll 放进
     <Unturned>/BepInEx/Plugins/
  3) 启动游戏一次 —— 会生成
     <Unturned>/BepInEx/config/com.trae.customui.cfg
     按需修改；改完【立即生效】，不用重启。

【联机】
  客户端与服务端【各装各的】（客户端不会用服务端的 Plugins）。
  纯界面功能都在客户端跑；服务端装了不会报错，只是"不做事"。

【常见配置】
  [General]
  Enabled         = true      总开关
  DefaultView     = grid      grid / list / blueprint
  VanillaBoxes    =          填箱型名(逗号分隔) = 这些箱子保持原版界面
  KeepVanillaGrid = false    接管时是否保留原版箱子网格（上面原版、下面插件界面）

【卸载】
  删掉 CustomUIMod.dll 即可（本插件不往存档里写任何东西；
  但 cfg 与日志会留下，可一并删除）。

【许可】
  未声明许可证（作者保留所有权利）：
  可自由下载使用；未经作者许可不要二次分发或用于商业用途。
  非官方项目，与 Smartly Dressed Games 无关。
  装之前建议备份存档。

【给其他插件的作者】
  想接自己的界面请看源码仓库里的《第三方接入指南.md》。
"""


def sh(cmd):
    return subprocess.call(cmd, cwd=HERE, shell=True)


def read_version():
    p = os.path.join(HERE, "CustomUI.cs")
    with io.open(p, "r", encoding="utf-8", errors="ignore") as f:
        text = f.read()
    m = re.search(r'BepInPlugin\(\s*"[^"]*"\s*,\s*"[^"]*"\s*,\s*"([0-9][0-9.]*)"', text)
    if not m:
        raise SystemExit("读不出版本号（CustomUI.cs 的 BepInPlugin 变了？）")
    return m.group(1)


def main():
    do_build = "--no-build" not in sys.argv
    ver = read_version()
    print("版本号（来自 CustomUI.cs）：", ver)

    # ---- ① 自检必须全绿（不绿就拒绝打包）----
    print("跑自检 …")
    r = subprocess.run([sys.executable, os.path.join(HERE, "_check_customui.py")],
                       cwd=HERE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    out = r.stdout.decode("utf-8", "replace")
    if "FAIL=0" not in out:
        tail = "\n".join(out.strip().split("\n")[-8:])
        raise SystemExit("自检不是全绿，拒绝打包：\n" + tail)
    cnt = len(re.findall(r"\[OK\]", out))
    print("  自检 OK（%d 条通过）" % cnt)

    # ---- ② 编译 ----
    if do_build:
        print("编译 …")
        if sh("build.bat") != 0:
            raise SystemExit("编译失败")
    else:
        print("跳过编译（--no-build）")

    if not os.path.exists(PLUGIN_DLL):
        raise SystemExit("找不到产物：" + PLUGIN_DLL)

    # ---- ③ 打包（只有运行时需要的东西）----
    if not os.path.isdir(OUT_DIR):
        os.makedirs(OUT_DIR)
    name = "CustomUIMod-%s.zip" % ver
    path = os.path.join(OUT_DIR, name)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(PLUGIN_DLL, "BepInEx/Plugins/CustomUIMod.dll")
        z.writestr("README.txt", README_TXT.format(ver=ver))

    size = os.path.getsize(path)
    print("\n[OK] 发布包：%s  (%.1f KB)" % (path, size / 1024.0))
    print("     内容：BepInEx/Plugins/CustomUIMod.dll + README.txt")
    print("     ⚠ 别忘了把这份 dll 也同步到服务端 U3DS/BepInEx/Plugins/（如果发联机版）")


if __name__ == "__main__":
    main()
