using System.Runtime.CompilerServices;

namespace Brymen.Dmm;

/// <summary>
/// <c>await</c> que sempre continua numa thread do pool, sem o <see cref="SynchronizationContext"/> nem o
/// <see cref="TaskScheduler"/> de quem chama. As operações do <see cref="BrymenMeter"/> começam por ele: um
/// <see cref="Hid.IHidDevice"/> que bloqueia ou conclui de forma síncrona não prende a thread de quem chamou (a de uma
/// interface gráfica, por exemplo), e o método volta para ela já com uma tarefa em andamento.
/// </summary>
internal readonly struct ThreadPoolHop : ICriticalNotifyCompletion
{
    public bool IsCompleted => false;

    public ThreadPoolHop GetAwaiter() => this;

    public void GetResult()
    {
    }

    public void OnCompleted(Action continuation) => ThreadPool.QueueUserWorkItem(static c => c(), continuation, preferLocal: false);

    public void UnsafeOnCompleted(Action continuation) => ThreadPool.UnsafeQueueUserWorkItem(static c => c(), continuation, preferLocal: false);
}
