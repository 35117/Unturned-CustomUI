// ============================================================================
//  CustomUI · 通用「下拉选择器」（StorageUiDropdown，26.10.3.56）
// ============================================================================
//  一个显示当前值的按钮；点它展开一列选项，点选项即选中并收起。给**任何界面**用：
//  排序方式、筛选维度、模式切换、分类选择…
//
//  ★ 为什么这是一个"框架空白"（回 SDK 核过，不是没找到）：
//    · Glazier 的接口清单里**没有** `ISleekDropdown` —— 只有 Button / Box / Field / Label /
//      Slider / Toggle / Sprite / Image / ScrollView / 各种 numeric field（`SDG.Glazier/*.cs`）；
//    · 原版也没有真正的下拉：全库出现 `Dropdown` 的只有两处，且都不是控件 ——
//      `ServerPrefabUtil.cs` 把 `UnityEngine.UI.Dropdown` 列进**禁用组件名单**、
//      `CanvasSortOrders.cs` 的注释提到 devkit 的 Dropdown（而 devkit 实现在 SDK 里根本没有）。
//    ⟹ 第三方想"点一下展开选项"只能自己拼；本组件就是替它把这几件事做对。
//
//  ★ 画法取**原版自己的两个最近参考**、各取一半：
//    · `ItemStoreMenu.optionsPanel`（`ItemStoreMenu.cs:144-157` / `:616-620`）：
//      **弹层 = 触发按钮的兄弟节点，且在按钮之后 AddChild** —— 同一父容器里**后加的盖在先加的
//      上面**（`GlazierElementBase_uGUI.cs:333` 注释 "Order of children is important for depth"）；
//      开合就是 `panel.IsVisible = !panel.IsVisible`。
//    · `PlayerDashboardInventoryUI.selectionFrame`：弹层里的选项**点一下即收起**并写回选中值。
//
//  ⚠ 五条"只有读引擎源码才知道"的边界（本组件的实现就是围绕它们）：
//    1. **Glazier 没有"浮层/最高层"API，也没有绝对位置 API** —— `ISleekElement` 只有
//       `GetAbsoluteSize()`，**没有** `GetAbsolutePosition()`（`SDG.Glazier/Sleek.cs`）。
//       ⟹ **没法**把弹层挂到全局 Root 再按屏幕坐标摆。唯一可行的层叠手段就是
//       "**挂在共同父容器、且是最后添加的孩子**"。
//    2. **同一父容器重复 `AddChild` 是 no-op**（`GlazierElementBase_uGUI.cs:328-329`
//       `if (typedChild._parent == this) return;`）—— 所以**不能**靠"每次打开再挂一次"提升层级；
//       弹层的层级在它被加入的那一刻就定了。
//       ⟹ **硬约束**：同一个 parent 里，**在 Dropdown `Build` 之后添加的内容会盖住弹层**。
//       请把 Dropdown 建在同一容器内**较后**的位置（或给它一个专门的"浮层容器"当 parent）。
//    3. **`RemoveChild` 是"真拆"**（末尾 `InternalDestroy()`，铁律 #15/#38）⟹ 收起弹层只能用
//       `IsVisible = false`，**绝不能** `RemoveChild`（拆了下次就没得显示）。
//    4. ★★ **父容器若是滚动区（`ISleekScrollView`），弹层超出它 viewport 的部分会被裁** ——
//       uGUI `ScrollRect` 的 viewport 带 mask，裁掉的不只是**像素**，**连点击都收不到**
//       （实测症状：弹层只剩贴着触发器的一条、里面一个字没有、点它却点到下面的按钮）。
//       向下展开的弹层在"贴着面板底部"时**必然**中招 ⟹ 用 <see cref="StorageUiDropdown.OpenUpward"/>
//       把它翻到触发器上方（那里在可视范围内）。见坑 #91。
//    5. ★★ **`ISleekBox` 的底色做不到全不透明** —— 主题盒贴图 `DarkTheme/Box.png` 的**填充
//       alpha 只有 191/255**（实测 `(0,0,0,191)`；LightTheme 是 `(255,255,255,191)`），而 uGUI
//       `Image.color` 是**乘法** ⟹ 即使 `PopupBackground` 给 alpha 1，弹层最终也只有 ~75% 不透明，
//       **永远透出下面的内容**（用户 2026-10-10 实测「仍然透明」，坑 #92）。
//       ⟹ 弹层内必须再叠一层 `StorageUiFill`（纯白圆角贴图 **alpha 255** + `TintColor`）；
//       它 `raycastTarget = false` **不抢点击**，挡点击仍由 Box 负责 —— 两层各管一件事。
//       ⚠ 但这一层是**自绘白贴图** ⟹ 它吃的是**真彩色**，**不能**喂主题 tint：本主题的
//       颜色由**贴图**携带（`Box.png` 黑）、tint 只贡献 alpha，所以 `ESleekTint.BACKGROUND`
//       解析出来是 `SleekCustomization.backgroundColor`（**默认白**）⟹ 白贴图 × 白 =
//       **白色弹层**（用户 2026-10-10 实测「确实不透明，但是是白色的」，坑 #93）。
//
//  ⚠ 上色的现实（与另几个组件同源，坑 #81）：`ISleekButton.BackgroundColor` 在这套主题下
//    **只有 alpha 有效**（盒贴图填充是纯黑、uGUI 颜色是乘法）⟹ **"选中项"用两件事一起标**：
//    ① 文字颜色（`SelectedTextColor`，真彩色）；② 底色 alpha 差（`SelectedBackground` 更实）。
//    想要**自定义色相**的高亮块，自己叠一层 `StorageUiFill`（见 §4.29.3）。
//
//  ★ 生命周期与另八个公开组件一致：关界面用 `Hide()`（只藏不拆），明确不要了才 `Discard()`；
//    **`Build()` 同时是"恢复显示"的入口**（别漏 `IsVisible = true` —— 坑 #89 那一轮的教训）。
// ============================================================================
using System;
using SDG.Unturned;
using UnityEngine;

