using System.Globalization;

namespace Brymen.Dmm.Hid;

/// <summary>Falha injetada numa resposta do <see cref="SimulatedBm52xMeter"/> (testes de recuperação).</summary>
public enum SimulatedFault
{
    /// <summary>Sem falha: o medidor responde normalmente.</summary>
    None,

    /// <summary>O comando se perde: nenhuma resposta, e o medidor não o processa (o ponteiro da memória não anda).</summary>
    Drop,

    /// <summary>O medidor processa o comando (o ponteiro da memória anda), mas nenhum relatório chega.</summary>
    DropAfterProcessing,

    /// <summary>
    /// A resposta chega completa, mas corrompida: o checksum, num bloco de memória, ou um byte de dados, num quadro de
    /// tempo real.
    /// </summary>
    CorruptChecksum,

    /// <summary>O medidor processa o comando, mas só os 2 primeiros relatórios da resposta chegam.</summary>
    Truncate,

    /// <summary>Quadro de tempo real completo, com o Model ID (bytes 20–23) zerado, que o decodificador recusa.</summary>
    CorruptModel,
}

/// <summary>
/// BM52x simulado atrás do BC-86X, para usar como responder de <see cref="SimulatedHidDevice"/>. Reproduz o
/// comportamento observado no BM525s real (docs/protocol/bm52x-usb-protocol.md §2.3, §2.7 e §4.1):
/// <list type="bullet">
/// <item><c>52 66</c>: 3 relatórios com o próximo quadro de tempo real da lista (em rodízio);</item>
/// <item><c>52 88</c>: ponteiro P ← 0 e devolve o bloco P..P+23; <c>52 89</c>: próximo bloco, sem resposta depois do fim;
/// <c>52 8A</c>: P ← P − 24 e relê o bloco. Cada bloco são 4 relatórios: 24 bytes, checksum de 16 bits LE e zeros;</item>
/// <item>com <see cref="CurrentAfterHeadWindow"/>, um <c>8A</c> logo depois do <c>88</c> avança como um <c>89</c>,
/// como no medidor real (§2.7);</item>
/// <item>qualquer outro comando, ou o medidor desligado: nenhuma resposta.</item>
/// </list>
/// Falhas de comunicação são injetadas por comando com <see cref="FaultInjector"/>. É seguro para uso concorrente.
/// </summary>
public sealed class SimulatedBm52xMeter
{
    private const byte Family = 0x52;
    private const byte RealTime = 0x66;
    private const byte MemoryHead = 0x88;
    private const byte MemoryNext = 0x89;
    private const byte MemoryCurrent = 0x8A;
    private const int BlockLength = 24;
    private const int MemoryReportCount = 4;
    private const int RealTimeReportCount = 3;
    private const int RealTimeFrameLength = RealTimeReportCount * Bu86xLink.FrameReportLength;
    private const int MemoryFrameLength = MemoryReportCount * Bu86xLink.FrameReportLength;
    private const int ReportLength = Bu86xLink.ReportDataLength;

    private const string MemoryResource = "Brymen.Dmm.Hid.Simulation.memory-dump-1.txt";
    private const string RealTimeResource = "Brymen.Dmm.Hid.Simulation.rtd-acv.txt";

    private static readonly Lazy<byte[]> DefaultMemory = new(() => LoadEmbeddedCapture(MemoryResource, ParseMemoryCapture));
    private static readonly Lazy<IReadOnlyList<byte[]>> CapturedFrames = new(() => LoadEmbeddedCapture(RealTimeResource, ParseRealTimeCapture));

    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private byte[][] _realTimeFrames;
    private byte[] _memory;
    private int _realTimeIndex;
    private int _pointer;
    private bool _poweredOn = true;
    private long _commandCount;
    private long? _headTimestamp;
    private bool _lastWasHead;
    private TimeSpan _currentAfterHeadWindow;
    private Func<byte, long, SimulatedFault>? _faultInjector;

