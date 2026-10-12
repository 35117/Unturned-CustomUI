// ============================================================================
//  StorageUiMenu —— 「右键上下文菜单」（26.10.3.61 新增）
//
//  场景：格子上点右键想弹一排动作（取出 / 拆分 / 丢弃 / 打开管理…）。
//
//  ★ 它和 `StorageUiDropdown` 的**形状不同**，这是被引擎逼出来的：
//    · Dropdown 有"触发器元素"⟹ 弹层能挂在触发器**同一个 parent**里（后加 = 盖在上面）。
//    · 右键菜单**没有触发器**（触发器是"鼠标右键"这件事本身）⟹ 它必须能盖住
//      **整个界面**（包括原版右列、玩家背包网格那些不属于我们的元素）。
//
//  ★ 正解照抄原版（不是我们自己发明的）：`PlayerDashboardInventoryUI` 里那对
//      `selectionFrame` + `outsideSelectionInvisibleButton`（`:2820-2848`）——
//      全屏遮罩**先加**、内容面板**后加**（后加的盖在上面）：
//        PlayerUI.container （原版**全屏**容器，`SizeScale = 1`，public static）
//          ├─ scrim  ← 全屏遮罩：点它就"点到了菜单外面"⟹ 收起菜单
//          └─ panel  ← 菜单本体：后加 ⟹ 在遮罩之上，点它不会穿透到遮罩
//      ⚠ 原版那张遮罩用的是 `GlazierResources.PixelTexture` —— 那个类 `internal`，**插件拿不到**
//        （README 坑 #88 ②），所以这里显式挂 `Texture2D.whiteTexture`（坑 #84）。
//
//  ★★ "在鼠标处弹出"**能做**（原版自己没做，它把选择框居中）：
//      不需要绝对位置 API —— 只需要**缩放比**。`Glazier_uGUI` 给 canvas 挂的是
//      `CanvasScaler` + `ScaleMode.ConstantPixelSize` + `scaleFactor = GraphicsSettings.userInterfaceScale`
//      （`Glazier_uGUI.cs:748-766`）⟹ `1 Glazier 单位 = userInterfaceScale 个屏幕像素`。
//      于是：`x = mouse.x / scale`、`y = (Screen.height - mouse.y) / scale`。
//      ⚠ 这是**本组件唯一拿不准的地方**（UI 缩放不是 1 时若有别的缩放层叠加就会偏），
//        所以坐标永远由**调用方**决定（`Show(x, y)`）；`ShowAtPointer()` 只是把上面这套
//        换算替你做掉，且**逐帧可重算**（拖鼠标时想跟着走就每帧调）。
//
//  ⚠ 三条边界：
//    1. **同一时刻只该有一个菜单**（两个实例各带一张全屏遮罩，后加的会盖住先加的菜单）。
//    2. **遮罩默认是"半透明黑"**（`ScrimColor` 默认 alpha 0.35）—— 设成 alpha 0 就是
//       "看不见但仍挡住点击"（uGUI 的 raycast **不看** alpha）。要给 0 才像右键菜单的观感。
//    3. **它挂在 `PlayerUI.container` 上，不在你的容器里** ⟹ `Element` 的坐标系是**全屏**，
//       不是 `areaBox`（`Show` 收的 x/y 就是全屏 Glazier 坐标）。
//
//  ★ 生命周期与另几个公开组件同一套：`Build()` 幂等、`Hide()` **只藏不拆**、
//    明确不要了才 `Discard()`。宿主/调用方都**不该** `RemoveChild`。
// ============================================================================
using System;
using SDG.Unturned;
using UnityEngine;

namespace CustomUI
{
    /// <summary>
    /// 右键上下文菜单：一排可点的动作。挂在**全屏容器**上，所以能盖住整个界面（含原版元素）。
    ///
    /// 典型用法（格子右键弹在鼠标处）：
    /// <code>
    /// menu = new StorageUiMenu();
    /// menu.Title = "这件物品";
    /// menu.SetItems(new int[] { 1, 2, 3 }, new string[] { "取出", "拆分", "丢弃" });
    /// menu.OnItemSelected = delegate (int index, int key) { ... };
    /// menu.Build();                       // 幂等，建一次
    /// // 在右键回调里：
    /// grid.OnSlotRightClicked += delegate (StorageSlotHit hit) {
    ///     menu.ShowAtPointer();           // 弹在鼠标处（坐标换算见类头）
    /// };
    /// </code>
    /// ⚠ `Show` 的 x/y 是**全屏** Glazier 坐标（不是 `areaBox` 内的）。
    /// </summary>
    public class StorageUiMenu
    {
        // ================= 外观旋钮 =================

