// ============================================================================
//  CustomUI · Harmony 补丁
// ============================================================================
//  四个补丁，都很克制，且每个都包了 try/catch（补丁内抛异常会打断原版主循环）。
//
//  1) PlayerDashboardInventoryUI.updateBoxAreas()  ← Prefix
//     私有静态，在 open / onInventoryResized / resetNearbyDrops / updateVehicle
//     处被调用，按 headers[].IsVisible 累加布局。
//     ⚠ 必须在 **Prefix** 同步改写原版存储区（藏网格 / 网格高度 = 面板高度）：
//       布局是在这次调用里算的，放在 Postfix 就只能等下帧才纠正 → 闪一帧
//       原版网格，且「附近物品」区会停在按旧高度算出的位置。
//
//  2) PlayerInventory.openStorage(InteractableStorage)  ← Postfix
//     原版箱子按 F 的入口（public）。它内部最后一行 sendStorage() 会触发客户端
//     的 onInventoryStored → 原版背包页 open() → updateBoxAreas（此时我们还没接管）。
//     所以在它的 Postfix 里立刻接管 + 主动重算一次布局，消除「闪一帧原版存储区」。
//
//  3) PlayerInventory.closeStorage()  ← Postfix
//     原版关闭存储时同步关掉我们的界面（双保险；Tick 里也有检测）。
//
//  ⚠ 时序问题的完整处理（这是本插件最容易出 bug 的地方）：
//     帧 N：按 F → openStorage → sendStorage → onInventoryStored → 背包页 open
//           → updateBoxAreas（IsOpen 尚为 false → 存储区被原版显示）
//           → 回到 openStorage 的 Postfix → TryOpen 成功 → Hide + 重算布局 ✓
//     所以「Postfix 里主动重算」是必须的，不能只靠 updateBoxAreas 的 Prefix。
// ============================================================================
using System;
using HarmonyLib;
using SDG.Unturned;

namespace CustomUI
{
    /// <summary>
    /// 补丁 1：布局前同步改写原版存储区（只藏网格、保留标题、网格高度跟随面板）。
    /// 放在 Prefix 是为了让**这次**布局就用上新高度，无需事后补救。
    /// </summary>
    [HarmonyPatch(typeof(PlayerDashboardInventoryUI), "updateBoxAreas")]
    internal static class Patch_UpdateBoxAreas
    {
        private static void Prefix()
        {
            try
            {
                if (!CustomUIPlugin.Enabled) return;
                if (!StorageUiHost.IsOpen) return;
                StorageUiHost.ApplyVanillaOverride();
            }
            catch (Exception) { }
        }
    }

    /// <summary>补丁 2：原版打开存储后立刻接管（消除闪帧）。</summary>
    /// <remarks>
    /// ⚠ 这个补丁**只在单机/主机**上生效：openStorage 是服务端方法，专用服务器
    ///   客户端不执行它（走 ReceiveStoraging）。联机接管由 Host.Tick 的每帧检测
    ///   兜住。这里保留它只是为了「单机时立刻接管、消除闪一帧」。
    /// </remarks>
    [HarmonyPatch(typeof(PlayerInventory), "openStorage")]
    internal static class Patch_OpenStorage
    {
        private static void Postfix(PlayerInventory __instance)
        {
            try
            {
                if (!CustomUIPlugin.Enabled) return;

                // 只处理本地玩家自己的背包：专用服务器上 Player.LocalPlayer 为 null
                Player local = Player.LocalPlayer;
                if (local == null || __instance == null || __instance != local.inventory) return;

                // ⚠ 数据源用 items[STORAGE]，不是 storage.items（见 Host 头部说明 B）
                Items container = null;
                if (__instance.items != null && PlayerInventory.STORAGE < __instance.items.Length)
                {
                    container = __instance.items[PlayerInventory.STORAGE];
                }
                if (container == null || container.height <= 0) return;

                InteractableStorage storage = __instance.storage;
                BarricadeDrop drop = null;
                try
                {
                    if (storage != null && storage.transform != null)
                    {
                        drop = BarricadeManager.FindBarricadeByRootTransform(storage.transform);
                    }
                }
                catch (Exception) { }

                if (StorageUiHost.TryOpen(container, storage, drop))
                {
                    // 立刻藏 + 主动重算布局（此时原版已按「存储区可见」算过一次了）
                    StorageUiHost.ApplyVanillaOverride();
                    StorageUiHost.ForceRelayout();
                }
            }
            catch (Exception e)
            {
                CustomUIPlugin.Error("openStorage 补丁异常：" + e);
            }
        }
    }

