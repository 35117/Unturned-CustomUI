// ============================================================================
//  CustomUI · 可复用的「搜索输入框」（StorageSearchBox，26.10.3.46）
// ============================================================================
//  给**第三方插件自己的界面**用：一行输入框，打字即时回传，手感与本插件三个内置视图
//  完全一致（位置 / 留白 / 最大长度 / 占位文字）。
//
//  ★ 它首先是一次**消重**：网格面板 / 列表面板 / 蓝图视图里各有一份**逐字相同**的
//    `CreateStringField()` 代码块（连 `PositionOffset_X = 8f`、`SizeOffset_X = -16f`、
//    `MaxLength = 32`、以及"每帧比一次文本再写回"那四行都一模一样）。
//    同一件事写到第三遍时就不是巧合了 —— **同一事实写在 N 个地方 = 迟早有 N-1 个是错的**
//    （README 坑 #68；前一个是 `StorageFilterBar`，同样从蓝图里抽出来）。
//    顺手治掉的还有一处小延迟：那三份都是"下一帧才把外部改的 `SearchQuery` 同步到控件"，
//    现在 setter 里当场同步。
//
//  ★ 三条纪律（都是被坑出来的，别在调用方重写）：
//    1. **宽度一律相对值**（`SizeScale_X = 1f` + `SizeOffset_X = -16f`）——
//       面板挂载前父容器宽度是 0，写像素宽度会算错（同 FilterBar 与几何铁律）。
//    2. **打字只回传、不重建**：`OnQueryChanged` 里只把新词交出去，
//       重建由调用方下一帧自己的 Tick 统一做（三个内置视图都这么约定）。
//    3. **组件不筛任何东西** —— 它只负责"拿到玩家打的字"。匹配规则用公开的
//       `StorageUiSearch.Matches`（与内置视图同一份实现），别再自己写一套，
//       否则同一个箱子在两个界面里搜出来的东西不一样。
//
//  ⚠ 引擎调用全部照抄三个内置视图里的既有用法（`Glazier.Get().CreateStringField()` /
//     `ISleekField.PlaceholderText` / `MaxLength` / `OnTextChanged` / `Text`），
//     没有一处凭印象写。输入框本身就是 `ISleekElement` ⟹ **不需要外层 frame 包一层**。
// ============================================================================
using System;
using SDG.Unturned;

namespace CustomUI
{
    /// <summary>玩家在搜索框里打字。<paramref name="query"/> 是输入框里的**当前**文本（可能是空）。</summary>
    public delegate void StorageSearchHandler(string query);

    /// <summary>
    /// **可复用的「搜索输入框」**（`26.10.3.46`）。
    ///
    /// 典型用法（第三方给自己的区块加一个搜索框）：
    /// <code>
    /// box = new StorageSearchBox();
    /// box.TopOffset = 34f;                        // 摆在你自己头部之下（内置视图的头部高 34）
    /// box.Placeholder = "搜索物品名 / 拼音";
    /// box.OnQueryChanged = OnQuery;               // (string query)
    /// box.Build(myContainer);                     // 建一次，挂进你自己的容器
    /// // 布局：这一行占 box.DesiredHeight（Show=false 时为 0，等于不占位）
    /// // 过滤：StorageUiSearch.Matches(asset, box.Query) —— 与内置视图同一套匹配
    /// // 关界面：box.Hide()；明确不要了：box.Discard()
    /// </code>
    ///
    /// **三条语义边界**（比功能更容易被误解）：
    /// 1. **它不筛任何东西** —— 只负责把玩家打的字交给你。匹配见 `StorageUiSearch.Matches`。
    /// 2. **打字只回传，不重建** —— 你在回调里改数据 / 标脏，重建放下一帧做。
    /// 3. **`Query` 是"这一次会话"的状态** —— 关界面时调用方要自己清（内置三个视图都清，
    ///    否则打开下一个箱子会莫名其妙只剩几件，玩家以为物品丢了）。
    ///
    /// ⚠ 生命周期与另三个公开组件一致：**关界面用 <see cref="Hide"/>（只藏不拆）**，
    ///   明确不要了才 <see cref="Discard"/>。
    /// </summary>
    public class StorageSearchBox
    {
        /// <summary>左右留白（与三个内置视图一致）。</summary>
        private const float INSET_X = 8f;

