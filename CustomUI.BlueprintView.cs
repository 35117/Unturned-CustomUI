// ============================================================================
//  CustomUI · 内置「蓝图视图」Provider（只读）
// ============================================================================
//  形态：右侧一行一个**配方** —— 左边产物图标，第一行「产物名 ×N 【状态】」，
//        第二行逐个材料列「可用/需要（箱内几）」。
//
//  这一行会改数据吗？会 —— 但只在开了 [Blueprint] ConsumeFromBox 且本机是服务端时。
//    · 默认（联机客户端 / 关掉开关）：纯只读，行上没有按钮，点了什么都不发生。
//    · 单机或主机 + 开关开着 + 配方属于支持子集：行右侧出现「合成」，
//      点它会**真的扣材料**（先箱内、不足再身上）并给出产物 —— 具体规则、
//      与原版的行为差异全在 CustomUI.Crafting.cs 的文件头，那里是唯一事实源。
//    ⚠ 为什么不像原版那样只读：原版合成永远不消耗箱内材料（下面 ★ 那条），
//      所以「箱里材料齐、身上没有」这种情况在原版里根本没法就地完成。
//      用户 2026-09-26 明确要求做「自己消耗、直接扣箱子」，故本视图承担这个动作。
//
//  ★ 一条决定显示口径的引擎事实（3.26.3.8 源码，PlayerCrafting.cs:564-565）：
//     原版搜材料时
//         IncludeEquipmentSlots       = !inputItemConfig.ShouldConsume
//         IncludeActiveStorageContainer = !inputItemConfig.ShouldConsume
//     也就是说 **要消耗的材料只在背包页（2..6）里找，绝不从正打开的箱子里扣**
//     （Nelson 注释：as for storage container, I'm preserving the old behavior）。
//     只有「工具」类材料（ShouldConsume == false）才会去看箱子和装备格。
//     ⟹「箱子里材料齐全」**不等于**「现在能合成」。
//     所以状态分四档，把「取出材料即可合成」单独标出来，而不是给人一个假的「可合成」。
//
//  零反射：蓝图枚举走 public 的 `Assets.find<List<IBlueprintOwner>>`
//    （与原版 PlayerDashboardCraftingUI 内部同一做法），材料计数按引擎语义自己算
//    （见 CountSupply）。刻意**不碰** internal 成员：GetBlueprintOwners /
//    BlueprintStatus / IsBlueprintPermanentlyDisabled / UpdateBlueprint*Status
//    全是 internal —— 反射它们等于把功能挂在游戏更新的风险上。
//    同理不用 [Obsolete] 的 blueprint.type / skill / tool / build（改用
//    Operation / GetCategoryTag / RequiresSkill / ShouldConsume）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using SDG.Unturned;
using UnityEngine;

namespace CustomUI
{
    /// <summary>
    /// 内置蓝图视图**当前生效的配置快照**（只读）。
    ///
    /// 为什么给"快照"而不是把这 9 个旋钮改成 public 可写 —— 见
    /// <see cref="StorageBlueprintView.Current"/> 的完整说明。一句话：
    /// 它们是**本插件配置的镜像**，唯一写入方 `ApplyDefaultView()` 会在任一项蓝图配置
    /// 变化时把 9 个字段**无条件重写**；公开可写 ⟹ 第三方的写入会被**静默回退**。
    /// </summary>
    public struct StorageBlueprintViewInfo
    {
        /// <summary>[Blueprint] Enabled —— 总开关（关掉时本视图对任何箱子都不接管）。</summary>
        public bool Enabled;
        /// <summary>true = 以右列区块形式显示；false = 抢存储槽位（replace 摆法）。</summary>
        public bool AsSection;
        /// <summary>区块在右列栈里的顺序（上=RegionOrderAboveStorage，下=RegionOrderBelowStorage）。</summary>
        public int RegionOrder;
        /// <summary>区块模式下列表的高度上限（像素）。</summary>
        public float SectionListHeight;
        /// <summary>true = 大格子摆法；false = 列表摆法。</summary>
        public bool GridLayout;
        /// <summary>是否被配置选为默认视图。</summary>
        public bool IsDefaultView;
        /// <summary>低频轮询间隔（秒）。0 = 不轮询。</summary>
        public float PollSeconds;
        /// <summary>是否允许「扣箱内材料」合成。</summary>
        public bool CraftsFromBox;
        /// <summary>接管候选链里的优先级（抢槽位时 11，否则 -10；由配置算出来）。</summary>
        public int Priority;
    }

    /// <summary>
    /// 内置蓝图列表。默认**不启用**：要 [Blueprint] Enabled = true。
    /// 两种摆法（[Blueprint] Placement）：
    ///   · below（默认）= 右列区块栈里排在箱内物品**之下**（右侧于是三段：箱内物品 / 蓝图 / 附近物品）；
    ///   · above = 同一个区块，但排在箱内物品**之上**；
    ///   · replace = 抢存储槽位，作为第三个视图与 grid/list 互斥（此时 DefaultView=blueprint 才生效）。
    /// 行右侧的「合成」按钮会改数据，条件见文件头（默认只在单机/主机 + 开关开着时出现）。
    /// 显示哪些配方还能被别的插件限定（<see cref="StorageBlueprintFilters"/>）。
    /// 想知道"它现在到底配成什么样"读 <see cref="Current"/>。
    /// </summary>
    public class StorageBlueprintView : IStorageUiProvider, IStorageUiRegion
    {
        public string Id { get { return "customui.blueprintview"; } }
        public int Priority { get { return currentPriority; } }

        /// <summary>
        /// 当前生效的配置快照（**只读**）。
        ///
        /// 用途：第三方插件 / 诊断面板想知道「蓝图这一块现在到底是什么样」——
        /// 摆法（区块还是抢槽位）、排在谁上面、高度上限、启没启用、能不能扣箱内材料合成 ——
        /// 以前这些**全问不出来**（9 个旋钮都是 `internal static`）。
        ///
        /// ⚠⚠ **为什么是快照，而不是把这 9 个旋钮改成 public 可写**（核过之后才定的，别改回去）：
        ///   这 9 个字段都是**本插件配置项的镜像**，唯一写入方是
        ///   <c>CustomUIPlugin.ApplyDefaultView()</c>；而 `SyncLiveConfig` 在**任一项蓝图配置
        ///   变化时**都会调它，**把 9 个字段全部无条件重写**。
        ///   ⟹ 若把它们改成 public 可写，第三方写进去的值会在玩家下一次动配置时**静默回退**；
        ///   而"改了当时有用、过一会儿自己变回去"是**最难查**的一类 bug
        ///   （症状离原因很远，而且中间还夹着一次玩家操作）。
        ///   ★ 真要**改**，走三条公开入口 —— 它们各自有明确的归属方，不会被配置覆盖：
        ///     · **位置** → <c>StorageUiRegistry.SetRegionOrder(Id, n)</c>
        ///     · **显隐** → <c>StorageUiRegistry.SetRegionVisible(source, Id, visible)</c>
        ///     · **显示哪些配方** → <c>StorageBlueprintFilters.Add(source, …)</c>
        ///   本视图的 Id 就是 <see cref="Id"/>（`customui.blueprintview`）。
        ///   ⚠ 只读快照是**一次取齐**的：不要在两次读之间假设它们没变（配置可在任意帧热生效）。
        /// </summary>
        public static StorageBlueprintViewInfo Current
        {
            get
            {
                StorageBlueprintViewInfo info = new StorageBlueprintViewInfo();
                info.Enabled = Enabled;
                info.AsSection = AsSection;
                info.RegionOrder = SectionOrder;
                info.SectionListHeight = SectionMaxListHeight;
                info.GridLayout = AsGrid;
                info.IsDefaultView = IsDefaultView;
                info.PollSeconds = PollInterval;
                info.CraftsFromBox = AllowCraftFromBox;
                info.Priority = currentPriority;
                return info;
            }
        }

        // ====================================================================
        // ⚠⚠ 这一组（9 个）都是**本插件配置的镜像**，唯一写入方是
        //   `CustomUIPlugin.ApplyDefaultView()`，它在任一项蓝图配置变化时会把它们**全部重写**。
        //   ⟹ **别把它们改成 public 可写**：第三方的写入会被配置热生效**静默回退**。
        //   要对外暴露就读 <see cref="Current"/>（只读快照）；要改走
        //   `SetRegionOrder` / `SetRegionVisible` / `StorageBlueprintFilters` 三条公开入口。
        //   （自检第 14 节有一条**反向判据**钉住"这 9 个不许变成公开可写"。）
        // ====================================================================

        /// <summary>由 CustomUIPlugin 按配置项设置（默认排在两个内置视图之后）。</summary>
        internal static int currentPriority = -10;

        /// <summary>
        /// true = 以「右列区块」形式显示（不抢存储槽位）。由 [Blueprint] Placement 设置
        /// （below / above 都是区块，只有 replace 不是）。
        /// 为 true 时 <see cref="CanHandle"/> 恒返回 false —— 同一块内容不会既占槽位又出现在栈里。
        /// </summary>
        internal static bool AsSection = true;

        /// <summary>
        /// 区块在右列栈里的顺序（小者在上，箱内物品 = 0）。
        /// below → 100（箱内物品之下）；above → -100（箱内物品之上）。
        /// ⚠ 别的插件用 <c>StorageUiRegistry.SetRegionOrder(Id, n)</c> 可以覆盖它。
        /// </summary>
        internal static int SectionOrder = StorageUiRegistry.RegionOrderBelowStorage;

        /// <summary>区块模式下列表的高度上限（比抢槽位时矮，得给「附近物品」留可见空间）。</summary>
        internal static float SectionMaxListHeight = 240f;

        /// <summary>
        /// 摆法：false = 列表（一行一配方），true = 大格子（一行 4 个，图标在上）。
        /// 由 [Blueprint] Layout 设置。⚠ 两种摆法**共用同一套行池与身份认领**，
        /// 只有"摆位/单元尺寸/单元内控件"不同 —— 复制一份池子逻辑必然漂移。
        /// </summary>
        internal static bool AsGrid = false;

        /// <summary>
        /// 总开关（[Blueprint] Enabled，默认 false）。关掉时本视图对任何箱子都
        /// CanHandle = false —— 包括被 BindBox 绑定的箱子；注册表会自动退回普通视图，
        /// 不会把箱子搞成「没有界面」。
        /// </summary>
        internal static bool Enabled = false;

        /// <summary>本视图是否是默认视图（[General] DefaultView = blueprint）。</summary>
        internal static bool IsDefaultView = false;

        // ---- 布局常量 ----
        private const float ROW_HEIGHT = 58f;
        private const float ROW_GAP = 2f;
        private const float HEAD_HEIGHT = 34f;
        private const float ICON_SIZE = 48f;

        // 大格子摆法：一行 4 个，图标在上、名称与状态在下。
        // ⚠ 列宽/列位用**相对值**（SizeScale_X = 1/列数、PositionScale_X = col/列数），
        //   不能用像素 —— 面板宽度依赖原版右列，挂载前是 0（同 GridView 那条铁律）。
        private const int GRID_COLUMNS = 4;
        private const float CELL_SCALE_X = 1f / GRID_COLUMNS;
        private const float CELL_H = 118f;
        private const float CELL_NAME_H = 26f;
        private const float CELL_STATUS_H = 24f;
        private const float CELL_INNER_ICON = 44f;

        /// <summary>★ 26.10.3.52：填充层**铺满整行**（内缩 0）—— 它自己带圆角（9 宫格），
        /// 内缩反而会露出一圈原版底色。与另两个视图同一常量。</summary>
        private const float FILL_INSET = 0f;

        // ⚠ 文字行高**必须 ≥ 24**（ESleekFontSize.Small 的实测下限，见 README 坑 #21）
        private const float TITLE_H = 26f;
        private const float DETAIL_H = 24f;

        // 「合成」按钮：固定宽，右侧靠 PositionScale_X=1 + 负偏移定位。
        // 文字区**恒定让出 CRAFT_RESERVE**（不随按钮显隐变化）——
        // 否则按钮出现/消失会让文字宽度跳一下。
        private const float CRAFT_W = 62f;
        private const float CRAFT_H = 30f;
        private const float CRAFT_RESERVE = CRAFT_W + 8f;

        /// <summary>列表区最大高度，超出则内部滚动（与另两个视图同一策略）。</summary>
        private const float MAX_LIST_HEIGHT = 510f;

        /// <summary>一次重建最多评估多少个候选配方（防止极端内容包把重建拖慢）。</summary>
        private const int MAX_CANDIDATES = 400;

        /// <summary>搜索框那一行占的高度（高度只有 <see cref="SearchRowHeight"/> 一个来源）。</summary>
        private const float SEARCH_H = 30f;

        // ★ 26.10.3.35 / T7：分类过滤按钮（按**产物的 `EItemType`** 分桶）
        //   ★ 26.10.3.43：按钮条本体提成**公开可复用组件** `StorageFilterBar`（README §4.24）——
        //     第三方终于也能给自己的界面配一个"点一次筛一类"的按钮条（以前只有本插件有）。
        //     这里只留"要几类"（数组尺寸用它）；列数/行高/间距**显式设给组件**，
        //     不靠组件默认值 —— 否则以后有人改默认值会悄悄改掉本视图的观感。
        // ⚠ 26.10.3.36：6 → **9**（3 行）。用户实测「似乎不止 6 类，但只显示 6 个按钮」——
        //   6 个确实会截掉真实存在的类别，而被截掉的类别**没有任何入口**能筛到它。
        //   行数自适应（只有 2 类时就占 1 行），所以放宽上限**不花固定代价**。
        private const int FILTER_COLS = 3;                     // 一行 3 个
        private const int FILTER_MAX = FILTER_COLS * 3;         // 最多 9 个（3 行）
        private const float FILTER_BTN_H = 26f;                 // ⚠ ≥24：文字行高下限（坑 #21）
        private const float FILTER_GAP = 2f;
        /// <summary>直方图按 <c>(int)EItemType</c> 索引（枚举 ≈50 个值，定长数组最省事）。</summary>
        private const int TYPE_SLOTS = 64;

        /// <summary>
        /// 低频轮询间隔（秒），由 [Blueprint] PollSeconds 设置。0 = 不轮询
        /// （只在箱子内容变化时重算，此时「附近工作台」不会自己变新）。
        /// </summary>
        internal static float PollInterval = 0.5f;

        /// <summary>
        /// 是否允许「扣箱内材料直接合成」（[Blueprint] ConsumeFromBox，默认开）。
        /// 关掉 = 本视图回到纯只读，行上不会出现「合成」按钮。
        /// ⚠ 这只是玩家的意愿开关；**真正的可行性**还要过
        /// <see cref="StorageCrafting.IsSupported"/>（= 本机是服务端 + 配方属于支持子集）。
        /// </summary>
        internal static bool AllowCraftFromBox = true;

