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
        Color background = dark ? Color.FromArgb(28, 30, 34) : Color.FromArgb(247, 248, 250);
        Color surface = dark ? Color.FromArgb(37, 40, 45) : Color.White;
        Color foreground = dark ? Color.FromArgb(230, 233, 239) : Color.FromArgb(35, 43, 56);
        Color header = dark ? Color.FromArgb(47, 51, 59) : Color.FromArgb(235, 238, 243);
        Color selection = dark ? Color.FromArgb(44, 68, 101) : Color.FromArgb(219, 234, 254);
        void Paint(Control control)
        {
            control.BackColor = control is TextBoxBase or ComboBox or NumericUpDown or DataGridView ? surface : background;
            control.ForeColor = foreground;
            if (control is Button button)
            {
                button.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                button.UseVisualStyleBackColor = !dark;
                if (dark) button.BackColor = header;
                button.FlatAppearance.BorderColor = dark ? Color.FromArgb(78, 85, 97) : SystemColors.ControlDark;
            }
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
                grid.GridColor = dark ? Color.FromArgb(62, 67, 76) : Color.FromArgb(228, 232, 238);
            }
            if (control is StatusStrip strip)
                foreach (ToolStripItem item in strip.Items) { item.BackColor = background; item.ForeColor = foreground; }
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

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
