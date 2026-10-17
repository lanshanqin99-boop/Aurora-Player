# 随包分发的第三方素材

这个文件记录**packaged 进 Aurora 的、非我方创作的**素材，以及它们的授权依据。
详细版本（含完整指纹与逐项说明）在 `IconForge\source\SOURCE-NOTES.md`，那份在项目之外、不进备份，
所以随包的这一份单独留在这里。

---

## `Assets\FileAudioIcon*.png` —— 音频文件的关联图标

| | |
|---|---|
| 平台 | iconfont.cn（阿里巴巴矢量图标库） |
| 所属图标库 / 作者 | My-icon 字体图标库（My-icon v3.0） / **myicon** |
| 图标名 | 「文件,音频」 |
| 授权声明 | 作者页面写明 **"可免费商用_不用私信"**（以页面原文为准） |
| 来源链接 | https://www.iconfont.cn/search/index?searchType=icon&q=%E9%9F%B3%E9%A2%91 —— **这是搜索页，不是永久链接**，不指向具体图标；定位素材请用下面的指纹 |
| 取回日期 | 2026-10-04 |

**图形指纹**（页面挪动后仍可凭此确认是同一份素材）

| 项 | 值 |
|---|---|
| 原始 SVG SHA256 | `7C9A4CE500DD7A1F1B70FD3DFEE4B1240A5254B232F4A3E6A4B4D5CEDB650E20` |
| 原始 SVG 根元素 `p-id` / `t` | `2018` / `1791091571932` |
| 路径 `p-id` | `2019`、`2020` |
| 原始 `viewBox` | `0 0 1024 1024` |

**我方改动**：图形未动，只设 `fill="#5879EB"`（品牌蓝紫，取自 Aurora logo 实测最饱和色）
与四周 8% 透明边距。改后文件 SHA256：
`15A0556A89C96C2D713A1C91D55EACB93F7F66BD7FE5528344056536865A962A`。

**生成方式**：`winapp manifest update-assets`，从 SVG **按每个尺寸矢量渲染**成 16–256 共 14 档
（`targetsize-N` 与 `targetsize-N_altform-unplated` 各一份）。

---

## 其余素材

`AppIcon.ico`、`Square*`、`StoreLogo*`、`Wide*`、`SplashScreen*`、`SplashMark.png`、`SplashWord.png`
均**源自用户自有的品牌设计稿**（`IconForge\source\Aurora logo.png`），无第三方条款。
`IdleBackground.jpg` 同为自有素材。
