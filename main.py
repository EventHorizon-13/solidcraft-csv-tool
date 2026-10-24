from __future__ import annotations

import csv
import json
import os
import queue
import re
import shutil
import socket
import tempfile
import threading
import tkinter as tk
from decimal import Decimal, InvalidOperation
from pathlib import Path
from tkinter import filedialog, messagebox, ttk
from typing import Any

APP_TITLE = "SolidCraft 参数与批量导出"
CSV_COLUMNS = ["编号", "孔数", "孔径", "孔距", "高度", "宽度", "子头", "母槽", "左半圆", "右半圆", "左斜边", "右斜边", "斜边Y", "斜边Z"]
PARAM_MAP = {
    "孔数": "孔数", "孔径": "辊径", "孔距": "辊距", "高度": "实体高度", "宽度": "密封宽度",
    "子头": "子头开关", "母槽": "母槽开关", "左半圆": "左半孔开关", "右半圆": "右半孔开关",
    "左斜边": "左斜边开关", "右斜边": "右斜边开关", "斜边Y": "斜边Y", "斜边Z": "斜边Z",
}
FLAG_COLUMNS = ("子头", "母槽", "左半圆", "右半圆", "左斜边", "右斜边")
POSITIVE_COLUMNS = ("孔径", "孔距", "高度", "宽度")
NONNEGATIVE_COLUMNS = ("斜边Y", "斜边Z")
INVALID_WIN_NAME = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
DEFAULT_ROW = {"编号": "1", "孔数": "5", "孔径": "90", "孔距": "120", "高度": "105", "宽度": "200", "子头": "1", "母槽": "1", "左半圆": "0", "右半圆": "0", "左斜边": "0", "右斜边": "0", "斜边Y": "30", "斜边Z": "30"}


class UserError(RuntimeError):
    pass


def decode_csv(path: Path) -> str:
    raw = path.read_bytes()
    encodings = ("utf-16",) if raw.startswith((b"\xff\xfe", b"\xfe\xff")) else (("utf-8-sig",) if raw.startswith(b"\xef\xbb\xbf") else ("utf-8-sig", "gb18030"))
    last: Exception | None = None
    for encoding in encodings:
        try:
            return raw.decode(encoding)
        except UnicodeDecodeError as exc:
            last = exc
    raise UserError(f"无法识别 CSV 编码：{last}")


def import_rows(path: Path) -> list[dict[str, str]]:
    text = decode_csv(path)
    reader = csv.DictReader(text.splitlines())
    if not reader.fieldnames:
        raise UserError("CSV 没有表头。")
    missing = [col for col in CSV_COLUMNS if col not in reader.fieldnames]
    if missing:
        raise UserError("CSV 缺少列：" + "、".join(missing))
    rows = []
    for row in reader:
        if not row or not any((v or "").strip() for v in row.values()):
            continue
        rows.append({col: (row.get(col) or "").strip() for col in CSV_COLUMNS})
    if not rows:
        raise UserError("CSV 没有数据行。")
    return rows


def write_rows(path: Path, rows: list[dict[str, str]]) -> None:
    with path.open("w", encoding="utf-8-sig", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=CSV_COLUMNS, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)


def parse_decimal(row: dict[str, str], column: str, line: int) -> Decimal:
    try:
        value = Decimal(row[column].strip())
    except (InvalidOperation, KeyError):
        raise UserError(f"第 {line} 行：{column} 不是有效数字。")
    if not value.is_finite():
        raise UserError(f"第 {line} 行：{column} 必须是有限数字。")
    return value


def safe_stem(value: str, line: int) -> str:
    stem = INVALID_WIN_NAME.sub("_", value.strip()).strip(" .")
    if not stem or stem in (".", ".."):
        raise UserError(f"第 {line} 行：编号为空，无法命名输出文件。")
    device = stem.upper().split(".", 1)[0]
    if device in {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}:
        stem = "_" + stem
    return stem