namespace CustomUI
{
    /// <summary>
    /// **选中变化回调**。<paramref name="index"/> 是选项下标（`-1` = 清空/无选择），
    /// <paramref name="key"/> 是该项的 key（<see cref="StorageUiDropdown.SetOptions(int[], string[])"/>
    /// 没给 key 数组时 = <paramref name="index"/>）。
    /// ⚠ **只在玩家点击选项时触发** —— 程序里设 <see cref="StorageUiDropdown.SelectedIndex"/> 不会回调
    /// （否则"设值 → 回调 → 又设值"会成环，与 `StorageFilterBar.ClearSelection` 同一条约定）。
    /// </summary>
    public delegate void StorageDropdownHandler(int index, int key);

    /// <summary>
    /// **通用「下拉选择器」**（`26.10.3.56`）：一个显示当前值的按钮，点开是一列选项。
    ///
    /// 典型用法：
    /// <code>
    /// dd = new StorageUiDropdown();
    /// dd.RowHeight = 24f;                          // 触发器高度（**下限 24**：低于它文字一个都不渲染，坑 #21）
    /// dd.ItemHeight = 24f;                         // 每个选项高度（同样下限 24）
    /// dd.Placeholder = "排序：默认";                // 没选中时按钮上显示什么
    /// dd.OpenUpward = true;                        // ★ 建在**滚动容器**里 / 位置靠下时**必须**开（见语义边界 4）
    /// dd.Build(myContainer);                       // ⚠ 建在同一容器**较后**的位置（见类头约束 2）
    /// dd.Element.PositionOffset_Y = 40f;           // 触发器位置由调用方摆（弹层会跟着它）
    /// dd.OnSelectionChanged = delegate (int i, int key) { SortBy(key); };
    /// dd.SetOptions(new int[] { 0, 1, 2 }, new string[] { "默认", "名称", "数量" });
    /// dd.SelectedIndex = 0;                        // 程序设初始值，**不触发**回调
    /// </code>
    ///
    /// **四条语义边界**：
    /// 1. **它是"选一个"**（`SelectedIndex` 单一真源）—— 要"选多个/多选"不是它的事。
    /// 2. **它不筛内容、不动数据** —— 选出什么由调用方在 <see cref="OnSelectionChanged"/> 里决定。
    /// 3. **弹层盖住的是"同一个 parent 内、在它之后添加的内容"** —— 见类头约束 2（架构限制，非缺陷）。
    /// 4. **弹层不会被父容器的裁剪框"救"** —— 父容器是滚动区时，先按 <see cref="OpenUpward"/> 翻面；
    ///    否则超出 viewport 的部分会被裁掉（连点击一起没），见类头约束 4。
    /// </summary>
    public class StorageUiDropdown
    {
        // 文字容器的**高度下限**：坑 #21 —— `SizeOffset_Y < 24` 时**一个字都不会渲染**（还不报错，
        // 「只剩图标/一片空白」）。触发按钮与每个选项都是文字容器 ⟹ 两个高度都夹到 ≥ 24
        // （`FilterBar.ButtonHeight` 内部夹到 24 是同一条纪律）。
        private const float MIN_ROW_HEIGHT = 24f;
        private const float MIN_ITEM_HEIGHT = 24f;

