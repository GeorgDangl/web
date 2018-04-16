// Copyright Matthias Koch 2018.
// Distributed under the MIT License.
// https://github.com/nuke-build/web/blob/master/LICENSE

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Nuke.Common.Tools.MSBuild;
using Nuke.Core;
using Nuke.Core.BuildServers;
using Nuke.Core.Execution;
using Nuke.Core.IO;
using Nuke.Core.Tooling;
using Nuke.Core.Utilities.Collections;
using static Nuke.Core.ControlFlow;
using static Nuke.Core.Logger;

static class CustomToc
{
    public static void WriteCustomToc(string tocFile, IEnumerable<string> solutionFiles)
    {
        var msBuildWorkspace = MSBuildWorkspace.Create(
            new Dictionary<string, string>
            {
                { "Configuration", "Release" },
                { "TargetFramework", "net461" }
            });
        msBuildWorkspace.WorkspaceFailed += (s, e) =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                Fail(e.Diagnostic.Message);
            else
                Warn(e.Diagnostic.Message);
        };

        var solutions = solutionFiles.Select(x => LoadSolution(msBuildWorkspace, x)).ToList();

        var iconClasses = (
                from solution in solutions
                from project in solution.Projects
                let compilation = project.GetCompilationAsync().Result
                from document in project.Documents
                let syntaxTree = document.GetSyntaxTreeAsync().Result
                let semanticModel = compilation.GetSemanticModel(syntaxTree)
                from attributeSyntax in syntaxTree.GetCompilationUnitRoot().DescendantNodes().OfType<AttributeSyntax>()
                let attributeSymbol = semanticModel.GetSymbolInfo(attributeSyntax.Name).Symbol
                where typeof(IconClassAttribute).Name.Equals(attributeSymbol?.ContainingType.Name)
                let arguments = attributeSyntax.ArgumentList.Arguments
                let typeOfExpression = (TypeOfExpressionSyntax) arguments.First().Expression
                select new
                       {
                           ClassFullName = semanticModel.GetSymbolInfo(typeOfExpression.Type).Symbol.ToDisplayString(),
                           IconClass = (string) semanticModel.GetConstantValue(arguments.Last().Expression).Value
                       })
            .ToDictionary(x => x.ClassFullName, x => x.IconClass);

        var relevantTypeSymbols = (
                from solution in solutions
                from project in solution.Projects
                let compilation = project.GetCompilationAsync().Result
                from document in project.Documents
                let syntaxTree = document.GetSyntaxTreeAsync().Result
                let semanticModel = compilation.GetSemanticModel(syntaxTree)
                from classDeclarationSyntax in syntaxTree.GetCompilationUnitRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                let typeSymbol = semanticModel.GetDeclaredSymbol(classDeclarationSyntax)
                let kind = GetKind(typeSymbol, iconClasses)
                where typeSymbol.ContainingAssembly.Name != ".build" && kind != Kind.None
                select new { TypeSymbol = typeSymbol, Kind = kind })
            .Distinct(x => x.TypeSymbol.ToDisplayString())
            .ForEachLazy(x => Info($"Found '{x.TypeSymbol.ToDisplayString()}' ({x.Kind})."))
            .ToLookup(x => x.Kind, x => x.TypeSymbol);

