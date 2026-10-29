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
    object[][] lastHistorySnapshot=[];
    object[][]? editSnapshot;
    readonly List<object[][]> undoHistory=[];
    readonly List<object[][]> redoHistory=[];
    bool historySuspended;
    object[][]? fillBeforeSnapshot;
    CellPosition? fillPreviewTarget;
    bool fillPreviewControlDown;
    int fillPreviewCount;
    bool fillPreviewApplied;

    void InitializeSpreadsheetInteractions()
    {
        grid.AddHandler(Control.MouseDoubleClickEvent,new MouseButtonEventHandler(ColumnHeaderDoubleClick),true);
        grid.BeginningEdit+=(_,_)=>BeginHistoryEdit();
        grid.CellEditEnding+=(_,_)=>Dispatcher.BeginInvoke(new Action(FinishHistoryEdit),DispatcherPriority.Background);
        table.ColumnChanged+=(_,_)=>TrackUnscopedTableChange();
        PreviewKeyDown+=SpreadsheetPreviewKeyDown;
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

    void InitializeUndoRedo()
    {
        lastHistorySnapshot=CaptureTableSnapshot();
        undoHistory.Clear();redoHistory.Clear();editSnapshot=null;
    }

    void BeginHistoryEdit()
    {
        if(historySuspended)return;
        if(editSnapshot!=null)FinishHistoryEdit();
        editSnapshot=CaptureTableSnapshot();
        lastHistorySnapshot=CloneSnapshot(editSnapshot);
    }

    void FinishHistoryEdit()
    {
        if(editSnapshot==null)return;
        var before=editSnapshot;editSnapshot=null;
        CommitHistoryChange(before);
    }

    void TrackUnscopedTableChange()
    {
        if(historySuspended||editSnapshot!=null)return;
        var current=CaptureTableSnapshot();
        if(SnapshotsEqual(lastHistorySnapshot,current))return;
        AddUndoSnapshot(lastHistorySnapshot);
        redoHistory.Clear();
        lastHistorySnapshot=current;
    }

    void AddUndoSnapshot(object[][] snapshot)
    {
        undoHistory.Add(CloneSnapshot(snapshot));
        if(undoHistory.Count>100)undoHistory.RemoveAt(0);
    }

    void CommitHistoryChange(object[][] before)
    {
        var after=CaptureTableSnapshot();
        if(!SnapshotsEqual(before,after))
        {
            AddUndoSnapshot(before);
            redoHistory.Clear();
        }
        lastHistorySnapshot=after;
    }

    void RunUndoable(Action action)
    {
        CommitRows();FinishHistoryEdit();
        var before=CaptureTableSnapshot();
        bool previous=historySuspended;historySuspended=true;
        try{action();}
        catch
        {
            RestoreTableSnapshot(before);
            throw;
        }
        finally{historySuspended=previous;}
        CommitHistoryChange(before);
    }

    object[][] CaptureTableSnapshot()=>table.Rows.Cast<DataRow>()
        .Select(row=>ModelRules.Columns.Select(name=>row[name]).ToArray()).ToArray();

    static object[][] CloneSnapshot(object[][] snapshot)=>snapshot
        .Select(row=>(object[])row.Clone()).ToArray();

    static bool SnapshotsEqual(object[][] left,object[][] right)
    {
        if(left.Length!=right.Length)return false;
        for(int row=0;row<left.Length;row++)
        {
            if(left[row].Length!=right[row].Length)return false;
            for(int column=0;column<left[row].Length;column++)
                if(!Equals(left[row][column],right[row][column]))return false;
        }
        return true;
    }

    void RestoreTableSnapshot(object[][] snapshot)
    {
        bool previous=historySuspended;historySuspended=true;
        try
        {
            if(table.Rows.Count!=snapshot.Length)
            {
                grid.SelectedCells.Clear();grid.UnselectAll();
                table.Rows.Clear();
                foreach(var row in snapshot)table.Rows.Add((object[])row.Clone());
                if(table.Rows.Count>0&&grid.Columns.Count>0)
                    grid.CurrentCell=new DataGridCellInfo(grid.Items[0],grid.Columns[0]);
            }
            else
            {
                for(int row=0;row<snapshot.Length;row++)
                    for(int column=0;column<ModelRules.Columns.Length;column++)
                        table.Rows[row][ModelRules.Columns[column]]=snapshot[row][column];
            }
            grid.Items.Refresh();
        }
        finally{historySuspended=previous;}
        lastHistorySnapshot=CaptureTableSnapshot();
        ScheduleFillHandleUpdate();
    }

    void RestoreSnapshotValues(object[][] snapshot)
    {
        bool previous=historySuspended;historySuspended=true;
        try
        {
            if(table.Rows.Count!=snapshot.Length){RestoreTableSnapshot(snapshot);return;}
            for(int row=0;row<snapshot.Length;row++)
                for(int column=0;column<ModelRules.Columns.Length;column++)
                    table.Rows[row][ModelRules.Columns[column]]=snapshot[row][column];
        }
        finally{historySuspended=previous;}
    }

    void UndoSpreadsheet()
    {
        CommitRows();FinishHistoryEdit();
        if(undoHistory.Count==0){SetStatus("没有可撤销的表格操作。");return;}
        var current=CaptureTableSnapshot();
        var target=undoHistory[^1];undoHistory.RemoveAt(undoHistory.Count-1);
        redoHistory.Add(current);
        RestoreTableSnapshot(target);
        SetStatus("已撤销表格操作。");Log("已撤销表格操作。");
    }

    void RedoSpreadsheet()
    {
        CommitRows();FinishHistoryEdit();
        if(redoHistory.Count==0){SetStatus("没有可重做的表格操作。");return;}
        var current=CaptureTableSnapshot();
        var target=redoHistory[^1];redoHistory.RemoveAt(redoHistory.Count-1);
        AddUndoSnapshot(current);
        RestoreTableSnapshot(target);
        SetStatus("已重做表格操作。");Log("已重做表格操作。");
    }

    void SpreadsheetPreviewKeyDown(object sender,KeyEventArgs e)
    {
        if(!grid.IsKeyboardFocusWithin||Keyboard.FocusedElement is TextBoxBase)return;
        if((Keyboard.Modifiers&ModifierKeys.Control)==0)return;
        if(e.Key==Key.Z&&(Keyboard.Modifiers&ModifierKeys.Shift)!=0)
        {
            RedoSpreadsheet();e.Handled=true;
        }
        else if(e.Key==Key.Z)
        {
            UndoSpreadsheet();e.Handled=true;
        }
        else if(e.Key==Key.Y)
        {
            RedoSpreadsheet();e.Handled=true;
        }
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
        CommitRows();FinishHistoryEdit();
        fillSourceCells=GetSelectedCellPositions();
        if(fillSourceCells.Count==0||!IsRectangle(fillSourceCells))
        {
            fillDragging=false;fillHandle.Visibility=Visibility.Collapsed;
            SetStatus("请先选择一个单元格或连续的矩形区域。");return;
        }
        fillBeforeSnapshot=CaptureTableSnapshot();
        fillDragging=true;fillPreviewApplied=false;fillPreviewTarget=null;fillPreviewCount=0;
        fillPreviewText.Text="拖动到目标单元格以预览填充";
        fillPreviewBadge.Visibility=Visibility.Visible;
        PositionFillPreviewBadge(Mouse.GetPosition(fillOverlay));
    }

    void FillDragDelta(object sender,DragDeltaEventArgs e)
    {
        if(fillDragging)UpdateFillPreviewFromPointer();
    }

    void FillDragCompleted(object sender,DragCompletedEventArgs e)
    {
        if(!fillDragging)return;
        try
        {
            if(e.Canceled)
            {
                if(fillBeforeSnapshot!=null)RestoreSnapshotValues(fillBeforeSnapshot);
                return;
            }
            UpdateFillPreviewFromPointer();
            if(fillBeforeSnapshot!=null&&fillPreviewTarget is CellPosition target&&fillPreviewCount>0)
            {
                CommitHistoryChange(fillBeforeSnapshot);
                SelectFillRange(fillSourceCells,target.Row,target.Column);
                string mode=fillPreviewControlDown?"Ctrl 模式":"默认模式";
                SetStatus($"已填充 {fillPreviewCount} 个单元格（{mode}）。");
                Log($"表格填充完成：{fillPreviewCount} 个单元格，{mode}。");
            }
            else if(fillBeforeSnapshot!=null)RestoreSnapshotValues(fillBeforeSnapshot);
        }
        catch(Exception ex)
        {
            if(fillBeforeSnapshot!=null)RestoreSnapshotValues(fillBeforeSnapshot);
            ShowError("填充失败："+ex.Message);
        }
        finally
        {
            fillDragging=false;fillSourceCells.Clear();fillBeforeSnapshot=null;
            fillPreviewTarget=null;fillPreviewApplied=false;fillPreviewCount=0;
            HideFillPreview();ScheduleFillHandleUpdate();
        }
    }

    CellPosition? HitTestFillTarget(Point pointer)
    {
        Point point=fillOverlay.TranslatePoint(pointer,grid);
        var hit=VisualTreeHelper.HitTest(grid,point)?.VisualHit;
        var cell=FindAncestor<DataGridCell>(hit);
        if(cell==null)return null;
        int row=grid.Items.IndexOf(cell.DataContext),column=grid.Columns.IndexOf(cell.Column);
        if(row<0||row>=table.Rows.Count||column<0||column>=ModelRules.Columns.Length)return null;
        return new CellPosition(row,column);
    }

    void UpdateFillPreviewFromPointer()
    {
        if(!fillDragging||fillBeforeSnapshot==null)return;
        Point pointer=Mouse.GetPosition(fillOverlay);
        CellPosition? target=HitTestFillTarget(pointer);
        bool controlDown=Keyboard.IsKeyDown(Key.LeftCtrl)||Keyboard.IsKeyDown(Key.RightCtrl);
        if(target==fillPreviewTarget&&controlDown==fillPreviewControlDown&&fillPreviewApplied)
        {
            PositionFillPreviewBadge(pointer);return;
        }

        if(fillPreviewApplied)RestoreSnapshotValues(fillBeforeSnapshot);
        fillPreviewApplied=false;fillPreviewTarget=null;fillPreviewCount=0;
        fillPreviewBorder.Visibility=Visibility.Collapsed;

        if(target==null)
        {
            fillPreviewText.Text="拖动到目标单元格以预览填充";
            fillPreviewBadge.Visibility=Visibility.Visible;PositionFillPreviewBadge(pointer);
            SetStatus("填充预览：将鼠标拖到目标单元格；按住 Ctrl 可切换复制与递增模式。");
            return;
        }

        try
        {
            int count;
            bool previous=historySuspended;historySuspended=true;
            try{count=FillCells(fillSourceCells,target.Value.Row,target.Value.Column,controlDown);}
            finally{historySuspended=previous;}
            if(count<=0)
            {
                fillPreviewText.Text="目标位于源区域内，请拖到所选区域之外";
                fillPreviewBadge.Visibility=Visibility.Visible;PositionFillPreviewBadge(pointer);
                SetStatus("填充预览：请将目标拖到源区域之外。");
                return;
            }

            fillPreviewTarget=target;fillPreviewControlDown=controlDown;fillPreviewCount=count;fillPreviewApplied=true;
            UpdateFillPreviewBounds(target.Value);
            string column=Convert.ToString(grid.Columns[target.Value.Column].Header,CultureInfo.InvariantCulture)??ModelRules.Columns[target.Value.Column];
            string value=Convert.ToString(table.Rows[target.Value.Row][ModelRules.Columns[target.Value.Column]],CultureInfo.InvariantCulture)??"";
            string mode=controlDown?"Ctrl：递增/复制切换":"默认填充";
            fillPreviewText.Text=$"预览：第 {target.Value.Row+1} 行「{column}」={value} · {count} 格 · {mode} · 松开完成";
            fillPreviewBadge.Visibility=Visibility.Visible;PositionFillPreviewBadge(pointer);
            SetStatus($"填充预览中：{count} 个单元格；{mode}；松开鼠标提交。");
        }
        catch(Exception ex)
        {
            RestoreSnapshotValues(fillBeforeSnapshot);
            fillPreviewApplied=false;fillPreviewTarget=null;fillPreviewCount=0;
            fillPreviewBorder.Visibility=Visibility.Collapsed;
            fillPreviewText.Text="无法填充："+ex.Message;
            fillPreviewBadge.Visibility=Visibility.Visible;PositionFillPreviewBadge(pointer);
            SetStatus("填充预览失败："+ex.Message);
        }
    }

    void UpdateFillPreviewBounds(CellPosition target)
    {
        int top=Math.Min(fillSourceCells.Min(c=>c.Row),target.Row);
        int bottom=Math.Max(fillSourceCells.Max(c=>c.Row),target.Row);
        int left=Math.Min(fillSourceCells.Min(c=>c.Column),target.Column);
        int right=Math.Max(fillSourceCells.Max(c=>c.Column),target.Column);
        var first=GetVisibleCell(new CellPosition(top,left));
        var last=GetVisibleCell(new CellPosition(bottom,right));
        if(first==null||last==null||!first.IsVisible||!last.IsVisible)
        {
            fillPreviewBorder.Visibility=Visibility.Collapsed;return;
        }
        try
        {
            Point a=first.TransformToAncestor(GridHost).Transform(new Point(0,0));
            Point b=last.TransformToAncestor(GridHost).Transform(new Point(last.ActualWidth,last.ActualHeight));
            Canvas.SetLeft(fillPreviewBorder,a.X);Canvas.SetTop(fillPreviewBorder,a.Y);
            fillPreviewBorder.Width=Math.Max(1,b.X-a.X);fillPreviewBorder.Height=Math.Max(1,b.Y-a.Y);
            fillPreviewBorder.Visibility=Visibility.Visible;
        }
        catch(InvalidOperationException){fillPreviewBorder.Visibility=Visibility.Collapsed;}
    }

    void PositionFillPreviewBadge(Point pointer)
    {
        fillPreviewBadge.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
        Size size=fillPreviewBadge.DesiredSize;
        double x=pointer.X+14,y=pointer.Y+14;
        if(x+size.Width>fillOverlay.ActualWidth)x=Math.Max(0,pointer.X-size.Width-14);
        if(y+size.Height>fillOverlay.ActualHeight)y=Math.Max(0,pointer.Y-size.Height-14);
        Canvas.SetLeft(fillPreviewBadge,Math.Clamp(x,0,Math.Max(0,fillOverlay.ActualWidth-size.Width)));
        Canvas.SetTop(fillPreviewBadge,Math.Clamp(y,0,Math.Max(0,fillOverlay.ActualHeight-size.Height)));
    }

    void HideFillPreview()
    {
        fillPreviewBorder.Visibility=Visibility.Collapsed;
        fillPreviewBadge.Visibility=Visibility.Collapsed;
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
        string commonPrefix=matches[0].Groups[1].Value;
        if(matches.Any(match=>match.Groups[1].Value!=commonPrefix))return false;
        prefix=commonPrefix;
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
        RunUndoable(()=>ToggleFlagColumnCore(index));
    }

    void ToggleFlagColumnCore(int index)
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

    void SetWholeColumnValue(int index,string input)=>RunUndoable(()=>SetWholeColumnValueCore(index,input));

    void SetWholeColumnValueCore(int index,string input)
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
        object[][] original=CaptureTableSnapshot();
        bool previousHistorySuspended=historySuspended;historySuspended=true;
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
            if(Convert.ToString(FillSequence(new object[]{"2026-10-28"},1,false))!="2026-10-29")
                throw new InvalidOperationException("日期序列填充失败。");
            if(Convert.ToString(FillSequence(new object[]{"Part001","Part002"},2,false))!="Part003")
                throw new InvalidOperationException("编号后缀序列填充失败。");

            foreach(DataRow row in table.Rows)row["子头"]=false;
            ToggleFlagColumn(6);
            if(table.Rows.Cast<DataRow>().Any(row=>row["子头"] is not true))throw new InvalidOperationException("列头全选失败。");
            ToggleFlagColumn(6);
            if(table.Rows.Cast<DataRow>().Any(row=>row["子头"] is not false))throw new InvalidOperationException("列头取消全选失败。");

            var beforeBulk=CaptureTableSnapshot();
            SetWholeColumnValue(3,"150");
            if(table.Rows.Cast<DataRow>().Any(row=>Convert.ToString(row["孔距"])!="150"))throw new InvalidOperationException("整列批量修改失败。");
            UndoSpreadsheet();
            if(!SnapshotsEqual(CaptureTableSnapshot(),beforeBulk))throw new InvalidOperationException("Ctrl+Z 撤销失败。");
            RedoSpreadsheet();
            if(table.Rows.Cast<DataRow>().Any(row=>Convert.ToString(row["孔距"])!="150"))throw new InvalidOperationException("Ctrl+Y 重做失败。");
            UndoSpreadsheet();
        }
        finally
        {
            RestoreTableSnapshot(original);
            historySuspended=previousHistorySuspended;
            InitializeUndoRedo();
            grid.Items.Refresh();ScheduleFillHandleUpdate();
        }
    }
}
