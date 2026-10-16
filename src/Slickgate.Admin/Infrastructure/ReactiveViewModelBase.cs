using ReactiveUI;
using ReactiveUI.SourceGenerators;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace Slickgate.Admin.Infrastructure;

/// <summary>
/// Base class for all reactive ViewModels in the admin UI.
/// Provides common reactive patterns and lifecycle management.
/// </summary>
public abstract partial class ReactiveViewModelBase : ReactiveObject, IReactiveViewModel
{
    private bool _disposed;
    protected CompositeDisposable Subscriptions { get; } = new();

    /// <summary>Observable that emits when the ViewModel becomes active.</summary>
    protected IObservable<bool> WhenActivated => this.WhenAnyValue(x => x.IsActive);

    /// <summary>Observable that emits when the ViewModel becomes inactive.</summary>
    protected IObservable<bool> WhenDeactivated => WhenActivated.Select(isActive => !isActive);

    [Reactive]
    public partial bool IsActive { get; private set; }

    /// <summary>
    /// Task that completes when initial data loading finishes. Set by derived
    /// ViewModels in their constructor; the component awaits it in OnInitializedAsync
    /// so Blazor calls StateHasChanged on completion.
    /// </summary>
    public Task Initialization { get; protected set; } = Task.CompletedTask;

    public virtual Task ActivateAsync()
    {
        IsActive = true;
        return Task.CompletedTask;
    }

    public virtual Task DeactivateAsync()
    {
        IsActive = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Setup reactive bindings and subscriptions. Called during activation.
    /// Override to set up reactive pipelines using WhenAnyValue, ToProperty, etc.
    /// </summary>
    protected virtual void SetupReactiveBindings()
    {
    }

    /// <summary>
    /// Setup reactive subscriptions. Called during activation after SetupReactiveBindings.
    /// </summary>
    protected virtual void SetupReactiveSubscriptions()
    {
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            Subscriptions.Dispose();
        }

        _disposed = true;
    }

    ~ReactiveViewModelBase()
    {
        Dispose(false);
    }
}
