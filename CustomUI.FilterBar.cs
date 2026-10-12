// ============================================================================
//  CustomUI · 可复用的「分类过滤按钮条」（StorageFilterBar，26.10.3.43）
// ============================================================================
//  给**第三方插件自己的界面**用：一行到几行的文字按钮，点一次只看这一类、再点一次取消。
//  以前这套只长在内置蓝图视图里（T7 / 26.10.3.35）—— 于是出现一个很刺眼的不对称：
//  第三方能"筛数据"（`StorageBlueprintFilters` 公开），却**不能给用户一个筛选按钮**，
//  因为按钮做在内置视图里、外面拿不到。
//
//  ★ 它**只画按钮、只管选中**；**每一类的计数由接入方算好传进来**。
//    这不是图省事，是纪律：计数**绝不能长在它自己参与的那条筛选链里** ——
//    统计范围会被自己的筛选改变 ⟹ "改了 A、B 跟着变"（README 坑 #63-② 就是这个形状：
//    用户实测「点了 Gun，那个按钮变成了 Skirt」）。
//  ★ 选中态按 **key** 不按下标：按钮按计数排序，接入方每轮重算后顺序会变；
//    按下标记选中 ⟹ 玩家点「枪」结果选中跑到「裙」上（同样是坑 #63-② 的原形）。
//
//  ⚠ 引擎调用全部照抄内置视图里的既有用法（`Glazier.Get().CreateButton()` /
//    `SleekColor(ESleekTint.BACKGROUND, …)` / `ESleekFontSize.Small` / 相对宽度），
//    没有一处凭印象写。`CreateFrame()` 是本文件唯一新引入的 API ——
//    已在 SDK `Glazier_UIToolkit.cs:31` 核过：返回 `ISleekElement`。
// ============================================================================
using System;
using SDG.Unturned;
using UnityEngine;

namespace CustomUI
{
    /// <summary>过滤条选中变化。<paramref name="hasSelection"/> = false 表示"取消筛选（看全部）"。</summary>
    public delegate void StorageFilterHandler(bool hasSelection, int key);

    /// <summary>
    /// **可复用的「分类过滤按钮条」**（`26.10.3.43`）。
    ///
    /// 典型用法（第三方给自己的区块加一条筛选）：
    /// <code>
    /// bar = new StorageFilterBar();
    /// bar.OnSelectionChanged = OnFilterChanged;        // (bool hasSelection, int key)
    /// bar.Build(myContainer);                          // 建一次，挂进你自己的容器
    /// // 数据变了就重填一遍（三个数组按序对应，used = 前几个有效）：
    /// bar.SetOptions(keys, labels, counts, used);      // ⚠ 计数由**你**算好
    /// // 你的筛选逻辑读：
    /// if (bar.HasSelection) { int k = bar.SelectedKey; ... }
    /// // 布局：这条占 bar.DesiredHeight（选项太少时是 0，等于不占位）
    /// </code>
    ///
    /// **三条语义边界**（比功能更容易被误解）：
    /// 1. **计数由接入方给** —— 组件不统计。理由见文件头（统计长在筛选链里，范围会被自己改变）。
    /// 2. **选中态按 key，不按下标** —— 你的按钮顺序每轮可能变（按计数排序）。
    /// 3. **选中的那一项必须留在 options 里** —— 否则玩家看不见"自己在筛什么"，也没法再点取消。
    ///    组件发现"选中了但不在选项里"会**报一次 warning**（不静默 ——
    ///    静默会让玩家以为"筛选按钮坏了"，而真正的原因是接入方漏了这一步）。
    ///
    /// ⚠ 生命周期与另两个公开组件一致：**关界面用 <see cref="Hide"/>（只藏不拆）**，
    ///   明确不要了才 <see cref="Discard"/>。
    /// </summary>
    public class StorageFilterBar
    {
        // ================= 旋钮（改完立即生效：会重排/重建按钮池）=================

        private int _columns = 3;
        private int _maxButtons = 9;
        private float _buttonHeight = 26f;
        private float _gap = 2f;
        private int _minOptionsToShow = 2;

