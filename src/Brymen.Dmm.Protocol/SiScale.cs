namespace Brymen.Dmm.Protocol;

/// <summary>
/// Aritmética decimal exata para valores e prefixos. O cálculo é feito em <see cref="decimal"/> e só no fim
/// convertido para <see cref="double"/>, para que 0.287 × 10⁻³ dê 0.000287, e não 0.00028700000000000004.
/// </summary>
internal static class SiScale
{
    /// <summary>Expoente decimal do prefixo ("", "n", "u", "m", "k" ou "M").</summary>
    public static int PrefixExponent(string prefix) => prefix switch
    {
        "n" => -9,
        "u" => -6,
        "m" => -3,
        "k" => 3,
        "M" => 6,
        _ => 0,
    };

    /// <summary>Unidades que não recebem conversão SI (§3.6 passo 9): %, dB/dBm e temperatura.</summary>
    public static bool HasSiConversion(string unit) => unit is not ("%" or "dB" or "dBm" or "°C" or "°F");

    /// <summary><paramref name="value"/> × 10^prefixo, ou o próprio valor quando a unidade não tem conversão SI.</summary>
    public static decimal ToSi(decimal value, string prefix, string unit)
        => HasSiConversion(unit) ? value * Pow10(PrefixExponent(prefix)) : value;

    public static decimal Pow10(int exponent) => exponent switch
    {
        -9 => 0.000000001m,
        -6 => 0.000001m,
        -3 => 0.001m,
        0 => 1m,
        3 => 1000m,
        6 => 1000000m,
        _ => throw new ArgumentOutOfRangeException(nameof(exponent), exponent, "Expoente de prefixo não suportado."),
    };

    /// <summary>Conversão para <see cref="double"/> sem zero negativo.</summary>
    public static double ToDouble(decimal value) => value == 0m ? 0d : (double)value;
}