        // ---- 状态档位（同时决定显示顺序） ----
        private const int RANK_CRAFTABLE = 0;   // 现在就能合
        private const int RANK_FROM_BOX = 1;    // 材料都在箱内，取到身上即可合成
        private const int RANK_GATE = 2;        // 材料够，但缺工作台/技能/任务条件
        private const int RANK_MISSING = 3;     // 缺材料

        // ================= Provider 字段 =================

        private ISleekBox panel;
        private ISleekLabel headLabel;
        private ISleekScrollView scroll;
        private ISleekLabel emptyLabel;
        private StorageUiContext ctx;

        // ★ 26.10.3.51：本视图的行填充色**不走实例属性**，而是登记式 ——
        //   内置蓝图视图的实例归 CustomUI 自己所有，第三方拿不到它，
        //   给它挂实例属性就是"谁也调不到的公开面"。见 `StorageBlueprintFills`。

        /// <summary>
        /// 搜索框（★ 26.10.3.34 / T6）。★ 26.10.3.46 改用**公开可复用组件** `StorageSearchBox`
        /// —— 原来那份 `CreateStringField()` 与两个面板**逐字相同**（README 坑 #68）。
        /// 搜索词的真源就是它的 <see cref="StorageSearchBox.Query"/>，本类不再自己存一份。
        /// </summary>
        private readonly StorageSearchBox searchBox = new StorageSearchBox();

        /// <summary>「共 M 项」的那个 M —— 搜索**之前**的候选数（与"显示 N"出自同一次遍历）。</summary>
        private int preSearchEntries;

        // ---- ★ T7：分类过滤 ----
        // ★ 26.10.3.43：按钮条本体提成**公开可复用组件** `StorageFilterBar`（README §4.24）。
        //   这里只剩「本轮有哪些类、各多少个」这份**数据**，控件全归组件管 —— 不留第二份实现
        //   （两份必然会漂：改样式只改一处、另一处还留着旧观感）。
        private readonly StorageFilterBar filterBar = new StorageFilterBar();
        /// <summary>按 <c>(int)EItemType</c> 索引的类型直方图。⚠ 每轮先清零再统计。</summary>
        private readonly int[] typeHist = new int[TYPE_SLOTS];
        /// <summary>本轮拿去当按钮用的**前 N 个类型**（降序），<c>(int)EItemType</c>；-1 = 取不到类型。</summary>
        private readonly int[] catType = new int[FILTER_MAX];
        private readonly int[] catCount = new int[FILTER_MAX];
        /// <summary>按钮上那行字（不含计数 —— 计数由组件拼，见 StorageFilterBar 注释第 1 条）。</summary>
        private readonly string[] catLabel = new string[FILTER_MAX];
        /// <summary>本轮直方图里被选中的类型个数（≤ FILTER_MAX）。</summary>
        private int catUsed;
        /// <summary>挑前 N 类时的预分配草稿（避免每轮 new 数组）。</summary>
        private readonly int[] typeScratch = new int[TYPE_SLOTS];
        /// <summary>
        /// 是否正按类型筛选 + 筛的是哪个（<see cref="EItemType"/>）。
        /// ⚠ 这是**数据层**的状态，由 <see cref="OnFilterChanged"/> 从组件回调里更新 ——
        ///   组件只管控件（谁被按下、按钮长什么样），筛选条件留在这一侧，
        ///   免得"界面状态"与"数据判据"变成两份真源。
        /// </summary>
        private bool filterOn;
        private EItemType filterType;

        private int lastFingerprint = -1;
        private bool wasAttached;
        private float nextPollTime;

        // 常驻面板是按哪种摆法建的 —— 与当前 AsGrid 不一致时必须作废重建
        private bool builtAsGrid;

        private readonly List<BlueprintEntry> entries = new List<BlueprintEntry>();
        private readonly List<BlueprintRow> rows = new List<BlueprintRow>();

        // 重建期复用的索引与缓冲（避免每次重建产生垃圾）
        private readonly JarIndex boxIndex = new JarIndex();
        private readonly JarIndex packIndex = new JarIndex();
        private readonly JarIndex equipIndex = new JarIndex();
        private readonly HashSet<ItemAsset> materialAssets = new HashSet<ItemAsset>();
        private readonly List<BlueprintSupplyStat> statBuffer = new List<BlueprintSupplyStat>();
        private readonly StringBuilder sb = new StringBuilder(192);

        private int evaluatedCandidates;
        private int lastLoggedSignature = int.MinValue;

        // 上一次点「合成」的结果 —— 显示在头部几秒，让失败原因有地方露出来
        // （不弹 MenuUI.alert：那是全局模态提示，用来报一次界面内的小失败太重）。
        private string lastAction;
        private float lastActionTime;
        private const float ACTION_SHOW_SECONDS = 6f;

        // ================= IStorageUiProvider / IStorageUiRegion =================

        /// <summary>
        /// 抢不抢「存储区」那块位置（= 是不是第三个互斥视图）。
        /// ⚠ 区块模式（默认）下恒 false —— 位置让给格子/列表视图，本内容排在它们旁边。
        /// </summary>
        public bool CanHandle(StorageUiContext c)
        {
            if (AsSection) return false;
            return Accepts(c);
        }

        /// <summary>要不要进右列区块栈（宿主重算区块栈时问一次，必须廉价）。</summary>
        public bool WantsRegion(StorageUiContext c)
        {
            if (!AsSection) return false;
            return Accepts(c);
        }

        /// <summary>栈内顺序（below = 箱内物品之下，above = 之上）。</summary>
        public int RegionOrder(StorageUiContext c)
        {
            return SectionOrder;
        }

        /// <summary>
        /// 交给宿主排版的面板。宿主只挂 + 定位 + SetActive，**不会** RemoveChild。
        /// Build 之前（Open 还没跑）返回 null = 这一帧不占位。
        /// </summary>
        public ISleekElement RegionPanel(StorageUiContext c)
        {
            return panel;
        }

        /// <summary>两种摆法共用的基本可显示判据。</summary>
        private bool Accepts(StorageUiContext c)
        {
            if (!Enabled) return false;
            if (c == null || c.Container == null) return false;

            // ⚠ 专用服务器上没有本地玩家 → 必须拒绝。本视图全程要用
            //   Player.LocalPlayer.crafting / inventory。
            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null || player.crafting == null) return false;

            // 区块模式：只要总开关开着就该出现（它不抢位置，不需要额外授权）。
            if (AsSection) return true;

            // 抢槽位模式：默认视图是蓝图 → 接管所有箱子；否则只接管被 BindBox 绑定的箱型。
            if (IsDefaultView) return true;
            return c.Asset != null && StorageUiRegistry.IsBoundTo(c.Asset.GUID, Id);
        }

        public bool Open(StorageUiContext c)
        {
            try
            {
                ctx = c;
                lastFingerprint = -1;
                RefreshBlueprintCache();                       // 每次打开重扫一次资产表
                nextPollTime = Time.unscaledTime + PollInterval;

                // 常驻面板仍挂在当前挂载点上 → 复用。本视图的图标**只由配方身份决定**，
                // 所以复用池子后重开箱子的图标请求数是 0（物品视图至少还会为新物品加载）。
                // ⚠ 但**换了摆法就必须作废**：列表是一行整宽 + 右侧按钮，
                //   格子是 1/4 宽 + 图标在上 + 点整格 —— 尺寸与控件结构都不同，
                //   复用会把上一世的结构当成新摆法用（文字重叠、按钮错位）。
                if (panel != null && builtAsGrid != AsGrid) DiscardPark();

                if (!IsParkUsable()) DiscardPark();
                if (panel == null) Build();

                Rebuild(c);
                return true;
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("蓝图视图打开失败：" + e);
                DiscardPark();   // 宿主不会再调 Close，半成品必须自己拆干净
                return false;
            }
        }

        public void Close()
        {
            // ⚠ 只藏不拆（实测反馈「每次重开箱子都要重新加载图标」）：
            //   Glazier 的 RemoveChild 会 GameObject.Destroy 整棵子树 → 行与产物图标全没；
            //   而图标是统一尺寸 + 带 state，不满足原版 ItemTool.getIcon 的缓存判据
            //   （state 为空且尺寸 = size×50）→ 进不了 iconCache，只能重新排队渲染。
            //   IsVisible=false 走 SetActive(false)，控件与贴图都留着。
            try { if (panel != null) panel.IsVisible = false; } catch (Exception) { }

            ctx = null;
            entries.Clear();       // 数据每次打开重算；要留的是控件池（rows）
            evaluatedCandidates = 0;
            lastFingerprint = -1;
            lastAction = null;     // 上一次的合成结果不带进下一次打开
            // ★ 26.10.3.51：`CellFill` 的"只报一次"同样按**一次会话**算 —— 关界面即复位，
            //   否则第二次遇到完全无声（与另两个视图同一取向，见盘点 §2.4）。
            StorageBlueprintFills.ResetWarnings();
            lastLoggedSignature = int.MinValue;   // 下次打开至少记一行日志（可用来验证池子有没有复用）
            // ★ T6：搜索词也清掉 —— 它是"**这一次会话**的状态"。
            //   留着会让玩家打开下一个箱子时发现"怎么只剩几项"（其实是上一次搜的词）。
            SearchQuery = "";
            // ★ T7：分类筛选同理（会话状态）；按钮内容下一轮重建时刷新。
            //   ⚠ **组件那边也要清** —— 只清数据层会出现"其实没在筛、按钮却还亮着选中"。
            filterOn = false;
            catUsed = 0;
            filterBar.ClearSelection();
            // panel / headLabel / scroll / searchBox / filterBar / emptyLabel / rows / wasAttached 保留 —— 缓存本体。
        }

