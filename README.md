# 标准辊密封批量生成

Windows 10/11 amd64（x64）和 arm64 桌面版。完整解压后运行 `标准辊密封批量生成.exe`，保留同目录 `SolidCraft` 文件夹。自包含 .NET，并附带对应架构的官方 SolidCraft 0.23 CLI。

另提供 Linux amd64/arm64 命令行版 `standard-roller-seal`，可由 CSV 批量生成 SolidCraft、STEP、DXF 和PDF，无图形界面。Linux 安装包内也带对应架构的 SolidCraft CLI。当前没有 macOS 桌面版：WPF 桌面界面不能直接编译成 macOS universal 应用。

## 界面截图

> 以下截图来自此前版本。2.2.3 改进了深色选中行、关于窗口和可调整高度的日志区；2.2.4 进一步增强当前单元格边框、设置选项卡高亮，并对齐关于窗口的信息布局。


以下为 GitHub Actions 在 Windows 环境实际运行生成的界面截图。图片已保存在仓库的 `assets/screenshots/` 中，可在 README 直接预览，不依赖会过期的 Actions 附件。

### 主界面

**浅色模式**

![标准辊密封批量生成 - 主界面（浅色模式）](assets/screenshots/main-light.png)

**深色模式（已修复参数表表头白色背景）**

![标准辊密封批量生成 - 主界面（深色模式）](assets/screenshots/main-dark.png)

### 出图设置

**浅色模式**

![出图设置 - 浅色模式](assets/screenshots/settings-light.png)

**深色模式**

![出图设置 - 深色模式](assets/screenshots/settings-dark.png)

### 关于

**浅色模式**

![关于页面 - 浅色模式](assets/screenshots/about-light.png)

**深色模式**

![关于页面 - 深色模式](assets/screenshots/about-dark.png)

