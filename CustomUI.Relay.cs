using System;
using System.Collections.Generic;
using SDG.Unturned;

namespace CustomUI
{
    /// <summary>
    /// 一件借道复制品（放进中转 <see cref="Items"/> 页的那个 <see cref="Item"/> 实例）最终的归宿。
    /// </summary>
    public enum RelayFate
    {
        /// <summary>落到了玩家的某一页（背包 / 装备栏 / 时装页）—— 物品真的离箱了，调用方该扣账。</summary>
        LandedOnPlayerPage = 0,
        /// <summary>被丢到地上（原版语义：拖到「附近物品」区 = <c>sendDropItem</c>）—— 同样该扣账。</summary>
        DroppedOnGround = 1,
        /// <summary>还在中转页里（同页挪动 / 落点校验失败被引擎退回）—— 账不能动。</summary>
        StillInRelay = 2,
        /// <summary>界面关闭时仍留在中转页 —— 复制品随页丢弃，账本来就没动过。</summary>
        DiscardedOnClose = 3
    }

    /// <summary>一次归宿判定的结果。<see cref="Provenance"/> 是调用方自己登记的对象，本插件原样带回。</summary>
    public struct RelayResolved
    {
        public Item Clone;
        public object Provenance;
        /// <summary>
        /// 登记这件复制品时声明的**来源**（<see cref="StorageUiRelay.Track(Item, object, string)"/>）。
        /// 两参 Track 登记的是 null。按来源订阅的 handler 只会收到与它同源的事件。
        /// </summary>
        public string Source;
        public RelayFate Fate;
        /// <summary>落点页号（仅 <see cref="RelayFate.LandedOnPlayerPage"/> 有意义）。</summary>
        public byte Page;
        public byte X;
        public byte Y;
        /// <summary>复制品离开中转页时的数量（扣账就按这个数，别用显示值推算）。</summary>
        public byte Amount;
    }

    public delegate void RelayResolvedHandler(RelayResolved resolved);

    /// <summary>
    /// **中转页 ↔ 调用方数据源的对账**（借道方案的第 3 步）。
    ///
    /// 要解决的问题：物品从中转页被引擎搬走之后，插件自己的清单（例如数字箱的记账列表）
    /// 并不知道 —— 不扣账就是**凭空复制**。
    ///
    /// 为什么能靠引用对上（三条都已回源码核实）：
    /// · <c>PlayerInventory.ReceiveDragItem</c> 是 <c>removeItem(page_0, index)</c> +
    ///   <c>items[page_1].addItem(x_1, y_1, rot_1, jar.item)</c> —— **同一个 Item 实例**被搬走；
    /// · <c>ReceiveDropItem</c>（丢到地上）也是 <c>ItemManager.dropItem(jar.item, ...)</c> +
    ///   <c>removeItem(page, index)</c>，同一个实例；
    /// · <c>Items.onItemRemoved</c> 是 public 字段委托（<c>Items.cs:27</c>），
    ///   且引擎自己注释警告：**触发时物品还没真的从列表里移除**（public issue #4894）。
    ///
    /// ⟹ 所以**不能在事件里判定落点**（那一刻它还在原页），必须延后一 Tick 再按引用扫全部玩家页。
    ///
    /// ⚠ 只订阅 <c>onItemRemoved</c>。绝不去接 <c>onStateUpdated</c>：
    ///   原版对真实容器是 <c>items.onStateUpdated = onStateUpdated</c>（**赋值**，
    ///   <c>InteractableStorage.cs:412</c>），插件接上去会把原版的存盘钩子顶掉。
    ///
    /// **订阅口按来源记名**（`26.10.3.41` 起）：中转页是**全局单例**（同一时刻只有一页），
    /// 但订阅者是全局列表 —— 不按来源切，A 插件的落点会送进 B 插件的回调里，
    /// B 若不认来历就**扣错账**（= 跨插件串账）。所以：
    /// · 登记用 <see cref="Track(Item, object, string)"/>、订阅用
    ///   <see cref="AddResolvedHandler(string, RelayResolvedHandler)"/>，**两头同一个来源串**；
    /// · 全局版（<see cref="AddResolvedHandler(RelayResolvedHandler)"/>）保留给旁观者/诊断，
    ///   并且会收到**别人**的事件 —— 用它的代码必须自己认来历；
    /// · 卸载时用 <see cref="RemoveResolvedHandlers(string)"/> 一次清掉自己那份。
    ///   这套记名规矩与 <c>StorageUiPanels.SetRegionVisible(source, …)</c> 一致。
    /// </summary>
    public static class StorageUiRelay
    {
        private class Entry
        {
            public Item clone;
            public object provenance;
            public string source;   // ★ 登记时声明的来源（两参 Track 时为 null = 没声明）
            public byte amount;     // 登记时的数量（落点判定用不到，报告要用）
        }