        /// <summary>一行几个按钮（默认 3）。改完立即重排。</summary>
        public int Columns
        {
            get { return _columns; }
            set { _columns = Mathf.Max(1, value); LayoutButtons(); }
        }

        /// <summary>最多建几个按钮（默认 9）。改完立即重建按钮池。</summary>
        public int MaxButtons
        {
            get { return _maxButtons; }
            set { _maxButtons = Mathf.Max(1, value); RebuildPool(); }
        }

        /// <summary>按钮高度（默认 26）。⚠ 文字行高下限 24（README 坑 #21），别设更小。</summary>
        public float ButtonHeight
        {
            get { return _buttonHeight; }
            set { _buttonHeight = Mathf.Max(24f, value); LayoutButtons(); }
        }

        /// <summary>按钮之间的缝（默认 2）。</summary>
        public float Gap
        {
            get { return _gap; }
            set { _gap = Mathf.Max(0f, value); LayoutButtons(); }
        }

        /// <summary>
        /// 选项少于这个数时**整条不占高度**（默认 2）。
        /// 只有一类时筛选没有意义，不该白占几十像素 —— 蓝图视图当年就是这么定的。
        /// </summary>
        public int MinOptionsToShow
        {
            get { return _minOptionsToShow; }
            set { _minOptionsToShow = Mathf.Max(1, value); Refresh(); }
        }

        private string _itemNoun = "项";

        /// <summary>
        /// 提示语里"一个选项"叫什么（默认"项"）。接入方按自己的语义改：
        /// 蓝图视图设成"个配方"，于是提示仍是「只看「枪」 （3 个配方）」。
        /// ⚠ 这是**接入方的文案**，组件不该替它定词 —— 所以它是旋钮而不是写死的字符串。
        /// </summary>
        public string ItemNoun
        {
            get { return _itemNoun; }
            set { _itemNoun = string.IsNullOrEmpty(value) ? "项" : value; Refresh(); }
        }

        // ================= 数据（接入方每轮填；组件只引用不拷贝）=================

        private int[] keys;
        private string[] labels;
        private int[] counts;
        private int used;

        private bool hasSelection;
        private int selectedKey;

        /// <summary>选中变化回调（玩家点击时同步调用）。</summary>
        public StorageFilterHandler OnSelectionChanged;

        // ================= 控件 =================

        private ISleekElement element;
        private ISleekButton[] buttons;
        private bool built;
        private bool warnedMissingSelection;

        // ================= 只读状态 =================

        /// <summary>这条按钮条的根元素（挂进父容器后由父容器定位；宽度跟随父容器）。</summary>
        public ISleekElement Element { get { return element; } }

        public bool IsBuilt { get { return built; } }

        /// <summary>当前有没有在筛（false = 看全部）。</summary>
        public bool HasSelection { get { return hasSelection; } }

        /// <summary>当前选中的 key。⚠ <see cref="HasSelection"/> 为 false 时这个值没有意义。</summary>
        public int SelectedKey { get { return selectedKey; } }

        /// <summary>这一轮有效的选项个数。</summary>
        public int OptionCount { get { return used; } }

        /// <summary>这条要占的高度（选项太少时 0）。接入方按它给按钮条留位。</summary>
        public float DesiredHeight
        {
            get
            {
                if (used < _minOptionsToShow) return 0f;
                int rows = (used + _columns - 1) / _columns;
                return rows * (_buttonHeight + _gap);
            }
        }

        // ================= 生命周期 =================

