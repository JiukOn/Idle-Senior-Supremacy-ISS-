using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Abstractions;

namespace IdleSenior.UI.Services
{
    public class NavigationPageProvider : INavigationViewPageProvider
    {
        private readonly IServiceProvider _serviceProvider;

        public NavigationPageProvider(IServiceProvider serviceProvider)
            => _serviceProvider = serviceProvider;

        public object? GetPage(Type pageType)
            => _serviceProvider.GetService(pageType);
    }
}