        /// <summary>一次订阅。<see cref="source"/> 为 null = 全局（收所有来源的事件）。</summary>
        private class Sub
        {
            public string source;
            public RelayResolvedHandler handler;
        }

        private static readonly List<Entry> tracked = new List<Entry>();
        private static readonly List<Entry> pending = new List<Entry>();
        private static readonly List<Sub> subs = new List<Sub>();

        /// <summary>当前盯着的中转页（null = 没在借道）。</summary>
        private static Items relay;
        private static ItemRemoved watcher;

        private static bool warnedNoHandler;

        /// <summary>本插件只负责"报告归宿"，扣账由调用方做 —— 没人接就是白忙活。</summary>
        private static void WarnNoHandlerOnce()
        {
            if (warnedNoHandler) return;
            warnedNoHandler = true;
            CustomUIPlugin.Warn("中转页有物品被搬走，但没人接这件东西的归宿 —— " +
                "调用方若不据此扣自己的清单，就等于凭空复制" +
                "（若你确实挂了订阅却还看到这条：多半是**登记与订阅的来源对不上**，" +
                "用 ResolvedHandlerSources() / HasGlobalHandlers 查）");
        }

        /// <summary>
        /// 订阅落点回报 —— **全局版：任何来源的事件都会送到你这里**。
        ///
        /// ⚠ 两个插件同时用中转页时，你会收到**别人**的归宿事件；不认来历就扣账
        ///   = 扣错账（本插件自己的接入探针就栽过：它按 <see cref="RelayFate"/> 直接扣库存，
        ///   于是工业数字箱的落点也让它扣了一次，见 README 坑 #66）。
        ///   ⟹ 正经接入方一律用 <see cref="AddResolvedHandler(string, RelayResolvedHandler)"/>
        ///   按来源订阅；全局版只留给"本来就是旁观者"的排查工具（接入探针 / 诊断面板）。
        /// </summary>
        public static void AddResolvedHandler(RelayResolvedHandler handler)
        {
            AddSub(null, handler);
        }

        /// <summary>
        /// ★ 按下**来源**订阅：只收到在
        /// <see cref="Track(Item, object, string)"/> 里声明了同一 <paramref name="source"/>
        /// 的那些归宿事件。
        ///
        /// <paramref name="source"/> 填调用方自己的标识（建议用插件 GUID）——
        /// 与 <c>StorageUiPanels.SetRegionVisible(source, …)</c> 同一套记名规矩：
        /// **跨插件共享的注册口必须带来源，不能留全局位**。
        ///
        /// ⚠ 光在订阅端声明不够：**登记（Track）也要带同一个来源**，否则一件都收不到
        ///   （对账链剪断 = 调用方不扣账 = 凭空复制）。
        /// </summary>
        public static void AddResolvedHandler(string source, RelayResolvedHandler handler)
        {
            AddSub(source, handler);
        }

        private static void AddSub(string source, RelayResolvedHandler handler)
        {
            if (handler == null) return;
            if (string.IsNullOrEmpty(source)) source = null;
            for (int i = 0; i < subs.Count; i++)
            {
                if (string.Equals(subs[i].source, source, StringComparison.Ordinal) &&
                    subs[i].handler == handler) return;   // 同一(来源,委托)只留一份
            }
            Sub s = new Sub();
            s.source = source;
            s.handler = handler;
            subs.Add(s);
        }

        /// <summary>撤销某个委托的订阅（无论它当初是按哪个来源登记的）。</summary>
        public static void RemoveResolvedHandler(RelayResolvedHandler handler)
        {
            if (handler == null) return;
            for (int i = subs.Count - 1; i >= 0; i--)
            {
                if (subs[i].handler == handler) subs.RemoveAt(i);
            }
        }

