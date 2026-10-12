// ============================================================================
//  CustomUI · 宿主（Host）
// ============================================================================
//  职责：
//    1) 检测「原版背包页进入了存储状态」→ 询问注册表是否有人接管
//    2) 接管后把原版存储区藏掉，并把自己的面板挂进原版右列
//    3) 关闭兜底（ESC / 退出地图 / 目标销毁 / 原版关闭存储）
//
//  ⚠ 三个关键设计决定（都从源码推出来的）：
//
//  A) 用「每帧检测」而不是补丁 openStorage 来触发接管。
//     理由：openStorage 是**服务端**方法（PlayerInventory.cs:1571，注释写着
//     "Serverside open a storage crate and notify client"）。在专用服务器上
//     客户端根本不会执行它 —— 客户端走的是 ReceiveStoraging（:1120）。
//     所以补丁 openStorage 只能覆盖「单机/主机」，联机客户端要靠每帧检测。
//     每帧查 `inventory.isStoring && inventory.storage != null` 覆盖所有路径；
//     且天然排除车后备箱（openTrunk 时 storage 字段为 null）。
//
//  B) ⚠ 数据源必须是 `inventory.items[STORAGE]`，不是 `storage.items`！
//     这是本插件最容易搞错的一点：
//       · 服务端：openStorage → updateItems(STORAGE, storage.items)
//                 → items[STORAGE] 与 storage.items **同一个对象引用**
//       · 客户端：ReceiveStoraging 自己 `items[STORAGE].resize/addItem`，
//                 **完全不碰 storage.items**（那还是箱子自己的服务端数据副本）
//       · storage 字段本身：只有服务端 openStorage 会赋值；客户端
//                 ReceiveStoraging **不设 storage** → 客户端上它一直是 null！
//     结论：客户端上 `inv.storage` 恒为 null，而真实数据在 `inv.items[STORAGE]`。
//     因此「打开检测」用 `isStoring && !isStorageTrunk`（不依赖 storage 字段），
//     数据一律从 `inv.items[STORAGE]` 取。这样单机与联机行为完全一致。
//
//  C) 改写原版存储区必须发生在原版布局**之前**（Prefix），不是之后（Postfix）。
//     理由：updateBoxAreas 是按 headers[].IsVisible 累加布局的。若在它跑完后
//     才改，各元素的 y 与 ContentSizeOffset 都还是按旧值算的 —— 要等下一帧才纠正。
//     在 Prefix 改写，这一次布局就直接用对的值。
//
//  D) ⚠ 只藏「物品网格」，**必须保留「储存物品」标题的 IsVisible = true**。
//     这是本插件最容易做错的一处，细节见 ApplyVanillaOverride 的方法注释。
//     一句话：布局累加只看 headers[].IsVisible（:1849），高度取自
//     items[].SizeOffset_Y（:1866，不看 items 的可见性）。若连标题一起藏，
//     原版就不再给这一项分配空间 → 面板会被挤到「附近物品」区下方，
//     而原版右列顺序本是「存储区在上、附近物品在下」。
//     保留标题 → 原版照常给这一项分配空间 → 面板稳稳占据原网格的位置。
//
//  E) 右列是「区块栈」，不是「一块主面板 + 一块下方区块」。
//     主面板（存储视图）与任意多个 IStorageUiRegion 区块一起排成一条栈：
//     LayoutStack 按 order 从上往下叠，OccupiedHeight 把整栈高度交给原版那一行，
//     「附近物品」由原版自己排到栈下面。于是别的插件能自由搭配右侧
//     （把某块放到箱内物品上面、再加第三块第四块），而不必互相猜位置。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace CustomUI
{
    internal static class StorageUiHost
    {
        // ---- 反射缓存（原版 private static 字段/方法） ----
        private static FieldInfo fItems;
        private static FieldInfo fHeaders;
        private static FieldInfo fAreaBox;
        private static FieldInfo fClothingBox;

        private static bool reflectionReady;
        private static bool reflectionFailedLogged;

        // ---- 当前会话状态 ----
        private static bool isOpen;
        private static IStorageUiProvider activeProvider;
        private static StorageUiContext activeContext;
        private static ISleekElement panel;
        private static ISleekElement attachedParent;
        private static bool narrowScreenWarned;

        // ---- 临时提示（顶在标题栏上几秒）----
        // ★ 26.10.3.38：公开面 StorageUiPanels.Notify 的落地处。以前"把拒绝原因顶到界面"
        //   只有借道那一句**硬编码**，第三方拦掉了自己的动作却没法告诉玩家为什么。
        private static string noticeText;
        private static float noticeUntil;              // Time.realtimeSinceStartup 的绝对时刻
        private const float DEFAULT_NOTICE_SECONDS = 6f;

        // ---- 右列区块栈（箱内物品 / 任意多块 / 附近物品）----
        // 每个区块也是一个 Provider（复用它的 Open/Close/Tick/Rebuild 与常驻面板机制），
        // 只是**不抢存储槽位**：它把面板交给宿主，宿主按 order 从上往下叠，
        // 整栈占掉原版「储存物品」那一行的垂直空间 —— 附近物品由原版自己排到最下面。
        private class RegionSlot
        {
            internal string Id;
            internal IStorageUiProvider Provider;
            internal ISleekElement Panel;     // 上一次挂上去的面板（常驻，只藏不拆）
            internal int Order;
            internal bool Keep;               // 本轮 SyncRegions 里还要不要（差分用）
            internal bool Failed;             // RegionPanel 抛过异常（日志只报一次）
        }
        private static readonly List<RegionSlot> slots = new List<RegionSlot>();
        private static bool regionRefresh;    // 外部请求重算区块栈
        private static int regionSyncFailures; // 连续打开失败的区块数（只在日志里出现一次时说明问题）

        // 排版复用的缓冲（避免每帧产生垃圾）
        private class StackItem
        {
            internal ISleekElement Panel;
            internal int Order;
            internal int Tie;                 // 同 order 时按收集顺序（稳定）
            internal RegionSlot Slot;         // null = 主面板（存储视图）
        }
        private static readonly List<StackItem> stackBuf = new List<StackItem>();

        // ---- 接管期间对原版存储区的临时改写（关闭时必须逐项还原） ----
        private static bool overrideApplied;
        private static string originalHeaderText;
        private static float originalItemsHeight;

        /// <summary>
        /// ★★ 26.10.3.67：**原版存储网格与我们并存**（默认 `false` = 现状：接管时把网格藏起来）。
        ///
        /// <para>为什么它是"可选"：接管这一侧时，我们**冒名顶替**原版网格的**高度槽位** ——
        /// 把 `grid.SizeOffset_Y` 写成"我们整栈占的高度"，原版据此把「附近物品」排到我们下面。
        /// 这是我们与**原版自己的布局引擎**（`updateBoxAreas`）唯一的接口。</para>
        ///
        /// <para>打开后改成"一分为二"：原版网格**保持可见**（玩家能照原版方式拖放），
        /// 高度槽位 = `原版高度 + 间隔 + 我们高度`，我们的整栈整体下移到网格下面。</para>
        ///
        /// ⚠ 代价（知道再开）：网格元素被拉高 ⟹ 它**多出来的那段会画出空槽轮廓**，
        /// 而它正好落在我们面板的位置 ⟹ 面板底色**如果不透明**就完全盖住（看不出来），
        /// 若面板半透明则会隐约透出格子。所以这是一个**观感取舍**，默认关。</remarks>
        /// </summary>
        internal static bool KeepVanillaGrid;

        /// <summary>并存模式下，原版网格 + 间隔占掉的高度（没在并存/没有快照时 = 0）。</summary>
        private static float VanillaGridBlockHeight()
        {
            if (!KeepVanillaGrid) return 0f;
            if (!overrideApplied) return 0f;
            float h = (originalItemsHeight > 0f) ? originalItemsHeight : 0f;
            return (h > 0f) ? (h + VANILLA_GRID_GAP) : 0f;
        }

        /// <summary>并存模式下原版网格与我们的栈之间的间隔。</summary>
        private const float VANILLA_GRID_GAP = 6f;

        internal static bool IsOpen { get { return isOpen; } }

        /// <summary>
        /// ★ 26.10.3.37：当前占存储槽位的那个 Provider 的 Id（没人占时 null）。
        /// 给公开面 <see cref="StorageUiPanels.ActiveProviderId"/> 用 ——
        /// 接入方靠它回答"这次占着的是我，还是别人"（同族状态查询见 <see cref="IsRegionAttached"/>）。
        /// </summary>
        internal static string ActiveProviderId
        {
            get { return (activeProvider != null) ? activeProvider.Id : null; }
        }

        /// <summary>
        /// ★ 26.10.3.38：现在**该显示**的那条临时提示（没有 / 已过期时返回 null）。
        /// 顺带做过期清理 —— 免得调用方还要自己判时间。
        /// ⚠ 不要缓存它的返回值（下一帧可能就过期了）。
        /// </summary>
        internal static string Notice
        {
            get
            {
                if (noticeText == null) return null;
                if (Time.realtimeSinceStartup >= noticeUntil)
                {
                    noticeText = null;
                    return null;
                }
                return noticeText;
            }
        }

        /// <summary>
        /// ★ 26.10.3.38：发一条临时提示（<paramref name="seconds"/> &lt;= 0 用默认 6 秒）。
        ///
        /// ⚠ 发完**主动重排一次**：标题栏是在 <see cref="ApplyVanillaOverride"/> 里写的，
        ///   而它只在排版时被调 —— 光设个字段、等"恰好下一次排版"，提示可能迟迟不出现。
        ///   这里调 <see cref="ForceRelayout"/> 把"发完就能看见"变成**机制保证**而不是巧合
        ///   （界面没开着时补丁自己 `return`，所以它是安全的）。
        /// </summary>
        internal static void SetNotice(string text, float seconds)
        {
            if (string.IsNullOrEmpty(text))
            {
                ClearNotice();
                return;
            }
            if (seconds <= 0f) seconds = DEFAULT_NOTICE_SECONDS;
            noticeText = text;
            noticeUntil = Time.realtimeSinceStartup + seconds;
            ForceRelayout();
        }

        /// <summary>★ 26.10.3.38：立刻收掉提示（关界面时必须调，否则下次开会话会看到旧提示）。</summary>
        internal static void ClearNotice()
        {
            noticeText = null;
            noticeUntil = 0f;
        }

        // 存储区索引：STORAGE(7) - SLOTS(2) = 5；附近物品区：AREA(8) - SLOTS(2) = 6
        private static readonly int StorageIndex = PlayerInventory.STORAGE - PlayerInventory.SLOTS;
        private static readonly int AreaIndex = PlayerInventory.AREA - PlayerInventory.SLOTS;

        // ================= 反射准备 =================

        private static void EnsureReflection()
        {
            if (reflectionReady) return;
            try
            {
                Type t = typeof(PlayerDashboardInventoryUI);
                const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Static;

                fItems = t.GetField("items", BF);
                fHeaders = t.GetField("headers", BF);
                fAreaBox = t.GetField("areaBox", BF);
                fClothingBox = t.GetField("clothingBox", BF);

                if (fItems == null || fHeaders == null || fAreaBox == null)
                {
                    if (!reflectionFailedLogged)
                    {
                        reflectionFailedLogged = true;
                        CustomUIPlugin.Warn("反射失败：原版字段缺失（items=" + (fItems != null) +
                            " headers=" + (fHeaders != null) + " areaBox=" + (fAreaBox != null) +
                            "）→ 界面接管不可用，全部回退原版");
                    }
                    return;
                }
                reflectionReady = true;
                CustomUIPlugin.Log("反射就绪（items / headers / areaBox / clothingBox 全部命中）");
            }
            catch (Exception e)
            {
                if (!reflectionFailedLogged)
                {
                    reflectionFailedLogged = true;
                    CustomUIPlugin.Error("反射异常：" + e);
                }
            }
        }

        private static SleekItems[] GetItemsArray()
        {
            if (fItems == null) return null;
            return fItems.GetValue(null) as SleekItems[];
        }

        private static ISleekButton[] GetHeadersArray()
        {
            if (fHeaders == null) return null;
            return fHeaders.GetValue(null) as ISleekButton[];
        }

        private static ISleekScrollView GetAreaBox()
        {
            if (fAreaBox == null) return null;
            return fAreaBox.GetValue(null) as ISleekScrollView;
        }

        // ================= 隐藏 / 恢复原版存储区 =================

        /// <summary>
        /// 接管原版存储区。**只藏物品网格、保留「储存物品」标题**，并把网格高度
        /// 改成面板高度。幂等，可在每帧以及 updateBoxAreas 的 Prefix 里反复调用。
        ///
        /// ⚠ 为什么「留标题、只藏网格」是必须的（本插件最容易做错的一处）：
        ///   updateBoxAreas 的布局累加**只判 headers[index].IsVisible**（:1849），
        ///   高度却取自 items[index].SizeOffset_Y（:1866，完全不看 items 的可见性）。
        ///   若把标题一起藏掉，原版就不给这一项分配空间 → 「附近物品」区上移，
        ///   面板只能接在它下方。而原版右列顺序是「存储区在上、附近物品在下」，
        ///   结果箱子内容跑到右列最底部（要滚动才看得到），位置与预期不符。
        ///   保留标题 → 原版照常给这一项分配空间 → 面板稳稳占据原网格的位置。
        ///
        /// 顺带：这个标题栏就是「储存物品」，因此本方法同时是「魔改上方栏目」的宿主。
        /// </summary>
        /// <returns>
        /// 是否发生了「会影响后续项排版」的改动（目前只有网格高度）。返回 true 时，
        /// 位于本项下方的「附近物品」区还没挪位 —— 调用方需重算一次布局；
        /// 若调用点本身就在 updateBoxAreas 的 Prefix（原版紧接着就要排版），可忽略。
        /// </returns>
        internal static bool ApplyVanillaOverride()
        {
            if (!CustomUIPlugin.Enabled) return false;
            EnsureReflection();
            if (!reflectionReady) return false;
            try
            {
                SleekItems[] items = GetItemsArray();
                ISleekButton[] headers = GetHeadersArray();
                if (items == null || headers == null) return false;
                if (StorageIndex < 0 || StorageIndex >= items.Length || StorageIndex >= headers.Length) return false;

                SleekItems grid = items[StorageIndex];
                ISleekButton head = headers[StorageIndex];
                if (grid == null || head == null) return false;

                // 首次接管：记下原值，供关闭时逐项还原
                if (!overrideApplied)
                {
                    overrideApplied = true;
                    originalHeaderText = head.Text;
                    originalItemsHeight = grid.SizeOffset_Y;
                }

                // ① 网格可见性：
                //    · 默认（替换）：**藏起来** —— 我们整栈直接占它原来那一行（标题保留，理由见方法注释）。
                //    · `KeepVanillaGrid`（并存，26.10.3.67）：**保持可见** —— 玩家仍能照原版方式拖放，
                //      我们整栈下移到它下面（起点与高度记账两处都跟着改）。
                grid.IsVisible = KeepVanillaGrid;

                // ② 网格高度 = 「原版自己那一块（仅并存时）」+「我们占了这一行多少空间」：
                //    物品面板 + 下方区块（含两者之间的间隔）—— 原版据此给「附近物品」排版，
                //    面板变高变矮都不必去动别人。
                //    高度变化会影响后续项的 y，因此要报告出去让调用方重排。
                //  ⚠ 这是我们与原版布局引擎（`updateBoxAreas`）**唯一的接口**：
                //    不改它，「附近物品」不会给我们让位。
                float occupied = OccupiedHeight();
                float target = VanillaGridBlockHeight() + occupied;
                bool heightChanged = false;
                if (target > 0f && Mathf.Abs(grid.SizeOffset_Y - target) > 0.5f)
                {
                    grid.SizeOffset_Y = target;
                    heightChanged = true;
                }

                // ③ 魔改标题栏文字（只改文字，不影响布局）
                string title = (activeContext != null) ? activeContext.Title : null;
                // ★ 26.10.3.38：提示有两路，**公开那路优先** ——
                //   ① 第三方（或本插件）通过 StorageUiPanels.Notify 发的；
                //   ② 借道期间玩家试着往中转页里放东西时，本插件那句兜底说明。
                //   同一时刻只显示一条：后发的盖住先发的（提示就该"谁后说谁显示"）。
                string note = Notice;
                if (note == null && activeContext != null && activeContext.IsVirtual)
                {
                    note = StorageUiShared.RelayDepositNote();
                }
                if (note != null) title = (title == null ? "" : title) + " · " + note;
                if (!string.IsNullOrEmpty(title) && head.Text != title)
                {
                    head.Text = title;
                }

                return heightChanged;
            }
            catch (Exception e)
            {
                CustomUIPlugin.Warn("接管原版存储区失败：" + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 交还控制权给原版：把接管时改写过的项还原，再重算布局。
        /// 关闭接管时必须调用，否则原版存储网格会一直保持隐藏。
        ///
        /// ⚠ 可见性**不能**恢复成「接管那一刻的快照」，必须按原版规则来算：
        ///   onInventoryResized:1801/1804 设的是 `IsVisible = newHeight > 0`。
        ///   若照搬快照（接管时必然是 true），那么「箱子已关/已空」时还原出
        ///   一个可见但无内容的存储区，与之后的原版状态自相矛盾。
        ///   高度同理：只在箱子仍打开时写回快照，否则留给原版下次 resize 决定。
        /// </summary>
        internal static void RestoreVanillaStorage()
        {
            EnsureReflection();
            if (!reflectionReady) return;
            try
            {
                if (!overrideApplied) return;   // 没接管过就不要去写原版元素

                SleekItems[] items = GetItemsArray();
                ISleekButton[] headers = GetHeadersArray();
                if (items == null || headers == null) return;
                if (StorageIndex < 0 || StorageIndex >= items.Length || StorageIndex >= headers.Length) return;

                SleekItems grid = items[StorageIndex];
                ISleekButton head = headers[StorageIndex];

                bool stillOpen = (grid != null) && (grid.height > 0);

                if (grid != null)
                {
                    grid.IsVisible = stillOpen;
                    if (stillOpen) grid.SizeOffset_Y = originalItemsHeight;
                }
                if (head != null)
                {
                    head.IsVisible = stillOpen;
                    head.Text = originalHeaderText;
                }

                overrideApplied = false;
                TriggerUpdateBoxAreas();
            }
            catch (Exception e)
            {
                CustomUIPlugin.Warn("恢复原版存储区失败：" + e.Message);
            }
        }

        /// <summary>
        /// 主动触发原版布局重算。接管瞬间必须调一次：那时原版已经按「存储区可见」
        /// 算过布局了，不重算会留下空白。
        /// </summary>
        internal static void ForceRelayout()
        {
            try
            {
                MethodInfo mi = AccessTools.Method(typeof(PlayerDashboardInventoryUI), "updateBoxAreas");
                if (mi != null) mi.Invoke(null, null);
            }
            catch (Exception) { /* 布局重算失败不致命，下一帧原版自己会算 */ }
        }

        private static void TriggerUpdateBoxAreas()
        {
            ForceRelayout();
        }

        // ================= 窄屏判据 =================

        /// <summary>
        /// 右列（areaBox）是否可用。判据与原版 isSplitClothingArea 完全一致
        /// （PlayerDashboardInventoryUI.cs:129 `Screen.width >= 1350`）。
        /// 常数值故意硬编码 —— 原版是表达式属性、无法反射读取，若跟随更新改动
        /// 会在窄屏上表现为「界面一片空白」，故在此显式注释来源。
        /// </summary>
        internal static bool IsWideEnoughForRightColumn()
        {
            return Screen.width >= 1350;
        }

        private static void WarnNarrowScreenOnce()
        {
            if (narrowScreenWarned) return;
            narrowScreenWarned = true;
            CustomUIPlugin.Warn("屏幕宽度 " + Screen.width + " < 1350：原版为单列布局、" +
                "右列不存在 → 本次不接管，回退原版存储区。" +
                "请在更宽的窗口/分辨率下使用自定义界面。");
        }

        // ================= 面板挂载与定位 =================

        /// <summary>区块之间的间隔。</summary>
        private const float REGION_GAP = 8f;

        // ================= 区块栈：收集 / 高度 / 排版 =================

        private static readonly List<ISleekElement> counted = new List<ISleekElement>();

        /// <summary>
        /// 我们在原版「存储行」里实际占掉的高度 = 栈里每一块的高度 + 块之间的间隔。
        /// 原版 updateBoxAreas 拿这个高度排版（:1866 累加 items[].SizeOffset_Y），
        /// 所以「附近物品」会自然落到最下面，我们不需要自己去挪别人的元素。
        ///
        /// ⚠⚠ 同一块面板只算一次：视图的面板是**常驻复用**的，在插件管理里把
        ///   Placement 从 below 点成 replace 时，同一块面板会从「区块」变成「主面板」。
        ///   算两遍 = 原版按 2 倍高度留位 → 实测「附近物品上方一大段空位」（README 坑 #41）。
        ///   这里按引用去重，不依赖「谁先把身份换掉」的时序。
        /// </summary>
        private static float OccupiedHeight()
        {
            // 先清：ApplyVanillaOverride 会被原版的 updateBoxAreas（我们的补丁 Prefix）
            // 重入调用，中途出异常时这里可能留着上一次的残留 —— 不清就会多算一块。
            counted.Clear();
            CountVisible(panel);
            for (int i = 0; i < slots.Count; i++) CountVisible(slots[i].Panel);

            float h = 0f;
            for (int i = 0; i < counted.Count; i++)
            {
                if (i > 0) h += REGION_GAP;
                h += counted[i].SizeOffset_Y;
            }
            counted.Clear();
            return h;
        }

        /// <summary>可见性参与累加（区块被藏起来时不该继续占空间，否则右侧留空白）。</summary>
        private static void CountVisible(ISleekElement p)
        {
            if (p == null || !p.IsVisible) return;
            if (counted.Contains(p)) return;      // ← 同一块面板只算一次
            counted.Add(p);
        }

        /// <summary>原版存储网格该在的位置（右列靠上、「附近物品」之上）—— 栈的第一块放这里。</summary>
        private static float StorageRowTopY()
        {
            SleekItems[] items = GetItemsArray();
            if (items != null && StorageIndex >= 0 && StorageIndex < items.Length
                && items[StorageIndex] != null)
            {
                return items[StorageIndex].PositionOffset_Y;
            }
            return 0f;
        }

        private static void AddStackItem(ISleekElement p, int order, RegionSlot slot)
        {
            if (p == null) return;
            for (int i = 0; i < stackBuf.Count; i++)
            {
                if (ReferenceEquals(stackBuf[i].Panel, p)) return;   // 同一块面板只占一格
            }
            StackItem it = new StackItem();
            it.Panel = p;
            it.Order = order;
            it.Slot = slot;
            it.Tie = stackBuf.Count;
            stackBuf.Add(it);
        }

        /// <summary>
        /// 右列排版：把「箱内物品面板 + 所有区块」按 order 从上往下叠，
        /// 再把整栈高度报给原版（原版据此把「附近物品」排到栈下面）。
        ///
        /// ⚠ 每一块只做「挂 + 定位 + SetActive」，**绝不 RemoveChild**
        ///   （= GameObject.Destroy → 毁掉常驻行池与已渲染图标，见 README §4.7）。
        /// ⚠ 先定位、后改行高是安全的：存储网格的 y 只取决于**它上面**那些项
        ///   （updateBoxAreas 按 index 升序累加），跟本行的高度无关。
        /// </summary>
        private static void LayoutStack()
        {
            EnsureReflection();
            if (!reflectionReady) return;

            try
            {
                ISleekScrollView area = GetAreaBox();
                if (area == null) return;
                // 窄屏（右列不存在）：AttachAndPlacePanel 里已经走过回退，这里不硬挂
                if (!area.IsVisible) return;

                // ⚠ 主面板没就位时**整栈都不排版**：整栈以它为锚（起点 = 它该在的那一行），
                //   早一帧挂会把区块摆到存储位上。会走到这里的真实时机只有一个：
                //   TryOpen 里主视图刚 Open 完、它的 Tick 还没跑（panel 仍是上一世藏起来的那块），
                //   而 SyncRegions 末尾已经要排版了 —— 少这一道守卫就是「区块闪一帧错位」。
                if (panel == null || !panel.IsVisible) return;

                // ① 收集这一帧要显示的块（主面板 + 各区块），各带生效顺序
                stackBuf.Clear();
                AddStackItem(panel,
                    StorageUiRegistry.EffectiveRegionOrder(activeProvider, activeContext), null);

                for (int i = 0; i < slots.Count; i++)
                {
                    RegionSlot s = slots[i];
                    ISleekElement p = RegionPanelOf(s);
                    if (p == null)
                    {
                        // Provider 这一帧没有内容 → 让位（只藏，不销毁；下次有内容再显示）
                        if (s.Panel != null)
                        {
                            try { s.Panel.IsVisible = false; } catch (Exception) { }
                        }
                        continue;
                    }
                    s.Panel = p;
                    s.Order = StorageUiRegistry.EffectiveRegionOrder(s.Provider, activeContext);
                    AddStackItem(p, s.Order, s);
                }

                if (stackBuf.Count == 0) return;

                // ② order 升序；同 order 用收集顺序打破平局
                //   （List.Sort 不保证稳定，所以平局必须显式判，否则同一帧内顺序会跳）
                stackBuf.Sort(delegate (StackItem a, StackItem b)
                {
                    int c = a.Order.CompareTo(b.Order);
                    if (c != 0) return c;
                    return a.Tie.CompareTo(b.Tie);
                });

                // ③ ★ 先跟原版**对账**（藏/留网格 + 高度 + 标题），再算 y ——
                //    并存模式下我们的起点要**跳过「原版网格 + 间隔」那一段**，而那个量正是
                //    这次对账确定的（`overrideApplied` 与 `originalItemsHeight` 快照都在这落定）。
                //    ⚠ 顺序反了的话，**接管的第一帧**会拿上一次箱子的高度去定位（一帧错位）。
                bool vanillaHeightChanged = ApplyVanillaOverride();

                // ④ 挂进右列 + 从上往下定位
                float y = StorageRowTopY() + VanillaGridBlockHeight();
                for (int i = 0; i < stackBuf.Count; i++)
                {
                    StackItem it = stackBuf[i];
                    // 只认「当前父节点是不是右列」—— 面板自己带着这个事实，
                    // 不必在槽位里再记一份（记了就是第二个真相，迟早和面板实际挂处不一致）。
                    if (!ReferenceEquals(it.Panel.Parent, area)) area.AddChild(it.Panel);

                    if (i > 0) y += REGION_GAP;
                    it.Panel.IsVisible = true;
                    it.Panel.PositionOffset_Y = y;
                    y += it.Panel.SizeOffset_Y;
                }

                // ⑤ 整栈高度已经交给原版（③）；它说高度变了 → 「附近物品」还停在旧位置，重排一次
                if (vanillaHeightChanged) TriggerUpdateBoxAreas();

                // ⑥ 滚动范围要罩住整栈，否则滚不到「附近物品」
                //   （原版会按新行高重算，这里取 max 只为兜住重算前的那一帧）
                float needed = y + 10f;
                if (area.ContentSizeOffset.y < needed)
                {
                    area.ContentSizeOffset = new Vector2(area.ContentSizeOffset.x, needed);
                }
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("右列区块排版失败：" + e);
            }
        }

        /// <summary>取区块面板（判据抛异常时当作「这一帧没有内容」，并只报一次日志）。</summary>
        private static ISleekElement RegionPanelOf(RegionSlot s)
        {
            IStorageUiRegion r = s.Provider as IStorageUiRegion;
            if (r == null) return null;
            try
            {
                ISleekElement p = r.RegionPanel(activeContext);
                if (p != null) s.Failed = false;
                return p;
            }
            catch (Exception e)
            {
                if (!s.Failed)
                {
                    s.Failed = true;
                    CustomUIPlugin.Error("区块 " + s.Id + " 的 RegionPanel 抛异常，本帧不显示：" + e.Message);
                }
                return null;
            }
        }

        /// <summary>
        /// 重算区块栈（**差分**，不是一律拆了重建）：
        ///   · 新出现的候选 → Open；失败只 warn（少一块不影响主界面接管）
        ///   · 还在的 → 原样复用（重开 = 白毁一次常驻面板与图标，见 §4.7）
        ///   · 消失的 → Close + 摘掉槽位
        /// </summary>
        private static void SyncRegions(StorageUiContext ctx, IStorageUiProvider main)
        {
            regionRefresh = false;
            if (ctx == null) return;

            List<IStorageUiProvider> wanted = StorageUiRegistry.FindRegions(ctx, main);

            for (int i = 0; i < slots.Count; i++) slots[i].Keep = false;

            for (int i = 0; i < wanted.Count; i++)
            {
                IStorageUiProvider cand = wanted[i];
                RegionSlot s = FindSlot(cand);
                if (s != null)
                {
                    s.Keep = true;
                    s.Order = StorageUiRegistry.EffectiveRegionOrder(cand, ctx);
                    continue;
                }

                bool ok;
                try
                {
                    ok = cand.Open(ctx);
                }
                catch (Exception e)
                {
                    CustomUIPlugin.Error("区块 " + cand.Id + " 的 Open 抛异常：" + e);
                    ok = false;
                }

                if (!ok)
                {
                    regionSyncFailures++;
                    CustomUIPlugin.Warn("右列区块 " + cand.Id + " 打开失败，右侧少一块（累计 " +
                        regionSyncFailures + " 次）");
                    continue;
                }

                s = new RegionSlot();
                s.Id = cand.Id;
                s.Provider = cand;
                s.Keep = true;
                s.Order = StorageUiRegistry.EffectiveRegionOrder(cand, ctx);
                slots.Add(s);
                CustomUIPlugin.Log("右列区块 + " + cand.Id + "（顺序 " + s.Order + "）");
            }

            for (int i = slots.Count - 1; i >= 0; i--)
            {
                RegionSlot s = slots[i];
                if (s.Keep) continue;
                CloseSlot(s);
                slots.RemoveAt(i);
                CustomUIPlugin.Log("右列区块 - " + s.Id);
            }

            LayoutStack();
        }

        private static RegionSlot FindSlot(IStorageUiProvider provider)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                RegionSlot s = slots[i];
                if (s.Id != provider.Id) continue;
                if (ReferenceEquals(s.Provider, provider)) return s;

                // 同名但换了实例（插件重新注册）→ 旧的必须关掉，
                // 否则它的常驻面板还挂在右列上，而我们再也不驱动它。
                CloseSlot(s);
                slots.RemoveAt(i);
                return null;
            }
            return null;
        }

        private static void CloseSlot(RegionSlot s)
        {
            try { s.Provider.Close(); }
            catch (Exception e) { CustomUIPlugin.Error("区块 " + s.Id + " Close 异常：" + e.Message); }
            if (s.Panel != null)
            {
                // 只藏不拆（RemoveChild = Destroy）
                try { s.Panel.IsVisible = false; } catch (Exception) { }
            }
        }

        private static void TickRegions(StorageUiContext ctx)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                RegionSlot s = slots[i];
                try { s.Provider.Tick(ctx); }
                catch (Exception e) { CustomUIPlugin.Error("区块 " + s.Id + " Tick 异常：" + e.Message); }
            }
        }

        /// <summary>外部请求重算区块栈（下一帧生效）。幂等、廉价。</summary>
        internal static void RequestRegionRefresh()
        {
            regionRefresh = true;
        }

        /// <summary>
        /// 某个 Provider 被注销了：宿主必须立刻放手，否则右列会留下一块没人驱动的死面板。
        /// 正占存储槽位的那个被注销 → 整个接管收回（回退原版界面）。
        /// </summary>
        internal static void DropProvider(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                if (activeProvider != null && activeProvider.Id == id)
                {
                    if (isOpen)
                    {
                        // 正占着存储位的那个走了 → 界面没有主人，整个收回（回退原版网格）
                        CustomUIPlugin.Warn("界面提供者 " + id + " 被注销 → 收回接管，回退原版界面");
                        Close();
                    }
                    return;
                }
                for (int i = slots.Count - 1; i >= 0; i--)
                {
                    if (slots[i].Id != id) continue;
                    CloseSlot(slots[i]);
                    slots.RemoveAt(i);
                }
                if (isOpen) LayoutStack();
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("注销区块时出错：" + e.Message);
            }
        }

        /// <summary>
        /// ★ 26.10.3.37：某个区块是不是**真的挂上了**（给公开面
        /// <see cref="StorageUiPanels.IsRegionAttached"/>）。按 Provider Id 查。
        ///
        /// ⚠ 语义边界（写清楚免得被误读）：判的是"这个区块**交出过面板、且没抛过异常**",
        ///   不是"这一帧一定看得见" —— 面板可能因为"这一帧没有内容"被临时藏起来
        ///   （布局里那句「让位，只藏不拆」，<see cref="RegionSlot.Panel"/> 会保留旧值）。
        ///   想判"此刻可见"得自己去读那块面板的 <c>IsVisible</c>。
        /// ⚠ 「在 slots 里」**不等于**挂上了：<c>WantsRegion</c> 说过要它、但
        ///   <c>RegionPanel</c> 还没被调到（或抛了异常）时，它就是"占着槽位却没面板"。
        ///   —— 这正是接入方光看 <c>WantsRegion</c> 返回值判断不出来的那件事。
        /// </summary>
        internal static bool IsRegionAttached(string providerId)
        {
            if (string.IsNullOrEmpty(providerId)) return false;
            for (int i = 0; i < slots.Count; i++)
            {
                RegionSlot s = slots[i];
                if (s == null || s.Id != providerId) continue;
                return s.Panel != null && !s.Failed;
            }
            return false;
        }

        /// <summary>
        /// 把面板挂进原版右列（areaBox）并让它占据**原版存储网格该在的位置**。
        /// 定位本身交给 <see cref="LayoutStack"/>（它才知道栈里还有谁）。
        /// ⚠ 关卡切换会销毁整个 PlayerUI（连同其中元素），必须按「当前父节点」
        ///   判断是否需要重挂：旧元素的 Parent 引用仍非空，但已随旧画布销毁。
        /// </summary>
        internal static void AttachAndPlacePanel(ISleekElement newPanel)
        {
            if (newPanel == null) return;
            EnsureReflection();
            if (!reflectionReady) return;

            try
            {
                ISleekScrollView area = GetAreaBox();
                if (area == null) return;

                // 兜底：右列不可见（TryOpen 已按 Screen.width 拦过窄屏，
                // 正常不会走到这里；窗口在打开期间被缩窄等情形可能触发）。
                // ⚠ 此时**不能**简单 return：原版存储区已被 ApplyVanillaOverride
                //   藏掉、面板又挂不上 → 用户看到一片空白，且日志毫无线索。
                //   必须真正回退：Close() 会拆面板 + 恢复原版存储区。
                //   重入是安全的 —— 调用方（ListView.Tick）在 Close 后 panel
                //   已置 null，后续 Rebuild 会立即 return。
                if (!area.IsVisible)
                {
                    WarnNarrowScreenOnce();
                    Close();
                    return;
                }

                if (!ReferenceEquals(panel, newPanel))
                {
                    if (panel != null)
                    {
                        // ⚠ 不 RemoveChild！那是 Destroy（见 DetachPanel 注释），
                        //   会把**另一个视图**的常驻面板连带行池、图标贴图一起销毁。
                        //   这里只是「宿主不再管它」：藏起来，交给那个视图自己决定
                        //   复用还是丢弃（它下次 Open 时用 CurrentAttachSpot 自检）。
                        try { panel.IsVisible = false; } catch (Exception) { }
                    }
                    panel = newPanel;
                }
                panel.IsVisible = true;

                LayoutStack();
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("挂载/定位面板失败：" + e);
            }
        }

        /// <summary>挂到 PlayerUI.container（完全独立界面用，不依赖背包页）。</summary>
        internal static void AttachPanelToPlayerUI(ISleekElement newPanel)
        {
            if (newPanel == null) return;
            try
            {
                if (PlayerUI.container == null)
                {
                    CustomUIPlugin.Warn("PlayerUI.container 为 null → 无法挂载独立面板");
                    return;
                }
                if (!ReferenceEquals(attachedParent, PlayerUI.container) || !ReferenceEquals(panel, newPanel))
                {
                    if (panel != null && !ReferenceEquals(panel, newPanel))
                    {
                        // 同 AttachAndPlacePanel：藏起来即可，**别 RemoveChild**（= Destroy）
                        try { panel.IsVisible = false; } catch (Exception) { }
                    }
                    panel = newPanel;
                    PlayerUI.container.AddChild(panel);
                    attachedParent = PlayerUI.container;
                }
                panel.IsVisible = true;
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("挂载独立面板失败：" + e);
            }
        }

        /// <summary>
        /// 交还界面：**只把面板藏起来，不摘除**。
        ///
        /// ⚠ 为什么不能摘：Glazier 的 `RemoveChild` 会 `GameObject.Destroy` 整个子树
        ///   （SleekWrapper.RemoveChild）→ 面板与行池、连同已经异步渲染好的图标贴图全没。
        ///   而我们的图标是**统一尺寸 + 带 state**，不满足原版
        ///   `ItemTool.getIcon` 的缓存判据（`state.Length==0 && x==size_x*50 && y==size_y*50`）
        ///   → 进不了 `ItemTool.iconCache`，每次 Refresh 都要重新排队渲染。
        ///   拆掉重建的实测表现就是「**每重开一次箱子，所有图标重新加载**」。
        ///   `IsVisible=false` 走的是 `gameObject.SetActive(false)`，元素与贴图都留着，
        ///   下次打开由视图自己复用（见各视图 Open 里的 IsParkUsable）。
        ///
        /// ⚠ 这里**故意不清** panel：同一视图下次打开时，AttachAndPlacePanel 认出
        ///   「还是这块面板」（LayoutStack 再按 Parent 判要不要重挂）→
        ///   不重挂、只 SetActive(true)，一步到位。
        /// </summary>
        internal static void DetachPanel()
        {
            try
            {
                if (panel != null) panel.IsVisible = false;
            }
            catch (Exception) { }
        }

        /// <summary>
        /// ★ 26.10.3.31：第三方主动摘掉**自己挂的**那块面板
        /// （<see cref="StorageUiPanels.Detach"/> 的宿主侧实现）。
        ///
        /// ⚠ 只藏不拆：绝不 `RemoveChild`（那是 `GameObject.Destroy`，会毁掉对方常驻的
        ///   控件池与已渲染的图标贴图 —— 见 DetachPanel 上面那段）。
        /// ⚠ **只认引用相同的那一块**：否则 A 插件能把 B 插件的面板摘下架
        ///   （与"归还借来的引擎状态时要比对引用"是同一条纪律）。
        /// ⚠ 若它正是宿主记着的主面板，顺手清掉引用 —— 否则下次 `AttachAndPlacePanel`
        ///   会认出"还是这块"、把它重新 `SetActive(true)`，等于没摘。
        /// </summary>
        internal static void DetachExternalPanel(ISleekElement target)
        {
            if (target == null) return;
            try { target.IsVisible = false; } catch (Exception) { }
            try { if (ReferenceEquals(target, panel)) panel = null; } catch (Exception) { }
        }

        /// <summary>★ 26.10.3.31：上一次接管/借道失败的原因（给 <see cref="StorageUiPanels.LastOpenReject"/>）。</summary>
        internal static string LastOpenReject { get { return lastOpenReject; } }

        /// <summary>
        /// 当前的挂载点（原版右列 areaBox）。视图用它判断「常驻的面板还挂在活着的父节点上吗」：
        /// 切关/换服会重建整个 PlayerUI，那时旧面板已随旧画布销毁，
        /// 但它的 Parent 字段仍指向**那个旧的** areaBox 实例 —— 一比对就能识破，
        /// 从而丢弃常驻、重建一块新的（而不是往已销毁的控件上写）。
        /// </summary>
        internal static ISleekElement CurrentAttachSpot()
        {
            EnsureReflection();
            if (!reflectionReady) return null;
            try
            {
                return GetAreaBox();
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ================= 打开 / 关闭 =================

        /// <summary>
        /// 配置在运行时被改了（例如在插件管理面板里点击切换视图）→ 界面正开着就**就地换视图**。
        ///
        /// 做法是「关掉再用同一份上下文重开」，而不是直接替换 provider 对象：
        ///   · <c>Close()</c> 会把该还原的原版区域还原（被藏起来的网格/标题、滚动能范围），
        ///     直接换对象会留下上一任改过没还原的状态；
        ///   · 重开走的是与按 F 完全相同的一条接管路径，不需要第二份接管逻辑。
        /// 因为面板已改成常驻（README §4.7），重开**不会**重载图标，所以这一步很便宜。
        /// ⚠ 调用方只在「配置值发生变化」这个边沿上调一次，不是每帧 → 不会来回重开。
        /// </summary>
        internal static void ReloadForConfigChange()
        {
            if (!isOpen || activeContext == null) return;

            Items container = activeContext.Container;
            InteractableStorage storage = activeContext.Storage;
            BarricadeDrop drop = activeContext.Drop;
            string from = (activeProvider != null) ? activeProvider.Id : "?";
            // ⚠ 借道的虚拟存储**不能**走"关了重开"：Close 会把那个 trunk 归还掉
            //   （closeStorage → 存储页换成新的空 Items），再 TryOpen 就等于把面板
            //   挂在一个已经脱离背包的页上 —— 内容看着在、点下去没有。
            bool wasVirtual = (container != null && IsVirtualFor(container));

            Close();

            if (container == null) return;   // 上下文已失效（箱子被收走），不重开
            if (wasVirtual)
            {
                CustomUIPlugin.Log("配置变更：借道的虚拟存储已归还，不重开（下次调用方自己再开）");
                return;
            }

            TryOpen(container, storage, drop);

            string to = isOpen && activeProvider != null ? activeProvider.Id : "原版界面";
            CustomUIPlugin.Log("视图已热切换：" + from + " → " + to);
        }

        /// <summary>
        /// 询问注册表：这次打开的储物由谁处理。返回 true = 已接管。
        /// ⚠ container 必须传 inventory.items[STORAGE]（权威数据源），
        ///   storage 只作辅助信息（联机客户端上为 null）。
        /// </summary>
        /// <summary>
        /// 插件借道的虚拟存储（走 <c>openTrunk</c>）。
        /// ⚠ 只登记**调用方给的那个 Items 实例**，接管判据靠引用相等认出"这次 trunk 是我借的道"。
        ///   界面关闭时必须清掉 —— 否则之后玩家打开真的车后备箱也会被接管。
        /// </summary>
        private static Items virtualItems;
        private static string virtualTitle;

        /// <summary>
        /// 这次借道是谁开的（由 <c>OpenVirtualStorage(items, title, tag)</c> 传进来）。
        /// 本插件**不解释**它，只原样放进 <c>StorageUiContext.Tag</c> ——
        /// 让调用方在自己的 CanHandle 里认出"这是我这次开的会话"。
        /// </summary>
        private static object virtualTag;

        /// <summary>
        /// 这次借道声明的"这一页是什么性质"（见 <see cref="StorageUiVirtualPage"/>）。
        /// ⚠ 必须与 <see cref="virtualItems"/> <b>同生同灭</b>：只清 items 不清它，
        ///   下一次没声明的会话会继承上一次的口径（那正是这条改动要消除的"靠猜"）。
        /// </summary>
        private static StorageUiVirtualPage virtualPage = StorageUiVirtualPage.Relay;

        /// <summary>
        /// 当前会话声明的页面性质。<b>不在借道会话里时返回 Storage</b>
        /// （真箱子当然可以当容器用 —— 这个回落方向不能反，
        ///   否则每个普通箱子的扣箱合成都会被自己禁掉）。
        /// </summary>
        internal static StorageUiVirtualPage VirtualPage
        {
            get { return VirtualActive ? virtualPage : StorageUiVirtualPage.Storage; }
        }

        internal static object VirtualTag
        {
            get { return virtualTag; }
        }

        internal static bool VirtualActive
        {
            get
            {
                if (virtualItems == null) return false;
                // ⚠ 标记必须和"存储页现在真的还是那个 Items"一起成立。只查字段的话，
                //   换地图 / 被别的插件 closeStorage 之后，字段指着那个已死的实例，
                //   而玩家随后打开**真车后备箱**（isStoring + isStorageTrunk 都为真）
                //   就会被我们误接管。实测踩过一次：Esc 关掉借道界面后标记是死的，
                //   桥接探针因此一直走"托起"分支、反复报找不到物品。
                Player player = Player.LocalPlayer;
                if (player == null || player.inventory == null) return false;
                if (!player.inventory.isStoring) return false;
                if (player.inventory.items == null) return false;
                if (PlayerInventory.STORAGE >= player.inventory.items.Length) return false;
                return ReferenceEquals(player.inventory.items[PlayerInventory.STORAGE], virtualItems);
            }
        }

        internal static bool IsVirtualFor(Items container)
        {
            return virtualItems != null && ReferenceEquals(virtualItems, container);
        }

        internal static bool OpenVirtual(Items items, string title, object tag,
            StorageUiVirtualPage page)
        {
            if (items == null) return false;
            if (items.height <= 0 || items.width <= 0) return false;

            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null) return false;

            try
            {
                // ⚠ 先清标记再开：openTrunk 内部若已开着别的存储会先 closeStorage()，
                //   那会触发我们的补丁 → Host.Close() → 清标记。顺序反了会把刚设好的标记清掉。
                virtualItems = null;
                virtualTitle = null;
                virtualTag = null;
                virtualPage = StorageUiVirtualPage.Relay;

                player.inventory.openTrunk(items);

                // ⚠★ `openTrunk` **只把 Items 挂到存储页，不会弹背包界面** ——
                //   车后备箱是玩家坐进车里时由别的路径把界面打开的。
                //   桥接探针当时看不出这个问题（玩家本来就开着某个箱子界面）。
                //   从世界里直接调（对数字箱按 F）时必须自己打开，否则：
                //   下面 TryOpen 会"接管成功"并记日志，而下一 Tick 的关闭检测发现
                //   `!PlayerDashboardUI.active` ⟹ forceClose ⟹ 归还 trunk
                //   ⟹ 玩家只看到"卡一下，什么都没打开"（2026-10-04 实测）。
                EnsureDashboardOpen();

                // 开完再登记（此时 closeStorage 已经跑过了）
                virtualItems = items;
                virtualTitle = string.IsNullOrEmpty(title) ? "存储" : title;
                virtualTag = tag;
                // 调用方声明的页面性质 —— 必须在 TryOpen **之前**登记，
                // 因为 TryOpen 会同步构造 ctx 并把它交给候选 Provider（区块也一样会读到它）。
                virtualPage = page;

                // ⚠ 必须**当场**接管，不能等下一帧的 Tick：openTrunk 换掉存储页会触发原版的
                //   onItemsResized → onInventoryResized(:1801) 把 `headers[STORAGE].IsVisible`
                //   重新设成 true → 我们的"藏"被顶掉一帧，玩家看到那一瞬间的超大网格
                //   （实测：中转页开 32×32 时右侧被占满，下一帧才刷回大格子）。
                //   顺带还有一好处：接管不了（窄屏 / 没有可用视图）就在这里失败，
                //   不会留下一个"借了道却没人接管"的 trunk。
                if (!TryOpen(items, null, null))
                {
                    // ⚠ 必须把**为什么没接管**打出来：只归还 trunk 的话，玩家看到的
                    //   就是"原版空网格"，日志一条线索都没有（2026-10-04 绕了一整轮）。
                    CustomUIPlugin.Warn("借道界面接管失败：" + (lastOpenReject ?? "未知原因") +
                        " —— 已归还中转页，界面会退回原版网格");
                    virtualItems = null;
                    virtualTitle = null;
                    virtualPage = StorageUiVirtualPage.Relay;
                    ReleaseBorrowedTrunk(items);
                    return false;
                }
                // 接管成功才开始盯中转页：调用方随后 Track 的复制品被引擎搬走时，
                // 由 StorageUiRelay 延后一 Tick 判落点并回报（见 CustomUI.Relay.cs）。
                StorageUiRelay.Attach(items);
                return true;
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("打开虚拟存储失败：" + e);
                virtualItems = null;
                virtualTitle = null;
                ReleaseBorrowedTrunk(items);
                return false;
            }
        }

        internal static void CloseVirtual()
        {
            if (virtualItems == null) return;
            Items ours = virtualItems;
            virtualItems = null;
            virtualTitle = null;
            virtualTag = null;
            // ⚠ 顺序：先 Detach（此时存储页还挂在背包上，落点判得准）、再归还 trunk。
            //   反了的话 closeStorage 会把存储页换成一个新的空 Items，
            //   "还留在中转页里"的复制品就扫不到了 → 被误报成"掉到地上"。
            StorageUiRelay.Detach();
            ReleaseBorrowedTrunk(ours);
        }

        /// <summary>
        /// 确保背包界面真的开着、且停在背包页。
        ///
        /// 为什么由本插件做：<c>openTrunk</c> 只挂页、**不弹界面**（车后备箱是坐进车里时
        /// 由别的路径打开的）。调用方"打开一个虚拟存储界面"的语义里就包含"玩家看得见它"，
        /// 不该让每个调用方各自去拼原版 UI 的开关顺序。
        ///
        /// 照抄的是原版按 Tab 打开背包那一套（<c>PlayerUI.cs:1933-1943</c>）：
        /// 先把四个子页的 active 摆好，再 <c>PlayerDashboardUI.open()</c> ——
        /// 它看到 <c>PlayerDashboardInventoryUI.active</c> 为真会先置 false 再 <c>open()</c>，
        /// 从而真正重建背包页（<c>PlayerDashboardUI.cs:29-33</c>）。
        ///
        /// ⚠ 没有照抄原版的 <c>canOpenMenus</c>（<c>PlayerUI.cs:1816</c> 是 private）：
        ///   它的三条判据是"手持界面开着 / 光标可见 / 正在聊天"。
        ///   本方法只在**世界里**被调到（对箱子按 F），那三种情况本来就不成立；
        ///   真从别的界面里调，请调用方自己先判 —— 不要在这里猜。
        /// </summary>
        private static void EnsureDashboardOpen()
        {
            try
            {
                if (PlayerDashboardUI.active && PlayerDashboardInventoryUI.active) return;

                if (PlayerDashboardUI.active)
                {
                    // 已经开着别的子页（制作/技能/信息）：照原版那一支 ——
                    // 先关掉其它子页，再直接开背包页。
                    // ⚠ 不能只设 active 然后调 PlayerDashboardUI.open()：
                    //   它在 active 已经是 true 时**直接 return**，切不过去。
                    PlayerDashboardCraftingUI.close();
                    PlayerDashboardSkillsUI.close();
                    PlayerDashboardInformationUI.close();
                    PlayerDashboardInventoryUI.open();
                    return;
                }

                PlayerLifeUI.close();
                PlayerPauseUI.close();

                PlayerDashboardInventoryUI.active = true;
                PlayerDashboardCraftingUI.active = false;
                PlayerDashboardSkillsUI.active = false;
                PlayerDashboardInformationUI.active = false;

                // 上面把 active 摆好之后，open() 内部会走
                // `InventoryUI.active=false → open()` 这一支，真正重建背包页。
                PlayerDashboardUI.open();
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("打开背包界面失败（借道界面会看不见）：" + e.Message);
            }
        }

        /// <summary>
        /// 把借走的存储页**还给原版**。
        ///
        /// 为什么必须由我们做：原版关存储的三条路全都**排除 trunk** ——
        /// `closeDistantStorage` 对 trunk 直接 return（`PlayerInventory.cs:1546`）、
        /// 收起手势关要 `shouldInventoryStopGestureCloseStorage`（`PlayerAnimator.cs:809`，
        /// = `!isStorageTrunk`）、按 F 关要 `shouldInteractCloseStorage`（`PlayerInteract.cs:431`，
        /// 同一条）。真车后备箱是靠下车时调 `closeTrunk()` 关的 —— 我们没有那个"载体"。
        /// ⚠ 漏掉的后果（实测）：`isStoring` + `isStorageTrunk` 永远挂着 →
        ///   `InteractableStorage.cs:563` 用 "already in trunk storage" 拒绝打开**任何**箱子
        ///   （玩家看到的症状就是 F 键失效），而且背包界面常驻显示那个中转页。
        /// </summary>
        private static void ReleaseBorrowedTrunk(Items ours)
        {
            if (ours == null) return;
            try
            {
                Player player = Player.LocalPlayer;
                if (player == null || player.inventory == null) return;
                PlayerInventory inv = player.inventory;
                if (!inv.isStoring || !inv.isStorageTrunk) return;
                // ⚠ 必须比对引用，不能只看"我登记过"：借道期间若别人（引擎的车、或另一个
                //   插件）把存储页换成了**它自己的** Items，我们这一关就会把别人的后备箱关掉。
                if (inv.items == null || PlayerInventory.STORAGE >= inv.items.Length) return;
                if (!ReferenceEquals(inv.items[PlayerInventory.STORAGE], ours)) return;
                inv.closeStorage();
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("归还借道的存储页失败：" + e);
            }
        }

        /// <summary>
        /// 最近一次 <see cref="TryOpen"/> 被拒的原因。
        /// ⚠ 只是"记一下"，本身不打日志（TryOpen 每帧都会被 Tick 调，打了会刷屏）；
        ///   由 <see cref="OpenVirtual"/> 在失败时读出来打一条 —— 接管失败如果只表现为
        ///   "原版网格还在"，玩家和我们都无从判断是哪一条拦的（2026-10-04 就是这么绕了一圈）。
        /// </summary>
        private static string lastOpenReject;

        private static bool Reject(string why)
        {
            lastOpenReject = why;
            return false;
        }

        internal static bool TryOpen(Items container, InteractableStorage storage, BarricadeDrop drop)
        {
            lastOpenReject = null;
            if (!CustomUIPlugin.Enabled) return Reject("插件已关闭（Enabled=false）");
            if (container == null) return Reject("容器为 null");

            // ⚠ 窄屏（Screen.width < 1350）必须拒绝接管，否则是「静默失效」：
            //   原版 isSplitClothingArea（:129）为 false 时是**单列**布局 ——
            //   open() 里 areaBox.IsVisible 保持 false，且 updateBoxAreas 只在
            //   分栏时才把 STORAGE/AREA 的标题移进 areaBox（:1851）。
            //   也就是说窄屏下右列根本不存在 → 面板无处可挂。此时若照常接管，
            //   原版存储区已被 ApplyVanillaOverride 藏掉、自定义面板又挂不上，
            //   用户会看到一片空白，排查时极难定位。
            //   判据与原版完全一致，直接不接管、回退原版网格。
            if (!IsWideEnoughForRightColumn())
            {
                WarnNarrowScreenOnce();
                return Reject("屏幕太窄（Screen.width=" + Screen.width + " < 1350，右列不存在）");
            }

            if (isOpen) Close();

            try
            {
                Player player = Player.LocalPlayer;
                // ⚠ 没有本地玩家 = 我们在专用服务器上（或还没进地图）→ 什么都不做。
                //   这个插件的全部功能都是**客户端 UI**：界面、拖拽、原版信息面板都在客户端跑，
                //   服务器上装了也只是静静地不做事（不会报错、不会改数据）。
                //   物品搬运本来就走 sendDragItem/sendDropItem（客户端请求 → 服务端校验），
                //   所以真正生效的地方在客户端，服务器端无需安装本插件。
                if (player == null || player.inventory == null)
                    return Reject("没有本地玩家（专用服务器上或还没进地图）");

                // 资产优先从路障拿；联机客户端拿不到 drop 时退回 storage（也可能为 null）。
                ItemStorageAsset asset = null;
                if (drop != null) asset = drop.asset as ItemStorageAsset;
                StorageUiContext ctx = new StorageUiContext(container, storage, player, drop, asset);

                // 插件借道的虚拟存储：没有资产、也没有友好名可取，标题由调用方给。
                if (IsVirtualFor(container))
                {
                    ctx.IsVirtual = true;
                    ctx.Title = virtualTitle;
                    // 会话归属原样带回 —— 谁的会话谁认领（CanHandle 里按引用比）。
                    ctx.Tag = VirtualTag;
                    // 这一页**是什么性质**由开会话的一方声明（区块与扣箱合成都读它）。
                    // ⚠ 不能在这里"根据 IsVirtual 猜成 Relay" —— 那样声明就白做了。
                    ctx.SetVirtualPage(virtualPage);
                }

                // ⚠ 拿**整条候选链**而不是第一个：选中者的 Open 可能失败（蓝图视图要扫
                //   全资产表、调一堆引擎 API，是三个内置视图里最容易在别人机器上出岔的）。
                //   只给一个的话，它一失败就没人接 → 玩家看到原版网格，而不是退回大格子。
                List<IStorageUiProvider> candidates = StorageUiRegistry.FindCandidates(ctx);
                if (candidates.Count == 0)
                {
                    // ⚠ 两种"空链"要分开报：一种是**没人认领**（可能是故障，要给排查线索），
                    //   另一种是**被明确声明成走原版**（那是预期行为）——后者若也报成
                    //   "没有任何提供者认领"，会让人以为坏了、然后去查一个不存在的故障。
                    if (asset != null && StorageUiRegistry.IsBoxVanilla(asset.GUID))
                    {
                        return Reject("这个箱型被声明为「保持原版」（[General] VanillaBoxes 或 " +
                            "StorageUiRegistry.BindBox(guid, VanillaViewId)）—— 这是预期行为，不是故障");
                    }
                    return Reject("没有任何提供者认领（CanHandle 全为 false；IsVirtual=" +
                        ctx.IsVirtual + "，Tag=" + (ctx.Tag == null ? "null" : ctx.Tag.GetType().Name) + "）");
                }
                for (int i = 0; i < candidates.Count; i++)
                {
                    IStorageUiProvider provider = candidates[i];
                    bool ok = false;
                    try
                    {
                        ok = provider.Open(ctx);
                    }
                    catch (Exception e)
                    {
                        CustomUIPlugin.Error("Provider " + provider.Id + " 的 Open 抛异常：" + e);
                        ok = false;
                    }

                    if (ok)
                    {
                        isOpen = true;
                        activeProvider = provider;
                        activeContext = ctx;
                        CustomUIPlugin.Log("界面已由 " + provider.Id + " 接管（" + ctx.Title +
                            "，" + container.width + "×" + container.height + "）");

                        // 右列区块栈：谁想出现在这一侧就自己 WantsRegion = true，
                        // 顺序由各区块的 RegionOrder（或注册表覆盖）决定。
                        // 主面板自己也是栈里的一格（StorageRegionOrder = 0）。
                        SyncRegions(ctx, provider);
                        return true;
                    }

                    // 这个候选放弃了 → 换下一个。Provider 约定在返回 false 前
                    // 自己拆掉半成品（宿主不会调它的 Close，见 IStorageUiProvider 注释）。
                    string next = (i + 1 < candidates.Count) ? candidates[i + 1].Id : "原版界面";
                    CustomUIPlugin.Warn("界面提供者 " + provider.Id + " 打开失败，回退到 " + next);
                }

                return Reject("候选链里每一个 provider.Open 都返回了 false（见上一条 Warn）");
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("TryOpen 异常：" + e);
                return Reject("TryOpen 抛异常：" + e.Message);
            }
        }

        internal static void Close()
        {
            if (!isOpen) return;
            isOpen = false;

            // ⚠ 借道标记必须在这里清：玩家可能是按 Esc / 点别处关掉的（不走 CloseVirtual），
            //   标记留着的话，之后玩家打开**真的车后备箱**也会被判定成"我借的道"而接管。
            Items borrowed = virtualItems;
            virtualItems = null;
            virtualTitle = null;
            virtualTag = null;
            // ★ 26.10.3.38：临时提示也是**会话状态** —— 不清的话，下次开会话会看到上一轮的旧提示。
            ClearNotice();

            // 区块先关（它们也走"只藏不拆"的常驻策略）
            for (int i = slots.Count - 1; i >= 0; i--) CloseSlot(slots[i]);
            slots.Clear();
            // ⚠ 槽位记录要清，但**面板引用不清**（各 Provider 自己缓存）：
            //   常驻面板的有效性由视图下次 Open 时用 CurrentAttachSpot 自检。

            try
            {
                if (activeProvider != null) activeProvider.Close();
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("Provider Close 异常：" + e);
            }
            activeProvider = null;
            activeContext = null;
            DetachPanel();
            // 交还控制权：把原版存储区按真实尺寸恢复显示
            RestoreVanillaStorage();

            // ⚠ 借道的 trunk 必须由我们关掉，原版没有任何路径会替我们关它
            //   （见 ReleaseBorrowedTrunk 的注释：三条关闭路径全排除 trunk）。
            //   放在 RestoreVanillaStorage **之后**：先按真实尺寸还原、再把存储页清空，
            //   原版随后收到 height=0 的 resize 自己把该区隐藏 —— 顺序反了会先闪一下
            //   中转页的原版网格。
            // ⚠ Detach 必须在归还**之前**：归还会把存储页换成新的空 Items，
            //   那时还留在中转页里的复制品就扫不到了 → 被误报成"掉到地上"、调用方白扣一次账。
            if (borrowed != null) StorageUiRelay.Detach();
            ReleaseBorrowedTrunk(borrowed);
        }

        /// <summary>
        /// 引擎侧物品增删/排序后重建。⚠ 区块也要重建 —— 蓝图列表读的就是箱内物品，
        /// 只重建主面板会让它显示到下一轮轮询才更新。
        /// </summary>
        internal static void Rebuild()
        {
            if (!isOpen || activeProvider == null) return;
            try
            {
                activeProvider.Rebuild(activeContext);
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("Provider Rebuild 异常：" + e);
            }

            for (int i = 0; i < slots.Count; i++)
            {
                try { slots[i].Provider.Rebuild(activeContext); }
                catch (Exception e) { CustomUIPlugin.Error("区块 Rebuild 异常：" + e.Message); }
            }
        }

        // ================= 每帧 =================

        internal static void Tick()
        {
            if (!CustomUIPlugin.Enabled) return;

            try
            {
                Player player = Player.LocalPlayer;
                if (player == null || player.inventory == null)
                {
                    if (isOpen) Close();
                    return;
                }

                PlayerInventory inv = player.inventory;

                // ---- 打开检测（每帧轮询，覆盖所有打开路径） ----
                if (!isOpen)
                {
                    // 没开着界面时「重算区块栈」的请求没有意义 —— 下次 TryOpen 会现算
                    regionRefresh = false;

                    // ⚠ 不能用 `inv.storage != null` 判定：那是服务端字段，
                    //   联机客户端走 ReceiveStoraging 不会赋值 → 恒为 null。
                    //   isStorageTrunk=false 才代表「箱子」而非「车后备箱」。
                    // ⚠ 但「插件借道」的虚拟存储走的正是 trunk 通道（openTrunk 是唯一能挂
                    //   任意 Items 的 public 入口），所以这里要放行我们自己登记的那一次。
                    if (inv.isStoring && (!inv.isStorageTrunk || VirtualActive))
                    {
                        Items container = null;
                        try
                        {
                            if (inv.items != null && PlayerInventory.STORAGE < inv.items.Length)
                            {
                                container = inv.items[PlayerInventory.STORAGE];
                            }
                        }
                        catch (Exception) { }

                        if (container != null && container.height > 0)
                        {
                            // storage / drop 尽力而为（客户端可能拿不到，不影响数据读写）
                            InteractableStorage storage = null;
                            BarricadeDrop drop = null;
                            try
                            {
                                storage = inv.storage;
                                if (storage != null && storage.transform != null)
                                {
                                    drop = BarricadeManager.FindBarricadeByRootTransform(storage.transform);
                                }
                                else
                                {
                                    // 客户端 storage 为 null 时，从「正在看的交互物」兜底拿 drop
                                    InteractableStorage looked = PlayerInteract.interactable as InteractableStorage;
                                    if (looked != null && looked.transform != null)
                                    {
                                        drop = BarricadeManager.FindBarricadeByRootTransform(looked.transform);
                                    }
                                }
                            }
                            catch (Exception) { }

                            TryOpen(container, storage, drop);
                        }
                    }
                    return;
                }

                // ---- 关闭检测 ----
                // ⚠ 必须把**命中了哪一条**打出来。原来六个条件压成一个静默的 Close()，
                //   结果"界面自己关掉了"这种症状在日志里完全 invisible，只能靠猜
                //   （2026-10-04 排查借道界面时就绕在这里）。
                string closeWhy = null;

                if (Input.GetKeyDown(KeyCode.Escape)) closeWhy = "按了 Esc";
                else if (PlayerUI.window == null) closeWhy = "PlayerUI.window 已销毁";

                // ⚠ 必须查 PlayerDashboardUI.active：原版「储存物品」标题被点击时会走
                //   onClickedHeader 的 case 5（:2361-2364）→ PlayerDashboardUI.close()，
                //   此时 isStoring 可能仍为 true、存储也没关闭，但整个背包页连同我们
                //   的面板已经不可见了。若不在这里关掉，isOpen 会一直停在 true →
                //   下次按 F 不再进入接管分支，而原版存储网格已在接管时被藏起来 →
                //   用户看到「一片空白」，且日志毫无线索。
                else if (!PlayerDashboardUI.active) closeWhy = "背包界面已关（PlayerDashboardUI.active=false）";
                else if (!inv.isStoring) closeWhy = "已不在存储状态（isStoring=false，被引擎或别的插件关掉了）";
                // 容器被换掉（换了箱子）或清空（height=0）→ 视为已关闭
                else if (activeContext == null || activeContext.Container == null)
                    closeWhy = "上下文容器丢失";
                else if (activeContext.Container.height <= 0)
                    closeWhy = "容器 height=0（存储页被换成空的）";
                else if (activeContext.Drop != null &&
                    (activeContext.Drop.model == null || !activeContext.Drop.model.gameObject.activeSelf))
                    closeWhy = "箱子模型已回池（Set(false)，不是销毁）";

                if (closeWhy != null)
                {
                    CustomUIPlugin.Log("界面关闭（原因：" + closeWhy + "）");
                    Close();
                    return;
                }

                // 外部请求重算区块栈（注册/注销/改判据/改顺序）→ 必须在驱动之前，
                // 这样新出现的区块这一帧就能被 Tick、下一帧已有内容。
                if (regionRefresh) SyncRegions(activeContext, activeProvider);

                if (activeProvider != null) activeProvider.Tick(activeContext);

                // 区块必须在主面板之后驱动：整栈的排版要等所有块的高度都定型。
                TickRegions(activeContext);

                // 排版只在最后做一次：此时每一块的 SizeOffset_Y 都是这一帧的新值。
                LayoutStack();
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("Tick 异常：" + e);
                Close();
            }
        }

        internal static void OnPluginShutdown()
        {
            Close();
            StorageUiRegistry.Clear();
        }
    }
}
