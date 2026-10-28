// Tradutor pt-BR para a captura guiada do dmm-cli (dmm-tools): roda o dmm-cli com a entrada e a saída redirecionadas,
// troca as mensagens e as instruções das etapas por português e repassa o que se digita. O YAML gerado não muda
// (continua em inglês, para o mantenedor). Uso:
//   dotnet run captura-ptbr.cs -- <caminho do dmm-cli.exe> --device bm82x capture -o saida.yaml
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length < 1)
{
    Console.Error.WriteLine("Uso: dotnet run captura-ptbr.cs -- <dmm-cli.exe> <argumentos do dmm-cli>");
    return 2;
}

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var psi = new ProcessStartInfo(args[0])
{
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    StandardOutputEncoding = Encoding.UTF8,
    StandardErrorEncoding = Encoding.UTF8,
    StandardInputEncoding = new UTF8Encoding(false),
    UseShellExecute = false,
};
foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
// O BM52x (ET-2615A = BM525s) tem etapas que o BM82x não tem, e outras com instruções diferentes.
bool isBm52x = args.Skip(1).Any(a => a.Equals("bm52x", StringComparison.OrdinalIgnoreCase));
psi.Environment["NO_COLOR"] = "1";

using var proc = Process.Start(psi)!;
var consoleLock = new Lock();

var outPump = Task.Run(() => Pump(proc.StandardOutput, Console.Out));
var errPump = Task.Run(() => Pump(proc.StandardError, Console.Error));

// O que se digita vai direto para o dmm-cli, uma linha por vez.
var inPump = new Thread(() =>
{
    try
    {
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            if (proc.HasExited) break;
            proc.StandardInput.WriteLine(line);
            proc.StandardInput.Flush();
        }
        proc.StandardInput.Close();
    }
    catch (IOException) { }
    catch (InvalidOperationException) { }
}) { IsBackground = true };
inPump.Start();

proc.WaitForExit();
await Task.WhenAll(outPump, errPump);
return proc.ExitCode;

// Lê caractere a caractere: as linhas inteiras saem traduzidas; um pedaço sem quebra de linha (um prompt esperando
// resposta) sai traduzido depois de 150 ms sem nada novo.
async Task Pump(StreamReader reader, TextWriter writer)
{
    var buf = new StringBuilder();
    var chunk = new char[1];
    Task<int>? pending = null;
    while (true)
    {
        pending ??= reader.ReadAsync(chunk, 0, 1);
        var done = await Task.WhenAny(pending, Task.Delay(150));
        if (done != pending)
        {
            if (buf.Length > 0) { Write(writer, Translate(buf.ToString())); buf.Clear(); }
            continue;
        }
        int n = await pending;
        pending = null;
        if (n == 0) break;
        char c = chunk[0];
        if (c == '\n')
        {
            Write(writer, Translate(buf.ToString().TrimEnd('\r')) + Environment.NewLine);
            buf.Clear();
        }
        else buf.Append(c);
    }
    if (buf.Length > 0) Write(writer, Translate(buf.ToString()));
}

void Write(TextWriter writer, string text)
{
    lock (consoleLock) { writer.Write(text); writer.Flush(); }
}

string Translate(string line)
{
    var m = StepLine.Match(line);
    if (m.Success && (isBm52x && Bm52xSteps.TryGetValue(m.Groups[2].Value, out var pt)
                      || Steps.TryGetValue(m.Groups[2].Value, out pt)))
        line = $"{m.Groups[1].Value}[{m.Groups[2].Value}] {pt}{m.Groups[3].Value}";

    var cov = Covered.Match(line);
    if (cov.Success)
        line = $"Cobertas {cov.Groups[1].Value} de {cov.Groups[2].Value} etapas não verificadas para {cov.Groups[3].Value}.";

    foreach (var (en, ptText) in Phrases) line = line.Replace(en, ptText);
    return line;
}

partial class Program
{
    // "[dcv] Leads in ΩV ..." e, nas etapas que o dmm-cli reconhece sozinho, um "(gate)" no fim.
    static readonly Regex StepLine = new(@"^(\s*)\[([a-z0-9_]+)\] (?:.*?)(\s*\(gate\))?$");
    static readonly Regex Covered = new(@"^Covered (\d+) of (\d+) unverified steps for (.*)\.$");