        /// <summary>
        /// 撤销某个来源的**全部**订阅（插件卸载时用）——
        /// 与 <c>StorageUiPanels.ClearRegionVisibility(source)</c> 对称：
        /// 卸载方自己清干净，不留一个指向已卸载插件的委托在全局列表里。
        /// </summary>
        public static void RemoveResolvedHandlers(string source)
        {
            if (string.IsNullOrEmpty(source)) return;
            for (int i = subs.Count - 1; i >= 0; i--)
            {
                if (string.Equals(subs[i].source, source, StringComparison.Ordinal)) subs.RemoveAt(i);
            }
        }

        /// <summary>
        /// 现在有哪些**来源**在订阅（不含全局订阅者）。排查「我的回调怎么没来」用 ——
        /// 没有这行信息就只能猜（与 <c>RegionHidingSources</c> 同一个用途）。
        /// </summary>
        public static List<string> ResolvedHandlerSources()
        {
            List<string> list = new List<string>();
            for (int i = 0; i < subs.Count; i++)
            {
                string s = subs[i].source;
                if (s == null || list.Contains(s)) continue;
                list.Add(s);
            }
            return list;
        }

        /// <summary>有没有全局订阅者（= 有人会被**别人的**事件打扰）。</summary>
        public static bool HasGlobalHandlers
        {
            get
            {
                for (int i = 0; i < subs.Count; i++)
                {
                    if (subs[i].source == null) return true;
                }
                return false;
            }
        }

        public static bool IsAttached
        {
            get { return relay != null; }
        }

        /// <summary>已登记、还在中转页里等着的复制品数。</summary>
        public static int TrackedCount
        {
            get { return tracked.Count; }
        }

        /// <summary>已离开中转页、还没判定落点的数量（正常只存活一 Tick）。</summary>
        public static int PendingCount
        {
            get { return pending.Count; }
        }

        // ================= 生命周期（由宿主调用） =================

        /// <summary>开始盯住某个中转页。重复调用会先摘掉上一个页面的订阅。</summary>
        internal static void Attach(Items page)
        {
            Detach();
            if (page == null) return;
            relay = page;
            if (watcher == null) watcher = new ItemRemoved(OnItemRemoved);
            try
            {
                page.onItemRemoved += watcher;
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("订阅中转页 onItemRemoved 失败：" + e.Message);
                relay = null;
            }
        }

        /// <summary>停止盯页：把已离开的先判完，还留在页里的报成 <see cref="RelayFate.DiscardedOnClose"/>。</summary>
        internal static void Detach()
        {
            if (relay == null && tracked.Count == 0 && pending.Count == 0) return;

            if (relay != null && watcher != null)
            {
                try { relay.onItemRemoved -= watcher; }
                catch (Exception) { /* 页面可能已被引擎换掉，摘不掉就算了 */ }
            }

            // 已经离开页面的：照常判定（它可能就在关闭前那一瞬落进了背包）
            ResolvePending();

            // ⚠ 关界面之前先把"不是我们登记的复制品"还回玩家身上。
            //   原版 `closeStorage()` 是 `updateItems(STORAGE, null)` —— 整个 Items 对象被换成
            //   一个新的空页，留在里面的东西**没有任何回收路径**（= 凭空消失）。
            //   正常情况下这一页只有我们的复制品（放入已被 DropIntoStorage 拒绝），
            //   但别的插件、或以后新加的写入路径都可能留下真物品 ⟹ 这道兜底必须有。
            EvacuateForeignItems();

            // 还留在页面里的：随页丢弃，账不该动
            for (int i = tracked.Count - 1; i >= 0; i--)
            {
                Entry en = tracked[i];
                tracked.RemoveAt(i);
                Report(en, RelayFate.DiscardedOnClose, 0, 0, 0);
            }

            relay = null;
        }

        private static bool IsTracked(Item item)
        {
            if (item == null) return false;
            for (int i = 0; i < tracked.Count; i++)
            {
                if (ReferenceEquals(tracked[i].clone, item)) return true;
            }
            return false;
        }

        private static void EvacuateForeignItems()
        {
            if (relay == null) return;
            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null) return;

