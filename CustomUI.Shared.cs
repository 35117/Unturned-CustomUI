// ============================================================================
//  CustomUI · 共享层（交互 + 文本工具）
// ============================================================================
//  为什么要有这一层：
//    本插件内置了两种视图（列表 / 大格子）。它们在**布局**上完全不同，
//    但**交互与文本**必须一模一样 —— 否则玩家换个视图就会发现「手感变了」。
//    所以把这两块抽出来共用，两种视图只负责画，不负责行为。
//
//  设计约定：
//    · 方法都是**无状态**静态，返回 bool 表示「是否改变了箱子数据」，
//      由调用方决定要不要刷新列表（各视图有自己的刷新机制）。
//    · 全部走原版 public API（tryFindSpace / sendDragItem / sendDropItem），
//      单机与联机行为一致；物品信息面板与音效则**反射复用原版**实现。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;      // RuntimeHelpers.GetHashCode（弹匣不合并的分组键）
using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace CustomUI
{
    internal static class StorageUiShared
    {
        // ---- 反射缓存 ----
        private static MethodInfo mOpenSelection;
        private static bool openSelectionResolved;
        private static MethodInfo mPlayInventoryAudio;
        private static bool playAudioResolved;

        // ---- 原版「拖拽」机制的反射缓存（托起物品要用） ----
        //  为什么反射借原版而不是自己实现：
        //    原版拖拽是**一整套**现成机制 —— 预览控件（dragItem，挂在 PlayerUI.container
        //    上、每帧由 PlayerUI.update → updateDraggedItem 驱动跟随鼠标）、「抓在物品内
        //    哪一点」的偏移换算（dragOffset/dragPivot）、旋转键、音效、「放到背包格」的
        //    落点处理。自己重写等于把这些再实现一遍，而且外观/手感必然与原版有差异。
        //    我们只需要把「要拖哪件物品」告诉它（这几个私有字段），其余全白送。
        private static FieldInfo fDragJar;
        private static FieldInfo fDragSource;
        private static FieldInfo fDragItem;
        private static FieldInfo fDragOffset;
        private static FieldInfo fDragPivot;
        private static FieldInfo fDragFromPage;
        private static FieldInfo fDragFrom_x;
        private static FieldInfo fDragFrom_y;
        private static FieldInfo fDragFromRot;
        private static MethodInfo mStartDrag;
        private static MethodInfo mUpdatePivot;
        private static bool dragResolved;

        // ================= 按键判定 =================

        /// <summary>
        /// Ctrl（原版 `other` 键）是否按下 —— 原版正是用它区分
        /// 「快速存取 / 丢弃」组合键（`onSelectedItem` / `onGrabbedItem`）。
        /// </summary>
        internal static bool IsCtrlDown()
        {
            return InputEx.GetKey(ControlsSettings.other);
        }

        // ================= 交互入口（两个视图共用） =================

        /// <summary>
        /// **左键**点击某一格：
        ///   · 已有物品被「托起」 → 这一格是**落点**：放进箱子
        ///   · Ctrl+左键          → 原版直接丢弃
        ///   · 否则               → **托起**（借用原版拖拽预览，原大小跟随鼠标）
        /// </summary>
        /// <returns>是否改变了箱子数据（调用方据此刷新）。</returns>
        internal static bool HandlePrimaryClick(byte x, byte y)
        {
            try
            {
                // 托起中：左键 = 放到这一格
                if (IsDragging) return DropIntoStorage(x, y);

                if (IsCtrlDown()) return DropItem(x, y);

                // 托起不改数据（物品还在箱子里），不需要刷新
                if (BeginGrab(x, y)) return false;

                // 兜底：原版拖拽机制反射失败时，退回「直接取回背包」，
                // 至少保证界面还能用（并已在 ResolveDrag 里报过 warn）
                return TakeToBackpack(x, y);
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("左键交互异常：" + e);
                return false;
            }
        }

        /// <summary>
        /// **右键**点击某一格：
        ///   · 有物品被托起 → 取消托起（放回原处）。Esc 也能取消，但 Esc 会把背包页一起关掉。
        ///   · Ctrl+右键    → 取回到背包（原版「快速存取」组合键的语义）
        ///   · 否则         → 弹原版物品信息面板
        /// ⚠ Ctrl 分支必须先判：玩家按这个组合键时期望的是「立刻取回」，
        ///   直接弹信息面板会被当成功能坏了（实测被指出过）。
        /// </summary>
        /// <returns>是否改变了箱子数据（弹信息面板不算）。</returns>
        internal static bool HandleSecondaryClick(byte x, byte y)
        {
            try
            {
                if (IsDragging)
                {
                    EndGrab();
                    return false;
                }

                if (IsCtrlDown()) return TakeToBackpack(x, y);
                ShowItemInfo(x, y);
                return false;
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("右键交互异常：" + e);
                return false;
            }
        }

        /// <summary>
        /// 取回到背包：找背包空位 → 请求搬运。
        /// ⚠ 必须走 `sendDragItem` 而不是直接改数组 —— 引擎的物品改动要走网络同步，
        ///   `sendDragItem` 是原版自己的通道（单机时自动本地执行）。
        /// </summary>
        internal static bool TakeToBackpack(byte x, byte y)
        {
            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null) return false;

            ItemJar jar = player.inventory.getItem(PlayerInventory.STORAGE,
                player.inventory.getIndex(PlayerInventory.STORAGE, x, y));
            if (jar == null) return false;

            byte newPage;
            byte new_x;
            byte new_y;
            byte newRot;
            if (player.inventory.tryFindSpace(jar.size_x, jar.size_y, out newPage, out new_x, out new_y, out newRot))
            {
                // 音效与原版一致（原版 onSelectedItem 快速取出时同样先播音再发请求）
                PlayAudio(jar.GetAsset());

                player.inventory.sendDragItem(PlayerInventory.STORAGE, x, y,
                    newPage, new_x, new_y, newRot);
                return true;
            }

            CustomUIPlugin.Log("背包没有空间，无法取出");
            return false;
        }

        /// <summary>原版直接丢弃。</summary>
        internal static bool DropItem(byte x, byte y)
        {
            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null) return false;

            player.inventory.sendDropItem(PlayerInventory.STORAGE, x, y);
            return true;
        }

        // ============ 槽位命中 → 插件回调（两个面板共用这一份，别各写一遍） ============

        /// <summary>
        /// 二维 <c>Items</c> 源按**坐标**认领：返回该坐标上的物品，取不到返回 null。
        ///
        /// ⚠ 用 `getIndex`（自带边界检查、越界返回 255），不用 `getItem(x,y)` ——
        ///   这个 SDK 版本上 `Items` 没有二维版 `getItem`。
        /// ⚠ 必须**核对身份**：坐标是上一轮重建时从代表物品抄下来的，若插件在重建之后
        ///   改过这份容器，同一个坐标可能已经换了物品 —— 拿它让插件改数据就是「点 A 改 B」
        ///   （与坑 #55 同一取向：宁可不做事）。
        /// </summary>
        internal static Item ResolveItemAt(Items container, byte x, byte y, ushort boundItemId)
        {
            if (container == null || container.page < PlayerInventory.SLOTS) return null;

            byte idx = container.getIndex(x, y);
            if (idx == byte.MaxValue) return null;

            ItemJar jar = container.getItem(idx);
            if (jar == null || jar.item == null || jar.item.id != boundItemId) return null;
            return jar.item;
        }

        /// <summary>
        /// 造一份槽位命中信息（**字段映射只此一处**）：交付给回调时用，
        /// 「这一格该铺什么颜色」的解析也用同一份 —— 否则两个入口对同一格给出不同的 id/数量。
        /// ⚠ **不含按键状态**（Ctrl / 托起）：那是"点击那一刻"的事，
        ///   填色的解析是**每轮一次**的纯函数，不该去读输入（`ResolveCellFill` 的调用点正是如此）。
        /// </summary>
        internal static StorageSlotHit MakeSlotHit(Item item, int slotIndex, int x, int y)
        {
            StorageSlotHit hit = new StorageSlotHit();
            hit.Item = item;
            hit.SlotIndex = slotIndex;
            hit.X = x;
            hit.Y = y;
            hit.ItemId = (item == null) ? (ushort) 0 : item.id;
            hit.Amount = (item == null) ? (byte) 0 : item.amount;
            hit.Quality = (item == null) ? (byte) 0 : item.quality;
            return hit;
        }

        /// <summary>
        /// 问一次「这一格该铺什么颜色」—— 三个内置视图的 `CellFill` 共用这一份
        /// （解析 + 异常隔离 + 只报一次都在这儿，视图里不再各写一遍）。
        ///
        /// ⚠ 回调是**每轮对每个可见格**调一次的纯函数：插件在里面改数据 / 标脏都不对
        ///   （与 `SetItem` 的"拉"同一约定）。
        /// ⚠ 返回 <c>default(SleekColor)</c>（alpha 0）= 这一格不铺 —— 插件返回什么都行，
        ///   组件不替它做判断。
        /// </summary>
        internal static SleekColor ResolveCellFill(StorageCellFillHandler handler, StorageSlotHit hit,
            string logName, ref bool warnedThrow)
        {
            if (handler == null) return default(SleekColor);

            try
            {
                return handler(hit);
            }
            catch (Exception e)
            {
                if (!warnedThrow)
                {
                    warnedThrow = true;
                    CustomUIPlugin.Error(logName + "：CellFill 回调抛异常，这一格按「不铺」处理：" +
                        e.Message);
                }
                return default(SleekColor);
            }
        }

        /// <summary>
        /// 同上，但给的是**配方**（蓝图视图的行里装的是配方，不是物品）。
        /// 两个重载共用同一套「异常隔离 + 只报一次」—— 不另写一份同义实现。
        /// </summary>
        internal static SleekColor ResolveCellFill(StorageBlueprintFillHandler handler,
            Blueprint blueprint, bool craftable, string logName, ref bool warnedThrow)
        {
            if (handler == null || blueprint == null) return default(SleekColor);

            try
            {
                return handler(blueprint, craftable);
            }
            catch (Exception e)
            {
                if (!warnedThrow)
                {
                    warnedThrow = true;
                    CustomUIPlugin.Error(logName + "：CellFill 回调抛异常，这一行按「不铺」处理：" +
                        e.Message);
                }
                return default(SleekColor);
            }
        }

        /// <summary>
        /// 把一次槽位命中交付给插件回调 —— **两个面板（网格 / 列表）共用这一份**，
        /// 避免「同一语义两份实现」（本项目反复栽过的那种漂移）。
        ///
        /// <paramref name="item"/> 为 null = 没解析出物品（列表在重建后被改过）：
        /// 记一次过期警告并放弃，绝不拿可能指错的下标/坐标去让插件改数据。
        /// 扁平源与二维源**互斥地**填下标与坐标（两套混用 = 插件分不清该用哪一套）。
        /// ⚠ 插件抛异常不能拖垮界面：吞掉 + 只报一次。
        /// ⚠ <paramref name="warnedStale"/> / <paramref name="warnedThrow"/> 是「只报一次」的闸门，
        ///   由调用方持有，并在**关面板时复位** —— 否则第二次遇到完全无声（盘点 §2.4 那一族）。
        /// </summary>
        internal static void DeliverSlotHit(StorageSlotHitHandler handler, string logName, string what,
            Item item, int slotIndex, int x, int y, ref bool warnedStale, ref bool warnedThrow)
        {
            if (handler == null) return;

            if (item == null)
            {
                if (!warnedStale)
                {
                    warnedStale = true;
                    CustomUIPlugin.Warn(logName + "：" + what + "点到的槽位已过期（列表在重建后被改过），" +
                        "这次不触发回调 —— 请让插件在改完数据后调 MarkDirty()");
                }
                return;
            }

            StorageSlotHit hit = MakeSlotHit(item, slotIndex, x, y);
            // 按键状态由组件统一读，插件不必自己去碰 Input
            hit.Ctrl = IsCtrlDown();
            hit.Dragging = IsDragging;

            try { handler(hit); }
            catch (Exception e)
            {
                if (!warnedThrow)
                {
                    warnedThrow = true;
                    CustomUIPlugin.Error(logName + "的槽位" + what + "回调抛异常，这次点击已忽略：" + e.Message);
                }
            }
        }

        // ================= 数据指纹（网格 / 列表共用，别各写一份） =================

        /// <summary>
        /// 二维容器指纹：尺寸 + 数量 + 每件 (id, amount, quality) 与坐标。
        /// 用来判断「外部改了数据」而触发刷新 —— 不依赖任何委托签名，最鲁棒。
        /// ⚠ 绝不混入 <c>item.state</c>：工业机械物品的 state 每帧都在变，
        ///   一旦入指纹就会每帧重建，图标（异步加载）永远显示不出来（坑 #30）。
        /// </summary>
        internal static int FingerprintItems(Items container)
        {
            if (container == null) return -1;
            unchecked
            {
                int h = container.width * 397 ^ container.height;
                byte count = container.getItemCount();
                h = h * 397 ^ count;
                for (byte i = 0; i < count; i++)
                {
                    ItemJar jar = container.getItem(i);
                    if (jar == null) continue;
                    h = h * 397 ^ jar.item.id;
                    h = h * 397 ^ jar.item.amount;
                    h = h * 397 ^ jar.item.quality;
                    h = h * 397 ^ jar.x;
                    h = h * 397 ^ jar.y;
                }
                return h;
            }
        }

        /// <summary>
        /// 扁平槽位的指纹。⚠ **不含坐标** —— 扁平模型没有坐标语义（我们给的全是 0,0），
        /// 带上只会让每一项都相同、白算一场。同样绝不混入 <c>item.state</c>。
        /// </summary>
        internal static int FingerprintJars(List<ItemJar> jars)
        {
            if (jars == null) return -1;
            unchecked
            {
                int h = jars.Count;
                for (int i = 0; i < jars.Count; i++)
                {
                    ItemJar jar = jars[i];
                    if (jar == null || jar.item == null) continue;
                    h = h * 397 ^ jar.item.id;
                    h = h * 397 ^ jar.item.amount;
                    h = h * 397 ^ jar.item.quality;
                }
                return h;
            }
        }

        // ================= 托起（左键拿起）/ 放入（左键落点） =================

        /// <summary>原版是否正处于「有物品被托起」的状态。</summary>
        internal static bool IsDragging
        {
            get { return PlayerDashboardInventoryUI.isDragging; }   // public 字段，直接读
        }

        private static bool ResolveDrag()
        {
            if (dragResolved) return fDragJar != null;
            dragResolved = true;

            Type t = typeof(PlayerDashboardInventoryUI);
            fDragJar = AccessTools.Field(t, "dragJar");
            fDragSource = AccessTools.Field(t, "dragSource");
            fDragItem = AccessTools.Field(t, "dragItem");
            fDragOffset = AccessTools.Field(t, "dragOffset");
            fDragPivot = AccessTools.Field(t, "dragPivot");
            fDragFromPage = AccessTools.Field(t, "dragFromPage");
            fDragFrom_x = AccessTools.Field(t, "dragFrom_x");
            fDragFrom_y = AccessTools.Field(t, "dragFrom_y");
            fDragFromRot = AccessTools.Field(t, "dragFromRot");
            mStartDrag = AccessTools.Method(t, "startDrag");
            mUpdatePivot = AccessTools.Method(t, "updatePivot");

            bool all = fDragJar != null && fDragSource != null && fDragItem != null
                && fDragOffset != null && fDragPivot != null && fDragFromPage != null
                && fDragFrom_x != null && fDragFrom_y != null && fDragFromRot != null
                && mStartDrag != null && mUpdatePivot != null;

            if (!all)
            {
                CustomUIPlugin.Warn("原版拖拽机制反射失败 → 左键退回「直接取回背包」" +
                    "（托起/放置不可用，其余功能不受影响）");
            }
            return all;
        }

        /// <summary>
        /// 读出"手上正托着的那件物品"以及它的**来源页与坐标**。
        ///
        /// 为什么公开：接入方要把"托着物品点格子"这个手势复用成**存入**，
        /// 就必须能区分"手上这件是我刚从箱里托起来的复制品"（那种情况不能当存入，
        /// 否则自己吞自己）和"从背包/装备栏拿来的"（那才是存入）。
        /// ⚠ 只读不改；没在拖拽、或反射没就绪时返回 false。
        /// </summary>
        internal static bool TryGetHeldItem(out Item item, out byte page, out byte x, out byte y)
        {
            item = null;
            page = 0;
            x = 0;
            y = 0;
            try
            {
                if (!IsDragging) return false;
                if (!ResolveDrag()) return false;
                ItemJar jar = fDragJar.GetValue(null) as ItemJar;
                if (jar == null || jar.item == null) return false;
                page = (byte)fDragFromPage.GetValue(null);
                x = (byte)fDragFrom_x.GetValue(null);
                y = (byte)fDragFrom_y.GetValue(null);
                item = jar.item;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// **托起**箱内某一格的物品（原大小跟随鼠标），由玩家自己决定放到哪里。
        /// 之后点背包格 → 原版自己处理；点箱内格子 → 走 <see cref="DropIntoStorage"/>。
        /// </summary>
        /// <returns>是否成功开始托起（false 时调用方应回退为「直接取回」）。</returns>
        internal static bool BeginGrab(byte x, byte y)
        {
            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null) return false;

            ItemJar jar = player.inventory.getItem(PlayerInventory.STORAGE,
                player.inventory.getIndex(PlayerInventory.STORAGE, x, y));
            if (jar == null) return false;
            return BeginGrabFrom(PlayerInventory.STORAGE, jar);
        }

        /// <summary>
        /// 托起**指定页**上的一格。
        /// ⚠ 为什么需要按页号而不是只认 STORAGE：第三方插件（如工业的数字箱）的物品
        ///   不在任何页上，而原版拖拽的 `dragJar` 是**指向源页里那个 jar 的引用**
        ///   （`PlayerDashboardInventoryUI.cs:1112`）、收尾时按 `dragFromPage` 归位
        ///   —— 所以虚拟物品只能"先落到一个真实页（背包），再托起"。
        /// </summary>
        internal static bool BeginGrabFrom(byte page, ItemJar jar)
        {
            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null) return false;
            if (jar == null) return false;

            if (!ResolveDrag()) return false;

            SleekItem preview = fDragItem.GetValue(null) as SleekItem;
            if (preview == null)
            {
                // 正常情况下原版打开背包页时就建好了它（PlayerDashboardInventoryUI 初始化里）
                CustomUIPlugin.Warn("原版拖拽预览控件不存在 → 左键退回「直接取回背包」");
                return false;
            }

            try
            {
                fDragJar.SetValue(null, jar);

                // ⚠ dragSource 必须**非空**：原版 onPlacedItem 的第一句就是
                //   `if (dragSource != null && isDragging)`，为空则「放到背包格」整条
                //   路径都不会走（点了没反应）。它只被用作空判断，指向预览控件本身即可。
                fDragSource.SetValue(null, preview);

                fDragFromPage.SetValue(null, page);
                fDragFrom_x.SetValue(null, jar.x);
                fDragFrom_y.SetValue(null, jar.y);
                fDragFromRot.SetValue(null, jar.rot);

                // 让「拿起的东西」以鼠标为中心：原版是按「抓在物品内的哪一点」算偏移，
                // 而我们的格子是固定尺寸的图标，居中更符合直觉。
                // （size*50 = 原版一格 50 像素，与清单/格子里的图标尺寸无关）
                fDragOffset.SetValue(null,
                    new Vector2(-(jar.size_x * 25f), -(jar.size_y * 25f)));

                mUpdatePivot.Invoke(null, null);   // 由 dragOffset 算出 dragPivot
                preview.updateItem(jar);           // public：让预览显示这件物品（原大小）
                mStartDrag.Invoke(null, null);     // private：置 isDragging + 禁用原版格子 + 音效

                return true;
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("托起物品失败：" + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 结束托起。原版 `stopDrag()` 是 public：它会还原物品旋转、隐藏预览、
        /// 恢复原版格子的可点状态、并播音效 —— 直接调用即可。
        /// </summary>
        internal static void EndGrab()
        {
            try { PlayerDashboardInventoryUI.stopDrag(); }
            catch (Exception) { /* 未在拖拽时 stopDrag 会自己 return */ }
        }

        // ---- 借道中转页「不可放入」的说明 ----
        // ⚠ 光写日志等于没说（实测被指出过：自定义语义必须显式告诉玩家），
        //   所以拒绝之后的一小段时间里，把这句话挂到原版标题栏上。
        private static bool relayDepositWarned;
        private static float relayDepositRefusedAt = -999f;
        private const float RELAY_NOTE_SECONDS = 6f;

        internal static void WarnRelayDepositOnce()
        {
            relayDepositRefusedAt = Time.realtimeSinceStartup;
            if (relayDepositWarned) return;
            relayDepositWarned = true;
            CustomUIPlugin.Warn("借道的中转页不能放入物品：关界面时原版 closeStorage 会把整页" +
                "换成新的空 Items，放进去的会随页销毁 ⟹ 已拒绝这次放入（物品留在原处）。" +
                "反向存入要走调用方回调（C 第 4 步），不是写这一页。");
        }

        /// <summary>
        /// 刚拒绝过放入时返回一句提示，否则 null（宿主每帧写标题栏时拼上去）。
        /// ⚠ 返回的是**纯文字、不带分隔符** —— 前缀「" · "」由宿主统一拼（26.10.3.38 起，
        ///   公开提示与这句走同一个拼接点；两边各自带前缀会长出「" ·  · "」）。
        /// </summary>
        internal static string RelayDepositNote()
        {
            if (Time.realtimeSinceStartup - relayDepositRefusedAt > RELAY_NOTE_SECONDS) return null;
            return "这一页是中转区，不能放入物品";
        }

        /// <summary>
        /// 把「托起中」的物品放进箱子。
        ///
        /// 落点：**点击的那一格在显示上是排序结果，和箱子里的真实坐标无关**，
        /// 所以这里只把它当作「放进箱子」的意图，实际落点由箱内空位决定
        /// （与原版一致：面板会自动重排显示）。
        /// 放不下就取消托起并把物品留在原处。
        /// </summary>
        /// <returns>是否改变了箱子数据。</returns>
        internal static bool DropIntoStorage(byte targetX, byte targetY)
        {
            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null) return false;
            if (!PlayerDashboardInventoryUI.isDragging) return false;
            if (!ResolveDrag()) { EndGrab(); return false; }

            ItemJar jar = fDragJar.GetValue(null) as ItemJar;
            if (jar == null) { EndGrab(); return false; }

            byte fromPage = (byte) fDragFromPage.GetValue(null);
            byte fromX = (byte) fDragFrom_x.GetValue(null);
            byte fromY = (byte) fDragFrom_y.GetValue(null);

            // 点在原格上 = 取消（原版同款语义）
            if (fromPage == PlayerInventory.STORAGE && fromX == targetX && fromY == targetY)
            {
                EndGrab();
                return false;
            }

            Items container = player.inventory.items[PlayerInventory.STORAGE];
            if (container == null) { EndGrab(); return false; }

            // ⚠ **借道的中转页不是箱子**：往它里面放东西 = 关界面时随页销毁。
            //   原版 `closeStorage()` 是 `updateItems(STORAGE, null)`（`PlayerInventory.cs:1639`），
            //   整个 Items 对象被换成一个新的空页，留在里面的物品**没有任何回收路径**。
            //   实测（2026-10-03 用户指出）：借道界面里那一格显示"金属板 ×1"，
            //   玩家再放一件进去变 ×2、拿出来变 0，看着完全像个小箱子 —— 但它其实是中转页。
            //   ⟹ 这里**明确拒绝**（取消托起、物品留在原处），不静默收下。
            //   反向存入是第 4 步的正式功能，届时走调用方回调写自己的清单，不是写这一页。
            if (StorageUiHost.IsVirtualFor(container))
            {
                EndGrab();
                WarnRelayDepositOnce();
                return false;
            }

            byte size_x = jar.size_x;
            byte size_y = jar.size_y;
            byte newRot = jar.rot;
            byte nx = 255;
            byte ny = 255;

            // ① 自己扫一遍候选格：用 checkSpaceDrag 而不是 checkSpaceEmpty，
            //    这样「同一页内移动」时允许与自己原来的占位重叠（原版 onPlacedItem
            //    也是用 checkSpaceDrag + checkSame 处理同页移动的）。
            bool samePage = (fromPage == PlayerInventory.STORAGE);
            byte rot = jar.rot;
            bool found = false;
            for (byte ty = 0; ty < container.height && !found; ty++)
            {
                for (byte tx = 0; tx < container.width && !found; tx++)
                {
                    if (container.checkSpaceDrag(fromX, fromY, (byte) fDragFromRot.GetValue(null),
                            tx, ty, rot, size_x, size_y, samePage))
                    {
                        nx = tx;
                        ny = ty;
                        newRot = rot;
                        found = true;
                    }
                }
            }

            // ② 仍找不到 → 交给箱子自己找（它会尝试两个旋转方向）
            if (!found)
            {
                byte fx;
                byte fy;
                byte fr;
                if (container.tryFindSpace(size_x, size_y, out fx, out fy, out fr))
                {
                    nx = fx;
                    ny = fy;
                    newRot = fr;
                    found = true;
                }
            }

            if (!found)
            {
                CustomUIPlugin.Log("箱子没有空位，放不下（已取消托起）");
                EndGrab();
                return false;
            }

            // ⚠ 顺序与原版一致：先 stopDrag（还原 rot / 隐藏预览 / 恢复原版格子可点）
            //   再发移动请求。
            EndGrab();

            player.inventory.sendDragItem(fromPage, fromX, fromY,
                PlayerInventory.STORAGE, nx, ny, newRot);

            PlayAudio(jar.GetAsset());
            return true;
        }

        /// <summary>
        /// 弹原版物品信息面板。**反射复用** `PlayerDashboardInventoryUI.openSelection`
        /// —— 它含图标、描述、装备/丢弃/存储等完整按钮，自己重写不划算且容易漏。
        /// 它只吃 `(page, x, y)` 坐标，与网格控件无关，所以自定义格子照样能用。
        /// </summary>
        internal static void ShowItemInfo(byte x, byte y)
        {
            if (!openSelectionResolved)
            {
                openSelectionResolved = true;
                mOpenSelection = AccessTools.Method(typeof(PlayerDashboardInventoryUI), "openSelection");
                if (mOpenSelection == null)
                {
                    CustomUIPlugin.Warn("找不到原版 openSelection → 右键信息面板不可用（不影响其他功能）");
                }
            }
            if (mOpenSelection == null) return;

            mOpenSelection.Invoke(null, new object[] { PlayerInventory.STORAGE, x, y });
        }

        /// <summary>
        /// 播放原版搬运音效。反射复用原版实现 —— 音量与音效资源都跟随玩家的
        /// 原版设置；自己播 AudioClip 会绕开玩家的音量配置，手感不一致。
        /// </summary>
        internal static void PlayAudio(ItemAsset asset)
        {
            if (asset == null) return;
            if (!playAudioResolved)
            {
                playAudioResolved = true;
                mPlayInventoryAudio = AccessTools.Method(typeof(PlayerDashboardInventoryUI),
                    "PlayInventoryAudio", new Type[] { typeof(ItemAsset) });
                if (mPlayInventoryAudio == null)
                {
                    CustomUIPlugin.Warn("找不到原版 PlayInventoryAudio → 取放物品无音效（不影响功能）");
                }
            }
            if (mPlayInventoryAudio == null) return;
            try
            {
                mPlayInventoryAudio.Invoke(null, new object[] { asset });
            }
            catch (Exception) { /* 音效失败不致命，不影响取放本身 */ }
        }

        // ================= 取数（合并同类 + 排序） =================

        /// <summary>
        /// 显示用的一项 = 若干件**完全相同**的物品合并后的结果。
        /// 视图不直接遍历物品，而是遍历它 —— 「合并显示」这件事只在这里定义一次，
        /// 两个视图自然保持一致。
        /// </summary>
        internal class ItemGroup
        {
            /// <summary>代表物品：图标、交互（取回/托起/丢弃）都以它为准。</summary>
            public ItemJar First;

            /// <summary>合并了几件（容器里实际有几个 jar）。</summary>
            public int Count;

            /// <summary>数量之和（每件的 item.amount 累加；不堆叠的物品 amount 恒为 1）。</summary>
            public int TotalAmount;

            /// <summary>分组键（供视图判断「这一格绑的还是不是同一组」）。</summary>
            public int Key;
        }

        /// <summary>
        /// 把容器内的物品**按「完全相同的物品」合并**，并排序后写入 into。
        ///
        /// 合并判据 = **物品 ID + 耐久**（`item.state` 不参与，理由见下）。
        ///   · 「相同的物品」对玩家而言就是「图标和信息都一样」，所以同 ID + 同耐久即视为相同；
        ///   · ⚠ **state 绝不能进判据**：工业物品把 UID/电量这类数据存在 `item.state` 里
        ///     （见项目记忆：棒属性/电量/UID 存 state），每件都不同 → 带上它就永远合并不了，
        ///     而且 state 可能每帧变化 → 合并结果每帧抖动。它对玩家也是不可见的。
        ///
        /// 排序键：物品 ID → 耐久降序。同类型必然相邻。
        /// 为什么要排序：容器内部 `List&lt;ItemJar&gt;` 的顺序取决于放置/移除历史
        /// （`tryFindSpace` 会优先填洞），直接按它显示看起来是乱的（实测反馈过）。
        /// 排序放在共享层是为了两个视图**同序** —— 否则切换视图物品会「跳位置」。
        ///
        /// ⚠ 用 `into` 传入可复用容器，避免每帧产生垃圾。
        /// </summary>
        /// <summary>
        /// 把容器的 jar 摊进 <paramref name="into"/>，可选按搜索词过滤，并统计件数。
        ///
        /// ★ 26.10.3.29：**这是全项目唯一的 jar 收集点** —— 取证（<see cref="AuditStackAmount"/>）
        ///   与搜索过滤都挂在这里。此前这段逻辑只长在 `CollectGroups(Items,…)` 内部，
        ///   一旦有人为了"先筛再分组"自己写一遍遍历，**取证就会静默漏掉**
        ///   （非法堆叠不再被发现，而它当初正是靠这一步才被抓到的）。⟹ 抽出来，两条路走同一个地方。
        ///
        /// <paramref name="query"/> 为空 = 不过滤（⚠ 空查询**不该**把界面清空）。
        /// <paramref name="allJars"/> / <paramref name="matchedJars"/> = 全量 / 通过过滤的**条目数**
        ///   （= 头部"N 件"那个口径，不是 Σ amount —— 两个口径混用就是"同一个数两套算法"）。
        ///   ⚠ 两个数从**同一次遍历**里出 —— 别让调用方再遍历一遍。
        /// </summary>
        internal static void CollectJars(Items container, List<ItemJar> into, string query,
            out int allJars, out int matchedJars)
        {
            into.Clear();
            allJars = 0;
            matchedJars = 0;
            if (container == null) return;

            bool filter = !string.IsNullOrEmpty(query);
            byte count = container.getItemCount();
            for (byte i = 0; i < count; i++)
            {
                ItemJar jar = container.getItem(i);
                if (jar == null || jar.item == null) continue;

                AuditStackAmount(jar.item);
                allJars++;

                if (filter && !MatchesQuery(jar.GetAsset(), query)) continue;
                matchedJars++;
                into.Add(jar);
            }
        }

        /// <summary>不带过滤的收集（内部用，不统计件数）。</summary>
        internal static void CollectJars(Items container, List<ItemJar> into)
        {
            int all = 0;
            int matched = 0;
            CollectJars(container, into, null, out all, out matched);
        }

        internal static void CollectGroups(Items container, List<ItemGroup> into)
        {
            // 摊成 jar 列表后交给**同一份**合并核心。
            // ⚠ 不要把合并逻辑在这里再写一遍：那样「什么叫同一件物品」就有了两个答案
            //   （本项目反复栽过的"同一逻辑两份实现"，坑 #20/#34/#53）。
            CollectJars(container, _jarBuf);
            CollectGroups(_jarBuf, into);
        }

        /// <summary>
        /// 合并核心的**唯一实现**：输入是一串 jar。
        ///
        /// 为什么单独开这个入口：不是所有数据源都是原版 `Items`。
        /// 工业插件的数字箱/机器槽位是**扁平 `List&lt;Item&gt;` + 总格数容量**（没有二维坐标），
        /// 让它把物品包成 `ItemJar`（`ItemJar` 的构造函数是 public）就能复用同一套
        /// 合并、排序、文本与图标规则 —— 第三方不必重学一遍本项目全部界面铁律。
        /// </summary>
        internal static void CollectGroups(List<ItemJar> jars, List<ItemGroup> into)
        {
            CollectGroups(jars, into, false);
        }

        /// <summary>
        /// 合并核心（带策略版）。
        ///
        /// <paramref name="idOnly"/> = true 时**只按物品 ID 合并**（不同耐久/质量也并成一项）。
        /// ⚠ 这个策略是给**扁平入口**（工业数字箱那类记账型清单）用的：
        ///   那种容器里"同一件物品"本来就散成多条（每条一个耐久），
        ///   按 id+耐久分会让玩家看到同一把材料占好几格，误以为是很不同的东西。
        ///   而原版箱子的二维摆放**不传这个参数**（走 false）——
        ///   那边"同 id 不同耐久"确实是两格不同的物品，合并会误导玩家点错。
        ///   ⟹ 所以策略必须由调用点选，不能全局改（用户 2026-10-04 明确"先只改数字箱"）。
        /// </summary>
        internal static void CollectGroups(List<ItemJar> jars, List<ItemGroup> into, bool idOnly)
        {
            CollectGroups(jars, into, idOnly, false);
        }

        /// <summary>
        /// 合并核心（带完整策略版）。
        /// <paramref name="mergeContentPages"/> = true 时**内容量物品（弹匣与弹药箱）也允许并成一格**
        /// （一格代表 N 件）。
        /// ⚠ 只该给**记账型扁平清单**开（数字箱的"合并显示"模式）：开了之后一格不再唯一对应一件，
        ///   调用方必须自己定好"点这一格取哪一件"并把它告诉玩家（工业侧 `ChooseSource` + TooltipHint），
        ///   否则就是"×N 点下去作用在哪一件"没人说得清 —— 那正是枪械/近战永不合并的理由。
        /// </summary>
        internal static void CollectGroups(List<ItemJar> jars, List<ItemGroup> into, bool idOnly,
            bool mergeContentPages)
        {
            into.Clear();
            if (jars == null) return;

            int count = jars.Count;
            for (int i = 0; i < count; i++)
            {
                ItemJar jar = jars[i];
                if (jar == null || jar.item == null) continue;

                int key = GroupKey(jar, idOnly, mergeContentPages);
                ItemGroup g = null;
                for (int k = 0; k < into.Count; k++)
                {
                    if (into[k].Key == key)
                    {
                        g = into[k];
                        break;
                    }
                }

                if (g == null)
                {
                    g = new ItemGroup();
                    g.First = jar;
                    g.Key = key;
                    into.Add(g);
                }

                g.Count++;
                g.TotalAmount += jar.item.amount;
            }

            into.Sort(CompareGroup);
        }

        /// <summary>摊平 `Items` 时复用的缓冲（避免每帧产生垃圾）。</summary>
        private static readonly List<ItemJar> _jarBuf = new List<ItemJar>();

        /// <summary>物品 ID → 引擎给的单件堆叠上限（<c>ItemAsset.MaxAmount</c>）。查过一次就不再查。</summary>
        private static readonly Dictionary<ushort, int> _stackCaps = new Dictionary<ushort, int>();
        /// <summary>已经报告过的物品 ID（每种只报一次，正常玩不会刷屏）。</summary>
        private static readonly HashSet<ushort> _stackCapReported = new HashSet<ushort>();

        /// <summary>
        /// 【取证】真实页面上出现了 <c>amount &gt; MaxAmount</c> 的物品 ⟹ 这是**原版造不出来的堆叠**。
        ///
        /// 为什么原版造不出来：引擎只在资产上限内发物品（<c>Item(id, origin)</c> 取
        /// <c>MaxAmountAsByte</c>，<c>Item.cs:100</c>；合成产物是**逐件**给 <c>output.amount</c> 次、
        /// 每次一件，<c>PlayerCrafting.cs:930-958</c>），而 <c>Items</c> 的放入不做部分合并。
        /// 玩家也**看不出来**：<c>SleekItem.cs:215-227</c> 只在 <c>MaxAmount &gt; 1</c> 时才在格子上
        /// 印 "x数量"，上限为 1 的物品永远不印 ⟹ 一格藏 N 件、界面只有一个图标。
        ///
        /// ⚠ 本方法**只报告、不改数据**：拆堆要在别人的容器里腾格子，
        ///   那是"改玩家存档"的动作，得由持有那块数据的一方决定怎么做。
        /// 代价：只对 <c>amount &gt; 1</c> 的条目查一次资产并永久缓存 ⟹ 常见物品（amount=1）零开销。
        /// </summary>
        private static void AuditStackAmount(Item it)
        {
            if (it == null || it.amount <= 1) return;
            int cap;
            if (!_stackCaps.TryGetValue(it.id, out cap))
            {
                ItemAsset a = Assets.find(EAssetType.ITEM, it.id) as ItemAsset;
                if (a == null) return;          // 资产没加载完，下轮再看（不缓存未知）
                cap = (a.amount > 0) ? a.amount : 1;
                _stackCaps[it.id] = cap;
            }
            if (it.amount <= cap) return;
            if (_stackCapReported.Add(it.id))
            {
                CustomUIPlugin.Warn("[取证] 真实物品数量超过资产堆叠上限：id=" + it.id +
                    " amount=" + it.amount + " 上限=" + cap +
                    " —— 原版不产生这种数据，界面上也不会显示这个数字（一格其实藏着 " +
                    it.amount + " 件）。本插件不修改它，每种物品只报一次。");
            }
        }

        /// <summary>
        /// 扁平来源的占用格数 = Σ size_x × size_y。
        /// ⚠ 只算占用，**不算最大连续空位** —— 那要求二维摆放语义（见 ComputeSpace 注释），
        ///   而扁平模型（工业数字箱）的真源是「总格数」，不做二维碰撞检查。
        ///   给它编一个"最大空位 W×H"就是**会误导玩家的假信息**（同 §4.1 的取向：宁可少说）。
        /// </summary>
        internal static int OccupiedCellsOf(List<ItemJar> jars)
        {
            if (jars == null) return 0;
            int used = 0;
            for (int i = 0; i < jars.Count; i++)
            {
                ItemJar jar = jars[i];
                if (jar == null || jar.item == null) continue;
                ItemAsset asset = jar.GetAsset();
                int sx = (asset != null) ? asset.size_x : 1;
                int sy = (asset != null) ? asset.size_y : 1;
                if (jar.rot % 2 == 1) { int t = sx; sx = sy; sy = t; }
                used += sx * sy;
            }
            return used;
        }

        // ================= 空间占用 =================

        /// <summary>
        /// 容器的空间占用信息 —— 头部统计用。
        ///
        /// ⚠ 为什么要有「最大连续空位」：引擎的 `Items` 是 **2D 摆放模型**，
        ///   `tryAddItem → tryFindSpace(size_x, size_y)` 要求一块**连续**的空矩形，
        ///   且这是服务端权威检查（联机下插件绕不过）。我们的合并列表视图把空洞藏起来了，
        ///   玩家只看「总空格数」会误以为放得下（实测反馈：2×2 放不进、取出几个 1×1 又放得回）。
        ///   把「最大连续空位 W×H」摆到头部，放不放得下一眼可知。
        /// </summary>
        internal struct SpaceInfo
        {
            public int OccupiedCells;   // Σ size_x × size_y（含旋转后的占位）
            public int TotalCells;      // width × height
            public int MaxFreeW;        // 最大连续空位矩形的宽
            public int MaxFreeH;        // 　　　　　　　　　　　　高
        }

        /// <summary>
        /// 重算容器占用。网格很小（≤ 20×20），直接建占用表 + 穷举左上角行扩展，代价可忽略。
        /// ⚠ 旋转：`rot % 2 == 1` 时 size_x/size_y 互换（与引擎 `Items.resize` 的处理一致 :408-412）。
        /// </summary>
        internal static SpaceInfo ComputeSpace(Items container)
        {
            SpaceInfo info = new SpaceInfo();
            if (container == null) return info;

            int w = container.width;
            int h = container.height;
            info.TotalCells = w * h;

            bool[,] free = new bool[w, h];
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    free[x, y] = true;
                }
            }

            byte count = container.getItemCount();
            for (byte i = 0; i < count; i++)
            {
                ItemJar jar = container.getItem(i);
                if (jar == null) continue;

                ItemAsset asset = jar.GetAsset();
                int sx = (asset != null) ? asset.size_x : 1;
                int sy = (asset != null) ? asset.size_y : 1;
                if (jar.rot % 2 == 1)
                {
                    int t = sx; sx = sy; sy = t;
                }

                info.OccupiedCells += sx * sy;

                for (int dx = 0; dx < sx; dx++)
                {
                    for (int dy = 0; dy < sy; dy++)
                    {
                        int gx = jar.x + dx;
                        int gy = jar.y + dy;
                        if (gx >= 0 && gx < w && gy >= 0 && gy < h)
                        {
                            free[gx, gy] = false;
                        }
                    }
                }
            }

            // 最大连续空位矩形：穷举每个空位作左上角，先向右量出本行可延伸宽，
            // 再逐行向下扩展。面积最大者优先；同面积取「更接近正方形」者（更能装）。
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    if (!free[x, y]) continue;

                    int rowWidth = 0;
                    while (x + rowWidth < w && free[x + rowWidth, y])
                    {
                        rowWidth++;
                    }

                    for (int ww = 1; ww <= rowWidth; ww++)
                    {
                        int hh = 1;
                        while (y + hh < h)
                        {
                            bool rowFree = true;
                            for (int dx = 0; dx < ww; dx++)
                            {
                                if (!free[x + dx, y + hh])
                                {
                                    rowFree = false;
                                    break;
                                }
                            }
                            if (!rowFree) break;
                            hh++;
                        }

                        int area = ww * hh;
                        int bestArea = info.MaxFreeW * info.MaxFreeH;
                        if (area > bestArea
                            || (area == bestArea
                                && Math.Min(ww, hh) > Math.Min(info.MaxFreeW, info.MaxFreeH)))
                        {
                            info.MaxFreeW = ww;
                            info.MaxFreeH = hh;
                        }
                    }
                }
            }

            return info;
        }

        /// <summary>
        /// 头部统计行。**占用格子数必须放最前面** ——
        /// 实测反馈：「物品 18 件 · 2 项 / 48 格」会让人误读成「还剩 46 格」，
        /// 件数/项数只是次要信息，真正决定放不放得下的是占用格与最大连续空位。
        /// </summary>
        internal static string BuildHeaderText(SpaceInfo s, int totalItems, int groupCount)
        {
            return "占用 " + s.OccupiedCells + "/" + s.TotalCells + " 格"
                + " · 最大空位 " + s.MaxFreeW + "×" + s.MaxFreeH
                + " · " + totalItems + " 件 · " + groupCount + " 项";
        }

        /// <summary>
        /// 头部统计行（**扁平槽位来源**版：没有二维摆放语义时用）。
        /// ⚠ 刻意**不报「最大连续空位」** —— 那个数字来自二维占用表，
        ///   扁平模型（工业数字箱/机器槽位）不做二维碰撞检查，报了就是假信息。
        /// ⚠ 占用**超过**容量时明确标出来，不要让它长得像正常数字
        ///   （用户实测：借背包物品扮槽位时出现 `占用 12/8 格`，看着像 UI 算错了）。
        ///   超额是接入方数据层的真实状态（例如改了容量配置），**藏起来比标出来更糟**。
        /// </summary>
        internal static string BuildHeaderTextFlat(int occupied, int capacity,
            int totalItems, int groupCount)
        {
            string head = "占用 " + occupied + "/" + capacity + " 格";
            if (capacity > 0 && occupied > capacity)
            {
                head += "（⚠ 超出容量 " + (occupied - capacity) + " 格）";
            }
            return head + " · " + totalItems + " 件 · " + groupCount + " 项";
        }

        /// <summary>
        /// 分组键：物品 ID + 耐久（**不含 state**，理由见 CollectGroups）。
        ///
        /// ⚠ 但**不可分的物品（弹匣）必须每条自成一组**：弹匣的 `amount` 是**备弹量**、
        /// 不是堆叠数。按 id+quality 合并会把发数相加 —— 三匣 10/25/30 会显示成「弹匣 ×65」，
        /// 玩家以为有 65 个弹匣，而点一下其实只取出其中一整匣。
        /// ⟹ 用 `Item` 实例的引用哈希把每条拆开。
        /// ⚠ 为什么这个哈希是安全的：数字箱每帧重建的是 <c>ItemJar</c>，
        ///   而 <c>Item</c> 对象是磁盘清单里那一个（同一实例）⟹ 键跨帧稳定，
        ///   不会让格子每帧指纹变化 → 不会触发图标全量重载（那是坑 #30 那一族）。
        /// </summary>
        internal static int GroupKey(ItemJar jar)
        {
            return GroupKey(jar, false);
        }

        /// <param name="idOnly">
        /// true = 只按 ID 合并（不同耐久/质量也并成一项）。只给**扁平入口**用，
        /// 理由见 <see cref="CollectGroups(List{ItemJar},List{ItemGroup},bool)"/>。
        /// </param>
        internal static int GroupKey(ItemJar jar, bool idOnly)
        {
            return GroupKey(jar, idOnly, false);
        }

        /// <param name="mergeContentPages">
        /// true = **内容量物品（弹匣与弹药箱）也允许并成一格**（一格代表 N 件，数字箱的"合并显示"模式）。
        /// ⚠ 只放开这一族：枪械/近战永远不并 —— 它们没有"发数"这种可读的量，
        ///   并成一格只会让"点这格作用于哪一把"变成猜。
        ///   而并成一格后，"点这格取哪一件"由**调用方**按自己的消耗顺序配置决定
        ///   （工业侧 <c>ChooseSource</c>），并且必须写进 Tooltip 告诉玩家。
        /// </param>
        internal static int GroupKey(ItemJar jar, bool idOnly, bool mergeContentPages)
        {
            unchecked
            {
                int h = jar.item.id;
                if (!idOnly)
                {
                    h = h * 397 ^ jar.item.quality;
                }
                ItemAsset a = jar.GetAsset();
                if (IsIndivisible(a) && !(mergeContentPages && IsMergeableContentPage(jar.item, a)))
                {
                    h = h * 397 ^ RuntimeHelpers.GetHashCode(jar.item);
                }
                return h;
            }
        }

        /// <summary>
        /// 「允许并成一格的内容量物品」= **弹匣或弹药箱**（判据 = <see cref="IsContentContainer"/>）
        /// **且这条物品没有 state**。
        ///
        /// ★ 为什么弹药箱也算（用户 2026-10-05）：它的 `amount` 与弹匣同为**发数**，
        ///   并格与取匣的规矩完全一样；而且它比弹匣**更适合**并 ——
        ///   弹匣还带耐久/型号与枪的配对关系，弹药箱只是纯弹药容器，
        ///   "点这格取哪一箱"由备弹量决定没有额外歧义。
        ///   ⚠ 枪械 / 近战**永远不并**：它们没有"发数"这种可读的量。
        ///
        /// ⚠ 为什么把 state 也写进判据、不只判资产类型：有 state 的物品在**数据层**就是
        ///   「一条 = 一件、独立成行」（工业的存入分支这么分流），显示层若把它们并成一格，
        ///   就会出现「界面写着 6 匣、点下去只认第一条」这种两边不一致。
        ///   ⟹ 判据只有一份，对外经 <c>StorageUiPanels.IsMergeableContentPage</c> 暴露，
        ///     调用方（工业 <c>DigitalBoxRegion.ChooseSource</c>）不许自己重述。
        /// </summary>
        internal static bool IsMergeableContentPage(Item it, ItemAsset a)
        {
            if (it == null || a == null) return false;
            if (!(a is ItemMagazineAsset) && !IsContentContainer(a)) return false;
            return it.state == null || it.state.Length == 0;
        }

        /// <summary>
        /// 「一条 = 一个物品，<b>amount 不是可拆的件数</b>」的资产。
        /// 弹匣的 amount 是备弹量：不能一分为二，也不能把两匣合成一匣
        /// （原版只能通过装填把弹并到别的匣里）。
        ///
        /// ★ 枪械 / 近战同理，而且它们还多一个理由：合并格点击走的是
        ///   <b>代表那一条</b>的坐标（<c>cell.X/Y</c>），两把同型号同耐久的枪并成一格后，
        ///   玩家看到「×2」点下去其实只作用在其中一把上 —— 这是歧义，不是便利。
        ///   用户 2026-10-04 明确"两边一起改"（数字箱与普通箱子都生效）。
        /// ⚠ 判据按<b>资产类型</b>，不是"有没有 state"：state 对玩家不可见、
        ///   而且工业物品把 UID/电量存在里面，用它会误伤可堆叠的工业物品。
        /// </summary>
        internal static bool IsIndivisible(ItemAsset a)
        {
            if (a == null) return false;
            // 弹匣 / 枪械 / 近战按类型判；**再加上"内容量类"**（弹药箱那一族，见下面）
            return a is ItemMagazineAsset || a is ItemGunAsset || a is ItemMeleeAsset
                || IsContentContainer(a);
        }

        /// <summary>缓存：物品 ID → 是不是"内容量类"（配方很少，但本判据每帧每个物品都要问）。</summary>
        private static readonly Dictionary<ushort, bool> _contentContainer =
            new Dictionary<ushort, bool>();

        /// <summary>
        /// 「一条记录 = 一件物品，但它的 <c>amount</c> 是<b>可以倒出去的内容量</b>」的资产。
        ///
        /// 判据取<b>引擎自己的定义</b>：这个资产的配方里有一条 <c>FillTargetItem</c>
        /// 且<b>自己就是那一路输入</b> —— 也就是"引擎会把它身上的量倒进别的东西里"。
        /// 原版命中 56 个 = 51 个弹匣 + 5 个弹药箱（Military_40 / Civilian_40 / Ranger_40 /
        /// Military_20 / Ranger_20），一个不多一个不少。
        ///
        /// ★ 为什么<b>不能</b>用「<c>MaxAmount &gt; 1</c>」当判据（本项目差点走错的一步）：
        ///   扫完全部 2198 个原版物品 dat 后，<c>Amount &gt; 1</c> 的 56 个<b>恰好全是</b>弹匣+弹药箱，
        ///   看起来"MaxAmount&gt;1 ⟺ 内容量"成立；但工业内容包里有 16 个 <c>Amount &gt; 1</c> 的
        ///   <b>真堆叠</b>（铁矿石/焦炭/铁锭 8、超级布料 20、磁盘 4、电路板 2 …），
        ///   用 MaxAmount 判会把它们全打成不可分 ⟹ 一格只准放一件、整条材料链卡死。
        ///   ⟹ 只有"会不会被倒出去"这个<b>行为</b>信号能区分"一格 8 件"与"一箱 8 发"。
        /// ⚠ 与工业侧 <c>PowerNetwork.IsIndivisibleAsset</c> 同源同判据（工业主文件不能引用本插件类型，
        ///   只能各写一份"从引擎数据推导"的实现）—— 自检第 25 节钉住两边都走 FillTargetItem。
        /// </summary>
        internal static bool IsContentContainer(ItemAsset a)
        {
            if (a == null || a.amount <= 1) return false;   // 单件恒为 1 的物品谈不上"内容量"
            bool cached;
            if (_contentContainer.TryGetValue(a.id, out cached)) return cached;

            bool yes = false;
            List<Blueprint> bps = a.blueprints;
            if (bps != null)
            {
                for (int i = 0; i < bps.Count && !yes; i++)
                {
                    Blueprint bp = bps[i];
                    if (bp == null || bp.Operation != EBlueprintOperation.FillTargetItem) continue;
                    BlueprintSupply[] sup = bp.supplies;
                    if (sup == null) continue;
                    for (int j = 0; j < sup.Length; j++)
                    {
                        if (sup[j] != null && sup[j].IsItem(a)) { yes = true; break; }
                    }
                }
            }
            _contentContainer[a.id] = yes;
            return yes;
        }

        private static int CompareGroup(ItemGroup a, ItemGroup b)
        {
            int c = a.First.item.id.CompareTo(b.First.item.id);
            if (c != 0) return c;

            // 耐久高的在前
            c = b.First.item.quality.CompareTo(a.First.item.quality);
            if (c != 0) return c;

            // ⚠ 必须有最后这一级：不可分物品（弹匣/枪械）现在按 Item 引用哈希分组，
            //   同 id 同耐久的两把枪在前两级是<b>并列</b>的，而 List.Sort <b>不稳定</b>
            //   ⟹ 每帧可能互换顺序 ⟹ 格子指纹每帧变化 → 反复整格重建 + 图标全量重载
            //   （坑 #30 那一族），玩家看到的还会"每帧左右跳动"。
            //   拿分组键当末级判据 = 全序，同样输入永远排出同样结果。
            return a.Key.CompareTo(b.Key);
        }

        // ================= 文本 =================

        /// <summary>
        /// `ESleekFontSize` 的**理想高度**（像素）—— 直接取自引擎枚举的注释
        /// （`SDG.Glazier/SleekFontSize.cs`：Tiny 8 / Small 20 / Default 30 / Medium 50 / Large 70）。
        ///
        /// ★ 为什么需要它：视图里那些"文字行高"常量（如大格子的 26 / 24）**只对 `Small` 成立**。
        ///   一旦字号可配，行高就必须**按字号算**，否则调大字号会把字挤掉
        ///   （⚠ 行高 < 24 时一个字都不渲染，且不报错 —— 坑 #21 / 铁律 #18）。
        /// ⚠ `Title` 没有注释值（只给主菜单用）⟹ 按 `Default` 处理（宁可偏大也不挤）。
        /// </summary>
        internal static float IdealTextHeight(ESleekFontSize size)
        {
            switch (size)
            {
                case ESleekFontSize.Tiny: return 8f;
                case ESleekFontSize.Small: return 20f;
                case ESleekFontSize.Medium: return 50f;
                case ESleekFontSize.Large: return 70f;
                default: return 30f;      // Default / Title
            }
        }

        /// <summary>列表视图用的一行文本：名字 · 数量 · 耐久（数量为**合并后的总数**）。</summary>
        internal static string BuildRowText(ItemAsset asset, ItemGroup g)
        {
            ItemJar jar = g.First;
            string t = (asset != null) ? asset.FriendlyName : ("物品 " + jar.item.id);

            // 数量一律走 BuildCountText（空容器也要有字，见那里的注释）
            string count = BuildCountText(g);
            if (count.Length > 0) t += "  " + count;
            // ⚠ 只有「该物品类型本身有耐久」才显示耐久。
            //   判据用引擎的 `ItemAsset.showQuality`（原版 SleekItem 正是用它决定画不画
            //   百分比，见 SleekItem.cs:181；dat 键 Override_Show_Quality，子类各自覆盖：
            //   枪/近战/服装恒 true，弹匣看 stuck>0，消耗品看 FOOD/WATER…）。
            //   只用 `quality > 0` 判断是错的 —— 无耐久物品的 quality 也是满值 100，
            //   于是金属线、超织物这类会一直显示「100%」（实测反馈过）。
            if (asset != null && asset.showQuality && jar.item.quality > 0)
            {
                t += "  耐久 " + jar.item.quality + "%";
            }
            return t;
        }

        /// <summary>物品名（资产缺失时退回内部 ID）。</summary>
        internal static string BuildName(ItemAsset asset, ItemJar jar)
        {
            return (asset != null) ? asset.FriendlyName : ("物品 " + jar.item.id);
        }

        // ---------- 搜索：把"中文名 + 英文名 + 描述 + 拼音"统一成一次匹配 ----------------
        //
        // 为什么要缓存：这段每帧每个可见物品都会被问，而"拼拼音"是逐字查表 + 拼字符串。
        // 按**物品 ID** 缓存 ⟹ 一个 ID 一生只算一次（名称在运行期不会变）。
        private static readonly Dictionary<ushort, SearchIndex> _searchIndex =
            new Dictionary<ushort, SearchIndex>();

        private struct SearchIndex
        {
            public string Text;       // 中文名 + 英文名 + 描述，全小写，用空格连着
            public string Cn;         // 只留中文名（拼音要按它逐字展开）
        }

        /// <summary>
        /// 这个物品是否匹配搜索词。匹配面：中文名、**英文名**、
        /// 以及**拼音全拼**与**拼音首字母**（`dao`→刀、`dj`→刀架）。
        /// ⚠ 暂不含描述，原因见下面缓存处那条注释。
        ///
        /// ★ 英文名是免费的：`Asset.name` 就是 `English.dat` 里那个名字，
        ///   而 `ItemAsset` 覆写了 <c>FriendlyName</c> 返回本地化名
        ///   （`Asset.cs:39`/`:164`、`ItemAsset.cs:175`）⟹ 两个都拼进检索串就能中英混搜。
        /// ⚠ 空 query = 全部匹配（搜索框没内容时不该把界面清空）。
        /// </summary>
        internal static bool MatchesQuery(ItemAsset a, string query)
        {
            if (a == null) return false;
            if (string.IsNullOrEmpty(query)) return true;

            SearchIndex ix;
            if (!_searchIndex.TryGetValue(a.id, out ix))
            {
                ix = new SearchIndex();
                ix.Cn = a.FriendlyName;
                // ⚠ 这里**故意不含描述**：原版的描述不是字段，要调
                //   `ItemAsset.BuildDescription(ItemDescriptionBuilder, Item)`（ItemAsset.cs:274）——
                //   那是 UI 级的组装（要 builder、要物品实例、会拼耐久/配件/分类等一堆行），
                //   放进每帧的搜索路径不划算。要加的话得另做一份"按 ID 缓存一次的纯文本描述"，
                //   并且要确认它不含本地化键未就绪的窗口期（启动早期 FriendlyName 可能是 ID）。
                ix.Text = ((a.FriendlyName ?? "") + " " + (a.name ?? "")).ToLowerInvariant();
                _searchIndex[a.id] = ix;
            }
            string q = query.ToLowerInvariant().Trim();
            if (q.Length == 0) return true;
            if (ix.Text.IndexOf(q, StringComparison.Ordinal) >= 0) return true;

            // 只有纯 ASCII 的查询才值得去拼拼音（中文查询在上面那步就已经命中或不会命中）
            return PinyinMatches(ix.Cn, q);
        }

        /// <summary>
        /// 拼音匹配：把中文名的汉字逐个展开成"读音候选"，看查询能否由**连续若干个字**的
        /// **读音前缀**依次拼出（每个字贡献 1 … 它读音的全长 个字符）。
        ///
        /// ★ 26.10.3.30 改"任意前缀"（原来是"整读音 / 只吃首字母"两条路）——
        ///   后者会破坏**前缀单调性**，打字过程中结果一会儿有一会儿没（用户实测
        ///   `jin`✓`jins`✓ 但 `jinsh`✗`jinshu`✓）。判据见 <see cref="Consume"/> 的注释。
        /// 回溯规模 = 字数 × 每字 ≤3 个读音 × ≤6 个前缀长度，且结果按 (ID, 查询) 不缓存
        /// —— 查询是用户手打的短串，逐帧重算的代价远小于缓存带来的失效风险。
        /// </summary>
        internal static bool PinyinMatches(string cn, string q)
        {
            if (string.IsNullOrEmpty(cn) || string.IsNullOrEmpty(q)) return false;
            for (int i = 0; i < q.Length; i++)
            {
                char c = q[i];
                if (c > 127) return false;         // 非 ASCII 查询不走拼音这条路
            }
            int n = cn.Length;
            for (int start = 0; start < n; start++)
            {
                if (Consume(cn, start, q, 0)) return true;
            }
            return false;
        }

        /// <summary>从 <paramref name="ci"/> 起，能否用连续的汉字读音把 <paramref name="q"/> 的 <paramref name="qi"/> 之后吃干净。</summary>
        private static bool Consume(string cn, int ci, string q, int qi)
        {
            if (qi >= q.Length) return true;                     // 查询已被吃完 = 命中
            if (ci >= cn.Length) return false;

            string[] readings = StorageUiPinyinData.Of(cn[ci]);
            if (readings != null)
            {
                for (int r = 0; r < readings.Length; r++)
                {
                    string s = readings[r];
                    if (s.Length == 0) continue;
                    // ★ 26.10.3.30：这个字可以贡献它读音的**任意前缀**（1 … s.Length 个字符）。
                    //
                    // 原来只有两条路（"吃掉整个读音" / "只吃首字母"），那会**破坏前缀单调性**：
                    //   用户实测「金属板」—— `jin` ✓ `jins` ✓ 但 `jinsh` ✗ `jinshu` ✓，
                    //   打字过程中结果一会儿有一会儿没（`sh` 既不是完整读音、也不是首字母）。
                    // 而"任意前缀"把两条路都覆盖了（长度 1 = 首字母、长度 = s.Length = 整读音），
                    // 同时天然满足单调：**已经命中的目标，它前面任意长的输入也一定命中**。
                    // ⟹ `ji` / `jinsh` / `jshuban` / `jsban` / `jinsb` / `jshuba` 现在全都能中。
                    int max = s.Length < (q.Length - qi) ? s.Length : (q.Length - qi);
                    for (int take = 1; take <= max; take++)
                    {
                        // ⚠ break 而不是 continue：前缀一旦不匹配，更长的 take 也不可能匹配
                        //   （s 是固定串）——剪枝，省掉无谓回溯。
                        if (q[qi + take - 1] != s[take - 1]) break;
                        if (Consume(cn, ci + 1, q, qi + take)) return true;
                    }
                }
            }
            // 这个字不是汉字（或表里没有）：查询里对应位置必须正好是它本身，然后继续
            if (char.ToLowerInvariant(cn[ci]) == q[qi])
            {
                return Consume(cn, ci + 1, q, qi + 1);
            }
            return false;
        }

        /// <summary>
        /// 数量文本（大格子第二行 / 列表行 / Tooltip 共用一份，别三处各写一遍）。
        ///
        /// ⚠ 为什么需要它：空容器（被打空的弹匣、空水瓶）的 `item.amount == 0`，
        ///   而引擎的 `DeleteAmount(..., alwaysDeleteAtZeroAmount: false)` **故意把空容器留着**
        ///   （合成弹匣就是这么留的）—— 旧写法「只有 TotalAmount > 1 才写字」于是对它们
        ///   一个字都不显示，玩家看到「图标 + 名字」就没了，会以为格子坏了（实测反馈）。
        ///   合计为 0 时按**件数**报，并标出是空的。
        /// </summary>
        internal static string BuildCountText(ItemGroup g)
        {
            // ⚠ 不可分物品（弹匣/枪械）的 `amount` <b>不是件数</b>：一匣 25 发的 amount 是 25，
            //   直接印「×25」会被读成"25 个弹匣"。所以这里报的是<b>条数</b>，
            //   弹匣另外把备弹量带单位写出来。
            if (IsIndivisible(g.First.GetAsset()))
            {
                // 弹匣与弹药箱共用这一处：判据是"amount 是可倒出去的内容量"，不是资产类型
                // —— 弹药箱（Ranger_40 那五个）的 amount 同样是**发数**，
                // 只按 `is ItemMagazineAsset` 判会让弹药箱退回"不显示数字"，
                // 玩家就分不出满箱与空箱（用户 2026-10-05 实测指出弹药箱那一族）。
                if (IsContentContainer(g.First.GetAsset()))
                {
                    // 两种显示模式共用这一处：**条数**决定该报什么单位，不需要调用方再传开关
                    //  · 一格 = 一件（默认，或没开合并显示）→ 报**备弹量**：`25发`
                    //  · 一格 = N 件（数字箱开了合并显示）→ 报**件数**：弹匣 `6匣` / 弹药箱 `6箱`
                    // ⚠ 绝不印 `×25`：那是把备弹量伪装成件数（用户 2026-10-04 实测指出）；
                    //   也绝不印 `6匣 145发`：一个格子两个数就是"×N（M个）"那个被否掉的双报数。
                    //   合计发数放 Tooltip（BuildTooltip 里走「共 N 匣/箱（合计 M 发）」）。
                    // ★ 单位按类型分开写：弹药箱并格是用户 2026-10-05 明确要的
                    //   （"弹药箱堆叠事实上比弹匣堆叠更好" —— 它只是纯弹药容器，没有匣/枪配对那层歧义）。
                    if (g.Count > 1)
                    {
                        return g.Count +
                            ((g.First.GetAsset() is ItemMagazineAsset) ? "匣" : "箱");
                    }
                    return g.First.item.amount + "发";
                }
                return g.Count > 1 ? ("×" + g.Count) : "";
            }

            // 可堆叠的普通物品：合计量才是玩家关心的"有几个"。
            // ⚠ 曾经在这里加过「×N（M个）」标条数 —— 已删：
            //   不可堆叠物品 N 与 M 必然相等（写了等于没写），
            //   可堆叠物品被拆成几条对玩家取放没有任何影响，纯属噪声。
            if (g.TotalAmount > 1) return "×" + g.TotalAmount;

            if (g.TotalAmount == 0)
            {
                return g.Count > 1 ? ("×" + g.Count + "件 空") : "空";
            }

            // 合计正好 1 却有多件（例如一个 1 发的弹匣 + 一个空弹匣）
            return g.Count > 1 ? ("×" + g.Count + "件 共1") : "";
        }

        /// <summary>
        /// 数量与耐久（大格子里的第二行，超出上限时视图不建这个标签）。
        /// 数量用**合并后的总数**：合并显示时「一件 ×1」没有意义，
        /// 玩家关心的是箱子里一共有多少个（如 9 件金属线显示 ×9）。
        /// 耐久同样必须过 `asset.showQuality`（理由见 BuildRowText）。
        /// </summary>
        internal static string BuildAmountText(ItemAsset asset, ItemGroup g)
        {
            string t = BuildCountText(g);
            if (asset != null && asset.showQuality && g.First.item.quality > 0)
            {
                if (t.Length > 0) t += "  ";
                t += g.First.item.quality + "%";
            }
            return t;
        }

        /// <summary>鼠标悬浮提示：名字 + 描述 + 耐久 + 合并件数 + 占用格数。</summary>
        /// <remarks>
        /// ★ `26.10.3.63` 起**一律委托给公开的 `StorageUiTooltip`** —— 三方自己画格子拿到的
        /// 就是这一份，措辞不可能漂。只有**「一格多件且是弹药容器」**这一支在这里，
        /// 因为那两个量是"件（匣/箱）"与"发"，与普通物品的"件 / 合计 ×N"不是一回事。
        /// </remarks>
        internal static string BuildTooltip(ItemAsset asset, ItemGroup g)
        {
            if (asset == null) return "";
            ItemJar jar = g.First;

            if (g.Count > 1 && IsContentContainer(asset))
            {
                // 合并显示模式下"一格多件"：这里的两个量是**件**（匣/箱）与**发**，
                // 别沿用"件 / ×"那套（会被读成"6 个弹匣共 145 个弹匣"这种废话）。
                // 单位按类型分：弹匣说"匣"、弹药箱说"箱"（用户 2026-10-05 要两者同权）。
                return StorageUiTooltip.ItemHead(asset, jar.item.quality)
                    + "\n共 " + g.Count + ((asset is ItemMagazineAsset) ? " 匣" : " 箱")
                    + "（合计 " + g.TotalAmount + " 发）"
                    + StorageUiTooltip.SizeLine(asset);
            }

            return StorageUiTooltip.ForItem(asset, g.Count, g.TotalAmount, jar.item.quality);
        }
    }
}
