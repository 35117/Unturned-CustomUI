# -*- coding: utf-8 -*-
"""临时探查 v2：枚举 CustomUI 的**公开面**，并对照「到底谁真的用过它」。

v1 的判据是错的（按 `类型名.成员` 匹配）：实例成员在调用侧写成 `ctx.Title`、
`hit.Item`、`list.Add(...)`，全部被误判成"零消费者"。v2 按**成员种类**分三套判据：
  · static 成员      → 调用侧必须写 `类型名.成员`（C# 不会用 using static）⟹ 精确匹配
  · 实例成员/字段    → 调用侧写成 `.成员`（任何变量）⟹ 匹配 `.成员`
  · 接口成员         → 由**实现方**证明（第三方写 `public Xxx Yyy(...)`，没有点）⟹ 宽松匹配
"""
import io, os, re, sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

SRC_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(SRC_DIR)

PUB_TYPE = re.compile(
    r'^    public\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+|unsafe\s+)*'
    r'(class|struct|enum|interface)\s+([A-Za-z_]\w*)')
# delegate 的"名字"是第三个词：public delegate bool XxxHandler(...)
PUB_DEL = re.compile(r'^    public\s+delegate\s+[A-Za-z_][\w<>\[\],\.\?]*\s+([A-Za-z_]\w*)')
PUB_MEMBER = re.compile(
    r'^        public\s+(?:(static)\s+|const\s+|virtual\s+|override\s+|readonly\s+|sealed\s+'
    r'|new\s+|extern\s+|unsafe\s+|partial\s+)*'
    r'([A-Za-z_][\w<>\[\],\.\?]*)\s+([A-Za-z_]\w*)\s*(?:=>|\{|\(|;|=|$)')
IFACE_MEMBER = re.compile(r'^        [A-Za-z_][\w<>\[\],\.\?]*\s+([A-Za-z_]\w*)\s*(?:=>|\{|\(|;)')
ENUM_ITEM = re.compile(r'^\s{8,12}([A-Za-z_]\w*)\s*(?:=[^,]*)?,')


def read(p):
    return io.open(p, encoding="utf-8-sig", errors="replace").read()


types = {}          # 类型名 -> {kind, file, members: [(name, is_static)]}
cur = None
for fn in sorted(os.listdir(SRC_DIR)):
    if not (fn.startswith("CustomUI") and fn.endswith(".cs")):
        continue
    cur = None      # ⚠ 每个文件都要重置：否则上个文件末尾的类型会吞掉下个文件的成员
    for line in read(os.path.join(SRC_DIR, fn)).splitlines():
        m = PUB_TYPE.match(line)
        if m:
            cur = m.group(2)
            types[cur] = {"kind": m.group(1), "file": fn, "members": []}
            continue
        d = PUB_DEL.match(line)
        if d:
            types[d.group(1)] = {"kind": "delegate", "file": fn, "members": []}
            continue
        if cur is None:
            continue
        info = types[cur]
        if info["kind"] == "enum":
            e = ENUM_ITEM.match(line)
            if e:
                info["members"].append((e.group(1), "enum"))
            continue
        if info["kind"] == "interface":
            mm = IFACE_MEMBER.match(line)
            if mm:
                info["members"].append((mm.group(1), "iface"))
            continue
        if info["kind"] in ("class", "struct"):
            mm = PUB_MEMBER.match(line)
            if mm:
                info["members"].append((mm.group(3), "static" if mm.group(1) else "inst"))

ind_dir = os.path.join(ROOT, "Industrial-Source")
ind_files = ([os.path.join(ind_dir, f) for f in os.listdir(ind_dir) if f.endswith(".cs")]
             if os.path.isdir(ind_dir) else [])
consumers = {
    "接入自检": read(os.path.join(ROOT, "CustomUIBridge-Source", "CustomUIBridge.cs")),
    "下游": "\n".join(read(p) for p in ind_files),
}


def used_by(tname, mem, kind):
    out = []
    for cname, ctext in consumers.items():
        if kind == "static":
            pat = r'\b' + re.escape(tname) + r'\s*\.\s*' + re.escape(mem) + r'\b'
        elif kind == "inst":
            pat = r'\.\s*' + re.escape(mem) + r'\b'
        else:                       # enum / iface / delegate：宽松（枚举值可能被限定的写法多种）
            pat = r'\b' + re.escape(mem) + r'\b'
        if re.search(pat, ctext):
            out.append(cname)
    return out


total = 0
zero = []
for tname in sorted(types):
    info = types[tname]
    for mem, kind in info["members"]:
        total += 1
        ub = used_by(tname, mem, kind)
        if not ub:
            zero.append((tname, info["kind"], mem, kind))

print("=" * 72)
print("公开面总计: %d 项（%d 个公开类型）" % (total, len(types)))
print("零外部消费者: %d 项" % len(zero))
print("=" * 72)

print("\n---- 按类型 ----")
for tname in sorted(types):
    info = types[tname]
    n = len(info["members"])
    z = len([x for x in zero if x[0] == tname])
    print("%s %-28s %-9s %2d 项，零消费 %2d" % ("★" if z else " ", tname, info["kind"], n, z))

print("\n---- 零外部消费者（按类型，括号里是成员种类）----")
for tname in sorted(set(x[0] for x in zero)):
    mems = ["%s(%s)" % (x[2], x[3]) for x in zero if x[0] == tname]
    print("%-28s %s" % (tname, ", ".join(mems)))
