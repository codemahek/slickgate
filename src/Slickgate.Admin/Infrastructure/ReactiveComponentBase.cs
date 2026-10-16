using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Slickgate.Admin.Infrastructure;

/// <summary>
/// Base class for pages: creates a transient ViewModel, awaits its initial load
/// (so Blazor re-renders when data arrives) and disposes it on navigation away.
/// </summary>
public abstract class ReactiveComponentBase<TViewModel> : ComponentBase, IAsyncDisposable
    where TViewModel : class, IReactiveViewModel
{
    [Inject]
    protected IServiceProvider ServiceProvider { get; set; } = default!;

    protected TViewModel? ViewModel { get; set; }

    protected abstract TViewModel CreateViewModel(IServiceProvider serviceProvider);

    protected override async Task OnInitializedAsync()
    {
        ViewModel = CreateViewModel(ServiceProvider);
        await ViewModel.ActivateAsync();

        // Wait for the ViewModel's initial load so Blazor calls StateHasChanged on completion.
        if (ViewModel.Initialization != null)
        {
            await ViewModel.Initialization;
        }
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (ViewModel != null)
        {
            await ViewModel.DeactivateAsync();
            ViewModel.Dispose();
        }
    }
}