        private float _width = 180f;
        private float _itemHeight = 26f;
        private float _headerHeight = 26f;
        private string _title = "";
        private float _padding = 4f;
        private ESleekFontSize _fontSize = ESleekFontSize.Default;
        private TextAnchor _textAlignment = TextAnchor.MiddleLeft;
        private SleekColor _itemTextColor = new SleekColor(ESleekTint.FONT, 1f);
        private SleekColor _disabledTextColor = new SleekColor(ESleekTint.FOREGROUND, 0.4f);
        private SleekColor _itemBackground = new SleekColor(ESleekTint.BACKGROUND, 0.6f);
        // ★ 默认值**必须是真彩色**：面板实心底走的是自绘白贴图（tint 就是目标色），
        //   给 `ESleekTint.BACKGROUND` 会解析成 `SleekCustomization.backgroundColor`（默认白）
        //   ⟹ 白 × 白 = **白色菜单**（坑 #93，上一轮在 Dropdown 上刚踩过）。
        private SleekColor _popupBackground = SleekColor.BackgroundIfLight(UnityEngine.Color.black);
        // 遮罩：默认半透明黑（模态观感）。★ alpha = 0 就是"看不见但仍挡点击"（uGUI raycast 不看 alpha）。
        private SleekColor _scrimColor = new SleekColor(new UnityEngine.Color(0f, 0f, 0f, 0.35f));

        // ================= 自动收起（`26.10.3.62`）=================
        //   ★ 为什么需要：右键菜单是**我们自己**的浮层，**引擎不会替我们关** ——
        //     玩家按 Esc（或 Alt+Tab 让游戏自动暂停）时，游戏会关掉存储界面、弹出暂停菜单，
        //     而我们的菜单会**孤零零留在屏幕上**（用户实测报的正是这个）。
        //   ⟹ 组件每帧自检四条条件（见 `Tick`），任一成立就收起：
        //     ① 画布没了 / 容器换了实例（回主菜单 · 换关 · 换服）—— **硬条件，无旋钮**
        //     ② 游戏窗口失焦（Alt+Tab 切到别的窗口）
        //     ③ 宿主界面关了 / 被收走（Esc · 自动暂停 · 玩家走开）
        //     ④ 调用方自己的判据（`KeepOpen`）
        private bool _hideWithHost = true;
        private bool _hideWhenUnfocused = true;
        private bool _sawFocus;      // ⚠ 失焦判定用的"闩"—— 见 `Tick` 里的注释

        /// <summary>菜单宽度（Glazier 单位）。默认 180。</summary>
        public float Width
        {
            get { return _width; }
            set { _width = value < 40f ? 40f : value; }
        }

        /// <summary>每个选项的高度。**下限 24** —— 低于它文字一个都不渲染（坑 #21）。</summary>
        public float ItemHeight
        {
            get { return _itemHeight; }
            set { _itemHeight = value < MIN_ROW_HEIGHT ? MIN_ROW_HEIGHT : value; }
        }

        /// <summary>标题行高（`Title` 为空时不显示这一行）。同样下限 24。</summary>
        public float HeaderHeight
        {
            get { return _headerHeight; }
            set { _headerHeight = value < MIN_ROW_HEIGHT ? MIN_ROW_HEIGHT : value; }
        }

        /// <summary>标题（空串 = 不显示标题行）。⚠ 标签是 Truncate 不换行，写短点。</summary>
        public string Title
        {
            get { return _title; }
            set { _title = (value == null) ? "" : value; }
        }

        /// <summary>面板内边距。默认 4。</summary>
        public float Padding
        {
            get { return _padding; }
            set { _padding = value < 0f ? 0f : value; }
        }

        /// <summary>选项文字字号。</summary>
        public ESleekFontSize FontSize
        {
            get { return _fontSize; }
            set { _fontSize = value; }
        }

        /// <summary>选项文字对齐。</summary>
        public TextAnchor TextAlignment
        {
            get { return _textAlignment; }
            set { _textAlignment = value; }
        }

