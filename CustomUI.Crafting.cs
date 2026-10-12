// ============================================================================
//  CustomUI · 「用箱内材料直接合成」执行器
// ============================================================================
//  为什么必须自己实现（不是偷懒，是原版这条路被关着）：
//    · 原版合成搜材料时 `IncludeActiveStorageContainer = !ShouldConsume`
//      （PlayerCrafting.cs:564-565），**要消耗的材料永远不看正打开的箱子**；
//      Nelson 注释原文："preserving the old behavior (in part because I haven't
//      double-checked whether consuming items from storage would work as-expected here yet)"。
//    · 没有 public 参数能重新打开它：`BlueprintSupply.ShouldConsume` 的 setter 是 internal
//      （BlueprintSupply.cs:120），`HandleCraftRequestInternal` 本身也是 internal
//      （PlayerCrafting.cs:740）→ **不存在「复用原版合成钩子」的捷径**。
//
//  分工（这一条决定成败，别改错）：
//    · **单机 / 主机**：本机就是服务端 → `items[STORAGE]` 与 `storage.items` 是同一个
//      Items 实例（`openStorage` 里 `updateItems(STORAGE, storage.items)`），
//      所以本文件可以真的扣箱内材料。判据 = `Provider.isServer`。
//    · **连专用服务器的客户端**：箱子 `Items` 只在服务端创建
//      （InteractableStorage.cs:349-357 → 客户端 `storage.items` 恒 null），
//      客户端那份 `items[STORAGE]` 只是 ReceiveStoraging 重建的**镜像**，改它=改幻觉，
//      而且所有同步出口 sendItemAdd/sendItemRemove 都是 private 且被 `Provider.isServer` 锁死
//      （PlayerInventory.cs:1371/1376/1744/1756）。
//      → 本文件**直接拒绝**（CraftBlock.NotServer），不做任何"先改本地再说"的假动作。
//      （用户 2026-09-26 定的范围：联机这半边先不做。要做得加 CustomUI 自己的服务端半边，
//        而 3.26 的 NetInvokable 只认构建期生成的序列化类，插件加不了自己的 RPC ——
//        NetReflection.cs:310-323 + ServerMethodHandle.cs:32-38。）
//
//  不自造语义：扣料走引擎自己的通道
//    · 材料搜索 = `Items.SearchContents(ref PlayerInventorySearchParameters)`（public，
//      含空容器/满耐久/满堆叠/计数方式这些判据，与原版一模一样，不需要我抄一份）
//      ⚠ 该方法参数是 `in`，本项目用的 csc（C# 5）把它看成 `ref` —— **必须用 `ref` 传**，
//        直接传变量会报 CS1620（已用探针验证）。
//    · 扣一件 = `PlayerInventorySearchResultV2.Delete(player)`
//      扣一部分 = `DeleteAmount(player, n, /*alwaysDeleteAtZeroAmount*/ false)`
//      （InventorySearch.cs:252+，注释写明 "Serverside delete an amount of this item"）
//      它们内部走 `player.crafting.removeItem(page, jar)` → 剥附件、触发 onItemRemoved
//      → 自动同步给正开着箱子的 owner + `InteractableStorage.onStateUpdated → rebuildState()`
//      **自动存进关卡存档**。自己手搓 `inventory.removeItem` 会漏掉这些，所以不许用。
//    · 产物落地 = `Items.tryAddItem(item)`（箱子）→ `PlayerInventory.forceAddItem(item, true)`
//      （背包，塞不下原版会自己掉地上）。顺序按用户要求：**箱子 → 背包 → 地上**。
//
//  与原版的行为差异（**明写着，别当隐藏差异**）：
//    1. 支持 `Operation == None`（普通合成）、`FillTargetItem`（装填/合并弹匣）、
//       `RepairTargetItem`（修理，含脏水→净水特例）、以及 `transferState`
//       （产物继承第一路材料排序后第 0 件的 数量/耐久/state，含拆附件与写 firemode）。
//       ⚠ 只剩两类仍拒绝：`questConditions` / `questRewards`（会改任务状态，
//       本该在 NPC/任务界面做）—— 由 UnsupportedReason 出一句人话给界面。
//    2. 一次点 **1 次**合成（不做 asManyAsPossible 连合）。
//    3. **不给技能经验** —— 查过：原版合成根本不加经验（PlayerCrafting.cs 全文没有
//       experience/giveExperience），只 `sendStat(FOUND_CRAFTS)` + 特效。所以我们也不加。
//       ⚠ 别"好心"补经验，那会做出原版没有的行为。
//    4. 材料优先级：普通配方先扣**箱内**再扣背包（原版只能扣背包）；
//       ⚠ 但 `transferState` 配方的第一路材料**必须按引擎排序扣**（先箱内后背包会让
//       「继承的那件」和「被吃掉的那件」不是同一件 —— 等于抄了状态还留着原件）。
//       工具类（ShouldConsume=false）一律不扣，只要求存在 —— 与原版一致。
//    5. 附件不会丢：Delete → crafting.removeItem → stripAttachments 会把瞄具/握把/弹匣
//       forceAddItem 回玩家背包（与原版扣带附件的枪同一行为）。
//    6. 会补一句 `crafting.ServerRefreshOwnerCrafting()`（SDK 注释原文 "Requested for
//       plugin use"）—— 我们绕过了 ReceiveCraft，不通知的话原版合成菜单会一直显示旧状态。
//    7. 存档不用插件管：改箱子 Items 会经 onStateUpdated → InteractableStorage.rebuildState()
//       自动写进关卡存档（所以中途失败必须走本文件的 Rollback，否则错误会直接被存下去）。
// ============================================================================
using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace CustomUI
{
    /// <summary>不能合成的原因（UI 直接拿去显示，不要再自己拼一套话）。</summary>
    internal enum CraftBlock
    {
        None = 0,
        NotServer,          // 连着专用服务器的客户端 —— 范围外，明确拒绝
        NotPlainRecipe,     // 修理/装填/state 转移/带任务条件，本版不做
        LevelDisallowed,    // 关卡配置禁合成
        EquipmentBusy,      // 手上正忙（原版也拒）
        Blacklisted,        // 地图/黑名单禁用该配方
        WrongMap,           // 配方指定了别的地图
        Skill,              // 技能等级不够
        Workbench,          // 缺附近工作台标签
        Conditions,         // 任务条件未满足
        MissingMaterials,   // 材料不够（箱内 + 背包合计）
        ToolMissing,        // 工具类材料不在场
        NoProductAsset,     // 产物资产取不到
        StaleContext,       // 面板记着的容器已经不是玩家当前打开的那个了
        TargetMissing,      // 装填/修理找不到目标（已满 / 耐久已新 / 根本不在场）
        TargetFull,         // 装填：目标已经满了，没有可装的空间
    }

    /// <summary>一次合成的执行结果。</summary>
    internal struct CraftOutcome
    {
        internal bool ok;
        internal CraftBlock block;
        internal string message;      // 成功/失败都要有话说，UI 直接显示
        internal int producedCount;   // 成功产出的物品件数

        internal static CraftOutcome Fail(CraftBlock b, string msg)
        {
            CraftOutcome o = new CraftOutcome();
            o.ok = false;
            o.block = b;
            o.message = msg;
            return o;
        }
    }

    /// <summary>
    /// 计划里的一条扣料动作。先规划、后执行 —— 中途绝不边查边扣，
    /// 否则一旦后面发现材料不够，前面扣掉的就成了「吃了材料没出货」。
    /// </summary>
    internal struct PlannedTake
    {
        internal PlayerInventorySearchResultV2 target;
        internal uint amount;     // TotalItems 时恒为 1（整件删除）
        internal bool wholeItem;  // true → Delete()；false → DeleteAmount()
    }

    /// <summary>
    /// 扣之前先把原样记一份，回滚用。
    /// ⚠ 必须记 **owner（那个 Items）**：材料可能一部分来自箱子、一部分来自背包，
    ///   回滚只往箱子里放就会**凭空复制**背包里被扣掉的那件（反之亦然）。
    /// ⚠ 必须记 quality/state：附件、弹仓里的弹药、机械电量全在 state 里，
    ///   还原成"干净的新物品"等于玩家的枪被洗白。
    /// </summary>
    internal struct UndoRecord
    {
        internal Items owner;
        internal byte x;
        internal byte y;
        internal byte rot;
        internal ushort id;
        internal byte amount;
        internal byte quality;
        internal byte[] state;

        // 装填/修理的目标是「原地被改」而不是被删 → 回滚要**无条件**写回
        // 数量/耐久/state（不像材料那样只在"看起来不一样"时才动）。
        internal bool force;
    }

    internal static class StorageCrafting
    {
        // 复用缓冲，避免每次点击都 new 一堆 List
        private static readonly List<PlayerInventorySearchResultV2> boxHits =
            new List<PlayerInventorySearchResultV2>();
        private static readonly List<PlayerInventorySearchResultV2> carryHits =
            new List<PlayerInventorySearchResultV2>();
        private static readonly List<PlayerInventorySearchResultV2> targetHits =
            new List<PlayerInventorySearchResultV2>();
        private static readonly List<PlannedTake> plan = new List<PlannedTake>();
        private static readonly List<UndoRecord> undo = new List<UndoRecord>();

        // 装填/修理的目标（一次规划内有效）。targetHasPick=false 表示没选中。
        private static PlayerInventorySearchResultV2 targetPick;
        private static bool targetHasPick;
        private static int refillAmount;

        // transferState 配方：产物要继承「第一路材料排序后的第 0 件」的数量/耐久/state。
        // 必须在**扣料之前**抓一份快照（扣完 jar 就没了），所以放在规划阶段。
        private static readonly List<PlayerInventorySearchResultV2> transferHits =
            new List<PlayerInventorySearchResultV2>();
        private static bool transferHasSource;
        private static byte transferAmount;
        private static byte transferQuality;
        private static byte[] transferState;
        private static ItemAsset transferInputAsset;

        // 与原版**同一批**比较器（InventorySearch.cs 里是 public 类）——
        // 排序决定"继承哪一件"，所以绝不自造一份，直接用引擎的。
        private static readonly InventorySearchAmountAscendingComparator CMP_AMOUNT_ASC =
            new InventorySearchAmountAscendingComparator();
        private static readonly InventorySearchAmountDescendingComparator CMP_AMOUNT_DESC =
            new InventorySearchAmountDescendingComparator();
        private static readonly InventorySearchQualityAscendingComparator CMP_QUALITY_ASC =
            new InventorySearchQualityAscendingComparator();
        private static readonly InventorySearchQualityDescendingComparator CMP_QUALITY_DESC =
            new InventorySearchQualityDescendingComparator();

        /// <summary>
        /// 按供料自己的 `Prioritization` 排序 —— 直接把引擎的 public 比较器交给
        /// `List.Sort(IComparer)`，与原版 `PlayerCrafting.cs:632-651` 用同一份逻辑。
        ///
        /// ⚠ 排序决定三件事：先吃哪一件材料、往哪一件里灌、产物继承谁的状态。
        ///   所以绝不自写第二份比较器（自写的一份只要与引擎差一点，产物耐久就会静默抄错）。
        ///   引擎的比较器是 `a.Jar.item.X - b.Jar.item.X`，不判空 —— 所以这里先把空 jar 滤掉。
        /// </summary>
        private static void SortByPrioritization(
            List<PlayerInventorySearchResultV2> hits, ECraftingInputPrioritization prioritization)
        {
            if (hits == null || hits.Count < 2) return;

            for (int i = hits.Count - 1; i >= 0; i--)
            {
                if (hits[i].Jar == null || hits[i].Jar.item == null) hits.RemoveAt(i);
            }
            if (hits.Count < 2) return;

            switch (prioritization)
            {
                case ECraftingInputPrioritization.LowestAmount:
                    hits.Sort(CMP_AMOUNT_ASC);
                    break;
                case ECraftingInputPrioritization.HighestAmount:
                    hits.Sort(CMP_AMOUNT_DESC);
                    break;
                case ECraftingInputPrioritization.LowestQuality:
                    hits.Sort(CMP_QUALITY_ASC);
                    break;
                case ECraftingInputPrioritization.HighestQuality:
                    hits.Sort(CMP_QUALITY_DESC);
                    break;
                default:
                    // 以后原版加了新的排序方式：保持引擎返回的原顺序并说一声，
                    // 而不是自己猜一个方向（猜错就是静默抄错耐久）。
                    CustomUIPlugin.Warn("未处理的 crafting 排序方式（" + prioritization +
                        "），保持搜索原顺序 —— 继承/消耗的对象可能与原版不同");
                    break;
            }
        }

        private const string NOT_SERVER_MSG =
            "联机时箱子内容只有服务端能改，本插件的「扣箱合成」只在单机/主机开放";

        /// <summary>
        /// UI 在重建路径上对**每个候选**都会调这里的廉价判据，所以：
        /// 只看「本机能不能改箱子内容」+「配方属于支持子集」，**不搜材料**。
        /// 材料够不够由点击时的 TryCraft 权威复核（它会重跑一遍引擎搜索）。
        /// </summary>
        internal static bool IsSupported(Blueprint bp)
        {
            // 非服务端 = 改不动箱子（见文件头），按钮干脆不要出现，别给了再报错
            if (!Provider.isServer) return false;
            if (bp == null) return false;
            return IsPlainRecipe(bp);
        }

        /// <summary>
        /// 这次上下文里，<c>ctx.Container</c> <b>能不能当"箱子"用</b>
        /// —— 即：能不能算进箱内材料、能不能当装填/修理的目标来源、产物能不能塞进去。
        ///
        /// ★ 为什么必须有这一道：借道会话把插件自己的 <c>Items</c> 挂在了
        ///   <c>items[STORAGE]</c> 上，与真箱子<b>在引擎里完全同形</b>。
        ///   而纯中转页里躺着的是<b>玩家正托着的那件复制品</b>：
        ///   把它当材料扣掉 ⟹ 对账层报"丢到地上" ⟹ 调用方按数量扣账 ⟹ 玩家凭空少一件；
        ///   把它当装填/修理目标 ⟹ 改的是复制品的耐久/发数，落点回来时账就对不上。
        ///   这些都不会崩，只会<b>静默地少东西或多东西</b>，所以必须在源头判、不能靠各调用点自觉。
        ///
        /// 判据是<b>开会话的一方声明的事实</b>（<see cref="StorageUiVirtualPage"/>），
        /// 不是本插件猜的：只有它知道"这一页关界面时会不会连东西一起没了"。
        /// </summary>
        internal static bool BoxCounts(StorageUiContext c)
        {
            if (c == null || c.Container == null) return false;
            if (!c.IsVirtual) return true;                    // 真箱子
            return c.UsableAsContainer;
        }

        /// <summary>
        /// 只查非材料闸门（界面与执行共用同一套，避免「按钮亮着但点开失败」）。
        /// </summary>
        internal static bool CheckGates(StorageUiContext c, Blueprint bp, Player player,
            out CraftBlock block, out string message)
        {
            block = CraftBlock.None;
            message = null;

            if (c == null || c.Container == null || bp == null || player == null)
            {
                block = CraftBlock.MissingMaterials;
                return false;
            }

            // ★ 第一道、也是最重要的一道：只有服务端能改箱子内容
            if (!Provider.isServer)
            {
                block = CraftBlock.NotServer;
                message = NOT_SERVER_MSG;
                return false;
            }

            // ★ 第二道：面板记着的那个容器必须**仍是玩家当前打开的那个**。
            //   原版关存储时 closeStorage 会把 items[STORAGE] 换成一个全新的 Items，
            //   所以这里比对实例就能发现「界面已经过期」——
            //   不查的话，界面残留 + 玩家其实已经开了另一个箱子，
            //   一按合成就会去扣**别的容器**的材料（或对着已经不存在的箱子扣）。
            Items[] pages = player.inventory != null ? player.inventory.items : null;
            if (pages == null || pages.Length <= PlayerInventory.STORAGE ||
                !ReferenceEquals(pages[PlayerInventory.STORAGE], c.Container))
            {
                block = CraftBlock.StaleContext;
                message = "这个界面已经不对应当前打开的箱子了，没有动任何材料";
                return false;
            }

            // 支持子集判据 + 一句人话原因（界面也用同一个函数拼文案，两处不会说不一样的话）
            if (!IsPlainRecipe(bp))
            {
                block = CraftBlock.NotPlainRecipe;
                string why = UnsupportedReason(bp);
                message = string.IsNullOrEmpty(why) ? "这个配方本版不扣箱合成" : why;
                return false;
            }

            if (!Level.IsCraftingAllowedByLevel)
            {
                block = CraftBlock.LevelDisallowed;
                message = "当前关卡配置禁止合成";
                return false;
            }

            if (player.equipment != null && player.equipment.isBusy)
            {
                block = CraftBlock.EquipmentBusy;
                message = "手上正忙";
                return false;
            }

            if (bp.supplies == null || bp.supplies.Length == 0)
            {
                block = CraftBlock.NotPlainRecipe;
                message = "这个配方没有材料定义";
                return false;
            }

            if (FindMainOutput(bp) == null)
            {
                block = CraftBlock.NoProductAsset;
                message = "配方的产物资产取不到（内容包缺资产？）";
                return false;
            }

            if (player.crafting != null && player.crafting.isBlueprintBlacklisted(bp))
            {
                block = CraftBlock.Blacklisted;
                message = "这个配方在当前地图被禁用";
                return false;
            }

            if (!string.IsNullOrEmpty(bp.map) &&
                (Level.info == null ||
                 !bp.map.Equals(Level.info.name, StringComparison.InvariantCultureIgnoreCase)))
            {
                block = CraftBlock.WrongMap;
                message = "这个配方只属于地图 " + bp.map;
                return false;
            }

            if (bp.RequiresSkill && bp.GetPlayerSkillLevel(player) < bp.level)
            {
                block = CraftBlock.Skill;
                message = "技能等级不够（需要 " + bp.level + "）";
                return false;
            }

            CachingAssetRef[] tags = null;
            try { tags = bp.GetApplicableRequiredNearbyCraftingTags(); }
            catch (Exception) { tags = null; }

            if (tags != null)
            {
                for (int i = 0; i < tags.Length; i++)
                {
                    TagAsset tag = tags[i].Get<TagAsset>();
                    if (tag == null) continue;

                    bool available = false;
                    try { available = player.crafting != null && player.crafting.IsCraftingTagAvailable(tag); }
                    catch (Exception) { available = false; }

                    if (!available)
                    {
                        block = CraftBlock.Workbench;
                        message = "附近没有" + tag.PlainTextName;
                        return false;
                    }
                }
            }

            try
            {
                if (!bp.areConditionsMet(player))
                {
                    block = CraftBlock.Conditions;
                    message = "任务条件未满足";
                    return false;
                }
            }
            catch (Exception) { /* 条件系统读失败不该拦住合成，原版也不因此失败 */ }

            return true;
        }

        /// <summary>
        /// 执行一次「扣箱内材料」的合成。
        /// 顺序：查闸门 → **规划**（全部材料都能凑齐才继续）→ 快照 → 扣料 → 给产物 → 收尾。
        /// 扣料阶段出任何岔 → 按快照原样放回（含耐久与 state），绝不出现
        /// 「材料没了、产物也没出来」。
        /// </summary>
        internal static CraftOutcome TryCraft(StorageUiContext c, Blueprint bp, Player player)
        {
            CraftBlock matBlock;
            string gateMsg;
            CraftBlock gateBlock;

            CraftOutcome fail;
            if (!CheckGates(c, bp, player, out gateBlock, out gateMsg))
            {
                fail = CraftOutcome.Fail(gateBlock, gateMsg);
                return fail;
            }

            plan.Clear();

            if (!BuildPlan(c, bp, player, out matBlock))
            {
                fail = CraftOutcome.Fail(matBlock, BlockMessage(matBlock));
                return fail;
            }

            // 快照：回滚时要原样还回去（quality / state 都不能丢 —— 附件、弹药都在 state 里）
            undo.Clear();

            CraftOp op = OperationOf(bp);

            // 装填/修理的目标先入快照，而且是**逆序回滚时最后恢复**的那个
            // （先扣的材料先放回，目标的位置才不会被别人的放回占掉）。
            if (targetHasPick)
            {
                UndoRecord tu = new UndoRecord();
                tu.owner = targetPick.JarOwner;
                tu.x = targetPick.Jar.x;
                tu.y = targetPick.Jar.y;
                tu.rot = targetPick.Jar.rot;
                tu.id = targetPick.Jar.item.id;
                tu.amount = targetPick.Jar.item.amount;
                tu.quality = targetPick.Jar.item.quality;
                tu.state = (byte[])targetPick.Jar.item.state.Clone();
                tu.force = true;
                undo.Add(tu);
            }

            int targetAmountBefore = targetHasPick ? targetPick.Jar.item.amount : 0;
            int targetQualityBefore = targetHasPick ? targetPick.Jar.item.quality : 0;

            for (int i = 0; i < plan.Count; i++)
            {
                PlayerInventorySearchResultV2 t = plan[i].target;
                if (t.Jar == null || t.Jar.item == null)
                {
                    Rollback();
                    return CraftOutcome.Fail(CraftBlock.MissingMaterials,
                        "材料在规划后发生了变化，本次没有动任何东西");
                }
                UndoRecord u = new UndoRecord();
                u.owner = t.JarOwner;
                u.x = t.Jar.x;
                u.y = t.Jar.y;
                u.rot = t.Jar.rot;
                u.id = t.Jar.item.id;
                u.amount = t.Jar.item.amount;
                u.quality = t.Jar.item.quality;
                u.state = (byte[])t.Jar.item.state.Clone();
                undo.Add(u);
            }

            // 扣料 + 目标改造。原版顺序是：装填先改目标 → 再扣材料 → 修理再改耐久
            // （PlayerCrafting.cs:827-841 / :845-898 / :904-919），这里照抄。
            try
            {
                if (op == CraftOp.Fill)
                {
                    ApplyFill(player);
                }

                for (int i = 0; i < plan.Count; i++)
                {
                    PlannedTake take = plan[i];
                    if (take.wholeItem)
                    {
                        take.target.Delete(player);
                    }
                    else
                    {
                        uint deleted = take.target.DeleteAmount(player, take.amount, false);
                        if (deleted < take.amount)
                        {
                            Rollback();
                            return CraftOutcome.Fail(CraftBlock.MissingMaterials,
                                "扣料中途数量对不上，已把动过的材料原样放回");
                        }
                    }
                }

                if (op == CraftOp.Repair)
                {
                    ApplyRepair(player, bp);
                }
            }
            catch (Exception e)
            {
                Rollback();
                CustomUIPlugin.Error("扣箱合成异常（已尽力回滚）：" + e);
                return CraftOutcome.Fail(CraftBlock.MissingMaterials,
                    "合成异常，已尽力回滚，详见日志");
            }

            // 给产物：箱子 → 背包 →（背包塞不下时原版 forceAddItem 自己掉地上）。
            // ⚠ 装填/修理**本来就没有产物**（原版注释："Repair and Ammo didn't support
            //   output items"，ItemAsset.cs:1733-1737 → outputs 是空数组），
            //   所以只有普通配方 produced==0 才是异常，别对那两类报错。
            int produced = GrantProducts(c, bp, player);
            if (produced == 0 && op == CraftOp.Plain)
            {
                CustomUIPlugin.Warn("扣了材料但产物数量为 0（配方 outputs 异常）：" + bp);
            }

            Finish(bp, player);

            CraftOutcome good = new CraftOutcome();
            good.ok = true;
            good.block = CraftBlock.None;
            good.producedCount = produced;

            if (op == CraftOp.Fill)
            {
                good.message = "已装填 " + refillAmount + " 发（目标 " + targetAmountBefore +
                    " → " + (targetAmountBefore + refillAmount) + "，材料按箱内优先扣）";
            }
            else if (op == CraftOp.Repair)
            {
                good.message = "已修理（耐久 " + targetQualityBefore + " → 100，材料按箱内优先扣）";
            }
            else
            {
                good.message = "已合成 " + produced + " 件（材料按箱内优先扣）";
            }

            plan.Clear();
            undo.Clear();
            targetHasPick = false;
            refillAmount = 0;
            // 继承快照只在一次合成内有效 —— 带到下一次就会拿旧耐久造东西（静默错）
            transferHasSource = false;
            transferState = null;
            transferInputAsset = null;
            return good;
        }

        /// <summary>
        /// 每个拒绝原因对应一句人话。界面日志与头部提示都用它，避免"两处说法不一样"。
        /// 新增 CraftBlock 时必须在这里补一句 —— 否则会退回那句含糊的"材料不够"，
        /// 玩家就分不清到底是缺弹药还是弹匣已经满了（自检 16.10 钉住这条）。
        /// </summary>
        private static string BlockMessage(CraftBlock b)
        {
            switch (b)
            {
                case CraftBlock.NotServer: return NOT_SERVER_MSG;
                case CraftBlock.StaleContext: return "界面已不对应当前打开的箱子，没有动任何材料";
                case CraftBlock.NotPlainRecipe: return "这类配方本版不代合，请走原版制作页";
                case CraftBlock.LevelDisallowed: return "当前关卡配置禁止合成";
                case CraftBlock.EquipmentBusy: return "手上正忙";
                case CraftBlock.Blacklisted: return "这个配方在当前地图被禁用";
                case CraftBlock.WrongMap: return "这个配方只属于别的地图";
                case CraftBlock.Skill: return "技能等级不够";
                case CraftBlock.Workbench: return "附近没有需要的工作台";
                case CraftBlock.Conditions: return "任务条件未满足";
                case CraftBlock.MissingMaterials: return "材料不够（箱内 + 身上合计仍不足）";
                case CraftBlock.ToolMissing: return "缺少工具";
                case CraftBlock.TargetMissing: return "找不到能装填/修理的目标（已满或全新的不算）";
                case CraftBlock.TargetFull: return "目标已经满了，没有可装的空间";
                case CraftBlock.NoProductAsset: return "配方的产物资产取不到";
                default: return "不能合成";
            }
        }

        /// <summary>
        /// 装填：把目标加满到 refillAmount（原版同一句 sendUpdateAmount）。
        /// 目标可能正在玩家手上（装备格）或在箱子里 —— page 直接取自搜索结果，
        /// 所以两种都能改；箱子那份就是 items[STORAGE]，事件链会自动同步与落盘。
        /// </summary>
        private static void ApplyFill(Player player)
        {
            byte next = (byte)Mathf.Clamp(targetPick.Jar.item.amount + refillAmount, 0, 255);
            player.inventory.sendUpdateAmount(targetPick.Page, targetPick.Jar.x, targetPick.Jar.y, next);
        }

        /// <summary>
        /// 修理：耐久写 100；再加上原版那个"脏水变净水"的特例
        /// （同一处代码里，条件是 REFILL 型物品且 state 只有 1 字节）。
        /// </summary>
        private static void ApplyRepair(Player player, Blueprint bp)
        {
            player.inventory.sendUpdateQuality(targetPick.Page, targetPick.Jar.x, targetPick.Jar.y, 100);

            ItemAsset asset = targetPick.GetAsset();
            if (asset != null && asset.type == EItemType.REFILL &&
                targetPick.Jar.item.state != null && targetPick.Jar.item.state.Length == 1)
            {
                if ((ERefillWaterType)targetPick.Jar.item.state[0] == ERefillWaterType.DIRTY)
                {
                    byte[] cleaned = (byte[])targetPick.Jar.item.state.Clone();
                    cleaned[0] = (byte)ERefillWaterType.CLEAN;
                    player.inventory.sendUpdateInvState(
                        targetPick.Page, targetPick.Jar.x, targetPick.Jar.y, cleaned);
                }
            }
        }

        // ================= 内部 =================

        /// <summary>
        /// 支持的操作类型（用户 2026-09-26 实测要求把装填/合并弹匣也做上）。
        ///
        /// 原版这两类只是在执行阶段多几步，用的**全是 public 原语**，所以能照做：
        ///   · FillTargetItem（装填 / 合并弹匣，PlayerCrafting.cs:827-841）
        ///       refillAmount = min(目标.MaxAmount - 目标.amount, 第一路输入的总量)
        ///       inventory.sendUpdateAmount(目标 page,x,y, 目标.amount + refillAmount)  // 目标变满
        ///       第一路输入只扣 refillAmount（等价于原版的 requiredAmountOverride）      // 来源变少/变空
        ///   · RepairTargetItem（修理，:904-919）
        ///       inventory.sendUpdateQuality(目标 page,x,y, 100)
        ///       脏水变净水那个特例（REFILL 且 state.Length==1）另需 sendUpdateInvState
        ///   两者的 TargetItem 都是 ShouldConsume=false → **只改不删**（原版注释原文
        ///   "Don't delete the refilled item"）；Fill 带 ShouldExcludeFullAmount=true、
        ///   Repair 带 ShouldIncludeMaxQuality=false → 已满/全新的目标不会被选中。
        ///
        /// 仍然排除的两类（不是偷懒，是自造容易悄悄做错）：
        ///   · transferState：要把输入的耐久/state 造进产物，且它取的是 inputItems[0] 的第 0 件
        ///     （:931-950），我们对「哪件算第一件」的排序只要差一点，就会造出耐久不对的产物 ——
        ///     那是会直接写进存档的错。
        ///   · 带任务条件/奖励：合成会改任务状态，这类本该在 NPC/任务界面做。
        /// </summary>
        internal enum CraftOp
        {
            None = 0,       // 不支持
            Plain,          // 普通合成
            Fill,           // 装填 / 合并弹匣
            Repair,         // 修理
        }

        internal static CraftOp OperationOf(Blueprint bp)
        {
            if (bp == null) return CraftOp.None;
            switch (bp.Operation)
            {
                case EBlueprintOperation.None: return CraftOp.Plain;
                case EBlueprintOperation.FillTargetItem:
                    return bp.TargetItem == null ? CraftOp.None : CraftOp.Fill;
                case EBlueprintOperation.RepairTargetItem:
                    return bp.TargetItem == null ? CraftOp.None : CraftOp.Repair;
                default: return CraftOp.None;   // 以后新增的操作类型一律先不支持
            }
        }

        /// <summary>
        /// 不支持时给一句**人话原因**；支持则返回 null。
        /// 界面必须拿它拼文案 —— 绝不能再出现「写着可合成、却没有任何按钮也没解释」。
        /// </summary>
        internal static string UnsupportedReason(Blueprint bp)
        {
            CraftOp op = OperationOf(bp);
            if (op == CraftOp.None)
            {
                if (bp != null && bp.Operation != EBlueprintOperation.None && bp.TargetItem == null)
                    return "配方缺目标物品定义";
                return "该操作类型本版不支持";
            }
            // ⚠ transferState 以前也被挡在这里，2026-09-27 已放开：
            //   排序直接用引擎自己的 public 比较器（InventorySearch*Comparator），
            //   firemode 虽然字段是 internal，但 ItemGunAsset.getState(origin)[11] 就是它
            //   （原版那句 `item.state[11] = (byte)outputGunAsset.firemode` 的等价取法）。
            //   ⟹ 没有剩下"必须自造语义"的部分，所以不再拒绝。
            if (bp.questConditions != null && bp.questConditions.Length > 0)
                return "带任务条件的配方请走原版合成";
            if (bp.questRewards != null && bp.questRewards.Length > 0)
                return "带任务奖励的配方请走原版合成";
            return null;
        }

        /// <summary>属于本版支持的子集吗（不含「本机能不能改箱子」，那是 IsSupported 的事）。</summary>
        private static bool IsPlainRecipe(Blueprint bp)
        {
            return OperationOf(bp) != CraftOp.None && UnsupportedReason(bp) == null;
        }

        /// <summary>
        /// 凑齐计划：每个消耗类材料按「先箱内、再身上」的顺序排好要扣哪几件。
        /// 工具类（ShouldConsume=false）不进计划 —— 原版执行阶段就跳过它们，不扣。
        /// </summary>
        private static bool BuildPlan(StorageUiContext c, Blueprint bp, Player player,
            out CraftBlock block)
        {
            block = CraftBlock.None;
            plan.Clear();
            targetHasPick = false;
            refillAmount = 0;

            CraftOp op = OperationOf(bp);
            ItemJar ignore = null;

            // ★ 这一页能不能当箱子用，**在这里一次性决定**，后面所有搜索只认这个 `box`：
            //   传 null 就等于"箱内这一轮什么都不认"
            //   （SearchOne 的箱子轮会直接 return，PruneForeignContainers(hits, null)
            //    会把任何 storage 页 jar 全剔掉）。
            //   比在每个搜索点各写一遍 if 可靠 —— 漏一处就是静默扣掉玩家的复制品。
            Items box = BoxCounts(c) ? c.Container : null;

            // 装填 / 修理：先选目标（原版也是这个顺序），再把目标从材料搜索里排除
            if (op == CraftOp.Fill || op == CraftOp.Repair)
            {
                if (!PickTarget(bp, box, player, out block)) return false;
                ignore = targetPick.Jar;
            }

            BlueprintSupply[] supplies = bp.supplies;

            // ★ transferState 配方：先抓「产物继承谁」的快照（必须在扣料之前，扣完 jar 就没了）。
            //   并且第一路材料**必须按同一份排序去扣** —— 原版取的就是
            //   `inputItems[0].searchResults[0]`（排序后的第 0 件），它既是继承来源也是先吃的那件。
            //   若这里仍按"先箱内后背包"扣，就会出现「抄了 A 件的耐久、实际吃掉 B 件」，
            //   等于凭空复制了一件的状态还留着原件 —— 所以这条路必须放弃"箱内优先"。
            bool transfer = bp.transferState;
            if (transfer && !CaptureTransferSource(bp, c, player, ignore, out block))
            {
                plan.Clear();
                return false;
            }

            for (int i = 0; i < supplies.Length; i++)
            {
                BlueprintSupply sup = supplies[i];
                if (sup == null) continue;

                if (!sup.ShouldConsume)
                {
                    if (CountBoth(sup, box, player) < 1)
                    {
                        block = CraftBlock.ToolMissing;
                        return false;
                    }
                    continue;
                }

                // 装填的第一路材料只扣 refillAmount（等价原版的 requiredAmountOverride），
                // 其余各路按配方声明扣。
                int remaining = (op == CraftOp.Fill && i == 0)
                    ? refillAmount
                    : (sup.amount <= 0 ? 1 : sup.amount);
                bool byItems = sup.CountingMethod == ECraftingInputCountingMethod.TotalItems;

                if (transfer && i == 0)
                {
                    // 与原版同序：合并 + 引擎比较器排好的那一份
                    AddTakes(transferHits, sup, byItems, ref remaining);
                }
                else
                {
                    // 先箱子，后背包（用户要的「直接扣箱内材料」就是这个顺序）
                    for (int round = 0; round < 2 && remaining > 0; round++)
                    {
                        SearchOne(sup, box, player, round == 0, ignore);
                        AddTakes(round == 0 ? boxHits : carryHits, sup, byItems, ref remaining);
                    }
                }

                if (remaining > 0)
                {
                    block = CraftBlock.MissingMaterials;
                    plan.Clear();
                    return false;
                }
            }
            // 计划允许为空：例如这个配方只需要工具在场（不消耗任何材料）
            return true;
        }

        /// <summary>
        /// 把一批候选按配方计数法转成"要扣什么"，累加进 plan，并把 remaining 递减。
        /// 两个调用方（箱内/背包两轮、以及 transferState 的排序轮）共用同一份扣料算法。
        /// </summary>
        private static void AddTakes(List<PlayerInventorySearchResultV2> hits,
            BlueprintSupply sup, bool byItems, ref int remaining)
        {
            if (hits == null) return;

            for (int k = 0; k < hits.Count && remaining > 0; k++)
            {
                PlayerInventorySearchResultV2 r = hits[k];
                if (r.Jar == null || r.Jar.item == null) continue;

                // ⚠ 装备格（page 0/1）绝不进计划，两个理由：
                //   ① 消耗 `Items.getIndex` 对 page < SLOTS 恒返回 0 → 回滚会把
                //      改动写到 0 号格上（等于改错东西）；
                //   ② 扣掉玩家正穿着的护甲/手里拿着的工具，属于"玩家没料到的后果"。
                //   判据上本来也不会出现（只有 ShouldConsume=true 的才进这里，
                //   而那一类搜索传的是 IncludeEquipmentSlots=false），
                //   这道检查是把"本来不会"变成"写死了不会"。
                if (r.Page < PlayerInventory.SLOTS) continue;

                PlannedTake take = new PlannedTake();
                take.target = r;

                if (byItems)
                {
                    // 数件数：整件删除（与原版 TotalItems 分支一致）
                    take.wholeItem = true;
                    take.amount = 1;
                    remaining -= 1;
                }
                else
                {
                    int have = r.Jar.item.amount;
                    if (have <= 0 && sup.ShouldCountEmptyAsOne)
                    {
                        take.wholeItem = true;      // 空容器按 1 件算 → 整件删
                        take.amount = 1;
                        remaining -= 1;
                    }
                    else
                    {
                        take.wholeItem = false;
                        int takeAmount = Math.Min(have, remaining);
                        take.amount = (uint)Math.Max(0, takeAmount);
                        if (take.amount == 0) continue;
                        remaining -= takeAmount;
                    }
                }

                plan.Add(take);
            }
        }

        /// <summary>
        /// 抓「产物继承谁」的快照：把第一路材料的候选（箱内 + 背包，已剔外来容器与目标本身）
        /// 合并后**用引擎自己的比较器**按该路 Prioritization 排序，取第 0 件。
        /// 与原版 `status.inputItems[0].searchResults[0]`（PlayerCrafting.cs:933）同一取法。
        /// </summary>
        private static bool CaptureTransferSource(Blueprint bp, StorageUiContext c, Player player,
            ItemJar ignore, out CraftBlock block)
        {
            block = CraftBlock.MissingMaterials;
            transferHasSource = false;
            transferInputAsset = null;
            transferHits.Clear();

            BlueprintSupply[] supplies = bp.supplies;
            if (supplies == null || supplies.Length == 0 || supplies[0] == null)
            {
                block = CraftBlock.NotPlainRecipe;
                return false;
            }

            BlueprintSupply first = supplies[0];
            transferInputAsset = first.FindItemAsset();
            if (transferInputAsset == null) return false;

            // 两路都搜（箱内 + 背包），合并成一个候选集 —— 原版是一次带
            // IncludeActiveStorageContainer 的搜索，我们这里范围只认当前箱 + 背包。
            // ⚠ 借道的纯中转页不算箱（`box` 为 null）：那一页里可能是玩家正托着的复制品，
            //   把它当"继承来源"会抄走它的耐久/发数，落点回来时账就对不上。
            Items box = BoxCounts(c) ? c.Container : null;
            SearchOne(first, box, player, true, ignore);
            transferHits.AddRange(boxHits);
            SearchOne(first, box, player, false, ignore);
            transferHits.AddRange(carryHits);

            if (transferHits.Count == 0) return false;

            SortByPrioritization(transferHits, first.Prioritization);
            if (transferHits.Count == 0) return false;

            PlayerInventorySearchResultV2 src = transferHits[0];
            if (src.Jar == null || src.Jar.item == null) return false;

            transferAmount = src.Jar.item.amount;
            transferQuality = src.Jar.item.quality;
            // 必须 clone：扣料后原 jar 会被删/改，而且多个产物不能共用同一个数组
            transferState = (byte[])src.Jar.item.state.Clone();
            transferHasSource = true;
            return true;
        }

        /// <summary>
        /// 给界面用的一句话：这条配方会把哪件材料的状态带到产物上。
        /// ⚠ 必须走 CaptureTransferSource（同一份排序），界面自己再排一次序 = 第二套实现。
        /// </summary>
        internal static string DescribeTransfer(StorageUiContext c, Blueprint bp, Player player)
        {
            if (bp == null || !bp.transferState) return null;

            CraftBlock block;
            if (!CaptureTransferSource(bp, c, player, null, out block)) return null;
            if (!transferHasSource || transferInputAsset == null) return null;

            string name = string.IsNullOrEmpty(transferInputAsset.FriendlyName)
                ? ("物品 " + transferInputAsset.id) : transferInputAsset.FriendlyName;
            string t = "产物将继承「" + name + "」" + (transferQuality > 0 ? ("（耐久 " + transferQuality + "%）") : "");
            if (bp.withoutAttachments) t += "，附件会被拆掉";
            return t;
        }

        private static void SearchOne(BlueprintSupply sup, Items container, Player player, bool fromBox)
        {
            SearchOne(sup, container, player, fromBox, null);
        }

        /// <summary>
        /// 走引擎自己的搜索。两种范围：
        ///   fromBox=true  → 只搜箱子那个 Items
        ///   fromBox=false → 只搜玩家背包，**不含**箱子
        /// 其余筛选条件全部取 BlueprintSupply 自己的配置，与原版一致。
        ///
        /// ⚠ 装备格是否算数必须跟着 `ShouldConsume` 走（原版判据
        ///   `IncludeEquipmentSlots = !ShouldConsume`）—— 否则会出现「显示说够、点了说不够」，
        ///   两套口径的漂移就是这么来的。
        /// ⚠ `ignore` 用于装填/修理：原版搜材料时会把**已选中的目标**排除
        ///   （PlayerCrafting.cs:519/525 的 `ignoreTargetItem`），
        ///   否则半满弹匣会被当成"自己的弹药"吃掉 —— 那是会吞装备的错。
        /// </summary>
        private static void SearchOne(BlueprintSupply sup, Items container, Player player,
            bool fromBox, ItemJar ignore)
        {
            List<PlayerInventorySearchResultV2> buffer = fromBox ? boxHits : carryHits;
            buffer.Clear();

            PlayerInventorySearchParameters p = new PlayerInventorySearchParameters();
            p.Results = buffer;
            p.AssetRef = sup.ItemRef;
            p.IncludeEmpty = sup.ShouldIncludeEmptyAmount;
            p.IncludeMaxQuality = sup.ShouldIncludeMaxQuality;
            p.ExcludeFullAmount = sup.ShouldExcludeFullAmount;
            p.ItemToIgnore = ignore;

            if (fromBox)
            {
                p.IncludeEquipmentSlots = false;
                p.IncludeActiveStorageContainer = false;   // 直接搜那个 Items，不靠页号
                if (container == null) return;
                container.SearchContents(ref p);           // ⚠ 必须 ref（C# 5 把 in 当 ref）
                // 只承认"就是本箱"的 jar（防御式，见 PruneForeignContainers 注释）
                PruneForeignContainers(boxHits, container);
            }
            else
            {
                if (player == null || player.inventory == null) return;
                p.IncludeEquipmentSlots = !sup.ShouldConsume;
                p.IncludeActiveStorageContainer = false;   // 明确排除箱子，避免与箱内那轮重复计
                player.inventory.SearchContents(ref p);
                // 背包那一轮：任何 storage 页 jar 都不要（箱子有专门一轮数）
                PruneForeignContainers(carryHits, null);
            }
        }

        /// <summary>
        /// 剔除「不属于本次范围」的 storage 页 jar。
        ///
        /// 为什么必须有这道：用户定的范围是**当前打开的这个箱子 + 背包**。
        /// 而工作区里存在别的插件会给 `PlayerInventory.SearchContents` 打 Postfix、
        /// 把**周围其它箱子**的 jar 注入进结果里（本插件不认识它、也不该依赖它）。
        /// 一旦被注入又不筛掉，计划就会把远处箱子的东西当成本箱材料扣掉 ——
        /// 那是"扣错容器"级别的错：静默、且改完直接落盘。
        ///
        /// `keepOwner == null` 表示这一轮完全不承认任何 storage 页 jar（背包轮用 null，
        ///  箱子由 fromBox=true 那一轮专门数，避免同一件被算两次）。
        /// </summary>
        private static void PruneForeignContainers(
            List<PlayerInventorySearchResultV2> hits, Items keepOwner)
        {
            if (hits == null || hits.Count == 0) return;

            for (int i = hits.Count - 1; i >= 0; i--)
            {
                PlayerInventorySearchResultV2 r = hits[i];
                if (r.Page != PlayerInventory.STORAGE) continue;
                if (keepOwner != null && ReferenceEquals(r.JarOwner, keepOwner)) continue;
                hits.RemoveAt(i);
            }
        }

        /// <summary>
        /// 选中装填/修理的**目标**，并算出这一次要灌进去多少（原版同序）。
        ///
        /// 目标用一次 `player.inventory.SearchContents` 就够：它的
        /// `ShouldConsume == false` → 装备格与当前打开的箱子都算（page 0..7，
        /// 服务端上第 7 页就是那个箱子），与原版给目标搜索用的判据完全一致。
        /// 排序取哪件也是原版的判据：Fill 拿**最满**的（灌它）、Repair 拿**最破**的（修它），
        /// 而且 `ShouldExcludeFullAmount` / `ShouldIncludeMaxQuality=false` 已经把
        /// 「满弹匣 / 全新物品」排除在候选之外，所以不会白扣材料。
        /// </summary>
        private static bool PickTarget(Blueprint bp, Items container, Player player,
            out CraftBlock block)
        {
            block = CraftBlock.None;
            targetHasPick = false;
            refillAmount = 0;
            targetHits.Clear();

            BlueprintSupply tgt = bp.TargetItem;
            if (tgt == null || player == null || player.inventory == null)
            {
                block = CraftBlock.TargetMissing;
                return false;
            }

            PlayerInventorySearchParameters p = new PlayerInventorySearchParameters();
            p.Results = targetHits;
            p.AssetRef = tgt.ItemRef;
            p.IncludeEmpty = tgt.ShouldIncludeEmptyAmount;
            p.IncludeMaxQuality = tgt.ShouldIncludeMaxQuality;
            p.ExcludeFullAmount = tgt.ShouldExcludeFullAmount;
            p.IncludeEquipmentSlots = !tgt.ShouldConsume;
            p.IncludeActiveStorageContainer = !tgt.ShouldConsume;
            player.inventory.SearchContents(ref p);

            // 目标是「原地要改的那个容器」——范围同样只允许：本箱 + 背包 + 装备格。
            // 别的箱子即便被其它插件注入进来，也不能成为目标（那会去改远处箱子的物品）。
            PruneForeignContainers(targetHits, container);

            if (targetHits.Count == 0)
            {
                block = CraftBlock.TargetMissing;
                return false;
            }

            // 用引擎自己的比较器 + 这条 TargetItem 自带的 Prioritization
            //（原版给 TargetItem 配的：Fill=HighestAmount 挑最满的灌、
            //  Repair=LowestQuality 挑最破的修，ItemAsset.cs:1438/1449）
            SortByPrioritization(targetHits, tgt.Prioritization);
            if (targetHits.Count == 0)
            {
                block = CraftBlock.TargetMissing;
                return false;
            }
            targetPick = targetHits[0];
            if (targetPick.Jar == null || targetPick.Jar.item == null)
            {
                block = CraftBlock.TargetMissing;
                return false;
            }
            targetHasPick = true;

            // 只有装填要算灌入量；修理不需要（它只改耐久）
            if (bp.Operation != EBlueprintOperation.FillTargetItem) return true;

            // refillAmount = min(目标还缺多少, 第一路材料能给多少)  —— 原版 PlayerCrafting.cs:831-836
            ItemAsset targetAsset = tgt.FindItemAsset();
            if (targetAsset == null)
            {
                block = CraftBlock.TargetMissing;
                return false;
            }

            int missing = targetAsset.MaxAmount - targetPick.Jar.item.amount;
            if (missing < 0) missing = 0;

            BlueprintSupply[] supplies = bp.supplies;
            if (supplies == null || supplies.Length == 0 || supplies[0] == null)
            {
                block = CraftBlock.TargetFull;
                return false;
            }

            int available = CountSupplyIgnoring(supplies[0], player, targetPick.Jar);
            refillAmount = Math.Min(missing, available);
            if (refillAmount <= 0)
            {
                block = CraftBlock.TargetFull;     // 目标已满，或第一路材料一件也没有
                return false;
            }
            return true;
        }

        /// <summary>按供料自己的计数法合计「身上 + 箱内」有多少，并排除目标本身。</summary>
        private static int CountSupplyIgnoring(BlueprintSupply sup, Player player, ItemJar ignore)
        {
            SearchOne(sup, null, player, false, ignore);
            int total = CountOf(sup, carryHits);

            // 箱内那一轮同样要排除目标（目标可能就在箱子里）
            boxHits.Clear();
            if (player.inventory != null)
            {
                Items[] pages = player.inventory.items;
                if (pages != null && PlayerInventory.STORAGE < pages.Length)
                {
                    SearchOneInto(sup, pages[PlayerInventory.STORAGE], ignore);
                    total += CountOf(sup, boxHits);
                }
            }
            return total;
        }

        /// <summary>与 SearchOne(fromBox=true) 同，但可以带 ignore 并指定那个 Items。</summary>
        private static void SearchOneInto(BlueprintSupply sup, Items container, ItemJar ignore)
        {
            if (container == null) return;
            boxHits.Clear();

            PlayerInventorySearchParameters p = new PlayerInventorySearchParameters();
            p.Results = boxHits;
            p.AssetRef = sup.ItemRef;
            p.IncludeEmpty = sup.ShouldIncludeEmptyAmount;
            p.IncludeMaxQuality = sup.ShouldIncludeMaxQuality;
            p.ExcludeFullAmount = sup.ShouldExcludeFullAmount;
            p.ItemToIgnore = ignore;
            p.IncludeEquipmentSlots = false;
            p.IncludeActiveStorageContainer = false;
            container.SearchContents(ref p);
            PruneForeignContainers(boxHits, container);   // 同上：只认这一个容器
        }

        /// <summary>箱内 + 身上的合计（按 CountingMethod）。与界面显示同一套判据。</summary>
        private static int CountBoth(BlueprintSupply sup, Items container, Player player)
        {
            SearchOne(sup, container, player, true);
            int total = CountOf(sup, boxHits);

            SearchOne(sup, container, player, false);
            total += CountOf(sup, carryHits);

            return total;
        }

        private static int CountOf(BlueprintSupply sup, List<PlayerInventorySearchResultV2> hits)
        {
            if (sup.CountingMethod == ECraftingInputCountingMethod.TotalItems)
            {
                return hits.Count;
            }

            int total = 0;
            for (int i = 0; i < hits.Count; i++)
            {
                if (hits[i].Jar == null || hits[i].Jar.item == null) continue;
                total += sup.ShouldCountEmptyAsOne
                    ? Mathf.Max(1, hits[i].Jar.item.amount)
                    : hits[i].Jar.item.amount;
            }
            return total;
        }

        /// <summary>
        /// 按快照把动过的东西放回**它原来所在的那个 Items**（箱子还是背包），逆序执行。
        ///
        /// ⚠ 两个必须分开的情况（合起来才是真回滚）：
        ///  · 整件被删（Delete / DeleteAmount 把堆扣空）→ 格位空了 → 用 addItem 原样放回；
        ///  · 只是数量被减（DeleteAmount 部分扣）→ **格子还在** → 只能 updateAmount/updateQuality
        ///    还原，绝不能 addItem —— 在同一坐标叠出第二个 jar 就是凭空多出物品。
        /// ⚠ 回滚目标必须是 jar 原来的 owner：材料可能一半来自箱子、一半来自背包，
        ///   统一往箱子里放会把背包那部分"搬"进箱子（玩家东西凭空换位）。
        /// </summary>
        private static void Rollback()
        {
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                try
                {
                    UndoRecord u = undo[i];
                    Items owner = u.owner;
                    if (owner == null) continue;

                    byte index = owner.getIndex(u.x, u.y);
                    if (index == 255)
                    {
                        Item item = new Item(u.id, u.amount, u.quality, u.state);
                        owner.addItem(u.x, u.y, u.rot, item);
                    }
                    else
                    {
                        ItemJar jar = owner.getItem(index);
                        if (jar == null || jar.item == null) continue;

                        // 材料那一类只动过 amount（DeleteAmount 部分扣减不会碰 quality、
                        // 也不会剥附件），所以按需还原两项。
                        // 目标（force）是「原地被改」：装填改数量、修理改耐久，
                        // 修理的脏水变净水特例还改过 state → 一律无条件写回快照。
                        if (u.force || jar.item.amount != u.amount) owner.updateAmount(index, u.amount);
                        if (u.force || jar.item.quality != u.quality) owner.updateQuality(index, u.quality);

                        if (u.force && u.state != null && jar.item.state != null &&
                            jar.item.state.Length == u.state.Length)
                        {
                            owner.updateState(index, (byte[])u.state.Clone());
                        }
                    }
                }
                catch (Exception e)
                {
                    CustomUIPlugin.Error("合成回滚失败（材料可能已丢失或重复，见日志）：" + e);
                }
            }
            plan.Clear();
            undo.Clear();
        }

        /// <summary>
        /// transferState 配方的产物：把「继承来源」的数量/耐久/state 抄到新物品上。
        /// 逐条对照原版 `PlayerCrafting.cs:931-955`。
        ///
        /// ⚠ 三处容易做错的地方：
        ///  · state 必须**每个产物各 clone 一份**。原版直接把 jar 的 state 数组塞进新 Item，
        ///    多个产物会共用同一个数组（改一个动全部）；而且那个 jar 已经被扣掉了。
        ///  · 拆附件（withoutAttachments）必须**先清零、后写 firemode**，顺序反了 firemode 会被抹掉。
        ///  · `ItemGunAsset.firemode` 是 **internal**，读不到；
        ///    但 `getState(origin)` 是 public 且返回 byte[18]，**index 11 就是 (byte)firemode**
        ///    （数组顺序：0-1瞄具 2-3战术 4-5握把 6-7枪管 8-9弹匣 10弹药 11开火模式），
        ///    所以取 `((ItemGunAsset)产物资产).getState(...)[11]` 与原版等价。
        /// </summary>
        private static Item BuildTransferItem(Blueprint bp, ItemAsset outputAsset)
        {
            if (!transferHasSource || transferState == null)
            {
                CustomUIPlugin.Warn("transferState 配方没有抓到继承来源，跳过一件产物（不造状态不明的物品）");
                return null;
            }

            Item item = new Item(outputAsset.id, transferAmount, transferQuality,
                (byte[])transferState.Clone());

            ItemAsset inputAsset = transferInputAsset;
            if (inputAsset != null && inputAsset.type == EItemType.GUN &&
                outputAsset != null && outputAsset.type == EItemType.GUN &&
                item.state != null && item.state.Length >= 12)
            {
                if (bp.withoutAttachments)
                {
                    for (int z = 0; z < item.state.Length; z++) item.state[z] = 0;
                }

                ItemGunAsset gun = outputAsset as ItemGunAsset;
                if (gun != null)
                {
                    byte[] fresh = null;
                    try { fresh = gun.getState(EItemOrigin.CRAFT); }
                    catch (Exception) { fresh = null; }

                    if (fresh != null && fresh.Length >= 12) item.state[11] = fresh[11];
                }
            }

            return item;
        }

        /// <summary>产物逐件给：先塞箱子，再塞背包（背包放不下原版自己掉地上）。</summary>
        private static int GrantProducts(StorageUiContext c, Blueprint bp, Player player)
        {
            int produced = 0;
            BlueprintOutput[] outputs = bp.outputs;
            if (outputs == null) return 0;

            for (int i = 0; i < outputs.Length; i++)
            {
                BlueprintOutput output = outputs[i];
                if (output == null) continue;

                ItemAsset asset = output.FindItemAsset();
                if (asset == null) continue;

                int amount = output.amount <= 0 ? 1 : output.amount;
                for (int k = 0; k < amount; k++)
                {
                    Item item = bp.transferState ? BuildTransferItem(bp, asset)
                                                 : new Item(asset.id, output.origin);
                    if (item == null) continue;

                    bool intoBox = false;
                    // ⚠ 借道的纯中转页**不是产物落点**：塞进去的东西关界面时就没了
                    //   （对账层最多把它塞回背包/掉脚下，但那已经不是"箱内产物"的语义了）。
                    //   所以这一页不算箱时，产物直接走背包/装备那一套（与原版一致）。
                    try { intoBox = BoxCounts(c) && c.Container.tryAddItem(item); }
                    catch (Exception) { intoBox = false; }

                    if (!intoBox)
                    {
                        // 与原版一致：forceAddItem(auto=true) 自己会在放不下时掉地上
                        player.inventory.forceAddItem(item, true);
                    }
                    produced++;
                }
            }
            return produced;
        }

        /// <summary>收尾，逐项照抄原版 HandleCraftRequestInternal 的尾部（PlayerCrafting.cs:966-995）。</summary>
        private static void Finish(Blueprint bp, Player player)
        {
            try
            {
                bp.ApplyConditions(player);
                bp.GrantRewards(player);
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("配方条件/奖励处理异常：" + e.Message);
            }

            try { player.sendStat(EPlayerStat.FOUND_CRAFTS); }
            catch (Exception) { /* 统计失败不影响合成 */ }

            // 让原版合成菜单（以及其他订阅了 onCraftingUpdated 的插件）知道材料变了。
            // 这个方法在 SDK 里的注释原文就是 "Requested for plugin use."，
            // 我们自己扣料绕过了 ReceiveCraft，所以必须补这一句，否则
            // 玩家切回原版背包页会看到旧的「可合成」标记。
            try { player.crafting.ServerRefreshOwnerCrafting(); }
            catch (Exception) { /* 刷新通知失败不影响已完成的合成 */ }

            try
            {
                EffectAsset effect = bp.FindBuildEffectAsset();
                if (effect != null)
                {
                    TriggerEffectParameters p = new TriggerEffectParameters(effect);
                    p.position = player.transform.position;
                    p.relevantDistance = EffectManager.SMALL;
                    EffectManager.triggerEffect(p);

                    if (Provider.isServer)
                    {
                        AlertTool.alert(player.transform.position, 8);
                    }
                }
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("合成特效失败：" + e.Message);
            }

            // ⚠ 这里**不给技能经验**：原版合成也不给（见文件头「行为差异」第 3 条）。
            // ⚠ 也不播放背包音效 —— 原版合成链里没有音效调用，加上去就是自造行为。
        }

        private static ItemAsset FindMainOutput(Blueprint bp)
        {
            BlueprintOutput[] outputs = bp.outputs;
            if (outputs != null)
            {
                for (int i = 0; i < outputs.Length; i++)
                {
                    if (outputs[i] == null) continue;
                    ItemAsset asset = outputs[i].FindItemAsset();
                    if (asset != null) return asset;
                }
            }
            try { return bp.GetOwnerAsset() as ItemAsset; }
            catch (Exception) { return null; }
        }
    }
}
