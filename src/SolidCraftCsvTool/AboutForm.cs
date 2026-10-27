using System.Diagnostics;

namespace SolidCraftCsvTool;

internal sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text="关于";Icon=AppArtwork.LoadIcon();Font=new Font("Microsoft YaHei UI",9F);
        ClientSize=new Size(520,330);FormBorderStyle=FormBorderStyle.FixedDialog;
        StartPosition=FormStartPosition.CenterParent;MaximizeBox=false;MinimizeBox=false;ShowInTaskbar=false;
        BackColor=Color.White;
        TableLayoutPanel content=new(){Dock=DockStyle.Fill,Padding=new Padding(24),ColumnCount=2,RowCount=6};
        content.ColumnStyles.Add(new(SizeType.Absolute,104));content.ColumnStyles.Add(new(SizeType.Percent,100));
        foreach(int height in new[]{52,32,32,32,54})content.RowStyles.Add(new(SizeType.Absolute,height));
        content.RowStyles.Add(new(SizeType.Percent,100));Controls.Add(content);
        var image=AppArtwork.LoadImage();
        var logo=new PictureBox{Image=image,SizeMode=PictureBoxSizeMode.Zoom,Dock=DockStyle.Fill,Margin=new Padding(0,0,20,0)};
        content.Controls.Add(logo,0,0);content.SetRowSpan(logo,3);FormClosed+=(_,_)=>image.Dispose();
        content.Controls.Add(new Label{Text=AppBuildInfo.ProductName,Font=new Font(Font.FontFamily,13,FontStyle.Bold),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},1,0);
        content.Controls.Add(new Label{Text="版本  "+AppBuildInfo.Version,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},1,1);
        var author=new LinkLabel{Text="作者  Wei Yunhui",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};
        author.Links.Clear();author.Links.Add(4,10,"https://github.com/EventHorizon-13");
        author.LinkClicked+=(_,_)=>OpenLink("https://github.com/EventHorizon-13");content.Controls.Add(author,1,2);
        content.Controls.Add(new Label{Text="编译  "+AppBuildInfo.BuildDate,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},1,3);
        FlowLayoutPanel links=new(){Dock=DockStyle.Fill,Padding=new Padding(0,18,0,0),WrapContents=false};
        var home=new LinkLabel{Text="项目主页",AutoSize=true,Margin=new Padding(0,0,20,0)};
        home.LinkClicked+=(_,_)=>
        {
            try{Process.Start(new ProcessStartInfo("https://github.com/EventHorizon-13/solidcraft-roller-seal"){UseShellExecute=true});}
            catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or InvalidOperationException){MessageBox.Show(this,"未能打开浏览器，请稍后重试。","项目主页");}
        };
        var notices=new LinkLabel{Text="第三方许可",AutoSize=true};notices.LinkClicked+=(_,_)=>ShowNotices();
        links.Controls.Add(home);links.Controls.Add(notices);content.Controls.Add(links,0,4);content.SetColumnSpan(links,2);
        Button close=new(){Text="关闭",AutoSize=true,DialogResult=DialogResult.OK,Anchor=AnchorStyles.Right|AnchorStyles.Bottom};
        content.Controls.Add(close,1,5);AcceptButton=close;CancelButton=close;
    }
    void OpenLink(string url)
    {
        try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}
        catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or InvalidOperationException){MessageBox.Show(this,"未能打开浏览器，请稍后重试。","GitHub");}
    }
    void ShowNotices()
    {
        string file=Path.Combine(AppContext.BaseDirectory,"THIRD_PARTY_NOTICES.md");
        string text=File.Exists(file)?File.ReadAllText(file):"第三方组件：SolidCraft CLI 0.23.0、netDxf 2023.11.10、.NET 10。详细许可见软件包 THIRD_PARTY_NOTICES.md 与 SolidCraft 文件夹。";
        using Form dialog=new(){Text="第三方许可",Size=new Size(660,460),StartPosition=FormStartPosition.CenterParent,Icon=AppArtwork.LoadIcon()};
        dialog.Controls.Add(new TextBox{Text=text,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,Font=new Font("Microsoft YaHei UI",9F)});
        dialog.ShowDialog(this);
    }
}