    // Ordem importa: as frases mais longas antes das que são parte delas.
    static readonly (string En, string Pt)[] Phrases =
    [
        ("━━━ Measurement Modes ━━━", "━━━ Modos de medição ━━━"),
        ("each step captures itself once the meter settles — Enter=capture now, s=skip one, q=skip to end and save",
            "cada etapa captura sozinha quando a leitura estabiliza — Enter=capturar agora, s=pular esta, q=ir ao fim e salvar"),
        ("━━━ Part 4: Additional Captures (optional) ━━━", "━━━ Parte 4: capturas extras (opcional) ━━━"),
        ("Set the meter to any mode/state not covered above.", "Coloque o medidor num modo/estado que não apareceu acima."),
        ("Numbers of anything you don't have (Enter = have everything):", "Números do que você NÃO tem (Enter = tenho tudo):"),
        ("Numbers of the readings that did NOT match the meter's screen (Enter = all correct):",
            "Números das leituras que NÃO bateram com o visor (Enter = todas certas):"),
        ("You will need:", "Você vai precisar de:"),
        ("shorted test leads", "pontas de prova em curto"),
        ("battery or other DC source", "pilha ou outra fonte DC"),
        ("K-type thermocouple", "termopar tipo K"),
        ("live mains wire nearby", "fio da rede energizado por perto"),
        ("(steps:", "(etapas:"),
        ("any key=capture, s=skip, q=finish:", "Enter=capturar, s=pular, q=terminar:"),
        ("Status: experimental — the protocol is reverse-engineered, not confirmed",
            "Status: experimental — protocolo obtido por engenharia reversa, ainda não confirmado"),
        ("meter shows: mode is", "o medidor mostra: modo"),
        ("\", want \"", "\", esperado \""),
        ("We read:", "Lido:"),
        ("Did the meter show this?", "O medidor mostrou isso?"),
        ("(Enter = yes, n = no, r = retake)", "(Enter = sim, n = não, r = refazer)"),
        ("(Enter = yes, n = no)", "(Enter = sim, n = não)"),
        ("Answer n, r, or press Enter.", "Responda n, r ou aperte Enter."),
        ("Answer n, or press Enter.", "Responda n ou aperte Enter."),
        ("What did the meter show?", "O que o medidor mostrou?"),
        ("Press Enter when the meter is ready (s=skip, q=finish)", "Aperte Enter quando o medidor estiver pronto (s=pular, q=terminar)"),
        ("Press Enter when it is ready, or s to skip:", "Aperte Enter quando estiver pronto, ou s para pular:"),
        ("Press Enter once it is off, or s to skip:", "Aperte Enter quando ele estiver desligado, ou s para pular:"),
        ("Turn it back on at DC V.", "Ligue de novo em V DC."),
        ("The reading is taken once the meter settles — Enter takes it now, s skips.",
            "A leitura é feita quando o medidor estabiliza — Enter lê agora, s pula."),
        ("Describe what you set the meter to (or 'q' to finish):", "Descreva em que modo deixou o medidor (ou 'q' para terminar):"),
        ("=== Capture complete! ===", "=== Captura concluída! ==="),
        ("Report saved to:", "Relatório salvo em:"),
        ("Attach the report to", "Anexe o relatório em"),
        ("Output file:", "Arquivo de saída:"),
        ("Not finished — run the same command again and answer r to resume.",
            "Incompleta — rode o mesmo comando de novo e responda r para continuar."),
        ("--steps <ids> runs named steps on their own.", "--steps <ids> roda só as etapas indicadas."),
        ("No response from meter.", "Sem resposta do medidor."),
        ("Checking meter communication...", "Verificando a comunicação com o medidor..."),
        ("(the meter does not report a name)", "(o medidor não informa o nome)"),
        ("Please complete as many steps as possible and share the report.", "Complete o máximo de etapas que puder e compartilhe o relatório."),
        ("No readings arrived, so no file was written", "Nenhuma leitura chegou; nenhum arquivo foi gravado"),
        ("Timed out after", "Tempo esgotado após"),
        ("Device:", "Aparelho:"),
    ];