        /// <summary>可点选项的文字色（默认跟随主题字体色）。</summary>
        public SleekColor ItemTextColor
        {
            get { return _itemTextColor; }
            set { _itemTextColor = value; RefreshItems(); }
        }

        /// <summary>**禁用**项的文字色（默认主题前景色 40%）—— 比可点项暗，一眼看出点不动。</summary>
        public SleekColor DisabledTextColor
        {
            get { return _disabledTextColor; }
            set { _disabledTextColor = value; RefreshItems(); }
        }

        /// <summary>选项底色（⚠ 与别处同源：按钮底色**只有 alpha 有效**，坑 #81）。</summary>
        public SleekColor ItemBackground
        {
            get { return _itemBackground; }
            set { _itemBackground = value; RefreshItems(); }
        }

        /// <summary>
        /// 面板底色。**真彩色**（内部叠了一层 `StorageUiFill` 绕开主题盒贴图那道 191/255 衰减，坑 #92）
        /// ⟹ `alpha = 1` 就是完全不透明。**别给 `ESleekTint.*`**（会解析成主题 tint = 白，坑 #93）。
        /// </summary>
        public SleekColor PopupBackground
        {
            get { return _popupBackground; }
            set
            {
                _popupBackground = value;
                if (panel != null) { try { panel.BackgroundColor = _popupBackground; } catch (Exception) { } }
                if (panelSolid != null) { try { panelSolid.Color = _popupBackground; } catch (Exception) { } }
            }
        }

        /// <summary>
        /// 全屏遮罩色。默认半透明黑（模态观感）。
        /// ★ 给 `new SleekColor(new Color(0,0,0,0))` = **隐身但仍挡点击** —— uGUI 的 raycast 不看 alpha。
        /// </summary>
        public SleekColor ScrimColor
        {
            get { return _scrimColor; }
            set
            {
                _scrimColor = value;
                if (scrim != null) { try { scrim.TintColor = _scrimColor; } catch (Exception) { } }
            }
        }

        /// <summary>
        /// **宿主界面关了 / 被收走时自动收起**（默认 `true`）。
        /// 判据 = `StorageUiPanels.IsOpen`（界面还被我方接管着吗）—— 玩家按 Esc 关掉存储界面、
        /// 或让游戏自动暂停时它会变 `false`。⚠ 若你的菜单不属于存储界面（自己另开的一套 UI），
        /// 设 `false` 并改用 <see cref="KeepOpen"/> 给判据。
        /// </summary>
        public bool HideWithHost
        {
            get { return _hideWithHost; }
            set { _hideWithHost = value; }
        }

        /// <summary>
        /// **游戏窗口失焦时自动收起**（默认 `true`）—— Alt+Tab 切到别的窗口。
        /// ⚠ 实现上带一个"闩"：只在**见过一次焦点为真**之后才认失焦，
        /// 免得某些环境全程报 `false` ⟹ 菜单刚弹出就自己没了（那比不收起更糟）。
        /// </summary>
        public bool HideWhenUnfocused
        {
            get { return _hideWhenUnfocused; }
            set { _hideWhenUnfocused = value; }
        }

        /// <summary>
        /// 调用方自己的「现在还该开着吗」判据（`null` = 不用它）。
        /// ★ 每帧问一次；返回 `false` ⟹ 收起（异常也算 `false`，并且只按"收起"处理，不刷日志）。
        /// 典型写法：`menu.KeepOpen = delegate { return myUiStillVisible; };`
        /// </summary>
        public Func<bool> KeepOpen;

        // ================= 数据 =================
        private int[] keys;
        private string[] labels;
        private bool[] enabledMask;     // null = 全部可点
        private int used;

        /// <summary>当前有几项。</summary>
        public int ItemCount { get { return used; } }

        /// <summary>
        /// 设选项。⚠ 数组**被直接引用、不拷贝**（与 `StorageFilterBar` / `StorageUiDropdown` 同一个约定），
        /// 改数组内容要重新调一次本方法。
        /// </summary>
        public void SetItems(int[] itemKeys, string[] itemLabels)
        {
            keys = itemKeys;
            labels = itemLabels;
            enabledMask = null;
            int n = 0;
            if (itemKeys != null && itemLabels != null)
            {
                n = itemKeys.Length < itemLabels.Length ? itemKeys.Length : itemLabels.Length;
            }
            used = n;
            if (IsOpen) Layout();      // 开着的时候改项 = 立刻重排（不然size 不对）
            RefreshItems();
        }

