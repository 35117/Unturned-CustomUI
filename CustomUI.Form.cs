// ============================================================================
//  CustomUI · 可复用的「纵向表单」+ 现成的设置行（StorageUiForm，26.10.3.48）
// ============================================================================
//  给**第三方插件自己的界面**用：控件按加入顺序**自上而下**自动排好，总高由它自己算，
//  调用方不用再手写 `PositionOffset_Y` 的累加（自绘界面里最容易"改了一处忘了下一处"的东西）。
//
//  ★ 为什么要有它：本框架此前只"给容器"、不给"排版"。第三方做一台机器的配置界面时，
//    得自己 `Glazier.Get().CreateToggle() / CreateSlider() / CreateInt32Field()`，
//    并自己处理四件**很容易错、又不容易被发现**的事：
//      1. **滑块是 0~1 归一化的** —— uGUI 后端里 `ISleekSlider.Value` 直接就是
//         `scrollbarComponent.value`（已核 `GlazierSlider_uGUI.cs:34-46`）
//         ⟹ 区间映射与"整数档"都得自己做；不量化就会拿一串浮点去反复重建。
//         ★★ 而且它的**默认方向是竖直的** —— `GlazierSlider_uGUI.cs:14`
//         `_orientation = ESleekOrientation.VERTICAL`（源码原话 "User code assumes
//         vertical default"）⟹ **想要横向必须自己** `sd.Orientation = HORIZONTAL`。
//         全 Unturned 的横滑块（`SleekValue` / 编辑器各 UI）都显式写了这一句 ——
//         不写拿到的是根**竖条**（208 实测报过「滑块应该是左右滑才对」）。
//      2. **数值框不夹取值** —— `ISleekInt32Field.Value` 是 `_state = value` 原样存
//         （已核 `GlazierInt32Field_uGUI.cs:14-28`）⟹ 玩家敲 9999 就真的交给你 9999。
//      3. **同一个值会被重复触发** —— 面板重建后重新挂回调会再收到一次当前值；
//         不筛就会把"回调 = 要重建"变成"每帧重建"（同族坑 #73）。
//      4. **重新挂载不能 `RemoveChild` 再 `AddChild`** —— uGUI 的 `RemoveChild` 末尾是
//         `InternalDestroy()`（`GlazierElementBase_uGUI.cs:253-283`，内部还会先
//         `RemoveAllChildren()`）⟹ 把这个 frame **连同里面的行一起**销毁/回收，
//         紧接着的 `AddChild` 拿到的是一具尸体 ⟹ `IsVisible` 停在 false，
//         "Build 恢复可见"当场失效（208 实测报过，见 `Build` 里的注释）。
//         `AddChild` 自己会从旧父容器摘走（同文件 `:321-347`），**不需要先摘**。
//    这四条都在**组件内部**处理掉，调用方看到的口径只有一句：
//    **回调 = 值真的变了** ⟹ 可以放心在回调里 `MarkDirty()`。
//
//  ★ 三条取值纪律（写在这里是因为它们比功能更容易被误解）：
//    · **开关**：只在布尔值真的变了时回调；
//    · **滑块**：整数档（`min`~`max`），同一个档重复拖动**不**回调，标签上实时显示当前档；
//    · **数值**：照抄玩家输入后**当场夹到 [min,max]**，越界**不**回调（并写回纠正显示）。
//
//  ★ 生命周期与另四个公开组件一致：**关界面用 `Hide()`（只藏不拆）**，
//    明确不要了才 `Discard()`。`Reset()` 是"这些行都不要了、马上重建一批"。
//
//  ⚠ 引擎调用全部照抄本仓库既有用法（`CreateFrame` / `CreateLabel` / `CreateButton` 的
//    属性写法见 `GridPanel.cs:499-507`）与 SDK 接口声明（`SleekSlider.cs` / `SleekToggle.cs` /
//    `SleekInt32Field.cs`），**没有一处凭印象写**。默认后端是 uGUI
//    （`GlazierFactory.Create()` 的兜底分支），已核这四个工厂方法在 uGUI 后端都存在。
// ============================================================================
using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace CustomUI
{
    /// <summary>开关行被玩家切换（只在值**真的变了**时回调）。</summary>
    public delegate void StorageUiToggleHandler(bool value);

    /// <summary>滑块行 / 数值行的整数档被改（只在值**真的变了**时回调）。</summary>
    public delegate void StorageUiValueHandler(int value);

    /// <summary>
    /// **可复用的「纵向表单」**（`26.10.3.47`）。
    ///
    /// 典型用法（第三方给一台机器做配置界面）：
    /// <code>
    /// form = new StorageUiForm();
    /// form.Padding = 6f;  form.Gap = 4f;
    /// form.AddLabel("熔炉设置", 22f);
    /// form.AddToggle("自动出料", true, OnAutoOut, 24f);
    /// form.AddSlider("目标温度", 200, 2000, 800, OnTemp, 24f);
    /// form.AddNumber("最大批量", 1, 64, 16, OnBatch, 24f);
    /// ISleekButton apply = form.AddButton("应用", "改完点这里", 26f);
    /// apply.OnClicked += delegate (ISleekElement el) { Apply(); };
    ///
    /// form.Build(myContainer);      // 建一次；总高 = form.DesiredHeight
    /// // 关界面：form.Hide()；换一批行：form.Reset()；明确不要了：form.Discard()
    /// </code>
    ///
    /// **也可以把已有组件塞进来**（它们自己的根元素由 `Build` 建）：
    /// <code>
    /// box.Build(form.AddRow(30f));                 // StorageSearchBox 占一行
    /// bar.Build(form.AddRow(bar.DesiredHeight));   // StorageFilterBar 占一行
    /// </code>
    ///
    /// **四条语义边界**：
    /// 1. **它不存任何业务值** —— 只把"玩家改成了多少"交给回调；真源永远在调用方。
    /// 2. **回调里只该改数据 / 标脏** —— 重建放下一帧做（与三个内置视图同一条约定）。
    /// 3. **宽度一律相对值**（跟随父容器）—— 挂载前父容器宽度是 0，写像素宽会算错。
    /// 4. **左右也有留白**（`Padding` + 右侧的 `RightMargin`）—— 右侧控件（开关/滑块/数值）
    ///    都贴着行右边界，**行本身不缩进就等于零右边距**（用户实测："右侧距离太窄"）。
    /// </summary>
    public class StorageUiForm
    {
        /// <summary>行内控件与行左边界的距离（标签都从这里起）。</summary>
        private const float ROW_INSET = 0f;

        /// <summary>开关行右侧那个方块控件占的宽度。</summary>
        private const float TOGGLE_W = 26f;

        /// <summary>开关本体比行高矮这么多（否则会顶满一行，观感很挤）。</summary>
        private const float TOGGLE_INSET_Y = 3f;

        // ================= 旋钮（Build 之后改也会当场重排）=================

        private float _padding = 6f;
        private float _rightMargin = 4f;
        private float _gap = 4f;

        /// <summary>
        /// **四周**留白（默认 6）：上 = 首行起点、下 = 总高末尾、左 = 行左边界、
        /// 右 = 行右边界（再叠一个 <see cref="RightMargin"/>）。改它会立刻重排所有行。
        /// ⚠ 总高只吃它的**上下**（`2*Padding`）—— 左右留白不影响 `DesiredHeight`。
        /// </summary>
        public float Padding
        {
            get { return _padding; }
            set
            {
                if (_padding == value) return;
                _padding = value;
                Relayout();
            }
        }

        /// <summary>
        /// **在 <see cref="Padding"/> 之外，右侧再多留这么多**（默认 4）。
        /// 右侧那一列是控件（开关 / 滑块 / 数值框），紧贴边线会显得很挤 —— 这个旋钮就是给
        /// 接入方调"右边距"用的（用户实测反馈：默认太窄）。改它会立刻重排所有行。
        /// ⚠ 实际右边距 = `Padding + RightMargin`；只改右边距就**别**去动 `Padding`
        ///   （那会连带把上下也一起加宽）。同样不影响 `DesiredHeight`。
        /// </summary>
        public float RightMargin
        {
            get { return _rightMargin; }
            set
            {
                if (_rightMargin == value) return;
                _rightMargin = value;
                Relayout();
            }
        }

        /// <summary>相邻两行之间的间距（默认 4）。改它会立刻重排所有行。</summary>
        public float Gap
        {
            get { return _gap; }
            set
            {
                if (_gap == value) return;
                _gap = value;
                Relayout();
            }
        }

        // ================= 只读状态 =================

        /// <summary>整块表单的根元素（**它自己不带任何外观**，就是个用来分组的 frame）。</summary>
        public ISleekElement Element { get { return root; } }

        public bool IsBuilt { get { return root != null; } }

        /// <summary>
        /// 这一块要占的高度 = `Padding*2 + Σ行高 + Gap*(行数-1)`（没有行时就是 `Padding*2`）。
        /// ⚠ 它是**算出来的**、不是攒出来的 —— 行高改了 / `Gap` 改了立刻就对。
        /// </summary>
        public float DesiredHeight { get { return MeasureHeight(); } }

        // ================= 生命周期 =================

        /// <summary>
        /// 建一次并挂进 <paramref name="parent"/>。重复调用只是重新挂父容器（不重复建）。
        /// ★ **它同时是"恢复显示"的入口**：宿主的区块面板是"只藏不拆"的，重新打开时
        ///   宿主只会把**区块根面板**恢复可见，**不会**管里面这一块 —— 所以 <see cref="Hide"/>
        ///   之后想再显示，就在这里再 `Build(parent)` 一次（幂等，且会恢复可见）。
        /// ⚠ 宽度**跟随父容器**（相对值）—— 挂载前父容器宽度是 0，所以这里不写像素宽。
        /// </summary>
        public void Build(ISleekElement parent)
        {
            if (parent == null) return;

            if (root != null)
            {
                // ⚠⚠ 这里**绝不能**先 `RemoveChild` 再 `AddChild`：uGUI 后端的 `RemoveChild`
                //   末尾是 `InternalDestroy()`（`GlazierElementBase_uGUI.cs:253-283`，它自己还会先
                //   `RemoveAllChildren()`）⟹ 把这个 frame **连同里面的行一起**销毁/回收；
                //   紧接着的 `AddChild` 拿到的是一具尸体（新父容器上的 `SetParent` 直接
                //   空引用）⟹ 整块表单永远回不来，`IsVisible` 停在 `Hide()` 留下的 false。
                //   **208 实测就是这么 FAIL 的**：`Build恢复可见=False`（另外两项全 True）。
                //   `AddChild` 自己会把它从旧父容器摘走（同文件 `:321-347`，`_parent == this`
                //   还会直接早退）⟹ **不需要先摘**。
                try { parent.AddChild(root); } catch (Exception) { return; }
                try { root.IsVisible = true; } catch (Exception) { }
                Relayout();
                return;
            }

            root = Glazier.Get().CreateFrame();
            root.PositionScale_X = 0f;
            root.PositionOffset_X = 0f;
            root.PositionOffset_Y = 0f;
            root.SizeScale_X = 1f;
            root.SizeOffset_X = 0f;
            root.IsVisible = true;
            try { parent.AddChild(root); }
            catch (Exception) { root = null; return; }

            // 先建好的行（Build 之前 Add 的）现在才真正挂上
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                if (r == null || r.element == null) continue;
                try { root.AddChild(r.element); } catch (Exception) { }
            }
            Relayout();
        }

        /// <summary>
        /// 只藏不拆（临时收起时用；行与它们的值都还在）。
        /// ⚠ 再显示要在 <see cref="Build"/> 里再挂一次 —— 宿主恢复区块可见性时**不会**管里面这一块。
        /// </summary>
        public void Hide()
        {
            try { if (root != null) root.IsVisible = false; } catch (Exception) { }
        }

        /// <summary>
        /// 真拆掉整块（**行也跟着拆**）。下一次 <see cref="Build"/> 会重建。
        /// </summary>
        public void Discard()
        {
            Reset();
            try
            {
                if (root != null && root.Parent != null) root.Parent.RemoveChild(root);
            }
            catch (Exception) { }
            root = null;
        }

        /// <summary>
        /// 把这些行**全部真拆**（不是只藏）—— 用于"马上要换一批行"。
        /// ⚠ 它**不**清 <see cref="Padding"/> / <see cref="Gap"/>（那是整块的样式，不是行）。
        /// ⚠ 拆掉的行上的回调会随之失效，调用方要按需重新挂。
        /// </summary>
        public void Reset()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                if (r == null || r.element == null) continue;
                try { if (r.element.Parent != null) r.element.Parent.RemoveChild(r.element); }
                catch (Exception) { }
            }
            rows.Clear();
            Relayout();
        }

        // ================= 加行 =================

        /// <summary>
        /// 加一个**空行**并发回它的容器。要放**已有组件**时用它：
        /// <code>searchBox.Build(form.AddRow(30f));</code>
        /// ⚠ 行容器会把子元素的宽度撑满（`SizeScale_X = 1f`）；你自己建的控件若不想要满宽，
        ///   在 `AddChild` 之后再改它的 `SizeScale_X` / `SizeOffset_X` 即可。
        /// </summary>
        public ISleekElement AddRow(float height)
        {
            return FinishRow(NewRow(height), height);
        }

        /// <summary>加一行左对齐文字，发回那个标签（想改文字 / 字号就改它）。</summary>
        public ISleekLabel AddLabel(string text, float height)
        {
            ISleekElement row = NewRow(height);
            ISleekLabel lb = Glazier.Get().CreateLabel();
            lb.PositionOffset_X = ROW_INSET;
            lb.PositionOffset_Y = 0f;
            lb.SizeScale_X = 1f;
            lb.SizeOffset_X = -ROW_INSET;
            lb.SizeOffset_Y = height;
            lb.FontSize = ESleekFontSize.Small;
            lb.TextAlignment = TextAnchor.MiddleLeft;
            lb.Text = text ?? "";
            row.AddChild(lb);
            FinishRow(row, height);
            return lb;
        }

        /// <summary>
        /// 加一行通栏按钮，发回那个按钮 —— **点击事件由调用方自己挂**
        /// （`btn.OnClicked += delegate (ISleekElement el) { … };`）。
        /// 这样比再包一层委托类型更直接，也和本仓库内置视图里的写法一致。
        /// </summary>
        public ISleekButton AddButton(string text, string tooltip, float height)
        {
            ISleekElement row = NewRow(height);
            ISleekButton btn = Glazier.Get().CreateButton();
            btn.PositionOffset_X = ROW_INSET;
            btn.PositionOffset_Y = 0f;
            btn.SizeScale_X = 1f;
            btn.SizeOffset_X = -ROW_INSET;
            btn.SizeOffset_Y = height;
            btn.FontSize = ESleekFontSize.Small;
            btn.Text = text ?? "";
            btn.TooltipText = tooltip ?? "";
            row.AddChild(btn);
            FinishRow(row, height);
            return btn;
        }

        /// <summary>
        /// 加一行「左标签 + 右开关」，发回那个开关。
        /// <paramref name="onChange"/> 只在布尔值**真的变了**时触发。
        /// </summary>
        public ISleekToggle AddToggle(string label, bool initial, StorageUiToggleHandler onChange,
            float height)
        {
            ISleekElement row = NewRow(height);

            ISleekLabel lb = MakeRowLabel(row, label, height, TOGGLE_W);

            ISleekToggle tog = Glazier.Get().CreateToggle();
            tog.PositionScale_X = 1f;
            tog.PositionOffset_X = -TOGGLE_W;
            tog.PositionOffset_Y = (height - (height - TOGGLE_INSET_Y)) * 0.5f;
            tog.SizeScale_X = 0f;
            tog.SizeOffset_X = TOGGLE_W;
            tog.SizeOffset_Y = height - TOGGLE_INSET_Y;
            try { tog.Value = initial; } catch (Exception) { }
            row.AddChild(tog);

            // ⚠ 夹具式捕获：`last` 存**控件当前已生效的值**，用来把重复触发挡掉。
            bool last = initial;
            tog.OnValueChanged += delegate (ISleekToggle t, bool v)
            {
                if (v == last) return;      // 面板重建后重新挂回调会再收到一次当前值
                last = v;
                FireToggle(onChange, v);
            };

            FinishRow(row, height);
            return tog;
        }

        /// <summary>
        /// 加一行「左标签 + 右滑块」，发回那个滑块。**横向**滑块（引擎默认是竖直的，这里替调用方设好）。
        /// · 区间是**整数档** `[min, max]`，滑块内部按 `(value-min)/(max-min)` 映射到引擎的 0~1；
        /// · 标签上会实时显示 `"标签：当前档"`；
        /// · <paramref name="onChange"/> 只在**档位真的变了**时触发（同一档重复拖动不回调）。
        /// </summary>
        public ISleekSlider AddSlider(string label, int min, int max, int initial,
            StorageUiValueHandler onChange, float height)
        {
            int lo = min, hi = max;
            if (hi < lo) { int t = lo; lo = hi; hi = t; }
            if (hi == lo) hi = lo + 1;          // 退化区间：滑块仍有意义（一个档），且不会除零
            int span = hi - lo;

            int start = Mathf.Clamp(initial, lo, hi);

            ISleekElement row = NewRow(height);
            ISleekLabel lb = MakeRowLabel(row, label, height, 0f);
            SetValueText(lb, label, start);

            ISleekSlider sd = Glazier.Get().CreateSlider();
            // ★★ 必须显式设横向：uGUI 后端**默认就是竖直的**
            //   （`GlazierSlider_uGUI.cs:14` `_orientation = ESleekOrientation.VERTICAL`，
            //   源码原话 "User code assumes vertical default"）。
            //   不写这一句拿到的是一根竖条（208 实测报过「滑块应该是左右滑才对」）；
            //   全 Unturned 的横滑块（`SleekValue.cs:65`、编辑器各 UI）都显式写了这一句。
            sd.Orientation = ESleekOrientation.HORIZONTAL;
            sd.PositionScale_X = 0.45f;
            sd.PositionOffset_X = 0f;
            sd.PositionOffset_Y = (height - (height - TOGGLE_INSET_Y)) * 0.5f;
            sd.SizeScale_X = 0.55f;
            sd.SizeOffset_X = 0f;
            sd.SizeOffset_Y = height - TOGGLE_INSET_Y;
            try { sd.Value = (float)(start - lo) / span; } catch (Exception) { }
            row.AddChild(sd);

            int last = start;
            sd.OnValueChanged += delegate (ISleekSlider s, float state)
            {
                int v = lo + Mathf.RoundToInt(Mathf.Clamp01(state) * span);
                if (v == last) return;          // 浮点拖动一帧能报好几次 —— 不量化就是每帧重建
                last = v;
                SetValueText(lb, label, v);
                FireValue(onChange, v);
            };

            FinishRow(row, height);
            return sd;
        }

        /// <summary>
        /// 加一行「左标签 + 右数值框」，发回那个数值框。
        /// · 玩家敲进去的值**当场夹到 `[min, max]`**；越界时写回纠正显示；
        /// · <paramref name="onChange"/> 只在**夹取后的值真的变了**时触发；
        /// · 标签上会实时显示 `"标签：当前值"`。
        /// </summary>
        public ISleekInt32Field AddNumber(string label, int min, int max, int initial,
            StorageUiValueHandler onChange, float height)
        {
            int lo = min, hi = max;
            if (hi < lo) { int t = lo; lo = hi; hi = t; }

            int start = Mathf.Clamp(initial, lo, hi);

            ISleekElement row = NewRow(height);
            ISleekLabel lb = MakeRowLabel(row, label, height, 60f);
            SetValueText(lb, label, start);

            ISleekInt32Field fld = Glazier.Get().CreateInt32Field();
            fld.PositionScale_X = 1f;
            fld.PositionOffset_X = -60f;
            fld.PositionOffset_Y = (height - (height - TOGGLE_INSET_Y)) * 0.5f;
            fld.SizeScale_X = 0f;
            fld.SizeOffset_X = 60f;
            fld.SizeOffset_Y = height - TOGGLE_INSET_Y;
            try { fld.Value = start; } catch (Exception) { }
            row.AddChild(fld);

            int last = start;
            fld.OnValueChanged += delegate (ISleekInt32Field f, int raw)
            {
                int v = Mathf.Clamp(raw, lo, hi);
                // 写回纠正显示：`Value` 的 setter 走的是 `_state = value`（不触发回调），
                // 所以这里写回是安全的、不会绕回自己（已核 GlazierInt32Field_uGUI.cs:14-28）。
                if (v != raw)
                {
                    try { f.Value = v; } catch (Exception) { }
                }
                if (v == last) return;
                last = v;
                SetValueText(lb, label, v);
                FireValue(onChange, v);
            };

            FinishRow(row, height);
            return fld;
        }

        // ================= 内部：排版 =================

        private class Row
        {
            public ISleekElement element;
            public float height;
        }

        private ISleekElement root;
        private readonly List<Row> rows = new List<Row>();

        /// <summary>
        /// 总高的**唯一来源**（`DesiredHeight` 与 `Relayout` 都走它）——
        /// 分成两处算迟早对不上（同族坑 #68）。
        /// </summary>
        private float MeasureHeight()
        {
            float h = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0) h += _gap;
                h += rows[i].height;
            }
            return h + 2f * _padding;
        }

        /// <summary>按加入顺序自上而下排一遍，并把总高写到根元素上。没有根就只更新记录。</summary>
        private void Relayout()
        {
            float y = _padding;
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                if (r.element != null)
                {
                    try
                    {
                        // 左右留白也在这里落地（`Padding` 四周 + 右侧再叠 `RightMargin`）——
                        // 只在纵向留白的话，右侧的开关/滑块/数值框会**贴着父容器右边缘**
                        // （用户实测：太窄）。行宽仍然跟随父容器（相对值），只是两端各让开一点。
                        r.element.PositionOffset_X = _padding;
                        r.element.SizeOffset_X = -(_padding + _rightMargin);
                        r.element.PositionOffset_Y = y;
                    }
                    catch (Exception) { }
                }
                y += r.height + _gap;
            }

            if (root != null)
            {
                try { root.SizeOffset_Y = MeasureHeight(); } catch (Exception) { }
            }
        }

        /// <summary>建一个空行容器（不带外观，只是用来分组与定位）。</summary>
        private static ISleekElement NewRow(float height)
        {
            ISleekElement el = Glazier.Get().CreateFrame();
            el.PositionScale_X = 0f;
            el.PositionOffset_X = 0f;
            el.PositionOffset_Y = 0f;
            el.SizeScale_X = 1f;
            el.SizeOffset_X = 0f;
            el.SizeOffset_Y = height;
            return el;
        }

        /// <summary>登记一行并在需要时挂上、重排。</summary>
        private ISleekElement FinishRow(ISleekElement row, float height)
        {
            Row r = new Row();
            r.element = row;
            r.height = height;
            rows.Add(r);

            if (root != null && row != null)
            {
                try { root.AddChild(row); } catch (Exception) { }
            }
            Relayout();
            return row;
        }

        /// <summary>
        /// 行左侧那个标签。<paramref name="reserveRight"/> 是右侧控件要占的宽度
        /// （0 = 后面那行由调用方自己用 scale 分宽度，例如滑块）。
        /// </summary>
        private static ISleekLabel MakeRowLabel(ISleekElement row, string label, float height,
            float reserveRight)
        {
            ISleekLabel lb = Glazier.Get().CreateLabel();
            lb.PositionOffset_X = ROW_INSET;
            lb.PositionOffset_Y = 0f;
            if (reserveRight > 0f)
            {
                lb.SizeScale_X = 1f;
                lb.SizeOffset_X = -reserveRight - ROW_INSET - 4f;
            }
            else
            {
                lb.SizeScale_X = 0.45f;
                lb.SizeOffset_X = -ROW_INSET;
            }
            lb.SizeOffset_Y = height;
            lb.FontSize = ESleekFontSize.Small;
            lb.TextAlignment = TextAnchor.MiddleLeft;
            lb.Text = label ?? "";
            row.AddChild(lb);
            return lb;
        }

        /// <summary>把"标签：值"写上去（值随玩家改动实时更新）。</summary>
        private static void SetValueText(ISleekLabel lb, string label, int value)
        {
            if (lb == null) return;
            try { lb.Text = (label == null ? "" : label) + "：" + value; }
            catch (Exception) { }
        }

        /// <summary>
        /// 把回调包一层 try —— 一个接入方的回调抛异常不该把整块表单卡住
        /// （与 Shared 层对回调的处理一致）。
        /// </summary>
        private static void FireToggle(StorageUiToggleHandler h, bool v)
        {
            if (h == null) return;
            try { h(v); }
            catch (Exception e) { CustomUIPlugin.Error("表单开关回调异常：" + e.Message); }
        }

        private static void FireValue(StorageUiValueHandler h, int v)
        {
            if (h == null) return;
            try { h(v); }
            catch (Exception e) { CustomUIPlugin.Error("表单取值回调异常：" + e.Message); }
        }
    }
}
