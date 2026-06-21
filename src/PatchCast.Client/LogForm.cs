namespace PatchCast.Client;

internal sealed class LogForm : Form
{
    private readonly ClientActivityLog activityLog;
    private readonly TextBox logText = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 9f)
    };

    public LogForm(ClientActivityLog activityLog)
    {
        this.activityLog = activityLog;
        Text = "PatchCast Activity Log";
        ClientSize = new Size(900, 500);
        StartPosition = FormStartPosition.CenterParent;
        logText.Lines = activityLog.Snapshot();
        logText.SelectionStart = logText.TextLength;
        logText.ScrollToCaret();
        Controls.Add(logText);
        activityLog.EntryAdded += OnEntryAdded;
    }

    private void OnEntryAdded(string entry)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(() => OnEntryAdded(entry));
            return;
        }
        logText.AppendText((logText.TextLength == 0 ? string.Empty : Environment.NewLine) + entry);
        logText.SelectionStart = logText.TextLength;
        logText.ScrollToCaret();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            activityLog.EntryAdded -= OnEntryAdded;
        base.Dispose(disposing);
    }
}