        /// <summary>
        /// 常驻面板是否还能接着用：必须仍挂在**当前**的挂载点（原版右列 areaBox）上。
        /// 切关/换服会重建整个 PlayerUI，旧面板随旧画布一起销毁，但 panel.Parent
        /// 还指着那个废掉的 areaBox 实例 —— 与当前挂载点一比就穿帮，于是丢弃重建。
        /// </summary>
        private bool IsParkUsable()
        {
            if (panel == null || scroll == null) return false;
            try
            {
                return ReferenceEquals(panel.Parent, StorageUiHost.CurrentAttachSpot());
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>丢弃常驻（这次真销毁），行池整体作废。</summary>
        private void DiscardPark()
        {
            try { if (panel != null && panel.Parent != null) panel.Parent.RemoveChild(panel); }
            catch (Exception) { }

            rows.Clear();
            emptyLabel = null;
            panel = null;
            headLabel = null;
            scroll = null;
            searchBox.Discard();   // ★ T6：下次 Build 会连同输入框一起重建
            filterBar.Discard();   // ★ T7 / 26.10.3.43：分类按钮条也一样（组件自己拆干净）
            wasAttached = false;
            builtAsGrid = false;   // 池子已废，下次 Build 按当前摆法重建
            entries.Clear();
        }

        public void Tick(StorageUiContext c)
        {
            if (panel == null) return;

            int fp = Fingerprint(c);
            bool dirty = fp != lastFingerprint;

            // 「附近有没有工作台」会随走位变化，而原版只在背包页打开/背包尺寸变化时
            // 才刷列表（onInventoryResized / onCraftingUpdated），所以原版的门槛判定本身
            // 会陈旧。本视图把「缺设备」当卖点之一，就给它一个低频轮询；
            // 间隔可配（0 = 不轮询，只在数据变化时算）。
            if (!dirty && PollInterval > 0f && Time.unscaledTime >= nextPollTime) dirty = true;

            if (dirty)
            {
                // ⚠ 顺序必须是「先重建 → 后挂载定位」（README 坑 #5）：
                //   重建会改写 panel.SizeOffset_Y，挂载定位要用新高度扩原版滚动范围。
                lastFingerprint = fp;
                nextPollTime = Time.unscaledTime + PollInterval;
                Rebuild(c);
                if (panel == null) return;   // 重建期间可能因异常被清空
            }

            // 抢槽位时自己挂到存储位（AttachAndPlacePanel 会顺带排版）；
            // 区块模式下**面板交给宿主排版** —— 宿主才知道栈里还有谁、谁在上面。
            // 两种摆法的常驻规则一致：只藏不拆（见宿主注释）。
            if (!AsSection) StorageUiHost.AttachAndPlacePanel(panel);

            // ⚠ 首次挂进 Glazier 树之后必须**作废全部行并重建一次**（坑 #19/#22）：
            //   挂载前控件拿不到真实宽度 → 行宽 0 → 行内文字（相对宽）成负值不渲染，
            //   且不会自己恢复。只 MarkDirty 不够 —— 复用条件恰好是「同一个配方」，
            //   坏控件会被原样复用，所以连身份一起作废。
            if (panel.Parent != null && !wasAttached)
            {
                wasAttached = true;
                InvalidateAll();
                MarkDirty();
            }
        }

        public void Rebuild(StorageUiContext c)
        {
            if (panel == null || c == null || c.Container == null) return;
            try
            {
                RebuildRows(c);
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("蓝图列表重建失败：" + e);
            }
        }

        private void InvalidateAll()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].Key = int.MinValue;
            }
        }

        private void MarkDirty()
        {
            lastFingerprint = -1;   // 下一帧 Tick 会重建
        }

        // ================= 搜索（★ 26.10.3.34 / T6） =================

        /// <summary>
        /// 搜索词（空 = 不筛）。改完下一帧生效。
        ///
        /// ⚠ 过滤发生在 <see cref="Evaluate"/> **之前**（过滤的是"哪些配方参与细算"）——
        ///   不是"算完再把行藏几行"：后者会白算一大堆（`Evaluate` 是全流程最贵的一步），
        ///   而且头部的"共 M 项"会与画面不符（"同一个数只在一处打折"那一族）。
        /// ⚠ 它是**这一次会话**的状态：Close() 会清掉（见那里）。
        /// </summary>
        internal string SearchQuery
        {
            get { return searchBox.Query; }
            set
            {
                string v = value ?? "";
                if (string.Equals(searchBox.Query, v)) return;
                searchBox.Query = v;
                MarkDirty();
            }
        }

        /// <summary>
        /// 搜索框那一行占的高度。**唯一的**高度来源，见 Build 与面板高度两处调用点。
        /// ★ 26.10.3.46：改由组件算（`DesiredHeight`）。⚠ 它从 `static` 变成**实例**方法
        ///   —— 高度现在是那个组件实例的属性（调用点全在实例方法里，不需要改）。
        /// </summary>
        private float SearchRowHeight()
        {
            return searchBox.DesiredHeight;
        }

        /// <summary>
        /// 用户在搜索框里打字：把新词交给 <see cref="SearchQuery"/>，再**无条件**标脏，
        /// **不在这里重建** —— 重建由下一帧的 Tick 统一做（与另两个视图同一条约定）。
        /// ⚠ 为什么不能只靠 setter 标脏：玩家打字时组件在回调**之前**就把 `Query` 设成新词了
        ///   ⟹ 这里赋同一个值会撞上 setter 的"相同就 return"而早退，打了字不筛（26.10.3.46 踩到）。
        /// </summary>
        private void OnSearchTextChanged(string text)
        {
            if (!string.Equals(searchBox.Query, text)) searchBox.Query = text;
            MarkDirty();
        }

        /// <summary>
        /// 这个配方归哪一类 —— 取**产物的** <see cref="EItemType"/>；-1 = 取不到。
        /// ⚠ 操作类配方（装弹/修理）可能没写 `outputs`，那时"产物"就是被操作的那件物品
        ///   （与 `MatchesProduct` 同一取向：`GetOwnerAsset()` 兜底）。
        /// </summary>
        private static int TypeOfBlueprint(Blueprint bp)
        {
            BlueprintOutput[] outputs = bp.outputs;
            if (outputs != null)
            {
                for (int i = 0; i < outputs.Length; i++)
                {
                    if (outputs[i] == null) continue;
                    ItemAsset a = outputs[i].FindItemAsset();
                    if (a != null) return (int)a.type;
                }
            }
            try
            {
                ItemAsset owner = bp.GetOwnerAsset() as ItemAsset;
                if (owner != null) return (int)owner.type;
            }
            catch (Exception) { }
            return -1;
        }

        /// <summary>
        /// 原版 `Item_Type_*` 那套键所在的 Local —— **要自己去读**。
        ///
        /// ⚠⚠ 这是用户报的第一个 bug（"显示全是英文"）的**真正根因**，两条都得踩对：
        ///   ① 别用 `Local.has()`：它只查 `data` 不查 fallback；
        ///   ② 更关键的是**键不在 `Provider.localization` 里** —— 原版那个
        ///      `MenuWorkshopSubmitUI` 是**屏幕级**读的（`:499` `Localization.read("/Menu/Workshop/MenuWorkshopSubmit.dat")`），
        ///      键在 `Localization/&lt;语言&gt;/Menu/Workshop/MenuWorkshopSubmit.dat`。
        ///      ⟹ 换成 `FormatOrNull` 也照样读不到（键根本不在那个字典里）。
        ///   读一次就缓存（和原版同一个文件、同一套语言回退）。
        ///   ⚠ `Localization.read` 对"文件不存在"是静默返回空 Local（不会刷日志）。
        /// </summary>
        private static Local TypeLocalization()
        {
            if (!typeLocTried)
            {
                typeLocTried = true;
                try { typeLoc = Localization.read("/Menu/Workshop/MenuWorkshopSubmit.dat"); }
                catch (Exception) { typeLoc = null; }
            }
            return typeLoc;
        }

        private static Local typeLoc;
        private static bool typeLocTried;

        /// <summary>
        /// 类型的显示名。三层（**顺序不能反**）：
        ///   ① 原版 `Item_Type_*` 本地化（见 <see cref="TypeLocalization"/>）；
        ///   ② 本表补的几个（原版只覆盖 25 个 workshop 可提交类型，
        ///      `PANTS` / `STORAGE` 这些**实测就会出现在配方列表里**）；
        ///   ③ 兜底：英文枚举名（好过显示裸键 `Item_Type_Magazine`）。
        /// </summary>
        private static string TypeLabel(int type)
        {
            if (type < 0) return "其他";

            string raw = ((EItemType)type).ToString();
            string nice = raw.Length == 0 ? raw
                : (raw.Substring(0, 1) + raw.Substring(1).ToLowerInvariant());

            try
            {
                Local loc = TypeLocalization();
                if (loc != null)
                {
                    // ⚠ 用 FormatOrNull（走 TryReadString：data 与 fallback 都查，空串也算没查到），
                    //   不用 has()（只查 data ⟹ 对这批键恒 false）。
                    string s = loc.FormatOrNull("Item_Type_" + nice);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch (Exception) { }

            string extra = ExtraTypeLabel(nice);
            return string.IsNullOrEmpty(extra) ? nice : extra;
        }

        /// <summary>
        /// 原版 `Item_Type_*` **没有覆盖**的类型（实测会出现在配方列表里的那一批）。
        /// ⚠ 这是**补充**，不是替换：原版有键的一律走原版（改本地化/换语言时自动跟着变）。
        /// </summary>
        private static string ExtraTypeLabel(string nice)
        {
            switch (nice)
            {
                case "Pants": return "裤子";
                case "Storage": return "储物";
                case "Beacon": return "信标";
                case "Farm": return "农具";
                case "Trap": return "陷阱";
                case "Refill": return "灌注";
                case "Map": return "地图";
                case "Key": return "钥匙";
                case "Box": return "盒";
                case "Cloud": return "云";
                case "Generator": return "发电机";
                case "Tank": return "储罐";
                case "Sentry": return "哨塔";
                case "Filter": return "过滤器";
                case "Library": return "图鉴";
                case "Compass": return "指南针";
                case "Charge": return "炸药";
                case "Detonator": return "引爆器";
                case "Tire": return "轮胎";
                case "Oil_pump": return "抽油机";
                case "Vehicle_repair_tool": return "车辆修理工具";
                case "Vehicle_paint_tool": return "车辆喷漆工具";
                case "Vehicle_lockpick_tool": return "车辆开锁工具";
                case "Arrest_start": return "手铐（上铐）";
                case "Arrest_end": return "手铐（解铐）";
                default: return null;
            }
        }

        /// <summary>
        /// 组件报告选中变化 —— 把"界面状态"翻成**数据层的筛选条件**（用户定的语义：
        /// 再点一次同一个 = 取消）。★ 组件只管按钮长什么样，筛选条件留在这条**唯一**的
        /// 数据层字段上，免得"谁被选中"变成两份真源。
        /// </summary>
        private void OnFilterChanged(bool hasSelection, int key)
        {
            filterOn = hasSelection;
            filterType = (EItemType)key;
            MarkDirty();
        }

        /// <summary>
        /// 把本轮直方图交给按钮条组件（文字 / 计数 / 选中态 / 可见性 / 高度全归组件管）。
        /// ⚠ `catUsed &lt; 2` 时组件**整条不占高度** —— 候选里只有一种类型时筛选没有意义。
        /// ⚠ 计数（`catCount`）来自 `TallyTypes` 那一趟**独立统计**，
        ///   与"当前选没选、搜没搜"无关 —— 见 TallyTypes 的注释（坑 #63-②）。
        /// </summary>
        private void PushFilterOptions()
        {
            for (int i = 0; i < catUsed; i++)
            {
                catLabel[i] = TypeLabel(catType[i]);
            }
            filterBar.SetOptions(catType, catLabel, catCount, catUsed);
        }

        /// <summary>
        /// 这个配方是否匹配搜索词 —— ★ 匹配面**同时含产物与材料名**。
        ///
        /// 为什么材料名必须进匹配面：玩家找配方时想的经常是"我有金属板，能做什么"，
        /// 打「金属板」应当把**用金属板做的**配方都列出来，而不是只列出"名字里带金属板"的产物。
        /// 这里本来就在遍历 `bp.supplies`（`Evaluate` 里算材料可用量也要走一遍），
        /// 所以放在这一侧比在物品侧硬拼自然。
        ///
        /// ⚠ 匹配用共享层那一份 `MatchesQuery`（中文名 / 英文名 / 拼音，**前缀单调**，见 §4.11 末）
        ///   —— 蓝图侧自己再写一套匹配规则就会与物品搜索"手感不一样"。
        /// ⚠ 描述**故意不进**匹配面：`ItemAsset.BuildDescription` 是 UI 级组装（要 builder、
        ///   要物品实例、会拼耐久/配件/分类一堆行），塞进这条路不划算（理由也在 `Shared.cs` 缓存处）。
        /// </summary>
        private static bool BlueprintMatchesQuery(Blueprint bp, string query)
        {
            if (bp == null) return false;
            if (string.IsNullOrEmpty(query)) return true;

            // ① 产物（玩家多数时候是按"想做什么"找）
            //    ⚠ 产物在 `outputs` 上，**没有** `resultItem` 这个字段（我第一版就写错了，
            //      编译前先回 SDK 核了 `Blueprint` 的 public 成员）。
            BlueprintOutput[] outputs = bp.outputs;
            if (outputs != null)
            {
                for (int i = 0; i < outputs.Length; i++)
                {
                    if (outputs[i] != null && StorageUiShared.MatchesQuery(outputs[i].FindItemAsset(), query))
                        return true;
                }
            }

            // ② 材料（"我有它，能做什么"）—— 引擎把目标物品单独存成 TargetItem，
            //    它是**一个 BlueprintSupply**（不是 ItemAsset），与 supplies 同一套匹配规则。
            if (bp.TargetItem != null && StorageUiShared.MatchesQuery(bp.TargetItem.FindItemAsset(), query))
                return true;

            BlueprintSupply[] supplies = bp.supplies;
            if (supplies != null)
            {
                for (int i = 0; i < supplies.Length; i++)
                {
                    BlueprintSupply sup = supplies[i];
                    if (sup == null) continue;
                    if (StorageUiShared.MatchesQuery(sup.FindItemAsset(), query)) return true;
                }
            }
            return false;
        }

        // ================= 面板 =================

        private void Build()
        {
            // 记下这块面板是按哪种摆法建的 —— 下次 Open 时摆法变了要作废
            builtAsGrid = AsGrid;

            panel = Glazier.Get().CreateBox();
            panel.PositionOffset_X = 0f;
            panel.PositionOffset_Y = 0f;
            panel.SizeScale_X = 1f;
            panel.SizeOffset_X = -20f;   // 留出外层滚动条宽度
            panel.SizeOffset_Y = HEAD_HEIGHT;
            panel.BackgroundColor = new SleekColor(ESleekTint.BACKGROUND, 0.9f);

            headLabel = Glazier.Get().CreateLabel();
            headLabel.PositionOffset_X = 8f;
            headLabel.PositionOffset_Y = 4f;
            headLabel.SizeScale_X = 1f;
            headLabel.SizeOffset_X = -16f;
            headLabel.SizeOffset_Y = 26f;
            headLabel.FontSize = ESleekFontSize.Small;
            headLabel.TextAlignment = TextAnchor.MiddleLeft;
            headLabel.TextColor = new SleekColor(ESleekTint.FONT);
            headLabel.Text = "蓝图";
            panel.AddChild(headLabel);

            // ★ 26.10.3.34 / T6：搜索框 —— 占**一行**，在头部之下、列表之上。
            //   ★ 26.10.3.46：控件本体交给**公开可复用组件** `StorageSearchBox`
            //   （原来这段 `CreateStringField()` 与两个面板**逐字相同**）。
            //   ⚠ 旋钮**显式设一遍**，不靠组件默认值。
            searchBox.Show = true;                  // 本视图这一行**恒有**（T6 起就是这样）
            searchBox.Height = SEARCH_H;
            searchBox.TopOffset = HEAD_HEIGHT;
            searchBox.Placeholder = "搜索配方（产物或材料名 / 拼音）";
            searchBox.OnQueryChanged = OnSearchTextChanged;
            searchBox.Build(panel);

            // ★ 26.10.3.43：分类过滤按钮条交给**公开可复用组件** `StorageFilterBar`（README §4.24）。
            //   以前这段"固定建 9 个按钮"的代码长在本文件里 ⟹ 第三方能筛数据、却给不了用户
            //   一个筛选按钮。现在组件自己管按钮池与刷新，且把"回调只认下标、内容现读"
            //   那条纪律收进了它内部（见 StorageFilterBar.OnButtonClicked）。
            //   ⚠ 旋钮**显式设一遍**，不靠组件默认值 —— 免得以后有人改默认值悄悄改掉本视图观感。
            filterBar.Columns = FILTER_COLS;
            filterBar.MaxButtons = FILTER_MAX;
            filterBar.ButtonHeight = FILTER_BTN_H;
            filterBar.Gap = FILTER_GAP;
            filterBar.MinOptionsToShow = 2;      // 只有一类时不显示（组件默认也是 2，写清意图）
            filterBar.ItemNoun = "个配方";        // 提示语保持原样：「只看「枪」（3 个配方）」
            filterBar.OnSelectionChanged = OnFilterChanged;
            filterBar.Build(panel);
            // ⚠ 纵向位置**每轮由 UpdatePanelLayout 重算**（它同时算面板高度与滚动区起点，
            //   那个表达式只有一处）；这里先给个初值，免得建完到第一次重建之间闪一帧。
            if (filterBar.Element != null)
            {
                filterBar.Element.PositionOffset_Y = HEAD_HEIGHT + SearchRowHeight();
            }

            scroll = Glazier.Get().CreateScrollView();
            scroll.PositionOffset_X = 4f;
            // ⚠ 高度只有 SearchRowHeight() **一个来源**（这一行与面板高度两处共用它）
            scroll.PositionOffset_Y = HEAD_HEIGHT + SearchRowHeight();
            scroll.SizeScale_X = 1f;
            scroll.SizeOffset_X = -8f;
            scroll.SizeOffset_Y = 0f;        // 每轮按「内容高 vs 上限」重算

            // ⚠⚠ 这两行缺一不可（README 坑 #18）：
            //   缺 ScaleContentToWidth → 内容宽为 0 → 行宽 0 → 文字不渲染
            //   （图标是固定尺寸仍可见，「只剩图标」就是它的识别特征）；
            //   缺 HandleScrollWheel → 鼠标滚轮无效，只能拖滚动条。
            scroll.ScaleContentToWidth = true;
            scroll.HandleScrollWheel = true;

            panel.AddChild(scroll);
        }

        // ================= 数据模型 =================

        /// <summary>一个材料的可用情况（一次重建内算出，供文本与状态判定复用）。</summary>
        private class BlueprintSupplyStat
        {
            public ItemAsset asset;
            public int need;
            public int carry;     // 身上（背包页；工具类另含装备格）
            public int box;       // 箱内
            public int usable;    // 此刻按引擎语义真能用到的
            public bool isTool;   // ShouldConsume == false（工具，不消耗）
            public bool isTarget; // 蓝图的目标物品（装弹/修理的对象）
        }

        /// <summary>一行 = 一个配方。</summary>
        private class BlueprintEntry
        {
            public Blueprint blueprint;
            public int Key;             // 蓝图身份（跨重建稳定，决定行复用）
            public int TextFp;          // 文本指纹（材料数字变了就要重刷文字）
            public ItemAsset output;    // 产物（图标 + 名字）
            public int outputAmount;
            public int rank;
            public bool craftable;      // 能不能直接「扣箱内材料合成」（点击时还会再权威校验一次）
            public string title;
            public string productName;   // 格子摆法用：只有产物名（不含状态）
            public string statusShort;   // 格子摆法用：短状态（与 title 里的状态同源）
            public string detail;
            public string tooltip;
        }

        /// <summary>
        /// 「某个资产 → 该资产的所有 Item 实例」索引。
        ///
        /// 一次重建要对几十个配方、每个配方几个材料问「有多少」，
        /// 逐个材料全容器扫是 O(配方×材料×容器)；建索引后是 O(容器 + 命中数)。
        /// 键用 ItemAsset **实例引用** —— 与引擎自己的
        /// GatherUniqueItems(HashSet&lt;ItemAsset&gt;) / ContainsAnyOfItems 同一套口径。
        /// </summary>
        private class JarIndex
        {
            private readonly Dictionary<ItemAsset, List<Item>> map =
                new Dictionary<ItemAsset, List<Item>>();

            private static readonly List<Item> EMPTY = new List<Item>();

            public void Rebuild(Items container)
            {
                map.Clear();
                if (container == null) return;

                byte count = container.getItemCount();
                for (byte i = 0; i < count; i++)
                {
                    ItemJar jar = container.getItem(i);
                    if (jar == null || jar.item == null) continue;
                    Add(jar.GetAsset(), jar.item);
                }
            }

            /// <summary>把背包的若干页建进索引（页号区间闭开：[fromPage, toPageExclusive)）。</summary>
            public void RebuildPages(PlayerInventory inventory, int fromPage, int toPageExclusive)
            {
                map.Clear();
                if (inventory == null || inventory.items == null) return;

                for (int page = fromPage; page < toPageExclusive; page++)
                {
                    if (page < 0 || page >= inventory.items.Length) continue;
                    Items items = inventory.items[page];
                    if (items == null) continue;

                    byte count = items.getItemCount();
                    for (byte i = 0; i < count; i++)
                    {
                        ItemJar jar = items.getItem(i);
                        if (jar == null || jar.item == null) continue;
                        Add(jar.GetAsset(), jar.item);
                    }
                }
            }

            private void Add(ItemAsset asset, Item item)
            {
                if (asset == null) return;
                List<Item> list;
                if (!map.TryGetValue(asset, out list))
                {
                    list = new List<Item>(2);
                    map[asset] = list;
                }
                list.Add(item);
            }

            public List<Item> Find(ItemAsset asset)
            {
                if (asset == null) return EMPTY;
                List<Item> list;
                return map.TryGetValue(asset, out list) ? list : EMPTY;
            }

            public void CollectAssets(HashSet<ItemAsset> into)
            {
                foreach (KeyValuePair<ItemAsset, List<Item>> kv in map)
                {
                    into.Add(kv.Key);
                }
            }
        }

        /// <summary>
        /// 按引擎语义统计某个材料「有多少」。
        ///
        /// 逐条对照 <c>Items.SearchContents</c>（它是 public，但参数结构体用 `in`
        /// 传，本项目用 .NET Framework 的 csc 编译，所以把语义抄在这里而不调它）：
        ///   · amount &lt; 1 且 !ShouldIncludeEmptyAmount → 不算（空容器）
        ///   · quality &gt;= 100 且 !ShouldIncludeMaxQuality → 不算
        ///   · ShouldExcludeFullAmount 且 amount &gt;= MaxAmount → 不算
        ///   · CountingMethod = TotalItems → 数**件数**；TotalAmount → 累加 **amount**
        ///     （ShouldCountEmptyAsOne 时空容器按 1 计）
        /// 这四条判据全都是 BlueprintSupply 自带的 public 属性，不需要反射。
        /// </summary>
        private static int CountSupply(BlueprintSupply sup, List<Item> list)
        {
            if (sup == null || list == null || list.Count == 0) return 0;

            ItemAsset asset = sup.FindItemAsset();
            if (asset == null) return 0;

            int maxAmount = asset.MaxAmount;
            bool byItems = sup.CountingMethod == ECraftingInputCountingMethod.TotalItems;
            int total = 0;

            for (int i = 0; i < list.Count; i++)
            {
                Item it = list[i];
                if (it == null) continue;

                if (it.amount < 1 && !sup.ShouldIncludeEmptyAmount) continue;
                if (it.quality >= 100 && !sup.ShouldIncludeMaxQuality) continue;
                if (sup.ShouldExcludeFullAmount && it.amount >= maxAmount) continue;

                if (byItems)
                {
                    total += 1;
                }
                else
                {
                    total += sup.ShouldCountEmptyAsOne ? Mathf.Max(1, it.amount) : it.amount;
                }
            }
            return total;
        }

        // ================= 重建 =================

        private void RebuildRows(StorageUiContext c)
        {
            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null) return;

            // ① 三本索引：箱子 / 背包页(2..6) / 装备格(0..1)
            //    装备格只有工具类材料才算（与引擎 IncludeEquipmentSlots = !ShouldConsume 一致）
            // ★ 借道的临时页**根本不建箱索引**，而不是"建了再在 usable 上打折"。
            //   因为 `s.box` 不只喂给 usable，它还喂给：明细里的「箱内几」、候选粗筛
            //   （boxIndex.CollectAssets）、以及 `allPotential` → RANK_FROM_BOX。
            //   只在 usable 上打折的结果（用户 2026-10-04 实测）：锯子要 2 块金属板，
            //   身上 1 块 + 从数字箱托起 1 块（那件复制品此刻就在临时页里）
            //   ⟹ 界面显示「背包1 · 箱1」并**给了合成按钮**（FROM_BOX 也算可点），
            //   点下去执行时那一页不算箱 ⟹ 只有 1 块 ⟹ 合不成。
            //   ⟹ **口径必须在数据源头统一，不能每个消费者各自判一遍。**
            boxIndex.Rebuild(StorageCrafting.BoxCounts(c) ? c.Container : null);
            packIndex.RebuildPages(player.inventory, PlayerInventory.SLOTS, PlayerInventory.STORAGE);
            equipIndex.RebuildPages(player.inventory, 0, PlayerInventory.SLOTS);

            // 原版刷新配方列表时同样先调它（PlayerDashboardCraftingUI.cs:569）
            try { player.crafting.UpdateAvailableCraftingTags(); } catch (Exception) { }

            CollectEntries(player);

            // ② 头部汇总（箱子名由上方那条原版标题栏承担，这里不重复）
            int craftable = 0, fromBox = 0, gated = 0, missing = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                switch (entries[i].rank)
                {
                    case RANK_CRAFTABLE: craftable++; break;
                    case RANK_FROM_BOX: fromBox++; break;
                    case RANK_GATE: gated++; break;
                    default: missing++; break;
                }
            }
            string head = "蓝图 " + entries.Count + " 项 · 可合成 " + craftable +
                " · 取出即可 " + fromBox + " · 需设备 " + gated + " · 缺材料 " + missing;
            // ★ T6/T7：**被筛过就要说清楚** —— 否则"项数变少"看起来像配方没了。
            //   两个数出自同一次遍历（M 是在过滤那一步之前数的），不另算一遍。
            if (!string.IsNullOrEmpty(searchBox.Query) || filterOn)
            {
                string why = "";
                if (!string.IsNullOrEmpty(searchBox.Query)) why = "筛选中";
                if (filterOn)
                {
                    why = string.IsNullOrEmpty(why)
                        ? ("只看「" + TypeLabel((int)filterType) + "」")
                        : (why + " + 只看「" + TypeLabel((int)filterType) + "」");
                }
                head = head + "（" + why + "：显示 " + entries.Count + " / 共 " + preSearchEntries + " 项）";
            }
            headLabel.Text = head;

            // ★ 26.10.3.46：不再需要"每帧把 SearchQuery 同步到输入框"——
            //   组件在 Query 的 setter 里当场同步（见 StorageSearchBox）。

            // ★ T7 / 26.10.3.43：把本轮直方图交给按钮条组件（文字/计数/选中态/可见性/高度归它管）。
            //   ⚠ 高度也要在这一趟之前定好 —— 组件的高度是 UpdatePanelLayout 读的输入。
            PushFilterOptions();

            // ★ 口径说明放在**头部一行**，不塞进每一行的"为什么不能合"里：
            //   借道的临时页不算箱子 ⟹ 整块的可合成/缺材料都只按身上算。
            //   不说，玩家会看着"清单里明明有"却被算成缺材料，以为界面坏了
            //   （"界面显示的可用量必须等于点这个界面能做到的量"那条铁律）。
            if (ctx != null && !StorageCrafting.BoxCounts(ctx))
            {
                headLabel.Text = "临时中转页不算箱子 · 材料只按身上算 ⟵ " + headLabel.Text;
            }

            // 上一次点「合成」的结果（成功/失败都显示几秒）——
            // 破坏性操作必须把结果说出口，不能只写进日志让玩家去翻文件。
            if (!string.IsNullOrEmpty(lastAction) &&
                Time.unscaledTime - lastActionTime < ACTION_SHOW_SECONDS)
            {
                headLabel.Text = headLabel.Text + " ⟵ " + lastAction;
            }

            // ③ 行复用：**按配方身份认领**（与另两个视图同一条铁律：按索引比对会让
            //    数组前移看起来像「所有行都变了」→ 图标全量重载）。
            //    本视图还有个额外好处：图标只由「哪个配方」决定，
            //    材料数字变化只改文字 → **永远不碰图标**。
            for (int i = 0; i < rows.Count; i++) rows[i].InUse = false;

            int reusedRows = 0;
            int rebuiltRows = 0;
            int textRows = 0;

            for (int i = 0; i < entries.Count; i++)
            {
                BlueprintEntry e = entries[i];

                BlueprintRow row = ClaimByKey(e.Key);
                if (row != null)
                {
                    reusedRows++;
                }
                else
                {
                    row = ClaimAnyFreeRow();
                    if (row == null) row = NewClaimedRow();   // ⚠ 必须走这个入口（建立即认领）
                    RebuildRow(row, e);   // 整行重建（Button + 产物图标 + 两个文字容器）
                    rebuiltRows++;
                }

                if (row.TextFp != e.TextFp)
                {
                    FillRowText(row, e);  // 只改文字与 Tooltip
                    textRows++;
                }

                // 摆位交给 PlaceItem：列表一行一格、格子一行四格（相对宽度）
                PlaceItem(row, i);
                row.Button.IsVisible = true;

                // ★ 26.10.3.51：填充色每轮重算（颜色没变时组件内部一个 int 比较就早退）
                ApplyCellFill(row, e);
            }

            // ⑤ 多余的闲置行隐藏留作池子，**不销毁**
            for (int i = 0; i < rows.Count; i++)
            {
                if (!rows[i].InUse && rows[i].Button != null)
                {
                    rows[i].Button.IsVisible = false;
                }
            }

            float y = ContentHeight(entries.Count);

            // 空态提示 —— 否则用户看到「一块空面板」会以为界面坏了
            if (entries.Count == 0)
            {
                if (emptyLabel == null)
                {
                    emptyLabel = Glazier.Get().CreateLabel();
                    emptyLabel.PositionOffset_X = 8f;
                    emptyLabel.SizeScale_X = 1f;
                    emptyLabel.SizeOffset_X = -16f;
                    emptyLabel.SizeOffset_Y = 24f;
                    emptyLabel.FontSize = ESleekFontSize.Small;
                    emptyLabel.TextAlignment = TextAnchor.MiddleLeft;
                    emptyLabel.TextColor = new SleekColor(Color.gray);
                    scroll.AddChild(emptyLabel);   // 与行同级，挂在滚动内容里
                }
                // 措辞要跟着口径走：借道期间根本没有"箱内"这一份，
                // 说"箱内与身上都没有"会让人去翻箱子（而数字箱清单里可能真有）。
                emptyLabel.Text = StorageCrafting.BoxCounts(ctx)
                    ? "（箱内与身上都没有可作为合成材料的物品）"
                    : "（身上没有可作为合成材料的物品 —— 这里开的是临时中转页，不算箱子）";
                emptyLabel.PositionOffset_Y = 4f;
                emptyLabel.IsVisible = true;
            }
            else if (emptyLabel != null)
            {
                emptyLabel.IsVisible = false;
            }

            // 高度策略：内容高 vs 上限，超出则列表**内部**滚动（坑 #16）。
            // 区块模式下上限更小 —— 右侧还要留给「附近物品」，不能把整列撑满。
            scroll.ContentSizeOffset = new Vector2(0f, y);
            float cap = AsSection ? Mathf.Clamp(SectionMaxListHeight, 90f, MAX_LIST_HEIGHT)
                                  : MAX_LIST_HEIGHT;
            float listHeight = Mathf.Min(y, cap);
            scroll.SizeOffset_Y = listHeight;
            // ★ T7：分类按钮那一块也计入高度，滚动区起点跟着它下移。
            //   ⚠ 「搜索行 + 分类行」的总高度只有这一处算（滚动区 Y 与面板高共用它）——
            //     分头写迟早漏改一处，两处对不上就是"列表被按钮压住"或"底部留白"。
            float headExtra = SearchRowHeight() + filterBar.DesiredHeight;
            // ★ 26.10.3.43：按钮条纵向位置也在这里给 —— 「搜索行 + 分类行」这个表达式
            //   只有本方法一处（面板高度 / 滚动区起点 / 按钮条位置共用它），
            //   分头写迟早漏改一处（漏了就是"列表被按钮压住"或"按钮飘在搜索框上"）。
            if (filterBar.Element != null)
            {
                filterBar.Element.PositionOffset_Y = HEAD_HEIGHT + SearchRowHeight();
            }
            scroll.PositionOffset_Y = HEAD_HEIGHT + headExtra;
            panel.SizeOffset_Y = HEAD_HEIGHT + headExtra + listHeight + 6f;

            // 诊断用 —— **只在画面真的变了时才记**：本视图有低频轮询，无条件刷会把
            // LogOutput.log 灌满（开着箱子不动也每半秒一行）。
            //   · 「新建」长期等于「总项数」→ 身份认领没生效（图标全量重载）
            //   · 「滚动容器控件数」应 >= 项数（可能多 1 个空态提示）；
            //     少于项数说明有行的控件没挂进容器 —— 「N 项只显示 M 行」的确诊特征
            //   ★ 26.10.3.33：还要看**可见行数** —— 控件挂进容器了却 `IsVisible = false`
            //     是**同一种病**（用户 2026-10-05 报的那条正是这种：控件在树里、就是没显示）
            //     ⟹ 控件数 ≥ 项数**不代表**画面正常。
            int signature = VisibleSignature(reusedRows, rebuiltRows, textRows);
            if (signature != lastLoggedSignature)
            {
                lastLoggedSignature = signature;
                int visibleRows = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i].Button != null && rows[i].Button.IsVisible) visibleRows++;
                }
                CustomUIPlugin.Log("蓝图刷新：复用 " + reusedRows + " 行 / 新建 " + rebuiltRows +
                    " 行 / 改文字 " + textRows + " 行（共 " + entries.Count + " 项，可见 " +
                    visibleRows + " 行，候选 " + evaluatedCandidates + "，滚动容器控件 " +
                    scroll.GetChildCount() + " 个）");
                if (visibleRows != entries.Count)
                {
                    CustomUIPlugin.Warn("蓝图：可见行数 " + visibleRows + " ≠ 显示项数 " + entries.Count +
                        " —— 有行没显示出来（行池认领漏了？）。这就是「N 项只显示 M 行」的确诊特征。");
                }
            }
        }

        /// <summary>单个单元的高度（列表一行 / 格子一格）。</summary>
        private static float ItemHeight { get { return AsGrid ? CELL_H : ROW_HEIGHT; } }

        /// <summary>
        /// 把第 index 项摆到位。两种摆法**只有这里与 RebuildRow/FillRowText 不同**，
        /// 行池、身份认领、文字刷新全部共用 —— 复制一份池子逻辑必然漂移。
        /// ⚠ 格子横向定位用相对值（PositionScale_X / SizeScale_X），不能用像素：
        ///   面板宽度依赖原版右列、挂载前是 0（同 GridView 那条铁律）。
        /// </summary>
        private void PlaceItem(BlueprintRow r, int index)
        {
            if (r == null || r.Button == null) return;

            if (AsGrid)
            {
                int col = index % GRID_COLUMNS;
                int line = index / GRID_COLUMNS;

                r.Button.PositionScale_X = col * CELL_SCALE_X;
                r.Button.PositionOffset_X = 1f;              // 单元格之间的细缝
                r.Button.SizeScale_X = CELL_SCALE_X;
                r.Button.SizeOffset_X = -2f;
                r.Button.PositionOffset_Y = line * (CELL_H + ROW_GAP);
            }
            else
            {
                r.Button.PositionScale_X = 0f;
                r.Button.PositionOffset_X = 0f;
                r.Button.SizeScale_X = 1f;
                r.Button.SizeOffset_X = -18f;                // 留出滚动条宽度
                r.Button.PositionOffset_Y = index * (ROW_HEIGHT + ROW_GAP);
            }

            r.Button.SizeOffset_Y = ItemHeight;
        }

        /// <summary>内容总高（格子按行折算；空表给一个提示的高度）。</summary>
        private static float ContentHeight(int count)
        {
            if (count <= 0) return 32f;
            if (AsGrid)
            {
                int lines = (count + GRID_COLUMNS - 1) / GRID_COLUMNS;
                return lines * (CELL_H + ROW_GAP);
            }
            return count * (ROW_HEIGHT + ROW_GAP);
        }

        /// <summary>
        /// 本次画面的签名：行计数 + 每一项的身份与文字指纹。
        /// 用它做「变了才记日志」的判据（轮询到点但内容没变时不该产生任何日志）。
        /// </summary>
        private int VisibleSignature(int reused, int rebuilt, int texted)
        {
            unchecked
            {
                int h = 13;
                h = h * 397 ^ reused;
                h = h * 397 ^ rebuilt;
                h = h * 397 ^ texted;
                h = h * 397 ^ evaluatedCandidates;
                for (int i = 0; i < entries.Count; i++)
                {
                    h = h * 397 ^ entries[i].Key;
                    h = h * 397 ^ entries[i].TextFp;
                }
                return h;
            }
        }

        /// <summary>
        /// 收集本次要显示的配方：先按「箱内至少有一个它的材料」粗筛出候选
        /// （判据用引擎自己的 public <c>Blueprint.ContainsAnyOfItems</c>，
        /// 与原版背包页同一做法），再逐个算材料可用量并定档。
        /// </summary>
        private void CollectEntries(Player player)
        {
            entries.Clear();
            evaluatedCandidates = 0;
            preSearchEntries = 0;
            // ★ T7：类型直方图每轮清零重算（它是"按钮该显示哪几类"的唯一来源）
            Array.Clear(typeHist, 0, typeHist.Length);
            catUsed = 0;

            int maxEntries = (CustomUIPlugin.cfgBlueprintMax != null)
                ? Mathf.Clamp(CustomUIPlugin.cfgBlueprintMax.Value, 10, 500) : 150;
            bool showMissing = (CustomUIPlugin.cfgBlueprintShowMissing == null)
                || CustomUIPlugin.cfgBlueprintShowMissing.Value;

            // 粗筛用的资产集合 = **箱内 ∪ 背包 ∪ 装备格**。
            // ⚠ 以前只取箱内，于是「材料全在身上、箱子里一样没有」的配方**整条不出现**
            //   （用户实测：背包里有金属碎片却看不到对应配方）。
            //   既然本视图现在能扣箱内也能扣身上，候选集就必须覆盖两个来源。
            materialAssets.Clear();
            boxIndex.CollectAssets(materialAssets);
            packIndex.CollectAssets(materialAssets);
            equipIndex.CollectAssets(materialAssets);
            if (materialAssets.Count == 0) return;

            List<Blueprint> all = cachedBlueprints;
            if (all == null) return;

            // ★★ 26.10.3.36（用户实测报的 bug）：**类型直方图单独走一趟**。
            //
            //   原先它长在下面那个候选循环里，于是被两件与"有哪些类型"无关的事影响：
            //     ① `if (evaluatedCandidates >= MAX_CANDIDATES) break;` —— 这个 break 一旦
            //        触发，后面的蓝图根本没被统计；而 `evaluatedCandidates` 只在**通过过滤之后**
            //        才 ++，所以**选中某一类之后循环会跑得更远、统计到更多蓝图**
            //        ⟹ 前 6 名重排 ⟹ 按钮内容变了。
            //        用户实测症状：「点了 Gun 之后那个按钮变成了 Skirt」。
            //     ② 搜索词同样会改变循环走到哪儿。
            //   ⟹ 直方图的条件只有"粗筛 + 插件过滤"（与分类/搜索/候选上限**全都无关**），
            //     按钮栏才是稳定的 —— 这也是"选中项不会突然消失"的前提。
            TallyTypes(all);

            for (int i = 0; i < all.Count; i++)
            {
                Blueprint bp = all[i];
                if (bp == null) continue;

                // 粗筛：这个配方有任何一个材料在场（箱里或身上）才值得细算
                if (!bp.ContainsAnyOfItems(materialAssets)) continue;

                // 显示范围限定（配置 + 其他插件挂的判据）。放在粗筛之后、细算之前：
                // 被滤掉的配方既不占候选额度，也不进 Evaluate（那是最贵的一步）。
                if (!StorageBlueprintFilters.Passes(bp, ctx)) continue;

                // ★ 26.10.3.35 / T7：按类型过滤。⚠ 直方图**不在这里**统计（见上面 TallyTypes 的注释：
                //   长在这个循环里会被候选上限的 break 截断 ⟹ 按钮栏会随选中/打字而变）。
                int tp = TypeOfBlueprint(bp);
                if (filterOn && tp != (int)filterType) continue;

                // ★ 26.10.3.34 / T6：搜索过滤同样放在**细算之前** ——
                //   不是"算完再把行藏几行"（那会白算一大堆，而且头部"共 M 项"会与画面不符）。
                //   匹配面**含配方材料名**（"我有金属板，能做什么"），见 BlueprintMatchesQuery。
                //   ⚠ `preSearchEntries` 只在这里数：它是「共 M 项」的那个 M，
                //     与「显示 N 项」出自同一次遍历，不另算一遍（"同一个数算两遍"那一族）。
                preSearchEntries++;
                if (!BlueprintMatchesQuery(bp, searchBox.Query)) continue;

                if (evaluatedCandidates >= MAX_CANDIDATES)
                {
                    CustomUIPlugin.Log("蓝图候选超过 " + MAX_CANDIDATES + " 条，本次截断");
                    break;
                }
                evaluatedCandidates++;

                if (IsHidden(bp, player)) continue;

                BlueprintEntry e = Evaluate(bp, player, showMissing);
                if (e != null) entries.Add(e);
            }

            // ★ T7：候选遍历完才能知道"哪几类最多" ⟹ 按钮栏在这里定内容
            BuildTopTypes();

            entries.Sort(CompareEntry);

            if (entries.Count > maxEntries)
            {
                int dropped = entries.Count - maxEntries;
                entries.RemoveRange(maxEntries, dropped);
                CustomUIPlugin.Log("蓝图列表截断 " + dropped + " 项（上限 " + maxEntries +
                    "，改 [Blueprint] MaxEntries）");
            }
        }

        /// <summary>
        /// 统计"当前候选里各类物品各有多少个配方"—— **条件只有粗筛 + 插件过滤**。
        ///
        /// ★ 26.10.3.36：单独一趟，**故意不并进候选循环**。并入之后它会被
        ///   `MAX_CANDIDATES` 的 `break` 与搜索词影响（选中某类 ⟹ 循环跑得更远 ⟹ 统计得更多
        ///   ⟹ 前 6 名重排 ⟹ 按钮内容变了）。用户实测：「点了 Gun，那个按钮变成 Skirt」。
        ///   代价是与主循环重复一遍粗筛（`ContainsAnyOfItems` + 插件判据），但只在重建时跑，
        ///   换来的是一条**与筛选状态无关**的稳定按钮栏（"选中项不会突然消失"的前提）。
        /// </summary>
        private void TallyTypes(List<Blueprint> all)
        {
            for (int i = 0; i < all.Count; i++)
            {
                Blueprint bp = all[i];
                if (bp == null) continue;
                if (!bp.ContainsAnyOfItems(materialAssets)) continue;
                if (!StorageBlueprintFilters.Passes(bp, ctx)) continue;

                int tp = TypeOfBlueprint(bp);
                if (tp >= 0 && tp < TYPE_SLOTS) typeHist[tp]++;
            }
        }

        /// <summary>
        /// 从直方图里挑前 <see cref="FILTER_MAX"/> 个类型当按钮（次数降序；同次数按枚举序 ⟹ **稳定**：
        /// 别的类型多一个不会让按钮换位置）。顺手把"选中类型已消失"的筛选取掉 ——
        /// 否则会出现"正筛着某一类、按钮栏里却没有它"（玩家看不见自己选了什么）。
        /// </summary>
        private void BuildTopTypes()
        {
            catUsed = 0;
            bool selPresent = filterOn
                && ((int)filterType) >= 0 && ((int)filterType) < TYPE_SLOTS
                && typeHist[(int)filterType] > 0;

            // ⚠ 用预分配的 scratch，不在重建路径里 new（本视图每轮都跑，垃圾会攒起来）
            Array.Copy(typeHist, typeScratch, TYPE_SLOTS);

            for (int k = 0; k < FILTER_MAX; k++)
            {
                int best = -1;
                for (int t = 0; t < TYPE_SLOTS; t++)
                {
                    if (typeScratch[t] <= 0) continue;
                    if (best < 0 || typeScratch[t] > typeScratch[best]) best = t;
                }
                if (best < 0) break;

                catType[catUsed] = best;
                catCount[catUsed] = typeScratch[best];
                catUsed++;
                typeScratch[best] = 0;   // 取走，避免同一个类型占两个按钮
            }

            if (filterOn && !selPresent)
            {
                // 选中那一类整个从候选里消失了（东西被拿走了）⟹ 清掉筛选，别留个空筛
                filterOn = false;
                return;
            }

            // ★ 26.10.3.36：选中那一类**必须留在按钮栏里**（哪怕它的计数掉出前 N）。
            //   否则玩家会看到"正筛着某一类、按钮栏里却没有它" —— 看不见自己在筛什么。
            //   ⚠ 只**补位**、不挪到第一个：把它挪来挪去同样是"按钮乱跳"，
            //     而正常情况（刚点的那个本来就在前 N 里）顺序完全不动。
            if (filterOn && (int)filterType >= 0 && (int)filterType < TYPE_SLOTS)
            {
                int sel = (int)filterType;
                bool shown = false;
                for (int i = 0; i < catUsed; i++)
                {
                    if (catType[i] == sel) { shown = true; break; }
                }
                if (!shown)
                {
                    int slot = (catUsed < FILTER_MAX) ? catUsed : (FILTER_MAX - 1);
                    catType[slot] = sel;
                    catCount[slot] = typeHist[sel];
                    if (catUsed < FILTER_MAX) catUsed++;
                }
            }

            // 还有类别挤不进按钮栏 ⟹ 那些类别**没有入口**能筛到它们（只能用搜索）。
            // ⚠ 只记一次：重建会随轮询反复跑，无条件刷就是灌日志。
            if (!filterTruncWarned)
            {
                int present = 0;
                for (int t = 0; t < TYPE_SLOTS; t++) if (typeHist[t] > 0) present++;
                if (present > catUsed)
                {
                    filterTruncWarned = true;
                    CustomUIPlugin.Warn("蓝图分类按钮只列了配方最多的 " + catUsed + " 类，另有 " +
                        (present - catUsed) + " 类没进按钮栏（只能用搜索筛到它们）");
                }
            }
        }

        private bool filterTruncWarned;

        /// <summary>
        /// 「永久不可用」的配方不列。
        ///
        /// 引擎的判据是 internal（IsBlueprintPermanentlyDisabled），这里只**安全地**
        /// 复刻其中用 public API 就能查的两条（黑名单、指定地图）。
        /// 其余（freeform 开关、静态标签）不查 —— 少滤几条只是多显示几行，
        /// 而反射 internal 一旦游戏改名就是静默失效。
        /// </summary>
        private static bool IsHidden(Blueprint bp, Player player)
        {
            try
            {
                if (player.crafting != null && player.crafting.isBlueprintBlacklisted(bp)) return true;

                if (!string.IsNullOrEmpty(bp.map) &&
                    (Level.info == null ||
                     !bp.map.Equals(Level.info.name, StringComparison.InvariantCultureIgnoreCase)))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 过滤判据出问题不该让整张列表消失
            }
            return false;
        }

        /// <summary>
        /// 算一个配方：逐材料的 carry/box/usable → 定档 → 生成三段文本。
        /// 返回 null 表示这个配方没有可显示的对象（产物资产取不到）。
        /// </summary>
        private BlueprintEntry Evaluate(Blueprint bp, Player player, bool showMissing)
        {
            statBuffer.Clear();

            // 这一行「箱内的算不算能用」—— 必须与「能不能出现合成按钮」同一套判据，
            // 否则会出现「数量明明够却没有按钮」或「有按钮但显示不够」（用户实测撞上过）。
            // ★ 借道的纯中转页**不算箱**（`StorageCrafting.BoxCounts`）：那一页里可能只有
            //   玩家正托着的那件复制品。以前这里只看"容器是不是 null"，
            //   于是借道界面里整块蓝图按一个<b>几乎永远空</b>的箱子算可用量 ——
            //   满屏"缺材料"，而真实来源（插件自己的清单）压根不在搜索范围内。
            //   那是"界面显示的可用量必须等于点这个界面能做到的量"的反例。
            bool boxCounts = AllowCraftFromBox && StorageCrafting.IsSupported(bp) &&
                StorageCrafting.BoxCounts(ctx);

            // 目标物品（装弹/修理的对象）也当一条材料参与判定：
            // 引擎把它单独存成 Blueprint.TargetItem，但搜索规则与材料同一套。
            if (bp.TargetItem != null)
            {
                BlueprintSupplyStat t = AddStat(bp.TargetItem, player, boxCounts);
                if (t != null) t.isTarget = true;
            }

            BlueprintSupply[] supplies = bp.supplies;
            if (supplies != null)
            {
                for (int i = 0; i < supplies.Length; i++)
                {
                    AddStat(supplies[i], player, boxCounts);
                }
            }

            bool allUsable = true;
            bool allPotential = true;
            bool usedBox = false;   // 这一行有没有真的靠箱子里的东西凑够（影响文案）

            sb.Length = 0;
            for (int i = 0; i < statBuffer.Count; i++)
            {
                BlueprintSupplyStat s = statBuffer[i];
                if (s.usable < s.need) allUsable = false;
                if (s.carry + s.box < s.need) allPotential = false;
                if (!s.isTool && s.usable > s.carry) usedBox = true;

                if (i > 0) sb.Append(" · ");
                if (s.isTarget) sb.Append("目标 ");

                sb.Append(Name(s.asset));
                sb.Append(' ');
                sb.Append(s.usable);
                sb.Append('/');
                sb.Append(s.need);

                if (s.isTool) sb.Append("(工具)");

                // 材料来源一律标出来。旧写法只在「不够」时才标，于是
                // 「钉子 2/1」看不出这 2 个是在箱子里还是身上 —— 用户实测据此
                // 以为数量没变化（其实已经扣过了）。
                if (!s.isTool && s.box > 0)
                {
                    sb.Append("(箱");
                    sb.Append(s.box);
                    if (s.carry > 0)
                    {
                        sb.Append("·身");
                        sb.Append(s.carry);
                    }
                    sb.Append(')');
                }
                else if (!s.isTool && s.usable < s.need && s.carry > 0)
                {
                    sb.Append("(身");
                    sb.Append(s.carry);
                    sb.Append(')');
                }
            }
            string detail = sb.ToString();

            // ---- 非材料门槛（工作台标签 / 技能 / 任务条件） ----
            string gate = BuildGateText(bp, player);
            bool gateOk = gate.Length == 0;

            int rank;
            if (allUsable) rank = gateOk ? RANK_CRAFTABLE : RANK_GATE;
            else if (allPotential) rank = gateOk ? RANK_FROM_BOX : RANK_GATE;
            else rank = RANK_MISSING;

            // 「扣箱内材料合成」的可点判据：
            //   featureOn = 功能开着 + 属于支持子集 + 本机是服务端（见 AddStat 与 Crafting 文件头）
            //   再要求门槛过了、材料凑得齐（可用量已按同一口径算过）。
            // ⚠ 这里刻意**不跑引擎搜索**（候选最多 400 条 × 几个材料 × 两次全容器扫描，
            //   放在 0.5 秒轮询的重建路径上是白扔性能）。真实校验在点击时由
            //   StorageCrafting.TryCraft 用引擎搜索权威重做一遍，不足会明确告知。
            // ★ 不能用 boxCounts 当按钮闸门：它现在还含"这一页算不算箱子"，
            //   而借道的中转页不算 —— 拿它拦按钮会把"只用身上材料合成"这条**仍然成立**的路
            //   一起禁掉，那 `RelayWithSections` 这个声明就成了骗人（区块显示了却什么都点不动）。
            //   "功能开不开"与"这一页算不算箱"是两件事，必须分开。
            bool featureOn = AllowCraftFromBox && StorageCrafting.IsSupported(bp);
            bool craftable = featureOn && gateOk &&
                (rank == RANK_CRAFTABLE || rank == RANK_FROM_BOX);

            string status;
            if (rank == RANK_CRAFTABLE)
            {
                // 「含箱内」要说出来：不然玩家看到 2/1 会以为材料在身上，
                // 合成后才发现是箱子里被扣掉的（用户实测就误读过一次）。
                if (gateOk) status = usedBox ? "可合成·含箱内" : "可合成";
                else status = gate;
            }
            else if (rank == RANK_FROM_BOX)
            {
                // 走到这里说明箱子**不能**直接扣（联机客户端 / 关了开关 / 子集外配方），
                // 所以这一档仍是原版的口径：先把材料搬到身上再合。
                if (gateOk) status = "取出材料即可合成";
                else status = "取出材料 · " + gate;
            }
            else if (rank == RANK_GATE)
            {
                status = gate;
            }
            else
            {
                if (gateOk) status = "缺材料";
                else status = "缺材料 · " + gate;
            }

            // ★ 绝不允许「写着可合成、却没有按钮、也没说为什么」——
            //   用户实测就是这样看到装填弹匣那一行的（旧版把修理/装填整个排除在子集外，
            //   但状态仍按"材料够"算，于是显示【可合成】而按钮不出现）。
            //   现在装填/修理已经支持，剩下的不可点情形只有三种
            //   （联机客户端 / 关了 ConsumeFromBox / transferState·任务类配方），
            //   一律在文案里说清该去哪儿合，具体原因再进 Tooltip。
            string unsupported = null;
            if (!craftable && (rank == RANK_CRAFTABLE || rank == RANK_FROM_BOX))
            {
                unsupported = StorageCrafting.UnsupportedReason(bp);
                if (string.IsNullOrEmpty(unsupported))
                {
                    // 不是子集问题，那就是开关或联机范围问题
                    // ⚠ 不要在这里加"这一页不算箱"那一条：可点判据已经不含它
                    //   （featureOn 与 boxCounts 是分开的），那条文案永远不会命中。
                    //   口径提示放在**区块头部**一行统一说（见 headLabel 那里）。
                    unsupported = AllowCraftFromBox
                        ? "联机时箱子内容只有服务端能改"
                        : "已关闭 [Blueprint] ConsumeFromBox";
                }
                status = status + "·请走原版制作页";
            }

            if (rank == RANK_MISSING && !showMissing) return null;

            ItemAsset output = FindMainOutput(bp);
            if (output == null) return null;

            BlueprintEntry e = new BlueprintEntry();
            e.blueprint = bp;
            e.Key = BlueprintKey(bp);
            e.output = output;
            e.outputAmount = FindMainOutputAmount(bp);
            e.rank = rank;
            e.craftable = craftable;
            // 产物名与状态分开存：列表拼成「名 ×N 【状态】」，格子分两行显示。
            // 两种摆法用的是同一份数据，不存在"格子模式另算一遍状态"的第二套判据。
            e.productName = OperationPrefix(bp.Operation) + Name(output)
                + (e.outputAmount > 1 ? (" ×" + e.outputAmount) : "");
            e.statusShort = ShortStatus(rank, craftable);
            e.title = e.productName + "  【" + status + "】";
            e.detail = detail;
            e.tooltip = BuildTooltip(bp, output, e.title, detail, gate, craftable);

            // transferState 配方：把「产物会继承哪件材料的耐久/状态」写进 Tooltip。
            // ⚠ 只能调 StorageCrafting.DescribeTransfer —— 它和执行走同一个 capture
            //   （同一份引擎比较器排序）。界面自己再排一次序 = 第二套实现，
            //   就会出现「界面说继承 A、实际抄了 B」。
            //   只对有按钮的行算（多两次搜索），不拖慢整表重建。
            if (craftable && bp.transferState)
            {
                string transferNote = StorageCrafting.DescribeTransfer(ctx, bp, player);
                if (!string.IsNullOrEmpty(transferNote))
                {
                    e.tooltip = e.tooltip + "\n" + transferNote;
                }
            }

            if (!string.IsNullOrEmpty(unsupported))
            {
                // 具体原因进 Tooltip（行上只写「请走原版制作页」，放不下太多字）
                e.tooltip = e.tooltip + "\n这里不能直接合成：" + unsupported;
            }
            e.TextFp = TextFingerprint(rank, detail, gate, craftable);
            return e;
        }

        /// <summary>
        /// 把一个 BlueprintSupply 的 carry/box/usable 算进 statBuffer。
        ///
        /// ⚠ `usable` 的含义**只有一个正确的定义**：「玩家现在点合成，能凑到的量」。
        ///   它必须与执行侧（StorageCrafting 用引擎搜索算的）一致，否则就会出现
        ///   「明明能合、界面说不能」或反过来 —— 用户实测就撞上过这条（见 README 坑 #36）。
        ///   · 本视图可以扣箱内材料（单机/主机 + 开关开 + 属于支持子集）→ usable = 身上 + 箱内
        ///   · 不可以扣箱子（联机客户端 / 关了开关 / 修理装填等子集外配方）→ usable = 只算身上
        ///     （这才是原版口径，此时界面该显示「取出材料即可合成」而不是「可合成」）
        /// </summary>
        private BlueprintSupplyStat AddStat(BlueprintSupply sup, Player player, bool boxCountsAsUsable)
        {
            if (sup == null) return null;

            ItemAsset asset = sup.FindItemAsset();
            if (asset == null) return null;   // 内容包缺资产 → 与引擎一样跳过

            bool tool = !sup.ShouldConsume;

            BlueprintSupplyStat s = new BlueprintSupplyStat();
            s.asset = asset;
            s.need = sup.amount <= 0 ? 1 : sup.amount;
            s.isTool = tool;

            s.carry = CountSupply(sup, packIndex.Find(asset));
            if (tool)
            {
                // 工具类：引擎搜材料时 IncludeEquipmentSlots = true
                s.carry += CountSupply(sup, equipIndex.Find(asset));
            }
            s.box = CountSupply(sup, boxIndex.Find(asset));

            if (tool)
            {
                // 工具不消耗，箱子和装备格都算数（与引擎同判据），显示按 need 封顶
                s.usable = Mathf.Min(s.need, s.carry + s.box);
            }
            else if (boxCountsAsUsable)
            {
                // 本视图能直接扣箱内材料 → 箱里那些是真的「能用上的」
                s.usable = s.carry + s.box;
            }
            else
            {
                // 扣不到箱子（联机客户端 / 关了开关 / 子集外配方）→ 只算身上，与原版一致
                s.usable = s.carry;
            }

            statBuffer.Add(s);
            return s;
        }

        /// <summary>非材料门槛文本（空串 = 没有门槛，或全部满足）。</summary>
        private string BuildGateText(Blueprint bp, Player player)
        {
            sb.Length = 0;

            // ① 工作台 / 热源等附近标签（判据与原版同一套 public API）
            CachingAssetRef[] tags = null;
            try { tags = bp.GetApplicableRequiredNearbyCraftingTags(); }
            catch (Exception) { tags = null; }

            if (tags != null)
            {
                for (int i = 0; i < tags.Length; i++)
                {
                    TagAsset tag = tags[i].Get<TagAsset>();
                    if (tag == null) continue;

                    bool available;
                    try { available = player.crafting != null && player.crafting.IsCraftingTagAvailable(tag); }
                    catch (Exception) { available = false; }

                    if (!available)
                    {
                        if (sb.Length > 0) sb.Append(" · ");
                        sb.Append("需");
                        sb.Append(tag.PlainTextName);
                    }
                }
            }

            // ② 技能等级（用 public 的 RequiresSkill / GetPlayerSkillLevel，
            //    不碰 [Obsolete] 的 blueprint.skill）
            if (bp.RequiresSkill)
            {
                int has = 0;
                try { has = bp.GetPlayerSkillLevel(player); } catch (Exception) { has = 0; }
                if (has < bp.level)
                {
                    if (sb.Length > 0) sb.Append(" · ");
                    string skillName = null;
                    try { skillName = bp.DebugGetSkillName(); } catch (Exception) { }
                    sb.Append("需技能 ");
                    sb.Append(string.IsNullOrEmpty(skillName) ? "等级" : skillName);
                    sb.Append(' ');
                    sb.Append(bp.level);
                    sb.Append("(当前 ");
                    sb.Append(has);
                    sb.Append(')');
                }
            }

            // ③ 任务条件
            try
            {
                if (!bp.areConditionsMet(player))
                {
                    if (sb.Length > 0) sb.Append(" · ");
                    sb.Append("需任务条件");
                }
            }
            catch (Exception) { /* 条件系统出问题不该影响整张列表 */ }

            return sb.ToString();
        }

        private static string BuildTooltip(Blueprint bp, ItemAsset output,
            string title, string detail, string gate, bool craftable)
        {
            StringBuilder t = new StringBuilder(256);
            t.Append(title);

            TagAsset category = null;
            try { category = bp.GetCategoryTag(); } catch (Exception) { }
            if (category != null && !string.IsNullOrEmpty(category.PlainTextName))
            {
                t.Append("\n分类：").Append(category.PlainTextName);
            }

            try
            {
                Asset ownerAsset = bp.GetOwnerAsset();
                if (ownerAsset != null)
                {
                    ItemAsset owner = ownerAsset as ItemAsset;
                    t.Append("\n来源：").Append(owner != null ? owner.FriendlyName : ownerAsset.name);
                }
            }
            catch (Exception) { }

            if (!string.IsNullOrEmpty(detail)) t.Append("\n材料：").Append(detail);
            if (!string.IsNullOrEmpty(gate)) t.Append("\n").Append(gate);

            // 提示按能力写，别一律写「只读」—— 开着扣箱合成时这句话就是错的。
            if (craftable)
            {
                t.Append("\n点「合成」会**真的扣掉材料**（先扣箱内，不足再扣身上），" +
                    "产物按 箱子 → 背包 → 地上 的顺序给。");
                t.Append("\n一次点 1 次；不给技能经验（原版合成也不给）。");
            }
            else
            {
                t.Append("\n本行只读：不会扣材料、也不会挪动东西。");
            }
            return t.ToString();
        }

        // ---- 产物 ----

        /// <summary>
        /// 操作类配方的前缀。这类配方**不产出新物品**（产物就是被修/被装的那件），
        /// 不加前缀玩家会以为「修理扳手 ×1」是造一个扳手。
        /// 判据用 3.26 的 <c>Blueprint.Operation</c>（[Obsolete] 的 <c>type</c> 已废弃）。
        /// </summary>
        private static string OperationPrefix(EBlueprintOperation operation)
        {
            switch (operation)
            {
                case EBlueprintOperation.RepairTargetItem: return "修理 ";
                case EBlueprintOperation.FillTargetItem: return "装填 ";
                default: return "";
            }
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
            // 没有 outputs（或资产缺失）→ 退回「拥有这个配方的物品」（原版 legacy 模型里产物就是它）
            try { return bp.GetOwnerAsset() as ItemAsset; }
            catch (Exception) { return null; }
        }

        private static int FindMainOutputAmount(Blueprint bp)
        {
            BlueprintOutput[] outputs = bp.outputs;
            if (outputs == null || outputs.Length == 0 || outputs[0] == null) return 1;
            return outputs[0].amount <= 0 ? 1 : outputs[0].amount;
        }

        private static string Name(ItemAsset asset)
        {
            if (asset == null) return "未知物品";
            string n = asset.FriendlyName;
            return string.IsNullOrEmpty(n) ? ("物品 " + asset.id) : n;
        }

        // ---- 身份与文本指纹 ----

        /// <summary>
        /// 配方身份：所属资产 GUID + 配方下标。跨重建稳定 → 决定「这一行还是不是同一个配方」。
        /// </summary>
        private static int BlueprintKey(Blueprint bp)
        {
            unchecked
            {
                int h = 0;
                try
                {
                    if (bp.Owner != null)
                    {
                        Asset owner = bp.Owner.GetBlueprintOwnerAsset();
                        if (owner != null) h = owner.GUID.GetHashCode();
                    }
                }
                catch (Exception) { }
                return h * 397 ^ bp.Index;
            }
        }

        /// <summary>
        /// 文本指纹：状态档 + 材料明细 + 门槛。
        /// ⚠ 只喂**文字**，不喂图标 —— 图标只由配方身份决定，所以材料数字变化
        ///   走 FillRowText（改 label.Text），永远不触发图标重载。
        /// ⚠ 绝不能混入 item.state（工业机械的 state 每帧变 → 每帧重建整张列表）。
        /// </summary>
        private static int TextFingerprint(int rank, string detail, string gate, bool craftable)
        {
            unchecked
            {
                int h = rank;
                // 按钮可用性也要进指纹：材料数字没变、但「能不能扣箱合成」翻了
                // （例如开关切了、配方子集判定变了），文字与按钮都得刷新。
                h = h * 397 ^ (craftable ? 1 : 0);
                if (detail != null) h = h * 397 ^ detail.GetHashCode();
                if (gate != null) h = h * 397 ^ gate.GetHashCode();
                return h;
            }
        }

        private static int CompareEntry(BlueprintEntry a, BlueprintEntry b)
        {
            int c = a.rank.CompareTo(b.rank);
            if (c != 0) return c;
            c = a.output.id.CompareTo(b.output.id);
            if (c != 0) return c;
            return string.CompareOrdinal(a.detail, b.detail);
        }

        // ---- 行池 ----

        private class BlueprintRow
        {
            public ISleekButton Button;
            /// <summary>★ 26.10.3.51：这一行的填充层（`CellFill` 上色用）。建行时就建好，一直留着。</summary>
            public StorageUiFill Fill;
            public ISleekLabel Title;
            public ISleekLabel Detail;
            public ISleekButton Craft;      // 「合成」按钮（列表摆法专有；格子摆法为 null）
            public Blueprint Bp;            // 本行当前代表的配方 —— 回调读它，不闭包捕获
            public bool Craftable;          // 本行当前是否可点（格子摆法点整格时现读这个）
            public int Key;         // 当前绑的配方身份（int.MinValue = 已作废）
            public int TextFp;      // 当前文字指纹
            public bool InUse;
        }

        private BlueprintRow ClaimByKey(int key)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                BlueprintRow r = rows[i];
                if (!r.InUse && r.Button != null && r.Key == key)
                {
                    r.InUse = true;
                    return r;
                }
            }
            return null;
        }

        /// <summary>
        /// 池子里新建一行 —— **建立即认领**（`InUse = true`）。
        ///
        /// ⚠⚠ 与 `StorageListPanel.NewClaimedRow` 同一条教训（用户 2026-10-05 实测报的
        ///   「N 项只显示 M 行」）：第 ⑤ 步「清理闲置行」按 `InUse` 判，新建行若留
        ///   `InUse = false`，**同一趟里就会被自己人收走** —— 刚建好的行当场被藏起来，
        ///   而且只在**池子需要长大那一趟**出现 ⟹ 关掉重开就好（自愈型假象）。
        ///   ⚠ 这个视图与本条是**同一个 bug 的第二个受害者**（第三个受害者是大格子，
        ///     它在坑 #20 时改成了按序号绑定）⟹ 池子只留这**一个**新建入口。
        /// </summary>
        private BlueprintRow NewClaimedRow()
        {
            BlueprintRow r = new BlueprintRow();
            r.InUse = true;
            rows.Add(r);
            return r;
        }

        private BlueprintRow ClaimAnyFreeRow()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                BlueprintRow r = rows[i];
                if (!r.InUse)
                {
                    r.InUse = true;
                    return r;
                }
            }
            return null;
        }

        /// <summary>
        /// **整行重建**（Button + 产物图标 + 两个文字容器）。
        ///
        /// ⚠ 为什么整行重建而不是就地换图标（坑 #15）：SleekItemIcon 的贴图靠
        ///   ItemTool.getIcon 的异步回调才换上，同一元素二次 Refresh 不可靠；
        ///   而只 RemoveChild 图标会牵连同一父节点下的兄弟元素（文字一起消失）。
        ///   这里只在「这一行换成了另一个配方」时才走到，材料数字变化不重建。
        /// </summary>
        /// <summary>
        /// ★ 26.10.3.51：问一次「这一行配方该铺什么颜色」并交给填充层。
        /// 给的是**配方 + 材料够不够**（行里装的是配方，不是物品）——
        /// "能不能点合成"用的就是 <c>e.craftable</c>，所以颜色与那句提示同源。
        /// ⚠ 来源是**登记式**的（`StorageBlueprintFills`）：本视图的实例第三方拿不到，
        ///   挂实例属性等于写了个谁也调不到的公开面。
        /// ⚠ 颜色**不进指纹**：它每轮重算，改规则不必重建行（进指纹 = 换个颜色全量重建 + 图标重载）。
        /// </summary>
        private void ApplyCellFill(BlueprintRow r, BlueprintEntry e)
        {
            if (r == null || r.Fill == null) return;

            // 解析 + 异常隔离 + 只报一次都在登记处（与另两个视图的 `CellFill` 同一套语义）
            SleekColor c = (e == null)
                ? default(SleekColor)
                : StorageBlueprintFills.Resolve(e.blueprint, e.craftable);
            r.Fill.Color = c;
        }

        private void RebuildRow(BlueprintRow r, BlueprintEntry e)
        {
            if (r.Button != null)
            {
                try { scroll.RemoveChild(r.Button); } catch (Exception) { }
                r.Button = null;
                r.Title = null;
                r.Detail = null;
            }

            ISleekButton btn = Glazier.Get().CreateButton();
            btn.PositionOffset_X = 0f;
            btn.PositionOffset_Y = 0f;
            btn.SizeScale_X = 1f;
            btn.SizeOffset_X = -18f;   // 留出滚动条宽度
            btn.SizeOffset_Y = ROW_HEIGHT;
            btn.Text = "";             // 文字用独立 label，避免被图标遮挡
            btn.BackgroundColor = new SleekColor(ESleekTint.BACKGROUND, 0.55f);

            // ⚠ 先挂进树再建子元素（子元素尺寸相对父节点算，坑 #19）
            scroll.AddChild(btn);
            r.Button = btn;
            r.Key = e.Key;

            // ★ 26.10.3.51 填充层：**必须是本行的第一个子元素**（同一父容器里后加的盖在先加的上面）。
            //   颜色由 `CellFill` 每轮解析（见 ApplyCellFill），这里只建壳。
            r.Fill = new StorageUiFill();
            r.Fill.Inset = FILL_INSET;
            r.Fill.Build(btn);

            ItemAsset output = e.output;

            // 图标：固定尺寸 + 资产默认状态。
            // ⚠ 状态来自 output.getState()（合成产物的默认态），
            //   **绝不读任何 item.state** —— 那是每帧在变的数据（坑 #17）。
            float iconSize = AsGrid ? CELL_INNER_ICON : ICON_SIZE;

            SleekItemIcon icon = new SleekItemIcon();
            if (AsGrid)
            {
                // 格子里图标水平居中：PositionScale_X=0.5 再左挪半个图标
                icon.PositionScale_X = 0.5f;
                icon.PositionOffset_X = -iconSize * 0.5f;
                icon.PositionOffset_Y = 6f;
            }
            else
            {
                icon.PositionScale_X = 0f;
                icon.PositionOffset_X = 4f;
                icon.PositionOffset_Y = (ROW_HEIGHT - ICON_SIZE) * 0.5f;
            }
            icon.SizeOffset_X = iconSize;
            icon.SizeOffset_Y = iconSize;
            byte[] state = null;
            try { state = output.getState(); } catch (Exception) { }
            icon.Refresh(output.id, 100, state, output, (int) iconSize, (int) iconSize);
            btn.AddChild(icon);

            // 两个文字标签：列表摆在图标右侧（左对齐），格子摆在图标下方（居中）。
            // ⚠ 行高都必须 ≥ 24（小号字体的实测下限，坑 #21）。
            ISleekLabel title = Glazier.Get().CreateLabel();
            ISleekLabel detail = Glazier.Get().CreateLabel();

            if (AsGrid)
            {
                title.PositionOffset_X = 2f;
                title.SizeScale_X = 1f;
                title.SizeOffset_X = -4f;
                title.PositionOffset_Y = iconSize + 10f;
                title.SizeOffset_Y = CELL_NAME_H;
                title.TextAlignment = TextAnchor.MiddleCenter;

                detail.PositionOffset_X = 2f;
                detail.SizeScale_X = 1f;
                detail.SizeOffset_X = -4f;
                detail.PositionOffset_Y = iconSize + 10f + CELL_NAME_H;
                detail.SizeOffset_Y = CELL_STATUS_H;
                detail.TextAlignment = TextAnchor.MiddleCenter;
            }
            else
            {
                title.PositionOffset_X = ICON_SIZE + 12f;
                title.PositionOffset_Y = 2f;
                title.SizeScale_X = 1f;
                title.SizeOffset_X = -(ICON_SIZE + 20f + CRAFT_RESERVE);
                title.SizeOffset_Y = TITLE_H;
                title.TextAlignment = TextAnchor.MiddleLeft;

                detail.PositionOffset_X = ICON_SIZE + 12f;
                detail.PositionOffset_Y = 2f + TITLE_H;
                detail.SizeScale_X = 1f;
                detail.SizeOffset_X = -(ICON_SIZE + 20f + CRAFT_RESERVE);
                detail.SizeOffset_Y = DETAIL_H;
                detail.TextAlignment = TextAnchor.MiddleLeft;
            }

            title.FontSize = ESleekFontSize.Small;
            title.TextColor = new SleekColor(ESleekTint.FONT);
            btn.AddChild(title);
            r.Title = title;

            detail.FontSize = ESleekFontSize.Small;
            detail.TextColor = new SleekColor(ESleekTint.FONT);
            btn.AddChild(detail);
            r.Detail = detail;

            // 「合成」入口按摆法分两种：
            //   列表 = 行右侧一个「合成」按钮（整行本身不响应点击）
            //   格子 = 点整格合成（格子里塞不下按钮），所以必须**现读 r.Craftable**，
            //          不可点的格子点了什么都不做（不能靠"上次建的时候是可点的"这种状态）
            if (AsGrid)
            {
                r.Craft = null;
                btn.OnClicked += delegate (ISleekElement b) { OnCellClicked(r); };
            }
            else
            {
                ISleekButton craft = Glazier.Get().CreateButton();
                craft.PositionScale_X = 1f;                // 贴右：父宽 ×1 再往左挪
                craft.PositionOffset_X = -(CRAFT_W + 4f);
                craft.PositionOffset_Y = (ROW_HEIGHT - CRAFT_H) * 0.5f;
                craft.SizeScale_X = 0f;                    // 固定宽，不随面板宽度伸缩
                craft.SizeOffset_X = CRAFT_W;
                craft.SizeOffset_Y = CRAFT_H;
                craft.Text = "合成";
                craft.FontSize = ESleekFontSize.Small;
                craft.IsVisible = false;                   // 默认藏，由 FillRowText 决定

                // ⚠ 回调读「这一行当前是哪个配方」（r.Bp），不闭包捕获条目/下标 ——
                //   行池会复用，捕获旧对象就会点 A 行合成 B 配方（同列表视图那条坑）。
                craft.OnClicked += delegate (ISleekElement b) { OnCraftClicked(r); };

                btn.AddChild(craft);
                r.Craft = craft;
            }

            // 新行建完立刻填一次文字（不靠「指纹哨兵」，避免与真实指纹撞车）
            FillRowText(r, e);
        }

        /// <summary>
        /// 点「合成」：真的扣料 + 给产物。**结果一定显示在头部**（成功与失败都要有回音），
        /// 失败原因由 StorageCrafting 给（UI 不再自己拼一套话，免得两处说法不一致）。
        /// </summary>
        private void OnCraftClicked(BlueprintRow r)
        {
            if (r == null || r.Bp == null || ctx == null) return;

            Player player = Player.LocalPlayer;
            if (player == null) return;

            CraftOutcome outcome;
            try
            {
                outcome = StorageCrafting.TryCraft(ctx, r.Bp, player);
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("点合成抛出异常：" + e);
                lastAction = "合成异常，详见日志";
                lastActionTime = Time.unscaledTime;
                MarkDirty();
                return;
            }

            lastAction = outcome.message;
            lastActionTime = Time.unscaledTime;

            if (outcome.ok)
            {
                CustomUIPlugin.Log("扣箱合成成功：" + r.Bp + " → " + outcome.message);
            }
            else
            {
                CustomUIPlugin.Warn("扣箱合成未执行：" + r.Bp + "（原因 " + outcome.block + "）" +
                    " " + outcome.message);
            }

            // 材料可能已经变了，必须重算（指纹本身也会变，这里是双保险）
            MarkDirty();
        }

        /// <summary>
        /// 只刷文字与 Tooltip（材料数字变了、或状态档变了）。
        /// 颜色用主题的「坏消息」色 ESleekTint.BAD，不自定义颜色
        /// （自定义色在部分主题里会与背景混淆，见列表视图同款处理）。
        /// </summary>
        private void FillRowText(BlueprintRow r, BlueprintEntry e)
        {
            if (r.Button == null || r.Title == null || r.Detail == null) return;

            // 列表：产物名+状态 / 材料明细
            // 格子：产物名 / 短状态（塞不下明细，明细进 Tooltip）
            if (AsGrid)
            {
                r.Title.Text = e.productName;
                r.Detail.Text = e.statusShort;

                // 可点的格子底色重一点，让玩家一眼看出哪些能按
                r.Button.BackgroundColor = new SleekColor(ESleekTint.BACKGROUND,
                    e.craftable ? 0.78f : 0.42f);
            }
            else
            {
                r.Title.Text = e.title;
                r.Detail.Text = e.detail;
            }

            r.Button.TooltipText = e.tooltip;

            // 配方身份与可点性都写进行对象，供回调**现读**（行池会复用）
            r.Bp = e.blueprint;
            r.Craftable = e.craftable;

            if (r.Craft != null)
            {
                // 只有 IsVisible（Glazier 的 ISleekButton 没有 IsInteractable）。
                // 不可合时整块隐藏 —— 隐藏的 GameObject 收不到任何点击，
                // 比「亮着但点了报错」诚实得多。
                r.Craft.IsVisible = e.craftable;
                r.Craft.TooltipText = e.craftable
                    ? "从这里扣材料合成（箱内优先），产物先塞回箱子"
                    : e.tooltip;
            }

            ESleekTint tint = (e.rank == RANK_MISSING) ? ESleekTint.BAD : ESleekTint.FONT;
            r.Title.TextColor = new SleekColor(tint);
            r.Detail.TextColor = new SleekColor(tint);

            r.TextFp = e.TextFp;
        }

        /// <summary>
        /// 格子里那句短状态（宽度只有面板的 1/4，放不下长句）。
        /// ⚠ 与列表的完整状态**同源**（同一个 rank + craftable），
        ///   两种摆法说的必须是同一件事。
        /// ⚠ 「能就地扣箱内合成」与「材料都在身上」在这里**都只写「可合成」**
        ///   （用户 2026-09-27 要求）：格子里放不下区分，而区分信息在 Tooltip
        ///   与材料明细里都有；两种情况点下去的结果都是「立刻出产物」。
        /// </summary>
        private static string ShortStatus(int rank, bool craftable)
        {
            if (rank == RANK_MISSING) return "缺材料";
            if (rank == RANK_GATE) return "需设备";
            if (rank == RANK_FROM_BOX) return "取出即可";
            // RANK_CRAFTABLE
            if (craftable) return "可合成";
            return "走原版";
        }

        /// <summary>
        /// 格子摆法：点整格合成。⚠ 必须现读 r.Craftable —— 格子会被复用到别的配方，
        /// 用"建的时候可点"当状态会让不可合的配方也能按下去。
        /// </summary>
        private void OnCellClicked(BlueprintRow r)
        {
            if (r == null || !r.Craftable) return;
            OnCraftClicked(r);
        }

        // ================= 蓝图总表（缓存） =================

        /// <summary>
        /// 所有蓝图（来自全部 IBlueprintOwner：ItemAsset 与 CraftingAsset）。
        ///
        /// 用 public 的 <c>Assets.find&lt;IBlueprintOwner&gt;</c> 枚举 —— 与原版
        /// PlayerDashboardCraftingUI 内部同一做法，不反射它的 internal 方法。
        /// 资产表只在连服/换图时变，而原版那时也会重建背包页，所以**每次 Open 重扫一次**
        /// （一次线性扫资产表，代价远小于每帧），运行中不重复扫。
        /// </summary>
        private static List<Blueprint> cachedBlueprints;

        /// <summary>清缓存（下次 Open 会重扫）。</summary>
        public static void InvalidateCache()
        {
            cachedBlueprints = null;
        }

        private static void RefreshBlueprintCache()
        {
            List<Blueprint> list = new List<Blueprint>();
            try
            {
                List<IBlueprintOwner> owners = new List<IBlueprintOwner>();
                Assets.find(owners);

                for (int i = 0; i < owners.Count; i++)
                {
                    IBlueprintOwner owner = owners[i];
                    if (owner == null) continue;

                    List<Blueprint> blueprints = owner.GetBlueprints();
                    if (blueprints == null) continue;

                    for (int k = 0; k < blueprints.Count; k++)
                    {
                        Blueprint bp = blueprints[k];
                        if (bp != null) list.Add(bp);
                    }
                }
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("枚举蓝图失败：" + e.Message);
                cachedBlueprints = null;
                return;
            }

            int previous = (cachedBlueprints != null) ? cachedBlueprints.Count : -1;
            cachedBlueprints = list;
            if (list.Count != previous)
            {
                CustomUIPlugin.Log("蓝图总表：" + list.Count + " 条（资产扫描，只读缓存）");
            }
        }

        // ================= 指纹 =================

        /// <summary>
        /// 数据指纹：箱子内容 + 身上背包内容（只 id / amount / quality）+ 过滤器版本。
        ///
        /// ⚠ 与另两个视图不同，这里**不含坐标**：蓝图列表不按坐标排，
        ///   含坐标只会造成无谓重建。
        /// ⚠ 同样绝不能混入 item.state —— 工业机械的 state 每帧都在变，
        ///   混进来等于每帧重建整张列表（README 坑 #17）。
        /// ⚠ 必须含过滤器版本：别的插件在界面开着时改了「显示哪些配方」，
        ///   箱子内容没变 → 不含版本就会一直显示旧列表（看起来像"设置没生效"）。
        /// </summary>
        private static int Fingerprint(StorageUiContext c)
        {
            if (c == null || c.Container == null) return -1;

            unchecked
            {
                int h = 17;
                h = h * 397 ^ StorageBlueprintFilters.Version;
                h = MixItems(h, c.Container);

                Player player = Player.LocalPlayer;
                if (player != null && player.inventory != null && player.inventory.items != null)
                {
                    for (int page = PlayerInventory.SLOTS; page < PlayerInventory.STORAGE; page++)
                    {
                        if (page < player.inventory.items.Length)
                        {
                            h = MixItems(h, player.inventory.items[page]);
                        }
                    }
                }
                return h;
            }
        }

        private static int MixItems(int hash, Items items)
        {
            if (items == null) return hash;

            unchecked
            {
                int h = hash;
                byte count = items.getItemCount();
                h = h * 397 ^ count;
                for (byte i = 0; i < count; i++)
                {
                    ItemJar jar = items.getItem(i);
                    if (jar == null || jar.item == null) continue;
                    h = h * 397 ^ jar.item.id;
                    h = h * 397 ^ jar.item.amount;
                    h = h * 397 ^ jar.item.quality;
                }
                return h;
            }
        }
    }

    /// <summary>配方过滤器：返回 false = 这条配方不显示在蓝图列表里。</summary>
    public delegate bool StorageBlueprintFilterDelegate(Blueprint bp, StorageUiContext ctx);

    /// <summary>
    /// 「蓝图列表显示哪些配方」（公开 API，给别的插件限定显示范围）。
    ///
    /// 典型用法（其他插件侧）：
    /// <code>
    /// // 只显示某个类别
    /// StorageBlueprintFilters.Add("mymod", StorageBlueprintFilters.ByCategory("Utilities"));
    /// // 只显示自己机器能用到的那几条
    /// StorageBlueprintFilters.Add("mymod", StorageBlueprintFilters.ByProduct(22, 25, 260));
    /// // 完全自定义（ ctx 就是当前这块箱子，可以按箱型给不同范围）
    /// StorageBlueprintFilters.Add("mymod", delegate (Blueprint bp, StorageUiContext c)
    /// {
    ///     return c.Asset != null && c.Asset.GUID == myMachineGuid
    ///         && bp.Operation == EBlueprintOperation.CraftItem;
    /// });
    /// StorageBlueprintFilters.Remove("mymod");   // 卸载/关功能时撤掉
    /// </code>
    ///
    /// 设计要点：
    ///   · **按来源命名**，多个插件各占一个名字、互不覆盖（否则谁后注册谁说话）。
    ///   · 全部来源都通过才算通过（AND）—— 限定范围是收窄，不是投票。
    ///   · 改动会 <c>Version++</c>，而 Version 进了蓝图视图的数据指纹
    ///     → 界面正开着也会立刻重算，不需要玩家重开箱子。
    ///   · 判据抛异常时按「显示」处理并只报一次日志：坏判据最多让人多看到几行，
    ///     不该把整张列表弄没（与 <c>IsHidden</c> 同一取向）。
    ///   · 这里只管**显示**。要加自己的配方不必碰本插件：给资产实现
    ///     <c>IBlueprintOwner</c> 就会被蓝图视图枚举到（它扫的是全部 owner）。
    /// </summary>
    public static class StorageBlueprintFilters
    {
        private class Source
        {
            internal string Name;
            internal StorageBlueprintFilterDelegate Filter;
            internal bool Warned;     // 抛过异常（报过一次日志，不再刷）
        }

        private static readonly List<Source> sources = new List<Source>();
        private static int version;

        // 配置来源（[Blueprint] OnlyCategories / OnlyProducts），与第三方来源分开存
        private static string[] cfgCategories;
        private static ushort[] cfgProducts;

        /// <summary>每次改动 +1：视图把它并进数据指纹 → 立刻重算。</summary>
        public static int Version { get { return version; } }

        /// <summary>当前挂了多少个第三方来源。</summary>
        public static int Count { get { return sources.Count; } }

        /// <summary>
        /// 挂一个判据。**同名会替换**（重复调用不叠加），所以插件重启/重载时
        /// 直接再 Add 一次即可，不必先 Remove。
        /// </summary>
        public static void Add(string source, StorageBlueprintFilterDelegate filter)
        {
            if (string.IsNullOrEmpty(source) || filter == null) return;

            for (int i = sources.Count - 1; i >= 0; i--)
            {
                if (sources[i].Name == source) sources.RemoveAt(i);
            }

            Source s = new Source();
            s.Name = source;
            s.Filter = filter;
            sources.Add(s);
            version++;
            CustomUIPlugin.Log("蓝图过滤器 + " + source + "（当前 " + sources.Count + " 个来源）");
        }

        /// <summary>撤掉某个来源（插件卸载/关功能时用）。</summary>
        public static void Remove(string source)
        {
            if (string.IsNullOrEmpty(source)) return;
            for (int i = sources.Count - 1; i >= 0; i--)
            {
                if (sources[i].Name == source)
                {
                    sources.RemoveAt(i);
                    version++;
                    CustomUIPlugin.Log("蓝图过滤器 - " + source);
                }
            }
        }

        /// <summary>清空所有第三方来源（配置来源不受影响）。</summary>
        public static void Clear()
        {
            if (sources.Count == 0) return;
            sources.Clear();
            version++;
        }

        /// <summary>
        /// 这条配方要不要显示。配置判据 + 全部来源都通过才算通过。
        /// </summary>
        public static bool Passes(Blueprint bp, StorageUiContext ctx)
        {
            if (bp == null) return false;
            if (!PassesConfig(bp)) return false;

            for (int i = 0; i < sources.Count; i++)
            {
                Source s = sources[i];
                if (s.Filter == null) continue;
                try
                {
                    if (!s.Filter(bp, ctx)) return false;
                    s.Warned = false;
                }
                catch (Exception e)
                {
                    if (!s.Warned)
                    {
                        s.Warned = true;
                        CustomUIPlugin.Error("蓝图过滤器 " + s.Name + " 抛异常，这条按显示处理：" + e.Message);
                    }
                }
            }
            return true;
        }

        // ================= 配置来源 =================

        private static bool PassesConfig(Blueprint bp)
        {
            if (cfgCategories != null && cfgCategories.Length > 0 && !MatchesCategory(bp, cfgCategories)) return false;
            if (cfgProducts != null && cfgProducts.Length > 0 && !MatchesProduct(bp, cfgProducts)) return false;
            return true;
        }

        /// <summary>由 [Blueprint] OnlyCategories / OnlyProducts 落地（空 = 不限）。</summary>
        public static void SetConfigFilter(string categories, string products)
        {
            string[] c = ParseList(categories);
            ushort[] p = ParseIds(products);

            bool same = SameNames(c, cfgCategories) && SameIds(p, cfgProducts);
            cfgCategories = c;
            cfgProducts = p;
            if (!same) version++;
        }

        // ================= 判据本体（也供第三方复用） =================

        /// <summary>
        /// 类别 = 配方的 category 标签（原版制作页的分类，<c>Blueprint.GetCategoryTag()</c>）。
        /// 认资产名（英文，如 Tool / Utilities）也认显示名（如 工具 / 实用品），不分大小写。
        /// </summary>
        public static bool MatchesCategory(Blueprint bp, string[] categories)
        {
            if (bp == null || categories == null) return false;

            TagAsset tag = null;
            try { tag = bp.GetCategoryTag(); }
            catch (Exception) { return false; }
            if (tag == null) return false;

            for (int i = 0; i < categories.Length; i++)
            {
                string want = categories[i];
                if (string.IsNullOrEmpty(want)) continue;
                if (string.Equals(tag.name, want, StringComparison.OrdinalIgnoreCase)) return true;
                string fn = tag.FriendlyName;
                if (!string.IsNullOrEmpty(fn) &&
                    string.Equals(fn, want, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>产物 id 命中（一个配方可有多个产物，任一命中即算）。</summary>
        public static bool MatchesProduct(Blueprint bp, ushort[] ids)
        {
            if (bp == null || ids == null) return false;

            BlueprintOutput[] outputs = bp.outputs;
            if (outputs != null)
            {
                for (int i = 0; i < outputs.Length; i++)
                {
                    if (outputs[i] == null) continue;
                    ItemAsset asset = outputs[i].FindItemAsset();
                    if (asset == null) continue;
                    if (IndexOf(ids, asset.id) >= 0) return true;
                }
            }

            // 操作类配方（装弹/修理）可能没写 outputs —— 那时产物就是被操作的那件物品
            try
            {
                ItemAsset owner = bp.GetOwnerAsset() as ItemAsset;
                if (owner != null && IndexOf(ids, owner.id) >= 0) return true;
            }
            catch (Exception) { }

            return false;
        }

        // ================= 便捷判据 =================

        /// <summary>按类别限定（一个或多个类别名）。</summary>
        public static StorageBlueprintFilterDelegate ByCategory(params string[] categories)
        {
            string[] copy = (string[])(categories ?? new string[0]);
            return delegate (Blueprint bp, StorageUiContext ctx) { return MatchesCategory(bp, copy); };
        }

        /// <summary>按产物 id 限定（一个或多个物品 id）。</summary>
        public static StorageBlueprintFilterDelegate ByProduct(params ushort[] ids)
        {
            ushort[] copy = (ushort[])(ids ?? new ushort[0]);
            return delegate (Blueprint bp, StorageUiContext ctx) { return MatchesProduct(bp, copy); };
        }

        // ================= 解析配置字符串 =================

        private static string[] ParseList(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;

            string[] parts = s.Split(',');
            List<string> list = new List<string>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                string v = parts[i].Trim();
                if (v.Length > 0) list.Add(v);
            }
            if (list.Count == 0) return null;
            return list.ToArray();
        }

        private static ushort[] ParseIds(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;

            string[] parts = s.Split(',');
            List<ushort> list = new List<ushort>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                string v = parts[i].Trim();
                if (v.Length == 0) continue;

                ushort id;
                if (ushort.TryParse(v, out id)) list.Add(id);
                else CustomUIPlugin.Warn("[Blueprint] OnlyProducts 里的「" + v + "」不是物品 id，已忽略");
            }
            if (list.Count == 0) return null;
            return list.ToArray();
        }

        private static bool SameNames(string[] a, string[] b)
        {
            if (a == null || a.Length == 0) return b == null || b.Length == 0;
            if (b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static bool SameIds(ushort[] a, ushort[] b)
        {
            if (a == null || a.Length == 0) return b == null || b.Length == 0;
            if (b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static int IndexOf(ushort[] ids, ushort id)
        {
            for (int i = 0; i < ids.Length; i++) if (ids[i] == id) return i;
            return -1;
        }
    }
}