        private float _rowHeight = 24f;
        private float _itemHeight = 24f;
        private bool _openUpward;
        private ESleekFontSize _fontSize = ESleekFontSize.Default;
        private TextAnchor _textAlignment = TextAnchor.MiddleLeft;
        private SleekColor _textColor = new SleekColor(ESleekTint.FONT, 1f);
        private SleekColor _itemTextColor = new SleekColor(ESleekTint.FONT, 1f);
        private SleekColor _selectedTextColor = new SleekColor(ESleekTint.FOREGROUND, 1f);
        private SleekColor _itemBackground = new SleekColor(ESleekTint.BACKGROUND, 0.5f);
        private SleekColor _selectedBackground = new SleekColor(ESleekTint.BACKGROUND, 1f);
        // ★ 默认值**必须是真彩色**（这里是"暗色主题用黑、浅色主题用主题背景"），
        //   **不能**写 `new SleekColor(ESleekTint.BACKGROUND, 1f)` —— 那会解析成
        //   `SleekCustomization.backgroundColor`（**默认白**）⟹ 白贴图 × 白 = **白色弹层**（坑 #93）。
        private SleekColor _popupBackground = SleekColor.BackgroundIfLight(UnityEngine.Color.black);
        private string _placeholder = "—";

        // 数据（与 StorageFilterBar 同一形状：数组被直接引用、不拷贝）
        private int[] keys;
        private string[] labels;
        private int used;
        private int selectedIndex = -1;

        private ISleekButton trigger;      // 触发器（调用方摆位置/尺寸）
        private ISleekBox popup;           // 弹层（与触发器同 parent、在它之后加入）
        private StorageUiFill popupSolid;  // 弹层里的**不透明实心底**（见类头约束 5）
        private ISleekButton[] items;      // 选项按钮池（按需增长）
        private bool built;
        private bool warnedCallback;

        // ================= 外观旋钮 =================

        /// <summary>触发器高度（像素，默认 24，最小 2）。</summary>
        public float RowHeight
        {
            get { return _rowHeight; }
            set
            {
                float v = value < MIN_ROW_HEIGHT ? MIN_ROW_HEIGHT : value;
                if (_rowHeight == v) return;
                _rowHeight = v;
                LayoutTrigger();
            }
        }

        /// <summary>每个选项的高度（像素，默认 22，最小 2）。</summary>
        public float ItemHeight
        {
            get { return _itemHeight; }
            set
            {
                float v = value < MIN_ITEM_HEIGHT ? MIN_ITEM_HEIGHT : value;
                if (_itemHeight == v) return;
                _itemHeight = v;
                RefreshItems();
                if (IsOpen) LayoutPopup();
            }
        }

