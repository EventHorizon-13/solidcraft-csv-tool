using System.Text.Json;
using Microsoft.Win32;

namespace SolidCraftCsvTool;

internal enum ThemeMode { Automatic, Dark, Light }

internal static class AppTheme
{
    public static ThemeMode Mode {get;private set;}
    public static void Initialize()
    {
        var mode=ThemeMode.Automatic;
        try
        {
            string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SolidCraft","csv-tool","settings.json");
            if(File.Exists(path))
            {
                using var settings=JsonDocument.Parse(File.ReadAllText(path));
                if(settings.RootElement.TryGetProperty("themeMode",out var saved)&&saved.ValueKind==JsonValueKind.String&&Enum.TryParse<ThemeMode>(saved.GetString(),out var parsed)&&Enum.IsDefined(parsed))mode=parsed;
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException){}
        SetMode(mode);
    }
    public static void SetMode(ThemeMode mode)
    {
        Mode=mode;
#pragma warning disable WFO5001
        Application.SetColorMode(mode switch {ThemeMode.Dark=>SystemColorMode.Dark,ThemeMode.Light=>SystemColorMode.Classic,_=>SystemColorMode.System});
#pragma warning restore WFO5001
        foreach(Form form in Application.OpenForms)Apply(form);
    }
    public static void Attach(Form form)
    {
        UserPreferenceChangedEventHandler changed=(_,_)=>
        {
            if(form.IsDisposed||!form.IsHandleCreated)return;
            try{form.BeginInvoke((Action)(()=>{if(!form.IsDisposed)SetMode(Mode);}));}catch(InvalidOperationException){}
        };
        SystemEvents.UserPreferenceChanged+=changed;
        form.Disposed+=(_,_)=>SystemEvents.UserPreferenceChanged-=changed;
        Apply(form);
    }
    public static void Apply(Form form)
    {
        void Native(Control control)
        {
            control.ResetBackColor();control.ResetForeColor();
            if(control.Tag is string role&&role=="muted")control.ForeColor=SystemColors.GrayText;
            if(control is Button button){button.FlatStyle=FlatStyle.Standard;button.UseVisualStyleBackColor=true;}
            if(control is TextBoxBase text)text.BorderStyle=BorderStyle.Fixed3D;
            if(control is NumericUpDown number)number.BorderStyle=BorderStyle.Fixed3D;
            if(control is ComboBox combo)combo.FlatStyle=FlatStyle.Standard;
            if(control is LinkLabel link){link.LinkColor=SystemColors.HotTrack;link.ActiveLinkColor=SystemColors.HotTrack;link.VisitedLinkColor=SystemColors.HotTrack;}
            if(control is DataGridView grid)
            {
                grid.EnableHeadersVisualStyles=true;grid.BackgroundColor=SystemColors.Window;grid.GridColor=SystemColors.ControlLight;
                grid.DefaultCellStyle.BackColor=SystemColors.Window;grid.DefaultCellStyle.ForeColor=SystemColors.WindowText;
                grid.DefaultCellStyle.SelectionBackColor=SystemColors.Highlight;grid.DefaultCellStyle.SelectionForeColor=SystemColors.HighlightText;
                grid.AlternatingRowsDefaultCellStyle.BackColor=Color.Empty;
                grid.ColumnHeadersDefaultCellStyle.BackColor=SystemColors.Control;grid.ColumnHeadersDefaultCellStyle.ForeColor=SystemColors.ControlText;
                grid.RowHeadersDefaultCellStyle.BackColor=SystemColors.Control;grid.RowHeadersDefaultCellStyle.ForeColor=SystemColors.ControlText;
            }
            if(control is StatusStrip strip)
            {
                strip.Renderer=new ToolStripSystemRenderer();
                foreach(ToolStripItem item in strip.Items){item.BackColor=SystemColors.Control;item.ForeColor=SystemColors.ControlText;}
            }
            foreach(Control child in control.Controls)Native(child);
        }
        form.SuspendLayout();Native(form);form.ResumeLayout(true);form.Invalidate(true);
    }
}
