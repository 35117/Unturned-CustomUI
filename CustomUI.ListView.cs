// ============================================================================
//  CustomUI · 内置「列表视图」Provider（StorageListPanel 的薄封装）
// ============================================================================
//  作用有两个：
//    1) 作为「测试机械」的可视化验证（确认接管链路通了）
//    2) 作为第三方 Provider 的参考实现
//
//  ★ 26.10.3.32（T9）：面板本体已提成**公开可复用组件** `StorageListPanel`
//    （见 CustomUI.ListPanel.cs）—— 此前它也长在这个文件里，但面板/行池全 private、
//    且没有 `Element`/`IsBuilt`/`DesiredHeight` ⟹ 第三方拿到本类实例也**挂不上**
//    `IStorageUiRegion.RegionPanel`（2026-10-05《能力缺口盘点》§1 那条结构性缺口）。
//    ⟹ 本类现在只负责三件事：认领上下文、把数据源喂给面板、每帧把它挂到原版右列。
//    第三方要做自己的列表区块，**直接用 `StorageListPanel`**，不必再借道这里。
//
//  形态：一行一件 —— 左图标、右文字（名字 · 数量 · 耐久），鼠标悬浮出 Tooltip。
//
//  交互（面板里走的都是共享层，因此单机/联机行为一致）：
//    · 左键点击  → 取出到背包（tryFindSpace 找空位 + sendDragItem）
//    · 右键点击  → 原版物品信息面板（反射 openSelection，完整复用原版 UI）
//    · Ctrl+左键 → 原版直接丢弃（sendDropItem）
//
//  ⚠ 为什么「取出」用 sendDragItem 而不是直接改 items[]：
//     引擎的 items 改动要走网络同步。sendDragItem 是原版自己用的通道，
//     客户端发请求→服务端执行→回同步，单机时自动本地执行。直接改数组在
//     联机客户端上不会生效（服务端不知道）。
// ============================================================================
using System;
using SDG.Unturned;

namespace CustomUI
{
    /// <summary>内置列表视图。优先级 0，第三方可用更高优先级覆盖。
    /// ★ 26.10.3.32 起只是 <see cref="StorageListPanel"/> 的薄封装。</summary>
    public class StorageListView : IStorageUiProvider
    {
        public string Id { get { return "customui.listview"; } }
        public int Priority { get { return currentPriority; } }

        /// <summary>由 CustomUIPlugin 按配置项设置（决定两视图谁是默认）。</summary>
        internal static int currentPriority = 0;

        private readonly StorageListPanel list = new StorageListPanel();

        public StorageListView()
        {
            list.LogName = "列表";
            // 列表区最大高度：超出后**列表内部自己滚动** ——
            // 否则条目一多，面板会把「附近物品」区挤到很下面（实测反馈「上下高度太高了」）。
            list.MaxListHeight = 510f;   // 340 × 1.5（按实测要求放大）
            // ★ 26.10.3.29：列表视图也开搜索栏（与内置大格子视图同款）。
            //   必须在 Build 之前设 —— ShowSearch 的 setter 会重建面板（输入框是 Build 里建的）。
            list.ShowSearch = true;
        }

        // ================= IStorageUiProvider =================

        public bool CanHandle(StorageUiContext c)
        {
            // 内置视图处理「引擎储物」的全部情况。
            // ⚠ 判据用 Container（权威数据源），不要用 Storage —— 联机客户端上
            //   Storage 为 null，用它会漏掉所有联机场景。
            return c != null && c.Container != null;
        }

        public bool Open(StorageUiContext c)
        {
            try
            {
                // 常驻面板仍挂在当前挂载点上 → 复用（行池与图标贴图原样留用）；
                // 否则丢掉常驻重新 Build。见 StorageListPanel.Hide 的注释。
                if (!list.IsUsableAt(StorageUiHost.CurrentAttachSpot())) list.Discard();
                list.Build();
                list.Refresh(c.Container);
                return true;
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("列表视图打开失败：" + e);
                list.Discard();   // 宿主不会再调 Close，半成品必须自己拆干净
                return false;
            }
        }

        public void Close()
        {
            // 只藏不拆（详见 StorageListPanel.Hide）—— 面板与行池跨次打开常驻复用。
            list.Hide();
        }

        public void Tick(StorageUiContext c)
        {
            if (!list.IsBuilt) return;

            // ⚠ 顺序必须是「先重建 → 后挂载定位」，不能反过来：
            //   重建会改写面板高度（行数变了就变高），而挂载定位要用这个高度去扩
            //   原版滚动区的 ContentSizeOffset。若先挂载再重建，首帧拿到的是旧高度
            //   → 面板长高了但滚动范围没跟上 → 底部滚不到。
            if (c != null && c.Container != null) list.Refresh(c.Container);

            // 挂载 + 定位。必须每帧调用：
            //   · 挂载本身幂等（Host 内部按「当前父节点」判断是否需重挂）
            //   · 定位需每帧刷新 —— 原版「附近物品」区的高度会随地上物品变化，
            //     面板要跟着它移动，否则会与上方内容重叠
            //   （首次挂到 Glazier 树之后的重建由 StorageListPanel.RefreshCore 负责）
            StorageUiHost.AttachAndPlacePanel(list.Element);
        }

        /// <summary>数据变化时的重建钩子：交给面板按指纹自己判断要不要真重建。</summary>
        public void Rebuild(StorageUiContext c)
        {
            if (!list.IsBuilt || c == null || c.Container == null) return;
            list.Refresh(c.Container);
        }
    }
}
