# iOS 应用图标

使用 SteamGridDB 上 Oyasumi 上传的 Initial D Arcade Stage Ver.3 封面，保留 AE86、游戏标识与橙色 3。

- 素材页：https://www.steamgriddb.com/grid/803589
- 作者：https://www.steamgriddb.com/profile/76561198336696092
- 原图：https://cdn2.steamgriddb.com/grid/f04b4053b5b1c5fabd05ca595aedf1c9.png
- 本地原图：`steamgriddb-stage3-source.png`，600 × 900 RGB。
- 应用图标：`Assets/Branding/iOS/AppIcon.png`，1024 × 1024 RGB，无透明通道，无预绘制圆角。

裁切原图矩形 `(0, 200, 600, 800)`，再用 Pillow `Image.Resampling.LANCZOS` 缩放至 1024 × 1024。此封面为原版 Ver.3 素材。

`Idas3IosBuild` 将此本地图标配置到 iOS 的各类图标槽位；后续编译不需要网络下载。原素材及游戏标识的权利属于相应权利人。
