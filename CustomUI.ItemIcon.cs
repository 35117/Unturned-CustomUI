// ============================================================================
//  CustomUI · 可复用的「物品图标格」（StorageItemIcon，26.10.3.50）
// ============================================================================
//  给**第三方插件自己的界面**用：一格"显示某件物品"的东西 —— 图标 + 右下角数量/耐久角标
//  + 悬浮提示，尺寸是固定像素（图标本来就不该随容器宽度变）。
//
//  ★ 它解决的问题：此前第三方想"显示一件物品"，只能自己 `new SleekItemIcon()` 并自己处理
//    三件**很容易错、又不容易被发现**的事（三条都回源码核过，不是印象）：
//      1. **贴图是异步来的** —— `SleekItemIcon.Refresh` 只是把请求排进 `ItemTool` 的队列
//         （`ItemTool.cs:566-631`），贴图要等回调才换上；同一帧读不出任何东西。
//      2. **每帧都 `Refresh` 会把渲染队列灌爆** —— 带 `state` 的物品**不满足缓存判据**
//         （`isEligibleForCaching = state.Length == 0 && …`，`ItemTool.cs:583`）
//         ⟹ 每帧一次新请求 = 每帧一次离线渲染。**必须只在"图标该变"时才请求**。
//      3. **数量/耐久的口径与内置视图必须同源** —— 否则同一个箱子在两个界面里
//         显示的件数/单位不一样（弹匣 `25发` vs `×25` 那类）。本组件直接调
//         `StorageUiShared` 的同一份实现（`BuildCountText` / `BuildAmountText` / `BuildTooltip`）。
//
//  ★ 两条已核实的引擎事实（更正了本仓库一条旧结论，见 README §4.29）：
//    · **一个 `SleekItemIcon` 可以反复 `Refresh`**：原版物品格自己就是这么干的
//      （`SleekItem.updateItem` → `icon.Refresh(...)`，`SleekItem.cs:156`），
//      而 `SleekItemIcon.OnIconReady` 用 `expectedHandle` 把过期回调滤掉了
//      （`SleekItemIcon.cs:91-97`）⟹ **不需要"整格重建"来换图标**。
//    · **id 变了先 `Clear()`**：原版在物品 ID 变化时会先 `icon.Clear()`
//      （`SleekItem.cs:113-117`，注释原话 "clear the icon to avoid a confusing frame"）
//      ⟹ 避免闪一帧旧图标。本组件照做。
//
//  ★ 图标键**不含 `item.state`**：与内置三个视图的指纹口径一致（坑 #17 ——
//    state 每帧可能变，混进键里 = 每帧重建 = 异步贴图永远来不及显示）。
//    键 = `id + 耐久`；数量只影响角标文字、不影响贴图。
//
//  ★ 第三条引擎事实（2026-10-09 用**贴图像素**证实，见坑 #81）——
//    **按钮的 `BackgroundColor` 改不了框底的颜色**：
//      · `DarkTheme/Box.png` 的填充是**纯黑** `(0,0,0,191)`；
//      · `Box_Highlighted.png` 只有**边角是白的** `(255,255,255,67)`；
//      · uGUI `Image.color` 是**乘法** ⟹ 黑 × 色 = 黑 ⟹ **色相被吃掉，只有 alpha 有效**，
//        色相实际只作用在「悬停 / 按下的高亮边框」上。
//    ⟹ 要真的给格子铺一层颜色，只能**另铺一层**：`ISleekImage` + **白贴图** + `TintColor`
//      —— 原版进度条 / 命中标记 / 取色器 / `PlayerUI.colorOverlayImage` 都是这么画的。
//      这就是本组件的 `FillColor`，实现抽在通用的 `StorageUiFill` 里（一份实现，视图也用它）。
//
//  ★★ 第四条引擎事实（2026-10-10 补，`26.10.3.51`）——
//    那一层**必须自己指定一张白贴图**，光"不设 Texture"是**永远画不出来**的：
//      uGUI 后端 `GlazierImage_uGUI.ConstructNew` 确实把 `rawImageComponent.texture` 填成了
//      内置白像素（`:202`，为了绕开一个 uGUI bug），但 C# 侧字段 `desiredTexture` 仍是 **null**；
//      而 `SynchronizeColors`（`:227-247`）判的是 `desiredTexture` —— 为 null 时它走 else 分支
//      **把自己 `enabled = false`**（或涂成全透明黑），于是 `TintColor` 完全无效、也不报错。
//      ⚠ 上一版这里就写错了（注释说"不设就用引擎内置白像素"），用户实测「填充换色不过」才发现；
//      引擎自己那张白贴图是 `internal`（`GlazierResources`），插件拿不到 —— 用 `Texture2D.whiteTexture`。
//
//  ⚠ 生命周期与另五个公开组件一致：**关界面用 `Hide()`（只藏不拆）**，
//    明确不要了才 `Discard()`。`Build()` 同时是"恢复显示"的入口。
// ============================================================================
using System;
using SDG.Unturned;
using UnityEngine;

