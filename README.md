# SolidCraft 参数与批量导出

Windows 桌面小工具：在表格中录入或修改辊密封参数，导入/导出 CSV，并让运行中的 SolidCraft 桌面版生成每行的原生 `.solidcraft` 与单件 `.step`。可选生成一个组合 STEP，程序按实体包围盒沿 X 方向留间距排布，并在导出前调用 SolidCraft 的精确实体相交检查；只要发现相交、检查超时或有实体未检查，就停止组合 STEP 导出。

## 运行环境

- Windows 10/11
- Python 3.10+（含 Tkinter）
- SolidCraft Windows 桌面版
- V13 参数化辊密封模板

先保存桌面中打开的设计，再用控制端口启动 SolidCraft：

```powershell
solidcraft --control 7878
```

也可在快捷方式“目标”末尾添加 `--control 7878`。程序只连 `127.0.0.1`，并从 `%LOCALAPPDATA%\SolidCraft\control\7878.token` 读取本次启动的令牌。

启动程序：

```powershell
py .\main.py
```

选 CSV、V13 `.solidcraft` 模板和输出目录后点“连接 SolidCraft 并生成”。模板和 STEP 会先暂存在本机 `%LOCALAPPDATA%\SolidCraft\csv-tool`，所以 CSV、模板及最终输出目录都可以位于 NAS；通过 SolidCraft 文件命令访问的路径保持在本地盘。

## 参数表

CSV 表头为：

```text
编号,孔数,孔径,孔距,高度,宽度,子头,母槽,左半圆,右半圆,左斜边,右斜边,斜边Y,斜边Z
```

开关使用 0/1；小数孔数必须为 `x.5` 且恰好指定左或右半圆。程序兼容 UTF-8、带 BOM 的 UTF-8、UTF-16 和 GBK/GB18030；导出默认 UTF-8 BOM，便于 Excel 打开。

## 导出行为

- 每行生成 `编号_辊密封.solidcraft` 和同名单件 `.step`。
- 勾选组合 STEP 后，所有单件 STEP 作为独立实体装入一个新设计。按每个零件的实体包围盒计算 X 偏移，零件之间保留设定的间距。
- 组合导出前调用 `inspect.interference`。相交对、未检查实体、超时或被截断时，不生成组合 STEP；单件输出仍保留。
- 输出默认不覆盖同名文件；只有显式勾选“覆盖已有输出”才会替换。

本工具通过 SolidCraft 桌面控制通道调用 `doc.open`、`parameters.change`、`file.save_as`、`file.export`、`file.insert_step` 和 `inspect.interference`。参见 SolidCraft 官方[控制协议](https://docs.solidcraft.io/dev/control-protocol.html)与[命令参考](https://docs.solidcraft.io/reference/commands.html)。
