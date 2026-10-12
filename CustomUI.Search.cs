// ============================================================================
//  CustomUI · 公开的「搜索匹配」（StorageUiSearch，26.10.3.43）
// ============================================================================
//  为什么需要它 —— 先说清"缺的到底是哪一半"：
//    · 搜索**框**本来就有公开入口：`StorageGridPanel.ShowSearch` / `StorageListPanel.ShowSearch`
//      （连占位文字、每帧同步、前缀单调都做好了）—— 但那是"用我们的面板才有"；
//    · 第三方**自己画界面**时（机器槽位界面、商店界面、自己的区块）拿不到的是**匹配规则**：
//      `StorageUiShared.MatchesQuery`（中文名 / 英文名 / 拼音首字母 / 全拼）是 internal
//      ⟹ 只能自己再写一套，于是"手感"与内置搜索不一样。
//      同一个箱子里，我们的搜索能搜到、别人的搜不到 —— 玩家只会觉得是 bug。
//
//  ★ 本类**只转发**共享层那一份实现（`StorageUiShared.MatchesQuery`），
//    绝不在这里再写一套 —— 两份匹配规则一定会漂（改了一处，另一处还留着旧规则）。
//
//  ⚠ 空 / 全空白的 query 视为**全部匹配**（与三个内置视图一致：没打字就是不过滤）。
//  ⚠ 不看**描述**：原版描述要 `ItemAsset.BuildDescription`（UI 级组装），
//    放进每帧的搜索路径不划算 —— 理由与取舍写在 `StorageUiShared` 的缓存注释处。
// ============================================================================
using SDG.Unturned;

namespace CustomUI
{
    /// <summary>
    /// 公开的搜索匹配（`26.10.3.43`）—— 给**自己画界面**的接入方复用**同一套**匹配规则，
    /// 免得各写一套导致"同一个箱子，我们的搜索能搜到、别人的搜不到"。
    ///
    /// 用法：
    /// <code>
    /// if (StorageUiSearch.Matches(jar.GetAsset(), myQuery)) { … }   // myQuery 空 = 全部匹配
    /// </code>
    /// </summary>
    public static class StorageUiSearch
    {
        /// <summary>
        /// 这个物品资产是否匹配搜索词。空 / 全空白 <paramref name="query"/> 恒为 true。
        ///
        /// 匹配面：**中文名 / 英文名 / 拼音首字母 / 全拼**，且**前缀单调**
        /// （打字过程中只会"有→无"收窄，不会出现"有→无→有"）。
        /// 规则细节与为什么这样定：README §4.11 末。
        ///
        /// ⚠ `asset == null` 返回 **false**（不是"全部匹配"）—— 拿不到资产的格子不该因为
        ///   搜索框空着就混进来。
        /// </summary>
        public static bool Matches(ItemAsset asset, string query)
        {
            // ★ 单一实现：只转发，不重写（重写一份匹配规则 = 迟早与内置搜索手感不一致）
            return StorageUiShared.MatchesQuery(asset, query);
        }
    }
}