        /// <summary>文字字号（枚举 6 档；触发按钮与选项共用）。</summary>
        public ESleekFontSize FontSize
        {
            get { return _fontSize; }
            set { _fontSize = value; RefreshTrigger(); RefreshItems(); }
        }

        /// <summary>触发器文字对齐（默认 `MiddleLeft` —— 下拉按钮的惯例）。</summary>
        public TextAnchor TextAlignment
        {
            get { return _textAlignment; }
            set
            {
                if (_textAlignment == value) return;
                _textAlignment = value;
                if (trigger != null) { try { trigger.TextAlignment = _textAlignment; } catch (Exception) { } }
            }
        }

        /// <summary>触发器文字颜色（默认 = 玩家选的字体色）。</summary>
        public SleekColor TextColor
        {
            get { return _textColor; }
            set { _textColor = value; RefreshTrigger(); }
        }

        /// <summary>**未选中**选项的文字颜色（真彩色）。</summary>
        public SleekColor ItemTextColor
        {
            get { return _itemTextColor; }
            set { _itemTextColor = value; RefreshItems(); }
        }

        /// <summary>**选中**选项的文字颜色（真彩色）—— 选中态的主要视觉信号。</summary>
        public SleekColor SelectedTextColor
        {
            get { return _selectedTextColor; }
            set { _selectedTextColor = value; RefreshItems(); }
        }

        /// <summary>未选中选项的底色。⚠ 按钮底色**只有 alpha 有效**（坑 #81）。</summary>
        public SleekColor ItemBackground
        {
            get { return _itemBackground; }
            set { _itemBackground = value; RefreshItems(); }
        }

        /// <summary>选中选项的底色（比未选中更实 = alpha 更大）。⚠ 同样只有 alpha 有效。</summary>
        public SleekColor SelectedBackground
        {
            get { return _selectedBackground; }
            set { _selectedBackground = value; RefreshItems(); }
        }

        /// <summary>
        /// 弹层底色。★ **`26.10.3.58` 起它是真彩色** —— 组件内部已经叠了一层 `StorageUiFill`
        /// （纯白圆角贴图，alpha 255），所以这个颜色**原样**画出来，不再被主题盒贴图那道
        /// 191/255 的衰减吃掉（坑 #92）。`alpha = 1` 就是**完全不透明**。
        /// ⚠ **必须给真彩色**（例如 `new SleekColor(new Color(0.1f, 0.1f, 0.12f, 1f))`）。
        /// **不要**给 `ESleekTint.BACKGROUND` / `FOREGROUND` 这类**主题 tint** —— 本主题的
        /// 颜色由**贴图**携带（`Box.png` 是黑）、tint 只贡献 alpha，所以
        /// `ESleekTint.BACKGROUND` 解析出来是 `SleekCustomization.backgroundColor`（**默认白**），
        /// 白贴图乘上去就是**白色弹层**（坑 #93）。默认值已按「暗色=黑 / 浅色=主题背景」给好，
        /// 通常**不用动这个旋钮**。
        /// </summary>
        public SleekColor PopupBackground
        {
            get { return _popupBackground; }
            set
            {
                _popupBackground = value;
                if (popup != null) { try { popup.BackgroundColor = _popupBackground; } catch (Exception) { } }
                if (popupSolid != null) { try { popupSolid.Color = _popupBackground; } catch (Exception) { } }
            }
        }

        /// <summary>没选中任何项时触发器上显示什么（默认一个破折号）。</summary>
        public string Placeholder
        {
            get { return _placeholder; }
            set
            {
                _placeholder = (value == null) ? "" : value;
                RefreshTrigger();
            }
        }

