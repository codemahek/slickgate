namespace Slickgate.Admin.Infrastructure;

public interface IReactiveViewModel : IDisposable
{
    Task ActivateAsync();
    Task DeactivateAsync();
    bool IsActive { get; }
    Task Initialization { get; }
}