        /// <summary>
        /// 建一次并挂进 <paramref name="parent"/>。重复调用只是重新挂父容器（不重复建池子）。
        /// ⚠ 宽度**跟随父容器**（相对值）—— 挂载前父容器宽度是 0，所以组件内部一律不写像素宽。
        /// </summary>
        public void Build(ISleekElement parent)
        {
            if (parent == null) return;

            if (built && element != null)
            {
                // ⚠ 不能先 `RemoveChild` —— uGUI 的 `RemoveChild` 末尾是 `InternalDestroy()`
                //   （`GlazierElementBase_uGUI.cs:253-283`）= 连按钮池一起销毁/回收，紧接着的
                //   `AddChild` 就挂了一具尸体 ⟹ **重新 Build 之后整条再也看不见**。
                //   `AddChild` 自己会从旧父容器摘走（同文件 `:321-347`），不需要先摘。
                try { parent.AddChild(element); } catch (Exception) { return; }
                // 可见性由**数据**决定（`used >= MinOptionsToShow`），别在这里写死 true ——
                // 那会让"选项不够就该收起来"这条失效（`Refresh` 是唯一口径）。
                Refresh();
                return;
            }

            element = Glazier.Get().CreateFrame();
            element.SizeScale_X = 1f;
            element.SizeOffset_X = 0f;
            element.SizeOffset_Y = 0f;
            parent.AddChild(element);

            built = true;
            RebuildPool();
            Refresh();
        }

        /// <summary>
        /// 只藏不拆（临时收起时用；按钮与文字都还在）。
        /// ⚠ **再显示靠下一次 <see cref="SetOptions"/>** —— 它会按"选项数够不够"重新算可见性。
        ///   只调 Hide 之后再想显示、却不重设数据，按钮条会一直是藏着的
        ///   （常驻面板复用最容易撞上：关界面时藏了，重开时没重设 ⟹ "我的筛选按钮不见了"）。
        /// </summary>
        public void Hide()
        {
            try { if (element != null) element.IsVisible = false; } catch (Exception) { }
        }

        /// <summary>真拆掉（下一次 <see cref="Build"/> 会重建）。调用方明确不要它了才用。</summary>
        public void Discard()
        {
            DetachButtons();
            buttons = null;
            try
            {
                if (element != null && element.Parent != null) element.Parent.RemoveChild(element);
            }
            catch (Exception) { }

            element = null;
            built = false;
            used = 0;
            hasSelection = false;
            warnedMissingSelection = false;
        }

        // ================= 数据入口 =================

        /// <summary>
        /// 填这一轮的可选项。三个数组**按序对应**、同长；<paramref name="optionUsed"/> 指定前几个有效。
        /// ⚠ 数组会被**直接引用**，不拷贝 —— 调用方每轮复用自己的缓冲区即可（零分配）。
        /// ⚠ **计数必须由调用方算好**，且**不能受本次筛选影响**（见类注释第 1 条）。
        /// </summary>
        public void SetOptions(int[] optionKeys, string[] optionLabels, int[] optionCounts, int optionUsed)
        {
            keys = optionKeys;
            labels = optionLabels;
            counts = optionCounts;

            int max = (optionKeys == null) ? 0 : optionKeys.Length;
            if (optionLabels != null && optionLabels.Length < max) max = optionLabels.Length;
            if (optionCounts != null && optionCounts.Length < max) max = optionCounts.Length;
            if (max > _maxButtons) max = _maxButtons;

            if (optionUsed < 0) optionUsed = 0;
            if (optionUsed > max) optionUsed = max;
            used = optionUsed;

            warnedMissingSelection = false;   // 新的一轮 ⟹ 允许再报一次
            Refresh();
        }

        /// <summary>
        /// 取消筛选（等价于玩家再点一次选中项）。
        /// ⚠ **不触发回调** —— 调用方自己知道是它清的；触发会形成"清 → 回调 → 又清"的回环。
        /// </summary>
        public void ClearSelection()
        {
            hasSelection = false;
            Refresh();
        }

        // ================= 内部 =================

        private void RebuildPool()
        {
            if (!built || element == null) return;

            DetachButtons();

            buttons = new ISleekButton[_maxButtons];
            for (int i = 0; i < _maxButtons; i++)
            {
                ISleekButton fb = Glazier.Get().CreateButton();
                fb.FontSize = ESleekFontSize.Small;
                fb.IsVisible = false;
                // ⚠ 回调**只捕获下标**，内容在点击那一刻从当前数据现读 ——
                //   按钮列表每轮会重排，闭包捕获"这一下是哪一类"就会点 A 筛 B
                //   （与行池那条坑同源：回调读行对象"当前字段"，别捕获当时的值）。
                int idx = i;
                fb.OnClicked += delegate (ISleekElement el) { OnButtonClicked(idx); };
                element.AddChild(fb);
                buttons[i] = fb;
            }

            LayoutButtons();
            Refresh();
        }

