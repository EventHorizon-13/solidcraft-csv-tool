using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace SolidCraftCsvTool;

public partial class MainWindow : Window
{
    readonly DataTable table=new();
    readonly DesktopSettings settings;
    string displayedStamp=OutputNames.Stamp;
    CancellationTokenSource? cancellation;
    DrawingOptions drawingOptions;
    AppearanceMode appearance;
    const int ControlPort=7878;
    const double CombinedGap=10;
    string nativeTemplatePath="";
    internal MainWindow(DesktopSettings settings)
    {
        this.settings=settings;drawingOptions=settings.drawing??new();appearance=settings.Appearance;
        InitializeComponent();Title=AppBuildInfo.WindowTitle;
        output.Text=settings.output;dxfBase.Text=settings.dxfBase??OutputNames.DefaultBase();stepBase.Text=settings.stepBase??OutputNames.DefaultBase();
        combined.IsChecked=settings.combined;taskCsv.IsChecked=settings.taskCsv;overwrite.IsChecked=settings.overwrite;
        openDxf.IsChecked=settings.openDxf;exportPdf.IsChecked=settings.exportPdf;
        foreach(string name in ModelRules.Columns)
        {
            bool flag=ModelRules.Flags.Contains(name);table.Columns.Add(name,flag?typeof(bool):typeof(string));
            var binding=new Binding($"[{name}]"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged};
            DataGridColumn column;
            if(flag)
            {
                // Make the checkbox itself hit-testable in display mode so one click toggles it.
                var checkStyle=new Style(typeof(CheckBox));
                checkStyle.Setters.Add(new Setter(UIElement.IsHitTestVisibleProperty,true));
                checkStyle.Setters.Add(new Setter(UIElement.FocusableProperty,false));
                checkStyle.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center));
                checkStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center));
                column=new DataGridCheckBoxColumn{Binding=binding,ElementStyle=checkStyle,EditingElementStyle=checkStyle};
            }
            else
            {
                var display=new Style(typeof(TextBlock));
                display.Setters.Add(new Setter(TextBlock.TextAlignmentProperty,TextAlignment.Center));
                display.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Stretch));
                display.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center));
                display.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));
                var edit=new Style(typeof(TextBox));
                edit.Setters.Add(new Setter(TextBox.TextAlignmentProperty,TextAlignment.Center));
                edit.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty,VerticalAlignment.Center));
                column=new DataGridTextColumn{Binding=binding,ElementStyle=display,EditingElementStyle=edit};
            }
            column.Header=name.Replace("半圆","半孔");column.MinWidth=64;column.Width=new DataGridLength(1,DataGridLengthUnitType.Star);column.CanUserSort=false;
            grid.Columns.Add(column);
        }
        var headerStyle=new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Center));
        headerStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty,VerticalAlignment.Center));
        // Fluent's default column header can retain a light surface after switching to dark mode.
        // Bind to the DataGrid's theme-aware brushes so Light, Dark and System stay in sync.
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty,new Binding(nameof(Control.Background)){Source=grid,Mode=BindingMode.OneWay}));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty,new Binding(nameof(Control.Foreground)){Source=grid,Mode=BindingMode.OneWay}));
        headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty,new Binding(nameof(Control.BorderBrush)){Source=grid,Mode=BindingMode.OneWay}));
        headerStyle.Setters.Add(new Setter(System.Windows.Controls.Primitives.DataGridColumnHeader.SeparatorBrushProperty,new Binding(nameof(Control.BorderBrush)){Source=grid,Mode=BindingMode.OneWay}));
        headerStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,"双击列头：批量修改；勾选列：全选/取消全选"));
        grid.ColumnHeaderStyle=headerStyle;
        grid.ItemsSource=table.DefaultView;InitializeSpreadsheetInteractions();ResolveLocalTemplates();AddRow(ModelRules.DefaultRow());InitializeUndoRedo();
        Closing+=OnClosing;
    }
    void OnClosing(object? sender,CancelEventArgs e)
    {
        if(cancellation!=null){cancellation.Cancel();e.Cancel=true;stop.IsEnabled=false;SetStatus("当前命令完成后停止，随后可以关闭窗口。");return;}
        try{SaveSettings();}catch(Exception ex){Log("未能保存设置："+ex.Message);}
    }
    void ResolveLocalTemplates()
    {
        string builtin=Path.Combine(AppContext.BaseDirectory,"模板","标准辊密封.solidcraft");
        nativeTemplatePath=File.Exists(builtin)?builtin:Path.Combine(Environment.CurrentDirectory,"模板","标准辊密封.solidcraft");
        var found=LocalTemplates.Find(AppContext.BaseDirectory,Environment.CurrentDirectory);
        if(!File.Exists(drawingOptions.TemplatePath))drawingOptions=drawingOptions with{TemplatePath=found.Dxf??""};
        if(!Directory.Exists(drawingOptions.FontDirectory))
        {
            string fonts=Path.Combine(AppContext.BaseDirectory,"模板","字体");
            drawingOptions=drawingOptions with{FontDirectory=Directory.Exists(fonts)?fonts:""};
        }
    }
    void RefreshDefaultNames()
    {
        string now=OutputNames.Stamp;
        if(dxfBase.Text==OutputNames.DefaultBase(displayedStamp))dxfBase.Text=OutputNames.DefaultBase(now);
        if(stepBase.Text==OutputNames.DefaultBase(displayedStamp))stepBase.Text=OutputNames.DefaultBase(now);displayedStamp=now;
    }
    void SaveSettings()
    {
        settings.output=output.Text;settings.combined=combined.IsChecked==true;settings.taskCsv=taskCsv.IsChecked==true;
        settings.overwrite=overwrite.IsChecked==true;settings.openDxf=openDxf.IsChecked==true;settings.exportPdf=exportPdf.IsChecked==true;
        settings.themeMode=appearance.ToString();settings.drawing=drawingOptions;
        settings.dxfBase=dxfBase.Text==OutputNames.DefaultBase(displayedStamp)?null:dxfBase.Text;
        settings.stepBase=stepBase.Text==OutputNames.DefaultBase(displayedStamp)?null:stepBase.Text;settings.Save();
    }
    void AddRow(string[] cells)=>table.Rows.Add(cells.Select((value,i)=>ModelRules.Flags.Contains(ModelRules.Columns[i])?(object)(value=="1"):value).ToArray());
    void CommitRows(){grid.CommitEdit(DataGridEditingUnit.Cell,true);grid.CommitEdit(DataGridEditingUnit.Row,true);}
    List<string[]> GetRows()
    {
        CommitRows();return table.Rows.Cast<DataRow>().Where(r=>r.RowState!=DataRowState.Deleted).Select(r=>ModelRules.Columns.Select(c=>
            ModelRules.Flags.Contains(c)?(r[c] is true?"1":"0"):Convert.ToString(r[c],CultureInfo.InvariantCulture)?.Trim()??"").ToArray()).ToList();
    }
    void Guard(Action action){try{CommitRows();action();}catch(Exception ex){ShowError(ex.Message);}}
    void ImportCsv(object sender,RoutedEventArgs e)=>Guard(()=>
    {
        var dialog=new OpenFileDialog{Filter="CSV 参数表 (*.csv)|*.csv|所有文件 (*.*)|*.*"};if(dialog.ShowDialog(this)!=true)return;
        var rows=CsvFiles.Read(dialog.FileName);
        RunUndoable(()=>{table.Rows.Clear();foreach(var row in rows)AddRow(row);});
        grid.SelectedCells.Clear();grid.UnselectAll();
        Log($"已导入 {rows.Count} 行：{dialog.FileName}");SetStatus($"已导入 {rows.Count} 行");
    });
    void ExportCsv(object sender,RoutedEventArgs e)=>Guard(()=>
    {
        var rows=GetRows();ModelRules.Validate(rows);var dialog=new SaveFileDialog{Filter="CSV 参数表 (*.csv)|*.csv",FileName="辊密封.csv",DefaultExt=".csv"};
        if(dialog.ShowDialog(this)==true){CsvFiles.Write(dialog.FileName,rows);Log("已导出 CSV："+dialog.FileName);}
    });
    void ChooseOutput(object sender,RoutedEventArgs e)=>Guard(()=>
    {
        var dialog=new OpenFolderDialog{Title="选择输出文件夹",InitialDirectory=Directory.Exists(output.Text)?output.Text:""};
        if(dialog.ShowDialog(this)==true){output.Text=dialog.FolderName;SaveSettings();}
    });
    void AddRowClick(object sender,RoutedEventArgs e)=>Guard(()=>RunUndoable(()=>AddRow(ModelRules.DefaultRow((table.Rows.Count+1).ToString()))));
    void DuplicateSelected(object sender,RoutedEventArgs e)=>Guard(()=>RunUndoable(()=>
    {
        var selected=GetSelectedRows();var used=GetRows().Select(r=>r[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var row in selected)
        {
            var copy=ModelRules.Columns.Select(c=>ModelRules.Flags.Contains(c)?(row[c] is true?"1":"0"):Convert.ToString(row[c])??"").ToArray();
            string stem=copy[0]+"_副本",candidate=stem;int suffix=2;while(used.Contains(candidate))candidate=stem+suffix++;
            copy[0]=candidate;used.Add(candidate);AddRow(copy);
        }
    }));
    void DeleteSelected(object sender,RoutedEventArgs e)=>Guard(()=>RunUndoable(()=>{foreach(var row in GetSelectedRows())table.Rows.Remove(row);}));
    void CheckRows(object sender,RoutedEventArgs e)=>Guard(()=>SetStatus($"{ModelRules.Validate(GetRows()).Count} 行参数检查通过"));
    void ConfigureDrawing(object sender,RoutedEventArgs e)=>Guard(()=>
    {
        var dialog=new SettingsWindow(drawingOptions,appearance){Owner=this};
        if(dialog.ShowDialog()==true){drawingOptions=dialog.Value;appearance=dialog.Appearance;SaveSettings();Log("设置已保存。");}
    });
    void ShowAbout(object sender,RoutedEventArgs e)=>new AboutWindow{Owner=this}.ShowDialog();
    async void RunExportClick(object sender,RoutedEventArgs e)=>await RunExport();
    async void RunDxfClick(object sender,RoutedEventArgs e)=>await RunExport(true);
    async void StepDrawingClick(object sender,RoutedEventArgs e)=>await RunStepDrawing();
    void StopClick(object sender,RoutedEventArgs e){cancellation?.Cancel();stop.IsEnabled=false;SetStatus("当前命令完成后停止；已导出的文件保留。");}
    void SetStatus(string text)=>status.Text=text;
    void Log(string text){log.AppendText($"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}");if(log.Text.Length>60000)log.Text=log.Text[^40000..];log.ScrollToEnd();}
    void ShowError(string text)=>MessageBox.Show(this,text,"请检查",MessageBoxButton.OK,MessageBoxImage.Warning);
    void SetRunning(bool running)
    {
        Metadata.IsEnabled=!running;Tools.IsEnabled=!running;Info.IsEnabled=!running;Choices.IsEnabled=!running;grid.IsReadOnly=running;
        run.IsEnabled=!running;runDxf.IsEnabled=!running;stop.IsEnabled=running;ScheduleFillHandleUpdate();
    }
    static string DescribeException(Exception ex)
    {
        var messages=new List<string>();for(Exception? current=ex;current!=null;current=current.InnerException)messages.Add(current.GetType().Name+": "+current.Message);
        return string.Join("\n原因：",messages.Distinct());
    }
    Task EnsureEngineAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string cli=Path.Combine(AppContext.BaseDirectory,"SolidCraft","solidcraft-cli.exe");
        if(!File.Exists(cli))throw new FileNotFoundException("找不到后台引擎，请完整解压新版软件包（包含 SolidCraft 文件夹）。");
        ControlClient.HeadlessExecutable=cli;Log("自动启动 SolidCraft 独立后台会话，全程无窗口。");
        return Task.CompletedTask;
    }

    async Task RunExport(bool exportDxf=false)
    {
        try
        {
            ResolveLocalTemplates();RefreshDefaultNames();
            var csvRows=GetRows();var rows = ModelRules.Validate(csvRows);
            string stamp=OutputNames.Stamp;
            string stepName=OutputNames.FileName(stepBase.Text,".step",stamp);
            string dxfName=OutputNames.FileName(dxfBase.Text,".dxf",stamp);
            string templateFile = nativeTemplatePath.Trim(), outputFolder = output.Text.Trim();
            if (!File.Exists(templateFile) || !string.Equals(Path.GetExtension(templateFile), ".solidcraft", StringComparison.OrdinalIgnoreCase)) throw new IOException("内置标准模型缺失，请完整解压安装包中的“模板”文件夹。");
            if (outputFolder.Length == 0) throw new IOException("请选择输出文件夹。");
            bool createPdf=(exportPdf.IsChecked==true),createDrawing=exportDxf||createPdf;
            var drawing = drawingOptions with{OutputFileName=dxfName,OverwriteOutput=(overwrite.IsChecked==true)};
            var options = new ExportOptions(rows, Path.GetFullPath(templateFile), Path.GetFullPath(outputFolder), ControlPort, CombinedGap, (combined.IsChecked==true), (overwrite.IsChecked==true),stamp,stepName,(taskCsv.IsChecked==true),csvRows, ExportStep: (!(exportDxf || exportPdf.IsChecked==true) || combined.IsChecked==true), Drawing: createDrawing ? drawing : null, ExportPdf: createPdf);
            if(createDrawing)
            {
                drawing.Validate();
                if(createPdf)
                {
                    if(string.IsNullOrWhiteSpace(drawing.TemplatePath))
                        throw new IOException("PDF 导出需要在“设置”中选择固定 A4 图框 DXF 模板。");
                    _=DrawingResources.Load(drawing);
                    PdfExportService.ValidateFontDirectory(drawing.FontDirectory);
                }
                if(!(overwrite.IsChecked==true)&&File.Exists(Path.Combine(options.Output,drawing.ResolvedFileName)))
                    throw new IOException("汇总 DXF 已存在，请更换输出目录或移走旧 DXF 后重新生成。");
                if(createPdf&&!(overwrite.IsChecked==true)&&File.Exists(Path.Combine(options.Output,Path.ChangeExtension(drawing.ResolvedFileName,".pdf"))))
                    throw new IOException("汇总 PDF 已存在，请更换输出目录或移走旧 PDF 后重新生成。");
            }
            SaveSettings(); cancellation = new(); SetRunning(true); progressBar.Maximum = rows.Count; progressBar.Value = 0;
            Progress<ExportProgress> progress = new(p => { SetStatus(p.Message); progressBar.Value = Math.Min(progressBar.Maximum, p.Completed); if (p.Log) Log(p.Message); });
            var token = cancellation.Token;
            await EnsureEngineAsync(token);
            await Task.Run(() => Exporter.RunAsync(options, progress, token));
            int drawingCount=createDrawing?rows.Count:0;
            SetStatus($"完成：{rows.Count} 个型号"+(createDrawing?$"，1 个 DXF"+(createPdf?$" 和 {drawingCount} 页 PDF":"") :"")); Log("输出目录：" + options.Output);
            MessageBox.Show(this, $"已导出 {rows.Count} 个型号。"+(createDrawing?$"\n已生成汇总 DXF"+(createPdf?$" 和 {drawingCount} 页 A4 PDF":"")+"。":"")+$"\n{options.Output}", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
            if(createDrawing)OpenCompletedDxf(Path.Combine(options.Output,drawing.ResolvedFileName));
        }
        catch (OperationCanceledException) { SetStatus("已停止，已导出的文件保留。"); Log("已按请求停止。"); }
        catch (Exception ex) { string details = DescribeException(ex); Log("导出中止：" + details); ShowError(details); SetStatus("导出中止"); }
        finally { cancellation?.Dispose(); cancellation = null; SetRunning(false); }
    }

    async Task RunStepDrawing()
    {
        var input = new OpenFileDialog() { Filter = "三维模型 (*.solidcraft;*.step;*.stp)|*.solidcraft;*.step;*.stp", Multiselect = true, Title = "选择要批量生成加工图的 SolidCraft 或 STEP 三维文件" };
        if (input.ShowDialog(this) != true) return;
        var outputFolder = new OpenFolderDialog() { Title = "选择加工图 DXF 的输出文件夹" };
        if (outputFolder.ShowDialog(this) != true) return;
        try
        {
            ResolveLocalTemplates();RefreshDefaultNames();
            string[] files = input.FileNames.OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray();
            var target = Path.GetFullPath(outputFolder.FolderName);
            cancellation = new(); SetRunning(true); progressBar.Maximum = files.Length; progressBar.Value = 0;
            Progress<ExportProgress> progress = new(p => { SetStatus(p.Message); progressBar.Value = Math.Min(progressBar.Maximum, p.Completed); if (p.Log) Log(p.Message); });
            var drawing = drawingOptions with{OutputFileName=OutputNames.FileName(dxfBase.Text,".dxf"),OverwriteOutput=(overwrite.IsChecked==true)};
            bool createPdf=(exportPdf.IsChecked==true);
            if(createPdf)
            {
                if(string.IsNullOrWhiteSpace(drawing.TemplatePath))
                    throw new IOException("PDF 导出需要在“设置”中选择固定 A4 图框 DXF 模板。");
                _=DrawingResources.Load(drawing);
                PdfExportService.ValidateFontDirectory(drawing.FontDirectory);
            }
            await EnsureEngineAsync(cancellation.Token);
            await Task.Run(() => StepDrawingExporter.RunAsync(files, target, ControlPort, progress, cancellation.Token, drawing,createPdf));
            SetStatus($"加工图完成：1 个 DXF"+(createPdf?$" 和 {files.Length} 页 A4 PDF":"")); Log("汇总 DXF 输出文件夹：" + target);
            MessageBox.Show(this, $"已生成 1 个汇总 DXF"+(createPdf?$" 和 {files.Length} 页 A4 PDF":"")+ $"，含 {files.Length} 张带尺寸标注的三视图，按输入文件名排序。尺寸单位为 mm，请在加工前核对。\n\n{Path.Combine(target, drawing.ResolvedFileName)}", "出图完成", MessageBoxButton.OK, MessageBoxImage.Information);
            OpenCompletedDxf(Path.Combine(target,drawing.ResolvedFileName));
        }
        catch (OperationCanceledException) { SetStatus("已停止；已完成的 DXF 保留。"); Log("三维模型批量出图已停止。"); }
        catch (Exception ex) { string details = DescribeException(ex); Log("三维模型出图中止：" + details); ShowError(details); SetStatus("三维模型出图中止"); }
        finally { cancellation?.Dispose(); cancellation = null; SetRunning(false); }
    }

    void OpenCompletedDxf(string file)
    {
        if(!(openDxf.IsChecked==true))return;
        try
        {
            if(!File.Exists(file))throw new FileNotFoundException("未找到生成的 DXF 文件。",file);
            Process.Start(new ProcessStartInfo(file){UseShellExecute=true});
        }
        catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log("DXF 已生成，自动打开失败："+ex.Message);
            MessageBox.Show(this,"DXF 已生成，但无法自动打开。请检查 DXF 默认打开软件，或手动打开：\n"+file,"打开 DXF",MessageBoxButton.OK,MessageBoxImage.Information);
        }
    }

    internal void SelectSmokeRow()
    {
        if(grid.Items.Count==0)return;
        grid.UnselectAll();grid.SelectedCells.Clear();
        grid.ScrollIntoView(grid.Items[0],grid.Columns[0]);grid.UpdateLayout();
        grid.CurrentCell=new DataGridCellInfo(grid.Items[0],grid.Columns[0]);
        grid.SelectedCells.Add(new DataGridCellInfo(grid.Items[0],grid.Columns[0]));
        if(grid.ItemContainerGenerator.ContainerFromIndex(0) is not DataGridRow row)return;
        row.ApplyTemplate();row.UpdateLayout();
        var cell=FindVisualChild<DataGridCell>(row,c=>ReferenceEquals(c.Column,grid.Columns[0]));
        if(cell!=null){cell.Focus();Keyboard.Focus(cell);}
        UpdateFillHandle();
    }
    static T? FindVisualChild<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child is T found)return found;
            var nested=FindVisualChild<T>(child);if(nested!=null)return nested;
        }
        return null;
    }
    internal void ClearSmokeSelection(){grid.UnselectAll();grid.SelectedCells.Clear();UpdateFillHandle();}
    internal void LoadSmokeRows()
    {
        string csv=Path.Combine(AppContext.BaseDirectory,"辊密封参数模板.csv");
        if(File.Exists(csv)){table.Rows.Clear();foreach(var row in CsvFiles.Read(csv))AddRow(row);}
        InitializeUndoRedo();
    }
    internal void VerifyUi()
    {
        if(grid.Columns.Count!=ModelRules.Columns.Length||table.Rows.Count<1)throw new InvalidOperationException("参数表初始化失败。");
        ModelRules.Validate(GetRows());
        if(log.ActualHeight<85)throw new InvalidOperationException("日志窗口默认高度过小。");
        if(!run.IsEnabled||!runDxf.IsEnabled||stop.IsEnabled)throw new InvalidOperationException("初始按钮状态不正确。");
        if(table.Rows.Count==6&&!GetRows().Select(r=>r[0]).SequenceEqual(new[]{"1","2","3","4","5","6"}))throw new InvalidOperationException("编号必须完整且唯一。");
    }
}
