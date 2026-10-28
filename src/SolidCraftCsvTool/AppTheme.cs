using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SolidCraftCsvTool;

internal enum ThemeMode { Automatic, Dark, Light }

internal static class AppTheme
{
    public static ThemeMode Mode { get; private set; }
    public static bool IsDark
    {
        get
        {
            if (SystemInformation.HighContrast) return false;
            if (Mode != ThemeMode.Automatic) return Mode == ThemeMode.Dark;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }
    }

    public static Color Background=>IsDark?Color.FromArgb(27,28,31):Color.FromArgb(244,245,247);
    public static Color Surface=>IsDark?Color.FromArgb(35,37,41):Color.White;
    public static Color Field=>IsDark?Color.FromArgb(43,45,50):Color.FromArgb(248,249,251);
    public static Color Border=>IsDark?Color.FromArgb(57,60,66):Color.FromArgb(224,227,232);
    public static Color Accent=>Color.FromArgb(36,119,235);
    public static Color Muted=>IsDark?Color.FromArgb(153,159,170):Color.FromArgb(119,125,137);
    public static Color Button=>IsDark?Color.FromArgb(48,51,57):Color.White;
    public static Color Hover=>IsDark?Color.FromArgb(57,61,69):Color.FromArgb(232,236,242);

    public static void SetMode(ThemeMode mode)
    {
        Mode = mode;
        foreach (Form form in Application.OpenForms) Apply(form);
    }

    public static void Attach(Form form)
    {
        UserPreferenceChangedEventHandler changed = (_, _) =>
        {
            if (form.IsDisposed || !form.IsHandleCreated) return;
            try { form.BeginInvoke((Action)(() => { if (!form.IsDisposed) Apply(form); })); }
            catch (InvalidOperationException) { }
        };
        SystemEvents.UserPreferenceChanged += changed;
        form.Disposed += (_, _) => SystemEvents.UserPreferenceChanged -= changed;
        form.HandleCreated += (_, _) => Apply(form);
        Apply(form);
    }

    public static void Apply(Form form)
    {
        bool dark = IsDark;
        Color background = Background;
        Color surface = Surface;
        Color foreground = dark ? Color.FromArgb(230, 233, 239) : Color.FromArgb(35, 43, 56);
        Color header = dark ? Color.FromArgb(40,43,48) : Color.FromArgb(248,249,251);
        Color selection = dark ? Color.FromArgb(39,55,76) : Color.FromArgb(235,243,255);
        void Paint(Control control)
        {
            bool inCard=control is SurfacePanel||Ancestors(control).Any(c=>c is SurfacePanel);
            control.BackColor = control is FieldPanel||control.Parent is FieldPanel ? Field : control is TextBoxBase or ComboBox or NumericUpDown or DataGridView ? surface : inCard?surface:background;
            control.ForeColor = foreground;
            if (control is Button button)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.UseVisualStyleBackColor = false;
                button.BackColor = Button;
                button.FlatAppearance.BorderColor = Border;
                if(button is not ModernButton)button.FlatAppearance.BorderSize=0;
            }
            if(control.Tag is string role&&role=="muted")control.ForeColor=Muted;
            if(control is ComboBox combo){combo.FlatStyle=FlatStyle.Flat;combo.BackColor=Field;}
            if(control is TextBoxBase text)text.BorderStyle=BorderStyle.None;
            if(control is NumericUpDown number)number.BorderStyle=BorderStyle.None;
            if (control is LinkLabel link)
            {
                link.LinkColor = dark ? Color.FromArgb(123, 184, 255) : Color.FromArgb(37, 99, 235);
                link.ActiveLinkColor = link.LinkColor;
                link.VisitedLinkColor = link.LinkColor;
            }
            if (control is DataGridView grid)
            {
                grid.BackgroundColor = surface;
                grid.DefaultCellStyle.BackColor = surface;
                grid.DefaultCellStyle.ForeColor = foreground;
                grid.DefaultCellStyle.SelectionBackColor = selection;
                grid.DefaultCellStyle.SelectionForeColor = foreground;
                grid.AlternatingRowsDefaultCellStyle.BackColor = dark ? Color.FromArgb(32, 35, 40) : Color.FromArgb(249, 250, 252);
                grid.ColumnHeadersDefaultCellStyle.BackColor = header;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
                grid.RowHeadersDefaultCellStyle.BackColor = header;
                grid.RowHeadersDefaultCellStyle.ForeColor = foreground;
                grid.RowHeadersDefaultCellStyle.SelectionBackColor = selection;
                grid.RowHeadersDefaultCellStyle.SelectionForeColor = foreground;
                grid.GridColor = Border;
            }
            if (control is StatusStrip strip)
                foreach (ToolStripItem item in strip.Items) { item.BackColor = background; item.ForeColor = foreground; }
            if(control.IsHandleCreated&&control is TextBoxBase or DataGridView)
                _=SetWindowTheme(control.Handle,dark?"DarkMode_Explorer":"Explorer",null);
            foreach (Control child in control.Controls) Paint(child);
        }
        form.SuspendLayout();
        Paint(form);
        if (form.IsHandleCreated)
        {
            int value = dark ? 1 : 0;
            try { _ = DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int)); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        form.ResumeLayout(true);
        form.Invalidate(true);
    }

    static IEnumerable<Control> Ancestors(Control control)
    {
        for(Control? parent=control.Parent;parent!=null;parent=parent.Parent)yield return parent;
    }
    [DllImport("uxtheme.dll",CharSet=CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr window,string theme,string? subId);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