        /// <summary>清空选项（列表还在，只是没有项）。</summary>
        public void ClearItems()
        {
            keys = null;
            labels = null;
            enabledMask = null;
            used = 0;
            RefreshItems();
        }

        /// <summary>把某一项标成**禁用**（灰掉、点不动）—— 例如"没有材料所以不能合成"。</summary>
        public void SetItemEnabled(int index, bool on)
        {
            if (enabledMask == null)
            {
                enabledMask = new bool[used];
                for (int i = 0; i < used; i++) enabledMask[i] = true;
            }
            if (index < 0 || index >= used) return;
            enabledMask[index] = on;
            RefreshItems();
        }

        /// <summary>项被选中（只有**可点**的项会回调）。参数：下标 / 键。</summary>
        public delegate void ItemSelectedHandler(int index, int key);

        /// <summary>选中回调。</summary>
        public ItemSelectedHandler OnItemSelected;

        // ================= 状态 =================

        private const float MIN_ROW_HEIGHT = 24f;

        private ISleekImage scrim;          // 全屏遮罩（点它 = 点到了外面 ⟹ 收起）
        private ISleekBox panel;            // 菜单本体（挡点击 + 圆角外框）
        private StorageUiFill panelSolid;   // 不透明实心底（坑 #92）
        private ISleekLabel header;         // 标题（Title 为空时不显示）
        private ISleekButton[] items;       // 选项按钮池（只增不减）
        private bool built;
        private bool opened;
        private bool warnedCallback;

        /// <summary>菜单面板根元素（没建好时为 null）。⚠ 它挂在**全屏容器**上。</summary>
        public ISleekElement Element { get { return panel; } }

        /// <summary>面板是否已建好。</summary>
        public bool IsBuilt { get { return built && panel != null; } }

        /// <summary>菜单是否正开着。</summary>
        public bool IsOpen { get { return opened; } }

        /// <summary>本菜单当前的高度（标题 + 各项 + 上下留白）。</summary>
        public float DesiredHeight
        {
            get
            {
                float h = _padding * 2f + used * _itemHeight;
                if (!string.IsNullOrEmpty(_title)) h += _headerHeight;
                return h;
            }
        }

        // ================= 全屏容器 =================

        /// <summary>
        /// 菜单与遮罩挂哪 —— 原版那个**全屏**容器（`PlayerUI.container`，`SizeScale = 1`，
        /// `PlayerUI.cs:2259-2261`）。原版的"点外部关闭"遮罩就挂它（`:2824-2838`）。
        /// ⚠ 它只在玩家 UI 存在时非 null（打开存储界面时必然存在）。
        /// </summary>
        private static ISleekElement OverlayParent()
        {
            try { return PlayerUI.container; }
            catch (Exception) { return null; }
        }

        // ================= 生命周期 =================