            try
            {
                byte count = relay.getItemCount();
                int moved = 0;
                int dropped = 0;
                for (byte i = 0; i < count; i++)
                {
                    ItemJar jar = relay.getItem(i);
                    if (jar == null || jar.item == null) continue;
                    // 我们登记过的复制品**不能**还给玩家 —— 那是凭空造物品。
                    // 它随页丢弃才是对的（调用方的账本来就没扣）。
                    if (IsTracked(jar.item)) continue;

                    // 先自己试一次，目的是**知道它到底去了哪**：
                    //  · tryAddItem(item, false) 成功 = 真的进了玩家身上
                    //    （auto=false 跳过装备分支，不会因为"放回去"把枪/衣服穿上身）；
                    //  · 失败 = 背包放不下，只能交给引擎掉在脚下
                    //    （`forceAddItemAuto` 的失败方式就是 `ItemManager.dropItem`，不销毁，
                    //    `PlayerInventory.cs:615-621`）。
                    // ⚠ 上一版只笼统说"已塞回玩家身上"，用户实测回"没给玩家"——
                    // 背包满时东西其实掉在脚下，而那句话把两种结局混成了一句（坑 #57 同族）。
                    if (player.inventory.tryAddItem(jar.item, false)) moved++;
                    else
                    {
                        player.inventory.forceAddItemAuto(jar.item, false, false, false);
                        dropped++;
                    }
                }
                if (moved > 0 || dropped > 0)
                {
                    CustomUIPlugin.Warn("中转页上留着 " + (moved + dropped) +
                        " 件不是本插件登记的物品 —— 塞回背包 " + moved + " 件" +
                        (dropped > 0 ? "，背包放不下、掉在脚下 " + dropped + " 件" : "") +
                        "。正常不该发生：借道期间往这一页放东西已被所有入口拒绝。");
                }
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("清空中转页失败：" + e.Message);
            }
        }

        // ================= 调用方的登记口 =================

        /// <summary>
        /// 登记"这个复制品是我放进中转页的，来历是 <paramref name="provenance"/>（本插件不解释它）"。
        /// 必须在 <c>OpenVirtualStorage</c> 之后、托起之前调用。
        /// </summary>
        public static bool Track(Item clone, object provenance)
        {
            return Track(clone, provenance, null);
        }

        /// <summary>
        /// 同上，并声明**来源** —— 只有声明了同一来源的订阅者会收到这件东西的归宿
        /// （见 <see cref="AddResolvedHandler(string, RelayResolvedHandler)"/>）。
        /// </summary>
        public static bool Track(Item clone, object provenance, string source)
        {
            if (clone == null || relay == null) return false;
            // 同一个实例登记两次 = 落点会被报告两次 = 调用方可能扣两次账。宁可拒掉。
            for (int i = 0; i < tracked.Count; i++)
            {
                if (ReferenceEquals(tracked[i].clone, clone)) return false;
            }
            Entry en = new Entry();
            en.clone = clone;
            en.provenance = provenance;
            en.source = string.IsNullOrEmpty(source) ? null : source;
            en.amount = clone.amount;
            tracked.Add(en);
            return true;
        }

