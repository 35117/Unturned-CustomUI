// ============================================================================
//  StorageUiRegionBase —— 「常驻右列区块」的样板基类（26.10.3.60 新增）
//
//  背景：往右列区块栈加一块面板，**必须**守住四条规矩。它们每一条都有踩坑记录，
//  但在接入方那边是**一模一样**的四段样板 —— 探针自己就抄了四遍（四个区块类）。
//
//    ① **只藏不拆**：`Close()` 只能 `IsVisible = false`。`RemoveChild` 末尾是
//       `GameObject.Destroy`，会把行池与已渲染图标一起销毁（坑 #15/#38，README §4.7）。
//    ② **判代**：`Open()` 里必须先问「常驻面板还挂在**当前**挂载点上吗」——
//       切关/换服会重建整个 `PlayerUI`，旧面板的 `Parent` 还指着那个**废掉的**实例，
//       非空且看起来还在（README §4.7）。
//    ③ **`Tick` 要有 `IsBuilt` 守卫**：面板还没建好时刷新会读到空控件。
//    ④ **`Open` 失败要自己拆半成品**：宿主在返回 false 后**不会**调 `Close`。
//
//  这个基类把那四段收口，接入方只需要回答三个问题：
//    · `Id`（标识）、`Panel`（哪块面板）、`Refresh`（拿什么填内容）
//    · 可选覆写 `WantsRegion` / `RegionOrder` / `Priority`
//
//  ⚠ 为什么不把 `WantsRegion` / `RegionOrder` / `Id` 也固定死：它们**每块都不同**
//    （判据不同、顺序不同），收死等于强制所有区块长一样。
//  ⚠ 为什么 `CanHandle` 恒 false 可以固定：区块**不该抢存储位**。真要接管存储区，
//    那是另一条路（普通 Provider + `StorageUiPanels.Attach`），两者互斥（README §4.9 第 4 条）。
//
//  ★ 与另几个公开组件同一套生命周期：`Build()` 只建一次（幂等），`Close()` 只藏不拆，
//    明确不要了才 `Discard()`。
// ============================================================================
using System;
using SDG.Unturned;

namespace CustomUI
{
    /// <summary>
    /// 「常驻右列区块」的样板基类。继承它 = 白拿「只藏不拆 / 判代 / `IsBuilt` 守卫 /
    /// 失败自拆」这四条规矩，你只管画自己的面板。
    ///
    /// 典型用法：
    /// <code>
    /// internal class MyMachineRegion : StorageUiRegionBase
    /// {
    ///     private readonly StorageListPanel list = new StorageListPanel();
    ///
    ///     public override string Id { get { return "mymod.machine"; } }
    ///     protected override IStorageUiPanel Panel { get { return list; } }
    ///     public override int RegionOrder(StorageUiContext c) { return 200; }   // 排在存储面板下面
    ///     public override bool WantsRegion(StorageUiContext c) { return IsMyMachineOpen; }
    ///     protected override void Refresh(StorageUiContext c) { list.Refresh(slots); }
    /// }
    /// StorageUiRegistry.Register(new MyMachineRegion());
    /// </code>
    /// ⚠ `WantsRegion` 变了要自己喊 `StorageUiRegistry.RequestRegionsChanged()` ——
    ///   宿主**不每帧轮询**判据（接口注释里写明）。
    /// </summary>
    public abstract class StorageUiRegionBase : IStorageUiProvider, IStorageUiRegion
    {
        /// <summary>唯一标识（建议 "作者.插件名" 形式）。</summary>
        public abstract string Id { get; }

        /// <summary>
        /// 要挂进右列的那一块面板。
        /// ⚠ 通常是 `readonly` 字段（常驻复用）—— **不要**在 getter 里每次都 new。
        /// </summary>
        protected abstract IStorageUiPanel Panel { get; }

        /// <summary>
        /// 优先级。⚠ 区块的 <see cref="CanHandle"/> 恒为 false（不抢存储位），
        /// 所以这个值**实际不会被用到** —— 留着只是为了满足接口。
        /// </summary>
        public virtual int Priority { get { return 0; } }

