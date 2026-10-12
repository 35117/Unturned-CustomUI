// ============================================================================
//  StorageUiShapes —— **自绘贴图的唯一口径**（`26.10.3.64` 新增，internal）
//
//  为什么需要它：白贴图原先**生成了两份** —— `StorageUiFill.RoundedBox()`（写死半径 9）
//  与 `StorageUiBar.RoundedSprite(radius)`（可配半径）**逐像素等价**（同一套公式），
//  一份能配半径、一份不能 ⟹ 「填充层不能改圆角」这个缺口就是这么来的，
//  而且两份随时可能被各自改歪（铁律 #5 / #54：同一事实写在 N 处）。
//
//  ★ 形状公式（**照主题 `Box.png` 逐行反推**，不是随手调的好看值）：
//    · 贴图边长 `S = 2R + 2`；9 宫格边距 `border = R`；四周 `M = 1px` **透明留白**；
//    · 圆角半径 = 形状半高（`R = 9` 时与原版盒子**逐像素相同**）；
//    · `pixelsPerUnit = 100`（`Box.png.meta: spritePixelsToUnits` 也是 100）⟹ 缩放观感一致；
//    · `SpriteMeshType.FullRect`（必须 —— 否则 9 宫格切不出中间那块）。
//
//  ⚠ 三条纪律：
//    1. **RGB 必须纯白** —— `TintColor` 是**乘法**，不是白就调不出目标色（坑 #86）。
//    2. 贴图**按半径缓存、全进程共用一次生成**（不销毁，也不设 `ShouldDestroyTexture`）。
//    3. 9 宫格的边距切片**不随尺寸缩放** ⟹ 那 1px 留白会原样画出来
//       （少了它就是"比原版框大一圈" —— 用户 2026-10-10 实测踩过，坑 #87）。
// ============================================================================
using UnityEngine;

namespace CustomUI
{
    /// <summary>自绘形状贴图（白贴图 + alpha 携带形状）。**全项目只有这一处生成它们。**</summary>
    internal static class StorageUiShapes
    {
        /// <summary>直角贴图：1×1 纯白，配 `Regular` 拉伸 = 纯色矩形（与原版加载条同观感）。</summary>
        private static Sprite flat;

        /// <summary>圆角贴图按半径缓存（`R = 9` 是最常用的那个 —— 与主题 `Box.png` 同形）。</summary>
        private static readonly System.Collections.Generic.Dictionary<int, Sprite> roundedCache =
            new System.Collections.Generic.Dictionary<int, Sprite>();

        /// <summary>直角（1×1 白）。`radius &lt;= 0` 时用它。</summary>
        internal static Sprite Flat()
        {
            if (flat != null) return flat;

            Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.name = "CustomUI_ShapeFlat";
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.SetPixel(0, 0, UnityEngine.Color.white);
            tex.Apply();

            flat = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 100f);
            return flat;
        }

        /// <summary>
        /// 白色**圆角矩形**贴图（9 宫格），按半径缓存。`radius &lt;= 0` 退回 <see cref="Flat"/>。
        /// 形状说明与纪律见本类文件头。
        /// </summary>
        internal static Sprite Rounded(int radius)
        {
            if (radius < 1) return Flat();

            Sprite cached;
            if (roundedCache.TryGetValue(radius, out cached) && cached != null) return cached;

            const float M = 1f;                 // 四周透明留白（与 Box.png 的可见形状同：x[1..18] ⟹ 1px）
            int R = radius;
            int S = 2 * R + 2;                  // R = 9 ⟹ 20，与主题 Box.png 同尺寸

            // 形状矩形 = [M, S-M]；`lo` / `hi` 是"圆心被夹到的那条线"的上下界。
            // ⚠ R == (S - 2M)/2 时 lo == hi，算式退化成"到贴图中心的距离"（胶囊的退化情形），
            //   与 Box.png 的角弧同形。
            float lo = M + R;
            float hi = S - M - R;

            Texture2D tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.name = "CustomUI_ShapeRound" + R;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            Color32[] px = new Color32[S * S];
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    float fx = x + 0.5f;
                    float fy = y + 0.5f;

                    float cx = (fx < lo) ? lo : ((fx > hi) ? hi : fx);
                    float cy = (fy < lo) ? lo : ((fy > hi) ? hi : fy);

                    float dx = fx - cx;
                    float dy = fy - cy;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    byte a = (byte)(Mathf.Clamp01(R - d + 0.5f) * 255f);   // 边缘 1px 抗锯齿

                    px[y * S + x] = new Color32(255, 255, 255, a);
                }
            }

            tex.SetPixels32(px);
            tex.Apply();

            Sprite sp = Sprite.Create(tex, new Rect(0f, 0f, S, S), new Vector2(0.5f, 0.5f),
                100f, 0u, SpriteMeshType.FullRect, new Vector4(R, R, R, R));
            roundedCache[radius] = sp;
            return sp;
        }
    }
}