        /// <summary>
        /// 建菜单 + 遮罩（**幂等**；两者都挂在 `PlayerUI.container`）。建完是**藏着**的，
        /// 要显示得调 `Show` / `ShowAtPointer`。
        /// ⚠ 它**不**在调用方的容器里 ⟹ 调用方不必（也不能）把自己的 parent 传进来。
        /// </summary>
        public void Build()
        {
            ISleekElement parent = OverlayParent();
            if (parent == null)
            {
                CustomUIPlugin.Warn("StorageUiMenu：拿不到全屏容器（PlayerUI.container 为 null），本次不建。");
                return;
            }

            if (built && panel != null)
            {
                // ★ 幂等：建好了、也挂在对的容器上 ⟹ 什么都不做。
                //   但**容器换过实例**（换关 / 换服 —— 旧的随旧 `PlayerUI` 一起销毁）时必须重挂：
                //   否则面板挂在一个**已销毁**的容器上，之后 `Show()` 只会把它设成"可见"
                //   却永远看不见 —— 而且**不报错**（与坑 #84 同一族：静默失效）。
                if (!ReferenceEquals(panel.Parent, parent))
                {
                    // ⚠ 顺序与新建分支一致：**遮罩先、面板后**
                    //   （新 parent 上 `AddChild` 是"追加到末尾" ⟹ 反过来遮罩会盖住菜单）
                    if (scrim != null) { try { parent.AddChild(scrim); } catch (Exception) { } }
                    try { parent.AddChild(panel); } catch (Exception) { }
                }
                return;
            }

            try
            {
                if (scrim == null)
                {
                    scrim = Glazier.Get().CreateImage();
                    // ⚠ 必须显式挂贴图 —— 不挂的话 uGUI 把这一层 `enabled = false`（不渲染也不报错，坑 #84）。
                    scrim.Texture = Texture2D.whiteTexture;
                    scrim.SizeScale_X = 1f;                 // 全屏
                    scrim.SizeScale_Y = 1f;
                    scrim.TintColor = _scrimColor;
                    scrim.IsVisible = false;
                    scrim.OnClicked += OnScrimClicked;
                    parent.AddChild(scrim);                 // ★ 遮罩**先加**
                }

                if (panel == null)
                {
                    panel = Glazier.Get().CreateBox();
                    panel.SizeOffset_X = _width;
                    panel.SizeOffset_Y = DesiredHeight;
                    panel.BackgroundColor = _popupBackground;
                    panel.IsVisible = false;
                    parent.AddChild(panel);                 // ★ 菜单**后加** ⟹ 盖在遮罩之上
                }

                // 不透明实心底（绕开主题盒贴图 191/255 的天花板，坑 #92）+ 真圆角。
                if (panelSolid == null) panelSolid = new StorageUiFill();
                panelSolid.Inset = 0f;
                panelSolid.Color = _popupBackground;
                panelSolid.Build(panel);

                if (header == null)
                {
                    header = Glazier.Get().CreateLabel();
                    header.TextAlignment = TextAnchor.MiddleLeft;
                    header.TextColor = new SleekColor(ESleekTint.FONT, 1f);
                    panel.AddChild(header);
                }

                built = true;
                if (!live.Contains(this)) live.Add(this);   // ★ 登记进"每帧自检"名单（见 TickAll）
                Layout();
                RefreshItems();
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("StorageUiMenu 建立失败：" + e);
                scrim = null;
                panel = null;
                panelSolid = null;
                header = null;
                built = false;
            }
        }

        /// <summary>
        /// 在指定位置弹出（`x` / `y` 是**全屏** Glazier 坐标 —— 不是 `areaBox` 内的）。
        /// 返回 false = 没建好 / 拿不到全屏容器（见 `Build`）。
        /// </summary>
        public bool Show(float x, float y)
        {
            if (!IsBuilt) Build();
            if (!IsBuilt) return false;

            try
            {
                Layout();
                PlaceClamped(x, y);
                if (scrim != null) scrim.IsVisible = true;
                panel.IsVisible = true;
                opened = true;
                return true;
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("StorageUiMenu 弹出失败：" + e);
                return false;
            }
        }

        /// <summary>
        /// 在**鼠标处**弹出（自己按 `userInterfaceScale` 换算，并夹取到屏幕内）。
        /// ⚠ 换算依据见类头（`CanvasScaler = ConstantPixelSize`）。想让它跟着鼠标走就每帧调。
        /// </summary>
        public bool ShowAtPointer()
        {
            float x, y;
            if (!TryGetPointerPosition(out x, out y)) return false;
            return Show(x, y);
        }

