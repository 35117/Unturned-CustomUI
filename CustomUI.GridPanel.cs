// ============================================================================
//  CustomUI · 公开的可复用「物品网格」面板（B1：任意 Items 页 → 一块网格）
// ============================================================================
//  为什么单独一个文件：这套机制原先长在 StorageGridView 里，但**它跟"接管存储区"
//  没有必然关系** —— 关系只在「合并/排序/占用/图标/限高滚动」这套显示规则上。
//  别的插件要做「一台机器两块存储」「输入格 + 输出格」，缺的就是这个：
//  它手里已经有一个 `Items`（自己的虚拟容器、或引擎里另一个箱子），
//  但没法把它画成一块长得跟原版一致、且遵守本项目全部界面铁律的面板。
//
//  ⟹ 本文件是**唯一**一份网格实现：内置大格子视图（CustomUI.GridView.cs）
//     现在只是它的一层薄封装（Provider 生命周期 + 挂到存储位）。
//     复制一份池子逻辑 = 两份认领规则，迟早漂移（README 坑 #20 / #53 同一类）。
//
//  ⚠ 搬过来时**逐条保留**了原实现里那些"看起来多余"的写法，它们各对应一次实测踩坑：
//     · ScaleContentToWidth / HandleScrollWheel（缺了格宽塌成 0、滚轮失效）
//     · 指纹不含 item.state（含了会每帧重建、图标永远显示不出来）
//     · 换物品时整格重建（就地改图标不可靠，且会牵连兄弟元素）
//     · 回调读单元当前坐标（不闭包捕获）
//     · 挂进 Glazier 树之后强制重建一次（挂载前宽度为 0，坏尺寸会被固化）
//     · 格子按「序号」绑定 + epoch 判代（见 CellUi 注释）
//     · 格内文字高度必须 ≥ 24（实测 13 高的小号标签一个字都不渲染）
//     · 关闭只藏不拆（RemoveChild = GameObject.Destroy → 图标全量重载）
// ============================================================================
using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace CustomUI
{
    /// <summary>
    /// 一次点击交给宿主插件的信息（<see cref="StorageGridPanel.OnSlotClicked"/> 用）。
    ///
    /// ⚠ 只给**身份 + 按键状态**，不给坐标语义：扁平槽位没有页号，插件要认的是
    ///   "我交给你的第几个槽位被点了、按的什么键"，而不是 (x,y)。
    /// ⚠ 带上 Ctrl / 拖拽状态，是为了让插件能复刻**内置视图那套按键语义**
    ///   （左键托起、Ctrl+左键丢弃、右键信息面板、Ctrl+右键取回，
    ///   依据 `CustomUI.Shared.cs:63-122`）—— 否则同一个格子上"直接取回"和
    ///   "取回并托起"没法区分，右列就会出现两套手感（工业数字箱区块正是如此）。
    /// </summary>
    public struct StorageSlotHit
    {
        /// <summary>被点那格当前显示的物品（就是插件当初交给组件的那个实例）。</summary>
        public Item Item;

        /// <summary>
        /// 它在**扁平列表**里的下标 —— 按插件自己 Add 的顺序去认。
        /// ⚠ 只在数据源是 <see cref="StorageSlotList"/> 时有效；数据源是二维 <c>Items</c> 时为 -1
        ///   （那时用 <see cref="X"/> / <see cref="Y"/>）。
        /// </summary>
        public int SlotIndex;

        /// <summary>
        /// 二维来源（<c>Items</c>）下的**格子坐标**；扁平来源下恒为 -1。
        ///
        /// ★ 26.10.3.27 新增：类头那张表承诺「别的 <c>Items</c> + 挂回调 → 交给插件自己处理」，
        ///   但此前只给扁平源填了 <see cref="SlotIndex"/>，二维源的点击全部落进
        ///   「槽位已过期」分支（静默不动作）—— 承诺与实现相反（能力缺口盘点 §2.2）。
        ///   现在二维源按坐标认领，插件拿 (X, Y) 去操作自己的容器。
        /// ⚠ 结构体不能带字段初始化器，所以这两个初值由组件在 <c>FireSlotClick</c> 里显式填
        ///   （不适用时填 -1）—— 插件自己 new 一个 hit 时别指望它是 -1。
        /// </summary>
        public int X;

        /// <summary>见 <see cref="X"/>。</summary>
        public int Y;

        /// <summary>物品 id（省得插件再判一次 null）。</summary>
        public ushort ItemId;

        /// <summary>数量。</summary>
        public byte Amount;

        /// <summary>耐久。</summary>
        public byte Quality;

        /// <summary>按下时是否按住 Ctrl（内置语义：左键+Ctrl=丢弃、右键+Ctrl=取回）。</summary>
        public bool Ctrl;

        /// <summary>按下时是否已有物品被托起（内置语义：此时左键=放入、右键=取消托起）。</summary>
        public bool Dragging;
    }

    /// <summary>槽位被点击的回调（左键 / 右键各一个）。</summary>
    public delegate void StorageSlotHitHandler(StorageSlotHit hit);

    /// <summary>
    /// 一块**可复用**的物品网格：一行 N 个，每格「上图标、下名称、再下数量/耐久」，
    /// 格子尺寸固定（不随物品实际占用格数变化），带 Tooltip、内部滚动、占用统计头部。
    ///
    /// 用法（第三方插件，配合 <see cref="IStorageUiRegion"/> 挂进右列）：
    /// <code>
    /// private readonly StorageGridPanel grid = new StorageGridPanel();
    ///
    /// public ISleekElement RegionPanel(StorageUiContext c)
    /// {
    ///     grid.Build();                 // 幂等；常驻复用，别每帧新建
    ///     return grid.Element;          // 交给宿主排版（宿主只藏不拆）
    /// }
    /// public void Tick(StorageUiContext c)
    /// {
    ///     grid.Refresh(myItems);        // 内容变了才重建（内部比指纹）
    /// }
    /// </code>
    ///
    /// ## 三种数据源 × 能不能点
    ///
    /// | 数据源 | 点击行为 |
    /// |---|---|
    /// | **当前打开的那个容器**（<c>items[STORAGE]</c>） | 走内置的原版搬运（<see cref="Interactive"/>），任何环境可用 |
    /// | 别的 <c>Items</c> / 扁平 <see cref="StorageSlotList"/>，**且挂了回调** | 把 <see cref="StorageSlotHit"/> 交给插件自己处理（B2-a） |
    /// | 同上但**没挂回调** | 只读（并记一次日志说明原因） |
    ///
    /// ⚠ 为什么中间那一档必须走回调、而不是组件自己去搬：
    ///   搬运走的是原版**按页号**的 RPC（<c>sendDragItem(page,x,y)</c>），而"页号"只有
    ///   玩家那 9 页 + 打开中的 STORAGE 页。给一个别的 <c>Items</c> 时，坐标会被
    ///   **按当前打开的那个箱子解释** ⟹ "点 A 箱的东西、B 箱少物品"，而且自动落盘（坑 #55）。
    ///   所以组件**不猜**：要么数据源就是那个容器，要么动作交给你（数据的真正主人）。
    /// ⚠ 挂了回调时组件**不再判页号**——它把动作完全交给插件，插件对那份数据有权也有责
    ///   （容量检查、回滚、存盘都在插件那一侧，组件只负责"哪一格被点了"）。
    /// ⚠ 联机客户端**读不到**未打开容器的 <c>Items</c>：
    ///   <c>InteractableStorage.updateState</c> 里 <c>new Items(...)</c> 在
    ///   <c>if (Provider.isServer)</c> 分支内（:349）—— 不是"不能改"，是**没有数据**。
    ///   虚拟容器（插件自己维护的 <c>Items</c> / <c>List&lt;Item&gt;</c>）不受此限制，
    ///   引擎还留了持久化钩子 <c>InteractableStorage.onStateRebuilt</c>。
    /// </summary>
    public class StorageGridPanel : IStorageUiPanel
    {
        // ---- 外观旋钮 ----
        // ★ 26.10.3.27：这些旋钮从 public **字段**改成**属性**，setter 值一变就自动 `MarkDirty()`
        //   ⟹ 契约变成「设了就有反应（下一帧重建）」，不再要求调用方记得额外标脏。
        //
        //   为什么必须改（2026-10-05 能力缺口盘点 §2.1-2.3，逐条亲验）：
        //   `Columns` / `MaxListHeight` 只在重建格子时被读，`Interactive` 只在算点击路由时被读，
        //   而 `RefreshCore` **只在内容指纹变化时**才重建 ⟹ 数据没变、只改旋钮，画面一动不动。
        //   接入方的第一反应是「我设了怎么没反应」，而且无处可查 —— 这就是半截 API。
        //   `EmptyText` 更极端：原先只在空标签**创建那一刻**被读一次，之后改了永不生效
        //   （现已在 RebuildCells 的空箱分支每轮同步）。
        //   ⚠ **值相同不标脏**：工业侧每轮 Tick 都会把同一组旋钮重设一遍（`ApplyMode`），
        //     不去重就会变成「每帧重建」→ 图标全量重载（坑 #30 那一族）。
        //   ⟹ `MarkDirty()` 仍公开，留给「数据源被外部改过、但指纹算出来没变」这种组件看不出来的情况。

        private int _columns = 4;
        private float _maxListHeight = 510f;
        private string _title = "";
        private string _emptyText = "（空箱）";
        private string _tooltipHint = "";
        private bool _interactive = true;

        /// <summary>一行几格。默认 4（与内置大格子视图同款）。**改完下一帧生效**。</summary>
        public int Columns
        {
            get { return _columns; }
            set
            {
                if (_columns != value)
                {
                    _columns = value;
                    // ★ 26.10.3.47：换列数 = 版面要变 ⟹ 把玩家看到的位置带过去
                    //   （数字箱"预览 4 列 → 会话 6 列"就是这条路，不保留就弹回顶部）。
                    SaveScrollPosition();
                    RestoreScrollPosition();
                    MarkDirty();
                }
            }
        }

        /// <summary>网格区最大高度，超出内部滚动。默认 510；挂在右列区块里建议 240 左右。**改完下一帧生效**。</summary>
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

        /// <summary>
        /// 头部前缀（例如「输入」「输出」）。留空 = 只显示统计，与内置视图同款。**改完下一帧生效**。
        /// </summary>
        public string Title
        {
            get { return _title; }
            set { if (!string.Equals(_title, value)) { _title = value; MarkDirty(); } }
        }

        /// <summary>空容器时那行字。**改完下一帧生效**（空箱分支每轮都会重新同步）。</summary>
        public string EmptyText
        {
            get { return _emptyText; }
            set { if (!string.Equals(_emptyText, value)) { _emptyText = value; MarkDirty(); } }
        }

        /// <summary>
        /// 追加到每格 Tooltip 末尾的一行**操作说明**（自定义按键语义的插件应该填）。
        /// **改完下一帧生效**。
        ///
        /// ⚠ 为什么需要它：像数字箱这类**记账型清单**用的是自己定义的按键约定
        ///   （不是原版箱子那套托起/落点）。玩家不知道约定时，每个键看起来都「没反应」
        ///   或「反应奇怪」。把说明挂在鼠标最先停留的地方（Tooltip）比写在标题里合适 ——
        ///   标题一行放不下，而玩家是「对着某一格」才想知道能干什么。
        /// </summary>
        public string TooltipHint
        {
            get { return _tooltipHint; }
            set { if (!string.Equals(_tooltipHint, value)) { _tooltipHint = value; MarkDirty(); } }
        }

        // ================= 外观旋钮（26.10.3.66）=================
        //   ★ 这三个原先都是**写死的常量**（`ICON_SIZE` / `CELL_HEIGHT` / 字号 `Small`）。
        //     默认值 = 原常量值 ⟹ **现有观感一字不变**（加旋钮 ≠ 改默认）。
        //   ⚠ 它们**互相有约束**，所以设大了会自动"把格子撑开"，设小了不会把内容挤坏：
        //     格高 = `max(CellHeight, IconSize + 6 + 名称行 + 数量行 + CELL_GAP)`
        //     （名称行 / 数量行又由 `LabelFontSize` 的**理想高度**决定）⟹ **永远不会溢出**。

        /// <summary>
        /// 图标边长（默认 **48**）。格高会自动跟着长（见 <see cref="CellHeight"/>）。
        /// ⚠ 夹到 ≥ 8（再小图标组件自己也画不出东西）。
        /// </summary>
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
        /// 每一行的**格高**（默认 **108**，含格间距）。它同时是"**下限**"：
        /// 实际格高 = `max(它, 内容所需)` —— 把 <see cref="IconSize"/> 或
        /// <see cref="LabelFontSize"/> 调大时，格子会**自动**撑开，不会让文字/图标溢出。
        /// ⚠ 夹到 ≥ 40（再小连一个图标都放不下）。
        /// </summary>
        public float CellHeight
        {
            get { return _cellHeight; }
            set
            {
                float v = value < 40f ? 40f : value;
                if (_cellHeight == v) return;
                _cellHeight = v;
                MarkDirty();
            }
        }

        /// <summary>
        /// **实际用的格高**（只读）= `max(CellHeight, 内容所需)`。
        /// ★ 有了它，"调大 <see cref="IconSize"/> / <see cref="LabelFontSize"/> 会把格子撑开"
        ///   才是**可断言的事实**（否则只能肉眼猜）。
        /// </summary>
        public float EffectiveCellHeight
        {
            get { return CellPitch(); }
        }

        /// <summary>
        /// 格内文字字号（默认 <see cref="ESleekFontSize.Small"/>）。
        /// ★ 文字行高会按它的**理想高度**自动加高（`SleekFontSize.cs` 的注释值：
        ///   Tiny 8 / Small 20 / Default 30 / Medium 50 / Large 70）—— 所以调大字号
        ///   不会把字挤掉，只会让**格子变高**。
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

        /// <summary>名称行的实际高度（≥ <see cref="NAME_MIN_H"/>，随字号长）。</summary>
        private float NameHeight()
        {
            float need = StorageUiShared.IdealTextHeight(_labelFontSize) + 6f;
            return need > NAME_MIN_H ? need : NAME_MIN_H;
        }

        /// <summary>数量 / 耐久行的实际高度（≥ <see cref="INFO_MIN_H"/>，随字号长）。</summary>
        private float InfoHeight()
        {
            float need = StorageUiShared.IdealTextHeight(_labelFontSize) + 4f;
            return need > INFO_MIN_H ? need : INFO_MIN_H;
        }

        /// <summary>内容所需的**最小格高**（图标 + 间距 + 名称行 + 数量行 + 格间距）。</summary>
        private float RequiredCellHeight()
        {
            return _iconSize + 6f + NameHeight() + InfoHeight() + CELL_GAP;
        }

        /// <summary>
        /// **实际格距** —— `max(CellHeight, 内容所需)`。所有几何都读它，别直接读 `_cellHeight`
        /// （那样把图标调大会溢出；铁律：布局只有一个口径）。
        /// </summary>
        private float CellPitch()
        {
            float need = RequiredCellHeight();
            return (_cellHeight > need) ? _cellHeight : need;
        }

        /// <summary>日志前缀（内置视图传「大格子」）。⚠ 只影响日志文本，改它不必标脏。</summary>
        public string LogName = "网格";

        /// <summary>
        /// 是否允许点击搬运。⚠ 只有当数据源就是**当前打开的容器**时才真的生效，
        /// 否则自动降级为只读（见类头那张表）。**改完下一帧生效**。
        /// </summary>
        public bool Interactive
        {
            get { return _interactive; }
            set { if (_interactive != value) { _interactive = value; MarkDirty(); } }
        }

        private bool _showSearch;

        /// <summary>
        /// 是否在头部下方显示一行搜索框（默认 false = **完全不改变现有观感**）。
        /// ★ 26.10.3.29 新增。匹配面与判定都在共享层（`StorageUiShared.MatchesQuery`）：
        ///   中文名 / 英文名 / 拼音首字母 / 全拼。
        ///
        /// ⚠ 输入框是在 <see cref="Build"/> 里建的 ⟹ **改了它必须重建面板才看得见**，
        ///   所以 setter 直接 <see cref="Discard"/>（下次 `Build()` 会重建）。
        ///   只标脏**不行** —— 那只重建格子，那一行输入框永远不出现（"设了没反应"的老毛病，
        ///   正是 `26.10.3.27` 刚修掉的那一族）。
        /// ⟹ 调用方在 `Build()` **之前**设好它（内置大格子视图就是这么做的）。
        /// </summary>
        public bool ShowSearch
        {
            get { return _showSearch; }
            set
            {
                if (_showSearch == value) return;
                _showSearch = value;
                if (panel != null) Discard();   // 已建好 → 真丢弃，下次 Build 重建（含输入框）
                else MarkDirty();
            }
        }

        /// <summary>
        /// 搜索词（空 = 不筛）。改完下一帧生效。
        ///
        /// ⚠ 过滤发生在 **`CollectGroups` 之前**（过滤的是"哪些条目参与分组"），
        ///   不是"分完组再藏几格" —— 后者会让头部的件数/占用与画面不符
        ///   （"同一个数只在一处打折"那一族，已踩过四次）。
        /// ⚠ **同时要驱动输入框的文本** —— ★ 26.10.3.46 起由 `StorageSearchBox.Query` 的
        ///   setter 当场同步（以前是 `RebuildCells` 里每帧比一次再写回，那份已随消重删掉）。
        /// </summary>
        public string SearchQuery
        {
            get { return searchBox.Query; }
            set
            {
                string v = value ?? "";
                if (string.Equals(searchBox.Query, v)) return;
                searchBox.Query = v;
                MarkDirty();
            }
        }

        private StorageSlotHitHandler _onSlotClicked;
        private StorageSlotHitHandler _onSlotRightClicked;

        /// <summary>
        /// 左键回调（B2-a）：数据源**不是**「当前打开的容器」时，点击就交到这里，
        /// 由插件自己决定动作（存取、拆并、打开子界面…）。
        /// ⚠ 挂上它 = 组件把那份数据的**处置权整个交给你**：容量检查、失败提示、
        ///   存盘时机都在插件那一侧，组件只保证「哪一格被点了」报得准。
        /// ⚠ 回调里**别直接改组件正在遍历的那份槽位列表**；改你的数据源，
        ///   或调 <see cref="MarkDirty"/> 让下一帧重建。
        /// ★ 26.10.3.27：从字段改成属性 —— 挂/摘回调会改变点击走哪条路
        ///   （pageClick 与 callbackClick 互斥），所以同样必须标脏，
        ///   否则会出现「挂了回调但界面还是老样子」。
        /// </summary>
        public StorageSlotHitHandler OnSlotClicked
        {
            get { return _onSlotClicked; }
            set { if (_onSlotClicked != value) { _onSlotClicked = value; MarkDirty(); } }
        }

        /// <summary>右键回调（与左键同一套语义，通常放「次要动作」：整组取出、打开管理…）。</summary>
        public StorageSlotHitHandler OnSlotRightClicked
        {
            get { return _onSlotRightClicked; }
            set { if (_onSlotRightClicked != value) { _onSlotRightClicked = value; MarkDirty(); } }
        }

        private StorageCellFillHandler _cellFill;

        /// <summary>
        /// ★ 26.10.3.51：**每一格铺什么颜色**由你定 —— 稀有度 / 耐久告警 / 选中态 / 命中搜索…
        /// 返回 <c>default(SleekColor)</c>（或任何 alpha 0 的颜色）就是「这一格不铺」。
        ///
        /// <code>
        /// grid.CellFill = delegate (StorageSlotHit hit)
        /// {
        ///     if (hit.Item == null) return default(SleekColor);
        ///     return (hit.Quality &lt; 50) ? new SleekColor(new Color(0.8f, 0.2f, 0.15f, 0.55f))
        ///                                : default(SleekColor);
        /// };
        /// </code>
        ///
        /// <list type="bullet">
        /// <item>它是**每轮刷新的纯函数**（每个可见格一次）：别在里面改数据、别标脏。</item>
        /// <item>收到的是**本格当前显示的那一件**（合并组的代表物品），不看它有没有被点过。</item>
        /// <item>填充层铺在格子的**图标与文字之下**（后加的子元素盖在先加的上面 ⟹ 它必须先建）。</item>
        /// <item>⚠ 它会**盖住悬停高亮边框**（内缩 4 像素后的那一圈）：想要悬停反馈就把颜色调浓一点，
        ///   或者别用填充色、只用 <c>BackgroundColor</c> 的深浅。</item>
        /// <item>⚠ 走的是**真彩色**那条路（`ISleekImage` + 白贴图 + `TintColor`），
        ///   不是 `BackgroundColor` —— 后者在盒子上**改不了色相**（见 `StorageUiFill` 的注释）。</item>
        /// </list>
        /// ⚠ 换回调**不需要**重建：颜色每轮重算（内置视图每帧刷新一次）。
        /// </summary>
        public StorageCellFillHandler CellFill
        {
            get { return _cellFill; }
            set { _cellFill = value; }
        }

        /// <summary>让下一帧必定重建一次（数据被外部改过、但指纹可能没变时用）。</summary>
        public void MarkDirty()
        {
            lastFingerprint = int.MinValue;
        }

        // ================= 滚动位置保留（26.10.3.47） =================
        //
        // 为什么需要：面板换模式 / 换数据源 / 被拆重建时，引擎会把滚动位置**弹回顶部**
        // —— 121 项的箱子点一下就被扔回第一行，玩家得重新滚一遍（用户 2026-10-08 报的：
        // "点一下重置了整页，滑条位置变为最顶部同时页面也弹回了最顶部"）。
        // 所以：**版面即将变化或面板即将被拆时记一次，下一帧布局就绪后写回去**。
        //
        // ⚠ 用 `NormalizedStateCenter` 而不是 `NormalizedVerticalPosition`：
        //   后者在接口里是**只读**的（`SleekScrollBox.cs:112`，只有 getter），
        //   而前者可写、三个 Glazier 后端的实现里都有 setter，且接口注释写着它就是
        //   "用来保留位置的"（地图菜单拿它保缩放）。存与取都用同一个口径，才不会有半屏偏移。

        /// <summary>记下的滚动位置（归一化的**视口中心**）。<c>x &lt; 0</c> = 还没记过。</summary>
        private Vector2 scrollMemo = new Vector2(-1f, -1f);
        private bool scrollRestorePending;
        private int scrollRestoreTries;

        /// <summary>恢复最多尝试的帧数（布局完成前引擎可能忽略这次写入，所以要允许重试）。</summary>
        private const int SCROLL_RESTORE_MAX_TRIES = 4;

        /// <summary>
        /// 记下玩家**现在看到的位置**（面板即将被拆 / 版面即将变化时调）。
        /// ⚠ 面板还没挂进 Glazier 树时读不到可信的值 ⟹ 什么都不记（保留上一次的，
        ///   别用一个"挂载前的 0"把好数据覆盖掉）。
        /// </summary>
        public void SaveScrollPosition()
        {
            if (scroll == null || panel == null || panel.Parent == null) return;
            try { scrollMemo = new Vector2(0f, scroll.NormalizedStateCenter.y); }
            catch (Exception) { }
        }

        /// <summary>
        /// 请组件在**下一帧布局就绪后**把记下的位置写回去（幂等；没记过就什么都不做）。
        /// 用法：改版面 / 换数据源之前 <see cref="SaveScrollPosition"/>，之后调它。
        /// </summary>
        public void RestoreScrollPosition()
        {
            if (scrollMemo.x < 0f) return;
            scrollRestorePending = true;
            scrollRestoreTries = 0;
        }

        /// <summary>
        /// 待恢复的滚动位置：每帧试一次，**至少两帧**读到对得上才收工。
        /// ⚠ 为什么不是"对上了就立刻收工"：引擎的复位可能发生在我们的重建**之后**那一帧，
        ///   只写一次会被它随后覆盖掉。
        /// </summary>
        private void ApplyPendingScroll()
        {
            if (!scrollRestorePending) return;
            // 还没挂上去（或面板已拆）时写了也白写 —— 留着 pending 等下一帧
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
                scrollRestorePending = false;   // 引擎不认这个后端就放弃，别每帧对着抛异常
            }
        }

        /// <summary>挂了任一回调 = 宿主自己接管点击（此时不再要求数据源是打开中的容器）。</summary>
        private bool HasSlotCallbacks
        {
            get { return OnSlotClicked != null || OnSlotRightClicked != null; }
        }

        // ---- 布局常量（改动前请先读文件头那份"踩坑清单"） ----

        private const float CELL_GAP = 4f;
        private const float HEAD_HEIGHT = 34f;
        /// <summary>★ 26.10.3.29：搜索框那一行占的高度（只开 ShowSearch 时计入）。</summary>
        private const float SEARCH_H = 28f;

        // ★ 26.10.3.66：这三个原先是**写死的常量**，现在做成旋钮（默认值 = 原常量值 ⟹ 观感一字不变）。
        private const float DEFAULT_ICON_SIZE = 48f;
        private const float DEFAULT_CELL_HEIGHT = 108f;
        private const ESleekFontSize DEFAULT_LABEL_FONT = ESleekFontSize.Small;

        // ⚠ 文字行高**必须 ≥ 24**：项目内已验证的小号标签（ESleekFontSize.Small）
        //   最小高度是 24。实测写成 13 时文字**一个字都不渲染**，而且不报任何错。
        //   ★ 这两个是**下限**（不是写死值）：实际行高由 `NameHeight()` / `InfoHeight()`
        //     按当前 `LabelFontSize` 的"理想高度"算（`Small` 时算出来正好等于它们）。
        private const float NAME_MIN_H = 26f;    // 名称行的下限
        private const float INFO_MIN_H = 24f;    // 数量 / 耐久行的下限

        private float _iconSize = DEFAULT_ICON_SIZE;
        private float _cellHeight = DEFAULT_CELL_HEIGHT;
        private ESleekFontSize _labelFontSize = DEFAULT_LABEL_FONT;

        /// <summary>
        /// ★ 26.10.3.52：填充层的内缩 —— **必须是 0（铺满整格）**。
        /// 填充层自己就带圆角（`ISleekSprite` + `Sliced` 9 宫格，见 `StorageUiFill`），
        /// 不需要再内缩给谁让位；一旦内缩，颜色外侧就露出一圈原版底色
        /// （旧版这里是 4f，用户 2026-10-10 实测「没盖全」）。
        /// </summary>
        private const float FILL_INSET = 0f;

        private ISleekBox panel;
        private ISleekLabel headLabel;
        private ISleekScrollView scroll;
        private ISleekLabel emptyLabel;
        /// <summary>
        /// ★ 26.10.3.29 引入搜索框；★ 26.10.3.46 改用**公开可复用组件** `StorageSearchBox`
        /// —— 原来那份 `CreateStringField()` 与列表面板、蓝图视图**逐字相同**（README 坑 #68）。
        /// 搜索词的真源就是它的 <see cref="StorageSearchBox.Query"/>，本类不再自己存一份。
        /// </summary>
        private readonly StorageSearchBox searchBox = new StorageSearchBox();

        private int lastFingerprint = -1;
        private bool wasAttached;
        private bool warnedPassive;

        /// <summary>
        /// 「面板世代号」：每次 Build() 自增。
        ///
        /// ⚠ 它是**复用安全性的核心**：格子只有在「与本代面板同一个世代」时才允许复用。
        ///   跨代复用的控件，其父节点早已随旧画布销毁 —— 判断为可复用就会得到一个
        ///   永远不显示的格子（实测：18 件只画出 13 件就是这个症状）。
        ///   不去用 `Button.Parent == scroll` 做这个判断：滚动容器内部如何组织子节点
        ///   属于 Glazier 的实现细节，一旦它加了内层容器，判断就会恒假 → 复用永不生效。
        /// </summary>
        private int epoch;

        /// <summary>
        /// 一个格子 = 「图标 + 名称 + 数量/耐久」的**可复用单元**。
        ///
        /// ⚠ 池子下标 == 格子序号：`cells[slot]` 就是第 slot 格的控件，第 slot 格
        ///   自己记住「当前绑的是哪件物品」（BoundFp）。**不要**改成「按指纹找空闲格」——
        ///   实测那样做会出现两个问题：
        ///     ① 同款物品（指纹相同）除第一件外全都认领失败 → 每轮都整格重建，
        ///        图标被反复重新加载（日志实测：18 件里 13 件每轮重建）；
        ///     ② 认领到的可能是**上一次界面遗留的控件**（其父节点已随旧画布销毁），
        ///        复用它等于用一个永远不显示的格子 —— 表现为「18 件只画出 13 件」。
        /// </summary>
        private class CellUi
        {
            public ISleekButton Button;
            /// <summary>★ 26.10.3.51：这一格的填充层（`CellFill` 上色用）。建格时就建好，一直留着。</summary>
            public StorageUiFill Fill;
            public int BoundFp;             // 本格当前绑定物品的指纹
            public int Epoch;               // 建/改这个控件时面板所处的世代
            public byte X;                  // 命中坐标（回调时读取，不闭包捕获）
            public byte Y;
            public int SlotIndex;           // 扁平槽位下标（回调模式用；页号模式下无意义）
            public ushort BoundItemId;      // 本格当前绑定物品的 id（二维回调前用来核对坐标有没有过期）
        }

        private readonly List<CellUi> cells = new List<CellUi>();

        // 合并 + 排序后的**显示项**（复用容器，避免每帧产生垃圾）。
        // 「哪些物品算同一件」与顺序都由共享层定义 —— 所有网格必须同款。
        private readonly List<StorageUiShared.ItemGroup> groups =
            new List<StorageUiShared.ItemGroup>();

        /// <summary>
        /// ★ 26.10.3.29：**过完搜索**的 jar（复用容器）。
        /// 过滤发生在分组**之前**，所以它是"哪些条目参与分组"的输入 ——
        /// 见 <see cref="StorageUiShared.CollectJars"/>。
        /// </summary>
        private readonly List<ItemJar> visible = new List<ItemJar>();

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
        /// 建面板（幂等）。⚠ 只在没有面板时建 —— 反复建会自增 epoch，
        /// 于是上一代的格子控件全部作废、图标全量重载（就是坑 #30 那个症状）。
        /// </summary>
        public void Build()
        {
            if (panel != null) return;

            epoch++;   // 新一代面板：上一代留下的格子控件全部作废（见 epoch 字段注释）

            panel = Glazier.Get().CreateBox();
            panel.PositionOffset_X = 0f;
            panel.PositionOffset_Y = 0f;
            panel.SizeScale_X = 1f;
            panel.SizeOffset_X = -20f;   // 留出外层滚动条宽度
            panel.SizeOffset_Y = HEAD_HEIGHT;
            panel.BackgroundColor = new SleekColor(ESleekTint.BACKGROUND, 0.9f);

            headLabel = Glazier.Get().CreateLabel();
            headLabel.PositionOffset_X = 8f;
            headLabel.PositionOffset_Y = 4f;
            headLabel.SizeScale_X = 1f;
            headLabel.SizeOffset_X = -16f;
            headLabel.SizeOffset_Y = 26f;
            headLabel.FontSize = ESleekFontSize.Small;
            headLabel.TextAlignment = TextAnchor.MiddleLeft;
            headLabel.TextColor = new SleekColor(ESleekTint.FONT);
            headLabel.Text = string.IsNullOrEmpty(Title) ? "物品" : Title;
            panel.AddChild(headLabel);

            // ★ 26.10.3.29：可选的搜索框 —— 占**一行**，在头部之下、网格之上。
            //   ★ 26.10.3.46：控件本体交给**公开可复用组件** `StorageSearchBox`——
            //   原来这段 `CreateStringField()` 与列表面板、蓝图视图**逐字相同**
            //   （同一件事写到第三遍时就不是巧合了，见 README 坑 #68）。
            //   ⚠ 旋钮**显式设一遍**，不靠组件默认值 —— 免得以后有人改默认值悄悄改掉本视图观感。
            //   ⚠ 高度 28 是本视图**已验收**的观感（另两个视图用 30），所以这里显式设。
            searchBox.Show = ShowSearch;
            searchBox.Height = SEARCH_H;
            searchBox.TopOffset = HEAD_HEIGHT;
            searchBox.Placeholder = "搜索物品名 / 拼音";
            searchBox.OnQueryChanged = OnSearchTextChanged;
            searchBox.Build(panel);

            scroll = Glazier.Get().CreateScrollView();
            scroll.PositionOffset_X = 4f;
            // 搜索框占一行时网格整体下移 —— ⚠ 高度只有 SearchRowHeight() **一个来源**，
            // 别在这行和 RebuildCells 的面板高度里各写一份（两处分头写 = 迟早漏改一处）
            scroll.PositionOffset_Y = HEAD_HEIGHT + SearchRowHeight();
            scroll.SizeScale_X = 1f;
            scroll.SizeOffset_X = -8f;
            scroll.SizeOffset_Y = 0f;        // 每轮按「内容高 vs 上限」重算

            // ⚠⚠ 这两行缺一不可（照抄项目内已验证的工业插件用法）：
            //  · ScaleContentToWidth：内容宽跟随视口。缺了它内容宽为 0，
            //    于是子元素 SizeScale_X 参照到 0 → 格子宽度塌成 0 → 文字不渲染、
            //    鼠标落不到格子上（Tooltip 因此不出现），而图标是固定尺寸仍可见。
            //  · HandleScrollWheel：响应鼠标滚轮，缺了只能拖滚动条。
            scroll.ScaleContentToWidth = true;
            scroll.HandleScrollWheel = true;

            panel.AddChild(scroll);
        }

        /// <summary>
        /// 常驻面板是否还能接着用：必须仍挂在**当前**的挂载点上。
        /// 切关/换服会重建整个 PlayerUI，旧面板随旧画布一起销毁，但 panel.Parent
        /// 还指着那个废掉的实例 —— 与当前挂载点一比就穿帮，于是丢弃重建。
        /// ⚠ 挂载点用 <see cref="StorageUiPanels.AttachSpot"/> 取（公开门面）。
        /// </summary>
        public bool IsUsableAt(ISleekElement spot)
        {
            if (panel == null || scroll == null || spot == null) return false;
            try { return ReferenceEquals(panel.Parent, spot); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 搜索框那一行占的高度（没开 = 0）。**唯一的**高度来源，见 Build 里的调用点。
        /// ★ 26.10.3.46：改由组件算（`DesiredHeight` = Show ? Height : 0）。保留这个函数名
        ///   是为了自检那条"高度只有一个来源"的判据 —— 不是为了兼容。
        /// </summary>
        private float SearchRowHeight()
        {
            return searchBox.DesiredHeight;
        }

        /// <summary>
        /// 用户在搜索框里打字（由 <see cref="StorageSearchBox.OnQueryChanged"/> 回调进来）：
        /// 把新词交给 <see cref="SearchQuery"/>，然后**无条件**标脏，
        /// **不在这里重建** —— 重建由下一帧的 `Refresh` 统一做（与格子点击、旋钮同一条约定）。
        /// ⚠ 为什么不能只靠 setter 标脏：玩家打字时，组件在回调**之前**就已经把
        ///   `Query` 设成新词了 ⟹ 这里再赋同一个值会撞上 setter 的"值相同就 return"
        ///   而**早退**，于是不标脏、打了字不筛（26.10.3.46 消重时踩到）。
        /// </summary>
        private void OnSearchTextChanged(string text)
        {
            if (!string.Equals(searchBox.Query, text)) searchBox.Query = text;
            MarkDirty();
        }

        /// <summary>只藏不拆（关闭时用它；重开时控件池与图标贴图都还在）。</summary>
        public void Hide()
        {
            // ⚠ 绝不调 RemoveChild —— Glazier 的 RemoveChild 末尾是 GameObject.Destroy，
            //   格子与已渲染的图标贴图会一起没掉，而我们的图标不满足原版 getIcon 的
            //   缓存判据（统一尺寸 + 带 state）→ 进不了 iconCache，只能重新排队渲染。
            try { if (panel != null) panel.IsVisible = false; } catch (Exception) { }
            groups.Clear();
            lastFingerprint = -1;
            // ★ 26.10.3.27：关闭一次面板 = 一次会话结束 ⟹ 只报一次的警告该重新可用
            //   （否则"第二次遇到时完全无声"，能力缺口盘点 §2.4）。
            ResetClickWarnings();
            // ⚠ 数据源引用也要放手：留着一个已销毁容器的 `Items`，
            //   下次打开若忘了换源就会拿旧数据刷新（而且它可能已经不属于这个世界了）。
            _items = null;
            _flat = null;
            // ★ 26.10.3.29：搜索词也清掉 —— 它是"**这一次会话**的状态"。
            //   留着会让玩家打开**下一个**箱子时发现"怎么只剩几件"（其实是上一次搜的词）。
            SearchQuery = "";
        }

        /// <summary>真丢弃常驻（旧画布已销毁、或换数据源重建时）。下次 Build 会自增 epoch。</summary>
        public void Discard()
        {
            // ★ 26.10.3.47：拆之前先记下玩家看到的位置 —— 下次 Build 出来的新滚动容器
            //   会从顶部开始（引擎行为），不记就等着被"弹回第一行"。
            SaveScrollPosition();

            try { if (panel != null && panel.Parent != null) panel.Parent.RemoveChild(panel); }
            catch (Exception) { }

            cells.Clear();
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

        /// <summary>作废所有格子的绑定（下一次 Refresh 会整批重建）。</summary>
        public void InvalidateAll()
        {
            for (int i = 0; i < cells.Count; i++)
            {
                cells[i].BoundFp = int.MinValue;
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
            RefreshCore(Fingerprint(source));
        }

        /// <summary>
        /// 用一串**扁平槽位**刷一次（没有二维摆放语义的数据源，例如工业插件的
        /// `List&lt;Item&gt;` + 总格数容量）。合并、排序、图标、Tooltip 与 `Items` 路径**同一份实现**，
        /// 差别只有两处：头部不报「最大连续空位」（那需要二维占用表，报了就是假信息），
        /// 以及**永远只读**（扁平槽位没有页号，见 <see cref="ResolveInteractive"/>）。
        /// </summary>
        public void Refresh(StorageSlotList source)
        {
            _flat = source;
            _items = null;
            RefreshCore(source == null ? -1 : Fingerprint(source.Jars));
        }

        private Items _items;
        private StorageSlotList _flat;

        private void RefreshCore(int fp)
        {
            if (panel == null) return;

            if (fp != lastFingerprint)
            {
                lastFingerprint = fp;
                RebuildCells();
                if (panel == null) return;   // 重建里出过异常就不再继续
            }

            // ⚠ 首次挂进 Glazier 树之后必须**重建一次**：
            //   挂载前面板拿不到真实宽度（内容宽为 0），那一刻建出来的格子宽度为 0
            //   → 格内文字（相对宽）落成负值 → 不渲染，而且不会自己恢复。
            if (panel.Parent != null && !wasAttached)
            {
                wasAttached = true;
                InvalidateAll();   // 挂载前建的格子整体作废
                RebuildCells();
            }

            // ★ 26.10.3.47：版面变了 / 面板刚重建过 ⟹ 把玩家原来的滚动位置写回去。
            //   放最后：必须等这一轮的重建把内容高算完（内容高变了，归一化位置才有意义）。
            ApplyPendingScroll();
        }

        /// <summary>
        /// 让**所有**格子都判定为「需要重建」并立刻重建一轮（供外部在换摆位/改列数后调）。
        /// </summary>
        public void RebuildNow()
        {
            InvalidateAll();
            RebuildCells();
        }

        /// <summary>当前数据源是不是扁平槽位（头部统计与交互都按它分流）。</summary>
        private bool IsFlat
        {
            get { return _flat != null; }
        }

        // ================= 格子刷新（按序号绑定，只有换了物品才整格重建） =================

        private void RebuildCells()
        {
            if (scroll == null || headLabel == null) return;

            int columns = Columns < 1 ? 1 : Columns;
            float cellScaleX = 1f / columns;

            // ★ 26.10.3.29：**先过搜索、再分组** —— 过滤的是"哪些条目参与分组"。
            //   ⚠ 不是"分完组再藏几格"：那样头部的件数与画面会对不上
            //     （"同一个数只在一处打折"那一族，已踩四次）。
            visible.Clear();
            int allJars = 0;
            if (IsFlat)
            {
                // 扁平入口（数字箱那类记账清单）**不做取证** —— 它的 amount 是记账件数，
                // 取证会误报（见 AuditStackAmount 的注释）⟹ 它自己遍历，不走 CollectJars。
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
            else
            {
                // 二维源走共享层的**唯一收集点**（取证与过滤都在那里，别自己遍历）
                int matchedJars = 0;
                StorageUiShared.CollectJars(_items, visible, searchBox.Query, out allJars, out matchedJars);
                StorageUiShared.CollectGroups(visible, groups);
            }
            int count = groups.Count;

            int totalItems = 0;
            for (int i = 0; i < count; i++) totalItems += groups[i].Count;

            // 头部统一走共享层：占用格 + 件数/项数。
            // 「最大空位 W×H」直接回应「明明有空格却放不进 2×2」——那是引擎 2D 摆放模型的
            // 碎片化（tryFindSpace 要求连续矩形，服务端权威检查），合并列表把空洞藏起来了。
            // ⚠ 但它只对**有二维摆放语义**的容器成立；扁平槽位（工业数字箱那类）不做
            //   碰撞检查，给它报最大空位就是假信息 → 用不带该字段的另一版头部。
            string stats = IsFlat
                ? StorageUiShared.BuildHeaderTextFlat(
                    // 调用方给了真源就用它（数字箱的占用判定在工业那边），
                    // 否则才自己估 —— 自己估只会算 Σ size_x×size_y，会**低报**多堆叠的物品。
                    (_flat.OccupiedCells >= 0) ? _flat.OccupiedCells
                        : StorageUiShared.OccupiedCellsOf(_flat.Jars),
                    _flat.CapacityCells,
                    totalItems, count)
                : StorageUiShared.BuildHeaderText(
                    StorageUiShared.ComputeSpace(_items), totalItems, count);
            // ★ 26.10.3.29：筛选中要**显式**说明 —— 否则"件数变少了"看起来像物品丢了
            //   （玩家核对不上就会来问）。两个数都出自上面那一次遍历，不额外算。
            if (ShowSearch && !string.IsNullOrEmpty(searchBox.Query))
            {
                stats = stats + "（筛选中：显示 " + totalItems + " / 共 " + allJars + " 件）";
            }
            headLabel.Text = string.IsNullOrEmpty(Title) ? stats : (Title + " · " + stats);

            // ★ 26.10.3.46：这里不再需要"每帧把 SearchQuery 同步到输入框"了 ——
            //   组件在 Query 的 setter 里当场同步（顺带少一帧延迟，见 StorageSearchBox）。

            // 点击走哪条路（详见类头那张表）：
            //   · 数据源就是当前打开的容器 → 内置页号搬运（任何环境可用）
            //   · 否则挂了回调 → 交给插件（B2-a）
            //   · 都不是 → 只读
            bool pageClick = !IsFlat && ResolveInteractive(_items);
            bool callbackClick = !pageClick && HasSlotCallbacks;

            // ⚠ 数据源就是当前打开的容器、同时又挂了回调 ⟹ 内置页号搬运优先，回调**永不触发**。
            //   这原先是静默的：接入方挂了回调却什么都没发生，而且查不出原因
            //   （与 §2.2 同族的「承诺与实现相反」）。给一次明确日志，并告诉他两条出路。
            //   一次性警告在 Hide/Discard 里复位，见 ResetClickWarnings。
            if (pageClick && HasSlotCallbacks && !_callbackShadowedWarned)
            {
                _callbackShadowedWarned = true;
                CustomUIPlugin.Warn(LogName + "：数据源就是当前打开的容器 ⟹ 点击走内置搬运，" +
                    "你挂的 OnSlotClicked / OnSlotRightClicked 不会触发。" +
                    "要让回调生效：把 Interactive 设成 false（自己处理动作），或换成别的数据源。");
            }

            int rebuilt = 0;

            // `slot` 是「第几个格子」，与物品在列表中的位置一一对应 ——
            // 用它排版就一定不会出现空洞（这与列表视图的递增 y 是同一个道理）。
            for (int slot = 0; slot < count; slot++)
            {
                StorageUiShared.ItemGroup g = groups[slot];
                int fp = CellFingerprint(g);

                while (cells.Count <= slot)
                {
                    cells.Add(new CellUi());
                }
                CellUi cell = cells[slot];

                // 复用条件三条缺一不可：
                //   ① 已经有控件；
                //   ② 它与**本代面板**同世代 —— 否则它是上一次界面遗留的控件，
                //      父节点已销毁，复用它就是一个永远不显示的格子；
                //   ③ 绑的还是同一件物品（指纹相同）。
                bool canReuse = cell.Button != null
                    && cell.Epoch == epoch
                    && cell.BoundFp == fp;

                if (!canReuse)
                {
                    RebuildCell(cell, slot, g, fp, columns, cellScaleX, pageClick, callbackClick);
                    rebuilt++;
                }
                else
                {
                    // ⚠⚠ 复用的格子也**必须每轮重算几何**。
                    //   复用条件是"物品没变"，可**物品没变 ≠ 版面没变**：
                    //   `Columns` 会在两种形态之间改（数字箱：右列预览 4 列、借道界面 6 列），
                    //   不重算就会在同一行里混着 1/4 与 1/6 两种宽度，位置也跟着错位 ——
                    //   实测症状是"交互过的那格变窄了、中间出现空洞"
                    //   （交互过 = 指纹变了 = 被重建 = 拿到新宽度；没交互的留着旧宽度）。
                    //   三个 float 赋值，比重建一个格子（含异步图标）便宜得多。
                    ApplyCellGeometry(cell, slot, columns, cellScaleX);
                }

                // 命中坐标每轮都更新（很便宜，不碰图标）。
                // 用**代表物品**的坐标 —— 取回/托起/丢弃都作用在这一件上。
                cell.X = g.First.x;
                cell.Y = g.First.y;

                // 回调模式要的是「扁平列表里的第几个槽位」。
                // ⚠ 不能直接用 slot：groups 是**合并 + 排序后**的显示项，
                //   第 slot 格对应的未必是原始列表第 slot 个。这里按代表 jar 反查下标。
                cell.SlotIndex = (callbackClick && IsFlat) ? _flat.Jars.IndexOf(g.First) : -1;

                cell.Button.IsVisible = true;

                // ★ 26.10.3.51：填充色**每轮重算**（很便宜：颜色没变时组件内部一个 int 比较就早退）
                ApplyCellFill(cell, g);
            }

            // 多余的格子在池子里留作备用，隐藏但**不销毁**
            for (int i = count; i < cells.Count; i++)
            {
                if (cells[i].Button != null)
                {
                    cells[i].Button.IsVisible = false;
                }
            }

            int rows = (count + columns - 1) / columns;
            float contentH = rows * CellPitch();

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
                    scroll.AddChild(emptyLabel);
                }
                // ★ 26.10.3.27：每轮同步 —— 原先只在「创建那一刻」读一次 EmptyText，
                //   之后改了永不生效（2026-10-05 能力缺口盘点 §2.1）。空箱分支本来就很便宜。
                emptyLabel.Text = EmptyText;
                emptyLabel.PositionOffset_Y = 4f;
                emptyLabel.IsVisible = true;
                contentH = Mathf.Max(contentH, 28f);
            }
            else if (emptyLabel != null)
            {
                emptyLabel.IsVisible = false;
            }

            scroll.ContentSizeOffset = new Vector2(0f, contentH);
            float listHeight = Mathf.Min(contentH, MaxListHeight);
            scroll.SizeOffset_Y = listHeight;
            panel.SizeOffset_Y = HEAD_HEIGHT + SearchRowHeight() + listHeight + 6f;

            // 诊断用：一行汇总。
            //   · 「重建」长期等于「总项数」→ 复用没生效（图标全量重载）。
            //   · 「滚动容器控件数」应当 == 项数（或项数+1：空箱提示）。小于项数就说明
            //     有格子的控件没挂进容器里 —— 那正是「N 件只显示 M 件」的确诊特征。
            CustomUIPlugin.Log(LogName + "刷新：复用 " + (count - rebuilt) + " 格 / 重建 " +
                rebuilt + " 格（共 " + count + " 项，滚动容器控件 " + scroll.GetChildCount() + " 个）");
        }

        /// <summary>
        /// 点击搬运到底能不能生效 —— **只有数据源就是当前打开的那个容器时才行**。
        ///
        /// ⚠ 为什么必须这样挡：搬运走原版按页号的 RPC（<c>sendDragItem(page,x,y)</c>），
        ///   而共享层里那套坐标全部按 <c>PlayerInventory.STORAGE</c> 页解释。
        ///   给它一个别的 <c>Items</c>（机器自己的格子、别人的箱子），
        ///   坐标会**指到当前打开的那个容器上去** —— 症状是"点 A 箱的东西，B 箱少了物品"，
        ///   而且改的是会**自动落盘**的数据。宁可只读，也不能猜错对象。
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

        /// <summary>
        /// 格子指纹：决定「这一格要不要重建」。
        /// 除了物品身份，还必须含**合并件数与总数** —— 否则从 9 件里拿走 1 件时，
        /// 要显示的 ×8 不会刷新（格子会被判定为「没变」而原样复用）。
        /// ⚠ 绝不能加 `item.state` —— 工业机械物品的 state 每帧都在变，
        ///   一旦入指纹就会每帧重建全部格子，图标（异步加载）永远显示不出来。
        /// </summary>
        private static int CellFingerprint(StorageUiShared.ItemGroup g)
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

        /// <summary>
        /// ★ 26.10.3.51：问一次「这一格该铺什么颜色」并交给填充层。
        /// 解析（造命中信息）与异常隔离都在共享层 —— 列表 / 蓝图用的是**同一份**，三处不会各自漂移。
        /// ⚠ 颜色**不进指纹**：它每轮重算，所以改规则不必重建格子
        ///   （进指纹就会变成「换个颜色全量重建 + 图标重载」）。
        /// </summary>
        private void ApplyCellFill(CellUi cell, StorageUiShared.ItemGroup g)
        {
            if (cell == null || cell.Fill == null) return;

            SleekColor c = default(SleekColor);
            if (_cellFill != null)
            {
                Item at = (g == null || g.First == null) ? null : g.First.item;
                StorageSlotHit hit = StorageUiShared.MakeSlotHit(at, cell.SlotIndex, cell.X, cell.Y);
                c = StorageUiShared.ResolveCellFill(_cellFill, hit, LogName, ref _fillHandlerWarned);
            }
            cell.Fill.Color = c;
        }

        /// <summary>把第 <paramref name="slot"/> 格在当前版面下应有的位置与宽度写进已存在的格子。
        /// 复用的格子每轮都要调 —— 见调用点的注释（物品没变不代表版面没变）。
        /// ★ 26.10.3.66：不再是 `static` —— 纵向位置要读 `CellPitch()`（它取决于外观旋钮）。
        /// </summary>
        private void ApplyCellGeometry(CellUi cell, int slot, int columns, float cellScaleX)
        {
            if (cell == null || cell.Button == null) return;
            ApplyCellGeometryTo(cell.Button, slot, columns, cellScaleX);
        }

        /// <summary>
        /// ⚠ 位置与宽度**必须用同一个钳过的列数**：原先位置用字段 <c>Columns</c>、
        /// 宽度用钳过的 <c>cellScaleX</c>，两者在 <c>Columns &lt; 1</c> 时不一致，
        /// 而且 <c>slot % Columns</c> 会当场除零。
        /// ★ 26.10.3.66：格距走 `CellPitch()`（= `max(CellHeight, 内容所需)`），不再读常量。
        /// </summary>
        private void ApplyCellGeometryTo(ISleekElement el, int slot, int columns, float cellScaleX)
        {
            if (el == null || columns < 1) return;
            el.PositionScale_X = (slot % columns) * cellScaleX;
            el.PositionOffset_Y = (slot / columns) * CellPitch();
            el.SizeScale_X = cellScaleX;
        }

        /// <summary>
        /// **整格重建**（Button + 图标 + 两行文字），位置/宽度在 <c>ApplyCellGeometryTo</c> 里算。
        /// ⚠ 为什么是「重建」而不是「就地改图标」：
        ///   SleekItemIcon 的贴图要等 ItemTool.getIcon 的异步回调才换上，
        ///   同一元素二次 Refresh 不可靠；而「移除图标子元素再新建」会牵连兄弟元素。
        ///   整格换新元素结构上不可能出错；代价只在「格子真的换了物品」时才付。
        /// ⚠ 但**位置不是"绑好就不再变"** —— 列数会因形态切换而改，所以复用的格子
        ///   每轮都要重算几何（见调用点的 ApplyCellGeometry）。
        /// </summary>
        private void RebuildCell(CellUi cell, int slot, StorageUiShared.ItemGroup g, int fp,
            int columns, float cellScaleX, bool pageClick, bool callbackClick)
        {
            // 图标与名字取**代表物品**（同组物品完全相同，取哪一件都一样）
            ItemJar jar = g.First;
            ItemAsset asset = jar.GetAsset();

            if (cell.Button != null)
            {
                try { scroll.RemoveChild(cell.Button); } catch (Exception) { }
                cell.Button = null;
            }

            ISleekButton btn = Glazier.Get().CreateButton();
            ApplyCellGeometryTo(btn, slot, columns, cellScaleX);
            btn.PositionScale_Y = 0f;
            btn.PositionOffset_X = 0f;
            btn.SizeOffset_X = -CELL_GAP;                 // 右侧留格间距
            btn.SizeOffset_Y = CellPitch() - CELL_GAP;
            btn.Text = "";   // 文字用独立 label，避免被图标遮挡
            btn.BackgroundColor = new SleekColor(ESleekTint.BACKGROUND, 0.55f);
            // ★ 26.10.3.63：正文 + 自定义按键约定一律走公开的 `StorageUiTooltip.Compose`
            //   （三套视图 + 图标格原先各写了一遍"+"\n"+hint"，收成一处 —— 见 CustomUI.Tooltip.cs）。
            btn.TooltipText = StorageUiTooltip.Compose(
                StorageUiShared.BuildTooltip(asset, g), TooltipHint);

            // ⚠ 先把按钮挂进树，再建子元素 —— 子元素的大小是相对父节点算的，
            //   父节点不在树里时拿不到真实宽度，会把坏尺寸固化下来。
            scroll.AddChild(btn);
            cell.Button = btn;
            cell.BoundFp = fp;
            // ★ 26.10.3.51 填充层：**必须是本格的第一个子元素** ——
            //   同一父容器里后加的元素盖在先加的上面，晚建一层就会盖住图标与文字。
            //   颜色由 `CellFill` 每轮解析（见 RebuildCells 的 ApplyCellFill），这里只建壳。
            cell.Fill = new StorageUiFill();
            cell.Fill.Inset = FILL_INSET;
            cell.Fill.Build(btn);
            // ★ 26.10.3.27：二维源的点击要按坐标反查物品，而坐标可能已经过期
            //   （插件在重建之后改过容器）⟹ 反查出来后拿 id 对一次，见 FireSlotClick。
            cell.BoundItemId = jar.item.id;
            cell.Epoch = epoch;

            // ---- 图标：水平居中、固定尺寸（不随物品占用格数变化） ----
            if (asset != null)
            {
                SleekItemIcon icon = new SleekItemIcon();
                icon.PositionScale_X = 0.5f;               // 先落到父宽中点…
                icon.PositionOffset_X = -_iconSize * 0.5f; // …再左移半个图标 → 居中
                icon.PositionOffset_Y = 4f;
                icon.SizeOffset_X = _iconSize;
                icon.SizeOffset_Y = _iconSize;
                icon.Refresh(jar.item.id, jar.item.quality, jar.item.state, asset,
                    (int) _iconSize, (int) _iconSize);
                btn.AddChild(icon);
            }

            // ---- 名称（高度 ≥ 24 才会渲染，实测 13 高完全不显示） ----
            ISleekLabel name = Glazier.Get().CreateLabel();
            name.PositionOffset_X = 2f;
            name.PositionOffset_Y = _iconSize + 6f;
            name.SizeScale_X = 1f;
            name.SizeOffset_X = -4f;
            name.SizeOffset_Y = NameHeight();
            name.FontSize = _labelFontSize;
            name.TextAlignment = TextAnchor.MiddleCenter;
            name.TextColor = new SleekColor(Color.white);
            name.Text = StorageUiShared.BuildName(asset, jar);
            btn.AddChild(name);

            // ---- 数量 / 耐久（数量 = 合并后的总数；耐久只在物品类型本身有耐久时才显示） ----
            string info = StorageUiShared.BuildAmountText(asset, g);
            if (info.Length > 0)
            {
                ISleekLabel infoLabel = Glazier.Get().CreateLabel();
                infoLabel.PositionOffset_X = 2f;
                infoLabel.PositionOffset_Y = _iconSize + 6f + NameHeight();
                infoLabel.SizeScale_X = 1f;
                infoLabel.SizeOffset_X = -4f;
                infoLabel.SizeOffset_Y = InfoHeight();
                infoLabel.FontSize = _labelFontSize;
                infoLabel.TextAlignment = TextAnchor.MiddleCenter;
                infoLabel.TextColor = new SleekColor(ESleekTint.FONT, 0.8f);
                infoLabel.Text = info;
                btn.AddChild(infoLabel);
            }

            // ---- 点击：两条路，互斥（见类头那张表）----
            if (pageClick)
            {
                // 内置页号搬运：与列表视图完全同源，避免「换个视图手感就变了」。
                // ⚠ 只标脏、**不在回调里立刻重建**：此刻引擎侧的搬运还没同步回来，
                //   当场重算会拿到旧数据；下一帧调用方的 Refresh 才是对的时机。
                btn.OnClicked += delegate (ISleekElement b)
                {
                    if (StorageUiShared.HandlePrimaryClick(cell.X, cell.Y)) MarkDirty();
                };
                btn.OnRightClicked += delegate (ISleekElement b)
                {
                    if (StorageUiShared.HandleSecondaryClick(cell.X, cell.Y)) MarkDirty();
                };
                return;
            }

            if (!callbackClick) return;

            // 回调模式（B2-a）：动作整个交给插件 —— 组件只报"哪一格被点了"。
            // ⚠ 现读 cell（不闭包捕获 g / slot）：行池会复用，捕获的那一份早就过期了。
            // ⚠ 插件抛异常不能拖垮界面：吞掉 + 只报一次（与区块判据同一取向）。
            btn.OnClicked += delegate (ISleekElement b) { FireSlotClick(OnSlotClicked, cell, "左键"); };
            btn.OnRightClicked += delegate (ISleekElement b) { FireSlotClick(OnSlotRightClicked, cell, "右键"); };
        }

        /// <summary>把一次点击转成 <see cref="StorageSlotHit"/> 交给插件。</summary>
        private void FireSlotClick(StorageSlotHitHandler handler, CellUi cell, string what)
        {
            if (handler == null) return;

            // ★ 26.10.3.32：解析（认领哪一件）与交付（填 StorageSlotHit + 调回调 + 隔离异常）
            //   都抽到了共享层（`StorageUiShared.ResolveItemAt` / `DeliverSlotHit`）——
            //   列表面板用的是**同一份**语义，两处不会各自漂移。
            //   ⚠ 用 getIndex（自带边界检查、越界返回 255），不用 getItem(x,y)：
            //     这个 SDK 版本上 Items 没有二维版 getItem。
            //   ⚠ 坐标必须**核对身份**：它是上一轮重建时从代表物品抄下来的，
            //     若插件在重建之后改过这份容器，同一个坐标可能已经换了物品 ——
            //     拿它让插件改数据就是「点 A 改 B」（与坑 #55 同一取向：宁可不做事）。
            if (IsFlat)
            {
                // 扁平槽位：身份 = 「清单里第几个」（按插件自己 Add 的顺序）。
                List<ItemJar> jars = _flat.Jars;
                Item item = null;
                if (cell.SlotIndex >= 0 && cell.SlotIndex < jars.Count && jars[cell.SlotIndex] != null)
                    item = jars[cell.SlotIndex].item;

                // 扁平源只填下标（坐标恒 -1），二维源只填坐标（下标恒 -1）—— 两套互斥
                StorageUiShared.DeliverSlotHit(handler, LogName, what, item, cell.SlotIndex, -1, -1,
                    ref _staleSlotWarned, ref _slotHandlerWarned);
                return;
            }

            Item at = StorageUiShared.ResolveItemAt(_items, cell.X, cell.Y, cell.BoundItemId);
            StorageUiShared.DeliverSlotHit(handler, LogName, what, at, -1, cell.X, cell.Y,
                ref _staleSlotWarned, ref _slotHandlerWarned);
        }

        private bool _slotHandlerWarned;
        private bool _staleSlotWarned;

        /// <summary>★ 26.10.3.51：`CellFill` 回调抛异常是否已经报过（同样在关面板时复位）。</summary>
        private bool _fillHandlerWarned;

        /// <summary>
        /// 「数据源就是当前打开容器、同时又挂了回调」这条提示是否已经报过 —— 见 RebuildCells。
        /// </summary>
        private bool _callbackShadowedWarned;

        /// <summary>
        /// 复位所有「只报一次」的警告。
        ///
        /// ★ 26.10.3.27：原先这些 bool 一旦置位就**永不复位** —— 目的是不刷屏，
        ///   代价却是「第二次遇到时完全无声」：接入方拿着一份只在首轮出现过的日志根本查不出问题
        ///   （能力缺口盘点 §2.4）。
        ///   ⟹ 现在 Hide()（关闭一次面板）与 Discard()（真丢弃）都当作「一次会话结束」，
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

        // ⚠ 原先这里有个 WarnStaleSlot —— 26.10.3.32 搬进了
        //   `StorageUiShared.DeliverSlotHit`（列表面板现在也走那一份，两份实现会漂移）。

        // ================= 指纹 =================

        /// <summary>
        /// 二维容器指纹 —— ★ 26.10.3.32 起真正的实现在共享层（`StorageUiShared.FingerprintItems`），
        /// 列表面板用的是**同一份**（两份指纹公式漂移 = 两个视图对"数据变了没"判断不一致）。
        /// </summary>
        private static int Fingerprint(Items container)
        {
            return StorageUiShared.FingerprintItems(container);
        }

        /// <summary>
        /// 扁平槽位指纹 —— 同上，实现在 `StorageUiShared.FingerprintJars`。
        /// </summary>
        private static int Fingerprint(List<ItemJar> jars)
        {
            return StorageUiShared.FingerprintJars(jars);
        }
    }

    /// <summary>
    /// 一串**扁平槽位**：给没有二维摆放语义的数据源用。
    ///
    /// 为什么需要它（E'-1 的来处）：工业插件的数字箱/机器槽位真源是
    /// `List&lt;Item&gt;` + 总格数容量（见 `Industrial.cs` 的 `DiskData.contents`），
    /// 而原版 `Items` 是**二维摆放**模型 —— 两者模型不同，不该为了套 UI 去扭曲数据层
    /// （项目记忆里就有这条硬规则）。⟹ 反过来让 UI 适配数据：
    /// 把 `Item` 包成引擎的 `ItemJar`（它的构造函数是 public）就能复用同一份
    /// 合并 / 排序 / 图标 / Tooltip 规则。
    ///
    /// 用法（第三方每帧重建，容器自己复用）：
    /// <code>
    /// slots.Clear();
    /// slots.CapacityCells = 40;                 // 总格数（头部统计用）
    /// for (int i = 0; i &lt; myContents.Count; i++) slots.Add(myContents[i]);
    /// grid.Refresh(slots);                      // 扁平来源 → 自动只读
    /// </code>
    /// </summary>
    public class StorageSlotList
    {
        private readonly List<ItemJar> jars = new List<ItemJar>();

        /// <summary>总格数容量（扁平模型的容量语义）。≤ 0 时头部只显示占用不显示分母。</summary>
        public int CapacityCells;

        /// <summary>
        /// 是否**只按物品 ID 合并**（默认 false = 按 id + 耐久，与内置视图一致）。
        ///
        /// 记账型清单（工业数字箱）里"同一件物品"本来就散成多条（每条一个耐久），
        /// 按 id+耐久分会让同一把材料占好几格。数字箱设成 true。
        /// ⚠ 只对扁平入口生效，不会影响原版箱子的显示；
        ///   且**不可分物品（弹匣、枪械）默认仍是一格一条**（见 <c>IsIndivisible</c>），
        ///   开了这个也不会把两匣弹匣并成一条 —— 弹匣/弹药箱要并格得另开下面的 <c>MergeContentPages</c>。
        /// </summary>
        public bool MergeByIdOnly;

        /// <summary>
        /// **内容量物品（弹匣与弹药箱）**是否并成一格（一格代表 N 件，数字第二行报 <c>N匣</c> / <c>N箱</c>）。
        /// 默认 false = 每件一格、报 <c>N发</c>。
        ///
        /// 为什么单独一个开关、不并进 <c>MergeByIdOnly</c>：并格之后**一格不再唯一对应一件**，
        /// "点这一格取到哪一件"必须有人定规矩并告诉玩家 —— 那是**数据层的语义**，
        /// 只有持有清单的一方（工业数字箱）知道，展示层不该替他决定。
        /// ⟹ 开这个开关的调用方**必须**同时做两件事：
        ///   ① 自己按确定规则挑那一件（工业侧 <c>ChooseSource</c> 用 `DigitalConsumeOrder`）；
        ///   ② 把该规则写进 <c>StorageGridPanel.TooltipHint</c>（自定义语义不告诉玩家 = 让人猜）。
        /// ⚠ 枪械 / 近战**永远不并**：它们没有"发数"这种可读的量，并格只会产生歧义。
        /// ⚠ 本字段原名 <c>MergeMagazines</c>，2026-10-05 随弹药箱并入而改名为 <c>MergeContentPages</c>
        ///   —— 名字只盖住弹匣会让人以为箱子不受影响。
        /// </summary>
        public bool MergeContentPages;

        /// <summary>
        /// 已占用格数 —— <b>由调用方给真源时用它</b>，默认 -1 表示"让面板自己估"。
        ///
        /// ⚠ 为什么必须能覆盖：面板自己只会算 <c>Σ size_x×size_y</c>（一条 255 个的 1×1
        /// 物品算 1 格），而工业数字箱的**存取判定**用的是
        /// <c>DigitalItemSize = size_x×size_y×ceil(数量/堆叠上限)</c> —— 两个公式。
        /// 结果就是头部**低报**占用：玩家看到 764/1024 却存不进 2×2 的东西，
        /// 因为引擎算的真实占用早就满了。⟹ **显示的数字必须和做判断用的是同一个数**。
        /// </summary>
        public int OccupiedCells = -1;

        /// <summary>当前槽位数。</summary>
        public int Count
        {
            get { return jars.Count; }
        }

        /// <summary>共享层要读的列表（不对外暴露可写引用）。</summary>
        internal List<ItemJar> Jars
        {
            get { return jars; }
        }

        /// <summary>清空（每帧重建前先调）。⚠ 只清列表，不动我们缓存的 jar 包装之外的东西。</summary>
        public void Clear()
        {
            jars.Clear();
        }

        /// <summary>放一件物品进槽位（坐标按 0,0 —— 扁平模型没有坐标）。</summary>
        public void Add(Item item)
        {
            if (item == null) return;
            jars.Add(new ItemJar(item));
        }

        /// <summary>
        /// 放一件并给出坐标 —— 给"其实有二维摆放语义、但只想交一串 jar"的调用方用。
        /// ⚠ 传了坐标也**不会**因此获得「最大连续空位」：那个数字要求完整的二维占用表，
        ///   由 <see cref="StorageGridPanel.Refresh(Items)"/> 那条路径负责。
        /// </summary>
        public void Add(Item item, byte x, byte y, byte rot)
        {
            if (item == null) return;
            jars.Add(new ItemJar(x, y, rot, item));
        }
    }
}
