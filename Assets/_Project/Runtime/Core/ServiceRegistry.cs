using System;
using System.Collections.Generic;

namespace WaveByWave.Core
{
    public interface IServiceResolver
    {
        T Resolve<T>() where T : class;
        bool TryResolve<T>(out T service) where T : class;
    }

    public interface IServiceConsumer
    {
        void Inject(IServiceResolver services);
    }

    public sealed class ServiceRegistry : IServiceResolver, IDisposable
    {
        private readonly Dictionary<Type, object> _services = new();
        private readonly List<IDisposable> _disposables = new();

        public void Register<T>(T service) where T : class
        {
            if (service == null)
                throw new ArgumentNullException(nameof(service));

            _services[typeof(T)] = service;
            if (service is IDisposable disposable && !_disposables.Contains(disposable))
                _disposables.Add(disposable);
        }

        public T Resolve<T>() where T : class
        {
            if (TryResolve<T>(out var service))
                return service;

            throw new InvalidOperationException($"Service {typeof(T).Name} is not registered.");
        }

        public bool TryResolve<T>(out T service) where T : class
        {
            if (_services.TryGetValue(typeof(T), out var value))
            {
                service = (T)value;
                return true;
            }

            service = null;
            return false;
        }

        public void Dispose()
        {
            for (var i = _disposables.Count - 1; i >= 0; i--)
                _disposables[i].Dispose();

            _disposables.Clear();
            _services.Clear();
        }
    }
}
