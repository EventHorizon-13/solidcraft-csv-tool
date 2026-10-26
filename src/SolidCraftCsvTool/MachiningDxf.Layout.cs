namespace SolidCraftCsvTool;

internal static partial class MachiningDxf
{
    sealed class Scene
    {
        public List<(Action<double,double> Draw, Box2 Box)> Items { get; } = [];
        public Box2 Bounds => Items.Count == 0 ? new(0,0,0,0) : Union(Items.Select(i=>i.Box));
        public void Add(Action<double,double> draw, Box2 box) => Items.Add((draw,box));
        public void Include(Scene scene, double x=0, double y=0)
        {
            foreach (var item in scene.Items) Add((ox,oy)=>item.Draw(ox+x,oy+y),Shift(item.Box,x,y));
        }
        public void Render(double x,double y) { foreach (var item in Items) item.Draw(x,y); }
    }
    static Box2 Shift(Box2 b,double x,double y) => new(b.MinX+x,b.MinY+y,b.MaxX+x,b.MaxY+y);
    static Box2 Union(IEnumerable<Box2> boxes)
    {
        var all=boxes.ToArray();
        return new(all.Min(b=>b.MinX),all.Min(b=>b.MinY),all.Max(b=>b.MaxX),all.Max(b=>b.MaxY));
    }
    static double TextWidth(string value,double height) => Math.Max(height*.7,value.Sum(c=>c>127 ? 1.1 : .72)*height);
    static Box2 TextBounds(string value,Point2 at,double height,double rotation)
    {
        double width=TextWidth(value,height);
        return Math.Abs(rotation%180)>45 ? new(at.X-height*.6,at.Y-width/2,at.X+height*.6,at.Y+width/2) :
            new(at.X-width/2,at.Y-height*.6,at.X+width/2,at.Y+height*.6);
    }
    static Map Normalize(Projection2 p) => new(p,-p.Bounds.MinX,-p.Bounds.MinY);
    static Scene PlanPage(Writer writer,StepDrawingPage page,int? sequence)
    {
        if (!page.End.Name.Equals("left",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("第一角投影要求左视图位于主视图右侧，不能使用右视图。");
        Map front=Normalize(page.Front),top=Normalize(page.Top),end=Normalize(page.End);
        var frontScene=writer.Record(()=> {var arcs=DrawView(writer,front);DrawFrontAnnotations(writer,front,arcs);});
        var planScene=writer.Record(()=> {DrawView(writer,top);DrawPlanAnnotations(writer,top);});
        var endScene=writer.Record(()=> {DrawView(writer,end);DrawEndAnnotations(writer,end);});
        double sideX=frontScene.Bounds.MaxX+writer.Options.ViewGap-endScene.Bounds.MinX;
        double planY=frontScene.Bounds.MinY-writer.Options.ViewGap-planScene.Bounds.MaxY;
        var result=new Scene();result.Include(frontScene);result.Include(planScene,0,planY);result.Include(endScene,sideX,0);
        Box2 views=result.Bounds;
        double titleHeight=Math.Max(5,writer.DimensionTextHeight*.85),lineHeight=titleHeight*1.6;
        string title=sequence is int number ? $"序号 {number:D3}    {page.Name}" : page.Name;
        var lines=new List<string>();string current="";
        foreach(char c in title)
        {
            if(current.Length>0 && TextWidth(current+c,titleHeight)>views.Width){lines.Add(current);current="";}
            current+=c;
        }
        if(current.Length>0)lines.Add(current);
        double titleY=views.MaxY+writer.DimensionRow;
        var header=writer.Record(()=>
        {
            writer.Text("第一角投影 · 单位 mm",new((views.MinX+views.MaxX)/2,titleY),titleHeight*.8,"TITLE");
            for(int i=0;i<lines.Count;i++)writer.Text(lines[i],new((views.MinX+views.MaxX)/2,titleY+lineHeight*(lines.Count-i)),titleHeight,"TITLE");
        });
        result.Include(header);return result;
    }

    static void RenderSheets(Writer writer,IReadOnlyList<StepDrawingPage> pages)
    {
        var scenes=pages.Select((p,i)=>PlanPage(writer,p,i+1)).ToArray();
        double margin=writer.Options.FrameMargin;
        var frame=writer.Resources.Frame;
        double baseWidth=frame?.Boundary.Width ?? 1190,baseHeight=frame?.Boundary.Height ?? 849;
        double bottom=(frame==null ? 155 : frame.TitleTop-frame.Boundary.MinY)+margin;
        double availableWidth=baseWidth-2*margin,availableHeight=baseHeight-bottom-margin;
        if(availableWidth<=0 || availableHeight<=0)throw new InvalidDataException("图框内容区不足，请减小图框边距。");
        double factor=Math.Max(1,scenes.Max(s=>Math.Max(s.Bounds.Width/availableWidth,s.Bounds.Height/availableHeight)));
        double sheetWidth=baseWidth*factor,sheetHeight=baseHeight*factor;
        for(int i=0;i<scenes.Length;i++)
        {
            double sheetY=-i*(sheetHeight+writer.Options.SheetGap);
            if(writer.Options.IncludeFrame)writer.DrawFrame(sheetY,factor,sheetWidth,sheetHeight,pages[i].Name,i+1);
            Scene scene=scenes[i];Box2 b=scene.Bounds;
            double x=margin*factor+(availableWidth*factor-b.Width)/2-b.MinX;
            double y=sheetY+bottom*factor+(availableHeight*factor-b.Height)/2-b.MinY;
            scene.Render(x,y);
        }
    }

    // Assign close labels to separate dimension rows; numeric values and
    // extension origins stay unchanged when labels need more room.
    static int[] ChainLanes(Writer writer,double[] marks)
    {
        var ends=new List<double>();var lanes=new int[Math.Max(0,marks.Length-1)];
        for(int i=1;i<marks.Length;i++)
        {
            double width=TextWidth(writer.FormatDimension(marks[i]-marks[i-1]),writer.DimensionTextHeight);
            double left=(marks[i]+marks[i-1])/2-width/2,right=left+width;
            int lane=ends.FindIndex(e=>left>e+writer.DimensionTextHeight*.4);
            if(lane<0){lane=ends.Count;ends.Add(right);}else ends[lane]=right;
            lanes[i-1]=lane;
        }
        return lanes;
    }
}