namespace CustomUI
{
    /// <summary>
    /// **可复用的「物品图标格」**（`26.10.3.50`）。
    ///
    /// 典型用法（第三方画自己的格子 / 列表行 / 配方条）：
    /// <code>
    /// cell = new StorageItemIcon();
    /// cell.Size = 64;                 // 方框边长（像素，默认 64）
    /// cell.TooltipHint = "左键取出";   // 追加到 tooltip 的自定义说明（可选）
    /// cell.FillColor = new SleekColor(new Color(0.93f, 0.45f, 0.20f, 0.85f));  // 选中/强调用的填充色（可选）
    /// cell.Build(myContainer);
    /// cell.Element.PositionOffset_X = 8f;    // 位置由**调用方**摆（组件只管它自己这一格）
    /// cell.Element.PositionOffset_Y = 30f;
    /// cell.Element.OnClicked += delegate (ISleekElement el) { Take(); };
    ///
    /// cell.SetItem(jar);              // 每轮把当前那件交给它（没变就一个控件都不写）
    /// // 关界面：cell.Hide()；明确不要了：cell.Discard()；空着但仍占位：cell.Clear()
    /// </code>
    ///
    /// **五条语义边界**：
    /// 1. **它不筛、不动数据** —— 只把"这一件长什么样"画出来；点击事件由调用方自己挂。
    /// 2. **每轮 `SetItem` 是"拉"，不是"推"** —— 组件自己比对（图标键 = id+耐久；
    ///    角标键 = id+数量+耐久），没变就一个控件都不写 ⟹ 可以放心每帧调用。
    /// 3. **数量与耐久的口径来自 `StorageUiShared`**（与三个内置视图**同一份实现**），
    ///    不在这里另写一套 —— 同一个箱子在两个界面里显示的数必须一样。
    /// 4. **`Size` 是固定像素**（图标本来就不随容器宽度变）—— 与"宽度一律用相对值"那条铁律
    ///    不冲突：那条针对的是**满宽的行**。改 `Size` 会重新请求一次贴图（贴图按尺寸渲染）。
    /// 5. **`BackgroundColor` 只管深浅、`FillColor` 才是真彩色** —— 盒子贴图填充是纯黑，
    ///    色相被乘法吃掉（见 `BackgroundColor` / `FillColor` 的注释）；不设 `FillColor` 时
    ///    这一格的外观与"没有这个功能"**逐像素一致**。
    /// </summary>
    public class StorageItemIcon
    {
        /// <summary>图标与方框边缘的距离（四边一样）。⚠ 这只是**图标**的边距，与填充层无关。</summary>
        private const float ICON_INSET = 4f;

        /// <summary>
        /// 填充层的内缩。★ 必须是 **0（铺满整格）**：填充层自己带圆角（`ISleekSprite` + 9 宫格，
        /// 见 `StorageUiFill`），铺满才与盒子对齐；一旦内缩，颜色外侧就露出一圈原版底色
        /// （用户 2026-10-10 实测「没盖全」）。⚠ 别和上面的 `ICON_INSET` 搞混。
        /// </summary>
        private const float FILL_INSET = 0f;

        /// <summary>角标行高。⚠ **必须 ≥ 24** —— 小号字体的标签矮于 24 时一个字都不渲染（坑 #21）。</summary>
        private const float BADGE_H = 24f;

        /// <summary>方框最小边长（小于它会被夹到这里 —— 再小就没有可读性了）。</summary>
        private const int MIN_SIZE = 16;

