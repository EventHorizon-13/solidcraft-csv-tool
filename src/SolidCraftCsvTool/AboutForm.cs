using System.Diagnostics;

namespace SolidCraftCsvTool;

internal sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text="关于";Icon=AppArtwork.LoadIcon();Font=new Font("Microsoft YaHei UI",9F);
        ClientSize=new Size(560,430);FormBorderStyle=FormBorderStyle.FixedDialog;
        StartPosition=FormStartPosition.CenterParent;MaximizeBox=false;MinimizeBox=false;ShowInTaskbar=false;
        BackColor=Color.White;
        TableLayoutPanel content=new(){Dock=DockStyle.Fill,Padding=new Padding(24),ColumnCount=2,RowCount=8};
        content.ColumnStyles.Add(new(SizeType.Absolute,104));content.ColumnStyles.Add(new(SizeType.Percent,100));
        foreach(int height in new[]{52,32,32,32,72,38,50})content.RowStyles.Add(new(SizeType.Absolute,height));
        content.RowStyles.Add(new(SizeType.Percent,100));Controls.Add(content);
        var image=AppArtwork.LoadImage();
        var logo=new PictureBox{Image=image,SizeMode=PictureBoxSizeMode.Zoom,Dock=DockStyle.Fill,Margin=new Padding(0,0,20,0)};
        content.Controls.Add(logo,0,0);content.SetRowSpan(logo,3);FormClosed+=(_,_)=>image.Dispose();
        content.Controls.Add(new Label{Text=AppBuildInfo.ProductName,Font=new Font(Font.FontFamily,13,FontStyle.Bold),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},1,0);
        content.Controls.Add(new Label{Text="版本  "+AppBuildInfo.Version,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},1,1);
        var author=new LinkLabel{Text="作者  EventHorizon-13",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};
        author.Links.Clear();author.Links.Add(4,"EventHorizon-13".Length,"https://github.com/EventHorizon-13");
        author.LinkClicked+=(_,_)=>OpenLink("https://github.com/EventHorizon-13");content.Controls.Add(author,1,2);
        content.Controls.Add(new Label{Text="编译  "+AppBuildInfo.BuildDate,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},1,3);
        content.Controls.Add(new Label{Text="调用工具",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,4);
        content.Controls.Add(new Label{Text="SolidCraft CLI 0.23.0 — 参数建模、STEP 与三视图投影\nnetDxf 2023.11.10 — DXF 写入\nGitHub Releases API — 在线检查更新",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},1,4);
        FlowLayoutPanel updateRow=new(){Dock=DockStyle.Fill,Padding=new Padding(0,3,0,0),WrapContents=false};
        Button checkUpdate=new(){Text="检查更新",AutoSize=true,MinimumSize=new Size(100,27),UseVisualStyleBackColor=true,Margin=new Padding(0,0,12,0)};
        Label updateStatus=new(){Text="",AutoSize=true,ForeColor=SystemColors.GrayText,TextAlign=ContentAlignment.MiddleLeft,Margin=new Padding(0,6,0,0)};
        checkUpdate.Click+=async(_,_)=>await CheckUpdateAsync(checkUpdate,updateStatus);
        updateRow.Controls.Add(checkUpdate);updateRow.Controls.Add(updateStatus);content.Controls.Add(updateRow,0,5);content.SetColumnSpan(updateRow,2);
        FlowLayoutPanel links=new(){Dock=DockStyle.Fill,Padding=new Padding(0,14,0,0),WrapContents=false};
        var home=new LinkLabel{Text="项目主页",AutoSize=true,Margin=new Padding(0,0,20,0)};
        home.LinkClicked+=(_,_)=>
        {
            try{Process.Start(new ProcessStartInfo("https://github.com/EventHorizon-13/solidcraft-roller-seal"){UseShellExecute=true});}
            catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or InvalidOperationException){MessageBox.Show(this,"未能打开浏览器，请稍后重试。","项目主页");}
        };
        var notices=new LinkLabel{Text="第三方许可",AutoSize=true};notices.LinkClicked+=(_,_)=>ShowNotices();
        links.Controls.Add(home);links.Controls.Add(notices);content.Controls.Add(links,0,6);content.SetColumnSpan(links,2);
        Button close=new(){Text="关闭",AutoSize=true,DialogResult=DialogResult.OK,Anchor=AnchorStyles.Right|AnchorStyles.Bottom};
        content.Controls.Add(close,1,7);AcceptButton=close;CancelButton=close;
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
        using Form dialog=new(){Text="第三方许可",Size=new Size(660,460),StartPosition=FormStartPosition.CenterParent,Icon=AppArtwork.LoadIcon()};
        dialog.Controls.Add(new TextBox{Text=text,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,Font=new Font("Microsoft YaHei UI",9F)});
        dialog.ShowDialog(this);
    }
}