        /// <summary>
        /// 算当前鼠标在**全屏 Glazier 坐标**里的位置（`ShowAtPointer` 用的就是它）。
        /// 返回 false = 拿不到缩放（此时别用指针定位）。
        /// </summary>
        public static bool TryGetPointerPosition(out float x, out float y)
        {
            x = 0f; y = 0f;
            try
            {
                float scale = GraphicsSettings.userInterfaceScale;   // 1 Glazier 单位 = 这么多屏幕像素
                if (scale <= 0f) return false;
                Vector3 m = Input.mousePosition;                     // 屏幕像素，**左下**为原点
                x = m.x / scale;
                y = (Screen.height - m.y) / scale;                   // Glazier 的 y 是**从上往下**
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>收起（**只藏不拆** —— 菜单与遮罩一起藏）。</summary>
        public void Hide()
        {
            opened = false;
            try { if (panel != null) panel.IsVisible = false; } catch (Exception) { }
            try { if (scrim != null) scrim.IsVisible = false; } catch (Exception) { }
        }

        // ================= 自动收起（每帧自检）=================

        /// <summary>所有"活着"的菜单（`Build` 登记 / `Discard` 注销）—— `TickAll` 遍历它。</summary>
        private static readonly System.Collections.Generic.List<StorageUiMenu> live =
            new System.Collections.Generic.List<StorageUiMenu>();

        /// <summary>
        /// 每帧自检每条菜单该不该自动收起 —— **插件（`CustomUIPlugin.Update`）替所有菜单调它**，
        /// 调用方**不用**手动调。收起条件共四条，见 <see cref="Tick"/>。
        /// </summary>
        internal static void TickAll()
        {
            for (int i = live.Count - 1; i >= 0; i--)
            {
                try { live[i].Tick(); }
                catch (Exception e) { CustomUIPlugin.Error("StorageUiMenu 自动收起检查异常：" + e); }
            }
        }

        /// <summary>
        /// 检查"现在是不是该收起了"。⚠ 由 <see cref="TickAll"/> 每帧代调，调用方一般不用管。
        ///
        /// 四条条件（任一成立 ⟹ `Hide()`）：
        /// ① **画布没了 / 容器换了实例** —— 回主菜单 · 换关 · 换服（**硬条件，无旋钮**）；
        /// ② **游戏窗口失焦**（Alt+Tab 切到别的窗口）—— `HideWhenUnfocused`；
        /// ③ **宿主界面关了 / 被收走**（Esc · 自动暂停 · 玩家走开）—— `HideWithHost`；
        /// ④ **调用方自己的判据**返回 `false` —— `KeepOpen`。
        ///
        /// ★ 为什么 ① 不能省：面板挂的是**我们记下的那个容器实例**；换关后旧容器连同面板一起
        ///   被销毁，而我们的字段还指着它 —— 只判 `panel != null` 是**看不出来**的（铁律 #31 同族）。
        /// </summary>
        public void Tick()
        {
            if (!opened) return;

            // ① 画布没了 / 容器换了实例（引擎级硬条件）
            ISleekElement parent = OverlayParent();
            if (parent == null || panel == null || !ReferenceEquals(panel.Parent, parent))
            {
                Hide();
                return;
            }

            // ② 窗口失焦（Alt+Tab）
            //   ⚠ 用"闩"：只在**见过一次焦点为真**之后才认失焦 ——
            //     否则某些环境（或编辑器/无焦点启动）全程报 false，菜单一弹出就自己没了。
            if (_hideWhenUnfocused)
            {
                if (Application.isFocused) _sawFocus = true;
                else if (_sawFocus) { Hide(); return; }
            }

            // ③ 宿主界面关了 / 被收走
            if (_hideWithHost && !StorageUiPanels.IsOpen) { Hide(); return; }

            // ④ 调用方自己的判据（异常按"收起"处理 —— 判据坏了就别把浮层留在屏幕上）
            if (KeepOpen != null)
            {
                bool keep;
                try { keep = KeepOpen(); }
                catch (Exception) { keep = false; }
                if (!keep) { Hide(); return; }
            }
        }

        /// <summary>真丢弃（换画布 / 明确不要了）。下次 `Build` 重头来。</summary>
        public void Discard()
        {
            live.Remove(this);     // ★ 注销"每帧自检"名单（不注销 = 每帧白跑 + 半成品留屏）
            try { if (scrim != null && scrim.Parent != null) scrim.Parent.RemoveChild(scrim); } catch (Exception) { }
            try { if (panel != null && panel.Parent != null) panel.Parent.RemoveChild(panel); } catch (Exception) { }
            scrim = null;
            panel = null;
            panelSolid = null;
            header = null;
            items = null;          // 控件池随面板一起销毁了
            built = false;
            opened = false;
            warnedCallback = false;
        }

        // ================= 内部 =================

        /// <summary>
        /// 点遮罩 = 点到了菜单外面 ⟹ 收起（照原版 `onClickedOutsideSelection` 的语义）。
        /// ⚠ 签名**无参**：`ISleekImage.OnClicked` 是 `System.Action`（`SleekImageTexture.cs:53`），
        ///   而 `ISleekButton.OnClicked` 是带 `ISleekElement` 的那个 —— 两者**不一样**，别照抄。
        /// </summary>
        private void OnScrimClicked()
        {
            Hide();
        }

        /// <summary>摆位 + 夹取到屏幕内（否则贴边弹出会有一半在屏幕外）。</summary>
        private void PlaceClamped(float x, float y)
        {
            if (panel == null) return;
            float w = _width;
            float h = DesiredHeight;
            try
            {
                float scale = GraphicsSettings.userInterfaceScale;
                if (scale > 0f)
                {
                    float screenW = Screen.width / scale;
                    float screenH = Screen.height / scale;
                    if (x + w > screenW) x = screenW - w;
                    if (y + h > screenH) y = screenH - h;
                }
            }
            catch (Exception) { }
            if (x < 0f) x = 0f;
            if (y < 0f) y = 0f;

            panel.PositionScale_X = 0f;
            panel.PositionScale_Y = 0f;
            panel.PositionOffset_X = x;
            panel.PositionOffset_Y = y;
        }

        /// <summary>几何每轮重算（指纹只描述内容不描述版面 —— 同铁律 #17）。</summary>
        private void Layout()
        {
            if (panel == null) return;
            try
            {
                panel.SizeOffset_X = _width;
                panel.SizeOffset_Y = DesiredHeight;

                float y = _padding;
                bool hasTitle = !string.IsNullOrEmpty(_title);

                if (header != null)
                {
                    header.IsVisible = hasTitle;
                    if (hasTitle)
                    {
                        header.PositionScale_X = 0f;
                        header.PositionScale_Y = 0f;
                        header.PositionOffset_X = _padding;
                        header.PositionOffset_Y = y;
                        header.SizeScale_X = 1f;
                        header.SizeOffset_X = -_padding * 2f;
                        header.SizeOffset_Y = _headerHeight;
                        header.Text = _title;
                        header.FontSize = _fontSize;
                        y += _headerHeight;
                    }
                }

                if (items != null)
                {
                    for (int i = 0; i < items.Length; i++)
                    {
                        ISleekButton b = items[i];
                        if (b == null) continue;
                        bool usedItem = i < used;
                        b.IsVisible = usedItem;
                        if (!usedItem) continue;
                        b.PositionScale_X = 0f;
                        b.PositionScale_Y = 0f;
                        b.PositionOffset_X = _padding;
                        b.PositionOffset_Y = y;
                        b.SizeScale_X = 1f;
                        b.SizeOffset_X = -_padding * 2f;
                        b.SizeOffset_Y = _itemHeight;
                        y += _itemHeight;
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>把当前 `keys` / `labels` / `enabledMask` 刷到按钮上（按钮池只增不减）。</summary>
        private void RefreshItems()
        {
            if (!built || panel == null) return;

            // 池子按需长大（**只增不减** —— 与另几个组件同一条）。
            if (items == null || items.Length < used)
            {
                int want = used < 4 ? 4 : used;
                ISleekButton[] grown = new ISleekButton[want];
                for (int i = 0; i < want; i++)
                {
                    if (items != null && i < items.Length) { grown[i] = items[i]; continue; }
                    ISleekButton b = Glazier.Get().CreateButton();
                    b.FontSize = _fontSize;
                    b.TextAlignment = _textAlignment;
                    b.BackgroundColor = _itemBackground;
                    int captured = i;                        // ⚠ 只捕获下标（行回调读当前字段，不闭包捕获数据）
                    b.OnClicked += delegate (ISleekElement e) { Fire(captured); };
                    panel.AddChild(b);
                    grown[i] = b;
                }
                items = grown;
            }

            for (int i = 0; i < items.Length; i++)
            {
                ISleekButton b = items[i];
                if (b == null) continue;
                if (i >= used)
                {
                    b.IsVisible = false;
                    continue;
                }
                bool on = (enabledMask == null) || enabledMask[i];
                b.Text = labels[i];
                b.FontSize = _fontSize;
                b.TextAlignment = _textAlignment;
                b.BackgroundColor = _itemBackground;
                b.TextColor = on ? _itemTextColor : _disabledTextColor;
                b.IsClickable = on;                          // ★ 禁用项**点不动**（不只是变灰）
            }

            Layout();
        }

        /// <summary>
        /// 选项被点：**只有可点的项**会回调；回调异常隔离（一个插件的回调炸了不该把菜单卡死）。
        /// </summary>
        private void Fire(int index)
        {
            if (index < 0 || index >= used) return;
            if (enabledMask != null && !enabledMask[index]) return;

            Hide();      // 先收起（照原版"点选项即收起"）
            if (OnItemSelected == null) return;
            try
            {
                OnItemSelected(index, keys[index]);
            }
            catch (Exception e)
            {
                if (!warnedCallback)
                {
                    warnedCallback = true;
                    CustomUIPlugin.Error("StorageUiMenu 的 OnItemSelected 抛异常（只报一次）：" + e);
                }
            }
        }
    }
}
