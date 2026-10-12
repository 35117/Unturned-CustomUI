// ============================================================================
//  StorageUiTooltip —— 「悬浮提示的统一口径」（`26.10.3.63` 新增）
//
//  为什么需要它：tooltip 的文字**散在各处各写一遍** —— 大格子 / 列表 / 蓝图三套视图
//  各拼各的，`StorageItemIcon` 又自己拼一遍，第三方自己画格子时**连正文都拿不到**
//  （原先那份在 `StorageUiShared` 里，而那个类是 `internal`）。
//  ⟹ 同一个物品在两个地方 hover，措辞会**慢慢漂开**（铁律 #5 那条：同一事实写在 N 处）。
//
//  本类把两件事收成**一处**：
//    · `Compose(正文, 说明)` —— 「正文 + 自定义按键说明」怎么接（空串怎么办、用什么分隔）；
//    · `ForItem(资产, 件数, 合计数量, 耐久)` —— 一件物品的正文
//      （名称 / 描述 / 耐久 / 共几件 / 占用格数）。
//  内置三套视图与 `StorageItemIcon` 现在**都走这里**；第三方自己画格子也能拿到同一份。
//
//  ★ 与「按键约定」的分工（坑 #71 立的规矩，这里把它固化下来）：
//    · **正文**（`ForItem`）= 这件**物品**是什么（名称/描述/耐久/数量/格数）；
//    · **说明**（各视图的 `TooltipHint` / `StorageItemIcon.TooltipHint`）= 这个**格子**上
//      有哪些**自定义按键约定**（"左键取出"）。
//    ⟹ **别拿 TooltipHint 放状态数据**（上下文、坐标、选中项…）—— 它只在**有物品的格子**上出现，
//       而且语义是"告诉玩家怎么操作"，塞别的东西等于把说明书当便签纸（坑 #71 就是踩这个）。
//
//  ⚠ 引擎侧三条**只看 C# 看不出来**的事（决定"为什么是这个观感"，改之前先知道）：
//    ① 原生 tooltip 由 `GlazieruGUITooltip`（MonoBehaviour）驱动，**要悬停 0.5 秒**才出现；
//    ② 只有 `Root.ShouldDrawTooltip`（= `showTooltips && ShouldDrawCursor`）为真才画 ——
//       **鼠标锁定时（mouselook）根本不显示**；
//    ③ 位置跟着鼠标，在 `mouse.x > 0.7` 时**翻到左侧**右对齐；**纵向不做夹取**
//       （贴屏幕底部时会被切掉一截）—— 所以别把关键信息放在最后几行。
// ============================================================================
using SDG.Unturned;

namespace CustomUI
{
    /// <summary>
    /// 悬浮提示（tooltip）的**统一口径** —— 内置视图与第三方共用同一份拼装规矩。
    ///
    /// 典型用法（第三方自己画一格）：
    /// <code>
    /// myButton.TooltipText = StorageUiTooltip.Compose(
    ///     StorageUiTooltip.ForItem(asset, jar.item.amount, jar.item.amount, jar.item.quality),
    ///     "左键取出");   // 自定义按键约定（可选）
    /// </code>
    /// </summary>
    public static class StorageUiTooltip
    {
        /// <summary>
        /// 「正文 + 自定义说明」的唯一拼装口径：说明为空就原样返回正文，正文为空就只剩说明，
        /// 两者都有才用**换行**接起来。⚠ 三套内置视图原先各自写了一遍
        /// （`text + "\n" + hint`），这里收成一处。
        /// </summary>
        public static string Compose(string body, string hint)
        {
            bool hasBody = !string.IsNullOrEmpty(body);
            bool hasHint = !string.IsNullOrEmpty(hint);
            if (!hasHint) return hasBody ? body : "";
            if (!hasBody) return hint;
            return body + "\n" + hint;
        }

        /// <summary>
        /// 一件物品的 tooltip **正文**（与内置视图**同一份**）：
        /// 名称 → 描述 → 耐久 → 共几件 → 占用格数。
        ///
        /// <paramref name="count"/> = **格子里的件数**（自己一格一件就传 `1`）；
        /// <paramref name="total"/> = **合计数量**（一格一件时传 `jar.item.amount`）。
        /// 两者都只为「共 N 件（合计 ×M）」这一行服务 —— 单件物品（`count = 1`）时那一行不出现。
        /// </summary>
        public static string ForItem(ItemAsset asset, int count, int total, int quality)
        {
            if (asset == null) return "";
            if (count < 1) count = 1;
            if (total < 0) total = 0;

            string t = ItemHead(asset, quality);
            if (count > 1)
            {
                // 合计 0 = 全是空容器（打空的弹匣、空水瓶）——
                // 别写成「合计 ×0」，那看着像 bug（口径与内置视图一致）。
                t += (total == 0)
                    ? ("\n共 " + count + " 件（都是空容器）")
                    : ("\n共 " + count + " 件（合计 ×" + total + "）");
            }
            else if (total == 0)
            {
                t += "\n空的（容量类物品被打空后引擎会保留这件容器）";
            }
            return t + SizeLine(asset);
        }

        /// <summary>名称 + 描述 + 耐久（正文的**前半段**，弹药容器的"共 N 匣"那一支也要用它）。</summary>
        internal static string ItemHead(ItemAsset asset, int quality)
        {
            string t = asset.FriendlyName;
            if (!string.IsNullOrEmpty(asset.itemDescription))
            {
                t += "\n" + asset.itemDescription;
            }
            if (asset.showQuality && quality > 0)
            {
                t += "\n耐久 " + quality + "%";
            }
            return t;
        }

        /// <summary>「占用 N×M 格」行（1×1 物品没有这一行）。</summary>
        internal static string SizeLine(ItemAsset asset)
        {
            return (asset != null && (asset.size_x > 1 || asset.size_y > 1))
                ? ("\n占用 " + asset.size_x + "×" + asset.size_y + " 格")
                : "";
        }
    }
}