        // ================= 旋钮（Build 之后改也会当场写到控件上）=================

        private int _size = 64;
        private bool _showBadge = true;
        private SleekColor _background = new SleekColor(ESleekTint.BACKGROUND, 0.55f);
        private string _tooltipHint = "";

        /// <summary>
        /// 方框边长（像素，默认 64）。图标按它渲染，**最小 16**。
        /// 改了会：① 重排控件；② 让图标键作废 ⟹ 重新请求一次贴图（贴图是按尺寸渲染的）。
        /// </summary>
        public int Size
        {
            get { return _size; }
            set
            {
                int v = value < MIN_SIZE ? MIN_SIZE : value;
                if (_size == v) return;
                _size = v;
                iconKey = int.MinValue;     // 贴图是按尺寸渲染的 ⟹ 尺寸变了必须重新请求
                Layout();
                ApplyIcon();
            }
        }

        /// <summary>要不要右下角那个「数量 / 耐久」角标（默认要）。</summary>
        public bool ShowBadge
        {
            get { return _showBadge; }
            set
            {
                if (_showBadge == value) return;
                _showBadge = value;
                textKey = int.MinValue;     // 旋钮变了 ⟹ 角标键作废（否则「关掉」会被早退吃掉）
                ApplyBadge();
            }
        }

        /// <summary>
        /// 盒子底色（默认与内置大格子一致：`BACKGROUND` 55%）。
        ///
        /// ⚠ **它只能改"深浅"，改不了色相**（2026-10-09 用贴图像素核实的引擎事实）：
        ///   盒子贴图 `DarkTheme/Box.png` 的填充是**纯黑** `(0,0,0,191)`，而 uGUI `Image.color`
        ///   是**乘法** —— 黑 × 任何颜色 = 黑 ⟹ 色相被吃掉，**只有 alpha 有效**；
        ///   色相实际只落在「悬停 / 按下的高亮边框」上（`Box_Highlighted.png` 只有**边角是白的**，
        ///   `(255,255,255,67)`）。**想要真正给格子铺一层颜色，请用 <see cref="FillColor"/>。**
        /// </summary>
        public SleekColor BackgroundColor
        {
            get { return _background; }
            set
            {
                _background = value;
                if (root != null) { try { root.BackgroundColor = _background; } catch (Exception) { } }
            }
        }

        /// <summary>
        /// **填充色**（默认**全透明** = 不画填充层，外观与"没这个功能"**逐像素一致**）。
        /// 设成 alpha > 0 的颜色就铺上；想关掉设回 `default(SleekColor)`（或任何 alpha 0 的颜色）。
        ///
        /// ★ 为什么需要它：见 <see cref="BackgroundColor"/> —— 按钮自带的盒子改不了框底色相，
        ///   所以"给这一格铺个颜色"（选中 / 高亮 / 稀有度）只能另铺一层。
        ///   实现走的是**原版画彩色块的那条路**：`ISleekImage` + **内置白像素贴图**
        ///   （`GlazierResources.PixelTexture`，源码注释原话 "White 1x1 texture for solid colored images"）
        ///   + `TintColor` —— 原版 `PlayerUI.colorOverlayImage` / 命中标记 / 进度条都是这么画的。
        ///
        /// ⚠ 三条边界：
        ///   1. 填充层铺在**图标区**（内缩 `ICON_INSET`）—— 外圈那几像素留给宿主盒子的**圆角轮廓**，
        ///      这样格子仍然是圆角的。
        ///   2. ⟹ **悬停高亮边框会被填充层盖住**（位置重叠、填充在上层）。
        ///      要保留悬停反馈，就别给这一格上填充色；`BackgroundColor` 的深浅可以照常单独用。
        ///   3. 它是**非可空**的 `SleekColor`（用"全透明"当"不画"的哨兵）——
        ///      ⚠ 别改成 `SleekColor?`：那会要求调用方引用 `UnityEngine.UIElementsModule`
        ///      （`SleekColor` 有到 `StyleColor` 的隐式转换，可空类型会触发"提升转换"查找）
        ///      ⟹ **外部程序集编译不过**，而本插件自己编译得过（本插件引用了那个程序集）——
        ///      单程序集自检发现不了这类缺口（见坑 #82）。
        /// </summary>
        public SleekColor FillColor
        {
            // ★ 值只存一份：就是那一层自己（`StorageUiFill.Color`）。以前这里另存了一份
            //   `_fill`，两份真源迟早会漂（铁律 #5）。
            get { return fill.Color; }
            set { fill.Color = value; }
        }

