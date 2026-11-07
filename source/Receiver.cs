using System.ComponentModel;
using System.Text;

sealed record SlotInfo(byte Slot, ushort Wpid, string Name);

enum PairOutcome { Success, PairedNoAnswer, WrongMouse, Failed, Cancelled }

sealed record PairResult(PairOutcome Outcome, string Message);

/// <summary>Talks HID++ 1.0 to a LIGHTSPEED receiver. All calls block, so run them off the UI thread.</summary>
sealed class ReceiverClient
{
    const byte RegNotifications = 0x00, RegPairing = 0xB2, RegInfo = 0xB5;
    const byte SubSet = 0x80, SubGet = 0x81, SubGetLong = 0x83, SubError = 0x8F;
    const byte SwId = 0x0A;

    public const int LockSeconds = 60;

    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightspeedPair", "pairing-log.txt");

    readonly HidppPort _port;
    readonly object _gate = new(); // one conversation with the receiver at a time

    public ushort Pid { get; }
    public bool IsAlive => _port.IsAlive;

    public ReceiverClient(HidppPort port, ushort pid)
    {
        _port = port;
        Pid = pid;
    }

    public List<SlotInfo> ReadSlots()
    {
        lock (_gate)
        {
            var slots = new List<SlotInfo>();
            for (byte n = 1; n <= 6; n++)
            {
                var pairing = GetInfo((byte)(0x20 + n - 1));
                if (pairing == null) continue;
                var name = GetInfo((byte)(0x40 + n - 1));
                var text = name != null && name[5] is > 0 and <= 14 ? Encoding.ASCII.GetString(name, 6, name[5]) : "";
                slots.Add(new SlotInfo(n, (ushort)((pairing[7] << 8) | pairing[8]), text));
            }
            return slots;
        }
    }

