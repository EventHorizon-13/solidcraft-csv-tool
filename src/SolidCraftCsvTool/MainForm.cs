using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace SolidCraftCsvTool;

public sealed class MainForm : Form
{
    readonly DataGridView grid = new();
    readonly TextBox template = new(), output = new(), log = new();
    readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Value = 7878, Width = 78 };
    readonly NumericUpDown gap = new() { Minimum = 0.1m, Maximum = 1000000, DecimalPlaces = 1, Value = 10, Width = 82 };
    readonly CheckBox combined = new() { Text = "另导出组合 STEP", Checked = true, AutoSize = true };
    readonly CheckBox overwrite = new() { Text = "覆盖已有文件", AutoSize = true };
    readonly Button run = new() { Text = "生成并导出", AutoSize = true, Height = 34, BackColor = Color.FromArgb(29, 78, 216), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    readonly Button stop = new() { Text = "停止", AutoSize = true, Enabled = false };
    readonly ToolStripStatusLabel status = new("准备就绪");
    readonly ToolStripProgressBar progressBar = new() { Width = 150 };
    readonly List<Control> editControls = [];
    readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidCraft", "csv-tool", "settings.json");
    CancellationTokenSource? cancellation;
    string? solidCraftExe;

    public MainForm()
    {
        Text = "SolidCraft 参数与批量导出";
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(1360, 810); MinimumSize = new Size(1000, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(247, 248, 250);
        AutoScaleMode = AutoScaleMode.Dpi;
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 8), ColumnCount = 1, RowCount = 7 };
        layout.RowStyles.Add(new(SizeType.Absolute, 58));
        layout.RowStyles.Add(new(SizeType.Absolute, 88));
        layout.RowStyles.Add(new(SizeType.Absolute, 48));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 32));
        layout.RowStyles.Add(new(SizeType.Absolute, 98));
        layout.RowStyles.Add(new(SizeType.Absolute, 54));
        Controls.Add(layout);

        Label heading = new() { Text = "参数与批量导出", Font = new Font(Font.FontFamily, 22, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        layout.Controls.Add(heading, 0, 0);
        TableLayoutPanel paths = new() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        paths.ColumnStyles.Add(new(SizeType.Absolute, 98)); paths.ColumnStyles.Add(new(SizeType.Percent, 100)); paths.ColumnStyles.Add(new(SizeType.Absolute, 96));
        PathRow(paths, 0, "V13 模板", template, ChooseTemplate);
        PathRow(paths, 1, "输出文件夹", output, ChooseOutput);
        layout.Controls.Add(paths, 0, 1);

        FlowLayoutPanel toolbar = new() { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 6, 0, 0) };
        toolbar.Controls.Add(MakeButton("导入 CSV", ImportCsv));
        toolbar.Controls.Add(MakeButton("导出 CSV", ExportCsv));
        toolbar.Controls.Add(MakeButton("新增", () => AddRow(ModelRules.DefaultRow((grid.Rows.Count + 1).ToString()))));
        toolbar.Controls.Add(MakeButton("复制选中", DuplicateSelected));
        toolbar.Controls.Add(MakeButton("删除选中", DeleteSelected));
        toolbar.Controls.Add(MakeButton("检查参数", () => { var rows = ModelRules.Validate(GetRows()); SetStatus($"{rows.Count} 行参数检查通过"); }));
        toolbar.Controls.Add(MakeButton("启动 SolidCraft…", LaunchSolidCraft));
        toolbar.Controls.Add(MakeButton("STEP 批量出图 PDF", () => { _ = RunStepDrawing(); }));
        layout.Controls.Add(toolbar, 0, 2);

        grid.Dock = DockStyle.Fill; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
        grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None; grid.RowHeadersVisible = true;
        grid.RowHeadersWidth = 42; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = true; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.ColumnHeadersHeight = 42; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.RowTemplate.Height = 36; grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(235, 238, 243), ForeColor = Color.FromArgb(45, 55, 72), Alignment = DataGridViewContentAlignment.MiddleCenter, Font = new Font(Font, FontStyle.Bold) };
        grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(30, 41, 59);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 250, 252);
        grid.GridColor = Color.FromArgb(228, 232, 238);
        foreach (string name in ModelRules.Columns)
        {
            DataGridViewColumn column = ModelRules.Flags.Contains(name)
                ? new DataGridViewCheckBoxColumn { ThreeState = false }
                : new DataGridViewTextBoxColumn();
            column.Name = name; column.HeaderText = name.Replace("半圆", "半孔"); column.MinimumWidth = 65;
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.Columns.Add(column);
        }
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.RowPostPaint += (_, e) => TextRenderer.DrawText(e.Graphics, (e.RowIndex + 1).ToString(), Font, new Rectangle(e.RowBounds.Left, e.RowBounds.Top, grid.RowHeadersWidth - 5, e.RowBounds.Height), Color.DimGray, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        grid.DataError += (_, e) => { e.ThrowException = false; SetStatus("此单元格的值无效，请重新输入。"); };
        editControls.Add(grid); layout.Controls.Add(grid, 0, 3);
        layout.Controls.Add(new Label { Text = ".5 孔选一侧半孔；整数孔选两侧或都不选。尺寸单位：mm。", ForeColor = Color.DimGray, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
        log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical; log.Dock = DockStyle.Fill;
        log.BackColor = Color.White; log.BorderStyle = BorderStyle.FixedSingle;
        layout.Controls.Add(log, 0, 5);

        FlowLayoutPanel footer = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
        footer.Controls.Add(combined); footer.Controls.Add(new Label { Text = "间距", AutoSize = true, Margin = new Padding(12, 5, 3, 0) }); footer.Controls.Add(gap);
        footer.Controls.Add(overwrite); footer.Controls.Add(new Label { Text = "控制端口", AutoSize = true, Margin = new Padding(12, 5, 3, 0) }); footer.Controls.Add(port);
        footer.Controls.Add(run); footer.Controls.Add(stop);
        editControls.AddRange([combined, gap, overwrite, port]);
        run.Click += async (_, _) => await RunExport();
        stop.Click += (_, _) => { cancellation?.Cancel(); stop.Enabled = false; SetStatus("当前命令完成后停止；已导出的单件会保留。"); };
        combined.CheckedChanged += (_, _) => gap.Enabled = combined.Checked;
        layout.Controls.Add(footer, 0, 6);
        StatusStrip strip = new() { SizingGrip = true, BackColor = BackColor };
        status.Spring = true; status.TextAlign = ContentAlignment.MiddleLeft;
        strip.Items.Add(status); strip.Items.Add(progressBar); Controls.Add(strip); strip.Dock = DockStyle.Bottom;
        output.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SolidCraft输出");
        LoadSettings(); AddRow(ModelRules.DefaultRow());
        FormClosing += (_, e) =>
        {
            if (cancellation != null) { cancellation.Cancel(); e.Cancel = true; stop.Enabled = false; SetStatus("当前命令完成后停止。停止完成后可以关闭窗口。"); }
            else SaveSettings();
        };
    }

    void PathRow(TableLayoutPanel panel, int row, string text, TextBox box, Action choose)
    {
        panel.RowStyles.Add(new(SizeType.Percent, 50));
        panel.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        box.Dock = DockStyle.Fill; box.Margin = new Padding(0, 8, 8, 4); panel.Controls.Add(box, 1, row); editControls.Add(box);
        var button = MakeButton("选择…", choose); button.Dock = DockStyle.Fill; button.Margin = new Padding(0, 5, 0, 5); panel.Controls.Add(button, 2, row);
    }

    Button MakeButton(string text, Action action)
    {
        Button button = new() { Text = text, AutoSize = true, Height = 31, Padding = new Padding(4, 0, 4, 0), Margin = new Padding(0, 0, 8, 0) };
        button.Click += (_, _) => { try { grid.EndEdit(); action(); } catch (Exception ex) { ShowError(ex.Message); } };
        editControls.Add(button); return button;
    }

    void AddRow(string[] cells) => grid.Rows.Add(cells.Select((value, i) => ModelRules.Flags.Contains(ModelRules.Columns[i]) ? (object)(value == "1") : value).ToArray());

    List<string[]> GetRows()
    {
        grid.EndEdit();
        return grid.Rows.Cast<DataGridViewRow>().Select(row => ModelRules.Columns.Select((col, i) =>
            ModelRules.Flags.Contains(col) ? (row.Cells[i].Value is true ? "1" : "0") : Convert.ToString(row.Cells[i].Value, CultureInfo.InvariantCulture)?.Trim() ?? "").ToArray()).ToList();
    }

    void ImportCsv()
    {
        using OpenFileDialog dialog = new() { Filter = "CSV 参数表 (*.csv)|*.csv|所有文件 (*.*)|*.*", Title = "导入 CSV" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var rows = CsvFiles.Read(dialog.FileName); grid.Rows.Clear(); foreach (var row in rows) AddRow(row);
        Log($"已导入 {rows.Count} 行：{dialog.FileName}"); SetStatus($"已导入 {rows.Count} 行");
    }

    void ExportCsv()
    {
        var rows = GetRows(); ModelRules.Validate(rows);
        using SaveFileDialog dialog = new() { Filter = "CSV 参数表 (*.csv)|*.csv", FileName = "辊密封.csv", DefaultExt = "csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        CsvFiles.Write(dialog.FileName, rows); Log("已导出 CSV：" + dialog.FileName);
    }

    void ChooseTemplate()
    {
        using OpenFileDialog dialog = new() { Filter = "SolidCraft 模板 (*.solidcraft)|*.solidcraft" };
        if (dialog.ShowDialog(this) == DialogResult.OK) { template.Text = dialog.FileName; SaveSettings(); }
    }

    void ChooseOutput()
    {
        using FolderBrowserDialog dialog = new() { Description = "选择输出文件夹", UseDescriptionForTitle = true, SelectedPath = output.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK) { output.Text = dialog.SelectedPath; SaveSettings(); }
    }

    void DuplicateSelected()
    {
        var selected = grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Order().ToArray();
        var rows = GetRows(); var used = rows.Select(r => r[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (int i in selected)
        {
            var copy = (string[])rows[i].Clone(); string candidate = copy[0] + "_副本"; int suffix = 2;
            while (used.Contains(candidate)) candidate = copy[0] + "_副本" + suffix++;
            copy[0] = candidate; used.Add(candidate); AddRow(copy);
        }
    }

    void DeleteSelected() { foreach (var row in grid.SelectedRows.Cast<DataGridViewRow>().OrderByDescending(r => r.Index)) grid.Rows.Remove(row); }

    void LaunchSolidCraft()
    {
        if (solidCraftExe == null || !File.Exists(solidCraftExe))
        {
            using OpenFileDialog dialog = new() { Title = "选择 SolidCraft 桌面程序", Filter = "SolidCraft (solidcraft.exe)|solidcraft.exe|应用程序 (*.exe)|*.exe" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            solidCraftExe = dialog.FileName;
        }
        var start = new ProcessStartInfo(solidCraftExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(solidCraftExe)! };
        start.ArgumentList.Add("--control"); start.ArgumentList.Add(port.Value.ToString(CultureInfo.InvariantCulture));
        Process.Start(start); SaveSettings(); Log("已启动 SolidCraft；桌面版加载完成后可生成导出。");
    }

    async Task RunExport()
    {
        try
        {
            var rows = ModelRules.Validate(GetRows());
            string templateFile = template.Text.Trim(), outputFolder = output.Text.Trim();
            if (!File.Exists(templateFile) || !string.Equals(Path.GetExtension(templateFile), ".solidcraft", StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择 V13 .solidcraft 模板。");
            if (outputFolder.Length == 0) throw new IOException("请选择输出文件夹。");
            var options = new ExportOptions(rows, Path.GetFullPath(templateFile), Path.GetFullPath(outputFolder), (int)port.Value, (double)gap.Value, combined.Checked, overwrite.Checked);
            SaveSettings(); cancellation = new(); SetRunning(true); progressBar.Maximum = rows.Count; progressBar.Value = 0;
            Progress<ExportProgress> progress = new(p => { SetStatus(p.Message); progressBar.Value = Math.Min(progressBar.Maximum, p.Completed); if (p.Log) Log(p.Message); });
            var token = cancellation.Token;
            await Task.Run(() => Exporter.RunAsync(options, progress, token));
            SetStatus($"完成：{rows.Count} 个型号"); Log("输出目录：" + options.Output);
            MessageBox.Show(this, $"已导出 {rows.Count} 个型号。\n{options.Output}", "导出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) { SetStatus("已停止，已导出的文件保留。"); Log("已按请求停止。"); }
        catch (Exception ex) { Log("导出中止：" + ex.Message); ShowError(ex.Message); SetStatus("导出中止"); }
        finally { cancellation?.Dispose(); cancellation = null; SetRunning(false); }
    }

    async Task RunStepDrawing()
    {
        using OpenFileDialog input = new() { Filter = "STEP 三维文件 (*.step;*.stp)|*.step;*.stp", Multiselect = true, Title = "选择要批量生成加工图的 STEP 文件" };
        if (input.ShowDialog(this) != DialogResult.OK) return;
        using SaveFileDialog outputFile = new() { Filter = "PDF 加工图 (*.pdf)|*.pdf", DefaultExt = "pdf", AddExtension = true, FileName = "辊密封_加工图.pdf", Title = "保存多页加工图 PDF" };
        if (outputFile.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            string[] files = input.FileNames.OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray();
            var target = Path.GetFullPath(outputFile.FileName);
            if (File.Exists(target)) throw new IOException("目标 PDF 已存在，请选择新文件名；当前入口不会覆盖原图纸。");
            cancellation = new(); SetRunning(true); progressBar.Maximum = files.Length; progressBar.Value = 0;
            Progress<ExportProgress> progress = new(p => { SetStatus(p.Message); progressBar.Value = Math.Min(progressBar.Maximum, p.Completed); if (p.Log) Log(p.Message); });
            await Task.Run(() => StepDrawingExporter.RunAsync(files, target, (int)port.Value, progress, cancellation.Token));
            SetStatus($"加工图完成：{files.Length} 个 STEP"); Log("多页 PDF：" + target);
            MessageBox.Show(this, $"已生成 {files.Length} 页加工图。\n{target}", "出图完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) { SetStatus("已停止；未完成的 PDF 不会发布。"); Log("STEP 批量出图已停止。"); }
        catch (Exception ex) { Log("STEP 出图中止：" + ex.Message); ShowError(ex.Message); SetStatus("STEP 出图中止"); }
        finally { cancellation?.Dispose(); cancellation = null; SetRunning(false); }
    }

    void SetRunning(bool running) { foreach (var control in editControls) control.Enabled = !running; run.Enabled = !running; stop.Enabled = running; grid.ReadOnly = running; if (!running) gap.Enabled = combined.Checked; }
    void SetStatus(string text) => status.Text = text;
    void ShowError(string text) => MessageBox.Show(this, text, "请检查", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    void Log(string text) { log.AppendText($"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}"); if (log.Lines.Length > 500) log.Lines = log.Lines.TakeLast(400).ToArray(); log.SelectionStart = log.TextLength; log.ScrollToCaret(); }

    void LoadSettings()
    {
        try
        {
            if (!File.Exists(settingsPath)) return;
            using var data = JsonDocument.Parse(File.ReadAllText(settingsPath)); var root = data.RootElement;
            if (root.TryGetProperty("template", out var t)) template.Text = t.GetString() ?? "";
            if (root.TryGetProperty("output", out var o)) output.Text = o.GetString() ?? output.Text;
            if (root.TryGetProperty("exe", out var e)) solidCraftExe = e.GetString();
            if (root.TryGetProperty("port", out var p) && p.TryGetInt32(out int value) && value >= 1 && value <= 65535) port.Value = value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Log("未能读取上次设置：" + ex.Message); }
    }

    void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            string stage = settingsPath + ".tmp";
            File.WriteAllText(stage, JsonSerializer.Serialize(new { template = template.Text, output = output.Text, exe = solidCraftExe, port = (int)port.Value }));
            File.Move(stage, settingsPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("未能保存设置：" + ex.Message); }
    }

    public void VerifyUi()
    {
        if (grid.Columns.Count != 14 || grid.Rows.Count == 0 || grid.Columns.Cast<DataGridViewColumn>().Count(c => c is DataGridViewCheckBoxColumn) != 6)
            throw new InvalidOperationException("GUI 参数表初始化失败。");
        ModelRules.Validate(GetRows());
        if (!run.Enabled || stop.Enabled) throw new InvalidOperationException("GUI 初始按钮状态不正确。");
    }
}
