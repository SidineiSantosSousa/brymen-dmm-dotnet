namespace Brymen.Dmm.Protocol.Tests;

/// <summary>
/// Monta imagens sintéticas da memória do datalogger (Table 2): cabeçalho, sessões com DLE/STX, PS/NS,
/// Bfunction/Bselect/Bstatus, comprimento, dados e DLE/ETX.
/// </summary>
internal sealed class LoggerImageBuilder
{
    private readonly List<(byte Function, byte Select, byte Status, byte[] Data)> _sessions = [];

    public byte ModelId { get; init; } = 0x01;

    /// <summary>PS gravado na 1ª sessão ("don't care"); o dump real traz A0_0418.</summary>
    public (byte A2, byte A1, byte A0) FirstPrevious { get; init; } = (0xA0, 0x04, 0x18);

    /// <summary>Byte alto usado nos endereços PS/NS gravados (A0/A2, ou os aliases A1/A3).</summary>
    public bool UseReadAliases { get; init; }

    /// <summary>Head pointer de cada sessão, preenchido por <see cref="Build"/>.</summary>
    public List<int> HeadPointers { get; } = [];

    public LoggerImageBuilder Add(byte function, byte select, byte status, params byte[][] samples)
    {
        _sessions.Add((function, select, status, [.. samples.SelectMany(s => s)]));
        return this;
    }

    public LoggerImageBuilder AddRaw(byte function, byte select, byte status, byte[] data)
    {
        _sessions.Add((function, select, status, data));
        return this;
    }

    /// <summary>Um registro de 3 bytes: X_0 (sinal, OL, LB, faixa) e count little-endian.</summary>
    public static byte[] Value(int count, int range = 0, bool negative = false, bool overload = false, bool lowBattery = false)
    {
        int x0 = range & 0x0F;
        if (negative)
            x0 |= 0x80;
        if (overload)
            x0 |= 0x40;
        if (lowBattery)
            x0 |= 0x20;
        return [(byte)x0, (byte)count, (byte)(count >> 8)];
    }

    public static byte[] Dual(byte[] main, byte[] secondary) => [.. main, .. secondary];

    public byte[] Build()
    {
        HeadPointers.Clear();
        int pointer = Bm52xMemory.FirstSessionPointer;
        foreach (var session in _sessions)
        {
            HeadPointers.Add(pointer);
            pointer += 16 + session.Data.Length;
        }
        int total = pointer;

        var image = new List<byte>(total)
        {
            ModelId,
            (byte)total, (byte)(total >> 8), (byte)(total >> 16),
            (byte)_sessions.Count, (byte)(_sessions.Count >> 8),
        };

        for (int i = 0; i < _sessions.Count; i++)
        {
            var (function, select, status, data) = _sessions[i];
            int head = HeadPointers[i];
            int next = head + 16 + data.Length;
            var previous = i == 0 ? FirstPrevious : Address(HeadPointers[i - 1]);
            var nextAddress = Address(next);

            image.AddRange([0xEE, 0xA0]);
            image.AddRange([previous.A0, previous.A1, previous.A2]);
            image.AddRange([nextAddress.A0, nextAddress.A1, nextAddress.A2]);
            image.AddRange([function, select, status]);
            image.AddRange([(byte)data.Length, (byte)(data.Length >> 8), (byte)(data.Length >> 16)]);
            image.AddRange(data);
            image.AddRange([0xEE, 0xC0]);
        }

        Assert.Equal(total, image.Count);
        return [.. image];
    }

    private (byte A2, byte A1, byte A0) Address(int pointer)
    {
        int linear = pointer + Bm52xMemory.BaseAddress;
        int a2 = 0xA0 + 2 * (linear >> 16) + (UseReadAliases ? 1 : 0);
        return ((byte)a2, (byte)(linear >> 8), (byte)linear);
    }
}