    /// <summary>Removes every pairing, then reads the receiver back. Returns what is still paired,
    /// or null if contact with the receiver was lost.</summary>
    public List<SlotInfo>? ClearPairings()
    {
        lock (_gate)
        {
            try
            {
                var before = ReadSlots();
                Log($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} v{Application.ProductVersion} clear receiver {Pid:X4}, slots: {string.Join(", ", before.Select(s => $"{s.Slot}={s.Wpid:X4} {s.Name}"))}");
                foreach (var slot in before)
                {
                    var ack = SetRegister(RegPairing, 0x03, slot.Slot, 0x00);
                    Log($"unpair slot {slot.Slot} ack {(ack == null ? "none" : Convert.ToHexString(ack))}");
                    Thread.Sleep(300);
                }
                var after = ReadSlots();
                Log($"slots after clear: {string.Join(", ", after.Select(s => $"{s.Slot}={s.Wpid:X4} {s.Name}"))}");
                return after;
            }
            catch (Win32Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Opens the pairing lock, waits for the receiver to close it, then checks what actually paired.</summary>
    public PairResult Pair(MouseModel mouse, Action onLockOpen, CancellationToken cancel)
    {
        lock (_gate)
        {
            try
            {
                return PairLocked(mouse, onLockOpen, cancel);
            }
            catch (Win32Exception)
            {
                return new PairResult(PairOutcome.Failed, "Lost contact with the receiver. Was it unplugged? Plug it back in and try again.");
            }
        }
    }

    PairResult PairLocked(MouseModel mouse, Action onLockOpen, CancellationToken cancel)
    {
        Log($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} v{Application.ProductVersion} mouse '{mouse.Name}' receiver {Pid:X4}");

        var before = ReadSlots();
        var oldFlags = GetRegister(RegNotifications);
        if (oldFlags == null)
            return new PairResult(PairOutcome.Failed, "The receiver did not answer. Unplug it, plug it back in and try again.");

        // Wireless (0x000100) and software-present (0x000800) notifications are needed to see pairing events.
        if (SetRegister(RegNotifications, oldFlags[0], (byte)(oldFlags[1] | 0x09), oldFlags[2]) == null)
            return new PairResult(PairOutcome.Failed, "The receiver refused to start pairing. Nothing was changed.");

        byte? lockError = null;
        var reconnected = false;
        try
        {
            _port.Drain();
            var ack = SetRegister(RegPairing, 0x01, 0x00, LockSeconds);
            Log($"open lock ack {(ack == null ? "none" : Convert.ToHexString(ack))}, old flags {Convert.ToHexString(oldFlags)}");
            if (ack == null)
                return new PairResult(PairOutcome.Failed, "The receiver refused to open for pairing.");

            onLockOpen();

            var deadline = Environment.TickCount64 + (LockSeconds + 5) * 1000L;
            while (lockError == null && Environment.TickCount64 < deadline)
            {
                if (cancel.IsCancellationRequested)
                    return new PairResult(PairOutcome.Cancelled, "Pairing cancelled.");

                var frame = _port.Next(250);
                if (frame == null || frame.Length < 7 || frame[0] != 0x10) continue; // long frames are other apps' traffic
                Log($"rx {Convert.ToHexString(frame)}");

                // 0x4A reports the lock state: byte 3 bit 0 is open, byte 4 is an error code.
                if (frame[1] == 0xFF && frame[2] == 0x4A && (frame[3] & 0x01) == 0)
                    lockError = frame[4];

                // An already-paired mouse that is switched on just reconnects (0x41, bit 0x40 clear
                // means the link is up, bytes 5-6 are its ID) and the lock stays open. Seen on a real
                // G502 X and C547. Stop here rather than waiting for a timeout that says nothing happened.
                if (frame[2] == 0x41 && (frame[4] & 0x40) == 0 && mouse.Wpid == (ushort)((frame[6] << 8) | frame[5])
                    && before.Any(s => s.Slot == frame[1] && s.Wpid == mouse.Wpid))
                {
                    reconnected = true;
                    break;
                }
            }
        }
        finally
        {
            SetRegister(RegPairing, 0x02, 0x00, 0x00);
            SetRegister(RegNotifications, oldFlags[0], oldFlags[1], oldFlags[2]);
        }

        Thread.Sleep(800);
        return Evaluate(mouse, before, ReadSlots(), lockError, reconnected);
    }

    // The slot table decides the result. On a real G502 X and C547 a successful pairing closed the
    // lock with no error and sent no 0x41 connection message, so 0x41 can't be relied on.
    PairResult Evaluate(MouseModel mouse, List<SlotInfo> before, List<SlotInfo> after, byte? lockError, bool reconnected)
    {
        Log($"lock error {(lockError?.ToString("X2") ?? "none")}, reconnected {reconnected}, slots after: {string.Join(", ", after.Select(s => $"{s.Slot}={s.Wpid:X4} {s.Name}"))}");

        var added = after.Where(s => !before.Any(b => b.Slot == s.Slot && b.Wpid == s.Wpid)).ToList();
        var match = mouse.Wpid != null ? after.FirstOrDefault(s => s.Wpid == mouse.Wpid) : added.FirstOrDefault();

        if (match != null && reconnected)
        {
            var version = PingWithRetries(match.Slot);
            Log($"ping slot {match.Slot}: {version ?? "no answer"}");
            return version != null
                ? new PairResult(PairOutcome.Success, $"This mouse was already paired in slot {match.Slot}, so it simply reconnected and is answering over wireless. You're done!")
                : new PairResult(PairOutcome.PairedNoAnswer, $"This mouse is already paired in slot {match.Slot} and reconnected, but it isn't answering yet. Move it around to check.");
        }

        if (match != null && added.Count == 0 && lockError != 0)
            return new PairResult(PairOutcome.Failed, $"This mouse is already paired in slot {match.Slot}, but it didn't connect when it was switched on. Make sure it's charged and close to the receiver.");

        if (match != null && (lockError == 0 || added.Contains(match)))
        {
            var version = PingWithRetries(match.Slot);
            Log($"ping slot {match.Slot}: {version ?? "no answer"}");
            return version != null
                ? new PairResult(PairOutcome.Success, $"Your mouse is paired in slot {match.Slot} and answers over wireless. You're done!")
                : new PairResult(PairOutcome.PairedNoAnswer, $"The receiver saved the mouse in slot {match.Slot}, but the mouse isn't answering yet. Move it around. If the cursor doesn't follow, try pairing again.");
        }

        if (added.Count > 0)
            return new PairResult(PairOutcome.WrongMouse,
                $"The receiver paired with a different device: {(added[0].Name.Length > 0 ? added[0].Name : "unknown")} (ID {added[0].Wpid:X4}). Check the mouse selected in the list.");

        return new PairResult(PairOutcome.Failed, lockError switch
        {
            0x01 or null => "The receiver never saw the mouse. Make sure it's charged and close to the receiver, quit G HUB, then try again. Switch the mouse on only after the app tells you to, then click the left button 3 times. If it still fails, this receiver may not work with this mouse.",
            0x02 => "The receiver says this mouse isn't supported. This mouse and receiver are probably not compatible.",
            0x03 => "The receiver has no free pairing slots. Close this window, press Clear receiver, then try again.",
            0x06 => "Pairing started but timed out. Keep the mouse close to the receiver and try again.",
            0x00 => "The receiver stopped pairing without adding a device. Quit G HUB and try again.",
            _ => $"The receiver reported pairing error 0x{lockError:X2}.",
        });
    }

    string? PingWithRetries(byte slot)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var version = Ping(slot);
            if (version != null) return version;
            Thread.Sleep(1000);
        }
        return null;
    }

    // HID++ 2.0 ping through the receiver. It only succeeds if the mouse is actually linked.
    string? Ping(byte slot)
    {
        var reply = _port.Request(new byte[] { 0x10, slot, 0x00, 0x10 | SwId, 0, 0, 0x5A }, f =>
            f.Length >= 7 && f[1] == slot &&
            ((f[2] == 0x00 && f[3] == (0x10 | SwId)) || (f[2] == SubError && f[3] == 0x00)), 2000);
        return reply == null || reply[2] == SubError ? null : $"{reply[4]}.{reply[5]}";
    }

    byte[]? GetRegister(byte reg)
    {
        var reply = _port.Request(new byte[] { 0x10, 0xFF, SubGet, reg, 0, 0, 0 }, f => IsRegisterReply(f, SubGet, reg));
        return reply == null || reply[2] == SubError ? null : reply[4..7];
    }

    byte[]? SetRegister(byte reg, byte p0, byte p1, byte p2)
    {
        var reply = _port.Request(new byte[] { 0x10, 0xFF, SubSet, reg, p0, p1, p2 }, f => IsRegisterReply(f, SubSet, reg));
        return reply == null || reply[2] == SubError ? null : reply[4..7];
    }

    byte[]? GetInfo(byte sub)
    {
        var reply = _port.Request(new byte[] { 0x10, 0xFF, SubGetLong, RegInfo, sub, 0, 0 }, f =>
            (f.Length >= 20 && f[1] == 0xFF && f[2] == SubGetLong && f[3] == RegInfo && f[4] == sub)
            || (f.Length >= 7 && f[1] == 0xFF && f[2] == SubError && f[3] == SubGetLong && f[4] == RegInfo));
        return reply == null || reply[2] == SubError ? null : reply;
    }

    static bool IsRegisterReply(byte[] f, byte sub, byte reg) => f.Length >= 7 && f[1] == 0xFF &&
        ((f[2] == sub && f[3] == reg) || (f[2] == SubError && f[3] == sub && f[4] == reg));

    static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (IOException) { } // logging must never break pairing
        catch (UnauthorizedAccessException) { }
    }
}