        /// <summary>
        /// 弹层向**上**展开（默认 `false` = 向下，符合"下拉"直觉）。
        ///
        /// ⚠ **什么时候必须设 `true`**：Dropdown 建在**滚动容器**（`ISleekScrollView`，例如原版右列
        ///   `areaBox`）里、且位置**靠下**时。uGUI `ScrollRect` 的 viewport **会裁掉**超出可视范围的
        ///   内容 ⟹ 向下展开的弹层会被截成一条、**被截掉的部分连点击都收不到**（坑 #91：
        ///   "浮层被滚动区裁"）。向上展开则把它放回可视范围内。
        ///
        /// ⚠ 为什么不自动判断：Glazier **没有**绝对位置 API（只有 `GetAbsoluteSize()`，
        ///   见类头约束 1），组件拿不到"触发器在屏幕/容器里的位置"，**算不出**下方还剩多少空间 ⟹
        ///   由调用方告诉它（与"弹层层级由 parent 决定"同一条：能力边界交给调用方）。
        /// </summary>
        public bool OpenUpward
        {
            get { return _openUpward; }
            set
            {
                if (_openUpward == value) return;
                _openUpward = value;
                if (IsOpen) LayoutPopup();      // 已经展开就就地翻个面
            }
        }

        // ================= 状态 =================

        /// <summary>触发器本身（**未建之前是 null**）。位置与宽度由调用方摆；想加悬浮提示就设 `TooltipText`。</summary>
        public ISleekElement Element { get { return trigger; } }

        public bool IsBuilt { get { return built; } }

        /// <summary>
        /// 选中变化回调（**只在玩家点击选项时**触发；程序设 `SelectedIndex` / `ClearSelection`
        /// 都**不回调** —— 否则会形成"设值 → 回调 → 又设值"的回环，与 `StorageFilterBar` 同一条约定）。
        /// </summary>
        public StorageDropdownHandler OnSelectionChanged;

        /// <summary>弹层当前是否展开。</summary>
        public bool IsOpen { get; private set; }

        /// <summary>占位高度 = <see cref="RowHeight"/>。</summary>
        public float DesiredHeight { get { return _rowHeight; } }

        /// <summary>这一轮有几个可选项。</summary>
        public int OptionCount { get { return used; } }

        /// <summary>
        /// 当前选中的下标（`-1` = 没选）。**程序设值不会触发 <see cref="OnSelectionChanged"/>**；
        /// 越界会自动夹到 `[-1, OptionCount-1]`。
        /// </summary>
        public int SelectedIndex
        {
            get { return selectedIndex; }
            set
            {
                int v = value;
                if (v < -1) v = -1;
                if (v > used - 1) v = used - 1;
                if (selectedIndex == v) return;
                selectedIndex = v;
                RefreshTrigger();
                RefreshItems();
            }
        }

        /// <summary>
        /// 当前选中项的 key（`SetOptions` 没给 key 数组时 = <see cref="SelectedIndex"/>）。
        /// 没选中时 = `-1`（`SelectedIndex` 也是 `-1`）。
        /// </summary>
        public int SelectedKey
        {
            get
            {
                if (selectedIndex < 0) return -1;
                if (keys == null || selectedIndex >= keys.Length) return selectedIndex;
                return keys[selectedIndex];
            }
        }

        /// <summary>当前选中项的文本（没选中 = 空串）。</summary>
        public string SelectedLabel
        {
            get
            {
                if (selectedIndex < 0) return "";
                if (labels == null || selectedIndex >= labels.Length || labels[selectedIndex] == null) return "";
                return labels[selectedIndex];
            }
        }

        // ================= 数据入口 =================

