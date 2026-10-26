namespace SolidCraftCsvTool;

internal sealed class DrawingSettingsForm : Form
{
    readonly TextBox template=new(){ReadOnly=true,Dock=DockStyle.Fill};
    readonly ComboBox styles=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Left,Width=260};
    readonly NumericUpDown hidden=new(){Minimum=.05m,Maximum=100,DecimalPlaces=2,Increment=.25m,Width=120};
    readonly NumericUpDown viewGap=new(){Minimum=10,Maximum=5000,Width=120};
    readonly NumericUpDown margin=new(){Minimum=10,Maximum=1000,Width=120};
    readonly NumericUpDown sheetGap=new(){Minimum=10,Maximum=5000,Width=120};
    readonly CheckBox frame=new(){Text="添加图框",AutoSize=true};
    public DrawingOptions Value {get;private set;}
    public DrawingSettingsForm(DrawingOptions initial)
    {
        Value=initial;Text="出图设置";Font=new Font("Microsoft YaHei UI",9.5f);
        ClientSize=new Size(680,390);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(22),ColumnCount=2,RowCount=8};
        layout.ColumnStyles.Add(new(SizeType.Absolute,130));layout.ColumnStyles.Add(new(SizeType.Percent,100));Controls.Add(layout);
        var file=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3};
        file.ColumnStyles.Add(new(SizeType.Percent,100));file.ColumnStyles.Add(new(SizeType.Absolute,76));file.ColumnStyles.Add(new(SizeType.Absolute,60));
        file.Controls.Add(template,0,0);
        var browse=new Button{Text="选择…",Dock=DockStyle.Fill};var clear=new Button{Text="清除",Dock=DockStyle.Fill};file.Controls.Add(browse,1,0);file.Controls.Add(clear,2,0);
        AddRow(0,"DXF 模板",file);AddRow(1,"标注样式",styles);AddRow(2,"隐藏线比例",hidden);AddRow(3,"视图净间距 / mm",viewGap);AddRow(4,"图框留白 / mm",margin);AddRow(5,"图纸间距 / mm",sheetGap);AddRow(6,"图框",frame);
        template.Text=initial.TemplatePath;hidden.Value=(decimal)initial.HiddenLineScale;viewGap.Value=(decimal)initial.ViewGap;margin.Value=(decimal)initial.FrameMargin;sheetGap.Value=(decimal)initial.SheetGap;frame.Checked=initial.IncludeFrame;
        LoadStyles(initial.DimensionStyleName);
        browse.Click+=(_,_)=>
        {
            using var dialog=new OpenFileDialog{Filter="DXF 图框与标注模板 (*.dxf)|*.dxf",Title="选择图框和标注样式模板"};
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            string previous=template.Text;template.Text=dialog.FileName;
            try{LoadStyles("_5");}catch(Exception ex){template.Text=previous;MessageBox.Show(this,ex.Message,"模板读取失败",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        };
        clear.Click+=(_,_)=>{template.Text="";LoadStyles("_5");};
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};
        var ok=new Button{Text="保存",AutoSize=true};var cancel=new Button{Text="取消",DialogResult=DialogResult.Cancel,AutoSize=true};buttons.Controls.Add(ok);buttons.Controls.Add(cancel);layout.Controls.Add(buttons,1,7);CancelButton=cancel;AcceptButton=ok;
        ok.Click+=(_,_)=>
        {
            try
            {
                var value=new DrawingOptions{TemplatePath=template.Text,DimensionStyleName=Convert.ToString(styles.SelectedItem)??"_5",HiddenLineScale=(double)hidden.Value,ViewGap=(double)viewGap.Value,FrameMargin=(double)margin.Value,SheetGap=(double)sheetGap.Value,IncludeFrame=frame.Checked};
                _=DrawingResources.Load(value);Value=value;DialogResult=DialogResult.OK;Close();
            }
            catch(Exception ex){MessageBox.Show(this,ex.Message,"请检查出图设置",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        };
        void AddRow(int row,string label,Control control)
        {
            layout.RowStyles.Add(new(SizeType.Absolute,row==0?44:38));
            layout.Controls.Add(new Label{Text=label,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,row);layout.Controls.Add(control,1,row);control.Margin=new Padding(3,6,3,3);
        }
    }
    void LoadStyles(string selected)
    {
        string[] names=string.IsNullOrWhiteSpace(template.Text)?["_5"]:
            (netDxf.DxfDocument.Load(template.Text)??throw new InvalidDataException("无法读取 DXF 模板。")).DimensionStyles.Select(s=>s.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        styles.Items.Clear();styles.Items.AddRange(names);
        styles.SelectedItem=names.FirstOrDefault(n=>n.Equals(selected,StringComparison.OrdinalIgnoreCase))??names.FirstOrDefault();
    }
}