    // Instruções das etapas do BM82x (ET-2517A = BM827s). "Pule (s)" = só existe no BM829s.
    static readonly Dictionary<string, string> Steps = new()
    {
        ["dcv"] = "Pontas em ΩV e COM: chave em V⎓, SELECT até aparecer ⎓ sem nada no visor pequeno, pontas abertas",
        ["dcv_short"] = "V DC: encoste uma ponta na outra",
        ["dcv_negative"] = "V DC: pontas invertidas numa pilha ou fonte DC (s se não tiver)",
        ["ohm_ol"] = "Chave em Ω, pontas abertas, SELECT se precisar até mostrar Ω sem bip nem nS (aparece 0L)",
        ["ohm_body"] = "Ω: segure uma ponta em cada mão (resistência do corpo, centenas de kΩ)",
        ["ohm_short"] = "Ω: encoste uma ponta na outra",
        ["autocheck"] = "Auto Check — o ET-2517A não tem: pule (s)",
        ["autocheck_dc"] = "Auto Check DC — o ET-2517A não tem: pule (s)",
        ["autocheck_ac"] = "Auto Check AC — o ET-2517A não tem: pule (s)",
        ["autocheck_ohm"] = "Auto Check Ω — o ET-2517A não tem: pule (s)",
        ["acv"] = "Chave em Hz V~, pontas abertas, SELECT até o visor grande mostrar ~ V e o pequeno Hz",
        ["dbm"] = "dBm — o ET-2517A não tem: pule (s)",
        ["hz_acv"] = "Hz V~: SELECT até o grande mostrar Hz e o pequeno V",
        ["dcv_acv"] = "Chave em V⎓, pontas abertas, SELECT até o grande mostrar ⎓ e o pequeno ~ V; depois Enter",
        ["acdcv"] = "AC+DC — o ET-2517A não tem: pule (s)",
        ["ef"] = "V⎓: segure HOLD (EF) 1 s ou mais, longe de fios (aparece E.F.); depois Enter",
        ["ncv"] = "EF: aproxime o topo do medidor de um fio energizado, sem encostar (aparecem traços); Enter. Depois segure HOLD 1 s para sair do EF (s se não tiver fio)",
        ["hold"] = "V⎓: SELECT até sumir o visor pequeno, toque HOLD (aparece H); Enter. Depois toque HOLD de novo",
        ["rel"] = "V DC: toque Δ (aparece Δ); Enter. Depois toque Δ de novo",
        ["manual_range"] = "V DC: toque RANGE (AUTO apaga); Enter",
        ["range_auto"] = "V DC: segure RANGE 1 s ou mais (AUTO volta); Enter",
        ["rec"] = "V DC: toque REC (aparece R e MAX MIN AVG); Enter",
        ["rec_max"] = "REC: toque REC (aparece MAX); Enter",
        ["rec_min"] = "REC: toque REC de novo (aparece MIN); Enter",
        ["rec_max_min"] = "REC: toque REC de novo (aparece MAX-MIN); Enter",
        ["rec_avg"] = "REC: toque REC de novo (aparece AVG); Enter. Depois segure REC 1 s para sair",
        ["crest_max"] = "V DC: toque CREST (aparece C e MAX); Enter",
        ["crest_min"] = "CREST: toque CREST de novo (aparece MIN); Enter",
        ["crest_max_min"] = "CREST: toque CREST de novo (aparece MAX-MIN); Enter. Depois segure CREST 1 s para sair",
        ["dcmv"] = "Chave em mV⎓, pontas abertas, SELECT até aparecer ⎓ sem o visor pequeno; Enter",
        ["dcmv_acmv"] = "mV⎓: SELECT até o grande mostrar ⎓ e o pequeno ~ mV",
        ["acdcmv"] = "AC+DC mV — o ET-2517A não tem: pule (s)",
        ["logic_hz"] = "mV⎓: SELECT até aparecer Hz (frequência lógica)",
        ["duty"] = "mV⎓: SELECT até aparecer D% (ciclo de trabalho)",
        ["acmv"] = "Chave em Hz mV~, pontas abertas, SELECT até o grande mostrar ~ mV e o pequeno Hz",
        ["dbm_mv"] = "dBm mV — o ET-2517A não tem: pule (s)",
        ["hz_acmv"] = "Hz mV~: SELECT até o grande mostrar Hz e o pequeno mV",
        ["cont"] = "Chave em Ω, SELECT até aparecer o símbolo de continuidade (bip); encoste as pontas",
        ["ns"] = "Ω: SELECT até aparecer nS, pontas abertas",
        ["t1_open"] = "Tire as pontas de prova e gire a chave para Temp (T1), sem nada ligado",
        ["t1"] = "Temperatura: ligue o termopar tipo K em ΩV (+) e COM (−)",
        ["temp_unit"] = "Temperatura T1: toque SELECT para trocar °C/°F; Enter. Depois toque SELECT de novo",
        ["t2"] = "T2 — o ET-2517A não tem: pule (s)",
        ["t1_t2"] = "T1 e T2 — o ET-2517A não tem: pule (s)",
        ["t1_minus_t2"] = "T1-T2 — o ET-2517A não tem: pule (s)",
        ["cap"] = "Tire o termopar, pontas de volta em ΩV e COM, abertas; chave em capacitância, SELECT até sumir \"diod\". A leitura pode levar segundos",
        ["diode"] = "Capacitância: SELECT até aparecer \"diod\" no visor pequeno, pontas abertas",
        ["dcua"] = "Ponta vermelha para mA µA, pontas abertas; chave em µA, SELECT até aparecer ⎓ sem o visor pequeno; Enter",
        ["acdcua"] = "µA: SELECT até aparecer ⎓ e ~ juntos (s se nunca aparecer)",
        ["acua"] = "µA: SELECT até o grande mostrar ~ e o pequeno Hz",
        ["dcma"] = "Vermelha ainda em mA µA: chave em A mA, SELECT até aparecer ⎓ sem o visor pequeno; Enter",
        ["acdcma"] = "A mA, vermelha em mA µA: SELECT até aparecer ⎓ e ~ juntos (s se nunca aparecer)",
        ["acma"] = "A mA, vermelha em mA µA: SELECT até o grande mostrar ~ e o pequeno Hz",
        ["dca"] = "Ponta vermelha para A, pontas abertas; chave em A mA, SELECT até aparecer ⎓ sem o visor pequeno",
        ["acdca"] = "A mA, vermelha em A: SELECT até aparecer ⎓ e ~ juntos (s se nunca aparecer)",
        ["aca"] = "A mA, vermelha em A: SELECT até o grande mostrar ~ e o pequeno Hz",
        ["iner"] = "Vermelha ainda em A: chave em V⎓, pontas abertas (o medidor apita e mostra InEr). Depois volte a vermelha para ΩV",
        ["detect"] = "Desligue e ligue o medidor; o programa confere se a detecção automática o encontra",
    };

