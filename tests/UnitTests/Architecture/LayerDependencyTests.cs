using System.Reflection;
using AISupportOps.Domain.Common;
using AISupportOps.Infrastructure;

namespace AISupportOps.UnitTests.Architecture;

/// <summary>
/// Guards the Clean Architecture dependency rule: dependencies point inward only.
/// </summary>
public class LayerDependencyTests
{
    private static readonly Assembly Domain = typeof(Entity).Assembly;
    private static readonly Assembly Application = Assembly.Load("AISupportOps.Application");
    private static readonly Assembly Infrastructure = typeof(DependencyInjection).Assembly;

    [Fact]
    public void Domain_has_no_project_or_framework_dependencies()
    {
        var references = ReferencedNames(Domain);

        Assert.DoesNotContain(references, r => r.StartsWith("AISupportOps.", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_does_not_depend_on_Infrastructure_or_Api()
    {
        var references = ReferencedNames(Application);

        Assert.DoesNotContain("AISupportOps.Infrastructure", references);
        Assert.DoesNotContain("AISupportOps.Api", references);
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_Api()
    {
        Assert.DoesNotContain("AISupportOps.Api", ReferencedNames(Infrastructure));
    }

    private static List<string> ReferencedNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
}
