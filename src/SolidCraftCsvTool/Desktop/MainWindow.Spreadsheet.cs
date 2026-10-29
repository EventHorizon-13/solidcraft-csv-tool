using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SolidCraftCsvTool;

public partial class MainWindow
{
    readonly record struct CellPosition(int Row,int Column);
    internal bool FillHandleVisible=>fillHandle.Visibility==Visibility.Visible;
    List<CellPosition> fillSourceCells=[];
    bool fillDragging;

    void InitializeSpreadsheetInteractions()
    {
        grid.AddHandler(Control.MouseDoubleClickEvent,new MouseButtonEventHandler(ColumnHeaderDoubleClick),true);
        grid.SelectedCellsChanged+=(_,_)=>ScheduleFillHandleUpdate();
        grid.CurrentCellChanged+=(_,_)=>ScheduleFillHandleUpdate();
        grid.Loaded+=(_,_)=>ScheduleFillHandleUpdate();
        grid.SizeChanged+=(_,_)=>ScheduleFillHandleUpdate();
        grid.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler((_,_)=>ScheduleFillHandleUpdate()));
        fillHandle.ToolTip="拖动填充柄：单个数字默认复制，按住 Ctrl 递增；日期默认递增，按住 Ctrl 复制；多格默认延续序列，按住 Ctrl 复制模式。";
    }

    void ScheduleFillHandleUpdate()
    {
        if(!IsLoaded||fillDragging)return;
        Dispatcher.BeginInvoke(new Action(UpdateFillHandle),DispatcherPriority.Loaded);
    }

    List<CellPosition> GetSelectedCellPositions()
    {
        return grid.SelectedCells
            .Select(cell=>new CellPosition(grid.Items.IndexOf(cell.Item),grid.Columns.IndexOf(cell.Column)))
            .Where(cell=>cell.Row>=0&&cell.Row<table.Rows.Count&&cell.Column>=0&&cell.Column<grid.Columns.Count)
            .Distinct().ToList();
    }

    static bool IsRectangle(IReadOnlyCollection<CellPosition> cells)
    {
        if(cells.Count==0)return false;
        int top=cells.Min(c=>c.Row),bottom=cells.Max(c=>c.Row);
        int left=cells.Min(c=>c.Column),right=cells.Max(c=>c.Column);
        return cells.Count==(bottom-top+1)*(right-left+1);
    }

    DataGridCell? GetVisibleCell(CellPosition position)
    {
        if(position.Row<0||position.Row>=grid.Items.Count||position.Column<0||position.Column>=grid.Columns.Count)return null;
        if(grid.ItemContainerGenerator.ContainerFromIndex(position.Row) is not DataGridRow row)return null;
        row.ApplyTemplate();row.UpdateLayout();
        return FindVisualChild<DataGridCell>(row,cell=>ReferenceEquals(cell.Column,grid.Columns[position.Column]));
    }

    static T? FindVisualChild<T>(DependencyObject parent,Func<T,bool> predicate) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child is T found&&predicate(found))return found;
            var nested=FindVisualChild(child,predicate);if(nested!=null)return nested;
        }
        return null;
    }

    static T? FindAncestor<T>(DependencyObject? current) where T:DependencyObject
    {
        while(current!=null)
        {
            if(current is T found)return found;
            DependencyObject? parent=null;
            try{parent=VisualTreeHelper.GetParent(current);}catch{}
            current=parent??LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    void UpdateFillHandle()
    {
        if(fillDragging)return;
        var selected=GetSelectedCellPositions();
        if(grid.IsReadOnly||selected.Count==0||!IsRectangle(selected))
        {
            fillHandle.Visibility=Visibility.Collapsed;return;
        }
        var corner=new CellPosition(selected.Max(c=>c.Row),selected.Max(c=>c.Column));
        var cell=GetVisibleCell(corner);
        if(cell==null||!cell.IsVisible||cell.ActualWidth<=0||cell.ActualHeight<=0)
        {
            fillHandle.Visibility=Visibility.Collapsed;return;
        }
        try
        {
            Point point=cell.TransformToAncestor(GridHost).Transform(new Point(cell.ActualWidth,cell.ActualHeight));
            Canvas.SetLeft(fillHandle,point.X-fillHandle.Width/2);
            Canvas.SetTop(fillHandle,point.Y-fillHandle.Height/2);
            fillHandle.Visibility=Visibility.Visible;
        }
        catch(InvalidOperationException){fillHandle.Visibility=Visibility.Collapsed;}
    }

    void FillDragStarted(object sender,DragStartedEventArgs e)
    {
        CommitRows();
        fillSourceCells=GetSelectedCellPositions();
        if(fillSourceCells.Count==0||!IsRectangle(fillSourceCells))
        {
            fillDragging=false;fillHandle.Visibility=Visibility.Collapsed;return;
        }
        fillDragging=true;
    }

    void FillDragDelta(object sender,DragDeltaEventArgs e)
    {
        if(!fillDragging)return;
        double left=Canvas.GetLeft(fillHandle),top=Canvas.GetTop(fillHandle);
        if(double.IsNaN(left))left=0;if(double.IsNaN(top))top=0;
        Canvas.SetLeft(fillHandle,Math.Clamp(left+e.HorizontalChange,0,Math.Max(0,fillOverlay.ActualWidth-fillHandle.Width)));
        Canvas.SetTop(fillHandle,Math.Clamp(top+e.VerticalChange,0,Math.Max(0,fillOverlay.ActualHeight-fillHandle.Height)));
    }

    void FillDragCompleted(object sender,DragCompletedEventArgs e)
    {
        if(!fillDragging)return;
        try
        {
            if(e.Canceled)return;
            Point pointer=Mouse.GetPosition(fillOverlay);
            Point point=fillOverlay.TranslatePoint(pointer,grid);
            var hit=VisualTreeHelper.HitTest(grid,point)?.VisualHit;
            var cell=FindAncestor<DataGridCell>(hit);
            if(cell==null)return;
            int row=grid.Items.IndexOf(cell.DataContext),column=grid.Columns.IndexOf(cell.Column);
            if(row<0||column<0)return;
            bool controlDown=Keyboard.IsKeyDown(Key.LeftCtrl)||Keyboard.IsKeyDown(Key.RightCtrl);
            int count=FillCells(fillSourceCells,row,column,controlDown);
            if(count>0)
            {
                SelectFillRange(fillSourceCells,row,column);
                string mode=controlDown?"Ctrl 模式":"默认模式";
                SetStatus($"已填充 {count} 个单元格（{mode}）。");
                Log($"表格填充完成：{count} 个单元格，{mode}。");
            }
        }
        catch(Exception ex){ShowError("填充失败："+ex.Message);}
        finally
        {
            fillDragging=false;fillSourceCells.Clear();ScheduleFillHandleUpdate();
        }
    }

    void SelectFillRange(IReadOnlyList<CellPosition> source,int targetRow,int targetColumn)
    {
        int top=Math.Min(source.Min(c=>c.Row),targetRow),bottom=Math.Max(source.Max(c=>c.Row),targetRow);
        int left=Math.Min(source.Min(c=>c.Column),targetColumn),right=Math.Max(source.Max(c=>c.Column),targetColumn);
        grid.SelectedCells.Clear();
        for(int row=top;row<=bottom;row++)
            for(int column=left;column<=right;column++)
                grid.SelectedCells.Add(new DataGridCellInfo(grid.Items[row],grid.Columns[column]));
        grid.CurrentCell=new DataGridCellInfo(grid.Items[targetRow],grid.Columns[targetColumn]);
    }

    int FillCells(IReadOnlyList<CellPosition> source,int targetRow,int targetColumn,bool controlDown)
    {
        if(source.Count==0||!IsRectangle(source))throw new InvalidOperationException("请先选择一个连续的矩形单元格区域。");
        if(targetRow<0||targetRow>=table.Rows.Count||targetColumn<0||targetColumn>=ModelRules.Columns.Length)return 0;
        int top=source.Min(c=>c.Row),bottom=source.Max(c=>c.Row);
        int left=source.Min(c=>c.Column),right=source.Max(c=>c.Column);
        bool extendVertical=targetRow<top||targetRow>bottom;
        bool extendHorizontal=targetColumn<left||targetColumn>right;
        if(!extendVertical&&!extendHorizontal)return 0;

        int fillTop=Math.Min(top,targetRow),fillBottom=Math.Max(bottom,targetRow);
        int fillLeft=Math.Min(left,targetColumn),fillRight=Math.Max(right,targetColumn);
        var sourceValues=new Dictionary<CellPosition,object>();
        for(int row=top;row<=bottom;row++)
            for(int column=left;column<=right;column++)
                sourceValues[new(row,column)]=table.Rows[row][ModelRules.Columns[column]];

        var original=new Dictionary<CellPosition,object>();
        for(int row=fillTop;row<=fillBottom;row++)
            for(int column=fillLeft;column<=fillRight;column++)
                if(row<top||row>bottom||column<left||column>right)
                    original[new(row,column)]=table.Rows[row][ModelRules.Columns[column]];

        try
        {
            // First extend the selected columns vertically. This also supports
            // diagonal fills: the newly filled rows become the source for step two.
            if(extendVertical)
            {
                for(int row=fillTop;row<=fillBottom;row++)
                {
                    if(row>=top&&row<=bottom)continue;
                    for(int column=left;column<=right;column++)
                    {
                        var values=Enumerable.Range(top,bottom-top+1)
                            .Select(r=>sourceValues[new(r,column)]).ToArray();
                        table.Rows[row][ModelRules.Columns[column]]=FillSequence(values,row-top,controlDown);
                    }
                }
            }
            if(extendHorizontal)
            {
                for(int row=fillTop;row<=fillBottom;row++)
                {
                    for(int column=fillLeft;column<=fillRight;column++)
                    {
                        if(column>=left&&column<=right)continue;
                        var values=Enumerable.Range(left,right-left+1)
                            .Select(c=>table.Rows[row][ModelRules.Columns[c]]).ToArray();
                        table.Rows[row][ModelRules.Columns[column]]=FillSequence(values,column-left,controlDown);
                    }
                }
            }
            ModelRules.Validate(GetRows());
            return original.Count;
        }
        catch
        {
            foreach(var pair in original)table.Rows[pair.Key.Row][ModelRules.Columns[pair.Key.Column]]=pair.Value;
            throw;
        }
    }

    static object FillSequence(IReadOnlyList<object> source,int offset,bool controlDown)
    {
        if(source.Count==0)return "";
        string[] text=source.Select(value=>Convert.ToString(value,CultureInfo.InvariantCulture)??"").ToArray();
        int repeat=((offset%source.Count)+source.Count)%source.Count;

        if(text.All(value=>TryFillNumber(value,out _)))
        {
            bool series=source.Count==1?controlDown:!controlDown;
            if(!series)return source[repeat];
            decimal[] numbers=text.Select(value=>decimal.Parse(value,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture)).ToArray();
            decimal step=numbers.Length>1?numbers[^1]-numbers[^2]:1;
            decimal result=offset<0?numbers[0]+step*offset:offset>=numbers.Length?numbers[^1]+step*(offset-numbers.Length+1):numbers[offset];
            return FormatFillNumber(result,text[^1]);
        }

        if(text.All(value=>TryFillDate(value,out _,out _)))
        {
            if(controlDown)return source[repeat];
            var dates=text.Select(value=>{TryFillDate(value,out var date,out _);return date;}).ToArray();
            TryFillDate(text[0],out _,out string format);
            double step=dates.Length>1?(dates[^1]-dates[^2]).TotalDays:1;
            DateTime result=offset<0?dates[0].AddDays(step*offset):offset>=dates.Length?dates[^1].AddDays(step*(offset-dates.Length+1)):dates[offset];
            return result.ToString(format,CultureInfo.InvariantCulture);
        }

        if(TryFillSuffix(text,out string prefix,out int[] suffixes,out int width))
        {
            bool series=source.Count==1?controlDown:!controlDown;
            if(!series)return source[repeat];
            int step=suffixes.Length>1?suffixes[^1]-suffixes[^2]:1;
            int result=offset<0?suffixes[0]+step*offset:offset>=suffixes.Length?suffixes[^1]+step*(offset-suffixes.Length+1):suffixes[offset];
            return prefix+result.ToString("D"+width,CultureInfo.InvariantCulture);
        }

        return source[repeat];
    }

    static bool TryFillNumber(string value,out decimal number)=>
        decimal.TryParse(value,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out number);

    static string FormatFillNumber(decimal value,string template)
    {
        int dot=template.LastIndexOf('.');
        int decimals=dot<0?0:template.Length-dot-1;
        string format=decimals==0?"0":"0."+new string('0',decimals);
        return value.ToString(format,CultureInfo.InvariantCulture);
    }

    static readonly string[] FillDateFormats=["yyyy-MM-dd","yyyy/M/d","yyyy/MM/dd","yyyy.M.d","yyyy.MM.dd","M/d/yyyy","MM/dd/yyyy","yyyyMMdd","yyyy-MM-dd HH:mm","yyyy/MM/dd HH:mm"];
    static bool TryFillDate(string value,out DateTime date,out string format)
    {
        foreach(string candidate in FillDateFormats)
            if(DateTime.TryParseExact(value,candidate,CultureInfo.InvariantCulture,DateTimeStyles.None,out date)){format=candidate;return true;}
        date=default;format="";
        return false;
    }

    static bool TryFillSuffix(string[] values,out string prefix,out int[] numbers,out int width)
    {
        prefix="";numbers=[];width=0;
        var matches=values.Select(value=>Regex.Match(value,@"^(.*?)(\d+)$")).ToArray();
        if(matches.Any(match=>!match.Success))return false;
        prefix=matches[0].Groups[1].Value;
        if(matches.Any(match=>match.Groups[1].Value!=prefix))return false;
        numbers=matches.Select(match=>int.TryParse(match.Groups[2].Value,NumberStyles.None,CultureInfo.InvariantCulture,out int value)?value:int.MinValue).ToArray();
        if(numbers.Any(value=>value==int.MinValue))return false;
        width=matches[^1].Groups[2].Value.Length;
        return true;
    }

    void ColumnHeaderDoubleClick(object sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Left)return;
        var header=FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject??e.Source as DependencyObject);
        if(header?.Column==null)return;
        int index=grid.Columns.IndexOf(header.Column);
        if(index<0||index>=ModelRules.Columns.Length)return;
        e.Handled=true;
        try
        {
            CommitRows();
            if(ModelRules.Flags.Contains(ModelRules.Columns[index]))ToggleFlagColumn(index);
            else
            {
                string name=ModelRules.Columns[index];
                bool numbering=index==0;
                string initial=numbering?"零件":Convert.ToString(table.Rows.Count>0?table.Rows[0][name]:"",CultureInfo.InvariantCulture)??"";
                string? value=PromptWholeColumnValue(name,initial,numbering);
                if(value==null)return;
                SetWholeColumnValue(index,value);
                SetStatus(numbering?$"已重新生成编号列：{table.Rows.Count} 行。":$"已批量修改“{name}”列。");
                Log(numbering?"已批量重新编号。":$"已批量修改列：{name}。");
            }
        }
        catch(Exception ex){ShowError("整列操作失败："+ex.Message);}
        finally{ScheduleFillHandleUpdate();}
    }

    void ToggleFlagColumn(int index)
    {
        string name=ModelRules.Columns[index];
        bool allChecked=table.Rows.Count>0&&table.Rows.Cast<DataRow>().All(row=>row[name] is true);
        foreach(DataRow row in table.Rows)row[name]=!allChecked;
        string header=Convert.ToString(grid.Columns[index].Header)??name;
        SetStatus(allChecked?$"已取消全部勾选：{header}。":$"已全部勾选：{header}。");
    }

    string? PromptWholeColumnValue(string name,string initial,bool numbering)
    {
        var dialog=new Window
        {
            Title=numbering?"批量修改编号":$"批量修改“{name}”",
            Width=460,SizeToContent=SizeToContent.Height,MinWidth=460,MaxWidth=460,
            ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Owner=this,ShowInTaskbar=false,
            FontFamily=new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"),FontSize=13
        };
        dialog.SetResourceReference(BackgroundProperty,"WindowBackground");
        var root=new StackPanel{Margin=new Thickness(20)};
        root.Children.Add(new TextBlock
        {
            Text=numbering
                ?$"请输入编号前缀，系统将生成“前缀1、前缀2……”（共 {table.Rows.Count} 行）。"
                :$"将把“{name}”列的所有数据统一改为以下内容：",
            TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)
        });
        var input=new TextBox{Text=initial,MinHeight=32,VerticalContentAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,0,16)};
        root.Children.Add(input);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Content="取消",MinWidth=78,Padding=new Thickness(14,6,14,6),IsCancel=true,Margin=new Thickness(0,0,8,0)};
        var apply=new Button{Content="确定",MinWidth=78,Padding=new Thickness(14,6,14,6),IsDefault=true};
        cancel.Click+=(_,_)=>dialog.DialogResult=false;
        apply.Click+=(_,_)=>dialog.DialogResult=true;
        buttons.Children.Add(cancel);buttons.Children.Add(apply);root.Children.Add(buttons);
        dialog.Content=root;
        dialog.Loaded+=(_,_)=>{input.Focus();input.SelectAll();};
        return dialog.ShowDialog()==true?input.Text:null;
    }

    void SetWholeColumnValue(int index,string input)
    {
        if(index<0||index>=ModelRules.Columns.Length)throw new ArgumentOutOfRangeException(nameof(index));
        string name=ModelRules.Columns[index];
        object[] original=table.Rows.Cast<DataRow>().Select(row=>row[name]).ToArray();
        try
        {
            if(index==0)
            {
                string prefix=input.Trim();
                if(prefix.Length==0)throw new InvalidDataException("编号前缀不能为空。");
                for(int row=0;row<table.Rows.Count;row++)table.Rows[row][name]=prefix+(row+1).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                string value=input.Trim();
                foreach(DataRow row in table.Rows)row[name]=value;
            }
            ModelRules.Validate(GetRows());
        }
        catch
        {
            for(int row=0;row<original.Length;row++)table.Rows[row][name]=original[row];
            throw;
        }
    }

    DataRow[] GetSelectedRows()
    {
        var selected=new HashSet<DataRow>();
        foreach(object item in grid.SelectedItems)if(item is DataRowView view)selected.Add(view.Row);
        foreach(var cell in grid.SelectedCells)if(cell.Item is DataRowView view)selected.Add(view.Row);
        return selected.OrderBy(row=>table.Rows.IndexOf(row)).ToArray();
    }

    internal void VerifySpreadsheetInteractions()
    {
        if(table.Rows.Count<4)throw new InvalidOperationException("表格填充测试至少需要四行。");
        object[][] original=table.Rows.Cast<DataRow>().Select(row=>ModelRules.Columns.Select(name=>row[name]).ToArray()).ToArray();
        try
        {
            table.Rows[0]["孔距"]="100";table.Rows[1]["孔距"]="200";
            var pair=new[]{new CellPosition(0,3),new CellPosition(1,3)};
            FillCells(pair,3,3,false);
            if(Convert.ToString(table.Rows[2]["孔距"])!="300"||Convert.ToString(table.Rows[3]["孔距"])!="400")
                throw new InvalidOperationException("数字序列填充失败。");

            table.Rows[0]["孔距"]="100";table.Rows[1]["孔距"]="200";
            FillCells(pair,3,3,true);
            if(Convert.ToString(table.Rows[2]["孔距"])!="100"||Convert.ToString(table.Rows[3]["孔距"])!="200")
                throw new InvalidOperationException("Ctrl 复制模式失败。");

            table.Rows[0]["孔距"]="100";
            FillCells([new CellPosition(0,3)],2,3,true);
            if(Convert.ToString(table.Rows[1]["孔距"])!="101"||Convert.ToString(table.Rows[2]["孔距"])!="102")
                throw new InvalidOperationException("Ctrl 数字递增失败。");

            foreach(DataRow row in table.Rows)row["子头"]=false;
            ToggleFlagColumn(6);
            if(table.Rows.Cast<DataRow>().Any(row=>row["子头"] is not true))throw new InvalidOperationException("列头全选失败。");
            ToggleFlagColumn(6);
            if(table.Rows.Cast<DataRow>().Any(row=>row["子头"] is not false))throw new InvalidOperationException("列头取消全选失败。");

            SetWholeColumnValue(3,"150");
            if(table.Rows.Cast<DataRow>().Any(row=>Convert.ToString(row["孔距"])!="150"))throw new InvalidOperationException("整列批量修改失败。");
        }
        finally
        {
            for(int row=0;row<original.Length;row++)
                for(int column=0;column<ModelRules.Columns.Length;column++)
                    table.Rows[row][ModelRules.Columns[column]]=original[row][column];
            grid.Items.Refresh();ScheduleFillHandleUpdate();
        }
    }
}
