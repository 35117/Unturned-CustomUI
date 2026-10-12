// ============================================================================
//  CustomUI · 公开「列表面板」组件 StorageListPanel（一行一件的竖排清单）
// ============================================================================
//  这是本插件公开面里与 StorageGridPanel **对等**的另一半：网格是一行几格，
//  列表是一行一件。此前列表那套只长在内置视图 `StorageListView` 的私有字段里
//  （面板/行池全 private，且没有 Element/IsBuilt/DesiredHeight）⟹ 第三方拿到
//  `StorageListView` 的实例也**挂不上** `IStorageUiRegion.RegionPanel` ——
//  这是 2026-10-05《能力缺口盘点》§1 那条"结构性最大"的缺口。
//
//  ★ 26.10.3.32（T9）：把它提成**公开可复用组件**，公开面与 StorageGridPanel 对齐：
//      Element / IsBuilt / DesiredHeight / Build / IsUsableAt / Hide / Discard /
//      Refresh(Items | StorageSlotList) / MarkDirty / InvalidateAll / RebuildNow
//      + Title / EmptyText / MaxListHeight / TooltipHint / Interactive / ShowSearch /
//        SearchQuery / OnSlotClicked / OnSlotRightClicked
//  内置 `StorageListView` 现在只是它的**薄封装**（两条路合成一条：
//  "列表能复用"与"列表也有搜索栏"是同一件事）。
//
//  交互（与网格面板**同源**，绝不各写一遍 —— 见 StorageUiShared 的两个助手）：
//    · 数据源就是**当前打开的容器** → 内置页号搬运（取回 / 丢弃 / 托起落点）
//    · 否则挂了 OnSlotClicked / OnSlotRightClicked → 动作交给插件
//    · 都不是 → 只读（点击不产生任何效果，也不会静默改到别的箱子）
//  ⚠ 搬运走原版按页号的 RPC，坐标全部按 PlayerInventory.STORAGE 解释 —— 所以
//    "别的数据源"**宁可只读也不能猜错对象**（会把 A 箱的东西搬到 B 箱去）。
// ============================================================================
using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace CustomUI
{
    /// <summary>
    /// 公开的列表面板：一行一件，左图标 + 右文字（名字 · 数量 · 耐久），悬浮出 Tooltip。
    /// 用法与 <see cref="StorageGridPanel"/> 完全一样：<c>Build()</c> → 每帧
    /// <c>Refresh(数据源)</c> → 挂 <see cref="Element"/> → 关闭时 <c>Hide()</c>。
    /// </summary>
    public class StorageListPanel : IStorageUiPanel
    {
        // 尺寸常量（与内置列表视图同款）
        // ★ 26.10.3.66：行高 / 图标 / 字号原先写死，现在做成旋钮（默认 = 原值 ⟹ 观感不变）。
        private const float DEFAULT_ROW_HEIGHT = 42f;
        private const float HEAD_HEIGHT = 34f;
        private const float DEFAULT_ICON_SIZE = 34f;
        private const float ROW_GAP = 2f;
        private const float SEARCH_H = 30f;
        private const ESleekFontSize DEFAULT_LABEL_FONT = ESleekFontSize.Small;

        // ⚠ 行的标签高度 = 行高 ⟹ 行高 < 24 会**一个字都不渲染**（且不报错，坑 #21 / 铁律 #18）。
        private const float MIN_ROW_HEIGHT = 24f;

        private float _rowHeight = DEFAULT_ROW_HEIGHT;
        private float _iconSize = DEFAULT_ICON_SIZE;
        private ESleekFontSize _labelFontSize = DEFAULT_LABEL_FONT;

        /// <summary>★ 26.10.3.52：填充层**铺满整行**（内缩 0）—— 它自己带圆角（9 宫格），
        /// 内缩反而会露出一圈原版底色。见网格面板同一常量。</summary>
        private const float FILL_INSET = 0f;

        // ---- 控件 ----
        private ISleekBox panel;
        private ISleekLabel headLabel;
        private ISleekScrollView scroll;
        /// <summary>
        /// ★ 26.10.3.31 引入搜索框；★ 26.10.3.46 改用**公开可复用组件** `StorageSearchBox`
        /// —— 原来那份 `CreateStringField()` 与网格面板、蓝图视图**逐字相同**（README 坑 #68）。
        /// 搜索词的真源就是它的 <see cref="StorageSearchBox.Query"/>，本类不再自己存一份。
        /// </summary>
        private readonly StorageSearchBox searchBox = new StorageSearchBox();
        private ISleekLabel emptyLabel;

        // ---- 数据源（二维 Items 与扁平 StorageSlotList 二选一）----
        private Items _items;
        private StorageSlotList _flat;

        private int lastFingerprint = -1;

        // 面板是否已经挂进 Glazier 树 —— 首帧挂载前控件拿不到真实宽度，
        // 那时建出来的行会因宽度为 0 而不显示文字，必须挂载后重建一次。
        private bool wasAttached;

        // ================= 公开旋钮 =================
        // ★ 与 StorageGridPanel 同一条约定：这些是**属性**，setter 值一变就自动
        //   `MarkDirty()`（下一帧生效）。⚠ 值相同**不**标脏 —— 调用方每帧重设同一组值
        //   是常态，不去重就变成"每帧重建 → 图标全量重载"（坑 #30 那一族）。

        private float _maxListHeight = 510f;
        private string _title = "";
        private string _emptyText = "（空箱）";
        private string _tooltipHint = "";
        private bool _interactive = true;
        private bool _showSearch;

        /// <summary>列表区最大高度，超出则**内部滚动**。默认 510；挂在右列区块里建议 240 左右。**改完下一帧生效**。</summary>
        public float MaxListHeight
        {
            get { return _maxListHeight; }
            set
            {
                if (_maxListHeight != value)
                {
                    _maxListHeight = value;
                    SaveScrollPosition();     // ★ 26.10.3.47：高度上限变了 = 版面要变
                    RestoreScrollPosition();
                    MarkDirty();
                }
            }
        }

        /// <summary>头部前缀（例如「输入」「输出」）。留空 = 只显示统计，与内置视图同款。**改完下一帧生效**。</summary>
        public string Title
        {
            get { return _title; }
            set { if (!string.Equals(_title, value)) { _title = value; MarkDirty(); } }
        }

        /// <summary>空容器时那行字。**改完下一帧生效**（空容器分支每轮都会重新同步）。</summary>
        public string EmptyText
        {
            get { return _emptyText; }
            set { if (!string.Equals(_emptyText, value)) { _emptyText = value; MarkDirty(); } }
        }

        /// <summary>
        /// 追加到每行 Tooltip 末尾的一行**操作说明**（自定义按键语义的插件应该填）。
        /// **改完下一帧生效**。理由见 <see cref="StorageGridPanel.TooltipHint"/>。
        /// </summary>
        public string TooltipHint
        {
            get { return _tooltipHint; }
            set { if (!string.Equals(_tooltipHint, value)) { _tooltipHint = value; MarkDirty(); } }
        }

        // ================= 外观旋钮（26.10.3.66）=================
        //   ★ 这三个原先写死（`ROW_HEIGHT` / `ICON_SIZE` / 字号 `Small`）；默认 = 原值 ⟹ **观感不变**。
        //   ⚠ 行高是"**下限**"：`RowPitch()` = `max(RowHeight, 图标 + 8, 字号理想高度 + 8, 24)`
        //     ⟹ 调大图标或字号会自动把行撑开，且**永远不会低于 24**
        //       （行高 < 24 时行内标签一个字都不渲染，且不报错 —— 坑 #21 / 铁律 #18）。

        /// <summary>行内图标边长（默认 **34**）。行高会自动跟着长。⚠ 夹到 ≥ 8。</summary>
        public float IconSize
        {
            get { return _iconSize; }
            set
            {
                float v = value < 8f ? 8f : value;
                if (_iconSize == v) return;
                _iconSize = v;
                MarkDirty();
            }
        }

        /// <summary>
        /// 每一行的**行高**（默认 **42**）。它同时是"**下限**"：实际行高 =
        /// `max(它, 图标 + 8, 字号理想高度 + 8, 24)`。⚠ 夹到 ≥ <see cref="MIN_ROW_HEIGHT"/>。
        /// </summary>
        public float RowHeight
        {
            get { return _rowHeight; }
            set
            {
                float v = value < MIN_ROW_HEIGHT ? MIN_ROW_HEIGHT : value;
                if (_rowHeight == v) return;
                _rowHeight = v;
                MarkDirty();
            }
        }

        /// <summary>
        /// **实际用的行高**（只读）= `max(RowHeight, 图标 + 8, 字号理想高度 + 8, 24)`。
        /// ★ 有了它，"调大 <see cref="IconSize"/> / <see cref="LabelFontSize"/> 会把行撑开"
        ///   与"永远不会低于 24"才是**可断言的事实**。
        /// </summary>
        public float EffectiveRowHeight
        {
            get { return RowPitch(); }
        }

        /// <summary>
        /// 行内文字字号（默认 <see cref="ESleekFontSize.Small"/>）。
        /// ★ 行高按它的**理想高度**自动加高（8 / 20 / 30 / 50 / 70），不会把字挤掉。
        /// </summary>
        public ESleekFontSize LabelFontSize
        {
            get { return _labelFontSize; }
            set
            {
                if (_labelFontSize == value) return;
                _labelFontSize = value;
                MarkDirty();
            }
        }

        /// <summary>
        /// **实际行高** —— `max(RowHeight, 图标 + 8, 字号理想高度 + 8, 24)`。
        /// 所有几何都读它，别直接读 `_rowHeight`（那样调大图标就会溢出；布局只有一个口径）。
        /// </summary>
        private float RowPitch()
        {
            float need = _rowHeight;
            float byIcon = _iconSize + 8f;
            if (byIcon > need) need = byIcon;
            float byText = StorageUiShared.IdealTextHeight(_labelFontSize) + 8f;
            if (byText > need) need = byText;
            if (need < MIN_ROW_HEIGHT) need = MIN_ROW_HEIGHT;
            return need;
        }

        /// <summary>日志前缀（内置视图传「列表」）。⚠ 只影响日志文本，改它不必标脏。</summary>
        public string LogName = "列表";

        /// <summary>
        /// 是否允许点击搬运。⚠ 只有当数据源就是**当前打开的容器**时才真的生效，
        /// 否则自动降级为只读。**改完下一帧生效**。
        /// </summary>
        public bool Interactive
        {
            get { return _interactive; }
            set { if (_interactive != value) { _interactive = value; MarkDirty(); } }
        }

        /// <summary>
        /// 是否在头部下方显示一行搜索框（默认 false = **完全不改变现有观感**）。
        /// 匹配面在共享层：中文名 / 英文名 / 拼音首字母 / 全拼（**前缀单调**，见 Shared 注释）。
        ///
        /// ⚠ 输入框是在 <see cref="Build"/> 里建的 ⟹ **改了它必须重建面板才看得见**，
        ///   所以 setter 直接 <see cref="Discard"/>（下次 `Build()` 会重建）。
        ///   ⟹ 调用方在 `Build()` **之前**设好它。
        /// </summary>
        public bool ShowSearch
        {
            get { return _showSearch; }
            set { if (_showSearch == value) return; _showSearch = value; if (panel != null) Discard(); else MarkDirty(); }
        }

        /// <summary>
        /// 搜索词（空 = 不筛）。改完下一帧生效。
        /// ⚠ 过滤发生在 **`CollectGroups` 之前**（过滤的是"哪些条目参与分组"），
        ///   不是"分完组再藏几行" —— 后者会让头部件数与画面不符。
        /// ⚠ 它是**这一次会话**的状态：Hide() 会清掉（见那里）。
        /// </summary>
        public string SearchQuery
        {
            get { return searchBox.Query; }
            set { if (string.Equals(searchBox.Query, value)) return; searchBox.Query = value; MarkDirty(); }
        }

        private StorageSlotHitHandler _onSlotClicked;
        private StorageSlotHitHandler _onSlotRightClicked;

        /// <summary>
        /// 左键点击回调。挂了它 = **动作整个交给插件**（组件只报"哪一行被点了"）。
        /// ⚠ 只有当数据源**不是**当前打开的容器时才走它；是的话内置页号搬运优先，
        ///   回调不会触发（组件会**显式记一次日志**告诉你，不会静默）。
        /// ⚠ 改它要让**已建好的行**重新接线，所以 setter 会作废全部绑定。
        /// </summary>
        public StorageSlotHitHandler OnSlotClicked
        {
            get { return _onSlotClicked; }
            set { if (_onSlotClicked != value) { _onSlotClicked = value; InvalidateAll(); MarkDirty(); } }
        }

        /// <summary>右键回调（与左键同一套语义，通常放「次要动作」：整组取出、打开管理…）。</summary>
        public StorageSlotHitHandler OnSlotRightClicked
        {
            get { return _onSlotRightClicked; }
            set { if (_onSlotRightClicked != value) { _onSlotRightClicked = value; InvalidateAll(); MarkDirty(); } }
        }

        /// <summary>挂了任一回调 = 宿主自己接管点击（此时不再要求数据源是打开中的容器）。</summary>
        private bool HasSlotCallbacks
        {
            get { return _onSlotClicked != null || _onSlotRightClicked != null; }
        }

        private StorageCellFillHandler _cellFill;

        /// <summary>
        /// ★ 26.10.3.51：**每一行铺什么颜色**由你定（与 <c>StorageGridPanel.CellFill</c> **同名同义**：
        /// 网格那边叫"格"、这边叫"行"，是同一个概念）。返回 alpha 0 = 这一行不铺。
        ///
        /// <code>
        /// list.CellFill = delegate (StorageSlotHit hit)
        /// {
        ///     return (hit.Item != null &amp;&amp; hit.Quality == 0) ? new SleekColor(gold) : default(SleekColor);
        /// };
        /// </code>
        ///
        /// ⚠ 它是**每轮刷新的纯函数**（每个可见行一次）：别在里面改数据、别标脏。
        /// ⚠ 填充层铺在行的图标与文字**之下**；它会盖住悬停高亮边框（内缩 4 像素后那一圈）。
        /// ⚠ 走**真彩色**那条路（`ISleekImage` + 白贴图 + `TintColor`），不是 `BackgroundColor`
        ///   —— 后者在盒子上改不了色相（见 `StorageUiFill` 的注释）。
        /// ⚠ 换回调**不需要**重建：颜色每轮重算。
        /// </summary>
        public StorageCellFillHandler CellFill
        {
            get { return _cellFill; }
            set { _cellFill = value; }
        }

        // ================= 根元素 / 尺寸 =================

        /// <summary>要挂进 Glazier 树的那个根元素（宿主/你自己都只认它）。</summary>
        public ISleekElement Element
        {
            get { return panel; }
        }

        /// <summary>面板是否已建好（Build 之后为 true）。</summary>
        public bool IsBuilt
        {
            get { return panel != null; }
        }

        /// <summary>本面板当前想要的高度（宿主排版按它累加）。</summary>
        public float DesiredHeight
        {
            get { return panel == null ? 0f : panel.SizeOffset_Y; }
        }

        // ================= 生命周期 =================

        /// <summary>
        /// 建面板（幂等）。⚠ 只在没有面板时建 —— 反复建会让上一代的行控件失去父节点
        /// （它们不会被复用，图标要全部重新排队渲染）。
        /// </summary>
        public void Build()
        {
            if (panel != null) return;

            panel = Glazier.Get().CreateBox();
            panel.PositionOffset_X = 0f;
            panel.PositionOffset_Y = 0f;
            panel.SizeScale_X = 1f;
            panel.SizeOffset_X = -20f;   // 留出滚动条宽度
            panel.SizeOffset_Y = HEAD_HEIGHT;
            panel.BackgroundColor = new SleekColor(ESleekTint.BACKGROUND, 0.9f);

            headLabel = Glazier.Get().CreateLabel();
            headLabel.PositionOffset_X = 8f;
            headLabel.PositionOffset_Y = 4f;
            headLabel.SizeScale_X = 1f;
            headLabel.SizeOffset_X = -16f;
            headLabel.SizeOffset_Y = 26f;
            // 字号 Small、主题标准字色：Medium 下中文更容易因宽度不足被裁切；
            // 自定义青色在部分主题里会与背景混淆。
            headLabel.FontSize = ESleekFontSize.Small;
            headLabel.TextAlignment = TextAnchor.MiddleLeft;
            headLabel.TextColor = new SleekColor(ESleekTint.FONT);
            headLabel.Text = string.IsNullOrEmpty(Title) ? "物品" : Title;
            panel.AddChild(headLabel);

            // ★ 26.10.3.46：控件本体交给**公开可复用组件** `StorageSearchBox`——
            //   原来这段 `CreateStringField()` 与网格面板、蓝图视图**逐字相同**。
            //   ⚠ 旋钮**显式设一遍**，不靠组件默认值。
            searchBox.Show = ShowSearch;
            searchBox.Height = SEARCH_H;
            searchBox.TopOffset = HEAD_HEIGHT;
            searchBox.Placeholder = "搜索物品名 / 拼音";
            searchBox.OnQueryChanged = OnSearchTextChanged;
            searchBox.Build(panel);

            scroll = Glazier.Get().CreateScrollView();
            scroll.PositionOffset_X = 4f;
            // ⚠ 高度只有 SearchRowHeight() **一个来源**（这行与面板高度共用它），别两处各写一份
            scroll.PositionOffset_Y = HEAD_HEIGHT + SearchRowHeight();
            scroll.SizeScale_X = 1f;
            scroll.SizeOffset_X = -8f;
            scroll.SizeOffset_Y = 0f;        // 每轮按「内容高 vs 上限」重算

            // ⚠⚠ 这两行缺一不可（本项目已验证用法）：
            //  · ScaleContentToWidth：内容宽跟随视口。缺了它内容宽为 0 → 整行宽度塌成 0
            //    → 文字不渲染、行背景不可见、鼠标也落不到行上（Tooltip 因此不出现）；
            //    而图标是固定尺寸仍可见 ⟹ 实测特征就是"整列只有图标"。
            //  · HandleScrollWheel：响应鼠标滚轮，缺了只能拖滚动条。
            scroll.ScaleContentToWidth = true;
            scroll.HandleScrollWheel = true;

            panel.AddChild(scroll);
        }

        /// <summary>
        /// 常驻面板是否还能接着用：必须仍挂在**当前**的挂载点上。
        /// 切关/换服会重建整个 PlayerUI，旧面板随旧画布一起销毁，但 panel.Parent
        /// 还指着那个废掉的实例 —— 与当前挂载点一比就穿帮，于是丢弃重建。
        /// </summary>
        public bool IsUsableAt(ISleekElement spot)
        {
            if (panel == null || scroll == null || spot == null) return false;
            try { return ReferenceEquals(panel.Parent, spot); }
            catch (Exception) { return false; }
        }

        /// <summary>搜索框那一行占的高度（没开 = 0）。**唯一的**高度来源，见 Build 的调用点。</summary>
        private float SearchRowHeight()
        {
            return searchBox.DesiredHeight;
        }

        /// <summary>
        /// 用户在搜索框里打字：把新词交给 <see cref="SearchQuery"/>，再**无条件**标脏，
        /// **不在这里重建** —— 重建由下一帧的 `Refresh` 统一做。
        /// ⚠ 为什么不能只靠 setter 标脏：玩家打字时组件在回调**之前**就把 `Query` 设成新词了
        ///   ⟹ 这里赋同一个值会撞上 setter 的"相同就 return"而早退，不打字不筛（26.10.3.46 踩到）。
        /// </summary>
        private void OnSearchTextChanged(string text)
        {
            if (!string.Equals(searchBox.Query, text)) searchBox.Query = text;
            MarkDirty();
        }

        /// <summary>只藏不拆（关闭时用它；重开时行池与图标贴图都还在）。</summary>
        public void Hide()
        {
            // ⚠ 绝不调 RemoveChild —— Glazier 的 RemoveChild 末尾是 GameObject.Destroy，
            //   行与已渲染的图标贴图会一起没掉，而我们的图标不满足原版 getIcon 的缓存判据
            //   （统一尺寸 + 带 state）→ 进不了 iconCache，只能重新排队渲染。
            try { if (panel != null) panel.IsVisible = false; } catch (Exception) { }

            groups.Clear();
            lastFingerprint = -1;
            // 关闭一次面板 = 一次会话结束 ⟹ 只报一次的警告该重新可用
            // （否则"第二次遇到时完全无声"，能力缺口盘点 §2.4）。
            ResetClickWarnings();
            // ⚠ 数据源引用也要放手：留着一个已销毁容器的引用，下次打开若忘了换源
            //   就会拿旧数据刷新（而且它可能已经不属于这个世界了）。
            _items = null;
            _flat = null;
            // 搜索词同理：它是"**这一次会话**的状态"。留着会让玩家打开下一个箱子时
            // 发现"怎么只剩几件"（其实是上一次搜的词）。
            SearchQuery = "";
        }

        /// <summary>真丢弃常驻（旧画布已销毁、或换数据源重建时）。下次 Build 会重新建。</summary>
        public void Discard()
        {
            // ★ 26.10.3.47：拆之前先记下位置（下次 Build 的新滚动容器会从顶部开始）。
            SaveScrollPosition();

            try { if (panel != null && panel.Parent != null) panel.Parent.RemoveChild(panel); }
            catch (Exception) { }

            rows.Clear();
            emptyLabel = null;
            panel = null;
            headLabel = null;
            scroll = null;
            searchBox.Discard();
            wasAttached = false;
            ResetClickWarnings();
            _items = null;
            _flat = null;

            RestoreScrollPosition();      // 下一次挂载/刷新后把位置写回去
        }

        /// <summary>作废所有行的绑定（下一次 Refresh 会整批重建）。</summary>
        public void InvalidateAll()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].Fingerprint = int.MinValue;
            }
        }

        // ================= 刷新 =================

        /// <summary>
        /// 用一个新的 <c>Items</c>（原版二维容器）刷一次。⚠ 每帧调也没关系：内部比指纹，
        /// 内容没变就一个控件都不碰（图标不会因为"只是挪了位置"而重载）。
        /// </summary>
        public void Refresh(Items source)
        {
            _flat = null;
            _items = source;
            RefreshCore(StorageUiShared.FingerprintItems(source));
        }

        /// <summary>
        /// 用一串**扁平槽位**刷一次（没有二维摆放语义的数据源，例如工业插件的
        /// <c>List&lt;Item&gt;</c> + 总格数容量）。合并、排序、图标、Tooltip 与 `Items` 路径
        /// **同一份实现**；差别只有两处：头部不报「最大连续空位」（那需要二维占用表，
        /// 报了就是假信息），以及**永远只读**（扁平槽位没有页号）。
        /// </summary>
        public void Refresh(StorageSlotList source)
        {
            _flat = source;
            _items = null;
            RefreshCore(source == null ? -1 : StorageUiShared.FingerprintJars(source.Jars));
        }

        private void RefreshCore(int fp)
        {
            if (panel == null) return;

            if (fp != lastFingerprint)
            {
                lastFingerprint = fp;
                RebuildRows();
                if (panel == null) return;   // 重建里出过异常就不再继续
            }

            // ⚠ 首次挂进 Glazier 树之后必须**重建一次**：
            //   挂载前面板拿不到真实宽度（内容宽为 0），那一刻建出来的行宽度为 0
            //   → 行内文字（相对宽）落成负值 → 不渲染，而且不会自己恢复。
            //   实测特征：刚打开时整列只有图标，一旦放入物品触发重建就全都好了。
            if (panel.Parent != null && !wasAttached)
            {
                wasAttached = true;
                InvalidateAll();   // 挂载前建的行整体作废
                RebuildRows();
            }

            // ★ 26.10.3.47：版面变了 / 面板刚重建过 ⟹ 把玩家原来的滚动位置写回去（放最后，
            //   必须等内容高算完）。
            ApplyPendingScroll();
        }

        /// <summary>让**所有**行都判定为「需要重建」并立刻重建一轮（换旋钮后想马上看效果时用）。</summary>
        public void RebuildNow()
        {
            InvalidateAll();
            RebuildRows();
        }

        /// <summary>当前数据源是不是扁平槽位（头部统计与交互都按它分流）。</summary>
        private bool IsFlat
        {
            get { return _flat != null; }
        }

        // ================= 行刷新（按指纹复用，只有换了物品才整行重建） =================

        /// <summary>
        /// 一行 = 「图标 + 名字/数量/耐久」的**可复用单元**。
        ///
        /// ⚠ 两条经验都是实测踩出来的：
        ///   1) 不能按**索引**比对新旧数据 —— `Items` 数组在取走物品后会前移，
        ///      后面每行看到的物品都换了 ⟹ 「取走一件」会让**所有**图标重载。
        ///      → 改为按**物品指纹**认领行。
        ///   2) 也不能「就地更新已有控件的图标」—— `SleekItemIcon` 的贴图要靠
        ///      `ItemTool.getIcon` 的**异步回调**才换上，同一元素二次 `Refresh` 不可靠；
        ///      而「移除图标子元素再新建」又会牵连同父节点下的兄弟元素（实测把文字一起弄没了）。
        ///      → 换物品时**整行重建**，结构上不可能出错。
        ///   代价仍可控：只有「行真的换了物品」才重建，单纯挪位置不碰控件。
        /// </summary>
        private class RowUi
        {
            public ISleekButton Button;     // 整行的按钮（图标与文字都是它的子元素）
            /// <summary>★ 26.10.3.51：这一行的填充层（`CellFill` 上色用）。建行时就建好，一直留着。</summary>
            public StorageUiFill Fill;
            public int Fingerprint;         // 当前显示物品的指纹（**不含坐标**）
            public bool InUse;
            public byte X;                  // 该行当前代表哪个格子（回调时**现读**，不闭包捕获）
            public byte Y;
            public int SlotIndex;           // 扁平槽位下标（回调模式用；二维模式为 -1）
            public ushort BoundItemId;      // 绑定物品的 id（二维回调前核对坐标有没有过期）
        }

        private readonly List<RowUi> rows = new List<RowUi>();

        /// <summary>合并 + 排序后的**显示项**（复用容器，避免每帧产生垃圾）。
        /// 顺序与"哪些物品算同一件"都由共享层定义 —— 两个视图必须同款，否则切换会「跳位置」。</summary>
        private readonly List<StorageUiShared.ItemGroup> groups =
            new List<StorageUiShared.ItemGroup>();

        /// <summary>过完搜索的 jar（复用容器）—— 过滤发生在分组**之前**，见 RebuildRows。</summary>
        private readonly List<ItemJar> visible = new List<ItemJar>();

        private void RebuildRows()
        {
            if (scroll == null || headLabel == null) return;

            bool flat = _flat != null;
            Items itemsSrc = _items;

            // ★ 先过搜索、再分组 —— 过滤的是"哪些条目参与分组"。
            //   ⚠ 不是"分完组再藏几行"：那样头部件数与画面会对不上（"同一个数只在一处打折"那一族）。
            visible.Clear();
            int allJars = 0;
            if (flat)
            {
                // 扁平入口（数字箱那类记账清单）**不做取证** —— 它的 amount 是记账件数，
                // 取证会误报 ⟹ 它自己遍历，不走 CollectJars。
                List<ItemJar> src = _flat.Jars;
                bool filterOn = !string.IsNullOrEmpty(searchBox.Query);
                for (int i = 0; i < src.Count; i++)
                {
                    ItemJar jar = src[i];
                    if (jar == null || jar.item == null) continue;
                    allJars++;
                    if (filterOn && !StorageUiShared.MatchesQuery(jar.GetAsset(), searchBox.Query)) continue;
                    visible.Add(jar);
                }
                StorageUiShared.CollectGroups(visible, groups, _flat.MergeByIdOnly, _flat.MergeContentPages);
            }
            else if (itemsSrc != null)
            {
                // 二维源走共享层的**唯一收集点**（取证与过滤都在那里，别自己遍历）
                int matchedJars = 0;
                StorageUiShared.CollectJars(itemsSrc, visible, searchBox.Query, out allJars, out matchedJars);
                StorageUiShared.CollectGroups(visible, groups);
            }
            // 两个源都空 → visible 保持空（按"空容器"渲染）

            int count = groups.Count;
            int totalItems = 0;
            for (int i = 0; i < count; i++) totalItems += groups[i].Count;

            // 头部统一走共享层（与网格面板同源）。
            string stats;
            if (flat)
            {
                stats = StorageUiShared.BuildHeaderTextFlat(
                    // 调用方给了真源就用它（数字箱的占用判定在工业那边），
                    // 否则才自己估 —— 自己估只会算 Σ size_x×size_y，会**低报**多堆叠的物品。
                    (_flat.OccupiedCells >= 0) ? _flat.OccupiedCells
                        : StorageUiShared.OccupiedCellsOf(_flat.Jars),
                    _flat.CapacityCells, totalItems, count);
            }
            else if (itemsSrc != null)
            {
                stats = StorageUiShared.BuildHeaderText(
                    StorageUiShared.ComputeSpace(itemsSrc), totalItems, count);
            }
            else
            {
                stats = "";
            }
            // 筛选中要**显式**说明 —— 否则"件数变少了"看起来像物品丢了。两个数出自同一次遍历。
            if (ShowSearch && !string.IsNullOrEmpty(searchBox.Query))
            {
                stats = stats + "（筛选中：显示 " + totalItems + " / 共 " + allJars + " 件）";
            }
            headLabel.Text = string.IsNullOrEmpty(Title) ? stats : (Title + " · " + stats);

            // ★ 26.10.3.46：不再需要"每帧把 SearchQuery 同步到输入框"——
            //   组件在 Query 的 setter 里当场同步（见 StorageSearchBox）。

            // 点击走哪条路（与网格面板同一条判定）：
            //   · 数据源就是当前打开的容器 → 内置页号搬运（任何环境可用）
            //   · 否则挂了回调 → 交给插件
            //   · 都不是 → 只读
            bool pageClick = !flat && ResolveInteractive(itemsSrc);
            bool callbackClick = !pageClick && HasSlotCallbacks;

            // 数据源就是当前打开的容器、同时又挂了回调 ⟹ 内置搬运优先，回调**永不触发**。
            // 给一次明确日志（原先静默 —— 接入方挂了回调却什么都没发生，查不出原因）。
            if (pageClick && HasSlotCallbacks && !_callbackShadowedWarned)
            {
                _callbackShadowedWarned = true;
                CustomUIPlugin.Warn(LogName + "：数据源就是当前打开的容器 ⟹ 点击走内置搬运，" +
                    "你挂的 OnSlotClicked / OnSlotRightClicked 不会触发。" +
                    "要让回调生效：把 Interactive 设成 false（自己处理动作），或换成别的数据源。");
            }

            // ① 先把所有行标为「未被认领」
            for (int i = 0; i < rows.Count; i++) rows[i].InUse = false;

            int reusedRows = 0;
            int rebuiltRows = 0;

            // ⚠ y 相对**滚动内容区**（行挂在 scroll 里，不再加头部高度）
            float y = 0f;
            for (int i = 0; i < count; i++)
            {
                StorageUiShared.ItemGroup g = groups[i];
                if (g == null || g.First == null) continue;

                int fp = GroupFingerprint(g);

                // ② 优先认领「指纹相同的闲置行」—— 这一步是图标不被重载的关键
                RowUi row = ClaimRow(fp);
                if (row != null)
                {
                    reusedRows++;
                }
                else
                {
                    // ③ 没有指纹相同的 → 拿一个闲置行**整行重建**；池子空了才新建
                    //    ⚠ 新建必须走 NewClaimedRow()（建立即认领）—— 直接 new 会漏标，见那个方法
                    row = ClaimAnyFreeRow();
                    if (row == null) row = NewClaimedRow();
                    RebuildRow(row, g, fp, pageClick, callbackClick);
                    rebuiltRows++;
                }

                // ④ 位置与命中信息每轮都更新（很便宜，不碰图标）。
                //    坐标用**代表物品**的 —— 交互只作用在那一件上。
                row.X = g.First.x;
                row.Y = g.First.y;
                // 回调模式要的是「扁平清单里第几个」—— ⚠ 不能直接用 i：groups 是合并+排序后的显示项
                row.SlotIndex = (callbackClick && flat) ? _flat.Jars.IndexOf(g.First) : -1;
                row.BoundItemId = (g.First.item == null) ? (ushort) 0 : g.First.item.id;

                row.Button.PositionOffset_Y = y;
                row.Button.IsVisible = true;

                // ★ 26.10.3.51：填充色每轮重算（颜色没变时组件内部一个 int 比较就早退）
                ApplyCellFill(row, g);

                y += RowPitch() + ROW_GAP;
            }

            // ⑤ 多余的闲置行隐藏起来留作池子，**不销毁**
            for (int i = 0; i < rows.Count; i++)
            {
                if (!rows[i].InUse && rows[i].Button != null)
                {
                    rows[i].Button.IsVisible = false;
                }
            }

            // 空容器提示 —— 否则用户看到「一块空面板」会以为界面坏了
            if (count == 0)
            {
                if (emptyLabel == null)
                {
                    emptyLabel = Glazier.Get().CreateLabel();
                    emptyLabel.PositionOffset_X = 8f;
                    emptyLabel.SizeScale_X = 1f;
                    emptyLabel.SizeOffset_X = -16f;
                    emptyLabel.SizeOffset_Y = 24f;
                    emptyLabel.FontSize = ESleekFontSize.Small;
                    emptyLabel.TextAlignment = TextAnchor.MiddleLeft;
                    emptyLabel.TextColor = new SleekColor(Color.gray);
                    scroll.AddChild(emptyLabel);   // 与行同级，挂在滚动内容里
                }
                // ★ 每轮同步 —— 原先只在「创建那一刻」读一次 EmptyText，之后改了永不生效
                //   （2026-10-05 能力缺口盘点 §2.1）。空容器分支本来就很便宜。
                emptyLabel.Text = EmptyText;
                emptyLabel.PositionOffset_Y = y + 4f;
                emptyLabel.IsVisible = true;
                y += 28f;
            }
            else if (emptyLabel != null)
            {
                emptyLabel.IsVisible = false;
            }

            // ---- 高度策略：内容高 vs 上限，超出则**列表内部滚动** ----
            // 不能让面板无限长：条目一多会把下方的「附近物品」区顶得很远
            // （实测反馈「上下高度太高了」，同时要求「加滑动条以显示完全物品」）。
            scroll.ContentSizeOffset = new Vector2(0f, y);
            float listHeight = Mathf.Min(y, MaxListHeight);
            scroll.SizeOffset_Y = listHeight;
            panel.SizeOffset_Y = HEAD_HEIGHT + SearchRowHeight() + listHeight + 6f;

            lastFingerprint = CurrentFingerprint();

            // 诊断用：一行汇总。
            //   · 「重建」长期等于「总项数」→ 行池没起作用（图标全量重载）。
            //   ★ 26.10.3.33：还要报**可见行数** —— 老日志只报"滚动容器控件 N 个"，
            //     而"控件挂进容器了却 IsVisible = false"是**同一种病**：
            //     用户 2026-10-05 报的「N 项只显示 M 行」正是这种（控件在树里、就是没显示）。
            //     ⟹ 控件数 ≥ 项数**不代表**画面正常，可见行数才是那条判据。
            int visibleRows = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Button != null && rows[i].Button.IsVisible) visibleRows++;
            }
            if (visibleRows != count)
            {
                CustomUIPlugin.Warn(LogName + "：可见行数 " + visibleRows + " ≠ 显示项数 " + count +
                    " —— 有行没显示出来（行池认领漏了？）。这就是「N 项只显示 M 行」的确诊特征。");
            }
            CustomUIPlugin.Log(LogName + "刷新：复用 " + reusedRows + " 行 / 重建 " + rebuiltRows +
                " 行（共 " + count + " 项，可见 " + visibleRows + " 行，滚动容器控件 " +
                scroll.GetChildCount() + " 个）");
        }

        private int CurrentFingerprint()
        {
            return (_flat != null)
                ? StorageUiShared.FingerprintJars(_flat.Jars)
                : StorageUiShared.FingerprintItems(_items);
        }

        /// <summary>
        /// 行指纹：决定「这一行要不要重建」。
        ///
        /// 除了物品身份，还必须含**合并件数与总数** —— 合并显示时「9 件」变「8 件」
        /// 只体现在这两个数上，漏了就会出现「拿走一件但 ×9 不更新」。
        ///
        /// ⚠ 千万**不要**把 `item.state` 混进来 —— 工业机械物品的 state 里存着
        ///   电量/运行进度这类**每帧都在变**的数据。一旦入指纹，指纹就每帧都变，
        ///   于是每帧重建 → 图标（异步加载）永远来不及显示。
        ///   坐标同理：位置变化只需挪 PositionOffset_Y，不该触发重建。
        /// </summary>
        private static int GroupFingerprint(StorageUiShared.ItemGroup g)
        {
            unchecked
            {
                int h = g.First.item.id;
                h = h * 397 ^ g.First.item.quality;
                h = h * 397 ^ g.Count;
                h = h * 397 ^ g.TotalAmount;
                return h;
            }
        }

        /// <summary>找一个指纹相同的闲置行（复用它的图标，不重载）。</summary>
        private RowUi ClaimRow(int fp)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                RowUi r = rows[i];
                if (!r.InUse && r.Fingerprint == fp)
                {
                    r.InUse = true;
                    return r;
                }
            }
            return null;
        }

        /// <summary>
        /// 池子里新建一行 —— **建立即认领**（`InUse = true`）。
        ///
        /// ⚠⚠ 这条不是洁癖，是本项目刚付过学费的一条（用户 2026-10-05 实测报的）：
        ///   末尾第 ⑤ 步「清理闲置行」是按 `InUse` 判的，新建行若留 `InUse = false`，
        ///   **同一趟里它就会被自己人收走** —— 刚建好的那行当场被藏起来。
        ///   症状是「**N 项只显示 M 行**」，而且**只在池子需要长大的那一趟**出现
        ///   ⟹ 「关掉界面重开就好了」（自愈型假象，最难查）。
        ///   ⟹ 池子只留这**一个**新建入口，就不会再有第二条"忘了标"的路。
        /// </summary>
        private RowUi NewClaimedRow()
        {
            RowUi r = new RowUi();
            r.InUse = true;
            rows.Add(r);
            return r;
        }

        /// <summary>找一个任意闲置行（内容会被重填）。</summary>
        private RowUi ClaimAnyFreeRow()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                RowUi r = rows[i];
                if (!r.InUse)
                {
                    r.InUse = true;
                    return r;
                }
            }
            return null;
        }

        /// <summary>
        /// **整行重建**这一行的控件（Button + 图标 + 文字）。
        ///
        /// ⚠ 为什么是「重建整行」而不是「更新已有控件」：
        ///   · `SleekItemIcon` 的贴图要等 `ItemTool.getIcon` 的**异步回调**才换上，
        ///     在同一个元素上二次 `Refresh` 不可靠（实测：新放入的物品不显示图标）；
        ///   · 「只移除图标子元素再新建」会牵连同一父节点下的兄弟元素
        ///     （实测：那一行的**文字一起消失**）；
        ///   · 整行换新元素是**结构上不可能出错**的做法。
        ///   代价仍可控：只有「行真的换了物品」才走到这里，单纯挪位置不重建。
        /// </summary>
        private void RebuildRow(RowUi r, StorageUiShared.ItemGroup g, int fp,
            bool pageClick, bool callbackClick)
        {
            // 图标与文字取**代表物品**（同组物品完全相同，取哪一件都一样）
            ItemJar jar = g.First;
            ItemAsset asset = jar.GetAsset();

            // 先把上一代的整行摘掉（首个行 Button 为 null，跳过）
            if (r.Button != null)
            {
                try { scroll.RemoveChild(r.Button); } catch (Exception) { }
                r.Button = null;
            }

            ISleekButton btn = Glazier.Get().CreateButton();
            btn.PositionOffset_X = 0f;
            btn.PositionOffset_Y = 0f;
            btn.SizeScale_X = 1f;
            btn.SizeOffset_X = -18f;   // 留出滚动条宽度
            btn.SizeOffset_Y = RowPitch();
            btn.Text = "";             // 文字用独立 label，避免被图标遮挡
            btn.BackgroundColor = new SleekColor(ESleekTint.BACKGROUND, 0.55f);
            // ★ 26.10.3.51 填充层：**必须是本行的第一个子元素**（同一父容器里后加的盖在先加的上面）。
            //   颜色由 `CellFill` 每轮解析（见 ApplyCellFill），这里只建壳。
            r.Fill = new StorageUiFill();
            r.Fill.Inset = FILL_INSET;
            r.Fill.Build(btn);
            // ★ 26.10.3.63：正文 + 自定义按键约定一律走公开的 `StorageUiTooltip.Compose`
            //   （三套视图 + 图标格原先各写了一遍，收成一处 —— 见 CustomUI.Tooltip.cs）。
            btn.TooltipText = StorageUiTooltip.Compose(
                StorageUiShared.BuildTooltip(asset, g), TooltipHint);

            // 图标：固定尺寸（不随物品占用格数变化）—— widthOverride/heightOverride 的用途
            if (asset != null)
            {
                SleekItemIcon icon = new SleekItemIcon();
                icon.PositionOffset_X = 4f;
                icon.PositionOffset_Y = (RowPitch() - _iconSize) * 0.5f;
                icon.SizeOffset_X = _iconSize;
                icon.SizeOffset_Y = _iconSize;
                icon.Refresh(jar.item.id, jar.item.quality, jar.item.state, asset,
                    (int) _iconSize, (int) _iconSize);
                btn.AddChild(icon);
            }

            // 文字：名字 · 数量 · 耐久
            ISleekLabel label = Glazier.Get().CreateLabel();
            label.PositionOffset_X = _iconSize + 12f;
            label.PositionOffset_Y = 0f;
            label.SizeScale_X = 1f;
            label.SizeOffset_X = -(_iconSize + 20f);
            label.SizeOffset_Y = RowPitch();
            label.FontSize = _labelFontSize;
            label.TextAlignment = TextAnchor.MiddleLeft;
            label.TextColor = new SleekColor(Color.white);
            label.Text = StorageUiShared.BuildRowText(asset, g);
            btn.AddChild(label);

            // ---- 点击：两条路，互斥（见类头）----
            if (pageClick)
            {
                // 内置页号搬运：与网格面板完全同源，避免「换个视图手感就变了」。
                // ⚠ 只标脏、**不在回调里立刻重建**：此刻引擎侧的搬运还没同步回来，
                //   当场重算会拿到旧数据；下一帧调用方的 Refresh 才是对的时机。
                btn.OnClicked += delegate (ISleekElement b)
                {
                    if (StorageUiShared.HandlePrimaryClick(r.X, r.Y)) MarkDirty();
                };
                btn.OnRightClicked += delegate (ISleekElement b)
                {
                    if (StorageUiShared.HandleSecondaryClick(r.X, r.Y)) MarkDirty();
                };
                scroll.AddChild(btn);
                r.Button = btn;
                r.Fingerprint = fp;
                return;
            }

            // ⚠ 回调里读**行对象当前字段**（r.X/r.Y/r.SlotIndex/r.BoundItemId），不闭包捕获 ——
            //   行被复用时那些字段会变，捕获下来的一直指向旧格子（点错物品）。
            if (callbackClick)
            {
                btn.OnClicked += delegate (ISleekElement b) { FireSlotClick(_onSlotClicked, r, "左键"); };
                btn.OnRightClicked += delegate (ISleekElement b) { FireSlotClick(_onSlotRightClicked, r, "右键"); };
            }

            scroll.AddChild(btn);
            r.Button = btn;
            r.Fingerprint = fp;
        }

        /// <summary>
        /// ★ 26.10.3.51：问一次「这一行该铺什么颜色」并交给填充层。
        /// 解析与异常隔离都在共享层（与网格 / 蓝图**同一份**，三处不会各自漂移）。
        /// ⚠ 颜色**不进指纹**：它每轮重算，改规则不必重建行（进指纹 = 换个颜色全量重建 + 图标重载）。
        /// </summary>
        private void ApplyCellFill(RowUi r, StorageUiShared.ItemGroup g)
        {
            if (r == null || r.Fill == null) return;

            SleekColor c = default(SleekColor);
            if (_cellFill != null)
            {
                Item at = (g == null || g.First == null) ? null : g.First.item;
                StorageSlotHit hit = StorageUiShared.MakeSlotHit(at, r.SlotIndex, r.X, r.Y);
                c = StorageUiShared.ResolveCellFill(_cellFill, hit, LogName, ref _fillHandlerWarned);
            }
            r.Fill.Color = c;
        }

        /// <summary>
        /// 把一次点击转成 <see cref="StorageSlotHit"/> 交给插件 —— 解析与交付都在共享层
        /// （与网格面板用的是同一份，两处不会各自漂移）。
        /// </summary>
        private void FireSlotClick(StorageSlotHitHandler handler, RowUi r, string what)
        {
            if (handler == null) return;

            if (_flat != null)
            {
                // 扁平槽位：身份 = 「清单里第几个」（按插件自己 Add 的顺序）。
                // 扁平源只填下标（坐标恒 -1），二维源只填坐标（下标恒 -1）—— 两套互斥。
                List<ItemJar> jars = _flat.Jars;
                Item item = null;
                if (r.SlotIndex >= 0 && r.SlotIndex < jars.Count && jars[r.SlotIndex] != null)
                    item = jars[r.SlotIndex].item;

                StorageUiShared.DeliverSlotHit(handler, LogName, what, item, r.SlotIndex, -1, -1,
                    ref _staleSlotWarned, ref _slotHandlerWarned);
                return;
            }

            Item at = StorageUiShared.ResolveItemAt(_items, r.X, r.Y, r.BoundItemId);
            StorageUiShared.DeliverSlotHit(handler, LogName, what, at, -1, r.X, r.Y,
                ref _staleSlotWarned, ref _slotHandlerWarned);
        }

        private bool _slotHandlerWarned;
        private bool _staleSlotWarned;

        /// <summary>「数据源就是当前打开容器、同时又挂了回调」这条提示是否已经报过。</summary>
        private bool _callbackShadowedWarned;

        /// <summary>★ 26.10.3.51：`CellFill` 回调抛异常是否已经报过（同样在关面板时复位）。</summary>
        private bool _fillHandlerWarned;

        /// <summary>
        /// 复位所有「只报一次」的警告。
        ///
        /// ⚠ 这些 bool 一旦置位就**永不复位**的话，代价是「第二次遇到时完全无声」——
        ///   接入方拿着一份只在首轮出现过的日志根本查不出问题（能力缺口盘点 §2.4）。
        ///   ⟹ `Hide()`（关闭一次面板）与 `Discard()`（真丢弃）都当作「一次会话结束」，
        ///     重新打开时警告重新可用 —— 同一会话内仍只报一次（不刷屏），但不会永久静音。
        /// </summary>
        private void ResetClickWarnings()
        {
            _slotHandlerWarned = false;
            _staleSlotWarned = false;
            _callbackShadowedWarned = false;
            _fillHandlerWarned = false;
            warnedPassive = false;
        }

        /// <summary>
        /// 点击搬运到底能不能生效 —— **只有数据源就是当前打开的那个容器时才行**。
        ///
        /// ⚠ 为什么必须这样挡：搬运走原版按页号的 RPC（`sendDragItem(page,x,y)`），
        ///   而共享层里那套坐标全部按 `PlayerInventory.STORAGE` 页解释。
        ///   给它一个别的 `Items`（别的箱子、机器自己的格子），坐标会**指到当前打开的那个
        ///   容器上去** —— 症状是"点 A 箱的东西，B 箱少了物品"，而且改的是会**自动落盘**的数据。
        ///   宁可只读，也不能猜错对象。
        /// </summary>
        private bool ResolveInteractive(Items source)
        {
            if (!Interactive) return false;

            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null || source == null) return false;

            Items open = player.inventory.items[PlayerInventory.STORAGE];
            bool same = ReferenceEquals(open, source);
            if (!same && !warnedPassive)
            {
                warnedPassive = true;
                CustomUIPlugin.Log(LogName + "：数据源不是当前打开的容器 → 页号搬运不可用，本次只读" +
                    "（要让它可点：挂 OnSlotClicked / OnSlotRightClicked 回调，自己处理动作）");
            }
            return same;
        }

        private bool warnedPassive;

        /// <summary>
        /// 让下一帧必定重建一次 —— 留给「数据源被外部改过、但指纹算出来没变」这种
        /// 组件自己看不出来的情况（与 StorageGridPanel 同名同义）。
        /// </summary>
        public void MarkDirty()
        {
            lastFingerprint = -1;   // 下一帧 Refresh 会重建
        }

        // ================= 滚动位置保留（26.10.3.47） =================
        //   与 <see cref="StorageGridPanel"/> 同一套语义与理由（那里有完整注释）：
        //   面板要拆 / 版面要变时记一次，下一帧布局就绪后写回去，别把玩家弹回第一行。
        //   ⚠ 只认"版面变化"（高度上限 / 显隐 / 拆重建）；**筛选用词变了不该保留** ——
        //     换了个搜索词本来就该从头看（那走的是内容变化，这里不插手）。

        private Vector2 scrollMemo = new Vector2(-1f, -1f);
        private bool scrollRestorePending;
        private int scrollRestoreTries;
        private const int SCROLL_RESTORE_MAX_TRIES = 4;

        /// <summary>记下玩家**现在看到的位置**（面板即将被拆 / 版面即将变化时调）。</summary>
        public void SaveScrollPosition()
        {
            if (scroll == null || panel == null || panel.Parent == null) return;
            try { scrollMemo = new Vector2(0f, scroll.NormalizedStateCenter.y); }
            catch (Exception) { }
        }

        /// <summary>请组件在**下一帧布局就绪后**把记下的位置写回去（幂等；没记过就不做）。</summary>
        public void RestoreScrollPosition()
        {
            if (scrollMemo.x < 0f) return;
            scrollRestorePending = true;
            scrollRestoreTries = 0;
        }

        /// <summary>待恢复的滚动位置：每帧试一次，**至少两帧**读到对得上才收工（理由见 GridPanel）。</summary>
        private void ApplyPendingScroll()
        {
            if (!scrollRestorePending) return;
            if (scroll == null || panel == null || panel.Parent == null) return;

            float want = scrollMemo.y;
            try
            {
                scroll.NormalizedStateCenter = new Vector2(0f, want);
                scrollRestoreTries++;
                float now = scroll.NormalizedStateCenter.y;
                if (scrollRestoreTries >= SCROLL_RESTORE_MAX_TRIES ||
                    (scrollRestoreTries >= 2 && Mathf.Abs(now - want) < 0.01f))
                {
                    scrollRestorePending = false;
                }
            }
            catch (Exception)
            {
                scrollRestorePending = false;
            }
        }
    }
}