        /// <summary>
        /// 填这一轮的可选项。<paramref name="optionKeys"/> 传 null 时 key = 下标。
        /// ⚠ 数组会被**直接引用**、不拷贝（调用方每轮复用自己的缓冲区即可，零分配）。
        /// ⚠ 选项变了之后，选中项若越界会被自动夹回去（选中的那一项**该留在列表里** ——
        ///   否则玩家看不见自己在选什么，与 `StorageFilterBar` 同一条约定）。
        /// </summary>
        public void SetOptions(int[] optionKeys, string[] optionLabels)
        {
            keys = optionKeys;
            labels = optionLabels;

            int max = (optionLabels == null) ? 0 : optionLabels.Length;
            if (optionKeys != null && optionKeys.Length < max) max = optionKeys.Length;
            used = max;

            // 选中项夹进新范围（-1 保留为"没选"）
            if (selectedIndex > used - 1) selectedIndex = used - 1;
            if (selectedIndex < -1) selectedIndex = -1;

            warnedCallback = false;     // 新的一轮 ⟹ 允许再报一次异常
            RefreshTrigger();
            RefreshItems();
            if (IsOpen) LayoutPopup();
        }

        /// <summary>只给文本（key = 下标）。</summary>
        public void SetOptions(string[] optionLabels)
        {
            SetOptions(null, optionLabels);
        }

        /// <summary>清空所有选项（连带取消选中）。**不触发回调**。</summary>
        public void ClearOptions()
        {
            SetOptions(null, null);
        }

        /// <summary>
        /// 取消选中（回到 <see cref="Placeholder"/>）。**不触发回调** —— 调用方自己知道是它清的；
        /// 触发会形成"清 → 回调 → 又清"的回环。
        /// </summary>
        public void ClearSelection()
        {
            if (selectedIndex == -1) return;
            selectedIndex = -1;
            RefreshTrigger();
            RefreshItems();
        }

        // ================= 生命周期 =================

        /// <summary>
        /// 建一次并挂进 <paramref name="parent"/>（触发器铺满它的宽度，高度取 <see cref="RowHeight"/>）。
        /// 重复调用只是重新挂父容器，不重复建。
        /// ⚠ **弹层也挂在同一个 parent、且在触发器之后** —— 所以它盖得住同容器内**此前**的内容，
        ///   盖不住**在此之后**再加的内容（见类头约束 2）。
        /// ⚠ 与另几个组件一样：它同时是"恢复显示"的入口（宿主的区块面板只藏不拆）。
        /// </summary>
        public void Build(ISleekElement parent)
        {
            if (parent == null) return;

            if (built && trigger != null)
            {
                // ⚠ 不能先 RemoveChild（那是 InternalDestroy）。AddChild 自己会从旧父容器摘走。
                try { parent.AddChild(trigger); } catch (Exception) { return; }
                if (popup != null) { try { parent.AddChild(popup); } catch (Exception) { } }
                // 实心底跟着弹层走（它是 popup 的子；重挂同时"恢复显示" —— 与 StorageUiFill 同一语义）
                if (popupSolid != null) { try { popupSolid.Build(popup); } catch (Exception) { } }

                IsOpen = false;
                try { if (popup != null) popup.IsVisible = false; } catch (Exception) { }
                RefreshTrigger();
                RefreshItems();
                // ★ 必须显式恢复可见性（Hide 之后 Build 再也显示不出来 —— 坑 #89 那一轮的教训）。
                try { trigger.IsVisible = true; } catch (Exception) { }
                return;
            }

            try
            {
                trigger = Glazier.Get().CreateButton();
                trigger.SizeScale_X = 1f;             // 宽度跟随父容器（调用方可覆盖）
                trigger.SizeOffset_X = 0f;
                trigger.TextAlignment = _textAlignment;
                trigger.OnClicked += OnTriggerClicked;
                try { parent.AddChild(trigger); }
                catch (Exception) { trigger = null; return; }

                // ★ 弹层紧跟触发器、在它之后加入 ⟹ 画在它（及此前一切内容）上面。
                popup = Glazier.Get().CreateBox();
                popup.IsVisible = false;
                popup.SizeScale_X = 1f;
                popup.BackgroundColor = _popupBackground;
                try { parent.AddChild(popup); }
                catch (Exception) { popup = null; }

                // ★★ 再铺一层**不透明实心底**（坑 #92）：主题 `Box.png` 的填充 alpha 只有 **191/255**
                //   （`DarkTheme/Box.png` 实测 `(0,0,0,191)`），而 uGUI `Image.color` 是**乘法**
                //   ⟹ 单靠 `Box.BackgroundColor` 的上限就是 ~75% 不透明，**永远透出下面的内容**
                //   （用户 2026-10-10 实测「仍然透明」）。
                //   `StorageUiFill` 用**纯白圆角贴图（alpha 255）** + `TintColor` ⟹ 真不透明；
                //   而且它 `raycastTarget = false`，**不抢点击** —— 挡点击仍由上面那层 Box 负责。
                //   ⚠ 必须在这里建、也就是赶在 `RefreshItems()`（它才去加选项按钮）**之前** ——
                //     同一父容器里后加的盖在先加的上面，晚一步它就把选项全盖住了。
                if (popup != null)
                {
                    popupSolid = new StorageUiFill();
                    popupSolid.Inset = 0f;                  // 铺满（留边的话外圈会露出 Box 那层半透明底）
                    popupSolid.Color = _popupBackground;    // alpha = 1 ⟹ 不透明
                    popupSolid.Build(popup);
                }
            }
            catch (Exception) { trigger = null; popup = null; popupSolid = null; return; }

            built = true;
            IsOpen = false;
            LayoutTrigger();
            RefreshTrigger();
            RefreshItems();
            try { trigger.IsVisible = true; } catch (Exception) { }
        }