def validate_rows(rows: list[dict[str, str]]) -> list[tuple[str, dict[str, str]]]:
    prepared = []
    names: set[str] = set()
    for line, row in enumerate(rows, start=2):
        name = safe_stem(row.get("编号", ""), line)
        if name.casefold() in names:
            raise UserError(f"第 {line} 行：编号 {name!r} 重复。")
        names.add(name.casefold())
        for col in CSV_COLUMNS[1:]:
            if not row.get(col, "").strip():
                raise UserError(f"第 {line} 行：{col} 为空。")
        holes = parse_decimal(row, "孔数", line)
        if holes <= 0 or holes % Decimal("0.5") != 0:
            raise UserError(f"第 {line} 行：孔数只允许正整数或 x.5。")
        flags: dict[str, int] = {}
        for col in FLAG_COLUMNS:
            try:
                val = Decimal(row[col])
            except InvalidOperation:
                raise UserError(f"第 {line} 行：{col} 只能填 0 或 1。")
            if val not in (Decimal(0), Decimal(1)):
                raise UserError(f"第 {line} 行：{col} 只能填 0 或 1。")
            flags[col] = int(val)
            row[col] = str(int(val))
        if holes % 1 == Decimal("0.5") and flags["左半圆"] + flags["右半圆"] != 1:
            raise UserError(f"第 {line} 行：{holes} 孔必须恰好选左半圆或右半圆一侧。")
        for col in POSITIVE_COLUMNS:
            if parse_decimal(row, col, line) <= 0:
                raise UserError(f"第 {line} 行：{col} 必须大于 0。")
        for col in NONNEGATIVE_COLUMNS:
            if parse_decimal(row, col, line) < 0:
                raise UserError(f"第 {line} 行：{col} 不能小于 0。")
        params = {PARAM_MAP[col]: row[col].strip() for col in PARAM_MAP}
        prepared.append((name, params))
    if not prepared:
        raise UserError("参数表没有数据行。")
    return prepared


