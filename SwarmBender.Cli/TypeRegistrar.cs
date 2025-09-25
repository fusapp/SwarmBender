using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace SwarmBender.Cli;


public sealed class SwarmBenderTypeRegistrar : ITypeRegistrar
{
    private readonly IServiceCollection _services;

    public SwarmBenderTypeRegistrar(IServiceCollection services)
    {
        _services = services;
    }

    public ITypeResolver Build()
        => new TypeResolver(_services.BuildServiceProvider());

    public void Register(Type service, Type implementation)
        => _services.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object instance)
        => _services.AddSingleton(service, instance);

    public void RegisterLazy(Type service, Func<object> factory)
        => _services.AddSingleton(service, _ => factory());
}

public sealed class TypeResolver : ITypeResolver, IDisposable
{
    private readonly ServiceProvider _provider;

    public TypeResolver(ServiceProvider provider)
    {
        _provider = provider;
    }

    // Nullability uyarısını gidermek için Spectre imzasıyla birebir:
    public object? Resolve(Type? type)
        => type is null ? null : _provider.GetService(type);

    public void Dispose()
        => _provider.Dispose();
}