        /// <summary>只藏不拆（临时收起时用；触发器与弹层都还在）。再显示靠 `Build(parent)`。</summary>
        public void Hide()
        {
            Close();
            try { if (trigger != null) trigger.IsVisible = false; } catch (Exception) { }
        }

        /// <summary>真拆掉（下一次 `Build` 会重建）。调用方明确不要它了才用。</summary>
        public void Discard()
        {
            IsOpen = false;
            items = null;
            try
            {
                if (popup != null && popup.Parent != null) popup.Parent.RemoveChild(popup);
            }
            catch (Exception) { }
            try
            {
                if (trigger != null && trigger.Parent != null) trigger.Parent.RemoveChild(trigger);
            }
            catch (Exception) { }

            popup = null;
            // ⚠ 实心底不单独拆除：它挂在 popup 里，随 popup 一起被销毁
            //   （`RemoveChild` = `InternalDestroy`，拆完再挂就是尸体 —— 铁律 #38）。
            popupSolid = null;
            trigger = null;
            built = false;
            warnedCallback = false;
        }

        /// <summary>展开选项（选项为空时不展开）。</summary>
        public void Open()
        {
            if (!built || trigger == null || popup == null) return;
            if (IsOpen) return;
            if (used <= 0) return;

            IsOpen = true;
            LayoutPopup();          // 读触发器**当前**几何（调用方可能刚挪过它）
            RefreshItems();
            try { popup.IsVisible = true; } catch (Exception) { }
        }

        /// <summary>收起选项（`IsVisible = false`，**不拆** —— 见类头约束 3）。</summary>
        public void Close()
        {
            IsOpen = false;
            try { if (popup != null) popup.IsVisible = false; } catch (Exception) { }
        }

        /// <summary>开/合（触发器点击走的就是它）。</summary>
        public void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        // ================= 内部 =================

        private void OnTriggerClicked(ISleekElement button)
        {
            Toggle();
        }

        private void OnItemClicked(int index)
        {
            if (index < 0 || index >= used) return;

            selectedIndex = index;
            RefreshTrigger();
            RefreshItems();
            Close();
            FireSelectionChanged();
        }

        private void FireSelectionChanged()
        {
            StorageDropdownHandler h = OnSelectionChanged;
            if (h == null) return;
            try { h(selectedIndex, SelectedKey); }
            catch (Exception e)
            {
                if (warnedCallback) return;
                warnedCallback = true;
                CustomUIPlugin.Error("下拉选择回调异常：" + e.Message);
            }
        }

        private void LayoutTrigger()
        {
            if (trigger == null) return;
            try { trigger.SizeOffset_Y = _rowHeight; } catch (Exception) { }
        }

