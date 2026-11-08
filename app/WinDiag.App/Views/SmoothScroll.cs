using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinDiag.App.Views;

/// <summary>
/// Rolagem suave da roda do mouse em um <see cref="ScrollViewer"/>.
/// Cada clique da roda só acumula o destino; um único laço por quadro (<see cref="CompositionTarget.Rendering"/>)
/// aproxima o deslocamento do destino com desaceleração exponencial e se desliga ao chegar.
/// Assim não há animações reiniciadas a cada clique (que causavam trancos) e nenhum custo quando parado.
/// </summary>
public static class SmoothScroll
{
    /// <summary>Distância por "clique" da roda (120 unidades de delta), em pixels independentes de DPI.</summary>
    private const double Step = 110;

    /// <summary>Fração do caminho restante percorrida por segundo (meia-vida curta, ~70 ms).</summary>
    private const double Speed = 10;

    private sealed class State
    {
        public double Target;
        public bool Running;
        public TimeSpan Last;
    }

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("SmoothScrollState", typeof(State), typeof(SmoothScroll));

    /// <summary>Liga a rolagem suave ao ScrollViewer informado.</summary>
    public static void Attach(ScrollViewer sv)
    {
        ArgumentNullException.ThrowIfNull(sv);
        sv.SetValue(StateProperty, new State { Target = double.NaN });
        sv.PreviewMouseWheel += OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || sv.ScrollableHeight <= 0) return;
        // Deixa passar a roda sobre listas suspensas abertas e sobre ScrollViewers internos que ainda podem rolar.
        if (e.OriginalSource is DependencyObject o && FindParent<System.Windows.Controls.Primitives.Popup>(o) != null) return;
        if (sv.GetValue(StateProperty) is not State st) return;
        e.Handled = true;

        double from = double.IsNaN(st.Target) || !st.Running ? sv.VerticalOffset : st.Target;
        st.Target = Math.Clamp(from - e.Delta / 120.0 * Step, 0, sv.ScrollableHeight);
        if (st.Running) return;

        st.Running = true;
        st.Last = TimeSpan.Zero;
        EventHandler? tick = null;
        tick = (_, args) =>
        {
            var now = ((RenderingEventArgs)args).RenderingTime;
            double dt = st.Last == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - st.Last).TotalSeconds, 0, 0.05);
            st.Last = now;

            double target = Math.Clamp(st.Target, 0, sv.ScrollableHeight);
            double cur = sv.VerticalOffset;
            double diff = target - cur;
            if (Math.Abs(diff) < 0.5 || !sv.IsVisible)
            {
                sv.ScrollToVerticalOffset(target);
                st.Running = false;
                st.Target = double.NaN;
                CompositionTarget.Rendering -= tick;
                return;
            }
            double k = 1 - Math.Exp(-Speed * dt);
            sv.ScrollToVerticalOffset(cur + diff * k);
        };
        CompositionTarget.Rendering += tick;
    }

    private static T? FindParent<T>(DependencyObject o) where T : DependencyObject
    {
        for (var p = o; p != null; p = ParentOf(p))
            if (p is T t) return t;
        return null;
    }

    /// <summary>
    /// Pai de um elemento na árvore. Trechos de texto (<see cref="System.Windows.Documents.Run"/>, Hyperlink...)
    /// são <see cref="ContentElement"/> e não fazem parte da árvore visual: para eles sobe-se pela árvore lógica.
    /// </summary>
    internal static DependencyObject? ParentOf(DependencyObject o) =>
        o is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(o)
            : (o as FrameworkContentElement)?.Parent ?? LogicalTreeHelper.GetParent(o);
}