        private void DetachButtons()
        {
            if (buttons == null) return;
            for (int i = 0; i < buttons.Length; i++)
            {
                ISleekButton b = buttons[i];
                if (b == null) continue;
                try
                {
                    if (b.Parent != null) b.Parent.RemoveChild(b);
                }
                catch (Exception) { }
            }
        }

        private void LayoutButtons()
        {
            if (buttons == null) return;

            for (int i = 0; i < buttons.Length; i++)
            {
                ISleekButton b = buttons[i];
                if (b == null) continue;

                int col = i % _columns;
                int row = i / _columns;
                // ⚠ 宽度一律**相对值**（1/列数）：父容器宽度挂载前是 0，像素宽会算错。
                b.PositionScale_X = (float)col / _columns;
                b.SizeScale_X = 1f / _columns;
                b.PositionOffset_X = 2f;          // 相邻按钮之间留缝
                b.SizeOffset_X = -4f;
                b.PositionOffset_Y = row * (_buttonHeight + _gap);
                b.SizeOffset_Y = _buttonHeight;
            }

            Refresh();
        }

        private void OnButtonClicked(int idx)
        {
            // ⚠ 读**当前**字段：按钮列表可能刚变短，这个下标可能已经越界
            //   （同一趟里"选中项被挤出列表"是正常路径，见 TallyTypes 的补位逻辑）。
            if (keys == null || idx < 0 || idx >= used) return;
            int k = keys[idx];

            if (hasSelection && selectedKey == k) hasSelection = false;
            else { hasSelection = true; selectedKey = k; }

            Refresh();

            if (OnSelectionChanged != null)
            {
                try { OnSelectionChanged(hasSelection, selectedKey); }
                catch (Exception e)
                {
                    // 一个接入方的回调抛异常不该把按钮条卡住
                    CustomUIPlugin.Error("过滤条选中回调异常：" + e.Message);
                }
            }
        }

        private void Refresh()
        {
            if (element != null)
            {
                element.SizeOffset_Y = DesiredHeight;
                element.IsVisible = used >= _minOptionsToShow;
            }

            if (buttons == null) return;

            // ★ 选中项**必须留在选项里**（否则玩家看不见自己在筛什么、也没法再点一次取消）。
            //   接入方漏了这一步时**报一次** —— 不静默：静默会让玩家以为"筛选坏了"，
            //   而真正的原因是数据层漏了一行。
            if (hasSelection)
            {
                bool selFound = false;
                for (int i = 0; i < used; i++)
                {
                    if (keys[i] == selectedKey) { selFound = true; break; }
                }
                if (!selFound && !warnedMissingSelection)
                {
                    warnedMissingSelection = true;
                    CustomUIPlugin.Warn("过滤条：选中的 key=" + selectedKey +
                        " 不在这一轮的选项里 —— 玩家会看不见自己在筛什么，也无法再点一次取消。" +
                        "接入方得把选中的那一项留在 SetOptions 的列表里（见 StorageFilterBar 注释第 3 条）。");
                }
            }

            bool show = used >= _minOptionsToShow;
            for (int i = 0; i < buttons.Length; i++)
            {
                ISleekButton b = buttons[i];
                if (b == null) continue;

                bool on = show && i < used;
                b.IsVisible = on;
                if (!on) continue;

                string lb = (labels != null && i < labels.Length && labels[i] != null)
                    ? labels[i] : "";
                int c = (counts != null && i < counts.Length) ? counts[i] : 0;

                string txt = lb + " " + c;
                if (b.Text != txt) b.Text = txt;

                bool sel = hasSelection && keys[i] == selectedKey;
                // 选中态靠**背景亮度**区分（按钮没有"按下"状态可查，也不能读默认背景再还原）
                b.BackgroundColor = new SleekColor(ESleekTint.BACKGROUND, sel ? 1f : 0.5f);
                b.TooltipText = sel
                    ? ("正在只看「" + lb + "」—— 再点一次取消")
                    : ("只看「" + lb + "」（" + c + " " + _itemNoun + "）");
            }
        }
    }
}