        /// <summary>
        /// 弹层几何 = **复制触发器当前的** 左/宽，纵向紧贴触发器（下沿或上沿，看 <see cref="OpenUpward"/>）。
        /// ⚠ 必须在**打开那一刻**重读触发器几何（调用方可能刚摆过位置）。
        /// ⚠ `PositionOffset_Y` 是弹层**顶边**相对父容器顶边的偏移（pivot 在左上、向下延伸）——
        ///   所以"向上展开" = 顶边退到 `触发器顶边 - 弹层高`（底边正好贴住触发器上沿）。
        /// </summary>
        private void LayoutPopup()
        {
            if (popup == null || trigger == null) return;
            try
            {
                float popupHeight = used * _itemHeight;

                popup.PositionScale_X = trigger.PositionScale_X;
                popup.PositionOffset_X = trigger.PositionOffset_X;
                popup.SizeScale_X = trigger.SizeScale_X;
                popup.SizeOffset_X = trigger.SizeOffset_X;
                popup.PositionScale_Y = 0f;
                popup.PositionOffset_Y = _openUpward
                    ? (trigger.PositionOffset_Y - popupHeight)
                    : (trigger.PositionOffset_Y + _rowHeight);
                popup.SizeOffset_Y = popupHeight;
            }
            catch (Exception) { }
        }

        private void RefreshTrigger()
        {
            if (trigger == null) return;
            try
            {
                string text = (selectedIndex >= 0) ? SelectedLabel : _placeholder;
                if (trigger.Text != text) trigger.Text = text;
                trigger.FontSize = _fontSize;
                trigger.TextColor = _textColor;
            }
            catch (Exception) { }
        }

        private void RefreshItems()
        {
            if (popup == null) return;

            EnsureItems(used);
            if (items == null) return;

            for (int i = 0; i < items.Length; i++)
            {
                ISleekButton b = items[i];
                if (b == null) continue;

                bool on = i < used;
                try
                {
                    b.IsVisible = on;
                    if (!on) continue;

                    string lb = (labels != null && i < labels.Length && labels[i] != null)
                        ? labels[i] : "";
                    if (b.Text != lb) b.Text = lb;

                    b.PositionScale_X = 0f;         // 弹层内满宽（相对弹层）
                    b.SizeScale_X = 1f;
                    b.PositionOffset_X = 0f;
                    b.SizeOffset_X = 0f;
                    b.PositionOffset_Y = i * _itemHeight;
                    b.SizeOffset_Y = _itemHeight;
                    b.FontSize = _fontSize;
                    b.TextAlignment = TextAnchor.MiddleLeft;

                    bool sel = (i == selectedIndex);
                    // ⚠ 按钮底色**只有 alpha 有效**（坑 #81）⟹ 选中同时靠"更实的底 + 亮的字"。
                    b.BackgroundColor = sel ? _selectedBackground : _itemBackground;
                    b.TextColor = sel ? _selectedTextColor : _itemTextColor;
                }
                catch (Exception) { }
            }
        }

        /// <summary>按需把选项按钮池长到 <paramref name="n"/> 个（只增不减；多余的只藏）。</summary>
        private void EnsureItems(int n)
        {
            if (n < 0) n = 0;
            if (items == null) items = new ISleekButton[0];
            if (items.Length >= n) return;

            ISleekButton[] grown = new ISleekButton[n];
            Array.Copy(items, grown, items.Length);
            for (int i = items.Length; i < n; i++)
            {
                ISleekButton b = Glazier.Get().CreateButton();
                b.IsVisible = false;
                // ⚠ 回调只捕获**下标**，内容在点击那一刻现读 —— 选项列表会重排，
                //   闭包捕获"当时那一条"就会点 A 选 B（与行池那条坑同源）。
                int idx = i;
                b.OnClicked += delegate (ISleekElement el) { OnItemClicked(idx); };
                if (popup != null) popup.AddChild(b);
                grown[i] = b;
            }
            items = grown;
        }
    }
}
