# -*- coding: utf-8 -*-
"""
CustomUI 插件自检（静态）
=========================
编译通过 ≠ 逻辑对。这个脚本只做「能静态查出来的错」：
  1) 数据源纪律：不得用 storage.items 取数据（联机客户端为 null）
  2) 配置字符串里不得出现 ASCII 双引号（本项目 C# 纪律）
  3) 补丁自检必须存在（挂不上要能报出来）
  4) 反射字段名与原版一致
  5) 关键 API 调用签名与 SDK 一致
  6) Provider 生命周期对称（Open 有对应 Close 路径）
  7) ID / GUID 不撞现有插件
输出：_check.txt（同时打印到 stdout）
"""
import datetime, io, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
GAME = os.path.dirname(HERE)
SDK = os.path.join(GAME, "U3-SDK-3.26.3.8", "Assets", "Runtime", "Assembly-CSharp", "Unturned")

# 清单**扫目录**，与 build.bat 同源（那边也写死过一次：新增 CustomUI.Relay.cs 没进来，
# 于是自检对新文件里的所有判据**静默失明** —— 比报错更危险）。
import glob as _glob
FILES = sorted([os.path.basename(p) for p in _glob.glob(os.path.join(HERE, "CustomUI*.cs"))])
if not FILES:
    raise SystemExit("no CustomUI*.cs found in " + HERE)

out = []
bad = 0
warn = 0


def ok(msg):
    out.append("  [OK]   " + msg)


def err(msg):
    global bad
    bad += 1
    out.append("  [FAIL] " + msg)


def w(msg):
    global warn
    warn += 1
    out.append("  [WARN] " + msg)


def read(fn):
    p = os.path.join(HERE, fn)
    if not os.path.exists(p):
        return None
    with io.open(p, "r", encoding="utf-8") as f:
        return f.read()


src = {}
for fn in FILES:
    t = read(fn)
    if t is None:
        err("源文件缺失：" + fn)
    else:
        src[fn] = t

all_src = "\n".join(src.values())


def strip_comments(text):
    """剔掉整行注释与行内 // 之后的部分，只留代码。

    ⚠ 判据凡是「某个名字**不得**出现」的，必须先过这个函数 ——
    本项目大量在注释里写「为什么不用 X / 别再调 RemoveChild」，
    不剔注释就会把注释当代码，报出假 FAIL（第 14、15 节都踩过）。
    """
    keep = []
    for line in text.split("\n"):
        s = line.strip()
        if s.startswith("//") or s.startswith("*") or s.startswith("/*"):
            continue
        cut = line.find("//")
        keep.append(line if cut < 0 else line[:cut])
    return "\n".join(keep)

# ------------------------------------------------- 常用判据小工具（模块级）
def fn_body(text, sig, label):
    """取「8 空格缩进的成员函数体」。参数列表用通配 [^)]*，签名一改就要同步定位串。"""
    m = re.search(re.escape(sig) + r"[^)]*\)\s*\{(.*?)\n        \}", text, re.S)
    if m is None:
        err("找不到函数体：" + label + "（改名或改缩进要同步这条判据）")
        return None
    return m.group(1)


def sdk_read(rel):
    """读 SDK 反编译源码（rel 相对 Assembly-CSharp/Unturned）。"""
    p = os.path.join(SDK, *rel.split("/"))
    if not os.path.exists(p):
        return None
    with io.open(p, "r", encoding="utf-8", errors="ignore") as f:
        return f.read()


# ------------------------------ 外部消费者（通用扫描，不点名任何插件）------
# 铁律：仓库里**不许写任何具体插件的名字**。消费者由环境变量 `CONSUMERS_DIR` 指过来
#   （逗号分隔多个目录；本机开发时指向真实源码目录即可，**不必复制**源码）。
#   外部使用者不设这个变量、也没有 ./consumers ⟹ 与「外部消费者」相关的判据一律 **SKIP**
#   （不算失败、不计入分母）。判定形状一律是「在**合并后的消费者源码**里找成员名」。
import _consumers as _CONSUMERS

_CFILES = _CONSUMERS.files()        # {相对路径: 文本}
_CHAVE = bool(_CFILES)              # 有没有扫到消费者
_CMERGED = _CONSUMERS.merged()      # 所有消费者源码拼成的一段文本
_CCODE = strip_comments(_CMERGED)   # 同上，已剔注释（反向断言用这个）


def skip(msg):
    """消费者缺失时**跳过**（不算失败、不计入分母）—— 硬要求。"""
    out.append("  [SKIP] " + msg)


# ---------------------------------------------------------------- 1. 数据源纪律
out.append("1) 数据源纪律（不得用 storage.items 取数据）")
hits = re.findall(r"Storage\.items|Storage\s*\.\s*items", all_src)
# Provider.cs 的注释里提到 Storage.items 是允许的，只查代码行
code_hits = []
for fn, t in src.items():
    for i, line in enumerate(t.split("\n"), 1):
        s = line.strip()
        if s.startswith("//") or s.startswith("///") or s.startswith("*"):
            continue
        if re.search(r"\bStorage\.items\b", line):
            code_hits.append(fn + ":" + str(i) + "  " + s)
if code_hits:
    for h in code_hits:
        err("代码里出现 Storage.items（联机客户端为 null，应用 Container）：" + h)
else:
    ok("没有代码引用 storage.items —— 数据源统一走 Container")

if re.search(r"c\.Container", all_src):
    ok("Provider 侧已使用 ctx.Container 作为数据源")
else:
    err("Provider 侧未使用 ctx.Container")

# 打开检测不得依赖 inv.storage != null
host = src.get("CustomUI.Host.cs", "")
for i, line in enumerate(host.split("\n"), 1):
    s = line.strip()
    if s.startswith("//"):
        continue
    if re.search(r"inv\.storage\s*!=\s*null", line):
        err("Host.cs:" + str(i) + " 打开检测仍依赖 inv.storage != null（联机客户端恒 null）")
if re.search(r"inv\.isStoring\s*&&\s*\(\s*!\s*inv\.isStorageTrunk\s*\|\|\s*VirtualActive\s*\)", host):
    ok("打开检测用 isStoring && (!isStorageTrunk || 借道标记)（联机安全 + 虚拟存储可接管）")
elif re.search(r"inv\.isStoring\s*&&\s*!\s*inv\.isStorageTrunk", host):
    w("打开检测还是老判据（不认插件借道）—— 虚拟存储那条路会被自己挡在门外")
else:
    err("打开检测未使用 isStoring && (!isStorageTrunk || VirtualActive)")

# 借道标记必须**在关闭时清掉** —— 否则之后玩家打开真的车后备箱也会被接管
_close_body = fn_body(host, "internal static void Close(", "Host.Close")
if _close_body is not None and "virtualItems = null" in _close_body:
    ok("界面关闭时清掉借道标记（不会误接管之后的真·车后备箱）")
else:
    err("Close 没清借道标记 —— 玩家开一次虚拟存储后，所有车后备箱都会被误接管")

# ⚠⚠ 光清标记**不够**，必须把借走的 trunk 关掉。原版关存储的三条路全都排除 trunk：
#   closeDistantStorage 对 trunk 直接 return（PlayerInventory.cs:1546）、
#   收起手势关要 shouldInventoryStopGestureCloseStorage（PlayerAnimator.cs:809 = !isStorageTrunk）、
#   按 F 关要 shouldInteractCloseStorage（PlayerInteract.cs:431，同一条）；
#   真车后备箱是靠下车时 closeTrunk() 关的 —— 插件借道没有那个"载体"。
#   实测后果（2026-10-03 用户报）：isStoring+isStorageTrunk 永远挂着 →
#   InteractableStorage.cs:563 用 "already in trunk storage" 拒绝打开**任何**箱子
#   （玩家看到的症状是 F 键失效），且背包界面常驻那个中转页。
if _close_body is not None and "ReleaseBorrowedTrunk(" in _close_body:
    ok("Close 会归还借道的存储页（不留 isStoring+isStorageTrunk → F 不会失效）")
else:
    err("Close 只清标记没关 trunk —— 借道一次之后所有箱子都打不开（F 失效）")

_rb = fn_body(host, "private static void ReleaseBorrowedTrunk(", "Host.ReleaseBorrowedTrunk")
if _rb is not None and "ReferenceEquals" in _rb and "closeStorage" in _rb:
    ok("归还前先比对存储页引用（借道期间真后备箱被打开也不会被我们误关）")
else:
    err("ReleaseBorrowedTrunk 缺引用比对或缺 closeStorage")

# VirtualActive 不能只查字段：死标记（换地图 / 被别人 closeStorage）会放行误接管
_mv = re.search(r"internal static bool VirtualActive\s*\{(.*?)\n        \}", host, re.S)
if _mv is None:
    err("找不到 VirtualActive 属性体（改名或改缩进要同步这条判据）")
elif "ReferenceEquals" in _mv.group(1) and "isStoring" in _mv.group(1):
    ok("VirtualActive 现算（标记 + 存储页仍是那个 Items），死标记不放行接管")
else:
    err("VirtualActive 只查字段 —— 标记死了仍会把真·车后备箱接管走")

_ov = fn_body(host, "internal static bool OpenVirtual(", "Host.OpenVirtual")
if _ov is not None and "TryOpen(" in _ov:
    ok("OpenVirtual 当场接管（不等下一帧 Tick，避免闪一帧原版超大网格）")
else:
    err("OpenVirtual 没有当场 TryOpen —— 借道与接管之间隔一帧，原版会先把中转页画出来")

# ⚠ openTrunk **只挂页、不弹界面**（车后备箱是坐进车里时由别的路径打开的）。
#   从世界里直接调（对数字箱按 F）时若不自己开背包界面：TryOpen 会"接管成功"并记日志，
#   而下一 Tick 的关闭检测发现 !PlayerDashboardUI.active ⟹ forceClose ⟹ 归还 trunk
#   ⟹ 玩家只看到"卡一下，什么都没打开"（2026-10-04 实测就是这个）。
if _ov is not None and "EnsureDashboardOpen()" in _ov and \
        _ov.find("EnsureDashboardOpen()") < _ov.find("TryOpen("):
    ok("OpenVirtual 先确保背包界面开着、再接管（否则借道界面根本看不见）")
else:
    err("OpenVirtual 没在 TryOpen 前调 EnsureDashboardOpen —— 借道会『接管成功但玩家没界面』")
_ed = fn_body(host, "private static void EnsureDashboardOpen(", "Host.EnsureDashboardOpen")
if _ed is None:
    err("找不到 EnsureDashboardOpen")
else:
    if "PlayerDashboardInventoryUI.active = true" in _ed and "PlayerDashboardUI.open()" in _ed:
        ok("开界面照抄原版顺序（先摆四个子页 active，再 PlayerDashboardUI.open()）")
    else:
        err("EnsureDashboardOpen 没照抄原版的 active+open 顺序（只 open() 会停在别的子页）")
    # ⚠ PlayerDashboardUI.open() 在 active 已经是 true 时**直接 return**，
    #   所以"界面已开着别的子页"必须走另一支（关其它子页 + InventoryUI.open()）。
    if "PlayerDashboardInventoryUI.open()" in _ed:
        ok("已开着别的子页时走原版另一支（否则 open() 早退、切不到背包页）")
    else:
        err("EnsureDashboardOpen 漏了『dashboard 已 active』那一支")

# 借道会话的**归属令牌**：IsVirtual 只说"这页是借来的"，不说"是谁借的"。
# 没有它，两个都用虚拟存储的插件会抢同一个存储位，抢输的那个连提示都没有。
_prov26 = src.get("CustomUI.Provider.cs", "")
if re.search(r"public\s+object\s+Tag\s*;", _prov26):
    ok("StorageUiContext.Tag 是 public object（前置不解释、只原样带回）")
else:
    err("StorageUiContext 没有 Tag 字段 —— 借道会话无法认领归属")
if "OpenVirtualStorage(Items items, string title, object tag)" in _prov26:
    ok("公开面有带 tag 的 OpenVirtualStorage 重载")
else:
    err("缺 OpenVirtualStorage(items, title, tag) 重载")
if _ov is not None and "virtualTag = tag" in _ov:
    ok("OpenVirtual 记下 tag")
else:
    err("OpenVirtual 没记 tag")
_close26 = fn_body(host, "internal static void Close(", "Host.Close")
if _close26 is not None and "virtualTag = null" in _close26:
    ok("Host.Close 清掉 tag（不会把上一次会话的归属留给下一次）")
else:
    err("Host.Close 没清 virtualTag")
if "ctx.Tag = VirtualTag;" in host:
    ok("TryOpen 把 tag 放进 ctx（Provider 才能在 CanHandle 里认自己的会话）")
else:
    err("TryOpen 没把 virtualTag 传进 ctx.Tag")

# ---- ★ 借道页面的**性质声明**（StorageUiVirtualPage）------------------------------
#   借道页与真箱子容器在引擎里完全同形（都挂在 items[STORAGE]），前置分不出
#   "这一页关界面时会不会连东西一起没了"。这个事实只有开会话的一方知道，
#   不声明就只能猜 —— 猜成"算箱子"就会把玩家正托着的复制品当箱内材料扣掉
#   （对账报"丢到地上" → 调用方扣账 → 凭空少一件），猜成"不算箱子"又会
#   让借道界面里的蓝图按一个几乎永远空的箱子算可用量（满屏假"缺材料"）。
_vpg = src.get("CustomUI.Provider.cs", "")
_crf = src.get("CustomUI.Crafting.cs", "")
_bvf = src.get("CustomUI.BlueprintView.cs", "")
if "public enum StorageUiVirtualPage" in _vpg and \
        "Relay = 0" in _vpg and "RelayWithSections = 1" in _vpg and "Storage = 2" in _vpg:
    ok("公开面有 StorageUiVirtualPage（三档：纯中转 / 中转但区块可见 / 真容器）")
else:
    err("缺 StorageUiVirtualPage 或档位不全 —— 「这一页算不算箱子」又变成前置去猜")
# 上下文字段的默认值方向必须是 Storage：真箱子走的是同一条构造路径，
# 默认成 Relay 会把**每个普通箱子**的扣箱合成与右列区块一起禁掉。
if re.search(r"private StorageUiVirtualPage virtualPage = StorageUiVirtualPage\.Storage\s*;", _vpg):
    ok("ctx.VirtualPage 默认 Storage（回落方向必须是「算箱子」，否则会误禁普通箱子的合成与区块）")
else:
    err("ctx.VirtualPage 默认值不是 Storage 或没显式写 —— 默认值方向错了会静默砍掉正常功能")
if "OpenVirtualStorage(Items items, string title, object tag,\n            StorageUiVirtualPage page)" in _vpg:
    ok("公开面有带页面性质的四参重载")
else:
    err("缺四参重载 —— 接入方没法声明自己那一页是什么")
if _vpg.count("StorageUiHost.OpenVirtual(items, title, null, StorageUiVirtualPage.Relay)") >= 1 \
        and _vpg.count("StorageUiHost.OpenVirtual(items, title, tag, StorageUiVirtualPage.Relay)") >= 1:
    ok("两参/三参老重载一律转发 Relay（最保守）：已接入的插件不改代码也不会多出可扣的材料")
else:
    err("老重载没显式转发 Relay —— 默认值一改就会悄悄改变第三方会话的口径")
_fr = fn_body(_vpg, "public static List<IStorageUiProvider> FindRegions(", "FindRegions")
if _fr is not None and "!ctx.WantsRegions) return result" in _fr:
    ok("区块栈在唯一汇合点(FindRegions)按声明过滤 —— 不要求每个区块自己记得判")
else:
    err("FindRegions 没按页面性质过滤 —— 漏一个区块就是一个静默骗人的面板")
_bx = fn_body(_crf, "internal static bool BoxCounts(", "BoxCounts")
if _bx is None:
    err("缺 StorageCrafting.BoxCounts —— 「这一页算不算箱」又要在五六个调用点各写一遍")
else:
    if "c.IsVirtual" in _bx and "c.UsableAsContainer" in _bx:
        ok("BoxCounts 一处定义：真箱子算箱，借道页只有声明成 Storage 才算")
    else:
        err("BoxCounts 判据不完整")
    # 反向：扣料/产物路径不许再直接吃 c.Container（必须经 box / BoxCounts）
    _bp = fn_body(_crf, "private static bool BuildPlan(", "BuildPlan")
    _ct = fn_body(_crf, "private static bool CaptureTransferSource(", "CaptureTransferSource")
    _gp = fn_body(_crf, "private static int GrantProducts(", "GrantProducts")
    if _bp is None or _ct is None or _gp is None:
        err("找不到 BuildPlan / CaptureTransferSource / GrantProducts —— 上面的判据会静默失明")
    else:
        # 允许**恰好一次** c.Container：就是那条 `Items box = BoxCounts(c) ? c.Container : null;`
        # 之后所有搜索只认 box。多于一次 = 有地方绕过声明直接吃容器。
        if _bp.count("c.Container") == 1 and \
                "Items box = BoxCounts(c) ? c.Container : null;" in _bp:
            ok("BuildPlan 里所有搜索只认 box（=BoxCounts 决定），没有一处绕过声明直接用容器")
        else:
            err("BuildPlan 还有绕过 BoxCounts 的 c.Container —— 漏一处就是静默扣掉玩家的复制品")
        if _ct.count("c.Container") == 1 and \
                "Items box = BoxCounts(c) ? c.Container : null;" in _ct:
            ok("transferState 的「继承来源」同样不认借道页（否则抄走复制品的耐久/发数）")
        else:
            err("CaptureTransferSource 绕过 BoxCounts —— 会把中转页里的复制品当继承来源")
        if "BoxCounts(c) && c.Container.tryAddItem" in _gp:
            ok("产物不会塞进借道的中转页（塞进去 = 关界面就没了）")
        else:
            err("GrantProducts 仍可能把产物塞进中转页")
    if "StorageCrafting.BoxCounts(ctx)" in _bvf:
        ok("蓝图视图的可用量口径读同一个声明（借道界面不再按空箱子算「缺材料」）")
    else:
        err("蓝图视图没读 BoxCounts —— 界面显示的可用量又与点它实际能做到的分家")
    # ★ 口径要在**数据源头**统一：`s.box` 除了喂 usable，还喂明细、候选粗筛和
    #   allPotential(→ RANK_FROM_BOX → 给了按钮)。只在 usable 上打折 = 用户实测的
    #   "背包1·箱1、有按钮、点下去合不成"。所以箱索引本身必须不建这一页。
    if re.search(r"boxIndex\.Rebuild\(\s*StorageCrafting\.BoxCounts\(c\)\s*\?\s*c\.Container\s*:\s*null\s*\)", _bvf):
        ok("借道页**根本不建箱索引**（不是建了再在 usable 上打折）—— 显示/粗筛/定档同源")
    else:
        err("箱索引无条件建了借道页 —— s.box 会经 allPotential 造出「有按钮却合不成」的行")
    if "箱内与身上都没有" in _bvf and "StorageCrafting.BoxCounts(ctx)" in _bvf:
        ok("空清单文案跟着口径变（借道期间不提「箱内」，免得玩家去翻不算箱的那一页）")
    else:
        err("空清单文案没跟着口径走")

# ------------------------------------------------------- 2. 配置字符串引号纪律
out.append("")
out.append("2) 配置字符串引号纪律（禁 ASCII 双引号，用「」）")
qbad = []


def _strip_line_comment(line):
    """剪掉**行尾注释**（`//...`），但只认"不在字符串字面量里"的那个 `//`。
    ★ 26.10.3.49：判据原来只跳过**整行**注释（`s.startswith("//")`），
      行尾注释（`foo();  // 原文引用 "…"`）**照样被当成违规** → 假 FAIL（判据太紧，同族坑 #74）。
      这里的纪律防的是"中文**字面量**里夹 ASCII 双引号"引起的 CS1026/CS1056，
      注释根本参与不了编译 ⟹ 本来就不该判。
    ⚠ 不能用 `line.split("//")[0]`：`"a//b"` 里的 `//` 在字符串里，切早了会把
      真违规（后面的字面量）一起切掉 ⟹ 变成"永远绿"。所以按字符走一遍、认引号与转义。"""
    in_str = False
    esc = False
    i = 0
    while i < len(line):
        c = line[i]
        if in_str:
            if esc:
                esc = False
            elif c == u'\\':
                esc = True
            elif c == u'"':
                in_str = False
        else:
            if c == u'"':
                in_str = True
            elif c == u'/' and i + 1 < len(line) and line[i + 1] == u'/':
                return line[:i]
        i += 1
    return line


for fn, t in src.items():
    for i, line in enumerate(t.split("\n"), 1):
        s = line.strip()
        # ① 注释行跳过：这条纪律防的是 CS1026/CS1056（编译器读串），注释不会触发；
        #    而本项目的注释里大量出现「原文引用 "..."」，不跳会全是假 FAIL。
        if s.startswith("//") or s.startswith("///") or s.startswith("*"):
            continue
        # ①b 行尾注释同样跳过（见 `_strip_line_comment` 的说明）
        line = _strip_line_comment(line)
        # ② 中间那段不允许出现代码标点：
        #    旧写法 [^"]* 会把 `? "缺少工具" : "材料不够"` 当成「中文串里夹引号」
        #    —— 那是两个相邻字面量之间的三目/拼接定界符，不是串内引号（实测假 FAIL）。
        if re.search(u'[\\u4e00-\\u9fff]"[^"():;+\\-\\n]*"[\\u4e00-\\u9fff]', line):
            qbad.append(fn + ":" + str(i))
if qbad:
    for h in qbad:
        err("中文串内夹 ASCII 双引号：" + h)
else:
    ok("未发现中文串内夹 ASCII 双引号")

# ------------------------------------------------------------ 3. 补丁自检存在
out.append("")
out.append("3) 补丁自检")
if "CheckPatchApplied" in src.get("CustomUI.cs", ""):
    ok("存在补丁自检（CheckPatchApplied）")
else:
    err("缺少补丁自检 —— 补丁静默失效时无法发现")
if "PatchAll(typeof(CustomUIPlugin).Assembly)" in all_src:
    ok("用 PatchAll(Assembly)（不是 CreateAndPatchAll）")
else:
    err("补丁挂载方式不对：应用 PatchAll(Assembly)")

# 每个 [HarmonyPatch] 类都应被自检覆盖
# ⚠ 两种写法都要认：`[HarmonyPatch(typeof(X), "m")]` 与带参数类型数组的
#   `[HarmonyPatch(typeof(X), "m", new[] { typeof(byte), ... })]`（重载方法必须写类型，
#   否则撞 AmbiguousMatchException）。只认前一种 = 新加的补丁**静默不被检查**。
patched = re.findall(r'\[HarmonyPatch\(typeof\((\w+)\),\s*"(\w+)"\)\]', all_src)
patched += re.findall(r'\[HarmonyPatch\(typeof\((\w+)\),\s*"(\w+)"\s*,\s*new\[\]', all_src)
cs = src.get("CustomUI.cs", "")
for (cls, meth) in patched:
    if ('"' + meth + '"') in cs:
        ok("补丁有自检：" + cls + "." + meth)
    else:
        w("补丁未纳入自检：" + cls + "." + meth)

# --------------------------------------------------------- 4. 反射字段名核对
out.append("")
out.append("4) 反射字段名与原版一致")
dash = os.path.join(SDK, "UI", "Player", "PlayerDashboardInventoryUI.cs")
if os.path.exists(dash):
    with io.open(dash, "r", encoding="utf-8", errors="ignore") as f:
        d = f.read()
    for fld in ["items", "headers", "areaBox", "clothingBox"]:
        if re.search(r"(private|public|protected|internal)?\s*static\s+[\w\[\]<>. ]+\s+" + fld + r"\s*;", d):
            ok("原版字段存在：" + fld)
        else:
            err("原版字段名对不上：" + fld)
    if "private static void updateBoxAreas()" in d:
        ok("原版方法存在：updateBoxAreas()")
    else:
        err("原版方法名对不上：updateBoxAreas")
    if "private static void openSelection(byte page, byte x, byte y)" in d:
        ok("原版方法签名：openSelection(byte,byte,byte)")
    else:
        w("openSelection 签名与预期不同，需核对")
else:
    w("找不到 PlayerDashboardInventoryUI.cs，跳过反射核对")

# ------------------------------------------------- 5. 关键 API 签名核对
out.append("")
out.append("5) 关键 API 签名核对")
inv = os.path.join(SDK, "Player", "PlayerInventory.cs")
if os.path.exists(inv):
    with io.open(inv, "r", encoding="utf-8", errors="ignore") as f:
        iv = f.read()
    checks = [
        (r"public void openStorage\(InteractableStorage newStorage\)", "openStorage(InteractableStorage)"),
        (r"public void closeStorage\(\)", "closeStorage()"),
        (r"public bool isStoring;", "isStoring 是 public 字段"),
        (r"public bool isStorageTrunk;", "isStorageTrunk 是 public 字段"),
        (r"public InteractableStorage storage;", "storage 是 public 字段"),
        (r"public Items\[\] items", "items 是 public 字段"),
        (r"public static readonly byte STORAGE = 7", "STORAGE == 7"),
        (r"public static readonly byte AREA = 8", "AREA == 8"),
        (r"public static readonly byte SLOTS = 2", "SLOTS == 2"),
        (r"public void sendDragItem\(byte page_0, byte x_0, byte y_0, byte page_1, byte x_1, byte y_1, byte rot_1\)", "sendDragItem 7 参"),
        # 补丁 4 挂的就是它 —— 签名变了补丁会静默挂不上（CheckPatchApplied 会报，但这里先拦住）
        (r"public void ReceiveDragItem\(byte page_0, byte x_0, byte y_0, byte page_1, byte x_1, byte y_1, byte rot_1\)", "ReceiveDragItem 7 参"),
        (r"public void sendDropItem\(byte page, byte x, byte y\)", "sendDropItem 3 参"),
        (r"public bool tryFindSpace\(byte size_x, byte size_y, out byte page, out byte x, out byte y, out byte rot\)", "tryFindSpace 5 out"),
        (r"public ItemJar getItem\(byte page, byte index\)", "getItem(page,index)"),
        (r"public byte getIndex\(byte page, byte x, byte y\)", "getIndex(page,x,y)"),
    ]
    for (pat, name) in checks:
        if re.search(pat, iv):
            ok(name)
        else:
            err("API 签名对不上：" + name)
    # 关键：openStorage 是服务端方法
    if re.search(r"Serverside open a storage crate", iv):
        ok("已确认 openStorage 是服务端方法（联机客户端不走它）")
else:
    err("找不到 PlayerInventory.cs")

# 交互调用现在集中在共享层 —— 逐个核对参数个数（两个视图都走它）
lv = src.get("CustomUI.Shared.cs", "")
if re.search(r"sendDragItem\(PlayerInventory\.STORAGE,\s*x,\s*y,\s*newPage,\s*new_x,\s*new_y,\s*newRot\)", lv):
    ok("sendDragItem 调用参数个数正确（7）")
else:
    err("sendDragItem 调用参数个数可能不对")
if re.search(r"sendDropItem\(PlayerInventory\.STORAGE,\s*x,\s*y\)", lv):
    ok("sendDropItem 调用参数个数正确（3）")
else:
    err("sendDropItem 调用参数个数可能不对")
if re.search(r"tryFindSpace\(jar\.size_x,\s*jar\.size_y,\s*out newPage,\s*out new_x,\s*out new_y,\s*out newRot\)", lv):
    ok("tryFindSpace 调用参数个数正确（含 out page）")
else:
    err("tryFindSpace 调用参数个数可能不对（必须带 out page 的 5-out 重载）")
if "PlayerInventory.STORAGE" in lv:
    ok("交互页号用 PlayerInventory.STORAGE（7），未硬编码")
else:
    w("交互页号可能硬编码")

# ---- ★ 26.10.3.28：让「某些箱子保持原版」成为一件事（前置能力，不是下游侧改动）----
# 意义：本插件默认接管**所有** Storage 类路障（原版 46 种）。但"作为前置"时经常只需要
# 在某几种箱子上用自绘界面，其余保持原版才对（原版手感 / 别的插件依赖原版网格 /
# 玩家自己认得）。没有这条路时唯一的"不接管"办法是全局 Enabled=false —— 那是另一件事。
_prov28 = src.get("CustomUI.Provider.cs", "")
_cui28 = src.get("CustomUI.cs", "")
_host28 = src.get("CustomUI.Host.cs", "")

if re.search(r'public const string VanillaViewId\s*=\s*"customui\.vanilla"', _prov28):
    ok("公开面有保留视图 Id `StorageUiRegistry.VanillaViewId`")
else:
    err("缺 VanillaViewId —— 第三方没有「这个箱型走原版」的声明入口")

if re.search(r"public static bool IsBoxVanilla\(Guid", _prov28):
    ok("有 `IsBoxVanilla(Guid)` 查询（意图可读，等价于 IsBoundTo(guid, VanillaViewId)）")
else:
    err("缺 IsBoxVanilla 查询")

# ⚠ 短路必须在「① 箱型绑定优先」**之前**，且**返回空候选链**：
#   绑一个假 provider 不行 —— 绑定失效时会继续走优先级流程，反而把箱子接管了。
_fc28 = fn_body(_prov28, "public static List<IStorageUiProvider> FindCandidates", "FindCandidates")
if _fc28 is not None:
    _f28 = strip_comments(_fc28)
    _short28 = re.search(r"if \(hasBinding && boundId == VanillaViewId\) return result;", _f28)
    if _short28 is None:
        err("FindCandidates 里没有「保持原版 → 返回空候选链」的短路")
    else:
        _bindAdd28 = _f28.find("result.Add(bound)")
        if _bindAdd28 < 0 or _short28.start() < _bindAdd28:
            ok("短路排在「绑定优先」之前（保持原版不会先被收进候选链）")
        else:
            err("短路的顺序不对 —— 绑定的 provider 会先被收进来，声明就白做了")
if "没有任何提供者认领" in _host28:
    ok("宿主本来就处理「候选链为空」→ 回退原版（不需要新增接管分支）")
else:
    err("宿主没处理空候选链 —— 短路会让箱子变成「没有界面」")

if re.search(r'Config\.Bind\("General",\s*"VanillaBoxes"', _cui28):
    ok("玩家侧配置 `[General] VanillaBoxes`（不写插件也能配）")
else:
    err("缺 VanillaBoxes 配置 —— 只有会写插件的人才能用这个能力")

# ⚠ 撤销**只能动我们自己写过的那条**：第三方也可能 BindBox(guid, VanillaViewId)，
#   无差别 UnbindBox 会把别人的声明一起清掉。
_av28 = fn_body(_cui28, "private static bool ApplyVanillaBoxes", "ApplyVanillaBoxes")
if _av28 is not None:
    _a28 = strip_comments(_av28)
    if "vanillaBoxGuids" in _a28 and "IsBoxVanilla" in _a28:
        ok("撤销只认「上一轮我们自己写过的 + 现在仍是 vanilla」的那些（不误清第三方声明）")
    else:
        err("撤销判据不对 —— 要么删不掉、要么会把第三方设的 vanilla 一起清掉")
    if re.search(r"if \(!StorageUiRegistry\.IsBoxVanilla\(want\[i\]\)\)", _a28):
        ok("已绑好的不重复 BindBox（它每帧可能被调，不幂等就刷屏）")
    else:
        err("ApplyVanillaBoxes 不幂等 —— 每帧会重复打绑定日志")

if re.search(r"ApplyVanillaBoxes\(vanilla\)", _cui28):
    ok("配置热生效接上了（VanillaBoxes 改完立即生效）")
else:
    err("VanillaBoxes 没接进配置热生效 —— 改完要重启")

# ---- ★ 26.10.3.44：启动期的「假警报」与刷屏（坑 #69）----
# 实测现象：启动后头几帧**每帧一条** `VanillaBoxes：「Locker」认不出来`，刷了上百条
# 后自己停了 —— 而那条配置**从头到尾都是对的**（日志里随后就有
# `箱型绑定：362ab81b… → customui.vanilla`，那正是 Locker.dat 的 GUID）。
# 两个病因：
#   ① 资产表还没就绪时，把"暂时查不到"说成"这个名字认不出来" ⟹ 玩家被送去改一个没问题的配置；
#   ② 用一个**只在成功时更新**的字段兼作变更判据 ⟹ 失败时"变了"这个条件**每帧都成立**
#      ⟹ 挂在它下面的去重复位也每帧跑 ⟹ 去重形同虚设。
if re.search(r"private static string lastVanillaRaw", _cui28) and \
        re.search(r"private static bool vanillaDone", _cui28) and \
        re.search(r"private static int vanillaRetry", _cui28):
    ok("「配置变了没」与「落地了没」是两个字段（挤在一个字段里 = 失败时每帧重试）")
else:
    err("VanillaBoxes 又把两个语义挤在一个字段里了 —— 失败会退化成每帧重试 + 每帧报一次")
if "vanillaWarned" in _cui28:
    err("又出现单个 bool 管全部去重（vanillaWarned）—— 列表里两个错名字只报得出一个")
else:
    ok("去重不靠「整份配置报过没有」的单个 bool（改为按 token 逐条记）")

_sync44 = fn_body(_cui28, "private static void SyncLiveConfig", "SyncLiveConfig")
if _sync44 is not None:
    _s44 = strip_comments(_sync44)
    if re.search(r"lastVanillaRaw = vanilla;", _s44) and "reportedTokens.Clear();" in _s44:
        ok("配置原文在「变了」分支里**无条件**记下（成败都记），去重集合只在那里复位")
    else:
        err("配置原文只在成功时记 ⟹ 失败时「变了」永远为真 ⟹ 去重与退避全都会失效")
    if "VANILLA_RETRY_FRAMES" in _s44:
        ok("认不出时**退避**重试（不是每帧遍历一遍资产表）")
    else:
        err("认不出时每帧重试 —— 每帧遍历几百个资产，这也正是刷屏的一半原因")
else:
    err("找不到 SyncLiveConfig —— 判据锚点要跟着实现一起搬")

if _av28 is not None:
    # 「表没就绪」与「名字确实不存在」必须分开：一个是 Log + 说明会自动重试，一个是 Warn
    if "notReady" in _a28 and re.search(r'Log\("VanillaBoxes：', _a28) \
            and re.search(r'Warn\("VanillaBoxes：', _a28):
        ok("「表还没就绪」与「名字确实不存在」分开报（前者 Info + 明说会自动重试）")
    else:
        err("两种失败又混成一句话了 —— 玩家会被送去改一个没问题的配置（坑 #69 的原症状）")
    if re.search(r"if \(notReady\) retryLater = true;", _a28):
        ok("只有「表没就绪」才重试；名字确实不存在是**终态**（不再每半秒白遍历）")
    else:
        err("重试条件没限定在「表没就绪」上 —— 名字写错会一直每半秒遍历一次资产表")

# 行的三种写法住在 ResolveBoxToGuid 里（别锚到 ApplyVanillaBoxes —— 那儿没有）
_rb44 = fn_body(_cui28, "private static Guid ResolveBoxToGuid", "ResolveBoxToGuid")
if _rb44 is not None:
    _r44 = strip_comments(_rb44)
    if "ushort.TryParse" in _r44 and "EAssetType.ITEM" in _r44:
        ok("数字 ID 也能填（dat 里的 `ID 328` —— 比 32 位 GUID 好抄）")
    else:
        err("只认名字与 GUID —— dat 里现成的数字 ID 抄不了")
    if "sawAnyStorage" in _r44:
        ok("「表未就绪」的判据是**表里一个 Storage 都没有**（不是「表空不空」这种更弱的条件）")
    else:
        err("分辨不出「表还没就绪」—— 又会把暂时查不到报成名字不存在")
else:
    err("找不到 ResolveBoxToGuid —— 判据锚点要跟着实现一起搬")

# ---- ★ 26.10.3.31：公开面补齐「能挂能拆 + 失败有原因」（盘点 §2.5 / §2.7）----
# 这两条是盘点里排第 3 的"第三方会被咬"项：接错了**自己查不出来**，
# 而且挂上去之后**摘不下来**（`Open` 失败要自己拆半成品时最需要）。
if re.search(r"public static void Detach\(ISleekElement", _prov28):
    ok("公开面有 `StorageUiPanels.Detach`（`Attach` 的反操作 —— 以前只能挂不能拆）")
else:
    err("缺 `Detach` —— 第三方挂上面板后摘不下来（`Open` 失败时只能留个半成品）")
if re.search(r"public static string LastOpenReject", _prov28):
    ok("公开面有 `LastOpenReject`（失败不再只给一个 bool）")
else:
    err("缺 `LastOpenReject` —— 外部只知道「没成功」，不知道为什么")

# ⚠ 拆卸必须**只藏不拆**、且**比对引用**（同「归还借来的引擎状态时要比对引用」那条纪律）
_dep31 = fn_body(_host28, "internal static void DetachExternalPanel", "DetachExternalPanel")
if _dep31 is not None:
    _d31 = strip_comments(_dep31)
    if "RemoveChild" not in _d31:
        ok("`Detach` 只藏不拆（RemoveChild 是 Destroy，会毁掉对方常驻的控件池与图标）")
    else:
        err("`Detach` 用了 RemoveChild —— 那会毁掉对方常驻的控件池与已渲染的图标")
    if "ReferenceEquals(target, panel)" in _d31:
        ok("`Detach` 比对引用（A 插件摘不掉 B 插件的面板）")
    else:
        err("`Detach` 没比对引用 —— 谁都能把别人的面板摘下架")
if re.search(r"internal static string LastOpenReject", _host28):
    ok("`LastOpenReject` 的宿主侧出口存在（读到的是 `Reject` 记的那条原因）")
else:
    err("`LastOpenReject` 没有宿主侧出口 —— 公开属性会读到 null")

# 外部消费者必须**真的用上**（编译期即约束 —— 坑 #45 的成因就是"公开面没人验证"）
if not _CHAVE:
    skip("没扫到外部消费者 —— 跳过「新公开面有没有人从外部用」")
elif "StorageUiPanels.Detach(" in _CMERGED and "StorageUiPanels.LastOpenReject" in _CMERGED:
    ok("外部消费者真的调用了这两个新公开面（编译通过 = 外部可用）")
else:
    err("没有外部消费者用到新公开面 —— 它的外部可用性没人验证")

# ------------------------------------------------- 6. 生命周期对称
out.append("")
out.append("6) Provider 生命周期对称")
prov = src.get("CustomUI.Provider.cs", "")
for m in ["CanHandle", "Open", "Close", "Tick", "Rebuild"]:
    if re.search(r"\b" + m + r"\s*\(", prov):
        ok("接口含 " + m)
    else:
        err("接口缺 " + m)
# Host 里 Close 必须被调用
if re.search(r"internal static void Close\(\)", host) and re.search(r"Close\(\);", host):
    ok("Host 有 Close 且被调用")
else:
    err("Host 的 Close 未被调用 —— 会漏恢复原版存储区")
# RestoreVanillaStorage 必须在 Close 里
mclose = re.search(r"internal static void Close\(\)\s*\{(.*?)\n        \}", host, re.S)
if mclose and "RestoreVanillaStorage" in mclose.group(1):
    ok("Close 里恢复了原版存储区")
else:
    err("Close 未恢复原版存储区 —— 关闭后原版网格会一直消失")

# 幂等：挂载必须「父节点变了才 AddChild」。
# ⚠ 判据要跟排版走：右列的挂载现在在 LayoutStack 里（宿主统一排版后
#   AttachAndPlacePanel 不再自己 AddChild），别只认 attachedParent 那个老写法。
if "ReferenceEquals(it.Panel.Parent, area)" in host and "area.AddChild(it.Panel)" in host:
    ok("挂载是幂等的（按父节点判断，右列这一路）")
elif "ReferenceEquals(attachedParent" in host:
    err("只找到独立界面那条路的幂等判断，右列排版没查到 —— 排版可能又变成各自挂")
else:
    err("挂载不幂等 —— 会重复 AddChild")

# ------------------------------------------------- 7. ID / GUID 不撞
out.append("")
out.append("7) ID / GUID 不撞")
guid = re.search(r'BepInPlugin\("([^"]+)"', src.get("CustomUI.cs", ""))
if guid:
    g = guid.group(1)
    ok("GUID = " + g)
    plugins = os.path.join(GAME, "BepInEx", "Plugins")
    if os.path.isdir(plugins):
        others = [fn for fn in os.listdir(plugins)
                  if fn.endswith(".dll") and "CustomUI" not in fn]
        # 只汇总数量：本项目常驻十几个插件，逐个列成 WARN 会把真正的失败淹没
        ok("同目录另有 " + str(len(others)) + " 个插件 dll（GUID 需人工确认不撞）")
else:
    err("未找到 BepInPlugin GUID")

# ★ 版本号只有一个真源：[BepInPlugin] 第三参数。
#   实测漂过一次：26.10.3.37–.40 四轮改动都忘了改版本号，README 头部一路写到 .41 才发现 ——
#   于是「文档说的版本」和「BepInEx 元数据/日志里的版本」能差四个小版本而没人察觉。
#   这条门禁把两者钉在一起：**改版本号只改 [BepInPlugin]，README 头部跟着走**。
_pver = re.search(r'BepInPlugin\("[^"]+",\s*"[^"]*",\s*"([^"]+)"\)', src.get("CustomUI.cs", ""))
_rdp = os.path.join(HERE, "开发文档.md")
_rdtxt = ""
if os.path.exists(_rdp):
    with io.open(_rdp, "r", encoding="utf-8", errors="ignore") as f:
        _rdtxt = f.read()
_rver = re.search(r"^> 版本 `([^`]+)`", _rdtxt, re.M)
if _pver is None:
    err("读不出 [BepInPlugin] 的版本号 —— 门禁失效（别让它静默跳过）")
elif _rver is None:
    err("README 头部没有 `> 版本 \\`x.y.z\\`` 那一行 —— 门禁没得比，等于没门禁")
elif _pver.group(1) == _rver.group(1):
    ok("版本号单一真源：README 头部 == [BepInPlugin]（" + _pver.group(1) + "）")
else:
    err("[BepInPlugin] 是 " + _pver.group(1) + "，README 头部写的是 " + _rver.group(1) +
        " —— 版本号漂了（改版本只改 [BepInPlugin]，README 跟着走）")

# 测试机械 ID
datp = os.path.join(GAME, "Sandbox", "CustomUI", "Items", "TestStorage", "TestStorage.dat")
if os.path.exists(datp):
    with io.open(datp, "r", encoding="utf-8", errors="ignore") as f:
        dt = f.read()
    # ⚠ 一律先剔除注释行再断言。这里踩过坑：dat 只在注释里提到
    #   Bundle_Override_Path / Type Storage，旧版自检把它们当成指令行 → 假绿，
    #   实际放置后没有模型、也不是储物类型。
    dt_code = "\n".join(ln for ln in dt.split("\n")
                        if not ln.strip().startswith("//"))

    m = re.search(r"^\s*ID\s+(\d+)", dt_code, re.M)
    if m:
        i = int(m.group(1))
        if i >= 14081:
            ok("测试机械 ID = " + str(i) + "（14081 起，未撞下游段）")
        else:
            err("测试机械 ID " + str(i) + " 与下游段（14001-14080）冲突")
    else:
        err("TestStorage.dat 缺 ID 指令行")
    if re.search(r"^\s*Type\s+Storage\s*$", dt_code, re.M):
        ok("TestStorage 是 Type Storage（指令行）")
    else:
        err("TestStorage 不是 Type Storage（必须是指令行，注释不算）")
    if re.search(r"^\s*Storage_X\s+16\s*$", dt_code, re.M) and \
       re.search(r"^\s*Storage_Y\s+16\s*$", dt_code, re.M):
        ok("容量 16×16 = 256 格")
    else:
        err("Storage_X/Y 不是 16×16 —— 容量与测试目标（256）不符")
    # ⚠ 借 core bundle 的模型必须「成对」写这两行：
    #   Master_Bundle_Override 决定 Bundle_Override_Path 的**语义**。
    #   引擎 Assets.cs：masterBundle != null 时它才是「bundle 内相对路径」；
    #   若 masterBundle == null（Sandbox 目录没有 MasterBundle.dat），
    #   它会被当成 legacy "xxx.unity3d" 去 Resources 找 → PopulateAsset 抛
    #   「missing "Item" GameObject …」→ asset 为 null → /give 静默失效。
    mb = re.search(r"^\s*Bundle_Override_Path\s+(\S+)", dt_code, re.M)
    mbMaster = re.search(r"^\s*Master_Bundle_Override\s+(\S+)", dt_code, re.M)
    if mb and mbMaster:
        ok("借外观成对：Master_Bundle_Override " + mbMaster.group(1) +
           " + Bundle_Override_Path " + mb.group(1))
        if mbMaster.group(1).lower() == "core.masterbundle":
            src_dir = os.path.join(GAME, "Bundles", mb.group(1).replace("/", os.sep))
            if os.path.isdir(src_dir):
                ok("被借目录存在于 Bundles/：" + mb.group(1))
            else:
                err("被借目录不存在，模型取不到：" + mb.group(1))
        else:
            ok("指向自家 masterbundle：" + mbMaster.group(1) +
               "（无需核对 Bundles/ 目录）")
    elif mb and not mbMaster:
        err("只写了 Bundle_Override_Path 却缺 Master_Bundle_Override —— "
            "引擎会当成 legacy .unity3d 路径 → asset 加载失败 → /give 静默失效")
    elif mbMaster and not mb:
        w("有 Master_Bundle_Override 但无 Bundle_Override_Path："
          "路径将由 dat 目录相对 bundle 目录推导，请确认是否符合预期")
    else:
        err("缺借外观指令 → 放置后没有模型（写在注释里不算）")
else:
    err("测试机械 dat 不存在：" + datp)

# ------------------------------------------------- 8. 输出 DLL
out.append("")
out.append("8) 产物")
dll = os.path.join(GAME, "BepInEx", "Plugins", "CustomUIMod.dll")
if os.path.exists(dll):
    ok("CustomUIMod.dll 存在（" + str(os.path.getsize(dll)) + " 字节）")
else:
    err("CustomUIMod.dll 未产出")

# ------------------------------------------- 9. 回归断言（布局 / 生命周期）
# 本节每一条都对应一个真实踩过的坑；改动布局或生命周期代码后，务必让它们全绿。
out.append("")
out.append("9) 回归断言（布局 / 生命周期）")

# 9.1 窄屏（Screen.width < 1350）必须拒绝接管。
#     否则：原版右列根本不存在（open() 里 areaBox.IsVisible 保持 false），
#     而原版网格已被藏 → 用户看到一片空白，且日志毫无线索。
if re.search(r"Screen\.width\s*>=\s*1350", host):
    ok("窄屏判据与原版 isSplitClothingArea 一致（Screen.width >= 1350）")
else:
    err("缺窄屏判据 —— 窄屏会接管到不存在的右列，界面一片空白")

# 9.2 右列不可见的兜底必须真正回退（Close），不能只 return。
m = re.search(r"if\s*\(\s*!\s*area\.IsVisible\s*\)\s*\{(.*?)\}", host, re.S)
if m and "Close()" in m.group(1):
    ok("右列不可见的兜底会回退（Close），不是静默 return")
elif re.search(r"if\s*\(\s*!\s*area\.IsVisible\s*\)\s*return", host):
    err("右列不可见时只 return —— 造成「原版被藏 + 面板没挂」的空白")
else:
    w("未找到 !area.IsVisible 兜底，请确认是否已被移除")

# 9.3 补丁自检必须按 owner 精确匹配，且同时接受 Prefix / Postfix。
#     只查 Postfix → updateBoxAreas（Prefix）被误报为失败；
#     只查「有补丁」→ 被同装的其它插件蒙混成假绿。
cs2 = src.get("CustomUI.cs", "")
if "HasOwnPatch" in cs2 and "p.owner" in cs2:
    ok("补丁自检按 owner 精确匹配（不会被其它插件的补丁蒙混）")
else:
    err("补丁自检未按 owner 匹配 —— 其它插件的补丁会让自检假绿")
if re.search(r"HasOwnPatch\(patches\.Prefixes\)[\s\S]{0,80}HasOwnPatch\(patches\.Postfixes\)", cs2):
    ok("补丁自检同时接受 Prefix 与 Postfix（updateBoxAreas 挂的是 Prefix）")
else:
    err("补丁自检只认一种 —— updateBoxAreas 是 Prefix，会被误报为失败")

# 9.4 只藏物品网格、必须保留标题。
#     updateBoxAreas 的布局累加只判 headers[].IsVisible（:1849），高度取自
#     items[].SizeOffset_Y（:1866，不看 items 的可见性）。连标题一起藏 → 原版
#     不再给这一项分配空间 → 面板被挤到「附近物品」下方，位置与原版不符。
#   ★ 26.10.3.67：可见性从"写死 false"改成**由 `KeepVanillaGrid` 决定**
#     （默认 false = 现状；打开 = 原版网格与我们并存）⟹ 判据跟着搬家，并**升一级**：
#     锁「由开关决定」这件事，而不是锁某一个字面值。
if re.search(r"headers\s*\[\s*StorageIndex\s*\]\s*\.\s*IsVisible\s*=\s*false", host):
    err("把存储标题也藏掉了 —— 面板会被挤到「附近物品」之下")
elif re.search(r"grid\s*\.\s*IsVisible\s*=\s*KeepVanillaGrid\s*;", host):
    ok("物品网格的可见性**由 `KeepVanillaGrid` 决定**（默认 false = 藏起来·现状；开关打开 = 并存），且标题保留")
elif re.search(r"grid\s*\.\s*IsVisible\s*=\s*(?:true|false)\s*;", host):
    err("网格可见性被写死 —— 并存开关会失效（或者永远不藏、与自定义面板重叠）")
else:
    err("没有藏原版物品网格 —— 原版网格会与自定义面板重叠")

# 9.5 关闭检测必须覆盖「点击标题关闭背包页」。
#     onClickedHeader 的 case 5 会 PlayerDashboardUI.close()，此时 isStoring
#     可能仍为 true → 若不关掉，isOpen 残留，下次按 F 不再接管 → 一片空白。
if re.search(r"!\s*PlayerDashboardUI\.active", host):
    ok("关闭检测覆盖 PlayerDashboardUI.active（点标题关闭背包页）")
else:
    err("关闭检测漏了 PlayerDashboardUI.active —— 点标题后 isOpen 残留，下次按 F 空白")

# 9.6 恢复可见性必须按原版规则（height > 0），不得照搬接管时的快照。
if re.search(r"originalItemsVisible|originalHeaderVisible", host):
    err("用快照恢复可见性 —— 箱子已关时会还原出可见的空存储区")
else:
    ok("恢复可见性未用过期快照")
if re.search(r"grid\.height\s*>\s*0", host):
    ok("还原时按原版规则 grid.height > 0 判定")
else:
    w("还原逻辑请确认按 height > 0 判定")

# 9.7 ListView 必须先重建、后挂载定位（否则首帧滚动范围用旧高度）。
# ⚠ 26.10.3.32（T9）：列表面板已提成**公开组件** `CustomUI.ListPanel.cs` ⟹
#   下面这一整族"列表"判据一律指向**面板本体**（逻辑搬到哪儿、判据跟到哪儿）；
#   `StorageListView` 现在只剩薄封装，它的形状由本节末尾那条单独守。
lv2 = src.get("CustomUI.ListPanel.cs", "")
lvp = src.get("CustomUI.ListView.cs", "")
mt = re.search(r"public void Tick\(StorageUiContext c\)\s*\{(.*?)\n        \}", lvp, re.S)
if mt:
    body = mt.group(1)
    i_fp = body.find("list.Refresh(")
    i_att = body.find("AttachAndPlacePanel")
    if i_fp >= 0 and i_att >= 0 and i_fp < i_att:
        ok("ListView.Tick 先刷新、后挂载定位（滚动范围跟得上高度）")
    else:
        err("ListView.Tick 顺序反了 —— 首帧滚动范围会用旧高度")
else:
    w("未找到 ListView.Tick，跳过")

# 9.8 面板定位必须取「原版存储网格的位置」，不是「附近物品」区之下。
#     （区块栈改造后这句挪进了 StorageRowTopY，所以两种写法都算认过。）
if re.search(r"(y\s*=\s*|return\s+)items\s*\[\s*StorageIndex\s*\]\s*\.\s*PositionOffset_Y", host):
    ok("面板定位取原存储网格的位置（items[StorageIndex].PositionOffset_Y）")
elif re.search(r"items\s*\[\s*AreaIndex\s*\]\s*\.\s*SizeOffset_Y\s*\+", host):
    err("面板被定位到「附近物品」区之下 —— 位置与原版存储区不符")
else:
    err("未识别面板定位方式 —— 区块栈的起点必须来自原版存储网格，不能凭空给")

# 9.9 Ctrl+右键必须走「取回」，不能只弹信息面板。
#     实测被指出「Ctrl+右键没有迅速取回，而是打开了物品信息界面」——
#     原版在存储页上 Ctrl+右键就是快速取出（onSelectedItem:864-890）。
#     ⚠ 交互现已集中在共享层 StorageUiShared（两个视图共用），所以查共享层。
#       定位仍要用「方法签名」而不是裸名（裸名会命中别处的调用）。
sh = src.get("CustomUI.Shared.cs", "")
m_sec = re.search(r"internal static bool HandleSecondaryClick\(byte x, byte y\)\s*\{(.*?)\n        \}",
                  sh, re.S)
if m_sec is not None:
    body_sec = m_sec.group(1)
    if "IsCtrlDown" in body_sec and "TakeToBackpack" in body_sec:
        ok("Ctrl+右键走取回（共享层 HandleSecondaryClick）")
    else:
        err("Ctrl+右键未接取回 —— 会变成只弹信息面板（实测踩过）")
else:
    err("共享层缺 HandleSecondaryClick —— 右键交互没有统一入口")

# 9.10 交互必须**只在共享层实现一次**，两个视图不许各写一份。
n_take = sh.count("TakeToBackpack")
if n_take >= 3:      # 定义 1 处 + 左键 / Ctrl+右键 两条路径调用
    ok("取回逻辑集中在共享层（TakeToBackpack）")
else:
    err("取回逻辑未集中 —— 只剩 " + str(n_take) + " 处引用")
if "StorageUiShared.HandlePrimaryClick" in lv2 and "StorageUiShared.HandleSecondaryClick" in lv2:
    ok("列表视图走共享层交互")
else:
    err("列表视图没走共享层交互")

# 9.11 行必须复用（行池），不得退回「清空所有子元素再重建」。
#      实测被指出「每次存取物品都会导致箱子内所有图标重新加载」——
#      根因正是每次 RemoveChild 掉全部行、再重新 new SleekItemIcon + Refresh。
if "private class RowUi" in lv2 and "rows.Clear()" in lv2:
    ok("行用行池复用（RowUi + rows）")
else:
    err("缺少行池 —— 会退回「每次清空重建」，图标全量重载（实测踩过）")
if re.search(r"while\s*\(\s*panel\.GetChildCount\(\)\s*>\s*1\s*\)", lv2):
    err("仍在「清空所有子元素再重建」—— 图标会被全量重载")
else:
    ok("没有「清空子元素再重建」的写法")

# 9.12 换物品时必须「整行重建」。
#      SleekItemIcon 的贴图由 ItemTool.getIcon 的**异步回调**才换上：
#        · 同一元素二次 Refresh 不可靠 → 新放入的物品不显示图标；
#        · 只移除图标子元素再新建 → 会牵连兄弟元素（实测那一行的文字一起消失）。
#      故整行换新元素（RebuildRow），结构上不可能出错。
n_refresh = lv2.count(".Refresh(")
if n_refresh == 1:
    ok("图标只在一处刷新（RebuildRow）—— 单纯挪位置不碰控件")
else:
    err(".Refresh( 出现 " + str(n_refresh) + " 处 —— 应集中在一处")
n_new_icon = lv2.count("new SleekItemIcon()")
if n_new_icon == 1:
    ok("图标随整行重建（new SleekItemIcon 只有 1 处）")
else:
    err("new SleekItemIcon() 出现 " + str(n_new_icon) + " 处 —— 应只有 1 处")
if re.search(r"scroll\.RemoveChild\(r\.Button\)", lv2):
    ok("重建前先摘掉上一代整行（scroll.RemoveChild(r.Button)）")
else:
    err("整行重建前没摘掉旧行 —— 行会越积越多")

# 9.13 行回调不得闭包捕获坐标 —— 行被复用后闭包会一直指向旧格子（点错物品）。
if re.search(r"OnClicked\s*\+=\s*delegate[^}]*\br\.X\b", lv2):
    ok("行回调读行对象当前坐标（r.X/r.Y），不是闭包捕获")
elif re.search(r"byte\s+cx\s*=\s*jar\.x", lv2):
    err("行回调闭包捕获了 jar.x/jar.y —— 行复用时点击会指向旧格子")
else:
    w("未识别行回调写法，请人工确认")

# 9.14 指纹**绝不能**含 item.state —— 下游机械物品的 state 每一帧都在变，
#      入指纹会导致每帧 Rebuild → 每帧整行重建 → 图标（异步加载）永远显示不出来
#      （实测：截图里所有图标空白，日志中同一个 id 反复出现即可确诊）。
# ⚠ 参数列表用通配 `[^)]*`：本轮把 JarFingerprint(ItemJar) 改成 GroupFingerprint(ItemGroup)，
#   写死签名的正则直接匹配不上 → 检查被静默跳过（只剩一条 WARN），等于没查。
ok_state = True
# ⚠ 26.10.3.32：两条**数据指纹**（二维 / 扁平）的实现搬到了共享层 —— 判据跟过去，
#   并且**找不到就报错**（原先只是 WARN，静默跳过 = 这条判据等于不存在）。
for pat, tag, where in [
        (r"private static int (?:JarFingerprint|GroupFingerprint)\([^)]*\)\s*\{(.*?)\n        \}",
         "行指纹", lv2),
        (r"internal static int FingerprintItems\([^)]*\)\s*\{(.*?)\n        \}", "二维整体指纹", sh),
        (r"internal static int FingerprintJars\([^)]*\)\s*\{(.*?)\n        \}", "扁平指纹", sh)]:
    m_fp = re.search(pat, where, re.S)
    if m_fp is None:
        ok_state = False
        err("未找到" + tag + "函数体 —— 这条判据会静默失去作用（别让它跳过）")
    elif re.search(r"jar\.item\.state", m_fp.group(1)):
        ok_state = False
        err(tag + "里混入了 jar.item.state —— 会导致每帧重建、图标永远显示不出来")
if ok_state:
    ok("指纹不含 item.state（稳定，不会每帧重建）")

# 9.15 列表必须限高并自带滚动条。
#      实测反馈「右边给一个最大上限，不然上下高度太高了」+「加一个滑动条以显示完全物品」。
if "MaxListHeight" in lv2 and "CreateScrollView()" in lv2:
    ok("列表限高 + 内部滚动（MaxListHeight 属性 + ScrollView）")
else:
    err("缺少列表限高或内部滚动 —— 条目一多会把「附近物品」顶得很远")
if "ContentSizeOffset" in lv2 and re.search(r"Mathf\.Min\(", lv2):
    ok("滚动内容尺寸与可视高度分开设置（ContentSizeOffset + Mathf.Min）")
else:
    err("未正确分离「内容高」与「可视高」—— 滚动条会失效或滚不到底部")
# 行必须挂在滚动容器里（挂在面板上就滚不动）。
# ⚠ 用**函数体**定位，不要按变量名写字面量 —— 变量名一改断言就会假 FAIL
#   （本轮已踩过一次：把 r.Icon.Refresh 改成 icon.Refresh 后断言归零）。
# ⚠ 参数列表要用通配 `[^)]*`，别写死签名：签名一改（本轮把 ItemJar 换成 ItemGroup）
#   断言就会静默失去作用（表现为假 FAIL，或更糟 —— 改成宽松断言后变成永久通过）。
m_rb = re.search(r"private void RebuildRow\(RowUi r, [^)]*\)\s*\{(.*?)\n        \}",
                 lv2, re.S)
if m_rb is not None and "scroll.AddChild(" in m_rb.group(1):
    ok("行挂在滚动容器内（RebuildRow 内 scroll.AddChild）")
else:
    err("行没挂进滚动容器 —— 滚动条滚不动它们")

# 9.16 ScrollView 必须设这两个开关（本项目下游插件的已验证用法）。
#      缺 ScaleContentToWidth → 内容宽为 0 → 子元素 SizeScale_X=1 参照到 0 →
#        **整行宽度塌成 0**：文字不渲染、Tooltip 落不到、行背景不可见；
#        而图标是固定尺寸所以仍可见 —— 这个"只剩图标"的现象正是识别特征。
#      缺 HandleScrollWheel → 只能拖滚动条，鼠标滚轮无效。
if re.search(r"ScaleContentToWidth\s*=\s*true", lv2):
    ok("ScrollView 设了 ScaleContentToWidth（行宽才撑得满）")
else:
    err("缺 ScaleContentToWidth —— 行宽会塌成 0（文字/Tooltip/行背景全没）")
if re.search(r"HandleScrollWheel\s*=\s*true", lv2):
    ok("ScrollView 设了 HandleScrollWheel（滚轮可用）")
else:
    err("缺 HandleScrollWheel —— 滚轮滚不动，只能拖滚动条")

# 9.17 首次挂到 Glazier 树后必须**重建一次**，且必须**作废全部绑定**。
#      挂载前控件拿不到真实宽度（内容宽为 0）→ 那一刻建的行宽度为 0 →
#      行内文字（相对宽 = 行宽 - 54）落成负值 → **不渲染，且不会自己恢复**。
#      ⚠ 只 MarkDirty 不够：复用条件是「指纹相同就复用」，挂载前那批行指纹恰好相同
#        → 会被原样复用，坏尺寸被固化。必须 InvalidateAll 把绑定一并作废。
if re.search(r"panel\.Parent\s*!=\s*null\s*&&\s*!\s*wasAttached", lv2) and "InvalidateAll" in lv2:
    ok("首次挂载后强制重建一次，并作废全部行绑定（避免坏尺寸被复用固化）")
else:
    err("缺「挂载后重建 + 作废绑定」—— 刚打开时行宽为 0，文字不会显示且不会自愈")

# ================ 9.18 列表组件**公开化**（26.10.3.32 / T9） ================
# 为什么这一节本身算一条"能力"：此前列表那套只长在内置视图的私有字段里 ——
# 类 public 但面板/行池全 private，且**没有 Element/IsBuilt/DesiredHeight**
# ⟹ 第三方拿到实例也取不到可挂的 ISleekElement，IStorageUiRegion.RegionPanel
# 这条路**物理上关死**（2026-10-05《能力缺口盘点》§1 那条结构性缺口）。
_pub_need = [
    ("public ISleekElement Element", "Element（拿不到它 = 挂不上区块栈）"),
    ("public bool IsBuilt", "IsBuilt"),
    ("public float DesiredHeight", "DesiredHeight（宿主靠它累加区块栈高度）"),
    ("public void Build()", "Build"),
    ("public bool IsUsableAt", "IsUsableAt"),
    ("public void Hide()", "Hide（只藏不拆）"),
    ("public void Discard()", "Discard"),
    ("public void Refresh(Items", "Refresh(Items)"),
    ("public void Refresh(StorageSlotList", "Refresh(StorageSlotList)"),
    ("public void MarkDirty()", "MarkDirty"),
    ("public void InvalidateAll()", "InvalidateAll"),
]
_pub_miss = [name for sig, name in _pub_need if sig not in lv2]
if _pub_miss:
    err("列表组件缺公开成员：" + "、".join(_pub_miss) +
        " —— 第三方拿不到可用面板（盘点 §1 的缺口会重新出现）")
else:
    ok("列表组件公开面与 StorageGridPanel 对齐（Element/IsBuilt/DesiredHeight/生命周期齐全）")

# 旋钮必须是**属性**且 setter 会自动标脏 —— 否则"设了不生效"重演（§2.1/§2.3 那一族）
_pub_knobs = ["Title", "EmptyText", "MaxListHeight", "TooltipHint", "Interactive",
              "ShowSearch", "SearchQuery", "OnSlotClicked", "OnSlotRightClicked"]
_kb = []
for _k in _pub_knobs:
    # ⚠ 同上：只锁"是属性 + setter 会标脏"，不锁行版式（26.10.3.47 展开过 setter）。
    _m = re.search(r"public [^\n]*\b" + _k + r"\b\s*\n\s*\{([\s\S]{0,700}?)\n        \}", lv2)
    if _m is None:
        _kb.append(_k + "(没找到属性块)")
    elif "get" not in _m.group(1) or "set" not in _m.group(1):
        _kb.append(_k + "(不是 get/set 属性)")
    elif not ("MarkDirty()" in _m.group(1) or "Discard()" in _m.group(1)
              or "InvalidateAll()" in _m.group(1)):
        _kb.append(_k + "(setter 什么也没做)")
if _kb:
    err("列表组件的旋钮不合格：" + "、".join(_kb) + " —— 设了不生效（§2.1/§2.3 那一族）")
else:
    ok("列表组件的九个旋钮都是属性且 setter 会自动标脏（设了即生效）")

# 列表也带搜索栏 —— "能复用"与"有搜索"是同一件事的两个面（两条路合成一条）
# ★ 26.10.3.46：输入框本体改由**公开组件** `StorageSearchBox` 画（原来这里那份
#   `CreateStringField()` 与网格面板、蓝图视图**逐字相同**）—— 判据锁的事实没变：
#   列表有搜索栏、匹配走共享层、过滤在分组之前。
if "ShowSearch" in lv2 and "StorageSearchBox" in lv2 \
        and "MatchesQuery" in lv2 and "CollectJars" in lv2:
    ok("列表组件自带搜索栏（输入框交给公开组件，匹配走共享层，过滤在分组之前）")
else:
    err("列表组件没有搜索栏或没走共享层的匹配/收集 —— 两条路又分家了")

# ListView 必须只剩薄封装：机制若在它里面再写一份，迟早与面板漂移
_lvp_code = strip_comments(lvp)
_dups2 = [d for d in ("new SleekItemIcon()", "class RowUi", "CreateScrollView()",
                      "ClaimRow") if d in _lvp_code]
if _dups2:
    err("ListView 里又出现 " + "、".join(_dups2) + " —— 列表机制被复制成两份，迟早漂移")
else:
    ok("ListView 只剩 Provider 生命周期（列表机制只有一份实现）")
if "StorageListPanel" in lvp and "list.Element" in lvp:
    ok("ListView 把面板元素交给宿主（挂载走 StorageListPanel.Element）")
else:
    err("ListView 没用 StorageListPanel —— 薄封装没做起来")

# ---------------- 9.19 行池的「建立即认领」不变量（26.10.3.33，用户实测报的） ----------------
# 病根：末尾「清理闲置行」按 InUse 判，而新建的行**忘了标** ⟹ 同一趟里刚建好的行被自己人收走
# ⟹ 「N 项只显示 M 行」，且只在**池子需要长大那一趟**出现 ⟹ 关掉重开就好（自愈型假象）。
# 用户 2026-10-05 实测：箱子 3 项首开只画 2 行、关掉重开变 3 行；桥接测试列表同样。
# 锁法（不锁写法、锁事实）：**池子的新建入口必须唯一，且它自己标 InUse**。
_bp19 = src.get("CustomUI.BlueprintView.cs", "")
for _f19, _t19, _cls19 in ((lv2, "列表", "RowUi"), (_bp19, "蓝图", "BlueprintRow")):
    if not _f19:
        err("源文件缺失，9.19 判据无从检查")
        continue
    _adds19 = _f19.count("rows.Add(")
    if _adds19 == 1:
        ok(_t19 + "视图的行池只有**一个**新建入口（不会再有第二条「忘了标」的路）")
    else:
        err(_t19 + "视图里 rows.Add( 出现 " + str(_adds19) + " 处 —— 行池有了第二个新建入口，"
            "迟早有一处忘了标 InUse（那就是「N 项只显示 M 行」）")
    _new19 = fn_body(_f19, "private " + _cls19 + " NewClaimedRow", "NewClaimedRow")
    if _new19 is not None and "InUse = true" in strip_comments(_new19) \
            and "rows.Add(" in _new19:
        ok(_t19 + "视图：池里新建的行**建立即认领**（InUse = true）")
    else:
        err(_t19 + "视图的新建行没当场认领 —— 同一趟末尾的「清理闲置行」会把刚建的行藏起来，"
            "症状正是用户 2026-10-05 报的「N 项只显示 M 行」（且关掉重开就自愈）")
    # 清理闲置行的循环里不许再出现"新建"（否则又绕开闸口）
    # ⚠ 锚点必须是**代码**（那个 if 条件），不能锚注释 —— `strip_comments` 会把注释整段删掉，
    #   锚在注释上会让这条判据永远走 else 分支（26.10.3.33 我自己踩了一次）。
    _clean19 = re.search(r"if \(!rows\[i\]\.InUse && rows\[i\]\.Button != null\)([\s\S]{0,200}?)\}",
                         strip_comments(_f19))
    if _clean19 is not None and ("new " + _cls19) not in _clean19.group(1):
        ok(_t19 + "视图：「清理闲置行」只藏不建（不会顺手产生新的未认领行）")
    else:
        err(_t19 + "视图：「清理闲置行」那段找不到，或它在新建行 —— 闸口被绕开了")

# 诊断必须报**可见行数**并显式报警 —— 老日志只报"滚动容器控件 N 个"，
# 而"控件在树里却 IsVisible=false"是同一种病（控件数 ≥ 项数不代表画面正常）。
for _f19b, _t19b, _need19 in ((lv2, "列表", "可见 "), (_bp19, "蓝图", "可见 ")):
    if not _f19b:
        continue
    if _need19 in _f19b and "≠ 显示项数" in _f19b:
        ok(_t19b + "视图的诊断报了可见行数，并对「可见 ≠ 项数」显式报警")
    else:
        err(_t19b + "视图的诊断只看「控件数 ≥ 项数」—— 控件挂进容器却不可见时它一言不发"
            "（用户 2026-10-05 报的那条就是这样被漏掉的）")

# ------------------------------------- 10. 大格子视图（与第 9 节同源规则）
# 两个视图的**底层规则必须一致**（滚动开关、指纹稳定、换物品整行重建、
# 挂载后重建、交互同源），所以这里对 GridView 再跑一遍同款检查 ——
# 任一视图漏掉都会被抓住，不会因为"另一个视图已经写过"而漏掉。
out.append("")
out.append("10) 大格子视图（GridView + 公开的 StorageGridPanel）")
# ⚠ 网格机制已从 GridView 搬进公开组件 StorageGridPanel（B1：让第三方也能画任意 Items 页），
#   GridView 只剩 Provider 生命周期。所以本节判的是**这套机制**，跨两个文件一起看 ——
#   判据的意义没变（"这套网格实现必须满足 X"），只是逻辑换了归属。
gv = src.get("CustomUI.GridView.cs", "") + "\n" + src.get("CustomUI.GridPanel.cs", "")

if not gv.strip():
    err("缺少 CustomUI.GridView.cs / CustomUI.GridPanel.cs")
else:
    # 10.1 一行 4 格（默认列数 + 相对宽度：列数的倒数，不是像素）
    # ⚠ 26.10.3.27 起默认值写在私有字段 `_columns` 上（Columns 改成属性、setter 自动标脏）
    #   ⟹ 判据**允许两种写法**：判的是"默认 4 列"这个事实，不是"它长什么样"。
    if (re.search(r"public int Columns\s*=\s*4", gv) or re.search(r"_columns\s*=\s*4\s*;", gv)) and \
            re.search(r"cellScaleX\s*=\s*1f\s*/\s*columns", gv):
        ok("一行 4 格（列数可配，格宽 = 1f / 列数 的相对值）")
    else:
        err("格数配置不对 —— 应为默认 4 列、格宽用 1f/列数 的相对值")

    # 10.2 ScrollView 两个开关
    if re.search(r"ScaleContentToWidth\s*=\s*true", gv) and re.search(r"HandleScrollWheel\s*=\s*true", gv):
        ok("ScrollView 两个开关齐全（ScaleContentToWidth / HandleScrollWheel）")
    else:
        err("GridView 的 ScrollView 缺开关 —— 格子宽度会塌成 0 / 滚轮失效")

    # 10.3 限高 + 内部滚动（上限字段 B1 之后叫 MaxListHeight，内置视图仍用默认 510）
    # ⚠ 26.10.3.27 起默认值在私有字段 `_maxListHeight`（MaxListHeight 改成属性）⟹ 两种写法都认。
    if (re.search(r"MaxListHeight\s*=\s*510f", gv) or re.search(r"_maxListHeight\s*=\s*510f", gv)) \
            and "CreateScrollView()" in gv \
            and "ContentSizeOffset" in gv and "Mathf.Min(contentH, MaxListHeight)" in gv:
        ok("限高 + 内部滚动（MaxListHeight + ContentSizeOffset + Mathf.Min）")
    else:
        err("GridView 未限高或未设滚动内容尺寸")

    # 10.4 指纹不得含 item.state
    # ⚠ 只查**指纹函数体** —— `icon.Refresh(..., jar.item.state, ...)` 是正当用法
    #   （刷新图标时读一次），全文件搜索会把它误判成违规（这里踩过）。
    fp_bad = False
    for m_fp_gv in re.finditer(
            r"private static int (?:CellFingerprint|Fingerprint)\([^)]*\)\s*\{(.*?)\n        \}",
            gv, re.S):
        if re.search(r"jar\.item\.state", m_fp_gv.group(1)):
            fp_bad = True
    if fp_bad:
        err("GridView 指纹混入了 jar.item.state —— 会每帧重建、图标显示不出来")
    else:
        ok("GridView 指纹不含 item.state（刷新图标时读 state 不算）")

    # 10.5 换物品必须整格重建
    if gv.count("new SleekItemIcon()") == 1 and re.search(r"scroll\.RemoveChild\(cell\.Button\)", gv):
        ok("换物品时整格重建（new SleekItemIcon 只 1 处 + 先摘旧格）")
    else:
        err("GridView 未整格重建 —— 图标会加载不出来 / 旧格越积越多")

    # 10.6 首次挂载后重建 + 作废全部绑定（与列表视图同款）
    if re.search(r"panel\.Parent\s*!=\s*null\s*&&\s*!\s*wasAttached", gv) and "InvalidateAll" in gv:
        ok("首次挂载后强制重建一次，并作废全部格子绑定")
    else:
        err("GridView 缺「挂载后重建 + 作废绑定」—— 刚打开时格子宽度为 0，文字不显示")

    # 10.7 交互必须走共享层，且不得自己实现搬运
    if "StorageUiShared.HandlePrimaryClick" in gv and "StorageUiShared.HandleSecondaryClick" in gv:
        ok("交互走共享层（与列表视图同源）")
    else:
        err("GridView 没走共享层交互 —— 两个视图手感会不一致")
    # ⚠ 反向判据必须先剔注释：组件的注释里就写着「搬运走 sendDragItem(page,x,y)」，
    #   不剔会把这句说明当成违规（本项目第七次踩同一条）。
    if re.search(r"sendDragItem|sendDropItem", strip_comments(gv)):
        err("GridView 自己实现了搬运 —— 应统一走 StorageUiShared")
    else:
        ok("GridView 未自行实现搬运（无重复实现）")

    # 10.7b 格子必须按「序号」绑定（cells[slot]），不得退回「按指纹认领空闲格」。
    #       实测「18 件只画出 13 件」就是这么来的：按指纹认领会认领到上一次界面的
    #       残留控件（其父节点已随旧画布销毁）→ 那个格子永远不显示；
    #       另外同款物品除第一件外每轮都认领失败 → 每轮整格重建（日志实测 13/18）。
    if re.search(r"cells\[slot\]", gv) and "ClaimCell(" not in gv:
        ok("格子按序号绑定（cells[slot]），不再按指纹认领")
    else:
        err("格子又变成按指纹认领了 —— 会复用上次界面的残留控件（界面缺格）")

    # 10.7c 跨代复用必须被拦住。
    #       跨代的控件其父节点已随旧画布销毁，复用它 = 永远不显示的格子。
    if "Epoch" in gv and re.search(r"cell\.Epoch\s*==\s*epoch", gv):
        ok("跨代复用被拦住（cell.Epoch == epoch）")
    else:
        err("没有跨代复用保护 —— 复用上次界面的控件会导致格子整片不显示")

    # 10.7d 格内文字行高必须 >= 24。
    #       实测把行高写成 13（ESleekFontSize.Small）时，格子里**一个字都不渲染**，
    #       且不报任何错 —— 项目内已验证的小号标签最小高度是 24（下游插件 StatusH）。
    #   ★ 26.10.3.66：行高从"写死常量"改成了"**下限** + 按 `LabelFontSize` 算" ⟹ 判据跟着升级：
    #     ① 三个**下限**常量都 ≥ 24；② 派生里确实**取较大者**（否则字号一调大文字就被挤掉）。
    _lows = re.findall(r"(?:NAME_MIN_H|INFO_MIN_H|MIN_ROW_HEIGHT)\s*=\s*(\d+(?:\.\d+)?)f",
                       src.get("CustomUI.GridPanel.cs", "")
                       + "\n" + src.get("CustomUI.ListPanel.cs", ""))
    if len(_lows) >= 3 and all(float(h) >= 24 for h in _lows):
        ok("文字行高**下限**齐全且 >= 24（" + " / ".join(sorted(set(_lows))) + "）—— 字号可配也不会掉到 24 以下")
    else:
        err("文字行高下限偏低或缺失（" + " / ".join(_lows) + "）—— 实测 < 24 时文字完全不渲染")
    _gp_c = strip_comments(src.get("CustomUI.GridPanel.cs", ""))
    if "IdealTextHeight(" in _gp_c and re.search(r"return need > NAME_MIN_H \? need : NAME_MIN_H", _gp_c):
        ok("名称行高按字号**取较大者**（调大字号只会把行撑开，不会把字挤掉）")
    else:
        err("名称行高没按字号取较大者 —— 字号一调大文字就被挤掉（而且不会报错）")
    _lp_c = strip_comments(src.get("CustomUI.ListPanel.cs", ""))
    if re.search(r"if \(need < MIN_ROW_HEIGHT\) need = MIN_ROW_HEIGHT", _lp_c):
        ok("列表行高**兜底夹到 24**（行的标签高度 = 行高，低于 24 一个字都不渲染）")
    else:
        err("列表行高没有兜底夹到 24 —— 行内文字会整片消失（且不报错）")

# 10.8 共享层自身是否提供全部入口
if not sh:
    err("缺少 CustomUI.Shared.cs")
else:
    need = ["HandlePrimaryClick", "HandleSecondaryClick", "TakeToBackpack",
            "DropItem", "ShowItemInfo", "PlayAudio", "BuildTooltip"]
    miss = [m for m in need if m not in sh]
    if not miss:
        ok("共享层提供全部交互/文本入口（" + str(len(need)) + " 项）")
    else:
        err("共享层缺：" + ", ".join(miss))

# 10.9 Ctrl 判定不得散落在视图里（交互应只在共享层实现）
if re.search(r"InputEx\.GetKey\(ControlsSettings\.other\)", lv2) or \
   re.search(r"InputEx\.GetKey\(ControlsSettings\.other\)", gv):
    err("视图里直接判 Ctrl —— 交互应只在共享层实现")
else:
    ok("Ctrl 判定只在共享层（视图不自行实现交互）")

# 10.10 按箱型绑定视图
if "BindBox" in prov and "boxBindings" in prov:
    ok("支持按箱型绑定视图（StorageUiRegistry.BindBox）")
else:
    err("缺按箱型绑定视图的能力")

# 10.11 默认视图由配置决定，且大格子与列表的优先级必须**互斥**
#       （同一条配置分支下两者取值不同 —— 否则 Find 取谁取决于排序稳定性）。
#       ⚠ 判据不写死条件变量名（原来是 gridIsDefault，加蓝图视图后改成
#       listIsDefault + blueprintIsDefault，写死签名会变假 FAIL —— MEMORY 里的老坑）。
cs3 = src.get("CustomUI.cs", "")
if "ApplyDefaultView" in cs3 and "currentPriority" in cs3:
    m_grid = re.search(r"StorageGridView\.currentPriority\s*=\s*(\w+)\s*\?\s*(-?\d+)\s*:\s*(-?\d+)", cs3)
    m_list = re.search(r"StorageListView\.currentPriority\s*=\s*(\w+)\s*\?\s*(-?\d+)\s*:\s*(-?\d+)", cs3)
    if m_grid is None or m_list is None:
        err("认不出大格子/列表的优先级赋值 —— 写法改了要同步这条判据")
    elif m_grid.group(1) != m_list.group(1):
        err("大格子与列表用了不同条件变量，无法证明互斥：" +
            m_grid.group(1) + " vs " + m_list.group(1))
    elif m_grid.group(2) == m_list.group(2) or m_grid.group(3) == m_list.group(3):
        err("两视图优先级未做成互斥 —— 可能出现「有时列表有时大格子」的不确定行为")
    else:
        ok("默认视图由配置决定，且大格子/列表在任一分支下优先级都互斥（" +
           m_grid.group(1) + " → " + m_grid.group(2) + "/" + m_grid.group(3) + "）")
else:
    err("缺 ApplyDefaultView / currentPriority —— 默认视图没做成配置项")

# 10.12 合并 + 排序都必须由共享层统一提供，且两个视图都用它。
#       实测反馈「格子应该排序，现在这样有点乱」，随后「将相同的物品合并显示」——
#       两者都定义在共享层：容器内部顺序取决于放置历史（直接用会乱），
#       而两个视图若各排各的 / 各并各的，切换视图物品会「跳位置」或「数量不一致」。
if "CollectGroups" in sh and "StorageUiShared.CollectGroups" in gv \
        and "StorageUiShared.CollectGroups" in lv2:
    ok("两个视图共用共享层「合并 + 排序」（CollectGroups）")
else:
    err("合并/排序没走共享层 / 有一个视图没用 —— 切换视图会跳位置或数量不一致")

# ------------------------- 11. 托起（左键拿起）/ 放入 + 耐久显示门控
out.append("")
out.append("11) 托起 / 放入 + 耐久显示门控")

# 11.1 耐久必须过 asset.showQuality。
#      实测反馈「给没有耐久的物品不显示耐久」—— 只用 quality > 0 判断是**错的**：
#      无耐久物品的 quality 也是满值 100，于是金属线/超织物会一直显示「100%」。
#      引擎判据 = `ItemAsset.showQuality`（原版 SleekItem.cs:181 自己也是这么用的；
#      子类各覆盖：枪/近战/服装恒 true、弹匣 stuck>0、消耗品 FOOD/WATER、dat 键 Override_Show_Quality）。
#      ⚠ 逐个**函数体内**查，否则注释里提到 showQuality 也会算过（假绿）。
dur_targets = [
    (r"internal static string BuildRowText\(ItemAsset asset, ItemGroup g\)\s*\{(.*?)\n        \}", "列表行文本"),
    (r"internal static string BuildAmountText\(ItemAsset asset, ItemGroup g\)\s*\{(.*?)\n        \}", "格子第二行"),
]
miss_shq = []
for pat, tag in dur_targets:
    mq = re.search(pat, sh, re.S)
    if mq is None:
        miss_shq.append(tag + "(函数未找到)")
    elif "showQuality" not in mq.group(1):
        miss_shq.append(tag)

# ★ 26.10.3.63：**悬浮提示的耐久门控搬到了 `StorageUiTooltip.ItemHead`** ——
#   实现搬家时判据要跟着搬（否则判据指着一段已经没有这个逻辑的代码）。
#   ⚠ 并且要**升一级**：光判"新家有 showQuality"还不够 —— 还得判 `BuildTooltip` **真的委托**
#     给了公开面（否则以后有人在 `BuildTooltip` 里重写一份，措辞又会漂开）。
_tl_src = src.get("CustomUI.Tooltip.cs", "")
_mhead = re.search(r"internal static string ItemHead\(ItemAsset asset, int quality\)\s*\{(.*?)\n        \}",
                   _tl_src, re.S)
if _mhead is None:
    miss_shq.append("悬浮提示(ItemHead 未找到)")
elif "showQuality" not in _mhead.group(1):
    miss_shq.append("悬浮提示(ItemHead)")

if not miss_shq:
    ok("耐久显示全部过 asset.showQuality（无耐久物品不再显示 100%）")
else:
    err("这些文本没过 showQuality 门控：" + ", ".join(miss_shq))

# 11.1b 悬浮提示**只有一处口径**：`BuildTooltip` 必须委托给公开的 `StorageUiTooltip`
#   （非容器那一支），不许自己再拼一遍名称/描述/耐久/件数/格数。
_mbt = re.search(r"internal static string BuildTooltip\(ItemAsset asset, ItemGroup g\)\s*\{(.*?)\n        \}",
                 sh, re.S)
_btb = _mbt.group(1) if _mbt is not None else ""
if "StorageUiTooltip.ForItem(" in _btb and "StorageUiTooltip.ItemHead(" in _btb \
        and "StorageUiTooltip.SizeLine(" in _btb:
    ok("悬浮提示正文**只有一处口径**（`BuildTooltip` 委托给公开的 `StorageUiTooltip`）")
else:
    err("`BuildTooltip` 没委托给 `StorageUiTooltip` —— 它又自己拼了一份正文（措辞会与公开面漂开）")
if "asset.FriendlyName" in _btb:
    err("`BuildTooltip` 里又出现了 `asset.FriendlyName` —— 正文口径重复了（该走 ItemHead）")
else:
    ok("`BuildTooltip` 里没有再自己拼名称（正文只有 ItemHead 一处）")

# 11.1c 「正文 + 自定义说明」也只有一处口径：三处拼装点都必须调 `StorageUiTooltip.Compose`。
#   ⚠ 反向锁：不许再出现 `+ "\n" + TooltipHint` 这种"各写一遍"的写法 ——
#     本轮（26.10.3.63）就是三处各写一遍，谁改一处另两处就漂。
_app_sites = [("CustomUI.GridPanel.cs", "大格子"), ("CustomUI.ListPanel.cs", "列表"),
              ("CustomUI.ItemIcon.cs", "图标格")]
_missing = []
_bare = []
for (_fn, _tag) in _app_sites:
    _c = strip_comments(src.get(_fn, ""))
    if "StorageUiTooltip.Compose(" not in _c:
        _missing.append(_tag)
    if re.search(r'\+\s*"\\n"\s*\+\s*_?TooltipHint', _c):
        _bare.append(_tag)
if not _missing:
    ok("三处「正文 + 说明」拼装都走 `StorageUiTooltip.Compose`（大格子 / 列表 / 图标格）")
else:
    err("有拼装点没走统一口径：" + ", ".join(_missing))
if _bare:
    err("这些地方还在裸拼 `+ \"\\n\" + TooltipHint`：" + ", ".join(_bare))
else:
    ok("没有裸拼 `+ \"\\n\" + TooltipHint` 的残留（拼装只有一处口径）")

# 11.2 左键必须能「托起」，且反射失败时有兜底 —— 否则反射一变，左键就彻底没反应。
m_pc = re.search(r"internal static bool HandlePrimaryClick\(byte x, byte y\)\s*\{(.*?)\n        \}",
                 sh, re.S)
if m_pc is not None and "BeginGrab" in m_pc.group(1) and "TakeToBackpack" in m_pc.group(1):
    ok("左键托起，且反射不可用时回退「直接取回背包」")
else:
    err("左键缺托起或缺少兜底 —— 反射一变左键就彻底没反应")

# 11.3 托起必须**借用原版**拖拽机制，不得自建预览/状态。
#      原版的预览控件 dragItem 挂在 PlayerUI.container 上、每帧由 PlayerUI.update →
#      updateDraggedItem 驱动跟随鼠标；自己重写等于再实现一遍且手感必然不同。
if "fDragItem" in sh and "preview.updateItem(jar)" in sh and "mStartDrag" in sh:
    ok("托起借用原版拖拽机制（dragItem.updateItem + startDrag）")
else:
    err("托起没有复用原版拖拽控件 —— 会出现两套拖拽状态")

# 11.4 dragSource 必须显式置**非空**。
#      原版 onPlacedItem 第一句是 `if (dragSource != null && isDragging)` ——
#      为空则「放到背包格」整条路径都被跳过（点了没反应），且不报错。
if re.search(r"fDragSource\s*\.\s*SetValue\(null,\s*preview\)", sh):
    ok("dragSource 显式置非空（否则放到背包格静默无效）")
else:
    err("没有置 dragSource —— 原版 onPlacedItem 会整条跳过，放到背包格没反应")

# 11.5 放入必须先 stopDrag 再 sendDragItem（与原版 onPlacedItem 顺序一致）。
i_send = sh.find("player.inventory.sendDragItem(fromPage, fromX, fromY")
i_stop = sh.rfind("EndGrab();", 0, i_send) if i_send >= 0 else -1
if i_send >= 0 and i_stop >= 0:
    ok("放入时先 stopDrag 再 sendDragItem（顺序与原版一致）")
else:
    err("放入顺序不对 —— 应先 stopDrag 再 sendDragItem")

# 11.6 左键三态齐全：托起 / Ctrl 丢弃 / 托起中=落点
if "IsDragging) return DropIntoStorage" in sh and "if (IsCtrlDown()) return DropItem" in sh:
    ok("左键三态齐全（托起 / Ctrl+左键丢弃 / 托起中左键=落点）")
else:
    err("左键语义不全 —— 托起、Ctrl 丢弃、落点 三者缺一")

# ------------------------------ 12. 合并同类显示（相同物品合并 + 数量汇总）
out.append("")
out.append("12) 合并同类显示")

# 12.1 合并判据必须是「物品 ID + 耐久」，且**不得含 item.state**。
#      为什么不能含 state：下游物品把 UID/电量这类数据存在 item.state 里（见项目记忆），
#      每件都不同 → 带上它永远合并不了；而且 state 可能每帧变化 → 合并结果每帧抖动。
#      它对玩家也是不可见的（图标/信息面板都不显示它）。
if "GroupKey" in sh:
    # ⚠ 判据要看**带完整策略的那一版**（少参版现在只是转发默认值）——
    #   指向被搬走的代码是本项目的老毛病（假 FAIL 第 N 次）。
    m_gk = re.search(r"internal static int GroupKey\(ItemJar jar, bool idOnly, "
                     r"bool mergeContentPages\)\s*\{(.*?)\n        \}", sh, re.S)
    if m_gk is None:
        err("找不到 GroupKey 函数体，无法确认合并判据")
    elif re.search(r"item\.state", m_gk.group(1)):
        err("合并判据里混进了 item.state —— 下游物品每件 state 都不同，会永远合并不了")
    elif "item.id" in m_gk.group(1) and "quality" in m_gk.group(1) and "idOnly" in m_gk.group(1):
        ok("合并判据 = 物品 ID（+耐久，除非调用点要只按 ID），不含 state")
    else:
        err("合并判据不含 ID/耐久")
else:
    err("缺 GroupKey —— 合并判据没有集中定义")

# 12.2 数量必须是**累加**（Σ item.amount），不是数 jar 个数。
#      实测要求「数量 * n（对应的数量）」：9 个各含 1 件的金属线显示 ×9；
#      2 个各含 30 发的弹匣应显示 ×60。
if re.search(r"TotalAmount\s*\+=\s*jar\.item\.amount", sh):
    ok("数量按 Σ item.amount 累加（堆叠物品也正确）")
else:
    err("数量不是累加 —— 堆叠物品（如 30 发弹匣）会少算")

# 12.3 合并件数/总数必须进**视图指纹**，否则拿走一件时 ×N 不刷新
#      （视图会判定「这一格没变」而原样复用旧文字）。
_fp_has_count = r"int h = g\.First\.item\.id;[\s\S]{0,240}?h = h \* 397 \^ g\.Count;"
if re.search(_fp_has_count, gv) and re.search(_fp_has_count, lv2):
    ok("两个视图的指纹都含合并件数/总数（拿走一件时 ×N 会刷新）")
else:
    err("视图指纹没含件数 —— 从 9 件里拿走 1 件时 ×8 不会更新")

# 12.4 交互必须作用在**代表物品**上（取回/托起/丢弃都只动一件）。
if re.search(r"cell\.X = g\.First\.x", gv) and re.search(r"row\.X = g\.First\.x", lv2):
    ok("交互坐标取代表物品（一次只对一件生效）")
else:
    err("交互没用代表物品的坐标 —— 会作用到错误的那一件")

# 12.5 头部必须同时写「件数」和「项数」。
#      合并后 项数 ≠ 件数，只写项数会让玩家以为箱子里只有那几件。
if re.search(r"totalItems", gv) and re.search(r"totalItems", lv2):
    ok("头部同时显示件数与项数（合并后不会被误读）")
else:
    err("头部只显示项数 —— 合并后会让人误以为箱子里只有那几件")

# 12.6 头部必须显示「占用格数」而不是件数/项数当容量。
#      实测反馈：48 格塞满后头部写「18 件 · 2 项 / 48 格」，看起来像还剩 46 格。
if "BuildHeaderText" in sh and "BuildHeaderText" in gv and "BuildHeaderText" in lv2:
    ok("头部统一走共享层 BuildHeaderText（两视图同款）")
else:
    err("头部没走共享层 BuildHeaderText —— 两个视图会显示不一样的统计")

if re.search(r"OccupiedCells\s*\+\s*/\s*\+\s*|占用 \" \+ s\.OccupiedCells", sh):
    ok("头部显示「占用 X/Y 格」（而不是件数当容量）")
else:
    err("头部没显示占用格数 —— 满箱仍显示「1/64」，玩家会误以为还有空间")

# 12.7 必须计算「最大连续空位」—— 这是碎片化的可见化。
#      实测反馈：2×2 放不进、取出几个 1×1 又放得回 = tryFindSpace 要求连续矩形，
#      而合并列表把空洞藏起来了。
if re.search(r"ComputeSpace", sh) and re.search(r"ComputeSpace", gv) and re.search(r"ComputeSpace", lv2):
    ok("两个视图都调用 ComputeSpace（占用 + 最大连续空位）")
else:
    err("头部没算最大连续空位 —— 玩家无法预判 2×2 放不放得下")

m_cs = re.search(r"internal static SpaceInfo ComputeSpace\(Items container\)\s*\{(.*?)\n        \}", sh, re.S)
if m_cs is not None:
    body = m_cs.group(1)
    if "rot % 2 == 1" in body:
        ok("占用计算处理了旋转（rot%2==1 时宽高互换，与引擎 resize 一致）")
    else:
        err("占用计算没处理旋转 —— 旋转过的物品占用格会算错")
    if "MaxFreeW" in body and "MaxFreeH" in body:
        ok("计算了最大连续空位矩形（MaxFreeW/MaxFreeH）")
    else:
        err("没算最大连续空位矩形")
else:
    err("找不到 ComputeSpace 函数体（签名变了？断言用通配参数）")

# ---- 12.x 空容器（amount == 0）必须有字 —— 实测反馈：空弹匣只剩图标和名字 ----
#   根因：合成弹匣时引擎的 DeleteAmount(..., alwaysDeleteAtZeroAmount:false) **故意留着**空容器，
#   于是 item.amount == 0；而旧的数量文本写成「TotalAmount > 1 才写字」→ 整格什么都不显示。
bc = fn_body(sh, "internal static string BuildCountText(", "BuildCountText")
if bc is None:
    err("找不到 BuildCountText —— 数量文本又散回各处各写一遍了")
else:
    if "TotalAmount == 0" in bc and "Count" in bc:
        ok("数量文本处理空容器：合计 0 时按件数报（两个空弹匣要看得出是两件）")
    else:
        err("BuildCountText 没处理 TotalAmount == 0 —— 空弹匣在大格子里又会只剩图标和名字")
    if "TotalAmount > 1" in bc:
        ok("正常堆叠的显示与旧行为一致（×9 金属线不变，只是补上了以前空白的情况）")
    else:
        err("BuildCountText 丢了 TotalAmount > 1 那条 → ×N 的既有显示被改坏")
_refs = len(re.findall(r"BuildCountText\(g\)", sh))
if _refs >= 2:
    ok("格子第二行 / 列表行都走同一份数量文本（共 " + str(_refs) + " 处调用）")
else:
    err("只有 " + str(_refs) + " 处用 BuildCountText —— 数量文本又被分头重写，空容器会只在其中一处修好")

# ---- 12.y 范围收口：绝不碰「不是当前打开的那个箱子」的容器 ----
#   （用户实测决定：扣箱合成只扣当前箱子 + 背包；工作区另有插件会把别的箱子
#    注入进 PlayerInventory.SearchContents 的结果里，不挡就会扣到别人箱子里。）
#   （crc 要到第 16 节才定义，这里就地取一份，别依赖后面的变量）
_craft_src = src.get("CustomUI.Crafting.cs", "")
if "PruneForeignContainers" in _craft_src:
    ok("搜索结果会剔除非当前容器的 storage 页 jar（ReferenceEquals 认容器）")
else:
    err("缺 PruneForeignContainers —— 被其它插件注入的远处箱子物品会被我们当成本箱材料扣掉")

# ---------------------------- 13. 联机 / 专用服务器安全性
out.append("")
out.append("13) 联机 / 专用服务器（装了不该炸，但功能只在客户端生效）")

# 13.1 每帧入口必须先在「没有本地玩家」时返回。
#      专用服务器上 Player.LocalPlayer 恒为 null —— 若不判，第一帧就会 NRE 刷屏。
if re.search(r"Player\.LocalPlayer;[\s\S]{0,120}?if \(player == null \|\| player\.inventory == null\)\s*\{?[\s\S]{0,80}?return;", host):
    ok("Host.Tick 在无 LocalPlayer（专用服务器）时直接返回")
else:
    err("Host.Tick 没判 LocalPlayer —— 专用服务器上会每帧 NRE")

# 13.2 TryOpen 自己要判，不能只依赖调用方。
#      ⚠ 窗口要开够（TryOpen 里在空判之前还有窄屏检查等约 20 行），否则会假 FAIL。
#      ⚠ 返回写法有两种：`return false;` 或 `return Reject("...");`（带原因的静默拒绝
#        记录器）。判据只认第一种的话，加诊断日志就会报假 FAIL —— 查的是"有没有空判"，
#        不是"用哪个返回语句"。
if re.search(r"internal static bool TryOpen\([^)]*\)[\s\S]{0,2500}?"
             r"if \(player == null \|\| player\.inventory == null\)\s*\n?\s*return (false|Reject\()", host):
    ok("TryOpen 自带 LocalPlayer 空判（不依赖调用方）")
else:
    err("TryOpen 没判 LocalPlayer —— 补丁路径一旦变化就会在服务器上炸")

# 13.2b ★ 失败必须留原因。症状是"原版网格还在 / 界面自己没了"，
#       而日志一条都不说的时候，排查只能靠猜（2026-10-04 绕了一整轮）。
if "lastOpenReject" in host and "借道界面接管失败：" in host:
    ok("接管失败会打出被拒的原因（不留『静默回退原版』的死角）")
else:
    err("OpenVirtual 失败路径没记原因 —— 玩家只看到原版网格，日志零线索")
if "界面关闭（原因：" in host:
    ok("关闭检测把命中的那一条条件写进日志（六个条件压成静默 Close 是排查黑洞）")
else:
    err("Host.Tick 的 forceClose 仍然是静默的")
if "原版 closeStorage 被调用" in src.get("CustomUI.Patches.cs", ""):
    ok("外部调 closeStorage 关掉借道会话时也留痕（区分『我们自己关的』和『别人关的』）")
else:
    err("Patch_CloseStorage 没留痕 —— 分不清是谁关的界面")

# 13.3 打开检测不得依赖服务端字段 inv.storage（联机客户端恒 null）。
#      （第 1 节已有，这里再钉一次，因为它是联机能不能用的关键。）
if re.search(r"inv\.isStoring\s*&&\s*\(\s*!\s*inv\.isStorageTrunk\s*\|\|\s*VirtualActive\s*\)", host):
    ok("打开检测走 isStoring && (!isStorageTrunk || 借道标记)（联机客户端也成立）")
else:
    err("打开检测依赖服务端字段 —— 联机客户端永远打不开界面")

# 13.4 所有物品搬运必须走原版 RPC（sendDragItem/sendDropItem），
#      不得在客户端直接改 items 数组 —— 那样联机会不同步、也等于开了作弊口子。
if re.search(r"sendDragItem|sendDropItem", sh) and not re.search(r"\.addItem\(|\.removeItem\(", sh):
    ok("搬运只走原版 RPC（sendDragItem/sendDropItem），客户端不直接改数组")
else:
    err("共享层直接改 items 数组 —— 联机会不同步 / 绕过服务端校验")

# ---------------------------- 14. 蓝图视图（只读 / 零反射 / 口径与引擎一致）
out.append("")
out.append("14) 蓝图视图（CustomUI.BlueprintView.cs）")

bv = src.get("CustomUI.BlueprintView.cs", "")
if not bv:
    err("源文件缺失：CustomUI.BlueprintView.cs")
else:
    # 本节的判据只针对**代码**：注释里大量出现引擎的 internal 成员名（是为了说明
    # 「为什么不用它们」），不剥注释会全部误判。剔注释用文件级的 strip_comments。
    bvc = strip_comments(bv)

    # 自检窗口只覆盖「8 空格缩进的成员函数体」；参数列表用通配 [^)]*
    # （写死签名会在一改名字后静默失去作用 —— 见 MEMORY 的 ⚠⚠ 条）
    def body_of(sig, label):
        # 统一委托模块级 fn_body（同一份正则别抄三遍）。
        # 本节历史契约是「找不到返回 ""」，这里保持不改。
        b = fn_body(bv, sig, label)
        return b if b is not None else ""

    # ---- 14.0 依赖的引擎签名逐个核对（SDK 源码为准） ----
    # 读 SDK 用模块级 sdk_read（别在这里再写一份）
    sdk_checks = [
        ("Bundles/Assets.cs", r"public static void find<T>\(List<T> results\) where T : class",
         "Assets.find<T>(List<T>) 是 public（蓝图枚举不必反射）"),
        ("Bundles/CraftingAsset.cs", r"public interface IBlueprintOwner",
         "IBlueprintOwner 是 public 接口"),
        ("Bundles/CraftingAsset.cs", r"public List<Blueprint> GetBlueprints\(\)",
         "GetBlueprints() 是 public"),
        ("Inventory/Blueprint.cs", r"public bool ContainsAnyOfItems\(HashSet<ItemAsset> availableItems\)",
         "ContainsAnyOfItems 是 public（候选粗筛用引擎自己的判据）"),
        ("Inventory/Blueprint.cs", r"public CachingAssetRef\[\] GetApplicableRequiredNearbyCraftingTags\(\)",
         "GetApplicableRequiredNearbyCraftingTags 是 public"),
        ("Inventory/Blueprint.cs", r"public bool RequiresSkill", "RequiresSkill 是 public"),
        ("Inventory/Blueprint.cs", r"public int GetPlayerSkillLevel\(Player player\)",
         "GetPlayerSkillLevel 是 public"),
        ("Inventory/BlueprintSupply.cs", r"public bool ShouldConsume",
         "ShouldConsume 是 public（区分材料与工具）"),
        ("Inventory/BlueprintSupply.cs", r"public ECraftingInputCountingMethod CountingMethod",
         "CountingMethod 是 public"),
        ("Player/PlayerCrafting.cs", r"public void UpdateAvailableCraftingTags\(\)",
         "UpdateAvailableCraftingTags 是 public"),
        ("Player/PlayerCrafting.cs", r"public bool isBlueprintBlacklisted\(Blueprint blueprint\)",
         "isBlueprintBlacklisted 是 public"),
        ("Inventory/Items.cs", r"public ItemJar getItem\(byte index\)", "Items.getItem(byte) 是 public"),
        ("Level/Level.cs", r"public static LevelInfo info", "Level.info 是 public"),
    ]
    for (rel, pat, name) in sdk_checks:
        t = sdk_read(rel)
        if t is None:
            w("SDK 文件缺失，跳过：" + rel)
        elif re.search(pat, t):
            ok(name)
        else:
            err("API 签名对不上（本视图的可行性依据变了）：" + name)

    # ★ 全插件最要紧的一条口径：原版**不会**从正打开的箱子里扣消耗材料。
    #   如果哪天引擎改了这行，视图的「可合成」含义就变了，必须知道。
    pc = sdk_read("Player/PlayerCrafting.cs")
    if pc is not None:
        if re.search(r"IncludeActiveStorageContainer\s*=\s*!inputItemConfig\.ShouldConsume", pc):
            ok("引擎口径未变：消耗类材料 IncludeActiveStorageContainer = !ShouldConsume"
               "（箱子敞开也不算可用）")
        else:
            err("引擎口径可能变了：搜不到 IncludeActiveStorageContainer = !ShouldConsume —— "
                "蓝图视图的「可合成 / 取出即可」分档需要重新评估")

    # ---- 14.1 只读承诺 ----
    ro = re.findall(r"sendDragItem|sendDropItem|sendUpdateAmount|sendUpdateQuality|"
                    r"sendUpdateInvState|sendCraft|askCraft|SendRequestToCraft|"
                    r"\bremoveItem\(|\baddItem\(|DeleteAmount", bvc)
    if ro:
        err("蓝图视图自己动了数据/执行了合成（改数据只能经 StorageCrafting 一条路）：" +
            ", ".join(sorted(set(ro))))
    else:
        ok("蓝图视图不直接碰数据：写入一律委派 StorageCrafting（其自身判据见第 16 节）")

    # ---- 14.2 零反射（本视图的设计前提） ----
    if re.search(r"AccessTools|GetField\(|GetMethod\(|BindingFlags|MethodInfo|FieldInfo", bvc):
        err("蓝图视图用了反射 —— 它的设计前提是全走 public API")
    else:
        ok("零反射：游戏改内部实现不会静默失效")

    # ---- 14.3 不碰引擎 internal ----
    internal_names = ["GetBlueprintOwners", "BlueprintStatus",
                      "IsBlueprintPermanentlyDisabled", "UpdateBlueprintStaticStatus",
                      "UpdateBlueprintDynamicStatus", "GatherUniqueInputItems",
                      "HasCurrentAssetMappingChanged"]
    used_internal = [n for n in internal_names if n in bvc]
    if used_internal:
        for n in used_internal:
            err("用了引擎 internal 成员 " + n + "（直接写会编译失败；反射绕过则一改名就废）")
    else:
        ok("未使用任何已知 internal 成员（" + str(len(internal_names)) + " 个全部缺席）")

    # ---- 14.3b 不碰 [Obsolete] 的旧蓝图字段（3.26 已换 API） ----
    obsolete_hits = re.findall(r"\bbp\.(type|skill|tool|build)\b", bvc)
    if obsolete_hits:
        err("用了 [Obsolete] 的旧蓝图字段 " + ", ".join(sorted(set(obsolete_hits))) +
            "（3.26 改用 Operation / GetCategoryTag / ShouldConsume / RequiresSkill）")
    else:
        ok("未使用 [Obsolete] 的 blueprint.type / skill / tool / build")
    if "EBlueprintOperation" in bvc and "OperationPrefix(" in bvc:
        ok("操作类配方（修理/装填）有单独前缀 —— 不会把「修理 军裤」显示成产出军裤")
    else:
        err("没处理 EBlueprintOperation：修理/装填类配方的产物会被误读成「造一件新物品」")

    # ---- 14.4 枚举方式与缓存时效 ----
    if "Assets.find(" in bvc and "IBlueprintOwner" in bvc:
        ok("蓝图枚举走 public 的 Assets.find<IBlueprintOwner>（与原版同一做法）")
    else:
        err("蓝图枚举没走 public 的 Assets.find / IBlueprintOwner")
    if "RefreshBlueprintCache(" in body_of("public bool Open(", "Open"):
        ok("每次 Open 重扫资产表（连服/换图后不会拿着陈旧缓存）")
    else:
        err("Open 没刷新蓝图缓存 —— 换图或连服后会显示陈旧列表")

    # ---- 14.5 材料计数必须复刻引擎四判据 ----
    cs_body = body_of("private static int CountSupply(", "CountSupply")
    for (kw, why) in [("ShouldIncludeEmptyAmount", "空容器"),
                      ("ShouldIncludeMaxQuality", "满耐久物品"),
                      ("ShouldExcludeFullAmount", "满堆叠物品"),
                      ("ECraftingInputCountingMethod.TotalItems", "件数/总量两种计数法"),
                      ("ShouldCountEmptyAsOne", "空容器按 1 计")]:
        if kw in cs_body:
            ok("材料计数含引擎判据：" + why)
        else:
            err("材料计数缺判据 " + kw + "（" + why + "）→ 可用量会算错")

    # ---- 14.6 箱内/身上口径（本视图最容易搞错的一处） ----
    as_body = body_of("private BlueprintSupplyStat AddStat(", "AddStat")
    # ★ 口径的唯一正确定义：usable =「玩家现在点合成能凑到的量」，必须与执行侧一致。
    #   （旧断言写的是「usable 只算身上」—— 那在没有扣箱合成时才对；
    #    加了这个动作之后照旧显示，就会做出「明明能合却说不能、扣了看起来没变」，
    #    用户实测三条现象全是这一个根因。见 README 坑 #36。）
    if "else if (boxCountsAsUsable)" in as_body and "s.usable = s.carry + s.box;" in as_body:
        ok("能扣箱内时 usable = 身上 + 箱内（与 TryCraft 实际可扣到的一致）")
    else:
        err("usable 没跟着「能不能扣箱子」走 → 界面与执行漂移（显示够却没按钮 / 扣了看不出变化）")
    if re.search(r"else\s*\{[^}]*s\.usable = s\.carry;", as_body):
        ok("扣不到箱子时（联机客户端 / 关了开关 / 子集外配方）退回只算身上 = 原版口径")
    else:
        err("丢了「不可扣箱」时的原版口径分支 → 联机客户端会显示假的「可合成」")
    ev_body = body_of("private BlueprintEntry Evaluate(", "Evaluate")
    # ⚠ 判据只锁「boxCounts 这一条式子里必须同时含三个谓词」，不锁它长什么样
    #   （加参数/换行都会让字面匹配假 FAIL —— 本项目第十二次就栽在锚字面写法上）
    _boxstmt = re.search(r"bool boxCounts = ([^;]*);", ev_body)
    if _boxstmt is not None and "AllowCraftFromBox" in _boxstmt.group(1) \
            and "StorageCrafting.IsSupported(bp)" in _boxstmt.group(1) \
            and "StorageCrafting.BoxCounts(ctx)" in _boxstmt.group(1):
        ok("箱内算不算可用 = 意愿开关 AND 可行性 AND 这一页算不算箱（与按钮同源、一处定义）")
    else:
        err("boxCounts 缺谓词或分头写了 → 迟早漂移成「数量够但没按钮」")
    if ev_body.count("player, boxCounts)") >= 2:
        ok("AddStat 的每一路调用都传了 boxCounts（材料和目标物品不会用不同口径）")
    else:
        err("有 AddStat 调用没传 boxCounts → 目标物品的可用量会与材料不一致")
    # ⚠ 不锁写法只锁语义。craftable 必须复用"功能谓词"那一份（featureOn），
    #   而且**不得**含"这一页算不算箱子"（boxCounts 现在含它）——
    #   拿它拦按钮会把"只用身上材料合成"这条仍然成立的路一起禁掉，
    #   那 StorageUiVirtualPage.RelayWithSections 这个声明就成了骗人。
    _crf_stmt = re.search(r"bool craftable = ([^;]*);", ev_body)
    _feat_stmt = re.search(r"bool featureOn = ([^;]*);", ev_body)
    if _crf_stmt is not None and _feat_stmt is not None \
            and "featureOn" in _crf_stmt.group(1) \
            and "boxCounts" not in _crf_stmt.group(1) \
            and "AllowCraftFromBox" in _feat_stmt.group(1) \
            and "IsSupported" in _feat_stmt.group(1):
        ok("craftable 复用功能谓词 featureOn，且不含「这一页算不算箱」（两件事分开）")
    else:
        err("craftable 的判据变了：要么又自己拼了一遍，要么把「页面算不算箱」当成了按钮闸门"
            "（后者会误禁只用身上材料的合成）")
    if re.search(r"if \(!s\.isTool && s\.box > 0\)", ev_body):
        ok("明细无条件标出「箱内几·身上几」（不再只在不够时才标，避免误读成数量没变）")
    else:
        err("来源标注退回到「只在缺时才标」→ 玩家看不出材料是哪来的")
    if "Mathf.Min(s.need, s.carry + s.box)" in as_body:
        ok("工具类材料（ShouldConsume=false）算箱内并按需求封顶")
    else:
        err("工具类材料没按引擎口径统计（应含箱内并按 need 封顶）")
    if "equipIndex.Find(asset)" in as_body and "if (tool)" in as_body:
        ok("装备格只在工具类时计入（对应 IncludeEquipmentSlots = !ShouldConsume）")
    else:
        err("装备格计入条件不对（工具类才算）")

    # ---- 14.6b 候选集必须覆盖「箱内 + 背包 + 装备格」 ----
    ce_body = body_of("private void CollectEntries(", "CollectEntries")
    for (call, what) in [("boxIndex.CollectAssets(materialAssets)", "箱内"),
                         ("packIndex.CollectAssets(materialAssets)", "背包"),
                         ("equipIndex.CollectAssets(materialAssets)", "装备格")]:
        if call in ce_body:
            ok("候选集含" + what + "（材料全在这里的配方才会出现）")
        else:
            err("候选集漏了" + what + " → 该处的材料不参与计算（用户实测：背包里有金属碎片却看不到配方）")
    if "ContainsAnyOfItems(materialAssets)" in ce_body:
        ok("粗筛用合并后的资产集（不是只筛箱内）")
    else:
        err("粗筛仍只看箱子 → 身上的材料永远不会被列出来")
    if "RANK_FROM_BOX" in ev_body and "取出材料即可合成" in ev_body:
        ok("保留「取出材料即可合成」档（不可扣箱时仍然要说清该怎么合）")
    else:
        err("丢了「取出即可」档 → 联机/关开关时玩家不知道该把材料搬到身上")

    # ---- 14.7 四档状态 + 排序（截断只会砍缺材料的） ----
    for (rank_name, rank_val) in [("RANK_CRAFTABLE", "0"), ("RANK_FROM_BOX", "1"),
                                  ("RANK_GATE", "2"), ("RANK_MISSING", "3")]:
        if re.search(r"private const int " + rank_name + r" = " + rank_val + r";", bv):
            ok("状态档 " + rank_name + " = " + rank_val)
        else:
            err("状态档 " + rank_name + " 缺失或值变了（顺序依赖它）")
    cmp_body = body_of("private static int CompareEntry(", "CompareEntry")
    if ("a.rank.CompareTo(b.rank)" in cmp_body and "output.id" in cmp_body
            and cmp_body.index("a.rank") < cmp_body.index("output.id")):
        ok("显示顺序第一判据 = 状态档 → 超出上限时先砍「缺材料」")
    else:
        err("排序第一判据不是状态档 → MaxEntries 截断会砍错东西")

    # ---- 14.8 界面铁律（与另两个视图同源） ----
    if ("scroll.ScaleContentToWidth = true" in bv and "scroll.HandleScrollWheel = true" in bv):
        ok("滚动容器两个必需属性都设了（缺前者 = 整行宽 0、只剩图标）")
    else:
        err("滚动容器缺 ScaleContentToWidth / HandleScrollWheel")
    for const_name in ["TITLE_H", "DETAIL_H"]:
        m = re.search(r"private const float " + const_name + r" = ([0-9.]+)f?;", bv)
        if m is None:
            err("找不到常量 " + const_name + "（改名要同步自检）")
        elif float(m.group(1)) < 24:
            err(const_name + " = " + m.group(1) + " < 24 —— 小号标签行高不足会一个字都不渲染")
        else:
            ok(const_name + " = " + m.group(1) + " ≥ 24（实测最小可渲染行高）")
    tk_body = body_of("public void Tick(", "Tick")
    if "InvalidateAll()" in tk_body and "MarkDirty()" in tk_body \
            and "AttachAndPlacePanel" in tk_body:
        ok("挂载后作废全部行身份 + 重建（否则宽度为 0 的坏行被原样复用）")
    else:
        err("Tick 缺挂载后作废/重建")
    if "Rebuild(c)" in tk_body and "AttachAndPlacePanel" in tk_body \
            and tk_body.index("Rebuild(c)") < tk_body.index("AttachAndPlacePanel"):
        ok("Tick 顺序 = 先重建后挂载（反了底部滚不到）")
    else:
        err("Tick 顺序反了或取不到函数体")
    if "MAX_LIST_HEIGHT" in bv and "Mathf.Min(y, cap)" in bv and "AsSection ?" in bv:
        ok("列表限高 + 内部滚动，且区块模式用更小的上限（不把「附近物品」挤没）")
    else:
        err("蓝图列表没限高（或区块模式没单独限高）—— 会挤压原版区域")

    # ---- 14.8b 轮询与日志噪音（本视图有定时重算，不设闸会灌满 LogOutput） ----
    if "PollInterval > 0f" in tk_body and "Time.unscaledTime >= nextPollTime" in tk_body:
        ok("定时重算可关（PollInterval = 0 时只在数据变化时算）")
    else:
        err("轮询没有开关 —— 有人会把 PollSeconds 设 0 却发现仍在跑")
    rb_body = body_of("private void RebuildRows(", "RebuildRows")
    # ⚠ 判据要认**块**、不认"两行相邻"：26.10.3.33 在两者之间插了「可见行数」统计，
    #   原来那种 `if (...) {...140字内...} Log(` 的写法立刻假 FAIL（本项目第三次栽这条）。
    _gate19 = re.search(r"if \(signature != lastLoggedSignature\)\s*\{([\s\S]*?)\n            \}",
                        rb_body)
    if _gate19 is not None and "CustomUIPlugin.Log(" in _gate19.group(1):
        ok("刷新日志有签名闸：内容没变时一行都不记（定时重算不会刷日志）")
    else:
        err("蓝图刷新日志无条件 —— 开着箱子不动会每半秒灌一行 LogOutput")
    # RebuildRows 会被定时重算走到 —— **闸门之外**的任何一条日志都会变成噪音。
    #   ⚠ 别只数条数：闸门内多一条（比如"可见行数 ≠ 项数"的警告）完全没问题，
    #     该报的是"闸门外还有没有"。26.10.3.33 加了一条闸内 Warn，判据要认得它。
    _outside19 = rb_body
    if _gate19 is not None:
        _outside19 = rb_body.replace(_gate19.group(0), "")
    _rb_logs = len(re.findall(r"CustomUIPlugin\.(Log|Warn|Error)\(", _outside19))
    if _rb_logs == 0:
        ok("RebuildRows 没有闸门外的日志（定时重算不会产生噪音）")
    else:
        w("RebuildRows 的签名闸之外还有 " + str(_rb_logs) + " 条日志 —— 定时重算下会刷日志")
    if re.search(r'Config\.Bind\("Blueprint",\s*"PollSeconds"', src.get("CustomUI.cs", "")):
        ok("[Blueprint] PollSeconds 已接配置")
    else:
        err("缺 [Blueprint] PollSeconds 配置（轮询写死就没人能关）")

    # ---- 14.9 图标只在换配方时重建，改文字绝不碰图标 ----
    rr_body = body_of("private void RebuildRow(", "RebuildRow")
    if "icon.Refresh(" in rr_body and "scroll.AddChild(btn)" in rr_body:
        ok("产物图标只在整行重建时创建（换配方才走到这条路）")
    else:
        err("RebuildRow 缺图标刷新，或子元素建在挂载之前")
    ft_body = body_of("private void FillRowText(", "FillRowText")
    if "Refresh(" in ft_body or "AddChild(" in ft_body or "RemoveChild(" in ft_body:
        err("FillRowText 里出现 Refresh/AddChild/RemoveChild —— 改文字的路径不能碰图标")
    else:
        ok("FillRowText 只改文字与颜色（材料数字变化不会重载图标）")
    ck_body = body_of("private BlueprintRow ClaimByKey(", "ClaimByKey")
    if "r.Key == key" in ck_body and "!r.InUse" in ck_body:
        ok("行按配方身份认领（与另两视图同一铁律，避免图标全量重载）")
    else:
        err("行认领条件不对")

    # ---- 14.9b 搜索栏 + **材料名进匹配面**（★ T6 / 26.10.3.34） ----
    #   用户定的完成判据：搜「金属板」能搜到**用金属板做的**配方（产物名不含"金属板"也算命中）。
    # ★ 26.10.3.46：三份逐字相同的输入框代码合并成**公开组件** `StorageSearchBox` ——
    #   判据锁的事实没变（蓝图有搜索框、能打字），只是"自己建的控件"变成"组件画的控件"。
    if "searchBox.Build(panel)" in bv and "searchBox.Placeholder" in bv \
            and "searchBox.OnQueryChanged" in bv:
        ok("蓝图视图的搜索框交给**公开组件** StorageSearchBox（第三份复制品已消重）")
    else:
        err("蓝图视图没有搜索框（或又自己建了一份）—— T6 的完成判据无从谈起")
    # 搜索必须**在细算之前**生效：过滤的是"哪些配方参与 Evaluate"，
    # 而不是"算完再把行藏几行"（后者白算一大堆，且头部"共 M 项"与画面不符）。
    _ce19b = body_of("private void CollectEntries(", "CollectEntries")
    _i_search = _ce19b.find("BlueprintMatchesQuery(bp, searchBox.Query)")
    _i_eval = _ce19b.find("Evaluate(bp, player")
    if _i_search >= 0 and _i_eval >= 0 and _i_search < _i_eval:
        ok("搜索过滤发生在 Evaluate **之前**（被滤掉的配方既不占候选额度、也不细算）")
    else:
        err("搜索过滤不在 Evaluate 之前 —— 会白算一大堆，而且头部与画面可能对不上")
    # 匹配面：产物 + TargetItem + 每个材料，且走**共享层**那一份匹配（不许蓝图侧再写一套）
    _bm_body = body_of("private static bool BlueprintMatchesQuery", "BlueprintMatchesQuery")
    _bm_miss = [n for n in ("bp.outputs", "bp.TargetItem", "bp.supplies",
                            "StorageUiShared.MatchesQuery") if n not in _bm_body]
    if _bm_miss:
        err("蓝图匹配面漏了：" + "、".join(_bm_miss) +
            " —— 少材料名就搜不到「用金属板做的配方」；少共享匹配就会与物品搜索手感不一致")
    else:
        ok("蓝图匹配面 = 产物 + TargetItem + 全部材料名，且复用共享层 MatchesQuery（含拼音）")
    # 材料的 FindItemAsset 必须判空后再匹配（有 ID 但资产没加载完时不能炸）
    if "StorageUiShared.MatchesQuery(sup.FindItemAsset(), query)" in _bm_body \
            or "MatchesQuery(sup.FindItemAsset()" in _bm_body:
        ok("材料侧用 BlueprintSupply.FindItemAsset()（MatchesQuery 内部对 null 返回 false）")
    else:
        err("材料侧没走 FindItemAsset() —— 拿不到资产就匹配不了")
    # 头部要显式报「筛选中：显示 N / 共 M 项」
    # ⚠ 判据锁**事实**（头部把 N/M 与被谁筛的都说出来），不锁字面串：
    #   26.10.3.35 把原因从固定「筛选中」改成 `why`（搜索/分类/两者），字面串因此变了。
    if '：显示 " + entries.Count + " / 共 " + preSearchEntries' in bv and "string why" in bv:
        ok("被筛过时头部显式标「（<原因>：显示 N / 共 M 项）」（否则项数变少像配方没了）")
    else:
        err("筛选中没说明口径 —— 玩家会以为配方消失了")
    # 搜索词是"这一次会话"的状态：关界面要清掉
    _cl_body = body_of("public void Close(", "Close")
    if 'SearchQuery = ""' in _cl_body:
        ok("Close 清空搜索词（不带进下一个箱子）")
    else:
        err("Close 没清搜索词 —— 打开下一个箱子会只剩几项，玩家以为配方丢了")
    # 高度只有一个来源（★ 26.10.3.35：T7 把「搜索行 + 分类行」合并成 headExtra，
    #   判据跟着搬到新写法 —— 锁的仍然是"滚动区 Y 与面板高共用同一个和"这个事实）
    #   ★ 26.10.3.43：分类行的高度改由**公开组件**算（`filterBar.DesiredHeight`）——
    #     判据锁的事实没变，仍然是"滚动区 Y 与面板高共用同一个和"。
    if "float headExtra = SearchRowHeight() + filterBar.DesiredHeight;" in bv \
            and "scroll.PositionOffset_Y = HEAD_HEIGHT + headExtra;" in bv \
            and "panel.SizeOffset_Y = HEAD_HEIGHT + headExtra + listHeight + 6f;" in bv:
        ok("面板高度与滚动区起点共用 headExtra（搜索行 + 分类行的单一来源，不会漏改一处）")
    else:
        err("搜索/分类行高度有多处来源 —— 迟早漏改一处（面板高度与滚动区起点对不上）")

    # ---- 14.9c 分类过滤按钮（★ T7 / 26.10.3.35；★ 26.10.3.43 提成公开组件） ----
    #   用户定的语义：点一个类别只显示它；**再点一次取消**；数据层完全没动。
    #
    #   ★ 26.10.3.43：按钮条本体搬进了**公开可复用组件** `StorageFilterBar`（第三方也能用，
    #     README §4.24）⟹ 本节只管"**蓝图在用组件、没自建第二份**"；
    #     组件自身的行为（取消语义 / 相对宽度 / 现读下标 / 不占位）在 **14.9d** 单独钉，
    #     那才是这些事实现在**住的地方**（判据跟着实现走，别留在原地当化石）。
    _fb = src.get("CustomUI.FilterBar.cs", "")
    _fbc = strip_comments(_fb) if _fb else ""
    if not _fb:
        err("找不到 CustomUI.FilterBar.cs —— 分类过滤按钮条组件没了")
    if "private readonly StorageFilterBar filterBar" in bv and "typeHist" in bv:
        ok("蓝图视图用公开组件 StorageFilterBar 做分类过滤（数据层没动，纯显示过滤）")
    else:
        err("蓝图视图没有分类过滤按钮，或没用公开组件（又自建了一份）—— T7 / 框架化未达成")
    _fcalls = ("filterBar.Build(panel)", "filterBar.SetOptions(catType, catLabel, catCount, catUsed)")
    if all(c in bv for c in _fcalls):
        ok("蓝图把控件与数据都交给组件：Build(panel) + SetOptions(catType/catLabel/catCount)")
    else:
        err("蓝图没走组件的 Build/SetOptions —— 它还在自己驱动按钮")
    # 反向断言：那套自建实现不许回来（两份实现必然漂：改一处、另一处留旧观感）
    _stale = [n for n in ("filterButtons", "UpdateFilterButtons", "FilterRowHeight",
                          "OnFilterClicked") if n in bv]
    if not _stale:
        ok("蓝图里旧的私有按钮池实现已清干净（filterButtons / UpdateFilterXxx / FilterRowHeight）")
    else:
        err("蓝图还留着旧的私有实现：" + "、".join(_stale) + " —— 与组件会各改各的")
    # 组件报来的选中变化必须落到**数据层那两个字段**上（不另立第二份"谁被选中"的真源）
    _ofc = body_of("private void OnFilterChanged(", "OnFilterChanged")
    if "filterOn = hasSelection" in _ofc and "filterType = (EItemType)key" in _ofc:
        ok("组件回调把选中状态翻成数据层的 filterOn / filterType（不另立第二份真源）")
    else:
        err("OnFilterChanged 没把选中状态写进数据层 —— 会出现两份「谁被选中」")
    # ⚠ 原判据（"直方图必须在类型过滤与搜索之前"）在 26.10.3.36 被**上一条**取代 ——
    #   直方图已搬出候选循环单独成趟，"在循环里的位置"这件事不再成立。
    #   这里只留下"分类过滤本身仍在 Evaluate 之前"这一半事实。
    _i_tfilter = _ce19b.find("if (filterOn && tp != (int)filterType) continue;")
    if _i_tfilter >= 0 and _i_eval >= 0 and _i_tfilter < _i_eval:
        ok("分类过滤在 Evaluate 之前（被滤掉的配方不占候选额度、不细算）")
    else:
        err("分类过滤放在细算之后 —— 白算一大堆")
    # 直方图必须在**独立一趟**里统计 —— 长在候选循环里会被「MAX_CANDIDATES 的 break」与搜索词
    # 影响（选中某类 ⟹ 循环跑得更远 ⟹ 统计更多 ⟹ 前 6 名重排 ⟹ 按钮内容变了）。
    # 用户实测症状：「点了 Gun 之后那个按钮变成了 Skirt」。
    if "typeHist[tp]++" not in _ce19b and "TallyTypes(all)" in _ce19b:
        ok("类型直方图走**独立一趟**（不受候选上限的 break / 搜索词影响 ⟹ 按钮栏稳定）")
    else:
        err("直方图还长在候选循环里 —— 选中/打字会改变统计范围，按钮内容会跟着变（用户报过）")
    _tt = body_of("private void TallyTypes(", "TallyTypes")
    if "ContainsAnyOfItems" in _tt and "TypeOfBlueprint" in _tt \
            and "MAX_CANDIDATES" not in _tt and "BlueprintMatchesQuery" not in _tt:
        ok("TallyTypes 的条件只有「粗筛 + 插件过滤」（与分类/搜索/候选上限全都无关）")
    else:
        err("TallyTypes 混进了分类/搜索/上限判据 —— 按钮栏又会随筛选状态变")
    # 选中那一类必须留在按钮栏里（计数掉出前 N 时补位），否则玩家看不见自己在筛什么
    _btt = body_of("private void BuildTopTypes(", "BuildTopTypes")
    if "if (!shown)" in _btt and "catType[slot] = sel;" in _btt:
        ok("选中那一类计数掉出前 N 时**补位**（不会出现「正筛着它、按钮栏里却没有它」）")
    else:
        err("选中项可能从前 N 掉出去 —— 玩家看不见自己正在筛什么")
    # 本地化：必须用 FormatOrNull（走 TryReadString，data 与 fallback 都查）
    #   ⚠ `Local.has()` 只查 data ⟹ 对 Item_Type_*（在 Menu/Workshop 里）恒返回 false，
    #     用户实测报的第一个 bug「显示全是英文」就是它。
    _tl = body_of("private static string TypeLabel(", "TypeLabel")
    if "FormatOrNull(" in _tl and "Item_Type_" in _tl:
        ok("类型标签用 FormatOrNull 读原版 Item_Type_*（data 与 fallback 都查）")
    else:
        err("类型标签没走 FormatOrNull —— 键在 fallback 里时读不到，会整片退化成英文")
    if ".has(" in _tl:
        err("TypeLabel 里用了 Local.has() —— 它**只查 data 不查 fallback**，"
            "对 Item_Type_* 恒 false（这正是用户报的「显示全是英文」的根因）")
    else:
        ok("TypeLabel 没有再用 Local.has()（那个 API 只查 data，会漏 fallback）")
    if "ExtraTypeLabel" in _tl and "Pants" in bv and "储物" in bv:
        ok("原版没覆盖的类型有补充表（Pants/Storage 这些实测会出现在配方列表里）")
    else:
        err("原版没覆盖的类型会显示英文（Pants/Storage 实测就出现）—— 缺补充表")
    # ⚠⚠ 更关键的一条：键**不在 `Provider.localization` 里**。
    #   原版 `MenuWorkshopSubmitUI` 是屏幕级读的（`/Menu/Workshop/MenuWorkshopSubmit.dat`），
    #   换成 FormatOrNull 但读错 Local 照样全英文 —— 用户报的「显示全是英文」的真正根因。
    _tl_all = body_of("private static string TypeLabel(", "TypeLabel")
    if "TypeLocalization()" in _tl_all and "Provider.localization" not in _tl_all:
        ok("类型标签读的是**它自己那份** MenuWorkshopSubmit 本地化（不是 Provider.localization）")
    else:
        err("类型标签还在 Provider.localization 上找 Item_Type_* —— 那里面根本没有这批键，"
            "换 FormatOrNull 也一样全英文")
    if 'Localization.read("/Menu/Workshop/MenuWorkshopSubmit.dat")' in bv:
        ok("本地化文件自己读（与原版 MenuWorkshopSubmitUI 同一个文件、同一套语言回退）")
    else:
        err("TypeLocalization 没读 /Menu/Workshop/MenuWorkshopSubmit.dat —— 键拿不到")
    # ★★ 端到端：**复刻 TypeLabel 的三层查法，逐个枚举值过一遍**。
    #   只查"代码里有没有 FormatOrNull"证明不了"50 个类型都显示中文" —— 证据必须与结论同型。
    #   （判据纪律：找不到目标必须 FAIL，不能 WARN 跳过）
    _enum_p = os.path.join(SDK, "Bundles", "EItemType.cs")
    _dat_p = os.path.join(GAME, "Localization", "Schinese", "Menu", "Workshop",
                          "MenuWorkshopSubmit.dat")
    if not os.path.isfile(_enum_p):
        err("找不到 EItemType.cs（" + _enum_p + "）—— 无法验证类型标签")
    elif not os.path.isfile(_dat_p):
        err("找不到中文本地化（" + _dat_p + "）—— 无法验证类型标签")
    else:
        _names = [m.strip().rstrip(",;") for m in re.findall(
            r"^\s*([A-Z][A-Z_0-9]*)\s*[,$]",
            io.open(_enum_p, encoding="utf-8", errors="replace").read(), re.M)]
        if len(_names) < 20:
            err("EItemType 只解析出 " + str(len(_names)) + " 个值 —— 正则失效，判据别信")
        _vanilla = set()
        for _ln in io.open(_dat_p, encoding="utf-8", errors="replace"):
            _m = re.match(r"^(Item_Type_[A-Za-z_]+)\s+(\S+)", _ln.strip())
            if _m:
                _vanilla.add(_m.group(1))
        _eb = body_of("private static string ExtraTypeLabel(", "ExtraTypeLabel")
        if _eb is None:
            err("找不到 ExtraTypeLabel 函数体 —— 无法验证补充表")
            _extra = set()
        else:
            _extra = set(k for k, _v in re.findall(
                r'case "([A-Za-z_]+)": return "([^"]*)";', _eb))
        _fallback = []
        for _n in _names:
            _nice = _n[:1] + _n[1:].lower()
            if ("Item_Type_" + _nice) in _vanilla:
                continue
            if _nice in _extra:
                continue
            _fallback.append(_nice)
        if _fallback:
            err("有 " + str(len(_fallback)) + " 个类型会显示英文（原版没键、补充表也没补）："
                + "、".join(_fallback[:8]))
        else:
            ok("全部 " + str(len(_names)) + " 个 EItemType **都能显示中文**"
               "（原版键 " + str(len(_vanilla)) + " 个 + 补充表补齐其余，逐个枚举值验证过）")
    # 只有一种类型时整块不占位 —— ★ 26.10.3.43：规则搬进组件（蓝图把阈值交给它）
    if "filterBar.MinOptionsToShow = 2;" in bv \
            and "if (used < _minOptionsToShow) return 0f;" in _fbc:
        ok("候选里只有一种类型时分类按钮**整块不占位**（蓝图设阈值，组件据此给 0 高）")
    else:
        err("分类按钮无条件占位 —— 单类型箱子里白吃一行高度")
    # 会话状态：关界面要清筛选 —— ⚠ **两边都清**（数据层字段 + 组件选中态）
    if "filterOn = false;" in _cl_body and "catUsed = 0;" in _cl_body \
            and "filterBar.ClearSelection();" in _cl_body:
        ok("Close 清空分类筛选（数据层字段与组件选中态**两边都清**，不跨箱子）")
    else:
        err("Close 没清分类筛选、或只清了一边 —— 会出现「其实没在筛、按钮却还亮着」")

    # ---- 14.9d 过滤条组件自身（★ 26.10.3.43 公开面 `StorageFilterBar`）----
    #   这些事实**原来住在蓝图视图里**，随实现一起搬进了组件 —— 判据跟着搬，
    #   别留在原地当化石（否则"实现搬走了、判据还在查老地方"，两条都是假的）。
    if not _fb:
        err("14.9d 无法执行：过滤条组件源码缺失")
    else:
        # ⚠ 本节的 `body_of` 是**写死读蓝图**的局部函数 ⟹ 组件侧必须用模块级 `fn_body`。
        #   （同一条纪律：判据找不到目标要 FAIL，不能静默跳过 —— fn_body 自己会 err。）
        _obc = fn_body(_fb, "private void OnButtonClicked(", "FilterBar.OnButtonClicked")
        if _obc is None:
            err("找不到 OnButtonClicked —— 过滤条的点击语义搬走了？")
        elif "hasSelection && selectedKey == k" in _obc and "hasSelection = false" in _obc:
            ok("再点同一个选项 = 取消筛选（用户定的语义，住在组件里）")
        else:
            err("组件没有「再点一次取消」—— 用户第一句需求就没满足")
        if _obc is not None and "int k = keys[idx];" in _obc and "idx >= used" in _obc:
            ok("组件回调**现读** keys[idx] 并先查越界（不闭包捕获「这一下是哪一类」）")
        else:
            err("组件回调闭包捕获了类别 —— 选项列表一变就会点 A 筛 B")
        # 计数必须由接入方给（入口参数），组件里**不许**自己统计
        if "int[] optionCounts, int optionUsed)" in _fbc:
            ok("组件的计数是**入口参数**（接入方算好给它）—— 统计不长在组件参与的那条链里")
        else:
            err("组件没有「计数由接入方传入」的入口 —— 说明它自己在那儿统计（坑 #63-② 的形状）")
        if "typeHist" not in _fbc and "TallyTypes" not in _fbc:
            ok("组件内部没有任何直方图/统计代码（它只画按钮、只管选中）")
        else:
            err("组件里出现了统计代码 —— 统计一旦长在筛选链里，范围会被自己的筛选改变")
        # 宽度必须用相对值（父容器挂载前宽度为 0）
        if "SizeScale_X = 1f / _columns;" in _fbc and "PositionScale_X = (float)col / _columns;" in _fbc:
            ok("过滤条用**相对宽度**（父容器挂载前宽度为 0，像素宽度会算错）")
        else:
            err("过滤条用了像素宽度 —— 挂载前算出来的位置/宽度是错的")
        if "warnedMissingSelection" in _fbc and "CustomUIPlugin.Warn(" in _fbc:
            ok("选中项不在选项里时**报一次 warning**（接入方漏了补位不静默 —— 静默会让玩家以为筛选坏了）")
        else:
            err("组件对「选中了却不在选项里」没有任何提示 —— 玩家会看见一个亮着却点不掉的按钮")
        if "IsVisible = false" in _fbc and "RemoveChild(element)" in _fbc:
            ok("组件的 Hide 只藏不拆、Discard 才真拆（与另两个公开组件同一套生命周期）")
        else:
            err("组件的生命周期与另两个公开组件不一致（Hide 应当是只藏不拆）")

    # ---- 14.9e 搜索框组件自身（★ 26.10.3.46 公开面 `StorageSearchBox`）----
    #   它是三个视图里三份**逐字相同**的输入框代码的合并 ⟹ 这一节钉「那一份」的行为；
    #   三个视图侧只钉「在用组件、没自建第二份」（见 14.9b 与上面那条反向锁）。
    _sbx = src.get("CustomUI.SearchBox.cs", "")
    if not _sbx:
        err("14.9e 无法执行：搜索框组件源码缺失")
    else:
        _sbxc = strip_comments(_sbx)
        # ① 宽度必须是**相对值**（挂载前父容器宽是 0，像素宽会算错 —— 与 FilterBar 同一条铁律）
        if "SizeScale_X = 1f" in _sbxc and "SizeOffset_X = -2f * INSET_X" in _sbxc:
            ok("搜索框宽度用相对值（挂载前父容器宽度是 0，写像素会算错）")
        else:
            err("搜索框用了像素宽度 —— 挂载前算出来的位置/宽度是错的")
        # ② 纵向位置由调用方给（TopOffset），别在组件里写死
        if "PositionOffset_Y = _topOffset" in _sbxc:
            ok("纵向位置由 TopOffset 给（别写死 —— 换个头部高度的调用方就会摆错）")
        else:
            err("纵向位置写死了 —— 换个头部高度的调用方就摆错")
        # ③ Query 的 setter 必须**当场**同步控件（这是能删掉三份「每帧比对」的关键）
        _qset = re.search(r"public string Query[\s\S]{0,1400}?\n        \}", _sbxc)
        if _qset is not None and "field.Text = _query" in _qset.group(0):
            ok("外部改 Query **当场**同步到输入框（三个视图那三趟「每帧比对」因此被删掉）")
        else:
            err("Query 改了不写控件 —— 又得让每个调用方自己每帧比对一遍")
        # ④ 打字只回传、不重建（三个视图同一条约定）
        _ofc = re.search(r"private void OnFieldChanged[\s\S]{0,900}?\n        \}", _sbxc)
        _ofc_txt = "" if _ofc is None else _ofc.group(0)
        if "OnQueryChanged" in _ofc_txt and "MarkDirty" not in _ofc_txt \
                and "RemoveChild" not in _ofc_txt:
            ok("打字只回传（不在这里重建 —— 重建由调用方下一帧统一做）")
        else:
            err("打字回调里做了重建/标脏 —— 三个视图的约定是「下一帧统一重建」")
        # ⑤ 回调抛异常不许把输入框卡住
        if "catch (Exception" in _ofc_txt:
            ok("接入方回调抛异常不会卡住输入框（吞掉并记一条日志）")
        else:
            err("回调没兜异常 —— 一个插件抛异常就会让输入框再也不响应")
        # ⑥ Show=false 就不建、也不占高度（否则调用方会白留一行空位）
        if "if (!_show) return;" in _sbxc and "_show ? _height : 0f" in _sbxc:
            ok("Show=false 时不建控件、也不占高度（DesiredHeight=0）")
        else:
            err("Show=false 的行为没写全 —— 要么建了空控件，要么白占一行高度")
        # ⑦ ★ 承诺必须与实现一致：注释写着「setter **不触发** OnQueryChanged」，
        #    而写 `field.Text` 会让控件回调再进来一次。必须有一面旗把"代码写入"挡掉。
        if "_writingFromCode = true" in _sbxc and "if (_writingFromCode) return;" in _sbxc:
            ok("代码写 Query 时用旗挡住控件回调（兑现「setter 不触发 OnQueryChanged」这句承诺）")
        else:
            err("Query 的 setter 会绕回触发 OnQueryChanged —— 注释里的承诺是假的（坑 #70/#71 那一族）")

    # ---- 14.9f 三个视图的搜索接线（★ 26.10.3.46 消重后）----
    #   ⚠ 本轮踩到的真 regression：玩家打字时**组件先把 Query 设成新词**，回调里再
    #     `SearchQuery = text` 会撞上 setter 的「值相同就 return」而早退 ⟹ 不标脏 ⟹ 打了字不筛。
    #     ⟹ 三处的打字回调必须**自己无条件标脏**，不能靠 setter 代劳。
    _typing_ok = True
    for (_tf, _tn) in (("CustomUI.GridPanel.cs", "网格面板"),
                       ("CustomUI.ListPanel.cs", "列表面板"),
                       ("CustomUI.BlueprintView.cs", "蓝图视图")):
        _tc = strip_comments(src.get(_tf, ""))
        _m = re.search(r"private void OnSearchTextChanged\(string \w+\)[\s\S]{0,700}?\n        \}",
                       _tc)
        _body = "" if _m is None else _m.group(0)
        if "MarkDirty()" not in _body:
            _typing_ok = False
            err(_tn + "的打字回调没有标脏 —— 组件已先把 Query 设成新词，靠 setter 会早退（打了字不筛）")
    if _typing_ok:
        ok("三个视图的打字回调都**自己**标脏（不依赖 setter —— 组件已先行赋值，靠它必早退）")

    # ---- 14.9g 表单组件自身（★ 26.10.3.47 公开面 `StorageUiForm`）----
    #   它解决的问题：第三方做配置界面时得自己踩三个坑（重复触发 / 滑块 0~1 归一化 /
    #   数值框不夹取值）。这三条必须在**组件内部**处理掉 —— 判据就锁这三条，
    #   外加"总高只有一个来源"与"Hide 只藏不拆、Build 恢复可见"。
    _frm = src.get("CustomUI.Form.cs", "")
    if not _frm or "public class StorageUiForm" not in _frm:
        err("14.9g 无法执行：表单组件源码缺失（CustomUI.Form.cs）")
    else:
        # ① 总高只有一个来源（两处各算一份迟早对不上，同族坑 #68）
        _mh = fn_body(_frm, "private float MeasureHeight(", "Form.MeasureHeight")
        _rl = fn_body(_frm, "private void Relayout(", "Form.Relayout")
        _dh = re.search(r"public float DesiredHeight\s*\{\s*get\s*\{([^}]*)\}", _frm)
        if _mh is not None and _rl is not None and _dh is not None \
                and "MeasureHeight()" in _dh.group(1) and "MeasureHeight()" in _rl:
            ok("表单总高只有一个来源（DesiredHeight 与 Relayout 都走 MeasureHeight）")
        else:
            err("表单总高有第二份算法 —— 改 Padding/Gap 后总高与实际排版会对不上")

        # ② 自动排 Y（调用方不写 PositionOffset_Y 的前提）
        if _rl is not None and "PositionOffset_Y = y" in _rl:
            ok("行由上而下自动排（调用方不写 PositionOffset_Y）")
        else:
            err("表单没有自动排 Y —— 那它相对手写坐标就没省任何事")

        # ③ 三条取值纪律：回调 = 值真的变了
        _tog = fn_body(_frm, "public ISleekToggle AddToggle(", "Form.AddToggle")
        if _tog is not None and "if (v == last) return;" in _tog and "last = v;" in _tog:
            ok("开关行只在布尔值**真的变了**时回调（重复触发不会把重建引进来）")
        else:
            err("开关行没筛重复触发 —— 面板重建后重新挂回调会立刻收到一次当前值")

        _sld = fn_body(_frm, "public ISleekSlider AddSlider(", "Form.AddSlider")
        if _sld is not None and "Mathf.RoundToInt" in _sld and "if (v == last) return;" in _sld:
            ok("滑块行把引擎的 0~1 浮点**量化成整数档**、且同档重复拖动不回调")
        else:
            err("滑块行没量化 / 没筛同档 —— 拖动时一帧报好几次，调用方会被拖进每帧重建")

        _num = fn_body(_frm, "public ISleekInt32Field AddNumber(", "Form.AddNumber")
        if _num is not None and "Mathf.Clamp(raw" in _num and "if (v == last) return;" in _num:
            ok("数值行把玩家输入**当场夹到 [min,max]**，且夹取后的值没变就不回调")
        else:
            err("数值行没夹取值 —— 玩家敲 9999 会原样交给调用方（引擎的 Value 是原样存）")
        if _num is not None and "f.Value = v;" in _num:
            ok("越界时写回纠正显示（`Value` 的 setter 不触发回调，所以写回不会绕回自己）")
        else:
            err("越界输入没有写回纠正 —— 玩家会看到输入框里留着一个非法值")

        # ④ 回调包 try：一个接入方抛异常不该把整块表单卡住
        _ft = fn_body(_frm, "private static void FireToggle(", "Form.FireToggle")
        _fv = fn_body(_frm, "private static void FireValue(", "Form.FireValue")
        if _ft is not None and "catch (Exception" in _ft \
                and _fv is not None and "catch (Exception" in _fv:
            ok("两路回调都包了 try（接入方抛异常不会把表单卡死）")
        else:
            err("回调没包 try —— 接入方一个异常就能让表单的行失效")

        # ⑤ Reset 真拆、Hide 只藏不拆、Build 恢复可见
        _rst = fn_body(_frm, "public void Reset(", "Form.Reset")
        if _rst is not None and "RemoveChild" in _rst and "rows.Clear()" in _rst:
            ok("Reset 把行**真拆**（RemoveChild = GameObject.Destroy）并清记录")
        else:
            err("Reset 没真拆行 —— 行会越堆越多（或留下悬空引用）")

        _hid = fn_body(_frm, "public void Hide(", "Form.Hide")
        if _hid is not None and "IsVisible = false" in _hid \
                and "RemoveChild" not in strip_comments(_hid):
            ok("Hide 只藏不拆（只改 IsVisible，不动行）")
        else:
            err("Hide 不是\"只藏不拆\" —— 临时收起会把行销毁掉")

        _bld = fn_body(_frm, "public void Build(", "Form.Build")
        if _bld is not None and "IsVisible = true" in _bld:
            ok("Build 同时是\"恢复显示\"入口（宿主只恢复区块根面板，不会管子元素）")
        else:
            err("Build 没恢复可见 —— Hide 之后就永远看不见了（宿主不管子元素）")

        # ⑥ 反向锁：组件**不存业务值**（真源永远在调用方）
        if re.search(r"public\s+(?:bool|int|float|string)\s+Value\b", _frm):
            err("表单组件自己存了业务值 —— 真源必须在调用方，否则两份真源必然漂")
        else:
            ok("表单组件不存业务值（它只把\"改成了多少\"交出去），真源在调用方")

        # ⑦ ★ 26.10.3.48：滑块必须是**横向**的 —— 引擎默认是竖直的
        #    （`GlazierSlider_uGUI.cs:14` `_orientation = ESleekOrientation.VERTICAL`，
        #    源码原话 "User code assumes vertical default"；全 Unturned 的横滑块
        #    都显式写了这一句，如 `SleekValue.cs:65`）。
        #    208 实测：不写这一句拿到的是一根**竖条**（用户原话「滑块应该是左右滑才对」）。
        if _sld is not None \
                and "Orientation = ESleekOrientation.HORIZONTAL" in strip_comments(_sld):
            ok("滑块显式设成横向（引擎默认竖直 —— 不设拿到的是一根竖条）")
        else:
            err("滑块没设 Orientation —— uGUI 默认竖直，玩家看到的是竖条（208 实测）")

        # ⑧ ★ 26.10.3.48：左右留白也要落地 —— `Padding` 名义上"四周"，以前却只写在纵向；
        #    行本身满宽 ⟹ 右侧的开关/滑块/数值框**零右边距**（用户实测：右侧太窄）。
        if _rl is not None and "PositionOffset_X = _padding" in _rl \
                and "-(_padding + _rightMargin)" in _rl:
            ok("行两端都有留白（左 `Padding` / 右 `Padding + RightMargin`）—— 右侧控件不再贴边")
        else:
            err("行只做了纵向留白 —— 右侧控件会贴着父容器右边缘（用户实测：右边距太窄）")

        # ⑨ 右边距必须**可配**（用户点名要的旋钮），且改了立刻重排
        #    ⚠ 别用裸 `public float RightMargin[\s\S]{0,600}` —— 那会被 `RightMarginX`
        #      这种"前缀相同"的成员名满足（阳性对照实测过：改名成 RightMarginX 依然全绿）。
        #      必须要求名字后面紧跟 `{`（即正好是这个属性本身）。
        _rm = re.search(r"public float RightMargin\s*\{[\s\S]{0,600}?Relayout\(\);", _frm)
        if _rm is not None:
            ok("右侧额外留白是公开旋钮 `RightMargin`，改了立刻重排（右边距可配）")
        else:
            err("右边距写死了 —— 接入方没法按自己的容器宽度调（用户点名要这个旋钮）")

    # ---- 14.9h 公开组件的「重新挂载」不许销毁本体（★ 26.10.3.48）----
    #   208 实测 FAIL 的真根因：`Build` 的重挂路径先 `RemoveChild` 再 `AddChild` ——
    #   而 uGUI 的 `RemoveChild` 末尾是 `InternalDestroy()`（`GlazierElementBase_uGUI.cs:253-283`，
    #   它自己还会先 `RemoveAllChildren()`）⟹ **本体连同里面的控件一起**被销毁/回收，
    #   紧接着的 `AddChild` 挂的是一具尸体 ⟹ `IsVisible` 停在 `Hide()` 留下的 false
    #   （自检报 `Build恢复可见=False`，而 `Hide只藏不拆=True` —— 两行恰是这条病的指纹）。
    #   `AddChild` 自己会把本体从旧父容器摘走（同文件 `:321-347`）⟹ **重挂路径里不许 RemoveChild**。
    #   ⚠ 判据必须**先剔注释**：这三处源码的注释里就在写"别用 RemoveChild"。
    for (_bf, _bn) in (("CustomUI.Form.cs", "表单"),
                       ("CustomUI.SearchBox.cs", "搜索框"),
                       ("CustomUI.FilterBar.cs", "过滤条"),
                       ("CustomUI.ItemIcon.cs", "图标格"),
                       ("CustomUI.Fill.cs", "填充层"),
                       ("CustomUI.Bar.cs", "进度条"),
                       ("CustomUI.Dropdown.cs", "下拉选择器")):
        _bb = fn_body(strip_comments(src.get(_bf, "")), "public void Build(", _bn + ".Build")
        if _bb is None:
            err("14.9h 读不出" + _bn + "的 Build —— 判据失效（别让它静默跳过）")
        elif "RemoveChild" in _bb:
            err(_bn + "的 Build 里 RemoveChild 之后再 AddChild —— RemoveChild = InternalDestroy"
                "（连子控件一起销毁）⟹ 重挂之后本体永远回不来（208 实测）")
        elif "AddChild" not in _bb:
            err(_bn + "的 Build 没挂父容器 —— 它建的控件根本不在界面上")
        else:
            ok(_bn + "的 Build 重挂不销毁本体（直接 AddChild，它会自己从旧父容器摘走）")

    # ---- 14.9i 图标格组件自身（★ 26.10.3.49 公开面 `StorageItemIcon`；26.10.3.50 补填充色一族）----
    #   它存在的理由是**替调用方挡住三件事**：异步贴图 / 渲染队列 / 文案口径。
    #   判据锁这三条 + 两条引擎事实（id 变先 Clear、角标高度够渲染）+ ⑧-⑫（真彩色填充：
    #   `FillColor` 非可空、填充层**必须显式挂白贴图**、左右留白都落地、按 alpha 判画不画、
    #   以及**文档必须写清** `BackgroundColor` 改不了色相）。
    #   ⚠ 26.10.3.51 起填充层的实现搬进了通用组件 `StorageUiFill`，⑨-⑪ 已跟着搬家。
    _ico = src.get("CustomUI.ItemIcon.cs", "")
    if not _ico or "public class StorageItemIcon" not in _ico:
        err("14.9i 无法执行：图标格组件源码缺失（CustomUI.ItemIcon.cs）")
    else:
        _icoc = strip_comments(_ico)

        # ① ★ 核心：图标请求必须"键没变就早退"—— 少了它，带 state 的物品每帧排一次离线渲染
        _ai = fn_body(_ico, "private void ApplyIcon(", "Icon.ApplyIcon")
        if _ai is not None and "if (k == iconKey) return;" in _ai:
            ok("图标只在键（id+耐久）真的变了时才请求一次贴图（否则每帧灌爆渲染队列）")
        else:
            err("图标请求没有'键没变就早退' —— 带 state 的物品会被每帧重新渲染（ItemTool 不进缓存）")

        # ② 反向锁：**键的计算里不许出现 state**（口径与内置三个视图的指纹一致，坑 #17）
        #    ⚠ 判据必须查**调用点 + 定义**两处：真实改动会落在调用点
        #      （`Mix3(item.id, item.amount, item.state.Length)` 这种），
        #      只看 `Mix3` 的函数体根本拦不住（第一版就是这么写错的，阳性对照当场抓出来）。
        _keycalls = re.findall(r"Mix3?\([^)]*\)", _icoc)
        _keydefs = re.findall(r"private static int Mix3?\([^)]*\)[\s\S]{0,200}?\n        \}", _icoc)
        if _keycalls and "state" not in " ".join(_keycalls) \
                and _keydefs and "state" not in " ".join(_keydefs):
            ok("图标键 / 角标键都不含 item.state（调用点与定义都查了 —— 与内置视图同一口径，坑 #17）")
        else:
            err("键的计算里混进了 state（或判据抓不到键调用点）—— state 每帧可能变 ⟹"
                " 每帧重建 ⟹ 异步贴图永远来不及显示")

        # ③ id 变了先 Clear（原版 SleekItem 同一处做法）：免得闪一帧旧图标
        #    ⚠ 判据必须**认块**：`ApplyIcon` 里还有另一处 `icon.Clear()`（空资产的早退分支），
        #      只查"这个函数里有没有 icon.Clear()"会被它满足 ⟹ 删掉 id 分支那一处也照样绿
        #      （第一版就是这么写的，阳性对照当场抓出来）。
        if _ai is not None and re.search(r"lastId != item\.id[\s\S]{0,400}?icon\.Clear\(\)", _ai):
            ok("物品 ID 变了先 Clear 一下贴图（原版 SleekItem 同一处做法，避免闪一帧旧图标）")
        else:
            err("ID 变了没先 Clear —— 换物品时会闪一帧上一个物品的图标")

        # ④ 文案口径必须**同源**（数量/耐久/Tooltip 都走 Shared 那一份，不许自己拼）
        if "StorageUiShared.BuildAmountText" in _icoc and "StorageUiShared.BuildTooltip" in _icoc:
            ok("数量/耐久/Tooltip 全部复用 StorageUiShared（与三个内置视图同一份口径）")
        else:
            err("图标格自己拼了数量/提示文案 —— 同一个箱子在两个界面里会显示不一样")
        #    ⚠ 反向锁要**两种写法都堵**：字面量 `"×"` 与转义 `"\u00d7"`（第一版只堵了前者，
        #      阳性对照用转义写法注入，当场漏过）。
        if u'"\u00d7"' not in _icoc and u'"\\u00d7"' not in _icoc and "'\\u00d7'" not in _icoc:
            ok("没有自己拼「×N」这类数量文本（反向锁，字面量与转义写法都堵上了）")
        else:
            err("图标格里自己拼了数量文本 —— 口径只能有一处（StorageUiShared）")

        # ⑤ 角标行高必须 ≥ 24：小号字体的标签矮于 24 时**一个字都不渲染**（坑 #21，实测过）
        _bh = re.search(r"private const float BADGE_H = ([0-9.]+)f;", _icoc)
        if _bh is not None and float(_bh.group(1)) >= 24.0:
            ok("角标行高 = %sf ≥ 24（小于 24 时小号字体一个字都不渲染 —— 坑 #21）" % _bh.group(1))
        else:
            err("角标行高没有写成 ≥ 24 的常量 —— 高度不够时角标会整行不显示，且不报任何错")

        # ⑥ 生命周期：Hide 只藏不拆、Discard 才真拆
        _ih = fn_body(_ico, "public void Hide(", "Icon.Hide")
        if _ih is not None and "IsVisible = false" in _ih and "RemoveChild" not in _ih:
            ok("图标格的 Hide 只藏不拆（不 RemoveChild）")
        else:
            err("图标格的 Hide 不是只藏不拆")

        # ⑦ 位置由调用方负责：组件**不许**在 Build 里写死位置（否则调用方摆不动它）
        _ib = fn_body(_ico, "public void Build(", "Icon.Build")
        if _ib is not None and "PositionScale_X = 0f" in _ib and "PositionOffset_Y = 0f" in _ib:
            ok("Build 不替调用方定位置（建完由调用方摆 —— 一格一格拼行时才有意义）")
        else:
            err("Build 里给控件写死了位置 —— 调用方就没法把它摆到自己的行上")

        # ⑧ ★★ `FillColor` 必须存在，且**必须是非可空**的 `SleekColor`。
        #    反向锁：源码里不许出现 `SleekColor?` —— 见坑 #82：`SleekColor` 有到 `StyleColor`
        #    的隐式转换，可空类型会让编译器去找"提升转换" ⟹ 调用方被要求引用
        #    `UnityEngine.UIElementsModule` ⟹ **外部程序集编译不过**；而本插件引用了那个程序集、
        #    自己照样编译过 ⟹ **单程序集自检发现不了**（这正是本接入自检存在的理由）。
        #   ⚠ 名字必须**整词**匹配 —— 第一版写成 `"public SleekColor FillColor" in _icoc`，
        #     被 `FillColorBak` 这种改名满足 ⟹ 阳性对照当场抓出来（又是老坑 #77 的前缀假绿）。
        if re.search(r"public\s+SleekColor\s+FillColor\b", _icoc):
            ok("公开旋钮 FillColor 存在（真彩色填充 —— 盒子贴图填充是纯黑，BackgroundColor 改不了色相）")
        else:
            err("没有 FillColor —— 第三方没法给格子铺颜色（BackgroundColor 只有 alpha 有效）")
        if "SleekColor?" in _icoc or "SleekColor ?" in _icoc:
            err("组件里出现了可空 `SleekColor?` —— 那会让**外部程序集编译不过**"
                "（得引用 UnityEngine.UIElementsModule，坑 #82）；用 alpha 0 当哨兵")
        else:
            ok("没有可空 `SleekColor?`（外部程序集才不会因缺 UIElements 引用而编译失败）")

        # ⑨ ★★ 填充层的实现住在通用组件 `StorageUiFill` 里（`.51` 成立、`.52` 换实现）——
        #    判据跟着实现走（铁律：实现搬家时判据得一起搬，否则整组假 FAIL）。
        #    ★★ `.52` 这两个 bug 都是"**只有真看图 / 真看资源才看得见**"那一类：
        #      ① 「填充换色不过」：`ISleekImage` 那一层必须自己挂白贴图 ——
        #         uGUI `GlazierImage_uGUI.ConstructNew` 把 `rawImageComponent.texture` 填成内置白像素
        #         （为绕开一个 uGUI bug），但 C# 侧 `desiredTexture` 仍是 null；
        #         而 `SynchronizeColors`（`:227-247`）判的是 `desiredTexture` ⟹ 为 null 就
        #         `enabled = false`（颜色无效、`IsVisible` 也没用、**还不报错**）。
        #      ② 「该圆角的地方是直角、外侧还留一圈原版颜色」：`ISleekImage` 是 `RawImage`
        #         （`GlazierImage_uGUI.cs:199`）——**整张贴图拉伸、没有 9 宫格** ⟹ 只能是直角方块；
        #         旧版只能靠"内缩 4px"躲开盒子圆角，而内缩又露出原版底色。
        #         ⟹ 改用 `ISleekSprite`（`GlazierSprite_uGUI.cs:111` 是 `Image`）+ `Sliced` 9 宫格。
        #    ⚠ 所以判据要**同时**钉：走 Sprite + Sliced / 贴图是运行时生成的白色圆角 9 宫格 /
        #      **不许**退回 `ISleekImage`（那种回退看起来"也能变色"，只是没有圆角）。
        _fillc = src.get("CustomUI.Fill.cs", "")
        _fillcode = strip_comments(_fillc)     # 头注释里大段引用这两个词，必须剔掉注释再查
        _shapes = src.get("CustomUI.Shapes.cs", "")
        _shapesc = strip_comments(_shapes)
        _fb = fn_body(_fillc, "public void Build(", "Fill.Build")
        _fs = fn_body(_fillc, "private void ApplyShape(", "Fill.ApplyShape")
        if _fb is None:
            err("14.9i 读不出 StorageUiFill.Build —— 判据失效（别让它静默跳过）")
        elif "CreateSprite()" in _fb and _fs is not None \
                and "ESleekSpriteType.Sliced" in _fs and "ESleekSpriteType.Regular" in _fs:
            ok("填充层走 ISleekSprite（`Radius` 决定 9 宫格 / 直角两路 —— 见 Fill.ApplyShape）")
        else:
            err("填充层不是 ISleekSprite（或没按 `Radius` 分两路）—— ISleekImage 是 RawImage"
                "（整张贴图拉伸、无 9 宫格）⟹ 只能画直角方块（用户 2026-10-10 实测「圆角变直角 + 没盖全」）")
        # ★ 26.10.3.64：贴图生成**搬到了 `StorageUiShapes`**（原先 Bar / Fill 各一份、逐像素等价）
        #   ⟹ 判据跟着搬；并**升一级**：不只判"Fill 用了它"，还判"Bar 也用它、且两处都不再自己生成"。
        if _fs is not None and "StorageUiShapes.Rounded(" in _fs:
            ok("填充层贴图出自 `StorageUiShapes`（与进度条**同一份形状**）")
        else:
            err("填充层没走 `StorageUiShapes` —— 形状又有第二份了（这正是「填充层不能改圆角」的成因）")
        _barc = strip_comments(src.get("CustomUI.Bar.cs", ""))
        if re.search(r"StorageUiShapes\.Rounded\(", _barc):
            ok("进度条也走 `StorageUiShapes`（两处共用一份形状，改一处两处一起变）")
        else:
            err("进度条没走 `StorageUiShapes` —— 形状口径又分家")
        _selfgen = []
        for (_fn, _tag) in (("CustomUI.Fill.cs", "填充层"), ("CustomUI.Bar.cs", "进度条")):
            if "new Texture2D(" in strip_comments(src.get(_fn, "")):
                _selfgen.append(_tag)
        if _selfgen:
            err("这些地方还在自己生成贴图：" + ", ".join(_selfgen) + " —— 形状必须只有一处口径")
        else:
            ok("填充层与进度条都不自己生成贴图（贴图只在 `StorageUiShapes` 里建）")
        if "CreateImage(" in _fillcode or "Texture2D.whiteTexture" in _fillcode:
            err("填充层又退回了 ISleekImage / Texture2D.whiteTexture 那套 —— 那是直角方块那条路"
                "（能变色但没有圆角；用户实测「非常丑」）")
        else:
            ok("填充层没退回 ISleekImage 那条路（RawImage 画不出圆角）")

        # ⑨c' **默认圆角半径必须还是 9** —— 0 = 直角，会把三套视图的格子观感整体改掉。
        _dr = re.search(r"DEFAULT_RADIUS = ([0-9.]+)f", _fillcode)
        if _dr is None:
            err("14.9i 读不出 `DEFAULT_RADIUS` —— 默认圆角不明（锚点要跟着改）")
        elif float(_dr.group(1)) == 9.0:
            ok("默认圆角半径仍是 **9**（与主题 Box.png 同形 ⟹ 现有格子观感一字不变）")
        else:
            err("默认圆角半径变成了 " + _dr.group(1) + " —— 现有三套视图的格子观感会被整体改掉")

        # ⑨e ★ 26.10.3.64：**圆角与边框都必须是旋钮**，且边框的可见性有**两个**条件。
        _fap = fn_body(_fillc, "private void Apply(", "Fill.Apply")
        _sub = []
        for _need in ("public float Radius", "public SleekColor BorderColor", "public float BorderThickness"):
            if _need not in _fillcode:
                _sub.append(_need)
        if _sub:
            err("填充层缺旋钮：" + ", ".join(_sub) + " —— 圆角 / 边框必须是可配的")
        else:
            ok("填充层有 `Radius` / `BorderColor` / `BorderThickness` 三个旋钮（圆角与边框都可配）")
        if _fap is not None and re.search(r"_borderThickness\s*>\s*0f", _fap):
            ok("边框可见性是**两个条件**（宽度 > 0 **且** 有不透明色）—— 不是只看颜色")
        else:
            err("边框只看颜色不看宽度 —— 宽度 0 时白亮一层（被填充压住，纯浪费绘制）")
        # 位置锁：`Build` 里**边框先加、填充后加**（后加=盖在上面 ⟹ 反了就整块变成边框色）
        _ib = _fb.find("AddChild(border)") if _fb else -1
        _is = _fb.find("AddChild(sprite)") if _fb else -1
        if _ib >= 0 and _is >= 0 and _ib < _is:
            ok("`Build` 先挂边框、后挂填充（后加 = 盖在上面 ⟹ 反了整块会变成边框色）")
        else:
            err("`Build` 的挂载顺序不对（AddChild(border)@%d / AddChild(sprite)@%d）—— 反了整块变边框色"
                % (_ib, _is))
        # ★★ 边框的**去重键必须含可见性**（铁律 #36：键承载的语义不能比状态少）。
        #   键只含颜色时，「先设边框色（此时宽度还是 0 ⟹ 判成隐藏、键已记下）→ 再设宽度 3」
        #   这一串**键没变** ⟹ `Apply()` 早退，可见性**再也不会被重新评估** ⟹ 边框永远不亮。
        #   本轮实测正是它：自检 `宽3显边=False`、玩家看到"有边框那块小一圈"。
        if _fap is not None and re.search(r"int bk = on \?", _fap):
            ok("边框去重键**含可见性**（`int bk = on ? … : 0`）—— 先设色后设宽度不会早退")
        else:
            err("边框去重键只含颜色 —— 先设色再设宽度会**早退**，边框永远不亮（本轮实测过）")

        # `ThemedBorder` 必须是**真彩色**（`new SleekColor(Color)`）—— ⚠ 只锁"真彩色"这个**需求**，
        #   不锁它用哪个 API：第一版走 `BackgroundIfLight`，那在**浅色主题**下返回的是**背景色**
        #   ⟹ 描边与底同色、等于隐形（也是本轮实测出来的）。
        _tb = fn_body(_fillc, "public static SleekColor ThemedBorder(", "Fill.ThemedBorder")
        if _tb is not None and "new SleekColor(" in _tb and "ESleekTint." not in _tb:
            ok("`ThemedBorder` 是**真彩色**、不是主题 tint（自绘面吃真彩色，坑 #93）")
        else:
            err("`ThemedBorder` 不是真彩色（或用了 `ESleekTint.*`）—— 自绘面会解析成白 / 背景色")

        # ⑨b 形状公式本身必须**仍是"纯白 + 9 宫格 + 同 ppu + 与原版同形"** —— 四条缺一条就变味：
        #     缺 border ⟹ `Sliced` 退化成整张拉伸（圆角变椭圆）；不是纯白 ⟹ `TintColor` 乘出来不是目标色；
        #     ppu 与主题 `Box.png`（100）不一致 ⟹ 圆角看起来比原生盒子大/小；
        #     留白/半径不对 ⟹ 每边比原版框大一圈（用户 2026-10-10 实测）。
        _rb = fn_body(_shapes, "internal static Sprite Rounded(", "StorageUiShapes.Rounded")
        if _rb is None:
            err("14.9i 读不出 `StorageUiShapes.Rounded` —— 判据失效（别让它静默跳过）")
        else:
            if "SpriteMeshType.FullRect" in _rb and "new Vector4(" in _rb:
                ok("圆角贴图是 9 宫格（FullRect + border）—— 缺了它 Sliced 会把圆角拉成椭圆")
            else:
                err("圆角贴图不是 9 宫格（缺 FullRect 或 border）—— 拉伸时圆角会被拉变形")
            if "new Color32(255, 255, 255," in _rb:
                ok("圆角贴图是**纯白**的 —— TintColor 是乘法，不是白就调不出目标色")
            else:
                err("圆角贴图不是纯白 —— `TintColor` 乘上去得不到目标色相")
            if "100f" in _rb:
                ok("圆角贴图 ppu = 100（与主题 Box.png 同值 ⟹ 圆角缩放与原生盒子一致）")
            else:
                err("圆角贴图 ppu 与主题 Box.png 不一致 —— 圆角看起来会比原生盒子大或小")

            # ⑨d ★★ 形状必须**与原版盒子同形** —— 这是"填充块比原版框大一圈"的根因。
            #   公式改成"按半径现算"后（`S = 2R + 2` / `M = 1` / `lo = M + R`），
            #   **数值结论**要在 Python 侧按 R = 9 推一遍（别只判有没有那两行）。
            if "const float M = 1f;" in _rb and re.search(r"int S = 2 \* R \+ 2;", _rb) \
                    and "float lo = M + R;" in _rb and "float hi = S - M - R;" in _rb:
                _S, _M, _R = 2 * 9 + 2, 1.0, 9.0
                _vis = int(_S - 2 * _M)
                if _vis == 18 and abs(_R - _vis / 2.0) < 0.01:
                    ok("半径 9 时可见形状 18×18、四周留白 1px、半径 = 形状半高（公式推算，与 Box.png 同形）")
                else:
                    err("按公式推 R = 9 得到可见 " + str(_vis) + "×" + str(_vis) +
                        " / 半径 " + ("%g" % _R) + " —— 与主题 Box.png 的 18×18（半高 9）不符")
            else:
                err("14.9i 读不出圆角贴图的形状公式（`S = 2R + 2` / `M = 1` / `lo = M + R`）"
                    "—— 同形判据失效（别让它静默跳过）")

        # ⑨c 填充层必须**铺满**（内缩 0）—— 内缩会在颜色外侧留一圈原版底色（用户实测「没盖全」）。
        #     四个落点（三个内置视图 + 图标格）都查；常量改名 / 挪走要同步搬这条锚点。
        for (_f3, _n3) in (("CustomUI.GridPanel.cs", "网格"), ("CustomUI.ListPanel.cs", "列表"),
                           ("CustomUI.BlueprintView.cs", "蓝图"), ("CustomUI.ItemIcon.cs", "图标格")):
            _m3 = re.search(r"FILL_INSET\s*=\s*([0-9.]+)f", src.get(_f3, ""))
            if _m3 is None:
                err("14.9i 在" + _n3 + "里找不到 FILL_INSET —— 填充层的内缩值不明（锚点要跟着改）")
            elif float(_m3.group(1)) == 0.0:
                ok(_n3 + "的填充层铺满整格（内缩 0）")
            else:
                err(_n3 + "的填充层内缩 = " + _m3.group(1) + "f —— 颜色外侧会露出一圈原版底色"
                    "（用户 2026-10-10 实测「没盖全」）")

        # ⑩ 几何：铺满父容器再内缩 —— `Inset` 必须**四周都落地**
        #    （本项目栽过"注释说四周、实现只落纵向"的坑：表现为右侧贴边，用户实测才发现，坑 #39）
        _fl = fn_body(_fillc, "private void Layout(", "Fill.Layout")
        if _fl is not None and "PositionOffset_X = _inset" in _fl \
                and "SizeOffset_X = -2f * _inset" in _fl:
            ok("填充层的 Inset 左右都落地（四周留白，不是只落纵向）")
        else:
            err("填充层的左右留白没落地 —— 注释说「四周」就必须四周都实现（坑 #39）")

        # ⑪ 「画不画」必须由**透明度**判定，别另外长 bool 旋钮、也别用可空类型
        _fa = fn_body(_fillc, "private void Apply(", "Fill.Apply")
        if _fa is not None and "c.a > 0" in _fa:
            ok("填充层的'画不画'由透明度判定（alpha 0 = 不铺）—— 调用方不需要额外的开关")
        else:
            err("填充层'画不画'不是按透明度判的 —— 要么默认会铺、要么多长了一个旋钮")

        # ⑪b 反向锁：图标格**不许**再自己建填充层（同一份实现，铁律 #5）
        _ico_code = strip_comments(_ico)
        if "CreateImage(" in _ico_code:
            err("图标格里又自己建了填充层 —— 实现只能有一份（通用组件 StorageUiFill）")
        elif "fill.Build(" in _ico_code:
            ok("图标格的填充层走通用组件（一份实现，视图与组件不会各画一套）")
        else:
            err("图标格既没自己建、也没用通用组件 —— 填充色无处落地")

        # ⑫ ★ **文档纪律**（这条**故意锚在注释上**，所以用**未剔注释**的源码 `_ico`）：
        #    `BackgroundColor` 的说明必须写明"盒贴图填充是纯黑 / 只有 alpha 有效"，
        #    否则下一个人又会拿它当"能改框底色"（坑 #81 就是这么来的）。
        #    ⚠ 与"锚点别锚在注释上"不冲突：那条防的是"判据静默走 else"，这里是**有意**判文档。
        if "纯黑" in _ico and "只有 alpha 有效" in _ico:
            ok("BackgroundColor 的说明写清了引擎事实（盒贴图填充纯黑 ⟹ 只有 alpha 有效）")
        else:
            err("BackgroundColor 的说明没写清它改不了色相 —— 下一个人会重犯坑 #81")

        # ⑫b 文档纪律（同样**故意锚在注释上**）：`Fill.cs` 必须写明**两条"不写就会被当多余代码删掉"的
        #    引擎依据** ——
        #      ① `ISleekImage` 是 `RawImage`、**没有 9 宫格** ⟹ 画不出圆角（所以改用 `ISleekSprite`）；
        #      ② `desiredTexture` 那条**静默失效**（光"不设 Texture" = 这一层整个被禁用、还不报错）。
        #    不写的话，下一个人会把这些当成"多加的一层"删掉，症状是**颜色与圆角又都没了、还不报错**。
        #    判据锁"讲清依据"这件事，不锁它被写成几行。
        if "RawImage" in _fillc and "9 宫格" in _fillc and "desiredTexture" in _fillc:
            ok("Fill.cs 写清了「为什么要 ISleekSprite+Sliced」与「为什么不设 Texture 会静默失效」")
        else:
            err("Fill.cs 没写清引擎依据（RawImage 无 9 宫格 / desiredTexture 静默失效）—— "
                "下一个人会把它当多余代码删掉：症状是填充色与圆角又都没了，而且不报错")

    # ---- 14.9k 每格填充色 CellFill / 蓝图填充登记处（★ 26.10.3.51）----
    #   钉的全是**行为**，不是写法：
    #     ① 网格 / 列表公开 `CellFill`，且解析走共享层（三处不会各自漂移）；
    #     ② 填充层必须是**第一个子元素** —— 同一父容器里后加的盖在先加的上面，
    #        晚建一层就会盖住图标与文字（"语句在不在" ≠ "顺序对不对"，这条专门锁顺序）；
    #     ③ 蓝图的填充**不能是实例属性**：那个视图的实例第三方拿不到，
    #        挂实例属性就是写了个谁也调不到的公开面（半截 API）；必须按来源登记；
    #     ④ 登记处：同一来源重复 `Add` 是**替换**不是叠加；`Remove` 只摘自己那条；
    #        `Sources()` 返回**副本**（外部改它不能动到内部）。
    _grid = src.get("CustomUI.GridPanel.cs", "")
    _list = src.get("CustomUI.ListPanel.cs", "")
    _bpv = src.get("CustomUI.BlueprintView.cs", "")
    _fils = src.get("CustomUI.Fill.cs", "")

    for (_vn, _vs, _reb, _iconCall) in (("大格子", _grid, "private void RebuildCell(", "btn.AddChild(icon)"),
                                        ("列表", _list, "private void RebuildRow(", "btn.AddChild(icon)"),
                                        ("蓝图", _bpv, "private void RebuildRow(", "btn.AddChild(icon)")):
        if not _vs:
            err("14.9k 读不到" + _vn + "视图源码 —— 判据失效（别静默跳过）")
            continue
        _rb = fn_body(_vs, _reb, _vn + ".Rebuild")
        if _rb is None:
            err("14.9k 读不出" + _vn + "的行/格重建函数 —— 判据失效")
            continue
        # ⚠ 先剔注释再量顺序：注释里写一句"Fill.Build(...)"就会把顺序判成对的（阳性对照抓过）
        _rb = strip_comments(_rb)
        _iFill = _rb.find("Fill.Build(")
        _iIcon = _rb.find(_iconCall)
        if _iFill < 0:
            err(_vn + "的格子/行没建填充层 —— CellFill 无处落地")
        elif _iIcon >= 0 and _iFill > _iIcon:
            err(_vn + "的填充层建在图标**之后** —— 同一父容器里后加的盖在先加的上面，"
                "它会盖住图标与文字")
        else:
            ok(_vn + "的填充层建在子内容之前（盖在盒子背景上、又在图标文字之下）")

    for (_vn, _vs) in (("大格子", _grid), ("列表", _list)):
        if re.search(r"public\s+StorageCellFillHandler\s+CellFill\b", _vs):
            ok(_vn + "视图公开了 CellFill（每格铺什么颜色由接入方定）")
        else:
            err(_vn + "视图没有 CellFill —— 第三方没法给它的格子/行上色")
        _ap = fn_body(_vs, "private void ApplyCellFill(", _vn + ".ApplyCellFill")
        _ap = strip_comments(_ap) if _ap is not None else None   # ⚠ 不剔注释：把某行注释掉也算"有"
        if _ap is not None and "Fill.Color" in _ap and "StorageUiShared.ResolveCellFill(" in _ap:
            ok(_vn + "每格填色的解析走共享层（到处一份：与另两个视图同一个实现）")
        else:
            err(_vn + "的 ApplyCellFill 没把结果写进填充层，或没走共享解析 —— "
                "三份实现迟早漂")

    # ③ 蓝图：**不许**有实例 CellFill（它的实例第三方拿不到）
    _bpv_code = strip_comments(_bpv)
    if re.search(r"public\s+StorageBlueprintFillHandler\s+CellFill\b", _bpv_code):
        err("蓝图视图挂了实例 CellFill —— 它的实例第三方根本拿不到，这个属性谁也调不到"
            "（半截 API）；应当按来源登记")
    else:
        ok("蓝图视图没挂实例 CellFill（改用按来源登记 —— 第三方够得着）")
    _bp_ap = fn_body(_bpv, "private void ApplyCellFill(", "Blueprint.ApplyCellFill")
    _bp_ap = strip_comments(_bp_ap) if _bp_ap is not None else None
    if _bp_ap is not None and "StorageBlueprintFills.Resolve(" in _bp_ap:
        ok("蓝图行的填充色走登记处解析（每轮重算）")
    else:
        err("蓝图视图没有通过登记处取填充色 —— 用登记式那条就断了")

    # ④ 登记处的账目语义
    _add = fn_body(_fils, "public static void Add(", "Fills.Add")
    _rmv = fn_body(_fils, "public static bool Remove(", "Fills.Remove")
    _srcs = fn_body(_fils, "public static string[] Sources(", "Fills.Sources")
    # ⚠ 三处都先剔注释（把语义那段注释掉、只留注释，不许算"还有语义"）
    _add = strip_comments(_add) if _add is not None else None
    _rmv = strip_comments(_rmv) if _rmv is not None else None
    _srcs = strip_comments(_srcs) if _srcs is not None else None
    if _add is not None and "entries[i].handler = handler;" in _add:
        ok("同一来源重复登记 = 替换（不是叠加 —— 叠加会让两个插件的规则互相顶）")
    else:
        err("登记处对同一来源没有替换语义 —— 重复登记会叠加，谁生效变成玄学")
    if _rmv is not None and "entries[i].source != source" in _rmv and "RemoveAt(" in _rmv:
        ok("摘除只摘**自己那条**（拿别人的来源串才摘得掉别人的）")
    else:
        err("摘除不是按来源来的 —— 会把别人的规则一起摘掉")
    if _srcs is not None and "new string[entries.Count]" in _srcs:
        ok("来源清单返回**副本**（外部改它不影响内部账目）")
    else:
        err("来源清单把内部容器交出去了 —— 外部一改就坏账")

    # ⑤ 颜色**不进指纹**：进了指纹 = 换个颜色就全量重建 + 图标重载
    for (_vn, _fp) in (("大格子", _grid), ("列表", _list), ("蓝图", _bpv)):
        _hit = False
        for (_sig, _lab) in (("private static int CellFingerprint(", "CellFingerprint"),
                             ("private static int GroupFingerprint(", "GroupFingerprint"),
                             ("private static int Fingerprint(", "Fingerprint"),
                             ("private static int TextFingerprint(", "TextFingerprint")):
            # ⚠ 三个视图各定义自己那几个指纹函数，**不能盲目 fn_body** ——
            #   它找不到会当场 err（判据自己制造假 FAIL）。
            if _sig not in _fp:
                continue
            _body = fn_body(_fp, _sig, _vn + "." + _lab)
            if _body is None:
                continue
            _hit = True
            if "Fill" in _body or "_cellFill" in _body:
                err(_vn + "的 " + _lab + " 指纹里混进了填充色 —— 换个颜色就会全量重建 + 图标重载")
            elif "state" in _body:
                err(_vn + "的 " + _lab + " 指纹混进 state（老坑 #17）")
        if _hit:
            ok(_vn + "的指纹里没有填充色（换色只改颜色、不重建）")

    # ---- 14.9m 进度条组件自身（★ 26.10.3.55 公开面 `StorageUiBar`）----
    #   它存在的理由是**替调用方挡住几件"只有读引擎源码才知道"的事**：
    #     ① 要**圆角**只能 `ISleekSprite`（后端 uGUI `Image`，`GlazierSprite_uGUI.cs:111`）
    #        —— `ISleekImage` 是 `RawImage`（`:199`），**整张拉伸、没有 9 宫格** ⟹ 只能画直角（坑 #84）。
    #     ② `Sprite` 的 setter **不改** `Image.type`，改类型的是 `DrawMethod`
    #        （`GlazierSprite_uGUI.cs:53-74`），且 `ConstructNew()` 默认是 **`Tiled`**（`:116`）
    #        ⟹ 不显式设 `DrawMethod` 就是平铺（形状根本不对）。
    #     ③ `SynchronizeColors` 只在 `Sprite != null` 时把这一层 `enabled = true`（`:118-127`）
    #        ⟹ 先挂 `Sprite` 再写 `TintColor`，否则颜色不生效（与 `desiredTexture` 同族，坑 #84）。
    #   再钉住它自己的语义：`Fill` 是**唯一**的"多满"口径、改 `Fill` 只改几何不重建、
    #   文字空串只藏不拆、填充必须挂在**轨道**上且**先于**文字、
    #   **`Build()` 必须把 `Hide()` 藏起来的那层重新显示**（本轮自检 `Build恢复可见=False` 抓的就是它）。
    _bar = src.get("CustomUI.Bar.cs", "")
    _barcode = strip_comments(_bar)

    _bb = fn_body(_bar, "public void Build(", "Bar.Build")
    if _bb is None:
        err("14.9m 读不出 StorageUiBar.Build —— 判据失效（别让它静默跳过）")
    else:
        # ① 轨道与填充都必须用 `ISleekSprite`（要圆角就只能走它）
        if _bb.count("CreateSprite()") >= 2:
            ok("轨道与填充都用 `CreateSprite()`（`ISleekSprite` —— 圆角的前提）")
        else:
            err("进度条没用 `ISleekSprite` 画 —— `ISleekImage` 是 RawImage（无 9 宫格）"
                "⟹ 只能画直角，设了圆角也不生效")

        # ①b `DrawMethod` 必须在**两个分支**都显式设（默认 Tiled，不设就画错）
        _shape = fn_body(_bar, "private void ApplyShape(", "Bar.ApplyShape")
        if _shape is not None and "ESleekSpriteType.Sliced" in _shape \
                and "ESleekSpriteType.Regular" in _shape:
            ok("平角/圆角两个分支都显式设了 `DrawMethod`（默认是 Tiled，不设就画错）")
        else:
            err("`ApplyShape` 没在两个分支都设 `DrawMethod` —— `Sprite` 的 setter 不改 Image.type，"
                "默认 Tiled ⟹ 形状不对")

        # ② 顺序：填充必须**先于**文字挂（同一父容器里后加的盖在先加的上面）
        _iF = _bb.find("root.AddChild(fill)")
        _iL = _bb.find("root.AddChild(label)")
        if _iF >= 0 and _iL >= 0 and _iF < _iL:
            ok("填充建在文字之前（顺序反了文字会被填充盖住 —— 语法正确但不显示）")
        else:
            err("填充与文字的挂载顺序不对 —— 后加的那个盖在上面（文字会被填充盖住）")

        # ③ 填充必须挂在**轨道**上，不是挂在 parent 上
        if "root.AddChild(fill)" in _bb and "parent.AddChild(fill)" not in _bb:
            ok("填充是**轨道**的子元素（挂到 parent 上就变成铺满整块，而不是按比例）")
        else:
            err("填充没挂在轨道上 —— 它的宽度就不再是“轨道的比例”了")

        # ③b ★ `Build()` 必须恢复可见性 —— 否则 `Hide()` 之后再也显示不出来（本轮真撞到的 FAIL）
        if "IsVisible = true" in _bb:
            ok("`Build()` 显式恢复可见性（Hide 之后能重新显示 —— 漏了就是“打开也不出现”）")
        else:
            err("`Build()` 没恢复可见性 —— `Hide()` 藏了之后再也显示不出来（自检 Build恢复可见=False）")

    # ④ 反向锁：代码里不许碰 `ShouldDestroyTexture`（注释里讲它为什么不能碰，不算）
    if "ShouldDestroyTexture" in _barcode:
        err("代码里碰了 `ShouldDestroyTexture` —— 本组件挂的是 Unity **内置公共**贴图，"
            "置 true 会在拆控件时去销毁公共资源（`InternalDestroy` :112-119）")
    else:
        ok("没碰 `ShouldDestroyTexture`（保持默认 false）⟹ 拆的时候不会去销毁公共贴图")

    # ⑤ `Fill` 必须自动夹到 [0,1]
    if re.search(r"public float Fill\s*\{", _bar) and "value < 0f" in _bar and "value > 1f" in _bar:
        ok("`Fill` 自动夹到 [0,1] —— 调用方不用自己防越界")
    else:
        err("`Fill` 没夹取 —— 越界值会一路写进 `SizeScale_X`（负数 / 超出父容器）")

    # ⑥ 反向锁：不许长出第二个"多满"口径（同一事实两处存 = 必然漂，铁律 #5）
    if re.search(r"public\s+(?:float|int)\s+(?:Min|Max|Value|Percent|Percentage)\b", _barcode):
        err("进度条长出了第二个“多满”口径（Min / Max / Value / Percent）"
            " —— 同一事实写在两处 ⟹ 迟早有一处是错的")
    else:
        ok("`Fill` 是唯一的“多满”口径（没有第二份真源）")

    # ⑦ 改 `Fill` 只写几何，不重建控件
    _lf = fn_body(_bar, "private void LayoutFill(", "Bar.LayoutFill")
    if _lf is not None and "SizeScale_X" in _lf \
            and "AddChild" not in _lf and "Create" not in _lf:
        ok("改 `Fill` 只写一次 `SizeScale_X`（不重建控件 —— 每帧设一次是常见用法）")
    else:
        err("改 `Fill` 动到了控件树 —— 每帧设一次就会每帧重建")

    # ⑧ 文字空串：把那一层**藏起来**（不拆控件）
    _at = fn_body(_bar, "private void ApplyText(", "Bar.ApplyText")
    if _at is not None and "IsVisible = show" in _at and "RemoveChild" not in _at:
        ok("文字空串把那一层藏起来（只藏不拆 —— 拆了下次还得重建）")
    else:
        err("文字开关不是“只藏不拆” —— 空串会把标签拆掉，下次显示要重建")

    # ⑨ 上色必须走 `TintColor`（真彩色），不许退回只有 alpha 有效的 `BackgroundColor`
    if "TintColor" in _barcode and "BackgroundColor" not in _barcode:
        ok("上色走 `TintColor`（真彩色）—— 没退回只有 alpha 有效的 `BackgroundColor`（坑 #81）")
    else:
        err("进度条用了 `BackgroundColor` —— 盒贴图填充是纯黑 + uGUI 颜色是乘法 ⟹ 改不了色相")

    # ⑩ `Radius` 可配（默认 0 = 平角），且不许为负
    if re.search(r"public float Radius\s*\{", _bar) and "value < 0f" in _bar:
        ok("有可配置 `Radius`（默认 0 = 平角、不为负）—— 圆角是旋钮不是写死的")
    else:
        err("`Radius` 不是可配置的非负旋钮")

    # ⑪ 圆角贴图必须带 9 宫格 `border`（`Sliced` 靠它，否则整张拉伸 ⟹ 圆角变椭圆）
    #   ★ 26.10.3.64：贴图生成搬到 `StorageUiShapes` ⟹ 判据跟着搬（Bar 里已经没有这段了）。
    if "Sprite.Create(" in _shapesc and re.search(r"Vector4\(\s*R\s*,", _shapesc):
        ok("圆角贴图带 9 宫格 `border`（= 半径）—— 拉长也不把圆角拉成椭圆")
    else:
        err("圆角贴图没设 `border` ⟹ `Sliced` 退化成整张拉伸（圆角变椭圆）")

    # ---- 14.9n 下拉选择器组件自身（★ 26.10.3.56 公开面 `StorageUiDropdown`）----
    #   它存在的理由是**替调用方挡住一件引擎根本没有的能力**：Glazier **没有** `ISleekDropdown`
    #   （原版也没有真正的下拉）⟹ 只能自己拼"按钮 + 弹层"。拼弹层有两条铁律：
    #     ① **弹层必须与触发器同 parent、且在触发器之后 AddChild** —— Glazier 既没有浮层 API、
    #        也没有绝对位置 API（`ISleekElement` 只有 `GetAbsoluteSize()`），层叠只能靠
    #        "同一父容器里后加的盖在先加的上面"（`GlazierElementBase_uGUI.cs:333`）。
    #     ② **同一 parent 重复 AddChild 是 no-op**（`:328-329`）⟹ 打开时没法"再挂一次"提升层级；
    #        所以收起**只能** `IsVisible = false` —— `RemoveChild` 是"真拆"（末尾 `InternalDestroy`）。
    #   再钉住它自己的语义：程序设值**不回调**、选项回调**只捕获下标**（列表会重排）、
    #   越界**自动夹取**、按钮池**只增不减**、回调**异常隔离**、`Build` **恢复可见性**。
    _drop = src.get("CustomUI.Dropdown.cs", "")
    if not _drop or "public class StorageUiDropdown" not in _drop:
        err("14.9n 无法执行：下拉选择器组件源码缺失（CustomUI.Dropdown.cs）")
    else:
        _dropc = strip_comments(_drop)

        _ddb = fn_body(_dropc, "public void Build(", "Dropdown.Build")
        if _ddb is None:
            err("14.9n 读不出 StorageUiDropdown.Build —— 判据失效（别让它静默跳过）")
        else:
            # ① 触发器先加、弹层后加（后加 = 盖在上面）
            #   ⚠ Build 有**两个分支**（重挂 / 新建），两边各有一对 AddChild —— 判据必须
            #     **认分支**（铁律 #41）：只看"新建"那一段，否则重挂分支的顺序会把
            #     "新建分支写反了"盖过去（阳性对照实测假绿过一次）。
            _ddnew = _ddb[_ddb.find("trigger = Glazier.Get().CreateButton()"):] \
                if "trigger = Glazier.Get().CreateButton()" in _ddb else _ddb
            _iT = _ddnew.find("parent.AddChild(trigger)")
            _iP = _ddnew.find("parent.AddChild(popup)")
            if _iT >= 0 and _iP >= 0 and _iT < _iP:
                ok("弹层在触发器**之后**加入同一 parent（后加 = 画在上面，Glazier 唯一的层叠手段）")
            else:
                err("弹层没有在触发器之后加入同一 parent —— Glazier 没有浮层 API，"
                    "层叠只能靠 AddChild 顺序；顺序反了弹层会被触发器或其它内容盖住")

            # ② Build 必须恢复可见性（两个分支各一处：重挂分支 + 新建分支）
            if _ddb.count("IsVisible = true") >= 2:
                ok("下拉的 `Build()` 显式恢复可见性（Hide 之后能重新显示）")
            else:
                err("下拉的 `Build()` 没恢复可见性 —— `Hide()` 藏了之后再也显示不出来")

        # ③ 收起弹层只用 IsVisible，**不许 RemoveChild**（反向锁；注释里讲它不能碰，不算）
        _ddcl = fn_body(_dropc, "public void Close(", "Dropdown.Close")
        if _ddcl is None:
            err("14.9n 读不出 StorageUiDropdown.Close —— 判据失效")
        elif "RemoveChild" in _ddcl:
            err("收起弹层用了 RemoveChild —— 那是 `InternalDestroy()`（真拆），下次打开就没弹层了"
                "（铁律 #15/#38）")
        elif "IsVisible = false" in _ddcl:
            ok("收起弹层走 `IsVisible = false`（只藏不拆）")
        else:
            err("收起弹层既没藏也没拆 —— 实现变了或判据写错了")

        # ④ 程序设 `SelectedIndex` **不触发**回调（否则成环）；⑤ 越界自动夹取
        _ddss = re.search(r"public int SelectedIndex[\s\S]*?set\s*\{([\s\S]*?)\n        \}", _dropc)
        if _ddss is None:
            err("14.9n 读不出 SelectedIndex 的 setter —— 判据失效")
        else:
            _body = _ddss.group(1)
            if "FireSelectionChanged" in _body or "OnSelectionChanged" in _body:
                err("程序设 `SelectedIndex` 触发了回调 —— 会形成“设值 → 回调 → 又设值”的回环"
                    "（与 StorageFilterBar.ClearSelection 同一条约定）")
            else:
                ok("程序设 `SelectedIndex` **不触发**回调（只有玩家点击才回调）")

            if "used - 1" in _body and "< -1" in _body:
                ok("`SelectedIndex` 越界自动夹到 [-1, OptionCount-1]")
            else:
                err("`SelectedIndex` 没夹取 —— 越界值会让按钮文本与状态对不上")

        # ⑥ 选项回调**只捕获下标**（列表会重排，捕获内容 = 点 A 选 B）；⑦ 池只增不减
        _ddei = fn_body(_dropc, "private void EnsureItems(", "Dropdown.EnsureItems")
        if _ddei is None:
            err("14.9n 读不出 EnsureItems —— 判据失效")
        else:
            if "int idx = i;" in _ddei and "OnItemClicked(idx)" in _ddei:
                ok("选项按钮的回调只捕获**下标**（内容点击那一刻现读 —— 列表重排也点得准）")
            else:
                err("选项回调捕获了“当时那一条”而不是下标 —— 选项列表重排后会点 A 选 B")

            if "RemoveChild" in _ddei or "Destroy" in _ddei:
                err("选项按钮池会拆控件 —— 应“只增不减、多余的只藏”（铁律 #16）")
            else:
                ok("选项按钮池只增不减（多余的只 IsVisible=false）")

        # ⑧ 回调异常隔离
        if "CustomUIPlugin.Error" in _dropc and "catch (Exception" in _dropc:
            ok("下拉回调异常被隔离（try/catch + 报错），接入方抛异常不会打断界面")
        else:
            err("下拉回调没做异常隔离 —— 接入方抛异常会打断我们")

        # ⑨ 触发器/弹层是引擎控件（跟随主题与缩放）
        if "CreateButton()" in _dropc and "CreateBox()" in _dropc:
            ok("触发器用 `CreateButton()`、弹层用 `CreateBox()`（都是引擎控件）")
        else:
            err("下拉没用引擎的 Button/Box —— 自造控件会跟主题/缩放脱节")

        # ⑩ 文字容器高度下限 24（坑 #21）：触发按钮与每个选项都是文字容器，
        #    `SizeOffset_Y < 24` 时**一个字都不渲染** ⟹ 两个下限常量都必须是 24。
        #    （26.10.3.57 实测：上一轮选项行高 22，弹层里一个字都没有。）
        if re.search(r"MIN_ROW_HEIGHT\s*=\s*24", _dropc) and \
           re.search(r"MIN_ITEM_HEIGHT\s*=\s*24", _dropc):
            ok("触发器与选项的**文字容器高度下限都是 24**（坑 #21：低于 24 一个字都不渲染）")
        else:
            err("行高下限不是 24 —— 文字容器低于 24 时一个字都不渲染、还不报错（坑 #21）")

        # ⑪ `ItemHeight` 的 setter 用**它自己的**下限常量（与 RowHeight 各管一头，将来分家不会互相带偏）
        if "value < MIN_ITEM_HEIGHT" in _dropc:
            ok("`ItemHeight` 的 setter 用它自己的下限常量（没借 RowHeight 的）")
        else:
            err("`ItemHeight` 的下限借用了别的常量 —— 两处下限将来会各自漂移")

        # ⑫ ★★ 弹层能**向上**展开（坑 #91）：父容器若是滚动区（`ISleekScrollView`），
        #    uGUI `ScrollRect` 的 viewport 会**裁掉**超出可视范围的弹层 —— 不只是像素，
        #    **连点击都收不到**。向下展开在"贴着面板底部"时必然中招 ⟹ 必须有向上分支。
        _ddlp = fn_body(_dropc, "private void LayoutPopup(", "Dropdown.LayoutPopup")
        if _ddlp is None:
            err("14.9n 读不出 LayoutPopup —— 判据失效")
        else:
            # 认分支（铁律 #41）：`PositionOffset_Y` 必须是带 `_openUpward` 的**三元**，
            # 且向上那一支是**减法**（顶边退到触发器上方）。
            if "_openUpward" in _ddlp and "- popupHeight" in _ddlp:
                ok("弹层支持**向上**展开（`PositionOffset_Y` 在 `_openUpward` 时用减法 —— 绕开滚动区裁剪）")
            else:
                err("弹层只会向下展开 —— 挂在滚动容器里会被 viewport 裁掉，"
                    "被裁的部分连点击都收不到（坑 #91）")

        # ⑬ `OpenUpward` 是公开旋钮，且**已展开时改它会立刻重排**（否则要收起再开一次才翻面）
        _dduw = re.search(r"public bool OpenUpward[\s\S]*?set\s*\{([\s\S]*?)\n        \}", _dropc)
        if _dduw is None:
            err("14.9n 读不出 OpenUpward 的 setter —— 判据失效")
        elif "LayoutPopup" in _dduw.group(1):
            ok("`OpenUpward` 在已展开时改会**立刻重排**（不用先收起再开）")
        else:
            err("改 `OpenUpward` 不会重排 —— 已经打开的弹层要收起再开才翻面")

        # ⑭ ★★ 弹层必须自己叠一层**不透明实心底**（坑 #92）：主题盒贴图 `DarkTheme/Box.png`
        #    的填充 alpha 只有 **191/255**（实测 `(0,0,0,191)`；LightTheme 是 `(255,255,255,191)`），
        #    而 uGUI `Image.color` 是**乘法** ⟹ `ISleekBox.BackgroundColor` 的上限就是 ~75% 不透明，
        #    **永远透出下面的内容**（用户实测「仍然透明」）。`StorageUiFill` 走的是**纯白圆角贴图
        #    （alpha 255）** + `TintColor` ⟹ 真不透明。
        #    ⚠ 认分支（铁律 #41）：`Build` 有两个分支、`parent.AddChild(popup)` 出现两次，
        #      `find` 取第一处会被**重挂分支**盖过去 ⟹ 只看从 `CreateBox()` 开始的那一段。
        _ddnew2 = _ddb[_ddb.find("popup = Glazier.Get().CreateBox()"):] \
            if "popup = Glazier.Get().CreateBox()" in _ddb else _ddb
        if "popupSolid = new StorageUiFill()" not in _ddnew2:
            err("弹层没有实心底 —— 单靠 `ISleekBox.BackgroundColor` 永远只有 ~75% 不透明（坑 #92）")
        elif "popupSolid.Build(popup)" not in _ddnew2:
            err("实心底建了却没挂进弹层 —— 缺 `popupSolid.Build(popup)`（要铺满它）")
        else:
            ok("弹层自己叠了一层**不透明实心底**（`StorageUiFill`）—— 绕开 Box 贴图那道 191/255 衰减（坑 #92）")

        # ⑮ 旋钮必须**两边都喂**：`PopupBackground` 改色要同时写给 Box 与实心底
        #    （只喂一边 = 改了旋钮看着没反应，或 Box 的圆角外沿与实心底不同色）
        _ddpb = re.search(r"public SleekColor PopupBackground[\s\S]*?set\s*\{([\s\S]*?)\n        \}", _dropc)
        if _ddpb is None:
            err("14.9n 读不出 PopupBackground 的 setter —— 判据失效")
        elif "popup.BackgroundColor" in _ddpb.group(1) and "popupSolid.Color" in _ddpb.group(1):
            ok("`PopupBackground` 同时喂给 Box 与实心底（改一个旋钮两处一起变）")
        else:
            err("`PopupBackground` 只喂了一边 —— 实心底 / Box 会各显各的色")

        # ⑯ ★★ `_popupBackground` 的**默认值**必须是真彩色，**不能**是主题 tint（坑 #93）：
        #    实心层是**自绘白贴图**（只让 alpha 携带圆角形状），TintColor 是乘法 ⟹ 它吃的是
        #    目标真彩色。而本主题的颜色由**贴图**携带（`Box.png` 黑）、tint 只贡献 alpha，
        #    `ESleekTint.BACKGROUND` 解析出来是 `SleekCustomization.backgroundColor`（**默认白**）
        #    ⟹ 白贴图 × 白 = **白色弹层**（用户 2026-10-10 实测「确实不透明，但是是白色的」）。
        _dddef = re.search(r"private SleekColor _popupBackground\s*=\s*([^;]+);", _dropc)
        if _dddef is None:
            err("14.9n 读不出 `_popupBackground` 的默认值 —— 判据失效")
        else:
            _dv = _dddef.group(1)
            if "ESleekTint." in _dv:
                err("`_popupBackground` 默认值用了**主题 tint** —— 本主题解析出来是白 ⟹ "
                    "白贴图乘上去 = **白色弹层**（坑 #93）")
            elif ("BackgroundIfLight" in _dv) or ("new Color" in _dv):
                ok("`_popupBackground` 默认值是**真彩色**（非主题 tint）—— 白贴图才画得出深色（坑 #93）")
            else:
                err("`_popupBackground` 默认值不是显式真彩色 —— 无法保证弹层不是白的（坑 #93）")

    # ---- 14.9o 「常驻右列区块」样板基类（★ 26.10.3.60 公开面 `StorageUiRegionBase`）----
    #   存在的理由：往右列区块栈加一块面板要守**四条规矩**（只藏不拆 / 判代 / `Tick` 守卫 /
    #   失败自拆），而接入方那边是**一模一样**的四段样板 —— 接入自检自己就抄了四遍。
    #   ⟹ 判据必须钉住「这四条规矩**真的在里面**」：基类退化成空壳比没有基类**更糟**
    #     （接入方以为它管了，于是四处都不再自查）。
    _reg_src = src.get("CustomUI.Region.cs", "")
    if not _reg_src:
        err("找不到 CustomUI.Region.cs —— 14.9o 判据失效（基类文件没了？）")
    else:
        _reg_c = strip_comments(_reg_src)

        # ① 两个接口都得实现（少一个就挂不进区块栈 / 进不了候选链）
        if "class StorageUiRegionBase : IStorageUiProvider, IStorageUiRegion" in _reg_c:
            ok("`StorageUiRegionBase` 同时实现 `IStorageUiProvider` + `IStorageUiRegion`")
        else:
            err("基类没有同时实现两个接口 —— 少一个就挂不进区块栈")

        # ② 区块**不抢存储位**：`CanHandle` 恒 false
        if re.search(r"public bool CanHandle\(StorageUiContext ctx\)\s*\{\s*return false;\s*\}", _reg_c):
            ok("`CanHandle` **恒 false** —— 区块不抢存储位（抢了它就成了主面板，得走另一条路）")
        else:
            err("区块基类的 `CanHandle` 不是恒 false —— 区块接管了存储位（同一条内容会显示两遍）")

        # ③ `Open`：判代 + 建 + 刷新，且失败时**自己拆半成品**
        _ob = fn_body(_reg_c, "public bool Open(", "StorageUiRegionBase.Open")
        if _ob is not None:
            if "IsUsableAt(StorageUiPanels.AttachSpot())" in _ob:
                ok("`Open` 里做了**判代**（切关/换服后旧画布的 Parent 还指着废实例 —— README §4.7）")
            else:
                err("`Open` 没判代 —— 换服后会拿一个已销毁的画布元素接着用")
            # ⚠ 必须**认分支**：`Open` 里 `Discard()` 有**两处**（判代那一行 + `catch` 块）——
            #   只判"整段里有 `Discard()`"会被判代那行**永远满足**（本判据第一版就是这么假绿的，
            #   阳性对照当场抓出来。铁律 #41：判"某函数里有 X"先问"这个函数里有几处 X"）。
            _cat = re.search(r"catch \(Exception[^)]*\)\s*\{([\s\S]*?)\n            \}", _ob)
            if _cat is not None and "Discard()" in _cat.group(1):
                ok("`Open` 失败时**自己拆半成品**（宿主返回 false 后不会调 `Close` —— 接口契约）")
            else:
                err("`Open` 失败时没自己拆 —— 宿主不会调 `Close`，会留下挂着的残骸")

        # ④ `Close` **只藏不拆**（反向锁：不许 `RemoveChild`；⚠ 反向断言先剔注释）
        _cl = fn_body(_reg_c, "public void Close(", "StorageUiRegionBase.Close")
        if _cl is not None:
            if "RemoveChild" in _cl:
                err("`Close` 用了 `RemoveChild` —— 那是 `GameObject.Destroy`，会把行池与"
                    "已渲染图标一起毁掉（坑 #15/#38）")
            elif "Hide()" in _cl:
                ok("`Close` **只藏不拆**（`Hide()`；反向锁：没有 `RemoveChild`）")
            else:
                err("`Close` 既没 `RemoveChild` 也没调 `Hide()` —— 它到底做了什么？")

        # ⑤ `Tick` / `Rebuild` 必须有 `IsBuilt` 守卫（没建好时刷新会读到空控件）
        _guards = []
        for _m in ["public void Tick(", "public void Rebuild("]:
            _mb = fn_body(_reg_c, _m, "StorageUiRegionBase" + _m)
            if _mb is not None and "IsBuilt" in _mb:
                _guards.append(_m)
        if len(_guards) == 2:
            ok("`Tick` 与 `Rebuild` 都有 **`IsBuilt` 守卫**（面板没建好时刷新会读到空控件）")
        else:
            err("`Tick`/`Rebuild` 少了 `IsBuilt` 守卫（只有 " + str(len(_guards)) + " 个有）")

        # ⑥ `RegionPanel` 交的是**面板自己的 `Element`**
        _rp = fn_body(_reg_c, "public ISleekElement RegionPanel(", "StorageUiRegionBase.RegionPanel")
        if _rp is None:
            err("14.9o 读不出基类的 `RegionPanel` —— 判据失效")
        elif ".Element" in _rp:
            ok("`RegionPanel` 交的是**面板自己的 `Element`**")
        else:
            err("`RegionPanel` 没交 `Element` —— 宿主挂上去的会是别的什么")

        # ⑦ 契约接口存在，且两个面板都实现了它（否则基类只能替一个实现服务）
        _prov_c = strip_comments(src.get("CustomUI.Provider.cs", ""))
        if re.search(r"public interface IStorageUiPanel\b", _prov_c):
            ok("公开接口 `IStorageUiPanel` 存在（区块栈必须问到的七件事）")
        else:
            err("没有 `IStorageUiPanel` —— 基类只能替某一个实现服务（或退化成 object + 反射）")
        _impl = []
        for (_fn, _cls) in [("CustomUI.GridPanel.cs", "StorageGridPanel"),
                            ("CustomUI.ListPanel.cs", "StorageListPanel")]:
            if ("class " + _cls + " : IStorageUiPanel") in strip_comments(src.get(_fn, "")):
                _impl.append(_cls)
        if len(_impl) == 2:
            ok("`StorageGridPanel` / `StorageListPanel` 都实现了 `IStorageUiPanel`（加接口是**兼容**的）")
        else:
            err("有面板没实现 `IStorageUiPanel`（只看到 " + str(len(_impl)) + " 个）—— 基类替它服务不了")

    # ---- 14.9p 右键上下文菜单（★ 26.10.3.61 公开面 `StorageUiMenu`）----
    #   它和下拉**形状不同**：没有"触发器元素" ⟹ 必须盖住**整个界面**，
    #   于是挂到**全屏容器** `PlayerUI.container` 上，照原版 `outsideSelectionInvisibleButton`
    #   那套「**遮罩先加、面板后加**」（后加 = 盖在上面）。判据钉住这套**顺序** + 三件易错事
    #   （遮罩不挂贴图 / 面板底色吃主题 tint 变白 / 禁用项只变灰没关 `IsClickable`）。
    _menu_src = src.get("CustomUI.Menu.cs", "")
    if not _menu_src:
        err("找不到 CustomUI.Menu.cs —— 14.9p 判据失效（组件文件没了？）")
    else:
        _menu_c = strip_comments(_menu_src)

        # ① 挂的是**全屏容器**（PlayerUI.container），不是调用方传进来的 parent。
        #   ⚠ 必须锁**取容器的那个表达式**（`return PlayerUI.container;`）——
        #     只判 `"PlayerUI.container" in src` 会被**日志字符串**里的同名子串满足
        #     ⟹ 永远绿（铁律 #8：太松 = 永远绿）。
        if re.search(r"return\s+PlayerUI\.container\s*;", _menu_c):
            ok("菜单挂在**全屏容器** `PlayerUI.container` 上（能盖住原版右列与玩家背包）")
        else:
            err("菜单没挂 `PlayerUI.container` —— 它盖不住不属于我们的元素")

        # ② 遮罩**先加**、面板**后加**（后加 = 画在上面）。
        #   ⚠ 认分支：`Build` 有**新建 / 重挂**两个分支，每个分支里都有一对 AddChild ⟹
        #     用"第一对"+"最后一对"两头卡（铁律 #41 —— 只看第一处会漏掉重挂分支写反）。
        _mbody = fn_body(_menu_c, "public void Build(", "Menu.Build")
        _mscr = [m.start() for m in re.finditer(r"AddChild\(scrim\)", _mbody or "")]
        _mpan = [m.start() for m in re.finditer(r"AddChild\(panel\)", _mbody or "")]
        if _mbody is not None and _mscr and _mpan \
                and _mscr[0] < _mpan[0] and _mscr[-1] < _mpan[-1]:
            ok("`Build` 里**遮罩先加、菜单后加**（两个分支都对 —— 后加 = 盖在上面）")
        else:
            err("`Build` 的 AddChild 顺序不对 —— 重挂分支把遮罩加在了面板后面 ⟹ 遮罩会盖住菜单")

        # ③ 遮罩是 `ISleekImage` 且**显式挂了贴图**（不挂 = 整层不渲染，坑 #84）
        if re.search(r"scrim\s*=\s*Glazier\.Get\(\)\.CreateImage\(\)", _menu_c) \
                and "scrim.Texture = Texture2D.whiteTexture" in _menu_c:
            ok("遮罩是 `ISleekImage` 且**显式挂了白贴图**（不挂 = 不渲染还不报错，坑 #84）")
        else:
            err("遮罩没显式挂贴图 —— uGUI 会因 `desiredTexture == null` 把整层禁用（坑 #84）")

        # ④ 点遮罩 = 点到了菜单外面 ⟹ 收起；且回调**无参**（`ISleekImage.OnClicked` 是 `Action`）。
        #   ⚠ 订阅句要锁**完整语句**（带 `;`）—— 只判 `"…+= OnScrimClicked"` 会被
        #     `OnScrimClickedBak` 这种**前缀**满足 ⟹ 永远绿（坑 #77/#83 同族）。
        if re.search(r"scrim\.OnClicked\s*\+=\s*OnScrimClicked\s*;", _menu_c) \
                and re.search(r"void OnScrimClicked\(\)\s*\{[\s\S]*?Hide\(\)", _menu_c):
            ok("点遮罩 → `Hide()`（「点到了菜单外面」的语义）；回调**无参**（`ISleekImage.OnClicked` 是 `Action`）")
        else:
            err("遮罩点击没接 `Hide()`，或回调签名照抄了 `ISleekButton` 的带参版本")

        # ⑤ 面板叠 `StorageUiFill` 不透明实心底（绕开主题盒贴图 191/255 天花板，坑 #92）
        if re.search(r"panelSolid\s*=\s*new StorageUiFill\(\)", _menu_c) \
                and "panelSolid.Build(panel)" in _menu_c:
            ok("面板叠了 `StorageUiFill` 不透明实心底（绕开 `Box.png` 191/255 天花板，坑 #92）")
        else:
            err("面板没叠实心底 —— `ISleekBox` 底色上限 ~75%，永远透光（坑 #92）")

        # ⑥ `PopupBackground` 的**默认值必须真彩色**（给 `ESleekTint.*` 会解析成白，坑 #93）
        _pbg = re.search(r"_popupBackground\s*=\s*([^;]+);", _menu_c)
        if _pbg is not None and "ESleekTint." not in _pbg.group(1) \
                and ("BackgroundIfLight" in _pbg.group(1) or "new Color" in _pbg.group(1)):
            ok("`PopupBackground` 默认值是**真彩色**（实心层是白贴图，主题 tint 会解析成白，坑 #93）")
        else:
            err("`PopupBackground` 默认值用了主题 tint —— 实心层会画成**白色**（坑 #93）")

        # ⑦ 行高下限 24（标题行 + 选项行**两条**都要）—— 低于它文字一个都不渲染（坑 #21）
        _clamps = len(re.findall(r"value < MIN_ROW_HEIGHT \? MIN_ROW_HEIGHT : value", _menu_c))
        if _clamps >= 2:
            ok("`ItemHeight` / `HeaderHeight` 都夹到 **24**（坑 #21）")
        else:
            err("行高下限只夹了 " + str(_clamps) + " 条 —— 漏的那条 < 24 时文字不渲染（坑 #21）")

        # ⑧ 禁用项**真的点不动**（`IsClickable = false`），不只是变灰
        if re.search(r"IsClickable\s*=\s*on\s*;", _menu_c):
            ok("禁用项 `IsClickable = false` —— **点不动**，不只是变灰")
        else:
            err("禁用项只改了颜色没关 `IsClickable` —— 玩家照样点得动")

        # ⑨ 按钮池**只增不减**（只在不够用时长大；缩短不销毁）
        if re.search(r"items\.Length\s*<\s*used", _menu_c):
            ok("按钮池**只增不减**（只在不够用时长大，缩短不销毁）")
        else:
            err("按钮池没做「只增不减」—— 缩短时可能销毁控件（下次还得重建）")

        # ⑩ 行回调**只捕获下标**（不闭包捕获数据 —— 行回调要读当前字段）
        if re.search(r"int captured = i;", _menu_c) and "Fire(captured)" in _menu_c:
            ok("选项回调**只捕获下标**（`Fire(captured)`）—— 数据变了回调仍读当前值")
        else:
            err("选项回调闭包捕获了数据 —— 换物品后回调用的是旧值")

        # ⑪ 回调异常隔离（一个插件回调炸了不该卡死菜单）——
        #   ⚠ 认 `Fire` 的**函数体**：全文件里有好几处 `catch (Exception e)`，
        #     锚在全文会"别处的 catch + 别处的 warnedCallback"凑成假绿（坑 #77/#83 同族）。
        _mfire = fn_body(_menu_c, "private void Fire(", "Menu.Fire")
        if _mfire is not None and re.search(r"catch \(Exception e\)", _mfire) \
                and "warnedCallback" in _mfire:
            ok("`OnItemSelected` 回调**异常隔离**（只报一次，不卡死菜单）")
        else:
            err("回调没做异常隔离 —— 插件回调抛异常会卡住菜单")

        # ⑫ `Hide` **只藏不拆**（反向锁：不许 `RemoveChild`）；`Discard` 才真拆
        _mhid = fn_body(_menu_c, "public void Hide(", "Menu.Hide")
        if _mhid is not None and "RemoveChild" not in _mhid and "IsVisible = false" in _mhid:
            ok("`Hide` **只藏不拆**（反向锁：没有 `RemoveChild` —— 那是 `GameObject.Destroy`）")
        else:
            err("`Hide` 用了 `RemoveChild` 或没藏可见性 —— 收起会把控件一起销毁（坑 #15/#38）")
        _mdis = fn_body(_menu_c, "public void Discard(", "Menu.Discard")
        if _mdis is not None and "RemoveChild" in _mdis:
            ok("`Discard` 才真拆（`RemoveChild`）—— 明确不要了才走它")
        else:
            err("`Discard` 没拆干净 —— 残骸会留在全屏容器上")

        # ⑭ 每帧自检四条「自动收起」条件都在（`Tick`）—— 26.10.3.63
        #   ⚠ 少任何一条都会留下"该消失却孤零零挂着"的浮层（用户实测报的正是 Esc 那条）。
        _mtick = fn_body(_menu_c, "public void Tick(", "Menu.Tick")
        if _mtick is None:
            err("14.9p 读不出 `Tick` —— 自动收起判据失效")
        else:
            _c1 = ("OverlayParent()" in _mtick) and ("panel.Parent" in _mtick)
            _c2 = ("Application.isFocused" in _mtick) and ("_sawFocus" in _mtick)
            _c3 = "StorageUiPanels.IsOpen" in _mtick
            _c4 = "KeepOpen" in _mtick
            if _c1 and _c2 and _c3 and _c4 and ("Hide()" in _mtick):
                ok("`Tick` 四条自动收起条件齐（画布/容器换实例 · 失焦(**带闩**) · 宿主关了 · 调用方判据）")
            else:
                err("`Tick` 少了自动收起条件（画布=%s 失焦闩=%s 宿主=%s 调用方=%s）"
                    % (_c1, _c2, _c3, _c4))

        # ⑮ `TickAll` 存在，且**插件 `Update` 真的调了它** —— 没人调 = 自动收起等于没有
        if re.search(r"internal static void TickAll\(\)", _menu_c):
            _plug = strip_comments(src.get("CustomUI.cs", ""))
            if "StorageUiMenu.TickAll()" in _plug:
                ok("`TickAll` 由**插件 `Update` 每帧调**（没人调 = 自动收起是死的）")
            else:
                err("插件 `Update` 没调 `StorageUiMenu.TickAll()` —— 自动收起是死的")
        else:
            err("没有 `TickAll` —— 菜单自己不会每帧跑（调用方得手动 Tick，第三方必忘）")

        # ⑯ 自动收起名单**登记 / 注销两头都要有**（只登记不注销 = 残骸留屏，铁律 #35 同源）
        _mbd2 = fn_body(_menu_c, "public void Build(", "Menu.Build")
        _reg = (_mbd2 is not None) and ("live.Add(this)" in _mbd2)
        _unreg = (_mdis is not None) and ("live.Remove(this)" in _mdis)
        if _reg and _unreg:
            ok("自动收起名单**登记/注销两头都有**（`Build` 加、`Discard` 删）")
        else:
            err("自动收起名单只做了一头（登记=%s 注销=%s）—— 只登记不注销会留残骸" % (_reg, _unreg))

        # ⑰ `Build` 幂等分支必须**比对容器**（换关/换服 ⟹ 重挂）。
        #   反向锁：不许"建好了就无条件 return" —— 那会把面板留在**已销毁**的容器上，
        #   之后 `Show` 只让它变可见却永远看不见（与坑 #84 同族的**静默失效**）。
        #   ⚠ 判据用**位置**锁（`ReferenceEquals` 必须出现在"创建面板"之前），
        #     不用 `\{[\s\S]*?ReferenceEquals` —— 那是**无界**的，能一路跨到创建分支里去
        #     （第一版就是那样写的，破坏实验"毫无反应"）。
        _iRef = _mbd2.find("ReferenceEquals(panel.Parent, parent)") if _mbd2 else -1
        _iBox = _mbd2.find("panel = Glazier.Get().CreateBox()") if _mbd2 else -1
        if _iRef >= 0 and _iBox >= 0 and _iRef < _iBox:
            ok("`Build` 幂等分支会**比对容器**（换关/换服后重挂，不是无条件 return）")
        else:
            err("`Build` 幂等分支没比对容器（ReferenceEquals@%d / 创建面板@%d）"
                "—— 换容器后 `Show` 只让它变可见却永远看不见（静默失效）" % (_iRef, _iBox))
        if "if (built && panel != null) return;" in _menu_c:
            err("`Build` 里残留旧的「建好就无条件 return」写法 —— 与重挂分支矛盾")
        else:
            ok("没有残留「建好就无条件 return」的旧写法")

    # ---- 14.9q 视图级外观旋钮（★ 26.10.3.66：`StorageGridPanel` / `StorageListPanel`）----
    #   ★ 三条纪律：① **默认值 = 现状**（加旋钮 ≠ 改默认）；② **派生只有一处口径**
    #     （几何只许读 `CellPitch()` / `RowPitch()`，不许直接读字段或默认常量）；
    #     ③ **夹取**（格高 ≥ 40 / 行高 ≥ 24 / 图标 ≥ 8）。
    _gpc = strip_comments(src.get("CustomUI.GridPanel.cs", ""))
    _lpc = strip_comments(src.get("CustomUI.ListPanel.cs", ""))

    # ① 三个旋钮都在，且都是**属性**（字段就没有 setter 标脏这回事）
    _knob_miss = []
    for (_pat, _tag) in ((r"public float IconSize\s*\{", "IconSize"),
                         (r"public float CellHeight\s*\{", "CellHeight"),
                         (r"public ESleekFontSize LabelFontSize\s*\{", "LabelFontSize")):
        if not re.search(_pat, _gpc):
            _knob_miss.append("大格子." + _tag)
    for (_pat, _tag) in ((r"public float IconSize\s*\{", "IconSize"),
                         (r"public float RowHeight\s*\{", "RowHeight"),
                         (r"public ESleekFontSize LabelFontSize\s*\{", "LabelFontSize")):
        if not re.search(_pat, _lpc):
            _knob_miss.append("列表." + _tag)
    if not _knob_miss:
        ok("两个视图各有 `IconSize` / `CellHeight`|`RowHeight` / `LabelFontSize` 三个外观旋钮")
    else:
        err("视图缺外观旋钮：" + ", ".join(_knob_miss))

    # ② **默认值必须等于原常量**（48 / 108 / 34 / 42 / Small）—— 改了默认 = 三套视图观感整体变
    _defaults = {}
    for (_src, _tag) in ((_gpc, "大格子"), (_lpc, "列表")):
        for (_name, _m) in (("IconSize", re.search(r"DEFAULT_ICON_SIZE = ([0-9.]+)f", _src)),
                            ("Height", re.search(r"DEFAULT_(?:CELL|ROW)_HEIGHT = ([0-9.]+)f", _src)),
                            ("Font", re.search(r"DEFAULT_LABEL_FONT = ESleekFontSize\.(\w+)", _src))):
            _defaults[_tag + "." + _name] = _m.group(1) if _m else None
    _want = {"大格子.IconSize": "48", "大格子.Height": "108", "大格子.Font": "Small",
             "列表.IconSize": "34", "列表.Height": "42", "列表.Font": "Small"}
    _bad_def = [k for k, v in _want.items() if _defaults.get(k) != v]
    if not _bad_def:
        ok("两个视图的默认值 = 原常量（48/108/Small · 34/42/Small）⟹ 现有观感一字不变")
    else:
        err("外观默认值被改掉：" + ", ".join("%s=%s" % (k, _defaults.get(k)) for k in _bad_def)
            + "（加旋钮 ≠ 改默认）")

    # ③ **派生只有一处口径** —— 几何只许读 `CellPitch()` / `RowPitch()`
    _gbody = fn_body(_gpc, "private float CellPitch(", "Grid.CellPitch")
    _lbody = fn_body(_lpc, "private float RowPitch(", "List.RowPitch")
    if _gbody is None or _lbody is None:
        err("14.9q 读不出 `CellPitch` / `RowPitch` —— 派生判据失效（别让它静默跳过）")
    else:
        if "DEFAULT_CELL_HEIGHT" in _gbody or "DEFAULT_ICON_SIZE" in _gbody \
                or "DEFAULT_ROW_HEIGHT" in _lbody or "DEFAULT_ICON_SIZE" in _lbody:
            err("派生函数里又读默认常量 —— 口径重复了（该读字段）")
        else:
            ok("格高/行高的派生只由「字段 + 内容所需」算出（不再掺默认常量）")
        # ★ 反向锁：把「字段声明 / 属性块 / 派生函数」全剔掉之后，几何里**不许**再出现裸字段。
        #   ⚠ 只数 `CellPitch()` 的出现次数是**太松**的（破坏一处还剩 4 处照样绿，阳性对照当场抓到）。
        def _strip_owner(s, field, prop, deriv):
            t = re.sub(r"private float " + field + r"[^;]*;", "", s)
            t = re.sub(r"public float " + prop + r"\s*\{[\s\S]*?\n        \}", "", t)
            t = re.sub(r"private float " + deriv + r"\(\)\s*\{[\s\S]*?\n        \}", "", t)
            return t
        _g_left = len(re.findall(r"(?<![\w.])_cellHeight\b",
                                 _strip_owner(_gpc, "_cellHeight", "CellHeight", "CellPitch")))
        _l_left = len(re.findall(r"(?<![\w.])_rowHeight\b",
                                 _strip_owner(_lpc, "_rowHeight", "RowHeight", "RowPitch")))
        if _g_left == 0 and _l_left == 0:
            ok("几何一律走派生，不再直接读裸字段 `_cellHeight` / `_rowHeight`（调大图标/字号不会溢出）")
        else:
            err("还有几何直接读裸字段（大格子 " + str(_g_left) + " 处 / 列表 " + str(_l_left) +
                " 处）—— 调大图标就会溢出")
        _gp_left = len(re.findall(r"(?<![\w.])CellPitch\(\)", _gpc))
        _lp_left = len(re.findall(r"(?<![\w.])RowPitch\(\)", _lpc))
        if _gp_left >= 3 and _lp_left >= 4:
            ok("派生入口被几何广泛使用（大格子 " + str(_gp_left) + " 处 / 列表 " + str(_lp_left) + " 处）")
        else:
            err("派生入口用得偏少（CellPitch=" + str(_gp_left) + " / RowPitch=" + str(_lp_left) +
                "）—— 布局可能又分了家")
        if re.search(r"private float CellPitch\(\)", _gpc) and re.search(r"private float RowPitch\(\)", _lpc):
            ok("两个视图都有单一的派生入口（`CellPitch` / `RowPitch`）")

    # ④ **夹取**：格高 ≥ 40 / 行高 ≥ 24 / 图标 ≥ 8
    if re.search(r"value < 40f \? 40f : value", _gpc):
        ok("格高夹到 ≥ 40（再小连一个图标都放不下）")
    else:
        err("格高没有下限夹取 —— 设小了内容会被挤没")
    if re.search(r"value < MIN_ROW_HEIGHT \? MIN_ROW_HEIGHT : value", _lpc) \
            and re.search(r"MIN_ROW_HEIGHT = 24f", _lpc):
        ok("行高夹到 ≥ 24（★★ 行高 = 行内标签高度，低于 24 一个字都不渲染）")
    else:
        err("行高没有夹到 24 —— 行内文字会整片消失，而且不报错（坑 #21）")
    if re.search(r"value < 8f \? 8f : value", _gpc) and re.search(r"value < 8f \? 8f : value", _lpc):
        ok("两个视图的图标边长都夹到 ≥ 8")
    else:
        err("图标边长的下限夹取不齐（两个视图都要）")

    # ⑤ `IdealTextHeight` 的表必须与引擎注释一致（Tiny 8 / Small 20 / Default 30 / Medium 50 / Large 70）
    _ish = fn_body(lv, "internal static float IdealTextHeight(", "Shared.IdealTextHeight")
    if _ish is None:
        err("缺 `IdealTextHeight` —— 字号一可配就没有行高依据（文字会被挤掉）")
    else:
        _pairs = re.findall(r"ESleekFontSize\.(\w+): return ([0-9.]+)f", _ish)
        _map = dict(_pairs)
        if _map.get("Tiny") == "8" and _map.get("Small") == "20" and _map.get("Medium") == "50" \
                and _map.get("Large") == "70":
            ok("`IdealTextHeight` 与引擎注释一致（Tiny 8 / Small 20 / Medium 50 / Large 70）")
        else:
            err("`IdealTextHeight` 与 `SleekFontSize.cs` 的注释值不符：" + str(_map))
        if "default: return 30f;" in _ish:
            ok("`Default`（及没注释值的 `Title`）按 30 处理 —— 宁可偏大也不把字挤掉")
        else:
            err("`IdealTextHeight` 缺 `default` 分支 —— 没列到的字号会拿到 0")

    # ---- 14.9r 原版内容 + 外挂界面并存（★ 26.10.3.67：`StorageUiPanels.KeepVanillaGrid`）----
    #   ★ 四条：① 公开开关是**静态属性**（第三方运行期也能开）；② cfg **默认必须是 false**
    #     （改默认 = 全体玩家排版变）；③ Host 三处都要**读开关**（网格可见性 / 高度记账 / 栈起点）；
    #     ④ **对账必须在算 y 之前**（否则并存的第一帧会拿上一次箱子的高度去定位）。
    _prov_c = strip_comments(src.get("CustomUI.Provider.cs", ""))
    if re.search(r"public static bool KeepVanillaGrid\s*\{[\s\S]{0,240}?get[\s\S]{0,240}?set", _prov_c):
        ok("公开面有 `StorageUiPanels.KeepVanillaGrid`（静态属性，第三方运行期也能开）")
    else:
        err("缺 `StorageUiPanels.KeepVanillaGrid` 公开属性 —— 并存能力第三方拿不到")
    _plug_c = strip_comments(src.get("CustomUI.cs", ""))
    if re.search(r'Config\.Bind\("General",\s*"KeepVanillaGrid",\s*false', _plug_c):
        ok("cfg `[General] KeepVanillaGrid` **默认 false**（不改默认 ⟹ 现有观感不变）")
    else:
        err("`KeepVanillaGrid` 的 cfg 默认不是 false —— 会改掉所有玩家的排版")
    _hc = strip_comments(host)
    if "grid.IsVisible = KeepVanillaGrid;" in _hc:
        ok("Host 读开关决定原版网格可见性（不再写死）")
    else:
        err("Host 没按开关决定网格可见性 —— 并存开关是死的")
    if re.search(r"=\s*VanillaGridBlockHeight\(\)\s*\+\s*occupied\s*;", _hc):
        ok("Host 的高度记账含着并存那一段（否则原版网格会被算成 0 高度）")
    else:
        err("高度记账没把并存段算进去 —— 并存时原版网格会占 0 高度")
    if re.search(r"float y = StorageRowTopY\(\)\s*\+\s*VanillaGridBlockHeight\(\);", _hc):
        ok("整栈起点 = 存储行顶 + 并存段（并存时自然下移到原版网格下面）")
    else:
        err("整栈起点没算并存段 —— 与原版网格重叠")
    # ④ 位置锁：对账（`ApplyVanillaOverride()`）必须**早于** `float y = StorageRowTopY()`
    _ls = fn_body(host, "private static void LayoutStack(", "Host.LayoutStack")
    if _ls is None:
        err("14.9r 读不出 `LayoutStack` —— 顺序判据失效（别让它静默跳过）")
    else:
        _i_ov = _ls.find("ApplyVanillaOverride()")
        _i_y = _ls.find("float y = StorageRowTopY()")
        if _i_ov >= 0 and _i_y >= 0 and _i_ov < _i_y:
            ok("顺序正确：**先与原版对账、再算 y**（并存的第一帧不会用上次箱子的高度）")
        else:
            err("`LayoutStack` 先算 y 再对账（ApplyVanillaOverride@%d / y@%d）—— 并存的第一帧会错位"
                % (_i_ov, _i_y))
    # cfg ↔ 运行时开关必须**双向同步**（只写 cfg 不同步 = 配置改了没反应）
    if re.search(r"keepVanilla\s*!=\s*lastKeepVanillaGrid", _plug_c) \
            and re.search(r"StorageUiHost\.KeepVanillaGrid\s*=\s*keepVanilla", _plug_c):
        ok("cfg 改动与运行时开关双向同步（改完立即生效，不用重开箱子）")
    else:
        err("cfg 与运行时开关没同步 —— 改了配置没反应（或只能重启）")

    # ★ 26.10.3.68：**顺序语义**必须写清参照物（用户实测把「之上」读成「排到原版网格上方」）。
    #   ① 整栈起点**不许按 region 顺序分叉** —— 并存时一律在原版网格下方（原版网格的 y 改不了）；
    #   ② 常量注释里必须点明参照物是「**箱内物品**」（不是原版网格）；
    #   ③ 接入自检状态行是**玩家看得到**的那份文案 ⟹ 它也必须带参照物（证据与结论同型，铁律 #7）。
    _ym = re.search(r"float y = ([^;]+);", _ls) if _ls else None
    if _ym is not None and "Order" not in _ym.group(1) \
            and "VanillaGridBlockHeight()" in _ym.group(1):
        ok("整栈起点**不按 region 顺序分叉**（并存时一律在原版网格下方 —— 原版网格的 y 我们改不了）")
    else:
        err("整栈起点按顺序分叉了（或没算并存段）—— 并存时要排到原版网格上方做不到，只会重叠")
    #   ⚠⚠ 这条要看**注释** ⟹ 必须用**原始源码**（`strip_comments` 会把注释删掉，
    #      第一条版本就是这么假红的）；接入自检源码不在 `src` 里 ⟹ 得按路径单独读。
    _prov_raw = src.get("CustomUI.Provider.cs", "")
    #   ⚠ 只取**紧邻这个常量的那段注释**：
    #     窗口太小 ⟹ 假红（那段注释比 400 字长）；
    #     窗口放宽到邻常量 ⟹ **太松**（`RegionOrderBelowStorage` 的注释也含「箱内物品」）。
    _pre = _prov_raw.split("RegionOrderAboveStorage")[0]
    _i_doc = _pre.rfind("/// <summary>")
    _above_zone = _pre[_i_doc:] if _i_doc >= 0 else _pre[-1500:]
    if re.search(r"RegionOrderAboveStorage = -100;", _prov_raw) \
            and "箱内物品" in _above_zone and "不是" in _above_zone:
        ok("`RegionOrderAboveStorage` 的注释点明了参照物是「**箱内物品**（我们自己的主面板）」")
    else:
        err("顺序常量的注释没说清参照物 —— 「之上」会被读成「排到原版网格上方」（用户实测误读过）")
    if not _CHAVE:
        skip("没扫到外部消费者 —— 跳过「状态行有没有写清参照物」")
    elif "（栈内：主面板之上）" in _CMERGED and "整栈在原版网格下" in _CMERGED:
        ok("外部消费者状态行写清了参照物（`栈内：主面板之上` + 并存时补 `整栈在原版网格下`）")
    else:
        err("外部消费者状态行只写「之上/之下」—— 玩家会把参照物读成原版网格（用户实测误读过）")

    # ---- 14.9s 就地取回（★ 26.10.3.69：`StorageUiPanels.TryTakeToBackpackFrom`）----
    #   ★★ 这是**唯一碰"防刷物品"红线**的公开面 ⟹ 四条缺一不可：
    #     ① **联机闸门**（客户端扣不了没打开的容器 ⟹ 必须拒绝，不许"尽力而为"）；
    #     ② **扣箱在入包之前**（因为回滚是精确的：源坐标与 rot 都还在手上）；
    #     ③ **失败必须精确还原**（放回原坐标 ⟹ 不存在"扣了没进"的丢物）；
    #     ④ **对账**（报实际搬走的数量给调用方核对守恒）。
    #   ★ 外加一条**反向锁**：不许为了它把 `ResolveInteractive` 的"只读降级"放开
    #     （那是现有保护，放开 = 所有区块面板默认都能跨容器搬 ⟹ 红线扩散）。
    _take = fn_body(_prov_raw, "public static bool TryTakeToBackpackFrom(Items source, byte x, byte y,",
                    "Provider.TryTakeToBackpackFrom")
    if _take is None:
        err("14.9s 读不出 `TryTakeToBackpackFrom` —— 红线判据失效（别让它静默跳过）")
    else:
        if re.search(r"if\s*\(!\s*Provider\.isServer\s*\)", _take):
            ok("★★ 就地取回有**联机闸门**（`!Provider.isServer` 就拒绝并给出原因）")
        else:
            err("★★ 就地取回没有联机闸门 —— 客户端扣不了没打开的容器，硬做会**凭空复制物品**")
        _i_rm = _take.find("removeItem(")
        _i_add = _take.find("tryAddItem(")
        if _i_rm >= 0 and _i_add >= 0 and _i_rm < _i_add:
            ok("扣箱（`removeItem`）在入包（`tryAddItem`）**之前** —— 回滚才精确（源坐标还在手上）")
        else:
            err("扣箱与入包的顺序不对（removeItem@%d / tryAddItem@%d）—— 回滚不再精确"
                % (_i_rm, _i_add))
        # ★ 认块：还原动作必须在 **tryAddItem 失败的那个分支里**（不是别处随便一处 addItem）。
        #   ⚠ 要在**整个函数体**里找：按 `find("tryAddItem(")` 切出来的 tail 会丢掉 `bag.` 前缀
        #     （第一版就是这么假红的）。
        if re.search(r"if\s*\(!\s*bag\.tryAddItem\([^)]*\)\s*\)\s*\{[^}]*addItem\(x,\s*y,\s*rot,\s*item\)",
                     _take, re.S):
            ok("入包失败时**放回原坐标**（`addItem(x, y, rot, item)` 就在那个分支里）⟹ 不会丢物")
        else:
            err("入包失败没有精确还原 —— 会出现「扣了没进」的丢物（红线）")
        if re.search(r"movedAmount\s*=\s*item\.amount\s*;", _take):
            ok("对账：报的是**实际搬走的数量**（`movedAmount = item.amount`，弹药按发）")
        else:
            err("没报实际搬走的数量 —— 调用方没法核对守恒（对账证据缺失）")
    # ★ 26.10.3.71：「存」那一半（`TryStoreToContainer`）—— 与「取」**同构、同一条红线**，
    #   所以四条证据一条都不能少（方向相反：源是背包页、目标是那个容器）。
    _store = fn_body(_prov_raw, "public static bool TryStoreToContainer(Items target, byte page, byte x, byte y,",
                     "Provider.TryStoreToContainer")
    if _store is None:
        err("14.9s 读不出 `TryStoreToContainer` —— 红线判据失效（别让它静默跳过）")
    else:
        if re.search(r"if\s*\(!\s*Provider\.isServer\s*\)", _store):
            ok("★★ 就地存入也有**联机闸门**（`!Provider.isServer` 就拒绝并给出原因）")
        else:
            err("★★ 就地存入没有联机闸门 —— 客户端写不了没打开的容器，硬做会**凭空复制物品**")
        _i_rm2 = _store.find("removeItem(")
        _i_add2 = _store.find("tryAddItem(")
        if _i_rm2 >= 0 and _i_add2 >= 0 and _i_rm2 < _i_add2:
            ok("就地存入也是「先扣来源、后入目标」—— 回滚精确（背包那格的坐标还在手上）")
        else:
            err("就地存入的扣/入顺序不对（removeItem@%d / tryAddItem@%d）—— 回滚不再精确"
                % (_i_rm2, _i_add2))
        # ★ 认块：还原必须在 **tryAddItem 失败的那个分支里**（把物品放回背包原格）
        if re.search(r"if\s*\(!\s*target\.tryAddItem\([^)]*\)\s*\)\s*\{[^}]*addItem\(x,\s*y,\s*rot,\s*item\)",
                     _store, re.S):
            ok("就地存入失败时**放回背包原格**（`addItem(x, y, rot, item)` 就在那个分支里）⟹ 不会丢物")
        else:
            err("就地存入失败没有精确还原 —— 会出现「扣了没进」的丢物（红线）")
        if re.search(r"movedAmount\s*=\s*item\.amount\s*;", _store):
            ok("就地存入也对账（报**实际搬走的数量**）")
        else:
            err("就地存入没报实际搬走的数量 —— 调用方没法核对守恒（对账证据缺失）")
        # 目标放不下要在**扣之前**就问（否则白扣一次再还原 —— 虽然正确但多一次写）
        if re.search(r"tryFindSpace\(", _store) and \
                _store.find("tryFindSpace(") < _i_rm2:
            ok("就地存入**先问目标放不放得下**、再扣来源（不白扣一次）")
        else:
            err("就地存入没先问目标空间 —— 会先扣再还原（多一次写，且失败路径更长）")

    _ri = fn_body(_gpc, "private bool ResolveInteractive(", "Grid.ResolveInteractive")
    if _ri is None:
        err("14.9s 读不出 `ResolveInteractive` —— 只读降级判据失效（别让它静默跳过）")
    elif "ReferenceEquals(open, source)" in _ri and "return same" in _ri:
        ok("反向锁：`ResolveInteractive` 仍**只对当前打开的容器**放行（没为就地取放开只读降级）")
    else:
        err("`ResolveInteractive` 的只读降级被动过 —— 放开 = 所有区块面板默认都能跨容器搬（红线扩散）")

    # ---- 14.9u 版本号格式（★ 26.10.12.3：`年两位.月.日.当天第几个版本`）----
    #   ★ 为什么要有这条：版本号散在**三个地方**（`CustomUI.cs` / 文档首行 / `发布说明` 的版本表），
    #     旧格式 `26.10.3.71` 的第三段既不像日期也不像序号，人一眼看不出对错 ⟹ 迟早对不齐。
    #   ⚠ 判据**不能锁"等于今天"** —— 版本号是**发布时**才改的，隔天没发版不该报红。
    #     所以锁的是：**4 段** + **前 3 段构成合法日期** + **不晚于今天** + **第 4 段 ≥ 1**。
    #   ⚠ 这里只查**本仓库自己**的版本号真源（`CustomUI.cs` 的 [BepInPlugin] 第三参数）。
    #     外部消费者各自的版本号是**别人家的事** —— 那一半判据整段删除（不在本仓库门禁内）。
    _ver_srcs = [("CustomUI.cs", src.get("CustomUI.cs", ""))]
    _today = datetime.date.today()
    for (_tag, _txt) in _ver_srcs:
        _m = re.search(r'BepInPlugin\(\s*"[^"]*"\s*,\s*"[^"]*"\s*,\s*"([0-9][0-9.]*)"', _txt)
        if not _m:
            err("读不出 " + _tag + " 的版本号 —— 版本格式判据失效（别让它静默跳过）")
            continue
        _v = _m.group(1)
        _parts = _v.split(".")
        _bad = None
        if len(_parts) != 4:
            _bad = "不是 4 段（年两位.月.日.当天第几个版本）"
        else:
            try:
                _d = datetime.date(2000 + int(_parts[0]), int(_parts[1]), int(_parts[2]))
            except ValueError:
                _bad = "前 3 段不是合法日期"
            else:
                if _d > _today:
                    _bad = "日期比今天还晚"
                elif int(_parts[3]) < 1:
                    _bad = "第 4 段（当天第几个版本）必须 ≥ 1"
        if _bad is None:
            ok(_tag + " 版本号格式正确：`" + _v + "`（年两位.月.日.当天第几个版本）")
        else:
            err(_tag + " 版本号 `" + _v + "` 不合规：" + _bad)

    # ---- 14.10 指纹纪律 ----
    for (sig, label) in [("private static int Fingerprint(", "Fingerprint"),
                         ("private static int MixItems(", "MixItems"),
                         ("private static int TextFingerprint(", "TextFingerprint")]:
        b = body_of(sig, label)
        if ".state" in b:
            err(label + " 指纹混进 state —— 下游物品 state 每帧变会导致每帧重建")
        elif "jar.x" in b or "jar.y" in b:
            err(label + " 指纹含坐标 —— 蓝图列表不按坐标排，只会造成无谓重建")
        else:
            ok(label + " 指纹不含 state / 坐标")

    # ---- 14.11 默认不启用 + 服务器安全 ----
    csall = src.get("CustomUI.cs", "")
    if re.search(r'Config\.Bind\("Blueprint",\s*"Enabled",\s*false', csall):
        ok("[Blueprint] Enabled 默认 false（不会悄悄接管玩家的箱子）")
    else:
        err("[Blueprint] Enabled 默认值不是 false —— 交接文档要求默认不启用")
    if re.search(r"internal static bool Enabled = false;", bv):
        ok("视图静态开关初值同为 false（配置没绑上时保持关闭）")
    else:
        err("视图静态开关初值不是 false")
    ch_body = body_of("public bool CanHandle(", "CanHandle")
    ws_body = body_of("public bool WantsRegion(", "WantsRegion")
    # 判据本体已挪到 Accepts（两种摆法共用），CanHandle / WantsRegion 只按 AsSection 分流
    acc_body = body_of("private bool Accepts(", "Accepts")
    if "Player.LocalPlayer" in acc_body and "if (!Enabled) return false;" in acc_body:
        ok("两种摆法共用的判据里有总开关与 LocalPlayer（专用服务器上不会去枚举蓝图）")
    else:
        err("Accepts 缺总开关或 LocalPlayer 判据")
    if "if (AsSection) return false;" in ch_body and "if (!AsSection) return false;" in ws_body:
        ok("两种摆法互斥：区块模式下 CanHandle 恒 false（不会既占存储位又出现在区块栈里）")
    else:
        err("CanHandle / WantsRegion 没按 AsSection 分流 → 同一块内容可能显示两遍")
    # 区块三件套必须齐（宿主靠它们排版：要不要出现 / 排在哪 / 挂哪块面板）
    ro_body = body_of("public int RegionOrder(", "RegionOrder")
    rp_body = body_of("public ISleekElement RegionPanel(", "RegionPanel")
    if "return SectionOrder;" in ro_body and "return panel;" in rp_body:
        ok("区块接口三件套齐全，且面板直接交出去由宿主排版（视图不再自己挂区块）")
    else:
        err("RegionOrder / RegionPanel 实现不对 —— 宿主拿不到面板或顺序")
    if "if (!AsSection) StorageUiHost.AttachAndPlacePanel(panel);" in tk_body:
        ok("只有抢槽位时视图自己挂；区块模式交给宿主（避免两套排版各算一份 y）")
    else:
        err("蓝图视图仍在自己决定区块位置 —— 会与宿主的区块栈互相叠")
    if "IsBoundTo(" in acc_body and "if (AsSection) return true;" in acc_body:
        ok("区块模式只要总开关开着就出现；抢槽位模式仍认 DefaultView / BindBox")
    else:
        err("接管条件判据变了 —— 区块模式不该再要求改 DefaultView")
    if '"blueprint"' in csall:
        ok("[General] DefaultView 支持 blueprint 取值")
    else:
        err("DefaultView 没接 blueprint")

    # ---- 14.12 三个内置视图优先级必须互不相同 ----
    adv = re.search(r"private static void ApplyDefaultView\(\)\s*\{(.*?)\n        \}",
                    csall, re.S)
    if adv is None:
        err("找不到 ApplyDefaultView 函数体")
    else:
        ab = adv.group(1)
        g = re.search(r"StorageGridView\.currentPriority\s*=\s*listIsDefault \? (\-?\d+) : (\-?\d+)", ab)
        l = re.search(r"StorageListView\.currentPriority\s*=\s*listIsDefault \? (\-?\d+) : (\-?\d+)", ab)
        b = re.search(r"StorageBlueprintView\.currentPriority\s*=\s*blueprintTakesSlot \? (\-?\d+) : (\-?\d+)", ab)
        if not (g and l and b):
            err("三视图优先级赋值没被认出来（写法改了要同步自检）——"
                "同优先级会让 Find 取谁取决于排序稳定性")
        elif "blueprintIsDefault && !StorageBlueprintView.AsSection" not in ab:
            err("blueprintTakesSlot 没排除区块模式 —— 蓝图会既挂在下方又抢存储区位置")
        else:
            same_branch = (g.group(1) == l.group(1)) or (g.group(2) == l.group(2))
            gl_vals = set([g.group(1), g.group(2), l.group(1), l.group(2)])
            if same_branch:
                err("某个配置分支下大格子与列表同优先级（会出现「有时列表有时大格子」）")
            elif b.group(1) in gl_vals or b.group(2) in gl_vals:
                err("蓝图视图的某个优先级值与 grid/list 撞了")
            else:
                ok("三种配置下三个视图优先级互不相同（grid/list " + g.group(1) + "/" + l.group(1) +
                   "，blueprint " + b.group(1) + "/" + b.group(2) + "）")

    # ---- 14.13 文档同步 ----
    rt = ""
    rp = os.path.join(HERE, "开发文档.md")
    if os.path.exists(rp):
        with io.open(rp, "r", encoding="utf-8") as f:
            rt = f.read()
    if "customui.blueprintview" in rt:
        ok("开发文档 写了蓝图视图")
    else:
        err("README 缺蓝图视图说明（customui.blueprintview）")
    if "IncludeActiveStorageContainer" in rt:
        ok("开发文档 写了「原版不从敞开的箱子里扣材料」这条口径")
    else:
        err("README 没写引擎消耗口径 —— 后来人会以为箱内材料齐就能合成")
    bat = os.path.join(HERE, "build.bat")
    if os.path.exists(bat):
        with io.open(bat, "r", encoding="utf-8", errors="ignore") as f:
            bt = f.read()
        # ★ 26.10.3.49：这条判据原来只盯 `CustomUI.BlueprintView.cs` 一个文件 ——
        #   于是**新增任何别的源文件都可能静默漏编**：`build.bat` 是 `_mkbuild.py` 生成的，
        #   忘了重跑就只是"少编一个文件"，`build.bat` 却照样报 `[OK]`（实测：
        #   新增 `CustomUI.ItemIcon.cs` 后编译成功、产物里根本没有那个类型）。
        #   ⟹ 判据改成**清单对账**：`build.bat` 里列的 `.cs` 必须与目录里的 `CustomUI*.cs`
        #   **完全一致**（既不缺、也不多余）。这才是"清单型常量一律扫目录"那条铁律的落地。
        _bat_cs = set(re.findall(r'"%~dp0(CustomUI[^"]*\.cs)"', bt))
        _dir_cs = set(FILES)
        _missing = sorted(_dir_cs - _bat_cs)
        _extra = sorted(_bat_cs - _dir_cs)
        if not _missing and not _extra:
            ok("build.bat 的源文件清单与目录完全一致（%d 个 .cs，一个不漏、一个不多）"
               % len(_dir_cs))
        else:
            err("build.bat 的源文件清单与目录不一致（跑 `python _mkbuild.py` 重新生成）："
                + ("缺 " + ", ".join(_missing) if _missing else "")
                + ("；" if _missing and _extra else "")
                + ("多 " + ", ".join(_extra) if _extra else ""))
    else:
        err("build.bat 不存在")

    # ★ 26.10.3.42（盘点 §2.11）：蓝图那 9 个旋钮是**配置镜像** ——
    #   唯一写入方 `ApplyDefaultView()` 会在**任一项蓝图配置变化时把它们全部无条件重写**
    #   ⟹ **不许改成 public 可写**（第三方写进去的值会被配置热生效**静默回退**，
    #   "改了当时有用、过一会儿自己变回去"是最难查的一类 bug）。
    #   对外只给"读"：`StorageBlueprintView.Current`（+ 公开结构体）。
    _bpv = src.get("CustomUI.BlueprintView.cs", "")
    _bpv_plain = strip_comments(_bpv) if _bpv else ""
    if not _bpv:
        err("找不到 CustomUI.BlueprintView.cs")
    else:
        _bp_fields = ("currentPriority", "AsSection", "SectionOrder", "SectionMaxListHeight",
                      "AsGrid", "Enabled", "IsDefaultView", "PollInterval", "AllowCraftFromBox")
        # ⚠ 反向断言：这 9 个里**一个都不许**变成 public 字段/属性。
        #   反向断言单独写（不混在正向里），且上面已 strip_comments —— 否则会被
        #   我自己写的那段"别改成 public 可写"的注释命中（判据永远走 else）。
        _leaked = []
        for _f in _bp_fields:
            if re.search(r"public\s+static\s+[\w<>\[\]\.]+\s+" + _f + r"\s*(=[^;]*)?[;{]", _bpv_plain):
                _leaked.append(_f)
        if _leaked:
            err("蓝图旋钮被改成公开可写了：" + "、".join(_leaked) +
                " —— 它们是配置镜像，第三方写进去会被配置热生效静默回退（§2.11 核过，别改回去）")
        else:
            ok("蓝图 9 个旋钮仍是 internal（配置镜像不许公开可写 —— 会被 ApplyDefaultView 覆盖）")

        # 只读快照必须**逐个字段转发到真源**（转发错了 = 报出来的数不是实际生效的那个）
        _ci = _bpv_plain.find("public static StorageBlueprintViewInfo Current")
        if _ci < 0:
            err("找不到 StorageBlueprintView.Current —— 第三方又没法问『蓝图配成什么样』了")
        else:
            _cbody = _bpv_plain[_ci:]
            _map = (("Enabled", "Enabled"), ("AsSection", "AsSection"),
                    ("RegionOrder", "SectionOrder"), ("SectionListHeight", "SectionMaxListHeight"),
                    ("GridLayout", "AsGrid"), ("IsDefaultView", "IsDefaultView"),
                    ("PollSeconds", "PollInterval"), ("CraftsFromBox", "AllowCraftFromBox"),
                    ("Priority", "currentPriority"))
            _bad = []
            for _a, _b in _map:
                if ("info." + _a + " = " + _b + ";") not in _cbody:
                    _bad.append(_a + "<-" + _b)
            if not _bad:
                ok("蓝图快照 9 个字段逐个转发到真源（不是另抄一份值 —— 抄一份就会漂）")
            else:
                err("蓝图快照这些字段没转发到真源：" + "、".join(_bad))
        if "public struct StorageBlueprintViewInfo" in _bpv_plain:
            ok("公开结构体 StorageBlueprintViewInfo 存在（快照一次取齐，不是 9 个 getter 各读一次）")
        else:
            err("没有公开结构体 StorageBlueprintViewInfo —— 快照类型拿不到，等于没公开")

        # 外部消费者要真的读它（同坑 #45/#49：公开面没人从外部用 → 等于没公开）。
        # ⚠ 消费者缺失时**跳过**（外部使用者没有消费者目录，不该因此报 FAIL）。
        if not _CHAVE:
            skip("没扫到外部消费者 —— 跳过「公开面有没有人从外部钉住」")
        elif "StorageBlueprintView.Current" in _CCODE:
            ok("外部消费者在运行期读蓝图配置快照（新公开面有人从外部钉住）")
        else:
            err("没有外部消费者读 StorageBlueprintView.Current —— 新公开面没人从外部验证")

# ---------------------- 15. 常驻面板（只藏不拆）与接管回退链
out.append("")
out.append("15) 常驻面板 / 接管回退链")

# 背景（实测反馈）：关掉界面时若 RemoveChild，Glazier 会 GameObject.Destroy 整棵子树
#   → 行池与已渲染的图标贴图全没 → 下次打开全部重新排队加载。
#   而图标尺寸是统一的、且带 state，不满足原版 ItemTool.getIcon 的缓存判据
#   （state.Length==0 && x==size_x*50 && y==size_y*50）→ 进不了 iconCache。
#   ⇒ 正确做法是「关掉只 SetActive(false)，控件与贴图跨次打开常驻复用」。

_VIEWS = [("列表视图", "CustomUI.ListView.cs"), ("大格子视图", "CustomUI.GridView.cs"),
          ("蓝图视图", "CustomUI.BlueprintView.cs")]


def view_body(text, sig, label):
    return fn_body(text, sig, label)


for (vname, vfile) in _VIEWS:
    vt = src.get(vfile, "")
    if not vt:
        err("源文件缺失：" + vfile)
        continue

    cb = view_body(vt, "public void Close(", vname + ".Close")
    if cb is None:
        continue
    cb_code = strip_comments(cb)   # 反向判据必须先剔注释（注释里就在写「别用 RemoveChild」）
    # 两种合法写法：① 自己 `panel.IsVisible = false`（蓝图视图）；
    # ② 委托给公开组件 `grid.Hide()` / `list.Hide()`（大格子视图 B1 之后、
    #    列表视图 T9 之后）—— 后者由第 23 节/第 9.18 节去查组件本体。
    delegates_hide = ("grid.Hide()" in cb_code) or ("list.Hide()" in cb_code)
    if delegates_hide or ("panel.IsVisible = false" in cb_code and "RemoveChild" not in cb_code):
        ok(vname + " Close 只藏不拆（RemoveChild = Destroy，会把图标一起销毁）" +
           ("—— 委托给公开组件 Hide" if delegates_hide else ""))
    else:
        err(vname + " Close 没做到「只藏不拆」—— 重开箱子会全量重载图标")

    # 数据池可以清（每次打开重算），**控件池**绝不能清 —— 清了就没有缓存可言。
    ctrl_cleared = re.search(r"\b(rows|cells)\.Clear\(\)", cb_code)
    if ctrl_cleared:
        err(vname + " Close 里清了控件池 " + ctrl_cleared.group(0) +
            " —— 常驻复用等于白做，图标仍会全量重载")
    else:
        ok(vname + " Close 保留控件池（只清数据）")

    ob = view_body(vt, "public bool Open(", vname + ".Open")
    if ob is None:
        continue
    reuse_direct = "if (!IsParkUsable()) DiscardPark();" in ob and "if (panel == null) Build();" in ob
    reuse_delegated = (("grid.IsUsableAt(" in ob and "grid.Discard()" in ob and "grid.Build()" in ob)
                       or ("list.IsUsableAt(" in ob and "list.Discard()" in ob and "list.Build()" in ob))
    if reuse_direct or reuse_delegated:
        ok(vname + " Open 先验常驻有效性、可用就直接复用（不重 Build）")
    else:
        err(vname + " Open 没走「验常驻 → 复用 / 否则丢弃重建」，每次都会重建面板")
    # ⚠ 判据必须落在 **catch 块**里 —— Open 的正常路径本来就会调丢弃（常驻失效时），
    #   只搜「有没有丢弃调用」是条永远为真的废断言（同「判据窗口要够」那条坑）。
    # ⚠ 分组的 `;` 只能出现一次：写成 (A;|B); 会让 A 分支要求「A;;」，永远不匹配（踩过）。
    if re.search(r"catch\s*\(Exception[\s\S]{0,220}?(?:DiscardPark\(\)|grid\.Discard\(\)|list\.Discard\(\));"
                 r"[\s\S]{0,60}?return false;", ob):
        ok(vname + " Open 的 catch 里自己拆半成品（宿主不会再调 Close）")
    else:
        err(vname + " Open 失败路径没拆半成品 —— 会留下半初始化面板，且回退链上它可能被当可用常驻复用")

    # ⚠ 委托给组件的视图（大格子）已经没有这个方法了 —— 这里必须「静默查找」，
    #   因为 fn_body 找不到时会自己 err() 一条，那对本视图是假 FAIL。
    pb = None
    _pm = re.search(r"private bool IsParkUsable\([^)]*\)\s*\{(.*?)\n        \}", vt, re.S)
    if _pm is not None:
        pb = _pm.group(1)
    if pb is not None:
        if "CurrentAttachSpot()" in pb and "panel.Parent" in pb:
            ok(vname + " IsParkUsable 比对「父节点 == 当前挂载点」"
               "（只判 Parent != null 不够：旧画布销毁后 Parent 仍非空）")
        else:
            err(vname + " IsParkUsable 判据不足 —— 切关/换服后会复用已销毁的面板")
    elif reuse_delegated:
        ok(vname + " 的常驻判活委托给 StorageGridPanel.IsUsableAt（第 23 节查组件本体）")

# 15.x 宿主侧
host_code = strip_comments(host)
if "RemoveChild" not in host_code:
    ok("宿主全程不销毁视图面板（DetachPanel 与两个挂载点都改成隐藏）")
else:
    err("宿主里又出现 RemoveChild —— 那会 Destroy 掉某个视图的常驻面板（含图标）")

dp = view_body(host, "internal static void DetachPanel(", "Host.DetachPanel")
if dp is not None:
    dp_code = strip_comments(dp)
    if "panel.IsVisible = false" in dp_code and "panel = null" not in dp_code:
        ok("DetachPanel 只隐藏、且故意不清 panel 引用"
           "（同视图下次打开时认出同一块面板，不重挂、只 SetActive）")
    else:
        err("DetachPanel 写法不对 —— 要么销毁了面板，要么清了引用导致下次强制重挂")

if view_body(host, "internal static ISleekElement CurrentAttachSpot(", "CurrentAttachSpot") is not None:
    ok("宿主提供 CurrentAttachSpot（视图自检常驻是否还活着）")

# 15.y 接管回退链：Open 失败必须换下一个候选，不能直接放弃回原版
if "StorageUiRegistry.FindCandidates(ctx)" in host and "candidates.Count" in host:
    ok("TryOpen 拿整条候选链依次试（某视图 Open 失败还能退回另一个视图）")
else:
    err("TryOpen 没走候选链 —— 首选视图 Open 一失败就直接回原版，玩家看不到兜底视图")
if re.search(r"if \(!ok\) return false;", host):
    err("TryOpen 里仍有「Open 失败就直接 return」的写法 —— 回退链被截断")
else:
    ok("没有「第一个候选失败就放弃」的短路")
if re.search(r"打开失败，回退到", host):
    ok("回退会记一条 warn（否则静默换视图，排查时看不出发生过失败）")
else:
    err("回退没记日志")

fc = view_body(prov, "public static List<IStorageUiProvider> FindCandidates(", "FindCandidates")
if fc is not None:
    if "result.Contains(p)" in fc:
        ok("候选链去重（被 BindBox 命中的那个不会被重复试一次 Open）")
    else:
        err("候选链没去重 —— 绑定的 Provider 会被连续 Open 两次")
    if "Accepts(" in fc:
        ok("CanHandle 走 Accepts（异常隔离，单个 Provider 出问题不拖垮整条链）")
    else:
        err("CanHandle 没走异常隔离")
if re.search(r"List<IStorageUiProvider> candidates = FindCandidates\(ctx\);\s*"
             r"return candidates\.Count > 0 \? candidates\[0\] : null;", prov):
    ok("Find 委托给 FindCandidates（绑定优先 + 优先级降序只有一份实现，不会两边跑偏）")
else:
    err("Find 没复用 FindCandidates —— 两套选序逻辑会随改动不一致")

# ---------------- 16. 扣箱内材料合成（写数据的路径，判据全部回源码）
out.append("")
out.append("16) 扣箱内材料合成（CustomUI.Crafting.cs）")

cr = src.get("CustomUI.Crafting.cs", "")
if not cr:
    err("源文件缺失：CustomUI.Crafting.cs")
else:
    crc = strip_comments(cr)

    def cr_body(sig, label):
        return fn_body(cr, sig, label)

    # ---- 16.0 依赖的引擎事实逐个回 SDK 核对（写数据的路径，错了就是丢玩家东西） ----
    inv = sdk_read("Player/InventorySearch.cs")
    if inv is None:
        err("SDK 文件缺失：Player/InventorySearch.cs")
    else:
        if re.search(r"public void Delete\(Player player\)", inv):
            ok("PlayerInventorySearchResultV2.Delete(Player) 是 public（扣整件走它）")
        else:
            err("Delete(Player) 不再是 public —— 扣料通道要重新设计")
        if re.search(r"public uint DeleteAmount\(Player player, uint desiredAmount", inv):
            ok("DeleteAmount(Player, uint, bool) 是 public（扣部分堆叠走它）")
        else:
            err("DeleteAmount 签名变了 —— 部分扣料会编译不过或语义错位")
        if "player.crafting.removeItem(_jarOwner.page, _jar)" in inv:
            ok("Delete/DeleteAmount 内部走 crafting.removeItem（会剥附件、触发同步与存档）")
        else:
            err("Delete 不再走 crafting.removeItem —— 我们借的那条通道行为变了")

    items_sdk = sdk_read("Inventory/Items.cs")
    if items_sdk is not None:
        if re.search(r"public bool tryAddItem\(Item item\)", items_sdk):
            ok("Items.tryAddItem(Item) 是 public（产物先塞回箱子）")
        else:
            err("Items.tryAddItem(Item) 不是 public —— 产物落箱的写法要改")
        if re.search(r"public void addItem\(byte x, byte y, byte rot, Item item\)", items_sdk):
            ok("Items.addItem(x,y,rot,item) 是 public（回滚原样放回）")
        else:
            err("Items.addItem 签名变了 —— 回滚可能放不回去")

    prov = sdk_read("Provider/Provider.cs")
    if prov is not None and re.search(r"public static bool isServer", prov):
        ok("Provider.isServer 是 public（「能不能改箱子」的唯一权威判据）")
    else:
        err("Provider.isServer 取不到 —— 联机下会误改镜像数据")

    pc2 = sdk_read("Player/PlayerCrafting.cs")
    if pc2 is not None:
        if re.search(r"IncludeEquipmentSlots\s*=\s*!inputItemConfig\.ShouldConsume", pc2):
            ok("引擎口径未变：装备格算不算数跟着 ShouldConsume 走")
        else:
            err("引擎口径可能变了：搜不到 IncludeEquipmentSlots = !ShouldConsume")
        # 反向：原版合成链**不给技能经验**。我们照它做，所以自己也不给。
        #   这条断言是为了防止后来人「好心补经验」造出原版没有的行为。
        xp_hits = re.findall(r"recieveExpForUsingSkill|giveExperience|\.experience\s*[+\-]?=", pc2)
        if xp_hits:
            err("原版合成链里出现了发经验的调用（" + ", ".join(set(xp_hits)) +
                "）→ 我们不给经验就不再是与原版一致，需要重新对齐")
        else:
            ok("原版合成确实不发技能经验（全文无发经验调用）→ 本插件不发经验=与原版一致")

    # ---- 16.1 服务端闸门必须在任何写动作之前 ----
    gates = cr_body("internal static bool CheckGates(", "CheckGates")
    if gates is not None:
        pos = gates.find("if (!Provider.isServer)")
        if pos >= 0:
            ok("CheckGates 第一道就是 Provider.isServer（联机客户端一律拒绝）")
        else:
            err("CheckGates 里没有 Provider.isServer 闸门 —— 联机下会去改一份镜像数据")
        if pos >= 0 and "Delete" not in gates[pos:]:
            ok("CheckGates 只做判据、不扣料（写动作集中在 TryCraft）")
        else:
            err("CheckGates 里出现 Delete —— 闸门函数不该改数据")
    try_body = cr_body("internal static CraftOutcome TryCraft(", "TryCraft")
    if try_body is not None:
        if "CheckGates(" in try_body and "BuildPlan(" in try_body:
            ok("TryCraft 先过闸门、再规划")
        else:
            err("TryCraft 缺闸门或规划步骤")
        if try_body.find("BuildPlan(") < try_body.find(".Delete("):
            ok("两段式：全部材料规划齐了才开始扣（不会扣一半发现不够）")
        else:
            err("不是两段式 —— 可能吃了材料又给不出产物")
        if "Rollback(" in try_body:
            ok("扣料中途失败会 Rollback")
        else:
            err("TryCraft 里没有回滚路径")

    # ---- 16.1b 界面过期防护：面板记的容器必须仍是当前打开的那个 ----
    # （原版 closeStorage 会把 items[STORAGE] 换成全新 Items → 比对实例就能发现面板已过期）
    if "StaleContext" in cr and "ReferenceEquals(pages[PlayerInventory.STORAGE], c.Container)" in crc:
        ok("有「面板已不对应当前打开的箱子」这道防护（否则会扣到别的容器 / 已关的箱子）")
    else:
        err("缺界面过期防护 —— 面板残留时按合成会去扣别的容器的材料")
    # 前提核对：宿主与补丁都必须把 items[STORAGE] 当 container 传进来，否则上面那条永远失败
    if "items[PlayerInventory.STORAGE]" in host and "items[PlayerInventory.STORAGE]" in \
            src.get("CustomUI.Patches.cs", ""):
        ok("两个接管入口的 container 都是 items[STORAGE]（界面过期判据的前提成立）")
    else:
        err("接管入口传的不再是 items[STORAGE] —— CheckGates 里的实例比对会失真")

    # ---- 16.2 一律走引擎通道，不自造删除/搜索 ----
    if ".Delete(player)" in crc and "DeleteAmount(player," in crc:
        ok("扣料走引擎自己的 Delete / DeleteAmount")
    else:
        err("没走引擎删除通道 —— 手搓删除会漏掉附件剥离、同步与存档")
    # ⚠ 判据要区分两种「直接改 Items」：
    #   · 材料 → 必须走引擎的 Delete / DeleteAmount（会剥附件、同步、落盘）
    #   · 目标（装填/修理）→ 原版自己就是调 sendUpdateAmount / sendUpdateQuality /
    #     sendUpdateInvState（PlayerCrafting.cs:838/907/916），所以这三句是**合法通道**，
    #     但只允许出现在 ApplyFill / ApplyRepair 里 —— 别的地方冒出来就是绕过材料删除通道。
    bad_writes = re.findall(r"inventory\.removeItem\(|items\[PlayerInventory\.STORAGE\]|"
                            r"sendDragItem\(|sendDropItem\(", crc)
    if bad_writes:
        err("出现自造/绕道的写入调用：" + ", ".join(sorted(set(bad_writes))) +
            "（材料必须经 Delete/DeleteAmount，否则不会同步与存档）")
    else:
        ok("没有绕道自造删除（不直接 removeItem、不发 drag/drop 请求）")

    crc_no_target = crc
    for _sig in ("private static void ApplyFill(", "private static void ApplyRepair("):
        _m = re.search(re.escape(_sig) + r"[^)]*\)\s*\{.*?\n        \}", crc, re.S)
        if _m:
            crc_no_target = crc_no_target.replace(_m.group(0), "")
    stray = re.findall(r"sendUpdateAmount\(|sendUpdateQuality\(|sendUpdateInvState\(", crc_no_target)
    if stray:
        err("目标改造的三句引擎通道跑出了 ApplyFill/ApplyRepair：" +
            ", ".join(sorted(set(stray))) + " —— 材料那部分不许直接改数量/耐久")
    else:
        ok("sendUpdateAmount/Quality/InvState 只用于目标改造（与原版同一用法），未泄漏到扣料路径")
    if "SearchContents(ref" in crc:
        ok("材料搜索走引擎 SearchContents（判据与原版同一份，不自己抄）")
    else:
        err("没用引擎搜索 —— 自己抄筛选条件迟早与原版漂移")

    # ⚠ 有两个 SearchOne 重载（4 参数的是转发的），判据必须抓**带 ignore 的那个实现体**，
    #   否则抓到转发函数体会以为判据缺失（本项目栽过一模一样的"抓错函数体"）。
    _so = re.search(r"private static void SearchOne\(BlueprintSupply sup, Items container, Player player,\s*"
                    r"bool fromBox, ItemJar ignore\)\s*\{(.*?)\n        \}", cr, re.S)
    if _so is None:
        err("找不到 SearchOne 的实现体（签名改了要同步这条判据）")
        so = ""
    else:
        so = _so.group(1)
    if so is not None:
        if "IncludeEquipmentSlots = !sup.ShouldConsume" in so:
            ok("背包那轮的装备格判据跟着 ShouldConsume（与原版一致）")
        else:
            err("装备格判据写死 —— 会与界面显示口径漂移（显示够、点了不够）")
        if "IncludeActiveStorageContainer = true" in so:
            err("SearchOne 里放了 IncludeActiveStorageContainer = true —— "
                "箱子那轮是直接搜 container，两条路都开会**重复计数**")
        else:
            ok("两轮搜索都排除「按页号带箱子」的路线（箱内只数一次，不重复计）")

    # ---- 16.3 回滚必须「回到原来的那个 Items」并区分整删/部分扣 ----
    rb = cr_body("private static void Rollback(", "Rollback")
    if rb is not None:
        if "u.owner" in rb and "c.Container" not in rb:
            ok("回滚写回 jar 原来的 owner（材料可能一半来自箱子、一半来自背包）")
        else:
            err("回滚把背包里扣掉的东西放回箱子 = 玩家物品凭空换位（必须按 u.owner 回原处）")
        if "new Item(u.id, u.amount, u.quality, u.state)" in rb:
            ok("整件删除的回滚带 id/数量/耐久/state 原样放回（附件、弹药都在 state 里）")
        else:
            err("回滚没有还原 quality/state —— 玩家的枪会被洗成无附件")
        if "getIndex(u.x, u.y)" in rb and "255" in rb and "updateAmount(" in rb:
            ok("部分扣减的回滚走 updateAmount 还原，不 addItem（同坐标叠两个 jar 就是复制物品）")
        else:
            err("回滚没区分「格子还在（只减了数量）」与「整件被删」—— 前者再 addItem 会叠出重复物品")
        if "undo.Count - 1" in rb and "i--" in rb:
            ok("回滚逆序执行（先放的先取走，位置才不会错）")
        else:
            err("回滚顺序变了，检查是否会互相占位")

    # ---- 16.3b 快照必须记 owner，且必须在扣之前取 ----
    snap = try_body if try_body is not None else ""
    if "u.owner = t.JarOwner;" in snap:
        ok("扣料前的快照记下 jar 所属的 Items（回滚才知道该放回箱子还是背包）")
    else:
        err("快照没记 JarOwner —— 回滚会放错容器")
    if snap.find("undo.Add(u)") < snap.find(".Delete(player)") or snap.find("undo.Add(u)") < snap.find("take.target.Delete"):
        ok("快照在扣料循环之前完成（否则删完后 jar 已失效，回滚无从依据）")
    else:
        err("快照与扣料的先后顺序可疑 —— 必须在扣之前拿到原样数据")

    # ---- 16.3c 合成完必须通知原版刷新（我们绕过了 ReceiveCraft） ----
    fin = cr_body("private static void Finish(", "Finish")
    if fin is not None:
        if "ServerRefreshOwnerCrafting()" in fin:
            ok("补了 ServerRefreshOwnerCrafting（SDK 注释：Requested for plugin use）"
               "—— 否则原版合成菜单会一直显示旧的可合成状态")
        else:
            err("没通知原版刷新合成菜单 —— 自造合成通道的必做收尾")
        if "ApplyConditions" in fin and "GrantRewards" in fin:
            ok("收尾调了 ApplyConditions / GrantRewards（与原版同一对方法）")
        else:
            err("收尾缺条件/奖励处理 —— 以后放开带任务配方时会漏")

    # ---- 16.4 工具类不扣、子集闸门齐全 ----
    build = cr_body("private static bool BuildPlan(", "BuildPlan")
    if build is not None:
        if re.search(r"if \(!sup\.ShouldConsume\)[\s\S]{0,300}?continue;", build):
            ok("工具类材料（ShouldConsume=false）不进扣料计划 —— 只要求在场，与原版一致")
        else:
            err("工具类材料被纳入了扣料计划 —— 会把玩家的锤子扣掉（原版不扣）")
        # ⚠ 装备格的拦截已挪进 AddTakes（两条扣料路径共用一份），判据要跟着挪。
        at = fn_body(cr, "private static void AddTakes(", "AddTakes")
        if at is not None and "r.Page < PlayerInventory.SLOTS" in at:
            ok("装备格写死进不了扣料计划（`Items.getIndex` 对 page<SLOTS 恒返回 0，"
               "否则回滚会改到 0 号格；顺带也不会扣掉玩家正穿着的东西）")
        else:
            err("没挡装备格 —— 一旦有装备页的 jar 进了计划，回滚会写到错误的格子上")

    plain = cr_body("private static bool IsPlainRecipe(", "IsPlainRecipe")
    if plain is not None and "OperationOf(bp) != CraftOp.None" in plain and "UnsupportedReason(bp) == null" in plain:
        ok("支持子集判据委托给 OperationOf + UnsupportedReason（界面与执行共用，不会两处说法分家）")
    else:
        err("IsPlainRecipe 写法变了 —— 界面拿的 UnsupportedReason 与执行的判据可能已经不一致")

    # ---- 16.9b 装填 / 修理（用户实测要求支持，第 43 条） ----
    oor = cr_body("internal static CraftOp OperationOf(", "OperationOf")
    if oor is not None:
        if "EBlueprintOperation.FillTargetItem" in oor and "EBlueprintOperation.RepairTargetItem" in oor \
                and "bp.TargetItem == null" in oor:
            ok("装填/修理已纳入支持（且要求有 TargetItem 才算支持，缺定义一律拒绝）")
        else:
            err("OperationOf 没把 FillTargetItem/RepairTargetItem 纳入支持 —— 用户报的第 43 条没修")
        if "default: return CraftOp.None" in oor:
            ok("未来新增的操作类型默认**不支持**（宁可少做，不要猜语义）")
        else:
            err("OperationOf 的 default 分支不再是拒绝 —— 以后原版加新操作会被当成普通合成")

    ur = cr_body("internal static string UnsupportedReason(", "UnsupportedReason")
    if ur is not None:
        for (kw, why) in [("transferState", "state 转进产物（怕造出耐久不对的物品）"),
                          ("questConditions", "带任务条件"),
                          ("questRewards", "带任务奖励")]:
            if kw in ur:
                ok("仍然拒绝 " + why + "（原因写在 UnsupportedReason 里，界面直接拿去显示）")
            else:
                err("UnsupportedReason 不再检查 " + kw + " —— 悄悄放开了一类没验证过的语义")

    af = cr_body("private static void ApplyFill(", "ApplyFill")
    if af is not None:
        if "sendUpdateAmount(" in af and "refillAmount" in af:
            ok("装填用原版那句 sendUpdateAmount 改目标数量（不是删了再造）")
        else:
            err("装填没有用 sendUpdateAmount 改目标 —— 删了重造会丢耐久/附件/弹药以外的 state")
        if "Delete" not in af:
            ok("装填不删目标（原版注释：Don't delete the refilled item）")
        else:
            err("装填路径里出现 Delete —— 会把玩家那个弹匣吃掉")

    pt = cr_body("private static bool PickTarget(", "PickTarget")
    if pt is not None:
        if "p.AssetRef = tgt.ItemRef" in pt and "IncludeActiveStorageContainer = !tgt.ShouldConsume" in pt:
            ok("目标搜索用 TargetItem 自己的判据（ShouldConsume=false → 箱子与装备格都算，同原版）")
        else:
            err("目标搜索判据与原版不一致 —— 会选不到目标或选错目标")
        if "ShouldExcludeFullAmount" in pt and "ShouldIncludeMaxQuality" in pt:
            ok("满弹匣 / 全新物品被引擎自己的筛选排除在外（不会白扣材料）")
        else:
            err("目标筛选没带上 ShouldExcludeFullAmount / ShouldIncludeMaxQuality")
        if "MaxAmount" in pt and "Math.Min(missing, available)" in pt:
            ok("refillAmount = min(目标缺多少, 第一路能给多少)（原版 PlayerCrafting:831-836 同式）")
        else:
            err("refillAmount 算法与原版不一致 —— 会灌爆或灌不满")
        if "SortByPrioritization(targetHits, tgt.Prioritization)" in pt:
            ok("目标按该 TargetItem 自己的 Prioritization 选（引擎比较器，与原版同一份）")
        else:
            err("没按原版 Prioritization 选目标（多个候选时会选错灌/修哪一件）")

    if "p.ItemToIgnore = ignore" in crc and "ignore = targetPick.Jar" in crc:
        ok("材料搜索排除了已选中的目标（否则半满弹匣会被当成自己的弹药吃掉）")
    else:
        err("没把目标从材料搜索里排除 —— 会把要装填的那个弹匣自己扣掉")

    bmsg = cr_body("private static string BlockMessage(", "BlockMessage")
    if bmsg is not None:
        blocks = re.findall(r"^\s+(\w+),?\s*(?://.*)?$", cr[cr.find("internal enum CraftBlock"):cr.find("/// <summary>一次合成的执行结果")], re.M)
        blocks = [b for b in blocks if b not in ("None",)]
        noMsg = [b for b in blocks if ("CraftBlock." + b) not in bmsg]
        if not noMsg:
            ok("每个拒绝原因都有一句话（共 " + str(len(blocks)) + " 个 CraftBlock 全覆盖）")
        else:
            err("这些拒绝原因没有对应文案，会退回含糊的默认句：" + ", ".join(noMsg))

    # ---- 16.5 产物落点顺序 ----
    grant = cr_body("private static int GrantProducts(", "GrantProducts")
    if grant is not None:
        if grant.find("tryAddItem(") >= 0 and grant.find("forceAddItem(") > grant.find("tryAddItem("):
            ok("产物顺序 = 箱子 tryAddItem → 背包 forceAddItem（背包塞不下原版自己掉地上）")
        else:
            err("产物落点顺序不对（用户要求：箱子 → 背包 → 地上）")

    # ---- 16.6 不自造经验/音效 ----
    xp_mine = re.findall(r"recieveExpForUsingSkill|giveExperience|addExperience", crc)
    if xp_mine:
        err("自造了发经验调用 " + ", ".join(sorted(set(xp_mine))) +
            " —— 原版合成不发经验，加了就是原版没有的行为")
    else:
        ok("没有自造发经验（与原版一致：原版只 sendStat + 特效）")
    if "sendStat(EPlayerStat.FOUND_CRAFTS)" in crc and "FindBuildEffectAsset" in crc:
        ok("收尾借原版：sendStat(FOUND_CRAFTS) + FindBuildEffectAsset 特效")
    else:
        err("收尾没借原版那几项 —— 与原版合成表现不一致，玩家会觉得少了反馈")

    # ---- 16.7 IsSupported 必须是廉价判据（它在重建路径上对每个候选调用） ----
    sup_body = cr_body("internal static bool IsSupported(", "IsSupported")
    if sup_body is not None:
        if "SearchContents" in sup_body or "CountBoth" in sup_body:
            err("IsSupported 里做了搜索 —— 它对每个候选都会调，重建会慢一个数量级")
        else:
            ok("IsSupported 只查判据不搜材料（可以安全放在重建路径上）")

    # ---- 16.8 界面侧：按钮与回调 ----
    bv2 = src.get("CustomUI.BlueprintView.cs", "")
    f2 = view_body(bv2, "private void FillRowText(", "FillRowText")
    if f2 is not None:
        if "r.Bp = e.blueprint;" in f2:
            ok("每次填文字都把配方写回 r.Bp（行池复用后回调才不会点到旧配方）")
        else:
            err("FillRowText 没更新 r.Bp —— 行被复用后回调会拿旧配方扣料！")
        if "r.Craft.IsVisible = e.craftable" in f2:
            ok("按钮只在可点判据通过时出现")
        else:
            err("按钮显隐没跟 craftable 走")
    oc = view_body(bv2, "private void OnCraftClicked(", "OnCraftClicked")
    if oc is not None:
        if "TryCraft(ctx, r.Bp, player)" in oc:
            ok("点击时现读 r.Bp（不闭包捕获条目/下标）")
        else:
            err("点击回调没有现读 r.Bp —— 行池复用会合成错的配方")
        if "lastAction" in oc and "MarkDirty()" in oc:
            ok("结果写回头部 + 立刻作废重建（扣完料数字要跟着变）")
        else:
            err("点击后没有反馈或没有重算 —— 玩家不知道有没有合成成功")
    # 「usable」和「按钮能不能出现」必须同源，分成两处写就会漂移（用户实测的三条现象
    # 正是分家导致的）。但**同源不等于同一个变量**：
    #   ① 可用量口径 boxCounts = 意愿开关 AND 可行性 AND 这一页算不算箱子
    #   ② 按钮闸门 craftable = 功能谓词 featureOn（意愿 AND 可行性）AND 门槛 AND 材料齐
    #      ⚠ craftable 里**不能有"这一页算不算箱"** —— 借道页不算箱，但"只用身上材料合成"
    #        这条路仍然成立；把它当按钮闸门会让 RelayWithSections 变成骗人的声明。
    _bs2 = re.search(r"bool boxCounts = ([^;]*);", bv2)
    _fo2 = re.search(r"bool featureOn = ([^;]*);", bv2)
    _ck2 = re.search(r"bool craftable = ([^;]*);", bv2)
    if _bs2 is not None and "AllowCraftFromBox" in _bs2.group(1) \
            and "IsSupported(bp)" in _bs2.group(1) and "BoxCounts(ctx)" in _bs2.group(1) \
            and _fo2 is not None and "AllowCraftFromBox" in _fo2.group(1) \
            and "IsSupported(bp)" in _fo2.group(1) \
            and _ck2 is not None and "featureOn" in _ck2.group(1) \
            and "BoxCounts" not in _ck2.group(1) and "gateOk" in _ck2.group(1):
        ok("可用量口径含「页面算不算箱」，按钮闸门只含功能谓词+门槛+材料（两件事分开且各自同源）")
    else:
        err("可点判据/可用量口径的拼装变了 —— 要么与 usable 分家，"
            "要么把「页面算不算箱」当成了按钮闸门（会误禁只用身上材料的合成）")
    if re.search(r'Config\.Bind\("Blueprint",\s*"ConsumeFromBox",\s*true', csall):
        ok("[Blueprint] ConsumeFromBox 默认开（用户要的行为），可一键回到纯只读")
    else:
        err("[Blueprint] ConsumeFromBox 默认值不是 true 或没接上配置")

# ------------- 17. 插件管理器标签与配置热生效
out.append("")
out.append("17) 插件管理标签 / 配置热生效")

csfull = src.get("CustomUI.cs", "")

# 17.0（已删）原来这里去**别的插件**的源码里核对它的标签约定 —— 那是别家插件自己的实现细节，
#      公开仓库里不该出现；本插件只需保证自己用的是标准 `Unturned.Cycle` 标签即可。
# 17.1 DefaultView 必须是「可点击循环切换」的形态
dv = re.search(r"cfgDefaultView\s*=\s*Config\.Bind\([\s\S]{0,900}?\);", csfull)
if dv is None:
    err("认不出 cfgDefaultView 的绑定语句")
else:
    dvs = dv.group(0)
    if "AcceptableValueList<string>" in dvs and "grid" in dvs and "list" in dvs and "blueprint" in dvs:
        ok("DefaultView 给出三档候选值（插件管理据此渲染切换按钮，而不是手输框）")
    else:
        err("DefaultView 没有 AcceptableValueList 三档 —— 玩家在插件管理里只能手输字符串")
    if "Unturned.Cycle" in dvs:
        ok("DefaultView 带 Unturned.Cycle 标签（点击即切视图）")
    else:
        err("DefaultView 缺 Unturned.Cycle 标签 —— 不会被渲染成点击切换按钮")
    if "Unturned.Category:" in dvs or "Cat(" in dvs:
        ok("DefaultView 挂了分类标签（否则会掉进「未分类」，和别的插件混在一屏）")
    else:
        err("DefaultView 没有 Category 标签")

# 17.2 所有配置都要挂 Category；分类名必须复用同族插件已有的词，别自造
binds = re.findall(r"Config\.Bind\(", csfull)
cats = re.findall(r"Cat\(Cat(General|Blueprint)\,", csfull)
if len(binds) == len(cats) and len(binds) > 0:
    ok("每一条配置都挂了分类标签（" + str(len(binds)) + " 条全中）")
else:
    err("配置 " + str(len(binds)) + " 条但只 " + str(len(cats)) +
        " 条挂了 Category —— 没挂的会掉进「未分类」，导航栏就碎了")

shared_cat = re.findall(r'CatGeneral = "([^"]+)"', csfull)
if shared_cat:
    # ⚠ 原来这里拿**同族插件的源码**去核对分类名一致 —— 那属于"别人家自己的文件"，
    #   本仓库不认识任何外部插件 ⟹ 跨插件的那一半整段删除，只留「常量存在」。
    ok("CatGeneral 分类名是个常量（" + shared_cat[0] + "）—— 判据里不点名别的插件")
else:
    err("找不到 CatGeneral 常量")

# 17.3 热生效：必须现读 ConfigEntry 并在变化时重新落地
if re.search(r"SyncLiveConfig\(\);", csfull) and "private static void SyncLiveConfig()" in csfull:
    ok("Update 里每帧比对配置（插件管理器 不回调插件，只能自己发现变化）")
else:
    err("没有 SyncLiveConfig —— 在插件管理里改了配置不会生效（必须重启，等于没接上）")

slc = fn_body(csfull, "private static void SyncLiveConfig(", "SyncLiveConfig")
if slc is not None:
    if "cfgDefaultView.Value" in slc and "cfgEnabled.Value" in slc:
        ok("热同步读的是 ConfigEntry.Value（插件管理器 改的就是内存里这个值）")
    else:
        err("SyncLiveConfig 没读 ConfigEntry.Value")
    if "ApplyDefaultView();" in slc:
        ok("配置变了会重算三视图优先级与 static 开关")
    else:
        err("没重算 ApplyDefaultView —— 优先级/static 还停在启动时的值")
    if "Resort()" in slc:
        ok("重算后重排注册表（否则 FindCandidates 仍按注册时的旧顺序）")
    else:
        err("没重排注册表 —— 改了默认视图但候选顺序没变，界面不会换")
    if "ReloadForConfigChange()" in slc:
        ok("界面正开着会就地重开（不用先关背包再按 F）")
    else:
        err("没就地重开 —— 切换要等下次开箱子才生效")
    if "changed" in slc and "return;" in slc:
        ok("只在值真的变化时才动手（边沿触发，不会每帧重开界面）")
    else:
        err("没做「变化才处理」的边沿判断 —— 每帧重开会抖个不停")

# 17.4 排序逻辑只留一份
reg2 = src.get("CustomUI.Provider.cs", "")
sorts = re.findall(r"providers\.Sort\(", reg2)
if len(sorts) == 1 and "public static void Resort()" in reg2:
    ok("排序只有 Resort() 一处实现，Register 复用它（两处各写一遍迟早漂移）")
else:
    err("providers.Sort 出现 " + str(len(sorts)) + " 处 —— 排序逻辑被复制了")

# 17.5 就地重开必须走既有的 Close→TryOpen，不能新造接管路径
rl = fn_body(host, "internal static void ReloadForConfigChange(", "ReloadForConfigChange")
if rl is not None:
    if "Close();" in rl and "TryOpen(" in rl and rl.find("Close();") < rl.find("TryOpen("):
        ok("热切换复用 Close→TryOpen（还原原版区域的那套状态机只有一份）")
    else:
        err("ReloadForConfigChange 没有复用 Close→TryOpen —— 会留下上一任没还原的原版状态")
    if "!isOpen" in rl and "activeContext == null" in rl:
        ok("没开界面时直接返回（不会凭空弹出一个界面）")
    else:
        err("ReloadForConfigChange 缺早退判据")
    if "ApplyVanillaOverride" in rl or "AttachAndPlacePanel" in rl:
        err("重开逻辑里出现了直接改原版区域/直接挂面板 —— 绕过了状态机")
    else:
        ok("重开逻辑不碰原版区域（全部交给 Close/TryOpen）")
    if "container == null" in rl:
        ok("上下文已失效（箱子被收走）时不重开")
    else:
        err("没判 container 失效 —— 可能对已消失的箱子重开界面")

# -------------------- 18. 右列区块栈（任意多块 + 顺序可被别的插件调）
out.append("")
out.append("18) 右列区块栈（IStorageUiRegion）")

# 设计要点：区块不抢存储槽位，而是把面板交给宿主，宿主按 order 从上往下叠成一条栈；
# 位置靠「把原版那一行的高度改成整栈高度」让「附近物品」自动下移，
# 绝不去改别人的元素或读别人的私有布局字段。
# 注意：变量名 prov 在第 16 节已被复用成 SDK 的 Provider/Provider.cs，
# 这里要的是我们自己的接入层 → 另起一个名字，别复用（复用了会一路"找不到"假 FAIL）。
cprov = src.get("CustomUI.Provider.cs", "")

reg_iface = re.search(r"public interface IStorageUiRegion\s*\{(.*?)\n    \}", cprov, re.S)
if reg_iface is None:
    err("找不到 IStorageUiRegion 接口")
else:
    ok("区块走独立标记接口（不给 IStorageUiProvider 加成员 → 已接入的第三方插件不会编译失败）")
    _miss = [m for m in ("WantsRegion", "RegionOrder", "RegionPanel")
             if m not in reg_iface.group(1)]
    if _miss:
        err("IStorageUiRegion 缺成员：" + " / ".join(_miss))
    else:
        ok("接口三件套齐全（要不要出现 / 排在哪 / 挂哪块面板），生命周期仍复用 Provider")
if "IStorageUiSection" in cprov or "WantsSection" in cprov:
    err("旧的 IStorageUiSection / WantsSection 还留着 —— 两套区块接口必然漂移")
else:
    ok("只有一套区块接口（IStorageUiSection 已被 IStorageUiRegion 取代）")

fr = fn_body(cprov, "public static List<IStorageUiProvider> FindRegions(", "FindRegions")
if fr is None:
    err("注册表没有 FindRegions（宿主无从收集区块候选）")
else:
    if "as IStorageUiRegion" in fr:
        ok("区块候选靠类型探测（老插件不实现新接口也照常工作）")
    else:
        err("FindRegions 没做类型探测")
    if "ReferenceEquals(p, main)" in fr:
        ok("正占存储槽位的那个不会再被收进区块栈（同一条内容不显示两遍）")
    else:
        err("FindRegions 没排除主视图 —— 区块与主面板可能是同一块常驻面板")
    if "IsRegionHidden(p.Id)" in fr:
        ok("被强制关掉的区块连 WantsRegion 都不问（关掉就是真关掉）")
    else:
        err("FindRegions 没查强制隐藏名单")
    if "CustomUIPlugin.Error" in fr:
        ok("区块判据抛异常会被记录并跳过（不连累别人的区块）")
    else:
        err("FindRegions 缺异常隔离")

er = fn_body(cprov, "public static int EffectiveRegionOrder(", "EffectiveRegionOrder")
if er is None:
    err("注册表没有 EffectiveRegionOrder")
else:
    if "regionOrders.TryGetValue" in er:
        ok("宿主侧顺序覆盖优先于 Provider 自己的 RegionOrder（布局类插件能统一重排）")
    else:
        err("EffectiveRegionOrder 没查覆盖表 —— SetRegionOrder 会白调")
    if "catch" in er and "return StorageRegionOrder" in er:
        ok("取不到顺序时回落默认位置（宁可排错，也不因为一个数字把整块弄没）")
    else:
        err("EffectiveRegionOrder 缺兜底 —— Provider 抛异常会连累排版")

for _api in ("SetRegionOrder", "ClearRegionOrder", "SetRegionVisible",
             "RequestRegionsChanged", "RegionIds", "IsRegionHidden",
             "HasRegionOrderOverride"):
    if "public static" in cprov and re.search(r"public static [^\n]*\b" + _api + r"\(", cprov):
        ok("公开 API 有 " + _api + "（外部插件可动态调右侧）")
    else:
        err("缺公开 API：" + _api)
for _c in ("RegionOrderBelowStorage", "RegionOrderAboveStorage", "StorageRegionOrder"):
    if re.search(r"public const int " + _c, cprov):
        ok("公开常量 " + _c + "（第三方不必猜数字）")
    else:
        err("缺公开常量 " + _c)
_clr = fn_body(cprov, "public static void Clear(", "Registry.Clear")
if _clr is not None and "regionOrders.Clear()" in _clr and "hiddenBySource.Clear()" in _clr:
    ok("Clear 连顺序覆盖与强制隐藏名单一起清（卸载后不留残留状态）")
else:
    err("Clear 没清区块覆盖表 —— 重载插件后旧覆盖还会生效")

# 强制隐藏必须**按来源**记名：否则 A 关掉某块、B「恢复」一下就把它放出来（B 从没关过它）
_sv = fn_body(cprov, "public static void SetRegionVisible(string source, string id, bool visible", "SetRegionVisible")
if _sv is None:
    err("SetRegionVisible 不是「(source, id, visible)」三参 —— 全局隐藏名单会让插件互相覆盖")
else:
    if "sources.Remove(source)" in _sv and "sources.Add(source)" in _sv:
        ok("强制隐藏按来源增删（撤销只撤自己那份）")
    else:
        err("SetRegionVisible 没按来源操作集合")
if "public static void SetRegionVisible(string id, bool visible)" in cprov:
    err("还留着两参的全局重载 —— 插件会误用成「替别人恢复」")
else:
    ok("没有「不分来源」的旧重载残留")
if "public static void ClearRegionVisibility(string source)" in cprov:
    ok("ClearRegionVisibility(source)：插件卸载时一次撤掉自己所有标记")
else:
    err("缺 ClearRegionVisibility —— 卸载后会留下别人撤不掉的隐藏标记")
_ih = fn_body(cprov, "public static bool IsRegionHidden(string id", "IsRegionHidden")
if _ih is not None and "sources.Count > 0" in _ih:
    ok("IsRegionHidden 判的是「还有没有来源关着它」（任一来源关着即隐藏）")
else:
    err("IsRegionHidden 判据不对")
if "public static List<string> RegionHidingSources(string id" in cprov:
    ok("RegionHidingSources：能查出「是谁把这块关掉的」（否则只能猜）")
else:
    err("缺 RegionHidingSources —— 「这块怎么不见了」没有可诊断入口")
_unreg = fn_body(cprov, "public static void Unregister(", "Unregister")
if _unreg is not None and "DropProvider(" in _unreg:
    ok("注销 Provider 时先让宿主放手（否则右列留下一块没人驱动的死面板）")
else:
    err("Unregister 没通知宿主 —— 注销后面板会挂在右列上不动")
# ⚠ `Clear()` 是"插件卸载"那条路 —— 它曾经**只清集合**，于是右列留下没人驱动的死面板
#   （宿主以为还有主，而面板因为「关界面只藏不拆」永不销毁、也永不刷新）。
#   判据锁"它也走 DropProvider"，不锁它长什么样。
_clr = fn_body(cprov, "public static void Clear(", "Clear")
if _clr is not None and "DropProvider(" in _clr:
    ok("Clear() 与 Unregister 同款：先让宿主放手再清集合（卸载不留死面板）")
else:
    err("Clear() 只清集合、没让宿主放手 —— 卸载后右列会留下没人驱动的死面板（盘点 §2.6）")
# 状态查询这一族的**宿主侧出口**（公开属性是转发；出口没了就是恒 false/null 的假门面）
if re.search(r"internal static string ActiveProviderId", host):
    ok("`ActiveProviderId` 的宿主侧出口存在（否则公开属性恒 null）")
else:
    err("`ActiveProviderId` 没有宿主侧出口 —— 公开属性会恒返回 null")
if re.search(r"internal static bool IsRegionAttached\(string", host):
    ok("`IsRegionAttached` 的宿主侧出口存在（否则公开属性恒 false）")
else:
    err("`IsRegionAttached` 没有宿主侧出口 —— 公开属性会恒返回 false")
# 临时提示这条线（26.10.3.38）：出口 + 两处语义
for _m, _nm in ((r"internal static void SetNotice\(", "SetNotice"),
                (r"internal static void ClearNotice\(", "ClearNotice"),
                (r"internal static string Notice\b", "Notice")):
    if re.search(_m, host):
        ok("`" + _nm + "` 的宿主侧出口存在（临时提示通道）")
    else:
        err("缺宿主侧出口 `" + _nm + "` —— 公开的 Notify 这条线是断的")
_close = fn_body(host, "internal static void Close(", "Host.Close")
if _close is not None and "ClearNotice()" in _close:
    ok("关界面时清掉临时提示（它是会话状态，不清下次开会话会看到旧提示）")
else:
    err("Host.Close 没清临时提示 —— 新会话会看到上一轮的残留")
_ov = fn_body(host, "internal static bool ApplyVanillaOverride(", "ApplyVanillaOverride")
if _ov is not None and "= Notice" in _ov and "RelayDepositNote" in _ov \
        and _ov.find("= Notice") < _ov.find("RelayDepositNote"):
    ok("标题栏两路提示共用同一拼接点，且**公开那路优先**（第三方发的盖住内置借道兜底）")
else:
    err("标题栏提示的拼接顺序不对 —— 公开提示应当优先于内置的借道兜底")
_sn = fn_body(host, "internal static void SetNotice(", "Host.SetNotice")
if _sn is not None and "ForceRelayout()" in _sn:
    ok("发提示后主动重排一次（标题栏只在排版时写 —— 不重排就成了「等巧合」）")
else:
    err("SetNotice 没主动重排 —— 提示要等「恰好下一次排版」才可能显示（坑 #65）")

# 公开挂载门面：宿主是 internal，没有这层的话「接管存储位」对第三方就是假的路子
# ⚠ 类体不能用 fn_body（它按「签名 + ) + {」匹配，类声明没有参数表）→ 单独取。
_sp_m = re.search(r"public static class StorageUiPanels\s*\{([\s\S]*?)\n    \}", cprov)
if _sp_m is None:
    err("缺公开门面 StorageUiPanels —— 外部插件的 Open 里挂不上面板（StorageUiHost 是 internal）")
else:
    _sp = _sp_m.group(1)
    for _m, _to, _nm in (("public static void Attach(", "StorageUiHost.AttachAndPlacePanel", "Attach"),
                         ("public static ISleekElement AttachSpot(", "StorageUiHost.CurrentAttachSpot", "AttachSpot"),
                         ("public static bool RightColumnAvailable(", "StorageUiHost.IsWideEnoughForRightColumn", "RightColumnAvailable"),
                         ("public static bool IsOpen", "StorageUiHost.IsOpen", "IsOpen"),
                         ("public static string ActiveProviderId", "StorageUiHost.ActiveProviderId", "ActiveProviderId"),
                         ("public static bool IsRegionAttached(", "StorageUiHost.IsRegionAttached", "IsRegionAttached"),
                         ("public static object CurrentVirtualTag", "StorageUiHost.VirtualTag", "CurrentVirtualTag"),
                         ("public static void Notify(", "StorageUiHost.SetNotice", "Notify"),
                         ("public static void ClearNotice(", "StorageUiHost.ClearNotice", "ClearNotice"),
                         ("public static string CurrentNotice", "StorageUiHost.Notice", "CurrentNotice")):
        if _m in _sp and _to in _sp:
            ok("公开门面 StorageUiPanels." + _nm + " 转发到宿主（门面不写第二套逻辑）")
        else:
            err("公开门面缺 StorageUiPanels." + _nm + " 或它没转发到宿主")
    # ⚠ 反向判据必须先剔注释 —— 门面的注释里就写着「绝不 RemoveChild」，
    #   不剔会把这句当违规（本项目反复踩过的那条假 FAIL）。
    if "RemoveChild" not in strip_comments(_sp):
        ok("门面不销毁面板")
    else:
        err("门面里出现 RemoveChild")
    # README 的接入示例必须用公开名，不能教第三方去调 internal
    _rd = ""
    _rp = os.path.join(HERE, "开发文档.md")
    if os.path.exists(_rp):
        with io.open(_rp, "r", encoding="utf-8") as f:
            _rd = f.read()
    if "StorageUiPanels.Attach" in _rd:
        ok("README 的接入示例用公开门面挂面板（不是 internal 的 StorageUiHost）")
    else:
        err("README 还在教第三方调 internal 的 StorageUiHost.AttachAndPlacePanel —— 照着抄会编译不过")

# ★ 26.10.3.40：`StorageUiContext.VirtualPage` 是三档枚举，判据必须**收口**在
#   两个语义化属性上 —— 不许任何消费方自己写 `== Relay` / `== Storage`
#   （"各记一条、漏一处就静默算错"是本项目反复烧过的那一族，见坑 #63）。
_cv_m = re.search(r"public class StorageUiContext\s*\{([\s\S]*?)\n    \}", cprov)
if _cv_m is None:
    err("找不到 StorageUiContext 类体 —— 页面性质的判据没法核")
else:
    _cvb = _cv_m.group(1)
    for _nm, _expr in (("UsableAsContainer", "== StorageUiVirtualPage.Storage"),
                       ("WantsRegions", "!= StorageUiVirtualPage.Relay")):
        if _nm in _cvb and _expr in _cvb:
            ok("语义化判据 `StorageUiContext." + _nm + "` 存在（三档枚举不再让消费方各记一条）")
        else:
            err("缺语义化判据 `" + _nm + "` —— 消费方又要自己去比枚举了")
    # ⚠ 属性不能用 fn_body（它要参数表的 `)`）—— 直接用正则取属性块。
    _setr_m = re.search(r"public StorageUiVirtualPage VirtualPage\s*\{([\s\S]*?)\n        \}", cprov)
    if _setr_m is not None and "WarnExternalVirtualPageWrite()" in _setr_m.group(1):
        ok("`ctx.VirtualPage` 的 setter 有校验/警告（不再是一个谁都能悄悄改的裸字段）")
    else:
        err("`ctx.VirtualPage` 的 setter 没校验 —— 外部改写会静默改掉区块与合成两件事")
# 两个真正的消费方必须改用语义化属性
_fr = fn_body(cprov, "public static List<IStorageUiProvider> FindRegions(", "FindRegions")
if _fr is not None and "WantsRegions" in _fr and "== StorageUiVirtualPage.Relay" not in _fr:
    ok("区块闸口（FindRegions）改用 `ctx.WantsRegions`，不再自己比枚举")
else:
    err("FindRegions 还在自己比 `== Relay` —— 判据没收口（口径改一次就漏一处）")
_craftp = os.path.join(HERE, "CustomUI.Crafting.cs")
_craftsrc = ""
if os.path.exists(_craftp):
    with io.open(_craftp, "r", encoding="utf-8", errors="ignore") as f:
        _craftsrc = f.read()
if "c.UsableAsContainer" in _craftsrc and "c.VirtualPage ==" not in _craftsrc:
    ok("扣箱合成改用 `ctx.UsableAsContainer`，不再自己比枚举")
else:
    err("扣箱合成还在自己比枚举 —— 口径改一次就漏一处")
# 宿主写入必须走 SetVirtualPage（否则自己的写入会被 setter 当成「外部改写」来警告）
if "ctx.SetVirtualPage(" in host and re.search(r"ctx\.VirtualPage\s*=(?!=)", strip_comments(host)) is None:
    ok("宿主走 `SetVirtualPage` 写入（不会把自己按声明写入当成外部改写）")
else:
    err("宿主还直接写 `ctx.VirtualPage =` —— 会把自己的写入当成外部改写来警告")

# 宿主：排版集中在 LayoutStack 一处（谁都不许自己算 y），且沿用「只藏不拆」
ls = fn_body(host, "private static void LayoutStack(", "LayoutStack")
if ls is None:
    err("找不到 LayoutStack —— 右列排版没有唯一入口")
else:
    if "area.AddChild(it.Panel)" in ls and "ReferenceEquals(it.Panel.Parent, area)" in ls:
        ok("区块挂在原版右列（areaBox）上，父节点变化时才重挂（切关/换服后不会挂到旧画布）")
    else:
        err("区块没有按「当前父节点」判断重挂 —— 切关/换服后会挂到已销毁的画布上")
    if "RemoveChild" in ls:
        err("排版里出现 RemoveChild（= Destroy，会毁掉常驻行池与图标）")
    else:
        ok("排版不销毁任何面板（沿用 §4.7 的常驻策略）")
    if "PositionOffset_Y = y" in ls and "y += it.Panel.SizeOffset_Y" in ls:
        ok("从上往下累加定位（每块的 y = 上一块下沿 + 间隔）")
    else:
        err("LayoutStack 没有累加定位 —— 区块会全部叠在同一个 y 上")
    if "REGION_GAP" in ls:
        ok("块与块之间留间隔（多块不会糊成一片）")
    else:
        err("没加间隔 —— 多块会贴在一起看不出边界")
    if "StorageRowTopY()" in ls:
        ok("栈的起点 = 原版存储网格该在的位置（不凭空给 y）")
    else:
        err("LayoutStack 没从原版存储行的位置起算")
    # 旧版这条守卫写在 AttachAndPlaceSectionPanel 里（判 panel.Parent == area），
    # 改成宿主统一排版后它的等价物是「主面板可见才排版」。
    if "if (panel == null || !panel.IsVisible) return;" in ls:
        ok("主面板没就位时整栈不排版（打开那一帧区块不会先抢到存储位 = 旧「主面板先就位」守卫）")
    else:
        err("缺「主面板先就位」的守卫 —— 打开界面那一帧区块会闪到存储位上")
    if "a.Tie.CompareTo" in ls and "stackBuf.Sort" in ls:
        ok("排序显式打破同 order 平局（List.Sort 不稳定，不判 Tie 会每帧换顺序）")
    else:
        err("排序没判平局 —— 两个区块给同一个 order 时右侧会抖")
    if "ContentSizeOffset" in ls:
        ok("会扩滚动内容尺寸（否则多块时右侧滚不到「附近物品」）")
    else:
        err("没扩滚动尺寸 —— 区块一多就滚不到底")
    if "ApplyVanillaOverride()" in ls:
        ok("排版把整栈高度交给原版（附近物品由原版自己下移，我们不去挪别人）")
    else:
        err("排版没反馈高度给原版 —— 区块会压住「附近物品」")

# 除了 LayoutStack，别人不许再写面板 y（两处各算一份 y 必然叠）
_writers = len(re.findall(r"\.PositionOffset_Y\s*=\s*y", host))
if _writers == 1:
    ok("全宿主只有一处写面板 y（排版只有一个入口）")
else:
    err("有 " + str(_writers) + " 处在写面板 y —— 两套排版必然互相叠")

# 高度累加：每一块都算、同一块面板只算一次、藏起来的不占位
_cv = fn_body(host, "private static void CountVisible(", "CountVisible")
oh = fn_body(host, "private static float OccupiedHeight(", "OccupiedHeight")
if oh is None or _cv is None:
    err("找不到 OccupiedHeight / CountVisible")
else:
    if "CountVisible(panel)" in oh and "slots[i].Panel" in oh:
        ok("占用高度 = 主面板 + 每一个区块（缺一块就会重叠）")
    else:
        err("OccupiedHeight 没把区块栈全部算进去")
    if "REGION_GAP" in oh:
        ok("高度含块之间的间隔（原版留的位置才不会比实际矮）")
    else:
        err("没算间隔")
    # ⚠ 用户实测：在插件管理里把 Placement 从 below 点成 replace，同一块常驻面板
    #   会同时是主面板与区块 → 高度算两遍 → 附近物品上方一大段空位。
    if "counted.Contains" in _cv:
        ok("挡住「同一块面板算两次」（below / replace 热切换时的实测空位 bug）")
    else:
        err("CountVisible 没去重 —— 切换摆法后附近物品上方留空位")
    if "!p.IsVisible" in _cv:
        ok("按可见性累加（区块被藏起来时不该继续占空间）")
    else:
        err("没判可见性 —— 关掉某块后右侧会留一段空白")

ov = fn_body(host, "internal static bool ApplyVanillaOverride(", "ApplyVanillaOverride")
# ★ 26.10.3.67：高度的口径从 `occupied` 变成 `VanillaGridBlockHeight() + occupied`
#   （并存时给原版网格自己也留一段）⟹ 判据跟着搬家，并**升一级**：两个加数都要在，
#   而且**缺哪个就报哪个**（不然"丢了并存段"会被误报成"没并进整栈高度"）。
if ov is None or "OccupiedHeight()" not in ov or "grid.SizeOffset_Y" not in ov:
    err("ApplyVanillaOverride 没把区块栈高度并进去 —— 区块会压住附近物品")
elif re.search(r"=\s*VanillaGridBlockHeight\(\)\s*\+\s*occupied\s*;", ov) \
        and "grid.SizeOffset_Y = target" in ov:
    ok("原版那一行的高度 = 并存段 + 整栈高度 → 附近物品由原版自己下移（并存时也不会压住）")
else:
    err("高度记账**丢了并存段** —— 并存模式下原版网格会被算成 0 高度（与我们重叠）")

# 区块集合的差分维护：新出现的才 Open，还在的原样复用，消失的 Close
sy = fn_body(host, "private static void SyncRegions(", "SyncRegions")
if sy is None:
    err("找不到 SyncRegions")
else:
    if "FindRegions(ctx, main)" in sy:
        ok("区块候选来自注册表（宿主不认识任何具体插件）")
    else:
        err("SyncRegions 没走注册表")
    if "s.Keep = true" in sy and "if (s.Keep) continue;" in sy:
        ok("差分维护：还在的区块不重开（重开 = 白毁一次常驻面板与图标）")
    else:
        err("SyncRegions 不是一律拆了重建 —— 每次改顺序都会重载图标")
    if "regionRefresh = false" in sy:
        ok("进来先清请求标记（否则一次请求会反复重算）")
    else:
        err("没清 regionRefresh —— 会每帧重算区块栈")
    if "regionSyncFailures" in sy:
        ok("区块打开失败计数并写进日志（少一块不会静默）")
    else:
        err("区块失败没留痕")
fs = fn_body(host, "private static RegionSlot FindSlot(", "FindSlot")
if fs is not None and "CloseSlot(s)" in fs and "slots.RemoveAt(i)" in fs:
    ok("同名换了实例（重新注册）会关掉旧的（否则死面板还挂在右列）")
else:
    err("FindSlot 没处理「同 Id 不同实例」")
rp = fn_body(host, "private static ISleekElement RegionPanelOf(", "RegionPanelOf")
if rp is not None and "catch" in rp and "s.Failed" in rp:
    ok("取面板有异常隔离，且只报一次日志（不每帧刷）")
else:
    err("RegionPanelOf 缺异常隔离 —— 一个坏区块会拖垮整栈")

# 生命周期：驱动顺序、关闭、注销
clo = fn_body(host, "internal static void Close(", "Host.Close")
if clo is not None:
    if "CloseSlot(slots[i])" in clo and "slots.Clear()" in clo:
        ok("关闭时区块一起收（并保留常驻控件，不销毁）")
    else:
        err("Close 没处理区块 —— 关背包后区块可能残留")
    if "RemoveChild" not in clo:
        ok("Close 不销毁区块面板（与 §4.7 一致）")
    else:
        err("Close 里对区块做了 RemoveChild = Destroy")
_host_tick = fn_body(host, "internal static void Tick(", "Host.Tick")
if _host_tick is not None and "activeProvider.Tick" in _host_tick \
        and "TickRegions(activeContext)" in _host_tick and "LayoutStack();" in _host_tick:
    if (_host_tick.index("activeProvider.Tick") < _host_tick.index("TickRegions(activeContext)")
            < _host_tick.index("LayoutStack();")):
        ok("顺序 = 主面板 Tick → 区块 Tick → 排版（排版要等所有高度定型）")
    else:
        err("Tick 顺序不对 —— 排版会用到旧高度，区块位置慢一帧")
else:
    err("宿主 Tick 里没找到「主面板 → 区块 → 排版」这条顺序")
if _host_tick is not None and "if (regionRefresh) SyncRegions" in _host_tick:
    ok("外部请求重算区块栈会在下一帧生效（动态加/减区域不必重开箱子）")
else:
    err("Tick 没处理 regionRefresh 请求")
dp = fn_body(host, "internal static void DropProvider(", "DropProvider")
if dp is not None and "CloseSlot" in dp and "Close()" in dp:
    ok("注销时：区块收掉；正占存储槽位的那个被注销 → 整个接管收回（回退原版）")
else:
    err("DropProvider 不完整 —— 注销主视图会留下没人驱动的面板")
rb2 = fn_body(host, "internal static void Rebuild(", "Host.Rebuild")
if rb2 is not None and "slots[i].Provider.Rebuild" in rb2:
    ok("引擎侧物品变化时区块也重建（蓝图列表读的就是箱内物品）")
else:
    err("Rebuild 只重建主面板 —— 区块会等到下一轮轮询才更新")

# 配置：三档摆法 + 覆盖顺序的入口 + 热生效
if re.search(r'Config\.Bind\("Blueprint",\s*"Placement",\s*"below"', csfull):
    ok("[Blueprint] Placement 默认 below（右侧三段是默认形态）")
else:
    err("[Blueprint] Placement 默认值不是 below")
if re.search(r'AcceptableValueList<string>\("below",\s*"above",\s*"replace"\)', csfull):
    ok("Placement 三档候选（below / above / replace）+ Cycle → 插件管理里点一下就换")
else:
    err("Placement 没给出 above 档 —— 「把合成放在存储上方」得能点出来")
adv2 = fn_body(csfull, "private static void ApplyDefaultView(", "ApplyDefaultView")
if adv2 is not None and "RegionOrderAboveStorage" in adv2 and "RegionOrderBelowStorage" in adv2:
    ok("Placement 翻译成区块顺序（above → 负数、below → 正数）")
else:
    err("ApplyDefaultView 没把 Placement 映射成 SectionOrder")
if re.search(r'Config\.Bind\("Blueprint",\s*"SectionMaxHeight",\s*240f', csfull):
    ok("[Blueprint] SectionMaxHeight 有上限可调（防止把附近物品挤没）")
else:
    err("缺 SectionMaxHeight 配置")
slc2 = fn_body(csfull, "private static void SyncLiveConfig(", "SyncLiveConfig")
if slc2 is not None and "lastBpPlacement" in slc2 and "lastBpSectionHeight" in slc2:
    ok("摆法与区块高度也纳入热生效（在插件管理里点一下就换）")
else:
    err("SyncLiveConfig 没跟 Placement / SectionMaxHeight —— 改了要重启才生效")
if slc2 is not None and "RequestRegionsChanged()" in slc2:
    ok("配置热生效后请宿主重算区块栈（光重排优先级不会改区块顺序）")
else:
    err("热生效没请求重算区块栈 —— 改摆法要重开箱子才看到")

# ---------------- 19. transferState（产物继承材料状态）—— 逐条回 SDK 核前提
out.append("")
out.append("19) transferState 产物继承")

crc19 = strip_comments(cr)

# 19.0 三件前提（不成立则本节实现全错）
inv19 = sdk_read("Player/InventorySearch.cs")
if inv19 is None:
    err("SDK 文件缺失：Player/InventorySearch.cs")
else:
    cmps = ["InventorySearchAmountAscendingComparator",
            "InventorySearchAmountDescendingComparator",
            "InventorySearchQualityAscendingComparator",
            "InventorySearchQualityDescendingComparator"]
    missing = []
    for c in cmps:
        at = inv19.find("public class " + c)
        if at < 0 or "public int Compare(PlayerInventorySearchResultV2" not in inv19[at:at + 900]:
            missing.append(c)
    if not missing:
        ok("四个排序比较器都是 public 且能排 PlayerInventorySearchResultV2 → 排序用引擎的")
    else:
        err("这些比较器不是 public 或排不了 V2：" + ", ".join(missing))

gun = sdk_read("Bundles/ItemGunAsset.cs")
if gun is not None:
    if re.search(r"internal EFiremode firemode", gun):
        ok("ItemGunAsset.firemode 确实是 internal（不能直接读，必须走 getState 的等价取法）")
    else:
        w("firemode 可见性变了 —— 重新核对能否直接读")
    if re.search(r"public override byte\[\] getState\(EItemOrigin origin\)", gun) \
            and "(byte) firemode, // firemode" in gun:
        ok("getState(origin) 是 public 且数组含 firemode → 取 [11] 与原版那句等价")
    else:
        err("getState 可见性或数组内容变了 —— state[11] 的取法需重新核对")
else:
    err("SDK 文件缺失：Bundles/ItemGunAsset.cs")

pc19 = sdk_read("Player/PlayerCrafting.cs")
if pc19 is not None and "inputItems[0].searchResults[0]" in pc19:
    ok("原版 transferState 取的就是 inputItems[0].searchResults[0]（排序后第 0 件）")
else:
    err("原版 transferState 的取法变了 —— 继承来源的选择要重新对齐")

# 19.1 排序只用引擎那份
if "hits.Sort(CMP_AMOUNT_ASC)" in crc19 and "hits.Sort(CMP_QUALITY_DESC)" in crc19:
    ok("SortByPrioritization 用引擎的四个比较器（List.Sort(IComparer)）")
else:
    err("排序没走引擎比较器 —— 自写一份迟早漂移，产物耐久抄错是静默的")
if re.search(r"Comparison<PlayerInventorySearchResultV2>", crc19):
    err("文件里还留着自写的 Comparison 比较器 —— 与引擎那份重复，删掉自写的")
else:
    ok("没有第二份排序实现（自写比较器已删除）")
if "未处理的 crafting 排序方式" in crc19:
    ok("遇到未知 Prioritization 保持原顺序并 warn（不猜方向 = 不静默抄错）")
else:
    err("未知 Prioritization 没有兜底分支")

# 19.2 不再被拒绝
ur19 = fn_body(cr, "internal static string UnsupportedReason(", "UnsupportedReason")
if ur19 is None:
    err("找不到 UnsupportedReason")
elif "bp.transferState" in ur19:
    err("UnsupportedReason 里还挡着 transferState —— 实现没接上")
else:
    ok("UnsupportedReason 不再拒绝 transferState（已实现）")

# 19.3 快照时机 + 第一路按排序扣
bp19 = fn_body(cr, "private static bool BuildPlan(", "BuildPlan")
if bp19 is None:
    err("找不到 BuildPlan")
else:
    capAt = bp19.find("CaptureTransferSource(")
    loopAt = bp19.find("for (int i = 0; i < supplies.Length")
    if capAt >= 0 and loopAt >= 0 and capAt < loopAt:
        ok("继承快照在规划/扣料之前抓（扣完 jar 就拿不到原始数量/耐久/state 了）")
    else:
        err("快照抓得太晚 —— 被扣掉的 jar 拿不到原样数据")
    if "if (transfer && i == 0)" in bp19 and "AddTakes(transferHits" in bp19:
        ok("transferState 的第一路材料按引擎排序扣（「继承的那件」就是「被吃掉的那件」）")
    else:
        err("transferState 仍按先箱内后背包扣 —— 会抄了 A 件状态、吃掉 B 件而原件还在")

# 19.4 造产物
bt = fn_body(cr, "private static Item BuildTransferItem(", "BuildTransferItem")
if bt is None:
    err("找不到 BuildTransferItem")
else:
    if "(byte[])transferState.Clone()" in bt:
        ok("每个产物各 clone 一份 state（原版直接塞 jar 的数组：多产物共用一个、且 jar 已删）")
    else:
        err("state 没 clone —— 多产物共用一个数组，改一个动全部")
    zeroAt = bt.find("item.state[z] = 0")
    fireAt = bt.find("item.state[11]")
    if zeroAt >= 0 and fireAt >= 0 and zeroAt < fireAt:
        ok("先清零附件、后写 firemode（顺序反了 firemode 会被抹掉）")
    else:
        err("清零与写 firemode 的顺序不对")
    if ".firemode" in bt:
        err("试图读 internal 的 firemode 字段 —— 必须走 getState(origin)[11]")
    elif "getState(EItemOrigin.CRAFT)" in bt and "state[11]" in bt:
        ok("firemode 走 public 的 getState(origin)[11]（等价于原版那句 internal 读取）")
    else:
        err("没找到 firemode 的等价取法")
    if "EItemType.GUN" in bt and "Length >= 12" in bt:
        ok("只在枪→枪且 state 足够长时动 firemode/附件（与原版条件一致）")
    else:
        err("枪类特例判据与原版不一致")

gp = fn_body(cr, "private static int GrantProducts(", "GrantProducts")
if gp is not None and "bp.transferState ? BuildTransferItem(" in gp:
    ok("发产物按 transferState 分流（普通产物仍走 new Item(id, origin)）")
else:
    err("GrantProducts 没接 BuildTransferItem —— 产物永远全新，继承没生效")

# 19.5 快照不跨次泄漏
tc19 = fn_body(cr, "internal static CraftOutcome TryCraft(", "TryCraft")
if tc19 is not None and "transferHasSource = false;" in tc19:
    ok("合成结束清掉继承快照（静态字段带到下一次会拿旧耐久造东西）")
else:
    err("没清快照 —— 跨次残留")

# 19.6 界面提示必须与执行同源
bv19 = src.get("CustomUI.BlueprintView.cs", "")
if "StorageCrafting.DescribeTransfer(" in bv19:
    ok("界面的「继承谁」提示走 DescribeTransfer（与执行同一个 capture）")
else:
    err("界面没显示继承来源 —— 没法核对，且以后容易有人另写一套排序")
# ⚠ 判据要指准：视图里 entries.Sort(CompareEntry) 是排**显示顺序**（合法且必要）。
#   这里要挡的是"对搜索结果排序"（那才是第二套继承顺序）。
if "PlayerInventorySearchResultV2" in bv19 or "SortByPrioritization" in bv19:
    err("蓝图视图碰了搜索结果/引擎排序 —— 「继承谁」会出现第二套实现，必须只调 DescribeTransfer")
else:
    ok("蓝图视图不对搜索结果排序（继承顺序只存在于执行侧；视图自己的 entries 排序是显示顺序）")
if "if (craftable && bp.transferState)" in bv19:
    ok("只对「有按钮」的行算继承提示（不拖慢整表重建）")
else:
    err("继承提示没按 craftable 收窄 —— 每次重建多跑一批搜索")

# ---------------- 20. 蓝图大格子摆法（list / grid 共用一套池子）
out.append("")
out.append("20) 蓝图大格子摆法（Layout = list / grid）")

bv20 = src.get("CustomUI.BlueprintView.cs", "")
bvc20 = strip_comments(bv20)

# 20.1 两种摆法只能有一套身份认领逻辑（复制一份必然漂移）
claims = len(re.findall(r"ClaimByKey\(e\.Key\)", bv20))
if claims == 1:
    ok("行/格共用同一套身份认领（ClaimByKey 只被调用一处，没有第二份池子循环）")
else:
    err("ClaimByKey 被调用 " + str(claims) + " 处 —— 两种摆法各写了一份池子逻辑，迟早漂移")
if bv20.count("private BlueprintRow ClaimByKey(") == 1:
    ok("认领函数只有一个定义")
else:
    err("ClaimByKey 定义不唯一")

# 20.2 摆法差异只允许出现在 PlaceItem / RebuildRow / FillRowText
pi = fn_body(bv20, "private void PlaceItem(", "PlaceItem")
if pi is None:
    err("找不到 PlaceItem（摆法差异应收敛在这里）")
else:
    if "AsGrid" in pi and "CELL_SCALE_X" in pi:
        ok("摆法差异收敛在 PlaceItem（列表整宽 / 格子 1/4 宽）")
    else:
        err("PlaceItem 没按 AsGrid 分支")
    # ⚠ 格子横向必须用相对宽度：像素宽在挂载前面板宽为 0 时会全挤在左边
    if "SizeScale_X = CELL_SCALE_X" in pi:
        ok("格子用相对宽度（SizeScale_X = 1/列数）—— 不用像素，避开「挂载前宽度为 0」那条坑")
    else:
        err("格子没用相对宽度 —— 面板宽度依赖原版右列，像素定位会错位")
    if "GRID_COLUMNS" in pi and "index % GRID_COLUMNS" in pi and "index / GRID_COLUMNS" in pi:
        ok("格子按「列 = index % 列数、行 = index / 列数」摆位（与 GridView 同一套算法）")
    else:
        err("格子行列换算不对")

ch = fn_body(bv20, "private static float ContentHeight(", "ContentHeight")
if ch is not None and "GRID_COLUMNS" in ch:
    ok("内容总高按摆法折算（格子按行取整），滚动范围不会算错")
else:
    err("ContentHeight 没按摆法折算格子行数")

# 20.3 换摆法必须作废常驻池
op20 = fn_body(bv20, "public bool Open(", "Open")
if op20 is not None and "builtAsGrid != AsGrid" in op20 and "DiscardPark()" in op20:
    ok("切换摆法时作废常驻池（列表整宽行不能当 1/4 格子复用，会文字重叠/按钮错位）")
else:
    err("没在摆法变化时作废池子 —— 从 list 切 grid 会拿旧结构渲染")
bd = fn_body(bv20, "private void Build(", "Build")
if bd is not None and "builtAsGrid = AsGrid" in bd:
    ok("Build 记下本块面板是按哪种摆法建的")
else:
    err("Build 没记录 builtAsGrid（作废判断失去依据）")

# 20.4 格子点整格：必须现读可点性
oc = fn_body(bv20, "private void OnCellClicked(", "OnCellClicked")
if oc is None:
    err("找不到 OnCellClicked（格子摆法的点击入口）")
elif "!r.Craftable" not in oc:
    err("OnCellClicked 没现读 r.Craftable —— 格子被复用后不可合的配方也能按下去扣料")
else:
    ok("格子点击现读 r.Craftable（不可合时点了不扣任何东西）")
ft = fn_body(bv20, "private void FillRowText(", "FillRowText")
if ft is not None and "r.Craftable = e.craftable" in ft and "r.Bp = e.blueprint" in ft:
    ok("每次填文字都刷新 r.Bp / r.Craftable（复用单元不会带旧状态）")
else:
    err("FillRowText 没同时刷新 r.Bp 与 r.Craftable —— 行池复用会拿旧配方或旧可点性")

# 20.5 状态文案两摆法同源
ss = fn_body(bv20, "private static string ShortStatus(", "ShortStatus")
if ss is None:
    err("找不到 ShortStatus")
else:
    if "rank ==" in ss and "craftable" in ss:
        ok("短状态由 rank + craftable 推得（与列表完整状态同源，不是另算一套判据）")
    else:
        err("ShortStatus 的判据不完整")
    if bvc20.count("e.statusShort = ShortStatus(") == 1:
        ok("Evaluate 里只算一次短状态")
    else:
        err("statusShort 赋值处不唯一")

# 20.6 文字行高与图标纪律（两摆法共用同一套铁律）
for const_name in ["CELL_NAME_H", "CELL_STATUS_H"]:
    m = re.search(r"private const float " + const_name + r" = ([0-9.]+)f?;", bv20)
    if m is None:
        err("找不到常量 " + const_name)
    elif float(m.group(1)) < 24:
        err(const_name + " = " + m.group(1) + " < 24 —— 小号标签行高不足会一个字都不渲染")
    else:
        ok(const_name + " = " + m.group(1) + " ≥ 24（格子两行文字都不会踩空）")
rr = fn_body(bv20, "private void RebuildRow(", "RebuildRow")
if rr is not None:
    # ⚠ 必须剔注释再查：本项目的注释里就写着「绝不读任何 item.state」，
    #   不剔会把这句当成违规（反向判据的老坑）。
    rr_code = strip_comments(rr)
    if "getState(" in rr_code and "item.state" not in rr_code:
        ok("格子图标同样只取资产默认状态（不读 item.state —— 那会每帧变、导致指纹抖动）")
    else:
        err("RebuildRow 的图标状态取法有问题（必须 getState()，绝不读 item.state）")
    if "AsGrid" in rr:
        ok("单元结构按摆法分支（图标位置 / 文字位置 / 是否建合成按钮）")
    else:
        err("RebuildRow 没按摆法分支")
if ft is not None and "Refresh(" in ft:
    err("FillRowText 里出现 Refresh —— 改文字/换底色不该重载图标")
elif ft is not None:
    ok("FillRowText 只改文字、底色与可点性（图标只在换配方时重建）")

# 20.7 配置：默认 list、可点击循环切换、且热生效
if re.search(r'Config\.Bind\("Blueprint",\s*"Layout",\s*"list"', csall):
    ok("[Blueprint] Layout 默认 list（保持既有观感，格子是新增选项）")
else:
    err("[Blueprint] Layout 默认值不是 list")
_lay = re.search(r"cfgBlueprintLayout\s*=\s*Config\.Bind\([\s\S]{0,700}?\);", csall)
if _lay is not None and "AcceptableValueList<string>" in _lay.group(0) \
        and "Unturned.Cycle" in _lay.group(0):
    ok("Layout 挂 Cycle + 两档候选 → 插件管理里点一下就换摆法")
else:
    err("Layout 没做成可点击循环切换")
slc20 = fn_body(csall, "private static void SyncLiveConfig(", "SyncLiveConfig")
if slc20 is not None and "lastBpLayout" in slc20:
    ok("Layout 纳入热生效（改了不用重启）")
else:
    err("SyncLiveConfig 没跟 Layout —— 切换摆法要重启才生效")

# -------------------- 21. 蓝图显示范围（StorageBlueprintFilters，公开 API）
out.append("")
out.append("21) 配方显示范围可被别的插件限定")

bv21 = src.get("CustomUI.BlueprintView.cs", "")
if "public static class StorageBlueprintFilters" in bv21:
    ok("公开的配方过滤器存在（第三方限定范围不必改本插件）")
else:
    err("找不到 StorageBlueprintFilters")
if re.search(r"public delegate bool StorageBlueprintFilterDelegate\(Blueprint bp, StorageUiContext ctx\)", bv21):
    ok("判据签名带上下文 —— 可以按「当前这块箱子」给不同显示范围")
else:
    err("判据签名不对（必须收 ctx，否则机器类插件没法按箱型区分）")

_fadd = fn_body(bv21, "public static void Add(string source, StorageBlueprintFilterDelegate filter", "Filters.Add")
if _fadd is not None:
    if "sources[i].Name == source" in _fadd and "RemoveAt(i)" in _fadd:
        ok("按来源命名且同名替换 —— 多个插件各占一名，不会谁后注册谁覆盖别人")
    else:
        err("Add 没做同名替换 —— 插件重载一次就会挂两份判据")
    if "version++" in _fadd:
        ok("改动会 bump Version（视图靠它做「界面开着也立刻重算」）")
    else:
        err("Add 没 bump version —— 改了过滤器要重开箱子才生效")
_fpass = fn_body(bv21, "public static bool Passes(Blueprint bp, StorageUiContext ctx", "Filters.Passes")
if _fpass is not None:
    if "PassesConfig(bp)" in _fpass:
        ok("配置限定与第三方判据叠加（AND），不是互相顶掉")
    else:
        err("Passes 没查配置限定 —— OnlyCategories / OnlyProducts 会形同虚设")
    if "return false" in _fpass:
        ok("任一来源说不显示就不显示（限定 = 收窄）")
    else:
        err("Passes 没有否决路径")
    _fpc = strip_comments(_fpass)
    if "catch" in _fpc and "s.Warned" in _fpc:
        ok("判据抛异常按「显示」处理并只报一次（坏判据不该把整张列表弄没）")
    else:
        err("Passes 缺异常隔离 —— 一个插件抛错会让别人的配方全消失")
_fb = fn_body(bv21, "public static bool MatchesCategory(Blueprint bp, string[] categories", "MatchesCategory")
if _fb is not None:
    if "GetCategoryTag()" in strip_comments(_fb):
        ok("类别走 public 的 GetCategoryTag（不碰 [Obsolete] 的 blueprint.type）")
    else:
        err("MatchesCategory 没查 category 标签")
    if "FriendlyName" in _fb and "OrdinalIgnoreCase" in _fb:
        ok("类别名认英文资产名也认中文显示名，不分大小写")
    else:
        ok("类别名判据已简化（只认资产名）")
_fp = fn_body(bv21, "public static bool MatchesProduct(Blueprint bp, ushort[] ids", "MatchesProduct")
if _fp is not None:
    if "outputs" in strip_comments(_fp) and "FindItemAsset()" in strip_comments(_fp):
        ok("按产物 id 限定用引擎的 outputs（不是猜 bp.id —— 3.26 的 Blueprint 没有 id）")
    else:
        err("MatchesProduct 没走 outputs")
    if "GetOwnerAsset()" in strip_comments(_fp):
        ok("装弹/修理这类没有 outputs 的配方，退回「被操作的那件」当产物")
    else:
        err("MatchesProduct 漏了操作类配方（它们没有 outputs）")

# 视图侧必须真的用上：判据进重建路径、版本进指纹
ce = fn_body(bv21, "private void CollectEntries(Player player", "CollectEntries")
if ce is not None and "StorageBlueprintFilters.Passes(bp, ctx)" in strip_comments(ce):
    ok("重建路径调了 Passes（过滤器真的在收窄列表）")
else:
    err("CollectEntries 没接过滤器 —— API 挂上去没人读")
if ce is not None and "ContainsAnyOfItems" in ce and "evaluatedCandidates >=" in ce:
    # ⚠ 顺序判据**必须先剔注释**：26.10.3.36 我新加的注释里正好引用了
    #   `if (evaluatedCandidates >= MAX_CANDIDATES) break;`，于是"第一次出现"落在注释上 ⟹ 假 FAIL。
    #   （同族坑已踩多次：注释/字符串里的代码片段会被顺序判据当成真代码。）
    _ce_code = strip_comments(ce)
    if _ce_code.index("ContainsAnyOfItems") < _ce_code.index("StorageBlueprintFilters.Passes") \
            < _ce_code.index("evaluatedCandidates >="):
        ok("过滤位置在粗筛之后、细算之前（被滤掉的不占候选额度、不进 Evaluate）")
    else:
        err("过滤时机不对 —— 放在细算之后等于白算，放在最前面会跳过引擎粗筛")
fp21 = fn_body(bv21, "private static int Fingerprint(StorageUiContext c", "Blueprint.Fingerprint")
if fp21 is not None and "StorageBlueprintFilters.Version" in strip_comments(fp21):
    ok("过滤器版本进数据指纹（界面开着改范围也会立刻重算）")
else:
    err("Fingerprint 没含过滤器版本 —— 改范围要重开箱子才看到，看起来像没生效")

# 配置：两条限定项 + 热生效 + 文档
cs21 = src.get("CustomUI.cs", "")
if re.search(r'Config\.Bind\("Blueprint",\s*"OnlyCategories",\s*""', cs21):
    ok("[Blueprint] OnlyCategories 已接配置（留空 = 不限，不会悄悄藏配方）")
else:
    err("缺 OnlyCategories 配置")
if re.search(r'Config\.Bind\("Blueprint",\s*"OnlyProducts",\s*""', cs21):
    ok("[Blueprint] OnlyProducts 已接配置（按产物 id 限定）")
else:
    err("缺 OnlyProducts 配置")
if "Unturned.Category:蓝图视图" in cs21:
    ok("两条新配置仍挂在「蓝图视图」分类下（不会掉进未分类）")
else:
    err("新配置没挂分类标签")
_adv21 = fn_body(cs21, "private static void ApplyDefaultView(", "ApplyDefaultView")
if _adv21 is not None and "SetConfigFilter(" in _adv21:
    ok("ApplyDefaultView 把两条限定落地到过滤器")
else:
    err("ApplyDefaultView 没落地 OnlyCategories / OnlyProducts")
_slc21 = fn_body(cs21, "private static void SyncLiveConfig(", "SyncLiveConfig")
if _slc21 is not None and "lastBpCategories" in _slc21 and "lastBpProducts" in _slc21:
    ok("显示范围纳入热生效（改完立刻重算，不必重启也不必重开箱子）")
else:
    err("SyncLiveConfig 没跟 OnlyCategories / OnlyProducts —— 改了要重启才生效")
_rt21 = ""
_rp21 = os.path.join(HERE, "开发文档.md")
if os.path.exists(_rp21):
    with io.open(_rp21, "r", encoding="utf-8") as f:
        _rt21 = f.read()
for _kw in ("StorageBlueprintFilters", "IStorageUiRegion", "SetRegionOrder", "RequestRegionsChanged"):
    if _kw in _rt21:
        ok("开发文档 写了 " + _kw)
    else:
        err("README 缺 " + _kw + " —— 这是给第三方插件用的公开 API，没文档等于没有")

# -------------------- 22. 外部消费者 —— 公开面真的接得上吗
out.append("")
out.append("22) 外部插件接入自检（通用扫描，不点名任何插件）")

# 为什么单开一节：CustomUI 的内置视图与宿主**同程序集**，能直接用 internal，
# 所以"内置视图能跑"永远证明不了"公开面对外部可用"。
# README 坑 #45 / #49 就是这类缺口（接口写好了、文档写了、第三方照着抄编译不过）。
#
# ★ 通用扫描（用户 2026-10-12 定）：本仓库**不点名任何消费者插件**。
#   消费者由环境变量 `CONSUMERS_DIR` 指过来（逗号分隔多目录，**不必复制**源码）；
#   外部使用者不设它、也没有 ./consumers ⟹ 本节判据**全部 SKIP**（不算失败、不进分母）。
#   判据形状一律是「在**合并后的消费者源码**里找成员名」—— 不关心是谁写的。
if not _CHAVE:
    skip("没扫到外部消费者（" + _CONSUMERS.summary() + "）—— 第 22 节判据全部跳过")
else:
    ok("扫到外部消费者：" + _CONSUMERS.summary())

    # ---- (a) 公开面/机制必须被外部**真的用上**（只在合并源码里找成员名）----
    # 状态查询面（26.10.3.37）：既是编译期约束（碰 internal 就编译不过），运行期也看得见。
    _q4 = ("StorageUiPanels.IsOpen", "StorageUiPanels.ActiveProviderId",
           "StorageUiPanels.IsRegionAttached(", "StorageUiPanels.CurrentVirtualTag")
    _q4_miss = [t for t in _q4 if t not in _CCODE]
    if not _q4_miss:
        ok("外部消费者运行期读了状态查询面（IsOpen / ActiveProviderId / IsRegionAttached / CurrentVirtualTag）")
    else:
        err("外部消费者没读全状态查询面（缺：" + "、".join(_q4_miss) + "）—— 这些公开面没人从外部验证")
    _notice = ("StorageUiPanels.Notify(", "StorageUiPanels.CurrentNotice", "StorageUiPanels.ClearNotice(")
    _notice_miss = [t for t in _notice if t not in _CCODE]
    if not _notice_miss:
        ok("外部消费者运行期跑了临时提示面（发 → 读回 → 收掉，三件都真调了）")
    else:
        err("外部消费者没跑全临时提示面（缺：" + "、".join(_notice_miss) + "）")
    if "UsableAsContainer" in _CCODE and "WantsRegions" in _CCODE:
        ok("外部消费者运行期读了语义化页面判据（UsableAsContainer / WantsRegions）")
    else:
        err("外部消费者没读语义化页面判据 —— 这 2 个公开面又没人从外部验证了")

    # ⚠ 反向断言先剔注释（本项目第五次提醒自己）。
    if "StorageUiHost" in _CCODE:
        err("外部消费者源码里出现 StorageUiHost（internal）—— 外部消费者不能走内部后门")
    else:
        ok("外部消费者零 internal 访问（只用公开面）")
    if "IStorageUiProvider" in _CCODE and "IStorageUiRegion" in _CCODE:
        ok("外部消费者实现了两个公开接口")
    else:
        err("外部消费者没实现公开接口 —— 那它就没在验证接入面")
    _iface_miss = [m for m in ("public bool WantsRegion(", "public int RegionOrder(",
                               "public ISleekElement RegionPanel(") if m not in _CCODE]
    if not _iface_miss:
        ok("区块三件套在外部程序集里可实现（WantsRegion / RegionOrder / RegionPanel）")
    else:
        err("外部消费者缺区块成员：" + "、".join(_iface_miss))
    # 常驻纪律同样适用于第三方：只藏不拆
    if "panel.IsVisible = false" in _CCODE and "RemoveChild(panel)" in _CCODE:
        ok("外部消费者自己遵守「只藏不拆」（关掉时藏，丢弃时才 RemoveChild）")
    else:
        err("外部消费者的常驻策略没写对 —— 第三方照它抄就会每次重开箱子重载图标")

    # 每个公开入口都要被真调用过（否则那条路等于没验证）
    CALLS = [
        ("StorageUiRegistry.Register", "注册区块"),
        ("StorageUiRegistry.Unregister", "注销区块"),
        ("StorageUiRegistry.SetRegionOrder", "外部改顺序"),
        ("StorageUiRegistry.ClearRegionOrder", "撤销顺序覆盖"),
        ("StorageUiRegistry.HasRegionOrderOverride", "读有没有覆盖"),
        ("StorageUiRegistry.SetRegionVisible", "外部关/开某块"),
        ("StorageUiRegistry.ClearRegionVisibility", "按来源一次撤销"),
        ("StorageUiRegistry.IsRegionHidden", "读是否被关"),
        ("StorageUiRegistry.RegionHidingSources", "查是谁关的"),
        ("StorageUiRegistry.RequestRegionsChanged", "请宿主重算"),
        ("StorageUiRegistry.RegionIds", "布局清单"),
        ("StorageUiRegistry.EffectiveRegionOrder", "读生效顺序"),
        ("StorageUiRegistry.StorageRegionOrder", "常量：存储位"),
        ("StorageUiRegistry.RegionOrderAboveStorage", "常量：之上"),
        ("StorageUiRegistry.RegionOrderBelowStorage", "常量：之下"),
        ("StorageBlueprintFilters.Add", "挂配方判据"),
        ("StorageBlueprintFilters.Remove", "撤配方判据"),
        ("StorageBlueprintFilters.ByCategory", "便捷判据：类别"),
        ("StorageBlueprintFilters.MatchesCategory", "类别判据本体"),
        ("StorageBlueprintFilters.MatchesProduct", "产物判据本体"),
        ("StorageBlueprintFilters.Version", "改动版本（进指纹）"),
        ("StorageBlueprintFilters.Count", "来源数"),
        ("StorageUiPanels.AttachSpot", "公开挂载门面"),
        ("StorageUiContext", "上下文类型"),
        ("[BepInDependency(", "声明依赖 CustomUI"),
    ]
    _missing = [lbl for api, lbl in CALLS if api not in _CCODE]
    if _missing:
        err("外部消费者没覆盖这些公开面：" + "、".join(_missing))
    else:
        ok("公开面 %d 项全部被外部插件真实调用（含常量与门面）" % len(CALLS))

    # ⛔ 门禁：不许写死自检的分母（坑 #53 = 把能算出来的数写死在标签里；坑 #64 = 它的注释版）。
    #   ⚠ 只扫**本仓库自己的**当前状态区（README §7）—— §8 坑表与 §10 历史区里引用当时
    #     日志的数字是**历史事实**，不能删也不该判违规；外部消费者自己的分母是别人家的事。
    _rd22 = ""
    _rp22 = os.path.join(HERE, "开发文档.md")
    if os.path.exists(_rp22):
        with io.open(_rp22, "r", encoding="utf-8") as f:
            _rd22 = f.read()
    _s7 = ""
    _a7 = _rd22.find("## 7. 验收清单")
    _b7 = _rd22.find("## 8. 坑清单")
    if _a7 >= 0 and _b7 > _a7:
        _s7 = _rd22[_a7:_b7]
    _hardcoded = []
    if _s7:
        for _mm in re.finditer(r"自检\s*\d+\s*/\s*\d+|断言\s*\d+\s*条|\d+\s*条断言", _s7):
            _hardcoded.append("README §7：" + _mm.group(0))
    if not _hardcoded:
        ok("README §7 没写死自检的分母（分母是算出来的 —— 写死会随断言增减而腐烂，坑 #53 / #64）")
    else:
        err("README §7 有写死的自检分母/条数：" + " · ".join(_hardcoded[:5]) + "（坑 #53 / #64）")

# -------------------- 23. 公开的网格组件（StorageGridPanel，B1）
out.append("")
out.append("23) 公开网格组件（StorageGridPanel）")

gp = src.get("CustomUI.GridPanel.cs", "")
gvc = src.get("CustomUI.GridView.cs", "")
if not gp:
    err("缺 CustomUI.GridPanel.cs —— 第三方没有画网格的公开组件")
else:
    if "public class StorageGridPanel" in gp:
        ok("组件是 public（外部程序集可 new）")
    else:
        err("StorageGridPanel 不是 public —— 又变成只有内置视图能用")

    # ⚠⚠ 复用格子**只复用内容，不复用几何** —— 位置/宽度每轮都要重算。
    #   复用条件是"物品指纹没变"，可"物品没变 ≠ 版面没变"：Columns 会因形态切换而改
    #   （数字箱：右列预览 4 列、借道界面 6 列）。不重算的实测症状是
    #   "交互过的那格变窄、同一行里混着两种宽度、中间出现空洞"。
    if "ApplyCellGeometry(cell, slot, columns, cellScaleX)" in strip_comments(gp):
        ok("复用的格子每轮重算几何（列数变了不会出现两种宽度混排）")
    else:
        err("复用分支没重算格子几何 —— 改 Columns 后会出现宽度/位置错位的空洞")
    # 位置与宽度必须用同一个**钳过的**列数：原先位置用字段 Columns、宽度用 cellScaleX，
    # Columns=0 时 `slot % Columns` 直接除零。
    #   ★ 26.10.3.66：这两个助手不再是 `static` —— 纵向位置要读 `CellPitch()`（取决于外观旋钮）。
    _geo = re.search(r"private void ApplyCellGeometryTo\([\s\S]*?\n        \}", gp)
    if _geo is None:
        err("找不到 ApplyCellGeometryTo")
    elif "slot % columns" in _geo.group(0) and "slot / columns" in _geo.group(0) \
            and "SizeScale_X = cellScaleX" in _geo.group(0) and "columns < 1" in _geo.group(0):
        ok("格子几何只用钳过的 columns（不再出现 slot % Columns 的除零）")
    else:
        err("ApplyCellGeometryTo 又混用了字段 Columns 与钳过的 cellScaleX")
    if "slot % Columns" not in strip_comments(gp) and "slot / Columns" not in strip_comments(gp):
        ok("全文件不再有用字段 Columns 直接算行列的地方")
    else:
        err("还有地方用未钳过的 Columns 算行列 —— Columns<1 时会除零或与宽度不一致")

    # ★ 一份实现：机制搬进组件后，GridView 里不许再留第二套
    gvc_code = strip_comments(gvc)
    _dups = []
    for _dup, _why in (("new SleekItemIcon()", "图标重建规则"),
                       ("class CellUi", "格子池结构"),
                       ("epoch++", "面板世代号"),
                       ("CreateScrollView()", "滚动容器")):
        if _dup in gvc_code:
            _dups.append(_dup + "(" + _why + ")")
    if _dups:
        err("GridView 里又出现 " + "、".join(_dups) + " —— 同一套机制被复制成两份，迟早漂移")
    else:
        ok("GridView 只剩 Provider 生命周期，网格机制只有一份实现")

    # 组件自己必须守住那几条铁律
    _hide = fn_body(gp, "public void Hide(", "Panel.Hide")
    if _hide is not None:
        _hide_code = strip_comments(_hide)
        if "panel.IsVisible = false" in _hide_code and "RemoveChild" not in _hide_code:
            ok("Hide 只藏不拆（外部照它抄才不会每次重开都重载图标）")
        else:
            err("Hide 写法不对 —— 要么销毁了面板，要么根本没藏")
        if "groups.Clear()" in _hide_code and "cells.Clear()" not in _hide_code:
            ok("Hide 清数据、留控件池")
        else:
            err("Hide 把控件池也清了 —— 常驻复用等于白做")
    _dis = fn_body(gp, "public void Discard(", "Panel.Discard")
    if _dis is not None and "RemoveChild" in strip_comments(_dis) and "cells.Clear()" in _dis:
        ok("Discard 才是真销毁（并作废整池，下次 Build 自增 epoch）")
    else:
        err("Discard 没做真销毁/清池 —— 与 Hide 的分工模糊，会留下用得起的坏控件")
    # ⚠ fn_body 的签名串**不能带参数表**：它自己会再接 `[^)]*\)`，
    #   写成 "IsUsableAt(ISleekElement spot)" 就永远匹配不上（本项目第二次踩这条）。
    _usa = fn_body(gp, "public bool IsUsableAt", "Panel.IsUsableAt")
    if _usa is not None and "ReferenceEquals(panel.Parent, spot)" in strip_comments(_usa):
        ok("IsUsableAt 比对传入的挂载点实例（外部插件用 StorageUiPanels.AttachSpot 就能自检）")
    else:
        err("IsUsableAt 判据不足 —— 切关后会复用已随旧画布销毁的面板")
    _bld = fn_body(gp, "public void Build(", "Panel.Build")
    if _bld is not None and "if (panel != null) return;" in _bld:
        ok("Build 幂等（反复调不会自增 epoch → 不会白毁图标）")
    else:
        err("Build 不幂等 —— 每帧调就会每代作废全部格子")

    # ★ 页号安全闸：数据源不是当前打开的容器时必须降级为只读
    _ri = fn_body(gp, "private bool ResolveInteractive", "ResolveInteractive")
    if _ri is None:
        err("缺 ResolveInteractive —— 无法确认点击搬运有没有按页号挡一道")
    else:
        _ri_code = strip_comments(_ri)
        if "ReferenceEquals(open, source)" in _ri_code and \
                "items[PlayerInventory.STORAGE]" in _ri_code:
            ok("搬运按「数据源 == 当前打开的容器」放行（否则坐标会指到别的箱子上）")
        else:
            err("搬运没有页号安全闸 —— 给一个别的 Items 就会点 A 箱改到 B 箱（且自动落盘）")
        if "if (!Interactive) return false;" in _ri_code:
            ok("Interactive 关掉时直接只读")
        else:
            err("Interactive 字段没被尊重")
        if "warnedPassive" in _ri_code:
            ok("降级为只读时记一次日志（不每帧刷）")
        else:
            err("静默降级 —— 第三方会以为自己的面板坏了")

    # 公开面必须够用（外部只靠这些就能挂一块网格）
    for _sig, _nm in (("public ISleekElement Element", "Element"),
                      ("public void Refresh(Items source)", "Refresh(Items)"),
                      ("public bool IsBuilt", "IsBuilt"),
                      ("public float DesiredHeight", "DesiredHeight"),
                      ("public int Columns", "Columns"),
                      ("public float MaxListHeight", "MaxListHeight"),
                      ("public string Title", "Title")):
        if _sig in gp:
            ok("公开成员齐：" + _nm)
        else:
            err("公开成员缺 " + _sig)
    # ⚠ internal 类型不得出现在 public 签名里（否则外部根本编译不过 —— 坑 #45 同一类）
    _leak = re.findall(r"public [^\n;]*StorageUiShared\.", gp)
    if _leak:
        err("public 签名里泄漏了 internal 类型：" + _leak[0])
    else:
        ok("public 签名不泄漏 internal 类型（ItemGroup 只留在内部）")

    # 外部消费者要真的用上它
    if not _CHAVE:
        skip("没扫到外部消费者 —— 跳过「公开网格组件有没有被外部用上」")
    elif "StorageGridPanel" in _CMERGED:
        ok("外部消费者用上了公开网格组件（外部可用性被真实调用）")
    else:
        err("没有外部消费者用 StorageGridPanel —— 组件的外部可用性没人验证（坑 #45 的成因）")

    _rt23 = ""
    _rp23 = os.path.join(HERE, "开发文档.md")
    if os.path.exists(_rp23):
        with io.open(_rp23, "r", encoding="utf-8") as f:
            _rt23 = f.read()
    if "StorageGridPanel" in _rt23:
        ok("开发文档 写了公开网格组件")
    else:
        err("README 缺 StorageGridPanel —— 第三方不知道有现成组件可用")

    # ---- ★ 26.10.3.27：公开面「半截 API」总清理（能力缺口盘点 §2.1-2.4）----
    # 这一族的总形状：组件承诺了一个旋钮/回调，但它**只在"重建那一刻"被读**，
    # 而重建只在内容指纹变化时发生 ⟹ 数据没变、只改旋钮，画面一动不动。
    # 接入方看到的是「我设了怎么没反应」，而且无处可查。
    # ⟹ 现在的契约是「属性 setter 值一变就自动 MarkDirty()」（下一帧生效）。
    _k27 = strip_comments(gp)

    _k27_leak = re.findall(
        r"public\s+(?:int|float|string|bool|StorageSlotHitHandler)\s+"
        r"(Columns|MaxListHeight|Title|EmptyText|TooltipHint|Interactive|"
        r"OnSlotClicked|OnSlotRightClicked)\s*(?:=|;)", _k27)
    if _k27_leak:
        err("这些旋钮又变回 public 字段了：" + "、".join(sorted(set(_k27_leak))) +
            " —— 字段没法在赋值时标脏，接入方改完不生效（§2.1/§2.3 那一族）")
    else:
        ok("外观旋钮与槽位回调都是属性（不是字段）—— 有地方可以在赋值时标脏")

    _k27_bad = []
    for _p27 in ("Columns", "MaxListHeight", "Title", "EmptyText", "TooltipHint",
                 "Interactive", "OnSlotClicked", "OnSlotRightClicked"):
        # ⚠ 判据只该锁"它是属性 + setter 会标脏"，**不锁写成几行** ——
        #   26.10.3.47 把 Columns/MaxListHeight 的 setter 从单行展开成块状（加了滚动位置保留），
        #   旧判据（写死 `get {…}\n set {…}` 的行版式）当场 FAIL，而事实一条没变。
        _m27 = re.search(r"public [^\n]*\b" + _p27 + r"\b\s*\n\s*\{([\s\S]{0,700}?)\n        \}",
                         _k27)
        if _m27 is None:
            _k27_bad.append(_p27 + "(没找到属性块)")
        elif "get" not in _m27.group(1) or "set" not in _m27.group(1):
            _k27_bad.append(_p27 + "(不是 get/set 属性)")
        elif "MarkDirty()" not in _m27.group(1):
            _k27_bad.append(_p27 + "(setter 没标脏)")
        elif "value" not in _m27.group(1):
            _k27_bad.append(_p27 + "(setter 没比对新值)")
    if _k27_bad:
        err("这些旋钮的 setter 不合格：" + "、".join(_k27_bad) +
            " —— 要么不会生效，要么每次赋值都标脏（下游侧每帧重设 = 每帧重建）")
    else:
        ok("八个旋钮/回调的 setter 都是「值变了才 MarkDirty()」（设了即生效，且不会每帧重建）")

    # ---- ★ 26.10.3.47：滚动位置保留（换模式 / 重拆面板时不许把玩家弹回第一行）----
    #   用户 2026-10-08 报：数字箱预览（4 列）点一下 → 关箱开借道页（6 列），
    #   "重置了整页，滑条位置变为最顶部同时页面也弹回了最顶部"。
    #   引擎在版面变化 / 面板重建时会把滚动位置弹回顶部 ⟹ 组件必须自己记一次再写回。
    _scroll_bad = []
    for _sf, _sn in (("CustomUI.GridPanel.cs", "网格面板"), ("CustomUI.ListPanel.cs", "列表面板")):
        _sc = strip_comments(src.get(_sf, ""))
        # ① 两个公开成员必须都在（第三方也要能用同一套）
        if "public void SaveScrollPosition()" not in _sc \
                or "public void RestoreScrollPosition()" not in _sc:
            _scroll_bad.append(_sn + "（缺 Save/RestoreScrollPosition）")
            continue
        # ② 写回必须用**可写**的 NormalizedStateCenter —— NormalizedVerticalPosition 在接口里是只读的
        if "NormalizedStateCenter =" not in _sc:
            _scroll_bad.append(_sn + "（没用可写的 NormalizedStateCenter 写回）")
        # ③ Discard 里必须**先记**（拆完就读不到位置了）
        _d = fn_body(_sc, "public void Discard", _sn + ".Discard")
        if _d is None or "SaveScrollPosition()" not in _d:
            _scroll_bad.append(_sn + "（Discard 里没先记位置）")
        # ④ 版面旋钮变了也要记（不是只有"拆面板"才记）
        for _knob in ("_columns", "_maxListHeight"):
            if _knob + " != value" in _sc:
                _blk = re.search(r"if \(" + _knob + r" != value\)\s*\n\s*\{([\s\S]{0,400}?)\}",
                                 _sc)
                if _blk is None or "SaveScrollPosition()" not in _blk.group(1):
                    _scroll_bad.append(_sn + "（" + _knob + " 变了没记位置）")
        # ⑤ 写回必须**有帧数上限**且**要求已挂载**（不能每帧死写 / 挂载前写了个零）
        _ap = fn_body(_sc, "private void ApplyPendingScroll", _sn + ".ApplyPendingScroll")
        if _ap is None:
            _scroll_bad.append(_sn + "（缺 ApplyPendingScroll）")
        else:
            if "SCROLL_RESTORE_MAX_TRIES" not in _ap:
                _scroll_bad.append(_sn + "（写回没有帧数上限 —— 会每帧死写）")
            if "panel.Parent == null" not in _ap:
                _scroll_bad.append(_sn + "（挂载前也写 —— 那时写进去的是个零）")
    if _scroll_bad:
        err("滚动位置保留不合格：" + "、".join(_scroll_bad))
    else:
        ok("网格/列表组件在「拆面板 / 改版面」时先记滚动位置，挂载后写回（且写回有帧数上限）")

    # ⚠ 原来这里查「外部消费者的四个演示区块默认关闭」（读别人家插件的 cfg 绑定）——
    #   那属于"别人家自己的文件"，判据整段删除（不在本仓库门禁内）。

    _m27_empty = re.search(
        r"if \(count == 0\)([\s\S]{0,900}?)else if \(emptyLabel != null\)", _k27)
    if _m27_empty is None:
        err("找不到空箱分支 —— EmptyText 这条判据要跟着改")
    elif re.search(r"scroll\.AddChild\(emptyLabel\);[\s\S]{0,400}?emptyLabel\.Text = EmptyText;",
                   _m27_empty.group(1)):
        ok("EmptyText 每轮同步（改了下一帧生效，不再只在 label 创建那一刻读一次）")
    else:
        err("EmptyText 仍然只在 label 创建那一刻被读 —— 改完永不生效（§2.1）")

    # ★ 26.10.3.32：解析（认领哪一件）与交付（填 StorageSlotHit + 调回调 + 隔离异常）
    #   搬进了共享层 —— 网格与列表用**同一份**，两处不会各自漂移。
    _ria27 = fn_body(sh, "internal static Item ResolveItemAt", "ResolveItemAt")
    _dsh27 = fn_body(sh, "internal static void DeliverSlotHit", "DeliverSlotHit")
    if _ria27 is not None and "getIndex(" in strip_comments(_ria27):
        ok("二维 Items 源按坐标认领（getIndex 自带边界检查、越界返回 255）")
    else:
        err("ResolveItemAt 没了或没走 getIndex —— 二维源的点击会落进过期分支"
            "（承诺与实现相反，§2.2）")
    if _ria27 is not None and "jar.item.id != boundItemId" in strip_comments(_ria27):
        ok("坐标认领前核对物品身份（坐标过期时宁可不动手，也不会点 A 改 B）")
    else:
        err("坐标认领没核对身份 —— 插件重建后改过容器时会拿错物品（坑 #55 取向）")
    # ★ 26.10.3.51：字段映射（ItemId/Amount/Quality）**搬进了 `MakeSlotHit`** ——
    #   判据跟着搬（判"有没有"要看实现搬到哪儿了，不能还钉在旧的调用点函数体里）。
    _msh27 = fn_body(sh, "internal static StorageSlotHit MakeSlotHit(", "MakeSlotHit")
    _d27 = strip_comments(_msh27) if _msh27 is not None else ""
    if "hit.SlotIndex = slotIndex;" in _d27 and "hit.X = x;" in _d27 and "hit.Y = y;" in _d27:
        ok("扁平/二维两套身份互斥地填进 StorageSlotHit（下标与坐标不混用）")
    else:
        err("StorageSlotHit 的下标/坐标填充不对 —— 插件分不清该用哪一套")
    # 反向锁：交付那条路必须**用它**（两份字段映射迟早漂）
    _dsh27b = strip_comments(_dsh27) if _dsh27 is not None else ""
    if "MakeSlotHit(" in _dsh27b:
        ok("交付给回调的那份命中信息也走 MakeSlotHit（字段映射只此一处）")
    else:
        err("DeliverSlotHit 自己又拼了一遍字段映射 —— 两个入口对同一格会给出不同的 id/数量")
    if "StorageUiShared.DeliverSlotHit" in gp and "StorageUiShared.DeliverSlotHit" in lv2:
        ok("网格与列表两个面板共用**同一份**交付逻辑（不会各自漂移）")
    else:
        err("两个面板没共用交付逻辑 —— 同一语义两份实现迟早漂移")
    if "cell.BoundItemId = jar.item.id;" in _k27:
        ok("重建格子时记下物品 id（供二维回调核对）")
    else:
        err("CellUi 没记 BoundItemId —— 二维回调的过期核对没有依据")

    if "pageClick && HasSlotCallbacks" in _k27:
        ok("「数据源就是打开的容器 + 挂了回调」时显式提示（回调不会被静默吃掉）")
    else:
        err("回调被内置搬运静默遮蔽 —— 接入方挂了回调却什么都没发生，且查不出原因")

    _rc27 = fn_body(gp, "private void ResetClickWarnings", "ResetClickWarnings")
    if _rc27 is not None:
        _r27 = strip_comments(_rc27)
        _miss27 = [n for n in ("_slotHandlerWarned", "_staleSlotWarned",
                               "_callbackShadowedWarned", "warnedPassive") if n not in _r27]
        if _miss27:
            err("ResetClickWarnings 漏了：" + "、".join(_miss27))
        else:
            ok("一次性警告能被复位（同一会话内仍只报一次，但不会永久静音）")
    _hide27 = fn_body(gp, "public void Hide(", "Panel.Hide")
    _dis27 = fn_body(gp, "public void Discard(", "Panel.Discard")
    if _hide27 and _dis27 and "ResetClickWarnings()" in strip_comments(_hide27) \
            and "ResetClickWarnings()" in strip_comments(_dis27):
        ok("Hide / Discard 都复位警告（关一次面板 = 一次会话结束）")
    else:
        err("Hide 或 Discard 没复位警告 —— 关掉重开后仍然完全无声")

    # ⑦ 验收入口必须真的存在 —— 否则 §7 第 179-181 条无从验证（判据与验收项要配套）。
    #   ⚠ 179 尤其要按钮：**改代码重编只能证明"新默认值生效"**（重启后首次 Refresh
    #     本来就会重建），证明不了"数据一个字节没变、只改旋钮也生效" —— 而那正是本轮修的。
    #   ⚠ 入口演示的是**接入方真会做的事**（改 Title / 清空自己的数据源），
    #     不是"挑一个刚好能看出变化的旋钮"（用户 2026-10-05 明确否掉了拿 Columns 充数）。
    if not _CHAVE:
        skip("没扫到外部消费者 —— 跳过「验收入口」两条")
    else:
        if "grid.OnSlotClicked" in _CMERGED and "OnSlotRightClicked" in _CMERGED:
            ok("外部消费者给「二维 Items 源 + 回调」挂了真回调（§7 第 181 条的验收入口）")
        else:
            err("没有外部消费者挂二维源的回调 —— §7 第 181 条将无从验证")
        if "Grid.Title" in _CMERGED:
            ok("外部消费者有个能当场改公开旋钮的入口（§7 第 179/180 条的验收入口）")
        else:
            err("没有外部消费者在界面开着时改旋钮 —— §7 第 179/180 条证不了它们该证明的事")

    # ---- ★ 26.10.3.29：搜索栏（T4/T5）—— 那个零调用者的半成品终于接线了 ----
    # 交接文档第四十二轮留的话：「`MatchesQuery` 现在**零调用者**，接上才有效果」。
    # 这条判据就是守「它真的有调用点」，否则搜索框是个摆设。
    _sh29 = src.get("CustomUI.Shared.cs", "")
    _has_query_call = False
    for _fn29, _txt29 in src.items():
        if _fn29 == "CustomUI.Shared.cs":
            continue                      # 定义在自己文件里不算「被调用」
        if "MatchesQuery(" in strip_comments(_txt29):
            _has_query_call = True
            break
    if _has_query_call:
        ok("`MatchesQuery` 有真实调用点（搜索不再是个摆设）")
    else:
        err("`MatchesQuery` 仍然零调用者 —— 搜索框接不上，等于没做")

    # ⚠ 过滤必须在 **CollectGroups 之前**：过滤的是「哪些条目参与分组」。
    #   「分完组再藏几格」会让头部件数/占用与画面不符（「同一个数只在一处打折」那一族）。
    _rb29 = fn_body(gp, "private void RebuildCells", "RebuildCells")
    if _rb29 is not None:
        _r29 = strip_comments(_rb29)
        _posFilter = _r29.find("searchBox.Query")
        _posGroup = _r29.find("CollectGroups(")
        if _posFilter >= 0 and _posGroup > _posFilter:
            ok("搜索过滤排在 CollectGroups **之前**（过滤的是「哪些条目参与分组」）")
        else:
            err("过滤的位置不对 —— 要么没接线，要么在分组之后（头部数字会与画面对不上）")
        if "筛选中：显示" in _r29:
            ok("筛选中时头部显式说明（否则「件数变少」看起来像物品丢了）")
        else:
            err("筛选中没有头部说明 —— 玩家核对不上会以为丢了东西")

    # ⚠ 唯一的 jar 收集点：取证与过滤必须走同一条路。
    #   否则谁为了「先筛再分组」自己写一遍遍历，取证就**静默漏掉**了。
    _cg_items = fn_body(_sh29, "internal static void CollectGroups(", "CollectGroups(Items)")
    if _cg_items is not None and "CollectJars(" in strip_comments(_cg_items):
        ok("`CollectGroups(Items)` 走唯一的收集点 `CollectJars`（取证与过滤同一条路）")
    else:
        err("`CollectGroups(Items)` 自己遍历了 —— 收集逻辑又变成两份，取证迟早漏")
    _cj29 = fn_body(_sh29, "internal static void CollectJars(", "CollectJars")
    if _cj29 is not None:
        _c29 = strip_comments(_cj29)
        if "AuditStackAmount(" in _c29:
            ok("收集点里带着取证（非法堆叠不会因为「先筛再分组」而静默漏掉）")
        else:
            err("收集点没有取证 —— 非法堆叠会漏报")
        if "string.IsNullOrEmpty(query)" in _c29:
            ok("空查询 = 不过滤（搜索框没内容时不该把界面清空）")
        else:
            err("空查询的处理不对 —— 没内容时界面会被清空")

    # ⚠ ShowSearch 改了必须**重建面板**（输入框是 Build 里建的）——
    #   只标脏只重建格子，那一行输入框永远不出现（「设了没反应」的老毛病）。
    _ss29 = re.search(r"public bool ShowSearch[\s\S]{0,700}?\n        \}", _k27)
    if _ss29 is not None and "Discard()" in _ss29.group(0):
        ok("`ShowSearch` 改了会重建面板（输入框才可能出现，不是「设了没反应」）")
    else:
        err("`ShowSearch` 只标脏 —— 输入框永远建不出来")
    # ★ 26.10.3.46：输入框本体搬进公开组件后，"挂回调"这件事变成"把组件回调接上"——
    #   判据锁的事实没变（**打了字要能筛**），只是钩子的名字换了。
    if "searchBox.OnQueryChanged = OnSearchTextChanged" in _k27:
        ok("输入框接上了文本回调（打了字会筛；控件本体归 StorageSearchBox 管）")
    else:
        err("输入框没接文本回调 —— 打了字不会筛")
    if "grid.ShowSearch = true" in src.get("CustomUI.GridView.cs", ""):
        ok("内置大格子视图开了搜索栏")
    else:
        err("内置视图没开 ShowSearch —— 玩家看不到搜索框")

    # ★ 26.10.3.46 反向锁：三个视图**都不许**再自建输入框 —— 消重要真消掉，
    #   否则第四份复制品迟早出现（坑 #68 的通用形状：同一事实写在 N 个地方）。
    #   ⚠ 必须 strip_comments 后再扫：三处的**新注释**里正解释着
    #     「原来这段 `CreateStringField()` 与另两个视图逐字相同」——
    #     不剔注释的话，判据会扫到自己的说明文字而误报（坑 #8「别锚在注释上」的现成例子）。
    _selfbuilt = []
    for _sn, _st in (("网格面板", gp), ("列表面板", lv2), ("蓝图视图", bv)):
        if "CreateStringField()" in strip_comments(_st):
            _selfbuilt.append(_sn)
    if _selfbuilt:
        err("这些视图又在自建输入框了：" + "、".join(_selfbuilt)
            + " —— 输入框本体只该在 StorageSearchBox 里建那一份")
    else:
        ok("三个视图都不再自建输入框（`CreateStringField()` 只剩公开组件里那一份）")

    # ---- ★ 26.10.3.30：拼音匹配改成「任意前缀」----
    # 用户实测报的：「金属板」`jin`✓ `jins`✓ 但 **`jinsh`✗** `jinshu`✓，
    # 打字过程中结果一会儿有一会儿没。真因：`Consume` 只有「吃掉整个读音」与
    # 「只吃首字母」两条路，而 `sh` 两头不靠 ⟹ **破坏了前缀单调性**。
    # ⟹ 改成"这个字可以贡献它读音的 1..全长 个字符"（两条路分别是它的特例）。
    # 实测证据：一次性接入自检跑 32 条断言全过（ji/jinsh/jshuban/jsban/jinsb/jshuba + 单调性 + 不该匹配的）。
    _cs30 = fn_body(_sh29, "private static bool Consume", "Consume")
    if _cs30 is not None:
        _c30 = strip_comments(_cs30)
        if re.search(r"for \(int take = 1; take <= max; take\+\+\)", _c30):
            ok("拼音匹配允许「任意长度前缀」（一个字的读音可贡献 1..全长 个字符）")
        else:
            err("拼音匹配退回了两条路（整读音 / 只吃首字母）—— 前缀不单调，打字时结果会跳")
        if "break;" in _c30:
            ok("前缀不匹配时 break 剪枝（不再试更长的 take）")
        else:
            err("前缀不匹配时没剪枝 —— 无谓回溯")
        if "Substring(0, 1)" not in _c30:
            ok("不再单独走「只吃首字母」特判（它已是 take=1 的特例）")
        else:
            err("还留着「只吃首字母」特判 —— 与 take=1 重复，两套写法迟早漂移")
    if "MatchesPrefix(" not in _sh29:
        ok("死代码 `MatchesPrefix` 已删除（「任意前缀」把它的职责吃掉了）")
    else:
        err("`MatchesPrefix` 还在 —— 要么有人又用回去了，要么是死代码在误导下一轮")

# ★ 26.10.3.43：两个新公开面 —— 过滤条组件 + 搜索匹配门面。
#   动机：第三方能"筛数据"（StorageBlueprintFilters 公开）却**给不了用户一个筛选按钮**，
#   因为按钮做在内置视图里；以及"自己画界面"时拿不到**匹配规则**（MatchesQuery 原本 internal）。
#   见 README §4.24。
_fbsrc = src.get("CustomUI.FilterBar.cs", "")
_fbsc = src.get("CustomUI.Search.cs", "")
if not _fbsrc:
    err("找不到公开组件 StorageFilterBar 的源文件（CustomUI.FilterBar.cs）")
else:
    if "public class StorageFilterBar" in _fbsrc:
        ok("公开组件 StorageFilterBar 存在（第三方能给自己的界面配一条筛选按钮条）")
    else:
        err("StorageFilterBar 不是 public —— 第三方拿不到，等于没做")
    # 公开面要与另两个组件**同一套**（挂得上、量得到高度、能藏能拆、有数据口）
    _bar_api = ("public ISleekElement Element", "public bool IsBuilt", "public float DesiredHeight",
                "public void Build(", "public void Hide()", "public void Discard()",
                "public void SetOptions(", "public bool HasSelection", "public int SelectedKey")
    _bar_miss = [a for a in _bar_api if a not in _fbsrc]
    if not _bar_miss:
        ok("过滤条的公开面与网格/列表面板**同一套**（Element/IsBuilt/DesiredHeight/Build/Hide/Discard + 数据口）")
    else:
        err("过滤条公开面缺：" + "、".join(_bar_miss) + " —— 与另两个组件不是一套控制粒度")
    if "public delegate void StorageFilterHandler(bool hasSelection, int key)" in _fbsrc:
        ok("回调签名带 hasSelection + key（取消筛选也能报出来，且按 key 不按下标）")
    else:
        err("过滤条的回调签名没带 hasSelection/key —— 接入方没法区分「取消」和「选了别的」")
if not _fbsc:
    err("找不到公开搜索匹配的源文件（CustomUI.Search.cs）")
else:
    if "public static class StorageUiSearch" in _fbsc \
            and "public static bool Matches(ItemAsset asset, string query)" in _fbsc:
        ok("公开搜索匹配 StorageUiSearch.Matches 存在（第三方自绘界面能复用同一套匹配规则）")
    else:
        err("StorageUiSearch.Matches 不是公开的 —— 第三方只能自己再写一套（同一个箱子两种手感）")
    # ★ 单一实现：门面**只许转发**共享层那一份，不许自己再写匹配规则
    _smb = fn_body(_fbsc, "public static bool Matches(ItemAsset asset, string query", "Search.Matches")
    if _smb is not None and "StorageUiShared.MatchesQuery(" in _smb:
        ok("搜索门面**只转发**共享层那一份实现（两份匹配规则一定会漂）")
    else:
        err("搜索门面没转发 StorageUiShared.MatchesQuery —— 它自己又写了一套匹配规则")
    if "PinyinMatches" not in _fbsc:
        ok("搜索门面里没有第二份拼音实现（拼音只在共享层有一份）")
    else:
        err("搜索门面里出现了拼音实现 —— 两份拼音规则必然漂")

# -------------------- 24. 扁平槽位入口（E'-1）
out.append("")
out.append("24) 扁平槽位入口（StorageSlotList）")

sh24 = src.get("CustomUI.Shared.cs", "")

# ★ 合并核心必须只有一份：Items 入口只许"摊平成 jar 再委托"，
#   不许把「什么叫同一件物品」再写一遍（本项目反复栽过的两份实现）。
_items_entry = fn_body(sh24, "internal static void CollectGroups(Items container", "CollectGroups(Items)")
_core_entry = fn_body(sh24,
    "internal static void CollectGroups(List<ItemJar> jars, List<ItemGroup> into, bool idOnly,",
    "CollectGroups(合并核心)")
if _items_entry is None or _core_entry is None:
    err("CollectGroups 的两个入口没找齐（签名变了要同步判据；注意签名串不要带参数表）")
else:
    if "CollectGroups(_jarBuf, into)" in _items_entry and "GroupKey(" not in _items_entry:
        ok("Items 入口只摊平 + 委托，合并逻辑没有第二份")
    else:
        err("Items 入口里又写了合并逻辑 —— 「同一件物品」会有两个答案（迟早漂移）")
    if ("GroupKey(jar, idOnly" in _core_entry) \
            and "into.Sort(CompareGroup)" in _core_entry:
        ok("合并核心含分组键与排序（同序规则只有一处定义）")
    else:
        err("合并核心缺分组或排序 —— 两条数据路径会不同序")
    if "jar.item.state" not in _core_entry:
        ok("合并核心不含 item.state（state 进判据就永远合并不了，且会每帧抖动）")
    else:
        err("合并核心把 state 当判据了")

# 摊平用的缓冲：必须复用，不能每帧 new
# ⚠ 26.10.3.29：摊平逻辑搬进了 `CollectJars`（唯一的收集点），
#   缓冲的 `Clear()` 现在在那里对 `into` 做 —— 判据跟着搬家（别锁旧位置）。
_cj_buf = fn_body(sh24, "internal static void CollectJars(", "CollectJars")
if "private static readonly List<ItemJar> _jarBuf" in sh24 and _cj_buf is not None \
        and "into.Clear()" in _cj_buf and "new List<ItemJar>" not in _cj_buf:
    ok("摊平 jar 用复用缓冲（每帧不产生垃圾）")
else:
    err("摊平 jar 每次新建列表 —— 每帧分配，白白制造 GC 压力")

# ---- 【取证】真实页面上的非法堆叠（amount > ItemAsset.MaxAmount）------------------
#   原版造不出这种数据（Item(id,origin) 取 MaxAmountAsByte；合成产物逐件给；
#   Items 放入不做部分合并），而原版界面只在 MaxAmount>1 才印 "x数量" ⟹ 玩家看不见。
#   本插件**只报不改**：拆堆要在别人的容器里腾格子，那是持有方的决定。
_aud = fn_body(sh24, "private static void AuditStackAmount", "AuditStackAmount")
if _aud is None:
    err("缺 AuditStackAmount —— 非法堆叠又变成只能靠猜")
else:
    if "it.amount <= 1" in _aud:
        ok("取证只对 amount>1 的条目查资产（常见物品恒 1，等于零开销）")
    else:
        err("取证没先按 amount>1 短路 —— 每个 jar 每帧都要 Assets.find")
    if "_stackCaps" in _aud and "_stackCapReported" in _aud:
        ok("取证按物品 ID 缓存上限且每种只报一次（不刷屏、不重复查资产）")
    else:
        err("取证没有缓存/去重 —— 会每帧查资产或刷屏 Warn")
    if "updateAmount" in _aud or ".amount =" in _aud:
        err("取证在改真实物品的数量 —— 它只该报告，改数据不是显示层的权限")
    else:
        ok("取证不改数据（只报告），改存档留给持有方决定")
# 只挂在**真实页**摊平路径上：数字箱那类扁平清单的 amount 是记账件数，本就可能超上限
# ⚠ 26.10.3.29：取证搬进了唯一的收集点 `CollectJars`（它与扁平入口**分工**：
#   二维页走它、扁平入口不走 —— 而取证本来就只该对"真实页"生效）。
_items_entry24 = fn_body(sh24, "internal static void CollectJars(", "CollectJars")
if _items_entry24 is None:
    err("找不到 CollectJars —— 取证挂载点判据失效")
else:
    if "AuditStackAmount(" in _items_entry24:
        ok("取证挂在真实容器摊平处（只有原版页上的物品才谈得上非法堆叠）")
    else:
        err("取证没接进收集点 —— 永远不会有证据")
if "CollectJars(container, _jarBuf)" in sh24:
    ok("`CollectGroups(Items)` 转发到收集点（没有把遍历再写一遍）")
else:
    err("`CollectGroups(Items)` 又自己遍历了 —— 收集逻辑变成两份，取证迟早漏")
    if "AuditStackAmount(" not in _core_entry:
        ok("合并核心不查上限（扁平清单的条目是记账件数，查了全是假警报）")
    else:
        err("取证放进了合并核心 —— 数字箱的合法计数会被当成非法堆叠报出来")

# 扁平头部：不得报「最大连续空位」（那需要二维占用表，报了就是假信息）
_flat_head = fn_body(sh24, "internal static string BuildHeaderTextFlat", "BuildHeaderTextFlat")
if _flat_head is None:
    err("缺 BuildHeaderTextFlat")
else:
    if "最大空位" not in _flat_head:
        ok("扁平头部不报最大连续空位（扁平模型不做二维碰撞检查，报了是假信息）")
    else:
        err("扁平头部报了最大空位 —— 那是编出来的数字，会误导玩家")
    if "占用" in _flat_head and "件" in _flat_head:
        ok("扁平头部仍有占用/件数/项数")
    else:
        err("扁平头部少了基本统计")
# 二维路径的头部不能被削弱
_head2 = fn_body(sh24, "internal static string BuildHeaderText(", "BuildHeaderText")
if _head2 is not None and "MaxFreeW" in _head2:
    ok("二维路径仍报最大连续空位（§4.1 的碎片化可见化没被削弱）")
else:
    err("二维头部丢了最大空位 —— 玩家又无法预判 2×2 放不放得下")

# 占用格数：扁平按 Σ size_x×size_y，且处理旋转
_occ = fn_body(sh24, "internal static int OccupiedCellsOf", "OccupiedCellsOf")
if _occ is not None and "rot % 2 == 1" in _occ and "size_x" in _occ:
    ok("扁平占用按 Σ size_x×size_y 且处理旋转（与引擎 resize 一致）")
else:
    err("OccupiedCellsOf 算法不对 —— 占用格数会算错")

# 面板侧：扁平恒只读 + 两条指纹各自正确
gp24 = src.get("CustomUI.GridPanel.cs", "")
# ★ 点击路由（B2-a 之后）：页号路径与回调路径**互斥**，且页号路径永不用于扁平来源。
#   原 B1 判据写的是"扁平恒只读"，B2-a 有意放宽成"扁平 + 挂了回调 → 交给插件"，
#   所以这里钉的是真正的安全边界：**回调模式下也绝不走 HandlePrimaryClick**。
if "bool pageClick = !IsFlat && ResolveInteractive(_items);" in gp24 and \
        "bool callbackClick = !pageClick && HasSlotCallbacks;" in gp24:
    ok("页号点击与回调点击互斥，且页号路径永不用于扁平来源（坑 #55 的边界仍在）")
else:
    err("点击路由不再互斥 —— 扁平来源可能被按当前箱子的坐标解释，会改到别人的箱子且自动落盘")
_rc24 = re.search(r"private void RebuildCell\([\s\S]*?\n        \}", gp24)
if _rc24 is None:
    err("取不到 RebuildCell（签名变了要同步这条判据）")
else:
    _rcb = strip_comments(_rc24.group(0))
    _page_blk = re.search(r"if \(pageClick\)\s*\{([\s\S]*?)\n            \}", _rcb)
    if _page_blk is not None and "HandlePrimaryClick" in _page_blk.group(1) \
            and "FireSlotClick" not in _page_blk.group(1):
        ok("页号分支只走共享层搬运，不混进回调")
    else:
        err("页号分支不对 —— 两条路一旦交叉，「谁改了这份数据」就说不清了")
    _cb_at = _rcb.find("if (!callbackClick) return;")
    _cb_blk = _rcb[_cb_at:] if _cb_at >= 0 else ""
    if _cb_blk and "FireSlotClick" in _cb_blk and "HandlePrimaryClick" not in _cb_blk:
        ok("回调分支不碰页号 —— 动作整个交给插件，组件只报哪一格被点")
    else:
        err("回调分支里出现页号搬运 —— 扁平槽位没有页号，会指到当前打开的箱子上")
# ★ 26.10.3.32：过期保护搬进了共享层 DeliverSlotHit（两个面板共用一份），
#   但"扁平源下标越界就拒绝回调"这条边界仍留在各面板自己的解析里。
if "cell.SlotIndex >= 0" in gp24 and "r.SlotIndex >= 0" in lv2 \
        and "点到的槽位已过期" in sh:
    ok("槽位失效时拒绝触发回调（不拿可能指错的下标/坐标去改数据）")
else:
    err("缺下标过期保护 —— 列表在重建后被改过就会取错格子")
# ★ 26.10.3.32：异常隔离跟交付逻辑一起搬进了共享层 ⟹ 判据指向那份实现。
_dsh24 = fn_body(sh, "internal static void DeliverSlotHit", "DeliverSlotHit")
if _dsh24 is not None and "catch (Exception" in strip_comments(_dsh24) \
        and "warnedThrow = true;" in strip_comments(_dsh24):
    ok("插件回调抛异常被吞掉并只报一次（不拖垮整个右列）")
else:
    err("回调异常没隔离 —— 一个插件写错就会卡住界面")
# ⚠ 签名串停在第一个 `(` 之前 —— 带参数表就永远匹配不上（fn_body 自己会再接 [^)]*\) ）
# ★ 26.10.3.32：指纹实现搬进了共享层（GridPanel 只剩一层转发）⟹ 判据指向真身。
_ff = fn_body(sh, "internal static int FingerprintJars", "FingerprintJars")
if _ff is None:
    err("缺扁平指纹函数")
else:
    if "jar.x" not in _ff and "jar.y" not in _ff:
        ok("扁平指纹不含坐标（扁平模型给的全是 0,0，带上只是白算）")
    else:
        err("扁平指纹混了坐标")
    if "item.state" not in _ff:
        ok("扁平指纹不含 item.state（同二维路径：含了就每帧重建）")
    else:
        err("扁平指纹混了 state")
# ⚠ 26.10.3.29：两条路现在都先过搜索，交给合并核心的是**过滤后的** `visible`（复用容器）
#   —— 判据跟着搬到新位置，但判的仍然是「两条路都进同一份合并核心」。
if "CollectGroups(visible, groups, _flat.MergeByIdOnly, _flat.MergeContentPages)" in gp24 and \
        "CollectGroups(visible, groups)" in gp24:
    ok("面板两条数据路径都进同一份合并核心（扁平那条可带合并策略参数）")
else:
    err("面板没走统一的合并入口")
# 换源/关闭时必须放手旧引用（否则会拿已销毁容器刷新）
_hide24 = fn_body(gp24, "public void Hide(", "Panel.Hide")
if _hide24 is not None and "_items = null" in _hide24 and "_flat = null" in _hide24:
    ok("Hide 放手数据源引用（不会攥着已销毁容器的 Items）")
else:
    err("Hide 没放手数据源 —— 下次打开若忘换源会拿旧数据刷新")

# StorageSlotList 公开面
if "public class StorageSlotList" in gp24:
    ok("StorageSlotList 是 public（外部可 new）")
else:
    err("StorageSlotList 不是 public")
for _m, _mlabel in (("public void Clear()", "Clear"),
                    ("public void Add(Item item)", "Add(Item)"),
                    ("public int CapacityCells", "CapacityCells"),
                    ("public int Count", "Count")):
    if _m in gp24:
        ok("扁平入口公开面齐：" + _mlabel)
    else:
        err("扁平入口缺公开成员 " + _m)
if "internal List<ItemJar> Jars" in gp24:
    ok("jar 列表只内部可见（不把可写引用交出去，防外部绕过 Add 塞脏数据）")
else:
    err("Jars 暴露成 public 了 —— 外部能直接改内部列表")

# 外部消费者真的按"下游形态"用它（交 Item，不是 jar）
if not _CHAVE:
    skip("没扫到外部消费者 —— 跳过「扁平槽位入口有没有被外部用上」")
else:
    if "new StorageSlotList()" in _CCODE and "slots.Add(jar.item)" in _CCODE:
        ok("外部消费者按下游形态交数据（一串 Item，不是 jar）—— 扁平入口对外真的可用")
    else:
        err("没有外部消费者用扁平入口（或交的是 jar）—— E'-1 的外部可用性没人验证")
    if "grid.Refresh(slots)" in _CCODE:
        ok("外部消费者调了 Refresh(StorageSlotList)")
    else:
        err("没有外部消费者走 Refresh(StorageSlotList) 这条路径")
    if re.search(r"CapacityCells\s*=", _CCODE):
        ok("外部消费者给了总格数（扁平模型的容量语义）")
    else:
        err("没有外部消费者设 CapacityCells —— 头部占用统计会缺分母")

_rt24 = ""
_rp24 = os.path.join(HERE, "开发文档.md")
if os.path.exists(_rp24):
    with io.open(_rp24, "r", encoding="utf-8") as f:
        _rt24 = f.read()
    if "StorageSlotList" in _rt24:
        ok("开发文档 写了扁平槽位入口")
    else:
        err("README 缺 StorageSlotList —— 下游那边不知道该怎么交数据")

# -------------------- 25. 下游接入（E'-2：外部消费者的依赖声明）
out.append("")
out.append("25) 下游接入 CustomUI（E'-2）")

# ★ 通用扫描（用户 2026-10-12 定）：本仓库**不点名任何下游插件**，也**不检查
#   「别人家插件自己的文件」** —— 那一族判据（外部 build.bat 的行尾 CRLF / 非 ASCII 字节 /
#   源文件清单、外部主文件必须零引用 CustomUI 类型的代码隔离、外部 README 的指路、
#   别处内容包 Sandbox/… 的不可堆叠不变量）**已整段删除**：它们本就不该出现在这个仓库里。
#   这里只留本仓库该管的事：① 依赖方向与消费者是否存在（缺失 ⟹ SKIP）；
#   ② 依赖声明（外部项目必须声明 `[BepInDependency("<本插件 GUID>")]`）。
if not _CHAVE:
    skip("没扫到外部消费者 —— 跳过下游接入判据（" + _CONSUMERS.summary() + "）")
else:
    # ---- 依赖声明 ----
    # ★ GUID **从真源读**（`CustomUI.cs` 的 [BepInPlugin] 第一参数），绝不硬编码第二份
    #   （同一事实写两处 = 迟早漂，见坑 #67 那一族）。
    _guid_m = re.search(r'BepInPlugin\(\s*"([^"]+)"', src.get("CustomUI.cs", ""))
    _guid = _guid_m.group(1) if _guid_m else None
    if not _guid:
        err("读不出本插件的 GUID（[BepInPlugin] 第一参数）—— 依赖声明判据失效，别静默跳过")
    else:
        _refs = _CONSUMERS.referencing(_guid)
        if not _refs:
            err("没有任何消费者引用本插件 GUID（" + _guid + "）—— 依赖方向也没人从外部钉住")
        else:
            _dep = [k for k, v in _refs.items()
                    if re.search(r'\[BepInDependency\(\s*"' + re.escape(_guid) + r'"', v)]
            _soft = [k for k, v in _refs.items()
                     if re.search(r'\[BepInDependency\(\s*"' + re.escape(_guid) + r'"\s*,\s*'
                                  r'BepInDependency\.DependencyFlags\.SoftDependency\s*\)\]', v)]
            if _dep:
                ok("外部消费者声明了对本插件的依赖：" + "、".join(sorted(_dep)))
            else:
                err("引用本插件 GUID 的消费者里没有一条 [BepInDependency] —— 依赖声明缺失")
            if _soft:
                ok("软依赖：至少一个消费者把本插件声明成 SoftDependency（没装也必须能跑）")
            else:
                err("没有消费者把本插件声明成 SoftDependency —— 硬依赖会让没装本插件的玩家直接报错")

    # ---- 方向铁律：本插件是**前置**，不许认识下游 ----
    #   判据**不写死下游名字**：拿扫到的消费者文件名（去扩展名）当探针 ——
    #   它们出现在本插件**代码**里就是依赖方向反了（注释里提一句不算，所以先剔注释）。
    _cu_code = strip_comments("".join(src.get(f, "") for f in FILES))
    _stems = sorted(set(os.path.splitext(os.path.basename(p))[0] for p in _CFILES
                        if len(os.path.splitext(os.path.basename(p))[0]) >= 6))
    _leaked = [s for s in _stems if s in _cu_code]
    if _leaked:
        err("本插件代码里出现了消费者的名字：" + "、".join(_leaked) +
            " —— 依赖方向反了（前置不认识上层，见 README §1）")
    else:
        ok("依赖方向仍是单向：本插件不认识下游（前置定位没破）")

    # ---- (a) 本插件为下游这种数据模型准备的公开入口，必须真的被外部用上 ----
    if "new StorageSlotList()" in _CCODE and "grid.Refresh(slots)" in _CCODE:
        ok("外部消费者用**扁平槽位入口**（一串 Item + 总格数）—— 公开面真的可用")
    else:
        err("没有外部消费者用扁平槽位入口（new StorageSlotList + grid.Refresh）—— 那条路没人验证")
    if "TryGetHeldItem(" in _CCODE and "EndHeldItem()" in _CCODE:
        ok("外部消费者用上了公开的 TryGetHeldItem / EndHeldItem")
    else:
        err("公开面 TryGetHeldItem/EndHeldItem 没有外部消费者钉住")
    if "MergeByIdOnly" in _CCODE:
        ok("外部消费者用上了 StorageSlotList.MergeByIdOnly（同 id 合并的公开开关）")
    else:
        err("没有外部消费者用 MergeByIdOnly —— 那个公开开关没人验证")

# ============================================================================
# 以下判据只回**本仓库自己的源码**（与外部消费者无关 ⟹ 消费者缺失时也不跳过）。
# ============================================================================
_sh25 = strip_comments(src.get("CustomUI.Shared.cs", ""))

# ---- 扁平槽位入口：占用数要能被调用方覆盖 ----
if "public int OccupiedCells = -1;" in gp:
    ok("StorageSlotList 允许调用方直接给占用数（默认 -1 才由面板自己估）")
else:
    err("StorageSlotList 没有 OccupiedCells 覆盖口 —— 面板只能用自己的公式，必然与真源不一致")
if "_flat.OccupiedCells >= 0" in strip_comments(gp):
    ok("头部优先用调用方给的占用数")
else:
    err("头部仍然只用自己的估算 —— 显示的数字不是做判断的那个数")

# ---- 分组键对不可分物品的处理 ----
if "RuntimeHelpers.GetHashCode(jar.item)" in _sh25 and "IsIndivisible" in _sh25:
    ok("分组键对不可分物品按 Item 实例哈希拆开（同一匣跨帧稳定，不会图标全量重载）")
else:
    err("CustomUI 分组键没处理不可分物品 —— 弹匣会在显示上被并成「×总发数」")

# ---- ★ 合并规则返工（只改数字箱：同 id 全合并 + 可配消耗顺序）----
# 1) 策略必须**按调用点选**，默认值必须是旧行为 —— 否则会把原版箱子的显示一起改掉
#    （用户 2026-10-04 明确"先只改数字箱"）。
if "public bool MergeByIdOnly;" in gp:
    ok("扁平入口有独立的合并策略开关（默认 false = 旧行为，不动原版箱子）")
else:
    err("StorageSlotList 没有 MergeByIdOnly —— 要么改不到数字箱，要么全局改坏原版箱子")
if "CollectGroups(jars, into, false);" in _sh25:
    ok("旧的 2 参 CollectGroups 转发 false（既有调用点行为不变）")
else:
    err("CollectGroups 的 2 参版没保持旧语义 —— 内置视图的合并判据被顺手改掉了")

# 4) ★ 弹匣并格必须是**独立开关且默认关**，并且"哪些弹匣能并"这条判据只有一份
#    （用户 10-04 选定"两种模式都做、设置切换"；并格之后一格不代表一匣，
#     所以取匣规矩必须由数据层定、还要写给玩家看）。
if "public bool MergeContentPages;" in gp:
    ok("弹匣并格是独立开关（默认 false = 每匣一格，不跟 MergeByIdOnly 捆在一起）")
else:
    err("没有独立的 MergeContentPages —— 玩家没法只改材料合并而保留每匣一格")
# ⚠ 26.10.3.29：扁平那条路先过搜索，交给合并核心的是过滤后的 `visible`（判据跟着搬）
if "CollectGroups(visible, groups, _flat.MergeByIdOnly, _flat.MergeContentPages)" in gp:
    ok("网格把 MergeContentPages 透传给合并核心（只有扁平入口拿得到这个策略）")
else:
    err("网格没透传 MergeContentPages —— 开关设了没人读，等于没做")
if "IsIndivisible(a) && !(mergeContentPages && IsMergeableContentPage(jar.item, a))" in _sh25:
    ok("并格例外只从「不可分」里挖出**可并的弹匣**，枪械/近战永远走引用哈希")
else:
    err("GroupKey 的弹匣例外写法变了 —— 核对是否仍然只放开弹匣、没漏进枪械/近战")
# ★ 并格范围必须是"内容量类"（弹匣 ∪ 弹药箱），不能退回只认弹匣类型
_mc = fn_body(_sh25, "internal static bool IsMergeableContentPage(", "IsMergeableContentPage")
if _mc is None:
    err("找不到 IsMergeableContentPage —— 「什么能并格」又变成各处自己判")
elif "IsContentContainer(a)" in _mc and "ItemMagazineAsset" in _mc and "state" in _mc:
    ok("并格范围 = 弹匣 ∪ 内容量类（弹药箱同权，用户 2026-10-05）且仍排除有 state 的条目")
else:
    err("并格判据缺三项之一（弹匣类型 / 内容量类 / state 排除）—— " +
        "漏 state 那条会让「界面写 6 匣、点下去只认第一条」复活")

# ---- ★ "内容量类"判据（弹药箱那一族，2026-10-05 用户实测）----------------------
#   Ranger_40 那 5 个弹药箱：Type=Supply、Amount=40、配方 FillTargetItem ⟹
#   `Item.amount` 是**箱里剩多少发**，不是一箱一箱的件数。旧判据只认弹匣类型，
#   于是两箱 20 发被并成一条 40、取的时候按 1 个一个发 ⟹ 玩家拿到"1 发的弹药箱"。
_cc_ui = fn_body(_sh25, "internal static bool IsContentContainer(", "IsContentContainer")
if _cc_ui is None:
    err("缺 IsContentContainer —— 弹药箱那一族没有共同判据了")
else:
    if "EBlueprintOperation.FillTargetItem" in _cc_ui and "IsItem(a)" in _cc_ui:
        ok("「内容量类」按**引擎行为**判（自己有 FillTargetItem 配方且自己是输入）")
    else:
        err("内容量判据没走 FillTargetItem —— 又回到靠类型/靠猜")
    # 反向：不许把 5 个弹药箱 ID 硬编码进判据
    if re.search(r"\b(119|43|44|1192|1193)\b\s*[,)=]", _cc_ui):
        err("内容量判据里出现了硬编码物品 ID —— 换存档/加模组就漏，必须按行为推导")
    else:
        ok("判据零硬编码物品 ID（原版 51 弹匣 + 5 弹药箱全靠引擎配方数据自动命中）")
    # 反向：不许退化成"MaxAmount>1 就是不可分"
    if "a.amount <= 1" not in _cc_ui:
        err("内容量判据少了 amount>1 前置 —— 但别反过来把它当唯一判据：见下面那条")
    else:
        ok("amount>1 只当**前置短路**（省掉每帧查配方），不是唯一判据")
    # ⚠ 定位串**不要带参数表**：fn_body 自己会接 `[^)]*\)`。
    _ind_ui = fn_body(_sh25, "internal static bool IsIndivisible(", "IsIndivisible")
    if _ind_ui is None:
        err("找不到 IsIndivisible —— 不可分判据的锚点失效")
    elif "IsContentContainer" in _ind_ui and "ItemMagazineAsset" in _ind_ui:
        ok("不可分 = 类型三项 ∪ 内容量类（弹匣类型判据保留，模组弹匣没配方也不会漏）")
    else:
        err("不可分判据变了 —— 类型三项与内容量类必须**并集**，且不能删掉弹匣类型判据")

# ---- 公开面：哪些弹匣允许并格 ----
if "public static bool IsMergeableContentPage(Item item)" in cprov:
    ok("「哪些弹匣允许并格」经公开面暴露（调用方不必重述判据）")
else:
    err("缺公开查询 IsMergeableContentPage —— 下游只能自己写一份，两边迟早漂")
# ⚠ 分组排序必须全序
_cmp = re.search(r"private static int CompareGroup\(ItemGroup a, ItemGroup b\)[\s\S]*?\n        \}", _sh25)
if _cmp is not None and "a.Key.CompareTo(b.Key)" in _cmp.group(0):
    ok("分组排序有末级判据（Key）= 全序，不可分物品不会每帧互换位置")
else:
    err("CompareGroup 缺末级判据 —— 同 id 同耐久的弹匣/枪械会因 List.Sort 不稳定而每帧跳动、"
        "格子反复整格重建（图标全量重载）")
# 数量文案：不可分物品的 amount <b>不是件数</b>（一匣 25 发印成 ×25 会被读成 25 个弹匣）
_bc = re.search(r"internal static string BuildCountText\(ItemGroup g\)[\s\S]*?\n        \}", _sh25)
if _bc is None:
    err("找不到 BuildCountText")
elif 'IsIndivisible(g.First.GetAsset())' in _bc.group(0) and '"发"' in _bc.group(0):
    ok("弹匣报「N发」而不是「×N」—— 不把备弹量伪装成件数")
else:
    err("BuildCountText 又把不可分物品的 amount 当件数印（一匣 25 发会显示成 ×25）")
# 一格多匣时那个数**不再是发数** ⟹ 单位必须跟着换（同一条原则：数字要说什么就是什么）
if '"匣"' in _bc.group(0) and '"箱"' in _bc.group(0) and '"发"' in _bc.group(0):
    ok("内容量类（弹匣/弹药箱）单件报「N发」、并格报匣/箱数 —— 单位跟着量走，一处定义")
else:
    err("并格后没换单位 —— 一格代表多件时印出来的数字说的是另一件事")
if '（" + g.Count + "个）' not in _bc.group(0):
    ok("没有「×N（M个）」这种重复报数（用户实测指出：两个数说的是同一件事）")
else:
    err("又把条数加回来了 —— 不可堆叠物品 N 与 M 必然相等，纯噪声")

# ---- 统一按键语义所需的公开面（2026-10-01）----
if "public bool Ctrl;" in gp24 and "public bool Dragging;" in gp24:
    ok("StorageSlotHit 带上按键状态（Ctrl / 是否已托起）—— 插件才能复刻内置那套分发")
else:
    err("StorageSlotHit 缺按键状态 —— 插件没法区分「取回」和「取回并托起」")
# ⚠ 26.10.3.32：填 hit 的那段搬进了共享层 DeliverSlotHit（两个面板共用）⟹ 判据跟过去。
if "hit.Ctrl = IsCtrlDown();" in sh:
    ok("按键状态由组件统一读（插件不必自己去碰 Input）")
else:
    err("组件没填 hit.Ctrl —— 插件只能瞎猜按键")
# ⚠ 托起逻辑已收进 GrabFromPages（背包页与存储页共用），判据要指到它。
_gfp = re.search(r"private static bool GrabFromPages\([\s\S]*?\n        \}", cprov)
_gfp_body = strip_comments(_gfp.group(0)) if _gfp is not None else ""
if _gfp_body and "BeginGrabFrom(page, jar)" in _gfp_body:
    ok("两个托起入口共用一份页内查找 + BeginGrabFrom（不写第二套拖拽实现）")
else:
    err("GrabFromPages 没走 BeginGrabFrom —— 又开一套拖拽实现就是两份漂移")
if _gfp_body and "ReferenceEquals(jar.item, item)" in _gfp_body:
    ok("先按引用认物品（ItemJar 不克隆），退一步才按 id+数量+耐久匹配")
else:
    err("托起没按引用优先匹配 —— 可能托起另一件同名物品")
if "public static bool GrabFromStorage(Item item)" in cprov and \
        "PlayerInventory.STORAGE" in cprov:
    ok("公开入口 GrabFromStorage 存在（虚拟清单逐件托起要用）")
else:
    err("缺 GrabFromStorage —— 虚拟清单没法把物品托起来")
_bgf = fn_body(sh24, "internal static bool BeginGrabFrom", "BeginGrabFrom")
if _bgf is not None and "fDragFromPage.SetValue(null, page)" in _bgf:
    ok("托起支持任意页（dragFromPage 用传入页号）—— 原版收尾按它归位，写死 STORAGE 会归错地方")
else:
    err("BeginGrabFrom 没按页号写 dragFromPage —— 托起后取消/收尾会归位到错的页")
if "return BeginGrabFrom(PlayerInventory.STORAGE, jar);" in sh24:
    ok("内置左键也走同一个 BeginGrabFrom（托起逻辑只有一份）")
else:
    err("内置 BeginGrab 没委托给 BeginGrabFrom —— 拖拽反射出现两份实现")

# ---- 文档 ----
if "NoInlining" in _rt24 and "软依赖" in _rt24:
    ok("开发文档 写了软依赖的类型隔离（NoInlining）")
else:
    err("README 缺软依赖隔离的说明 —— 这是接 CustomUI 时最容易做错的一步")

# ------------------------------------ 26. 中转页对账（CustomUI.Relay.cs，C 第 3 步）
out.append("")
out.append("26) 中转页 ↔ 调用方清单的对账（借道会不会凭空复制物品）")

rl = src.get("CustomUI.Relay.cs", "")
rlc = strip_comments(rl) if rl else ""
if not rl:
    err("找不到 CustomUI.Relay.cs —— 借道的对账层没了")
else:
    # ⚠ 引擎注释（Items.cs:23-27）：onItemRemoved 触发时物品**还没真的**从列表移除。
    #   所以移除回调里绝不能判定落点、绝不能报告，只能挪进待判定队列。
    _oir = fn_body(rl, "private static void OnItemRemoved(", "Relay.OnItemRemoved")
    if _oir is None:
        err("找不到 OnItemRemoved")
    elif "pending.Add" in _oir and "Report(" not in _oir and "ResolvePending" not in _oir:
        ok("移除回调只入队、不当场判定/报告（引擎保证事件早于真移除）")
    else:
        err("OnItemRemoved 里当场判定或报告了 —— 那一刻物品还在原页，落点必然判错")

    # 回调在引擎自己的移除流程里被调用，抛出去会打断搬运（removeItem 之后没人 addItem = 物品丢失）
    if _oir is not None and "catch" in _oir:
        ok("移除回调自带 try/catch（异常外抛会打断引擎的搬运）")
    else:
        err("OnItemRemoved 没包异常 —— 一次抛错可能让物品从页里消失")

    # 绝不能碰 onStateUpdated：原版对真实容器是 `=` 赋值（InteractableStorage.cs:412），
    # 插件接上去就把原版存盘钩子顶掉了（症状：能存能取、重启全没）。
    if "onStateUpdated" in rlc:
        err("对账层出现 onStateUpdated（原版用 = 赋值，插件接了会顶掉存盘钩子）")
    else:
        ok("对账层只订阅 onItemRemoved，没碰 onStateUpdated")

    if "onItemRemoved +=" in rlc and "onItemRemoved -=" in rlc:
        ok("订阅/退订成对（+= 与 -= 都有，不会漏摘）")
    else:
        err("onItemRemoved 的 += 与 -= 不成对")

    # 落点扫描范围：原版 sendDragItem 校验 page < PAGES-1，附近物品页(8) 不可能是落点
    if re.search(r"PAGES\s*-\s*2", rlc):
        ok("按引用扫玩家页的范围收到 PAGES-2（附近物品页不是合法落点）")
    else:
        err("落点扫描范围没用 PAGES-2 —— 会把不可达的页算进去 / 或漏掉 STORAGE")

    # ⚠ 归还顺序：Detach 必须在 ReleaseBorrowedTrunk **之前**。
    #   归还会把存储页换成新的空 Items，那时"还留在中转页里"的复制品扫不到
    #   → 被误报成 DroppedOnGround → 调用方白扣一次账。
    _cb26 = fn_body(host, "internal static void Close(", "Host.Close")
    _cv26 = fn_body(host, "internal static void CloseVirtual(", "Host.CloseVirtual")
    for _nm, _body in (("Host.Close", _cb26), ("Host.CloseVirtual", _cv26)):
        if _body is None:
            err("找不到 " + _nm)
        elif _body.find("StorageUiRelay.Detach()") < 0:
            err(_nm + " 没有 Detach —— 复制品的落点永远不会被判定")
        elif _body.find("StorageUiRelay.Detach()") < _body.find("ReleaseBorrowedTrunk("):
            ok(_nm + "：先 Detach 再归还 trunk（顺序对了才判得准落点）")
        else:
            err(_nm + "：Detach 排在归还之后 —— 归还后存储页已被换空，" +
                "留在页里的复制品会被误报成掉到地上")

    _ov26 = fn_body(host, "internal static bool OpenVirtual(", "Host.OpenVirtual")
    if _ov26 is not None and "StorageUiRelay.Attach(" in _ov26:
        _i_att = _ov26.find("StorageUiRelay.Attach(")
        _i_try = _ov26.find("if (!TryOpen(")
        if _i_try >= 0 and _i_att > _i_try:
            ok("只在接管成功后才 Attach（接管不了就不该承诺对账）")
        else:
            err("Attach 早于 TryOpen 判定 —— 接管失败时也会开始盯页")
    else:
        err("OpenVirtual 没有 Attach 中转页")

    # 对账必须独立于 Enabled / 界面开关：物品可能就在关界面那一瞬落进背包
    _upd = fn_body(src.get("CustomUI.cs", ""), "private void Update(", "Plugin.Update")
    if _upd is not None and "StorageUiRelay.Tick()" in _upd:
        ok("每帧驱动对账（放在 Host.Tick 之外，关界面/被禁用时也要判）")
    else:
        err("CustomUI.cs 的 Update 没驱动 StorageUiRelay.Tick()")

    # 同一实例登记两次 = 落点报两次 = 接入方可能扣两次账
    # ⚠ 26.10.3.41：锚点搬到**三参重载** —— 去重逻辑现在只在那一份，
    #   两参版只剩一句转发；原锚点 `Track(` 会命中两参版 ⟹ 判据永远 FAIL（加参数要搬锚点）。
    _trk = fn_body(rl, "public static bool Track(Item clone, object provenance, string source", "Relay.Track")
    if _trk is not None and "ReferenceEquals" in _trk and "return false" in _trk:
        ok("Track 按引用去重（重复登记直接拒，避免同一次搬运扣两遍账）")
    else:
        err("Track 没做引用去重")

    # ⚠ 借道的中转页**不能当箱子写入**：closeStorage 会把整页换成新的空 Items，
    #   留在里面的真物品没有任何回收路径（2026-10-03 用户实测指出：往那一格放东西
    #   数量会变、看着就是个小箱子）。两道防线都要在：
    _sh26 = src.get("CustomUI.Shared.cs", "")
    _dis26 = fn_body(_sh26, "internal static bool DropIntoStorage(", "Shared.DropIntoStorage")
    if _dis26 is None:
        err("找不到 DropIntoStorage")
    elif "IsVirtualFor(container)" not in strip_comments(_dis26):
        err("DropIntoStorage 没拒绝往借道的中转页里放 —— 放进去的会在关界面时随页销毁")
    elif "EndGrab()" in _dis26 and "WarnRelayDepositOnce()" in _dis26:
        ok("借道中转页拒绝放入：取消托起（物品留在原处）+ 说明原因，不静默收下")
    else:
        err("DropIntoStorage 的拒绝分支没取消托起或没给说明")

    _ev = fn_body(rl, "private static void EvacuateForeignItems(", "Relay.EvacuateForeignItems")
    if _ev is None:
        err("找不到 EvacuateForeignItems —— 关界面时的兜底回收没了")
    elif "IsTracked" not in _ev:
        err("EvacuateForeignItems 没跳过我们登记的复制品 —— 会把复制品还给玩家 = 凭空造物品")
    elif "forceAddItemAuto(jar.item, false, false, false)" in _ev:
        ok("兜底回收用 forceAddItemAuto(...,false,false,false)：放不下掉脚下、且不会自动穿上身")
    else:
        err("EvacuateForeignItems 的归还调用形状变了（可能引入自动装备或丢物品）")

    _dt26 = fn_body(rl, "internal static void Detach(", "Relay.Detach")
    if _dt26 is not None and "EvacuateForeignItems()" in _dt26 \
            and _dt26.find("EvacuateForeignItems()") < _dt26.find("DiscardedOnClose"):
        ok("Detach 在丢弃复制品之前先回收外来物品（顺序不能反）")
    else:
        err("Detach 没在丢弃前先回收外来物品")

    if "RelayDepositNote()" in strip_comments(host):
        ok("宿主把「不能放入」的说明顶到原版标题栏（不只写日志）")
    else:
        err("拒绝放入的提示没接到标题栏 —— 玩家只会觉得点下去没反应（自定义语义必须显式告诉玩家）")

    # ⚠ 只拦我们自己那条写入路径**不够**：原版往存储页放东西有好几个入口
    #   （Ctrl+右键快速移动 `PlayerDashboardInventoryUI.cs:897/901`、
    #    信息面板的「移动到箱子」`:446/:450`、我们的 DropIntoStorage），
    #   它们最后都汇到 `ReceiveDragItem(..., page_1=STORAGE, ...)`。
    #   2026-10-03 就是只拦了自己那条，用户实测「Ctrl+右键仍把数量变成 ×2」。
    ptc = src.get("CustomUI.Patches.cs", "")
    _blk = re.search(r"class Patch_BlockDragIntoRelay[\s\S]*?\n    \}", ptc)
    if _blk is None:
        err("找不到 Patch_BlockDragIntoRelay —— 快速移动进中转页没人拦了")
    else:
        _b = strip_comments(_blk.group(0))
        if 'typeof(PlayerInventory), "ReceiveDragItem"' in ptc:
            ok("补丁 4 挂在 ReceiveDragItem 上（所有原版搬入入口的最后汇合点）")
        else:
            err("补丁 4 没挂在 ReceiveDragItem 上 —— 会漏掉 Ctrl+右键 / 信息面板按钮")
        if "IsVirtualFor(container)" in _b:
            ok("补丁 4 按引用认页，真箱子/真后备箱的快速移动不受影响")
        else:
            err("补丁 4 没按 IsVirtualFor 判 —— 可能把真箱子的快速移动也拦了")
        _tail = _b.split("catch")[-1] if "catch" in _b else ""
        if "return true" in _tail:
            ok("补丁 4 异常时放行原版（我们的判据出错不能卡住玩家的搬运）")
        else:
            err("补丁 4 的 catch 必须 return true —— 否则异常会拦掉原版所有拖拽")

    # 公开面要有外部消费者钉住（同坑 #45/#49 的教训：内置视图能走 internal）
    if not _CHAVE:
        skip("没扫到外部消费者 —— 跳过「对账公开面有没有人从外部用」")
    else:
        if "StorageUiRelay.Track(" in _CCODE and "AddResolvedHandler(" in _CCODE:
            ok("外部消费者真的 Track 并挂了落点回调（公开面对外部可用）")
        else:
            err("没有外部消费者走 Track/AddResolvedHandler —— 对账的公开面没人从外部验证")
        if "new RelayResolvedHandler(" in _CCODE:
            ok("落点委托实例存字段复用（面板重建不会挂出两份 handler）")
        else:
            err("外部消费者每次建面板都新建落点委托 —— 同一次落点会被报多遍")

    # ★ 26.10.3.41：订阅口**按来源记名**。
    #   中转页是全局单例，但订阅者挂在全局列表上 —— 不按来源切，A 插件的落点
    #   会送进 B 插件的回调里；B 不认来历就**扣错账**（跨插件串账）。
    #   这套记名规矩与 StorageUiPanels.SetRegionVisible(source, …) 一致。
    _rep = fn_body(rl, "private static void Report(", "Relay.Report")
    if _rep is None:
        err("找不到 Relay.Report —— 落点派发口没了")
    else:
        if "s.source" in _rep and "Ordinal" in _rep:
            ok("落点按来源派发：按来源订阅者只收同源事件（全局订阅者才收所有）")
        else:
            err("Report 没按来源过滤 —— 两个插件会互相收到对方的落点（不认来历的一方扣错账）")
        if "r.Source = en.source" in _rep:
            ok("事件自带来源（RelayResolved.Source）—— 接入方能自己核对是不是自己的事")
        else:
            err("RelayResolved 没带上来源 —— 订阅端只能靠 Provenance 自己猜")
        if "HasAnyReceiver(" in _rep:
            ok("「没人接」的警告按来源判定（不然会出现「明明有人听却报没人挂」）")
        else:
            err("Report 还在按订阅者总数判「有没有人接」—— 来源对不上时会漏报凭空复制")

    for _sig, _nm in (("public static void AddResolvedHandler(string source, RelayResolvedHandler handler)",
                       "AddResolvedHandler(source, …) 按来源订阅"),
                      ("public static void RemoveResolvedHandlers(string source)",
                       "RemoveResolvedHandlers(source) 卸载时按来源撤销"),
                      ("public static List<string> ResolvedHandlerSources()",
                       "ResolvedHandlerSources() 来源自省"),
                      ("public static bool Track(Item clone, object provenance, string source)",
                       "Track(…, source) 带来源登记")):
        if _sig in rlc:
            ok("公开面有 " + _nm)
        else:
            err("公开面缺 " + _nm + " —— 第三方没法把对账回调限定到自己那几件")

    # 两参 Track 必须转发三参：老调用方行为不变（来源记 null ⟹ 只被全局订阅者收到）
    _t2 = fn_body(rl, "public static bool Track(Item clone, object provenance", "Relay.Track(两参)")
    if _t2 is not None and "Track(clone, provenance, null)" in _t2:
        ok("两参 Track 转发 null 来源（老代码不改也不变行为，且不会被按来源订阅者误收）")
    else:
        err("两参 Track 没转发三参 —— 两套登记口会各自漂移（一个改了另一个忘）")

    # 接入方：登记与订阅**必须同一个来源串**。只声明一头 = 一件落点都收不到
    # = 永远不扣账 = 凭空复制（比扣错账更隐蔽：库存只多不少）。
    #   判据**不点名任何插件**：来源串从消费者的订阅调用里**现取**，再要求 Track 用同一个。
    if not _CHAVE:
        skip("没扫到外部消费者 —— 跳过「登记/订阅同源」判据")
    else:
        _src_m = re.search(r"AddResolvedHandler\(\s*([A-Za-z_][\w.]*)\s*,", _CCODE)
        if _src_m is None:
            err("没有外部消费者按来源订阅（AddResolvedHandler(来源, …)）—— 对账链的外侧没人钉住")
        elif re.search(r"Track\(\s*[^,]+,\s*[^,]+,\s*" + re.escape(_src_m.group(1)) + r"\s*\)", _CCODE):
            ok("外部消费者登记与订阅用**同一个来源串**（只声明一头会让对账链静默断掉）")
        else:
            err("外部消费者的登记/订阅来源不一致或没带来源 —— 对账链会静默断掉 / 或又退回全局订阅")
        if "ResolvedHandlerSources()" in _CCODE and "HasGlobalHandlers" in _CCODE:
            ok("外部消费者在运行期读来源清单/全局订阅者（这两个新公开面有人从外部钉住）")
        else:
            err("没有外部消费者读 ResolvedHandlerSources/HasGlobalHandlers —— 新公开面没人从外部验证")

# -------------------- 27. 公开面：每个公开成员都得有人真的用过它 --------------------
# 为什么要有这一节：公开面清单如果**手写在文档里**，立刻开始腐烂（同坑 #67）。
# 这里以**源码**为唯一真源现扫，再拿**外部消费者**（只引用本 dll 的独立插件）去对照 ——
# 「文档说能用」和「真有插件在用」是两件事，本节能证明的是后者。
out.append("27) 公开面：谁真的用过它（清单现扫源码，不手抄）")

_pub_type = re.compile(
    r'^    public\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+|unsafe\s+)*'
    r'(class|struct|enum|interface)\s+([A-Za-z_]\w*)')
_pub_del = re.compile(r'^    public\s+delegate\s+[A-Za-z_][\w<>\[\],\.\?]*\s+([A-Za-z_]\w*)')
# ⚠ 末尾那个 `|$` **不能省**：本仓库的属性一律写成「名字一行、`{` 另起一行」，
#   旧判据只认「同一行里后面跟着 `{` / `(` / `;` / `=`」⟹ **这一整类属性在公开面上是隐形的**
#   （实测漏掉 58 / 259 项 = 22%，含 `StorageGridPanel.Columns` 这种最核心的旋钮）。
#   这正是"判据不知道'公开面'长什么样"那一族（坑 #37 的翻版：清单不全 = 门禁白设）。
# ★ 26.10.3.60：`abstract` 也认 —— 此前它**不在**修饰符表里 ⟹ `public abstract string Id { get; }`
#   这一类成员在公开面上是**隐形**的（与上面 `|$` 那次同一个病：清单不全 = 门禁白设）。
#   同时把 `virtual|abstract` 单独捕获出来，好在下面判成 **virtual 种类**（见 `_api_used`）：
#   「给第三方 override 的虚成员」既不是"被调用"、也不是"内部"，旧门禁**没有这一类**。
_pub_mem = re.compile(
    r'^        public\s+(?:(static|virtual|abstract)\s+|const\s+|override\s+|readonly\s+|sealed\s+'
    r'|new\s+|extern\s+|unsafe\s+|partial\s+)*'
    r'([A-Za-z_][\w<>\[\],\.\?]*)\s+([A-Za-z_]\w*)\s*(?:=>|\{|\(|;|=|$)')
_pub_iface = re.compile(r'^        [A-Za-z_][\w<>\[\],\.\?]*\s+([A-Za-z_]\w*)\s*(?:=>|\{|\(|;)')
_pub_enum = re.compile(r'^\s{8,12}([A-Za-z_]\w*)\s*(?:=[^,]*)?,')

# 公开成员表：类型 -> (种类, [(成员, 成员种类)])
# ⚠ 成员种类决定「怎么才算被用过」：static → 调用方必须写 `类型.成员`；
#   inst → 调用方写 `.成员`（实例总是通过变量访问）；iface/enum/delegate → 宽松匹配。
_apimap = {}
for _afn in FILES:
    _acur = None
    for _aln in src.get(_afn, "").splitlines():
        _am = _pub_type.match(_aln)
        if _am:
            _acur = _am.group(2)
            _apimap.setdefault(_acur, {"kind": _am.group(1), "members": []})
            continue
        _ad = _pub_del.match(_aln)
        if _ad:
            _apimap.setdefault(_ad.group(1), {"kind": "delegate", "members": []})
            continue
        if _acur is None:
            continue
        _ai = _apimap[_acur]
        if _ai["kind"] == "enum":
            _ae = _pub_enum.match(_aln)
            if _ae:
                _ai["members"].append((_ae.group(1), "enum"))
            continue
        if _ai["kind"] == "interface":
            _aif = _pub_iface.match(_aln)
            if _aif:
                _ai["members"].append((_aif.group(1), "iface"))
            continue
        if _ai["kind"] in ("class", "struct"):
            _ap = _pub_mem.match(_aln)
            if _ap:
                if _ap.group(1) == "static":
                    _akind = "static"
                elif _ap.group(1) in ("virtual", "abstract"):
                    # ★ 给第三方 **override** 的虚成员 —— 判定方式见 `_api_used`。
                    _akind = "virtual"
                else:
                    _akind = "inst"
                _ai["members"].append((_ap.group(3), _akind))

# ---- 外部消费者：由环境变量 `CONSUMERS_DIR` 指过来（本仓库不点名任何插件）----
#   `files()` = {相对路径: 文本}；它就是 `_api_used()` 要对照的那份"外部证据"。
#   ⚠ 一个都没扫到 ⟹ 第 27 节的孤儿判据**跳过**（见下方 ③），绝不能报 FAIL。
_consumers = _CONSUMERS.files()

# ---- 例外表 ----
# ★ 默认规则：**所有公开成员都按"接入面"处理** ⟹ 必须有外部消费者，否则 FAIL。
#   这条默认值就是"防默默长出新公开面"的机制：没被任何人用过的公开成员，
#   要么补一条外部调用（接入自检 / 下游），要么到下面登记成"内部 / 可选"。
#   ⚠ 登记成"内部"时要写清**为什么第三方不该调**，别拿它当"懒得补证据"的垃圾桶。
API_EXCEPTIONS = {
    # 内置视图实现 IStorageUiProvider / IStorageUiRegion 的产物。
    # 第三方不需要**调**这些 —— 他们要自己**实现**这两个接口（见 §4.1 / §4.9）。
    ("StorageGridView", "Id"): "内部",
    ("StorageGridView", "Priority"): "内部",
    ("StorageGridView", "CanHandle"): "内部",
    ("StorageGridView", "Open"): "内部",
    ("StorageGridView", "Close"): "内部",
    ("StorageGridView", "Rebuild"): "内部",
    ("StorageListView", "Id"): "内部",
    ("StorageListView", "Priority"): "内部",
    ("StorageListView", "CanHandle"): "内部",
    ("StorageListView", "Open"): "内部",
    ("StorageListView", "Close"): "内部",
    ("StorageListView", "Tick"): "内部",
    ("StorageListView", "Rebuild"): "内部",
    ("StorageBlueprintView", "Id"): "内部",
    ("StorageBlueprintView", "Priority"): "内部",
    ("StorageBlueprintView", "CanHandle"): "内部",
    ("StorageBlueprintView", "Open"): "内部",
    ("StorageBlueprintView", "Close"): "内部",
    ("StorageBlueprintView", "Tick"): "内部",
    ("StorageBlueprintView", "Rebuild"): "内部",
    ("StorageBlueprintView", "WantsRegion"): "内部",
    ("StorageBlueprintView", "RegionOrder"): "内部",
    ("StorageBlueprintView", "RegionPanel"): "内部",
    # ★ 26.10.3.60：`StorageUiRegionBase` **替第三方实现**的框架成员。
    #   第三方既不**调**它们（宿主调），也不该**override** 它们 —— override 就绕过了
    #   基类里那四条规矩（判代 / 只藏不拆 / `IsBuilt` 守卫 / 失败自拆）。
    #   要挂内容只 override **`Refresh`**（protected，不进这张表）。
    #   ⚠ 不是"懒得补证据"：这几个成员的消费者是**宿主**（本插件自己），
    #     而本门禁只认**外部程序集**（下游 / 接入自检）—— 同类先例见上面 `StorageGridView` 那批。
    ("StorageUiRegionBase", "CanHandle"): "内部",     # 基类固定恒 false（改 true = 区块抢存储位，语义冲突）
    ("StorageUiRegionBase", "Open"): "内部",         # 判代 + 建 + 刷新 + 失败自拆，四条规矩都在里面
    ("StorageUiRegionBase", "Close"): "内部",        # 只藏不拆（让第三方自己 override = 迟早写出 RemoveChild）
    ("StorageUiRegionBase", "Tick"): "内部",         # `IsBuilt` 守卫 + 汇到 Refresh
    ("StorageUiRegionBase", "Rebuild"): "内部",      # 同上（与 Tick 同一个出口）
    ("StorageUiRegionBase", "RegionPanel"): "内部",  # 宿主每帧来取面板；第三方不必碰挂载
    # 危险 / 框架内部流程
    ("StorageUiRegistry", "Clear"): "内部",          # 会连内置三个视图一起清掉 ⟹ 只会把插件搞瘫
    ("StorageUiRegistry", "FindRegions"): "内部",     # 宿主的区块栈流程
    ("StorageUiRegistry", "FindCandidates"): "内部",  # 宿主的候选链流程
    ("StorageBlueprintFilters", "Passes"): "内部",    # 本插件自己调的判据内核
    ("StorageBlueprintFilters", "Clear"): "内部",     # 清空**所有第三方来源**（含别人的）⟹ 接入方该用 Remove(自己的 source)
    ("StorageBlueprintFilters", "SetConfigFilter"): "内部",   # 配置驱动（CustomUI.cs 从 cfg 调）
    # ★ 26.10.3.48：门禁补上「`{` 另起一行」那一类属性后才扫出来的（以前整类隐形）。
    ("StorageUiContext", "VirtualPage"): "内部",      # 三档**原始值**；注释明写"别直接比较它，
                                                     # 用 UsableAsContainer / WantsRegions" ⟹
                                                     # 读它等于绕开收口（接入自检读的就是那两个属性）
    # 可选（诊断 / 边角能力，用得上但不影响接入）
    ("StorageUiRegistry", "Find"): "可选",            # 诊断："谁会接管这个箱子"
    ("StorageUiRegistry", "GetById"): "可选",         # 按 Id 找 provider（做设置界面时用）
    ("StorageUiRegistry", "DefaultSource"): "可选",   # 没给来源时的兜底标识（内置视图用的）
    ("StorageBlueprintView", "InvalidateCache"): "可选",  # 配方表变了想让缓存失效
}


def _api_used(tname, mem, kind):
    got = []
    for cname, ctext in _consumers.items():
        if kind == "static":
            pat = r'\b' + re.escape(tname) + r'\s*\.\s*' + re.escape(mem) + r'\b'
        elif kind == "inst":
            pat = r'\.\s*' + re.escape(mem) + r'\b'
        elif kind == "virtual":
            # ★ 26.10.3.60：给第三方 **override** 的虚成员 —— 外部**真的覆写了它**才算有人在用。
            #   这比 `.成员` **更强**（`override` 必须签名逐字匹配，编译期就钉住了），
            #   也不会像 `.成员` 那样被同名局部变量满足（坑 #77/#83 那一族）。
            #   ⚠ 限定在同一行内（`[^\n;=]*`）—— 跨行会连下一个成员的声明一起吞进来。
            pat = r'\boverride\s+[^\n;=]*\b' + re.escape(mem) + r'\b'
        else:
            pat = r'\b' + re.escape(mem) + r'\b'
        if re.search(pat, ctext):
            got.append(cname)
    return got


_api_total = 0
_api_orphan = []       # 接入面但**没人用过**（= 等于没公开）
_api_exception_hit = set()
for _tname in sorted(_apimap):
    for _mem, _kind in _apimap[_tname]["members"]:
        _api_total += 1
        _cls = API_EXCEPTIONS.get((_tname, _mem))
        if _cls is not None:
            _api_exception_hit.add((_tname, _mem))
            continue
        if not _api_used(_tname, _mem, _kind):
            _api_orphan.append(_tname + "." + _mem)

# ① 例外表里登记的东西必须还在（改名 / 删掉要一次性搬走锚点）
for _k in sorted(API_EXCEPTIONS):
    if _k not in [(t, m) for t in _apimap for m, _ in _apimap[t]["members"]]:
        err("公开面例外表里的 `%s.%s` 在源码里找不到了 —— 它被改名或删掉了，表要跟着搬" % _k)

# ② 总账（数字进报告，便于下一轮对比）
ok("公开面共 %d 项（%d 个公开类型）；外部消费者 %d 个文件；例外登记 %d 项"
   % (_api_total, len(_apimap), len(_consumers), len(API_EXCEPTIONS)))

# ③ 接入面（= 没登记成例外的）必须至少被一个外部消费者真的调用过
#   ⚠ 消费者缺失时**跳过**（外部使用者没有消费者目录 ⟹ 这里没有"外部证据"可对照，
#     但**绝不能**把所有公开成员都报成孤儿 —— 那是把"没扫到"当成"写了没人用")。
if not _consumers:
    skip("没扫到外部消费者 —— 跳过「公开面有没有人真的用过它」（" + _CONSUMERS.summary() + "）")
elif _api_orphan:
    err("以下 %d 个公开成员**没有任何外部消费者**（文档说能用 ≠ 真有插件在用）：%s"
        % (len(_api_orphan), ", ".join(sorted(_api_orphan))))
else:
    ok("接入面里的每一项都至少被一个外部插件真调用过（没有「写了没人用」的公开面）")

# ④ 接入自检的「运行期证据」必须落在**打开就看得见**的地方。
#   为什么单拎这一条：本接入自检的验收方式一贯是"打开箱子 → 看面板"，
#   而 26.10.3.45 把 ctx 三字段挂到了网格的**格子 Tooltip** 上 ——
#   Tooltip 只对着"有物品的格子"出现，背包一空就**一个格子都没有**，
#   照验收提示去停鼠标必然一无所获（用户实测「哪个标题？没看到」，坑 #71）。
#   ⟹ 钉死：证据走状态行（打开就有），且**不许**再往 TooltipHint 塞上下文
#     （那是「自定义按键约定」的位置，语义挪用 = 下一个人会照着抄错）。
if not _CHAVE:
    skip("没扫到外部消费者 —— 跳过「ctx 三字段的调用点」两条")
else:
    _brtxt = _CCODE
    # ⚠ 判据必须**认块**、不能只看"字符串在不在"：`CtxSummary()` 在**方法定义处**也有，
    #   只查存在性的话，把调用点删掉照样通过（本轮第一版就是这么写的，阳性对照当场揭穿）。
    _sa_blk = re.search(r'statusA\.Text\s*=[^;]+;', _brtxt)
    _fp_blk = re.search(r'int fp = unchecked\([^;]+;', _brtxt)
    _ctx3 = ("c.Player" in _brtxt) and ("c.Drop" in _brtxt) and ("c.Asset" in _brtxt)
    if _ctx3 and _sa_blk and ("CtxSummary()" in _sa_blk.group(0)) \
            and _fp_blk and ("CtxFingerprint()" in _fp_blk.group(0)):
        ok("ctx 的 Player / Drop / Asset 三字段真被读，且**调用点**落在状态行（打开就有，不靠背包有东西）")
    else:
        err("ctx 三字段没被用上，或调用点没落在状态行/状态指纹上 —— Tooltip 只在有物品的格子上出现，空背包看不到")
    if re.search(r'TooltipHint\s*=\s*"ctx', _brtxt):
        err("外部消费者又把上下文塞进 TooltipHint 了 —— 那是「按键约定」的位置，且空背包时看不见")
    else:
        ok("上下文没有塞进 TooltipHint（它只该放「按键约定」，且要挂格子）")


out.append("")
out.append("=" * 60)
out.append("结果：FAIL=" + str(bad) + "  WARN=" + str(warn))
out.append("=" * 60)

txt = "\n".join(out)
with io.open(os.path.join(HERE, "_check.txt"), "w", encoding="utf-8") as f:
    f.write(txt)

# ⚠ 控制台是 GBK：正文里的箭头/生僻符号会让 print 直接抛 UnicodeEncodeError，
#   结果「检查其实全过了，但脚本崩在最后一步、退出码非 0」—— 会被误读成自检失败。
#   _check.txt 永远是完整 UTF-8；这里只把控制台输出降级成"能印就印"。
try:
    enc = sys.stdout.encoding or "utf-8"
    sys.stdout.write(txt.encode(enc, errors="replace").decode(enc, errors="replace") + "\n")
except Exception:
    try:
        print(txt.encode("ascii", errors="replace").decode("ascii"))
    except Exception:
        print("结果：FAIL=" + str(bad) + "  WARN=" + str(warn) + "（详见 _check.txt）")
sys.exit(1 if bad else 0)
