using System.Diagnostics;

/// <summary>The step-by-step pairing window. Everything works from the keyboard, because the
/// user's only mouse may be the one being paired: Enter is the main button, Esc cancels.</summary>
sealed class PairForm : Form
{
    const int ContentWidth = 460;

    readonly ReceiverClient _receiver;
    readonly MouseModel _mouse;
    readonly CancellationTokenSource _cancel = new();
    readonly Label _title = new() { AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Margin = new Padding(0, 0, 0, 8) };
    readonly Label _body = new() { AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Margin = new Padding(0, 0, 0, 10) };
    readonly ProgressBar _progress = new() { Width = ContentWidth, Height = 14, Visible = false, Maximum = ReceiverClient.LockSeconds, Margin = new Padding(0, 0, 0, 10) };
    readonly Button _primary = new() { Width = 170, Height = 36, Margin = new Padding(0) };
    readonly Button _secondary = new() { Width = 110, Height = 36, Margin = new Padding(10, 0, 0, 0) };
    readonly System.Windows.Forms.Timer _countdown = new() { Interval = 1000 };

    bool _running;
    int _secondsLeft;

    public PairForm(ReceiverClient receiver, MouseModel mouse, bool ghubRunning)
    {
        _receiver = receiver;
        _mouse = mouse;

        Text = $"Pairing {mouse.Name}";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _title.Font = new Font(Font.FontFamily, 13f, FontStyle.Bold);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        buttons.Controls.Add(_primary);
        buttons.Controls.Add(_secondary);

        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(20),
            MinimumSize = new Size(ContentWidth + 40, 0),
        };
        root.Controls.Add(_title);
        root.Controls.Add(_body);
        root.Controls.Add(_progress);
        root.Controls.Add(buttons);
        Controls.Add(root);

        AcceptButton = _primary;
        CancelButton = _secondary;
        _countdown.Tick += (_, _) => Tick();

        var steps = new List<string>();
        if (ghubRunning) steps.Add("Quit G HUB from the system tray.");
        if (mouse.HasDataCable) steps.Add("Unplug the mouse's USB cable.");
        steps.Add("Switch the mouse OFF (the switch is on its underside).");
        var numbered = steps.Select((step, i) => $"{i + 1}.  {step}");

        ShowStep("Step 1 of 3: Get the mouse ready",
            string.Join(Environment.NewLine, numbered) + Environment.NewLine + Environment.NewLine
            + "Your mouse may stop working during this, so use the keyboard: press Enter to continue, or Esc to cancel.",
            "Continue (Enter)", StartPairing, "Cancel", Close);

        FormClosing += (_, e) =>
        {
            if (!_running) return;
            e.Cancel = true; // the receiver has to be put back first
            _cancel.Cancel();
        };
    }

    async void StartPairing()
    {
        _running = true;
        _primary.Enabled = false;
        ShowStep("Starting...", "Opening the receiver for pairing.", null, null, "Cancel (Esc)", () => _cancel.Cancel());

        var result = await Task.Run(() => _receiver.Pair(_mouse, () => BeginInvoke(ShowSwitchOn), _cancel.Token));

        _running = false;
        _countdown.Stop();
        _progress.Visible = false;
        ShowResult(result);
    }

    void ShowSwitchOn()
    {
        if (!_running) return;
        _secondsLeft = ReceiverClient.LockSeconds;
        _progress.Value = _secondsLeft;
        _progress.Visible = true;
        _countdown.Start();
        ShowStep("Step 2 of 3: Switch the mouse ON now",
            CountdownText(), null, null, "Cancel (Esc)", () => _cancel.Cancel());
    }

    void Tick()
    {
        if (_secondsLeft > 0) _secondsLeft--;
        _progress.Value = _secondsLeft;
        _body.Text = CountdownText();
    }

    string CountdownText() => "Then click the LEFT mouse button 3 times while this window is waiting."
        + Environment.NewLine + Environment.NewLine
        + $"The receiver is looking for your mouse. {_secondsLeft} seconds left."
        + Environment.NewLine + "Keep the mouse close to the receiver.";

    void ShowResult(PairResult result)
    {
        if (result.Outcome == PairOutcome.Cancelled)
        {
            Close();
            return;
        }

        var title = result.Outcome switch
        {
            PairOutcome.Success => "Step 3 of 3: Paired!",
            PairOutcome.PairedNoAnswer => "Step 3 of 3: Almost there",
            _ => "Pairing didn't work",
        };
        _title.ForeColor = result.Outcome == PairOutcome.Success ? Color.FromArgb(21, 128, 61)
            : result.Outcome == PairOutcome.PairedNoAnswer ? Color.FromArgb(180, 83, 9)
            : Color.FromArgb(185, 28, 28);

        if (result.Outcome == PairOutcome.Success)
            ShowStep(title, result.Message, "Close (Enter)", Close, null, null);
        else
            ShowStep(title, result.Message, "Close (Enter)", Close, "Open log", OpenLog);
    }

    static void OpenLog()
    {
        try { Process.Start(new ProcessStartInfo(ReceiverClient.LogPath) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException) { }
    }

    Action? _primaryAction, _secondaryAction;
    bool _wired;

    void ShowStep(string title, string body, string? primaryText, Action? primary, string? secondaryText, Action? secondary)
    {
        if (!_wired)
        {
            _wired = true;
            _primary.Click += (_, _) => _primaryAction?.Invoke();
            _secondary.Click += (_, _) => _secondaryAction?.Invoke();
            _secondary.DialogResult = DialogResult.None;
        }

        _title.Text = title;
        _body.Text = body;

        _primaryAction = primary;
        _primary.Visible = primary != null;
        _primary.Enabled = primary != null;
        _primary.Text = primaryText ?? "";

        _secondaryAction = secondary;
        _secondary.Visible = secondary != null;
        _secondary.Text = secondaryText ?? "";

        // With no main button, focus nothing: a held Enter key must not press Cancel. Esc still works.
        ActiveControl = primary != null ? _primary : null;
    }
}