        /// <summary>
        /// 这次打开要不要出现在区块栈里。默认 **true**（被注册的区块当然是要显示的）。
        /// ⚠ 必须廉价：宿主只在**重算区块栈**时问（打开界面时 + `RequestRegionsChanged()` 之后），
        ///   不是每帧问 —— 改了判据要自己喊那个方法。
        /// </summary>
        public virtual bool WantsRegion(StorageUiContext ctx) { return true; }

        /// <summary>栈内顺序，**小者在上**。默认 0（= 存储面板那一格）。常量见 `StorageRegionOrder` 一族。</summary>
        public virtual int RegionOrder(StorageUiContext ctx) { return 0; }

        /// <summary>
        /// 内容刷新 —— `Open` / `Tick` / `Rebuild` 三条路**都汇到这里**。
        /// 默认空实现（有些区块只有静态内容）。
        /// ⚠ 高度不用自己算：改你面板的 `DesiredHeight` 背后的东西，宿主下一帧排版时
        ///   会按新高度累加（README §4.9 关键一节）。
        /// </summary>
        protected virtual void Refresh(StorageUiContext ctx) { }

        // ================= IStorageUiProvider（基类替你实现） =================

        /// <summary>
        /// ⚠ **恒 false** —— 区块**不接管存储位**。要接管存储区请另写一个普通
        /// <see cref="IStorageUiProvider"/> 并用 <see cref="StorageUiPanels.Attach"/> 挂面板
        /// （两者互斥：同一条内容既占存储位又出现在栈里 = 显示两遍）。
        /// </summary>
        public bool CanHandle(StorageUiContext ctx) { return false; }

        /// <summary>
        /// 判代 → 建 → 刷新。全程异常隔离（失败返回 false 让宿主换下一个候选）。
        /// </summary>
        public bool Open(StorageUiContext ctx)
        {
            IStorageUiPanel p = Panel;
            if (p == null) return false;

            try
            {
                // ★ 判代：旧画布已销毁时，面板的 Parent 仍指着废掉的挂载点 —— 一比就穿帮。
                if (!p.IsUsableAt(StorageUiPanels.AttachSpot())) p.Discard();
                p.Build();          // 幂等：常驻复用时它直接 return
                Refresh(ctx);
                return true;
            }
            catch (Exception e)
            {
                // ⚠ 宿主在返回 false 后**不会**调 Close（接口契约）⟹ 自己拆掉半成品。
                CustomUIPlugin.Error("区块 " + Id + " 打开失败：" + e);
                try { p.Discard(); } catch (Exception) { }
                return false;
            }
        }

        /// <summary>只藏不拆（**绝不** `RemoveChild` —— 那会连行池与已渲染图标一起销毁）。</summary>
        public void Close()
        {
            IStorageUiPanel p = Panel;
            if (p == null) return;
            try { p.Hide(); } catch (Exception) { }
        }

        /// <summary>每帧刷新（宿主的 Tick 外面已经有 try/catch）。</summary>
        public void Tick(StorageUiContext ctx)
        {
            IStorageUiPanel p = Panel;
            if (p == null || !p.IsBuilt) return;     // ★ 守卫：没建好时刷新会读到空控件
            Refresh(ctx);
        }

        /// <summary>数据变化后的重建钩子（与 <see cref="Tick"/> 同一个出口）。</summary>
        public void Rebuild(StorageUiContext ctx)
        {
            IStorageUiPanel p = Panel;
            if (p == null || !p.IsBuilt) return;
            Refresh(ctx);
        }

        // ================= IStorageUiRegion =================

        /// <summary>
        /// 交给宿主的元素。**没建好时返回 null** 是合法的（接口约定：这一帧不占位）。
        /// </summary>
        public ISleekElement RegionPanel(StorageUiContext ctx)
        {
            IStorageUiPanel p = Panel;
            return (p == null) ? null : p.Element;
        }
    }
}
