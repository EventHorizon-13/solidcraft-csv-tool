namespace SolidCraftCsvTool;

internal sealed class DrawingSettingsForm : Form
{
    readonly TextBox template = new() { ReadOnly = true, Dock = DockStyle.Fill };
    readonly TextBox fonts = new() { ReadOnly = true, Dock = DockStyle.Fill };
    readonly ComboBox styles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly NumericUpDown hidden = new() { Minimum = .05m, Maximum = 100, DecimalPlaces = 2, Increment = .25m, Dock = DockStyle.Fill };
    readonly NumericUpDown viewGap = new() { Minimum = 10, Maximum = 5000, Dock = DockStyle.Fill };
    readonly NumericUpDown margin = new() { Minimum = 10, Maximum = 1000, Dock = DockStyle.Fill };
    readonly NumericUpDown sheetGap = new() { Minimum = 10, Maximum = 5000, Dock = DockStyle.Fill };
    readonly CheckBox frame = new ModernCheckBox() { Text = "添加图框与标题栏", AutoSize = true };
    readonly TableLayoutPanel titleFields = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 0, Padding = new Padding(10) };
    readonly Dictionary<string, TextBox> titleEditors = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> titleValues;
    public DrawingOptions Value { get; private set; }

    public DrawingSettingsForm(DrawingOptions initial)
    {
        Value = initial;
        titleValues = new(initial.TitleBlockAttributes ?? [], StringComparer.OrdinalIgnoreCase);
        Text = "出图设置"; Icon = AppArtwork.LoadIcon(); Font = new Font("Microsoft YaHei UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(820, 740); MinimumSize = new Size(740, 580);
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(20) };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.AutoSize)); Controls.Add(root);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var sections = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 0, AutoSize = true, Padding = new Padding(0, 0, 18, 0) };
        sections.ColumnStyles.Add(new(SizeType.Percent, 100)); scroll.Controls.Add(sections); root.Controls.Add(scroll, 0, 0);
        var paths = FormGrid();
        var browse = MakeButton("选择…"); var clear = MakeButton("清除"); var chooseFonts = MakeButton("选择…");
        AddRow(paths, "DXF 图框模板", PathPicker(template, browse, clear));
        AddRow(paths, "SHX 字体目录", PathPicker(fonts, chooseFonts)); AddRow(paths, "图框", frame);
        AddSection("图框与字体", paths);
        var dimensions = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = 3, Padding = new Padding(10) };
        dimensions.ColumnStyles.Add(new(SizeType.Absolute, 144)); dimensions.ColumnStyles.Add(new(SizeType.Percent, 50));
        dimensions.ColumnStyles.Add(new(SizeType.Absolute, 156)); dimensions.ColumnStyles.Add(new(SizeType.Percent, 50));
        for(int row=0;row<3;row++)dimensions.RowStyles.Add(new(SizeType.AutoSize));
        AddSetting("标注样式", styles, 0, 0); dimensions.SetColumnSpan(styles, 3);
        AddSetting("隐藏线比例", hidden, 0, 1); AddSetting("视图净间距 / mm", viewGap, 2, 1);
        AddSetting("图框留白 / mm", margin, 0, 2); AddSetting("图纸间距 / mm", sheetGap, 2, 2);
        AddSection("标注与排布", dimensions);
        titleFields.ColumnStyles.Add(new(SizeType.Absolute, 156)); titleFields.ColumnStyles.Add(new(SizeType.Percent, 100));
        var titleGroup = AddSection("标题栏属性", titleFields);
        frame.CheckedChanged += (_, _) => titleGroup.Enabled = frame.Checked;
        template.Text = initial.TemplatePath; fonts.Text = initial.FontDirectory;
        hidden.Value = (decimal)initial.HiddenLineScale; viewGap.Value = (decimal)initial.ViewGap;
        margin.Value = (decimal)initial.FrameMargin; sheetGap.Value = (decimal)initial.SheetGap; frame.Checked = initial.IncludeFrame;
        titleGroup.Enabled = frame.Checked; LoadTemplate(initial.DimensionStyleName);
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "DXF 图框与标注模板 (*.dxf)|*.dxf", Title = "选择图框和标注样式模板" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            RememberTitles(); string previous = template.Text, selected = Convert.ToString(styles.SelectedItem) ?? "_5";
            template.Text = dialog.FileName;
            try { LoadTemplate(selected); }
            catch (Exception ex) { template.Text = previous; MessageBox.Show(this, ex.Message, "模板读取失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        clear.Click += (_, _) => { RememberTitles(); template.Text = ""; LoadTemplate("_5"); };
        chooseFonts.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择包含 gbenor.shx 和 gbcbig.shx 的外置字体文件夹", UseDescriptionForTitle = true, SelectedPath = fonts.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) fonts.Text = dialog.SelectedPath;
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 14, 0, 0), Margin = Padding.Empty };
        var ok = MakeButton("保存"); var cancel = MakeButton("取消"); cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel); root.Controls.Add(buttons, 0, 1); CancelButton = cancel; AcceptButton = ok;
        ok.Click += (_, _) =>
        {
            try
            {
                RememberTitles();
                var value = initial with { TemplatePath = template.Text, FontDirectory = fonts.Text, DimensionStyleName = Convert.ToString(styles.SelectedItem) ?? "_5", HiddenLineScale = (double)hidden.Value, ViewGap = (double)viewGap.Value, FrameMargin = (double)margin.Value, SheetGap = (double)sheetGap.Value, IncludeFrame = frame.Checked, TitleBlockAttributes = new(titleValues, StringComparer.OrdinalIgnoreCase) };
                _ = DrawingResources.Load(value); Value = value; DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "请检查出图设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        AppTheme.Attach(this);
        void AddSetting(string label,Control control,int column,int row)
        {
            dimensions.Controls.Add(new Label{Text=label,AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(column==0?0:14,8,8,8)},column,row);
            control.Margin=new Padding(0,5,0,5);dimensions.Controls.Add(control is NumericUpDown?Ui.Field(control):control,column+1,row);
        }
        SurfacePanel AddSection(string text, Control body)
        {
            var group=new SurfacePanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(16),Margin=new Padding(0,0,0,16)};
            var content=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
            content.ColumnStyles.Add(new(SizeType.Percent,100));content.RowStyles.Add(new(SizeType.AutoSize));content.RowStyles.Add(new(SizeType.AutoSize));
            content.Controls.Add(new Label{Text=text,Font=new Font(Font,FontStyle.Bold),AutoSize=true,Margin=new Padding(8,2,8,12)},0,0);
            content.Controls.Add(body,0,1);group.Controls.Add(content);sections.RowStyles.Add(new(SizeType.AutoSize));sections.Controls.Add(group,0,sections.RowCount++);return group;
        }
    }
    static Button MakeButton(string text) => new ModernButton { Text=text,MinimumSize=new Size(76,36),Margin=new Padding(6,0,0,0),Primary=text=="保存" };
    static TableLayoutPanel FormGrid()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 0, Padding = new Padding(10) };
        grid.ColumnStyles.Add(new(SizeType.Absolute, 156)); grid.ColumnStyles.Add(new(SizeType.Percent, 100)); return grid;
    }
    static void AddRow(TableLayoutPanel layout, string label, Control control)
    {
        int row = layout.RowCount++; layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 8) }, 0, row);
        control.Margin = new Padding(0, 5, 0, 5); control.Anchor = AnchorStyles.Left | AnchorStyles.Right; layout.Controls.Add(control is TextBoxBase?Ui.Field(control):control, 1, row);
    }
    static Control PathPicker(TextBox box, params Button[] buttons)
    {
        var picker = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = buttons.Length + 1, RowCount = 1, Margin = Padding.Empty };
        picker.ColumnStyles.Add(new(SizeType.Percent, 100)); box.Anchor = AnchorStyles.Left | AnchorStyles.Right; box.Margin = new Padding(0, 3, 0, 0); picker.Controls.Add(Ui.Field(box), 0, 0);
        for (int i = 0; i < buttons.Length; i++) { picker.ColumnStyles.Add(new(SizeType.AutoSize)); picker.Controls.Add(buttons[i], i + 1, 0); }
        return picker;
    }
    void RememberTitles()
    {
        foreach (var pair in titleEditors) titleValues[pair.Key] = pair.Value.Text.Trim();
    }
    void LoadTemplate(string selected)
    {
        string[] names = string.IsNullOrWhiteSpace(template.Text) ? ["_5"] :
            (netDxf.DxfDocument.Load(template.Text) ?? throw new InvalidDataException("无法读取 DXF 模板。")).DimensionStyles.Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string style = names.FirstOrDefault(n => n.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault() ?? "_5";
        var fields = DrawingResources.TitleFields(new() { TemplatePath = template.Text, DimensionStyleName = style });
        styles.Items.Clear(); styles.Items.AddRange(names); styles.SelectedItem = style;
        titleFields.SuspendLayout();
        foreach (Control control in titleFields.Controls.Cast<Control>().ToArray()) control.Dispose();
        titleFields.Controls.Clear(); titleFields.RowStyles.Clear(); titleFields.RowCount = 0; titleEditors.Clear();
        var hint = new Label { Text = fields.Count == 0 ? "选择含标题栏属性的 DXF 模板后，可在此编辑图号、材料、设计、审核、日期等实际字段。" : "留空保留模板原值；图名留空时，按每个零件的编号自动填写。下方灰字为模板原值。", AutoSize = true, Dock = DockStyle.Fill, Tag="muted", Margin = new Padding(0, 0, 0, 10) };
        titleFields.RowStyles.Add(new(SizeType.AutoSize)); titleFields.Controls.Add(hint, 0, titleFields.RowCount++); titleFields.SetColumnSpan(hint, 2);
        foreach (var field in fields)
        {
            var editor = new TextBox { Dock = DockStyle.Fill, Text = titleValues.GetValueOrDefault(field.Key) ?? "", PlaceholderText = field.Value };
            titleEditors.Add(field.Key, editor); AddRow(titleFields, field.Key, editor);
        }
        titleFields.ResumeLayout(true); AppTheme.Apply(this);
    }
}