        /// <summary>
        /// 追加到悬浮提示末尾的**自定义说明**（可选）。语义与内置视图的 `TooltipHint` 一致：
        /// 放"这个格子上有哪些按键约定"，别拿它放状态数据（坑 #71）。
        /// </summary>
        public string TooltipHint
        {
            get { return _tooltipHint; }
            set
            {
                string v = value ?? "";
                if (string.Equals(_tooltipHint, v)) return;
                _tooltipHint = v;
                ApplyTooltip();
            }
        }

        // ================= 只读状态 =================

        /// <summary>
        /// 这一格的根元素（**就是那个按钮本体**）—— 位置由调用方摆，
        /// 点击事件也由调用方自己往它身上挂（`Element.OnClicked += …`）。
        /// </summary>
        public ISleekButton Element { get { return root; } }

        public bool IsBuilt { get { return root != null; } }

        /// <summary>这一格要占的高度 = <see cref="Size"/>（方框是正方形）。</summary>
        public float DesiredHeight { get { return _size; } }

        /// <summary>当前格子里有没有东西。</summary>
        public bool HasItem { get { return asset != null; } }

        /// <summary>当前显示物品的资产（空的返回 null）—— 供调用方核对"我设的和我看到的是不是同一件"。</summary>
        public ItemAsset CurrentAsset { get { return asset; } }

        // ================= 生命周期 =================

        /// <summary>
        /// 建一次并挂进 <paramref name="parent"/>。重复调用只是重新挂父容器（不重复建）。
        /// ⚠ 位置**不由本组件负责** —— 建完自己设 `Element.PositionOffset_*` / `PositionScale_*`。
        /// ★ 它同时是"恢复显示"的入口（宿主只恢复区块根面板，**不会**管子元素）。
        /// </summary>
        public void Build(ISleekElement parent)
        {
            if (parent == null) return;

            if (root != null)
            {
                // ⚠ 不能先 `RemoveChild`（那是 `InternalDestroy`，会把本体连同子控件一起销毁，
                //   紧接着的 `AddChild` 挂的是尸体）—— `AddChild` 自己会从旧父容器摘走。
                try { parent.AddChild(root); } catch (Exception) { return; }
                try { root.IsVisible = true; } catch (Exception) { }
                Layout();
                return;
            }

            root = Glazier.Get().CreateButton();
            root.PositionScale_X = 0f;
            root.PositionOffset_X = 0f;
            root.PositionOffset_Y = 0f;
            root.SizeScale_X = 0f;
            root.SizeOffset_X = _size;
            root.SizeOffset_Y = _size;
            root.Text = "";                 // 文字用独立标签，别让按钮的文字盖住图标
            root.BackgroundColor = _background;
            try { parent.AddChild(root); }
            catch (Exception) { root = null; return; }

            // ★ 填充层必须建在**图标之前**（同一父容器里后加的元素盖在先加的上面）。
            //   ⚠ 它**铺满整格**（`FILL_INSET = 0`）：填充层自带圆角，铺满才与盒子对齐；
            //     内缩会露出一圈原版底色（用户实测「没盖全」）。
            //     与图标的 `ICON_INSET` 是两件事 —— 那个只管图标留多少边。
            //   ⚠ 白色圆角贴图由组件自己在运行时生成（`StorageUiFill.RoundedBox()`）。
            fill.Inset = FILL_INSET;
            fill.Build(root);

            icon = new SleekItemIcon();
            icon.isAngled = true;           // 与原版物品格一致
            try { root.AddChild(icon); } catch (Exception) { icon = null; }

            badge = Glazier.Get().CreateLabel();
            badge.PositionOffset_X = 2f;
            badge.PositionScale_Y = 1f;
            badge.PositionOffset_Y = -(BADGE_H + 1f);
            badge.SizeScale_X = 1f;
            badge.SizeOffset_X = -4f;
            badge.SizeOffset_Y = BADGE_H;
            badge.FontSize = ESleekFontSize.Small;
            badge.TextAlignment = TextAnchor.MiddleRight;
            badge.TextColor = new SleekColor(Color.white);
            // 压在图标上要有底衬，否则浅色贴图上看不清（原版物品格也是这么做的）
            badge.TextContrastContext = ETextContrastContext.InconspicuousBackdrop;
            badge.Text = "";
            badge.IsVisible = false;
            try { root.AddChild(badge); } catch (Exception) { badge = null; }

            // ⚠ 建控件之前可能已经 SetItem / FillColor 过了 ⟹ 两个键都作废，下面按当前快照重画一遍
            //   （填充色不用管：`fill.Build` 之后已经按当前值铺好了）
            iconKey = int.MinValue;
            textKey = int.MinValue;
            Layout();
            ApplyIcon();
            ApplyTooltip();
            ApplyBadge();
        }

