using System.Drawing.Drawing2D;

namespace SolidCraftCsvTool;

internal static class Ui
{
    public static GraphicsPath Round(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90); path.AddArc(bounds.Right-d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right-d, bounds.Bottom-d, d, d, 0, 90); path.AddArc(bounds.X, bounds.Bottom-d, d, d, 90, 90); path.CloseFigure(); return path;
    }
    public static Control Field(Control input)
    {
        if(input is TextBox text)text.BorderStyle=BorderStyle.None;
        if(input is NumericUpDown number)number.BorderStyle=BorderStyle.None;
        var field=new FieldPanel{Dock=DockStyle.Fill,Height=38,MinimumSize=new Size(0,36),Padding=new Padding(10,8,10,6),Margin=input.Margin};
        input.Dock=DockStyle.Fill;input.Margin=Padding.Empty;field.Controls.Add(input);
        input.GotFocus+=(_,_)=>field.Invalidate();input.LostFocus+=(_,_)=>field.Invalidate();return field;
    }
}

internal sealed class FieldPanel : Panel
{
    public FieldPanel(){DoubleBuffered=true;ResizeRedraw=true;}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);e.Graphics.Clear(Parent?.BackColor??AppTheme.Background);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using var path=Ui.Round(new RectangleF(.5f,.5f,Width-1,Height-1),7);
        using var fill=new SolidBrush(AppTheme.Field);e.Graphics.FillPath(fill,path);
        using var pen=new Pen(ContainsFocus?AppTheme.Accent:AppTheme.Border,ContainsFocus?1.5f:1);e.Graphics.DrawPath(pen,path);
    }
}

internal sealed class SurfacePanel : Panel
{
    public SurfacePanel(){DoubleBuffered=true;ResizeRedraw=true;Padding=new Padding(16);}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);e.Graphics.Clear(Parent?.BackColor??AppTheme.Background);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using var path=Ui.Round(new RectangleF(.5f,.5f,Width-1,Height-1),12);
        using var fill=new SolidBrush(AppTheme.Surface);e.Graphics.FillPath(fill,path);
        using var pen=new Pen(AppTheme.Border);e.Graphics.DrawPath(pen,path);
    }
}

internal sealed class ModernButton : Button
{
    bool hover,pressed;
    [System.ComponentModel.DefaultValue(false)]
    internal bool Primary {get;set;}
    [System.ComponentModel.DefaultValue(false)]
    internal bool Quiet {get;set;}
    public ModernButton()
    {
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;
        AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;Padding=new Padding(12,7,12,7);Margin=new Padding(0,0,6,0);Cursor=Cursors.Hand;
    }
    protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hover=false;pressed=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnMouseDown(MouseEventArgs e){pressed=true;Invalidate();base.OnMouseDown(e);}
    protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
    protected override void OnEnabledChanged(EventArgs e){Invalidate();base.OnEnabledChanged(e);}
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor??AppTheme.Background);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        Color fill=Primary?AppTheme.Accent:Quiet?(hover?AppTheme.Hover:Parent?.BackColor??AppTheme.Background):AppTheme.Button;
        if(pressed)fill=ControlPaint.Dark(fill,.08f);else if(hover&&Primary)fill=ControlPaint.Light(fill,.12f);
        if(!Enabled)fill=AppTheme.Button;
        using var path=Ui.Round(new RectangleF(.5f,.5f,Width-1,Height-1),7);
        using var brush=new SolidBrush(fill);e.Graphics.FillPath(brush,path);
        if(!Primary&&!Quiet){using var pen=new Pen(AppTheme.Border);e.Graphics.DrawPath(pen,path);}
        TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,!Enabled?AppTheme.Muted:Primary?Color.White:ForeColor,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
        if(Focused&&ShowFocusCues){using var pen=new Pen(AppTheme.Accent);using var focus=Ui.Round(new RectangleF(2,2,Width-4,Height-4),5);e.Graphics.DrawPath(pen,focus);}
    }
}

internal sealed class ModernCheckBox : CheckBox
{
    public ModernCheckBox(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);AutoSize=true;Cursor=Cursors.Hand;}
    public override Size GetPreferredSize(Size proposedSize)=>new(TextRenderer.MeasureText(Text,Font).Width+28,Math.Max(Font.Height+10,28*DeviceDpi/96));
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        float size=14*DeviceDpi/96f;
        var box=new RectangleF(1,(Height-size)/2,size,size);
        using var path=Ui.Round(box,3);
        using var fill=new SolidBrush(Checked?AppTheme.Accent:AppTheme.Field);e.Graphics.FillPath(fill,path);
        if(!Checked){using var pen=new Pen(AppTheme.Border);e.Graphics.DrawPath(pen,path);}
        if(Checked){using var pen=new Pen(Color.White,1.8f);e.Graphics.DrawLines(pen,[new PointF(box.Left+3,box.Top+size*.5f),new PointF(box.Left+size*.43f,box.Bottom-3),new PointF(box.Right-3,box.Top+3)]);}
        TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle((int)size+8,0,Width-(int)size-8,Height),Enabled?ForeColor:AppTheme.Muted,TextFormatFlags.Left|TextFormatFlags.VerticalCenter);
        if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,ClientRectangle);
    }
}
