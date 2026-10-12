// ============================================================================
//  CustomUI —— 自定义交互界面框架（工业插件的前置）
// ============================================================================
//  目的：把「按 F 打开路障/箱子」时的右侧存储区换成插件自己的界面，并保留
//        原版的背包页（左列）与原版操作手感。同时提供「完全独立界面」能力
//        （不依赖背包页的全屏面板）。
//
//  为什么能这么做（源码依据，U3-SDK-3.26.3.8）：
//    1) 原版没有独立的「存储界面类」。所谓右侧存储区只是
//       PlayerDashboardInventoryUI 里的两个元素：
//         items[STORAGE - SLOTS]   ← 存储网格（SleekItems）
//         headers[STORAGE - SLOTS] ← 「储存物品」标题按钮
//       updateBoxAreas() 只对 IsVisible 的项布局（按 SizeOffset_Y 累加），
//       所以把这两者 IsVisible=false，原版网格就消失并自动腾出位置。
//    2) PlayerInventory.openStorage(InteractableStorage) 是 public —— 这是
//       原版箱子按 F 走的同一个入口，插件可直接调用以弹出原版背包页。
//       其内部 sendStorage() 会触发客户端 public 委托字段 onInventoryStored，
//       这正是「面板该在何时挂载」的钩子。
//    3) 原版的存储交互（拖拽/右键/丢弃）实现里**没有任何一处依赖 SleekItems
//       控件本身**，全部只吃 (page, x, y) 坐标 + public 的 inventory API。
//       因此自定义控件可以照抄这些逻辑。
//
//  设计边界（重要）：
//    · 本插件**不引用工业插件的任何类型**。它只认识「引擎储物」这一件事。
//      依赖方向是单向的：工业用 [BepInDependency] 依赖本插件（2026-09-29 已落地，
//      见 Industrial-Source/Industrial.CustomUi.cs）—— 本插件始终不认识工业。
//    · 数据层第一版完全交给引擎（Items / 关卡存档）。自管数据是后续阶段。
//
//  作者：35117    版本：见下方 `[BepInPlugin]` 第三参数（**唯一真源**，别在这儿再抄一遍）    编译：运行 build.bat
// ============================================================================
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace CustomUI
{
    [BepInPlugin("com.35117.customui", "CustomUI 自定义交互界面", "26.10.12.3")]
    public class CustomUIPlugin : BaseUnityPlugin
    {
        internal static CustomUIPlugin Instance;

        // ⚠ 改 GUID 时这两处必须同步：本常量 与 下方 [BepInPlugin] 的第一参数。
        //   补丁自检按 owner 匹配，不一致会导致自检永远报「没有本插件的补丁」。
        internal const string HarmonyId = "com.35117.customui";

        // ⚠ **版本号的唯一真源就是上面 [BepInPlugin] 的第三参数**（日志与 BepInEx 元数据都以它为准）。
        //   别在这里另立一个常量、也别在头注释里再写一遍 —— 写两处就会漂
        //   （实测：`26.10.3.37`–`.40` 四轮改动都忘了改版本号，文档一路写到 `.41` 才发现）。
        //   `_check_customui.py` 有一条门禁盯着「README 头部版本 == 这里」。

        // PluginManager（插件管理）的分类标签 —— 没有标签的话本插件的配置会挤进
        // 「未分类」，而且 string 配置只会渲染成手输框。
        // ⚠ 标签值必须与其他插件**同名**才能归到同一分类（同族插件用的是
        //   "Unturned.Category:通用设置"，这里保持一致，别自己造新词）。
        private const string CatGeneral = "Unturned.Category:通用设置";
        private const string CatBlueprint = "Unturned.Category:蓝图视图";

        /// <summary>给配置挂分类标签（可选再挂 Unturned.Cycle 之类的控件标签）。</summary>
        private static ConfigDescription Cat(string categoryTag, string description, params object[] extraTags)
        {
            return Category(categoryTag, description, null, extraTags);
        }

        /// <summary>带候选值列表的版本：PluginManager 会据此渲染点击循环切换的标签。</summary>
        private static ConfigDescription Cat(string categoryTag, string description,
            BepInEx.Configuration.AcceptableValueBase acceptableValues, params object[] extraTags)
        {
            return Category(categoryTag, description, acceptableValues, extraTags);
        }

        private static ConfigDescription Category(string categoryTag, string description,
            BepInEx.Configuration.AcceptableValueBase acceptableValues, params object[] extraTags)
        {
            object[] tags;
            if (extraTags == null || extraTags.Length == 0)
            {
                tags = new object[] { categoryTag };
            }
            else
            {
                tags = new object[extraTags.Length + 1];
                tags[0] = categoryTag;
                for (int i = 0; i < extraTags.Length; i++)
                {
                    tags[i + 1] = extraTags[i];
                }
            }
            return new ConfigDescription(description, acceptableValues, tags);
        }

        // 本插件实例（供补丁与界面层访问配置）
        internal static ConfigEntry<bool> cfgEnabled;

        /// <summary>★ 26.10.3.28：这些箱型**保持原版**、不接管（逗号分隔的资产名或 GUID）。</summary>
        internal static ConfigEntry<string> cfgVanillaBoxes;

        /// <summary>★ 26.10.3.67：接管时**保留**原版存储网格（并存），默认 false = 现状。</summary>
        internal static ConfigEntry<bool> cfgKeepVanillaGrid;

        internal static ConfigEntry<string> cfgDefaultView;
        internal static ConfigEntry<bool> cfgBlueprintEnabled;
        internal static ConfigEntry<int> cfgBlueprintMax;
        internal static ConfigEntry<bool> cfgBlueprintShowMissing;
        internal static ConfigEntry<float> cfgBlueprintPoll;
        internal static ConfigEntry<bool> cfgBlueprintConsumeFromBox;
        internal static ConfigEntry<string> cfgBlueprintPlacement;
        internal static ConfigEntry<float> cfgBlueprintSectionHeight;
        internal static ConfigEntry<string> cfgBlueprintLayout;
        internal static ConfigEntry<string> cfgBlueprintOnlyCategories;
        internal static ConfigEntry<string> cfgBlueprintOnlyProducts;
        internal static bool Enabled = true;

        // 日志包装：BaseUnityPlugin.Logger 是 protected，补丁类（非子类）不能直接访问
        internal static void Log(string msg)
        {
            if (Instance != null) Instance.Logger.LogInfo("[CustomUI] " + msg);
        }

        internal static void Warn(string msg)
        {
            if (Instance != null) Instance.Logger.LogWarning("[CustomUI] " + msg);
        }

        internal static void Error(string msg)
        {
            if (Instance != null) Instance.Logger.LogError("[CustomUI] " + msg);
        }

        private void Awake()
        {
            Instance = this;
            try
            {
                cfgEnabled = Config.Bind("General", "Enabled", true,
                    Cat(CatGeneral, "功能总开关（关闭后不接管任何界面，全部走原版）"));

                // ★ 26.10.3.28：让「某些箱子保持原版」成为一件事。
                //   ⚠ 与上面的 Enabled 是**两回事**：Enabled=false 是全局关；
                //     这里只影响列出来的箱型，其余箱子照常接管。
                cfgVanillaBoxes = Config.Bind("General", "VanillaBoxes", "",
                    Cat(CatGeneral, "**保持原版界面**的箱子（逗号分隔，留空 = 全部接管）。" +
                        "填资产英文名（Locker / Crate_Pine / Fridge …，大小写不敏感）或 32 位 GUID。" +
                        "原版共有 46 种 Storage 类储物路障。改完**立即生效**。" +
                        "⚠ 只影响这里列出的箱型；想全局关用上面的 Enabled。"));

                // ★ 26.10.3.67：接管时**保留**原版存储网格（默认关 = 现状：藏起来由我们整栈顶替）。
                cfgKeepVanillaGrid = Config.Bind("General", "KeepVanillaGrid", false,
                    Cat(CatGeneral, "**接管时保留原版存储网格**（默认关）。" +
                        "开 = 上面显示原版箱子（能照原版方式拖放）、下面显示本插件的界面；" +
                        "关 = 直接由本插件界面顶替原版网格那一行（现状）。" +
                        "⚠ 原版网格会被拉高，多出来的那段画空槽轮廓、正好在面板底下 ——" +
                        "面板底色不透明就看不出来，半透明会隐约透出格子。改完**立即生效**。"));

                // ⚠ DefaultView 带 AcceptableValueList + Unturned.Cycle：
                //   PluginManager（插件管理）据此渲染成**点击循环切换**的标签
                //   （左键下一档、右键上一档），而不是让人手输字符串。
                //   选项顺序就是循环顺序；值必须与 ApplyDefaultView 里的判据一致。
                cfgDefaultView = Config.Bind("General", "DefaultView", "grid",
                    Cat(CatGeneral, "默认视图：grid = 大格子（一行 4 个），list = 列表（一行一物品），" +
                        "blueprint = 蓝图（配方表，另需 [Blueprint] Enabled = true）。" +
                        "其他插件可用 StorageUiRegistry.BindBox 按箱型覆盖。改完**立即生效**" +
                        "（当前开着的界面会就地换成新视图）。",
                        new AcceptableValueList<string>("grid", "list", "blueprint"),
                        "Unturned.Cycle"));

                cfgBlueprintEnabled = Config.Bind("Blueprint", "Enabled", false,
                    Cat(CatBlueprint, "蓝图视图总开关（默认关）。关掉时它对任何箱子都不接管，" +
                        "包括被 BindBox 绑到 customui.blueprintview 的箱子。"));
                cfgBlueprintMax = Config.Bind("Blueprint", "MaxEntries", 150,
                    Cat(CatBlueprint, "蓝图视图一次最多列多少条配方（10-500）。排序后截断，" +
                        "所以被截掉的一定是「缺材料」那一批。",
                        new AcceptableValueRange<int>(10, 500)));
                cfgBlueprintShowMissing = Config.Bind("Blueprint", "ShowMissing", true,
                    Cat(CatBlueprint, "是否列出「箱内材料不够」的配方。关掉后只剩能合与取出即可的，列表短很多。"));
                cfgBlueprintPoll = Config.Bind("Blueprint", "PollSeconds", 0.5f,
                    Cat(CatBlueprint, "蓝图视图重算间隔（秒，0-10；0 = 只在箱子/背包变化时算）。" +
                        "存在的理由：配方可能要求附近的工作台，而玩家会走动。",
                        new AcceptableValueRange<float>(0f, 10f)));
                cfgBlueprintConsumeFromBox = Config.Bind("Blueprint", "ConsumeFromBox", true,
                    Cat(CatBlueprint, "允许「扣箱内材料直接合成」：配方行上出现合成按钮。" +
                        "只在单机/主机生效（联机时箱子内容只有服务端能改，本插件没做服务端半边）。" +
                        "关掉 = 蓝图视图回到纯只读。扣料先扣箱内、不足再扣身上，" +
                        "产物按 箱子 → 背包 → 地上 的顺序给；一次点 1 次。"));

                // 摆法：below / above = 右列区块栈里排在箱内物品之下 / 之上；
                //              replace = 抢箱内物品那块位置，作为第三个视图与 grid/list 互斥。
                cfgBlueprintPlacement = Config.Bind("Blueprint", "Placement", "below",
                    Cat(CatBlueprint, "蓝图列表摆在右列区块栈的哪里：below = 箱内物品之下（右侧三段：" +
                        "箱内物品 / 蓝图列表 / 附近物品），与大格子/列表同时显示；" +
                        "above = 排在箱内物品**上面**（蓝图在前、箱子内容在后）；" +
                        "replace = 直接占据箱内物品那块位置（这时才用 [General] DefaultView = blueprint）。" +
                        "其他插件可用 StorageUiRegistry.SetRegionOrder 覆盖这个顺序。",
                        new AcceptableValueList<string>("below", "above", "replace"),
                        "Unturned.Cycle"));
                cfgBlueprintSectionHeight = Config.Bind("Blueprint", "SectionMaxHeight", 240f,
                    Cat(CatBlueprint, "区块摆法下蓝图列表的最大高度（90-510，超出内部滚动）。" +
                        "调太大会把「附近物品」挤出可视区。",
                        new AcceptableValueRange<float>(90f, 510f)));
                // 蓝图自己的排布：list = 一行一配方（带材料明细）；
                //              grid = 大格子（一行 4 个，图标在上 + 短状态，明细进 Tooltip，点整格合成）
                cfgBlueprintLayout = Config.Bind("Blueprint", "Layout", "list",
                    Cat(CatBlueprint, "蓝图条目的排布：list = 列表（一行一配方，带材料明细和「合成」按钮）；" +
                        "grid = 大格子（一行 4 个，图标在上、下面是产物名与短状态，明细看 Tooltip，" +
                        "点整格即可合成）。同样的高度里格子能多看到一倍的配方。",
                        new AcceptableValueList<string>("list", "grid"),
                        "Unturned.Cycle"));

                // 显示范围：两条都是「留空 = 不限」。类别认资产名（英文）也认显示名（中文）。
                cfgBlueprintOnlyCategories = Config.Bind("Blueprint", "OnlyCategories", "",
                    Cat(CatBlueprint, "只显示这些类别的配方（逗号分隔，留空 = 不限）。" +
                        "原版类别：Ammo 弹药 / Apparel 服饰 / Barricade 可放置物品 / Furniture 家具 / " +
                        "Gear 器械 / Repair 维修 / Salvage 拆解 / Structure 建筑 / Supply 补给品 / " +
                        "Tool 工具 / Utilities 实用品（内容包可自加）。" +
                        "其他插件也能改这个范围：StorageBlueprintFilters.Add。"));
                cfgBlueprintOnlyProducts = Config.Bind("Blueprint", "OnlyProducts", "",
                    Cat(CatBlueprint, "只显示产物是这些物品的配方（逗号分隔物品 id，留空 = 不限）。" +
                        "例如 22,25 = 只留这两件。要按更复杂的规则限定，" +
                        "用 StorageBlueprintFilters.Add（支持按箱子区分）。"));

                Enabled = cfgEnabled.Value;
                ApplyDefaultView();

                Harmony harmony = new Harmony(HarmonyId);
                // ⚠ 绝不用 CreateAndPatchAll(typeof(X))：嵌套 [HarmonyPatch] 类会静默失效。
                //   必须传 Assembly，并在下方自检补丁是否真的挂上。
                harmony.PatchAll(typeof(CustomUIPlugin).Assembly);

                // 补丁自检：挂不上就报出来，别等运行时才发现界面没接管
                CheckPatchApplied(typeof(PlayerDashboardInventoryUI), "updateBoxAreas");
                CheckPatchApplied(typeof(PlayerInventory), "openStorage");
                CheckPatchApplied(typeof(PlayerInventory), "closeStorage");
                CheckPatchApplied(typeof(PlayerInventory), "ReceiveDragItem");

                // 注册三个内置视图。优先级由 DefaultView 配置决定（三者一定互不相同），
                // 第三方插件想完全接管时注册更高优先级即可。
                StorageUiRegistry.Register(new StorageGridView());
                StorageUiRegistry.Register(new StorageListView());
                StorageUiRegistry.Register(new StorageBlueprintView());

                Log("插件启动完成（界面接管已启用=" + Enabled +
                    "，已注册界面提供者 " + StorageUiRegistry.Count + " 个）");
            }
            catch (Exception e)
            {
                Error("初始化异常：" + e);
            }
        }

        // 补丁自检：用 GetPatchInfo 确认目标方法上确实挂着**本插件**的补丁。
        // ⚠ 判定必须同时接受 Prefix 与 Postfix，并按 owner 精确匹配：
        //   · 只查 Postfix 会漏掉 updateBoxAreas（它挂的是 Prefix）→ 启动日志
        //     误报「自检失败」，反而把真正的失效信息淹没掉；
        //   · 只查「有补丁」会被其它插件的补丁蒙混过关（本项目同时装了十几个），
        //     因此必须比 owner。
        private static void CheckPatchApplied(Type target, string methodName)
        {
            try
            {
                System.Reflection.MethodInfo mi = AccessTools.Method(target, methodName);
                if (mi == null)
                {
                    Warn("补丁自检失败：找不到方法 " + target.Name + "." + methodName);
                    return;
                }
                Patches patches = Harmony.GetPatchInfo(mi);
                if (patches == null)
                {
                    Warn("补丁自检失败：" + target.Name + "." + methodName + " 上没有任何补丁");
                    return;
                }
                if (!HasOwnPatch(patches.Prefixes) && !HasOwnPatch(patches.Postfixes))
                {
                    Warn("补丁自检失败：" + target.Name + "." + methodName +
                        " 上没有本插件（" + HarmonyId + "）的补丁");
                }
            }
            catch (Exception e)
            {
                Warn("补丁自检异常（" + methodName + "）：" + e.Message);
            }
        }

        private static bool HasOwnPatch(System.Collections.Generic.IEnumerable<Patch> list)
        {
            if (list == null) return false;
            foreach (Patch p in list)
            {
                if (p != null && p.owner == HarmonyId) return true;
            }
            return false;
        }

        /// <summary>
        /// 把「默认视图」配置翻译成三个内置 Provider 的**优先级差**。
        ///
        /// ⚠ 三者优先级必须**互不相同** —— 相同的话 `StorageUiRegistry.Find` 取哪个
        ///   就取决于排序稳定性，会出现「同一类箱子有时列表、有时大格子」的
        ///   不确定行为，而且极难排查。
        /// ⚠ 蓝图视图即便当默认视图，也仍要 [Blueprint] Enabled = true 才接管；
        ///   它 CanHandle 返回 false 时注册表会继续往下找，自动退回大格子/列表。
        /// </summary>
        private static void ApplyDefaultView()
        {
            string v = (cfgDefaultView != null) ? cfgDefaultView.Value : "grid";
            bool listIsDefault = string.Equals(v, "list", StringComparison.OrdinalIgnoreCase);
            bool blueprintIsDefault = string.Equals(v, "blueprint", StringComparison.OrdinalIgnoreCase);

            // 摆法（below / above = 右列区块栈的两个位置 / replace = 抢存储槽位）
            string place = (cfgBlueprintPlacement != null) ? cfgBlueprintPlacement.Value : "below";
            bool replaceMode = string.Equals(place, "replace", StringComparison.OrdinalIgnoreCase);
            StorageBlueprintView.AsSection = !replaceMode;
            StorageBlueprintView.SectionOrder = string.Equals(place, "above",
                StringComparison.OrdinalIgnoreCase)
                ? StorageUiRegistry.RegionOrderAboveStorage
                : StorageUiRegistry.RegionOrderBelowStorage;
            StorageBlueprintView.SectionMaxListHeight = (cfgBlueprintSectionHeight != null)
                ? Mathf.Clamp(cfgBlueprintSectionHeight.Value, 90f, 510f) : 240f;

            // 显示范围（留空 = 不限）。第三方插件通过 StorageBlueprintFilters.Add
            // 各挂自己的命名判据，与这里叠加（AND），互不覆盖。
            StorageBlueprintFilters.SetConfigFilter(
                cfgBlueprintOnlyCategories == null ? "" : cfgBlueprintOnlyCategories.Value,
                cfgBlueprintOnlyProducts == null ? "" : cfgBlueprintOnlyProducts.Value);

            // 蓝图条目排布（list / grid）—— 视图内部会检测变化并作废常驻池子
            StorageBlueprintView.AsGrid = string.Equals(
                cfgBlueprintLayout == null ? "list" : cfgBlueprintLayout.Value,
                "grid", StringComparison.OrdinalIgnoreCase);

            StorageGridView.currentPriority = listIsDefault ? 0 : 10;
            StorageListView.currentPriority = listIsDefault ? 10 : 0;

            // ⚠ 上面两行的写法是故意的：即使蓝图占了默认视图，grid/list 也保持 10/0 的
            //   相对差。若写成「非 grid 默认就都是 0」，grid 与 list 会同优先级 ——
            //   那正是本方法注释里禁止的情况（取谁取决于排序稳定性）。

            // 蓝图视图：只有 replace 摆法且被设成默认视图时才去抢槽位（11）；
            // 其余情况排到最后（-10），区块模式更是 CanHandle 直接 false。
            bool blueprintTakesSlot = blueprintIsDefault && !StorageBlueprintView.AsSection;
            StorageBlueprintView.Enabled = cfgBlueprintEnabled != null && cfgBlueprintEnabled.Value;
            StorageBlueprintView.IsDefaultView = blueprintIsDefault;
            StorageBlueprintView.currentPriority = blueprintTakesSlot ? 11 : -10;
            // 轮询间隔（0 = 不轮询）。上限 10 秒是防止有人填个奇怪值把功能关掉。
            StorageBlueprintView.PollInterval = (cfgBlueprintPoll != null)
                ? Mathf.Clamp(cfgBlueprintPoll.Value, 0f, 10f) : 0.5f;
            StorageBlueprintView.AllowCraftFromBox = cfgBlueprintConsumeFromBox == null
                || cfgBlueprintConsumeFromBox.Value;

            // 日志要说清「槽位归谁」+「蓝图摆在哪」+「范围有没有被限定」——
            // 排查「为什么没看到蓝图列表 / 为什么少了几条配方」时，这几行就是答案
            // （区块模式下即使 DefaultView=blueprint，槽位仍是 grid/list）。
            string slot = listIsDefault ? "列表（list）"
                : (blueprintTakesSlot ? "蓝图（blueprint）" : "大格子（grid）");
            string where = replaceMode ? "抢占存储区（replace）"
                : (StorageBlueprintView.SectionOrder < 0
                    ? "区块栈·箱内物品之上（above）"
                    : "区块栈·箱内物品之下（below）");
            string scope = "";
            if (!string.IsNullOrEmpty(cfgBlueprintOnlyCategories == null ? null : cfgBlueprintOnlyCategories.Value))
                scope = "，类别限定 " + cfgBlueprintOnlyCategories.Value;
            if (!string.IsNullOrEmpty(cfgBlueprintOnlyProducts == null ? null : cfgBlueprintOnlyProducts.Value))
                scope = scope + "，产物限定 " + cfgBlueprintOnlyProducts.Value;
            Log("存储区视图 = " + slot +
                "；蓝图：" + (StorageBlueprintView.Enabled
                    ? (where + "（上限 " + StorageBlueprintView.SectionMaxListHeight + "px）" + scope)
                    : "未启用"));
        }

        private void Update()
        {
            try
            {
                SyncLiveConfig();
                // ⚠ 对账必须**独立于** Enabled / 界面是否还开着：
                //   物品可能就在玩家关掉界面那一瞬落进背包，漏判一次 = 调用方没扣账 = 凭空复制。
                StorageUiRelay.Tick();
                StorageUiHost.Tick();
                // ★ 26.10.3.62：右键菜单是**我们自己**的浮层，引擎不会替我们关 ——
                //   玩家 Esc / Alt+Tab 让游戏收掉存储界面时，菜单必须自己收（否则孤零零留在屏幕上）。
                //   插件替**所有活着的菜单**每帧跑一遍自检（调用方不用管）。
                StorageUiMenu.TickAll();
            }
            catch (Exception e)
            {
                Error("Update 异常：" + e);
            }
        }

        // ================= ★ 「保持原版的箱子」（26.10.3.28） =================

        /// <summary>
        /// 把配置里的一个 token 解析成箱子资产 GUID。认三种写法（都大小写不敏感）：
        ///   · **32 位 GUID**（dat 里的 `GUID …` 行，最稳）；
        ///   · **数字 ID**（dat 里的 `ID 328` 行 —— 比 GUID 好抄得多）；
        ///   · **英文名**（`Asset.name`，也就是 dat 文件名，如 Locker / Crate_Pine / Fridge）。
        ///
        /// ⚠ 只在 `EItemType.STORAGE` 里找 —— 同名物品不该被当成箱子
        ///   （原版 46 种 Storage 类路障的名字不重复，但不赌这件事）。
        ///
        /// ★★ 26.10.3.44：**"表还没就绪"和"表里没这个名字"必须分开** ——
        ///   前者是启动期几帧内的**正常状态**（等一会儿自己就好），后者才是玩家配错了。
        ///   混成同一句"认不出来"会把玩家送去改一个根本没问题的配置：
        ///   实测启动期假警告刷了上百条，而那条 `Locker` 配置从头到尾都是对的
        ///   （日志里随后就有 `箱型绑定：362ab81b… → customui.vanilla`）。见坑 #69。
        ///
        /// <paramref name="how"/> 命中时回填"靠哪种写法认出来的"（只给日志用）；
        /// 没命中时为 null；表未就绪时回填 <see cref="TABLE_NOT_READY"/>。
        /// </summary>
        private static Guid ResolveBoxToGuid(string token, out string how, out bool tableNotReady)
        {
            how = null;
            tableNotReady = false;

            Guid parsed = Guid.Empty;
            if (Guid.TryParse(token, out parsed)) { how = "GUID"; return parsed; }

            // 数字 ID：抄 dat 里的 `ID 328` 比抄 32 位 GUID 容易。
            // ⚠ 认成数字就**不再回落**去当名字试 —— "328" 不可能是一个资产名；
            //   一旦回落，"ID 写错了"会被报成"名字不存在"，又把原因指错一次。
            ushort idToken;
            if (ushort.TryParse(token, out idToken))
            {
                ItemAsset byId = null;
                try { byId = Assets.find(EAssetType.ITEM, idToken) as ItemAsset; }
                catch (Exception) { }
                if (byId != null && byId.type == EItemType.STORAGE) { how = "数字 ID"; return byId.GUID; }
                return Guid.Empty;
            }

            List<ItemAsset> all = new List<ItemAsset>();
            try { Assets.find(all); }
            catch (Exception e)
            {
                Error("VanillaBoxes：遍历资产表失败：" + e.Message);
                return Guid.Empty;
            }

            // 表里**一个 Storage 都没有** ⟺ 资产表还没建好（而不是"没这个名字"）。
            // ⚠ 判据用"有没有 Storage"而不是"表空不空"：表可能已装好一半类型而恰好还没到 Storage。
            bool sawAnyStorage = false;
            for (int i = 0; i < all.Count; i++)
            {
                ItemAsset a = all[i];
                if (a == null || a.type != EItemType.STORAGE) continue;
                sawAnyStorage = true;
                if (string.Equals(a.name, token, StringComparison.OrdinalIgnoreCase))
                {
                    how = "英文名";
                    return a.GUID;
                }
            }

            if (!sawAnyStorage)
            {
                tableNotReady = true;
                how = TABLE_NOT_READY;
            }
            return Guid.Empty;
        }

        /// <summary>
        /// 落地 `[General] VanillaBoxes`：把列表里的箱型声明成「保持原版」。
        /// 返回值 = **还需不需要稍后重试**。只有"资产表还没就绪"才返回 true
        /// （<see cref="ResolveBoxToGuid"/> 的 <c>tableNotReady</c>）。
        ///
        /// ★ 26.10.3.44：名字**确实不存在**时返回 false —— 那是**终态**，
        ///   重试一万次也还是不存在。老版本分不清这两种失败，只能"认不出就一直重试"，
        ///   于是资产表就绪后仍在每帧白遍历一次资产表，而且日志被刷（见坑 #69）。
        ///
        /// ⚠ 必须**可撤销**：玩家从列表里删掉一项，那条绑定要跟着消失，
        ///   否则这个配置就变成单向的（只能加、不能减，"我删了它怎么还接管"）。
        /// ⚠ 但**只能撤销我们自己写过的那条** —— 第三方插件也可能
        ///   `BindBox(guid, VanillaViewId)` 声明它自己的箱子，无差别 `UnbindBox`
        ///   会把别人的声明一起清掉。⟹ 记住"上一轮我们写过哪些 Guid"，
        ///   只在这个交集里、且**现在仍是 vanilla** 的那些上动手。
        /// ⚠ 已绑好的不重复 `BindBox`（它会打一行日志）—— 本方法每帧都可能被调，
        ///   不幂等 = 每帧刷屏。
        /// </summary>
        private static bool ApplyVanillaBoxes(string raw)
        {
            List<Guid> want = new List<Guid>();
            bool retryLater = false;

            if (!string.IsNullOrEmpty(raw))
            {
                string[] parts = raw.Split(new char[] { ',', ';', '|' },
                    StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    string token = parts[i].Trim();
                    if (token.Length == 0) continue;

                    string how;
                    bool notReady;
                    Guid g = ResolveBoxToGuid(token, out how, out notReady);
                    if (notReady) retryLater = true;

                    if (g == Guid.Empty)
                    {
                        // ★ 按 **token** 去重，不是"整份配置报过没有"：
                        //   列表里写了 A、B 两个错名字，就该各报一条（老版本只有一个 bool，
                        //   报完 A 就再也报不出 B）。
                        // ★ 而这个去重集合的**复位点只有一个** —— 配置原文真的变了
                        //   （见 SyncLiveConfig 的 lastVanillaRaw）。老版本把复位挂在
                        //   "每帧都成立"的条件里 ⟹ 去重等于没做 ⟹ 启动期刷屏上百条。
                        if (reportedTokens.Add(token))
                        {
                            if (notReady)
                            {
                                // 启动期表还没建好：**正常状态**，等一会儿自己就好 ——
                                // 用 Info 且明说"稍后自动重试"，别把玩家送去改一个没问题的配置。
                                Log("VanillaBoxes：「" + token + "」暂未解析（资产表还没就绪），稍后自动重试");
                            }
                            else
                            {
                                Warn("VanillaBoxes：「" + token + "」认不出来：资产表里没有叫这个名字的 Storage。" +
                                    "可填**英文名**（dat 文件名，如 Locker / Crate_Pine / Fridge）、" +
                                    "**数字 ID**（如 328）或**32 位 GUID**。");
                            }
                        }
                    }
                    else if (!want.Contains(g))
                    {
                        want.Add(g);
                        // 认出来也报一条（一次性）：玩家能当场看出自己写的名字对不对，
                        // 而不是只能靠"箱子行为变了没有"去猜。
                        if (reportedTokens.Add(token))
                        {
                            Log("VanillaBoxes：「" + token + "」→ " + g.ToString("N") +
                                "（按" + how + "认出来）");
                        }
                    }
                }
            }

            // ① 撤销：上一轮写过、这一轮不在列表里、且现在仍然是 vanilla 的
            for (int i = 0; i < vanillaBoxGuids.Count; i++)
            {
                Guid g = vanillaBoxGuids[i];
                if (!want.Contains(g) && StorageUiRegistry.IsBoxVanilla(g))
                {
                    StorageUiRegistry.UnbindBox(g);
                    Log("VanillaBoxes：已解除「" + g.ToString("N") + "」的保持原版（它已从配置里删掉）");
                }
            }

            // ② 新写：已经绑好的跳过（BindBox 会打日志，每帧重复就是刷屏）
            vanillaBoxGuids.Clear();
            for (int i = 0; i < want.Count; i++)
            {
                if (!StorageUiRegistry.IsBoxVanilla(want[i]))
                    StorageUiRegistry.BindBox(want[i], StorageUiRegistry.VanillaViewId);
                vanillaBoxGuids.Add(want[i]);
            }

            return retryLater;
        }

        /// <summary>上一轮由 VanillaBoxes 写进注册表的箱型（撤销时**只认这些**，见 ApplyVanillaBoxes）。</summary>
        private static readonly List<Guid> vanillaBoxGuids = new List<Guid>();

        /// <summary>
        /// 每条 token 的结论只报一次（认出来一条 Info / 认不出一条 Warn，
        /// 表未就绪一条 Info）。★ 唯一复位点 = 配置原文真的变了（<see cref="lastVanillaRaw"/>）。
        /// </summary>
        private static readonly HashSet<string> reportedTokens = new HashSet<string>();

        /// <summary>"资产表还没就绪"的哨兵（只回填给日志用，不参与匹配）。</summary>
        private const string TABLE_NOT_READY = "<资产表未就绪>";

        /// <summary>
        /// 还需要重试时，隔这么多帧再试一次（≈0.5 秒 @60fps）。
        /// ⚠ 不要每帧重试：每帧遍历几百个资产是纯浪费，而且这正是刷屏的另一半原因。
        /// </summary>
        private const int VANILLA_RETRY_FRAMES = 30;

        // ---- 配置热生效 ----
        // PluginManager（插件管理）改配置的方式是：写**内存里的 ConfigEntry** + 立刻 Save()，
        // 既不重启进程、也**不回调插件**（它源码里没有任何 SettingChanged 事件）。
        // 所以"改了要重启才生效"从来不是它的限制，而是**本插件自己缓存了值**的限制
        // —— 原来 Awake 里 Enabled = cfgEnabled.Value 与 ApplyDefaultView() 各只跑一次。
        // 下面每帧比对一次缓存值（7 个值，开销可忽略），变了就重新落地并就地重开界面。
        private static bool lastEnabled;
        private static bool lastKeepVanillaGrid;
        private static string lastDefaultView;
        /// <summary>
        /// `[General] VanillaBoxes` 的**配置原文**（null 起步 ⟹ 第一次 Sync 就会解析一次）。
        /// ★★ 26.10.3.44：它只回答一个问题 —— **"配置变了吗"** —— 所以**成败都要更新**。
        ///   老版本拿"上次收下的值"（只在全部认出来时才更新）兼作变更判据 ⟹
        ///   解析失败时这个条件**每帧都成立** ⟹ 挂在它下面的去重复位也每帧跑 ⟹
        ///   启动期假警报刷上百条（见坑 #69）。
        /// </summary>
        private static string lastVanillaRaw;
        /// <summary>当前这份配置是否已经**定论**（全部认出来，或已确认某些名字确实不存在）。</summary>
        private static bool vanillaDone;
        /// <summary>还需重试时要等的帧数（退避；0 = 这一帧就可以试）。</summary>
        private static int vanillaRetry;
        private static bool lastBpEnabled;
        private static bool lastBpConsumeFromBox;
        private static float lastBpPoll;
        private static string lastBpPlacement;
        private static string lastBpLayout;
        private static float lastBpSectionHeight;
        private static string lastBpCategories;
        private static string lastBpProducts;
        private static bool liveSyncInitialized;

        private static void SyncLiveConfig()
        {
            if (cfgEnabled == null || cfgDefaultView == null || cfgBlueprintEnabled == null ||
                cfgBlueprintConsumeFromBox == null || cfgBlueprintPoll == null ||
                cfgVanillaBoxes == null || cfgKeepVanillaGrid == null)
            {
                return;     // Awake 还没跑完（理论上不会发生，这里是不信任绑定顺序的兜底）
            }

            if (!liveSyncInitialized)
            {
                lastEnabled = Enabled;
                lastKeepVanillaGrid = StorageUiHost.KeepVanillaGrid;
                lastDefaultView = cfgDefaultView.Value;
                lastBpEnabled = StorageBlueprintView.Enabled;
                lastBpConsumeFromBox = StorageBlueprintView.AllowCraftFromBox;
                lastBpPoll = StorageBlueprintView.PollInterval;
                lastBpPlacement = cfgBlueprintPlacement == null ? "below" : cfgBlueprintPlacement.Value;
                lastBpLayout = cfgBlueprintLayout == null ? "list" : cfgBlueprintLayout.Value;
                lastBpSectionHeight = cfgBlueprintSectionHeight == null
                    ? 240f : cfgBlueprintSectionHeight.Value;
                lastBpCategories = cfgBlueprintOnlyCategories == null
                    ? "" : cfgBlueprintOnlyCategories.Value;
                lastBpProducts = cfgBlueprintOnlyProducts == null
                    ? "" : cfgBlueprintOnlyProducts.Value;
                liveSyncInitialized = true;
                return;
            }

            // ★ VanillaBoxes 单独处理（**故意不挂进下面的 changed**）：它的"解析"依赖资产表，
            //   而 Awake 期间资产可能还没加载完 ⟹ 失败时要能重试，而重试不该连累
            //   ApplyDefaultView 反复打日志。
            //   ★★ 26.10.3.44：这里必须分成**两个字段** —— "配置变了没"（lastVanillaRaw，
            //   成败都更新）与"落地了没"（vanillaDone）。老版本让一个字段兼两职，
            //   失败时"变了"永远为真 ⟹ 每帧重试 + 每帧复位去重标记 ⟹ 启动期刷屏（坑 #69）。
            string vanilla = (cfgVanillaBoxes == null) ? "" : cfgVanillaBoxes.Value;
            if (vanilla != lastVanillaRaw)
            {
                lastVanillaRaw = vanilla;       // 无论认不认得出都记住：它只管"变了吗"
                reportedTokens.Clear();         // 配置真的变了 → 每条 token 允许重新报一次
                vanillaDone = false;
                vanillaRetry = 0;               // 立刻试一次
            }
            if (!vanillaDone)
            {
                if (vanillaRetry > 0)
                {
                    vanillaRetry--;             // 退避中：这一帧什么都不做
                }
                else
                {
                    bool retryLater = ApplyVanillaBoxes(vanilla);
                    if (retryLater) vanillaRetry = VANILLA_RETRY_FRAMES;   // 表没就绪 → 过一会儿再来
                    else vanillaDone = true;                               // 已定论（含"名字确实不存在"）
                }
            }

            bool keepVanilla = cfgKeepVanillaGrid.Value;
            bool enabled = cfgEnabled.Value;
            string view = cfgDefaultView.Value;
            bool bpEnabled = cfgBlueprintEnabled.Value;
            bool consume = cfgBlueprintConsumeFromBox.Value;
            float poll = cfgBlueprintPoll.Value;
            string place = cfgBlueprintPlacement == null ? "below" : cfgBlueprintPlacement.Value;
            float secH = cfgBlueprintSectionHeight == null ? 240f : cfgBlueprintSectionHeight.Value;
            string layout = cfgBlueprintLayout == null ? "list" : cfgBlueprintLayout.Value;
            string cats = cfgBlueprintOnlyCategories == null ? "" : cfgBlueprintOnlyCategories.Value;
            string prods = cfgBlueprintOnlyProducts == null ? "" : cfgBlueprintOnlyProducts.Value;

            bool changed = enabled != lastEnabled || keepVanilla != lastKeepVanillaGrid ||
                view != lastDefaultView ||
                bpEnabled != lastBpEnabled || consume != lastBpConsumeFromBox ||
                poll != lastBpPoll || place != lastBpPlacement ||
                layout != lastBpLayout ||
                cats != lastBpCategories || prods != lastBpProducts ||
                Mathf.Abs(secH - lastBpSectionHeight) > 0.5f;
            if (!changed) return;

            lastEnabled = enabled;
            lastKeepVanillaGrid = keepVanilla;
            lastDefaultView = view;
            lastBpEnabled = bpEnabled;
            lastBpConsumeFromBox = consume;
            lastBpPoll = poll;
            lastBpPlacement = place;
            lastBpLayout = layout;
            lastBpSectionHeight = secH;
            lastBpCategories = cats;
            lastBpProducts = prods;

            Enabled = enabled;
            // ★ 26.10.3.67：并存开关只影响**下一次排版**（`ApplyVanillaOverride` / `LayoutStack` 每帧读它）
            //   ⟹ 改完不必重建界面；但要让宿主**立刻重排一次**，否则要等下一个触发点才生效。
            if (StorageUiHost.KeepVanillaGrid != keepVanilla)
            {
                StorageUiHost.KeepVanillaGrid = keepVanilla;
                StorageUiRegistry.RequestRegionsChanged();
            }
            ApplyDefaultView();

            // 优先级是运行时算出来的，注册表里的顺序必须跟着重排，
            // 否则 FindCandidates 还是按注册那一刻的旧顺序找。
            StorageUiRegistry.Resort();

            // 摆法（below / above / replace）改的是**区块栈里的顺序**，
            // 光重排优先级不够 —— 还要请宿主重算区块栈。
            StorageUiRegistry.RequestRegionsChanged();

            Log("配置已热生效：总开关=" + enabled + "，默认视图=" + view +
                "，蓝图视图=" + bpEnabled + "，扣箱合成=" + consume + "，轮询=" + poll +
                "s，蓝图排布=" + layout + "，摆法=" + place +
                "，保留原版网格=" + keepVanilla +
                "，类别限定=" + (string.IsNullOrEmpty(cats) ? "不限" : cats) +
                "，产物限定=" + (string.IsNullOrEmpty(prods) ? "不限" : prods));

            // 界面正开着 → 就地换成新视图（面板常驻，所以切换不会重载图标）
            StorageUiHost.ReloadForConfigChange();
        }

        private void OnDestroy()
        {
            try { StorageUiHost.OnPluginShutdown(); } catch (Exception) { }
            Instance = null;
        }
    }
}