        /// <summary>只藏不拆（临时收起时用）。再显示靠调用方重新 <see cref="Build"/>。</summary>
        public void Hide()
        {
            try { if (root != null) root.IsVisible = false; } catch (Exception) { }
        }

        /// <summary>真拆掉（下一次 <see cref="Build"/> 会重建）。</summary>
        public void Discard()
        {
            try
            {
                if (root != null && root.Parent != null) root.Parent.RemoveChild(root);
            }
            catch (Exception) { }

            root = null;
            icon = null;
            badge = null;
            fill.Discard();
            iconKey = int.MinValue;
            textKey = int.MinValue;
        }

        // ================= 数据入口 =================

        /// <summary>
        /// 显示容器里的这一件（图标 + 数量/耐久 + 提示）。
        /// ⚠ 每轮都可以调：**没变就一个控件都不写**（键相同直接返回）。
        /// </summary>
        public void SetItem(ItemJar jar)
        {
            if (jar == null || jar.item == null) { Clear(); return; }
            SetCore(jar.item, jar.GetAsset(), jar);
        }

        /// <summary>
        /// 只按**资产**显示外观（例如配方材料、图鉴）。
        /// 数量按该资产的最大堆叠数、耐久按 100 —— 也就是"崭新的一件"。
        /// </summary>
        public void SetAsset(ItemAsset a)
        {
            if (a == null) { Clear(); return; }
            // `new Item(id, true)` 只是填字段、不注册任何东西（`Item.cs:86-111` 已核）；
            // 包成 ItemJar 后才能复用 Shared 那份"数量 / 耐久 / Tooltip"口径。
            // ⚠ 只建**一个** Item 实例：再建第二个会让内容物各持一份 state。
            Item it = new Item(a.id, true);
            SetCore(it, a, new ItemJar(it));
        }

        /// <summary>清空这一格（元素留着，只是没东西了）；<see cref="HasItem"/> 变 false。</summary>
        public void Clear()
        {
            asset = null;
            item = null;
            wrap = null;
            iconKey = int.MinValue;
            textKey = int.MinValue;
            if (icon != null)
            {
                try { icon.Clear(); } catch (Exception) { }
            }
            ApplyIcon();
            ApplyTooltip();
            ApplyBadge();
        }

        // ================= 内部 =================

        private ISleekButton root;
        /// <summary>填充层（通用组件；在图标之下、盒子之上）。⚠ 值只此一份，见 `FillColor`。</summary>
        private readonly StorageUiFill fill = new StorageUiFill();
        private SleekItemIcon icon;
        private ISleekLabel badge;

        private ItemAsset asset;      // 当前显示的资产（null = 空的）
        private Item item;            // id / 耐久 / state 的真源
        private ItemJar wrap;         // Tooltip 与角标的「件数」口径要用它
        private ushort lastId;        // 上一次请求图标时的物品 ID（用来判"id 变了"）

        private int iconKey = int.MinValue;   // id + 耐久
        private int textKey = int.MinValue;   // 隐藏哨兵 / id + 数量 + 耐久

        /// <summary>复用同一个组对象（只为给 Shared 的文案函数传参，不参与合并逻辑）。</summary>
        private readonly StorageUiShared.ItemGroup group = new StorageUiShared.ItemGroup();

