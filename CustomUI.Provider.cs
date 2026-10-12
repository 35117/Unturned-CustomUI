// ============================================================================
//  CustomUI · 界面提供者接口与注册表
// ============================================================================
//  这是给「其他插件」用的接入层。其他插件只需实现 IStorageUiProvider 并注册，
//  本插件负责「拦截 F 打开 → 询问谁能处理 → 分发」。
//
//  典型用法（其他插件侧，3 行）：
//      StorageUiRegistry.Register(new MyProvider());
//
//  设计要点：
//    · Provider 只拿到「上下文」对象（InteractableStorage + 玩家 + 目标路障），
//      不需要知道本插件内部怎么挂载 Glazier 元素。
//    · Priority 高者优先。原版回退（返回 null）由宿主自动处理。
//    · 生命周期由宿主保证：Open 成功后一定会在关闭时调 Close。
// ============================================================================
using System;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace CustomUI
{
    /// <summary>
    /// 插件借道（<c>openTrunk</c>）挂上存储页的那个 <c>Items</c>，<b>到底是什么</b>。
    ///
    /// ★ 为什么要有这个声明：宿主只能看出"这一页是借来的"（<c>IsVirtual</c>），
    ///   看不出这一页**是不是一个容器**。而"是不是容器"决定另外两件事，不声明就只能猜：
    ///   ① 右列区块（蓝图/配方那类）该不该挂载；
    ///   ② 「扣箱内材料合成」能不能把这一页当材料源、产物能不能塞进去。
    ///   猜错的后果不是崩溃，是<b>静默的账不对</b>：借道页里正托着的那件复制品
    ///   被当箱内材料扣掉 ⟹ 对账报"丢到地上" ⟹ 调用方按数量扣账 ⟹ 玩家凭空少一件
    ///   （2026-10-04 就记在本插件 README §4.17 的"接工业之前应当先做"里，一直没做）。
    ///   ⟹ 这个事实<b>只有开会话的一方知道</b>，必须由它声明。
    /// </summary>
    public enum StorageUiVirtualPage
    {
        /// <summary>
        /// 默认。这一页<b>只是拖拽中转区</b>，不是容器：
        /// 右列区块不挂载；合成不认它当箱内材料；产物不塞进它。
        /// ⟹ 拿不准就选这个 —— 它最接近"这一页什么都没有"，不会凭空多出可扣的东西。
        /// </summary>
        Relay = 0,

        /// <summary>
        /// 同样不是容器，但<b>右列区块照常出现</b>：蓝图的"可合成/缺材料"只按背包与装备算，
        /// 「扣箱内合成」禁用并说明原因。
        /// ⟹ 给"借道界面里仍希望玩家能看配方、能用手上的东西合成"的接入方。
        /// </summary>
        RelayWithSections = 1,

        /// <summary>
        /// 这一页是<b>真实、可当箱子读写</b>的容器（例如插件自己持久化的机器内部库存）。
        /// 行为与真箱子同款：区块挂载、材料算它、产物塞它。
        /// ⚠ 只有当"往这一页放东西不会在关界面时被销毁"成立时才能选它 ——
        ///   纯中转页选这个就等于把复制品喂给合成（就是上面那个坑）。
        /// </summary>
        Storage = 2
    }

    /// <summary>
    /// 自定义存储界面的上下文。宿主在决定接管时构造，交给 Provider 使用。
    /// </summary>
    public class StorageUiContext
    {
        /// <summary>
        /// ⚠ 权威数据源：本地玩家背包里的「存储页」容器。
        /// 单机时它与 Storage.items 是同一个对象；联机客户端上 Storage 为 null，
        /// 数据只存在于这里。**一切读写都以它为准。**
        /// </summary>
        public Items Container;

        /// <summary>
        /// 引擎储物组件。⚠ 只有服务端/单机才有值；专用服务器客户端上为 null。
        /// 仅用于取资产/交互等辅助信息，**不要用它取物品数据**（用 Container）。
        /// </summary>
        public InteractableStorage Storage;

        /// <summary>操作者（本地玩家）。</summary>
        public Player Player;

        /// <summary>该储物所属的路障。可能为 null（车后备箱、或客户端拿不到时）。</summary>
        public BarricadeDrop Drop;

        /// <summary>储物资产（含 storage_x / storage_y 尺寸）。可能为 null。</summary>
        public ItemStorageAsset Asset;

        /// <summary>界面标题（默认取资产友好名，Provider 可改）。</summary>
        public string Title;

        /// <summary>
        /// 这次接管的是不是**插件借道**的虚拟存储（见 <see cref="StorageUiPanels.OpenVirtualStorage"/>）。
        /// 借道走的是引擎的车后备箱通道（<c>openTrunk</c>，它是唯一能挂任意 <c>Items</c> 的公开入口），
        /// 所以 <see cref="Storage"/> 与 <see cref="Drop"/> 都为 null、<see cref="Asset"/> 也为 null
        /// —— Provider 要靠这个标记判断"这不是普通箱子，别按普通箱子的规则读写"。
        /// </summary>
        public bool IsVirtual;

        /// <summary>
        /// 借道会话的**归属令牌**：由 <c>OpenVirtualStorage(items, title, tag)</c> 传进来，
        /// 本插件**不解释、只原样带回**。
        ///
        /// 为什么需要：<see cref="IsVirtual"/> 只说明"这一页是借来的"，不说明**是谁借的**。
        /// 两个插件都想用虚拟存储时会抢同一个存储位（候选链里谁优先级高谁拿到，
        /// 另一个连提示都没有）⟹ 各自的 Provider 在 <c>CanHandle</c> 里用
        /// <c>ReferenceEquals(ctx.Tag, 自己发出去的令牌)</c> 只认自己那次。
        /// 普通（真箱子）接管时恒为 null。
        /// ⚠ 与 <c>StorageUiRelay.Track</c> 的来历令牌同一套做法：
        ///   跨插件共享的可变状态必须带来源，不能留全局位。
        /// </summary>
        public object Tag;

        /// <summary>
        /// 借道会话里，<see cref="Container"/> 这一页**是什么性质**（由开会话的一方声明，
        /// 见 <see cref="StorageUiVirtualPage"/>）。真箱子接管时恒为
        /// <see cref="StorageUiVirtualPage.Storage"/> —— 真箱子当然可以当箱子用。
        ///
        /// ⚠ **别直接比较这个枚举** —— 请用两个语义化属性：
        ///   <see cref="UsableAsContainer"/>（能不能当容器读写）、
        ///   <see cref="WantsRegions"/>（要不要挂右列区块）。
        ///   三档的真值表在这两个属性上才看得清；直接写 `== Relay` / `== Storage`
        ///   正是"每个消费方各记一条判据、漏一处就静默算错"的老病（`26.10.3.40` 收口）。
        ///
        /// ⚠ 它是**宿主**按「开会话那一方」的声明写入的。外部改写会被记一条警告
        ///   （**不拒绝** —— 拒绝会让任何写过它的老代码直接出错；见 setter 的说明）。
        /// </summary>
        public StorageUiVirtualPage VirtualPage
        {
            get { return virtualPage; }
            set
            {
                virtualPage = value;
                WarnExternalVirtualPageWrite();
            }
        }

        /// <summary>
        /// ★ `26.10.3.40`：这一页**能不能当容器用**（能不能在里面读写真实物品）。
        ///
        /// `Storage` 可以（插件自己持久化的容器 = 真容器）；两种 `Relay*` **都不行**
        /// —— 那一页在关界面时会被原版换成新的空 `Items`，放进去的东西随之消失。
        /// ⚠ 扣箱合成、容器类读取都该问它，**不要**自己去比 `VirtualPage == Storage`。
        /// </summary>
        public bool UsableAsContainer
        {
            get { return virtualPage == StorageUiVirtualPage.Storage; }
        }

        /// <summary>
        /// ★ `26.10.3.40`：这一页**要不要挂右列区块**。
        ///
        /// `Relay` 不挂（纯中转区，区块出现在那儿既没意义也会骗人）；
        /// `RelayWithSections` 与 `Storage` 都挂。
        /// ⚠ 它与 <see cref="UsableAsContainer"/> **不是互补关系** ——
        ///   `RelayWithSections` 正是「不挂容器、但区块要挂」的那个中间档。
        /// </summary>
        public bool WantsRegions
        {
            get { return virtualPage != StorageUiVirtualPage.Relay; }
        }

        /// <summary>★ `26.10.3.40`：宿主专用写入（不触发"外部改写"警告）。</summary>
        internal void SetVirtualPage(StorageUiVirtualPage page)
        {
            virtualPage = page;
        }

        private StorageUiVirtualPage virtualPage = StorageUiVirtualPage.Storage;
        private static bool virtualPageWriteWarned;

        private static void WarnExternalVirtualPageWrite()
        {
            if (virtualPageWriteWarned) return;
            virtualPageWriteWarned = true;
            CustomUIPlugin.Warn("有代码改写了 ctx.VirtualPage —— 它是宿主按「开会话那一方」的声明写入的，" +
                "改写会同时改掉「区块挂不挂」与「能不能当容器用」两件事。" +
                "要改声明请用 StorageUiPanels.OpenVirtualStorage(items, title, tag, page) 重开会话；" +
                "只想读，用 ctx.UsableAsContainer / ctx.WantsRegions。");
        }

        /// <summary>容器容量（格数）。</summary>
        public int Capacity
        {
            get { return (Container != null) ? (Container.width * Container.height) : 0; }
        }

        public StorageUiContext(Items container, InteractableStorage storage, Player player, BarricadeDrop drop, ItemStorageAsset asset)
        {
            Container = container;
            Storage = storage;
            Player = player;
            Drop = drop;
            Asset = asset;
            Title = (asset != null) ? asset.FriendlyName : "储存";
        }
    }

    /// <summary>
    /// 界面提供者。想接管存储界面的插件实现这个接口并注册到 StorageUiRegistry。
    /// </summary>
    public interface IStorageUiProvider
    {
        /// <summary>唯一标识（用于日志与去重），建议用 "作者.插件名" 形式。</summary>
        string Id { get; }

        /// <summary>优先级，大者优先。内置列表视图用 0，第三方可用 100+ 覆盖。</summary>
        int Priority { get; }

        /// <summary>
        /// 是否能处理这个上下文。返回 false 则宿主继续问下一个 Provider，
        /// 全都不能处理时回退原版界面。
        /// </summary>
        bool CanHandle(StorageUiContext ctx);

        /// <summary>
        /// 打开界面。返回 true 表示已接管（宿主将隐藏原版存储区）；
        /// 返回 false 表示放弃 —— 宿主会**换候选链里的下一个 Provider 再试**
        /// （全都失败才回退原版）。
        /// ⚠ 因为宿主不会在返回 false 后调 Close，所以失败前必须自己拆掉半成品
        ///   （内置三个视图都是在 catch 里 DiscardPark()）。
        /// </summary>
        bool Open(StorageUiContext ctx);

        /// <summary>
        /// 关闭界面。宿主保证只在 Open 返回 true 后调用。
        /// ⚠ 内置视图的实现是「**只藏不拆**」：面板与行池跨次打开常驻复用。
        ///   拆掉 = 图标全部作废，而我们的图标尺寸/带 state，进不了原版
        ///   ItemTool.iconCache（判据要求 state 为空且尺寸 = size×50）→
        ///   每次重开箱子都全量异步重载图标。
        /// </summary>
        void Close();

        /// <summary>每帧调用（宿主 Tick）。用于关闭兜底、内容刷新等。isOpen 由宿主维护。</summary>
        void Tick(StorageUiContext ctx);

        /// <summary>数据变化时的重建钩子（引擎侧物品增删/排序后调用）。可为空实现。</summary>
        void Rebuild(StorageUiContext ctx);
    }

    /// <summary>
    /// 「右列区块」接口：实现它的 Provider 把**自己的一块面板交给宿主排版**，
    /// 于是右侧可以出现任意多块内容（箱内物品 / 蓝图 / 你的机器面板 / …），
    /// 而「附近物品」始终排在最下面。
    ///
    /// ⚠ 为什么单开一个接口、而不是给 IStorageUiProvider 加成员：
    ///   加成员 = 破坏性变更（已经实现该接口的第三方插件当场编译不过）。
    ///   标记接口 + `as` 探测，老插件一行都不用改。
    /// ⚠ 为什么排版由宿主做、而不是 Provider 自己算 y：
    ///   右列只有原版那一条滚动列（areaBox），谁自己算位置就会互相叠。
    ///   宿主按 order 从上往下累加高度，别人才可能自由搭配 ——
    ///   把某块挪到存储面板上面、再加第三块第四块，都不必猜别人占了多少。
    /// ⚠ 生命周期**完全复用** IStorageUiProvider 的 Open/Close/Tick/Rebuild：
    ///   区块也是一个面板，不该有第二套状态机（否则常驻、挂载、回退那些规矩要各维护一份）。
    ///   宿主只对 WantsRegion 为 true 的 Provider 调 Open，它下次不再出现时调 Close。
    /// </summary>
    public interface IStorageUiRegion
    {
        /// <summary>
        /// 这次打开要不要出现在区块栈里。
        /// ⚠ 必须廉价（和 CanHandle 同样的理由）：宿主只在**重算区块栈**时问
        ///   （打开界面时、以及 <see cref="StorageUiRegistry.RequestRegionsChanged"/> 之后），
        ///   不是每帧问 —— 想改判据就调那个方法，不要指望宿主每帧轮询。
        /// </summary>
        bool WantsRegion(StorageUiContext ctx);

        /// <summary>
        /// 栈内顺序，**小者在上**。存储面板自己记为
        /// <see cref="StorageUiRegistry.StorageRegionOrder"/>（= 0）：
        /// 要排在箱内物品上面给负数，下面给正数。
        /// ⚠ 宿主侧 <see cref="StorageUiRegistry.SetRegionOrder"/> 会覆盖这里的返回值 ——
        ///   这样「布局管理」类插件能统一重排，不必每个区块的作者私下商量数字。
        /// </summary>
        int RegionOrder(StorageUiContext ctx);

        /// <summary>
        /// 要挂进右列的面板。宿主每帧取一次，负责 AddChild（只在父节点变化时）、
        /// SetActive、按 order 定位，以及把整栈高度报给原版布局。
        /// ⚠ 宿主**绝不 RemoveChild**（= GameObject.Destroy，会毁掉常驻行池与已渲染图标）。
        /// 返回 null = 这一帧没有内容（该区块暂时不占位）。
        /// ⚠ 面板宽度必须用相对值（SizeScale_X）—— 像素宽度在挂载前是 0（README 坑 #18）。
        /// </summary>
        ISleekElement RegionPanel(StorageUiContext ctx);
    }

    /// <summary>
    /// 「一块能挂进右列、跨次打开常驻复用的视图面板」的统一契约。
    ///
    /// ⚠ **为什么要有它**：区块栈要的是「一个能自己挂到右列、关掉只藏不拆的面板」，
    ///   而这件事在本插件里有**两个**实现（<see cref="StorageGridPanel"/> /
    ///   <see cref="StorageListPanel"/>），第三方也可能自己画一块。
    ///   没有这层契约，<see cref="StorageUiRegionBase"/> 就只能替某一个实现服务
    ///   （或者退化成 `object` + 反射 —— 那更糟）。
    ///
    /// ⚠ **为什么是这七个成员**：它们就是「常驻区块」这套规矩**必须**问到的全部 ——
    ///   ① 要挂进树的根元素（<see cref="Element"/>）；
    ///   ② 现在建好了没有（<see cref="IsBuilt"/> —— `Tick` 的守卫）；
    ///   ③ 该占多高（<see cref="DesiredHeight"/> —— 宿主排版按它累加）；
    ///   ④ 建（<see cref="Build"/>）；
    ///   ⑤ 常驻面板还挂在**当前**挂载点上吗（<see cref="IsUsableAt"/> —— 切关/换服会重建
    ///      整个 `PlayerUI`，旧面板随旧画布销毁，但它的 `Parent` 还指着那个废掉的实例，
    ///      不比一下就穿帮，见 README §4.7）；
    ///   ⑥ 只藏不拆（<see cref="Hide"/>）/ 真丢弃（<see cref="Discard"/>）。
    ///
    /// ⚠ **加这个接口不是破坏性变更**：`StorageGridPanel` / `StorageListPanel` 本来就已经
    ///   有这七个成员（名字与语义都一样），实现接口不需要改任何调用方代码。
    /// </summary>
    public interface IStorageUiPanel
    {
        /// <summary>要挂进 Glazier 树的那个根元素（没建好时可以为 null）。</summary>
        ISleekElement Element { get; }

        /// <summary>面板是否已建好（`Build` 之后为 true）。</summary>
        bool IsBuilt { get; }

        /// <summary>本面板当前想要的高度（宿主排版按它累加）。</summary>
        float DesiredHeight { get; }

        /// <summary>建面板（**幂等** —— 反复建会作废上一代控件与已渲染图标）。</summary>
        void Build();

        /// <summary>
        /// 常驻面板是否还能接着用：必须仍挂在**当前**挂载点上。
        /// ⚠ 传 null（右列不可用）必须返回 false。
        /// </summary>
        bool IsUsableAt(ISleekElement attachSpot);

        /// <summary>只藏不拆（关闭时用它；重开时控件池与图标贴图都还在）。</summary>
        void Hide();

        /// <summary>真丢弃常驻（旧画布已销毁、或换数据源重建时）。下次 `Build` 会重头来。</summary>
        void Discard();
    }

    /// <summary>
    /// 挂载入口的**公开**包装（给「抢存储区位置」的 Provider 用）。
    ///
    /// ⚠ 为什么要有这个类：宿主 <c>StorageUiHost</c> 是 internal，内置视图在同一程序集里
    ///   能直接调它的挂载方法，**外部插件调不到**。没有这层包装的话，
    ///   「实现 IStorageUiProvider 接管存储区」这条接入路径对第三方就是假的
    ///   （编译能过，但 Open 里没法把面板挂上右列）—— README 坑 #45 / #49。
    /// 区块（<see cref="IStorageUiRegion"/>）不需要这里：宿主主动来取面板。
    /// </summary>
    public static class StorageUiPanels
    {
        /// <summary>
        /// 把面板挂进原版右列并占据「存储网格」的位置。
        /// 每帧调（在 <c>Tick</c> 里）、幂等：只在父节点变化时才 <c>AddChild</c>，
        /// 宿主**绝不 RemoveChild**（= Destroy，会毁掉常驻控件池与已渲染图标）。
        /// ⚠ 窄屏（右列不存在，见 <see cref="RightColumnAvailable"/>）时它会**收回接管**
        ///   并恢复原版存储区 —— 这是有意的，否则用户看到一片空白且无线索。
        /// </summary>
        public static void Attach(ISleekElement panel)
        {
            StorageUiHost.AttachAndPlacePanel(panel);
        }

        /// <summary>
        /// 当前挂载点（原版右列 areaBox）。Provider 用它自检「常驻的面板还活着吗」：
        /// 切关/换服会重建整个 PlayerUI，旧面板的 Parent 引用仍指向**那个废掉的**实例，
        /// 与当前挂载点一比就穿帮（只判 <c>Parent != null</c> 不够）。
        /// </summary>
        public static ISleekElement AttachSpot()
        {
            return StorageUiHost.CurrentAttachSpot();
        }

        /// <summary>
        /// 右列这一侧现在能不能用（窄屏 = 原版单列布局、右列根本不存在）。
        /// 建议在 <c>CanHandle</c> 里顺手判掉，别等 <c>Open</c> 之后挂不上才发现。
        /// </summary>
        public static bool RightColumnAvailable()
        {
            return StorageUiHost.IsWideEnoughForRightColumn();
        }

        /// <summary>
        /// ★ 26.10.3.37：**界面现在被接管着吗**（`false` = 玩家没开箱子 / 开着但走了原版）。
        ///
        /// 补的是"界面状态**无处可问**"这个缺口：以前接入方只能靠"我调了 Open 没报错"
        /// 间接猜，猜不出"界面什么时候关的"、"这次会不会轮到我"。
        /// ⚠ 它是**当下那一刻**的事实（宿主每帧维护），别缓存、别跨帧用。
        /// </summary>
        public static bool IsOpen
        {
            get { return StorageUiHost.IsOpen; }
        }

        /// <summary>
        /// ★ 26.10.3.67：**让原版存储网格与我们的界面并存**（默认 `false` = 现状：接管时把网格藏起来）。
        ///
        /// 打开后原版网格**保持可见**（玩家仍能照原版方式拖放），我们整栈下移到它下面；
        /// 高度槽位 = `原版高度 + 间隔 + 我们高度`（这是与原版布局引擎唯一的接口）。
        ///
        /// ⚠ **代价**（知道再开）：原版网格元素被拉高 ⟹ 多出来的那段会画**空槽轮廓**，
        /// 而它正好落在我们面板的位置 —— 面板底色**不透明**就完全盖住，半透明则隐约透格子。
        /// 所以这是观感取舍，默认关。改完**下一帧生效**（下一次排版时读）。
        /// </summary>
        public static bool KeepVanillaGrid
        {
            get { return StorageUiHost.KeepVanillaGrid; }
            set { StorageUiHost.KeepVanillaGrid = value; }
        }

        /// <summary>
        /// ★ 26.10.3.37：现在**占着存储槽位**的那个 Provider 的 Id（没人占时 `null`）。
        /// 配合 <see cref="IsOpen"/> 用：`IsOpen=true` 而这里不是你的 Id ⟹ 这次是别人的界面。
        /// 与 <see cref="IStorageUiProvider.Priority"/> 一起看，就能解释"为什么没轮到我"。
        /// </summary>
        public static string ActiveProviderId
        {
            get { return StorageUiHost.ActiveProviderId; }
        }

        /// <summary>
        /// ★ 26.10.3.37：某个区块**真的挂上去了吗**（按 Provider Id 查）。
        ///
        /// 为什么需要：`IStorageUiRegion.WantsRegion` 返回 true **只说明"它想挂"** ——
        /// `RegionPanel` 可能还没被调到、可能抛了异常、也可能窄屏收回了接管。
        /// 接入方想确认"我那块到底在不在"，以前只能靠 <see cref="AttachSpot"/> 自己比 Parent。
        ///
        /// ⚠ 语义 = "交出过面板、且没抛过异常"，**不是**"这一帧一定看得见"
        ///   （没内容时面板会被临时藏起来，只藏不拆）。要判可见性请自己读面板的 `IsVisible`。
        /// </summary>
        public static bool IsRegionAttached(string providerId)
        {
            return StorageUiHost.IsRegionAttached(providerId);
        }

        /// <summary>
        /// ★ 26.10.3.31：把**你自己挂上去的**那块面板从右列摘下来（<see cref="Attach"/> 的反操作）。
        ///
        /// ⚠ **只藏不拆** —— 不 `RemoveChild`（那是 `GameObject.Destroy`，会毁掉你常驻的
        ///   控件池与已经渲染好的图标贴图，这正是本项目「关界面只藏不拆」那条铁律的由来）。
        ///   下次想再用同一块面板，重新 <see cref="Attach"/> 即可，控件与贴图都还在。
        /// ⚠ 传的必须是**同一块**元素（内部比对引用）—— 免得 A 插件把 B 插件的面板摘下架。
        ///
        /// 什么时候需要它：`Open` 里已经挂了半成品、随后判定「这次接管不成立」要返回 false 时
        /// —— 宿主**不会**替你调 `Close`（见 `IStorageUiProvider.Open` 的约定），
        /// 半成品得自己拆干净。
        /// </summary>
        public static void Detach(ISleekElement panel)
        {
            StorageUiHost.DetachExternalPanel(panel);
        }

        /// <summary>
        /// ★ 26.10.3.38：往**当前界面**的标题栏顶一条临时提示（默认 6 秒后自动消失）。
        ///
        /// 补的是「**拦了动作却没法解释**」这个缺口：接入方在自己的回调里拒绝了一次操作
        /// （放不下 / 类型不对 / 权限不够……），以前只能写日志 —— 玩家看不到，
        /// 表现就是"点了没反应"。现在可以 <c>StorageUiPanels.Notify("这里只能放弹药")</c>
        /// 让标题栏当场说明原因。
        ///
        /// 语义与边界：
        /// · **没有界面开着时静默忽略**（不抛、不排队）—— 它只是"给当前画面加一句话"，
        ///   不是通知系统；玩家关掉界面后本来也无处可显示。
        /// · **同一时刻只有一条**：后发的盖住先发的（提示就该"谁后说谁显示"）。
        ///   这里**故意不做来源记名** —— 它只活几秒，记名只会让"我的提示怎么没了"更难查。
        /// · 关界面时**自动清掉**（会话状态），新会话不会看到上一轮的残留。
        /// · 与借道那句内置兜底说明**共用同一个拼接点**：公开提示优先；
        ///   两者都不在时，标题栏就只是标题。
        /// </summary>
        public static void Notify(string text)
        {
            StorageUiHost.SetNotice(text, 0f);
        }

        /// <summary>同上，但由调用方定显示秒数（&lt;= 0 视为默认 6 秒）。</summary>
        public static void Notify(string text, float seconds)
        {
            StorageUiHost.SetNotice(text, seconds);
        }

        /// <summary>★ 26.10.3.38：立刻收掉自己发的提示（幂等；没提示时调也无害）。</summary>
        public static void ClearNotice()
        {
            StorageUiHost.ClearNotice();
        }

        /// <summary>
        /// ★ 26.10.3.38：现在**真的显示着**的那条提示（没有 / 已过期时返回 `null`）。
        /// 给自检与诊断用 —— 接入方靠它确认"我那句到底有没有顶上去"。
        /// </summary>
        public static string CurrentNotice
        {
            get { return StorageUiHost.Notice; }
        }

        /// <summary>
        /// ★ 26.10.3.31：上一次**接管 / 借道**失败的原因。
        ///
        /// 配合 <see cref="StorageUiRegistry.OpenVirtualStorage"/> 用：它返回 false 时
        /// **当场读这里**就知道为什么 —— 窄屏右列不存在 / 没有本地玩家 / 候选链为空 /
        /// 每个 Provider 的 `Open` 都返回了 false / 抛了异常…… 以前这些原因只写进内部字段，
        /// 外部只拿到一个 `false`（"我调了但没反应，也不知道为什么"）。
        ///
        /// ⚠ 它是**一次性**的：每次尝试接管开始时会被清空 ⟹ **别缓存、别跨帧用**，
        /// 失败后当场读、当场记日志。
        /// ⚠ 返回 `null` = 没有记录（没尝试过，或这次尝试还没走到任何判据）。
        /// </summary>
        public static string LastOpenReject
        {
            get { return StorageUiHost.LastOpenReject; }
        }

        /// <summary>
        /// ★★ 26.10.3.69：**就地取回** —— 把 <paramref name="source"/> 这一格的物品直接搬进玩家背包，
        /// **不用切会话**（不必"点进去"关掉当前箱子）。这就是「区块面板就地可用」那条缺口的正解。
        ///
        /// ⚠⚠ **只在"自己就是权威"时可用**（单机 / 听服主机）；**联机客户端一律拒绝并给出原因**：
        ///   原版服务端只接受「**当前打开的那个容器**」的取放请求（`sendDragItem` / `sendDropItem`），
        ///   一个没被打开的容器**没有那条通道** —— 客户端改本地 `Items` 服务端不认，
        ///   结果是**凭空复制**（这是本项目的"防刷物品"红线）。
        ///
        /// <para>**三条证据**（盘点要求，缺一不可）：</para>
        /// · **扣箱**：先从源容器 `removeItem(index)` —— 失败就没得搬，直接返回；
        /// · **入包**：再 `tryAddItem`；**失败就精确还原**（源坐标与 `rot` 都记着，
        ///   `addItem(x, y, rot, item)` 放回原位）⟹ 不存在"扣了没进"的丢物；
        /// · **对账**：<paramref name="movedAmount"/> 报**实际搬走的数量**（弹药按发、普通按件），
        ///   调用方拿它跟"源容器件数 / 背包件数"的前后差值对一次 —— 数量必须守恒。
        ///
        /// <code>
        /// int moved; string why;
        /// if (StorageUiPanels.TryTakeToBackpackFrom(boxItems, x, y, out moved, out why))
        ///     MyLog("取到 " + moved + " 件");
        /// else
        ///     MyLog("取不了：" + why);      // why 里是**真实原因**，别自己另编一套
        /// </code>
        /// </summary>
        /// <returns>真的搬走了一件才返回 true。任何失败都不改动数据（已还原）。</returns>
        public static bool TryTakeToBackpackFrom(Items source, byte x, byte y,
            out int movedAmount, out string why)
        {
            movedAmount = 0;
            why = null;

            if (source == null) { why = "源容器为 null"; return false; }
            if (x >= source.width || y >= source.height)
            {
                why = "坐标越界（" + x + "," + y + " 不在 " + source.width + "×" + source.height + " 内）";
                return false;
            }

            byte index = source.getIndex(x, y);
            ItemJar jar = source.getItem(index);
            if (jar == null || jar.item == null || jar.item.id == 0) { why = "这一格没有物品"; return false; }

            Item item = jar.item;
            byte rot = jar.rot;
            ItemAsset asset = item.GetAsset();
            int sx = (asset != null) ? asset.size_x : 1;
            int sy = (asset != null) ? asset.size_y : 1;

            // ★★ 红线：联机客户端上**没有**扣这个容器的通道 ⟹ 拒绝（而不是"尽力而为"）
            if (!Provider.isServer)
            {
                why = "联机客户端不支持就地取（服务端只接受「当前打开的容器」的取放请求 —— "
                    + "扣不了这个没打开的容器，硬做会凭空复制物品）";
                return false;
            }

            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null) { why = "没有本地玩家（界面层只在客户端跑）"; return false; }

            // 找一页放得下（手上 SLOTS → 背包 BACKPACK）
            Items bag = null;
            for (byte page = 0; page <= PlayerInventory.BACKPACK; page++)
            {
                Items p = player.inventory.items[page];
                if (p == null) continue;
                byte fx, fy, fr;
                if (p.tryFindSpace((byte)sx, (byte)sy, out fx, out fy, out fr)) { bag = p; break; }
            }
            if (bag == null) { why = "背包放不下（手上与背包都没有空位）"; return false; }

            // ① 扣箱（先扣 —— 因为回滚是**精确**的：源坐标与 rot 都还在手上）
            source.removeItem(index);
            if (ReferenceEquals(source.getItem(index), jar)) { why = "源容器拒绝移除（数据层没响应）"; return false; }

            // ② 入包；失败 ⟹ **放回原处**（不存在"扣了没进"）
            if (!bag.tryAddItem(item, false))
            {
                source.addItem(x, y, rot, item);
                why = "背包放不下（已还原，物品还在原处）";
                return false;
            }

            movedAmount = item.amount;
            return true;
        }

        /// <summary>
        /// ★★ 26.10.3.71：**就地存入** —— 把**背包（手上 / 背包页）**里第 <c>(page, x, y)</c> 格的一件物品
        /// 直接放进 <paramref name="target"/> 这个容器（**不是当前打开的那个**，所以不用切会话）。
        /// 这是「区块面板就地可用」的另一半（配 <see cref="TryTakeToBackpackFrom"/> 用）。
        ///
        /// ⚠⚠ 与"取"**同一条红线**：**只在"自己就是权威"时可用**（单机 / 听服主机）；
        ///   **联机客户端一律拒绝并给出原因**（原版服务端只认「当前打开的那个容器」，
        ///   没打开的容器没有那条通道 ⟹ 硬做就是凭空复制）。
        ///
        /// <para>**三条证据**（与"取"同构）：</para>
        /// · **扣来源**（背包页）：先从背包 `removeItem(index)` —— 失败就没得搬；
        /// · **入目标**：再 `target.tryAddItem(item, false)`；**失败就精确还原**
        ///   （背包那件的坐标与 `rot` 都在手上 ⟹ `addItem(x, y, rot, item)` 放回原格）；
        /// · **对账**：<paramref name="movedAmount"/> 报实际搬走的数量。
        ///
        /// ★ 为什么"先扣来源"而不是"先入目标"：`tryAddItem` 会**自动找个空位**、不告诉你放哪了
        ///   ⟹ 想回滚就得去目标容器里**反找**那一件；而来源那格的 `(x, y, rot)` 就在手上，
        ///   放回去是确定的。两种顺序效果一样：**要么搬成功、要么原样**。
        /// </summary>
        /// <returns>真的搬走了一件才返回 true。任何失败都不改动数据（已还原）。</returns>
        public static bool TryStoreToContainer(Items target, byte page, byte x, byte y,
            out int movedAmount, out string why)
        {
            movedAmount = 0;
            why = null;

            if (target == null) { why = "目标容器为 null"; return false; }

            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null) { why = "没有本地玩家（界面层只在客户端跑）"; return false; }
            if (page >= player.inventory.items.Length || player.inventory.items[page] == null)
            {
                why = "页号 " + page + " 上没有容器（背包/手上页不可用）";
                return false;
            }

            Items bag = player.inventory.items[page];
            if (x >= bag.width || y >= bag.height)
            {
                why = "坐标越界（" + x + "," + y + " 不在 " + bag.width + "×" + bag.height + " 内）";
                return false;
            }

            byte index = bag.getIndex(x, y);
            ItemJar jar = bag.getItem(index);
            if (jar == null || jar.item == null || jar.item.id == 0) { why = "这一格没有物品"; return false; }

            Item item = jar.item;
            byte rot = jar.rot;
            ItemAsset asset = item.GetAsset();
            int sx = (asset != null) ? asset.size_x : 1;
            int sy = (asset != null) ? asset.size_y : 1;

            // ★★ 红线：与"取"同一条闸门 —— 联机客户端上**没有**往这个容器写的通道
            if (!Provider.isServer)
            {
                why = "联机客户端不支持就地存（服务端只接受「当前打开的容器」的取放请求 —— "
                    + "这个没打开的容器服务端不认，硬做会凭空复制物品）";
                return false;
            }

            // 目标放得下吗（先问，免得白扣）
            byte fx, fy, fr;
            if (!target.tryFindSpace((byte)sx, (byte)sy, out fx, out fy, out fr))
            {
                why = "目标容器放不下（没有 " + sx + "×" + sy + " 的空位）";
                return false;
            }

            // ① 扣来源（背包页）—— 先扣，因为回滚是**精确**的（源坐标与 rot 都在手上）
            bag.removeItem(index);
            if (ReferenceEquals(bag.getItem(index), jar)) { why = "背包拒绝移除（数据层没响应）"; return false; }

            // ② 入目标；失败 ⟹ **放回背包原格**（不存在"扣了没进"）
            if (!target.tryAddItem(item, false))
            {
                bag.addItem(x, y, rot, item);
                why = "目标容器放不下（已还原，物品还在背包原处）";
                return false;
            }

            movedAmount = item.amount;
            return true;
        }

        /// <summary>
        /// 把一件**已经在玩家身上（背包/装备格）的物品**托起来（走原版拖拽预览），
        /// 由玩家决定放到哪里。找不到该物品就返回 false。
        ///
        /// 用途：给**没有页号**的虚拟容器（例如工业的数字型存储箱）复刻内置手感。
        /// 因为原版拖拽的 <c>dragJar</c> 是**指向源页里那个 jar 的引用**
        /// （<c>PlayerDashboardInventoryUI.cs:1112</c>），收尾按 <c>dragFromPage</c> 归位
        /// —— 所以虚拟物品必须先落到一个真实页上才能托起。正确用法：
        /// <code>
        /// // ① 先从虚拟容器扣掉、塞进背包（插件自己的数据层，成功才算数）
        /// if (!myData.TakeOneInto(item, player)) return;
        /// // ② 再把它托起来；托不起来也无妨 —— 物品已经在背包里，不会丢
        /// StorageUiPanels.GrabFromInventory(item);
        /// </code>
        /// ⚠ **别指望它替你搬数据**：这一步只做"托起"，物品必须已经真实地在玩家身上，
        ///   否则就是凭空造出一件复制品。
        /// </summary>
        public static bool GrabFromInventory(Item item)
        {
            return GrabFromPages(item, 0, (byte)(PlayerInventory.SLOTS + PlayerInventory.BACKPACK));
        }

        /// <summary>
        /// 从**存储页**（<c>items[STORAGE]</c>）里托起指定物品。
        /// 配合 <see cref="OpenVirtualStorage"/> 用：虚拟清单里的物品点开时先塞进这个临时页，
        /// 再从这里托起 —— 这样 <c>dragJar</c> 有真实的页可指，
        /// 原版拖拽的落点、取消、信息面板全部照常工作，而清单本身**不需要有坐标语义**。
        /// </summary>
        public static bool GrabFromStorage(Item item)
        {
            return GrabFromPages(item, PlayerInventory.STORAGE, (byte)(PlayerInventory.STORAGE + 1));
        }

        private static bool GrabFromPages(Item item, byte pageFrom, byte pageTo)
        {
            if (item == null) return false;

            Player player = Player.LocalPlayer;
            if (player == null || player.inventory == null) return false;

            for (byte page = pageFrom; page < pageTo; page++)
            {
                Items items = player.inventory.items[page];
                if (items == null) continue;

                byte count = items.getItemCount();
                for (byte i = 0; i < count; i++)
                {
                    ItemJar jar = items.getItem(i);
                    if (jar == null || jar.item == null) continue;
                    // 引用相等最准（ItemJar 存的就是传进去的那个 Item 实例，不克隆）；
                    // 退一步再按 id+数量+耐久 认（有些路径会新建 Item）。
                    if (!ReferenceEquals(jar.item, item) &&
                        !(jar.item.id == item.id && jar.item.amount == item.amount
                          && jar.item.quality == item.quality)) continue;

                    return StorageUiShared.BeginGrabFrom(page, jar);
                }
            }
            return false;
        }

        // ================= 虚拟存储（插件借道） =================

        /// <summary>
        /// 打开一个**插件自管的虚拟存储**：走引擎的车后备箱通道（<c>PlayerInventory.openTrunk</c>）
        /// 把调用方给的一个 <c>Items</c> 挂到存储页上。
        ///
        /// 为什么用这条路：它是引擎里**唯一**能把"任意 <c>Items</c>"挂进原版背包界面存储位的
        /// public 入口（普通箱子要求 <c>InteractableStorage</c>，而插件自管的清单不是）。
        /// 挂上之后，右列就是真正的"页"—— 原版托起/落点/取消/信息面板全部可用，
        /// 手感与普通箱子一致。
        ///
        /// ⚠ 这个 <c>Items</c> 应当只是**中转区**（例如只放"当前正被托起的那一件"），
        ///   不要把整个清单装进去 —— 一个 <c>Items</c> 最多 200 个条目
        ///   （<c>Items.tryAddItem</c> 开头 <c>getItemCount() &gt;= 200</c> 直接返回失败，
        ///   <c>Items.cs:323</c>），而且它是二维摆放模型（会有"有空格但形状塞不进"的碎片化）。
        ///   清单本身请继续由插件按自己的语义维护，用 <see cref="GrabFromStorage"/> 逐件托起。
        /// ⚠ 借道期间 <c>isStorageTrunk == true</c>，所以宿主靠这个调用登记的标记才肯接管；
        ///   界面关闭时标记自动清除（不会让之后的真·车后备箱被误接管）。
        /// ⚠ <c>openTrunk</c> 在服务端语义下调用（单机=本地）。专用服务器客户端需要服务器配合，
        ///   与"数字箱目前是单机方案"同源，不是本入口新增的限制。
        /// </summary>
        /// <param name="items">要挂到存储页的容器（调用方持有并负责内容）。</param>
        /// <param name="title">右列标题（因为借道时取不到资产友好名）。</param>
        public static bool OpenVirtualStorage(Items items, string title)
        {
            // 老重载一律按最保守的 Relay（详见四参版：不改代码也不会突然多出可扣的材料）
            return StorageUiHost.OpenVirtual(items, title, null, StorageUiVirtualPage.Relay);
        }

        /// <summary>
        /// 同上，但**带上会话归属标记**。
        ///
        /// 为什么需要：<c>ctx.IsVirtual</c> 只说明"这一页是借来的"，不说明**是谁借的**。
        /// 装了两个都想用虚拟存储的插件时，它们的 Provider 都会去抢这个存储位
        /// （候选链里谁优先级高谁拿到，另一个连提示都没有）。
        /// <paramref name="tag"/> 会被原样放进 <see cref="StorageUiContext.Tag"/>，
        /// 调用方在自己的 <c>CanHandle</c> 里用 <c>ReferenceEquals(ctx.Tag, 自己发出去的令牌)</c>
        /// 认出"这是我这次开的"。
        /// ⚠ 本插件**不解释**这个对象，只负责原样带回 —— 与
        ///   <c>StorageUiRelay.Track</c> 的来历令牌同一套做法（跨插件共享的东西必须带来源）。
        /// </summary>
        public static bool OpenVirtualStorage(Items items, string title, object tag)
        {
            return StorageUiHost.OpenVirtual(items, title, tag, StorageUiVirtualPage.Relay);
        }

        /// <summary>
        /// 同上，再<b>声明这一页的性质</b>（<see cref="StorageUiVirtualPage"/>）。
        ///
        /// 为什么必须显式选：借道页在引擎眼里和真箱子容器<b>完全同形</b>（都挂在
        /// <c>items[STORAGE]</c> 上），本插件分不出"这一页关界面时会不会连东西一起没了"。
        /// 分不出又不能问，就只能两边都按最保守的处理 —— 那会把"借道界面里也能看配方"
        /// 这类合理需求一起砍掉。所以这个事实由<b>开会话的一方</b>交出来：
        /// · 纯中转页（数字箱那种）→ <see cref="StorageUiVirtualPage.RelayWithSections"/>
        ///   或 <see cref="StorageUiVirtualPage.Relay"/>；
        /// · 自己持久化的容器 → <see cref="StorageUiVirtualPage.Storage"/>。
        /// ⚠ 两参/三参的老重载一律按 <see cref="StorageUiVirtualPage.Relay"/>（最保守），
        ///   所以已接入的插件不改代码也不会突然多出可扣的材料。
        /// </summary>
        public static bool OpenVirtualStorage(Items items, string title, object tag,
            StorageUiVirtualPage page)
        {
            return StorageUiHost.OpenVirtual(items, title, tag, page);
        }

        /// <summary>
        /// 当前借道会话声明的页面性质。<b>不在借道会话里时返回
        /// <see cref="StorageUiVirtualPage.Storage"/></b>（= 真箱子，可以当容器用）。
        /// 给第三方区块/合成类 Provider 用，免得它们自己去碰宿主内部状态。
        /// </summary>
        public static StorageUiVirtualPage CurrentVirtualPage
        {
            get { return StorageUiHost.VirtualPage; }
        }

        /// <summary>
        /// ★ 26.10.3.37：当前借道会话的**归属令牌**（不在借道会话里时返回 `null`）。
        ///
        /// 与 <see cref="CurrentVirtualPage"/> 成对：后者说"这一页**是什么性质**"，
        /// 它说"这一页**是谁借的**"。`ctx.Tag` 只在 `Open` 时给到接入方，
        /// 关掉之后想再确认"这个存储位还是我那次会话吗"就只能问这里。
        ///
        /// ⚠ 两参/三参的 <see cref="OpenVirtualStorage(Items, string)"/> 老重载**不带 tag**
        ///   ⟹ 这里可能是 null，也可能是**别人**设的令牌。**务必用自己的令牌对象做
        ///   <c>ReferenceEquals</c> 比对**，不要只判"非空" —— 这正是让老重载的调用方
        ///   能判断出"我被按最保守的 <see cref="StorageUiVirtualPage.Relay"/> 降级了"的办法。
        /// </summary>
        public static object CurrentVirtualTag
        {
            get { return StorageUiHost.VirtualTag; }
        }

        /// <summary>关掉虚拟存储（等价于关背包界面）。</summary>
        public static void CloseVirtualStorage()
        {
            StorageUiHost.CloseVirtual();
        }

        /// <summary>当前是不是插件借道的虚拟存储开着。</summary>
        public static bool IsVirtualStorageOpen
        {
            get { return StorageUiHost.VirtualActive; }
        }

        /// <summary>
        /// 读出"手上正托着的那件物品"及其**来源页与坐标**（没在托东西时返回 false）。
        /// 接入方靠它把"托着物品点格子"这个手势安全地复用成存入：
        /// 手上这件如果就是自己刚从虚拟容器里托起来的复制品，必须**不当存入**。
        /// </summary>
        public static bool TryGetHeldItem(out Item item, out byte page, out byte x, out byte y)
        {
            return StorageUiShared.TryGetHeldItem(out item, out page, out x, out y);
        }

        /// <summary>
        /// 结束托起（只收视觉）。存入成功之后**必须调它** ——
        /// 源物品已被搬走/减量，而原版拖拽是纯视觉的，不停掉会留一个幽灵图标。
        /// </summary>
        public static void EndHeldItem()
        {
            StorageUiShared.EndGrab();
        }

        /// <summary>
        /// 这个物品是不是<b>不可分</b>的（弹匣 / 枪械 / 近战）：
        /// 一条记录 = 一个物品，<c>amount</c> 不是可拆的件数
        /// （弹匣的 amount 是备弹量，枪的 amount 恒为 1）。
        ///
        /// ⚠ 公开这个查询是为了<b>不让调用方自己重述判据</b>：
        ///   显示层（本插件的合并）与数据层（调用方的清单/扣账）必须对
        ///   「什么叫不可分」给出同一个答案，否则会出现
        ///   "显示上分成两格、扣账时却按可拆去拆条"这种两边各自成立、合起来错的行为。
        ///   （判据本身见 <c>StorageUiShared.IsIndivisible</c>：按资产类型，不看 state。）
        /// </summary>
        public static bool IsIndivisibleItem(ushort itemId)
        {
            return StorageUiShared.IsIndivisible(
                Assets.find(EAssetType.ITEM, itemId) as ItemAsset);
        }

        /// <summary>
        /// 这条物品是不是「允许并成一格」的那一类 = **内容量物品（弹匣或弹药箱）且没有 state**。
        ///
        /// 公开理由与 <see cref="IsIndivisibleItem"/> 完全相同：
        /// 显示层的并格（本插件的 <c>MergeContentPages</c>）与数据层的挑件（调用方决定
        /// "点这一格取哪一件"）**必须对同一个问题给同一个答案**，否则会出现
        /// "界面写着 6 匣、点下去只认第一条"这种各自成立、合起来错的行为。
        /// ⚠ 本方法<b>不判</b>调用方有没有开"合并显示"那个开关 —— 那是调用方的配置。
        /// ⚠ 原名 <c>IsMergeableMagazine</c>：2026-10-05 弹药箱并入同一类后改名，
        ///   因为"能不能并格"的判据是<b>行为</b>（amount 是不是可倒出去的内容量），不是资产类型。
        /// </summary>
        public static bool IsMergeableContentPage(Item item)
        {
            if (item == null) return false;
            return StorageUiShared.IsMergeableContentPage(item,
                Assets.find(EAssetType.ITEM, item.id) as ItemAsset);
        }
    }

    /// <summary>
    /// 全局注册表。其他插件在这里注册自己的 Provider。
    /// </summary>
    public static class StorageUiRegistry
    {
        private static readonly List<IStorageUiProvider> providers = new List<IStorageUiProvider>();

        /// <summary>
        /// 箱型 → 视图 的绑定（键为路障资产 GUID 的 N 形式，小写）。
        /// 用途：让别的插件指定「这种箱子用大格子、那种用列表」，
        /// 而不必自己实现一个 Provider。绑定优先于优先级排序。
        /// </summary>
        private static readonly Dictionary<string, string> boxBindings =
            new Dictionary<string, string>();

        // ---- 区块栈的宿主侧覆盖（别的插件来调「右侧显示哪几块、什么顺序」）----

        /// <summary>存储面板（当前接管存储槽位的那个视图）在区块栈里的顺序。</summary>
        public const int StorageRegionOrder = 0;

        /// <summary>
        /// 推荐默认值：排在**箱内物品**（<see cref="StorageRegionOrder"/> 那一块）**之下**
        /// （内置蓝图区块用它）。
        /// ⚠ 「箱内物品」= **我们自己的主面板**，**不是**原版右列那个存储网格 —— 这两个别混：
        ///   本常量只管**栈内**谁在谁上面；原版网格的位置由原版算（见 <see cref="KeepVanillaGrid"/>）。
        /// </summary>
        public const int RegionOrderBelowStorage = 100;

        /// <summary>
        /// 排在**箱内物品**（<see cref="StorageRegionOrder"/> 那一块）**之上**
        /// （例如机器插件想把自己的面板顶在第一位）。
        /// ⚠ 「之上」是**栈内**相对**我们自己的主面板**，**不是**"排到原版存储网格上方" ——
        ///   后者做不到：原版把网格固定在存储行顶，我们只能改它的**高度**、改不了它的 y
        ///   （`updateBoxAreas` 是我们的 Prefix 补丁，我们写的网格 y 会被它覆盖）。
        ///   ⟹ 打开 <see cref="KeepVanillaGrid"/>（并存）时，**整个栈**仍在原版网格**下方**，
        ///     而本常量只决定栈**内部**的先后。
        /// </summary>
        public const int RegionOrderAboveStorage = -100;

        /// <summary>Id → 顺序覆盖。存在即优先于 Provider 自己的 RegionOrder。</summary>
        private static readonly Dictionary<string, int> regionOrders =
            new Dictionary<string, int>();

        /// <summary>
        /// 被强制关掉的区块：<b>区块 Id → 是谁关的</b>。
        /// ⚠ 必须按来源分开记，不能用一个全局名单 —— 那样 A 插件关掉某块、
        ///   B 插件「恢复」一下就把它放出来了（B 从没关过它，它凭什么有权放）。
        ///   与 <c>StorageBlueprintFilters</c> 按来源命名是同一条道理（README 坑 #48 / #50）。
        /// </summary>
        private static readonly Dictionary<string, HashSet<string>> hiddenBySource =
            new Dictionary<string, HashSet<string>>();

        /// <summary>注册一个界面提供者。重复 Id 会替换旧的。</summary>
        public static void Register(IStorageUiProvider provider)
        {
            if (provider == null) return;
            try
            {
                Unregister(provider.Id);
                providers.Add(provider);
                // 按优先级降序，保证遍历时第一个就是最高优先级
                Resort();
                CustomUIPlugin.Log("已注册界面提供者：" + provider.Id + "（优先级 " + provider.Priority + "）");
                // 运行中新注册的区块要立刻出现在正开着的界面上（否则得等玩家重开箱子）
                RequestRegionsChanged();
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("注册界面提供者失败：" + e);
            }
        }

        /// <summary>
        /// 按**当前**优先级重排。
        ///
        /// ⚠ 为什么单独暴露：内置视图的优先级是配置算出来的
        ///   （DefaultView → 10/0/11），而 PluginManager 改配置发生在运行时。
        ///   注册时排过一次就再不重排的话，`FindCandidates` 仍按注册那一刻的旧顺序找
        ///   → 「在插件管理里点了切换，界面却没变」。
        /// </summary>
        public static void Resort()
        {
            providers.Sort(delegate (IStorageUiProvider a, IStorageUiProvider b)
            {
                return b.Priority.CompareTo(a.Priority);
            });
        }

        /// <summary>按 Id 注销。</summary>
        public static void Unregister(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            bool removed = false;
            for (int i = providers.Count - 1; i >= 0; i--)
            {
                if (providers[i] != null && providers[i].Id == id)
                {
                    // ⚠ 先让宿主关掉它（区块的面板是常驻的，直接丢引用会留下一块
                    //   挂在右列上的死面板）。宿主只在它正显示时才 Close。
                    StorageUiHost.DropProvider(id);
                    providers.RemoveAt(i);
                    removed = true;
                }
            }
            if (removed) RequestRegionsChanged();
        }

        /// <summary>
        /// 清空（插件卸载时用）。
        ///
        /// ★ 26.10.3.37：**先逐个 <see cref="StorageUiHost.DropProvider"/>，再清四个集合** ——
        /// 对齐 <see cref="Unregister(string)"/> 的做法。原实现只 `providers.Clear()`，
        /// 于是右列会留下**没人驱动的死面板**：宿主仍以为有主，而那些面板既不会被收走
        /// 也不会再被刷新（本项目"关界面只藏不拆"的铁律让它们连销毁都不会发生）。
        /// 内置三个视图也在 <c>providers</c> 里，一并走正常的退役路径。
        /// </summary>
        public static void Clear()
        {
            for (int i = 0; i < providers.Count; i++)
            {
                IStorageUiProvider p = providers[i];
                if (p != null) StorageUiHost.DropProvider(p.Id);
            }
            providers.Clear();
            boxBindings.Clear();
            regionOrders.Clear();
            hiddenBySource.Clear();
            RequestRegionsChanged();
        }

        // ================= 区块栈：显示哪几块、什么顺序（外部可动态调） =================

        /// <summary>
        /// 覆盖某个区块在右列栈里的顺序（小者在上，箱内物品 = <see cref="StorageRegionOrder"/>）。
        /// 对内置视图同样有效 —— 例如 <c>SetRegionOrder("customui.blueprintview", -100)</c>
        /// 把蓝图列表挪到箱内物品**上面**。
        /// ⚠ 顺序是宿主**每帧现读**的，所以改完下一帧就生效，不必重开界面、也不必
        ///   <see cref="RequestRegionsChanged"/>。那个方法只在「某块要不要出现」变了时才需要
        ///   （<c>WantsRegion</c> 不是每帧问的）。
        /// </summary>
        public static void SetRegionOrder(string id, int order)
        {
            if (string.IsNullOrEmpty(id)) return;
            regionOrders[id] = order;
        }

        /// <summary>撤销顺序覆盖，交回区块自己的 RegionOrder。</summary>
        public static void ClearRegionOrder(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            regionOrders.Remove(id);
        }

        /// <summary>有没有顺序覆盖（布局类插件做「恢复默认」时用）。</summary>
        public static bool HasRegionOrderOverride(string id)
        {
            return !string.IsNullOrEmpty(id) && regionOrders.ContainsKey(id);
        }

        /// <summary>
        /// 强制隐藏/恢复某个区块（<b>按来源记名</b>）。<c>visible = false</c> = 不显示
        /// （宿主连 <c>WantsRegion</c> 都不再问它）。
        /// ⚠ <c>source</c> 填调用方自己的标识（建议用插件 GUID）。恢复只会撤销**自己**那份
        ///   隐藏标记 —— 别的插件关着的东西不会被你顺手放出来（README 坑 #50）。
        /// ⚠ <c>true</c> 只是「撤销自己这次的强制隐藏」，**不能**替 Provider 决定出现 ——
        ///   要不要出现只有它自己知道自己有没有内容可显示。
        /// </summary>
        public static void SetRegionVisible(string source, string id, bool visible)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (string.IsNullOrEmpty(source)) source = DefaultSource;

            HashSet<string> sources;
            if (!hiddenBySource.TryGetValue(id, out sources))
            {
                if (visible) return;            // 没人关过，撤销个空
                sources = new HashSet<string>();
                hiddenBySource[id] = sources;
            }

            if (visible) sources.Remove(source);
            else sources.Add(source);

            if (sources.Count == 0) hiddenBySource.Remove(id);
            RequestRegionsChanged();
        }

        /// <summary>撤销某个来源对**所有**区块的强制隐藏（插件卸载时用）。</summary>
        public static void ClearRegionVisibility(string source)
        {
            if (string.IsNullOrEmpty(source)) return;
            bool changed = false;
            List<string> drop = new List<string>();
            foreach (KeyValuePair<string, HashSet<string>> kv in hiddenBySource)
            {
                if (kv.Value.Remove(source)) changed = true;
                if (kv.Value.Count == 0) drop.Add(kv.Key);
            }
            for (int i = 0; i < drop.Count; i++) hiddenBySource.Remove(drop[i]);
            if (changed) RequestRegionsChanged();
        }

        /// <summary>这个区块有没有被**任何**来源强制关掉。</summary>
        public static bool IsRegionHidden(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            HashSet<string> sources;
            return hiddenBySource.TryGetValue(id, out sources) && sources.Count > 0;
        }

        /// <summary>
        /// 是谁把这个区块关掉的（排查「这块怎么不见了」用 —— 没有这行日志就只能猜）。
        /// 返回空列表 = 没被强制关过，那它没出现就是 Provider 自己的 <c>WantsRegion</c> 说了不要。
        /// </summary>
        public static List<string> RegionHidingSources(string id)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(id)) return list;
            HashSet<string> sources;
            if (!hiddenBySource.TryGetValue(id, out sources)) return list;
            foreach (string s in sources) list.Add(s);
            return list;
        }

        /// <summary>没给来源时的兜底标识（内置视图用的就是它）。</summary>
        public const string DefaultSource = "customui";

        /// <summary>
        /// 请宿主重算右列区块栈（下一帧生效）。
        /// 什么时候要调：改了 WantsRegion 的判据、改了顺序覆盖、注册/注销了区块、
        /// 或者机器状态变了想让某块出现/消失。
        /// ⚠ 幂等且廉价（只是置一个标记），所以不确定要不要调时尽管调。
        /// </summary>
        public static void RequestRegionsChanged()
        {
            StorageUiHost.RequestRegionRefresh();
        }

        /// <summary>
        /// 所有实现了 <see cref="IStorageUiRegion"/> 的 Provider Id（给设置界面列清单用）。
        /// 不含当前是否显示 —— 那是每次打开按 WantsRegion 现问的。
        /// </summary>
        public static List<string> RegionIds()
        {
            List<string> ids = new List<string>();
            for (int i = 0; i < providers.Count; i++)
            {
                IStorageUiProvider p = providers[i];
                if (p == null) continue;
                if (!(p is IStorageUiRegion)) continue;
                if (!string.IsNullOrEmpty(p.Id) && !ids.Contains(p.Id)) ids.Add(p.Id);
            }
            return ids;
        }

        // ================= 箱型绑定（外部插件按箱型选视图） =================

        /// <summary>
        /// 把某种路障资产绑定到指定视图。例如：
        /// <code>
        /// StorageUiRegistry.BindBox(myChestAsset.GUID, "customui.gridview");
        /// </code>
        /// 绑定优先于优先级排序；找不到该 Provider（或它 CanHandle 返回 false）时
        /// 自动退回普通优先级流程，不会把箱子搞成"没有界面"。
        /// </summary>
        public static void BindBox(Guid assetGuid, string providerId)
        {
            if (assetGuid == Guid.Empty || string.IsNullOrEmpty(providerId)) return;
            boxBindings[Key(assetGuid)] = providerId;
            CustomUIPlugin.Log("箱型绑定：" + assetGuid.ToString("N") + " → " + providerId);
        }

        /// <summary>解除某个路障资产的绑定。</summary>
        public static void UnbindBox(Guid assetGuid)
        {
            if (assetGuid == Guid.Empty) return;
            boxBindings.Remove(Key(assetGuid));
        }

        // ================= ★ 让"某些箱子保持原版"成为一件事（26.10.3.28） =================

        /// <summary>
        /// 保留的视图 Id：**声明某种箱型保持原版界面、本插件不接管它**。
        /// <code>
        /// StorageUiRegistry.BindBox(myChestAsset.GUID, StorageUiRegistry.VanillaViewId);
        /// </code>
        /// 撤销方式与别的绑定一样：<see cref="UnbindBox"/>。
        /// 玩家也可以不写插件、直接在 `[General] VanillaBoxes` 里按资产名/GUID 配。
        ///
        /// ⚠ 为什么需要它：本插件默认接管**所有** `EItemType.STORAGE` 路障（原版共 46 种）。
        ///   但"作为前置"时经常是**只需要在某几种箱子上用自绘界面**，其余箱子保持原版才对
        ///   —— 原版手感、别的插件依赖原版网格、玩家自己认得。没有这条路时唯一的"不接管"
        ///   办法是 `Enabled = false`，而那是**全局**的，跟"只有某几种箱子回原版"是两件事。
        ///
        /// ⚠ 它**不是**"绑定到一个名叫 vanilla 的 provider"：绑定若失效（Provider 注销 /
        ///   `CanHandle` 返回 false），`FindCandidates` 会**继续走优先级流程**，
        ///   结果反而把箱子接管了 —— 与声明的意思正好相反。所以它在候选链上是一条
        ///   **短路**（直接返回空链 = 宿主按"没人认领"处理 = 回退原版网格）。
        /// </summary>
        public const string VanillaViewId = "customui.vanilla";

        /// <summary>
        /// 这个箱型是否被声明为「保持原版、本插件不接管」。
        /// 等价于 <c>IsBoundTo(guid, VanillaViewId)</c>，单独开一个是为了让调用方的意图可读。
        /// </summary>
        public static bool IsBoxVanilla(Guid assetGuid)
        {
            if (assetGuid == Guid.Empty) return false;
            string bound;
            return boxBindings.TryGetValue(Key(assetGuid), out bound) && bound == VanillaViewId;
        }

        /// <summary>
        /// 某种箱子是否被绑定到指定视图。
        ///
        /// 为什么要把绑定关系开放出来：注册表命中绑定时**不会**告诉 Provider
        /// 「你是被绑来的」，而 Provider 的 CanHandle 又要自己决定接不接受。
        /// 「默认不启用、只对特定箱型开放」的视图（内置蓝图视图就是）需要这个查询。
        /// </summary>
        public static bool IsBoundTo(Guid assetGuid, string providerId)
        {
            if (assetGuid == Guid.Empty || string.IsNullOrEmpty(providerId)) return false;
            string bound;
            return boxBindings.TryGetValue(Key(assetGuid), out bound) && bound == providerId;
        }

        private static string Key(Guid assetGuid)
        {
            return assetGuid.ToString("N").ToLowerInvariant();
        }

        /// <summary>按 Id 找 Provider（绑定用）。</summary>
        public static IStorageUiProvider GetById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < providers.Count; i++)
            {
                IStorageUiProvider p = providers[i];
                if (p != null && p.Id == id) return p;
            }
            return null;
        }

        /// <summary>
        /// 找到第一个能处理该上下文的 Provider。找不到返回 null（宿主将回退原版）。
        /// 顺序：**箱型绑定** → 优先级降序。
        ///
        /// ⚠ 宿主接管流程实际用的是 <see cref="FindCandidates"/>（Open 失败要能退下一个）。
        /// 本方法保留给外部插件做「这块箱子归谁」的查询。
        /// </summary>
        public static IStorageUiProvider Find(StorageUiContext ctx)
        {
            List<IStorageUiProvider> candidates = FindCandidates(ctx);
            return candidates.Count > 0 ? candidates[0] : null;
        }

        /// <summary>
        /// 找「右列区块」候选：按优先级顺序，列出所有实现了 <see cref="IStorageUiRegion"/>、
        /// 且 WantsRegion 为 true 的 Provider（被 SetRegionVisible(id, false) 关掉的、
        /// 以及当前正占存储槽位的那个都不收）。
        /// ⚠ 与 <see cref="FindCandidates"/> 同样做异常隔离：某个 Provider 判据炸了
        ///   不该连累别人的区块显示。
        /// </summary>
        public static List<IStorageUiProvider> FindRegions(StorageUiContext ctx, IStorageUiProvider main)
        {
            List<IStorageUiProvider> result = new List<IStorageUiProvider>();

            // ★ 借道会话里"这一页**要不要挂区块**"是接入方声明的事实（见 StorageUiVirtualPage）。
            //   声明成纯中转页时**整栈区块都不挂载** —— 闸口放在这个唯一的汇合点上，
            //   而不是要求每个区块自己记得判（漏一个就是一个静默骗人的面板）。
            //   ★ 26.10.3.40：判据收口到 `ctx.WantsRegions`，**不在这里自己写 `== Relay`**
            //   （那样每个消费方各记一条，改口径时必漏）。
            if (ctx != null && ctx.IsVirtual && !ctx.WantsRegions) return result;

            for (int i = 0; i < providers.Count; i++)
            {
                IStorageUiProvider p = providers[i];
                if (p == null || ReferenceEquals(p, main)) continue;

                IStorageUiRegion asRegion = p as IStorageUiRegion;
                if (asRegion == null) continue;

                if (IsRegionHidden(p.Id)) continue;

                try
                {
                    if (asRegion.WantsRegion(ctx)) result.Add(p);
                }
                catch (Exception e)
                {
                    CustomUIPlugin.Error("Provider " + p.Id + " 的 WantsRegion 抛异常，已跳过：" + e.Message);
                }
            }
            return result;
        }

        /// <summary>
        /// 某个区块**当前生效**的栈内顺序：宿主侧覆盖值优先，其次问 Provider 自己，
        /// 判据抛异常时回落到 <see cref="StorageRegionOrder"/>
        /// （宁可排错位置，也不要因为一个数字取不到就把整块内容弄没）。
        /// </summary>
        public static int EffectiveRegionOrder(IStorageUiProvider provider, StorageUiContext ctx)
        {
            if (provider == null) return StorageRegionOrder;

            int overridden;
            if (regionOrders.TryGetValue(provider.Id, out overridden)) return overridden;

            IStorageUiRegion asRegion = provider as IStorageUiRegion;
            if (asRegion == null) return StorageRegionOrder;

            try
            {
                return asRegion.RegionOrder(ctx);
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("Provider " + provider.Id + " 的 RegionOrder 抛异常，按默认位置排：" + e.Message);
                return StorageRegionOrder;
            }
        }

        /// <summary>
        /// 按顺序列出**所有**能处理该上下文的 Provider（候选链），供宿主依次尝试打开。
        ///
        /// 为什么不能只给一个：宿主拿到 Provider 之后还要调 <c>Open</c>，而 Open 可能失败
        /// （蓝图视图要扫全资产表、调一堆引擎 API，是三个内置视图里最容易在别人机器上
        /// 出岔的一个）。只给一个的话，它 Open 一失败就没人接了 → 玩家直接看到原版网格，
        /// 而不是「退回大格子/列表」。所以这里把整条链交出去，由宿主逐个试、失败就换下一个。
        ///
        /// ⚠ 行为变化：链上每个 Provider 的 <c>CanHandle</c> 每次打开都会被问一次
        /// （以前命中即停、后面的不会被问）。CanHandle 按接口约定必须是廉价判据。
        /// </summary>
        public static List<IStorageUiProvider> FindCandidates(StorageUiContext ctx)
        {
            List<IStorageUiProvider> result = new List<IStorageUiProvider>();

            string boundId = null;
            bool hasBinding = ctx != null && ctx.Asset != null &&
                boxBindings.TryGetValue(Key(ctx.Asset.GUID), out boundId);

            // ⓪ ★ 「这个箱型保持原版」—— 最高优先级的**短路**，返回**空候选链**。
            //   宿主的既有行为是"候选链为空 → 不接管、回退原版网格"（TryOpen 本来就要处理
            //   "一个 Provider 都没有"这种情况），所以这里不需要新增接管分支，
            //   也不会漏掉什么清理 —— 根本没开始接管。
            //   ⚠ 短路必须在这里，而不是"绑一个假的 provider"：绑定失效时下面 ① 会放它过去、
            //     ② 的优先级流程照样把箱子接管掉，与声明的意思正好相反。
            if (hasBinding && boundId == VanillaViewId) return result;

            // ① 箱型绑定优先
            if (hasBinding)
            {
                IStorageUiProvider bound = GetById(boundId);
                // 绑定失效（已注销 / 不接受该上下文）→ 继续走下面的优先级流程
                if (bound != null && Accepts(bound, ctx)) result.Add(bound);
            }

            // ② 优先级降序（绑定那个已收进来的不重复收，否则会白试一次 Open）
            for (int i = 0; i < providers.Count; i++)
            {
                IStorageUiProvider p = providers[i];
                if (p == null || result.Contains(p)) continue;
                if (Accepts(p, ctx)) result.Add(p);
            }
            return result;
        }

        /// <summary>CanHandle 判据 + 异常隔离：单个 Provider 出问题不能拖垮整条链。</summary>
        private static bool Accepts(IStorageUiProvider p, StorageUiContext ctx)
        {
            try
            {
                return p.CanHandle(ctx);
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("Provider " + p.Id + " 的 CanHandle 抛异常，已跳过：" + e.Message);
                return false;
            }
        }

        internal static int Count { get { return providers.Count; } }
    }
}
