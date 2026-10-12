// ============================================================================
//  CustomUI · 内置「大格子视图」Provider
// ============================================================================
//  形态：一行 4 格。每格「上图标、下名称、再下数量/耐久」，
//        格子尺寸**固定**（不随物品实际占用格数变化），鼠标悬浮显示描述。
//
//  ⚠ 本文件现在**只剩 Provider 的生命周期与挂载**：
//     网格本身（行池、epoch、按序号绑定、整格重建、限高滚动、指纹）全部在
//     <see cref="StorageGridPanel"/>（CustomUI.GridPanel.cs）里，因为那套机制
//     跟"接管存储区"没有必然关系 —— 别的插件要画自己的一块网格也必须能用它。
//     ⟹ 一份实现，两处使用。复制一份池子逻辑 = 两份认领规则，迟早漂移（坑 #20/#53）。
//
//  与列表视图的关系：
//     两者是**并列**的内置 Provider，同一套数据层与交互层（StorageUiShared），
//     只有布局不同。顺序也由共享层统一决定，切换视图不会「跳位置」。
//
//  ⚠ 改布局前请先读 CustomUI.GridPanel.cs 文件头那份"踩坑清单"，
//     那里每条都对应一次实测。
// ============================================================================
using System;
using SDG.Unturned;

namespace CustomUI
{
    /// <summary>内置大格子视图。默认优先级由插件配置决定。</summary>
    public class StorageGridView : IStorageUiProvider
    {
        public string Id { get { return "customui.gridview"; } }
        public int Priority { get { return currentPriority; } }

        /// <summary>由 CustomUIPlugin 按配置项设置（决定两视图谁是默认）。</summary>
        internal static int currentPriority = 0;

        /// <summary>网格本体（公开组件）。本视图只是它的一层 Provider 封装。</summary>
        private readonly StorageGridPanel grid = new StorageGridPanel();

        private StorageUiContext ctx;

        public StorageGridView()
        {
            // 与改造前逐项同款：一行 4 格、上限 510、无前缀、日志仍叫「大格子」。
            // ⚠ Interactive 保持 true，但本视图的数据源就是**当前打开的那个容器**，
            //   所以组件内部的页号校验会放行（换到别的 Items 会自动降级为只读）。
            grid.LogName = "大格子";
            grid.Interactive = true;
            // ★ 26.10.3.29：内置大格子视图**开搜索栏**（占一行，在头部之下）。
            //   ⚠ 必须在 Build() 之前设 —— 输入框是 Build 里建的（见 ShowSearch 的注释）。
            //   列表视图暂时没有：它的面板/行池还是私有的，加输入框要动它（README §6 记着这条缺口）。
            grid.ShowSearch = true;
        }

        // ================= IStorageUiProvider =================

        public bool CanHandle(StorageUiContext c)
        {
            // 与列表视图同样的判据：只认 Container（联机客户端上 Storage 为 null）
            return c != null && c.Container != null;
        }

        public bool Open(StorageUiContext c)
        {
            try
            {
                ctx = c;

                // 常驻面板仍挂在**当前**挂载点上 → 直接复用：面板不重建 → epoch 不变
                //   → 池子里的格子控件与它们已渲染好的图标贴图原样留用（这正是本方法的目的）。
                // 否则（首次打开 / 切关换服销毁了旧画布）丢掉常驻、重新 Build。
                if (!grid.IsUsableAt(StorageUiHost.CurrentAttachSpot())) grid.Discard();
                grid.Build();

                grid.Refresh(c.Container);
                return true;
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("大格子视图打开失败：" + e);
                grid.Discard();   // 宿主不会再调 Close，半成品必须自己拆干净
                return false;
            }
        }

        public void Close()
        {
            // ⚠ 只藏不拆 —— 具体原因写在 StorageGridPanel.Hide()（坑 #30）。
            grid.Hide();
            ctx = null;
        }

        public void Tick(StorageUiContext c)
        {
            if (!grid.IsBuilt || c == null) return;

            // 先同步内容（尺寸变化会影响后面的滚动范围），再挂载定位
            grid.Refresh(c.Container);
            if (!grid.IsBuilt) return;

            StorageUiHost.AttachAndPlacePanel(grid.Element);
        }

        public void Rebuild(StorageUiContext c)
        {
            if (c == null) return;
            grid.Refresh(c.Container);
        }
    }
}
