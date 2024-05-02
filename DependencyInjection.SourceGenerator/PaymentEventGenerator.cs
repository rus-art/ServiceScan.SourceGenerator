﻿using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DependencyInjection.SourceGenerator;

[Generator]
public class DependencyInjectionGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor NotPartialDefinition = new("DI001", "Error shouldn't happen", "Test", "DI", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor WrongReturnType = new("DI002", "Error shouldn't happen", "Test", "DI", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor WrongMethodParameters = new("DI003", "Error shouldn't happen", "Test", "DI", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor NoMatchingTypesFound = new("DI004", "Error shouldn't happen", "Test", "DI", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(context => context.AddSource("GenerateAttribute.Generated.cs", SourceText.From(GenerateAttributeSource.Source, Encoding.UTF8)));

        var syntaxProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (syntaxNode, ct) => syntaxNode is MethodDeclarationSyntax methodSyntax && methodSyntax.AttributeLists.Count > 0,
                transform: static (node, ct) => node.SemanticModel.GetDeclaredSymbol(node.Node) as IMethodSymbol)
            .Where(method => method != null && method.IsPartialDefinition && method.GetAttributes()
                .Any(a => a.AttributeClass.ToDisplayString() == "DependencyInjection.SourceGenerator.GenerateAttribute"));

        // We require all matching type symbols, and create the generated files.
        context.RegisterImplementationSourceOutput(syntaxProvider,
            static (context, method) =>
            {
                if (!method.IsPartialDefinition)
                {
                    context.ReportDiagnostic(Diagnostic.Create(NotPartialDefinition, method.Locations[0]));
                    return;
                }

                if (!method.ReturnsVoid && method.ReturnType.ToDisplayString() != "Microsoft.Extensions.DependencyInjection.IServiceCollection")
                {
                    context.ReportDiagnostic(Diagnostic.Create(WrongReturnType, method.Locations[0]));
                    return;
                }

                if (method.Parameters.Length != 1 || method.Parameters[0].Type.ToDisplayString() != "Microsoft.Extensions.DependencyInjection.IServiceCollection")
                {
                    context.ReportDiagnostic(Diagnostic.Create(WrongMethodParameters, method.Locations[0]));
                    return;
                }

                //Debugger.Launch();

                var sb = new StringBuilder();

                foreach (var attribute in method.GetAttributes().Where(a => a.AttributeClass.ToDisplayString() == "DependencyInjection.SourceGenerator.GenerateAttribute"))
                {
                    var assemblyType = attribute.NamedArguments.FirstOrDefault(a => a.Key == "FromAssemblyOf").Value.Value as INamedTypeSymbol;
                    var assembly = assemblyType?.ContainingAssembly ?? method.ContainingAssembly;
                    var assignableTo = attribute.NamedArguments.FirstOrDefault(a => a.Key == "AssignableTo").Value.Value as INamedTypeSymbol;
                    var lifetime = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Lifetime").Value.Value as int? switch
                    {
                        0 => "Singleton",
                        1 => "Scoped",
                        2 => "Transient",
                        _ => "Singleton"
                    };

                    var types = GetTypesFromAssembly(assembly)
                        .Where(t => !t.IsAbstract && !t.IsStatic && t.TypeKind == TypeKind.Class);

                    bool anyFound = false;

                    foreach (var t in types)
                    {
                        var implementationType = t;

                        INamedTypeSymbol matchedType = null;
                        if (assignableTo != null && !IsAssignableTo(implementationType, assignableTo, out matchedType))
                            continue;

                        anyFound = true;

                        var serviceType = matchedType ?? assignableTo ?? implementationType;

                        if (implementationType.IsGenericType)
                        {
                            implementationType = implementationType.ConstructUnboundGenericType();

                            sb.AppendLine();
                            sb.Append($"            .Add{lifetime}(typeof({serviceType.ToDisplayString()}), typeof({implementationType.ToDisplayString()}))");
                        }
                        else
                        {
                            sb.AppendLine();
                            sb.Append($"            .Add{lifetime}<{serviceType.ToDisplayString()}, {implementationType.ToDisplayString()}>()");
                        }
                    }

                    if (!anyFound)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(NoMatchingTypesFound, method.Locations[0]));
                        return;
                    }
                }

                var @namespace = method.ContainingNamespace.ToDisplayString();
                var type = method.ContainingType;
                var returnType = method.ReturnsVoid ? "void" : "IServiceCollection";


                var source = $$"""
                using Microsoft.Extensions.DependencyInjection;

                namespace {{@namespace}};

                {{GetAccessModifier(type)}} {{IsStatic(type)}} partial class {{type.Name}}
                {
                    {{GetAccessModifier(method)}} {{IsStatic(method)}} partial {{returnType}} {{method.Name}}({{(method.IsExtensionMethod ? "this" : "")}} IServiceCollection services)
                    {
                        {{(method.ReturnsVoid ? "" : "return ")}}services{{sb}};
                    }
                }
                """;

                context.AddSource($"{type.Name}_{method.Name}.Generated.cs", SourceText.From(source, Encoding.UTF8));
            });
    }

    private static bool IsAssignableTo(INamedTypeSymbol type, INamedTypeSymbol assignableTo, out INamedTypeSymbol matchedType)
    {
        if (SymbolEqualityComparer.Default.Equals(type, assignableTo))
        {
            matchedType = type;
            return true;
        }

        if (assignableTo.IsUnboundGenericType)
        {
            if (assignableTo.TypeKind == TypeKind.Interface)
            {
                var matchingInterface = type.Interfaces.FirstOrDefault(i => i.IsGenericType && SymbolEqualityComparer.Default.Equals(i.ConstructUnboundGenericType(), assignableTo));
                matchedType = matchingInterface;
                return matchingInterface != null;
            }

            var baseType = type.BaseType;
            while (baseType != null)
            {
                if (baseType.IsGenericType && SymbolEqualityComparer.Default.Equals(baseType.ConstructUnboundGenericType(), assignableTo))
                {
                    matchedType = baseType;
                    return true;
                }

                baseType = baseType.BaseType;
            }
        }
        else
        {
            if (assignableTo.TypeKind == TypeKind.Interface)
            {
                matchedType = assignableTo;
                return type.Interfaces.Contains(assignableTo, SymbolEqualityComparer.Default);
            }

            var baseType = type.BaseType;
            while (baseType != null)
            {
                if (SymbolEqualityComparer.Default.Equals(baseType, assignableTo))
                {
                    matchedType = baseType;
                    return true;
                }

                baseType = baseType.BaseType;
            }
        }

        matchedType = null;
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> GetTypesFromAssembly(IAssemblySymbol assemblySymbol)
    {
        var @namespace = assemblySymbol.GlobalNamespace;
        return GetTypesFromNamespace(@namespace);

        static IEnumerable<INamedTypeSymbol> GetTypesFromNamespace(INamespaceSymbol namespaceSymbol)
        {
            foreach (var member in namespaceSymbol.GetMembers())
            {
                if (member is INamedTypeSymbol namedType)
                {
                    yield return namedType;
                }
                else if (member is INamespaceSymbol nestedNamespace)
                {
                    foreach (var type in GetTypesFromNamespace(nestedNamespace))
                    {
                        yield return type;
                    }
                }
            }
        }
    }

    private static string IsStatic(ISymbol symbol)
    {
        return symbol.IsStatic ? "static" : "";
    }

    private static string GetAccessModifier(ISymbol symbol)
    {
        return symbol.DeclaredAccessibility.ToString().ToLowerInvariant();
    }
}