[查看 Windows UI 截图自动测试](https://github.com/EventHorizon-13/solidcraft-roller-seal/actions/workflows/ui-preview.yml)

## 使用

1. 首次启动自动选择内置的 `模板/标准辊密封.solidcraft`（用户提供的标准模板），主界面不再显示模板路径和选择按钮。选择输出文件夹，导入 `辊密封参数模板.csv` 或编辑参数表。
2. 在“出图设置…”选择本地图框 DXF 和标注样式，默认 `_5`。PDF 需要外置 `gbenor.shx` 与 `gbcbig.shx`；默认字体目录为软件包 `模板/字体`，也可指定其他目录。字体文件不会随软件分发。
   “标题栏属性”会读取 DXF 图框中的实际属性，包括图号、材料、设计、审核、日期等；支持嵌套标题栏。填写后用于本次全部零件；留空保留模板原值，图名留空继续按零件编号自动填写，设置会保存。
3. 可分别填写 DXF 文件名和组合 STEP 名。留空使用当天 `辊密封_MMDD`，不用填写后缀。
4. 勾选是否“合并 STEP”“导出当前任务 CSV”“覆盖已有文件”“完成后打开 DXF”或“同时导出 PDF”（默认不勾选、记住设置；SolidCraft 固定自动后台运行，无需手动启动）。选 PDF 时会同时生成 DXF。
5. “生成并导出Step”生成各单件 SolidCraft、STEP，以及选中的组合 STEP/任务 CSV；“一键生成并导出 DXF”再将同目录零件 STEP 汇总出图，排除组合 STEP。勾选 PDF 后也会生成多页 A4 横向 PDF，每个零件图框一页。

顶部“深色模式”提供“自动（跟随系统）／开／关”，立即应用于主窗口、出图设置、关于及许可页面，并记住选择。“自动”会响应系统主题变化。主题使用 .NET / Windows 原生颜色模式；原生深色模式仅在 Windows 11 及以上受支持，其他系统回退到系统浅色，避免混合自绘深色控件。

底部选项与生成/停止按钮按内容自适应高度，窗口缩小时选项可换行，剩余高度交给参数表。关于页采用紧凑、左对齐的 Windows 原生对话框排版，“检查更新”和“关闭”同一行左对齐；出图设置使用 Windows 原生选项卡组织图框与字体、标注与排布、标题栏属性。

默认文件：

| 内容 | 文件名 |
| --- | --- |
| 单件原生模型 | 辊密封_MMDD_编号.solidcraft |
| 单件 STEP | 辊密封_MMDD_编号.step |
| 组合 STEP（可关闭、可改名） | 辊密封_MMDD.step |
| 汇总 DXF（可改名） | 辊密封_MMDD.dxf |
| 多页加工图 PDF | 辊密封_MMDD.pdf |
| 当前任务 CSV（可关闭） | 辊密封_MMDD.csv |

“当前任务 CSV”保留本次参数与编号。软件记住合并、CSV选项和自定义名称；默认名称每天自动更新日期。

## Linux CLI

解压对应 amd64 或 arm64 安装包，在终端运行 `./standard-roller-seal --help` 查看参数。最少需要一个参数 CSV，默认使用内置标准辊密封模板；可用 `--template` 指定其他 V13 `.solidcraft` 模板：

```sh
./standard-roller-seal --csv 辊密封参数模板.csv --output ./输出
```

常用选项：`--combine-step` 输出组合 STEP，`--export-current-csv` 保存本次实际尺寸，`--export-dxf --drawing-template ./模板/图框.dxf` 生成带图框加工图；`--export-pdf` 同时生成汇总 DXF 和每个图框一页的 A4 横向 PDF，需指定含固定 A4 图框的 `--drawing-template`。字体从软件包内 `模板/字体` 读取，也可用 `--font-dir ./字体目录` 指定用户外挂的 `gbenor.shx` 与 `gbcbig.shx`；字体不打包，文字绘制为矢量轮廓。`--dimension-style _5` 指定标注样式，`--overwrite` 覆盖同名文件。仅导出 DXF 时可不提供图框模板。输入与输出路径可使用空格，需用 shell 引号包住。

## 后台运行与模板

后台模式自动启动独立 `solidcraft-cli mcp --headless` 会话，全程不打开 SolidCraft 窗口，完成或中止后关闭自己启动的后台进程，不操作已打开的桌面会话。软件包固定使用官方 0.23 CLI，避免新保存格式使旧桌面版无法打开。

不显示后台运行、控制端口、组合间距或启动按钮。端口固定7878，组合间距固定10 mm。
默认从软件所在目录的 `模板` 子文件夹识别内置 `标准辊密封.solidcraft`、DXF 模板和外置字体目录，缺少时再找工作目录的 `模板` 文件夹。桌面版固定使用内置标准模型；命令行版可用 `--template` 指定其他模型。发行包包含本次用户指定的标准模型，不包含私人 DXF 图框或字体文件。

官方说明：[MCP 无窗口会话](https://docs.solidcraft.io/guide/mcp.html)。官方发行：[SolidCraft 0.23](https://github.com/tangency-labs/solidcraft/releases/tag/v0.23.0)。

## CSV 参数

模板为 UTF-8 BOM，19 列，6 行有效示例；开关填 0/1。孔数允许整数与 x.5：小数孔只能选择一侧半孔，整数孔须左右都不选或同时选半孔。启用斜边时斜边Y、斜边Z必须大于0。单位 mm。

新增五个可选尺寸列：`子头宽`、`子头突出`、`母槽宽`、`母槽深`、`配合间隙`。填数字时覆盖对应模板参数；空白时每行恢复所选模板的原始默认表达式。母槽宽默认与子头宽和间隙联动，不继承前一行的覆盖值。旧14列CSV仍可导入。

原始V13参考值为子头宽82、突出9、母槽宽=子头宽+2×配合间隙、母槽深10、间隙1.5 mm；实际以自己的模板默认值为准。当前任务CSV记录实际采用的尺寸，方便复现。

## DXF

第一角投影，主视图右侧放左视图，上视图放下方。主视图左侧总高，右侧不放尺寸；左视图仅宽、高和斜角。上视图首尾间距由中间完整孔边线校准，母槽即使隐藏或拆段也保留尺寸，槽深文字紧靠槽口，过滤多余的1.11/1.26 mm细节。

图框与标题栏全部文字使用 gbenor.shx/gbcbig.shx；图名只保留在标题栏，编号从单件文件名提取。`_5`标注样式从本地模板复制，隐藏线比例与间距可调。每零件一张图框，全部纵向排入汇总 DXF。

DXF 使用 netDxf 生成 AutoCAD 2010 完整对象与原生可编辑 DIMENSION；图框内 Logo 的实心填充会保留闭合边界。数值来自 STEP 几何，制造前请核对实际图纸。

PDF 使用 MLightCAD 的开源 MIT DXF 数据模型、SHX/BigFont 字体渲染与矢量 PDF 渲染器。渲染依赖会预先打包成单个 JS 文件，安装包不带完整 `node_modules`。需提供含固定 A4 图框的 DXF 模板。生成时按每个独立加工图框排成一张 A4 横向页面，并将多页合并。PDF 中字体以矢量轮廓绘制；加工使用前请用实际图纸核对图框、标注和 Logo。可在“出图设置”指定外置字体目录；如果目录中含 `fonts.json`，可为 BigFont 指定编码。无 `fonts.json` 时，`gbcbig.shx` 默认按 GBK 读取。软件包包含依赖锁文件与许可证，便于追溯组件。

## 构建和检查

2.2.3 正式版由 GitHub Actions 构建并发布 Windows amd64/arm64 桌面包和 Linux amd64/arm64 CLI 包。开发分支仍可单独构建预览包。Linux CLI 项目位于 `src/SolidCraftCli/`，自包含 .NET，并调用随包的官方 SolidCraft CLI；PDF 转换随包附带开源 Node.js 运行时及 MLightCAD 渲染器。仅将用户指定的标准辊密封模型作为内置模板；其他私人模型、图框及 SHX 字体不上传仓库。

文件名并排显示，生成与停止按钮放在底部导出选项同一行；半孔说明移到状态栏，不再显示大标题。SolidCraft 自动启动，控制端口固定7878，组合间距固定10 mm，界面不再提供端口和间距输入。

当前单元格用蓝色外框定位；状态栏说明在进度条左侧。SolidCraft 模板标签、顶部右侧关于按钮及关于界面已加入，程序和窗口使用用户确认的实心浅玻璃辊密封图标。可编辑SVG源见 assets/roller-seal-icon.svg。

项目主页：https://github.com/EventHorizon-13/solidcraft-roller-seal 。程序文件、窗口与关于界面名称统一为“标准辊密封批量生成”。
