using System.Diagnostics.CodeAnalysis;

namespace DictationApp.Core.Settings;

/// <summary>
/// A set of up to three keys that must all be held to trigger dictation. Keys are stored as Windows
/// virtual-key codes with left/right variants normalised to the generic code (e.g. 0xA2/0xA3 → 0x11).
/// </summary>
public sealed class HotkeyChord : IEquatable<HotkeyChord>
{
    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkAlt = 0x12;
    public const int VkCapsLock = 0x14;
    public const int VkEscape = 0x1B;
    public const int VkSpace = 0x20;
    public const int VkLeft = 0x25;
    public const int VkUp = 0x26;
    public const int VkRight = 0x27;
    public const int VkDown = 0x28;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;
    public const int VkLShift = 0xA0;
    public const int VkRShift = 0xA1;
    public const int VkLControl = 0xA2;
    public const int VkRControl = 0xA3;
    public const int VkLAlt = 0xA4;
    public const int VkRAlt = 0xA5;
    public const int MaxKeys = 3;

    private static readonly Dictionary<string, int> NameToVk = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = VkControl,
        ["Control"] = VkControl,
        ["Shift"] = VkShift,
        ["Alt"] = VkAlt,
        ["Win"] = VkLWin,
        ["Windows"] = VkLWin,
        ["CapsLock"] = VkCapsLock,
        ["Space"] = VkSpace,
        ["Tab"] = 0x09,
        ["Pause"] = 0x13,
        ["ScrollLock"] = 0x91,
        ["Insert"] = 0x2D,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
    };

    private static readonly Dictionary<int, string> VkToName = BuildReverse();

    public HotkeyChord(IEnumerable<int> virtualKeys)
    {
        var keys = virtualKeys.Select(Normalise).Distinct().OrderBy(SortRank).ToArray();
        if (keys.Length == 0)
        {
            throw new ArgumentException("A chord needs at least one key.", nameof(virtualKeys));
        }

        if (keys.Length > MaxKeys)
        {
            throw new ArgumentException($"A chord may have at most {MaxKeys} keys.", nameof(virtualKeys));
        }

        VirtualKeys = keys;
    }

    public static HotkeyChord Default { get; } = new([VkControl, VkLWin]);

    /// <summary>Ctrl+Alt, offered by the first-run wizard for users who dislike Win-key suppression.</summary>
    public static HotkeyChord Alternative { get; } = new([VkControl, VkAlt]);

    public IReadOnlyList<int> VirtualKeys { get; }

    public bool ContainsWin => VirtualKeys.Contains(VkLWin);

    public bool IsModifierOnly => VirtualKeys.All(IsModifier);

    public static bool IsModifier(int vk) => Normalise(vk) is VkControl or VkShift or VkAlt or VkLWin;

    /// <summary>Maps left/right variants of modifiers to their generic code.</summary>
    public static int Normalise(int vk) => vk switch
    {
        VkLControl or VkRControl => VkControl,
        VkLShift or VkRShift => VkShift,
        VkLAlt or VkRAlt => VkAlt,
        VkRWin => VkLWin,
        _ => vk,
    };

    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyChord? chord)
    {
        chord = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var keys = new List<int>();
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (NameToVk.TryGetValue(part, out var vk))
            {
                keys.Add(vk);
            }
            else if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
            {
                keys.Add(char.ToUpperInvariant(part[0]));
            }
            else if (part.Length is 2 or 3 && part[0] is 'F' or 'f' && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24)
            {
                keys.Add(0x70 + f - 1);
            }
            else if (part.StartsWith("VK_0x", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(part[5..], System.Globalization.NumberStyles.HexNumber, null, out var raw))
            {
                keys.Add(raw);
            }
            else
            {
                return false;
            }
        }

        try
        {
            chord = new HotkeyChord(keys);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static HotkeyChord Parse(string text) =>
        TryParse(text, out var chord) ? chord : throw new FormatException($"Invalid hotkey chord '{text}'.");

    public static string KeyName(int vk)
    {
        vk = Normalise(vk);
        if (VkToName.TryGetValue(vk, out var name))
        {
            return name;
        }

        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            return ((char)vk).ToString();
        }

        if (vk is >= 0x70 and <= 0x87)
        {
            return "F" + (vk - 0x70 + 1);
        }

        return $"VK_0x{vk:X2}";
    }

    public override string ToString() => string.Join("+", VirtualKeys.Select(KeyName));

    public bool Equals(HotkeyChord? other) => other is not null && VirtualKeys.SequenceEqual(other.VirtualKeys);

    public override bool Equals(object? obj) => Equals(obj as HotkeyChord);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var k in VirtualKeys)
        {
            hash.Add(k);
        }

        return hash.ToHashCode();
    }

    private static int SortRank(int vk) => vk switch
    {
        VkControl => 0,
        VkShift => 1,
        VkAlt => 2,
        VkLWin => 3,
        _ => 10 + vk,
    };

    private static Dictionary<int, string> BuildReverse()
    {
        var map = new Dictionary<int, string>();
        foreach (var (name, vk) in NameToVk)
        {
            map.TryAdd(vk, name);
        }

        map[VkControl] = "Ctrl";
        map[VkLWin] = "Win";
        return map;
    }
}