        /// <summary>取消登记（调用方自己把复制品收回去了，不希望再收到落点报告）。</summary>
        public static bool Untrack(Item clone)
        {
            if (clone == null) return false;
            for (int i = 0; i < tracked.Count; i++)
            {
                if (ReferenceEquals(tracked[i].clone, clone))
                {
                    tracked.RemoveAt(i);
                    return true;
                }
            }
            for (int i = 0; i < pending.Count; i++)
            {
                if (ReferenceEquals(pending[i].clone, clone))
                {
                    pending.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        // ================= 信号与判定 =================

        private static void OnItemRemoved(byte page, byte index, ItemJar jar)
        {
            // ⚠ 这个回调在引擎自己的移除流程里（removeItem → onItemRemoved?.Invoke），
            //   抛出去会打断搬运。这里**只记账、不判定、不外抛**。
            try
            {
                if (relay == null || jar == null || jar.item == null) return;
                if (!ReferenceEquals(relay, GetStoragePage())) return;   // 不是我们那张页

                for (int i = tracked.Count - 1; i >= 0; i--)
                {
                    if (!ReferenceEquals(tracked[i].clone, jar.item)) continue;
                    Entry en = tracked[i];
                    tracked.RemoveAt(i);
                    pending.Add(en);
                    return;
                }
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("中转页移除回调异常：" + e.Message);
            }
        }

        /// <summary>每帧由插件 Update 调；没有待判定项时立刻返回。</summary>
        internal static void Tick()
        {
            if (pending.Count == 0) return;
            ResolvePending();
        }

        private static void ResolvePending()
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Entry en = pending[i];
                pending.RemoveAt(i);

                byte foundPage;
                byte foundX;
                byte foundY;
                bool onPlayerPage = FindItemPage(en.clone, out foundPage, out foundX, out foundY);

                if (!onPlayerPage)
                {
                    // 任何玩家页上都没有 = 被丢到地上了（ReceiveDropItem 的 dropItem + removeItem）
                    Report(en, RelayFate.DroppedOnGround, 0, 0, 0);
                    continue;
                }

                if (relay != null && foundPage == PlayerInventory.STORAGE &&
                    ReferenceEquals(GetStoragePage(), relay))
                {
                    // 还在我们那张中转页里 = 同页挪动 / 落点被引擎退回 → 继续盯着，账不动
                    tracked.Add(en);
                    continue;
                }

                Report(en, RelayFate.LandedOnPlayerPage, foundPage, foundX, foundY);
            }
        }

        /// <summary>
        /// 按**引用**在玩家页（0..<c>PAGES-2</c>）里找这个 Item 实例。
        /// 原版 <c>sendDragItem</c> 的落点校验就是 <c>page &lt; PAGES - 1</c>，
        /// 所以附近物品页(8) 永远不可能是落点 —— 扫到 7 就够。
        /// </summary>
        private static bool FindItemPage(Item item, out byte page, out byte x, out byte y)
        {
            page = 0;
            x = 0;
            y = 0;
            if (item == null) return false;

            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null || player.inventory.items == null) return false;

            byte max = (byte)(PlayerInventory.PAGES - 2);
            for (byte p = 0; p < max; p++)
            {
                Items items = player.inventory.items[p];
                if (items == null) continue;
                byte count = items.getItemCount();
                for (byte i = 0; i < count; i++)
                {
                    ItemJar jar = items.getItem(i);
                    if (jar == null || jar.item == null) continue;
                    if (!ReferenceEquals(jar.item, item)) continue;
                    page = p;
                    x = jar.x;
                    y = jar.y;
                    return true;
                }
            }
            return false;
        }

        private static Items GetStoragePage()
        {
            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null || player.inventory.items == null) return null;
            if (PlayerInventory.STORAGE >= player.inventory.items.Length) return null;
            return player.inventory.items[PlayerInventory.STORAGE];
        }

        /// <summary>这件东西的归宿有没有人接（全局订阅者，或声明了同一来源的订阅者）。</summary>
        private static bool HasAnyReceiver(string source)
        {
            for (int i = 0; i < subs.Count; i++)
            {
                if (subs[i].source == null) return true;                  // 全局订阅者收所有
                if (string.Equals(subs[i].source, source, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static void Report(Entry en, RelayFate fate, byte page, byte x, byte y)
        {
            RelayResolved r = new RelayResolved();
            r.Clone = en.clone;
            r.Provenance = en.provenance;
            r.Source = en.source;
            r.Fate = fate;
            r.Page = page;
            r.X = x;
            r.Y = y;
            // ⚠ 用**落点那一刻的实际数量**，不是登记时的快照：复制品在中转页里那几帧
            //   可能被别的路径改动过（例如合成扣料），按快照报会多扣调用方的账。
            //   物品已经不在（丢地上/被消耗）时退回登记值。
            r.Amount = (en.clone != null && en.clone.amount > 0) ? en.clone.amount : en.amount;

            if ((fate == RelayFate.LandedOnPlayerPage || fate == RelayFate.DroppedOnGround) &&
                !HasAnyReceiver(en.source))
            {
                WarnNoHandlerOnce();
            }

            for (int i = 0; i < subs.Count; i++)
            {
                Sub s = subs[i];
                // ★ 全局订阅者收所有；按来源订阅者**只收同一来源**的事件。
                //   不这么切，两个插件都会收到对方的落点 —— 不认来历的一方就是扣错账。
                if (s.source != null &&
                    !string.Equals(s.source, en.source, StringComparison.Ordinal)) continue;
                try
                {
                    s.handler(r);
                }
                catch (Exception e)
                {
                    // 一个接入方抛异常不该影响别人，也不该把对账卡住
                    CustomUIPlugin.Error("中转页落点回调异常（已跳过该 handler）：" + e.Message);
                }
            }
        }
    }
}
