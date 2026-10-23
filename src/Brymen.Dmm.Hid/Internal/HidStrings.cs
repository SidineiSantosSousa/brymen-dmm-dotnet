using System.Text;

namespace Brymen.Dmm.Hid;

internal static class HidStrings
{
    /// <summary>
    /// Normaliza uma string de descritor USB/HID: corta no 1º NUL, remove caracteres de controle e troca
    /// surrogates órfãos por U+FFFD (o serial do BC-86X é lixo não-ASCII e precisa sobreviver a JSON e logs).
    /// Devolve <see langword="null"/> para texto vazio.
    /// </summary>
    public static string? Clean(ReadOnlySpan<char> raw)
    {
        int nul = raw.IndexOf('\0');
        if (nul >= 0)
            raw = raw[..nul];

        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (char.IsHighSurrogate(c) && i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]))
            {
                sb.Append(c).Append(raw[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                sb.Append('�');
            }
            else if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }

        string text = sb.ToString().Trim();
        return text.Length == 0 ? null : text;
    }
}