    // BM52x (ET-2615A = BM525s): o que muda em relação ao BM82x.
    static readonly Dictionary<string, string> Bm52xSteps = new()
    {
        ["autocheck"] = "Chave em Auto Check, pontas abertas (aparece Auto)",
        ["autocheck_dc"] = "Auto Check: pontas numa fonte DC acima de 1,5 V, como uma bateria de 9 V",
        ["autocheck_ac"] = "Auto Check: pontas numa fonte AC baixa acima de 3 V, como a saída de um transformador (s se não tiver)",
        ["autocheck_ohm"] = "Auto Check: segure uma ponta em cada mão",
        ["acdcv"] = "V⎓: SELECT até o grande mostrar ⎓ e ~ juntos",
        ["rec"] = "V DC: toque REC (aparece R e MAX MIN); Enter",
        ["rec_max_min"] = "REC: toque REC de novo (aparece MAX-MIN); Enter. Depois segure REC 1 s para sair",
        ["acdcmv"] = "mV⎓: SELECT até o grande mostrar ⎓ e ~ juntos",
        ["cont"] = "Chave em Ω, SELECT até aparecer o símbolo de continuidade (bip); encoste as pontas",
        ["t1_open"] = "Tire as pontas de prova, chave em T1 T2 e RANGE até aparecer T1 sem nada no visor pequeno",
        ["t2"] = "Temperatura: passe o termopar para mA µA (+) e A (−) e RANGE até aparecer T2",
        ["t1_t2"] = "Temperatura: um segundo termopar em ΩV (+) e COM (−), RANGE até o grande mostrar T1 e o pequeno T2 (s se só tiver um)",
        ["t1_minus_t2"] = "Temperatura, os dois termopares ligados: RANGE até aparecer T1-T2 (s se só tiver um)",
        ["log_left"] = "Vermelha de volta em ΩV, pontas abertas, chave em V⎓. Segure Yes ▲ 1 s: aparece LEFt e os pontos livres; Enter. NÃO aperte ▼ Erase (apaga todos os registros). Se quiser guardar os registros do medidor, pule esta e as próximas (s)",
        ["log_start"] = "Registro: toque Yes ▲ para iniciar uma sessão sem apagar as outras (t0.05, depois Strt, depois a leitura volta); Enter. NÃO aperte ▼ Erase",
        ["log_pause"] = "Registro: toque Yes ▲ para pausar (aparece PAUS); Enter",
        ["log_continue"] = "Registro: toque Yes ▲ de novo para continuar (aparece Cont); Enter",
        ["log_stop"] = "Registro: segure Yes ▲ 1 s para parar (aparece StoP); Enter",
        ["recall"] = "Aperte Yes ▲ e ▼ Erase juntos para o Recall: página P.nnn, depois a última leitura com R e C; Enter. Depois gire a chave para sair do Recall",
    };
}
