# -*- coding: utf-8 -*-
"""消费者发现（门禁用）—— 本仓库里**不写任何具体插件的名字**。

思路（由用户 2026-10-12 定）：
    扫一个「约定目录」下**每个子项目**的源码，凡是**引用了本仓库公开接口**的，就算消费者。
    这样仓库里不需要点名谁在用我 —— 谁用了、用了哪些，**扫出来**即可。

配置：
    CONSUMERS_DIR   逗号分隔的目录列表（可用环境变量覆盖）。
                    默认 `./consumers`（外部使用者把想检查的消费者子项目放这里）。
    本机开发时把两个外部消费者的**源码目录**指过来即可，**不必复制**（避免两份真源）：
        set CONSUMERS_DIR=..\\ConsumerA-Source,..\\ConsumerB-Source

对外接口：
    files()            -> {相对路径: 源码文本}（只扫 .cs，跳过 bin/obj）
    merged()           -> 所有消费者源码拼成的一段文本（判据直接在里面找成员名）
    merged_path()      -> 把 merged() 落到临时文件并返回路径（给老代码当"一个文件"用）
    referencing(guid)  -> {相对路径: 文本}，只留**引用过该 GUID** 的文件
    summary()          -> 一行人类可读的概览（给自检报告用）
"""
import io
import os
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ENV_KEY = "CONSUMERS_DIR"
DEFAULT_DIR = "./consumers"

_cache = None
_merged_path_cache = None


def dirs():
    """要扫的目录（已转绝对路径；不存在的会被跳过）。"""
    raw = os.environ.get(ENV_KEY, DEFAULT_DIR)
    out = []
    for d in raw.split(","):
        d = d.strip()
        if not d:
            continue
        if not os.path.isabs(d):
            d = os.path.normpath(os.path.join(HERE, d))
        if os.path.isdir(d):
            out.append(d)
    return out


def files():
    """扫到的源码：{相对路径: 文本}。⚠ 跳过 bin / obj（编译中间物里有抄来的副本）。"""
    global _cache
    if _cache is not None:
        return _cache
    found = {}
    for root in dirs():
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [x for x in dirnames
                           if x.lower() not in ("bin", "obj", ".git", ".vs")]
            for fn in filenames:
                if not fn.endswith(".cs"):
                    continue
                p = os.path.join(dirpath, fn)
                rel = os.path.relpath(p, root)
                try:
                    with io.open(p, "r", encoding="utf-8", errors="ignore") as f:
                        found[rel] = f.read()
                except Exception:
                    pass
    _cache = found
    return found


def merged():
    """所有消费者源码拼成的一段文本（判据在里面找成员名即可，不必知道是谁）。"""
    return "\n".join(files().values())


def merged_path():
    """把 merged() 落到临时文件，返回其路径。

    ★ 给**老式判据**用的兼容层：它们原来是 `os.path.exists(p)` + `open(p).read()`，
      只要把 p 指到这里，判据逻辑一行都不用改。
    ⚠ 临时文件在系统临时目录（**不在仓库里**），仓库因此不含任何插件名。
    """
    global _merged_path_cache
    if _merged_path_cache and os.path.exists(_merged_path_cache):
        return _merged_path_cache
    fd, path = tempfile.mkstemp(prefix="customui_consumers_", suffix=".cs")
    with os.fdopen(fd, "w", encoding="utf-8", newline="") as f:
        f.write(merged())
    _merged_path_cache = path
    return path


def referencing(guid):
    """只留源码里出现过 `guid` 的文件（= 真的在引用本仓库的那些）。"""
    return dict((k, v) for k, v in files().items() if guid in v)


def summary():
    ds = dirs()
    fs = files()
    if not fs:
        return ("没扫到消费者（" + ENV_KEY + "=" +
                os.environ.get(ENV_KEY, DEFAULT_DIR) + "）⟹ 与「外部消费者」相关的判据会跳过")
    return ("扫到 %d 个消费者文件（%d 个目录）：%s"
            % (len(fs), len(ds), os.environ.get(ENV_KEY, DEFAULT_DIR) + " ⟶ " + ", ".join(sorted(fs)[:6])
               + (" …" if len(fs) > 6 else "")))