        TextTasks.WriteAllText(tocFile,
            new StringBuilder()
                .WriteBlock(Kind.Entry, relevantTypeSymbols, iconClasses)
                .WriteBlock(Kind.Servers, relevantTypeSymbols, iconClasses)
                .WriteBlock(Kind.Injection, relevantTypeSymbols, iconClasses)
                .WriteBlock(Kind.Common, relevantTypeSymbols, iconClasses)
                .WriteBlock(Kind.Addons, relevantTypeSymbols, iconClasses)
                .ToString());
    }

    static Solution LoadSolution(MSBuildWorkspace msBuildWorkspace, string solutionFile)
    {
        var solution = msBuildWorkspace.OpenSolutionAsync(solutionFile).Result;
        return solution;
    }

    enum Kind
    {
        None,
        Entry,
        Servers,
        Injection,
        Common,
        Addons
    }

    static Kind GetKind(ITypeSymbol typeSymbol, Dictionary<string, string> iconClasses)
    {
        if (IsEntryType(typeSymbol, iconClasses))
            return Kind.Entry;
        if (IsServerType(typeSymbol))
            return Kind.Servers;
        if (IsInjectionAttribute(typeSymbol, iconClasses))
            return Kind.Injection;
        if (typeSymbol.Name.EndsWith("Tasks"))
            return IsCommonType(typeSymbol)
                ? Kind.Common
                : Kind.Addons;

        return Kind.None;
    }


    static StringBuilder WriteBlock(
        this StringBuilder builder,
        Kind kind,
        ILookup<Kind, INamedTypeSymbol> typeSymbols,
        IDictionary<string, string> iconClasses)
        => builder
            .AppendLine($"- separator: {kind}")
            .ForEach(typeSymbols[kind].OrderBy(x => x.Name), x => builder.WriteType(x, iconClasses));

    static StringBuilder ForEach<T>(
        this StringBuilder builder,
        IEnumerable<T> enumerable,
        Action<T> builderAction)
    {
        foreach (var item in enumerable)
            builderAction(item);
        return builder;
    }

    static StringBuilder WriteType(this StringBuilder builder, ITypeSymbol typeSymbol, IDictionary<string, string> iconClasses)
        => builder
            .AppendLine($"- uid: {typeSymbol.ToDisplayString()}")
            .AppendLine($"  name: {typeSymbol.GetName()}")
            .AppendLine($"  icon: {typeSymbol.GetIconClassText(iconClasses)}");


    static bool IsEntryType(ITypeSymbol typeSymbol, Dictionary<string, string> iconClasses)
    {
        if (!iconClasses.ContainsKey(typeSymbol.ToDisplayString()))
            return false;

        //if (typeSymbol.ContainingAssembly.Name == typeof(NukeBuild).Assembly.GetName().Name)
        //    return true;

        return new[]
               {
                   typeof(ControlFlow),
                   typeof(EnvironmentInfo),
                   typeof(Logger),
                   typeof(NukeBuild),
                   typeof(PathConstruction),
                   typeof(ProcessTasks),
                   typeof(ToolPathResolver)
               }.Any(x => typeSymbol.ToDisplayString().Equals(x.FullName));
    }

    static bool IsServerType(this ITypeSymbol typeSymbol)
        => typeSymbol.GetAttributes().Any(x => x.AttributeClass.ToDisplayString().Equals(typeof(BuildServerAttribute).FullName));

    static bool IsInjectionAttribute(ITypeSymbol typeSymbol, Dictionary<string, string> iconClasses)
    {
        if (!iconClasses.ContainsKey(typeSymbol.ToDisplayString()))
            return false;

        return typeSymbol.Name.EndsWith("Attribute");
    }

    static bool IsCommonType(ITypeSymbol typeSymbol)
        => typeSymbol.ContainingAssembly.Name == typeof(MSBuildTasks).Assembly.GetName().Name
           || typeSymbol.ContainingAssembly.Name == typeof(MSBuildTasks).Assembly.GetName().Name;

    static string GetName(this ITypeSymbol typeSymbol)
    {
        var typeSymbolName = typeSymbol.Name;

        if (typeSymbolName.EndsWith("Tasks"))
            return typeSymbolName.Substring(startIndex: 0, length: typeSymbolName.Length - "Tasks".Length);

        if (typeSymbolName.EndsWith("Attribute"))
            return typeSymbolName.Substring(startIndex: 0, length: typeSymbolName.Length - "Attribute".Length);
        
        return typeSymbolName;
    }

    static string GetIconClassText(this ITypeSymbol typeSymbol, IDictionary<string, string> iconClasses)
    {
        if (iconClasses.TryGetValue(typeSymbol.ToDisplayString(), out var iconClass))
            return iconClass;
        if (IsServerType(typeSymbol))
            return "server";

        return "power-cord2";
    }
}
