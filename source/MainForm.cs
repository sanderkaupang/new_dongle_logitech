using System.Diagnostics;
using System.Drawing.Drawing2D;

enum LightState { Off, Green, Amber, Red }

/// <summary>A round status light.</summary>
sealed class StatusLight : Control
{
    LightState _state;

    public LightState State
    {
        get => _state;
        set { if (_state != value) { _state = value; Invalidate(); } }
    }

    public StatusLight()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(22, 22);
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var color = _state switch
        {
            LightState.Green => Color.FromArgb(34, 197, 94),
            LightState.Amber => Color.FromArgb(245, 158, 11),
            LightState.Red => Color.FromArgb(239, 68, 68),
            _ => Color.FromArgb(190, 190, 190),
        };
        using var brush = new SolidBrush(color);
        e.Graphics.FillEllipse(brush, 1, 1, Width - 3, Height - 3);
    }
}

sealed class MainForm : Form
{
    const int ContentWidth = 500;

    readonly ComboBox _mice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = ContentWidth, Margin = new Padding(0, 4, 0, 4) };
    readonly Label _compat = Wrapped();
    readonly StatusLight _receiverLight = new();
    readonly StatusLight _mouseLight = new();
    readonly Label _receiverText = Wrapped(ContentWidth - 40);
    readonly Label _mouseText = Wrapped(ContentWidth - 40);
    readonly CheckBox _skipCheck = new() { Text = "My mouse has no USB cable right now. Skip the mouse check.", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
    readonly Label _next = Wrapped();
    readonly Label _gHub = Wrapped();
    readonly Button _pair = new() { Text = "&Pair", Width = ContentWidth, Height = 44, Margin = new Padding(0, 8, 0, 4) };
    readonly Button _clear = new() { Text = "&Clear receiver (remove all pairings)", Width = ContentWidth, Height = 32, Margin = new Padding(0, 0, 0, 8) };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1500 };

    ReceiverClient? _receiver;
    List<SlotInfo> _slots = new();
    HashSet<ushort> _present = new();
    int _receiverCount;
    bool _polling, _pairing, _autoSelected, _ghubRunning;

    public MainForm()
    {
        Text = "Lightspeed Pair";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }

        _mice.Items.AddRange(Catalog.Mice);
        _mice.SelectedIndex = 0;
        _mice.SelectedIndexChanged += (_, _) => { _autoSelected = true; UpdateUi(); };
        _skipCheck.CheckedChanged += (_, _) => UpdateUi();
        _pair.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        _pair.Click += (_, _) => StartPairing();
        _clear.Click += (_, _) => ClearReceiver();
        _gHub.ForeColor = Color.FromArgb(180, 83, 9);

        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(20, 16, 20, 12),
        };
        root.Controls.Add(Heading("Lightspeed Pair", 16f));
        root.Controls.Add(Plain("Pair a Logitech LIGHTSPEED mouse with a USB receiver."));
        root.Controls.Add(Heading("1. Select your mouse", 11f));
        root.Controls.Add(_mice);
        root.Controls.Add(_compat);
        root.Controls.Add(Heading("2. Connect your devices", 11f));
        root.Controls.Add(LightRow(_receiverLight, _receiverText));
        root.Controls.Add(LightRow(_mouseLight, _mouseText));
        root.Controls.Add(_skipCheck);
        root.Controls.Add(Heading("3. Pair", 11f));
        root.Controls.Add(_next);
        root.Controls.Add(_gHub);
        root.Controls.Add(_pair);
        root.Controls.Add(_clear);
        root.Controls.Add(new Label
        {
            Text = $"Version {Application.ProductVersion}. Independent tool, not made or supported by Logitech.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 0, 0),
        });
        Controls.Add(root);

        _timer.Tick += (_, _) => Poll();
        Shown += (_, _) => { Poll(); _timer.Start(); };
        UpdateUi();
    }

    async void Poll()
    {
        if (_polling || _pairing) return;
        _polling = true;
        try
        {
            _present = await Task.Run(HidppPort.PresentPids);
            _ghubRunning = await Task.Run(IsGHubRunning);

            var found = Catalog.Receivers.Where(_present.Contains).ToList();
            _receiverCount = found.Count;
            if (found.Count != 1)
            {
                _receiver = null;
                _slots = new();
            }
            else if (_receiver == null || _receiver.Pid != found[0] || !_receiver.IsAlive)
            {
                var pid = found[0];
                var client = await Task.Run(() =>
                {
                    var port = HidppPort.Open(pid);
                    return port == null ? null : new ReceiverClient(port, pid);
                });
                _receiver = client;
                _slots = client != null ? await Task.Run(client.ReadSlots) : new();
            }

            if (!_autoSelected)
            {
                var plugged = Catalog.Mice.Where(m => m.UsbPid is ushort p && _present.Contains(p))
                    .Concat(Catalog.Mice.Where(m => _slots.Any(s => s.Wpid == m.Wpid))).FirstOrDefault();
                if (plugged != null)
                {
                    _mice.SelectedItem = plugged; // this also marks the selection as made
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _receiver = null; // the receiver went away mid-read, the next poll sorts it out
            _slots = new();
        }
        finally
        {
            _polling = false;
        }
        if (!IsDisposed) UpdateUi();
    }

    void UpdateUi()
    {
        var mouse = (MouseModel)_mice.SelectedItem!;
        var receiverOk = _receiver != null;

        if (_receiverCount > 1)
        {
            _receiverLight.State = LightState.Red;
            _receiverText.Text = "More than one LIGHTSPEED receiver is plugged in. Unplug all but the one you want to pair.";
        }
        else if (_receiverCount == 1 && _receiver == null)
        {
            _receiverLight.State = LightState.Red;
            _receiverText.Text = "Receiver found, but it could not be opened. Unplug it and plug it back in.";
        }
        else if (receiverOk)
        {
            _receiverLight.State = LightState.Green;
            _receiverText.Text = $"Receiver detected (046D:{_receiver!.Pid:X4}), {_slots.Count} device(s) paired.";
        }
        else
        {
            _receiverLight.State = LightState.Red;
            _receiverText.Text = "No receiver found. Plug your LIGHTSPEED USB receiver into this PC.";
        }

        var onCable = mouse.UsbPid is ushort usb && _present.Contains(usb);
        var paired = mouse.Wpid != null && _slots.Any(s => s.Wpid == mouse.Wpid);
        var other = Catalog.Mice.FirstOrDefault(m => m != mouse && m.UsbPid is ushort p && _present.Contains(p));
        var canDetect = mouse.UsbPid != null;

        if (onCable)
        {
            _mouseLight.State = LightState.Green;
            _mouseText.Text = $"{mouse.Name} detected on its USB cable.";
        }
        else if (paired)
        {
            _mouseLight.State = LightState.Green;
            _mouseText.Text = $"{mouse.Name} is already paired with this receiver.";
        }
        else if (!mouse.HasDataCable)
        {
            _mouseLight.State = LightState.Amber;
            _mouseText.Text = $"{mouse.Name} has no USB cable, so it can't be checked now. Keep it next to the receiver.";
        }
        else if (!canDetect)
        {
            _mouseLight.State = LightState.Amber;
            _mouseText.Text = $"This app doesn't have the USB ID for {mouse.Name} yet, so it can't be checked. You can still try pairing.";
        }
        else if (other != null)
        {
            _mouseLight.State = LightState.Red;
            _mouseText.Text = $"Found {other.Name} instead. Select it above, or plug in your {mouse.Name}.";
        }
        else
        {
            _mouseLight.State = LightState.Red;
            _mouseText.Text = $"{mouse.Name} not found. Connect it to this PC with its USB cable.";
        }

        var mouseOk = _mouseLight.State != LightState.Red || _skipCheck.Checked;
        _skipCheck.Visible = _mouseLight.State == LightState.Red;

        _compat.Text = mouse.TestedReceiver == null
            ? "Named in the receiver's listing, but not tested yet."
            : $"Tested and working with receiver 046D:{mouse.TestedReceiver:X4}."
              + (receiverOk && _receiver!.Pid != mouse.TestedReceiver ? " Your receiver is a different model, so results may vary." : "");

        _pair.Enabled = receiverOk && mouseOk;
        _clear.Enabled = receiverOk && _slots.Count > 0 && !_pairing;
        _next.Text = !receiverOk ? "Connect your receiver to continue."
            : !mouseOk ? "Connect the selected mouse to continue."
            : paired ? "This mouse is already paired. You only need to pair again if it stopped working."
            : "Ready. Press Pair and follow the instructions.";

        _gHub.Visible = _ghubRunning;
        _gHub.Text = "G HUB is open and may interfere with pairing. Quit it from the system tray first.";
    }

    async void StartPairing()
    {
        var receiver = _receiver;
        if (receiver == null) return;

        _pairing = true;
        using (var dialog = new PairForm(receiver, (MouseModel)_mice.SelectedItem!, _ghubRunning))
            dialog.ShowDialog(this);

        try { _slots = await Task.Run(receiver.ReadSlots); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { _slots = new(); }
        _pairing = false;
        Poll();
    }

    async void ClearReceiver()
    {
        var receiver = _receiver;
        if (receiver == null || _slots.Count == 0) return;

        var devices = string.Join(Environment.NewLine, _slots.Select(s =>
            $"    Slot {s.Slot}: {(s.Name.Length > 0 ? s.Name : "Unknown device")} (ID {s.Wpid:X4})"));
        var answer = MessageBox.Show(this,
            "Remove these devices from the receiver?" + Environment.NewLine + Environment.NewLine + devices + Environment.NewLine + Environment.NewLine
            + "They will stop working wirelessly until they are paired again. If one of them is the mouse you're using now, "
            + "connect it with its USB cable or use the keyboard: Tab moves between buttons and Enter presses them.",
            "Clear receiver", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        _pairing = true;
        UpdateUi();
        var remaining = await Task.Run(receiver.ClearPairings);
        _slots = remaining ?? new();
        _pairing = false;
        UpdateUi();

        if (remaining == null)
            MessageBox.Show(this, "Lost contact with the receiver. Plug it back in and check whether its devices were removed.",
                "Clear receiver", MessageBoxButtons.OK, MessageBoxIcon.Error);
        else if (remaining.Count > 0)
            MessageBox.Show(this, $"The receiver kept {remaining.Count} device(s), so it wasn't fully cleared. Quit G HUB and try again. "
                + "This receiver may not allow removing pairings.", "Clear receiver", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else
            MessageBox.Show(this, "The receiver has no paired devices now and is ready to pair with a mouse.",
                "Clear receiver", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static bool IsGHubRunning()
    {
        var processes = Process.GetProcessesByName("lghub");
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }

    static Label Wrapped(int width = ContentWidth) => new()
    {
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        Margin = new Padding(0, 2, 0, 2),
    };

    static Label Plain(string text)
    {
        var label = Wrapped();
        label.Text = text;
        return label;
    }

    Label Heading(string text, float size) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(Font.FontFamily, size, FontStyle.Bold),
        Margin = new Padding(0, size > 12 ? 0 : 12, 0, 2),
    };

    static Control LightRow(StatusLight light, Label text)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 3, 0, 3) };
        light.Margin = new Padding(0, 0, 10, 0);
        text.Margin = new Padding(0, 2, 0, 0);
        row.Controls.Add(light);
        row.Controls.Add(text);
        return row;
    }
}
