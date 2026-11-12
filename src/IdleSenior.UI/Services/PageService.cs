using System;
using Wpf.Ui.Abstractions;

namespace IdleSenior.UI.Services
{
    public class PageService : INavigationViewPageProvider
    {
        private readonly IServiceProvider _serviceProvider;

        public PageService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public T? GetPage<T>() where T : class
        {
            return _serviceProvider.GetService(typeof(T)) as T;
        }

        public object? GetPage(Type pageType)
        {
            return _serviceProvider.GetService(pageType);
        }
    }
}