        /// <summary>输入框比它那一行矮这么多（留出与下一块的间隙，与内置观感一致）。</summary>
        private const float INNER_GAP = 6f;

        // ================= 旋钮（Build 之后改也会当场写到控件上）=================

        private float _height = 30f;
        private float _topOffset = 34f;
        private int _maxLength = 32;
        private string _placeholder = "搜索物品名 / 拼音";
        private bool _show = true;
        private string _query = "";

        /// <summary>
        /// 这一行占的高度（默认 30）。输入框本体比它矮 6px。
        /// ⚠ 内置的**大格子视图用的是 28**（那是已验收的布局），所以它显式设过 ——
        ///   默认值只管多数情况，不是"必须一致"。
        /// </summary>
        public float Height
        {
            get { return _height; }
            set
            {
                if (_height == value) return;
                _height = value;
                if (field != null)
                {
                    try { field.SizeOffset_Y = _height - INNER_GAP; } catch (Exception) { }
                }
            }
        }

        /// <summary>纵向位置（默认 34 = 三个内置视图的头部高度）。按你自己的头部高度设。</summary>
        public float TopOffset
        {
            get { return _topOffset; }
            set
            {
                if (_topOffset == value) return;
                _topOffset = value;
                if (field != null)
                {
                    try { field.PositionOffset_Y = _topOffset; } catch (Exception) { }
                }
            }
        }

        /// <summary>最大字符数（默认 32，与内置视图一致）。</summary>
        public int MaxLength
        {
            get { return _maxLength; }
            set
            {
                if (_maxLength == value) return;
                _maxLength = value;
                if (field != null)
                {
                    try { field.MaxLength = _maxLength; } catch (Exception) { }
                }
            }
        }