    /// <summary>补丁 3：原版关闭存储时同步关掉我们的界面。</summary>
    [HarmonyPatch(typeof(PlayerInventory), "closeStorage")]
    internal static class Patch_CloseStorage
    {
        private static void Postfix()
        {
            try
            {
                // ⚠ 这条也要留痕：借道界面如果"自己没了"，很可能是**别人**调了 closeStorage
                //   （引擎的走远/换页、或另一个插件），而宿主这边的关闭是静默的。
                if (StorageUiHost.IsOpen && StorageUiHost.VirtualActive)
                {
                    CustomUIPlugin.Warn("原版 closeStorage 被调用，且当前是借道会话 —— 界面随之关闭");
                }
                if (StorageUiHost.IsOpen) StorageUiHost.Close();
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// 补丁 4：**禁止任何"搬进借道的中转页"**。
    ///
    /// 为什么必须在这儿拦：往存储页里放东西有**好几个原版入口**，最后都汇到
    /// `ReceiveDragItem(page_0,..., page_1=STORAGE, ...)`：
    ///  · 按住 Ctrl + 右键背包物品 = 快速移动（`PlayerDashboardInventoryUI.cs:897/901`）；
    ///  · 信息面板里的「移动到箱子」按钮（同文件 `:446/:450`）；
    ///  · 我们自己的 `DropIntoStorage`（那条已在 Shared 里单独拦了一次）。
    /// 只拦我们自己那条 = 漏（2026-10-03 用户实测：手动拖被拒了，Ctrl+右键仍把数量变成 ×2）。
    ///
    /// 为什么必须拦：中转页是临时的 —— `closeStorage()` 会把整个 `Items` 换成新的空页
    /// （`PlayerInventory.cs:1639`），放进去的**真物品随页销毁**。
    ///
    /// ⚠ 反向存入（背包 → 插件清单，C 第 4 步）要做时，**必须改成走调用方回调**
    ///   由插件写自己的清单，**不要**在这里开口子直接放行搬进这一页。
    /// </summary>
    [HarmonyPatch(typeof(PlayerInventory), "ReceiveDragItem", new[] {
        typeof(byte), typeof(byte), typeof(byte),
        typeof(byte), typeof(byte), typeof(byte), typeof(byte) })]
    internal static class Patch_BlockDragIntoRelay
    {
        // 显式写参数类型：本工作区有插件按名字补丁撞上重载，当场崩在加载期
        // （AmbiguousMatchException —— 见 传送器 插件的 destroyBarricade 案例）。
        private static bool Prefix(PlayerInventory __instance, byte page_1)
        {
            try
            {
                if (page_1 != PlayerInventory.STORAGE) return true;   // 不是搬进存储页，放行

                Player local = Player.LocalPlayer;
                if (local == null || __instance == null || __instance != local.inventory)
                    return true;
                if (__instance.items == null || PlayerInventory.STORAGE >= __instance.items.Length)
                    return true;

                Items container = __instance.items[PlayerInventory.STORAGE];
                if (container == null) return true;
                if (!StorageUiHost.IsVirtualFor(container)) return true;   // 真箱子/真后备箱，放行

                // 借道的中转页：跳过原版（物品留在原处），并把原因顶到标题栏
                StorageUiShared.WarnRelayDepositOnce();
                return false;
            }
            catch (Exception)
            {
                return true;   // 判据出错时**放行**原版 —— 绝不能因为我们的异常卡住原版搬运
            }
        }
    }
}
