namespace Enlace.Windows;

/// <summary>Correspondencia entre keysyms X11 (lo que viaja por RFB) y teclas virtuales de Windows.</summary>
public static class KeyMap
{
    public const uint XK_Shift_L = 0xFFE1, XK_Shift_R = 0xFFE2, XK_Control_L = 0xFFE3, XK_Control_R = 0xFFE4,
        XK_Alt_L = 0xFFE9, XK_Alt_R = 0xFFEA, XK_Meta_L = 0xFFE7, XK_Meta_R = 0xFFE8,
        XK_Super_L = 0xFFEB, XK_Super_R = 0xFFEC, XK_ISO_Level3_Shift = 0xFE03, XK_Mode_switch = 0xFF7E,
        XK_Tab = 0xFF09, XK_Escape = 0xFF1B, XK_Menu = 0xFF67;

    /// <summary>keysym -> (vk, tecla extendida). Teclas que no producen carácter.</summary>
    public static readonly IReadOnlyDictionary<uint, (byte Vk, bool Ext)> SpecialToVk = new Dictionary<uint, (byte, bool)>
    {
        [0xFF08] = (0x08, false), // BackSpace
        [0xFF09] = (0x09, false), // Tab
        [0xFE20] = (0x09, false), // ISO_Left_Tab
        [0xFF0B] = (0x0C, false), // Clear
        [0xFF0D] = (0x0D, false), // Return
        [0xFF13] = (0x13, false), // Pause
        [0xFF14] = (0x91, false), // Scroll_Lock
        [0xFF15] = (0x2C, true),  // Sys_Req
        [0xFF1B] = (0x1B, false), // Escape
        [0xFFFF] = (0x2E, true),  // Delete
        [0xFF50] = (0x24, true),  // Home
        [0xFF51] = (0x25, true),  // Left
        [0xFF52] = (0x26, true),  // Up
        [0xFF53] = (0x27, true),  // Right
        [0xFF54] = (0x28, true),  // Down
        [0xFF55] = (0x21, true),  // Prior
        [0xFF56] = (0x22, true),  // Next
        [0xFF57] = (0x23, true),  // End
        [0xFF61] = (0x2C, true),  // Print
        [0xFF63] = (0x2D, true),  // Insert
        [0xFF67] = (0x5D, true),  // Menu
        [0xFF7F] = (0x90, true),  // Num_Lock
        [0xFF8D] = (0x0D, true),  // KP_Enter
        [0xFF95] = (0x24, false), [0xFF96] = (0x25, false), [0xFF97] = (0x26, false), [0xFF98] = (0x27, false),
        [0xFF99] = (0x28, false), [0xFF9A] = (0x21, false), [0xFF9B] = (0x22, false), [0xFF9C] = (0x23, false),
        [0xFF9D] = (0x0C, false), [0xFF9E] = (0x2D, false), [0xFF9F] = (0x2E, false),
        [0xFFAA] = (0x6A, false), // KP_Multiply
        [0xFFAB] = (0x6B, false), // KP_Add
        [0xFFAC] = (0x6C, false), // KP_Separator
        [0xFFAD] = (0x6D, false), // KP_Subtract
        [0xFFAE] = (0x6E, false), // KP_Decimal
        [0xFFAF] = (0x6F, true),  // KP_Divide
        [0xFFB0] = (0x60, false), [0xFFB1] = (0x61, false), [0xFFB2] = (0x62, false), [0xFFB3] = (0x63, false),
        [0xFFB4] = (0x64, false), [0xFFB5] = (0x65, false), [0xFFB6] = (0x66, false), [0xFFB7] = (0x67, false),
        [0xFFB8] = (0x68, false), [0xFFB9] = (0x69, false),
        [XK_Shift_L] = (0xA0, false), [XK_Shift_R] = (0xA1, false),
        [XK_Control_L] = (0xA2, false), [XK_Control_R] = (0xA3, true),
        [0xFFE5] = (0x14, false), // Caps_Lock
        [XK_Meta_L] = (0xA4, false), [XK_Meta_R] = (0xA5, true),
        [XK_Alt_L] = (0xA4, false), [XK_Alt_R] = (0xA5, true),
        [XK_Super_L] = (0x5B, true), [XK_Super_R] = (0x5C, true),
        [XK_ISO_Level3_Shift] = (0xA5, true), [XK_Mode_switch] = (0xA5, true),
    }.Concat(Enumerable.Range(0, 24).Select(i => new KeyValuePair<uint, (byte, bool)>((uint)(0xFFBE + i), ((byte)(0x70 + i), false))))
     .ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>Carácter que representa un keysym imprimible, o null.</summary>
    public static string? KeysymToText(uint keysym)
    {
        if (keysym is >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF) return ((char)keysym).ToString();
        if ((keysym & 0xFF000000) == 0x01000000)
        {
            uint cp = keysym & 0x00FFFFFF;
            if (cp <= 0x10FFFF && !(cp is >= 0xD800 and <= 0xDFFF)) return char.ConvertFromUtf32((int)cp);
            return null;
        }
        return keysym switch
        {
            0x20AC => "€",
            0xFE50 => "`", 0xFE51 => "´", 0xFE52 => "^", 0xFE53 => "~", 0xFE57 => "¨", // teclas muertas
            _ => null,
        };
    }

    public static uint CharToKeysym(char c)
    {
        if (c is >= (char)0x20 and <= (char)0x7E or >= (char)0xA0 and <= (char)0xFF) return c;
        if (c == '€') return 0x20AC;
        return 0x01000000u | c;
    }

    /// <summary>Tecla virtual (con datos del mensaje) -> keysym, sólo para teclas sin carácter.</summary>
    public static uint? VkToSpecialKeysym(int vk, bool extended, int scanCode)
    {
        switch (vk)
        {
            case 0x08: return 0xFF08;
            case 0x09: return 0xFF09;
            case 0x0C: return 0xFF0B;
            case 0x0D: return extended ? 0xFF8Du : 0xFF0D;
            case 0x13: return 0xFF13;
            case 0x14: return 0xFFE5;
            case 0x1B: return 0xFF1B;
            case 0x21: return 0xFF55;
            case 0x22: return 0xFF56;
            case 0x23: return 0xFF57;
            case 0x24: return 0xFF50;
            case 0x25: return 0xFF51;
            case 0x26: return 0xFF52;
            case 0x27: return 0xFF53;
            case 0x28: return 0xFF54;
            case 0x2C: return 0xFF61;
            case 0x2D: return 0xFF63;
            case 0x2E: return 0xFFFF;
            case 0x5B: return XK_Super_L;
            case 0x5C: return XK_Super_R;
            case 0x5D: return XK_Menu;
            case 0x90: return 0xFF7F;
            case 0x91: return 0xFF14;
            case 0x10: return scanCode == 0x36 ? XK_Shift_R : XK_Shift_L;
            case 0xA0: return XK_Shift_L;
            case 0xA1: return XK_Shift_R;
            case 0x11: return extended ? XK_Control_R : XK_Control_L;
            case 0xA2: return XK_Control_L;
            case 0xA3: return XK_Control_R;
            case 0x12: return extended ? XK_Alt_R : XK_Alt_L;
            case 0xA4: return XK_Alt_L;
            case 0xA5: return XK_Alt_R;
        }
        if (vk is >= 0x70 and <= 0x87) return (uint)(0xFFBE + (vk - 0x70));
        return null;
    }
}