        /// <summary>占位文字（输入框为空时显示的那行灰字）。</summary>
        public string Placeholder
        {
            get { return _placeholder; }
            set
            {
                string v = value ?? "";
                if (string.Equals(_placeholder, v)) return;
                _placeholder = v;
                if (field != null)
                {
                    try { field.PlaceholderText = _placeholder; } catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// 是否显示这一行（默认 true）。
        /// ⚠ false 时 <see cref="DesiredHeight"/> 是 0 —— 按它留位的调用方**得自己重排**。
        /// ⚠ 与另三个组件不同，它**不**在 setter 里 Discard 自己：面板级重建是调用方的事
        ///   （内置两个面板的 `ShowSearch` setter 就是"改了就作废整个面板、下次 Build 重建"）。
        /// </summary>
        public bool Show
        {
            get { return _show; }
            set
            {
                if (_show == value) return;
                _show = value;
                if (field != null)
                {
                    try { field.IsVisible = _show; } catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// 搜索词（空 = 不筛）。
        /// ★ 外部改它**立刻**同步到输入框 —— 以前三个内置视图各写了一趟"每帧比对再写回"，
        ///   现在收在这里（顺带少一帧延迟）。
        /// ⚠ 它**不触发** <see cref="OnQueryChanged"/>：那个回调只代表"玩家打的字"，
        ///   否则 set 一次会绕回调用方自己。
        /// </summary>
        public string Query
        {
            get { return _query; }
            set
            {
                string v = value ?? "";
                if (string.Equals(_query, v)) return;
                _query = v;
                // ⚠ 只在真的不同时写：写下去会触发控件的 OnTextChanged 再绕一圈
                //   （值相同不会无限递归，但白写一次文本会让 Glazier 每帧重排）。
                if (field != null)
                {
                    try
                    {
                        if (!string.Equals(field.Text, _query))
                        {
                            // ★ 26.10.3.46：写控件前先立旗 —— 否则控件回调会**再进来一次**
                            //   （`OnFieldChanged` 会把同一串当成"玩家打的字"回传给调用方）。
                            //   这就是上面注释承诺的"setter 不触发 OnQueryChanged"，
                            //   而**承诺必须与实现一致**（本会话已因"注释说一套、代码做另一套"
                            //   栽过两次：坑 #70 / #71）。
                            _writingFromCode = true;
                            try { field.Text = _query; }
                            finally { _writingFromCode = false; }
                        }
                    }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>玩家打字时回调（**不包含**外部对 <see cref="Query"/> 的赋值）。</summary>
        public StorageSearchHandler OnQueryChanged;

        // ================= 只读状态 =================

        /// <summary>输入框本体（它自己就是 `ISleekElement`，没有外层 frame）。</summary>
        public ISleekElement Element { get { return field; } }

        public bool IsBuilt { get { return field != null; } }

        /// <summary>这一行要占的高度 —— <see cref="Show"/> 为 false 时是 0（等于不占位）。</summary>
        public float DesiredHeight { get { return _show ? _height : 0f; } }

        // ================= 生命周期 =================

        /// <summary>
        /// 建一次并挂进 <paramref name="parent"/>。重复调用只是重新挂父容器（不重复建）。
        /// ⚠ 宽度**跟随父容器**（相对值）—— 挂载前父容器宽度是 0，所以这里不写像素宽。
        /// </summary>
        public void Build(ISleekElement parent)
        {
            if (parent == null) return;

            if (field != null)
            {
                // ⚠ 不能先 `RemoveChild` —— uGUI 的 `RemoveChild` 末尾是 `InternalDestroy()`
                //   （`GlazierElementBase_uGUI.cs:253-283`）= 把控件销毁/回收，紧接着的 `AddChild`
                //   就挂了一具尸体 ⟹ **重新 Build 之后永远看不见**（`IsVisible` 停在 Hide 的 false）。
                //   `AddChild` 自己会从旧父容器摘走（同文件 `:321-347`），不需要先摘。
                try { parent.AddChild(field); } catch (Exception) { return; }
                try { field.IsVisible = _show; } catch (Exception) { }
                return;
            }

            // 关着就别建：DesiredHeight 也是 0，按它留位的调用方不会给这一行留位置。
            if (!_show) return;

            field = Glazier.Get().CreateStringField();
            field.PositionOffset_X = INSET_X;
            field.PositionOffset_Y = _topOffset;
            field.SizeScale_X = 1f;
            field.SizeOffset_X = -2f * INSET_X;
            field.SizeOffset_Y = _height - INNER_GAP;
            field.MaxLength = _maxLength;
            field.PlaceholderText = _placeholder;
            field.Text = _query;
            field.OnTextChanged += OnFieldChanged;
            parent.AddChild(field);
        }

        /// <summary>只藏不拆（关界面时用）。再显示靠调用方重新 <see cref="Build"/> 或设 <see cref="Show"/>。</summary>
        public void Hide()
        {
            try { if (field != null) field.IsVisible = false; } catch (Exception) { }
        }

        /// <summary>
        /// 真拆掉（下一次 <see cref="Build"/> 会重建）。
        /// ⚠ 文本**留在 <see cref="Query"/> 里**不清 —— 清不清是会话语义，调用方定
        ///   （内置三个视图都在自己的 `Close`/`Hide` 里 `SearchQuery = ""`）。
        /// </summary>
        public void Discard()
        {
            try
            {
                if (field != null && field.Parent != null) field.Parent.RemoveChild(field);
            }
            catch (Exception) { }

            field = null;
        }

        // ================= 内部 =================

        private ISleekField field;

        /// <summary>
        /// 正在由代码写控件文本（<see cref="Query"/> 的 setter）。置位期间控件回调**不**回传
        /// —— 那是我们自己的赋值，不是"玩家打的字"。
        /// </summary>
        private bool _writingFromCode;

        /// <summary>
        /// 玩家打字。只把新词回传给调用方，**不在这里重建**任何东西 ——
        /// 重建由调用方下一帧自己的 Tick 统一做（三个内置视图同一条约定）。
        /// </summary>
        private void OnFieldChanged(ISleekField f, string text)
        {
            _query = (text == null) ? "" : text;
            // ★ 26.10.3.46：代码写入不算"玩家打的字"（详见 _writingFromCode）。
            if (_writingFromCode) return;
            if (OnQueryChanged == null) return;

            // 一个接入方的回调抛异常不该把输入框卡住（同 Shared 层对回调的处理）。
            try { OnQueryChanged(_query); }
            catch (Exception e) { CustomUIPlugin.Error("搜索框回调异常：" + e.Message); }
        }
    }
}
