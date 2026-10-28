using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SolidCraftCsvTool;

internal sealed class SettingsWindow : Window
{
    readonly DrawingOptions initial;
    readonly AppearanceMode initialAppearance;
    readonly TextBox template=Field(),fonts=Field(),hidden=Field(),viewGap=Field(),margin=Field(),sheetGap=Field();
    readonly ComboBox styles=new(){MinHeight=32,VerticalContentAlignment=VerticalAlignment.Center,Margin=new Thickness(0,4,0,4)};
    readonly ComboBox appearance=new(){MinWidth=190,MinHeight=32,VerticalContentAlignment=VerticalAlignment.Center};
    readonly CheckBox frame=new(){Content="添加图框与标题栏",Margin=new Thickness(0,8,0,8)};
    readonly Grid titles=new();
    readonly Dictionary<string,TextBox> titleEditors=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,string> titleValues;
    internal readonly TabControl Tabs=new();
    public DrawingOptions Value {get;private set;}
    public AppearanceMode Appearance=>(AppearanceMode)appearance.SelectedIndex;
    internal SettingsWindow(DrawingOptions initial,AppearanceMode mode)
    {
        this.initial=initial;Value=initial;initialAppearance=mode;titleValues=new(initial.TitleBlockAttributes??[],StringComparer.OrdinalIgnoreCase);
        Title="设置";Width=760;Height=540;MinWidth=700;MinHeight=460;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        FontFamily=new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI");FontSize=13;
        var root=new Grid{Margin=new Thickness(16)};root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});Content=root;
        root.Children.Add(Tabs);
        appearance.ItemsSource=new[]{"自动（跟随系统）","深色","浅色"};appearance.SelectedIndex=(int)mode;
        var general=Rows();Add(general,"深色模式",appearance);
        Add(general,"",new TextBlock{Text="外观由微软 Fluent 主题统一管理。",Opacity=.7,Margin=new Thickness(0,8,0,0)});
        Page("常规",general);
        template.IsReadOnly=true;fonts.IsReadOnly=true;template.Text=initial.TemplatePath;fonts.Text=initial.FontDirectory;
        frame.IsChecked=initial.IncludeFrame;
        var resources=Rows();Add(resources,"DXF 图框模板",Picker(template,ChooseTemplate,()=>{RememberTitles();template.Text="";LoadTemplate("_5");}));
        Add(resources,"SHX 字体目录",Picker(fonts,ChooseFonts));Add(resources,"图框",frame);Page("图框与字体",resources);
        hidden.Text=initial.HiddenLineScale.ToString(CultureInfo.InvariantCulture);viewGap.Text=initial.ViewGap.ToString(CultureInfo.InvariantCulture);
        margin.Text=initial.FrameMargin.ToString(CultureInfo.InvariantCulture);sheetGap.Text=initial.SheetGap.ToString(CultureInfo.InvariantCulture);
        var dimensions=Rows();Add(dimensions,"标注样式",styles);Add(dimensions,"隐藏线比例",hidden);Add(dimensions,"视图净间距 / mm",viewGap);
        Add(dimensions,"图框留白 / mm",margin);Add(dimensions,"图纸间距 / mm",sheetGap);Page("标注与排布",dimensions);
        Page("标题栏属性",titles);titles.IsEnabled=frame.IsChecked==true;
        frame.Checked+=(_,_)=>titles.IsEnabled=true;frame.Unchecked+=(_,_)=>titles.IsEnabled=false;
        LoadTemplate(initial.DimensionStyleName);
        appearance.SelectionChanged+=(_,_)=>DesktopApp.SetAppearance(Appearance);
        Closing+=(_,_)=>{if(DialogResult!=true)DesktopApp.SetAppearance(initialAppearance);};
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
        var cancel=new Button{Content="取消",MinWidth=80,Padding=new Thickness(12,5,12,5),IsCancel=true,Margin=new Thickness(0,0,8,0)};
        var save=new Button{Content="保存",MinWidth=80,Padding=new Thickness(12,5,12,5),IsDefault=true};save.Click+=Save;
        buttons.Children.Add(cancel);buttons.Children.Add(save);Grid.SetRow(buttons,1);root.Children.Add(buttons);
    }
    static TextBox Field()=>new(){MinHeight=32,VerticalContentAlignment=VerticalAlignment.Center,Margin=new Thickness(0,4,0,4)};
    static Grid Rows()
    {
        var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new GridLength(150)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});return grid;
    }
    static void Add(Grid grid,string label,UIElement control)
    {
        int row=grid.RowDefinitions.Count;grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var text=new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,6,12,6)};
        Grid.SetRow(text,row);grid.Children.Add(text);Grid.SetRow(control,row);Grid.SetColumn(control,1);grid.Children.Add(control);
    }
    void Page(string title,UIElement body)=>Tabs.Items.Add(new TabItem{Header=title,Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(16)}});
    static DockPanel Picker(TextBox field,Action choose,Action? clear=null)
    {
        var panel=new DockPanel{LastChildFill=true};
        if(clear!=null)
        {
            var button=new Button{Content="清除",MinWidth=66,Margin=new Thickness(6,4,0,4)};button.Click+=(_,_)=>clear();DockPanel.SetDock(button,Dock.Right);panel.Children.Add(button);
        }
        var browse=new Button{Content="选择…",MinWidth=76,Margin=new Thickness(6,4,0,4)};browse.Click+=(_,_)=>choose();DockPanel.SetDock(browse,Dock.Right);panel.Children.Add(browse);panel.Children.Add(field);return panel;
    }
    void ChooseTemplate()
    {
        var dialog=new OpenFileDialog{Filter="DXF 图框与标注模板 (*.dxf)|*.dxf"};if(dialog.ShowDialog(this)!=true)return;
        RememberTitles();string previous=template.Text;template.Text=dialog.FileName;
        try{LoadTemplate(Convert.ToString(styles.SelectedItem)??"_5");}
        catch(Exception ex){template.Text=previous;MessageBox.Show(this,ex.Message,"模板读取失败",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    void ChooseFonts()
    {
        var dialog=new OpenFolderDialog{Title="选择包含 gbenor.shx 和 gbcbig.shx 的外置字体目录",InitialDirectory=Directory.Exists(fonts.Text)?fonts.Text:""};
        if(dialog.ShowDialog(this)==true)fonts.Text=dialog.FolderName;
    }
    void RememberTitles(){foreach(var pair in titleEditors)titleValues[pair.Key]=pair.Value.Text.Trim();}
    void LoadTemplate(string selected)
    {
        string[] names=string.IsNullOrWhiteSpace(template.Text)?["_5"]:(netDxf.DxfDocument.Load(template.Text)??throw new InvalidDataException("无法读取 DXF 模板。")).DimensionStyles.Select(s=>s.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var fields=DrawingResources.TitleFields(new(){TemplatePath=template.Text});
        styles.ItemsSource=names;styles.SelectedItem=names.FirstOrDefault(n=>n.Equals(selected,StringComparison.OrdinalIgnoreCase))??names.FirstOrDefault();
        titles.Children.Clear();titles.RowDefinitions.Clear();titles.ColumnDefinitions.Clear();titleEditors.Clear();
        for(int pair=0;pair<2;pair++){titles.ColumnDefinitions.Add(new(){Width=new GridLength(90)});titles.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});}
        titles.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var hint=new TextBlock{Text=fields.Count==0?"选择含标题栏属性的 DXF 模板后，可编辑对应字段。":"留空保留模板原值；图名留空时按零件编号自动填写。",TextWrapping=TextWrapping.Wrap,Opacity=.75,Margin=new Thickness(0,0,0,12)};
        Grid.SetColumnSpan(hint,4);titles.Children.Add(hint);int index=0;
        foreach(var field in fields)
        {
            int row=1+index/2,column=index%2*2;if(column==0)titles.RowDefinitions.Add(new(){Height=GridLength.Auto});
            var label=new TextBlock{Text=field.Key,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(column==0?0:12,6,8,6)};
            var editor=Field();editor.Text=titleValues.GetValueOrDefault(field.Key)??"";editor.ToolTip="模板原值："+field.Value;titleEditors.Add(field.Key,editor);
            Grid.SetRow(label,row);Grid.SetColumn(label,column);titles.Children.Add(label);Grid.SetRow(editor,row);Grid.SetColumn(editor,column+1);titles.Children.Add(editor);index++;
        }
    }
    static double Number(TextBox field,string name)=>double.TryParse(field.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&double.IsFinite(value)?value:throw new InvalidDataException(name+"必须为有效数字。");
    void Save(object sender,RoutedEventArgs e)
    {
        try
        {
            RememberTitles();var value=initial with{TemplatePath=template.Text,FontDirectory=fonts.Text,DimensionStyleName=Convert.ToString(styles.SelectedItem)??"_5",HiddenLineScale=Number(hidden,"隐藏线比例"),ViewGap=Number(viewGap,"视图间距"),FrameMargin=Number(margin,"图框留白"),SheetGap=Number(sheetGap,"图纸间距"),IncludeFrame=frame.IsChecked==true,TitleBlockAttributes=new(titleValues,StringComparer.OrdinalIgnoreCase)};
            _=DrawingResources.Load(value);Value=value;DialogResult=true;
        }
        catch(Exception ex){MessageBox.Show(this,ex.Message,"请检查设置",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
}
