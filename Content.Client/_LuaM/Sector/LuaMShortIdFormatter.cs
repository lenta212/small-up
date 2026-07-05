using System.Linq;

namespace Content.Client._LuaM.Sector;

public static class LuaMShortIdFormatter
{
    public static string BuildShortId(string id)
    {
        var letters = new string(id.Where(char.IsLetter).Take(2).ToArray()).ToUpperInvariant();
        if (letters.Length < 2)
            letters = (letters + "XX")[..2];

        var digits = new string(id.Where(char.IsDigit).Take(5).ToArray());
        if (digits.Length < 4)
            digits = string.Concat(digits, "00000")[..5];

        return $"{letters}-{digits}";
    }
}
