// ============================================================================
//  CustomUI · 通用「填充层」+ 每格填充色回调（StorageUiFill，26.10.3.52）
// ============================================================================
//  给**任何**界面元素铺一层**真彩色 + 真圆角**：格子、行、列表项、整块面板都行。
//
//  ★ 为什么需要它（不是"多一个旋钮"，是"少三个坑"）：
//    引擎里"给一个元素上色"有**三条路，只有第三条既变色又保圆角**：
//
//      · `ISleekButton/ISleekBox.BackgroundColor` —— **改不了色相**。
//        盒子贴图 `DarkTheme/Box.png` 填充是**纯黑** `(0,0,0,191)`，而 uGUI `Image.color`
//        是**乘法** ⟹ 黑 × 任何颜色 = 黑，**只有 alpha 有效**（坑 #81）。
//
//      · `ISleekImage`（`CreateImage()`）+ 白贴图 + `TintColor` —— **能变色，但只有直角**。
//        它是 `RawImage`（`GlazierImage_uGUI.cs:199` `AddComponent<RawImage>()`）：
//        **整张贴图拉伸、没有 9 宫格** ⟹ 1×1 白像素就是一个纯直角方块。
//        ⚠ 那条路还有一个**静默失效点**：`SynchronizeColors`（`:227-247`）判的是
//        `desiredTexture`，"不设 Texture"时它把这一层 `enabled = false`
//        （颜色无效、`IsVisible` 也没用，**还不报错**）—— 2026-10-10 撞到过。
//
//      · ★ `ISleekSprite`（`CreateSprite()`）+ `ESleekSpriteType.Sliced` + **白色圆角贴图**
//        + `TintColor` —— **本组件走的就是这条**：真彩色 + 真圆角 + 任意尺寸不变形。
//        它是 `Image`（`GlazierSprite_uGUI.cs:111`），`Sliced` 就是 uGUI 的 9 宫格：
//        四角按贴图的 `border` 固定、**只有中间那块被拉伸** ⟹ 一行 400px 宽也不会
//        把圆角拉成椭圆。原版所有圆角盒子（按钮 / 滑块 / 输入框）都这么画。
//
//    ★ 那张白色圆角贴图由 `StorageUiShapes`（26.10.3.64 起）**在运行时生成**，形状逐项照主题 `Box.png` 反推：
//      · 20×20、9 宫格边距 9（`Box.png.meta: spriteBorder {x:9,y:9,z:9,w:9}`）；
//      · **可见形状 18×18**（四周各 1px 透明）—— `Box.png` 实测可见包围盒就是 x[1..18]。
//        ★ 9 宫格的边距切片**不随尺寸缩放** ⟹ 贴图里留 1px，画出来就往外 1px。
//        ⚠ 不留这 1px = 填充层每边比原版盒子**大 1 个单位**（用户 2026-10-10 实测「比原版框大一圈」）；
//      · 圆角半径 **9 = 形状半高**（`Box.png` 实测：角弧自 t=0 一路弯到 t=9 ⟹ 内切圆/胶囊形）；
//      · `pixelsPerUnit = 100`（`Box.png.meta: spritePixelsToUnits: 100` 同值）
//        ⟹ 屏幕上的圆角缩放**与原生盒子完全相同**，看着就是一套；
//      · RGB **纯白**（只让 alpha 携带圆角形状）—— `TintColor` 是乘法，不是白就调不出目标色；
//      · 用引擎自己的白像素不行：`GlazierResources.PixelTexture` 是 `internal`，插件拿不到；
//        `Texture2D.whiteTexture` 虽公开但是 1×1 ⟹ **直角**。
//
//    ★ `M = 1` / `R = 9` 不是"调出来的好看值"，而是**照 `Box.png` 反推**的：把这两个数代进
//      生成公式，逐行"最左非零像素"与原版 20 行全对，包围盒也正好 x[1..18]。
//
//    ⚠ `ISleekSprite` 的 `Image` 默认 `raycastTarget = false`（`GlazierSprite_uGUI.cs:113`）
//      ⟹ 铺在按钮上**不会抢走点击**。也**别去碰** `ISleekSprite.IsRaycastTarget` ——
//      那个属性在 uGUI 后端是 `throw new NotImplementedException()`。
//
//  ⚠ 三条使用边界：
//    1. **它是覆盖层，几何就是它的职责** —— 默认**铺满**父容器（`Inset` 可四周内缩）。
//       ★ 默认 0：内缩会在颜色外侧留一圈原版底色（用户 2026-10-10 实测「没盖全」）。
//       与另几个组件"位置由调用方摆"的规矩不冲突 —— 那几条讲的是**有内容的行**。
//    2. **alpha = 0 就是"不铺"**（`default(SleekColor)` 正好全透明；`ESleekTint.NONE` 是 0 号值，
//       `SleekColor.cs:54-55` 对 NONE 直接返回自定义色）⟹ 不需要额外的 bool 旋钮，
//       也**不许**改成可空类型（`SleekColor?` 会让外部程序集必须引用
//       `UnityEngine.UIElementsModule`，见坑 #82）。
//       ⚠ 但 alpha 别给太低：它叠在**深色**盒子上，0.3 会发脏（探针演示色取 0.55 以上）。
//    3. **它盖在宿主盒子的背景之上** ⟹ 父容器若是按钮，**悬停高亮边框会被它挡住**
//       （想保留悬停反馈，就把 `Inset` 调大几像素让外圈露出来）。
//
//  ★ 生命周期与另六个公开组件一致：**关界面用 `Hide()`（只藏不拆）**，
//    明确不要了才 `Discard()`。`Build()` 同时是"恢复显示"的入口。
// ============================================================================
using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace CustomUI
{
    /// <summary>
    /// 「这一格该铺什么颜色」的回调（三个内置视图的 `CellFill` 用）。
    /// 返回 **alpha 0** 就是"这一格不铺"；返回什么由插件自己的规则定
    /// （稀有度 / 耐久告警 / 选中态 / 搜索结果 …）。
    /// ⚠ 它是**每轮刷新都对每个可见格调一次**的纯函数：别在里面改数据、别标脏
    /// （与 `SetItem` 的"拉"同一约定）；抛异常会被吞掉并只报一次，界面不崩。
    /// </summary>
    public delegate SleekColor StorageCellFillHandler(StorageSlotHit hit);

    /// <summary>
    /// 「这一**行配方**该铺什么颜色」的回调（蓝图视图的 `CellFill` 用）。
    /// 与 <see cref="StorageCellFillHandler"/> 是同一条约定（返回 alpha 0 = 不铺、每轮一次的纯函数），
    /// 只是**行里装的东西不同**：蓝图行装的是**配方**，所以给的是配方与"材料够不够"，
    /// 而不是物品的 id/耐久 —— 于是"材料齐 = 一层绿色调、缺料 = 一层红色调"可以一眼看出来
    /// （此前只能用 `BackgroundColor` 的深浅，改不了色相）。
    /// ⚠ 兜底判定按 **storage 之外（身上）** 的库存算，与"能不能点合成"那句提示同源
    ///   （`StorageCrafting` 的口径，见蓝图视图头部的说明）。
    /// </summary>
    public delegate SleekColor StorageBlueprintFillHandler(Blueprint blueprint, bool craftable);

    /// <summary>
    /// **通用填充层**（`26.10.3.52`）：给任意元素铺一层**真彩色 + 圆角**的覆盖层。
    ///
    /// 典型用法（给一个格子 / 一行 / 一整块面板上色）：
    /// <code>
    /// fill = new StorageUiFill();
    /// fill.Build(cell);                        // 铺满 cell（作为它的子层，盖在它自己的背景上）
    /// fill.Color = new SleekColor(new Color(0.93f, 0.45f, 0.20f, 0.85f));   // 真彩色 + 圆角
    /// fill.Color = default(SleekColor);        // 全透明 = 不铺（不需要额外的开关）
    /// fill.Inset = 2f;                         // 可选：缩几像素，让盒子的悬停高亮边框露出来
    /// fill.Hide();                             // 临时收起（只藏不拆）
    /// </code>
    /// ⚠ **要在子内容之前 `Build`**：同一父容器里后加的元素盖在先加的上面，
    ///   所以填充层应当是该容器的**第一个子元素**，否则会盖住文字与图标。
    /// </summary>
    public class StorageUiFill
    {
        private ISleekSprite sprite;        // 填充层（上面那层）
        private ISleekSprite border;        // 边框层（下面那层；默认不铺）
        private SleekColor _color;          // 默认零值 = 全透明 = 不铺（见类头注 2）
        private SleekColor _borderColor;    // 同上：alpha 0 = 不铺
        private float _inset;
        private float _borderThickness;
        private float _radius = DEFAULT_RADIUS;
        private int appliedKey = int.MinValue;
        private int borderKey = int.MinValue;

        /// <summary>
        /// 圆角半径的**默认值 = 9** —— 与主题 `Box.png` 的角弧同形（见 <see cref="StorageUiShapes"/>）。
        /// ⚠ **不能改成 0**：0 = 直角，会把三套内置视图的格子观感整体改掉（默认值必须保持现状）。
        /// </summary>
        private const float DEFAULT_RADIUS = 9f;

        /// <summary>
        /// 这一层（没建之前是 null）。
        /// ⚠ `26.10.3.52` 起类型是 <see cref="ISleekSprite"/>（`Sliced` 9 宫格 ⟹ 有圆角），
        ///   此前是 `ISleekImage`（`RawImage`，只能是直角方块）—— 见类头那段。
        /// </summary>
        public ISleekSprite Element { get { return sprite; } }

        /// <summary>
        /// **边框层**（没建之前是 null）—— 它在填充层**下面**，所以看得见的只有露出一圈的那部分。
        /// 想让它出现，要同时给 <see cref="BorderColor"/> 一个不透明色并设 <see cref="BorderThickness"/>&gt;0。
        /// </summary>
        public ISleekSprite BorderElement { get { return border; } }

        public bool IsBuilt { get { return sprite != null; } }

        /// <summary>
        /// 填充色。**alpha = 0 就是不铺**（`default(SleekColor)` 即可），
        /// 不需要额外的 bool 开关，也**不要**用可空类型（见类头注 2）。
        /// ⚠ 每轮重复设同一个颜色是安全的（内部按颜色键去重，不会反复写控件）。
        /// </summary>
        public SleekColor Color
        {
            get { return _color; }
            set
            {
                _color = value;
                Apply();
            }
        }

        /// <summary>四周内缩（默认 0 = 铺满父容器）。改成非 0 会让父容器的圆角轮廓露出来。</summary>
        public float Inset
        {
            get { return _inset; }
            set
            {
                float v = value < 0f ? 0f : value;
                if (_inset == v) return;
                _inset = v;
                Layout();
            }
        }

        /// <summary>
        /// 圆角半径（Glazier 单位）。默认 **9**（与主题 `Box.png` 同形 ⟹ 观感与原版一致）。
        /// 设 `0` = **直角**（走 1×1 白贴图 + `Regular`，与进度条的平角同一条路）。
        /// ★ 与 `StorageUiBar.Radius` 是**同一套形状**（都出自 `StorageUiShapes`）—— 不会两边不一样。
        /// ⚠ 9 宫格的边距切片不随尺寸缩放：半径大于父容器半高时会自动退化成胶囊。
        /// </summary>
        public float Radius
        {
            get { return _radius; }
            set
            {
                float v = value < 0f ? 0f : value;
                if (_radius == v) return;
                _radius = v;
                ApplyShape();
            }
        }

        /// <summary>
        /// **边框色**（默认零值 = 无边框）。语义与 <see cref="Color"/> 一样：**alpha = 0 就是不铺**。
        /// ⚠ 它要**真彩色**：边框层也是自绘白贴图（tint 就是目标色）——
        ///   给 `ESleekTint.BACKGROUND` 这类主题 tint 会解析成 `SleekCustomization.backgroundColor`，
        ///   在本主题里那是**白色**（坑 #93）。默认值本身用 `default` ⟹ 不会踩。
        /// 想跟主题一致又不想踩坑：`StorageUiFill.ThemedBorder()`（见下）。
        /// </summary>
        public SleekColor BorderColor
        {
            get { return _borderColor; }
            set
            {
                _borderColor = value;
                Apply();
            }
        }

        /// <summary>
        /// 边框宽度（Glazier 单位，默认 0 = 没有边框）。它把**填充层**再向内缩这么多，
        /// 露出来的那一圈就是 <see cref="BorderColor"/>。
        /// ★ **与 <see cref="Inset"/> 是两件事**：`Inset` 是整个面（含边框）相对父容器的内缩；
        ///   这个是"填充相对边框"的内缩 —— 两个会叠加。
        /// </summary>
        public float BorderThickness
        {
            get { return _borderThickness; }
            set
            {
                float v = value < 0f ? 0f : value;
                if (_borderThickness == v) return;
                _borderThickness = v;
                Layout();
                Apply();
            }
        }

        /// <summary>
        /// 便捷：**跟随主题**的边框色（**真彩色** —— 能直接喂给自绘面，坑 #93 的正解）。
        /// ★ 用主题**前景色**（`SleekCustomization.foregroundColor`，public 的**真值**）——
        ///   ⚠ 第一版走的是 `BackgroundIfLight(暗?白:黑)`，它在**浅色主题**下返回的是
        ///     `SleekCustomization.backgroundColor`（**就是背景色**）⟹ 描边与底同色 = **隐形**。
        ///   取前景色则是"与主题一致、且两个主题下都看得见"的那个色。
        /// </summary>
        public static SleekColor ThemedBorder(float alpha)
        {
            UnityEngine.Color c = SleekCustomization.foregroundColor;
            c.a = alpha;
            return new SleekColor(c);
        }

        /// <summary>
        /// 建一次并挂进 <paramref name="parent"/>（**铺满它**）。重复调用只是重新挂父容器，不重复建。
        /// ⚠ 与另几个组件的 `Build` 一样：它同时是"恢复显示"的入口 ——
        ///   宿主的区块面板只藏不拆，重新打开时不会管这一层。
        /// ⚠ 在父容器里它应该是**第一个子元素**（同一父容器下后加的盖在先加的上面）。
        /// </summary>
        public void Build(ISleekElement parent)
        {
            if (parent == null) return;

            if (sprite != null)
            {
                // ⚠ 不能先 RemoveChild（那是 InternalDestroy，会把本体销毁，紧接着挂上去的是尸体）。
                //   AddChild 自己会从旧父容器摘走。
                // ⚠ 顺序铁律：**先边框、后填充**（同一父容器里后加的盖在上面；换父容器时是"追加到末尾"
                //   ⟹ 反过来会把填充压在边框下面，整块变成边框色 —— 与右键菜单那条同源，坑 #98）。
                try { if (border != null) parent.AddChild(border); } catch (Exception) { }
                try { parent.AddChild(sprite); } catch (Exception) { return; }
                Layout();
                appliedKey = int.MinValue;      // 重挂之后按当前颜色再写一遍
                borderKey = int.MinValue;
                Apply();
                return;
            }

            // ★ 必须是 `ISleekSprite` + 9 宫格（或直角）+ 一张**白色贴图** —— 见类头那段：
            //   · `ISleekImage` 是 RawImage（整张拉伸、无 9 宫格）⟹ 只能画直角方块；
            //   · 贴图由 `StorageUiShapes` 运行时生成（默认半径 9 ⟹ 20×20 / border 9 /
            //     **可见 18×18** / 纯白），逐项照主题 `Box.png` 反推 ⟹ 圆角缩放与**可见范围**
            //     都与原生盒子完全相同。
            //   ⚠ 顺序：先挂 `Sprite` 再写颜色 —— `SynchronizeColors` 只在 `Sprite != null`
            //     时才把这一层 `enabled = true`（`GlazierSprite_uGUI.cs:118-127`）。
            try
            {
                // 边框层**先建、先挂**（它在下面）
                border = Glazier.Get().CreateSprite();
                border.PositionScale_X = 0f;
                border.PositionScale_Y = 0f;
                border.SizeScale_X = 1f;
                border.SizeScale_Y = 1f;
                border.IsVisible = false;
                parent.AddChild(border);

                sprite = Glazier.Get().CreateSprite();
                sprite.PositionScale_X = 0f;
                sprite.PositionScale_Y = 0f;
                sprite.SizeScale_X = 1f;
                sprite.SizeScale_Y = 1f;
                sprite.IsVisible = false;            // 先藏起来，Color 说了算（见 Apply）
                parent.AddChild(sprite);
            }
            catch (Exception) { sprite = null; border = null; return; }

            ApplyShape();                        // 按当前 `Radius` 给两层贴图 / 画法
            Layout();
            appliedKey = int.MinValue;
            borderKey = int.MinValue;
            Apply();
        }

        /// <summary>只藏不拆（临时收起时用）。再显示靠 `Build(parent)`。</summary>
        public void Hide()
        {
            try { if (sprite != null) sprite.IsVisible = false; } catch (Exception) { }
            try { if (border != null) border.IsVisible = false; } catch (Exception) { }
        }
        /// <summary>真拆掉（下一次 `Build` 会重建）。</summary>
        public void Discard()
        {
            try
            {
                if (sprite != null && sprite.Parent != null) sprite.Parent.RemoveChild(sprite);
            }
            catch (Exception) { }
            try
            {
                if (border != null && border.Parent != null) border.Parent.RemoveChild(border);
            }
            catch (Exception) { }

            sprite = null;
            border = null;
            appliedKey = int.MinValue;
            borderKey = int.MinValue;
        }

        // ================= 内部 =================

        /// <summary>
        /// 铺满父容器（减 <see cref="Inset"/>）—— 相对值，父容器改宽高时自动跟随。
        /// ★ 有边框时**填充层再向内缩 <see cref="BorderThickness"/>**，露出来的那一圈就是边框。
        /// </summary>
        private void Layout()
        {
            if (sprite == null) return;
            try
            {
                if (border != null)
                {
                    border.PositionOffset_X = _inset;
                    border.PositionOffset_Y = _inset;
                    border.SizeOffset_X = -2f * _inset;
                    border.SizeOffset_Y = -2f * _inset;
                }

                float fi = _inset + _borderThickness;
                sprite.PositionOffset_X = fi;
                sprite.PositionOffset_Y = fi;
                sprite.SizeOffset_X = -2f * fi;
                sprite.SizeOffset_Y = -2f * fi;
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 按当前颜色决定「铺 / 不铺」（填充层与边框层各按各的颜色）。**按颜色键去重**：
        /// 同一个颜色重复设不会反复写控件（每轮给每个格子调一次是很常见的用法）。
        /// </summary>
        private void Apply()
        {
            if (sprite != null)
            {
                Color32 c = _color.Get();           // NONE 直接返回自定义色（SleekColor.cs:54-55）
                int k = unchecked((c.r << 24) | (c.g << 16) | (c.b << 8) | c.a);
                if (k != appliedKey)
                {
                    appliedKey = k;
                    try
                    {
                        // ⚠ 只在要铺的时候写 TintColor：写它就是一次 SynchronizeColors。
                        //   alpha 0 的"不铺"用 IsVisible 收掉（uGUI 里那是 gameObject.SetActive，
                        //   隐藏的元素不产生绘制调用）。
                        bool on = c.a > 0;
                        if (on) sprite.TintColor = _color;
                        sprite.IsVisible = on;
                    }
                    catch (Exception) { }
                }
            }

            if (border != null)
            {
                Color32 b = _borderColor.Get();
                // ★ 边框"看得见"要**两个条件**：有不透明色 **且** 有宽度 ——
                //   宽度 0 时填充铺满整块，边框再亮也只会被压在下面（白费一次绘制）。
                bool on = b.a > 0 && _borderThickness > 0f;
                // ⚠⚠ 去重键**必须把 `on` 也算进去**（铁律 #36：键承载的语义不能比状态少）：
                //   键只含颜色时，「先设边框色（此时宽度还是 0 ⟹ 判成隐藏、键已记下）→ 再设宽度」
                //   这一串**键没变** ⟹ 下面直接早退，可见性**再也不会被重新评估** ⟹ 边框永远不亮。
                //   本轮实测正是这个 bug（自检 `宽3显边=False`；玩家看到的则是"有边框那块小一圈"）。
                int bk = on ? unchecked((b.r << 24) | (b.g << 16) | (b.b << 8) | b.a) : 0;
                if (bk != borderKey)
                {
                    borderKey = bk;
                    try
                    {
                        if (on) border.TintColor = _borderColor;
                        border.IsVisible = on;
                    }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// 按 <see cref="Radius"/> 给**填充层与边框层**换贴图 / 画法：
        /// 半径 0 ⟹ `Regular` + 1×1 白贴图（直角）；半径 &gt; 0 ⟹ `Sliced` + 圆角贴图（9 宫格）。
        /// ★ 贴图出自 <see cref="StorageUiShapes"/> —— 与 `StorageUiBar` **同一份形状**（不会两边不一样）。
        /// ⚠ `Sprite` 的 setter **不改** Image.type，改类型的是 `DrawMethod`（默认还是 `Tiled`）。
        /// </summary>
        private void ApplyShape()
        {
            try
            {
                int r = Mathf.RoundToInt(_radius);
                bool rounded = r >= 1;
                Sprite sp = StorageUiShapes.Rounded(rounded ? r : 0);
                ESleekSpriteType method = rounded ? ESleekSpriteType.Sliced : ESleekSpriteType.Regular;

                if (sprite != null) { sprite.Sprite = sp; sprite.DrawMethod = method; }
                if (border != null) { border.Sprite = sp; border.DrawMethod = method; }
            }
            catch (Exception) { }
        }

    }

    /// <summary>
    /// 给**内置蓝图视图**的行上色的登记处（`26.10.3.51`）。
    ///
    /// ★ 为什么是"登记"而不是 `view.CellFill = …`：内置蓝图视图的实例归 CustomUI 自己所有，
    ///   第三方**根本拿不到它** —— 给它挂一个实例属性，就是写了一个谁也调不到的公开面
    ///   （本项目反复栽过的"半截 API"）。所以这里用**登记**，形状与已有的
    ///   `StorageBlueprintFilters.Add(source, …)` 完全一致：按来源登记、多个插件互不干扰、
    ///   用同一个来源串摘掉自己那条。
    ///
    /// <code>
    /// StorageBlueprintFills.Add("mymod", delegate (Blueprint bp, bool craftable)
    /// {
    ///     return craftable ? new SleekColor(new Color(0.2f, 0.7f, 0.3f, 0.35f))
    ///                      : new SleekColor(new Color(0.8f, 0.2f, 0.15f, 0.35f));
    /// });
    /// StorageBlueprintFills.Remove("mymod");     // 不想要了摘掉自己那条
    /// </code>
    ///
    /// ⚠ 同一条规则：
    ///   · 返回 alpha 0 = 这一行不铺；一行由**第一个返回非透明色的来源**决定（顺序 = 登记顺序）。
    ///   · 同一个来源重复 `Add` 是**替换**，不是叠加。
    ///   · 每轮刷新调一次（每个可见行），是**纯函数**：别在里面改数据 / 标脏。
    /// </summary>
    public static class StorageBlueprintFills
    {
        private class Entry
        {
            public string source;
            public StorageBlueprintFillHandler handler;
        }

        private static readonly List<Entry> entries = new List<Entry>();

        /// <summary>`Resolve` 抛异常"只报一次"的闸门 —— 每次登记 / 摘除都复位一次。</summary>
        private static bool warnedThrow;

        /// <summary>
        /// 登记（或**替换**）一个来源的填充规则。<paramref name="source"/> 用你自己的标识
        /// （惯例：插件 GUID）。<paramref name="handler"/> 传 null 等于摘除这个来源。
        /// </summary>
        public static void Add(string source, StorageBlueprintFillHandler handler)
        {
            if (string.IsNullOrEmpty(source)) return;

            warnedThrow = false;        // 配置变了 ⟹ 允许再报一次异常（不然第二次完全无声）

            if (handler == null)
            {
                Remove(source);
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].source == source)
                {
                    entries[i].handler = handler;
                    return;
                }
            }

            Entry e = new Entry();
            e.source = source;
            e.handler = handler;
            entries.Add(e);
        }

        /// <summary>按来源摘除（**只摘自己那条** —— 拿别人的来源串会摘掉别人的）。</summary>
        public static bool Remove(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;

            warnedThrow = false;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].source != source) continue;
                entries.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>当前登记了几个来源（诊断用）。</summary>
        public static int Count { get { return entries.Count; } }

        /// <summary>当前登记了哪些来源（诊断用）。★ 返回**副本** —— 外部改它不影响内部。</summary>
        public static string[] Sources()
        {
            string[] a = new string[entries.Count];
            for (int i = 0; i < entries.Count; i++) a[i] = entries[i].source;
            return a;
        }

        /// <summary>
        /// 复位"只报一次"的闸门 —— 蓝图视图**关界面**时调（一次会话结束）。
        /// 目的与另两个视图的 `ResetClickWarnings` 一样：置位后永不复位 = 第二次遇到完全无声。
        /// </summary>
        internal static void ResetWarnings()
        {
            warnedThrow = false;
        }

        /// <summary>
        /// 问一遍所有来源，返回**第一个非透明色**（全都不管这一行 → 返回全透明 = 不铺）。
        /// 异常隔离 + 只报一次都在这儿（与另两个视图的 `CellFill` 同一套语义）。
        /// </summary>
        internal static SleekColor Resolve(Blueprint blueprint, bool craftable)
        {
            if (blueprint == null) return default(SleekColor);

            for (int i = 0; i < entries.Count; i++)
            {
                // 异常隔离 + 只报一次走共享层那一份（与另两个视图的 `CellFill` 同一个实现）
                SleekColor c = StorageUiShared.ResolveCellFill(entries[i].handler, blueprint,
                    craftable, "蓝图填充来源 " + entries[i].source, ref warnedThrow);
                if (c.Get().a > 0f) return c;
            }

            return default(SleekColor);
        }
    }
}
