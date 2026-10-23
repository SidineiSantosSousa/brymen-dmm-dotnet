namespace Brymen.Dmm.Hid;

/// <summary>Tradução de errno (Linux, asm-generic: x64 e arm64) em exceções do transporte hidraw.</summary>
internal static class HidrawErrors
{
    public const int EPERM = 1;
    public const int ENOENT = 2;
    public const int EINTR = 4;
    public const int EIO = 5;
    public const int ENXIO = 6;
    public const int EAGAIN = 11;
    public const int EACCES = 13;
    public const int EBUSY = 16;
    public const int ENODEV = 19;
    public const int ENOTDIR = 20;
    public const int EISDIR = 21;
    public const int EINVAL = 22;
    public const int ENOTTY = 25;
    public const int EPIPE = 32;
    public const int ESHUTDOWN = 108;
    public const int ETIMEDOUT = 110;

    public const string UdevRulePath = "deploy/linux/99-brymen-bu86x.rules";

    public static Exception ForOpen(int errno, string path) => errno switch
    {
        EACCES => new HidException(
            $"Sem permissão para abrir {path} ({Name(errno)}). Instale a regra udev {UdevRulePath} em /etc/udev/rules.d/, " +
            "recarregue as regras (sudo udevadm control --reload-rules && sudo udevadm trigger), reconecte o cabo " +
            "e confira se o usuário do serviço pertence ao grupo definido na regra.",
            nativeError: errno),
        // EPERM no open() vem do controlador de dispositivos do cgroup, e não das permissões do nó: no serviço systemd
        // (DevicePolicy=closed), o DeviceAllow=char-hidraw só vale se o hidraw já estava em /proc/devices na partida.
        EPERM => new HidException(
            $"Acesso a {path} negado ({Name(errno)}). No serviço systemd, o DeviceAllow=char-hidraw só libera o hidraw " +
            "se ele já estava em /proc/devices quando o serviço iniciou: carregue o módulo hid no boot " +
            "(/etc/modules-load.d/brymen-dmm.conf, instalado pelo install.sh) e reinicie o serviço " +
            $"(sudo systemctl restart brymen-dmm). Fora do systemd, confira a regra udev {UdevRulePath} e o grupo do usuário.",
            nativeError: errno),
        ENOENT or ENODEV or ENXIO => new HidException($"Dispositivo {path} não encontrado ou desconectado ({Name(errno)}).", nativeError: errno),
        EBUSY => new HidException($"Dispositivo {path} ocupado por outro processo ({Name(errno)}).", nativeError: errno),
        _ => new HidException($"Falha ao abrir {path} ({Name(errno)}).", nativeError: errno),
    };

    public static Exception ForIo(int errno, string operation, string path) => errno switch
    {
        ENODEV or EIO or ENXIO or EPIPE or ESHUTDOWN => Disconnected(path, errno),
        ETIMEDOUT or EAGAIN => new TimeoutException($"Tempo esgotado em {operation} de {path} ({Name(errno)})."),
        _ => new HidException($"Falha em {operation} de {path} ({Name(errno)}).", nativeError: errno),
    };

    public static HidException Disconnected(string path, int? errno = null) =>
        new(errno is int e ? $"Dispositivo {path} desconectado ({Name(e)})." : $"Dispositivo {path} desconectado.", nativeError: errno);

    public static string Name(int errno) => errno switch
    {
        EPERM => "EPERM",
        ENOENT => "ENOENT",
        EINTR => "EINTR",
        EIO => "EIO",
        ENXIO => "ENXIO",
        EAGAIN => "EAGAIN",
        EACCES => "EACCES",
        EBUSY => "EBUSY",
        ENODEV => "ENODEV",
        ENOTDIR => "ENOTDIR",
        EISDIR => "EISDIR",
        EINVAL => "EINVAL",
        ENOTTY => "ENOTTY",
        EPIPE => "EPIPE",
        ESHUTDOWN => "ESHUTDOWN",
        ETIMEDOUT => "ETIMEDOUT",
        _ => FormattableString.Invariant($"errno {errno}"),
    };
}
