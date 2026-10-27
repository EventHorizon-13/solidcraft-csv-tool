# 标准辊密封批量生成

Windows 10/11 x64。完整解压后运行 `SolidCraftCsvTool.exe`，保留同目录 `SolidCraft` 文件夹。
自包含 .NET，附带官方 SolidCraft 0.23 CLI 与许可说明。后台模式无需手动启动 SolidCraft 桌面版；模型生成仍需选择自己的 V13 `.solidcraft` 模板。

## 使用

1. 选择 V13 模板与输出文件夹，导入 `辊密封参数模板.csv` 或编辑参数表。
2. 在“出图设置…”选择本地图框 DXF 和标注样式，默认 `_5`。
3. 可分别填写 DXF 文件名和组合 STEP 名。留空使用当天 `辊密封_MMDD`，不用填写后缀。
4. 勾选是否“合并 STEP”“导出当前任务 CSV”“覆盖已有文件”，后台运行默认为开启。
5. “生成并导出Step”生成各单件 SolidCraft、STEP，以及选中的组合 STEP/任务 CSV；“一键生成并导出 DXF”再将同目录零件 STEP 汇总出图，排除组合 STEP。

默认文件：

| 内容 | 文件名 |
| --- | --- |
| 单件原生模型 | 辊密封_MMDD_编号.solidcraft |
| 单件 STEP | 辊密封_MMDD_编号.step |
| 组合 STEP（可关闭、可改名） | 辊密封_MMDD.step |
| 汇总 DXF（可改名） | 辊密封_MMDD.dxf |
| 当前任务 CSV（可关闭） | 辊密封_MMDD.csv |

“当前任务 CSV”保留本次参数与编号。软件记住合并、CSV、后台选项和自定义名称；默认名称每天自动更新日期。

## 后台与桌面

后台模式自动启动独立 `solidcraft-cli mcp --headless` 会话，全程不打开 SolidCraft 窗口，完成或中止后关闭自己启动的后台进程，不操作已打开的桌面会话。软件包固定使用官方 0.23 CLI，避免新保存格式使旧桌面版无法打开。

取消“后台运行”时复用指定控制端口；未连接时自动启动所选 SolidCraft 桌面程序并等待就绪。首次使用此模式可能需要选择 `solidcraft.exe`。停止会等待当前 CAD 命令结束，已导出的文件保留。

官方说明：[MCP 无窗口会话](https://docs.solidcraft.io/guide/mcp.html)。官方发行：[SolidCraft 0.23](https://github.com/tangency-labs/solidcraft/releases/tag/v0.23.0)。

## CSV 参数

模板为 UTF-8 BOM，14 列，6 行有效示例；开关填 0/1。孔数允许整数与 x.5：小数孔只能选择一侧半孔，整数孔须左右都不选或同时选半孔。启用斜边时斜边Y、斜边Z必须大于0。单位 mm。

## DXF

第一角投影，主视图右侧放左视图，上视图放下方。主视图左侧总高，右侧不放尺寸；左视图仅宽、高和斜角。上视图首尾间距由中间完整孔边线校准，母槽即使隐藏或拆段也保留尺寸，槽深文字紧靠槽口，过滤多余的1.11/1.26 mm细节。

图框与标题栏全部文字使用 gbenor.shx/gbcbig.shx；图名只保留在标题栏，编号从单件文件名提取。`_5`标注样式从本地模板复制，隐藏线比例与间距可调。每零件一张图框，全部纵向排入汇总 DXF。

DXF 使用 netDxf 生成 AutoCAD 2010 完整对象与原生可编辑 DIMENSION。数值来自 STEP 几何，制造前请核对实际图纸。

## 构建和检查

`./build.ps1 -Version 1.0.0-dev` 发布自包含程序。GitHub Windows 构建同时附带固定官方 CLI 和 CSV 模板，仅执行本次命名、CSV、图名、界面和真实无窗口导出选项的针对性检查；不运行全量测试或 SHA-256 校验。私人模型及图框不上传仓库。
