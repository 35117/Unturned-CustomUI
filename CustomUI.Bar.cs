// ============================================================================
//  CustomUI · 通用「进度条 / 状态条」（StorageUiBar，26.10.3.55）
// ============================================================================
//  给**任何界面**一根按比例填充的条：机器进度、电量、血量、经验、剩余容量…
//
//  ★ 画法（`26.10.3.55` 起**统一走 `ISleekSprite`**，为的是支持可配置圆角与颜色）：
//    · `Radius = 0`（默认）⟹ `DrawMethod = Regular` + **1×1 白贴图** ⟹ **平角**实心块，
//      与原版自己那根加载进度条（`SleekLoadingScreenProgressBar.cs`）观感一致。
//    · `Radius > 0` ⟹ `DrawMethod = Sliced`（uGUI 9 宫格）+ **运行时生成的白色圆角贴图**
//      ⟹ 真圆角、任意尺寸不变形。生成公式与填充层 `StorageUiFill.RoundedBox()`
//      **同一套**（半径 9 时两张贴图逐像素相同）。
//
//  ★ 为什么**不能**用 `ISleekImage`（`CreateImage()`）：它是 `RawImage`
//    （`GlazierImage_uGUI.cs:199` `AddComponent<RawImage>()`）—— **整张贴图拉伸、没有 9 宫格**
//    ⟹ 只能画直角方块（坑 #84）。要圆角只能 `ISleekSprite`（后端 uGUI `Image`，
//    `GlazierSprite_uGUI.cs:111`）。于是本组件把两条路都收进 `ISleekSprite`：
//    直角用 `Regular`、圆角用 `Sliced`。
//
//  ⚠ 三条"只有读引擎源码才知道"的坑（本组件的实现就是围绕它们）：
//    1. **`Sprite` setter 不改 `Image.type`**，改类型的是 `DrawMethod`（`GlazierSprite_uGUI.cs:53-74`），
//       而 `ConstructNew()` 的默认是 **`Tiled`**（`:116`）⟹ **不显式设 `DrawMethod` 就是平铺**。
//       本组件两个分支都显式设它。
//    2. **`Sliced` 的 9 宫格靠贴图的 `border`**（`Sprite.Create(...border)`）：只有中间那块被
//       拉伸、四角固定 ⟹ 一行几百像素也不会把圆角拉成椭圆。`border` 生成贴图时给（= 半径）。
//    3. **`SynchronizeColors` 只在 `Sprite != null` 时把这一层 `enabled = true`**
//       （`GlazierSprite_uGUI.cs:118-127`）⟹ **先挂 `Sprite` 再写 `TintColor`**，否则颜色不生效
//       （与 `ISleekImage` 那条路的 `desiredTexture` 是同一族坑，见 #84）。
//
//  ⚠ 圆角的**物理限制**：填充部分宽度是**相对值**（`SizeScale_X = Fill`）。当 `Fill` 很小、
//    填充宽度 < 2×半径时，9 宫格左右角会重叠 ⟹ 小进度看起来是个"压扁的小胶囊"。
//    这是圆角进度条的固有现象（轨道本身不受影响）。要绝对精确就让 `Radius = 0`。
//
//  ★ 生命周期与另七个公开组件一致：**关界面用 `Hide()`（只藏不拆）**，
//    明确不要了才 `Discard()`。**`Build()` 同时是"恢复显示"的入口**。
// ============================================================================
using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace CustomUI
{
    /// <summary>
    /// **通用「进度条 / 状态条」**（`26.10.3.55`）：一根按比例填充的实心条。
    /// 直角（`Radius = 0`，默认）与原版加载进度条同观感；设了 <see cref="Radius"/> 就有真圆角。
    ///
    /// 典型用法：
    /// <code>
    /// bar = new StorageUiBar();
    /// bar.Height = 20f;                                              // 条的厚度（默认 18）
    /// bar.Radius = 10f;                                              // 圆角半径（0 = 平角；= 高度/2 就是胶囊）
    /// bar.TrackColor = new SleekColor(ESleekTint.BACKGROUND, 0.55f);// 未填充部分
    /// bar.Color = new SleekColor(new Color(0.25f, 0.70f, 0.35f, 1f));// 已填充部分（真彩色）
    /// bar.Label = "炼制 42%";                                         // 压在条上的文字（空串 = 不显示）
    /// bar.Build(myContainer);          // 铺满父容器**宽度**（高度取 Height）
    /// bar.Element.PositionOffset_Y = 30f;   // 位置由**调用方**摆
    /// bar.Element.OnClicked += delegate { Jump(); };   // 点它要自己订阅（不订阅就不挡点击）
    /// bar.Fill = 0.42f;                // 唯一"多满"口径（0~1，自动夹取；每轮设一次很安全）
    /// </code>
    ///
    /// **四条语义边界**：
    /// 1. **`Fill` 是唯一的"多满"口径**（0~1）—— 调用方别自己再存一份"当前/最大"再除一次；
    ///    要显示 `3 / 8` 这类文本就用 <see cref="Label"/> 自己拼（组件的职责只是画）。
    /// 2. **宽度是相对值**（跟随父容器），高度是像素（<see cref="Height"/>）—— 与"满宽的行用
    ///    相对值、有内容的行由调用方摆"那条约定一致。
    /// 3. **文字压在条上**（原版同款）+ `ColorfulBackdrop` ⟹ 条是任意颜色时文字都读得清。
    ///    <see cref="DesiredHeight"/> 就等于 <see cref="Height"/>（不额外占高）。
    /// 4. **它不动数据、不筛内容** —— 画多满由 `Fill` 说了算，含义由调用方定。
    /// </summary>
    public class StorageUiBar
    {
        /// <summary>条厚默认 18（比小号字体的 24 行高低一点，正好压得住一行字）。</summary>
        private const float DEFAULT_HEIGHT = 18f;

        /// <summary>文字左右各留一点，免得贴到条的两端。</summary>
        private const float LABEL_PAD = 4f;

        // ★ 26.10.3.64：直角/圆角贴图**不再由本类生成** —— 一律走 `StorageUiShapes`
        //   （原先进度条与填充层各生成一份、逐像素等价 ⟹ 形状口径写了两处）。

        private ISleekSprite root;     // 轨道（未填充部分）—— 也是"点击"的宿主
        private ISleekSprite fill;     // 已填充部分
        private ISleekLabel label;     // 压在条上的文字

        private float _height = DEFAULT_HEIGHT;
        private float _fill;
        private float _radius;
        private SleekColor _color = new SleekColor(ESleekTint.FOREGROUND, 1f);
        private SleekColor _track = new SleekColor(ESleekTint.BACKGROUND, 0.55f);
        private SleekColor _textColor = new SleekColor(ESleekTint.FONT, 1f);
        private ESleekFontSize _fontSize = ESleekFontSize.Default;
        private string _label = "";
        private int appliedColorKey = int.MinValue;
        private int appliedTextKey = int.MinValue;

        /// <summary>
        /// 条本身（**未建之前是 null**）。位置由调用方摆；想接点击就订阅它的 `OnClicked`
        /// （`ISleekSprite` 自带；不订阅时 `raycastTarget = false` ⟹ **不会挡住下面的控件**）。
        /// </summary>
        public ISleekSprite Element { get { return root; } }

        public bool IsBuilt { get { return root != null; } }

        /// <summary>占位高度 = <see cref="Height"/>（文字压在条上，不额外占高）。</summary>
        public float DesiredHeight { get { return _height; } }

        /// <summary>条的厚度（像素，默认 18，最小 2）。改它立刻重排。</summary>
        public float Height
        {
            get { return _height; }
            set
            {
                float v = value < 2f ? 2f : value;
                if (_height == v) return;
                _height = v;
                Layout();
            }
        }

        /// <summary>
        /// 圆角半径（像素，默认 **0 = 平角**）。单位与 <see cref="Height"/> 相同：
        /// 设 `Radius = Height / 2` 就是**胶囊**（半圆端）；想"圆角矩形"就取小一点（如 4）。
        /// 改它只换贴图（按半径缓存），不重建控件；建之前设、建之后设都行。
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

        /// <summary>已填充比例（0~1，**自动夹取**）。这是唯一的"多满"口径。</summary>
        public float Fill
        {
            get { return _fill; }
            set
            {
                float v = value < 0f ? 0f : (value > 1f ? 1f : value);
                if (_fill == v) return;
                _fill = v;
                LayoutFill();
            }
        }

        /// <summary>已填充部分的颜色（**真彩色** —— 走 `TintColor`，不是只有 alpha 有效的 `BackgroundColor`）。</summary>
        public SleekColor Color
        {
            get { return _color; }
            set { _color = value; ApplyColors(); }
        }

        /// <summary>未填充部分的颜色（轨道）。默认与内置大格子同色（`BACKGROUND` 55%）。</summary>
        public SleekColor TrackColor
        {
            get { return _track; }
            set { _track = value; ApplyColors(); }
        }

        /// <summary>压在条上的文字（**空串 = 不显示**；不显示时那层会被藏起来，不拆控件）。</summary>
        public string Label
        {
            get { return _label; }
            set { _label = (value == null) ? "" : value; ApplyText(); }
        }

        /// <summary>文字颜色。默认 `ESleekTint.FONT`（= 玩家在设置里选的字体颜色）。</summary>
        public SleekColor TextColor
        {
            get { return _textColor; }
            set { _textColor = value; ApplyText(); }
        }

        /// <summary>
        /// 文字字号。⚠ 引擎只给**枚举 6 档**（`Tiny / Small / Default / Medium / Large / Title`），
        /// 没有"任意像素字号"这种旋钮 —— 想要更大只能换字号档。
        /// </summary>
        public ESleekFontSize FontSize
        {
            get { return _fontSize; }
            set { _fontSize = value; ApplyText(); }
        }

        /// <summary>
        /// 建一次并挂进 <paramref name="parent"/>（**铺满它的宽度**，高度取 <see cref="Height"/>）。
        /// 重复调用只是重新挂父容器，不重复建。
        /// ⚠ 与另几个组件一样：它同时是"恢复显示"的入口（宿主的区块面板只藏不拆）。
        /// </summary>
        public void Build(ISleekElement parent)
        {
            if (parent == null) return;

            if (root != null)
            {
                // ⚠ 不能先 RemoveChild（那是 InternalDestroy，会把本体销毁，紧接着挂上去的是尸体）。
                //   AddChild 自己会从旧父容器摘走。
                try { parent.AddChild(root); } catch (Exception) { return; }
                Layout();
                LayoutFill();
                appliedColorKey = int.MinValue;     // 重挂之后按当前值再写一遍
                appliedTextKey = int.MinValue;
                ApplyShape();                       // 半径可能在建之后被改过 —— 幂等等价（按当前值设）
                ApplyColors();
                ApplyText();
                // ★ 必须显式恢复可见性：Hide() 把 root 藏了，而上面几条都不碰 root.IsVisible
                //   ⟹ 漏了这一句就是"Build 之后条还是不出现"（自检 Build恢复可见=False 抓的就是它）。
                try { root.IsVisible = true; } catch (Exception) { }
                return;
            }

            try
            {
                // ★ 顺序与画法：先轨道、再填充、最后文字（同一父容器里后加的盖在先加的上面）。
                root = Glazier.Get().CreateSprite();
                root.PositionScale_X = 0f;
                root.SizeScale_X = 1f;                   // 宽度跟随父容器
                try { parent.AddChild(root); }
                catch (Exception) { root = null; return; }

                fill = Glazier.Get().CreateSprite();
                fill.PositionScale_X = 0f;
                fill.PositionScale_Y = 0f;
                fill.SizeScale_Y = 1f;                   // 高度跟随轨道
                root.AddChild(fill);

                label = Glazier.Get().CreateLabel();
                label.TextAlignment = TextAnchor.MiddleCenter;
                label.TextContrastContext = ETextContrastContext.ColorfulBackdrop;   // 压条可读（原版同款）
                label.PositionScale_X = 0f;
                label.PositionScale_Y = 0f;
                label.SizeScale_X = 1f;
                root.AddChild(label);
            }
            catch (Exception) { root = null; fill = null; label = null; return; }

            Layout();
            LayoutFill();
            ApplyShape();        // ⚠ 必须在 ApplyColors 之前：先挂 Sprite，写 TintColor 才生效（见类头注 3）
            ApplyColors();
            ApplyText();
            try { root.IsVisible = true; } catch (Exception) { }
        }

        /// <summary>只藏不拆（临时收起时用）。再显示靠 `Build(parent)`。</summary>
        public void Hide()
        {
            try { if (root != null) root.IsVisible = false; } catch (Exception) { }
        }

        /// <summary>真拆掉（下一次 `Build` 会重建）。</summary>
        public void Discard()
        {
            try
            {
                if (root != null && root.Parent != null) root.Parent.RemoveChild(root);
            }
            catch (Exception) { }

            // ⚠ 只松引用，**不去销毁贴图**：两张贴图都是全进程共用的静态缓存
            //   （`ISleekSprite` 没有 `ShouldDestroyTexture`，本来也不会替你销毁）。
            root = null;
            fill = null;
            label = null;
            appliedColorKey = int.MinValue;
            appliedTextKey = int.MinValue;
        }

        // ================= 内部 =================

        /// <summary>轨道高度 + 文字几何（相对值：宽度跟随父容器，只留像素级左右内缩）。</summary>
        private void Layout()
        {
            if (root != null)
            {
                try { root.SizeOffset_Y = _height; } catch (Exception) { }
            }

            if (label == null) return;
            try
            {
                label.PositionOffset_X = LABEL_PAD;
                label.PositionOffset_Y = 0f;
                label.SizeOffset_X = -2f * LABEL_PAD;
                label.SizeOffset_Y = _height;      // 占满条高 + MiddleCenter ⟹ 垂直居中
            }
            catch (Exception) { }
        }

        /// <summary>填充宽度 = 轨道宽度的 <see cref="Fill"/> 倍（相对值，父容器改宽自动跟随）。</summary>
        private void LayoutFill()
        {
            if (fill == null) return;
            try { fill.SizeScale_X = _fill; } catch (Exception) { }
        }

        /// <summary>
        /// 按 <see cref="Radius"/> 给轨道与填充换贴图 / 画法：
        /// 半径 0 ⟹ `Regular` + 1×1 白贴图（平角）；半径 &gt; 0 ⟹ `Sliced` + 圆角贴图（9 宫格）。
        /// 幂等：同一个半径重复调只是重写两个属性。
        /// </summary>
        private void ApplyShape()
        {
            if (root == null) return;
            try
            {
                int r = Mathf.RoundToInt(_radius);
                bool rounded = r >= 1;
                // ★ 贴图一律走 `StorageUiShapes`（与填充层**同一份**形状 —— 26.10.3.64 收口）。
                Sprite sp = StorageUiShapes.Rounded(rounded ? r : 0);
                ESleekSpriteType method = rounded ? ESleekSpriteType.Sliced : ESleekSpriteType.Regular;

                // ⚠ Sprite 的 setter **不改** Image.type —— 改类型的是 DrawMethod（类头注 1）。
                root.Sprite = sp;
                root.DrawMethod = method;
                if (fill != null)
                {
                    fill.Sprite = sp;
                    fill.DrawMethod = method;
                }
            }
            catch (Exception) { }
        }

        /// <summary>两个颜色一起写（**按颜色键去重** —— 每轮设同一个颜色不会反复触发 `SynchronizeColors`）。</summary>
        private void ApplyColors()
        {
            if (root == null) return;

            int k = Key(_color) ^ (Key(_track) * 31);
            if (k == appliedColorKey) return;
            appliedColorKey = k;

            try
            {
                root.TintColor = _track;                 // 写它就是一次 SynchronizeColors ⟹ 这一层 enabled
                if (fill != null) fill.TintColor = _color;
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 文字：内容变了才写文本，颜色/字号按颜色键去重；**空串把这一层藏起来（不拆控件）**。
        /// </summary>
        private void ApplyText()
        {
            if (label == null) return;

            try
            {
                bool show = _label.Length > 0;
                if (label.Text != _label) label.Text = _label;

                int k = Key(_textColor) ^ ((int)_fontSize * 31);
                if (k != appliedTextKey)
                {
                    appliedTextKey = k;
                    label.TextColor = _textColor;
                    label.FontSize = _fontSize;
                }

                label.IsVisible = show;
            }
            catch (Exception) { }
        }

        /// <summary>把颜色压成一个 int（去重用；`ESleekTint.NONE` 直接返回自定义色）。</summary>
        private static int Key(SleekColor c)
        {
            Color32 x = c.Get();
            return unchecked((x.r << 24) | (x.g << 16) | (x.b << 8) | x.a);
        }

    }
}