class SolidCraftControl:
    def __init__(self, port: int, timeout: float = 75.0) -> None:
        local = os.environ.get("LOCALAPPDATA")
        if not local:
            raise UserError("找不到 LOCALAPPDATA；请在运行 Windows 桌面版的电脑上启动本程序。")
        token_path = Path(local) / "SolidCraft" / "control" / f"{port}.token"
        if not token_path.is_file():
            raise UserError(f"找不到控制令牌：{token_path}\n请先保存工作，再用 --control {port} 启动 SolidCraft 桌面版。")
        self.token = token_path.read_text(encoding="ascii").strip()
        try:
            self.sock = socket.create_connection(("127.0.0.1", port), timeout=timeout)
        except OSError as exc:
            raise UserError(f"无法连接 SolidCraft 127.0.0.1:{port}：{exc}\n请确认桌面版是用 --control {port} 启动的。")
        self.sock.settimeout(timeout)
        self.stream = self.sock.makefile("rwb")
        self.seq = 1

    def request(self, method: str, params: dict[str, Any]) -> Any:
        req: dict[str, Any] = {"id": self.seq, "method": method, "params": params}
        if self.seq == 1:
            req["token"] = self.token
        wanted = self.seq
        self.seq += 1
        try:
            self.stream.write(json.dumps(req, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n")
            self.stream.flush()
            data = self.stream.readline()
        except (OSError, TimeoutError) as exc:
            raise UserError(f"控制请求 {method} 中断：{exc}。不要重发同一操作，先检查 SolidCraft 桌面状态。")
        if not data:
            raise UserError("SolidCraft 桌面版已关闭控制连接。")
        try:
            response = json.loads(data.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise UserError(f"SolidCraft 返回了无效响应：{exc}")
        if response.get("id") != wanted or not response.get("ok"):
            detail = response.get("error", response)
            if response.get("running"):
                detail = f"SolidCraft 仍在执行 {method}，不要重复提交。请先查看桌面版状态。{detail}"
            raise UserError(f"SolidCraft 请求失败：{detail}")
        return response.get("result")

    def execute(self, command: str, params: dict[str, Any]) -> Any:
        return self.request("engine.execute", {"command": command, "params": params})

    def inspect(self) -> dict[str, Any]:
        result = self.request("document.inspect", {"measure": True})
        return result if isinstance(result, dict) else {}

    def close(self) -> None:
        try:
            self.stream.close()
        finally:
            self.sock.close()


def model_bbox(info: dict[str, Any]) -> tuple[list[float], list[float]]:
    boxes = []
    for body in info.get("bodies", []):
        bbox = body.get("bbox") or body.get("bounding_box")
        if isinstance(bbox, dict):
            lo = bbox.get("min") or bbox.get("minimum")
            hi = bbox.get("max") or bbox.get("maximum")
        elif isinstance(bbox, (list, tuple)) and len(bbox) == 2:
            lo, hi = bbox
        else:
            continue
        if isinstance(lo, (list, tuple)) and isinstance(hi, (list, tuple)) and len(lo) == 3 and len(hi) == 3:
            boxes.append(([float(x) for x in lo], [float(x) for x in hi]))
    if not boxes:
        raise UserError("SolidCraft 没返回实体包围盒，无法安全排布组合 STEP。")
    lo = [min(box[0][i] for box in boxes) for i in range(3)]
    hi = [max(box[1][i] for box in boxes) for i in range(3)]
    return lo, hi


def check_half_validation(client: SolidCraftControl, name: str) -> None:
    result = client.execute("parameters.list", {"filter": "user"})
    params = result.get("parameters", []) if isinstance(result, dict) else []
    check = next((p for p in params if p.get("name") == "半孔校验"), None)
    if check is None:
        raise UserError("模板中没有“半孔校验”参数；请选用 V13 参数化模板。")
    if abs(float(check.get("value", 0))) > 1e-9:
        raise UserError(f"型号 {name} 的半孔校验不为 0，已停止。")


class App:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        root.title(APP_TITLE)
        root.geometry("1390x780")
        root.minsize(1050, 640)
        self.events: queue.Queue[tuple[str, str]] = queue.Queue()
        self.running = False
        self.rows: list[dict[str, str]] = []
        self.csv_path = tk.StringVar()
        self.template_path = tk.StringVar()
        self.output_path = tk.StringVar(value=str(Path.home() / "Documents" / "SolidCraft输出"))
        self.port = tk.StringVar(value="7878")
        self.gap = tk.StringVar(value="10")
        self.combined = tk.BooleanVar(value=True)
        self.overwrite = tk.BooleanVar(value=False)
        self._build_ui()
        default_csv = Path(__file__).resolve().parent.parent / "辊密封.csv"
        if default_csv.is_file():
            self.load_csv(default_csv, quiet=True)
        if not self.rows:
            self.set_rows([DEFAULT_ROW.copy()])
        self.root.after(120, self._poll_events)

    def _build_ui(self) -> None:
        outer = ttk.Frame(self.root, padding=12)
        outer.pack(fill="both", expand=True)
        heading = ttk.Frame(outer)
        heading.pack(fill="x", pady=(0, 8))
        ttk.Label(heading, text="SolidCraft 参数与批量导出", font=("Segoe UI", 17, "bold")).pack(side="left")
        ttk.Label(heading, text="CSV 参数表 · 原生模型 · STEP", foreground="#52616b").pack(side="left", padx=16, pady=(5, 0))

        top = ttk.Frame(outer)
        top.pack(fill="x", pady=(0, 8))
        self._path_row(top, 0, "CSV 表", self.csv_path, self.choose_csv, "打开…")
        self._path_row(top, 1, "SolidCraft 模板", self.template_path, self.choose_template, "选择…")
        self._path_row(top, 2, "输出目录", self.output_path, self.choose_output, "选择…")

        controls = ttk.Frame(outer)
        controls.pack(fill="x", pady=(0, 10))
        ttk.Button(controls, text="导入 CSV", command=self.choose_csv).pack(side="left")
        ttk.Button(controls, text="导出 CSV", command=self.export_csv).pack(side="left", padx=(6, 18))
        ttk.Button(controls, text="新增一行", command=self.add_row).pack(side="left")
        ttk.Button(controls, text="删除选中", command=self.delete_rows).pack(side="left", padx=6)
        ttk.Separator(controls, orient="vertical").pack(side="left", fill="y", padx=12)
        ttk.Checkbutton(controls, text="另导出一个组合 STEP（多个实体错开放置并做相交检查）", variable=self.combined).pack(side="left")
        ttk.Label(controls, text="实体间距 mm").pack(side="left", padx=(14, 4))
        ttk.Entry(controls, textvariable=self.gap, width=7).pack(side="left")
        ttk.Checkbutton(controls, text="覆盖已有输出", variable=self.overwrite).pack(side="left", padx=14)
        ttk.Label(controls, text="控制端口").pack(side="right", padx=(10, 4))
        ttk.Entry(controls, textvariable=self.port, width=7).pack(side="right")
        self.run_button = ttk.Button(controls, text="连接 SolidCraft 并生成", command=self.start_run)
        self.run_button.pack(side="right", padx=(0, 12))

        table_frame = ttk.Frame(outer)
        table_frame.pack(fill="both", expand=True)
        self.tree = ttk.Treeview(table_frame, columns=CSV_COLUMNS, show="headings", selectmode="extended")
        widths = {"编号": 70, "孔数": 65, "孔径": 65, "孔距": 65, "高度": 65, "宽度": 65, "子头": 55, "母槽": 55, "左半圆": 72, "右半圆": 72, "左斜边": 72, "右斜边": 72, "斜边Y": 70, "斜边Z": 70}
        for col in CSV_COLUMNS:
            self.tree.heading(col, text=col)
            self.tree.column(col, width=widths[col], minwidth=52, anchor="center", stretch=True)
        yscroll = ttk.Scrollbar(table_frame, orient="vertical", command=self.tree.yview)
        xscroll = ttk.Scrollbar(table_frame, orient="horizontal", command=self.tree.xview)
        self.tree.configure(yscrollcommand=yscroll.set, xscrollcommand=xscroll.set)
        self.tree.grid(row=0, column=0, sticky="nsew")
        yscroll.grid(row=0, column=1, sticky="ns")
        xscroll.grid(row=1, column=0, sticky="ew")
        table_frame.rowconfigure(0, weight=1)
        table_frame.columnconfigure(0, weight=1)
        self.tree.bind("<Double-1>", self.edit_cell)

        ttk.Label(outer, text="开关填 0/1；x.5 孔数必须恰好选择左或右半圆。双击表格单元格编辑。", foreground="#536471").pack(anchor="w", pady=(7, 4))
        log_frame = ttk.LabelFrame(outer, text="运行记录", padding=6)
        log_frame.pack(fill="x")
        self.log = tk.Text(log_frame, height=6, wrap="word", state="disabled", font=("Consolas", 9))
        log_scroll = ttk.Scrollbar(log_frame, orient="vertical", command=self.log.yview)
        self.log.configure(yscrollcommand=log_scroll.set)
        self.log.pack(side="left", fill="both", expand=True)
        log_scroll.pack(side="right", fill="y")
        self.status = tk.StringVar(value="准备就绪。请选 V13 模板，并以 --control 7878 启动 SolidCraft。")
        ttk.Label(outer, textvariable=self.status, anchor="w").pack(fill="x", pady=(5, 0))

    def _path_row(self, parent: ttk.Frame, row: int, label: str, var: tk.StringVar, cmd: Any, button: str) -> None:
        ttk.Label(parent, text=label, width=13).grid(row=row, column=0, sticky="w", pady=2)
        ttk.Entry(parent, textvariable=var).grid(row=row, column=1, sticky="ew", pady=2)
        ttk.Button(parent, text=button, command=cmd).grid(row=row, column=2, padx=(6, 0), pady=2)
        parent.columnconfigure(1, weight=1)

    def set_rows(self, rows: list[dict[str, str]]) -> None:
        self.rows = [{col: row.get(col, "") for col in CSV_COLUMNS} for row in rows]
        for item in self.tree.get_children():
            self.tree.delete(item)
        for row in self.rows:
            self.tree.insert("", "end", values=[row[col] for col in CSV_COLUMNS])

    def table_rows(self) -> list[dict[str, str]]:
        return [dict(zip(CSV_COLUMNS, self.tree.item(item, "values"))) for item in self.tree.get_children()]

    def load_csv(self, path: Path, quiet: bool = False) -> None:
        try:
            rows = import_rows(path)
            self.set_rows(rows)
            self.csv_path.set(str(path))
            self._log(f"已导入 {len(rows)} 行：{path}")
        except Exception as exc:
            if quiet:
                return
            messagebox.showerror("导入失败", str(exc), parent=self.root)

    def choose_csv(self) -> None:
        initial = Path(self.csv_path.get()).parent if self.csv_path.get() else Path.home()
        path = filedialog.askopenfilename(parent=self.root, title="导入参数 CSV", initialdir=initial, filetypes=[("CSV 文件", "*.csv"), ("所有文件", "*.*")])
        if path:
            self.load_csv(Path(path))

    def export_csv(self) -> None:
        try:
            rows = self.table_rows()
            if not rows:
                raise UserError("参数表为空。")
            path = filedialog.asksaveasfilename(parent=self.root, title="导出参数 CSV", initialdir=Path(self.csv_path.get()).parent if self.csv_path.get() else Path.home(), initialfile="辊密封.csv", defaultextension=".csv", filetypes=[("CSV 文件", "*.csv")])
            if path:
                write_rows(Path(path), rows)
                self.csv_path.set(path)
                self._log(f"已导出 CSV：{path}")
        except Exception as exc:
            messagebox.showerror("导出失败", str(exc), parent=self.root)

    def choose_template(self) -> None:
        path = filedialog.askopenfilename(parent=self.root, title="选择 V13 SolidCraft 模板", filetypes=[("SolidCraft 原生文件", "*.solidcraft"), ("所有文件", "*.*")])
        if path:
            self.template_path.set(path)

    def choose_output(self) -> None:
        path = filedialog.askdirectory(parent=self.root, title="选择 STEP 输出目录", mustexist=False)
        if path:
            self.output_path.set(path)

    def add_row(self) -> None:
        rows = self.table_rows()
        row = DEFAULT_ROW.copy()
        row["编号"] = str(len(rows) + 1)
        self.tree.insert("", "end", values=[row[c] for c in CSV_COLUMNS])

    def delete_rows(self) -> None:
        for item in self.tree.selection():
            self.tree.delete(item)

    def edit_cell(self, event: tk.Event) -> None:
        if self.running:
            return
        item = self.tree.identify_row(event.y)
        column = self.tree.identify_column(event.x)
        if not item or not column:
            return
        x, y, width, height = self.tree.bbox(item, column)
        col_index = int(column[1:]) - 1
        old = self.tree.item(item, "values")[col_index]
        entry = ttk.Entry(self.tree)
        entry.place(x=x, y=y, width=width, height=height)
        entry.insert(0, old)
        entry.select_range(0, "end")
        entry.focus_set()
        finished = False
        def commit(_event: Any = None) -> None:
            nonlocal finished
            if finished:
                return
            finished = True
            values = list(self.tree.item(item, "values"))
            values[col_index] = entry.get().strip()
            self.tree.item(item, values=values)
            entry.destroy()
        entry.bind("<Return>", commit)
        entry.bind("<FocusOut>", commit)
        entry.bind("<Escape>", lambda _e: entry.destroy())

    def start_run(self) -> None:
        if self.running:
            return
        try:
            table = self.table_rows()
            prepared = validate_rows(table)
            template = Path(self.template_path.get())
            if not template.is_file() or template.suffix.lower() != ".solidcraft":
                raise UserError("请选择存在的 V13 .solidcraft 原生模板。")
            output = Path(self.output_path.get())
            if not str(output).strip():
                raise UserError("请选择输出目录。")
            output_text = self.output_path.get().strip()
            if not output_text:
                raise UserError("请选择输出目录。")
            output = Path(output_text)
            try:
                port = int(self.port.get())
                gap = float(Decimal(self.gap.get()))
            except (ValueError, InvalidOperation):
                raise UserError("控制端口或实体间距不是有效数字。")
            if not 1 <= port <= 65535 or gap < 0:
                raise UserError("控制端口需为 1–65535，实体间距不能小于 0。")
            if self.combined.get() and gap <= 0:
                raise UserError("组合 STEP 的实体间距必须大于 0，避免零间距接触。")
            self.running = True
            self.run_button.configure(state="disabled")
            self.status.set("正在连接 SolidCraft…")
            options = {"rows": prepared, "template": template, "output": output, "port": port, "gap": gap, "combined": self.combined.get(), "overwrite": self.overwrite.get()}
            threading.Thread(target=self._run_worker, args=(options,), daemon=True).start()
        except Exception as exc:
            messagebox.showerror("参数有误", str(exc), parent=self.root)

    def _run_worker(self, options: dict[str, Any]) -> None:
        client: SolidCraftControl | None = None
        work: Path | None = None
        try:
            local = Path(os.environ["LOCALAPPDATA"]) / "SolidCraft" / "csv-tool"
            local.mkdir(parents=True, exist_ok=True)
            work = Path(tempfile.mkdtemp(prefix="run-", dir=local))
            local_template = work / "template.solidcraft"
            shutil.copy2(options["template"], local_template)
            output: Path = options["output"]
            targets: list[Path] = []
            for name, _params in options["rows"]:
                targets += [output / f"{name}_辊密封.solidcraft", output / f"{name}_辊密封.step"]
            combined_target = output / "辊密封_组合.step"
            if options["combined"]:
                targets.append(combined_target)
            if not options["overwrite"]:
                exists = [str(path) for path in targets if path.exists()]
                if exists:
                    raise UserError("以下输出已存在；勾选“覆盖已有输出”后才能替换：\n" + "\n".join(exists))
            output.mkdir(parents=True, exist_ok=True)
            client = SolidCraftControl(options["port"])
            client.execute("doc.open", {"path": str(local_template)})
            self.events.put(("log", "已连接；模板在桌面版新标签中打开。"))
            bboxes: list[tuple[list[float], list[float]]] = []
            step_paths: list[Path] = []
            for index, (name, params) in enumerate(options["rows"], start=1):
                self.events.put(("status", f"正在生成 {index}/{len(options['rows'])}：{name}"))
                client.execute("parameters.change", {"changes": params})
                check_half_validation(client, name)
                info = client.inspect()
                timeline_errors = [item for item in info.get("timeline", []) if item.get("error")]
                if timeline_errors:
                    raise UserError(f"型号 {name} 有特征错误：{timeline_errors[0].get('name', '')} {timeline_errors[0].get('error')}")
                bbox = model_bbox(info) if options["combined"] else None
                stage_native = work / f"{name}_辊密封.solidcraft"
                stage_step = work / f"{name}_辊密封.step"
                client.execute("file.save_as", {"path": str(stage_native), "format": "container", "cache": True, "preview": True})
                client.execute("file.export", {"path": str(stage_step), "format": "step"})
                shutil.copy2(stage_native, output / stage_native.name)
                shutil.copy2(stage_step, output / stage_step.name)
                if bbox:
                    bboxes.append(bbox)
                    step_paths.append(stage_step)
                self.events.put(("log", f"已导出 {name}_辊密封.solidcraft 和 .step"))
            if options["combined"]:
                self.events.put(("status", "正在排布组合 STEP，并检查实体相交…"))
                client.execute("file.new", {"name": "辊密封组合"})
                cursor_x = 0.0
                for (name, _params), step_path, (lo, hi) in zip(options["rows"], step_paths, bboxes):
                    x_move = cursor_x - lo[0]
                    matrix = [1, 0, 0, x_move, 0, 1, 0, 0, 0, 0, 1, 0]
                    client.execute("file.insert_step", {"path": str(step_path), "name": name, "matrix": matrix, "up": "z"})
                    cursor_x += (hi[0] - lo[0]) + options["gap"]
                interference = client.execute("inspect.interference", {"budget_s": 240})
                overlaps = interference.get("overlaps", [])
                unchecked = interference.get("unchecked", [])
                if overlaps or unchecked or interference.get("truncated") or interference.get("budget_exceeded"):
                    raise UserError(f"组合 STEP 未导出：相交 {len(overlaps)} 对，未核验 {len(unchecked)} 项。请查看 SolidCraft 组合模型后再处理。")
                client.execute("file.export", {"path": str(work / combined_target.name), "format": "step"})
                shutil.copy2(work / combined_target.name, combined_target)
                self.events.put(("log", f"组合 STEP 已导出；相交 0 对，未核验 0 项。{combined_target.name}"))
            self.events.put(("done", f"完成 {len(options['rows'])} 个型号。输出目录：{output}"))
            try:
                shutil.rmtree(work)
            except OSError:
                pass
        except Exception as exc:
            self.events.put(("error", f"{exc}\n本次临时文件：{work}" if work else str(exc)))
        finally:
            if client:
                client.close()

    def _log(self, message: str) -> None:
        self.log.configure(state="normal")
        self.log.insert("end", message + "\n")
        self.log.see("end")
        self.log.configure(state="disabled")

    def _poll_events(self) -> None:
        try:
            while True:
                kind, text = self.events.get_nowait()
                if kind == "log":
                    self._log(text)
                elif kind == "status":
                    self.status.set(text)
                else:
                    self.running = False
                    self.run_button.configure(state="normal")
                    self.status.set(text.splitlines()[0])
                    if kind == "done":
                        self._log(text)
                        messagebox.showinfo("完成", text, parent=self.root)
                    else:
                        self._log("失败：" + text)
                        messagebox.showerror("生成中止", text, parent=self.root)
        except queue.Empty:
            pass
        self.root.after(120, self._poll_events)


def main() -> None:
    root = tk.Tk()
    App(root)
    root.mainloop()


if __name__ == "__main__":
    main()