        /// <summary>唯一的数据入口：三个公开入口都汇到这里（判据/写法只有一份）。</summary>
        private void SetCore(Item newItem, ItemAsset newAsset, ItemJar newWrap)
        {
            item = newItem;
            asset = newAsset;
            wrap = newWrap;
            Layout();
            ApplyIcon();
            ApplyTooltip();
            ApplyBadge();
        }

        /// <summary>
        /// 几何：方框 + 图标（都按 <see cref="Size"/> 算）。
        /// ★ 填充层不在这里 —— 它铺满方框（<c>FILL_INSET = 0</c>），由 `StorageUiFill` 自己管
        ///   （相对几何，父容器改尺寸也会跟着走；同一份算式别写两处）。
        /// </summary>
        private void Layout()
        {
            if (root == null) return;
            try
            {
                root.SizeOffset_X = _size;
                root.SizeOffset_Y = _size;
            }
            catch (Exception) { }

            float side = _size - 2f * ICON_INSET;

            if (icon != null)
            {
                try
                {
                    icon.PositionOffset_X = ICON_INSET;
                    icon.PositionOffset_Y = ICON_INSET;
                    icon.SizeOffset_X = side;
                    icon.SizeOffset_Y = side;
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// 图标：**只在图标键（id + 耐久）真的变了时**才请求一次贴图。
        /// ⚠ 这一条就是本组件存在的理由 —— 每帧 `Refresh` 会为带 `state` 的物品
        ///   （不满足 `ItemTool` 的缓存判据）每帧排一次离线渲染。
        /// </summary>
        private void ApplyIcon()
        {
            if (root == null || icon == null) return;

            if (asset == null || item == null)
            {
                try { icon.Clear(); } catch (Exception) { }
                iconKey = int.MinValue;
                return;
            }

            int k = Mix(item.id, item.quality);
            if (k == iconKey) return;

            // id 变了先清一下，避免闪一帧旧图标（原版 `SleekItem.updateItem` 同一处做法）；
            // 只是耐久变了就别清 —— 清一下反而会白一帧。
            if (iconKey != int.MinValue && lastId != item.id)
            {
                try { icon.Clear(); } catch (Exception) { }
            }

            iconKey = k;
            lastId = item.id;
            try
            {
                int px = _size - (int)(2f * ICON_INSET);
                icon.Refresh(item.id, item.quality, item.state, asset, px, px);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 提示：与内置视图**同一份** `BuildTooltip`（含描述 / 耐久 / 合并件数 / 格数），
        /// 拼装也走**同一份** <see cref="StorageUiTooltip.Compose"/>。
        /// </summary>
        private void ApplyTooltip()
        {
            if (root == null) return;
            try
            {
                string t = (asset == null || wrap == null)
                    ? "" : StorageUiShared.BuildTooltip(asset, MakeGroup());
                root.TooltipText = StorageUiTooltip.Compose(t, _tooltipHint);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 角标：数量/耐久文字（同一份 `BuildAmountText`）。
        /// 键 = `id + 数量 + 耐久`，**隐藏态用哨兵**（否则"把角标关掉"会被"键没变"早退吃掉）。
        /// </summary>
        private void ApplyBadge()
        {
            if (badge == null) return;

            bool show = _showBadge && asset != null && item != null && wrap != null;
            int k = show ? Mix3(item.id, item.amount, item.quality) : int.MinValue;
            if (k == textKey) return;
            textKey = k;

            try
            {
                if (!show)
                {
                    badge.Text = "";
                    badge.IsVisible = false;
                    return;
                }
                string text = StorageUiShared.BuildAmountText(asset, MakeGroup());
                badge.Text = text;
                badge.IsVisible = text.Length > 0;
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 把当前这一件包成"只有一件的合并组"—— 只为复用 `StorageUiShared` 那份文案口径，
        /// 不参与任何合并 / 排序（所以 `Count = 1`）。复用同一个对象，避免每帧分配。
        /// </summary>
        private StorageUiShared.ItemGroup MakeGroup()
        {
            group.First = wrap;
            group.Count = 1;
            group.TotalAmount = (item != null) ? item.amount : 0;
            return group;
        }

        private static int Mix(ushort id, int second)
        {
            unchecked { return id * 397 ^ second; }
        }

        private static int Mix3(ushort id, int amount, int quality)
        {
            unchecked { return (id * 397 ^ amount) * 397 ^ quality; }
        }
    }
}
