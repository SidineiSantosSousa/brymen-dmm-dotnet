using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Brymen.Dmm.Hid;

/// <summary>
/// P/Invokes da libc usados pelo hidraw. As constantes e os errno são os do asm-generic, iguais em x64, arm64 e
/// arm (32 bits); <c>size_t</c>/<c>ssize_t</c>/<c>nfds_t</c> têm o tamanho de um ponteiro (<see cref="nuint"/>/<see cref="nint"/>).
/// O nome "libc" é tratado à parte pelo runtime, também no Native AOT: vira libc.so.6 na glibc e libc.so no musl
/// (Alpine), que o carregador do musl reconhece como ele mesmo, sem precisar do pacote musl-dev.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe partial class LibC
{
    private const string Library = "libc";

    public const int O_RDWR = 0x2;
    public const int O_NOCTTY = 0x100;
    public const int O_NONBLOCK = 0x800;
    public const int O_CLOEXEC = 0x80000;

    public const short POLLIN = 0x1;
    public const short POLLERR = 0x8;
    public const short POLLHUP = 0x10;
    public const short POLLNVAL = 0x20;

    /// <summary><c>struct pollfd</c>: 8 bytes em todas as arquiteturas Linux.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    // open(2) é variádica, mas sem O_CREAT/O_TMPFILE o mode não é lido; nas ABIs do Linux (x64, arm64, arm) os
    // argumentos fixos de uma chamada variádica vão nos mesmos registradores de uma chamada comum.
    [LibraryImport(Library, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int Open(string path, int flags);

    [LibraryImport(Library, EntryPoint = "poll", SetLastError = true)]
    public static partial int Poll(PollFd* fds, nuint nfds, int timeout);

    [LibraryImport(Library, EntryPoint = "read", SetLastError = true)]
    public static partial nint Read(int fd, byte* buffer, nuint count);

    [LibraryImport(Library, EntryPoint = "write", SetLastError = true)]
    public static partial nint Write(int fd, byte* buffer, nuint count);

    // ioctl(2) também é variádica: int ioctl(int, unsigned long, ...) na glibc e int ioctl(int, int, ...) no musl. O
    // request vai como unsigned long; no musl só os 32 bits baixos são lidos, e o kernel o trunca para 32 bits de
    // qualquer forma (o cmd do sys_ioctl é unsigned int). O argumento é o ponteiro para a struct do ioctl.
    [LibraryImport(Library, EntryPoint = "ioctl", SetLastError = true)]
    public static partial int Ioctl(int fd, nuint request, void* argument);

    // Sem SetLastError: o close de um SafeHandle não deve sobrescrever o errno da operação que o liberou.
    [LibraryImport(Library, EntryPoint = "close")]
    public static partial int Close(int fd);
}

/// <summary>Descritor de arquivo da libc; fecha com <c>close(2)</c>.</summary>
[SupportedOSPlatform("linux")]
internal sealed class SafeFileDescriptor : SafeHandleMinusOneIsInvalid
{
    public SafeFileDescriptor(int fd) : base(ownsHandle: true) => SetHandle(fd);

    public int Fd => (int)handle;

    protected override bool ReleaseHandle() => LibC.Close((int)handle) == 0;
}
