﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DependencyInjection.SourceGenerator.Tests;

public class Tests
{
    private readonly DependencyInjectionGenerator _generator = new();

    [Theory]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    [InlineData(ServiceLifetime.Singleton)]
    public void AddServicesWithLifetime(ServiceLifetime lifetime)
    {
        var attribute = $"[Generate(AssignableTo = typeof(IService), Lifetime = ServiceLifetime.{lifetime})]";

        var compilation = CreateCompilation(
            Sources.MethodWithAttribute(attribute),
            """
            namespace GeneratorTests;

            public interface IService { }
            public class MyService1 : IService { }
            public class MyService2 : IService { }
            """);

        var results = CSharpGeneratorDriver
            .Create(_generator)
            .RunGenerators(compilation)
            .GetRunResult();

        var registrations = $"""
            return services
                .Add{lifetime}<GeneratorTests.IService, GeneratorTests.MyService1>()
                .Add{lifetime}<GeneratorTests.IService, GeneratorTests.MyService2>();
            """;
        Assert.Equal(Sources.GetMethodImplementation(registrations), results.GeneratedTrees[1].ToString());
    }

    [Fact]
    public void AddServicesFromAnotherAssembly()
    {
        var attribute = "[Generate(FromAssemblyOf = typeof(External.IExternalService), AssignableTo = typeof(External.IExternalService))]";
        var compilation = CreateCompilation(Sources.MethodWithAttribute(attribute));

        var results = CSharpGeneratorDriver
            .Create(_generator)
            .RunGenerators(compilation)
            .GetRunResult();

        var registrations = $"""
            return services
                .AddTransient<External.IExternalService, External.ExternalService1>()
                .AddTransient<External.IExternalService, External.ExternalService2>();
            """;
        Assert.Equal(Sources.GetMethodImplementation(registrations), results.GeneratedTrees[1].ToString());
    }

    [Fact]
    public void AddServicesAssignableToOpenGenericInterface()
    {
        var attribute = $"[Generate(AssignableTo = typeof(IService<>))]";

        var compilation = CreateCompilation(
            Sources.MethodWithAttribute(attribute),
            """
            namespace GeneratorTests;

            public interface IService<T> { }
            public class MyIntService : IService<int> { }
            public class MyStringService : IService<string> { }
            """);

        var results = CSharpGeneratorDriver
            .Create(_generator)
            .RunGenerators(compilation)
            .GetRunResult();

        var registrations = $"""
            return services
                .AddTransient<GeneratorTests.IService<int>, GeneratorTests.MyIntService>()
                .AddTransient<GeneratorTests.IService<string>, GeneratorTests.MyStringService>();
            """;
        Assert.Equal(Sources.GetMethodImplementation(registrations), results.GeneratedTrees[1].ToString());
    }

    private static Compilation CreateCompilation(params string[] source)
    {
        var path = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var runtimeAssemblyPath = Path.Combine(path, "System.Runtime.dll");

        var runtimeReference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

        return CSharpCompilation.Create("compilation",
                source.Select(s => CSharpSyntaxTree.ParseText(s)),
                [
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(runtimeAssemblyPath),
                    MetadataReference.CreateFromFile(typeof(IServiceCollection).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(External.IExternalService).Assembly.Location),
                ],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
