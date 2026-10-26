# 标准辊密封批量生成

Windows 10/11 x64 桌面程序，使用 C# / WinForms 原生窗口与表格控件。发布包自带 .NET 运行时，双击运行，无需安装 Python 或 .NET。

[下载 Windows 版本](https://github.com/EventHorizon-13/solidcraft-csv-tool/releases/latest/download/SolidCraftCsvTool-win-x64.zip) · [所有发行版](https://github.com/EventHorizon-13/solidcraft-csv-tool/releases)

解压 ZIP 后运行 `SolidCraftCsvTool.exe`。需要电脑上已安装 SolidCraft Windows 桌面版，并准备 V13 参数化辊密封模板。模板由使用者自行选择，程序不包含私人模型文件。

## 使用

1. 保存 SolidCraft 中当前打开的设计。点程序里的“启动 SolidCraft…”选择桌面版 `solidcraft.exe`，程序会加上控制参数启动；也可自行用 `solidcraft.exe --control 7878` 启动。
2. 选择 V13 `.solidcraft` 模板与输出文件夹。
3. 在参数表里直接输入，或导入 CSV。子头、母槽、左右半孔、左右斜边分别使用复选框。
4. 按需勾选“另导出组合 STEP”，设置实体间距，点“生成并导出”。
5. 若已有 STEP 文件，点“STEP 合并出图 DXF”，多选 `.step/.stp` 并指定输出文件夹。程序按文件名排序，把所有加工图排入一个 `STEP加工图_汇总.dxf`，每张图上方标有对应序号和 STEP 文件名。处理后会关闭临时导入标签并恢复 SolidCraft 原视角。

每行输出 `编号_辊密封.solidcraft` 与同名 `.step`。组合输出为 `辊密封_组合.step`。各 STEP 先插入新设计，再通过 SolidCraft 的实例移动命令沿 X 方向留间距排开；导出前检查组合实体数量并精确检查相交。SolidCraft 的检查接口提供局部包围盒，不能拿它验证移动后的世界坐标。相交、实体数量不一致、检查未完成或返回信息不足时，中止组合 STEP 导出，并报告已知相交对；已完成的单件会保留。默认不覆盖同名文件。

## STEP 批量加工图

“STEP 合并出图 DXF”使用现有 STEP 实体，不读取 CSV 来绘制轮廓。SolidCraft 导出主视、俯视和端视投影，程序按 mm 原尺寸绘制。多张图按文件名顺序纵向排版在同一个 `STEP加工图_汇总.dxf` 中，每张图包含零件投影、序号、原 STEP 文件名及自动尺寸标注；默认不覆盖已有汇总文件。后续可增加导入 DXF 图框模板，让图框、图层和文字样式沿用用户模板。

尺寸标注参照辊密封加工图样式：青色尺寸线、绿色数字，单位 mm。自动标注三视图总长、宽、高，圆槽中心距、俯视槽口及端部台阶的分段尺寸。尺寸依据 STEP 实际投影提取，不读取 CSV 来猜尺寸，不添加未提供的公差。尺寸为原生 CAD `DIMENSION` 对象，可在 CAD 中编辑；几何未能支持的分段不自动补齐，加工前请核对。当前不复制参考文件中的公司图框。

标题栏显示“标准辊密封批量生成 · v版本号 · 编译于 yyyy-MM-dd”。正式版本号与发布标签一致，编译日期在构建时写入，复制 EXE 不会改变日期。

DXF 使用 AutoCAD 2010（AC1024）格式，单位 mm，文字采用 Unicode。由 netDxf 写入完整的图层、文字/尺寸样式、块记录、句柄、引用及 OBJECTS 结构；保留原生可编辑尺寸。构建检查包含双库读取、对象引用和字段版本检查；实际 AutoCAD 打开结果以用户端验证为准。

“停止”会等当前 SolidCraft 命令完成，再停止后续操作。长任务通过控制协议查询原请求结果，不会自动重发修改或导出命令。生成期间请勿手动切换 SolidCraft 标签或修改模型。

## CSV

兼容原来的 CSV 表头，界面把“左半圆 / 右半圆”显示为“左半孔 / 右半孔”：

```text
编号,孔数,孔径,孔距,高度,宽度,子头,母槽,左半圆,右半圆,左斜边,右斜边,斜边Y,斜边Z
```

开关在 CSV 里为 `0/1`。孔数为 `x.5` 时必须选一侧半孔；整数孔数必须两侧都不选，或两侧都选。半孔由 V13 模板的整排孔位平移与端面剪切实现，例如 4.5 孔切削 5 个孔位、5 孔双半孔切削 6 个孔位。尺寸单位 mm。

支持 UTF-8、UTF-8 BOM、UTF-16、GBK / GB18030，导出为 Excel 可直接识别的 UTF-8 BOM。`examples/参数示例.csv` 是通用示例，未包含用户的生产表格。

## 本机文件

CSV、模板和最终输出可放 NAS；SolidCraft 读写的中间文件位于 `%LOCALAPPDATA%\SolidCraft\csv-tool\run-*`。成功后清理本次中间文件，失败或停止时保留并在运行记录中显示路径。界面日志仅保留约 500 行，不持续写日志文件。模板路径、输出目录、SolidCraft 程序路径与端口保存在同目录的 `settings.json`。

控制连接仅访问 `127.0.0.1`。默认读取 `%LOCALAPPDATA%\SolidCraft\control\端口.token`；也支持 `SOLIDCRAFT_CONTROL_DIR`。令牌只在连接首请求发送，不写入日志。

## 从源码构建

开发环境需要 Windows 与 .NET 10 SDK。最终使用者不需要安装 SDK 或 .NET 运行时。

```powershell
.\build.ps1
```

输出位于 `dist/app/SolidCraftCsvTool.exe`。DXF 由随程序打包的 netDxf 库写出，无需另装 CAD 转换器。GitHub Actions 在 Windows 构建机发布自包含程序包和 SHA256 校验文件。

构建不连接 SolidCraft 桌面端，也不生成 CAD 模型。真实导入、摆放和组合导出仍以 Windows 桌面端实际运行为准。

本工具使用 SolidCraft 官方[控制协议](https://docs.solidcraft.io/dev/control-protocol.html)与[命令参考](https://docs.solidcraft.io/reference/commands.html)。旧 Python 实现可在 Git 历史中查看，当前入口为 C# Windows 应用。
