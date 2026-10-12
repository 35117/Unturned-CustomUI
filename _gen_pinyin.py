"""离线生成 CustomUI 的拼音表（一次性工具，不进运行时依赖）。

语料 = 搜索框真会面对的文本：原版 + 模组物品的中文名、英文名、描述。
产物 = CustomUI-Source/CustomUI.PinyinData.cs（两个平行字符串 + 首次使用时建字典）。
多音字：保留最多 3 个读音，避免"长"只剩一个读法。
"""
import io, os, re, collections
from pypinyin import pinyin, Style

GAME = r"D:\Software\Steam\steamapps\common\Unturned"
CORPUS_DIRS = [
    os.path.join(GAME, "Bundles", "Items"),
    os.path.join(GAME, "Sandbox"),
]
LOCALIZED = ("English.dat", "Schinese.dat")
HAN = re.compile(r"[\u4e00-\u9fff]")

# 本地化 dat 的真实字节格式是「一行一键」：
#   Name 俄制弹药箱（小口径）\nDescription 小口径突击级弹药箱。设计用来装上40颗子弹。ID：119\n
# ⚠ 别被 read_file 的渲染骗了 —— 它会把值显示成 `Name` / `[` / 值 / `]` 三行，文件里并没有方括号。
BLOCK = re.compile(r"(?m)^(Name|Description)[ \t]+(.*)$")

texts = []
n_loc = 0
n_block = 0
for root in CORPUS_DIRS:
    for dirpath, _dn, files in os.walk(root):
        for fn in files:
            if fn not in LOCALIZED:
                continue
            p = os.path.join(dirpath, fn)
            try:
                t = io.open(p, "r", encoding="utf-8", errors="ignore").read()
            except Exception:
                continue
            n_loc += 1
            for m in BLOCK.finditer(t):
                texts.append(m.group(2))
                n_block += 1

corpus = "".join(texts)
chars = sorted(set(HAN.findall(corpus)))
print("读本地化文件 %d 个，取到 %d 段 Name/Description，去重汉字 %d 个"
      % (n_loc, n_block, len(chars)))
if len(chars) < 50:
    print("!! 汉字数少得可疑，提取正则八成还是没匹配上 —— 停，别写文件")
    raise SystemExit(1)

table = collections.OrderedDict()
for ch in chars:
    rs = []
    for v in pinyin(ch, style=Style.NORMAL, heteronym=True)[0]:
        v = v.lower()
        if v and v not in rs:
            rs.append(v)
    table[ch] = ",".join(rs[:3]) if rs else ""

total_len = sum(len(v) + 1 for v in table.values())
print("读音串总长约 %d 字符（含分隔符）" % total_len)
print("抽查：")
for probe in "刀枪板属罐带匣缩碳米纤维长短重":
    if probe in table:
        print("   ", probe, "->", table[probe])

CHUNK = 900
def chunks(s, n):
    for i in range(0, len(s), n):
        yield s[i:i + n]

chars_str = "".join(table.keys())
vals_str = "|".join(table[c] for c in table)

def lit(name, s):
    out = []
    parts = list(chunks(s, CHUNK))
    if len(parts) == 1:
        out.append('        private const string %s = "%s";' % (name, parts[0]))
    else:
        out.append('        private const string %s =' % name)
        for i, p in enumerate(parts):
            tail = ";" if i == len(parts) - 1 else " +"
            out.append('            "%s"%s' % (p, tail))
    return "\n".join(out)

body = """// 自动生成，不要手改 —— 生成方式见 CustomUI README §4.21（离线用 pypinyin 生成，运行时零依赖）。
//
// 为什么只覆盖"游戏里实际出现的汉字"而不是全量 GB2312：
//   搜索框要拼拼音的文本只有物品/蓝图的中文名、英文名与描述。
//   按语料生成把表压到几百~两千字，人工抽查得过来；
//   手敲一张全量表 = 没人能核对（本项目刚因"没核实的表/正则"烧过一次事故）。
// 多音字保留最多 3 个读音 ⟹ 匹配时任一读音命中即算命中。
namespace CustomUI
{
    internal static class StorageUiPinyinData
    {
        /// <summary>表里的汉字，按顺序排；与 PinyinValues 用同一个下标对齐。</summary>
        %s

        /// <summary>每个汉字的读音：条目内逗号分隔多音，条目间用 | 分隔。</summary>
        %s

        private static System.Collections.Generic.Dictionary<char, string[]> _map;

        /// <summary>取一个汉字的读音数组；表里没有返回 null（= 该字不参与拼音匹配）。</summary>
        public static string[] Of(char c)
        {
            if (_map == null) Build();
            string[] v;
            return _map.TryGetValue(c, out v) ? v : null;
        }

        private static void Build()
        {
            var m = new System.Collections.Generic.Dictionary<char, string[]>();
            string[] vals = PinyinValues.Split('|');
            for (int i = 0; i < PinyinChars.Length && i < vals.Length; i++)
            {
                m[PinyinChars[i]] = vals[i].Split(',');
            }
            _map = m;
        }
    }
}
""" % (lit("PinyinChars", chars_str), lit("PinyinValues", vals_str))

out = os.path.join(GAME, "CustomUI-Source", "CustomUI.PinyinData.cs")
io.open(out, "w", encoding="utf-8", newline="\r\n").write(body)
print("写出", out, os.path.getsize(out), "字节")
