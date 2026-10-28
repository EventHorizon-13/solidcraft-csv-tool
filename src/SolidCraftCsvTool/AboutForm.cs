using System.Diagnostics;

namespace SolidCraftCsvTool;

internal sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text="关于";Icon=AppArtwork.LoadIcon();Font=SystemFonts.MessageBoxFont;
        AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(470,292);FormBorderStyle=FormBorderStyle.FixedDialog;
        StartPosition=FormStartPosition.CenterParent;MaximizeBox=false;MinimizeBox=false;ShowInTaskbar=false;
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(24),ColumnCount=1,RowCount=7};
        root.ColumnStyles.Add(new(SizeType.Percent,100));
        foreach(int height in new[]{60,28,28,28,32})root.RowStyles.Add(new(SizeType.Absolute,height));
        root.RowStyles.Add(new(SizeType.Percent,100));root.RowStyles.Add(new(SizeType.AutoSize));Controls.Add(root);
        var header=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Margin=Padding.Empty};
        header.ColumnStyles.Add(new(SizeType.Absolute,56));header.ColumnStyles.Add(new(SizeType.Percent,100));
        header.RowStyles.Add(new(SizeType.Absolute,28));header.RowStyles.Add(new(SizeType.Percent,100));
        var image=AppArtwork.LoadImage();
        var logo=new PictureBox{Image=image,SizeMode=PictureBoxSizeMode.Zoom,Size=new Size(40,40),Anchor=AnchorStyles.Top|AnchorStyles.Left,Margin=new Padding(0,0,16,0)};
        header.Controls.Add(logo,0,0);header.SetRowSpan(logo,2);FormClosed+=(_,_)=>image.Dispose();
        header.Controls.Add(new Label{Text=AppBuildInfo.ProductName,Font=new Font(Font,FontStyle.Bold),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Margin=Padding.Empty},1,0);
        header.Controls.Add(new Label{Text="版本 "+AppBuildInfo.Version,Dock=DockStyle.Fill,TextAlign=ContentAlignment.TopLeft,Margin=Padding.Empty},1,1);root.Controls.Add(header,0,0);
        var author=new LinkLabel{Text="EventHorizon-13",AutoSize=true,Anchor=AnchorStyles.Left,Margin=Padding.Empty};
        author.LinkClicked+=(_,_)=>OpenLink("https://github.com/EventHorizon-13");root.Controls.Add(author,0,1);
        root.Controls.Add(new Label{Text="构建时间  "+AppBuildInfo.BuildDate,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Margin=Padding.Empty},0,2);
        root.Controls.Add(new Label{Text="参数建模、STEP 与加工图批量导出",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Margin=Padding.Empty},0,3);
        var links=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
        var home=new LinkLabel{Text="项目主页",AutoSize=true,Margin=new Padding(0,6,18,0)};home.LinkClicked+=(_,_)=>OpenLink("https://github.com/EventHorizon-13/solidcraft-roller-seal");
        var notices=new LinkLabel{Text="组件与许可",AutoSize=true,Margin=new Padding(0,6,0,0)};notices.LinkClicked+=(_,_)=>ShowNotices();
        links.Controls.Add(home);links.Controls.Add(notices);root.Controls.Add(links,0,4);
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=false,Margin=Padding.Empty};
        var check=new Button{Text="检查更新",AutoSize=true,MinimumSize=new Size(90,28),Margin=new Padding(0,0,8,0),UseVisualStyleBackColor=true};
        var close=new Button{Text="关闭",AutoSize=true,MinimumSize=new Size(76,28),DialogResult=DialogResult.OK,Margin=Padding.Empty,UseVisualStyleBackColor=true};
        var updateStatus=new Label{AutoSize=true,Margin=new Padding(12,6,0,0)};
        check.Click+=async(_,_)=>await CheckUpdateAsync(check,updateStatus);
        buttons.Controls.Add(check);buttons.Controls.Add(close);buttons.Controls.Add(updateStatus);root.Controls.Add(buttons,0,6);
        AcceptButton=close;CancelButton=close;AppTheme.Attach(this);
    }
    internal void VerifyUi()
    {
        static IEnumerable<Control> All(Control root)=>root.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(All(c)));
        var check=All(this).OfType<Button>().Single(b=>b.Text=="检查更新");
        var close=All(this).OfType<Button>().Single(b=>b.Text=="关闭");
        if(check.Parent!=close.Parent||check.Top!=close.Top||check.Left!=0||close.Left<=check.Right||!check.Visible||!close.Visible)
            throw new InvalidOperationException("检查更新和关闭按钮必须同一行左对齐。");
    }

    async Task CheckUpdateAsync(Button button,Label status)
    {
        button.Enabled=false;button.Text="正在检查…";
        try
        {
            var release=await UpdateService.GetLatestAsync();
            if(!UpdateService.IsNewer(release))
            {
                status.Text="已是最新版本";
                MessageBox.Show(this,$"当前已是最新版本 {AppBuildInfo.Version}。","检查更新",MessageBoxButtons.OK,MessageBoxIcon.Information);
                return;
            }
            if(!release.Target.CanInstallInPlace)
            {
                if(MessageBox.Show(this,$"发现适用于 {release.Target.DisplayName} 的新版本 {release.Version}。打开下载页面？","发现新版本",MessageBoxButtons.YesNo,MessageBoxIcon.Information)==DialogResult.Yes)OpenLink(release.PageUrl);
                return;
            }
            string size=release.PackageBytes>=1024*1024?$"约 {release.PackageBytes/(1024*1024)} MB":$"{release.PackageBytes/1024} KB";
            if(MessageBox.Show(this,$"发现 {release.Target.DisplayName} 新版本 {release.Version}（当前 {AppBuildInfo.Version}），安装包 {size}。\n下载并更新后程序会自动重新启动。","发现新版本",MessageBoxButtons.YesNo,MessageBoxIcon.Information)!=DialogResult.Yes)return;
            string install=Path.GetDirectoryName(Environment.ProcessPath??"")??AppContext.BaseDirectory;
            string probe=Path.Combine(install,".update-permission-"+Guid.NewGuid().ToString("N"));
            try{using(File.Create(probe)){}File.Delete(probe);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {
                if(MessageBox.Show(this,"程序所在文件夹不允许自动写入。是否打开下载页面手动更新？","需要手动更新",MessageBoxButtons.YesNo,MessageBoxIcon.Information)==DialogResult.Yes)OpenLink(release.PageUrl);
                return;
            }
            button.Text="正在下载…";status.Text="正在准备更新";
            var progress=new Progress<int>(value=>status.Text=$"正在下载更新 {value}%");
            string stage=await UpdateService.DownloadAndStageAsync(release,progress);
            try{UpdateService.StartInstaller(stage);status.Text="正在安装并重新启动…";Application.Exit();Close();}
            catch{try{Directory.Delete(Path.GetDirectoryName(stage)!,true);}catch{}throw;}
        }
        catch(Exception ex)
        {
            status.Text="检查更新失败";
            if(MessageBox.Show(this,"暂时无法下载更新。可稍后重试，或打开 GitHub 下载页。\n\n"+ex.Message,"更新失败",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)==DialogResult.Yes)OpenLink(UpdateService.ReleasesUrl);
        }
        finally{if(!IsDisposed){button.Enabled=true;button.Text="检查更新";}}
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
        using Form dialog=new(){Text="组件与许可",Size=new Size(660,460),StartPosition=FormStartPosition.CenterParent,Icon=AppArtwork.LoadIcon()};
        dialog.Controls.Add(new TextBox{Text="SolidCraft CLI · 参数建模与 STEP\r\nnetDxf · DXF 写入\r\nMLightCAD · SHX 与矢量 PDF\r\nNode.js · PDF 渲染运行时\r\n\r\n"+text,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,Font=new Font("Microsoft YaHei UI",9F)});
        AppTheme.Attach(dialog);dialog.ShowDialog(this);
    }
}
