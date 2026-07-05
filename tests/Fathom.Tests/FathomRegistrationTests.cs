using Fathom.Core;
using Fathom.Core.Lookups;
using Fathom.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fathom.Tests;

[TestFixture]
public class FathomRegistrationTests
{
    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.Configure<FathomOptions>(o => o.ConnectionString = "Server=unused;Database=unused");
        return services;
    }

    [Test]
    public void AddCodeListLookup_registers_one_shared_instance_under_both_lookup_interfaces()
    {
        var services = BaseServices();
        services.AddFathom().UseSqlServer().AddCodeListLookup("countries", "Country");

        using var provider = services.BuildServiceProvider();
        var exportLookup = provider.GetServices<IExportLookupProvider>().Single(p => p.Name == "countries");
        var requestLookup = provider.GetServices<IRequestLookupProvider>().Single(p => p.Name == "countries");

        Assert.That(exportLookup, Is.SameAs(requestLookup), "both directions must share one cache");
    }

    [Test]
    public void AddCodeListLookup_from_bound_options_registers_under_the_configured_name()
    {
        var services = BaseServices();
        services.AddFathom().UseSqlServer().AddCodeListLookup(new CodeListLookupOptions { Name = "species", CodeType = "Species" });

        using var provider = services.BuildServiceProvider();
        Assert.Multiple(() =>
        {
            Assert.That(provider.GetServices<IExportLookupProvider>().Select(p => p.Name), Does.Contain("species"));
            Assert.That(provider.GetServices<IRequestLookupProvider>().Select(p => p.Name), Does.Contain("species"));
        });
    }

    [Test]
    public void Multiple_code_lists_register_independently()
    {
        var services = BaseServices();
        var fathom = services.AddFathom().UseSqlServer();
        fathom.AddCodeListLookup("countries", "Country");
        fathom.AddCodeListLookup("species", "Species");

        using var provider = services.BuildServiceProvider();
        var names = provider.GetServices<IExportLookupProvider>().Select(p => p.Name).ToArray();
        Assert.That(names, Is.EquivalentTo(new[] { "countries", "species" }));
    }

    [Test]
    public void AddFathom_registers_the_export_definition_registry()
    {
        var services = BaseServices();
        services.AddFathom();

        using var provider = services.BuildServiceProvider();
        Assert.That(provider.GetService<IExportDefinitionRegistry>(), Is.Not.Null);
    }
}
