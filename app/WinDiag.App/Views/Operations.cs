using System.Windows;

namespace WinDiag.App.Views;

/// <summary>
/// Estado ocupado único do aplicativo: só uma operação que altera o sistema (ferramenta, otimização, desinstalação,
/// restauração, atualização) roda por vez. A janela principal consulta este estado ao fechar.
/// </summary>
internal static class Operations
{
    /// <summary>Trava das mudanças de estado (a UI é de uma thread só, mas a espera de ociosidade pode vir de outra).</summary>
    private static readonly object Gate = new();
    /// <summary>Nome da operação em curso (null = livre).</summary>
    private static string? _name;
    /// <summary>Cancelamento da operação em curso, se ela for cancelável.</summary>
    private static CancellationTokenSource? _cts;
    /// <summary>Concluída quando a operação termina; usada para esperar antes de fechar o app.</summary>
    private static TaskCompletionSource _done = Completed();

    /// <summary>Cria uma fonte de conclusão já concluída (estado livre).</summary>
    private static TaskCompletionSource Completed()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    /// <summary>Nome da operação em curso, ou null.</summary>
    public static string? Current
    {
        get { lock (Gate) return _name; }
    }

    /// <summary>true se há uma operação em curso.</summary>
    public static bool IsBusy => Current is not null;

    /// <summary>Tenta iniciar uma operação; devolve o bilhete (descarte ao terminar) ou null se já houver outra em curso.</summary>
    public static Ticket? TryBegin(string name, CancellationTokenSource? cts = null)
    {
        lock (Gate)
        {
            if (_name is not null) return null;
            _name = name;
            _cts = cts;
            _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Ticket(_done);
        }
    }

    /// <summary>Pede o cancelamento da operação em curso (se for cancelável).</summary>
    public static void Cancel()
    {
        CancellationTokenSource? c;
        lock (Gate) c = _cts;
        try { c?.Cancel(); } catch (ObjectDisposedException) { /* já terminou */ }
    }

    /// <summary>Espera a operação em curso terminar (ou o limite de <paramref name="timeout"/>).</summary>
    public static async Task WaitIdleAsync(TimeSpan timeout)
    {
        Task t;
        lock (Gate) t = _done.Task;
        await Task.WhenAny(t, Task.Delay(timeout));
    }

    /// <summary>Avisa qual operação está rodando, em vez de iniciar outra.</summary>
    public static void WarnBusy(Window? owner)
    {
        string text = WinDiag.Core.Loc.T("Já há uma operação em andamento: ", "An operation is already in progress: ") + (Current ?? "") +
                      WinDiag.Core.Loc.T(". Aguarde terminar antes de iniciar outra.", ". Wait for it to finish before starting another.");
        if (owner is { IsLoaded: true }) MessageBox.Show(owner, text, "WinDiag", MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(text, "WinDiag", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Bilhete de uma operação em curso; descartar (várias vezes é seguro) libera o estado ocupado.</summary>
    public sealed class Ticket : IDisposable
    {
        /// <summary>Conclusão a sinalizar ao descartar.</summary>
        private readonly TaskCompletionSource _tcs;
        /// <summary>1 depois do primeiro descarte.</summary>
        private int _disposed;

        /// <summary>Cria o bilhete ligado à conclusão da operação.</summary>
        internal Ticket(TaskCompletionSource tcs) => _tcs = tcs;

        /// <summary>Libera o estado ocupado (idempotente).</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Gate)
            {
                if (ReferenceEquals(_done, _tcs))
                {
                    _name = null;
                    _cts = null;
                }
            }
            _tcs.TrySetResult();
        }
    }
}
