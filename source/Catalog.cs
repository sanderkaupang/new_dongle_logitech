/// <summary>A selectable mouse. UsbPid is its ID on a USB cable, Wpid its ID on a receiver. Null means not known.</summary>
sealed record MouseModel(string Name, ushort? UsbPid, ushort? Wpid, bool HasDataCable = true, ushort? TestedReceiver = null)
{
    public override string ToString() => Name;
}

static class Catalog
{
    // Only the mice named in the receiver's product listing:
    // https://www.aliexpress.com/item/1005012759621235.html
    //
    // IDs come from libratbag's device files, Solaar's device tables and libratbag issue reports.
    // An ID is left null when no source states it. Never guess one: add it when confirmed.
    public static readonly MouseModel[] Mice =
    {
        new("G502 X LIGHTSPEED", 0xC098, 0x409F, TestedReceiver: 0xC547),
        new("PRO X SUPERLIGHT", 0xC094, 0x4093),
        new("PRO X SUPERLIGHT 2", 0xC09B, 0x40A9),
        new("PRO X SUPERLIGHT 2 DEX", null, null),
        new("PRO 2 LIGHTSPEED", null, null),
        new("G PRO Wireless", 0xC088, 0x4079),
        new("G309 LIGHTSPEED", null, null),
        new("G705", 0xC096, 0x409D),
    };

    public static readonly ushort[] Receivers = { 0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC54D };
}