    /// <param name="realTimeFrames">Quadros de 27 bytes no formato das capturas (com os report IDs); padrão: <see cref="DefaultRealTimeFrames"/>.</param>
    /// <param name="memoryImage">Conteúdo da memória do datalogger a partir do ponteiro 0; padrão: <see cref="LoadDefaultMemoryImage"/>.</param>
    /// <param name="timeProvider">Relógio de <see cref="CurrentAfterHeadWindow"/>; padrão: <see cref="TimeProvider.System"/>.</param>
    public SimulatedBm52xMeter(IEnumerable<byte[]>? realTimeFrames = null, byte[]? memoryImage = null, TimeProvider? timeProvider = null)
    {
        _realTimeFrames = ValidateFrames(realTimeFrames ?? DefaultRealTimeFrames);
        _memory = (memoryImage ?? DefaultMemory.Value).ToArray();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Quadros RTD reais do BM525s em ACV + Hz, pontas abertas (0,287 V, 0,156 V e 0,090 V; vetores
    /// <c>rtd1</c>–<c>rtd3</c> de docs/protocol/test-vectors.json).
    /// </summary>
    public static IReadOnlyList<byte[]> DefaultRealTimeFrames { get; } =
    [
        Convert.FromHexString("00001000BFCBEFA80000BFAFAFAF00012006005252525211000000"),
        Convert.FromHexString("00001000BFA06D6F0000BFAFAFAF00012006005252525210000000"),
        Convert.FromHexString("00001000BFAFEDAF0000BFAFAFAF00012006005252525210000000"),
    ];

    /// <summary>
    /// Os 14 quadros RTD da captura docs/protocol/captures/rtd-acv.txt, na ordem da captura: os de
    /// <see cref="DefaultRealTimeFrames"/> e as 12 leituras de 2026-10-22 (BM525s em ACV + Hz, pontas abertas).
    /// </summary>
    public static IReadOnlyList<byte[]> CapturedRealTimeFrames => CapturedFrames.Value;

    /// <summary>
    /// Janela de <see cref="CurrentAfterHeadWindow"/> que reproduz o medidor real: os 200 ms que o software original
    /// espera depois do <c>88</c>. Um <c>8A</c> antes disso é tratado como inseguro.
    /// </summary>
    public static TimeSpan RealMeterCurrentAfterHeadWindow { get; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Medidor com as capturas reais completas: os quadros de <see cref="CapturedRealTimeFrames"/>, a memória de
    /// <see cref="LoadDefaultMemoryImage"/> e o <c>8A</c> que avança como um <c>89</c> logo depois do <c>88</c>
    /// (<see cref="CurrentAfterHeadWindow"/> = <see cref="RealMeterCurrentAfterHeadWindow"/>).
    /// </summary>
    /// <param name="timeProvider">Relógio da janela do <c>8A</c>; padrão: <see cref="TimeProvider.System"/>.</param>
    public static SimulatedBm52xMeter FromCaptures(TimeProvider? timeProvider = null) =>
        new(CapturedRealTimeFrames, null, timeProvider) { CurrentAfterHeadWindow = RealMeterCurrentAfterHeadWindow };

    /// <summary>
    /// Memória do dump real (docs/protocol/captures/memory-dump-1.txt): 95 blocos (2280 bytes) com 1 sessão
    /// ACV + Hz de 375 amostras; o cabeçalho declara TotalBytes = 2272.
    /// </summary>
    public static byte[] LoadDefaultMemoryImage() => DefaultMemory.Value.ToArray();

    /// <summary>Se o medidor responde. Desligado, nenhum comando tem resposta (o BC-86X só repassa a luz do medidor).</summary>
    public bool IsPoweredOn
    {
        get { lock (_lock) return _poweredOn; }
        set { lock (_lock) _poweredOn = value; }
    }

    /// <summary>Ponteiro atual da memória (bytes a partir do início).</summary>
    public int MemoryPointer
    {
        get { lock (_lock) return _pointer; }
    }

    /// <summary>Blocos de 24 bytes da memória atual (o último pode ser parcial).</summary>
    public int MemoryBlockCount
    {
        get { lock (_lock) return (_memory.Length + BlockLength - 1) / BlockLength; }
    }

    /// <summary>Comandos da família <c>52h</c> recebidos, inclusive os perdidos por uma falha injetada.</summary>
    public long CommandCount => Interlocked.Read(ref _commandCount);

    /// <summary>
    /// Janela depois do <c>88</c> em que um <c>8A</c> avança o ponteiro como um <c>89</c>, como no medidor real: em 9 de
    /// 16 tentativas, um <c>8A</c> cerca de 15 ms depois da resposta do <c>88</c> devolveu o bloco seguinte (o original
    /// espera <c>Sleep(200)</c> depois do <c>88</c>). Vale só se o comando anterior foi o <c>88</c>.
    /// <see cref="TimeSpan.Zero"/> (padrão) desliga.
    /// </summary>
    public TimeSpan CurrentAfterHeadWindow
    {
        get { lock (_lock) return _currentAfterHeadWindow; }
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            lock (_lock)
                _currentAfterHeadWindow = value;
        }
    }

    /// <summary>
    /// Decide a falha de cada comando: recebe o código do comando e o seu número sequencial (a partir de 1, como em
    /// <see cref="CommandCount"/>); <see langword="null"/> (padrão) = sem falhas. É chamado fora do lock do medidor, na
    /// thread que escreveu o comando, e pode bloquear para simular um medidor que demora a responder.
    /// </summary>
    public Func<byte, long, SimulatedFault>? FaultInjector
    {
        get => Volatile.Read(ref _faultInjector);
        set => Volatile.Write(ref _faultInjector, value);
    }

    /// <summary>Substitui os quadros de tempo real devolvidos pelo <c>66</c> (em rodízio, a partir do 1º).</summary>
    public void SetRealTimeFrames(params IEnumerable<byte[]> frames)
    {
        var validated = ValidateFrames(frames);
        lock (_lock)
        {
            _realTimeFrames = validated;
            _realTimeIndex = 0;
        }
    }

    /// <summary>Substitui a memória do datalogger e zera o ponteiro.</summary>
    public void SetMemoryImage(ReadOnlySpan<byte> image)
    {
        var copy = image.ToArray();
        lock (_lock)
        {
            _memory = copy;
            _pointer = 0;
        }
    }

    /// <summary>Responder para <see cref="SimulatedHidDevice"/>: recebe o relatório de saída <c>[00 | 00 52 cmd]</c>.</summary>
    /// <returns>Os relatórios de entrada de 8 bytes da resposta, ou <see langword="null"/> para nenhuma resposta.</returns>
    public IReadOnlyList<byte[]>? Respond(byte reportId, ReadOnlyMemory<byte> outputData)
    {
        var data = outputData.Span;
        if (reportId != 0x00 || data.Length < 3 || data[0] != 0x00 || data[1] != Family)
            return null;

        byte command = data[2];
        long sequence = Interlocked.Increment(ref _commandCount);
        // Fora do lock: o injetor pode bloquear (medidor lento) ou consultar o próprio medidor.
        SimulatedFault fault = FaultInjector?.Invoke(command, sequence) ?? SimulatedFault.None;
        if (fault == SimulatedFault.Drop)
            return null;

        byte[][]? reports;
        lock (_lock)
        {
            if (!_poweredOn)
                return null;
            reports = command switch
            {
                RealTime => NextRealTime(),
                MemoryHead => ReadBlockAt(0),
                // §2.7: depois do último bloco o medidor não responde, e o ponteiro não anda.
                MemoryNext => ReadBlockAt(_pointer),
                MemoryCurrent when IsInsideHeadWindow() => ReadBlockAt(_pointer),
                MemoryCurrent => ReadBlockAt(Math.Max(_pointer - BlockLength, 0)),
                _ => null,
            };
            _lastWasHead = command == MemoryHead;
            if (_lastWasHead)
                _headTimestamp = _time.GetTimestamp();
        }
        if (reports is null || fault == SimulatedFault.DropAfterProcessing)
            return null;

        switch (fault)
        {
            case SimulatedFault.CorruptChecksum when reports.Length == MemoryReportCount:
                reports[3][0] ^= 0x5A;
                break;
            case SimulatedFault.CorruptChecksum:
                reports[0][1] ^= 0x5A;
                break;
            case SimulatedFault.CorruptModel when command == RealTime:
                // Bytes 20–23 do quadro (numeração com os report IDs) = os 4 primeiros bytes de dados do 3º relatório.
                reports[2].AsSpan(0, 4).Clear();
                break;
            case SimulatedFault.Truncate:
                return reports[..Math.Min(2, reports.Length)];
        }
        return reports;
    }

    /// <summary>Divide um quadro no formato das capturas (relatórios de 9 bytes com report ID) nos relatórios de 8 bytes de dados.</summary>
    public static IReadOnlyList<byte[]> SplitFrame(ReadOnlySpan<byte> frame) => Split(frame);

    /// <summary>Monta os 4 relatórios de um bloco de memória: 3 × 8 bytes de dados e o checksum (soma dos 24 bytes, LE).</summary>
    public static IReadOnlyList<byte[]> CreateMemoryReports(ReadOnlySpan<byte> block) => CreateBlockReports(block);

    private static byte[][] Split(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0 || frame.Length % Bu86xLink.FrameReportLength != 0)
            throw new ArgumentException("O quadro deve ter um múltiplo de 9 bytes (report ID + 8 bytes de dados).", nameof(frame));
        var reports = new byte[frame.Length / Bu86xLink.FrameReportLength][];
        for (int i = 0; i < reports.Length; i++)
            reports[i] = frame.Slice(i * Bu86xLink.FrameReportLength + 1, ReportLength).ToArray();
        return reports;
    }

    private static byte[][] CreateBlockReports(ReadOnlySpan<byte> block)
    {
        if (block.Length > BlockLength)
            throw new ArgumentException("Um bloco de memória tem no máximo 24 bytes.", nameof(block));
        Span<byte> data = stackalloc byte[BlockLength];
        data.Clear();
        block.CopyTo(data);

        int checksum = 0;
        foreach (byte b in data)
            checksum += b;
        var reports = new byte[MemoryReportCount][];
        for (int i = 0; i < 3; i++)
            reports[i] = data.Slice(i * ReportLength, ReportLength).ToArray();
        reports[3] = new byte[ReportLength];
        reports[3][0] = (byte)checksum;
        reports[3][1] = (byte)(checksum >> 8);
        return reports;
    }

    // Chamado sob _lock.
    private byte[][] NextRealTime()
    {
        var frame = _realTimeFrames[_realTimeIndex];
        _realTimeIndex = (_realTimeIndex + 1) % _realTimeFrames.Length;
        return Split(frame);
    }

    // Chamado sob _lock: o comando anterior foi o 88 e a janela ainda não passou.
    private bool IsInsideHeadWindow()
        => _lastWasHead && _headTimestamp is { } head && _currentAfterHeadWindow > TimeSpan.Zero
            && _time.GetElapsedTime(head) < _currentAfterHeadWindow;

    // Chamado sob _lock. Ponteiro além do fim: o medidor não responde (exceto o 88 de uma memória vazia, que devolve zeros).
    private byte[][]? ReadBlockAt(int pointer)
    {
        if (pointer >= _memory.Length && !(pointer == 0 && _memory.Length == 0))
            return null;
        int length = Math.Min(BlockLength, _memory.Length - pointer);
        _pointer = pointer + BlockLength;
        return CreateBlockReports(_memory.AsSpan(pointer, length));
    }

    private static byte[][] ValidateFrames(IEnumerable<byte[]> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        byte[][] copy = [.. frames.Select(f => f.ToArray())];
        if (copy.Length == 0)
            throw new ArgumentException("Informe ao menos um quadro de tempo real.", nameof(frames));
        if (copy.Any(f => f.Length != RealTimeFrameLength))
            throw new ArgumentException("Cada quadro de tempo real deve ter 27 bytes (3 relatórios de 9 bytes).", nameof(frames));
        return copy;
    }

    private static T LoadEmbeddedCapture<T>(string name, Func<TextReader, T> parse)
    {
        using var stream = typeof(SimulatedBm52xMeter).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Recurso {name} ausente do assembly Brymen.Dmm.Hid.");
        using var reader = new StreamReader(stream);
        return parse(reader);
    }

    /// <summary>Linhas <c>"&lt;cmd&gt; &lt;hex&gt;"</c> de uma captura; linhas vazias ou iniciadas por <c>#</c> são comentários.</summary>
    /// <remarks>O comando pode vir com a família (<c>5288</c>) ou sem ela (<c>88</c>): vale o último byte.</remarks>
    internal static IEnumerable<(byte Command, byte[] Frame)> ParseCaptureLines(TextReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length < 2)
                throw new FormatException($"Linha de captura inválida: '{line}'.");
            byte command = byte.Parse(parts[0].AsSpan(parts[0].Length - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            yield return (command, Convert.FromHexString(parts[1]));
        }
    }

    /// <summary>Os quadros <c>66</c> de uma captura de tempo real, na ordem.</summary>
    internal static IReadOnlyList<byte[]> ParseRealTimeCapture(TextReader reader)
        => [.. ParseCaptureLines(reader).Where(l => l.Command == RealTime && l.Frame.Length == RealTimeFrameLength).Select(l => l.Frame)];

    /// <summary>
    /// Imagem da memória (os 24 bytes úteis de cada bloco, na ordem do ponteiro) de uma captura de <c>88</c>/<c>89</c>/<c>8A</c>,
    /// reproduzindo o ponteiro na ordem da captura: um <c>8A</c> relê o bloco anterior e um <c>88</c> volta ao início.
    /// </summary>
    internal static byte[] ParseMemoryCapture(TextReader reader)
    {
        var blocks = new List<byte[]>();
        int index = -1;
        foreach (var (command, frame) in ParseCaptureLines(reader))
        {
            int? next = command switch
            {
                MemoryHead => 0,
                MemoryNext => index + 1,
                MemoryCurrent => Math.Max(index, 0),
                _ => null,
            };
            if (next is null || frame.Length != MemoryFrameLength)
                continue;
            index = next.Value;
            if (index == blocks.Count)
                blocks.Add(frame);
            else if (index > blocks.Count)
                throw new FormatException("Captura de memória sem o bloco inicial (88).");
        }

        var image = new byte[blocks.Count * BlockLength];
        for (int i = 0; i < blocks.Count; i++)
        {
            for (int r = 0; r < 3; r++)
                blocks[i].AsSpan(r * Bu86xLink.FrameReportLength + 1, ReportLength).CopyTo(image.AsSpan(i * BlockLength + r * ReportLength));
        }
        return image;
    }
}
