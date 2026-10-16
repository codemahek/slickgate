using System.Reactive.Disposables;

namespace Slickgate.Admin.Infrastructure;

/// <summary>
/// Extension methods for IDisposable to support the ReactiveUI 26 patterns.
/// Replaces the removed DisposeWith extension from System.Reactive.
/// </summary>
public static class DisposableExtensions
{
    public static T DisposeWith<T>(this T disposable, CompositeDisposable composite)
        where T : IDisposable
    {
        composite.Add(disposable);
        return disposable;
    }
}
